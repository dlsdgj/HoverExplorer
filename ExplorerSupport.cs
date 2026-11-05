using System;
using System.IO;
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
        private static System.Drawing.Rectangle _hitRect, _outRect;
        private static bool _hitIsDir;
        private static DateTime _hitAt = DateTime.MinValue;

        // 静止指针缓存：指针不动时坐标下的结果一般也不变。同一窗口同一像素点 250ms 内
        // 直接复用上次检测结果（含 null），静止 tick 不再全量跑 UIA（全量 ~100ms 级）。
        // TTL 到期重检一次，切标签/列表刷新最迟 250ms 后被感知；指针一动坐标就变，缓存自然失效
        private static IntPtr _stillHwnd;
        private static int _stillX = int.MinValue, _stillY;
        private static string _stillPath;
        private static System.Drawing.Rectangle _stillRect;
        private static bool _stillIsDir;
        private static DateTime _stillAt = DateTime.MinValue;

        public static string DetectHoverItem(int mx, int my, out System.Drawing.Rectangle rect, out bool isDir)
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

            if (_stillHwnd == explorerHwnd && mx == _stillX && my == _stillY
                && (DateTime.UtcNow - _stillAt).TotalMilliseconds < 250)
            {
                rect = _stillRect; isDir = _stillIsDir;
                return _stillPath;
            }

            if (_hitHwnd == explorerHwnd && (DateTime.UtcNow - _hitAt).TotalMilliseconds < 600)
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
                    _hitHwnd = explorerHwnd; _hitPath = fullPath; _hitIsDir = isDir;
                    _hitRect = rect; _outRect = rect; _hitAt = DateTime.UtcNow;
                    result = fullPath;
                }
                else
                {
                    // "此电脑"等 shell 命名空间视图：位置不是文件系统目录，拼路径必失败。
                    // 通解走 shell COM（Folder.ParseName 按显示名解析，FolderItem.Path 直接给
                    // 真实路径：驱动器 → "C:\"、文档 → C:\Users\...\Documents）；COM 够不着时
                    // （如管理员权限的窗口）退驱动器盘符正则兜底
                    string nsPath = ResolveNamespaceItem(explorerHwnd, name);
                    if (nsPath == null) nsPath = ResolveDriveItem(name);
                    if (nsPath != null)
                    {
                        isDir = Directory.Exists(nsPath);
                        if (isDir || File.Exists(nsPath))
                        {
                            _hitHwnd = explorerHwnd; _hitPath = nsPath; _hitIsDir = isDir;
                            _hitRect = rect; _outRect = rect; _hitAt = DateTime.UtcNow;
                            result = nsPath;
                        }
                    }
                    if (result == null) { rect = System.Drawing.Rectangle.Empty; isDir = false; }
                }
            }
            _stillHwnd = explorerHwnd; _stillX = mx; _stillY = my;
            _stillPath = result; _stillRect = result != null ? rect : System.Drawing.Rectangle.Empty;
            _stillIsDir = result != null && isDir;
            _stillAt = DateTime.UtcNow;
            return result;
        }

        private static readonly Regex _driveNameRe = new Regex(@"([A-Za-z]):", RegexOptions.Compiled);

        // "此电脑"的驱动器条目显示名（"本地磁盘 (C:)"）抽盘符 → "C:\"；匹配不到返回 null
        internal static string ResolveDriveItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Match m = _driveNameRe.Match(name);
            if (!m.Success) return null;
            string drive = m.Groups[1].Value.ToUpperInvariant() + ":\\";
            return Directory.Exists(drive) ? drive : null;
        }

        // shell 命名空间视图（此电脑/回收站/控制面板...）按显示名解析真实路径。
        // 与 GetOpenExplorerPaths 同款后期绑定 ShellWindows：按 HWND 找窗口 → Document
        // 就是 shell32 的 Folder 对象 → ParseName(显示名) → FolderItem.Path 为真实路径
        // （设备类条目 Path 为空串/异常 → 返回 null）。explorerHwnd 不在 ShellWindows 里
        // （提权窗口）时返回 null，调用方走正则兜底
        internal static string ResolveNamespaceItem(IntPtr explorerHwnd, string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return null;
            try
            {
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
                        object item = doc.GetType().InvokeMember("ParseName",
                            System.Reflection.BindingFlags.InvokeMethod, null, doc,
                            new object[] { displayName });
                        if (item == null) continue;
                        string path = item.GetType().InvokeMember("Path",
                            System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                        if (!string.IsNullOrEmpty(path) && (Directory.Exists(path) || File.Exists(path)))
                            return path;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
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