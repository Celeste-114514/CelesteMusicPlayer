using System;
using System.Threading;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 自研原生内核（celeste_core.dll，MIT）独占输出适配器，接口与自研
    /// <see cref="NativeWasapiExclusiveOut"/>、ECHO 核心 <see cref="EchoCoreOutput"/>
    /// 完全一致（<see cref="IExclusiveOutput"/>），供设置里三选一切换。
    ///
    /// 与另外两个内核的架构差异：
    ///   自研托管版：C# 渲染线程直接读源 → 写设备（.NET GC STW 会把它冻住 → 偶发断音）；
    ///   ECHO 核心：  原生渲染线程只 memcpy，但 feeder 灌的是 float（24 位精度上限，
    ///               32bit 整数源做不到字节直通）；
    ///   本类（原生内核）：原生渲染线程只 memcpy，feeder 灌**整数字节原样**
    ///               （协商保证端点容器 == 源布局）→ 全链路不碰 float，
    ///               DSP 全关时端到端 bit-perfect（字节级，比 ECHO 的"数值无损"更硬）。
    ///
    /// 快进/seek：SeekTo 只置请求；feeder 下一轮循环开头消费（重定位源 →
    /// replace 清 ring + 重灌 + 淡入 10ms 防爆音 + framesPlayed 归零）。
    /// </summary>
    internal sealed class NativeCoreOutput : IExclusiveOutput
    {
        // feeder 每次灌入的帧数。与 ECHO 核心同口径（4096 帧/块：44.1k ≈93ms，96k ≈43ms）；
        // ring 常备 1.5s 存货，块越小背压越平滑、P/Invoke 次数略多（开销可忽略）。
        private const int ChunkFrames = 4096;

        private readonly object _seekLock = new();
        private Thread? _feeder;
        private volatile bool _stopRequested;
        private volatile bool _disposed;
        private TimeSpan? _pendingSeek; // feeder 消费的 seek 请求（唯一同时碰源与 ring 的线程，天然无竞争）

        private IWaveSourceProvider? _provider;
        private IntPtr _handle;
        private byte[] _readBuf = Array.Empty<byte>();  // feeder 复用（零分配）
        private int _srcBlockAlign = 4;
        private int _srcBits = 16;
        private uint _srcTag = NativeCoreAudio.FmtPcm16;
        private int _carryLen; // 上一个读周期剩下的不足一帧字节（一般是 0）
        private readonly byte[] _carry = new byte[7]; // blockAlign 最大 8，余数最多 7 字节

        private long _framesBaseline; // 当前曲目（或 seek 目标）起始帧基准，保持绝对进度
        private int _rate;
        private int _channels = 2;
        private int _bufferFrames;
        private string _endpointFormat = "?";

        // ---------- feeder 侧窗口诊断（2026-09-23 用户实机：native2 偶有卡顿但比自研少） ----------
        // 内核渲染线程是原生 C++（CRITICAL+1ms 定时器，不受 GC 影响），其补货间隔/欠载统计
        // 内核一直在记（maxGapMs/spikeCount/underrun，读走即清零的窗口值），但 C# 侧此前无人
        // 读取——播放全程零诊断输出，卡顿无法归因。这里把统计接到 feeder 循环打成日志，
        // 与自研内核 [渲染统计]/[渲染诊断] 同口径，三引擎日志可直接 A/B 对比。
        private readonly System.Diagnostics.Stopwatch _aggWatch = new();
        private int _aggWrites;
        private int _aggMaxGap;
        private ulong _aggSpikes;
        private int _aggMinReady;
        private long _aggReadySum;
        private int _aggReadyCount;
        private int _lastReady;
        private double _readMaxMs;
        private int _gc0Base, _gc1Base, _gc2Base;
        private long _underrunCbBase, _underrunFramesBase;
        private int _lastCapacity;

        private static readonly TimeSpan AggPeriod = TimeSpan.FromSeconds(5);
        private const int SpikeThresholdMs = 30; // 轮询12ms 下 >2.5× 视为异常尖峰

        public event Action? Ended;
        public event Action<Exception>? Failed;

        public TimeSpan Duration { get; private set; }

        public int BufferMilliseconds { get; set; } = 100;

        public string? LastError { get; private set; }

        public string? ActualFormatDescription { get; private set; }

        /// <summary>设备端协商结果（结构化）。Init 未成功时为 null。
        /// 协商成功时端点容器必与源布局一致（否则原生侧 Init 直接失败），率=源率、位深=源位深。</summary>
        public AudioFormat? NegotiatedFormat { get; private set; }

        /// <summary>本内核 Init 成功即端点容器==源布局（字节直通）→ Lossless；
        /// 万一 stats 回报的容器名与源标签不符（内部不一致），据实降级 Degraded 并留痕。</summary>
        public DevicePath DevicePathKind { get; private set; } = DevicePath.Unknown;

        /// <summary>设备端点容器名（pcm16/pcm24/pcm24in32/pcm32/float32 的人话版）。</summary>
        public string? DeviceEndpointName { get; private set; }

        public bool LastAlignDance { get; private set; } // stats 读回的周期与请求不符 = 发生过对齐 dance

        /// <summary>协商后的采样率（帧/秒）。Init 前为 0。</summary>
        public int SampleRateValue => _rate;

        public bool IsStarted { get; private set; }

        /// <summary>已播帧数（绝对：曲目起点基准 + 内核已播）。</summary>
        public long FramesWritten
        {
            get
            {
                if (_handle == IntPtr.Zero) return 0;
                return _framesBaseline + (long)NativeCoreAudio.Stats(_handle).FramesPlayed;
            }
        }

        /// <summary>ring 欠载回调累计（feeder 供不上时的硬指标；A/B 对比看这个）。</summary>
        public long UnderrunCount
        {
            get
            {
                if (_handle == IntPtr.Zero) return 0;
                return (long)NativeCoreAudio.Stats(_handle).UnderrunCallbacks;
            }
        }

        /// <summary>源已读尽（feeder 已 mark_input_ended）且 ring 存货也播空 = 这一曲真的放完了。
        /// 不能拿「reader 游标到头」当播完：feeder 会超前读源备货（ring 最多囤 1.5s+），
        /// reader 到头时歌曲尾巴还在 ring/设备缓冲里，那时切歌会把尾巴切掉。</summary>
        public bool IsDrained
        {
            get
            {
                if (_handle == IntPtr.Zero) return false;
                return NativeCoreAudio.Stats(_handle).Drained != 0;
            }
        }

        /// <summary>渲染线程窗口统计（诊断）：[上次读取以来的最大补货间隔ms, 尖峰次数, ring存货帧, 容量帧]。
        /// maxGapMs/spikeCount 是「自上次调用起」的窗口值（内核读走即清零），调用方轮询即得连续曲线。</summary>
        public (int MaxGapMs, ulong Spikes, int ReadyFrames, int CapacityFrames) SnapshotWindow()
        {
            if (_handle == IntPtr.Zero) return (0, 0, 0, 0);
            var st = NativeCoreAudio.Stats(_handle);
            return (st.MaxGapMs, st.SpikeCount, st.ReadyFrames, st.CapacityFrames);
        }

        public bool Init(NativeWasapi.IMMDevice device, IWaveSourceProvider provider, bool requireExactFormat = false)
        {
            if (_handle != IntPtr.Zero)
            {
                LastError = "输出已初始化。";
                return false;
            }

            if (requireExactFormat)
            {
                // DSD/DoP 直出走 DoP 容器（原生侧需 start_dop 通道，第一阶段未接）→ 明确拒绝，让上层回退自研。
                LastError = "原生内核暂不支持 DSD/DoP 精确直出（requireExact），请改用自研内核。";
                return false;
            }

            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Duration = provider.TotalTime;
            var src = provider.WaveFormat;
            if (src == null)
            {
                LastError = "输出源无有效格式。";
                return false;
            }

            _srcBlockAlign = src.BlockAlign;
            _srcBits = src.BitsPerSample;
            _channels = src.Channels;
            if (src.Channels < 1 || src.Channels > 2)
            {
                LastError = "原生内核仅支持 1/2 声道独占（当前 " + src.Channels + "ch）。";
                return false;
            }

            // 源布局标签：协商只接受端点容器 == 源布局（bit-perfect 字节直通的前提）。
            // 24bit 分两种容器：packed（BlockAlign=3×ch，转码 WAV 的 s24le）与 in32（4×ch，高位 24 位）。
            if (src.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                _srcTag = NativeCoreAudio.FmtFloat32;
            }
            else if (_srcBits == 16)
            {
                _srcTag = NativeCoreAudio.FmtPcm16;
            }
            else if (_srcBits == 24)
            {
                _srcTag = _srcBlockAlign == _channels * 4
                    ? NativeCoreAudio.FmtPcm24In32   // 4 字节容器
                    : NativeCoreAudio.FmtPcm24;      // 3 字节紧密排列
            }
            else if (_srcBits == 32)
            {
                _srcTag = NativeCoreAudio.FmtPcm32;
            }
            else
            {
                LastError = "原生内核暂不支持 " + _srcBits + "bit 源（支持 16/24/32bit 整数与 float32）。";
                return false;
            }
            if (_srcBlockAlign != _channels * TagBytes(_srcTag))
            {
                LastError = "源缓冲区布局与格式标签不符（blockAlign=" + _srcBlockAlign + "），拒绝起播。";
                return false;
            }
            _carryLen = 0;

            // 请求缓冲帧 = 缓冲毫秒 × 采样率（DLL 按设备周期/对齐规则调整，实际值 stats 读回）。
            int bufferMs = Math.Clamp(BufferMilliseconds, 10, 1000);
            int requestFrames = Math.Max(1, (int)((long)src.SampleRate * bufferMs / 1000));

            // 设备 ID：C# 侧持有 IMMDevice ID；DLL 内按 ID 精确匹配（友好名称有歧义，不用）。
            // 取不到 ID 时传空 = 默认渲染设备。
            string? deviceId = null;
            try
            {
                device.GetId(out deviceId);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("NativeCoreOutput.Init(GetId)", caught);
                deviceId = null;
            }

            // 起播预填：先读最多一个周期的**真实字节**。设备启动时首缓冲直接填它（不填静音），
            // 起播即出声——与自研/ECHO 两条路径的 pre-roll 修复同一口径。
            int prefillBytes = requestFrames * _srcBlockAlign;
            _readBuf = new byte[ChunkFrames * _srcBlockAlign];
            var prefill = new byte[prefillBytes];
            int prefillGotBytes = ReadRaw(prefill, prefillBytes);
            if (prefillGotBytes < prefillBytes)
            {
                StartupLog.Write($"原生内核 预填不足：请求{requestFrames}帧 实得{prefillGotBytes / _srcBlockAlign}帧（源短或起播点靠近结尾）");
            }
            uint prefillFrames = (uint)(prefillGotBytes / _srcBlockAlign);

            int rc;
            IntPtr handle;
            try
            {
                rc = NativeCoreAudio.celeste_core_start((uint)src.SampleRate, (uint)_channels,
                    (uint)requestFrames, deviceId, _srcTag,
                    prefillFrames > 0 ? prefill : null, prefillFrames, out handle);
            }
            catch (DllNotFoundException)
            {
                LastError = "找不到 celeste_core.dll（原生内核文件）。请在「音频设置 → 独占输出内核」切回自研，或重装程序。";
                StartupLog.Write("原生内核 celeste_core.dll 缺失（DllNotFoundException），回退自研");
                return false;
            }
            catch (BadImageFormatException)
            {
                LastError = "celeste_core.dll 与当前系统架构不匹配。请切回自研内核，或重装程序。";
                StartupLog.Write("原生内核 celeste_core.dll 架构不匹配（BadImageFormatException），回退自研");
                return false;
            }
            if (rc != 0 || handle == IntPtr.Zero)
            {
                LastError = NativeCoreAudio.DescribeStartError(rc)
                    + (string.IsNullOrEmpty(deviceId) ? "（默认设备）" : " device=" + deviceId);
                StartupLog.Write("原生内核 celeste_core_start 失败 rc=" + rc + " src=" + src.SampleRate + "/" + _srcBits + "bit/" + _channels + "ch tag=" + _srcTag);
                return false;
            }
            _handle = handle;

            NativeCoreAudio.NativeCoreStats st;
            try { st = NativeCoreAudio.Stats(_handle); }
            catch (Exception caught)
            {
                StartupLog.WriteException("NativeCoreOutput.Init(Stats)", caught);
                LastError = "读取原生内核状态失败：" + caught.Message;
                return false;
            }
            _rate = (int)st.SampleRate;
            _bufferFrames = st.BufferFrames;
            _endpointFormat = string.IsNullOrEmpty(st.Format) ? "?" : st.Format;

            // 结构化协商结果（徽标判标志位，绝不反解析描述串：951796f 教训）。
            // 原生 Init 成功 ⟺ 端点容器 == 源布局；这里复核 stats 回报的容器名，不符=内部不一致，据实降级。
            bool layoutMatch = string.Equals(_endpointFormat, NativeCoreAudio.TagName(_srcTag), StringComparison.Ordinal);
            NegotiatedFormat = new AudioFormat(_rate, EndpointBits(), _channels, _srcTag == NativeCoreAudio.FmtFloat32);
            DevicePathKind = layoutMatch ? DevicePath.Lossless : DevicePath.Degraded;
            DeviceEndpointName = EndpointFriendlyName();
            ActualFormatDescription = DescribeOutput(src);
            LastAlignDance = _bufferFrames > 0 && requestFrames > 0 && _bufferFrames != requestFrames;

            StartupLog.Write(string.Format(
                "原生内核 协商成功 源={0}bit/{1}Hz/{2}ch → 设备 {3} Hz/{4} 缓冲{5}帧({6}ms) 容量{7}帧 | {8}{9}",
                _srcBits, src.SampleRate, _channels,
                _rate, _endpointFormat, _bufferFrames,
                _rate > 0 ? _bufferFrames * 1000.0 / _rate : 0, st.CapacityFrames,
                ActualFormatDescription,
                layoutMatch ? "" : " ⚠端点容器与源标签不符（内部不一致，已降级判定）"));
            return true;
        }

        public bool Play(TimeSpan? initialPosition = null)
        {
            if (_handle == IntPtr.Zero) return false;
            if (_provider == null) return false;

            // 源在开播前已被上层 seek 到 initialPosition（PlayWavAsync 预 seek）；
            // 基准设到该帧，Position = 基准 + 已播，保持绝对进度（与另外两个内核口径一致）。
            _framesBaseline = initialPosition.HasValue && initialPosition.Value > TimeSpan.Zero && _rate > 0
                ? (long)(initialPosition.Value.TotalSeconds * _rate)
                : 0;
            _stopRequested = false;
            IsStarted = true;
            AggReset();

            _feeder = new Thread(FeederLoop)
            {
                IsBackground = true,
                Name = "NativeCoreFeeder",
                Priority = ThreadPriority.Normal, // ring 有 1.5s 存货兜底；normal 即可，不抢渲染线程
            };
            _feeder.Start();
            return true;
        }

        /// <summary>线程安全请求 seek：feeder 在下一轮循环开头消费（重定位源 + replace 缓冲 + 淡入）。</summary>
        public void SeekTo(TimeSpan pos)
        {
            lock (_seekLock) { _pendingSeek = pos; }
        }

        public void Stop()
        {
            _stopRequested = true;
            try { _feeder?.Join(3000); } catch (Exception caught) { StartupLog.WriteException("NativeCoreOutput.Stop", caught); }
            _feeder = null;
            if (_handle != IntPtr.Zero)
            {
                // stop 会让阻塞中的 push 返回 false → write 得 -3 → feeder 自然退出。
                NativeCoreAudio.celeste_core_stop(_handle);
            }
            _framesBaseline = 0;
            IsStarted = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch (Exception caught) { StartupLog.WriteException("NativeCoreOutput.Dispose", caught); }
            if (_handle != IntPtr.Zero)
            {
                try { NativeCoreAudio.celeste_core_destroy(_handle); } catch (Exception caught) { StartupLog.WriteException("NativeCoreOutput.Dispose(destroy)", caught); }
                _handle = IntPtr.Zero;
            }
            _provider = null;
        }

        // ---------- feeder 线程 ----------

        private void FeederLoop()
        {
            try
            {
                while (!_stopRequested)
                {
                    TimeSpan? seek;
                    lock (_seekLock)
                    {
                        seek = _pendingSeek;
                        _pendingSeek = null;
                    }
                    if (seek.HasValue)
                    {
                        ConsumeSeek(seek.Value);
                        if (_stopRequested) break;
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int got = ReadRaw(_readBuf, _readBuf.Length);
                    sw.Stop();
                    if (sw.Elapsed.TotalMilliseconds > _readMaxMs) _readMaxMs = sw.Elapsed.TotalMilliseconds;
                    if (got <= 0)
                    {
                        // 源尽：标记输入结束。ring 播完剩余存货即 drained（不再计欠载），
                        // 之后由上层 UpdatePosition 的 IsDrained 判定 Stop→切下一首。
                        FlushAgg("源尽收尾");
                        NativeCoreAudio.celeste_core_mark_input_ended(_handle);
                        break;
                    }

                    int rc = NativeCoreAudio.celeste_core_write(_handle, _readBuf, (uint)got, _srcTag);
                    if (rc != 0)
                    {
                        FlushAgg("write 失败 rc=" + rc);
                        break; // -3=stop 已请求；-4/-5=格式异常；都退出
                    }

                    // 窗口统计：write 返回间隔 = 上一块被设备消耗掉的时长，
                    // 采样节奏天然对齐 feeder，无需额外定时器/线程。
                    AggSample();
                    if (_aggMaxGap >= SpikeThresholdMs)
                    {
                        WriteSpikeLine();
                        _aggMaxGap = 0; // 已报过，吞掉避免每轮重复打同一尖峰
                    }
                }
            }
            catch (Exception ex)
            {
                IsStarted = false;
                try { NativeCoreAudio.celeste_core_stop(_handle); } catch { /* 已在异常路径，忽略 */ }
                Failed?.Invoke(ex);
                return;
            }

            // feeder 自然退出（源尽）不算停止：设备会话还活着（ring 播完余货即 drained），
            // IsStarted 保持 true，与自研/ECHO 两条路径口径一致。播完检测由上层用 IsDrained 统一做。
        }

        // ---------- feeder 窗口诊断实现 ----------

        private void AggReset()
        {
            _aggWatch.Restart();
            _aggWrites = 0;
            _aggMaxGap = 0;
            _aggSpikes = 0;
            _aggMinReady = int.MaxValue;
            _aggReadySum = 0;
            _aggReadyCount = 0;
            _lastReady = 0;
            _lastCapacity = 0;
            _readMaxMs = 0;
            _gc0Base = GC.CollectionCount(0);
            _gc1Base = GC.CollectionCount(1);
            _gc2Base = GC.CollectionCount(2);
            _underrunCbBase = 0;
            _underrunFramesBase = 0;
        }

        private void AggSample()
        {
            var win = SnapshotWindow();
            _aggWrites++;
            if (win.MaxGapMs > _aggMaxGap) _aggMaxGap = win.MaxGapMs;
            _aggSpikes += win.Spikes;
            if (win.ReadyFrames < _aggMinReady) _aggMinReady = win.ReadyFrames;
            _aggReadySum += win.ReadyFrames;
            _aggReadyCount++;
            _lastReady = win.ReadyFrames;
            _lastCapacity = win.CapacityFrames;
            if (_aggWatch.Elapsed >= AggPeriod) FlushAgg(null);
        }

        /// <summary>尖峰即打（与自研内核 [渲染诊断] 同风格）。
        /// 注意口径：尖峰发生在原生渲染线程，feeder 被 GC 冻**不会**直接造成渲染尖峰
        /// （feeder 有 1.6s ring 兜底）——这行的用途是记录尖峰当时的水位与 feeder 状态。</summary>
        private void WriteSpikeLine()
        {
            StartupLog.Write(string.Format(
                "[原生内核诊断] 渲染间隔尖峰={0}ms（轮询12ms／缓冲{1}帧={2:F0}ms）ring余{3}/{4}帧 feeder读源最大{5:F2}ms 欠载累计{6}次",
                _aggMaxGap, _bufferFrames,
                _rate > 0 ? _bufferFrames * 1000.0 / _rate : 0,
                _lastReady, _lastCapacity,
                _readMaxMs, UnderrunCount));
        }

        private void FlushAgg(string? tail)
        {
            if (_aggWrites == 0) return;
            NativeCoreAudio.NativeCoreStats st;
            try { st = NativeCoreAudio.Stats(_handle); }
            catch { return; }
            long dCb = (long)st.UnderrunCallbacks - _underrunCbBase;
            long dFr = (long)st.UnderrunFrames - _underrunFramesBase;
            _underrunCbBase = (long)st.UnderrunCallbacks;
            _underrunFramesBase = (long)st.UnderrunFrames;
            int minReady = _aggMinReady == int.MaxValue ? 0 : _aggMinReady;
            long avgReady = _aggReadyCount > 0 ? _aggReadySum / _aggReadyCount : 0;
            double heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            StartupLog.Write(string.Format(
                "[原生内核统计] {0:F0}s 补货{1}次 | ring水位 最低{2}/平均{3}/满{4}帧 | 渲染间隔 最大{5:F1}ms 尖峰{6}次 | feeder读源 最大{7:F2}ms | 真欠载{8}次({9}帧) | GC gen0+{10} gen1+{11} gen2+{12} 堆{13:F0}MB 模式={14}{15}",
                _aggWatch.Elapsed.TotalSeconds, _aggWrites,
                minReady, avgReady, st.CapacityFrames,
                _aggMaxGap, _aggSpikes,
                _readMaxMs,
                dCb, dFr,
                GC.CollectionCount(0) - _gc0Base, GC.CollectionCount(1) - _gc1Base, GC.CollectionCount(2) - _gc2Base,
                heapMb, System.Runtime.GCSettings.LatencyMode,
                string.IsNullOrEmpty(tail) ? "" : " | " + tail));
            _aggWatch.Restart();
            _aggWrites = 0;
            _aggMaxGap = 0;
            _aggSpikes = 0;
            _aggMinReady = int.MaxValue;
            _aggReadySum = 0;
            _aggReadyCount = 0;
            _readMaxMs = 0;
            _gc0Base = GC.CollectionCount(0);
            _gc1Base = GC.CollectionCount(1);
            _gc2Base = GC.CollectionCount(2);
        }

        /// <summary>feeder 线程内消费 seek：重定位源 → 读一小段 → replace 进 ring（重置统计 + 淡入）。</summary>
        private void ConsumeSeek(TimeSpan pos)
        {
            try
            {
                _provider!.Seek(pos);
                int got = ReadRaw(_readBuf, _readBuf.Length);
                int rc = NativeCoreAudio.celeste_core_replace(_handle, got > 0 ? _readBuf : null, (uint)got, _srcTag);
                if (rc < 0)
                {
                    StartupLog.Write("原生内核 replace 失败 rc=" + rc + "（seek 目标=" + pos.TotalSeconds.ToString("F2") + "s）");
                }
                // replace 内部把 framesPlayed 归零，这里把基准挪到 seek 目标，
                // FramesWritten = 基准 + 已播，进度条连续不回头。
                _framesBaseline = _rate > 0 ? (long)(pos.TotalSeconds * _rate) : 0;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("NativeCoreOutput.ConsumeSeek", caught);
            }
        }

        // ---------- 源读取：字节原样（不做任何格式转换） ----------

        /// <summary>从源读满 maxBytes 字节（或读到源尽），原样写进 dst。返回实得字节数（始终整帧）。</summary>
        private int ReadRaw(byte[] dst, int maxBytes)
        {
            if (_srcBlockAlign <= 0) return 0;
            int wantBytes = maxBytes - (maxBytes % _srcBlockAlign);
            if (wantBytes <= 0) return 0;
            var buf = dst;

            int total = 0;
            // 接上上次余下的不足一帧字节（正常路径恒为 0，防御性处理）
            if (_carryLen > 0)
            {
                Buffer.BlockCopy(_carry, 0, buf, 0, _carryLen);
                total = _carryLen;
                _carryLen = 0;
            }

            while (total < wantBytes)
            {
                int n = _provider!.Read(buf, total, wantBytes - total);
                if (n <= 0) break;
                total += n;
            }

            int gotFrames = total / _srcBlockAlign;
            int leftover = total - gotFrames * _srcBlockAlign;
            if (leftover > 0)
            {
                // 防御：源返回了非整帧字节（不规范 provider），余数留到下一轮，避免流错位
                Buffer.BlockCopy(buf, gotFrames * _srcBlockAlign, _carry, 0, leftover);
                _carryLen = leftover;
            }
            return gotFrames * _srcBlockAlign;
        }

        // ---------- 描述 ----------

        /// <summary>每样本字节数（与 celeste_core.cpp fmt_bytes_per_sample 一致）。</summary>
        private static int TagBytes(uint tag) => tag switch
        {
            NativeCoreAudio.FmtPcm16 => 2,
            NativeCoreAudio.FmtPcm24 => 3,
            NativeCoreAudio.FmtPcm24In32 => 4,
            NativeCoreAudio.FmtPcm32 => 4,
            NativeCoreAudio.FmtFloat32 => 4,
            _ => 0,
        };

        /// <summary>DLL 协商用的端点格式名 → 人话。</summary>
        private string EndpointFriendlyName() => _endpointFormat switch
        {
            "float32" => "float32",
            "pcm24in32" => "PCM24-in-32",
            "pcm24" => "PCM24",
            "pcm32" => "PCM32",
            "pcm16" => "PCM16",
            _ => _endpointFormat,
        };

        /// <summary>端点容器位深（协商成功时与源一致；未知名按源位深保守展示）。</summary>
        private int EndpointBits() => _endpointFormat switch
        {
            "float32" => 32,
            "pcm24in32" or "pcm24" => 24,
            "pcm32" => 32,
            "pcm16" => 16,
            _ => _srcBits,
        };

        private string DescribeOutput(WaveFormat src)
        {
            string endpoint = EndpointFriendlyName();
            bool direct = string.Equals(_endpointFormat, NativeCoreAudio.TagName(_srcTag), StringComparison.Ordinal);
            if (direct)
            {
                // 端点容器 == 源布局，整数字节原样过：字节级 bit-perfect（全链不碰 float）
                return string.Format("{0} Hz / {1}bit / {2}ch → 设备 {3}（源直通，字节级 bit-perfect）",
                    src.SampleRate, _srcBits, _channels, endpoint);
            }
            return string.Format("{0} Hz / {1}bit / {2}ch → 设备 {3}（端点容器与源不一致，已降级）",
                src.SampleRate, _srcBits, _channels, endpoint);
        }
    }
}
