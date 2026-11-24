using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Drawing.Text;

namespace HoverExplorer.Wpf
{
    // 设置项：左侧标题 + 说明，右侧放任意控件。样式见 WpfSettings/Styles.xaml。
    // 纯 C# 依赖属性类，松散 XAML（XamlReader）无需 x:Class 即可实例化。
    public class SettingRow : ContentControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(SettingRow), new PropertyMetadata(""));
        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register("Description", typeof(string), typeof(SettingRow), new PropertyMetadata(""));

        public string Title { get { return (string)GetValue(TitleProperty); } set { SetValue(TitleProperty, value); } }
        public string Description { get { return (string)GetValue(DescriptionProperty); } set { SetValue(DescriptionProperty, value); } }
    }

    // 数值步进器：模板里的 +/- 按钮在 OnApplyTemplate 中挂接
    public class Stepper : Control
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register("Value", typeof(double), typeof(Stepper), new PropertyMetadata(0.0, OnDisplayChanged));
        public static readonly DependencyProperty MinProperty =
            DependencyProperty.Register("Min", typeof(double), typeof(Stepper), new PropertyMetadata(0.0));
        public static readonly DependencyProperty MaxProperty =
            DependencyProperty.Register("Max", typeof(double), typeof(Stepper), new PropertyMetadata(100.0));
        public static readonly DependencyProperty StepProperty =
            DependencyProperty.Register("Step", typeof(double), typeof(Stepper), new PropertyMetadata(1.0));
        public static readonly DependencyProperty FormatProperty =
            DependencyProperty.Register("Format", typeof(string), typeof(Stepper), new PropertyMetadata("0", OnDisplayChanged));
        public static readonly DependencyProperty SuffixProperty =
            DependencyProperty.Register("Suffix", typeof(string), typeof(Stepper), new PropertyMetadata("", OnDisplayChanged));
        public static readonly DependencyProperty DisplayTextProperty =
            DependencyProperty.Register("DisplayText", typeof(string), typeof(Stepper), new PropertyMetadata("0"));
        // 单位绑定：非空时显示可点击的 px/% 后缀，点击即切换（不再单独占一行下拉）
        public static readonly DependencyProperty UnitKeyProperty =
            DependencyProperty.Register("UnitKey", typeof(string), typeof(Stepper), new PropertyMetadata("", OnUnitChanged));
        public static readonly DependencyProperty UnitTextProperty =
            DependencyProperty.Register("UnitText", typeof(string), typeof(Stepper), new PropertyMetadata(""));

        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public double Min { get { return (double)GetValue(MinProperty); } set { SetValue(MinProperty, value); } }
        public double Max { get { return (double)GetValue(MaxProperty); } set { SetValue(MaxProperty, value); } }
        public double Step { get { return (double)GetValue(StepProperty); } set { SetValue(StepProperty, value); } }
        public string Format { get { return (string)GetValue(FormatProperty); } set { SetValue(FormatProperty, value); } }
        public string Suffix { get { return (string)GetValue(SuffixProperty); } set { SetValue(SuffixProperty, value); } }
        public string DisplayText { get { return (string)GetValue(DisplayTextProperty); } private set { SetValue(DisplayTextProperty, value); } }
        public string UnitKey { get { return (string)GetValue(UnitKeyProperty); } set { SetValue(UnitKeyProperty, value); } }
        public string UnitText { get { return (string)GetValue(UnitTextProperty); } private set { SetValue(UnitTextProperty, value); } }

        public Stepper()
        {
            Refresh();
        }

        private TextBox _input;
        private Button _unitBtn;

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            Button minus = GetTemplateChild("PART_Minus") as Button;
            Button plus = GetTemplateChild("PART_Plus") as Button;
            if (minus != null) minus.Click += delegate { Set(Value - Step); };
            if (plus != null) plus.Click += delegate { Set(Value + Step); };
            _unitBtn = GetTemplateChild("PART_Unit") as Button;
            if (_unitBtn != null)
            {
                _unitBtn.Click += delegate { ToggleUnit(); };
                _unitBtn.Visibility = HasUnit ? Visibility.Visible : Visibility.Collapsed;
            }

            _input = GetTemplateChild("PART_Input") as TextBox;
            if (_input != null)
            {
                _input.GotFocus += delegate { _input.SelectAll(); };
                _input.LostFocus += delegate { CommitInput(); };
                _input.KeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e)
                {
                    if (e.Key == System.Windows.Input.Key.Enter) { CommitInput(); Keyboard.ClearFocus(); }
                    else if (e.Key == System.Windows.Input.Key.Escape) { Refresh(); Keyboard.ClearFocus(); }
                };
            }
            RefreshUnit();
            Refresh();
        }

        private static void OnDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((Stepper)d).Refresh();
        }

        private static void OnUnitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((Stepper)d).RefreshUnit();
        }

        private bool HasUnit { get { return !string.IsNullOrEmpty(UnitKey); } }

        private int CurrentUnit()
        {
            if (!HasUnit) return 1;
            try { return Cfg.UnitOf(UnitKey); } catch { return 1; }
        }

        // 百分比模式最大 50，像素模式用 XAML 里的 Max
        private double EffectiveMax()
        {
            return HasUnit && CurrentUnit() == 0 ? Math.Min(Max, 50) : Max;
        }

        private void ToggleUnit()
        {
            if (!HasUnit) return;
            int next = 1 - CurrentUnit();
            try { Cfg.Write(UnitKey, next); } catch { }
            RefreshUnit();
            Set(Value);
            Refresh();
        }

        private void RefreshUnit()
        {
            if (_unitBtn != null) _unitBtn.Visibility = HasUnit ? Visibility.Visible : Visibility.Collapsed;
            UnitText = HasUnit ? (CurrentUnit() == 0 ? " %" : " px") : "";
        }

        private void CommitInput()
        {
            if (_input == null) return;
            // 去掉后缀与空白后解析，允许用户直接输入数字（含小数）
            string raw = (_input.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(Suffix)) raw = raw.Replace(Suffix, "").Trim();
            double v;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) Set(v);
            Refresh();
        }

        private void Refresh()
        {
            string fmt = string.IsNullOrEmpty(Format) ? "0" : Format;
            DisplayText = Value.ToString(fmt, CultureInfo.InvariantCulture) + (Suffix ?? "");
            if (_input != null && !_input.IsFocused) _input.Text = DisplayText;
        }

        private void Set(double v)
        {
            Value = Math.Round(Math.Max(Min, Math.Min(EffectiveMax(), v)), 4);
        }
    }

    // 颜色行：标签 + 色块 + 可编辑 hex/rgba 文本
    public class ColorRow : Control
    {
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register("Label", typeof(string), typeof(ColorRow), new PropertyMetadata(""));
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register("Text", typeof(string), typeof(ColorRow), new PropertyMetadata("", OnTextChanged));
        public static readonly DependencyProperty BrushProperty =
            DependencyProperty.Register("Brush", typeof(Brush), typeof(ColorRow), new PropertyMetadata(Brushes.Transparent));
        public static readonly DependencyProperty PickColorProperty =
            DependencyProperty.Register("PickColor", typeof(Action<string>), typeof(ColorRow), new PropertyMetadata(null));

        public string Label { get { return (string)GetValue(LabelProperty); } set { SetValue(LabelProperty, value); } }
        public string Text { get { return (string)GetValue(TextProperty); } set { SetValue(TextProperty, value); } }
        public Brush Brush { get { return (Brush)GetValue(BrushProperty); } set { SetValue(BrushProperty, value); } }
        // 由绑定层注入：点击吸管按钮时调用，取色结果写回本行 Text
        public Action<string> PickColor { get { return (Action<string>)GetValue(PickColorProperty); } set { SetValue(PickColorProperty, value); } }

        public ColorRow()
        {
            UpdateBrush();
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            var btn = GetTemplateChild("PART_Pick") as System.Windows.Controls.Button;
            if (btn != null) btn.Click += delegate
            {
                if (PickColor == null) return;
                string hex = ScreenColorPicker.Pick();
                if (!string.IsNullOrEmpty(hex)) PickColor(hex);
            };
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ColorRow)d).UpdateBrush();
        }

        private void UpdateBrush()
        {
            Color c;
            if (TryParse(Text, out c)) Brush = new SolidColorBrush(c);
        }

        private static bool TryParse(string s, out Color c)
        {
            c = default(Color);
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            try
            {
                if (s.StartsWith("rgba", StringComparison.OrdinalIgnoreCase))
                {
                    int l = s.IndexOf('(');
                    if (l < 0) return false;
                    string[] p = s.Substring(l + 1).TrimEnd(')').Split(',');
                    c = Color.FromArgb(byte.Parse(p[3].Trim()), byte.Parse(p[0].Trim()),
                                       byte.Parse(p[1].Trim()), byte.Parse(p[2].Trim()));
                    return true;
                }
                if (!s.StartsWith("#")) s = "#" + s;
                c = (Color)ColorConverter.ConvertFromString(s);
                return true;
            }
            catch { return false; }
        }
    }

    // ===== 列表外观：统一界面字体 + 预览点选部位单独覆盖 =====
    // 预览与检查器都是纯 C# 构建，供 XamlReader 加载的松散 XAML 直接 <c:.../> 使用。
    internal static class ListFontParts
    {
        // 预览与检查器共享的选中部位：任一方改动都能通知另一方刷新
        internal static string Selected;
        internal static event Action SelectedChanged;
        internal static void Select(string part)
        {
            Selected = part;
            var h = SelectedChanged;
            if (h != null) h();
        }

        internal static readonly string[] Keys = { "header", "title", "subtitle", "item", "code", "chip", "meta", "source" };
        internal static readonly string[] Names = { "表头", "标题", "副标题", "正文", "代码", "徽标", "元信息", "来源" };
        internal static string NameOf(string key)
        {
            for (int i = 0; i < Keys.Length; i++) if (Keys[i] == key) return Names[i];
            return key;
        }
        internal static string FontNameOf(string key)
        {
            switch (key)
            {
                case "header": return Config.ListHeaderFontName;
                case "title": return Config.ListTitleFontName;
                case "subtitle": return Config.ListSubtitleFontName;
                case "code": return Config.ListCodeFontName;
                case "chip": return Config.ListChipFontName;
                case "meta": return Config.ListMetaFontName;
                case "source": return Config.ListSourceFontName;
                default: return Config.ListItemFontName;
            }
        }
        internal static float FontSizeOf(string key)
        {
            switch (key)
            {
                case "header": return Config.ListHeaderFontSize;
                case "title": return Config.ListTitleFontSize;
                case "subtitle": return Config.ListSubtitleFontSize;
                case "code": return Config.ListCodeFontSize;
                case "chip": return Config.ListChipFontSize;
                case "meta": return Config.ListMetaFontSize;
                case "source": return Config.ListSourceFontSize;
                default: return Config.ListItemFontSize;
            }
        }
        internal static void Set(string key, string name, float size)
        {
            switch (key)
            {
                case "header": Config.ListHeaderFontName = name; Config.ListHeaderFontSize = size; break;
                case "title": Config.ListTitleFontName = name; Config.ListTitleFontSize = size; break;
                case "subtitle": Config.ListSubtitleFontName = name; Config.ListSubtitleFontSize = size; break;
                case "code": Config.ListCodeFontName = name; Config.ListCodeFontSize = size; break;
                case "chip": Config.ListChipFontName = name; Config.ListChipFontSize = size; break;
                case "meta": Config.ListMetaFontName = name; Config.ListMetaFontSize = size; break;
                case "source": Config.ListSourceFontName = name; Config.ListSourceFontSize = size; break;
                default: Config.ListItemFontName = name; Config.ListItemFontSize = size; break;
            }
        }
        // 当前实际生效的字体/字号（跟随界面字体时按比例缩放）
        internal static string EffectiveName(string key)
        {
            return Config.IsFontOverridden(key) ? FontNameOf(key) : Config.UiFontName;
        }
        internal static float EffectiveSize(string key)
        {
            if (Config.IsFontOverridden(key)) return FontSizeOf(key);
            float b = Config.UiFontSize > 0 ? Config.UiFontSize : 10f;
            switch (key)
            {
                case "subtitle": case "code": case "chip": return Math.Max(6.5f, b - 1f);
                case "meta": case "source": return Math.Max(6.5f, b - 2f);
                default: return b;
            }
        }
    }

    // 可点选的列表面板预览：每块文字是一个可点击区域，点击后高亮并通知外部。
    public class ListAppearancePreview : UserControl
    {
        public static readonly DependencyProperty SelectedPartProperty =
            DependencyProperty.Register("SelectedPart", typeof(string), typeof(ListAppearancePreview),
                new PropertyMetadata("title", OnSelectedChanged));
        public string SelectedPart { get { return (string)GetValue(SelectedPartProperty); } set { SetValue(SelectedPartProperty, value); } }

        private static List<string> _fonts;
        internal static List<string> SystemFontNames()
        {
            if (_fonts == null)
            {
                _fonts = new List<string>();
                try { using (var ifc = new InstalledFontCollection()) foreach (var f in ifc.Families) _fonts.Add(f.Name); }
                catch { }
            }
            return _fonts;
        }

        private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ListAppearancePreview)d).Redraw();
        }

        private StackPanel _root;

        public ListAppearancePreview()
        {
            _root = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
            AddContent();
            Grid host = new Grid();
            host.Children.Add(_root);
            Content = host;
            if (!string.IsNullOrEmpty(ListFontParts.Selected)) SelectedPart = ListFontParts.Selected;
            ListFontParts.SelectedChanged += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!string.IsNullOrEmpty(ListFontParts.Selected)) SelectedPart = ListFontParts.Selected;
                    Redraw();
                }));
            };
            Loaded += delegate { Redraw(); };
            Unloaded += delegate { };
        }

        private static SolidColorBrush BrushOf(System.Drawing.Color c)
        {
            return new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        }

        private Border MakeChip(string key, string text, System.Drawing.Color? fg = null)
        {
            var tb = new TextBlock { Text = text, Tag = key, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 1, 3, 1) };
            var b = new Border { Tag = "part:" + key, Child = tb, Cursor = Cursors.Hand, Padding = new Thickness(2, 1, 2, 1) };
            b.MouseLeftButtonUp += delegate { ListFontParts.Select(key); };
            return b;
        }

        private void AddContent()
        {
            _root.Children.Add(new TextBlock { Text = "示例文件夹", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 2, 4) });
            var head = new WrapPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(MakeChip("header", "名称 ▲"));
            head.Children.Add(MakeChip("header", "大小"));
            head.Children.Add(MakeChip("header", "日期"));
            _root.Children.Add(head);
            string[,] rows = { { "📁", "cs", "89 项" }, { "📁", "备份", "57 项" }, { "📁", "_bc_test", "1 项" },
                               { "📄", "config.json", "1.7 KB" }, { "📄", "备份HF.ahk", "1.7 KB" } };
            for (int i = 0; i < rows.GetLength(0); i++)
            {
                var row = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 3, 2, 3) };
                row.Children.Add(new TextBlock { Text = rows[i, 0], Width = 18 });
                var mid = MakeChip("item", rows[i, 1]);
                DockPanel.SetDock(mid, Dock.Left);
                row.Children.Add(mid);
                var sz = MakeChip("meta", rows[i, 2]);
                DockPanel.SetDock(sz, Dock.Right);
                row.Children.Add(sz);
                _root.Children.Add(row);
            }
            var chips = new WrapPanel { Margin = new Thickness(2, 6, 2, 2) };
            chips.Children.Add(MakeChip("chip", "DIR"));
            chips.Children.Add(MakeChip("chip", "AHK"));
            chips.Children.Add(MakeChip("chip", "JSON"));
            _root.Children.Add(chips);
            _root.Children.Add(new TextBlock { Text = "副标题：D:\\work\\示例文件夹", Margin = new Thickness(2, 4, 2, 2) });
            _root.Children.Add(new TextBlock { Text = "代码：{\"hover\":130, \"mode\":\"always\"}", Margin = new Thickness(2, 2, 2, 2) });
            _root.Children.Add(new TextBlock { Text = "来源角标：FILE", Margin = new Thickness(2, 2, 2, 2) });
        }

        private void ApplyFont(TextBlock tb, string key)
        {
            var fam = new FontFamily(ListFontParts.EffectiveName(key));
            tb.FontFamily = fam;
            tb.FontSize = ListFontParts.EffectiveSize(key);
        }

        private void StyleBorders(Panel panel, string sel)
        {
            foreach (var c in panel.Children)
            {
                var b = c as Border;
                if (b != null)
                {
                    string tag = b.Tag as string;
                    if (tag != null && tag.StartsWith("part:"))
                    {
                        string key = tag.Substring(5);
                        var tb = b.Child as TextBlock;
                        if (tb != null) ApplyFont(tb, key);
                        bool on = key == sel;
                        b.BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0xE0, 0x23, 0x7A)) : Brushes.Transparent;
                        b.BorderThickness = on ? new Thickness(1) : new Thickness(0);
                        b.Background = on ? new SolidColorBrush(Color.FromArgb(24, 0xE0, 0x23, 0x7A)) : Brushes.Transparent;
                    }
                    continue;
                }
                var sub = c as Panel;
                if (sub != null) StyleBorders(sub, sel);
            }
        }

        public void Redraw()
        {
            if (_root == null) return;
            StyleBorders(_root, SelectedPart);
            // 底部三行说明文字：副标题 / 代码 / 来源
            int n = _root.Children.Count;
            if (n >= 3)
            {
                var sub = _root.Children[n - 3] as TextBlock;
                var code = _root.Children[n - 2] as TextBlock;
                var src = _root.Children[n - 1] as TextBlock;
                if (sub != null) ApplyFont(sub, "subtitle");
                if (code != null) ApplyFont(code, "code");
                if (src != null) ApplyFont(src, "source");
            }
        }
    }

    // 列表外观表格：项目 | 颜色 | 字体 | 字号（无实时预览）。
    // 颜色行用 ColorRow + Cfg.Key，保持设置窗现有的配色读写链路。
    public class AppearanceTable : UserControl
    {
        private static readonly string[] PalKeys =
            { "bg", "zb", "fg", "mu", "hv", "sel", "selfg", "bar", "drop", "ac", "acfg", "bd", "sb", "hdbg", "btnhv" };
        private static readonly string[] PalNames =
            { "行背景(奇)", "斑马(偶)", "名称文字", "大小/数量", "悬停高亮", "选中背景", "选中文字", "大小背景条", "拖拽放置", "排序按钮底", "排序按钮字", "边框分隔线", "滚动条", "表头底色", "按钮悬停" };
        // 表格行：颜色键 / 字体分区 / 项目名 / 副说明
        private static readonly string[] RowColor = { "", "", "bg", "zb", "fg", "mu", "hv", "sel", "selfg", "bar", "drop", "ac", "acfg", "bd", "sb", "hdbg", "btnhv", "", "" };
        private static readonly string[] RowFont = { "ui", "", "", "", "title", "meta", "", "", "item", "", "", "", "", "", "", "", "", "header", "subtitle" };
        private static readonly string[] RowName = { "界面字体", "", "行背景(奇)", "斑马(偶)", "名称文字", "大小/数量", "悬停高亮", "选中背景", "选中文字", "大小背景条", "拖拽放置", "排序按钮底", "排序按钮字", "边框分隔线", "滚动条", "表头底色", "按钮悬停", "表头", "信息栏" };
        private static readonly string[] RowI18n = { "font_ui", "", "pal_lbl_bg", "pal_lbl_zb", "pal_lbl_fg", "pal_lbl_mu", "pal_lbl_hv", "pal_lbl_sel", "pal_lbl_selfg", "pal_lbl_bar", "pal_lbl_drop", "pal_lbl_ac", "pal_lbl_acfg", "pal_lbl_bd", "pal_lbl_sb", "pal_lbl_hdbg", "pal_lbl_btnhv", "font_header", "font_subtitle" };
        private static readonly string[] RowSub = { "全局默认:列表、表头、信息栏统一使用", "", "bg", "zb", "fg", "mu", "hv", "sel", "selfg", "bar", "drop", "ac", "acfg", "bd", "sb", "hdbg", "btnhv", "列表标题行", "底部/副标题信息" };
        // 副说明的 i18n 键：只有前两项与最后两项是需翻译文案，中间是 key 名
        private static readonly string[] RowSubI18n = { "ui_font_desc", "", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, "font_header_desc", "font_subtitle_desc" };

        private readonly Dictionary<string, ComboBox> _fonts = new Dictionary<string, ComboBox>();
        private readonly Dictionary<string, ComboBox> _sizes = new Dictionary<string, ComboBox>();
        private readonly List<string> _famList = new List<string>();
        private bool _loading;

        public AppearanceTable()
        {
            try { using (var ifc = new InstalledFontCollection()) foreach (var f in ifc.Families) _famList.Add(f.Name); } catch { }

            var root = new Grid { Margin = new Thickness(12, 6, 12, 10) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            int row = 0;
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(root, 0, 0, I18n.T("col_item"), true, "col_item");
            AddCell(root, 0, 1, I18n.T("col_color"), true, "col_color");
            AddCell(root, 0, 2, I18n.T("col_font"), true, "col_font");
            AddCell(root, 0, 3, I18n.T("col_size"), true, "col_size");
            AddRule(root, 0, 0.3);
            row++;

            var colorRows = new Dictionary<string, ColorRow>();
            for (int i = 0; i < RowColor.Length; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                int r = row++;
                var name = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
                var nameTb = new TextBlock { Text = RowName[i], FontWeight = FontWeights.SemiBold, FontSize = 11 };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Cfg.SetI18n(nameTb, RowI18n[i]);
                name.Children.Add(nameTb);
                if (!string.IsNullOrEmpty(RowSub[i]))
                {
                    var subTb = new TextBlock { Text = RowSub[i], FontSize = 9 };
                    subTb.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                    if (!string.IsNullOrEmpty(RowSubI18n[i])) Cfg.SetI18n(subTb, RowSubI18n[i]);
                    name.Children.Add(subTb);
                }
                Grid.SetRow(name, r); Grid.SetColumn(name, 0);
                root.Children.Add(name);

                string ck = RowColor[i];
                if (!string.IsNullOrEmpty(ck))
                {
                    var cr = new ColorRow();
                    Cfg.SetKey(cr, "pal:" + ck);
                    Cfg.SetI18n(cr, "pal_lbl_" + ck);
                    cr.Label = PalNames[Array.IndexOf(PalKeys, ck)];
                    Grid.SetRow(cr, r); Grid.SetColumn(cr, 1);
                    cr.VerticalAlignment = VerticalAlignment.Center;
                    root.Children.Add(cr);
                    colorRows[ck] = cr;
                }
                else
                {
                    AddCell(root, r, 1, i == 0 ? "—" : "— " + I18n.T("no_own_color"), false, i == 0 ? null : "no_own_color");
                }

                string part = RowFont[i];
                if (!string.IsNullOrEmpty(part))
                {
                    var fc = MakeFontCombo(part);
                    Grid.SetRow(fc, r); Grid.SetColumn(fc, 2);
                    fc.VerticalAlignment = VerticalAlignment.Center;
                    root.Children.Add(fc);
                    _fonts[part] = fc;
                    var sc = MakeSizeCombo(part);
                    Grid.SetRow(sc, r); Grid.SetColumn(sc, 3);
                    sc.VerticalAlignment = VerticalAlignment.Center;
                    root.Children.Add(sc);
                    _sizes[part] = sc;
                }
                else AddCell(root, r, 2, "—", false);
                AddRule(root, r, 0.25);
            }

            // 恢复按钮：作为表格最后一行跨全宽、右对齐 → 与表格右边缘对齐
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int rr = row++;
            var restore = new Button { Content = I18n.T("restore_follow"), Padding = new Thickness(10, 2, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 4, 0) };
            Cfg.SetI18n(restore, "restore_follow");
            restore.Click += delegate { Config.ListFontOverrides = ""; Config.RebuildFontOverrides(); Commit(); };
            Grid.SetRow(restore, rr);
            Grid.SetColumn(restore, 0);
            Grid.SetColumnSpan(restore, 4);
            root.Children.Add(restore);
            Content = root;   // 不拦截滚轮：滚动交给外层设置页的 ScrollViewer
            Loaded += delegate { RefreshFontCombos(); };
        }

        private static Brush ResourceBrush(string key, Brush fallback)
        {
            try { var b = Application.Current.Resources[key] as Brush; if (b != null) return b; } catch { }
            return fallback;
        }

        private static void AddRule(Grid g, int row, double opacity)
        {
            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.Gray, Opacity = opacity };
            Grid.SetRow(line, row);
            Grid.SetColumn(line, 0);
            Grid.SetColumnSpan(line, 4);
            g.Children.Add(line);
        }

        private static void AddCell(Grid g, int row, int col, string text, bool header, string i18nKey = null)
        {
            var tb = new TextBlock { Text = text, Margin = new Thickness(4, 5, 4, 5), VerticalAlignment = VerticalAlignment.Center,
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal };
            tb.SetResourceReference(TextBlock.ForegroundProperty, header ? "TextBrush" : "SubTextBrush");
            if (!string.IsNullOrEmpty(i18nKey)) Cfg.SetI18n(tb, i18nKey);
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }

        private ComboBox MakeFontCombo(string part)
        {
            var cb = new ComboBox { Margin = new Thickness(2, 3, 4, 3), FontSize = 11 };
            for (int i = 0; i < _famList.Count; i++) cb.Items.Add(_famList[i]);
            cb.Tag = part;
            cb.SelectionChanged += delegate
            {
                if (_loading || cb.SelectedIndex < 0) return;
                string key = (string)cb.Tag;
                if (key == "ui") { Config.UiFontName = cb.SelectedItem.ToString(); Commit(); return; }
                float sz = Config.IsFontOverridden(key) ? ListFontParts.FontSizeOf(key) : Config.UiScaledSize(key);
                ListFontParts.Set(key, cb.SelectedItem.ToString(), sz);
                Config.SetFontOverridden(key, true);
                Commit();
            };
            return cb;
        }

        private ComboBox MakeSizeCombo(string part)
        {
            var cb = new ComboBox { Margin = new Thickness(2, 3, 4, 3), FontSize = 11 };
            for (int n = 6; n <= 24; n++) cb.Items.Add(n.ToString());
            cb.Tag = part;
            cb.SelectionChanged += delegate
            {
                if (_loading || cb.SelectedIndex < 0) return;
                string key = (string)cb.Tag;
                if (key == "ui") { Config.UiFontSize = Convert.ToSingle(cb.SelectedItem); Commit(); return; }
                string nm = Config.IsFontOverridden(key) ? ListFontParts.FontNameOf(key) : Config.UiFontName;
                ListFontParts.Set(key, nm, Convert.ToSingle(cb.SelectedItem));
                Config.SetFontOverridden(key, true);
                Commit();
            };
            return cb;
        }

        private void Commit()
        {
            Config.Save();
            Program.RefreshAllPanels();
            RefreshFontCombos();
        }

        private void RefreshFontCombos()
        {
            _loading = true;
            try
            {
                foreach (var kv in _fonts)
                {
                    string nm = kv.Key == "ui" ? Config.UiFontName : ListFontParts.EffectiveName(kv.Key);
                    if (!kv.Value.Items.Contains(nm)) kv.Value.Items.Insert(0, nm);
                    kv.Value.SelectedItem = nm;
                }
                foreach (var kv in _sizes)
                {
                    float sz = kv.Key == "ui" ? Config.UiFontSize : ListFontParts.EffectiveSize(kv.Key);
                    string s = ((int)Math.Round(sz)).ToString();
                    if (!kv.Value.Items.Contains(s)) kv.Value.Items.Insert(0, s);
                    kv.Value.SelectedItem = s;
                }
            }
            finally { _loading = false; }
        }
    }

    // 字体检查器：选中部位 → 字体/字号 + 跟随全局；底部统一界面字体。
    public class FontInspector : UserControl
    {
        private ComboBox _fontCombo, _sizeCombo, _uiFontCombo, _uiSizeCombo;
        private Button _followBtn;
        private TextBlock _title, _overrideSummary;
        private WrapPanel _overrideList;
        private bool _loading;

        public static readonly DependencyProperty SelectedPartProperty =
            DependencyProperty.Register("SelectedPart", typeof(string), typeof(FontInspector),
                new PropertyMetadata("title", OnPartChanged));
        public string SelectedPart { get { return (string)GetValue(SelectedPartProperty); } set { SetValue(SelectedPartProperty, value); } }

        private static void OnPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((FontInspector)d).RefreshAll();
        }

        private bool _syncing;

        public FontInspector()
        {
            var sp = new StackPanel();
            _title = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            sp.Children.Add(_title);

            _fontCombo = new ComboBox { Width = 210, Margin = new Thickness(0, 0, 6, 0) };
            foreach (var f in ListAppearancePreview.SystemFontNames()) _fontCombo.Items.Add(f);
            _fontCombo.SelectionChanged += delegate
            {
                if (_loading || _fontCombo.SelectedItem == null) return;
                string name = _fontCombo.SelectedItem.ToString();
                string key = SelectedPart;
                ListFontParts.Set(key, name, ListFontParts.FontSizeOf(key));
                Config.SetFontOverridden(key, true);
                Commit();
            };
            _followBtn = new Button { Content = "跟随全局", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            _followBtn.Click += delegate
            {
                Config.SetFontOverridden(SelectedPart, false);
                Commit();
            };
            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row1.Children.Add(_fontCombo);
            row1.Children.Add(_followBtn);
            sp.Children.Add(row1);

            _sizeCombo = new ComboBox { Width = 90 };
            for (int i = 6; i <= 24; i++) _sizeCombo.Items.Add(i);
            _sizeCombo.SelectionChanged += delegate
            {
                if (_loading || _sizeCombo.SelectedItem == null) return;
                string key = SelectedPart;
                ListFontParts.Set(key, ListFontParts.FontNameOf(key), Convert.ToSingle(_sizeCombo.SelectedItem));
                Config.SetFontOverridden(key, true);
                Commit();
            };
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row2.Children.Add(new TextBlock { Text = "字号", VerticalAlignment = VerticalAlignment.Center, Width = 32 });
            row2.Children.Add(_sizeCombo);
            sp.Children.Add(row2);

            sp.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 8) });
            _overrideList = new WrapPanel();
            _overrideSummary = new TextBlock { Margin = new Thickness(0, 0, 0, 6), Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x81, 0x90)) };
            sp.Children.Add(_overrideSummary);
            sp.Children.Add(_overrideList);

            sp.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
            sp.Children.Add(new TextBlock { Text = "界面字体（列表、表头、信息栏统一使用；在预览中点选某部位可单独覆盖）", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            _uiFontCombo = new ComboBox { Width = 210, Margin = new Thickness(0, 0, 6, 0) };
            foreach (var f in ListAppearancePreview.SystemFontNames()) _uiFontCombo.Items.Add(f);
            _uiFontCombo.SelectionChanged += delegate
            {
                if (_loading || _uiFontCombo.SelectedItem == null) return;
                Config.UiFontName = _uiFontCombo.SelectedItem.ToString();
                Commit();
            };
            _uiSizeCombo = new ComboBox { Width = 90 };
            for (int i = 6; i <= 24; i++) _uiSizeCombo.Items.Add(i);
            _uiSizeCombo.SelectionChanged += delegate
            {
                if (_loading || _uiSizeCombo.SelectedItem == null) return;
                Config.UiFontSize = Convert.ToSingle(_uiSizeCombo.SelectedItem);
                Commit();
            };
            var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row3.Children.Add(_uiFontCombo);
            row3.Children.Add(_uiSizeCombo);
            sp.Children.Add(row3);
            var restore = new Button { Content = "全部恢复跟随", HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 0, 0) };
            restore.Click += delegate { Config.ListFontOverrides = ""; Config.RebuildFontOverrides(); Commit(); };
            sp.Children.Add(restore);

            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            if (!string.IsNullOrEmpty(ListFontParts.Selected) && ListFontParts.Selected != SelectedPart)
                SelectedPart = ListFontParts.Selected;
            ListFontParts.SelectedChanged += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_syncing) return;
                    _syncing = true;
                    try
                    {
                        if (!string.IsNullOrEmpty(ListFontParts.Selected)) SelectedPart = ListFontParts.Selected;
                        RefreshAll();
                    }
                    finally { _syncing = false; }
                }));
            };
            Loaded += delegate { RefreshAll(); };
        }

        private void Commit()
        {
            Config.Save();
            Program.RefreshAllPanels();
            RefreshAll();
        }

        private void RefreshAll()
        {
            _loading = true;
            try
            {
                string key = SelectedPart ?? "title";
                _title.Text = "字体 · " + ListFontParts.NameOf(key) + (Config.IsFontOverridden(key) ? "（已覆盖）" : "（跟随界面字体）");
                string fn = ListFontParts.EffectiveName(key);
                if (!_fontCombo.Items.Contains(fn)) _fontCombo.Items.Insert(0, fn);
                _fontCombo.SelectedItem = fn;
                _sizeCombo.SelectedItem = (int)Math.Round(ListFontParts.EffectiveSize(key));
                string ufn = Config.UiFontName;
                if (!_uiFontCombo.Items.Contains(ufn)) _uiFontCombo.Items.Insert(0, ufn);
                _uiFontCombo.SelectedItem = ufn;
                _uiSizeCombo.SelectedItem = (int)Math.Round(Config.UiFontSize);

                _overrideList.Children.Clear();
                var overridden = new List<string>();
                foreach (var k in ListFontParts.Keys) if (Config.IsFontOverridden(k)) overridden.Add(k);
                _overrideSummary.Text = overridden.Count == 0 ? "逐项覆盖字体/字号：暂无，全部跟随界面字体" : "逐项覆盖字体/字号：";
                foreach (var k in overridden)
                {
                    var chip = new Button { Content = ListFontParts.NameOf(k) + " ×", Margin = new Thickness(0, 0, 4, 4),
                        Padding = new Thickness(6, 1, 6, 1), Tag = k };
                    chip.Click += delegate(object s, RoutedEventArgs e)
                    {
                        string ck = (string)((Button)s).Tag;
                        Config.SetFontOverridden(ck, false);
                        Commit();
                    };
                    _overrideList.Children.Add(chip);
                }
            }
            finally { _loading = false; }
        }
    }
}
