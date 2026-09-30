// MainWindow.DspComp.cs
// 压缩器页的专业视图：静态传递曲线 / 入口电平表 / 压缩量历史折线。
//
// 职责边界（与 MainWindow.DspRack.cs 的分工）：
//   · 参数真值只有一份，在 DspRackStore（dsp-rack.json）。加载/下发/预设都在 MainWindow.DspRack.cs，
//     本文件一律「当场读滑杆 → 画出来」，不持有第二份参数镜像，避免和 store 漂移。
//   · GR 轮询定时器也在 DspRack.cs（EnsureDspCompGrTimer / DspCompGr_Tick），
//     Tick 只做「拿读数 → 交给 UpdateDspCompMeters」，绘制全在本文件。
//   · 传递曲线用的压缩公式是从 native/dsp-core/audio-engine/CompressorProcessor.cpp
//     的 computeReductionDb 逐行复刻来的（含软拐点的二次曲线段），所以曲线与内核实际行为一致，
//     不是另一套近似。改内核公式时必须同步改这里。
//
// 面板就绪守卫：所有 handler 都用 DspRack.cs 的 _dspRackReady 挡第一道。
// 理由同 MainWindow.EqDsp.cs：XAML 里 Slider 的 Value 初值、ToggleSwitch 的 IsOn 初值
// 会在 InitializeComponent 解析阶段就地触发事件，此时同页后续控件的 x:Name 字段还没连上，
// 直接访问就是空引用 → 整个进程秒崩（0xC000027B）。

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
// 项目统一约定：Shapes 走别名。WinUI3 的 Shapes 与别处同名类型容易打架，
// 写成别名既能用 Shapes.Line 这种短写法，又不会污染全局类型解析。
using Shapes = Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>传递曲线的纵/横轴范围（dBFS）。入口 -60..0 已覆盖常见母带电平。</summary>
        private const double CompCurveMinDb = -60.0;
        private const double CompCurveMaxDb = 0.0;

        /// <summary>传递曲线的取样点数（整幅宽 ~840px，2 点/px 足够平滑）。</summary>
        private const int CompCurveSamples = 241;

        /// <summary>压缩量历史：200ms 一拍 × 40 拍 = 最近 8 秒。</summary>
        private const int CompGrHistoryMax = 40;

        // 电平取样缓冲（LevelMeter 每声道一份，最多取前 16 声道里最大的）
        private readonly float[] _dspCompPeak = new float[16];
        private readonly float[] _dspCompRms = new float[16];

        /// <summary>最近的压缩量读数序列（dB，正 = 压掉多少），环形增长到常量上限后丢弃最旧的。</summary>
        private readonly List<double> _dspCompGrHistory = new();

        /// <summary>true = 电平表显示峰值，false = 显示 RMS（与页内开关同步）。</summary>
        private bool _dspCompMeterShowsPeak = true;

        // ─────────────────────────────────────────────────────────────
        // 内核公式复刻（CompressorProcessor::computeReductionDb）
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 给定入口电平（dBFS）与压缩器参数，算出压掉多少 dB（返回值 ≥ 0，正 = 压缩量）。
        /// 与 native CompressorProcessor.cpp 的 computeReductionDb 逐行同构：
        /// 软拐点区内是一条二次曲线，区外退化为斜率 slope = 1 - 1/ratio 的直线。
        /// </summary>
        private static double ComputeCompReductionDb(double inputDb, double thresholdDb, double ratio, double kneeDb)
        {
            double safeRatio = ratio < 1.0 ? 1.0 : ratio;
            double overDb = inputDb - thresholdDb;
            double slope = 1.0 - 1.0 / safeRatio;

            if (kneeDb <= 0.001)
            {
                return Math.Max(0.0, overDb * slope);
            }

            double halfKnee = kneeDb * 0.5;
            if (overDb <= -halfKnee)
            {
                return 0.0;
            }

            if (overDb >= halfKnee)
            {
                return overDb * slope;
            }

            double kneePosition = overDb + halfKnee;
            return slope * kneePosition * kneePosition / (2.0 * kneeDb);
        }

        /// <summary>
        /// 静态传递曲线在给定入口电平的输出电平（dBFS），按内核的先后次序算：
        /// 先压缩（dB 域）→ 补偿增益（dB 域相加）→ 再按干湿比做【线性】混合。
        /// 混合必须在线性域做（内核就是这么写的：dry * ((1-mix) + mix*wetGain)），
        /// 若在 dB 域加权平均会得到一条形状完全不对的曲线。
        /// </summary>
        private static double ComputeCompOutputDb(double inputDb, double thresholdDb, double ratio, double kneeDb, double makeupDb, double mix)
        {
            double reduction = ComputeCompReductionDb(inputDb, thresholdDb, ratio, kneeDb);
            double wetLinear = Math.Pow(10.0, (-reduction + makeupDb) / 20.0);
            double totalLinear = (1.0 - mix) + mix * wetLinear;
            if (totalLinear < 1e-9)
            {
                totalLinear = 1e-9;
            }

            return inputDb + 20.0 * Math.Log10(totalLinear);
        }

        // ─────────────────────────────────────────────────────────────
        // 事件入口
        // ─────────────────────────────────────────────────────────────

        private void DspCompCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_dspRackReady)
            {
                return;
            }

            RedrawDspCompTransferCurve();
        }

        private void DspCompGrCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_dspRackReady)
            {
                return;
            }

            RedrawDspCompGrHistory();
        }

        private void DspCompMeterMode_Toggled(object sender, RoutedEventArgs e)
        {
            // XAML 里写了 IsOn="True" 初值 + Toggled 绑定 → 解析期会就地触发一次，
            // 此时 DspCompPeakToggle 自己都可能还没连上，守卫挡掉后由 Tank 首拍补齐。
            if (!_dspRackReady)
            {
                return;
            }

            _dspCompMeterShowsPeak = DspCompPeakToggle?.IsOn ?? true;
        }

        // ─────────────────────────────────────────────────────────────
        // 每拍刷新：压缩量 + 入口电平
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// GR 定时器每一拍调用。更新大号压缩量读数、把这一拍记进 8 秒历史并重画折线、
        /// 以及用当前输出电平反推压缩器入口电平（估算，见面板说明）。
        /// </summary>
        private void UpdateDspCompMeters(float grDb)
        {
            if (!_dspRackReady)
            {
                return;
            }

            try
            {
                double gr = grDb > 0f ? grDb : 0.0;

                if (DspCompGrText != null)
                {
                    DspCompGrText.Text = gr.ToString("0.0") + " dB";
                }

                _dspCompGrHistory.Add(gr);
                while (_dspCompGrHistory.Count > CompGrHistoryMax)
                {
                    _dspCompGrHistory.RemoveAt(0);
                }

                RedrawDspCompGrHistory();

                UpdateDspCompInputMeter(gr);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateDspCompMeters", caught);
            }
        }

        /// <summary>
        /// 入口电平：LevelMeter 挂在 DSP 链末端，测的是【整链之后】的实际输出，
        /// 所以只能反推——把压缩环节那一坨增益（压缩量 + 补偿 + 干湿）除掉。
        /// 链上还有 EQ / 音量 / 响度归一化在前，因此这是估算值，UI 上也如实标了「估算」。
        /// </summary>
        private void UpdateDspCompInputMeter(double grDb)
        {
            bool compOn = DspCompToggle != null && DspCompToggle.IsOn;
            double makeup = DspCompMakeupSlider?.Value ?? 0.0;
            double mix = (DspCompMixSlider?.Value ?? 100.0) / 100.0;

            double outLinear = 0.0;
            bool hasSignal = false;

            int channels = _audioEngine?.LevelMeterChannels ?? 0;
            if (channels > 0 && _audioEngine != null)
            {
                Array.Clear(_dspCompPeak, 0, _dspCompPeak.Length);
                Array.Clear(_dspCompRms, 0, _dspCompRms.Length);

                if (_audioEngine.TryGetLevels(_dspCompPeak, _dspCompRms))
                {
                    int n = Math.Min(channels, _dspCompPeak.Length);
                    for (int i = 0; i < n; i++)
                    {
                        float v = _dspCompMeterShowsPeak ? _dspCompPeak[i] : _dspCompRms[i];
                        if (v > outLinear)
                        {
                            outLinear = v;
                        }
                    }

                    hasSignal = outLinear > 1e-7;
                }
            }

            double outDb = hasSignal ? 20.0 * Math.Log10(outLinear) : CompCurveMinDb - 1.0;

            double wetDb = compOn ? -grDb + makeup : 0.0;
            double wetLinear = Math.Pow(10.0, wetDb / 20.0);
            double totalLinear = compOn ? (1.0 - mix) + mix * wetLinear : 1.0;
            double inDb = outDb - 20.0 * Math.Log10(Math.Max(totalLinear, 1e-9));

            if (DspCompInText != null)
            {
                DspCompInText.Text = hasSignal
                    ? (inDb <= CompCurveMinDb + 0.5 ? "-∞" : inDb.ToString("0.0") + " dB")
                    : "—";
            }

            DrawDspCompInputMeterBar(hasSignal ? inDb : double.NegativeInfinity);
        }

        /// <summary>画细长电平条：-60..0 dB 铺满宽度，-12dB 起转黄、-3dB 起转红。</summary>
        private void DrawDspCompInputMeterBar(double db)
        {
            Canvas? canvas = DspCompInMeterCanvas;
            if (canvas == null || canvas.ActualWidth <= 1 || canvas.ActualHeight <= 1)
            {
                return;
            }

            try
            {
                double w = canvas.ActualWidth;
                double h = canvas.ActualHeight;
                bool dark = IsDspCompHostDark(DspCompCurveHost);
                Color trough = dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(34, 0, 0, 0);

                canvas.Children.Clear();
                canvas.Children.Add(new Shapes.Rectangle
                {
                    Width = w,
                    Height = h,
                    RadiusX = h / 2,
                    RadiusY = h / 2,
                    Fill = new SolidColorBrush(trough)
                });

                if (double.IsInfinity(db) || double.IsNaN(db))
                {
                    return;
                }

                double frac = (Math.Clamp(db, CompCurveMinDb, CompCurveMaxDb) - CompCurveMinDb)
                              / (CompCurveMaxDb - CompCurveMinDb);
                if (frac <= 0.001)
                {
                    return;
                }

                Color fill = db >= -3.0
                    ? Color.FromArgb(255, 0xd1, 0x34, 0x38)
                    : db >= -12.0
                        ? Color.FromArgb(255, 0xd9, 0x8c, 0x0b)
                        : (dark ? Color.FromArgb(255, 0x4a, 0xc0, 0x8a) : Color.FromArgb(255, 0x18, 0x8a, 0x5c));

                canvas.Children.Add(new Shapes.Rectangle
                {
                    Width = Math.Max(2.0, frac * w),
                    Height = h,
                    RadiusX = h / 2,
                    RadiusY = h / 2,
                    Fill = new SolidColorBrush(fill)
                });
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.DrawDspCompInputMeterBar", caught);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 绘制：8 秒压缩量折线
        // ─────────────────────────────────────────────────────────────

        private void RedrawDspCompGrHistory()
        {
            Canvas? canvas = DspCompGrCanvas;
            if (canvas == null || canvas.ActualWidth <= 2 || canvas.ActualHeight <= 2)
            {
                return;
            }

            try
            {
                double w = canvas.ActualWidth;
                double h = canvas.ActualHeight;
                double padL = 4, padR = 6, padT = 8, padB = 14;
                double plotW = w - padL - padR;
                double plotH = h - padT - padB;
                if (plotW <= 2 || plotH <= 2)
                {
                    return;
                }

                canvas.Children.Clear();

                bool dark = IsDspCompHostDark(DspCompGrHost);
                Color grid = dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
                Color label = dark ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(140, 30, 30, 30);
                Color accent = dark ? Color.FromArgb(255, 0x2e, 0x71, 0x68) : Color.FromArgb(255, 0x0f, 0x76, 0x6e);
                if (GeekDspAccentColor() is Color ph)
                {
                    accent = ph;
                }

                // 纵轴上限取整到 3/6/12/24 dB：既不会让小动作贴底，也不会让 2dB 撑满
                double peak = 0.0;
                foreach (double v in _dspCompGrHistory)
                {
                    if (v > peak)
                    {
                        peak = v;
                    }
                }

                double maxDb = 3.0;
                foreach (double cand in new[] { 3.0, 6.0, 12.0, 24.0 })
                {
                    if (peak <= cand * 0.92)
                    {
                        maxDb = cand;
                        break;
                    }

                    maxDb = cand;
                }

                double Y(double db) => padT + plotH - Math.Clamp(db / maxDb, 0.0, 1.0) * plotH;
                double X(int i, int total) => padL + (total <= 1 ? plotW : (double)i / (total - 1) * plotW);

                // 横向刻度（0 / maxDb）
                foreach (double db in new[] { 0.0, maxDb })
                {
                    canvas.Children.Add(new Shapes.Line
                    {
                        X1 = padL, Y1 = Y(db), X2 = padL + plotW, Y2 = Y(db),
                        Stroke = new SolidColorBrush(grid), StrokeThickness = 1
                    });
                    canvas.Children.Add(MakeEqAxisLabel(
                        db.ToString("0") + " dB", padL, Y(db) - (db > 0 ? -1 : 14), 48, TextAlignment.Left, label));
                }

                if (_dspCompGrHistory.Count < 2)
                {
                    return;
                }

                int count = _dspCompGrHistory.Count;
                var pts = new PointCollection();
                for (int i = 0; i < count; i++)
                {
                    pts.Add(new Windows.Foundation.Point(X(i, CompGrHistoryMax), Y(_dspCompGrHistory[i])));
                }

                // 填充面积（浅）
                var area = new PointCollection();
                area.Add(new Windows.Foundation.Point(X(0, CompGrHistoryMax), Y(0.0)));
                foreach (Windows.Foundation.Point p in pts)
                {
                    area.Add(p);
                }

                area.Add(new Windows.Foundation.Point(X(count - 1, CompGrHistoryMax), Y(0.0)));
                canvas.Children.Add(new Shapes.Polygon
                {
                    Points = area,
                    Fill = new SolidColorBrush(Color.FromArgb((byte)(dark ? 46 : 40), accent.R, accent.G, accent.B))
                });

                canvas.Children.Add(new Shapes.Polyline
                {
                    Points = pts,
                    Stroke = new SolidColorBrush(accent),
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round
                });

                // 末端高亮点（最新一拍）
                double lastY = Y(_dspCompGrHistory[^1]);
                var dot = new Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetLeft(dot, Math.Min(X(count - 1, CompGrHistoryMax) - 3.5, w - 8));
                Canvas.SetTop(dot, lastY - 3.5);
                canvas.Children.Add(dot);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RedrawDspCompGrHistory", caught);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 绘制：静态传递曲线
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 重画传递曲线。仅在压缩器页可见或有尺寸变化时才真正出图，隐藏时直接返回，不占 UI 线程。
        /// </summary>
        internal void RedrawDspCompTransferCurve()
        {
            Canvas? canvas = DspCompCurveCanvas;
            if (canvas == null || canvas.ActualWidth <= 2 || canvas.ActualHeight <= 2)
            {
                return;
            }

            // 面板还没构建好（解析期 SizeChanged 提前到达）→ 控件字段可能全是 null
            if (DspCompThresholdSlider == null || DspCompMixSlider == null)
            {
                return;
            }

            try
            {
                double w = canvas.ActualWidth;
                double h = canvas.ActualHeight;
                double padL = 42, padR = 14, padT = 12, padB = 22;
                double plotW = w - padL - padR;
                double plotH = h - padT - padB;
                if (plotW <= 2 || plotH <= 2)
                {
                    return;
                }

                double threshold = DspCompThresholdSlider.Value;
                double ratio = DspCompRatioSlider.Value;
                double knee = DspCompKneeSlider.Value;
                double makeup = DspCompMakeupSlider.Value;
                double mix = DspCompMixSlider.Value / 100.0;
                bool enabled = DspCompToggle != null && DspCompToggle.IsOn;

                double minDb = CompCurveMinDb, maxDb = CompCurveMaxDb;
                double X(double db) => padL + (db - minDb) / (maxDb - minDb) * plotW;
                double Y(double db) => padT + (maxDb - db) / (maxDb - minDb) * plotH;

                bool dark = IsDspCompHostDark(DspCompCurveHost);
                Color gridWeak = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
                Color gridStrong = dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(74, 0, 0, 0);
                Color labelColor = dark ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(145, 30, 30, 30);
                Color unitLabelColor = dark ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(105, 30, 30, 30);
                Color accent = dark ? Color.FromArgb(255, 0x2e, 0x71, 0x68) : Color.FromArgb(255, 0x0f, 0x76, 0x6e);
                Color unity = dark ? Color.FromArgb(140, 200, 200, 200) : Color.FromArgb(140, 90, 90, 90);
                Color caution = dark ? Color.FromArgb(220, 0xd9, 0x8c, 0x0b) : Color.FromArgb(220, 0xb4, 0x53, 0x09);

                // 极客皮肤：Canvas 上的 Shape 既不读 ThemeResource 也吃不到元素级资源字典，
                // 必须在这里显式换成磷光色（与 EQ 曲线同一套处理）。
                if (GeekDspAccentColor() is Color ph)
                {
                    accent = ph;
                }

                var children = canvas.Children;
                children.Clear();

                // 1) 网格 + 刻度（每 12 dB 一条）
                for (double db = minDb; db <= maxDb + 0.001; db += 12.0)
                {
                    children.Add(new Shapes.Line
                    {
                        X1 = X(db), Y1 = padT, X2 = X(db), Y2 = padT + plotH,
                        Stroke = new SolidColorBrush(gridWeak), StrokeThickness = 1
                    });
                    children.Add(new Shapes.Line
                    {
                        X1 = padL, Y1 = Y(db), X2 = padL + plotW, Y2 = Y(db),
                        Stroke = new SolidColorBrush(gridWeak), StrokeThickness = 1
                    });
                    children.Add(MakeEqAxisLabel(db.ToString("0"), 0, Y(db) - 7, padL - 8, TextAlignment.Right, labelColor));
                    children.Add(MakeEqAxisLabel(db.ToString("0"), X(db) - 16, padT + plotH + 4, 32, TextAlignment.Center, labelColor));
                }

                // 轴单位提示
                children.Add(MakeEqAxisLabel(
                    "入 dBFS →", padL + 2, padT + plotH + 4, 80, TextAlignment.Left, unitLabelColor));

                // 2) 1:1 参考线（完全不压时会走的线）
                children.Add(new Shapes.Line
                {
                    X1 = X(minDb), Y1 = Y(minDb), X2 = X(maxDb), Y2 = Y(maxDb),
                    Stroke = new SolidColorBrush(unity),
                    StrokeThickness = 1.2,
                    StrokeDashArray = new DoubleCollection { 4, 4 }
                });

                if (!enabled)
                {
                    // 关闭态：只留参考线，明说「输出 = 输入」，不画任何压缩曲线
                    children.Add(MakeEqAxisLabel(
                        "压缩器已关闭 · 输出 = 输入", padL + plotW / 2 - 90, padT + plotH / 2 - 8, 180,
                        TextAlignment.Center, labelColor));
                    return;
                }

                // 3) 拐点区淡色带：threshold ± knee/2，过渡段就在这里发生
                double kneeLo = Math.Clamp(threshold - knee / 2.0, minDb, maxDb);
                double kneeHi = Math.Clamp(threshold + knee / 2.0, minDb, maxDb);
                if (kneeHi - kneeLo > 0.2)
                {
                    var band = new Shapes.Rectangle
                    {
                        Width = X(kneeHi) - X(kneeLo),
                        Height = plotH,
                        Fill = new SolidColorBrush(Color.FromArgb((byte)(dark ? 22 : 18), accent.R, accent.G, accent.B))
                    };
                    Canvas.SetLeft(band, X(kneeLo));
                    Canvas.SetTop(band, padT);
                    children.Add(band);
                }

                // 4) 阈值竖线
                children.Add(new Shapes.Line
                {
                    X1 = X(threshold), Y1 = padT, X2 = X(threshold), Y2 = padT + plotH,
                    Stroke = new SolidColorBrush(caution),
                    StrokeThickness = 1.4,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                });
                children.Add(MakeEqAxisLabel(
                    "阈值 " + threshold.ToString("0.#"),
                    Math.Clamp(X(threshold) - 46, 0, Math.Max(0, w - 96)), padT + 2, 96,
                    TextAlignment.Center, caution));

                // 5) 传递曲线本体
                var pts = new PointCollection();
                for (int i = 0; i < CompCurveSamples; i++)
                {
                    double inDb = minDb + (maxDb - minDb) * i / (CompCurveSamples - 1);
                    double outDb = ComputeCompOutputDb(inDb, threshold, ratio, knee, makeup, mix);
                    pts.Add(new Windows.Foundation.Point(X(inDb), Y(outDb)));
                }

                // 补偿增益为正时曲线会冲出顶部 pen 区，clip 到绘图区内，避免画到刻度带上
                var shapeArea = new PointCollection();
                shapeArea.Add(new Windows.Foundation.Point(X(minDb), Y(minDb)));
                foreach (Windows.Foundation.Point p in pts)
                {
                    shapeArea.Add(p);
                }

                shapeArea.Add(new Windows.Foundation.Point(X(maxDb), Y(minDb)));

                children.Add(new Shapes.Polygon
                {
                    Points = shapeArea,
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
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RedrawDspCompTransferCurve", caught);
            }
        }

        /// <summary>压缩器页两块画布一起重画（切肤 / 切页时用）。</summary>
        internal void RedrawDspCompCanvas()
        {
            RedrawDspCompTransferCurve();
            RedrawDspCompGrHistory();
        }

        /// <summary>
        /// 宿主底色偏暗 = 深色主题。判断方式与 EQ 曲线的 IsEqCurveDarkTheme 一致：
        /// 直接看宿主 Border 的 Background 亮度，不依赖 Application.RequestedTheme
        /// （客户区可以单独指定 RequestedTheme，全局主题未必等于实际渲染色）。
        /// </summary>
        private bool IsDspCompHostDark(Border? host)
        {
            try
            {
                if (host?.Background is SolidColorBrush sb)
                {
                    double lum = (0.299 * sb.Color.R + 0.587 * sb.Color.G + 0.114 * sb.Color.B) / 255.0;
                    return lum < 0.5;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.IsDspCompHostDark", caught);
            }

            return true;
        }
    }
}
