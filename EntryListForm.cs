using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;


using System.Windows.Forms;

namespace HoverExplorer
{
    // 链接条目：收藏分组里以 [文本](url) 的形式与路径条目共用 List<string>，
    // 拖拽只落到收藏分组（历史仍是自动记录的路径，但已有链接也能照常显示/打开）。
    internal static class LinkEntry
    {
        private static readonly Dictionary<string, Image> _icons = new Dictionary<string, Image>();

        public static bool IsLink(string s)
        {
            string t, u;
            return TryParse(s, out t, out u);
        }

        public static bool TryParse(string s, out string text, out string url)
        {
            text = null; url = null;
            if (string.IsNullOrEmpty(s)) return false;
            string head, note;
            SplitHead(s, out head, out note);
            return ParseHead(head, out text, out url);
        }

        private static bool ParseHead(string head, out string text, out string url)
        {
            text = null; url = null;
            head = (head ?? "").Trim();
            if (head.Length < 8 || head[0] != '[') return false;
            int close = head.IndexOf("](", StringComparison.Ordinal);
            if (close <= 1 || head[head.Length - 1] != ')') return false;
            string t = head.Substring(1, close - 1);
            string u = head.Substring(close + 2, head.Length - close - 3);
            if (t.Length == 0 || u.Length == 0 || !IsWebUrl(u)) return false;
            text = t; url = u;
            return true;
        }

        // 拆出链接头与备注正文。正常条目头在第 1 行；备注保存后若第一行被误写成正文，
        // 仍从任意行找回 [标题](url)，避免整条链接降级成"文件"导致预览打不开。
        private static void SplitHead(string s, out string head, out string note)
        {
            head = ""; note = "";
            if (string.IsNullOrEmpty(s)) return;
            string[] lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int hi = -1;
            string t, u;
            for (int i = 0; i < lines.Length; i++)
                if (ParseHead(lines[i], out t, out u)) { hi = i; break; }
            head = hi >= 0 ? lines[hi].Trim() : lines[0].Trim();
            var rest = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
                if (i != hi) { if (rest.Length > 0) rest.Append('\n'); rest.Append(lines[i]); }
            note = rest.ToString().TrimStart('\r', '\n');
        }

        // 条目的链接头（可能是备注中的某行）；纯文本条目返回首行
        public static string Header(string s)
        {
            string head, note;
            SplitHead(s, out head, out note);
            return head;
        }

        // 链接头之后的 md 备注正文（无备注返回空串）
        public static string Note(string s)
        {
            string head, note;
            SplitHead(s, out head, out note);
            return note;
        }

        // 生成"链接 + 备注"完整条目
        public static string FormatWithNote(string text, string url, string note)
        {
            string head = Format(text, url);
            if (string.IsNullOrEmpty(note)) return head;
            note = note.Trim();
            return note.Length == 0 ? head : head + "\n" + note;
        }

        public static bool IsWebUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            Uri uri;
            if (!Uri.TryCreate(s.Trim(), UriKind.Absolute, out uri)) return false;
            switch (uri.Scheme.ToLowerInvariant())
            {
                case "http": case "https": case "ftp": case "mailto": case "file": return true;
            }
            return false;
        }

        public static string Format(string text, string url)
        {
            text = Regex.Replace(text ?? "", "\\s+", " ").Trim();
            text = text.Replace("[", "［").Replace("]", "］");
            if (text.Length == 0) text = (url ?? "").Trim();
            return "[" + text + "](" + (url ?? "").Trim().Replace(")", "%29") + ")";
        }

        // 列表显示文本 / 实际打开的 URL
        public static string Display(string s)
        {
            string t, u;
            return TryParse(s, out t, out u) ? t : Path.GetFileName(s);
        }

        public static string Url(string s)
        {
            string t, u;
            return TryParse(s, out t, out u) ? u : s;
        }

        // 现存路径或链接都算有效条目（列表过滤用，替代裸 PathExists）
        public static bool IsEntryValid(string s)
        {
            return IsLink(s) || Directory.Exists(s) || File.Exists(s);
        }

