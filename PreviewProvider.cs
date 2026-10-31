using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Xml;
using WpfBmp = System.Windows.Media.Imaging;

namespace HoverExplorer
{
    internal class PreviewResult
    {
        public enum KindEnum { Image, Text, Media, SvgHtml }
        public KindEnum Kind;
        public Image Image;
        public bool IsGif;
        public bool IsVideo;
        public int DisplayW;
        public int DisplayH;
        public string Text;
        public long Size;
    }

    internal static class PreviewProvider
    {
        // 该扩展名属于哪一类预览——与设置面板里的分类开关一一对应
        internal static Config.PreviewKind KindOf(string ext)
        {
            if (Config.ImgExts.Contains(ext) || Config.IcoExts.Contains(ext)) return Config.PreviewKind.Image;
            if (Config.VideoExts.Contains(ext)) return Config.PreviewKind.Video;
            if (Config.AudioExts.Contains(ext)) return Config.PreviewKind.Audio;
            if (Config.OfficeExts.Contains(ext) || Config.EpubExts.Contains(ext)) return Config.PreviewKind.Document;
            if (Config.FontExts.Contains(ext)) return Config.PreviewKind.Font;
            if (Config.ZipExts.Contains(ext) || Config.SevenZipExts.Contains(ext)) return Config.PreviewKind.Archive;
            if (Config.TextExts.Contains(ext) || ext == ".csv") return Config.PreviewKind.Text;  // .md/.markdown 在 Generate 里单独走渲染
            return Config.PreviewKind.Other;
        }

        public static bool CanPreview(string path)
        {
            if (path == null) return false;
            if (!File.Exists(path)) return false;
            // 分类开关：该类被关掉就直接不预览——既不占悬停延时，也不弹空窗
            return Config.PreviewEnabled(KindOf(Path.GetExtension(path).ToLowerInvariant()));
        }

        public static PreviewResult Generate(string full)
        {
            string ext = Path.GetExtension(full).ToLowerInvariant();
            // 该类预览被关掉 → 不生成任何内容
            if (!Config.PreviewEnabled(KindOf(ext))) return null;
            if (Config.ImgExts.Contains(ext) || Config.IcoExts.Contains(ext)) return GenImage(full);
            if (Config.VideoExts.Contains(ext)) return GenVideo(full);
            if (Config.SvgExts.Contains(ext)) return GenSvg(full);
            if (Config.AudioExts.Contains(ext)) return GenAudio(full);
            if (Config.FontExts.Contains(ext)) return GenFont(full);
            if (Config.OfficeExts.Contains(ext)) return GenOffice(full, ext);
            if (Config.ZipExts.Contains(ext)) return GenZip(full);
            if (Config.SevenZipExts.Contains(ext)) return GenSevenZip(full);
            if (Config.ExeExts.Contains(ext)) return GenExe(full);
            if (Config.LnkExts.Contains(ext)) return GenLnk(full);
            if (Config.EpubExts.Contains(ext)) return GenEpub(full);
            if (ext == ".csv") return GenCsv(full);
            if (Config.TextExts.Contains(ext)) return GenText(full);
            return GenMeta(full);
        }

        // GDI+ 不支持的格式（webp/heic/avif 等）用 WPF/WIC 解码，零额外依赖
        private static Bitmap LoadViaWic(string full)
        {
            try
            {
                var dec = WpfBmp.BitmapDecoder.Create(
                    new Uri(full),
                    WpfBmp.BitmapCreateOptions.None,
                    WpfBmp.BitmapCacheOption.OnLoad);
                if (dec.Frames.Count == 0) return null;
                System.Windows.Media.Imaging.BitmapSource src = dec.Frames[0];
                if (src.PixelWidth <= 0 || src.PixelHeight <= 0) return null;
                if (src.Format != System.Windows.Media.PixelFormats.Bgra32)
                {
                    var conv = new WpfBmp.FormatConvertedBitmap();
                    conv.BeginInit();
                    conv.Source = src;
                    conv.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
                    conv.EndInit();
                    src = conv;
                }
                int w = src.PixelWidth, h = src.PixelHeight;
                int stride = w * 4;
                byte[] px = new byte[stride * h];
                src.CopyPixels(px, stride, 0);
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
                Marshal.Copy(px, 0, data.Scan0, px.Length);
                bmp.UnlockBits(data);
                return bmp;
            }
            catch { return null; }
        }

