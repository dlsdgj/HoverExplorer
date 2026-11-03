using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using Rect = System.Windows.Rect;

namespace HoverExplorer
{
    internal static class ExplorerUia
    {
        private static string _lastDetectLog;
        private static void LogDetect(string state, string detail)
        {
            string key = state + "|" + detail;
            if (key == _lastDetectLog) return;
            _lastDetectLog = key;
            Program.DebugLog("[detect] " + state + (string.IsNullOrEmpty(detail) ? "" : " | " + detail));
        }

        private static string SafeCls(AutomationElement el)
        {
            try { return el.Current.ClassName; } catch { return "?"; }
        }

        private static string SafeName(AutomationElement el)
        {
            try { return el.Current.Name; } catch { return null; }
        }

        private static AutomationElement FindNameCellByName(AutomationElement item, string name, int depth)
        {
            if (depth > 4 || string.IsNullOrEmpty(name)) return null;
            var walker = TreeWalker.RawViewWalker;
            var child = walker.GetFirstChild(item);
            while (child != null)
            {
                string n = SafeName(child);
                if (n == name) return child;
                var sub = FindNameCellByName(child, name, depth + 1);
                if (sub != null) return sub;
                child = walker.GetNextSibling(child);
            }
            return null;
        }

        // 递归查找 Text/Edit 子元素，取"右边界最大但仍明显小于单元格右边界"的那个
        private static void FindTextChild(AutomationElement el, double cellRight, int depth, ref double best)
        {
            if (depth > 3) return;
            var walker = TreeWalker.RawViewWalker;
            AutomationElement child;
            try { child = walker.GetFirstChild(el); } catch { return; }
            while (child != null)
            {
                try
                {
                    var ct = child.Current.ControlType;
                    if (ct == ControlType.Text || ct == ControlType.Edit)
                    {
                        var r = child.Current.BoundingRectangle;
                        if (r.Width > 0 && r.Height > 0 && r.Right < cellRight - 1 && r.Right > best)
                            best = r.Right;
                    }
                }
                catch { }
                FindTextChild(child, cellRight, depth + 1, ref best);
                AutomationElement next;
                try { next = walker.GetNextSibling(child); } catch { next = null; }
                child = next;
            }
        }

        private static bool TryGetNameTextRect(AutomationElement cell, out double tLeft, out double tRight)
        {
            tLeft = tRight = 0;
            Rect cr;
            try { cr = cell.Current.BoundingRectangle; } catch { return false; }
            if (cr.Width <= 0) return false;

            // 1) TextPattern：直接取文本实际占用的像素矩形（比元素矩形精确）
            try
            {
                object pat;
                if (cell.TryGetCurrentPattern(TextPattern.Pattern, out pat) && pat != null)
                {
                    var rects = ((TextPattern)pat).DocumentRange.GetBoundingRectangles();
                    if (rects != null && rects.Length > 0)
                    {
                        double l = double.MaxValue, r = double.MinValue;
                        foreach (var rr in rects)
                        {
                            if (rr.Width <= 0 || rr.Height <= 0) continue;
                            if (rr.Left < l) l = rr.Left;
                            if (rr.Right > r) r = rr.Right;
                        }
                        if (r > l && r < cr.Right - 1)
                        { tLeft = l; tRight = r; return true; }
                    }
                }
            }
            catch { }

            // 2) 递归找 Text/Edit 子元素
            double best = double.MinValue;
            FindTextChild(cell, cr.Right, 0, ref best);
            if (best > double.MinValue)
            { tLeft = cr.Left; tRight = best; return true; }
            return false;
        }

        private static readonly Dictionary<string, double> _textWidthCache = new Dictionary<string, double>();

