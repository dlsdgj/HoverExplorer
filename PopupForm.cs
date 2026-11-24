using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 全局 Hint 目标：所有 HE 窗口的行/按钮统一编号；Ctl 用于定位标记，Exec 为命中后的动作
    internal sealed class HintItem
    {
        public Control Ctl;
        public Action Exec;
        public string Code;
        public Label Tag;
    }

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
            BackColor = Color.FromArgb(0xFF, 0xF5, 0x9C);
            _lbl = new SmoothLabel
            {
                AutoSize = false,
                ForeColor = Color.Black,
                BackColor = Color.FromArgb(0xFF, 0xF5, 0x9C),
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

        // 用 Show()/Hide() 才会真正走 ShowWithoutActivation + WS_EX_NOACTIVATE，
        // 直接改 Visible 可能让前台窗口失焦
        public void ShowTip() { if (!Visible) Show(); }
        public void HideTip() { if (Visible) Hide(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            // 提示文字一律 1px 细边：圆角模式的非客户区粗描边（FramePad 还会内缩裁掉文字）
            // 对小尺寸提示窗太重，不需要跟随 WindowCorners
            using (var pen = new Pen(Color.FromArgb(0xCC, 0xA8, 0x00))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // 列表/预览窗口的按钮提示统一管理：受 Config.ShowTips 开关控制。
    // 提示直接挂在宿主窗口底部，不再创建独立浮窗（会抢前台焦点）。
    internal static class Tips
    {
        private static int BarH { get { return Dpi.S(22); } }
        // 文案始终登记（与开关状态无关），开关开启后 ApplyConfig 才能为已存在的控件恢复绑定
        private static readonly Dictionary<Control, string> _map = new Dictionary<Control, string>();
        // 已挂上事件的控件，防止重复绑定
        private static readonly HashSet<Control> _bound = new HashSet<Control>();
        // 允许"菜单打开期间"仍显示底部提示的控件（复制按钮悬停即弹菜单，需放行）
        private static readonly HashSet<Control> _menuOk = new HashSet<Control>();
        private static readonly Timer _timer = new Timer { Interval = 300 };
        private static Timer _leaveCheck;          // 复制按钮"假 MouseLeave"延迟复查
        private static Control _leaveCheckTarget;
        private static TipForm _form;      // 仅 Flash（无宿主窗口的一次性门控提示）使用
        private static Control _cur;
        private static Form _host;
        private static SmoothLabel _bar;

        static Tips()
        {
            _timer.Tick += (s, e) => { _timer.Stop(); if (_cur != null) ShowAt(_cur); };
        }

        public static void Set(Control c, string text, bool allowWithMenu = false)
        {
            if (c == null || c.IsDisposed) return;
            if (string.IsNullOrEmpty(text))
            {
                _map.Remove(c); Unbind(c); _menuOk.Remove(c);
                return;
            }
            _map[c] = text;                         // 无论开关状态都登记，供开启后恢复
            if (allowWithMenu) _menuOk.Add(c); else _menuOk.Remove(c);
            if (Config.ShowTips) Bind(c); else Unbind(c);
        }

        internal static string TextOf(Control c)
        {
            if (c == null || c.IsDisposed) return null;
            string t;
            return _map.TryGetValue(c, out t) ? t : null;
        }

        // 开关切换时同步事件绑定：开启要为已存在的控件补绑，关闭要全部摘掉。
        // 之前只在关闭时清理，导致"关→开"之后预览窗等控件永远不再显示提示。
        public static void ApplyConfig()
        {
            if (Config.ShowTips)
            {
                foreach (var kv in _map.ToArray())
                {
                    if (kv.Key.IsDisposed) { _map.Remove(kv.Key); _menuOk.Remove(kv.Key); continue; }
                    Bind(kv.Key);
                }
                return;
            }
            foreach (var c in _bound.ToArray()) Unbind(c);
            Hide();
            if (_form != null) _form.HideTip();
        }

        private static void Bind(Control c)
        {
            if (c == null || c.IsDisposed || !_bound.Add(c)) return;
            c.MouseEnter += OnEnter;
            c.MouseMove += OnMove;
            c.MouseLeave += OnLeave;
            c.MouseDown += OnDown;
            c.Disposed += OnDisposed;
        }

        private static void Unbind(Control c)
        {
            if (c == null || !_bound.Remove(c)) return;
            c.MouseEnter -= OnEnter;
            c.MouseMove -= OnMove;
            c.MouseLeave -= OnLeave;
            c.MouseDown -= OnDown;
            c.Disposed -= OnDisposed;
        }

        private static void OnEnter(object s, EventArgs e)
        {
            _cur = s as Control;
            ShowAt(_cur);   // 悬停到控件即立即显示底部提示，不再延迟 300ms
        }

        // 底部条固定在窗口里，不需要跟随鼠标
        private static void OnMove(object s, EventArgs e) { }

        private static void OnLeave(object s, EventArgs e)
        {
            if (_cur != s) return;
            var c = s as Control;
            // 复制按钮悬停即弹菜单，菜单弹出会让按钮收到"假 MouseLeave"（光标其实还在按钮上）。
            // 同步判断容易被紧随其后的消息推翻，改为延迟复查光标真实落点：
            // 仍在按钮上就保留提示，真正移开才收起。
            if (c != null && !c.IsDisposed && _menuOk.Contains(c))
            {
                if (_leaveCheck == null)
                {
                    _leaveCheck = new Timer { Interval = 120 };
                    _leaveCheck.Tick += (s2, e2) =>
                    {
                        _leaveCheck.Stop();
                        var t = _leaveCheckTarget;
                        _leaveCheckTarget = null;
                        if (t == null || t.IsDisposed
                            || !t.RectangleToScreen(t.ClientRectangle).Contains(Cursor.Position)) Hide();
                    };
                }
                _leaveCheckTarget = c;
                _leaveCheck.Stop();
                _leaveCheck.Start();
                return;
            }
            Hide();
        }

        // 点了按钮就不再显示提示（比如点了固定按钮，提示不该一直挂着）
        private static void OnDown(object s, EventArgs e) { if (_cur == s) Hide(); }

        private static void OnDisposed(object s, EventArgs e)
        {
            var c = s as Control;
            if (c == null) return;
            _map.Remove(c);
            _menuOk.Remove(c);
            _bound.Remove(c);
            if (_cur == c) Hide();
            if (_host != null && (_host.IsDisposed || c == _host)) DetachBar();
        }

        private static void ShowAt(Control c)
        {
            if (!Config.ShowTips) return;
            // 菜单打开期间不弹提示，避免盖到菜单项上；复制按钮悬停即弹菜单，
            // 但它被标记为放行，否则它的提示永远没机会显示。
            if (Program.ShellMenuOpen && !_menuOk.Contains(c)) { _cur = null; return; }
            string t;
            if (!_map.TryGetValue(c, out t)) return;
            Form host = c.FindForm();
            if (host == null || host.IsDisposed) return;
            ShowBar(host, t);
        }

        // 宿主窗口底部提示条：纯子控件、不创建顶层窗口、不接收焦点
        internal static void ShowBar(Form host, string text)
        {
            if (host == null || host.IsDisposed) return;
            if (_bar == null || _bar.IsDisposed || _host != host)
            {
                DetachBar();
                _host = host;
                _bar = new SmoothLabel
                {
                    AutoSize = false,
                    AutoEllipsis = true,
                    Height = BarH,
                    TextAlign = ContentAlignment.MiddleLeft,
                    BackColor = Config.ListHdbg,
                    ForeColor = Config.FgDir,
                    Font = new Font(Config.FontName, 8f),
                    Padding = new Padding(8, 0, 8, 0),
                    Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                    Visible = false,
                };
                host.Controls.Add(_bar);
            }
            _bar.Text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
            _bar.Size = new Size(Math.Max(0, host.ClientSize.Width), BarH);
            _bar.Location = new Point(0, Math.Max(0, host.ClientSize.Height - BarH));
            _bar.Visible = true;
            _bar.BringToFront();
            Program.DebugLog("[tip] show host=" + host.GetType().Name + " text=" + _bar.Text
                + " bounds=" + _bar.Bounds + " vis=" + _bar.Visible);
        }

        // 一次性反馈提示（如"已复制文件"）：显示 ms 后自动收起。
        // 仅当底部条仍在显示本次文本时才隐藏，避免误关之后出现的悬停提示。
        private static Timer _autoHide;
        private static string _autoHideText;
        private static Form _autoHideHost;
        internal static void ShowBarTimed(Form host, string text, int ms)
        {
            ShowBar(host, text);
            _autoHideHost = host;
            _autoHideText = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (_autoHide == null)
            {
                _autoHide = new Timer();
                _autoHide.Tick += (s, e) =>
                {
                    _autoHide.Stop();
                    var h = _autoHideHost;
                    var t = _autoHideText;
                    _autoHideHost = null; _autoHideText = null;
                    if (h == null || h.IsDisposed || t == null) return;
                    if (_host != h || _bar == null || _bar.IsDisposed || !_bar.Visible) return;
                    if (_bar.Text != t) return;   // 已被悬停提示覆盖 → 不关
                    HideBar(h);
                };
            }
            _autoHide.Interval = Math.Max(100, ms);
            _autoHide.Stop();
            _autoHide.Start();
        }

        private static void DetachBar()
        {
            if (_bar == null) return;
            // 宿主窗口可能正在销毁，子控件集合已不可用：清理失败也不能影响窗口关闭
            try
            {
                if (!_bar.IsDisposed)
                {
                    if (_bar.Parent != null) _bar.Parent.Controls.Remove(_bar);
                    _bar.Dispose();
                }
            }
            catch { }
            _bar = null;
            _host = null;
        }

        internal static void HideBar() { HideBar(null); }

        // 定时提示（悬停提示 / "已复制X"反馈）在时长内不允许被移开、菜单开合等事件提前收起。
        private static bool TimedTipActive(Form host)
        {
            if (_autoHideHost == null) return false;
            if (host != null && _autoHideHost != host) return false;
            return _bar != null && !_bar.IsDisposed && _bar.Visible && _bar.Text == _autoHideText;
        }

        internal static void HideBar(Form host)
        {
            if (host != null && _host != host) return;   // 只收起属于该窗口的提示条
            bool blocked = TimedTipActive(host);
            Program.DebugLog("[tip] hide host=" + (host == null ? "null" : host.GetType().Name)
                + " text=" + (_bar == null || _bar.IsDisposed ? "null" : _bar.Text)
                + " blocked=" + blocked);
            if (blocked) return;
            _timer.Stop();
            if (_bar != null && !_bar.IsDisposed) _bar.Visible = false;
        }

        // 真正离开按钮 / 菜单关闭时主动结束指定文案的定时提示，不影响之后出现的"已复制X"
        internal static void EndTimedTip(Form host, string text)
        {
            if (host == null || host.IsDisposed || _host != host || _autoHideHost != host) return;
            if (_bar == null || _bar.IsDisposed || !_bar.Visible) return;
            string t = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (_bar.Text != t) return;
            if (_autoHide != null) _autoHide.Stop();
            _autoHideHost = null; _autoHideText = null;
            Program.DebugLog("[tip] end host=" + host.GetType().Name + " text=" + t);
            HideBar(host);
        }

        private static void Hide()
        {
            _timer.Stop();
            _cur = null;
            // 复制反馈等定时提示在其时长内不被"移开/按下"提前收起，保证显示满 1000ms
            if (_autoHideHost != null && _bar != null && !_bar.IsDisposed
                && _bar.Visible && _bar.Text == _autoHideText) return;
            HideBar();
        }

        // 一次性提示（门控发现提示用）：此时通常还没有任何悬停窗口可作宿主，
        // 只能在光标旁短暂显示；ShowTip 走 ShowWithoutActivation，不抢前台。
        private static Timer _flashTimer;
        public static void Flash(string text, int ms)
        {
            if (_form == null) _form = new TipForm();
            _form.SetText(text);
            _form.Follow();
            _form.ShowTip();
            if (_flashTimer == null) { _flashTimer = new Timer(); _flashTimer.Tick += (s2, e2) => { _flashTimer.Stop(); _form.HideTip(); }; }
            _flashTimer.Interval = Math.Max(300, ms);
            _flashTimer.Stop(); _flashTimer.Start();
        }

        // 仅 Flash 的独立提示窗口需要参与每 Tick 提顶；底部条是子控件，不需要
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
            // 故必须用配色方案背景色（ListBg，跟随方案）而非主题色 Config.BG，
            // 否则 tint 会把 Config.BG 换成浅色 lbg，行区四周/滚动条让位列露出浅色
            _content = new ContentPanel { BackColor = Config.ListBg, Tag = "noTint", Location = new Point(0, 0) };
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

    // 方案 A「统一弱化」：4 个按钮同尺寸、无边框、常态只有灰色图标，
    // 底色与悬停行完全一致（不出现独立色块）；仅 hover 的那个按钮显示淡圆角背景 + 强调色图标。
    internal class RowActionButton : Label
    {
        public bool Hovered;
        public Image HoverIcon;
        public Color HoverTint;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Config.ListHv))
                g.FillRectangle(bg, ClientRectangle);
            if (!Hovered || HoverTint.A == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RowLabel.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), 5f))
            using (var tint = new SolidBrush(HoverTint))
                g.FillPath(tint, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var img = Hovered ? (HoverIcon ?? Image) : Image;
            if (img == null) return;
            int iw = Math.Min(14, img.Width), ih = Math.Min(14, img.Height);
            e.Graphics.DrawImage(img, new Rectangle((Width - iw) / 2, (Height - ih) / 2, iw, ih));
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
        public Font SizeFont;          // 大小/子项数文本字体（MetaFont）；null=跟随行字体
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
        private static readonly Size _measureMax = new Size(int.MaxValue, int.MaxValue);
        private const TextFormatFlags _measureFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

        internal static string MidEllipsis(Graphics g, string s, Font f, float maxW)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (TextRenderer.MeasureText(g, s, f, _measureMax, _measureFlags).Width <= maxW) return s;
            const string ell = "…";
            float ellW = TextRenderer.MeasureText(g, ell, f, _measureMax, _measureFlags).Width;
            if (maxW <= ellW) return ell;
            int lo = 1, hi = Math.Max(1, s.Length - 1), best = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (MeasureBuild(g, s, f, mid, ell) <= maxW) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best > 0 ? BuildStr(s, best, ell) : ell;
        }

        private static string BuildStr(string s, int k, string ell)
        {
            int a = Math.Max(1, (int)Math.Round(k * 0.6));
            int b = Math.Max(1, k - a);
            if (a > s.Length) a = s.Length;
            if (b > s.Length - a) b = s.Length - a;
            if (b < 1) return s.Substring(0, a) + ell;
            return s.Substring(0, a) + ell + s.Substring(s.Length - b);
        }

        private static float MeasureBuild(Graphics g, string s, Font f, int k, string ell)
        {
            return TextRenderer.MeasureText(g, BuildStr(s, k, ell), f, _measureMax, _measureFlags).Width;
        }

        // 右侧预览触发带宽度（0 = 整行均可触发）。夹到 0~80，防止配置异常撑满整行
        public static int TriggerBandW()
        {
            int v = Config.RowTriggerW;
            if (v < 0) v = 0;
            if (v > 80) v = 80;
            return v;
        }

        // 圆角矩形路径：半径自适应短边，退化为普通矩形避免极窄条异常
        internal static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 悬停 / 选中 / 拖拽：高亮色画成圆角卡片，四角露出斑马底色（OrigBg），不再是整块矩形
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color baseBg = OrigBg.A == 0 ? BackColor : OrigBg;
            using (var bg = new SolidBrush(baseBg))
                g.FillRectangle(bg, ClientRectangle);
            if (BackColor == OrigBg || BackColor.A == 0) return;
            var r = new RectangleF(1, 1, Math.Max(0, Width - 2), Math.Max(0, Height - 2));
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(r, Math.Min(6f, r.Height / 2f)))
            using (var hi = new SolidBrush(BackColor))
                g.FillPath(hi, path);
        }

        // 大小指示条：圆角 + 横向渐变。淡端始终在远离名称的一侧 ——
        // 名称在右（互换布局）时左端淡，名称在左（默认布局）时右端淡。
        // 淡端用同色全透明，自动与斑马底 / 悬停底 / 选中底融合，不会插值出灰黑色。
        private static void DrawSizeBar(Graphics g, RectangleF r, bool nameOnRight)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color solid = Config.ListBar;
            Color fade = Color.FromArgb(0, solid);
            using (var path = RoundRect(r, Math.Min(6f, r.Height / 2f)))
            using (var br = new LinearGradientBrush(r,
                nameOnRight ? fade : solid,
                nameOnRight ? solid : fade,
                LinearGradientMode.Horizontal))
                g.FillPath(br, path);
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
                        DrawSizeBar(g, new RectangleF(PadEdge, 0, barW, Height), true);
                }
                int split = SplitX;
                if (split >= 0 && split < Width - 4)
                {
                    using (var p = new Pen(Config.ListBd))
                        g.DrawLine(p, split, 0, split, Height);
                }
                // 右侧预览触发带分隔线（与左分栏线同样的 1px 列表边线）
                // 名称靠右时可隐藏这条线；bandX 仍用于名称留白，触发区本身不受影响
                int bandW = TriggerBandW();
                int bandX = Width - bandW;
                if (!Config.RowHideBandLine && bandW > 0 && bandX > 8 && bandX > split + 16)
                {
                    using (var p = new Pen(Config.ListBd))
                        g.DrawLine(p, bandX, 0, bandX, Height);
                }
                if (!string.IsNullOrEmpty(SizeText))
                {
                    // GDI+ 绘制（与名称同路径同垂直居中公式），TextRenderer(GDI) 笔画粗粝显锯齿
                    var fS = SizeFont ?? Font;
                    float lhS = fS.GetHeight(g);
                    using (var b = new SolidBrush(Config.ListMu))
                        g.DrawString(SizeText, fS, b, PadEdge, (Height - lhS) / 2, StringFormat.GenericTypographic);
                }
                if (!string.IsNullOrEmpty(Name))
                {
                    // 图标紧贴分隔线右侧，名称自图标右侧起单行排布，放不下中间省略
                    int iconSz = Dpi.S(16);
                    float textX = split + Dpi.S(3);
                    if (ShowIndex) { DrawIndex(g, split + Dpi.S(1)); textX = split + Dpi.S(3) + iconSz + Dpi.S(3); }
                    else if (Icon != null)
                    {
                        g.DrawImage(Icon, new Rectangle(split + Dpi.S(3), (Height - iconSz) / 2, iconSz, iconSz));
                        textX = split + Dpi.S(3) + iconSz + Dpi.S(3);
                    }
                    float maxW = bandX - Dpi.S(6) - textX;
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
            int rowIconSz = Dpi.S(16);
            if (ShowIndex) DrawIndex(e.Graphics, Dpi.S(2));
            else if (Icon != null)
                e.Graphics.DrawImage(Icon, new Rectangle(Dpi.S(4), (Height - rowIconSz) / 2, rowIconSz, rowIconSz));
            // 右侧预览触发带：竖分隔线与互换布局分栏线同款 1px 列表边线
            int tbW = TriggerBandW();
            int tbX = Width - tbW;
            if (tbW > 0 && tbX > 8)
            {
                using (var p = new Pen(Config.ListBd))
                    e.Graphics.DrawLine(p, tbX, 0, tbX, Height);
            }
            // 大小背景条（文件，文件夹不画）：仍贴右缘，不受预览触发带影响
            if (!IsDir && SizeRatio > 0 && SizeColW > 0)
            {
                float barW = SizeColW * SizeRatio;
                if (barW > 0)
                    DrawSizeBar(e.Graphics, new RectangleF(Width - PadEdge - barW, 0, barW, Height), false);
            }
            if (!string.IsNullOrEmpty(SizeText))
            {
                // GDI+ 右对齐：与名称同一渲染路径/垂直居中公式（TextRenderer(GDI) 笔画粗粝显锯齿）
                var fS = SizeFont ?? Font;
                float lhS = fS.GetHeight(e.Graphics);
                float wS = e.Graphics.MeasureString(SizeText, fS, PointF.Empty, StringFormat.GenericTypographic).Width;
                using (var b = new SolidBrush(Config.ListMu))
                    e.Graphics.DrawString(SizeText, fS, b, Width - PadEdge - wS, (Height - lhS) / 2, StringFormat.GenericTypographic);
            }
            // 名称单行显示、过长时中间省略（Label 文本会换行，故走自绘）
            if (!string.IsNullOrEmpty(Name))
            {
                float textX = Padding.Left;              // 图标位之后
                float maxW = tbX - 6 - textX;            // 右侧留出大小文本列与触发带
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
            // 圆角描边（与设置窗预览按钮一致）
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(BorderColor, 1))
            using (var p = new System.Drawing.Drawing2D.GraphicsPath())
            {
                int r = 3, w = Width - 1, h = Height - 1;
                if (r > 0 && w >= r * 2 && h >= r * 2)
                {
                    p.AddArc(0, 0, r * 2, r * 2, 180, 90);
                    p.AddArc(w - r * 2, 0, r * 2, r * 2, 270, 90);
                    p.AddArc(w - r * 2, h - r * 2, r * 2, r * 2, 0, 90);
                    p.AddArc(0, h - r * 2, r * 2, r * 2, 90, 90);
                    p.CloseFigure();
                }
                else
                {
                    p.AddRectangle(new Rectangle(0, 0, w, h));
                }
                e.Graphics.DrawPath(pen, p);
            }
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
                using (var b = new SolidBrush(Color.FromArgb(30, ForeColor)))
                    g.FillRectangle(b, 0, 0, fw, h);
            using (var b = new SolidBrush(Color.FromArgb(45, ForeColor)))
                g.FillRectangle(b, 0, h - 2, w, 2);
            if (fw > 0)
                using (var b = new SolidBrush(Config.ListSel))
                    g.FillRectangle(b, 0, h - 2, fw, 2);
        }
    }

    // 音频信息区：封面阴影/图片统一画在背景层。
    // 子控件改用透明背景后，会向父级取 PaintBackground，这样文字/chip/倍速尺
    // 压在阴影上也不会出现直角底色切断（方案的“同一张自绘画布”）。
    internal sealed class AudioInfoBody : Panel
    {
        internal Action<Graphics> ArtPainter;

        internal AudioInfoBody()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (ArtPainter != null) ArtPainter(e.Graphics);
        }
    }

    // 封面上的搜索按钮：自绘抗锯齿圆角底 + 放大镜，不用 Label+Region（会有锯齿）。
    internal sealed class ImageSearchButton : Control
    {
        internal Action<MouseButtons> Clicked;

        internal ImageSearchButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int w = Width - 1, h = Height - 1;
            using (var path = PreviewForm.RoundRectPath(0, 0, w, h, Math.Max(3, w / 4)))
            using (var b = new SolidBrush(Color.FromArgb(215, 24, 24, 24)))
                g.FillPath(b, path);
            float r = w * 0.24f;
            float cx = w * 0.42f, cy = w * 0.42f;
            using (var pen = new Pen(Color.White, Math.Max(1.2f, w / 12f)))
            {
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                g.DrawLine(pen, cx + r * 0.75f, cy + r * 0.75f, cx + r * 1.55f, cy + r * 1.55f);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Program.DebugLog("[searchbtn] control down=" + e.Button);
            Action<MouseButtons> cb = Clicked;
            if (cb != null) cb(e.Button);
        }
    }

    // 图片预览的画布：棋盘格底（6% 前景色），让透明 PNG 也能看出边界。
    // 棋盘格缓存到位图：GIF 逐帧刷新时透明 PictureBox 会让本面板每帧重绘背景，
    // 若每帧画上千个小矩形会显著拖慢动画；缓存后每帧只需一次 DrawImage。
    internal class CheckerPanel : Panel
    {
        private Bitmap _bgCache;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (_bgCache == null || _bgCache.Width != Width || _bgCache.Height != Height)
                RebuildCache();
            if (_bgCache != null) e.Graphics.DrawImageUnscaled(_bgCache, 0, 0);
            else base.OnPaintBackground(e);
        }

        private void RebuildCache()
        {
            if (_bgCache != null) { _bgCache.Dispose(); _bgCache = null; }
            if (Width <= 0 || Height <= 0) return;
            var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(BackColor);
                int s = 8;
                Color fg = ForeColor;
                using (var b = new SolidBrush(Color.FromArgb(
                    (BackColor.R * 94 + fg.R * 6) / 100,
                    (BackColor.G * 94 + fg.G * 6) / 100,
                    (BackColor.B * 94 + fg.B * 6) / 100)))
                {
                    for (int y = 0; y < Height; y += s)
                        for (int x = 0; x < Width; x += s)
                            if (((x / s) + (y / s)) % 2 == 0)
                                g.FillRectangle(b, x, y, s, s);
                }
            }
            _bgCache = bmp;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_bgCache != null) { _bgCache.Dispose(); _bgCache = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _bgCache != null) { _bgCache.Dispose(); _bgCache = null; }
            base.Dispose(disposing);
        }
    }

    // 文本预览用 RichTextBox：RichEdit 内部会在 WM_SETCURSOR 把光标强制成 I 形，
    // 单纯在 MouseMove 里设 Cursor 会被反复覆盖 → 链接上手型不稳定/默认 I 形。
    // 这里重写 WM_SETCURSOR：光标位置落在链接上时强制 Hand，否则交还基类。
    internal class LinkRichTextBox : RichTextBox
    {
        public Func<Point, bool> IsOverLink;
        // 返回非 null 表示已消费本次 Ctrl+V（值为要插入的 md 文本）
        public Func<Image, string> PasteImage;
        public Action AfterImagePaste;
        private const int WM_SETCURSOR = 0x0020;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.V) && PasteImage != null && Clipboard.ContainsImage())
            {
                Image img = null;
                try { img = Clipboard.GetImage(); } catch { }
                if (img != null)
                {
                    string md;
                    try { md = PasteImage(img); } finally { img.Dispose(); }
                    if (md != null)
                    {
                        int pos = SelectionStart;
                        SelectedText = md;
                        SelectionStart = pos + md.Length;
                        if (AfterImagePaste != null) AfterImagePaste();
                        return true;   // 吞掉，避免 RichEdit 再把原图当 RTF 粘进来
                    }
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETCURSOR && IsOverLink != null)
            {
                NativeMethods.POINT pt;
                if (NativeMethods.GetCursorPos(out pt))
                {
                    Point local = PointToClient(new Point(pt.X, pt.Y));
                    if (local.X >= 0 && local.Y >= 0 && local.X < Width && local.Y < Height
                        && IsOverLink(local))
                    {
                        Cursor.Current = Cursors.Hand;
                        m.Result = (IntPtr)1;   // 已处理：阻止 RichEdit 把光标改回 I 形
                        return;
                    }
                }
            }
            base.WndProc(ref m);
        }
    }

    internal class PreviewForm : Form
    {
        // 不抢前台：预览仅为悬停展示，激活会打断当前窗口的焦点
        protected override bool ShowWithoutActivation { get { return true; } }

        // 诞生即置顶：Windows 在 Show 时会清掉 WS_EX_TOPMOST 样式位，靠 tick 的
        // EnsureWinTopmost 补会有 ~50ms 排在普通窗口后面的"裸奔帧"（快速开关预览时
        // 看起来就是预览窗藏在 Explorer 后面）。直接写进 CreateParams，第一帧就置顶
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x8;   // WS_EX_TOPMOST
                return cp;
            }
        }

        // ── 配色：预览窗跟随列表配色（List* 调色板），视觉上和列表面板同一套 ──
        private static Color PvBg { get { return Config.ListBg; } }
        private static Color PvFg { get { return Config.ListFg; } }
        private static Color PvMu { get { return Config.ListMu; } }
        private static Color PvHv { get { return Config.ListHv; } }
        private static Color PvBd
        {
            get
            {
                // 低对比分隔线：背景与前景按 ~12% 混合，任何配色下都不会突兀
                var a = PvBg; var b = PvFg;
                return Color.FromArgb((a.R * 88 + b.R * 12) / 100,
                                      (a.G * 88 + b.G * 12) / 100,
                                      (a.B * 88 + b.B * 12) / 100);
            }
        }
        private static bool PvDark
        {
            get { var c = PvBg; return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128; }
        }
        internal static System.Drawing.Drawing2D.GraphicsPath RoundRectPath(int x, int y, int w, int h, int r)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            if (w <= 0 || h <= 0) return p;
            r = Math.Min(r, Math.Min(w, h) / 2);
            int d = 2 * r;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 信息区留白：左/上给阴影留够空间（顶部也要完整，不被信息区上边界切掉）
        private int AudioArtLeft { get { return Dpi.S(16); } }
        private int AudioArtTop { get { return Dpi.S(10); } }
        private int AudioArtSize { get { return Dpi.S(84); } }
        private int AudioTextX { get { return AudioArtLeft + AudioArtSize + Dpi.S(8); } }
        private int AudioNameY { get { return AudioArtTop + Dpi.S(2); } }

        // 音频封面统一绘制：阴影 + 抗锯齿圆角贴图 + 细描边。画在信息区背景层，
        // 文字/chip/倍速尺透明背景，阴影可以一直延伸到它们下面。
        private void PaintAudioCover(Graphics g2)
        {
            var cr = new Rectangle(AudioArtLeft, AudioArtTop, AudioArtSize, AudioArtSize);
            int rr = Dpi.S(10);
            g2.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color glow = _skin != null ? _skin.Glow : Color.FromArgb(90, 0, 0, 0);
            // 宽扩散 pad=16 / 下移 6：上扩 10、左扩 16，刚好落在留白内；下/右由透明文字层透出
            DrawCoverShadow(g2, cr, rr, glow, Dpi.S(16), Dpi.S(6), 1.6f);
            DrawCoverShadow(g2, cr, rr, glow, Dpi.S(6), Dpi.S(2), 1.2f);
            try
            {
                // 圆角必须用 FillPath 抗锯齿（SetClip 的剪切区是硬边，会出锯齿）
                using (var path = RoundRectPath(cr.X, cr.Y, cr.Width, cr.Height, rr))
                {
                    if (_audioCoverImg != null)
                    {
                        float sw = (float)cr.Width / _audioCoverImg.Width;
                        float sh = (float)cr.Height / _audioCoverImg.Height;
                        float sc = Math.Max(sw, sh);
                        int dw = Math.Max(cr.Width, (int)Math.Ceiling(_audioCoverImg.Width * sc));
                        int dh = Math.Max(cr.Height, (int)Math.Ceiling(_audioCoverImg.Height * sc));
                        using (var scaled = new Bitmap(dw, dh, PixelFormat.Format32bppPArgb))
                        {
                            using (var sg = Graphics.FromImage(scaled))
                            {
                                sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                sg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                                sg.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                                sg.DrawImage(_audioCoverImg, 0, 0, dw, dh);
                            }
                            using (var tb = new TextureBrush(scaled, System.Drawing.Drawing2D.WrapMode.Clamp))
                            {
                                tb.TranslateTransform(cr.X + (cr.Width - dw) / 2f, cr.Y + (cr.Height - dh) / 2f);
                                g2.FillPath(tb, path);
                            }
                        }
                    }
                    else
                    {
                        using (var b = new SolidBrush(APvNg)) g2.FillPath(b, path);
                    }
                }
                // 原型 box-shadow: 0 0 0 1px rgba(0,0,0,.06)
                using (var pen = new Pen(Color.FromArgb(16, 0, 0, 0), 1f))
                using (var ring = RoundRectPath(cr.X - 1, cr.Y - 1, cr.Width + 2, cr.Height + 2, rr + 1))
                    g2.DrawPath(pen, ring);
            }
            catch { }   // 封面异常时保留底色，别让绘制异常冒到消息循环
        }

        // 封面柔和阴影：径向渐变从中心 alpha 衰减到边缘 0（模拟 CSS box-shadow 的模糊扩散）
        private static void DrawCoverShadow(Graphics g, Rectangle cr, int rr, Color glow,
            int pad, int offY, float boost)
        {
            int alpha = Math.Min(255, (int)Math.Round(glow.A * boost));
            using (var path = RoundRectPath(cr.X - pad, cr.Y - pad + offY,
                                             cr.Width + 2 * pad, cr.Height + 2 * pad, rr + pad))
            using (var brush = new System.Drawing.Drawing2D.PathGradientBrush(path))
            {
                brush.CenterPoint = new PointF(cr.X + cr.Width / 2f, cr.Y + cr.Height / 2f + offY);
                brush.CenterColor = Color.FromArgb(alpha, glow);
                brush.SurroundColors = new[] { Color.FromArgb(0, glow.R, glow.G, glow.B) };
                g.FillPath(brush, path);
            }
        }
        // 拖到卡片：文字卡片预览要按 text 类型重新生成卡片，不能当文件路径处理
        public string PinKind = "file";
        public string PinTitle;
        // 桌面文字卡片（Kind=text）：编辑内容没有真实磁盘文件，保存时走回调把文本交还卡片，
        // 避免 SaveClick 误把「文本内容」当 FilePath 写磁盘。由 PinCardForm 在创建预览时设置。
        public Action<string> TextCardCommit;
        private Label _grip;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win11.Apply(Handle, PvDark, Config.Border);
            Win11.RemoveNcBorder(Handle);

        }
        private Image _img;
        private bool _pinned;
        private AudioBarPanel _toolbar;
        private Label _renameBtn, _openBtn;   // 左上角：重命名 / 用默认程序打开
        private Label _nameLbl;
        private Label _metaLbl;

        // Ctrl+拖头部 = 拖出文件（OLE FileDrop）；ExtDragActive 冻结拖放期间的预览窗关闭
        private bool _fileDragArmed;
        private Point _fileDragPt;
        internal static bool ExtDragActive;
        private Label _pinBtn;

        private Label _copyBtn;
        private Label _saveBtn;
        private bool _textCardMode;   // textcard 预览：没有真实文件，复制组只留"文本/选中"
        private Panel _scrollPanel;
        private Control _content;

        public string FilePath;
        public Action<string> OnFileSwitched;
        private Label _pinLaunchBtn;
        private double _zoom = 1.0;
        private WheelFilter _wheelFilter;
        private List<LinkRange> _inlineLinks;   // RTF 文本预览的链接区间缓存
        private NoteBlockPanel _notePanel;      // 链接备注的块渲染面板（文本块 + 图片块）
        private TextBox _sourceBox;             // 备注"源码"视图（原始 md）
        private Label _sourceToggle;            // 块视图 / 源码视图 切换
        // GIF 手动逐帧播放：绕开 PictureBox 内置动画（透明链每帧重绘 + 原图缩放导致很慢）
        private Timer _gifTimer;
        private int[] _gifDelays;         // 每帧延时(ms)，来自 PropertyItem 0x5100（单位 1/100 秒）
        private int _gifFrameCount;
        private Bitmap _gifFrameBuf;      // 复用的一帧缓冲（预缩放到显示尺寸）
        private PictureBox _gifBox;

        // ===== 音频预览专用（操作栏：播放键+全量波形；悬停展开信息区：标题/时间/meta）=====
        internal IMediaSession _audio;
        private Control _audioPlayBtn;                 // V4：圆形播放键（RoundPlayButton）
        private MediaPreview.SpeedRuler _audioRuler;   // 音频倍速刻度尺（展开时显示）
        private Label _audioSpdHint;    // 右上角速度提示浮层
        private Label _audioCloseBtn;    // 关闭预览窗
        private ImageSearchButton _coverSearchBtn;   // 悬停封面时出现的“以文件名搜图”按钮
        private DateTime _lastCoverSearchAt = DateTime.MinValue;
        private Label _audioTimeLbl;
        private Label _audioMetaLbl;
        private Label _audioNameLbl;
        private WaveView _audioViz;
        private SpectrumView _spectrumViz;
        private AudioProgress _audioProgress;
        private AudioMeter _audioMeter;
        private WaveDecoder _waveDec;                          // 全量波形（MF 静默解码）
        private readonly float[] _waveData = new float[720];   // 环回渐进包络（解码失败时回退）
        private string _wavePath;
        private Timer _audioUiTimer;
        private Panel _audioBody;
        private bool _audioExpanded;
        private static int AudioInfoH { get { return Dpi.S(74); } }
        private Label _audioHeadBar;
        // V5 封面取色换肤：封面图 + 由主色派生的窗口底色/强调色/淡色
        private Image _audioCoverImg;
        private Color _audioSkinBg = Color.Empty, _audioSkinAc = Color.Empty, _audioSkinMu = Color.Empty, _audioSkinFaint = Color.Empty;
        private bool _audioSkinned;
        // V4 整套换肤（封面 HSL → 明暗两套）
        private AudioArtwork.Skin _skin;
        private RoundPlayButton _audioRoundPlay;
        private AudioChip _chipFormat, _chipSize;
        private Label _audioTimeCur, _audioTimeTot;
        private static int HeadBarH { get { return Dpi.S(20); } }
        // 操作栏空白处点击=seek、拖动=移动窗口：按下记录起点，超过阈值才进入系统移动
        private bool _audioBarDown;
        private bool _audioBarMoved;
        private Point _audioBarStart;
        private int _audioBarDownX;
        private float _audioPhase;
        private bool _meterOn;

        private bool _textDirty;
        // 链接条目编辑：没有磁盘文件，保存时把文本回传给调用方
        private Action<string> _linkCommit;
        private Action<string> _linkAutoCommit;    // 自动保存：写回但不收起编辑窗
        private Timer _linkAutoSaveTimer;
        private Label _linkHint;                   // 链接预览窗底部常显提示
        private int _linkHintH;                    // 链接预览底部提示占用高度

        // ===== 文件夹播放模式（音频）：滚轮进入，滚动切歌；☰ 按钮展开音频列表+循环模式 =====
        private bool _folderMode;                  // 进入后持续到关窗；滚动=切歌
        private List<string> _folderTracks;        // 同目录音频文件，名称自然排序（默认排序）
        private string _folderDir;
        private int _folderIdx;
        private int _loopMode;                     // 0=顺序(播完即止) 1=列表循环 2=单曲循环

        private Panel _folderPanel;
        private Panel _folderHead;
        private FolderListBox _folderList;
        private Label _loopBtn;
        private Label _folderHeadLbl;
        private bool _folderListOn;

        private Timer _folderTipTimer;
        private static int FolderRowH { get { return Dpi.S(22); } }
        private const int FolderMaxRows = 8;

        private Timer _unpinCloseTimer;

        private Timer _sizeSaveTimer;
        // 自定义内容类型：video / audio / svg（决定用哪套尺寸记忆）
        private string _mediaKind = "svg";
        internal string MediaKind { get { return _mediaKind; } }
        // 复制按钮悬停弹出的菜单（每次新建，留引用才能在鼠标移开后收起）
        private ContextMenuStrip _copyMenu;

        private int _toolbarH { get { return Dpi.S(34); } }

        // ===== 信息 + 操作栏（标题栏下方）：类型/大小/行数/时间与常用复制操作 =====
        private static int InfoBarH { get { return Dpi.S(28); } }
        private Panel _infoBar;
        private Label _infoLbl;
        private Label _ibPrimary, _ibSecond, _ibThird, _ibMore, _ibSelected, _ibLocate;
        private float _ibMaxPt = -1f;   // 最近一次布局使用的固定字号（设定变化才重建字体）
        private string _ibFontName = "";  // 最近一次布局使用的字体族（设定变化才重建字体）
        private string _mediaSpec = "";      // 视频左上角规格角标文本，宽度够时并入信息栏
        private string _infoBaseText = "";   // 不含规格的基础信息文本
        // 区分预览窗的"挪位置"与"改尺寸"，避免挪动时把当前尺寸写进记忆
        private Size _pvSizeMoveStart;
        // 手动 resize：FormBorderStyle.None 下系统 HTBOTTOM sizing loop 会拖顶部，改手动保持 top/left 不变
        private bool _pvResize;
        private int _pvResizeMode, _pvResizeSX, _pvResizeSY, _pvResizeSW, _pvResizeSH, _pvResizeT0;
        // 双击标题打开文件夹的手动判定
        private int _pvHdrLastTick;
        private Point _pvHdrLastPos;

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
            // P1：内容区 12px 留白 + 6% 棋盘格底（透明 PNG 能看出边界）
            _scrollPanel.Padding = new Padding(12);
            var host = new CheckerPanel { BackColor = PvBg, ForeColor = PvFg, Dock = DockStyle.Fill };
            var pb = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = Color.Transparent };
            _content = pb;
            host.Controls.Add(pb);
            _scrollPanel.Controls.Add(host);
            BindResize(host);
            ClientSize = Dpi.Sz(img.Width + 24, img.Height + 24);
            ApplyImageSize(img.Width + 24, img.Height + 24);

        }

        public PreviewForm(Image img, int displayW, int displayH)
            : this()
        {
            _img = img;
            _scrollPanel.Padding = new Padding(12);
            var host = new Panel { BackColor = PvBg, Dock = DockStyle.Fill };
            // 不透明背景 + 手动逐帧：避免 PictureBox 内置动画在透明链里每帧触发父容器重绘、并反复
            // 对原图做 Zoom 缩放（GIF 慢的根因）。帧缓冲预缩放到显示尺寸，播放只换像素。
            _gifFrameBuf = new Bitmap(Math.Max(1, Dpi.S(displayW)), Math.Max(1, Dpi.S(displayH)), PixelFormat.Format32bppArgb);
            var pb = new PictureBox { Image = _gifFrameBuf, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = PvBg };
            _gifBox = pb;
            _content = pb;
            host.Controls.Add(pb);
            _scrollPanel.Controls.Add(host);
            BindResize(host);
            ClientSize = Dpi.Sz(displayW, displayH);
            ApplyImageSize(displayW, displayH);

            // 帧数 / 每帧延时（GIF 标准：延时存于 PropertyItem 0x5100，单位 1/100 秒）
            try
            {
                var dim = new FrameDimension(img.FrameDimensionsList[0]);
                _gifFrameCount = img.GetFrameCount(dim);
                var prop = img.GetPropertyItem(0x5100);
                if (prop != null && prop.Value != null)
                {
                    int n = Math.Min(_gifFrameCount, prop.Value.Length / 4);
                    _gifDelays = new int[_gifFrameCount];
                    for (int i = 0; i < _gifFrameCount; i++)
                        _gifDelays[i] = i < n ? Math.Max(2, BitConverter.ToInt32(prop.Value, i * 4)) * 10 : 100;
                }
            }
            catch { _gifFrameCount = 1; }
            if (_gifFrameCount <= 1) { _gifTimer = null; return; }
            if (_gifDelays == null) { _gifDelays = new int[_gifFrameCount]; for (int i = 0; i < _gifFrameCount; i++) _gifDelays[i] = 100; }

            var gifDim = new FrameDimension(img.FrameDimensionsList[0]);
            int frame = 0;
            _gifTimer = new Timer { Interval = _gifDelays[0] };
            _gifTimer.Tick += (s, e) =>
            {
                if (IsDisposed || pb.IsDisposed || _img == null) { StopGif(); return; }
                try
                {
                    _img.SelectActiveFrame(gifDim, frame);
                    using (var g = Graphics.FromImage(_gifFrameBuf))
                    {
                        g.Clear(PvBg);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(_img, new Rectangle(0, 0, _gifFrameBuf.Width, _gifFrameBuf.Height));
                    }
                    pb.Invalidate();
                }
                catch { }
                frame = (frame + 1) % _gifFrameCount;
                _gifTimer.Interval = Math.Max(20, _gifDelays[frame]);
            };
            // 首帧立即绘制，随后启动播放
            try { _img.SelectActiveFrame(gifDim, 0); using (var g = Graphics.FromImage(_gifFrameBuf)) { g.Clear(PvBg); g.DrawImage(_img, new Rectangle(0, 0, _gifFrameBuf.Width, _gifFrameBuf.Height)); } } catch { }
            _gifTimer.Start();
        }

        private void StopGif()
        {
            if (_gifTimer != null) { _gifTimer.Stop(); _gifTimer.Dispose(); _gifTimer = null; }
            if (_gifFrameBuf != null) { if (_gifBox != null) _gifBox.Image = null; _gifFrameBuf.Dispose(); _gifFrameBuf = null; }
        }

        public PreviewForm(string text, long size)
            : this()
        {
            // 文本里出现颜色值（#RGB/#RRGGBB、rgb()/rgba()）或 md 图片引用时，走 RTF 内联渲染
            var colorTokens = ExtractColorTokens(text);
            var imageMarks = FindImageMarks(text);
            // 用 RichTextBox：DetectUrls 让链接自动呈现为蓝色下划线并只响应链接点击，
            // 不会出现 TextBox 里"整块窗口都算点在链接上"的命中问题。
            // 历史上 RichTextBox 构造函数在个别系统字体状态下会抛 ArgumentException，
            // 这里保留 TextBox 兜底（此时只是没有可点击链接，功能不中断）
            TextBoxBase tb;
            try
            {
                var rtb = new LinkRichTextBox
                {
                    DetectUrls = false,   // 统一用自定义命中：RichEdit 内置 URL 光标会不稳定地跳动
                    Font = new Font("Microsoft YaHei", 9),
                    BackColor = PvBg,
                    ForeColor = PvFg,
                    BorderStyle = BorderStyle.None,
                    ReadOnly = false,
                    ScrollBars = RichTextBoxScrollBars.Vertical,
                    WordWrap = true,
                    Dock = DockStyle.Fill,
                };
                tb = rtb;
            }
            catch
            {
                tb = new TextBox
                {
                    Multiline = true,
                    Font = new Font("Microsoft YaHei", 9),
                    BackColor = PvBg,
                    ForeColor = PvFg,
                    BorderStyle = BorderStyle.None,
                    ReadOnly = false,
                    ScrollBars = ScrollBars.Vertical,
                    WordWrap = true,
                    Dock = DockStyle.Fill,
                };
            }
            tb.Text = text;
            tb.SelectionStart = 0;
            tb.SelectionLength = 0;
            // 有颜色值时改用 RTF：色块作为内联小图直接画在原文颜色值旁边（不再单独占色块带）。
            // 链接在两种情况下都走同一套自定义命中/光标逻辑：DetectUrls 已全局关闭，
            // RichEdit 内置 URL 光标不稳定会跳动。关键：链接区间必须基于"渲染后"的 rtb.Text
            // 计算——RTF 里 \pict 色块会占位，原始 text 的索引与 rtb.Text 不一致会导致命中错位。
            var rtbInline = tb as RichTextBox;
            bool inlineColors = rtbInline != null && (colorTokens.Count > 0 || imageMarks.Count > 0);
            if (inlineColors)
            {
                try { rtbInline.Rtf = BuildInlineSwatchRtf(text, colorTokens, imageMarks); }
                catch { rtbInline.Text = text; inlineColors = false; }
            }
            if (rtbInline != null)
            {
                if (inlineColors)
                    StyleTextLinks(rtbInline, rtbInline.Text);   // 在渲染后的文本上色（索引对齐）
                else
                    StyleTextLinks(rtbInline, text);
                // 链接区间取自渲染后的真实文本，保证与 GetCharIndexFromPosition 索引一致
                _inlineLinks = FindLinkRanges(rtbInline.Text);
                rtbInline.SelectionStart = 0;
                rtbInline.SelectionLength = 0;
                rtbInline.MouseUp += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left || rtbInline.SelectionLength > 0) return;
                    string url = LinkAtInline(rtbInline, e.Location);
                    if (url == null) return;
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                };
                // 光标交给 LinkRichTextBox 的 WM_SETCURSOR 处理：RichEdit 会在 MouseMove 之后
                // 再次把光标改回 I 形，导致这里设的 Hand 被覆盖、形态反复跳动。
                var linkBox = rtbInline as LinkRichTextBox;
                if (linkBox != null)
                {
                    linkBox.IsOverLink = (p) => LinkAtInline(linkBox, p) != null;
                    // Ctrl+V 剪贴板图片：落盘为附件并插入 md 引用 ![](link_assets/xxx.png)
                    linkBox.PasteImage = delegate(Image img)
                    {
                        try
                        {
                            string rel = LinkAssets.SaveImage(img);
                            return "![](" + rel + ")";
                        }
                        catch { return null; }
                    };
                    linkBox.AfterImagePaste = delegate
                    {
                        // 插入后重渲染：把新 ![](...) 变成内联缩略图，并同步链接/图片区间
                        string plain = StripSwatchSpaces(linkBox.Text);
                        var cts = ExtractColorTokens(plain);
                        var ims = FindImageMarks(plain);
                        try
                        {
                            linkBox.Rtf = BuildInlineSwatchRtf(plain, cts, ims);
                            _inlineLinks = FindLinkRanges(linkBox.Text);
                        }
                        catch { }
                    };
                }
            }
            _content = tb;
            _mediaKind = "text";   // 独立一组尺寸记忆，避免落到 SVG 的 PreviewMediaW/H
            _scrollPanel.Padding = new Padding(10, 6, 6, 6);   // P0：文本内容留白，避免贴边
            _scrollPanel.Controls.Add(tb);
            BindResize(tb);
            Win11.ApplyScrollTheme(tb, PvDark);
            tb.TextChanged += (s, e) =>
            {
                if (!_textDirty) { _textDirty = true; _saveBtn.Visible = true; UpdateToolbarBtnPos(); }
            };
            // 选区变化时，操作栏即时插入/移除「选中」复制按钮
            tb.MouseUp += (s, e) => RefreshSelectionButtons();
            tb.KeyUp += (s, e) => RefreshSelectionButtons();

            using (var g = CreateGraphics())
            {
                var sz = g.MeasureString("W", tb.Font);
                int lineH = (int)sz.Height + 1;
                int initH = lineH * Config.TextPreviewLines + Dpi.S(4);
                int maxW = 0;
                foreach (var line in text.Split(new[] { "\r\n" }, StringSplitOptions.None))
                {
                    var w = g.MeasureString(line, tb.Font);
                    if (w.Width > maxW) maxW = (int)w.Width;
                }
                int chrome = _toolbarH + (Config.PreviewInfoBar ? InfoBarH : 0);
                int contentH = Math.Min(initH, Dpi.S(320)) + Dpi.S(12);   // +12：内容上下留白
                if (Config.PreviewTextW > 0 && Config.PreviewTextH > 0)
                {
                    // 用户手动调整过尺寸：和其它内容类型一样优先套用记忆值（含工具栏/信息栏开销）
                    var wa = Screen.GetWorkingArea(this);
                    ClientSize = new Size(Math.Max(Dpi.S(240), Math.Min(Dpi.S(Config.PreviewTextW), wa.Width - Dpi.S(20))),
                                          Math.Max(Dpi.S(120) + chrome, Math.Min(Dpi.S(Config.PreviewTextH), wa.Height - Dpi.S(20))));
                    Program.DebugLog("[pv-size] text-remembered " + ClientSize.Width + "x" + ClientSize.Height);
                }
                else ClientSize = new Size(Math.Max(Math.Min(maxW + Dpi.S(36), Dpi.S(480)), Dpi.S(240)), contentH + chrome);
            }

        }

        // 链接备注编辑：块渲染模式（文本块 + 图片块），不再显示 ![](path) 语法文本。
        // 首行 [标题](url) 仍作为可编辑文本块，换行后的 md 正文解析成块；保存时序列化回 md。
        public PreviewForm(string text, long size, Action<string> onCommit, Action<string> onAutoCommit = null)
            : this()
        {
            _linkCommit = onCommit;
            _linkAutoCommit = onAutoCommit;
            _textCardMode = true;
            _mediaKind = "text";
            // 链接模式下：编辑/打开/复制/固定为卡片对这些按钮都无真实文件可作用，统一隐藏；
            // 只保留「源码」切换、保存（编辑后出现）与右下角固定窗口。
            HideCopyForTextCard();
            _pinLaunchBtn.Visible = false;
            _scrollPanel.Padding = new Padding(Dpi.S(14), Dpi.S(14), Dpi.S(14), Dpi.S(10));

            // 底部常显提示：支持拖拽浏览器图片到此窗口
            _linkHintH = Dpi.S(24);
            _linkHint = new Label
            {
                Text = "支持拖拽浏览器图片到此窗口",
                ForeColor = PvMu,
                BackColor = PvBg,
                Font = new Font(Config.FontName, 9f),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            Controls.Add(_linkHint);

            // 自动保存：编辑停止约 0.8 秒后写回，保持编辑窗打开（不调用 onCommit 的收起逻辑）
            _linkAutoSaveTimer = new Timer { Interval = 800 };
            _linkAutoSaveTimer.Tick += delegate
            {
                _linkAutoSaveTimer.Stop();
                if (_linkAutoCommit == null || !_textDirty || IsDisposed) return;
                try
                {
                    string md = _content == _sourceBox ? _sourceBox.Text : _notePanel.GetMarkdown();
                    _linkAutoCommit(md.Trim());
                    _textDirty = false;
                    if (_saveBtn != null) _saveBtn.Visible = false;
                    UpdateToolbarBtnPos();
                }
                catch { }
            };
            Action markDirty = delegate
            {
                if (!_textDirty) { _textDirty = true; _saveBtn.Visible = true; UpdateToolbarBtnPos(); }
                if (_linkAutoSaveTimer != null) { _linkAutoSaveTimer.Stop(); _linkAutoSaveTimer.Start(); }
            };

            var font = new Font(Config.FontName, 9f);
            _notePanel = new NoteBlockPanel(font, 0, Dpi.S(220), markDirty);
            // 关键：与文本预览的 RichTextBox 一样填满滚动面板，否则只保持默认窄尺寸，
            // 文本块/图片块全挤在左上角一小块，右侧大片空白。
            _notePanel.Dock = DockStyle.Fill;
            _notePanel.Load(text);
            _content = _notePanel;
            _scrollPanel.Controls.Add(_notePanel);
            BindResize(_notePanel);
            Win11.ApplyScrollTheme(_notePanel, PvDark);

            // 源码视图：两个视图都 Dock=Fill，靠 Visible 切换（不需要重建尺寸）
            _sourceBox = new TextBox
            {
                Multiline = true,
                Font = new Font("Consolas", Dpi.S(9f)),
                BorderStyle = BorderStyle.None,
                BackColor = PvBg,
                ForeColor = PvFg,
                WordWrap = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Visible = false,
            };
            _sourceBox.Text = text;
            _sourceBox.TextChanged += delegate
            {
                if (!_textDirty) { _textDirty = true; _saveBtn.Visible = true; UpdateToolbarBtnPos(); }
                if (_linkAutoSaveTimer != null) { _linkAutoSaveTimer.Stop(); _linkAutoSaveTimer.Start(); }
            };
            _scrollPanel.Controls.Add(_sourceBox);
            BindResize(_sourceBox);

            // 工具栏"源码"切换按钮
            _sourceToggle = new Label
            {
                Text = "源码",
                AutoSize = true,
                ForeColor = PvMu,
                Cursor = Cursors.Hand,
            };
            bool sourceMode = false;
            _sourceToggle.Click += delegate(object s, EventArgs e)
            {
                if (sourceMode)
                {
                    _notePanel.Load(_sourceBox.Text);
                    _notePanel.Visible = true;
                    _sourceBox.Visible = false;
                    _content = _notePanel;
                    _sourceToggle.Text = "源码";
                }
                else
                {
                    _sourceBox.Text = _notePanel.GetMarkdown();
                    _notePanel.Visible = false;
                    _sourceBox.Visible = true;
                    _content = _sourceBox;
                    _sourceToggle.Text = "块视图";
                }
                sourceMode = !sourceMode;
                // 两个视图构造时都已 BindResize，且都 Dock=Fill；切换 Visible 即可自动重排，
                // 这里不再重复绑定鼠标事件（否则缩放手柄消息会被多次触发）。
            };
            _toolbar.Controls.Add(_sourceToggle);

            using (var g = CreateGraphics())
            {
                var sz = g.MeasureString("W", font);
                int lineH = (int)sz.Height + 1;
                int chrome = _toolbarH + (Config.PreviewInfoBar ? InfoBarH : 0);
                var wa = Screen.GetWorkingArea(this);
                // 链接备注默认尺寸贴近原型（648×560），仍受用户记忆值与屏幕尺寸约束
                int w = Config.PreviewTextW > 0 ? Math.Max(Dpi.S(300), Math.Min(Dpi.S(Config.PreviewTextW), wa.Width - Dpi.S(20)))
                                                : Dpi.S(648);
                int h = Config.PreviewTextH > 0 ? Math.Max(Dpi.S(180) + chrome, Math.Min(Dpi.S(Config.PreviewTextH), wa.Height - Dpi.S(20)))
                                                : Math.Min(Dpi.S(560), Dpi.S(420) + chrome);
                ClientSize = new Size(Math.Min(w, wa.Width - Dpi.S(20)), Math.Min(h, wa.Height - Dpi.S(20)));
            }
        }

        // 链接编辑：把键盘焦点交给标题编辑框（地址栏拖入的链接标题是 URL，改名最直接）
        public void FocusContent()
        {
            Activate();
            if (_notePanel != null && _content == _notePanel)
            {
                _notePanel.FocusTitle();
                return;
            }
            var tb = _content as TextBoxBase;
            if (tb == null) return;
            tb.Focus();
            tb.SelectAll();
        }

        // 文本中的颜色值 → 色块条。支持 #RGB / #RRGGBB / #RRGGBBAA（忽略 alpha）、
        // rgb(r,g,b) / rgba(r,g,b,a)，按颜色去重，最多 24 个
        private static List<KeyValuePair<string, Color>> ExtractColorTokens(string text)
        {
            var list = new List<KeyValuePair<string, Color>>();
            if (string.IsNullOrEmpty(text)) return list;
            var seen = new HashSet<int>();
            for (int i = 0; i < text.Length && list.Count < 24; i++)
            {
                char c = text[i];
                if (c == '#')
                {
                    int j = i + 1, n = 0;
                    while (j < text.Length && Uri.IsHexDigit(text[j]) && n < 8) { j++; n++; }
                    if ((n == 3 || n == 6 || n == 8) && (j >= text.Length || !char.IsLetterOrDigit(text[j])))
                    {
                        string tok = text.Substring(i, n + 1);
                        Color col;
                        if (TryHexColor(tok, out col)) AddColor(list, seen, tok, col);
                        i = j - 1;
                    }
                    continue;
                }
                if ((c == 'r' || c == 'R') && IsBoundary(text, i - 1))
                {
                    int end;
                    string tok;
                    Color col;
                    if (TryRgbToken(text, i, out tok, out end, out col))
                    {
                        AddColor(list, seen, tok, col);
                        i = end;
                    }
                }
            }
            return list;
        }

        private static void AddColor(List<KeyValuePair<string, Color>> list, HashSet<int> seen, string tok, Color col)
        {
            if (!seen.Add(col.ToArgb())) return;
            list.Add(new KeyValuePair<string, Color>(tok, col));
        }

        private static bool IsBoundary(string s, int idx)
        {
            if (idx < 0) return true;
            return !char.IsLetterOrDigit(s[idx]) && s[idx] != '_';
        }

        private static bool TryHexColor(string tok, out Color col)
        {
            col = Color.Empty;
            string h = tok.Substring(1);
            try
            {
                if (h.Length == 3)
                    h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
                else if (h.Length == 8)
                    h = h.Substring(0, 6);   // #RRGGBBAA：色块只体现 RGB
                if (h.Length != 6) return false;
                int v = Convert.ToInt32(h, 16);
                col = Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
                return true;
            }
            catch { return false; }
        }

        private static bool TryRgbToken(string text, int start, out string tok, out int end, out Color col)
        {
            tok = null; end = start; col = Color.Empty;
            bool alpha = false;
            if (start + 4 > text.Length) return false;
            string head = text.Substring(start, 4).ToLowerInvariant();
            if (head == "rgba") alpha = true;
            else if (head != "rgb(") return false;   // "rgb(" 正好 4 个字符

            int open = alpha ? start + 4 : start + 3;
            if (open >= text.Length || text[open] != '(') return false;
            int close = text.IndexOf(')', open + 1);
            if (close < 0 || close - open > 32) return false;

            string body = text.Substring(open + 1, close - open - 1);
            string[] parts = body.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            int[] rgb = new int[3];
            for (int k = 0; k < 3; k++)
            {
                int v;
                if (!int.TryParse(parts[k].Trim().TrimEnd('%'), out v)) return false;
                if (v < 0) v = 0; if (v > 255) v = 255;
                rgb[k] = v;
            }
            col = Color.FromArgb(rgb[0], rgb[1], rgb[2]);
            tok = text.Substring(start, close - start + 1);
            end = close;
            return true;
        }

        private struct SwatchMark { public int Start; public int Len; public Color Color; }
        private struct LinkRange { public int Start; public int End; public string Url; }

        // 同一颜色值的所有出现位置（总量上限 40，避免大文件里满屏图片）。
        // 生成 RTF 与保存时剔除图片占位空格共用这一份规则，保证两边一致
        private static List<SwatchMark> FindSwatchMarks(string text, List<KeyValuePair<string, Color>> tokens)
        {
            var marks = new List<SwatchMark>();
            foreach (var kv in tokens)
            {
                int from = 0;
                while (marks.Count < 40)
                {
                    int idx = text.IndexOf(kv.Key, from, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) break;
                    marks.Add(new SwatchMark { Start = idx, Len = kv.Key.Length, Color = kv.Value });
                    from = idx + kv.Key.Length;
                }
            }
            marks.Sort((a, b) => a.Start.CompareTo(b.Start));
            return marks;
        }

        // md 图片引用 ![](path)：Start/Len 指向整段语法，Path 是引用地址
        private struct ImageMark
        {
            public int Start;
            public int Len;
            public string Path;
        }

        private static List<ImageMark> FindImageMarks(string text)
        {
            var list = new List<ImageMark>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (Match m in Regex.Matches(text, @"!\[[^\]]*\]\(([^)]+)\)"))
                list.Add(new ImageMark { Start = m.Index, Len = m.Length, Path = m.Groups[1].Value.Trim() });
            return list;
        }

        // 把文本转成 RTF：颜色值后面内联 14px 色块、![](path) 后面内联缩略图（均为 \pict），
        // 链接用蓝色下划线。md 图片语法本身保留在纯文本里，保存不丢引用。
        private static string BuildInlineSwatchRtf(string text, List<KeyValuePair<string, Color>> tokens, List<ImageMark> images)
        {
            var marks = FindSwatchMarks(text, tokens);

            var links = FindLinkRanges(text);
            var markAt = new Dictionary<int, SwatchMark>();
            foreach (var m in marks) if (!markAt.ContainsKey(m.Start)) markAt[m.Start] = m;
            var imgAt = new Dictionary<int, ImageMark>();
            if (images != null) foreach (var im in images) if (!imgAt.ContainsKey(im.Start)) imgAt[im.Start] = im;
            var linkAt = new Dictionary<int, LinkRange>();
            foreach (var l in links) if (!linkAt.ContainsKey(l.Start)) linkAt[l.Start] = l;

            var sb = new StringBuilder(text.Length * 2 + 2048);
            Color fg = Config.ListFg;
            sb.Append(@"{\rtf1\ansi\ansicpg936\deff0{\fonttbl{\f0\fnil\fcharset134 Microsoft YaHei;}}{\colortbl ;\red0\green102\blue204;\red130\green135\blue140;\red" + fg.R + @"\green" + fg.G + @"\blue" + fg.B + @";}\f0\fs18\cf3 ");
            int i = 0;
            while (i < text.Length)
            {
                LinkRange link;
                if (linkAt.TryGetValue(i, out link))
                {
                    // 用纯样式文本（蓝+下划线）而非 RTF HYPERLINK 域：域在部分 RichEdit
                    // 版本会把显示文本包成 <url>（记事本里没有），且光标命中不稳；
                    // 链接命中/点击已由 LinkAtInline + MouseUp 接管，不需要 RTF 域。
                    sb.Append(@"{\cf1\ul ");
                    AppendRtfEscaped(sb, text, i, link.End - i);
                    sb.Append("}");
                    i = link.End;
                    continue;
                }
                ImageMark img;
                if (imgAt.TryGetValue(i, out img))
                {
                    // 保留 ![](path) 原文（灰显），后面内联缩略图；占位空格在保存时剔除
                    sb.Append(@"{\cf2 ");
                    AppendRtfEscaped(sb, text, i, img.Len);
                    sb.Append("}");
                    sb.Append(ImagePict(img.Path));
                    i += img.Len;
                    continue;
                }
                SwatchMark mark;
                if (markAt.TryGetValue(i, out mark))
                {
                    AppendRtfEscaped(sb, text, i, mark.Len);
                    // 色块图片本身在纯文本里占一个空格位（保存时由 StripSwatchSpaces 剔除），
                    // 因此这里不再额外补空格，保证"打开→编辑→保存"文本不漂移
                    sb.Append(SwatchPict(mark.Color));
                    i += mark.Len;
                    continue;
                }
                AppendRtfEscaped(sb, text, i, 1);
                i++;
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendRtfEscaped(StringBuilder sb, string text, int start, int len)
        {
            for (int i = start; i < start + len && i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;
                if (c == '\n') { sb.Append("\\par "); continue; }
                if (c == '\t') { sb.Append("\\tab "); continue; }
                if (c == '\\' || c == '{' || c == '}') { sb.Append('\\'); sb.Append(c); continue; }
                if (c >= 32 && c < 127) { sb.Append(c); continue; }
                // RTF 的 \uN 是有符号 16 位：码位 > 32767 必须写负值，否则解析越界会丢内容
                int code = c;
                if (code > 32767) code -= 65536;
                sb.Append("\\u").Append(code).Append('?');
            }
        }

        private static string EscapeRtfToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '\\' || c == '{' || c == '}') { sb.Append('\\'); sb.Append(c); }
                else if (c == '"') sb.Append("\\\"");
                else if (c >= 32 && c < 127) sb.Append(c);
                else sb.Append(' ');
            }
            return sb.ToString();
        }

        // 色块图片。必须用 DIB（\dibitmap0）而不是 PNG（\pngblip）：
        // WinForms 的 RichEdit 2.0 接口会静默丢弃 pngblip 组，连带丢掉其后的部分内容，
        // 表现为色块看不到、保存后重开预览内容缺失。DIB 各系统都稳定支持。
        private static string SwatchPict(Color c)
        {
            const int W = 14, H = 14;
            int stride = ((W * 3 + 3) / 4) * 4;
            var data = new byte[40 + stride * H];   // BITMAPINFOHEADER + 24bpp 像素（bottom-up）
            using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(c);
                    using (var pen = new Pen(Color.FromArgb(90, 0, 0, 0)))
                        g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
                }
                BitConverter.GetBytes(40).CopyTo(data, 0);
                BitConverter.GetBytes(W).CopyTo(data, 4);
                BitConverter.GetBytes(H).CopyTo(data, 8);
                BitConverter.GetBytes((short)1).CopyTo(data, 12);
                BitConverter.GetBytes((short)24).CopyTo(data, 14);
                BitConverter.GetBytes(0).CopyTo(data, 16);            // BI_RGB
                BitConverter.GetBytes(stride * H).CopyTo(data, 20);
                var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[bd.Stride];
                    for (int y = 0; y < H; y++)
                    {
                        Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, Math.Min(bd.Stride, stride));
                        Buffer.BlockCopy(row, 0, data, 40 + (H - 1 - y) * stride, stride);
                    }
                }
                finally { bmp.UnlockBits(bd); }
            }
            var hx = new StringBuilder(data.Length * 2);
            foreach (byte b in data) hx.Append(b.ToString("x2"));
            return @"{\pict\dibitmap0\picw14\pich14\picwgoal210\pichgoal210 " + hx + "}";
        }

        // md 图片引用的内联缩略图：从磁盘加载并等比缩到展示尺寸，输出 24bpp DIB \pict
        // （RichEdit 2.0 不支持 pngblip，必须 DIB）。图片不存在/损坏时返回空串（只显示原文）。
        private static string ImagePict(string path)
        {
            try
            {
                string full = LinkAssets.Resolve(path);
                if (!File.Exists(full)) return "";
                using (var src = Image.FromFile(full))
                {
                    const int MaxW = 220, MaxH = 120;
                    double sc = Math.Min(1.0, Math.Min((double)MaxW / src.Width, (double)MaxH / src.Height));
                    int W = Math.Max(1, (int)(src.Width * sc)), H = Math.Max(1, (int)(src.Height * sc));
                    int stride = ((W * 3 + 3) / 4) * 4;
                    var data = new byte[40 + stride * H];
                    using (var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.White);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, new Rectangle(0, 0, W, H));
                        }
                        BitConverter.GetBytes(40).CopyTo(data, 0);
                        BitConverter.GetBytes(W).CopyTo(data, 4);
                        BitConverter.GetBytes(H).CopyTo(data, 8);
                        BitConverter.GetBytes((short)1).CopyTo(data, 12);
                        BitConverter.GetBytes((short)24).CopyTo(data, 14);
                        BitConverter.GetBytes(0).CopyTo(data, 16);
                        BitConverter.GetBytes(stride * H).CopyTo(data, 20);
                        var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                        try
                        {
                            var row = new byte[bd.Stride];
                            for (int y = 0; y < H; y++)
                            {
                                Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), row, 0, Math.Min(bd.Stride, stride));
                                Buffer.BlockCopy(row, 0, data, 40 + (H - 1 - y) * stride, stride);
                            }
                        }
                        finally { bmp.UnlockBits(bd); }
                    }
                    var hx = new StringBuilder(data.Length * 2);
                    foreach (byte b in data) hx.Append(b.ToString("x2"));
                    int goalW = W * 15, goalH = H * 15;   // px -> twips
                    return @"{\pict\dibitmap0\picw" + W + @"\pich" + H + @"\picwgoal" + goalW + @"\pichgoal" + goalH + " " + hx + "}";
                }
            }
            catch { return ""; }
        }

        // 保存前去掉色块/图片图片在纯文本中占位的那一个空格
        private static string StripSwatchSpaces(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // 收集所有内联图片占位的区间尾：颜色值色块 + md 图片缩略图
            var ends = new List<int>();
            var tokens = ExtractColorTokens(text);
            foreach (var m in FindSwatchMarks(text, tokens)) ends.Add(m.Start + m.Len);
            foreach (var im in FindImageMarks(text)) ends.Add(im.Start + im.Len);
            if (ends.Count == 0) return text;
            ends.Sort();
            var sb = new StringBuilder(text.Length);
            int mi = 0;
            for (int i = 0; i < text.Length; i++)
            {
                sb.Append(text[i]);
                if (mi < ends.Count && i + 1 == ends[mi])
                {
                    mi++;
                    // \pict 在纯文本里通常占一个空格；也可能被解析成对象替换符
                    if (i + 1 < text.Length && (text[i + 1] == ' ' || text[i + 1] == '\uFFFC')) i++;
                }
            }
            return sb.ToString();
        }

        // 与 StyleTextLinks 相同口径：按空白切词，去掉尾部标点后判断是否链接
        private static List<LinkRange> FindLinkRanges(string text)
        {
            var list = new List<LinkRange>();
            int i = 0;
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                int s = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                if (i == s) break;
                string raw = text.Substring(s, i - s);
                string tok = raw.Trim('(', ')', '[', ']', '{', '}', '<', '>', '"', '\'',
                                      ',', '.', ';', ':', '!', '?',
                                      '，', '。', '；', '：', '、', '！', '？', '“', '”', '‘', '’', '《', '》', '「', '」');
                bool isLink = tok.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || tok.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || tok.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
                if (!isLink) continue;
                int off = raw.IndexOf(tok, StringComparison.Ordinal);
                if (off < 0) continue;
                string url = tok.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + tok : tok;
                list.Add(new LinkRange { Start = s + off, End = s + off + tok.Length, Url = url });
            }
            return list;
        }

        // RTF 模式下 DetectUrls 不可用：单击时按缓存的链接区间判断是否点在链接上。
        // 用 RichEdit 自己的 GetCharIndexFromPosition/GetPositionFromCharIndex 判定，
        // 不再混用 GDI MeasureText（与 RichEdit 布局宽度不一致会在边界反复翻转→手型闪烁）。
        private string LinkAtInline(RichTextBox rtb, Point pt)
        {
            try
            {
                var links = _inlineLinks;
                if (links == null || links.Count == 0) return null;
                string t = rtb.Text;
                int idx = rtb.GetCharIndexFromPosition(pt);
                if (idx < 0 || idx >= t.Length) return null;
                Point pc = rtb.GetPositionFromCharIndex(idx);
                int lh = rtb.Font.Height + 3;
                if (pt.Y < pc.Y - 1 || pt.Y > pc.Y + lh) return null;
                for (int n = 0; n < links.Count; n++)
                {
                    var l = links[n];
                    if (idx < l.Start || idx >= l.End) continue;
                    // 字符索引落在链接区间内即可：RichEdit 的吸附本身稳定，不再用字符宽度二次裁剪
                    return l.Url;
                }
            }
            catch { }
            return null;
        }

        // 把文本中的链接标成蓝色+下划线（DetectUrls 负责点击命中，这里补视觉样式）。
        // 只按空白切词，尾部中英文标点不算链接的一部分
        private static void StyleTextLinks(RichTextBox rtb, string text)
        {
            try
            {
                int i = 0;
                while (i < text.Length)
                {
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    int s = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                    if (i == s) break;
                    string raw = text.Substring(s, i - s);
                    string tok = raw.Trim('(', ')', '[', ']', '{', '}', '<', '>', '"', '\'',
                                          ',', '.', ';', ':', '!', '?',
                                          '，', '。', '；', '：', '、', '！', '？', '“', '”', '‘', '’', '《', '》', '「', '」');
                    bool isLink = tok.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || tok.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || tok.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
                    if (!isLink) continue;
                    int off = raw.IndexOf(tok, StringComparison.Ordinal);
                    if (off < 0) continue;
                    rtb.Select(s + off, tok.Length);
                    rtb.SelectionColor = Color.FromArgb(0, 102, 204);
                    rtb.SelectionFont = new Font(rtb.Font, FontStyle.Underline);
                }
                rtb.Select(0, 0);
                rtb.SelectionColor = PvFg;
                rtb.SelectionFont = rtb.Font;
            }
            catch { }
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
        // V5 换肤下的实例配色：底色/次要色跟随封面主色，前景文字仍用方案色保证可读。
        // V4 整套换肤：有皮肤时全用封面派生色；无皮肤时退回当前方案色（明暗已体现在方案里）。
        private Color APvBg { get { return _skin != null ? _skin.Bg : (_audioSkinned ? _audioSkinBg : PvBg); } }
        private Color APvNg { get { return _skin != null ? _skin.Bg2 : PvBg; } }
        private Color APvFg { get { return _skin != null ? _skin.Fg : PvFg; } }
        private Color APvF2 { get { return _skin != null ? _skin.Fg2 : PvFg; } }
        private Color APvMu { get { return _skin != null ? Color.FromArgb(220, _skin.Fg2) : (_audioSkinned ? _audioSkinMu : PvMu); } }
        private Color APvAc { get { return _skin != null ? _skin.Accent : (_audioSkinned ? _audioSkinAc : Config.ListAc); } }

        // V5 封面取色换肤：只有真实封面（内嵌/同目录）才染底色与强调色；占位图不换肤，
        // 避免把浅色占位主色误当强调色导致文字看不清。文字色始终用方案色保证可读。
        // 切歌时复用同一方法，保证封面、底色、强调色随曲目一起更新。
        private void ReloadAudioArtwork(string path)
        {
            if (_audioCoverImg != null) { _audioCoverImg.Dispose(); _audioCoverImg = null; }
            _audioSkinned = false;
            _skin = null;
            _audioSkinBg = Color.Empty; _audioSkinAc = Color.Empty;
            _audioSkinMu = PvMu; _audioSkinFaint = PvBd;
            if (string.IsNullOrEmpty(path)) { ApplyAudioSkinToControls(); return; }
            try
            {
                AudioArtwork.Info art = AudioArtwork.Load(path);
                _audioCoverImg = art.Cover;
                if (_audioBody != null) _audioBody.Invalidate(true);   // 封面画在背景层：换图后连子控件一起重绘
                if (art.HasCover && art.Dominant != Color.Empty)
                {
                    // V4：按封面 HSL 派生整套配色，明暗跟随当前主题配置（不自动判断）。
                    _skin = AudioArtwork.Palette(_audioCoverImg, Config.IsDark);
                    _audioSkinBg = _skin.Bg;
                    _audioSkinAc = _skin.Accent;
                    _audioSkinMu = APvMu;
                    _audioSkinFaint = Color.FromArgb(120, _skin.Fg2);
                    _audioSkinned = true;
                }
            }
            catch { _audioSkinned = false; _skin = null; }
            // 换肤应用不能中断预览窗构造（否则有声音却不弹卡）：单独兜底。
            try { ApplyAudioSkinToControls(); } catch { }
        }

        // 把当前 skin 应用到所有音频控件（换肤初始化与切歌后都要调用）。
        private void ApplyAudioSkinToControls()
        {
            BackColor = APvBg;
            _toolbar.BackColor = APvBg;
            _scrollPanel.BackColor = APvBg;
            if (_audioHeadBar != null) { _audioHeadBar.BackColor = APvBg; _audioHeadBar.ForeColor = APvF2; }
            if (_audioBody != null) _audioBody.BackColor = APvBg;
            // 文字背景必须透明：阴影画在信息区背景层，透明子控件才能取到（否则会切出直角）
            if (_audioNameLbl != null) { _audioNameLbl.BackColor = Color.Transparent; _audioNameLbl.ForeColor = APvFg; }
            if (_audioTimeLbl != null) { _audioTimeLbl.BackColor = Color.Transparent; _audioTimeLbl.ForeColor = APvMu; }
            if (_audioTimeCur != null) { _audioTimeCur.BackColor = Color.Transparent; _audioTimeCur.ForeColor = APvAc; }
            if (_audioTimeTot != null) { _audioTimeTot.BackColor = Color.Transparent; _audioTimeTot.ForeColor = APvMu; }
            if (_audioMetaLbl != null) { _audioMetaLbl.BackColor = Color.Transparent; _audioMetaLbl.ForeColor = APvMu; }
            if (_audioPlayBtn != null) _audioPlayBtn.ForeColor = APvFg;
            if (_audioCloseBtn != null) _audioCloseBtn.ForeColor = APvF2;
            if (_audioRoundPlay != null) { _audioRoundPlay.Accent = APvAc; _audioRoundPlay.Fore = _skin != null ? _skin.Bg : PvBg; }
            if (_folderPanel != null) _folderPanel.BackColor = APvBg;
            if (_folderHead != null) _folderHead.BackColor = APvBg;
            if (_folderHeadLbl != null) { _folderHeadLbl.BackColor = APvBg; _folderHeadLbl.ForeColor = APvF2; }
            if (_loopBtn != null) { _loopBtn.BackColor = APvBg; _loopBtn.ForeColor = APvAc; }
            if (_folderList != null) { _folderList.BackColor = APvBg; _folderList.ForeColor = APvFg; _folderList.Invalidate(); }
            Color chipBg = _skin != null ? _skin.TintC : Color.FromArgb(20, Config.ListFg);
            Color chipBd = _skin != null ? Color.FromArgb(46, _skin.Accent) : Color.FromArgb(40, Config.ListAc);
            if (_chipFormat != null) { _chipFormat.Ink = APvF2; _chipFormat.ChipBg = chipBg; _chipFormat.Border = chipBd; }
            if (_chipSize != null) { _chipSize.Ink = APvF2; _chipSize.ChipBg = chipBg; _chipSize.Border = chipBd; }
            if (_audioViz != null)
            {
                _audioViz.BackColor = APvBg;
                _audioViz.AccentColor = _audioSkinned ? (Color?)APvAc : null;
                _audioViz.DimColor = _audioSkinned ? (Color?)_audioSkinFaint : null;
            }
            if (_spectrumViz != null)
            {
                _spectrumViz.BackColor = APvBg;
                _spectrumViz.AccentColor = _audioSkinned ? (Color?)APvAc : null;
                _spectrumViz.DimColor = _audioSkinned ? (Color?)_audioSkinFaint : null;
            }
            if (_audioProgress != null)
            {
                _audioProgress.BackColor = APvBg;
                _audioProgress.AccentColor = _audioSkinned ? (Color?)APvAc : null;
                _audioProgress.DimColor = _audioSkinned ? (Color?)_audioSkinFaint : null;
            }
            if (_audioRuler != null)
            {
                _audioRuler.BackColor = Color.Transparent;   // 透出信息区背景层的封面阴影
                _audioRuler.AccentColor = _audioSkinned ? (Color?)APvAc : null;
                _audioRuler.MutedColor = _audioSkinned ? (Color?)APvF2 : null;
                _audioRuler.FaintColor = _audioSkinned ? (Color?)_audioSkinFaint : null;
                _audioRuler.Invalidate();
            }
            Invalidate(true);
            // 圆角模式的窗口描边画在非客户区（WM_NCPAINT 回调取 BackColor），
            // 仅 Invalidate 客户区不会重画它：切歌后边框会卡在上一首封面的颜色。
            if (IsHandleCreated) Win11.RefreshNc(Handle);
        }

        // ---- 拖图片到音频封面区，直接替换当前曲目的封面 ----
        private static readonly string[] AudioCoverExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

        private void UpdateCoverSearchBtn()
        {
            if (_coverSearchBtn == null || _audioBody == null || IsDisposed || _audioBody.IsDisposed) return;
            Point p = _audioBody.PointToClient(Cursor.Position);
            var cr = new Rectangle(AudioArtLeft, AudioArtTop, AudioArtSize, AudioArtSize);
            bool show = cr.Contains(p) && !string.IsNullOrEmpty(FilePath);
            if (_coverSearchBtn.Visible != show) _coverSearchBtn.Visible = show;
        }

        // 首次点击可能被未激活窗口吞掉，多个入口都会触发搜索；用短去抖避免一次点击开多个标签。
        private void TryTriggerCoverSearch(bool forceOther)
        {
            if ((DateTime.UtcNow - _lastCoverSearchAt).TotalMilliseconds < 600) return;
            _lastCoverSearchAt = DateTime.UtcNow;
            OpenCoverImageSearch(forceOther);
        }

        private void OpenCoverImageSearch(bool forceOther)
        {
            string name = string.IsNullOrEmpty(FilePath) ? "" : Path.GetFileNameWithoutExtension(FilePath);
            if (string.IsNullOrWhiteSpace(name)) return;
            // 按界面语言选引擎：zh→百度，其余（en/ja/ko/fr/ru/es）→Google；右键切到另一个。
            bool zh = string.Equals(I18n.Lang, "zh", StringComparison.OrdinalIgnoreCase);
            bool google = forceOther ? zh : !zh;
            string q = Uri.EscapeDataString(name);
            string url = google
                ? "https://www.google.com/search?tbm=isch&q=" + q
                : "https://image.baidu.com/search/index?tn=baiduimage&word=" + q;
            Program.DebugLog("[searchbtn] start url=" + url);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Program.DebugLog("[searchbtn] fail=" + ex.Message); }
        }

        // 不依赖 Label 自身的 MouseDown：封面区每个控件都按按钮矩形命中处理点击，
        // 避免被上层透明控件吃掉事件导致“点了没反应”。
        private void CoverAreaMouseDown(MouseEventArgs e)
        {
            if (_coverSearchBtn == null || !_coverSearchBtn.Visible || _audioBody == null
                || _audioBody.IsDisposed || IsDisposed) return;
            Point p = _audioBody.PointToClient(Cursor.Position);
            if (!_coverSearchBtn.Bounds.Contains(p)) return;
            if (e.Button == MouseButtons.Left) TryTriggerCoverSearch(false);
            else if (e.Button == MouseButtons.Right) TryTriggerCoverSearch(true);
        }

        // 封面区里的子控件（标题/时间/倍速尺）也要接住拖放，否则光标压在文字上时
        // 子控件自己吃掉 DragEnter，父类收不到。
        private static void EnableAudioCoverDrop(Control c, PreviewForm owner)
        {
            if (c == null) return;
            c.AllowDrop = true;
            c.DragEnter += (s, e) => { e.Effect = CanAcceptAudioCover(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; };
            c.DragOver += (s, e) => { e.Effect = CanAcceptAudioCover(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; };
            c.DragDrop += (s, e) => { owner.ApplyAudioCoverDrop(e.Data); e.Effect = DragDropEffects.Copy; };
            c.MouseMove += (s, e) => { owner.UpdateCoverSearchBtn(); };
            if (!(c is ImageSearchButton))   // 按钮自己有 OnMouseDown，别重复触发
                c.MouseDown += (s, e) => { owner.CoverAreaMouseDown(e); };
            foreach (Control child in c.Controls) EnableAudioCoverDrop(child, owner);
        }

        private static bool IsAudioCoverFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return AudioCoverExts.Contains(ext);
        }

        private static bool CanAcceptAudioCover(IDataObject data)
        {
            if (data == null) return false;
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                    foreach (var f in files)
                        if (IsAudioCoverFile(f) && File.Exists(f)) return true;
            }
            if (data.GetDataPresent(DataFormats.Bitmap)) return true;
            // 浏览器拖图：Chrome/Edge 常只给 HTML 里的 <img src> + UniformResourceLocator，
            // 没有 FileDrop，也没有 Bitmap。复用收藏列表已有的图片 URL 提取逻辑。
            string url;
            return LinkEntry.TryGetImageUrl(data, out url);
        }

        // GDI+ 不认识 WebP：先按普通图片解，失败则用项目自带 ffmpeg 转成临时 PNG 再读。
        private static Image LoadCoverImageFile(string path)
        {
            try
            {
                using (var src = Image.FromFile(path)) return new Bitmap(src);
            }
            catch { }
            string exe = FfmpegTools.FindFfmpeg();
            if (string.IsNullOrEmpty(exe)) return null;
            string tmp = Path.Combine(Path.GetTempPath(), "hf_cover_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var psi = new ProcessStartInfo(exe);
                psi.Arguments = "-y -loglevel error -i \"" + path + "\" -frames:v 1 \"" + tmp + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                using (var proc = Process.Start(psi))
                {
                    proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(15000)) { try { proc.Kill(); } catch { } return null; }
                    if (proc.ExitCode != 0) return null;
                }
                using (var src = Image.FromFile(tmp)) return new Bitmap(src);
            }
            catch { return null; }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        // 浏览器拖过来的图片通常只有 URL（HTML 里的 <img src>），先下载到临时文件，
        // 再走本地文件同一条解码路径（含 WebP→PNG 兜底）。
        private static Image LoadCoverImageUrl(string url)
        {
            string tmp = null;
            try
            {
                try { System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12; }
                catch { }
                string ext = ".png";
                try
                {
                    string e = (Path.GetExtension(new Uri(url).AbsolutePath) ?? "").ToLowerInvariant();
                    if (AudioCoverExts.Contains(e)) ext = e;
                }
                catch { }
                tmp = Path.Combine(Path.GetTempPath(), "hf_cover_url_" + Guid.NewGuid().ToString("N") + ext);
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                    try { wc.Headers["Referer"] = new Uri(url).GetLeftPart(UriPartial.Authority) + "/"; }
                    catch { }
                    wc.DownloadFile(url, tmp);
                }
                return LoadCoverImageFile(tmp);
            }
            catch { return null; }
            finally { try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private void ApplyAudioCoverDrop(IDataObject data)
        {
            Image img = null;
            try
            {
                if (data == null) return;
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null)
                        foreach (var f in files)
                        {
                            if (!IsAudioCoverFile(f) || !File.Exists(f)) continue;
                            img = LoadCoverImageFile(f);   // 支持 WebP（ffmpeg 转码兜底）
                            if (img != null) break;
                        }
                }
                if (img == null && data.GetDataPresent(DataFormats.Bitmap))
                {
                    var src = data.GetData(DataFormats.Bitmap) as Image;
                    if (src != null) img = new Bitmap(src);
                }
                if (img == null)
                {
                    string imgUrl;
                    if (LinkEntry.TryGetImageUrl(data, out imgUrl) && !string.IsNullOrEmpty(imgUrl))
                        img = LoadCoverImageUrl(imgUrl);
                }
                if (img == null) return;
                string path = _audio != null ? _audio.Path : FilePath;
                if (string.IsNullOrEmpty(path)) return;
                if (!AudioArtwork.SaveCoverOverride(path, img)) return;
                // 真正写进文件元数据，资源管理器/其他播放器才能看到封面。
                // MCI/WPF 会话可能占着文件，先 Close 再写，写完 Reload 回到原来的播放状态；
                // 失败（被别的进程占用等）时 sidecar 仍生效，本程序里照常显示替换封面。
                string cover = AudioArtwork.OverridePath(path);
                bool wasPlaying = _audio != null && _audio.IsPlaying;
                if (_audio != null) { try { _audio.Close(); } catch { } }
                AudioArtwork.EmbedCoverFile(path, cover);
                if (_audio != null)
                {
                    try
                    {
                        _audio.Reload(path);
                        if (!wasPlaying) _audio.Pause();
                    }
                    catch { }
                }
                ReloadAudioArtwork(path);
            }
            catch { }
            finally { if (img != null) img.Dispose(); }
        }

        private void SetupAudio(Control hostPanel, int contentW)
        {
            _audio = hostPanel.Tag as IMediaSession;
            // V5 封面取色换肤（封面/取色细节见 ReloadAudioArtwork）；切歌时由 PlayFolderTrack 复用。
            // 这里先加载一次拿到封面图，供下面创建封面 PictureBox 使用。
            string ap0 = _audio != null ? _audio.Path : null;
            if (string.IsNullOrEmpty(ap0)) ap0 = FilePath;
            ReloadAudioArtwork(ap0);
            BackColor = APvBg;
            // WPF 宿主必须留在可见控件树里才会持续播放：塞进操作栏左上角 1×1 区域，
            // 被操作栏盖住不可见，但折叠/展开都不影响音频输出
            hostPanel.Dock = DockStyle.None;
            hostPanel.Size = new Size(1, 1);
            hostPanel.Location = new Point(0, 0);
            _toolbar.Controls.Add(hostPanel);
            hostPanel.SendToBack();

            _renameBtn.Visible = false;
            _openBtn.Visible = false;
            _copyBtn.Visible = false;
            _pinLaunchBtn.Visible = false;
            _metaLbl.Visible = false;
            _nameLbl.Visible = false;   // 标题移到展开信息区
            _pinBtn.BackColor = Color.Transparent;
            _toolbar.ShowProgress = false;   // 进度由波形呈现
            _scrollPanel.AutoScroll = false;   // 音频窗：Dock=Fill 子控件高度由 UpdateAudioHeight 精确管理，关 AutoScroll 避免 Visible 切换时滚动条显隐抖动

            // V4：30px 圆形播放/暂停键（accent 底 + 前景图标）
            _audioRoundPlay = new RoundPlayButton
            {
                Size = Dpi.Sz(30, 30),
                Location = new Point(Dpi.S(4), Dpi.S(2)),
                Playing = _audio != null && _audio.IsPlaying,
                Accent = APvAc,
                Fore = _skin != null ? _skin.Bg : PvBg,
            };
            _audioPlayBtn = _audioRoundPlay;
            _audioPlayBtn.Click += delegate
            {
                if (_audio == null) return;
                _audio.Toggle();
                UpdateAudioUi();
            };
            _toolbar.Controls.Add(_audioPlayBtn);

            // V4：倍速独立一行（左「倍速」+ 8 档圆点鱼眼 + 档下数值 + 右当前值）。
            // 先建 hint 再 attach（旧代码先传 null，浮层提示永不显示）。
            _audioSpdHint = MediaPreview.CreateSpeedHint(_scrollPanel, 2);
            _audioRuler = new MediaPreview.SpeedRuler { AudioRowMode = true, Height = Dpi.S(34) };
            if (_audioSkinned)
            {
                _audioRuler.AccentColor = APvAc;
                _audioRuler.MutedColor = APvF2;
                _audioRuler.FaintColor = _audioSkinFaint;
            }
            MediaPreview.AttachSpeedRuler(_audioRuler, _audio, _audioSpdHint);

            _audioCloseBtn = new Label
            {
                Size = new Size(18, 18),
                AutoSize = false,
                Text = "✕",
                Font = new Font("Microsoft YaHei", 8f),
                ForeColor = PvFg,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };
            _audioCloseBtn.Click += delegate { try { Close(); } catch { } };
            Tips.Set(_audioCloseBtn, I18n.T("tip_close_preview"));
            _toolbar.Controls.Add(_audioCloseBtn);

            // 操作栏中段即波形：点击/横向拖动 = seek；竖向拖动 = 移动窗口（拖动过即固定）
            _audioViz = new WaveView { BackColor = APvBg };
            if (_audioSkinned) { _audioViz.AccentColor = _audioSkinAc; _audioViz.DimColor = _audioSkinFaint; }
            _audioViz.Seek += delegate(double r) { _audio.SeekRatio(r); };
            _audioViz.MoveWindow += delegate
            {
                var before = Location;
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                if (Location != before && !_pinned)
                {
                    _pinned = true;
                    RefreshPinIcon();
                    Padding = new Padding(1);
                    Invalidate();
                }
            };
            _toolbar.Controls.Add(_audioViz);

            _spectrumViz = new SpectrumView { BackColor = APvBg, Visible = false };
            if (_audioSkinned) { _spectrumViz.AccentColor = _audioSkinAc; _spectrumViz.DimColor = _audioSkinFaint; }
            _spectrumViz.Seek += delegate(double r) { _audio.SeekRatio(r); };
            _spectrumViz.MoveWindow += delegate
            {
                var before = Location;
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                if (Location != before && !_pinned)
                {
                    _pinned = true;
                    RefreshPinIcon();
                    Padding = new Padding(1);
                    Invalidate();
                }
            };
            _toolbar.Controls.Add(_spectrumViz);

            _audioProgress = new AudioProgress { BackColor = APvBg, Visible = false };
            if (_audioSkinned) { _audioProgress.AccentColor = _audioSkinAc; _audioProgress.DimColor = _audioSkinFaint; }
            _audioProgress.Seek += delegate(double r) { _audio.SeekRatio(r); };
            _toolbar.Controls.Add(_audioProgress);


            // 信息区折叠头：横条按钮，点击展开/折叠详细信息
            _audioHeadBar = new Label
            {
                Height = HeadBarH,
                BackColor = APvBg,
                ForeColor = APvMu,
                Font = new Font(Config.FontName, 8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                Cursor = Cursors.Hand,
                Text = "▶ " + I18n.T("audio_details")
            };
            _scrollPanel.Controls.Add(_audioHeadBar);
            _audioHeadBar.MouseDown += AudioBarDown;
            _audioHeadBar.MouseMove += AudioBarMove;
            _audioHeadBar.MouseUp += AudioBarUp;

            // 信息区统一自绘：阴影 + 封面画在背景层；文字/chip/倍速尺全部透明背景，
            // 压在阴影上也不会把阴影切出直角硬边（方案 3）。
            _audioBody = new AudioInfoBody { BackColor = APvBg };
            ((AudioInfoBody)_audioBody).ArtPainter = PaintAudioCover;
            // 拖图片到封面区直接替换封面；替换结果按音频路径持久化，切歌/重启后仍在。
            Tips.Set(_audioBody, I18n.T("tip_audio_cover_drop"));
            int searchSize = Dpi.S(24);
            _coverSearchBtn = new ImageSearchButton
            {
                Size = new Size(searchSize, searchSize),
                Location = new Point(AudioArtLeft + AudioArtSize - searchSize - Dpi.S(6),
                                     AudioArtTop + AudioArtSize - searchSize - Dpi.S(6)),
                AccessibleName = "搜索封面图",
                Visible = false
            };
            _coverSearchBtn.Clicked = btn =>
            {
                if (btn == MouseButtons.Left) TryTriggerCoverSearch(false);
                else if (btn == MouseButtons.Right) TryTriggerCoverSearch(true);
            };
            Tips.Set(_coverSearchBtn, I18n.T("tip_audio_cover_search"));
            _audioBody.Controls.Add(_coverSearchBtn);
            _coverSearchBtn.BringToFront();
            _audioBody.MouseLeave += (s, e) =>
            {
                // 移到封面上的子控件时偶尔会收到伪 MouseLeave：光标还在封面内就别隐藏按钮，
                // 否则紧跟着的点击会打在已隐藏的控件上，表现为"要点很多次"。
                if (_coverSearchBtn == null || _audioBody.IsDisposed) return;
                Point p = _audioBody.PointToClient(Cursor.Position);
                if (!new Rectangle(AudioArtLeft, AudioArtTop, AudioArtSize, AudioArtSize).Contains(p))
                    _coverSearchBtn.Visible = false;
            };
            int textX = AudioTextX;
            _audioNameLbl = new Label
            {
                Location = new Point(textX, AudioNameY),
                Font = new Font(Config.FontName, 9.5f, FontStyle.Bold),
                ForeColor = PvFg,
                BackColor = Color.Transparent,
                AutoEllipsis = false,      // 长文件名换行完整显示，不截断
                Height = Dpi.S(20)
            };
            _audioTimeLbl = new Label
            {
                Location = new Point(textX, AudioNameY + Dpi.S(24)),
                Font = new Font(Config.FontName, 9f),
                ForeColor = APvMu,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                Height = Dpi.S(18),
                Text = I18n.T("media_loading"),
                Visible = false   // V4：时间拆成 cur/tot 两段显示
            };
            _audioMetaLbl = new Label
            {
                Location = new Point(textX, AudioNameY + Dpi.S(46)),
                Font = new Font(Config.FontName, 8f),
                ForeColor = APvMu,
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                Height = Dpi.S(18),
                Visible = false   // V4：格式/大小改用 chip 胶囊
            };
            // V4 时间：当前时间强调色 + 总时长弱化色
            _audioTimeCur = new Label { Font = new Font(Config.FontName, 9f, FontStyle.Bold), ForeColor = APvAc, BackColor = Color.Transparent, AutoSize = true, Location = new Point(textX, AudioNameY + Dpi.S(24)), Text = "00:00" };
            _audioTimeTot = new Label { Font = new Font(Config.FontName, 9f), ForeColor = APvMu, BackColor = Color.Transparent, AutoSize = true, Location = new Point(textX + Dpi.S(52), AudioNameY + Dpi.S(24)), Text = "/ 00:00" };
            // V4 chip：格式、大小（胶囊）
            _chipFormat = new AudioChip("") { Font = new Font(Config.FontName, 8f), Ink = APvF2, Size = Dpi.Sz(46, 20), Location = new Point(textX, AudioNameY + Dpi.S(46)) };
            _chipSize = new AudioChip("") { Font = new Font(Config.FontName, 8f), Ink = APvF2, Size = Dpi.Sz(58, 20), Location = new Point(textX + Dpi.S(52), AudioNameY + Dpi.S(46)) };
            _audioBody.Controls.Add(_audioNameLbl);
            _audioBody.Controls.Add(_audioTimeLbl);
            _audioBody.Controls.Add(_audioMetaLbl);
            _audioBody.Controls.Add(_audioTimeCur);
            _audioBody.Controls.Add(_audioTimeTot);
            _audioBody.Controls.Add(_chipFormat);
            _audioBody.Controls.Add(_chipSize);
            // V4：倍速尺独立一行（全宽），放在信息区底部
            _audioRuler.Dock = DockStyle.None;
            _audioBody.Controls.Add(_audioRuler);
            _scrollPanel.Controls.Add(_audioBody);
            _audioBody.Dock = DockStyle.None;
            _audioBody.Location = new Point(0, 0);
            _audioBody.Height = 0;
            BindResize(_audioBody);



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
                // ffmpeg 会话在后台音频线程触发这些事件；必须切回 UI 线程，
                // 否则自动切歌时会在后台线程销毁/重设封面位图，和界面绘制撞车
                // （GDI+“对象当前正在其他地方使用”，封面随后显示成错误图）。
                _audio.Opened += delegate { OnAudioEvent(UpdateAudioUi); };
                _audio.EstimateReady += delegate { OnAudioEvent(UpdateAudioUi); };
                _audio.Ended += delegate
                {
                    OnAudioEvent(delegate
                    {
                        UpdateAudioUi();
                        OnFolderTrackEnded();   // 文件夹播放模式：按循环模式自动切下一首
                    });
                };
                _audio.Failed += delegate
                {
                    OnAudioEvent(delegate
                    {
                        if (_audioTimeLbl != null) _audioTimeLbl.Text = "解码失败";
                        UpdateAudioUi();
                    });
                };
                // 采集不在此启动：SyncMeter 跟随播放状态启停，暂停时释放环回
                _audioMeter = new AudioMeter();
            }

            // 封面区及其中所有文字/控件统一开启拖放接收
            EnableAudioCoverDrop(_audioBody, this);

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
            if (_audioHeadBar != null)
                _audioHeadBar.Text = (on ? "▼ " : "▶ ") + I18n.T("audio_details");
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
            // 倍速尺显隐随展开（V4：信息区底部独立一行）；先定显隐，再据此算高度。
            if (_audioRuler != null)
                _audioRuler.Visible = _audioExpanded && Config.AudioSpeedToggle;
            // 展开时按标题换行/封面/倍速行实际所需高度撑开信息区。
            if (_audioExpanded && infoH > 0) infoH = Math.Max(infoH, CalcAudioInfoNeed());
            int body = HeadBarH + infoH + listH;
            // 只改窗口高度，不动宽度：ClientSize setter 会触发 WM_NCCALCSIZE 内缩 FramePad，
            // 每次读 ClientSize.Width（已被 WM_SIZE 更新为客户区宽）再写回会让窗口宽度递减 2*FramePad。
            // 用 Height（窗口高度）避开 ClientSize.Width 读写循环。
            Height = _toolbarH + body + 2 * Win11.FramePad;
            ApplyLayout();
            // 手动定位信息区与列表区（避免 AutoScroll+Dock 交互导致 _folderPanel 空白）
            int w = ClientSize.Width;
            if (_audioHeadBar != null)
            {
                _audioHeadBar.Location = new Point(0, 0);
                _audioHeadBar.Size = new Size(w, HeadBarH);
            }
            if (_audioBody != null)
            {
                _audioBody.Location = new Point(0, HeadBarH);
                _audioBody.Size = new Size(w, infoH);
            }
            if (_folderPanel != null && _folderMode)
            {
                _folderPanel.Location = new Point(0, HeadBarH + infoH);
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
            SortFolderTracks();
            _folderIdx = _folderTracks.FindIndex(t => string.Equals(t, FilePath, StringComparison.OrdinalIgnoreCase));
            if (_folderIdx < 0) _folderIdx = 0;
            return _folderTracks.Count > 0;
        }

        private void SortFolderTracks()
        {
            string mode = Config.SortMode;
            bool desc = Config.SortDesc;
            if (mode == "date")
            {
                _folderTracks.Sort((a, b) =>
                {
                    int c = File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                    return desc ? -c : c;
                });
            }
            else if (mode == "size")
            {
                _folderTracks.Sort((a, b) =>
                {
                    int c;
                    try { c = new FileInfo(a).Length.CompareTo(new FileInfo(b).Length); }
                    catch { c = 0; }
                    return desc ? -c : c;
                });
            }
            else
            {
                _folderTracks.Sort((a, b) =>
                {
                    int c = NativeMethods.StrCmpLogicalW(a, b);
                    return desc ? -c : c;
                });
            }
        }

        private void UpdateFolderHeadText()
        {
            if (_folderHeadLbl == null || _folderTracks == null) return;
            string dirName = "";
            try { dirName = Path.GetFileName(_folderDir); } catch { }
            _folderHeadLbl.Text = (_folderListOn ? "▼ " : "▶ ")
                + (string.IsNullOrEmpty(dirName) ? _folderDir : dirName)
                + "  (" + _folderTracks.Count + ")";
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
                SetAudioExpanded(true);   // 进入文件夹模式：信息区默认显示（横条按钮可折叠）

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
            // 切歌：重新加载新曲目的封面并按封面主色换肤（封面/取色必须与曲目同步）
            ReloadAudioArtwork(path);
            SetHeader(Path.GetFileName(path), meta);
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
            _folderPanel = new Panel { BackColor = APvBg, Dock = DockStyle.None, Visible = false };
            _folderHead = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = APvBg };
            var head = _folderHead;
            _folderHeadLbl = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = APvF2,
                BackColor = APvBg,
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
                ForeColor = APvAc,
                BackColor = APvBg,
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

            _folderList = new FolderListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = APvBg,
                ForeColor = APvFg,
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
            head.Controls.Add(_loopBtn);         // Right
            _folderPanel.Controls.Add(_folderList);   // Fill 先加
            _folderPanel.Controls.Add(head);          // Top 后加
            _scrollPanel.Controls.Add(_folderPanel);
            foreach (var c in new Control[] { _folderHead, _folderHeadLbl, _loopBtn })
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

            UpdateFolderHeadText();
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
            using (var b = new SolidBrush(sel ? (_skin != null ? _skin.TintC : PvHv) : APvBg))
                e.Graphics.FillRectangle(b, e.Bounds);
            string name = (cur ? "♪ " : "") + (string)_folderList.Items[e.Index];
            var rc = new Rectangle(e.Bounds.X + Dpi.S(8), e.Bounds.Y, e.Bounds.Width - Dpi.S(16), e.Bounds.Height);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var sf = StringFormat.GenericTypographic;
            float lh = _folderList.Font.GetHeight(e.Graphics);
            using (var b = new SolidBrush(cur ? APvAc : APvFg))
                e.Graphics.DrawString(RowLabel.MidEllipsis(e.Graphics, name, _folderList.Font, rc.Width), _folderList.Font, b, rc.X, e.Bounds.Y + (e.Bounds.Height - lh) / 2, sf);
        }

        private int FolderListH()
        {
            int headH = Dpi.S(24);
            if (!_folderListOn) return headH;
            int rows = Math.Min(Math.Max(_folderTracks != null && _folderTracks.Count > 0 ? _folderTracks.Count : 1, 1), FolderMaxRows);
            return headH + rows * FolderRowH;
        }

        private void SetFolderListVisible(bool on)
        {
            _folderListOn = on;
            if (_folderList != null) _folderList.Visible = on;
            UpdateFolderHeadText();
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

        // 模式进入提示：显示在窗口底部，1.6s 自动消失（与按钮提示共用底部条）
        private void ShowFolderTip(string text)
        {
            if (_folderTipTimer == null)
            {
                _folderTipTimer = new Timer { Interval = 1600 };
                _folderTipTimer.Tick += delegate { _folderTipTimer.Stop(); Tips.HideBar(this); };
            }
            Tips.ShowBar(this, text);
            _folderTipTimer.Stop();
            _folderTipTimer.Start();
        }


        // 音频信息区所需高度：标题换行后的行数决定，至少容纳封面与三行信息。
        // 布局与高度共用此计算，避免标题变长时被卡片裁掉。
        private int CalcAudioInfoNeed()
        {
            int textX = AudioTextX;
            int textW = Math.Max(Dpi.S(60), _audioBody != null ? _audioBody.Width - Dpi.S(10) - textX : Dpi.S(160));
            int nameH = Dpi.S(20);
            if (_audioNameLbl != null && !string.IsNullOrEmpty(_audioNameLbl.Text))
            {
                using (var g = _audioNameLbl.CreateGraphics())
                {
                    var sz = g.MeasureString(_audioNameLbl.Text, _audioNameLbl.Font, textW);
                    int lines = Math.Max(1, (int)Math.Ceiling(sz.Height / (_audioNameLbl.Font.GetHeight(g))));
                    if (lines > 3) lines = 3;   // 最多 3 行，再多也不至于把窗口撑爆
                    nameH = Math.Max(Dpi.S(20), (int)Math.Ceiling(_audioNameLbl.Font.GetHeight(g)) * lines + Dpi.S(2));
                }
            }
            // V4：上半区（封面 + 标题/时间/chip） + 下半区倍速独立行
            int upper = AudioNameY + nameH + Dpi.S(2) + Dpi.S(18) + Dpi.S(2) + Dpi.S(20) + Dpi.S(4);
            int coverNeed = AudioArtTop + AudioArtSize + Dpi.S(6);
            int rulerH = _audioRuler != null && _audioRuler.Visible ? _audioRuler.Height + Dpi.S(4) : 0;
            int total = Math.Max(upper, coverNeed) + rulerH + Dpi.S(4);
            return Math.Max(AudioInfoH, total);
        }

        // 音频会话事件统一切回 UI 线程（ffmpeg 在后台音频线程触发 Ended/Opened/Failed）
        private void OnAudioEvent(Action act)
        {
            if (act == null || IsDisposed) return;
            try
            {
                if (InvokeRequired)
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(act);
                }
                else act();
            }
            catch { }
        }

        // 横向改尺寸后重排音频信息区（标题换行、chip、倍速尺跟随新宽度）
        private void RelayoutAudioOnResize()
        {
            if (_audio == null || _audioBody == null) return;
            _audioBody.Width = ClientSize.Width;
            UpdateToolbarBtnPos();   // 内部会调用 UpdateAudioInfoLayout
        }

        private void UpdateAudioInfoLayout()
        {
            if (_audioBody == null || _audioNameLbl == null) return;
            int textX = AudioTextX;
            int textW = Math.Max(Dpi.S(60), _audioBody.Width - Dpi.S(10) - textX);
            _audioNameLbl.Location = new Point(textX, AudioNameY);
            _audioNameLbl.Width = textW;
            // 标题按测量高度换行显示
            using (var g = _audioNameLbl.CreateGraphics())
            {
                var sz = g.MeasureString(_audioNameLbl.Text, _audioNameLbl.Font, textW);
                int lines = Math.Max(1, (int)Math.Ceiling(sz.Height / (_audioNameLbl.Font.GetHeight(g))));
                if (lines > 3) lines = 3;
                _audioNameLbl.Height = Math.Max(Dpi.S(20), (int)Math.Ceiling(_audioNameLbl.Font.GetHeight(g)) * lines + Dpi.S(2));
            }
            int timeY = _audioNameLbl.Top + _audioNameLbl.Height;   // 紧贴标题，不再额外留白
            _audioTimeLbl.Location = new Point(textX, timeY);
            if (_audioTimeCur != null) _audioTimeCur.Location = new Point(textX, timeY);
            // 当前时间与总时长紧凑排列（原型 "00:33 / 04:00"）
            int totX = textX + (_audioTimeCur != null ? _audioTimeCur.Width : Dpi.S(38)) + Dpi.S(3);
            if (_audioTimeTot != null) _audioTimeTot.Location = new Point(totX, timeY);
            int chipY = timeY + Dpi.S(18);
            if (_chipFormat != null) _chipFormat.Location = new Point(textX, chipY);
            if (_chipSize != null)
                _chipSize.Location = new Point(textX + (_chipFormat != null ? _chipFormat.Width + Dpi.S(6) : Dpi.S(52)), chipY);
            int afterChips = chipY + Dpi.S(22);
            _audioMetaLbl.Location = new Point(textX, afterChips);
            _audioMetaLbl.Width = textW;
            // V4：倍速尺独立一行，横跨整个信息区（左标签 + 8 档圆点 + 右当前值）
            if (_audioRuler != null)
            {
                // 倍速行不能压到封面：至少放到封面（含底部阴影）之下
                int rulerY = Math.Max(afterChips + Dpi.S(2), AudioArtTop + AudioArtSize + Dpi.S(6));
                _audioRuler.SetBounds(Dpi.S(4), rulerY, Math.Max(Dpi.S(60), _audioBody.Width - Dpi.S(8)), _audioRuler.Height);
                _audioRuler.BackColor = Color.Transparent;   // 透出封面阴影，不再切出直角色块
                _audioRuler.BringToFront();
            }
        }

        // 音频窗口：宽度沿用上次；高度由悬停展开模型管理，打开总是折叠 34px
        private void ApplyAudioSize(int contentW)
        {
            var wa = Screen.GetWorkingArea(this);
            int w = Config.PreviewAudioW > 0 ? Dpi.S(Config.PreviewAudioW) : 0;
            if (w <= 0) w = Math.Max(Dpi.S(360), contentW + Dpi.S(20));
            w = Math.Max(Dpi.S(300), Math.Min(w, wa.Width - Dpi.S(20)));
            ClientSize = new Size(w, _toolbarH);
            UpdateAudioHeight();
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
            // 波形图关闭时取消已运行的解码器（频谱用 AudioMeter 实时数据，不需要 WaveDecoder）
            if (!Config.WaveformEnabled && _waveDec != null)
            {
                _waveDec.Cancel();
                _waveDec = null;
                _wavePath = null;
                Array.Clear(_waveData, 0, _waveData.Length);
            }
            if (_waveDec == null && total > 0 && !_audio.HasFailed && (_audioExpanded || _pinned)
                && Config.WaveformEnabled)
                _waveDec = WaveDecoder.Begin(_audio.Path, total / 1000.0);

            // V4 时间拆分：当前时间（强调色）+ 总时长（弱化色）
            if (_audio.HasFailed)
                _audioTimeCur.Text = "解码失败";
            else if (total > 0)
                _audioTimeCur.Text = MediaPreview.Fmt(TimeSpan.FromMilliseconds(pos));
            else if (est > 0)
                _audioTimeCur.Text = "≈ " + MediaPreview.Fmt(TimeSpan.FromMilliseconds(est));
            else
                _audioTimeCur.Text = "00:00";
            if (_audioTimeTot != null)
                _audioTimeTot.Text = total > 0 ? "/ " + MediaPreview.Fmt(_audio.Duration) : "/ --:--";
            if (_audioTimeLbl != null)
                _audioTimeLbl.Text = _audioTimeCur.Text + " " + (_audioTimeTot != null ? _audioTimeTot.Text : "");
            if (_audioRoundPlay != null) _audioRoundPlay.Playing = _audio.IsPlaying;
            _audioPlayBtn.Enabled = !_audio.HasFailed;

            double r = total > 0 ? pos / total : 0;
            if (r > 1) r = 1;
            // 波形图/频谱可见性：波形图关闭时隐藏 _audioViz，频谱开启时显示 _spectrumViz
            _audioViz.Visible = Config.WaveformEnabled;
            if (_spectrumViz != null) _spectrumViz.Visible = !Config.WaveformEnabled && Config.SpectrumEnabled;
            if (_audioProgress != null) _audioProgress.Visible = !Config.WaveformEnabled && !Config.SpectrumEnabled;
            double durSec = total > 0 ? total / 1000.0 : (est > 0 ? est / 1000.0 : 0);
            _audioViz.DurationSec = durSec;
            if (_spectrumViz != null) _spectrumViz.DurationSec = durSec;
            if (Config.WaveformEnabled && _waveDec != null && !_waveDec.Failed)
            {
                // 全量摘要：解码中逐桶点亮，解码完即完整波形
                _audioViz.SetData(_waveDec.Rms, _waveDec.Peak, r);
            }
            else if (Config.WaveformEnabled)
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
            // 频谱：实时环回采集 FFT（AudioMeter.Levels），随播放跳动
            if (!Config.WaveformEnabled && Config.SpectrumEnabled && _spectrumViz != null)
            {
                float[] lv = _audioMeter != null && !_audioMeter.Failed ? _audioMeter.Levels : null;
                _spectrumViz.SetData(lv, r);
            }
            if (!Config.WaveformEnabled && !Config.SpectrumEnabled && _audioProgress != null)
                _audioProgress.SetPos(r);

        }

        // 采集只跟着播放走：暂停即停环回（省 CPU，别的程序出声也不会被白算 FFT），恢复播放再启
        // 波形图和频谱都关时不需要 AudioMeter（省 CPU），任一开启才随播放启停
        private void SyncMeter()
        {
            if (_audioMeter == null) return;
            bool want = _audio.IsPlaying && (Config.WaveformEnabled || Config.SpectrumEnabled);
            if (want == _meterOn) return;
            _meterOn = want;
            if (want) _audioMeter.Start(); else _audioMeter.Stop();
        }

        // 操作栏按下：右下/边缘命中时先走尺寸调整；否则记录起点，等待判定点击还是拖动
        private bool _audioDownInHeadBar; // 按下起点在信息区横条：原地点击=展开/折叠
        private bool _audioDownInInfo;   // 按下起点在信息区：原地点击不操作（拖动由 Move 处理）
        private bool _audioDownInHead;   // 按下起点在文件夹 head 行：原地点击=展开/折叠列表

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
            _audioDownInHeadBar = ReferenceEquals(sender, _audioHeadBar);
            _audioDownInInfo = ReferenceEquals(sender, _audioBody) || ReferenceEquals(sender, _audioNameLbl)
                || ReferenceEquals(sender, _audioTimeLbl) || ReferenceEquals(sender, _audioMetaLbl);
            _audioDownInHead = ReferenceEquals(sender, _folderHead) || ReferenceEquals(sender, _folderHeadLbl)
                || ReferenceEquals(sender, _loopBtn);
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
                RefreshPinIcon();
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
            if (_audioDownInHeadBar)
            {
                SetAudioExpanded(!_audioExpanded);
                return;
            }
            if (_audioDownInInfo) return;   // 信息区原地点击不操作（拖动由 AudioBarMove 处理）
            if (_audioDownInHead)
            {
                if (ReferenceEquals(sender, _loopBtn)) return;   // 循环按钮交给 Click
                ToggleFolderList();
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
            BackColor = PvBg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);

            KeyPreview = true;
            // 正在改文件名时 Esc 归编辑框处理（取消改名），不能直接把整个预览窗关掉
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && _renameBox == null) Close(); };
            _wheelFilter = new WheelFilter(this);
            Application.AddMessageFilter(_wheelFilter);
            FormClosed += (s, e) =>
            {
                try { Application.RemoveMessageFilter(_wheelFilter); } catch { }
                if (_renameClickFilter != null)
                {
                    try { Application.RemoveMessageFilter(_renameClickFilter); } catch { }
                    _renameClickFilter = null;
                }
                if (_sizeSaveTimer != null)
                {
                    _sizeSaveTimer.Stop(); _sizeSaveTimer.Dispose(); _sizeSaveTimer = null;
                    try { Config.Save(); } catch { }
                }
                // 关窗时若有未提交的编辑（防抖还没到点），立即写回一次，避免丢改动
                if (_linkAutoSaveTimer != null)
                {
                    _linkAutoSaveTimer.Stop();
                    if (_linkAutoCommit != null && _textDirty && !IsDisposed)
                        try
                        {
                            string md = _content == _sourceBox ? _sourceBox.Text : (_notePanel != null ? _notePanel.GetMarkdown() : "");
                            _linkAutoCommit(md.Trim());
                            _textDirty = false;
                        }
                        catch { }
                    _linkAutoSaveTimer.Dispose(); _linkAutoSaveTimer = null;
                }
                // 预览窗关了，悬停弹出的复制菜单不能还挂在屏幕上
                if (_copyMenu != null) { try { _copyMenu.Close(); } catch { } _copyMenu = null; }
            };

            _toolbar = new AudioBarPanel { BackColor = PvBg, ForeColor = PvFg, Height = Dpi.S(34) };
            Controls.Add(_toolbar);
            // P0：操作栏与内容之间一条低对比分隔线（颜色取自列表配色）
            _toolbar.Paint += (s, e) =>
            {
                using (var pen = new Pen(PvBd))
                    e.Graphics.DrawLine(pen, 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
            };

            _scrollPanel = new Panel { AutoScroll = false, BackColor = PvBg };
            Controls.Add(_scrollPanel);
            Win11.ApplyScrollTheme(_scrollPanel, PvDark);

            // P1：右下角淡色拖拽手柄（纯装饰；Disabled 不接鼠标，缩放仍由窗口边缘命中测试处理）
            _grip = new Label
            {
                Size = Dpi.Sz(12, 12),
                Enabled = false,
                BackColor = PvBg,
                Visible = false,
            };
            _grip.Paint += (s, e) =>
            {
                using (var pen = new Pen(PvMu))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        int d = 4 + i * 3;
                        e.Graphics.DrawLine(pen, _grip.Width - 2, d, d, _grip.Height - 2);
                    }
                }
            };
            Controls.Add(_grip);


            // 左上角：重命名 + 用默认程序打开（取代原类型角标）
            _renameBtn = new Label
            {
                Size = Dpi.Sz(20, 18),
                Location = new Point(Dpi.S(6), Dpi.S(8)),
                AutoSize = false,
                BackColor = PvBg,
                Image = EditIcon(PvFg),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _renameBtn.MouseEnter += (s, e) => _renameBtn.BackColor = PvHv;
            _renameBtn.MouseLeave += (s, e) => _renameBtn.BackColor = PvBg;
            Tips.Set(_renameBtn, I18n.T("rename"));
            // 编辑中再点一次 = 收起编辑框（改过名就保存），给用户一个明确的退出入口
            _renameBtn.Click += (s, e) =>
            {
                if (_renameBox != null) EndRenameByOutsideClick();
                else BeginFileRename();
            };
            _toolbar.Controls.Add(_renameBtn);

            _openBtn = new Label
            {
                Size = new Size(20, 18),
                Location = new Point(28, 8),
                AutoSize = false,
                BackColor = PvBg,
                Image = OpenIcon(PvFg),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _openBtn.MouseEnter += (s, e) => _openBtn.BackColor = PvHv;
            _openBtn.MouseLeave += (s, e) => _openBtn.BackColor = PvBg;
            Tips.Set(_openBtn, I18n.T("tip_open_file"));
            _openBtn.Click += (s, e) => OpenWithDefaultApp();
            _toolbar.Controls.Add(_openBtn);

            _nameLbl = new Label
            {
                Location = new Point(34, 9),
                Font = new Font(Config.FontName, 9.75f, FontStyle.Bold),
                ForeColor = PvFg,
                BackColor = PvBg,
                AutoEllipsis = true,
                Cursor = Cursors.Hand,
            };
            _toolbar.Controls.Add(_nameLbl);

            _metaLbl = new Label
            {
                Font = new Font(Config.FontName, 8.25f),
                ForeColor = PvMu,
                BackColor = PvBg,
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true,
            };
            _toolbar.Controls.Add(_metaLbl);

            _pinBtn = new Label
            {
                Size = Dpi.Sz(20, 18),
                AutoSize = false,
                BackColor = PvBg,
                Image = PinIcon(false),
                Cursor = Cursors.Hand,
            };
            _pinBtn.Click += (s, e) => TogglePin();
            Tips.Set(_pinBtn, I18n.T("tip_pin_panel"));
            _toolbar.Controls.Add(_pinBtn);

            _pinLaunchBtn = new Label
            {
                Size = Dpi.Sz(20, 18),
                AutoSize = false,
                BackColor = PvBg,
                Image = PinLaunchIcon(false),
                Cursor = Cursors.Hand,
            };
            _pinLaunchBtn.Click += (s, e) => TogglePinToCard();
            // 卡片可能已被取消固定：指针移入时按最新固定状态重画图标
            _pinLaunchBtn.MouseEnter += (s, e) => RefreshPinLaunchIcon();
            Tips.Set(_pinLaunchBtn, I18n.T("tip_pin_card"));
            _toolbar.Controls.Add(_pinLaunchBtn);


            _copyBtn = new Label
            {
                Size = Dpi.Sz(20, 18),
                AutoSize = false,
                BackColor = PvBg,
                Image = CopyIcon(),
                Cursor = Cursors.Hand,
            };
            // 复制按钮悬停即弹菜单，但底部提示条不会盖住菜单，因此仍登记提示并放行菜单门控
            // 鼠标移开按钮后短延时收起：期间鼠标要是移到了菜单上就不关，否则菜单没法点
            var copyLeave = new Timer { Interval = 200 };
            copyLeave.Tick += (s, e) =>
            {
                copyLeave.Stop();
                // 光标确实离开按钮才收起悬停提示；菜单弹出引发的假 Leave 不算
                bool onBtn = !_copyBtn.IsDisposed
                    && _copyBtn.RectangleToScreen(_copyBtn.ClientRectangle).Contains(Cursor.Position);
                if (!onBtn) Tips.EndTimedTip(this, I18n.T("tip_copy_menu"));
                if (_copyMenu == null) return;
                NativeMethods.POINT p; NativeMethods.GetCursorPos(out p);
                if (!_copyMenu.Bounds.Contains(p.X, p.Y)) _copyMenu.Close();
            };
            _copyBtn.MouseEnter += (s, e) =>
            {
                copyLeave.Stop();
                ShowCopyMenu();
                Program.DebugLog("[tip] copy-enter ShowTips=" + Config.ShowTips
                    + " menuOpen=" + Program.ShellMenuOpen + " path=" + (FilePath ?? "null"));
                // 定时提示在时长内免疫 Tips.Hide()，可挡住菜单弹出造成的假 MouseLeave 秒关；
                // 真正离开按钮 / 菜单关闭时由 EndTimedTip 提前收起。
                if (Config.ShowTips) Tips.ShowBarTimed(this, I18n.T("tip_copy_menu"), 3000);
            };
            _copyBtn.MouseLeave += (s, e) => copyLeave.Start();
            Tips.Set(_copyBtn, I18n.T("tip_copy_menu"), true);   // 悬停底部提示；放开菜单门控，否则悬停即弹菜单时提示出不来
            _toolbar.Controls.Add(_copyBtn);

            _saveBtn = new Label
            {
                Size = Dpi.Sz(20, 18),
                AutoSize = false,
                BackColor = PvBg,
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
                // Ctrl+拖头部 = 拖出文件（OLE FileDrop，可直接丢进达芬奇/资源管理器/聊天）：
                // 武装后在 MouseMove 里越过 3px 阈值才真正进 DoDragDrop（模态循环），
                // 拖放期间 CloseExtPreviewForm 被 ExtDragActive 冻结，预览窗不会中途消失
                if (Control.ModifierKeys.HasFlag(Keys.Control) && !string.IsNullOrEmpty(FilePath))
                {
                    _fileDragArmed = true;
                    _fileDragPt = ((Control)s).PointToScreen(e.Location);
                    return;
                }
                _fileDragArmed = false;
                // 双击标题 = 打开所在文件夹（手动判定）
                int nowTick = Environment.TickCount;
                var curPos = Cursor.Position;
                bool dbl = _pvHdrLastTick != 0 && nowTick - _pvHdrLastTick >= 0 && nowTick - _pvHdrLastTick < SystemInformation.DoubleClickTime
                    && Math.Abs(curPos.X - _pvHdrLastPos.X) <= SystemInformation.DoubleClickSize.Width / 2
                    && Math.Abs(curPos.Y - _pvHdrLastPos.Y) <= SystemInformation.DoubleClickSize.Height / 2;
                _pvHdrLastTick = dbl ? 0 : nowTick;
                _pvHdrLastPos = curPos;
                if (dbl && !string.IsNullOrEmpty(FilePath))
                {
                    // 双击标题 = 改名；用 Timer 延迟创建编辑框，确保双击的所有鼠标事件
                    // （包括第二次 MouseUp/Click）完全处理完毕，避免干扰重命名框焦点
                    var renameTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime + 50 };
                    renameTimer.Tick += (ts, te) =>
                    {
                        renameTimer.Stop(); renameTimer.Dispose();
                        if (!IsDisposed && _renameBox == null && !string.IsNullOrEmpty(FilePath))
                            BeginFileRename();
                    };
                    renameTimer.Start();
                    return;
                }
                var sp = ((Control)s).PointToScreen(e.Location);
                int hit = GetResizeHit(sp.X, sp.Y);
                if (hit != 0) { NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, hit, 0); return; }
                // 实际拖动后才固定；单纯点击标题不固定
                var before = Location;
                NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                if (Location != before && !_pinned) { _pinned = true; RefreshPinIcon(); Padding = new Padding(1); Invalidate(); }
            };
            _toolbar.MouseDown += drag;
            _nameLbl.MouseDown += drag;
            _metaLbl.MouseDown += drag;
            foreach (var ctl in new Control[] { _toolbar, _nameLbl, _metaLbl })
            {
                ctl.MouseMove += (s, e) =>
                {
                    var c = (Control)s; var sp = c.PointToScreen(e.Location);
                    UpdateResizeCursor(c, sp.X, sp.Y);
                    // Ctrl 拖出文件：越过阈值进模态 DoDragDrop（数据是路径副本，预览窗生死不影响落点）
                    if (_fileDragArmed && !string.IsNullOrEmpty(FilePath)
                        && Math.Abs(sp.X - _fileDragPt.X) + Math.Abs(sp.Y - _fileDragPt.Y) > 3)
                    {
                        _fileDragArmed = false;
                        ExtDragActive = true;
                        try { DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { FilePath }), DragDropEffects.Copy | DragDropEffects.Move); }
                        catch { }
                        ExtDragActive = false;
                    }
                };
                ctl.MouseLeave += (s, e) => { ((Control)s).Cursor = null; };
                ctl.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) _fileDragArmed = false; };
                Tips.Set(ctl, I18n.T("tip_pv_drag"));
            }
            BuildInfoBar();
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
            int b = Dpi.S(6), br = Dpi.S(20);
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
            int b = Dpi.S(6), br = Dpi.S(20);
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
            if (y <= Top + b) return 0x1C;   // 顶边：专用内部标签（HTTOP 0x0C=12 与内部底边模式撞车）
            if (y >= Bottom - b) return 0x0F;
            return 0;
        }

        // 窗口宽度变化（含手动拖左右边）后重排音频信息区：标题换行/chip/倍速尺跟随
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RelayoutAudioOnResize();
        }

        protected override void WndProc(ref Message m)
        {
            // 预览窗用 ShowWithoutActivation 显示，未激活窗口的第一次点击默认会被
            // MA_ACTIVATEANDEAT 吞掉：按钮看得见但点了没反应。改为 MA_ACTIVATE，
            // 首次点击同时激活窗口并把消息投递给控件。
            if (m.Msg == 0x0021)
            {
                // 未激活窗口的点击常在此处被吞掉，根本不会到 MouseDown。
                // 光标正落在搜索按钮上时直接在这里触发，去抖避免后续 MouseDown 重复。
                // 这里不检查按钮 Visible：伪 MouseLeave 可能刚把它隐藏，但光标仍在按钮区域，
                // 用户这一下点击仍然应该生效。
                if (_coverSearchBtn != null && _audioBody != null && !_audioBody.IsDisposed)
                {
                    Point cp = _audioBody.PointToClient(Cursor.Position);
                    bool hit = _coverSearchBtn.Bounds.Contains(cp);
                    Program.DebugLog("[searchbtn] activate hit=" + hit + " cp=" + cp + " btn=" + _coverSearchBtn.Bounds + " vis=" + _coverSearchBtn.Visible);
                    if (hit) TryTriggerCoverSearch(false);
                }
                base.WndProc(ref m); m.Result = (IntPtr)1; return;
            }
            // 顶层窗口直接按坐标命中搜索按钮：不依赖子控件的 MouseDown（可能被
            // 未激活窗口的首次点击或透明控件吃掉）。
            if ((m.Msg == 0x0201 || m.Msg == 0x0204) && _coverSearchBtn != null &&
                _audioBody != null && !_audioBody.IsDisposed)
            {
                long lp = m.LParam.ToInt64();
                var formPt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                Point bodyPt = _audioBody.PointToClient(PointToScreen(formPt));
                if (_coverSearchBtn.Bounds.Contains(bodyPt))
                {
                    Program.DebugLog("[searchbtn] wndproc click msg=" + m.Msg);
                    TryTriggerCoverSearch(m.Msg != 0x0201);
                    return;
                }
            }
            // 跨显示器/改缩放：按新 DPI 重新布局（工具栏/信息栏/滚动区跟随缩放）
            if (m.Msg == (int)Dpi.WM_DPICHANGED && Handle != IntPtr.Zero)
            {
                Dpi.SetFromWindow(Handle);
                try { ApplyLayout(); UpdateToolbarBtnPos(); } catch { }
            }
            // 圆角模式描边与自身背景同色：亮色描边压在深色配色上很突兀，
            // 同色后只剩 DWM 圆角外形（回调动态取 BackColor，主题/层级着色跟随）
            if (Win11.WndNcBorder(Handle, ref m, () => BackColor)) return;
            if (m.Msg == 0x84)   // WM_NCHITTEST
            {
                var lp = m.LParam.ToInt64();
                var sp = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                var wb = Bounds;
                int grip = Dpi.S(6);
                // 音频窗高度由悬停展开模型管理，只允许左右边缘改宽
                if (sp.X >= wb.Right - grip && sp.Y >= wb.Bottom - grip && _audio == null) { m.Result = (IntPtr)17; return; }
                if (sp.X >= wb.Right - grip && sp.Y >= wb.Top && sp.Y < wb.Bottom) { m.Result = (IntPtr)11; return; }
                if (sp.Y >= wb.Bottom - grip && sp.X >= wb.Left && sp.X < wb.Right && _audio == null) { m.Result = (IntPtr)12; return; }
            }
            else if (m.Msg == 0xA1)   // WM_NCLBUTTONDOWN：拦截系统 sizing loop，手动 resize
            {
                int ht = m.WParam.ToInt32();
                if (ht == 12 || ht == 11 || ht == 17)
                {
                    _pvResize = true;
                    // 0x1C = 顶边专用内部标签：HTTOP(0x0C) 十进制就是 12，与底边的内部
                    // "高度模式"标签 12 撞车——顶边被当成底边式缩放，往下拉反而越拉越长
                    _pvResizeMode = ht == 12 ? 1 : (ht == 11 ? 2 : (ht == 0x1C ? 4 : 3));
                    var sp0 = Cursor.Position;
                    _pvResizeSX = sp0.X;
                    _pvResizeSY = sp0.Y;
                    _pvResizeSW = Width;
                    _pvResizeSH = Height;
                    _pvResizeT0 = Top;
                    Capture = true;
                    return;
                }
            }
            else if (_pvResize && m.Msg == 0x200)   // WM_MOUSEMOVE
            {
                var sp = Cursor.Position;
                if (_pvResizeMode == 4)
                {
                    // 顶边：上沿跟随光标、下沿固定，最小高 60（PR#13 修复顶边拉伸变高）
                    int nh = Math.Max(Dpi.S(60), _pvResizeSH - (sp.Y - _pvResizeSY));
                    Top = _pvResizeT0 + _pvResizeSH - nh;
                    Height = nh;
                    return;
                }
                if ((_pvResizeMode & 1) != 0) Height = Math.Max(Dpi.S(60), _pvResizeSH + (sp.Y - _pvResizeSY));
                if ((_pvResizeMode & 2) != 0) Width = Math.Max(Dpi.S(120), _pvResizeSW + (sp.X - _pvResizeSX));
                return;
            }
            else if (_pvResize && m.Msg == 0x202)   // WM_LBUTTONUP
            {
                _pvResize = false;
                Capture = false;
                if (_content is PictureBox || _content is Panel || _mediaKind == "text")
                {
                    if (_content is PictureBox && ClientSize.Width >= Dpi.S(120)) Config.PreviewMinWidth = Dpi.UnS(ClientSize.Width);
                    RememberSize(ClientSize.Width, ClientSize.Height);
                    Program.DebugLog("[pv-size] resize-end " + _mediaKind + " " + ClientSize.Width + "x" + ClientSize.Height);
                }
                return;
            }
            else if (m.Msg == 0x231) _pvSizeMoveStart = ClientSize;
            else if (m.Msg == 0x232)
            {
                if (ClientSize != _pvSizeMoveStart && (_content is PictureBox || _content is Panel || _mediaKind == "text"))
                {
                    if (_content is PictureBox && ClientSize.Width >= Dpi.S(120)) Config.PreviewMinWidth = Dpi.UnS(ClientSize.Width);
                    RememberSize(ClientSize.Width, ClientSize.Height);
                    Program.DebugLog("[pv-size] resize-end " + _mediaKind + " " + ClientSize.Width + "x" + ClientSize.Height);
                }
            }
            base.WndProc(ref m);
        }

        // 图片预览尺寸入口：优先套用记住的尺寸，否则走自动尺寸
        // 预览窗固定开销：顶部操作栏 34px，加上可选信息栏 28px（音频模式 _audio!=null 不走这里）
        private int ChromeHeight { get { return _toolbarH + (Config.PreviewInfoBar && _audio == null ? InfoBarH : 0); } }

        private void ApplyImageSize(int w, int h)
        {
            int chrome = ChromeHeight;
            if (Config.PreviewImgW > 0 && Config.PreviewImgH > 0)
            {
                // remembered 存的就是窗口总尺寸（用户之前手动拖过），直接套用
                var wa0 = Screen.GetWorkingArea(this);
                int rw = Math.Max(Dpi.S(120), Math.Min(Dpi.S(Config.PreviewImgW), wa0.Width - Dpi.S(20)));
                int rh = Math.Max(Dpi.S(90) + chrome, Math.Min(Dpi.S(Config.PreviewImgH), wa0.Height - Dpi.S(20)));
                ClientSize = new Size(rw, rh);
                Program.DebugLog("[pv-size] remembered " + rw + "x" + rh + " (chrome=" + chrome + ")");
                return;
            }
            AutoFitSize(w, h);
        }

        // 自动尺寸：不小于 PreviewMinWidth，超限时等比收缩到最大占屏比例以内
        // （原图模式下超大图不再铺满整屏）
        private void AutoFitSize(int w, int h)
        {
            int chrome = ChromeHeight;
            if (w <= 0) w = Dpi.S(Config.PreviewMinWidth);
            if (h <= 0) h = (int)(w * 0.75);
            int minW = Dpi.S(Config.PreviewMinWidth);
            if (w < minW && w > 0) { h = (int)Math.Round(h * (double)minW / w); w = minW; }
            var wa = Screen.GetWorkingArea(this);
            double ratio = Math.Min(1, Math.Max(0.3, Config.PreviewMaxRatio));
            int maxW = Math.Max(Dpi.S(240), (int)(wa.Width * ratio));
            int maxH = Math.Max(Dpi.S(180), (int)(wa.Height * ratio) - chrome);
            if (w > maxW || h > maxH)
            {
                double k = Math.Min((double)maxW / w, (double)maxH / h);
                w = (int)(w * k); h = (int)(h * k);
            }
            if (w > wa.Width - Dpi.S(20)) w = wa.Width - Dpi.S(20);
            if (h > wa.Height - Dpi.S(20) - chrome) h = wa.Height - Dpi.S(20) - chrome;
            ClientSize = new Size(Math.Max(Dpi.S(120), w), Math.Max(Dpi.S(90) + chrome, h + chrome));
            Program.DebugLog("[pv-size] auto " + w + "x" + h + " -> " + ClientSize.Width + "x" + ClientSize.Height + " (chrome=" + chrome + ") max=" + maxW + "x" + maxH);
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
            int chrome = ChromeHeight;
            var wa = Screen.GetWorkingArea(this);
            int mw, mh;
            GetRememberedMediaSize(out mw, out mh);
            if (mw > 0 && mh > 0)
            {
                // remembered 存的就是窗口总尺寸（逻辑像素）
                int rw = Math.Max(Dpi.S(160), Math.Min(Dpi.S(mw), wa.Width - Dpi.S(20)));
                int rh = Math.Max(Dpi.S(120) + chrome, Math.Min(Dpi.S(mh), wa.Height - Dpi.S(20)));
                ClientSize = new Size(rw, rh);
                Program.DebugLog("[pv-size] media-remembered " + _mediaKind + " " + rw + "x" + rh + " (chrome=" + chrome + ")");
                return;
            }
            double ratio = Math.Min(1, Math.Max(0.3, Config.PreviewMaxRatio));
            int maxW = Math.Max(Dpi.S(240), (int)(wa.Width * ratio));
            int maxH = Math.Max(Dpi.S(180), (int)(wa.Height * ratio) - chrome);
            int cw = w, ch = h;
            if (cw > maxW || ch > maxH)
            {
                double k = Math.Min((double)maxW / cw, (double)maxH / ch);
                cw = (int)(cw * k); ch = (int)(ch * k);
            }
            if (cw > wa.Width - Dpi.S(20)) cw = wa.Width - Dpi.S(20);
            if (ch > wa.Height - Dpi.S(20) - chrome) ch = wa.Height - Dpi.S(20) - chrome;
            ClientSize = new Size(Math.Max(Dpi.S(160), cw), Math.Max(Dpi.S(120) + chrome, ch + chrome));
            Program.DebugLog("[pv-size] media-auto " + w + "x" + h + " -> " + ClientSize.Width + "x" + ClientSize.Height + " (chrome=" + chrome + ")");
        }

        // 记住用户调整后的尺寸（右下角手柄拖拽 / Ctrl+滚轮），延迟落盘避免频繁写配置
        private void RememberSize(int w, int h)
        {
            // 配置统一按逻辑像素存：换到不同缩放比的显示器上仍能还原相同的物理大小
            w = Dpi.UnS(w);
            h = Dpi.UnS(h);
            if (_content is PictureBox)
            {
                Config.PreviewImgW = w;
                Config.PreviewImgH = h;
            }
            else if (_mediaKind == "text")
            {
                // 文本预览独立一组，勿落到 SVG 的 PreviewMediaW/H
                Config.PreviewTextW = w;
                Config.PreviewTextH = h;
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
            int chrome = ChromeHeight;
            var wa = Screen.GetWorkingArea(this);
            double ratio = Math.Min(1, Math.Max(0.3, Config.PreviewMaxRatio));
            int maxW = Math.Max(240, (int)(wa.Width * ratio));
            int maxH = Math.Max(180, (int)(wa.Height * ratio) - chrome);
            int w = Math.Max(200, Math.Min(ClientSize.Width, maxW));
            int playBar = 30;
            int h = chrome + playBar + (int)Math.Round(w * (double)vh / vw);
            if (h > maxH)
            {
                h = maxH;
                w = Math.Max(200, (int)Math.Round((h - chrome - playBar) * (double)vw / vh));
            }
            if (h > wa.Height - 20) h = wa.Height - 20;
            ClientSize = new Size(w, Math.Max(120 + chrome, h));
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
            if (pb != null && pb.Image == null) return;
            // 统一按“当前窗口尺寸”缩放：以前图片按原始像素乘倍率，
            // 大图第一次 Ctrl+滚轮会直接放大到屏幕极限，表现成突然撑满
            newW = (int)(ClientSize.Width * factor);
            newH = (int)(ClientSize.Height * factor);
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

        // 用系统默认程序打开当前预览文件（左上角打开按钮）
        private void OpenWithDefaultApp()
        {
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;
            try { Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true }); }
            catch { }
        }

        // ===== 就地改名：点左上角 ✎ / 双击文件名 → 行内编辑框 =====
        // Enter 提交、Esc 取消；点编辑框外或失焦时"改过就保存、没改就安静退出"。
        // 提交走 PopupForm.RenamePath：释放被预览占用的句柄 → File.Move → 按新路径恢复内容与标题
        private TextBox _renameBox;
        private IMessageFilter _renameClickFilter;

        // Label/Panel 这类不可聚焦控件被点击不会触发 TextBox.LostFocus，
        // 所以用消息过滤器捕获窗口内任意鼠标按下，判断是否落在编辑框之外。
        private class RenameOutsideClickFilter : IMessageFilter
        {
            private readonly PreviewForm _f;
            public RenameOutsideClickFilter(PreviewForm f) { _f = f; }

            public bool PreFilterMessage(ref Message m)
            {
                try
                {
                    if (m.Msg != 0x201 && m.Msg != 0x204) return false;   // 左键 / 右键按下
                    var box = _f._renameBox;
                    if (box == null || box.IsDisposed) return false;
                    Control hit = Control.FromHandle(m.HWnd);
                    for (Control p = hit; p != null; p = p.Parent)
                        if (p == box || p == _f._renameBtn) return false;   // 编辑框内 / 重命名按钮本身
                    // 异步收起：避免在消息过滤器遍历过程中增删 filter，点击本身照常生效
                    _f.BeginInvoke((MethodInvoker)delegate { _f.EndRenameByOutsideClick(); });
                }
                catch { }
                return false;
            }
        }

        // 双击标题 = 打开所在文件夹。链接/文本卡的 FilePath 不是真实路径，安全忽略。
        internal void OpenContainingFolder()
        {
            if (string.IsNullOrEmpty(FilePath) || IsDisposed) return;
            try
            {
                string p = FilePath;
                string dir = Directory.Exists(p) ? p
                    : (File.Exists(p) ? System.IO.Path.GetDirectoryName(p) : null);
                if (string.IsNullOrEmpty(dir)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        }

        private void BeginFileRename()
        {
            if (_renameBox != null || string.IsNullOrEmpty(FilePath) || IsDisposed) return;
            string cur = System.IO.Path.GetFileName(FilePath);
            _renameBox = new TextBox
            {
                Font = new Font(Config.FontName, 9f),
                ForeColor = PvFg,
                BackColor = PvBg,
                BorderStyle = BorderStyle.FixedSingle,
                Text = cur,
            };
            // 覆在文件名标签的位置（窗体坐标，工具栏高度不够放编辑框）
            var scr = _toolbar.RectangleToScreen(_nameLbl.Bounds);
            var fb = RectangleToClient(scr);
            _renameBox.SetBounds(fb.X, fb.Y - 2, Math.Max(140, fb.Width + 8), fb.Height + 6);
            Controls.Add(_renameBox);
            _renameBox.BringToFront();
            _nameLbl.Visible = false;
            // 预览窗是 ShowWithoutActivation：不先 Activate，Focus() 只是"假焦点"，
            // 键盘消息仍归前台窗口，鼠标一动就 LostFocus，改名框刚出现就被收掉
            if (!Focused) Activate();
            _renameBox.Focus();
            int extLen = System.IO.Path.GetExtension(cur).Length;
            _renameBox.Select(0, Math.Max(0, cur.Length - extLen));   // 选中主名，扩展名不选（与 Explorer F2 一致）
            // 事件在拿到真焦点之后再挂，避免 Activate/Focus 过程中的焦点抖动误触发收起
            _renameBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndFileRename(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndFileRename(false); }
            };
            // 延迟订阅 LostFocus：避免双击标题时残余的 MouseUp/Click 事件触发 LostFocus → 退出改名
            var lfTimer = new System.Windows.Forms.Timer { Interval = 150 };
            lfTimer.Tick += (s, e) =>
            {
                lfTimer.Stop(); lfTimer.Dispose();
                if (_renameBox != null && !_renameBox.IsDisposed)
                    _renameBox.LostFocus += (s2, e2) => EndRenameByOutsideClick();
            };
            lfTimer.Start();
            _renameClickFilter = new RenameOutsideClickFilter(this);
            Application.AddMessageFilter(_renameClickFilter);
        }

        // 点别处 / 切到别的窗口：改过名字就保存，没改则安静退出
        private void EndRenameByOutsideClick()
        {
            var box = _renameBox;
            if (box == null || box.IsDisposed) return;
            string cur = "";
            try { cur = System.IO.Path.GetFileName(FilePath); } catch { }
            bool changed = !string.Equals(box.Text.Trim(), cur, StringComparison.Ordinal);
            EndFileRename(changed);
        }

        private void EndFileRename(bool commit)
        {
            if (_renameBox == null) return;
            if (_renameClickFilter != null)
            {
                try { Application.RemoveMessageFilter(_renameClickFilter); } catch { }
                _renameClickFilter = null;
            }
            var box = _renameBox; _renameBox = null;
            string nn = commit ? box.Text.Trim() : null;
            box.Dispose();
            if (_nameLbl != null && !_nameLbl.IsDisposed) _nameLbl.Visible = true;
            if (!commit || string.IsNullOrEmpty(nn)) return;
            if (string.Equals(nn, System.IO.Path.GetFileName(FilePath), StringComparison.Ordinal)) return;
            if (!PopupForm.RenamePath(FilePath, nn)) ShowToast(I18n.T("rename_fail"));
            // 成功时 RenamePath → RestorePreviewOf 已按新路径恢复内容并刷新标题（含 FilePath）
        }

        internal void SwitchFile(int dir)
        {
            if (string.IsNullOrEmpty(FilePath)) return;            string dir0 = Path.GetDirectoryName(FilePath);
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
                // 媒体面板/文本预览只在 Ctrl+滚轮（缩放窗口）时拦截，普通滚轮交还给控件
                if (!isPic && !(ctrl0 && (_form._content is Panel || _form._mediaKind == "text"))) return false;
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
            int btnY = Dpi.S(8);
            _pinBtn.Location = new Point(_toolbar.Width - _pinBtn.Width - Dpi.S(4), btnY);
            if (_audio != null)
            {
                // 音频操作栏：播放键 | 波形（占满中段，seek/移窗）| 倍速 | 固定此窗口 | 关闭
                // ☰ 文件夹列表按钮已移到下方 head（进入文件夹播放模式后显示）
                _audioPlayBtn.Location = new Point(Dpi.S(4), Dpi.S(2));
                int vx = Dpi.S(38);
                int vw = _pinBtn.Left - vx - Dpi.S(6);

                if (_audioCloseBtn != null)
                    _audioCloseBtn.Location = new Point(_toolbar.Width - _audioCloseBtn.Width - Dpi.S(2), btnY);
                if (vw < Dpi.S(40)) vw = Dpi.S(40);
                _audioViz.Location = new Point(vx, Dpi.S(8));
                _audioViz.Size = new Size(vw, Dpi.S(18));
                if (_spectrumViz != null)
                {
                    _spectrumViz.Location = new Point(vx, Dpi.S(6));
                    _spectrumViz.Size = new Size(vw, Dpi.S(22));
                }
                if (_audioProgress != null)
                {
                    _audioProgress.Location = new Point(vx, Dpi.S(8));
                    _audioProgress.Size = new Size(vw, Dpi.S(18));
                }
                UpdateAudioInfoLayout();
                return;
            }
            _pinLaunchBtn.Location = new Point(_pinBtn.Left - _pinLaunchBtn.Width - Dpi.S(2), btnY);
            int copyX = _pinLaunchBtn.Left - _copyBtn.Width - Dpi.S(2);
            _copyBtn.Location = new Point(copyX, btnY);
            // 信息栏接管复制操作后右上复制图标隐藏：把省下的空间让给标题/meta，
            // 保存按钮也顺势左移到 pinLaunch 旁边，避免留一块空白
            bool copyShown = _copyBtn.Visible;
            int anchor = copyShown ? copyX : _pinLaunchBtn.Left;
            if (_saveBtn.Visible)
            {
                _saveBtn.Location = new Point(anchor - _saveBtn.Width - Dpi.S(2), btnY);
                anchor = _saveBtn.Left;
            }
            // 备注块模式的"源码/块视图"切换：编辑/打开按钮隐藏时靠最左，否则在其右侧
            if (_sourceToggle != null)
            {
                int sx = (_renameBtn != null && _renameBtn.Visible) ? Dpi.S(56) : Dpi.S(6);
                _sourceToggle.Location = new Point(sx, btnY + Dpi.S(2));
            }

            // 标题起点：源码按钮右侧 → 编辑/打开按钮 → 最左留白
            int nameX = (_sourceToggle != null && _sourceToggle.Visible)
                ? _sourceToggle.Right + Dpi.S(8)
                : ((_renameBtn != null && _renameBtn.Visible) ? Dpi.S(52) : Dpi.S(8));
            using (var g = _metaLbl.CreateGraphics())
            {
                var sz = g.MeasureString(_metaLbl.Text, _metaLbl.Font);
                int metaW = _metaLbl.Visible ? Math.Min((int)sz.Width + Dpi.S(6), anchor - nameX - Dpi.S(4)) : 0;
                if (metaW < 0) metaW = 0;
                _metaLbl.Size = new Size(metaW, Dpi.S(18));
                _metaLbl.Location = new Point(anchor - metaW - Dpi.S(4), btnY + Dpi.S(1));
            }
            int nameW = _metaLbl.Left - nameX - Dpi.S(2);
            if (nameW < 0) nameW = 0;
            _nameLbl.Location = new Point(nameX, btnY + Dpi.S(1));
            _nameLbl.Size = new Size(nameW, Dpi.S(18));
            RefreshPinLaunchIcon();
        }

        // textcard 预览：隐藏复制按钮（复制文件/复制路径对文本无意义）
        private bool _textCardSaveHooked;
        public void HideCopyForTextCard()
        {
            _textCardMode = true;
            _copyBtn.Visible = false;   // 复制文件/复制路径对文本无意义，暂不提供粘贴按钮
            // 文本卡片没有对应的真实文件，重命名/打开按钮无意义
            _renameBtn.Visible = false;
            _openBtn.Visible = false;
            // 文字卡片编辑后必须能看到「保存」按钮：Control 构造（textcard）没有像
            // 纯文本构造那样绑定 TextChanged → 显示保存按钮，这里补上，否则用户改完字
            // 没有任何可见的保存入口，只能靠关窗兜底，一旦关窗路径不触发就表现为「没保存」。
            if (!_textCardSaveHooked && _content is TextBoxBase)
            {
                _textCardSaveHooked = true;
                _content.TextChanged += delegate
                {
                    if (!_textDirty) { _textDirty = true; _saveBtn.Visible = true; UpdateToolbarBtnPos(); }
                };
            }
            ApplyInfoBar();   // 信息栏复制组同步收掉"文件、路径"两项
            UpdateToolbarBtnPos();
        }

        public void SetHeader(string name, string meta)
        {
            _mediaSpec = "";   // 切换到别的文件时清掉上一个视频的规格文字
            _nameLbl.Text = name;
            _metaLbl.Text = meta;
            if (_audioNameLbl != null) _audioNameLbl.Text = name;   // 展开信息区：文件名
            if (_audioMetaLbl != null) _audioMetaLbl.Text = meta;   // 展开信息区：格式·大小
            // V4 chip：从 meta（"M4A · 5.6 MB"）拆出格式与大小
            if (_chipFormat != null || _chipSize != null)
            {
                string fmt = "", size = "";
                if (!string.IsNullOrEmpty(meta))
                {
                    string[] parts = meta.Split(new[] { '·', '•' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0) fmt = parts[0].Trim();
                    if (parts.Length > 1) size = parts[1].Trim();
                }
                if (_chipFormat != null) _chipFormat.Text = fmt;
                if (_chipSize != null) _chipSize.Text = size;
            }
            // 标题不再显示 hover tooltip：窗口信息栏/底部已经显示文件名，重复提示多余且干扰。
            // 清掉构造期可能登记过的拖动提示，避免标题悬停仍弹出任何气泡。
            Tips.Set(_nameLbl, "");
            if (_audioNameLbl != null) Tips.Set(_audioNameLbl, "");
            // 设置 meta 文本后必须重算 _metaLbl 的宽高与位置，
            // 否则沿用构造期(空文本)算出的 6px 宽，内容被 AutoEllipsis 截断不可见
            UpdateToolbarBtnPos();
            // 音频标题换行后高度可能变化，展开时按实际行数重算窗口高度
            if (_audio != null && _audioExpanded && IsHandleCreated) UpdateAudioHeight();
            ApplyInfoBar();   // 切换文件/改标题时同步信息栏与按钮文案
        }

        // ---- 信息 + 操作栏 ----

        private bool InfoBarVisible()
        {
            return Config.PreviewInfoBar && _audio == null && _infoBar != null;
        }

        private void BuildInfoBar()
        {
            _infoBar = new Panel { BackColor = PvBg, Height = InfoBarH, Visible = false };
            // 底部一条极细分隔线，与内容区分开（颜色取列表配色混出的低对比线）
            _infoBar.Paint += (s, e) =>
            {
                using (var pen = new Pen(PvBd))
                    e.Graphics.DrawLine(pen, 0, InfoBarH - 1, _infoBar.Width, InfoBarH - 1);
            };
            Controls.Add(_infoBar);

            _infoLbl = new Label
            {
                AutoEllipsis = true,
                ForeColor = PvMu,
                BackColor = PvBg,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _infoBar.Controls.Add(_infoLbl);

            // 原型方案 4「并排合并」：复制按钮合成一组，共用一条圆角外框，组内 gap 0，
            // 按钮之间只留一条淡色细分隔线；不再显示「复制」前缀文字。
            _ibPrimary = MakeInfoButton();
            _ibSecond = MakeInfoButton();
            _ibThird = MakeInfoButton();
            _ibSelected = MakeInfoButton();
            _ibMore = MakeInfoButton();
            // 定位按钮：独立图标，不与复制按钮组合
            _ibLocate = new Label
            {
                AutoSize = false,
                Size = Dpi.Sz(20, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = PvBg,
                Cursor = Cursors.Hand,
                Image = LocateIcon(PvFg),
            };
            _ibLocate.MouseEnter += (s, e) => _ibLocate.BackColor = PvHv;
            _ibLocate.MouseLeave += (s, e) => _ibLocate.BackColor = PvBg;
            _ibMore.Text = "⋯";
            Tips.Set(_ibMore, I18n.T("ib_more"));
            _ibMore.Click += (s, e) => ShowCopyMenuAt(_ibMore.PointToScreen(new Point(0, _ibMore.Height + 2)));
            _ibPrimary.Click += (s, e) => PrimaryCopy(_ibPrimary);
            _ibSecond.Click += (s, e) => CopyFileAction(_ibSecond);
            _ibThird.Click += (s, e) => CopyPathAction(_ibThird);
            _ibLocate.Click += (s, e) => OpenContainingFolder();
            Tips.Set(_ibLocate, I18n.T("ib_locate_tip"));
            // 文本预览里有选区时插入「选中」，复制选中片段
            _ibSelected.Click += (s, e) =>
            {
                var tb = _content as TextBoxBase;
                if (tb == null || tb.SelectionLength == 0) return;
                try { Clipboard.SetText(tb.SelectedText); Feedback(_ibSelected, "ib_word_selected"); } catch { }
            };
            foreach (Label l in new[] { _ibPrimary, _ibSecond, _ibThird,
                                        _ibSelected, _ibMore })
                _infoBar.Controls.Add(l);
            _infoBar.Controls.Add(_ibLocate);

        }

        private Label MakeInfoButton()
        {
            var b = new Label
            {
                AutoSize = false,
                Height = InfoBarH - 6,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = PvBg,
                ForeColor = PvFg,
                Cursor = Cursors.Hand,
            };
            // 原型 :hover / :active：悬停淡背景、按下整块变列表强调色，鼠标离开复原
            b.MouseEnter += (s, e) =>
            {
                b.BackColor = PvHv; b.Invalidate();
                string t = Tips.TextOf(b);
                if (Config.ShowTips && !string.IsNullOrEmpty(t))
                {
                    Program.DebugLog("[tip] ib-enter text=" + t);
                    // 定时提示免疫窗口 z-order / 菜单开合带来的假 MouseLeave 秒关
                    Tips.ShowBarTimed(this, t, 2000);
                }
            };
            b.MouseDown += (s, e) => { b.BackColor = Config.ListSel; b.ForeColor = Config.ListSelFg; b.Invalidate(); };
            b.MouseUp += (s, e) => { b.BackColor = PvHv; b.ForeColor = PvFg; b.Invalidate(); };
            b.MouseLeave += (s, e) =>
            {
                b.BackColor = PvBg; b.ForeColor = PvFg; b.Invalidate();
                Tips.EndTimedTip(this, Tips.TextOf(b));
            };
            // 组内位置由 LayoutInfoBar 写进 Tag：int[]{ 序号, 总数 }。
            // 边框画在背景之上，圆角弧线不会被按钮矩形背景遮住。
            b.Paint += (s, e) => DrawInfoButtonBorder(b, e.Graphics);
            return b;
        }

        // 合并组外框：每个按钮画自己那一段（上/下边 + 首按钮左圆角 + 末按钮右圆角），
        // gap=0 拼成一整条框；非末按钮右缘再画一条淡色分隔线。
        private static void DrawInfoButtonBorder(Label b, Graphics g)
        {
            Rectangle r = b.ClientRectangle;
            r.Width -= 1; r.Height -= 1;
            if (r.Width < 6 || r.Height < 6) return;
            int[] pos = b.Tag as int[];
            int idx = pos != null && pos.Length > 0 ? pos[0] : 0;
            int cnt = pos != null && pos.Length > 1 ? pos[1] : 1;
            bool first = idx == 0, last = idx == cnt - 1;
            int rad = Math.Min(6, Math.Min(r.Width, r.Height) / 2);
            int d = rad * 2;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // 圆角外侧不该露出按钮的矩形背景（hover/按下时尤其明显）：
            // 先把四个角补成信息栏底色，再把圆内 1/4 补回按钮背景色，最后画边框线。
            if (first || last)
            {
                using (var outer = new SolidBrush(PvBg))
                using (var inner = new SolidBrush(b.BackColor))
                {
                    if (first)
                    {
                        g.FillRectangle(outer, r.X, r.Y, rad, rad);
                        g.FillRectangle(outer, r.X, r.Bottom - rad + 1, rad, rad);
                        g.FillPie(inner, r.X, r.Y, d, d, 180, 90);
                        g.FillPie(inner, r.X, r.Bottom - d, d, d, 90, 90);
                    }
                    if (last)
                    {
                        g.FillRectangle(outer, r.Right - rad + 1, r.Y, rad, rad);
                        g.FillRectangle(outer, r.Right - rad + 1, r.Bottom - rad + 1, rad, rad);
                        g.FillPie(inner, r.Right - d, r.Y, d, d, 270, 90);
                        g.FillPie(inner, r.Right - d, r.Bottom - d, d, d, 0, 90);
                    }
                }
            }
            using (var pen = new Pen(Config.ListBd))
            {
                int x0 = first ? r.X + rad : r.X;
                int x1 = last ? r.Right - rad : r.Right;
                g.DrawLine(pen, x0, r.Y, x1, r.Y);
                g.DrawLine(pen, x0, r.Bottom, x1, r.Bottom);
                if (first)
                {
                    g.DrawArc(pen, r.X, r.Y, d, d, 180, 90);
                    g.DrawArc(pen, r.X, r.Bottom - d, d, d, 90, 90);
                    g.DrawLine(pen, r.X, r.Y + rad, r.X, r.Bottom - rad);
                }
                if (last)
                {
                    g.DrawArc(pen, r.Right - d, r.Y, d, d, 270, 90);
                    g.DrawArc(pen, r.Right - d, r.Bottom - d, d, d, 0, 90);
                    g.DrawLine(pen, r.Right, r.Y + rad, r.Right, r.Bottom - rad);
                }
            }
            if (!last)
            {
                Color bd = Config.ListBd;
                using (var sep = new Pen(Color.FromArgb(90, bd.R, bd.G, bd.B)))
                    g.DrawLine(sep, r.Right, r.Y + 1, r.Right, r.Bottom - 1);
            }
        }

        // 左侧信息项 + 右侧文字链文案；内容切换时跟着刷新
        private void ApplyInfoBar()
        {
            if (_infoBar == null) return;
            bool on = InfoBarVisible();
            _infoBar.Visible = on;
            // 信息栏接管复制操作后，标题栏右上角原来的复制图标让位；
            // 音频预览有自己的工具栏，不要动它的按钮可见性
            if (_copyBtn != null && _audio == null) _copyBtn.Visible = !on && !_textCardMode;
            // 格式/大小已并入信息栏，标题栏右上角的原格式信息让位（音频有自己的工具栏）
            if (_audio == null) _metaLbl.Visible = !on;
            if (IsHandleCreated) UpdateToolbarBtnPos();
            if (!on) return;

            string ext = "", size = "", extra = "", when = "";
            try
            {
                if (!string.IsNullOrEmpty(FilePath))
                {
                    ext = Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists)
                    {
                        size = Config.FmtSize(fi.Length);
                        when = fi.LastWriteTime.ToString("MM-dd HH:mm");
                    }
                }
                var rtb = _content as TextBoxBase;
                if (rtb != null) extra = string.Format(I18n.T("ib_lines"), rtb.Lines.Length);
                else if (_img != null) extra = _img.Width + "×" + _img.Height;
            }
            catch { }
            var parts = new List<string>();
            if (ext.Length > 0) parts.Add(ext);
            if (size.Length > 0) parts.Add(size);
            if (extra.Length > 0) parts.Add(extra);
            if (when.Length > 0) parts.Add(when);
            _infoBaseText = string.Join(" · ", parts.ToArray());
            _infoLbl.Text = _infoBaseText;

            bool isText = _content is TextBoxBase;
            bool isImage = _img != null;
            _ibPrimary.Text = isText ? I18n.T("ib_word_text")
                : (isImage ? I18n.T("ib_word_image") : I18n.T("ib_word_file"));
            _ibSecond.Text = I18n.T("ib_word_file");
            _ibThird.Text = I18n.T("ib_word_path");
            _ibSelected.Text = I18n.T("ib_word_selected");

            // 信息栏接管复制后，右上 _copyBtn 隐藏；这排按钮才是用户实际悬停的复制按钮，
            // 必须随文案（文本/图片/文件…）同步登记各自的提示。
            Tips.Set(_ibPrimary, I18n.T("ib_copy_fmt", _ibPrimary.Text));
            Tips.Set(_ibSecond, I18n.T("ib_copy_fmt", _ibSecond.Text));
            Tips.Set(_ibThird, I18n.T("ib_copy_fmt", _ibThird.Text));
            Tips.Set(_ibSelected, I18n.T("ib_copy_fmt", _ibSelected.Text));
            Tips.Set(_ibMore, I18n.T("ib_more"));
            Tips.Set(_ibLocate, I18n.T("ib_locate_tip"));
            LayoutInfoBar();
        }

        // 原型方案 4：按钮并排合并成一组，共用圆角外框，组内间距 0；
        // 字号/内边距固定，不随窗口缩放；宽度不够只让左侧信息文本让位。
        private void LayoutInfoBar()
        {
            if (_infoBar == null || !_infoBar.Visible || !IsHandleCreated) return;
            int w = _infoBar.Width > 0 ? _infoBar.Width : ClientSize.Width;
            if (w <= 0) return;
            float infoPt = Math.Max(6f, Math.Min(24f, Config.PreviewInfoBarFontMax));
            string fam = string.IsNullOrEmpty(Config.PreviewInfoBarFontName)
                ? Config.FontName : Config.PreviewInfoBarFontName;
            var all = new[] { _ibPrimary, _ibSecond, _ibThird, _ibSelected, _ibMore };
            if (Math.Abs(infoPt - _ibMaxPt) > 0.01f
                || !string.Equals(fam, _ibFontName, StringComparison.OrdinalIgnoreCase))
            {
                _ibMaxPt = infoPt;
                _ibFontName = fam;
                SetScaledFont(_infoLbl, infoPt, FontStyle.Regular, fam);
                foreach (Label b in all) SetScaledFont(b, infoPt, FontStyle.Regular, fam);
            }

            const int Gap = 0;    // 合并组：按钮之间不留空隙，只画分隔线
            int Pad = Dpi.S(12);   // 原型 v4 的 padding: 1px 6px → 左右各 6px
            // 非文本/图片时主按钮已经是"文件"，不再重复一个"文件"链；其余一律显示
            bool second = !string.Equals(_ibPrimary.Text, _ibSecond.Text, StringComparison.Ordinal);
            var tb = _content as TextBoxBase;
            bool hasSelection = tb != null && tb.SelectionLength > 0;
            // 文字卡片没有真实文件：复制按钮里只保留"文本/选中"，去掉"文件、路径"
            var chain = new List<Label> { _ibPrimary };
            if (hasSelection) chain.Add(_ibSelected);
            if (second && !_textCardMode) chain.Add(_ibSecond);
            if (!_textCardMode) chain.Add(_ibThird);


            int[] cw = new int[chain.Count];
            int total = Math.Max(0, chain.Count - 1) * Gap;
            for (int i = 0; i < chain.Count; i++)
            {
                cw[i] = TextRenderer.MeasureText(chain[i].Text, chain[i].Font).Width + Pad;
                total += cw[i];
            }
            foreach (Label b in all)
            {
                b.Visible = chain.Contains(b);
                b.Tag = new int[] { 0, 1 };   // 隐藏按钮的组内位置复位
            }

            int y = Dpi.S(3);
            // 极窄窗口允许整组左移（左端可能被裁），右侧复制按钮仍完整可见
            int groupLeft = w - Dpi.S(8) - total;
            int x = groupLeft;
            for (int i = 0; i < chain.Count; i++)
            {
                chain[i].Tag = new int[] { i, chain.Count };
                chain[i].Size = new Size(cw[i], InfoBarH - Dpi.S(6));
                chain[i].Location = new Point(x, y);
                x += cw[i] + Gap;
                chain[i].Invalidate();   // 合并外框跟随新尺寸/新配色重绘
            }

            _infoLbl.Location = new Point(Dpi.S(8), 0);
            int infoW = Math.Max(0, groupLeft - Dpi.S(12));
            _infoLbl.Size = new Size(infoW, InfoBarH - 1);
            // 定位按钮：独立图标，放在复制按钮组左侧（留 6px 间隔）
            _ibLocate.Visible = !_textCardMode;
            if (_ibLocate.Visible)
            {
                _ibLocate.Size = Dpi.Sz(20, 20);
                _ibLocate.Location = new Point(groupLeft - Dpi.S(26), Dpi.S(4));
                _ibLocate.BringToFront();
            }
            // 视频规格角标：宽度够才并入，否则只显示基础信息
            string text = _infoBaseText;
            if (!string.IsNullOrEmpty(_mediaSpec))
            {
                string full = _infoBaseText + " · " + _mediaSpec;
                if (infoW > 20 && TextRenderer.MeasureText(full, _infoLbl.Font).Width <= infoW - 4)
                    text = full;
            }
            _infoLbl.Text = text;
            UpdateToolbarBtnPos();
        }

        // 拖选松手 / Ctrl+A / 方向键改变选区时，操作栏插入或移除「选中」按钮
        private void RefreshSelectionButtons()
        {
            if (_infoBar == null || !InfoBarVisible() || !IsHandleCreated) return;
            LayoutInfoBar();
        }

        private static void SetScaledFont(Label l, float size, FontStyle style, string family)
        {
            // 不要 Dispose 旧 Font：初始 Font 通常是 Control.DefaultFont 这类共享静态实例，
            // Dispose 了会导致后续新控件（TextBox/RichTextBox）继承到已释放的 Font 而崩溃。
            // 让 Label/Form 的 Dispose 统一处理即可。
            try { l.Font = new Font(family, size, style); }
            catch
            {
                try { l.Font = new Font(Config.FontName, size, style); } catch { }
            }
        }

        private void PrimaryCopy(Label btn)
        {
            try
            {
                var rtb = _content as TextBoxBase;
                if (rtb != null) { Clipboard.SetText(rtb.Text); Feedback(btn, "ib_word_text"); return; }
                if (_img != null) { Clipboard.SetImage(_img); Feedback(btn, "ib_word_image"); return; }
                CopyFileAction(btn);
            }
            catch { }
        }

        private void CopyFileAction(Label btn)
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            try
            {
                var files = new System.Collections.Specialized.StringCollection();
                files.Add(FilePath);
                Clipboard.SetFileDropList(files);
                Feedback(btn, "ib_word_file");
            }
            catch { }
        }

        private void CopyPathAction(Label btn)
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            try { Clipboard.SetText(FilePath); Feedback(btn, "ib_word_path"); } catch { }
        }

        // 复制反馈：按钮短暂显示 √，1000ms 后还原；窗口底部同时显示"已复制 X"，同样 1000ms 后收起。
        // 只改 Text/Image、不动颜色 → 不会出现强调色色块；
        // 还原时若文字已被外部（信息栏重排）改写，则只还原图标不覆盖文字 → 不会"文字消失"。
        private Label _fbBtn;
        private Image _fbImage;
        private string _fbText;
        private ContentAlignment _fbAlign;
        private Timer _fbTimer;

        private void Feedback(Label btn, string wordKey = "ib_word_file")
        {
            if (IsDisposed) return;
            Tips.ShowBarTimed(this, I18n.T("ib_copied_fmt", I18n.T(wordKey)), 1000);

            RestoreFeedback();                 // 连续复制先还原上一次，避免快照串味
            var target = FeedbackTarget(btn);
            if (target == null) return;
            _fbBtn = target;
            _fbImage = target.Image;
            _fbText = target.Text;
            _fbAlign = target.TextAlign;
            target.Image = null;
            target.Text = "√";
            target.TextAlign = ContentAlignment.MiddleCenter;
            _fbTimer = new Timer { Interval = 1000 };
            _fbTimer.Tick += (s, e) => RestoreFeedback();
            _fbTimer.Start();
        }

        // 复制菜单常从信息栏"⋯"弹出，此时右上角 _copyBtn 是隐藏的：挑一个可见按钮来显示 √
        private Label FeedbackTarget(Label btn)
        {
            if (btn != null && !btn.IsDisposed && btn.Visible) return btn;
            if (_ibMore != null && !_ibMore.IsDisposed && _ibMore.Visible) return _ibMore;
            if (_copyBtn != null && !_copyBtn.IsDisposed && _copyBtn.Visible) return _copyBtn;
            return null;
        }

        private void RestoreFeedback()
        {
            if (_fbTimer != null) { _fbTimer.Stop(); _fbTimer.Dispose(); _fbTimer = null; }
            var b = _fbBtn;
            var img = _fbImage;
            var text = _fbText;
            var align = _fbAlign;
            _fbBtn = null; _fbImage = null; _fbText = null;
            if (b == null || b.IsDisposed) return;
            // 图标/对齐是结构属性，外部重排不会改 → 始终还原
            if (b.Image == null && img != null) b.Image = img;
            b.TextAlign = align;
            if (b.Text != "√") return;          // 外部已改写文字 → 不覆盖成旧快照，避免"文字消失"
            b.Text = text;
        }

        // 视频预览左上角规格角标文字同步过来；宽度够时由 LayoutInfoBar 并入信息栏
        internal void SetMediaSpec(string spec)
        {
            _mediaSpec = spec ?? "";
            if (InfoBarVisible() && IsHandleCreated) ApplyInfoBar();
        }

        // 设置项切换时刷新已打开的预览窗
        internal void RefreshInfoBar()
        {
            if (IsDisposed || _infoBar == null) return;
            ApplyInfoBar();
            if (IsHandleCreated) ApplyLayout();
            _infoBar.Invalidate();
        }

        // 首次 Show 时尺寸未变，Resize 不会触发：信息栏会一直保持构造期的默认位置
        // （按钮文字挤在左上角），要手动调整窗口尺寸才正常。显示时主动布局一次。
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshInfoBar();
        }

        private void ApplyLayout()
        {

            if (!IsHandleCreated) return;
            int rulerH = 0;   // 倍速尺已并入音频信息区，不再单独占行（由 UpdateAudioInfoLayout 定位）
            _toolbar.Location = new Point(0, 0);
            _toolbar.Size = new Size(ClientSize.Width, _toolbarH);
            int infoH = 0;
            if (_infoBar != null)
            {
                bool on = InfoBarVisible();
                _infoBar.Visible = on;
                infoH = on ? InfoBarH : 0;
                _infoBar.Location = new Point(0, _toolbarH + rulerH);
                _infoBar.Size = new Size(ClientSize.Width, InfoBarH);
            }
            int hintH = (_linkHint != null) ? _linkHintH : 0;
            if (_linkHint != null)
            {
                _linkHint.Location = new Point(0, ClientSize.Height - hintH);
                _linkHint.Size = new Size(ClientSize.Width, hintH);
                _linkHint.BringToFront();
            }
            _scrollPanel.Location = new Point(0, _toolbarH + rulerH + infoH);
            _scrollPanel.Size = new Size(ClientSize.Width, Math.Max(0, ClientSize.Height - _toolbarH - rulerH - infoH - hintH));
            // 右下角手柄：只给静态内容（图片/文本/SVG）画，视频/音频画面不压角标
            if (_grip != null)
            {
                bool showGrip = _content is PictureBox;   // 只画在图片上：文本会挡住滚动条、视频会压住画面
                _grip.Visible = showGrip;
                if (showGrip)
                {
                    _grip.Location = new Point(Math.Max(0, ClientSize.Width - 13), Math.Max(0, ClientSize.Height - 13));
                    _grip.BringToFront();
                }
            }
            if (infoH > 0) LayoutInfoBar();
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

            if (_folderTipTimer != null) { _folderTipTimer.Stop(); _folderTipTimer.Dispose(); _folderTipTimer = null; }
            Tips.HideBar(this);
            if (_waveDec != null) { _waveDec.Cancel(); _waveDec = null; }
            if (_audioMeter != null) { _audioMeter.Dispose(); _audioMeter = null; }
            // V5 封面位图：预览独占，关窗即释放（PictureBox 不自动 Dispose 其 Image）
            if (_audioCoverImg != null) { _audioCoverImg.Dispose(); _audioCoverImg = null; }
            // 音频播放器是挂在 hostPanel.Tag 上的 COM 对象，窗体 Dispose 不会自动调其
            // Dispose→Stop，窗口关了声音还在播；必须显式停止，否则鼠标离开后声音拖好几秒
            if (_audio != null) { try { _audio.Close(); } catch { } _audio = null; }
            base.OnFormClosed(e);
        }

        private void TogglePin()
        {
            _pinned = !_pinned;
            RefreshPinIcon();
            if (_pinned)
            {
                // 重新固定：必须终止"取消固定后离开即关"的计时器，否则再固定后鼠标一离开仍会关闭
                if (_unpinCloseTimer != null) { _unpinCloseTimer.Stop(); _unpinCloseTimer.Dispose(); _unpinCloseTimer = null; }
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
            if (disposing)
            {
                StopGif();
                if (_img != null) { _img.Dispose(); _img = null; }
            }
            base.Dispose(disposing);
        }

        // 固定按钮仅按"是否已固定"绘制（实心=已固定，空心=未固定），不再随窗口激活变色。

        internal void RefreshPinIcon()
        {
            if (_pinBtn == null || _pinBtn.IsDisposed) return;
            SetPinIcon(_pinBtn, _pinned, PvFg);
        }

        // 预览窗内参与全局 hint 的按钮：重命名 / 打开 / 复制 / 固定 / 信息栏复制组等
        internal void HintCollect(List<HintItem> list, bool copyMode)
        {
            if (list == null || IsDisposed || !Visible) return;
            Control[] btns = { _renameBtn, _openBtn, _copyBtn, _saveBtn,
                               _pinBtn, _pinLaunchBtn, _audioPlayBtn,
                               _ibPrimary, _ibSecond, _ibThird, _ibSelected, _ibMore, _ibLocate };
            foreach (var c in btns) PopupForm.AddHintButton(list, c);
        }

        internal static Image PinIcon(bool pinned)
        {
            return PinIcon(pinned, PvFg);
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

        // 固定按钮图标：仅按"是否已固定"绘制（实心=已固定，空心=未固定）；不再随窗口激活变色。
        // 预览窗与列表面板共用同一实现，避免各自为政。
        internal static void SetPinIcon(Label btn, bool pinned, Color fg)
        {
            if (btn == null || btn.IsDisposed) return;
            btn.Image = PinIcon(pinned, fg);
        }

        internal static Image PinLaunchIcon(bool active)
        {
            return PinLaunchIcon(active, PvFg);
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
                    var col = active ? Config.ListSel : c;
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
            string kind = string.IsNullOrEmpty(PinKind) ? "file" : PinKind;
            bool active = !string.IsNullOrEmpty(FilePath) && Program.IsPinned(kind, FilePath);
            _pinLaunchBtn.Image = PinLaunchIcon(active);
            _pinLaunchBtn.Invalidate();
        }

        // 固定为桌面卡片：文字卡片预览按 text 类型重建卡片（Kind=file + 文本内容会被当路径）
        private void TogglePinToCard()
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            Program.TogglePinItem(new PinnedItem
            {
                Kind = string.IsNullOrEmpty(PinKind) ? "file" : PinKind,
                Path = FilePath,
                Title = PinTitle,
                X = Cursor.Position.X + 20,
                Y = Cursor.Position.Y + 20,
            });
            RefreshPinLaunchIcon();
        }

        internal static Image TrashIcon()
        {
            return TrashIcon(PvFg);
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
            return CopyIcon(PvFg);
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
            return EditIcon(PvFg);
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

        // 外部打开图标：方框缺右上角 + 右上斜箭头
        internal static Image OpenIcon(Color c)
        {
            return PopupForm.UiIcon("open_" + c.ToArgb(), () =>
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var p = new Pen(c, 1.4f))
                    {
                        g.DrawLine(p, 2, 5, 2, 14);
                        g.DrawLine(p, 2, 14, 10, 14);
                        g.DrawLine(p, 10, 14, 10, 10);
                        g.DrawLine(p, 6, 2, 14, 2);
                        g.DrawLine(p, 14, 2, 14, 10);
                        g.DrawLine(p, 14, 2, 8, 8);
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
                    using (var p = new Pen(PvFg, 1.4f))
                    using (var b = new SolidBrush(PvFg))
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
            ShowCopyMenuAt(_copyBtn.PointToScreen(new Point(0, _copyBtn.Height + 2)));
        }

        internal static Image LocateIcon(Color c)
        {
            return PopupForm.UiIcon("locate_" + c.ToArgb(), () =>
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var p = new Pen(c, 1.4f))
                    {
                        // 十字线
                        g.DrawLine(p, 8, 1, 8, 5);
                        g.DrawLine(p, 8, 11, 8, 15);
                        g.DrawLine(p, 1, 8, 5, 8);
                        g.DrawLine(p, 11, 8, 15, 8);
                        // 中心圆圈
                        g.DrawEllipse(p, 5.5f, 5.5f, 5, 5);
                    }
                }
                return bmp;
            });
        }

        private void ShowCopyMenuAt(Point at)
        {
            if (string.IsNullOrEmpty(FilePath) || Program.ShellMenuOpen) return;
            var m = new ContextMenuStrip { BackColor = Config.BG, ForeColor = Config.FG, Font = new Font(Config.FontName, Config.FontSize) };
            m.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            var rtb = _content as TextBoxBase;
            if (rtb != null && rtb.SelectionLength > 0)
                m.Items.Add(new ToolStripMenuItem(I18n.T("copy_selected"), null, (EventHandler)((s, e) => { try { Clipboard.SetText(rtb.SelectedText); Feedback(_copyBtn, "ib_word_selected"); } catch { } })));
            else if (rtb != null)
                m.Items.Add(new ToolStripMenuItem(I18n.T("copy_all"), null, (EventHandler)((s, e) => { try { Clipboard.SetText(rtb.Text); Feedback(_copyBtn, "ib_word_text"); } catch { } })));
            // SVG 预览带源码框（RichTextBox 不在 _content 顶层，上面两个分支取不到），
            // 单独补一项"复制代码"，直接读源文件——源码框超过 64KB 会被截断，不能作为来源
            if (IsSvgFile())
                m.Items.Add(new ToolStripMenuItem(I18n.T("copy_code"), null, (EventHandler)((s, e) => CopySvgCode())));
            m.Items.Add(new ToolStripMenuItem(I18n.T("copy_file"), null, (EventHandler)((s, e) => { try { var f = new System.Collections.Specialized.StringCollection(); f.Add(FilePath); Clipboard.SetFileDropList(f); Feedback(_copyBtn, "ib_word_file"); } catch { } })));
            m.Items.Add(new ToolStripMenuItem(I18n.T("copy_path"), null, (EventHandler)((s, e) => { try { Clipboard.SetText(FilePath); Feedback(_copyBtn, "ib_word_path"); } catch { } })));
            Program.ShellMenuOpen = true;
            m.Closed += (s, ev) =>
            {
                Program.ShellMenuOpen = false;
                if (_copyMenu == m) _copyMenu = null;
                // 菜单自身开合（光标仍在按钮上）不算离开，保留悬停提示
                if (!_copyBtn.IsDisposed
                    && _copyBtn.RectangleToScreen(_copyBtn.ClientRectangle).Contains(Cursor.Position)) return;
                // 只结束悬停提示，不误关点击菜单项后刚显示的"已复制X"定时反馈
                Tips.EndTimedTip(this, I18n.T("tip_copy_menu"));
            };
            // 鼠标移出菜单就直接收起：悬停菜单不该一直挂着
            m.MouseLeave += (s, ev) => m.Close();
            _copyMenu = m;
            m.Show(at);
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
                Feedback(_copyBtn, "ib_word_code");
                Program.DebugLog("[copy] svg code " + FilePath);
            }
            catch (Exception ex) { Program.DebugLog("[copy] svg code fail: " + ex.Message); }
        }

        private void CopyClick()
        {
            try
            {
                var rtb = _content as TextBoxBase;
                if (rtb != null && rtb.SelectionLength > 0)
                {
                    Clipboard.SetText(rtb.SelectedText);
                    Feedback(_copyBtn, "ib_word_selected");
                    return;
                }
                if (!string.IsNullOrEmpty(FilePath))
                {
                    var files = new System.Collections.Specialized.StringCollection();
                    files.Add(FilePath);
                    Clipboard.SetFileDropList(files);
                    Feedback(_copyBtn, "ib_word_file");
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
                BackColor = PvBg,
                ShowInTaskbar = false,
                TopMost = true,
                Size = new Size(60, 22),
            };
            var lbl = new Label { Text = msg, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = PvFg, BackColor = PvBg, Font = new Font("Microsoft YaHei", 9) };
            t.Controls.Add(lbl);
            t.Location = new Point(Left + (Width - t.Width) / 2, Top + 34);
            t.Show();
            var tm = new Timer { Interval = 800 };
            tm.Tick += (s, e) => { tm.Stop(); tm.Dispose(); t.Close(); t.Dispose(); };
            tm.Start();
        }

        // 文字卡片：暴露编辑框，供 PinCardForm 在关窗/Pinned 解除引用时读取最新文本写回。
        public TextBoxBase GetTextCardTextBox() { return _content as TextBoxBase; }

        // 文字卡片：把当前编辑框内容交给回调写回 PinnedItem。供保存按钮与关闭兜底共用。
        public void CommitTextCardEdit()
        {
            if (!_textCardMode || TextCardCommit == null || _content == null) return;
            string tc = _content is TextBoxBase ? ((TextBoxBase)_content).Text : _content.Text;
            TextCardCommit(StripSwatchSpaces(tc));
            _textDirty = false;
            if (_saveBtn != null) _saveBtn.Visible = false;
            UpdateToolbarBtnPos();
        }

        private void SaveClick()
        {
            try
            {
                // 桌面文字卡片：FilePath 装的是文本内容本身（不是磁盘路径），
                // 必须优先交给 TextCardCommit 回写 PinnedItem，否则会被当成路径写文件而失败。
                if (_textCardMode && TextCardCommit != null)
                {
                    CommitTextCardEdit();
                    return;
                }
                var rtb = _content as TextBoxBase;
                if (rtb != null && !string.IsNullOrEmpty(FilePath))
                {
                    // 色块是内联 RTF 图片，纯文本里每个图占一个空格位：写回前剔除，避免每次保存多出空格
                    string text = StripSwatchSpaces(rtb.Text);
                    System.IO.File.WriteAllText(FilePath, text, DetectFileEncoding(FilePath));
                    Program.DebugLog("[pv-save] ok " + FilePath + " chars=" + text.Length);
                    _textDirty = false;
                    _saveBtn.Visible = false;
                    UpdateToolbarBtnPos();
                }
                else if (_linkCommit != null && _notePanel != null)
                {
                    // 链接备注：从块模型序列化回 md（文本块 + ![](图片块)），首行仍是完整链接
                    string md = _content == _sourceBox ? _sourceBox.Text : _notePanel.GetMarkdown();
                    _linkCommit(md.Trim());
                    _textDirty = false;
                    _saveBtn.Visible = false;
                    UpdateToolbarBtnPos();
                }
                else Program.DebugLog("[pv-save] skip path=" + (FilePath ?? "null") + " content=" + (_content == null ? "null" : _content.GetType().Name));
            }
            catch (Exception ex) { Program.DebugLog("[pv-save] fail " + FilePath + " : " + ex.Message); }
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
        private Label _statusLbl;
        private Point _statusDownPos;
        private bool _statusDragging;
        // 顶部名称行：复制路径按钮 + 编辑图标（点击就地改名）+ 当前文件夹名
        private Panel _nameRow;
        private SmoothLabel _titleLbl;
        private SmoothLabel _countLbl;  // 名称行右侧的条目计数小字（格式标签多行时会盖住状态栏文字，故移到顶部）
        private SmoothLabel _resLbl;    // 计数右侧的 HE 资源占用（设置开启时显示，约 1 秒刷新）
        private Label _copyBtn;
        private Label _editBtn;
        private TextBox _nameEdit;
        private FlowLayoutPanel _sortFlow;
        private SlimScrollPanel _listPanel;
        private SlimScrollBar _listScrollBar;
        private bool _syncingScroll;   // 滑块↔内容层双向同步防回环（避免动画被 sb.Scroll 回调误杀）
        private static int SlimBarW { get { return Dpi.S(10); } }   // 自绘细滚动条占宽（行右侧让位）
        // 列表行区左右内缩/底部留白：与顶部名称栏、排序栏、底部状态栏的左右内缩(6)对齐，
        // 行不再顶到面板左右边缘与底边，视觉上与顶部呼应
        private static int ListPadLr { get { return Dpi.S(6); } }
        private static int ListPadBottom { get { return Dpi.S(6); } }
        private string _path;

        // 来源是文件对话框时记录对话框句柄：单击文件夹行 → 让对话框进入该文件夹；
        // 表头出现"填入对话框"按钮 → 把多选文件写进对话框文件名框
        internal IntPtr DialogHwnd;

        // 标题名称后缀（如从文件夹快捷方式跳来的面板显示 "lnk"）；由调用方在 ShowPanel 前设置
        internal string TitleSuffix;
        private int _headH;
        private int _panelW = 260;
        // 固定时记下自身尺寸：后续刷新沿用，避免条目少时被内容宽度或全局默认值改写
        private int _pinnedW, _pinnedH;
        // 进入移动/调整大小时的尺寸，用于区分"只是挪了位置"和"真的改了尺寸"
        private Size _sizeMoveStart;
        // 手动 resize：FormBorderStyle.None 下系统 HTBOTTOM sizing loop 会拖顶部，改手动保持 top/left 不变
        private bool _ppResize;
        private int _ppResizeMode, _ppResizeSX, _ppResizeSY, _ppResizeSW, _ppResizeSH;

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
        // 右侧预览触发带分隔线拖动中（带宽 = 行宽 - 线位置）
        private bool _bandDrag;
        // 上次 MouseMove 是否在右侧触发带内（仅跨线切换时调度/取消，避免带内微动反复触发闪烁）
        private bool _wasInBand;
        // 互换布局：左侧不触发悬停调度的安全区宽度（按钮组+大小文本列）
        private int _rowSideZone;
        // 当前显示行内操作按钮的行（拖触发带时用它实时重排按钮）
        private RowLabel _rowBtnRow;
        private int? _lastClickIdx;
        private bool _dragged;
        private Point _dragStart;
        private RowLabel _dragRow;
        private RowLabel _activeDirRow;
        private RowLabel _hlRow;
        private RowLabel _contextRow;
        private bool _pinned;
        private Timer _unpinCloseTimer;  // 取消固定后：光标离开面板即自动关闭（与预览窗 TogglePin 同语义）
        private int _lastUnpinTick;      // 最近一次取消固定时刻：短暂抑制"拖动即固定"，避免取消固定后顺手挪一下又被静默固定
        private Label _pinBtn;
        private Label _refreshBtn;  // 表头 ↻ 手动刷新（仅固定面板显示）
        private Label _fillDlgBtn;  // 表头"填入对话框"（仅从文件对话框打开的面板显示）
        private Label _pinCardBtn;   // 面板顶部"钉为桌面卡片"按钮
        private Label _rowDelBtn;
        private Label _rowCopyBtn;  // 行悬停时显示的"复制路径"按钮
        private Label _rowPinBtn;   // 行悬停时显示的"钉为桌面卡片"按钮
        private Label _rowRenameBtn; // 行悬停时显示的"重命名"按钮（复制按钮左侧）
        private static int RowBtnW { get { return Dpi.S(17); } }    // 方案 A：4 个按钮同尺寸（紧凑：仅比 16px 图标多 1px）
        private static int RowBtnH { get { return Dpi.S(18); } }
        private static int RowBtnGap { get { return Dpi.S(1); } }   // 紧凑排布：按钮之间只留 1px
        private static int RowBtnStep { get { return RowBtnW + RowBtnGap; } }
        private static int RowBtnInset { get { return Dpi.S(4); } } // 组距行缘 4px，避开行圆角

        private TextBox _rowEdit;   // 行内重命名编辑框（激活期间存在）
        private RowLabel _rowEditRow; // 正被行内重命名的行（Esc 取消时恢复显示）
        private readonly HashSet<string> _filterSelected = new HashSet<string>();
        private readonly HashSet<string> _filterExcluded = new HashSet<string>();
        private readonly List<Label> _filterBtns = new List<Label>();
        private int _statusCountW; // 状态栏左侧计数文字宽度（格式标签从其后排布）

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
            Win11.RemoveNcBorder(Handle);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            if (_resizeFreeze != null)
            {
                // 拖动全程冻结：只让快照盖满新客户区（扩边处露快照底色），不做逐行重排——
                // 逐帧 SyncWidth（逐行改宽+计数测量+筛选标签重排）叠加子控件异步重绘正是拖动卡顿根源
                _resizeFreeze.Bounds = new Rectangle(Point.Empty, ClientSize);
                return;
            }
            // 拖动右下角手柄改宽度后同步顶部与行宽度（只同步宽度，不重排行高）
            if (ClientSize.Width != _panelW) SyncWidth(ClientSize.Width);
            if (_pinBtn != null) _pinBtn.BringToFront();
            if (_pinCardBtn != null) _pinCardBtn.BringToFront();
        }

        private PictureBox _resizeFreeze;
        private Image _resizeFreezeImg;

        // 拖动尺寸时冻结列表：快照盖住客户区 + 隐藏几百行 Label（每次 WM_SIZE
        // 都要对全部子行重算布局+重绘，文件多时每帧都卡）。拖动全程系统只画这张静态图。
        // force=true 用于手动 resize：行数少也要冻结——卡顿不只来自行多，
        // 还来自每帧整窗重排/重绘与 DWM 合成未绘制新暴露区的黑条伪影
        private void _fnBeginResizeFreeze(bool force)
        {
            try
            {
                _fnEndResizeFreeze();
                if (_listPanel == null) return;
                if (!force && ((SlimScrollPanel)_listPanel).ContentCount <= 40) return;
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
            // 跨显示器/改缩放：取新 DPI 后按新比例重建布局（面板宽高、行高、按钮全部重算）
            if (m.Msg == (int)Dpi.WM_DPICHANGED && Handle != IntPtr.Zero)
            {
                Dpi.SetFromWindow(Handle);
                try { if (!string.IsNullOrEmpty(_path)) RefreshPanel(); } catch { }
            }
            // 圆角模式描边与自身背景同色：亮色描边压在深色配色上很突兀，
            // 同色后只剩 DWM 圆角外形（回调动态取 BackColor，主题/层级着色跟随）
            if (Win11.WndNcBorder(Handle, ref m, () => BackColor)) return;
            if (m.Msg == 0x84)   // WM_NCHITTEST
            {
                var lp = m.LParam.ToInt64();
                var sp = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                var wb = Bounds;
                int grip = Dpi.S(6);
                if (sp.X >= wb.Right - grip && sp.Y >= wb.Bottom - grip) { m.Result = (IntPtr)17; return; }
                if (sp.X >= wb.Right - grip && sp.Y >= wb.Top && sp.Y < wb.Bottom) { m.Result = (IntPtr)11; return; }
                if (sp.Y >= wb.Bottom - grip && sp.X >= wb.Left && sp.X < wb.Right) { m.Result = (IntPtr)12; return; }
            }
            else if (m.Msg == 0xA1)   // WM_NCLBUTTONDOWN：拦截系统 sizing loop，手动 resize
            {
                int ht = m.WParam.ToInt32();
                if (ht == 12 || ht == 11 || ht == 17)
                {
                    _ppResize = true;
                    _ppResizeMode = ht == 12 ? 1 : (ht == 11 ? 2 : 3);
                    var lp = m.LParam.ToInt64();
                    _ppResizeSX = (short)(lp & 0xFFFF);
                    _ppResizeSY = (short)((lp >> 16) & 0xFFFF);
                    _ppResizeSW = Width;
                    _ppResizeSH = Height;
                    _fnBeginResizeFreeze(true);
                    Capture = true;
                    return;
                }
            }
            else if (_ppResize && m.Msg == 0x200)   // WM_MOUSEMOVE
            {
                var lp = m.LParam.ToInt64();
                var sp = PointToScreen(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                // 单次 SetBounds 代替 Width/Height 各设一次（各触发一次 SetWindowPos/布局）
                int nw = (_ppResizeMode & 2) != 0 ? Math.Max(120, _ppResizeSW + (sp.X - _ppResizeSX)) : Width;
                int nh = (_ppResizeMode & 1) != 0 ? Math.Max(60, _ppResizeSH + (sp.Y - _ppResizeSY)) : Height;
                if (nw != Width || nh != Height)
                {
                    SetBounds(Location.X, Location.Y, nw, nh);
                    // 同步刷出快照：DWM 永远合成已绘制的帧，消除扩边处黑条伪影
                    if (_resizeFreeze != null) _resizeFreeze.Update();
                }
                return;
            }
            else if (_ppResize && m.Msg == 0x202)   // WM_LBUTTONUP
            {
                _ppResize = false;
                Capture = false;
                _fnEndResizeFreeze();
                // 冻结期间 OnResize 跳过了 SyncWidth，松手后按最终宽度一次重排到位
                if (ClientSize.Width != _panelW) SyncWidth(ClientSize.Width);
                Config.PanelWidth = Dpi.UnS(ClientSize.Width);
                Config.PanelHeight = Dpi.UnS(ClientSize.Height);
                Config.PanelSizeAuto = false;
                if (_pinned) { _pinnedW = ClientSize.Width; _pinnedH = ClientSize.Height; }
                Config.Save();
                Program.DebugLog("[panel-size] resize-end " + ClientSize.Width + "x" + ClientSize.Height);
                return;
            }
            else if (m.Msg == 0x231) { _sizeMoveStart = ClientSize; _fnBeginResizeFreeze(false); }
            else if (m.Msg == 0x232)
            {
                _fnEndResizeFreeze();
                // 冻结期间 OnResize 跳过了 SyncWidth，结束后按最终宽度补一次重排
                if (ClientSize.Width != _panelW) SyncWidth(ClientSize.Width);
                if (ClientSize != _sizeMoveStart)
                {
                    Config.PanelWidth = Dpi.UnS(ClientSize.Width);
                    Config.PanelHeight = Dpi.UnS(ClientSize.Height);
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
            if (_unpinCloseTimer != null) { _unpinCloseTimer.Stop(); _unpinCloseTimer.Dispose(); _unpinCloseTimer = null; }
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

        // 子目录项数缓存：大目录面板每行都要显示"N 项"，逐个枚举目录会拖慢面板打开。
        // TTL 短缓存让反复悬停同一目录直接命中；目录内容变化最多 5s 后刷新。
        private static readonly Dictionary<string, KeyValuePair<DateTime, int>> _dirCountCache =
            new Dictionary<string, KeyValuePair<DateTime, int>>();
        private static int GetCachedDirCount(string full)
        {
            DateTime now = DateTime.UtcNow;
            KeyValuePair<DateTime, int> hit;
            lock (_dirCountCache)
            {
                if (_dirCountCache.TryGetValue(full, out hit)
                    && (now - hit.Key).TotalSeconds < 5) return hit.Value;
            }
            int cnt = 0;
            try { cnt = Directory.GetFileSystemEntries(full).Length; } catch { }
            lock (_dirCountCache) { _dirCountCache[full] = new KeyValuePair<DateTime, int>(now, cnt); }
            return cnt;
        }

        private static Image GetIcon(string full, bool isDir)
        {
            string ext = isDir ? "" : Path.GetExtension(full);
            string key = isDir ? "<dir>" : ext;
            // lnk 的图标由目标决定（指向文件夹=文件夹图标，指向 png=图片图标），
            // 每个快捷方式按自身路径独立缓存——既避免一个 lnk 污染整个 ".lnk" 桶，
            // 也避免多个指向 .exe 的 lnk 共享同一桶而显示成第一个的图标
            if (!isDir && string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                key = "lnk|" + full.ToLowerInvariant();
            }
            // 可执行文件/图标/快捷方式的图标随文件本身而变（不同 exe 各有自己的图标），
            // 按扩展名分桶会让所有 exe 共用第一个 exe 的图标 → 这类文件按完整路径单独缓存
            else if (!isDir && PerFileIconExt(ext))
            {
                key = "f|" + full.ToLowerInvariant();
            }
            Image cached;
            if (_iconCache.TryGetValue(key, out cached)) return cached;
            Image img = null;
            var shfi = new NativeMethods.SHFILEINFOW();
            NativeMethods.SHGetFileInfo(full, 0, ref shfi, (uint)Marshal.SizeOf(typeof(NativeMethods.SHFILEINFOW)),
                0x100 | (Dpi.Scale >= 1.6f ? 0u : 0x1u));
            if (shfi.hIcon != IntPtr.Zero)
            {
                try { using (var icon = Icon.FromHandle(shfi.hIcon)) img = icon.ToBitmap(); }
                catch { }
                NativeMethods.DestroyIcon(shfi.hIcon);
            }
            _iconCache[key] = img;
            return img;
        }


        // 图标内容取决于文件本身的扩展名：不能按扩展名共用缓存桶
        private static bool PerFileIconExt(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".exe": case ".ico": case ".url": case ".msi":
                case ".scr": case ".cpl": case ".dll": case ".com":
                case "":   // 无扩展名（appref-ms 等）图标也随文件变
                    return true;
            }
            return false;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
            return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F2") + " GB";
        }

        // 名称行布局：✎ 重命名按钮固定在最左，标题从其右侧起；计数紧跟标题文字；
        // 右侧留出 ⧉/📌 固定按钮区，名称过长时在省略号处截断
        private void LayoutTitleCount()
        {
            if (_titleLbl == null || _countLbl == null || _nameRow == null || _editBtn == null) return;
            int titleLeft = _editBtn.Right + Dpi.S(2);
            int rightEdge = _nameRow.Width - Dpi.S(46);   // 42px 固定按钮区 + 4px 间隙
            int cw = 0, rw = 0;
            // 不能依赖 Visible：控件树/面板尚未显示时 Visible 恒为 false，会导致首次打开
            // 计数/资源占用不定位（先偏左，过一会采样 tick 才跳回右侧）
            if (_countLbl.Text.Length > 0)
            {
                using (var g = _countLbl.CreateGraphics())
                    cw = (int)Math.Ceiling(g.MeasureString(_countLbl.Text, _countLbl.Font).Width) + 6;
                _countLbl.Width = cw;
            }
            if (_resLbl != null && _resLbl.Text.Length > 0)
            {
                using (var g = _resLbl.CreateGraphics())
                    rw = (int)Math.Ceiling(g.MeasureString(_resLbl.Text, _resLbl.Font).Width) + 6;
                _resLbl.Width = rw;
            }
            int resW = rw > 0 ? rw + 8 : 0;   // 资源文字（含与计数的最小间隔）
            _titleLbl.Width = Math.Max(40, rightEdge - titleLeft - 4 - cw - resW);
            int textW;
            using (var g = _titleLbl.CreateGraphics())
                textW = (int)Math.Ceiling(g.MeasureString(_titleLbl.Text, _titleLbl.Font).Width);
            int x = titleLeft + textW + 6;
            if (cw > 0)
                _countLbl.Left = Math.Min(x, rightEdge - 2 - cw - resW);
            // 资源占用固定靠右（贴着右侧按钮区），与左侧标题/计数互不干扰
            if (rw > 0)
                _resLbl.Left = rightEdge - rw;
            // 标题宽度收缩为文字实际宽度：右侧留出"真空白"（可拖动移动面板）。
            // 标签背景与行底同色时拉满宽会让整行看似空白实则都在标题上，拖动/右键全部失效
            int maxW = _titleLbl.Width, tW;
            using (var g = _titleLbl.CreateGraphics())
                tW = (int)Math.Ceiling(g.MeasureString(_titleLbl.Text, _titleLbl.Font).Width);
            _titleLbl.Width = Math.Min(maxW, tW + 2);
            // 计数加在标题之后（z 序更靠后），名称短时整体落在标题标签范围内，必须提到最上层否则被不透明标题盖住
            _countLbl.BringToFront();
            if (_resLbl != null) _resLbl.BringToFront();
        }

        // ── HE 自身资源占用（标题行计数右侧，默认关闭）────────────────────────
        private static Timer _resTimer;
        private static string _resText = "";
        private static DateTime _resLastAt;
        private static TimeSpan _resLastCpu;

        // 每秒采样一次；所有打开的面板共享一份数据，避免每个面板各起一个定时器
        private static void EnsureResourceSampler()
        {
            if (_resTimer != null) return;
            _resTimer = new Timer { Interval = 1000 };
            _resTimer.Tick += delegate
            {
                SampleResourceText();
                try
                {
                    foreach (var f in Program.Panels)
                    {
                        if (f != null && !f.IsDisposed) f.RefreshResourceLabel();
                    }
                }
                catch { }
            };
            _resTimer.Start();
        }

        // 采样一次（CPU% 用两次 TotalProcessorTime 差分除以实际经过时间×逻辑核数）
        private static string SampleResourceText()
        {
            try
            {
                using (var p = Process.GetCurrentProcess())
                {
                    var cpu = p.TotalProcessorTime;
                    var now = DateTime.UtcNow;
                    double pct = 0;
                    if (_resLastAt != DateTime.MinValue)
                    {
                        double usedMs = (cpu - _resLastCpu).TotalMilliseconds;
                        double wallMs = (now - _resLastAt).TotalMilliseconds;
                        if (wallMs > 0)
                            pct = Math.Max(0, Math.Min(100, usedMs / (wallMs * Environment.ProcessorCount) * 100));
                    }
                    _resLastCpu = cpu;
                    _resLastAt = now;
                    // 紧凑格式：只留数字，靠右显示；内存用私有工作集（与任务管理器同口径）
                    long mem = GetPrivateWorkingSet(p);
                    _resText = pct.ToString("0.#") + "% · " + (mem / (1024 * 1024)) + "M";
                }
            }
            catch { }
            return _resText;
        }

        // 私有工作集（active private working set）：任务管理器"内存"列的默认口径。
        // WorkingSet64 含共享页（DLL/字体等），会比任务管理器大不少
        private static long GetPrivateWorkingSet(Process p)
        {
            try
            {
                var c = new NativeMethods.PROCESS_MEMORY_COUNTERS_EX2();
                c.cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.PROCESS_MEMORY_COUNTERS_EX2));
                if (NativeMethods.GetProcessMemoryInfo(p.Handle, ref c, c.cb))
                {
                    long v = (long)c.PrivateWorkingSetSize.ToUInt64();
                    if (v > 0) return v;
                }
            }
            catch { }
            return p.WorkingSet64;
        }

        private void RefreshResourceLabel()
        {
            if (_resLbl == null || _resLbl.IsDisposed) return;
            // 首次显示/改宽后，名称行可能还是旧宽度：资源文字会偏在中间，这里按最终宽度校正
            if (_nameRow != null && !_nameRow.IsDisposed)
            {
                int want = Math.Max(60, ClientSize.Width - 12);
                if (_nameRow.Width != want) _nameRow.Width = want;
            }
            bool show = Config.ShowResourceUsage && _resText.Length > 0;
            if (_resLbl.Visible != show) _resLbl.Visible = show;
            if (show && _resLbl.Text != _resText) _resLbl.Text = _resText;
            // 关闭显示时清空文本：布局按文本长度占位，残留文本会让标题白白缩窄
            if (!show && _resLbl.Text.Length > 0) _resLbl.Text = "";
            LayoutTitleCount();
        }

        // 顶部控件与行宽度按面板宽度同步：拖动手柄改宽后必须刷新，
        // 否则排序行/名称行停在旧宽度、固定按钮位置不对
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 首次显示时窗口才拿到最终宽度：立刻把资源占用摆到右侧，不等 1s 采样 tick
            RefreshResourceLabel();
        }

        private void SyncWidth(int w)
        {
            if (w < 40) return;
            _panelW = w;
            if (_level == 0)
            {
                foreach (Control c in Controls)
                {
                    if (c is Panel && c.BackColor == Config.Border) c.Width = w - Dpi.S(12);
                }
                if (_searchBox != null) _searchBox.Width = w - Dpi.S(14);
            }
            // 名称行所有层级都要跟随宽度：二级面板此前只在 level==0 更新，
            // 导致资源占用/计数停在旧位置
            if (_nameRow != null)
            {
                _nameRow.Width = w - Dpi.S(12);
                LayoutTitleCount();   // 计数紧跟名称文字、标题宽度避开计数（✎ 在左侧，宽度由 LayoutTitleCount 统一算）
                if (_nameEdit != null) _nameEdit.Width = Math.Max(Dpi.S(60), w - Dpi.S(12) - Dpi.S(24));
            }
            // 表头三件套所有层级都同步（子面板排序栏/固定按钮同样跟随宽度）
            if (_sortFlow != null) _sortFlow.Width = w - Dpi.S(12);
            // 固定按钮 y 跟随所在带：一级面板跟随名称行垂直居中，子面板在排序栏带内（y=7）
            int pinY = _level == 0 ? (_nameRow != null ? _nameRow.Top + (_nameRow.Height - Dpi.S(18)) / 2 : Dpi.S(2)) : Dpi.S(7);
            if (_pinBtn != null) _pinBtn.Location = new Point(w - Dpi.S(26), pinY);
            if (_pinCardBtn != null) _pinCardBtn.Location = new Point(w - Dpi.S(26) - Dpi.S(22), pinY);
            if (_listPanel != null)
            {
                var lp = (SlimScrollPanel)_listPanel;
                int sw = lp.CanScroll ? SlimBarW : 0;
                _listPanel.Width = w - ListPadLr * 2;
                foreach (var r in _rows) r.Width = w - ListPadLr * 2 - sw;
                _fnSyncListScroll();
            }
            LayoutFilterBtns();
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
            _kbRow = null;   // 重建销毁行控件，键盘选中行引用一并作废
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
                // 拖动即固定；但刚取消固定后的 1.2s 内不回钉——用户取消固定后顺手挪正位置的
                // 二次拖动会被静默重新固定，表现为"取消了固定窗口却迟迟不自动关闭"
                if (Location != before && !_pinned && Environment.TickCount - _lastUnpinTick > 1200) TogglePinPopup();
            };
            // 顶部区域空白处右键 = 选项菜单
            MouseEventHandler blankMenu = (s, e) => { if (e.Button == MouseButtons.Right && Program.TrayMenu != null) { _menuOpen = true; Program.TrayMenu.Show(Cursor.Position); } };

            // 右侧留 48px 给 _pinCardBtn/_pinBtn：FlowLayoutPanel 不裁切溢出子控件，
            // 占满宽度时 ⧉ 会钻到按钮下面被覆盖；收窄后行尾空白也是拖拽入口
            var sortFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Location = new Point(6, curY), AutoSize = false, Height = 26, Width = _panelW - 60, BackColor = Config.ListBg };
                _sortFlow = sortFlow;
                // 新建文件夹：排序方式左侧的 "+" 按钮（文字色跟随当前列表配色方案）
                var newFolderBtn = new BorderLabel
                {
                    Text = "+",
                    Font = Config.ListHeaderFont(),
                    Padding = new Padding(5, 1, 5, 1),
                    AutoSize = true,
                    BackColor = Config.ListBg,
                    BorderColor = Config.ListBd,
                    ForeColor = Config.ListFg,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 2, 0),
                };
                newFolderBtn.Click += (s, e) => NewFolderHere();
                Tips.Set(newFolderBtn, I18n.T("new_folder"));
                sortFlow.Controls.Add(newFolderBtn);
                foreach (var kv in Config.SortLabels)
                {
                var btn = new BorderLabel
                {
                    Text = kv.Value,
                    Font = Config.ListHeaderFont(),
                    Padding = new Padding(5, 1, 13, 1),
                    AutoSize = true,
                    // 同设置窗实时预览样式：ListBg 底 + ListBd 描边（排序条底色同为 ListBg，视觉上仅剩描边）
                    BackColor = Config.ListBg,
                    BorderColor = Config.ListBd,
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
                    Font = Config.ListHeaderFont(),
                    AutoSize = true,
                    BackColor = Config.ListBg,
                    ForeColor = Config.ListFg,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 2, 0),
                    Padding = new Padding(5, 1, 5, 1),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Visible = _pinned,
                };
                _refreshBtn.Click += (s, e) => RefreshPanel();
                Tips.Set(_refreshBtn, I18n.T("refresh"));
                sortFlow.Controls.Add(_refreshBtn);

                // 对话框场景：把多选文件填入当前文件对话框（不提交，仍由用户点打开/保存）。
                // 表头横向空间有限（排序按钮已占满），正文用"填入"，完整说明放 tooltip
                _fillDlgBtn = new Label
                {
                    Text = I18n.T("dlg_fill"),
                    Font = Config.ListHeaderFont(),
                    AutoSize = true,
                    BackColor = Config.ListBg,
                    ForeColor = Config.ListFg,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 0, 2, 0),
                    Padding = new Padding(5, 1, 5, 1),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Visible = DialogHwnd != IntPtr.Zero,
                };
                _fillDlgBtn.Click += (s, e) =>
                {
                    if (!DialogSupport.IsDialogAlive(DialogHwnd)) { ShowToast(I18n.T("dlg_gone")); return; }
                    var files = SelRows().Where(r => !r.IsDir).Select(r => r.Name).ToList();
                    if (files.Count == 0) { ShowToast(I18n.T("dlg_fill_none")); return; }
                    if (DialogSupport.FillFileNames(DialogHwnd, files)) ShowToast(I18n.T("dlg_fill_ok"));
                };
                Tips.Set(_fillDlgBtn, I18n.T("dlg_fill_tip"));
                sortFlow.Controls.Add(_fillDlgBtn);
                // 表头带高度随字体自适应：取最高子项 + 6px，各按钮（含 ↻）在带内纵向居中，
                // 调大表头字号时文字仍在背景条中央而不是贴顶被裁
                int bandH = Dpi.S(26);
                foreach (Control c in sortFlow.Controls)
                {
                    int ph = c.GetPreferredSize(Size.Empty).Height;
                    if (ph + Dpi.S(6) > bandH) bandH = ph + Dpi.S(6);
                }
                sortFlow.Height = bandH;
                foreach (Control c in sortFlow.Controls)
                {
                    int ph = c.GetPreferredSize(Size.Empty).Height;
                    c.Margin = new Padding(c.Margin.Left, Math.Max(0, (bandH - ph) / 2), c.Margin.Right, 0);
                }
                Controls.Add(sortFlow);
                curY += bandH + Dpi.S(2);


                // 固定按钮：一级面板名称行在最顶部 → 叠在名称行右端（底色随名称行 ListBg）；
                // 子面板排序栏在最顶部 → 叠在排序栏带内（ListHdbg）。必须 BringToFront，否则被后加的名称行盖住
                int pinY = _level == 0 ? Dpi.S(2) : Dpi.S(7);
                _pinBtn = new Label
                {
                    Size = Dpi.Sz(20, 18),
                    AutoSize = false,
                    Location = new Point(_panelW - Dpi.S(26), pinY),
                    BackColor = Config.ListBg,
                    Image = PreviewForm.PinIcon(_pinned, Config.ListFg),
                    Cursor = Cursors.Hand,
                };
                Tips.Set(_pinBtn, I18n.T("tip_pin_panel"));
                _pinBtn.Click += (s, e) => TogglePinPopup();
                Controls.Add(_pinBtn);

                _pinCardBtn = new Label
                {
                    Size = Dpi.Sz(20, 18),
                    AutoSize = false,
                    Location = new Point(_panelW - Dpi.S(26) - Dpi.S(22), pinY),
                    BackColor = Config.ListBg,
                    Image = PreviewForm.PinLaunchIcon(false, Config.ListFg),
                    Cursor = Cursors.Hand,
                };
                Tips.Set(_pinCardBtn, I18n.T("tip_pin_card"));
                _pinCardBtn.Click += (s, e) => { if (!string.IsNullOrEmpty(_path)) Program.TogglePin(_path); };
                Controls.Add(_pinCardBtn);
                _pinCardBtn.BringToFront();
                _pinBtn.BringToFront();
                // 表头公共交互：拖动固定 + 空白右键菜单（子面板同样可用）
                sortFlow.MouseDown += drag;
                sortFlow.MouseUp += blankMenu;

            if (_level >= 0)   // 名称行+搜索框：一级面板与子面板都显示文件夹名称（搜索框默认隐藏，Ctrl+F 展开）
            {
                // 名称行：当前文件夹名（过长省略，悬停 tooltip 显示完整路径）+ 编辑图标（点击就地改名）
                // 底色不参与层级着色（Tag=noTint）：否则名称条上下 2px 内边距会露出 LevelBgColor 的彩色描边，
                // 在浅色主题下呈粉红一圈，与面板割裂
                // 名称行高度随标题字体自适应：调大字号时文字仍在条内纵向居中不被裁
                int nameRowH = Math.Max(Dpi.S(22), TextRenderer.MeasureText("Ag", Config.ListTitleFont()).Height + Dpi.S(6));
                _nameRow = new Panel { BackColor = Config.ListBg, Tag = "noTint", Location = new Point(Dpi.S(6), curY), Size = new Size(_panelW - Dpi.S(12), nameRowH) };
                // 条目计数小字：紧跟文件夹名文字之后显示，高度与名称/编辑按钮对齐（18px，同 y）
                // 名称行左侧两个 18px 按钮（⧉ 复制路径 + ✎ 重命名）各带 2px 间隙 = 40px
                int headBtnsW = Dpi.S(40);
                int editX = _panelW - Dpi.S(12) - Dpi.S(18);
                string countText = originalCount > 0 ? I18n.T("panel_status_count", originalCount) : "";
                var countFont = Config.ListMetaFont();
                int countW = countText.Length > 0 ? TextRenderer.MeasureText(countText, countFont).Width + Dpi.S(6) : 0;
                _countLbl = new SmoothLabel
                {
                    Text = countText,
                    Font = countFont,
                    ForeColor = Config.ListMu,
                    BackColor = Config.ListBg,   // 与名称行底色一致：标题行不再有独立背景条
                    AutoSize = false,
                    UseCompatibleTextRendering = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Cursor = DefaultCursor,
                    Location = new Point(0, Dpi.S(2)),
                    Size = new Size(countW, nameRowH - Dpi.S(4)),
                    Visible = countText.Length > 0,
                };
                _resLbl = new SmoothLabel
                {
                    Text = "",
                    Font = countFont,
                    ForeColor = Config.ListMu,
                    BackColor = Config.ListBg,
                    AutoSize = false,
                    UseCompatibleTextRendering = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Cursor = DefaultCursor,
                    Location = new Point(0, Dpi.S(2)),
                    Size = new Size(0, nameRowH - Dpi.S(4)),
                    Visible = false,
                };
                _titleLbl = new SmoothLabel
                {
                    Text = ShowName(path) + (string.IsNullOrEmpty(TitleSuffix) ? "" : " · " + TitleSuffix),
                    Font = Config.ListTitleFont(),
                    ForeColor = Config.ListFg,
                    BackColor = Config.ListBg,   // 移除标题背景条：与名称行底色一致
                    AutoEllipsis = true,
                    UseCompatibleTextRendering = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Cursor = Cursors.Hand,
                    Location = new Point(headBtnsW, Dpi.S(2)),   // ⧉ 与 ✎ 在名称行最左侧，标题从两个按钮右侧起
                    Size = new Size(Math.Max(Dpi.S(40), editX - headBtnsW - countW), nameRowH - Dpi.S(4)),
                };
                var titleDown = new Point(int.MinValue, int.MinValue);
                bool titleDbl = false;   // 手动双击已触发：随后的抬起不再走单击复制路径
                bool titleDragging = false;   // 按住标题拖动过面板：抬起时不再复制路径
                _titleLbl.MouseDown += (s, e) =>
                {
                    titleDown = e.Location;
                    titleDragging = false;
                    // 双击标题 = 打开文件夹（手动判定：e.Clicks 受单击复制等时序影响不可靠）
                    if (e.Button == MouseButtons.Left && _fnHeaderDoubleClick())
                    {
                        titleDbl = true;
                        if (_titleCopyTimer != null) { _titleCopyTimer.Stop(); _titleCopyTimer.Dispose(); _titleCopyTimer = null; }
                        _fnOpenInExplorer();
                    }
                };
                // 按住文件名拖动列表（超过 3px 才算拖动，避免吞掉单击复制路径）
                _titleLbl.MouseMove += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left || titleDragging || titleDown.X == int.MinValue) return;
                    if (Math.Abs(e.X - titleDown.X) <= 3 && Math.Abs(e.Y - titleDown.Y) <= 3) return;
                    titleDragging = true;
                    var before = Location;
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);   // WM_NCLBUTTONDOWN / HTCAPTION
                    if (Location != before && !_pinned && Environment.TickCount - _lastUnpinTick > 1200) TogglePinPopup();
                };
                // 点击粗体文件夹名 = 复制路径（拖动面板后不误触发）；重命名走左侧 ✎ 按钮
                _titleLbl.MouseUp += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (titleDragging) { titleDragging = false; titleDown = new Point(int.MinValue, int.MinValue); return; }
                    if (titleDbl) { titleDbl = false; return; }   // 双击的第二次抬起：不再复制路径
                    if (e.Clicks >= 2) return;
                    if (Math.Abs(e.Location.X - titleDown.X) > 3 || Math.Abs(e.Location.Y - titleDown.Y) > 3) return;
                    // 延迟复制路径：可能是双击的第一次抬起，等 DoubleClickTime 确认非双击后再复制
                    if (_titleCopyTimer != null) { _titleCopyTimer.Stop(); _titleCopyTimer.Dispose(); }
                    var pathCopy = path;
                    _titleCopyTimer = new Timer { Interval = SystemInformation.DoubleClickTime + 50 };
                    _titleCopyTimer.Tick += (s2, e2) =>
                    {
                        _titleCopyTimer.Stop();
                        _titleCopyTimer.Dispose();
                        _titleCopyTimer = null;
                        try { Clipboard.SetText(pathCopy); ShowToast(I18n.T("copied")); } catch { }
                    };
                    _titleCopyTimer.Start();
                };
                // 标题提示：完整路径 + 点击复制说明（统一走 Tips，受开关控制）
                Tips.Set(_titleLbl, path + "\n" + I18n.T("copy_path"));
                // ⧉ 复制路径：放在 ✎ 重命名按钮左边，点击即复制当前面板目录的完整路径
                _copyBtn = new Label
                {
                    BackColor = Config.ListBg,
                    Image = PreviewForm.CopyIcon(Config.ListFg),
                    Size = Dpi.Sz(18, 18),
                    AutoSize = false,
                    Cursor = Cursors.Hand,
                    Location = new Point(0, (nameRowH - Dpi.S(18)) / 2),
                    TextAlign = ContentAlignment.MiddleCenter,
                };
                _copyBtn.MouseEnter += (s, e) => _copyBtn.BackColor = Config.ListBtnHv;
                _copyBtn.MouseLeave += (s, e) => _copyBtn.BackColor = Config.ListBg;
                Tips.Set(_copyBtn, I18n.T("copy_path"));
                _copyBtn.Click += (s, e) =>
                {
                    if (string.IsNullOrEmpty(_path)) return;
                    try { Clipboard.SetText(_path); ShowToast(I18n.T("copied")); } catch { }
                };
                _editBtn = new Label
                {
                    BackColor = Config.ListBg,   // 移除按钮底色：与名称行底色一致，悬停时才提亮
                    Image = PreviewForm.EditIcon(Config.ListFg),
                    Size = Dpi.Sz(18, 18),
                    AutoSize = false,
                    Cursor = Cursors.Hand,
                    // 重命名按钮在复制按钮右侧（标题/计数依次排其后）
                    Location = new Point(Dpi.S(20), (nameRowH - Dpi.S(18)) / 2),
                    TextAlign = ContentAlignment.MiddleCenter,
                };
                // 悬停提亮一档，与列表行内按钮组保持一致的反馈
                _editBtn.MouseEnter += (s, e) => _editBtn.BackColor = Config.ListBtnHv;
                _editBtn.MouseLeave += (s, e) => _editBtn.BackColor = Config.ListBg;
                Tips.Set(_editBtn, I18n.T("tip_rename_dir"));
                _editBtn.Click += (s, e) => BeginTitleRename();
                _nameEdit = new TextBox
                {
                    Font = Config.ListTitleFontReg(),
                    // 顶栏改名框跟随列表配色（noTint 防止 tint 用主题基调改写）
                    BackColor = Config.ListBg,
                    ForeColor = Config.ListFg,
                    Tag = "noTint",
                    BorderStyle = BorderStyle.FixedSingle,
                    Location = new Point(0, Dpi.S(1)),
                    Width = Math.Max(Dpi.S(60), _panelW - Dpi.S(12) - Dpi.S(24)),
                    Visible = false,
                };
                _nameEdit.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Return) { e.SuppressKeyPress = true; EndTitleRename(true); }
                    else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndTitleRename(false); }
                };
                // 点别处 = 提交（与资源管理器重命名一致）
                _nameEdit.LostFocus += (s, e) => { if (_nameEdit.Visible) EndTitleRename(true); };
                // 名称行与表头排序栏调换位置后，面板顶部空白在名称行：空白处同样可拖动移动/右键选项菜单
                _nameRow.MouseDown += drag;
                _nameRow.MouseUp += blankMenu;
                _countLbl.MouseDown += drag;    // 计数小字无自身点击行为，同样作为拖拽入口
                _countLbl.MouseUp += blankMenu;
                _resLbl.MouseDown += drag;
                _resLbl.MouseUp += blankMenu;
                _nameRow.Controls.Add(_titleLbl);
                _nameRow.Controls.Add(_countLbl);
                _nameRow.Controls.Add(_resLbl);
                Tips.Set(_resLbl, I18n.T("resource_tip"));
                _nameRow.Controls.Add(_copyBtn);
                _nameRow.Controls.Add(_editBtn);
                _nameRow.Controls.Add(_nameEdit);
                // 资源占用：设置开启时在计数右侧显示（采样器全局共享，1 秒刷新）
                if (Config.ShowResourceUsage)
                {
                    EnsureResourceSampler();
                    _resLbl.Text = SampleResourceText();
                    _resLbl.Visible = _resLbl.Text.Length > 0;
                }
                LayoutTitleCount();   // ⧉/✎ 在左、计数紧跟名称文字、右侧避开固定按钮（改宽/刷新后同样走这里）
                Controls.Add(_nameRow);
                // 名称行与表头排序栏调换位置：名称行提到面板最顶部，排序栏移到其下方（两带总高不变，curY 不受影响）
                int sortBandTop = sortFlow.Top;
                _nameRow.Top = sortBandTop;
                sortFlow.Top = sortBandTop + nameRowH + Dpi.S(2);
                curY += nameRowH + Dpi.S(2);
                // 固定按钮跟随名称行垂直居中（创建时用临时 y，名称行调换位置后校正）
                int pinY0 = sortBandTop + (nameRowH - Dpi.S(18)) / 2;
                if (_pinBtn != null) _pinBtn.Top = pinY0;
                if (_pinCardBtn != null) _pinCardBtn.Top = pinY0;

                // 搜索框：默认隐藏，Ctrl+F 才展开（常驻输入框会带来假焦点/IME 遮挡问题）
                // 搜索框跟随列表配色（ListBg/ListMu/ListFg/ListHdbg）：主题色经 tint 只换
                // 浅色基调，深色方案下背景/边框会残留浅色；Tag=noTint 防止 tint 再改写
                _searchBorder = new Panel { BackColor = Config.ListHdbg, Tag = "noTint", Location = new Point(Dpi.S(6), 0), Size = new Size(_panelW - Dpi.S(12), Dpi.S(22)), Visible = false };
                _searchBox = new TextBox
                {
                    Text = I18n.T("search"),
                    Font = new Font(Config.FontName, Config.FontSize),
                    BackColor = Config.ListBg,
                    ForeColor = Config.ListMu,
                    BorderStyle = BorderStyle.None,
                    Location = new Point(1, Dpi.S(2)),
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

                // _nameRow 上面已挂过 drag / blankMenu：重复订阅会让一次单击连续两次进入
                // _fnHeaderDoubleClick()，第二次被当成"双击"→ 单击就打开文件夹（误操作）。
                // 每个控件只订阅一次，顶部真正双击时仍由手动判定打开文件夹。
                _searchBorder.MouseDown += drag;
                _searchBorder.MouseUp += blankMenu;
            }
            // 表头底基准（子面板也有了排序栏，列表从此处起步）
            _headH = curY;

            // 自管偏移滚动：SlimScrollBar 细滑块与内容层双向同步（滑块随滚动实时移动）
            // _syncingScroll 防回环：动画更新 sb.Value 会再触发 sb.Scroll，
            // 不加守卫会回调 SetOffset→StopAnim 把缓动动画杀在第一帧
            _listPanel = new SlimScrollPanel { Location = new Point(ListPadLr, curY), BackColor = Config.ListBg };
            _listPanel.MouseUp += blankMenu;
            // 内容层铺满 viewport，空白处右键同样弹菜单（否则被 content 挡住收不到）
            _listPanel.Content.MouseUp += blankMenu;
            Controls.Add(_listPanel);
            _listScrollBar = new SlimScrollBar
            {
                // SlimScrollBar 构造里默认 Dock=Right；停靠会让滚动条铺满整个窗口高度（顶到标题栏/排序栏），
                // 这里取消停靠，改由 _fnSyncListScroll 按列表范围定位
                Dock = DockStyle.None,
                // 不用 Transparent：透明模拟重画的是父窗体背景（主题混色），而滚动条实际压在
                // _listPanel（ListBg）上，深色方案下两端会露出两块浅色。直接取行区同色，
                // 轨道仍是半透明黑叠加，视觉效果不变
                BackColor = Config.ListBg,
                Style = SlimScrollBar.BarStyle.Thin,
                // 滚动条跟随列表边框色；悬停时用强调色加深反馈
                ThumbColor = Config.ListBd,
                ThumbHotColor = Config.ListAc,
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

            _rowDelBtn = new RowActionButton
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

            _rowCopyBtn = new RowActionButton
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

            _rowPinBtn = new RowActionButton
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

            _rowRenameBtn = new RowActionButton
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

            var rowBtns = new Label[] { _rowDelBtn, _rowCopyBtn, _rowPinBtn, _rowRenameBtn };
            foreach (var b in rowBtns) b.Size = new Size(RowBtnW, RowBtnH);
            // 方案 A：常态统一用次要色（弱化）图标；hover 才给淡底 + 强调图标。
            // 删除偏红、编辑偏主题色，其余两个用通用深灰。
            Color danger = Color.FromArgb(0xC0, 0x39, 0x2B);
            Color neutral = Color.FromArgb(18, Color.Black);
            Color accent = Color.FromArgb(36, Config.Accent);
            _rowDelBtn.Image = PreviewForm.TrashIcon(Config.ListMu);
            _rowCopyBtn.Image = PreviewForm.CopyIcon(Config.ListMu);
            _rowPinBtn.Image = PreviewForm.PinLaunchIcon(false, Config.ListMu);
            _rowRenameBtn.Image = PreviewForm.EditIcon(Config.ListMu);
            Action<RowActionButton, Image, Color> hover = (b, ic, tint) =>
            { b.HoverIcon = ic; b.HoverTint = tint; };
            hover((RowActionButton)_rowDelBtn, PreviewForm.TrashIcon(danger), Color.FromArgb(30, danger));
            hover((RowActionButton)_rowCopyBtn, PreviewForm.CopyIcon(Config.ListFg), neutral);
            hover((RowActionButton)_rowPinBtn, PreviewForm.PinLaunchIcon(false, Config.ListFg), neutral);
            hover((RowActionButton)_rowRenameBtn, PreviewForm.EditIcon(Config.Accent), accent);
            foreach (var b in rowBtns)
            {
                var btn = (RowActionButton)b;
                btn.MouseEnter += (s, e) => { btn.Hovered = true; btn.Invalidate(); };
                btn.MouseLeave += (s, e) => { btn.Hovered = false; btn.Invalidate(); };
            }

            var font = Config.ListItemFont();
            var metaFont = Config.ListMetaFont();   // 行内大小/子项数小字
            int maxW = Dpi.S(200);
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
                    if (sz.Width + Dpi.S(36) > maxW) maxW = (int)sz.Width + Dpi.S(36);
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
                            // 大文件夹（数百子目录）逐个 GetFileSystemEntries 会枚举数百次目录，
                            // 是"收藏列表大文件夹打开慢"的根因。用 TTL 缓存子项数，重复悬停秒开。
                            sizeTexts[i] = GetCachedDirCount(full) + " " + I18n.T("items");
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
                        sizeWs[i] = g.MeasureString(sizeTexts[i], metaFont).Width;
                        if (sizeWs[i] > maxSizeW) maxSizeW = sizeWs[i];
                    }
                }
                if (maxSizeW > 0) maxW += (int)maxSizeW + Dpi.S(10);
            }
            // 所有层级都有排序按钮栏，最小宽度须容纳表头
            foreach (var btn in _sortBtns.Values)
            {
                int bw = btn.Width + btn.Margin.Horizontal + Dpi.S(6);
                if (bw > maxW) maxW = bw;
            }
            // 手动调整过的宽度强制沿用（拖窄也持久化，长文件名 AutoEllipsis 截断）
            // 配置里存的是逻辑像素，换缩放比后仍对应相同物理宽度
            int cfgPanelW = Config.PanelWidth > 0 ? Dpi.S(Config.PanelWidth) : 0;
            if (cfgPanelW >= Dpi.S(200)) maxW = cfgPanelW;
            if (maxW > Math.Max(Dpi.S(400), cfgPanelW)) maxW = Math.Max(Dpi.S(400), cfgPanelW);
            // 已固定的面板沿用固定时的宽度：条目少的目录刷新后不该被拉回默认宽度
            if (_pinned && _pinnedW > 0) maxW = _pinnedW;
            _panelW = maxW;
            // 互换布局分栏线：拖动过的位置沿用（clamp 进当前面板宽），否则自动=按钮组(72)+大小列+缓冲
            int autoZone = Dpi.S(72) + (int)maxSizeW + Dpi.S(12);
            int minZone = Dpi.S(78), maxZone = _panelW - Dpi.S(130);
            if (maxZone < minZone) maxZone = minZone;
            _rowSideZone = Config.RowSplitX > 0 ? Math.Min(Math.Max(Dpi.S(Config.RowSplitX), minZone), maxZone) : autoZone;

            // 所有层级都同步：子面板表头（sortFlow/pinBtn/行宽）也要按最终宽度校正
            SyncWidth(_panelW);

            int rowH = Dpi.S(24);
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
                        ? new Padding(sizeTexts[i] == null ? Dpi.S(4) : (int)sizeWs[i] + Dpi.S(10), 0, Dpi.S(28), 0)
                        : new Padding(Dpi.S(24), 0, sizeTexts[i] == null ? Dpi.S(4) : (int)sizeWs[i] + Dpi.S(10), 0),
                    AutoSize = false,
                    UseCompatibleTextRendering = true,
                    BackColor = Config.BG,
                    ForeColor = Config.FG,
                    Location = new Point(0, ry),
                    Size = new Size(_panelW - ListPadLr * 2, rowHeights[i]),
                    Icon = GetIcon(full, isDir),
                    SizeText = sizeTexts[i],
                    SizeFont = metaFont,
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
                Tips.Set(row, name);   // 悬停行时沿用底部提示条显示文件名
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
            int maxH = workArea.Height - Dpi.S(40);
            int needH = curY + ry;
            // 高度两种模式：自动适配（默认）=随内容长高，条目多时显示更多；
            // 用户手动拖过尺寸后转纯记忆模式=严格按记忆高度（不再自动撑大）
            int cfgPanelH = Config.PanelHeight > 0 ? Dpi.S(Config.PanelHeight) : 0;
            if (!Config.PanelSizeAuto && cfgPanelH >= Dpi.S(150))
                needH = Math.Min(needH, Math.Min(cfgPanelH, maxH));
            // 同理：固定面板沿用固定时的高度（内容变多时仍可更高，但不超过工作区）
            if (_pinned && _pinnedH > 0 && needH < _pinnedH) needH = _pinnedH;
            if (needH > maxH)
                needH = maxH;
            _listPanel.Size = new Size(_panelW - ListPadLr * 2, needH - curY);
            // 内容层高度 = 行总高（空目录给 emptyLbl 一行高），滚动范围由此确定
            int contentH = Math.Max(ry, Dpi.S(24));
            ((SlimScrollPanel)_listPanel).SetContentHeight(contentH);
            // 行右侧按“实际是否出现滚动条”让位（此前只在新建超高一条路径收缩：
            // 固定面板沿用记忆高度、内容后来变多等场景会漏——滚动条压住行尾“xx 项”的根因）
            if (((SlimScrollPanel)_listPanel).CanScroll)
                foreach (var r in _rows) r.Width = _panelW - ListPadLr * 2 - SlimBarW;
            _fnSyncListScroll();
            // 底部状态栏：占位防止 grip 挡住最后一行删除按钮；左侧计数，右侧实际格式（扩展名）筛选标签
            int statusRowH = Dpi.S(18);
            _filterBtns.Clear();
            var btnFont = Config.ListChipFont();
            var statusFont = Config.ListChipFont();
            // 统计列表内实际出现的格式：文件夹单独一类，文件按实际扩展名
            // （设置关闭"列表底部格式筛选"时不统计：不创建格式标签，状态栏只留一行作拖动/搜索入口）
            var extCount = new Dictionary<string, int>();
            if (Config.StatusFilter)
            {
                foreach (var row in _rows)
                {
                    string ext = RowExtKey(row);
                    if (ext == null) continue;
                    if (!extCount.ContainsKey(ext)) extCount[ext] = 0;
                    extCount[ext]++;
                }
            }
            // 出现次数多的排前面，同数按名称排序
            var kindList = extCount.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => p.Key).ToList();
            int btnH = Dpi.S(16), btnGap = Dpi.S(4);
            int availW = _panelW - Dpi.S(12);
            // 条目计数已移到顶部名称行右侧（_countLbl）：状态栏整体让给格式标签，从左端排布
            _statusCountW = 0;
            // 标签在状态栏内流式排布，放不下换行，状态栏高度随之增加
            int statusRows = 1;
            int flowX = Dpi.S(6);
            foreach (var kind in kindList)
            {
                int bw = TextRenderer.MeasureText(ChipText(kind), btnFont).Width + Dpi.S(8);
                if (flowX + bw > Dpi.S(6) + availW && flowX > Dpi.S(6)) { flowX = Dpi.S(6); statusRows++; }
                flowX += bw + btnGap;
            }
            int statusH = statusRows * statusRowH;
            ClientSize = new Size(_panelW, needH + statusH + ListPadBottom);
            // 圆角描边模式下 ClientSize 写入口会做内缩补偿，实际宽度与 _panelW 有偏差：
            // 不校正的话首弹时名称行右端的计数/重命名按钮会被裁掉一半（拖宽后 SyncWidth 才修正）
            if (ClientSize.Width != _panelW) SyncWidth(ClientSize.Width);
            Program.DebugLog("[filter-btn] needH=" + needH + " statusH=" + statusH + " kindList=" + string.Join(",", kindList) + " rows=" + _rows.Count);
            _statusLbl = new Label
            {
                Location = new Point(Dpi.S(6), needH),
                Size = new Size(availW, statusH),
                Text = "",
                Font = statusFont,
                ForeColor = Config.ListMu,
                BackColor = Config.ListBg,   // 状态栏不再显示深色条，与列表画布同色
                TextAlign = ContentAlignment.MiddleLeft,
            };
            Controls.Add(_statusLbl);
            _statusLbl.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                _statusDownPos = e.Location;
                _statusDragging = false;
            };
            _statusLbl.MouseMove += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || _statusDragging) return;
                if (Math.Abs(e.X - _statusDownPos.X) > 3 || Math.Abs(e.Y - _statusDownPos.Y) > 3)
                {
                    _statusDragging = true;
                    var before = Location;
                    NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                    // 同 3198：刚取消固定后的 1.2s 内拖动不回钉
                    if (Location != before && !_pinned && Environment.TickCount - _lastUnpinTick > 1200) TogglePinPopup();
                }
            };
            _statusLbl.Click += (s, e) => { if (!_statusDragging) ToggleSearch(); };

            foreach (var kind in kindList)
            {
                // 紧凑宽度：文字 + 左右各 4px 对称内边距（居中对齐）
                int bw = TextRenderer.MeasureText(ChipText(kind), btnFont).Width + Dpi.S(8);
                var btn = new BorderLabel
                {
                    Text = ChipText(kind),
                    Font = btnFont,
                    AutoSize = false,
                    Size = new Size(bw, btnH),
                    BackColor = Config.ListBg,
                    BorderColor = Config.ListBd,   // 圆角描边样式（BorderLabel 自绘）
                    ForeColor = Config.ListFg,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                    Tag = kind,
                };
                btn.MouseDown += FilterBtnMouseDown;
                btn.MouseUp += FilterBtnMouseUp;
                btn.MouseLeave += FilterBtnMouseLeave;
                btn.Click += FilterBtnClick;
                Controls.Add(btn);
                _filterBtns.Add(btn);
                btn.BringToFront(); // 后于状态栏添加，必须提到最上层，否则被不透明的状态栏盖住不可见
            }
            LayoutFilterBtns();
            UpdateFilterBtnAppearance();
            if (_filterBtns.Count > 0) Program.DebugLog("[filter-btn] btn0 loc=" + _filterBtns[0].Location + " size=" + _filterBtns[0].Size + " statusY=" + _statusLbl.Top);

            ResumeLayout(false);
            PerformLayout();

            ApplySortBtns();

            ClampPos(ref x, ref y);
            Location = new Point(x, y);

            // 整面板按 level 着色：递归把子控件 Config.BG 背景换成 LevelBgColor
            {
                Color lbg = LevelBgColor(_level);
                Action<Control> tint = null;
                tint = (ctl) => { if ((ctl.Tag as string) != "noTint" && ctl.BackColor == Config.BG && !_filterBtns.Contains(ctl)) ctl.BackColor = lbg; foreach (Control cc in ctl.Controls) tint(cc); };
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
            if (_filterSelected.Count > 0 || _filterExcluded.Count > 0)
                FilterRows(_searchBox != null ? _searchBox.Text : "");
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
            if (_searchBorder == null || _searchBox == null || _listPanel == null || _statusLbl == null) return;
            bool on = !_searchBorder.Visible;
            int dh = 24;
            int listBottom = _listPanel.Top + _listPanel.Height;
            _searchBorder.Width = _panelW - 12;
            _searchBox.Width = _panelW - 14;
            _searchBorder.Location = new Point(6, listBottom);
            _statusLbl.Location = new Point(6, listBottom + (on ? dh : 0));
            LayoutFilterBtns();
            _searchBorder.Visible = on;
            ClientSize = new Size(_panelW, ClientSize.Height + (on ? dh : -dh));
            if (_pinned) _pinnedH = ClientSize.Height;
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
            if (keyData == (Keys.Control | Keys.S)) { ToggleSearch(); return true; }
            if (keyData == Keys.Escape && SearchActive) { ToggleSearch(); return true; }
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
            // 正被外部悬停/预览引用的旧路径同步改掉，否则悬停状态与新路径错位（预览残留/重复调度）
            if (string.Equals(_extPreviewPath, src, StringComparison.OrdinalIgnoreCase)) _extPreviewPath = dst;
            if (string.Equals(Program._extHoverFile, src, StringComparison.OrdinalIgnoreCase)) Program._extHoverFile = dst;
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
            if (_copyBtn != null) _copyBtn.Visible = false;
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
            if (_copyBtn != null) _copyBtn.Visible = true;
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

        // 设置里切换"信息+操作栏"或语言时，刷新所有已打开的预览窗
        internal static void RefreshPreviewInfoBars()
        {
            if (_extPreviewForm != null && !_extPreviewForm.IsDisposed) _extPreviewForm.RefreshInfoBar();
            foreach (var p in Program.Panels)
            {
                if (p == null || p.IsDisposed) continue;
                if (p._previewForm != null && !p._previewForm.IsDisposed) p._previewForm.RefreshInfoBar();
            }
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
                // 激活态不用背景/变色：三角形箭头已指示当前排序，所有按钮统一 ListBg 底 + ListFg 文字
                kv.Value.BackColor = Config.ListBg;
                kv.Value.ForeColor = Config.ListFg;
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

        // 筛选用实际格式键：文件夹固定 "dir"，文件取小写扩展名；无扩展名返回 null（不参与筛选）
        private static string RowExtKey(RowLabel row)
        {
            if (row.IsDir) return "dir";
            string ext = Path.GetExtension(row.Full);
            if (string.IsNullOrEmpty(ext)) return null;
            return ext.Substring(1).ToLowerInvariant();
        }

        // 筛选标签显示文字：文件夹统一显示 "DIR"，文件直接显示实际格式（扩展名大写）
        private static string ChipText(string kind)
        {
            return kind == "dir" ? "DIR" : kind.ToUpperInvariant();
        }

        // 筛选标签排布在状态栏内部：首行让出左侧计数文字，放不下换行
        private void LayoutFilterBtns()
        {
            if (_filterBtns.Count == 0 || _statusLbl == null) return;
            int btnRowH = Dpi.S(18), btnGap = Dpi.S(4);
            int availW = ClientSize.Width - Dpi.S(12);
            int fx = Dpi.S(6) + _statusCountW;
            // 格式按钮上方留 5px 边距（与列表行区分开）
            int fy = _statusLbl.Top + Dpi.S(5);
            foreach (var btn in _filterBtns)
            {
                if (fx + btn.Width > Dpi.S(6) + availW) { fx = Dpi.S(6); fy += btnRowH; }
                btn.Location = new Point(fx, fy);
                fx += btn.Width + btnGap;
            }
        }

        private void UpdateFilterBtnAppearance()
        {
            foreach (var btn in _filterBtns)
            {
                string kind = btn.Tag as string;
                if (kind == null) continue;
                if (_filterSelected.Contains(kind))
                {
                    btn.BackColor = Config.ListHdbg;   // 激活 = 加深底色（不用蓝色强调）
                    btn.ForeColor = Config.ListFg;
                }
                else if (_filterExcluded.Contains(kind))
                {
                    btn.BackColor = Config.ListBg;
                    btn.ForeColor = Color.Gray;
                }
                else
                {
                    btn.BackColor = Config.ListBg;
                    btn.ForeColor = Config.ListFg;
                }
            }
        }

        // 格式标签：单击=只看该格式；长按=反选（隐藏该格式，看其余）
        private Timer _filterHoldTimer;
        private Label _filterHoldBtn;
        private bool _filterHoldFired;

        private void FilterBtnMouseDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var btn = s as Label;
            string kind = btn != null ? btn.Tag as string : null;
            if (kind == null) return;
            _fnStopFilterHold();
            _filterHoldBtn = btn;
            _filterHoldFired = false;
            if (_filterHoldTimer == null)
            {
                _filterHoldTimer = new Timer { Interval = 450 };
                _filterHoldTimer.Tick += (s2, e2) =>
                {
                    _filterHoldTimer.Stop();
                    _fnToggleExclude(_filterHoldBtn);
                    _filterHoldFired = true;   // 随后的 Click 吞掉，避免长按后又切回包含
                };
            }
            _filterHoldTimer.Start();
        }

        private void FilterBtnMouseUp(object s, MouseEventArgs e)
        {
            _fnStopFilterHold();
        }

        private void FilterBtnMouseLeave(object s, EventArgs e)
        {
            // 长按已生效后移出按钮：不会再有 Click 到来，重置标志，避免吞掉下一次正常单击
            if (_filterHoldFired) _filterHoldFired = false;
            _fnStopFilterHold();
        }

        private void _fnStopFilterHold()
        {
            if (_filterHoldTimer != null) { _filterHoldTimer.Stop(); _filterHoldTimer.Dispose(); _filterHoldTimer = null; }
            _filterHoldBtn = null;
        }

        private void _fnToggleExclude(Label btn)
        {
            string kind = btn != null ? btn.Tag as string : null;
            if (kind == null) return;
            if (_filterExcluded.Contains(kind)) _filterExcluded.Remove(kind);
            else
            {
                _filterExcluded.Add(kind);
                _filterSelected.Remove(kind);   // 白名单与黑名单互斥，避免一个格式同时被选中又排除
            }
            UpdateFilterBtnAppearance();
            FilterRows(_searchBox != null ? _searchBox.Text : "");
        }

        private void FilterBtnClick(object s, EventArgs e)
        {
            if (_filterHoldFired) { _filterHoldFired = false; _fnStopFilterHold(); return; }
            var btn = s as Label;
            string kind = btn != null ? btn.Tag as string : null;
            if (kind == null) return;
            if (_filterSelected.Contains(kind)) _filterSelected.Remove(kind);
            else
            {
                _filterSelected.Add(kind);
                _filterExcluded.Remove(kind);
            }
            UpdateFilterBtnAppearance();
            FilterRows(_searchBox != null ? _searchBox.Text : "");
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
            bool kindFiltering = _filterSelected.Count > 0 || _filterExcluded.Count > 0;
            bool anyFilter = filtering || kindFiltering;
            int hidden = 0;
            int visY = 0;                 // 收拢游标：可见行紧贴上移，不留隐藏行空洞
            RowLabel firstVis = null;
            foreach (var row in _rows)
            {
                bool nameVis = !filtering || row.Name.ToLowerInvariant().Contains(q);
                bool kindVis = true;
                string kind = RowExtKey(row);
                if (_filterSelected.Count > 0)
                    kindVis = kind != null && _filterSelected.Contains(kind);
                // 反选（排除）在此基础上再减掉：白名单与黑名单可同时存在且互不冲突
                if (kindVis && kind != null && _filterExcluded.Contains(kind))
                    kindVis = false;
                bool vis = nameVis && kindVis;
                row.Visible = vis;
                if (!vis) hidden++;
                if (anyFilter)
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

        // 触发区起点 X：默认右侧触发带；名称靠右且隐藏触发线时扩展为整块右侧名称区
        // （鼠标落在文件名上即可预览/展开子列表）；带宽 0 仍视为整行触发。左侧安全区永不触发。
        private int _fnTriggerStartX(RowLabel row)
        {
            int bandW = RowLabel.TriggerBandW();
            if (bandW <= 0) return 0;
            if (Config.RowSwapSides && Config.RowHideBandLine) return Math.Max(0, row.SplitX);
            return row.Width - bandW;
        }

        private void OnRowEnter(RowLabel row)
        {
            // 拖分栏线/触发带线中（Capture 丢消息时的兜底）：行视觉冻结，避免高亮/按钮随拖动跳变
            if (_splitDrag || _bandDrag) return;
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
                if (!row.IsDir && Config.PreviewFiles && PreviewProvider.CanPreview(row.Full)) SchedulePreview(row);
                else SchedulePreviewClose();
                return;
            }
            // 序号叠加：与行内操作按钮同生共死——只显示当前悬停行的行号，先清掉残留行
            _fnClearRowIndex();
            // 旧行可能因"光标仍停在按钮组上"跳过了 MouseLeave 的复位（按钮组随后整体
            // 移到新行），整行高亮会残留在上一行：进入新行时统一清掉其他行的高亮
            _fnClearRowHighlight(row);
            row.ShowIndex = true;
            row.Invalidate();
            row.BackColor = Config.ListHv;
            // 方案 A：按钮组底色与行悬停色融为一体，只在单键 hover 时出现淡圆角块。
            // 4 个按钮同尺寸、留 3px 间距；整组距行缘 6px，避开行的圆角。
            // 默认布局下整组进一步左移触发带宽度，保证按钮全部落在分隔线左侧
            _rowBtnRow = row;
            _fnLayoutRowButtons(row);
            if (_rowDelBtn != null) _rowDelBtn.Tag = row.Full;
            if (_rowCopyBtn != null) _rowCopyBtn.Tag = row.Full;
            if (_rowRenameBtn != null && _rowEdit == null) _rowRenameBtn.Tag = row;
            if (_rowPinBtn != null) _rowPinBtn.Tag = row.Full;
            NativeMethods.POINT _ept; NativeMethods.GetCursorPos(out _ept);
            bool canPv = !row.IsDir && Config.PreviewFiles && PreviewProvider.CanPreview(row.Full);
            Program.DebugLog("[row] enter level=" + _level + " lang=" + I18n.Lang + " " + (row.IsDir ? "DIR " : "FILE ") + row.Name + " mouse=" + _ept.X + "," + _ept.Y + " previewFiles=" + Config.PreviewFiles + " canPreview=" + canPv);
            // 触发区：默认右侧触发带；名称靠右且隐藏触发线时扩展为整块右侧名称区
            // （鼠标落在文件名上即可预览）。带外进入后横移入区由 OnRowMove 补调度。
            bool enterInBand = row.PointToClient(Cursor.Position).X >= _fnTriggerStartX(row);
            _wasInBand = enterInBand;
            // 悬停抑制：面板内部只吃 Alt 临时禁用——门控只拦第一级，一级面板已过闸，行悬停免键
            if (!enterInBand || Config.HoverSuppressedInside(!row.IsDir)) return;
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
            // 拖分栏线/触发带线中：按钮组与高亮保持现状（Capture 兜底路径）
            if (_splitDrag || _bandDrag) return;
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
                _fnResetRowBtnHover();   // 按钮组隐藏后清掉指针悬停色块，避免下一个按钮残留高亮
            }
            // 光标仍在按钮列时保持整行悬停高亮（方案 A 按钮底色就是行悬停色，不能擦掉）
            if (onBtns || row == _activeDirRow || row == _contextRow) row.BackColor = Config.ListHv;
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
            _hoverTimer.Tick += (s, e) => { _hoverTimer.Stop(); _hoverTimer.Dispose(); _hoverTimer = null; if (Config.HoverSuppressedInside(false)) { Program.DebugLog("[panel] 悬停计时触发但悬停被抑制，取消 level=" + _level); return; } Program.DebugLog("[panel] 悬停计时触发 -> 请求打开子面板 level=" + _level + " path=" + _hoverPath); if (_hoverPath != null && _onOpenSub != null) _onOpenSub(this, _level, _hoverPath, _hoverRowY); };
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


        private void SchedulePreview(RowLabel row) { SchedulePreview(row, false); }

        private void SchedulePreview(RowLabel row, bool immediate)
        {
            // 级联抑制：更深层子面板开着时，本层（父层）行预览一律不再调度——用户已"下钻"，
            // 父层应休眠；否则扫过父层残余可见行会弹出带声预览，穿透子面板（核心 bug：跨层出声）
            if (Program.Panels.Any(p => !p.IsDisposed && p.Level > _level))
            {
                Program.DebugLog("[panel-preview] suppressed (deeper panel open) level=" + _level + " " + row.Full);
                return;
            }
            if (_previewForm != null && _previewPath == row.Full) { Program.DebugLog("[panel-preview] keep-existing level=" + _level + " " + row.Full); return; }
            Program.DebugLog("[panel-preview] schedule level=" + _level + " lang=" + I18n.Lang + " " + row.Full + " delay=" + Config.HoverDelayMs + " (hadTimer=" + (_previewTimer != null) + " hadForm=" + (_previewForm != null) + ")");
            CancelPreview();
            // 切到另一个文件：立即关掉旧预览（含停止其音频）。否则旧音频会拖到新预览
            // 异步生成完（解码波形等耗时）才在 ShowPreview 里被关，出现“新卡片已出、旧还在响”
            if (_previewForm != null && !_previewForm.Pinned) ClosePreviewForm();
            _previewPath = row.Full;
            // 按类型延时：视频单独延迟（起播慢、误触成本高），音频提速，其余全局
            int previewDelay = immediate ? 1 : Config.PreviewDelayMsFor(row.Full);
            foreach (var e in Config.AudioExts)
                if (row.Full.EndsWith(e, StringComparison.OrdinalIgnoreCase)) { previewDelay = Math.Min(previewDelay, 100); break; }
            _previewTimer = new Timer { Interval = previewDelay };
            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); _previewTimer.Dispose(); _previewTimer = null; if (!immediate && Config.HoverSuppressedInside(true)) { Program.DebugLog("[panel-preview] timer-fired 但悬停被抑制，取消 level=" + _level + " " + row.Full); return; } Program.DebugLog("[panel-preview] timer-fired level=" + _level + " " + row.Full); ShowPreview(row); };
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

        // ===== 键盘导航（全局键盘钩子路由）：↑/↓ 移动选中行，Enter 打开，F2 弹原生菜单（含重命名），
        // Del 送回收站，Esc 取消。选中态复用行的 Selected；面板重建时随行销毁一并作废 =====
        private RowLabel _kbRow;

        internal bool KbNavActive()
        {
            return _kbRow != null && !_kbRow.IsDisposed;
        }

        internal bool KbNavigate(int dir)
        {
            if (_rows.Count == 0) return false;
            int idx = KbNavActive() ? _rows.IndexOf(_kbRow) : -1;
            idx = idx < 0 ? (dir > 0 ? 0 : _rows.Count - 1) : Math.Max(0, Math.Min(_rows.Count - 1, idx + dir));
            SetKbRow(_rows[idx]);
            ShowKbHintOnce();
            // 列表内 ↑/↓ 只移动选中行：立刻弹预览会展开子文件夹列表并抢走焦点，
            // 一级列表就没法继续导航了。预览改由 → 显式触发，← 关闭并回到列表
            return true;
        }

        // 键盘会话里「→ 显示预览 / ← 返回列表」的状态；目录行的级联子面板也算已显示的预览
        private bool _kbShownPreview;
        internal bool KbHasPreview() { return _kbShownPreview; }

        internal bool KbShowPreview()
        {
            if (!KbNavActive()) return false;
            _kbShownPreview = true;
            ActivateRowPreview(_kbRow);
            return true;
        }

        internal void KbClosePreview()
        {
            _kbShownPreview = false;
            ClosePreviewIfAny();
            try { Program.CloseAfter(_level); } catch { }
        }

        // 键盘选中 / hint 命中 → 激活预览（不打开路径）：目录行弹级联子列表，
        // 可预览文件弹内嵌预览，复用鼠标悬停的同一套调度
        private void ActivateRowPreview(RowLabel row)
        {
            if (row == null || row.IsDisposed) return;
            if (row.IsDir)
            {
                SchedulePreviewClose();
                ScheduleHover(row.Full, row.PointToScreen(Point.Empty).Y);
            }
            // 键盘 →/hint 命中是显式动作：不受「文件预览」自动触发开关限制，且跳过悬停延时
            else if (PreviewProvider.CanPreview(row.Full))
            {
                if (_onScheduleCloseAfter != null) _onScheduleCloseAfter(_level);
                SchedulePreview(row, true);
            }
        }

        // 首次用键盘导航时面板底部浮出一条用法提示（每次会话只出现一次），几秒后自灭
        private static bool _kbHintShown;
        private Label _kbHint;
        private Timer _kbHintTimer;

        private void ShowKbHintOnce()
        {
            if (_kbHintShown || IsDisposed) return;
            _kbHintShown = true;
            try
            {
                _kbHint = new Label
                {
                    Text = I18n.T("kb_hint"),
                    Dock = DockStyle.Bottom,
                    Height = 22,
                    BackColor = Config.ListHdbg,
                    ForeColor = Config.FgDir,
                    Font = new Font(Config.FontName, 8f),
                    TextAlign = ContentAlignment.MiddleCenter,
                };
                Controls.Add(_kbHint);
                _kbHint.BringToFront();
                _kbHintTimer = new Timer { Interval = 4000 };
                _kbHintTimer.Tick += delegate
                {
                    try { if (_kbHint != null) { _kbHint.Dispose(); _kbHint = null; } } catch { }
                    try { if (_kbHintTimer != null) { _kbHintTimer.Stop(); _kbHintTimer.Dispose(); _kbHintTimer = null; } } catch { }
                };
                _kbHintTimer.Start();
            }
            catch { }
        }

        private void SetKbRow(RowLabel r)
        {
            if (_kbRow != null && !_kbRow.IsDisposed) { _kbRow.Selected = false; ApplySel(_kbRow); }
            _kbRow = r;
            if (r != null && !r.IsDisposed) { r.Selected = true; ApplySel(r); ScrollRowIntoView(r); }
        }

        // 选中行滚出视口时拉回来（列表长、高亮在屏外跟随移动的基础）
        private void ScrollRowIntoView(RowLabel r)
        {
            if (_listPanel == null || r == null || r.IsDisposed || r.Top < 0) return;
            int top = r.Top - _listPanel.ContentScrollPos;
            if (top < 0) _listPanel.SetOffset(r.Top);
            else if (top + r.Height > _listPanel.Height) _listPanel.SetOffset(r.Top + r.Height - _listPanel.Height);
        }

        internal void ClearKbRow()
        {
            _kbShownPreview = false;
            if (_kbRow != null && !_kbRow.IsDisposed) { _kbRow.Selected = false; ApplySel(_kbRow); }
            _kbRow = null;
        }

        internal bool KbAction(Keys k)
        {
            if (!KbNavActive()) return false;
            var row = _kbRow;
            switch (k)
            {
                case Keys.Enter: ClearKbRow(); ActivateRow(row); return true;
                case Keys.Escape: ClearKbRow(); return true;
            }
            return false;
        }

        // ===== 全局 Hint：上报本面板可见行 + 所有可见按钮 + 内嵌预览窗按钮 =====
        // 编号、标记绘制与逐字符过滤统一由 Program 完成，保证所有 HE 窗口的码不冲突。
        internal void HintCollect(List<HintItem> list, bool copyMode)
        {
            if (list == null || IsDisposed || !Visible) return;
            if (_listPanel != null)
            {
                int scroll = _listPanel.ContentScrollPos;
                int viewH = _listPanel.Height;
                foreach (var r in _rows)
                {
                    if (r == null || r.IsDisposed || !r.Visible || r.Parent == null) continue;
                    int top = r.Top - scroll;
                    if (top + r.Height < 0 || top > viewH) continue;     // 只给视口内的行打标
                    var row = r;
                    list.Add(new HintItem
                    {
                        Ctl = row,
                        Exec = delegate
                        {
                            if (copyMode)
                            {
                                try { Clipboard.SetText(row.Full); ShowToast(I18n.T("copied")); } catch { }
                            }
                            else
                            {
                                SetKbRow(row);
                                ActivateRowPreview(row);   // 命中即激活预览，不直接打开路径
                            }
                        }
                    });
                }
            }
            AddHintButton(list, _copyBtn);
            AddHintButton(list, _editBtn);
            AddHintButton(list, _refreshBtn);
            AddHintButton(list, _pinBtn);
            AddHintButton(list, _pinCardBtn);
            // 行悬停时浮出的复制 / 重命名 / 固定 / 删除按钮（仅当前可见的那个）
            AddHintButton(list, _rowCopyBtn);
            AddHintButton(list, _rowRenameBtn);
            AddHintButton(list, _rowPinBtn);
            AddHintButton(list, _rowDelBtn);
            foreach (var b in _sortBtns.Values) AddHintButton(list, b);
            foreach (var b in _filterBtns) AddHintButton(list, b);
            if (_previewForm != null && !_previewForm.IsDisposed && _previewForm.Visible)
                _previewForm.HintCollect(list, copyMode);
        }

        // 可见按钮上报：命中时用反射触发 Click（Label 没有 PerformClick）
        internal static void AddHintButton(List<HintItem> list, Control c)
        {
            if (list == null || c == null || c.IsDisposed || !c.Visible || c.FindForm() == null) return;
            var ctl = c;
            list.Add(new HintItem { Ctl = ctl, Exec = delegate { HintClick(ctl); } });
        }

        internal static void HintClick(Control c)
        {
            if (c == null || c.IsDisposed) return;
            try
            {
                var m = typeof(Control).GetMethod("OnClick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (m != null) m.Invoke(c, new object[] { EventArgs.Empty });
            }
            catch { }
        }

        // 外部（桌面/资源管理器直接悬停）预览窗也要参与全局 hint
        internal static void HintCollectExt(List<HintItem> list, bool copyMode)
        {
            if (_extPreviewForm != null && !_extPreviewForm.IsDisposed && _extPreviewForm.Visible)
                _extPreviewForm.HintCollect(list, copyMode);
        }

        internal static Form ExtPreviewHost()
        {
            return _extPreviewForm != null && !_extPreviewForm.IsDisposed && _extPreviewForm.Visible
                ? (Form)_extPreviewForm : null;
        }

        // ←/→ 切换同文件夹文件（键盘钩子路由入口）：图片走预览窗原地换图，其余按原窗位置重建
        internal bool NavigatePreview(int dir)
        {
            var pf = _previewForm;
            if (pf == null || pf.IsDisposed || string.IsNullOrEmpty(pf.FilePath)) return false;
            if (pf.IsImagePreview) { pf.SwitchFile(dir); return true; }
            string cur = pf.FilePath;
            string dir0 = Path.GetDirectoryName(cur);
            if (string.IsNullOrEmpty(dir0)) return false;
            var files = PreviewForm.GetSortedFiles(dir0, f => PreviewProvider.CanPreview(f));
            if (files.Count < 2) return false;
            int idx = files.FindIndex(f => string.Equals(f, cur, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            int next = (idx + dir + files.Count) % files.Count;
            var keep = new Rectangle(pf.Left, pf.Top, pf.Width, pf.Height);
            ClosePreviewForm();
            _previewPath = files[next];
            ShowPreviewCore(files[next], null, keep);
            SyncRowToPath(files[next]);   // 面板行跟随：高亮当前预览的文件并滚到可见
            Program.DebugLog("[panel-preview] navigate " + dir + " -> " + files[next]);
            return true;
        }

        // 面板行跟随预览切换：选中当前预览的文件所在行并滚入视口（滚动在 SetKbRow 里统一做）
        private void SyncRowToPath(string path)
        {
            foreach (var r in _rows)
            {
                if (r == null || r.IsDisposed || !string.Equals(r.Full, path, StringComparison.OrdinalIgnoreCase)) continue;
                SetKbRow(r);
                return;
            }
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
        private static Rectangle _extPreviewRect;   // 源条目矩形：文件预览激活时在条目边缘画热区指示
        private static bool _extPreviewUseRect;     // true=按选中项矩形贴靠定位（空格 QuickLook），false=按鼠标定位（悬停/键盘切文件）

        public static void ScheduleFilePreview(string full, Point mouse, Rectangle sourceRect)
        {

            CancelFilePreview();
            if (!Config.PreviewFiles) return;
            if (!PreviewProvider.CanPreview(full)) return;
            _extRetryCount = 0;
            _extPreviewPath = full;
            _extPreviewRect = sourceRect;
            Program.DebugLog("[preview] schedule " + full);
            _extPreviewTimer = new Timer { Interval = Config.LongPressFor(true) ? Config.LongPressDelayMs : Config.PreviewDelayMsFor(full) };
            _extPreviewTimer.Tick += delegate(object s, EventArgs e)
            {
                ((Timer)s).Stop(); ((Timer)s).Dispose(); _extPreviewTimer = null;
                LoadFilePreview(_extPreviewPath, mouse);

            };
            _extPreviewTimer.Start();
        }

        public static void CancelFilePreview()
        {
            Program.HideHighlight();
            if (_extPreviewTimer != null) { _extPreviewTimer.Stop(); _extPreviewTimer.Dispose(); _extPreviewTimer = null; }
            _extPreviewPath = null;
            _extPreviewUseRect = false;   // 复位定位模式：默认走鼠标（悬停/键盘切文件）
            if (_extPreviewCloseTimer != null) { _extPreviewCloseTimer.Stop(); _extPreviewCloseTimer.Dispose(); _extPreviewCloseTimer = null; }
            if (_extPreviewForm != null && !_extPreviewForm.Pinned) { Program.DebugLog("[preview] cancel-existing-form"); CloseExtPreviewForm(false); }
        }

        public static bool FilePreviewContainsPoint(int x, int y)
        {
            return _extPreviewForm != null && _extPreviewForm.ContainsPoint(x, y);
        }

        // 外部直接预览存活（键盘钩子据此决定是否装钩/路由 ←→）
        internal static bool ExtPreviewActive()
        {
            return _extPreviewForm != null && !_extPreviewForm.IsDisposed && !string.IsNullOrEmpty(_extPreviewForm.FilePath);
        }

        // 当前外部预览显示的文件路径（未开预览返回 null）；空格预览的开关判定用
        internal static string ExtPreviewFilePath()
        {
            return ExtPreviewActive() ? _extPreviewForm.FilePath : null;
        }

        // 当前外部预览是否被钉住（空格不响应钉住预览的开关——用户钉住就是要它留着）
        internal static bool ExtPreviewPinned()
        {
            return _extPreviewForm != null && !_extPreviewForm.IsDisposed && _extPreviewForm.Pinned;
        }

        // 外部预览 ←/→ 切换：图片原地换图，其余按鼠标当前位置重建（LoadFilePreview 自带定位）
        internal static void NavigateExtPreview(int dir)
        {
            var pf = _extPreviewForm;
            if (pf == null || pf.IsDisposed || string.IsNullOrEmpty(pf.FilePath)) return;
            if (pf.IsImagePreview) { pf.SwitchFile(dir); return; }
            string cur = pf.FilePath;
            string dir0 = Path.GetDirectoryName(cur);
            if (string.IsNullOrEmpty(dir0)) return;
            var files = PreviewForm.GetSortedFiles(dir0, f => PreviewProvider.CanPreview(f));
            if (files.Count < 2) return;
            int idx = files.FindIndex(f => string.Equals(f, cur, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            int next = (idx + dir + files.Count) % files.Count;
            // 切换后让窗口留在当前位置，而不是跳到鼠标/旧选中项矩形。
            // 用一个"右边界=左边界+gap、高度=窗口高"的虚拟矩形，矩形贴靠算法会把窗放回原处。
            Point keep = pf.Location;
            _extPreviewUseRect = true;
            _extPreviewRect = new Rectangle(keep.X - Config.PopupOffset, keep.Y, Config.PopupOffset, pf.Height);
            LoadFilePreview(files[next], keep);
            // 打开着同目录面板时行跟随（高亮 + 滚动到可见）
            foreach (var p in Program.Panels)
            {
                if (p == null || p.IsDisposed || !p.Visible) continue;
                if (string.Equals(p.CurrentPath, dir0, StringComparison.OrdinalIgnoreCase)) p.SyncRowToPath(files[next]);
            }
            Program.DebugLog("[preview] navigate " + dir + " -> " + files[next]);
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

        // notifyOnClose：只有"用户真正离开而关闭"才复位 Program 触发闩锁。
        // 切换到新预览（取消/替换旧窗）时必须传 false——否则会把刚设好的 _extHoverFile
        // 清掉并置 _extNeedMove=true，导致鼠标停住后 stable 被挡、只能靠持续移动才触发。
        private static void CloseExtPreviewForm(bool notifyOnClose = true)
        {
            if (_extPreviewForm == null) return;
            if (PreviewForm.ExtDragActive) return;   // Ctrl 拖出文件进行中：冻结关闭，否则预览窗在 DoDragDrop 模态循环里被抽走、拖放视觉中断
            Program.HideHighlight();   // 预览关闭：源条目热区指示一并撤掉
            if (_extPreviewForm.Pinned) { Program.DebugLog("[preview] CLOSE detach (pinned)"); _extPreviewForm = null; return; }
            Program.DebugLog("[preview] CLOSE form"); try { _extPreviewForm.Close(); _extPreviewForm.Dispose(); } catch { } _extPreviewForm = null;
            if (notifyOnClose) Program.NotifyExtPreviewClosed();
        }


        internal static void ApplyHeader(PreviewForm form, string full)
        {
            string name = Path.GetFileName(full);
            string ext = Path.GetExtension(full).TrimStart('.').ToUpperInvariant();
            long size = 0;
            try { size = new FileInfo(full).Length; } catch { }
            string meta = ext + " · " + FormatSize(size);
            // lnk 头部显示指向目标：一眼看出快捷方式指向什么
            if (string.Equals(Path.GetExtension(full), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string t = PreviewProvider.GetLnkTarget(full);
                meta = !string.IsNullOrEmpty(t)
                    ? "LNK → " + (File.Exists(t) || Directory.Exists(t) ? Path.GetFileName(t) : t)
                    : "LNK · " + FormatSize(size);
            }
            form.SetHeader(name, meta);
        }

        // 空格预览/筛选架点击的立即预览入口：悬停流程由 Schedule 先登记 _extPreviewPath，
        // 而 LoadFilePreview 校验路径一致——直接调用会因登记缺失静默退出（空格预览
        // 出生即坏的根因）。这里先登记再加载
        internal static void PreviewNow(string full, Point mouse)
        {
            if (string.IsNullOrEmpty(full)) return;
            CancelFilePreview();
            _extPreviewPath = full;
            _extPreviewUseRect = false;
            LoadFilePreview(full, mouse);
        }

        // QuickLook 空格预览：按键盘选中项的矩形贴靠定位，鼠标坐标完全不参与
        internal static void PreviewNow(string full, Rectangle sourceRect)
        {
            if (string.IsNullOrEmpty(full)) return;
            CancelFilePreview();
            _extPreviewPath = full;
            _extPreviewRect = sourceRect;
            _extPreviewUseRect = true;
            LoadFilePreview(full, new Point(sourceRect.IsEmpty ? 0 : sourceRect.Left, sourceRect.IsEmpty ? 0 : sourceRect.Top));
        }

        internal static void LoadFilePreview(string full, Point mouse)
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
                        CloseExtPreviewForm(false);   // 替换为新预览，不复位触发闩锁
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
                        // 文件预览激活：高亮只画启用的触发带，与命中判定同源
                        Program.ShowHighlight(_extPreviewRect, Program.IsIconTile(_extPreviewRect));
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

            // 空格 QuickLook：按键盘选中项矩形贴靠，鼠标坐标完全不参与
            if (_extPreviewUseRect)
            {
                Rectangle ar = _extPreviewRect;
                int rw = _extPreviewForm.Width, rh = _extPreviewForm.Height;
                // 矩形必须落在某个显示器工作区内才算有效，否则退回该显示器中心
                Point anchorPt = ar.IsEmpty ? Point.Empty : new Point(ar.Left + ar.Width / 2, ar.Top + ar.Height / 2);
                var scr = Screen.FromPoint(anchorPt).WorkingArea;
                bool valid = !ar.IsEmpty && ar.Width > 0 && ar.Height > 0
                    && ar.Right > scr.Left && ar.Left < scr.Right && ar.Bottom > scr.Top && ar.Top < scr.Bottom;
                int rx, ry;
                if (!valid)
                {
                    rx = scr.Left + (scr.Width - rw) / 2;
                    ry = scr.Top + (scr.Height - rh) / 2;
                    Program.DebugLog("[space] rect-invalid " + ar + " -> center " + rx + "," + ry);
                }
                else
                {
                    int gap = Config.PopupOffset;
                    // 水平：优先贴右侧，放不下翻左侧，再放不下钳到工作区右缘
                    rx = ar.Right + gap;
                    if (rx + rw > scr.Right) rx = ar.Left - rw - gap;
                    if (rx < scr.Left || rx + rw > scr.Right) rx = Math.Max(scr.Left + 4, scr.Right - rw - 4);
                    // 垂直：以条目顶部对齐（预览窗通常远高于图标，居中会把它顶到屏幕最上方）；
                    // 底部超出则上移，顶部超出则钳到工作区顶部
                    ry = ar.Top;
                    if (ry + rh > scr.Bottom) ry = scr.Bottom - rh - 4;
                    if (ry < scr.Top) ry = scr.Top + 4;
                    Program.DebugLog("[space] rect=" + ar + " scr=" + scr + " -> " + rx + "," + ry);
                }
                _extPreviewForm.Location = new Point(rx, ry);
                return;
            }

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
                // 右侧触发带分隔线热区（±4px）：拖动调整带宽。
                // 默认布局下这条线始终可见，必须始终可拖；RowHideBandLine 只对互换布局生效，
                // 否则旧配置里残留的 true 会让默认布局的线看得见却拖不动。
                int bandW = RowLabel.TriggerBandW();
                bool bandLine = bandW > 0 && (Config.RowSwapSides ? !Config.RowHideBandLine : row.Width - bandW > 8);
                if (bandLine && Math.Abs(e.X - (row.Width - bandW)) <= 4)
                {
                    _bandDrag = true;
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
            // 拖触发带分隔线中：线跟手，带宽 = 行宽 - 线位置（最小 8 保证线始终可再拖动），
            // 按钮组实时左移/右移
            if (_bandDrag)
            {
                int v = row.Width - e.X;
                if (v < 8) v = 8;
                if (v > 80) v = 80;
                if (v != Config.RowTriggerW)
                {
                    Config.RowTriggerW = v;
                    foreach (var r in _rows)
                        if (!r.IsDisposed) r.Invalidate();
                    _fnLayoutRowButtons(_rowBtnRow);
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
            // 右侧触发带：同一行内横移跨过分隔线时切换调度——仅跨线时动作（_wasInBand 守卫），
            // 避免带内微动每次 MouseMove 重复调度（子面板弹出后 _hoverTimer=null 又被重新触发→闪烁）
            if (e.Button == MouseButtons.None && _rowEdit == null && !SearchActive)
            {
                int bandW = RowLabel.TriggerBandW();
                int bandX = row.Width - bandW;
                bool inBand = e.X >= _fnTriggerStartX(row);
                if (inBand != _wasInBand)
                {
                    _wasInBand = inBand;
                    if (!inBand)
                    {
                        if (_hoverTimer != null || _previewTimer != null)
                        {
                            Program.DebugLog("[row] band-zone hold level=" + _level + " " + row.Name);
                            CancelHover();
                            // 不弹新不杀旧：只取消未触发的调度；已开的预览窗保留——
                            // 鼠标在行内从触发带移向名称/按钮时，半路不再被杀
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
                // 分隔线热区：↔ 光标提示可拖（隐藏触发线时该线不可见，不给拖动提示）
                bool onSplit = Config.RowSwapSides && Math.Abs(e.X - _rowSideZone) <= 4;
                bool onBand = bandW > 0 && (Config.RowSwapSides ? !Config.RowHideBandLine : bandX > 8)
                    && Math.Abs(e.X - bandX) <= 4;
                if (onSplit || onBand) row.Cursor = Cursors.SizeWE;
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
                Config.RowSplitX = Dpi.UnS(_rowSideZone);
                Config.Save();
                return;
            }
            if (_bandDrag)
            {
                _bandDrag = false;
                try { row.Capture = false; } catch { }
                Config.Save();
                return;
            }
            if (_dragged) { _dragged = false; return; }
            if (e.Button != MouseButtons.Left) return;
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control) { ToggleSel(row); return; }
            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift) { RangeSel(row); return; }
            ClearSel();
            ActivateRow(row);
        }

        // 打开行：来源是文件对话框且该行是文件夹时，让对话框进入该目录，并在本面板继续列出其内容
        private void ActivateRow(RowLabel row)
        {
            if (row == null) return;
            if (DialogHwnd != IntPtr.Zero)
            {
                if (!DialogSupport.IsDialogAlive(DialogHwnd)) DialogHwnd = IntPtr.Zero;
                else if (row.IsDir && DialogSupport.Navigate(DialogHwnd, row.Full))
                {
                    // 延后重建：当前还在行控件的鼠标/键盘事件里，直接 ShowPanel 会销毁正在派发的控件
                    string dir = row.Full;
                    Program.DebugLog("[dialog] nav-from-panel " + dir);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!IsDisposed) ShowPanel(dir, Location.X, Location.Y);
                    });
                    return;
                }
            }
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

        // 排序栏 "+"：在当前目录新建文件夹并进入行内改名；默认名冲突时自动加序号
        private void NewFolderHere()
        {
            if (_path == null || !Directory.Exists(_path)) return;
            string baseName = I18n.T("new_folder");
            string full = Path.Combine(_path, baseName);
            for (int i = 1; Directory.Exists(full) || File.Exists(full); i++)
                full = Path.Combine(_path, baseName + " (" + i + ")");
            try { Directory.CreateDirectory(full); }
            catch (Exception ex)
            {
                Program.Log("[新建文件夹失败] " + ex.Message);
                ShowToast(I18n.T("new_folder_fail"));
                return;
            }
            RefreshPanel();
            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed) return;
                var row = _rows.FirstOrDefault(r => string.Equals(r.Full, full, StringComparison.OrdinalIgnoreCase));
                if (row != null) BeginRowRename(row);
            });
        }

        private void OnMoveDone()
        {
            // 修复：多选拖放时每个文件"叮"一声——根因是这里对每次复制/移动完成都播一声系统提示音，
            // 多选时 DoCopy/DoMove 按文件逐个回调本方法，故一声接一声。删除提示音，
            // 仅保留刷新面板；拖放的复制/移动行为与指针反馈均不变。
            try { RefreshPanel(); } catch { }
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
            tb.TextChanged += (s, e) => { if (_rowEdit == tb) tb.ScrollToCaret(); };
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
            if (_unpinCloseTimer != null) _unpinCloseTimer.Stop();
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

        // 行内操作按钮定位：默认布局下整组落在右侧触发带分隔线左侧；
        // 互换布局下落在左侧安全区（与名称左分栏线一致）
        private void _fnLayoutRowButtons(RowLabel row)
        {
            if (row == null || row.IsDisposed) return;
            int btnY = row.Top + Math.Max(0, (row.Height - RowBtnH) / 2);
            bool sw = Config.RowSwapSides;
            int bandW = sw ? 0 : RowLabel.TriggerBandW();
            int baseX = sw ? RowBtnInset : row.Width - bandW - RowBtnInset;
            Func<int, int> bx = i => sw ? baseX + i * RowBtnStep : baseX - (i + 1) * RowBtnW - i * RowBtnGap;
            var btns = new Label[] { _rowDelBtn, _rowPinBtn, _rowCopyBtn, _rowRenameBtn };
            for (int i = 0; i < btns.Length; i++)
            {
                var b = btns[i];
                if (b == null || b.IsDisposed) continue;
                if (b == _rowRenameBtn && _rowEdit != null) { b.Visible = false; continue; }
                b.Location = new Point(bx(i), btnY);
                b.Visible = true;
                b.BringToFront();
            }
        }

        // 清掉除 keep 以外所有行的悬停高亮（活动目录行/右键上下文行/滚轮预览行保留）
        private void _fnClearRowHighlight(RowLabel keep)
        {
            foreach (var r in _rows)
            {
                if (r == keep || r.IsDisposed) continue;
                if (r == _activeDirRow || r == _contextRow || r == _hlRow) continue;
                if (r.BackColor == Config.ListHv) ApplySel(r);
            }
            _fnResetRowBtnHover();
        }

        // 行内操作按钮的悬停色块：按钮组被隐藏/挪到新行时清掉，避免上一个按钮残留高亮
        private void _fnResetRowBtnHover()
        {
            foreach (var b in new Label[] { _rowDelBtn, _rowCopyBtn, _rowPinBtn, _rowRenameBtn })
            {
                var rb = b as RowActionButton;
                if (rb != null && rb.Hovered) { rb.Hovered = false; rb.Invalidate(); }
            }
        }

        // 双击顶部操作栏：资源管理器打开当前面板所在文件夹（子面板各自打开自己的目录）
        // 手动双击判定：首击会进模态拖动（ReleaseCapture+HTCAPTION 移动循环会吞掉首个抬起），
        // 系统双击时序被打乱、e.Clicks 经常到不了 2——自行按系统双击时间/范围判定
        private int _hdrLastDownTick;
        private Point _hdrLastDownPos;
        private Timer _titleCopyTimer;   // 延迟复制路径：双击的第一次抬起不立即复制，等确认非双击后再复制
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

        // 当前激活的 HE 窗口：固定按钮显示绿色（固定=实心绿、未固定=空心绿），
        // 非激活窗口保持列表默认色。程序主循环按鼠标/前台变化节流刷新。
        internal void RefreshPinIcon()
        {
            if (_pinBtn == null || _pinBtn.IsDisposed) return;
            PreviewForm.SetPinIcon(_pinBtn, _pinned, Config.ListFg);
        }

        internal void RefreshChildPreviewPin()
        {
            if (_previewForm != null && !_previewForm.IsDisposed && _previewForm.Visible) _previewForm.RefreshPinIcon();
        }

        internal static void RefreshExtPreviewPin()
        {
            if (_extPreviewForm != null && !_extPreviewForm.IsDisposed && _extPreviewForm.Visible) _extPreviewForm.RefreshPinIcon();
        }

        private void TogglePinPopup()
        {
            _pinned = !_pinned;
            Program.DebugLog("[panel] TogglePinPopup level=" + _level + " path=" + _path + " pinned=" + _pinned);
            RefreshPinIcon();
            if (_refreshBtn != null) _refreshBtn.Visible = _pinned;   // 刷新按钮只在固定面板出现
            // 固定瞬间锁住当前尺寸，取消固定后回到内容自适应
            if (_pinned)
            {
                _pinnedW = ClientSize.Width; _pinnedH = ClientSize.Height;
                if (_unpinCloseTimer != null) _unpinCloseTimer.Stop();   // 重新固定：终止"离开即关"
            }
            else
            {
                _pinnedW = 0; _pinnedH = 0;
                _lastUnpinTick = Environment.TickCount;
                // 取消固定后自动关闭：只靠主循环 MaybeClose 会在鼠标停在其他面板/预览上时无限期不关，
                // 用户感知就是"取消了固定窗口却不自动关闭"。200ms 轮询，光标一离开本面板立即关
                if (_unpinCloseTimer == null)
                {
                    _unpinCloseTimer = new Timer { Interval = 200 };
                    _unpinCloseTimer.Tick += (s, e) =>
                    {
                        if (IsDisposed || !IsHandleCreated || !Visible || _pinned) { _unpinCloseTimer.Stop(); return; }
                        NativeMethods.POINT pt;
                        NativeMethods.GetCursorPos(out pt);
                        if (!ContainsPoint(pt.X, pt.Y))
                        {
                            _unpinCloseTimer.Stop();
                            Program.DebugLog("[panel] unpin auto-close level=" + _level + " path=" + _path);
                            ClosePanel();
                        }
                    };
                }
                _unpinCloseTimer.Start();
            }
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

    // 源条目激活指示（方案 E）：真正的半透明分层窗口（WS_EX_LAYERED + 逐像素 Alpha）。
    // 之前用 TransparencyKey 只能二值透明，预混的"淡底"其实是不透明实色块——既盖住图标，
    // 视觉上又像阴影。分层窗口下 10% accent 是真半透明：图标透出、无阴影、无整行框。
    // 热区就是触发带本身（关=整行、1=左缘、2=右缘、3=两侧），贴边窄条不覆盖主体。
    internal class HighlightOverlay : Form
    {
        private readonly Rectangle _inner;        // 内缩触发区（窗口内相对坐标）
        private readonly bool _fullItem;           // 全零：整条目淡底

        public HighlightOverlay(Rectangle r, bool iconTile)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(1, 1);
            Bounds = r;

            _fullItem = Program.TriggersOff(iconTile);
            if (_fullItem)
            {
                _inner = new Rectangle(0, 0, Width, Height);
            }
            else
            {
                Rectangle z = Program.InnerRect(r, iconTile);
                _inner = new Rectangle(z.X - r.X, z.Y - r.Y, z.Width, z.Height);
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // 关键：高亮窗盖在源条目上，必须鼠标穿透，否则会拦截资源管理器的 hover 命中——
                // 导致"鼠标离开→关预览→闩锁复位→又回到条目"的反复触发，且资源管理器自身
                // 的条目高亮阴影因收不到正常 leave 而残留在名称下方/右侧。
                cp.ExStyle |= 0x00080000    // WS_EX_LAYERED（逐像素 Alpha）
                           | 0x00000020    // WS_EX_TRANSPARENT（鼠标穿透）
                           | 0x08000000;   // WS_EX_NOACTIVATE（不抢焦点）
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Win11.Apply 把 CS_DROPSHADOW 设在 Form 共享的类样式上，本窗作为 Form 会继承到
            // 系统阴影——表现为条目框下方/右侧那条边界清晰的投影。高亮层只要无阴影描边。
            Win11.RemoveDropShadow(Handle);
            RenderLayered();
        }

        protected override void OnPaint(PaintEventArgs e) { /* 全交给分层窗口绘制 */ }

        private void RenderLayered()
        {
            if (IsDisposed || !IsHandleCreated || Width <= 0 || Height <= 0) return;
            int w = Width, h = Height;
            Color ac = Config.Accent;
            int barW = Dpi.S(3);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 淡底：只覆盖内缩后的触发区（全零=整条目）
                using (var b = new SolidBrush(Color.FromArgb(26, ac.R, ac.G, ac.B)))
                    g.FillRectangle(b, _inner);
                // 触发区边框（全条目模式不画）：颜色取配色方案的边框分隔线
                if (!_fullItem)
                    using (var p = new Pen(Config.ListBd, barW))
                        g.DrawRectangle(p, new Rectangle(_inner.Left, _inner.Top,
                            Math.Max(1, _inner.Width - 1), Math.Max(1, _inner.Height - 1)));

                // 预乘 alpha 后经 CreateDIBSection 直推（同 PinCardForm：GetHbitmap 会把低 alpha 拍成白边）
                byte[] px;
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                px = new byte[bd.Stride * h];
                Marshal.Copy(bd.Scan0, px, 0, px.Length);
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte a = px[i + 3];
                    if (a < 255)
                    {
                        px[i] = (byte)(px[i] * a / 255);
                        px[i + 1] = (byte)(px[i + 1] * a / 255);
                        px[i + 2] = (byte)(px[i + 2] * a / 255);
                    }
                }
                Marshal.Copy(px, 0, bd.Scan0, px.Length);
                bmp.UnlockBits(bd);

                IntPtr hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
                try
                {
                    var bmi = new NativeMethods.BITMAPINFO();
                    bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
                    bmi.bmiHeader.biWidth = w;
                    bmi.bmiHeader.biHeight = -h;
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0;
                    IntPtr bits;
                    IntPtr dib = NativeMethods.CreateDIBSection(hdcScreen, ref bmi, 0, out bits, IntPtr.Zero, 0);
                    Marshal.Copy(px, 0, bits, px.Length);
                    IntPtr hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
                    IntPtr hOld = NativeMethods.SelectObject(hdcMem, dib);
                    var ptDst = new NativeMethods.POINT { X = Left, Y = Top };
                    var ptSrc = new NativeMethods.POINT { X = 0, Y = 0 };
                    var sz = new Size(w, h);
                    var blend = new NativeMethods.BLENDFUNCTION
                    {
                        BlendOp = NativeMethods.AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = NativeMethods.AC_SRC_ALPHA
                    };
                    NativeMethods.UpdateLayeredWindow(Handle, hdcScreen, ref ptDst, ref sz, hdcMem, ref ptSrc, 0, ref blend, NativeMethods.ULW_ALPHA);
                    NativeMethods.SelectObject(hdcMem, hOld);
                    NativeMethods.DeleteObject(dib);
                    NativeMethods.DeleteDC(hdcMem);
                }
                finally { NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen); }
            }
        }
    }
}
