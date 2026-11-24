using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HoverExplorer
{
    // 音频封面与取色（原型 he-audio-cover.html V5）：
    //   封面优先级 = 文件内嵌 ID3 APIC → 同目录 cover/folder/front → 内置占位。
    //   取色模仿原型 dom()：按 4bit/通道量化分桶，取出现最多且非近黑/近白的桶作为主色，
    //   再由主色派生窗口底色/强调色/淡色，供音频预览窗 V5 换肤使用。
    internal static class AudioArtwork
    {
        internal class Info
        {
            public Image Cover;      // 可能为 null（无封面且占位也失败）
            public bool HasCover;    // 是否来自真实封面（内嵌/同目录），false=占位
            public Color Dominant;   // 主色（占位时为占位图主色）
        }

        private static readonly string[] FileNames = { "cover.jpg", "cover.png", "folder.jpg", "folder.png", "front.jpg", "front.png" };

        // 读取封面：优先内嵌 APIC，其次同目录图片，最后占位。返回的 Image 由调用方 Dispose。
        internal static Info Load(string audioPath)
        {
            var info = new Info { Dominant = Color.Empty };
            if (!string.IsNullOrEmpty(audioPath))
            {
                // 用户拖入卡片替换的封面优先：按音频绝对路径哈希持久化，切歌/重启后仍生效。
                try { info.Cover = ReadCoverOverride(audioPath); } catch { }
                if (info.Cover != null) info.HasCover = true;
                if (info.Cover == null)
                {
                    Image embedded = null;
                    // M4A/MP4 封面在 moov/udta/meta/ilst/covr（不是 ID3），先按容器判断再解析，
                    // 否则 mp3 的 ID3 解析会在 moov 数据里误命中，掩盖真正的 covr。
                    bool isMp4 = IsMp4(audioPath);
                    if (isMp4) { try { embedded = ReadMp4Cover(audioPath); } catch { } }
                    if (embedded == null) { try { embedded = ReadId3Cover(audioPath); } catch { } }
                    if (embedded != null)
                    {
                        info.Cover = embedded; info.HasCover = true;
                    }
                    else
                    {
                        Image side = null;
                        try { side = ReadSideCover(audioPath); } catch { }
                        if (side != null)
                        {
                            info.Cover = side; info.HasCover = true;
                        }
                    }
                }
            }
            if (info.Cover == null)
            {
                try
                {
                    string defaultCover = Path.Combine(
                        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                        "音频默认图片.png");
                    if (!File.Exists(defaultCover))
                        defaultCover = @"C:\Users\Administrator\Desktop\音频默认图片.png";
                    if (File.Exists(defaultCover))
                        using (var fs = new FileStream(defaultCover, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var tmp = Image.FromStream(fs))
                            info.Cover = new Bitmap(tmp);
                } catch { }
                if (info.Cover == null)
                    try { info.Cover = Placeholder(audioPath); } catch { }
                info.HasCover = false;
            }
            if (info.Cover != null)
            {
                try { info.Dominant = Dominant(info.Cover as Bitmap ?? new Bitmap(info.Cover)); } catch { }
            }
            if (info.Dominant.IsEmpty) info.Dominant = Color.FromArgb(0x1f, 0x6f, 0x78);
            return info;
        }

        // 判断是否为 ISO-BMFF（M4A/MP4/M4B）：头部 4 字节后是 "ftyp"。
        private static bool IsMp4(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var b = new byte[12];
                if (fs.Read(b, 0, 12) < 12) return false;
                return b[4] == (byte)'f' && b[5] == (byte)'t' && b[6] == (byte)'y' && b[7] == (byte)'p';
            }
        }

        // 解析 ISO-BMFF 的 moov/udta/meta/ilst/covr/data，取首个图片。
        // box = [4字节长度][4字节类型][负载]；长度=1 时后跟 8 字节 largesize；长度=0 表示到文件尾。
        private static Image ReadMp4Cover(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long fileEnd = fs.Length;
                byte[] covr = FindBoxData(fs, 0, fileEnd, "covr");
                if (covr == null || covr.Length < 8) return null;
                // covr 负载内是若干 data box：前 4 字节 type（0=隐式 jpeg/png，1=jpeg，13=jpeg，14=png），
                // 再 4 字节 locale，剩下是图片字节。
                int p = 0;
                while (p + 8 <= covr.Length)
                {
                    int size = (int)(((uint)covr[p] << 24) | ((uint)covr[p + 1] << 16) | ((uint)covr[p + 2] << 8) | covr[p + 3]);
                    string type = Encoding.ASCII.GetString(covr, p + 4, 4);
                    if (size < 8 || p + size > covr.Length) break;
                    if (type == "data")
                    {
                        int imgOff = p + 16;   // data box: 4 size + 4 "data" + 4 type + 4 locale
                        int imgLen = size - 16;
                        if (imgLen > 8 && imgOff + imgLen <= covr.Length)
                        {
                            var ms = new MemoryStream(covr, imgOff, imgLen, false);
                            try { using (var tmp = Image.FromStream(ms)) return new Bitmap(tmp); }
                            catch { }
                        }
                    }
                    p += size;
                }
            }
            return null;
        }

        // 深度优先查找指定 box，返回其内容字节（不含 8 字节头）。
        private static byte[] FindBoxData(FileStream fs, long start, long end, string want)
        {
            long pos = start;
            var head = new byte[8];
            while (pos + 8 <= end)
            {
                fs.Position = pos;
                if (fs.Read(head, 0, 8) < 8) return null;
                ulong size32 = ((ulong)head[0] << 24) | ((ulong)head[1] << 16) | ((ulong)head[2] << 8) | head[3];
                string type = Encoding.ASCII.GetString(head, 4, 4);
                long size = (long)size32;
                long hdr = 8;
                if (size32 == 1)
                {
                    var ext = new byte[8];
                    if (fs.Read(ext, 0, 8) < 8) return null;
                    size = 0;
                    for (int i = 0; i < 8; i++) size = (size << 8) | ext[i];
                    hdr = 16;
                }
                else if (size32 == 0) size = end - pos;
                if (size <= hdr || pos + size > end) return null;
                if (type == want)
                {
                    var data = new byte[(int)(size - hdr)];
                    fs.Position = pos + hdr;
                    if (fs.Read(data, 0, data.Length) < data.Length) return null;
                    return data;
                }
                // 容器类型才递归
                if (type == "moov" || type == "udta" || type == "meta" || type == "ilst")
                {
                    long childStart = pos + hdr;
                    // meta 在 QuickTime 里前面还有 4 字节 version/flags
                    if (type == "meta") childStart += 4;
                    byte[] hit = FindBoxData(fs, childStart, pos + size, want);
                    if (hit != null) return hit;
                }
                pos += size;
            }
            return null;
        }

        // 解析 ID3v2 的 APIC 帧（v2.2 PIC / v2.3 / v2.4），取首个图片。
        private static Image ReadId3Cover(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var head = new byte[10];
                if (fs.Read(head, 0, 10) < 10) return null;
                if (head[0] != 'I' || head[1] != 'D' || head[2] != '3') return null;
                int ver = head[3];
                if (ver == 0xff) return null;
                bool unsynch = (head[5] & 0x80) != 0;
                long tagSize = Synchsafe(head, 6);
                long end = 10 + tagSize;
                if (end > fs.Length) end = fs.Length;
                int idLen = (ver == 2) ? 3 : 4;
                int sizeLen = (ver == 2) ? 3 : 4;
                var frame = new byte[idLen + sizeLen + 2];
                while (fs.Position + frame.Length <= end)
                {
                    long frameStart = fs.Position;
                    if (fs.Read(frame, 0, frame.Length) < frame.Length) break;
                    bool zero = true;
                    for (int i = 0; i < idLen; i++) if (frame[i] != 0) { zero = false; break; }
                    if (zero) break;
                    string id = Encoding.ASCII.GetString(frame, 0, idLen);
                    long size = ver == 4 ? Synchsafe(frame, idLen) : BigEndian(frame, idLen, sizeLen);
                    int dataSkip = idLen + sizeLen + 2;
                    if (size <= 0 || size > 32 * 1024 * 1024 || frameStart + dataSkip + size > end)
                    {
                        // 畸形帧：至少越过帧头，避免在同一个位置死循环
                        fs.Position = frameStart + dataSkip;
                        continue;
                    }
                    bool isPic = id == "APIC" || id == "PIC";
                    if (!isPic)
                    {
                        // 关键：非图片帧也必须跳过整帧数据。原来直接 continue，下一次循环
                        // 把 TSSE/TXXX 的数据当帧头读，几步就撞上 0 退出，APIC 永远读不到。
                        fs.Position = frameStart + dataSkip + size;
                        continue;
                    }
                    fs.Position = frameStart + dataSkip;
                    var data = new byte[(int)size];
                    if (fs.Read(data, 0, data.Length) < data.Length) return null;
                    if (unsynch) data = DeUnsynch(data);
                    Image img = ParseApic(data, ver, id == "PIC");
                    if (img != null) return img;
                    fs.Position = frameStart + dataSkip + size;
                }
            }
            return null;
        }

        private static Image ParseApic(byte[] d, int ver, bool isPic)
        {
            if (d == null || d.Length < 4) return null;
            int enc = 0;
            int p = 0;
            string mime;
            if (isPic)
            {
                // ID3v2.2 PIC: 3 字节图片格式 + 1 字节图片类型 + 描述 + 数据（无编码字节）
                mime = Encoding.ASCII.GetString(d, p, Math.Min(3, d.Length - p)).TrimEnd('\0');
                p += 3;
            }
            else
            {
                // ID3v2.3/2.4 APIC: 编码 + MIME(终止串) + 1 字节图片类型 + 描述(终止串) + 数据
                enc = d[p++];
                mime = ReadTerminated(d, ref p, enc);
            }
            if (p >= d.Length) return null;
            p++;   // 跳过图片类型（0=其他，3=封面）；以前漏跳，类型 0 时把描述终止符当图像数据起点
            ReadTerminated(d, ref p, enc);   // 描述
            if (p >= d.Length) return null;
            var ms = new MemoryStream(d, p, d.Length - p, false);
            try
            {
                using (var tmp = Image.FromStream(ms))
                    return new Bitmap(tmp);   // 脱离流，独立持有
            }
            catch { return null; }
        }

        private static string ReadTerminated(byte[] d, ref int p, int enc)
        {
            int w = (enc == 1 || enc == 2) ? 2 : 1;
            var sb = new StringBuilder();
            if (w == 1)
            {
                while (p < d.Length && d[p] != 0) { sb.Append((char)d[p]); p++; }
                p++;
            }
            else
            {
                while (p + 1 < d.Length && !(d[p] == 0 && d[p + 1] == 0))
                { sb.Append((char)(d[p] | (d[p + 1] << 8))); p += 2; }
                p += 2;
            }
            return sb.ToString();
        }

        private static byte[] DeUnsynch(byte[] src)
        {
            var list = new List<byte>(src.Length);
            for (int i = 0; i < src.Length; i++)
            {
                list.Add(src[i]);
                if (i + 1 < src.Length && src[i] == 0xFF && src[i + 1] == 0x00) i++;
            }
            return list.ToArray();
        }

        private static Image ReadSideCover(string audioPath)
        {
            try
            {
                string dir = Path.GetDirectoryName(audioPath);
                if (string.IsNullOrEmpty(dir)) return null;
                foreach (var name in FileNames)
                {
                    string p = Path.Combine(dir, name);
                    if (!File.Exists(p)) continue;
                    using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var tmp = Image.FromStream(fs))
                        return new Bitmap(tmp);
                }
            }
            catch { }
            return null;
        }

        // ===== 拖拽图片替换封面：按音频路径哈希存到程序目录 audio_covers/ =====
        // 不改动用户音乐文件本身；切歌、重启都能找回替换后的封面。
        internal static string OverridePath(string audioPath)
        {
            if (string.IsNullOrEmpty(audioPath)) return null;
            using (var sha1 = SHA1.Create())
            {
                string h = BitConverter.ToString(
                    sha1.ComputeHash(Encoding.UTF8.GetBytes(audioPath.ToLowerInvariant())))
                    .Replace("-", "").Substring(0, 24);
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio_covers", h + ".png");
            }
        }

        // 保存/清除/读取用户替换封面。返回 false 仅表示写入失败，不影响继续播放。
        internal static bool SaveCoverOverride(string audioPath, Image img)
        {
            if (string.IsNullOrEmpty(audioPath) || img == null) return false;
            try
            {
                string p = OverridePath(audioPath);
                string dir = Path.GetDirectoryName(p);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = p + ".tmp";
                using (var bmp = new Bitmap(img)) bmp.Save(tmp, ImageFormat.Png);
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
                return true;
            }
            catch { return false; }
        }

        // 把替换封面真正写进音频文件（MP3 的 APIC / M4A 的 covr），
        // 这样资源管理器大图标、其他播放器也能显示，而不只是本程序看的 sidecar。
        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const int SHCNE_UPDATEITEM = 0x00002000;
        private const uint SHCNF_PATHW = 0x0005;

        internal static bool EmbedCoverFile(string audioPath, string coverPath)
        {
            string exe = FfmpegTools.FindFfmpeg();
            if (exe == null || string.IsNullOrEmpty(audioPath) || string.IsNullOrEmpty(coverPath)
                || !File.Exists(audioPath) || !File.Exists(coverPath)) return false;
            string ext = Path.GetExtension(audioPath);
            if (string.IsNullOrEmpty(ext)) ext = ".mp3";
            string tmp = audioPath + ".covertmp" + ext;
            IntPtr pPath = IntPtr.Zero;
            try
            {
                var psi = new ProcessStartInfo(exe);
                psi.Arguments = "-y -loglevel error -i \"" + audioPath + "\" -i \"" + coverPath
                    + "\" -map 0:a -map 1 -map_metadata 0 -c copy -disposition:v:0 attached_pic"
                    // comment=Cover (front) 让 ffmpeg 写 APIC 的 picture type=3，
                    // 否则是 type=0(Other)，资源管理器缩略图不会把它当正面封面显示。
                    + " -metadata:s:v:0 title=\"Album cover\""
                    + " -metadata:s:v:0 comment=\"Cover (front)\" \"" + tmp + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                using (var proc = Process.Start(psi))
                {
                    proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(20000)) { try { proc.Kill(); } catch { } return false; }
                    if (proc.ExitCode != 0) return false;
                }
                if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0) return false;
                File.Copy(tmp, audioPath, true);
                pPath = Marshal.StringToHGlobalUni(audioPath);
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, pPath, IntPtr.Zero);   // 刷新资源管理器缩略图缓存
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);            // SHCNE_ASSOCCHANGED：强制丢弃已缓存的旧缩略图
                return true;
            }
            catch { return false; }
            finally
            {
                if (pPath != IntPtr.Zero) Marshal.FreeHGlobal(pPath);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        internal static bool ClearCoverOverride(string audioPath)
        {
            try
            {
                string p = OverridePath(audioPath);
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return false;
                File.Delete(p);
                return true;
            }
            catch { return false; }
        }

        internal static bool HasCoverOverride(string audioPath)
        {
            try { string p = OverridePath(audioPath); return !string.IsNullOrEmpty(p) && File.Exists(p); }
            catch { return false; }
        }

        private static Image ReadCoverOverride(string audioPath)
        {
            string p = OverridePath(audioPath);
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var tmp = Image.FromStream(fs))
                return new Bitmap(tmp);
        }

        // 无封面时的占位图：底色 + ♪，颜色由文件名哈希稳定派生，避免每次打开都变。
        private static Image Placeholder(string audioPath)
        {
            const int S = 300;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            string name = string.IsNullOrEmpty(audioPath) ? "" : Path.GetFileName(audioPath);
            int h = 0;
            foreach (char c in name) h = (h * 31 + c) & 0x7fffffff;
            // 默认封面图：稳定派生一个低饱和主色，让无封面曲目仍有一张像样的封面
            float defaultHue = h % 360;
            Color baseC = Hsl(defaultHue, .18f, .82f);
            Color ink = Hsl(defaultHue, .28f, .42f);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(baseC);
                using (var b = new SolidBrush(Color.FromArgb(0xFF, 0xF7, 0xDD))) g.FillRectangle(b, 0, 0, S, S);
                using (var b = new SolidBrush(Color.FromArgb(0xE6, 0xDA, 0xB0))) g.FillRectangle(b, 20, 20, S - 40, S - 40);
                using (var f = new Font("Segoe UI", 60f, FontStyle.Bold))
                using (var b = new SolidBrush(ink))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("♪", f, b, new RectangleF(0, 0, S, S), sf);
                }
                using (var f = new Font("Segoe UI", 13f, FontStyle.Bold))
                using (var b = new SolidBrush(ink))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    string ext = string.IsNullOrEmpty(name) ? "AUDIO" : (Path.GetExtension(name).TrimStart('.').ToUpperInvariant());
                    if (ext.Length == 0) ext = "AUDIO";
                    g.DrawString(ext, f, b, new RectangleF(0, S - 70, S, 40), sf);
                }
            }
            return bmp;
        }

        // 原型 dom()：量化到 4bit/通道，取出现次数最多且非近黑/近白的桶。
        internal static Color Dominant(Bitmap bmp)
        {
            if (bmp == null) return Color.Empty;
            using (var small = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, new Rectangle(0, 0, 64, 64));
                }
                var count = new Dictionary<int, int>();
                int bestKey = -1, bestN = 0;
                for (int y = 0; y < small.Height; y++)
                    for (int x = 0; x < small.Width; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        int r = c.R >> 4, g2 = c.G >> 4, b = c.B >> 4;
                        int sum = r + g2 + b;
                        if (sum > 40 || sum < 4) continue;   // 跳过近白/近黑
                        int key = (r << 8) | (g2 << 4) | b;
                        int n;
                        count.TryGetValue(key, out n);
                        n++; count[key] = n;
                        if (n > bestN) { bestN = n; bestKey = key; }
                    }
                if (bestKey < 0) return Color.Empty;
                int rr = ((bestKey >> 8) & 0xF) * 16 + 8;
                int gg = ((bestKey >> 4) & 0xF) * 16 + 8;
                int bb = (bestKey & 0xF) * 16 + 8;
                return Color.FromArgb(rr, gg, bb);
            }
        }

        // ===== V4 整套换肤：从封面提取 HSL 色相 → 派生整套配色（明暗两套）=====

        internal class Skin
        {
            public Color Accent, Bg, Bg2, Fg, Fg2, Muted, Glow, TintC;
        }

        // 严格照抄原型 extract()：40×40，12 个 30° 色相桶，
        // 权重 w = s*(1-|l-0.5|*1.6)，过滤 s<0.25||l<0.15||l>0.9，取最大权重桶的加权平均 h/s。
        private static bool DomHsl(Bitmap bmp, out float hOut, out float sOut, out float lOut, out float satOut)
        {
            hOut = sOut = lOut = 0f; satOut = 0f;
            if (bmp == null) return false;
            using (var small = new Bitmap(40, 40, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, new Rectangle(0, 0, 40, 40));
                }
                float[] bw = new float[12], bh = new float[12], bs = new float[12];
                for (int y = 0; y < 40; y++)
                    for (int x = 0; x < 40; x++)
                    {
                        Color c = small.GetPixel(x, y);
                        float r = c.R / 255f, gg = c.G / 255f, b = c.B / 255f;
                        float M = Math.Max(r, Math.Max(gg, b)), m = Math.Min(r, Math.Min(gg, b));
                        float l = (M + m) / 2f;
                        float s = 0f, h = 0f;
                        float d = M - m;
                        if (d > 1e-6f)
                        {
                            s = l > .5f ? d / (2f - M - m) : d / (M + m);
                            if (M == r) h = ((gg - b) / d + (gg < b ? 6f : 0f));
                            else if (M == gg) h = (b - r) / d + 2f;
                            else h = (r - gg) / d + 4f;
                            h *= 60f;
                        }
                        if (s < .25f || l < .15f || l > .9f) continue;
                        float w = s * (1f - Math.Abs(l - .5f) * 1.6f);
                        int k = ((int)(h / 30f)) % 12;
                        bw[k] += w; bh[k] += h * w; bs[k] += s * w;
                    }
                int best = 0;
                for (int k = 1; k < 12; k++) if (bw[k] > bw[best]) best = k;
                if (bw[best] <= 0f) return false;
                hOut = bh[best] / bw[best];
                sOut = bs[best] / bw[best];
                lOut = .5f;
                satOut = sOut;
                return true;
            }
        }

        // 按原型 palette() 派生整套配色；dark=true 用深色套，false 用浅色套。
        internal static Skin Palette(Image cover, bool dark, float fallbackHue = 205f, float fallbackSat = .35f)
        {
            float h = fallbackHue, s = fallbackSat, l = .5f, sat = Math.Max(.12f, fallbackSat);
            if (cover != null)
            {
                Bitmap bm = cover as Bitmap ?? new Bitmap(cover);
                float hh, ss, ll, sw;
                if (DomHsl(bm, out hh, out ss, out ll, out sw)) { h = hh; s = ss; l = ll; sat = sw; }
            }
            // 原型：sa = clamp(s, .55, .85)
            float sa = Math.Min(.85f, Math.Max(.55f, s));
            if (dark)
                return new Skin
                {
                    Accent = Hsl(h, sa, .62f), Bg = Hsl(h, .28f, .13f), Bg2 = Hsl(h, .32f, .19f),
                    Fg = Hsl(h, .30f, .93f), Fg2 = Hsl(h, .15f, .68f),
                    Muted = HslA(h, .20f, .80f, .18f), Glow = HslA(h, sa, .50f, .40f), TintC = HslA(h, sa, .50f, .16f)
                };
            return new Skin
            {
                Accent = Hsl(h, sa, .40f), Bg = Hsl(h, .45f, .95f), Bg2 = Hsl(h, .42f, .89f),
                Fg = Hsl(h, .40f, .14f), Fg2 = Hsl(h, .22f, .38f),
                Muted = HslA(h, .30f, .30f, .16f), Glow = HslA(h, sa, .50f, .40f), TintC = HslA(h, sa, .50f, .10f)
            };
        }

        private static Color Hsl(float h, float s, float l) { return HslA(h, s, l, 1f); }
        private static Color HslA(float h, float s, float l, float a)
        {
            h = ((h % 360f) + 360f) % 360f / 360f;
            s = Clamp01(s); l = Clamp01(l); a = Clamp01(a);
            float r, g, b;
            if (s <= 0f) { r = g = b = l; }
            else
            {
                float q = l < .5f ? l * (1f + s) : l + s - l * s;
                float p = 2f * l - q;
                r = Hue2(p, q, h + 1f / 3f); g = Hue2(p, q, h); b = Hue2(p, q, h - 1f / 3f);
            }
            return Color.FromArgb((int)(a * 255), (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }
        private static float Hue2(float p, float q, float t)
        {
            if (t < 0f) t += 1f; if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < .5f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
        private static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        // 主色 → 窗口底色（按 t 轻染）
        internal static Color Tint(Color bg, Color dom, float t)
        {
            return Color.FromArgb(
                Mix(bg.R, dom.R, t), Mix(bg.G, dom.G, t), Mix(bg.B, dom.B, t));
        }

        // 主色 → 强调色：压暗 20%，保证在浅/深底上都有对比（原型 dc*0.8）
        internal static Color AccentOf(Color dom)
        {
            return Color.FromArgb((int)(dom.R * 0.8f), (int)(dom.G * 0.8f), (int)(dom.B * 0.8f));
        }

        // 主色 → 淡色：底色与强调色按 25% 混合（原型 --muted 的取法）
        internal static Color MutedOf(Color bg, Color accent)
        {
            return Color.FromArgb(
                Mix(bg.R, accent.R, 0.25f), Mix(bg.G, accent.G, 0.25f), Mix(bg.B, accent.B, 0.25f));
        }

        private static int Mix(int a, int b, float t)
        {
            int v = (int)(a * (1 - t) + b * t);
            if (v < 0) v = 0; else if (v > 255) v = 255;
            return v;
        }

        private static long Synchsafe(byte[] b, int off)
        {
            ulong v = 0;
            for (int i = 0; i < 4; i++) v = (v << 7) | ((ulong)b[off + i] & 0x7fUL);
            return (long)v;
        }

        private static long BigEndian(byte[] b, int off, int len)
        {
            long v = 0;
            for (int i = 0; i < len; i++) v = (v << 8) | b[off + i];
            return v;
        }
    }
}
