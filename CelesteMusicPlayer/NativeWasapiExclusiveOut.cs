using System;
using System.Threading;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 原生 WASAPI 独占输出器（复刻 ECHO NEXT 的 wasapi_exclusive 思路）。
    /// 直接驱动 IAudioClient/IAudioRenderClient：按「源格式优先」协商设备独占格式，
    /// 事件驱动音频线程把源 PCM 直写 render 缓冲，不经 NAudio 的 sample 转换层。
    /// 设备原生支持源格式时样本字节整块直通（严格 bit-perfect）；否则降级到设备 FLOAT32 做标准量化。
    /// </summary>
    internal sealed class NativeWasapiExclusiveOut : IDisposable
    {
        private enum Kind { Float32, Pcm24Packed, Pcm24In32, Pcm32, Pcm16 }

        private readonly EventWaitHandle _renderSignal = new(false, EventResetMode.AutoReset);
        private readonly EventWaitHandle _stopSignal = new(false, EventResetMode.ManualReset);
        private Thread? _renderThread;
        private volatile bool _disposed;
        private volatile bool _requestStop;
        private readonly object _framesLock = new();

        private NativeWasapi.IAudioClient? _audioClient;
        private NativeWasapi.IAudioRenderClient? _renderClient;
        private IWaveSourceProvider? _provider;
        private long _framesWritten;
        private long _lastUnderrunLogMs; // 限频记录 underrun 诊断
        private long _lastReadSlowMs;    // 限频记录"读源耗时尖峰"诊断（卡顿定位）
        private readonly object _seekLock = new();
        private TimeSpan? _pendingSeek; // 线程安全 seek 请求（由 render 线程消费）

        private Kind _kind;
        private int _rate;
        private int _channels;
        private int _srcBlock;    // 源每帧字节
        private int _dstBlock;    // 目标每帧字节
        private uint _bufferFrames;
        private bool _direct;     // 源布局 == 目标布局（整块 memcpy）

        public event Action? Ended;
        public event Action<Exception>? Failed;

        public TimeSpan Duration { get; private set; }
        public TimeSpan Position
        {
            get
            {
                lock (_framesLock)
                {
                    return _rate > 0 ? TimeSpan.FromSeconds((double)_framesWritten / _rate) : TimeSpan.Zero;
                }
            }
        }

        /// <summary>已写入的总帧数（累加，不随曲目切换归零；供上层按曲目相对进度换算）。</summary>
        public long FramesWritten
        {
            get
            {
                lock (_framesLock) { return _framesWritten; }
            }
        }

        /// <summary>协商后的采样率（帧/秒）。</summary>
        public int SampleRateValue => _rate;

        public string? LastError { get; private set; }
        public string? ActualFormatDescription { get; private set; }
        public bool IsStarted { get; private set; }

        /// <summary>最近一次初始化是否触发了 AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED 对齐 dance（供日志排障）。</summary>
        public bool LastAlignDance { get; private set; }

        /// <summary>用指定设备 + 输出源（PCM 无缝源或 DSD/DoP 源）初始化（格式协商 + Initialize + 取 render client + 绑定事件）。
        /// <paramref name="requireExactFormat"/> 为 true（DSD/DoP 直出）时只尝试「源格式精确直通」，任何降级都视为失败，
        /// 防止 Bit-perfect DSD 容器被设备降级改写。</summary>
        public bool Init(NativeWasapi.IMMDevice device, IWaveSourceProvider provider, bool requireExactFormat = false)
        {
            if (_audioClient != null)
            {
                LastError = "输出已初始化。";
                return false;
            }

            _provider = provider;
            Duration = provider.TotalTime;
            var src = provider.WaveFormat;
            if (src == null)
            {
                LastError = "输出源无有效格式。";
                return false;
            }

            _srcBlock = src.BlockAlign;
            _rate = src.SampleRate;
            _channels = src.Channels;

            // ---- 初始化日志（排障"假 bit-perfect"）：源格式与协商轨迹 ----
            string initLog = string.Format(
                "WASAPI独占协商 源=\"{0}\" {1}bit/{2}Hz/{3}ch", provider.GetType().Name, src.BitsPerSample, src.SampleRate, src.Channels);

            // 1) 尝试「源格式直通」（bit-perfect 优先）
            var srcExt = MakeSourceFormat(src);
            NativeWasapi.IAudioClient? ac;
            NativeWasapi.IAudioRenderClient? rc;
            uint frames;
            bool srcAlignDance = false;
            if (TryInitialize(device, ref srcExt, out ac, out rc, out frames, out srcAlignDance) == NativeWasapi.S_OK)
            {
                _audioClient = ac;
                _renderClient = rc;
                _bufferFrames = frames;
                _kind = ToKind(src);
                _dstBlock = src.BlockAlign;
                _direct = true;
                ActualFormatDescription = src.SampleRate + " Hz / " + src.BitsPerSample + " bit(源直通" + (requireExactFormat ? "/DoP" : "") + ") / " + src.Channels + " ch";
                StartupLog.Write(initLog + "  → 源直通成功 " + ActualFormatDescription + (srcAlignDance ? "（含对齐dance）" : ""));
                LastAlignDance = srcAlignDance;
                FinishInit();
                return true;
            }

            // 2) 仅 DSD/DoP 且要求精确格式：不再协商降级候选，直接失败（避免容器被设备改写破坏 bit-perfect）
            if (requireExactFormat)
            {
                LastError = "设备不支持 DSD/DoP 所需容器格式（" + src.SampleRate + "Hz/" + src.BitsPerSample + "bit/" + src.Channels + "ch）";
                StartupLog.Write(initLog + "  → 精确格式未支持，禁止降级，失败: " + LastError);
                return false;
            }

            // 3) 源格式设备不支持 → 按候选表逐次降级（FLOAT32 → PCM16 → PCM32 → PCM24IN32），
            //    仅接受「与源同布局」的格式（保证 bit-perfect 直出，无需转换）；源采样率设备不认时交给引擎按 MixFormat 重采样。
            var cands = new[] { Kind.Float32, Kind.Pcm16, Kind.Pcm32, Kind.Pcm24In32 };
            int lastHr = NativeWasapi.AUDCLNT_E_UNSUPPORTED_FORMAT;
            LastAlignDance = false;
            foreach (var kind in cands)
            {
                var cand = MakeFormat(kind, src.SampleRate, src.Channels);
                bool alignDance = false;
                int h = TryInitialize(device, ref cand, out ac, out rc, out frames, out alignDance);
                LastAlignDance |= alignDance;
                if (h != NativeWasapi.S_OK)
                {
                    if (h != NativeWasapi.AUDCLNT_E_UNSUPPORTED_FORMAT) lastHr = h;
                    StartupLog.Write(initLog + "  降级候选 " + kind + " 失败 hr=0x" + h.ToString("X8"));
                    continue;
                }

                if (!SameLayout(src, kind))
                {
                    try { Marshal.ReleaseComObject(rc!); } catch { }
                    try { Marshal.ReleaseComObject(ac!); } catch { }
                    continue; // 布局不符，无意义，释放后继续下一个
                }

                _audioClient = ac;
                _renderClient = rc;
                _bufferFrames = frames;
                _kind = kind;
                _channels = src.Channels;
                _dstBlock = src.BlockAlign;
                _direct = true; // 与源同布局 → 源字节整块直通（bit-perfect）
                ActualFormatDescription = src.SampleRate + " Hz / " + src.BitsPerSample + " bit / " + src.Channels + " ch";
                StartupLog.Write(initLog + "  → 候选降级 " + kind + " 成功 " + ActualFormatDescription + (LastAlignDance ? "（含对齐dance）" : ""));
                FinishInit();
                return true;
            }

            LastError = "设备不支持所需的独占格式（源 " + src.BitsPerSample + "bit@" + src.SampleRate + "Hz 不可用）HRESULT=0x" + lastHr.ToString("X8");
            StartupLog.Write(initLog + "  → 全部失败 " + LastError);
            return false;
        }

        private static bool SameLayout(WaveFormat src, Kind kind)
        {
            if (kind == Kind.Pcm16) return src.BitsPerSample == 16 && src.Encoding != WaveFormatEncoding.IeeeFloat;
            if (kind == Kind.Pcm32) return src.BitsPerSample == 32 && src.Encoding != WaveFormatEncoding.IeeeFloat;
            if (kind == Kind.Float32) return src.Encoding == WaveFormatEncoding.IeeeFloat && src.BitsPerSample == 32;
            return false; // Pcm24In32/Pcm24Packed 无同布局源直通
        }

        /// <summary>线程安全请求 seek（render 线程在下一帧消费并重定位源，避免与正在读源的线程竞争）。</summary>
        public void SeekTo(TimeSpan pos)
        {
            lock (_seekLock) { _pendingSeek = pos; }
        }

        /// <summary>启动播放。若从中间位置续播（暂停恢复/续播），传入已 seek 到的起始位置，
        /// 使 <see cref="Position"/> 从该处开始累计（保持绝对进度），而不会重置为 0。
        /// 注意：本方法假定在**渲染线程尚未启动**的新/已停止实例上调用；活跃播放中请改用 <see cref="SeekTo"/>，
        /// 避免把 baseline 与正在迭代累计的帧重复相加。</summary>
        public bool Play(TimeSpan? initialPosition = null)
        {
            if (_audioClient == null) return false;
            int hr = _audioClient.Start();
            if (hr != NativeWasapi.S_OK)
            {
                LastError = "WASAPI Start 失败 HRESULT=0x" + hr.ToString("X8");
                return false;
            }

            lock (_framesLock)
            {
                // 源在播放前已被 seek 到 initialPosition（见 PlayWavAsync 预seek），
                // 故先把 framesWritten 基准设到该帧，Position = 基准 + 已写帧，保持绝对进度。
                if (initialPosition.HasValue && initialPosition.Value > TimeSpan.Zero)
                {
                    _framesWritten = (long)(initialPosition.Value.TotalSeconds * _rate);
                }
                else
                {
                    _framesWritten = 0;
                }
            }

            _requestStop = false;
            IsStarted = true;
            _stopSignal.Reset();
            if (_renderThread == null || !_renderThread.IsAlive)
            {
                _renderThread = new Thread(RenderLoop)
                {
                    IsBackground = true,
                    Name = "NativeWasapiRender",
                    Priority = ThreadPriority.Highest // 配合 RenderLoop 内的 Pro Audio MM 提升，降低偶发调度延迟导致的微卡顿
                };
                _renderThread.Start();
            }

            return true;
        }

        public void Stop()
        {
            _requestStop = true;
            _stopSignal.Set();
            try { _renderThread?.Join(3000); } catch { }
            // 不在此主动 Stop/Reset：由 render 线程退出时自 Stop/Reset，避免主线程与 render 线程竞争同一 COM 对象（防 AccessViolation）
            lock (_framesLock) { _framesWritten = 0; }
            IsStarted = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch { }
            try { Marshal.ReleaseComObject(_renderClient!); } catch { }
            try { Marshal.ReleaseComObject(_audioClient!); } catch { }
            _stopSignal.Dispose();
            _renderSignal.Dispose();
            // 注意：_provider（SeamlessWaveProvider）归 HiFiOutputBackend 所有，不在此释放。
            _provider = null;
        }

        // ---------- 初始化 ----------

        private void FinishInit()
        {
            _audioClient!.SetEventHandle(_renderSignal.GetSafeWaitHandle().DangerousGetHandle());
            _audioClient.Reset();
        }

        /// <summary>尝试以给定独占格式初始化并取 render client；成功返回 S_OK。</summary>
        private static int TryInitialize(NativeWasapi.IMMDevice device, ref NativeWasapi.WAVEFORMATEXTENSIBLE wave,
            out NativeWasapi.IAudioClient? ac, out NativeWasapi.IAudioRenderClient? rc, out uint frames, out bool alignDance)
        {
            ac = null; rc = null; frames = 0; alignDance = false;
            var c = NativeWasapi.ActivateAudioClient(device);
            if (c == null) return NativeWasapi.REGDB_E_CLASSNOTREG;

            // 200ms（100ns 单位 = 2,000,000）。旧 FrameCountToHns(200000,...) 误算成 ~4.5s 巨大缓冲导致卡顿/迟滞
            long hns = 2000000L;
            int hr = c.Initialize(NativeWasapi.AUDCLNT_SHAREMODE_EXCLUSIVE, NativeWasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, hns, hns, ref wave, IntPtr.Zero);

            if (hr == NativeWasapi.AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED)
            {
                alignDance = true; // 记录对齐 dance（供日志/排障"假 bit-perfect"）
                uint aligned = 0;
                if (c.GetBufferSize(out aligned) == NativeWasapi.S_OK && aligned > 0)
                {
                    c.Reset(); Marshal.ReleaseComObject(c); c = null;
                    c = NativeWasapi.ActivateAudioClient(device);
                    if (c == null) return NativeWasapi.REGDB_E_CLASSNOTREG;
                    hns = FrameCountToHns(aligned, wave.Format.nSamplesPerSec);
                    hr = c.Initialize(NativeWasapi.AUDCLNT_SHAREMODE_EXCLUSIVE, NativeWasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, hns, hns, ref wave, IntPtr.Zero);
                }
            }

            if (hr != NativeWasapi.S_OK)
            {
                if (c != null) Marshal.ReleaseComObject(c);
                return hr;
            }

            if (c.GetBufferSize(out frames) != NativeWasapi.S_OK)
            {
                Marshal.ReleaseComObject(c);
                return NativeWasapi.E_PENDING;
            }

            Guid iid = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
            if (c.GetService(ref iid, out IntPtr ps) != NativeWasapi.S_OK || ps == IntPtr.Zero)
            {
                Marshal.ReleaseComObject(c);
                return NativeWasapi.E_PENDING;
            }

            NativeWasapi.IAudioRenderClient? r;
            try
            {
                r = Marshal.GetObjectForIUnknown(ps) as NativeWasapi.IAudioRenderClient;
            }
            finally
            {
                Marshal.Release(ps);
            }
            if (r == null)
            {
                Marshal.ReleaseComObject(c);
                return NativeWasapi.E_PENDING;
            }

            ac = c;
            rc = r;
            return NativeWasapi.S_OK;
        }

        private static NativeWasapi.WAVEFORMATEXTENSIBLE MakeSourceFormat(WaveFormat src)
        {
            Guid sub = src.Encoding == WaveFormatEncoding.IeeeFloat ? NativeWasapi.SubTypeIeeeFloat : NativeWasapi.SubTypePcm;
            return NativeWasapi.WAVEFORMATEXTENSIBLE.Make(src.SampleRate, src.Channels, src.BitsPerSample, sub, ChannelMask(src.Channels));
        }

        private static NativeWasapi.WAVEFORMATEXTENSIBLE MakeFormat(Kind kind, int rate, int ch)
        {
            return kind switch
            {
                Kind.Float32 => NativeWasapi.WAVEFORMATEXTENSIBLE.Make(rate, ch, 32, NativeWasapi.SubTypeIeeeFloat, ChannelMask(ch)),
                Kind.Pcm24In32 => NativeWasapi.WAVEFORMATEXTENSIBLE.Make(rate, ch, 32, NativeWasapi.SubTypePcm, ChannelMask(ch)),
                Kind.Pcm32 => NativeWasapi.WAVEFORMATEXTENSIBLE.Make(rate, ch, 32, NativeWasapi.SubTypePcm, ChannelMask(ch)),
                Kind.Pcm16 => NativeWasapi.WAVEFORMATEXTENSIBLE.Make(rate, ch, 16, NativeWasapi.SubTypePcm, ChannelMask(ch)),
                _ => NativeWasapi.WAVEFORMATEXTENSIBLE.Make(rate, ch, 24, NativeWasapi.SubTypePcm, ChannelMask(ch)),
            };
        }

        private static uint ChannelMask(int ch) => ch == 1 ? 0x0004u : ch == 2 ? 0x0003u : 0;

        private static Kind ToKind(WaveFormat src)
        {
            if (src.Encoding == WaveFormatEncoding.IeeeFloat) return Kind.Float32;
            return src.BitsPerSample switch { 16 => Kind.Pcm16, 32 => Kind.Pcm32, _ => Kind.Pcm24Packed };
        }

        private static long FrameCountToHns(uint frames, uint rate) => (long)frames * 10000000L / rate;

        // ---------- render 线程 ----------

        private void RenderLoop()
        {
            NativeWasapi.CoInitializeEx(IntPtr.Zero, 0); // render 线程 COM 用 MTA（对齐 ECHO com_scope_enter）
            // Pro Audio 任务提升（foobar/Exclusive-Mode 示例做法）：降低独占音频线程的调度延迟/爆音。
            uint taskIdx = 0;
            IntPtr avrtTask = NativeWasapi.AvSetMmThreadCharacteristicsW("Pro Audio", out taskIdx);
            try
            {
                var rc = _renderClient!;
                var src = _provider;
                if (src == null) return;
                int maxFrames = (int)_bufferFrames;
                if (maxFrames <= 0) return;

                var srcWf = src.WaveFormat;
                byte[] srcBuf = new byte[maxFrames * _srcBlock];
                var waits = new WaitHandle[] { _stopSignal, _renderSignal };

                while (!_requestStop && !_disposed)
                {
                    int wi = WaitHandle.WaitAny(waits, -1); // 无限等待 WASAPI 事件（对齐 ECHO INFINITE），避免轮询导致卡顿
                    if (_requestStop || _disposed) break;
                    if (wi != 1) continue; // 非 render 信号或超时

                    // 消费线程安全 seek 请求：在 render 线程自身重定位源，避免与正在读源的并发冲突
                    TimeSpan? seekReq;
                    lock (_seekLock)
                    {
                        seekReq = _pendingSeek;
                        _pendingSeek = null;
                    }
                    if (seekReq.HasValue)
                    {
                        src.Seek(seekReq.Value);
                        lock (_framesLock) { _framesWritten = (long)(seekReq.Value.TotalSeconds * _rate); }
                    }

                    // ECHO 式：每次取整缓冲，写满后整体提交；无需 GetCurrentPadding/frames 换算 → 无越界
                    if (rc.GetBuffer((uint)maxFrames, out IntPtr dst) != NativeWasapi.S_OK) break;

                    int got;
                    long readStart = Environment.TickCount64;
                    got = ReadFully(src, srcBuf, maxFrames * _srcBlock);
                    long readMs = Environment.TickCount64 - readStart;
                    // 诊断：单次读源耗时尖峰（>12ms）→ 读文件/源慢（磁盘/云盘定点区段）会成为卡顿点。
                    // 若读取总快但播放仍卡在固定位置，则指向设备/驱动/定时器层（与此处无关）。
                    if (readMs > 12 && _lastReadSlowMs < Environment.TickCount64)
                    {
                        _lastReadSlowMs = Environment.TickCount64 + 1000; // 限频 1s 一次
                        var ps = src.ProbeCurrentState;
                        long pos = ps?.Pos ?? 0, len = ps?.Len ?? 0;
                        StartupLog.Write($"[源诊断] 读源耗时尖峰={readMs}ms（需{maxFrames * _srcBlock}B）srcPos={pos}/{len} nextMount={src.NextMounted}");
                    }

                    if (got < srcBuf.Length)
                    {
                        Array.Clear(srcBuf, got, srcBuf.Length - got); // 不足部分静音，避免旧/越界数据
                        // 诊断：本次 WASAPI 缓冲未能从数据源读满（潜在 underrun → 播放卡顿）。
                        // 正常无缝续接时 ReadFully 可跨曲填满；此处仅当磁盘读不足或源已尽时出现。
                        long nowMs = Environment.TickCount64;
                        if (nowMs - _lastUnderrunLogMs > 1000) // 限频，避免刷屏
                        {
                            _lastUnderrunLogMs = nowMs;
                            var ps = src.ProbeCurrentState;
                            long pos = ps?.Pos ?? 0, len = ps?.Len ?? 0;
                            StartupLog.Write($"[源诊断] render underrun: 需要{srcBuf.Length}B 读得{got}B srcPos={pos}/{len} nextMount={src.NextMounted}");
                        }
                    }

                    if (_direct)
                    {
                        Marshal.Copy(srcBuf, 0, dst, maxFrames * _dstBlock);
                    }
                    else
                    {
                        ConvertToFloat(dst, srcBuf, maxFrames, srcWf);
                    }

                    rc.ReleaseBuffer((uint)maxFrames, 0);
                    lock (_framesLock) { _framesWritten += maxFrames; }
                }

                bool completed = !_requestStop && !_disposed;
                IsStarted = false;
                try { _audioClient!.Stop(); _audioClient.Reset(); } catch { }
                if (completed)
                {
                    Ended?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _requestStop = true;
                IsStarted = false;
                try { _audioClient?.Stop(); } catch { }
                Failed?.Invoke(ex);
            }
            finally
            {
                if (avrtTask != IntPtr.Zero)
                {
                    try { NativeWasapi.AvRevertMmThreadCharacteristics(avrtTask); } catch { }
                }
            }
        }

        private static int ReadFully(IWaveSourceProvider src, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = src.Read(buf, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>源整数/float PCM 转 FLOAT32（标准量化，[-1,1]）写入设备缓冲。仅在设备不支持源格式时使用。</summary>
        private void ConvertToFloat(IntPtr dst, byte[] srcPcm, int frames, WaveFormat src)
        {
            int ch = src.Channels;
            int srcBits = src.BitsPerSample;
            bool srcFloat = src.Encoding == WaveFormatEncoding.IeeeFloat;
            var data = new float[frames * ch];
            int si = 0;
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = srcFloat ? ReadFloat(srcPcm, ref si) : ReadIntAsFloat(srcPcm, ref si, srcBits);
            }
            GC.KeepAlive(this);
            byte[] bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            Marshal.Copy(bytes, 0, dst, bytes.Length);
        }

        private static float ReadFloat(byte[] b, ref int i)
        {
            float f = BitConverter.ToSingle(b, i);
            i += 4;
            return f;
        }

        private static float ReadIntAsFloat(byte[] b, ref int i, int bits)
        {
            long s;
            if (bits == 16)
            {
                s = (short)(b[i] | (b[i + 1] << 8));
                i += 2;
                return s / 32768f;
            }
            if (bits == 32)
            {
                s = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
                i += 4;
                return s / 2147483648f;
            }
            int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
            i += 3;
            s = (v & 0x800000) != 0 ? v - 0x1000000L : v;
            return s / 8388608f;
        }
    }
}
