using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HoverExplorer
{
    // 全量波形解码：MF SourceReader 后台静默解码整个音频文件 → 单级峰值/RMS 摘要（2048 桶）。
    // 流式处理：每块 PCM 更新桶统计后即丢弃，峰值内存 <1MB；桶下标按采样时间戳/总时长线性映射，
    // 与播放进度轴对齐。失败置 Failed，调用方回退“边播边攒”的环回包络。
    // GUID 与虚表顺序对照 Windows SDK 26100 的 mfapi.h / mfobjects.h / mfreadwrite.h
    internal sealed class WaveDecoder
    {
        internal const int Buckets = 2048;

        internal readonly float[] Rms = new float[Buckets];
        internal readonly float[] Peak = new float[Buckets];
        private readonly double[] _sq = new double[Buckets];
        private readonly int[] _cnt = new int[Buckets];
        private readonly double _dur100ns;   // 总时长（100ns 单位）
        private volatile bool _done, _failed, _cancel;


        internal string Path { get; private set; }
        internal bool Done { get { return _done; } }
        internal bool Failed { get { return _failed; } }

        private WaveDecoder(string path, double durationSec)
        {
            Path = path;
            _dur100ns = durationSec * 1e7;
        }

        // 时长就绪才可启动（桶下标映射依赖总时长）；调用方在拿到 Duration 后发起
        internal static WaveDecoder Begin(string path, double durationSec)
        {
            if (string.IsNullOrEmpty(path) || durationSec <= 0) return null;
            var d = new WaveDecoder(path, durationSec);
            var t = new Thread(d.Run)
            {
                IsBackground = true,
                Name = "WaveDecoder",
                Priority = ThreadPriority.BelowNormal   // 不与播放/预览抢 CPU
            };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            return d;
        }

        internal void Cancel() { _cancel = true; }

        private void Run()
        {
            IMFSourceReader reader = null;
            try
            {
                MFStartup(0x20070, 0);
                MFCreateSourceReaderFromURL(Path, IntPtr.Zero, out reader);
                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(FirstAudio, true);

                // 部分类型（只给 Audio+PCM）→ SourceReader 自动插入解码器/重采样，
                // 默认输出 16bit 整型 PCM（源为 float 时给 float，按实际子类型解析）
                IMFAttributes mt;
                MFCreateMediaType(out mt);
                var audio = MediaTypeAudio;
                var pcm = AudioPcm;
                mt.SetGuid(MtMajorType, ref audio);
                mt.SetGuid(MtSubtype, ref pcm);
                reader.SetCurrentMediaType(FirstAudio, IntPtr.Zero, mt);

                IMFAttributes cur;
                reader.GetCurrentMediaType(FirstAudio, out cur);
                int ch, rate;
                cur.GetUint32(MtChannels, out ch);
                cur.GetUint32(MtRate, out rate);
                if (ch < 1) ch = 1;
                if (rate < 8000) rate = 44100;   // 拿不到采样率时按常用值兜底（只影响桶内插值精度）

                Guid sub;
                cur.GetGuid(MtSubtype, out sub);
                bool isFloat = sub == AudioFloat;
                Marshal.ReleaseComObject(cur);
                Marshal.ReleaseComObject(mt);

                while (!_cancel)
                {
                    int act, flags;
                    long ts;
                    IMFSample sample;
                    reader.ReadSample(FirstAudio, 0, out act, out flags, out ts, out sample);
                    if ((flags & 0x2) != 0) break;   // MF_SOURCE_READERF_ENDOFSTREAM
                    if (sample == null) { if ((flags & 0x1) != 0) { _failed = true; break; } continue; }
                    IMFMediaBuffer buf;
                    sample.ConvertToContiguousBuffer(out buf);
                    IntPtr p;
                    int maxLen, curLen;
                    buf.Lock(out p, out maxLen, out curLen);
                    try { Accumulate(p, curLen, ch, rate, isFloat, ts); }
                    finally { buf.Unlock(); }
                    Marshal.ReleaseComObject(buf);
                    Marshal.ReleaseComObject(sample);
                }
                FinalizeBuckets();
                _done = true;
                Program.DebugLog("[wave] decode " + (_failed ? "failed" : "done") + " " + Path);
            }
            catch (Exception ex)
            {
                _failed = true;
                Program.DebugLog("[wave] decode fail: " + ex.Message);
            }
            finally
            {
                try { if (reader != null) Marshal.ReleaseComObject(reader); } catch { }
                try { MFShutdown(0); } catch { }
            }
        }

        // 一块 PCM（MF 单次 ReadSample 约 50-250ms，会跨多个桶）→ 逐帧按时间戳落桶
        private void Accumulate(IntPtr p, int len, int ch, int rate, bool isFloat, long ts100ns)
        {
            int bps = isFloat ? 4 : 2;
            int frameBytes = ch * bps;
            if (frameBytes <= 0) return;
            int frames = len / frameBytes;
            if (frames <= 0) return;
            double frame100ns = 1e7 / rate;
            double k = Buckets / _dur100ns;

            int n = frames * ch;
            if (isFloat)
            {
                var buf = new float[n];
                Marshal.Copy(p, buf, 0, n);
                for (int i = 0; i < frames; i++)
                {
                    float s = 0f;
                    for (int c = 0; c < ch; c++) s += buf[i * ch + c];
                    s /= ch;
                    Put(ts100ns + i * frame100ns, k, s);

                }
            }
            else
            {
                var buf = new short[n];
                Marshal.Copy(p, buf, 0, n);
                for (int i = 0; i < frames; i++)
                {
                    float s = 0f;
                    for (int c = 0; c < ch; c++) s += buf[i * ch + c];
                    s = s / ch / 32768f;
                    Put(ts100ns + i * frame100ns, k, s);

                }
            }
        }

        private void Put(double t100ns, double k, float s)
        {
            int b = (int)(t100ns * k);
            if (b < 0) b = 0;
            else if (b >= Buckets) b = Buckets - 1;
            float a = s < 0 ? -s : s;
            if (a > Peak[b]) Peak[b] = a;
            _sq[b] += (double)s * s;
            _cnt[b]++;
        }

        private void FinalizeBuckets()
        {
            for (int i = 0; i < Buckets; i++)
                Rms[i] = _cnt[i] > 0 ? (float)Math.Sqrt(_sq[i] / _cnt[i]) : 0f;

        }

        private const int AllStreams = -2;    // MF_SOURCE_READER_ALL_STREAMS (0xFFFFFFFE)
        private const int FirstAudio = -3;    // MF_SOURCE_READER_FIRST_AUDIO_STREAM (0xFFFFFFFD)

        // ===== COM 互操作（虚表槽位顺序必须与 SDK 头一致，未调用的方法占位即可）=====
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown(int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IMFAttributes mt);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

        private static readonly Guid MtMajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MtSubtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MtChannels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid MtRate = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MediaTypeAudio = new Guid("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid AudioPcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid AudioFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            void GetItem(ref Guid key, IntPtr value);     // 1
            void GetItemType(ref Guid key, IntPtr type);  // 2
            void CompareItem();                           // 3
            void Compare();                               // 4
            void GetUint32(ref Guid key, out int v);      // 5
            void GetUint64(ref Guid key, out long v);     // 6
            void GetDouble();                             // 7
            void GetGuid(ref Guid key, out Guid v);       // 8
            void GetStringLength();                       // 9
            void GetString();                             // 10
            void GetAllocatedString();                    // 11
            void GetBlobSize();                           // 12
            void GetBlob();                               // 13
            void GetAllocatedBlob();                      // 14
            void GetUnknown();                            // 15
            void SetItem();                               // 16
            void DeleteItem();                            // 17
            void DeleteAllItems();                        // 18
            void SetUint32(ref Guid key, int v);          // 19
            void SetUint64();                             // 20
            void SetDouble();                             // 21
            void SetGuid(ref Guid key, ref Guid v);       // 22
            void SetString();                             // 23
            void SetBlob();                               // 24
            void SetUnknown();                            // 25
            void LockStore();                             // 26
            void UnlockStore();                           // 27
            void GetCount();                              // 28
            void GetItemByIndex();                        // 29
            void CopyAllItems();                          // 30
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // IMFAttributes 基类 30 个槽位（顺序一致，未用占位）
            void Attr01(); void Attr02(); void Attr03(); void Attr04(); void Attr05();
            void Attr06(); void Attr07(); void Attr08(); void Attr09(); void Attr10();
            void Attr11(); void Attr12(); void Attr13(); void Attr14(); void Attr15();
            void Attr16(); void Attr17(); void Attr18(); void Attr19(); void Attr20();
            void Attr21(); void Attr22(); void Attr23(); void Attr24(); void Attr25();
            void Attr26(); void Attr27(); void Attr28(); void Attr29(); void Attr30();
            // IMFSample 自有方法
            void GetSampleFlags();
            void SetSampleFlags();
            void GetSampleTime(out long ts);
            void SetSampleTime();
            void GetSampleDuration();
            void SetSampleDuration();
            void GetBufferCount();
            void GetBufferByIndex();
            void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            void Lock(out IntPtr ppBuffer, out int maxLength, out int currentLength);
            void Unlock();
            void GetCurrentLength(out int currentLength);
            void SetCurrentLength();
            void GetMaxLength();
        }

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            void GetStreamSelection(int stream, out bool selected);
            void SetStreamSelection(int stream, bool selected);
            void GetNativeMediaType();
            void GetCurrentMediaType(int stream, out IMFAttributes mediaType);
            void SetCurrentMediaType(int stream, IntPtr reserved, IMFAttributes mediaType);
            void SetCurrentPosition();
            void ReadSample(int stream, int flags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample sample);
            void Flush();
            void GetServiceForStream();
            void GetPresentationAttribute();
        }
    }
}
