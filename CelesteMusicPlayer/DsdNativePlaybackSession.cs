using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 原生 DSD 的一次播放会话：DSF / DFF → 后台喂料 ring → AsioNativeDsdOutput → DAC。
    ///
    /// 独立于 HiFiOutputBackend 的 PCM/DoP 链路，失败时可以干净回退，不会污染现有状态机。
    /// 对外用事件把进度 / 播完 / 无缝换曲转给 AudioPlaybackEngine。
    ///
    /// 支持：DSF 与 DFF、DSD64–1024（实际能不能播由驱动 IsSampleRateSupported 决定）、
    /// 1–N 声道取前两声道、拖动进度、CUE 分轨、A-B 循环、软暂停、同格式无缝换源。
    ///
    /// 硬约束（1-bit 码流的本质，不是设计口味）：
    ///   · 软件音量 / EQ / DSP / 重采样 / 变速一律不接 —— 线性运算会把 DSD 变成噪声。
    ///   · 换源只在同容器 / 同倍率 / 同声道 / 同位序时复用驱动；否则必须重开，会有间隙。
    ///   · 驱动不支持当前倍率或 DSD 格式时 Open() 抛异常，由调用方回退 DoP / PCM。
    /// </summary>
    internal sealed class DsdNativePlaybackSession : IDisposable
    {
        // 位时钟表：KA13 实测只到 DSD256，但别把上限写死在代码里——
        // 别的设备（或将来换 DAC）支持 DSD512/1024 时应当照常工作，由驱动自己拒绝不支持的档位。
        private static readonly uint[] SupportedRates =
        {
            2822400, 5644800, 11289600, 22579200, 45158400
        };

        public event Action<TimeSpan>? PositionChanged;
        public event Action? PlaybackEnded;
        public event Action? TrackChanged;

        public TimeSpan Duration { get; private set; }
        public string? LastError { get; private set; }
        public string FormatDescription { get; private set; } = "Native DSD";
        public bool IsPlaying => !_ended && _output != null;
        /// <summary>播完但驱动仍保持在 DSD，等同格式续接（此时不要销毁会话）。</summary>
        public bool IsAwaitingNext => _awaitingNext;

        // 供音频链路面板读的真实参数（不猜、不编）
        public uint NativeRateHz { get; private set; }
        public int SourceChannels { get; private set; }
        public int OutputChannels => 2;
        public bool SourceLsbFirst { get; private set; }
        public string DriverName { get; private set; } = string.Empty;
        public int DriverDsdType { get; private set; }
        public int BytesPerChannel { get; private set; }
        public string DsdLevelName { get; private set; } = string.Empty;
        public string ContainerName { get; private set; } = string.Empty;

        public string ChannelSampleTypeName => DriverDsdType switch
        {
            32 => "32 · DSDInt8LSB1（LSB-first）",
            33 => "33 · DSDInt8MSB1（MSB-first）",
            40 => "40 · DSDInt8NER8",
            _ => "未知（" + DriverDsdType + "）",
        };

        private DsdNativeReader? _reader;
        private AsioFeederProvider? _feeder;
        private AsioNativeDsdOutput? _output;
        private Thread? _monitor;
        private volatile bool _stopMonitor;
        private volatile bool _ended;
        private double _bytesPerSecond = 1;
        private double _startSeconds;         // 分轨起点（相对文件）
        private double _endSeconds;           // 分轨终点（相对文件）
        private long _bytesBaseline;          // 上次 seek / 换源时的 BytesConsumed 基准
        private TimeSpan _timeBaseline;       // 上次 seek / 换源时的曲目进度基准
        private bool _loopActive;
        private double _loopStart;
        private double _loopEnd;
        private readonly object _seekGate = new();
        private bool _disposed;

        // 预载的下一首：播完瞬间在同一次 ASIO 会话内换源，字节级连续、驱动不重开。
        private DsdNativeReader? _pendingReader;
        private string _pendingPath = string.Empty;
        private double _pendingStart;
        private double _pendingLength;
        // 播完但驱动仍保持在 DSD（只喂静音码），等同格式续接；避免 DSD→PCM→DSD 重开产生爆音。
        private volatile bool _awaitingNext;
        private Timer? _awaitTimer;        // 超时只撤「可续接」标志，不在回调里碰 COM

        public bool Start(string path, string driverName, double startSeconds = 0, double lengthSeconds = 0)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(driverName))
                {
                    throw new InvalidOperationException("没有可用的 ASIO 驱动。");
                }

                _reader = DsdNativeReader.Open(path);
                var h = _reader.Header;
                if (!SupportedRates.Contains(h.RateHz))
                {
                    throw new InvalidDataException($"不支持的 DSD 位时钟 {h.RateHz} Hz（只认 DSD64/128/256/512/1024）。");
                }

                ApplyTrack(path, h, startSeconds, lengthSeconds, seekReader: true);
                _feeder = new AsioFeederProvider(_reader, dsdSilence: true);
                _feeder.Start();

                _output = new AsioNativeDsdOutput(driverName, h.RateHz, OutputChannels, h.LsbFirst,
                    (buf, need) => _feeder!.Read(buf, 0, need), s => StartupLog.Write(s));
                _output.Open();          // 内部会按驱动能力拒绝不支持的倍率 → 自动回退
                DriverDsdType = _output.DriverDsdType;
                BytesPerChannel = _output.BytesPerChannel;
                _output.Start();

                StartupLog.Write($"[DSD原生] 起播 {Path.GetFileName(path)} | {FormatDescription} | 时长={Duration} | 驱动={driverName}");
                _monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "DsdNativeMonitor" };
                _monitor.Start();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                StartupLog.Write("[DSD原生] 起播失败，自动回退 DoP/PCM：" + ex);
                Dispose();
                return false;
            }
        }

        private void ApplyTrack(string path, DsdNativeReader.HeaderInfo h,
            double startSeconds, double lengthSeconds, bool seekReader)
        {
            double total = h.TotalSamples / (double)h.RateHz;
            _startSeconds = Math.Clamp(startSeconds, 0, Math.Max(0, total - 0.001));
            _endSeconds = lengthSeconds > 0 ? Math.Min(total, _startSeconds + lengthSeconds) : total;
            Duration = TimeSpan.FromSeconds(Math.Max(0, _endSeconds - _startSeconds));
            if (seekReader && _startSeconds > 0 && _reader != null)
            {
                _reader.SeekSample((long)(_startSeconds * h.RateHz));
            }

            NativeRateHz = h.RateHz;
            SourceChannels = h.Channels;
            SourceLsbFirst = h.LsbFirst;
            DsdLevelName = h.LevelName;
            ContainerName = h.ContainerName;
            _bytesPerSecond = h.RateHz / 8.0 * OutputChannels;
            FormatDescription = $"Native DSD {h.RateHz:0} Hz / {h.LevelName} / 1-bit / {h.ContainerName}"
                + (SourceChannels > 2 ? $"（{SourceChannels}ch 取前两声道）" : "")
                + "（ASIO 原生）";
        }

        /// <summary>拖动进度。position 是「当前曲目」的相对时间（CUE 分轨从 0 起算）。</summary>
        public void Seek(TimeSpan position)
        {
            lock (_seekGate)
            {
                // 播完后输出已经 Dispose（驱动已还原成 PCM），此时再拖进度不能去碰它；
                // 正常流程里 PlaybackEnded 会先切下一首并重建会话。
                if (_disposed || _ended || _reader == null || _feeder == null || _output == null) return;
                double rel = Math.Clamp(position.TotalSeconds, 0, Math.Max(0, Duration.TotalSeconds));
                double abs = _startSeconds + rel;
                _reader.SeekSample((long)(abs * NativeRateHz));
                _feeder.Reset(TimeSpan.FromSeconds(rel));   // 清 ring、进度基线归零；欠载补 0x69/0x96
                _bytesBaseline = _output.BytesConsumed;
                _timeBaseline = TimeSpan.FromSeconds(rel);
                StartupLog.Write($"[DSD原生] seek → 曲目 {rel:0.00}s / 文件 {abs:0.00}s");
            }
        }

        /// <summary>A-B 循环，秒数相对当前曲目。seek 能力之上只需在监控线程里到点跳回。</summary>
        public void SetAbLoop(double startSec, double endSec, bool active)
        {
            _loopStart = startSec;
            _loopEnd = endSec;
            _loopActive = active && endSec > startSec;
        }

        /// <summary>
        /// 预载下一首（由 PlaybackSession 的无缝预加载通道调用）。只有同格式才接受。
        /// 真正换源在旧曲播完瞬间由监控线程做，不经 PlaybackEnded、不重开驱动，
        /// 因此不会在 DAC 端出现 DSD↔PCM 切换的那一声异响。
        /// </summary>
        public bool PrepareNext(string path, double startSeconds = 0, double lengthSeconds = 0)
        {
            lock (_seekGate)
            {
                if (_disposed || _ended || _reader == null) return false;
                ClearPending();
                try
                {
                    var h = DsdNativeReader.Peek(path);
                    if (!CanAcceptSeamless(h)) return false;
                    var reader = DsdNativeReader.Open(path);
                    double total = h.TotalSamples / (double)h.RateHz;
                    double start = Math.Clamp(startSeconds, 0, Math.Max(0, total - 0.001));
                    if (start > 0) reader.SeekSample((long)(start * h.RateHz));
                    _pendingReader = reader;
                    _pendingPath = path;
                    _pendingStart = start;
                    _pendingLength = lengthSeconds;
                    StartupLog.Write("[DSD原生] 已预载下一首（同格式无缝）: " + Path.GetFileName(path));
                    return true;
                }
                catch (Exception ex)
                {
                    StartupLog.Write("[DSD原生] 预载下一首失败（改走常规切歌）：" + ex.Message);
                    return false;
                }
            }
        }

        private void ClearPending()
        {
            try { _pendingReader?.Dispose(); } catch { }
            _pendingReader = null;
            _pendingPath = string.Empty;
            _pendingStart = 0;
            _pendingLength = 0;
        }

        /// <summary>旧曲数据真正播完（ring 排空）时，在同一驱动会话内直接接上下一首。</summary>
        private bool SwitchToPending()
        {
            lock (_seekGate)
            {
                if (_pendingReader == null || _reader == null || _feeder == null || _output == null || _disposed) return false;
                var next = _pendingReader;
                string path = _pendingPath;
                _pendingReader = null;                  // 所有权交给会话，别被 ClearPending 释放
                try
                {
                    _feeder.ReplaceSource(next, TimeSpan.Zero);   // 不清 ring；此时 ring 已排空
                    _output.ResetDrained();                      // 别让上一曲的「播完」标志误伤新曲
                    _reader.Dispose();
                    _reader = next;
                    ApplyTrack(path, next.Header, _pendingStart, _pendingLength, seekReader: false);
                    _timeBaseline = TimeSpan.Zero;
                    _bytesBaseline = _output.BytesConsumed;
                    _ended = false;
                    _awaitingNext = false;
                    _pendingPath = string.Empty;
                    PositionChanged?.Invoke(TimeSpan.Zero);
                    TrackChanged?.Invoke();              // → 引擎转发 SeamlessTrackChanged → 界面推进
                    StartupLog.Write("[DSD原生] 字节级无缝换源 → " + Path.GetFileName(path) + " | 驱动未重开");
                    return true;
                }
                catch (Exception ex)
                {
                    StartupLog.Write("[DSD原生] 无缝换源失败，改走常规切歌：" + ex.Message);
                    next.Dispose();
                    return false;
                }
            }
        }

        /// <summary>同格式无缝换源：驱动不重开、ring 存货不清，字节流首尾相接。</summary>
        public bool ReplaceSource(string path, double startSeconds = 0, double lengthSeconds = 0)
        {
            lock (_seekGate)
            {
                if (_disposed || (_ended && !_awaitingNext) || _reader == null || _feeder == null || _output == null) return false;
                bool wasAwaiting = _awaitingNext;
                ClearPending();                         // 手动切歌 / 等待续接时丢掉旧预载
                DsdNativeReader? next = null;
                try
                {
                    var peek = DsdNativeReader.Peek(path);
                    if (!CanAcceptSeamless(peek)) return false;

                    next = DsdNativeReader.Open(path);
                    double total = peek.TotalSamples / (double)peek.RateHz;
                    double start = Math.Clamp(startSeconds, 0, Math.Max(0, total - 0.001));
                    if (start > 0) next.SeekSample((long)(start * peek.RateHz));

                    _feeder.ReplaceSource(next, TimeSpan.Zero);   // 换上游（默认保留 ring）
                    if (wasAwaiting)
                    {
                        // 旧曲已播完：ring 里可能还压着预读的尾巴，丢掉以免重复播旧曲。
                        // 等待期间回调一直写 0x69/0x96 静音码，衔接是「尾 → 静音 → 新曲头」。
                        _feeder.Reset(TimeSpan.Zero);
                        _output.ResetDrained();
                        _output.SetPaused(false);
                        _awaitTimer?.Dispose();
                        _awaitTimer = null;
                    }
                    _reader.Dispose();
                    _reader = next;
                    next = null;

                    ApplyTrack(path, _reader.Header, start, lengthSeconds, seekReader: false);
                    _timeBaseline = TimeSpan.Zero;
                    _bytesBaseline = _output.BytesConsumed;
                    _awaitingNext = false;
                    _ended = false;
                    if (wasAwaiting)
                    {
                        _stopMonitor = false;
                        _monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "DsdNativeMonitor" };
                        _monitor.Start();
                    }
                    StartupLog.Write($"[DSD原生] 同格式续接 {Path.GetFileName(path)} | {FormatDescription}");
                    return true;
                }
                catch (Exception ex)
                {
                    StartupLog.Write("[DSD原生] 无缝换源失败（改走重建会话）：" + ex.Message);
                    next?.Dispose();
                    return false;
                }
            }
        }

        /// <summary>只有容器 / 位时钟 / 声道 / 位序 / 块大小全一致才能不重开驱动直接续接。</summary>
        public bool CanAcceptSeamless(DsdNativeReader.HeaderInfo h)
        {
            var cur = _reader?.Header;
            // _awaitingNext：播完但驱动还在 DSD，允许下一首续接，不必重开驱动
            return !_disposed && (_awaitingNext || !_ended) && cur != null
                && h.RateHz == cur.Value.RateHz
                && h.Channels == cur.Value.Channels
                && h.LsbFirst == cur.Value.LsbFirst
                && h.Kind == cur.Value.Kind
                && h.BlockSize == cur.Value.BlockSize;
        }

        private void MonitorLoop()
        {
            while (!_stopMonitor)
            {
                try
                {
                    var output = _output;
                    if (output == null) break;

                    long consumed = output.BytesConsumed;
                    var pos = _timeBaseline + TimeSpan.FromSeconds((consumed - _bytesBaseline) / _bytesPerSecond);
                    if (pos > Duration) pos = Duration;
                    PositionChanged?.Invoke(pos);

                    if (_loopActive && pos.TotalSeconds >= _loopEnd && _loopEnd > _loopStart)
                    {
                        Seek(TimeSpan.FromSeconds(_loopStart));
                        continue;
                    }

                    // 先判「源真正读尽 + ring 排空」：只有这时换源才是字节级连续、不丢尾。
                    // 命中预载就直接续接，驱动不重开，也不触发 PlaybackEnded（上层不会 Stop→重建）。
                    bool sourceDone = _feeder?.Drained == true || output.Drained;
                    if (sourceDone && SwitchToPending()) continue;

                    if (sourceDone || pos.TotalSeconds >= Math.Max(0, Duration.TotalSeconds - 0.02))
                    {
                        Finish(output, consumed);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    StartupLog.Write("[DSD原生] 监控线程异常：" + ex.Message);
                    break;
                }

                Thread.Sleep(100);
            }
        }

        private void Finish(AsioNativeDsdOutput output, long consumed)
        {
            _ended = true;
            _stopMonitor = true;
            _awaitingNext = true;
            // ⚠ 不 Stop、不 Dispose：驱动保持在原生 DSD，回调只喂合法静音码。
            // 没有预载时自动切歌会走 PlaybackEnded → 常规切下一首；那一首若能复用本会话，
            // 就只换源不重开驱动，避免 DSD→PCM→DSD 切换在 DAC 端产生的那一声异响。
            // 真正释放交给 StopNativeDsd（用户停止 / 切到非 DSD / 格式不同需重建时）。
            output.SetPaused(true);
            // 超时只把「可续接」标志撤掉，不在这里 Dispose（Timer 回调在线程池线程，不碰 COM）。
            // 之后任何 Stop / 起播都会走 StopNativeDsd 正常释放驱动。
            _awaitTimer?.Dispose();
            _awaitTimer = new Timer(_ => _awaitingNext = false, null, 8000, Timeout.Infinite);
            StartupLog.Write($"[DSD原生] 播完，保持驱动等待续接 | 取走 {consumed} 字节 | 欠载 {output.Underruns} 次");
            PositionChanged?.Invoke(Duration);
            PlaybackEnded?.Invoke();
        }

        public void Pause()
        {
            if (_ended) return;
            _output?.SetPaused(true);
        }

        public void Resume()
        {
            if (_ended) return;
            _output?.SetPaused(false);
        }

        public void Stop()
        {
            _stopMonitor = true;
            _ended = true;
            _awaitingNext = false;
            ClearPending();
            _awaitTimer?.Dispose();
            _awaitTimer = null;
            try { _output?.SetPaused(false); } catch { }
            try { _output?.Stop(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 停止失败：" + ex.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopMonitor = true;
            _ended = true;
            _awaitingNext = false;
            ClearPending();
            _awaitTimer?.Dispose();
            _awaitTimer = null;

            try { _output?.SetPaused(false); } catch { }
            try { _output?.Dispose(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 释放输出失败：" + ex.Message); }
            try { _feeder?.Dispose(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 释放喂料器失败：" + ex.Message); }
            try { _reader?.Dispose(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 释放 DSD 读取器失败：" + ex.Message); }

            _output = null;
            _feeder = null;
            _reader = null;
        }
    }
}