        // 拿不到真实文字矩形时的兜底：按行高推算字号并用字体测量估算"图标+文件名"的右边界
        // 宁可略宽（避免误杀正常触发），也不能整列/整行放行
        internal static double EstimateNameRight(double left, double rowHeight, string name)
        {
            if (string.IsNullOrEmpty(name)) return left;
            int px = (int)Math.Round(Math.Min(18, Math.Max(11, rowHeight * 0.60)));
            string key = px + "|" + name;
            double w;
            lock (_textWidthCache)
            {
                if (_textWidthCache.TryGetValue(key, out w)) return left + w;
            }
            w = -1;
            try
            {
                using (var font = new Font("Segoe UI", px, GraphicsUnit.Pixel))
                using (var bmp = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(bmp))
                {
                    var sz = g.MeasureString(name, font, 2000, StringFormat.GenericTypographic);
                    double iconW = Math.Min(24, Math.Max(16, rowHeight * 0.7));
                    w = iconW + 6 + sz.Width + 10;
                }
            }
            catch { }
            if (w <= 0) return left;
            lock (_textWidthCache)
            {
                if (_textWidthCache.Count > 2000) _textWidthCache.Clear();
                _textWidthCache[key] = w;
            }
            return left + w;
        }

        private static AutomationElement FindItemsViewWalk(AutomationElement root)
        {
            var walker = TreeWalker.RawViewWalker;
            var stack = new Stack<AutomationElement>();
            stack.Push(root);
            int count = 0;
            while (stack.Count > 0 && count < 500)
            {
                count++;
                var el = stack.Pop();
                try { if (el.Current.ClassName == "UIItemsView") return el; } catch { }
                try
                {
                    var child = walker.GetFirstChild(el);
                    while (child != null) { stack.Push(child); child = walker.GetNextSibling(child); }
                }
                catch { }
            }
            return null;
        }

        private static IntPtr _cachedHeaderHwnd;
        private static double _cachedHeaderLeft = double.NaN, _cachedHeaderRight;
        private static double _cachedHeaderTop, _cachedHeaderBottom;
        // 表头位置按"相对窗口左上角"的偏移缓存：窗口移动不会失效，只有宽度（列布局）变化才重查
        private static double _hdrLeftOff = double.NaN, _hdrRightOff, _hdrBottomOff;
        private static double _hdrWinW = double.NaN;
        private static double _curWinX, _curWinY, _curWinW = double.NaN;
        private static DateTime _headerCacheTime;
        private static DateTime _winRectTime;

        // 窗口矩形（500ms 内复用），用于把表头偏移换算成屏幕坐标，并检测列布局变化
        private static bool TryGetWindowRect(IntPtr hwnd)
        {
            if (!double.IsNaN(_curWinW) && (DateTime.UtcNow - _winRectTime).TotalMilliseconds < 500)
                return true;
            try
            {
                var el = AutomationElement.FromHandle(hwnd);
                if (el == null) return false;
                var r = el.Current.BoundingRectangle;
                if (r.Width <= 0 || r.Height <= 0) return false;
                _curWinX = r.X; _curWinY = r.Y; _curWinW = r.Width;
                _winRectTime = DateTime.UtcNow;
                return true;
            }
            catch { return false; }
        }

        private static bool CheckHeaderCandidate(AutomationElement e, double minItemY,
            out double l, out double r, out double t, out double b)
        {
            l = r = t = b = 0;
            try
            {
                if (e.Current.IsOffscreen) return false;
                string n = e.Current.Name;
                if (n != "名称" && n != "Name") return false;
                var hr = e.Current.BoundingRectangle;
                if (hr.Width <= 10 || hr.Height <= 0) return false;
                if (hr.Y >= minItemY - 2) return false;
                // 离首行太远 -> 多半是菜单/工具栏里的同名项，不是列头
                if (minItemY - hr.Bottom > 240) return false;
                l = hr.X; r = hr.Right; t = hr.Y; b = hr.Bottom;
                return true;
            }
            catch { return false; }
        }

