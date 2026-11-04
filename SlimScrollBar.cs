using System;
using System.Drawing;
using System.Windows.Forms;

namespace HoverExplorer
{
    /// <summary>
    /// 直角、扁平的自绘滚动条（Thin：无轨道，滑块 4px，悬停/拖动变 8px）。
    /// </summary>
    internal class SlimScrollBar : Control
    {
        public enum BarStyle { Thin, Line, Block }

        public BarStyle Style { get { return _style; } set { _style = value; Invalidate(); } }
        public Color ThumbColor = Color.FromArgb(170, 150, 155);
        public Color ThumbHotColor = Color.FromArgb(120, 100, 108);
        public Color TrackColor = Color.FromArgb(30, 0, 0, 0);
        public event EventHandler Scroll;

        BarStyle _style = BarStyle.Thin;
        int _max = 100, _large = 10, _value;
        bool _hot, _drag;
        int _dragOffset;

        public SlimScrollBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Width = 10;
            Dock = DockStyle.Right;
            BackColor = Color.Transparent;
        }

        // 滚动条贴在可调大小窗体（设置窗）右缘时会盖住尺寸调整热区：
        // 滑块本体（含余量）仍归滚动条拖拽，其余贴缘区域转交宿主窗体（PopupForm 等其他宿主不受影响）
        protected override void WndProc(ref Message m)
        {
            var sf = FindForm() as HoverExplorer.SettingsForm;
            if (sf != null)
            {
                const int WM_NCHITTEST = 0x0084;
                const int WM_NCLBUTTONDOWN = 0x00A1;
                if (m.Msg == WM_NCHITTEST)
                {
                    long lp = m.LParam.ToInt64();
                    var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    var tr = ThumbRect(); tr.Inflate(2, 2);
                    if (!tr.Contains(p) && sf._fnNcHitTest(ref m)) return;
                }
                else if (m.Msg == WM_NCLBUTTONDOWN) { if (sf.ForwardSizeDrag(ref m)) return; }
            }
            base.WndProc(ref m);
        }

        public int Maximum
        {
            get { return _max; }
            set { _max = Math.Max(1, value); Value = _value; Sync(); }
        }
        public int LargeChange
        {
            get { return _large; }
            set { _large = Math.Max(1, value); Value = _value; Sync(); }
        }
        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(0, Math.Min(value, Math.Max(0, _max - _large)));
                if (v == _value) return;
                _value = v; Invalidate();
                if (!_suppressScroll && Scroll != null) Scroll(this, EventArgs.Empty);
            }
        }
        internal bool _suppressScroll;
        internal bool IsDragging { get { return _drag; } }

        void Sync() { Visible = _max > _large; Invalidate(); }

        Rectangle ThumbRect()
        {
            int h = Height;
            int thumbH = Math.Max(24, (int)((long)h * _large / _max));
            int range = h - thumbH;
            int span = Math.Max(1, _max - _large);
            int y = (int)((long)range * _value / span);
            bool wide = _hot || _drag;
            int w = _style == BarStyle.Thin ? (wide ? 8 : 4) : 6;
            return new Rectangle(Width - w - 1, y, w, thumbH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; // 直角，无抗锯齿边缘
            // 先填背景：避免 SupportsTransparentBackColor 重画父窗体背景（tint 的浅色混色），
            // 让滚动条与压在下面的行区容器同色。轨道仍是半透明黑叠加，视觉效果不变
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, Width, Height);
            if (_style == BarStyle.Block)
                using (var b = new SolidBrush(TrackColor)) g.FillRectangle(b, Width - 8, 0, 8, Height);
            else if (_style == BarStyle.Line)
                using (var b = new SolidBrush(TrackColor)) g.FillRectangle(b, Width - 4, 0, 1, Height);
            using (var b = new SolidBrush(_hot || _drag ? ThumbHotColor : ThumbColor))
                g.FillRectangle(b, ThumbRect());
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var t = ThumbRect();
            if (t.Contains(e.Location)) { _drag = true; _dragOffset = e.Y - t.Y; Capture = true; }
            else Value += e.Y < t.Y ? -_large : _large; // 点轨道翻页
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_drag) return;
            var t = ThumbRect();
            int range = Math.Max(1, Height - t.Height);
            int y = Math.Max(0, Math.Min(e.Y - _dragOffset, range));
            Value = (int)((long)y * Math.Max(0, _max - _large) / range);
        }

        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Capture = false; Invalidate(); }
    }
}