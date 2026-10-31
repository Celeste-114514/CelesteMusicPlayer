using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 零散播放窗口的歌词（2026-10-18）：扒自主程序播放页的滚动歌词。
    /// 加载器与主程序同一个（<see cref="LyricsLoader"/>：同名 .lrc / 内嵌歌词，
    /// 尊重设置里的来源优先级），这里只自持 UI 与同步。
    /// 规则与主程序一致：当前行高亮、翻译行不高亮只作辅助、当前行滚动到视区中间。
    /// </summary>
    public sealed partial class ScatteredPlayWindow
    {
        private List<LyricLine> _lyricLines = new();
        private readonly List<TextBlock> _lyricBlocks = new();
        private int _currentLyricIndex = -1;

        /// <summary>当前行高亮色（跟随背景三态；与可视化同色系）。</summary>
        private Windows.UI.Color LyricAccentColor => _bgMode == "Light"
            ? Windows.UI.Color.FromArgb(255, 47, 84, 212)
            : Windows.UI.Color.FromArgb(255, 159, 180, 255);

        private static readonly Windows.UI.Color LyricNormalDark = Windows.UI.Color.FromArgb(255, 214, 216, 224);
        private static readonly Windows.UI.Color LyricDimDark = Windows.UI.Color.FromArgb(255, 150, 153, 163);
        private static readonly Windows.UI.Color LyricNormalLight = Windows.UI.Color.FromArgb(255, 58, 58, 64);
        private static readonly Windows.UI.Color LyricDimLight = Windows.UI.Color.FromArgb(255, 128, 130, 140);

        private void ClearLyricsUi()
        {
            _lyricLines = new List<LyricLine>();
            _lyricBlocks.Clear();
            _currentLyricIndex = -1;
            LyricsPanel.Children.Clear();
        }

        /// <summary>按加载结果重建歌词 UI。空列表时显示「没有找到歌词」占位。</summary>
        private void BuildLyricsUi(List<LyricLine> lines)
        {
            ClearLyricsUi();
            _lyricLines = lines ?? new List<LyricLine>();

            if (_lyricLines.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "没有找到歌词",
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 0),
                    Foreground = new SolidColorBrush(
                        _bgMode == "Light" ? LyricDimLight : LyricDimDark)
                };
                LyricsPanel.Children.Add(empty);
                return;
            }

            foreach (LyricLine line in _lyricLines)
            {
                var block = new TextBlock
                {
                    Text = line.Text,
                    FontSize = line.IsTranslation ? 11.5 : 13,
                    Opacity = line.IsTranslation ? 0.75 : 1.0,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(
                        _bgMode == "Light" ? LyricDimLight : LyricDimDark)
                };
                _lyricBlocks.Add(block);
                LyricsPanel.Children.Add(block);
            }

            // 刚建好：按当前播放位置落一次高亮（换歌时位置通常归零）
            _currentLyricIndex = -1;
        }

        /// <summary>按播放位置同步高亮行与滚动位置（250ms tick 调用）。</summary>
        private void SyncLyricsToPosition(TimeSpan position)
        {
            if (_lyricLines.Count == 0 || _lyricBlocks.Count != _lyricLines.Count)
            {
                return;
            }

            // 当前行 = 最后一句「非翻译」且时间点已过的歌词
            int index = -1;
            for (int i = 0; i < _lyricLines.Count; i++)
            {
                if (_lyricLines[i].IsTranslation)
                {
                    continue;
                }

                if (_lyricLines[i].Time <= position)
                {
                    index = i;
                }
                else
                {
                    break;
                }
            }

            if (index == _currentLyricIndex)
            {
                return;
            }

            int previous = _currentLyricIndex;
            _currentLyricIndex = index;

            if (previous >= 0 && previous < _lyricBlocks.Count)
            {
                LyricLine old = _lyricLines[previous];
                _lyricBlocks[previous].Foreground = new SolidColorBrush(
                    _bgMode == "Light"
                        ? (old.IsTranslation ? LyricDimLight : LyricNormalLight)
                        : (old.IsTranslation ? LyricDimDark : LyricNormalDark));
                _lyricBlocks[previous].FontSize = old.IsTranslation ? 11.5 : 13;
                _lyricBlocks[previous].FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }

            if (index < 0 || index >= _lyricBlocks.Count)
            {
                return;
            }

            TextBlock current = _lyricBlocks[index];
            current.Foreground = new SolidColorBrush(LyricAccentColor);
            current.FontSize = 15;
            current.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

            // 滚动到视区中间（只在新行落定时跳一次，不逐帧抢）
            ScrollLyricToCenter(current);
        }

        private void ScrollLyricToCenter(TextBlock block)
        {
            try
            {
                if (LyricsScroll.ViewportHeight <= 1)
                {
                    return;
                }

                var point = block.TransformToVisual(LyricsPanel).TransformPoint(new Windows.Foundation.Point(0, 0));
                double target = point.Y - (LyricsScroll.ViewportHeight - block.ActualHeight) / 2;
                LyricsScroll.ScrollToVerticalOffset(Math.Max(0, target));
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.ScrollLyricToCenter", caught);
            }
        }
    }
}