        // BFS：列头比列表条目浅，先浅后深可稳定命中，不再受条目数量影响
        private static bool BfsFindHeader(AutomationElement root, double minItemY,
            out double l, out double r, out double t, out double b)
        {
            l = r = t = b = 0;
            var walker = TreeWalker.RawViewWalker;
            var q = new Queue<AutomationElement>();
            q.Enqueue(root);
            int count = 0;
            while (q.Count > 0 && count < 4000)
            {
                count++;
                var el = q.Dequeue();
                double cl, cr, ct, cb;
                if (CheckHeaderCandidate(el, minItemY, out cl, out cr, out ct, out cb))
                { l = cl; r = cr; t = ct; b = cb; return true; }
                try
                {
                    var child = walker.GetFirstChild(el);
                    while (child != null) { q.Enqueue(child); child = walker.GetNextSibling(child); }
                }
                catch { }
            }
            return false;
        }

        private static void SetHeaderCache(IntPtr hwnd, double l, double r, double t, double b)
        {
            _cachedHeaderHwnd = hwnd;
            _hdrLeftOff = l - _curWinX;
            _hdrRightOff = r - _curWinX;
            _hdrBottomOff = b - _curWinY;
            _hdrWinW = _curWinW;
            _headerCacheTime = DateTime.UtcNow;
            _cachedHeaderLeft = l; _cachedHeaderRight = r; _cachedHeaderTop = t; _cachedHeaderBottom = b;
        }

        private static bool FindNameHeader(IntPtr explorerHwnd, double minItemY)
        {
            double l = 0, r = 0, t = 0, b = 0;
            bool found = false;
            try
            {
                var root = AutomationElement.FromHandle(explorerHwnd);
                if (root != null)
                {
                    // 策略1：按控制类型找列头（Header / HeaderItem），命中数少、开销小
                    try
                    {
                        var cond = new OrCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Header),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.HeaderItem));
                        foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
                        {
                            double cl, cr, ct, cb;
                            if (CheckHeaderCandidate(e, minItemY, out cl, out cr, out ct, out cb))
                            { l = cl; r = cr; t = ct; b = cb; found = true; break; }
                        }
                    }
                    catch { }

                    // 策略2：按名称精确查找（列头未暴露成 HeaderItem 时兜底）
                    if (!found)
                    {
                        try
                        {
                            var cond = new OrCondition(
                                new PropertyCondition(AutomationElement.NameProperty, "名称"),
                                new PropertyCondition(AutomationElement.NameProperty, "Name"));
                            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
                            {
                                double cl, cr, ct, cb;
                                if (CheckHeaderCandidate(e, minItemY, out cl, out cr, out ct, out cb))
                                { l = cl; r = cr; t = ct; b = cb; found = true; break; }
                            }
                        }
                        catch { }
                    }