        // ===== 图片 / 图标 / TIFF =====
        // ffmpeg 单帧兜底：解成 BMP 流交回 GDI+。ffmpeg 不存在/解不动返回 null（上层按无预览处理）
        private static Image LoadViaFfmpeg(string full)
        {
            string ff = FfmpegTools.FindFfmpeg();
            if (ff == null) return null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ff,
                    Arguments = "-v error -i \"" + full + "\" -frames:v 1 -f image2pipe -vcodec bmp -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var ms = new MemoryStream();
                    p.StandardOutput.BaseStream.CopyTo(ms);
                    p.WaitForExit(8000);
                    if (ms.Length == 0) return null;
                    var bytes = ms.ToArray();          // 拷贝出定长缓冲：Image 生命周期内流必须保持存活
                    var mem = new MemoryStream(bytes);
                    return Image.FromStream(mem);
                }
            }
            catch { return null; }
        }

        private static PreviewResult GenImage(string full)
        {
            Image img;
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext == ".ico")
            {
                try { using (var icon = new Icon(full)) img = icon.ToBitmap(); }
                catch { img = Image.FromFile(full); }
            }
            else
            {
                try
                {
                    img = Image.FromFile(full);
                    if ((ext == ".tiff" || ext == ".tif") && img.FrameDimensionsList.Length > 0)
                    {
                        var dim = new FrameDimension(img.FrameDimensionsList[0]);
                        img.SelectActiveFrame(dim, 0);
                    }
                }
                catch
                {
                    img = LoadViaWic(full);
                    if (img == null) img = LoadViaFfmpeg(full);   // WIC 也没有：EXR/DPX/TGA/QOI 等交给 ffmpeg
                    if (img == null) throw;
                }
            }
            int frameCount = 1;
            bool isGif = false;
            if (img.FrameDimensionsList.Length > 0)
            {
                var dim = new FrameDimension(img.FrameDimensionsList[0]);
                frameCount = img.GetFrameCount(dim);
                isGif = img.RawFormat.Equals(ImageFormat.Gif) && frameCount > 1;
            }
            if (Config.PreviewOriginalSize)
            {
                return new PreviewResult { Kind = PreviewResult.KindEnum.Image, Image = img, IsGif = isGif, DisplayW = img.Width, DisplayH = img.Height };
            }
            int limit = Math.Max(Config.PreviewMax, Config.PreviewMinWidth);
            double ratio = Math.Min((double)limit / img.Width, (double)limit / img.Height);
            if (ratio > 1) ratio = 1;
            int w = (int)(img.Width * ratio), h = (int)(img.Height * ratio);
            if (isGif)
            {
                return new PreviewResult { Kind = PreviewResult.KindEnum.Image, Image = img, IsGif = true, DisplayW = w, DisplayH = h };
            }
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, w, h);
            }
            img.Dispose();
            return new PreviewResult { Kind = PreviewResult.KindEnum.Image, Image = bmp };
        }

        // ===== 视频/音频：WPF MediaElement 播放器（零部署，Media Foundation 解码）=====
        private static PreviewResult GenVideo(string full)
        {
            // 初始尺寸，打开后按视频真实宽高比再校正（见 PreviewForm.FitVideoAspect）
            return new PreviewResult { Kind = PreviewResult.KindEnum.Media, IsVideo = true, DisplayW = 360, DisplayH = 240 };
        }

        // SVG：IE 引擎渲染图形 + 下方显示源码（深色模式图形垫浅色底）
        private static PreviewResult GenSvg(string full)
        {
            return new PreviewResult { Kind = PreviewResult.KindEnum.SvgHtml, Text = full };
        }

        private static PreviewResult ThumbResult(Image img)
        {
            if (img == null) return null;
            double ratio = Math.Min((double)Config.PreviewMax / img.Width, (double)Config.PreviewMax / img.Height);
            if (ratio > 1) ratio = 1;
            if (ratio >= 1) return new PreviewResult { Kind = PreviewResult.KindEnum.Image, Image = img };
            int w = (int)(img.Width * ratio), h = (int)(img.Height * ratio);
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, w, h);
            }
            img.Dispose();
            return new PreviewResult { Kind = PreviewResult.KindEnum.Image, Image = bmp };
        }

        private static PreviewResult GenAudio(string full)
        {
            return new PreviewResult { Kind = PreviewResult.KindEnum.Media, IsVideo = false, DisplayW = 300, DisplayH = 90 };
        }

        // ===== 字体样张 =====
        // 关键：PrivateFontCollection 必须存活到 DrawString 完成，且 Font 必须用 FontFamily 对象构造
        // （new Font(string, float) 只查系统字体集合，不查私有集合）
        private static PreviewResult GenFont(string full)
        {
            int W = Config.PreviewMax * 2, H = Config.PreviewMax * 2;
            var bmp = new Bitmap(W, H);
            var pfc = new PrivateFontCollection();
            try { pfc.AddFontFile(full); }
            catch { pfc.Dispose(); bmp.Dispose(); return null; }
            FontFamily family;
            try { family = pfc.Families[0]; }
            catch { pfc.Dispose(); bmp.Dispose(); return null; }
            string familyName = family.Name;
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Config.BG);
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                var sample = "The quick brown fox jumps over the lazy dog";
                var sample2 = "天地玄黄 宇宙洪荒 0123456789";
                var sizes = new[] { 22f, 16f, 12f, 9f };
                float y = 8;
                using (var brush = new SolidBrush(Config.FG))
                {
                    foreach (var sz in sizes)
                    {
                        using (var font = new Font(family, sz))
                        {
                            g.DrawString(sample, font, brush, 8, y);
                            y += font.GetHeight(g) + 4;
                            g.DrawString(sample2, font, brush, 8, y);
                            y += font.GetHeight(g) + 8;
                        }
                    }
                }
                using (var font = new Font(Config.FontName, 9f))
                using (var brush = new SolidBrush(Config.FgDir))
                    g.DrawString(I18n.T("font_label") + familyName, font, brush, 8, y);
            }
            pfc.Dispose();
            return new PreviewResult { Kind = PreviewResult.KindEnum.Image, Image = bmp };
        }

        // ===== Office 文档文本提取（.docx/.xlsx/.pptx，zip+xml） =====
        private static PreviewResult GenOffice(string full, string ext)
        {
            var sb = new StringBuilder();
            const int MaxChars = 8000;
            try
            {
                using (var za = ZipFile.OpenRead(full))
                {
                    if (ext == ".docx")
                    {
                        var entry = za.GetEntry("word/document.xml");
                        if (entry != null) ExtractXmlText(entry, sb, MaxChars, "t");
                    }
                    else if (ext == ".xlsx")
                    {
                        GenXlsx(za, sb, MaxChars);
                    }
                    else if (ext == ".pptx")
                    {
                        for (int i = 1; i <= 50; i++)
                        {
                            var entry = za.GetEntry("ppt/slides/slide" + i + ".xml");
                            if (entry == null) continue;
                            if (sb.Length > 0) sb.AppendLine();
                            sb.AppendLine(I18n.T("slide", i));
                            ExtractXmlText(entry, sb, MaxChars - sb.Length, "t");
                            if (sb.Length >= MaxChars) break;
                        }
                    }
                }
            }
            catch { }
            if (sb.Length == 0) sb.AppendLine(I18n.T("office_no_text"));
            return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = sb.ToString(), Size = new FileInfo(full).Length };
        }

        // ===== xlsx：按 工作表→行→单元格 流式解析。
        // 旧实现只读 xl/sharedStrings.xml：数字/日期单元格全部丢失、内联字符串（WPS/导出工具
        // 生成的表格）直接为空、顺序也是字符串池序而非表格序——用户看就是"预览不了"。
        // 状态机用提交式：ReadElementContentAsString 会连同闭标签一起越过（EndElement c/row
        // 不保证可见）→ 单元格在新 c/row 开始、循环收尾时提交
        private static void GenXlsx(ZipArchive za, StringBuilder sb, int maxChars)
        {
            // 1. 共享字符串池（si 内可能有多段富文本 <r><t>，全量拼接）
            var sst = new List<string>();
            var sstEntry = za.GetEntry("xl/sharedStrings.xml");
            if (sstEntry != null)
                using (var rs = sstEntry.Open())
                using (var r = XmlReader.Create(rs))
                    while (r.Read())
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "si")
                        {
                            var buf = new StringBuilder();
                            using (var sub = r.ReadSubtree())
                                while (sub.Read())
                                    if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "t")
                                        buf.Append(sub.ReadElementContentAsString());
                            sst.Add(buf.ToString());
                        }

            // 2. workbook.xml：表名 + 关系 Id（按 workbook 声明顺序）
            var sheetNames = new List<string>();
            var sheetRids = new List<string>();
            var wbEntry = za.GetEntry("xl/workbook.xml");
            if (wbEntry != null)
                using (var rs = wbEntry.Open())
                using (var r = XmlReader.Create(rs))
                    while (r.Read())
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                        {
                            sheetNames.Add(r.GetAttribute("name") ?? "");
                            sheetRids.Add(r.GetAttribute("r:id") ?? r.GetAttribute("id") ?? "");
                        }

            // 3. 关系表：rId → parts 路径（相对路径补 xl/ 前缀，绝对路径去掉开头 /）
            var rels = new Dictionary<string, string>();
            var relEntry = za.GetEntry("xl/_rels/workbook.xml.rels");
            if (relEntry != null)
                using (var rs = relEntry.Open())
                using (var r = XmlReader.Create(rs))
                    while (r.Read())
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship")
                            rels[r.GetAttribute("Id")] = r.GetAttribute("Target");

            // 4. 逐表逐行：t=s→共享串 / t=inlineStr→<is><t> / t=b→TRUE/FALSE / 其余按原值
            for (int si = 0; si < sheetNames.Count && sb.Length < maxChars; si++)
            {
                string target;
                if (!rels.TryGetValue(sheetRids[si], out target)) continue;
                if (target.StartsWith("/")) target = target.TrimStart('/');
                else if (!target.StartsWith("xl/")) target = "xl/" + target;
                var entry = za.GetEntry(target);
                if (entry == null) continue;
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("【" + (sheetNames[si].Length > 0 ? sheetNames[si] : target) + "】");
                using (var rs = entry.Open())
                using (var r = XmlReader.Create(rs))
                {
                    var row = new StringBuilder();
                    string cellType = "n", val = null;
                    bool inlineText = false, cellOpen = false;
                    Action commitCell = delegate
                    {
                        if (!cellOpen) return;
                        cellOpen = false;
                        string text;
                        if (inlineText) text = val ?? "";
                        else if (cellType == "s" && val != null)
                        {
                            int ssi;
                            text = int.TryParse(val, out ssi) && ssi >= 0 && ssi < sst.Count ? sst[ssi] : "";
                        }
                        else if (cellType == "b") text = val == "1" ? "TRUE" : "FALSE";
                        else text = val ?? "";
                        if (!string.IsNullOrEmpty(text)) { if (row.Length > 0) row.Append('	'); row.Append(text); }
                    };
                    Action commitRow = delegate
                    {
                        commitCell();
                        if (row.Length > 0) { sb.AppendLine(row.ToString()); row.Length = 0; }
                    };
                    while (r.Read() && sb.Length < maxChars)
                    {
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "c")
                        {
                            commitCell();
                            cellType = r.GetAttribute("t") ?? "n";
                            val = null; inlineText = false; cellOpen = true;
                        }
                        else if (r.NodeType == XmlNodeType.Element && r.LocalName == "is") inlineText = true;
                        else if (r.NodeType == XmlNodeType.Element && r.LocalName == "v")
                            val = r.ReadElementContentAsString();
                        else if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && inlineText)
                            val = (val ?? "") + r.ReadElementContentAsString();
                        else if (r.NodeType == XmlNodeType.Element && r.LocalName == "row")
                            commitRow();
                    }
                    commitRow();
                }
            }
        }

        private static void ExtractXmlText(ZipArchiveEntry entry, StringBuilder sb, int maxChars, string textElementLocalName)
        {
            using (var stream = entry.Open())
            using (var reader = XmlReader.Create(stream))
            {
                while (reader.Read() && sb.Length < maxChars)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == textElementLocalName)
                    {
                        string text = reader.ReadElementContentAsString();
                        if (!string.IsNullOrEmpty(text)) sb.AppendLine(text);
                    }
                }
            }
        }

        // ===== zip 清单 =====
        private static PreviewResult GenZip(string full)
        {
            var sb = new StringBuilder();
            int total;
            using (var za = ZipFile.OpenRead(full))
            {
                total = za.Entries.Count;
                int n = 0;
                foreach (var e in za.Entries)
                {
                    if (n >= 200) break;
                    sb.AppendLine(string.Format("{0,-10} {1}", FmtSize(e.Length), e.FullName));
                    n++;
                }
            }
            if (total > 200) sb.AppendLine(I18n.T("total_items", total));
            return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = sb.ToString(), Size = new FileInfo(full).Length };
        }

        // ===== 7z / rar（调用 Bandizip 控制台版 bz.exe，l 命令只在 bz.exe 可用） =====
        // 列宽因格式而异（zip 11 列、7z 6 列），需动态从表头找 Name/Size 列位置
        private static PreviewResult GenSevenZip(string full)
        {
            var exe = FindBzExe();
            if (exe == null)
                return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = I18n.T("no_bandizip"), Size = new FileInfo(full).Length };

            string output;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "l -list:v -consolemode:utf8 \"" + full + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                };
                using (var p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } }
                }
            }
            catch (Exception ex)
            {
                return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = I18n.T("bz_failed") + ex.Message, Size = new FileInfo(full).Length };
            }

            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int nameCol = -1, sizeStart = -1, sizeEnd = -1;
            bool inData = false;
            int sepCount = 0;
            var entries = new List<Tuple<long, string>>();

            foreach (var line in lines)
            {
                if (line.StartsWith("------"))
                {
                    sepCount++;
                    inData = sepCount == 1;
                    continue;
                }
                if (nameCol < 0 && line.Contains("Name") && line.Contains("Size"))
                {
                    nameCol = line.IndexOf("Name");
                    int sizeIdx = line.IndexOf("Size");
                    int compIdx = line.IndexOf("CompSize");
                    if (sizeIdx >= 0 && compIdx > sizeIdx) { sizeStart = sizeIdx; sizeEnd = compIdx; }
                    continue;
                }
                if (inData && line.Length > 0 && char.IsDigit(line[0]))
                {
                    string name = nameCol >= 0 && line.Length > nameCol ? line.Substring(nameCol).Trim() : "";
                    long size = -1;
                    if (sizeStart >= 0 && sizeEnd > sizeStart && line.Length >= sizeEnd)
                        long.TryParse(line.Substring(sizeStart, sizeEnd - sizeStart).Trim(), out size);
                    if (!string.IsNullOrEmpty(name)) entries.Add(Tuple.Create(size, name));
                }
            }

            var sb = new StringBuilder();
            int total = entries.Count;
            int n = 0;
            foreach (var e in entries)
            {
                if (n >= 200) break;
                sb.AppendLine(string.Format("{0,-10} {1}", e.Item1 >= 0 ? FmtSize(e.Item1) : "?", e.Item2));
                n++;
            }
            if (total > 200) sb.AppendLine(I18n.T("total_items", total));
            if (total == 0) sb.AppendLine(I18n.T("empty_archive"));
            return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = sb.ToString(), Size = new FileInfo(full).Length };
        }

        private static string FindBzExe()
        {
            var candidates = new[]
            {
                @"C:\Program Files\Bandizip\bz.exe",
                @"C:\Program Files (x86)\Bandizip\bz.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bandizip", "bz.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Bandizip", "bz.exe"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        // ===== EXE/DLL 版本信息 =====
        private static PreviewResult GenExe(string full)
        {
            var info = FileVersionInfo.GetVersionInfo(full);
            var sb = new StringBuilder();
            sb.AppendLine(I18n.T("file_label") + Path.GetFileName(full));
            if (!string.IsNullOrEmpty(info.FileDescription)) sb.AppendLine(I18n.T("desc_label") + info.FileDescription);
            if (!string.IsNullOrEmpty(info.ProductName)) sb.AppendLine(I18n.T("product_label") + info.ProductName);
            if (!string.IsNullOrEmpty(info.CompanyName)) sb.AppendLine(I18n.T("company_label") + info.CompanyName);
            if (!string.IsNullOrEmpty(info.FileVersion)) sb.AppendLine(I18n.T("version_label") + info.FileVersion);
            if (!string.IsNullOrEmpty(info.OriginalFilename)) sb.AppendLine(I18n.T("origfile_label") + info.OriginalFilename);
            bool managed = false;
            try { AssemblyName.GetAssemblyName(full); managed = true; } catch { }
            if (full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine(I18n.T("type_label") + (managed ? I18n.T("managed_asm") : I18n.T("native_dll")));
            else if (managed)
                sb.AppendLine(I18n.T("type_label") + I18n.T("managed_exe"));
            sb.AppendLine(I18n.T("size_label") + FmtSize(new FileInfo(full).Length));
            return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = sb.ToString(), Size = new FileInfo(full).Length };
        }

        // 解析 lnk 目标路径（ApplyHeader 用）；带缓存避免悬停时重复 COM 调用
        private static readonly Dictionary<string, string> _lnkTargetCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string GetLnkTarget(string full)
        {
            string t;
            if (_lnkTargetCache.TryGetValue(full, out t)) return t;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(shellType);
                var sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { full });
                t = (string)sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) ?? "";
            }
            catch { t = ""; }
            if (_lnkTargetCache.Count > 200) _lnkTargetCache.Clear();
            _lnkTargetCache[full] = t;
            return t;
        }

        // ===== 快捷方式 .lnk（WScript.Shell COM 反射调用，免引用 IWshRuntimeLibrary） =====
        // 预览指向源文件的内容而非元数据：lnk 本质是指针，悬停意图是看目标内容。
        // target 存在且是普通文件 → 递归 Generate(target) 复用全部现有预览能力；
        // lnk→lnk 只解一层防循环；失效/无目标 → 元数据文本（排障仍可用）
        private static PreviewResult GenLnk(string full)
        {
            string target = null, args = null, workDir = null, icon = null, desc = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(shellType);
                var sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { full });
                var scType = sc.GetType();
                target = (string)scType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                args = (string)scType.InvokeMember("Arguments", BindingFlags.GetProperty, null, sc, null);
                workDir = (string)scType.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, sc, null);
                icon = (string)scType.InvokeMember("IconLocation", BindingFlags.GetProperty, null, sc, null);
                desc = (string)scType.InvokeMember("Description", BindingFlags.GetProperty, null, sc, null);
            }
            catch { }

            // 指向源文件的预览：目标存在且是普通文件
            bool targetIsFile = !string.IsNullOrEmpty(target) && File.Exists(target);
            if (targetIsFile)
            {
                string tExt = Path.GetExtension(target).ToLowerInvariant();
                bool targetIsLnk = Config.LnkExts.Contains(tExt);
                if (!targetIsLnk)
                {
                    try { return Generate(target); }
                    catch { }
                }
            }

            // 降级：元数据文本（失效快捷方式 / 指向文件夹 / lnk→lnk）
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(target)) sb.AppendLine(I18n.T("target_label") + target);
            if (!string.IsNullOrEmpty(args)) sb.AppendLine(I18n.T("args_label") + args);
            if (!string.IsNullOrEmpty(workDir)) sb.AppendLine(I18n.T("workdir_label") + workDir);
            if (!string.IsNullOrEmpty(icon)) sb.AppendLine(I18n.T("icon_label") + icon);
            if (!string.IsNullOrEmpty(desc)) sb.AppendLine(I18n.T("desc_label") + desc);
            if (!string.IsNullOrEmpty(target))
                sb.AppendLine(I18n.T("exists_label") + ((File.Exists(target) || Directory.Exists(target)) ? I18n.T("yes") : I18n.T("no")));
            return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = sb.ToString(), Size = new FileInfo(full).Length };
        }

        // ===== EPub 目录（兼容 EPUB2 NCX 和 EPUB3 nav） =====
        private static PreviewResult GenEpub(string full)
        {
            var sb = new StringBuilder();
            try
            {
                using (var za = ZipFile.OpenRead(full))
                {
                    var opfEntry = za.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
                    if (opfEntry != null)
                    {
                        using (var sr = new StreamReader(opfEntry.Open(), Encoding.UTF8))
                        {
                            var opfText = sr.ReadToEnd();
                            var titleMatch = Regex.Match(opfText, "<dc:title[^>]*>([^<]*)</dc:title>");
                            if (titleMatch.Success) sb.AppendLine(I18n.T("title_label") + WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim()));
                            var authorMatch = Regex.Match(opfText, "<dc:creator[^>]*>([^<]*)</dc:creator>");
                            if (authorMatch.Success) sb.AppendLine(I18n.T("author_label") + WebUtility.HtmlDecode(authorMatch.Groups[1].Value.Trim()));
                            var langMatch = Regex.Match(opfText, "<dc:language[^>]*>([^<]*)</dc:language>");
                            if (langMatch.Success) sb.AppendLine(I18n.T("lang_label") + langMatch.Groups[1].Value.Trim());
                        }
                    }
                    sb.AppendLine();
                    sb.AppendLine(I18n.T("toc_header"));
                    var ncxEntry = za.Entries.FirstOrDefault(e => e.FullName.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase));
                    if (ncxEntry != null)
                    {
                        using (var sr = new StreamReader(ncxEntry.Open(), Encoding.UTF8))
                        {
                            var ncxText = sr.ReadToEnd();
                            int n = 0;
                            foreach (Match m in Regex.Matches(ncxText, @"<navLabel>\s*<text>([^<]*)</text>"))
                            {
                                if (n >= 50) { sb.AppendLine("…"); break; }
                                sb.AppendLine("• " + WebUtility.HtmlDecode(m.Groups[1].Value.Trim()));
                                n++;
                            }
                        }
                    }
                    else
                    {
                        var navEntry = za.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nav.xhtml", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".nav.html", StringComparison.OrdinalIgnoreCase));
                        if (navEntry != null)
                        {
                            using (var sr = new StreamReader(navEntry.Open(), Encoding.UTF8))
                            {
                                var navText = sr.ReadToEnd();
                                int n = 0;
                                foreach (Match m in Regex.Matches(navText, @"<a[^>]*href=""[^""]*""[^>]*>([^<]*)</a>"))
                                {
                                    if (n >= 50) { sb.AppendLine("…"); break; }
                                    var t = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
                                    if (!string.IsNullOrEmpty(t)) { sb.AppendLine("• " + t); n++; }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine(I18n.T("parse_failed") + ex.Message + ")"); }
            return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = sb.ToString(), Size = new FileInfo(full).Length };
        }

        // ===== CSV 表格视图（对齐文本，前 50 行） =====
        private static PreviewResult GenCsv(string full)
        {
            var rows = new List<string[]>();
            int maxCols = 0;
            using (var sr = new StreamReader(full, Encoding.UTF8, true))
            {
                for (int i = 0; i < 50; i++)
                {
                    var line = sr.ReadLine();
                    if (line == null) break;
                    var cols = ParseCsvLine(line);
                    rows.Add(cols);
                    if (cols.Length > maxCols) maxCols = cols.Length;
                }
            }
            if (maxCols == 0) return GenText(full);
            var colW = new int[maxCols];
            foreach (var row in rows)
                for (int i = 0; i < row.Length && i < maxCols; i++)
                    if (row[i].Length > colW[i]) colW[i] = row[i].Length;
            var sb = new StringBuilder();
            for (int r = 0; r < rows.Count; r++)
            {
                for (int i = 0; i < maxCols; i++)
                {
                    var cell = i < rows[r].Length ? rows[r][i] : "";
                    sb.Append(cell.PadRight(colW[i] + 2));
                }
                sb.AppendLine();
                if (r == 0) sb.AppendLine(new string('-', Math.Min(maxCols * 12, 80)));
            }
            return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = sb.ToString(), Size = new FileInfo(full).Length };
        }

        private static string[] ParseCsvLine(string line)
        {
            var cols = new List<string>();
            var cur = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuote)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else inQuote = false;
                    }
                    else cur.Append(c);
                }
                else
                {
                    if (c == '"') inQuote = true;
                    else if (c == ',') { cols.Add(cur.ToString()); cur.Clear(); }
                    else cur.Append(c);
                }
            }
            cols.Add(cur.ToString());
            return cols.ToArray();
        }

        // ===== 文本（保留原逻辑：前 200 行，单行截 1000） =====
        private static PreviewResult GenText(string full)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true))
            {
                for (int i = 0; i < 200; i++)
                {
                    string line = sr.ReadLine();
                    if (line == null) break;
                    if (line.Length > 1000) line = line.Substring(0, 1000);
                    lines.Add(line);
                }
                if (sr.ReadLine() != null) lines.Add("…");
            }
            return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = string.Join("\r\n", lines), Size = new FileInfo(full).Length };
        }

        private static PreviewResult GenMeta(string full)
        {
            var sb = new StringBuilder();
            try
            {
                var fi = new FileInfo(full);
                sb.AppendLine(I18n.T("file_label") + fi.Name);
                string ext = fi.Extension;
                sb.AppendLine(I18n.T("type_label") + (string.IsNullOrEmpty(ext) ? "-" : ext));
                sb.AppendLine(I18n.T("size_label") + FmtSize(fi.Length));
                sb.AppendLine(I18n.T("sort_date") + ": " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(I18n.T("origfile_label") + fi.DirectoryName);
            }
            catch (Exception ex)
            {
                sb.AppendLine(I18n.T("parse_failed") + ex.Message + ")");
            }
            return new PreviewResult { Kind = PreviewResult.KindEnum.Text, Text = sb.ToString(), Size = new FileInfo(full).Length };
        }

        private static string FmtSize(long n)
        {
            double d = n;
            string[] units = { "B", "KB", "MB", "GB" };
            for (int i = 0; i < units.Length; i++)
            {
                if (d < 1024 || i == 3)
                    return i == 0 ? string.Format("{0:0} {1}", d, units[i]) : string.Format("{0:0.0} {1}", d, units[i]);
                d /= 1024;
            }
            return string.Format("{0:0.0} GB", d);
        }
    }
}