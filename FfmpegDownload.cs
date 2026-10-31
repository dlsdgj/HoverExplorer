using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace HoverExplorer
{
    // ffmpeg 自动下载（Mp3Fixer 同款方案，主程序版）：gyan.dev 官方永久链接（始终指向最新
    // Essentials 构建；GitHub latest 固定名会 404，该发布资产名带版本号）。
    // 落点选 FfmpegTools.FindFfmpeg 能自动探测到的位置：
    //   程序目录可写 → exe 同目录（portable 首选，候选顺序里排最前）；
    //   否则（Program Files 等无权限目录）→ %LOCALAPPDATA%\HoverExplorer\
    // 下载完成调 FfmpegTools.ResetCache()，设置里的探测状态立即生效，无需重启。
    internal static class FfmpegDownload
    {
        private static bool _busy;
        internal static bool Busy { get { return _busy; } }

        // 目标路径：优先 exe 同目录（探测顺序第 2 位），写不进去退 %LOCALAPPDATA%\HoverExplorer。
        // 可写性探测必须用独立探针文件：对 ffmpeg.exe 本身 OpenOrCreate 会在未装过 ffmpeg 的
        // 机器上留下 0 字节假 exe，FindFfmpeg 只查 File.Exists 会把它当真 → 兜底解码静默失效
        internal static string TargetExe()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var beside = Path.Combine(dir, "ffmpeg.exe");
            try
            {
                var probe = Path.Combine(dir, "ffmpeg_write_test.tmp");
                using (File.Open(probe, FileMode.Create, FileAccess.Write, FileShare.None)) { }
                try { File.Delete(probe); } catch { }
                return beside;
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"HoverExplorer\ffmpeg.exe");
        }

        // status: 进度文本回调（后台线程触发，调用方负责 Invoke）；
        // done(ok, message)：ok=true 时 message=落点路径，false 时=错误摘要（同样在后台线程）
        internal static void Download(Action<string> status, Action<bool, string> done)
        {
            if (_busy) return;
            // csc 直编的程序没有 TargetFrameworkAttribute，运行时按 4.0 旧默认只启用
            // SSL3/TLS1.0，而 gyan.dev 仅收 TLS1.2+（"未能创建 SSL/TLS 安全通道"的根因）。
            // 显式补齐现代协议（实测本机：默认 HEAD 失败，加 Tls12 后 200）
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            _busy = true;
            const string url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
            var zipPath = Path.Combine(Path.GetTempPath(), "hoverexplorer_ffmpeg.zip");
            var dest = TargetExe();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var wc = new WebClient())
                    {
                        wc.DownloadProgressChanged += (s, e) =>
                        {
                            var txt = (e.BytesReceived / 1024.0 / 1024.0).ToString("F1") + "/"
                                    + (e.TotalBytesToReceive / 1024.0 / 1024.0).ToString("F1") + "MB "
                                    + e.ProgressPercentage + "%";
                            status(txt);
                        };
                        wc.DownloadFile(url, zipPath);
                    }
                    status("");
                    using (var zip = ZipFile.OpenRead(zipPath))
                    {
                        // Essentials 包里 ffmpeg.exe 在 bin/ 子目录：先精确名、再全路径尾匹配兜底
                        var entry = zip.Entries.FirstOrDefault(en => en.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                                    ?? zip.Entries.FirstOrDefault(en => en.FullName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                        if (entry == null) throw new Exception("no ffmpeg.exe in zip");
                        entry.ExtractToFile(dest, true);
                    }
                    try { File.Delete(zipPath); } catch { }
                    FfmpegTools.ResetCache();
                    _busy = false;
                    done(true, dest);
                }
                catch (Exception ex)
                {
                    try { File.Delete(zipPath); } catch { }
                    _busy = false;
                    done(false, ex.Message);
                }
            });
        }
    }
}
