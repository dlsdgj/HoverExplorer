using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverExplorer
{
    internal static class Program
    {
        public const string Version = "1.0.9.9";

        private static string _desktopPath;
        private static IntPtr _listview = IntPtr.Zero;
        private static RemoteReader _reader;
        private static readonly List<PopupForm> _panels = new List<PopupForm>();
        private static Mutex _singleInstance;
        internal static List<PopupForm> Panels { get { return _panels; } }
        private static readonly List<PinCardForm> _pinCards = new List<PinCardForm>();
        internal static bool ShellMenuOpen;

        // 刷新正在显示指定目录的所有面板（跨面板移动/复制后同步源和目标列表）
        public static void RefreshPanelsShowing(string dir1, string dir2)
        {
            foreach (var p in _panels)
            {
                if (p.IsDisposed) continue;
                if (p.ShowingPath(dir1) || (dir2 != null && p.ShowingPath(dir2))) p.RefreshPanel();
            }
        }
        // 列表配色/布局等即时应用：刷新所有存活面板
        public static void RefreshAllPanels()
        {
            foreach (var p in _panels) if (!p.IsDisposed) p.RefreshPanel();
        }
        // 目录改名后同步已打开面板/悬停预览里的路径，避免它们仍指向改名前的旧路径
        public static void RebasePaths(string oldPrefix, string newPrefix)
        {
            if (string.IsNullOrEmpty(oldPrefix) || string.IsNullOrEmpty(newPrefix)) return;
            PopupForm.RebaseFilePreviewPath(oldPrefix, newPrefix);
            foreach (var p in _panels)
            {
                if (p.IsDisposed) continue;
                p.RebasePath(oldPrefix, newPrefix);
            }
        }

        private static string _hoverFolder;
        private static string _extHoverFile;
        private static string _lastHoverPath;
        // 上一 Tick 的指针位置：用于判断"指针是否真的移动过"
        // （悬停的正确语义是"指针移上来"，光看"坐标下的目标变了"会被切换标签页/列表刷新骗到）
        private static int _lastTickX = int.MinValue, _lastTickY = int.MinValue;
        // 前台窗口正处于输入态（资源管理器重命名/地址栏）时的抑制标记，仅用于日志节流
        private static DateTime _lastEditLog;
        private static DateTime _extNullSince = DateTime.MinValue;
        private static DateTime _hoverStart;
        private static DateTime _closeStart = DateTime.MinValue;
        private static System.Windows.Forms.Timer _subCloseTimer;
        private static int _subCloseLevel;
        private static System.Windows.Forms.Timer _timer;
        private static NotifyIcon _notifyIcon;
        private static IntPtr _trayHicon = IntPtr.Zero;
        internal static ContextMenuStrip TrayMenu;
        private static HotkeyWindow _hotkey;
        private static bool _hoverEnabled = true;
        private static bool _settingsOpen; // 设置窗口打开期间挂起面板自动关闭（面板可能是对话框属主）
        private static SettingsForm _settingsForm;
        internal static bool _entryArmed = true;   // 收藏·历史触发区域/卡片悬停武装位：点击行打开 Explorer 后解除，鼠标离开后恢复

        // 开机自启：写 HKCU Run 键，无需安装
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "HoverExplorer";
        private static bool IsAutoStart()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    var v = k == null ? null : k.GetValue(RunValueName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }
        private static void SetAutoStart(bool enable)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return;
                    if (enable) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }

        public static void Log(string msg) { Trace.WriteLine(msg); }

        private static readonly object _dbgLock = new object();
        private static string _logPath;
        public static void DebugLog(string msg)
        {
            if (!Config.DebugLog) return;
            try
            {
                lock (_dbgLock)
                    File.AppendAllText(_logPath ?? (_logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hover_debug.log")),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
            }
            catch { }
        }


        private static void TruncateLogIfNeeded()
        {
            try
            {
                var path = _logPath ?? (_logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hover_debug.log"));
                if (!File.Exists(path)) return;
                if (new FileInfo(path).Length < 2000000) return;
                var lines = File.ReadAllLines(path);
                File.WriteAllLines(path, lines.Skip(Math.Max(0, lines.Length - 500)).ToArray());
            }
            catch { }
        }

        private static void LogBootInfo()
        {
            DebugLog("[boot] === HoverExplorer start ===");
            DebugLog("[boot] os=" + Environment.OSVersion.VersionString);
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) DebugLog("[boot] dpi=" + g.DpiX); } catch { }
            DebugLog("[boot] theme=" + Config.Theme + " lang=" + Config.Language + " explorer=" + Config.ExplorerEnabled + " preview=" + Config.PreviewFiles + " previewDelay=" + Config.PreviewDelay + " checkInterval=" + Config.CheckInterval + " debugLog=" + Config.DebugLog + " hotkey=" + Config.HotkeyEnabled + "/" + SettingsForm.HotkeyDisplay(Config.HotkeyEnabled, Config.HotkeyModifiers, Config.HotkeyKey));
        }

        private static DateTime _lastHideTooltip;
        private static void HideTooltips()
        {
            if ((DateTime.UtcNow - _lastHideTooltip).TotalMilliseconds < 80) return;
            _lastHideTooltip = DateTime.UtcNow;
            int n = 0;
            var shadowTargets = new System.Collections.Generic.List<IntPtr>();
            NativeMethods.EnumWindows((h, l) =>
            {
                string cls = NativeMethods.GetClassOf(h);
                if (cls == "tooltips_class32" && NativeMethods.IsWindowVisible(h))
                {
                    NativeMethods.SendMsg(h, 0x041E, IntPtr.Zero, IntPtr.Zero); // TTM_POP
                    // 移到屏幕外：移动窗口触发系统立即重绘原覆盖区域，避免 SW_HIDE 残影
                    NativeMethods.SetWindowPos(h, IntPtr.Zero, -32000, -32000, 0, 0, 0x0001 | 0x0004);
                    n++;
                }
                // TC/Delphi/LCL 的悬停提示是独立 HintWindow（TOPMOST），同样会盖住面板/预览。
                // 阴影是独立 SysShadow 窗口（z 序紧邻 HintWindow），只藏本体留阴影 = 残影，两都藏
                else if (cls == "HintWindow" && NativeMethods.IsWindowVisible(h))
                {
                    NativeMethods.ShowWindow(h, 0); // SW_HIDE：LCL 会自己再显示，每次都藏
                    var sn = NativeMethods.GetWindow(h, NativeMethods.GW_HWNDNEXT);
                    if (sn != IntPtr.Zero && NativeMethods.GetClassOf(sn) == "SysShadow" && NativeMethods.IsWindowVisible(sn))
                        shadowTargets.Add(sn);
                    var sp = NativeMethods.GetWindow(h, NativeMethods.GW_HWNDPREV);
                    if (sp != IntPtr.Zero && NativeMethods.GetClassOf(sp) == "SysShadow" && NativeMethods.IsWindowVisible(sp))
                        shadowTargets.Add(sp);
                    n++;
                }
                return true;
            }, IntPtr.Zero);
            foreach (var s in shadowTargets) NativeMethods.ShowWindow(s, 0);
            if (n > 0) DebugLog("[tooltip] hidden " + n + (shadowTargets.Count > 0 ? " +shadows " + shadowTargets.Count : ""));
        }

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => DebugLog("[FATAL] UnhandledException: " + e.ExceptionObject);
            Application.ThreadException += (s, e) => DebugLog("[FATAL] ThreadException: " + e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Config.Load();


            _singleInstance = new Mutex(false, @"Local\HoverExplorer.SingleInstance");
            bool ownsMutex;
            try { ownsMutex = _singleInstance.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                DebugLog("[boot] duplicate instance blocked, exiting");
                MessageBox.Show(I18n.T("already_running"), "HoverExplorer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _desktopPath = GetDesktopPath();
            TruncateLogIfNeeded();
            LogBootInfo();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _notifyIcon = new NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Visible = !Config.HideTrayIcon,
                Text = "HoverExplorer",
            };
            var trayMenu = new ContextMenuStrip();
            // 深浅主题：DarkColorTable 的各属性实时读 Config.BG/Fg —— 主题切换后菜单自动跟上。
            // 此前托盘菜单没挂渲染器，系统浅色下菜单白底，与整套深色 UI 割裂
            trayMenu.ForeColor = Config.FG;
            trayMenu.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            var explorerItem = new ToolStripMenuItem(I18n.T("explorer_hover"));
            _trayExplorerItem = explorerItem;
            explorerItem.CheckOnClick = true;
            explorerItem.Checked = Config.ExplorerEnabled;
            explorerItem.CheckedChanged += (s, e) =>
            {
                Config.ExplorerEnabled = explorerItem.Checked;
                Config.Save();
                if (_settingsForm != null && !_settingsForm.IsDisposed) _settingsForm.SyncFromTray();
            };
            trayMenu.Items.Add(explorerItem);
            var previewItem = new ToolStripMenuItem(I18n.T("file_preview"));
            _trayPreviewItem = previewItem;
            previewItem.CheckOnClick = true;
            previewItem.Checked = Config.PreviewFiles;
            previewItem.CheckedChanged += (s, e) =>
            {
                Config.PreviewFiles = previewItem.Checked;
                Config.Save();
                if (_settingsForm != null && !_settingsForm.IsDisposed) _settingsForm.SyncFromTray();
            };
            trayMenu.Items.Add(previewItem);
            var origItem = new ToolStripMenuItem(I18n.T("preview_orig"));
            origItem.CheckOnClick = true;
            origItem.Checked = Config.PreviewOriginalSize;
            origItem.CheckedChanged += (s, e) =>
            {
                Config.PreviewOriginalSize = origItem.Checked;
                Config.Save();
            };
            trayMenu.Items.Add(origItem);
            var debugItem = new ToolStripMenuItem(I18n.T("debug_log"));
            debugItem.CheckOnClick = true;
            debugItem.Checked = Config.DebugLog;
            debugItem.CheckedChanged += (s, e) => { Config.DebugLog = debugItem.Checked; Config.Save(); if (Config.DebugLog) LogBootInfo(); };
            trayMenu.Items.Add(debugItem);
            var tooltipItem = new ToolStripMenuItem(I18n.T("hide_tooltip"));
            tooltipItem.CheckOnClick = true;
            tooltipItem.Checked = Config.HideTooltip;
            tooltipItem.CheckedChanged += (s, e) => { Config.HideTooltip = tooltipItem.Checked; Config.Save(); };
            trayMenu.Items.Add(tooltipItem);
            var tipsItem = new ToolStripMenuItem(I18n.T("button_tips"));
            tipsItem.CheckOnClick = true;
            tipsItem.Checked = Config.ShowTips;
            tipsItem.CheckedChanged += (s, e) =>
            {
                Config.ShowTips = tipsItem.Checked;
                Config.Save();
                Tips.ApplyConfig();
                // 提示在控件创建时挂上，已打开的面板重建一次即可生效（预览窗下次打开生效）
                foreach (var p in _panels) { if (!p.IsDisposed) p.RefreshPanel(); }
            };
            trayMenu.Items.Add(tipsItem);
            var autostartItem = new ToolStripMenuItem(I18n.T("autostart"));
            autostartItem.CheckOnClick = true;
            autostartItem.Checked = IsAutoStart();
            autostartItem.CheckedChanged += (s, e) => SetAutoStart(autostartItem.Checked);
            trayMenu.Items.Add(autostartItem);
            var hideTrayItem = new ToolStripMenuItem(I18n.T("hide_tray"));
            hideTrayItem.CheckOnClick = true;
            hideTrayItem.Checked = Config.HideTrayIcon;
            hideTrayItem.CheckedChanged += (s, e) =>
            {
                Config.HideTrayIcon = hideTrayItem.Checked;
                Config.Save();
                _notifyIcon.Visible = !Config.HideTrayIcon;
            };
            trayMenu.Items.Add(hideTrayItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            var exitItem = new ToolStripMenuItem(I18n.T("exit"));
            // 重启：退出后拉起新实例（无单实例互斥，Restart 先启新进程再退出，清理逻辑仍走 ApplicationExit）
            var restartItem = new ToolStripMenuItem(I18n.T("restart"));
            restartItem.Click += (s, e) => Application.Restart();

            Action refreshExitText = () =>
            {
                exitItem.Text = I18n.T("exit");
            };
            exitItem.Click += (s, e) => Application.Exit();
            refreshExitText();

            var themeItem = new ToolStripMenuItem(I18n.T("theme"));
            var themeAuto = new ToolStripMenuItem(I18n.T("theme_auto")) { Checked = (Config.Theme == "auto") };
            var themeDark = new ToolStripMenuItem(I18n.T("theme_dark")) { Checked = (Config.Theme == "dark") };
            var themeLight = new ToolStripMenuItem(I18n.T("theme_light")) { Checked = (Config.Theme == "light") };
            Action refreshThemeMenus = () => { themeAuto.Checked = Config.Theme == "auto"; themeDark.Checked = Config.Theme == "dark"; themeLight.Checked = Config.Theme == "light"; };
            themeAuto.Click += (s, e) => { Config.Theme = "auto"; ApplyThemeChange(); refreshThemeMenus(); };
            themeDark.Click += (s, e) => { Config.Theme = "dark"; ApplyThemeChange(); refreshThemeMenus(); };
            themeLight.Click += (s, e) => { Config.Theme = "light"; ApplyThemeChange(); refreshThemeMenus(); };
            themeItem.DropDownItems.Add(themeAuto);
            themeItem.DropDownItems.Add(themeDark);
            themeItem.DropDownItems.Add(themeLight);

            var settingsItem = new ToolStripMenuItem(I18n.T("settings"), null, (EventHandler)((s, e) =>
            {
                if (_settingsOpen && _settingsForm != null && !_settingsForm.IsDisposed)
                {
                    // 已打开：置顶激活（从托盘再次进入时把已有设置窗拉到前台）
                    if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
                    _settingsForm.Show();
                    _settingsForm.BringToFront();
                    _settingsForm.Activate();
                    return;
                }
                _settingsOpen = true;
                _settingsForm = new SettingsForm();
                _settingsForm.FormClosed += (s2, e2) =>
                {
                    // 设置即改即生效：改动已随事件实时写入并保存，关闭无需提交
                    _settingsOpen = false;
                    _settingsForm = null;
                };
                // 非模态：设置窗开着时仍可悬停操作列表/预览（Tick 不再被 _settingsOpen 短路）
                _settingsForm.Show();
            }));

            int curLang = Array.IndexOf(I18n.LangCodes, Config.Language);
            var langItem = new ToolStripMenuItem("Language" + (curLang >= 0 ? "  " + I18n.LangCodes[curLang] : ""));
            var addCardItem = new ToolStripMenuItem(I18n.T("card_from_clip"), null, (EventHandler)((s, e) => AddCardFromClipboard()));

            for (int i = 0; i < I18n.LangCodes.Length; i++)
            {
                var code = I18n.LangCodes[i];
                var name = I18n.LangNames[i];
                var sub = new ToolStripMenuItem(code + "  " + name) { Checked = (code == Config.Language) };
                sub.Click += (s, e) =>
                {
                    Config.Language = code;
                    I18n.Lang = code;
                    Config.RefreshSortLabels();
                    Config.Save();
                    foreach (ToolStripMenuItem si in langItem.DropDownItems) si.Checked = false;
                    sub.Checked = true;
                    langItem.Text = "Language  " + code;
                    explorerItem.Text = I18n.T("explorer_hover");
                    previewItem.Text = I18n.T("file_preview");
                    origItem.Text = I18n.T("preview_orig");
                    debugItem.Text = I18n.T("debug_log");
                    tooltipItem.Text = I18n.T("hide_tooltip");
                    tipsItem.Text = I18n.T("button_tips");
                    autostartItem.Text = I18n.T("autostart");
                    hideTrayItem.Text = I18n.T("hide_tray");

                    langItem.Text = "Language  " + code;
                    themeItem.Text = I18n.T("theme");
                    themeDark.Text = I18n.T("theme_dark");
                    themeLight.Text = I18n.T("theme_light");
                    settingsItem.Text = I18n.T("settings");
                    restartItem.Text = I18n.T("restart");
                    addCardItem.Text = I18n.T("card_from_clip");
                    EntryListForm.NotifyChanged();   // 开着的收藏·历史列表文本/按钮提示换语言
                    if (_entryCardForm != null && !_entryCardForm.IsDisposed) _entryCardForm.Relayout();   // 重测宽度（长语言自适应）+ 重绘换语言
                    if (_settingsOpen && _settingsForm != null && !_settingsForm.IsDisposed)
                    {
                        var sLoc = _settingsForm.Location;
                        _settingsForm.Close();
                        _settingsOpen = true;
                        _settingsForm = new SettingsForm();
                        _settingsForm.FormClosed += (s3, e3) => { _settingsOpen = false; _settingsForm = null; };
                        try { _settingsForm.Location = sLoc; } catch { }
                        _settingsForm.Show();
                    }
                    refreshExitText();
                };
                langItem.DropDownItems.Add(sub);
            }
            trayMenu.Items.Add(langItem);
            trayMenu.Items.Add(themeItem);

            trayMenu.Items.Add(addCardItem);
            trayMenu.Items.Add(settingsItem);
            trayMenu.Items.Add(new ToolStripSeparator());

            trayMenu.Items.Add(restartItem);
            trayMenu.Items.Add(exitItem);

            TrayMenu = trayMenu;
            ThemeStripRecursive(trayMenu);
            // 选项菜单关闭后解除所有面板的菜单打开标记（避免面板被悬停丢失逻辑关闭后标记残留）
            trayMenu.Closed += (s, e) => { foreach (var p in _panels) p.MenuOpen = false; };
            _notifyIcon.ContextMenuStrip = trayMenu;

            _hotkey = new HotkeyWindow();
            _hotkey.HotkeyPressed += () => ToggleHoverEnabled();
            _hotkey.CardHotkeyPressed += () => AddCardFromClipboard();
            _hotkey.EntryHotkeyPressed += () => ShowEntryList(Cursor.Position.X, Cursor.Position.Y, true);

            PopupForm.OnSortChange = () =>
            {
                foreach (var p in _panels) p.RefreshPanel();
            };

            _timer = new System.Windows.Forms.Timer { Interval = Config.CheckInterval };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();

            foreach (var item in Config.PinnedItems)
            {
                // 文件夹卡片 Kind 也是 file：路径存在性要同时认文件与目录（只查 File.Exists 会丢文件夹卡片）
                if (item.Kind == "file" && !File.Exists(item.Path) && !Directory.Exists(item.Path)) continue;
                var card = new PinCardForm(item);
                card.Tag = item;
                _pinCards.Add(card);
                card.Show();
            }

            Application.ApplicationExit += (s, e) =>
            {
                _timer.Stop();
                if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); }
                if (_trayHicon != IntPtr.Zero) { try { NativeMethods.DestroyIcon(_trayHicon); } catch { } _trayHicon = IntPtr.Zero; }
                if (_hotkey != null) _hotkey.Unregister();
                if (_reader != null) _reader.Dispose();
                if (_entryIndicator != null) { try { _entryIndicator.Dispose(); } catch { } _entryIndicator = null; }
                if (_singleInstance != null) { try { _singleInstance.ReleaseMutex(); _singleInstance.Close(); } catch { } }
            };

            // 卡片上的滚轮：卡片是未激活窗口，WM_MOUSEWHEEL 只会发给前台窗口，
            // 故用全局消息过滤按指针位置命中卡片（与 PreviewForm 的滚轮缩放同一套路）
            Application.AddMessageFilter(new CardWheelFilter());
            // 悬停面板顶部操作栏上的滚轮：在已保存的列表配色方案间切换（同样是非激活窗口，走全局过滤）
            Application.AddMessageFilter(new ListWheelFilter());

            // 已设置的触发区域：启动即显示桌面色块指示
            UpdateEntryIndicator();
            UpdatePauseIndicator();

            Application.Run();
        }

        // 滚轮在某张卡片上 → 只切换这张卡片的配色，并弹出（实时更新的）配色列表
        private class CardWheelFilter : IMessageFilter
        {
            private const int WM_MOUSEWHEEL = 0x20A;
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL) return false;
                long lp = m.LParam.ToInt64();
                int x = (short)(lp & 0xFFFF);
                int y = (short)((lp >> 16) & 0xFFFF);
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                int dir = delta < 0 ? 1 : -1;   // 向下滚 = 下一套，向上 = 上一套

                for (int i = _pinCards.Count - 1; i >= 0; i--)
                {
                    var c = _pinCards[i];
                    if (c == null || c.IsDisposed) continue;
                    if (!c.Bounds.Contains(x, y)) continue;
                    AdjustCardPalette(c, dir, c.Bounds);
                    return true;                // 吃掉消息：卡片上滚动不穿透到下面的窗口
                }
                // 收藏·历史卡片上滚动：同一套配色体系
                if (_entryCardForm != null && !_entryCardForm.IsDisposed && _entryCardForm.Bounds.Contains(x, y))
                {
                    AdjustCardPalette(_entryCardForm, dir, _entryCardForm.Bounds);
                    return true;
                }
                // 列表本身上滚动：继续调刚才那张卡片（指针常会滑到列表上）
                var list = PaletteListForm.Instance;
                if (list != null && list.Visible && _wheelCard != null && !_wheelCard.IsDisposed
                    && list.Bounds.Contains(x, y))
                {
                    AdjustCardPalette(_wheelCard, dir, _wheelCard.Bounds);
                    return true;
                }
                return false;
            }
        }

        // 悬停面板顶部操作栏上的滚轮 → 切换已保存的列表配色方案
        // （面板是不抢前台的未激活窗口，收不到自己的 WM_MOUSEWHEEL，只能靠全局过滤）
        private class ListWheelFilter : IMessageFilter
        {
            private const int WM_MOUSEWHEEL = 0x20A;
            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL) return false;
                long lp = m.LParam.ToInt64();
                int x = (short)(lp & 0xFFFF);
                int y = (short)((lp >> 16) & 0xFFFF);
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                int dir = delta < 0 ? 1 : -1;   // 向下滚 = 下一套，向上 = 上一套
                for (int i = _panels.Count - 1; i >= 0; i--)
                {
                    var p = _panels[i];
                    if (p == null || p.IsDisposed) continue;
                    if (!p.InHeader(x, y)) continue;
                    p.CycleListPalette(dir);
                    return true;   // 吃掉消息：操作栏上滚动不再穿透到下层窗口
                }
                return false;
            }
        }

        private static IPaletteTarget _wheelCard;   // 最近一次正在滚轮调色的卡片（桌面卡/收藏·历史卡）

        // ---- 拖拽滚轮 / 翻页键：低级钩子（按需装/卸，Tick 每轮同步）----
        // 面板是不抢前台的未激活窗口：OLE 拖拽中滚轮消息被拖拽循环截走，键盘焦点又始终在
        // 前台窗口——IMessageFilter 只能过滤本线程消息，两种输入都到不了面板，
        // 只能挂 WH_MOUSE_LL / WH_KEYBOARD_LL 按指针位置路由。钩子不常驻：
        // 拖拽会话（左键按住≥400ms+移动10px）才挂鼠标钩子、面板可见才挂键盘钩子
        private static IntPtr _mouseHook;
        private static NativeMethods.HookProc _mouseHookProc;
        private static IntPtr _kbHook;
        private static NativeMethods.HookProc _kbHookProc;

        private static void SyncMouseHook(bool want)
        {
            if (want && _mouseHook == IntPtr.Zero)
            {
                if (_mouseHookProc == null) _mouseHookProc = MouseHookProc;
                _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseHookProc,
                    NativeMethods.GetModuleHandle(null), 0);
                DebugLog("[hook] mouse hook " + (_mouseHook != IntPtr.Zero ? "installed" : "install fail"));
            }
            else if (!want && _mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
                DebugLog("[hook] mouse hook removed");
            }
        }

        private static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == NativeMethods.WM_MOUSEWHEEL)
            {
                var hs = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                int delta = (short)((hs.mouseData >> 16) & 0xFFFF);
                if (RouteDragWheel(hs.pt.X, hs.pt.Y, delta)) return (IntPtr)1;   // 已滚列表：吃掉，不穿透到下层窗口
            }
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // 滚轮路由到指针下的最顶层面板列表行区；收藏·历史列表（自适应高度无滚动）与预览窗不参与
        private static bool RouteDragWheel(int x, int y, int delta)
        {
            for (int i = _panels.Count - 1; i >= 0; i--)
            {
                var p = _panels[i];
                if (p == null || p.IsDisposed || !p.Visible) continue;
                if (!p.ListRectContains(x, y)) continue;
                p.ScrollByWheel(delta);
                return true;
            }
            return false;
        }

        private static void SyncKeyboardHook(bool want)
        {
            if (want && _kbHook == IntPtr.Zero)
            {
                if (_kbHookProc == null) _kbHookProc = KeyboardHookProc;
                _kbHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _kbHookProc,
                    NativeMethods.GetModuleHandle(null), 0);
                DebugLog("[hook] keyboard hook " + (_kbHook != IntPtr.Zero ? "installed" : "install fail"));
            }
            else if (!want && _kbHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_kbHook);
                _kbHook = IntPtr.Zero;
                DebugLog("[hook] keyboard hook removed");
            }
        }

        private static IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam.ToInt32() == NativeMethods.WM_KEYDOWN || wParam.ToInt32() == NativeMethods.WM_SYSKEYDOWN))
            {
                var hs = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                if (hs.vkCode == NativeMethods.VK_PRIOR || hs.vkCode == NativeMethods.VK_NEXT)
                {
                    // 仅裸翻页键：带 Ctrl/Alt/Shift 的组合留给前台应用
                    bool mod = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0
                        || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0
                        || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
                    if (!mod && RoutePageKey(hs.vkCode == NativeMethods.VK_NEXT ? 1 : -1)) return (IntPtr)1;
                }
            }
            return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        // 翻页键路由：指针压在哪个面板上就翻哪个（最顶层优先）；不在面板上不拦截，
        // 前台应用（Explorer 等）的翻页不受影响
        private static bool RoutePageKey(int dir)
        {
            NativeMethods.POINT pt;
            NativeMethods.GetCursorPos(out pt);
            for (int i = _panels.Count - 1; i >= 0; i--)
            {
                var p = _panels[i];
                if (p == null || p.IsDisposed || !p.Visible) continue;
                if (!p.ContainsPoint(pt.X, pt.Y)) continue;
                p.PageKey(dir);
                return true;
            }
            return false;
        }

        private static void AdjustCardPalette(IPaletteTarget card, int dir, Rectangle anchor)
        {
            int idx;
            var pin = card as PinCardForm;
            var entry = card as EntryCardForm;
            if (pin != null) idx = pin.CyclePalette(dir);
            else if (entry != null) idx = entry.CyclePalette(dir);
            else return;
            _wheelCard = card;
            PaletteListForm.ShowFor(idx, anchor, card);
            var pal = CardPalettes.Get(idx);
            DebugLog("[card-palette] card=" + (pin != null ? Convert.ToString(pin.Tag) : "entry") + " -> " + idx + " " + (pal != null ? pal.Name : ""));
        }

        private static System.Windows.Forms.Timer _saveTimer;

        // 写盘合并：滚轮连滚 / 连续拖卡片不会每格写一次 config.json
        internal static void SaveConfigDebounced()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new System.Windows.Forms.Timer { Interval = 400 };
                _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); Config.Save(); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        // 菜单全局主题化：递归所有子下拉（语言/主题等子菜单是独立 ToolStrip，不继承父级
        // ForeColor/渲染器——漏一层就是黑字白底）。DarkColorTable 属性实时读 Config，
        // 主题切换后重挂即全局换装
        private static void ThemeStripRecursive(ToolStrip strip)
        {
            if (strip == null) return;
            strip.ForeColor = Config.FG;
            strip.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            foreach (ToolStripItem it in strip.Items)
            {
                it.ForeColor = Config.FG;
                var dd = it as ToolStripDropDownItem;
                if (dd != null && dd.HasDropDownItems) ThemeStripRecursive(dd.DropDown);
            }
        }

        // 主题切换级联（托盘菜单三选一与"跟随系统"自动切换共用同一刷新链）
        internal static void ApplyThemeChange()
        {
            Config.ApplyTheme();
            Config.Save();
            _lastSysDark = Config.SystemDark;
            PopupForm.ClearUiIconCache();
            if (TrayMenu != null) ThemeStripRecursive(TrayMenu);   // 全菜单层级跟主题
            foreach (var p in _panels) p.RefreshPanel();
            foreach (var c in _pinCards) { if (!c.IsDisposed) c.ApplyPalette(); }
            EntryListForm.NotifyChanged();
            if (_entryCardForm != null && !_entryCardForm.IsDisposed) _entryCardForm.Relayout();
        }

        private static bool _lastGatePass = true;

        // "跟随系统"轮询状态（Tick 内 2s 节流一次注册表读取）
        private static DateTime _lastSysThemeAt = DateTime.UtcNow;
        private static bool _lastSysDark = Config.SystemDark;

        private static void Tick()
        {
            try { TickImpl(); }
            catch (Exception ex) { Log("[tick异常] " + ex.Message); }
        }

        private static readonly uint _currentPid = (uint)Process.GetCurrentProcess().Id;
        public static uint CurrentPid { get { return _currentPid; } }

        private static DateTime _lastCoverLog;
        // h 是否被其他进程的 TopMost 窗口压住（从 TopMost band 顶到 h 之间存在外部相交窗口）。
        // 本进程窗口之间的相对 z 序保持创建时的稳定顺序，不做每 Tick 翻转。
        // 必须叠加几何检查：TopMost band 顶部常有不叠加的外部窗口（任务栏、远处 tooltip），
        // 不检查几何会误判为压住 → 每 Tick 提顶 → 叠加的本进程窗口互相翻转 z 序 → 闪烁。
        // 注意：上方是普通窗口（非 TopMost）时必须 return false——悬停资源管理器是常态，
        // 面板必然与前台 Explorer 相交，若判"被盖"会每 Tick 提顶 → 面板盖住预览/卡片 + 闪烁。
        // he 掉出 TopMost band（被普通窗口真盖）的场景由 EnsureWinTopmost 第一段的
        // EXSTYLE 缺失检查兜底（补 SetWindowPos 提回），不走这里
        public static bool CoveredByOther(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            NativeMethods.RECT rcH;
            if (!NativeMethods.GetWindowRect(h, out rcH)) return false;
            try
            {
                IntPtr w = NativeMethods.GetWindow(h, NativeMethods.GW_HWNDPREV);
                for (int i = 0; i < 32 && w != IntPtr.Zero; i++)
                {
                    if ((NativeMethods.GetWindowLongW(w, NativeMethods.GWL_EXSTYLE) & (int)NativeMethods.WS_EX_TOPMOST) == 0)
                        return false;
                    uint pid;
                    NativeMethods.GetWindowThreadProcessId(w, out pid);
                    if (pid != _currentPid && NativeMethods.IsWindowVisible(w))
                    {
                        NativeMethods.RECT rcW;
                        if (NativeMethods.GetWindowRect(w, out rcW)
                            && rcW.Left < rcH.Right && rcW.Right > rcH.Left
                            && rcW.Top < rcH.Bottom && rcW.Bottom > rcH.Top)
                        {
                            if ((DateTime.UtcNow - _lastCoverLog).TotalMilliseconds > 500)
                            {
                                _lastCoverLog = DateTime.UtcNow;
                                DebugLog("[z] covered by pid=" + pid + " rect=" + rcW.Left + "," + rcW.Top + "-" + rcW.Right + "," + rcW.Bottom + " my=" + rcH.Left + "," + rcH.Top + "-" + rcH.Right + "," + rcH.Bottom);
                            }
                            return true;
                        }
                    }
                    w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDPREV);
                }
                return false;
            }
            catch (Exception ex) { DebugLog("[z] CoveredByOther ex: " + ex.Message); return false; }
        }

        // 前台窗口正在输入（资源管理器重命名编辑框 / 地址栏）：此时不该弹任何悬停面板或预览。
        // 用 GetGUIThreadInfo 取前台线程的焦点控件判类名，比 UIA 便宜得多（每 Tick 可调）。
        // 本进程窗口（自己的搜索框/预览文本框）不算，否则一输入就把自己关了。
        public static bool IsEditingInForeground()
        {
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                uint pid;
                uint tid = NativeMethods.GetWindowThreadProcessId(fg, out pid);
                if (tid == 0 || pid == _currentPid) return false;
                var gti = new NativeMethods.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.GUITHREADINFO)) };
                if (!NativeMethods.GetGUIThreadInfo(tid, ref gti) || gti.hwndFocus == IntPtr.Zero) return false;
                var sb = new System.Text.StringBuilder(64);
                NativeMethods.GetClassName(gti.hwndFocus, sb, 64);
                string cls = sb.ToString();
                // Edit / RichEdit*：重命名框、地址栏、Explorer 搜索框都是这类
                bool editing = cls == "Edit" || cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
                if (editing && (DateTime.UtcNow - _lastEditLog).TotalMilliseconds > 1000)
                {
                    _lastEditLog = DateTime.UtcNow;
                    DebugLog("[tick] foreground editing (" + cls + "), suppress hover");
                }
                return editing;
            }
            catch { return false; }
        }

        public static bool IsPinned(string path) { return IsPinned("file", path); }
        public static bool IsPinned(string kind, string path) { return Config.PinnedItems.Exists(p => p.Kind == kind && p.Path == path); }

        public static void TogglePin(string path)
        {
            TogglePinItem(new PinnedItem { Kind = "file", Path = path, X = Cursor.Position.X + 20, Y = Cursor.Position.Y + 20 });
        }

        // 统一入口：任意来源（文件/链接/文本）→ 卡片
        public static void TogglePinItem(PinnedItem item)
        {
            if (IsPinned(item.Kind, item.Path)) { Unpin(item.Kind, item.Path); return; }
            var card = new PinCardForm(item);
            card.Tag = item;
            Config.PinnedItems.Add(item);
            Config.Save();
            _pinCards.Add(card);
            card.Show();
            DebugLog("[pin] add " + item.Kind + " " + item.Path);
        }

        public static void Unpin(string path) { Unpin("file", path); }
        public static void Unpin(string kind, string path)
        {
            Config.PinnedItems.RemoveAll(p => p.Kind == kind && p.Path == path);
            Config.Save();
            for (int i = _pinCards.Count - 1; i >= 0; i--)
            {
                var c = _pinCards[i];
                if (c.IsDisposed) { _pinCards.RemoveAt(i); continue; }
                var pi = c.Tag as PinnedItem;
                if (pi != null && pi.Kind == kind && pi.Path == path) { try { c.Close(); c.Dispose(); } catch { } _pinCards.RemoveAt(i); PaletteListForm.HideNow(); }
            }
            DebugLog("[pin] remove " + kind + " " + path);
        }

        public static void UpdatePinPos(string path, int x, int y) { UpdatePinPos("file", path, x, y); }
        public static void UpdatePinPos(string kind, string path, int x, int y)
        {
            var item = Config.PinnedItems.Find(p => p.Kind == kind && p.Path == path);
            if (item != null) { item.X = x; item.Y = y; Config.Save(); }
        }

        public static void ShowBalloon(string title, string msg)
        {
            try { if (_notifyIcon != null) _notifyIcon.ShowBalloonTip(1200, title, msg, ToolTipIcon.Info); } catch { }
        }

        // 已存在卡片时激活并闪烁，让用户立刻看到（不依赖系统通知气泡）
        public static void FocusPinCard(string kind, string path)
        {
            foreach (var c in _pinCards)
            {
                if (c.IsDisposed) continue;
                var pi = c.Tag as PinnedItem;
                if (pi != null && pi.Kind == kind && pi.Path == path)
                {
                    try
                    {
                        NativeMethods.SetWindowPos(c.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
                        var orig = c.BackColor;
                        var t = new System.Windows.Forms.Timer { Interval = 120 };
                        int n = 0;
                        t.Tick += (s, e) =>
                        {
                            n++;
                            c.BackColor = (n % 2 == 1) ? Config.DropHighlight : orig;
                            c.Invalidate();
                            if (n >= 6) { t.Stop(); t.Dispose(); c.BackColor = orig; c.Invalidate(); }
                        };
                        t.Start();
                    }
                    catch { }
                    return;
                }
            }
        }

        // 热键/托盘入口：剪贴板内容 → ingest 分类 → 卡片
        private static void AddCardFromClipboard()
        {
            try
            {
                string raw = null;
                try { raw = Clipboard.GetText(); } catch (Exception ex) { DebugLog("[pin] clip-gettext-fail " + ex.Message); }
                // 复制文件(Ctrl+C)时剪贴板是 FileDrop 格式，GetText 返回空，需回退读 FileDrop
                if (string.IsNullOrWhiteSpace(raw))
                {
                    try { var files = Clipboard.GetData(DataFormats.FileDrop) as string[]; if (files != null && files.Length > 0) raw = files[0]; } catch { }
                }
                DebugLog("[pin] from-clipboard start raw=[" + (raw == null ? "null" : raw) + "]");
                var r = Ingest.Classify(raw);
                if (r == null) { DebugLog("[pin] classify-null"); ShowBalloon("HoverExplorer", I18n.T("card_empty_clip")); return; }
                DebugLog("[pin] classify kind=" + r.Kind + " target=[" + r.Target + "] pinned=" + IsPinned(r.Kind, r.Target));
                if (IsPinned(r.Kind, r.Target)) { DebugLog("[pin] already-pinned"); FocusPinCard(r.Kind, r.Target); ShowBalloon("HoverExplorer", I18n.T("card_exists")); return; }
                var item = new PinnedItem { Kind = r.Kind, Path = r.Target, Title = r.Title, X = Cursor.Position.X + 20, Y = Cursor.Position.Y + 20 };
                TogglePinItem(item);
                ShowBalloon("HoverExplorer", I18n.T("card_added"));
                DebugLog("[pin] from-clipboard " + item.Kind + " " + item.Path);
            }
            catch (Exception ex) { DebugLog("[pin] from-clipboard-exception " + ex.Message + " | " + ex.StackTrace); }
        }

        private static DateTime _lastMenuLog;
        private static DateTime _lastMenuSeen;
        private static DateTime _lastMenuCheck;
        private static bool IsSystemMenuOpen()
        {
            var now = DateTime.UtcNow;
            // 菜单打开后的 500ms 宽限期内直接返回，不 EnumWindows
            if ((now - _lastMenuSeen).TotalMilliseconds < 500) return true;
            // 节流：距上次未发现菜单的检测不足 200ms 则跳过 EnumWindows
            if ((now - _lastMenuCheck).TotalMilliseconds < 200) return false;
            _lastMenuCheck = now;
            bool found = false;
            NativeMethods.EnumWindows((h, l) =>
            {
                if (NativeMethods.IsWindowVisible(h))
                {
                    var sb = new System.Text.StringBuilder(64);
                    NativeMethods.GetClassName(h, sb, 64);
                    var cn = sb.ToString();
                    if (cn == "#32768") { found = true; return false; }
                }
                return true;
            }, IntPtr.Zero);
            if (found)
            {
                _lastMenuSeen = now;
                if ((now - _lastMenuLog).TotalMilliseconds > 1000)
                {
                    _lastMenuLog = now;
                    DebugLog("[tick] system menu #32768 detected, skip");
                }
            }
            return found;
        }

        // 全系统"单击失效"的根因防护：卡片拖动时用 Capture=true 接管鼠标，
        // 若 MouseUp 被模态菜单/焦点切换/窗口重排吞掉，卡片会永久持有系统鼠标捕获，
        // 所有点击都被路由到它（窗口本身不动，看起来就是"单击没反应"），
        // 只有销毁该窗口（中键关闭 / Unpin）才随 DestroyWindow 自动释放。
        // 左键已物理抬起却仍有卡片持有捕获，说明 MouseUp 丢了 → 强制释放自愈。
        private static void ReleaseStuckCaptures()
        {
            if ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0) return;
            for (int i = 0; i < _pinCards.Count; i++)
            {
                var c = _pinCards[i];
                if (c == null || c.IsDisposed || !c.IsHandleCreated) continue;
                if (c.Capture) c.ReleaseStuckDrag();
            }
        }

        // 切换悬停启用/暂停（全局热键与暂停触控区共用）：
        // 收起面板 + 托盘气泡 + 屏幕状态浮层 + 系统提示音
        internal static void ToggleHoverEnabled()
        {
            // 触发来源记录（诊断“神秘暂停”：热键 / 暂停触控区，还是别的路径）
            string src = "?";
            try
            {
                var st = new System.Diagnostics.StackTrace(true);
                for (int i = 1; i < Math.Min(6, st.FrameCount); i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    if (m != null && m.DeclaringType != null) { src = m.DeclaringType.Name + "." + m.Name; break; }
                }
            }
            catch { }
            DebugLog("[toggle] src=" + src + " -> " + (!_hoverEnabled ? "disabled" : "enabled"));
            _hoverEnabled = !_hoverEnabled;
            if (!_hoverEnabled)
            {
                CloseAll();
                _hoverFolder = null;
                _extHoverFile = null;
                PopupForm.ScheduleExtPreviewClose(0, 0);
            }
            try { _notifyIcon.ShowBalloonTip(800, "HoverExplorer", _hoverEnabled ? I18n.T("hover_enabled") : I18n.T("hover_disabled"), ToolTipIcon.Info); } catch { }
            ShowStatusToast(_hoverEnabled ? I18n.T("hover_enabled") : I18n.T("hover_disabled"), _hoverEnabled);
            UpdatePauseIndicator();   // 触控区色块随开关状态换色（绿=启用 / 红=已暂停）
        }

        private static StatusToastForm _statusToast;

        // 启停状态浮层：屏幕上方居中的短暂提示条；enabled 决定系统提示音（恢复=Asterisk 暂停=Exclamation）
        private static void ShowStatusToast(string text, bool enabled)
        {
            try { if (enabled) System.Media.SystemSounds.Asterisk.Play(); else System.Media.SystemSounds.Exclamation.Play(); } catch { }
            try
            {
                if (_statusToast != null) { _statusToast.Close(); _statusToast.Dispose(); _statusToast = null; }
                _statusToast = new StatusToastForm(text);
                _statusToast.Show();
            }
            catch { }
        }

        // ===== 暂停触控区：鼠标驻留屏幕角落 0.3s 切换悬停启用/暂停 =====
        // 驻留门槛防扫过误触；切换后解除武装，鼠标离开角落再回来才会再次切换
        private static System.Windows.Forms.Timer _pzTimer;
        private static bool _pzArmed = true;
        private static bool _pzLastIn;

        private static bool _lbWasDown;
        private static DateTime _lbDownSince;
        private static System.Drawing.Point _lbDownPos;
        private static bool _dragSessionActive;

        // 拖拽会话判定：左键按住 ≥400ms 且已移出按下点 ≥10px（区分“点按”与“拖拽”）。
        // 拖拽会话中悬停检测继续运行：光标停在文件夹上弹该文件夹面板（弹簧式导航），
        // 未打开列表时同样生效；文件预览与自动关闭在该状态下抑制
        private static bool DragSession(int mx, int my)
        {
            bool down = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (!down) { _lbWasDown = false; return false; }
            if (!_lbWasDown)
            {
                _lbWasDown = true;
                _lbDownSince = DateTime.UtcNow;
                NativeMethods.POINT pt; NativeMethods.GetCursorPos(out pt);
                _lbDownPos = new System.Drawing.Point(pt.X, pt.Y);
            }
            if ((DateTime.UtcNow - _lbDownSince).TotalMilliseconds < 400) return false;
            int dx = mx - _lbDownPos.X, dy = my - _lbDownPos.Y;
            return dx * dx + dy * dy >= 100;
        }

        // 暂停触控区 = 独立框选矩形（设置里框选/清除）：
        // 鼠标驻留该区域 300ms 切换悬停启用/暂停（浮层+声音，与全局热键 Ctrl+Alt+Q 同效）。
        // 放在 TickImpl 里 _hoverEnabled 提前返回之前：暂停状态下也要能靠区域恢复。
        // 触发区域不再承担“悬停弹 收藏·历史 列表”（列表走其快捷键 Ctrl+Alt+E）；
        // 拖拽进入触发区域时仍会立即弹列表接收拖放（见 TickImpl 左键按住分支）
        private static void PauseZoneTick()
        {
            // 拖拽进行中（左键按住）不切换：拖拽经过暂停区域不是暂停操作
            if ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0) return;
            NativeMethods.POINT pt;
            NativeMethods.GetCursorPos(out pt);
            if (Config.PauseRectW < 5 || Config.PauseRectH < 5)
            {
                _pzArmed = true;
                if (_pzTimer != null) { _pzTimer.Stop(); _pzTimer.Dispose(); _pzTimer = null; }
                return;
            }
            bool inZone = pt.X >= Config.PauseRectX && pt.X < Config.PauseRectX + Config.PauseRectW
                && pt.Y >= Config.PauseRectY && pt.Y < Config.PauseRectY + Config.PauseRectH;
            if (inZone)
            {
                if (_pzArmed && _pzTimer == null)
                {
                    _pzTimer = new System.Windows.Forms.Timer { Interval = 300 };
                    _pzTimer.Tick += (s, e) =>
                    {
                        var t = _pzTimer;
                        if (t != null) { t.Stop(); t.Dispose(); }
                        _pzTimer = null;
                        if (!_pzArmed) return;
                        NativeMethods.POINT p2;
                        NativeMethods.GetCursorPos(out p2);
                        if (p2.X >= Config.PauseRectX && p2.X < Config.PauseRectX + Config.PauseRectW
                            && p2.Y >= Config.PauseRectY && p2.Y < Config.PauseRectY + Config.PauseRectH)
                        {
                            _pzArmed = false;
                            ToggleHoverEnabled();
                        }
                    };
                    _pzTimer.Start();
                }
            }
            else
            {
                _pzArmed = true;
                if (_pzTimer != null) { _pzTimer.Stop(); _pzTimer.Dispose(); _pzTimer = null; }
            }
        }

        private static void TickImpl()
        {
            // 捕获健康检查放在所有提前 return 之前：即使菜单打开/悬停已关闭也要自愈，
            // 否则一旦捕获残留，点击被路由到卡片，用户连操作入口都点不到
            ReleaseStuckCaptures();
            // 跟随系统主题：Theme=auto 时轮询系统深浅色（2s 节流，注册表读取代价可忽略），变化即级联刷新
            if (Config.Theme == "auto" && (DateTime.UtcNow - _lastSysThemeAt).TotalSeconds >= 2)
            {
                _lastSysThemeAt = DateTime.UtcNow;
                if (Config.SystemDark != _lastSysDark) ApplyThemeChange();
            }
            // 钩子同步放在所有提前 return 之前：暂停/菜单等分支也要能卸载，钩子不能常驻。
            // 鼠标钩子 = 拖拽会话中且左键仍按住（会话标志在下方更新，这里按松键兜底卸载）；
            // 键盘钩子 = 任一面板可见
            SyncMouseHook(_dragSessionActive && (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0);
            bool anyPanelVis = false;
            for (int i = _panels.Count - 1; i >= 0; i--)
            {
                var p = _panels[i];
                if (p != null && !p.IsDisposed && p.Visible) { anyPanelVis = true; break; }
            }
            SyncKeyboardHook(anyPanelVis);
            bool menuOpen = ShellMenuOpen || IsSystemMenuOpen();
            // shell 原生菜单模态期间 WM_TIMER 照常派发（Tick 仍在跑）：
            // 此期间任何 SetWindowPos 提顶都可能把卡片/面板提到菜单上面 → 菜单被盖、点击失效。
            // 菜单是同步模态，开着时不需要悬停检测与窗口维护，全部跳过
            if (menuOpen) return;
            PauseZoneTick();   // 暂停触控区：必须在 _hoverEnabled 提前返回之前（暂停态可恢复）
            if (!_hoverEnabled)
            {
                PopupForm.EnsureExtPreviewTopmost();
                return;
            }

            NativeMethods.POINT pt;
            NativeMethods.GetCursorPos(out pt);
            int mx = pt.X, my = pt.Y;
            bool mouseMoved = (mx != _lastTickX || my != _lastTickY);
            _lastTickX = mx; _lastTickY = my;

            _dragSessionActive = false;
            bool dragSession = false;
            if (Control.MouseButtons == MouseButtons.Left)
            {
                // 面板行拖出进行中：冻结 CloseAll（收面板会丢 GiveFeedback 光标反馈，用户误判拖拽已结束）
                bool dragFromPanel = _panels.Any(p => !p.IsDisposed && p.IsDragging);
                // 拖拽会话（左键按住，含 OLE 从面板行/Explorer 拖出）：光标进入触发区 → 立即弹
                // 收藏·历史列表接收拖放（无悬停延时；穿透色块窗收不到 OLE 消息，只能走光标判定）
                if (Config.EntryRectW >= 5 && Config.EntryRectH >= 5
                    && mx >= Config.EntryRectX && mx < Config.EntryRectX + Config.EntryRectW
                    && my >= Config.EntryRectY && my < Config.EntryRectY + Config.EntryRectH
                    && (_entryListForm == null || _entryListForm.IsDisposed))
                {
                    ShowEntryList(Config.EntryRectX, Config.EntryRectY + Config.EntryRectH + 4);
                    DebugLog("[entry] drag-enter rect " + mx + "," + my);
                }
                bool inExtPv = PopupForm.FilePreviewContainsPoint(mx, my);
                bool inPanelPv = _panels.Any(p => p.PreviewContainsPoint(mx, my));
                bool inPanelBody = _panels.Any(p => p.ContainsPoint(mx, my));
                dragSession = DragSession(mx, my);
                _dragSessionActive = dragSession;
                if (!dragSession)
                {
                    if (!inExtPv && !inPanelPv && !dragFromPanel)
                    {
                        PopupForm.CancelFilePreview();
                        foreach (var p in _panels) p.ClosePreviewIfAny();
                        // "离开不关闭"模式：点击面板/卡片之外 = 收起未固定面板与预览
                        // （设置窗已改非模态：不再以 _settingsOpen 短路，鼠标离开去设置窗 = 正常离开语义）
                        if (Config.KeepOnLeave && !inPanelBody
                            && !_panels.Any(p => p.MenuOpen || p.ModalOpen)
                            && !_pinCards.Any(c => !c.IsDisposed && c.IsHandleCreated && c.RectangleToScreen(c.ClientRectangle).Contains(mx, my)))
                        {
                            _extHoverFile = null;
                            _hoverFolder = null;
                            CloseAll();
                        }
                    }
                    PopupForm.EnsureExtPreviewTopmost();
                    // 左键按住期间同样保面板置顶，否则按住拖拽时被盖住的窗口无法自愈
                    foreach (var p in _panels) p.EnsureTopmost();
                    foreach (var c in _pinCards) { if (!c.IsDisposed) PopupForm.EnsureWinTopmost(c.Handle, "pincard"); }
                    Tips.EnsureTopmost();
                    return;
                }
                // 拖拽会话：关闭外部文件预览与面板内预览——拖拽源文件的预览窗会盖住拖拽目标
                // （inExtPreview 门禁会拦掉后续检测），且拖拽中不需要预览。
                // 但鼠标在预览窗内时不关：按住 ≥400ms+移动 10px 就满足拖拽会话判定，
                // 用户在预览上按住微动（点波形/长按读取）不是拖文件，关掉就是“点击关闭预览”
                if (!PopupForm.FilePreviewContainsPoint(mx, my))
                {
                    PopupForm.CancelFilePreview();
                    _extHoverFile = null;
                }
                if (!_panels.Any(p2 => p2.PreviewContainsPoint(mx, my)))
                    foreach (var p in _panels) p.ClosePreviewIfAny();
                // 拖拽会话：继续走悬停检测（弹簧式文件夹——光标停在文件夹上弹其面板，
                // 未打开列表时同样生效）；文件预览在下方抑制，置顶维护照常执行
            }

            // 预览显示期间维持 TOPMOST（不激活），防止被前台窗口盖住
            PopupForm.EnsureExtPreviewTopmost();
            // 面板同理：偶发弹出后被资源管理器窗口压住，每 Tick 重新置顶
            foreach (var p in _panels) p.EnsureTopmost();
            // 提示窗口放在最后提顶：否则会被上面这些窗口的强制提顶压住
            Tips.EnsureTopmost();

            // 鼠标在任一桌面卡片上 = 用户仍在交互（卡片弹出的列表不该被 MaybeClose 收走）
            bool inCard = _pinCards.Any(c => !c.IsDisposed && c.IsHandleCreated && c.RectangleToScreen(c.ClientRectangle).Contains(mx, my));
            bool inEntryList = _entryListForm != null && !_entryListForm.IsDisposed && _entryListForm.Visible
                && mx >= _entryListForm.Left && mx < _entryListForm.Right && my >= _entryListForm.Top && my < _entryListForm.Bottom;
            bool inPanel = _panels.Any(p => p.ContainsPoint(mx, my)) || inCard || inEntryList;
            bool inPreview = _panels.Any(p => p.PreviewContainsPoint(mx, my));
            bool inExtPreview = PopupForm.FilePreviewContainsPoint(mx, my);
            // 面板/预览显示期间也清理 Explorer tooltip：tooltips_class32 是 TOPMOST 且弹出更晚，
            // 会压在 TopMost band 顶部盖住列表/预览；鼠标进面板后下方不再检测文件，只有这里能清
            if (Config.HideTooltip && (inPanel || inPreview || inExtPreview)) HideTooltips();
            if (menuOpen || inPanel || _panels.Any(p => p.MenuOpen) || _panels.Any(p => p.ModalOpen) || _panels.Any(p => p.IsDragging) || inPreview || inExtPreview)
            {
                if (inPreview || inExtPreview) Program.DebugLog("[tick] mouse in preview, skip mx=" + mx + ",my=" + my + " inPanel=" + inPanel + " inPreview=" + inPreview + " inExtPreview=" + inExtPreview + " panels=" + _panels.Count);
                _closeStart = DateTime.MinValue;
                return;
            }

            // 前台正在输入（重命名编辑框 / 地址栏 / Explorer 搜索框）：
            // 此时任何悬停面板/预览都会打断输入并遮挡编辑框，直接短路并收起未固定的面板
            if (IsEditingInForeground())
            {
                PopupForm.CancelFilePreview();
                _extHoverFile = null;
                _hoverFolder = null;
                foreach (var p in _panels) { if (!p.IsDisposed && !p.Pinned) p.ClosePanel(); }
                _panels.RemoveAll(p => p.IsDisposed);
                return;
            }

            // 悬停抑制：Alt 临时禁用 / 修饰键门控（按住才触发）。
            // 文件夹面板与文件预览各自一套门控：只拦各自功能，未拦的一方继续检测
            bool folderSup = Config.HoverSuppressed(false);
            bool previewSup = Config.HoverSuppressed(true);
            // 门控状态翻转日志：诊断"时灵时不灵"——不灵的那一下看这里（放行/抑制）与 [hover] 目标
            bool gatePass = !(folderSup && previewSup);
            if (gatePass != _lastGatePass)
            {
                DebugLog("[gate] " + (gatePass ? "放行" : "抑制") + " @" + mx + "," + my);
                _lastGatePass = gatePass;
            }
            MaybeGateHint(mx, my);
            // 两套都拦住：跳过昂贵的悬停检测（hoverPath 保持 null），但不能提前 return——
            // 下方的关闭维护（预览 300ms 宽限关闭 + MaybeClose 面板衰减）必须照常跑，
            // 否则按门控键打开的预览/面板在松键后永远不关
            bool bothSup = folderSup && previewSup;

            string hoverPath = null;
            bool hoverIsDir = false;
            Rectangle hoverRect = Rectangle.Empty;

            // 打开的资源管理器窗口/标签 → 历史列表（2.5s 节流；RecordRecent 顶部相同跳过）
            if ((DateTime.UtcNow - _lastExplorerPathAt).TotalMilliseconds >= 2500)
            {
                _lastExplorerPathAt = DateTime.UtcNow;
                try
                {
                    foreach (var exPath in ExplorerSupport.GetOpenExplorerPaths())
                        EntryListForm.RecordRecent(exPath, false);
                }
                catch { }
            }

            // 触发区域（桌面上的入口矩形）：区域内悬停延时弹 收藏·历史 列表，并抑制桌面图标悬停
            // _entryArmed：点击行打开 Explorer 后解除武装——鼠标须先离开区域再回来才会重新弹列表，
            // 否则列表刚关（点击 Close）鼠标仍在区域内，HoverDelay 后立刻重弹，盖住刚打开的 Explorer
            bool inEntryRect = Config.EntryRectW >= 5 && Config.EntryRectH >= 5
                && mx >= Config.EntryRectX && mx < Config.EntryRectX + Config.EntryRectW
                && my >= Config.EntryRectY && my < Config.EntryRectY + Config.EntryRectH;

            if (!menuOpen)
            {
                if (inEntryRect)
                {
                    if (_entryArmed && _entryTimer == null)
                    {
                        _entryTimer = new System.Windows.Forms.Timer { Interval = Math.Max(100, Config.EntryRectDelayMs) };
                        _entryTimer.Tick += (s, e) =>
                        {
                            if (_entryTimer != null) { _entryTimer.Stop(); _entryTimer.Dispose(); }
                            _entryTimer = null;
                            if (_entryListForm == null || _entryListForm.IsDisposed)
                                ShowEntryList(Config.EntryRectX, Config.EntryRectY + Config.EntryRectH + 4);
                        };
                        _entryTimer.Start();
                    }
                }
                else
                {
                    _entryArmed = true;   // 移出区域：重新武装
                    if (_entryTimer != null && (_entryListForm == null || _entryListForm.IsDisposed))
                    {
                        _entryTimer.Stop(); _entryTimer.Dispose(); _entryTimer = null;
                    }
                }
            }

            if (!menuOpen && !inEntryRect && !bothSup)
            {
                if (NativeMethods.PointOnDesktop(mx, my))
                {
                    hoverPath = ResolveHiddenExt(DetectDesktopItem(mx, my, out hoverRect, out hoverIsDir));
                }
                else if (Config.ExplorerEnabled || Config.TcEnabled || Config.PerchEnabled)
                {
                    // TC 优先：鼠标在 TC 主窗口上时 Explorer 检测必然落空，先走 TC 免得白跑
                    if (Config.TcEnabled)
                        hoverPath = ResolveHiddenExt(TcSupport.DetectHoverItem(mx, my, out hoverRect, out hoverIsDir));
                    if (hoverPath == null && Config.ExplorerEnabled)
                        hoverPath = ResolveHiddenExt(ExplorerSupport.DetectHoverItem(mx, my, out hoverRect, out hoverIsDir));
                    if (hoverPath == null && Config.PerchEnabled)
                        hoverPath = PerchSupport.DetectHoverItem(mx, my, out hoverRect, out hoverIsDir);
                }
            }

            string hoverFolder = (hoverPath != null && hoverIsDir) ? hoverPath : null;
            // 无面板时悬停文件（图片/文本）直接预览，受"文件预览"开关控制
            string hoverFile = (hoverPath != null && !hoverIsDir && !_dragSessionActive && _panels.All(p => p.Pinned)) ? hoverPath : null;
            // 门控只拦各自功能：文件夹被拦不弹面板，预览被拦不弹预览（未拦的一方正常走）
            if (folderSup) hoverFolder = null;
            if (previewSup) hoverFile = null;
            if (hoverPath != null && Config.HideTooltip) HideTooltips();
            if (hoverPath != _lastHoverPath)
            {
                _lastHoverPath = hoverPath;
                DebugLog("[hover] " + (hoverPath == null ? "null" : (hoverIsDir ? "DIR " : "FILE ") + hoverPath) + " @" + mx + "," + my);
            }
            if (hoverFile != null)
            {
                _extNullSince = DateTime.MinValue;
                if (hoverFile != _extHoverFile)
                {
                    // 指针没动却换了目标（切标签页、列表刷新）不是用户主动悬停，先不预览
                    if (!mouseMoved)
                        DebugLog("[preview] target changed w/o mouse move, hold: " + hoverFile);
                    else
                    {
                        _extHoverFile = hoverFile;
                        PopupForm.ScheduleFilePreview(hoverFile, new Point(mx, my));
                    }
                }
            }
            else if (_extHoverFile != null)
            {
                // 桌面/资源管理器直接悬停的文件预览不受"离开不关闭"影响：鼠标离开按 300ms 正常收起
                // UIA 偶发失效（如窗口激活瞬间检测不到行）不立即关预览：宽限 300ms
                if (_extNullSince == DateTime.MinValue) _extNullSince = DateTime.UtcNow;
                else if ((DateTime.UtcNow - _extNullSince).TotalMilliseconds > 300)
                {
                    _extHoverFile = null;
                    _extNullSince = DateTime.MinValue;
                    PopupForm.ScheduleExtPreviewClose(mx, my);
                }
            }

            if (hoverFolder == null)
            {
                MaybeClose();
                return;
            }

            _closeStart = DateTime.MinValue;
            if (hoverFolder != _hoverFolder)
            {
                if (!mouseMoved)
                {
                    // 指针静止而坐标下的目标变了（切换 Explorer 标签页/目录、列表刷新），
                    // 这不是"用户把指针移上来"，不触发；一直顺延计时，鼠标动一下才重新判定
                    DebugLog("[hover-dir] target changed w/o mouse move, hold: " + hoverFolder);
                    _hoverStart = DateTime.UtcNow;
                }
                else
                {
                    DebugLog("[hover-dir] new target " + hoverFolder);
                    _hoverFolder = hoverFolder;
                    _hoverStart = DateTime.UtcNow;
                    return;
                }
            }

            if ((DateTime.UtcNow - _hoverStart).TotalSeconds >= Config.HoverDelay)
            {
                var nonPinned = _panels.Where(p => !p.Pinned).ToList();
                if (nonPinned.Count == 0 || nonPinned[0].CurrentPath != hoverFolder)
                {
                    DebugLog("[panel] opening " + hoverFolder);
                    foreach (var p in nonPinned) p.ClosePanel();
                    _panels.RemoveAll(p => !p.Pinned);
                    var p2 = new PopupForm(0, OpenSub, DropResolver, ScheduleCloseAfter);
                    if (p2.ShowPanel(hoverFolder, mx + Config.PopupOffset, my + Config.PopupOffset))
                    {
                        ShowHighlight(hoverRect);
                        _panels.Add(p2);
                        p2.Show();
                        NativeMethods.SetWindowPos(p2.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                        DebugLog("[panel] OPENED at " + p2.Left + "," + p2.Top + " size " + p2.Width + "x" + p2.Height
                            + " visible=" + p2.Visible + " topmost=" + p2.TopMost);
                        // 诊断：z 序在面板上方的窗口
                        var prev = NativeMethods.GetWindow(p2.Handle, NativeMethods.GW_HWNDPREV);
                        int cnt = 0;
                        while (prev != IntPtr.Zero && cnt < 4)
                        {
                            var sb = new System.Text.StringBuilder(256);
                            NativeMethods.GetWindowTextW(prev, sb, 256);
                            var cls = new System.Text.StringBuilder(256);
                            NativeMethods.GetClassNameW(prev, cls, 256);
                            DebugLog("[panel] above" + cnt + ": [" + sb + "] cls=" + cls);
                            prev = NativeMethods.GetWindow(prev, NativeMethods.GW_HWNDPREV);
                            cnt++;
                        }
                    }
                }
            }
        }

        // 检测桌面悬停的条目（目录或文件）；isDir 标识类型，主循环据此分流弹面板或文件预览
        // 资源管理器隐藏已知类型扩展名（.lnk/.url 等），UIA/ListView 拿到的是显示名，
        // 拼出的路径 File.Exists=false → 预览被拒。尝试补全：目录下唯一匹配 基名.* 才采用（避免歧义）
        private static string ResolveHiddenExt(string full)
        {
            if (full == null) return null;
            if (File.Exists(full) || Directory.Exists(full)) return full;
            try
            {
                string dir = Path.GetDirectoryName(full), baseName = Path.GetFileName(full);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return full;
                var hits = Directory.GetFiles(dir, baseName + ".*");
                if (hits.Length == 1) return hits[0];
            }
            catch { }
            return full;
        }

        private static string DetectDesktopItem(int mx, int my, out Rectangle rect, out bool isDir)
        {
            rect = Rectangle.Empty;
            isDir = false;
            var reader = EnsureReader();
            if (reader == null || _listview == IntPtr.Zero) return null;

            NativeMethods.RECT rc;
            NativeMethods.GetWindowRect(_listview, out rc);
            if (mx < rc.Left || mx > rc.Right || my < rc.Top || my > rc.Bottom) return null;

            int idx = reader.HitTest(mx, my);
            if (idx < 0) return null;

            string name = reader.GetItemText(idx);
            if (string.IsNullOrEmpty(name)) return null;

            string full = Path.Combine(_desktopPath, name);
            isDir = Directory.Exists(full);
            rect = reader.GetItemRect(idx);
            return full;
        }

        private static void MaybeClose()
        {
            // "离开不关闭"保护范围仅限：二级+子面板 或 面板内预览（用户在列表里展开的深层内容）；
            // 一级列表切换照常关闭；桌面/资源管理器直接悬停的文件预览（ext）始终正常关闭
            if (Config.KeepOnLeave)
            {
                bool hasDeep = _panels.Any(p => !p.IsDisposed && !p.Pinned && p.Level >= 1);
                bool hasPv = _panels.Any(p => !p.IsDisposed && p.HasInnerPreview());
                if (hasDeep || hasPv) return;
            }
            // 拖拽进行中不启动关闭计时（保险：正常拖拽按着左键不会走到这，防消息时序竞态）
            if (_panels.Any(p => !p.IsDisposed && p.IsDragging)) return;
            // 拖拽会话（含从 Explorer/桌面拖出）同样不关闭：文件夹之间移动的间隙不能收走弹簧面板
            if (_dragSessionActive) return;
            if (_closeStart == DateTime.MinValue) _closeStart = DateTime.UtcNow;
            else if ((DateTime.UtcNow - _closeStart).TotalSeconds >= Config.CloseDelay)
            {
                DebugLog("[panel] close-delay fired (hover lost " + (DateTime.UtcNow - _closeStart).TotalMilliseconds.ToString("F0") + "ms ago)");
                CloseAll();
                _closeStart = DateTime.MinValue;
                _hoverFolder = null;
            }
        }

        private static RemoteReader EnsureReader()
        {
            var lv = GetDesktopListview();
            if (lv != _listview)
            {
                _listview = lv;
                if (_reader != null) _reader.Dispose();
                try { _reader = lv != IntPtr.Zero ? new RemoteReader(lv) : null; }
                catch (Exception ex) { _reader = null; Log("[错误] RemoteReader: " + ex.Message); }
            }
            return _reader;
        }

        // ===== 门控发现提示：门控开启而用户没按键直接悬停到条目约 1.5s，光标旁轻提示一次。
        // 提示后 5 分钟内不重复；鼠标移动即重计驻留。只在实际悬停到条目上才提示（桌面空白不打扰）=====
        private static DateTime _supDwellStart = DateTime.MinValue;
        private static Point _supDwellPos;
        private static bool _supDwellArmed;
        private static DateTime _gateHintAt = DateTime.MinValue;

        private static void MaybeGateHint(int mx, int my)
        {
            if (!Config.FolderGateEnabled && !Config.PreviewGateEnabled) { _supDwellStart = DateTime.MinValue; return; }
            if (!(Config.HoverSuppressed(false) || Config.HoverSuppressed(true))) { _supDwellStart = DateTime.MinValue; return; }
            var now = DateTime.UtcNow;
            if (_supDwellStart == DateTime.MinValue || mx != _supDwellPos.X || my != _supDwellPos.Y)
            {
                _supDwellStart = now; _supDwellPos = new Point(mx, my); _supDwellArmed = false;
                return;
            }
            if (_supDwellArmed || (now - _supDwellStart).TotalMilliseconds < 1500) return;
            if ((now - _gateHintAt).TotalMinutes < 5) return;
            _supDwellArmed = true;
            Rectangle hr; bool isDir;
            string p = null;
            if (NativeMethods.PointOnDesktop(mx, my))
                p = DetectDesktopItem(mx, my, out hr, out isDir);
            else if (Config.TcEnabled)
                p = TcSupport.DetectHoverItem(mx, my, out hr, out isDir);
            else if (Config.ExplorerEnabled)
                p = ExplorerSupport.DetectHoverItem(mx, my, out hr, out isDir);
            if (string.IsNullOrEmpty(p) && Config.PerchEnabled)
                p = PerchSupport.DetectHoverItem(mx, my, out hr, out isDir);
            if (string.IsNullOrEmpty(p)) return;
            _gateHintAt = now;
            int mods = Config.FolderGateEnabled ? Config.FolderGateMods : Config.PreviewGateMods;
            Tips.Flash(string.Format(I18n.T("gate_hint"), Config.ModsLabel(mods)), 1400);
            DebugLog("[gate] hint shown @ " + mx + "," + my + " target=" + p);
        }

        private static void OpenSub(PopupForm sender, int level, string path, int rowScreenY)
        {
            DebugLog("[cascade] OpenSub 子面板 level=" + (level + 1) + " path=" + path + " 当前面板数=" + _panels.Count);
            if (_subCloseTimer != null) { _subCloseTimer.Stop(); _subCloseTimer.Dispose(); _subCloseTimer = null; }
            CloseAfter(level);
            // 定位基准是触发悬停的面板（sender）。不能用 _panels[level]：level 是级联层级
            // 不是数组索引，pinned 面板占据索引 0 时 level 与索引错位
            if (sender == null || sender.IsDisposed || !sender.IsHandleCreated) { DebugLog("[cascade] OpenSub 中止：触发面板不存在 level=" + level); return; }
            int x = sender.RightEdge() + Config.SubGap;
            int y = rowScreenY;
            // 右边缘放不下时改展开到父面板左侧，避免 ClampPos 钳回右侧与父面板重叠闪烁
            try { var _wa = Screen.GetWorkingArea(new Point(x, y)); if (x + sender.Width > _wa.Right) x = sender.Left - sender.Width - Config.SubGap; } catch { }
            var form = new PopupForm(level + 1, OpenSub, DropResolver, ScheduleCloseAfter);
            if (form.ShowPanel(path, x, y))
            {
                _panels.Add(form);
                form.Show();
                sender.SetActiveDir(path);
                EntryListForm.RecordRecent(path);   // 历史 MRU 记录
                DebugLog("[cascade] 子面板已显示 level=" + (level + 1) + " at " + form.Left + "," + form.Top + " 面板总数=" + _panels.Count);
            }
            else
            {
                DebugLog("[cascade] ShowPanel 失败（目录读取失败？） path=" + path);
                form.Dispose();
            }
        }

        private static void CloseAfter(int level)
        {
            // 按面板的 Level 属性关闭，不用数组索引：pinned 面板占据索引 0 时
            // 索引 > level 会把同层级的面板（如 pinned 面板后面新开的 level 0 列表）误当成深层级关掉
            int before = _panels.Count;
            for (int i = _panels.Count - 1; i >= 0; i--)
            {
                var p = _panels[i];
                if (p.Pinned || p.Level <= level) continue;
                _panels.RemoveAt(i);
                p.ClosePanel();
            }
            if (before != _panels.Count) DebugLog("[cascade] CloseAfter level=" + level + " 面板数 " + before + " -> " + _panels.Count);
            var parent = _panels.FirstOrDefault(p => p.Level == level && !p.IsDisposed);
            if (parent != null) parent.ClearActiveDir();
        }

        private static void CloseAll()
        {
            var toClose = _panels.Where(p => !p.Pinned).ToList();
            var toKeep = _panels.Where(p => p.Pinned).ToList();
            DebugLog("[cascade] CloseAll 关闭面板 close=" + toClose.Count + " keep(pinned)=" + toKeep.Count);
            HideHighlight();
            if (_subCloseTimer != null) { _subCloseTimer.Stop(); _subCloseTimer.Dispose(); _subCloseTimer = null; }
            foreach (var p in toClose) p.ClosePanel();
            _panels.Clear();
            _panels.AddRange(toKeep);
            MaybeTrimMemory();
        }

        // 卡片徽章悬停弹出的文件夹列表：卡片侧轮询调它收起（鼠标离开 卡片∪面板 后）
        internal static void CloseHoverPanels()
        {
            if (_panels.Any(p => !p.IsDisposed && !p.Pinned)) CloseAll();
        }

        // ===== 历史/收藏文件夹入口 =====
        private static EntryListForm _entryListForm;
        private static System.Windows.Forms.Timer _entryTimer;
        private static EntryIndicatorForm _entryIndicator;
        private static EntryCardForm _entryCardForm;   // 折叠后的常驻卡片
        private static DateTime _lastExplorerPathAt;   // 前台 Explorer 路径轮询节流

        // 窗口边角风格切换 → 重刷存活窗口的 DWM 圆角偏好并重画描边（新弹出的窗口自动用新风格）
        internal static void RefreshWindowCorners()
        {
            foreach (var p in _panels)
            {
                if (p.IsDisposed || !p.IsHandleCreated) continue;
                Win11.Apply(p.Handle, Config.IsDark, Config.Border);
                Win11.RefreshNc(p.Handle);   // 重跑 NCCALCSIZE（描边环增减）并整帧重画
                p.Invalidate(true);
            }
            foreach (var c in _pinCards)
            {
                if (c.IsDisposed || !c.IsHandleCreated) continue;
                Win11.Apply(c.Handle, Config.IsDark, Config.Border);
                Win11.ForceSquareCorners(c.Handle);   // 卡片始终直角，不跟随圆角模式
                c.Invalidate(true);
            }
            EntryListForm.NotifyChanged();
            if (_entryListForm != null && !_entryListForm.IsDisposed && _entryListForm.IsHandleCreated)
                Win11.RefreshNc(_entryListForm.Handle);
        }

        // 刷新所有桌面卡片配色（设置面板编辑卡片配色后即改即生效）
        internal static void RefreshAllCards()
        {
            foreach (var c in _pinCards)
            {
                if (c.IsDisposed || !c.IsHandleCreated) continue;
                c.ApplyPalette();
                c.Invalidate(true);
            }
            if (_entryCardForm != null && !_entryCardForm.IsDisposed && _entryCardForm.IsHandleCreated)
                _entryCardForm.ApplyPalette();
        }

        // 设置面板即改即生效：统一落盘 + 桌面色块/托盘文案刷新，按需重注册热键、刷新存活面板
        private static ToolStripMenuItem _trayExplorerItem;
        private static ToolStripMenuItem _trayPreviewItem;

        internal static void ApplySettingsLive(bool hotkeys, bool panels)
        {            Config.Save();
            UpdateEntryIndicator();
            // 设置窗改了 Explorer 悬停 / 文件预览 → 托盘勾选态跟上（程序化赋值触发
            // CheckedChanged 把同值写回 Config，无副作用）
            if (_trayExplorerItem != null) _trayExplorerItem.Checked = Config.ExplorerEnabled;
            if (_trayPreviewItem != null) _trayPreviewItem.Checked = Config.PreviewFiles;
            if (hotkeys && _hotkey != null)
            {
                _hotkey.ReRegisterCard();
                _hotkey.ReRegisterEntry();
                bool ok = _hotkey.ReRegister();
                // 仅注册失败需要提示（成功无感）
                if (!ok && (Config.HotkeyEnabled || Config.CardHotkeyEnabled || Config.EntryHotkeyEnabled))
                    MessageBox.Show(I18n.T("hotkey_reg_fail"), I18n.T("settings"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (panels)
            {
                // 排序/行布局变化 → 立即应用到所有存活面板（含级联子面板）
                foreach (var p in _panels) { if (!p.IsDisposed) p.RefreshPanel(); }
            }
        }

        // 触发区域有效 → 桌面显示/更新穿透色块；无效（w<20）→ 移除
        internal static void UpdateEntryIndicator()
        {
            UpdateEntryIndicator(new Rectangle(Config.EntryRectX, Config.EntryRectY, Config.EntryRectW, Config.EntryRectH), Config.EntryRectColor);
        }

        // 带暂存值的重载：设置面板里区域/颜色未确定前，桌面色块先按暂存值实时预览（取消时按 Config 还原）
        internal static void UpdateEntryIndicator(Rectangle rect, int argb)
        {
            bool valid = rect.Width >= 5 && rect.Height >= 5;
            if (!valid)
            {
                if (_entryIndicator != null)
                {
                    try { _entryIndicator.Close(); _entryIndicator.Dispose(); } catch { }
                    _entryIndicator = null;
                }
                return;
            }
            if (_entryIndicator == null || _entryIndicator.IsDisposed)
            {
                _entryIndicator = new EntryIndicatorForm();
                _entryIndicator.Show();
            }
            _entryIndicator.Apply(rect, argb);
        }

        private static EntryIndicatorForm _pauseIndicator;

        // 暂停触控区的桌面标识块：双色表达悬停开关状态——绿色=悬停启用、红色=已暂停，
        // 与触发区蓝色块区分；状态切换（托盘/热键/驻留触控区）时由 ToggleHoverEnabled 刷新
        internal static void UpdatePauseIndicator()
        {
            bool active = Config.PauseRectW >= 5 && Config.PauseRectH >= 5;
            if (!active)
            {
                if (_pauseIndicator != null)
                {
                    try { _pauseIndicator.Close(); _pauseIndicator.Dispose(); } catch { }
                    _pauseIndicator = null;
                }
                return;
            }
            if (_pauseIndicator == null || _pauseIndicator.IsDisposed)
            {
                _pauseIndicator = new EntryIndicatorForm();
                _pauseIndicator.Show();
            }
            int argb = _hoverEnabled ? 0x283BBE6B : 0x28D05555;
            _pauseIndicator.Apply(new Rectangle(Config.PauseRectX, Config.PauseRectY, Config.PauseRectW, Config.PauseRectH), argb);
        }

        // 弹出 收藏·历史 列表（触发区域悬停 / 全局热键 / 拖拽进入）
        internal static void ShowEntryList(int x, int y)
        {
            ShowEntryList(x, y, false);
        }

        // underCursor=true（快捷键弹出）：列表水平居中于光标、主体在鼠标下方，光标压进顶部
        // 标题带（表头 26px 拖动区）——光标在列表外会被离开判定在关闭延迟后收起
        internal static void ShowEntryList(int x, int y, bool underCursor)
        {
            CloseEntryList();
            // 面板行拖出进行中：保留源面板（保 GiveFeedback 光标反馈），只弹列表；
            // 拖放结束后鼠标不在面板上，按正常悬停逻辑收起。无拖拽时照常清场避免叠窗
            if (!_panels.Any(p => !p.IsDisposed && p.IsDragging)) CloseAll();
            var f = new EntryListForm(OpenFolderInExplorer);
            _entryListForm = f;
            try
            {
                if (underCursor)
                {
                    x -= f.Width / 2;
                    y -= 20;   // 落在标题带内，避开首行的 400ms 行悬停
                }
                var wa = Screen.GetWorkingArea(new Point(x, y));
                if (x + f.Width > wa.Right - 8) x = wa.Right - f.Width - 8;
                if (y + f.Height > wa.Bottom - 8) y = wa.Bottom - f.Height - 8;
                x = Math.Max(x, wa.Left + 4);
                y = Math.Max(y, wa.Top + 4);
            }
            catch { }
            f.Location = new Point(x, y);
            f.Show();
            NativeMethods.SetWindowPos(f.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
            _entryArmed = true;   // 主动弹出（热键/卡片点击/区域悬停）= 新会话，恢复正常武装语义
            DebugLog("[entry] list shown at " + x + "," + y);
        }

        internal static void CloseEntryList()
        {
            if (_entryTimer != null) { _entryTimer.Stop(); _entryTimer.Dispose(); _entryTimer = null; }
            if (_entryListForm != null)
            {
                try { _entryListForm.Close(); _entryListForm.Dispose(); } catch { }
                _entryListForm = null;
            }
        }

        // 列表折叠为桌面卡片：列表关闭，卡片常驻（悬停/点击再弹列表）
        internal static void CollapseEntryToCard(int x, int y)
        {
            CloseEntryList();
            if (_entryCardForm == null || _entryCardForm.IsDisposed)
            {
                _entryCardForm = new EntryCardForm(x, y);
                _entryCardForm.Show();
                DebugLog("[entry] collapsed to card at " + x + "," + y);
            }
            else
            {
                _entryCardForm.Location = new Point(x, y);
            }
        }

        internal static bool EntryCardContainsCursor()
        {
            return _entryCardForm != null && !_entryCardForm.IsDisposed && _entryCardForm.Visible
                && _entryCardForm.RectangleToScreen(_entryCardForm.ClientRectangle).Contains(Cursor.Position);
        }

        // 入口列表行悬停：行右侧弹该文件夹的级联子列表（不记 MRU，避免悬停致列表重排跳动）
        internal static void ShowEntrySubList(Point rowRightTop, string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            var exist = _panels.FirstOrDefault(p => !p.IsDisposed && !p.Pinned && p.Level == 0);
            if (exist != null && exist.CurrentPath == path) return;
            // 拖拽源面板（行拖出进行中）不能关：关掉会 Dispose 正在 DoDragDrop 的行，中断拖放
            //（与 ShowEntryList 的清场豁免同一语义）
            var nonPinned = _panels.Where(p => !p.Pinned && !p.IsDragging).ToList();
            foreach (var p in nonPinned) p.ClosePanel();
            _panels.RemoveAll(p => !p.Pinned && !p.IsDragging);
            var p2 = new PopupForm(0, OpenSub, DropResolver, ScheduleCloseAfter);
            if (p2.ShowPanel(path, rowRightTop.X + 6, rowRightTop.Y - 4))
            {
                _panels.Add(p2);
                p2.Show();
                DebugLog("[entry] sub-list opened " + path);
            }
            else p2.Dispose();
        }

        // 收藏·历史列表行拖放：文件复制（默认）/移动（Shift）到目标文件夹
        // 每文件独立后台任务（与文件列表行拖放同模式：UniqueDest 重名避让 + Beep + 刷新受影响面板）
        internal static void DropFilesIntoEntryFolder(string folder, string[] files, bool move)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || files == null || files.Length == 0) return;
            DebugLog("[entry-drop] folder=" + folder + " move=" + move + " files=" + string.Join(";", files));
            foreach (var src0 in files)
            {
                var src = src0;
                if (string.IsNullOrEmpty(src) || (!File.Exists(src) && !Directory.Exists(src))) continue;
                Task.Run(() =>
                {
                    try
                    {
                        string dst = PopupForm.UniqueDest(src, folder);
                        if (dst == src) { DebugLog("[entry-drop-skip] same path " + src); return; }
                        if (Directory.Exists(src)) { if (move) Directory.Move(src, dst); else PopupForm.CopyDir(src, dst); }
                        else { if (move) File.Move(src, dst); else File.Copy(src, dst); }
                        DebugLog("[entry-drop-ok] " + src + " -> " + dst);
                        var cur = _entryListForm;
                        if (cur != null && !cur.IsDisposed)
                            cur.BeginInvoke((MethodInvoker)delegate
                            {
                                try { System.Media.SystemSounds.Beep.Play(); } catch { }
                                RefreshPanelsShowing(move ? Path.GetDirectoryName(src) : null, folder);
                            });
                    }
                    catch (Exception ex) { DebugLog("[entry-drop-fail] " + src + " -> " + folder + " : " + ex.Message); }
                });
            }
        }

        // 收藏·历史列表点击：用资源管理器打开文件夹（不再展开 HoverExplorer 级联列表）
        internal static void OpenFolderInExplorer(string path, Point pt)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            _entryArmed = false;   // 解除区域/卡片悬停武装：须鼠标离开后再进入才重弹列表
            // 关掉行悬停弹出的子列表（未固定面板），避免残留在屏幕上盖住刚打开的 Explorer
            var nonPinned = _panels.Where(p => !p.Pinned).ToList();
            foreach (var p in nonPinned) p.ClosePanel();
            _panels.RemoveAll(p => !p.Pinned);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                EntryListForm.RecordRecent(path);
                DebugLog("[entry] folder explorer-opened " + path);
            }
            catch (Exception ex) { DebugLog("[entry] explorer open fail: " + ex.Message); }
        }

        // 从入口列表点击展开某文件夹的级联列表
        internal static void ShowFolderListAt(string path, Point pt)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            var nonPinned = _panels.Where(p => !p.Pinned).ToList();
            foreach (var p in nonPinned) p.ClosePanel();
            _panels.RemoveAll(p => !p.Pinned);
            var p2 = new PopupForm(0, OpenSub, DropResolver, ScheduleCloseAfter);
            if (p2.ShowPanel(path, pt.X, pt.Y))
            {
                _panels.Add(p2);
                p2.Show();
                EntryListForm.RecordRecent(path);
                DebugLog("[entry] folder opened " + path);
            }
            else p2.Dispose();
        }

        // 文件夹卡片徽章悬停：在卡片下方（与卡片左对齐）弹出该文件夹的列表
        internal static void ShowCardFolderList(HoverExplorer.PinCardForm card, string path)
        {
            if (card == null || card.IsDisposed || string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            var exist = _panels.FirstOrDefault(p => !p.IsDisposed && !p.Pinned && p.Level == 0);
            if (exist != null && exist.CurrentPath == path) return;   // 已开着同目录列表
            var nonPinned = _panels.Where(p => !p.Pinned).ToList();
            foreach (var p in nonPinned) p.ClosePanel();
            _panels.RemoveAll(p => !p.Pinned);
            var p2 = new PopupForm(0, OpenSub, DropResolver, ScheduleCloseAfter);
            if (p2.ShowPanel(path, card.Left, card.Bottom + 4))
            {
                _panels.Add(p2);
                p2.Show();
                EntryListForm.RecordRecent(path);
                DebugLog("[card-panel] OPENED " + path + " at " + p2.Left + "," + p2.Top);
            }
            else
            {
                p2.Dispose();
            }
        }

        private static DateTime _lastTrim;
        private static void MaybeTrimMemory()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastTrim).TotalSeconds < 10) return;
            _lastTrim = now;
            GC.Collect(0, GCCollectionMode.Optimized, false);
            try { NativeMethods.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1); } catch { }
        }

        private static HighlightOverlay _highlight;

        private static void ShowHighlight(Rectangle r)
        {
            if (r.IsEmpty) return;
            HideHighlight();
            try { _highlight = new HighlightOverlay(r); _highlight.Show(); } catch { }
        }

        private static void HideHighlight()
        {
            if (_highlight != null) { try { _highlight.Close(); _highlight.Dispose(); } catch { } _highlight = null; }
        }

        private static void ScheduleCloseAfter(int level)
        {
            if (_subCloseTimer != null) { _subCloseTimer.Stop(); _subCloseTimer.Dispose(); _subCloseTimer = null; }
            _subCloseLevel = level;
            _subCloseTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _subCloseTimer.Tick += (s, e) =>
            {
                _subCloseTimer.Stop(); _subCloseTimer.Dispose(); _subCloseTimer = null;
                // 面板行拖出进行中：级联保持（收面板会掐断拖拽会话）
                if (_panels.Any(p => !p.IsDisposed && p.IsDragging)) { DebugLog("[cascade] dragging hold, level=" + _subCloseLevel); return; }
                NativeMethods.POINT pt;
                NativeMethods.GetCursorPos(out pt);
                // 按 Level 属性判断层级，不用数组索引（pinned 面板会导致索引错位）
                bool inSub = _panels.Any(p => p.Level > _subCloseLevel && p.ContainsPoint(pt.X, pt.Y));
                if (!inSub)
                {
                    // KeepOnLeave：鼠标已离开所有面板（含面板内预览）= 离开语义 → 级联保持，等点击其他位置；
                    // 仍悬在其他面板上（一级列表内换行/切换）→ 正常关闭旧级联
                    if (Config.KeepOnLeave)
                    {
                        bool inAny = PopupForm.InAnyHoverPanel(pt.X, pt.Y)
                                  || _panels.Any(p => !p.IsDisposed && p.PreviewContainsPoint(pt.X, pt.Y));
                        if (!inAny) { DebugLog("[cascade] KeepOnLeave hold, level=" + _subCloseLevel); return; }
                    }
                    CloseAfter(_subCloseLevel);
                }
            };
            _subCloseTimer.Start();
        }

        private static string DropResolver(int x, int y)
        {
            var reader = EnsureReader();
            if (reader == null || _listview == IntPtr.Zero) return null;
            NativeMethods.RECT rect;
            NativeMethods.GetWindowRect(_listview, out rect);
            if (x < rect.Left || x > rect.Right || y < rect.Top || y > rect.Bottom) return null;
            int idx = reader.HitTest(x, y);
            if (idx < 0) return null;
            string name = reader.GetItemText(idx);
            if (string.IsNullOrEmpty(name)) return null;
            string full = Path.Combine(_desktopPath, name);
            return Directory.Exists(full) ? full : null;
        }

        private static IntPtr GetDesktopListview()
        {
            var progman = NativeMethods.FindWindow("Progman", null);
            var view = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero)
            {
                IntPtr worker = IntPtr.Zero;
                while (true)
                {
                    worker = NativeMethods.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
                    if (worker == IntPtr.Zero) break;
                    var v = NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (v != IntPtr.Zero) { view = v; break; }
                }
            }
            if (view == IntPtr.Zero) return IntPtr.Zero;
            return NativeMethods.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
        }

        private static string GetDesktopPath()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
            catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop"); }
        }

        private static Icon CreateTrayIcon()
        {
            try
            {
                var bmp = new Bitmap(32, 32);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    var folderColor = ColorTranslator.FromHtml("#2d3a5f");
                    var outline = ColorTranslator.FromHtml("#9cdcfe");
                    var dot = ColorTranslator.FromHtml("#2d7a3d");
                    using (var b = new SolidBrush(folderColor))
                    {
                        g.FillRectangle(b, 3, 5, 13, 6);
                        g.FillRectangle(b, 3, 9, 26, 18);
                    }
                    using (var p = new Pen(outline, 2))
                        g.DrawRectangle(p, 3, 9, 26, 18);
                    using (var b = new SolidBrush(dot))
                        g.FillEllipse(b, 21, 14, 6, 6);
                }
                var hicon = bmp.GetHicon();
                var icon = Icon.FromHandle(hicon);
                bmp.Dispose();
                _trayHicon = hicon;
                return icon;
            }
            catch { return SystemIcons.Application; }
        }
    }

    internal class HotkeyWindow : NativeWindow
    {
        public event Action HotkeyPressed;       // 悬停开关
        public event Action CardHotkeyPressed;   // 剪贴板→卡片
        public event Action EntryHotkeyPressed;  // 收藏·历史 列表
        private const int HoverId = 0xB001;
        private const int CardId = 0xB002;
        private const int EntryId = 0xB003;
        private bool _hoverRegistered, _cardRegistered, _entryRegistered;
        private readonly int _createdTick = Environment.TickCount;   // 启动防抖：忽略键状态残留导致的立即 WM_HOTKEY

        public HotkeyWindow()
        {
            var cp = new CreateParams { Caption = "HE_Hotkey" };
            CreateHandle(cp);
            RegisterHover();
            RegisterCard();
            RegisterEntry();
        }

        private void RegisterHover()
        {
            if (_hoverRegistered) return;
            if (!Config.HotkeyEnabled) return;
            if (Config.HotkeyModifiers == 0 && Config.HotkeyKey == 0) return;
            _hoverRegistered = NativeMethods.RegisterHotKey(Handle, HoverId, Config.HotkeyModifiers, Config.HotkeyKey);
        }

        private void RegisterCard()
        {
            if (_cardRegistered) return;
            if (!Config.CardHotkeyEnabled) return;
            if (Config.CardHotkeyModifiers == 0 && Config.CardHotkeyKey == 0) return;
            _cardRegistered = NativeMethods.RegisterHotKey(Handle, CardId, Config.CardHotkeyModifiers, Config.CardHotkeyKey);
        }

        private void RegisterEntry()
        {
            if (_entryRegistered) return;
            if (!Config.EntryHotkeyEnabled) return;
            if (Config.EntryHotkeyModifiers == 0 && Config.EntryHotkeyKey == 0) return;
            _entryRegistered = NativeMethods.RegisterHotKey(Handle, EntryId, Config.EntryHotkeyModifiers, Config.EntryHotkeyKey);
        }

        public bool ReRegister()
        {
            UnregisterHover();
            RegisterHover();
            return _hoverRegistered || !Config.HotkeyEnabled;
        }

        public bool ReRegisterCard()
        {
            UnregisterCard();
            RegisterCard();
            return _cardRegistered || !Config.CardHotkeyEnabled;
        }

        public bool ReRegisterEntry()
        {
            UnregisterEntry();
            RegisterEntry();
            return _entryRegistered || !Config.EntryHotkeyEnabled;
        }

        private void UnregisterHover()
        {
            if (_hoverRegistered) { NativeMethods.UnregisterHotKey(Handle, HoverId); _hoverRegistered = false; }
        }

        private void UnregisterCard()
        {
            if (_cardRegistered) { NativeMethods.UnregisterHotKey(Handle, CardId); _cardRegistered = false; }
        }

        private void UnregisterEntry()
        {
            if (_entryRegistered) { NativeMethods.UnregisterHotKey(Handle, EntryId); _entryRegistered = false; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                // 启动后 800ms 内忽略热键：RegisterHotKey 对仍处于按下状态的组合会立即投递 WM_HOTKEY，
                // 重启 he 时若 Ctrl+Alt 残留会误触发，把悬停关掉或误建卡片
                if (Environment.TickCount - _createdTick < 800) { base.WndProc(ref m); return; }
                int id = m.WParam.ToInt32();
                // 触发时刻的物理按键状态：三个都 true=真实按键；false=软件注入。
                // 典型病例：Ctrl/Alt 卡住（GetAsyncKeyState 残留）时正常打字按 Q 就会触发
                bool ctrlDn = (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0;
                bool altDn = (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0;
                bool qDn = (NativeMethods.GetAsyncKeyState(0x51) & 0x8000) != 0;
                NativeMethods.POINT _hp; NativeMethods.GetCursorPos(out _hp);
                Program.DebugLog("[hotkey] id=0x" + id.ToString("X") + " phys(ctrl/alt/q)=" + ctrlDn + "/" + altDn + "/" + qDn + " mouse=" + _hp.X + "," + _hp.Y);
                if (id == HoverId && HotkeyPressed != null) HotkeyPressed();
                else if (id == CardId && CardHotkeyPressed != null) CardHotkeyPressed();
                else if (id == EntryId && EntryHotkeyPressed != null) EntryHotkeyPressed();
            }
            base.WndProc(ref m);
        }

        public void Unregister()
        {
            UnregisterHover();
            UnregisterCard();
            UnregisterEntry();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // 启停状态浮层：屏幕上方居中的短暂提示条（不抢焦点，1.4s 自动消失）
    internal class StatusToastForm : Form
    {
        private readonly System.Windows.Forms.Timer _tm;

        public StatusToastForm(string text)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Config.BG;
            DoubleBuffered = true;
            var lbl = new Label
            {
                Text = text,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Config.FG,
                BackColor = Config.BG,
                Font = new Font(Config.FontName, Config.FontSize + 3f, FontStyle.Bold),
                AutoEllipsis = true,
            };
            Size sz;
            using (var g = CreateGraphics()) sz = TextRenderer.MeasureText(g, text, lbl.Font);
            lbl.Size = new Size(sz.Width + 56, sz.Height + 22);
            ClientSize = lbl.Size;
            Controls.Add(lbl);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + Math.Max(24, wa.Height / 6));
            _tm = new System.Windows.Forms.Timer { Interval = 1400 };
            _tm.Tick += (s, e) => { try { Close(); } catch { } };
            _tm.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win11.ApplyRounded(Handle, Config.IsDark);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Config.WindowCorners != 1)
                using (var p = new Pen(Config.Border, 1)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_tm != null) { _tm.Stop(); _tm.Dispose(); }
            base.OnFormClosed(e);
        }
    }
}