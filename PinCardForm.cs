using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 桌面卡片·标签带样式（原型方案 D）：24px 高标签条 + 名字 + 扩展名徽章 + 左色条。
    // 全自绘（分层窗口 WS_EX_LAYERED + UpdateLayeredWindow）→ 逐像素 Alpha 透明。
    // 宽度恒定（名字+徽章），悬停提亮一档，sub 悬停显示在徽章右侧。
    // text 卡片：主操作=粘贴（MA_NOACTIVATE 不抢焦点 + 模拟 Ctrl+V 贴回原窗口），
    // 悬停 800ms 弹可编辑预览（复用 PreviewForm），关闭时写回内容。
    internal class PinCardForm : Form, IPaletteTarget
    {
        private readonly PinnedItem _item;
        private readonly string _path;   // = _item.Path，兼容外部按路径定位
        private readonly bool _isFile, _isUrl, _isText, _isDir;
        private Color _typeColor;   // 保留字段兼容（已不按类型着色，统一用配色 StripColor）
        private int _paletteIndex;  // 本卡片自己的配色索引（滚轮切换只影响这张卡）
        private readonly bool _isToday;      // 今天修改过的文件 → 名字左侧状态点
        private string _name, _ext, _sub;

        // 布局矩形（自绘命中测试用）
        private Rectangle _nameRect, _badgeRect, _subRect;
        private Font _nameFont, _badgeFont, _subFont;
        private bool _subVisible, _badgeHover, _badgeDown;

        private Point _dragStart, _formStart;
        private bool _moved, _hover, _dropHighlight;
        private const int ShadowPad = 2;   // 自绘阴影宽度（右下偏移，跟着圆角走）

        // 标签带配色：来自 CardPalettes（与面板主题 Config.Theme 无关，滚轮切换）
        private CardPalette _pal;
        private Color _bg, _bgHover, _fg, _fgSub;      // 由 _fnApplyPaletteColors() 按当前配色重算
        private Color _badgeBg, _badgeFg;              // 扩展名徽章的底/字色（文件夹卡片反色）

        private Timer _textPreviewTimer;
        private Timer _textHoverTimer;   // 预览开着时轮询：鼠标离开卡片∪预览且未在编辑 → 关（与文件预览行为一致）
        private Timer _badgeTimer;       // 徽章悬停计时：文件夹→弹列表 / 可预览文件→弹预览
        private Timer _badgeDragTimer;   // 拖拽悬停徽标 ~400ms → 弹簧展开文件夹列表
        private PreviewForm _filePreview; // file 卡徽章悬停弹出的内容预览
        private Timer _fileHoverTimer;   // 该预览的轮询关闭
        private Timer _panelWatchTimer;  // 卡片弹出的文件夹列表轮询：鼠标离开 卡片∪面板 → 收起
        private PreviewForm _textPreview;
        private bool _textDirty;

        public PinCardForm(PinnedItem item)
        {
            _item = item;
            _path = item.Path;
            _isFile = item.Kind == "file";
            _isUrl = item.Kind == "url";
            _isText = item.Kind == "text";
            // 文件夹先判定：文件夹卡片整条填类型色（配色里的 folderText 作文字色）
            _isDir = _isFile && Directory.Exists(item.Path);
            // 每张卡片记自己的配色；-1 的新卡片沿用默认（最近一次用过的配色）
            _paletteIndex = (item.Palette >= 0 && item.Palette < CardPalettes.All.Count)
                ? item.Palette : Config.CardPaletteIndex;

            _fnApplyPaletteColors();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(item.X, item.Y);
            TopMost = true;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;   // 高度按像素固定，禁止 DPI/字体缩放

            Height = 24;

            // ----- 内容提取 -----
            long size = 0;
            _isToday = false;
            if (_isFile)
            {
                _name = Path.GetFileName(item.Path);
                _ext = _isDir ? "DIR" : (Path.HasExtension(item.Path) ? Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant() : "FILE");
                try { var fi = new FileInfo(item.Path); size = fi.Length; _isToday = fi.LastWriteTime.Date == DateTime.Today; } catch { }
                _sub = _isDir ? _fnCountSub(item.Path) : Config.FmtSize(size);
            }
            else if (_isUrl)
            {
                string dom = Ingest.DomainOf(item.Path);
                _name = !string.IsNullOrEmpty(item.Title) ? item.Title : dom;
                _ext = "URL";
                _sub = dom;
            }
            else
            {
                _name = !string.IsNullOrEmpty(item.Title) ? item.Title : Ingest.Ellipsis(Ingest.FirstLine(item.Path), 18);
                _ext = "TXT";
                _sub = item.Path.Length + " 字";
            }

            // ----- 布局：宽度恒定 = 名字 + 徽章 + 右 padding（悬停不改变宽度） -----
            _fnLayout();

            // ----- 事件（全自绘命中测试）-----
            MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    _dragStart = Cursor.Position; _formStart = Location; _moved = false;
                    if (_badgeRect.Contains(e.Location)) _badgeDown = true;
                    else Capture = true;
                }
                if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right) ReleaseStuckDrag();
                if (e.Button == MouseButtons.Middle) Program.Unpin(_item.Kind, _item.Path);
                else if (e.Button == MouseButtons.Right)
                {
                    if (_isFile) ShellContextMenu.Show(_item.Path, Handle, () => Program.Unpin(_item.Kind, _item.Path), _fnAddPaletteFromClipboard);
                    else ShowCardMenu();
                }
            };
            MouseMove += (s, e) =>
            {
                // 徽标悬停跟踪
                bool inBadge = _badgeRect.Contains(e.Location);
                if (inBadge != _badgeHover)
                {
                    _badgeHover = inBadge;
                    _fnRender();
                    if (inBadge) _fnScheduleBadgeAction();
                    else if (_badgeTimer != null) _badgeTimer.Stop();
                }
                Cursor = inBadge ? Cursors.Hand : Cursors.Default;
                // 拖动（点徽标期间不拖动，避免执行文件时残留基准导致移位）
                if (_badgeDown) return;
                if (e.Button != MouseButtons.Left) return;
                if (!_moved)
                {
                    var d = Cursor.Position;
                    if (Math.Abs(d.X - _dragStart.X) > 6 || Math.Abs(d.Y - _dragStart.Y) > 6) _moved = true;
                }
                if (_moved) { var d = Cursor.Position; Location = new Point(_formStart.X + d.X - _dragStart.X, _formStart.Y + d.Y - _dragStart.Y); _fnRender(); }
            };
            MouseUp += (s, e) =>
            {
                if (Capture) Capture = false;
                if (e.Button == MouseButtons.Left)
                {
                    if (_badgeDown && !_moved && _badgeRect.Contains(e.Location))
                    { if (_isText) _fnPaste(); else Run(); }
                    _badgeDown = false;
                    if (_moved) Program.UpdatePinPos(_item.Kind, _item.Path, Left, Top);
                    _moved = false;
                }
            };
            DoubleClick += (s, e) => { try { Clipboard.SetText(_item.Path); Program.ShowBalloon("HoverExplorer", I18n.T("card_copied")); } catch { } };
            MouseEnter += (s, e) => _fnSetHover(true);
            MouseLeave += (s, e) => { if (!_fnCardContainsCursor()) _fnSetHover(false); };

            FormClosed += (s, e) =>
            {
                if (_textPreviewTimer != null) { _textPreviewTimer.Stop(); _textPreviewTimer.Dispose(); _textPreviewTimer = null; }
                if (_textHoverTimer != null) { _textHoverTimer.Stop(); _textHoverTimer.Dispose(); _textHoverTimer = null; }
                _fnCloseTextPreview();
                if (_badgeTimer != null) { _badgeTimer.Stop(); _badgeTimer.Dispose(); _badgeTimer = null; }
                if (_badgeDragTimer != null) { _badgeDragTimer.Stop(); _badgeDragTimer.Dispose(); _badgeDragTimer = null; }
                if (_fileHoverTimer != null) { _fileHoverTimer.Stop(); _fileHoverTimer.Dispose(); _fileHoverTimer = null; }
                if (_panelWatchTimer != null) { _panelWatchTimer.Stop(); _panelWatchTimer.Dispose(); _panelWatchTimer = null; }
                _fnCloseFilePreview();
            };

            // 文件夹卡片：接受拖入的文件/目录（分层窗口无子控件，form 级 AllowDrop 即可）
            if (_isDir) _fnEnableDrop(this);

            _fnRender();
        }

        // 徽标拖放 = 弹簧展开：DragOver 自行调度（OLE 拖拽期间 MouseEnter 不触发），
        // 拖停 ~400ms 调 _fnOpenFolderList（ShowCardFolderList 内部对已开同目录列表幂等）

        private void _fnScheduleBadgeDragOpen()
        {
            if (_badgeDragTimer == null)
            {
                _badgeDragTimer = new Timer { Interval = 400 };
                _badgeDragTimer.Tick += (s, e) =>
                {
                    _badgeDragTimer.Stop();
                    if (IsDisposed || !_isDir) return;
                    _fnOpenFolderList();
                };
            }
            if (_badgeDragTimer.Enabled) return;   // 该徽标已在计时（DragOver 连发不重置）
            _badgeDragTimer.Stop();
            _badgeDragTimer.Start();
        }

        private void _fnCancelBadgeDragOpen()
        {
            if (_badgeDragTimer == null) return;
            _badgeDragTimer.Stop();
            _badgeDragTimer.Dispose();
            _badgeDragTimer = null;
        }

        // 释放拖动捕获。除 MouseUp 正常路径外，Program 的 Tick 健康检查也会调用：
        // 左键已物理抬起但卡片仍持有捕获时强制放掉，避免全系统单击失效
        public void ReleaseStuckDrag()
        {
            if (!Capture) { _moved = false; return; }
            Capture = false;
            _moved = false;
            try { Program.DebugLog("[input] stuck capture released on card " + _path); } catch { }
        }

        // 重算颜色并刷新已建控件（新卡片沿用默认配色时调用）
        public void ApplyPalette()
        {
            _fnApplyPaletteColors();
            _fnRender();
        }

        public int PaletteIndex { get { return _paletteIndex; } }

        // 滚轮/列表点选：只改这一张卡片的配色，返回新索引
        public int CyclePalette(int dir)
        {
            int n = CardPalettes.All.Count;
            if (n == 0) return _paletteIndex;
            SetPalette((( _paletteIndex + dir) % n + n) % n, true);
            return _paletteIndex;
        }

        public void SetPalette(int index, bool persist)
        {
            int n = CardPalettes.All.Count;
            if (n == 0) return;
            index = ((index % n) + n) % n;
            _paletteIndex = index;

            _fnApplyPaletteColors();
            _fnRender();
            _item.Palette = index;
            // 同时作为新卡片的默认配色，下次钉的卡片沿用最近用过的这套
            Config.CardPaletteIndex = index;
            if (persist) Program.SaveConfigDebounced();
        }

        // 按当前配色算出本卡片的实际颜色（全自绘，支持 Alpha 透明）。
        private void _fnApplyPaletteColors()
        {
            _pal = CardPalettes.Get(_paletteIndex);
            if (_pal == null) return;
            _bg = _pal.Bg;
            _bgHover = _pal.Hover;
            _fg = _pal.Fg;
            _fgSub = _pal.Sub;
            _badgeBg = _pal.BadgeBg;
            _badgeFg = _pal.BadgeFg;
            _typeColor = _pal.StripColor;
            if (_nameFont != null) _nameFont.Dispose();
            if (_subFont != null) _subFont.Dispose();
            if (_badgeFont != null) _badgeFont.Dispose();
            _nameFont = new Font(_pal.FontName, _pal.FontSize);
            _subFont = new Font(_pal.FontName, _pal.FontSize - 1f);
            _badgeFont = new Font(_pal.FontName, _pal.FontSize - 1f, FontStyle.Bold);
            _fnLayout();
            _fnRender();
        }

        // 布局重算：配色切换后字体/字号变了，必须重测文字宽高并重算矩形，否则大字号方案文字被挤压
        private void _fnLayout()
        {
            if (_name == null) return;  // 内容未就绪（构造函数首次调 _fnApplyPaletteColors 时跳过）
            int nameW, subW, extW;
            using (var g = CreateGraphics())
            {
                nameW = Math.Min(TextRenderer.MeasureText(_name, _nameFont).Width + 2, 260);
                subW = TextRenderer.MeasureText(_sub, _subFont).Width + 2;
                extW = TextRenderer.MeasureText(_ext, _badgeFont, new Size(200, 20), TextFormatFlags.NoPadding).Width + 8;
            }
            int x = _isToday ? 21 : 14;
            int nameH = (int)Math.Ceiling(_nameFont.GetHeight()) + 2;
            int badgeH = nameH + 2;
            int subH = Math.Max(13, nameH - 2);
            // 内容区域高度：上下各留 4px padding，至少 24px
            int cardH = Math.Max(24, badgeH + 8);
            _targetH = cardH + ShadowPad;
            Height = _targetH;
            int nameY = (cardH - nameH) / 2;
            int badgeY = (cardH - badgeH) / 2;
            int subY = (cardH - subH) / 2;
            _nameRect = new Rectangle(x, nameY, nameW, nameH);
            _badgeRect = new Rectangle(x + nameW + 5, badgeY, extW, badgeH);
            _subRect = new Rectangle(_badgeRect.Right + 5, subY, Math.Max(subW, 30), subH);
            Width = _badgeRect.Right + 8 + ShadowPad;
        }

        private static Color _fnLighten(Color c, float k)
        {
            return Color.FromArgb(c.R + (int)((255 - c.R) * k), c.G + (int)((255 - c.G) * k), c.B + (int)((255 - c.B) * k));
        }

        private bool _fnCardContainsCursor()
        {
            // 卡片已 Dispose（中键关闭后轮询还在跑）：PointToClient 会抛 ObjectDisposedException，
            // 异常中断 Tick → 预览永不自动关闭。判定"不在卡片上"让轮询走正常关闭路径
            if (IsDisposed) return false;
            var p = PointToClient(Cursor.Position);
            return p.X >= 0 && p.X < ClientSize.Width - ShadowPad && p.Y >= 0 && p.Y < ClientSize.Height - ShadowPad;
        }

        private bool _fnPreviewContainsCursor()
        {
            return _textPreview != null && !_textPreview.IsDisposed
                && _textPreview.ClientRectangle.Contains(_textPreview.PointToClient(Cursor.Position));
        }

        private void _fnSetHover(bool on)
        {
            if (_hover == on) return;
            _hover = on;
            // sub 放徽章右侧到卡片右缘的剩余空间，放不下就不显示
            if (on)
            {
                int left = _badgeRect.Right + 5;
                int right = Width - ShadowPad - 6;
                if (right - left >= 24)
                {
                    _subRect = new Rectangle(left, _subRect.Y, right - left, _subRect.Height);
                    _subVisible = true;
                }
                else _subVisible = false;
            }
            else _subVisible = false;
            _fnRender();

            if (_isText)
            {
                if (on)
                {
                    if (_textPreviewTimer == null)
                    {
                        _textPreviewTimer = new Timer { Interval = 800 };
                        _textPreviewTimer.Tick += (s, e) => { _textPreviewTimer.Stop(); if (IsDisposed) return; if (_hover) _fnShowTextPreview(); };
                    }
                    _textPreviewTimer.Start();
                }
                else
                {
                    if (_textPreviewTimer != null) _textPreviewTimer.Stop();
                    // 关闭交给 _textHoverTimer 统一轮询（离开卡片∪预览 → 延迟关闭）
                }
            }
        }

        // text 卡可编辑预览：复用 PreviewForm 的 Control 构造，关闭时写回内容
        private void _fnShowTextPreview()
        {
            if (_textPreview != null && !_textPreview.IsDisposed) return;
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
                Text = _item.Path,
            };
            tb.TextChanged += (s, e) => _textDirty = true;

            int w = 300, h = 120;
            try
            {
                using (var g = CreateGraphics())
                {
                    var sz = g.MeasureString("W", tb.Font);
                    int lineH = (int)sz.Height + 1;
                    var lines = _item.Path.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    h = Math.Min(Math.Max(lineH * lines.Length + 8, 60), 300);
                    int maxW = 0;
                    foreach (var line in lines) { var lw = g.MeasureString(line, tb.Font).Width; if (lw > maxW) maxW = (int)lw; }
                    w = (int)Math.Max(Math.Min(maxW + 20, 480), 240);
                }
            }
            catch { }

            _textPreview = new PreviewForm(tb, w, h, "textcard");
            _textPreview.FilePath = _path;
            // textcard：隐藏复制按钮（复制文件/复制路径对文本无意义），同位显示粘贴按钮
            _textPreview.HideCopyForTextCard();
            // 记录预览打开前的前台窗口：预览的粘贴按钮把（可能已编辑的）文本贴回那里
            IntPtr pasteTarget = NativeMethods.GetForegroundWindow();
            _textPreview.PasteAction = () =>
            {
                try
                {
                    string txt = tb.Text;
                    if (!string.IsNullOrEmpty(txt)) Clipboard.SetText(txt);
                    if (pasteTarget != IntPtr.Zero && pasteTarget != _textPreview.Handle)
                        NativeMethods.SetForegroundWindow(pasteTarget);
                    var t = new Timer { Interval = 60 };
                    t.Tick += (s2, e2) =>
                    {
                        t.Stop(); t.Dispose();
                        NativeMethods.keybd_event(0x11, 0, 0, UIntPtr.Zero);
                        NativeMethods.keybd_event(0x56, 0, 0, UIntPtr.Zero);
                        NativeMethods.keybd_event(0x56, 0, 2, UIntPtr.Zero);
                        NativeMethods.keybd_event(0x11, 0, 2, UIntPtr.Zero);
                    };
                    t.Start();
                }
                catch (Exception ex) { Program.DebugLog("[pincard] preview-paste fail " + ex.Message); }
            };
            // 位置：卡片下方，工作区内钳制
            var wa = Screen.GetWorkingArea(new Rectangle(Location, Size));
            int px = Math.Min(Right + 4, wa.Right - w - 8);
            int py = Math.Min(Bottom + 6, wa.Bottom - h - 8);
            _textPreview.Location = new Point(Math.Max(px, wa.Left + 4), Math.Max(py, wa.Top + 4));
            _textPreview.FormClosing += (s, e) =>
            {
                if (_textDirty)
                {
                    _item.Path = tb.Text;
                    _textDirty = false;
                    Config.Save();
                    _fnRefreshTextCard();
                }
            };
            _textPreview.Show();
            // 轮询关闭（与文件预览的纯几何行为一致）：鼠标离开 卡片∪预览 400ms → 关，
            // FormClosing 里 dirty 写回内容（编辑中移开鼠标不丢字）。不看焦点——
            // 点击编辑后 ContainsFocus 恒 true 会导致永不自动关闭
            if (_textHoverTimer == null)
            {
                _textHoverTimer = new Timer { Interval = 400 };
                _textHoverTimer.Tick += (s, e) =>
                {
                    if (_textPreview == null || _textPreview.IsDisposed) { _textHoverTimer.Stop(); return; }
                    if (_textPreview.Pinned) return;   // 手动拖动固定后不自动关
                    if (_fnCardContainsCursor() || _fnPreviewContainsCursor()) return;
                    _textHoverTimer.Stop();
                    _fnCloseTextPreview();
                };
            }
            _textHoverTimer.Start();
            Program.DebugLog("[pincard] text-preview open " + _item.Path.Length + " 字");
        }

        private void _fnCloseTextPreview()
        {
            if (_textHoverTimer != null) { _textHoverTimer.Stop(); }
            if (_textPreview == null) return;
            try { _textPreview.Close(); _textPreview.Dispose(); } catch { }
            _textPreview = null;
        }

        // 徽章悬停动作调度：文件夹 400ms 弹列表；可预览文件 800ms 弹预览
        private void _fnScheduleBadgeAction()
        {
            if (_badgeTimer == null)
            {
                _badgeTimer = new Timer();
                _badgeTimer.Tick += (s, e) =>
                {
                    _badgeTimer.Stop();
                    if (IsDisposed || !_hover) return;
                    if (_isDir) _fnOpenFolderList();
                    else if (_isFile && PreviewProvider.CanPreview(_path)) _fnShowFilePreview();
                };
            }
            _badgeTimer.Interval = _isDir ? 400 : 800;
            _badgeTimer.Stop();
            _badgeTimer.Start();
        }

        // 文件夹卡：卡片下方（与卡片左对齐）弹文件夹列表；离开 卡片∪面板 后收起
        private void _fnOpenFolderList()
        {
            Program.ShowCardFolderList(this, _path);
            if (_panelWatchTimer == null)
            {
                int miss = 0;   // 连续两次采样（≈1s）不在 卡片∪面板 才收：从卡片移向列表跨缝隙时不误关
                _panelWatchTimer = new Timer { Interval = 500 };
                _panelWatchTimer.Tick += (s, e) =>
                {
                    if (IsDisposed) { _panelWatchTimer.Stop(); return; }
                    if (_fnCardContainsCursor()) { miss = 0; return; }
                    NativeMethods.POINT pt; NativeMethods.GetCursorPos(out pt);
                    if (PopupForm.InAnyHoverPanelOrPreview(pt.X, pt.Y)) { miss = 0; return; }
                    if (++miss < 2) return;
                    miss = 0;
                    _panelWatchTimer.Stop();
                    Program.CloseHoverPanels();
                };
            }
            _panelWatchTimer.Stop();
            _panelWatchTimer.Start();
        }

        // file 卡徽章悬停：内容预览（图片/媒体/文本等走 PreviewProvider 全链路）
        private void _fnShowFilePreview()
        {
            if (_filePreview != null && !_filePreview.IsDisposed) return;
            string full = _path;
            var ctx = System.Threading.SynchronizationContext.Current;
            System.Threading.Tasks.Task.Run(delegate
            {
                PreviewResult result = null;
                try { result = PreviewProvider.Generate(full); }
                catch { }
                if (result == null) return;
                try
                {
                    ctx.Post(delegate
                    {
                        if (IsDisposed || full != _path) { if (result.Image != null) result.Image.Dispose(); return; }
                        _fnCloseFilePreview();
                        PreviewForm frm;
                        if (result.Kind == PreviewResult.KindEnum.Media)
                        {
                            var ctl = MediaPreview.MediaPlayer(full, result.IsVideo, result.DisplayW, result.DisplayH);
                            frm = new PreviewForm(ctl, ctl.Width, ctl.Height, result.IsVideo ? "video" : "audio");
                        }
                        else if (result.Kind == PreviewResult.KindEnum.SvgHtml)
                        {
                            var ctl = MediaPreview.SvgBrowserPanel(full);
                            frm = new PreviewForm(ctl, ctl.Width, ctl.Height, "svg");
                        }
                        else if (result.Kind == PreviewResult.KindEnum.Image)
                        {
                            frm = result.IsGif
                                ? new PreviewForm(result.Image, result.DisplayW, result.DisplayH)
                                : new PreviewForm(result.Image);
                        }
                        else
                        {
                            frm = new PreviewForm(result.Text, result.Size);
                        }
                        _filePreview = frm;
                        frm.FilePath = full;
                        // 位置：卡片下方，与卡片左对齐（面板宽度超出工作区时右移钳制）
                        int w = frm.Width, h = frm.Height;
                        var wa = Screen.GetWorkingArea(new Rectangle(Location, Size));
                        int px = Math.Min(Left, wa.Right - w - 8);
                        int py = Math.Min(Bottom + 4, wa.Bottom - h - 8);
                        frm.Location = new Point(Math.Max(px, wa.Left + 4), Math.Max(py, wa.Top + 4));
                        frm.Show();
                        NativeMethods.SetWindowPos(frm.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
                        if (_fileHoverTimer == null)
                        {
                            _fileHoverTimer = new Timer { Interval = 400 };
                            _fileHoverTimer.Tick += (s2, e2) =>
                            {
                                if (_filePreview == null || _filePreview.IsDisposed) { _fileHoverTimer.Stop(); return; }
                                if (_filePreview.Pinned) return;   // 手动拖动固定后不自动关
                                if (_fnCardContainsCursor() || _fnFilePreviewContainsCursor()) return;
                                _fileHoverTimer.Stop();
                                _fnCloseFilePreview();
                            };
                        }
                        _fileHoverTimer.Stop();
                        _fileHoverTimer.Start();
                        Program.DebugLog("[pincard] file-preview open " + full);
                    }, null);
                }
                catch
                {
                    if (result.Image != null) { try { result.Image.Dispose(); } catch { } }
                }
            });
        }

        private bool _fnFilePreviewContainsCursor()
        {
            return _filePreview != null && !_filePreview.IsDisposed
                && _filePreview.ClientRectangle.Contains(_filePreview.PointToClient(Cursor.Position));
        }

        private void _fnCloseFilePreview()
        {
            if (_fileHoverTimer != null) _fileHoverTimer.Stop();
            if (_filePreview == null) return;
            try { _filePreview.Close(); _filePreview.Dispose(); } catch { }
            _filePreview = null;
        }

        // 文本写回后刷新卡片显示（名字摘要/副标题）
        private void _fnRefreshTextCard()
        {
            _name = !string.IsNullOrEmpty(_item.Title) ? _item.Title : Ingest.Ellipsis(Ingest.FirstLine(_item.Path), 18);
            _sub = _item.Path.Length + " 字";
            _fnRender();
        }

        // 粘贴：卡片 MA_NOACTIVATE 不抢焦点，SetClipboard 后模拟 Ctrl+V 贴回原焦点窗口
        private void _fnPaste()
        {
            try
            {
                Clipboard.SetText(_item.Path);
                var t = new Timer { Interval = 40 };
                t.Tick += (s, e) =>
                {
                    t.Stop(); t.Dispose();
                    NativeMethods.keybd_event(0x11, 0, 0, UIntPtr.Zero);
                    NativeMethods.keybd_event(0x56, 0, 0, UIntPtr.Zero);
                    NativeMethods.keybd_event(0x56, 0, 2, UIntPtr.Zero);
                    NativeMethods.keybd_event(0x11, 0, 2, UIntPtr.Zero);
                };
                t.Start();
            }
            catch (Exception ex) { Program.DebugLog("[pincard] paste fail " + ex.Message); }
        }

        // 文件便捷构造
        public PinCardForm(string path, int x, int y)
            : this(new PinnedItem { Kind = "file", Path = path, X = x, Y = y })
        {
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // 分层窗口：WS_EX_LAYERED + UpdateLayeredWindow 实现逐像素 Alpha 透明
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                return cp;
            }
        }

        private int _targetH = 24 + ShadowPad;   // 卡片目标高度（SetBoundsCore 钳制用，_fnLayout 更新）

        // 高度按 _targetH 钳制：AutoScale 在句柄创建/布局阶段会改写 bounds（实测 28→39），
        // 在 SetBoundsCore 统一钳制。不能用 MaximumSize=(0,H)：0 宽分量被当真实上限，
        // 宽度全被压到 MinimumSize.Width → 所有卡片同宽、徽章截断（实测踩坑）
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            height = _targetH;
            base.SetBoundsCore(x, y, width, height, specified);
        }

        // 点击不激活（MA_NOACTIVATE）：粘贴到原焦点窗口的前提；text 编辑预览是独立窗口正常激活
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }
            if (m.Msg == 0x84)   // WM_NCHITTEST：阴影区域点击穿透
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1)
                {
                    long lp = m.LParam.ToInt64();
                    var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                    if (pt.X >= Width - ShadowPad || pt.Y >= Height - ShadowPad)
                        m.Result = (IntPtr)(-1);   // HTTRANSPARENT
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 卡片配色独立于面板主题：DWM 暗色模式按卡片底色明暗决定。
            // 圆角模式也强制卡片直角：DWM 裁角与卡片自有硬描边/全高色条冲突（残角/碎边）
            Win11.Apply(Handle, CardPalette.Lum(_bg) < 128, Config.Border);
            Win11.RemoveDropShadow(Handle);     // 分层窗口自绘阴影，不用系统 CS_DROPSHADOW 直角阴影
            Win11.ForceSquareCorners(Handle);   // DWM 直角不裁剪窗口，自绘圆角+阴影完整可见
            Win11.RemoveNcBorder(Handle);       // 禁用 DWM 非客户区渲染+边框，去掉白色描边

            _fnRender();   // 句柄就绪后首次渲染
        }

        // 圆角矩形路径（AntiAlias 平滑）
        private static GraphicsPath _fnRoundRect(int x, int y, int w, int h, int r)
        {
            var path = new GraphicsPath();
            if (r <= 0) { path.AddRectangle(new Rectangle(x, y, w, h)); return path; }
            r = Math.Min(r, Math.Min(w, h) / 2);
            int d = r * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 全自绘渲染：画到 Format32bppArgb 位图 → UpdateLayeredWindow（逐像素 Alpha 透明）
        private void _fnRender()
        {
            if (IsDisposed || !IsHandleCreated || Width <= 0 || Height <= 0) return;
            int w = Width, h = Height;
            int cw = w - ShadowPad, ch = h - ShadowPad;
            int radius = _pal != null ? _pal.Radius : 0;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                // ClearTypeGridFit：文字按子像素与卡片底色混合，笔画锐利（灰阶 AA 在
                // 分层窗口浅色文字上发虚呈锯齿感）；文字始终落在已填充的卡片底色上，
                // 不会在透明区产生彩色边缘
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                // 圆角抗锯齿。此前关掉是因为旧推送链路(GetHbitmap)会把半透明边缘像素
                // 合成出白边，只能牺牲平滑换干净；改 CreateDIBSection 预乘直推后
                // 边缘像素按 alpha 正确合成，白边不复存在，可以放心开 AA
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // 自绘阴影（右下偏移，跟着圆角走）
                for (int i = ShadowPad; i > 0; i--)
                {
                    using (var sp = _fnRoundRect(i, i, cw, ch, radius))
                    using (var sb = new SolidBrush(Color.FromArgb(30, 0, 0, 0)))
                        g.FillPath(sb, sp);
                }

                // 背景（圆角 + Alpha 透明）
                Color bg = _dropHighlight ? Config.DropHighlight : (_hover ? _bgHover : _bg);
                if (bg.A > 0)
                    using (var path = _fnRoundRect(0, 0, cw, ch, radius))
                    using (var b = new SolidBrush(bg))
                        g.FillPath(b, path);

                // 左侧色条（裁剪到卡片圆角形状内）
                if (_pal != null && _pal.StripWidth > 0 && _pal.StripColor.A > 0)
                {
                    using (var clip = _fnRoundRect(0, 0, cw, ch, radius))
                    {
                        g.SetClip(clip);
                        using (var b = new SolidBrush(_pal.StripColor))
                            g.FillRectangle(b, 0, 0, _pal.StripWidth, ch);
                        g.ResetClip();
                    }
                }

                // 徽标背景（圆角，悬停时提亮一档）
                Color badgeBg = _badgeHover ? _fnLighten(_badgeBg, 0.15f) : _badgeBg;
                if (badgeBg.A > 0)
                    using (var path = _fnRoundRect(_badgeRect.X, _badgeRect.Y, _badgeRect.Width, _badgeRect.Height, radius))
                    using (var b = new SolidBrush(badgeBg))
                        g.FillPath(b, path);

                // 徽标文字
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.None })
                using (var br = new SolidBrush(_badgeFg))
                    g.DrawString(_ext, _badgeFont, br, _badgeRect, sf);

                // 名字
                using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                using (var br = new SolidBrush(_fg))
                    g.DrawString(_name, _nameFont, br, _nameRect, sf);

                // 副标题（仅悬停时显示）
                if (_subVisible)
                    using (var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    using (var br = new SolidBrush(_fgSub))
                        g.DrawString(_sub, _subFont, br, _subRect, sf);

                // 今天修改过：名字左侧状态点
                if (_isToday)
                    using (var b = new SolidBrush(Color.FromArgb(0x2f, 0xb3, 0x6b)))
                        g.FillEllipse(b, 10, 10, 5, 5);

                // 预乘 alpha：UpdateLayeredWindow 的 AC_SRC_ALPHA 期望预乘 alpha
                byte[] px;
                {
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
                }

                // 推送到分层窗口：CreateDIBSection 直写预乘字节。
                // 不能用 Bitmap.GetHbitmap()：它产出的 GDI 位图会把低 alpha 像素的 RGB
                // 拍平成不透明浅灰（实测 A=0→RGB211、A=56→RGB165），ULW 合成后阴影带/
                // 圆角镂空全变白边（在浅色壁纸上溢出为纯白）；DIB 按字节原样携带 alpha
                IntPtr hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
                try
                {
                    var bmi = new NativeMethods.BITMAPINFO();
                    bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
                    bmi.bmiHeader.biWidth = w;
                    bmi.bmiHeader.biHeight = -h;   // 负高 = 自上而下行序，与 LockBits 布局一致
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0;   // BI_RGB
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

        private static string _fnCountSub(string dir)
        {
            try
            {
                int n = Directory.GetFileSystemEntries(dir).Length;
                return n + " 项";
            }
            catch { return ""; }
        }

        // _fnTypeColor 已移除（类型色机制取消，色条/徽章统一用配色字段）

        private void Run()
        {
            try
            {
                if (_item.Kind == "text")
                {
                    try { Clipboard.SetText(_item.Path); } catch { }
                    Program.ShowBalloon("HoverExplorer", I18n.T("card_copied"));
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_item.Path) { UseShellExecute = true });
            }
            catch (Exception ex) { Program.DebugLog("[pincard] run fail " + _item.Kind + " " + _item.Path + " " + ex.Message); }
        }

        // 对一个控件启用文件拖放（复制/移动进 _item 文件夹）+ 徽标悬停弹簧展开
        private void _fnEnableDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                _dropHighlight = true; _fnRender();
                if (_isDir) _fnScheduleBadgeDragOpen();
            };
            c.DragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                var pt = c.PointToClient(new Point(e.X, e.Y));
                if (_isDir && _badgeRect.Contains(pt)) _fnScheduleBadgeDragOpen();
                else _fnCancelBadgeDragOpen();
            };
            c.DragLeave += (s, e) => { _dropHighlight = false; _fnRender(); _fnCancelBadgeDragOpen(); };
            c.DragDrop += (s, e) => { _fnCancelBadgeDragOpen(); _fnHandleDrop(e); };
        }

        private void _fnHandleDrop(DragEventArgs e)
        {
            _dropHighlight = false; _fnRender();
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            bool move = e.Effect == DragDropEffects.Move;
            int ok = 0;
            foreach (var f in files)
            {
                try
                {
                    string src = f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string dst = Path.Combine(_item.Path, Path.GetFileName(src));
                    if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) continue;
                    if (move)
                    {
                        if (Directory.Exists(src)) Directory.Move(src, dst);
                        else File.Move(src, dst);
                    }
                    else _fnCopyEntry(src, dst);
                    ok++;
                }
                catch (Exception ex) { Program.DebugLog("[pincard] drop fail " + f + " " + ex.Message); }
            }
            if (ok > 0) Program.ShowBalloon("HoverExplorer", (move ? "已移动 " : "已复制 ") + ok + " 项 → " + Path.GetFileName(_item.Path));
        }

        // 递归复制文件或目录到 dst
        private static void _fnCopyEntry(string src, string dst)
        {
            if (File.Exists(src)) { File.Copy(src, dst, true); return; }
            if (!Directory.Exists(src)) return;
            Directory.CreateDirectory(dst);
            foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            {
                string rel = d.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(dst, rel));
            }
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length).TrimStart(Path.DirectorySeparatorChar);
                File.Copy(f, Path.Combine(dst, rel), true);
            }
        }

        private void ShowCardMenu()
        {
            var m = new ContextMenuStrip { BackColor = Config.BG, ForeColor = Config.FG, Font = new Font(Config.FontName, Config.FontSize) };
            m.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            if (_item.Kind == "url")
            {
                m.Items.Add(I18n.T("open"), null, (s, e) => Run());
                m.Items.Add(I18n.T("copy_link"), null, (s, e) => { try { Clipboard.SetText(_item.Path); } catch { } });
            }
            else if (_item.Kind == "text")
            {
                m.Items.Add(I18n.T("copy_text"), null, (s, e) => { try { Clipboard.SetText(_item.Path); } catch { } });
            }
            else
            {
                m.Items.Add(I18n.T("copy_path"), null, (s, e) => { try { Clipboard.SetText(_item.Path); } catch { } });
            }
            m.Items.Add(I18n.T("card_add_palette"), null, (s, e) => _fnAddPaletteFromClipboard());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(I18n.T("unpin"), null, (s, e) => Program.Unpin(_item.Kind, _item.Path));
            m.Show(Cursor.Position);
        }

        // 右键菜单：保存剪贴板中的配色方案并应用到当前卡片
        private void _fnAddPaletteFromClipboard()
        {
            string clip = null; try { clip = Clipboard.GetText(); } catch { }
            if (string.IsNullOrEmpty(clip) || clip.IndexOf("bg=", StringComparison.OrdinalIgnoreCase) < 0)
            { Program.ShowBalloon("HoverExplorer", I18n.T("paste_invalid")); return; }
            var parsed = CardPalette.Parse(clip, null);
            string baseName = I18n.T("imported_prefix") + " " + DateTime.Now.ToString("MM-dd HH:mm");
            string nm = baseName; int dup = 2;
            while (CardPalettes.All.Exists(p => p.Name == nm)) nm = baseName + " (" + (dup++) + ")";
            parsed.Name = nm; parsed.Radius = Math.Max(0, Math.Min(12, parsed.Radius));
            CardPalettes.All.Add(parsed);
            CardPalettes.Save();
            SetPalette(CardPalettes.All.Count - 1, true);
        }
    }

}