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

        /// <summary>拖动截止竖线期间置 true：一次拖动会触发几百次 ValueChanged → ApplyDspToEngine
        /// （写档 + 下发），没必要每帧做，松手时统一补一次。</summary>
        private bool _dspXfeedSuppressPush;

        private bool _dspXfeedDragging;

        // ─────────────────────────────────────────────────────────────
        // 事件入口
        // ─────────────────────────────────────────────────────────────

        private void DspXfeedCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawDspXfeedCurve();
        }

        private void DspXfeedCurve_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (DspXfeedCurveCanvas == null)
            {
                return;
            }

            try
            {
                _dspXfeedDragging = true;
                _dspXfeedSuppressPush = true;
                DspXfeedCurveCanvas.CapturePointer(e.Pointer);
                ApplyDspXfeedDrag(e.GetCurrentPoint(DspXfeedCurveCanvas).Position.X);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.DspXfeedCurve_PointerPressed", caught);
            }
        }

        private void DspXfeedCurve_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dspXfeedDragging || DspXfeedCurveCanvas == null)
            {
                return;
            }

            try
            {
                ApplyDspXfeedDrag(e.GetCurrentPoint(DspXfeedCurveCanvas).Position.X);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.DspXfeedCurve_PointerMoved", caught);
            }
        }

        private void DspXfeedCurve_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dspXfeedDragging)
            {
                return;
            }

            _dspXfeedDragging = false;
            _dspXfeedSuppressPush = false;

            try
            {
                DspXfeedCurveCanvas?.ReleasePointerCapture(e.Pointer);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.DspXfeedCurve_PointerReleased.Capture", caught);
            }

            // 松手才落盘 + 下发（Crossfeed 走的是 DspExtraStore 那条链）
            ApplyDspToEngine();
        }

        /// <summary>把画布上的横坐标翻译成截止频率（对数轴 → Hz），写回滑杆。</summary>
        private void ApplyDspXfeedDrag(double x)
        {
            var (padL, plotW) = GetXfeedPlotRect();
            if (plotW <= 2)
            {
                return;
            }

            double frac = Math.Clamp((x - padL) / plotW, 0.0, 1.0);
            double freq = Math.Pow(10.0, Math.Log10(XfeedFreqMin) + frac * (Math.Log10(XfeedFreqMax) - Math.Log10(XfeedFreqMin)));

            Slider? slider = AudioFxChannelCrossfeedCutoffSlider;
            if (slider != null)
            {
                slider.Value = Math.Clamp(freq, slider.Minimum, slider.Maximum);
            }
        }

        private void DspXfeedPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag)
            {
                return;
            }

            // (强度%, 截止 Hz)
            (double level, double cutoff) preset = tag switch
            {
                "subtle" => (15.0, 500.0),
                "natural" => (25.0, 700.0),
                "wide" => (40.0, 1200.0),
                "strong" => (60.0, 2000.0),
                _ => (25.0, 700.0)
            };

            if (AudioFxChannelCrossfeedToggle != null)
            {
                AudioFxChannelCrossfeedToggle.IsOn = true;
            }

            if (AudioFxChannelCrossfeedSlider != null)
            {
                AudioFxChannelCrossfeedSlider.Value = preset.level;
            }

            if (AudioFxChannelCrossfeedCutoffSlider != null)
            {
                AudioFxChannelCrossfeedCutoffSlider.Value = preset.cutoff;
            }

            RefreshCrossfeedCutoffReadout();
            RefreshDspXfeedLevelReadout();
            RedrawDspXfeedCurve();
            SyncDspPowerSwitches();
        }

        // ─────────────────────────────────────────────────────────────
        // 读数 / 绘制
        // ─────────────────────────────────────────────────────────────

        /// <summary>强度百分比读数（滑杆旁那一行）。</summary>
        private void RefreshDspXfeedLevelReadout()
        {
            if (DspXfeedLevelText == null || AudioFxChannelCrossfeedSlider == null)
            {
                return;
            }

            DspXfeedLevelText.Text = AudioFxChannelCrossfeedSlider.Value.ToString("0") + "%";
        }

        internal void RedrawDspXfeedCanvas()
        {
            RedrawDspXfeedCurve();
        }

        /// <summary>
        /// 画「对侧漏过来多少」的频响：20*log10(amount × |H_lp(f)|)。
        /// 关闭时整条曲线落在 −∞，画一条底线并写明没启用。
        /// </summary>
        internal void RedrawDspXfeedCurve()
        {
            Canvas? canvas = DspXfeedCurveCanvas;
            if (canvas == null || canvas.ActualWidth <= 2 || canvas.ActualHeight <= 2)
            {
                return;
            }

            if (AudioFxChannelCrossfeedSlider == null || AudioFxChannelCrossfeedCutoffSlider == null)
            {
                return;
            }

            try
            {
                double w = canvas.ActualWidth;
                double h = canvas.ActualHeight;
                var (padL, plotW) = GetXfeedPlotRect();
                double padT = 12, padB = 22;
                double plotH = h - padT - padB;
                if (plotW <= 2 || plotH <= 2)
                {
                    return;
                }

                bool enabled = AudioFxChannelCrossfeedToggle != null && AudioFxChannelCrossfeedToggle.IsOn;
                double amount = AudioFxChannelCrossfeedSlider.Value / 100.0;
                double cutoff = AudioFxChannelCrossfeedCutoffSlider.Value;
                double fs = GetXfeedWorkingSampleRate();

                bool dark = IsDspCanvasHostDark(DspXfeedCurveHost);
                Color gridWeak = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
                Color gridStrong = dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(74, 0, 0, 0);
                Color labelColor = dark ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(145, 30, 30, 30);
                Color faintLabel = dark ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(105, 30, 30, 30);
                Color accent = dark ? Color.FromArgb(255, 0x2e, 0x71, 0x68) : Color.FromArgb(255, 0x0f, 0x76, 0x6e);
                Color caution = dark ? Color.FromArgb(220, 0xd9, 0x8c, 0x0b) : Color.FromArgb(220, 0xb4, 0x53, 0x09);

                if (enabled && GeekDspAccentColor() is Color ph)
                {
                    accent = ph;
                }

                double logMin = Math.Log10(XfeedFreqMin), logMax = Math.Log10(XfeedFreqMax);
                double X(double f) => padL + (Math.Log10(Math.Clamp(f, XfeedFreqMin, XfeedFreqMax)) - logMin) / (logMax - logMin) * plotW;
                double Y(double db) => padT + (XfeedDbMax - Math.Clamp(db, XfeedDbMin, XfeedDbMax)) / (XfeedDbMax - XfeedDbMin) * plotH;

                var children = canvas.Children;
                children.Clear();

                // 1) 网格 + 刻度
                foreach (double f in new[] { 20.0, 50.0, 100.0, 200.0, 500.0, 1000.0, 2000.0, 5000.0, 10000.0, 20000.0 })
                {
                    children.Add(new Shapes.Line
                    {
                        X1 = X(f), Y1 = padT, X2 = X(f), Y2 = padT + plotH,
                        Stroke = new SolidColorBrush(gridWeak), StrokeThickness = 1
                    });
                    string t = f >= 1000 ? (f / 1000.0).ToString("0.#") + "k" : f.ToString("0");
                    children.Add(MakeXfeedLabel(t, X(f) - 16, padT + plotH + 4, 32, TextAlignment.Center, labelColor));
                }

                for (double db = 0; db >= XfeedDbMin; db -= 12.0)
                {
                    children.Add(new Shapes.Line
                    {
                        X1 = padL, Y1 = Y(db), X2 = padL + plotW, Y2 = Y(db),
                        Stroke = new SolidColorBrush(Math.Abs(db) < 0.001 ? gridStrong : gridWeak),
                        StrokeThickness = Math.Abs(db) < 0.001 ? 1.4 : 1
                    });
                    children.Add(MakeXfeedLabel(db.ToString("0"), 0, Y(db) - 7, 38, TextAlignment.Right, labelColor));
                }

                children.Add(MakeXfeedLabel("dB", 0, padT - 2, 38, TextAlignment.Right, faintLabel));
                children.Add(MakeXfeedLabel(
                    "按 " + (fs / 1000.0).ToString("0.#") + " kHz 计算", padL + plotW - 120, padT + 2, 120,
                    TextAlignment.Right, faintLabel));

                if (!enabled || amount <= 0.0001)
                {
                    string msg = !enabled ? "Crossfeed 没启用，对侧完全不混入。" : "强度是 0，等于没混。";
                    children.Add(MakeXfeedLabel(msg, padL + plotW / 2 - 150, padT + plotH / 2 - 8, 300, TextAlignment.Center, labelColor));
                    UpdateDspXfeedNote(false, amount, cutoff, fs);
                    return;
                }

                // 2) 曲线本体
                var pts = new PointCollection();
                int samples = 220;
                for (int i = 0; i < samples; i++)
                {
                    double f = Math.Pow(10.0, logMin + (logMax - logMin) * i / (samples - 1));
                    double gainDb = 20.0 * Math.Log10(Math.Max(amount * CrossfeedLowpassMagnitude(f, cutoff, fs), 1e-12));
                    pts.Add(new Windows.Foundation.Point(X(f), Y(gainDb)));
                }

                var area = new PointCollection();
                area.Add(new Windows.Foundation.Point(X(XfeedFreqMin), Y(XfeedDbMin)));
                foreach (Windows.Foundation.Point p in pts)
                {
                    area.Add(p);
                }

                area.Add(new Windows.Foundation.Point(X(XfeedFreqMax), Y(XfeedDbMin)));
                children.Add(new Shapes.Polygon
                {
                    Points = area,
                    Fill = new SolidColorBrush(Color.FromArgb((byte)(dark ? 40 : 34), accent.R, accent.G, accent.B)),
                    Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(padL, padT, plotW, plotH) }
                });
                children.Add(new Shapes.Polyline
                {
                    Points = pts,
                    Stroke = new SolidColorBrush(accent),
                    StrokeThickness = 2.2,
                    StrokeLineJoin = PenLineJoin.Round,
                    Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(padL - 2, padT - 2, plotW + 4, plotH + 4) }
                });

                // 3) 截止竖线（可拖）
                children.Add(new Shapes.Line
                {
                    X1 = X(cutoff), Y1 = padT, X2 = X(cutoff), Y2 = padT + plotH,
                    Stroke = new SolidColorBrush(caution),
                    StrokeThickness = 1.4,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                });
                children.Add(MakeXfeedLabel(
                    cutoff.ToString("0") + " Hz", Math.Clamp(X(cutoff) - 40, 0, Math.Max(0, w - 84)), padT + 2, 84,
                    TextAlignment.Center, caution));

                UpdateDspXfeedNote(true, amount, cutoff, fs);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RedrawDspXfeedCurve", caught);
            }
        }

        /// <summary>下方说明：给三个具体频点的混入量，比看图更容易建立直觉。</summary>
        private void UpdateDspXfeedNote(bool enabled, double amount, double cutoff, double fs)
        {
            if (DspXfeedCurveNote == null)
            {
                return;
            }

            if (!enabled || amount <= 0.0001)
            {
                DspXfeedCurveNote.Text = enabled
                    ? "强度还是 0，先把上面的强度拉起来。"
                    : "Crossfeed 没启用，对侧完全不混入。";
                return;
            }

            string At(double f)
            {
                double db = 20.0 * Math.Log10(Math.Max(amount * CrossfeedLowpassMagnitude(f, cutoff, fs), 1e-12));
                return db.ToString("0.0") + " dB";
            }

            DspXfeedCurveNote.Text = $"截止 {cutoff:0} Hz：低音 100Hz 混进来 {At(100.0)}，"
                + $"中频 1kHz {At(1000.0)}，高频 8kHz {At(8000.0)}（相对本侧的比例）。";
        }

        /// <summary>
        /// 内核那个一阶 one-pole 的幅频响应绝对值：H(z) = a / (1 − (1−a)z⁻¹)，
        /// a = 1 − exp(−2π·fc/fs)。DC 处归一到 1，Nyquist 处趋近 a/(2−a)。
        /// </summary>
        private static double CrossfeedLowpassMagnitude(double freqHz, double cutoffHz, double sampleRate)
        {
            if (sampleRate <= 1.0)
            {
                return 0.0;
            }

            double a = 1.0 - Math.Exp(-2.0 * Math.PI * cutoffHz / sampleRate);
            double w = 2.0 * Math.PI * Math.Clamp(freqHz, 1.0, sampleRate / 2.0) / sampleRate;

            double re = 1.0 - (1.0 - a) * Math.Cos(w);
            double im = (1.0 - a) * Math.Sin(w);
            double denom = Math.Sqrt(re * re + im * im);
            return denom > 1e-12 ? a / denom : 0.0;
        }

        /// <summary>
        /// 曲线用的采样率：crossfeed 位于 SRC 之后，看到的是重采样后的流，
        /// 所以 SRC 开了就用 SRC 目标值；没开则取 48 kHz 这个最常见的基准，
        /// 并把实际取值标在图上（不是精确真值，别装成精确真值）。
        /// </summary>
        private double GetXfeedWorkingSampleRate()
        {
            try
            {
                if (SrcRateCombo != null && SrcRateCombo.SelectedIndex >= 0
                    && SrcRateCombo.SelectedIndex < SrcRateOptions.Length)
                {
                    int hz = SrcRateOptions[SrcRateCombo.SelectedIndex].Hz;
                    if (hz > 0)
                    {
                        return hz;
                    }
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

        private static UIElement MakeXfeedLabel(string text, double x, double y, double width, TextAlignment align, Color color)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new SolidColorBrush(color),
                Width = width,
                TextAlignment = align
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            return tb;
        }
    }
}
