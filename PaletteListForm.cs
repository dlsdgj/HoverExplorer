using System;
using System.Drawing;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 卡片配色速查列表：滚轮在某张卡片上切换配色时弹出，选中项随滚动实时高亮；
    // 鼠标离开卡片（且不在列表上）后自动关闭，停在列表上可点行选定。
    // 每行一条迷你标签带（配色底色 + 左侧类型色条 + 右端徽章块）作单列预览。
    // 配色应用目标：桌面卡片（PinCardForm）与收藏·历史卡片（EntryCardForm）共用调色列表。
    // Bounds/IsDisposed 由 Control 基类隐式实现
    internal interface IPaletteTarget
    {
        void SetPalette(int index, bool persist);
        Rectangle Bounds { get; }
        bool IsDisposed { get; }
    }

    // 不抢焦点（ShowWithoutActivation + MA_NOACTIVATE），否则会打断用户正在操作的窗口。
    internal class PaletteListForm : Form
    {
        private static int RowH { get { return Dpi.S(24); } }
        private static int PadX { get { return Dpi.S(8); } }
        private static int PadY { get { return Dpi.S(5); } }
        private static int TapeW { get { return Dpi.S(46); } }
        private static int TapeH { get { return Dpi.S(12); } }
        private const int LeaveDelay = 350;   // 离开后延迟关闭：卡片与列表之间 4px 缝隙要放行
        private const int PollInterval = 120;

        private static PaletteListForm _inst;

        private int _index;
        private int _scroll;                // 首行索引：列表高于可用空间时滚动显示，选中项始终可见
        private int _contentH;              // 全部方案所需高度（未限高）
        private IPaletteTarget _owner;   // Form 基类带 Bounds/IsDisposed，接口只补 SetPalette
        private readonly Timer _timer;      // 轮询：鼠标是否还在卡片/列表上
        private DateTime _leaveSince = DateTime.MinValue;

        public static PaletteListForm Instance
        {
            get { return (_inst != null && !_inst.IsDisposed && _inst.IsHandleCreated) ? _inst : null; }
        }

        private PaletteListForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Config.BG;
            Font = new Font(Config.FontName, 9f);

            Width = Dpi.S(232);
            _fnSyncSize();

            _timer = new Timer { Interval = PollInterval };
            _timer.Tick += (s, e) => _fnCheckHover();

            MouseDown += (s, e) =>
            {
                int i = HitRow(e.Location);
                if (i < 0) return;
                if (e.Button == MouseButtons.Middle)
                {
                    // 中键删除该方案，列表保持打开：不 Hide、不 Dispose，也先不收缩 Height，
                    // 否则光标会落到变矮后的窗口外，被 _fnCheckHover 当作"离开"而自动关闭。
                    if (CardPalettes.All.Count <= 1) return;
                    CardPalettes.All.RemoveAt(i);
                    int n = CardPalettes.All.Count;
                    if (_index > i) _index--;
                    if (_index >= n) _index = n - 1;
                    if (_index < 0) _index = 0;
                    CardPalettes.Save();
                    Program.ApplyThemeChange();     // 内含 Config.Save()，让所有卡片重绘
                    if (_owner != null && !_owner.IsDisposed) _owner.SetPalette(_index, true);
                    _leaveSince = DateTime.MinValue;
                    _fnSyncSize();
                    if (_owner != null && !_owner.IsDisposed) _fnPlace(_owner.Bounds);
                    _fnEnsureVisible();
                    Invalidate();
                    return;
                }
                if (e.Button != MouseButtons.Left) return;
                if (_owner != null && !_owner.IsDisposed) _owner.SetPalette(i, true);
                _index = i;
                Invalidate();
                _fnHide();
            };
        }

        // 滚轮触发：列表已存在就只更新选中项（实时高亮），否则新建并定位到卡片下方
        public static void ShowFor(int index, Rectangle anchor, IPaletteTarget card)
        {
            var f = Instance;
            if (f == null)
            {
                f = new PaletteListForm();
                _inst = f;
            }
            f._owner = card;
            f._index = index;
            f._leaveSince = DateTime.MinValue;
            f._fnSyncSize();   // 方案数可能已变（批量导入/删除），单例高度不能沿用构造时的值
            f._fnPlace(anchor);
            f._fnEnsureVisible();
            if (!f.Visible) f.Show();
            f.Invalidate();
            f._timer.Start();
        }

        // 计算全部方案所需高度；_fnPlace 会按工作区可用空间限高，先同步再定位
        private void _fnSyncSize()
        {
            _contentH = PadY * 2 + Math.Max(1, CardPalettes.All.Count) * RowH;
            Height = _contentH;
        }

        // 设置面板里保存/删除/批量导入后即时通知浮层，不必等下次滚轮或重启进程
        public static void RefreshSchemes()
        {
            var f = Instance;
            if (f == null) return;
            int n = CardPalettes.All.Count;
            if (f._index >= n) f._index = Math.Max(0, n - 1);
            f._fnSyncSize();
            if (f._owner != null && !f._owner.IsDisposed) f._fnPlace(f._owner.Bounds);
            f._fnEnsureVisible();
            f._leaveSince = DateTime.MinValue;
            f.Invalidate();
        }

        public static void HideNow()
        {
            var f = Instance;
            if (f == null) return;
            f._fnHide();
        }

        private void _fnHide()
        {
            _timer.Stop();
            _leaveSince = DateTime.MinValue;
            if (IsHandleCreated && Visible) Hide();
        }

        private int HitRow(Point p)
        {
            if (p.Y < PadY || p.Y >= Height - PadY) return -1;
            int i = _scroll + (p.Y - PadY) / RowH;
            if (i < _scroll || i >= CardPalettes.All.Count) return -1;
            if (i >= _scroll + VisibleRows()) return -1;
            return i;
        }

        private int VisibleRows()
        {
            return Math.Max(1, (Height - PadY * 2) / RowH);
        }

        // 鼠标离开卡片（也不在列表上）→ 延迟一小会儿关闭；卡片没了也立刻关
        private void _fnCheckHover()
        {
            if (_owner == null || _owner.IsDisposed) { _fnHide(); return; }
            var p = Cursor.Position;
            bool on = _owner.Bounds.Contains(p) || Bounds.Contains(p);
            if (on)
            {
                _leaveSince = DateTime.MinValue;
                return;
            }
            if (_leaveSince == DateTime.MinValue) { _leaveSince = DateTime.UtcNow; return; }
            if ((DateTime.UtcNow - _leaveSince).TotalMilliseconds >= LeaveDelay) _fnHide();
        }

        // 定位：优先卡片下方，放不下翻上方；两侧都放不下时限高并在列表内滚动，
        // 保证浮层始终落在卡片之外的空白区域，不遮挡卡片本身。
        private void _fnPlace(Rectangle anchor)
        {
            var wa = Screen.GetWorkingArea(new Point(anchor.Left, anchor.Top));
            int gap = Dpi.S(4);
            int above = Math.Max(0, anchor.Top - wa.Top - gap);
            int below = Math.Max(0, wa.Bottom - anchor.Bottom - gap);
            int minH = PadY * 2 + RowH;

            bool up = (_contentH > below) && (above >= _contentH || above > below);
            int room = up ? above : below;
            if (room < minH)
            {
                if (!up && above > room) { up = true; room = above; }
                else if (up && below > room) { up = false; room = below; }
            }
            if (room < minH) room = minH;

            int h = Math.Min(_contentH, room);
            Height = h;
            int x = Math.Max(wa.Left + gap, Math.Min(anchor.Left, wa.Right - Width - gap));
            int y = up ? anchor.Top - gap - h : anchor.Bottom + gap;
            if (up) y = Math.Max(wa.Top + gap, y);
            else y = Math.Min(y, wa.Bottom - gap - h);
            Location = new Point(x, y);
        }

        // 选中行随滚轮实时高亮；限高时把选中行滚进可见区，避免"选中了却看不到"
        private void _fnEnsureVisible()
        {
            int count = CardPalettes.All.Count;
            int visible = VisibleRows();
            int maxScroll = Math.Max(0, count - visible);
            if (_scroll > maxScroll) _scroll = maxScroll;
            if (_scroll < 0) _scroll = 0;
            if (_index < _scroll) _scroll = _index;
            else if (_index >= _scroll + visible) _scroll = _index - visible + 1;
            if (_scroll > maxScroll) _scroll = maxScroll;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // MA_NOACTIVATE
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            var list = CardPalettes.All;
            int last = Math.Min(list.Count, _scroll + VisibleRows());
            for (int i = _scroll; i < last; i++)
            {
                var p = list[i];
                int top = PadY + (i - _scroll) * RowH;
                bool sel = i == _index;
                if (sel)
                {
                    using (var b = new SolidBrush(Config.BgHover))
                        g.FillRectangle(b, 0, top, Width, RowH);
                    using (var b = new SolidBrush(Config.Accent))
                        g.FillRectangle(b, 0, top, 3, RowH);
                }
                // 单列预览：一条迷你标签带（底色 + 左色条 + 右端徽章块）
                int tx = PadX, ty = top + (RowH - TapeH) / 2;
                using (var b = new SolidBrush(p.Bg))
                    g.FillRectangle(b, tx, ty, TapeW, TapeH);
                using (var b = new SolidBrush(p.StripColor))
                    g.FillRectangle(b, tx, ty, 4, TapeH);
                using (var b = new SolidBrush(p.BadgeBg))
                    g.FillRectangle(b, tx + TapeW - Dpi.S(11), ty + Dpi.S(2), Dpi.S(9), TapeH - Dpi.S(4));
                if (p.StripWidth > 0 && p.StripColor.A > 0)
                {
                    using (var pen = new Pen(p.StripColor, p.StripWidth))
                        g.DrawRectangle(pen, tx, ty, TapeW, TapeH);
                }
                else
                {
                    using (var pen = new Pen(Color.FromArgb(60, 128, 128, 128)))
                        g.DrawRectangle(pen, tx, ty, TapeW, TapeH);
                }

                string text = (i + 1) + ". " + p.Name;
                Color fg = sel ? Config.FG : Config.PlaceholderFg;
                TextRenderer.DrawText(g, text, Font, new Point(tx + TapeW + Dpi.S(8), top + Dpi.S(5)), fg,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            using (var pen = new Pen(Config.Border))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
