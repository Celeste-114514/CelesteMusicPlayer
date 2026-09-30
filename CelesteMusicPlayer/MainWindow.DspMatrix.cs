// MainWindow.DspMatrix.cs
// 声道矩阵页的 2×2 信号流图：把四个混合系数画成「输入 L/R → 输出 L/R」的四条通路。
//
// 职责边界（同 MainWindow.DspComp.cs 的约定）：
//   · 参数真值只有一份，在 DspRackStore；加载/下发/预设都在 MainWindow.DspRack.cs，
//     本文件只负责「当场读滑杆 → 画出来」，不持有第二份镜像。
//   · 面板就绪守卫沿用 DspRack.cs 的 _dspRackReady —— XAML 滑杆的 Value 初值会在
//     InitializeComponent 解析阶段就地触发 ValueChanged，不挡就是进程秒崩（0xC000027B）。
//
// 为什么必须有这张图：四个系数两两交叉，用户在滑杆上很难建立「哪一路 feeds 哪一路」的直觉，
// 尤其是「左 → 右」这种对角项。图把这件事变成看得见的东西。

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>节点圆点直径。</summary>
        private const double MatrixNodeDiameter = 34.0;

        /// <summary>两条对角弧在中点的分离距离（px）。不分开的话会重成一条线。</summary>
        private const double MatrixArcSpread = 26.0;

        private void DspMatrixFlowCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_dspRackReady)
            {
                return;
            }

            RedrawDspMatrixFlow();
        }

        /// <summary>矩阵页画布重画（切肤 / 切页时用）。</summary>
        internal void RedrawDspMatrixCanvas()
        {
            RedrawDspMatrixFlow();
        }

        /// <summary>
        /// 画 2×2 信号流图。四条通路各有自己的语义：
        ///   左→左（同源直通）、右→右（同源直通）、左→右（混进对侧）、右→左（混进对侧）
        /// 线宽 ∝ |系数|；负系数用另一种颜色 + 虚线 + × 标记（表示这一路是反相叠加）；
        /// 系数为 0 的通路不画线（本来就没有信号流过去，画出来反而干扰）。
        /// </summary>
        internal void RedrawDspMatrixFlow()
        {
            Canvas? canvas = DspMatrixFlowCanvas;
            if (canvas == null || canvas.ActualWidth <= 2 || canvas.ActualHeight <= 2)
            {
                return;
            }

            // 面板未构建好时四个滑杆字段还可能全是 null（解析期提前到达）
            if (DspMatrixLlSlider == null || DspMatrixRrSlider == null)
            {
                return;
            }

            try
            {
                double w = canvas.ActualWidth;
                double h = canvas.ActualHeight;
                double r = MatrixNodeDiameter / 2.0;
                double padL = 58, padR = 58, padT = 28, padB = 24;
                double xL = padL + r;
                double xR = w - padR - r;
                double yTop = padT + r;
                double yBot = h - padB - r;
                if (xR - xL < 80 || yBot - yTop < 40)
                {
                    return;
                }

                bool enabled = DspMatrixToggle != null && DspMatrixToggle.IsOn;

                // 未启用 = 内核按直通处理（L→L=1, R→R=1），图上照这个样子画，但不骗用户："这是旁路的样子"
                double ll = enabled ? DspMatrixLlSlider.Value : 1.0;
                double rl = enabled ? DspMatrixRlSlider.Value : 0.0;
                double lr = enabled ? DspMatrixLrSlider.Value : 0.0;
                double rr = enabled ? DspMatrixRrSlider.Value : 1.0;

                bool dark = IsDspCanvasHostDark(DspMatrixFlowHost);
                Color nodeFill = enabled
                    ? (dark ? Color.FromArgb(255, 0x2e, 0x71, 0x68) : Color.FromArgb(255, 0x0f, 0x76, 0x6e))
                    : (dark ? Color.FromArgb(120, 120, 120, 120) : Color.FromArgb(120, 150, 150, 150));
                Color positive = enabled
                    ? (dark ? Color.FromArgb(255, 0x4a, 0xc0, 0x8a) : Color.FromArgb(255, 0x18, 0x8a, 0x5c))
                    : (dark ? Color.FromArgb(150, 140, 140, 140) : Color.FromArgb(160, 130, 130, 130));
                // 负系数（反相）：暖色，和正向形成一眼能分辨的反差
                Color negative = enabled
                    ? (dark ? Color.FromArgb(255, 0xd9, 0x8c, 0x0b) : Color.FromArgb(255, 0xb4, 0x53, 0x09))
                    : (dark ? Color.FromArgb(150, 140, 140, 140) : Color.FromArgb(160, 130, 130, 130));
                Color labelColor = dark ? Color.FromArgb(175, 255, 255, 255) : Color.FromArgb(150, 30, 30, 30);

                // 极客皮肤：Canvas 上的 Shape 读不到 ThemeResource，必须显式换成磷光色
                if (enabled && GeekDspAccentColor() is Color ph)
                {
                    positive = ph;
                    nodeFill = ph;
                }

                var children = canvas.Children;
                children.Clear();

                // 列标题
                children.Add(MakeCanvasLabel("输入", xL - 20, 6, 40, true, labelColor, 11));
                children.Add(MakeCanvasLabel("输出", xR - 20, 6, 40, true, labelColor, 11));

                double midX = (xL + r + xR - r) / 2.0;
                double midY = (yTop + yBot) / 2.0;

                // ① 左 → 左（上水平）
                DrawMatrixEdge(children, xL + r, yTop, xR - r, yTop, midX, yTop, midY, false,
                    ll, positive, negative, labelColor);

                // ② 右 → 右（下水平）
                DrawMatrixEdge(children, xL + r, yBot, xR - r, yBot, midX, yBot, midY, false,
                    rr, positive, negative, labelColor);

                // ③ 左 → 右（左上 → 右下，向上凸的弧，与另一条对角线错开）
                DrawMatrixEdge(children, xL + r, yTop, xR - r, yBot, midX, midY - MatrixArcSpread, midY, true,
                    lr, positive, negative, labelColor);

                // ④ 右 → 左（左下 → 右上，向下凸的弧）
                DrawMatrixEdge(children, xL + r, yBot, xR - r, yTop, midX, midY + MatrixArcSpread, midY, true,
                    rl, positive, negative, labelColor);

                // 四个节点 + 声道标识
                children.Add(MakeMatrixNode(xL, yTop, nodeFill, "L", dark));
                children.Add(MakeMatrixNode(xL, yBot, nodeFill, "R", dark));
                children.Add(MakeMatrixNode(xR, yTop, nodeFill, "L", dark));
                children.Add(MakeMatrixNode(xR, yBot, nodeFill, "R", dark));

                UpdateDspMatrixPhaseNote(enabled, ll, rl, lr, rr);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RedrawDspMatrixFlow", caught);
            }
        }

        /// <summary>
        /// 画一条通路。isArc=true 时走二次贝塞尔（两条对角线 :: this isolates them visually），
        /// ctrlY 是中段的控制点纵坐标。
        /// </summary>
        private void DrawMatrixEdge(
            UIElementCollection children,
            double x1, double y1, double x2, double y2,
            double ctrlX, double ctrlY, double rowMidY, bool isArc,
            double coeff, Color positive, Color negative, Color labelColor)
        {
            double abs = Math.Abs(coeff);
            if (abs < 0.001)
            {
                return; // 没有信号流过去，不画
            }

            bool negativePhase = coeff < 0;
            Color strokeColor = negativePhase ? negative : positive;
            double thickness = Math.Clamp(abs * 3.2, 0.9, 7.0);

            var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(x1, y1) };
            if (isArc)
            {
                fig.Segments.Add(new QuadraticBezierSegment
                {
                    Point1 = new Windows.Foundation.Point(ctrlX, ctrlY),
                    Point2 = new Windows.Foundation.Point(x2, y2)
                });
            }
            else
            {
                fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x2, y2) });
            }

            var geo = new PathGeometry();
            geo.Figures.Add(fig);

            var path = new Shapes.Path
            {
                Data = geo,
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };

            if (negativePhase)
            {
                path.StrokeDashArray = new DoubleCollection { 5, 3 };
            }

            children.Add(path);

            // 标签落在 t=0.5 处：二次贝塞尔的 B(0.5) = 0.25*P0 + 0.5*C + 0.25*P2
            double lx = isArc ? 0.25 * x1 + 0.5 * ctrlX + 0.25 * x2 : (x1 + x2) / 2.0;
            double ly = isArc ? 0.25 * y1 + 0.5 * ctrlY + 0.25 * y2 : (y1 + y2) / 2.0;

            string dbText = FormatMatrixCoeffDb(coeff);
            string body = (negativePhase ? "× " : "") + coeff.ToString("0.00");

            children.Add(MakeCanvasLabel(body, lx - 34, ly - 20, 68, true, labelColor, 13));
            children.Add(MakeCanvasLabel(dbText, lx - 34, ly + 5, 68, true,
                negativePhase ? negative : labelColor, 10));
        }

        /// <summary>系数转 dB：|系数| 才是对振幅有意义的量，符号单独用颜色/虚线表达。</summary>
        private static string FormatMatrixCoeffDb(double coeff)
        {
            double abs = Math.Abs(coeff);
            if (abs < 0.001)
            {
                return "−∞ dB";
            }

            double db = 20.0 * Math.Log10(abs);
            return (db >= 0 ? "+" : "") + db.ToString("0.0") + " dB";
        }

        private static UIElement MakeMatrixNode(double cx, double cy, Color fill, string text, bool dark)
        {
            var panel = new Canvas
            {
                Width = MatrixNodeDiameter,
                Height = MatrixNodeDiameter
            };

            panel.Children.Add(new Shapes.Ellipse
            {
                Width = MatrixNodeDiameter,
                Height = MatrixNodeDiameter,
                Fill = new SolidColorBrush(fill)
            });

            panel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            Canvas.SetLeft(panel, cx - MatrixNodeDiameter / 2.0);
            Canvas.SetTop(panel, cy - MatrixNodeDiameter / 2.0);
            return panel;
        }

        /// <summary>画布上的一行文字。</summary>
        private static UIElement MakeCanvasLabel(string text, double x, double y, double width, bool center, Color color, double fontSize)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = new SolidColorBrush(color),
                Width = width,
                TextAlignment = center ? TextAlignment.Center : TextAlignment.Left
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            return tb;
        }

        /// <summary>
        /// 相位提示。两种情况值得提醒：某个输出声道上两路符号相反（互相抵消），
        /// 以及整体上存在反相混合（会改变空间感）。没启用时说清楚「现在是旁路」。
        /// </summary>
        private void UpdateDspMatrixPhaseNote(bool enabled, double ll, double rl, double lr, double rr)
        {
            if (DspMatrixPhaseText == null)
            {
                return;
            }

            if (!enabled)
            {
                DspMatrixPhaseText.Text = "矩阵没启用，声音原样通过（图上是旁路的样子）。";
                return;
            }

            bool leftConflict = Math.Abs(ll) > 0.001 && Math.Abs(rl) > 0.001 && Math.Sign(ll) != Math.Sign(rl);
            bool rightConflict = Math.Abs(lr) > 0.001 && Math.Abs(rr) > 0.001 && Math.Sign(lr) != Math.Sign(rr);

            var parts = new System.Text.StringBuilder();
            if (leftConflict || rightConflict)
            {
                string which = leftConflict && rightConflict ? "左右两个输出" : (leftConflict ? "左输出" : "右输出");
                parts.Append(which).Append("上两路是反着的，会互相抵消一部分，音量可能反而变小。");
            }
            else if (ll < 0 || rl < 0 || lr < 0 || rr < 0)
            {
                parts.Append("有反相混合（虚线那几路），空间感会变，不是所有耳机都好听。");
            }
            else
            {
                parts.Append("四路都是正向混合，没有相位冲突。");
            }

            // 总量提示：任何一路系数绝对值超过 1 都会抬高音量
            double maxAbs = Math.Max(Math.Max(Math.Abs(ll), Math.Abs(rl)), Math.Max(Math.Abs(lr), Math.Abs(rr)));
            if (maxAbs > 1.001)
            {
                parts.Append(" 有系数超过 1，总音量会被抬高，留意后面的输出安全页。");
            }

            DspMatrixPhaseText.Text = parts.ToString();
        }
    }
}
