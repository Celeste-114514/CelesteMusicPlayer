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

        /// <summary>同格式无缝换源：驱动不重开、ring 存货不清，字节流首尾相接。</summary>
        public bool ReplaceSource(string path, double startSeconds = 0, double lengthSeconds = 0)
        {
            lock (_seekGate)
            {
                if (_disposed || _ended || _reader == null || _feeder == null || _output == null) return false;
                DsdNativeReader? next = null;
                try
                {
                    var peek = DsdNativeReader.Peek(path);
                    if (!CanAcceptSeamless(peek)) return false;

                    next = DsdNativeReader.Open(path);
                    double total = peek.TotalSamples / (double)peek.RateHz;
                    double start = Math.Clamp(startSeconds, 0, Math.Max(0, total - 0.001));
                    if (start > 0) next.SeekSample((long)(start * peek.RateHz));

                    _feeder.ReplaceSource(next, TimeSpan.Zero);   // 不清 ring
                    _reader.Dispose();
                    _reader = next;
                    next = null;

                    ApplyTrack(path, _reader.Header, start, lengthSeconds, seekReader: false);
                    _timeBaseline = TimeSpan.Zero;
                    _bytesBaseline = _output.BytesConsumed;
                    _ended = false;
                    StartupLog.Write($"[DSD原生] 同格式无缝续接 {Path.GetFileName(path)} | {FormatDescription}");
                    TrackChanged?.Invoke();
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
            return !_disposed && !_ended && cur != null
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

                    // 播完：分轨到终点、上游读尽且 ring 排空、或驱动回调读到尽头。
                    if (pos.TotalSeconds >= Math.Max(0, Duration.TotalSeconds - 0.02)
                        || _feeder?.Drained == true || output.Drained)
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
            output.SetPaused(false);
            output.Stop();
            output.Dispose();           // 立刻把 DAC 还原成 PCM，别让设备一直停在 DSD 模式
            StartupLog.Write($"[DSD原生] 播完 | 取走 {consumed} 字节 | 欠载 {output.Underruns} 次");
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
            try { _output?.Stop(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 停止失败：" + ex.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopMonitor = true;
            _ended = true;

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
