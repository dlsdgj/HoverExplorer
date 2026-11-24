using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HoverExplorer.Wpf
{
    // 可视化触发区域编辑器（编辑对象是"一个条目"）：
    //  - list：图标 + 文件名（按名称长度）为条目，左右两条线之间的区域=触发区；
    //  - icon：单个大图标（含文件名）为条目，四条边围出的中间区域=触发区；
    // 数值支持百分比与像素两种单位（Config.TrigUnit），全 0 = 整条目触发。
    public class TriggerZoneEditor : FrameworkElement
    {
        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register("Kind", typeof(string), typeof(TriggerZoneEditor),
                new FrameworkPropertyMetadata("list",
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public string Kind
        {
            get { return (string)GetValue(KindProperty); }
            set { SetValue(KindProperty, value); }
        }

        private enum Edge { None, Left, Top, Right, Bottom }

        private Edge _dragEdge = Edge.None;
        private int _dragPct;
        private bool _dragging;
        private Edge _focusEdge = Edge.None;
        private DispatcherTimer _watch;
        private string _signature;

        private bool IsIcon { get { return string.Equals(Kind, "icon", StringComparison.OrdinalIgnoreCase); } }
        private bool UsePx { get { return (IsIcon ? Config.TrigUnitIcon : Config.TrigUnitList) == 1; } }

        public TriggerZoneEditor()
        {
            Focusable = true;
            SnapsToDevicePixels = true;
            Loaded += delegate
            {
                InvalidateVisual();
                if (_watch == null)
                {
                    _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    _watch.Tick += delegate { CheckExternalChange(); };
                }
                _signature = Signature();
                _watch.Start();
            };
            Unloaded += delegate { if (_watch != null) _watch.Stop(); };
        }

        private string Signature()
        {
            return Config.TrigUnitList + "," + Config.TrigUnitIcon + "|" + Config.TrigListLeft + "," + Config.TrigListRight +
                   "," + Config.TrigIconLeft + "," + Config.TrigIconTop + "," + Config.TrigIconRight + "," + Config.TrigIconBottom;
        }

        // 单位切换等外部改动不会通知控件，用轻量轮询保证重绘
        private void CheckExternalChange()
        {
            string s = Signature();
            if (s != _signature) { _signature = s; InvalidateVisual(); }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 360 : availableSize.Width;
            return new Size(w, IsIcon ? 130 : 60);
        }

        private static FormattedText Text(string s, double size, Brush brush, bool bold = false)
        {
            return new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
                    bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, brush);
        }

        // 可编辑条目矩形：只覆盖图标 + 文件名，不含空白列
        private Rect ItemRect
        {
            get
            {
                double w = ActualWidth, h = ActualHeight;
                Brush textBrush = Brushes.Black;
                FormattedText name = Text("Documents", 11, textBrush);
                if (IsIcon)
                {
                    double contentW = Math.Max(64, name.Width + 20) + 16;
                    double contentH = 46 + 8 + name.Height + 12;
                    contentW = Math.Min(contentW, Math.Max(40, w - 16));
                    contentH = Math.Min(contentH, Math.Max(30, h - 12));
                    double top = Math.Max(20, (h - contentH) / 2);
                    return new Rect((w - contentW) / 2, top, contentW, contentH);
                }
                double cw = 8 + 18 + 6 + name.Width + 10;
                cw = Math.Min(cw, Math.Max(30, w - 20));
                return new Rect(8, 6, cw, 24);
            }
        }

        // ---- 取值/提交 ----

        // 列表视图只认左右两条边；上/下必须返回 0，否则图标视图的上/下设置会
        // 从视觉上“同步”进列表编辑器（触发区内缩高度被图标的值连带改变）。
        private int Value(Edge e)
        {
            switch (e)
            {
                case Edge.Left: return IsIcon ? Config.TrigIconLeft : Config.TrigListLeft;
                case Edge.Right: return IsIcon ? Config.TrigIconRight : Config.TrigListRight;
                case Edge.Top: return IsIcon ? Config.TrigIconTop : 0;
                case Edge.Bottom: return IsIcon ? Config.TrigIconBottom : 0;
                default: return 0;
            }
        }

        private string KeyOf(Edge e)
        {
            switch (e)
            {
                case Edge.Left: return IsIcon ? "TrigIconLeft" : "TrigListLeft";
                case Edge.Right: return IsIcon ? "TrigIconRight" : "TrigListRight";
                case Edge.Top: return "TrigIconTop";
                case Edge.Bottom: return "TrigIconBottom";
                default: return null;
            }
        }

        private int Opposite(Edge e)
        {
            switch (e)
            {
                case Edge.Left: return IsIcon ? Config.TrigIconRight : Config.TrigListRight;
                case Edge.Right: return IsIcon ? Config.TrigIconLeft : Config.TrigListLeft;
                case Edge.Top: return Config.TrigIconBottom;
                case Edge.Bottom: return Config.TrigIconTop;
                default: return 0;
            }
        }

        private int Shown(Edge e)
        {
            return _dragging && _dragEdge == e ? _dragPct : Value(e);
        }

        // 数值=边界位置（从条目左/上侧量起），因此上限就是条目尺寸；
        // 左右（上下）不能交叉，且右/下侧至少保留 1 单位，避免拖成"整条目"。
        private int MaxFor(Edge e)
        {
            Rect item = ItemRect;
            bool horizontal = e == Edge.Left || e == Edge.Right;
            double size = horizontal ? item.Width : item.Height;
            int max = UsePx ? Math.Max(1, (int)Math.Round(size)) : 100;
            if (e == Edge.Left || e == Edge.Top)
            {
                int other = Opposite(e);
                if (other > 0) max = Math.Min(max, other - 1);
            }
            return Math.Max(0, max);
        }

        private int MinFor(Edge e)
        {
            if (e == Edge.Left || e == Edge.Top) return 0;
            int other = Opposite(e);
            return other > 0 ? other + 1 : 1;
        }

        private void Commit(Edge e, int val)
        {
            val = Math.Max(MinFor(e), Math.Min(MaxFor(e), val));
            string key = KeyOf(e);
            if (key == null) return;
            try { Cfg.Write(key, val); } catch { }
            _signature = Signature();
            InvalidateVisual();
        }

        // 边界位置→像素（编辑器内 1px = 1 DIP）；0 表示该侧取条目边缘
        private double PosPx(double size, int val)
        {
            if (val <= 0) return 0;
            double px = UsePx ? val : size * Math.Min(100, val) / 100.0;
            return Math.Min(px, size);
        }

        // 当前显示值 → 触发区在该轴上的尺寸（用于"宽度"提示）
        private int ZoneSize(double size, int from, int to)
        {
            if (!UsePx)
            {
                double px = SizePx(size, from, to);
                return size <= 0 ? 0 : (int)Math.Round(px / size * 100);
            }
            return (int)Math.Round(SizePx(size, from, to));
        }

        private double SizePx(double size, int from, int to)
        {
            double a = PosPx(size, from);
            double b = to > 0 ? PosPx(size, to) : size;
            return Math.Max(0, b - a);
        }

        // ---- 绘制 ----

        private Brush Res(string key, Brush fallback)
        {
            object o = null;
            try { o = TryFindResource(key); } catch { }
            return o as Brush ?? fallback;
        }

        private static void Folder(DrawingContext dc, Rect r, Brush fill, Pen pen)
        {
            var g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                c.BeginFigure(new Point(r.Left, r.Bottom), true, true);
                c.LineTo(new Point(r.Left, r.Top + r.Height * 0.12), true, false);
                c.LineTo(new Point(r.Left + r.Width * 0.38, r.Top + r.Height * 0.12), true, false);
                c.LineTo(new Point(r.Left + r.Width * 0.50, r.Top + r.Height * 0.34), true, false);
                c.LineTo(new Point(r.Right, r.Top + r.Height * 0.34), true, false);
                c.LineTo(new Point(r.Right, r.Bottom), true, false);
            }
            dc.DrawGeometry(fill, pen, g);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 8 || h <= 8) return;

            Brush panel = Res("PanelBrush", Brushes.White);
            Brush line = Res("LineBrush", new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xDD)));
            Brush text = Res("TextBrush", Brushes.Black);
            Brush sub = Res("SubTextBrush", new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90)));
            Brush accent = Res("AccentBrush", new SolidColorBrush(Color.FromRgb(0, 103, 192)));

            var stage = new Rect(0.5, 0.5, w - 1, h - 1);
            dc.DrawRoundedRectangle(panel, new Pen(line, 1), stage, 6, 6);
            dc.PushClip(new RectangleGeometry(stage, 6, 6));
            try
            {
                Rect item = ItemRect;
                var accentColor = accent is SolidColorBrush ? ((SolidColorBrush)accent).Color : Color.FromRgb(0, 103, 192);
                // 仿资源管理器选中态：条目范围（图标+名称）淡蓝底
                dc.DrawRoundedRectangle(new SolidColorBrush(accentColor) { Opacity = 0.12 }, null, item, 3, 3);

                var folderFill = new SolidColorBrush(Color.FromRgb(0xF7, 0xC5, 0x5A));
                var folderPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xA9, 0x36)), 1);
                if (IsIcon) DrawIconItem(dc, item, text, folderFill, folderPen);
                else DrawListRow(dc, item, text, folderFill, folderPen);

                DrawZone(dc, item, stage, accent, sub);
            }
            finally { dc.Pop(); }
        }

        private void DrawListRow(DrawingContext dc, Rect item, Brush text, Brush folderFill, Pen folderPen)
        {
            var iconR = new Rect(item.Left + 8, item.Top + (item.Height - 14) / 2, 18, 14);
            Folder(dc, iconR, folderFill, folderPen);
            FormattedText name = Text("Documents", 11, text);
            dc.DrawText(name, new Point(item.Left + 32, item.Top + (item.Height - name.Height) / 2));
        }

        private void DrawIconItem(DrawingContext dc, Rect item, Brush text, Brush folderFill, Pen folderPen)
        {
            double side = Math.Min(item.Width - 24, 46);
            var fr = new Rect(item.Left + (item.Width - side) / 2, item.Top + 8, side, side * 0.82);
            Folder(dc, fr, folderFill, folderPen);
            FormattedText name = Text("Documents", 11, text);
            dc.DrawText(name, new Point(item.Left + (item.Width - name.Width) / 2, fr.Bottom + 6));
        }

        // 触发区：两条（图标为四条）边界线之间的淡蓝区域。
        // 手柄数值是"边界位置"，因此不再逐边显示数字，直接在下方标出选中区域的宽/高。
        private void DrawZone(DrawingContext dc, Rect item, Rect stage, Brush accent, Brush sub)
        {
            Edge[] edges = IsIcon
                ? new[] { Edge.Left, Edge.Top, Edge.Right, Edge.Bottom }
                : new[] { Edge.Left, Edge.Right };

            double l = PosPx(item.Width, Shown(Edge.Left));
            double r = PosPx(item.Width, Shown(Edge.Right));
            double t = PosPx(item.Height, Shown(Edge.Top));
            double b = PosPx(item.Height, Shown(Edge.Bottom));

            bool allOff = true;
            foreach (Edge e in edges) if (Shown(e) > 0) { allOff = false; break; }

            double leftB = item.Left + l;
            double rightB = Shown(Edge.Right) > 0 ? item.Left + r : item.Right;
            double topB = item.Top + t;
            double bottomB = IsIcon && Shown(Edge.Bottom) > 0 ? item.Top + b : item.Bottom;

            var inner = new Rect(leftB, topB,
                Math.Max(0, rightB - leftB), Math.Max(0, bottomB - topB));

            var accentColor = accent is SolidColorBrush ? ((SolidColorBrush)accent).Color : Color.FromRgb(0, 103, 192);
            if (!allOff)
            {
                dc.DrawRectangle(new SolidColorBrush(accentColor) { Opacity = 0.20 }, null, inner);
                var pen = new Pen(accent, 2);
                if (l > 0) dc.DrawLine(pen, new Point(leftB, item.Top), new Point(leftB, item.Bottom));
                if (Shown(Edge.Right) > 0)
                    dc.DrawLine(pen, new Point(rightB, item.Top), new Point(rightB, item.Bottom));
                if (IsIcon && t > 0)
                    dc.DrawLine(pen, new Point(item.Left, topB), new Point(item.Right, topB));
                if (IsIcon && Shown(Edge.Bottom) > 0)
                    dc.DrawLine(pen, new Point(item.Left, bottomB), new Point(item.Right, bottomB));
            }

            DrawZoneLabel(dc, item, stage, allOff, sub, accent);
        }

        private void DrawZoneLabel(DrawingContext dc, Rect item, Rect stage, bool allOff, Brush sub, Brush accent)
        {
            string label;
            if (allOff) label = I18n.T("zone_whole");
            else
            {
                string unit = UsePx ? " px" : "%";
                int w = ZoneSize(item.Width, Shown(Edge.Left), Shown(Edge.Right));
                if (IsIcon)
                {
                    int h = ZoneSize(item.Height, Shown(Edge.Top), Shown(Edge.Bottom));
                    label = I18n.T("zone_size") + " " + w + unit + " × " + h + unit;
                }
                else label = I18n.T("zone_width") + " " + w + unit;
            }
            FormattedText t = Text(label, 10, allOff ? sub : Brushes.White);
            double pw = t.Width + 14, ph = 16;
            double x = stage.Left + (stage.Width - pw) / 2;
            double y = stage.Bottom - ph - 4;
            if (allOff)
            {
                dc.DrawText(t, new Point(x, y + 2));
                return;
            }
            var pill = new Rect(x, y, pw, ph);
            dc.DrawRoundedRectangle(accent, null, pill, 8, 8);
            dc.DrawText(t, new Point(pill.Left + 7, pill.Top + (ph - t.Height) / 2));
        }

        // ---- 交互 ----

        private Edge HitEdge(Point p)
        {
            Rect item = ItemRect;
            var reach = new Rect(item.Left - 8, item.Top - 8, item.Width + 16, item.Height + 16);
            if (!reach.Contains(p)) return Edge.None;

            double leftB = item.Left + PosPx(item.Width, Shown(Edge.Left));
            double rightB = Shown(Edge.Right) > 0
                ? item.Left + PosPx(item.Width, Shown(Edge.Right)) : item.Right;
            double topB = item.Top + PosPx(item.Height, Shown(Edge.Top));
            double bottomB = Shown(Edge.Bottom) > 0
                ? item.Top + PosPx(item.Height, Shown(Edge.Bottom)) : item.Bottom;

            double tol = 9;
            Edge best = Edge.None;
            double bestDist = double.MaxValue;
            foreach (Edge e in (IsIcon
                ? new[] { Edge.Left, Edge.Top, Edge.Right, Edge.Bottom }
                : new[] { Edge.Left, Edge.Right }))
            {
                double d;
                switch (e)
                {
                    case Edge.Left: d = Math.Abs(p.X - leftB); break;
                    case Edge.Right: d = Math.Abs(p.X - rightB); break;
                    case Edge.Top: d = Math.Abs(p.Y - topB); break;
                    default: d = Math.Abs(p.Y - bottomB); break;
                }
                if (d <= tol && d < bestDist) { best = e; bestDist = d; }
            }
            return best;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            Edge edge = HitEdge(e.GetPosition(this));
            if (edge == Edge.None) return;
            _focusEdge = edge;
            if (e.ClickCount == 2) { Commit(edge, 0); return; }
            _dragEdge = edge;
            _dragPct = Value(edge);
            _dragging = true;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = e.GetPosition(this);
            if (_dragging)
            {
                Rect item = ItemRect;
                // 数值=边界位置：一律从条目左/上侧量起
                double d = IsHorizontal(_dragEdge) ? p.X - item.Left : p.Y - item.Top;
                int val = UsePx
                    ? (int)Math.Round(d)
                    : (int)Math.Round(d / (IsHorizontal(_dragEdge) ? item.Width : item.Height) * 100);
                _dragPct = Math.Max(MinFor(_dragEdge), Math.Min(MaxFor(_dragEdge), val));
                InvalidateVisual();
                return;
            }
            Edge hover = HitEdge(p);
            Cursor = hover == Edge.Left || hover == Edge.Right ? Cursors.SizeWE
                : hover == Edge.Top || hover == Edge.Bottom ? Cursors.SizeNS
                : Cursors.Arrow;
        }

        private static bool IsHorizontal(Edge e)
        {
            return e == Edge.Left || e == Edge.Right;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            Commit(_dragEdge, _dragPct);
            _dragEdge = Edge.None;
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            Edge edge = _focusEdge;
            if (edge == Edge.None) return;
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 1;
            int delta = 0;
            if ((edge == Edge.Left || edge == Edge.Right) && (e.Key == Key.Left || e.Key == Key.Right))
                delta = e.Key == Key.Right ? step : -step;
            else if ((edge == Edge.Top || edge == Edge.Bottom) && (e.Key == Key.Up || e.Key == Key.Down))
                delta = e.Key == Key.Down ? step : -step;
            if (delta == 0) return;
            Commit(edge, Value(edge) + delta);
            e.Handled = true;
        }
    }

    // 单位切换：横排 "%" / "px" 两段，点击高亮选中（不用下拉，也不显示文字名称）
    public class UnitSwitch : FrameworkElement
    {
        public static readonly DependencyProperty KeyProperty =
            DependencyProperty.Register("Key", typeof(string), typeof(UnitSwitch),
                new FrameworkPropertyMetadata("TrigUnitList",
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public string Key
        {
            get { return (string)GetValue(KeyProperty); }
            set { SetValue(KeyProperty, value); }
        }

        private DispatcherTimer _watch;
        private int _last = -1;

        public UnitSwitch()
        {
            Focusable = false;
            Cursor = Cursors.Hand;
            SnapsToDevicePixels = true;
            Loaded += delegate
            {
                _last = Unit;
                InvalidateVisual();
                if (_watch == null)
                {
                    _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    _watch.Tick += delegate { int u = Unit; if (u != _last) { _last = u; InvalidateVisual(); } };
                }
                _watch.Start();
            };
            Unloaded += delegate { if (_watch != null) _watch.Stop(); };
        }

        // 读取 Config 中对应单位字段：0=百分比 1=像素
        private int Unit
        {
            get
            {
                try
                {
                    var f = typeof(Config).GetField(Key,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    return f == null ? 0 : Convert.ToInt32(f.GetValue(null));
                }
                catch { return 0; }
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(54, 22);
        }

        private static FormattedText Text(string s, double size, Brush brush, bool bold)
        {
            return new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
                    bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, brush);
        }

        private Brush Res(string key, Brush fallback)
        {
            object o = null;
            try { o = TryFindResource(key); } catch { }
            return o as Brush ?? fallback;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 4 || h <= 4) return;
            Brush seg = Res("SegBrush", new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xF2)));
            Brush panel = Res("PanelBrush", Brushes.White);
            Brush sub = Res("SubTextBrush", new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x90)));
            Brush accent = Res("AccentBrush", new SolidColorBrush(Color.FromRgb(0, 103, 192)));

            dc.DrawRoundedRectangle(seg, null, new Rect(0, 0, w, h), 6, 6);
            int unit = Unit;
            double cw = w / 2;
            for (int i = 0; i < 2; i++)
            {
                var r = new Rect(i * cw + 1.5, 1.5, cw - 3, h - 3);
                bool on = i == unit;
                if (on) dc.DrawRoundedRectangle(panel, null, r, 5, 5);
                FormattedText t = Text(i == 0 ? "%" : "px", 10, on ? accent : sub, on);
                dc.DrawText(t, new Point(r.Left + (r.Width - t.Width) / 2, r.Top + (r.Height - t.Height) / 2));
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            int idx = e.GetPosition(this).X < ActualWidth / 2 ? 0 : 1;
            try { Cfg.Write(Key, idx); } catch { }
            _last = idx;
            InvalidateVisual();
            e.Handled = true;
        }
    }
}
