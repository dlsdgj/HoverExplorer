using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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

        public double Value { get { return (double)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public double Min { get { return (double)GetValue(MinProperty); } set { SetValue(MinProperty, value); } }
        public double Max { get { return (double)GetValue(MaxProperty); } set { SetValue(MaxProperty, value); } }
        public double Step { get { return (double)GetValue(StepProperty); } set { SetValue(StepProperty, value); } }
        public string Format { get { return (string)GetValue(FormatProperty); } set { SetValue(FormatProperty, value); } }
        public string Suffix { get { return (string)GetValue(SuffixProperty); } set { SetValue(SuffixProperty, value); } }
        public string DisplayText { get { return (string)GetValue(DisplayTextProperty); } private set { SetValue(DisplayTextProperty, value); } }

        public Stepper()
        {
            Refresh();
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            Button minus = GetTemplateChild("PART_Minus") as Button;
            Button plus = GetTemplateChild("PART_Plus") as Button;
            if (minus != null) minus.Click += delegate { Set(Value - Step); };
            if (plus != null) plus.Click += delegate { Set(Value + Step); };
            Refresh();
        }

        private static void OnDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((Stepper)d).Refresh();
        }

        private void Refresh()
        {
            string fmt = string.IsNullOrEmpty(Format) ? "0" : Format;
            DisplayText = Value.ToString(fmt, CultureInfo.InvariantCulture) + (Suffix ?? "");
        }

        private void Set(double v)
        {
            Value = Math.Round(Math.Max(Min, Math.Min(Max, v)), 4);
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

        public string Label { get { return (string)GetValue(LabelProperty); } set { SetValue(LabelProperty, value); } }
        public string Text { get { return (string)GetValue(TextProperty); } set { SetValue(TextProperty, value); } }
        public Brush Brush { get { return (Brush)GetValue(BrushProperty); } set { SetValue(BrushProperty, value); } }

        public ColorRow()
        {
            UpdateBrush();
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
}
