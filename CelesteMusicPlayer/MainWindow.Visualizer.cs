using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放信息页的可视化元素：波形 / 频谱柱（方块段+峰值帽）/ 径向频谱（环绕黑胶唱盘）/
    /// 示波器（实时波形线）/ 李萨如（L-R 相位图，留给极客主题播放页）/ 不显示。
    /// 每个布局一份可用清单（见 <see cref="VisualOptionsForLayout"/>），菜单只列该布局能用的档位。
    /// 全部只读 DSP 链上的现成数据（FFT 频段 / 环形样本缓冲），播放字节流一行不动（bit-perfect）。
    /// 颜色统一取 <see cref="WaveColorFor"/>（=主题强调色），在明暗主题下都融洽。
    /// </summary>
    public sealed partial class MainWindow
    {
        internal const string VisualWaveform = "Waveform";
        internal const string VisualBars = "Bars";
        internal const string VisualRadial = "Radial";
        internal const string VisualScope = "Scope";
        internal const string VisualLissajous = "Lissajous";
        internal const string VisualNone = "None";

        /// <summary>径向柱子从唱盘边缘往外最长能伸多少（画布比唱盘大 128，单边 64，柱子 56+偏移 4 仍在画布内）。</summary>
        private const double RadialMaxBarLength = 56;

        /// <summary>当前可视化模式（已校验合法值）。</summary>
        private string _visualMode = VisualWaveform;

        /// <summary>波形槽当前实际渲染的模式（Canvas 子元素按它管理）。</summary>
        private string _slotRenderedMode = string.Empty;

        private const int ScopeSampleCount = 1024;
        private const int ScopePointCount = 512;
        private const int RadialBarCount = 64;

        private readonly float[] _scopeSamples = new float[ScopeSampleCount];
        private readonly float[] _scopeLeft = new float[ScopeSampleCount];
        private readonly float[] _scopeRight = new float[ScopeSampleCount];

        /// <summary>频谱柱模式的峰值帽（慢落）；下标对齐 _waveLevels。</summary>
        private readonly double[] _wavePeaks = new double[WaveBarCount];

        /// <summary>径向频谱的柱子（懒建，颜色/几何每帧刷新）。</summary>
        private readonly Border?[] _radialBars = new Border?[RadialBarCount];

        private Polyline? _scopeLine;

        /// <summary>径向频谱的内圈半径（= 唱盘半径 + 4）。由 UpdateVinylGeometry 按唱盘尺寸写入。</summary>
        private double _radialInnerRadius;

        internal static bool IsKnownVisual(string? visual)
            => visual == VisualWaveform || visual == VisualBars || visual == VisualRadial
               || visual == VisualScope || visual == VisualLissajous || visual == VisualNone;

        internal static string VisualDisplayName(string visual) => visual switch
        {
            VisualBars => "频谱柱",
            VisualRadial => "径向频谱",
            VisualScope => "示波器",
            VisualLissajous => "李萨如",
            VisualNone => "不显示",
            _ => "波形"
        };

        /// <summary>菜单里的完整说明（比按钮上的短名更清楚每档是什么）。</summary>
        private static string VisualMenuText(string visual) => visual switch
        {
            VisualBars => "频谱柱（方块段 + 峰值帽）",
            VisualRadial => "径向频谱（环绕唱盘）",
            VisualScope => "示波器（实时波形线）",
            VisualNone => "不显示可视化",
            _ => "波形（原有样子）"
        };

        /// <summary>该档位是否占用封面下方的波形槽（径向画在唱盘外圈，不占槽）。</summary>
        private static bool VisualUsesSlot(string visual)
            => visual == VisualWaveform || visual == VisualBars || visual == VisualScope
               || visual == VisualLissajous;

        /// <summary>
        /// 每个布局各自一份「可用可视化」清单 —— 可视化不再全局共用一个档位。
        /// 规则（用户拍板）：
        ///   径向    —— 只给黑胶：画在唱盘外圈。其它布局的封面是方的，围一圈柱子不搭；
        ///   歌词 / 居中 —— 一个要整幅歌词、一个是纯上下结构，插不进任何可视化，只有「不显示」；
        ///   李萨如  —— 撤出现有七种布局，留给极客主题那套播放页（那里才有足够大的方形画布）。
        /// </summary>
        internal static string[] VisualOptionsForLayout(string layout) => layout switch
        {
            LayoutVinyl => new[] { VisualRadial, VisualNone },
            LayoutLyrics => new[] { VisualNone },
            LayoutCenter => new[] { VisualNone },
            // 终端页有自己的方块频谱 / 相位图，不走波形槽这一套，所以菜单里只有「不显示」
            LayoutTerminal => new[] { VisualNone },
            _ => new[] { VisualWaveform, VisualBars, VisualScope, VisualNone }
        };

        /// <summary>切到某布局且当前档位用不了时的回退档位。</summary>
        internal static string DefaultVisualForLayout(string layout)
            => layout == LayoutVinyl ? VisualRadial
                : layout == LayoutLyrics || layout == LayoutCenter ? VisualNone
                : VisualWaveform;

        /// <summary>
        /// 读取设置里的可视化模式并套用（可见性切换 + 立即重绘一帧）。
        /// 由 ApplyNowPlayingLayout 末尾调用，保证布局规则先生效、这里只做覆盖。
        /// </summary>
        internal void ApplyNowPlayingVisual()
        {
            try
            {
                string requested = AppSettingsStore.Load().NowPlayingVisual;
                if (!IsKnownVisual(requested))
                {
                    requested = VisualWaveform;
                }

                string[] options = VisualOptionsForLayout(_nowPlayingLayoutName);

                // 当前布局用不了这一档 → 回退到该布局的默认档位。
                // 故意不写回设置：用户切回原来那个布局时，他原先选的档位还能自动恢复。
                string visual = Array.IndexOf(options, requested) >= 0
                    ? requested
                    : DefaultVisualForLayout(_nowPlayingLayoutName);

                _visualMode = visual;

                // 波形槽：占用槽的档位 + 布局本身允许（黑胶把封面列让给唱机、歌词/居中不放可视化、
                // 终端布局用自己的方块频谱画布，经典波形槽必须让位，否则与终端元素重叠）
                bool slotOk = VisualUsesSlot(visual)
                              && !_layoutIsVinyl && !_layoutIsLyrics && !_layoutIsCenter && !_layoutIsTerminal;

                if (WaveformCanvas != null)
                {
                    WaveformCanvas.Visibility = slotOk ? Visibility.Visible : Visibility.Collapsed;
                }

                // 径向频谱：只画在黑胶唱盘外圈（画布挂在 VinylStage 里，尺寸跟唱盘走）
                if (RadialVisualCanvas != null)
                {
                    RadialVisualCanvas.Visibility = visual == VisualRadial && _layoutIsVinyl
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                // 李萨如已从现有布局撤下（留给极客主题播放页），双声道捕获一律关掉 —— 渲染线程零开销。
                // 例外：终端页的相位图就是李萨如，那一页要开。
                _audioEngine?.SetStereoCapture(_layoutIsTerminal || visual == VisualLissajous);

                // 菜单只列当前布局能用的档位（用不了的连项都不出现，不留"点了没反应"的按钮）
                RebuildVisualFlyout(options);

                // 该布局一个可视化档位都没有（歌词/居中）→ 按钮整个藏起来。
                // 之前是置灰 + 写"本布局不提供可视化"，等于摆个点了没反应的按钮，用户明确要求直接不显示。
                bool noneOnly = options.Length <= 1;
                if (NowPlayingVisualButton != null)
                {
                    NowPlayingVisualButton.Visibility = noneOnly ? Visibility.Collapsed : Visibility.Visible;
                    NowPlayingVisualButton.IsEnabled = true;
                    NowPlayingVisualButton.Opacity = 1;
                }

                if (NowPlayingVisualButtonText != null)
                {
                    NowPlayingVisualButtonText.Text = "可视化：" + VisualDisplayName(visual);
                }

                // 模式切换后清掉波形槽的旧子元素，让下一帧按新模式重建
                if (_slotRenderedMode != SlotModeFor(visual))
                {
                    ClearVisualSlot();
                }

                // 立即画一帧，避免切过来时闪一拍空白
                if (visual == VisualRadial)
                {
                    DrawRadialVisual();
                }
                else if (slotOk)
                {
                    DrawVisualSlot();
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ApplyNowPlayingVisual", caught);
            }
        }

        /// <summary>波形槽（WaveformCanvas）里按模式决定用哪套子元素。</summary>
        private static string SlotModeFor(string visual)
            => visual == VisualScope || visual == VisualLissajous ? "Scope" : visual;

        /// <summary>
        /// 示波器/李萨如需要更高的槽位：40px 高的条带里画波形线会被压成一条，
        /// 由 UpdateNowPlayingCardLayout 据此把画布加高（水面布局本来就够高，不受影响）。
        /// </summary>
        internal bool VisualNeedsTallSlot()
            => _visualMode == VisualScope || _visualMode == VisualLissajous;

        /// <summary>清空波形槽子元素（模式切换时调用一次）。</summary>
        private void ClearVisualSlot()
        {
            if (WaveformCanvas != null)
            {
                WaveformCanvas.Children.Clear();
            }

            _scopeLine = null;
            _slotRenderedMode = string.Empty;
        }

        /// <summary>页内可视化菜单：写设置并立即套用。</summary>
        private void NowPlayingVisualMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not MenuFlyoutItem item)
                {
                    return;
                }

                string visual = item.Tag as string ?? VisualWaveform;
                if (!IsKnownVisual(visual))
                {
                    visual = VisualWaveform;
                }

                AppSettingsStore.Update(s => s.NowPlayingVisual = visual);
                ApplyNowPlayingVisual();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.NowPlayingVisualMenuItem_Click", caught);
            }
        }

        /// <summary>
        /// 菜单按「当前布局的可用清单」重建：用不了的档位连菜单项都不生成。
        /// WinUI 的 MenuFlyoutItem 没有 Visibility，只能增删 Items，不能灰掉。
        /// </summary>
        private void RebuildVisualFlyout(string[] options)
        {
            if (NowPlayingVisualFlyout == null)
            {
                return;
            }

            if (NowPlayingVisualFlyout.Items.Count == options.Length)
            {
                bool same = true;
                for (int i = 0; i < options.Length; i++)
                {
                    if (NowPlayingVisualFlyout.Items[i] is not MenuFlyoutItem item
                        || (string?)item.Tag != options[i])
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    return;
                }
            }

            NowPlayingVisualFlyout.Items.Clear();
            foreach (string visual in options)
            {
                var item = new MenuFlyoutItem
                {
                    Text = VisualMenuText(visual),
                    Tag = visual
                };
                item.Click += NowPlayingVisualMenuItem_Click;
                NowPlayingVisualFlyout.Items.Add(item);
            }
        }

        /// <summary>
        /// 按当前模式重绘波形槽（由 WaveformTimer_Tick 与 ApplyNowPlayingVisual 调用）。
        /// 真实信号拿不到时统一回退到现有装饰性电平（_waveLevels 已含该逻辑）。
        /// </summary>
        private void DrawVisualSlot()
        {
            string slotMode = SlotModeFor(_visualMode);

            // 径向画在黑胶唱盘外圈，跟波形槽无关（此时槽是 Collapsed）—— 单独走一条路。
            if (slotMode == VisualRadial)
            {
                DrawRadialVisual();
                return;
            }

            if (WaveformCanvas == null || WaveformCanvas.Visibility != Visibility.Visible)
            {
                return;
            }

            // 子元素套件不匹配时先重建（首次进入某模式 / 模式刚切换）
            if (_slotRenderedMode != slotMode)
            {
                ClearVisualSlot();
                _slotRenderedMode = slotMode;
            }

            switch (slotMode)
            {
                case "Bars":
                    DrawBarsStyle();
                    break;
                case "Scope":
                    // 李萨如已撤出现有布局（留给极客主题播放页），这里恒定走单声道示波器模式；
                    // 分支保留，将来极客页只要把档位设为 Lissajous 就能直接复用。
                    DrawScopeStyle(_visualMode == VisualLissajous);
                    break;
                case "Radial":
                    DrawRadialVisual();
                    break;
                default:
                    DrawWaveformBars();
                    break;
            }
        }

        // =====================================================================
        // 频谱柱（方块段 + 峰值帽）
        // =====================================================================

        /// <summary>方块段高度：按画布高度分档，柱高取整到段上，形成 ECHO 截图那种“一格格”的数码感。</summary>
        private void DrawBarsStyle()
        {
            if (WaveformCanvas == null)
            {
                return;
            }

            double width = WaveformCanvas.ActualWidth;
            double height = WaveformCanvas.ActualHeight;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            double gap = 2;
            bool water = _layoutIsWater;
            int mainCount = WaveBarCount;
            int capBase = water ? mainCount * 2 : mainCount;
            int total = capBase + mainCount; // 主柱（+水面倒影）+ 每柱一根峰值帽
            double barWidth = Math.Max(2, (width - gap * (mainCount - 1)) / mainCount);

            // 段高：画布 40 高 → 5px 一段，高低两档都能看出“格子”
            double segH = Math.Max(3.0, Math.Round(height / 8.0));

            // 峰值帽慢落（每 tick 落 0.035，约 0.7s 从顶落底）
            for (int i = 0; i < mainCount; i++)
            {
                double level = Math.Clamp(_waveLevels[i], 0.06, 1.0);
                _wavePeaks[i] = Math.Max(_wavePeaks[i] - 0.035, level);
            }

            while (WaveformCanvas.Children.Count < total)
            {
                WaveformCanvas.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(1),
                    IsHitTestVisible = false
                });
            }

            while (WaveformCanvas.Children.Count > total)
            {
                WaveformCanvas.Children.RemoveAt(WaveformCanvas.Children.Count - 1);
            }

            for (int i = 0; i < mainCount; i++)
            {
                if (WaveformCanvas.Children[i] is not Border bar)
                {
                    continue;
                }

                Color accent = WaveColorFor(i);
                bar.Background = new SolidColorBrush(Color.FromArgb(175, accent.R, accent.G, accent.B));

                // 高度取整到段：方块段效果
                double rawH = Math.Max(segH, height * Math.Clamp(_waveLevels[i], 0.12, 1.0) * 1.15);
                double barHeight = Math.Round(rawH / segH) * segH;
                if (barHeight > height)
                {
                    barHeight = Math.Round(height / segH) * segH;
                }

                double left = i * (barWidth + gap);
                double top;
                if (water)
                {
                    double half = height * 0.5;
                    top = Math.Max(0, half - barHeight);
                    if (WaveformCanvas.Children[mainCount + i] is Border refl)
                    {
                        refl.Width = barWidth;
                        refl.Height = barHeight;
                        refl.Background = ReflectionBrushFor(i, accent);
                        Canvas.SetLeft(refl, left);
                        Canvas.SetTop(refl, half);
                    }
                }
                else
                {
                    top = (height - barHeight) / 2;
                }

                bar.Width = barWidth;
                bar.Height = barHeight;
                Canvas.SetLeft(bar, left);
                Canvas.SetTop(bar, top);

                // 峰值帽：满亮度的小横条，悬在柱顶上方 2px，慢落
                if (WaveformCanvas.Children[capBase + i] is Border cap)
                {
                    double peakH = Math.Max(segH, height * Math.Clamp(_wavePeaks[i], 0.12, 1.0) * 1.15);
                    double peakTop = water
                        ? Math.Max(0, height * 0.5 - Math.Round(peakH / segH) * segH - 4)
                        : (height - Math.Round(peakH / segH) * segH) / 2 - 4;
                    cap.Width = barWidth;
                    cap.Height = 3;
                    cap.Background = new SolidColorBrush(accent);
                    Canvas.SetLeft(cap, left);
                    Canvas.SetTop(cap, Math.Max(0, peakTop));
                }
            }
        }

        // =====================================================================
        // 示波器（实时波形线）/ 李萨如（L-R 相位图）
        // =====================================================================

        private void DrawScopeStyle(bool lissajous)
        {
            if (WaveformCanvas == null)
            {
                return;
            }

            double width = WaveformCanvas.ActualWidth;
            double height = WaveformCanvas.ActualHeight;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            if (_scopeLine == null)
            {
                _scopeLine = new Polyline
                {
                    StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                WaveformCanvas.Children.Add(_scopeLine);
            }

            Color accent = WaveColorFor(0);
            _scopeLine.Stroke = new SolidColorBrush(Color.FromArgb(230, accent.R, accent.G, accent.B));

            PointCollection points = new();
            bool playing = IsEnginePlayingNow || (_usingEnginePlayback && _audioEngine?.IsPlaying == true);

            if (lissajous)
            {
                // 李萨如：x = 左声道，y = 右声道（相位关系一目了然；单声道时是对角线，也真实）
                bool hasData = playing
                    && _audioEngine != null
                    && _audioEngine.TryGetStereoSamples(_scopeLeft, _scopeRight);
                if (hasData)
                {
                    double amp = Math.Min(width, height) * 0.48;
                    double cx = width / 2;
                    double cy = height / 2;
                    int step = ScopeSampleCount / ScopePointCount;
                    for (int i = 0; i < ScopeSampleCount; i += step)
                    {
                        points.Add(new Point(cx + _scopeLeft[i] * amp, cy - _scopeRight[i] * amp));
                    }
                }
            }
            else
            {
                // 示波器：一条实时波形线
                bool hasData = playing && _audioEngine != null && _audioEngine.TryGetSamples(_scopeSamples);
                if (hasData)
                {
                    double amp = height * 0.46;
                    double cy = height / 2;
                    double dx = width / (ScopePointCount - 1);
                    int step = ScopeSampleCount / ScopePointCount;
                    for (int i = 0; i < ScopePointCount; i++)
                    {
                        points.Add(new Point(i * dx, cy - _scopeSamples[i * step] * amp));
                    }
                }
            }

            if (points.Count == 0)
            {
                // 无信号：一条居中的静默基线（比空白好看，也符合“示波器没接信号”的直觉）
                double cy = height / 2;
                points.Add(new Point(0, cy));
                points.Add(new Point(width, cy));
            }

            _scopeLine.Points = points;
        }

        // =====================================================================
        // 径向频谱（环绕封面）
        // =====================================================================

        private void DrawRadialVisual()
        {
            if (RadialVisualCanvas == null || RadialVisualCanvas.Visibility != Visibility.Visible)
            {
                return;
            }

            double width = RadialVisualCanvas.ActualWidth;
            double height = RadialVisualCanvas.ActualHeight;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            // 内圈贴唱盘边缘留 4px（半径由 UpdateVinylGeometry 按唱盘尺寸写入）；
            // 柱子最多往外伸 56px，画布单边余量 64，伸满也不会被裁。
            // 兜底：还没套用过黑胶几何时按画布自己推算一个内径。
            double baseR = _radialInnerRadius > 0
                ? _radialInnerRadius
                : Math.Max(8, Math.Min(width, height) / 2 - 60);
            double maxLen = RadialMaxBarLength;

            double cx = width / 2;
            double cy = height / 2;
            double twoPi = Math.PI * 2;

            for (int i = 0; i < RadialBarCount; i++)
            {
                double level;
                if (i < WaveBarCount)
                {
                    // 64 根柱映射到 40 个频段：每根取“附近两三个频段”的最大值，保证圆环平滑
                    int a = (int)Math.Floor(i * WaveBarCount / (double)RadialBarCount);
                    int b = Math.Min(WaveBarCount - 1, (int)Math.Ceiling((i + 1) * WaveBarCount / (double)RadialBarCount) - 1);
                    double m = 0;
                    for (int k = a; k <= b; k++)
                    {
                        if (_waveLevels[k] > m)
                        {
                            m = _waveLevels[k];
                        }
                    }

                    level = m;
                }
                else
                {
                    level = _waveLevels[i % WaveBarCount];
                }

                // 最短 8px：静音时也要能看出"一圈刺"，不是一圈点
                double len = Math.Max(8, level * maxLen);

                Border? bar = _radialBars[i];
                if (bar == null)
                {
                    bar = new Border
                    {
                        Width = 4.5,
                        CornerRadius = new CornerRadius(2.25),
                        IsHitTestVisible = false,
                        RenderTransformOrigin = new Point(0.5, 0.5)
                    };
                    _radialBars[i] = bar;
                    RadialVisualCanvas.Children.Add(bar);
                }

                Color accent = WaveColorFor(i % WaveBarCount);
                // 长柱子更亮：静音也有 150 的底，最大到 255
                byte alpha = (byte)(150 + Math.Round(level * 105));
                bar.Background = new SolidColorBrush(Color.FromArgb(alpha, accent.R, accent.G, accent.B));
                bar.Height = len;

                double angle = i * twoPi / RadialBarCount;
                double sin = Math.Sin(angle);
                double cos = Math.Cos(angle);

                // 柱子中点放在 (baseR + len/2) 处，沿角度向外
                double midR = baseR + len / 2;
                double x = cx + sin * midR;
                double y = cy - cos * midR;

                Canvas.SetLeft(bar, x - bar.Width / 2);
                Canvas.SetTop(bar, y - len / 2);
                bar.RenderTransform = new RotateTransform { Angle = angle * 180 / Math.PI };
            }
        }
    }
}
