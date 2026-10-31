using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // 与 System.Threading.Timer 同名，显式指定消歧义

namespace HoverExplorer
{
    // ===== ffmpeg 定位 =====
    // 与 Mp3Fixer 同思路：用户指定 → 程序同目录 → Mp3Fixer 下载目录 → 常见安装位置 → PATH。
    // ffmpeg 只在 Media Foundation 解不动时兜底，不装也不影响常规预览。
    internal static class FfmpegTools
    {
        private static string _cached;
        private static bool _probed;

        internal static string FindFfmpeg()
        {
            if (_probed) return _cached;
            _probed = true;
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(Config.FfmpegPath)) cands.Add(Config.FfmpegPath);
            cands.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Mp3Fixer\ffmpeg.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"HoverExplorer\ffmpeg.exe"));   // 设置里"下载"按钮的落点（程序目录不可写时）
            cands.Add(@"D:\portable\ffmpeg.exe");
            cands.Add(@"C:\portable\ffmpeg.exe");
            cands.Add(@"C:\Tools\ffmpeg.exe");
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"ffmpeg\bin\ffmpeg.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"ffmpeg\bin\ffmpeg.exe"));
            cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\shims\ffmpeg.exe"));
            cands.Add(@"C:\ProgramData\chocolatey\bin\ffmpeg.exe");
            foreach (var c in cands)
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) { _cached = c; return _cached; }

            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(';'))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) { _cached = p; return _cached; }
            }
            return null;
        }

        // 设置里改了路径要能立即生效
        internal static void ResetCache() { _probed = false; _cached = null; }
    }

    // 视频基本信息（解析 ffmpeg -i 的 stderr 得到，不依赖 ffprobe）
    internal class VideoInfo
    {
        public int Width;
        public int Height;
        public double DurationSec;
        public double Fps;
        public string Codec = "";
        public string PixFmt = "";
        public long Bitrate;      // 视频码率 bit/s
        public bool HasAudio;
        public bool Valid { get { return Width > 0 && Height > 0; } }
    }

    // 用 `ffmpeg -i <file>` 读容器信息：ffmpeg 把输入/流信息打到 stderr，无输出文件即退出。
    // 不用 ffprobe 是因为官方精简构建常常只带 ffmpeg.exe。
    internal static class VideoProbe
    {
        // 探测结果缓存：悬停同一文件会反复探测，按 路径+大小+修改时间 判定失效
        private static readonly Dictionary<string, VideoInfo> _probeCache = new Dictionary<string, VideoInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _probeStamp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _probeLock = new object();

        /// <summary>带缓存的探测（同一文件只探一次）</summary>
        internal static VideoInfo ProbeCached(string path)
        {
            string stamp;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                stamp = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            }
            catch { return null; }

            lock (_probeLock)
            {
                VideoInfo cached;
                string cs;
                if (_probeCache.TryGetValue(path, out cached) && _probeStamp.TryGetValue(path, out cs) && cs == stamp)
                    return cached;
            }

            var info = Probe(path);
            lock (_probeLock)
            {
                if (_probeCache.Count > 64) { _probeCache.Clear(); _probeStamp.Clear(); }
                _probeCache[path] = info;
                _probeStamp[path] = stamp;
            }
            return info;
        }

        internal static VideoInfo Probe(string path)
        {
            var info = new VideoInfo();
            string exe = FfmpegTools.FindFfmpeg();
            if (exe == null) return info;
            string err = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-hide_banner -i \"" + path + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                using (var p = Process.Start(psi))
                {
                    // stderr 是信息主体；stdout 基本为空，读掉以免管道占满
                    err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    try { p.WaitForExit(20000); } catch { }
                }
            }
            catch (Exception ex)
            {
                Program.DebugLog("[video-probe] " + ex.Message);
            }
            Parse(err, info);
            return info;
        }

        private static void Parse(string err, VideoInfo info)
        {
            if (string.IsNullOrEmpty(err)) return;
            var lines = err.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Duration:", StringComparison.Ordinal))
                {
                    var m = Regex.Match(line, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                    if (m.Success)
                    {
                        double h, min, sec;
                        if (double.TryParse(m.Groups[1].Value, out h) &&
                            double.TryParse(m.Groups[2].Value, out min) &&
                            double.TryParse(m.Groups[3].Value, out sec))
                            info.DurationSec = h * 3600 + min * 60 + sec;
                    }
                    continue;
                }
                if (line.IndexOf(": Video:", StringComparison.Ordinal) >= 0)
                {
                    ParseVideoLine(line, info);
                    continue;
                }
                if (line.IndexOf(": Audio:", StringComparison.Ordinal) >= 0)
                {
                    info.HasAudio = true;
                    continue;
                }
            }
        }

        // 典型行：
        // Stream #0:0(und): Video: h264 (High 4:2:2) (avc1 / 0x31637661), yuv422p10le(pc),
        //                3840x2160 [SAR 1:1 DAR 16:9], 138235 kb/s, 25 fps, 25 tbr, ...
        // 像素格式里带逗号（yuv420p(tv, bt709, progressive)），所以按片段逐个正则识别，
        // 不依赖片段下标。
        private static void ParseVideoLine(string line, VideoInfo info)
        {
            var parts = line.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;

                if (i == 0)
                {
                    int vi = p.IndexOf("Video:", StringComparison.Ordinal);
                    if (vi >= 0)
                    {
                        string after = p.Substring(vi + 6).Trim();
                        int sp = after.IndexOf(' ');
                        info.Codec = sp > 0 ? after.Substring(0, sp) : after;
                    }
                    continue;
                }

                var mres = Regex.Match(p, @"^(\d{2,5})x(\d{2,5})");
                if (mres.Success)
                {
                    int w, h;
                    if (int.TryParse(mres.Groups[1].Value, out w) && int.TryParse(mres.Groups[2].Value, out h))
                    { info.Width = w; info.Height = h; }
                    continue;
                }

                var mfps = Regex.Match(p, @"^([\d.]+)\s+fps");
                if (mfps.Success)
                {
                    double f;
                    if (double.TryParse(mfps.Groups[1].Value, out f)) info.Fps = f;
                    continue;
                }

                var mbr = Regex.Match(p, @"^([\d.]+)\s+kb/s");
                if (mbr.Success)
                {
                    double kb;
                    if (double.TryParse(mbr.Groups[1].Value, out kb)) info.Bitrate = (long)(kb * 1000.0);
                    continue;
                }

                if (info.PixFmt.Length == 0 && Regex.IsMatch(p, @"^(yuv|rgb|nv|bgr|gray|p0)[0-9a-z]*", RegexOptions.IgnoreCase))
                {
                    info.PixFmt = p.Split('(')[0];
                }
            }
        }
    }

    // ===== 系统解码器（Media Foundation）能力判定 =====
    // 实测（Windows 自带解码器）：
    //   yuv420p 8bit 4:2:0        → 能开
    //   yuv420p10le 10bit 4:2:0   → 0xC00D11B1 打不开
    //   yuv422p 8bit 4:2:2        → 0xC00D11B1 打不开
    //   yuv422p10le 10bit 4:2:2   → 0xC00D11B1 打不开
    //   ProRes (Proxy/HQ/4444)    → 根本没有解码器
    // 即：H.264/HEVC 只吃 8bit 4:2:0，其余规格 MF 一律解不了。
    internal static class SystemDecoder
    {
        // 判断系统解码器能否处理这个文件。
        // 只把"确定不行"的挑出来（宁可多走一次失败再兜底，也不误判把能原生播放的文件降级）
        internal static bool CanDecode(VideoInfo info)
        {
            if (info == null) return true;

            string codec = (info.Codec ?? "").ToLowerInvariant();
            if (codec.StartsWith("prores") || codec == "dnxhd" || codec == "dnxhr" || codec == "cfhd"
                || codec == "ffv1" || codec == "utvideo" || codec == "rawvideo" || codec == "v210" || codec == "v410")
                return false;

            string pix = (info.PixFmt ?? "").ToLowerInvariant();
            if (pix.Length == 0) return true;                 // 探测不到像素格式时不武断
            if (pix.Contains("422") || pix.Contains("444")) return false;
            if (pix.Contains("10le") || pix.Contains("12le") || pix.Contains("10be") || pix.Contains("12be")) return false;
            return true;
        }
    }

    // ===== ffmpeg 帧流预览面板 =====
    // Media Foundation 解不了的规格（10bit 4:2:2、HEVC、某些 ProRes、超高码率）走这里：
    // ffmpeg 把画面解成 JPEG 帧流写到 stdout，本面板逐帧贴图并自绘播放条。
    // -re 让 ffmpeg 按源帧率实时吐帧，播放节奏天然对齐；
    // 暂停 = 结束进程，继续 = 从当前位置重启（-ss 快速定位）。
    internal sealed class FfmpegVideoPanel : Panel
    {
        private const int MaxQueuedFrames = 24;   // 队列上限 = 缓冲深度；满了就靠管道背压让 ffmpeg 停下
        private const int OutWidth = 640;          // 解码输出宽度（高度自适应，等比缩放）

        private readonly string _path;
        private readonly PictureBox _pb;
        private readonly Button _btn;
        private readonly Label _lbl;
        private readonly Label _badge;
        private readonly Label _spec;
        private readonly MediaPreview.SeekBar _prog;
        private readonly Timer _tick;

        private readonly Queue<Bitmap> _frames = new Queue<Bitmap>();
        private readonly object _frameLock = new object();
        private readonly object _procLock = new object();

        private Process _proc;
        private VideoInfo _info;
        private string _exe;
        private double _pos;          // 播放位置（秒）
        private double _posAtStart;   // 本段起始位置
        private DateTime _startedAt;
        private volatile bool _running;        // ffmpeg 进程在跑（读线程写、UI 线程读 → 要 volatile）
        private volatile bool _paused = true;
        private volatile bool _oneShot;        // 暂停状态下取单帧（拖动进度条后显示目标帧）
        private volatile bool _frameReady;     // 后台线程：已有帧进队
        private volatile bool _sizeReady;      // 后台线程：探测信息就绪（宽高/帧率）
        private volatile bool _failFlag;       // 后台线程：这条流解不出画面
        private bool _firstFrameRaised;        // UI 线程：首帧事件已抛
        private bool _failedRaised;            // UI 线程：失败事件已抛
        private volatile bool _disposed;
        private double _speed = 1.0;  // 倍速
        private double _sourceFps = 25;// 源帧率（探测得到），播放速率 = 源帧率 × 倍速
        private double _frameAcc = 0;  // 帧消费累加器
        private DateTime _lastTick = DateTime.MinValue;

        /// <summary>首帧就绪（可用于让缩略图让位）</summary>
        internal event Action FirstFrameReady;
        /// <summary>拿到真实宽高（预览窗按宽高比校正）</summary>
        internal event Action<int, int> VideoSizeKnown;
        /// <summary>完全解不出画面（调用方回退缩略图）</summary>
        internal event Action Failed;

        // 事件一律在 UI 线程抛（由 _tick 定时器统一转交）。
        // 后台线程（ffmpeg 读帧/探测）只置标志位——它们若直接改 WinForms 控件
        // （父容器层级、可见性），会和 UI 线程的布局交叉死锁：实测表现为整个 UI 线程卡死。

        internal FfmpegVideoPanel(string path, int w, int h, VideoInfo knownInfo)
        {
            _path = path;
            _info = knownInfo;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            Size = new Size(w, h);

            _pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            Controls.Add(_pb);

            // 右下角小角标：标明当前用的是 ffmpeg 解码通道（静音、无系统解码器依赖）
            _badge = new Label
            {
                Text = "ffmpeg",
                Dock = DockStyle.Bottom,
                Height = 1,
                ForeColor = Color.FromArgb(160, 160, 160),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 7f),
                TextAlign = ContentAlignment.MiddleRight,
                Visible = false,
            };
            Controls.Add(_badge);
            _badge.BringToFront();

            var bar = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Config.BG };
            _btn = new Button
            {
                Text = "❚❚",
                Width = 36,
                Dock = DockStyle.Left,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Config.FG,
                BackColor = Config.BG,
                Font = new Font("Microsoft YaHei", 9)
            };
            _btn.FlatAppearance.BorderSize = 0;
            _btn.FlatAppearance.MouseOverBackColor = Config.BgHover;
            _btn.FlatAppearance.MouseDownBackColor = Config.BgHover;
            _lbl = new Label
            {
                Width = 110,
                Dock = DockStyle.Right,
                Text = I18n.T("media_loading"),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Config.FgDir,
                BackColor = Config.BG,
                AutoEllipsis = true,
                Font = new Font("Microsoft YaHei", 8)
            };
            _prog = new MediaPreview.SeekBar { Dock = DockStyle.Fill };
            // 左上角媒体规格角标：分辨率 · 帧率 · 时长 · 有无声轨（探测数据齐全，比 MediaElement 路径完整）。
            // 探测在后台线程，文字更新统一放 _tick（UI 线程）——见字段区注释，跨线程改控件会死锁
            _spec = new Label
            {
                AutoSize = true,
                Location = new Point(4, 4),
                Font = new Font("Microsoft YaHei", 8f),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(160, 30, 30, 36),
                Padding = new Padding(4, 2, 4, 2),
                Visible = false,
            };
            Controls.Add(_spec);
            _spec.BringToFront();   // 压在画面上层；不参与 Dock，不影响布局
            // 倍速刻度尺：改的是"消费帧率"，ffmpeg 进程不用重启
            var spdHint = MediaPreview.CreateSpeedHint(this, 4);
            var ruler = new MediaPreview.SpeedRuler { Dock = DockStyle.Bottom };
            ruler.PreviewSpeed += delegate(double r) { _speed = r; MediaPreview.UpdateSpeedHint(spdHint, r); };
            ruler.CommitSpeed += delegate(double r) { _speed = r; MediaPreview.UpdateSpeedHint(spdHint, r); };
            ruler.RestoreSpeed += delegate { _speed = ruler.Committed; MediaPreview.UpdateSpeedHint(spdHint, ruler.Committed); };
            var cls = MediaPreview.CloseButton(this);
            // bar：[播放][进度][时间][关闭]
            bar.Controls.Add(_prog);
            bar.Controls.Add(_btn);
            bar.Controls.Add(_lbl);
            bar.Controls.Add(cls);
            Controls.Add(ruler);
            Controls.Add(bar);
            if (!Config.SpeedToggle) ruler.Hide();

            // 长按画面加速到 3×，释放恢复
            double savedSpd = 1.0;
            _pb.MouseDown += delegate
            {
                if (!Config.SpeedToggle) return;
                savedSpd = _speed;
                _speed = 3.0;
                MediaPreview.UpdateSpeedHint(spdHint, 3.0);
            };
            _pb.MouseUp += delegate
            {
                if (!Config.SpeedToggle) return;
                _speed = savedSpd;
                MediaPreview.UpdateSpeedHint(spdHint, savedSpd);
            };

            _btn.Click += delegate { TogglePlay(); };
            _prog.Seek += delegate(double ratio) { SeekTo(ratio); };

            _tick = new Timer { Interval = 10 };   // 10ms：2× 时一 tick 只消费一帧，不会漏帧
            _tick.Tick += delegate
            {
                // 后台线程只置标志位，事件统一在这里（UI 线程）抛 —— 见字段区注释
                if (_sizeReady)
                {
                    _sizeReady = false;
                    if (_info != null && _info.Valid)
                    {
                        if (_info.Fps > 0) _sourceFps = _info.Fps;
                        UpdateSpecBadge();
                        var hSize = VideoSizeKnown;
                        if (hSize != null) { try { hSize(_info.Width, _info.Height); } catch { } }
                    }
                }
                if (_failFlag) { _failFlag = false; RaiseFailedUi(); }
                PumpFrames();
                UpdateTime();
            };
            _tick.Start();

            var worker = new Thread(Worker);
            worker.IsBackground = true;
            worker.Start();
        }

        // 填左上角规格角标（只在 UI 线程的 _tick 里调用）。帧率能整就整（25fps），
        // 带小数保留（29.97fps）；无声轨显 🔇——ffmpeg 流本身 -an 没声音，但源文件有没有声轨探测是知道的
        private void UpdateSpecBadge()
        {
            if (_spec == null || _spec.IsDisposed || _info == null || !_info.Valid) return;
            string fps = _info.Fps > 0
                ? (_info.Fps == Math.Floor(_info.Fps) ? ((int)_info.Fps).ToString() : _info.Fps.ToString("0.##")) + "fps · "
                : "";
            string dur = _info.DurationSec > 0 ? " · " + MediaPreview.Fmt(TimeSpan.FromSeconds(_info.DurationSec)) : "";
            _spec.Text = _info.Width + "×" + _info.Height + " · " + fps + dur + " · " + (_info.HasAudio ? "🔊" : "🔇");
            _spec.Visible = true;
        }

        // 后台：探测 → 起 ffmpeg → 等首帧
        private void Worker()
        {
            try
            {
                _exe = FfmpegTools.FindFfmpeg();
                if (_exe == null) { _failFlag = true; return; }

                // 已有探测结果就不用再探一次（探测要起一个 ffmpeg 进程，不便宜）
                if (_info == null) _info = VideoProbe.ProbeCached(_path);
                if (_info != null && _info.Valid) _sizeReady = true;   // 宽高/帧率交给 UI 线程去取

                _paused = false;
                StartStream(0, false);

                // 等首帧：拿不到说明这个文件 ffmpeg 也解不了，交给调用方回退缩略图
                var deadline = DateTime.Now.AddSeconds(10);
                while (DateTime.Now < deadline && !_frameReady && !_disposed) Thread.Sleep(60);
                if (!_frameReady && !_disposed) _failFlag = true;
            }
            catch (Exception ex)
            {
                Program.DebugLog("[ffmpeg-video] " + ex.Message);
                _failFlag = true;
            }
        }

        private void StartStream(double fromSec, bool oneShot)
        {
            KillStream();
            _oneShot = oneShot;
            if (_exe == null) return;
            try
            {
                // 输出宽度不放大原片：小于 640 宽的视频按原宽解码
                int w = OutWidth;
                if (_info != null && _info.Width > 0 && _info.Width < w) w = _info.Width;
                if (w % 2 != 0) w--;          // 保持偶数，避免部分滤镜报错
                if (w < 16) w = 16;

                var sb = new System.Text.StringBuilder();
                // 不用 -re：让 ffmpeg 全速解码，靠"队列满就停读"的管道背压限速。
                // 这样播放速率完全由我们取帧的节奏决定，倍速（>1×）才做得到——
                // -re 会把输入钉在 1× 实时，源帧率以下根本取不到更多帧。
                sb.Append("-hide_banner -loglevel error -nostdin");
                if (fromSec > 0.05) sb.Append(" -ss ").Append(fromSec.ToString("F2"));
                sb.Append(" -i \"").Append(_path).Append("\"");
                sb.Append(" -an");                                        // 只取画面
                sb.Append(" -vf scale=").Append(w).Append(":-2");
                sb.Append(" -q:v 4 -f image2pipe -vcodec mjpeg -");

                var psi = new ProcessStartInfo
                {
                    FileName = _exe,
                    Arguments = sb.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var p = new Process();
                p.StartInfo = psi;
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) Program.DebugLog("[ffmpeg-video] " + e.Data);
                };
                p.Start();
                p.BeginErrorReadLine();     // 必须持续读 stderr，否则管道写满 ffmpeg 会卡住

                lock (_procLock) { _proc = p; }
                _running = true;
                _posAtStart = fromSec;
                _startedAt = DateTime.Now;
                _frameAcc = 0;
                _lastTick = DateTime.MinValue;

                // 进程对象直接传给读线程，避免换流时读线程误取到新进程的 stdout
                var reader = new Thread(delegate() { ReadFrames(p); });
                reader.IsBackground = true;
                reader.Start();

                if (hwndCreated) SetBadgeVisible(true);
            }
            catch (Exception ex)
            {
                Program.DebugLog("[ffmpeg-video] start fail " + ex.Message);
            }
        }

        private bool hwndCreated { get { return IsHandleCreated; } }

        private void SetBadgeVisible(bool v)
        {
            try
            {
                if (!IsHandleCreated) return;
                if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { SetBadgeVisible(v); }); return; }
                _badge.Visible = v;
            }
            catch { }
        }

        // 读 stdout：image2pipe 输出的是一串独立 JPEG，以 FFD8 开头、FFD9 结尾
        private void ReadFrames(Process p)
        {
            if (p == null) return;

            var outBuf = new MemoryStream();
            var buf = new byte[64 * 1024];
            bool inFrame = false;
            bool pendingFF = false;     // 上一个字节是 0xFF（标记可能跨块边界）
            try
            {
                var s = p.StandardOutput.BaseStream;
                while (true)
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        if (!inFrame)
                        {
                            if (pendingFF)
                            {
                                if (b == 0xD8)
                                {
                                    pendingFF = false;
                                    inFrame = true;
                                    outBuf.WriteByte(0xFF);
                                    outBuf.WriteByte(0xD8);
                                }
                                else
                                {
                                    // FF FF D8 这种情况：继续保持"待判定"状态
                                    pendingFF = (b == 0xFF);
                                }
                            }
                            else if (b == 0xFF) pendingFF = true;
                            continue;
                        }

                        if (pendingFF)
                        {
                            pendingFF = false;
                            if (b == 0xD9)
                            {
                                outBuf.WriteByte(0xFF);
                                outBuf.WriteByte(0xD9);
                                EmitFrame(outBuf.ToArray());
                                outBuf.SetLength(0);
                                inFrame = false;
                            }
                            else
                            {
                                outBuf.WriteByte(0xFF);
                                outBuf.WriteByte(b);
                            }
                        }
                        else
                        {
                            outBuf.WriteByte(b);
                            if (b == 0xFF) pendingFF = true;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                try { outBuf.Dispose(); } catch { }
                _running = false;
            }
        }

        private void EmitFrame(byte[] data)
        {
            try
            {
                Bitmap bmp;
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms))
                    bmp = new Bitmap(img);

                lock (_frameLock)
                {
                    // 队列满 → 等待：管道背压让 ffmpeg 停在"刚好够播"的位置，
                    // 既不会把整个文件解完，也不会让播放速度被源帧率钉死。
                    while (_frames.Count >= MaxQueuedFrames && !_disposed)
                        Monitor.Wait(_frameLock, 100);
                    if (_disposed) { try { bmp.Dispose(); } catch { } return; }
                    _frames.Enqueue(bmp);
                }

                // 只置标志位，事件由 UI 线程的定时器抛（跨线程改控件会死锁）
                _frameReady = true;
                // 单帧模式（暂停时拖动进度条）：拿到这一帧就收工
                if (_oneShot) { _oneShot = false; KillStream(); }
            }
            catch { }
        }

        // 按 源帧率×倍速 的节奏取帧显示。
        // 帧只进不丢（保证内容顺序正确），消费慢下来时队列变长，ffmpeg 被背压挡住。
        private void PumpFrames()
        {
            DateTime now = DateTime.Now;
            double dt = (_lastTick == DateTime.MinValue) ? 0.016 : (now - _lastTick).TotalSeconds;
            _lastTick = now;
            if (dt < 0) dt = 0;
            if (dt > 0.5) dt = 0.5;          // 窗口被挂起/卡顿时不要一次性冲掉一堆帧

            double target = (_sourceFps > 0 ? _sourceFps : 25) * _speed;
            if (target < 1) target = 1;
            _frameAcc += target * dt;

            Bitmap frame = null;
            lock (_frameLock)
            {
                // 首帧优先显示：不用等帧率累加器攒满 1 帧
                if (!_firstFrameRaised && _frames.Count > 0)
                {
                    frame = _frames.Dequeue();
                    _frameAcc = 0;
                }
                else
                {
                    while (_frameAcc >= 1.0 && _frames.Count > 0)
                    {
                        var f = _frames.Dequeue();
                        if (frame != null) { try { frame.Dispose(); } catch { } }
                        frame = f;
                        _frameAcc -= 1.0;
                    }
                }
                Monitor.PulseAll(_frameLock);   // 唤醒背压等待的读线程
            }
            if (frame == null) return;
            if (!_firstFrameRaised)
            {
                // 首帧事件在 UI 线程抛（这里就是 UI 线程）
                _firstFrameRaised = true;
                var fr = FirstFrameReady;
                if (fr != null) { try { fr(); } catch { } }
            }
            var oldImg = _pb.Image;
            _pb.Image = frame;
            if (oldImg != null) { try { oldImg.Dispose(); } catch { } }
        }

        private void UpdateTime()
        {
            if (_info == null || _info.DurationSec <= 0) return;
            if (_running && !_paused)
            {
                // 播放位置按倍速推进（内容时间，不是墙钟时间）
                _pos = _posAtStart + (DateTime.Now - _startedAt).TotalSeconds * _speed;
                if (_pos > _info.DurationSec) _pos = _info.DurationSec;
            }
            _lbl.Text = MediaPreview.Fmt(TimeSpan.FromSeconds(_pos)) + " / " + MediaPreview.Fmt(TimeSpan.FromSeconds(_info.DurationSec));
            _prog.Ratio = _pos / _info.DurationSec;
        }

        private void TogglePlay()
        {
            if (_paused)
            {
                // 播完再点 = 从头
                if (_info != null && _info.DurationSec > 0 && _pos >= _info.DurationSec - 0.15) _pos = 0;
                _paused = false;
                _btn.Text = "❚❚";
                StartStream(_pos, false);
            }
            else
            {
                _paused = true;
                _btn.Text = "▶";
                KillStream();
            }
        }

        private void SeekTo(double ratio)
        {
            if (_info == null || _info.DurationSec <= 0) return;
            double target = ratio * _info.DurationSec;
            if (target < 0) target = 0;
            _pos = target;
            _prog.Ratio = ratio;
            if (_paused)
            {
                // 暂停中：起一个只出首帧的短流，把目标帧显示出来
                StartStream(_pos, true);
            }
            else
            {
                StartStream(_pos, false);
            }
        }

        private void KillStream()
        {
            Process p = null;
            lock (_procLock) { p = _proc; _proc = null; }
            _running = false;
            if (p != null)
            {
                try { p.Kill(); } catch { }
                try { p.Close(); } catch { }
                try { p.Dispose(); } catch { }
            }
        }

        // 失败事件：只在 UI 线程抛（后台线程只置 _failFlag）
        private void RaiseFailedUi()
        {
            if (_disposed || _failedRaised) return;
            _failedRaised = true;
            var h = Failed;
            if (h == null) return;
            try { h(); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                KillStream();
                try { if (_tick != null) { _tick.Stop(); _tick.Dispose(); } } catch { }
                lock (_frameLock)
                {
                    Monitor.PulseAll(_frameLock);   // 唤醒背压等待的读线程，别让它挂住
                    while (_frames.Count > 0)
                    {
                        var b = _frames.Dequeue();
                        try { b.Dispose(); } catch { }
                    }
                }
                try { if (_pb != null && _pb.Image != null) { _pb.Image.Dispose(); _pb.Image = null; } } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
