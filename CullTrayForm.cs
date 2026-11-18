using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace HoverExplorer
{
    // 筛选标记架：预览时按 M（或点视频条上的 ＋）把文件收进架子，攒着批量拖出/复制。
    // 会话级（不持久化），架子空了自动收起。卡片可拖出（FileDrop）、左键点击预览、右键移除。
    // 粗筛素材的闭环：空格开预览 → M 标记 → ←/→ 换下一个 → M…… 筛完一次性拖出或复制
    internal static class CullTray
    {
        private static readonly List<string> _items = new List<string>();
        private static CullTrayForm _form;

        public static int Count { get { return _items.Count; } }

        public static List<string> Snapshot() { return new List<string>(_items); }

        public static void Toggle(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            int idx = _items.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _items.RemoveAt(idx); else _items.Add(path);
            SyncForm();
        }

        public static void Remove(string path)
        {
            _items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            SyncForm();
        }

        public static void Clear()
        {
            _items.Clear();
            SyncForm();
        }

        // 复制全部到目标文件夹；与目标重名的自动加 (2)/(3)；已在目标目录里的源文件跳过
        public static int CopyAllTo(string dir)
        {
            int n = 0;
            foreach (var p in _items)
            {
                try
                {
                    if (string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase)) continue;
                    string dst = Path.Combine(dir, Path.GetFileName(p));
                    string baseName = Path.GetFileNameWithoutExtension(dst), ext = Path.GetExtension(dst);
                    for (int k = 2; File.Exists(dst); k++) dst = Path.Combine(dir, baseName + " (" + k + ")" + ext);
                    File.Copy(p, dst);
                    n++;
                }
                catch { }
            }
            return n;
        }

        private static void SyncForm()
        {
            try
            {
                if (_items.Count == 0)
                {
                    if (_form != null) { _form.Close(); _form = null; }
                    return;
                }
                if (_form == null || _form.IsDisposed)
                {
                    _form = new CullTrayForm();
                    _form.Show();
                }
                _form.Reload(_items);
            }
            catch { }
        }
    }

    internal class CullTrayForm : Form
    {
        private readonly FlowLayoutPanel _flow;
        private readonly Label _countLbl;

        public CullTrayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(1, 1);
            DoubleBuffered = true;
            BackColor = Config.BG;
            Padding = new Padding(1);

            var wa = Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(760, wa.Width - 40);
            Location = new Point(wa.Right - Width - 12, wa.Top + 6);

            var clearBtn = new Label
            {
                Text = "✕",
                Width = 28, Height = 34,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Config.FG, BackColor = Config.BG,
                Font = new Font(Config.FontName, 10f),
                Cursor = Cursors.Hand,
                Location = new Point(Width - 30, 2),
            };
            clearBtn.Click += delegate { CullTray.Clear(); };
            Tips.Set(clearBtn, I18n.T("cull_clear_tip"));
            Controls.Add(clearBtn);

            var copyBtn = new Label
            {
                Text = "⧉",
                Width = 28, Height = 34,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Config.FG, BackColor = Config.BG,
                Font = new Font(Config.FontName, 10f),
                Cursor = Cursors.Hand,
                Location = new Point(Width - 58, 2),
            };
            copyBtn.Click += delegate { CopyAll(); };
            Tips.Set(copyBtn, I18n.T("cull_copy_tip"));
            Controls.Add(copyBtn);

            _countLbl = new Label
            {
                Text = "0",
                Width = 34, Height = 34,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Config.FG, BackColor = Config.BG,
                Font = new Font(Config.FontName, 10f, FontStyle.Bold),
                Location = new Point(2, 2),
            };
            Controls.Add(_countLbl);

            _flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false,
                Location = new Point(38, 2),
                Size = new Size(Width - 100, 34),
                BackColor = Config.BG,
            };
            Controls.Add(_flow);

            // 锁定本体尺寸（卡片/按钮都手动定位）
            Size = new Size(Width, 38);
        }

        public void Reload(List<string> items)
        {
            _countLbl.Text = items.Count.ToString();
            _flow.Controls.Clear();
            foreach (var p in items)
            {
                var card = new BorderLabel
                {
                    Text = Path.GetFileName(p),
                    Font = new Font(Config.FontName, 8.5f),
                    Width = 150, Height = 28,
                    Margin = new Padding(1, 2, 1, 2),
                    ForeColor = Config.ListFg,
                    BackColor = Config.ListBg,
                    Cursor = Cursors.Hand,
                };
                Tips.Set(card, p);
                string path = p;
                card.MouseClick += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                    {
                        var sp = card.PointToScreen(new Point(card.Width / 2, card.Height / 2));
                        PopupForm.LoadFilePreview(path, sp);   // 点卡片：原地预览这个文件
                    }
                    else if (e.Button == MouseButtons.Right) CullTray.Remove(path);
                };
                card.MouseMove += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (Math.Abs(e.X - _dragPt.X) + Math.Abs(e.Y - _dragPt.Y) < 4) { _dragPt = Point.Empty; return; }
                    _flow.SuspendLayout();
                    try { card.DoDragDrop(new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move); }
                    catch { }
                    _flow.ResumeLayout();
                };
                card.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) _dragPt = e.Location; };
                _flow.Controls.Add(card);
            }
        }

        private void CopyAll()
        {
            if (CullTray.Count == 0) return;
            using (var dlg = new FolderBrowserDialog { Description = I18n.T("cull_copy_tip"), ShowNewFolderButton = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int n = CullTray.CopyAllTo(dlg.SelectedPath);
                _countLbl.Text = "+" + n;
                var t = new Timer { Interval = 1500 };
                t.Tick += delegate { t.Stop(); t.Dispose(); Reload(CullTray.Snapshot()); };
                t.Start();
            }
        }

        private Point _dragPt = Point.Empty;
    }
}