                    // 策略3：BFS 全树兜底
                    if (!found)
                    {
                        double cl, cr, ct, cb;
                        if (BfsFindHeader(root, minItemY, out cl, out cr, out ct, out cb))
                        { l = cl; r = cr; t = ct; b = cb; found = true; }
                    }
                }
            }
            catch { }

            if (!found) return false;
            SetHeaderCache(explorerHwnd, l, r, t, b);
            LogDetect("header-found", string.Format("y={0:F0} bottom={1:F0} minItemY={2:F0} w={3:F0}", t, b, minItemY, r - l));
            return b < minItemY;
        }

        private static bool HasNameHeader(IntPtr explorerHwnd, double minItemY)
        {
            if (!TryGetWindowRect(explorerHwnd)) return false;
            if (explorerHwnd == _cachedHeaderHwnd && !double.IsNaN(_hdrLeftOff)
                && (DateTime.UtcNow - _headerCacheTime).TotalSeconds < 10
                && !double.IsNaN(_hdrWinW) && Math.Abs(_curWinW - _hdrWinW) < 2)
            {
                _cachedHeaderLeft = _curWinX + _hdrLeftOff;
                _cachedHeaderRight = _curWinX + _hdrRightOff;
                _cachedHeaderBottom = _curWinY + _hdrBottomOff;
                return _cachedHeaderBottom < minItemY;
            }
            return FindNameHeader(explorerHwnd, minItemY);
        }

        private static bool IsDetailsView(IntPtr explorerHwnd, Rect itemRect)
        {
            if (!HasNameHeader(explorerHwnd, itemRect.Y)) return false;
            if (explorerHwnd != _cachedHeaderHwnd || double.IsNaN(_cachedHeaderLeft)) return false;
            if (_cachedHeaderBottom > itemRect.Y + 1) return false;
            if (_cachedHeaderRight <= itemRect.X || _cachedHeaderLeft >= itemRect.Right) return false;
            return true;
        }

        private static bool FindNameArea(IntPtr explorerHwnd, AutomationElement item, bool detailsView,
            Rect full, out double nameLeft, out double nameRight, out double nameTop, out double nameHeight)
        {
            nameLeft = full.X; nameRight = full.Right; nameTop = full.Y; nameHeight = full.Height;

            if (detailsView)
            {
                try
                {
                    var cell = FindNameCellByName(item, SafeName(item), 0);
                    if (cell != null)
                    {
                        var nr = cell.Current.BoundingRectangle;
                        if (nr.Width > 0 && nr.Height > 0 && nr.Right <= full.Right + 2 && nr.Left >= full.X - 2)
                        {
                            nameLeft = nr.Left;
                            nameRight = nr.Right;
                            nameTop = full.Y;
                            nameHeight = full.Height;
                            return true;
                        }
                    }
                }
                catch { }

                try
                {
                    double colLeft = double.NaN, colRight = 0;
                    if (explorerHwnd == _cachedHeaderHwnd && !double.IsNaN(_cachedHeaderLeft)
                        && (DateTime.UtcNow - _headerCacheTime).TotalSeconds < 3)
                    {
                        colLeft = _cachedHeaderLeft; colRight = _cachedHeaderRight;
                    }
                    if (!double.IsNaN(colLeft))
                    {
                        double left = Math.Max(full.X, colLeft);
                        double right = Math.Min(full.Right, colRight - 1);
                        if (right > left + 40)
                        {
                            nameLeft = left; nameRight = right;
                            return true;
                        }
                    }
                }
                catch { }
            }

            return false;
        }

        public static string DetectHoverItemCore(int mx, int my, IntPtr explorerHwnd, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            var apt = new System.Windows.Point(mx, my);
            AutomationElement item = null;
            LogDetect("step:enter-uia", null);

            try
            {
                AutomationElement hitEl = AutomationElement.FromPoint(apt);
                LogDetect("step:frompoint-done", hitEl == null ? "null" : "ok");
                if (hitEl != null)
                {
                    string _hitCt = "?", _hitCls = "?";
                    try { _hitCt = hitEl.Current.ControlType.ProgrammaticName; } catch { }
                    try { _hitCls = hitEl.Current.ClassName; } catch { }
                    LogDetect("frompoint-el", "ct=" + _hitCt + " cls=" + _hitCls);
                    var walker = TreeWalker.RawViewWalker;
                    var c = hitEl;
                    int depth = 0;
                    while (c != null && depth < 30)
                    {
                        string cls = SafeCls(c);
                        if (cls == "CabinetWClass") break;
                        try
                        {
                            var ct = c.Current.ControlType;
                            if (ct == ControlType.ListItem || ct == ControlType.DataItem)
                            { item = c; LogDetect("m1-hit", ct.ProgrammaticName); break; }
                        }
                        catch { }
                        c = walker.GetParent(c);
                        depth++;
                    }
                }
            }
            catch { }

            if (item == null)
            {
                LogDetect("step:m2-start", null);
                AutomationElement bestItem = null;
                int bestDist = int.MaxValue;
                try
                {
                    var root = AutomationElement.FromHandle(explorerHwnd);
                    if (root != null)
                    {
                        var cond = new OrCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
                        var items = root.FindAll(TreeScope.Descendants, cond);
                        LogDetect("step:m2-found", items.Count + " items");
                        if (items.Count == 0)
                        {
                            var container = FindItemsViewWalk(root);
                            if (container != null)
                            {
                                items = container.FindAll(TreeScope.Descendants, cond);
                                LogDetect("step:m2-container", items.Count + " items");
                            }
                        }
                        // 图标/缩略图 tile 之间的留白用精确矩形必 miss（no-item），
                        // 对近方形 tile 给 8px 容差取最近命中；宽行（详细视图）保持精确，
                        // 避免吃到滚动条/右侧空白
                        for (int i = 0; i < items.Count; i++)
                        {
                            var it = items[i];
                            try
                            {
                                var r = it.Current.BoundingRectangle;
                                double top = r.Y;
                                if (r.Height < 32) top = r.Y - 48;
                                int tol = (r.Width <= (r.Bottom - r.Y) * 3 && (r.Bottom - r.Y) <= r.Width * 3) ? 8 : 0;
                                int dx = mx < r.X ? (int)(r.X - mx) : (mx > r.Right ? (int)(mx - r.Right) : 0);
                                int dy = Math.Max(my < top ? (int)(top - my) : 0, my > r.Bottom ? (int)(my - r.Bottom) : 0);
                                if (dx <= tol && dy <= tol && dx + dy < bestDist)
                                {
                                    bestDist = dx + dy;
                                    bestItem = it;
                                    if (dx == 0 && dy == 0) break;   // 精确命中优先，立即返回
                                }
                            }
                            catch { }
                        }
                        if (bestItem != null)
                        {
                            item = bestItem;
                            LogDetect("m2-hit", item.Current.ControlType.ProgrammaticName
                                + (bestDist > 0 ? " (gap-tol " + bestDist + ")" : ""));
                        }
                    }
                }
                catch { }
            }
            if (item == null)
            {
                LogDetect("no-item", "mouse=" + mx + "," + my);
                return null;
            }

            string name = SafeName(item);

            Rect full;
            try { full = item.Current.BoundingRectangle; }
            catch { return null; }

            bool detailsView = false;
            double nameLeft = full.X, nameRight = full.Right, nameTop = full.Y, nameHeight = full.Height;

            try
            {
                var cell = FindNameCellByName(item, name, 0);
                if (cell != null)
                {
                    var cr = cell.Current.BoundingRectangle;
                    if (cr.Width > 0 && cr.Height > 0 && cr.Width < full.Width * 0.8)
                    {
                        detailsView = true;
                        nameLeft = cr.Left;
                        nameRight = cr.Right;
                        nameTop = full.Y;
                        nameHeight = full.Height;
                        // 仅图标/名称触发：固定开启
                        double tL, tR;
                        if (TryGetNameTextRect(cell, out tL, out tR) && tR > tL && tR < cr.Right)
                        {
                            nameRight = tR;
                            LogDetect("namecell", string.Format("cellW={0:F0} textR={1:F0} fullW={2:F0} ratio={3:F2}", cr.Width, tR, full.Width, cr.Width / full.Width));
                        }
                        else
                        {
                            // 拿不到文字矩形时估算，避免退回整列宽（名称右侧空白也触发）
                            double est = EstimateNameRight(cr.Left, full.Height, name);
                            if (est > cr.Left + 8 && est < cr.Right)
                            {
                                nameRight = est;
                                LogDetect("namecell-est", string.Format("cellW={0:F0} estW={1:F0} fullW={2:F0}", cr.Width, est - cr.Left, full.Width));
                            }
                            else
                                LogDetect("namecell", string.Format("cellW={0:F0} fullW={1:F0} ratio={2:F2} (no-text)", cr.Width, full.Width, cr.Width / full.Width));
                        }
                    }
                }
            }
            catch { }

            if (!detailsView)
            {
                detailsView = IsDetailsView(explorerHwnd, full);
                FindNameArea(explorerHwnd, item, detailsView, full, out nameLeft, out nameRight, out nameTop, out nameHeight);
                // 走表头范围兜底时同样收缩到"图标+文件名"，否则名称列整列都会触发
                if (detailsView && nameRight - nameLeft > 60)
                {
                    double est = EstimateNameRight(nameLeft, full.Height, name);
                    if (est > nameLeft + 8 && est < nameRight) nameRight = est;
                }
            }

            try
            {
                var walker = TreeWalker.RawViewWalker;
                double pitch = 0;
                var sib = walker.GetNextSibling(item);
                if (sib != null)
                {
                    try { if (sib.Current.ControlType == ControlType.ListItem) pitch = sib.Current.BoundingRectangle.Y - full.Y; } catch { }
                }
                if (pitch <= 0)
                {
                    var prev = walker.GetPreviousSibling(item);
                    if (prev != null)
                    {
                        try { if (prev.Current.ControlType == ControlType.ListItem) pitch = full.Y - prev.Current.BoundingRectangle.Y; } catch { }
                    }
                }
                if (pitch > 0 && nameHeight > pitch) nameHeight = pitch;
            }
            catch { }

            if (!detailsView)
            {
                // 宽行 = 详细视图的典型形状。此时"仅图标/名称"绝不能整行放行，
                // 否则判定失败时整行都会触发（高亮框围住整行的现象）
                if (full.Width > full.Height * 3 && !string.IsNullOrEmpty(name))
                {
                    nameLeft = full.X;
                    nameRight = Math.Min(EstimateNameRight(full.X, full.Height, name), full.Right);
                    nameTop = full.Y;
                    nameHeight = full.Height;
                }
                else
                {
                    double cx = (full.X + full.Right) / 2;
                    double w = Math.Max(full.Width, full.Height);
                    nameLeft = cx - w / 2; nameRight = cx + w / 2;
                    nameTop = full.Y; nameHeight = full.Height;
                }
            }
            // 区域拒绝只对 tile 内的点生效：容差命中（留白里的点在 tile 矩形外）直接放行，
            // 高亮/预览仍用 nameArea 矩形
            bool inFull = mx >= full.X && mx <= full.Right && my >= full.Y && my <= full.Bottom;
            if (inFull && (mx < nameLeft || mx > nameRight || my < nameTop || my > nameTop + nameHeight))
            {
                LogDetect("rejected-region", string.Format("mouse={0},{1} nameArea=({2:F0},{3:F0})-({4:F0},{5:F0}) row=({6:F0},{7:F0})-({8:F0},{9:F0})",
                    mx, my, nameLeft, nameTop, nameRight, nameTop + nameHeight, full.X, full.Y, full.Right, full.Bottom));
                return null;
            }

            rect = new Rectangle((int)nameLeft, (int)nameTop, (int)(nameRight - nameLeft), (int)nameHeight);
            LogDetect("rect", string.Format("details={0} full=({1:F0},{2:F0})-({3:F0},{4:F0}) final=({5},{6})-({7},{8})",
                detailsView, full.X, full.Y, full.Right, full.Bottom, rect.Left, rect.Top, rect.Right, rect.Bottom));

            return name;
        }

        public static string GetBreadcrumbPath(IntPtr hwnd)
        {
            try
            {
                var el = AutomationElement.FromHandle(hwnd);
                if (el != null)
                {
                    var cond = new PropertyCondition(AutomationElement.ClassNameProperty, "FileExplorerExtensions.BreadcrumbBarItemControl");
                    var crumbs = el.FindAll(TreeScope.Descendants, cond);
                    if (crumbs.Count > 0)
                    {
                        string path = null;
                        foreach (AutomationElement crumb in crumbs)
                        {
                            string name = crumb.Current.Name;
                            if (string.IsNullOrEmpty(name)) continue;
                            try { if (crumb.Current.IsOffscreen) continue; } catch { }
                            var match = Regex.Match(name, @"\(([A-Za-z]:)\)");
                            if (match.Success)
                                path = match.Groups[1].Value + "\\";
                            else if (path != null)
                            {
                                string next = Path.Combine(path, name);
                                if (Directory.Exists(next)) path = next;
                            }
                        }
                        if (path != null && Directory.Exists(path)) return path;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}