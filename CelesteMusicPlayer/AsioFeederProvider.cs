using System;
using System.Threading;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// ASIO 喂料器（复刻 foobar foo_out_asio 的「回调只拷贝」架构，2026-09-24）：
    ///
    /// 背景（ASIO 卡顿定案）：
    /// NAudio 2.2.1 AsioOut 的 driver_BufferUpdate 在【ASIO 驱动回调线程】上直接调用源链
    /// WaveProvider.Read；而本仓 ASIO 读链是
    ///   SeamlessWaveProvider.Read（持锁 + 阻塞磁盘读）→ ManagedDspSourceProvider →
    ///   （24bit 源）Widen24To32Provider 逐样本循环 → NAudio AsioSampleConvertor 再转一次。
    /// ASIO 驱动缓冲只有首选值那么小（USB DAC 常见毫秒级），每次抖动——
    /// 磁盘延迟 / GC STW / 锁竞争 / 线程调度——都会让回调迟于下一缓冲周期，
    /// 驱动欠载 = 可闻"一卡一卡"（Kessler：ASIO 连播普通 WAV 都卡，与 DSD 无关）。
    /// 独占内核早已用「feeder 备货线程 + ring 库存」治好同一病灶，ASIO 这条路漏治了。
    ///
    /// 本类在驱动回调与源链之间插入后台备货线程 + 环形缓冲：
    ///   - feeder 线程提前把源链数据搬进 ring（磁盘/锁/转换链全在 feeder 线程，与驱动回调解耦）；
    ///   - ASIO 回调只做 Buffer.BlockCopy 取货——无磁盘、无锁、无转换；
    ///   - ring 默认 4 秒库存，吸收一切短抖动；水位低于目标自动补货。
    /// bit-perfect 保持：字节原样搬运，一个样本都不碰。
    ///
    /// 播完语义：Read 永远填满（不让 NAudio 短读触发 AutoStop），由上层用
    /// <see cref="Drained"/>（上游读尽 + ring 播空）判定，与现有 UpdatePosition 停摆路径同构。
    /// Seek：<see cref="Reset"/> 停 feeder→清 ring→基线归零→重启，同步等预填（上限内）。
    /// 开关：详见 HiFiOutputBackend 里 AppSettingsStore.AsioFeederEnabled，关即回退旧直读路径。
    /// </summary>
    internal sealed class AsioFeederProvider : IWaveProvider, IDisposable
    {
        private const int MinRingBytes = 1 << 20;          // ring 至少 1MB
        private const int MaxRingBytes = 64 << 20;         // ring 至多 64MB（防御高采样率爆内存）
        private const int RingDefaultSeconds = 4;          // 库存容量（秒）
        private const int TargetDefaultSeconds = 1;        // 目标水位（秒）：低于它 feeder 就补货
        private const int StagingBytes = 1 << 18;          // feeder 单次从源索取的字节（256KB）
        private const int FeederJoinTimeoutMs = 800;       // 停/重置 feeder 的等待上限
        private const int PrefillWaitMaxMs = 2000;         // 起播预填等待上限
        private const int ResetPrefillMaxMs = 250;         // seek 后预填等待上限（不能卡 UI）
        private const int IdlePollMs = 40;                 // feeder 空闲轮询（事件以外的保险丝）

        private readonly IWaveProvider _source;
        private readonly byte[] _ring;
        private readonly int _capacity;                    // ring 字节数（blockAlign 整数倍）
        private readonly int _targetBytes;                  // 目标水位（blockAlign 整数倍）
        private readonly int _blockAlign;
        private readonly byte[] _staging;
        private readonly object _gate = new object();      // 只做指针/计数更新，不做 IO
        private readonly ManualResetEventSlim _needData = new ManualResetEventSlim(false);
        private readonly bool _dopSilence;                  // 欠载兜底补 DoP 静音帧（而非静音零）
        private readonly Func<object?>? _identity;          // 无缝切换观察：reader 引用变了=切歌

        private int _readPos;
        private int _writePos;
        private int _available;
        private long _consumed;                            // 起播/上次切换/Seek 以来被回调取走的字节
        private long _underrunBytes;
        private long _silenceFrames;
        private volatile bool _upstreamEnded;
        private volatile bool _stopping;
        private volatile bool _disposed;
        private Thread? _feederThread;
        private TimeSpan _baseline = TimeSpan.Zero;        // Seek/切歌设定的起始位置（进度起点）
        private object? _lastIdentity;
        private bool _identitySeenFirst;

        /// <param name="source">上游源链（DSP 链 / Widen24To32 / AsioDoPProvider 均可）。</param>
        /// <param name="ringSeconds">ring 库存秒数（默认 4）。</param>
        /// <param name="targetSeconds">目标水位秒数（默认 1）。</param>
        /// <param name="dopSilence">欠载兜底补 DoP 合法静音帧（DSD/DoP 路径用；PCM 路径补零）。</param>
        /// <param name="identity">无缝切换观察器：返回当前 reader 引用，变化即把进度线归零。PCM 路径传入。</param>
        public AsioFeederProvider(IWaveProvider source, int ringSeconds = RingDefaultSeconds,
            int targetSeconds = TargetDefaultSeconds, bool dopSilence = false, Func<object?>? identity = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            WaveFormat = source.WaveFormat;
            _blockAlign = Math.Max(1, WaveFormat.BlockAlign);
            _dopSilence = dopSilence;
            _identity = identity;

            long bps = WaveFormat.AverageBytesPerSecond;
            if (bps <= 0) bps = 176400; // 保险：非常规格式按 16bit/44.1k 估
            long cap = bps * Math.Max(1, ringSeconds);
            long target = bps * Math.Clamp(targetSeconds, 1, ringSeconds);
            if (target > cap) target = cap;
            cap = Math.Clamp(cap, MinRingBytes, MaxRingBytes);
            cap = (cap + _blockAlign - 1) / _blockAlign * _blockAlign;
            if (cap < 2 * StagingBytes) cap = 2 * StagingBytes;
            _capacity = (int)cap;
            target = Math.Min(target, cap);
            target = (target + _blockAlign - 1) / _blockAlign * _blockAlign;
            _targetBytes = (int)target;
            _ring = new byte[_capacity];
            _staging = new byte[Math.Min(StagingBytes, _capacity)];
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>上游源链是否已读尽。读尽后 ring 存货继续播到空才由 <see cref="Drained"/> 报"真播完"。</summary>
        public bool UpstreamEnded => _upstreamEnded;

        /// <summary>真正播完：上游读尽且 ring 存货也排空。<see cref="Drained"/> 为 true 时上层可安全停摆切下一首。</summary>
        public bool Drained
        {
            get { lock (_gate) { return _upstreamEnded && _available < _blockAlign; } }
        }

        /// <summary>累计欠载（回调时 ring 空、补了静音）的字节数。诊断用；>0 即备货跟不上或 seek 冷启动。</summary>
        public long UnderrunBytes => Interlocked.Read(ref _underrunBytes);

        /// <summary>累计被回调取走的字节（seek 后归零）。</summary>
        public long ConsumedBytes { get { lock (_gate) { return _consumed; } } }

        /// <summary>ring 当前存货字节数（诊断：水位 ≈ ConsumedBytes 时间线）。</summary>
        public int BufferedBytes { get { lock (_gate) { return _available; } } }

        /// <summary>
        /// ASIO 驱动回调线程调用：从 ring 拷贝取货，永不短读（短读会触发 NAudio AutoStop）；
        /// ring 空则补静音（DoP 路径补合法 DoP 静音帧）并计 underrun。
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (count <= 0) return 0;

            int got = 0;
            lock (_gate)
            {
                int n = _available;
                n -= n % _blockAlign; // 只出整帧
                if (n > count) n = count - count % _blockAlign;
                if (n > 0)
                {
                    int first = Math.Min(n, _capacity - _readPos);
                    Buffer.BlockCopy(_ring, _readPos, buffer, offset, first);
                    if (n > first) Buffer.BlockCopy(_ring, 0, buffer, offset + first, n - first);
                    _readPos = (_readPos + n) % _capacity;
                    _available -= n;
                    _consumed += n;
                    got = n;
                }
            }

            if (got < count)
            {
                FillSilence(buffer, offset + got, count - got);
                Interlocked.Add(ref _underrunBytes, count - got);
            }

            if (_available < _targetBytes && !_upstreamEnded) _needData.Set();
            return count;
        }

        /// <summary>起播：起 feeder 线程并同步等预填（水位到目标或上游读尽），超时兜底不阻塞。</summary>
        public void Start()
        {
            StartFeeder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < PrefillWaitMaxMs)
            {
                bool ready;
                lock (_gate) ready = _upstreamEnded || _available >= _targetBytes;
                if (ready) break;
                Thread.Sleep(4);
            }
            StartupLog.Write(string.Format(
                "[ASIO] 喂料器启动 ring={0}MB 目标水位={1:F1}s 预填={2}ms 源={3}Hz/{4}bit/{5}ch{6}",
                _capacity / 1048576.0, _targetBytes / (double)Math.Max(1, WaveFormat.AverageBytesPerSecond),
                sw.ElapsedMilliseconds, WaveFormat.SampleRate, WaveFormat.BitsPerSample, WaveFormat.Channels,
                _upstreamEnded ? "（源已读尽）" : string.Empty));
        }

        /// <summary>Seek/切歌后调用：丢弃 ring 旧存货、进度基线归零、feeder 重灌（同步等短预填）。</summary>
        public void Reset(TimeSpan newBaseline)
        {
            StopFeeder();
            lock (_gate)
            {
                _readPos = 0;
                _writePos = 0;
                _available = 0;
                _consumed = 0;
                _underrunBytes = 0;
                _silenceFrames = 0;
                _upstreamEnded = false;
                _baseline = newBaseline;
                _needData.Reset();
            }
            _identitySeenFirst = false; // 重启后重新观察切换，不把重启当切歌
            StartFeeder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ResetPrefillMaxMs)
            {
                bool ready;
                lock (_gate) ready = _upstreamEnded || _available >= _targetBytes;
                if (ready) break;
                Thread.Sleep(4);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopFeeder();
            try { _needData.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AsioFeederProvider.cs", caught); }
        }

        // ---------- feeder 备货线程 ----------

        private void StartFeeder()
        {
            if (_disposed) return;
            StopFeeder();
            _stopping = false;
            var t = new Thread(FeederLoop)
            {
                IsBackground = true,
                Name = "ASIO Feeder",
                Priority = ThreadPriority.Normal,
            };
            _feederThread = t;
            t.Start();
        }

        private void StopFeeder()
        {
            _stopping = true;
            _needData.Set();
            var t = _feederThread;
            if (t != null && t.IsAlive)
            {
                if (!t.Join(FeederJoinTimeoutMs))
                {
                    StartupLog.Write("[ASIO] 喂料器线程 " + FeederJoinTimeoutMs + "ms 未退出（上游 Read 可能阻塞在读盘上；后台线程随进程退出而终结，不阻塞停播）");
                }
            }
            _feederThread = null;
            _stopping = false;
        }

        private void FeederLoop()
        {
            while (!_stopping)
            {
                if (_upstreamEnded)
                {
                    _needData.Wait(200); // 上游读尽：无事可做，等 Reset/Dispose
                    continue;
                }

                ObserveIdentitySwitch();

                bool needMore;
                lock (_gate) needMore = _available < _targetBytes;
                if (!needMore)
                {
                    _needData.Wait(IdlePollMs);
                    continue;
                }

                int want;
                lock (_gate) want = Math.Min(_staging.Length, _capacity - _available);
                want -= want % _blockAlign;
                if (want <= 0)
                {
                    _needData.Wait(IdlePollMs);
                    continue;
                }

                int n;
                try
                {
                    n = _source.Read(_staging, 0, want);
                }
                catch (Exception ex)
                {
                    // fail-closed：当读尽处理，让上层走 Drain→播完流程，绝不死循环转不出声音
                    _upstreamEnded = true;
                    StartupLog.Write("[ASIO] 喂料器上游读取异常，按读尽处理（上层将正常播完切歌）：" + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                n -= n % _blockAlign;
                if (n <= 0)
                {
                    _upstreamEnded = true; // 源自然读尽（SeamlessWaveProvider 无下一首可续）
                    continue;
                }

                lock (_gate)
                {
                    int first = Math.Min(n, _capacity - _writePos);
                    Buffer.BlockCopy(_staging, 0, _ring, _writePos, first);
                    if (n > first) Buffer.BlockCopy(_staging, first, _ring, 0, n - first);
                    _writePos = (_writePos + n) % _capacity;
                    _available += n;
                }
            }
        }

        /// <summary>无缝续接观察：reader 引用变了=已切入下一首 → 进度线归零（与上层 SwitchedToNext 同步、互补成基线）。</summary>
        private void ObserveIdentitySwitch()
        {
            if (_identity == null) return;
            var id = _identity.Invoke();
            lock (_gate)
            {
                if (!_identitySeenFirst)
                {
                    _identitySeenFirst = true;
                    _lastIdentity = id;
                    return;
                }
                if (!ReferenceEquals(id, _lastIdentity) && id != null)
                {
                    _lastIdentity = id;
                    _consumed = 0; // 下一首从 0 起算（PlaybackPosition 的基线随之归零）
                    _baseline = TimeSpan.Zero;
                }
            }
        }

        /// <summary>欠载兜底：PCM 填零；DoP 填合法静音帧（[0x69 0x69 0x00 marker]，marker 每帧 0x05/0xFA 交替，
        /// 与 AsioDoPProvider 尾部补静音同配方——保住 DoP 锁， DAC 不会当杂音）。</summary>
        private void FillSilence(byte[] buf, int offset, int bytes)
        {
            if (!_dopSilence)
            {
                Array.Clear(buf, offset, bytes);
                return;
            }

            int p = offset;
            int end = offset + bytes;
            while (p + _blockAlign <= end)
            {
                byte m = ((_silenceFrames++) & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                for (int s = 0; s + 4 <= _blockAlign; s += 4)
                {
                    buf[p + s] = 0x69;
                    buf[p + s + 1] = 0x69;
                    buf[p + s + 2] = 0x00;
                    buf[p + s + 3] = m;
                }
                p += _blockAlign;
            }
            if (p < end) Array.Clear(buf, p, end - p);
        }

        /// <summary>当前播出进度 = seek 基点 + ring 取走量（不含 ring 存货，与正在播放的声音一致）。
        /// 无缝续接/Seek 时归零由 <see cref="ObserveIdentitySwitch"/> / <see cref="Reset"/> 置基线。</summary>
        public TimeSpan PlaybackPosition
        {
            get
            {
                lock (_gate)
                {
                    double rate = WaveFormat.SampleRate;
                    if (rate <= 0) return _baseline;
                    long frames = _consumed / _blockAlign;
                    return _baseline + TimeSpan.FromSeconds(frames / rate);
                }
            }
        }
    }
}
