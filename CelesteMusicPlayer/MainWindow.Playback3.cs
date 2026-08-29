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

        private void RefreshRealizedSongListSelectionChrome(ListView list)
        {
            HashSet<object>? selectedSet = BuildSelectedItemsLookup(list);
            foreach (ListViewItem container in EnumerateRealizedListViewItems(list))
            {
                if (list.ItemFromContainer(container) is PlaylistItem song)
                {
                    ApplySongListItemSelectionChrome(list, container, song, selectedSet);
                }
            }
        }


        private void ApplySongListItemSelectionChrome(
            ListView list,
            ListViewItem container,
            PlaylistItem song,
            HashSet<object>? selectedSet = null)
        {
            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ResolveContrastingForeground(accent);
            bool multiOnThisList = _isMultiSelectMode && ReferenceEquals(_multiSelectTargetList, list);
            Brush unselectedBg = multiOnThisList
                ? CreateMultiSelectFrostBrush()
                : new SolidColorBrush(Colors.Transparent);

            // 容器本身保持透明，避免 Presenter 方角选中层
            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            bool selected = multiOnThisList
                ? IsItemSelected(list, song, selectedSet)
                : ReferenceEquals(list.SelectedItem, song);

            Border? chrome = FindTaggedBorder(container, "SongRowChrome");
            if (chrome != null)
            {
                chrome.MinHeight = 40;
                chrome.CornerRadius = new CornerRadius(8);
                chrome.VerticalAlignment = VerticalAlignment.Stretch;
                // 让行内容横向铺满列表宽度（选中矩形也因此铺满整行、字段对齐表头）。
                // 用显式宽度而非仅靠 HorizontalContentAlignment，确保在 ScrollViewer 布局下也生效。
                if (list.ActualWidth > 0)
                {
                    chrome.Width = list.ActualWidth;
                }
                if (selected)
                {
                    chrome.Background = accent;
                    ApplyForegroundToDescendants(chrome, selectedFg);
                }
                else
                {
                    chrome.Background = unselectedBg;
                    ClearForegroundOnDescendants(chrome);
                }
            }
            else if (selected)
            {
                // 兜底：无模板 Border 时仍尽量圆角
                container.Background = accent;
                container.Foreground = selectedFg;
                ApplyForegroundToDescendants(container, selectedFg);
            }
            else
            {
                container.Background = unselectedBg;
                container.ClearValue(Control.ForegroundProperty);
                ClearForegroundOnDescendants(container);
            }
        }


        private void ApplyPlaylistItemSelectionChrome(ListViewItem container, PlaylistItem song)
            => ApplySongListItemSelectionChrome(PlaylistView, container, song);

        private static Border? FindTaggedBorder(DependencyObject root, string tag)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is Border border
                    && border.Tag is string t
                    && string.Equals(t, tag, StringComparison.Ordinal))
                {
                    return border;
                }

                Border? nested = FindTaggedBorder(child, tag);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }


        // =====================================================================
        // 底部控制按钮
        // =====================================================================

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_audioEngine?.IsPlaying == true)
            {
                _audioEngine.Pause();
                _isEnginePaused = true;
                UpdateWaveformTimerForPlaybackState(false);
                UpdateEngineSmtcStatus(MediaPlaybackStatus.Paused);
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Glyph = "\uE768";
                }

                // 任务栏缩略图按钮：暂停 → 显示"播放"图标（提示用户点播放）
                _taskbarButtons?.UpdatePlayPause(false);

                _miniPlayerWindow?.RefreshFromOwner();
                return;
            }

            if (_isEnginePaused && _audioEngine != null)
            {
                _audioEngine.Resume();
                // 暂停-恢复本质是一次 seek 重建会话，无缝源 _next 已被清空；
                // 重新预加载下一首，避免恢复后播到尾因无续接而误切歌。
                if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
                {
                    _ = PreloadSeamlessNextAsync(_userPlaylist[_userPlaylistIndex]);
                }

                _isEnginePaused = false;
                UpdateWaveformTimerForPlaybackState(true);
                UpdateEngineSmtcStatus(MediaPlaybackStatus.Playing);
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Glyph = "\uE769";
                }

                // 任务栏缩略图按钮：播放中 → 显示"暂停"图标（提示用户点暂停）
                _taskbarButtons?.UpdatePlayPause(true);

                // 独占下音量完全由 Windows 托盘/DAC 物理键控制，程序不写设备主音量(避免多次 select/暂停音量跳变到 0/100)；
                // 仅共享模式在恢复时做软件增益淡入。
                if (!IsHiFiModeSelected())
                {
                    double resumeTarget = VolumeSlider.Value / 100.0;
                    _ = FadeInEngineAfterResumeAsync(resumeTarget);
                }

                _miniPlayerWindow?.RefreshFromOwner();
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            if (player.Source == null)
            {
                if (_userPlaylist.Count > 0)
                {
                    PlayUserPlaylistAt(0);
                }
                else if (_playlist.Count > 0)
                {
                    PlayAtIndex(0);
                }

                return;
            }

            bool wasPlaying = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            if (wasPlaying)
            {
                player.Pause();
                _taskbarButtons?.UpdatePlayPause(false);   // 暂停后 → 显示"播放"图标
            }
            else
            {
                player.Play();
                _taskbarButtons?.UpdatePlayPause(true);    // 播放后 → 显示"暂停"图标
            }
        }


        private void PreviousButton_Click(object sender, RoutedEventArgs e)
            => PlayPrevious();

        private void NextButton_Click(object sender, RoutedEventArgs e)
            => PlayNext();

        // =====================================================================
        // 进度条 / 音量
        // =====================================================================

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isUserSeeking = true;
        }


        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            SeekToSliderValue();
            _isUserSeeking = false;
        }


        private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            SeekToSliderValue();
            _isUserSeeking = false;
        }


        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // 波形进度条必须随播放位置实时重绘,不能被 UI 更新标志拦截(否则播放中波形停住/错误)
            if (_progressBarStyle == "Waveform")
            {
                RedrawProgressStyle();
            }

            if (_isUpdatingProgressUi)
            {
                return;
            }

            if (_isUserSeeking)
            {
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            }
        }


        private void SeekToSliderValue()
        {
            if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
            {
                try
                {
                    _audioEngine.Seek(TimeSpan.FromSeconds(ProgressSlider.Value));
                    // seek 会丢弃无缝源里已预加载的下一首（位置已变，续接会错位）。
                    // 重挂下一首，避免 seek 后播到尾时因 _next 为空而无法无缝续接。
                    if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
                    {
                        _ = PreloadSeamlessNextAsync(_userPlaylist[_userPlaylistIndex]);
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                // 用户点击进度条定位后：按设置决定「跳转并继续播放」或「跳转并暂停」。
                if (_audioEngine.IsPlaying
                    && AppSettingsStore.Load().ProgressBarClickBehavior != "SeekAndPlay")
                {
                    _audioEngine.Pause();
                    _isEnginePaused = true;
                    UpdateWaveformTimerForPlaybackState(false);
                    UpdateEngineSmtcStatus(MediaPlaybackStatus.Paused);
                    if (PlayPauseIcon != null)
                    {
                        PlayPauseIcon.Glyph = "\uE768";
                    }

                    _miniPlayerWindow?.RefreshFromOwner();
                }

                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player?.Source == null)
            {
                return;
            }

            try
            {
                player.PlaybackSession.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            UpdateVolumeIcon(e.NewValue);

            // 共享模式：MediaPlayer 数字音量（跟随用户）。
            MediaPlayer? player = GetPlayer();
            if (player != null)
            {
                player.Volume = e.NewValue / 100.0;
            }

            // 设备/引擎音量：独占下软件音量条固定 100%，且程序不设设备主音量（bit-perfect 直通，实际音量由系统托盘控制）；
            // 共享沿用原机制（数字增益随滑块）。
            if (!IsHiFiModeSelected())
            {
                _audioEngine?.SetVolume(e.NewValue / 100.0);
            }

            if (!_applyingSettingsVolume)
            {
                ScheduleVolumeSave(e.NewValue);
            }

            DrawVolumeStyle();
        }


        /// <summary>音量写盘去抖:停止拖动 300ms 后才保存一次,避免每 tick 全量写盘。</summary>
        private void ScheduleVolumeSave(double value)
        {
            _volumeToSave = value;
            _volumeSaveTimer ??= DispatcherQueue.CreateTimer();
            _volumeSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
            _volumeSaveTimer.IsRepeating = false;
            _volumeSaveTimer.Tick -= OnVolumeSaveTick;
            _volumeSaveTimer.Tick += OnVolumeSaveTick;
            _volumeSaveTimer.Start();
        }


        private void OnVolumeSaveTick(DispatcherQueueTimer sender, object args)
        {
            try
            {
                AppSettingsStore.Update(s => s.Volume = _volumeToSave);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void UpdateVolumeIcon(double volumePercent)
        {
            if (VolumeIcon == null)
            {
                return;
            }

            // E74F mute / E992 low / E993 mid / E767 high
            if (volumePercent <= 0.5)
            {
                VolumeIcon.Glyph = "\uE74F";
            }
            else if (volumePercent < 34)
            {
                VolumeIcon.Glyph = "\uE992";
            }
            else if (volumePercent < 67)
            {
                VolumeIcon.Glyph = "\uE993";
            }
            else
            {
                VolumeIcon.Glyph = "\uE767";
            }
        }


        private void DesktopLyricsButton_Click(object sender, RoutedEventArgs e)
            => SetDesktopLyricsEnabled(!_desktopLyricsEnabled);

        private void MiniPlayerButton_Click(object sender, RoutedEventArgs e)
            => SetMiniPlayerEnabled(!_miniPlayerEnabled);

        private void SetMiniPlayerEnabled(bool enabled, bool persistPreference = true)
        {
            _miniPlayerEnabled = enabled;
            if (_miniPlayerEnabled)
            {
                EnsureMiniPlayerWindow();
                AppSettingsState settings = AppSettingsStore.Load();
                _miniPlayerWindow!.SetAlwaysOnTop(settings.MiniPlayerAlwaysOnTop);
                _miniPlayerWindow.ApplyBackdropPreference(settings.EnableFrostedGlass);
                _miniPlayerWindow.RefreshFromOwner();
                _miniPlayerWindow.Activate();
            }
            else if (_miniPlayerWindow != null)
            {
                MiniPlayerWindow closing = _miniPlayerWindow;
                _miniPlayerWindow = null;
                closing.ClosedByUser -= OnMiniPlayerClosedByUser;
                closing.Close();
            }

            if (persistPreference)
            {
                AppSettingsStore.Update(s => s.OpenMiniPlayerOnStartup = _miniPlayerEnabled);
            }

            UpdateMiniPlayerBadge();
        }


        private void EnsureMiniPlayerWindow()
        {
            if (_miniPlayerWindow != null)
            {
                return;
            }

            _miniPlayerWindow = new MiniPlayerWindow(this);
            AppSettingsState settings = AppSettingsStore.Load();
            _miniPlayerWindow.SetAlwaysOnTop(settings.MiniPlayerAlwaysOnTop);
            _miniPlayerWindow.ApplyBackdropPreference(settings.EnableFrostedGlass);
            _miniPlayerWindow.ClosedByUser += OnMiniPlayerClosedByUser;
        }


        private void OnMiniPlayerClosedByUser()
        {
            _miniPlayerWindow = null;
            _miniPlayerEnabled = false;
            AppSettingsStore.Update(s => s.OpenMiniPlayerOnStartup = false);
            DispatcherQueue.TryEnqueue(UpdateMiniPlayerBadge);
        }


        private void UpdateMiniPlayerBadge()
        {
            if (MiniPlayerStateBadge == null)
            {
                return;
            }

            bool on = _miniPlayerEnabled && _miniPlayerWindow != null;
            MiniPlayerStateBadge.Text = on ? "on" : "off";
            MiniPlayerStateBadge.Foreground = on
                ? new SolidColorBrush(Color.FromArgb(255, 80, 200, 120))
                : new SolidColorBrush(Color.FromArgb(255, 176, 176, 176));
        }


        // ---- Mini player / desktop lyrics helpers ----

        internal PlaylistItem? GetCurrentPlayingItem()
        {
            if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
            {
                return _userPlaylist[_userPlaylistIndex];
            }

            if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                return _playlist[_currentIndex];
            }

            return null;
        }


        internal ImageSource? GetCurrentCoverImage() => TransportCoverImage?.Source;

        internal MediaPlayer? GetMediaPlayerPublic() => GetPlayer();

        internal string GetPlaybackOrderGlyphPublic()
            => _playbackOrder switch
            {
                PlaybackOrder.Sequential => "\uE8FD",
                PlaybackOrder.Random => "\uE8B1",
                PlaybackOrder.ListLoop => "\uE8EE",
                PlaybackOrder.TrackLoop => "\uE8ED",
                PlaybackOrder.TrackOnce => "\uE72A",
                _ => "\uE8EE"
            };


        internal void CyclePlaybackOrderPublic() => PlaybackOrderButton_Click(PlaybackOrderButton!, new RoutedEventArgs());

        internal void PreviousPublic() => PlayPrevious();

        internal void NextPublic() => PlayNext();

        internal void TogglePlayPausePublic() => PlayPauseButton_Click(PlayPauseButton!, new RoutedEventArgs());

        /// <summary>把当前播放曲目加入/移出"我喜欢的音乐"（托盘/菜单/任务栏缩略图复用）。
        /// 点击行为：未收藏 → 加入；已收藏 → 移除。托盘和任务栏共用同一份逻辑。</summary>
        internal void FavoriteCurrentPublic()
        {
            string? path = _nowPlayingPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
                {
                    path = _userPlaylist[_userPlaylistIndex].FilePath;
                }
                else if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    path = _playlist[_currentIndex].FilePath;
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                StartupLog.Write("[托盘] 收藏当前：无正在播放曲目");
                return;
            }

            bool fav = TrackStatsStore.ToggleFavorite(path);
            NamedPlaylistStore.SyncFavoritesPlaylist();
            UpdateFavoriteButtonUi();
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                ApplyCategoryView();
            }

            if (NowPlayingText != null)
            {
                NowPlayingText.Text = fav ? "已添加到我喜欢的音乐" : "已取消喜欢";
            }

            // 任务栏缩略图按钮：fav=true 实心红心 / fav=false 空心轮廓心
            _taskbarButtons?.UpdateFavorite(fav);

            StartupLog.Write("[托盘] " + (fav ? "已收藏 " : "已取消收藏 ") + path);
        }


        internal void SeekPublic(TimeSpan position)
        {
            MediaPlayer? player = GetPlayer();
            if (player?.Source == null)
            {
                return;
            }

            try
            {
                player.PlaybackSession.Position = position;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void PersistDesktopLyricPosition()
        {
            try
            {
                var w = _desktopLyricsWindow;
                if (w == null || !w.IsVisible)
                {
                    return;
                }

                var (px, py) = w.CurrentPosition;
                AppSettingsStore.Update(s =>
                {
                    s.DesktopLyricPosX = px;
                    s.DesktopLyricPosY = py;
                });
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        // =====================================================================
        // MediaPlayer 事件
        // =====================================================================

        private void Player_MediaOpened(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TimeSpan duration = sender.PlaybackSession.NaturalDuration;

                if ((duration.TotalSeconds <= 0 || double.IsNaN(duration.TotalSeconds))
                    && _currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    duration = _playlist[_currentIndex].Duration;
                }

                _isUpdatingProgressUi = true;
                try
                {
                    double totalSeconds = duration.TotalSeconds;
                    if (totalSeconds <= 0 || double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds))
                    {
                        ProgressSlider.Maximum = 100;
                        TotalTimeText.Text = "00:00";
                    }
                    else
                    {
                        ProgressSlider.Maximum = totalSeconds;
                        TotalTimeText.Text = FormatTime(duration);
                    }

                    double start = 0;
                    if (_pendingRestorePositionSeconds is double pending && pending > 0.5)
                    {
                        start = Math.Min(pending, Math.Max(0, totalSeconds - 0.5));
                        _pendingRestorePositionSeconds = null;
                        try
                        {
                            sender.PlaybackSession.Position = TimeSpan.FromSeconds(start);
                        }
                        catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                    }

                    ProgressSlider.Value = start;
                    CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(start));
                }
                finally
                {
                    _isUpdatingProgressUi = false;
                }
            });
        }


        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // E768=播放，E769=暂停
                bool playing = sender.PlaybackState == MediaPlaybackState.Playing;
                PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
                UpdateWaveformTimerForPlaybackState(playing);
                _desktopLyricsWindow?.SetPlaybackPaused(!playing && sender.PlaybackState != MediaPlaybackState.Opening);
            });
        }


        private void UpdateWaveformTimerForPlaybackState(bool playing)
        {
            if (_waveformTimer == null)
            {
                return;
            }

            if (playing)
            {
                _waveformIdleSettleTicks = 0;
                if (!_waveformTimer.IsRunning)
                {
                    _waveformTimer.Start();
                }
            }
            else if (!_waveformTimer.IsRunning)
            {
                DrawWaveformBars();
            }
            // 暂停/停止：若定时器仍在跑，由 Tick 做回落动画后自行 Stop
        }


        private void Player_MediaEnded(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(HandleMediaEnded);
        }


        private void Player_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                NowPlayingText.Text = "播放失败";
                if (AppSettingsStore.Load().StopWhenError)
                {
                    try
                    {
                        sender.Pause();
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                await ShowErrorAsync("无法播放", args.ErrorMessage);
            });
        }


        private void UpdateTaskbarProgress(TimeSpan position)
        {
            if (!AppSettingsStore.Load().ShowTaskbarProgress)
            {
                _taskbarProgress?.Clear();
                return;
            }

            if (_mainWindowHwnd == IntPtr.Zero)
            {
                return;
            }

            _taskbarProgress ??= new TaskbarProgressHelper(_mainWindowHwnd);
            MediaPlayer? player = GetPlayer();
            bool paused = player?.PlaybackSession.PlaybackState != MediaPlaybackState.Playing;
            _taskbarProgress.SetProgress(position.TotalSeconds, ProgressSlider.Maximum, paused);
        }


        private void PositionTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (_usingEnginePlayback)
            {
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player?.Source == null || _isUserSeeking)
            {
                return;
            }

            TimeSpan position = player.PlaybackSession.Position;
            UpdateTaskbarProgress(position);

            _isUpdatingProgressUi = true;
            try
            {
                double seconds = position.TotalSeconds;
                if (seconds <= ProgressSlider.Maximum
                    && Math.Abs(ProgressSlider.Value - seconds) >= 0.05)
                {
                    ProgressSlider.Value = seconds;
                }

                string timeText = FormatTime(position);
                if (!string.Equals(CurrentTimeText.Text, timeText, StringComparison.Ordinal))
                {
                    CurrentTimeText.Text = timeText;
                }
            }
            finally
            {
                _isUpdatingProgressUi = false;
            }

            SyncLyricsToPosition(position);
            _desktopLyricsWindow?.Sync(position);
            _miniPlayerWindow?.SyncPosition(position, player.PlaybackSession.NaturalDuration);
            TickFeaturePlaybackExtras(position);

            if ((DateTime.UtcNow - _lastPlaybackPersistUtc).TotalSeconds >= 4)
            {
                _lastPlaybackPersistUtc = DateTime.UtcNow;
                PersistPlaybackSession();
            }
        }


        // =====================================================================
        // 右侧：正在播放信息 / 波形 / 歌词
        // =====================================================================

                private void ProgressStyleCanvas_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            RedrawProgressStyle();
        }


        /// <summary>按设置的样式重绘进度条(4 种可切换)。</summary>
        private void RedrawProgressStyle()
        {
            if (ProgressStyleCanvas == null || ProgressSlider == null)
            {
                return;
            }

            bool waveform = _progressBarStyle == "Waveform";
            if (!waveform)
            {
                // 默认样式:恢复系统进度条(主题色跟随主题设置)
                ProgressSlider.Opacity = 1;
                ProgressStyleCanvas.Visibility = Visibility.Collapsed;
                ProgressStyleCanvas.Children.Clear();
                return;
            }

            // 波形进度条:底层 Slider 透明(交互保留),自绘画布显示波形
            ProgressSlider.Opacity = 0;
            ProgressStyleCanvas.Visibility = Visibility.Visible;

            double max = ProgressSlider.Maximum;
            double ratio = max > 0 ? ProgressSlider.Value / max : 0;
            ratio = Math.Clamp(ratio, 0, 1);

            var canvas = ProgressStyleCanvas;
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            Color accent = ResolveAccentColor();
            DrawWaveformStyle(canvas, w, h, ratio, accent);
        }


        private static Color Lighten(Color c, double t)
            => Color.FromArgb(255, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

        private static Color Darken(Color c, double t)
            => Color.FromArgb(255, (byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

        /// <summary>渐变光晕：渐变填充 + 圆角轨道 + 光晕滑块。</summary>
        private void DrawGradientStyle(Canvas canvas, double w, double h, double ratio, Color accent, bool hasSong)
        {
            Color light = Lighten(accent, 0.55);
            Color dark = Darken(accent, 0.35);

            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 4,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 4) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle { Width = Math.Max(2, w * ratio), Height = 4, RadiusX = 2, RadiusY = 2 };
                var grad = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0.5),
                    EndPoint = new Windows.Foundation.Point(1, 0.5)
                };
                grad.GradientStops.Add(new GradientStop { Color = accent, Offset = 0 });
                grad.GradientStops.Add(new GradientStop { Color = light, Offset = 1 });
                fill.Fill = grad;
                Canvas.SetTop(fill, (h - 4) / 2);
                canvas.Children.Add(fill);
            }

            if (hasSong)
            {
                double cx = Math.Clamp(w * ratio, 8, Math.Max(8, w - 8));
                var glow = new Shapes.Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B))
                };
                Canvas.SetLeft(glow, cx - 8);
                Canvas.SetTop(glow, (h - 16) / 2);
                canvas.Children.Add(glow);

                var dot = new Shapes.Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Stroke = new SolidColorBrush(dark),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetLeft(dot, cx - 5);
                Canvas.SetTop(dot, (h - 10) / 2);
                canvas.Children.Add(dot);
            }
        }


        /// <summary>Spotify 圆环：细轨道 + 白色圆环滑块。</summary>
        private void DrawSpotifyStyle(Canvas canvas, double w, double h, double ratio, Color accent, bool hasSong)
        {
            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 3) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle
                {
                    Width = Math.Max(2, w * ratio),
                    Height = 3,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetTop(fill, (h - 3) / 2);
                canvas.Children.Add(fill);
            }

            if (hasSong)
            {
                double cx = Math.Clamp(w * ratio, 6, Math.Max(6, w - 6));
                var ring = new Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(180, accent.R, accent.G, accent.B))
                };
                Canvas.SetLeft(ring, cx - 6);
                Canvas.SetTop(ring, (h - 12) / 2);
                canvas.Children.Add(ring);
            }
        }


        /// <summary>Apple 细线：2px 细线 + 圆点滑块。</summary>
        private void DrawAppleLineStyle(Canvas canvas, double w, double h, double ratio, Color accent, bool hasSong)
        {
            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 2) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle
                {
                    Width = Math.Max(2, w * ratio),
                    Height = 2,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetTop(fill, (h - 2) / 2);
                canvas.Children.Add(fill);
            }

            if (hasSong)
            {
                double cx = Math.Clamp(w * ratio, 4, Math.Max(4, w - 4));
                var dot = new Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(accent)
                };
                Canvas.SetLeft(dot, cx - 4);
                Canvas.SetTop(dot, (h - 8) / 2);
                canvas.Children.Add(dot);
            }
        }


        /// <summary>播放列表内容变化时,若未播放则尝试预览波形(列表恢复完成后自动触发)。</summary>
        private void OnPlaylistForWaveformPreview(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            TryLoadWaveformPreview();
        }


        /// <summary>未播放时加载列表选中(或第一首)歌曲的波形预览。</summary>
        private void TryLoadWaveformPreview()
        {
            if (_progressBarStyle != "Waveform" || !string.IsNullOrEmpty(_nowPlayingPath))
            {
                return;
            }

            PlaylistItem? item = PlaylistView.SelectedItem as PlaylistItem;
            if (item == null && _playlist.Count > 0)
            {
                item = _playlist[0];
            }

            if (item != null
                && !string.Equals(_waveformPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadWaveformForCurrentAsync(item.FilePath);
            }
        }


        /// <summary>延迟重试:等媒体库异步恢复完成后再尝试加载预览波形。</summary>
        private async System.Threading.Tasks.Task RetryWaveformPreviewLaterAsync()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(1200);
                TryLoadWaveformPreview();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


                /// <summary>应用播放列表列显隐与密度。</summary>
        private void ApplyPlaylistColumnSettings(AppSettingsState settings)
        {
            try
            {
                var cols = PlaylistColumnWidths.Instance;
                cols.Title = settings.ShowPlaylistTitle ? 140 : 0;
                cols.Artist = settings.ShowPlaylistArtist ? 110 : 0;
                cols.Album = settings.ShowPlaylistAlbum ? 110 : 0;
                cols.Year = settings.ShowPlaylistYear ? 52 : 0;
                cols.Duration = settings.ShowPlaylistDuration ? 60 : 0;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                // 密度:切换 ListView 行高(资源键通过 RootShell.Resources 查找)
                Style? style = null;
                if (RootShell != null && RootShell.Resources != null)
                {
                    string key = settings.PlaylistDensity == "Compact" ? "CompactListItemStyle" : "ComfortableListItemStyle";
                    if (RootShell.Resources.TryGetValue(key, out object? res) && res is Style s)
                    {
                        style = s;
                    }
                }

                foreach (ListView list in new[] { PlaylistView, AlbumTrackListView, ArtistTrackListView, FolderBrowserView })
                {
                    list.ItemContainerStyle = style;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>将当前播放队列保存为命名播放列表（Poweramp 式持久化，不影响当前队列）。</summary>
        internal void SaveCurrentQueueAsPlaylist(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                List<string> paths = _userPlaylist
                    .Select(p => p.FilePath)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                NamedPlaylistStore.SaveSongs(name.Trim(), paths);
                StartupLog.Write("队列已保存为播放列表: " + name.Trim() + " (" + paths.Count + " 首)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("SaveCurrentQueueAsPlaylist", ex);
            }
        }


        /// <summary>把命名单歌曲追加到当前播放队列（不播放、不替换）。</summary>
        internal void AddNamedPlaylistToQueue(string name)
        {
            try
            {
                List<string> paths = NamedPlaylistStore.LoadSongs(name);
                var items = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (!System.IO.File.Exists(path)) continue;
                    try { items.Add(CreatePlaylistItemFromPath(path)); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }
                if (items.Count == 0) return;
                AddSongsToUserPlaylist(items);
                StartupLog.Write("已把播放列表加入队列: " + name + " (" + items.Count + " 首)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("AddNamedPlaylistToQueue", ex);
            }
        }


        /// <summary>把命名播放列表载入当前队列并开始播放（队列替换为列表内容）。</summary>
        internal async System.Threading.Tasks.Task LoadNamedPlaylistToQueueAndPlayAsync(string name)
        {
            try
            {
                List<string> paths = NamedPlaylistStore.LoadSongs(name);
                if (paths.Count == 0)
                {
                    return;
                }

                var items = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (!System.IO.File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
                        items.Add(await System.Threading.Tasks.Task.Run(() => CreatePlaylistItemFromPath(path)));
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                if (items.Count == 0)
                {
                    return;
                }

                _userPlaylist.Clear();
                AddSongsToUserPlaylist(items);
                PlayUserPlaylistAt(0);
                StartupLog.Write("已载入播放列表到队列: " + name + " (" + items.Count + " 首)");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("LoadNamedPlaylistToQueueAndPlay", ex);
            }
        }


        /// <summary>已保存的命名播放列表名（供 UI 列示，含“我喜欢的音乐”）。</summary>
        internal IReadOnlyList<string> ListNamedPlaylists() => NamedPlaylistStore.List();

        /// <summary>当前播放列表拖拽重排后的回调(UserPlaylist 与主窗口共享,集合顺序已自动更新)。</summary>
        internal void RefreshFromPlaylistReorder()
        {
            // 用户播放列表顺序变化由共享 ObservableCollection 自动反映到主窗口;
            // 这里只需按当前播放曲目重新定位 _userPlaylistIndex,保证下一首播放方向正确。
            if (!string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                for (int i = 0; i < _userPlaylist.Count; i++)
                {
                    if (string.Equals(_userPlaylist[i].FilePath, _nowPlayingPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _userPlaylistIndex = i;
                        return;
                    }
                }
            }

            if (_userPlaylistIndex >= _userPlaylist.Count)
            {
                _userPlaylistIndex = _userPlaylist.Count - 1;
            }
        }


                /// <summary>自绘音量条:恒定波形竖线样式(已填充主题色/未填充灰色),无重影。</summary>
        private void DrawVolumeStyle()
        {
            if (VolumeStyleCanvas == null || VolumeSlider == null)
            {
                return;
            }

            var canvas = VolumeStyleCanvas;
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.ActualHeight;
            if (w <= 1 || h <= 1)
            {
                return;
            }

            Color accent = ThemeColorService.CurrentAccent;
            double ratio = VolumeSlider.Maximum > 0 ? VolumeSlider.Value / VolumeSlider.Maximum : 0;
            ratio = Math.Clamp(ratio, 0, 1);

            const int n = 28;
            double barW = w / n;
            var filledBrush = new SolidColorBrush(accent);
            var emptyBrush = new SolidColorBrush(Color.FromArgb(90, 150, 150, 150));
            double filledEdge = w * ratio;

            for (int i = 0; i < n; i++)
            {
                // 恒定高度竖线(无起伏)
                double bh = Math.Max(3, h * 0.85);
                var rect = new Shapes.Rectangle
                {
                    Width = Math.Max(1, barW - 1),
                    Height = bh,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = (i + 0.5) * barW <= filledEdge ? filledBrush : emptyBrush
                };
                Canvas.SetLeft(rect, i * barW);
                Canvas.SetTop(rect, (h - bh) / 2);
                canvas.Children.Add(rect);
            }
        }


        /// <summary>音量条点击/拖动定位。</summary>
        private void VolumeStyleCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            SetVolumeFromPointer(e);
            try
            {
                VolumeStyleCanvas.CapturePointer(e.Pointer);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void VolumeStyleCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (VolumeStyleCanvas.PointerCaptures != null && VolumeStyleCanvas.PointerCaptures.Count > 0)
            {
                SetVolumeFromPointer(e);
            }
        }


        private void VolumeStyleCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                VolumeStyleCanvas.ReleasePointerCapture(e.Pointer);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void SetVolumeFromPointer(PointerRoutedEventArgs e)
        {
            try
            {
                // HiFi 独占：音量条固定 100% 不动，调音量请用系统托盘（DAC 设备主音量，bit-perfect 保真）。
                if (IsHiFiModeSelected())
                {
                    VolumeSlider.Value = VolumeSlider.Maximum;
                    NowPlayingText.Text = "请在系统托盘音量条内修改音量";
                    return;
                }

                double px = e.GetCurrentPoint(VolumeStyleCanvas).Position.X;
                double ratio = Math.Clamp(px / Math.Max(1, VolumeStyleCanvas.ActualWidth), 0, 1);
                VolumeSlider.Value = ratio * VolumeSlider.Maximum; // 触发 ValueChanged -> 音量
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void VolumeStyleCanvas_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            DrawVolumeStyle();
        }


        private void ClearNowPlayingPanel()
        {
            // 保留旧波形:停止/切歌时进度条静态显示上次波形,不闪占位
            _waveformPath = null;
            RedrawProgressStyle();
            _nowPlayingPath = null;
            NowPlayingTitleText.Text = "未在播放";
            ResetNowPlayingArtistAlbumLinks();
            NowPlayingCoverImage.Source = null;
            ApplyNowPlayingPaneTransparent();
            UpdateTransportNowPlaying(null, null);
            ClearLyricsUi("开始播放后显示歌词");
            // 未播放时也填充静态频谱，保证信息卡波形始终可见
            for (int i = 0; i < WaveBarCount; i++)
            {
                _waveLevels[i] = IdleLevel(i);
            }

            DrawWaveformBars();
            ClearAlbumArtBackground();
        }


        private void UpdateTransportNowPlaying(PlaylistItem? item, ImageSource? cover)
        {
            if (item == null)
            {
                TransportTitleText.Text = "目前未播放音乐";
                TransportArtistText.Text = string.Empty;
                TransportArtistText.Visibility = Visibility.Collapsed;
                TransportFormatText.Text = string.Empty;
                TransportFormatText.Visibility = Visibility.Collapsed;
                TransportCoverImage.Source = null;
                _miniPlayerWindow?.RefreshFromOwner();
                return;
            }

            TransportTitleText.Text = item.Title;
            TransportArtistText.Text = item.Artist;
            TransportArtistText.Visibility = Visibility.Visible;
            TransportFormatText.Text = item.FormatInfoLine;
            TransportFormatText.Visibility =
                string.IsNullOrWhiteSpace(item.FormatInfoLine)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            TransportCoverImage.Source = cover;
            _miniPlayerWindow?.RefreshFromOwner();
        }


        /// <summary>独占/HiFi 输出时显示实际输出格式（WASAPI 设备端采样率/位深）；无则清空该行。</summary>
        private void UpdateNowPlayingOutputFormat()
        {
            try
            {
                string? outFmt = _audioEngine?.ActualOutputFormat;
                if (NowPlayingAudioInfoText != null)
                {
                    NowPlayingAudioInfoText.Text = string.IsNullOrEmpty(outFmt) ? string.Empty : "实际输出：" + outFmt;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>信号链调试面板：实时显示 源格式→输出格式→是否独占→是否经过 DSP（对标 foobar 排障"假 bit-perfect"）。</summary>
        internal void UpdateSignalChainDisplay()
        {
            if (SignalChainInfoText == null)
            {
                return;
            }

            try
            {
                bool hifi = _audioEngine?.IsHiFiMode == true || IsHiFiModeSelected();
                string? srcFmt = _audioEngine?.SourceFormatDescription;
                string? outFmt = _audioEngine?.ActualOutputFormat;

                // 源格式：HiFi 直通取 WAV 源；否则为系统 MediaPlayer 解码路径
                string src = string.IsNullOrWhiteSpace(srcFmt)
                    ? (hifi ? "（未知/解析中）" : "MediaPlayer（系统解码）")
                    : srcFmt;

                // 输出格式 / 设备
                string outp = string.IsNullOrWhiteSpace(outFmt)
                    ? (hifi ? "（未知/解析中）" : "系统混音器（Shared）")
                    : outFmt + (hifi ? "" : "（Shared）");

                string exclusivo = hifi ? "独占" : "共享";

                // DSP 摘要：EQ 仅在 AudioGraph（非 HiFi 独占）下有效；不显示音量（用户不关心它在此链路里）。
                string dsp;
                if (hifi)
                {
                    dsp = "无（bit-perfect 直通）";
                }
                else
                {
                    bool eqOn = EqCurveStore.Load().HasEffect();
                    dsp = eqOn ? "EQ=on" : "EQ=off";
                }

                SignalChainInfoText.Text =
                    "信号链：源[" + src + "] → 输出[" + outp + "] | " +
                    "模式=" + exclusivo + (hifi ? "" : "（系统混音）") +
                    " | DSP: " + dsp;
            }
            catch
            {
                SignalChainInfoText.Text = "信号链：—";
            }
        }


        private async Task UpdateNowPlayingPanelAsync(PlaylistItem item)
        {
            _nowPlayingPath = item.FilePath;
            NowPlayingTitleText.Text = item.Title;
            UpdateNowPlayingArtistAlbumText(item);
            UpdateTransportNowPlaying(item, null);
            _ = UpdateAudioInfoTextAsync(item.FilePath);

            byte[]? coverBytes = await Task.Run(() => ExtractCoverBytes(item.FilePath));
            if (_nowPlayingPath != item.FilePath)
            {
                return;
            }

            BitmapImage? coverImage = null;
            if (coverBytes != null && coverBytes.Length > 0)
            {
                coverImage = await CreateBitmapFromBytesAsync(coverBytes);
            }

            if (_nowPlayingPath != item.FilePath)
            {
                return;
            }

            NowPlayingCoverImage.Source = coverImage;
            UpdateTransportNowPlaying(item, coverImage);
            _ = ApplyAlbumArtBackgroundAsync(coverBytes, item.FilePath);
            ApplyNowPlayingPaneTransparent();

            List<LyricLine> lyrics = await Task.Run(() => LyricsLoader.LoadForAudio(item.FilePath));
            if (_nowPlayingPath != item.FilePath)
            {
                return;
            }

            BuildLyricsUi(lyrics);
            _ = MaybeAutoDownloadExtrasAsync(item, lyrics, coverBytes);
        }


        /// <summary>悬停超链接：显示下划线（无主题色，正常文字色 + 悬停下划线标识可点击）。</summary>
        private void NowPlayingArtistLink_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (NowPlayingArtistText != null) NowPlayingArtistText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        }


        private void NowPlayingArtistLink_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (NowPlayingArtistText != null) NowPlayingArtistText.TextDecorations = Windows.UI.Text.TextDecorations.None;
        }


        private void NowPlayingAlbumLink_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (NowPlayingAlbumText != null) NowPlayingAlbumText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        }


        private void NowPlayingAlbumLink_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (NowPlayingAlbumText != null) NowPlayingAlbumText.TextDecorations = Windows.UI.Text.TextDecorations.None;
        }


        /// <summary>重置艺术家/专辑超链接为空占位（未播放时禁用）。</summary>
        private void ResetNowPlayingArtistAlbumLinks()
        {
            try
            {
                if (NowPlayingArtistText != null) NowPlayingArtistText.Text = "未知艺术家";
                if (NowPlayingArtistLinkButton != null) NowPlayingArtistLinkButton.IsEnabled = false;
                if (NowPlayingAlbumText != null) NowPlayingAlbumText.Text = "未知专辑";
                if (NowPlayingAlbumLinkButton != null) NowPlayingAlbumLinkButton.IsEnabled = false;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>点击艺术家超链接：收起播放面板并跳转到对应艺术家详情页。</summary>
        private void NowPlayingArtistLinkButton_Click(object sender, RoutedEventArgs e)
        {
            string artistName = NowPlayingArtistText?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return;
            }

            ArtistEntry? entry = _artists.FirstOrDefault(
                a => string.Equals(a.Name, artistName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new ArtistEntry { Name = artistName };
            }

            SetNowPlayingPaneVisible(false);
            // 从播放面板跳转：先切到「艺术家」分类并真正显示其视图根，再打开详情，保证左侧分类与右侧面板同步
            _currentCategory = "Artists";
            ApplyCategoryView();
            DispatcherQueue.TryEnqueue(() => OpenArtistDetail(entry!));
        }


        /// <summary>点击专辑超链接：收起播放面板并跳转到对应专辑详情页。</summary>
        private void NowPlayingAlbumLinkButton_Click(object sender, RoutedEventArgs e)
        {
            string albumName = NowPlayingAlbumText?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(albumName))
            {
                return;
            }

            string artistName = NowPlayingArtistText?.Text?.Trim() ?? "";
            AlbumEntry? entry = _albums.FirstOrDefault(
                a => string.Equals(a.Name, albumName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = BuildAlbumEntriesFromTracks(_playlist).FirstOrDefault(
                    a => string.Equals(a.Name, albumName, StringComparison.OrdinalIgnoreCase));
            }
            if (entry == null)
            {
                entry = new AlbumEntry { Name = albumName, Artist = artistName };
            }

            SetNowPlayingPaneVisible(false);
            // 从播放面板跳转：先切到「专辑」分类并真正显示视图根，再打开详情，左侧分类与右侧面板同步
            _currentCategory = "Albums";
            ApplyCategoryView();
            DispatcherQueue.TryEnqueue(() => OpenAlbumDetail(entry!, fromArtist: false));
        }


        private void UpdateNowPlayingArtistAlbumText(PlaylistItem item)
        {
            bool hasArtist = !string.IsNullOrWhiteSpace(item.Artist) && item.Artist != "未知艺术家";
            bool hasAlbum = !string.IsNullOrWhiteSpace(item.Album) && item.Album != "未知专辑";

            NowPlayingArtistText.Text = hasArtist ? item.Artist.Trim() : "未知艺术家";
            NowPlayingArtistLinkButton.IsEnabled = hasArtist;

            NowPlayingAlbumText.Text = hasAlbum ? item.Album.Trim() : "未知专辑";
            NowPlayingAlbumLinkButton.IsEnabled = hasAlbum;
        }


        /// <summary>播放面板背景保持透明，与专辑详情页一致（露出主程序背景，非浮层）。</summary>
        private void ApplyNowPlayingPaneTransparent()
        {
            try
            {
                if (NowPlayingPaneContent != null)
                {
                    NowPlayingPaneContent.Background = null;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void SyncLyricsToPosition(TimeSpan position, bool force = false)
        {
            if (_lyricLines.Count == 0 || _lyricTextBlocks.Count == 0)
            {
                return;
            }

            int index = 0;
            for (int i = 0; i < _lyricLines.Count; i++)
            {
                if (_lyricLines[i].Time <= position)
                {
                    // 跳过翻译行：高亮停在原文行（翻译行紧跟原文、同一时刻，若不清跳会让主题色落到译文上）
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

            if (!force && index == _currentLyricIndex)
            {
                // 当前行未变：保持整行主题色，不把 Run 染成灰白
                if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
                {
                    Brush curAccent = ResolveAccentBrush();
                    TextBlock row = _lyricTextBlocks[_currentLyricIndex];
                    row.Foreground = curAccent;
                    row.Opacity = 1.0;
                    if (curAccent is SolidColorBrush scbCur2)
                    {
                        ResetRowRunColors(row, scbCur2.Color.R, scbCur2.Color.G, scbCur2.Color.B);
                    }
                }

                return;
            }

            _currentLyricIndex = index;
            if (force)
            {
                StartupLog.Write("歌词强制重渲染 index=" + index + " 行数=" + _lyricTextBlocks.Count);
            }

            // 方案A：当前句主题色强调 + 相邻句微亮（纯属性调整，不改行结构、不用 Inlines）
            Brush accent = ResolveAccentBrush();
            for (int i = 0; i < _lyricTextBlocks.Count; i++)
            {
                TextBlock row = _lyricTextBlocks[i];
                int dist = Math.Abs(i - index);
                if (dist == 0)
                {
                    row.FontSize = 19;
                    row.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    row.Foreground = accent;
                    row.Opacity = 1.0;
                    // 当前行整行保持主题色（不把 Run 染成灰白，避免播放中被掩盖成灰色）
                    if (accent is SolidColorBrush scbCur)
                    {
                        ResetRowRunColors(row, scbCur.Color.R, scbCur.Color.G, scbCur.Color.B);
                    }
                    else
                    {
                        ResetRowRunColors(row, 255, 255, 255);
                    }
                }
                else if (dist == 1)
                {
                    row.FontSize = 15;
                    row.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                    row.Foreground = new SolidColorBrush(Color.FromArgb(255, 205, 205, 205));
                    row.Opacity = 0.85;
                    ResetRowRunColors(row, 205, 205, 205);
                }
                else
                {
                    row.FontSize = 14;
                    row.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                    row.Foreground = new SolidColorBrush(Color.FromArgb(255, 154, 154, 154));
                    row.Opacity = 0.55;
                    ResetRowRunColors(row, 154, 154, 154);
                }
            }

            // 用户手动滚动中：仅更新颜色高亮，不强制视口吸附（避免抢走用户正在看的滚动位置）
            if (!_userScrollingLyrics)
            {
                ScrollLyricToCenter(_lyricTextBlocks[index]);
            }
        }


        /// <summary>当前行的逐字高亮刷新（无逐字数据时无操作）。</summary>
        private void UpdateCurrentLineCharHighlight(TimeSpan position)
        {
            if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
            {
                UpdateCharHighlight(_lyricTextBlocks[_currentLyricIndex], position);
            }
        }


        /// <summary>逐字着色：已唱字符白色，未唱灰色。仅对带 CharTimes 的行生效。</summary>
        private void UpdateCharHighlight(TextBlock row, TimeSpan position)
        {
            if (row.Tag is not LyricLine line
                || line.CharTimes == null
                || line.CharTimes.Count != line.Text.Length
                || row.Inlines.Count == 0)
            {
                return;
            }

            int n = 0;
            for (int i = 0; i < line.CharTimes.Count; i++)
            {
                if (line.CharTimes[i] <= position)
                {
                    n = i + 1;
                }
                else
                {
                    break;
                }
            }

            var played = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            var unplayed = new SolidColorBrush(Color.FromArgb(255, 154, 154, 154));
            for (int i = 0; i < row.Inlines.Count; i++)
            {
                if (row.Inlines[i] is Microsoft.UI.Xaml.Documents.Run run)
                {
                    run.Foreground = i < n ? played : unplayed;
                }
            }
        }


        /// <summary>单击歌词行：把播放跳到该行时间（对齐"点进度条→seek+暂停"）。</summary>
        private void SeekToLyricLine(TimeSpan target)
        {
            // 跳到目标时间（引擎 seek；若 MediaPlayer 播放也用其 seek）
            bool handled = false;
            if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
            {
                _audioEngine.Seek(target);
                handled = true;
            }

            MediaPlayer? player = GetPlayer();
            if (player != null && player.Source != null && !handled)
            {
                try { player.PlaybackSession.Position = target; handled = true; }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }

            if (_audioEngine != null && handled)
            {
                // 与点进度条一致：seek 后暂停，方便定位（用户再按播放继续）；同步刷新高亮/进度
                if (_audioEngine.IsPlaying)
                {
                    _audioEngine.Pause();
                    _isEnginePaused = true;
                    UpdateWaveformTimerForPlaybackState(false);
                    UpdateEngineSmtcStatus(MediaPlaybackStatus.Paused);
                    if (PlayPauseIcon != null)
                    {
                        PlayPauseIcon.Glyph = "\uE768";
                    }

                    _miniPlayerWindow?.RefreshFromOwner();
                }

                // 重挂下一首（seek 丢弃无缝源里的 next）
                if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
                {
                    _ = PreloadSeamlessNextAsync(_userPlaylist[_userPlaylistIndex]);
                }

                // 立即把高亮切到目标行
                SyncLyricsToPosition(target, force: true);
                // 单击选中：无视用户滚动状态，把该行滚到中间
                if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyricTextBlocks.Count)
                {
                    ScrollLyricToCenter(_lyricTextBlocks[_currentLyricIndex]);
                }
            }
        }


        private void LyricScrollTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            double elapsed = Environment.TickCount64 - _lyricScrollStartMs;
            double t = Math.Clamp(elapsed / LyricScrollDurationMs, 0, 1);
            // ease-in-out cubic，比系统默认滚动更柔和
            double eased = t < 0.5
                ? 4 * t * t * t
                : 1 - Math.Pow(-2 * t + 2, 3) / 2;

            double y = _lyricScrollFrom + (_lyricScrollTo - _lyricScrollFrom) * eased;
            LyricsScrollViewer.ChangeView(null, y, null, disableAnimation: true);

            if (t >= 1)
            {
                _lyricScrollTimer?.Stop();
                LyricsScrollViewer.ChangeView(null, _lyricScrollTo, null, disableAnimation: true);
            }
        }


        private void WaveformTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            MediaPlayer? player = GetPlayer();
            bool enginePlaying = _audioEngine?.IsPlaying == true;
            bool playing = (player?.Source != null
                    && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                || enginePlaying;

            double t = Environment.TickCount64 / 1000.0;
            double volume = enginePlaying ? VolumeSlider.Value / 100.0 : (player?.Volume ?? 0.8);
            bool changed = false;

            for (int i = 0; i < WaveBarCount; i++)
            {
                double target;
                if (playing)
                {
                    // 对称呼吸式频谱：中间高两边低、每柱各自节奏，无横向滚动
                    double rhythm = 0.5 + 0.5 * Math.Sin(t * 1.8 + _wavePhases[i]);
                    double halfSpan = Math.Max(1.0, (WaveBarCount - 1) / 2.0);
                    double pos = (i - (WaveBarCount - 1) / 2.0) / halfSpan;
                    double symmetry = 0.5 + 0.5 * (1.0 - Math.Min(1.0, Math.Abs(pos)));
                    double n = rhythm * symmetry;
                    target = Math.Clamp(n * (0.55 + 0.45 * volume), 0.1, 1.0);
                }
                else
                {
                    target = IdleLevel(i);
                }

                double next = _waveLevels[i] + (target - _waveLevels[i]) * (playing ? 0.35 : 0.18);
                if (Math.Abs(next - _waveLevels[i]) > 0.002)
                {
                    changed = true;
                }

                _waveLevels[i] = next;
            }

            if (changed || playing)
            {
                DrawWaveformBars();
            }

            if (!playing)
            {
                _waveformIdleSettleTicks++;
                if (_waveformIdleSettleTicks >= 12 || !changed)
                {
                    _waveformTimer?.Stop();
                    _waveformIdleSettleTicks = 0;
                }
            }
            else
            {
                _waveformIdleSettleTicks = 0;
            }
        }


        // =====================================================================
        // 播放核心
        // =====================================================================

        private void PlayAtIndex(int index)
            => PlayLibraryItemAt(index, syncUserPlaylistIndex: true);

        private void PlayLibraryItemAt(int index, bool syncUserPlaylistIndex)
        {
            if (index < 0 || index >= _playlist.Count)
            {
                return;
            }

            PlaylistItem item = _playlist[index];
            _currentIndex = index;
            if (syncUserPlaylistIndex)
            {
                _userPlaylistIndex = FindUserPlaylistIndex(item.FilePath);
            }

            if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                && !_isMultiSelectMode
                && index >= 0
                && PlaylistView.ItemsSource is System.Collections.IList songsList
                && index < songsList.Count)
            {
                PlaylistView.SelectedIndex = index;
                PlaylistView.ScrollIntoView(item);
            }

            StartPlayback(item);
        }


        private void PlayUserPlaylistAt(int index)
        {
            if (index < 0 || index >= _userPlaylist.Count)
            {
                return;
            }

            PlaylistItem item = _userPlaylist[index];
            _userPlaylistIndex = index;
            _currentIndex = FindLibraryIndex(item.FilePath);

            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal)
                && !_isMultiSelectMode)
            {
                PlaylistView.SelectedIndex = index;
                PlaylistView.ScrollIntoView(item);
            }
            else if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                && !_isMultiSelectMode
                && _currentIndex >= 0
                && PlaylistView.ItemsSource is System.Collections.IList songsList2
                && _currentIndex < songsList2.Count)
            {
                PlaylistView.SelectedIndex = _currentIndex;
                PlaylistView.ScrollIntoView(_playlist[_currentIndex]);
            }

            StartPlayback(item);
            PersistPlaybackSession();
            NotifyCurrentPlaylistWindow();
        }


        private void StartPlayback(PlaylistItem item)
        {
            ScrobblePreviousIfAny();

            // 进度条样式(读设置缓存) + 异步加载波形(波形样式用)
            _progressBarStyle = AppSettingsStore.Load().ProgressBarStyle;
            // 保留旧波形直到新波形解码完成(避免加载过程闪占位)
            _waveformPath = null;
            StartupLog.Write("波形加载开始: " + item.FilePath + " style=" + _progressBarStyle);
            LoadWaveformForCurrentAsync(item.FilePath);

            // DSD(DSF/DFF) 在非 WASAPI 独占模式下自动转码为 PCM 输出（保留可听性，非 bit-perfect），
            // 独占模式下走 DoP 原生直出。提示在 PlayExtendedWithEngineAsync 成功后给出。
            StartupLog.Write("StartPlayback: " + item.FilePath + " mode=" + (AppSettingsStore.Load().OutputMode));

            // 三模式统一走 FFmpeg 引擎 + NAudio/HiFi 输出（共享 / WASAPI 独占 / ASIO），
            // 使曲线 EQ / 声道平衡 / 限幅在所有输出模式下都能实时生效、暂停后继续保留。
            // 直接按设置判断（而非 _audioEngine.IsHiFiMode），避免 engine 尚未创建/未设 mode 时的首次播放漏走。
            {
                // 用源实际时长作为进度/播完上限（规避 DSD 转 PCM 转码 WAV 尾部 padding 的时长越界）。
                // DSD 源用 ffmpeg 探测真实音轨时长（TagLib 读 DSD 源时长可能不准）；其它用元数据时长，异步不阻塞开播。
                _ = ApplyEngineSourceDurationAsync(item);
                // 停掉 MediaPlayer，避免与引擎同时出声
                MediaPlayer? curPlayer = GetPlayer();
                if (curPlayer != null && curPlayer.Source != null)
                {
                    try
                    {
                        curPlayer.Pause();
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                _ = PlayExtendedWithEngineAsync(item);
                return;
            }

        }


        /// <summary>引擎播放前异步设置源实际时长（DSD 用 ffmpeg 探测真实音轨时长，其它用元数据时长），
        /// 作为进度/播完上限规避 DSD 转 PCM 转码 WAV 尾部 padding 的时长越界。</summary>
        private async System.Threading.Tasks.Task ApplyEngineSourceDurationAsync(PlaylistItem item)
        {
            try
            {
                if (_audioEngine == null || item == null) return;
                string srcExt = System.IO.Path.GetExtension(item.FilePath).ToLowerInvariant();
                TimeSpan src = item.Duration;
                if (srcExt is ".dsf" or ".dff")
                {
                    var probed = await _audioEngine.ProbeSourceDurationAsync(item.FilePath);
                    if (probed > TimeSpan.Zero) src = probed;
                }

                _audioEngine.SetSourceDuration(src);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }



        /// <summary>用 FFmpeg 引擎播放扩展格式（APE/WavPack 等）。</summary>
        private async Task PlayExtendedWithEngineAsync(PlaylistItem item)
        {
            // 播放历史：切到新歌前，把上一首（若有）记入历史（未播完）
            if (!string.IsNullOrWhiteSpace(_nowPlayingPath)
                && !string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                RecordCurrentTrackHistory(completed: false);
            }

            NowPlayingText.Text = "正在转码：" + item.Title;
            _audioEngine ??= new AudioPlaybackEngine();
            AppSettingsState hifiSettings = AppSettingsStore.Load();
            ApplyEngineOutputMode(hifiSettings);
            _audioEngine.SetOutputDevicePreference(string.IsNullOrWhiteSpace(hifiSettings.OutputDeviceId) ? null : hifiSettings.OutputDeviceId);
            _audioEngine.PlaybackEnded -= OnEnginePlaybackEnded;
            _audioEngine.PlaybackEnded += OnEnginePlaybackEnded;
            _audioEngine.SeamlessTrackChanged -= OnSeamlessTrackChanged;
            _audioEngine.SeamlessTrackChanged += OnSeamlessTrackChanged;

            try
            {
                _audioEngine.SetEqCurve(EqCurveStore.Load());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 应用 DSP 附加状态（声道平衡 + 安全限幅）到引擎，使持久化设置在新歌播放即生效
            try
            {
                DspExtraState _dspExtra = DspExtraStore.Load();
                _audioEngine.SetChannelBalance(_dspExtra.ChannelBalance);
                _audioEngine.SetSafety(_dspExtra.Safety);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 应用房间校正（卷积 FIR）：持久化状态 → 引擎 → DSP 链
            try
            {
                _audioEngine.SetRoomCorrection(RoomCorrectionStore.Load());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 应用 ReplayGain 响度归一化：从源文件读 track/album 增益与 peak，按持久化 mode 由 DSP 链处理。
            try
            {
                ReplayGainState rg = ReplayGainStore.Load();
                var rgData = await Task.Run(() => ReplayGainReader.ReadForAudio(item.FilePath));
                _currentRgData = rgData;
                if (rgData.HasValue)
                {
                    _audioEngine.SetReplayGain(rg, rgData.Value.TrackGainDb, rgData.Value.AlbumGainDb, rgData.Value.Peak);
                }
                else
                {
                    _audioEngine.SetReplayGain(rg, 0, 0, 1.0);
                }

                DispatcherQueue.TryEnqueue(RefreshAudioFxRgInfo);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            bool ok = await _audioEngine.PlayFileWithFfmpegAsync(item.FilePath, s =>
                DispatcherQueue.TryEnqueue(() => { NowPlayingText.Text = s; }));
            if (ok)
            {
                _isEnginePaused = false;
                _usingEnginePlayback = true;
                NowPlayingText.Text = "正在播放（引擎）：" + item.Title + " - " + item.Artist;
                // DSD：若走 DoP 直出（独占/ASIO + 设置=DoP）→ 提示直出；否则 ffmpeg 转 PCM → 提示转码。
                bool dsdDop = IsDsdFile(item.FilePath)
                    && IsHiFiModeSelected()
                    && string.Equals(AppSettingsStore.Load().DsdOutputMode, "Dop", StringComparison.OrdinalIgnoreCase);
                if (dsdDop)
                {
                    NowPlayingText.Text = "DSD DoP 直出（bit-perfect）· " + item.Title + " - " + item.Artist;
                }
                else if (IsDsdFile(item.FilePath))
                {
                    string pcmDesc = string.IsNullOrWhiteSpace(_audioEngine?.SourceFormatDescription)
                        ? (_audioEngine?.ActualOutputFormat ?? "PCM") : _audioEngine!.SourceFormatDescription!;
                    NowPlayingText.Text = "已转码为 " + pcmDesc + " 输出";
                    StartupLog.Write("DSD 已转码 PCM 输出: " + item.FilePath + " → " + (_audioEngine?.SourceFormatDescription ?? "?"));
                }
                // 设备主音量（DAC 驱动级）只由用户拖动音量条时设置，切歌不重置，保持用户设定的响度。
                // （播放器数字音量恒 100% 直通，bit-perfect；无实体音量键的小尾巴可拖动音量条调轻）
                _ = UpdateNowPlayingPanelAsync(item);
                UpdateNowPlayingOutputFormat();
                UpdateSignalChainDisplay();
                RecordPlaybackStatsOnStart(item);
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Glyph = "\uE769";
                }

                // 引擎桥接：时长 / 进度条 / 波形
                ProgressSlider.Maximum = Math.Max(1, _audioEngine.Duration.TotalSeconds);
                ProgressSlider.Value = 0;
                TotalTimeText.Text = FormatTime(_audioEngine.Duration);
                _audioEngine.PositionChanged -= EnginePositionChanged;
                _audioEngine.PositionChanged += EnginePositionChanged;
                UpdateWaveformTimerForPlaybackState(true);
                _ = FadeInEngineAsync();
                _miniPlayerWindow?.RefreshFromOwner();
                ConfigureEngineSmtc(item, playing: true);
                _ = PreloadSeamlessNextAsync(item);
            }
            else
            {
                string? reason = _audioEngine?.LastError;
                NowPlayingText.Text = string.IsNullOrWhiteSpace(reason)
                    ? "播放失败（FFmpeg 转码或打开出错）"
                    : "播放失败：" + reason;

                if (reason != null && reason.Contains("未找到内置 ffmpeg.exe"))
                {
                    _ = ShowErrorAsync(
                        "无法播放该格式",
                        "内置 FFmpeg 解码器未找到。\n\n这通常是被杀毒软件（如火绒/360）拦截删除。\n请将程序目录下 Assets\\ffmpeg 文件夹加入杀毒软件信任区，然后重新打开程序。");
                }
            }
        }



        private async Task FadeInEngineAfterResumeAsync(double target)
        {
            // 防御：若读到的暂停音量异常(<=1%≈静音误判)，回退全音量，避免恢复后设备音量停在约 0 造成"系统托盘音量归0"。
            if (target <= 0.01 || double.IsNaN(target))
            {
                target = 1.0;
            }

            target = Math.Clamp(target, 0.0, 1.0);
            try
            {
                // 暂停恢复后独占会话重建，瞬时全音量可达造成爆音/音量暴增：
                // 从较低音量极短渐变到目标，缓解瞬态。（仅暂停恢复路径调用，不影响常规切歌的 bit-perfect 直通。）
                _audioEngine?.SetVolume(target * 0.18);
                const int steps = 5;
                for (int i = 1; i <= steps; i++)
                {
                    _audioEngine?.SetVolume(target * (0.18 + 0.82 * i / (double)steps));
                    await Task.Delay(30);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            finally
            {
                // 无论 Fade 是否中断/异常，最终都恢复目标设备音量，避免停在 0.02 静音值
                try { _audioEngine?.SetVolume(target); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }
        }


        private void OnEnginePlaybackEnded()
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                // 播放历史：自然播完 → 记为已完成
                RecordCurrentTrackHistory(completed: true);
                _isEnginePaused = false;
                _usingEnginePlayback = false;
                UpdateWaveformTimerForPlaybackState(false);
                UpdateEngineSmtcStatus(MediaPlaybackStatus.Stopped);
                _miniPlayerWindow?.RefreshFromOwner();
                HandleMediaEnded();
            });
        }


        /// <summary>引擎开播后预加载下一首到无缝源（共享/ASIO、顺序播放）。同格式可无缝续接，否则由上层重建。</summary>
        private async System.Threading.Tasks.Task PreloadSeamlessNextAsync(PlaylistItem current)
        {
            if (_audioEngine == null || current == null)
            {
                return;
            }

            // DSD 播完自动切歌优先：DSD 源不参与无缝预加载（走原 Stop→PlayNext，避免无缝与 DSD 时长修正冲突）
            string curExt = System.IO.Path.GetExtension(current.FilePath).ToLowerInvariant();
            if (curExt is ".dsf" or ".dff")
            {
                _seamlessPreloaded = null;
                return;
            }

            try
            {
                PlaylistItem? next = ResolveSequentialNextItem(current);
                if (next == null || string.IsNullOrWhiteSpace(next.FilePath) || !System.IO.File.Exists(next.FilePath))
                {
                    StartupLog.Write("预加载: 无下一首 or 文件不存在 next=" + (next?.Title ?? "<null>"));
                    _seamlessPreloaded = null;
                    return;
                }

                string? wav = await _audioEngine.EnsureCachedWavAsync(next.FilePath);
                if (string.IsNullOrWhiteSpace(wav))
                {
                    StartupLog.Write("预加载: 转码失败 无WAV next=" + next.Title);
                    _seamlessPreloaded = null;
                    return;
                }

                bool ok = await _audioEngine.PrepareNextSeamless(wav);
                StartupLog.Write("预加载: \"" + current.Title + "\" → \"" + next.Title + "\" wav=" + System.IO.Path.GetFileName(wav) + " 采纳无缝=" + ok);
                if (ok)
                {
                    _seamlessPreloaded = next;
                }
                else
                {
                    _seamlessPreloaded = null; // 格式不同等：无缝不启用，后续走重建
                }
            }
            catch (Exception ex)
            {
                StartupLog.Write("预加载: 异常 " + ex.Message);
                _seamlessPreloaded = null;
            }
        }


        /// <summary>顺序播放时确定"当前曲目之后"的一首（播放队列优先，否则媒体库列表）。</summary>
        private PlaylistItem? ResolveSequentialNextItem(PlaylistItem current)
        {
            // 无缝预加载预测"即将播放的下一首"：必须遵循当前播放顺序（含随机），
            // 否则随机播放时自动切歌会错误地切到列表顺序的下一首而非随机下一首。
            if (_userPlaylist.Count > 0)
            {
                int baseIdx = FindUserPlaylistIndex(current.FilePath);
                if (baseIdx < 0)
                {
                    baseIdx = _userPlaylistIndex >= 0 ? _userPlaylistIndex : 0;
                }

                int next = NextIndexByOrder(_userPlaylist.Count, baseIdx, autoAdvance: true);
                if (next >= 0 && next < _userPlaylist.Count)
                {
                    return _userPlaylist[next];
                }

                return null;
            }

            if (_playlist.Count > 0)
            {
                int baseIdx = _playlist.ToList().FindIndex(p => string.Equals(p.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase));
                if (baseIdx < 0)
                {
                    baseIdx = _currentIndex >= 0 ? _currentIndex : 0;
                }

                int next = NextIndexByOrder(_playlist.Count, baseIdx, autoAdvance: true);
                if (next >= 0 && next < _playlist.Count)
                {
                    return _playlist[next];
                }
            }

            return null;
        }


        /// <summary>无缝切到预加载的下一首：更新正在播放信息与索引，并继续预加载下下首。</summary>
        private void OnSeamlessTrackChanged()
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                PlaylistItem? next = _seamlessPreloaded;
                _seamlessPreloaded = null;
                if (next != null)
                {
                    _isEnginePaused = false;
                    _usingEnginePlayback = true;
                    NowPlayingText.Text = "正在播放（引擎）：" + next.Title + " - " + next.Artist;
                    _ = UpdateNowPlayingPanelAsync(next);
                    // 无缝续接后重新加载下一首的波形/进度条样式（否则会残留上一首的波形与时长）
                    _progressBarStyle = AppSettingsStore.Load().ProgressBarStyle;
                    _waveformPath = null;
                    StartupLog.Write("无缝切歌 波形加载开始: " + next.FilePath + " style=" + _progressBarStyle);
                    LoadWaveformForCurrentAsync(next.FilePath);
                    UpdateNowPlayingOutputFormat();
                    RecordPlaybackStatsOnStart(next);
                    if (_audioEngine != null)
                    {
                        ProgressSlider.Maximum = Math.Max(1, _audioEngine.Duration.TotalSeconds);
                        ProgressSlider.Value = 0;
                        TotalTimeText.Text = FormatTime(_audioEngine.Duration);
                    }
                    ConfigureEngineSmtc(next, playing: true);
                    AdvanceUserPlaylistIndexTo(next);
                    _miniPlayerWindow?.RefreshFromOwner();
                    _ = PreloadSeamlessNextAsync(next);
                }
            });
        }


        /// <summary>无缝切歌后同步用户播放队列当前索引（命中则设为该项）。</summary>
        private void AdvanceUserPlaylistIndexTo(PlaylistItem item)
        {
            if (_userPlaylist.Count == 0) return;
            int idx = FindUserPlaylistIndex(item.FilePath);
            if (idx >= 0)
            {
                _userPlaylistIndex = idx;
                PlaylistView.SelectedIndex = idx;
            }
        }


        /// <summary>配置引擎播放的系统媒体控件（SMTC）。</summary>
        private void ConfigureEngineSmtc(PlaylistItem item, bool playing)
        {
            try
            {
                if (!AppSettingsStore.Load().EnableSmtc)
                {
                    return;
                }

                _engineSmtc ??= SystemMediaTransportControls.GetForCurrentView();
                SystemMediaTransportControls smtc = _engineSmtc;
                smtc.IsEnabled = true;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;
                smtc.IsNextEnabled = true;
                smtc.IsPreviousEnabled = true;
                smtc.IsStopEnabled = true;
                smtc.ButtonPressed -= Smtc_ButtonPressed;
                smtc.ButtonPressed += Smtc_ButtonPressed;
                smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

                SystemMediaTransportControlsDisplayUpdater updater = smtc.DisplayUpdater;
                // 切歌/重设前清空旧元数据，避免 deskbox 等系统小组件残留上一首状态（标题缺失/两状态叠加）。
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = item.Title;
                updater.MusicProperties.Artist = item.Artist;
                updater.MusicProperties.AlbumTitle = item.Album;
                updater.Thumbnail = null;
                updater.Update();

                // 异步补封面缩略图（deskbox/系统媒体浮层可显示专辑封面）
                _ = LoadAndSetSmtcThumbnailAsync(updater, item);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>异步读取曲目封面并设置到 SMTC 缩略图（失败静默）。</summary>
        private async System.Threading.Tasks.Task LoadAndSetSmtcThumbnailAsync(SystemMediaTransportControlsDisplayUpdater updater, PlaylistItem item)
        {
            try
            {
                if (updater == null || item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
                byte[]? bytes = await System.Threading.Tasks.Task.Run(() => ExtractCoverBytes(item.FilePath));
                if (bytes is not { Length: > 0 }) return;
                using var ms = new System.IO.MemoryStream(bytes);
                ms.Position = 0;
                var stream = ms.AsRandomAccessStream();
                var reference = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream);
                updater.Thumbnail = reference;
                updater.Update();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>更新引擎 SMTC 播放状态（暂停/恢复/结束）。</summary>
        private void UpdateEngineSmtcStatus(MediaPlaybackStatus status)
        {
            if (_engineSmtc == null)
            {
                return;
            }

            try
            {
                _engineSmtc.PlaybackStatus = status;
                if (status == MediaPlaybackStatus.Stopped)
                {
                    _engineSmtc.IsEnabled = false;
                }

            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }
    }
}
