using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace HoverExplorer.Wpf
{
    // 阶段②：松散 XAML 没有 x:Class / 代码后置，用附加属性做声明式绑定。
    //   c:Cfg.Key     → Config 字段名或虚拟键（pal:xxx / card:xxx / HkMain0 ...）
    //   c:Cfg.I18n    → I18n 键，替换标题/说明/按钮/导航文案
    //   c:Cfg.Desc    → SettingRow.Description 的 I18n 键
    //   c:Cfg.Action  → 按钮/色块动作名
    //   c:Cfg.Link    → Hyperlink 目标（http 链接或动作名）
    //   c:Cfg.Scale   → 控件值与 Config 值的倍率（秒 ↔ 毫秒用 1000）
    //   c:Cfg.Mode    → ComboBox 特殊模式：Fonts / Sort
    public static class Cfg
    {
        public static readonly DependencyProperty KeyProperty =
            DependencyProperty.RegisterAttached("Key", typeof(string), typeof(Cfg), new PropertyMetadata(null));
        public static string GetKey(DependencyObject d) { return d.GetValue(KeyProperty) as string; }
        public static void SetKey(DependencyObject d, string v) { d.SetValue(KeyProperty, v); }

        public static readonly DependencyProperty I18nProperty =
            DependencyProperty.RegisterAttached("I18n", typeof(string), typeof(Cfg), new PropertyMetadata(null));
        public static string GetI18n(DependencyObject d) { return d.GetValue(I18nProperty) as string; }
        public static void SetI18n(DependencyObject d, string v) { d.SetValue(I18nProperty, v); }

        public static readonly DependencyProperty DescProperty =
            DependencyProperty.RegisterAttached("Desc", typeof(string), typeof(Cfg), new PropertyMetadata(null));
        public static string GetDesc(DependencyObject d) { return d.GetValue(DescProperty) as string; }
        public static void SetDesc(DependencyObject d, string v) { d.SetValue(DescProperty, v); }

        public static readonly DependencyProperty ActionProperty =
            DependencyProperty.RegisterAttached("Action", typeof(string), typeof(Cfg), new PropertyMetadata(null));
        public static string GetAction(DependencyObject d) { return d.GetValue(ActionProperty) as string; }
        public static void SetAction(DependencyObject d, string v) { d.SetValue(ActionProperty, v); }

        public static readonly DependencyProperty LinkProperty =
            DependencyProperty.RegisterAttached("Link", typeof(string), typeof(Cfg), new PropertyMetadata(null));
        public static string GetLink(DependencyObject d) { return d.GetValue(LinkProperty) as string; }
        public static void SetLink(DependencyObject d, string v) { d.SetValue(LinkProperty, v); }

        public static readonly DependencyProperty ScaleProperty =
            DependencyProperty.RegisterAttached("Scale", typeof(double), typeof(Cfg), new PropertyMetadata(1.0));
        public static double GetScale(DependencyObject d) { return (double)d.GetValue(ScaleProperty); }
        public static void SetScale(DependencyObject d, double v) { d.SetValue(ScaleProperty, v); }

        public static readonly DependencyProperty ModeProperty =
            DependencyProperty.RegisterAttached("Mode", typeof(string), typeof(Cfg), new PropertyMetadata(""));
        public static string GetMode(DependencyObject d) { return d.GetValue(ModeProperty) as string; }
        public static void SetMode(DependencyObject d, string v) { d.SetValue(ModeProperty, v); }

        // 条件显隐：元素在此条件不满足时 Collapsed（依赖的“子选项”在父开关/父区域关闭时不显示）
        public static readonly DependencyProperty ShowIfProperty =
            DependencyProperty.RegisterAttached("ShowIf", typeof(string), typeof(Cfg), new PropertyMetadata(null));
        public static string GetShowIf(DependencyObject d) { return d.GetValue(ShowIfProperty) as string; }
        public static void SetShowIf(DependencyObject d, string v) { d.SetValue(ShowIfProperty, v); }

        // 从属项标记：当元素带有 ShowIf 时自动置 true，模板据此渲染左侧强调色条 + 浅色底，
        // 提示该选项是“上层开关开启才出现”的子选项。
        public static readonly DependencyProperty SubordinateProperty =
            DependencyProperty.RegisterAttached("Subordinate", typeof(bool), typeof(Cfg), new PropertyMetadata(false));
        public static bool GetSubordinate(DependencyObject d) { return (bool)(d.GetValue(SubordinateProperty) ?? false); }
        public static void SetSubordinate(DependencyObject d, bool v) { d.SetValue(SubordinateProperty, v); }

        private sealed class Entry
        {
            public DependencyObject D;
            public string Key;
            public double Scale;
            public string Mode;
            public DependencyPropertyDescriptor Descriptor;
            public EventHandler Handler;
        }

        private sealed class Display
        {
            public DependencyObject D;
            public string Key;
        }

        private sealed class ShowIfEntry
        {
            public DependencyObject D;
            public string Cond;
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static readonly List<Display> _displays = new List<Display>();
        private static readonly List<ShowIfEntry> _showIfs = new List<ShowIfEntry>();
        private static bool _loading;
        private static bool _skipPageHost;
        private static FrameworkElement _root;
        // 历史·收藏页的触发区色块：跟随 Config.EntryRectColor 显示桌面色块的实际颜色
        private static Border _areaSwatch;

        internal static Window Owner;

        private static List<string> _systemFonts;
        private static List<string> SystemFonts()
        {
            if (_systemFonts == null)
            {
                List<string> list = new List<string>();
                try
                {
                    using (InstalledFontCollection col = new InstalledFontCollection())
                        foreach (System.Drawing.FontFamily f in col.Families)
                            if (!string.IsNullOrEmpty(f.Name)) list.Add(f.Name);
                }
                catch { }
                list.Sort();
                if (list.Count == 0) list.Add("Microsoft YaHei UI");
                _systemFonts = list;
            }
            return _systemFonts;
        }

        // ===== 页面装载 / 刷新 =====

        internal static void Attach(FrameworkElement root)
        {
            foreach (Entry old in _entries)
            {
                if (old.Descriptor == null || old.Handler == null) continue;
                try { old.Descriptor.RemoveValueChanged(old.D, old.Handler); } catch { }
            }
            _entries.Clear();
            _displays.Clear();
            _showIfs.Clear();
            _root = root;
            _areaSwatch = null;
            if (root == null) return;
            root.ApplyTemplate();
            root.UpdateLayout();
            Walk(root);
        }

        // 外壳（导航本地化 / 语言下拉）：窗口 Show 之后视觉树才生成，且不能重复挂页面事件
        internal static void AttachShell(FrameworkElement shell)
        {
            if (shell == null) return;
            shell.ApplyTemplate();
            shell.UpdateLayout();
            _skipPageHost = true;
            try { Walk(shell); }
            finally { _skipPageHost = false; }
        }

        // 托盘菜单改了门控/开关后回读：只刷新控件显示，不重挂事件
        internal static void Refresh()
        {
            if (_root == null) return;
            _loading = true;
            try
            {
                foreach (Entry e in _entries) SetControl(e.D, e.Key, e.Scale, e.Mode);
                RefreshDisplays();
                SyncAreaSwatch(_areaSwatch);
            }
            finally { _loading = false; }
        }

        private static void RefreshDisplays()
        {
            foreach (Display dp in _displays)
            {
                try { SetDisplay(dp); } catch { }
            }
            RefreshShowIfs();
        }

        private static void RefreshShowIfs()
        {
            foreach (ShowIfEntry se in _showIfs)
            {
                try { ApplyShowIf(se.D, se.Cond); } catch { }
            }
        }

        private static void ApplyShowIf(DependencyObject d, string cond)
        {
            FrameworkElement fe = d as FrameworkElement;
            if (fe == null) return;
            fe.Visibility = EvalCond(cond) ? Visibility.Visible : Visibility.Collapsed;
        }

        // 条件显隐的布尔判定；未知条件一律显示（安全默认）
        private static bool EvalCond(string cond)
        {
            switch (cond)
            {
                case "PauseRectSet": return Config.PauseRectW >= 5 && Config.PauseRectH >= 5;
                case "EntryRectSet": return Config.EntryRectW >= 5 && Config.EntryRectH >= 5;
                case "LongPressOn": return (Config.ExplorerEnabled && Config.FolderGateEnabled && Config.FolderGateMods == 8)
                                     || (Config.PreviewFiles && Config.PreviewGateEnabled && Config.PreviewGateMods == 8);
                case "VideoDelayOn": return Config.VideoPreviewDelayEnabled;
                case "VideoOn": return Config.PreviewVideo;
                case "InfoBarOn": return Config.PreviewInfoBar;
                case "EdgeOn": return Config.PreviewEdgeMode != 0;
                case "WaveformOn": return Config.WaveformEnabled;
                case "AudioPreviewOn": return Config.PreviewAudio;
                // 频谱是波形图关闭时的替代显示（见 PopupForm.RefreshAudioViz），故仅在波形图关时可配
                case "SpectrumVisible": return Config.PreviewAudio && !Config.WaveformEnabled;
                case "HotkeyOn": return Config.HotkeyEnabled;
                case "CardHotkeyOn": return Config.CardHotkeyEnabled;
                case "EntryHotkeyOn": return Config.EntryHotkeyEnabled;
                default: return true;
            }
        }

        private static void Walk(DependencyObject d)
        {
            if (d == null) return;
            FrameworkElement skipCheck = d as FrameworkElement;
            if (_skipPageHost && skipCheck != null && skipCheck.Name == "PageHost") return;
            // 确保控件自身模板已生成（否则自定义控件内部元素不在视觉树中）
            if (skipCheck != null) skipCheck.ApplyTemplate();

            string i18n = GetI18n(d);
            if (!string.IsNullOrEmpty(i18n)) ApplyText(d, I18n.T(i18n), false);
            string desc = GetDesc(d);
            if (!string.IsNullOrEmpty(desc)) ApplyText(d, I18n.T(desc), true);

            string key = GetKey(d);
            if (!string.IsNullOrEmpty(key)) Bind(d, key);

            string action = GetAction(d);
            if (!string.IsNullOrEmpty(action)) AttachAction(d, action);

            string link = GetLink(d);
            if (!string.IsNullOrEmpty(link)) AttachAction(d, link);

            string showIf = GetShowIf(d);
            if (!string.IsNullOrEmpty(showIf))
            {
                ShowIfEntry se = new ShowIfEntry();
                se.D = d;
                se.Cond = showIf;
                _showIfs.Add(se);
                SetSubordinate(d, true);
                // 从属整行左缩进，配合强调色条形成层级感
                SettingRow subRow = d as SettingRow;
                if (subRow != null) subRow.Margin = new Thickness(22, 0, 0, 0);
                ApplyShowIf(d, showIf);
            }

            FrameworkElement fe = d as FrameworkElement;
            if (fe != null && !string.IsNullOrEmpty(fe.Name))
            {
                if (fe.Name == "LangCombo") FillLanguages(fe as ComboBox);
                else if (fe.Name == "TrayList") FillTrayList(fe as ItemsControl);
                else if (fe.Name == "ListSchemeCombo") FillListSchemes(fe as ComboBox);
                else if (fe.Name == "CardSchemeCombo") FillCardSchemes(fe as ComboBox);
                else if (fe.Name == "QrBox") FillQr(fe as Border);
                else if (fe.Name == "AreaColorSwatch") { _areaSwatch = fe as Border; SyncAreaSwatch(_areaSwatch); }
            }

            // ComboBoxItem 是逻辑子项，关闭状态下不一定在视觉树里
            ItemsControl ic = d as ItemsControl;
            if (ic != null)
            {
                foreach (object o in ic.Items)
                {
                    DependencyObject child = o as DependencyObject;
                    if (child == null) continue;
                    string ck = GetI18n(child);
                    if (!string.IsNullOrEmpty(ck)) ApplyText(child, I18n.T(ck), false);
                }
            }

            TextBlock tb = d as TextBlock;
            if (tb != null) WalkInlines(tb.Inlines);

            Visual vis = d as Visual;
            if (vis != null)
            {
                int n = VisualTreeHelper.GetChildrenCount(vis);
                for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(vis, i));
            }
        }

        // 触发区色块：显示与桌面指示层相同的 ARGB（RGB 取配置色、alpha 作为叠加透明度），
        // 而不是 XAML 里写死的示例色；点击换色后由 AreaAction 同步刷新。
        private static void SyncAreaSwatch(Border bd)
        {
            if (bd == null) return;
            int argb = Config.EntryRectColor;
            bd.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }

        private static void WalkInlines(InlineCollection col)
        {
            // 先快照再改内容：Hyperlink.Inlines 变化会让父 TextBlock 的 InlineCollection 枚举器失效
            List<Hyperlink> links = new List<Hyperlink>();
            List<Span> spans = new List<Span>();
            foreach (Inline inl in col)
            {
                Hyperlink h = inl as Hyperlink;
                if (h != null) links.Add(h);
                Span sp = inl as Span;
                if (sp != null) spans.Add(sp);
            }
            foreach (Hyperlink h in links)
            {
                string ik = GetI18n(h);
                if (!string.IsNullOrEmpty(ik))
                {
                    string text = I18n.T(ik);
                    Run r = h.Inlines.FirstInline as Run;
                    if (r != null) r.Text = text;
                    else { h.Inlines.Clear(); h.Inlines.Add(new Run(text)); }
                }
                string lk = GetLink(h);
                if (!string.IsNullOrEmpty(lk))
                {
                    try { h.NavigateUri = new Uri(lk.StartsWith("http") ? lk : "about:blank"); }
                    catch { }
                    h.RequestNavigate += delegate(object s, RequestNavigateEventArgs e)
                    {
                        e.Handled = true;
                        RunAction(lk);
                    };
                }
            }
            foreach (Span sp in spans) WalkInlines(sp.Inlines);
        }

        private static void ApplyText(DependencyObject d, string text, bool isDesc)
        {
            SettingRow row = d as SettingRow;
            if (row != null) { if (isDesc) row.Description = text; else row.Title = text; return; }
            ColorRow cr = d as ColorRow;
            if (cr != null) { if (!isDesc) cr.Label = text; return; }
            TextBlock tb = d as TextBlock;
            if (tb != null) { if (!isDesc) tb.Text = text; return; }
            HeaderedContentControl hc = d as HeaderedContentControl;
            if (hc != null) { if (!isDesc) hc.Header = text; return; }
            ContentControl cc = d as ContentControl;
            if (cc != null) { if (!isDesc) cc.Content = text; return; }
        }

        // ===== 控件 ↔ Config =====

        private static void Bind(DependencyObject d, string key)
        {
            if (d is TextBlock || d is SettingRow)
            {
                Display dp = new Display(); dp.D = d; dp.Key = key;
                _displays.Add(dp);
                SetDisplay(dp);
                return;
            }

            double scale = GetScale(d);
            if (scale <= 0) scale = 1.0;
            string mode = GetMode(d) ?? "";
            Entry e = new Entry(); e.D = d; e.Key = key; e.Scale = scale; e.Mode = mode;
            _entries.Add(e);

            ComboBox cb = d as ComboBox;
            if (cb != null) PrepareCombo(cb, mode);

            _loading = true;
            try { SetControl(d, key, scale, mode); }
            finally { _loading = false; }
            Hook(e);
        }

        private static void SetDisplay(Display dp)
        {
            string s = ToStr(Read(dp.Key));
            SettingRow row = dp.D as SettingRow;
            if (row != null) { row.Description = s; return; }
            TextBlock tb = dp.D as TextBlock;
            if (tb != null) tb.Text = s;
        }

        private static void SetControl(DependencyObject d, string key, double scale, string mode)
        {
            object v = Read(key);
            if (v == null) return;
            if (scale <= 0) scale = 1.0;

            ToggleButton t = d as ToggleButton;
            if (t != null) { t.IsChecked = ToBool(v); return; }
            ComboBox cb = d as ComboBox;
            if (cb != null)
            {
                if (mode == "Fonts")
                {
                    int idx = cb.Items.IndexOf(ToStr(v));
                    cb.SelectedIndex = idx >= 0 ? idx : 0;
                }
                else cb.SelectedIndex = ToInt(v);
                return;
            }
            TextBox tb = d as TextBox;
            if (tb != null) { tb.Text = ToStr(v); return; }
            Stepper st = d as Stepper;
            if (st != null) { st.Value = ToDbl(v) * scale; return; }
            ColorRow cr = d as ColorRow;
            if (cr != null) { cr.Text = ToStr(v); return; }
        }

        private static void Hook(Entry entry)
        {
            DependencyObject d = entry.D;
            string key = entry.Key;
            double scale = entry.Scale;
            string mode = entry.Mode;

            ToggleButton t = d as ToggleButton;
            if (t != null)
            {
                t.Click += delegate { if (!_loading) Commit(key, t.IsChecked == true); };
                return;
            }
            ComboBox cb = d as ComboBox;
            if (cb != null)
            {
                cb.SelectionChanged += delegate
                {
                    if (_loading) return;
                    object v = mode == "Fonts" ? (object)(cb.SelectedItem as string) : (object)cb.SelectedIndex;
                    Commit(key, v);
                };
                // 收起状态下滚轮应滚动页面，而不是切换选中项（避免误改配色方案等）
                cb.PreviewMouseWheel += delegate(object s, MouseWheelEventArgs e)
                {
                    if (cb.IsDropDownOpen) return;
                    e.Handled = true;
                    ScrollViewer sv = FindVisualAncestor<ScrollViewer>(cb);
                    if (sv != null) sv.ScrollToVerticalOffset(sv.VerticalOffset + (e.Delta > 0 ? -32 : 32));
                };
                return;
            }
            TextBox tb = d as TextBox;
            if (tb != null)
            {
                tb.LostFocus += delegate { if (!_loading) Commit(key, tb.Text); };
                tb.KeyDown += delegate(object s, KeyEventArgs ke)
                {
                    if (!_loading && ke.Key == Key.Enter) Commit(key, tb.Text);
                };
                return;
            }
            Stepper st = d as Stepper;
            if (st != null)
            {
                DependencyPropertyDescriptor dp = DependencyPropertyDescriptor.FromProperty(Stepper.ValueProperty, typeof(Stepper));
                EventHandler h = delegate { if (!_loading) Commit(key, st.Value / scale); };
                dp.AddValueChanged(st, h);
                entry.Descriptor = dp;
                entry.Handler = h;
                return;
            }
            ColorRow cr = d as ColorRow;
            if (cr != null)
            {
                DependencyPropertyDescriptor dp = DependencyPropertyDescriptor.FromProperty(ColorRow.TextProperty, typeof(ColorRow));
                EventHandler h = delegate { if (!_loading) Commit(key, cr.Text); };
                dp.AddValueChanged(cr, h);
                entry.Descriptor = dp;
                entry.Handler = h;
            }
        }

        private static void Commit(string key, object v)
        {
            try
            {
                Write(key, v);
                RefreshDisplays();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WpfSettings:Cfg] " + ex);
            }
        }

        private static void PrepareCombo(ComboBox cb, string mode)
        {
            if (mode == "Fonts")
            {
                cb.Items.Clear();
                string cur = ToStr(Read(GetKey(cb)));
                foreach (string f in SystemFonts()) cb.Items.Add(f);
                int idx = string.IsNullOrEmpty(cur) ? 0 : cb.Items.IndexOf(cur);
                if (idx < 0) { cb.Items.Insert(0, cur); idx = 0; }
                cb.SelectedIndex = idx;
            }
            else if (mode == "Sort")
            {
                cb.Items.Clear();
                string[] keys = { "sort_name", "sort_size", "sort_date" };
                foreach (string k in keys) { cb.Items.Add(I18n.T(k) + " ↑"); cb.Items.Add(I18n.T(k) + " ↓"); }
            }
        }

        // ===== 虚拟键读 =====

        private static object Read(string key)
        {
            switch (key)
            {
                case "VersionText": return "v" + Program.Version;
                case "Language": return Config.Language;
                case "Theme": return Config.Theme;
                case "ExplorerMode": return ModeFromConfig(Config.ExplorerEnabled, Config.FolderGateEnabled, Config.FolderGateMods);
                case "PreviewMode": return ModeFromConfig(Config.PreviewFiles, Config.PreviewGateEnabled, Config.PreviewGateMods);
                case "SortIndex": return SortIndex();
                case "EntryRectInfo": return AreaInfo(Config.EntryRectX, Config.EntryRectY, Config.EntryRectW, Config.EntryRectH);
                case "PauseRectInfo": return AreaInfo(Config.PauseRectX, Config.PauseRectY, Config.PauseRectW, Config.PauseRectH);
                case "FfmpegStatus":
                    {
                        string exe = FfmpegTools.FindFfmpeg();
                        return string.IsNullOrEmpty(exe) ? I18n.T("ffmpeg_status_missing") : I18n.T("ffmpeg_found", exe);
                    }
            }
            if (key.StartsWith("Hk"))
            {
                string[] parts = key.Split(':');
                if (parts.Length == 2)
                {
                    string[] labels = HotkeyLabels(parts[0]);
                    int i;
                    if (int.TryParse(parts[1], out i) && i >= 0 && i < labels.Length) return labels[i];
                }
                return "";
            }
            if (key.StartsWith("pal:"))
            {
                Dictionary<string, System.Drawing.Color> map = Config.ParsePalette(Config.ListPalette);
                System.Drawing.Color c;
                if (map.TryGetValue(key.Substring(4), out c)) return HexOf(c);
                return "";
            }
            if (key.StartsWith("card:")) return ReadCard(key.Substring(5));

            FieldInfo f = typeof(Config).GetField(key, BindingFlags.Public | BindingFlags.Static);
            return f == null ? null : f.GetValue(null);
        }

        private static object ReadCard(string part)
        {
            CardPalette p = CardPalettes.Current;
            switch (part)
            {
                case "Bg": return CardPalette.ToRgba(p.Bg);
                case "Fg": return CardPalette.ToRgba(p.Fg);
                case "BadgeBg": return CardPalette.ToRgba(p.BadgeBg);
                case "BadgeFg": return CardPalette.ToRgba(p.BadgeFg);
                case "Strip": return CardPalette.ToRgba(p.StripColor);
                case "StripWidth": return p.StripWidth;
                case "FontName": return p.FontName;
                case "FontSize": return (double)p.FontSize;
                case "Radius": return p.Radius;
            }
            return null;
        }

        // ===== 写回 + 级联刷新 =====

        internal static void Write(string key, object v)
        {
            switch (key)
            {
                case "Language":
                    {
                        string code = ToStr(v);
                        if (string.IsNullOrEmpty(code)) return;
                        Config.Language = code;
                        I18n.Lang = code;
                        Config.RefreshSortLabels();
                        Config.Save();
                        Program.RebuildTrayMenu();
                        Application.Current.Dispatcher.BeginInvoke(new Action(delegate { WpfSettingsWindow.Rebuild(); }));
                        return;
                    }
                case "ExplorerMode": ApplyMode(true, ToInt(v)); Program.ApplySettingsLive(false, false); return;
                case "PreviewMode": ApplyMode(false, ToInt(v)); Program.ApplySettingsLive(false, false); return;
                case "SortIndex":
                    {
                        int idx = ToInt(v);
                        Config.SortMode = idx / 2 == 1 ? "size" : (idx / 2 == 2 ? "date" : "name");
                        Config.SortDesc = (idx % 2 == 1);
                        Program.ApplySettingsLive(false, true);
                        return;
                    }
            }
            if (key.StartsWith("pal:")) { UpdateListPalette(); return; }
            if (key.StartsWith("card:")) { UpdateCardPalette(key.Substring(5), v); return; }

            FieldInfo f = typeof(Config).GetField(key, BindingFlags.Public | BindingFlags.Static);
            if (f == null) return;
            f.SetValue(null, ConvertTo(f.FieldType, v));

            if (key == "FfmpegPath")
            {
                FfmpegTools.ResetCache();
                Program.ApplySettingsLive(false, false);
                return;
            }
            if (key == "TrayIconPath")
            {
                Config.Save();
                Program.ApplyTrayIcon();
                return;
            }
            if (key == "Theme") { Program.ApplyThemeChange(); return; }
            if (key.StartsWith("Tray") || IsTrayEnable(key))
            {
                Program.ApplySettingsLive(false, false);
                Program.RebuildTrayMenu();
                if (key == "HideTrayIcon") Program.ApplyHideTrayIcon();
                if (key == "ShowTips") { Tips.ApplyConfig(); Program.RefreshAllPanels(); }
                return;
            }
            Program.ApplySettingsLive(IsHotkey(key), NeedsPanels(key));
            if (key.StartsWith("List") && key.IndexOf("Font") >= 0) Program.RefreshAllPanels();
        }

        private static bool IsTrayEnable(string key)
        {
            switch (key)
            {
                case "ExplorerEnabled":
                case "PreviewFiles":
                case "PreviewOriginalSize":
                case "DebugLog":
                case "HideTooltip":
                case "ShowTips":
                case "HideTrayIcon": return true;
            }
            return false;
        }

        private static bool IsHotkey(string key) { return key.IndexOf("Hotkey") >= 0; }

        private static bool NeedsPanels(string key)
        {
            switch (key)
            {
                case "SortIndex":
                case "RowSwapSides":
                case "StatusFilter": return true;
            }
            return false;
        }

        private static object ConvertTo(Type t, object v)
        {
            if (t == typeof(bool)) return ToBool(v);
            if (t == typeof(int)) return ToInt(v);
            if (t == typeof(double)) return ToDbl(v);
            if (t == typeof(float)) return (float)ToDbl(v);
            if (t == typeof(string)) return ToStr(v) ?? "";
            return v;
        }

        private static bool ToBool(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            if (v is bool?) return ((bool?)v).GetValueOrDefault();
            double d;
            if (double.TryParse(ToStr(v), out d)) return d != 0;
            return false;
        }

        private static int ToInt(object v)
        {
            if (v == null) return 0;
            if (v is int) return (int)v;
            if (v is bool) return (bool)v ? 1 : 0;
            if (v is double) return (int)Math.Round((double)v);
            int i;
            return int.TryParse(ToStr(v), NumberStyles.Any, CultureInfo.InvariantCulture, out i) ? i : 0;
        }

        private static double ToDbl(object v)
        {
            if (v == null) return 0;
            if (v is double) return (double)v;
            if (v is int) return (double)(int)v;
            if (v is float) return (double)(float)v;
            if (v is bool) return (bool)v ? 1 : 0;
            double d;
            return double.TryParse(ToStr(v), NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        private static string ToStr(object v) { return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }

        private static int ModeFromConfig(bool enabled, bool gateOn, int mods)
        {
            if (!enabled) return 0;
            if (!gateOn) return 1;
            if (mods == 1) return 3;
            if (mods == 4) return 4;
            if (mods == 8) return 5;
            return 2;
        }

        private static void ApplyMode(bool explorer, int mode)
        {
            bool en = mode > 0;
            bool gate = mode >= 2;
            int mods = mode == 3 ? 1 : mode == 4 ? 4 : mode == 5 ? 8 : 2;
            if (explorer) { Config.ExplorerEnabled = en; Config.FolderGateEnabled = gate; Config.FolderGateMods = mods; }
            else { Config.PreviewFiles = en; Config.PreviewGateEnabled = gate; Config.PreviewGateMods = mods; }
        }

        private static int SortIndex()
        {
            int b = Config.SortMode == "size" ? 2 : (Config.SortMode == "date" ? 4 : 0);
            return b + (Config.SortDesc ? 1 : 0);
        }

        private static string AreaInfo(int x, int y, int w, int h)
        {
            // 与运行时启用判定一致（Program.cs / UpdatePauseIndicator 用 <5 视为未设置），
            // 否则框选得到的区域（≥5 即生效）会被错误显示成“未设置”
            return w >= 5 && h >= 5 ? I18n.T("area_fmt", w, h, x, y) : I18n.T("area_none");
        }

        private static string[] HotkeyLabels(string which)
        {
            int mods, vk;
            bool on;
            if (which == "HkCard")
            {
                on = Config.CardHotkeyEnabled; mods = Config.CardHotkeyModifiers; vk = Config.CardHotkeyKey;
            }
            else if (which == "HkEntry")
            {
                on = Config.EntryHotkeyEnabled; mods = Config.EntryHotkeyModifiers; vk = Config.EntryHotkeyKey;
            }
            else
            {
                on = Config.HotkeyEnabled; mods = Config.HotkeyModifiers; vk = Config.HotkeyKey;
            }
            List<string> parts = new List<string>();
            if ((mods & 2) != 0) parts.Add("Ctrl");
            if ((mods & 1) != 0) parts.Add("Alt");
            if ((mods & 4) != 0) parts.Add("Shift");
            if ((mods & 8) != 0) parts.Add("Win");
            while (parts.Count < 2) parts.Add("");
            if (parts.Count > 2) parts.RemoveRange(2, parts.Count - 2);
            string kn = on && vk > 0 ? ((System.Windows.Forms.Keys)vk).ToString() : I18n.T("hotkey_none");
            parts.Add(kn);
            return parts.ToArray();
        }

        // ===== 列表 / 卡片配色 =====

        private static void Collect(DependencyObject d, List<ColorRow> list)
        {
            if (d == null) return;
            ColorRow cr = d as ColorRow;
            if (cr != null) list.Add(cr);
            Visual vis = d as Visual;
            if (vis == null) return;
            int n = VisualTreeHelper.GetChildrenCount(vis);
            for (int i = 0; i < n; i++) Collect(VisualTreeHelper.GetChild(vis, i), list);
        }

        private static string HexOf(System.Drawing.Color c)
        {
            string s = c.A < 255 ? c.A.ToString("X2") : "";
            return s + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private static string ColorHex(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string t = text.Trim();
            System.Drawing.Color c = CardPalette.Hx(t);
            if (c.A == 0 && t.IndexOf("rgba") < 0) return null;
            Match m = Regex.Match(t, "[0-9a-fA-F]{6,8}");
            if (m.Success) return m.Value;
            return HexOf(c);
        }

        private static void UpdateListPalette()
        {
            if (_root == null) return;
            List<ColorRow> rows = new List<ColorRow>();
            Collect(_root, rows);
            StringBuilder sb = new StringBuilder();
            foreach (ColorRow cr in rows)
            {
                string k = GetKey(cr);
                if (string.IsNullOrEmpty(k) || !k.StartsWith("pal:")) continue;
                string hex = ColorHex(cr.Text);
                if (hex == null) continue;
                sb.Append(k.Substring(4)).Append('=').Append(hex).Append('\n');
            }
            Config.ListPalette = sb.ToString();
            Config.ApplyListPalette();
            Config.Save();
            Program.RefreshAllPanels();
        }

        private static void UpdateCardPalette(string part, object v)
        {
            int idx = Config.CardPaletteIndex;
            if (idx < 0 || idx >= CardPalettes.All.Count) idx = 0;
            CardPalette p = CardPalettes.Get(idx);
            switch (part)
            {
                case "Bg": p.Bg = CardPalette.Hx(ToStr(v)); break;
                case "Fg": p.Fg = CardPalette.Hx(ToStr(v)); break;
                case "BadgeBg": p.BadgeBg = CardPalette.Hx(ToStr(v)); break;
                case "BadgeFg": p.BadgeFg = CardPalette.Hx(ToStr(v)); break;
                case "Strip": p.StripColor = CardPalette.Hx(ToStr(v)); break;
                case "StripWidth": p.StripWidth = ToInt(v); break;
                case "FontName": p.FontName = ToStr(v); break;
                case "FontSize": p.FontSize = (float)ToDbl(v); break;
                case "Radius": p.Radius = ToInt(v); break;
                default: return;
            }
            // _fnDerive 是私有：用 Serialize→Parse 重建，Hover/Sub 一起派生
            CardPalettes.All[idx] = CardPalette.Parse(p.Serialize(), p.Name);
            CardPalettes.Save();
            Config.Save();
            Program.ApplyThemeChange();
        }

        // ===== 特殊容器 =====

        private static void FillLanguages(ComboBox cb)
        {
            if (cb == null) return;
            _loading = true;
            try
            {
                cb.Items.Clear();
                foreach (string n in I18n.LangNames) cb.Items.Add(n);
                int i = Array.IndexOf(I18n.LangCodes, Config.Language);
                cb.SelectedIndex = i >= 0 ? i : 0;
            }
            finally { _loading = false; }
            cb.SelectionChanged += delegate(object s, SelectionChangedEventArgs e)
            {
                if (_loading) return;
                int i = cb.SelectedIndex;
                if (i >= 0 && i < I18n.LangCodes.Length) Write("Language", I18n.LangCodes[i]);
            };
        }

        private static void FillTrayList(ItemsControl ic)
        {
            if (ic == null) return;
            List<TrayRow> rows = new List<TrayRow>();
            // “资源管理器悬停”不在此列：其开关已在「预览列表 → 触发」页提供，避免重复
            rows.Add(new TrayRow(I18n.T("tray_show_preview"), "TrayShowPreview", "PreviewFiles"));
            rows.Add(new TrayRow(I18n.T("tray_show_orig"), "TrayShowOrigSize", "PreviewOriginalSize"));
            rows.Add(new TrayRow(I18n.T("tray_show_debuglog"), "TrayShowDebugLog", "DebugLog"));
            rows.Add(new TrayRow(I18n.T("tray_show_hidetooltip"), "TrayShowHideTooltip", "HideTooltip"));
            rows.Add(new TrayRow(I18n.T("tray_show_buttontips"), "TrayShowButtonTips", "ShowTips"));
            rows.Add(new TrayRow(I18n.T("tray_show_autostart"), "TrayShowAutostart", "__autostart__"));
            rows.Add(new TrayRow(I18n.T("tray_show_hidetray"), "TrayShowHideTray", "HideTrayIcon"));
            rows.Add(new TrayRow(I18n.T("tray_show_addcard"), "TrayShowAddCard", null));
            ic.ItemsSource = rows;
        }

        private static void FillListSchemes(ComboBox cb)
        {
            if (cb == null) return;
            _loading = true;
            try
            {
                cb.Items.Clear();
                cb.Items.Add(I18n.T("tray_default"));
                foreach (KeyValuePair<string, string> kv in Config.PaletteSchemes()) cb.Items.Add(kv.Key);
                cb.SelectedIndex = 0;
            }
            finally { _loading = false; }
            cb.SelectionChanged += delegate
            {
                if (_loading || cb.SelectedIndex <= 0) return;
                List<KeyValuePair<string, string>> all = Config.PaletteSchemes();
                int i = cb.SelectedIndex - 1;
                if (i >= all.Count) return;
                Config.ListPalette = all[i].Value;
                Config.ApplyListPalette();
                Config.Save();
                Program.RefreshAllPanels();
                Refresh();
            };
        }

        private static void FillCardSchemes(ComboBox cb)
        {
            if (cb == null) return;
            if (CardPalettes.All.Count == 0) CardPalettes.Load();
            _loading = true;
            try
            {
                cb.Items.Clear();
                foreach (CardPalette p in CardPalettes.All) cb.Items.Add(p.Name);
                cb.SelectedIndex = Math.Max(0, Math.Min(CardPalettes.All.Count - 1, Config.CardPaletteIndex));
            }
            finally { _loading = false; }
            cb.SelectionChanged += delegate
            {
                if (_loading || cb.SelectedIndex < 0) return;
                Config.CardPaletteIndex = cb.SelectedIndex;
                Config.Save();
                Program.ApplyThemeChange();
                Refresh();
            };
        }

        private static void FillQr(Border box)
        {
            if (box == null || box.Background is ImageBrush) return;
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HoverExplorer.donate_qr.png"))
                {
                    if (s == null) return;
                    BitmapImage bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = s;
                    bi.EndInit();
                    ImageBrush ib = new ImageBrush(bi);
                    ib.Stretch = Stretch.Uniform;
                    box.Background = ib;
                }
            }
            catch { }
        }

        internal static void ReloadSchemes()
        {
            if (_root == null) return;
            ComboBox cb = FindByName(_root, "CardSchemeCombo") as ComboBox;
            if (cb == null) return;
            _loading = true;
            try
            {
                cb.Items.Clear();
                foreach (CardPalette p in CardPalettes.All) cb.Items.Add(p.Name);
                cb.SelectedIndex = Math.Max(0, Math.Min(CardPalettes.All.Count - 1, Config.CardPaletteIndex));
            }
            finally { _loading = false; }
        }

        private static T FindVisualAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T) return (T)d;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private static FrameworkElement FindByName(DependencyObject d, string name)
        {
            if (d == null) return null;
            FrameworkElement fe = d as FrameworkElement;
            if (fe != null && fe.Name == name) return fe;
            Visual vis = d as Visual;
            if (vis == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(vis);
            for (int i = 0; i < n; i++)
            {
                FrameworkElement hit = FindByName(VisualTreeHelper.GetChild(vis, i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        // ===== 按钮 / 链接动作 =====

        private static void AttachAction(DependencyObject d, string action)
        {
            Hyperlink h = d as Hyperlink;
            if (h != null)
            {
                try { h.NavigateUri = new Uri(action.StartsWith("http") ? action : "about:blank"); } catch { }
                h.RequestNavigate += delegate(object s, RequestNavigateEventArgs e)
                {
                    e.Handled = true;
                    RunAction(action);
                };
                return;
            }
            Button b = d as Button;
            if (b != null)
            {
                b.Click += delegate { if (!_loading) RunAction(action); };
                return;
            }
            Border bd = d as Border;
            if (bd != null)
            {
                bd.MouseLeftButtonUp += delegate { if (!_loading) RunAction(action); };
            }
        }

        private static void RunAction(string action)
        {
            try { RunActionCore(action); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[WpfSettings:Action] " + ex); }
        }

        private static void RunActionCore(string action)
        {
            if (string.IsNullOrEmpty(action)) return;
            if (action.StartsWith("http"))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(action) { UseShellExecute = true }); }
                catch { }
                return;
            }
            string[] p = action.Split(':');
            switch (p[0])
            {
                case "hk": HotkeyAction(p); return;
                case "area": AreaAction(p); return;
                case "pause": PauseAction(p); return;
                case "ffmpeg": FfmpegAction(p); return;
                case "palette": PaletteAction(p); return;
                case "card": CardAction(p); return;
                case "gen": GeneratorLinks.Open(p.Length > 1 ? p[1] : "list"); return;
                case "tray": TrayBrowse(); return;
                case "mp3fixer": Mp3Fixer(); return;
                case "wechat":
                    try { System.Windows.Clipboard.SetText("jtugqivi"); } catch { }
                    System.Windows.MessageBox.Show(I18n.T("donated"), I18n.T("settings"));
                    return;
            }
        }

        // ---- 快捷键 ----

        private static void HotkeyAction(string[] p)
        {
            if (p.Length < 3) return;
            string which = p[1], op = p[2];
            if (op == "del")
            {
                if (which == "main") { Config.HotkeyEnabled = false; Config.HotkeyKey = 0; Config.HotkeyModifiers = 0; }
                else if (which == "card") { Config.CardHotkeyEnabled = false; Config.CardHotkeyKey = 0; Config.CardHotkeyModifiers = 0; }
                else { Config.EntryHotkeyEnabled = false; Config.EntryHotkeyKey = 0; Config.EntryHotkeyModifiers = 0; }
                Program.ApplySettingsLive(true, false);
                RefreshDisplays();
                return;
            }
            if (op == "reset") CaptureHotkey(which);
        }

        private static void CaptureHotkey(string which)
        {
            Window w = new Window();
            w.Owner = Owner;
            w.Title = I18n.T("hotkey_press_key");
            w.Width = 340; w.Height = 140;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.SetResourceReference(Window.BackgroundProperty, "BgBrush");
            TextBlock tb = new TextBlock();
            tb.Text = I18n.T("hotkey_press_key") + "\n" + I18n.T("hotkey_esc_cancel");
            tb.Margin = new Thickness(18);
            tb.FontSize = 14;
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            w.Content = tb;
            w.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { w.Close(); return; }
                int mods = 0;
                ModifierKeys mk = Keyboard.Modifiers;
                if ((mk & ModifierKeys.Control) != 0) mods |= 2;
                if ((mk & ModifierKeys.Alt) != 0) mods |= 1;
                if ((mk & ModifierKeys.Shift) != 0) mods |= 4;
                if ((mk & ModifierKeys.Windows) != 0) mods |= 8;
                if (mods == 0) return;
                int vk;
                try { vk = KeyInterop.VirtualKeyFromKey(e.Key); } catch { return; }
                if (vk <= 8 || vk > 254) return;
                // 只按修饰键时不要把它当成主键（0x10/11/12、0xA0-A5 左右键、0x5B/5C Win）
                if (vk == 0x10 || vk == 0x11 || vk == 0x12 ||
                    (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C) return;
                if (which == "main")
                {
                    Config.HotkeyEnabled = true; Config.HotkeyModifiers = mods; Config.HotkeyKey = vk;
                }
                else if (which == "card")
                {
                    Config.CardHotkeyEnabled = true; Config.CardHotkeyModifiers = mods; Config.CardHotkeyKey = vk;
                }
                else
                {
                    Config.EntryHotkeyEnabled = true; Config.EntryHotkeyModifiers = mods; Config.EntryHotkeyKey = vk;
                }
                w.Close();
                Program.ApplySettingsLive(true, false);
                RefreshDisplays();
            };
            w.ShowDialog();
        }

        // ---- 触发区 / 暂停区 ----

        private static void AreaAction(string[] p)
        {
            if (p.Length < 2) return;
            if (p[1] == "clear")
            {
                Config.EntryRectX = Config.EntryRectY = Config.EntryRectW = Config.EntryRectH = 0;
                Program.UpdateEntryIndicator();
                Program.ApplySettingsLive(false, false);
                Config.Save();
                RefreshDisplays();
                return;
            }
            if (p[1] == "color")
            {
                int[] colors = { 0x283B8EEA, 0x28EA8E3B, 0x28EA3B8E, 0x28EACB3B, 0x283B8EEA };
                int cur = Config.EntryRectColor;
                int next = colors[0];
                for (int i = 0; i < colors.Length - 1; i++) if (colors[i] == cur) { next = colors[i + 1]; break; }
                Config.EntryRectColor = next;
                Program.UpdateEntryIndicator();
                Program.ApplySettingsLive(false, false);
                SyncAreaSwatch(_areaSwatch);   // 色块同步显示桌面触发区刚切换到的实际颜色
                RefreshDisplays();
                return;
            }
            if (p[1] == "pick")
            {
                using (AreaPickForm pick = new AreaPickForm(delegate(System.Drawing.Rectangle r)
                {
                    if (r.Width < 5 || r.Height < 5) return;
                    Config.EntryRectX = r.X; Config.EntryRectY = r.Y;
                    Config.EntryRectW = r.Width; Config.EntryRectH = r.Height;
                    Program.UpdateEntryIndicator();
                    Program.ApplySettingsLive(false, false);
                    Config.Save();
                    RefreshDisplays();
                }))
                {
                    pick.ShowDialog();
                }
            }
        }

        private static void PauseAction(string[] p)
        {
            if (p.Length < 2) return;
            if (p[1] == "clear")
            {
                Config.PauseRectX = Config.PauseRectY = Config.PauseRectW = Config.PauseRectH = 0;
                Program.UpdatePauseIndicator();
                Program.ApplySettingsLive(false, false);
                Config.Save();
                RefreshDisplays();
                return;
            }
            if (p[1] == "pick")
            {
                using (AreaPickForm pick = new AreaPickForm(delegate(System.Drawing.Rectangle r)
                {
                    if (r.Width < 5 || r.Height < 5) return;
                    Config.PauseRectX = r.X; Config.PauseRectY = r.Y;
                    Config.PauseRectW = r.Width; Config.PauseRectH = r.Height;
                    Program.UpdatePauseIndicator();
                    Program.ApplySettingsLive(false, false);
                    Config.Save();
                    RefreshDisplays();
                }))
                {
                    pick.ShowDialog();
                }
            }
        }

        // ---- ffmpeg / MP3 修复 ----

        private static TextBox FindKeyTextBox(string key)
        {
            foreach (Entry e in _entries)
            {
                TextBox tb = e.D as TextBox;
                if (tb != null && e.Key == key) return tb;
            }
            return null;
        }

        private static void FfmpegAction(string[] p)
        {
            if (p.Length < 2) return;
            if (p[1] == "auto")
            {
                TextBox tb = FindKeyTextBox("FfmpegPath");
                if (tb != null) tb.Text = "";
                Write("FfmpegPath", "");
                RefreshDisplays();
                return;
            }
            if (p[1] == "download")
            {
                if (FfmpegDownload.Busy) return;
                if (FfmpegTools.FindFfmpeg() != null)
                {
                    MessageBoxResult r = System.Windows.MessageBox.Show(I18n.T("ffmpeg_download_confirm"),
                        I18n.T("ffmpeg_download"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                }
                FfmpegDownload.Download(delegate(string st) { },
                    delegate(bool ok, string msg)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            FfmpegTools.ResetCache();
                            RefreshDisplays();
                            System.Windows.MessageBox.Show(
                                ok ? I18n.T("ffmpeg_download_done", msg) : I18n.T("ffmpeg_download_fail", msg),
                                I18n.T("ffmpeg_download"));
                        }));
                    });
            }
        }

        private static void Mp3Fixer()
        {
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mp3Fixer.exe");
            if (File.Exists(exe))
            {
                try { System.Diagnostics.Process.Start(exe); return; } catch { }
            }
            try { System.Diagnostics.Process.Start("https://github.com/dlsdgj/HoverExplorer/releases"); } catch { }
        }

        // ---- 列表配色方案 ----

        // 清理后仍有效的键（drop / ac / acfg / sb / hdbg 已不再有对应视觉元素）
        private static readonly string[] ListPaletteKeys = { "bg", "zb", "fg", "mu", "hv", "sel", "selfg", "bar", "bd", "btnhv", "canvas" };
        private static readonly string[] CardPaletteKeys = { "bg", "fg", "badge_bg", "badge_fg", "strip", "strip_w", "font", "font_size", "radius" };

        // 批量导入格式：一套方案一段，空行分段；段首可用 # 名称 / [名称] / name=名称 命名。
        // 每段只保留白名单内的 key=value 行（生成器仍可能多输出旧键，安静丢弃即可）。
        private static List<KeyValuePair<string, string>> SplitSchemes(string text, string[] validKeys)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(text)) return result;
            string name = "";
            var lines = new List<string>();
            Action flush = delegate
            {
                if (lines.Count == 0) { name = ""; return; }
                string nm = string.IsNullOrEmpty(name)
                    ? I18n.T("imported_prefix") + " " + DateTime.Now.ToString("MM-dd HH:mm")
                    : name;
                result.Add(new KeyValuePair<string, string>(nm, string.Join("\n", lines.ToArray())));
                name = ""; lines.Clear();
            };
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { flush(); continue; }
                if (line.StartsWith("//") || line.StartsWith("/*")) continue;
                if (line.StartsWith("#") || (line.StartsWith("[") && line.EndsWith("]")))
                {
                    string nm = line.StartsWith("[") && line.Length > 2
                        ? line.Substring(1, line.Length - 2).Trim()
                        : line.TrimStart('#').Trim();
                    if (nm.Length > 0) name = nm;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                if (k == "name") { name = v; continue; }
                if (Array.IndexOf(validKeys, k) < 0 || v.Length == 0 || v.Length > 120) continue;
                lines.Add(k + "=" + v);
            }
            flush();
            return result;
        }

        private static string UniqueSchemeName(string name, List<KeyValuePair<string, string>> all)
        {
            string n = name;
            int i = 2;
            bool clash = true;
            while (clash)
            {
                clash = false;
                foreach (KeyValuePair<string, string> kv in all)
                    if (string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase)) { clash = true; break; }
                if (clash) n = name + " (" + (i++) + ")";
            }
            return n;
        }

        private static string UniqueCardName(string name)
        {
            string n = name;
            int i = 2;
            bool clash = true;
            while (clash)
            {
                clash = false;
                foreach (CardPalette p in CardPalettes.All)
                    if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) { clash = true; break; }
                if (clash) n = name + " (" + (i++) + ")";
            }
            return n;
        }

        private static string SerializeSchemes(List<KeyValuePair<string, string>> all)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in all)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(kv.Key.Replace("=", "\\e")).Append('=')
                  .Append(kv.Value.Replace("\n", "\\n").Replace("=", "\\e"));
            }
            return sb.ToString();
        }

        private static void ReloadListSchemes()
        {
            ComboBox cb = _root == null ? null : FindByName(_root, "ListSchemeCombo") as ComboBox;
            if (cb == null) return;
            _loading = true;
            try
            {
                cb.Items.Clear();
                cb.Items.Add(I18n.T("tray_default"));
                foreach (KeyValuePair<string, string> kv in Config.PaletteSchemes()) cb.Items.Add(kv.Key);
                cb.SelectedIndex = cb.Items.Count - 1;
            }
            finally { _loading = false; }
        }

        private static Dictionary<string, string> PaletteLines(string text)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return d;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim().TrimStart('#').ToUpperInvariant();
                if (v.Length == 8) v = v.Substring(2);
                if (v.Length != 6) continue;
                uint dummy;
                if (!uint.TryParse(v, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out dummy)) continue;
                d[k] = v;
            }
            return d;
        }

        private static System.Drawing.Color CurrentListColor(string k)
        {
            switch (k)
            {
                case "zb": return Config.ListZb;
                case "fg": return Config.ListFg;
                case "mu": return Config.ListMu;
                case "hv": return Config.ListHv;
                case "sel": return Config.ListSel;
                case "selfg": return Config.ListSelFg;
                case "bar": return Config.ListBar;
                case "bd": return Config.ListBd;
                case "btnhv": return Config.ListBtnHv;
                case "canvas": return Config.ListCanvas;
                default: return Config.ListBg;
            }
        }

        // 规范化为 11 行可导入格式：多余键丢弃，缺失键用当前生效色补齐
        private static string NormalizeListBlock(string text)
        {
            Dictionary<string, string> cur = PaletteLines(text);
            StringBuilder sb = new StringBuilder();
            foreach (string k in ListPaletteKeys)
            {
                string v;
                if (!cur.TryGetValue(k, out v)) v = HexOf(CurrentListColor(k));
                sb.Append(k).Append('=').Append(v).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static string BuildListTemplate()
        {
            return "# " + I18n.T("scheme_name") + "\n" + NormalizeListBlock(Config.ListPalette) + "\n";
        }

        private static string BuildListExport()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in Config.PaletteSchemes())
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("# ").Append(kv.Key).Append('\n').Append(NormalizeListBlock(kv.Value)).Append('\n');
            }
            if (sb.Length == 0) return BuildListTemplate();
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        private static void ClipboardCopy(string text, string okKey)
        {
            try { System.Windows.Clipboard.SetText(text); } catch { return; }
            System.Windows.MessageBox.Show(I18n.T(okKey), I18n.T("settings"));
        }

        private static void ImportDone(int n)
        {
            System.Windows.MessageBox.Show(string.Format(I18n.T("import_done"), n), I18n.T("settings"));
        }

        private static void PaletteAction(string[] p)
        {
            if (p.Length < 2) return;
            switch (p[1])
            {
                case "clear":
                case "warm":
                    Config.ListPalette = "";
                    Config.ApplyListPalette(); Config.Save(); Program.RefreshAllPanels(); Refresh();
                    return;
                case "template":
                    ClipboardCopy(BuildListTemplate(), "copied_template");
                    return;
                case "export":
                    ClipboardCopy(BuildListExport(), "copied_template");
                    return;
                case "paste":
                    {
                        string text;
                        try { text = System.Windows.Clipboard.GetText(); } catch { return; }
                        List<KeyValuePair<string, string>> blocks = SplitSchemes(text, ListPaletteKeys);
                        if (blocks.Count == 0)
                        {
                            System.Windows.MessageBox.Show(I18n.T("paste_invalid"), I18n.T("settings"));
                            return;
                        }
                        List<KeyValuePair<string, string>> all = Config.PaletteSchemes();
                        foreach (KeyValuePair<string, string> b in blocks)
                            all.Add(new KeyValuePair<string, string>(
                                UniqueSchemeName(b.Key, all), NormalizeListBlock(b.Value)));
                        Config.ListPalettesData = SerializeSchemes(all);
                        Config.ListPalette = all[all.Count - 1].Value;
                        Config.ApplyListPalette(); Config.Save(); Program.RefreshAllPanels();
                        ReloadListSchemes(); Refresh();
                        ImportDone(blocks.Count);
                        return;
                    }
                case "save":
                    {
                        string name = Prompt(I18n.T("scheme_name"), I18n.T("imported_prefix"));
                        if (string.IsNullOrEmpty(name)) return;
                        List<KeyValuePair<string, string>> all = Config.PaletteSchemes();
                        all.Add(new KeyValuePair<string, string>(
                            UniqueSchemeName(name, all), NormalizeListBlock(Config.ListPalette)));
                        Config.ListPalettesData = SerializeSchemes(all);
                        Config.Save();
                        ReloadListSchemes();
                        System.Windows.MessageBox.Show(I18n.T("hotkey_saved"), I18n.T("settings"));
                        return;
                    }
                case "del":
                    {
                        ComboBox cb = _root == null ? null : FindByName(_root, "ListSchemeCombo") as ComboBox;
                        int idx = cb == null ? -1 : cb.SelectedIndex - 1;
                        if (idx < 0) return;
                        List<KeyValuePair<string, string>> all = Config.PaletteSchemes();
                        if (idx >= all.Count) return;
                        all.RemoveAt(idx);
                        Config.ListPalettesData = SerializeSchemes(all);
                        Config.Save();
                        ReloadListSchemes();
                        return;
                    }
                case "wallpaper": WallpaperPick(); return;
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SystemParametersInfo(int action, int param, StringBuilder val, int flags);

        private static void WallpaperPick()
        {
            try
            {
                StringBuilder sb = new StringBuilder(512);
                if (SystemParametersInfo(0x0073, 512, sb, 0) == 0) return;
                string path = sb.ToString();
                if (!File.Exists(path)) return;
                int r = 0, g = 0, b = 0, n = 0;
                using (System.Drawing.Bitmap big = new System.Drawing.Bitmap(path))
                using (System.Drawing.Bitmap small = new System.Drawing.Bitmap(big, 32, 32))
                {
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                        {
                            System.Drawing.Color c = small.GetPixel(x, y);
                            r += c.R; g += c.G; b += c.B; n++;
                        }
                }
                if (n == 0) return;
                System.Drawing.Color baseC = System.Drawing.Color.FromArgb(r / n, g / n, b / n);
                System.Drawing.Color bg = Mix(baseC, System.Drawing.Color.White, 0.88);
                System.Drawing.Color zb = Mix(baseC, System.Drawing.Color.White, 0.80);
                System.Drawing.Color fg = Mix(baseC, System.Drawing.Color.Black, 0.72);
                System.Drawing.Color mu = Mix(baseC, System.Drawing.Color.Black, 0.42);
                System.Drawing.Color hv = Mix(baseC, System.Drawing.Color.White, 0.68);
                System.Drawing.Color sel = Mix(baseC, System.Drawing.Color.Black, 0.25);
                System.Drawing.Color bd = Mix(baseC, System.Drawing.Color.Black, 0.12);
                Config.ListPalette =
                    "bg=" + HexOf(bg) + "\nzb=" + HexOf(zb) + "\nfg=" + HexOf(fg) + "\nmu=" + HexOf(mu) +
                    "\nhv=" + HexOf(hv) + "\nsel=" + HexOf(sel) + "\nselfg=FFFFFF\nbar=" + HexOf(hv) +
                    "\ndrop=" + HexOf(sel) + "\nac=" + HexOf(sel) + "\nacfg=FFFFFF\nbd=" + HexOf(bd) +
                    "\nsb=" + HexOf(bd) + "\nhdbg=" + HexOf(zb) + "\nbtnhv=" + HexOf(hv) + "\ncanvas=" + HexOf(bg) + "\n";
                Config.ApplyListPalette(); Config.Save(); Program.RefreshAllPanels(); Refresh();
            }
            catch { }
        }

        private static System.Drawing.Color Mix(System.Drawing.Color a, System.Drawing.Color b, double t)
        {
            return System.Drawing.Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // ---- 卡片配色方案 ----

        private static void CardAction(string[] p)
        {
            if (p.Length < 2) return;
            switch (p[1])
            {
                case "template":
                    ClipboardCopy("# " + I18n.T("scheme_name") + "\n" + CardPalettes.Current.Serialize() + "\n", "copied_template");
                    return;
                case "export":
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (CardPalette p0 in CardPalettes.All)
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append("# ").Append(p0.Name).Append('\n').Append(p0.Serialize()).Append('\n');
                        }
                        ClipboardCopy(sb.ToString().TrimEnd('\n') + "\n", "copied_template");
                        return;
                    }
                case "save":
                    {
                        string name = Prompt(I18n.T("scheme_name"), I18n.T("imported_prefix"));
                        if (string.IsNullOrEmpty(name)) return;
                        CardPalette cur = CardPalettes.Current;
                        CardPalette np = CardPalette.Parse(cur.Serialize(), name);
                        CardPalettes.All.Add(np);
                        Config.CardPaletteIndex = CardPalettes.All.Count - 1;
                        CardPalettes.Save(); Config.Save(); Program.ApplyThemeChange();
                        ReloadSchemes(); Refresh(); PaletteListForm.RefreshSchemes();
                        return;
                    }
                case "del":
                    {
                        if (CardPalettes.All.Count <= 1) return;
                        int idx = Config.CardPaletteIndex;
                        CardPalettes.All.RemoveAt(idx);
                        if (Config.CardPaletteIndex >= CardPalettes.All.Count) Config.CardPaletteIndex = CardPalettes.All.Count - 1;
                        CardPalettes.Save(); Config.Save(); Program.ApplyThemeChange();
                        ReloadSchemes(); Refresh(); PaletteListForm.RefreshSchemes();
                        return;
                    }
                case "paste":
                    {
                        string text;
                        try { text = System.Windows.Clipboard.GetText(); } catch { return; }
                        List<KeyValuePair<string, string>> blocks = SplitSchemes(text, CardPaletteKeys);
                        if (blocks.Count == 0)
                        {
                            System.Windows.MessageBox.Show(I18n.T("paste_invalid"), I18n.T("settings"));
                            return;
                        }
                        foreach (KeyValuePair<string, string> b in blocks)
                            CardPalettes.All.Add(CardPalette.Parse(b.Value, UniqueCardName(b.Key)));
                        Config.CardPaletteIndex = CardPalettes.All.Count - 1;
                        CardPalettes.Save(); Config.Save(); Program.ApplyThemeChange();
                        ReloadSchemes(); Refresh(); PaletteListForm.RefreshSchemes();
                        ImportDone(blocks.Count);
                        return;
                    }
            }
        }

        private static void TrayBrowse()
        {
            using (System.Windows.Forms.OpenFileDialog ofd = new System.Windows.Forms.OpenFileDialog())
            {
                ofd.Filter = "Icon|*.ico|All|*.*";
                if (ofd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                TextBox tb = FindKeyTextBox("TrayIconPath");
                if (tb != null) tb.Text = ofd.FileName;
                Write("TrayIconPath", ofd.FileName);
            }
        }

        private static string Prompt(string title, string def)
        {
            Window w = new Window();
            w.Owner = Owner;
            w.Title = title;
            w.Width = 320; w.Height = 150;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.SetResourceReference(Window.BackgroundProperty, "BgBrush");
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(14);
            TextBlock cap = new TextBlock();
            cap.Text = title;
            cap.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            TextBox tb = new TextBox(); tb.Text = def ?? ""; tb.Margin = new Thickness(0, 10, 0, 10);
            Button ok = new Button();
            ok.Content = I18n.T("settings_ok");
            ok.HorizontalAlignment = HorizontalAlignment.Right;
            ok.Padding = new Thickness(16, 3, 16, 3);
            ok.Click += delegate { w.DialogResult = true; };
            sp.Children.Add(cap); sp.Children.Add(tb); sp.Children.Add(ok);
            w.Content = sp;
            tb.Focus();
            bool? r = w.ShowDialog();
            return r == true ? tb.Text.Trim() : null;
        }
    }

    // 托盘管理页的数据行：显示 / 启用两个开关直接读写 Config
    public class TrayRow : INotifyPropertyChanged
    {
        private readonly string _showKey;
        private readonly string _enableKey;

        public TrayRow(string label, string showKey, string enableKey)
        {
            Label = label;
            _showKey = showKey;
            _enableKey = enableKey;
        }

        public string Label { get; private set; }

        // 显示 / 启用 两列语义：
        //  · 显示(Show) = 该菜单项是否出现在托盘右键菜单里（TrayShowXxx 配置键）；
        //  · 启用(Enabled) = 功能本身是否开启，仅对确有独立“开/关”状态的项存在。
        // 无独立启用键的项（如“从剪贴板添加卡片”，纯动作项）没有“启用”概念，
        // 只保留“显示”开关，启用列隐藏（HasEnable=false）。
        // “开机自启”的启用状态存在注册表（IsAutoStart/SetAutoStart），与“显示”相互独立，
        // 用哨兵键 "__autostart__" 表示。
        public bool HasEnable { get { return !string.IsNullOrEmpty(_enableKey); } }
        public System.Windows.Visibility EnableVisibility
        {
            get { return HasEnable ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed; }
        }
        public bool EnableEditable { get { return true; } }

        public bool Show
        {
            get { return Read(_showKey); }
            set { Cfg.Write(_showKey, value); OnPropertyChanged("Show"); }
        }

        public bool Enabled
        {
            get
            {
                if (_enableKey == "__autostart__") return Program.IsAutoStart();
                if (string.IsNullOrEmpty(_enableKey)) return false; // 无独立启用项：该列隐藏，getter 不应被访问，安全兜底
                return Read(_enableKey);
            }
            set
            {
                if (_enableKey == "__autostart__")
                {
                    Program.SetAutoStart(value);
                    Program.RebuildTrayMenu();
                }
                else if (!string.IsNullOrEmpty(_enableKey))
                {
                    Cfg.Write(_enableKey, value);
                }
                OnPropertyChanged("Enabled");
            }
        }

        private static bool Read(string key)
        {
            FieldInfo f = typeof(Config).GetField(key, BindingFlags.Public | BindingFlags.Static);
            return f != null && f.GetValue(null) is bool && (bool)f.GetValue(null);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
        }
    }
}
