using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 链接备注的数据模型：把 "[标题](url)\n备注正文\n![](图片)" 解析成三段，
    // 界面按三段分区呈现；ToMarkdown 统一回写单括号 ![](rel)，与 LinkAssets 清理器兼容。
    internal class LinkNoteData
    {
        public string Title;
        public string Url;
        public string Note;
        public string Header;   // 原始首行 [标题](url)，备注编辑后原样带回，避免链接头丢失
        public List<string> Images;

        private static readonly Regex ImgRe = new Regex(@"!\[[^\]]*\]\(([^)]+)\)", RegexOptions.Compiled);

        public LinkNoteData()
        {
            Title = ""; Url = ""; Note = ""; Header = ""; Images = new List<string>();
        }

        public static LinkNoteData Parse(string md)
        {
            var d = new LinkNoteData();
            md = md ?? "";
            string[] lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string head = LinkEntry.Header(md);
            string ht, hu;
            bool hasLink = LinkEntry.TryParse(md, out ht, out hu);
            if (hasLink) { d.Title = ht; d.Url = hu; d.Header = head; }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!string.IsNullOrEmpty(head) && line.Trim() == head) continue;
                if (!hasLink && i == 0) { d.Title = line.Trim(); continue; }
                Match m = ImgRe.Match(line);
                if (m.Success) { d.Images.Add(m.Groups[1].Value.Trim()); continue; }
                d.Note += (d.Note.Length == 0 ? "" : "\n") + line;
            }
            d.Note = d.Note.Trim('\n', '\r', ' ');
            return d;
        }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            // 有原始链接头就原样带回；标题被编辑过时用新标题重建，绝不能退化成裸文本
            string head = LinkEntry.IsWebUrl(Url)
                ? (string.IsNullOrEmpty(Header) ? LinkEntry.Format(Title, Url)
                   : (Title == LinkEntry.Display(Header) ? Header : LinkEntry.Format(Title, Url)))
                : Title;
            sb.Append(head);
            if (!string.IsNullOrEmpty(Note))
            {
                sb.Append('\n').Append(Note.Trim('\n', '\r', ' '));
            }
            for (int i = 0; i < Images.Count; i++)
            {
                sb.Append("\n![](").Append(Images[i]).Append(')');
            }
            return sb.ToString();
        }
    }

    // 链接备注的分区式编辑面板：
    //   只读链接卡（文字徽标 + 标题 + URL + ↗）
    //   备注文本框（透明、自动增高、focus 才显边框）
    //   线索图网格（固定高缩略图 + hover 删除 + ＋ 添加 + 点击灯箱）
    // 源码视图由外层 PreviewForm 的 _sourceToggle 切换，此处只负责块视图。
    internal class NoteBlockPanel : Panel
    {
        private readonly Font _textFont;
        private readonly int _pad;
        private readonly int _thumbMaxW;
        private readonly int _gap;
        private readonly int _thumbH;
        private readonly Action _onChanged;
        private LinkNoteData _data;
        private bool _dirty;
        private bool _loading;   // Load() 期间把文本塞进控件不算用户编辑

        private Panel _card;
        private bool _cardHover;
        private TextBox _titleEdit;
        private TextBox _noteBox;
        private Label _notePlaceholder;
        private Label _secLabel;
        private Panel _grid;
        private readonly List<Control> _thumbViews = new List<Control>();
        private readonly List<int> _thumbWidths = new List<int>();
        private readonly List<int> _thumbHeights = new List<int>();
        private ToolTip _tip;
        private Label _toast;
        private Timer _toastTimer;


        private static readonly Color ThumbBg = Color.FromArgb(34, 34, 38);

        public bool IsDirty { get { return _dirty; } }

        public NoteBlockPanel(Font textFont, int pad, int imgMaxW, Action onChanged)
        {
            _textFont = textFont;
            _pad = Math.Max(Dpi.S(8), pad);
            _thumbMaxW = Math.Max(Dpi.S(320), imgMaxW);
            _gap = Dpi.S(10);
            _thumbH = Dpi.S(176);
            _onChanged = onChanged;
            AutoScroll = true;
            BackColor = Color.Transparent;
            DoubleBuffered = true;
            AllowDrop = true;
            _tip = new ToolTip();
            BuildUi();
            _data = new LinkNoteData();
        }

        public void Load(string md)
        {
            _loading = true;
            try
            {
                _data = LinkNoteData.Parse(md);
                // 必须先建缩略图区：设置备注文本会触发 OnTextChanged→Relayout→LayoutGrid，
                // 那里要用 _addBtn；以前顺序反了，带图片的备注一打开预览就 NullReferenceException，
                // 预览窗在构造途中抛异常，表现就是"备注后预览打不开"。
                RebuildThumbs();
                _noteBox.Text = _data.Note;
                // 打开时不要把整段备注变成选区（看上去像用户已选中准备编辑）
                _noteBox.SelectionStart = 0;
                _noteBox.SelectionLength = 0;
                UpdatePlaceholder();
                AutoSizeNote();
                Relayout();
            }
            finally { _loading = false; }
            _dirty = false;
        }

        public string GetMarkdown()
        {
            _data.Note = _noteBox.Text;
            return _data.ToMarkdown();
        }

        public void FocusFirst()
        {
            _noteBox.Focus();
            _noteBox.SelectionStart = _noteBox.Text.Length;
            _noteBox.SelectionLength = 0;
        }

        // 重命名链接时直接把焦点给标题编辑框
        public void FocusTitle()
        {
            BeginTitleEdit();
        }

        private void BuildUi()
        {
            // 链接卡：自绘圆角 + 徽标 + 标题 + URL + ↗，整卡点击打开 URL
            _card = new Panel { BackColor = Color.Transparent, Cursor = Cursors.Hand };
            _card.Paint += CardPaint;
            _card.MouseEnter += delegate { _cardHover = true; _card.Invalidate(); };
            _card.MouseLeave += delegate { _cardHover = false; _card.Invalidate(); };
            _card.Click += delegate(object s, EventArgs e)
            {
                var me = e as MouseEventArgs;
                if (me != null && TitleRect().Contains(me.Location)) { BeginTitleEdit(); return; }
                if (!LinkEntry.IsWebUrl(_data.Url)) return;
                try { Process.Start(new ProcessStartInfo(_data.Url) { UseShellExecute = true }); }
                catch { }
            };
            Controls.Add(_card);
            _tip.SetToolTip(_card, "点击标题可改名 · 点击其它区域打开链接");

            // 备注框：透明无边框，focus 才显卡片底与边框
            _noteBox = new TextBox
            {
                Multiline = true,
                Font = new Font(_textFont.FontFamily, 9f),
                BorderStyle = BorderStyle.None,
                BackColor = Config.ListBg,
                ForeColor = Config.ListFg,
                WordWrap = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.None,
            };
            _noteBox.TextChanged += delegate { MarkDirty(); AutoSizeNote(); Relayout(); };
            Controls.Add(_noteBox);

            _notePlaceholder = new Label
            {
                Text = "添加备注…",
                ForeColor = Config.ListMu,
                BackColor = Color.Transparent,
                Font = _noteBox.Font,
                AutoSize = true,
            };
            _notePlaceholder.Click += delegate { _noteBox.Focus(); };
            Controls.Add(_notePlaceholder);

            // 分节标题：线索图 · N + 横线
            _secLabel = new Label
            {
                ForeColor = Config.ListMu,
                BackColor = Color.Transparent,
                Font = new Font(_textFont.FontFamily, 8f),
                AutoSize = true,
            };
            Controls.Add(_secLabel);

            _grid = new Panel { BackColor = Color.Transparent };
            Controls.Add(_grid);

            _toast = new Label
            {
                Text = "",
                ForeColor = Color.White,
                BackColor = Color.FromArgb(34, 34, 34),
                Font = new Font("Microsoft YaHei", 8f),
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false,
            };
            Controls.Add(_toast);
            _toastTimer = new Timer { Interval = 1400 };
            _toastTimer.Tick += delegate { _toastTimer.Stop(); _toast.Visible = false; };
        }

        private Color CardBg
        {
            get
            {
                float k = PvDarkSingleton ? 0.10f : 0.55f;
                return Blend(Config.ListBg, Color.White, k);
            }
        }
        private static bool PvDarkSingleton
        {
            get
            {
                Color c = Config.ListBg;
                return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;
            }
        }
        private static Color Blend(Color a, Color b, float k)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * k),
                (int)(a.G + (b.G - a.G) * k),
                (int)(a.B + (b.B - a.B) * k));
        }

        // 标题可点击编辑：返回标题在卡片内的矩形
        private Rectangle TitleRect()
        {
            int bh = Dpi.S(36);
            int by = (_card.Height - bh) / 2;
            int tx = _pad + bh + Dpi.S(12);
            int tw = _card.Width - tx - Dpi.S(40);
            return new Rectangle(tx, by, Math.Max(1, tw), Dpi.S(19));
        }

        private void BeginTitleEdit()
        {
            if (_titleEdit != null) return;
            Rectangle r = TitleRect();
            var tb = new TextBox
            {
                Text = _data.Title,
                Font = new Font(_textFont.FontFamily, 10f, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = CardBg,
                ForeColor = Config.ListFg,
                Location = new Point(_card.Left + r.X, _card.Top + r.Y - Dpi.S(2)),
                Size = new Size(r.Width, Dpi.S(23)),
            };
            tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Return) { e.SuppressKeyPress = true; EndTitleEdit(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndTitleEdit(false); }
            };
            tb.LostFocus += (s, e) => EndTitleEdit(true);
            _titleEdit = tb;
            Controls.Add(tb);
            tb.BringToFront();
            tb.Focus();
            tb.SelectAll();
        }

        private void EndTitleEdit(bool commit)
        {
            if (_titleEdit == null) return;
            var tb = _titleEdit;
            _titleEdit = null;
            string nt = tb.Text.Trim();
            try { Controls.Remove(tb); tb.Dispose(); } catch { }
            if (commit && nt.Length > 0 && !string.Equals(nt, _data.Title, StringComparison.Ordinal))
            {
                _data.Title = nt;
                _data.Header = LinkEntry.IsWebUrl(_data.Url) ? LinkEntry.Format(nt, _data.Url) : "";
                MarkDirty();
                _card.Invalidate();
            }
        }

        private void CardPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, _card.Width - 1, _card.Height - 1);
            Color line = _cardHover ? Config.ListAc : Config.ListBd;
            using (var path = RoundPath(r, Dpi.S(10)))
            {
                using (var br = new SolidBrush(CardBg)) g.FillPath(br, path);
                using (var pen = new Pen(line, _cardHover ? 1.6f : 1f)) g.DrawPath(pen, path);
            }

            int bh = Dpi.S(36);
            int by = (_card.Height - bh) / 2;
            Rectangle badge = new Rectangle(_pad, by, bh, bh);
            using (var path = RoundPath(badge, Dpi.S(8)))
            using (var br = new SolidBrush(Config.ListAc))
                g.FillPath(br, path);
            string letter = BadgeLetter(_data.Url, _data.Title);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var br = new SolidBrush(Config.ListAcFg))
                g.DrawString(letter, new Font(_textFont.FontFamily, 12f, FontStyle.Bold), br, badge, sf);

            int tx = badge.Right + Dpi.S(12);
            int tw = _card.Width - tx - Dpi.S(40);
            if (tw < Dpi.S(40)) return;
            using (var titleFont = new Font(_textFont.FontFamily, 10f, FontStyle.Bold))
                TextRenderer.DrawText(g, _data.Title, titleFont, new Rectangle(tx, by, tw, Dpi.S(19)),
                    Config.ListFg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (_cardHover)
                using (var pen = new Pen(Config.ListAc, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                    g.DrawLine(pen, tx, by + Dpi.S(19), tx + tw, by + Dpi.S(19));
            using (var urlFont = new Font(_textFont.FontFamily, 8f))
                TextRenderer.DrawText(g, DisplayUrl(_data.Url), urlFont, new Rectangle(tx, by + Dpi.S(18), tw, Dpi.S(16)),
                    Config.ListMu, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            using (var arrowFont = new Font(_textFont.FontFamily, 14f))
            using (var br = new SolidBrush(_cardHover ? Config.ListAc : Config.ListMu))
                g.DrawString("↗", arrowFont, br, _card.Width - Dpi.S(30), by + Dpi.S(6));
        }

        private static GraphicsPath RoundPath(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            radius = d / 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static string BadgeLetter(string url, string title)
        {
            string host = "";
            try
            {
                Uri u;
                if (Uri.TryCreate(url, UriKind.Absolute, out u)) host = u.Host;
            }
            catch { }
            string src = !string.IsNullOrEmpty(host) ? host : title;
            if (string.IsNullOrEmpty(src)) return "?";
            // 去掉 www. 前缀，取第一个有意义的字母/数字
            if (src.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) src = src.Substring(4);
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            }
            return "?";
        }

        private static string DisplayUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string s = url;
            int p = s.IndexOf("://", StringComparison.Ordinal);
            if (p >= 0) s = s.Substring(p + 3);
            if (s.EndsWith("/", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            return s;
        }

        private void UpdatePlaceholder()
        {
            _notePlaceholder.Visible = string.IsNullOrEmpty(_noteBox.Text);
        }

        private void AutoSizeNote()
        {
            int w = Math.Max(Dpi.S(40), _noteBox.Width - Dpi.S(8));
            int lines = 0;
            string[] paras = _noteBox.Text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < paras.Length; i++)
            {
                var sz = TextRenderer.MeasureText(paras[i], _noteBox.Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak);
                lines += Math.Max(1, (sz.Height + _noteBox.Font.Height - 1) / _noteBox.Font.Height);
            }
            _noteBox.Height = Math.Max(Dpi.S(40), Math.Max(1, lines) * (_noteBox.Font.Height + 3) + Dpi.S(8));
        }

        private void Relayout()
        {
            if (_data == null) return;   // Load 之前的构造期事件直接忽略
            int pad = _pad;
            int w = Math.Max(0, ClientSize.Width - pad * 2);
            int y = pad;

            _card.SetBounds(pad, y, w, Dpi.S(56));
            y = _card.Bottom + Dpi.S(8);

            _noteBox.SetBounds(pad, y, w, _noteBox.Height);
            _notePlaceholder.Location = new Point(pad + Dpi.S(2), y + Dpi.S(1));
            y = _noteBox.Bottom + Dpi.S(18);

            _secLabel.Text = "线索图 · " + _data.Images.Count.ToString();
            _secLabel.Location = new Point(pad, y);
            y = _secLabel.Bottom + Dpi.S(8);

            LayoutGrid(pad, y, w);
            y += _grid.Height + Dpi.S(8);

            _toast.Location = new Point(Math.Max(0, (ClientSize.Width - Dpi.S(120)) / 2), Math.Max(0, ClientSize.Height - Dpi.S(36)));
            _toast.Size = new Size(Dpi.S(120), Dpi.S(22));
            _toast.BringToFront();
        }

        // 缩略图固定高、按图片宽高比定宽，横向排满自动换行（原型 .grid/.th 行为）
        private void LayoutGrid(int x0, int y0, int availW)
        {
            int x = 0, y = 0, rowH = 0;
            // 构造期间可能先收到 SizeChanged/TextChanged，_addBtn 还没建好
            if (_addBtn == null) RebuildThumbs();
            int maxH = (int)(Screen.FromControl(this).WorkingArea.Height * 0.6);
            for (int i = 0; i < _thumbViews.Count; i++)
            {
                var holder = _thumbViews[i];
                var pb = holder.Controls.Count > 0 ? holder.Controls[0] as PictureBox : null;
                Image img = pb != null ? pb.Image : null;
                // 以预览窗口内容宽度为准铺开；比窗口窄的图片保持原始尺寸（不放大）
                int tw = Math.Max(Dpi.S(60), availW), th = _thumbH;
                if (img != null)
                {
                    tw = Math.Min(img.Width, Math.Max(Dpi.S(60), availW));
                    th = Math.Max(1, (int)Math.Round(img.Height * (double)tw / img.Width));
                    if (th > maxH) { th = maxH; tw = Math.Max(1, (int)Math.Round(img.Width * (double)th / img.Height)); }
                }
                if (x > 0 && x + tw + _gap > availW) { x = 0; y += rowH + _gap; rowH = 0; }
                holder.SetBounds(x, y, tw, th);
                // ✕ 跟随新的宽高放回右上角
                if (holder.Controls.Count > 1)
                    holder.Controls[1].Location = new Point(tw - holder.Controls[1].Width - Dpi.S(5), Dpi.S(5));
                _thumbWidths[i] = tw;
                _thumbHeights[i] = th;
                x += tw + _gap;
                rowH = Math.Max(rowH, th);
            }
            int addW = Dpi.S(96);
            if (x > 0 && x + addW + _gap > availW) { x = 0; y += rowH + _gap; }
            _addBtn.Location = new Point(x, y);
            rowH = Math.Max(rowH, _thumbH);
            _grid.SetBounds(x0, y0, availW, y + rowH);
        }

        private Panel _addBtn;

        private void RebuildThumbs()
        {
            foreach (var c in _thumbViews) { _grid.Controls.Remove(c); c.Dispose(); }
            _thumbViews.Clear();
            _thumbWidths.Clear();
            _thumbHeights.Clear();

            if (_addBtn == null)
            {
                _addBtn = new Panel { BackColor = Color.Transparent, Cursor = Cursors.Hand };
                _addBtn.Paint += AddPaint;
                _addBtn.Click += delegate { AddFromClipboard(); };
                _tip.SetToolTip(_addBtn, "添加图片（也可粘贴 / 拖入）");
                _grid.Controls.Add(_addBtn);
            }

            for (int i = 0; i < _data.Images.Count; i++)
                AddThumbView(_data.Images[i], i);

            if (_grid.Controls.Contains(_addBtn)) { _grid.Controls.Remove(_addBtn); _grid.Controls.Add(_addBtn); }
        }

        private void AddThumbView(string rel, int index)
        {
            var holder = new Panel { BackColor = ThumbBg, Cursor = Cursors.Hand };
            var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = ThumbBg };
            // 一进来就按预览窗口宽度铺开显示（尺寸在 LayoutGrid 里按可用宽度计算）
            try
            {
                string full = LinkAssets.Resolve(rel);
                if (File.Exists(full))
                {
                    using (var img = Image.FromFile(full))
                        pb.Image = new Bitmap(img);
                }
            }
            catch { }
            holder.Size = new Size(Dpi.S(120), _thumbH);
            holder.Controls.Add(pb);

            var del = new Label
            {
                Text = "✕",
                Size = Dpi.Sz(20, 20),
                BackColor = Color.FromArgb(170, 0, 0, 0),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 8f),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Visible = false,
            };
            del.Location = new Point(Dpi.S(120) - del.Width - Dpi.S(5), Dpi.S(5));
            int captured = index;
            del.Click += delegate(object s, EventArgs e)
            {
                if (captured >= 0 && captured < _data.Images.Count)
                {
                    _data.Images.RemoveAt(captured);
                    RebuildThumbs();
                    Relayout();
                    MarkDirty();
                    ShowToast("已移除");
                }
            };
            holder.Controls.Add(del);
            holder.MouseEnter += delegate { del.Visible = true; };
            holder.MouseLeave += delegate { del.Visible = false; };
            // 双击：默认看图程序打开
            pb.DoubleClick += delegate { OpenWithDefaultApp(rel); };
            holder.DoubleClick += delegate { OpenWithDefaultApp(rel); };

            _tip.SetToolTip(pb, Path.GetFileName(rel) + " · 双击用默认看图程序打开");
            _tip.SetToolTip(holder, Path.GetFileName(rel));
            _grid.Controls.Add(holder);
            _thumbViews.Add(holder);
            _thumbWidths.Add(0);
            _thumbHeights.Add(0);
        }

        private void AddPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, _addBtn.Width - 1, _addBtn.Height - 1);
            using (var pen = new Pen(Config.ListMu, 1.4f) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(pen, r);
            using (var f = new Font("Microsoft YaHei", 22f))
            using (var br = new SolidBrush(Config.ListMu))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("＋", f, br, r, sf);
            }
        }

        // 双击：用系统默认看图程序打开
        private void OpenWithDefaultApp(string rel)
        {
            string full = LinkAssets.Resolve(rel);
            if (!File.Exists(full)) return;
            try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); } catch { }
        }

        private void AddFromClipboard()
        {
            if (!Clipboard.ContainsImage()) { ShowToast("剪贴板没有图片"); return; }
            Image img = null;
            try { img = Clipboard.GetImage(); } catch { }
            if (img == null) { ShowToast("剪贴板没有图片"); return; }
            string rel = null;
            try { rel = LinkAssets.SaveImage(img); } catch { }
            img.Dispose();
            if (rel == null) { ShowToast("保存失败"); return; }
            _data.Images.Add(rel);
            RebuildThumbs();
            Relayout();
            MarkDirty();
            ShowToast("已添加图片");
        }

        private void ShowToast(string msg)
        {
            _toast.Text = msg;
            _toast.Visible = true;
            _toast.BringToFront();
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void MarkDirty()
        {
            if (_loading) return;   // 程序填充文本不触发保存按钮/自动保存
            if (!_dirty) { _dirty = true; if (_onChanged != null) _onChanged(); }
            UpdatePlaceholder();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.V) && Clipboard.ContainsImage())
            {
                AddFromClipboard();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static DateTime _lastDragEnterDump = DateTime.MinValue;

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = DragDropEffects.None;
            if (e.Data == null) return;
            // 节流 dump：拖动进入会高频触发，1 秒内只记一次
            if ((DateTime.Now - _lastDragEnterDump).TotalMilliseconds > 1000)
            {
                _lastDragEnterDump = DateTime.Now;
                LinkEntry.DumpDrag(e.Data, "NoteBlockPanel.OnDragEnter");
            }
            // 接受：本地文件 / 位图 / DIB / 浏览器图片常带的 HTML 或 URI
            if (e.Data.GetDataPresent(DataFormats.FileDrop)
                || e.Data.GetDataPresent(DataFormats.Bitmap)
                || e.Data.GetDataPresent("DeviceIndependentBitmap")
                || e.Data.GetDataPresent("text/html")
                || e.Data.GetDataPresent(DataFormats.Html)
                || e.Data.GetDataPresent("UniformResourceLocatorW")
                || e.Data.GetDataPresent("text/uri-list"))
                e.Effect = DragDropEffects.Copy;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            if (e.Data == null) return;
            LinkEntry.DumpDrag(e.Data, "NoteBlockPanel.OnDragDrop");
            int added = 0;

            // 1) 本地图片文件
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        string f = files[i];
                        if (string.IsNullOrEmpty(f) || !File.Exists(f)) continue;
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".gif" && ext != ".bmp") continue;
                        try
                        {
                            using (var img = Image.FromFile(f))
                            {
                                string rel = LinkAssets.SaveImage(img);
                                if (rel != null) { _data.Images.Add(rel); added++; }
                            }
                        }
                        catch { }
                    }
                }
            }

            // 2) 浏览器拖出的位图（CF_BITMAP，WinForms 已转成 Image）
            if (added == 0 && e.Data.GetDataPresent(DataFormats.Bitmap))
            {
                var bmp = e.Data.GetData(DataFormats.Bitmap) as Image;
                if (bmp != null)
                {
                    try
                    {
                        string rel = LinkAssets.SaveImage(bmp);
                        if (rel != null) { _data.Images.Add(rel); added++; }
                    }
                    catch { }
                }
            }

            // 3) Chrome/Edge 常给 DIB(DeviceIndependentBitmap) 而不是 CF_BITMAP
            if (added == 0 && (e.Data.GetDataPresent("DeviceIndependentBitmap") || e.Data.GetDataPresent("CF_DIB")))
            {
                string fmt = e.Data.GetDataPresent("DeviceIndependentBitmap") ? "DeviceIndependentBitmap" : "CF_DIB";
                try
                {
                    byte[] dib = GetBytes(e.Data.GetData(fmt));
                    Image img = LinkAssets.ImageFromDib(dib);
                    if (img != null)
                    {
                        string rel = LinkAssets.SaveImage(img);
                        img.Dispose();
                        if (rel != null) { _data.Images.Add(rel); added++; }
                    }
                }
                catch { }
            }

            // 4) 浏览器常见的 PNG 格式（Chrome 拖图片会带 "PNG" 剪贴板格式）
            if (added == 0 && e.Data.GetDataPresent("PNG"))
            {
                try
                {
                    string rel = LinkAssets.SaveImageBytes(GetBytes(e.Data.GetData("PNG")));
                    if (rel != null) { _data.Images.Add(rel); added++; }
                }
                catch { }
            }

            // 5) 兜底：从 HTML <img src> / URI-list 里取图片 URL 下载
            if (added == 0)
            {
                string imgUrl = ExtractImageUrl(e.Data);
                if (imgUrl != null)
                {
                    try
                    {
                        string rel = LinkAssets.SaveImageBytes(LinkAssets.DownloadBytes(imgUrl));
                        if (rel != null) { _data.Images.Add(rel); added++; }
                    }
                    catch { }
                }
            }

            if (added > 0)
            {
                RebuildThumbs(); Relayout(); MarkDirty();
                ShowToast("已添加 " + added.ToString() + " 张图片");
            }
            else
                ShowToast("未能识别图片，请查看 drag_debug.log");
        }

        private static byte[] GetBytes(object obj)
        {
            if (obj == null) return null;
            var ms = obj as MemoryStream;
            if (ms != null) return ms.ToArray();
            return obj as byte[];
        }

        // 从拖放数据里找图片 URL：text/uri-list → UniformResourceLocatorW → HTML <img src>
        private static string ExtractImageUrl(IDataObject data)
        {
            try
            {
                if (data.GetDataPresent("text/uri-list"))
                {
                    byte[] b = GetBytes(data.GetData("text/uri-list")) ?? Encoding.UTF8.GetBytes(data.GetData("text/uri-list") as string ?? "");
                    foreach (var line in Encoding.UTF8.GetString(b).Split('\n'))
                    {
                        string u = line.Trim();
                        if (u.Length > 0 && u[0] != '#' && (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return u;
                    }
                }
                if (data.GetDataPresent("UniformResourceLocatorW"))
                {
                    byte[] b = GetBytes(data.GetData("UniformResourceLocatorW"));
                    if (b != null)
                    {
                        string u = Encoding.Unicode.GetString(b).TrimEnd('\0').Trim();
                        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return u;
                    }
                }
                string html = null;
                if (data.GetDataPresent(DataFormats.Html)) html = data.GetData(DataFormats.Html) as string;
                if (string.IsNullOrEmpty(html) && data.GetDataPresent("text/html")) html = data.GetData("text/html") as string;
                if (!string.IsNullOrEmpty(html))
                {
                    var m = Regex.Match(html, "<img\\b[^>]*\\bsrc\\s*=\\s*[\"']?([^\"'\\s>]+)", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string u = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return u;
                    }
                }
            }
            catch { }
            return null;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            AutoSizeNote();
            Relayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var c in _thumbViews)
                {
                    var pb = c.Controls.Count > 0 ? c.Controls[0] as PictureBox : null;
                    if (pb != null && pb.Image != null) pb.Image.Dispose();
                }
                if (_tip != null) { _tip.Dispose(); _tip = null; }
                if (_toastTimer != null) { _toastTimer.Dispose(); _toastTimer = null; }

            }
            base.Dispose(disposing);
        }
    }
}
