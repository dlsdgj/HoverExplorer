using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HoverExplorer
{
    internal static class ExplorerSupport
    {
        private static IntPtr _cachedHwnd;
        private static System.Collections.Generic.List<string> _cachedTabs;
        private static DateTime _cacheTime;

        // 当前活动标签页目录（短 TTL）。多标签页共用 HWND，缓存键必须带上它，
        // 否则切标签后 250ms 内仍复用上一个标签的悬停结果。
        private static IntPtr _activeHwnd;
        private static string _activeDir;
        private static DateTime _activeAt = DateTime.MinValue;

        internal static string GetActiveExplorerDir(IntPtr hwnd)
        {
            if (_activeHwnd == hwnd && _activeDir != null && (DateTime.UtcNow - _activeAt).TotalMilliseconds < 300)
                return _activeDir;
            // 标题+Shell 项匹配优先：Win11 地址栏 UIA 在多标签下可能只给出盘符（D:\），不可靠。
            string dir = GetActiveExplorerDirFromShell(hwnd);
            if (string.IsNullOrEmpty(dir)) dir = ExplorerUia.GetActiveFolderPath(hwnd);
            _activeHwnd = hwnd;
            _activeDir = string.IsNullOrEmpty(dir) || !Directory.Exists(dir) ? null : dir;
            _activeAt = DateTime.UtcNow;
            return _activeDir;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        // 窗口标题形如「临时同步 和 1 个其他选项卡 - 文件资源管理器」，
        // 前缀就是当前活动标签名——多标签页下这是唯一稳定的活动标签标识。
        private static string ActiveTabNameFromTitle(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(512);
                GetWindowText(hwnd, sb, sb.Capacity);
                string t = sb.ToString();
                if (string.IsNullOrEmpty(t)) return null;
                int dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0) t = t.Substring(0, dash);
                t = Regex.Replace(t, @"\s*(和|and)\s*\d+\s*(个其他选项卡|other tabs?)\s*$", "", RegexOptions.IgnoreCase).Trim();
                return t.Length == 0 ? null : t;
            }
            catch { return null; }
        }

        // ShellWindows 里同一 HWND 会出现多个标签项，用标题里的活动标签名挑出当前目录。
        private static string GetActiveExplorerDirFromShell(IntPtr hwnd)
        {
            string activeName = ActiveTabNameFromTitle(hwnd);
            if (string.IsNullOrEmpty(activeName)) return null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                object shell = Activator.CreateInstance(shellType);
                object windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
                if (windows == null) return null;
                int count = (int)windows.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, windows, null);
                long target = hwnd.ToInt64();
                for (int i = 0; i < count; i++)
                {
                    object w = windows.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, windows, new object[] { i });
                    if (w == null) continue;
                    try
                    {
                        if ((long)w.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, w, null) != target) continue;
                        object doc = w.GetType().InvokeMember("Document", BindingFlags.GetProperty, null, w, null);
                        if (doc == null) continue;
                        object folder = doc.GetType().InvokeMember("Folder", BindingFlags.GetProperty, null, doc, null);
                        if (folder == null) continue;
                        object self = folder.GetType().InvokeMember("Self", BindingFlags.GetProperty, null, folder, null);
                        if (self == null) continue;
                        string path = self.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, self, null) as string;
                        string name = self.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, self, null) as string;
                        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                        if (string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), activeName, StringComparison.OrdinalIgnoreCase))
                            return path;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // 当前打开的资源管理器窗口/标签的文件夹路径集合（历史 MRU 用）。
        // 走 ShellWindows COM 枚举 LocationURL（file:/// 转本地路径），实测含多标签；
        // 不依赖 UIA 面包屑类名（FileExplorerExtensions.BreadcrumbBarItemControl
        // 在部分 Win11 版本/视图下 FindAll 落空），HWND 属性亦不可靠（无窗口项抛错）→ 全量记录
        public static System.Collections.Generic.List<string> GetOpenExplorerPaths()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                // CLSID_ShellWindows 后期绑定，免 COM 引用
                var t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
                var obj = Activator.CreateInstance(t);
                foreach (var item in (System.Collections.IEnumerable)obj)
                {
                    string loc = null;
                    try { loc = (string)item.GetType().InvokeMember("LocationURL", System.Reflection.BindingFlags.GetProperty, null, item, null); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(loc) || !loc.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var p = new Uri(loc).LocalPath;
                        if (Directory.Exists(p)) list.Add(p);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        // UIA 命中缓存：全量检测 ~100ms，0.1s 级延时窗口内每 tick 重复检测会因
        // "检测期间指针微移" 反复作废（时灵时不灵的根因）。600ms 内指针仍在命中区
        // （放宽 8x4px）→ 直接复用上次结果，微秒级；离开即重检
        private static IntPtr _hitHwnd;
        private static string _hitPath;
        private static string _hitDir;
        private static System.Drawing.Rectangle _hitRect, _outRect;
        private static bool _hitIsDir;
        private static DateTime _hitAt = DateTime.MinValue;

        // 静止指针缓存：指针不动时坐标下的结果一般也不变。同一窗口同一像素点 250ms 内
        // 直接复用上次检测结果（含 null），静止 tick 不再全量跑 UIA（全量 ~100ms 级）。
        // TTL 到期重检一次，切标签/列表刷新最迟 250ms 后被感知；指针一动坐标就变，缓存自然失效
        private static IntPtr _stillHwnd;
        private static int _stillX = int.MinValue, _stillY;
        private static string _stillPath;
        private static string _stillDir;
        private static System.Drawing.Rectangle _stillRect;
        private static bool _stillIsDir;
        private static DateTime _stillAt = DateTime.MinValue;

        // miss 节流状态：上次全量检测未命中的窗口与时刻
        private static IntPtr _missHwnd;
        private static DateTime _missAt = DateTime.MinValue;

        public static string DetectHoverItem(int mx, int my, out System.Drawing.Rectangle rect, out bool isDir, bool force = false)
        {
            rect = System.Drawing.Rectangle.Empty;
            isDir = false;
            var pt = new NativeMethods.POINT { X = mx, Y = my };
            var hwnd = NativeMethods.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return null;

            IntPtr explorerHwnd = IntPtr.Zero;
            var cur = hwnd;
            while (cur != IntPtr.Zero)
            {
                var cls = NativeMethods.GetClassOf(cur);
                if (cls == "CabinetWClass") { explorerHwnd = cur; break; }
                cur = NativeMethods.GetParent(cur);
            }
            if (explorerHwnd == IntPtr.Zero) return null;
            string activeDir = GetActiveExplorerDir(explorerHwnd);

            // miss 节流：Win11 新版 Explorer 的 UIA 全量检测一次 130-500ms（44 条目的
            // FindAll+逐条矩形），鼠标移动时每 tick 都 miss 都全量跑 → UI 线程饱和
            // （实测 tick slow 527ms，关闭逻辑被饿死、置顶窗僵死）。上次未命中且还在
            // 节流窗口内 → 直接判空；悬停延时本来 ≥150ms，感知无损。force（空格预览）绕过
            if (!force && _missHwnd == explorerHwnd && (DateTime.UtcNow - _missAt).TotalMilliseconds < 250)
                return null;

            if (!force && _stillHwnd == explorerHwnd && mx == _stillX && my == _stillY
                && string.Equals(_stillDir, activeDir, StringComparison.OrdinalIgnoreCase)
                && (DateTime.UtcNow - _stillAt).TotalMilliseconds < 250)
            {
                rect = _stillRect; isDir = _stillIsDir;
                return _stillPath;
            }

            if (_hitHwnd == explorerHwnd && string.Equals(_hitDir, activeDir, StringComparison.OrdinalIgnoreCase)
                && (DateTime.UtcNow - _hitAt).TotalMilliseconds < 600)
            {
                var hitZone = System.Drawing.Rectangle.Inflate(_hitRect, 8, 4);
                if (hitZone.Contains(mx, my))
                {
                    _hitAt = DateTime.UtcNow;   // 仍在命中区：续期，0.1s 延时窗口内不再走 UIA
                    rect = _outRect; isDir = _hitIsDir;
                    return _hitPath;
                }
            }

            string result = null;
            rect = System.Drawing.Rectangle.Empty;
            isDir = false;
            string name = ExplorerUia.DetectHoverItemCore(mx, my, explorerHwnd, out rect);
            if (name == null)
            {
                string navPath = DetectNavPaneFolder(explorerHwnd, mx, my);
                if (navPath != null) { result = navPath; isDir = true; }
            }
            else
            {
                string dirPath = GetExplorerPath(explorerHwnd, name);
                if (dirPath != null)
                {
                    string fullPath = Path.Combine(dirPath, name);
                    if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                    {
                        // UIA 返回的是 shell 显示名；desktop.ini 本地化过的条目（如"保存的图片"实际是
                        // Saved Pictures、"屏幕截图"是 Screenshots）拼出的路径不存在 → 还原真实名，
                        // 否则文件夹被误判成文件、路径无效，长按/悬停全部哑火
                        string real = ResolveLocalizedName(dirPath, name);
                        if (real != null) fullPath = real;
                    }
                    isDir = Directory.Exists(fullPath);
                    _hitHwnd = explorerHwnd; _hitPath = fullPath; _hitIsDir = isDir; _hitDir = activeDir;
                    _hitRect = rect; _outRect = rect; _hitAt = DateTime.UtcNow;
                    result = fullPath;
                }
                else { rect = System.Drawing.Rectangle.Empty; isDir = false; }
            }
            _stillHwnd = explorerHwnd; _stillX = mx; _stillY = my; _stillDir = activeDir;
            _stillPath = result; _stillRect = result != null ? rect : System.Drawing.Rectangle.Empty;
            _stillIsDir = result != null && isDir;
            _stillAt = DateTime.UtcNow;
            // 记录 miss（供节流）；命中则清掉，别让成功后的偶发 miss 被误节流
            if (result == null) { _missHwnd = explorerHwnd; _missAt = DateTime.UtcNow; }
            else _missHwnd = IntPtr.Zero;
            return result;
        }

        // desktop.ini 显示名 → 真实文件系统路径 的还原缓存（失败的显示名也缓存，免得每次 tick 重扫）
        private static readonly System.Collections.Generic.Dictionary<string, string> _locNameCache =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOut, int cch, IntPtr ppvReserved);

        // 还原 dirPath 下以 shell 显示名呈现的条目。
        // ponytail: 只覆盖 desktop.ini 本地化（已知文件夹的现实机制）；极少数走其他来源的
        // 显示名（如云占位符）不覆盖，需换 IShellFolder 枚举才能兜住
        internal static string ResolveLocalizedName(string dirPath, string displayName)
        {
            if (string.IsNullOrEmpty(dirPath) || string.IsNullOrEmpty(displayName)) return null;
            string key = dirPath + "|" + displayName;
            string hit;
            if (_locNameCache.TryGetValue(key, out hit)) return hit == "" ? null : hit;
            string resolved = null;
            try
            {
                foreach (string d in Directory.GetDirectories(dirPath))
                {
                    string fsName = Path.GetFileName(d);
                    if (fsName.Equals(displayName, StringComparison.OrdinalIgnoreCase)) break;
                    string loc = ReadLocalizedIni(d);
                    if (loc != null && loc.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = d;
                        break;
                    }
                }
            }
            catch { }
            if (_locNameCache.Count > 256) _locNameCache.Clear();
            _locNameCache[key] = resolved ?? "";
            return resolved;
        }

        // 读目录 desktop.ini 的 LocalizedName/LocalizedResourceName，经 SHLoadIndirectString
        // 解析 "@%SystemRoot%\system32\windows.storage.dll,-21823" 这类间接串为当前语言显示名
        private static string ReadLocalizedIni(string dir)
        {
            try
            {
                string ini = Path.Combine(dir, "desktop.ini");
                if (!File.Exists(ini)) return null;
                foreach (string line in File.ReadAllLines(ini))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    if (!key.Equals("LocalizedResourceName", StringComparison.OrdinalIgnoreCase)
                        && !key.Equals("LocalizedName", StringComparison.OrdinalIgnoreCase)) continue;
                    string val = line.Substring(eq + 1).Trim().Trim('"');
                    if (val.Length == 0) continue;
                    var sb = new StringBuilder(520);
                    if (SHLoadIndirectString(Environment.ExpandEnvironmentVariables(val), sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0)
                        return sb.ToString();
                }
            }
            catch { }
            return null;
        }

// QuickLook 语义的后半段：取 Explorer 前台窗口当前选中的单个条目（光标不在条目上时用）。
        // 与 ResolveNamespaceItem 同款后期绑定 ShellWindows：按 HWND 匹配 → Document.SelectedItems → Path
        // QuickLook 语义：取资源管理器当前键盘高亮项（与鼠标位置无关）。
        // 返回磁盘真实路径；rect 为条目屏幕矩形，isDir 由 Directory.Exists 重算。
        public static string GetExplorerFocusedItem(IntPtr explorerHwnd, out System.Drawing.Rectangle rect, out bool isDir)
        {
            rect = System.Drawing.Rectangle.Empty;
            isDir = false;
            try
            {
                string name = ExplorerUia.GetFocusedItem(explorerHwnd, out rect);
                if (string.IsNullOrEmpty(name)) return null;

                string path = null;
                // 优先 Shell 选中项路径（本地化名下也能给真实路径，且是多视图兜底）
                string viaSel = GetExplorerSelectedPath(explorerHwnd);
                if (!string.IsNullOrEmpty(viaSel)) path = viaSel;

                // 选中项路径拿不到（或搜索视图）时用 UIA 显示名 + 当前目录拼，失败再还原本地化名
                if (string.IsNullOrEmpty(path))
                {
                    string dirPath = GetExplorerPath(explorerHwnd, name);
                    if (dirPath == null) return null;
                    string full = Path.Combine(dirPath, name);
                    if (!File.Exists(full) && !Directory.Exists(full))
                    {
                        string real = ResolveLocalizedName(dirPath, name);
                        if (real != null) full = real;
                    }
                    if (!File.Exists(full) && !Directory.Exists(full)) return null;
                    path = full;
                }
                isDir = Directory.Exists(path);
                return path;
            }
            catch { return null; }
        }

        internal static string GetExplorerSelectedPath(IntPtr explorerHwnd)
        {
            try
            {
                string activeDir = GetActiveExplorerDir(explorerHwnd);
                var t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
                var obj = Activator.CreateInstance(t);
                foreach (var w in (System.Collections.IEnumerable)obj)
                {
                    try
                    {
                        IntPtr hwnd = (IntPtr)(long)w.GetType().InvokeMember("HWND",
                            System.Reflection.BindingFlags.GetProperty, null, w, null);
                        if (hwnd != explorerHwnd) continue;
                        object doc = w.GetType().InvokeMember("Document",
                            System.Reflection.BindingFlags.GetProperty, null, w, null);
                        if (doc == null) continue;
                        // SelectedItems 在 IShellFolderViewDual 上是方法（DISP_E_MEMBERNOTFOUND
                        // 用 GetProperty 会抛异常→选中回退永远为 null）。必须 InvokeMethod。
                        object sel = doc.GetType().InvokeMember("SelectedItems",
                            System.Reflection.BindingFlags.InvokeMethod, null, doc, null);
                        if (sel == null) continue;
                        int count = (int)sel.GetType().InvokeMember("Count",
                            System.Reflection.BindingFlags.GetProperty, null, sel, null);
                        if (count != 1) return null;   // 多选/无选中：没有单一预览对象
                        object item = sel.GetType().InvokeMember("Item",
                            System.Reflection.BindingFlags.InvokeMethod, null, sel, new object[] { 0 });
                        string path = item.GetType().InvokeMember("Path",
                            System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) continue;
                        // 同一 HWND 的后台标签也会报 SelectedItems：只收当前标签目录下的项
                        if (activeDir != null)
                        {
                            string parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));
                            if (!string.IsNullOrEmpty(parent) && !string.Equals(parent, activeDir, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                        return path;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static string DetectNavPaneFolder(IntPtr explorerHwnd, int mx, int my)
        {
            IntPtr treeHwnd = IntPtr.Zero;
            NativeMethods.EnumChildProc cb = (h, l) =>
            {
                if (NativeMethods.GetClassOf(h) == "SysTreeView32") { treeHwnd = h; return false; }
                return true;
            };
            NativeMethods.EnumChildWindows(explorerHwnd, cb, IntPtr.Zero);
            if (treeHwnd == IntPtr.Zero) return null;

            NativeMethods.RECT tr;
            if (!NativeMethods.GetWindowRect(treeHwnd, out tr)) return null;
            if (mx < tr.Left || mx > tr.Right || my < tr.Top || my > tr.Bottom) return null;

            uint pid;
            NativeMethods.GetWindowThreadProcessId(treeHwnd, out pid);
            IntPtr proc = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
                false, pid);
            if (proc == IntPtr.Zero) return null;

            try
            {
                int htSize = Marshal.SizeOf(typeof(NativeMethods.TVHITTESTINFO));
                IntPtr remoteHt = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (uint)htSize,
                    NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
                if (remoteHt == IntPtr.Zero) return null;

                NativeMethods.POINT clientPt = new NativeMethods.POINT { X = mx, Y = my };
                NativeMethods.ScreenToClient(treeHwnd, ref clientPt);

                NativeMethods.TVHITTESTINFO ht = new NativeMethods.TVHITTESTINFO();
                ht.pt = clientPt;
                IntPtr localHt = Marshal.AllocHGlobal(htSize);
                Marshal.StructureToPtr(ht, localHt, false);
                uint written;
                NativeMethods.WriteProcessMemory(proc, remoteHt, localHt, (uint)htSize, out written);

                IntPtr res;
                NativeMethods.SendMessageTimeoutW(treeHwnd, (uint)NativeMethods.TVM_HITTEST,
                    IntPtr.Zero, remoteHt, NativeMethods.SMTO_ABORTIFHUNG, 1000, out res);

                NativeMethods.TVHITTESTINFO outHt = new NativeMethods.TVHITTESTINFO();
                uint read;
                NativeMethods.ReadProcessMemory(proc, remoteHt, localHt, (uint)htSize, out read);
                outHt = (NativeMethods.TVHITTESTINFO)Marshal.PtrToStructure(localHt, typeof(NativeMethods.TVHITTESTINFO));
                Marshal.FreeHGlobal(localHt);
                NativeMethods.VirtualFreeEx(proc, remoteHt, 0, NativeMethods.MEM_RELEASE);

                if (outHt.hItem == IntPtr.Zero) return null;

                int itemSize = Marshal.SizeOf(typeof(NativeMethods.TVITEMW));
                IntPtr remoteItem = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (uint)itemSize,
                    NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
                int textSize = 520 * 2;
                IntPtr remoteText = NativeMethods.VirtualAllocEx(proc, IntPtr.Zero, (uint)textSize,
                    NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
                if (remoteItem == IntPtr.Zero || remoteText == IntPtr.Zero) return null;

                try
                {
                    IntPtr localItem = Marshal.AllocHGlobal(itemSize);
                    IntPtr localText = Marshal.AllocHGlobal(textSize);

                    string path = GetTreeItemText(treeHwnd, proc, remoteItem, remoteText,
                        localItem, localText, itemSize, textSize, outHt.hItem);
                    if (string.IsNullOrEmpty(path)) return null;

                    IntPtr cur = outHt.hItem;
                    for (int i = 0; i < 10; i++)
                    {
                        IntPtr parentRes;
                        NativeMethods.SendMessageTimeoutW(treeHwnd, (uint)NativeMethods.TVM_GETNEXTITEM,
                            (IntPtr)NativeMethods.TVGN_PARENT, cur, NativeMethods.SMTO_ABORTIFHUNG, 1000, out parentRes);
                        IntPtr parent = parentRes;
                        if (parent == IntPtr.Zero) break;
                        string parentText = GetTreeItemText(treeHwnd, proc, remoteItem, remoteText,
                            localItem, localText, itemSize, textSize, parent);
                        if (string.IsNullOrEmpty(parentText)) break;
                        if (parentText == "此电脑" || parentText == "快速访问" || parentText == "网络" ||
                            parentText == "This PC" || parentText == "Quick access" || parentText == "Network")
                            break;
                        path = parentText + "\\" + path;
                        cur = parent;
                    }

                    Marshal.FreeHGlobal(localItem);
                    Marshal.FreeHGlobal(localText);

                    if (Directory.Exists(path)) return path;
                    return null;
                }
                finally
                {
                    NativeMethods.VirtualFreeEx(proc, remoteItem, 0, NativeMethods.MEM_RELEASE);
                    NativeMethods.VirtualFreeEx(proc, remoteText, 0, NativeMethods.MEM_RELEASE);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(proc);
            }
        }

        private static string GetTreeItemText(IntPtr treeHwnd, IntPtr proc, IntPtr remoteItem, IntPtr remoteText,
            IntPtr localItem, IntPtr localText, int itemSize, int textSize, IntPtr hItem)
        {
            NativeMethods.TVITEMW item = new NativeMethods.TVITEMW();
            item.mask = NativeMethods.TVIF_HANDLE | NativeMethods.TVIF_TEXT;
            item.hItem = hItem;
            item.pszText = remoteText;
            item.cchTextMax = 520;
            Marshal.StructureToPtr(item, localItem, false);
            uint written;
            NativeMethods.WriteProcessMemory(proc, remoteItem, localItem, (uint)itemSize, out written);
            IntPtr res;
            NativeMethods.SendMessageTimeoutW(treeHwnd, (uint)NativeMethods.TVM_GETITEMW,
                IntPtr.Zero, remoteItem, NativeMethods.SMTO_ABORTIFHUNG, 1000, out res);
            uint read;
            NativeMethods.ReadProcessMemory(proc, remoteText, localText, (uint)textSize, out read);
            return Marshal.PtrToStringUni(localText);
        }

        private static string GetExplorerPath(IntPtr hwnd, string itemName)
        {
            // 多标签页只认当前可见标签的地址栏路径；不能遍历所有同 HWND 标签再挑
            // "第一个能拼出文件"的目录，那会命中后台标签的同名文件。
            string active = GetActiveExplorerDir(hwnd);
            if (active != null) return active;
            var tabs = GetExplorerTabPaths(hwnd);
            if (tabs == null || tabs.Count == 0) return null;
            if (!string.IsNullOrEmpty(itemName))
            {
                foreach (var p in tabs)
                {
                    try { if (Directory.Exists(Path.Combine(p, itemName)) || File.Exists(Path.Combine(p, itemName))) return p; }
                    catch { }
                }
            }
            return tabs[0];
        }

        private static System.Collections.Generic.List<string> GetExplorerTabPaths(IntPtr hwnd)
        {
            if (hwnd == _cachedHwnd && _cachedTabs != null && (DateTime.UtcNow - _cacheTime).TotalSeconds < 2)
                return _cachedTabs;
            _cachedHwnd = hwnd;
            _cachedTabs = new System.Collections.Generic.List<string>();
            _cacheTime = DateTime.UtcNow;

            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType != null)
                {
                    object shell = Activator.CreateInstance(shellType);
                    object windows = shellType.InvokeMember("Windows",
                        System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                    if (windows != null)
                    {
                        int count = (int)windows.GetType().InvokeMember("Count",
                            System.Reflection.BindingFlags.GetProperty, null, windows, null);
                        long targetHwnd = hwnd.ToInt64();
                        for (int i = 0; i < count; i++)
                        {
                            object win = windows.GetType().InvokeMember("Item",
                                System.Reflection.BindingFlags.InvokeMethod, null,
                                windows, new object[] { i });
                            if (win == null) continue;
                            try
                            {
                                long wh = (long)win.GetType().InvokeMember("HWND",
                                    System.Reflection.BindingFlags.GetProperty, null, win, null);
                                if (wh != targetHwnd) continue;
                                object doc = win.GetType().InvokeMember("Document",
                                    System.Reflection.BindingFlags.GetProperty, null, win, null);
                                if (doc == null) continue;
                                object folder = doc.GetType().InvokeMember("Folder",
                                    System.Reflection.BindingFlags.GetProperty, null, doc, null);
                                if (folder == null) continue;
                                object self = folder.GetType().InvokeMember("Self",
                                    System.Reflection.BindingFlags.GetProperty, null, folder, null);
                                if (self == null) continue;
                                string path = (string)self.GetType().InvokeMember("Path",
                                    System.Reflection.BindingFlags.GetProperty, null, self, null);
                                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)
                                    && !_cachedTabs.Contains(path))
                                    _cachedTabs.Add(path);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            if (_cachedTabs.Count == 0)
            {
                string bp = ExplorerUia.GetBreadcrumbPath(hwnd);
                if (bp != null) _cachedTabs.Add(bp);
            }

            return _cachedTabs;
        }
    }
}