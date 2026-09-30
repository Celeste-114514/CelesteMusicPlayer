// MainWindow.DspField.cs
// 立体声场页的 M/S 声像圆环：可拖动 + 中间/两侧实际增益读数 + 相关度估算。
//
// 内核口径（native/dsp-core/audio-engine/SpatialDspProcessor.cpp:62-79）：
//   mid  = 0.5*(L+R) * 10^(centerDb/20)
//   side = 0.5*(L-R) * 10^(sideDb/20) * width
//   L' = mid + side , R' = mid - side
// 所以「两侧的实际增益」= sideDb + 20*log10(width) —— 宽度是乘在非 dB 之前的系数，
// 不是又一条 dB。这就是本页读数要单独算出来给用户看的原因：滑杆上的 −18..+18 dB
// 并不是两侧最终拿到的量，width 会再乘一道。
//
// 职责边界：参数真值只有一份（DspRackStore），加载/下发/预设在 MainWindow.DspRack.cs，
// 本文件只做「读滑杆 → 画出来 / 把拖动翻译成滑杆值」。
// 面板就绪守卫沿用 _dspRackReady（XAML 初值会在解析期就地触发事件，不挡就进程秒崩）。

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
        /// <summary>
        /// 圆环拖动期间置 true：滑杆的 ValueChanged 会一路触发到 PushDspRackToEngine
        /// （写 dsp-rack.json + 下发内核），拖一次要写几百次盘。拖动期间由本标志压住，
        /// 松手（PointerReleased）时统一补一次下发。
        /// </summary>
        private bool _dspFieldSuppressPush;

        private bool _dspFieldDragging;

        /// <summary>拖动杆的垂直分量能影响的中间/两侧偏向范围（dB）。刻意留±6，避免拖一把就极端。</summary>
        private const double FieldDragTiltDb = 6.0;

        /// <summary>
        /// 估算输出相关度时的「输入左右相关度」假设值。
        /// 真实值取决于当前这首歌，只能取一个常见混音的代表值，UI 上如实写明是估算。
        /// </summary>
        private const double FieldAssumedInputCorrelation = 0.5;

        private void DspFieldRingCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_dspRackReady)
            {
                return;
            }

            RedrawDspFieldRing();
        }

        internal void RedrawDspFieldCanvas()
        {
            RedrawDspFieldRing();
        }

        // ─────────────────────────────────────────────────────────────
        // 拖动交互
        // ─────────────────────────────────────────────────────────────

        private void DspFieldRing_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_dspRackReady || DspFieldRingCanvas == null)
            {
                return;
            }

            try
            {
                _dspFieldDragging = true;
                _dspFieldSuppressPush = true;
                DspFieldRingCanvas.CapturePointer(e.Pointer);
                ApplyDspFieldDrag(e.GetCurrentPoint(DspFieldRingCanvas).Position);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.DspFieldRing_PointerPressed", caught);
            }
        }

        private void DspFieldRing_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dspFieldDragging || DspFieldRingCanvas == null)
            {
                return;
            }

            try
            {
                ApplyDspFieldDrag(e.GetCurrentPoint(DspFieldRingCanvas).Position);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.DspFieldRing_PointerMoved", caught);
            }
        }

        private void DspFieldRing_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dspFieldDragging)
            {
                return;
            }

            _dspFieldDragging = false;
            _dspFieldSuppressPush = false;

            try
            {
                DspFieldRingCanvas?.ReleasePointerCapture(e.Pointer);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.DspFieldRing_PointerReleased.Capture", caught);
            }

            // 松手才真正落盘 + 下发：拖动过程中已经画好了，这里补最后一次
            PushDspRackToEngine();
        }

        /// <summary>
        /// 把指针位置翻译成三个参数：横向 → 宽度，纵向 → 中间/两侧的偏向。
        /// 圆环本身容许上下溢出，位置先按半径归一化再 clamp，指针跑到外面也不会出怪值。
        /// </summary>
        private void ApplyDspFieldDrag(Windows.Foundation.Point pt)
        {
            var (cx, cy, r) = GetDspFieldRingGeometry();
            if (r <= 1)
            {
                return;
            }

            double nx = Math.Clamp((pt.X - cx) / r, -1.0, 1.0);
            double ny = Math.Clamp((cy - pt.Y) / r, -1.0, 1.0); // 向上为正

            double width = Math.Clamp((nx + 1.0) * 100.0, 0.0, 200.0);
            double tilt = ny * FieldDragTiltDb;

            if (DspFieldWidthSlider != null)
            {
                DspFieldWidthSlider.Value = width;
            }

            if (DspFieldCenterSlider != null)
            {
                DspFieldCenterSlider.Value = Math.Clamp(tilt, DspFieldCenterSlider.Minimum, DspFieldCenterSlider.Maximum);
            }

            if (DspFieldSideSlider != null)
            {
                DspFieldSideSlider.Value = Math.Clamp(-tilt, DspFieldSideSlider.Minimum, DspFieldSideSlider.Maximum);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 绘制 + 读数
        // ─────────────────────────────────────────────────────────────

        private void RedrawDspFieldRing()
        {
            Canvas? canvas = DspFieldRingCanvas;
            if (canvas == null || canvas.ActualWidth <= 2 || canvas.ActualHeight <= 2)
            {
                return;
            }

            if (DspFieldWidthSlider == null || DspFieldSideSlider == null)
            {
                return;
            }

            try
            {
                var (cx, cy, r) = GetDspFieldRingGeometry();
                if (r <= 8)
                {
                    return;
                }

                bool enabled = DspFieldToggle != null && DspFieldToggle.IsOn;
                double width = DspFieldWidthSlider.Value;
                double centerDb = DspFieldCenterSlider?.Value ?? 0.0;
                double sideDb = DspFieldSideSlider.Value;

                bool dark = IsDspCanvasHostDark(DspFieldRingHost);
                Color grid = dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(34, 0, 0, 0);
                Color labelColor = dark ? Color.FromArgb(165, 255, 255, 255) : Color.FromArgb(140, 30, 30, 30);
                Color midColor = dark ? Color.FromArgb(255, 0x4a, 0xc0, 0x8a) : Color.FromArgb(255, 0x18, 0x8a, 0x5c);
                Color sideColor = dark ? Color.FromArgb(255, 0x8a, 0x62, 0xd8) : Color.FromArgb(255, 0x6d, 0x28, 0xd9);
                Color accent = midColor;

                if (enabled && GeekDspAccentColor() is Color ph)
                {
                    accent = ph;
                    midColor = ph;
                }

                if (!enabled)
                {
                    grid = Color.FromArgb((byte)(dark ? 30 : 26), 130, 130, 130);
                    labelColor = Color.FromArgb((byte)(dark ? 110 : 100), 140, 140, 140);
                    midColor = Color.FromArgb((byte)(dark ? 90 : 80), 150, 150, 150);
                    sideColor = Color.FromArgb((byte)(dark ? 90 : 80), 150, 150, 150);
                    accent = Color.FromArgb((byte)(dark ? 140 : 120), 150, 150, 150);
                }

                var children = canvas.Children;
                children.Clear();

                // 外圈
                children.Add(new Shapes.Ellipse
                {
                    Width = 2 * r,
                    Height = 2 * r,
                    Stroke = new SolidColorBrush(grid),
                    StrokeThickness = 1.2
                });
                Canvas.SetLeft(children[^1], cx - r);
                Canvas.SetTop(children[^1], cy - r);

                // 十字轴
                children.Add(new Shapes.Line
                {
                    X1 = cx - r, Y1 = cy, X2 = cx + r, Y2 = cy,
                    Stroke = new SolidColorBrush(grid), StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 }
                });
                children.Add(new Shapes.Line
                {
                    X1 = cx, Y1 = cy - r, X2 = cx, Y2 = cy + r,
                    Stroke = new SolidColorBrush(grid), StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 }
                });

                if (enabled)
                {
                    // 上半环线粗 = 中间增益；下半环线粗 = 两侧实际增益。
                    // 用 Clip 把一个整圆劈成上下两半，各自的描边粗细由增益决定。
                    children.Add(MakeFieldHalfRing(cx, cy, r, midColor, DbToRingThickness(centerDb), upper: true));
                    children.Add(MakeFieldHalfRing(cx, cy, r, sideColor, DbToRingThickness(EffectiveSideDb(sideDb, width)), upper: false));
                }

                // 方位标签
                children.Add(MakeFieldTag("中间", cx - 18, cy - r - 16, 36, labelColor));
                children.Add(MakeFieldTag("两侧", cx - 18, cy + r + 3, 36, labelColor));
                children.Add(MakeFieldTag("窄", cx - r - 26, cy - 7, 26, labelColor));
                children.Add(MakeFieldTag("宽", cx + r + 2, cy - 7, 26, labelColor));

                // 手柄：横向位置 ← 宽度，纵向位置 ← 中间/两侧偏向
                double nx = width / 100.0 - 1.0;
                double tiltDb = (centerDb - sideDb) / 2.0;
                double ny = Math.Clamp(tiltDb / FieldDragTiltDb, -1.0, 1.0);
                double hx = cx + nx * r;
                double hy = cy - ny * r;

                children.Add(new Shapes.Line
                {
                    X1 = cx, Y1 = cy, X2 = hx, Y2 = hy,
                    Stroke = new SolidColorBrush(accent),
                    StrokeThickness = 1.4,
                    Opacity = 0.6
                });
                children.Add(new Shapes.Ellipse
                {
                    Width = 18,
                    Height = 18,
                    Fill = new SolidColorBrush(accent),
                    Stroke = new SolidColorBrush(dark ? Color.FromArgb(200, 0, 0, 0) : Color.FromArgb(200, 255, 255, 255)),
                    StrokeThickness = 2
                });
                Canvas.SetLeft(children[^1], hx - 9);
                Canvas.SetTop(children[^1], hy - 9);

                UpdateDspFieldReadouts(enabled, width, centerDb, sideDb);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RedrawDspFieldRing", caught);
            }
        }

        /// <summary>半环（clip 到上半或下半），线粗表示这一路的强弱。</summary>
        private static UIElement MakeFieldHalfRing(double cx, double cy, double r, Color color, double thickness, bool upper)
        {
            var ring = new Shapes.Ellipse
            {
                Width = 2 * r,
                Height = 2 * r,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = Math.Max(0.6, thickness),
                Opacity = 0.9,
                Clip = new RectangleGeometry
                {
                    Rect = new Windows.Foundation.Rect(0, upper ? 0 : r, 2 * r, r)
                }
            };
            Canvas.SetLeft(ring, cx - r);
            Canvas.SetTop(ring, cy - r);
            return ring;
        }

        private static UIElement MakeFieldTag(string text, double x, double y, double width, Color color)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new SolidColorBrush(color),
                Width = width,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            return tb;
        }

        /// <summary>把 dB 增益映射成环线粗细（±6dB 覆盖 1..7px 的可辨范围）。</summary>
        private static double DbToRingThickness(double db)
        {
            double linear = Math.Pow(10.0, db / 20.0);
            return Math.Clamp(linear * 3.0, 0.6, 9.0);
        }

        /// <summary>
        /// 两侧的实际增益：sideDb 再乘上宽度。width=0 时没有任何侧信号能过去，返回负无穷。
        /// 这是内核 SpatialDspProcessor 里 sideGain = dbToGain(sideDb) * width 的直接换算。
        /// </summary>
        private static double EffectiveSideDb(double sideDb, double widthPercent)
        {
            if (widthPercent <= 0.0001)
            {
                return double.NegativeInfinity;
            }

            return sideDb + 20.0 * Math.Log10(widthPercent / 100.0);
        }

        private void UpdateDspFieldReadouts(bool enabled, double width, double centerDb, double sideDb)
        {
            if (DspFieldMidReadout != null)
            {
                DspFieldMidReadout.Text = enabled ? FormatSignedDb(centerDb) : "旁路";
            }

            double effSide = EffectiveSideDb(sideDb, width);
            if (DspFieldSideReadout != null)
            {
                DspFieldSideReadout.Text = !enabled
                    ? "旁路"
                    : double.IsNegativeInfinity(effSide) ? "−∞ dB" : FormatSignedDb(effSide);
            }

            if (DspFieldCorrText == null)
            {
                return;
            }

            if (!enabled)
            {
                DspFieldCorrText.Text = "模块没启用，左右原样送出。";
                return;
            }

            double gm = Math.Pow(10.0, centerDb / 20.0);
            double gs = double.IsNegativeInfinity(effSide) ? 0.0 : Math.Pow(10.0, effSide / 20.0);
            double rho = FieldAssumedInputCorrelation;

            // 等功率输入下，M/S 处理后输出相关度的解析式：
            //   ρ_out = (gm²(1+ρ) − gs²(1−ρ)) / (gm²(1+ρ) + gs²(1−ρ))
            // （交叉项 E[mid·side] 在等功率假设下为 0，所以式子只有两项）
            double a = gm * gm * (1.0 + rho);
            double b = gs * gs * (1.0 - rho);
            double denom = a + b;
            double rhoOut = denom > 1e-12 ? (a - b) / denom : 0.0;

            string verdict = rhoOut > 0.85
                ? "快合成单声道了，声场基本没了。"
                : rhoOut < -0.3
                    ? "左右已经反过来，和原曲的空间感是拧着的。"
                    : "空间感正常范围内。";

            DspFieldCorrText.Text = $"估算输出左右相关 {rhoOut:0.00}"
                + $"（假设输入相关 {rho:0.0}），{verdict}";
        }

        private static string FormatSignedDb(double db)
            => (db >= 0 ? "+" : "") + db.ToString("0.0") + " dB";

        /// <summary>圆环几何：圆心 + 半径（画布较短边的 40% 上下，留出标签空间）。</summary>
        private (double cx, double cy, double r) GetDspFieldRingGeometry()
        {
            Canvas? canvas = DspFieldRingCanvas;
            double w = canvas?.ActualWidth ?? 0;
            double h = canvas?.ActualHeight ?? 0;

            double cx = w / 2.0;
            double cy = h / 2.0;
            double r = Math.Min(w, h) / 2.0 - 20.0;
            return (cx, cy, Math.Max(0.0, r));
        }
    }
}
