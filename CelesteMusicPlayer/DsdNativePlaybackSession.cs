using System;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 把 DsdBitstream 的 1-bit 交织字节流包装成 IWaveProvider，供 AsioFeederProvider 当字节源用。
    ///
    /// WaveFormat 是「假容器格式」，只为让 feeder 算对每秒字节数：
    ///   sampleRate = DSD 位时钟 / 8，8bit，2 声道
    ///   ⇒ AverageBytesPerSecond = 位时钟/8 × 2 = 真实的 1-bit 交织流字节率（DSD64 = 705600 B/s）。
    /// ⚠ DsdBitstream 的输出恒为 L/R 逐字节交织（mono 在读出口复制成双声道），所以这里恒写 2ch，
    ///   不能拿源文件的声道数来填，否则 L/R 会错位串音。
    /// 这里的字节不是 PCM 采样，绝对不要经过任何 DSP / 音量 / 重采样。
    /// </summary>
    internal sealed class DsdNativeByteSource : IWaveProvider
    {
        private readonly DsdBitstream _stream;

        public WaveFormat WaveFormat { get; }

        public DsdNativeByteSource(DsdBitstream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            int pseudoRate = checked((int)(stream.NativeRateHz / 8));
            WaveFormat = new WaveFormat(pseudoRate, 8, 2);
        }

        public int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
    }

    /// <summary>
    /// 原生 DSD 的一次播放会话：DSF 文件 → 后台喂料 ring → AsioNativeDsdOutput → DAC。
    ///
    /// 独立于 HiFiOutputBackend 的 PCM/DoP 链路，这样失败时可以干净地回退，不会污染现有状态机。
    /// 对外只暴露三个东西：进度、播完、LastError，事件形态与 HiFiOutputBackend 对齐，
    /// 由 AudioPlaybackEngine 转发到它自己的 PositionChanged / PlaybackEnded。
    ///
    /// 硬约束（ASIO 原生 DSD 的本质要求，不是设计口味）：
    ///   · 只走 ASIO；只支持 DSF / DSD64-256 / 1-2 声道；不支持 seek 与无缝切歌。
    ///   · 软件音量、EQ、SRC、ReplayGain 全部必须旁路 —— 这里压根没接它们，天然满足。
    ///   · 驱动不支持 DSD 时 Open() 会抛，调用方负责回退到 DoP / PCM。
    /// </summary>
    internal sealed class DsdNativePlaybackSession : IDisposable
    {
        private static readonly uint[] SupportedRates = { 2822400, 5644800, 11289600 };

        public event Action<TimeSpan>? PositionChanged;
        public event Action? PlaybackEnded;

        public TimeSpan Duration { get; private set; }
        public string? LastError { get; private set; }
        public string FormatDescription { get; private set; } = "Native DSD";
        public bool IsPlaying => !_ended && _output != null;

        private DsdBitstream? _stream;
        private AsioFeederProvider? _feeder;
        private AsioNativeDsdOutput? _output;
        private Thread? _monitor;
        private volatile bool _stopMonitor;
        private volatile bool _ended;
        private double _bytesPerSecond = 1;
        private bool _disposed;

        public bool Start(string dsfPath, string driverName)
        {
            try
            {
                if (!".dsf".Equals(Path.GetExtension(dsfPath), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("原生 DSD 目前只支持 DSF 文件（DFF 走 DoP / PCM 回退）。");
                }

                if (string.IsNullOrWhiteSpace(driverName))
                {
                    throw new InvalidOperationException("没有可用的 ASIO 驱动。");
                }

                _stream = DsdBitstream.Open(dsfPath);
                uint rate = _stream.NativeRateHz;
                if (!SupportedRates.Contains(rate))
                {
                    throw new InvalidDataException($"不支持的 DSD 位时钟 {rate} Hz（只做 DSD64/128/256，KA13 上限 DSD256）。");
                }

                int srcChannels = _stream.Channels;
                if (srcChannels < 1 || srcChannels > 2)
                {
                    throw new InvalidDataException($"不支持 {srcChannels} 声道 DSD（只做 1-2 声道）。");
                }

                const int outChannels = 2;
                _bytesPerSecond = rate / 8.0 * outChannels;
                Duration = TimeSpan.FromSeconds(_stream.TotalSamples / (double)rate);
                FormatDescription = $"Native DSD {rate:0} Hz / 1-bit / {outChannels}ch（ASIO 原生）";

                var source = new DsdNativeByteSource(_stream);
                _feeder = new AsioFeederProvider(source, dsdSilence: true);
                _feeder.Start();

                _output = new AsioNativeDsdOutput(driverName, rate, outChannels, _stream.LsbFirst,
                    (buf, need) => _feeder!.Read(buf, 0, need), s => StartupLog.Write(s));
                _output.Open();
                _output.Start();

                StartupLog.Write($"[DSD原生] 起播 {Path.GetFileName(dsfPath)} | {FormatDescription} | 时长={Duration} | 驱动={driverName}");
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

        private void MonitorLoop()
        {
            while (!_stopMonitor)
            {
                try
                {
                    var output = _output;
                    if (output == null) break;

                    var pos = TimeSpan.FromSeconds(output.BytesConsumed / _bytesPerSecond);
                    if (pos > Duration) pos = Duration;
                    PositionChanged?.Invoke(pos);

                    // 播完以 feeder 的「上游读尽 + ring 播空」为准；output.Drained 是兜底。
                    if (_feeder?.Drained == true || output.Drained)
                    {
                        _ended = true;
                        _stopMonitor = true;
                        output.Stop();
                        output.Dispose(); // 立刻把 DAC 还原成 PCM，别让设备一直停在 DSD 模式
                        StartupLog.Write($"[DSD原生] 播完 | 取走 {output.BytesConsumed} 字节 | 欠载 {output.Underruns} 次");
                        PositionChanged?.Invoke(Duration);
                        PlaybackEnded?.Invoke();
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

        public void Pause()
        {
            if (_ended) return;
            try { _output?.Stop(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 暂停失败：" + ex.Message); }
        }

        public void Resume()
        {
            if (_ended) return;
            try { _output?.Start(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 恢复失败：" + ex.Message); }
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

            try { _output?.Dispose(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 释放输出失败：" + ex.Message); }
            try { _feeder?.Dispose(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 释放喂料器失败：" + ex.Message); }
            try { _stream?.Dispose(); }
            catch (Exception ex) { StartupLog.Write("[DSD原生] 释放 DSF 流失败：" + ex.Message); }

            _output = null;
            _feeder = null;
            _stream = null;
        }
    }
}
