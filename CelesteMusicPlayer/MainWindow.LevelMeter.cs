using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 实时电平表 UI：底部播放栏的一组竖条（左→右对应各声道）。
    /// 渲染线程把每声道峰值/RMS 写进 LevelMeter，这里用定时器（约 30fps）读出来画成条。
    /// 显示的是 post-DSP 信号，也就是真正送去输出的电平。
    /// </summary>
    public sealed partial class MainWindow
    {
        private DispatcherQueueTimer? _levelMeterTimer;
        private readonly List<LevelMeterBar> _levelMeterBars = new();
        private float[] _levelPeakBuf = Array.Empty<float>();
        private float[] _levelRmsBuf = Array.Empty<float>();
        private float[] _levelFillDisplay = Array.Empty<float>(); // 平滑后的 RMS 显示值（0..1）
        private float[] _levelPeakDisplay = Array.Empty<float>(); // 平滑后的峰值显示值（0..1）

        private const int LevelMeterBarHeight = 40;
        private const int LevelMeterBarWidth = 5;
        private const int LevelMeterMaxBars = 8; // 超过 8 声道不再增加条数（5.1/7.1 已够用）

        private sealed class LevelMeterBar
        {
            public Border Track = null!;
            public Rectangle Fill = null!;
            public Rectangle PeakLine = null!;
            public GradientStop FillStop = null!;
        }

        /// <summary>在构造函数里调用：建立刷新定时器（约 33ms ≈ 30fps）。</summary>
        private void InitializeLevelMeter()
        {
            try
            {
                _levelMeterTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _levelMeterTimer.Interval = TimeSpan.FromMilliseconds(33);
                _levelMeterTimer.Tick += LevelMeterTimer_Tick;
                _levelMeterTimer.Start();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.LevelMeter.cs", caught);
            }
        }

        private void LevelMeterTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            try
            {
                StackPanel? panel = LevelMeterPanel;
                if (panel == null)
                {
                    return;
                }

                // 未播放 / DSD 直出（不挂 DSP 链）时没有可测电平 → 隐藏并清零
                int channels = _audioEngine?.LevelMeterChannels ?? 0;
                if (channels <= 0 || _audioEngine == null)
                {
                    if (panel.Visibility != Visibility.Collapsed)
                    {
                        panel.Visibility = Visibility.Collapsed;
                        for (int i = 0; i < _levelFillDisplay.Length; i++) { _levelFillDisplay[i] = 0f; _levelPeakDisplay[i] = 0f; }
                    }

                    return;
                }

                // 声道数变化（换歌/换设备/单声道↔立体声）→ 重建条子
                int bars = Math.Min(channels, LevelMeterMaxBars);
                if (_levelMeterBars.Count != bars)
                {
                    RebuildLevelMeterBars(bars);
                }

                if (_levelPeakBuf.Length < bars) _levelPeakBuf = new float[bars];
                if (_levelRmsBuf.Length < bars) _levelRmsBuf = new float[bars];

                bool got = _audioEngine.TryGetLevels(_levelPeakBuf, _levelRmsBuf);
                if (!got)
                {
                    // 取不到（停止中）：把条子压到 0
                    for (int i = 0; i < bars; i++) { UpdateLevelMeterBar(i, 0f, 0f); }
                    if (panel.Visibility != Visibility.Collapsed) panel.Visibility = Visibility.Collapsed;
                    return;
                }

                if (panel.Visibility != Visibility.Visible)
                {
                    panel.Visibility = Visibility.Visible;
                }

                for (int i = 0; i < bars; i++)
                {
                    float fillTarget = FormatHelper.LinearToMeterFraction(_levelRmsBuf[i]);
                    float peakTarget = FormatHelper.LinearToMeterFraction(_levelPeakBuf[i]);

                    // 快起慢落（attack 立即跟随，release 缓慢回落），避免闪烁
                    _levelFillDisplay[i] = fillTarget > _levelFillDisplay[i]
                        ? fillTarget
                        : Math.Max(fillTarget, _levelFillDisplay[i] - 0.10f);
                    _levelPeakDisplay[i] = peakTarget > _levelPeakDisplay[i]
                        ? peakTarget
                        : Math.Max(peakTarget, _levelPeakDisplay[i] - 0.025f);

                    UpdateLevelMeterBar(i, _levelFillDisplay[i], _levelPeakDisplay[i]);
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.LevelMeter.cs", caught);
            }
        }

        /// <summary>重建电平条（声道数变化时）。</summary>
        private void RebuildLevelMeterBars(int bars)
        {
            LevelMeterPanel.Children.Clear();
            _levelMeterBars.Clear();
            _levelFillDisplay = new float[bars];
            _levelPeakDisplay = new float[bars];

            for (int i = 0; i < bars; i++)
            {
                var track = new Border
                {
                    Width = LevelMeterBarWidth,
                    Height = LevelMeterBarHeight,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                };

                var grid = new Grid();
                track.Child = grid;

                // 填充条：满高度 + 固定渐变（下绿上红），再用 Clip 裁出当前音量对应的高度，
                // 这样颜色刻度不随音量拉伸，与常规电平表一致。
                var fill = new Rectangle
                {
                    Width = LevelMeterBarWidth,
                    Height = LevelMeterBarHeight,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                var grad = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 1), EndPoint = new Windows.Foundation.Point(0, 0) };
                grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 76, 200, 120), Offset = 0.0 });   // 绿
                grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 190, 215, 90), Offset = 0.55 });  // 黄绿
                grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 240, 185, 70), Offset = 0.78 });  // 橙黄
                var topStop = new GradientStop { Color = Color.FromArgb(255, 230, 90, 80), Offset = 1.0 };             // 红
                grad.GradientStops.Add(topStop);
                fill.Fill = grad;

                // 峰值指示细线
                var peakLine = new Rectangle
                {
                    Width = LevelMeterBarWidth,
                    Height = 2,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Fill = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                    Opacity = 0.85,
                };

                grid.Children.Add(fill);
                grid.Children.Add(peakLine);

                LevelMeterPanel.Children.Add(track);
                _levelMeterBars.Add(new LevelMeterBar { Track = track, Fill = fill, PeakLine = peakLine, FillStop = topStop });

                UpdateLevelMeterBar(i, 0f, 0f);
            }
        }

        /// <summary>更新单根条子：fill=RMS 高度，peakLine=峰值标记位置。</summary>
        private void UpdateLevelMeterBar(int index, float fillFraction, float peakFraction)
        {
            if (index < 0 || index >= _levelMeterBars.Count)
            {
                return;
            }

            LevelMeterBar bar = _levelMeterBars[index];
            float fill = Math.Clamp(fillFraction, 0f, 1f);
            float peak = Math.Clamp(peakFraction, 0f, 1f);

            double fillH = LevelMeterBarHeight * fill;
            // 从底部裁出 fillH 高度（保留上方颜色刻度不随音量移动）
            bar.Fill.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, LevelMeterBarHeight - fillH, LevelMeterBarWidth, Math.Max(0, fillH)),
            };

            double peakY = LevelMeterBarHeight * (1.0 - peak);
            bar.PeakLine.Margin = new Thickness(0, 0, 0, Math.Max(0, peakY));
            bar.PeakLine.Opacity = peak > 0.005 ? 0.85 : 0.0;

            // 接近满刻度（>0.97）时峰值线转红，提示临近 0dBFS
            bar.PeakLine.Fill = peak > 0.97
                ? new SolidColorBrush(Color.FromArgb(255, 235, 80, 70))
                : new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        }

    }
}
