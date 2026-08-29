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

        /// <summary>引擎播放位置 → 进度条 / 时间 / 任务栏进度。</summary>
        /// <summary>更新 SMTC timeline 属性（进度/时长），让系统媒体小组件(deskbox)显示当前曲目进度并可 seek。</summary>
        private void UpdateSmtcTimeline(TimeSpan position)
        {
            if (_engineSmtc == null)
            {
                return;
            }

            try
            {
                TimeSpan duration = _audioEngine?.Duration ?? TimeSpan.Zero;
                if (duration <= TimeSpan.Zero)
                {
                    return;
                }

                var props = new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    EndTime = duration,
                    MinSeekTime = TimeSpan.Zero,
                    MaxSeekTime = duration,
                    Position = position
                };
                _engineSmtc.UpdateTimelineProperties(props);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void EnginePositionChanged(TimeSpan position)
        {
            try
            {
                if (!_usingEnginePlayback)
                {
                    return;
                }

                // 用户正在拖动/点击进度条时不覆盖，避免跳动
                if (_isUserSeeking)
                {
                    return;
                }

                // SMTC timeline（系统媒体小组件/deskbox 进度与可 seek）：约 500ms 一次
                if (Environment.TickCount64 - _lastSmtcTimelineMs > 400)
                {
                    _lastSmtcTimelineMs = Environment.TickCount64;
                    UpdateSmtcTimeline(position);
                }

                // 时长变化时同步进度条上限
                double duration = _audioEngine?.Duration.TotalSeconds ?? 0;
                if (duration > 1 && Math.Abs(ProgressSlider.Maximum - duration) > 1)
                {
                    ProgressSlider.Maximum = duration;
                    TotalTimeText.Text = FormatTime(_audioEngine!.Duration);
                }

                double seconds = position.TotalSeconds;
                if (seconds >= 0
                    && seconds <= ProgressSlider.Maximum
                    && Math.Abs(ProgressSlider.Value - seconds) >= 0.05)
                {
                    _isUpdatingProgressUi = true;
                    try
                    {
                        ProgressSlider.Value = seconds;
                    }
                    finally
                    {
                        _isUpdatingProgressUi = false;
                    }
                }

                string timeText = FormatTime(position);
                if (CurrentTimeText != null
                    && !string.Equals(CurrentTimeText.Text, timeText, StringComparison.Ordinal))
                {
                    CurrentTimeText.Text = timeText;
                }

                _desktopLyricsWindow?.Sync(position);
                _miniPlayerWindow?.SyncPosition(position, _audioEngine?.Duration ?? TimeSpan.Zero);
                _taskbarProgress?.SetProgress(position.TotalSeconds, ProgressSlider.Maximum, paused: false);

                // 引擎（HiFi 独占/ASIO）路径也要推进当前歌词行与滚动，
                // 否则歌词不随播放滚动（普通 MediaPlayer 路径由 PositionTimer_Tick 调用）。
                SyncLyricsToPosition(position);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>停止引擎播放并复位相关 UI 状态（切到普通格式时调用）。</summary>
        private void StopEngineIfActive()
        {
            if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
            {
                _audioEngine.PositionChanged -= EnginePositionChanged;
                _audioEngine.Stop();
                _isEnginePaused = false;
                _usingEnginePlayback = false;
                UpdateWaveformTimerForPlaybackState(false);
                UpdateEngineSmtcStatus(MediaPlaybackStatus.Stopped);
                _miniPlayerWindow?.RefreshFromOwner();
            }
        }


        private void PlayNext()
            => PlayNext(autoAdvance: false);

        private void PlayNext(bool autoAdvance)
        {
            // 优先按播放队列续播；若队列为空（例如直接从媒体库/文件夹双击播放），
            // 则按媒体库当前列表 (_playlist) 顺序推进，保证播完能自动连续下一首。
            if (_userPlaylist.Count > 0)
            {
                int? nextIndex = ResolveNextIndex(autoAdvance);
                if (nextIndex != null)
                {
                    PlayUserPlaylistAt(nextIndex.Value);
                }

                return;
            }

            AdvanceInLibraryPlaylist(autoAdvance);
        }


        /// <summary>队列为空时，从媒体库当前列表推进到下一首（按播放顺序）。</summary>
        private void AdvanceInLibraryPlaylist(bool autoAdvance)
        {
            if (_playlist.Count == 0 || _currentIndex < 0)
            {
                return;
            }

            switch (_playbackOrder)
            {
                case PlaybackOrder.TrackOnce:
                    return; // 单曲播放模式：不自动续播

                case PlaybackOrder.TrackLoop:
                    PlayLibraryItemAt(_currentIndex, syncUserPlaylistIndex: false);
                    return;

                case PlaybackOrder.Sequential:
                    if (_currentIndex + 1 < _playlist.Count)
                    {
                        PlayLibraryItemAt(_currentIndex + 1, syncUserPlaylistIndex: false);
                    }

                    return;

                case PlaybackOrder.Random:
                    if (_playlist.Count > 1)
                    {
                        int r = _playbackRandom.Next(_playlist.Count);
                        if (r == _currentIndex)
                        {
                            r = (r + 1) % _playlist.Count;
                        }

                        PlayLibraryItemAt(r, syncUserPlaylistIndex: false);
                    }
                    else if (_playlist.Count == 1)
                    {
                        PlayLibraryItemAt(_currentIndex, syncUserPlaylistIndex: false);
                    }

                    return;

                default: // ListLoop 等：循环到列表尾后回到开头
                    PlayLibraryItemAt((_currentIndex + 1) % _playlist.Count, syncUserPlaylistIndex: false);
                    return;
            }
        }


        private void PlayPrevious()
        {
            if (_userPlaylist.Count == 0)
            {
                return;
            }

            int? prevIndex = ResolvePreviousIndex();
            if (prevIndex == null)
            {
                return;
            }

            PlayUserPlaylistAt(prevIndex.Value);
        }


        private int? ResolveNextIndex(bool autoAdvance)
        {
            int count = _userPlaylist.Count;
            if (count == 0)
            {
                return null;
            }

            int baseIndex = _userPlaylistIndex >= 0 ? _userPlaylistIndex : 0;

            switch (_playbackOrder)
            {
                case PlaybackOrder.TrackOnce:
                    if (autoAdvance)
                    {
                        return null;
                    }
                    return baseIndex + 1 < count ? baseIndex + 1 : null;

                case PlaybackOrder.TrackLoop:
                    if (autoAdvance)
                    {
                        return baseIndex;
                    }
                    return baseIndex + 1 < count ? baseIndex + 1 : 0;

                case PlaybackOrder.Sequential:
                {
                    int next = baseIndex + 1;
                    return next >= count ? null : next;
                }

                case PlaybackOrder.ListLoop:
                    return (baseIndex + 1) % count;

                case PlaybackOrder.Random:
                {
                    if (count == 1)
                    {
                        return 0;
                    }

                    int next = _playbackRandom.Next(count);
                    if (next == baseIndex)
                    {
                        next = (next + 1) % count;
                    }
                    return next;
                }

                default:
                    return (baseIndex + 1) % count;
            }
        }


        /// <summary>按当前播放顺序解析下一个播放项的索引（不修改任何状态）。用于无缝预加载预测，
        /// 与 PlayNext 的切歌决策保持一致（修复随机播放时自动切歌仍按列表顺序的问题）。</summary>
        private int NextIndexByOrder(int count, int baseIndex, bool autoAdvance)
        {
            if (count <= 0)
            {
                return -1;
            }

            switch (_playbackOrder)
            {
                case PlaybackOrder.TrackOnce:
                    if (autoAdvance)
                    {
                        return -1; // 单曲只播一遍，不自动续播
                    }
                    return baseIndex + 1 < count ? baseIndex + 1 : -1;

                case PlaybackOrder.TrackLoop:
                    if (autoAdvance)
                    {
                        return baseIndex;
                    }
                    return baseIndex + 1 < count ? baseIndex + 1 : 0;

                case PlaybackOrder.Sequential:
                    return baseIndex + 1 < count ? baseIndex + 1 : -1;

                case PlaybackOrder.Random:
                    if (count == 1)
                    {
                        return baseIndex;
                    }
                    {
                        int next = _playbackRandom.Next(count);
                        return next == baseIndex ? (next + 1) % count : next;
                    }

                default: // ListLoop 等：循环到列表尾回到开头
                    return (baseIndex + 1) % count;
            }
        }


        private int? ResolvePreviousIndex()
        {
            int count = _userPlaylist.Count;
            if (count == 0)
            {
                return null;
            }

            int baseIndex = _userPlaylistIndex >= 0 ? _userPlaylistIndex : 0;

            switch (_playbackOrder)
            {
                case PlaybackOrder.Sequential:
                case PlaybackOrder.TrackOnce:
                    return baseIndex > 0 ? baseIndex - 1 : null;

                case PlaybackOrder.Random:
                case PlaybackOrder.ListLoop:
                case PlaybackOrder.TrackLoop:
                default:
                    return baseIndex <= 0 ? count - 1 : baseIndex - 1;
            }
        }


        // =====================================================================
        // 播放顺序按钮
        // =====================================================================

        private void PlaybackOrderButton_Click(object sender, RoutedEventArgs e)
        {
            PlaybackOrder[] order =
            {
                PlaybackOrder.Sequential,
                PlaybackOrder.Random,
                PlaybackOrder.ListLoop,
                PlaybackOrder.TrackLoop,
                PlaybackOrder.TrackOnce
            };

            int index = Array.IndexOf(order, _playbackOrder);
            int next = index < 0 ? 0 : (index + 1) % order.Length;
            SetPlaybackOrder(order[next]);
        }


        private void PlaybackOrderButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowPlaybackOrderMenu(PlaybackOrderButton, e.GetPosition(PlaybackOrderButton));
        }


        private void ShowPlaybackOrderMenu(FrameworkElement target, Windows.Foundation.Point position)
        {
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Top };

            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.Sequential, "\uE8FD", "顺序播放");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.Random, "\uE8B1", "随机播放");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.ListLoop, "\uE8EE", "列表循环");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.TrackLoop, "\uE8ED", "单曲循环");
            AddPlaybackOrderMenuItem(flyout, PlaybackOrder.TrackOnce, "\uE72A", "单曲播放");

            flyout.ShowAt(target, new FlyoutShowOptions
            {
                Position = position,
                Placement = FlyoutPlacementMode.Top
            });
        }


        private void AddPlaybackOrderMenuItem(MenuFlyout flyout, PlaybackOrder order, string glyph, string label)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = label,
                Icon = new FontIcon { Glyph = glyph, FontSize = 16 },
                IsChecked = _playbackOrder == order,
                Tag = order
            };
            item.Click += PlaybackOrderMenuItem_Click;
            flyout.Items.Add(item);
        }


        private void PlaybackOrderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem { Tag: PlaybackOrder order })
            {
                SetPlaybackOrder(order);
            }
        }


        private void SetPlaybackOrder(PlaybackOrder order, bool persist = true)
        {
            _playbackOrder = order;
            ApplyPlaybackOrderToPlayer();
            UpdatePlaybackOrderButtonUi();
            _miniPlayerWindow?.RefreshFromOwner();
            if (persist)
            {
                AppSettingsStore.Update(s => s.PlaybackOrder = order.ToString());
            }
        }


        private void ApplyPlaybackOrderToPlayer()
        {
            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            player.IsLoopingEnabled = _playbackOrder == PlaybackOrder.TrackLoop;
        }


        private void UpdatePlaybackOrderButtonUi()
        {
            bool trackOnce = _playbackOrder == PlaybackOrder.TrackOnce;
            PlaybackOrderIcon.Visibility = trackOnce ? Visibility.Collapsed : Visibility.Visible;
            PlaybackOrderTrackOnceGlyph.Visibility = trackOnce ? Visibility.Visible : Visibility.Collapsed;

            (string glyph, string name) = _playbackOrder switch
            {
                PlaybackOrder.Sequential => ("\uE8FD", "顺序播放"),
                PlaybackOrder.Random => ("\uE8B1", "随机播放"),
                PlaybackOrder.ListLoop => ("\uE8EE", "列表循环"),
                PlaybackOrder.TrackLoop => ("\uE8ED", "单曲循环"),
                PlaybackOrder.TrackOnce => ("\uE72A", "单曲播放"),
                _ => ("\uE8EE", "列表循环")
            };

            if (!trackOnce)
            {
                PlaybackOrderIcon.Glyph = glyph;
            }

            ToolTipService.SetToolTip(PlaybackOrderButton, name + "（左键切换，右键选择）");
        }


        // =====================================================================
        // 主区域：播放列表 / 右侧 可拖分割线
        // =====================================================================

        /// <summary>播放列表区域尺寸变化：保证各列完整可见且不过度拉宽。</summary>
        private void PlaylistListBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            {
                return;
            }

            FitColumnsToAvailableWidth();
        }


        private double GetPlaylistColumnsViewportWidth()
        {
            double border = PlaylistListBorder.ActualWidth;
            if (border > 0)
            {
                // Header Margin="4,4,4,4"
                return Math.Max(0, border - 8);
            }

            if (PlaylistHeaderGrid.ActualWidth > 0)
            {
                return PlaylistHeaderGrid.ActualWidth;
            }

            return PlaylistColumn.ActualWidth > 0 ? Math.Max(0, PlaylistColumn.ActualWidth - 24) : 0;
        }


        // =====================================================================
        // 单元格悬停 1 秒后显示该字段完整信息
        // =====================================================================

        private void PlaylistCell_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
            {
                return;
            }

            HideHoverTip();
            _hoverElement = element;
            _hoverTipText = ResolveCellDetailText(element);

            if (string.IsNullOrWhiteSpace(_hoverTipText) || _hoverTipTimer == null)
            {
                return;
            }

            _hoverTipTimer.Stop();
            _hoverTipTimer.Start();
        }


        private void PlaylistCell_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // 保留事件：移动时不重置 1 秒计时，避免轻微抖动导致提示永远不出
        }


        private void PlaylistCell_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _hoverTipTimer?.Stop();
            HideHoverTip();
            _hoverElement = null;
            _hoverTipText = null;
        }


        private void HoverTipTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            if (_hoverElement == null || string.IsNullOrWhiteSpace(_hoverTipText))
            {
                return;
            }

            HideHoverTip();

            _activeHoverTip = new ToolTip
            {
                Content = _hoverTipText,
                Placement = PlacementMode.Mouse
            };
            ToolTipService.SetToolTip(_hoverElement, _activeHoverTip);
            _activeHoverTip.IsOpen = true;
        }


    }
}
