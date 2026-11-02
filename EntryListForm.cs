using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 历史/收藏文件夹入口列表：表头（标题+⧉复制+图钉固定+⌄折叠）与文件列表行为一致；
    // 收藏分组在前、历史在后；点击行展开该文件夹级联列表，右键行管理。
    // 数据变更（RecordRecent/收藏增删）实时重建（NotifyChanged）；固定后不自动关闭
    internal class EntryListForm : Form
    {
        private readonly Action<string, Point> _onOpenFolder;
        private readonly List<Control> _rows = new List<Control>();   // 分组标签+行（重建时清除）
        private Timer _watchTimer;
        private bool _dirty;
        private static EntryListForm _current;   // 当前开着的实例（实时更新通知用）

        private Label _titleLbl;
        private Label _pinBtn;
        private Label _foldBtn;
        private Panel _headPanel;   // 表头容器：宽度须随 _listW 同步，否则按钮被裁切
        private bool _pinned;
        private int _listW = 340;
        private int _headerH = 26;
        private int _fullH = 60;
        // 行区/表头左右内缩与底部留白：与 PopupForm 一致，不顶到面板左右边缘与底边，
        // 与深色配色下圆角描边（同背景色）视觉协调
        private const int ListPadLr = 6;
        private const int ListPadBottom = 6;
        private Timer _rowHoverTimer;     // 行悬停 400ms → 右侧弹子列表
        private string _rowHoverPath;
        private EntryRow _rowHoverRow;
        private Timer _dragSubTimer;      // 拖拽悬停文件夹 ~500ms → 弹簧式打开级联子列表
        private EntryRow _dragSubRow;

        // ---- 标签栏（原型 A 分组标签，历史作为特殊分组）----
        private int _curG = 0;                    // 当前分组索引；-1 = 历史分组
        private int _lastFavG = 0;                // 上次选中的收藏分组（历史行点☆加入该组）
        private bool _suppressEnter;              // 拖放后抑制一次 MouseEnter 重建
        private const string DragFmtRow = "EntryRowDrag";   // 内部行拖动数据格式
        private class RowDragData { public string Path; public int? FromG; public int? FromH; }
        private int _listTop;   // 列表区顶部 y（拖放插入指示用）
        private const int SideGap = 4;            // 左侧分组栏与右侧列表间距
        private const int SideBarWMin = 72, SideBarWMax = 150;
        private int _sideW = 90;                  // 左侧分组栏宽度

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 与面板/预览一致：暗色标题区 + DWM 圆角（Win11）+ 投影
            Win11.Apply(Handle, Config.IsDark, Config.Border);
        }

        public EntryListForm(Action<string, Point> onOpenFolder)
        {
            _onOpenFolder = onOpenFolder;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Config.ListCanvas;   // 配色跟随文件列表（画布底）
            DoubleBuffered = false;   // 双缓冲绘制在首次弹出时偶发 BufferedGraphics AV 崩溃（进程退出），改直绘
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                // 行内重命名中：Esc 只取消改名，不关列表
                if (_rowEdit != null) { e.SuppressKeyPress = true; _fnEndEntryRename(false); return; }
                // Esc 主动关闭后区域内不再重弹（否则鼠标停在触发区时 HoverDelay 后又弹回来）；
                // 移出区域再回来即恢复武装
                Program._entryArmed = false;
                Close();
            };
            _fnBuildHeader();
            _curG = Config.FavoriteCurGroup;
            _lastFavG = Math.Max(0, Math.Min(_curG, Config.FavoriteGroups.Count - 1));
            _fnBuildRows();
        }

        // ---- 表头：拖动固定 + 图钉 + 折叠为卡片 ----
        private void _fnBuildHeader()
        {
            _headPanel = new Panel { BackColor = Config.ListHdbg, Location = new Point(ListPadLr, 0), Size = new Size(_listW - ListPadLr * 2, _headerH) };
            // 实际拖动后才固定；单纯点击不固定（与文件列表表头一致）
            MouseEventHandler drag = (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var before = Location;
                NativeMethods.ReleaseCapture(); NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                if (Location != before && !_pinned) _fnTogglePin();
            };
            _headPanel.MouseDown += drag;

            _titleLbl = new Label
            {
                Text = I18n.T("entry_title"),
                Font = new Font(Config.FontName, Config.FontSize, FontStyle.Bold),
                ForeColor = Config.ListFg,
                BackColor = Config.ListHdbg,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(6, 3),
                Size = new Size(_headPanel.Width - 58, 20),
                Cursor = Cursors.Hand,
            };
            Tips.Set(_titleLbl, I18n.T("entry_tip"));
            _titleLbl.MouseDown += drag;
            _headPanel.Controls.Add(_titleLbl);

            // 图钉：固定后不随鼠标离开自动关闭
            _pinBtn = new Label
            {
                Size = new Size(20, 18),
                AutoSize = false,
                BackColor = Config.ListHdbg,
                Image = PreviewForm.PinIcon(false),
                Cursor = Cursors.Hand,
                Location = new Point(_headPanel.Width - 46, 4),
            };
            Tips.Set(_pinBtn, I18n.T("tip_pin_panel"));
            _pinBtn.Click += (s, e) => _fnTogglePin();
            _headPanel.Controls.Add(_pinBtn);

            // 折叠为桌面卡片：列表收起为常驻小卡片（悬停卡片再弹列表）
            _foldBtn = new Label
            {
                Size = new Size(20, 18),
                AutoSize = false,
                BackColor = Config.ListHdbg,
                Image = PreviewForm.PinLaunchIcon(false),
                Cursor = Cursors.Hand,
                Location = new Point(_headPanel.Width - 24, 4),
            };
            _foldBtn.Click += (s, e) => Program.CollapseEntryToCard(Left, Top);
            Tips.Set(_foldBtn, I18n.T("fold_card"));
            _headPanel.Controls.Add(_foldBtn);

            Controls.Add(_headPanel);
        }

        private void _fnTogglePin()
        {
            _pinned = !_pinned;
            if (_pinBtn != null) _pinBtn.Image = PreviewForm.PinIcon(_pinned);
        }

        // ---- 行区：顶部标签栏（多分组 + 历史特殊分组）+ 当前分组列表 ----
        private FavoriteGroup CurrentGroup()
        {
            if (Config.FavoriteGroups.Count == 0)
                Config.FavoriteGroups.Add(new FavoriteGroup(I18n.T("fav_default_name"), "#4a7ab5"));
            if (_curG < 0) return null;   // 历史分组
            if (_curG >= Config.FavoriteGroups.Count) _curG = 0;
            return Config.FavoriteGroups[_curG];
        }

        private static bool PathExists(string p) { return Directory.Exists(p) || File.Exists(p); }

        // 按背景亮度返回黑/白文字（保证可读）
        private static Color ContrastFg(Color bg)
        {
            return (bg.R * 0.299 + bg.G * 0.587 + bg.B * 0.114) > 140 ? Color.Black : Color.White;
        }

        // 按索引取收藏分组，越界回退到第一个
        private FavoriteGroup _fnFavGroup(int idx)
        {
            if (Config.FavoriteGroups.Count == 0)
                Config.FavoriteGroups.Add(new FavoriteGroup(I18n.T("fav_default_name"), "#4a7ab5"));
            if (idx < 0 || idx >= Config.FavoriteGroups.Count) idx = 0;
            return Config.FavoriteGroups[idx];
        }

        private void _fnBuildRows()
        {
            foreach (var r in _rows) { try { Controls.Remove(r); r.Dispose(); } catch { } }
            _rows.Clear();
            var grp = CurrentGroup();
            bool isHist = _curG < 0;
            List<string> items;
            if (isHist) items = Config.RecentFolders.Where(PathExists).ToList();
            else items = grp.Items.Where(PathExists).ToList();

            var nameFont = new Font(Config.FontName, Config.FontSize);
            var tabFont = new Font(Config.FontName, Config.FontSize - 0.5f);

            // 左侧分组栏：色块窄栏固定宽度
            _sideW = 38;

            // 总宽度：手动调整值优先；否则按右栏内容自适应
            if (Config.EntryListW > 0) _listW = Config.EntryListW;
            else
            {
                int w = 300;
                using (var g = CreateGraphics())
                {
                    foreach (var p in items)
                    {
                        var sw = g.MeasureString(Path.GetFileName(p), nameFont).Width;
                        if (sw + 64 > w) w = (int)sw + 64;
                    }
                }
                _listW = ListPadLr * 2 + _sideW + SideGap + Math.Min(w, 420);
            }

            BackColor = Config.ListCanvas;
            var font = new Font(Config.FontName, Config.FontSize);
            var subFont = new Font(Config.FontName, Config.FontSize - 0.5f);
            int y = _headerH + 2;
            int zebra = 0;

            // ---- 左侧分组栏 + 右侧列表区 ----
            int rightX = ListPadLr + _sideW + SideGap;
            int rightW = _listW - rightX - ListPadLr;
            if (rightW < 120) rightW = 120;

            // 左侧分组栏（纵向，历史置顶）
            int sideBarH = _fnBuildSideBar(y, _sideW, font);

            // ---- 右侧当前分组列表 ----
            _listTop = y;
            int contentH = Math.Max(26, items.Count * 26 + 4);
            int listDropH = contentH;
            if (Config.EntryListH > 0) listDropH = Math.Max(26, Config.EntryListH - y - ListPadBottom);
            var listDrop = new Panel
            {
                Location = new Point(rightX, y),
                Size = new Size(rightW, listDropH),
                BackColor = Config.ListCanvas,
            };
            // 收藏分组空白区可接收行拖放（追加到末尾）；历史分组不接受
            if (!isHist)
            {
                listDrop.AllowDrop = true;
                listDrop.DragEnter += delegate(object s, DragEventArgs e)
                {
                    if (e.Data.GetDataPresent(DragFmtRow)) e.Effect = DragDropEffects.Move;
                    else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                        e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                };
                listDrop.DragOver += delegate(object s, DragEventArgs e)
                {
                    if (e.Data.GetDataPresent(DragFmtRow)) e.Effect = DragDropEffects.Move;
                    else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                        e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                    else e.Effect = DragDropEffects.None;
                };
                listDrop.DragDrop += delegate(object s, DragEventArgs e)
                {
                    if (e.Data.GetDataPresent(DragFmtRow)) _fnDropRow(null, e);
                    else
                    {
                        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                        if (files != null) _fnAddToCurrentGroup(files);
                    }
                };
            }
            Controls.Add(listDrop);
            _rows.Add(listDrop);

            if (items.Count == 0)
            {
                var empty = new Label
                {
                    Text = I18n.T(isHist ? "hist_empty" : "entry_empty"),
                    Location = new Point(4, 4),
                    AutoSize = true,
                    ForeColor = Config.ListMu,
                    Font = subFont,
                    BackColor = Config.ListCanvas,
                };
                listDrop.Controls.Add(empty);
            }
            int ry = 0;
            for (int idx = 0; idx < items.Count; idx++)
            {
                var p = items[idx];
                EntryRow row;
                if (isHist) row = _fnMakeHistRow(p, idx, font, subFont, zebra++);
                else row = _fnMakeRow(p, true, font, subFont, zebra++);
                row.Location = new Point(0, ry);
                row.Size = new Size(listDrop.Width, 24);
                listDrop.Controls.Add(row);
                _rows.Add(row);
                ry += 26;
            }
            y = listDrop.Bottom + 2;

            // 高度：手动调整值优先；否则按内容自适应（取两侧最大值，空分组不裁侧边栏）
            if (Config.EntryListH > 0) _fullH = Config.EntryListH;
            else _fullH = Math.Max(y + ListPadBottom, _headerH + 2 + sideBarH + ListPadBottom);
            if (_headPanel != null)
            {
                _headPanel.Size = new Size(_listW - ListPadLr * 2, _headerH);
                _headPanel.BackColor = Config.ListHdbg;
            }
            if (_titleLbl != null)
            {
                _titleLbl.Width = _headPanel.Width - 58;
                _titleLbl.BackColor = Config.ListHdbg;
                _titleLbl.ForeColor = Config.ListFg;
            }
            if (_pinBtn != null) { _pinBtn.Location = new Point(_headPanel.Width - 46, 4); _pinBtn.BackColor = Config.ListHdbg; }
            if (_foldBtn != null) { _foldBtn.Location = new Point(_headPanel.Width - 24, 4); _foldBtn.BackColor = Config.ListHdbg; }
            ClientSize = new Size(_listW, _fullH);
        }

        // 外部文件拖入空白区 → 收藏到当前分组
        private void _fnAddToCurrentGroup(string[] paths)
        {
            var grp = CurrentGroup();
            if (grp == null) return;
            foreach (var p in paths)
            {
                if (!PathExists(p)) continue;
                if (!grp.Items.Contains(p, StringComparer.OrdinalIgnoreCase)) grp.Items.Add(p);
            }
            _dirty = true; _fnSave(); _fnBuildRows();
        }

        // 行点击打开：文件用默认程序，文件夹走 _onOpenFolder
        private void _fnOpenPath(string path, Point pt)
        {
            if (File.Exists(path))
            {
                Program._entryArmed = false;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { Program.DebugLog("[entry] file open fail: " + ex.Message); }
            }
            else _onOpenFolder(path, pt);
        }

        // ---- 左侧色块窄栏（原型 S）：历史置顶、色块首字、悬停 tooltip、激活外圈、拖放移组 ----
        private int _fnBuildSideBar(int top, int w, Font font)
        {
            var bar = new Panel { Location = new Point(ListPadLr, top), Size = new Size(w, 100), BackColor = Config.ListHdbg };
            var chipFont = new Font(Config.FontName, Config.FontSize + 1f, FontStyle.Bold);
            var iconFont = new Font(Config.FontName, Config.FontSize + 2f);
            const int itemH = 36;
            const int cs = 22;   // 色块尺寸
            int iy = 0;

            // 历史置顶（图标，悬停切换）
            bool histOn = _curG < 0;
            var histTab = new SideTab { Location = new Point(0, iy), Size = new Size(w, itemH), BackColor = histOn ? Config.ListHv : Config.ListHdbg, Cursor = Cursors.Hand };
            histTab.Paint += delegate(object s, PaintEventArgs pe)
            {
                var g2 = pe.Graphics;
                g2.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var r = new Rectangle(0, 0, w, itemH);
                TextRenderer.DrawText(g2, "🕘", iconFont, r, histOn ? Config.ListFg : Config.ListMu,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (histOn)
                {
                    var ac = Config.Accent;
                    using (var p = new Pen(ac, 2)) g2.DrawRectangle(p, 1, 1, w - 2, itemH - 2);
                }
            };
            histTab.MouseEnter += delegate { if (_suppressEnter) return; if (Control.MouseButtons != MouseButtons.None) return; if (_curG >= 0) { _curG = -1; _fnBuildRows(); } };
            Tips.Set(histTab, I18n.T("hist_bar") + " · " + Config.RecentFolders.Count);
            bar.Controls.Add(histTab);
            iy += itemH;

            // 收藏分组色块
            for (int i = 0; i < Config.FavoriteGroups.Count; i++)
            {
                var g = Config.FavoriteGroups[i];
                bool on = i == _curG;
                var color = ColorTranslator.FromHtml(g.Color);
                string first = g.Name.Length > 0 ? g.Name.Substring(0, 1) : "?";
                int gi = i;
                var tab = new SideTab { Location = new Point(0, iy), Size = new Size(w, itemH), BackColor = on ? Config.ListHv : Config.ListHdbg, Cursor = Cursors.Hand };
                tab.Paint += delegate(object s, PaintEventArgs pe)
                {
                    var g2 = pe.Graphics;
                    g2.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    int cx = (w - cs) / 2, cy = (itemH - cs) / 2;
                    using (var b = new SolidBrush(color)) g2.FillRectangle(b, cx, cy, cs, cs);
                    if (on)
                    {
                        using (var p = new Pen(Color.White, 2)) g2.DrawRectangle(p, cx, cy, cs, cs);
                        using (var p = new Pen(color, 3)) g2.DrawRectangle(p, cx - 2, cy - 2, cs + 4, cs + 4);
                    }
                    TextRenderer.DrawText(g2, first, chipFont, new Rectangle(cx, cy, cs, cs), Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                };
                tab.MouseEnter += delegate { if (_suppressEnter) return; if (Control.MouseButtons != MouseButtons.None) return; if (gi != _curG) { _curG = gi; _lastFavG = gi; Config.FavoriteCurGroup = gi; _fnBuildRows(); } };
                tab.DoubleClick += delegate
                {
                    var nn = PromptDialog.Show(I18n.T("group_name"), g.Name);
                    if (!string.IsNullOrEmpty(nn)) { g.Name = nn; _dirty = true; _fnSave(); _fnBuildRows(); }
                };
                tab.MouseUp += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Right) _fnTabMenu(gi); };
                tab.AllowDrop = true;
                tab.DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DragFmtRow)) e.Effect = DragDropEffects.Move; else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
                tab.DragOver += delegate(object s, DragEventArgs e) {
                    if (e.Data.GetDataPresent(DragFmtRow)) e.Effect = DragDropEffects.Move;
                    else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    {
                        e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                        if (gi != _curG) { _curG = gi; _lastFavG = gi; Config.FavoriteCurGroup = gi; BeginInvoke((Action)(() => _fnBuildRows())); }
                    }
                    else e.Effect = DragDropEffects.None;
                };
                tab.DragDrop += delegate(object s, DragEventArgs e) { _fnDropOnTab(gi, e); };
                Tips.Set(tab, g.Name + " · " + g.Items.Count);
                bar.Controls.Add(tab);
                iy += itemH;
            }

            // ＋新建
            var add = new SideTab { Location = new Point(0, iy), Size = new Size(w, 32), BackColor = Config.ListHdbg, Cursor = Cursors.Hand };
            add.Paint += delegate(object s, PaintEventArgs pe)
            {
                TextRenderer.DrawText(pe.Graphics, "＋", iconFont, new Rectangle(0, 0, w, 32), Config.ListMu,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            add.MouseEnter += delegate { add.BackColor = Config.ListHv; add.Invalidate(); };
            add.MouseLeave += delegate { add.BackColor = Config.ListHdbg; add.Invalidate(); };
            add.Click += delegate
            {
                var colors = new[] { "#4a7ab5", "#d99a1c", "#c0587a", "#3a8a5a", "#7a5ac0", "#b55a3a" };
                var c = colors[Config.FavoriteGroups.Count % colors.Length];
                Config.FavoriteGroups.Add(new FavoriteGroup(I18n.T("add_group"), c));
                _curG = Config.FavoriteGroups.Count - 1;
                _lastFavG = _curG;
                Config.FavoriteCurGroup = _curG;
                _dirty = true; _fnSave(); _fnBuildRows();
            };
            Tips.Set(add, I18n.T("add_group"));
            bar.Controls.Add(add);
            iy += 32;

            bar.Size = new Size(w, iy);
            Controls.Add(bar);
            _rows.Add(bar);
            return iy;
        }

        // 拖放到标签 = 移入该分组（内部行拖放 / 外部文件拖放）
        private void _fnDropOnTab(int targetGi, DragEventArgs e)
        {
            if (targetGi < 0 || targetGi >= Config.FavoriteGroups.Count) return;
            var target = Config.FavoriteGroups[targetGi];
            var rd = e.Data.GetData(DragFmtRow) as RowDragData;
            if (rd != null && !string.IsNullOrEmpty(rd.Path))
            {
                if (rd.FromG.HasValue && rd.FromG.Value < Config.FavoriteGroups.Count)
                {
                    var src = Config.FavoriteGroups[rd.FromG.Value];
                    src.Items.RemoveAll(p => string.Equals(p, rd.Path, StringComparison.OrdinalIgnoreCase));
                }
                else if (rd.FromH.HasValue)
                {
                    Config.RecentFolders.RemoveAll(p => string.Equals(p, rd.Path, StringComparison.OrdinalIgnoreCase));
                }
                if (!target.Items.Contains(rd.Path, StringComparer.OrdinalIgnoreCase))
                    target.Items.Insert(0, rd.Path);
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null) return;
                bool added = false;
                foreach (var p in files)
                {
                    if (!PathExists(p)) continue;
                    if (!target.Items.Contains(p, StringComparer.OrdinalIgnoreCase)) { target.Items.Add(p); added = true; }
                }
                if (!added) return;
            }
            else return;
            _curG = targetGi; _lastFavG = targetGi; Config.FavoriteCurGroup = targetGi;
            _dirty = true; _fnSave();
            _suppressEnter = true;
            _fnBuildRows();
            var t = new Timer { Interval = 150 };
            t.Tick += (s2, e2) => { _suppressEnter = false; t.Dispose(); };
            t.Start();
        }

        private void _fnTabMenu(int gi)
        {
            var m = new ContextMenuStrip { ShowImageMargin = false, BackColor = Config.BG, ForeColor = Config.FG, Font = new Font(Config.FontName, Config.FontSize) };
                m.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            m.Items.Add(I18n.T("rename"), null, (s, e) =>
            {
                var g = Config.FavoriteGroups[gi];
                var nn = PromptDialog.Show(I18n.T("group_name"), g.Name);
                if (!string.IsNullOrEmpty(nn)) { g.Name = nn; _dirty = true; _fnSave(); _fnBuildRows(); }
            });
            m.Items.Add(I18n.T("group_color"), null, (s, e) =>
            {
                var g = Config.FavoriteGroups[gi];
                using (var cd = new ColorDialog { Color = ColorTranslator.FromHtml(g.Color) })
                    if (cd.ShowDialog() == DialogResult.OK)
                    {
                        g.Color = "#" + cd.Color.R.ToString("x2") + cd.Color.G.ToString("x2") + cd.Color.B.ToString("x2");
                        _dirty = true; _fnSave(); _fnBuildRows();
                    }
            });
            if (Config.FavoriteGroups.Count > 1)
                m.Items.Add(I18n.T("group_remove"), null, (s, e) =>
                {
                    Config.FavoriteGroups.RemoveAt(gi);
                    if (_curG >= Config.FavoriteGroups.Count) _curG = Config.FavoriteGroups.Count - 1;
                    Config.FavoriteCurGroup = _curG;
                    _dirty = true; _fnSave(); _fnBuildRows();
                });
            m.Show(Cursor.Position);
        }


        private EntryRow _fnMakeRow(string path, bool isFav, Font font, Font subFont, int zebra)
        {
            var baseColor = zebra % 2 == 1 ? Config.ListZb : Config.ListBg;
            var row = new EntryRow
            {
                Full = path,
                IsFav = isFav,
                Font = font,
                BackColor = baseColor,
                BaseColor = baseColor,
                ForeColor = Config.ListFg,
                Cursor = Cursors.Hand,
                Name = Path.GetFileName(path),
                SubFont = subFont,
            };

            row.MouseEnter += delegate
            {
                row.BackColor = Config.ListHv;
                _rowHoverPath = path;
                _fnStartRowHover(row);
            };
            row.MouseLeave += delegate
            {
                if (!_fnRowContains(row)) row.BackColor = row.BaseColor;
                if (_rowHoverPath == path) { _rowHoverPath = null; _fnStopRowHover(); }
            };
            row.StarHover += delegate(object s, bool inStar)
            {
                if (inStar) _fnStopRowHover();
                else if (_rowHoverPath == path) _fnStartRowHover(row);
            };
            row.StarToggled += delegate { _fnToggleFav(row); };
            row.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                var pt = row.PointToScreen(Point.Empty);
                RecordRecent(path);
                _fnOpenPath(path, pt);
                Close();
            };
            row.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) _fnRowMenu(row);
            };
            // ---- 拖动源：行内排序 / 跨标签移组（左键移动超过阈值启动）----
            row.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !row.IsStarPoint(e.Location))
                {
                    _dragSrcPath = path;
                    _dragSrcPt = e.Location;
                    _dragSrcRow = row;
                }
                else { _dragSrcRow = null; }
            };
            row.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_dragSrcRow != row || string.IsNullOrEmpty(_dragSrcPath)) return;
                if (Math.Abs(e.X - _dragSrcPt.X) + Math.Abs(e.Y - _dragSrcPt.Y) < 4) return;
                var idx = CurrentGroup().Items.IndexOf(path);
                var data = new DataObject();
                data.SetData(DragFmtRow, new RowDragData { Path = path, FromG = _curG, FromH = null });
                _dragSrcRow = null;
                try { row.DoDragDrop(data, DragDropEffects.Move); } catch { }
            };
            row.MouseUp += delegate { _dragSrcRow = null; };
            // ---- 拖放目标：内部行插入排序 + 外部文件复制/移动 ----
            row.AllowDrop = true;
            row.DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DragFmtRow)) { e.Effect = DragDropEffects.Move; row.BackColor = Config.ListHv; _fnStopRowHover(); }
                else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                    row.BackColor = Config.ListDrop;
                    _fnStopRowHover();
                }
            };
            row.DragOver += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DragFmtRow)) e.Effect = DragDropEffects.Move;
                else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                else e.Effect = DragDropEffects.None;
                _fnScheduleDragSub(row);
            };
            row.DragLeave += delegate
            {
                _fnCancelDragSub(row);
                if (!_fnRowContains(row)) row.BackColor = row.BaseColor;
            };
            row.DragDrop += delegate(object s, DragEventArgs e)
            {
                _fnCancelDragSub();
                row.BackColor = row.BaseColor;
                if (e.Data.GetDataPresent(DragFmtRow)) { _fnDropRow(row, e); return; }
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && Directory.Exists(path)) Program.DropFilesIntoEntryFolder(path, files, (e.KeyState & 4) != 0);
            };
            row.BtnHover += delegate(object s, bool inBtn)
            {
                if (inBtn) _fnStopRowHover();
                else if (_rowHoverPath == path) _fnStartRowHover(row);
            };
            row.CopyClicked += delegate { try { Clipboard.SetText(path); _fnToast(I18n.T("copied")); } catch { } };
            row.PinClicked += delegate { Program.TogglePin(path); };
            row.RenameClicked += delegate { _fnBeginEntryRename(row); };
            return row;
        }

        // ---- 历史抽屉行：☆ 收藏（加入当前分组）、拖进分组 ----
        private EntryRow _fnMakeHistRow(string path, int histIdx, Font font, Font subFont, int zebra)
        {
            var baseColor = zebra % 2 == 1 ? Config.ListZb : Config.ListBg;
            var row = new EntryRow
            {
                Full = path,
                IsFav = false,
                Font = font,
                BackColor = baseColor,
                BaseColor = baseColor,
                ForeColor = Config.ListFg,
                Cursor = Cursors.Hand,
                Name = Path.GetFileName(path),
                SubFont = subFont,
            };
            row.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                var pt = row.PointToScreen(Point.Empty);
                RecordRecent(path);
                _fnOpenPath(path, pt);
                Close();
            };
            row.StarToggled += delegate
            {
                // ☆ → 加入上次选中的收藏分组
                var grp = _fnFavGroup(_lastFavG);
                if (!grp.Items.Contains(path, StringComparer.OrdinalIgnoreCase)) grp.Items.Insert(0, path);
                _dirty = true; _fnSave(); _fnBuildRows();
            };
            // 拖动源：拖到分组列表/标签 = 移入收藏
            row.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && !row.IsStarPoint(e.Location))
                {
                    _dragSrcPath = path;
                    _dragSrcPt = e.Location;
                    _dragSrcRow = row;
                }
                else _dragSrcRow = null;
            };
            row.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_dragSrcRow != row || string.IsNullOrEmpty(_dragSrcPath)) return;
                if (Math.Abs(e.X - _dragSrcPt.X) + Math.Abs(e.Y - _dragSrcPt.Y) < 4) return;
                var data = new DataObject();
                data.SetData(DragFmtRow, new RowDragData { Path = path, FromG = null, FromH = histIdx });
                _dragSrcRow = null;
                try { row.DoDragDrop(data, DragDropEffects.Move); } catch { }
            };
            row.MouseUp += delegate { _dragSrcRow = null; };
            // ---- 悬停级联子列表（与收藏行同语义）----
            row.MouseEnter += delegate
            {
                row.BackColor = Config.ListHv;
                _rowHoverPath = path;
                _fnStartRowHover(row);
            };
            row.MouseLeave += delegate
            {
                if (!_fnRowContains(row)) row.BackColor = row.BaseColor;
                if (_rowHoverPath == path) { _rowHoverPath = null; _fnStopRowHover(); }
            };
            row.StarHover += delegate(object s, bool inStar)
            {
                if (inStar) _fnStopRowHover();
                else if (_rowHoverPath == path) _fnStartRowHover(row);
            };
            row.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) _fnRowMenu(row);
            };
            // ---- 拖放目标：外部文件拖到历史文件夹行 = 复制/移动 ----
            row.AllowDrop = true;
            row.DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                    row.BackColor = Config.ListDrop;
                    _fnStopRowHover();
                }
            };
            row.DragOver += delegate(object s, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? ((e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy)
                    : DragDropEffects.None;
                _fnScheduleDragSub(row);
            };
            row.DragLeave += delegate
            {
                _fnCancelDragSub(row);
                if (!_fnRowContains(row)) row.BackColor = row.BaseColor;
            };
            row.DragDrop += delegate(object s, DragEventArgs e)
            {
                _fnCancelDragSub();
                row.BackColor = row.BaseColor;
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && Directory.Exists(path)) Program.DropFilesIntoEntryFolder(path, files, (e.KeyState & 4) != 0);
            };
            // ---- 行悬停操作按钮 ----
            row.BtnHover += delegate(object s, bool inBtn)
            {
                if (inBtn) _fnStopRowHover();
                else if (_rowHoverPath == path) _fnStartRowHover(row);
            };
            row.CopyClicked += delegate { try { Clipboard.SetText(path); _fnToast(I18n.T("copied")); } catch { } };
            row.PinClicked += delegate { Program.TogglePin(path); };
            row.RenameClicked += delegate { _fnBeginEntryRename(row); };
            return row;
        }

        // 拖放落点处理：row=null 落在空白列表区（追加到末尾）；row=具体行（插入到该行位置）
        private void _fnDropRow(EntryRow targetRow, DragEventArgs e)
        {
            var rd = e.Data.GetData(DragFmtRow) as RowDragData;
            if (rd == null || string.IsNullOrEmpty(rd.Path)) return;
            var grp = CurrentGroup();
            // 从来源移除
            if (rd.FromG.HasValue && rd.FromG.Value < Config.FavoriteGroups.Count)
            {
                var src = Config.FavoriteGroups[rd.FromG.Value];
                src.Items.RemoveAll(p => string.Equals(p, rd.Path, StringComparison.OrdinalIgnoreCase));
            }
            else if (rd.FromH.HasValue)
            {
                Config.RecentFolders.RemoveAll(p => string.Equals(p, rd.Path, StringComparison.OrdinalIgnoreCase));
            }
            // 插入目标位置
            int ti = grp.Items.Count;
            if (targetRow != null)
            {
                ti = grp.Items.IndexOf(targetRow.Full);
                if (ti < 0) ti = grp.Items.Count;
            }
            if (!grp.Items.Contains(rd.Path, StringComparer.OrdinalIgnoreCase))
                grp.Items.Insert(ti, rd.Path);
            _curG = Config.FavoriteGroups.IndexOf(grp);
            Config.FavoriteCurGroup = _curG;
            _dirty = true; _fnSave(); _fnBuildRows();
        }

        private string _dragSrcPath;
        private Point _dragSrcPt;
        private EntryRow _dragSrcRow;

        // 星标点击：收藏行熄灭 → 从当前分组移除并移回历史顶部
        private void _fnToggleFav(EntryRow row)
        {
            if (row.IsFav)
            {
                var grp = CurrentGroup() ?? _fnFavGroup(_lastFavG);
                grp.Items.RemoveAll(p => string.Equals(p, row.Full, StringComparison.OrdinalIgnoreCase));
                if (!Config.RecentFolders.Contains(row.Full, StringComparer.OrdinalIgnoreCase))
                    Config.RecentFolders.Insert(0, row.Full);
            }
            else
            {
                var grp = CurrentGroup() ?? _fnFavGroup(_lastFavG);
                if (!grp.Items.Contains(row.Full, StringComparer.OrdinalIgnoreCase))
                    grp.Items.Insert(0, row.Full);
            }
            _dirty = true;
            _fnSave();
            _fnBuildRows();
        }

        private void _fnRowMenu(EntryRow row)
        {
            var m = new ContextMenuStrip { ShowImageMargin = false, BackColor = Config.BG, ForeColor = Config.FG, Font = new Font(Config.FontName, Config.FontSize) };
                m.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            if (!row.IsFav)
                m.Items.Add(I18n.T("add_fav"), null, (s, e) =>
                {
                    var grp = _fnFavGroup(_lastFavG);
                    if (!grp.Items.Contains(row.Full, StringComparer.OrdinalIgnoreCase)) grp.Items.Insert(0, row.Full);
                    _dirty = true; _fnSave(); _fnBuildRows();
                });
            else
                m.Items.Add(I18n.T("fav_remove"), null, (s, e) =>
                {
                    var grp = CurrentGroup() ?? _fnFavGroup(_lastFavG);
                    grp.Items.RemoveAll(p => string.Equals(p, row.Full, StringComparison.OrdinalIgnoreCase));
                    _dirty = true; _fnSave(); _fnBuildRows();
                });
            if (!row.IsFav)
                m.Items.Add(I18n.T("recent_remove"), null, (s, e) =>
                {
                    Config.RecentFolders.RemoveAll(p => string.Equals(p, row.Full, StringComparison.OrdinalIgnoreCase));
                    _dirty = true; _fnSave(); _fnBuildRows();
                });
            m.Show(row, row.PointToClient(Cursor.Position));
        }

        private void _fnSave()
        {
            if (!_dirty) return;
            _dirty = false;
            Config.Save();
        }

        // ---- 行内重命名（与文件列表行同语义）：Enter 提交、Esc 取消、失焦提交 ----
        private TextBox _rowEdit;
        private EntryRow _rowEditRow;

        private void _fnBeginEntryRename(EntryRow row)
        {
            if (row == null || _rowEdit != null) return;
            _fnStopRowHover();
            _fnCancelDragSub();
            var tb = new TextBox
            {
                Font = row.Font,
                BackColor = Config.ListBg,
                ForeColor = Config.ListFg,
                BorderStyle = BorderStyle.FixedSingle,
                Location = row.Location,
                Size = new Size(row.Width, Math.Max(22, row.Height)),
                Text = row.Name,
            };
            _rowEdit = tb;
            _rowEditRow = row;
            row.Visible = false;
            Controls.Add(tb);
            tb.BringToFront();
            tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Return) { e.SuppressKeyPress = true; _fnEndEntryRename(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _fnEndEntryRename(false); }
            };
            tb.LostFocus += (s, e) => { if (_rowEdit == tb) _fnEndEntryRename(true); };
            // 列表不抢前台，编辑要主动激活才能收到键盘（与文件列表行改名一致）
            if (!Focused) Activate();
            tb.Focus();
            tb.SelectAll();
        }

        private void _fnEndEntryRename(bool commit)
        {
            if (_rowEdit == null) return;
            var box = _rowEdit;
            _rowEdit = null;
            var row = _rowEditRow;
            _rowEditRow = null;
            string nn = commit ? box.Text.Trim() : "";
            try { Controls.Remove(box); box.Dispose(); } catch { }
            if (row != null && !row.IsDisposed) row.Visible = true;
            if (!commit || row == null || row.IsDisposed) return;
            string full = row.Full;
            if (string.IsNullOrEmpty(nn) || string.Equals(nn, Path.GetFileName(full), StringComparison.Ordinal)) return;
            bool ok = false;
            string newPath = Path.Combine(Path.GetDirectoryName(full), nn);
            if (File.Exists(full))
            {
                // 文件重命名
                try { File.Move(full, newPath); ok = true; }
                catch (Exception ex) { Program.DebugLog("[entry] file rename fail: " + ex.Message); }
            }
            else
            {
                // RenamePath：校验非法字符/重名 + Directory.Move + 同步固定卡片路径 + 刷新相关面板
                ok = PopupForm.RenamePath(full, nn);
            }
            if (ok)
            {
                // 收藏/历史存的是完整路径：遍历所有分组 + 历史，旧路径全部替换
                foreach (var g in Config.FavoriteGroups)
                    for (int i = 0; i < g.Items.Count; i++)
                        if (string.Equals(g.Items[i], full, StringComparison.OrdinalIgnoreCase)) g.Items[i] = newPath;
                for (int i = 0; i < Config.RecentFolders.Count; i++)
                    if (string.Equals(Config.RecentFolders[i], full, StringComparison.OrdinalIgnoreCase)) Config.RecentFolders[i] = newPath;
                Config.Save();
                _fnBuildRows();
            }
            else { try { System.Media.SystemSounds.Beep.Play(); } catch { } }
        }

        // 轻提示（复制路径等）：小浮层居中于列表顶部，800ms 自关（与文件面板 ShowToast 同款）
        private void _fnToast(string msg)
        {
            var t = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Config.BG,
                Size = new Size(70, 22),
            };
            var lbl = new Label { Text = msg, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Config.FG, BackColor = Config.BG, Font = new Font("Microsoft YaHei", 9) };
            t.Controls.Add(lbl);
            t.Location = new Point(Left + (Width - t.Width) / 2, Top + 34);
            t.Show();
            var tm = new Timer { Interval = 800 };
            tm.Tick += (s, e) => { tm.Stop(); tm.Dispose(); t.Close(); t.Dispose(); };
            tm.Start();
        }

        private bool _fnRowContains(Control c)
        {
            var pt = c.PointToClient(Cursor.Position);
            return pt.X >= 0 && pt.X < c.Width && pt.Y >= 0 && pt.Y < c.Height;
        }

        // 光标是否在控件屏幕范围内（含子控件，避免 MouseLeave 进入子控件误判）
        private bool _fnContainsCursor(Control c)
        {
            var pt = c.PointToClient(Cursor.Position);
            return pt.X >= 0 && pt.X < c.Width && pt.Y >= 0 && pt.Y < c.Height;
        }

        // ---- 行悬停 → 右侧级联子列表（与文件列表行为一致）----
        private void _fnStartRowHover(EntryRow row)
        {
            if (_rowHoverTimer == null)
            {
                _rowHoverTimer = new Timer { Interval = 400 };
                _rowHoverTimer.Tick += (s, e) =>
                {
                    _rowHoverTimer.Stop();
                    if (IsDisposed || _rowHoverPath == null) return;
                    var hr = _rowHoverRow;
                    if (hr == null || hr.IsDisposed) return;
                    var sp = hr.PointToScreen(Point.Empty);
                    Program.ShowEntrySubList(new Point(sp.X + hr.Width, sp.Y), _rowHoverPath);
                };
            }
            _rowHoverRow = row;
            _rowHoverTimer.Stop();
            _rowHoverTimer.Start();
        }

        private void _fnStopRowHover()
        {
            if (_rowHoverTimer != null) _rowHoverTimer.Stop();
            _rowHoverRow = null;
        }

        // ---- 拖拽悬停文件夹（弹簧式）：OLE 拖拽期间 MouseEnter/MouseMove 不触发，
        // 常规行悬停调度失效，由 DragOver 自行调度——拖停 ~500ms 后打开该文件夹的级联
        // 子列表（与文件列表行同语义），可在子列表里继续逐层导航后放置。
        // 列表自身保持依赖 _watchTimer 的拖拽豁免（左键按住不自动关闭）
        private void _fnScheduleDragSub(EntryRow row)
        {
            if (_dragSubRow == row && _dragSubTimer != null) return;   // 该行已在计时
            _fnCancelDragSub();
            _dragSubRow = row;
            _dragSubTimer = new Timer { Interval = 500 };
            _dragSubTimer.Tick += delegate
            {
                var t = _dragSubTimer; t.Stop(); t.Dispose(); _dragSubTimer = null;
                var r = _dragSubRow; _dragSubRow = null;
                if (r == null || r.IsDisposed || IsDisposed) return;
                var sp = r.PointToScreen(Point.Empty);
                Program.ShowEntrySubList(new Point(sp.X + r.Width, sp.Y), r.Full);
            };
            _dragSubTimer.Start();
        }

        private void _fnCancelDragSub(EntryRow row = null)
        {
            if (row != null && _dragSubRow != row) return;   // 已移到另一行：保留那行的调度
            if (_dragSubTimer != null) { _dragSubTimer.Stop(); _dragSubTimer.Dispose(); _dragSubTimer = null; }
            _dragSubRow = null;
        }

        // 数据变更/语言/主题切换 → 开着的列表实时重建（行重排+表头文本图标全刷新）
        internal static void NotifyChanged()
        {
            if (_current == null || _current.IsDisposed) return;
            var f = _current;
            f._fnBuildRows();
            // 配色/主题切换后窗口已存在，主动重算并重画非客户区描边，否则边框卡在旧色
            try { Win11.RefreshNc(f.Handle); } catch { }
            try
            {
                // 表头文本/提示随语言刷新；图标重取——主题切换 ClearUiIconCache 后
                // 旧 Image 引用已 Dispose，不重赋按钮会变空白
                f._titleLbl.Text = I18n.T("entry_title");
                Tips.Set(f._titleLbl, I18n.T("entry_tip"));
                f._pinBtn.Image = PreviewForm.PinIcon(f._pinned);
                f._foldBtn.Image = PreviewForm.PinLaunchIcon(false);
                Tips.Set(f._pinBtn, I18n.T("tip_pin_panel"));
                Tips.Set(f._foldBtn, I18n.T("fold_card"));
            }
            catch { }
        }

        // 右缘拖宽：NCHITTEST 返回 HTRIGHT 交给系统原生改宽；拖完按新宽重建行并记忆
        // 圆角模式（WindowCorners=1）下 WndNcBorder 的 WM_NCCALCSIZE 把客户区四周内缩
        // FramePad 像素，WinForms 的 ClientSize 并不知情：写入补描边占位、读取返回真实客户区，
        // 让四边留白与圆角描边协调（与 PopupForm 一致）；直角模式 FramePad=0 无影响
        public new Size ClientSize
        {
            get
            {
                var s = IsHandleCreated ? Win11.RealClient(Handle) : Size.Empty;
                return s.IsEmpty ? base.ClientSize : s;
            }
            set { base.ClientSize = new Size(value.Width + Win11.FramePad * 2, value.Height + Win11.FramePad * 2); }
        }

        protected override void WndProc(ref Message m)
        {
            // 圆角描边与自身背景同色：亮色描边压在深色配色上很突兀，同色后只剩 DWM 圆角外形
            if (Win11.WndNcBorder(Handle, ref m, () => BackColor)) return;
            if (m.Msg == 0x84)   // WM_NCHITTEST
            {
                var lp = m.LParam.ToInt64();
                var cp = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int cw = ClientSize.Width, ch = ClientSize.Height;
                // 右下角
                if (cp.X >= cw - 6 && cp.Y >= ch - 6) { m.Result = (IntPtr)17; return; }
                // 右缘拖宽
                if (cp.X >= cw - 6 && cp.Y >= 0 && cp.Y < ch) { m.Result = (IntPtr)11; return; }
                // 底缘拖高
                if (cp.Y >= ch - 6 && cp.X >= 0 && cp.X < cw) { m.Result = (IntPtr)12; return; }
            }
            else if (m.Msg == 0x232)   // WM_EXITSIZEMOVE
            {
                int w = ClientSize.Width, h = ClientSize.Height;
                if (w != _listW || h != _fullH)
                {
                    Config.EntryListW = w;
                    Config.EntryListH = h;
                    _dirty = true;
                    _fnBuildRows();
                }
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _current = this;
            if (_watchTimer == null)
            {
                // 离开时长 ≥ 关闭延迟即收（时间阈值而非两拍采样：采样法实际关闭会多出 0.5~1 倍延迟）。
                // 40ms 轮询保证阈值精确；触发区→列表间 4px 间隙的穿越 <30ms，被 ≥60ms 的阈值兜住
                int missSince = 0;
                _watchTimer = new Timer { Interval = 40 };
                _watchTimer.Tick += (s, e2) =>
                {
                    if (!Visible) { _watchTimer.Stop(); return; }
                    if (_pinned) return;   // 固定后不自动关（与文件列表图钉一致）
                    // 行内重命名中不自动关：鼠标可能移开列表，焦点在编辑框
                    if (_rowEdit != null) { missSince = 0; return; }
                    // 拖拽会话中（左键按住，含 OLE 拖放途经）：列表是放置目标，鼠标不在列表上也保持
                    if ((NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0) { missSince = 0; return; }
                    bool inside = _fnListContainsCursor();
                    if (!inside && Config.EntryRectW >= 5 && Config.EntryRectH >= 5)
                    {
                        // 鼠标仍在触发区域内也保持：列表弹在区域下方，区内鼠标不在列表上是常态，
                        // 不保持会形成“弹出→关闭→再弹出”的循环
                        var rp = Cursor.Position;
                        inside = rp.X >= Config.EntryRectX && rp.X < Config.EntryRectX + Config.EntryRectW
                            && rp.Y >= Config.EntryRectY && rp.Y < Config.EntryRectY + Config.EntryRectH;
                    }
                    if (!inside) inside = PopupForm.InAnyHoverPanelOrPreview(Cursor.Position.X, Cursor.Position.Y);
                    if (!inside && Program.EntryCardContainsCursor()) inside = true;   // 折叠卡片在 → 列表由它承接
                    if (inside) { missSince = 0; return; }
                    int now = Environment.TickCount;
                    if (missSince == 0) { missSince = now; return; }
                    // 收藏·历史定位是快速访问入口：关闭快于文件面板（上限 200ms），
                    // 即使全局关闭延迟设得较大也不在这里拖泥带水
                    if (now - missSince >= Math.Max(60, Math.Min(Config.CloseDelayMs, 200)))
                    {
                        _watchTimer.Stop();
                        _fnSave();
                        Close();
                    }
                };
            }
            _watchTimer.Stop();
            _watchTimer.Start();
        }

        private bool _fnListContainsCursor()
        {
            var pt = PointToClient(Cursor.Position);
            return pt.X >= 0 && pt.X < Width && pt.Y >= 0 && pt.Y < Height;
        }

        public static void RecordRecent(string path) { RecordRecent(path, true); }

        // reorder=false（Explorer 轮询每 2.5s 调用）：路径已在历史中就完全跳过——
        // 之前每个已存在的路径都被移到顶部并触发整表重建+写盘，多窗口时每轮轮询
        // 连续重建数次，列表无缓冲直接画 → “每隔几秒闪烁几次”的根因。
        // 真正访问（点击行打开）仍走 reorder=true 置顶
        public static void RecordRecent(string path, bool reorder)
        {
            if (string.IsNullOrEmpty(path) || !PathExists(path)) return;
            bool exists = Config.RecentFolders.Contains(path, StringComparer.OrdinalIgnoreCase);
            if (exists && !reorder) return;
            // 已在顶部且未超上限 = 完全无变化：不写盘不重建。此前点击行会经历两次
            // Save+NotifyChanged（行点击一次、OpenFolderInExplorer 再一次），整表连闪两次
            if (exists && string.Equals(Config.RecentFolders[0], path, StringComparison.OrdinalIgnoreCase)
                && Config.RecentFolders.Count <= Config.EntryRecentMax) return;
            if (!exists || !string.Equals(Config.RecentFolders[0], path, StringComparison.OrdinalIgnoreCase))
            {
                Config.RecentFolders.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                Config.RecentFolders.Insert(0, path);
            }
            if (Config.RecentFolders.Count > Config.EntryRecentMax)
                Config.RecentFolders.RemoveRange(Config.EntryRecentMax, Config.RecentFolders.Count - Config.EntryRecentMax);
            Config.Save();
            // 仅当前正显示历史分组时才重建（收藏分组显示时，历史变更无需立即刷新，
            // 避免子列表悬停级联 OpenSub→RecordRecent 连续重建导致列表闪烁）
            if (_current != null && !_current.IsDisposed && _current._curG < 0) NotifyChanged();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_watchTimer != null) { _watchTimer.Stop(); _watchTimer.Dispose(); _watchTimer = null; }
            if (_rowHoverTimer != null) { _rowHoverTimer.Stop(); _rowHoverTimer.Dispose(); _rowHoverTimer = null; }
            if (_dragSubTimer != null) { _dragSubTimer.Stop(); _dragSubTimer.Dispose(); _dragSubTimer = null; _dragSubRow = null; }

            if (_rowEdit != null) { try { _rowEdit.Dispose(); } catch { } _rowEdit = null; _rowEditRow = null; }
            if (_current == this) _current = null;
            base.OnFormClosed(e);
        }
    }

    // 收藏·历史折叠后的桌面常驻卡片（原型 D 标签带语言）：accent 色条 + 金色★徽章 + 标题；
    // 宽度随语言自适应（TextRenderer 测宽），悬停提亮一档；悬停 400ms / 点击 → 卡片下方左对齐
    // 弹出列表；拖动移动；中键关闭（彻底取消折叠态）
    internal class EntryCardForm : Form, IPaletteTarget
    {
        private Timer _hoverTimer;
        private bool _hover;
        private int _paletteIndex;   // 与桌面卡片同体系：滚轮循环切换，作为后续新卡默认
        private const int CardH = 28;
        protected override bool ShowWithoutActivation { get { return true; } }

        // 高度锁死 28（SetBoundsCore 层，AutoScale/DPI 绕不过）；宽度随语言自适应
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            base.SetBoundsCore(x, y, width, CardH, specified);
        }

        public EntryCardForm(int x, int y)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
            BackColor = Config.BG;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            _paletteIndex = (Config.CardPaletteIndex >= 0 && Config.CardPaletteIndex < CardPalettes.All.Count)
                ? Config.CardPaletteIndex : 0;
            _fnApplyPalette();
            _fnLayout();

            // 高度兜底：构造期设的 ClientSize 可能被 handle 创建时的 DPI/自动缩放改走
            // （首次 39px、Relayout 后 28px 的根因），Shown 时按物理像素再压一次
            Shown += (s, e) => _fnLayout();

            MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Middle) { Close(); return; }
                if (e.Button == MouseButtons.Left)
                {
                    NativeMethods.ReleaseCapture();
                    NativeMethods.SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };
            MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) _fnOpenList(false);
            };
            MouseEnter += (s, e) =>
            {
                _hover = true;
                _fnApplyPalette();
                Invalidate();
                if (_hoverTimer == null)
                {
                    _hoverTimer = new Timer { Interval = 400 };
                    _hoverTimer.Tick += (s2, e2) => { _hoverTimer.Stop(); if (!IsDisposed) _fnOpenList(true); };
                }
                _hoverTimer.Stop();
                _hoverTimer.Start();
            };
            MouseLeave += (s, e) =>
            {
                _hover = false;
                _fnApplyPalette();
                Invalidate();
                if (_hoverTimer != null) _hoverTimer.Stop();
            };
            Tips.Set(this, I18n.T("entry_tip"));
        }

        // 宽度按当前语言标题实测（TextRenderer=GDI 与绘制一致；MeasureString 偏窄会截字）
        private void _fnLayout()
        {
            using (var f = new Font(Config.FontName, 9f))
            {
                var tw = TextRenderer.MeasureText(I18n.T("entry_title"), f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                ClientSize = new Size(12 + 16 + 8 + tw.Width + 10, CardH);
            }
        }

        // 语言/主题切换：重测宽度并重绘（Program 侧调用）
        public void Relayout()
        {
            if (IsDisposed) return;
            _fnLayout();
            _fnApplyPalette();
            Tips.Set(this, I18n.T("entry_tip"));
            Invalidate();
        }

        // ---- 配色（与桌面卡片同体系，滚轮循环切换）----
        private void _fnApplyPalette()
        {
            var pal = CardPalettes.Get(_paletteIndex);
            if (pal == null) return;
            BackColor = CardPalette.Opaque(_hover ? pal.Hover : pal.Bg);
        }

        public void SetPalette(int index, bool persist)
        {
            int n = CardPalettes.All.Count;
            if (n == 0) return;
            index = ((index % n) + n) % n;
            _paletteIndex = index;
            Config.CardPaletteIndex = index;   // 作为后续新卡片的默认配色
            if (persist) Program.SaveConfigDebounced();
            _fnApplyPalette();
            Invalidate();
        }

        public void ApplyPalette() { _fnApplyPalette(); Invalidate(); }

        public int CyclePalette(int dir)
        {
            int n = CardPalettes.All.Count;
            if (n == 0) return _paletteIndex;
            SetPalette(_paletteIndex + dir, true);
            return _paletteIndex;
        }

        private void _fnOpenList(bool auto)
        {
            if (IsDisposed) return;
            // auto=true 悬停触发：刚点行打开过 Explorer（armed=false）时不弹，
            // 避免列表关掉后鼠标还停在卡片/触发区域上又被自动弹回
            if (auto && !Program._entryArmed) return;
            Program.ShowEntryList(Location.X, Bottom + 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            int h = ClientSize.Height;
            var pal = CardPalettes.Get(_paletteIndex);
            var tape = pal != null ? pal.StripColor : Config.Accent;   // 左色条显示当前配色
            var fg = pal != null ? CardPalette.Opaque(pal.Fg) : Config.FG;
            var border = pal != null ? pal.StripColor : Config.Border;
            // 左侧 6px 色条（palette 色，与桌面卡片同语言）
            using (var b = new SolidBrush(tape)) g.FillRectangle(b, 0, 0, 6, h);
            // 金色★徽章：16×16 色块 + 白星（收藏语义恒金色）
            var badge = new Rectangle(12, (h - 16) / 2, 16, 16);
            using (var b = new SolidBrush(ColorTranslator.FromHtml("#f0b429"))) g.FillRectangle(b, badge);
            using (var f = new Font(Config.FontName, 8.5f))
            using (var b = new SolidBrush(Color.White))
                g.DrawString("★", f, b, badge.X + (badge.Width - 10) / 2, badge.Y + (badge.Height - 12) / 2);
            // 标题：徽章右侧，垂直居中
            using (var f = new Font(Config.FontName, 9f))
            using (var b = new SolidBrush(fg))
                g.DrawString(I18n.T("entry_title"), f, b, 36, (h - f.Height) / 2);
            // 边框收尾（palette 自带宽度）
            if (pal != null && pal.StripWidth > 0)
                using (var p = new Pen(border, pal.StripWidth)) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, h - 1);
            else if (pal == null)
                using (var p = new Pen(border)) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, h - 1);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_hoverTimer != null) { _hoverTimer.Stop(); _hoverTimer.Dispose(); _hoverTimer = null; }
            base.OnFormClosed(e);
        }
    }

    // 列表行：图标 | ★收藏星标（恒在最前，可点击）| 文件夹名；路径尾已移除。
    // 斑马底色存 BaseColor（悬停/拖放还原用），配色取文件列表调色板。
    // 行悬停右端显示操作按钮组（与文件列表行一致，无删除）：重命名 · 复制路径 · 固定为桌面卡片
    internal class EntryRow : Control
    {
        public string Full;
        public bool IsFav;
        public new string Name;
        public Font SubFont;
        public Color BaseColor;
        public event EventHandler StarToggled;
        public event EventHandler<bool> StarHover;
        public event EventHandler CopyClicked;
        public event EventHandler PinClicked;
        public event EventHandler RenameClicked;
        public event EventHandler<bool> BtnHover;   // 进入按钮区 = 抑制行级联子列表调度

        private bool _inStar;
        private static readonly Color StarLit = ColorTranslator.FromHtml("#f0b429");
        private readonly Label _btnRename, _btnCopy, _btnPin;

        public EntryRow()
        {
            // 不用 OptimizedDoubleBuffer：同上，BufferedGraphics 在首次弹出有 AV 崩溃记录，行面积小直绘无碍
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            _btnRename = _fnMakeBtn(PreviewForm.EditIcon(Config.ListFg), I18n.T("rename"));
            _btnCopy = _fnMakeBtn(PreviewForm.CopyIcon(Config.ListFg), I18n.T("copy_path"));
            _btnPin = _fnMakeBtn(PreviewForm.PinLaunchIcon(false, Config.ListFg), I18n.T("tip_pin_card"));
            _btnRename.Click += delegate { var h = RenameClicked; if (h != null) h(this, EventArgs.Empty); };
            _btnCopy.Click += delegate { var h = CopyClicked; if (h != null) h(this, EventArgs.Empty); };
            _btnPin.Click += delegate { var h = PinClicked; if (h != null) h(this, EventArgs.Empty); };
        }

        // 按钮创建：18×18 贴行右缘排布（重命名·复制·固定），悬停由行级 OnMouseEnter 统一显隐
        private Label _fnMakeBtn(Image img, string tip)
        {
            var b = new Label
            {
                Size = new Size(18, 18),
                Location = new Point(-40, 3),
                Image = img,
                BackColor = Config.ListHv,
                Cursor = Cursors.Hand,
                Visible = false,
            };
            b.MouseEnter += delegate { var h = BtnHover; if (h != null) h(this, true); };
            b.MouseLeave += delegate { var h = BtnHover; if (h != null) h(this, false); };
            Tips.Set(b, tip);
            Controls.Add(b);
            return b;
        }

        private Rectangle StarRect { get { return new Rectangle(24, 0, 20, Height); } }
        public bool IsStarPoint(Point p) { return StarRect.Contains(p); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_btnPin != null) _btnPin.Left = Width - 20;
            if (_btnCopy != null) _btnCopy.Left = Width - 40;
            if (_btnRename != null) _btnRename.Left = Width - 60;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // 按钮底色取当前行底（悬停高亮 ListHv），与行浑然一体
            foreach (var b in new[] { _btnRename, _btnCopy, _btnPin }) b.BackColor = BackColor;
            foreach (var b in new[] { _btnRename, _btnCopy, _btnPin }) b.Visible = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool inStar = StarRect.Contains(e.Location);
            if (inStar == _inStar) return;
            _inStar = inStar;
            var h = StarHover;
            if (h != null) h(this, inStar);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // 移到子按钮上父级同样收到 MouseLeave（子控件是独立窗口）：光标仍在按钮区内整组保留
            if (!_fnCursorOnButtons())
                foreach (var b in new[] { _btnRename, _btnCopy, _btnPin }) b.Visible = false;
            if (!_inStar) return;
            _inStar = false;
            var h = StarHover;
            if (h != null) h(this, false);
            Invalidate();
        }

        private bool _fnCursorOnButtons()
        {
            var pt = Cursor.Position;
            foreach (var b in new[] { _btnRename, _btnCopy, _btnPin })
            {
                if (b == null || !b.Visible) continue;
                if (b.RectangleToScreen(b.ClientRectangle).Contains(pt)) return true;
            }
            return false;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            // 点星标 = 收藏/取消收藏，不触发行点击（打开文件夹）
            if (e.Button == MouseButtons.Left && StarRect.Contains(e.Location))
            {
                var t = StarToggled;
                if (t != null) t(this, EventArgs.Empty);
                return;
            }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var icon = PopupForm.GetEntryIconFor(Full);
            if (icon != null) g.DrawImage(icon, 6, (Height - icon.Height) / 2);
            // 星标：收藏=实心金（与桌面卡片★徽章同色），历史=描边弱化；悬停提亮
            var sf = SubFont ?? Font;
            var starColor = IsFav ? StarLit : (_inStar ? Config.ListFg : Config.ListMu);
            TextRenderer.DrawText(g, IsFav ? "★" : "☆", sf, StarRect, starColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            // 名称限制在行宽内，过长中间省略；DrawString 抗锯齿（TextRenderer 在双缓冲位图上 ClearType 退化出锯齿）
            float availW = Width - 46 - 6;
            var displayName = availW > 20 ? RowLabel.MidEllipsis(g, Name, Font, availW) : Name;
            using (var b = new SolidBrush(ForeColor))
                g.DrawString(displayName, Font, b, 46, (Height - Font.Height) / 2);
        }
    }

    // 触发区域桌面指示：半透明色块常驻标示入口位置（RGBA=RGB 底色 + 窗体 Opacity=alpha）。
    // WS_EX_TRANSPARENT 鼠标全穿透——纯视觉层，悬停触发仍由 Program.Tick 按矩形判定
    internal class EntryIndicatorForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20;   // WS_EX_TRANSPARENT：点击/悬停穿透
                return cp;
            }
        }

        public EntryIndicatorForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
        }

        public void Apply(Rectangle rect, int argb)
        {
            var c = Color.FromArgb(argb);
            BackColor = Color.FromArgb(255, c.R, c.G, c.B);
            Opacity = Math.Max(0.04, c.A / 255.0);
            Bounds = rect;
        }
    }

    // 桌面框选遮罩：盖住鼠标所在屏（FromPoint），Opacity 真半透明，
    // 拖拽画矩形（实时描边），松开确定并回调，Esc 取消
    internal class AreaPickForm : Form
    {
        private readonly Action<Rectangle> _onPicked;
        private Point _start;
        private Rectangle _rect;
        private bool _dragging;

        public AreaPickForm(Action<Rectangle> onPicked)
        {
            _onPicked = onPicked;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            // 鼠标所在屏（框选按钮在哪个屏就盖哪个屏；PrimaryScreen 会盖错屏）
            Bounds = Screen.FromPoint(Cursor.Position).Bounds;
            DoubleBuffered = true;
            // WinForms 不支持 ARGB BackColor（会截断成纯黑全屏窗）→ 用 Opacity 做真半透明
            BackColor = Color.Black;
            Opacity = 0.25;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { _start = e.Location; _dragging = true; _rect = new Rectangle(e.Location, Size.Empty); Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            int x = Math.Min(_start.X, e.X), y = Math.Min(_start.Y, e.Y);
            _rect = new Rectangle(x, y, Math.Abs(e.X - _start.X) + 1, Math.Abs(e.Y - _start.Y) + 1);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            if (_rect.Width >= 5 && _rect.Height >= 5)
            {
                var picked = new Rectangle(RectangleToScreen(_rect).Location, _rect.Size);
                DialogResult = DialogResult.OK;
                Close();
                Program.DebugLog("[entry] area picked " + picked);
                if (_onPicked != null) _onPicked(picked);
            }
            else
            {
                // 太小的框视为误触，继续选
                _rect = Rectangle.Empty;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_rect.Width > 0)
            {
                bool tooSmall = _rect.Width < 5 || _rect.Height < 5;
                Color boxColor = tooSmall ? Color.Red : Config.Accent;
                using (var p = new Pen(boxColor, 2))
                {
                    e.Graphics.DrawRectangle(p, _rect);
                    using (var b = new SolidBrush(Color.FromArgb(60, boxColor)))
                        e.Graphics.FillRectangle(b, _rect);
                }
                // 实时尺寸：跟随光标显示 宽 × 高（框选触发区/暂停区时即所见区域大小）
                string sz = _rect.Width + " × " + _rect.Height;
                using (var f = new Font(Config.FontName, 9, FontStyle.Bold))
                {
                    var msz = e.Graphics.MeasureString(sz, f);
                    var lp = PointToClient(Cursor.Position);
                    float tx = Math.Min(lp.X + 14, Width - msz.Width - 8);
                    float ty = Math.Min(lp.Y + 18, Height - msz.Height - 6);
                    using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                        e.Graphics.FillRectangle(b, tx - 3, ty - 2, msz.Width + 6, msz.Height + 3);
                    using (var b = new SolidBrush(Color.White))
                        e.Graphics.DrawString(sz, f, b, tx, ty);
                }
            }
            using (var b = new SolidBrush(Color.White))
            {
                var tip = I18n.T("area_pick") + "  ·  Esc";
                var f = new Font(Config.FontName, 11);
                var sz2 = e.Graphics.MeasureString(tip, f);
                e.Graphics.DrawString(tip, f, b, (Width - sz2.Width) / 2, Height / 2 - sz2.Height);
            }
        }
    }

    // 双缓冲 Panel（侧栏色块自绘用，避免闪烁）
    internal class SideTab : Panel
    {
        public SideTab() { DoubleBuffered = true; }
    }

    // 简易输入对话框（分组改名用，替代缺失的 InputBox）
    internal class PromptDialog : Form
    {
        private readonly TextBox _tb;
        public static string Show(string title, string initial)
        {
            using (var d = new PromptDialog(title, initial))
            {
                return d.ShowDialog() == DialogResult.OK ? d._tb.Text.Trim() : null;
            }
        }
        private PromptDialog(string title, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            TopMost = true;
            ClientSize = new Size(280, 80);
            MaximizeBox = false; MinimizeBox = false;
            BackColor = Config.BG; ForeColor = Config.FG;
            var lbl = new Label { Text = title, Location = new Point(12, 10), AutoSize = true, ForeColor = Config.FG };
            _tb = new TextBox { Text = initial, Location = new Point(12, 30), Size = new Size(256, 24) };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(120, 52), Size = new Size(70, 24) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(198, 52), Size = new Size(70, 24) };
            Controls.AddRange(new Control[] { lbl, _tb, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
        }
    }
}