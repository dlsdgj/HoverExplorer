using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media.Animation;

namespace HoverExplorer.Wpf
{
    // 阶段①：用 WPF 重写设置面板的外壳（顶栏 / 侧栏 / 页面 / 深浅主题）。
    // XAML 来自 D:\下载\HESettings 原型，作为嵌入资源由 XamlReader 在运行时加载，
    // 因此无需 MSBuild/XAML 编译，也不改变主程序的 .NET Framework + csc 构建链。
    // 旧 WinForms SettingsForm 保留作为加载失败时的回退。
    internal static class WpfSettingsWindow
    {
        private const string ResPrefix = "HoverExplorer.Wpf.Xaml.";

        private static Window _win;
        private static ContentControl _pageHost;
        private static ScrollViewer _pageScroll;
        private static bool _inited;
        private static bool _dark;
        private static bool _hasPos;
        private static double _left;
        private static double _top;
        private static SettingsForm _fallbackForm;

        internal static bool IsOpen
        {
            get
            {
                if (_win != null && _win.IsVisible) return true;
                return _fallbackForm != null && !_fallbackForm.IsDisposed && _fallbackForm.Visible;
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static string ReadXaml(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResPrefix + name))
            {
                if (s == null) throw new InvalidOperationException("缺少嵌入资源 " + ResPrefix + name);
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    string text = sr.ReadToEnd();
                    // XAML 里 clr-namespace 的 assembly 用占位名；运行时换成实际程序集名，
                    // 这样测试宿主/改名后的 exe 同样能解析自定义控件类型
                    return text.Replace("assembly=HoverExplorer",
                        "assembly=" + Assembly.GetExecutingAssembly().GetName().Name);
                }
            }
        }

        private static ResourceDictionary LoadDict(string name)
        {
            return (ResourceDictionary)XamlReader.Parse(ReadXaml(name));
        }

        // 索引 0 固定为主题字典，切换深浅色时只替换它，所有 DynamicResource 自动刷新
        private static void Ensure()
        {
            if (_inited) return;
            MediaPreview.EnsureWpf();
            Application app = Application.Current;
            if (app == null) throw new InvalidOperationException("WPF Application 未初始化");
            _dark = Config.IsDark;
            app.Resources.MergedDictionaries.Add(LoadDict(_dark ? "Dark.xaml" : "Light.xaml"));
            app.Resources.MergedDictionaries.Add(LoadDict("Styles.xaml"));
            app.Resources.MergedDictionaries.Add(LoadDict("Pages1.xaml"));
            app.Resources.MergedDictionaries.Add(LoadDict("Pages2.xaml"));
            _inited = true;
        }

        internal static void Show()
        {
            try
            {
                ShowCore();
            }
            catch (Exception ex)
            {
                // 阶段①安全网：WPF 资源/加载异常时回退到旧 WinForms 设置窗，功能不中断
                try { System.Diagnostics.Debug.WriteLine("[WpfSettings] " + ex); } catch { }
                _win = null;
                _pageHost = null;
                _pageScroll = null;
                if (_fallbackForm != null && !_fallbackForm.IsDisposed) { _fallbackForm.Activate(); return; }
                _fallbackForm = new SettingsForm();
                _fallbackForm.FormClosed += delegate { _fallbackForm = null; };
                _fallbackForm.Show();
            }
        }

        private static void ShowCore()
        {
            Ensure();
            if (IsOpen) { Activate(); return; }

            _win = (Window)XamlReader.Parse(ReadXaml("Shell.xaml"));
            _win.Title = "HoverExplorer v" + Program.Version + " " + I18n.T("settings");
            Cfg.Owner = _win;

            _pageHost = FindChild<ContentControl>(_win, "PageHost");
            _pageScroll = FindChild<ScrollViewer>(_win, "PageScroll");

            TextBlock title = FindChild<TextBlock>(_win, "TitleText");
            if (title != null) title.Text = _win.Title;

            Button themeBtn = FindChild<Button>(_win, "ThemeButton");
            if (themeBtn != null)
            {
                themeBtn.Content = "◐ " + I18n.T("theme_dark") + "/" + I18n.T("theme_light");
                themeBtn.Click += ThemeClick;
            }
            foreach (RadioButton rb in FindAll<RadioButton>(_win))
                rb.Click += NavClick;

            _win.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(_win).Handle;
                int dark = _dark ? 1 : 0;
                DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
            };
            _win.Closed += delegate
            {
                SavePos();
                _win = null;
                _pageHost = null;
                _pageScroll = null;
            };
            if (_hasPos)
            {
                _win.WindowStartupLocation = WindowStartupLocation.Manual;
                _win.Left = _left;
                _win.Top = _top;
            }

