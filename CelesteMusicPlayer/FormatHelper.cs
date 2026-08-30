using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>数值/时间格式化与曲线计算工具：从 MainWindow 里抽出的无状态静态方法。
    /// </summary>
    internal static class FormatHelper
    {
        /// <summary>波形/频谱柱数量。原本是 MainWindow 的 private const，
        /// 因 SpectrumEnvelope 搬到这里而一并上移；MainWindow 与 DSP 侧都改为引用本常量，
        /// 保证 40 这个数只有一处定义（改一处即可全局生效）。</summary>
        public const int WaveBarCount = 40;

        public static string FormatAudioFxDb(double db)
        {
            double r = Math.Round(db, 1);
            return r > 0 ? "+" + r.ToString("0.#") : r.ToString("0.#");
        }

        public static string FormatCrossfade(int ms)
        {
            if (ms <= 0) return "关闭";
            if (ms < 1000) return ms + " 毫秒";
            double sec = ms / 1000.0;
            return (Math.Abs(sec % 1) < 0.05 ? sec.ToString("0") : sec.ToString("0.0")) + " 秒";
        }

        public static string FormatAudioFxFreq(double f)
        {
            if (f >= 1000) return (f / 1000.0).ToString("0.##") + " kHz";
            return f.ToString("0") + " Hz";
        }

        /// <summary>线性幅度 → 电平表刻度（0..1）。按 dBFS 映射：-60dB 起、0dB 满刻度。</summary>
        public static float LinearToMeterFraction(float linear)
        {
            if (!(linear > 1e-7f))
            {
                return 0f;
            }

            double db = 20.0 * Math.Log10(linear);
            if (db <= -60.0) return 0f;
            if (db >= 0.0) return 1f;
            return (float)(1.0 + db / 60.0);
        }

        /// <summary>频谱包络：中间高、两边低。</summary>
        public static double SpectrumEnvelope(int index)
        {
            double center = (WaveBarCount - 1) / 2.0;
            double envelope = 1.0 - 0.55 * Math.Abs(index - center) / Math.Max(1.0, center);
            return Math.Max(0.2, envelope);
        }
    }
}