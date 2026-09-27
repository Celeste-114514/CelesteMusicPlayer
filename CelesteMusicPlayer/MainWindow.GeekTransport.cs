using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 极客模式的「命令行 transport」：把底部播放条换成终端那套视觉语言。
    ///
    /// 之前极客模式只做了"字体换等宽 + 圆角归零 + 底色变黑"，骨架还是 WinUI 的卡片流
    /// —— 用户实测反馈"换汤不换药"。这里动的是**语言**而不是皮肤：
    ///   1. 进度条字符化：滑块照常负责拖动（只是变透明），上面盖一行 ████░░░░ 字符块；
    ///   2. 命令提示符：时间前面加 "&gt; "，像在等一条命令；
    ///   3. 链路读数直接写在界面上：输出格式 + 是否 bit-perfect（引擎真数据，不是装饰）。
    ///
    /// 可回滚性：所有改动只在 geek=true 时生效，切回经典 / 图景时全部还原
    ///（滑块恢复不透明、两行文本 Collapsed、CRT 层 Collapsed），不留残余。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>极客磷光色的三档（设置项「极客磷光色」）。琥珀为默认。</summary>
        internal const string PhosphorAmber = "Amber";
        internal const string PhosphorGreen = "Green";
        internal const string PhosphorCyan = "Cyan";

        /// <summary>字符进度条有多少格。等宽字下每格宽度一致，格数越多越细腻。</summary>
        private const int GeekProgressBlocks = 28;

        private bool _geekTransportSubscribed;
        private bool _geekProgressSized;
        private bool _geekProgressSliderSized;

        private static Color PhosphorColor(string? name)
            => name switch
            {
                PhosphorGreen => Color.FromArgb(255, 0x3E, 0xFF, 0x7A),   // 荧光绿
                PhosphorCyan => Color.FromArgb(255, 0x35, 0xE0, 0xFF),    // 青
                _ => Color.FromArgb(255, 0xFF, 0xB0, 0x00),               // 琥珀（默认）：老式 CRT 的暖黄
            };

        /// <summary>极客 transport 总入口：由 ApplyGeekChrome 在界面风格切换时调用一次。</summary>
        internal void ApplyGeekTransport(bool geek)
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                var phosphor = new SolidColorBrush(PhosphorColor(settings.GeekPhosphorColor));

                if (GeekProgressText != null)
                {
                    GeekProgressText.Visibility = geek ? Visibility.Visible : Visibility.Collapsed;
                    GeekProgressText.Foreground = phosphor;
                }

                if (GeekTransportStatus != null)
                {
                    GeekTransportStatus.Visibility = geek ? Visibility.Visible : Visibility.Collapsed;
                    GeekTransportStatus.Foreground = phosphor;
                }

                // 顶栏那行读数也跟着换磷光色，保证整个界面只有一种彩色
                if (GeekOutputReadout != null && geek)
                {
                    GeekOutputReadout.Foreground = phosphor;
                }

                if (ProgressSlider != null)
                {
                    // 滑块透明但**照常可拖**：拖动逻辑一行不改，只是看不见圆头了。
                    // 这样既拿到字符条的观感，又不用重写一套拖动命中测试。
                    ProgressSlider.Opacity = geek ? 0 : 1;

                    if (geek && !_geekTransportSubscribed)
                    {
                        ProgressSlider.ValueChanged += GeekProgressSlider_ValueChanged;
                        _geekTransportSubscribed = true;
                    }
                }

                if (GeekProgressText != null && geek && !_geekProgressSized)
                {
                    // 格数得按实际宽度算：固定 28 格会缩成中间一小段，跟下面的滑块对不上，
                    // 拖起来就不跟手。窗口一缩放要重算。
                    GeekProgressText.SizeChanged -= GeekProgressText_SizeChanged;
                    GeekProgressText.SizeChanged += GeekProgressText_SizeChanged;
                    _geekProgressSized = true;
                }

                if (geek && ProgressSlider != null && !_geekProgressSliderSized)
                {
                    // 字符条宽度以滑块实测宽度为准（两者同一格，滑块天然铺满整行）——
                    // 滑块尺寸一变（窗口缩放 / 首次布局）就重排字符条。
                    ProgressSlider.SizeChanged -= GeekProgressRef_SizeChanged;
                    ProgressSlider.SizeChanged += GeekProgressRef_SizeChanged;
                    _geekProgressSliderSized = true;
                }

                if (geek && ProgressStyleCanvas != null)
                {
                    // 波形 / 频谱柱等自绘进度样式让位给字符进度条。
                    // RedrawProgressStyle 里也拦了一道（双保险）：那条管住"之后的重绘"，
                    // 这条管住"进入极客那一刻画布还开着"的现场。
                    ProgressStyleCanvas.Visibility = Visibility.Collapsed;
                    ProgressStyleCanvas.Children.Clear();
                }

                // 退出极客时主动重绘一次进度条样式：极客期间 RedrawProgressStyle 一直拒绝画波形，
                // 不补这一下的话，切回经典 / 图景后波形画布会停在 Collapsed，直到下一次触发重绘才回来
                if (!geek)
                {
                    RedrawProgressStyle();
                }

                ApplyGeekCrtOverlay(geek && settings.GeekCrtEnabled);

                if (geek)
                {
                    UpdateGeekProgressText();
                    UpdateGeekTransportStatus();
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ApplyGeekTransport", caught);
            }
        }

        /// <summary>进度一变就跟着刷（播放推进 / 拖动 / 切歌都会触发滑块的 ValueChanged）。</summary>
        private void GeekProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            UpdateGeekProgressText();
            UpdateGeekTransportStatus();
        }

        /// <summary>宽度变了要重排字符进度条：格数按宽度算，铺满整行才跟手。</summary>
        private void GeekProgressText_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            UpdateGeekProgressText();
        }

        /// <summary>滑块尺寸变了（首次布局 / 窗口缩放）也要重排：字符条以滑块宽度为基准。</summary>
        private void GeekProgressRef_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            UpdateGeekProgressText();
            UpdateGeekTransportStatus();
        }

        private void UpdateGeekProgressText()
        {
            if (GeekProgressText == null || GeekProgressText.Visibility != Visibility.Visible) return;
            if (ProgressSlider == null) return;

            double max = ProgressSlider.Maximum;
            double ratio = max > 0 ? Math.Clamp(ProgressSlider.Value / max, 0.0, 1.0) : 0;

            // 参考宽度拿滑块的实测宽度（与原波形条同一格、完全同长）。
            // 之前拿字符条自己的 ActualWidth —— 它是 Stretch 对齐，量的是容器还行，
            // 但一旦布局没定就是 0，回退成 28 格就缩成一小段。
            double width = ProgressSlider.ActualWidth;
            if (width <= 40)
            {
                width = GeekProgressText.ActualWidth;
            }

            // 布局还没定：先不画，等 SizeChanged / 下一次进度跳动再画。
            if (width <= 40) return;

            // 等宽字每格宽 ≈ 0.55em（Consolas 实际 0.5498em，误差不到半个像素）。
            double perChar = Math.Max(1.0, GeekProgressText.FontSize * 0.55);
            int blocks = (int)Math.Clamp(Math.Round(width / perChar), 12, 220);

            int filled = (int)Math.Round(ratio * blocks);
            if (filled < 0) filled = 0;
            if (filled > blocks) filled = blocks;

            SetGeekBarText(GeekProgressText, string.Empty, filled, blocks);
        }

        /// <summary>
        /// 画字符进度/音量条：实心部分用磷光色、空心部分用**同一个字符的半透明版**。
        /// 之前空心用 ░：这个字符在等宽字体里缺字、被系统换字体补，基线和 █ 对不齐，
        /// 用户实测"实心块和空心点阵上下错位"。统一用 █ + 半透明就绝不可能错位。
        /// </summary>
        internal static void SetGeekBarText(TextBlock bar, string prefix, int filled, int total)
        {
            SolidColorBrush brush = (SolidColorBrush)bar.Foreground;
            Color c = brush.Color;
            var dim = new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B));

            bar.Inlines.Clear();
            if (prefix.Length > 0)
            {
                bar.Inlines.Add(new Run { Text = prefix, Foreground = brush });
            }
            if (filled > 0)
            {
                bar.Inlines.Add(new Run { Text = new string('█', filled), Foreground = brush });
            }
            int empty = total - filled;
            if (empty > 0)
            {
                bar.Inlines.Add(new Run { Text = new string('█', empty), Foreground = dim });
            }
        }

        /// <summary>
        /// 命令行状态条：&gt; 当前时间 / 总时长 · 输出格式 · 链路状态。
        /// 全是引擎真数据 —— 这是别家播放器没有的：直接在界面上告诉你现在是不是 bit-perfect。
        /// </summary>
        private void UpdateGeekTransportStatus()
        {
            if (GeekTransportStatus == null || GeekTransportStatus.Visibility != Visibility.Visible) return;

            try
            {
                var parts = new List<string>();

                string current = CurrentTimeText?.Text ?? "00:00";
                string total = TotalTimeText?.Text ?? "00:00";
                // "> " 是命令提示符：整条看起来像一行等待执行的命令，而不是一排状态标签
                parts.Add("> " + current + " / " + total);

                string fmt = _audioEngine?.ActualOutputFormat ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(fmt))
                {
                    parts.Add(fmt);
                }

                // 链路状态交给右侧状态灯显示（口径见 DescribeBitPerfectFlag）。
                // 早先这里拿导航圆点数组判 DSP，而那个数组末位是"监控页常亮"占位符恒为 true，
                // 于是永远显示 DSP ON，跟顶栏的 bit-perfect 自相矛盾 —— 已拆开。

                GeekTransportStatus.Text = string.Join("  ·  ", parts);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("UpdateGeekTransportStatus", caught);
            }
        }

        /// <summary>CRT 扫描线：一层很淡的横向暗纹。不参与命中测试，也不碰音频链路。</summary>
        private void ApplyGeekCrtOverlay(bool on)
        {
            if (GeekCrtOverlay == null) return;

            try
            {
                if (!on)
                {
                    GeekCrtOverlay.Visibility = Visibility.Collapsed;
                    return;
                }

                GeekCrtOverlay.Background = BuildScanlineBrush();
                GeekCrtOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ApplyGeekCrtOverlay", caught);
            }
        }

        /// <summary>
        /// 用垂直渐变拼出扫描线：每段给两个同 offset 的 stop 造出硬边（不然会被插值成糊的）。
        /// 没有 RadialGradientBrush 可用，暗角那部分先不做 —— 扫描线已经够定调子了。
        /// </summary>
        private static LinearGradientBrush BuildScanlineBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.5, 0),
                EndPoint = new Windows.Foundation.Point(0.5, 1)
            };

            const int lines = 160;
            Color dark = Color.FromArgb(30, 0, 0, 0);
            Color clear = Color.FromArgb(0, 0, 0, 0);

            for (int i = 0; i < lines; i++)
            {
                double top = (double)i / lines;
                double mid = (i + 0.55) / lines;
                double bottom = (i + 1.0) / lines;

                brush.GradientStops.Add(new GradientStop { Color = dark, Offset = top });
                brush.GradientStops.Add(new GradientStop { Color = dark, Offset = mid });
                brush.GradientStops.Add(new GradientStop { Color = clear, Offset = mid });
                brush.GradientStops.Add(new GradientStop { Color = clear, Offset = bottom });
            }

            return brush;
        }
    }
}
