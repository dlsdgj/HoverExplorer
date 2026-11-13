using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HoverExplorer
{
    // ffmpeg + waveOut 音频播放会话：用 atempo 滤镜实现保调倍速（不变音调）。
    // ffmpeg 把音频解码到 s16le PCM 写到 stdout，本类用 Win32 waveOut API 直接播放。
    // 倍速变化时重启 ffmpeg 进程（换 atempo 参数），从当前位置继续播放。
    internal sealed class FfmpegAudioSession : IMediaSession
    {
        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hWaveOut, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);
        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hWaveOut);
        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, int uSize);
        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, int uSize);
        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveHdr, int uSize);
        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hWaveOut);
        [DllImport("winmm.dll")]
        private static extern int waveOutPause(IntPtr hWaveOut);
        [DllImport("winmm.dll")]
        private static extern int waveOutRestart(IntPtr hWaveOut);

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        private const int WAVE_MAPPER = -1;
        private const ushort WAVE_FORMAT_PCM = 1;
        private const uint WHDR_DONE = 0x0001;
        private const int SAMPLE_RATE = 44100;
        private const int CHANNELS = 2;
        private const int BITS_PER_SAMPLE = 16;
        private const int BLOCK_ALIGN = CHANNELS * BITS_PER_SAMPLE / 8;
        private const int BYTES_PER_SEC = SAMPLE_RATE * BLOCK_ALIGN;
        private const int BUF_COUNT = 4;
        private const int BUF_SIZE = 16384;

        private string _path;
        private double _speed = 1.0;
        private TimeSpan _duration;
        private bool _opened, _failed, _playing, _endedFired, _closed;
        private volatile bool _paused;
        private double _seekSec;
        private long _feedBytes;
        private IntPtr _waveHandle = IntPtr.Zero;
        private Process _process;
        private Thread _thread;
        private volatile bool _stopThread;
        private readonly object _restartLock = new object();

        private byte[][] _bufs;
        private GCHandle[] _bufPins;
        private IntPtr[] _hdrPtrs;

        public event Action Opened;
        public event Action Failed;
        public event Action Ended;
        public event Action<double?> EstimateReady;

        public string Path { get { return _path; } }
        public bool IsVideo { get { return false; } }
        public Control HostView { get { return null; } }
        public bool IsLoaded { get { return _opened; } }
        public bool HasFailed { get { return _failed; } }
        public bool IsPlaying { get { return _playing && !_endedFired; } }
        public TimeSpan Duration { get { return _duration; } }
        public double? EstimatedMs { get { return _duration.TotalMilliseconds > 0 ? _duration.TotalMilliseconds : (double?)null; } }
        public int NaturalWidth { get { return 0; } }
        public int NaturalHeight { get { return 0; } }

        public TimeSpan Position
        {
            get
            {
                if (!_opened || _failed) return TimeSpan.Zero;
                long fb = Interlocked.Read(ref _feedBytes);
                double sec = _seekSec + (fb / (double)BYTES_PER_SEC) * _speed;
                if (sec < 0) sec = 0;
                if (_duration.TotalSeconds > 0 && sec > _duration.TotalSeconds) sec = _duration.TotalSeconds;
                return TimeSpan.FromSeconds(sec);
            }
        }

        // startSec：直接从该秒数开始解码播放。视频预览用视频当前进度传入，
        // 避免"先从头出声、再 seek 回当前位置重播"的两次播放听感
        public FfmpegAudioSession(string path, double startSec = 0)
        {
            _path = path;
            if (startSec < 0) startSec = 0;
            double? est = MediaPreview.EstimateDurationMs(path);
            if (!est.HasValue || est.Value <= 0)
            {
                var vi = VideoProbe.ProbeCached(path);
                if (vi != null && vi.DurationSec > 0) est = vi.DurationSec * 1000;
            }
            if (!est.HasValue || est.Value <= 0)
            {
                _failed = true;
                var f = Failed; if (f != null) { try { f(); } catch { } }
                return;
            }
            _duration = TimeSpan.FromMilliseconds(est.Value);
            if (!InitWaveOut())
            {
                _failed = true;
                var f = Failed; if (f != null) { try { f(); } catch { } }
                return;
            }
            if (!StartFfmpeg(startSec))
            {
                CleanupWaveOut();
                _failed = true;
                var f = Failed; if (f != null) { try { f(); } catch { } }
                return;
            }
            _opened = true;
            var o = Opened; if (o != null) { try { o(); } catch { } }
            var e = EstimateReady; if (e != null) { try { e(_duration.TotalMilliseconds); } catch { } }
            _playing = true;
        }

        // 当前音频流的起始秒数（Play/Seek 重建时更新）；用于判断是否还需要对齐
        public double StartSec { get { return _seekSec; } }

        private bool InitWaveOut()
        {
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = CHANNELS,
                nSamplesPerSec = SAMPLE_RATE,
                nAvgBytesPerSec = BYTES_PER_SEC,
                nBlockAlign = BLOCK_ALIGN,
                wBitsPerSample = BITS_PER_SAMPLE,
                cbSize = 0
            };
            if (waveOutOpen(out _waveHandle, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                return false;
            _bufs = new byte[BUF_COUNT][];
            _bufPins = new GCHandle[BUF_COUNT];
            _hdrPtrs = new IntPtr[BUF_COUNT];
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < BUF_COUNT; i++)
            {
                _bufs[i] = new byte[BUF_SIZE];
                _bufPins[i] = GCHandle.Alloc(_bufs[i], GCHandleType.Pinned);
                _hdrPtrs[i] = Marshal.AllocHGlobal(hdrSize);
                var hdr = new WAVEHDR { lpData = _bufPins[i].AddrOfPinnedObject(), dwBufferLength = 0, dwFlags = 0 };
                Marshal.StructureToPtr(hdr, _hdrPtrs[i], false);
            }
            return true;
        }

        private bool StartFfmpeg(double seekSec)
        {
            string exe = FfmpegTools.FindFfmpeg();
            if (string.IsNullOrEmpty(exe)) return false;
            _seekSec = seekSec;
            _feedBytes = 0;
            _endedFired = false;
            var args = "-i \"" + _path + "\"";
            if (seekSec > 0.1) args = "-ss " + seekSec.ToString("0.###") + " " + args;
            if (Math.Abs(_speed - 1.0) > 0.01)
                args += " -af atempo=" + _speed.ToString("0.###");
            args += " -f s16le -ar " + SAMPLE_RATE + " -ac " + CHANNELS + " pipe:1";
            try
            {
                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };
                _process.Start();
                _process.BeginErrorReadLine();
            }
            catch { return false; }
            _stopThread = false;
            _thread = new Thread(PlaybackThread) { IsBackground = true, Name = "ffAudio" };
            _thread.Start();
            return true;
        }

        private void PlaybackThread()
        {
            byte[] readBuf = new byte[BUF_SIZE];
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            Stream stdout = null;
            try { stdout = _process.StandardOutput.BaseStream; } catch { }
            while (!_stopThread && stdout != null)
            {
                if (_paused) { Thread.Sleep(30); continue; }
                for (int i = 0; i < BUF_COUNT; i++)
                {
                    var hdr = (WAVEHDR)Marshal.PtrToStructure(_hdrPtrs[i], typeof(WAVEHDR));
                    if ((hdr.dwFlags & WHDR_DONE) != 0 && hdr.dwBufferLength > 0)
                    {
                        waveOutUnprepareHeader(_waveHandle, _hdrPtrs[i], hdrSize);
                        var reset = new WAVEHDR { lpData = _bufPins[i].AddrOfPinnedObject(), dwBufferLength = 0, dwFlags = 0 };
                        Marshal.StructureToPtr(reset, _hdrPtrs[i], false);
                    }
                }
                int idx = -1;
                for (int i = 0; i < BUF_COUNT; i++)
                {
                    var hdr = (WAVEHDR)Marshal.PtrToStructure(_hdrPtrs[i], typeof(WAVEHDR));
                    if (hdr.dwBufferLength == 0) { idx = i; break; }
                }
                if (idx < 0) { Thread.Sleep(5); continue; }
                int n = 0;
                try { n = stdout.Read(readBuf, 0, BUF_SIZE); } catch { n = 0; }
                if (n <= 0)
                {
                    WaitAllBuffers();
                    if (!_stopThread && !_endedFired)
                    {
                        _endedFired = true;
                        _playing = false;
                        var en = Ended; if (en != null) { try { en(); } catch { } }
                    }
                    break;
                }
                n -= n % BLOCK_ALIGN;
                if (n == 0) continue;
                Array.Copy(readBuf, 0, _bufs[idx], 0, n);
                var wh = new WAVEHDR
                {
                    lpData = _bufPins[idx].AddrOfPinnedObject(),
                    dwBufferLength = (uint)n,
                    dwFlags = 0
                };
                Marshal.StructureToPtr(wh, _hdrPtrs[idx], false);
                waveOutPrepareHeader(_waveHandle, _hdrPtrs[idx], hdrSize);
                waveOutWrite(_waveHandle, _hdrPtrs[idx], hdrSize);
                Interlocked.Add(ref _feedBytes, n);
            }
        }

        private void WaitAllBuffers()
        {
            for (int round = 0; round < 500; round++)
            {
                bool allDone = true;
                for (int i = 0; i < BUF_COUNT; i++)
                {
                    var hdr = (WAVEHDR)Marshal.PtrToStructure(_hdrPtrs[i], typeof(WAVEHDR));
                    if (hdr.dwBufferLength > 0 && (hdr.dwFlags & WHDR_DONE) == 0) { allDone = false; break; }
                }
                if (allDone) break;
                Thread.Sleep(10);
            }
        }

        private void StopThreadAndFfmpeg()
        {
            _stopThread = true;
            try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
            if (_thread != null && _thread.IsAlive) { try { _thread.Join(500); } catch { } }
            try { if (_process != null) _process.Dispose(); } catch { }
            _process = null;
            _thread = null;
        }

        private void ResetBuffers()
        {
            if (_waveHandle == IntPtr.Zero) return;
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            try { waveOutReset(_waveHandle); } catch { }
            for (int i = 0; i < BUF_COUNT; i++)
            {
                var hdr = (WAVEHDR)Marshal.PtrToStructure(_hdrPtrs[i], typeof(WAVEHDR));
                if (hdr.dwBufferLength > 0)
                {
                    try { waveOutUnprepareHeader(_waveHandle, _hdrPtrs[i], hdrSize); } catch { }
                }
                var reset = new WAVEHDR { lpData = _bufPins[i].AddrOfPinnedObject(), dwBufferLength = 0, dwFlags = 0 };
                Marshal.StructureToPtr(reset, _hdrPtrs[i], false);
            }
        }

        private void CleanupWaveOut()
        {
            if (_waveHandle != IntPtr.Zero)
            {
                try { waveOutReset(_waveHandle); } catch { }
                try { waveOutClose(_waveHandle); } catch { }
                _waveHandle = IntPtr.Zero;
            }
            if (_hdrPtrs != null)
            {
                for (int i = 0; i < _hdrPtrs.Length; i++)
                {
                    if (_hdrPtrs[i] != IntPtr.Zero) { Marshal.FreeHGlobal(_hdrPtrs[i]); _hdrPtrs[i] = IntPtr.Zero; }
                }
            }
            if (_bufPins != null)
            {
                for (int i = 0; i < _bufPins.Length; i++)
                {
                    if (_bufPins[i].IsAllocated) _bufPins[i].Free();
                }
            }
        }

        public void Play()
        {
            if (!_opened || _failed) return;
            _endedFired = false;
            _paused = false;
            if (_waveHandle != IntPtr.Zero) { try { waveOutRestart(_waveHandle); } catch { } }
            _playing = true;
            Program.DebugLog("[audio] play(ff) " + MediaPreview.TruncPath(_path));
        }

        public void Pause()
        {
            if (!_opened || _failed) return;
            _paused = true;
            if (_waveHandle != IntPtr.Zero) { try { waveOutPause(_waveHandle); } catch { } }
            _playing = false;
        }

        public void Toggle() { if (IsPlaying) Pause(); else Play(); }

        public double SpeedRatio
        {
            get { return _speed; }
            set
            {
                if (value < 0.25) value = 0.25;
                if (value > 4.0) value = 4.0;
                if (Math.Abs(value - _speed) < 0.001) return;
                if (!_opened || _failed) { _speed = value; return; }
                lock (_restartLock)
                {
                    double curPos = Position.TotalSeconds;
                    _speed = value;
                    StopThreadAndFfmpeg();
                    ResetBuffers();
                    if (!StartFfmpeg(curPos))
                    {
                        _failed = true;
                        var f = Failed; if (f != null) { try { f(); } catch { } }
                        return;
                    }
                    if (_paused && _waveHandle != IntPtr.Zero) { try { waveOutPause(_waveHandle); } catch { } }
                }
            }
        }

        public void SeekRatio(double ratio)
        {
            if (!_opened || _failed || _duration <= TimeSpan.Zero) return;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            double targetSec = _duration.TotalSeconds * ratio;
            lock (_restartLock)
            {
                StopThreadAndFfmpeg();
                ResetBuffers();
                if (!StartFfmpeg(targetSec))
                {
                    _failed = true;
                    var f = Failed; if (f != null) { try { f(); } catch { } }
                    return;
                }
                if (_paused && _waveHandle != IntPtr.Zero) { try { waveOutPause(_waveHandle); } catch { } }
            }
        }

        public void Reload(string path)
        {
            Close();
            _path = path;
            _failed = false; _endedFired = false; _duration = TimeSpan.Zero;
            _speed = 1.0; _seekSec = 0; _feedBytes = 0; _closed = false;
            double? est = MediaPreview.EstimateDurationMs(path);
            if (!est.HasValue || est.Value <= 0)
            {
                var vi = VideoProbe.ProbeCached(path);
                if (vi != null && vi.DurationSec > 0) est = vi.DurationSec * 1000;
            }
            if (!est.HasValue || est.Value <= 0) { _failed = true; return; }
            _duration = TimeSpan.FromMilliseconds(est.Value);
            if (!InitWaveOut()) { _failed = true; return; }
            if (!StartFfmpeg(0)) { CleanupWaveOut(); _failed = true; return; }
            _opened = true;
            var o = Opened; if (o != null) { try { o(); } catch { } }
            Play();
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _playing = false;
            StopThreadAndFfmpeg();
            CleanupWaveOut();
        }
    }
}