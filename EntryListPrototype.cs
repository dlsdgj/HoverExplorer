using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HoverExplorer
{
    internal partial class EntryListForm
    {
        // 原型 v2：搜索 + 可折叠二级分组 + 分组标题预览 + 工具栏/底栏
        private string _query = "";
        private bool _searchFocusPending;
        private bool _historyOpen = true;
        private RoundSearchBox _protoSearch;
        private Panel _protoScroll;
        internal const string GroupDragFmt = "EntryGroupDrag";
        internal class GroupDragData { public FavoriteGroup Group; }
        private readonly List<GroupHeader> _protoHeaders = new List<GroupHeader>();
        internal FavoriteGroup SelectedGroup;
        internal string QueryText { get { return _query; } }

        internal static IEnumerable<FavoriteGroup> EnumAllGroups(IEnumerable<FavoriteGroup> list)
        {
            if (list == null) yield break;
            foreach (var g in list)
            {
                yield return g;
                foreach (var c in EnumAllGroups(g.Children)) yield return c;
            }
        }

        internal static List<string> AllItemPaths(FavoriteGroup g)
        {
            var list = new List<string>(g.Items);
            foreach (var c in g.Children) list.AddRange(AllItemPaths(c));
            return list;
        }

        private bool ItemMatches(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (string.IsNullOrEmpty(_query)) return true;
            return LinkEntry.Display(path).ToLowerInvariant().Contains(_query);
        }

        private bool GroupMatches(FavoriteGroup g)
        {
            if (string.IsNullOrEmpty(_query)) return true;
            if (!string.IsNullOrEmpty(g.Name) && g.Name.ToLowerInvariant().Contains(_query)) return true;
            if (g.Items.Any(ItemMatches)) return true;
            return g.Children.Any(GroupMatches);
        }

        private static void SetOpenRecursive(IEnumerable<FavoriteGroup> list, bool open)
        {
            if (list == null) return;
            foreach (var g in list) { g.Open = open; SetOpenRecursive(g.Children, open); }
        }

        internal static void RemoveFavEverywhere(IEnumerable<FavoriteGroup> list, string path)
        {
            if (list == null) return;
            foreach (var g in list)
            {
                g.Items.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                RemoveFavEverywhere(g.Children, path);
            }
        }

        internal static void RenamePathInGroups(IEnumerable<FavoriteGroup> list, string oldPath, string newPath)
        {
            if (list == null) return;
            foreach (var g in list)
            {
                for (int i = 0; i < g.Items.Count; i++)
                    if (string.Equals(g.Items[i], oldPath, StringComparison.OrdinalIgnoreCase)) g.Items[i] = newPath;
                RenamePathInGroups(g.Children, oldPath, newPath);
            }
        }

        private static bool FindGroupLoc(List<FavoriteGroup> list, FavoriteGroup target,
            out List<FavoriteGroup> owner, out int index, out FavoriteGroup parent, out List<FavoriteGroup> grandOwner)
        {
            owner = null; index = -1; parent = null; grandOwner = null;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == target) { owner = list; index = i; return true; }
                List<FavoriteGroup> o2; int i2; FavoriteGroup p2; List<FavoriteGroup> g2;
                if (FindGroupLoc(list[i].Children, target, out o2, out i2, out p2, out g2))
                {
                    owner = o2; index = i2;
                    parent = p2 ?? list[i];
                    grandOwner = g2 ?? list;
                    return true;
                }
            }
            return false;
        }

        // Tab/Shift+Tab 调整大纲层级；编辑中的名称一并提交
        internal void IndentGroup(FavoriteGroup g, bool outdent, string editedName)
        {
            if (g == null) return;
            if (!string.IsNullOrEmpty(editedName)) g.Name = editedName;
            List<FavoriteGroup> owner; int idx; FavoriteGroup parent; List<FavoriteGroup> grandOwner;
            if (!FindGroupLoc(Config.FavoriteGroups, g, out owner, out idx, out parent, out grandOwner))
            {
                _dirty = true; _fnSave(); _fnBuildRows();
                return;
            }
            if (outdent)
            {
                // 大纲的一级分组 = 当前左侧分组的 Children；不能再反缩进到左侧分组栏
                if (parent == null || grandOwner == null || object.ReferenceEquals(grandOwner, Config.FavoriteGroups)) return;
                owner.RemoveAt(idx);
                int pidx = grandOwner.IndexOf(parent);
                grandOwner.Insert(Math.Max(0, pidx + 1), g);
            }
            else
            {
                if (idx <= 0) return;
                var prev = owner[idx - 1];
                owner.RemoveAt(idx);
                prev.Children.Add(g);
                prev.Open = true;
            }
            _dirty = true; _fnSave(); _fnBuildRows();
        }

        internal static Color BlendColor(Color fg, int percent, Color bg)
        {
            int p = Math.Max(0, Math.Min(100, percent));
            return Color.FromArgb(
                (fg.R * p + bg.R * (100 - p)) / 100,
                (fg.G * p + bg.G * (100 - p)) / 100,
                (fg.B * p + bg.B * (100 - p)) / 100);
        }

        internal static Color KindColor(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".bat" || ext == ".cmd") return ColorTranslator.FromHtml("#6b7b8c");
            if (ext == ".lnk" || ext == ".url") return ColorTranslator.FromHtml("#7a6fd0");
            if (ext == ".exe") return ColorTranslator.FromHtml("#3d8b5a");
            return ColorTranslator.FromHtml("#4a7fc1");
        }

        private int _fnBuildProtoMain(int x, int y, int w, bool isHist, Font font, Font subFont)
        {
            int toolH = Dpi.S(30), footH = Dpi.S(24);
            int listH = Config.EntryListH > 0
                ? Math.Max(Dpi.S(120), Dpi.S(Config.EntryListH) - y - toolH - footH - ListPadBottom)
                : Dpi.S(392);

            var tools = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, toolH - Dpi.S(2)),
                BackColor = Config.ListBg,
            };
            var search = new RoundSearchBox
            {
                Location = new Point(0, Dpi.S(3)),
                Size = new Size(Math.Max(Dpi.S(80), w - Dpi.S(92)), Dpi.S(22)),
                Font = new Font(Config.FontName, Math.Max(7f, Config.FontSize - 0.5f)),
                BackColor = Config.ListBg,
                ForeColor = Config.ListFg,
                Text = _query,
            };
            search.TextChanged += delegate
            {
                _query = (search.Text ?? "").Trim().ToLowerInvariant();
                _searchFocusPending = true;
                BeginInvoke((Action)(() => _fnBuildRows()));
            };
            _protoSearch = search;
            tools.Controls.Add(search);

            Func<string, string, Label> addTool = (text, tip) =>
            {
                var b = new Label
                {
                    Text = text,
                    Size = Dpi.Sz(26, 22),
                    Location = new Point(w - Dpi.S(84) + Math.Max(0, tools.Controls.Count - 1) * Dpi.S(28), Dpi.S(3)),
                    BackColor = Config.ListBg,
                    ForeColor = Config.ListMu,
                    Font = new Font(Config.FontName, Config.FontSize),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Cursor = Cursors.Hand,
                };
                Tips.Set(b, tip);
                return b;
            };
            var foldBtn = addTool("⇱", I18n.T("settings_collapse"));
            foldBtn.Click += delegate { SetOpenRecursive(CurrentGroup().Children, false); CurrentGroup().Open = false; _dirty = true; _fnSave(); _fnBuildRows(); };
            tools.Controls.Add(foldBtn);
            var expBtn = addTool("⇲", I18n.T("settings_expand"));
            expBtn.Click += delegate { SetOpenRecursive(CurrentGroup().Children, true); CurrentGroup().Open = true; _dirty = true; _fnSave(); _fnBuildRows(); };
            tools.Controls.Add(expBtn);
            var addBtn = addTool("＋", I18n.T("add_group"));
            addBtn.Click += delegate { _fnAddSubGroup(CurrentGroup()); };
            tools.Controls.Add(addBtn);
            Controls.Add(tools);
            _rows.Add(tools);

            var scroll = new Panel
            {
                Location = new Point(x, y + toolH),
                Size = new Size(w, listH),
                BackColor = Config.ListBg,
                AutoScroll = true,
            };
            Win11.ApplyScrollTheme(scroll, Config.IsDark);
            _protoScroll = scroll;
            scroll.AllowDrop = !isHist;
            if (!isHist)
            {
                scroll.DragEnter += delegate(object s, DragEventArgs e)
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop))
                        e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                    else if (LinkEntry.HasLinkData(e.Data)) e.Effect = DragDropEffects.Copy;
                };
                scroll.DragOver += delegate(object s, DragEventArgs e)
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop))
                        e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                    else if (LinkEntry.HasLinkData(e.Data)) e.Effect = DragDropEffects.Copy;
                    else e.Effect = DragDropEffects.None;
                };
                scroll.DragDrop += delegate(object s, DragEventArgs e)
                {
                    var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null) _fnAddToCurrentGroup(files);
                    else
                    {
                        string link;
                        if (LinkEntry.TryGetDroppedLink(e.Data, out link)) _fnAddLinkToCurrentGroup(link);
                    }
                };
            }
            Controls.Add(scroll);
            _rows.Add(scroll);
            _protoHeaders.Clear();

            int cy = Dpi.S(4), zebra = 0;
            if (isHist) _fnRenderHistory(scroll, font, subFont, ref cy, ref zebra);
            else _fnRenderRoot(scroll, CurrentGroup(), font, subFont, ref cy, ref zebra);

            if (Config.EntryListH <= 0) listH = Math.Min(Dpi.S(392), Math.Max(Dpi.S(120), cy + Dpi.S(10)));
            scroll.Size = new Size(w, listH);
            scroll.AutoScrollMinSize = new Size(0, cy + Dpi.S(8));

            var foot = new Panel
            {
                Location = new Point(x, y + toolH + listH + Dpi.S(2)),
                Size = new Size(w, footH - Dpi.S(4)),
                BackColor = Config.ListBg,
            };
            // 底栏只保留拖拽提示：分组统计与“新建分组”已移除（分组在工具栏/侧栏操作）
            var hint = new Label
            {
                Text = "支持拖拽链接、浏览器地址到此面板",
                Location = new Point(Dpi.S(2), Dpi.S(2)),
                Size = new Size(Math.Max(Dpi.S(60), w - Dpi.S(4)), Math.Max(Dpi.S(14), footH - Dpi.S(5))),
                Font = new Font(Config.FontName, Math.Max(7f, Config.FontSize - 1f)),
                ForeColor = Config.ListMu, BackColor = Config.ListBg,
                AutoEllipsis = true,
            };
            foot.Controls.Add(hint);
            Controls.Add(foot);
            _rows.Add(foot);

            if (_searchFocusPending && !isHist)
            {
                _searchFocusPending = false;
                BeginInvoke((Action)(() => { if (_protoSearch != null) { _protoSearch.Focus(); _protoSearch.SelectionStart = _protoSearch.Text.Length; } }));
            }
            return y + toolH + listH + footH + ListPadBottom;
        }

        // 顶层不再渲染“与侧边栏同名”的根分组标题：直接列出一级分组（以及根分组下的直接条目）
        private void _fnRenderRoot(Panel host, FavoriteGroup root, Font font, Font subFont, ref int cy, ref int zebra)
        {
            if (root == null) return;
            int idx = 0;
            int width = Math.Max(Dpi.S(120), host.ClientSize.Width - Dpi.S(18));
            foreach (var p in root.Items.Where(ItemMatches))
            {
                var row = _fnMakeRow(p, true, idx++, font, subFont, zebra++, root);
                row.Highlight = _query;
                row.Location = new Point(Dpi.S(16), cy);
                row.Size = new Size(Math.Max(Dpi.S(100), width - Dpi.S(8)), Dpi.S(24));
                host.Controls.Add(row);
                _rows.Add(row);
                cy += Dpi.S(26);
            }
            foreach (var child in root.Children)
                _fnRenderGroup(host, child, root.Children, 0, font, subFont, ref cy, ref zebra, root);
        }

        private void _fnRenderGroup(Panel host, FavoriteGroup g, List<FavoriteGroup> owner, int depth,
            Font font, Font subFont, ref int cy, ref int zebra, FavoriteGroup parentGroup = null)
        {
            if (g == null || !GroupMatches(g)) return;
            bool open = !string.IsNullOrEmpty(_query) || g.Open;
            int indent = Dpi.S(8) + depth * Dpi.S(14);
            int width = Math.Max(Dpi.S(120), host.ClientSize.Width - indent - Dpi.S(10));

            var header = new GroupHeader(this, g, owner, false)
            {
                Depth = depth,
                ParentGroup = parentGroup,
                Selected = object.ReferenceEquals(g, SelectedGroup),
                Location = new Point(indent, cy),
                Size = new Size(width, Dpi.S(28)),
            };
            host.Controls.Add(header);
            _protoHeaders.Add(header);
            cy += Dpi.S(30);
            if (!open) return;

            int idx = 0;
            foreach (var p in g.Items.Where(ItemMatches))
            {
                var row = _fnMakeRow(p, true, idx++, font, subFont, zebra++, g);
                row.Highlight = _query;
                row.Location = new Point(indent + Dpi.S(8), cy);
                row.Size = new Size(Math.Max(Dpi.S(100), width - Dpi.S(8)), Dpi.S(24));
                host.Controls.Add(row);
                _rows.Add(row);
                cy += Dpi.S(26);
            }
            foreach (var child in g.Children)
                _fnRenderGroup(host, child, g.Children, depth + 1, font, subFont, ref cy, ref zebra, g);
        }

        private void _fnRenderHistory(Panel host, Font font, Font subFont, ref int cy, ref int zebra)
        {
            int width = Math.Max(Dpi.S(120), host.ClientSize.Width - Dpi.S(18));
            var header = new GroupHeader(this, null, null, true)
            {
                Location = new Point(Dpi.S(8), cy),
                Size = new Size(width, Dpi.S(28)),
            };
            host.Controls.Add(header);
            _protoHeaders.Add(header);
            cy += Dpi.S(30);
            if (!_historyOpen) return;
            int idx = 0;
            foreach (var p in Config.RecentFolders.Where(LinkEntry.IsEntryValid).Where(ItemMatches))
            {
                var row = _fnMakeHistRow(p, idx, idx, font, subFont, zebra++);
                row.Highlight = _query;
                row.Location = new Point(Dpi.S(16), cy);
                row.Size = new Size(Math.Max(Dpi.S(100), width - Dpi.S(8)), Dpi.S(24));
                host.Controls.Add(row);
                _rows.Add(row);
                cy += Dpi.S(26);
            }
        }

        internal void SetGroupName(FavoriteGroup g, string name)
        {
            if (g == null || string.IsNullOrEmpty(name)) return;
            g.Name = name;
            _dirty = true; _fnSave(); _fnBuildRows();
        }

        internal void SelectGroupHeader(GroupHeader h)
        {
            SelectedGroup = (h == null || h.Virtual) ? null : h.Group;
            foreach (var x in _protoHeaders) { x.Selected = false; x.Invalidate(); }
            if (h != null && !h.Virtual) { h.Selected = true; h.Focus(); h.Invalidate(); }
        }

        // 只重建右侧大纲区，避免展开/折叠时把侧栏、工具栏、标题栏一起刷掉
        internal void RefreshGroupList()
        {
            if (_protoScroll == null) return;
            int top = _protoScroll.VerticalScroll.Value;
            foreach (var c in _protoScroll.Controls.Cast<Control>().ToList()) c.Dispose();
            _rows.RemoveAll(r => r == null || r.IsDisposed || object.ReferenceEquals(r.Parent, _protoScroll));
            _protoHeaders.Clear();
            var font = new Font(Config.FontName, Config.FontSize);
            var subFont = new Font(Config.FontName, Math.Max(7f, Config.FontSize - 0.5f));
            int cy = Dpi.S(4), zebra = 0;
            if (_curG < 0) _fnRenderHistory(_protoScroll, font, subFont, ref cy, ref zebra);
            else _fnRenderRoot(_protoScroll, CurrentGroup(), font, subFont, ref cy, ref zebra);
            _protoScroll.AutoScrollMinSize = new Size(0, cy + Dpi.S(8));
            try { _protoScroll.VerticalScroll.Value = Math.Min(top, Math.Max(0, _protoScroll.AutoScrollMinSize.Height - _protoScroll.Height)); } catch { }
        }

        internal void ToggleGroup(FavoriteGroup g)
        {
            if (g == null || !string.IsNullOrEmpty(_query)) return;
            g.Open = !g.Open;
            _dirty = true; _fnSave(); RefreshGroupList();
        }

        internal void ToggleHistory()
        {
            _historyOpen = !_historyOpen;
            _fnBuildRows();
        }

        private void _fnAddSubGroup(FavoriteGroup parent)
        {
            if (parent == null) return;
            string name = PromptDialog.Show(I18n.T("group_name"), I18n.T("add_group"));
            if (string.IsNullOrEmpty(name)) return;
            var colors = new[] { "#4a7fc1", "#7a6fd0", "#c4567a", "#d9961a", "#3d8b5a", "#2fa3a3" };
            parent.Children.Add(new FavoriteGroup(name, colors[parent.Children.Count % colors.Length]));
            parent.Open = true;
            _dirty = true; _fnSave(); _fnBuildRows();
        }

        internal void OpenGroupMenu(GroupHeader h)
        {
            if (h == null) return;
            if (h.Virtual)
            {
                var hm = new ContextMenuStrip { ShowImageMargin = false, BackColor = Config.BG, ForeColor = Config.FG };
                hm.Items.Add(_historyOpen ? I18n.T("fold_list") : I18n.T("settings_expand"), null, delegate { ToggleHistory(); });
                hm.Show(Cursor.Position);
                return;
            }
            var g = h.Group;
            if (g == null) return;
            var m = new ContextMenuStrip { ShowImageMargin = false, BackColor = Config.BG, ForeColor = Config.FG,
                Font = new Font(Config.FontName, Config.FontSize) };
            m.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            m.Items.Add(I18n.T("add_group"), null, delegate { _fnAddSubGroup(g); });
            m.Items.Add(I18n.T("rename"), null, delegate
            {
                string nn = PromptDialog.Show(I18n.T("group_name"), g.Name);
                if (!string.IsNullOrEmpty(nn)) { g.Name = nn; _dirty = true; _fnSave(); _fnBuildRows(); }
            });
            m.Items.Add(I18n.T("group_sort"), null, delegate
            {
                g.Items.Sort((a, b) => string.Compare(LinkEntry.Display(a), LinkEntry.Display(b), StringComparison.OrdinalIgnoreCase));
                _dirty = true; _fnSave(); _fnBuildRows();
            });
            m.Items.Add(I18n.T("group_color"), null, delegate
            {
                using (var cd = new ColorDialog { Color = ColorTranslator.FromHtml(g.Color) })
                    if (cd.ShowDialog() == DialogResult.OK)
                    {
                        g.Color = "#" + cd.Color.R.ToString("x2") + cd.Color.G.ToString("x2") + cd.Color.B.ToString("x2");
                        _dirty = true; _fnSave(); _fnBuildRows();
                    }
            });
            if (h.Owner != null)
                m.Items.Add(I18n.T("group_remove"), null, delegate
                {
                    h.Owner.Remove(g);
                    if (h.ParentGroup != null)
                    {
                        h.ParentGroup.Items.AddRange(g.Items);
                        h.ParentGroup.Children.AddRange(g.Children);
                    }
                    _dirty = true; _fnSave(); _fnBuildRows();
                });
            m.Show(Cursor.Position);
        }

        private static bool IsDescendant(FavoriteGroup root, FavoriteGroup node)
        {
            if (root == null || node == null) return false;
            foreach (var c in root.Children)
                if (object.ReferenceEquals(c, node) || IsDescendant(c, node)) return true;
            return false;
        }

        // 分组拖动：zone 0=插到目标前，1=成为目标子分组，2=插到目标后
        private void MoveGroup(FavoriteGroup target, FavoriteGroup dragged, int zone)
        {
            if (target == null || dragged == null || object.ReferenceEquals(target, dragged)) return;
            if (IsDescendant(dragged, target)) return;
            List<FavoriteGroup> owner; int idx; FavoriteGroup parent; List<FavoriteGroup> grandOwner;
            if (!FindGroupLoc(Config.FavoriteGroups, dragged, out owner, out idx, out parent, out grandOwner)) return;
            owner.RemoveAt(idx);
            if (zone == 1)
            {
                target.Children.Add(dragged);
                target.Open = true;
            }
            else
            {
                List<FavoriteGroup> toOwner; int ti; FavoriteGroup tp; List<FavoriteGroup> tg;
                if (!FindGroupLoc(Config.FavoriteGroups, target, out toOwner, out ti, out tp, out tg)) return;
                toOwner.Insert(zone == 0 ? Math.Max(0, ti) : Math.Min(toOwner.Count, ti + 1), dragged);
            }
            SelectedGroup = dragged;
            _dirty = true; _fnSave(); RefreshGroupList();
        }

        internal void DragEnterGroup(GroupHeader h, DragEventArgs e)
        {
            if (h == null || e == null) return;
            if (e.Data.GetDataPresent(GroupDragFmt) || e.Data.GetDataPresent(DragFmtRow)) e.Effect = DragDropEffects.Move;
            else if (!h.Virtual && e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        }

        internal void DragOverGroup(GroupHeader h, DragEventArgs e, int zone)
        {
            if (h == null || e == null) return;
            foreach (var x in _protoHeaders) { x.DragOverState = false; x.DropZone = -1; }
            bool group = e.Data.GetDataPresent(GroupDragFmt);
            bool row = e.Data.GetDataPresent(DragFmtRow);
            if (group) { h.DropZone = zone; e.Effect = DragDropEffects.Move; }
            else if (row) { h.DragOverState = true; e.Effect = DragDropEffects.Move; }
            else if (!h.Virtual && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // 外部文件拖到分组标题 = 归入该分组（支持子分组）
                h.DragOverState = true;
                e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
            }
            else e.Effect = DragDropEffects.None;
        }

        internal void DragLeaveGroup(GroupHeader h)
        {
            if (h != null) { h.DragOverState = false; h.DropZone = -1; }
        }

        internal void DropOnGroup(GroupHeader h, DragEventArgs e, int zone)
        {
            if (h == null || e == null) return;
            foreach (var x in _protoHeaders) { x.DragOverState = false; x.DropZone = -1; }
            var gd = e.Data.GetData(GroupDragFmt) as GroupDragData;
            if (gd != null && gd.Group != null && !h.Virtual) { MoveGroup(h.Group, gd.Group, zone); return; }
            if (h.Virtual) return;
            // 外部文件拖到分组标题（含子分组）→ 直接归入该分组
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) { AddFilesToGroup(h.Group, files); return; }
            var rd = e.Data.GetData(DragFmtRow) as RowDragData;
            if (rd == null || rd.Paths.Count == 0) return;
            RemoveDraggedSources(rd);
            int at = h.Group.Items.Count;
            foreach (var p in rd.Paths)
                if (!h.Group.Items.Contains(p, StringComparer.OrdinalIgnoreCase)) h.Group.Items.Insert(at++, p);
            h.Group.Open = true;
            _fnClearSelection();
            _dirty = true; _fnSave(); _fnBuildRows();
        }

        internal static void RemoveDraggedSources(RowDragData rd)
        {
            if (rd == null) return;
            foreach (var p in rd.Paths)
            {
                if (rd.FromGroup != null) rd.FromGroup.Items.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
                else if (rd.FromG.HasValue && rd.FromG.Value < Config.FavoriteGroups.Count)
                    Config.FavoriteGroups[rd.FromG.Value].Items.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
                else if (rd.FromH.HasValue)
                    Config.RecentFolders.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    // 原型式分组标题：箭头 + 色点 + 名称 + 前 5 项图标预览 + 计数 + ⋯ 菜单
    internal class GroupHeader : Control
    {
        private readonly EntryListForm _form;
        private bool _hover;
        private readonly Font _titleFont;
        private Point? _dragStart;

        internal FavoriteGroup Group;
        internal List<FavoriteGroup> Owner;
        internal FavoriteGroup ParentGroup;
        internal int Depth;
        internal bool Virtual;
        internal bool DragOverState;
        internal bool Selected;
        internal int DropZone = -1;   // 0=前 1=内 2=后

        internal GroupHeader(EntryListForm form, FavoriteGroup group, List<FavoriteGroup> owner, bool virtualHeader)
        {
            _form = form;
            Group = group;
            Owner = owner;
            Virtual = virtualHeader;
            _titleFont = Config.ListTitleFont();   // 跟随"列表外观·标题字体"设置，不再硬编码
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            AllowDrop = true;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _titleFont != null) _titleFont.Dispose();
            base.Dispose(disposing);
        }

        private Rectangle MenuRect { get { return new Rectangle(Width - Dpi.S(24), Dpi.S(4), Dpi.S(18), Dpi.S(20)); } }
        private Rectangle ArrowRect { get { return new Rectangle(Dpi.S(4), Dpi.S(3), Dpi.S(16), Height - Dpi.S(6)); } }
        private Rectangle NameRect { get { return new Rectangle(Dpi.S(32), Dpi.S(2), Math.Max(Dpi.S(20), Width - Dpi.S(58)), Height - Dpi.S(4)); } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_hover) { _hover = true; Invalidate(); }
            if (!_dragStart.HasValue) return;
            if (Math.Abs(e.X - _dragStart.Value.X) + Math.Abs(e.Y - _dragStart.Value.Y) < 4) return;
            _dragStart = null;
            var data = new DataObject();
            data.SetData(EntryListForm.GroupDragFmt, new EntryListForm.GroupDragData { Group = Group });
            try { DoDragDrop(data, DragDropEffects.Move); } catch { }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover) { _hover = false; Invalidate(); }
            _form.DragLeaveGroup(this);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var pt = PointToClient(Cursor.Position);
            if (MenuRect.Contains(pt)) { _form.OpenGroupMenu(this); return; }
            if (Virtual) { _form.ToggleHistory(); return; }
            if (ArrowRect.Contains(pt)) { _form.ToggleGroup(Group); return; }
            _form.SelectGroupHeader(this);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Tab) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Tab && !Virtual && Group != null && !e.Handled)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                _form.IndentGroup(Group, e.Shift, null);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _dragStart = null;
            if (e.Button != MouseButtons.Left || Virtual || Group == null) return;
            if (object.ReferenceEquals(Group, _form.CurrentGroup())) return;
            if (MenuRect.Contains(e.Location) || ArrowRect.Contains(e.Location)) return;
            _dragStart = e.Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragStart = null;
        }

        private int ZoneAt(Point pt)
        {
            if (pt.Y < Height * 0.30) return 0;
            if (pt.Y > Height * 0.70) return 2;
            return 1;
        }

        protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); _form.DragEnterGroup(this, e); }
        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            _form.DragOverGroup(this, e, ZoneAt(PointToClient(new Point(e.X, e.Y))));
        }
        protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); _form.DragLeaveGroup(this); }
        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            _form.DropOnGroup(this, e, ZoneAt(PointToClient(new Point(e.X, e.Y))));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color color = Virtual ? Config.Accent : ColorTranslator.FromHtml(Group.Color);
            // 原型：顶层分组标题 = 分组色 15% 混入面板底；子分组 = 行底色
            Color normalBg = Depth == 0 && !Virtual ? EntryListForm.BlendColor(color, 15, Config.ListBg) : Config.ListBg;
            Color bg = DragOverState ? Config.ListDrop : (_hover ? Config.ListHv : normalBg);
            var rowRect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = RowLabel.RoundRect(rowRect, 5f))
            using (var b = new SolidBrush(bg)) g.FillPath(b, path);
            if (Selected)
                using (var path = RowLabel.RoundRect(rowRect, 5f))
                using (var p = new Pen(Config.Accent, 2)) g.DrawPath(p, path);
            if (DragOverState)
                using (var path = RowLabel.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5, Height - 5), 4f))
                using (var p = new Pen(color, 2)) g.DrawPath(p, path);
            if (DropZone == 0)
                using (var p = new Pen(color, 3)) g.DrawLine(p, 2, 1, Width - 2, 1);
            else if (DropZone == 2)
                using (var p = new Pen(color, 3)) g.DrawLine(p, 2, Height - 2, Width - 2, Height - 2);
            else if (DropZone == 1)
                using (var path = RowLabel.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5, Height - 5), 4f))
                using (var p = new Pen(color, 2)) { p.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash; g.DrawPath(p, path); }

            bool open = Virtual || !string.IsNullOrEmpty(_form.QueryText) || Group.Open;
            string arrow = open ? "▼" : "▶";
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b = new SolidBrush(Config.ListMu))
                g.DrawString(arrow, Font, b, ArrowRect, sf);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, Dpi.S(22), (Height - Dpi.S(8)) / 2f, Dpi.S(8), Dpi.S(8));

            string name = Virtual ? ("🕘 " + I18n.T("hist_bar")) : Group.Name;
            string shown = RowLabel.MidEllipsis(g, name, _titleFont, NameRect.Width);
            using (var b = new SolidBrush(Config.ListFg))
                g.DrawString(shown, _titleFont, b, NameRect.X, (Height - _titleFont.Height) / 2f);

            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b = new SolidBrush(Config.ListFg))
                g.DrawString("⋯", _titleFont, b, MenuRect, sf);
        }
    }

    // 圆角搜索框：外层自绘圆角边框，内层 TextBox 无边框，避免 WinForms TextBox 的直角外观
    internal class RoundSearchBox : Control
    {
        private readonly TextBox _inner;

        public RoundSearchBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            _inner = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Location = new Point(Dpi.S(24), Dpi.S(2)),
                Size = new Size(Dpi.S(80), Dpi.S(18)),
            };
            _inner.TextChanged += delegate { EventHandler h = TextChanged; if (h != null) h(this, EventArgs.Empty); };
            Controls.Add(_inner);
        }

        public new event EventHandler TextChanged;

        public new string Text
        {
            get { return _inner.Text; }
            set { _inner.Text = value ?? ""; }
        }

        public int SelectionStart
        {
            get { return _inner.SelectionStart; }
            set { _inner.SelectionStart = value; }
        }

        public new void Focus() { _inner.Focus(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _inner.Location = new Point(Dpi.S(24), Math.Max(Dpi.S(1), (Height - Dpi.S(18)) / 2));
            _inner.Size = new Size(Math.Max(Dpi.S(20), Width - Dpi.S(30)), Dpi.S(18));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = RowLabel.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), (Height - 1) / 2f))
            {
                using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
                using (var p = new Pen(Config.ListBd, 1f)) g.DrawPath(p, path);
            }
            _inner.BackColor = BackColor;
            _inner.ForeColor = ForeColor;
            _inner.Font = Font;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var b = new SolidBrush(Config.ListMu))
                g.DrawString("⌕", Font, b, Dpi.S(7), (Height - Font.Height) / 2f);
        }
    }
}