        // 强制诊断日志：不管 Config.DebugLog 开关，写一行到 drag_debug.log
        public static void Diag(string line)
        {
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drag_debug.log");
                File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
            }
            catch { }
        }

        // 强制诊断日志：不管 Config.DebugLog 开关，把拖放里所有格式与内容 dump 到 drag_debug.log
        public static void DumpDrag(IDataObject d, string tag)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== " + tag + " ===");
                string[] fmts = d.GetFormats();
                sb.AppendLine("formats: " + string.Join(", ", fmts));
                foreach (string fmt in fmts)
                {
                    try
                    {
                        object o = d.GetData(fmt);
                        string desc;
                        if (o == null) desc = "null";
                        else if (o is string)
                        {
                            string s = (string)o;
                            desc = "string[" + s.Length + "] " + s.Substring(0, Math.Min(240, s.Length)).Replace("\r", "\\r").Replace("\n", "\\n");
                        }
                        else if (o is MemoryStream)
                        {
                            byte[] b = ((MemoryStream)o).ToArray();
                            desc = "bytes[" + b.Length + "] head=" + BitConverter.ToString(b, 0, Math.Min(48, b.Length));
                            try { desc += " | asUTF8=" + Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 160)).Replace("\r", "\\r").Replace("\n", "\\n"); } catch { }
                            try { desc += " | asGBK=" + Encoding.GetEncoding(936).GetString(b, 0, Math.Min(b.Length, 160)).Replace("\r", "\\r").Replace("\n", "\\n"); } catch { }
                        }
                        else if (o is string[])
                            desc = "string[] " + string.Join(" | ", (string[])o);
                        else if (o is Image)
                            desc = "Image " + ((Image)o).Width + "x" + ((Image)o).Height;
                        else
                            desc = "type=" + o.GetType().FullName;
                        sb.AppendLine("  [" + fmt + "] " + desc);
                    }
                    catch (Exception ex) { sb.AppendLine("  [" + fmt + "] ERR " + ex.Message); }
                }
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drag_debug.log");
                File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff ") + sb.ToString());
            }
            catch { }
        }

        // 拖拽里是否可能带链接（只在 DragEnter/DragOver 里做格式探测，不解析正文）
        public static bool HasLinkData(IDataObject d)
        {
            if (d == null) return false;
            try
            {
                return d.GetDataPresent("UniformResourceLocatorW")
                    || d.GetDataPresent("UniformResourceLocator")
                    || d.GetDataPresent("text/uri-list")
                    || d.GetDataPresent("text/html")
                    || d.GetDataPresent(DataFormats.Html)
                    || d.GetDataPresent(DataFormats.Text);
            }
            catch { return false; }
        }

        // 从浏览器拖拽数据取链接：标题优先 text/html 的 <a> 文本 → 纯文本 → URL 本身
        public static bool TryGetDroppedLink(IDataObject d, out string entry)
        {
            entry = null;
            string url = null, title = null;
            DumpDrag(d, "TryGetDroppedLink");
            try
            {
                url = ReadUrlW(d);
                if (string.IsNullOrEmpty(url)) url = ReadAnsi(d, "UniformResourceLocator");
                if (string.IsNullOrEmpty(url))
                {
                    var uriList = ReadAnsi(d, "text/uri-list");
                    if (!string.IsNullOrEmpty(uriList))
                        foreach (var line in uriList.Split('\n'))
                            if (line.Length > 0 && line[0] != '#' && IsWebUrl(line)) { url = line.Trim(); break; }
                }

                // 纯文本优先读 UnicodeText(CF_UNICODETEXT)，DataFormats.Text 是 ANSI 代码页，中文会乱码
                string plain = ReadUnicodeText(d);
                if (string.IsNullOrEmpty(plain) && d.GetDataPresent(DataFormats.Text))
                    plain = (d.GetData(DataFormats.Text) as string ?? "").Trim();
                if (string.IsNullOrEmpty(url) && IsWebUrl(plain)) url = plain;

                string html = ReadHtml(d);
                if (!string.IsNullOrEmpty(html)) title = ExtractAnchorText(html);
                if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(plain) && !IsWebUrl(plain)) title = plain;
                title = RepairMojibake(title);
            }
            catch { }
            if (string.IsNullOrEmpty(url)) return false;
            entry = Format(title, url);
            return true;
        }

        // 拖拽数据是否是“图片”：浏览器拖图时 HTML 里有 <img src>，或 URL 本身是图片后缀。
        // 用于把图片直接并入目标链接的线索图，而不是新建一条链接。
        public static bool TryGetImageUrl(IDataObject d, out string url)
        {
            url = null;
            try
            {
                string html = ReadHtml(d);
                if (!string.IsNullOrEmpty(html))
                {
                    var m = Regex.Match(html, "<img[^>]+src\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
                    if (m.Success && IsWebUrl(m.Groups[1].Value))
                    {
                        url = m.Groups[1].Value.Trim();
                        return true;
                    }
                }
                string u = ReadUrlW(d);
                if (string.IsNullOrEmpty(u)) u = ReadUnicodeText(d);
                if (!string.IsNullOrEmpty(u) && IsImageUrl(u))
                {
                    url = u.Trim();
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsImageUrl(string u)
        {
            try
            {
                string p = new Uri(u.Trim()).AbsolutePath.ToLowerInvariant();
                return p.EndsWith(".png") || p.EndsWith(".jpg") || p.EndsWith(".jpeg") ||
                       p.EndsWith(".gif") || p.EndsWith(".bmp") || p.EndsWith(".webp");
            }
            catch { return false; }
        }

        private static string ReadUnicodeText(IDataObject d)
        {
            try
            {
                if (d.GetDataPresent(DataFormats.UnicodeText))
                    return (d.GetData(DataFormats.UnicodeText) as string ?? "").Trim();
            }
            catch { }
            return null;
        }

        private static string ReadUrlW(IDataObject d)
        {
            if (!d.GetDataPresent("UniformResourceLocatorW")) return null;
            var ms = d.GetData("UniformResourceLocatorW") as System.IO.MemoryStream;
            if (ms == null) return null;
            var bytes = ms.ToArray();
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        }

        private static string ReadAnsi(IDataObject d, string fmt)
        {
            if (!d.GetDataPresent(fmt)) return null;
            var obj = d.GetData(fmt);
            var s = obj as string;
            if (s != null) return s.Trim();
            var ms = obj as System.IO.MemoryStream;
            if (ms == null) return null;
            return Encoding.Default.GetString(ms.ToArray()).TrimEnd('\0').Trim();
        }

        private static string ReadHtml(IDataObject d)
        {
            try
            {
                // 关键：Chrome 的 "HTML Format"(CF_HTML) 字符串常是 UTF-8 被当 GBK 解码后的乱码，
                // 而 "text/html" 字节流才是正确的 UTF-8。必须优先读原始字节自己解码。
                if (d.GetDataPresent("text/html"))
                {
                    var obj = d.GetData("text/html");
                    var ms = obj as MemoryStream;
                    if (ms != null)
                    {
                        string fromBytes = DecodeHtmlBytes(ms.ToArray());
                        if (!string.IsNullOrEmpty(fromBytes)) return fromBytes;
                    }
                    var s = obj as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                // CF_HTML 兜底：MemoryStream 按头部 charset 解码；若是已解码的字符串则可能乱码，交给 RepairMojibake
                if (d.GetDataPresent(DataFormats.Html))
                {
                    var obj = d.GetData(DataFormats.Html);
                    var s = obj as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                    var ms = obj as MemoryStream;
                    if (ms != null) return DecodeHtmlBytes(ms.ToArray());
                }
            }
            catch { }
            return null;
        }

        // 从 CF_HTML 头部解析 charset，按声明编码解码字节流
        private static string DecodeHtmlBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            string head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8192));
            var m = Regex.Match(head, "charset\\s*=\\s*[\"']?([\\w-]+)", RegexOptions.IgnoreCase);
            Encoding enc = null;
            if (m.Success)
            {
                try { enc = Encoding.GetEncoding(m.Groups[1].Value); } catch { }
            }
            if (enc == null && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                enc = new UTF8Encoding(false);
            return (enc ?? Encoding.UTF8).GetString(bytes);
        }

        // 修复"UTF-8 字节被当作 GBK/ANSI 解码"的中文乱码，如 涓枃鏍囬 → 中文标题。
        // 关键：必须有 PUA(私用区 U+E000-U+F8FF) 特征才尝试——GBK 字节被当 UTF8 解读时
        // 必然产生 PUA 字符，而正常中文/西文绝不含 PUA。否则"百度一下"这类正常串会被误伤成乱码。
        private static string RepairMojibake(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2) return s;
            bool hasPua = false;
            foreach (char ch in s) if (ch >= 0xE000 && ch <= 0xF8FF) { hasPua = true; break; }
            if (!hasPua) return s;
            var strictUtf8 = new UTF8Encoding(false, true);
            try
            {
                string fixedStr = strictUtf8.GetString(Encoding.GetEncoding(936).GetBytes(s));
                if (LooksLikeChinese(fixedStr)) return fixedStr;
            }
            catch { }
            try
            {
                string fixedStr = strictUtf8.GetString(Encoding.GetEncoding(1252).GetBytes(s));
                if (LooksLikeChinese(fixedStr)) return fixedStr;
            }
            catch { }
            return s;
        }

        // 逆转结果是否像正常文本：只允许 ASCII / CJK 汉字 / 中文标点 / 全角
        private static bool LooksLikeChinese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int readable = 0;
            foreach (char ch in s)
            {
                int c = ch;
                if (c == 0xFFFD || (c < 0x20 && c != '\t')) return false;
                if (c < 0x80) readable++;
                else if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3000 && c <= 0x303F) ||
                         (c >= 0xFF00 && c <= 0xFFEF)) readable++;
                else return false;
            }
            return readable > 0;
        }

        private static string ExtractAnchorText(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var m = Regex.Match(html, "<a\\b[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success)
            {
                // 优先 innerText
                string t = Regex.Replace(m.Groups[1].Value, "<[^>]+>", "");
                t = System.Net.WebUtility.HtmlDecode(t);
                t = Regex.Replace(t, "\\s+", " ").Trim();
                if (t.Length > 0) return t;
                // innerText 为空（图片链接/纯图标链接）时，退回 <a title="..."> / aria-label
                string attr = ExtractAttr(m.Groups[0].Value, "title") ?? ExtractAttr(m.Groups[0].Value, "aria-label");
                if (!string.IsNullOrEmpty(attr)) return attr;
            }
            // 没有 <a> 时（如纯 <img>），取 img 的 alt / title
            var im = Regex.Match(html, "<img\\b[^>]*>", RegexOptions.IgnoreCase);
            if (im.Success)
                return ExtractAttr(im.Groups[0].Value, "alt") ?? ExtractAttr(im.Groups[0].Value, "title");
            return null;
        }

        private static string ExtractAttr(string tag, string name)
        {
            var m = Regex.Match(tag, "\\b" + Regex.Escape(name) + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(tag, "\\b" + Regex.Escape(name) + "\\s*=\\s*'([^']*)'", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            v = Regex.Replace(v, "\\s+", " ").Trim();
            return v.Length == 0 ? null : v;
        }

        // 链接行图标：16px 地球，颜色跟随列表强调色
        public static Image Icon(Color c)
        {
            string key = "link_" + c.ToArgb();
            Image cached;
            if (_icons.TryGetValue(key, out cached)) return cached;
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var p = new Pen(c, 1.2f))
                {
                    g.DrawEllipse(p, 2.5f, 2.5f, 11f, 11f);
                    g.DrawEllipse(p, 5.5f, 2.5f, 5f, 11f);
                    g.DrawLine(p, 2.5f, 8f, 13.5f, 8f);
                }
            }
            _icons[key] = bmp;
            return bmp;
        }
    }

    // 历史/收藏文件夹入口列表：表头（标题+⧉复制+图钉固定+⌄折叠）与文件列表行为一致；
    // 收藏分组在前、历史在后；点击行展开该文件夹级联列表，右键行管理。
    // 数据变更（RecordRecent/收藏增删）实时重建（NotifyChanged）；固定后不自动关闭
    internal partial class EntryListForm : Form
    {
        private readonly Action<string, Point> _onOpenFolder;
        private readonly List<Control> _rows = new List<Control>();   // 分组标签+行（重建时清除）
        private readonly HashSet<string> _selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Ctrl+点击/Shift+区间多选
        private int _anchorIndex = -1;               // Shift+点击区间的锚点行索引
        private Timer _watchTimer;
        private bool _dirty;
        private static EntryListForm _current;   // 当前开着的实例（实时更新通知用）

        private Label _titleLbl;
        private Label _pinBtn;
        private Label _foldBtn;
        private Panel _headPanel;   // 表头容器：宽度须随 _listW 同步，否则按钮被裁切
        private bool _pinned;
        private bool _firstShownRebuilt;   // 首次显示后是否已完成一次重建（消除首帧白色）
        private int _listW = 340;
        private int _headerH { get { return Dpi.S(26); } }
        private Color _headerBg = Config.Accent;
        private int _fullH = 60;
        // 行区/表头左右内缩与底部留白：与 PopupForm 一致，不顶到面板左右边缘与底边，
        // 与深色配色下圆角描边（同背景色）视觉协调
        private static int ListPadLr { get { return Dpi.S(6); } }
        private static int ListPadBottom { get { return Dpi.S(6); } }
        private Timer _rowHoverTimer;     // 行悬停 400ms → 右侧弹子列表
        private string _rowHoverPath;
        // 手动 resize：FormBorderStyle.None 下系统 HTBOTTOM sizing loop 会拖顶部，改手动保持 top/left 不变
        private bool _manualResize;
        private int _resizeMode;          // 1=底缘 2=右缘 3=右下角
        private int _resizeStartX, _resizeStartY, _resizeStartW, _resizeStartH;

        private EntryRow _rowHoverRow;
        private Timer _dragSubTimer;      // 拖拽悬停文件夹 ~500ms → 弹簧式打开级联子列表
        private EntryRow _dragSubRow;

        // ---- 标签栏（原型 A 分组标签，历史作为特殊分组）----
        private int _curG = 0;                    // 当前分组索引；-1 = 历史分组
        private int _lastFavG = 0;                // 上次选中的收藏分组（历史行点☆加入该组）
        private bool _suppressEnter;              // 拖放后抑制一次 MouseEnter 重建
        private Timer _groupHoverTimer;           // 分组标签悬停延迟计时器
        private int _pendingGroup = int.MinValue; // int.MinValue=无待激活；-1=历史
        private const string DragFmtRow = "EntryRowDrag";   // 内部行拖动数据格式
        internal class RowDragData { public List<string> Paths = new List<string>(); public int? FromG; public int? FromH; public FavoriteGroup FromGroup; }
        private int _listTop;   // 列表区顶部 y（拖放插入指示用）
        private static int SideGap { get { return Dpi.S(4); } }            // 左侧分组栏与右侧列表间距
        private static int SideBarWMin { get { return Dpi.S(72); } }
        private static int SideBarWMax { get { return Dpi.S(150); } }
        private int _sideW = 90;
        private int _sideItemH = 36;              // 展开模式下按组名长度自动增高                  // 左侧分组栏宽度
        private PreviewForm _previewForm;
        private Timer _previewTimer, _previewCloseTimer;
        private string _previewPath;


        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 与面板/预览一致：暗色标题区 + DWM 圆角（Win11）+ 投影
            Win11.Apply(Handle, Config.IsDark, Config.Border);
            Win11.RemoveNcBorder(Handle);

        }

        public EntryListForm(Action<string, Point> onOpenFolder)
        {
            _onOpenFolder = onOpenFolder;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Config.ListBg;   // 面板统一底色：与文件夹列表行底同色，不叠加斑马色块
            DoubleBuffered = false;   // 双缓冲绘制在首次弹出时偶发 BufferedGraphics AV 崩溃（进程退出），改直绘
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Tab && SelectedGroup != null && (_protoSearch == null || !_protoSearch.ContainsFocus))
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    IndentGroup(SelectedGroup, e.Shift, null);
                    return;
                }
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
            _headPanel = new Panel { BackColor = Config.ListBg, Location = new Point(ListPadLr, 0), Size = new Size(_listW - ListPadLr * 2, _headerH) };
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
                BackColor = Config.ListBg,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(Dpi.S(6), Dpi.S(3)),
                Size = new Size(_headPanel.Width - Dpi.S(58), Dpi.S(20)),
                Cursor = Cursors.Hand,
            };
            Tips.Set(_titleLbl, I18n.T("entry_tip"));
            _titleLbl.MouseDown += drag;
            _headPanel.Controls.Add(_titleLbl);

            // 图钉：固定后不随鼠标离开自动关闭
            _pinBtn = new Label
            {
                Size = Dpi.Sz(20, 18),
                AutoSize = false,
                BackColor = Config.ListBg,
                Image = PreviewForm.PinIcon(false),
                Cursor = Cursors.Hand,
                Location = new Point(_headPanel.Width - Dpi.S(46), Dpi.S(4)),
            };
            Tips.Set(_pinBtn, I18n.T("tip_pin_panel"));
            _pinBtn.Click += (s, e) => _fnTogglePin();
            _headPanel.Controls.Add(_pinBtn);

            // 折叠为桌面卡片：列表收起为常驻小卡片（悬停卡片再弹列表）
            _foldBtn = new Label
            {
                Size = Dpi.Sz(20, 18),
                AutoSize = false,
                BackColor = Config.ListBg,
                Image = PreviewForm.PinLaunchIcon(false),
                Cursor = Cursors.Hand,
                Location = new Point(_headPanel.Width - Dpi.S(24), Dpi.S(4)),
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
        internal FavoriteGroup CurrentGroup()
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

        // ---- Ctrl+点击多选 ----
        private void _fnSetSelected(string path, EntryRow row, bool selected)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (selected) _selectedPaths.Add(path); else _selectedPaths.Remove(path);
            if (row != null && string.Equals(row.Full, path, StringComparison.OrdinalIgnoreCase))
            {
                row.Selected = selected;
                row.Invalidate();
            }
        }

        private void _fnToggleSelected(string path, EntryRow row)
        {
            _fnSetSelected(path, row, !_selectedPaths.Contains(path));
        }

        // Shift+点击：以锚点为起点连续选中（替换当前选择，与资源管理器一致）
        private void _fnSelectRange(int from, int to)
        {
            _fnClearSelection();
            int lo = Math.Min(from, to), hi = Math.Max(from, to);
            foreach (var r in _rows)
            {
                var row = r as EntryRow;
                if (row == null || row.Index < lo || row.Index > hi) continue;
                _fnSetSelected(row.Full, row, true);
            }
        }

        private void _fnClearSelection()
        {
            _selectedPaths.Clear();
            _anchorIndex = -1;
            foreach (var r in _rows)
            {
                var row = r as EntryRow;
                if (row == null || !row.Selected) continue;
                row.Selected = false;
                row.Invalidate();
            }
        }

        private void _fnPrepareDragSelection(string path, EntryRow row)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (!_selectedPaths.Contains(path)) _fnSetSelected(path, row, true);
            }
            else if (!_selectedPaths.Contains(path))
            {
                _fnClearSelection();
                _fnSetSelected(path, row, true);
            }
        }

        private void _fnHandleRowClick(string path, EntryRow row)
        {
            bool ctrl = (ModifierKeys & Keys.Control) != 0;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (ctrl)
            {
                _anchorIndex = row.Index;
                _fnToggleSelected(path, row);
                return;
            }
            if (shift)
            {
                int from = _anchorIndex >= 0 ? _anchorIndex : row.Index;
                _fnSelectRange(from, row.Index);
                _anchorIndex = from;
                return;
            }
            _fnClearSelection();
            _anchorIndex = -1;
            // 链接行：整行点击直接用默认浏览器打开，不计入历史
            if (LinkEntry.IsLink(path))
            {
                _fnOpenLink(path);
                if (!_pinned) Close();
                return;
            }
            var pt = row.PointToScreen(Point.Empty);
            RecordRecent(path);
            _fnOpenPath(path, pt);
            if (!_pinned) Close();
        }

        // ---- 分组标签悬停延迟 ----
        private void _fnScheduleGroup(int gi)
        {
            if (_suppressEnter || Control.MouseButtons != MouseButtons.None) return;
            _fnCancelPendingGroup();
            if (gi == _curG) return;
            int delay = Math.Max(0, Config.GroupHoverDelayMs);
            _pendingGroup = gi;
            if (delay == 0) { _fnActivateGroup(gi); return; }
            if (_groupHoverTimer == null)
            {
                _groupHoverTimer = new Timer();
                _groupHoverTimer.Tick += delegate
                {
                    int target = _pendingGroup;
                    _fnCancelPendingGroup();
                    if (target != int.MinValue) _fnActivateGroup(target);
                };
            }
            _groupHoverTimer.Interval = delay;
            _groupHoverTimer.Start();
        }

        private void _fnCancelPendingGroup(int gi = int.MinValue)
        {
            if (_groupHoverTimer != null) _groupHoverTimer.Stop();
            if (gi == int.MinValue || _pendingGroup == gi) _pendingGroup = int.MinValue;
        }

        private void _fnActivateGroup(int gi, bool fromDrag = false)
        {
            if (_suppressEnter) return;
            if (!fromDrag && Control.MouseButtons != MouseButtons.None) return;
            if (gi == _curG) return;
            _curG = gi;
            if (gi >= 0) { _lastFavG = gi; Config.FavoriteCurGroup = gi; }
            _fnClearSelection();
            _fnBuildRows();
        }

        private void _fnBuildRows()
        {

            foreach (var r in _rows) { try { Controls.Remove(r); r.Dispose(); } catch { } }
            _rows.Clear();
            var grp = CurrentGroup();
            bool isHist = _curG < 0;

            // 顶部标题栏与面板同底色：不叠加独立色块，圆角由窗体 DWM 统一裁切
            Color headBg = Config.ListBg;
            string headTxt = isHist ? "🕘 " + I18n.T("hist_bar") : grp.Name;
            Color headFg = Config.ListFg;

            List<string> items;
            if (isHist) items = Config.RecentFolders.Where(LinkEntry.IsEntryValid).ToList();
            else items = grp.Items.Where(LinkEntry.IsEntryValid).ToList();

            var nameFont = new Font(Config.FontName, Config.FontSize);
            var tabFont = new Font(Config.FontName, Config.FontSize - 0.5f);

            // 左侧分组栏：默认 38px 只显示首字；展开模式按最长组名撑开并换行显示全名
            _sideItemH = Dpi.S(36);
            if (Config.EntryGroupTitleExpand)
            {
                using (var g = CreateGraphics())
                {
                    int maxW = SideBarWMin;
                    foreach (var fg in Config.FavoriteGroups)
                    {
                        string label = string.IsNullOrEmpty(fg.Name) ? "?" : fg.Name;
                        int tw = (int)Math.Ceiling(g.MeasureString(label, nameFont).Width) + Dpi.S(12);
                        if (tw > maxW) maxW = tw;
                    }
                    _sideW = Math.Min(SideBarWMax, Math.Max(SideBarWMin, maxW));
                    int maxLines = 1;
                    foreach (var fg in Config.FavoriteGroups)
                    {
                        int tw = (int)Math.Ceiling(g.MeasureString(fg.Name ?? "", nameFont).Width) + Dpi.S(12);
                        int lines = Math.Max(1, (int)Math.Ceiling(tw / (double)_sideW));
                        if (lines > maxLines) maxLines = lines;
                    }
                    _sideItemH = Math.Min(Dpi.S(120), Math.Max(Dpi.S(36), maxLines * (nameFont.Height + Dpi.S(2)) + Dpi.S(8)));
                }
            }
            else _sideW = Dpi.S(38);

            // 总宽度：手动调整值优先；否则按右栏内容自适应
            if (Config.EntryListW > 0) _listW = Dpi.S(Config.EntryListW);
            else
            {
                int w = Dpi.S(300);
                using (var g = CreateGraphics())
                {
                    foreach (var p in items)
                    {
                        var sw = g.MeasureString(Path.GetFileName(p), nameFont).Width;
                        if (sw + Dpi.S(64) > w) w = (int)sw + Dpi.S(64);
                    }
                }
                _listW = ListPadLr * 2 + _sideW + SideGap + Math.Min(w, Dpi.S(420));
            }

            BackColor = Config.ListBg;
            var font = new Font(Config.FontName, Config.FontSize);
            var subFont = new Font(Config.FontName, Config.FontSize - 0.5f);
            int y = _headerH + Dpi.S(2);

            // ---- 左侧分组栏 + 右侧列表区 ----
            int rightX = ListPadLr + _sideW + SideGap;
            int rightW = _listW - rightX - ListPadLr;
            if (rightW < Dpi.S(120)) rightW = Dpi.S(120);

            // 左侧分组栏（纵向，历史置顶）
            int sideBarH = _fnBuildSideBar(y, _sideW, font);


            // ---- 右侧原型式列表区（工具栏 + 分组区块 + 底栏）----
            _listTop = y;
            int protoH = _fnBuildProtoMain(rightX, y, rightW, isHist, font, subFont);
            if (Config.EntryListH > 0) _fullH = Dpi.S(Config.EntryListH);
            else _fullH = Math.Max(protoH, _headerH + Dpi.S(2) + sideBarH + ListPadBottom);
            _headerBg = headBg;
            if (_headPanel != null)
            {
                _headPanel.Location = Point.Empty;
                _headPanel.Size = new Size(_listW, _headerH);
                _headPanel.BackColor = headBg;
                // 不设自绘圆角 Region：圆角由窗体 DWM 统一裁切。
                // 手动 Region 比控件高 8px，句柄未就绪时首帧会在圆角缺口露出系统白色，
                // 切换分组重建后才对齐——这正是"首次白色、切换后正常"的根因。
                _headPanel.Region = null;
            }
            if (_titleLbl != null)
            {
                _titleLbl.Width = _headPanel.Width - Dpi.S(58);
                _titleLbl.Text = headTxt;
                _titleLbl.BackColor = headBg;
                _titleLbl.ForeColor = headFg;
            }
            if (_foldBtn != null) { _foldBtn.Location = new Point(_headPanel.Width - Dpi.S(46), Dpi.S(4)); _foldBtn.BackColor = headBg; }
            if (_pinBtn != null) { _pinBtn.Location = new Point(_headPanel.Width - Dpi.S(24), Dpi.S(4)); _pinBtn.BackColor = headBg; }
            ClientSize = new Size(_listW, _fullH);

        }

        // 外部文件拖入空白区 → 收藏到当前分组
        private void _fnAddToCurrentGroup(string[] paths)
        {
            var grp = CurrentGroup();
            if (grp == null) return;
            foreach (var p0 in paths)
            {
                string p = NormalizeFavPath(p0);
                if (!LinkEntry.IsEntryValid(p)) continue;
                if (!grp.Items.Contains(p, StringComparer.OrdinalIgnoreCase)) grp.Items.Add(p);
            }
            _dirty = true; _fnSave(); _fnBuildRows();
        }

        // 收藏项规范化：桌面快捷方式(.lnk)写入收藏时解析为实际目标，
        // 否则源快捷方式被删除后条目既没有图标也点不开。
        internal static string NormalizeFavPath(string p)
        {
            if (string.IsNullOrEmpty(p) || LinkEntry.IsLink(p)) return p;
            try
            {
                if (string.Equals(Path.GetExtension(p), ".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string t = PreviewProvider.GetLnkTarget(p);
                    if (!string.IsNullOrEmpty(t) && (File.Exists(t) || Directory.Exists(t))) return t;
                }
            }
            catch { }
            return p;
        }

        // 能把拖入的文件当参数处理的目标（快捷方式 / 可执行文件 / 批处理）
        private static bool CanConsumeFiles(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".lnk" || ext == ".exe" || ext == ".com" || ext == ".bat" || ext == ".cmd";
        }

        // 把文件交给收藏项落在的程序处理（等价于把文件拖到桌面快捷方式上）
        internal static bool LaunchWithFiles(string target, string[] files)
        {
            if (string.IsNullOrEmpty(target) || files == null || files.Length == 0) return false;
            if (!CanConsumeFiles(target)) return false;
            try
            {
                string args = string.Join(" ", files.Select(f => "\"" + f + "\""));
                var psi = new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true, Arguments = args };
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                System.Diagnostics.Process.Start(psi);
                Program.DebugLog("[entry] launch-with-files " + target + " <- " + args);
                return true;
            }
            catch (Exception ex) { Program.DebugLog("[entry] launch-with-files fail " + ex.Message); return false; }
        }

        // 外部文件拖入分组（分组标题 / 子分组行）→ 归入该分组
        internal void AddFilesToGroup(FavoriteGroup grp, string[] files)
        {
            if (grp == null || files == null || files.Length == 0) return;
            int at = grp.Items.Count;
            bool added = false;
            foreach (var f in files)
            {
                string p = NormalizeFavPath(f);
                if (!LinkEntry.IsEntryValid(p)) continue;
                if (grp.Items.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
                grp.Items.Insert(at++, p);
                added = true;
            }
            if (!added) return;
            grp.Open = true;
            _dirty = true; _fnSave(); _fnBuildRows();
        }

        // 行点击打开：文件用默认程序，文件夹走 _onOpenFolder
        private void _fnOpenPath(string path, Point pt)
        {
            if (LinkEntry.IsLink(path)) { _fnOpenLink(path); return; }
            if (File.Exists(path))
            {
                Program._entryArmed = false;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { Program.DebugLog("[entry] file open fail: " + ex.Message); }
            }
            else _onOpenFolder(path, pt);
        }

        private void _fnOpenLink(string entry)
        {
            Program._entryArmed = false;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LinkEntry.Url(entry)) { UseShellExecute = true }); }
            catch (Exception ex) { Program.DebugLog("[entry] link open fail: " + ex.Message); }
        }

        // 拖入链接：插到当前收藏分组顶部（历史分组不接受拖入）
        private void _fnAddLinkToCurrentGroup(string entry)
        {
            var grp = CurrentGroup();
            if (grp == null || string.IsNullOrEmpty(entry)) return;
            if (!grp.Items.Contains(entry, StringComparer.OrdinalIgnoreCase)) grp.Items.Insert(0, entry);
            _dirty = true;
            _fnSave();
            _fnBuildRows();
        }

        // ---- 左侧色块窄栏（原型 S）：历史置顶、色块首字、悬停 tooltip、激活外圈、拖放移组 ----
        private int _fnBuildSideBar(int top, int w, Font font)
        {
            var bar = new Panel { Location = new Point(ListPadLr, top), Size = new Size(w, 100), BackColor = Config.ListBg };
            var chipFont = new Font(Config.FontName, Config.FontSize + 1f, FontStyle.Bold);
            var iconFont = new Font(Config.FontName, Config.FontSize + 2f);
            int itemH = _sideItemH;
            int cs = Dpi.S(22);   // 色块尺寸
            int iy = 0;

            // 历史置顶（图标，悬停切换）
            bool histOn = _curG < 0;
            var histTab = new SideTab { Location = new Point(0, iy), Size = new Size(w, itemH), BackColor = Config.ListBg, Cursor = Cursors.Hand };
            histTab.Paint += delegate(object s, PaintEventArgs pe)
            {
                var g2 = pe.Graphics;
                g2.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g2.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var r = new Rectangle(0, 0, w, itemH);
                using (var path = RowLabel.RoundRect(new RectangleF(2, 2, w - 4, itemH - 4), 6f))
                using (var b = new SolidBrush(histTab.BackColor)) g2.FillPath(b, path);
                if (Config.EntryGroupTitleExpand)
                {
                    TextRenderer.DrawText(g2, "🕘 " + I18n.T("hist_bar"), font,
                        new Rectangle(8, 0, w - 12, itemH), histOn ? Config.ListFg : Config.ListMu,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
                else
                {
                    TextRenderer.DrawText(g2, "🕘", iconFont, r, histOn ? Config.ListFg : Config.ListMu,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                if (histOn)
                {
                    using (var path = RowLabel.RoundRect(new RectangleF(2, 2, w - 4, itemH - 4), 6f))
                    using (var p = new Pen(Config.Accent, 2)) g2.DrawPath(p, path);
                }
            };
            histTab.MouseEnter += delegate { _fnScheduleGroup(-1); };
            histTab.MouseLeave += delegate { _fnCancelPendingGroup(-1); };
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
                var tab = new SideTab { Location = new Point(0, iy), Size = new Size(w, itemH), BackColor = Config.ListBg, Cursor = Cursors.Hand };
                tab.Paint += delegate(object s, PaintEventArgs pe)
                {
                    var g2 = pe.Graphics;
                    g2.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g2.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    using (var bgPath = RowLabel.RoundRect(new RectangleF(2, 2, w - 4, itemH - 4), 6f))
                    using (var bgBrush = new SolidBrush(tab.BackColor)) g2.FillPath(bgBrush, bgPath);
                    if (Config.EntryGroupTitleExpand)
                    {
                        var blockF = new RectangleF(Dpi.S(4), Dpi.S(4), w - Dpi.S(8), itemH - Dpi.S(8));
                        using (var path = RowLabel.RoundRect(blockF, 5f))
                        using (var b = new SolidBrush(color)) g2.FillPath(b, path);
                        if (on)
                        {
                            using (var path = RowLabel.RoundRect(blockF, 5f))
                            using (var p = new Pen(Color.White, 2)) g2.DrawPath(p, path);
                            using (var path = RowLabel.RoundRect(new RectangleF(blockF.X - 2, blockF.Y - 2, blockF.Width + 4, blockF.Height + 4), 6f))
                            using (var p = new Pen(color, 3)) g2.DrawPath(p, path);
                        }
                        using (var b = new SolidBrush(Color.White))
                            g2.DrawString(RowLabel.MidEllipsis(g2, g.Name, font, w - 16), font, b,
                                4f + (w - 16 - g2.MeasureString(RowLabel.MidEllipsis(g2, g.Name, font, w - 16), font).Width) / 2f,
                                4f + (itemH - 8 - font.Height) / 2f);
                    }
                    else
                    {
                        int cx = (w - cs) / 2, cy = (itemH - cs) / 2;
                        using (var b = new SolidBrush(color)) g2.FillRectangle(b, cx, cy, cs, cs);
                        if (on)
                        {
                            using (var p = new Pen(Color.White, 2)) g2.DrawRectangle(p, cx, cy, cs, cs);
                            using (var p = new Pen(color, 3)) g2.DrawRectangle(p, cx - 2, cy - 2, cs + 4, cs + 4);
                        }
                        using (var b = new SolidBrush(Color.White))
                            g2.DrawString(first, chipFont, b, cx + (cs - chipFont.Height) / 2f, cy + (cs - chipFont.Height) / 2f);
                    }
                };
                tab.MouseEnter += delegate { _fnScheduleGroup(gi); };
                tab.MouseLeave += delegate { _fnCancelPendingGroup(gi); };
                tab.DoubleClick += delegate
                {
                    var nn = PromptDialog.Show(I18n.T("group_name"), g.Name);
                    if (!string.IsNullOrEmpty(nn)) { g.Name = nn; _dirty = true; _fnSave(); _fnBuildRows(); }
                };
                tab.MouseUp += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Right) _fnTabMenu(gi); };
                tab.AllowDrop = true;
                tab.DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DragFmtRow)) e.Effect = DragDropEffects.Move; else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; else if (LinkEntry.HasLinkData(e.Data)) e.Effect = DragDropEffects.Copy; };
                tab.DragOver += delegate(object s, DragEventArgs e) {
                    if (e.Data.GetDataPresent(DragFmtRow)) e.Effect = DragDropEffects.Move;
                    else if (e.Data.GetDataPresent(DataFormats.FileDrop) || LinkEntry.HasLinkData(e.Data))
                    {
                        e.Effect = (e.Data.GetDataPresent(DataFormats.FileDrop) && (e.KeyState & 4) != 0) ? DragDropEffects.Move : DragDropEffects.Copy;
                        if (gi != _curG)
                        {
                            _fnCancelPendingGroup();
                            _curG = gi; _lastFavG = gi; Config.FavoriteCurGroup = gi;
                            BeginInvoke((Action)(() => _fnBuildRows()));
                        }
                    }
                    else e.Effect = DragDropEffects.None;
                };
                tab.DragDrop += delegate(object s, DragEventArgs e) { _fnDropOnTab(gi, e); };
                Tips.Set(tab, g.Name + " · " + g.Items.Count);
                bar.Controls.Add(tab);
                iy += itemH;
            }

            // ＋新建
            var add = new SideTab { Location = new Point(0, iy), Size = new Size(w, Dpi.S(32)), BackColor = Config.ListBg, Cursor = Cursors.Hand };
            add.Paint += delegate(object s, PaintEventArgs pe)
            {
                var g2 = pe.Graphics;
                g2.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = RowLabel.RoundRect(new RectangleF(Dpi.S(2), Dpi.S(2), w - Dpi.S(4), Dpi.S(28)), 6f))
                using (var b = new SolidBrush(add.BackColor)) g2.FillPath(b, path);
                TextRenderer.DrawText(g2, "＋", iconFont, new Rectangle(0, 0, w, Dpi.S(32)), Config.ListMu,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            add.MouseEnter += delegate { add.BackColor = Config.ListHv; add.Invalidate(); };
            add.MouseLeave += delegate { add.BackColor = Config.ListBg; add.Invalidate(); };
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
            iy += Dpi.S(32);

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
            if (rd != null && rd.Paths.Count > 0)
            {
                RemoveDraggedSources(rd);
                int insertAt = 0;
                foreach (var p in rd.Paths)
                    if (!target.Items.Contains(p, StringComparer.OrdinalIgnoreCase)) target.Items.Insert(insertAt++, p);
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null) return;
                bool added = false;
                foreach (var p0 in files)
                {
                    string p = NormalizeFavPath(p0);
                    if (!LinkEntry.IsEntryValid(p)) continue;
                    if (!target.Items.Contains(p, StringComparer.OrdinalIgnoreCase)) { target.Items.Add(p); added = true; }
                }
                if (!added) return;
            }
            else
            {
                // 浏览器拖来的链接：直接落进该分组
                string link;
                if (!LinkEntry.TryGetDroppedLink(e.Data, out link)) return;
                if (!target.Items.Contains(link, StringComparer.OrdinalIgnoreCase)) target.Items.Insert(0, link);
            }
            _fnClearSelection();
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


        private EntryRow _fnMakeRow(string path, bool isFav, int index, Font font, Font subFont, int zebra, FavoriteGroup group = null)
        {
            // 条目统一底色（不用斑马纹）：与文件夹列表条目基础色 ListBg 一致
            var baseColor = Config.ListBg;
            var row = new EntryRow
            {
                Full = path,
                IsFav = isFav,
                Index = index,
                Font = font,
                BackColor = baseColor,
                BaseColor = baseColor,
                ForeColor = Config.ListFg,
                Cursor = Cursors.Hand,
                Name = LinkEntry.Display(path),
                IsLinkRow = LinkEntry.IsLink(path),
                SubFont = subFont,
                Selected = _selectedPaths.Contains(path),
                ReadOnly = true,
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
                if (e.Button == MouseButtons.Left) _fnHandleRowClick(path, row);
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
                // 真正开始拖拽时才整理选中集：Ctrl+点击的选中交给 MouseClick 处理，避免被这里先选再取消
                _fnPrepareDragSelection(path, row);
                var paths = _selectedPaths.Count > 1 ? _selectedPaths.ToList() : new List<string> { path };
                var data = new DataObject();
                data.SetData(DragFmtRow, new RowDragData { Paths = paths, FromGroup = group, FromG = group == null ? (int?)_curG : null, FromH = null });
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
                else if (_curG >= 0 && LinkEntry.HasLinkData(e.Data))
                {
                    e.Effect = DragDropEffects.Copy;
                    row.BackColor = Config.ListDrop;
                    _fnStopRowHover();
                }
            };
            row.DragOver += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DragFmtRow)) e.Effect = DragDropEffects.Move;
                else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = (e.KeyState & 4) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                else if (_curG >= 0 && LinkEntry.HasLinkData(e.Data)) e.Effect = DragDropEffects.Copy;
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
                if (e.Data.GetDataPresent(DragFmtRow)) { _fnDropRow(row, e, group); return; }
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null)
                {
                    // 图片拖到链接行 → 追加为该链接的线索图
                    if (LinkEntry.IsLink(path) && _fnAddImagesToLink(path, files)) return;
                    if (Directory.Exists(path)) Program.DropFilesIntoEntryFolder(path, files, (e.KeyState & 4) != 0);
                    else if (CanConsumeFiles(path)) LaunchWithFiles(path, files);   // 拖到快捷方式/程序项 = 交给它处理
                    else if (group != null) AddFilesToGroup(group, files);          // 其余条目：归入所在分组
                }
                else if (_curG >= 0)
                {
                    string link;
                    if (LinkEntry.TryGetDroppedLink(e.Data, out link))
                    {
                        // 拖到链接行上的若是图片（浏览器图片 / 图片 URL），并入该链接的线索图
                        if (LinkEntry.IsLink(path) && _fnAddImageUrlToLink(path, e.Data)) return;
                        _fnAddLinkToCurrentGroup(link);
                    }
                }
            };
            row.BtnHover += delegate(object s, bool inBtn)
            {
                if (inBtn) _fnStopRowHover();
                else if (_rowHoverPath == path) _fnStartRowHover(row);
            };
            row.CopyClicked += delegate { try { Clipboard.SetText(LinkEntry.IsLink(path) ? LinkEntry.Url(path) : path); _fnToast(I18n.T("copied")); } catch { } };
            row.PinClicked += delegate { if (!LinkEntry.IsLink(path)) Program.TogglePin(path); };
            row.RenameClicked += delegate { _fnBeginEntryRename(row); };
            return row;
        }

        // ---- 历史抽屉行：☆ 收藏（加入当前分组）、拖进分组 ----
        private EntryRow _fnMakeHistRow(string path, int histIdx, int index, Font font, Font subFont, int zebra)
        {
            // 条目统一底色（不用斑马纹）：与文件夹列表条目基础色 ListBg 一致
            var baseColor = Config.ListBg;
            var row = new EntryRow
            {
                Full = path,
                IsFav = false,
                Index = index,
                Font = font,
                BackColor = baseColor,
                BaseColor = baseColor,
                ForeColor = Config.ListFg,
                Cursor = Cursors.Hand,
                Name = LinkEntry.Display(path),
                IsLinkRow = LinkEntry.IsLink(path),
                SubFont = subFont,
                Selected = _selectedPaths.Contains(path),
                ReadOnly = true,
            };
            row.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) _fnHandleRowClick(path, row);
            };
            row.StarToggled += delegate
            {
                // ☆ → 加入上次选中的收藏分组
                var grp = _fnFavGroup(_lastFavG);
                string fp = NormalizeFavPath(path);
                if (!grp.Items.Contains(fp, StringComparer.OrdinalIgnoreCase)) grp.Items.Insert(0, fp);
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
                _fnPrepareDragSelection(path, row);
                var paths = _selectedPaths.Count > 1 ? _selectedPaths.ToList() : new List<string> { path };
                var data = new DataObject();
                data.SetData(DragFmtRow, new RowDragData { Paths = paths, FromG = null, FromH = histIdx });
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
                if (files != null)
                {
                    if (Directory.Exists(path)) Program.DropFilesIntoEntryFolder(path, files, (e.KeyState & 4) != 0);
                    else if (CanConsumeFiles(path)) LaunchWithFiles(path, files);   // 拖到快捷方式/程序项 = 交给它处理
                }
            };
            // ---- 行悬停操作按钮 ----
            row.BtnHover += delegate(object s, bool inBtn)
            {
                if (inBtn) _fnStopRowHover();
                else if (_rowHoverPath == path) _fnStartRowHover(row);
            };
            row.CopyClicked += delegate { try { Clipboard.SetText(LinkEntry.IsLink(path) ? LinkEntry.Url(path) : path); _fnToast(I18n.T("copied")); } catch { } };
            row.PinClicked += delegate { if (!LinkEntry.IsLink(path)) Program.TogglePin(path); };
            row.RenameClicked += delegate { _fnBeginEntryRename(row); };
            return row;
        }

        // 拖放落点处理：row=null 落在空白列表区（追加到末尾）；row=具体行（插入到该行位置）
        private void _fnDropRow(EntryRow targetRow, DragEventArgs e, FavoriteGroup group = null)
        {
            var rd = e.Data.GetData(DragFmtRow) as RowDragData;
            if (rd == null || rd.Paths.Count == 0) return;
            var grp = group ?? CurrentGroup();
            int ti = grp.Items.Count;
            if (targetRow != null)
            {
                ti = grp.Items.IndexOf(targetRow.Full);
                if (ti < 0) ti = grp.Items.Count;
            }
            RemoveDraggedSources(rd);
            int insertAt = ti;
            foreach (var p in rd.Paths)
                if (!grp.Items.Contains(p, StringComparer.OrdinalIgnoreCase)) grp.Items.Insert(insertAt++, p);
            _fnClearSelection();
            if (group == null)
            {
                _curG = Config.FavoriteGroups.IndexOf(grp);
                Config.FavoriteCurGroup = _curG;
            }
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
                RemoveFavEverywhere(Config.FavoriteGroups, row.Full);
                if (!Config.RecentFolders.Contains(row.Full, StringComparer.OrdinalIgnoreCase))
                    Config.RecentFolders.Insert(0, row.Full);
            }
            else
            {
                var grp = CurrentGroup() ?? _fnFavGroup(_lastFavG);
                string fp = NormalizeFavPath(row.Full);
                if (!grp.Items.Contains(fp, StringComparer.OrdinalIgnoreCase))
                    grp.Items.Insert(0, fp);
            }
            _dirty = true;
            _fnSave();
            _fnBuildRows();
        }

        private void _fnRowMenu(EntryRow row)
        {
            // 收藏列表中的文件/文件夹：直接弹资源管理器同款系统菜单（另加“从收藏移除”）
            if (row.IsFav && !LinkEntry.IsLink(row.Full))
            {
                ShellContextMenu.Show(row.Full, Handle, null, null, I18n.T("fav_remove"), delegate
                {
                    RemoveFavEverywhere(Config.FavoriteGroups, row.Full);
                    _dirty = true; _fnSave(); _fnBuildRows();
                });
                return;
            }
            var m = new ContextMenuStrip { ShowImageMargin = false, BackColor = Config.BG, ForeColor = Config.FG, Font = new Font(Config.FontName, Config.FontSize) };
                m.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
            if (!row.IsFav)
                m.Items.Add(I18n.T("add_fav"), null, (s, e) =>
                {
                    var grp = _fnFavGroup(_lastFavG);
                    string fp = NormalizeFavPath(row.Full);
                    if (!grp.Items.Contains(fp, StringComparer.OrdinalIgnoreCase)) grp.Items.Insert(0, fp);
                    _dirty = true; _fnSave(); _fnBuildRows();
                });
            else
                m.Items.Add(I18n.T("fav_remove"), null, (s, e) =>
                {
                    RemoveFavEverywhere(Config.FavoriteGroups, row.Full);
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
            // 链接行：行内输入框太窄，改用预览窗编辑完整文本
            if (LinkEntry.IsLink(row.Full)) { _fnOpenLinkEditor(row, true); return; }
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
            tb.TextChanged += (s, e) => { if (_rowEdit == tb) tb.ScrollToCaret(); };
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
            // 链接行：只改显示文本，URL 保持；输入框里粘贴完整 [文本](url) 也接受
            if (LinkEntry.IsLink(full))
            {
                string oldText, url;
                LinkEntry.TryParse(full, out oldText, out url);
                // 行内改名只改标题：URL 与备注（含图片附件）必须原样保留，
                // 以前直接 Format(nn,url) 会把备注整段丢掉。
                string note = LinkEntry.Note(full);
                string nt, nu;
                string newEntry;
                if (LinkEntry.TryParse(nn, out nt, out nu))
                    newEntry = LinkEntry.FormatWithNote(nt, nu,
                        string.IsNullOrEmpty(LinkEntry.Note(nn)) ? note : LinkEntry.Note(nn));
                else
                    newEntry = LinkEntry.FormatWithNote(nn, url, note);
                if (string.Equals(newEntry, full, StringComparison.Ordinal)) return;
                RenamePathInGroups(Config.FavoriteGroups, full, newEntry);
                for (int i = 0; i < Config.RecentFolders.Count; i++)
                    if (string.Equals(Config.RecentFolders[i], full, StringComparison.OrdinalIgnoreCase)) Config.RecentFolders[i] = newEntry;
                Config.Save();
                _fnBuildRows();
                return;
            }
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
                RenamePathInGroups(Config.FavoriteGroups, full, newPath);
                for (int i = 0; i < Config.RecentFolders.Count; i++)
                    if (string.Equals(Config.RecentFolders[i], full, StringComparison.OrdinalIgnoreCase)) Config.RecentFolders[i] = newPath;
                Config.Save();
                _fnBuildRows();
            }
            else { try { System.Media.SystemSounds.Beep.Play(); } catch { } }
        }

        // 链接预览/编辑：悬停即自动显示完整的 [标题](链接)；点重命名时聚焦正文编辑
        private void _fnOpenLinkEditor(EntryRow row, bool focus)
        {
            if (row == null || row.IsDisposed) return;
            _fnClosePreviewForm();
            _previewPath = row.Full;
            string currentEntry = row.Full;   // 随每次提交更新，供连续自动保存定位当前条目
            // 手动保存：提交后收起编辑窗；自动保存：只写回不收起，避免正在编辑时窗口消失
            var f = new PreviewForm(row.Full, row.Full.Length,
                text => { string ne = _fnCommitLinkText(currentEntry, text, true); if (ne != null) currentEntry = ne; },
                text => { string ne = _fnCommitLinkText(currentEntry, text, false); if (ne != null) currentEntry = ne; });
            _previewForm = f;
            _fnPositionPreview(row);
            f.SetHeader(row.Name, "LINK · " + LinkEntry.Url(row.Full));
            f.Show();
            NativeMethods.SetWindowPos(f.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
            if (focus) f.FocusContent();
        }

        // 把拖入的图片保存为附件，追加到链接条目的备注图片区（![](rel)）。
        // 返回 false 表示没有可用的图片，调用方继续走原有“归入分组”逻辑。
        private bool _fnAddImagesToLink(string entry, string[] files)
        {
            if (string.IsNullOrEmpty(entry) || files == null || files.Length == 0) return false;
            var exts = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
            var picks = files.Where(f => exts.Contains((Path.GetExtension(f) ?? "").ToLowerInvariant()))
                             .Where(File.Exists).ToList();
            if (picks.Count == 0) return false;

            var added = new List<string>();
            foreach (var f in picks)
            {
                try
                {
                    using (var img = Image.FromFile(f))
                    {
                        string rel = LinkAssets.SaveImage(img);
                        if (!string.IsNullOrEmpty(rel)) added.Add(rel);
                    }
                }
                catch { }
            }
            if (added.Count == 0) return false;
            return _fnAppendImagesToLink(entry, added);
        }

        // 浏览器拖入的图片（HTML <img> 或图片 URL）：下载后并入目标链接的线索图
        private bool _fnAddImageUrlToLink(string entry, IDataObject data)
        {
            string imgUrl;
            if (!LinkEntry.TryGetImageUrl(data, out imgUrl)) return false;
            try
            {
                byte[] bytes = LinkAssets.DownloadBytes(imgUrl);
                if (bytes == null || bytes.Length == 0) return false;
                string rel = LinkAssets.SaveImageBytes(bytes);   // 解码失败会返回 null
                if (string.IsNullOrEmpty(rel)) return false;
                LinkEntry.Diag("ImageUrl→link " + imgUrl + " → " + rel);
                return _fnAppendImagesToLink(entry, new List<string> { rel });
            }
            catch (Exception ex)
            {
                LinkEntry.Diag("AddImageUrl EX: " + ex.Message);
                return false;
            }
        }

        private bool _fnAppendImagesToLink(string entry, List<string> added)
        {
            string text, url;
            if (!LinkEntry.TryParse(entry, out text, out url)) return false;
            string note = LinkEntry.Note(entry).TrimEnd();
            foreach (var rel in added)
                note = (note.Length > 0 ? note + "\n" : "") + "![](" + rel + ")";
            string newEntry = LinkEntry.FormatWithNote(text, url, note);
            if (string.Equals(newEntry, entry, StringComparison.Ordinal)) return true;
            RenamePathInGroups(Config.FavoriteGroups, entry, newEntry);
            for (int i = 0; i < Config.RecentFolders.Count; i++)
                if (string.Equals(Config.RecentFolders[i], entry, StringComparison.OrdinalIgnoreCase))
                    Config.RecentFolders[i] = newEntry;
            Config.Save();
            _fnBuildRows();
            _fnToast("已添加 " + added.Count + " 张线索图");
            return true;
        }

        // 提交链接文本：首行保留 [标题](url)，换行后的 md 备注（含 ![]() 图片引用）整段保留
        // closeEditor：手动保存后收起编辑窗；自动保存时保持窗口打开
        private string _fnCommitLinkText(string oldEntry, string newText, bool closeEditor)
        {
            string oldText, url;
            if (!LinkEntry.TryParse(oldEntry, out oldText, out url)) return null;
            newText = (newText ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (newText.Length == 0 || string.Equals(newText, oldEntry, StringComparison.Ordinal)) return null;

            // 拆首行（链接）与备注正文
            int nl = newText.IndexOf('\n');
            string firstLine = nl >= 0 ? newText.Substring(0, nl) : newText;
            string note = nl >= 0 ? newText.Substring(nl + 1) : "";

            string newEntry;
            string nt, nu;
            if (LinkEntry.TryParse(firstLine, out nt, out nu))
                newEntry = LinkEntry.FormatWithNote(nt, nu, note);           // 首行仍是完整链接：整段保留
            else
                newEntry = LinkEntry.FormatWithNote(firstLine.Trim(), url, note);  // 首行被改成纯标题：保留原 URL
            if (string.Equals(newEntry, oldEntry, StringComparison.Ordinal)) return null;
            RenamePathInGroups(Config.FavoriteGroups, oldEntry, newEntry);
            for (int i = 0; i < Config.RecentFolders.Count; i++)
                if (string.Equals(Config.RecentFolders[i], oldEntry, StringComparison.OrdinalIgnoreCase)) Config.RecentFolders[i] = newEntry;
            Config.Save();
            // 顺手清理不再被任何条目引用的孤立图片附件。
            // 必须递归收集所有分组（含 Children 嵌套子分组），否则嵌套组里的图片引用
            // 会被误判为"无人引用"而在保存当次就被删掉——这正是"粘贴保存后图片消失"的根因。
            var all = new List<string>();
            foreach (var g in EnumAllGroups(Config.FavoriteGroups))
                if (g != null && g.Items != null) all.AddRange(g.Items);
            all.AddRange(Config.RecentFolders);
            LinkAssets.Cleanup(all);
            _fnBuildRows();
            // 手动保存后收起编辑窗（延迟到消息循环，避免在窗体自己的 Click 里销毁自身）；
            // 自动保存保持编辑窗打开，避免用户连续输入时窗口被关掉。
            if (closeEditor)
            {
                // 重建行后光标还停在原处，Windows 不会补发 MouseEnter；
                // 不重新挂悬停，保存后这条链接就再也弹不出预览窗了。
                try
                {
                    foreach (var r in _rows)
                    {
                        var er = r as EntryRow;
                        if (er != null && !er.IsDisposed && _fnRowContains(er)) { _fnStartRowHover(er); break; }
                    }
                }
                catch { }
                try { BeginInvoke((Action)_fnClosePreviewForm); } catch { }
            }
            return newEntry;
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
            // 链接行：与文件一样自动弹预览窗（内容是完整的 [标题](链接)），不弹级联子列表
            if (LinkEntry.IsLink(row.Full)) { _fnSchedulePreview(row); return; }
            // 文件行：调度预览；文件夹行：调度级联子列表（与原行为一致）
            bool isFile = File.Exists(row.Full);
            if (isFile && Config.PreviewFiles && PreviewProvider.CanPreview(row.Full))
            {
                _fnSchedulePreview(row);
                return;
            }
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
            _fnSchedulePreviewClose();
        }

        // ---- 文件预览（复用 PreviewProvider/PreviewForm，定位锚定列表行）----
        private void _fnSchedulePreview(EntryRow row)
        {
            if (_previewForm != null && _previewPath == row.Full) return;
            if (_previewForm != null && !_previewForm.Pinned) _fnClosePreviewForm();
            _previewPath = row.Full;
            if (_previewTimer != null) { _previewTimer.Stop(); _previewTimer.Dispose(); _previewTimer = null; }
            // 悬停延时统一走"鼠标悬停延时"（HoverDelay）：列表预览不再用独立的 PreviewDelay，
            // 与文件夹面板/资源管理器文件预览保持一致
            int delay = Config.PreviewDelayMsFor(row.Full);
            _previewTimer = new Timer { Interval = delay > 0 ? delay : 1 };
            _previewTimer.Tick += (s, e) =>
            {
                _previewTimer.Stop(); _previewTimer.Dispose(); _previewTimer = null;
                if (IsDisposed || _previewPath == null) return;
                _fnShowPreview(row);
            };
            _previewTimer.Start();
        }

        private void _fnSchedulePreviewClose()
        {
            if (_previewForm == null)
            {
                if (_previewTimer != null) { _previewTimer.Stop(); _previewTimer.Dispose(); _previewTimer = null; }
                _previewPath = null;
                return;
            }
            if (_previewCloseTimer != null) { _previewCloseTimer.Stop(); _previewCloseTimer.Dispose(); _previewCloseTimer = null; }
            _previewCloseTimer = new Timer { Interval = Math.Max(60, Config.CloseDelayMs) };
            _previewCloseTimer.Tick += (s, e) =>
            {
                _previewCloseTimer.Stop(); _previewCloseTimer.Dispose(); _previewCloseTimer = null;
                if (_previewForm == null) return;
                var pt = Cursor.Position;
                bool inPv = _previewForm.ContainsPoint(pt.X, pt.Y);
                bool inList = _fnListContainsCursor();
                if (inPv || inList) { _fnSchedulePreviewClose(); return; }
                _fnClosePreviewForm();
            };
            _previewCloseTimer.Start();
        }

        private void _fnClosePreviewForm()
        {
            if (_previewCloseTimer != null) { _previewCloseTimer.Stop(); _previewCloseTimer.Dispose(); _previewCloseTimer = null; }
            if (_previewForm == null) return;
            if (_previewForm.Pinned) { _previewForm = null; return; }
            try { _previewForm.Close(); _previewForm.Dispose(); } catch { }
            _previewForm = null;
        }

        private void _fnShowPreview(EntryRow row)
        {
            string full = _previewPath;
            if (full == null || full != row.Full) return;
            // 链接：同步构建预览窗，显示完整的 [标题](链接)
            if (LinkEntry.IsLink(full)) { _fnOpenLinkEditor(row, false); return; }
            Task.Run(() =>
            {
                try
                {
                    var result = PreviewProvider.Generate(full);
                    if (result == null) return;
                    try
                    {
                        if (IsDisposed) { if (result.Image != null) result.Image.Dispose(); return; }
                        BeginInvoke((MethodInvoker)delegate()
                        {
                            if (full != _previewPath) { if (result.Image != null) result.Image.Dispose(); return; }
                            _fnClosePreviewForm();
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
                            _fnPositionPreview(row);
                            _previewForm.FilePath = full;
                            PopupForm.ApplyHeader(_previewForm, full);
                            _previewForm.Show();
                            NativeMethods.SetWindowPos(_previewForm.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
                        });
                    }
                    catch (Exception ex) { Program.DebugLog("[entry-preview] show fail: " + ex.Message); }
                }
                catch (Exception ex) { Program.DebugLog("[entry-preview] generate fail: " + ex.Message); }
            });
        }

        private void _fnPositionPreview(EntryRow row)
        {
            if (_previewForm == null) return;
            int lx = Left, rx = Right;
            int x = rx + 8, y = row.PointToScreen(Point.Empty).Y;
            int pw = _previewForm.Width, ph = _previewForm.Height;
            NativeMethods.POINT pt;
            NativeMethods.GetCursorPos(out pt);
            var hmon = NativeMethods.MonitorFromPoint(pt, 2);
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
            NativeMethods.GetMonitorInfo(hmon, ref mi);
            int wl = mi.rcWork.Left, wt = mi.rcWork.Top, wr = mi.rcWork.Right, wb = mi.rcWork.Bottom;
            int maxW = wr - wl - 16, maxH = wb - wt - 16;
            if (maxW < 120) maxW = 120;
            if (maxH < 90) maxH = 90;
            if (pw > maxW) { pw = maxW; _previewForm.ClientSize = new Size(pw, _previewForm.ClientSize.Height); }
            if (ph > maxH) { ph = maxH; _previewForm.ClientSize = new Size(_previewForm.ClientSize.Width, ph); }
            if (x + pw > wr)
            {
                x = lx - pw - 8;
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
                if (LinkEntry.IsLink(r.Full)) return;
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
                // 表头提示随语言刷新；图标重取——主题切换 ClearUiIconCache 后
                // 旧 Image 引用已 Dispose，不重赋按钮会变空白（标题文本/颜色由 _fnBuildRows 按分组设置）
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
            // 跨显示器/改缩放：按新 DPI 重建（侧栏宽度、行高、工具栏全部按新比例重算）
            if (m.Msg == (int)Dpi.WM_DPICHANGED && Handle != IntPtr.Zero)
            {
                Dpi.SetFromWindow(Handle);
                try { _fnBuildRows(); } catch { }
            }
            // 圆角描边与自身背景同色：亮色描边压在深色配色上很突兀，同色后只剩 DWM 圆角外形
            if (Win11.WndNcBorder(Handle, ref m, () => BackColor)) return;
            if (m.Msg == 0x84)   // WM_NCHITTEST
            {
                // 用屏幕坐标 + Bounds 做检测，避免 PointToClient/ClientSize 在圆角模式下的坐标偏移
                var lp = m.LParam.ToInt64();
                var sp = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                var wb = Bounds;
                int grip = Dpi.S(6);
                // 右下角
                if (sp.X >= wb.Right - grip && sp.Y >= wb.Bottom - grip) { m.Result = (IntPtr)17; return; }
                // 右缘拖宽
                if (sp.X >= wb.Right - grip && sp.Y >= wb.Top && sp.Y < wb.Bottom) { m.Result = (IntPtr)11; return; }
                // 底缘拖高
                if (sp.Y >= wb.Bottom - grip && sp.X >= wb.Left && sp.X < wb.Right) { m.Result = (IntPtr)12; return; }
            }
            else if (m.Msg == 0xA1)   // WM_NCLBUTTONDOWN：拦截系统 sizing loop，手动 resize
            {
                int ht = m.WParam.ToInt32();
                if (ht == 12 || ht == 11 || ht == 17)
                {
                    _manualResize = true;
                    _resizeMode = ht == 12 ? 1 : (ht == 11 ? 2 : 3);
                    var lp = m.LParam.ToInt64();
                    _resizeStartX = (short)(lp & 0xFFFF);
                    _resizeStartY = (short)((lp >> 16) & 0xFFFF);
                    _resizeStartW = Width;
                    _resizeStartH = Height;
                    Capture = true;
                    return;
                }
            }
            else if (_manualResize && m.Msg == 0x200)   // WM_MOUSEMOVE
            {
                var lp = m.LParam.ToInt64();
                var sp = PointToScreen(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if ((_resizeMode & 1) != 0)   // 底缘或右下角
                {
                    int newH = _resizeStartH + (sp.Y - _resizeStartY);
                    Height = Math.Max(Dpi.S(60), newH);
                }
                if ((_resizeMode & 2) != 0)   // 右缘或右下角
                {
                    int newW = _resizeStartW + (sp.X - _resizeStartX);
                    Width = Math.Max(Dpi.S(200), newW);
                }
                return;
            }
            else if (_manualResize && m.Msg == 0x202)   // WM_LBUTTONUP
            {
                _manualResize = false;
                Capture = false;
                Config.EntryListW = Dpi.UnS(ClientSize.Width);
                Config.EntryListH = Dpi.UnS(ClientSize.Height);
                _dirty = true;
                _fnBuildRows();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // 构造期 _fnBuildRows() 在窗体句柄/DWM 圆角就绪前执行，首帧可能停在默认白色；
            // 显示后重建一次（与切换分组完全相同的路径），句柄已就绪，确保首次即正确配色。
            // 用标志位保证每个实例只重建一次，不影响后续切换分组性能。
            if (!_firstShownRebuilt)
            {
                _firstShownRebuilt = true;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    try { _fnBuildRows(); } catch { }
                });
            }

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
                    if (!inside && _previewForm != null && _previewForm.ContainsPoint(Cursor.Position.X, Cursor.Position.Y)) inside = true;
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
            if (_groupHoverTimer != null) { _groupHoverTimer.Stop(); _groupHoverTimer.Dispose(); _groupHoverTimer = null; }
            if (_dragSubTimer != null) { _dragSubTimer.Stop(); _dragSubTimer.Dispose(); _dragSubTimer = null; _dragSubRow = null; }
            _fnClosePreviewForm();
            if (_previewTimer != null) { _previewTimer.Stop(); _previewTimer.Dispose(); _previewTimer = null; }

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
        private static int CardH { get { return Dpi.S(28); } }
        private static int ShadowPad { get { return Dpi.S(2); } }   // 自绘阴影宽度（与桌面卡片一致）
        protected override bool ShowWithoutActivation { get { return true; } }

        // 分层窗口 WS_EX_LAYERED：逐像素 Alpha，圆角抗锯齿 + 阴影/色条完全按配色方案
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
                return cp;
            }
        }

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
            BackColor = CardPalette.Opaque(CardPalettes.Get(_paletteIndex).Bg);
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
            // 不挂悬停提示：提示窗会盖住卡片下缘，挡住“悬停卡片弹出列表”的触发
        }

        // 宽度按当前语言标题实测（TextRenderer=GDI 与绘制一致；MeasureString 偏窄会截字）
        private void _fnLayout()
        {
            // 必须用与渲染相同的字体测宽，否则配色方案里字号/字体偏大时"历史"被省略号截断；
            // 再留出徽标、间距、阴影与测量误差的余量，保证标题完整显示。
            var pal = CardPalettes.Get(_paletteIndex);
            string fn = pal != null && !string.IsNullOrEmpty(pal.FontName) ? pal.FontName : Config.FontName;
            float fs = pal != null && pal.FontSize > 0 ? pal.FontSize : 9f;
            using (var f = new Font(fn, fs))
            {
                var tw = TextRenderer.MeasureText(I18n.T("entry_title"), f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                ClientSize = new Size(Dpi.S(12) + tw.Width + Dpi.S(8) + Dpi.S(16) + Dpi.S(10) + ShadowPad + Dpi.S(6), CardH);
            }
        }

        // 语言/主题切换：重测宽度并重绘（Program 侧调用）
        public void Relayout()
        {
            if (IsDisposed) return;
            _fnLayout();
            _fnApplyPalette();
            Invalidate();
        }

        // ---- 配色（与桌面卡片同体系，滚轮循环切换）----
        // 折叠卡片走分层窗口全自绘：配色应用=重绘，而非改 BackColor/Region
        private void _fnApplyPalette()
        {
            if (IsDisposed || !IsHandleCreated || Width <= 0 || Height <= 0) return;
            _fnRender();
        }

        // 圆角跟随当前配色方案的 radius（0=直角）
        private static System.Drawing.Drawing2D.GraphicsPath _fnRoundPath(Rectangle r, int rad)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            if (rad <= 0) { path.AddRectangle(r); return path; }
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 与桌面卡片一致：去掉系统 CS_DROPSHADOW 直角阴影 + DWM 边框，自绘圆角/阴影
            Win11.RemoveDropShadow(Handle);
            Win11.ForceSquareCorners(Handle);
            Win11.RemoveNcBorder(Handle);
            _fnRender();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _fnApplyPalette();
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

        // 全自绘到 32bppArgb 位图 → UpdateLayeredWindow（逐像素 Alpha），与桌面卡片同一渲染体系：
        // 圆角抗锯齿、自绘软阴影、色条按 StripWidth/StripColor.A、字体/徽标全部跟随配色方案。
        private void _fnRender()
        {
            if (IsDisposed || !IsHandleCreated || Width <= 0 || Height <= 0) return;
            int w = Width, h = Height;
            int cw = w - ShadowPad, ch = h - ShadowPad;   // 右下留给自绘阴影
            var pal = CardPalettes.Get(_paletteIndex);
            int radius = pal != null ? Dpi.S(Math.Max(0, pal.Radius)) : 0;
            Color bg = pal != null ? (_hover ? pal.Hover : pal.Bg) : Config.BG;
            Color fg = pal != null ? pal.Fg : Config.FG;
            Color strip = pal != null ? pal.StripColor : Config.Accent;
            int stripW = pal != null ? pal.StripWidth : 1;
            Color badgeBg = pal != null ? pal.BadgeBg : Config.Accent;
            Color badgeFg = pal != null ? pal.BadgeFg : Config.BG;
            string fontName = pal != null && !string.IsNullOrEmpty(pal.FontName) ? pal.FontName : Config.FontName;
            float fontSize = pal != null && pal.FontSize > 0 ? pal.FontSize : 9f;

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // 自绘软阴影（右下偏移，跟着圆角走；不再用系统 CS_DROPSHADOW 直角阴影）
                for (int i = ShadowPad; i > 0; i--)
                    using (var sp = _fnRoundPath(new Rectangle(i, i, cw, ch), radius))
                    using (var sb = new SolidBrush(Color.FromArgb(30, 0, 0, 0)))
                        g.FillPath(sb, sp);

                using (var path = _fnRoundPath(new Rectangle(0, 0, cw, ch), radius))
                {
                    // 背景（保留配色 Alpha，不再 Opaque 拍平）
                    if (bg.A > 0)
                        using (var b = new SolidBrush(bg)) g.FillPath(b, path);

                    // 左侧色条：完全按StripWidth/StripColor.A（0 或透明则不画），裁剪进圆角
                    if (stripW > 0 && strip.A > 0)
                    {
                        g.SetClip(path);
                        using (var b = new SolidBrush(strip)) g.FillRectangle(b, 0, 0, stripW, ch);
                        g.ResetClip();
                    }

                    // 徽标 = 五角星（右端），底色/字色取配色方案 BadgeBg/BadgeFg
                    var badge = new Rectangle(Math.Max(6, cw - Dpi.S(10) - 16), 0, 16, ch);
                    if (badgeBg.A > 0)
                        using (var bp = _fnRoundPath(badge, radius))
                        using (var b = new SolidBrush(badgeBg)) g.FillPath(b, bp);
                    using (var f = new Font(fontName, 11f))
                    using (var b = new SolidBrush(badgeFg))
                    using (var sf2 = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("★", f, b, badge, sf2);

                    // 标题：左端，配色字体名/字号，给徽标留空间
                    using (var f = new Font(fontName, fontSize))
                    using (var b = new SolidBrush(fg))
                    {
                        var tr = new Rectangle(Dpi.S(12), 0, Math.Max(10, badge.Left - Dpi.S(8) - Dpi.S(12)), ch);
                        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                            g.DrawString(I18n.T("entry_title"), f, b, tr, sf);
                    }
                }

                // 预乘 alpha（AC_SRC_ALPHA 期望预乘）
                byte[] px;
                {
                    var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                    px = new byte[bd.Stride * h];
                    Marshal.Copy(bd.Scan0, px, 0, px.Length);
                    for (int k = 0; k < px.Length; k += 4)
                    {
                        byte a = px[k + 3];
                        if (a < 255)
                        {
                            px[k] = (byte)(px[k] * a / 255);
                            px[k + 1] = (byte)(px[k + 1] * a / 255);
                            px[k + 2] = (byte)(px[k + 2] * a / 255);
                        }
                    }
                    Marshal.Copy(px, 0, bd.Scan0, px.Length);
                    bmp.UnlockBits(bd);
                }

                // 推送到分层窗口（CreateDIBSection 直写预乘字节，避免 GetHbitmap 白边）
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
        public int Index = -1;              // 当前列表中的行序号（Shift 区间选择用）
        public new string Name;
        public Font SubFont;
        public Color BaseColor;
        public bool Selected;   // Ctrl+点击多选态
        public string Highlight = ""; // 搜索命中片段
        public bool ReadOnly;         // 大纲模式：条目只读，仅分组可编辑
        public bool IsLinkRow;        // 链接行：文本用强调色+下划线，禁用固定卡片
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
                Size = Dpi.Sz(18, 18),
                Location = new Point(-Dpi.S(40), Dpi.S(3)),
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

        private Rectangle StarRect { get { return new Rectangle(Dpi.S(24), 0, Dpi.S(20), Height); } }
        public bool IsStarPoint(Point p) { return StarRect.Contains(p); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_btnPin != null) _btnPin.Left = Width - Dpi.S(20);
            if (_btnCopy != null) _btnCopy.Left = Width - Dpi.S(40);
            if (_btnRename != null) _btnRename.Left = Width - Dpi.S(60);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // 按钮底色取当前行底（悬停高亮 ListHv），与行浑然一体
            foreach (var b in new[] { _btnRename, _btnCopy, _btnPin }) b.BackColor = BackColor;
            foreach (var b in new[] { _btnRename, _btnCopy, _btnPin }) b.Visible = true;
            if (ReadOnly && !IsLinkRow) _btnRename.Visible = false;   // 链接行始终可改文本
            if (IsLinkRow) _btnPin.Visible = false;
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

        protected override void OnPaint(PaintEventArgs e) { PaintTo(e.Graphics); }

        internal void PaintTo(Graphics g)
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (Selected)
            {
                using (var b = new SolidBrush(Color.FromArgb(48, Config.Accent.R, Config.Accent.G, Config.Accent.B)))
                    g.FillRectangle(b, 0, 0, Width, Height);
                using (var b = new SolidBrush(Config.Accent)) g.FillRectangle(b, 0, 0, 3, Height);
            }
            var icon = IsLinkRow ? LinkEntry.Icon(Config.ListAc) : PopupForm.GetEntryIconFor(Full);
            int iconSz = Dpi.S(16);
            if (icon != null) g.DrawImage(icon, new Rectangle(Dpi.S(6), (Height - iconSz) / 2, iconSz, iconSz));
            // 星标：收藏=实心金（与桌面卡片★徽章同色），历史=描边弱化；悬停提亮
            var sf = SubFont ?? Font;
            var starColor = IsFav ? StarLit : (_inStar ? Config.ListFg : Config.ListMu);
            TextRenderer.DrawText(g, IsFav ? "★" : "☆", sf, StarRect, starColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            // 名称限制在行宽内，过长中间省略；DrawString 抗锯齿（TextRenderer 在双缓冲位图上 ClearType 退化出锯齿）
            float availW = Width - Dpi.S(46) - Dpi.S(6);
            var displayName = availW > 20 ? RowLabel.MidEllipsis(g, Name, Font, availW) : Name;
            g.SetClip(new RectangleF(Dpi.S(46), 0, Math.Max(1, Width - Dpi.S(52)), Height));
            int hit = string.IsNullOrEmpty(Highlight) ? -1 :
                displayName.IndexOf(Highlight, StringComparison.OrdinalIgnoreCase);
            if (IsLinkRow)
            {
                // 链接文本：强调色 + 自绘下划线（省略号出现时下划线仍贴合文字宽度）
                float ly = (Height - Font.Height) / 2f;
                using (var b = new SolidBrush(Config.ListAc)) g.DrawString(displayName, Font, b, Dpi.S(46), ly);
                float tw = g.MeasureString(displayName, Font, int.MaxValue, StringFormat.GenericTypographic).Width - 1;
                using (var p = new Pen(Config.ListAc, 1f))
                    g.DrawLine(p, Dpi.S(46), ly + Font.Height - 2.5f, Dpi.S(46) + tw, ly + Font.Height - 2.5f);
            }
            else if (hit >= 0)
            {
                float x = Dpi.S(46);
                float y = (Height - Font.Height) / 2;
                string pre = displayName.Substring(0, hit);
                string mid = displayName.Substring(hit, Highlight.Length);
                string post = displayName.Substring(hit + Highlight.Length);
                using (var b = new SolidBrush(ForeColor)) { g.DrawString(pre, Font, b, x, y); x += g.MeasureString(pre, Font).Width; }
                using (var f = new Font(Font, FontStyle.Bold))
                using (var b = new SolidBrush(Config.Accent)) { g.DrawString(mid, f, b, x, y); x += g.MeasureString(mid, f).Width; }
                using (var b = new SolidBrush(ForeColor)) g.DrawString(post, Font, b, x, y);
            }
            else
            {
                using (var b = new SolidBrush(ForeColor)) g.DrawString(displayName, Font, b, Dpi.S(46), (Height - Font.Height) / 2);
            }
            g.ResetClip();
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
        // 双缓冲在首次弹出有 AV 崩溃记录（同 EntryListForm/EntryRow），改直绘
        public SideTab() { DoubleBuffered = false; }
        protected override void OnPaintBackground(PaintEventArgs e) { }
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 去掉系统/自绘硬边投影，避免对话框下/右缘出现多余阴影
            Win11.RemoveDropShadow(Handle);
        }
    }
}