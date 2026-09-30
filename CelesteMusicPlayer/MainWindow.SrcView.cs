// MainWindow.SrcView.cs
// SRC 页的可视化：用法卡片 / 信号链路图 / 重采样滤波器示意曲线。
//
// ⚠ 诚实性红线：这张滤波器曲线是「示意图」，不是实测。
//   真实响应由 WDL 重采样器按质量档位生成的具体系数决定，本项目没有把系数导出来画真曲线的通道。
//   所以：① 卡片标题就写「示意图」 ② 图下方用文字明确说清它不实测 ③ 不标任何具体 dB 数值当真值。
//   只画「通带保持平坦 → 过渡带快速掉下去」这个所有重采样滤波器共有的形状，
//   以及质量档位越高过渡带越窄这个定性关系。
//
// 参数真值仍在 AppSettingsStore（MainWindow.Src.cs 负责读写），本文件只读不写。

using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>示意曲线：各质量档位对应的过渡带起点（相对 Nyquist 的比例）。
        /// 只是定性关系 —— 档位越高过渡带越窄，不是实测值。</summary>
        private static readonly (string Key, double KneeStart)[] SrcFilterKnees =
        {
            (ResamplingSourceProvider.QualityLowLatency, 0.78),
            (ResamplingSourceProvider.QualityBalanced, 0.88),
            (ResamplingSourceProvider.QualityTransparent, 0.94),
        };

        private void SrcChainCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawSrcChain();

        private void SrcFilterCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawSrcFilterCurve();

        internal void RedrawSrcCanvas()
        {
            RedrawSrcChain();
            RedrawSrcFilterCurve();
        }

        // ─────────────────────────────────────────────────────────────
        // 用法卡片
        // ─────────────────────────────────────────────────────────────

        private void SrcModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag || SrcRateCombo == null)
            {
                return;
            }

            try
            {
                int targetIndex;
                string note;

                switch (tag)
                {
                    case "off":
                        targetIndex = 0;
                        note = "已设为「关闭」：原采样率直出，一个字节都不改。";
                        break;

                    case "fixed96":
                        targetIndex = IndexOfHz(96000);
                        note = "已设为 96 kHz。注意只升不降：源本身高于 96k 时不会降下来，会直接按原采样率走。";
                        break;

                    case "integer":
                        // 整数倍要先知道源采样率 —— 源率只有在播放会话里才拿得到
                        bool parsed = TryParseSrcRates(out int srcHz, out _);
                        if (!parsed || srcHz <= 0)
                        {
                            if (SrcModeNote != null)
                            {
                                SrcModeNote.Text = "现在拿不到源采样率（要播一首歌才知道）。先播一首，再点这张卡片。";
                            }

                            return;
                        }

                        int want = srcHz * 2;
                        targetIndex = IndexOfHz(want);
                        if (targetIndex <= 0)
                        {
                            // 没有精确的 2 倍档位（比如源是 96k），退到比源高的最低一档
                            targetIndex = 0;
                            for (int i = 1; i < SrcRateOptions.Length; i++)
                            {
                                if (SrcRateOptions[i].Hz > srcHz)
                                {
                                    targetIndex = i;
                                    break;
                                }
                            }

                            note = targetIndex <= 0
                                ? $"源是 {srcHz / 1000.0:0.#} kHz，已经没有更高的档位可升了，保持原采样率。"
                                : $"列表里没有正好两倍的档位，退到高一档 {SrcRateOptions[targetIndex].Label}。";
                        }
                        else
                        {
                            note = $"源是 {srcHz / 1000.0:0.#} kHz，已设为正好两倍的 {SrcRateOptions[targetIndex].Label}。";
                        }

                        break;

                    default:
                        return;
                }

                if (SrcRateCombo.SelectedIndex != targetIndex)
                {
                    SrcRateCombo.SelectedIndex = targetIndex;
                }
                else
                {
                    // 没有变化就不会触发 SelectionChanged，这里手动补一次刷新
                    RefreshSrcSessionState();
                    RedrawSrcCanvas();
                }

                if (SrcModeNote != null)
                {
                    SrcModeNote.Text = note;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.SrcModeButton_Click", caught);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 链路图
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 画「音源 → 重采样 → 输出」三个方框。
        /// 源率只能从引擎的会话描述文字里解析（HiFiOutputBackend 没有把源/目标作为结构化字段暴露），
        /// 拿不到就显示「未知」并用会话文字兜底，不编一个数字填上去。
        /// </summary>
        private void RedrawSrcChain()
        {
            Canvas? canvas = SrcChainCanvas;
            if (canvas == null || canvas.ActualWidth <= 2 || canvas.ActualHeight <= 2)
            {
                return;
            }

            try
            {
                double w = canvas.ActualWidth;
                double h = canvas.ActualHeight;
                double boxH = 54;
                double boxW = Math.Clamp((w - 80) / 3.0, 90, 210);
                double y = (h - boxH) / 2.0 - 6;
                double gap = (w - 3 * boxW) / 4.0;

                bool dark = IsDspCanvasHostDark(SrcChainHost);
                Color labelColor = dark ? Color.FromArgb(178, 255, 255, 255) : Color.FromArgb(150, 30, 30, 30);
                Color faint = dark ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(105, 30, 30, 30);
                Color fill = dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0);
                Color edge = dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0);
                Color accent = dark ? Color.FromArgb(255, 0x2e, 0x71, 0x68) : Color.FromArgb(255, 0x0f, 0x76, 0x6e);
                if (GeekDspAccentColor() is Color ph)
                {
                    accent = ph;
                }

                int targetHz = SrcRateCombo != null && SrcRateCombo.SelectedIndex > 0
                    && SrcRateCombo.SelectedIndex < SrcRateOptions.Length
                    ? SrcRateOptions[SrcRateCombo.SelectedIndex].Hz
                    : 0;

                bool parsed = TryParseSrcRates(out int srcHz, out int actualOutHz);
                string srcLabel = parsed && srcHz > 0 ? (srcHz / 1000.0).ToString("0.#") + " kHz" : "未知";
                string outLabel = targetHz > 0
                    ? (targetHz / 1000.0).ToString("0.#") + " kHz"
                    : (parsed && actualOutHz > 0 ? (actualOutHz / 1000.0).ToString("0.#") + " kHz" : "原采样率");

                string quality = SrcQualityCombo != null && SrcQualityCombo.SelectedIndex >= 0
                    ? SrcQualityCombo.SelectedValue as string ?? SrcQualityCombo.SelectedItem as string ?? "均衡"
                    : "均衡";

                var children = canvas.Children;
                children.Clear();

                string[] titles = { "音源", "重采样", "输出" };
                string[] values = { srcLabel, targetHz > 0 ? "WDL · " + quality : "不升频", outLabel };

                for (int i = 0; i < 3; i++)
                {
                    double x = gap + i * (boxW + gap);

                    children.Add(new Shapes.Rectangle
                    {
                        Width = boxW,
                        Height = boxH,
                        RadiusX = 8,
                        RadiusY = 8,
                        Fill = new SolidColorBrush(fill),
                        Stroke = new SolidColorBrush(i == 1 && targetHz > 0 ? accent : edge),
                        StrokeThickness = i == 1 && targetHz > 0 ? 1.8 : 1
                    });
                    Canvas.SetLeft(children[^1], x);
                    Canvas.SetTop(children[^1], y);

                    children.Add(MakeSrcLabel(titles[i], x + 8, y + 6, boxW - 16, TextAlignment.Center, faint, 10));
                    children.Add(MakeSrcLabel(values[i], x + 6, y + 24, boxW - 12, TextAlignment.Center,
                        i == 1 && targetHz > 0 ? accent : labelColor, 13));

                    if (i < 2)
                    {
                        double ax = x + boxW + 2;
                        double ay = y + boxH / 2.0;
                        children.Add(new Shapes.Line
                        {
                            X1 = ax, Y1 = ay, X2 = ax + gap - 4, Y2 = ay,
                            Stroke = new SolidColorBrush(edge),
                            StrokeThickness = 1.4
                        });
                        children.Add(MakeSrcLabel("▸", ax + gap - 12, ay - 9, 12, TextAlignment.Center, faint, 11));
                    }
                }

                if (SrcChainNote != null)
                {
                    string desc = _audioEngine?.SrcStateDescription ?? string.Empty;
                    SrcChainNote.Text = string.IsNullOrEmpty(desc)
                        ? "还没播放过，等播一首歌这里会显示真实走了哪条路。"
                        : "当前会话：" + desc;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RedrawSrcChain", caught);
            }
        }

        /// <summary>
        /// 从引擎的会话描述里解析源/目标采样率（形如 "44.1k→96k（balanced）"）。
        /// 引擎没把这两个值做成结构化字段，只能这么取；解析不到就返回 false，调用方显示"未知"。
        /// </summary>
        private bool TryParseSrcRates(out int srcHz, out int outHz)
        {
            srcHz = 0;
            outHz = 0;

            try
            {
                string desc = _audioEngine?.SrcStateDescription ?? string.Empty;
                Match m = Regex.Match(desc, @"(\d+(?:[.,]\d+)?)k\s*→\s*(\d+(?:[.,]\d+)?)k");
                if (!m.Success)
                {
                    return false;
                }

                double a = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                double b = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                srcHz = (int)Math.Round(a * 1000);
                outHz = (int)Math.Round(b * 1000);
                return srcHz > 0 && outHz > 0;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.TryParseSrcRates", caught);
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 滤波器示意曲线
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 画重采样滤波器的形状示意：通带平坦 → 过渡带快速掉到很低 → 之后保持很低。
        /// 横轴是「相对目标 Nyquist 的比例」（0..1.4，超过 1 的部分是镜像/混叠区），
        /// 纵轴只标「0 dB」和「很低」两个定性层级 —— 不标具体 dB，避免被当成真值。
        /// </summary>
        private void RedrawSrcFilterCurve()
        {
            Canvas? canvas = SrcFilterCanvas;
            if (canvas == null || canvas.ActualWidth <= 2 || canvas.ActualHeight <= 2)
            {
                return;
            }

            try
            {
                double w = canvas.ActualWidth;
                double h = canvas.ActualHeight;
                double padL = 40, padR = 16, padT = 14, padB = 26;
                double plotW = w - padL - padR;
                double plotH = h - padT - padB;
                if (plotW <= 2 || plotH <= 2)
                {
                    return;
                }

                const double fMax = 1.4; // 画到 Nyquist 的 1.4 倍，把镜像区露出来
                double X(double r) => padL + Math.Clamp(r / fMax, 0.0, 1.0) * plotW;
                double Y(double level) => padT + (1.0 - Math.Clamp(level, 0.0, 1.0)) * plotH; // level: 1=0dB, 0=底

                bool dark = IsDspCanvasHostDark(SrcFilterHost);
                Color gridWeak = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
                Color gridStrong = dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(74, 0, 0, 0);
                Color labelColor = dark ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(145, 30, 30, 30);
                Color accent = dark ? Color.FromArgb(255, 0x2e, 0x71, 0x68) : Color.FromArgb(255, 0x0f, 0x76, 0x6e);
                Color caution = dark ? Color.FromArgb(180, 0xd9, 0x8c, 0x0b) : Color.FromArgb(180, 0xb4, 0x53, 0x09);
                if (GeekDspAccentColor() is Color ph)
                {
                    accent = ph;
                }

                double knee = 0.88;
                if (SrcQualityCombo != null && SrcQualityCombo.SelectedIndex >= 0)
                {
                    string key = SelectedKey(SrcQualityCombo, SrcQualityOptions, ResamplingSourceProvider.QualityBalanced);
                    foreach (var (k, start) in SrcFilterKnees)
                    {
                        if (string.Equals(k, key, StringComparison.Ordinal))
                        {
                            knee = start;
                            break;
                        }
                    }
                }

                var children = canvas.Children;
                children.Clear();

                // 纵向刻度只给三条定性线：0 dB / 中间 / 底
                children.Add(new Shapes.Line
                {
                    X1 = padL, Y1 = Y(1.0), X2 = padL + plotW, Y2 = Y(1.0),
                    Stroke = new SolidColorBrush(gridStrong), StrokeThickness = 1.4
                });
                children.Add(new Shapes.Line
                {
                    X1 = padL, Y1 = Y(0.5), X2 = padL + plotW, Y2 = Y(0.5),
                    Stroke = new SolidColorBrush(gridWeak), StrokeThickness = 1
                });
                children.Add(new Shapes.Line
                {
                    X1 = padL, Y1 = Y(0.0), X2 = padL + plotW, Y2 = Y(0.0),
                    Stroke = new SolidColorBrush(gridWeak), StrokeThickness = 1
                });
                children.Add(MakeSrcLabel("0 dB", 0, Y(1.0) - 7, 36, TextAlignment.Right, labelColor, 10));
                children.Add(MakeSrcLabel("很低", 0, Y(0.0) - 7, 36, TextAlignment.Right, labelColor, 10));

                // Nyquist 竖线
                children.Add(new Shapes.Line
                {
                    X1 = X(1.0), Y1 = padT, X2 = X(1.0), Y2 = padT + plotH,
                    Stroke = new SolidColorBrush(caution),
                    StrokeThickness = 1.4,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                });
                children.Add(MakeSrcLabel("Nyquist", X(1.0) - 30, padT + plotH + 6, 68, TextAlignment.Center, caution, 10));
                children.Add(MakeSrcLabel("0", padL - 8, padT + plotH + 6, 24, TextAlignment.Center, labelColor, 10));

                // 通带区域淡色块
                var pass = new Shapes.Rectangle
                {
                    Width = X(knee) - padL,
                    Height = plotH,
                    Fill = new SolidColorBrush(Color.FromArgb((byte)(dark ? 20 : 16), accent.R, accent.G, accent.B))
                };
                Canvas.SetLeft(pass, padL);
                Canvas.SetTop(pass, padT);
                children.Add(pass);
                children.Add(MakeSrcLabel("通带", padL + 6, padT + 4, 40, TextAlignment.Left, labelColor, 10));

                // 曲线本体
                var pts = new PointCollection();
                int samples = 200;
                for (int i = 0; i < samples; i++)
                {
                    double r = fMax * i / (samples - 1);
                    double level;
                    if (r <= knee)
                    {
                        level = 1.0;
                    }
                    else if (r >= 1.0 + (1.0 - knee) * 0.6)
                    {
                        level = 0.0;
                    }
                    else
                    {
                        double t = (r - knee) / ((1.0 + (1.0 - knee) * 0.6) - knee);
                        level = 1.0 - (t * t * (3.0 - 2.0 * t)); // smoothstep 下去
                    }

                    pts.Add(new Windows.Foundation.Point(X(r), Y(level)));
                }

                var area = new PointCollection();
                area.Add(new Windows.Foundation.Point(padL, Y(0.0)));
                foreach (Windows.Foundation.Point p in pts)
                {
                    area.Add(p);
                }

                area.Add(new Windows.Foundation.Point(padL + plotW, Y(0.0)));
                children.Add(new Shapes.Polygon
                {
                    Points = area,
                    Fill = new SolidColorBrush(Color.FromArgb((byte)(dark ? 40 : 34), accent.R, accent.G, accent.B))
                });
                children.Add(new Shapes.Polyline
                {
                    Points = pts,
                    Stroke = new SolidColorBrush(accent),
                    StrokeThickness = 2.2,
                    StrokeLineJoin = PenLineJoin.Round
                });

                // 图内再压一句，防止用户只看图不看下面的说明
                children.Add(MakeSrcLabel(
                    "示意图 · 非实测", padL + plotW - 110, padT + 2, 110, TextAlignment.Right, caution, 10));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RedrawSrcFilterCurve", caught);
            }
        }

        private static UIElement MakeSrcLabel(string text, double x, double y, double width, TextAlignment align, Color color, double fontSize)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
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
