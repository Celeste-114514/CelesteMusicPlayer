using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// A/B 诊断后端：用 NAudio 的 <see cref="WasapiOut"/>（独占共享模式）直接播 DoP 数据源
    /// （<see cref="DoPWaveSource"/> 已实现 <see cref="IWaveSourceProvider"/>，经适配器转成 NAAudio IWaveProvider）。
    /// 用途：判断「电流声/卡顿/DAC 黄灯」是来自我们自己写的原生 WASAPI render，还是来自 DoP 数据/通道本身。
    /// 完全独立于 HiFiOutputBackend 的原生状态机，不影响现有稳定路径。
    /// </summary>
    internal sealed class NaudioDsdBackend : IWaveSourceProvider, IDisposable
    {
        private readonly DoPWaveSource _wrapped;
        private readonly Adapter _provider;
        private WasapiOut? _out;
        private volatile bool _disposed;

        public NaudioDsdBackend(DoPWaveSource source)
        {
            _wrapped = source;
            _provider = new Adapter(source);
        }

        public WaveFormat WaveFormat => _wrapped.WaveFormat;
        public TimeSpan TotalTime => _wrapped.TotalTime;
        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState => _wrapped.ProbeCurrentState;
        public bool NextMounted => false;
        public int Read(byte[] buffer, int offset, int count) => _wrapped.Read(buffer, offset, count);
        public void Seek(TimeSpan position) => _wrapped.Seek(position);

        public TimeSpan Position => TimeSpan.Zero; // A/B 诊断：NAudio 路径暂不精确维护进度（只看声音）

        public bool Start(TimeSpan? seekTo = null)
        {
            try
            {
                if (seekTo.HasValue && seekTo.Value > TimeSpan.Zero)
                {
                    _wrapped.Seek(seekTo.Value);
                }

                var wf = _wrapped.WaveFormat;
                // 独占 + EventCallback（latency 100ms），尽力 bit-perfect 直通 DoP 容器
                _out = new WasapiOut(AudioClientShareMode.Exclusive, 100);
                _out.Init(_provider);
                _out.Play();
                return true;
            }
            catch (Exception ex)
            {
                try { StartupLog.Write("[NAudioDSD] Start 失败: " + ex); } catch { }
                return false;
            }
        }

        public void Pause()
        {
            try { _out?.Pause(); } catch { }
        }

        public void Resume()
        {
            try { _out?.Play(); } catch { }
        }

        public void Stop()
        {
            try { _out?.Stop(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { _out?.Stop(); _out?.Dispose(); } catch { }
            _out = null;
            try { _wrapped.Dispose(); } catch { }
        }

        /// <summary>把 <see cref="IWaveSourceProvider"/> 适配为 NAudio <see cref="IWaveProvider"/>。</summary>
        private sealed class Adapter : IWaveProvider
        {
            private readonly IWaveSourceProvider _src;
            public Adapter(IWaveSourceProvider src) => _src = src;
            public WaveFormat WaveFormat => _src.WaveFormat;
            public int Read(byte[] buffer, int offset, int count) => _src.Read(buffer, offset, count);
        }
    }
}