            try { System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_win); } catch { }
            _win.Show();
            _win.Activate();
            // 窗口显示后视觉树才完整：本地化导航项、填充语言下拉，再绑定首屏页面
            // （首屏必须在 Show 之后绑定，否则 DataTemplate 内容尚未生成，SettingRow 文案不会写入）
            Cfg.AttachShell(_win);
            ShowPage("TriggerPage");
        }

        private static void ShowPage(string key)
        {
            if (_win == null || _pageHost == null) return;
            DataTemplate dt = _win.TryFindResource(key) as DataTemplate;
            if (dt == null) return;
            _pageHost.ContentTemplate = dt;
            _pageHost.Content = new object();
            if (_pageScroll != null) _pageScroll.ScrollToTop();
            // 阶段②：模板生成后把页面控件接到 Config / I18n / 动作
            Cfg.Attach(_pageHost);
            _pageHost.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160))));
        }

        private static void NavClick(object sender, RoutedEventArgs e)
        {
            RadioButton rb = sender as RadioButton;
            if (rb == null) return;
            string key = rb.Tag as string;
            if (!string.IsNullOrEmpty(key)) ShowPage(key);
        }

        private static void ThemeClick(object sender, RoutedEventArgs e)
        {
            _dark = !_dark;
            Application.Current.Resources.MergedDictionaries[0] = LoadDict(_dark ? "Dark.xaml" : "Light.xaml");
            IntPtr h = _win == null ? IntPtr.Zero : new WindowInteropHelper(_win).Handle;
            if (h != IntPtr.Zero)
            {
                int dark = _dark ? 1 : 0;
                DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));
            }
            Config.Theme = _dark ? "dark" : "light";
            Program.ApplyThemeChange();
        }

        // 托盘菜单/系统自动切换主题时，同步 WPF 资源字典与标题栏深色
        internal static void SyncTheme()
        {
            if (!_inited || _win == null) return;
            bool dark = Config.IsDark;
            if (dark == _dark) return;
            _dark = dark;
            try
            {
                Application.Current.Resources.MergedDictionaries[0] = LoadDict(_dark ? "Dark.xaml" : "Light.xaml");
                IntPtr h = new WindowInteropHelper(_win).Handle;
                if (h != IntPtr.Zero)
                {
                    int d = _dark ? 1 : 0;
                    DwmSetWindowAttribute(h, 20, ref d, sizeof(int));
                }
            }
            catch { }
        }

        internal static void Activate()
        {
            if (_win != null)
            {
                if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
                _win.Show();
                _win.Activate();
            }
            else if (_fallbackForm != null && !_fallbackForm.IsDisposed)
            {
                _fallbackForm.Show();
                _fallbackForm.Activate();
            }
        }

        internal static void Close()
        {
            if (_win != null) _win.Close();
            else if (_fallbackForm != null && !_fallbackForm.IsDisposed) _fallbackForm.Close();
        }

        // 语言切换时重建窗口（阶段②接 I18n 后页面文案也会随之切换）
        internal static void Rebuild()
        {
            if (_win == null || !_win.IsVisible) return;
            SavePos();
            _win.Close();
            Show();
        }

        // 托盘菜单状态变化：回读 Config 到当前页面（模式下拉、开关等）
        internal static void SyncFromTray()
        {
            if (_fallbackForm != null && !_fallbackForm.IsDisposed) _fallbackForm.SyncFromTray();
            if (_win != null) Cfg.Refresh();
        }

        private static void SavePos()
        {
            if (_win == null) return;
            _left = _win.Left;
            _top = _win.Top;
            _hasPos = true;
        }

        private static T FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root == null) return null;
            foreach (object o in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject c = o as DependencyObject;
                if (c == null) continue;
                T hit = c as T;
                if (hit != null && hit.Name == name) return hit;
                T sub = FindChild<T>(c, name);
                if (sub != null) return sub;
            }
            return null;
        }

        private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            foreach (object o in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject c = o as DependencyObject;
                if (c == null) continue;
                T hit = c as T;
                if (hit != null) yield return hit;
                foreach (T sub in FindAll<T>(c)) yield return sub;
            }
        }
    }
}
