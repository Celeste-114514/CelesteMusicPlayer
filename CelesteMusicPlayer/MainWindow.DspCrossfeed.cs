// MainWindow.DspCrossfeed.cs
// 耳机 Crossfeed 独立页的频响曲线 + 截止频率拖动 + 预设。
//
// 2026-09-30：Crossfeed 从「声道工具」页搬到独立页（XAML 搬家时保留了全部 x:Name，
// 所以 MainWindow.EqDsp.cs 的 handler / MainWindow.Misc.cs 的 LoadAudioFxUiFromStore
// 一行都没改，仍然按老名字读写同一批控件）。本文件只加三样东西：曲线、拖动、预设。
//
// 内核口径（native/dsp-core/audio-engine/SpatialDspProcessor.cpp:34-60）：
//   mid  = 0.5*(L+R)
//   side = 0.5*(L-R)
//   lp  += a * (side - lp)            // 一阶 one-pole 低通，a = 1 - exp(-2π·fc/fs)
//   out  = side - amount * lp
// 也就是说「从对侧漏过来的量」= amount × |H_lp(f)|，H(z) = a / (1 - (1-a)z⁻¹)。
// 曲线严格按这个式子算（不是拿教科书的标准 RC 低通近似），DC 处归一到 1。
//
// 采样率的坑：a 依赖 fs，而 crossfeed 在 SRC 之后，看到的是重采样后的流。
// 所以曲线的 fs 取「SRC 目标」（未启用 SRC 时用 48 kHz 这一最常见基准），
// 并把实际用了多少 Hz 标在图上 —— 不假装它是精确真值。

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private const double XfeedFreqMin = 20.0;
        private const double XfeedFreqMax = 20000.0;

        /// <summary>纵轴显示范围（dB）。下限 −66 是为了给 −∞（强度 0）留出一条线的空间。</summary>
        private const double XfeedDbMin = -66.0;
        private const double XfeedDbMax = 6.0;

        /// <summary>
        /// 曲线用的采样率：crossfeed 位于 SRC 之后，看到的是重采样后的流，
        /// 所以 SRC 开了就用 SRC 目标值；没开则取 48 kHz 这个最常见的基准，
        /// 并把实际取值标在图上（不是精确真值，别装成精确真值）。
        /// </summary>
        private double GetXfeedWorkingSampleRate()
        {
            try
            {
                int hz = SrcParameters.TargetHz;
                if (hz > 0)
                {
                    return hz;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.GetXfeedWorkingSampleRate", caught);
            }

            return 48000.0;
        }

        private (double padL, double plotW) GetXfeedPlotRect()
        {
            double w = DspXfeedCurveCanvas?.ActualWidth ?? 0;
            double padL = 44;
            return (padL, Math.Max(0.0, w - padL - 14));
        }

    }
}
