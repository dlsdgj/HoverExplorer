using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace HoverExplorer
{
    // 标准文件对话框（打开/另存为）里的悬停识别。
    // 思路：
    //   1) 从指针下的窗口向上找父窗口，直到类名 "#32770"（Windows 通用对话框外壳）；
    //   2) 用 UIA 在对话框内找"值是一个已存在目录"的 Edit（地址栏），拿到当前目录；
    //      不能用 CDM_GETFOLDERPATH——WM_USER 消息不跨进程封送指针，发到别的进程只会写坏内存；
    //   3) UIA 取指针下的列表项（ListItem/DataItem）名字与矩形；
    //   4) 目录 + 名字拼出真实路径，交给现有预览链路。
    // 只在指针命中列表项时触发，文件名编辑框、按钮、地址栏都不会误触发。
    internal static class DialogSupport
    {
        private const int MaxAncestor = 8;

        private static IntPtr _missHwnd;
        private static DateTime _missAt = DateTime.MinValue;
        private static string _lastAddrRaw;   // 诊断：最近读到的地址栏/UIA 文字

        private static IntPtr _hitHwnd;
        private static string _hitPath;
        private static Rectangle _hitRect;
        private static bool _hitIsDir;
        private static DateTime _hitAt = DateTime.MinValue;

        private static IntPtr _stillHwnd;
        private static int _stillX, _stillY;
        private static string _stillPath;
        private static Rectangle _stillRect;
        private static bool _stillIsDir;
        private static DateTime _stillAt = DateTime.MinValue;

        // 目录缓存：对话框切目录后 500ms 内复用；CDM_GETFOLDERPATH 本身很便宜，缓存只为省消息
        private static IntPtr _dirHwnd;
        private static string _dirPath;
        private static DateTime _dirAt = DateTime.MinValue;

        // 诊断日志节流：同一 key 800ms 内只写一条，避免鼠标移动刷爆日志
        private static readonly Dictionary<string, DateTime> _logAt = new Dictionary<string, DateTime>();
        private static void Log(string key, string msg)
        {
            DateTime t;
            if (_logAt.TryGetValue(key, out t) && (DateTime.UtcNow - t).TotalMilliseconds < 800) return;
            if (_logAt.Count > 200) _logAt.Clear();
            _logAt[key] = DateTime.UtcNow;
            Program.DebugLog("[dialog] " + msg);
        }

        // 指针下的窗口若是对话框，返回窗口类链（供 Program 在"未命中"时打日志）
        internal static string DialogChainAt(int x, int y)
        {
            IntPtr hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
            if (hwnd == IntPtr.Zero) return null;
            string chain = ClassChainOf(hwnd, 8);
            return chain.IndexOf("#32770", StringComparison.OrdinalIgnoreCase) >= 0 ? chain : null;
        }

        // 最近一次命中的对话框句柄：HE 面板据此变成"对话框操作面板"
        internal static IntPtr ActiveDialog;

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr h);

        internal static bool IsDialogAlive(IntPtr dlg)
        {
            try { return dlg != IntPtr.Zero && IsWindow(dlg); } catch { return false; }
        }

        private static IntPtr _fnDlg;
        private static AutomationElement _fnEdit;

        // 对话框"文件名"输入框（标准控件 AutomationId=1148 的 ComboBox 内的 Edit）
        private static AutomationElement FindFileNameEdit(IntPtr dlg)
        {
            if (_fnDlg == dlg && (_fnEdit != null)) return _fnEdit;
            _fnDlg = dlg; _fnEdit = null;
            try
            {
                AutomationElement root = AutomationElement.FromHandle(dlg);
                if (root == null) return null;
                AutomationElement combo = null;
                try { combo = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "1148")); } catch { }
                if (combo == null)
                {
                    var combos = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
                    for (int i = 0; i < combos.Count && combo == null; i++)
                    {
                        string n = "";
                        try { n = combos[i].Current.Name ?? ""; } catch { }
                        if (n.IndexOf("文件名", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("File name", StringComparison.OrdinalIgnoreCase) >= 0) combo = combos[i];
                    }
                }
                AutomationElement edit = null;
                if (combo != null)
                {
                    try { edit = combo.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)); } catch { }
                    if (edit == null) edit = combo;
                }
                _fnEdit = edit;
                Log("fnedit:" + dlg, "filename edit=" + (edit == null ? "null" : ("ok aid=" + SafeVal(edit, false) + " name=" + SafeVal(edit, true))));
            }
            catch { }
            return _fnEdit;
        }

        private static string SafeVal(AutomationElement e, bool name)
        {
            try { return name ? (e.Current.Name ?? "") : (e.Current.AutomationId ?? ""); } catch { return "?"; }
        }

        private static bool SetEditValue(AutomationElement edit, string text)
        {
            try
            {
                object pat;
                if (edit != null && edit.TryGetCurrentPattern(ValuePattern.Pattern, out pat) && pat != null)
                {
                    ((ValuePattern)pat).SetValue(text);
                    return true;
                }
            }
            catch { }
            return false;
        }

        // 把文件名列表填进对话框（多选 → "a.txt" "b.txt"），不提交，仍由用户点"打开/保存"
        internal static bool FillFileNames(IntPtr dlg, IList<string> names)
        {
            if (!IsDialogAlive(dlg) || names == null || names.Count == 0) return false;
            var edit = FindFileNameEdit(dlg);
            if (edit == null) return false;
            var sb = new StringBuilder();
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('"').Append(n.Replace("\"", "")).Append('"');
            }
            if (sb.Length == 0) return false;
            bool ok = SetEditValue(edit, sb.ToString());
            Log("fill:" + dlg, "fill count=" + names.Count + " ok=" + ok + " text=" + sb);
            return ok;
        }

        // 让对话框进入指定文件夹：路径补结尾反斜杠后触发对话框默认按钮（AutomationId=1），
        // 标准对话框会把"目录\"当导航而不是文件名；按钮不可用时退回聚焦+回车
        internal static bool Navigate(IntPtr dlg, string dir)
        {
            if (!IsDialogAlive(dlg) || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var edit = FindFileNameEdit(dlg);
            if (edit == null) return false;
            string text = dir.EndsWith("\\") ? dir : dir + "\\";
            if (!SetEditValue(edit, text)) return false;
            bool ok = false;
            // 用 IDOK 按钮的 BM_CLICK 提交（跨进程安全）：文件名框里是"目录\"时对话框会进入该目录。
            // 比模拟回车可靠——回车依赖焦点真的落在文件名框（UIA SetFocus 可能失败，回车被列表吃掉）
            try
            {
                IntPtr okBtn = NativeMethods.GetDlgItem(dlg, 1);
                if (okBtn != IntPtr.Zero)
                {
                    NativeMethods.SendMessageW(okBtn, 0x00F5, IntPtr.Zero, IntPtr.Zero);   // BM_CLICK
                    ok = true;
                }
            }
            catch { }
            if (!ok)
            {
                try
                {
                    NativeMethods.SetForegroundWindow(dlg);
                    edit.SetFocus();
                    NativeMethods.keybd_event(0x0D, 0, 0, UIntPtr.Zero);
                    NativeMethods.keybd_event(0x0D, 0, 2, UIntPtr.Zero);
                    ok = true;
                }
                catch { }
            }
            Log("nav:" + dir, "navigate " + dir + " ok=" + ok);
            return ok;
        }

        // 首次打开的对话框常常要激活一次才把文件项暴露给 UIA（表现为悬停文件夹没反应，
        // 点一下里面任意文件后才正常）。悬停同一对话框超过 1.2s 仍找不到条目时主动激活它
        private static IntPtr _noItemDlg;
        private static DateTime _noItemAt = DateTime.MinValue;

        private static void TryActivateForUia(IntPtr dlg)
        {
            if (_noItemDlg != dlg) { _noItemDlg = dlg; _noItemAt = DateTime.UtcNow; return; }
            if ((DateTime.UtcNow - _noItemAt).TotalMilliseconds < 1200) return;
            _noItemAt = DateTime.UtcNow;
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg == dlg) return;
                NativeMethods.SetForegroundWindow(dlg);
                Log("activate:" + dlg, "activate dialog for UIA (no item for 1.2s)");
            }
            catch { }
        }

        private static string ClassChainOf(IntPtr h, int max)
        {
            var sb = new StringBuilder();
            IntPtr cur = h;
            for (int i = 0; i < max && cur != IntPtr.Zero; i++)
            {
                if (i > 0) sb.Append(" < ");
                sb.Append(NativeMethods.GetClassOf(cur));
                cur = NativeMethods.GetParent(cur);
            }
            return sb.ToString();
        }

        internal static string DetectHoverItem(int mx, int my, out Rectangle rect, out bool isDir)
        {
            rect = Rectangle.Empty;
            isDir = false;

            IntPtr hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = mx, Y = my });
            if (hwnd == IntPtr.Zero) return null;

            IntPtr dlg = AncestorOfClass(hwnd, "#32770");
            if (dlg == IntPtr.Zero) return null;
            Log("enter:" + dlg, "enter dlg=" + dlg + " chain=" + ClassChainOf(hwnd, 8));

            if (_missHwnd == dlg && (DateTime.UtcNow - _missAt).TotalMilliseconds < 300) return null;

            if (_stillHwnd == dlg && mx == _stillX && my == _stillY
                && (DateTime.UtcNow - _stillAt).TotalMilliseconds < 250)
            {
                rect = _stillRect; isDir = _stillIsDir;
                return _stillPath;
            }

            if (_hitHwnd == dlg && (DateTime.UtcNow - _hitAt).TotalMilliseconds < 600)
            {
                var zone = Rectangle.Inflate(_hitRect, 8, 4);
                if (zone.Contains(mx, my))
                {
                    _hitAt = DateTime.UtcNow;
                    rect = _hitRect; isDir = _hitIsDir;
                    return _hitPath;
                }
            }

            string folder = GetFolder(dlg);
            string result = null;
            Rectangle itemRect = Rectangle.Empty;
            bool itemIsDir = false;
            string name = ItemNameAt(mx, my, dlg, out itemRect);
            if (string.IsNullOrEmpty(name))
            {
                TryActivateForUia(dlg);
                Log("noitem:" + dlg, "no item at " + mx + "," + my + " probe=" + _lastItemProbe);
            }
            else _noItemDlg = IntPtr.Zero;
            Log("item:" + dlg, "folder=" + (folder ?? "null") + " item=" + (name ?? "null") + " rect=" + itemRect);
            if (!string.IsNullOrEmpty(name))
            {
                if (folder != null)
                {
                    string full = Path.Combine(folder, name);
                    if (!File.Exists(full) && !Directory.Exists(full))
                    {
                        // desktop.ini 本地化显示名（"下载"/"文档"等）→ 还原真实目录名
                        string real = ExplorerSupport.ResolveLocalizedName(folder, name);
                        if (real != null) full = real;
                    }
                    if (!File.Exists(full) && !Directory.Exists(full))
                    {
                        // 隐藏扩展名（.lnk/.url）：显示名没有扩展
                        string ext = Program.ResolveHiddenExt(full);
                        if (File.Exists(ext) || Directory.Exists(ext)) full = ext;
                    }
                    if (File.Exists(full) || Directory.Exists(full))
                    {
                        result = Program.ResolveHiddenExt(full);
                        itemIsDir = Directory.Exists(result);
                    }
                    else
                    {
                        // 侧边栏/导航窗格条目：名字就是"桌面/下载"等已知文件夹显示名
                        string known = KnownShellPath(name);
                        if (known != null) { result = Program.ResolveHiddenExt(known); itemIsDir = Directory.Exists(result); }
                    }
                }
                else
                {
                    // 「此电脑」/已知文件夹视图：地址栏只显示"此电脑"，条目本身就是盘符或桌面/下载等
                    string direct = KnownShellPath(name);
                    if (direct != null) { result = Program.ResolveHiddenExt(direct); itemIsDir = Directory.Exists(result); }
                }
            }
            if (result == null && folder == null)
                Log("nofolder:" + dlg, "folder=null addrRaw=[" + (_lastAddrRaw ?? "") + "]");

            rect = result != null ? itemRect : Rectangle.Empty;
            isDir = itemIsDir;

            _stillHwnd = dlg; _stillX = mx; _stillY = my;
            _stillPath = result; _stillRect = rect; _stillIsDir = isDir;
            _stillAt = DateTime.UtcNow;

            if (result == null) { MarkMiss(dlg); Log("miss:" + dlg, "no item at " + mx + "," + my); }
            else
            {
                _missHwnd = IntPtr.Zero;
                _hitHwnd = dlg; _hitPath = result; _hitRect = rect; _hitIsDir = isDir;
                _hitAt = DateTime.UtcNow;
                ActiveDialog = dlg;
                Log("hit:" + result, "hit " + (isDir ? "DIR " : "FILE ") + result);
            }
            return result;
        }

        private static void MarkMiss(IntPtr dlg)
        {
            _missHwnd = dlg;
            _missAt = DateTime.UtcNow;
        }

        private static IntPtr AncestorOfClass(IntPtr hwnd, string cls)
        {
            IntPtr cur = hwnd;
            for (int i = 0; i < MaxAncestor && cur != IntPtr.Zero; i++)
            {
                if (NativeMethods.GetClassOf(cur) == cls) return cur;
                cur = NativeMethods.GetParent(cur);
            }
            return IntPtr.Zero;
        }

        private static string GetFolder(IntPtr dlg)
        {
            if (_dirHwnd == dlg && (DateTime.UtcNow - _dirAt).TotalMilliseconds < 500)
                return _dirPath;

            string path = null;
            _lastAddrRaw = null;
            try
            {
                AutomationElement root = AutomationElement.FromHandle(dlg);
                if (root != null)
                {
                    // 首选地址栏：通用对话框里它是一个 ToolBar，Name 形如「地址: C:\dir」。
                    // Win11 的详情视图没有可读的地址 Edit（列表单元格反而是 Edit），只能靠这里
                    var barCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar);
                    AutomationElementCollection bars = root.FindAll(TreeScope.Descendants, barCond);
                    var rawNames = new StringBuilder();
                    for (int i = 0; i < bars.Count; i++)
                    {
                        string n = null;
                        try { n = bars[i].Current.Name; } catch { continue; }
                        if (rawNames.Length > 0) rawNames.Append(" | ");
                        rawNames.Append(n);
                        if (path == null) path = FolderFromAddressName(n);
                    }
                    _lastAddrRaw = "toolbars=" + bars.Count + " [" + rawNames + "]";

                    // 次选：值是一个已存在目录的 Edit（部分旧式/第三方对话框地址栏仍是 Edit）
                    if (path == null)
                    {
                        var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                        AutomationElementCollection edits = root.FindAll(TreeScope.Descendants, cond);
                        int n = Math.Min(edits.Count, 24);
                        for (int i = 0; i < n; i++)
                        {
                            try
                            {
                                object pat;
                                if (!edits[i].TryGetCurrentPattern(ValuePattern.Pattern, out pat) || pat == null) continue;
                                string v = ((ValuePattern)pat).Current.Value;
                                if (string.IsNullOrEmpty(v)) continue;
                                v = v.Trim().Trim('"');
                                if (v.Length > 3 && Directory.Exists(v)) { path = v; break; }
                            }
                            catch { }
                        }
                    }

                    // 兜底：面包屑子项的 HelpText 通常是完整路径
                    if (path == null)
                    {
                        for (int i = 0; i < bars.Count && path == null; i++)
                        {
                            AutomationElement child;
                            try { child = TreeWalker.ControlViewWalker.GetFirstChild(bars[i]); } catch { continue; }
                            for (int k = 0; k < 24 && child != null; k++)
                            {
                                try
                                {
                                    string h = child.Current.HelpText;
                                    if (!string.IsNullOrEmpty(h) && Directory.Exists(h)) { path = h.Trim(); break; }
                                }
                                catch { }
                                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { break; }
                            }
                        }
                    }
                }
            }
            catch { }

            _dirHwnd = dlg;
            _dirPath = path;
            _dirAt = DateTime.UtcNow;
            return path;
        }

        // 已知文件夹/盘符的"本地化显示名 → 真实路径"表（桌面/下载/文档、系统 (C:) 等）。
        // 用 SHGetFileInfo 取系统显示名，不依赖界面语言
        private static List<KeyValuePair<string, string>> _knownDisplay;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        // 用 Known Folder GUID 取真实路径（用户把"下载/文档"重定向到 D: 时也能拿到正确位置）
        private static string KnownFolder(string guid)
        {
            try
            {
                var id = new Guid(guid);
                IntPtr p;
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            catch { return null; }
        }

        private static void EnsureKnownDisplay()
        {
            if (_knownDisplay != null) return;
            var list = new List<KeyValuePair<string, string>>();
            var cands = new List<string>();
            Action<Environment.SpecialFolder> addKnown = f =>
            {
                try { string p = Environment.GetFolderPath(f); if (!string.IsNullOrEmpty(p)) cands.Add(p); } catch { }
            };
            addKnown(Environment.SpecialFolder.Desktop);
            addKnown(Environment.SpecialFolder.MyDocuments);
            addKnown(Environment.SpecialFolder.MyPictures);
            addKnown(Environment.SpecialFolder.MyMusic);
            addKnown(Environment.SpecialFolder.MyVideos);
            // Known Folder GUID：桌面/文档/下载/图片/音乐/视频（含被重定向的情况）
            cands.Add(KnownFolder("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"));
            cands.Add(KnownFolder("FDD39AD0-238F-46AF-ADB4-6C85480369C7"));
            cands.Add(KnownFolder("374DE290-123F-4565-9164-39C4925E467B"));
            cands.Add(KnownFolder("33E28130-4E1E-4676-835A-98395C3BC3BB"));
            cands.Add(KnownFolder("4BD8D571-6D19-48D3-BE97-422220080E43"));
            cands.Add(KnownFolder("18989B1D-99B5-455B-841C-AB7C74E4DDFC"));
            try
            {
                string prof = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(prof)) { cands.Add(prof); cands.Add(Path.Combine(prof, "Downloads")); cands.Add(Path.Combine(prof, "Documents")); }
            }
            catch { }
            try { foreach (var d in Directory.GetLogicalDrives()) cands.Add(d); } catch { }
            foreach (var p in cands)
            {
                try
                {
                    if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) continue;
                    // 英文目录名（Desktop/Downloads）也认，便于非中文系统
                    string leaf = Path.GetFileName(p.TrimEnd('\\'));
                    list.Add(new KeyValuePair<string, string>(leaf, p));
                    foreach (var al in ShellFolderAliases(leaf)) list.Add(new KeyValuePair<string, string>(al, p));
                    var sh = new NativeMethods.SHFILEINFOW();
                    uint flags = 0x200;   // SHGFI_DISPLAYNAME
                    if (NativeMethods.SHGetFileInfo(p, 0, ref sh, (uint)Marshal.SizeOf(typeof(NativeMethods.SHFILEINFOW)), flags) != IntPtr.Zero
                        && !string.IsNullOrEmpty(sh.szDisplayName))
                        list.Add(new KeyValuePair<string, string>(sh.szDisplayName.Trim(), p));
                }
                catch { }
            }
            _knownDisplay = list;
        }

        // 常见壳文件夹的多语言显示名别名（系统未通过 desktop.ini 本地化时兜底）
        private static string[] ShellFolderAliases(string leaf)
        {
            switch ((leaf ?? "").ToLowerInvariant())
            {
                case "desktop": return new[] { "桌面", "デスクトップ", "바탕 화면", "Bureau", "Рабочий стол", "Escritorio" };
                case "downloads": return new[] { "下载", "ダウンロード", "다운로드", "Téléchargements", "Загрузки", "Descargas" };
                case "documents": return new[] { "文档", "ドキュメント", "문서", "Dokumente", "Документы", "Documentos" };
                case "pictures": return new[] { "图片", "ピクチャ", "사진", "Images", "Изображения", "Imágenes" };
                case "music": return new[] { "音乐", "ミュージック", "음악", "Musique", "Музыка", "Música" };
                case "videos": return new[] { "视频", "ビデオ", "동영상", "Vidéos", "Видео", "Vídeos" };
            }
            return new string[0];
        }

        // 显示名（"桌面"、"下载"、"系统 (C:)"）→ 已存在的目录；不认识返回 null
        internal static string KnownShellPath(string display)
        {
            if (string.IsNullOrEmpty(display)) return null;
            display = display.Trim().Trim('"');
            if (display.Length == 0) return null;
            EnsureKnownDisplay();
            foreach (var kv in _knownDisplay)
                if (string.Equals(kv.Key, display, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        // 地址栏 Name → 目录：先按路径解析，再按本地化显示名解析（"地址: 桌面" → 桌面真实路径）
        private static string FolderFromAddressName(string barName)
        {
            if (string.IsNullOrEmpty(barName)) return null;
            string p = PathInText(barName);
            if (p != null) return p;
            p = KnownShellPath(barName);
            if (p != null) return p;
            int i = barName.IndexOf(": ", StringComparison.Ordinal);
            if (i > 0)
            {
                string tail = barName.Substring(i + 2);
                p = PathInText(tail) ?? KnownShellPath(tail);
                if (p != null) return p;
            }
            int j = barName.IndexOf('：');
            if (j > 0)
            {
                string tail = barName.Substring(j + 1);
                p = PathInText(tail) ?? KnownShellPath(tail);
                if (p != null) return p;
            }
            return null;
        }

        // 从一段文字里截出已存在的目录（"地址: C:\a\b" → "C:\a\b"），
        // 兼容盘符与 UNC 起点，并容忍尾部附加文本
        private static string PathInText(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            for (int i = 0; i + 2 < s.Length; i++)
            {
                bool drive = char.IsLetter(s[i]) && s[i + 1] == ':' && (s[i + 2] == '\\' || s[i + 2] == '/');
                bool unc = s[i] == '\\' && s[i + 1] == '\\';
                if (!drive && !unc) continue;
                string cand = s.Substring(i).Trim().Trim('"');
                for (int guard = 0; guard < 20 && cand.Length > 3; guard++)
                {
                    try { if (Directory.Exists(cand)) return cand; } catch { }
                    int k = cand.LastIndexOf('\\');
                    if (k <= 2) break;
                    cand = cand.Substring(0, k).Trim();
                }
                try { if (cand.Length > 3 && Directory.Exists(cand)) return cand; } catch { }
            }
            return null;
        }

        // 指针下的列表项名字。取不到（在按钮/地址栏/文件名框上）返回 null
        private static string _lastItemProbe = "";

        private static string ItemNameAt(int mx, int my, IntPtr dlg, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            try
            {
                System.Windows.Point pt = new System.Windows.Point(mx, my);
                AutomationElement el = AutomationElement.FromPoint(pt);
                if (el == null) { _lastItemProbe = "FromPoint=null"; return null; }
                try
                {
                    _lastItemProbe = el.Current.ControlType.ProgrammaticName + " name=[" + (el.Current.Name ?? "") + "] cls=" + (el.Current.ClassName ?? "");
                }
                catch { _lastItemProbe = "probe-fail"; }

                AutomationElement item = null;
                AutomationElement cur = el;
                for (int i = 0; i < 7 && cur != null; i++)
                {
                    ControlType ct;
                    try { ct = cur.Current.ControlType; } catch { break; }
                    if (ct == ControlType.ListItem || ct == ControlType.DataItem || ct == ControlType.TreeItem)
                    {
                        item = cur;
                        break;
                    }
                    try { cur = TreeWalker.ControlViewWalker.GetParent(cur); } catch { break; }
                }
                if (item == null) return null;

                string name = null;
                System.Windows.Rect r = System.Windows.Rect.Empty;
                try { name = item.Current.Name; r = item.Current.BoundingRectangle; } catch { return null; }
                if (string.IsNullOrEmpty(name)) return null;
                name = name.Trim();
                if (name == ".." || name.Length == 0) return null;

                NativeMethods.RECT wr;
                if (NativeMethods.GetWindowRect(dlg, out wr))
                {
                    var dialogRect = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
                    if (r.Width <= 0 || r.Height <= 0) return null;
                    var itemRect = Rectangle.FromLTRB((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
                    if (!dialogRect.IntersectsWith(itemRect)) return null;
                    rect = itemRect;
                }
                return name;
            }
            catch { return null; }
        }
    }
}
