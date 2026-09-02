using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shapes = Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
// TagLibSharp：包名 TagLibSharp，命名空间 TagLib
using TagLib;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Color = Windows.UI.Color;


namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {

        private async void MultiSelectDownloadLyricButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {

            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            int ok = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                PlaylistItem song = selected[i];
                NowPlayingText.Text = $"正在下载歌词 ({i + 1}/{selected.Count})…";
                string? path = await OnlineMusicApi.SearchAndDownloadLyricAsync(song.Title, song.Artist, song.FilePath);
                if (path != null)
                {
                    ok++;
                }
            }

            NowPlayingText.Text = $"歌词下载完成：{ok}/{selected.Count}";
        
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }}


        internal string GetCurrentLyricTextPublic()
        {
            if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricLines.Count)
            {
                return _lyricLines[_currentLyricIndex].Text;
            }

            return string.Empty;
        }


        private void DesktopLyricsButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_desktopLyricsWindow?.IsLocked == true)
            {
                _desktopLyricsWindow.SetLocked(false);
                e.Handled = true;
            }
        }


        private void SetDesktopLyricsEnabled(bool enabled, bool persistPreference = true)
        {
            _desktopLyricsEnabled = enabled;
            if (_desktopLyricsEnabled)
            {
                EnsureDesktopLyricsWindow();
                _desktopLyricsWindow!.SetLyrics(_lyricLines);
                MediaPlayer? player = GetPlayer();
                if (player?.Source != null)
                {
                    _desktopLyricsWindow.Sync(player.PlaybackSession.Position);
                }

                _desktopLyricsWindow.Show();
            }
            else
            {
                // 用户关闭显示开关前保存位置
                PersistDesktopLyricPosition();
                if (_desktopLyricsWindow != null)
                {
                    DesktopLyricsOverlay closing = _desktopLyricsWindow;
                    _desktopLyricsWindow = null;
                    closing.ClosedByUser -= OnDesktopLyricsClosedByUser;
                    closing.Close();
                    closing.Dispose();
                }
            }

            if (persistPreference)
            {
                AppSettingsStore.Update(s => s.OpenDesktopLyricsOnStartup = _desktopLyricsEnabled);
            }

            UpdateDesktopLyricsBadge();
        }


        private void EnsureDesktopLyricsWindow()
        {
            if (_desktopLyricsWindow != null)
            {
                return;
            }

            _desktopLyricsWindow = new DesktopLyricsOverlay
            {
                // 引擎路径（HiFi 独占 / ASIO / DSD）下 MediaPlayer.PlaybackSession.Position 不是真实播放位置，
                // 必须改读 _audioEngine.Position。否则歌词窗口内部 50ms 定时器拿到的是错误位置，
                // 只能靠外部 200ms 的 Sync 强制推帧 → 实际只有 5fps，进度条一跳一跳地闪。
                PositionProvider = () =>
                    _usingEnginePlayback && _audioEngine != null
                        ? _audioEngine.Position
                        : (GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero)
            };
            _desktopLyricsWindow.ClosedByUser += OnDesktopLyricsClosedByUser;
            _desktopLyricsWindow.ApplySettings(AppSettingsStore.Load());

            // 记忆位置：上次拖到哪，下次仍放到那（否则 PlaceInitially 居中贴底）
            AppSettingsState saved = AppSettingsStore.Load();
            if (saved.DesktopLyricPosX != int.MinValue && saved.DesktopLyricPosY != int.MinValue)
            {
                _desktopLyricsWindow.SetSavedPosition(saved.DesktopLyricPosX, saved.DesktopLyricPosY);
            }
        }


        private void OnDesktopLyricsClosedByUser()
        {
            // 关闭前保存位置，下次打开记忆
            PersistDesktopLyricPosition();
            // 可能从桌面歌词关闭按钮触发；确保主界面 badge 回到 off
            if (_desktopLyricsWindow != null)
            {
                _desktopLyricsWindow.ClosedByUser -= OnDesktopLyricsClosedByUser;
                _desktopLyricsWindow.PositionProvider = null;
                _desktopLyricsWindow.Dispose();
            }

            _desktopLyricsWindow = null;
            _desktopLyricsEnabled = false;
            AppSettingsStore.Update(s => s.OpenDesktopLyricsOnStartup = false);
            DispatcherQueue.TryEnqueue(UpdateDesktopLyricsBadge);
        }


        private void UpdateDesktopLyricsBadge()
        {
            if (DesktopLyricsStateBadge == null)
            {
                return;
            }

            bool on = _desktopLyricsEnabled && _desktopLyricsWindow != null;
            DesktopLyricsStateBadge.Text = on ? "on" : "off";
            DesktopLyricsStateBadge.Foreground = on
                ? new SolidColorBrush(Color.FromArgb(255, 80, 200, 120))
                : new SolidColorBrush(Color.FromArgb(255, 176, 176, 176));
        }


        private void BuildLyricsUi(List<LyricLine> lyrics)
        {
            _lyricLines = lyrics;
            _currentLyricIndex = -1;
            _lyricTextBlocks.Clear();
            LyricsPanel.Children.Clear();

            if (lyrics.Count == 0)
            {
                // 无歌词：右侧歌词区固定显示简短说明
                ClearLyricsUi("该音频没有歌词");
                return;
            }

            LyricsEmptyHint.Visibility = Visibility.Collapsed;
            LyricsScrollViewer.Visibility = Visibility.Visible;

            // 上下垫高，便于首尾句也能滚到中间
            double pad = Math.Max(80, LyricsScrollViewer.ActualHeight / 2);
            LyricsPanel.Padding = new Thickness(8, pad, 8, pad);

            AppSettingsState uiSettings = AppSettingsStore.Load();
            TextAlignment align = uiSettings.LyricAlign switch
            {
                "Left" => TextAlignment.Left,
                "Right" => TextAlignment.Right,
                _ => TextAlignment.Center
            };
            LyricsPanel.Spacing = uiSettings.LyricLineSpacing;

            foreach (LyricLine line in lyrics)
            {
                var tb = new TextBlock
                {
                    TextAlignment = align,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 154, 154, 154)),
                    Opacity = 0.55,
                    Tag = line
                };
                if (line.IsTranslation)
                {
                    // 翻译行：小号、更淡，且不作为主题色高亮的目标
                    tb.FontSize = 12;
                    tb.Opacity = 0.40;
                }
                else
                {
                    tb.Foreground = new SolidColorBrush(Color.FromArgb(255, 154, 154, 154));
                }
                if (line.CharTimes != null && line.CharTimes.Count == line.Text.Length)
                {
                    // 逐字歌词：每字一个 Run，便于按字高亮
                    tb.Text = null;
                    var unplayedBrush = new SolidColorBrush(Color.FromArgb(255, 154, 154, 154));
                    foreach (char c in line.Text)
                    {
                        tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                        {
                            Text = c.ToString(),
                            Foreground = unplayedBrush
                        });
                    }
                }
                else
                {
                    tb.Text = line.Text;
                }

                // 歌词区宽约=面板宽/2-左右留边；给每条歌词设最大宽使其在右半区换行(不依赖 layout 时序)
                double lyricMax = NowPlayingPane != null && NowPlayingPane.ActualWidth > 0
                    ? Math.Max(200, NowPlayingPane.ActualWidth / 2.0 - 76)
                    : 520;
                tb.MaxWidth = lyricMax;

                if (!line.IsTranslation)
                {
                    // 单击歌词行 → 把播放跳到该行时间（对齐"点进度条→seek+暂停"）
                    TimeSpan target = line.Time;
                    tb.Tapped += (_, _) => SeekToLyricLine(target);
                }

                _lyricTextBlocks.Add(tb);
                LyricsPanel.Children.Add(tb);
            }

            SyncLyricsToPosition(GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);
            _desktopLyricsWindow?.SetLyrics(_lyricLines);
            _miniPlayerWindow?.RefreshFromOwner();
        }


        private void ClearLyricsUi(string hint)
        {
            _lyricLines = new List<LyricLine>();
            _currentLyricIndex = -1;
            _lyricTextBlocks.Clear();
            LyricsPanel.Children.Clear();
            LyricsPanel.Padding = new Thickness(0);
            LyricsScrollViewer.Visibility = Visibility.Collapsed;
            LyricsEmptyHint.Text = hint;
            LyricsEmptyHint.Visibility = Visibility.Visible;
            _desktopLyricsWindow?.SetLyrics(_lyricLines);
        }


        // ---- 歌词手动滚动 + 单击跳进度 ----

        /// <summary>视口滚动/拖动：进入"用户手动滚动"状态，暂停自动吸附，若干秒无操作后恢复。</summary>
        private void LyricsScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            // ViewChanged 也会被程序控制触发；仅在用户滚轮/拖动后短暂标记，靠 resume 计时器兜底。
            MarkUserScrollingLyrics();
        }


        private void LyricsScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            MarkUserScrollingLyrics();
        }


        private void LyricsScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            MarkUserScrollingLyrics();
        }


        /// <summary>标记用户正在手动滚动，并在停止 3 秒后恢复自动吸附 + 隐藏滚动条。</summary>
        private void MarkUserScrollingLyrics()
        {
            if (LyricsPanel.Children.Count == 0)
            {
                return;
            }

            _userScrollingLyrics = true;
            // 用户滚动时临时显示滚动条，3s 无操作后隐藏
            LyricsScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            if (_lyricScrollResumeTimer == null)
            {
                _lyricScrollResumeTimer = DispatcherQueue.CreateTimer();
                _lyricScrollResumeTimer.Interval = TimeSpan.FromMilliseconds(3000);
                _lyricScrollResumeTimer.Tick += (_, _) =>
                {
                    _lyricScrollResumeTimer!.Stop();
                    _userScrollingLyrics = false;
                    // 恢复吸附：把当前高亮行滚回中间（即"回到原进度"）
                    if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
                    {
                        ScrollLyricToCenter(_lyricTextBlocks[_currentLyricIndex]);
                    }

                    // 恢复隐藏滚动条
                    LyricsScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                };
            }

            _lyricScrollResumeTimer.Stop();
            _lyricScrollResumeTimer.Start();
        }


        private void ScrollLyricToCenter(FrameworkElement line)
        {
            LyricsScrollViewer.UpdateLayout();
            line.UpdateLayout();

            double viewport = LyricsScrollViewer.ViewportHeight;
            if (viewport <= 0)
            {
                return;
            }

            GeneralTransform transform = line.TransformToVisual(LyricsPanel);
            Windows.Foundation.Point topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double lineCenter = topLeft.Y + line.ActualHeight / 2;
            double target = lineCenter - viewport / 2;
            target = Math.Max(0, Math.Min(target, LyricsScrollViewer.ScrollableHeight));

            _lyricScrollFrom = LyricsScrollViewer.VerticalOffset;
            _lyricScrollTo = target;
            if (Math.Abs(_lyricScrollTo - _lyricScrollFrom) < 0.5)
            {
                return;
            }

            _lyricScrollStartMs = Environment.TickCount64;
            if (_lyricScrollTimer == null)
            {
                _lyricScrollTimer = DispatcherQueue.CreateTimer();
                _lyricScrollTimer.Interval = TimeSpan.FromMilliseconds(8);
                _lyricScrollTimer.Tick += LyricScrollTimer_Tick;
            }

            _lyricScrollTimer.Start();
        }
    }
}
