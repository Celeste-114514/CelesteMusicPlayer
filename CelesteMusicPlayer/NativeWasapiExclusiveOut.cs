using System;
using System.Diagnostics;
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

        /// <summary>事件驱动缓冲大小（毫秒），须在 <see cref="Init"/> 之前设置。默认 100ms。
        /// 独占模式下这个值同时就是设备事件周期（见 <see cref="TryInitialize"/> 的说明）。
        /// 缓冲越大抗抖动余量越大（渲染线程偶尔慢一拍也不会断音），起播/拖动响应的延迟也越大。</summary>
        public int BufferMilliseconds { get; set; } = 100;

        /// <summary>渲染线程主动补货的轮询间隔（毫秒）。独占模式下事件周期必须等于缓冲，
        /// 也就是说事件只会在「整块缓冲被啃光」的那一刻才来——等到那一刻再读源，设备嘴里已经是空的，
        /// 读源的 15ms 就是 15ms 的静音。所以渲染线程不等事件、自己按这个间隔醒来补货：
        /// 设备缓冲被保持在接近满的水位，读源抖动被剩余存货吸收（2026-09-21 修）。</summary>
        private int FillPollMilliseconds => Math.Clamp(BufferMilliseconds / 8, 2, 15);

        /// <summary>最近一次初始化是否触发了 AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED 对齐 dance（供日志排障）。</summary>
        public bool LastAlignDance { get; private set; }

        /// <summary>累计欠载次数：渲染线程醒来时设备缓冲已被啃空（padding=0）的次数。
        /// 持续上涨 = 补货速度跟不上设备消耗，是卡顿的硬指标（供日志/后续自动调大缓冲用）。</summary>
        public long UnderrunCount => Interlocked.Read(ref _underrunCount);
        private long _underrunCount;

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
            if (TryInitialize(device, ref srcExt, out ac, out rc, out frames, out srcAlignDance, BufferMilliseconds) == NativeWasapi.S_OK)
            {
                _audioClient = ac;
                _renderClient = rc;
                _bufferFrames = frames;
                _kind = ToKind(src);
                _dstBlock = src.BlockAlign;
                _direct = true;
                ActualFormatDescription = src.SampleRate + " Hz / " + src.BitsPerSample + " bit(源直通" + (requireExactFormat ? "/DoP" : "") + ") / " + src.Channels + " ch";
                StartupLog.Write(initLog + "  → 源直通成功 " + ActualFormatDescription + DescribePeriod() + (srcAlignDance ? "（含对齐dance）" : ""));
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
                int h = TryInitialize(device, ref cand, out ac, out rc, out frames, out alignDance, BufferMilliseconds);
                LastAlignDance |= alignDance;
                if (h != NativeWasapi.S_OK)
                {
                    if (h != NativeWasapi.AUDCLNT_E_UNSUPPORTED_FORMAT) lastHr = h;
                    StartupLog.Write(initLog + "  降级候选 " + kind + " 失败 hr=0x" + h.ToString("X8"));
                    continue;
                }

                if (!SameLayout(src, kind))
                {
                    try { Marshal.ReleaseComObject(rc!); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeWasapiExclusiveOut.cs", caught); }
                    try { Marshal.ReleaseComObject(ac!); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeWasapiExclusiveOut.cs", caught); }
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

        /// <summary>初始化日志用：描述缓冲/周期配置。独占模式下二者恒等（微软规定），
        /// 抗抖动靠的是渲染线程按 <see cref="FillPollMilliseconds"/> 主动补货，而不是靠缩小周期。</summary>
        private string DescribePeriod()
        {
            return $" [周期=缓冲{BufferMilliseconds}ms 补货轮询{FillPollMilliseconds}ms]";
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

            // 预填充（pre-roll）：Start 之前先把整缓冲写成静音。事件驱动模式下设备 Start 后
            // 要过一个周期（10ms）才发首个事件，空缓冲起步会让设备最初这 10ms 无数据可播
            // → 每首曲目开头"啪/顿"一声。预滚后设备嘴里始终有货，第一个事件只管增量补空闲空间。
            if (_renderClient != null && _bufferFrames > 0)
            {
                // 预填充（pre-roll）：Start 之前先把整缓冲填满。
                // 旧实现填的是静音——设备会老老实实把这一整块静音播完（100ms 无声）才轮到真实音频，
                // 每首歌开头白白空一段。改成直接填真实音频（源布局与设备布局一致时），
                // 起播即出声；读不满/非直通布局时才退回静音兜底。
                int preFrames = (int)_bufferFrames;
                byte[] preBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(preFrames * _dstBlock);
                try
                {
                    Array.Clear(preBuf, 0, preFrames * _dstBlock);
                    uint flags = NativeWasapi.AUDCLNT_BUFFERFLAGS_SILENT;
                    if (_direct && _provider != null)
                    {
                        int got = ReadFully(_provider, preBuf, preFrames * _srcBlock);
                        if (got > 0)
                        {
                            preFrames = got / _dstBlock;
                            flags = 0; // 真实音频，不能标 SILENT
                        }
                    }

                    if (preFrames > 0 && _renderClient.GetBuffer((uint)preFrames, out IntPtr pre) == NativeWasapi.S_OK)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(preBuf, 0, pre, preFrames * _dstBlock);
                        _renderClient.ReleaseBuffer((uint)preFrames, flags);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(preBuf);
                }
            }

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
            try { _renderThread?.Join(3000); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeWasapiExclusiveOut.cs", caught); }
            // 不在此主动 Stop/Reset：由 render 线程退出时自 Stop/Reset，避免主线程与 render 线程竞争同一 COM 对象（防 AccessViolation）
            lock (_framesLock) { _framesWritten = 0; }
            IsStarted = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeWasapiExclusiveOut.cs", caught); }
            try { Marshal.ReleaseComObject(_renderClient!); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeWasapiExclusiveOut.cs", caught); }
            try { Marshal.ReleaseComObject(_audioClient!); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeWasapiExclusiveOut.cs", caught); }
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

        /// <summary>尝试以给定独占格式初始化并取 render client；成功返回 S_OK。
        /// 周期策略（2026-09-21 定论，勿再改回小周期）：
        /// 微软 MSDN 对 IAudioClient::Initialize 的硬规定——**独占模式 + 事件驱动**
        /// （AUDCLNT_STREAMFLAGS_EVENTCALLBACK）下 hnsPeriodicity 必须等于 hnsBufferDuration，
        /// 否则 Initialize 必然失败。2026-09-21 那次「小周期 10ms + 缓冲 100ms」的修复因此
        /// 从未真正生效：设备拒绝了请求，代码静默退回周期=缓冲，日志里留下的
        /// `[周期=缓冲100ms]` 就是证据。所以这里不再做无谓的两次尝试，直接用 周期=缓冲。
        /// 抗卡顿改由渲染线程主动补货实现（见 <see cref="FillPollMilliseconds"/> 与 RenderLoop）。
        /// </summary>
        private static int TryInitialize(NativeWasapi.IMMDevice device, ref NativeWasapi.WAVEFORMATEXTENSIBLE wave,
            out NativeWasapi.IAudioClient? ac, out NativeWasapi.IAudioRenderClient? rc, out uint frames,
            out bool alignDance, int bufferMs)
        {
            return InitExclusiveOnce(device, ref wave, out ac, out rc, out frames, out alignDance, bufferMs, bufferMs);
        }

        /// <summary>单次独占初始化（缓冲 + 周期）。含 AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED 对齐 dance。
        /// 失败时释放全部 COM 对象并返回 HRESULT（ac/rc 置 null）。</summary>
        private static int InitExclusiveOnce(NativeWasapi.IMMDevice device, ref NativeWasapi.WAVEFORMATEXTENSIBLE wave,
            out NativeWasapi.IAudioClient? ac, out NativeWasapi.IAudioRenderClient? rc, out uint frames,
            out bool alignDance, int bufferMs, int periodMs)
        {
            ac = null; rc = null; frames = 0; alignDance = false;
            var c = NativeWasapi.ActivateAudioClient(device);
            if (c == null) return NativeWasapi.REGDB_E_CLASSNOTREG;

            // 缓冲毫秒 → 100ns 单位（1ms = 10,000）。独占 + 事件驱动下周期恒等于缓冲（微软规定），
            // 即设备会在「整块缓冲被啃光」时才发事件。缓冲越大，渲染线程攒的存货越多、越抗抖动，
            // 但起播/拖动的响应延迟也越大。默认 100ms 折中稳定性与响应速度。
            long hnsBuf = Math.Clamp(bufferMs, 10, 1000) * 10000L;
            long hnsPer = hnsBuf;
            int hr = c.Initialize(NativeWasapi.AUDCLNT_SHAREMODE_EXCLUSIVE, NativeWasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, hnsBuf, hnsPer, ref wave, IntPtr.Zero);

            if (hr == NativeWasapi.AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED)
            {
                alignDance = true; // 记录对齐 dance（供日志/排障"假 bit-perfect"）
                uint aligned = 0;
                if (c.GetBufferSize(out aligned) == NativeWasapi.S_OK && aligned > 0)
                {
                    c.Reset(); Marshal.ReleaseComObject(c); c = null;
                    c = NativeWasapi.ActivateAudioClient(device);
                    if (c == null) return NativeWasapi.REGDB_E_CLASSNOTREG;
                    hnsBuf = FrameCountToHns(aligned, wave.Format.nSamplesPerSec);
                    hnsPer = Math.Min(hnsPer, hnsBuf); // 对齐后周期仍不得越过缓冲
                    hr = c.Initialize(NativeWasapi.AUDCLNT_SHAREMODE_EXCLUSIVE, NativeWasapi.AUDCLNT_STREAMFLAGS_EVENTCALLBACK, hnsBuf, hnsPer, ref wave, IntPtr.Zero);
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
                int pollMs = FillPollMilliseconds;

                // ---- 高精度渲染诊断（2026-09-22）----
                // 旧诊断用 Environment.TickCount64 计时，它的精度只有 ~15.6ms（Windows 默认时钟粒度），
                // 于是"读源耗时 15ms"其实是量化噪音、根本不能当证据——实测每次读数都恰好落在 15/16ms。
                // 改用 Stopwatch（高精度计数器），并额外统计「相邻两次补货的间隔」：
                //   间隔抖（远大于 pollMs） = 渲染线程没按时醒 → GC 暂停 / 系统 DPC / 驱动 / 等锁；
                //   读源慢（单次 Read 耗时长）              = 磁盘或源慢（云盘/杀毒扫描/转码抢 I/O）。
                // 两者在日志里分开报，一眼能看出卡顿到底归谁。
                long tsPrev = 0, tsReport = Stopwatch.GetTimestamp(), tsGapLog = 0, tsReadLog = 0, tsShortLog = 0;
                double maxGapMs = 0, maxReadMs = 0;
                double minPad = double.MaxValue, sumPad = 0;
                long loops = 0, gapSpikes = 0, padZero = 0, starve = 0;
                double reportEverySec = 10.0;

                // GC 归因（2026-09-22 加）：ECHO 在同一台机器上不卡，而它是 Rust 写的、**没有 GC**。
                // Celeste 是 .NET：gen2 / 大对象堆回收会 STW（stop-the-world）暂停**所有**托管线程，
                // 渲染线程也不例外，再高的线程优先级、再好的 MMCSS 也躲不掉。
                // 而一首歌要整读 37MB + 预载下一首 33MB 进大对象堆 —— 正是 gen2 的典型触发源，
                // 这与"起播时卡顿尤为明显""跟文件大小无关""ECHO 不卡"三条现象完全吻合。
                // 因此每次尖峰都记下各代 GC 增量：gen2 涨了 = GC 是元凶；全 0 = 系统/驱动侧。
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                double bufMsReal = _rate > 0 ? maxFrames * 1000.0 / _rate : 0; // 缓冲真实时长（毫秒）

                while (!_requestStop && !_disposed)
                {
                    // 关键（2026-09-21 修独占卡顿）：**不等设备事件**。
                    // 独占模式下事件周期恒等于缓冲，事件只在「整块缓冲被啃光」那一刻才来——
                    // 等到那一刻再读源，设备嘴里已经是空的，读源/GC/等锁的 15ms 就是 15ms 的静音。
                    // 改成按 pollMs 主动醒来，每次把空闲空间（缓冲 − padding）补满：
                    // 设备缓冲被维持在接近满的水位，剩余存货就是抗抖动的余量。
                    int wi = WaitHandle.WaitAny(waits, pollMs);
                    if (_requestStop || _disposed) break;
                    // wi==0 停止信号；wi==1 设备事件（也顺手补货）；WaitTimeout(258) 轮询到点。
                    if (wi == 0) continue;

                    long tsNow = Stopwatch.GetTimestamp();
                    if (tsPrev != 0)
                    {
                        double gapMs = (tsNow - tsPrev) * 1000.0 / Stopwatch.Frequency;
                        if (gapMs > maxGapMs) maxGapMs = gapMs;
                        if (gapMs > pollMs * 3.0)
                        {
                            gapSpikes++;
                            // 间隔 > 缓冲时长 = 设备在这段时间里必然把存货啃光并饿着 → 实锤可闻断音
                            if (bufMsReal > 0 && gapMs > bufMsReal) starve++;
                            if (tsNow - tsGapLog > Stopwatch.Frequency * 2)
                            {
                                tsGapLog = tsNow;
                                int n0 = GC.CollectionCount(0), n1 = GC.CollectionCount(1), n2 = GC.CollectionCount(2);
                                StartupLog.Write(string.Format(
                                    "[渲染诊断] 补货间隔尖峰={0:F1}ms（轮询{1}ms／缓冲{2:F0}ms）{3} GC: gen0+{4} gen1+{5} gen2+{6} 堆={7:F0}MB 模式={8}",
                                    gapMs, pollMs, bufMsReal,
                                    (bufMsReal > 0 && gapMs > bufMsReal) ? "★超过缓冲=必然断流" : "",
                                    n0 - gc0, n1 - gc1, n2 - gc2,
                                    GC.GetTotalMemory(false) / 1048576.0, System.Runtime.GCSettings.LatencyMode));
                                gc0 = n0; gc1 = n1; gc2 = n2;
                            }
                        }
                    }
                    tsPrev = tsNow;
                    loops++;

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

                    // 只填「空闲空间」= 整缓冲 − 设备已排队未播帧（GetCurrentPadding）。
                    // 事件驱动标准做法（对齐微软 RenderAudio 示例）：小周期下设备每 10ms 发一次事件，
                    // 每次只补这 10ms 的空闲；读源/GC/调度抖动（实测 15ms 尖峰）由设备嘴里
                    // 剩下的 ~90ms 存货吸收，不再变成可闻卡顿。旧实现每次取整缓冲（100ms），
                    // 读不满整缓冲就整体提交不足数据 → underrun → "时不时顿一下"。
                    if (_audioClient!.GetCurrentPadding(out uint pad) != NativeWasapi.S_OK) break;

                    // 缓冲水位统计（2026-09-22 修正判据）：
                    // 独占 + 事件驱动下周期恒等于缓冲，实测 pad 在每个周期边界必然归零一次
                    // （旧日志里 underrun 严格每 100ms +1、2.1 秒恰好 +21，规律得不像真欠载，
                    //  而是这个 API 配置的固有现象）。所以「pad==0 次数」不能当欠载硬指标。
                    // 有意义的是**水位**：平均/最低 padding 越接近满缓冲，抗抖动余量越足；
                    // 长期贴着 0 才说明补货真的跟不上。
                    if (pad < minPad) minPad = pad;
                    sumPad += pad;
                    if (pad == 0) padZero++;

                    int toFill = (int)Math.Min((long)maxFrames - pad, maxFrames);
                    if (toFill <= 0) continue; // 缓冲仍是满的（设备还没消耗）：无空闲空间可写

                    if (rc.GetBuffer((uint)toFill, out IntPtr dst) != NativeWasapi.S_OK) break;

                    int want = toFill * _srcBlock;
                    int got;
                    // 高精度计时（2026-09-22）：旧代码用 Environment.TickCount64，精度只有 ~15.6ms，
                    // 读数被量化成 15/16ms，看着像"读源慢"其实是时钟粒度的假象。这里改用 Stopwatch。
                    long readStart = Stopwatch.GetTimestamp();
                    got = ReadFully(src, srcBuf, want);
                    double readMs = (Stopwatch.GetTimestamp() - readStart) * 1000.0 / Stopwatch.Frequency;
                    if (readMs > maxReadMs) maxReadMs = readMs;
                    // 真实阈值 8ms：一次只补 pollMs(12ms) 的量，读它超过 8ms 说明磁盘/源确实慢。
                    if (readMs > 8 && tsNow - tsReadLog > Stopwatch.Frequency * 2)
                    {
                        tsReadLog = tsNow;
                        var ps = src.ProbeCurrentState;
                        long pos = ps?.Pos ?? 0, len = ps?.Len ?? 0;
                        StartupLog.Write(string.Format(
                            "[渲染诊断] 读源慢={0:F2}ms（需{1}B）srcPos={2}/{3} nextMount={4}",
                            readMs, want, pos, len, src.NextMounted));
                    }

                    if (got < want)
                    {
                        Array.Clear(srcBuf, got, want - got); // 不足部分静音，避免旧/越界数据
                        // 这才是**真的**欠载：源没喂满本次要的量（曲末/无缝未续上/磁盘跟不上）。
                        Interlocked.Increment(ref _underrunCount);
                        if (tsNow - tsShortLog > Stopwatch.Frequency) // 限频 1s 一次
                        {
                            tsShortLog = tsNow;
                            var ps = src.ProbeCurrentState;
                            long pos = ps?.Pos ?? 0, len = ps?.Len ?? 0;
                            StartupLog.Write($"[渲染诊断] 源没喂满：需要{want}B 读得{got}B srcPos={pos}/{len} nextMount={src.NextMounted}");
                        }
                    }

                    if (_direct)
                    {
                        Marshal.Copy(srcBuf, 0, dst, want);
                    }
                    else
                    {
                        ConvertToFloat(dst, srcBuf, toFill, srcWf);
                    }

                    rc.ReleaseBuffer((uint)toFill, 0);
                    lock (_framesLock) { _framesWritten += toFill; }

                    // 每 10 秒汇总一次：卡顿归因就看这行。
                    //   水位长期贴 0 + 读源慢     → 磁盘/源跟不上（需 feeder 队列或换缓存策略）
                    //   水位健康但间隔尖峰多      → 渲染线程被系统挂起（GC/DPC/驱动），应用侧救不了多少
                    if ((tsNow - tsReport) / (double)Stopwatch.Frequency > reportEverySec)
                    {
                        double avgPad = loops > 0 ? sumPad / loops : 0;
                        double zeroRatio = loops > 0 ? (double)padZero / loops : 0;
                        int m0 = GC.CollectionCount(0), m1 = GC.CollectionCount(1), m2 = GC.CollectionCount(2);
                        StartupLog.Write(string.Format(
                            "[渲染统计] {0:F0}s 补货{1}次 | 水位 最低{2:F0}/平均{3:F0}/满{4}帧，空缓冲{5}次({6:P0}) | 间隔 最大{7:F1}ms 尖峰{8}次 断流{9}次 | 读源 最大{10:F2}ms | 真欠载{11}次 | GC gen0+{12} gen1+{13} gen2+{14} 堆{15:F0}MB 模式={16}",
                            (tsNow - tsReport) / (double)Stopwatch.Frequency, loops,
                            minPad == double.MaxValue ? 0 : minPad, avgPad, maxFrames, padZero, zeroRatio,
                            maxGapMs, gapSpikes, starve, maxReadMs, UnderrunCount,
                            m0 - gc0, m1 - gc1, m2 - gc2,
                            GC.GetTotalMemory(false) / 1048576.0, System.Runtime.GCSettings.LatencyMode));
                        tsReport = tsNow;
                        maxGapMs = 0; maxReadMs = 0; minPad = double.MaxValue; sumPad = 0;
                        loops = 0; gapSpikes = 0; padZero = 0; starve = 0;
                        gc0 = m0; gc1 = m1; gc2 = m2;
                    }
                }

                bool completed = !_requestStop && !_disposed;
                IsStarted = false;
                try { _audioClient!.Stop(); _audioClient.Reset(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeWasapiExclusiveOut.cs", caught); }
                if (completed)
                {
                    Ended?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _requestStop = true;
                IsStarted = false;
                try { _audioClient?.Stop(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeWasapiExclusiveOut.cs", caught); }
                Failed?.Invoke(ex);
            }
            finally
            {
                if (avrtTask != IntPtr.Zero)
                {
                    try { NativeWasapi.AvRevertMmThreadCharacteristics(avrtTask); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("NativeWasapiExclusiveOut.cs", caught); }
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
