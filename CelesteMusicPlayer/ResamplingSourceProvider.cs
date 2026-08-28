using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 采样率转换（SRC）源：把内部源（目前是 <see cref="SeamlessWaveProvider"/>）重采样到目标采样率，
    /// 在链路中位于「无缝续接源」与「DSP 链」之间：
    ///   源(IWaveProvider) → WaveToSampleProvider(float，源采样率)
    ///     → WdlResamplingSampleProvider(float，目标采样率)
    ///     → SampleToWaveProvider24(24bit PCM，目标采样率)
    ///
    /// 重采样用 NAudio 的 WDL 重采样器（与 Reaper 同源），音质优于系统默认重采样。
    /// 输出固定 24bit：升频是按浮点插值算出来的，24bit 足以完整承载插值精度，
    /// 也是绝大多数 DAC 的原生位深，独占模式下兼容性最好。
    ///
    /// 时长 / 拖动 / 无缝续接状态仍按「源」的时域语义转发（重采样不改变时长），
    /// 所以上层进度条、无缝续接与交叉淡化的判定都不受影响。
    /// </summary>
    internal sealed class ResamplingSourceProvider : IWaveProvider, IWaveSourceProvider
    {
        private readonly IWaveSourceProvider _source;
        private readonly IWaveProvider _output;
        private readonly WaveFormat _format;

        /// <param name="source">上游源，需同时实现 IWaveProvider 与 IWaveSourceProvider（如 SeamlessWaveProvider）。</param>
        /// <param name="targetSampleRate">目标采样率（Hz），必须大于源采样率（只做升频）。</param>
        public ResamplingSourceProvider(IWaveSourceProvider source, int targetSampleRate)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            if (source is not IWaveProvider waveProvider)
            {
                throw new ArgumentException("SRC 的源必须同时实现 IWaveProvider。", nameof(source));
            }

            if (targetSampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
            }

            var toFloat = new WaveToSampleProvider(waveProvider);
            var resampler = new WdlResamplingSampleProvider(toFloat, targetSampleRate);
            _output = new SampleToWaveProvider24(resampler);
            _format = _output.WaveFormat;
        }

        public WaveFormat WaveFormat => _format;

        /// <summary>时长按源时域转发（重采样不改变时长）。</summary>
        public TimeSpan TotalTime => _source.TotalTime;

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState => _source.ProbeCurrentState;

        public bool NextMounted => _source.NextMounted;

        public int Read(byte[] buffer, int offset, int count) => _output.Read(buffer, offset, count);

        /// <summary>拖动转发到源（源位置 = 目标位置，时长一致）。</summary>
        public void Seek(TimeSpan position) => _source.Seek(position);
    }
}
