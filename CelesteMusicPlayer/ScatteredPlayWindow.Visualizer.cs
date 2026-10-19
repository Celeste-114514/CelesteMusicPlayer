using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 零散播放窗口的可视化（2026-10-18）：扒自主程序播放页的「频谱柱」档。
    /// 数据源与主程序同一个：引擎真 FFT（<see cref="AudioPlaybackEngine.TryGetSpectrum"/>，
    /// 只读 DSP 链上的现成数据，不动播放字节流）；拿不到真实信号时回退到装饰性呼吸动画。
    /// 画法同主程序：40 根柱、高度取整到「段」形成数码格、柱顶悬一根慢落的峰值帽。
    /// </summary>
    public sealed partial class ScatteredPlayWindow
    {
        private readonly double[] _waveLevels = new double[FormatHelper.WaveBarCount];
        private readonly double[] _wavePeaks = new double[FormatHelper.WaveBarCount];
        private readonly double[] _wavePhases = new double[FormatHelper.WaveBarCount];
        private readonly float[] _spectrumBands = new float[FormatHelper.WaveBarCount];
        private readonly Border?[] _vizBars = new Border?[FormatHelper.WaveBarCount];
        private readonly Border?[] _vizCaps = new Border?[FormatHelper.WaveBarCount];
        private readonly Random _waveRandom = new();

        private DispatcherQueueTimer? _vizTimer;
        private int _vizIdleTicks;

        /// <summary>柱子/峰值帽颜色（跟随背景三态：深浅底用亮色，浅色底用深蓝）。</summary>
        private Color VizBarColor => _bgMode == "Light"
            ? Color.FromArgb(210, 59, 91, 219)
            : Color.FromArgb(205, 142, 162, 255);

        private Color VizCapColor => _bgMode == "Light"
            ? Color.FromArgb(255, 30, 63, 168)
            : Color.FromArgb(255, 192, 203, 255);

        /// <summary>起播成功时调用；播完/静止约 0.6 秒后自动停表（不空转）。</summary>
        private void StartVisualizerTimer()
        {
            if (_vizTimer == null)
            {
                _vizTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _vizTimer.Interval = TimeSpan.FromMilliseconds(50);
                _vizTimer.Tick += (_, _) => VisualizerTick();
            }

            _vizIdleTicks = 0;
            if (!_vizTimer.IsRunning)
            {
                _vizTimer.Start();
            }
        }

        private void StopVisualizerTimer()
        {
            try
            {
                _vizTimer?.Stop();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.StopVisualizerTimer", caught);
            }
        }

        /// <summary>背景模式切换后刷新配色并立即重绘一帧。</summary>
        private void ApplyVisualizerColors()
        {
            DrawVisualizerBars();
        }

        private void VisualizerTick()
        {
            Safe(() =>
            {
                bool playing = _engine?.IsPlaying == true;
                double t = Environment.TickCount64 / 1000.0;

                // 真频谱优先；拿不到（DSD 直出 / 样本不足 / 没在播）回退装饰性动画
                bool realSpectrum = playing && _engine != null && _engine.TryGetSpectrum(_spectrumBands);

                bool changed = false;
                for (int i = 0; i < FormatHelper.WaveBarCount; i++)
                {
                    double target;
                    if (playing)
                    {
                        if (realSpectrum)
                        {
                            target = Math.Clamp(_spectrumBands[i], 0.06, 1.0);
                        }
                        else
                        {
                            // 对称呼吸式频谱：中间高两边低、每柱各自节奏（与主程序同款）
                            double rhythm = 0.5 + 0.5 * Math.Sin(t * 1.8 + _wavePhases[i]);
                            double halfSpan = Math.Max(1.0, (FormatHelper.WaveBarCount - 1) / 2.0);
                            double pos = (i - (FormatHelper.WaveBarCount - 1) / 2.0) / halfSpan;
                            double symmetry = 0.5 + 0.5 * (1.0 - Math.Min(1.0, Math.Abs(pos)));
                            target = Math.Clamp(rhythm * symmetry, 0.1, 1.0);
                        }
                    }
                    else
                    {
                        target = IdleLevel(i);
                    }

                    double ease = realSpectrum ? 0.55 : (playing ? 0.35 : 0.18);
                    double next = _waveLevels[i] + (target - _waveLevels[i]) * ease;
                    if (Math.Abs(next - _waveLevels[i]) > 0.002)
                    {
                        changed = true;
                    }

                    _waveLevels[i] = next;
                }

                if (changed || playing)
                {
                    DrawVisualizerBars();
                }

                if (!playing)
                {
                    _vizIdleTicks++;
                    if (_vizIdleTicks >= 12 || !changed)
                    {
                        StopVisualizerTimer();
                        _vizIdleTicks = 0;
                    }
                }
                else
                {
                    _vizIdleTicks = 0;
                }
            });
        }

        /// <summary>未播放时的静态频谱形状（始终可见，中间高两边低）。与主程序同一函数。</summary>
        private static double IdleLevel(int index)
        {
            double shape = 0.22 + 0.28 * (0.5 + 0.5 * Math.Sin(index * 1.7 + 1.3));
            return Math.Max(0.18, shape * FormatHelper.SpectrumEnvelope(index));
        }

        private void DrawVisualizerBars()
        {
            try
            {
                double width = VisualizerCanvas.ActualWidth;
                double height = VisualizerCanvas.ActualHeight;
                if (width <= 1 || height <= 1)
                {
                    return;
                }

                const int mainCount = FormatHelper.WaveBarCount;
                double gap = 2;
                double barWidth = Math.Max(2, (width - gap * (mainCount - 1)) / mainCount);

                // 段高：按画布高度分档，柱高取整到段上，形成「一格格」的数码感
                double segH = Math.Max(3.0, Math.Round(height / 8.0));

                // 峰值帽慢落（每 tick 落 0.035，约 0.7s 从顶落底）
                for (int i = 0; i < mainCount; i++)
                {
                    double level = Math.Clamp(_waveLevels[i], 0.06, 1.0);
                    _wavePeaks[i] = Math.Max(_wavePeaks[i] - 0.035, level);
                }

                while (VisualizerCanvas.Children.Count < mainCount * 2)
                {
                    var border = new Border
                    {
                        CornerRadius = new CornerRadius(1),
                        IsHitTestVisible = false
                    };
                    VisualizerCanvas.Children.Add(border);
                }

                Color barColor = VizBarColor;
                Color capColor = VizCapColor;
                SolidColorBrush barBrush = new(barColor);
                SolidColorBrush capBrush = new(capColor);

                for (int i = 0; i < mainCount; i++)
                {
                    if (VisualizerCanvas.Children[i] is not Border bar)
                    {
                        continue;
                    }

                    bar.Background = barBrush;

                    // 高度取整到段：方块段效果
                    double rawH = Math.Max(segH, height * Math.Clamp(_waveLevels[i], 0.12, 1.0) * 1.15);
                    double barHeight = Math.Round(rawH / segH) * segH;
                    if (barHeight > height)
                    {
                        barHeight = Math.Round(height / segH) * segH;
                    }

                    double left = i * (barWidth + gap);
                    bar.Width = barWidth;
                    bar.Height = barHeight;
                    Canvas.SetLeft(bar, left);
                    Canvas.SetTop(bar, (height - barHeight) / 2);

                    // 峰值帽：满亮度小横条，悬在柱顶上方 2px，慢落
                    if (VisualizerCanvas.Children[mainCount + i] is Border cap)
                    {
                        double peakH = Math.Max(segH, height * Math.Clamp(_wavePeaks[i], 0.12, 1.0) * 1.15);
                        double peakTop = (height - Math.Round(peakH / segH) * segH) / 2 - 4;
                        cap.Width = barWidth;
                        cap.Height = 3;
                        cap.Background = capBrush;
                        Canvas.SetLeft(cap, left);
                        Canvas.SetTop(cap, Math.Max(0, peakTop));
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.DrawVisualizerBars", caught);
            }
        }
    }
}
