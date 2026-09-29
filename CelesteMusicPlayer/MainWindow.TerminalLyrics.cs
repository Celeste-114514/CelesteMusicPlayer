using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 终端布局的歌词滚动面板（用户 2026-09-29 拍板「方案甲」：底部扩成
    /// LYRICS 2/3 + QUEUE 1/3 双栏）。
    /// 数据全部复用主歌词页已解析好的 _lyricLines（含翻译行、逐字时间轴），不重新读文件；
    /// 高亮 / 滚动 / 跳转独立一套：当前行 = 磷光色 + 左竖条 + ▶ 前缀，行 = [mm:ss.xx] 时间码 + 文本，
    /// Consolas 等宽。只读 UI，不碰音频字节流（bit-perfect 无关）。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly List<TextBlock> _terminalLyricTexts = new();
        private readonly List<Border> _terminalLyricFrames = new();
        private readonly List<string> _terminalLyricRaw = new();

        /// 终端歌词面板当前高亮行。独立于主歌词页的 _currentLyricIndex，免得两页互相干扰。</summary>
        private int _terminalLyricIndex = -1;

        /// 已建行的曲目路径。切歌时先清空，等 BuildLyricsUi 带新歌词进来再建 ——
        /// 否则异步歌词加载完成前，面板会短暂显示上一首的歌词（串歌）。
        private string _terminalLyricSongPath = string.Empty;

        /// 切歌调用：清掉上一首的歌词行（旧词不能留，等新词进来再建）。</summary>
        internal void ResetTerminalLyrics(string hint)
        {
            _terminalLyricIndex = -1;
            _terminalLyricTexts.Clear();
            _terminalLyricFrames.Clear();
            _terminalLyricRaw.Clear();
            if (TerminalLyricsPanel != null)
            {
                TerminalLyricsPanel.Children.Clear();
                TerminalLyricsPanel.Padding = new Thickness(0);
            }

            if (TerminalLyricsEmpty != null)
            {
                TerminalLyricsEmpty.Text = string.IsNullOrWhiteSpace(hint) ? "NO LYRICS" : hint;
                TerminalLyricsEmpty.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 歌词数据就绪（BuildLyricsUi 的出口）或切到终端布局时调用：按 _lyricLines 建行。
        /// 幂等 —— 重复调用只是重建行 + 回到当前播放位置。
        /// </summary>
        internal void BuildTerminalLyricsRows()
        {
            ResetTerminalLyrics(string.Empty);
            _terminalLyricSongPath = _nowPlayingPath ?? string.Empty;

            if (_lyricLines == null || _lyricLines.Count == 0 || TerminalLyricsPanel == null)
            {
                return; // Reset 里已显示 NO LYRICS
            }

            if (TerminalLyricsEmpty != null)
            {
                TerminalLyricsEmpty.Visibility = Visibility.Collapsed;
            }

            // 上下垫高，首尾行也能滚到中间（主歌词页同策略，量级缩小）
            TerminalLyricsPanel.Padding = new Thickness(0, 30, 0, 30);

            foreach (LyricLine line in _lyricLines)
            {
                string raw = "[" + FormatLyricStamp(line.Time) + "] " + line.Text;
                var tb = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = line.IsTranslation ? 11 : 12,
                    Text = raw,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsHitTestVisible = false,
                    Tag = line,
                };

                var frame = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(8, 3, 8, 3),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Child = tb
                };

                var row = new Grid
                {
                    // 透明底：点空白也能命中，否则点击漏到下面界面（主歌词页同款教训）
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                };
                row.Children.Add(frame);

                if (!line.IsTranslation)
                {
                    // 双击 = 从这句开始播放（与主歌词页一致；翻译行不跳）
                    TimeSpan target = line.Time;
                    row.DoubleTapped += (_, _) => PlayFromLyricLine(target);
                }

                _terminalLyricTexts.Add(tb);
                _terminalLyricFrames.Add(frame);
                _terminalLyricRaw.Add(raw);
                TerminalLyricsPanel.Children.Add(row);
            }

            RetintTerminalLyrics();
            SyncTerminalLyricsToPosition(TerminalCurrentPosition());
        }

        /// <summary>时间戳 [mm:ss.xx]（厘秒，标到这一行的准确起唱点）。</summary>
        private static string FormatLyricStamp(TimeSpan t)
            => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";

        /// <summary>
        /// 与主歌词页同一节拍（SyncLyricsToPosition 里调）：按播放位置推进高亮 + 滚动居中。
        /// adj 是已套用歌词偏移的位置。
        /// </summary>
        private void SyncTerminalLyricsToPosition(TimeSpan adj)
        {
            if (!_terminalStageVisible || _terminalLyricTexts.Count == 0)
            {
                return;
            }

            int index = 0;
            for (int i = 0; i < _lyricLines.Count; i++)
            {
                if (_lyricLines[i].Time <= adj)
                {
                    // 跳过翻译行：高亮停在原文行（主歌词页同一规则）
                    if (!_lyricLines[i].IsTranslation)
                    {
                        index = i;
                    }
                }
                else
                {
                    break;
                }
            }

            if (index == _terminalLyricIndex)
            {
                return;
            }

            _terminalLyricIndex = index;
            RetintTerminalLyrics();
            ScrollTerminalLyricIntoView(index);
        }

        /// <summary>按当前高亮行重染全部行（高亮行变更 / 换磷光色时调用）。</summary>
        internal void RetintTerminalLyrics()
        {
            if (_terminalLyricTexts.Count == 0)
            {
                return;
            }

            Color accent = TerminalAccentColor();
            for (int i = 0; i < _terminalLyricTexts.Count; i++)
            {
                TextBlock tb = _terminalLyricTexts[i];
                if (tb.Tag is not LyricLine line)
                {
                    continue;
                }

                Border frame = _terminalLyricFrames[i];
                if (line.IsTranslation)
                {
                    // 翻译行：小一号、暗一档，永远不高亮
                    tb.FontSize = 11;
                    tb.Foreground = PhosphorShade(accent, 0x66);
                    tb.Text = _terminalLyricRaw[i];
                    frame.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    frame.BorderThickness = new Thickness(0);
                    continue;
                }

                int dist = Math.Abs(i - _terminalLyricIndex);
                if (dist == 0)
                {
                    // 当前行：磷光满色 + 加粗 + 左竖条 + ▶ 前缀
                    tb.FontSize = 13;
                    tb.Foreground = new SolidColorBrush(accent);
                    tb.Text = "▶ " + _terminalLyricRaw[i];
                    frame.BorderBrush = new SolidColorBrush(accent);
                    frame.BorderThickness = new Thickness(2, 0, 0, 0);
                }
                else if (dist == 1)
                {
                    // 邻行：淡一档
                    tb.FontSize = 12;
                    tb.Foreground = PhosphorShade(accent, 0xD8);
                    tb.Text = _terminalLyricRaw[i];
                    frame.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    frame.BorderThickness = new Thickness(0);
                }
                else
                {
                    // 远处行：再暗一档
                    tb.FontSize = 12;
                    tb.Foreground = PhosphorShade(accent, 0x80);
                    tb.Text = _terminalLyricRaw[i];
                    frame.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    frame.BorderThickness = new Thickness(0);
                }
            }
        }

        /// <summary>把当前行滚到视口中间（行刚建成还没量尺寸就跳过，等下一次行切换再滚）。</summary>
        private void ScrollTerminalLyricIntoView(int index)
        {
            if (TerminalLyricsScroll == null || TerminalLyricsPanel == null)
            {
                return;
            }

            if (index < 0 || index >= TerminalLyricsPanel.Children.Count)
            {
                return;
            }

            if (TerminalLyricsPanel.Children[index] is not FrameworkElement row || row.ActualHeight <= 0)
            {
                return;
            }

            GeneralTransform transform = row.TransformToVisual(TerminalLyricsScroll);
            double y = transform.TransformPoint(new Point(0, 0)).Y;
            double target = Math.Max(0, y - TerminalLyricsScroll.ViewportHeight / 2.0 + row.ActualHeight / 2.0);
            TerminalLyricsScroll.ChangeView(null, target, null, false);
        }
    }
}
