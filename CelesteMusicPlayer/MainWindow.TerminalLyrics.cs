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
            // 换歌了，选择期没有意义：撤掉，别让旧歌的 hold 把新歌的面板钉在原地
            EndTerminalLyricHold();

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
                    TimeSpan target = line.Time;
                    int rowIndex = TerminalLyricsPanel.Children.Count; // row 还没 Add，当前计数就是它的索引
                    // 单击 = 滚到这句 + 进「选择期」：3 秒内不自动回弹到播放位置，让用户
                    // 从容决定要不要双击（用户 2026-09-29 反馈「点击后瞬间跳回当前进度」）；
                    // 超时由 Hold 计时器负责滚回当前播放句。与主歌词页 3 秒选中同一体验。
                    row.Tapped += (_, _) =>
                    {
                        ScrollTerminalLyricIntoView(rowIndex);
                        BeginTerminalLyricHold();
                    };
                    // 双击 = 从这句开始播放（与主歌词页一致；翻译行不跳）。
                    // 先撤选择期：否则 seek 后的强制同步会被 hold 挡着，滚不到目标行。
                    row.DoubleTapped += (_, _) =>
                    {
                        EndTerminalLyricHold();
                        PlayFromLyricLine(target);
                    };
                }

                _terminalLyricTexts.Add(tb);
                _terminalLyricFrames.Add(frame);
                _terminalLyricRaw.Add(raw);
                TerminalLyricsPanel.Children.Add(row);
            }

            RetintTerminalLyrics();
            SyncTerminalLyricsToPosition(TerminalCurrentPosition());
            // 行刚建出来还没过布局（ActualHeight=0），滚动会被「行高未实测」挡掉；
            // 延后到布局完成后补滚一次，否则首屏停在第一行、不落在当前播放句上。
            DispatcherQueue.TryEnqueue(() => ScrollTerminalLyricIntoView(_terminalLyricIndex));
            global::CelesteMusicPlayer.StartupLog.Write(
                $"[终端歌词] 建行 rows={_terminalLyricTexts.Count} lyricLines={_lyricLines.Count} " +
                $"path={_terminalLyricSongPath} layout={_layoutIsTerminal}");
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
            // 只依赖"有没有行"：不查 _terminalStageVisible —— 那个标志只在切布局时刷新，
            // 用户 2026-09-29 实测「高亮不随进度走」的排查里，它是重点嫌疑（切进终端布局的
            // 时序有多个入口，任何一条漏掉 ApplyTerminalLayout 就让同步整段静默死亡）。
            // 行存在（ResetTerminalLyrics 会清行）就该同步，面板不可见时滚一下也无害。
            if (_terminalLyricTexts.Count == 0)
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

            if (index != _terminalLyricIndex)
            {
                _terminalLyricIndex = index;
                RetintTerminalLyrics();
            }
            // 选择期内不自动吸附：用户刚点了某句，这时候每 tick 滚回播放位置就等于
            // 「点完瞬间被拽回」。高亮照常推进（真实播放状态），只是面板停住等人决定。
            if (_terminalUserScrolling)
            {
                return;
            }
            // index 没变也尝试滚动：双击当前高亮句时（用户先把面板滚去了别处）要能滚回来；
            // ScrollTerminalLyricIntoView 内部有 1px 防抖，不动时是几次属性读取，成本可忽略
            ScrollTerminalLyricIntoView(index);

            // 诊断（1s 节流）：「高亮不随进度走」的尸检报告——adj/index/当前高亮一站式看全
            if (Environment.TickCount64 - _lastTerminalLyricLogMs > 1000)
            {
                _lastTerminalLyricLogMs = Environment.TickCount64;
                global::CelesteMusicPlayer.StartupLog.Write(
                    $"[终端歌词] sync adj={adj.TotalSeconds:F2}s index={index} cur={_terminalLyricIndex} " +
                    $"rows={_terminalLyricTexts.Count} lyricLines={_lyricLines.Count} " +
                    $"layout={_layoutIsTerminal} stage={_terminalStageVisible}");
            }
        }

        private long _lastTerminalLyricLogMs;

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

            // 内容坐标：TransformToVisual(Scroll) 返回的是视觉坐标（已减掉当前滚动量），
            // 拿它算 target 会少滚一个"当前滚动量"——双击跳转后面板纹丝不动就是这个原因。
            GeneralTransform transform = row.TransformToVisual(TerminalLyricsPanel);
            double y = transform.TransformPoint(new Point(0, 0)).Y;

            // clamp 到可滚范围；与当前偏移差 ≤1px 就不动（播放推进时每句都 ChangeView 会微抖）
            double maxOffset = Math.Max(0, TerminalLyricsScroll.ExtentHeight - TerminalLyricsScroll.ViewportHeight);
            double target = Math.Clamp(y - TerminalLyricsScroll.ViewportHeight / 2.0 + row.ActualHeight / 2.0, 0, maxOffset);
            if (Math.Abs(TerminalLyricsScroll.VerticalOffset - target) <= 1)
            {
                return;
            }

            TerminalLyricsScroll.ChangeView(null, target, null, false);
        }

        // ---- 「选择期」：点一句后给用户几秒决定是否双击跳转，超时才回当前播放句 ----

        /// <summary>选择期时长（毫秒）。与主歌词页单击选中后的 3 秒自动取消保持一致。</summary>
        private const int TerminalLyricHoldMs = 3000;

        /// <summary>true = 用户刚点了某句，SyncTerminalLyricsToPosition 暂停自动回弹。</summary>
        private bool _terminalUserScrolling;

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _terminalLyricHoldTimer;

        /// <summary>
        /// 进入选择期：面板停在用户点的那句，不再每 tick 被拽回播放位置。
        /// 重复点击会续期（每次点击重新计时）。超时滚回当前播放句。
        /// </summary>
        private void BeginTerminalLyricHold()
        {
            _terminalUserScrolling = true;

            if (_terminalLyricHoldTimer == null)
            {
                _terminalLyricHoldTimer = DispatcherQueue.CreateTimer();
                _terminalLyricHoldTimer.Interval = TimeSpan.FromMilliseconds(TerminalLyricHoldMs);
                _terminalLyricHoldTimer.Tick += (_, _) =>
                {
                    _terminalLyricHoldTimer.Stop();
                    _terminalUserScrolling = false;
                    // 超时：回当前播放句（高亮行）。若用户已双击跳转过，高亮已在目标行，
                    // 这里滚过去正好对齐；没跳转就回到正在唱的那句。
                    ScrollTerminalLyricIntoView(_terminalLyricIndex);
                };
            }

            // Stop+Start = 重置计时周期（重复点击续期）
            _terminalLyricHoldTimer.Stop();
            _terminalLyricHoldTimer.Start();
        }

        /// <summary>退出选择期（双击跳转 / 切歌 / 重建行时调用）。</summary>
        private void EndTerminalLyricHold()
        {
            _terminalUserScrolling = false;
            _terminalLyricHoldTimer?.Stop();
        }
    }
}
