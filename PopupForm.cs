using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 抗锯齿 Label：双缓冲位图上 DrawString 默认不抗锯齿，需显式设 TextRenderingHint
    internal class SmoothLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            base.OnPaint(e);
        }
    }

    // 按钮/图标提示窗口：无边框、不抢前台、不在任务栏，位置固定在鼠标指针上方
    internal class TipForm : Form
    {
        private SmoothLabel _lbl;

        public TipForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Config.BG;
            _lbl = new SmoothLabel
            {
                AutoSize = false,
                ForeColor = Config.FG,
                BackColor = Config.BG,
                Location = new Point(5, 3),
                Font = new Font(Config.FontName, Config.FontSize),
                UseCompatibleTextRendering = true,
            };
            Controls.Add(_lbl);
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

        public void SetText(string t)
        {
            _lbl.Text = t;
            // 不支持换行的 MeasureText，逐行量出来再加总（标题提示是“路径 + 改名说明”两行）
            var g = CreateGraphics();
            int w = 0, h = 0;
            foreach (var ln in t.Split('\n'))
            {
                var s = TextRenderer.MeasureText(g, ln, _lbl.Font);
                if (s.Width > w) w = s.Width;
                h += s.Height;
            }
            g.Dispose();
            _lbl.Size = new Size(w + 2, h + 1);
            ClientSize = new Size(_lbl.Width + 10, _lbl.Height + 6);
        }

        // 指针上方 18px；上方放不下就落到指针下方，并始终留在工作区内
        public void Follow()
        {
            var p = Cursor.Position;
            var wa = Screen.FromPoint(p).WorkingArea;
            int x = p.X - Width / 2, y = p.Y - Height - 18;
            if (x + Width > wa.Right) x = wa.Right - Width - 2;
            if (x < wa.Left) x = wa.Left + 2;
            if (y < wa.Top) y = p.Y + 22;
            if (Location.X != x || Location.Y != y) Location = new Point(x, y);
        }

        public void ShowTip() { if (!Visible) Visible = true; }
        public void HideTip() { if (Visible) Visible = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            // 提示文字一律 1px 细边：圆角模式的非客户区粗描边（FramePad 还会内缩裁掉文字）
            // 对小尺寸提示窗太重，不需要跟随 WindowCorners
            using (var pen = new Pen(Config.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // 列表/预览窗口的按钮提示统一管理：受 Config.ShowTips 开关控制，位置由 TipForm 控制
    internal static class Tips
    {
        private static readonly Dictionary<Control, string> _map = new Dictionary<Control, string>();
        private static readonly Timer _timer = new Timer { Interval = 300 };
        private static TipForm _form;
        private static Control _cur;

        static Tips()
        {
            _timer.Tick += (s, e) => { _timer.Stop(); if (_cur != null) ShowAt(_cur); };
        }

        public static void Set(Control c, string text)
        {
            if (c == null || c.IsDisposed) return;
            if (!Config.ShowTips || string.IsNullOrEmpty(text))
            {
                if (_map.ContainsKey(c)) { Unbind(c); _map.Remove(c); }
                return;
            }
            if (!_map.ContainsKey(c)) Bind(c);
            _map[c] = text;
        }

        // 开关关闭 / 面板销毁时立刻摘掉全部提示，不依赖控件重建
        public static void ApplyConfig()
        {
            if (Config.ShowTips) return;
            foreach (var c in _map.Keys.ToArray()) { Unbind(c); _map.Remove(c); }
            Hide();
        }

        private static void Bind(Control c)
        {
            c.MouseEnter += OnEnter;
            c.MouseMove += OnMove;
            c.MouseLeave += OnLeave;
            c.MouseDown += OnDown;
            c.Disposed += OnDisposed;
        }

        private static void Unbind(Control c)
        {
            c.MouseEnter -= OnEnter;
            c.MouseMove -= OnMove;
            c.MouseLeave -= OnLeave;
            c.MouseDown -= OnDown;
            c.Disposed -= OnDisposed;
        }

        private static void OnEnter(object s, EventArgs e)
        {
            _cur = s as Control;
            _timer.Stop(); _timer.Start();
        }

        private static void OnMove(object s, EventArgs e)
        {
            if (_cur == s && _form != null && _form.Visible) _form.Follow();
        }

        private static void OnLeave(object s, EventArgs e) { if (_cur == s) Hide(); }

        // 点了按钮就不再显示提示（比如点了固定按钮，提示不该一直挂着）
        private static void OnDown(object s, EventArgs e) { if (_cur == s) Hide(); }

        private static void OnDisposed(object s, EventArgs e)
        {
            var c = s as Control;
            if (c == null) return;
            _map.Remove(c);
            if (_cur == c) Hide();
        }

        private static void ShowAt(Control c)
        {
            // 菜单打开期间不弹提示：提示窗口是 TopMost，会盖到菜单项上（复制按钮悬停即弹菜单）
            if (Program.ShellMenuOpen) { _cur = null; return; }
            string t;
            if (!_map.TryGetValue(c, out t)) return;
            if (_form == null) _form = new TipForm();
            _form.SetText(t);
            _form.Follow();
            _form.ShowTip();
        }

        private static void Hide()
        {
            _timer.Stop();
            _cur = null;
            if (_form != null) _form.HideTip();
        }

        // 一次性提示（门控发现提示用）：光标旁显示 ms 毫秒后自动消失，与控件悬停提示共用 TipForm
        private static Timer _flashTimer;
        public static void Flash(string text, int ms)
        {
            if (_form == null) _form = new TipForm();
            _form.SetText(text);
            _form.Follow();
            _form.ShowTip();
            if (_flashTimer == null) { _flashTimer = new Timer(); _flashTimer.Tick += (s2, e2) => { _flashTimer.Stop(); Hide(); }; }
            _flashTimer.Interval = Math.Max(300, ms);
            _flashTimer.Stop(); _flashTimer.Start();
        }

        // 提示窗口也要参与每 Tick 提顶：面板/预览会被强制提到 TopMost 最前，否则它反而被盖住
        public static void EnsureTopmost()
        {
            if (_form == null || !_form.Visible || !_form.IsHandleCreated) return;
            NativeMethods.SetWindowPos(_form.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    // 文件夹音频列表：原生 ListBox，使用原生垂直滚动条（不再隐藏/替换为自定义 SlimScrollBar）
    internal class FolderListBox : ListBox
    {
    }

    // 自管偏移滚动：viewport（本体，固定可视区）+ content（内容层，容纳全部行，Y=-offset 偏移）。
    // 不用 AutoScroll：隐藏原生滚动条（ShowScrollBar）会断掉 Scroll 事件链路（滑块钉在顶部不动），
    // 且每次滚动布局反复显隐滚动条造成卡顿。滚轮走 OnMouseWheel + 15ms 缓动动画（平滑滚动）。
    internal class SlimScrollPanel : Panel
    {
        private readonly Panel _content;
        private int _offset;
        private int _target;
        private Timer _anim;
        public event EventHandler ScrollChanged;   // offset 变化 → 外部同步 SlimScrollBar

        public SlimScrollPanel()
        {
            DoubleBuffered = true;
            AutoScroll = false;
            // 行区真实可见背景：所有行都加在 _content 上、它盖满整个行区，
            // 故必须用配色方案背景色（ListCanvas，跟随方案）而非主题色 Config.BG，
            // 否则 tint 会把 Config.BG 换成浅色 lbg，行区四周/滚动条让位列露出浅色
            _content = new ContentPanel { BackColor = Config.ListCanvas, Tag = "noTint", Location = new Point(0, 0) };
            Controls.Add(_content);
        }

        private class ContentPanel : Panel { public ContentPanel() { DoubleBuffered = true; } }

        public Panel Content { get { return _content; } }
        public int ContentScrollPos { get { return _offset; } }
        public int ContentHeight { get { return _content.Height; } }
        public bool CanScroll { get { return _content.Height > Math.Max(1, Height); } }
        public int ContentCount { get { return _content.Controls.Count; } }

        public void AddContent(Control c) { _content.Controls.Add(c); }

        public void SetContentHeight(int h)
        {
            _content.Size = new Size(Math.Max(0, Width), Math.Max(0, h));
            ApplyOffset(_offset);   // 内容变矮时把超限偏移拉回
        }

        private int Clamp(int v)
        {
            return Math.Max(0, Math.Min(v, Math.Max(0, _content.Height - Math.Max(1, Height))));
        }

        // 瞬跳（拖动滑块/搜索定位/恢复滚动位置）
        public void SetOffset(int v) { _target = Clamp(v); StopAnim(); ApplyOffset(_target); }

        // 缓动（滚轮）：每帧走剩余距离的 1/3，最小步 1px
        public void ScrollBy(int d) { _target = Clamp(_target + d); EnsureAnim(); }

        private void EnsureAnim()
        {
            if (_anim == null)
            {
                _anim = new Timer { Interval = 15 };
                _anim.Tick += delegate(object s, EventArgs e)
                {
                    int diff = _target - _offset;
                    if (Math.Abs(diff) <= 2) { StopAnim(); ApplyOffset(_target); return; }
                    int step = diff / 3;
                    if (step == 0) step = diff > 0 ? 2 : -2;
                    ApplyOffset(_offset + step);
                };
            }
            _anim.Start();
        }

        private void StopAnim() { if (_anim != null) _anim.Stop(); }

        private void ApplyOffset(int v)
        {
            _offset = Clamp(v);
            _content.Location = new Point(0, -_offset);
            if (ScrollChanged != null) ScrollChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollBy(-(e.Delta / 120) * 72);   // 一格约三行（行高≈24）
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _content.Width = Math.Max(0, Width);
            ApplyOffset(_offset);
        }
    }

    internal class RowLabel : Label
    {
        public string Full;
        public new string Name;
        public bool IsDir;
        public bool Selected;
        public Color OrigBg;
        public Color OrigFg;
        public Image Icon;
        public string SizeText;
        public int OriginalTop;        // 搜索过滤收拢前的原 Y
        public bool OriginalTopSet;   // false=未在过滤态（int 默认 0 无法区分原位 0）
        // 互换布局：分栏分隔线在行内的 X 坐标（左侧安全区右界），拖线时由面板实时更新
        public int SplitX;
        // 行外缘内缩：行区已整体内缩 ListPadLr，行内不再重复留出这段（大小条/大小文字贴行缘，
        // 与顶部内容右缘对齐）；保留常量便于将来微调
        private const int PadEdge = 0;
        // 斑马纹行号（构建时赋值，偶数行用 ListZb）+ 大小背景条比例(log10 比，文件夹=0)+大小列宽
        public int RowIndex;
        public float SizeRatio;
        public float SizeColW;
        // 序号叠加：指针悬停某行时在该行图标位显示 1 起行号（与行内操作按钮同生共死），
        // 移开行即恢复图标。只改绘制层：名称/搜索/中间省略/图标缓存都不受影响
        public bool ShowIndex;

        // 序号字体：固定 Courier New（等宽，与行字体区分便于扫读行号），
        // 比行字体小一号半（3 位数也要塞进 16~20px 图标槽），按字号缓存
        private static Font _idxFont;
        private static float _idxFontSize;
        private static Font IndexFont()
        {
            float sz = Math.Max(6f, Config.FontSize - 1.5f);
            if (_idxFont == null || _idxFontSize != sz)
            {
                if (_idxFont != null) _idxFont.Dispose();
                _idxFont = new Font("Courier New", sz);
                _idxFontSize = sz;
            }
            return _idxFont;
        }

        // 图标位画序号（左对齐 20px 槽；淡色=ListMu 与大小文字同层级）
        private void DrawIndex(Graphics g, int x)
        {
            // GDI+ DrawString（与名称/大小同一渲染路径）：TextRenderer(GDI) 小字号下笔画粗粝显锯齿
            var f = IndexFont();
            float ly = (Height - f.GetHeight(g)) / 2;
            using (var b = new SolidBrush(Config.ListMu))
                g.DrawString((RowIndex + 1).ToString(), f, b, new RectangleF(x, ly, 20, f.GetHeight(g)), StringFormat.GenericTypographic);
        }

        // 名称单行中间省略："前半…后半"（Finder 风格，左 60% 右 40%），二分找最大保留字符数
        // 测量与行绘制统一走 TextRenderer（GDI），保证"量出来放得下 = 画出来放得下"
        internal static string MidEllipsis(Graphics g, string s, Font f, float maxW)
        {
            if (string.IsNullOrEmpty(s)) return s;
            Func<string, float> measure = t => TextRenderer.MeasureText(g, t, f,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            if (measure(s) <= maxW) return s;
            const string ell = "…";
            float ellW = measure(ell);
            if (maxW <= ellW) return ell;
            Func<int, string> build = delegate(int k)
            {
                int a = Math.Max(1, (int)Math.Round(k * 0.6));
                int b = Math.Max(1, k - a);
                if (a > s.Length) a = s.Length;
                if (b > s.Length - a) b = s.Length - a;
                if (b < 1) return s.Substring(0, a) + ell;
                return s.Substring(0, a) + ell + s.Substring(s.Length - b);
            };
            int lo = 1, hi = Math.Max(1, s.Length - 1), best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (measure(build(mid)) <= maxW) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best > 0 ? build(best) : ell;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            // 互换布局=分栏：竖分隔线贯穿行，左栏画大小/子项数，右栏图标贴线、名称单行中间省略
            if (Config.RowSwapSides)
            {
                var g = e.Graphics;
                // 大小背景条（文件，文件夹不画）：自左缘内缩 PadEdge 起向右，不越过分隔线
                if (!IsDir && SizeRatio > 0 && SizeColW > 0)
                {
                    float barW = Math.Min(SizeColW * SizeRatio, SplitX - 2 - PadEdge);
                    if (barW > 0)
                        using (var b = new SolidBrush(Config.ListBar))
                            g.FillRectangle(b, PadEdge, 0, barW, Height);
                }
                int split = SplitX;
                if (split >= 0 && split < Width - 4)
                {
                    using (var p = new Pen(Config.Border))
                        g.DrawLine(p, split, 0, split, Height);
                }
                if (!string.IsNullOrEmpty(SizeText))
                {
                    // GDI+ 绘制（与名称同路径同垂直居中公式），TextRenderer(GDI) 笔画粗粝显锯齿
                    float lhS = Font.GetHeight(g);
                    var rcS = new RectangleF(PadEdge, (Height - lhS) / 2, Math.Max(0, SplitX - 2 - PadEdge), lhS);
                    using (var b = new SolidBrush(Config.ListMu))
                        g.DrawString(SizeText, Font, b, rcS, StringFormat.GenericTypographic);
                }
                if (!string.IsNullOrEmpty(Name))
                {
                    // 图标紧贴分隔线右侧，名称自图标右侧起单行排布，放不下中间省略
                    float textX = split + 3;
                    if (ShowIndex) { DrawIndex(g, split + 1); textX = split + 3 + 16 + 3; }
                    else if (Icon != null)
                    {
                        g.DrawImage(Icon, split + 3, (Height - Icon.Height) / 2);
                        textX = split + 3 + Icon.Width + 3;
                    }
                    float maxW = Width - 6 - textX;
                    if (maxW > 8)
                    {
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        var sf = StringFormat.GenericTypographic;
                        float lh = Font.GetHeight(g);
                        using (var b = new SolidBrush(ForeColor))
                            g.DrawString(MidEllipsis(g, Name, Font, maxW), Font, b, textX, (Height - lh) / 2, sf);
                    }
                }
                return;
            }
            if (ShowIndex) DrawIndex(e.Graphics, 2);
            else if (Icon != null)
                e.Graphics.DrawImage(Icon, 4, (Height - Icon.Height) / 2);
            // 大小背景条（文件，文件夹不画）：自右缘内缩 PadEdge 向左延伸
            if (!IsDir && SizeRatio > 0 && SizeColW > 0)
            {
                float barW = SizeColW * SizeRatio;
                if (barW > 0)
                    using (var b = new SolidBrush(Config.ListBar))
                        e.Graphics.FillRectangle(b, Width - PadEdge - barW, 0, barW, Height);
            }
            if (!string.IsNullOrEmpty(SizeText))
            {
                // GDI+ 右对齐：与名称同一渲染路径/垂直居中公式（TextRenderer(GDI) 笔画粗粝显锯齿）
                float lhS = Font.GetHeight(e.Graphics);
                float wS = e.Graphics.MeasureString(SizeText, Font, PointF.Empty, StringFormat.GenericTypographic).Width;
                using (var b = new SolidBrush(Config.ListMu))
                    e.Graphics.DrawString(SizeText, Font, b, Width - PadEdge - wS, (Height - lhS) / 2, StringFormat.GenericTypographic);
            }
            // 名称单行显示、过长时中间省略（Label 文本会换行，故走自绘）
            if (!string.IsNullOrEmpty(Name))
            {
                float textX = Padding.Left;                       // 图标位之后
                float maxW = Width - Padding.Left - Padding.Right; // 右侧留大小文本列
                if (maxW > 8)
                {
                    e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    var sf = StringFormat.GenericTypographic;
                    float lh = Font.GetHeight(e.Graphics);
                    using (var b = new SolidBrush(ForeColor))
                        e.Graphics.DrawString(MidEllipsis(e.Graphics, Name, Font, maxW), Font, b, textX, (Height - lh) / 2, sf);
                }
            }
        }
    }

    internal class BorderLabel : Label
    {
        public Color BorderColor = Config.Border;
        // 排序箭头：0 无 / 1 升 / 2 降。画在右侧固定预留区，Text 恒定 → 按钮宽度恒定无横向位移
        public int SortArrow;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(BorderColor, 1))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (SortArrow > 0)
            {
                int cx = Width - 8, cy = Height / 2;
                var pts = SortArrow == 1
                    ? new Point[] { new Point(cx - 3, cy + 2), new Point(cx + 3, cy + 2), new Point(cx, cy - 3) }
                    : new Point[] { new Point(cx - 3, cy - 2), new Point(cx + 3, cy - 2), new Point(cx, cy + 3) };
                using (var b = new SolidBrush(ForeColor))
                    e.Graphics.FillPolygon(b, pts);
            }
        }
    }


    // 音频操作栏：进度以浅色覆盖整条操作栏，底部 2px 实线更清晰。
    // 子控件（文件名/时间/频谱）背景透明，覆盖层才能透出来
    internal class AudioBarPanel : Panel
    {
        public bool ShowProgress;
        public double Ratio;

        public AudioBarPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw, true);
        }

        // 画在背景层：文件名/时间等子控件背景透明，会向父级取背景，
        // 覆盖层只有落在 OnPaintBackground 才不会在文字周围留下底色方块
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (!ShowProgress) return;
            int w = Width, h = Height;
            int fw = (int)Math.Round(w * Ratio);
            if (fw > 0)
                using (var b = new SolidBrush(Color.FromArgb(30, Config.FG)))
                    g.FillRectangle(b, 0, 0, fw, h);
            using (var b = new SolidBrush(Color.FromArgb(45, Config.FG)))
                g.FillRectangle(b, 0, h - 2, w, 2);
            if (fw > 0)
                using (var b = new SolidBrush(Config.Accent))
                    g.FillRectangle(b, 0, h - 2, fw, 2);
        }
    }

    internal class PreviewForm : Form
    {
        // 不抢前台：预览仅为悬停展示，激活会打断当前窗口的焦点
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win11.Apply(Handle, Config.IsDark, Config.Border);

        }
        private Image _img;
        private bool _pinned;
        private AudioBarPanel _toolbar;
        private Label _iconLbl;
        private Label _nameLbl;
        private Label _metaLbl;
        private Label _pinBtn;

        private Label _copyBtn;
        private Label _saveBtn;
        private Label _pasteBtn;   // textcard 专用：粘贴到原焦点窗口（PinCardForm 注入 PasteAction）
        public Action PasteAction;
        private Panel _scrollPanel;
        private Control _content;

        public string FilePath;
        public Action<string> OnFileSwitched;
        private Label _pinLaunchBtn;
        private double _zoom = 1.0;
        private WheelFilter _wheelFilter;

        // ===== 音频预览专用（操作栏：播放键+全量波形；悬停展开信息区：标题/时间/meta）=====
        internal IMediaSession _audio;
        private Label _audioPlayBtn;
        private MediaPreview.SpeedRuler _audioRuler;   // 音频倍速刻度尺（展开时显示）
        private Label _audioSpdHint;    // 右上角速度提示浮层
        private Label _audioCloseBtn;    // 关闭预览窗
        private Label _audioTimeLbl;
        private Label _audioMetaLbl;
        private Label _audioNameLbl;
        private WaveView _audioViz;
        private AudioMeter _audioMeter;
        private WaveDecoder _waveDec;                          // 全量波形（MF 静默解码）
        private readonly float[] _waveData = new float[720];   // 环回渐进包络（解码失败时回退）
        private string _wavePath;
        private Timer _audioUiTimer;
        private Timer _audioExpandTimer;   // 悬停操作栏 300ms → 展开
        private Timer _audioFoldTimer;     // 离开 ~450ms → 收起（固定态；未固定态整个窗口由预览关闭逻辑收走）
        private int _audioFoldMiss;
        private Panel _audioBody;
        private bool _audioExpanded;
        private const int AudioInfoH = 88;
        // 操作栏空白处点击=seek、拖动=移动窗口：按下记录起点，超过阈值才进入系统移动
        private bool _audioBarDown;
        private bool _audioBarMoved;
        private Point _audioBarStart;
        private int _audioBarDownX;
        private float _audioPhase;
        private bool _meterOn;

        private bool _textDirty;

        // ===== 文件夹播放模式（音频）：滚轮进入，滚动切歌；☰ 按钮展开音频列表+循环模式 =====
        private bool _folderMode;                  // 进入后持续到关窗；滚动=切歌
        private List<string> _folderTracks;        // 同目录音频文件，名称自然排序（默认排序）
        private string _folderDir;
        private int _folderIdx;
        private int _loopMode;                     // 0=顺序(播完即止) 1=列表循环 2=单曲循环
        private Label _folderBtn;
        private Panel _folderPanel;
        private Panel _folderHead;
        private FolderListBox _folderList;
        private Label _loopBtn;
        private Label _folderHeadLbl;
        private bool _folderListOn;

        private TipForm _folderTip;
        private Timer _folderTipTimer;
        private const int FolderRowH = 22;
        private const int FolderMaxRows = 8;

        private Timer _unpinCloseTimer;

        private Timer _sizeSaveTimer;
        // 自定义内容类型：video / audio / svg（决定用哪套尺寸记忆）
        private string _mediaKind = "svg";
        internal string MediaKind { get { return _mediaKind; } }
        // 复制按钮悬停弹出的菜单（每次新建，留引用才能在鼠标移开后收起）
        private ContextMenuStrip _copyMenu;

        private int _toolbarH = 34;
        // 区分预览窗的“挪位置”与“改尺寸”，避免挪动时把当前尺寸写进记忆
        private Size _pvSizeMoveStart;

        // 所在目录改名后，同步预览窗里的文件路径
        internal void RebaseFilePath(string oldPrefix, string newPrefix)
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            if (string.Equals(FilePath, oldPrefix, StringComparison.OrdinalIgnoreCase)) FilePath = newPrefix;
            else if (FilePath.StartsWith(oldPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                FilePath = newPrefix + FilePath.Substring(oldPrefix.Length);
        }



        public bool Pinned { get { return _pinned; } }

        public PreviewForm(Image img)
            : this()
        {
            _img = img;
            var pb = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill };
            _content = pb;
            _scrollPanel.Controls.Add(pb);
            BindResize(pb);
            ClientSize = new Size(img.Width, img.Height);
            ApplyImageSize(img.Width, img.Height);

        }

        public PreviewForm(Image img, int displayW, int displayH)
            : this()
        {
            _img = img;
            var pb = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill };
            _content = pb;
            _scrollPanel.Controls.Add(pb);
            BindResize(pb);
            ClientSize = new Size(displayW, displayH);
            ApplyImageSize(displayW, displayH);

        }

        public PreviewForm(string text, long size)
            : this()
        {
            var tb = new RichTextBox
            {
                Font = new Font("Microsoft YaHei", 9),
                BackColor = Config.BG,
                ForeColor = Config.FG,
                BorderStyle = BorderStyle.None,
                ReadOnly = false,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                Dock = DockStyle.Fill,
            };
            tb.Text = text;
            tb.SelectionStart = 0;
            tb.SelectionLength = 0;
            _content = tb;
            _scrollPanel.Controls.Add(tb);
            BindResize(tb);
            Win11.ApplyScrollTheme(tb, Config.IsDark);
            tb.TextChanged += (s, e) =>
            {
                if (!_textDirty) { _textDirty = true; _saveBtn.Visible = true; UpdateToolbarBtnPos(); }
            };

            using (var g = CreateGraphics())
            {
                var sz = g.MeasureString("W", tb.Font);
                int lineH = (int)sz.Height + 1;
                int initH = lineH * Config.TextPreviewLines + 4;
                int maxW = 0;
                foreach (var line in text.Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    var w = g.MeasureString(line, tb.Font);
                    if (w.Width > maxW) maxW = (int)w.Width;
                }
                ClientSize = new Size(Math.Max(Math.Min(maxW + 20, 480), 240), Math.Min(initH, 320));
            }

        }

        // 自定义内容面板（视频/音频播放器 / SVG 浏览器）
        // kind: video / audio / svg —— 视频与音频使用各自独立的尺寸记忆
        public PreviewForm(Control content, int w, int h, string kind)
            : this()
        {
            _content = content;
            _mediaKind = string.IsNullOrEmpty(kind) ? "svg" : kind;
            if (_mediaKind == "audio")
            {
                SetupAudio(content, w);
                return;
            }
            // 关键：填充滚动面板，窗口调整大小时内容（视频画面）才会跟着变
            content.Dock = DockStyle.Fill;
            _scrollPanel.Controls.Add(content);
            BindResize(content);
            ApplyContentSize(w, h);
        }

        // 音频预览：默认折叠成一条操作栏（34px），向下拖拽展开频谱与 meta。
        // 操作栏接管播放键、进度覆盖层、时间与频谱，复制/固定为卡片按钮不显示
        private void SetupAudio(Control hostPanel, int contentW)
        {
            _audio = hostPanel.Tag as IMediaSession;
            // WPF 宿主必须留在可见控件树里才会持续播放：塞进操作栏左上角 1×1 区域，
            // 被操作栏盖住不可见，但折叠/展开都不影响音频输出
            hostPanel.Dock = DockStyle.None;
            hostPanel.Size = new Size(1, 1);
            hostPanel.Location = new Point(0, 0);
            _toolbar.Controls.Add(hostPanel);
            hostPanel.SendToBack();

            _iconLbl.Visible = false;
            _copyBtn.Visible = false;
            _pinLaunchBtn.Visible = false;
            _metaLbl.Visible = false;
            _nameLbl.Visible = false;   // 标题移到展开信息区
            _pinBtn.BackColor = Color.Transparent;
            _toolbar.ShowProgress = false;   // 进度由波形呈现
            _scrollPanel.AutoScroll = false;   // 音频窗：Dock=Fill 子控件高度由 UpdateAudioHeight 精确管理，关 AutoScroll 避免 Visible 切换时滚动条显隐抖动

            _audioPlayBtn = new Label
            {
                Size = new Size(22, 18),
                Location = new Point(6, 8),
                Text = _audio != null && _audio.IsPlaying ? "❚❚" : "▶",
                Font = new Font("Microsoft YaHei", 9f),
                ForeColor = Config.FG,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            _audioPlayBtn.Click += delegate
            {
                if (_audio == null) return;
                _audio.Toggle();
                UpdateAudioUi();
            };
            _toolbar.Controls.Add(_audioPlayBtn);

            // 倍速刻度尺（常显在标题栏上方，与视频一致）+ 关闭
            _audioRuler = new MediaPreview.SpeedRuler();
            _audioRuler.PreviewSpeed += delegate(double r) { if (_audio != null) _audio.SpeedRatio = r; MediaPreview.UpdateSpeedHint(_audioSpdHint, r); };
            _audioRuler.CommitSpeed += delegate(double r) { if (_audio != null) _audio.SpeedRatio = r; MediaPreview.UpdateSpeedHint(_audioSpdHint, r); };
            _audioRuler.RestoreSpeed += delegate { if (_audio != null) _audio.SpeedRatio = _audioRuler.Committed; MediaPreview.UpdateSpeedHint(_audioSpdHint, _audioRuler.Committed); };
            Controls.Add(_audioRuler);
            _audioSpdHint = MediaPreview.CreateSpeedHint(_scrollPanel, 2);

            _audioCloseBtn = new Label
            {
                Size = new Size(18, 18),
                AutoSize = false,
                Text = "✕",
                Font = new Font("Microsoft YaHei", 8f),
                ForeColor = Config.FG,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            _audioCloseBtn.Click += delegate { try { Close(); } catch { } };
            Tips.Set(_audioCloseBtn, I18n.T("tip_close_preview"));
            _toolbar.Controls.Add(_audioCloseBtn);

            // 操作栏中段即波形：点击/横向拖动 = seek；竖向拖动 = 移动窗口（拖动过即固定）
            _audioViz = new WaveView { BackColor = Config.BG };
            _audioViz.Seek += delegate(double r) { _audio.SeekRatio(r); };
            _audioViz.MoveWindow += delegate
            {
                var before = Location;
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                if (Location != before && !_pinned)
                {
                    _pinned = true;
                    _pinBtn.Image = PinIcon(true);
                    Padding = new Padding(1);
                    Invalidate();
                }
            };
            _toolbar.Controls.Add(_audioViz);


            // 展开信息区（悬停操作栏 300ms 展开）：文件名 / 时间进度 / 格式·大小
            _audioBody = new Panel { BackColor = Config.BG };
            _audioNameLbl = new Label
            {
                Location = new Point(10, 6),
                Font = new Font(Config.FontName, 9.5f, FontStyle.Bold),
                ForeColor = Config.FG,
                BackColor = Config.BG,
                AutoEllipsis = true,
                Height = 20
            };
            _audioTimeLbl = new Label
            {
                Location = new Point(10, 32),
                Font = new Font(Config.FontName, 9f),
                ForeColor = Config.FgDir,
                BackColor = Config.BG,
                AutoEllipsis = true,
                Height = 18,
                Text = I18n.T("media_loading")
            };
            _audioMetaLbl = new Label
            {
                Location = new Point(10, 56),
                Font = new Font(Config.FontName, 8f),
                ForeColor = Config.PlaceholderFg,
                BackColor = Config.BG,
                AutoEllipsis = true,
                Height = 18
            };
            _audioBody.Controls.Add(_audioNameLbl);
            _audioBody.Controls.Add(_audioTimeLbl);
            _audioBody.Controls.Add(_audioMetaLbl);
            _scrollPanel.Controls.Add(_audioBody);
            _audioBody.Dock = DockStyle.None;
            _audioBody.Location = new Point(0, 0);
            _audioBody.Height = 0;
            BindResize(_audioBody);

            // 悬停展开：鼠标停在操作栏任意位置（播放键/波形/固定键）300ms → 展开信息区
            _audioExpandTimer = new Timer { Interval = 300 };
            _audioExpandTimer.Tick += delegate { _audioExpandTimer.Stop(); SetAudioExpanded(true); };
            EventHandler hoverIn = delegate { _audioExpandTimer.Stop(); _audioExpandTimer.Start(); };
            EventHandler hoverOut = delegate { _audioExpandTimer.Stop(); };
            foreach (var c in new Control[] { _toolbar, _audioPlayBtn, _audioViz, _pinBtn })
            {
                c.MouseEnter += hoverIn;
                c.MouseLeave += hoverOut;
            }
            // 离开收起：只管未固定态的短暂滞留（如“离开不关闭”保住的预览）。
            // 固定后信息区保持展开——常驻播放器不该随鼠标进出反复折叠，收起靠点击信息区
            _audioFoldTimer = new Timer { Interval = 150 };
            _audioFoldTimer.Tick += delegate
            {
                if (_pinned || !_audioExpanded) { _audioFoldTimer.Stop(); return; }
                NativeMethods.POINT pt;
                if (!NativeMethods.GetCursorPos(out pt)) return;
                if (ContainsPoint(pt.X, pt.Y)) { _audioFoldMiss = 0; return; }
                if (++_audioFoldMiss >= 3)
                {
                    _audioFoldMiss = 0;
                    _audioFoldTimer.Stop();
                    SetAudioExpanded(false);
                }
            };
            _audioFoldTimer.Start();

            // 信息区：与操作栏共用按下/移动/抬起判定链——拖动=移动窗口，原地点击=收起
            foreach (var c in new Control[] { _audioBody, _audioNameLbl, _audioTimeLbl, _audioMetaLbl })
            {
                c.MouseDown += AudioBarDown;
                c.MouseMove += AudioBarMove;
                c.MouseUp += AudioBarUp;
            }

            // 操作栏空白处保留：点击=seek / 拖动=挪窗（波形区自己处理两种手势）
            foreach (var c in new Control[] { _toolbar, _nameLbl })
            {
                c.MouseMove += AudioBarMove;
                c.MouseUp += AudioBarUp;
            }

            if (_audio != null)
            {
                _audio.Opened += delegate { UpdateAudioUi(); };
                _audio.EstimateReady += delegate { UpdateAudioUi(); };
                _audio.Ended += delegate { UpdateAudioUi(); };
                _audio.Ended += delegate { OnFolderTrackEnded(); };   // 文件夹播放模式：按循环模式自动切下一首
                _audio.Failed += delegate
                {
                    if (_audioTimeLbl != null) _audioTimeLbl.Text = "解码失败";
                    UpdateAudioUi();
                };
                // 采集不在此启动：SyncMeter 跟随播放状态启停，暂停时释放环回
                _audioMeter = new AudioMeter();
            }

            _audioUiTimer = new Timer { Interval = 40 };
            _audioUiTimer.Tick += delegate { UpdateAudioUi(); };
            _audioUiTimer.Start();

            ApplyAudioSize(contentW);
        }

        // 悬停展开/收起：高度在 34 与 34+信息区之间切换（向下展开）
        private void SetAudioExpanded(bool on)
        {
            if (_audioExpanded == on) return;
            _audioExpanded = on;
            UpdateAudioHeight();
        }

        // 高度统一管理：文件夹播放模式下信息区始终可见 + head 常驻 + 列表展开/折叠
        private void UpdateAudioHeight()
        {
            int infoH;
            int listH = 0;
            if (_folderMode)
            {
                infoH = _audioExpanded ? AudioInfoH : 0;   // 信息区可折叠（点击折叠/悬停展开）
                listH = FolderListH();   // head(24) + 可选列表行
            }
            else
            {
                infoH = _audioExpanded ? AudioInfoH : 0;
            }
            int body = infoH + listH;
            // 刻度尺仅在展开时显示（悬停波形图展开卡片 → 刻度尺 + 详细信息）
            int rulerH = 0;
            if (_audioRuler != null)
            {
                _audioRuler.Visible = _audioExpanded && Config.SpeedToggle;
                if (_audioExpanded) rulerH = _audioRuler.Height;
            }
            // 只改窗口高度，不动宽度：ClientSize setter 会触发 WM_NCCALCSIZE 内缩 FramePad，
            // 每次读 ClientSize.Width（已被 WM_SIZE 更新为客户区宽）再写回会让窗口宽度递减 2*FramePad。
            // 用 Height（窗口高度）避开 ClientSize.Width 读写循环。
            Height = _toolbarH + rulerH + body + 2 * Win11.FramePad;
            ApplyLayout();
            // 手动定位信息区与列表区（避免 AutoScroll+Dock 交互导致 _folderPanel 空白）
            int w = ClientSize.Width;
            if (_audioBody != null)
            {
                _audioBody.Location = new Point(0, 0);
                _audioBody.Size = new Size(w, infoH);
            }
            if (_folderPanel != null && _folderMode)
            {
                _folderPanel.Location = new Point(0, infoH);
                _folderPanel.Size = new Size(w, listH);
                _folderPanel.Visible = true;
            }
        }

        // ===== 文件夹播放模式 =====

        // 构建/刷新同目录音频清单（按名称自然排序=默认排序）；目录变了才重扫
        private bool EnsureFolderTracks()
        {
            if (string.IsNullOrEmpty(FilePath)) return false;
            string dir;
            try { dir = Path.GetDirectoryName(FilePath); } catch { return false; }
            if (string.IsNullOrEmpty(dir)) return false;
            if (_folderTracks != null && string.Equals(dir, _folderDir, StringComparison.OrdinalIgnoreCase))
                return _folderTracks.Count > 0;
            _folderDir = dir;
            _folderTracks = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                    if (Config.AudioExts.Contains(Path.GetExtension(f))) _folderTracks.Add(f);
            }
            catch { }
            _folderTracks.Sort(NativeMethods.StrCmpLogicalW);
            _folderIdx = _folderTracks.FindIndex(t => string.Equals(t, FilePath, StringComparison.OrdinalIgnoreCase));
            if (_folderIdx < 0) _folderIdx = 0;
            return _folderTracks.Count > 0;
        }

        // 滚轮驱动：第一次滚动只进入文件夹播放模式并提示；之后向下=下一首、向上=上一首
        internal void AudioWheel(int delta)
        {
            if (_audio == null) return;
            if (!EnsureFolderTracks()) return;
            if (!_folderMode)
            {
                _folderMode = true;
                BuildFolderPanel();
                _audioExpanded = true;   // 进入文件夹模式：信息区默认显示（点击可折叠）

                SetFolderListVisible(false);   // 列表初始折叠，仅显示 head（含 ☰ 与循环模式）
                ShowFolderTip(I18n.T("folder_play_enter"));
                return;
            }
            int n = _folderTracks.Count;
            PlayFolderTrack((_folderIdx + (delta < 0 ? 1 : -1) + n) % n);
        }

        // 切歌：会话 Reload 原路复用（MCI/MediaSession 各自重开），波形按新路径自动重解码
        private void PlayFolderTrack(int idx)
        {
            if (_folderTracks == null || idx < 0 || idx >= _folderTracks.Count) return;
            _folderIdx = idx;
            string path = _folderTracks[idx];
            FilePath = path;
            string meta;
            try
            {
                var fi = new FileInfo(path);
                meta = Path.GetExtension(path).TrimStart('.').ToUpper() + " · "
                    + (fi.Length >= 1048576 ? (fi.Length / 1048576.0).ToString("0.#") + " MB" : (fi.Length / 1024.0).ToString("0.#") + " KB");
            }
            catch { meta = ""; }
            SetHeader(Path.GetFileName(path), meta, "♪", Config.BG);
            try
            {
                _audio.Reload(path);
                _audio.Play();
            }
            catch { }
            if (OnFileSwitched != null) { try { OnFileSwitched(path); } catch { } }
            SyncFolderListSel();
            UpdateAudioUi();
        }

        // 播完一首：单曲循环原地重播；列表循环到底回首；顺序播完即止
        private void OnFolderTrackEnded()
        {
            if (!_folderMode || _folderTracks == null || _folderTracks.Count == 0 || _audio == null) return;
            if (_loopMode == 2)
            {
                try { _audio.SeekRatio(0); _audio.Play(); } catch { }
                return;
            }
            int next = _folderIdx + 1;
            if (next >= _folderTracks.Count)
            {
                if (_loopMode != 1) return;
                next = 0;
            }
            PlayFolderTrack(next);
        }

        private string LoopIcon() { return _loopMode == 1 ? "↻" : _loopMode == 2 ? "↻1" : "→"; }
        private string LoopTip() { return I18n.T(_loopMode == 1 ? "loop_all" : _loopMode == 2 ? "loop_one" : "loop_seq"); }

        // ☰ 按钮：手动点击展开/折叠文件夹列表
        private void ToggleFolderList()
        {
            if (_folderListOn) { SetFolderListVisible(false); return; }
            if (!EnsureFolderTracks()) return;
            SetFolderListVisible(true);
        }

        private void BuildFolderPanel()
        {
            if (_folderPanel != null) { RefreshFolderItems(); return; }
            _folderPanel = new Panel { BackColor = Config.BG, Dock = DockStyle.None, Visible = false };
            _folderHead = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Config.BG };
            var head = _folderHead;
            _folderHeadLbl = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Config.FgDir,
                BackColor = Config.BG,
                Font = new Font(Config.FontName, 8f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                AutoEllipsis = true
            };
            _loopBtn = new Label
            {
                Size = new Size(34, 18),
                Dock = DockStyle.Right,
                Text = LoopIcon(),
                ForeColor = Config.FG,
                BackColor = Config.BG,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Font = new Font(Config.FontName, 9f)
            };
            _loopBtn.Click += delegate
            {
                _loopMode = (_loopMode + 1) % 3;
                _loopBtn.Text = LoopIcon();
                Tips.Set(_loopBtn, LoopTip());
            };
            Tips.Set(_loopBtn, LoopTip());
            // ☰ 列表按钮：插入到循环模式按钮位置（最右），循环模式按钮左移；手动点击展开/折叠
            _folderBtn = new Label
            {
                Size = new Size(24, 18),
                Dock = DockStyle.Right,
                Text = "☰",
                ForeColor = Config.FG,
                BackColor = Config.BG,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei", 9f)
            };
            Tips.Set(_folderBtn, I18n.T("tip_folder_list"));
            _folderBtn.Click += delegate { ToggleFolderList(); };
            _folderList = new FolderListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Config.BG,
                ForeColor = Config.FG,
                Font = new Font(Config.FontName, Config.FontSize),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = FolderRowH,
                IntegralHeight = false
            };
            _folderList.DrawItem += FolderDrawItem;
            _folderList.Click += delegate
            {
                if (_folderList.SelectedIndex >= 0) PlayFolderTrack(_folderList.SelectedIndex);
            };
            head.Controls.Add(_folderHeadLbl);   // Fill 先加
            head.Controls.Add(_loopBtn);         // Right：先加→靠左
            head.Controls.Add(_folderBtn);       // Right：后加→最右
            _folderPanel.Controls.Add(_folderList);   // Fill 先加
            _folderPanel.Controls.Add(head);          // Top 后加
            _scrollPanel.Controls.Add(_folderPanel);
            foreach (var c in new Control[] { _folderHead, _folderHeadLbl, _loopBtn, _folderBtn })
            {
                c.MouseDown += AudioBarDown;
                c.MouseMove += AudioBarMove;
                c.MouseUp += AudioBarUp;
            }
            RefreshFolderItems();
        }

        private void RefreshFolderItems()
        {
            _folderList.BeginUpdate();
            _folderList.Items.Clear();
            foreach (var t in _folderTracks) _folderList.Items.Add(Path.GetFileName(t));
            _folderList.EndUpdate();

            string dirName = "";
            try { dirName = Path.GetFileName(_folderDir); } catch { }
            _folderHeadLbl.Text = (string.IsNullOrEmpty(dirName) ? _folderDir : dirName) + "  (" + _folderTracks.Count + ")";
            SyncFolderListSel();
        }

        private void SyncFolderListSel()
        {
            if (_folderList != null && _folderList.Items.Count == _folderTracks.Count
                && _folderIdx >= 0 && _folderList.SelectedIndex != _folderIdx)
                _folderList.SelectedIndex = _folderIdx;
        }

        private void FolderDrawItem(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0 || _folderTracks == null || e.Index >= _folderTracks.Count) return;
            bool cur = e.Index == _folderIdx;
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var b = new SolidBrush(sel ? Config.BgHover : Config.BG))
                e.Graphics.FillRectangle(b, e.Bounds);
            string name = (cur ? "♪ " : "") + (string)_folderList.Items[e.Index];
            var rc = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var sf = StringFormat.GenericTypographic;
            float lh = _folderList.Font.GetHeight(e.Graphics);
            using (var b = new SolidBrush(Config.FG))
                e.Graphics.DrawString(RowLabel.MidEllipsis(e.Graphics, name, _folderList.Font, rc.Width), _folderList.Font, b, rc.X, e.Bounds.Y + (e.Bounds.Height - lh) / 2, sf);
        }

        private int FolderListH()
        {
            int headH = 24;
            if (!_folderListOn) return headH;
            int rows = Math.Min(Math.Max(_folderTracks != null && _folderTracks.Count > 0 ? _folderTracks.Count : 1, 1), FolderMaxRows);
            return headH + rows * FolderRowH;
        }

        private void SetFolderListVisible(bool on)
        {
            _folderListOn = on;
            if (_folderList != null) _folderList.Visible = on;
            UpdateAudioHeight();
            // 打开列表：滚到当前曲目（展开即视为进入文件夹播放模式，跟随当前曲目）
            if (on) SyncFolderListSel();
        }


        // 指针是否落在展开的音频列表区（滚轮在该区域时滚动列表而不是切歌）
        internal bool PointInFolderList(int sx, int sy)
        {
            if (!_folderListOn || _folderList == null || !_folderList.Visible) return false;
            return _folderList.RectangleToScreen(_folderList.ClientRectangle).Contains(sx, sy);
        }

        // 模式进入提示：指针上方弹出，1.6s 自动消失（复用按钮提示的 TipForm）
        private void ShowFolderTip(string text)
        {
            if (_folderTip == null)
            {
                _folderTip = new TipForm();
                _folderTipTimer = new Timer { Interval = 1600 };
                _folderTipTimer.Tick += delegate { _folderTipTimer.Stop(); if (_folderTip != null) _folderTip.HideTip(); };
            }
            _folderTip.SetText(text);
            _folderTip.Follow();
            _folderTip.ShowTip();
            _folderTipTimer.Stop();
            _folderTipTimer.Start();
        }


        private void UpdateAudioInfoLayout()
        {
            if (_audioBody == null || _audioNameLbl == null) return;
            int w = _audioBody.Width - 20;
            _audioNameLbl.Width = w;
            _audioTimeLbl.Width = w;
            _audioMetaLbl.Width = w;
        }

        // 音频窗口：宽度沿用上次；高度由悬停展开模型管理，打开总是折叠 34px
        private void ApplyAudioSize(int contentW)
        {
            var wa = Screen.GetWorkingArea(this);
            int w = Config.PreviewAudioW;
            if (w <= 0) w = Math.Max(360, contentW + 20);
            w = Math.Max(300, Math.Min(w, wa.Width - 20));
            ClientSize = new Size(w, _toolbarH);
            Program.DebugLog("[pv-size] audio " + ClientSize.Width + "x" + ClientSize.Height);
        }

        // 采集与解码都失败时的包络动画（保底不让波形死平）
        private float FakeRms()
        {
            _audioPhase += 0.09f;
            return 0.28f + 0.22f * (float)Math.Abs(Math.Sin(_audioPhase));
        }

        private void UpdateAudioUi()
        {
            if (_audio == null || IsDisposed) return;
            SyncMeter();
            double total = _audio.Duration.TotalMilliseconds;
            double est = _audio.EstimatedMs.HasValue ? _audio.EstimatedMs.Value : 0;
            double pos = _audio.Position.TotalMilliseconds;
            if (pos < 0) pos = 0;

            // 全量波形：时长就绪后启动一次；路径变化（改名）重启
            if (_waveDec != null && !string.Equals(_waveDec.Path, _audio.Path, StringComparison.OrdinalIgnoreCase))
            {
                _waveDec.Cancel();
                _waveDec = null;
                _wavePath = null;
                Array.Clear(_waveData, 0, _waveData.Length);
            }
            // 仅当用户已展开信息区/固定预览时才解码全量波形：纯 hover 预览（鼠标还停在文件名上）
            // 不抢同一文件的磁盘 IO，避免与 WPF 播放器争读取导致音频启动慢；波形用回退包络动画顶替
            if (_waveDec == null && total > 0 && !_audio.HasFailed && (_audioExpanded || _pinned))
                _waveDec = WaveDecoder.Begin(_audio.Path, total / 1000.0);

            if (_audio.HasFailed)
                _audioTimeLbl.Text = "解码失败";
            else if (total > 0)
                _audioTimeLbl.Text = MediaPreview.Fmt(TimeSpan.FromMilliseconds(pos)) + " / " + MediaPreview.Fmt(_audio.Duration);
            else if (est > 0)
                _audioTimeLbl.Text = "≈ " + MediaPreview.Fmt(TimeSpan.FromMilliseconds(est));
            else
                _audioTimeLbl.Text = "≈ --:--";
            _audioPlayBtn.Text = _audio.IsPlaying ? "❚❚" : "▶";
            _audioPlayBtn.Enabled = !_audio.HasFailed;

            double r = total > 0 ? pos / total : 0;
            if (r > 1) r = 1;
            if (_waveDec != null && !_waveDec.Failed)
            {
                // 全量摘要：解码中逐桶点亮，解码完即完整波形
                _audioViz.SetData(_waveDec.Rms, _waveDec.Peak, r);
            }
            else
            {
                // 回退：环回 RMS 按进度写包络桶（同桶取 max），采集也挂了走包络动画
                if (_audio.Path != _wavePath)
                {
                    _wavePath = _audio.Path;
                    Array.Clear(_waveData, 0, _waveData.Length);
                }
                if (_audio.IsPlaying && total > 0)
                {
                    int idx = (int)(r * (_waveData.Length - 1));
                    if (idx < 0) idx = 0;
                    if (idx >= _waveData.Length) idx = _waveData.Length - 1;
                    float v = _audioMeter != null && !_audioMeter.Failed ? _audioMeter.Rms * 2.5f : FakeRms() * 2.5f;
                    if (v < 0) v = 0;
                    if (v > 1) v = 1;
                    v = (float)Math.Sqrt(v);
                    if (v > _waveData[idx]) _waveData[idx] = v;
                }
                _audioViz.SetData(_waveData, _waveData, r);
            }

        }

        // 采集只跟着播放走：暂停即停环回（省 CPU，别的程序出声也不会被白算 FFT），恢复播放再启
        private void SyncMeter()
        {
            if (_audioMeter == null) return;
            bool want = _audio.IsPlaying;
            if (want == _meterOn) return;
            _meterOn = want;
            if (want) _audioMeter.Start(); else _audioMeter.Stop();
        }

        // 操作栏按下：右下/边缘命中时先走尺寸调整；否则记录起点，等待判定点击还是拖动
        private bool _audioDownInInfo;   // 按下起点在信息区：原地点击=收起（操作栏空白=seek）
        private bool _audioDownInHead;   // 按下起点在文件夹 head 行：原地点击交给按钮 Click

        private void AudioBarDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var c = (Control)sender;
            var sp = c.PointToScreen(e.Location);
            int hit = GetResizeHit(sp.X, sp.Y);
            if (hit != 0)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, 0xA1, hit, 0);
                return;
            }
            _audioDownInInfo = ReferenceEquals(sender, _audioBody) || ReferenceEquals(sender, _audioNameLbl)
                || ReferenceEquals(sender, _audioTimeLbl) || ReferenceEquals(sender, _audioMetaLbl);
            _audioDownInHead = ReferenceEquals(sender, _folderHead) || ReferenceEquals(sender, _folderHeadLbl)
                || ReferenceEquals(sender, _loopBtn) || ReferenceEquals(sender, _folderBtn);
            _audioBarDown = true;
            _audioBarMoved = false;
            _audioBarStart = sp;
            _audioBarDownX = PointToClient(sp).X;
            c.Capture = true;
        }

        private void AudioBarMove(object sender, MouseEventArgs e)
        {
            if (!_audioBarDown) return;
            var c = (Control)sender;
            var sp = c.PointToScreen(e.Location);
            int dx = Math.Abs(sp.X - _audioBarStart.X);
            int dy = Math.Abs(sp.Y - _audioBarStart.Y);
            if (dx + dy <= 3) return;
            // 超过阈值：交给系统移动窗口，真实移动过才视为“固定”
            _audioBarMoved = true;
            _audioBarDown = false;
            c.Capture = false;
            var before = Location;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
            if (Location != before && !_pinned)
            {
                _pinned = true;
                _pinBtn.Image = PinIcon(true);
                Padding = new Padding(1);
                Invalidate();
            }
        }

        private void AudioBarUp(object sender, MouseEventArgs e)
        {
            if (!_audioBarDown) return;
            _audioBarDown = false;
            ((Control)sender).Capture = false;
            if (_audioBarMoved || _audio == null) return;
            if (_audioDownInHead) return;   // head 行原地点击：交给按钮 Click
            if (_audioDownInInfo)
            {
                // 信息区原地点击 = 收起展开
                SetAudioExpanded(false);
                return;
            }
            // 点击操作栏空白：按横向位置 seek
            double ratio = _toolbar.Width <= 0 ? 0 : (double)_audioBarDownX / _toolbar.Width;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            _audio.SeekRatio(ratio);
            UpdateAudioUi();
        }

        private PreviewForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Config.BG;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);

            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            _wheelFilter = new WheelFilter(this);
            Application.AddMessageFilter(_wheelFilter);
            FormClosed += (s, e) =>
            {
                try { Application.RemoveMessageFilter(_wheelFilter); } catch { }
                if (_sizeSaveTimer != null)
                {
                    _sizeSaveTimer.Stop(); _sizeSaveTimer.Dispose(); _sizeSaveTimer = null;
                    try { Config.Save(); } catch { }
                }
                // 预览窗关了，悬停弹出的复制菜单不能还挂在屏幕上
                if (_copyMenu != null) { try { _copyMenu.Close(); } catch { } _copyMenu = null; }
            };

            _toolbar = new AudioBarPanel { BackColor = Config.BG, Height = 34 };
            Controls.Add(_toolbar);

            _scrollPanel = new Panel { AutoScroll = false, BackColor = Config.BG };
            Controls.Add(_scrollPanel);
            Win11.ApplyScrollTheme(_scrollPanel, Config.IsDark);


            _iconLbl = new Label
            {
                Size = new Size(22, 16),
                Location = new Point(8, 9),
                Font = new Font(Config.FontName, 7.5f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Config.PlaceholderFg,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _toolbar.Controls.Add(_iconLbl);

            _nameLbl = new Label
            {
                Location = new Point(34, 9),
                Font = new Font(Config.FontName, 9f, FontStyle.Bold),
                ForeColor = Config.FG,
                BackColor = Config.BG,
                AutoEllipsis = true,
                Cursor = Cursors.Hand,
            };
            _toolbar.Controls.Add(_nameLbl);

            _metaLbl = new Label
            {
                Font = new Font(Config.FontName, 8f),
                ForeColor = Config.PlaceholderFg,
                BackColor = Config.BG,
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true,
            };
            _toolbar.Controls.Add(_metaLbl);

            _pinBtn = new Label
            {
                Size = new Size(20, 18),
                AutoSize = false,
                BackColor = Config.BG,
                Image = PinIcon(false),
                Cursor = Cursors.Hand,
            };
            _pinBtn.Click += (s, e) => TogglePin();
            Tips.Set(_pinBtn, I18n.T("tip_pin_panel"));
            _toolbar.Controls.Add(_pinBtn);

            _pinLaunchBtn = new Label
            {
                Size = new Size(20, 18),
                AutoSize = false,
                BackColor = Config.BG,
                Image = PinLaunchIcon(false),
                Cursor = Cursors.Hand,
            };
            _pinLaunchBtn.Click += (s, e) => { if (!string.IsNullOrEmpty(FilePath)) { Program.TogglePin(FilePath); RefreshPinLaunchIcon(); } };
            Tips.Set(_pinLaunchBtn, I18n.T("tip_pin_card"));
            _toolbar.Controls.Add(_pinLaunchBtn);


            _copyBtn = new Label
            {
                Size = new Size(20, 18),
                AutoSize = false,
                BackColor = Config.BG,
                Image = CopyIcon(),
                Cursor = Cursors.Hand,
            };
            // 复制按钮是悬停即弹菜单，不再挂提示（提示会盖到弹出的菜单项上）
            // 鼠标移开按钮后短延时收起：期间鼠标要是移到了菜单上就不关，否则菜单没法点
            var copyLeave = new Timer { Interval = 200 };
            copyLeave.Tick += (s, e) =>
            {
                copyLeave.Stop();
                if (_copyMenu == null) return;
                NativeMethods.POINT p; NativeMethods.GetCursorPos(out p);
                if (!_copyMenu.Bounds.Contains(p.X, p.Y)) _copyMenu.Close();
            };
            _copyBtn.MouseEnter += (s, e) => { copyLeave.Stop(); ShowCopyMenu(); };
            _copyBtn.MouseLeave += (s, e) => { if (_copyMenu != null) copyLeave.Start(); };
            _toolbar.Controls.Add(_copyBtn);

            // textcard：复制文件/复制路径无意义 → 隐藏 _copyBtn，同位换成粘贴按钮
            _pasteBtn = new Label
            {
                Size = new Size(20, 18),
                AutoSize = false,
                BackColor = Config.BG,
                Text = "⇥",
                Font = new Font(Config.FontName, 10f, FontStyle.Bold),
                ForeColor = Config.FG,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Visible = false,
            };
            _pasteBtn.Click += (s, e) => { if (PasteAction != null) PasteAction(); };
            _toolbar.Controls.Add(_pasteBtn);

            _saveBtn = new Label
            {
                Size = new Size(20, 18),
                AutoSize = false,
                BackColor = Config.BG,
                Image = SaveIcon(),
                Cursor = Cursors.Hand,
                Visible = false,
            };
            _saveBtn.Click += (s, e) => SaveClick();
            Tips.Set(_saveBtn, I18n.T("tip_save"));
            _toolbar.Controls.Add(_saveBtn);

            MouseEventHandler drag = (s, e) =>
            {
                // 音频操作栏：点击 seek / 拖动挪窗口，二义性由 AudioBarDown 处理
                if (_audio != null) { AudioBarDown(s, e); return; }
                if (e.Button != MouseButtons.Left) return;
                var sp = ((Control)s).PointToScreen(e.Location);
                int hit = GetResizeHit(sp.X, sp.Y);
                if (hit != 0) { NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, hit, 0); return; }
                // 实际拖动后才固定；单纯点击标题不固定
                var before = Location;
                NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                if (Location != before && !_pinned) { _pinned = true; _pinBtn.Image = PinIcon(true); Padding = new Padding(1); Invalidate(); }
            };
            _toolbar.MouseDown += drag;
            // 左上角类型图标：单击=用默认程序打开；按住拖动=挪窗口（与标题栏语义一致）
            _iconLbl.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var before = Location;
                NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                if (Location == before && !string.IsNullOrEmpty(FilePath)) { try { Process.Start(FilePath); } catch { } }
            };
            _iconLbl.Cursor = Cursors.Hand;
            Tips.Set(_iconLbl, I18n.T("tip_open_file"));
            _nameLbl.MouseDown += drag;
            _metaLbl.MouseDown += drag;
            foreach (var ctl in new Control[] { _toolbar, _iconLbl, _nameLbl, _metaLbl })
            {
                ctl.MouseMove += (s, e) => { var c = (Control)s; var sp = c.PointToScreen(e.Location); UpdateResizeCursor(c, sp.X, sp.Y); };
                ctl.MouseLeave += (s, e) => { ((Control)s).Cursor = null; };
            }
            BindResize(_scrollPanel);
            Resize += (s, e) => ApplyLayout();
            UpdateToolbarBtnPos();
        }

        private void BindResize(Control c)
        {
            c.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var sp = ((Control)s).PointToScreen(e.Location);
                int hit = GetResizeHit(sp.X, sp.Y);
                if (hit != 0) { Program.DebugLog("[pv-resize] down hit=0x" + hit.ToString("X") + " at " + sp.X + "," + sp.Y); NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, hit, 0); }
            };
            c.MouseMove += (s, e) =>
            {
                var sp = ((Control)s).PointToScreen(e.Location);
                UpdateResizeCursor((Control)s, sp.X, sp.Y);
            };
            c.MouseLeave += (s, e) => { ((Control)s).Cursor = null; };
        }

        private void UpdateResizeCursor(Control c, int x, int y)
        {
            int b = 6, br = 20;
            Cursor cur;
            if (_audio != null)
            {
                // 音频窗高度由悬停展开模型管理：只提示左右改宽
                cur = (x <= Left + b || x >= Right - b) ? Cursors.SizeWE : null;
                c.Cursor = cur;
                return;
            }
            if (x <= Left + b && y <= Top + b) cur = Cursors.SizeNWSE;
            else if (x >= Right - b && y <= Top + b) cur = Cursors.SizeNESW;
            else if (x <= Left + b && y >= Bottom - b) cur = Cursors.SizeNESW;
            else if (x >= Right - br && y >= Bottom - br) cur = Cursors.SizeNWSE;
            else if (x <= Left + b) cur = Cursors.SizeWE;
            else if (x >= Right - b) cur = Cursors.SizeWE;
            else if (y <= Top + b) cur = Cursors.SizeNS;
            else if (y >= Bottom - b) cur = Cursors.SizeNS;
            else cur = null;
            c.Cursor = cur;
        }

        private int GetResizeHit(int x, int y)
        {
            int b = 6, br = 20;
            if (_audio != null)
            {
                // 音频窗高度由悬停展开模型管理：只允许左右改宽
                if (x <= Left + b) return 0x0A;
                if (x >= Right - b) return 0x0B;
                return 0;
            }
            if (x <= Left + b && y <= Top + b) return 0x0D;
            if (x >= Right - b && y <= Top + b) return 0x0E;
            if (x <= Left + b && y >= Bottom - b) return 0x10;
            if (x >= Right - br && y >= Bottom - br) return 0x11;
            if (x <= Left + b) return 0x0A;
            if (x >= Right - b) return 0x0B;
            if (y <= Top + b) return 0x0C;
            if (y >= Bottom - b) return 0x0F;
            return 0;
        }

        protected override void WndProc(ref Message m)
        {
            // 圆角模式描边与自身背景同色：亮色描边压在深色配色上很突兀，
            // 同色后只剩 DWM 圆角外形（回调动态取 BackColor，主题/层级着色跟随）
            if (Win11.WndNcBorder(Handle, ref m, () => BackColor)) return;
            if (m.Msg == 0x84)   // WM_NCHITTEST
            {
                var lp = m.LParam.ToInt64();
                var cp = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int cw = ClientSize.Width, ch = ClientSize.Height;
                // 音频窗高度由悬停展开模型管理，只允许左右边缘改宽
                if (cp.X >= cw - 6 && cp.Y >= ch - 6 && _audio == null) { m.Result = (IntPtr)17; return; }
                if (cp.X >= cw - 6 && cp.Y >= 0 && cp.Y < ch) { m.Result = (IntPtr)11; return; }
                if (cp.Y >= ch - 6 && cp.X >= 0 && cp.X < cw && _audio == null) { m.Result = (IntPtr)12; return; }
            }
            else if (m.Msg == 0x231) _pvSizeMoveStart = ClientSize;
            else if (m.Msg == 0x232)
            {
                if (ClientSize != _pvSizeMoveStart && (_content is PictureBox || _content is Panel))
                {
                    if (_content is PictureBox && ClientSize.Width >= 120) Config.PreviewMinWidth = ClientSize.Width;
                    RememberSize(ClientSize.Width, ClientSize.Height);
                    Program.DebugLog("[pv-size] resize-end " + _mediaKind + " " + ClientSize.Width + "x" + ClientSize.Height);
                }
            }
            base.WndProc(ref m);
        }

        // 图片预览尺寸入口：优先套用记住的尺寸，否则走自动尺寸
        private void ApplyImageSize(int w, int h)
        {
            if (Config.PreviewImgW > 0 && Config.PreviewImgH > 0)
            {
                var wa0 = Screen.GetWorkingArea(this);
                int rw = Math.Max(120, Math.Min(Config.PreviewImgW, wa0.Width - 20));
                int rh = Math.Max(90, Math.Min(Config.PreviewImgH, wa0.Height - 20));
                ClientSize = new Size(rw, rh);
                Program.DebugLog("[pv-size] remembered " + rw + "x" + rh);
                return;
            }
            AutoFitSize(w, h);
        }

        // 自动尺寸：不小于 PreviewMinWidth，超限时等比收缩到最大占屏比例以内
        // （原图模式下超大图不再铺满整屏）
        private void AutoFitSize(int w, int h)
        {
            if (w <= 0) w = Config.PreviewMinWidth;
            if (h <= 0) h = (int)(w * 0.75);
            int minW = Config.PreviewMinWidth;
            if (w < minW && w > 0) { h = (int)Math.Round(h * (double)minW / w); w = minW; }
            var wa = Screen.GetWorkingArea(this);
            double ratio = Math.Min(1, Math.Max(0.3, Config.PreviewMaxRatio));
            int maxW = Math.Max(240, (int)(wa.Width * ratio));
            int maxH = Math.Max(180, (int)(wa.Height * ratio));
            if (w > maxW || h > maxH)
            {
                double k = Math.Min((double)maxW / w, (double)maxH / h);
                w = (int)(w * k); h = (int)(h * k);
            }
            if (w > wa.Width - 20) w = wa.Width - 20;
            if (h > wa.Height - 20) h = wa.Height - 20;
            ClientSize = new Size(Math.Max(120, w), Math.Max(90, h));
            Program.DebugLog("[pv-size] auto " + w + "x" + h + " -> " + ClientSize.Width + "x" + ClientSize.Height + " max=" + maxW + "x" + maxH);
        }

        // 取当前内容类型对应的记忆尺寸（视频/音频/SVG 各自独立）
        private void GetRememberedMediaSize(out int w, out int h)
        {
            if (_mediaKind == "video") { w = Config.PreviewVideoW; h = Config.PreviewVideoH; }
            else if (_mediaKind == "audio") { w = Config.PreviewAudioW; h = Config.PreviewAudioH; }
            else { w = Config.PreviewMediaW; h = Config.PreviewMediaH; }
        }

        private void SetRememberedMediaSize(int w, int h)
        {
            if (_mediaKind == "video") { Config.PreviewVideoW = w; Config.PreviewVideoH = h; }
            else if (_mediaKind == "audio") { Config.PreviewAudioW = w; Config.PreviewAudioH = h; }
            else { Config.PreviewMediaW = w; Config.PreviewMediaH = h; }
        }

        // 视频/音频/SVG 面板：优先套用记住的尺寸，否则按内容尺寸（含工具栏 34px）并限制占屏
        private void ApplyContentSize(int w, int h)
        {

            var wa = Screen.GetWorkingArea(this);
            int mw, mh;
            GetRememberedMediaSize(out mw, out mh);
            if (mw > 0 && mh > 0)
            {
                int rw = Math.Max(160, Math.Min(mw, wa.Width - 20));
                int rh = Math.Max(120, Math.Min(mh, wa.Height - 20));
                ClientSize = new Size(rw, rh);
                Program.DebugLog("[pv-size] media-remembered " + _mediaKind + " " + rw + "x" + rh);
                return;
            }
            double ratio = Math.Min(1, Math.Max(0.3, Config.PreviewMaxRatio));
            int maxW = Math.Max(240, (int)(wa.Width * ratio));
            int maxH = Math.Max(180, (int)(wa.Height * ratio));
            int cw = w, ch = h + 34;
            if (cw > maxW || ch > maxH)
            {
                double k = Math.Min((double)maxW / cw, (double)maxH / ch);
                cw = (int)(cw * k); ch = (int)(ch * k);
            }
            if (cw > wa.Width - 20) cw = wa.Width - 20;
            if (ch > wa.Height - 20) ch = wa.Height - 20;
            ClientSize = new Size(Math.Max(160, cw), Math.Max(120, ch));
            Program.DebugLog("[pv-size] media-auto " + w + "x" + h + " -> " + ClientSize.Width + "x" + ClientSize.Height);
        }

        // 记住用户调整后的尺寸（右下角手柄拖拽 / Ctrl+滚轮），延迟落盘避免频繁写配置
        private void RememberSize(int w, int h)
        {

            if (_content is PictureBox)
            {
                Config.PreviewImgW = w;
                Config.PreviewImgH = h;
            }
            else if (_audio != null)
            {
                // 音频：只记宽度；高度由悬停展开模型管理（34 / 34+信息区）
                Config.PreviewAudioW = w;
            }
            else
            {
                SetRememberedMediaSize(w, h);
            }
            if (_sizeSaveTimer == null)
            {
                _sizeSaveTimer = new Timer { Interval = 500 };
                _sizeSaveTimer.Tick += (s, e) => { _sizeSaveTimer.Stop(); try { Config.Save(); } catch { } };
            }
            _sizeSaveTimer.Stop();
            _sizeSaveTimer.Start();
        }

        // 视频解码完成后按真实宽高比调整窗口（避免固定 4:3 造成上下黑边）
        // 用户已记住视频尺寸时不改动，尊重其手动调整
        internal void FitVideoAspect(int vw, int vh)
        {
            if (vw <= 0 || vh <= 0) return;
            int mw, mh;
            GetRememberedMediaSize(out mw, out mh);
            if (mw > 0 && mh > 0) return;
            var wa = Screen.GetWorkingArea(this);
            double ratio = Math.Min(1, Math.Max(0.3, Config.PreviewMaxRatio));
            int maxW = Math.Max(240, (int)(wa.Width * ratio));
            int maxH = Math.Max(180, (int)(wa.Height * ratio));
            int w = Math.Max(200, Math.Min(ClientSize.Width, maxW));
            int h = 64 + (int)Math.Round(w * (double)vh / vw);   // 工具栏 34 + 播放条 30 + 画面
            if (h > maxH)
            {
                h = maxH;
                w = Math.Max(200, (int)Math.Round((h - 64) * (double)vw / vh));
            }
            if (h > wa.Height - 20) h = wa.Height - 20;
            ClientSize = new Size(w, Math.Max(120, h));
            // 尺寸变化后别越出屏幕
            if (Top + Height > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height - 4);
            if (Left + Width > wa.Right) Left = Math.Max(wa.Left, wa.Right - Width - 4);
            Program.DebugLog("[pv-size] video-aspect " + vw + "x" + vh + " -> " + ClientSize.Width + "x" + ClientSize.Height);
        }


        private void HandleWheel(int delta)
        {
            SwitchFile(delta > 0 ? -1 : 1);
        }

        private void ZoomBy(int delta)
        {

            double factor = delta > 0 ? 1.15 : 0.87;
            int newW, newH;
            var pb = _content as PictureBox;
            if (pb != null)
            {
                if (pb.Image == null) return;
                _zoom *= factor;
                _zoom = Math.Max(0.2, Math.Min(6, _zoom));
                newW = (int)(pb.Image.Width * _zoom);
                newH = (int)(pb.Image.Height * _zoom);
            }
            else
            {
                // 视频/音频/SVG 面板：按当前窗口等比缩放，画面随之变大
                newW = (int)(ClientSize.Width * factor);
                newH = (int)(ClientSize.Height * factor);
            }
            var wa = Screen.GetWorkingArea(this);
            newW = Math.Max(120, Math.Min(wa.Width, newW));
            newH = Math.Max(90, Math.Min(wa.Height, newH));
            ClientSize = new Size(newW, newH);
            RememberSize(newW, newH);
        }

        // 目录内文件按当前排序（名称/大小/日期 + 升降序）排好；filter 挑选参与的文件
        internal static List<string> GetSortedFiles(string dir, Func<string, bool> filter)
        {
            var result = new List<string>();
            string[] all;
            try { all = Directory.GetFiles(dir); } catch { return result; }
            foreach (var f in all)
            {
                if (filter == null || filter(f)) result.Add(f);
            }
            // 滚轮切换顺序与文件列表一致（名称/大小/日期 + 升降序），而不是固定按名称
            string mode = PopupForm.SortMode;
            bool desc = PopupForm.SortDesc;
            var sizes = new long[result.Count];
            var times = new long[result.Count];
            if (mode == "size" || mode == "date")
            {
                for (int i = 0; i < result.Count; i++)
                {
                    try
                    {
                        if (mode == "size") sizes[i] = new FileInfo(result[i]).Length;
                        else times[i] = File.GetLastWriteTimeUtc(result[i]).Ticks;
                    }
                    catch { }
                }
            }
            var idx = new int[result.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort<int>(idx, (ia, ib) =>
            {
                int cmp;
                if (mode == "size") cmp = sizes[ia].CompareTo(sizes[ib]);
                else if (mode == "date") cmp = times[ia].CompareTo(times[ib]);
                else cmp = string.Compare(result[ia], result[ib], StringComparison.OrdinalIgnoreCase);
                if (cmp == 0) cmp = string.Compare(result[ia], result[ib], StringComparison.OrdinalIgnoreCase);
                return desc ? -cmp : cmp;
            });
            var sorted = new List<string>(result.Count);
            for (int i = 0; i < result.Count; i++) sorted.Add(result[idx[i]]);
            return sorted;
        }

        // 图片预览原地换图（滚轮）：仅图片类预览可走
        internal bool IsImagePreview { get { return _content is PictureBox && _img != null; } }

        internal void SwitchFile(int dir)
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            string dir0 = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(dir0)) return;
            var imgs = GetSortedFiles(dir0, f => Config.ImgExts.Contains(Path.GetExtension(f)) || Config.IcoExts.Contains(Path.GetExtension(f)));
            if (imgs.Count == 0) return;
            int idx = imgs.FindIndex(f => string.Equals(f, FilePath, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            int next = idx + dir;
            if (next < 0) next = imgs.Count - 1;
            if (next >= imgs.Count) next = 0;
            string newPath = imgs[next];
            int pos = next + 1;
            int total = imgs.Count;
            Task.Run(() =>
            {
                try
                {
                    var result = PreviewProvider.Generate(newPath);
                    if (result == null || result.Kind != PreviewResult.KindEnum.Image) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) { if (result.Image != null) result.Image.Dispose(); return; }
                        var oldImg = _img;
                        _img = result.Image;
                        var pb = _content as PictureBox;
                        if (pb == null) { result.Image.Dispose(); return; }
                        pb.Image = result.Image;
                        FilePath = newPath;
                        _zoom = 1.0;
                        pb.SizeMode = PictureBoxSizeMode.Zoom;
                        ApplyImageSize(result.DisplayW > 0 ? result.DisplayW : result.Image.Width, result.DisplayH > 0 ? result.DisplayH : result.Image.Height);
                        UpdateMeta(newPath, pos, total);
                        if (oldImg != null) try { oldImg.Dispose(); } catch { }
                        if (OnFileSwitched != null) OnFileSwitched(newPath);
                    });
                }
                catch { }
            });
        }

        private void UpdateMeta(string path, int pos, int total)
        {
            try
            {
                var fi = new FileInfo(path);
                string name = Path.GetFileName(path);
                string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                string meta = ext + " · " + Config.FmtSize(fi.Length);
                if (total > 0) meta += " · " + pos + "/" + total;
                if (_nameLbl != null) _nameLbl.Text = name;
                if (_metaLbl != null) _metaLbl.Text = meta;
                UpdateToolbarBtnPos();
            }
            catch { }
        }

        private class WheelFilter : IMessageFilter
        {
            private PreviewForm _form;
            public WheelFilter(PreviewForm f) { _form = f; }
            public bool PreFilterMessage(ref Message m)
            {
                if (_form == null || _form.IsDisposed) return false;
                if (m.Msg != 0x20A) return false;
                bool ctrl0 = (Control.ModifierKeys & Keys.Control) == Keys.Control;
                int x = (short)((m.LParam.ToInt64() >> 0) & 0xFFFF);
                int y = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                int delta0 = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                // 音频预览：普通滚轮 = 文件夹播放模式（首次滚动进入并提示，继续滚动切歌）
                if (_form._mediaKind == "audio" && !ctrl0)
                {
                    if (!_form.Bounds.Contains(x, y)) return false;
                    // 列表展开时，指针在列表区内滚轮=滚动列表（交给 ListBox 默认处理）
                    if (_form.PointInFolderList(x, y)) return false;
                    _form.AudioWheel(delta0);
                    return true;
                }
                bool isPic = _form._content is PictureBox;
                // 媒体面板只在 Ctrl+滚轮（缩放窗口）时拦截，普通滚轮交还给控件
                if (!isPic && !(ctrl0 && _form._content is Panel)) return false;
                if (!_form.Bounds.Contains(x, y)) return false;
                int delta = delta0;
                bool ctrl = (Control.ModifierKeys & Keys.Control) == Keys.Control;
                if (ctrl) _form.ZoomBy(delta);
                else _form.HandleWheel(delta);
                return true;
            }
        }


        private void UpdateToolbarBtnPos()
        {
            int btnY = 8;
            _pinBtn.Location = new Point(_toolbar.Width - _pinBtn.Width - 4, btnY);
            if (_audio != null)
            {
                // 音频操作栏：播放键 | 波形（占满中段，seek/移窗）| 倍速 | 固定此窗口 | 关闭
                // ☰ 文件夹列表按钮已移到下方 head（进入文件夹播放模式后显示）
                _audioPlayBtn.Location = new Point(6, btnY);
                int vx = 32;
                int vw = _pinBtn.Left - vx - 6;

                if (_audioCloseBtn != null)
                    _audioCloseBtn.Location = new Point(_toolbar.Width - _audioCloseBtn.Width - 2, btnY);
                if (vw < 40) vw = 40;
                _audioViz.Location = new Point(vx, 8);
                _audioViz.Size = new Size(vw, 18);
                UpdateAudioInfoLayout();
                return;
            }
            _pinLaunchBtn.Location = new Point(_pinBtn.Left - _pinLaunchBtn.Width - 2, btnY);
            _copyBtn.Location = new Point(_pinLaunchBtn.Left - _copyBtn.Width - 2, btnY);
            _pasteBtn.Location = _copyBtn.Location;   // textcard：粘贴按钮占复制按钮同位
            int anchor = _copyBtn.Left;
            if (_saveBtn.Visible)
            {
                _saveBtn.Location = new Point(_copyBtn.Left - _saveBtn.Width - 2, btnY);
                anchor = _saveBtn.Left;
            }
            using (var g = _metaLbl.CreateGraphics())
            {
                var sz = g.MeasureString(_metaLbl.Text, _metaLbl.Font);
                int metaW = Math.Min((int)sz.Width + 6, anchor - 34 - 4);
                if (metaW < 0) metaW = 0;
                _metaLbl.Size = new Size(metaW, 18);
                _metaLbl.Location = new Point(anchor - metaW - 4, btnY + 1);
            }
            int nameW = _metaLbl.Left - 34 - 2;
            if (nameW < 0) nameW = 0;
            _nameLbl.Location = new Point(34, btnY + 1);
            _nameLbl.Size = new Size(nameW, 18);
            RefreshPinLaunchIcon();
        }

        // textcard 预览：复制按钮换粘贴按钮（PasteAction 由 PinCardForm 随后注入）
        public void HideCopyForTextCard()
        {
            _copyBtn.Visible = false;
            _pasteBtn.Visible = true;
            UpdateToolbarBtnPos();
        }

        public void SetHeader(string name, string meta, string iconText, Color iconColor)        {
            _nameLbl.Text = name;
            _metaLbl.Text = meta;
            if (_audioNameLbl != null) _audioNameLbl.Text = name;   // 展开信息区：文件名
            if (_audioMetaLbl != null) _audioMetaLbl.Text = meta;   // 展开信息区：格式·大小
            _iconLbl.Text = iconText;
            _iconLbl.BackColor = iconColor;
            // 标题常被右侧按钮挤成省略号，悬停给出完整路径
            Tips.Set(_nameLbl, string.IsNullOrEmpty(FilePath) ? name : FilePath);
            if (_audioNameLbl != null) Tips.Set(_audioNameLbl, string.IsNullOrEmpty(FilePath) ? name : FilePath);
            // 设置 meta 文本后必须重算 _metaLbl 的宽高与位置，
            // 否则沿用构造期(空文本)算出的 6px 宽，内容被 AutoEllipsis 截断不可见
            UpdateToolbarBtnPos();
        }

        private void ApplyLayout()
        {

            if (!IsHandleCreated) return;
            int rulerH = 0;
            if (_audioRuler != null && _audioRuler.Visible)
            {
                rulerH = _audioRuler.Height;
                _audioRuler.Location = new Point(0, _toolbarH);
                _audioRuler.Size = new Size(ClientSize.Width, rulerH);
                _audioRuler.BringToFront();
            }
            _toolbar.Location = new Point(0, 0);
            _toolbar.Size = new Size(ClientSize.Width, _toolbarH);
            _scrollPanel.Location = new Point(0, _toolbarH + rulerH);
            _scrollPanel.Size = new Size(ClientSize.Width, Math.Max(0, ClientSize.Height - _toolbarH - rulerH));
            UpdateToolbarBtnPos();
        }


        // 预览内容正占着文件句柄：图片是 Image.FromFile 锁的，媒体是播放中的 MediaElement。
        // 不释放直接 File.Move / File.Delete 必定失败，所以改名/删除前先释放，之后按原路径恢复。
        internal void ReleaseContent()
        {
            if (IsDisposed) return;
            var pb = _content as PictureBox;
            if (pb != null)
            {
                pb.Image = null;
                if (_img != null) { _img.Dispose(); _img = null; }
                return;
            }
            var p = _content as Panel;
            if (p != null) MediaPreview.StopMedia(p);
        }

        internal void ReloadContent()
        {
            var pb = _content as PictureBox;
            if (pb != null)
            {
                try
                {
                    var r = PreviewProvider.Generate(FilePath);
                    if (r != null && r.Image != null) { _img = r.Image; pb.Image = r.Image; }
                }
                catch (Exception ex) { Program.DebugLog("[pv-reload] fail " + ex.Message); }
                return;
            }
            var p = _content as Panel;
            if (p != null) MediaPreview.PlayMedia(p, FilePath);
        }

        internal void ReleaseContentIf(string path)
        {
            if (string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase)) ReleaseContent();
        }

        internal void RestoreContentTo(string oldPath, string newPath)
        {
            if (!string.Equals(FilePath, oldPath, StringComparison.OrdinalIgnoreCase)) return;
            FilePath = newPath;
            ReloadContent();
            // 改名可能连扩展名一起改：按新路径整套刷新标题（文件名/格式/图标）
            PopupForm.ApplyHeader(this, newPath);
        }




        internal static bool RecycleFile(string path)
        {
            try
            {
                var op = new NativeMethods.SHFILEOPSTRUCTW();
                op.hwnd = IntPtr.Zero;
                op.wFunc = NativeMethods.FO_DELETE;
                op.pFrom = path + "\0\0";
                op.pTo = null;
                op.fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT | NativeMethods.FOF_NOERRORUI;
                return NativeMethods.SHFileOperationW(ref op) == 0;
            }
            catch { return false; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {

            if (_unpinCloseTimer != null) { _unpinCloseTimer.Stop(); _unpinCloseTimer.Dispose(); _unpinCloseTimer = null; }

            if (_audioUiTimer != null) { _audioUiTimer.Stop(); _audioUiTimer.Dispose(); _audioUiTimer = null; }
            if (_audioExpandTimer != null) { _audioExpandTimer.Stop(); _audioExpandTimer.Dispose(); _audioExpandTimer = null; }
            if (_audioFoldTimer != null) { _audioFoldTimer.Stop(); _audioFoldTimer.Dispose(); _audioFoldTimer = null; }
            if (_folderTipTimer != null) { _folderTipTimer.Stop(); _folderTipTimer.Dispose(); _folderTipTimer = null; }

            if (_folderTip != null) { _folderTip.Close(); _folderTip = null; }
            if (_waveDec != null) { _waveDec.Cancel(); _waveDec = null; }
            if (_audioMeter != null) { _audioMeter.Dispose(); _audioMeter = null; }
            // 音频播放器是挂在 hostPanel.Tag 上的 COM 对象，窗体 Dispose 不会自动调其
            // Dispose→Stop，窗口关了声音还在播；必须显式停止，否则鼠标离开后声音拖好几秒
            if (_audio != null) { try { _audio.Close(); } catch { } _audio = null; }
            base.OnFormClosed(e);
        }

        private void TogglePin()
        {
            _pinned = !_pinned;
            _pinBtn.Image = PinIcon(_pinned);
            if (_pinned)
            {
                Padding = new Padding(1);
                Invalidate();
            }
            else
            {
                Padding = new Padding(0);
                Invalidate();
                if (_unpinCloseTimer == null)
                {
                    _unpinCloseTimer = new Timer { Interval = 200 };
                    _unpinCloseTimer.Tick += (s, e) =>
                    {
                        NativeMethods.POINT pt;
                        NativeMethods.GetCursorPos(out pt);
                        if (!ContainsPoint(pt.X, pt.Y))
                        {
                            _unpinCloseTimer.Stop();
                            try { Close(); } catch { }
                        }
                    };
                }
                _unpinCloseTimer.Start();
            }
        }

        public bool ContainsPoint(int x, int y)
        {
            if (!IsHandleCreated || !Visible) return false;
            return Left <= x && x <= Left + Width && Top <= y && y <= Top + Height;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _img != null) _img.Dispose();
            base.Dispose(disposing);
        }

        internal static Image PinIcon(bool pinned)
        {
            return PinIcon(pinned, Config.FG);
        }

        // c = 图标前景色：列表面板传 ListFg（用户调色板可能与应用主题明暗相反，用主题 FG 会看不见）
        internal static Image PinIcon(bool pinned, Color c)
        {
            return PopupForm.UiIcon("pin_" + pinned + "_" + c.ToArgb(), () =>
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var p = new Pen(c, 1.4f))
                    using (var b = new SolidBrush(c))
                    {
                        g.DrawEllipse(p, 3, 2, 8, 8);
                        if (pinned) g.FillEllipse(b, 3, 2, 8, 8);
                        g.DrawLine(p, 7, 10, 7, 14);
                        g.DrawLine(p, 6, 14, 8, 14);
                    }
                }
                return bmp;
            });
        }

        internal static Image PinLaunchIcon(bool active)
        {
            return PinLaunchIcon(active, Config.FG);
        }

        internal static Image PinLaunchIcon(bool active, Color c)
        {
            return PopupForm.UiIcon("pinlaunch_" + active + "_" + c.ToArgb(), () =>
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var col = active ? Config.Accent : c;
                    using (var p = new Pen(col, 1.4f))
                    using (var b = new SolidBrush(col))
                    {
                        g.DrawRectangle(p, 3, 3, 10, 10);
                        g.FillRectangle(b, 3, 3, 2, 10);
                    }
                }
                return bmp;
            });
        }

        internal void RefreshPinLaunchIcon()
        {
            if (_pinLaunchBtn == null) return;
            bool active = !string.IsNullOrEmpty(FilePath) && Program.IsPinned(FilePath);
            _pinLaunchBtn.Image = PinLaunchIcon(active);
            _pinLaunchBtn.Invalidate();
        }

        internal static Image TrashIcon()
        {
            return TrashIcon(Config.FG);
        }

        internal static Image TrashIcon(Color c)
        {
            return PopupForm.UiIcon("trash_" + c.ToArgb(), () =>
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var p = new Pen(c, 1.4f))
                    {
                        g.DrawLine(p, 2, 4, 14, 4);
                        g.DrawLine(p, 4, 2, 12, 2);
                        g.DrawLine(p, 4, 2, 4, 4);
                        g.DrawLine(p, 12, 2, 12, 4);
                        g.DrawLine(p, 3.5f, 4, 4.5f, 14);
                        g.DrawLine(p, 12.5f, 4, 11.5f, 14);
                        g.DrawLine(p, 4.5f, 14, 11.5f, 14);
                        g.DrawLine(p, 7, 6, 7, 12);
                        g.DrawLine(p, 10, 6, 10, 12);
                    }
                }
                return bmp;
            });
        }

        internal static Image CopyIcon()
        {
            return CopyIcon(Config.FG);
        }

        internal static Image CopyIcon(Color c)
        {
            return PopupForm.UiIcon("copy_" + c.ToArgb(), () =>
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var p = new Pen(c, 1.4f))
                    {
                        g.DrawRectangle(p, 2, 2, 8, 10);
                        g.DrawRectangle(p, 5, 4, 8, 10);
                    }
                }
                return bmp;
            });
        }

        internal static Image EditIcon()
        {
            return EditIcon(Config.FG);
        }

        internal static Image EditIcon(Color c)
        {
            return PopupForm.UiIcon("edit_" + c.ToArgb(), () =>
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var p = new Pen(c, 1.4f))
                    {
                        // 铅笔：斜杆 + 笔尖三角 + 顶部横线
                        g.DrawLine(p, 3, 13, 4, 10);
                        g.DrawLine(p, 4, 10, 11, 3);
                        g.DrawLine(p, 11, 3, 13, 5);
                        g.DrawLine(p, 13, 5, 6, 12);
                        g.DrawLine(p, 6, 12, 3, 13);
                        g.DrawLine(p, 3, 15, 13, 15);
                    }
                }
                return bmp;
            });
        }

        private static Image SaveIcon()
        {
            return PopupForm.UiIcon("save", () =>
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var p = new Pen(Config.FG, 1.4f))
                    using (var b = new SolidBrush(Config.FG))
                    {
                        g.DrawRectangle(p, 2, 2, 12, 12);
                        g.FillRectangle(b, 4, 3, 8, 3);
                        g.DrawRectangle(p, 5, 9, 6, 4);
                    }
                }
                return bmp;
            });
        }

        private void ShowCopyMenu()
        {
            if (string.IsNullOrEmpty(FilePath) || Program.ShellMenuOpen) return;
            var m = new ContextMenuStrip { BackColor = Config.BG, ForeColor = Config.FG, Font = new Font(Config.FontName, Config.FontSize) };
            m.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            var rtb = _content as RichTextBox;
            if (rtb != null && rtb.SelectionLength > 0)
                m.Items.Add(new ToolStripMenuItem(I18n.T("copy_selected"), null, (EventHandler)((s, e) => { try { Clipboard.SetText(rtb.SelectedText); ShowToast(I18n.T("copied")); } catch { } })));
            else if (rtb != null)
                m.Items.Add(new ToolStripMenuItem(I18n.T("copy_all"), null, (EventHandler)((s, e) => { try { Clipboard.SetText(rtb.Text); ShowToast(I18n.T("copied")); } catch { } })));
            // SVG 预览带源码框（RichTextBox 不在 _content 顶层，上面两个分支取不到），
            // 单独补一项"复制代码"，直接读源文件——源码框超过 64KB 会被截断，不能作为来源
            if (IsSvgFile())
                m.Items.Add(new ToolStripMenuItem(I18n.T("copy_code"), null, (EventHandler)((s, e) => CopySvgCode())));
            m.Items.Add(new ToolStripMenuItem(I18n.T("copy_file"), null, (EventHandler)((s, e) => { try { var f = new System.Collections.Specialized.StringCollection(); f.Add(FilePath); Clipboard.SetFileDropList(f); ShowToast(I18n.T("copied")); } catch { } })));
            m.Items.Add(new ToolStripMenuItem(I18n.T("copy_path"), null, (EventHandler)((s, e) => { try { Clipboard.SetText(FilePath); ShowToast(I18n.T("copied")); } catch { } })));
            Program.ShellMenuOpen = true;
            m.Closed += (s, ev) => { Program.ShellMenuOpen = false; if (_copyMenu == m) _copyMenu = null; };
            // 鼠标移出菜单就直接收起：悬停菜单不该一直挂着
            m.MouseLeave += (s, ev) => m.Close();
            _copyMenu = m;
            m.Show(_copyBtn.PointToScreen(new Point(0, _copyBtn.Height + 2)));
        }

        private bool IsSvgFile()
        {
            try
            {
                if (string.IsNullOrEmpty(FilePath)) return false;
                return Config.SvgExts.Contains(Path.GetExtension(FilePath));
            }
            catch { return false; }
        }

        // 复制 SVG 源码：直接读源文件，避免拿到源码框里被截断（64KB）的内容
        private void CopySvgCode()
        {
            try
            {
                if (!IsSvgFile() || !File.Exists(FilePath)) return;
                string code = File.ReadAllText(FilePath);
                if (code.Length == 0) return;
                Clipboard.SetText(code);
                ShowToast(I18n.T("copied"));
                Program.DebugLog("[copy] svg code " + FilePath);
            }
            catch (Exception ex) { Program.DebugLog("[copy] svg code fail: " + ex.Message); }
        }

        private void CopyClick()
        {
            try
            {
                var rtb = _content as RichTextBox;
                if (rtb != null && rtb.SelectionLength > 0)
                {
                    Clipboard.SetText(rtb.SelectedText);
                    ShowToast("已复制文本");
                    return;
                }
                if (!string.IsNullOrEmpty(FilePath))
                {
                    var files = new System.Collections.Specialized.StringCollection();
                    files.Add(FilePath);
                    Clipboard.SetFileDropList(files);
                    ShowToast("已复制文件");
                }
            }
            catch { }
        }

        private void ShowToast(string msg)
        {
            var t = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                BackColor = Config.BG,
                ShowInTaskbar = false,
                TopMost = true,
                Size = new Size(60, 22),
            };
            var lbl = new Label { Text = msg, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Config.FG, BackColor = Config.BG, Font = new Font("Microsoft YaHei", 9) };
            t.Controls.Add(lbl);
            t.Location = new Point(Left + (Width - t.Width) / 2, Top + 34);
            t.Show();
            var tm = new Timer { Interval = 800 };
            tm.Tick += (s, e) => { tm.Stop(); tm.Dispose(); t.Close(); t.Dispose(); };
            tm.Start();
        }

        private void SaveClick()
        {
            try
            {
                var rtb = _content as RichTextBox;
                if (rtb != null && !string.IsNullOrEmpty(FilePath))
                {
                    System.IO.File.WriteAllText(FilePath, rtb.Text, DetectFileEncoding(FilePath));
                    _textDirty = false;
                    _saveBtn.Visible = false;
                    UpdateToolbarBtnPos();
                }
            }
            catch { }
        }

        // 按原文件 BOM 决定写回编码：UTF-8(BOM)/UTF-16/UTF-32 原样保留，无 BOM 维持 UTF-8 无 BOM。
        // 否则 UTF-8(BOM) 文件被改成无 BOM 后，AHK 等按系统内码解析的程序会把中文读成乱码
        private static System.Text.Encoding DetectFileEncoding(string path)
        {
            try
            {
                var head = new byte[4];
                int n;
                using (var fs = File.OpenRead(path)) n = fs.Read(head, 0, 4);
                if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return new System.Text.UTF8Encoding(true);
                if (n >= 4 && head[0] == 0xFF && head[1] == 0xFE && head[2] == 0 && head[3] == 0) return System.Text.Encoding.UTF32;
                if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) return System.Text.Encoding.Unicode;
                if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) return System.Text.Encoding.BigEndianUnicode;
            }
            catch { }
            return new System.Text.UTF8Encoding(false);
        }
    }

    internal class RenameDialog : Form
    {
        public string NewName;
        private TextBox _entry;

        public RenameDialog(string oldName, int x, int y)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Math.Max(x - 60, 0), Math.Max(y - 10, 0));
            var border = new Panel { BackColor = Config.Border, Padding = new Padding(1) };
            _entry = new TextBox
            {
                Text = oldName,
                Font = new Font(Config.FontName, Config.FontSize),
                BackColor = Config.BG,
                ForeColor = Config.FG,
                BorderStyle = BorderStyle.None,
                Width = 200,
            };
            _entry.SelectAll();
            _entry.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Return) { NewName = _entry.Text.Trim(); DialogResult = DialogResult.OK; Close(); }
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            border.Controls.Add(_entry);
            Controls.Add(border);
            ClientSize = new Size(202, _entry.PreferredHeight + 2);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _entry.Focus();
        }
    }

    internal class PopupForm : Form
    {
        // 排序直接映射 Config：读旧值零改动，SetSort/ToggleSortDesc 写入即持久化候选
        public static string SortMode { get { return Config.SortMode; } set { Config.SortMode = value; } }
        public static bool SortDesc { get { return Config.SortDesc; } set { Config.SortDesc = value; } }
        public static Action OnSortChange;

        private readonly int _level;
        private readonly Action<PopupForm, int, string, int> _onOpenSub;
        private readonly Func<int, int, string> _dropResolver;
        private readonly Action<int> _onScheduleCloseAfter;
        private readonly List<RowLabel> _rows = new List<RowLabel>();
        private readonly Dictionary<string, BorderLabel> _sortBtns = new Dictionary<string, BorderLabel>();
        private TextBox _searchBox;
        private Panel _searchBorder;
        // 顶部名称行：当前文件夹名 + 编辑图标（点击就地改名）
        private Panel _nameRow;
        private SmoothLabel _titleLbl;
        private Label _editBtn;
        private TextBox _nameEdit;
        private FlowLayoutPanel _sortFlow;
        private SlimScrollPanel _listPanel;
        private SlimScrollBar _listScrollBar;
        private bool _syncingScroll;   // 滑块↔内容层双向同步防回环（避免动画被 sb.Scroll 回调误杀）
        private const int SlimBarW = 10;   // 自绘细滚动条占宽（行右侧让位）
        // 列表行区左右内缩/底部留白：与顶部名称栏、排序栏、底部状态栏的左右内缩(6)对齐，
        // 行不再顶到面板左右边缘与底边，视觉上与顶部呼应
        private const int ListPadLr = 6;
        private const int ListPadBottom = 6;
        private string _path;
        private int _headH;
        private int _panelW = 260;
        // 固定时记下自身尺寸：后续刷新沿用，避免条目少时被内容宽度或全局默认值改写
        private int _pinnedW, _pinnedH;
        // 进入移动/调整大小时的尺寸，用于区分“只是挪了位置”和“真的改了尺寸”
        private Size _sizeMoveStart;

        private Timer _hoverTimer;
        private Timer _dragSubTimer;      // 拖拽悬停文件夹的级联调度（OLE 拖拽期间 MouseEnter 不触发）
        private RowLabel _dragSubRow;
        private string _hoverPath;
        private int _hoverRowY;
        private Timer _previewTimer;
        private string _previewPath;
        private PreviewForm _previewForm;
        private Timer _previewCloseTimer;
        private bool _menuOpen;
        private bool _dragging;
        // 互换布局：分栏分隔线拖动中（按下位置在线±4px 热区）；_splitDragOff=按下点与线的偏移
        private bool _splitDrag;
        private int _splitDragOff;
        // 互换布局：上次 MouseMove 是否在左侧安全区（仅区域切换时调度/取消，避免右栏微动反复触发子列表闪烁）
        private bool _wasInSide;
        // 互换布局：左侧不触发悬停调度的安全区宽度（按钮组+大小文本列）
        private int _rowSideZone;
        private int? _lastClickIdx;
        private bool _dragged;
        private Point _dragStart;
        private RowLabel _dragRow;
        private RowLabel _activeDirRow;
        private RowLabel _hlRow;
        private RowLabel _contextRow;
        private bool _pinned;
        private Label _pinBtn;
        private Label _refreshBtn;  // 表头 ↻ 手动刷新（仅固定面板显示）
        private Label _pinCardBtn;   // 面板顶部"钉为桌面卡片"按钮
        private Label _rowDelBtn;
        private Label _rowCopyBtn;  // 行悬停时显示的"复制路径"按钮
        private Label _rowPinBtn;   // 行悬停时显示的"钉为桌面卡片"按钮
        private Label _rowRenameBtn; // 行悬停时显示的"重命名"按钮（复制按钮左侧）
        private TextBox _rowEdit;   // 行内重命名编辑框（激活期间存在）
        private RowLabel _rowEditRow; // 正被行内重命名的行（Esc 取消时恢复显示）

        public bool Pinned { get { return _pinned; } }

        // 圆角模式（WindowCorners=1）下 WndNcBorder 的 WM_NCCALCSIZE 把客户区四周内缩
        // FramePad 像素，WinForms 的 ClientSize 并不知情：设 260 宽实际只剩 254，底部状态栏
        // “6 项”被描边裁断、四边留白不对称。这里整体改写 ClientSize 语义——写入口补上描边
        // 占位、读取口返回真实客户区，类内所有布局/尺寸读写自动与描边协调；
        // 直角模式 FramePad=0，读写与原生行为完全一致
        public new Size ClientSize
        {
            get
            {
                // 不直接摸 Handle：getter 访问 Handle 会强制提前创建窗口句柄，打乱免激活时序
                var s = IsHandleCreated ? Win11.RealClient(Handle) : Size.Empty;
                return s.IsEmpty ? base.ClientSize : s;
            }
            set { base.ClientSize = new Size(value.Width + Win11.FramePad * 2, value.Height + Win11.FramePad * 2); }
        }

        public PopupForm(int level, Action<PopupForm, int, string, int> onOpenSub, Func<int, int, string> dropResolver, Action<int> onScheduleCloseAfter)
        {
            _level = level;
            _onOpenSub = onOpenSub;
            _dropResolver = dropResolver;
            _onScheduleCloseAfter = onScheduleCloseAfter;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // 最小宽度：排序栏 + ⧉ + 右上两按钮不互相覆盖；拖动时即受限，与持久化一致
            MinimumSize = new Size(280, 60);
            BackColor = Config.ListBg;
            DoubleBuffered = true;
            AllowDrop = true;
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    // 正在行内重命名 → 取消重命名；就地改名 → 取消改名；搜索展开中 → 先收起搜索；否则关闭面板
                    if (_rowEdit != null) { e.SuppressKeyPress = true; CancelRowRename(); }
                    else if (_nameEdit != null && _nameEdit.Visible) EndTitleRename(false);
                    else if (_searchBorder != null && _searchBorder.Visible) ToggleSearch();
                    else Close();
                }
            };
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy; };
            DragOver += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy; };
            DragDrop += (s, e) =>
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files == null || string.IsNullOrEmpty(_path)) return;
                bool move = e.Effect == DragDropEffects.Move;
                Program.DebugLog("[drop-form] effect=" + e.Effect + " keyState=" + e.KeyState + " move=" + move + " dstDir=" + _path + " files=" + string.Join(";", files));
                foreach (var f in files)
                {
                    if (!string.IsNullOrEmpty(f) && (File.Exists(f) || Directory.Exists(f)))
                    {
                        if (move) DoMove(f, _path);
                        else DoCopy(f, _path);
                    }
                }
            };
        }

        private static readonly Dictionary<string, Image> _iconCache = new Dictionary<string, Image>();

        private static readonly Dictionary<string, Image> _uiIconCache = new Dictionary<string, Image>();
        internal static Image UiIcon(string key, Func<Image> create)
        {
            string fullKey = key + "|" + (Config.IsDark ? "d" : "l");
            Image img;
            if (_uiIconCache.TryGetValue(fullKey, out img)) return img;
            img = create();
            _uiIconCache[fullKey] = img;
            return img;
        }
        internal static void ClearUiIconCache()
        {
            foreach (var kv in _uiIconCache) { try { kv.Value.Dispose(); } catch { } }
            _uiIconCache.Clear();
        }

        // 一律不抢前台：面板只是悬停展示，抢前台会打断资源管理器的输入/重命名。
        // 搜索框需要输入时由用户点击激活（点击属于真实输入，允许切换前台）
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win11.Apply(Handle, Config.IsDark, Config.Border);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            // 拖动右下角手柄改宽度后同步顶部与行宽度（只同步宽度，不重排行高）
            if (ClientSize.Width != _panelW) SyncWidth(ClientSize.Width);
            if (_pinBtn != null) _pinBtn.BringToFront();
            if (_pinCardBtn != null) _pinCardBtn.BringToFront();
        }

        private PictureBox _resizeFreeze;
        private Image _resizeFreezeImg;

        // 拖动尺寸时冻结列表：快照盖住客户区 + 隐藏几百行 Label（AutoScroll 容器每次 WM_SIZE
        // 都要对全部子行重算布局+重绘，文件多时每帧都卡）。拖动全程系统只画这张静态图。
        private void _fnBeginResizeFreeze()
        {
            try
            {
                _fnEndResizeFreeze();
                if (_listPanel == null || ((SlimScrollPanel)_listPanel).ContentCount <= 40) return;
                if (!Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
                var src = PointToScreen(Point.Empty);
                var img = new Bitmap(ClientSize.Width, ClientSize.Height);
                using (var g = Graphics.FromImage(img)) g.CopyFromScreen(src.X, src.Y, 0, 0, img.Size);
                _resizeFreezeImg = img;
                _resizeFreeze = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Normal, BackColor = LevelBgColor(_level), Bounds = new Rectangle(Point.Empty, ClientSize) };
                Controls.Add(_resizeFreeze);
                _resizeFreeze.BringToFront();
                _listPanel.Visible = false;
            }
            catch { _fnEndResizeFreeze(); }
        }

        private void _fnEndResizeFreeze()
        {
            if (_resizeFreeze != null) { try { Controls.Remove(_resizeFreeze); _resizeFreeze.Dispose(); } catch { } _resizeFreeze = null; }
            if (_resizeFreezeImg != null) { try { _resizeFreezeImg.Dispose(); } catch { } _resizeFreezeImg = null; }
            if (_listPanel != null && !_listPanel.Visible) _listPanel.Visible = true;
        }

        protected override void WndProc(ref Message m)
        {
            // 圆角模式描边与自身背景同色：亮色描边压在深色配色上很突兀，
            // 同色后只剩 DWM 圆角外形（回调动态取 BackColor，主题/层级着色跟随）
            if (Win11.WndNcBorder(Handle, ref m, () => BackColor)) return;
            if (m.Msg == 0x84)   // WM_NCHITTEST
            {
                var lp = m.LParam.ToInt64();
                var cp = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int cw = ClientSize.Width, ch = ClientSize.Height;
                if (cp.X >= cw - 6 && cp.Y >= ch - 6) { m.Result = (IntPtr)17; return; }
                if (cp.X >= cw - 6 && cp.Y >= 0 && cp.Y < ch) { m.Result = (IntPtr)11; return; }
                if (cp.Y >= ch - 6 && cp.X >= 0 && cp.X < cw) { m.Result = (IntPtr)12; return; }
            }
            else if (m.Msg == 0x231) { _sizeMoveStart = ClientSize; _fnBeginResizeFreeze(); }
            else if (m.Msg == 0x232)
            {
                _fnEndResizeFreeze();
                if (ClientSize != _sizeMoveStart)
                {
                    Config.PanelWidth = ClientSize.Width;
                    Config.PanelHeight = ClientSize.Height;
                    Config.PanelSizeAuto = false;   // 手动拖过 = 转纯记忆模式
                    // 固定面板手动改过尺寸后，后续刷新沿用新尺寸
                    if (_pinned) { _pinnedW = ClientSize.Width; _pinnedH = ClientSize.Height; }
                    Config.Save();
                    Program.DebugLog("[panel-size] resize-end " + ClientSize.Width + "x" + ClientSize.Height);
                }
                else Program.DebugLog("[panel-size] move-end, size unchanged -> keep defaults " + Config.PanelWidth + "x" + Config.PanelHeight);
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CancelDragSub();
            base.OnFormClosed(e);
        }

        // 入口列表用的固定目录图标（缓存 key "<dir>"）
        internal static Image GetEntryIcon()
        {
            return GetIcon(Environment.GetFolderPath(Environment.SpecialFolder.System), true);
        }

        // 按路径返回图标：文件夹=文件夹图标，文件=扩展名关联图标
        internal static Image GetEntryIconFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return GetEntryIcon();
            return GetIcon(path, Directory.Exists(path));
        }

        private static Image GetIcon(string full, bool isDir)
        {
            string ext = isDir ? "" : Path.GetExtension(full);
            string key = isDir ? "<dir>" : ext;
            // lnk 的图标由目标决定（指向文件夹=文件夹图标，指向 png=图片图标），
            // 按目标类型分桶缓存——否则一个指向文件夹的快捷方式会污染整个 ".lnk" 桶，
            // 让同列表里指向 png/mp4 的快捷方式也全显示文件夹图标
            if (!isDir && string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string t = PreviewProvider.GetLnkTarget(full);
                if (!string.IsNullOrEmpty(t))
                    key = Directory.Exists(t) ? ".lnk|dir" : ".lnk|" + Path.GetExtension(t);
            }
            Image cached;
            if (_iconCache.TryGetValue(key, out cached)) return cached;
            Image img = null;
            var shfi = new NativeMethods.SHFILEINFOW();
            NativeMethods.SHGetFileInfo(full, 0, ref shfi, (uint)Marshal.SizeOf(typeof(NativeMethods.SHFILEINFOW)), 0x100 | 0x1);
            if (shfi.hIcon != IntPtr.Zero)
            {
                try { using (var icon = Icon.FromHandle(shfi.hIcon)) img = icon.ToBitmap(); }
                catch { }
                NativeMethods.DestroyIcon(shfi.hIcon);
            }
            _iconCache[key] = img;
            return img;
        }


        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB";
        }

        // 顶部控件与行宽度按面板宽度同步：拖动手柄改宽后必须刷新，
        // 否则排序行/名称行停在旧宽度、固定按钮位置不对
        private void SyncWidth(int w)
        {
            if (w < 40) return;
            _panelW = w;
            if (_level == 0)
            {
                foreach (Control c in Controls)
                {
                    if (c is Panel && c.BackColor == Config.Border) c.Width = w - 12;
                }
                if (_searchBox != null) _searchBox.Width = w - 14;
                if (_nameRow != null)
                {
                    _nameRow.Width = w - 12;
                    if (_titleLbl != null) _titleLbl.Width = Math.Max(40, w - 12 - 24);
                    if (_editBtn != null) _editBtn.Left = w - 12 - 18;
                    if (_nameEdit != null) _nameEdit.Width = Math.Max(60, w - 12 - 24);
                }
            }
            // 表头三件套所有层级都同步（子面板排序栏/固定按钮同样跟随宽度）
            if (_sortFlow != null) _sortFlow.Width = w - 12;
            if (_pinBtn != null) _pinBtn.Location = new Point(w - 26, 7);
            if (_pinCardBtn != null) _pinCardBtn.Location = new Point(w - 26 - 22, 7);
            if (_listPanel != null)
            {
                var lp = (SlimScrollPanel)_listPanel;
                int sw = lp.CanScroll ? SlimBarW : 0;
                _listPanel.Width = w - ListPadLr * 2;
                foreach (var r in _rows) r.Width = w - ListPadLr * 2 - sw;
                _fnSyncListScroll();
            }
        }

        // 自绘滚动条与列表范围/位置同步（行重建、面板缩放后调用）
        private void _fnSyncListScroll()
        {
            if (_listScrollBar == null || _listPanel == null) return;
            var lp = (SlimScrollPanel)_listPanel;
            // 上下贴齐列表区：留 3px 会在滚动条两端露出一小块背景色（视觉断口）
            const int padY = 0;
            _listScrollBar.Location = new Point(_panelW - ListPadLr - SlimBarW, _listPanel.Top + padY);
            _listScrollBar.Size = new Size(SlimBarW, Math.Max(1, _listPanel.Height - padY * 2));
            _listScrollBar.Maximum = Math.Max(1, lp.ContentHeight);
            _listScrollBar.LargeChange = Math.Max(1, _listPanel.Height);
            _listScrollBar.Value = lp.ContentScrollPos;
            _listScrollBar.BringToFront();
        }


        public bool ShowPanel(string path, int x, int y)
        {
            CancelHover();
            CancelPreview();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(path).Select(e => Path.GetFileName(e)).ToArray(); }
            catch { return false; }
            Array.Sort(entries, StringComparer.Ordinal);
            int originalCount = entries.Length;
            // 先按当前排序（目录优先）排好再截断：先截断会按 Ordinal 序砍掉排在后面的
            // 中文名条目（实测 D:\下载 64 个文件夹只剩 23 个 ASCII 名的）
            try { SortEntries(path, ref entries); } catch (Exception ex) { Program.DebugLog("[panel] SortEntries fail: " + ex.Message); }
            if (entries.Length > Config.MaxItems)
                entries = entries.Take(Config.MaxItems).ToArray();
            Program.DebugLog("[panel] ShowPanel level=" + _level + " lang=" + I18n.Lang + " path=" + path + " entries=" + entries.Length);
            _path = path;

            SuspendLayout();
            // 视觉冻结：刷新前截屏，旧控件销毁后盖住客户区，重建期间不露白底（文件多时重建耗时长）
            Image freezeImg = null;
            try
            {
                if (Visible && ClientSize.Width > 0 && ClientSize.Height > 0)
                {
                    var src = PointToScreen(Point.Empty);
                    freezeImg = new Bitmap(ClientSize.Width, ClientSize.Height);
                    using (var g = Graphics.FromImage(freezeImg)) g.CopyFromScreen(src.X, src.Y, 0, 0, freezeImg.Size);
                }
            }
            catch { if (freezeImg != null) { freezeImg.Dispose(); freezeImg = null; } }
            while (Controls.Count > 0) Controls[0].Dispose();
            Controls.Clear();
            PictureBox freeze = null;
            if (freezeImg != null)
            {
                // 不用 Dock=Fill：SuspendLayout 挂起期间 Dock 不生效（PerformLayout 在移除本层之后才跑），
                // 必须直接设 Bounds 立即覆盖客户区，否则重建期间泵消息时新控件白底被绘制出来
                freeze = new PictureBox { Image = freezeImg, SizeMode = PictureBoxSizeMode.Normal, BackColor = LevelBgColor(_level), Bounds = new Rectangle(Point.Empty, ClientSize) };
                Controls.Add(freeze);
                freeze.BringToFront();
            }

            _hlRow = null;
            _rows.Clear();
            _sortBtns.Clear();
            _rowEdit = null;   // 重建会销毁编辑框，清掉死引用（rename 按钮才会恢复显示）
            _rowEditRow = null;

            int curY = 5;

            // ---- 表头（所有层级）：排序栏 + 固定/钉卡按钮——子列表也能直接固定 ----
            // 实际拖动后才固定；单纯点击不固定
            MouseEventHandler drag = (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                // 双击顶部操作栏 = 资源管理器打开当前文件夹（手动判定，见 _fnHeaderDoubleClick）
                if (_fnHeaderDoubleClick()) { _fnOpenInExplorer(); return; }
                var before = Location;
                NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                if (Location != before && !_pinned) TogglePinPopup();
            };
            // 顶部区域空白处右键 = 选项菜单
            MouseEventHandler blankMenu = (s, e) => { if (e.Button == MouseButtons.Right && Program.TrayMenu != null) { _menuOpen = true; Program.TrayMenu.Show(Cursor.Position); } };

            // 右侧留 48px 给 _pinCardBtn/_pinBtn：FlowLayoutPanel 不裁切溢出子控件，
            // 占满宽度时 ⧉ 会钻到按钮下面被覆盖；收窄后行尾空白也是拖拽入口
            var sortFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Location = new Point(6, curY), AutoSize = false, Height = 26, Width = _panelW - 60, BackColor = Config.ListHdbg };
                _sortFlow = sortFlow;
                foreach (var kv in Config.SortLabels)
                {
                var btn = new BorderLabel
                {
                    Text = kv.Value,
                    Font = new Font(Config.FontName, Config.FontSize),
                    Padding = new Padding(5, 1, 13, 1),
                    AutoSize = true,
                    BackColor = Config.BG,
                    ForeColor = Config.FG,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 2, 0),
                };
                    var mode = kv.Key;
                    btn.Click += (s, e) => SetSort(mode);
                    Tips.Set(btn, I18n.T("tip_sort"));
                    _sortBtns[mode] = btn;
                    sortFlow.Controls.Add(btn);
                }
                // 复制路径并入文件夹名称点击（见 _titleLbl）；排序栏只留排序与刷新
                // 手动刷新：仅固定面板显示（非固定面板本身随悬停重建，无需刷新）
                _refreshBtn = new Label
                {
                    Text = "↻",
                    Font = new Font(Config.FontName, Config.FontSize),
                    AutoSize = true,
                    BackColor = Config.ListHdbg,
                    ForeColor = Config.ListFg,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 2, 0),
                    Padding = new Padding(5, 1, 5, 1),
                    Visible = _pinned,
                };
                _refreshBtn.Click += (s, e) => RefreshPanel();
                Tips.Set(_refreshBtn, I18n.T("refresh"));
                sortFlow.Controls.Add(_refreshBtn);
                Controls.Add(sortFlow);
                curY += 28;

                // 收藏夹 chips（仅顶级面板）：点一下本面板直接跳到该文件夹，不用回桌面触发区。
                // 取当前收藏分组的条目；宽度装不下就截断（tooltip 有完整路径）。无收藏不占高度
                if (_level == 0 && Config.FavoriteGroups != null && Config.FavoriteCurGroup >= 0
                    && Config.FavoriteCurGroup < Config.FavoriteGroups.Count)
                {
                    var favItems = Config.FavoriteGroups[Config.FavoriteCurGroup].Items;
                    if (favItems != null && favItems.Count > 0)
                    {
                        var favFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = false, Height = 24, Width = _panelW - 12, Location = new Point(6, curY), BackColor = Config.ListHdbg, Margin = Padding.Empty };
                        int shown = 0;
                        foreach (var fav in favItems)
                        {
                            if (shown >= 8 || !Directory.Exists(fav)) continue;
                            var chip = new BorderLabel
                            {
                                Text = Path.GetFileName(fav.TrimEnd(Path.DirectorySeparatorChar)),
                                Font = new Font(Config.FontName, 8f),
                                Padding = new Padding(6, 2, 6, 2),
                                AutoSize = true,
                                BackColor = Config.BG,
                                ForeColor = Config.ListFg,
                                Cursor = Cursors.Hand,
                                Margin = new Padding(0, 1, 3, 1),
                            };
                            string favPath = fav;
                            chip.Click += (s2, e2) => { Program.DebugLog("[fav-chip] jump " + favPath); ShowPanel(favPath, Left, Top); };
                            Tips.Set(chip, favPath);
                            favFlow.Controls.Add(chip);
                            shown++;
                            // 宽度装不下就停（FlowLayoutPanel 不裁切溢出，会钻到边缘按钮下面）
                            if (favFlow.PreferredSize.Width > _panelW - 24) { chip.Dispose(); favFlow.Controls.Remove(chip); break; }
                        }
                        if (favFlow.Controls.Count > 0)
                        {
                            Controls.Add(favFlow);
                            curY += 26;
                        }
                        else favFlow.Dispose();
                    }
                }

                _pinBtn = new Label
                {
                    Size = new Size(20, 18),
                    AutoSize = false,
                    Location = new Point(_panelW - 26, 7),
                    BackColor = Config.ListHdbg,
                    Image = PreviewForm.PinIcon(_pinned, Config.ListFg),
                    Cursor = Cursors.Hand,
                };
                Tips.Set(_pinBtn, I18n.T("tip_pin_panel"));
                _pinBtn.Click += (s, e) => TogglePinPopup();
                Controls.Add(_pinBtn);

                _pinCardBtn = new Label
                {
                    Size = new Size(20, 18),
                    AutoSize = false,
                    Location = new Point(_panelW - 26 - 22, 7),
                    BackColor = Config.ListHdbg,
                    Image = PreviewForm.PinLaunchIcon(false, Config.ListFg),
                    Cursor = Cursors.Hand,
                };
                Tips.Set(_pinCardBtn, I18n.T("tip_pin_card"));
                _pinCardBtn.Click += (s, e) => { if (!string.IsNullOrEmpty(_path)) Program.TogglePin(_path); };
                Controls.Add(_pinCardBtn);
                // 表头公共交互：拖动固定 + 空白右键菜单（子面板同样可用）
                sortFlow.MouseDown += drag;
                sortFlow.MouseUp += blankMenu;

            if (_level == 0)
            {
                // 名称行：当前文件夹名（过长省略，悬停 tooltip 显示完整路径）+ 编辑图标（点击就地改名）
                // 底色不参与层级着色（Tag=noTint）：否则名称条上下 2px 内边距会露出 LevelBgColor 的彩色描边，
                // 在浅色主题下呈粉红一圈，与面板割裂
                _nameRow = new Panel { BackColor = Config.ListBg, Tag = "noTint", Location = new Point(6, curY), Size = new Size(_panelW - 12, 22) };
                _titleLbl = new SmoothLabel
                {
                    Text = ShowName(path),
                    Font = new Font(Config.FontName, Config.FontSize, FontStyle.Bold),
                    ForeColor = Config.ListFg,
                    BackColor = Config.ListHdbg,
                    AutoEllipsis = true,
                    UseCompatibleTextRendering = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Cursor = Cursors.Hand,
                    Location = new Point(0, 2),
                    Size = new Size(Math.Max(40, _panelW - 12 - 24), 18),
                };
                var titleDown = new Point(int.MinValue, int.MinValue);
                bool titleDbl = false;   // 手动双击已触发：随后的抬起不再走单击复制路径
                _titleLbl.MouseDown += (s, e) =>
                {
                    titleDown = e.Location;
                    // 双击标题 = 打开文件夹（手动判定：e.Clicks 受单击复制等时序影响不可靠）
                    if (e.Button == MouseButtons.Left && _fnHeaderDoubleClick()) { titleDbl = true; _fnOpenInExplorer(); }
                };
                // 点击粗体文件夹名 = 复制路径（拖动面板后不误触发）；重命名走右侧 ✎ 按钮
                _titleLbl.MouseUp += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (titleDbl) { titleDbl = false; return; }   // 双击的第二次抬起：不再复制路径
                    if (e.Clicks >= 2) return;
                    if (Math.Abs(e.Location.X - titleDown.X) > 3 || Math.Abs(e.Location.Y - titleDown.Y) > 3) return;
                    try { Clipboard.SetText(path); ShowToast(I18n.T("copied")); } catch { }
                };
                // 标题提示：完整路径 + 点击复制说明（统一走 Tips，受开关控制）
                Tips.Set(_titleLbl, path + "\n" + I18n.T("copy_path"));
                _editBtn = new Label
                {
                    BackColor = Config.ListHdbg,
                    Image = PreviewForm.EditIcon(Config.ListFg),
                    Size = new Size(18, 18),
                    AutoSize = false,
                    Cursor = Cursors.Hand,
                    // 右缘与名称行右缘齐平（= 顶部 ⧉/📌 按钮的右缘），不留多余缩进
                    Location = new Point(_panelW - 12 - 18, 2),
                    TextAlign = ContentAlignment.MiddleCenter,
                };
                // 悬停提亮一档，与列表行内按钮组保持一致的反馈
                _editBtn.MouseEnter += (s, e) => _editBtn.BackColor = Config.ListBtnHv;
                _editBtn.MouseLeave += (s, e) => _editBtn.BackColor = Config.ListHdbg;
                Tips.Set(_editBtn, I18n.T("tip_rename_dir"));
                _editBtn.Click += (s, e) => BeginTitleRename();
                _nameEdit = new TextBox
                {
                    Font = new Font(Config.FontName, Config.FontSize),
                    // 顶栏改名框跟随列表配色（noTint 防止 tint 用主题基调改写）
                    BackColor = Config.ListBg,
                    ForeColor = Config.ListFg,
                    Tag = "noTint",
                    BorderStyle = BorderStyle.FixedSingle,
                    Location = new Point(0, 1),
                    Width = Math.Max(60, _panelW - 12 - 24),
                    Visible = false,
                };
                _nameEdit.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Return) { e.SuppressKeyPress = true; EndTitleRename(true); }
                    else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndTitleRename(false); }
                };
                // 点别处 = 提交（与资源管理器重命名一致）
                _nameEdit.LostFocus += (s, e) => { if (_nameEdit.Visible) EndTitleRename(true); };
                _nameRow.Controls.Add(_titleLbl);
                _nameRow.Controls.Add(_editBtn);
                _nameRow.Controls.Add(_nameEdit);
                Controls.Add(_nameRow);
                curY += 22 + 2;

                // 搜索框：默认隐藏，Ctrl+F 才展开（常驻输入框会带来假焦点/IME 遮挡问题）
                // 搜索框跟随列表配色（ListBg/ListMu/ListFg/ListHdbg）：主题色经 tint 只换
                // 浅色基调，深色方案下背景/边框会残留浅色；Tag=noTint 防止 tint 再改写
                _searchBorder = new Panel { BackColor = Config.ListHdbg, Tag = "noTint", Location = new Point(6, curY), Size = new Size(_panelW - 12, 22), Visible = false };
                _searchBox = new TextBox
                {
                    Text = I18n.T("search"),
                    Font = new Font(Config.FontName, Config.FontSize),
                    BackColor = Config.ListBg,
                    ForeColor = Config.ListMu,
                    BorderStyle = BorderStyle.None,
                    Location = new Point(1, 2),
                    Width = _searchBorder.Width - 2,
                    Tag = "noTint",
                };
                _searchBox.GotFocus += (s, e) => { if (_searchBox.Text == I18n.T("search")) { _searchBox.Text = ""; _searchBox.ForeColor = Config.ListFg; } };
                _searchBox.LostFocus += (s, e) =>
                {
                    // 诊断：搜索中失焦记录抢走前台的是谁（正常应只有 Esc/用户主动点击）
                    try
                    {
                        var fg = NativeMethods.GetForegroundWindow();
                        var sbf = new System.Text.StringBuilder(64);
                        if (fg != IntPtr.Zero) NativeMethods.GetClassName(fg, sbf, 64);
                        Program.DebugLog("[search] lost-focus fg=0x" + fg.ToString("X") + " cls=" + sbf.ToString() + " stillOpen=" + (_searchBorder != null && _searchBorder.Visible));
                    }
                    catch { }
                    if (string.IsNullOrEmpty(_searchBox.Text)) { _searchBox.Text = I18n.T("search"); _searchBox.ForeColor = Config.ListMu; }
                };
                _searchBox.TextChanged += (s, e) => FilterRows(_searchBox.Text);
                Tips.Set(_searchBox, I18n.T("tip_search"));
                _searchBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; ToggleSearch(); } };
                _searchBorder.Controls.Add(_searchBox);
                Controls.Add(_searchBorder);
                _headH = curY;

                _nameRow.MouseDown += drag;
                _searchBorder.MouseDown += drag;
                _nameRow.MouseUp += blankMenu;
                _searchBorder.MouseUp += blankMenu;
            }
            // 表头底基准（子面板也有了排序栏，列表从此处起步）
            _headH = curY;

            // 自管偏移滚动：SlimScrollBar 细滑块与内容层双向同步（滑块随滚动实时移动）
            // _syncingScroll 防回环：动画更新 sb.Value 会再触发 sb.Scroll，
            // 不加守卫会回调 SetOffset→StopAnim 把缓动动画杀在第一帧
            _listPanel = new SlimScrollPanel { Location = new Point(ListPadLr, curY), BackColor = Config.ListCanvas };
            _listPanel.MouseUp += blankMenu;
            // 内容层铺满 viewport，空白处右键同样弹菜单（否则被 content 挡住收不到）
            _listPanel.Content.MouseUp += blankMenu;
            Controls.Add(_listPanel);
            bool dark = Config.IsDark;
            _listScrollBar = new SlimScrollBar
            {
                // SlimScrollBar 构造里默认 Dock=Right；停靠会让滚动条铺满整个窗口高度（顶到标题栏/排序栏），
                // 这里取消停靠，改由 _fnSyncListScroll 按列表范围定位
                Dock = DockStyle.None,
                // 不用 Transparent：透明模拟重画的是父窗体背景（主题混色），而滚动条实际压在
                // _listPanel（ListCanvas）上，深色方案下两端会露出两块浅色。直接取行区同色，
                // 轨道仍是半透明黑叠加，视觉效果不变
                BackColor = Config.ListCanvas,
                Style = SlimScrollBar.BarStyle.Thin,
                ThumbColor = dark ? ColorTranslator.FromHtml("#4a5260") : ColorTranslator.FromHtml("#b8bec8"),
                ThumbHotColor = dark ? ColorTranslator.FromHtml("#6a7484") : ColorTranslator.FromHtml("#8a919b"),
            };
            _listScrollBar.Scroll += (s, e) =>
            {
                if (!_syncingScroll && _listPanel != null) _listPanel.SetOffset(_listScrollBar.Value);
            };
            _listPanel.ScrollChanged += (s, e) =>
            {
                if (_listScrollBar == null || _listPanel == null) return;
                _syncingScroll = true;
                try { _listScrollBar.Value = _listPanel.ContentScrollPos; }
                finally { _syncingScroll = false; }
            };
            Controls.Add(_listScrollBar);

            _rowDelBtn = new Label
            {
                Size = new Size(18, 18),
                AutoSize = false,
                BackColor = Config.ListHv,
                Image = PreviewForm.TrashIcon(Config.ListFg),
                Cursor = Cursors.Hand,
                Visible = false,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _rowDelBtn.Click += (s, e) =>
            {
                var p = _rowDelBtn.Tag as string;
                if (string.IsNullOrEmpty(p)) return;
                var row = _rows.FirstOrDefault(r => string.Equals(r.Full, p, StringComparison.OrdinalIgnoreCase));
                if (row == null) return;
                // 悬停行在选中集合里且多选 → 整组删除（确认框带数量）；单删保持原静默行为
                var sel = SelRows();
                if (row.Selected && sel.Count > 1)
                    RecycleRows(sel);
                else if (PreviewForm.RecycleFile(p)) RefreshPanel();
            };

            ((SlimScrollPanel)_listPanel).AddContent(_rowDelBtn);
            _rowDelBtn.BringToFront();
            Tips.Set(_rowDelBtn, I18n.T("delete"));

            _rowCopyBtn = new Label
            {
                Size = new Size(18, 18),
                AutoSize = false,
                BackColor = Config.ListHv,
                Image = PreviewForm.CopyIcon(Config.ListFg),
                Cursor = Cursors.Hand,
                Visible = false,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _rowCopyBtn.Click += (s, e) =>
            {
                var p = _rowCopyBtn.Tag as string;
                if (string.IsNullOrEmpty(p)) return;
                try { Clipboard.SetText(p); ShowToast(I18n.T("copied")); } catch { }
            };
            ((SlimScrollPanel)_listPanel).AddContent(_rowCopyBtn);
            _rowCopyBtn.BringToFront();
            Tips.Set(_rowCopyBtn, I18n.T("copy_path"));

            _rowPinBtn = new Label
            {
                Size = new Size(18, 18),
                AutoSize = false,
                BackColor = Config.ListHv,
                Image = PreviewForm.PinLaunchIcon(false, Config.ListFg),
                Cursor = Cursors.Hand,
                Visible = false,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _rowPinBtn.Click += (s, e) =>
            {
                var p = _rowPinBtn.Tag as string;
                if (string.IsNullOrEmpty(p)) return;
                Program.TogglePin(p);
            };
            ((SlimScrollPanel)_listPanel).AddContent(_rowPinBtn);
            _rowPinBtn.BringToFront();
            Tips.Set(_rowPinBtn, I18n.T("tip_pin_card"));

            _rowRenameBtn = new Label
            {
                Size = new Size(18, 18),
                AutoSize = false,
                BackColor = Config.ListHv,
                Image = PreviewForm.EditIcon(Config.ListFg),
                Cursor = Cursors.Hand,
                Visible = false,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _rowRenameBtn.Click += (s, e) =>
            {
                var row = _rowRenameBtn.Tag as RowLabel;
                if (row != null) BeginRowRename(row);
            };
            ((SlimScrollPanel)_listPanel).AddContent(_rowRenameBtn);
            _rowRenameBtn.BringToFront();
            Tips.Set(_rowRenameBtn, I18n.T("rename"));

            // 行内按钮组：悬停高亮（进入提亮一档，移开回落）
            foreach (var b in new Label[] { _rowDelBtn, _rowCopyBtn, _rowPinBtn, _rowRenameBtn })
            {
                var btn = b;
                btn.MouseEnter += (s, e) => btn.BackColor = Config.ListBtnHv;
                btn.MouseLeave += (s, e) => btn.BackColor = Config.ListHv;
            }

            var font = new Font(Config.FontName, Config.FontSize);
            int maxW = 200;
            var sizeTexts = new string[entries.Length];
            var dirFlags = new bool[entries.Length];
            var sizeWs = new float[entries.Length];
            float maxSizeW = 0;
            var fileSizes = new long[entries.Length];
            long maxFileSize = 0;
            using (var g = CreateGraphics())
            {
                foreach (var name in entries)
                {
                    var sz = g.MeasureString(name, font);
                    if (sz.Width + 36 > maxW) maxW = (int)sz.Width + 36;
                }
                // 预取文件大小 / 文件夹子项数（仅 NTFS 元数据，不读文件内容）

                for (int i = 0; i < entries.Length; i++)
                {
                    string full = Path.Combine(path, entries[i]);
                    bool isDir = Directory.Exists(full);
                    dirFlags[i] = isDir;
                    try
                    {
                        if (isDir)
                        {
                            sizeTexts[i] = Directory.GetFileSystemEntries(full).Length + " " + I18n.T("items");
                        }
                        else
                        {
                            long fs = new FileInfo(full).Length;
                            fileSizes[i] = fs;
                            if (fs > maxFileSize) maxFileSize = fs;
                            sizeTexts[i] = FormatSize(fs);
                        }
                    }
                    catch { }
                    if (sizeTexts[i] != null)
                    {
                        sizeWs[i] = g.MeasureString(sizeTexts[i], font).Width;
                        if (sizeWs[i] > maxSizeW) maxSizeW = sizeWs[i];
                    }
                }
                if (maxSizeW > 0) maxW += (int)maxSizeW + 10;
            }
            // 所有层级都有排序按钮栏，最小宽度须容纳表头
            foreach (var btn in _sortBtns.Values)
            {
                int bw = btn.Width + btn.Margin.Horizontal + 6;
                if (bw > maxW) maxW = bw;
            }
            // 手动调整过的宽度强制沿用（拖窄也持久化，长文件名 AutoEllipsis 截断）
            if (Config.PanelWidth >= 200) maxW = Config.PanelWidth;
            if (maxW > Math.Max(400, Config.PanelWidth)) maxW = Math.Max(400, Config.PanelWidth);
            // 已固定的面板沿用固定时的宽度：条目少的目录刷新后不该被拉回默认宽度
            if (_pinned && _pinnedW > 0) maxW = _pinnedW;
            _panelW = maxW;
            // 互换布局分栏线：拖动过的位置沿用（clamp 进当前面板宽），否则自动=按钮组(72)+大小列+缓冲
            int autoZone = 72 + (int)maxSizeW + 12;
            int minZone = 78, maxZone = _panelW - 130;
            if (maxZone < minZone) maxZone = minZone;
            _rowSideZone = Config.RowSplitX > 0 ? Math.Min(Math.Max(Config.RowSplitX, minZone), maxZone) : autoZone;

            // 所有层级都同步：子面板表头（sortFlow/pinBtn/行宽）也要按最终宽度校正
            SyncWidth(_panelW);

            int rowH = 24;
            // 名称一律单行显示、过长用中间省略号（两种布局都自绘），行高恒定，
            // 不再按换行后的行数计算行高
            var rowHeights = new List<int>(entries.Length);
            for (int i = 0; i < entries.Length; i++) rowHeights.Add(rowH);
            int ry = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                string name = entries[i];
                string full = Path.Combine(path, name);
                bool isDir = dirFlags[i];
                var row = new RowLabel
                {
                    Full = full,
                    Name = name,
                    IsDir = isDir,
                    // 名称一律 OnPaint 自绘：Label 文本会换行，自绘才能单行+中间省略
                    // （GDI+ 兼容模式下 MiddleRight 渲染不贴缘，短名称停在行中部）
                    Text = null,
                    Font = font,
                    TextAlign = Config.RowSwapSides ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
                    Padding = Config.RowSwapSides
                        ? new Padding(sizeTexts[i] == null ? 4 : (int)sizeWs[i] + 10, 0, 28, 0)
                        : new Padding(24, 0, sizeTexts[i] == null ? 4 : (int)sizeWs[i] + 10, 0),
                    AutoSize = false,
                    UseCompatibleTextRendering = true,
                    BackColor = Config.BG,
                    ForeColor = Config.FG,
                    Location = new Point(0, ry),
                    Size = new Size(_panelW - ListPadLr * 2, rowHeights[i]),
                    Icon = GetIcon(full, isDir),
                    SizeText = sizeTexts[i],
                    SplitX = _rowSideZone,
                    RowIndex = i,
                    SizeColW = maxSizeW,
                    SizeRatio = (!isDir && fileSizes[i] > 0 && maxFileSize > 1)
                        ? (float)Math.Max(0.06, Math.Min(1.0, Math.Log10(fileSizes[i]) / Math.Log10(maxFileSize)))
                        : 0f,
                };
                // 斑马底色（奇数行 ListBg / 偶数行 ListZb）；选中/悬停/拖拽时由对应逻辑覆盖
                row.BackColor = (i % 2 == 0) ? Config.ListBg : Config.ListZb;
                row.ForeColor = Config.ListFg;
                row.OrigBg = row.BackColor;
                row.OrigFg = row.ForeColor;
                row.MouseEnter += (s, e) => OnRowEnter(row);
                row.MouseLeave += (s, e) => OnRowLeave(row);
                row.MouseDown += (s, e) => OnRowDown(row, e);
                row.MouseMove += (s, e) => OnRowMove(row, e);
                row.MouseUp += (s, e) => OnRowUp(row, e);
                row.GiveFeedback += (s, e) =>
                {
                    // 拖拽指针反馈：不可放置=禁止圈（拖拽仍在进行，防止误以为已结束），
                    // Shift=移动(纯箭头)，默认=复制(箭头+加号)
                    e.UseDefaultCursors = false;
                    if (e.Effect == DragDropEffects.None) { Cursor.Current = DropNoneCursor; return; }
                    bool move = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                    Cursor.Current = move ? DropMoveCursor : DropCopyCursor;
                };
                row.MouseClick += (s, e) => { if (e.Button == MouseButtons.Right) PopupMenu(row, e); };
                if (isDir)
                {
                    row.AllowDrop = true;
                    row.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy; row.BackColor = Config.ListDrop; } };
                    row.DragOver += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy; ScheduleDragSub(row); } };
                    row.DragLeave += (s, e) => { CancelDragSub(row); ApplySel(row); };
                    row.DragDrop += (s, e) => { CancelDragSub(); OnDrop(row, e); };
                }
                ((SlimScrollPanel)_listPanel).AddContent(row);
                _rows.Add(row);
                ry += rowHeights[i];
            }
            if (entries.Length == 0)
            {
                var emptyLbl = new Label
                {
                    Text = I18n.T("empty"),
                    Font = font,
                    ForeColor = Config.PlaceholderFg,
                    BackColor = Config.BG,
                    AutoSize = true,
                    Location = new Point(8, 0),
                };
                ((SlimScrollPanel)_listPanel).AddContent(emptyLbl);
            }
            var workArea = Screen.GetWorkingArea(Cursor.Position);
            int maxH = workArea.Height - 40;
            int needH = curY + ry;
            // 高度两种模式：自动适配（默认）=随内容长高，条目多时显示更多；
            // 用户手动拖过尺寸后转纯记忆模式=严格按记忆高度（不再自动撑大）
            if (!Config.PanelSizeAuto && Config.PanelHeight >= 150)
                needH = Math.Min(needH, Math.Min(Config.PanelHeight, maxH));
            // 同理：固定面板沿用固定时的高度（内容变多时仍可更高，但不超过工作区）
            if (_pinned && _pinnedH > 0 && needH < _pinnedH) needH = _pinnedH;
            if (needH > maxH)
                needH = maxH;
            _listPanel.Size = new Size(_panelW - ListPadLr * 2, needH - curY);
            // 内容层高度 = 行总高（空目录给 emptyLbl 一行高），滚动范围由此确定
            int contentH = Math.Max(ry, 24);
            ((SlimScrollPanel)_listPanel).SetContentHeight(contentH);
            // 行右侧按“实际是否出现滚动条”让位（此前只在新建超高一条路径收缩：
            // 固定面板沿用记忆高度、内容后来变多等场景会漏——滚动条压住行尾“xx 项”的根因）
            if (((SlimScrollPanel)_listPanel).CanScroll)
                foreach (var r in _rows) r.Width = _panelW - ListPadLr * 2 - SlimBarW;
            _fnSyncListScroll();
            // 底部状态栏：占位防止 grip 挡住最后一行删除按钮，并显示限高/计数提示
            int statusH = 18;
            // 底部留白：状态栏下方再留一段，与顶部 curY=5 的上边距呼应
            ClientSize = new Size(_panelW, needH + statusH + ListPadBottom);
            string statusText = originalCount > 0 ? I18n.T("panel_status_count", originalCount) : "";
            var statusLbl = new Label
            {
                Location = new Point(6, needH),
                Size = new Size(_panelW - 12, statusH),
                Text = statusText,
                Font = new Font(Config.FontName, Math.Max(7f, Config.FontSize - 1)),
                ForeColor = Config.ListMu,
                BackColor = Config.ListHdbg,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            Controls.Add(statusLbl);
            // 状态栏全宽可拖动固定：宽度收窄时顶部可拖区域被按钮挤占，底部是稳定拖拽入口
            statusLbl.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var before = Location;
                NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                if (Location != before && !_pinned) TogglePinPopup();
            };

            ResumeLayout(false);
            PerformLayout();

            ApplySortBtns();

            ClampPos(ref x, ref y);
            Location = new Point(x, y);

            // 整面板按 level 着色：递归把子控件 Config.BG 背景换成 LevelBgColor
            {
                Color lbg = LevelBgColor(_level);
                Action<Control> tint = null;
                tint = (ctl) => { if ((ctl.Tag as string) != "noTint" && ctl.BackColor == Config.BG) ctl.BackColor = lbg; foreach (Control cc in ctl.Controls) tint(cc); };
                tint(this);
            }

            // 移除冻结快照，显示重建后的内容；右上按钮此时才 BringToFront——
            // 重建全程按钮藏在遮罩的冻结图像里（视觉连续），移除后新按钮立即置顶出现，不闪不隐藏
            if (freeze != null)
            {
                Controls.Remove(freeze);
                freeze.Dispose();
            }
            if (freezeImg != null) freezeImg.Dispose();
            if (_pinBtn != null) _pinBtn.BringToFront();
            if (_pinCardBtn != null) _pinCardBtn.BringToFront();

            // 空白处右键 = 选项菜单（排序功能已由顶部排序按钮覆盖）
            MouseClick += (s, e) => { if (e.Button == MouseButtons.Right && Program.TrayMenu != null) { _menuOpen = true; Program.TrayMenu.Show(Cursor.Position); } };


            // 不自动聚焦搜索框：后台进程在 Timer 里 Focus 只会得到"假焦点"
            // （光标闪但键盘消息仍归真正的前台窗口），需要点一下搜索框才能真正输入
            return true;
        }

        public void FocusSearch()
        {
            if (_searchBox != null) _searchBox.Focus();
        }

        private static string ShowName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string n = Path.GetFileName(path);
            return string.IsNullOrEmpty(n) ? path : n;
        }

        // Ctrl+F：展开/收起搜索框（默认隐藏，避免常驻输入框带来的假焦点与 IME 遮挡）
        public void ToggleSearch()
        {
            if (_searchBorder == null || _searchBox == null || _listPanel == null) return;
            bool on = !_searchBorder.Visible;
            _searchBorder.Visible = on;
            int dh = 24;
            _listPanel.Top += on ? dh : -dh;
            _listPanel.Height -= on ? dh : -dh;
            _headH += on ? dh : -dh;
            ClientSize = new Size(_panelW, ClientSize.Height + (on ? dh : -dh));
            if (_pinned) _pinnedH = ClientSize.Height;
            _fnSyncListScroll();
            int lx = Left, ly = Top; ClampPos(ref lx, ref ly); Location = new Point(lx, ly);
            if (on)
            {
                _searchBox.ForeColor = Config.ListFg;
                _searchBox.Text = "";
                // 面板是 ShowWithoutActivation 的后台窗体：不先 Activate，搜索框拿到的只是
                // "假焦点"（光标闪但键盘消息仍归真正的前台），鼠标一动假象即穿帮
                if (!Focused) Activate();
                _searchBox.Focus();
            }
            else
            {
                _searchBox.Text = I18n.T("search");
                _searchBox.ForeColor = Config.ListMu;
                FilterRows("");
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { ToggleSearch(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // 重命名文件/目录：非法字符与重名校验 + 固定项路径同步 + 相关面板刷新
        public static bool RenamePath(string src, string newName)
        {
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(newName)) return false;
            newName = newName.Trim();
            if (newName.Length == 0) return false;
            if (newName.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                Program.Log("[重命名失败] 名称不能包含 \\ / : * ? \" < > |");
                return false;
            }
            string dir = Path.GetDirectoryName(src);
            if (string.IsNullOrEmpty(dir)) return false;
            string dst = Path.Combine(dir, newName);
            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) return false;
            // 关键：文件正被预览时就占着句柄（图片 Image.FromFile 锁文件、MediaElement 播放中），
            // 不先释放 File.Move 必定抛“文件正由另一进程使用”
            ReleasePreviewOf(src);
            bool moved = false;
            try
            {
                if (Directory.Exists(src)) { if (Directory.Exists(dst)) return false; Directory.Move(src, dst); }
                else { if (File.Exists(dst)) return false; File.Move(src, dst); }
                moved = true;
            }
            catch (Exception ex) { Program.Log("[重命名失败] " + ex.Message); }
            // 成功按新路径恢复画面，失败也要把空掉的画面放回去
            RestorePreviewOf(src, moved ? dst : src);
            if (!moved) return false;
            // 固定项存的是路径，改名后要同步，否则固定卡片/面板指向失效路径
            var pi = Config.PinnedItems.Find(p => string.Equals(p.Path, src, StringComparison.OrdinalIgnoreCase));
            if (pi != null) { pi.Path = dst; Config.Save(); }
            Program.RefreshPanelsShowing(dir, null);
            Program.DebugLog("[rename] " + src + " -> " + dst);
            return true;
        }

        // 顶部名称就地改名：改的是面板自己的根目录，成功后要更新 _path / 标题 / 子面板路径
        private void BeginTitleRename()
        {
            if (_nameEdit == null || _titleLbl == null || string.IsNullOrEmpty(_path)) return;
            _nameEdit.Text = ShowName(_path);
            _nameEdit.Visible = true;
            _nameEdit.BringToFront();
            _titleLbl.Visible = false;
            if (_editBtn != null) _editBtn.Visible = false;
            // 同上：面板默认不抢前台，改名时要主动激活才能收到键盘
            if (!Focused) Activate();
            _nameEdit.Focus();
            _nameEdit.SelectAll();
        }

        private void EndTitleRename(bool commit)
        {
            if (_nameEdit == null || !_nameEdit.Visible) return;
            string nn = commit ? _nameEdit.Text.Trim() : "";
            if (commit && !string.IsNullOrEmpty(nn))
            {
                string old = _path;
                string target = Path.Combine(Path.GetDirectoryName(old), nn);
                if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase) && RenamePath(old, nn))
                {
                    _path = target;
                    Program.RebasePaths(old, target);
                }
            }
            _nameEdit.Visible = false;
            _titleLbl.Visible = true;
            if (_editBtn != null) _editBtn.Visible = true;
            _titleLbl.Text = ShowName(_path);
            Tips.Set(_titleLbl, _path + "\n" + I18n.T("copy_path"));
        }

        // 目录改名后同步本面板路径（子面板路径以旧目录为前缀，一并改写）
        internal void RebasePath(string oldPrefix, string newPrefix)
        {
            if (string.IsNullOrEmpty(_path)) return;
            if (string.Equals(_path, oldPrefix, StringComparison.OrdinalIgnoreCase)) _path = newPrefix;
            else if (_path.StartsWith(oldPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                _path = newPrefix + _path.Substring(oldPrefix.Length);
        }

        public static void RebaseFilePreviewPath(string oldPrefix, string newPrefix)
        {
            if (_extPreviewForm != null && !_extPreviewForm.IsDisposed) _extPreviewForm.RebaseFilePath(oldPrefix, newPrefix);
        }

        // 所有改名都走这里：先把占用该文件的预览释放掉，移动完再恢复画面。
        // 覆盖悬停预览（_extPreviewForm）与各面板内的预览。
        internal static void ReleasePreviewOf(string path)
        {
            if (_extPreviewForm != null) _extPreviewForm.ReleaseContentIf(path);
            foreach (var p in Program.Panels) p.ReleaseInnerPreviewIf(path);
        }

        internal static void RestorePreviewOf(string oldPath, string newPath)
        {
            if (_extPreviewForm != null) _extPreviewForm.RestoreContentTo(oldPath, newPath);
            foreach (var p in Program.Panels) p.RestoreInnerPreview(oldPath, newPath);
        }

        // 面板内预览正显示该文件时，改名要先释放它的内容
        internal void ReleaseInnerPreviewIf(string path)
        {
            if (_previewForm != null) _previewForm.ReleaseContentIf(path);
        }

        internal void RestoreInnerPreview(string oldPath, string newPath)
        {
            if (_previewForm != null) _previewForm.RestoreContentTo(oldPath, newPath);
        }

        private void SortEntries(string path, ref string[] entries)
        {
            string mode = SortMode;
            bool desc = SortDesc;
            int n = entries.Length;
            var isDir = new bool[n];
            var sizes = new long[n];
            var times = new long[n];
            for (int i = 0; i < n; i++)
            {
                string full = Path.Combine(path, entries[i]);
                try
                {
                    isDir[i] = Directory.Exists(full);
                    if (mode == "size" && !isDir[i] && File.Exists(full)) sizes[i] = new FileInfo(full).Length;
                    if (mode == "date") times[i] = File.GetLastWriteTimeUtc(full).Ticks;
                }
                catch { }
            }
            var indices = new int[n];
            for (int i = 0; i < n; i++) indices[i] = i;
            var names = entries;
            Array.Sort<int>(indices, (ia, ib) =>
            {
                if (isDir[ia] != isDir[ib]) return isDir[ia] ? -1 : 1;
                int cmp;
                if (mode == "size") cmp = sizes[ia].CompareTo(sizes[ib]);
                else if (mode == "date") cmp = times[ia].CompareTo(times[ib]);
                else cmp = string.Compare(names[ia], names[ib], StringComparison.OrdinalIgnoreCase);
                if (cmp == 0) cmp = string.Compare(names[ia], names[ib], StringComparison.OrdinalIgnoreCase);
                return desc ? -cmp : cmp;
            });
            var sorted = new string[n];
            for (int i = 0; i < n; i++) sorted[i] = names[indices[i]];
            entries = sorted;
        }

        private void ApplySortBtns()
        {
            foreach (var kv in _sortBtns)
            {
                bool on = kv.Key == SortMode;
                kv.Value.Text = Config.SortLabels[kv.Key];
                kv.Value.SortArrow = on ? (SortDesc ? 2 : 1) : 0;
                kv.Value.BackColor = on ? Config.ListAc : Config.ListHdbg;
                kv.Value.ForeColor = on ? Config.ListAcFg : Config.ListFg;
                kv.Value.Invalidate();
            }
        }

        private void SetSort(string mode)
        {
            if (SortMode == mode) SortDesc = !SortDesc;
            else SortMode = mode;
            Config.Save();   // 排列方式持久化：重启后沿用
            ApplySortBtns();
            if (OnSortChange != null) OnSortChange();
        }

        private void ToggleSortDesc()
        {
            SortDesc = !SortDesc;
            Config.Save();
            ApplySortBtns();
            if (OnSortChange != null) OnSortChange();
        }

        // 搜索框展开 = 键盘输入态：行悬停调度（子列表/预览/行按钮）一并抑制
        private bool SearchActive
        {
            get { return _searchBorder != null && _searchBorder.Visible; }
        }

        private void FilterRows(string q)
        {
            string raw = q;
            // 必须在 ToLowerInvariant 之前与占位符比较：
            // en/fr/es/ru 等语言占位符首字母大写（Search/Rechercher/Buscar/Поиск），
            // 小写化后再比较会失配，导致占位文本被当成搜索词、整表被过滤空
            q = q.Trim();
            if (q == I18n.T("search")) q = "";
            q = q.ToLowerInvariant();
            bool filtering = !string.IsNullOrEmpty(q);
            int hidden = 0;
            int visY = 0;                 // 收拢游标：可见行紧贴上移，不留隐藏行空洞
            RowLabel firstVis = null;
            foreach (var row in _rows)
            {
                bool vis = !filtering || row.Name.ToLowerInvariant().Contains(q);
                row.Visible = vis;
                if (!vis) hidden++;
                if (filtering)
                {
                    if (!row.OriginalTopSet) { row.OriginalTop = row.Top; row.OriginalTopSet = true; }   // 首次过滤记原位
                    if (vis)
                    {
                        row.Top = visY;
                        visY += row.Height;
                        if (firstVis == null) firstVis = row;
                    }
                }
                else if (row.OriginalTopSet)
                {
                    row.Top = row.OriginalTop;   // 清空搜索：还原原位
                    row.OriginalTopSet = false;
                }
            }
            // 过滤后内容高度 = 可见行总高（滚动范围跟随，避免底部大片可滚空白）
            if (_listPanel != null)
            {
                int visBottom = 0;
                foreach (var row in _rows)
                    if (row.Visible) visBottom = Math.Max(visBottom, row.Bottom);
                ((SlimScrollPanel)_listPanel).SetContentHeight(Math.Max(visBottom, 24));
            }
            // 文件多时滚动停在原深度会看不到匹配行：滚到首个匹配行
            if (firstVis != null && _listPanel != null)
            {
                ((SlimScrollPanel)_listPanel).SetOffset(firstVis.Top);
            }
            _fnSyncListScroll();
            Program.DebugLog("[filter] level=" + _level + " lang=" + I18n.Lang + " raw=\"" + raw + "\" placeholder=\"" + I18n.T("search") + "\" rows=" + _rows.Count + " hidden=" + hidden);
        }

        private void ClampPos(ref int x, ref int y)
        {
            NativeMethods.POINT pt;
            NativeMethods.GetCursorPos(out pt);
            var hmon = NativeMethods.MonitorFromPoint(pt, 2);
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
            NativeMethods.GetMonitorInfo(hmon, ref mi);
            int wl = mi.rcWork.Left, wt = mi.rcWork.Top, wr = mi.rcWork.Right, wb = mi.rcWork.Bottom;
            if (x + Width > wr) x = wr - Width - 4;
            if (y + Height > wb) y = wb - Height - 4;
            if (x < wl) x = wl + 4;
            if (y < wt) y = wt + 4;
        }

        private void OnRowEnter(RowLabel row)
        {
            // 拖分栏线中（Capture 丢消息时的兜底）：行视觉冻结，避免高亮/按钮随拖动跳变
            if (_splitDrag) return;
            // 行内重命名进行中：进入其他行不触发悬停/高亮/按钮，避免预览弹出与视觉跳动
            if (_rowEdit != null)
            {
                if (_rowDelBtn != null) _rowDelBtn.Visible = false;
                if (_rowCopyBtn != null) _rowCopyBtn.Visible = false;
                if (_rowPinBtn != null) _rowPinBtn.Visible = false;
                if (_rowRenameBtn != null) _rowRenameBtn.Visible = false;
                _fnClearRowIndex();
                return;
            }
            // 搜索框展开 = 键盘输入态：扫行不弹子列表/预览（会抢走输入流），点击照常
            if (SearchActive)
            {
                if (_rowDelBtn != null) _rowDelBtn.Visible = false;
                if (_rowCopyBtn != null) _rowCopyBtn.Visible = false;
                if (_rowPinBtn != null) _rowPinBtn.Visible = false;
                if (_rowRenameBtn != null) _rowRenameBtn.Visible = false;
                _fnClearRowIndex();
                return;
            }
            // 序号叠加：与行内操作按钮同生共死——只显示当前悬停行的行号，先清掉残留行
            _fnClearRowIndex();
            row.ShowIndex = true;
            row.Invalidate();
            row.BackColor = Config.ListHv;
            // 按钮组紧贴排列（18px 步进无间距）：重命名·复制·钉卡片·删除；
            // 互换布局时整组贴左缘（del·pin·copy·rename 从左往右），与大小文本同侧。
            // 行区已整体内缩 ListPadLr，按钮贴行缘即距面板边 6px，与顶部内容对齐
            // 按钮高度与行同高：纵向铺满整行，图标靠 ImageAlign=MiddleCenter 垂直居中
            int btnY = row.Top;
            foreach (var b in new[] { _rowDelBtn, _rowCopyBtn, _rowPinBtn, _rowRenameBtn })
                if (b != null && b.Height != row.Height) b.Height = row.Height;
            bool sw = Config.RowSwapSides;
            if (_rowDelBtn != null)
            {
                _rowDelBtn.Tag = row.Full;
                _rowDelBtn.Location = new Point(sw ? 0 : row.Width - 18, btnY);
                _rowDelBtn.Visible = true;
                _rowDelBtn.BringToFront();
            }
            if (_rowCopyBtn != null)
            {
                _rowCopyBtn.Tag = row.Full;
                _rowCopyBtn.Location = new Point(sw ? 36 : row.Width - 54, btnY);
                _rowCopyBtn.Visible = true;
                _rowCopyBtn.BringToFront();
            }
            if (_rowRenameBtn != null && _rowEdit == null)
            {
                _rowRenameBtn.Tag = row;
                _rowRenameBtn.Location = new Point(sw ? 54 : row.Width - 72, btnY);
                _rowRenameBtn.Visible = true;
                _rowRenameBtn.BringToFront();
            }
            if (_rowPinBtn != null)
            {
                _rowPinBtn.Tag = row.Full;
                _rowPinBtn.Location = new Point(sw ? 18 : row.Width - 36, btnY);
                _rowPinBtn.Visible = true;
                _rowPinBtn.BringToFront();
            }
            NativeMethods.POINT _ept; NativeMethods.GetCursorPos(out _ept);
            bool canPv = !row.IsDir && Config.PreviewFiles && PreviewProvider.CanPreview(row.Full);
            Program.DebugLog("[row] enter level=" + _level + " lang=" + I18n.Lang + " " + (row.IsDir ? "DIR " : "FILE ") + row.Name + " mouse=" + _ept.X + "," + _ept.Y + " previewFiles=" + Config.PreviewFiles + " canPreview=" + canPv);
            // 互换布局：进入点在左侧安全区（大小/按钮列）→ 不调度悬停子面板与预览。
            // 后续移入名称区由 OnRowMove 补调度（_wasInSide 跟踪区域切换）
            bool enterInSide = Config.RowSwapSides && row.PointToClient(Cursor.Position).X < _rowSideZone;
            if (Config.RowSwapSides) _wasInSide = enterInSide;
            // 悬停抑制：面板内部只吃 Alt 临时禁用——门控只拦第一级，一级面板已过闸，行悬停免键
            if (enterInSide || Config.HoverSuppressedInside(!row.IsDir)) return;
            if (row.IsDir)
            {
                // 目录行：旧的文件预览先进入关闭调度（延迟里再判鼠标落点，鼠标移进预览则保持）。
                // 否则从文件行滑到目录行时，若 OnRowLeave 走了早退分支，预览会一直挂着不关
                SchedulePreviewClose();
                ScheduleHover(row.Full, row.PointToScreen(Point.Empty).Y);
            }
            else if (canPv)
            {
                // 文件行：旧的级联子列表先进入关闭调度（150ms 后按鼠标落点决定关/留）。
                // 否则从目录行滑到文件行时，已打开的子列表会一直挂着不关
                if (_onScheduleCloseAfter != null) _onScheduleCloseAfter(_level);
                SchedulePreview(row);
            }
        }

        private void OnRowLeave(RowLabel row)
        {
            // 拖分栏线中：按钮组与高亮保持现状（Capture 兜底路径）
            if (_splitDrag) return;
            // 按钮组同生共死：光标落在组内任一按钮上 → 整组保留（旧逻辑逐个判定，
            // 移到某个按钮上时其余三个因"光标不在"被隐藏）；不在任何按钮上 → 整组隐藏
            bool onBtns = false;
            NativeMethods.POINT _bpt; NativeMethods.GetCursorPos(out _bpt);
            foreach (var b in new Label[] { _rowDelBtn, _rowCopyBtn, _rowPinBtn, _rowRenameBtn })
            {
                if (b == null || !b.Visible) continue;
                var r = b.RectangleToScreen(b.ClientRectangle);
                if (_bpt.X >= r.Left && _bpt.X <= r.Right && _bpt.Y >= r.Top && _bpt.Y <= r.Bottom) { onBtns = true; break; }
            }
            if (!onBtns)
            {
                if (_rowDelBtn != null) _rowDelBtn.Visible = false;
                if (_rowCopyBtn != null) _rowCopyBtn.Visible = false;
                if (_rowPinBtn != null) _rowPinBtn.Visible = false;
                if (_rowRenameBtn != null) _rowRenameBtn.Visible = false;
                // 序号与按钮组同生共死：整组隐藏时序号一并消失（移到按钮组上则保留）
                if (row.ShowIndex) { row.ShowIndex = false; row.Invalidate(); }
            }
            if (row == _activeDirRow) row.BackColor = Config.ListHv;
            else if (row == _contextRow) row.BackColor = Config.ListHv;  // 右键期间保持悬停高亮不被 OnRowLeave 擦除
            else row.BackColor = row.Selected ? Config.ListSel : (row.RowIndex % 2 == 0 ? Config.ListBg : Config.ListZb);
            NativeMethods.POINT _lpt; NativeMethods.GetCursorPos(out _lpt);
            Program.DebugLog("[row] leave level=" + _level + " lang=" + I18n.Lang + " " + (row.IsDir ? "DIR " : "FILE ") + row.Name + " mouse=" + _lpt.X + "," + _lpt.Y + " onBtns=" + onBtns);
            if (onBtns) return;
            if (row == _contextRow) return;  // 右键模态期间不取消悬停状态、不关子面板/预览
            if (row.IsDir)
            {
                CancelHover();
                // 级联关闭调度（150ms 延迟里再判鼠标落点：切换=关，真离开=KeepOnLeave 保持）
                if (_onScheduleCloseAfter != null) _onScheduleCloseAfter(_level);
                if (row != _activeDirRow) ApplySel(row);
            }
            else
            {
                ApplySel(row);
            }
            SchedulePreviewClose();
        }

        public void SetActiveDir(string path)
        {
            if (_activeDirRow != null && _activeDirRow.Full != path) ApplySel(_activeDirRow);
            _activeDirRow = _rows.FirstOrDefault(r => r.Full == path);
            if (_activeDirRow != null) _activeDirRow.BackColor = Config.ListHv;
        }

        public void ClearActiveDir()
        {
            if (_activeDirRow != null) { ApplySel(_activeDirRow); _activeDirRow = null; }
        }

        // 滚轮切换图片时高亮列表中当前预览的文件
        public void HighlightPath(string path)
        {
            if (_hlRow != null && _hlRow.Full != path) { ApplySel(_hlRow); _hlRow = null; }
            if (string.IsNullOrEmpty(path)) return;
            var row = _rows.FirstOrDefault(r => string.Equals(r.Full, path, StringComparison.OrdinalIgnoreCase));
            if (row == null || row == _hlRow) return;
            row.BackColor = Config.ListHv;
            _hlRow = row;
        }

        private void ApplySel(RowLabel row)
        {
            if (row.Selected) { row.BackColor = Config.ListSel; row.ForeColor = Config.ListSelFg; }
            else { row.BackColor = (row.RowIndex % 2 == 0) ? Config.ListBg : Config.ListZb; row.ForeColor = Config.ListFg; }
        }

        private int RowIndex(RowLabel row) { return _rows.IndexOf(row); }

        private void ToggleSel(RowLabel row)
        {
            row.Selected = !row.Selected;
            ApplySel(row);
            _lastClickIdx = RowIndex(row);
        }

        private void RangeSel(RowLabel row)
        {
            int cur = RowIndex(row);
            if (cur < 0) return;
            int start = _lastClickIdx.HasValue ? _lastClickIdx.Value : 0;
            int lo = Math.Min(start, cur), hi = Math.Max(start, cur);
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].Selected = lo <= i && i <= hi;
                ApplySel(_rows[i]);
            }
            _lastClickIdx = cur;
        }

        private void ClearSel()
        {
            foreach (var r in _rows) if (r.Selected) { r.Selected = false; ApplySel(r); }
        }

        private List<RowLabel> SelRows() { return _rows.Where(r => r.Selected).ToList(); }

        private void ScheduleHover(string path, int rowScreenY)
        {
            CancelHover();
            _hoverPath = path;
            _hoverRowY = rowScreenY;
            _hoverTimer = new Timer { Interval = Config.SubHoverDelay };
            _hoverTimer.Tick += (s, e) => { _hoverTimer.Stop(); _hoverTimer.Dispose(); _hoverTimer = null; if (Config.HoverSuppressedInside(false)) { Program.DebugLog("[panel] 悬停计时触发但悬停被抑制（Alt 按住），取消 level=" + _level); return; } Program.DebugLog("[panel] 悬停计时触发 -> 请求打开子面板 level=" + _level + " path=" + _hoverPath); if (_hoverPath != null && _onOpenSub != null) _onOpenSub(this, _level, _hoverPath, _hoverRowY); };
            _hoverTimer.Start();
        }

        private void CancelHover()
        {
            if (_hoverTimer != null) { _hoverTimer.Stop(); _hoverTimer.Dispose(); _hoverTimer = null; }
            _hoverPath = null;
        }

        // 拖拽悬停文件夹（弹簧式文件夹）：OLE 拖拽期间 MouseEnter/MouseMove 不触发，
        // 常规行悬停调度失效，由 DragOver 自行调度——拖停 ~500ms 后打开该文件夹的级联
        // 子列表（列出其内容=子文件夹也在列表中），子列表与其中的文件夹行同样允许放置，
        // 可连续导航到更深层再放置。面板保持依赖 MaybeClose 的 IsDragging 豁免
        private void ScheduleDragSub(RowLabel row)
        {
            if (_dragSubRow == row && _dragSubTimer != null) return;   // 该行已在计时
            CancelDragSub();
            _dragSubRow = row;
            _dragSubTimer = new Timer { Interval = 500 };
            _dragSubTimer.Tick += (s, e) =>
            {
                var t = _dragSubTimer; t.Stop(); t.Dispose(); _dragSubTimer = null;
                var r = _dragSubRow; _dragSubRow = null;
                if (r == null || r.IsDisposed || !r.IsDir || IsDisposed) return;
                Program.DebugLog("[panel] drag-hover cascade level=" + _level + " " + r.Full);
                if (_onOpenSub != null) _onOpenSub(this, _level, r.Full, r.PointToScreen(Point.Empty).Y);
            };
            _dragSubTimer.Start();
        }

        private void CancelDragSub(RowLabel row = null)
        {
            if (row != null && _dragSubRow != row) return;   // 已移到另一行：保留那行的调度
            if (_dragSubTimer != null) { _dragSubTimer.Stop(); _dragSubTimer.Dispose(); _dragSubTimer = null; }
            _dragSubRow = null;
        }


        private void SchedulePreview(RowLabel row)
        {
            if (_previewForm != null && _previewPath == row.Full) { Program.DebugLog("[panel-preview] keep-existing level=" + _level + " " + row.Full); return; }
            Program.DebugLog("[panel-preview] schedule level=" + _level + " lang=" + I18n.Lang + " " + row.Full + " delay=" + Config.HoverDelayMs + " (hadTimer=" + (_previewTimer != null) + " hadForm=" + (_previewForm != null) + ")");
            CancelPreview();
            // 切到另一个文件：立即关掉旧预览（含停止其音频）。否则旧音频会拖到新预览
            // 异步生成完（解码波形等耗时）才在 ShowPreview 里被关，出现“新卡片已出、旧还在响”
            if (_previewForm != null && !_previewForm.Pinned) ClosePreviewForm();
            _previewPath = row.Full;
            // 音频预览出声要快：缩短触发延迟（图片等较重预览仍用常规防误触延迟），
            // 让 MediaElement 更早开始打开文件；剩余延迟来自 WPF 打开大/无索引 MP3 本身
            int previewDelay = Config.HoverDelayMs;
            foreach (var e in Config.AudioExts)
                if (row.Full.EndsWith(e, StringComparison.OrdinalIgnoreCase)) { previewDelay = Math.Min(previewDelay, 100); break; }
            _previewTimer = new Timer { Interval = previewDelay };
            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); _previewTimer.Dispose(); _previewTimer = null; if (Config.HoverSuppressedInside(true)) { Program.DebugLog("[panel-preview] timer-fired 但悬停被抑制（Alt 按住），取消 level=" + _level + " " + row.Full); return; } Program.DebugLog("[panel-preview] timer-fired level=" + _level + " " + row.Full); ShowPreview(row); };
            _previewTimer.Start();
        }

        private void CancelPreview()
        {
            if (_previewTimer != null || _previewForm != null) Program.DebugLog("[panel-preview] cancel level=" + _level + " hadTimer=" + (_previewTimer != null) + " hadForm=" + (_previewForm != null));
            if (_previewTimer != null) { _previewTimer.Stop(); _previewTimer.Dispose(); _previewTimer = null; }
            _previewPath = null;
            CancelPreviewClose();
            ClosePreviewForm();
        }

        public void ClosePreviewIfAny() { CancelPreview(); }

        private void SchedulePreviewClose()
        {
            if (_previewForm == null)
            {
                Program.DebugLog("[panel-preview] schedule-close level=" + _level + " no form -> cleanup timer");
                if (_previewTimer != null) { _previewTimer.Stop(); _previewTimer.Dispose(); _previewTimer = null; }
                _previewPath = null;
                return;
            }
            // 关闭调度照常启动；KeepOnLeave 的保持判断在 close-check tick 里按鼠标落点做
            Program.DebugLog("[panel-preview] schedule-close level=" + _level + " form at " + _previewForm.Left + "," + _previewForm.Top + " " + _previewForm.Width + "x" + _previewForm.Height + " pinned=" + _previewForm.Pinned);
            CancelPreviewClose();
            _previewCloseTimer = new Timer { Interval = Math.Max(60, Config.CloseDelayMs) };
            _previewCloseTimer.Tick += (s, e) =>
            {
                _previewCloseTimer.Stop(); _previewCloseTimer.Dispose(); _previewCloseTimer = null;
                NativeMethods.POINT pt;
                NativeMethods.GetCursorPos(out pt);
                bool inPv = _previewForm != null && _previewForm.ContainsPoint(pt.X, pt.Y);
                bool pinned = _previewForm != null && _previewForm.Pinned;
                // 互换布局：鼠标在本面板左侧按钮列（安全区）驻留/横穿时视同仍在面板——
                // 预览保持开启并继续监控，移出面板后走正常关闭
                bool inSideHold = false;
                if (!inPv && _previewForm != null && Config.RowSwapSides && !pinned)
                {
                    var ps = PointToScreen(Point.Empty);
                    inSideHold = pt.X >= ps.X && pt.X < ps.X + _rowSideZone && pt.Y >= ps.Y && pt.Y < ps.Y + Height;
                }
                Program.DebugLog("[panel-preview] close-check level=" + _level + " mouse=" + pt.X + "," + pt.Y + " inPreview=" + inPv + " inSideHold=" + inSideHold + " pinned=" + pinned + " formAlive=" + (_previewForm != null));
                if (pinned) return;
                if (_previewForm != null && (inPv || inSideHold))
                    SchedulePreviewClose();
                else if (Config.KeepOnLeave && !InAnyHoverPanel(pt.X, pt.Y))
                {
                    // 鼠标已离开所有面板 = 离开语义 → 预览保持，等点击其他位置；
                    // 仍在别的面板上（换行切换）→ 走下面的正常关闭
                    Program.DebugLog("[panel-preview] KeepOnLeave hold level=" + _level);
                    return;
                }
                else
                    ClosePreviewForm();
            };
            _previewCloseTimer.Start();
        }

        private void CancelPreviewClose()
        {
            if (_previewCloseTimer != null) { Program.DebugLog("[panel-preview] cancel-close level=" + _level); _previewCloseTimer.Stop(); _previewCloseTimer.Dispose(); _previewCloseTimer = null; }
        }

        private void ClosePreviewForm()
        {
            if (_previewForm == null) return;
            if (_previewForm.Pinned) { Program.DebugLog("[panel-preview] CLOSE detach (pinned) level=" + _level); _previewForm = null; return; }
            Program.DebugLog("[panel-preview] CLOSE level=" + _level + " form was at " + _previewForm.Left + "," + _previewForm.Top + " " + _previewForm.Width + "x" + _previewForm.Height); try { _previewForm.Close(); _previewForm.Dispose(); } catch { } _previewForm = null;
        }

        private void ShowPreview(RowLabel row)
        {
            string full = _previewPath;
            if (full == null || full != row.Full) { Program.DebugLog("[panel-preview] show-abort level=" + _level + " full=" + full + " row.Full=" + row.Full + " (path mismatch)"); return; }
            ShowPreviewCore(full, row, null);
        }

        // 导航切换后保持原预览窗位置重建（keepBounds 非空时不用行锚定）
        private void ShowPreviewCore(string full, RowLabel anchorRow, Rectangle? keepBounds)
        {
            Program.DebugLog("[panel-preview] 开始生成 level=" + _level + " lang=" + I18n.Lang + " " + full);
            Task.Run(() =>
            {
                try
                {
                    var result = PreviewProvider.Generate(full);
                    if (result == null) { Program.DebugLog("[panel-preview] generate returned null level=" + _level + " " + full); return; }
                    BeginInvoke((MethodInvoker)delegate()
                    {
                        if (full != _previewPath) { Program.DebugLog("[panel-preview] show-stale level=" + _level + " full=" + full + " curPath=" + _previewPath + " (discarded)"); if (result.Image != null) result.Image.Dispose(); return; }
                        ClosePreviewForm();
                        if (result.Kind == PreviewResult.KindEnum.Image)
                        {
                            if (result.IsGif)
                                _previewForm = new PreviewForm(result.Image, result.DisplayW, result.DisplayH);
                            else
                                _previewForm = new PreviewForm(result.Image);
                        }
                        else if (result.Kind == PreviewResult.KindEnum.Media)
                        {
                            var ctl = MediaPreview.MediaPlayer(full, result.IsVideo, result.DisplayW, result.DisplayH);
                            _previewForm = new PreviewForm(ctl, ctl.Width, ctl.Height, result.IsVideo ? "video" : "audio");
                        }
                        else if (result.Kind == PreviewResult.KindEnum.SvgHtml)
                        {
                            var ctl = MediaPreview.SvgBrowserPanel(full);
                            _previewForm = new PreviewForm(ctl, ctl.Width, ctl.Height, "svg");
                        }
                        else
                        {
                            _previewForm = new PreviewForm(result.Text, result.Size);
                        }
                        if (keepBounds.HasValue) _previewForm.Bounds = keepBounds.Value;
                        else PositionPreview(anchorRow);
                        _previewForm.FilePath = full;
                        ApplyHeader(_previewForm, full);
                        _previewForm.OnFileSwitched = p => HighlightPath(p);

                        _previewForm.Show();
                        // 同上：面板内预览也要在 Show 后立即置顶
                        NativeMethods.SetWindowPos(_previewForm.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
                        NativeMethods.POINT _spt; NativeMethods.GetCursorPos(out _spt);
                        Program.DebugLog("[panel-preview] SHOW level=" + _level + " " + full + " at " + _previewForm.Left + "," + _previewForm.Top + " size " + _previewForm.Width + "x" + _previewForm.Height + " mouse=" + _spt.X + "," + _spt.Y);

                    });
                }
                catch (Exception ex) { Program.DebugLog("[panel-preview] generate-exception level=" + _level + " " + full + " " + ex.Message); }
            });
        }


        private void BindPreviewEvents()
        {

        }

        public bool PreviewContainsPoint(int x, int y)
        {
            return _previewForm != null && _previewForm.ContainsPoint(x, y);
        }

        // 本面板是否有内嵌预览在显示
        internal bool HasInnerPreview()
        {
            return _previewForm != null && !_previewForm.IsDisposed;
        }

        // 鼠标是否在任一悬停面板（本体）上——不含预览，切换语义判定用
        internal static bool InAnyHoverPanel(int x, int y)
        {
            foreach (var p in Program.Panels)
            {
                if (p == null || p.IsDisposed) continue;
                try { if (p.ContainsPoint(x, y)) return true; } catch { }
            }
            return false;
        }

        // 鼠标在任一面板本体或其内嵌预览上——卡片/入口列表的关闭轮询用：
        // 鼠标停在面板弹出的预览窗口上不能判成"已离开"，否则未固定的
        // 级联连同预览被误关（固定面板不在 CloseAll 范围所以不受影响）
        internal static bool InAnyHoverPanelOrPreview(int x, int y)
        {
            foreach (var p in Program.Panels)
            {
                if (p == null || p.IsDisposed) continue;
                try { if (p.ContainsPoint(x, y) || p.PreviewContainsPoint(x, y)) return true; } catch { }
            }
            return false;
        }

        // ===== 悬停文件直接预览（桌面/资源管理器，无面板打开时） =====
        private static Timer _extPreviewTimer;
        private static string _extPreviewPath;
        private static PreviewForm _extPreviewForm;
        private static Timer _extPreviewCloseTimer;
        private static int _extRetryCount;

        public static void ScheduleFilePreview(string full, Point mouse)
        {

            CancelFilePreview();
            if (!Config.PreviewFiles) return;
            if (!PreviewProvider.CanPreview(full)) return;
            _extRetryCount = 0;
            _extPreviewPath = full;
            Program.DebugLog("[preview] schedule " + full);
            _extPreviewTimer = new Timer { Interval = Config.HoverDelayMs };
            _extPreviewTimer.Tick += delegate(object s, EventArgs e)
            {
                ((Timer)s).Stop(); ((Timer)s).Dispose(); _extPreviewTimer = null;
                LoadFilePreview(_extPreviewPath, mouse);
            };
            _extPreviewTimer.Start();
        }

        public static void CancelFilePreview()
        {
            if (_extPreviewTimer != null) { _extPreviewTimer.Stop(); _extPreviewTimer.Dispose(); _extPreviewTimer = null; }
            _extPreviewPath = null;
            if (_extPreviewCloseTimer != null) { _extPreviewCloseTimer.Stop(); _extPreviewCloseTimer.Dispose(); _extPreviewCloseTimer = null; }
            if (_extPreviewForm != null && !_extPreviewForm.Pinned) { Program.DebugLog("[preview] cancel-existing-form"); CloseExtPreviewForm(); }
        }

        public static bool FilePreviewContainsPoint(int x, int y)
        {
            return _extPreviewForm != null && _extPreviewForm.ContainsPoint(x, y);
        }

        // 预览显示期间保持 TOPMOST（不激活）：防止被前台窗口盖住。
        // 仅在被外部进程窗口压住时才提顶，避免与其他叠加窗口每 Tick 互相翻转 z 序（闪烁）
        public static void EnsureExtPreviewTopmost()
        {
            if (_extPreviewForm == null || !_extPreviewForm.IsHandleCreated || !_extPreviewForm.Visible) return;
            EnsureWinTopmost(_extPreviewForm.Handle, "extpreview[" + _extPreviewForm.MediaKind + "] " + _extPreviewForm.FilePath);
        }

        public static void ScheduleExtPreviewClose(int mx, int my)
        {
            if (_extPreviewForm == null) { Program.DebugLog("[preview] schedule-close but no form"); CancelFilePreview(); return; }
            if (_extPreviewCloseTimer != null) return;
            Program.DebugLog("[preview] schedule-close");
            _extPreviewCloseTimer = new Timer { Interval = Math.Max(60, Config.CloseDelayMs) };
            _extPreviewCloseTimer.Tick += delegate(object s, EventArgs e)
            {
                ((Timer)s).Stop(); ((Timer)s).Dispose(); _extPreviewCloseTimer = null;
                NativeMethods.POINT pt;
                NativeMethods.GetCursorPos(out pt);
                if (_extPreviewForm != null && !_extPreviewForm.Pinned && _extPreviewForm.ContainsPoint(pt.X, pt.Y))
                    ScheduleExtPreviewClose(pt.X, pt.Y);
                else if (_extPreviewForm != null && !_extPreviewForm.Pinned) CloseExtPreviewForm();
            };
            _extPreviewCloseTimer.Start();
        }

        private static void CloseExtPreviewForm()
        {
            if (_extPreviewForm == null) return;
            if (_extPreviewForm.Pinned) { Program.DebugLog("[preview] CLOSE detach (pinned)"); _extPreviewForm = null; return; }
            Program.DebugLog("[preview] CLOSE form"); try { _extPreviewForm.Close(); _extPreviewForm.Dispose(); } catch { } _extPreviewForm = null;
        }


        internal static void ApplyHeader(PreviewForm form, string full)
        {
            string name = Path.GetFileName(full);
            string ext = Path.GetExtension(full).TrimStart('.').ToUpperInvariant();
            long size = 0;
            try { size = new FileInfo(full).Length; } catch { }
            string meta = ext + " · " + FormatSize(size);
            Color iconColor; string iconText;
            // lnk 头部显示指向目标：一眼看出快捷方式指向什么
            if (string.Equals(Path.GetExtension(full), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string t = PreviewProvider.GetLnkTarget(full);
                if (!string.IsNullOrEmpty(t))
                    meta = "LNK → " + (File.Exists(t) || Directory.Exists(t) ? Path.GetFileName(t) : t);
                else
                    meta = "LNK · " + FormatSize(size);
                iconColor = ColorTranslator.FromHtml("#14b8a6");
                iconText = "↗";
                form.SetHeader(name, meta, iconText, iconColor);
                return;
            }
            switch (ext)
            {
                case "MP3": case "WAV": case "WMA": case "M4A": iconColor = ColorTranslator.FromHtml("#e5567a"); iconText = "♪"; break;
                case "MP4": case "MKV": case "AVI": case "MOV": case "WMV": iconColor = ColorTranslator.FromHtml("#9b5de5"); iconText = "▶"; break;
                case "SVG": iconColor = ColorTranslator.FromHtml("#3b8eea"); iconText = "S"; break;
                case "PNG": case "JPG": case "JPEG": case "GIF": case "BMP": iconColor = ColorTranslator.FromHtml("#3b8eea"); iconText = "▣"; break;
                case "ZIP": case "RAR": case "7Z": iconColor = ColorTranslator.FromHtml("#c98a2b"); iconText = "▤"; break;
                case "JSON": iconColor = ColorTranslator.FromHtml("#d9a21b"); iconText = "{}"; break;
                case "CSS": iconColor = ColorTranslator.FromHtml("#2fa66a"); iconText = "#"; break;
                case "MD": iconColor = ColorTranslator.FromHtml("#4a6cf7"); iconText = "M"; break;
                default: iconColor = Config.PlaceholderFg; iconText = ext.Length > 3 ? ext.Substring(0, 3) : ext; break;
            }
            form.SetHeader(name, meta, iconText, iconColor);
        }

        private static void LoadFilePreview(string full, Point mouse)
        {
            if (full == null || _extPreviewPath != full) return;
            Program.DebugLog("[preview] timer-fired, generate start " + full);
            var ctx = System.Threading.SynchronizationContext.Current;
            Task.Run(delegate
            {
                try
                {
                    var result = PreviewProvider.Generate(full);
                    if (result == null) return;
                    ctx.Post(delegate
                    {
                        if (full != _extPreviewPath) { if (result.Image != null) result.Image.Dispose(); return; }
                        CloseExtPreviewForm();
                        if (result.Kind == PreviewResult.KindEnum.Media)
                        {
                            var ctl = MediaPreview.MediaPlayer(full, result.IsVideo, result.DisplayW, result.DisplayH);
                            _extPreviewForm = new PreviewForm(ctl, ctl.Width, ctl.Height, result.IsVideo ? "video" : "audio");
                        }
                        else if (result.Kind == PreviewResult.KindEnum.SvgHtml)
                        {
                            var ctl = MediaPreview.SvgBrowserPanel(full);
                            _extPreviewForm = new PreviewForm(ctl, ctl.Width, ctl.Height, "svg");
                        }
                        else if (result.Kind == PreviewResult.KindEnum.Image)
                        {
                            if (result.IsGif)
                                _extPreviewForm = new PreviewForm(result.Image, result.DisplayW, result.DisplayH);
                            else
                                _extPreviewForm = new PreviewForm(result.Image);
                        }
                        else
                        {
                            _extPreviewForm = new PreviewForm(result.Text, result.Size);
                        }
                        PositionExtPreview(mouse);
                        _extPreviewForm.FilePath = full;
                        ApplyHeader(_extPreviewForm, full);

                        _extPreviewForm.Show();
                        // 刚创建的窗口最容易被刚激活的资源管理器压住，Show 后立即补一次置顶
                        NativeMethods.SetWindowPos(_extPreviewForm.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
                        Program.DebugLog("[preview] SHOW " + full + " at " + _extPreviewForm.Left + "," + _extPreviewForm.Top + " size " + _extPreviewForm.Width + "x" + _extPreviewForm.Height);
                    }, null);
                }
                catch
                {
                    // 失败自动重试（上限 3 次），避免一次性 IO 冲突导致预览永久消失
                    ctx.Post(delegate
                    {
                        if (_extPreviewPath == null || _extRetryCount >= 3 || !File.Exists(full)) return;
                        _extRetryCount++;
                        Program.DebugLog("[preview] generate-failed retry " + _extRetryCount);
                        var rt = new Timer { Interval = 400 };
                        rt.Tick += delegate(object s2, EventArgs e2)
                        {
                            ((Timer)s2).Stop(); ((Timer)s2).Dispose();
                            if (_extPreviewPath != null) LoadFilePreview(_extPreviewPath, mouse);
                        };
                        rt.Start();
                    }, null);
                }
            });
        }


        private static void PositionExtPreview(Point mouse)
        {
            if (_extPreviewForm == null) return;
            int x = mouse.X + 20, y = mouse.Y + 2;
            int pw = _extPreviewForm.Width, ph = _extPreviewForm.Height;
            var hpt = new NativeMethods.POINT { X = mouse.X, Y = mouse.Y };
            var hmon = NativeMethods.MonitorFromPoint(hpt, 2);
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
            NativeMethods.GetMonitorInfo(hmon, ref mi);
            int wl = mi.rcWork.Left, wt = mi.rcWork.Top, wr = mi.rcWork.Right, wb = mi.rcWork.Bottom;
            // 记住的尺寸/原图尺寸可能大于当前显示器，先钳到工作区内
            int maxW = wr - wl - 16, maxH = wb - wt - 16;
            if (maxW < 120) maxW = 120;
            if (maxH < 90) maxH = 90;
            if (pw > maxW) { pw = maxW; _extPreviewForm.ClientSize = new Size(pw, _extPreviewForm.ClientSize.Height); }
            if (ph > maxH) { ph = maxH; _extPreviewForm.ClientSize = new Size(_extPreviewForm.ClientSize.Width, ph); }
            if (x + pw > wr)
            {
                x = mouse.X - pw - 20;
                // 左侧放不下或会覆盖鼠标点：缩小宽度确保 x+pw <= mouse.X-4
                if (x < wl || x + pw > mouse.X - 4)
                {
                    int maxPw = mouse.X - wl - 24;
                    if (maxPw < 120) maxPw = 120;
                    if (pw > maxPw) { pw = maxPw; _extPreviewForm.ClientSize = new Size(pw, _extPreviewForm.ClientSize.Height); }
                    x = mouse.X - pw - 20;
                    if (x < wl) x = wl + 4;
                }
            }
            if (y + ph > wb) y = wb - ph - 4;
            if (y < wt) y = wt + 4;
            _extPreviewForm.Location = new Point(x, y);
        }

        private void PositionPreview(RowLabel row)
        {
            if (_previewForm == null) return;
            // 用面板自身边界定位：列表出现滚动条时行宽比面板窄（扣掉了滚动条宽度），
            // 按行右边界算会让预览窗压进面板里几个像素
            int lx = Left, rx = Right;
            int x = rx + 8, y = row.PointToScreen(Point.Empty).Y;
            int pw = _previewForm.Width, ph = _previewForm.Height;
            NativeMethods.POINT pt;
            NativeMethods.GetCursorPos(out pt);
            var hmon = NativeMethods.MonitorFromPoint(pt, 2);
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
            NativeMethods.GetMonitorInfo(hmon, ref mi);
            int wl = mi.rcWork.Left, wt = mi.rcWork.Top, wr = mi.rcWork.Right, wb = mi.rcWork.Bottom;
            // 记住的尺寸/原图尺寸可能大于当前显示器，先钳到工作区内
            int maxW = wr - wl - 16, maxH = wb - wt - 16;
            if (maxW < 120) maxW = 120;
            if (maxH < 90) maxH = 90;
            if (pw > maxW) { pw = maxW; _previewForm.ClientSize = new Size(pw, _previewForm.ClientSize.Height); }
            if (ph > maxH) { ph = maxH; _previewForm.ClientSize = new Size(_previewForm.ClientSize.Width, ph); }
            if (x + pw > wr)
            {
                x = lx - pw - 8;
                // 左侧放不下或会覆盖面板：缩小宽度确保 x+pw <= lx-4（不覆盖当前行）
                if (x < wl || x + pw > lx - 4)
                {
                    int maxPw = lx - wl - 12;
                    if (maxPw < 120) maxPw = 120;
                    if (pw > maxPw) { pw = maxPw; _previewForm.ClientSize = new Size(pw, _previewForm.ClientSize.Height); }
                    x = lx - pw - 8;
                    if (x < wl) x = wl + 4;
                }
            }
            if (y + ph > wb) y = wb - ph - 4;
            if (y < wt) y = wt + 4;
            _previewForm.Location = new Point(x, y);
            Program.DebugLog("[panel-preview] position level=" + _level + " panelL=" + lx + " panelR=" + rx + " rowY=" + row.PointToScreen(Point.Empty).Y + " pvSize=" + pw + "x" + ph + " -> pvAt=" + x + "," + y + " workArea=" + wl + "," + wt + "-" + wr + "," + wb + " mouse=" + pt.X + "," + pt.Y);
        }

        private void OnRowDown(RowLabel row, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 分栏线热区（±4px）：进入拖线模式（Capture 保跨行跟手），不进文件拖拽
                if (Config.RowSwapSides && Math.Abs(e.X - _rowSideZone) <= 4)
                {
                    _splitDrag = true;
                    _splitDragOff = e.X - _rowSideZone;
                    try { row.Capture = true; } catch { }
                    return;
                }
                _dragStart = e.Location;
                _dragRow = row;
                _dragged = false;
            }
        }

        private void OnRowMove(RowLabel row, MouseEventArgs e)
        {
            // 拖线中：线跟手（clamp 保左右栏最小宽度），实时刷新所有行自绘
            if (_splitDrag)
            {
                int min = 78, max = row.Width - 130;
                if (max < min) max = min;
                int v = e.X - _splitDragOff;
                if (v < min) v = min;
                if (v > max) v = max;
                if (v != _rowSideZone)
                {
                    _rowSideZone = v;
                    foreach (var r in _rows)
                        if (!r.IsDisposed) { r.SplitX = v; r.Invalidate(); }
                }
                return;
            }
            if (e.Button == MouseButtons.Left && _dragRow != null && !_dragged)
            {
                if (Math.Abs(e.X - _dragStart.X) > 3 || Math.Abs(e.Y - _dragStart.Y) > 3)
                {
                    var sel = SelRows();
                    string[] paths;
                    if (row.Selected && sel.Count > 1) paths = sel.Select(r => r.Full).ToArray();
                    else paths = new string[] { row.Full };
                    _dragged = true;
                    _dragging = true;
                    row.DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move);
                    _dragging = false;
                    _dragRow = null;
                }
            }
            // 互换布局：同一行内横移切换调度——仅区域切换时动作（_wasInSide 守卫），
            // 避免右栏微动每次 MouseMove 重复调度（子面板弹出后 _hoverTimer=null 又被重新触发→闪烁）
            if (Config.RowSwapSides && e.Button == MouseButtons.None && _rowEdit == null && !SearchActive)
            {
                bool inSide = e.X < _rowSideZone;
                if (inSide != _wasInSide)
                {
                    _wasInSide = inSide;
                    if (inSide)
                    {
                        if (_hoverTimer != null || _previewTimer != null || _previewForm != null)
                        {
                            Program.DebugLog("[row] side-zone hold level=" + _level + " " + row.Name);
                            CancelHover();
                            // 不弹新不杀旧：只取消未触发的预览调度；已开的预览窗保留——
                            // 互换布局下鼠标从文件名横穿按钮列去左侧预览，半路不再被杀
                            if (_previewTimer != null)
                            {
                                _previewTimer.Stop(); _previewTimer.Dispose(); _previewTimer = null;
                                _previewPath = null;
                            }
                        }
                    }
                    else if (_hoverTimer == null && _previewTimer == null && _previewForm == null)
                    {
                        if (!Config.HoverSuppressedInside(!row.IsDir))
                        {
                            if (row.IsDir) ScheduleHover(row.Full, row.PointToScreen(Point.Empty).Y);
                            else if (Config.PreviewFiles && PreviewProvider.CanPreview(row.Full)) SchedulePreview(row);
                        }
                    }
                }
                // 分栏线热区：↔ 光标提示可拖
                if (Math.Abs(e.X - _rowSideZone) <= 4) row.Cursor = Cursors.SizeWE;
                else if (row.Cursor == Cursors.SizeWE) row.Cursor = null;
            }
        }

        private void OnRowUp(RowLabel row, MouseEventArgs e)
        {
            // 拖线结束：松开落点持久化（下次打开沿用），不开文件不进多选
            if (_splitDrag)
            {
                _splitDrag = false;
                try { row.Capture = false; } catch { }
                Config.RowSplitX = _rowSideZone;
                Config.Save();
                return;
            }
            if (_dragged) { _dragged = false; return; }
            if (e.Button != MouseButtons.Left) return;
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control) { ToggleSel(row); return; }
            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift) { RangeSel(row); return; }
            ClearSel();
            OpenPath(row.Full);
        }

        private void OnDrop(RowLabel row, DragEventArgs e)
        {
            string dstDir = row.Full;
            if (!Directory.Exists(dstDir)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            bool move = e.Effect == DragDropEffects.Move;
            Program.DebugLog("[drop-row] effect=" + e.Effect + " keyState=" + e.KeyState + " move=" + move + " dstDir=" + dstDir + " files=" + (files == null ? "null" : string.Join(";", files)));
            foreach (var src in files)
            {
                if (!string.IsNullOrEmpty(src) && (File.Exists(src) || Directory.Exists(src)) && src != dstDir)
                {
                    if (move) DoMove(src, dstDir);
                    else DoCopy(src, dstDir);
                }
            }
            ApplySel(row);
        }

        private void DoCopy(string src, string dstDir)
        {
            Task.Run(() =>
            {
                try
                {
                    string dst = UniqueDest(src, dstDir);
                    if (dst == src) { Program.DebugLog("[copy-skip] same path " + src); return; }
                    if (Directory.Exists(src)) CopyDir(src, dst);
                    else File.Copy(src, dst);
                    Program.DebugLog("[copy-ok] " + src + " -> " + dst);
                    BeginInvoke((MethodInvoker)delegate() { OnMoveDone(); Program.RefreshPanelsShowing(null, dstDir); });
                }
                catch (Exception ex) { Program.DebugLog("[copy-fail] " + src + " -> " + dstDir + " : " + ex.Message); }
            });
        }

        internal static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // 拖拽反馈光标：复制=箭头+加号，移动=纯箭头，禁止=箭头+红圈斜杠
        private static Cursor _dropCopyCursor, _dropMoveCursor, _dropNoneCursor;
        private static Cursor DropCopyCursor { get { if (_dropCopyCursor == null) _dropCopyCursor = MakeDropCursor(true); return _dropCopyCursor; } }
        private static Cursor DropMoveCursor { get { if (_dropMoveCursor == null) _dropMoveCursor = MakeDropCursor(false); return _dropMoveCursor; } }
        private static Cursor DropNoneCursor { get { if (_dropNoneCursor == null) _dropNoneCursor = MakeDropNoneCursor(); return _dropNoneCursor; } }

        private static Cursor MakeDropCursor(bool copy)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Cursors.Arrow.Draw(g, new Rectangle(0, 0, 32, 32));
                if (copy)
                {
                    g.FillRectangle(Brushes.White, 19, 19, 12, 12);
                    using (var pen = new Pen(Color.Black, 2))
                    {
                        g.DrawRectangle(pen, 19, 19, 11, 11);
                        g.DrawLine(pen, 25, 22, 25, 28);
                        g.DrawLine(pen, 22, 25, 28, 25);
                    }
                }
            }
            IntPtr mask = bmp.GetHbitmap();
            IntPtr color = bmp.GetHbitmap();
            var ii = new NativeMethods.ICONINFO { fIcon = false, xHotspot = 0, yHotspot = 0, hbmMask = mask, hbmColor = color };
            IntPtr hCur = NativeMethods.CreateIconIndirect(ref ii);
            NativeMethods.DeleteObject(mask);
            NativeMethods.DeleteObject(color);
            bmp.Dispose();
            return new Cursor(hCur);
        }

        private static Cursor MakeDropNoneCursor()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Cursors.Arrow.Draw(g, new Rectangle(0, 0, 32, 32));
                using (var pen = new Pen(Color.FromArgb(220, 60, 60), 2.5f))
                {
                    g.DrawEllipse(pen, 17, 17, 14, 14);
                    g.DrawLine(pen, 19.8f, 19.8f, 28.2f, 28.2f);
                }
            }
            IntPtr mask = bmp.GetHbitmap();
            IntPtr color = bmp.GetHbitmap();
            var ii = new NativeMethods.ICONINFO { fIcon = false, xHotspot = 0, yHotspot = 0, hbmMask = mask, hbmColor = color };
            IntPtr hCur = NativeMethods.CreateIconIndirect(ref ii);
            NativeMethods.DeleteObject(mask);
            NativeMethods.DeleteObject(color);
            bmp.Dispose();
            return new Cursor(hCur);
        }

        private void DoMove(string src, string dstDir)
        {
            Task.Run(() =>
            {
                try
                {
                    string dst = UniqueDest(src, dstDir);
                    if (dst == src) { Program.DebugLog("[move-skip] same path " + src); return; }
                    if (Directory.Exists(src)) Directory.Move(src, dst);
                    else File.Move(src, dst);
                    Program.DebugLog("[move-ok] " + src + " -> " + dst);
                    BeginInvoke((MethodInvoker)delegate() { OnMoveDone(); Program.RefreshPanelsShowing(Path.GetDirectoryName(src), dstDir); });
                }
                catch (Exception ex) { Program.DebugLog("[move-fail] " + src + " -> " + dstDir + " : " + ex.Message); }
            });
        }

        private void OnMoveDone()
        {
            try { System.Media.SystemSounds.Beep.Play(); RefreshPanel(); } catch { }
        }

        internal static string UniqueDest(string src, string dstDir)
        {
            string name = Path.GetFileName(src);
            string dst = Path.Combine(dstDir, name);
            if (!File.Exists(dst) && !Directory.Exists(dst)) return dst;
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            int i = 1;
            while (true)
            {
                string cand = Path.Combine(dstDir, stem + "_" + i + ext);
                if (!File.Exists(cand) && !Directory.Exists(cand)) return cand;
                i++;
            }
        }

        public void RefreshPanel()
        {
            if (!IsHandleCreated || _path == null) return;
            // 删除/移动后重建列表：保持滚动位置（自管偏移量直接读写，无 AutoScroll 负值约定）
            int keepScroll = 0;
            if (_listPanel != null) keepScroll = ((SlimScrollPanel)_listPanel).ContentScrollPos;
            BackColor = Config.ListBg;
            Win11.Apply(Handle, Config.IsDark, Config.Border);
            // 切配色/主题后窗口已存在，WinForms 不会自动重发 WM_NCPAINT，主动重算并重画
            // 非客户区描边（圆角模式才有描边；直角模式无副作用），否则边框卡在旧色
            Win11.RefreshNc(Handle);
            int x = Location.X, y = Location.Y;
            ShowPanel(_path, x, y);
            if (keepScroll > 0 && _listPanel != null)
            {
                ((SlimScrollPanel)_listPanel).SetOffset(keepScroll);
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || _listPanel == null) return;
                    ((SlimScrollPanel)_listPanel).SetOffset(keepScroll);
                    _fnSyncListScroll();
                });
            }
            else
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || _listPanel == null) return;
                    _fnSyncListScroll();
                });
            }
        }

        // 当前面板是否正在显示指定目录
        public bool ShowingPath(string dir)
        {
            return dir != null && _path != null && string.Equals(_path, dir, StringComparison.OrdinalIgnoreCase);
        }

        private static ToolStripMenuItem MI(string text, Action handler)
        {
            return new ToolStripMenuItem(text, null, (EventHandler)((s, e) => handler()));
        }

        private static void EditFile(string path)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(path) { Verb = "edit", UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); }
                catch (Exception ex) { Program.DebugLog("[edit] fail " + path + " " + ex.Message); }
            }
        }

        private void PopupMenu(RowLabel row, MouseEventArgs e)
        {
            // 多选（右键行在选中集合内）→ 自绘批量菜单；单选 → 直接 Shell 原生菜单
            var sel = SelRows();
            if (sel.Count > 1 && sel.Contains(row))
            {
                var m = new ContextMenuStrip { BackColor = Config.BG, ForeColor = Config.FG, Font = new Font(Config.FontName, Config.FontSize) };
            m.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
                m.Items.Add(MI(I18n.T("delete_n", sel.Count), () => RecycleRows(sel)));
                m.Items.Add(MI(I18n.T("copy_path_n", sel.Count), () => CopyText(string.Join("\n", sel.Select(r => r.Full)))));
                m.Items.Add(MI(I18n.T("cancel_sel"), () => ClearSel()));
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(MI(I18n.T("native_menu"), () => { if (!row.IsDisposed) ShowShellMenu(row); }));
                MenuOpen = true;
                m.Closed += (s, ev) => MenuOpen = false;
                m.Show(row, e.Location);
                return;
            }
            // 右键高亮与悬停一致：直接用 LevelHoverColor，不占 Selected 状态（不影响 Ctrl/Shift 多选）
            if (_contextRow != null && _contextRow != row) ApplySel(_contextRow);
            _contextRow = row;
            row.BackColor = Config.ListHv;
            ShowShellMenu(row);
            // Shell 原生菜单是模态的，返回后恢复行底色
            if (_contextRow == row) { _contextRow = null; if (!row.IsDisposed) ApplySel(row); }
        }

        // Shell 原生菜单可能在模态期间执行了 删除/重命名/粘贴 等目录变更：
        // 菜单返回后对比目录条目，有变化才重建列表（否则必须重开列表才能看到删除结果）
        private void ShowShellMenu(RowLabel row)
        {
            string[] before = null;
            try { if (_path != null) before = Directory.GetFileSystemEntries(_path).OrderBy(s => s).ToArray(); } catch { }
            ShellContextMenu.Show(row.Full, Handle, null);
            if (before == null) return;
            try
            {
                var after = Directory.GetFileSystemEntries(_path).OrderBy(s => s).ToArray();
                if (!before.SequenceEqual(after)) RefreshPanel();
            }
            catch { }
        }

        // 行内原地重命名：行变编辑框，Enter/失焦提交、Esc 取消（与资源管理器 F2 一致）
        private void BeginRowRename(RowLabel row)
        {
            if (row == null || _rowEdit != null || _listPanel == null) return;
            ModalOpen = true;
            string full = row.Full;
            string name = row.Name;
            bool isDir = row.IsDir;
            var tb = new TextBox
            {
                Font = row.Font,
                BackColor = Config.ListBg,
                ForeColor = Config.ListFg,
                Tag = "noTint",
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(row.Location.X, row.Location.Y),
                Size = new Size(row.Width, Math.Max(22, row.Height)),
                Text = name,
            };
            _rowEdit = tb;
            _rowEditRow = row;
            Action<bool> end = null;
            end = delegate(bool commit)
            {
                if (_rowEdit == null) return;
                var box = _rowEdit;
                _rowEdit = null;
                var editRow = _rowEditRow;
                _rowEditRow = null;
                string nn = commit ? box.Text.Trim() : "";
                try { _listPanel.Controls.Remove(box); box.Dispose(); } catch { }
                if (editRow != null) editRow.Visible = true;
                ModalOpen = false;
                if (commit && !string.IsNullOrEmpty(nn) && !string.Equals(nn, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (RenamePath(full, nn))
                    {
                        if (isDir) Program.RebasePaths(full, Path.Combine(Path.GetDirectoryName(full), nn));
                        OnMoveDone();
                    }
                }
            };
            tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Return) { e.SuppressKeyPress = true; end(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; end(false); }
            };
            // 点别处 = 提交（与标题重命名一致）
            tb.LostFocus += (s, e) => { if (_rowEdit == tb) end(true); };
            row.Visible = false;
            ((SlimScrollPanel)_listPanel).AddContent(tb);
            tb.BringToFront();
            // 面板默认不抢前台，编辑要主动激活才能收到键盘
            if (!Focused) Activate();
            tb.Focus();
            int stemLen = Path.GetFileNameWithoutExtension(name).Length;
            if (!isDir && stemLen > 0 && stemLen < name.Length) { tb.SelectionStart = 0; tb.SelectionLength = stemLen; }
            else tb.SelectAll();
        }

        // 取消行内重命名（Form 级 Esc 在 KeyPreview 下先于 TextBox 到达，走这里）
        private void CancelRowRename()
        {
            if (_rowEdit == null) return;
            var box = _rowEdit;
            _rowEdit = null;
            var row = _rowEditRow;
            _rowEditRow = null;
            try { _listPanel.Controls.Remove(box); box.Dispose(); } catch { }
            if (row != null) row.Visible = true;
            ModalOpen = false;
        }

        private void Rename(RowLabel row, MouseEventArgs e)
        {
            string oldName = Path.GetFileName(row.Full);
            int sx = row.PointToScreen(e.Location).X;
            int sy = row.PointToScreen(e.Location).Y;
            ModalOpen = true;
            try
            {
                using (var dlg = new RenameDialog(oldName, sx, sy))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.NewName) && dlg.NewName != oldName)
                    {
                        // 复用统一入口：校验非法字符/重名、同步固定项路径、刷新相关面板
                        if (RenamePath(row.Full, dlg.NewName))
                        {
                            if (row.IsDir) Program.RebasePaths(row.Full, Path.Combine(Path.GetDirectoryName(row.Full), dlg.NewName));
                            OnMoveDone();
                        }
                    }
                }
            }
            finally { ModalOpen = false; }
        }

        private void RecycleRows(List<RowLabel> rows)
        {
            var paths = rows.Where(r => File.Exists(r.Full) || Directory.Exists(r.Full)).Select(r => r.Full).ToList();
            if (paths.Count == 0) return;
            // 模态期间主循环暂停自动关闭（否则确认框会随面板被连带销毁）
            ModalOpen = true;
            bool yes;
            try
            {
                yes = MessageBox.Show(this, I18n.T("delete_confirm", paths.Count), I18n.T("delete_title"), MessageBoxButtons.YesNo) == DialogResult.Yes;
            }
            finally { ModalOpen = false; }
            if (!yes) return;
            ClearSel();
            if (RecyclePaths(paths.ToArray())) OnMoveDone();
            else Program.Log("[删除失败]");
        }

        private static bool RecyclePaths(string[] paths)
        {
            try
            {
                var op = new NativeMethods.SHFILEOPSTRUCTW();
                op.hwnd = IntPtr.Zero;
                op.wFunc = NativeMethods.FO_DELETE;
                op.pFrom = string.Join("\0", paths) + "\0";
                op.pTo = null;
                op.fFlags = NativeMethods.FOF_ALLOWUNDO | NativeMethods.FOF_NOCONFIRMATION | NativeMethods.FOF_SILENT | NativeMethods.FOF_NOERRORUI;
                return NativeMethods.SHFileOperationW(ref op) == 0;
            }
            catch { return false; }
        }

        private void CopyText(string text) { try { Clipboard.SetText(text); } catch { } }

        private void ShowToast(string msg)
        {
            var t = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                BackColor = Config.BG,
                ShowInTaskbar = false,
                TopMost = true,
                Size = new Size(60, 22),
            };
            var lbl = new Label { Text = msg, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Config.FG, BackColor = Config.BG, Font = new Font("Microsoft YaHei", 9) };
            t.Controls.Add(lbl);
            t.Location = new Point(Left + (Width - t.Width) / 2, Top + 34);
            t.Show();
            var tm = new Timer { Interval = 800 };
            tm.Tick += (s, e) => { tm.Stop(); tm.Dispose(); t.Close(); t.Dispose(); };
            tm.Start();
        }

        // 复制文件本身到剪贴板（FileDrop 格式，可粘贴到资源管理器）
        private void CopyFileToClipboard(string full)
        {
            try
            {
                if (!File.Exists(full) && !Directory.Exists(full)) return;
                var files = new System.Collections.Specialized.StringCollection();
                files.Add(full);
                Clipboard.SetFileDropList(files);
                ShowToast(I18n.T("copied"));
            }
            catch { }
        }

        private static void OpenPath(string path) { try { Process.Start(path); } catch { } }

        private static void OpenInExplorer(string full)
        {
            try
            {
                if (Directory.Exists(full)) Process.Start(full);
                else Process.Start("explorer.exe", "/select,\"" + full + "\"");
            }
            catch (Exception ex) { Program.Log("[打开失败] " + ex.Message); }
        }

        public int RightEdge() { return Left + Width; }
        public int Level { get { return _level; } }
        public int GeometryY() { return Top; }
        public int HeadHeight() { return _level == 0 ? _headH : 0; }

        public bool ContainsPoint(int x, int y)
        {
            if (!IsHandleCreated || !Visible) return false;
            return Left <= x && x <= Left + Width && Top <= y && y <= Top + Height;
        }

        public bool MenuOpen { get { return _menuOpen; } set { _menuOpen = value; } }
        // 模态对话框（删除确认/重命名等）显示期间置位，主循环应暂停自动关闭
        public bool ModalOpen;
        public bool IsDragging { get { return _dragging; } }
        public string CurrentPath { get { return _path; } }

        public void ClosePanel()
        {
            Program.DebugLog("[panel] ClosePanel level=" + _level + " path=" + _path + " pinned=" + _pinned);
            if (_pinned) return;
            CancelHover();

            CancelPreview();
            if (IsHandleCreated) try { Close(); Dispose(); } catch { }
        }

        // 列表操作栏滚轮切换配色：与桌面卡片（PinCardForm.CyclePalette）同一套轮流切换语义
        private static int _paletteWheelAt = -1;

        // 指针是否落在顶部操作栏（排序栏 + 名称条）：只有这里滚轮才切换配色，列表内仍走滚动
        public bool InHeader(int screenX, int screenY)
        {
            if (!IsHandleCreated || IsDisposed || !Visible) return false;
            var p = PointToClient(new Point(screenX, screenY));
            return p.X >= 0 && p.X < ClientSize.Width && p.Y >= 0 && p.Y < _headH;
        }

        // ---- 拖拽滚轮 / 翻页键路由入口（由 Program 低级钩子按指针位置调用）----
        // 指针是否落在列表行区（含细滚动条）：拖拽滚轮只在行区滚动，操作栏滚轮仍是配色切换
        public bool ListRectContains(int x, int y)
        {
            if (_listPanel == null || IsDisposed || !Visible) return false;
            var r = _listPanel.RectangleToScreen(_listPanel.ClientRectangle);
            return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
        }

        // ---- 序号叠加：行悬停时在该行图标位显示 1 起序号（与行内操作按钮同生共死）----
        // 清掉所有行的序号显示（进入守卫分支/离开行时调用，防止残留）
        private void _fnClearRowIndex()
        {
            foreach (var r in _rows)
                if (!r.IsDisposed && r.ShowIndex) { r.ShowIndex = false; r.Invalidate(); }
        }

        // 双击顶部操作栏：资源管理器打开当前面板所在文件夹（子面板各自打开自己的目录）
        // 手动双击判定：首击会进模态拖动（ReleaseCapture+HTCAPTION 移动循环会吞掉首个抬起），
        // 系统双击时序被打乱、e.Clicks 经常到不了 2——自行按系统双击时间/范围判定
        private int _hdrLastDownTick;
        private Point _hdrLastDownPos;
        private bool _fnHeaderDoubleClick()
        {
            int now = Environment.TickCount;
            var pt = Cursor.Position;
            bool dbl = _hdrLastDownTick != 0
                && now - _hdrLastDownTick >= 0 && now - _hdrLastDownTick < SystemInformation.DoubleClickTime
                && Math.Abs(pt.X - _hdrLastDownPos.X) <= SystemInformation.DoubleClickSize.Width / 2
                && Math.Abs(pt.Y - _hdrLastDownPos.Y) <= SystemInformation.DoubleClickSize.Height / 2;
            // 触发后清零：三连击不会连续触发两次"双击"
            _hdrLastDownTick = dbl ? 0 : now;
            _hdrLastDownPos = pt;
            return dbl;
        }

        private void _fnOpenInExplorer()
        {
            if (string.IsNullOrEmpty(_path) || !Directory.Exists(_path)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_path) { UseShellExecute = true }); }
            catch (Exception ex) { Program.DebugLog("[panel] header-open fail " + _path + " " + ex.Message); }
        }

        // OLE 拖拽中滚轮滚动列表：拖拽循环截走 WM_MOUSEWHEEL，OnMouseWheel 收不到，
        // 由 WH_MOUSE_LL 钩子转发，步长与 OnMouseWheel 一致（一格约三行，行高≈24）
        public void ScrollByWheel(int delta)
        {
            if (_listPanel == null || IsDisposed) return;
            _listPanel.ScrollBy(-(delta / 120) * 72);
        }

        // 翻页键（PageUp/PageDown）：一页 = 列表可视高减一行，保留上下文
        public void PageKey(int dir)
        {
            if (_listPanel == null || IsDisposed) return;
            _listPanel.ScrollBy(dir * Math.Max(24, _listPanel.Height - 24));
        }

        // 由 Program.ListWheelFilter 调用（面板是非激活窗口，收不到自己的 WM_MOUSEWHEEL）
        public void CycleListPalette(int dir)
        {
            var schemes = Config.PaletteSchemes();
            int n = schemes.Count;
            if (n == 0) return;
            // 优先按内容定位当前方案（改过编辑器后也能接上）；定位不到时沿用上次循环位置
            int cur = -1;
            for (int i = 0; i < n; i++)
                if (string.Equals((schemes[i].Value ?? "").Trim(), (Config.ListPalette ?? "").Trim(), StringComparison.Ordinal)) { cur = i; break; }
            int idx = cur >= 0
                ? ((cur + dir) % n + n) % n
                : (_paletteWheelAt >= 0 ? ((_paletteWheelAt + dir) % n + n) % n : (dir > 0 ? 0 : n - 1));
            _paletteWheelAt = idx;
            Config.ListPalette = schemes[idx].Value;
            Config.ApplyListPalette();
            Program.SaveConfigDebounced();   // 与卡片换色一样，记住最近一次选择
            // 当前滚轮事件还压在即将重建的控件上：延迟到消息队列空闲后再重建所有面板
            try { BeginInvoke((MethodInvoker)delegate { Program.RefreshAllPanels(); }); } catch { }
        }

        private void TogglePinPopup()
        {
            _pinned = !_pinned;
            if (_pinBtn != null) _pinBtn.Image = PreviewForm.PinIcon(_pinned, Config.ListFg);
            if (_refreshBtn != null) _refreshBtn.Visible = _pinned;   // 刷新按钮只在固定面板出现
            // 固定瞬间锁住当前尺寸，取消固定后回到内容自适应
            if (_pinned) { _pinnedW = ClientSize.Width; _pinnedH = ClientSize.Height; }
            else { _pinnedW = 0; _pinnedH = 0; }
        }

        // 统一保顶：先恢复丢失的 WS_EX_TOPMOST（Show/Focus 激活会清），再按需提顶。
        // 每 Tick 无条件 SetWindowPos(TOPMOST) 会让叠加窗口互相翻转 z 序致闪烁，故仅在必要时提。
        // 面板、面板内预览、外部预览共用此方法，避免任一窗口 TopMost 丢失后陷入死锁
        // （TopMost 丢了 → CoveredByOther 首个上方窗口非 TopMost → return false → 永不提顶 → 永被盖住）
        private static DateTime _lastRestoreLog;
        private static DateTime _lastRaiseLog;
        private static DateTime _lastRaiseTime;

        // 命中测试：直接看 h 的位置上最顶层是谁。
        // 覆盖 CoveredByOther 漏判的情形——压住者不是 TopMost（说明 h 的 TopMost 已被系统重排/丢失），
        // 或 z 序被外部工具改动。外部进程窗口才计数，本进程窗口之间的相对顺序保持不动，避免互相翻转闪烁。
        private static bool CoveredByHitTest(IntPtr h)
        {
            try
            {
                int ex = NativeMethods.GetWindowLongW(h, NativeMethods.GWL_EXSTYLE);
                // 鼠标穿透窗口不会出现在命中测试里，跳过以免误判
                if ((ex & 0x20) != 0) return false;
                NativeMethods.RECT rc;
                if (!NativeMethods.GetWindowRect(h, out rc)) return false;
                if (rc.Right - rc.Left < 8 || rc.Bottom - rc.Top < 8) return false;
                int[] xs = { (rc.Left + rc.Right) / 2, rc.Left + 3, rc.Right - 3 };
                int[] ys = { (rc.Top + rc.Bottom) / 2, rc.Top + 3, rc.Bottom - 3 };
                int total = 0, covered = 0;
                foreach (int x in xs)
                {
                    foreach (int y in ys)
                    {
                        total++;
                        IntPtr top = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
                        if (top == IntPtr.Zero || top == h) continue;
                        bool mine = false;
                        IntPtr cur = top;
                        for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
                        {
                            if (cur == h) { mine = true; break; }
                            cur = NativeMethods.GetParent(cur);
                        }
                        if (mine) continue;
                        uint pid;
                        NativeMethods.GetWindowThreadProcessId(top, out pid);
                        if (pid == Program.CurrentPid) continue;
                        covered++;
                    }
                }
                return covered * 2 >= total;
            }
            catch { return false; }
        }

        public static void EnsureWinTopmost(IntPtr h, string tag)
        {
            if (h == IntPtr.Zero) return;
            int exStyle = 0;
            try { exStyle = NativeMethods.GetWindowLongW(h, NativeMethods.GWL_EXSTYLE); }
            catch { }
            // 补 WS_EX_TOPMOST 样式必须无条件执行（ContainsFocus 也不能跳过）：
            // Windows 在 Show/Activate 时会清掉 WS_EX_TOPMOST，若此时窗口恰有焦点，
            // 旧逻辑因 ContainsFocus 提前 return → 样式丢失无人补 → 窗口掉出 TopMost band
            // → 被资源管理器等普通窗口盖住。SetWindowPos(TOPMOST, SWP_NOACTIVATE) 不会
            // 激活窗口、不会打断输入、IME 候选窗由系统管理不受影响，是安全的。
            if ((exStyle & (int)NativeMethods.WS_EX_TOPMOST) == 0)
            {
                NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
                if ((DateTime.UtcNow - _lastRestoreLog).TotalMilliseconds > 500)
                {
                    _lastRestoreLog = DateTime.UtcNow;
                    Program.DebugLog("[z] lost-topmost " + tag + " ex=0x" + exStyle.ToString("X"));
                }
            }
            // 窗口内控件正在输入（搜索框/文本框）：跳过条件提顶（会触发重绘、可能盖 IME），
            // 但上面样式补回已执行，TopMost band 位置不会丢
            try
            {
                var f = Control.FromHandle(h) as Form;
                if (f != null && f.ContainsFocus) return;
            }
            catch { }
            bool byOther = Program.CoveredByOther(h);
            bool byHit = !byOther && CoveredByHitTest(h);
            if (byOther || byHit)
            {
                // 节流：与外部置顶窗口竞争时不要每 Tick 反复提顶导致闪烁
                if ((DateTime.UtcNow - _lastRaiseTime).TotalMilliseconds < 120) return;
                _lastRaiseTime = DateTime.UtcNow;
                NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
                if ((DateTime.UtcNow - _lastRaiseLog).TotalMilliseconds > 300)
                {
                    _lastRaiseLog = DateTime.UtcNow;
                    Program.DebugLog("[z] raise " + tag + (byHit ? " (hit-test)" : ""));
                }
            }
        }

        public void EnsureTopmost()
        {
            if (!IsHandleCreated || !Visible) return;
            EnsureWinTopmost(Handle, "panel L" + _level + " " + _path);
            // 预览必须始终在面板之上：无条件提顶。预览每面板唯一，不与其他预览竞争，
            // 不会引入多面板叠加闪烁；条件提顶时面板重绘/子面板 Show 后预览会沉到面板下
            if (_previewForm != null && _previewForm.IsHandleCreated && _previewForm.Visible)
                NativeMethods.SetWindowPos(_previewForm.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
        }

        // 级联层级色条：红橙黄绿青蓝紫循环，区分叠叠的面板
        // 面板底色：不做层级染色（v1.0.7 的红/橙/黄层级色在深色模式下是整片暗红/暗棕，
        // 用户两轮反馈不可接受；层级关系由子面板的错位排布自明）
        private static Color LevelBgColor(int level) { return Config.BG; }

    }

    internal class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder { get { return Config.BgHover; } }
        public override Color MenuItemBorder { get { return Config.BgHover; } }
        public override Color MenuItemSelected { get { return Config.BgHover; } }
        public override Color MenuStripGradientBegin { get { return Config.BG; } }
        public override Color MenuStripGradientEnd { get { return Config.BG; } }
        public override Color ToolStripDropDownBackground { get { return Config.BG; } }
        public override Color ImageMarginGradientBegin { get { return Config.BG; } }
        public override Color ImageMarginGradientMiddle { get { return Config.BG; } }
        public override Color ImageMarginGradientEnd { get { return Config.BG; } }
        public override Color SeparatorDark { get { return Config.BgHover; } }
        public override Color SeparatorLight { get { return Config.BgHover; } }
    }

    internal class HighlightOverlay : Form
    {
        public HighlightOverlay(Rectangle r)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // 关键：默认 MinimumSize 时系统会把窗口强制到最小跟踪高度(Win11 96DPI 下约 39px)，
            // 导致 21px 高的单行高亮框被撑成两行高。设为 (1,1) 解除该限制。
            MinimumSize = new Size(1, 1);
            Bounds = r;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80000;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(230, 9, 71, 113), 2))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
