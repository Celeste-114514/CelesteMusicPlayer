using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 量化前 Dither / 噪声整形（对齐 ECHO PcmDitherTransform）：
    ///  - off：不加任何抖动，直接舍入量化
    ///  - tpdf：三角分布抖动（TPDF），两个均匀随机数相减 × LSB，消除量化失真相关的可闻伪影
    ///  - highpass：高通 TPDF（(tpdf - 上一次tpdf) × 0.5），把抖动噪声频谱推到人耳不敏感的高频
    ///  - ns5：TPDF(±0.5LSB) + 5 阶误差反馈噪声整形（FIR 系数 [0.82,-0.38,0.19,-0.08,0.025]），
    ///    量化误差反馈到输入，把量化噪声整形到高频，中低频更干净
    /// 用于 SRC 升频后 float → 24bit 量化之前；位深转换 / 升频重算时建议至少用 tpdf。
    /// </summary>
    internal sealed class PcmDither
    {
        private const float Lsb24 = 1f / 8388608f; // 24bit LSB
        private const float Scale24 = 8388607f;    // 24bit 峰值（用于量化）

        private readonly string _mode;
        private readonly Random _rng = new();
        private float _prevTpdf;
        private float _h0, _h1, _h2, _h3, _h4; // NS-5 量化误差历史

        public PcmDither(string mode)
        {
            _mode = string.IsNullOrEmpty(mode) ? "off" : mode;
        }

        /// <summary>返回要加到 sample 上的值（LSB 量级）。调用方在量化前叠加。</summary>
        public float Add(float sample)
        {
            switch (_mode)
            {
                case "tpdf": return Tpdf(1.0f);
                case "highpass": return Highpass();
                case "ns5": return Ns5(sample);
                default: return 0f;
            }
        }

        /// <summary>TPDF 抖动，幅度 = ±amp × LSB。</summary>
        private float Tpdf(float amp)
        {
            float r = (float)(_rng.NextDouble() - _rng.NextDouble()); // [-1,1)
            return r * Lsb24 * amp;
        }

        private float Highpass()
        {
            float t = Tpdf(1.0f);
            float v = (t - _prevTpdf) * 0.5f;
            _prevTpdf = t;
            return v;
        }

        /// <summary>NS-5：TPDF(±0.5LSB) + 5 阶误差反馈整形。返回 dither+feedback 附加值。</summary>
        private float Ns5(float sample)
        {
            float d = Tpdf(0.5f);
            float feedback = 0.82f * _h0 - 0.38f * _h1 + 0.19f * _h2 - 0.08f * _h3 + 0.025f * _h4;
            float inWithFb = sample + d + feedback;
            float quantized = MathF.Round(inWithFb * Scale24) / Scale24;
            float err = quantized - inWithFb;

            _h4 = _h3;
            _h3 = _h2;
            _h2 = _h1;
            _h1 = _h0;
            _h0 = err;
            return d + feedback;
        }
    }
}
