using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 音频卡片 V4：格式/大小 chip 胶囊 + 圆形播放/暂停按钮（自绘，配色由外部注入）。
    internal sealed class AudioChip : Control
    {
        private Color _ink = Color.Gray;
        public Color Ink { get { return _ink; } set { _ink = value; Invalidate(); } }
        private Color _bg = Color.FromArgb(20, 128, 128, 128);
        public Color ChipBg { get { return _bg; } set { _bg = value; Invalidate(); } }
        private Color _border = Color.FromArgb(40, 128, 128, 128);
        public Color Border { get { return _border; } set { _border = value; Invalidate(); } }

        internal AudioChip(string text)
        {
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            float rad = Height / 2f;
            using (var path = new GraphicsPath())
            {
                path.AddArc(r.X, r.Y, 2 * rad, 2 * rad, 180, 90);
                path.AddArc(r.Right - 2 * rad, r.Y, 2 * rad, 2 * rad, 270, 90);
                path.AddArc(r.Right - 2 * rad, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 0, 90);
                path.AddArc(r.X, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 90, 90);
                path.CloseFigure();
                using (var br = new SolidBrush(ChipBg)) g.FillPath(br, path);
                using (var pen = new Pen(Border)) g.DrawPath(pen, path);
                using (var fg = new SolidBrush(Ink))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(Text, Font, fg, r, sf);
                }
            }
        }
    }

    // 圆形播放/暂停按钮：accent 底 + 前景色三角/双竖条。
    internal sealed class RoundPlayButton : Control
    {
        private bool _playing;
        private Color _accent = Color.FromArgb(0x8a, 0x5a, 0x3c);
        private Color _fore = Color.White;
        public bool Playing { get { return _playing; } set { if (_playing == value) return; _playing = value; Invalidate(); } }
        public Color Accent { get { return _accent; } set { _accent = value; Invalidate(); } }
        public Color Fore { get { return _fore; } set { _fore = value; Invalidate(); } }

        internal RoundPlayButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Math.Min(Width, Height) - Dpi.S(2);
            if (d < 4) return;
            float x = (Width - d) / 2f, y = (Height - d) / 2f;
            using (var br = new SolidBrush(_accent))
                g.FillEllipse(br, x, y, d, d);
            // 白色核心按原型 SVG 比例绘制：viewBox 16，图标 14/30 圆直径
            // play: M4 2.5v11l9-5.5z；pause: 两条 3.2×11、rx=1 的圆角竖条
            using (var fg = new SolidBrush(_fore))
            {
                float cx = Width / 2f, cy = Height / 2f;
                float u = d * (14f / 30f) / 16f;   // 1 个 viewBox 单位
                if (!_playing)
                {
                    using (var path = new GraphicsPath())
                    {
                        path.AddPolygon(new[] {
                            new PointF(cx - 4f * u, cy - 5.5f * u),
                            new PointF(cx - 4f * u, cy + 5.5f * u),
                            new PointF(cx + 5f * u, cy)
                        });
                        g.FillPath(fg, path);
                    }
                }
                else
                {
                    float bw = 3.2f * u, bh = 11f * u, rx = Math.Max(0.5f, 1f * u);
                    float top = cy - bh / 2f;
                    using (var p1 = RoundRect(cx - 4.5f * u, top, bw, bh, rx)) g.FillPath(fg, p1);
                    using (var p2 = RoundRect(cx + 1.3f * u, top, bw, bh, rx)) g.FillPath(fg, p2);
                }
            }
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d2 = Math.Min(2 * r, Math.Min(w, h));
            p.AddArc(x, y, d2, d2, 180, 90);
            p.AddArc(x + w - d2, y, d2, d2, 270, 90);
            p.AddArc(x + w - d2, y + h - d2, d2, d2, 0, 90);
            p.AddArc(x, y + h - d2, d2, d2, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
