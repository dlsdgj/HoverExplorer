using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HoverExplorer
{
    // 链接条目 md 备注里的图片附件：统一落到程序目录下的 link_assets/，
    // markdown 文本里以相对路径 link_assets/xxx.png 引用，随 config.json 一起走。
    internal static class LinkAssets
    {
        internal const string Dir = "link_assets";

        internal static string DirFull
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Dir); }
        }

        // 保存剪贴板图片，返回可写进 md 的相对引用
        internal static string SaveImage(Image img)
        {
            string dir = DirFull;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string name = "img_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
            string full = Path.Combine(dir, name);
            using (var bmp = new Bitmap(img)) bmp.Save(full, ImageFormat.Png);
            return Dir + "/" + name;
        }

        // 保存图片字节（浏览器拖出的 PNG/JPEG/DIB 等），返回可写进 md 的相对引用
        internal static string SaveImageBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            // 跳过 CF_HTML 之类的文本误入
            if (bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return null;
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
                return SaveImage(img);
        }

        // 从 CF_DIB/DeviceIndependentBitmap 字节构造 Image：DIB = BITMAPINFOHEADER + 像素，
        // 前面补一个 BITMAPFILEHEADER 即可交给 GDI+ 解析。
        internal static Image ImageFromDib(byte[] dib)
        {
            if (dib == null || dib.Length < 40) return null;
            int headerSize = BitConverter.ToInt32(dib, 0);
            if (headerSize < 40 || headerSize > dib.Length) return null;
            // 计算像素数据偏移：headerSize + 调色板(若 bpp<=8)
            short bpp = BitConverter.ToInt16(dib, 14);
            int clrUsed = BitConverter.ToInt32(dib, 32);
            int palette = 0;
            if (bpp <= 8)
            {
                int colors = clrUsed > 0 ? clrUsed : (1 << bpp);
                palette = colors * 4;
            }
            int pixelOffset = 14 + headerSize + palette;
            byte[] bmp = new byte[14 + dib.Length];
            bmp[0] = 0x42; bmp[1] = 0x4D;                       // 'BM'
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);    // bfSize
            BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);  // bfOffBits
            Array.Copy(dib, 0, bmp, 14, dib.Length);
            using (var ms = new MemoryStream(bmp))
                return Image.FromStream(ms);
        }

        // 从图片 URL 下载字节（同步，带超时），供拖放浏览器图片链接兜底
        internal static byte[] DownloadBytes(string url, int timeoutMs = 8000)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = WebRequest.Create(url) as HttpWebRequest;
                if (req == null) return null;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "Mozilla/5.0";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var ms = new MemoryStream())
                {
                    resp.GetResponseStream().CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        // 把 md 里可能存在的相对路径解析成绝对路径
        internal static string Resolve(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return rel;
            if (Path.IsPathRooted(rel)) return rel;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        // 收集所有条目里被引用的 link_assets 文件名
        internal static HashSet<string> CollectRefs(IEnumerable<string> entries)
        {
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var re = new Regex(@"!\[[^\]]*\]\(([^)]+)\)", RegexOptions.Compiled);
            string prefix = Dir + "/";
            if (entries == null) return refs;
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e)) continue;
                foreach (Match m in re.Matches(e))
                {
                    string p = m.Groups[1].Value.Trim();
                    if (p.IndexOf('/') < 0 && p.IndexOf('\\') < 0) p = prefix + p;
                    if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        refs.Add(Path.GetFileName(p.Replace('\\', '/')));
                }
            }
            return refs;
        }

        // 删除没有任何条目引用的孤立附件
        internal static void Cleanup(IEnumerable<string> entries)
        {
            try
            {
                string dir = DirFull;
                if (!Directory.Exists(dir)) return;
                var refs = CollectRefs(entries);
                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    if (refs.Contains(Path.GetFileName(f))) continue;
                    // 宽限期：刚生成 10 分钟内的图片不删，避免引用收集有遗漏时误删新鲜附件
                    try { if ((DateTime.Now - File.GetCreationTime(f)).TotalMinutes < 10) continue; } catch { }
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }
    }
}
