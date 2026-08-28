using System;
using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 采样率转换（SRC）源：把内部源（目前是 <see cref="SeamlessWaveProvider"/>）重采样到目标采样率，
    /// 在链路中位于「无缝续接源」与「DSP 链」之间：
    ///   源(IWaveProvider) → WaveToSampleProvider(float，源采样率)
    ///     → WdlResamplingSampleProvider(float，目标采样率，按质量档位设 WDL SetMode)
    ///     → [可选 Dither / 噪声整形] → 24bit PCM（目标采样率）
    ///
    /// 重采样用 NAudio 的 WDL 重采样器（与 Reaper 同源）。质量三档（对齐 ECHO SRC 的
    /// Balanced/Transparent/Low latency）通过反射设置内部 WdlResampler.SetMode：
    ///  - lowlatency：快速插值（最低 CPU）
    ///  - balanced：均衡（默认，滤波折中）
    ///  - transparent：大 sinc 表（最高质量，CPU 最高）
    /// 反射失败时回退到 WdlResamplingSampleProvider 默认模式，不影响功能。
    ///
    /// Dither / 噪声整形（对齐 ECHO PcmDitherTransform）在 24bit 量化前叠加，
    /// 升频重算的插值样本 + 量化时建议开启至少 tpdf。
    ///
    /// 时长 / 拖动 / 无缝续接状态仍按「源」的时域语义转发（重采样不改变时长），
    /// 所以上层进度条、无缝续接与交叉淡化的判定都不受影响。
    /// </summary>
    internal sealed class ResamplingSourceProvider : IWaveProvider, IWaveSourceProvider
    {
        public const string QualityLowLatency = "lowlatency";
        public const string QualityBalanced = "balanced";
        public const string QualityTransparent = "transparent";

        public const string DitherOff = "off";
        public const string DitherTpdf = "tpdf";
        public const string DitherHighpass = "highpass";
        public const string DitherNs5 = "ns5";

        private readonly IWaveSourceProvider _source;
        private readonly WdlResamplingSampleProvider _resampler;
        private readonly WaveFormat _format;
        private readonly int _channels;
        private readonly PcmDither[] _dither;
        private float[] _tmpBuf = Array.Empty<float>();

        /// <param name="source">上游源，需同时实现 IWaveProvider 与 IWaveSourceProvider（如 SeamlessWaveProvider）。</param>
        /// <param name="targetSampleRate">目标采样率（Hz），必须大于源采样率（只做升频）。</param>
        /// <param name="quality">质量档位：lowlatency / balanced / transparent。</param>
        /// <param name="ditherMode">量化前抖动：off / tpdf / highpass / ns5。</param>
        public ResamplingSourceProvider(IWaveSourceProvider source, int targetSampleRate, string quality = QualityBalanced, string ditherMode = DitherOff)
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
            _resampler = new WdlResamplingSampleProvider(toFloat, targetSampleRate);
            ApplyQuality(_resampler, quality);

            _channels = source.WaveFormat.Channels;
            _dither = new PcmDither[Math.Max(1, _channels)];
            for (int c = 0; c < _dither.Length; c++)
            {
                _dither[c] = new PcmDither(ditherMode);
            }

            int blockAlign = _channels * 3; // 24bit
            _format = WaveFormat.CreateCustomFormat(
                WaveFormatEncoding.Pcm, targetSampleRate, _channels,
                targetSampleRate * blockAlign, blockAlign, 24);
        }

        public WaveFormat WaveFormat => _format;

        /// <summary>时长按源时域转发（重采样不改变时长）。</summary>
        public TimeSpan TotalTime => _source.TotalTime;

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState => _source.ProbeCurrentState;

        public bool NextMounted => _source.NextMounted;

        public int Read(byte[] buffer, int offset, int count)
        {
            int blockAlign = _format.BlockAlign;
            if (blockAlign <= 0) return 0;

            int frames = count / blockAlign;
            if (frames <= 0) return 0;

            int samples = frames * _channels;
            if (_tmpBuf.Length < samples)
            {
                _tmpBuf = new float[samples];
            }

            // 重采样输出 float（可能不足 frames——源读尽/内部缓冲不足时返回部分或 0）
            int got = _resampler.Read(_tmpBuf, 0, samples);
            if (got <= 0) return 0;

            // Dither + 24bit 量化编码
            int bo = offset;
            for (int i = 0; i < got; i++)
            {
                float s = _tmpBuf[i];
                s += _dither[i % _channels].Add(s);
                int v = (int)Math.Clamp(MathF.Round(s * 8388607f), -8388608, 8388607);
                buffer[bo] = (byte)(v & 0xFF);
                buffer[bo + 1] = (byte)((v >> 8) & 0xFF);
                buffer[bo + 2] = (byte)((v >> 16) & 0xFF);
                bo += 3;
            }

            return got / _channels * blockAlign;
        }

        /// <summary>拖动转发到源（源位置 = 目标位置，时长一致）。</summary>
        public void Seek(TimeSpan position) => _source.Seek(position);

        /// <summary>按质量档位设置 WDL 重采样器模式（反射；失败回退默认，不影响功能）。</summary>
        private static void ApplyQuality(WdlResamplingSampleProvider provider, string quality)
        {
            try
            {
                FieldInfo? f = typeof(WdlResamplingSampleProvider).GetField(
                    "resampler", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f?.GetValue(provider) is not NAudio.Dsp.WdlResampler r)
                {
                    return;
                }

                switch (quality)
                {
                    case QualityLowLatency:
                        // 快速插值（4-point），最低 CPU
                        r.SetMode(true, 0, false, 0, 0);
                        break;
                    case QualityTransparent:
                        // 大 sinc 表 + 细插值粒度，最高质量
                        r.SetMode(false, 0, true, 1024, 64);
                        r.SetFilterParms(0.693f, 0.707f);
                        break;
                    default: // balanced
                        r.SetMode(false, 2, false, 0, 0);
                        r.SetFilterParms(0.693f, 0.707f);
                        break;
                }
            }
            catch
            {
                // 反射失败 → 使用 WdlResamplingSampleProvider 默认模式（NAudio 构造时已设置）
            }
        }
    }
}
