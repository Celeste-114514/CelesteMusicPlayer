using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using Windows.System;

namespace CelesteMusicPlayer
{
    /// <summary>MusicPlayer2 对齐功能：SMTC、热键、收藏、下一首播放、在线资源等。</summary>
    public sealed partial class MainWindow
    {
        private GlobalHotkeyService? _hotkeys;
        private FadePlaybackController? _fadeController;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sleepTimer;

        /// <summary>睡眠定时器停止模式。</summary>
        private enum SleepStopMode
        {
            None,
            AfterMinutes,
            AfterTrack,
            AfterTracks
        }

        private SleepStopMode _sleepMode = SleepStopMode.None;
        private int _sleepTracksRemaining;
        private AudioPlaybackEngine? _audioEngine;
        private readonly LibraryWatchService _libraryWatch = new();
        private DateTime _lastListenSampleUtc = DateTime.UtcNow;
        private string? _listenSamplePath;
        private PlaylistItem? _lastPlayedForScrobble;
        private DateTime _lastPlayStartUtc;
        private bool _featuresInitialized;

        private void InitializeMusicPlayer2Features()
        {
            if (_featuresInitialized)
            {
                return;
            }

            _featuresInitialized = true;
            _fadeController = new FadePlaybackController(DispatcherQueue);

            EqualizerWindow.Applied += OnEqualizerApplied;
            TagEditorWindow.TagsSaved += OnTagsSaved;

            ConfigureSmtcFromSettings();
            ApplyPlaybackRateFromSettings();
            RestartGlobalHotkeysFromSettings();
            RestartLibraryWatchFromSettings();
            AttachRootKeyboardAccelerators();
            UpdateFavoriteButtonUi();
            AppSettingsState boot = AppSettingsStore.Load();
            ApplyNavVisibilityFromSettings(boot);
            ApplySpectrumVisibilityFromSettings(boot);
            ApplyCoverVisibilityFromSettings(boot);
            AttachGlobalMouseWheelVolume();
        }

        private void AttachGlobalMouseWheelVolume()
        {
            if (Content is not UIElement root)
            {
                return;
            }

            root.PointerWheelChanged -= Root_PointerWheelChanged;
            root.PointerWheelChanged += Root_PointerWheelChanged;
        }

        private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!AppSettingsStore.Load().GlobalMouseWheelVolume)
            {
                return;
            }

            if (FocusManager.GetFocusedElement(Content.XamlRoot) is Slider)
            {
                return;
            }

            int delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            AdjustVolumeBy(delta > 0 ? 2 : -2);
            e.Handled = true;
        }

        private void DisposeMusicPlayer2Features()
        {
            try
            {
                EqualizerWindow.Applied -= OnEqualizerApplied;
                TagEditorWindow.TagsSaved -= OnTagsSaved;
            }
            catch
            {
            }

            try
            {
                _hotkeys?.StopListening();
                _hotkeys?.Dispose();
                _hotkeys = null;
            }
            catch
            {
            }

            try
            {
                _libraryWatch.Stop();
                _libraryWatch.Dispose();
            }
            catch
            {
            }

            _fadeController?.Cancel();
        }

        private void ConfigureSmtcFromSettings()
        {
            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            AppSettingsState settings = AppSettingsStore.Load();
            bool enable = settings.EnableSmtc;
            player.CommandManager.IsEnabled = enable;

            SystemMediaTransportControls smtc = player.SystemMediaTransportControls;
            smtc.IsEnabled = enable;
            smtc.IsPlayEnabled = enable;
            smtc.IsPauseEnabled = enable;
            smtc.IsNextEnabled = enable;
            smtc.IsPreviousEnabled = enable;
            smtc.IsStopEnabled = enable;
            smtc.ButtonPressed -= Smtc_ButtonPressed;
            if (enable)
            {
                smtc.ButtonPressed += Smtc_ButtonPressed;
            }
        }

        private void Smtc_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                switch (args.Button)
                {
                    case SystemMediaTransportControlsButton.Play:
                    case SystemMediaTransportControlsButton.Pause:
                        TogglePlayPausePublic();
                        break;
                    case SystemMediaTransportControlsButton.Next:
                        PlayNext();
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        PlayPrevious();
                        break;
                    case SystemMediaTransportControlsButton.Stop:
                        GetPlayer()?.Pause();
                        break;
                }
            });
        }

        private void ApplyPlaybackRateFromSettings()
        {
            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            double rate = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
            try
            {
                player.PlaybackSession.PlaybackRate = rate;
            }
            catch
            {
            }
        }

        private void RestartGlobalHotkeysFromSettings()
        {
            try
            {
                _hotkeys?.StopListening();
                _hotkeys?.Dispose();
                _hotkeys = null;
            }
            catch
            {
            }

            if (!AppSettingsStore.Load().EnableGlobalHotkeys)
            {
                return;
            }

            try
            {
                _hotkeys = new GlobalHotkeyService();
                _hotkeys.PlayPause += () => DispatcherQueue.TryEnqueue(TogglePlayPausePublic);
                _hotkeys.Next += () => DispatcherQueue.TryEnqueue(PlayNext);
                _hotkeys.Previous += () => DispatcherQueue.TryEnqueue(PlayPrevious);
                _hotkeys.VolumeUp += () => DispatcherQueue.TryEnqueue(() => AdjustVolumeBy(5));
                _hotkeys.VolumeDown += () => DispatcherQueue.TryEnqueue(() => AdjustVolumeBy(-5));
                _hotkeys.SeekForward += () => DispatcherQueue.TryEnqueue(() => SeekBySeconds(5));
                _hotkeys.SeekBack += () => DispatcherQueue.TryEnqueue(() => SeekBySeconds(-5));
                _hotkeys.ToggleDesktopLyrics += () => DispatcherQueue.TryEnqueue(() =>
                    SetDesktopLyricsEnabled(!_desktopLyricsEnabled));
                _hotkeys.ToggleFavorite += () => DispatcherQueue.TryEnqueue(ToggleFavoriteForCurrent);
                _hotkeys.ShowHideMain += () => DispatcherQueue.TryEnqueue(ToggleMainWindowVisibility);
                _hotkeys.Stop += () => DispatcherQueue.TryEnqueue(() => GetPlayer()?.Pause());
                _hotkeys.Start();
                _hotkeys.ApplyBindings(AppSettingsStore.Load().CustomHotkeys);
            }
            catch (Exception ex)
            {
                StartupLog.Write("GlobalHotkey start failed: " + ex.Message);
            }
        }

        private void RestartLibraryWatchFromSettings()
        {
            _libraryWatch.Changed -= LibraryWatch_Changed;
            _libraryWatch.Stop();

            AppSettingsState settings = AppSettingsStore.Load();
            if (!settings.AutoUpdateLibrary)
            {
                return;
            }

            var folders = new List<string>();
            if (settings.LibraryWatchFolders != null)
            {
                folders.AddRange(settings.LibraryWatchFolders.Where(Directory.Exists));
            }

            try
            {
                string? sessionFolder = LibrarySessionStore.TryLoad()?.FolderPath;
                if (!string.IsNullOrWhiteSpace(sessionFolder)
                    && Directory.Exists(sessionFolder)
                    && !folders.Any(f => string.Equals(f, sessionFolder, StringComparison.OrdinalIgnoreCase)))
                {
                    folders.Add(sessionFolder);
                    AppSettingsStore.Update(s =>
                    {
                        s.LibraryWatchFolders ??= new List<string>();
                        if (!s.LibraryWatchFolders.Any(f =>
                                string.Equals(f, sessionFolder, StringComparison.OrdinalIgnoreCase)))
                        {
                            s.LibraryWatchFolders.Add(sessionFolder);
                        }
                    });
                }
            }
            catch
            {
            }

            if (folders.Count == 0)
            {
                return;
            }

            _libraryWatch.Changed += LibraryWatch_Changed;
            _libraryWatch.Start(folders);
        }

        private void LibraryWatch_Changed(IReadOnlyList<string> paths)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (paths.Count == 0)
                {
                    return;
                }

                if (!AppSettingsStore.Load().AutoUpdateLibrary)
                {
                    return;
                }

                // 去抖合并:1 秒内的连续变更只触发一次重扫
                _libraryWatchDebounce ??= DispatcherQueue.CreateTimer();
                _libraryWatchDebounce.Interval = TimeSpan.FromMilliseconds(1000);
                _libraryWatchDebounce.IsRepeating = false;
                _libraryWatchDebounce.Tick -= OnLibraryWatchDebounceTick;
                _libraryWatchDebounce.Tick += OnLibraryWatchDebounceTick;
                _libraryWatchDebounce.Start();
            });
        }

        private void OnLibraryWatchDebounceTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            if (_libraryRescanInProgress)
            {
                // 上一次重扫还没完成:稍后重试
                _libraryWatchDebounce?.Start();
                return;
            }

            NowPlayingText.Text = "媒体库有更新，正在自动刷新…";
            RescanLocalLibraryButton_Click(null!, new RoutedEventArgs());
        }

        private void AttachRootKeyboardAccelerators()
        {
            if (Content is not UIElement root)
            {
                return;
            }

            root.KeyDown -= Root_KeyDown;
            root.KeyDown += Root_KeyDown;
        }

        private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Handled)
            {
                return;
            }

            // 输入框内不抢快捷键
            if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or AutoSuggestBox)
            {
                return;
            }

            VirtualKey key = e.Key;
            if (key == VirtualKey.Space)
            {
                TogglePlayPausePublic();
                e.Handled = true;
            }
            else if (key == VirtualKey.Left)
            {
                SeekBySeconds(-5);
                e.Handled = true;
            }
            else if (key == VirtualKey.Right)
            {
                SeekBySeconds(5);
                e.Handled = true;
            }
            else if (key == VirtualKey.F || key == VirtualKey.F3)
            {
                OpenFindSongWindow();
                e.Handled = true;
            }
        }

        internal void ApplyExtendedSettingsLive(AppSettingsState settings)
        {
            // 歌词与桌面歌词设置最先应用，避免被其它子系统的异常阻断
            try
            {
                ApplyLyricPanelFromSettings(settings);
            }
            catch
            {
            }

            try
            {
                _desktopLyricsWindow?.ApplySettings(settings);
            }
            catch
            {
            }

            // 自定义背景图片：保存后即时应用
            try
            {
                ApplyCustomBackground(settings.CustomBackgroundPath);
            }
            catch
            {
            }

            // 播放列表列显隐/密度：保存后即时应用
            try
            {
                ApplyPlaylistColumnSettings(settings);
            }
            catch
            {
            }

            // 进度条样式：保存后即时切换
            try
            {
                bool wasWaveform = _progressBarStyle == "Waveform";
                _progressBarStyle = settings.ProgressBarStyle;
                RedrawProgressStyle();
                if (_progressBarStyle == "Waveform" && !wasWaveform)
                {
                    // 刚打开波形开关:立即加载当前播放(或选中)歌曲的波形
                    string? cur = _nowPlayingPath;
                    if (string.IsNullOrEmpty(cur) && PlaylistView.SelectedItem is PlaylistItem selItem)
                    {
                        cur = selItem.FilePath;
                    }

                    if (!string.IsNullOrEmpty(cur))
                    {
                        LoadWaveformForCurrentAsync(cur);
                    }
                }
            }
            catch
            {
            }

            // 主题色：全局资源键在启动时应用(重启完全生效)；
            // 保存后即时刷新自绘强调元素（选中高亮/导航/正在播放卡/排序按钮）
            try
            {
                _waveAccentColor = ResolveAccentColor();
                StartupLog.Write("主题色应用-波形: " + _waveAccentColor.ToString());
            }
            catch
            {
            }

            try
            {
                DrawWaveformBars();
            }
            catch (Exception waveEx)
            {
                StartupLog.Write("主题色应用-波形重绘异常: " + waveEx.Message);
            }

            // 歌词:用新主题色重新渲染当前行
            try
            {
                TimeSpan lyricPos = _audioEngine?.IsPlaying == true
                    ? EnginePositionValue
                    : (GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);
                SyncLyricsToPosition(lyricPos, force: true);
            }
            catch
            {
            }

            // 迷你播放器:刷新强调元素
            try
            {
                _miniPlayerWindow?.RefreshAccentFromOwner();
            }
            catch
            {
            }

            // 音量条(自绘)重绘 + 进度条主题色
            try
            {
                Windows.UI.Color accent2 = ResolveAccentColor();
                DrawVolumeStyle();
                ThemeColorService.ApplySliderAccent(ProgressSlider, accent2);
            }
            catch
            {
            }

            try
            {
                ApplyCapsuleSortButtonStyle(accent: true);
            }
            catch
            {
            }

            try
            {
                UpdateLibraryNavHighlight();
            }
            catch
            {
            }

            try
            {
                ApplyNowPlayingCardChrome();
            }
            catch
            {
            }

            try
            {
                ApplyAccentSelectionResources(PlaylistView);
                ApplyAccentSelectionResources(AlbumGridView);
                ApplyAccentSelectionResources(AlbumTrackListView);
                ApplyAccentSelectionResources(ArtistTrackListView);
                ApplyAccentSelectionResources(ArtistAlbumGridView);
                ApplyAccentSelectionResources(FolderBrowserView);
            }
            catch
            {
            }

            try
            {
                ConfigureSmtcFromSettings();
            }
            catch
            {
            }

            try
            {
                ApplyPlaybackRateFromSettings();
            }
            catch
            {
            }

            try
            {
                ApplyAudioChannelFromSettings();
            }
            catch
            {
            }

            try
            {
                ApplyAlwaysOnTopFromSettings();
            }
            catch
            {
            }

            try
            {
                UpdatePlaybackRateButtonText();
            }
            catch
            {
            }

            try
            {
                _hotkeys?.ApplyBindings(settings.CustomHotkeys);
                RestartGlobalHotkeysFromSettings();
            }
            catch
            {
            }

            try
            {
                RestartLibraryWatchFromSettings();
            }
            catch
            {
            }

            try
            {
                ApplyNavVisibilityFromSettings(settings);
            }
            catch
            {
            }

            try
            {
                ApplySpectrumVisibilityFromSettings(settings);
            }
            catch
            {
            }

            try
            {
                ApplyCoverVisibilityFromSettings(settings);
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(_nowPlayingPath)
                && settings.EnableBackground
                && settings.AlbumCoverAsBackground)
            {
                try
                {
                    // 模糊半径变更时重绘背景
                    byte[]? bytes = ExtractCoverBytes(_nowPlayingPath);
                    _ = ApplyAlbumArtBackgroundAsync(bytes, _nowPlayingPath);
                }
                catch
                {
                }
            }
            else if (!settings.EnableBackground || !settings.AlbumCoverAsBackground)
            {
                try
                {
                    ClearAlbumArtBackground();
                }
                catch
                {
                }
            }

            // 主题色变更：刷新列表，让选中高亮等立即使用新颜色
            try
            {
                ApplyCategoryView();
            }
            catch
            {
            }
        }

        private void ApplyNavVisibilityFromSettings(AppSettingsState settings)
        {
            if (NavFavoritesButton != null)
            {
                NavFavoritesButton.Visibility = settings.ShowNavFavorites ? Visibility.Visible : Visibility.Collapsed;
            }

            if (NavRecentButton != null)
            {
                NavRecentButton.Visibility = settings.ShowNavRecent ? Visibility.Visible : Visibility.Collapsed;
            }

            if (NavGenreButton != null)
            {
                NavGenreButton.Visibility = settings.ShowNavGenre ? Visibility.Visible : Visibility.Collapsed;
            }

            if (NavYearButton != null)
            {
                NavYearButton.Visibility = settings.ShowNavYear ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ApplyLyricPanelFromSettings(AppSettingsState settings)
        {
            if (LyricsPanel == null)
            {
                return;
            }

            LyricsPanel.Spacing = settings.LyricLineSpacing;
            TextAlignment align = settings.LyricAlign switch
            {
                "Left" => TextAlignment.Left,
                "Right" => TextAlignment.Right,
                "Auto" => TextAlignment.Center,
                _ => TextAlignment.Center
            };

            foreach (var child in LyricsPanel.Children)
            {
                if (child is TextBlock tb)
                {
                    tb.TextAlignment = align;
                }
            }
        }

        private void ApplySpectrumVisibilityFromSettings(AppSettingsState settings)
        {
            if (WaveformCanvas == null)
            {
                return;
            }

            WaveformCanvas.Visibility = settings.ShowSpectrum ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyCoverVisibilityFromSettings(AppSettingsState settings)
        {
            if (NowPlayingCoverBorder != null)
            {
                NowPlayingCoverBorder.Visibility = settings.ShowAlbumCover ? Visibility.Visible : Visibility.Collapsed;
            }

            if (TransportCoverBorder != null)
            {
                TransportCoverBorder.Visibility = settings.ShowAlbumCover ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void OnEqualizerApplied()
        {
            EqualizerState state = EqualizerStore.Load();
            // 把增益实际应用到引擎：共享模式（AudioGraph）与 HiFi(ASIO/共享 NAudio) 均生效；
            // WASAPI 独占为保持 bit-perfect 不使用 EQ。启用 EQ 后输出非 bit-perfect。
            _audioEngine?.SetEqualizer(state.BandGains);
            string onlyWEx = _audioEngine != null && _audioEngine.IsHiFiMode
                ? "（WASAPI 独占保持 bit-perfect，不使用 EQ）"
                : "";
            NowPlayingText.Text = "均衡器已应用" + onlyWEx;
        }

        private void OnTagsSaved(string path)
        {
            DispatcherQueue.TryEnqueue(() => RefreshTrackMetadataFromDisk(path));
        }

        private void RefreshTrackMetadataFromDisk(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                PlaylistItem fresh = CreatePlaylistItemFromPath(path);
                ReplaceMetadataInCollection(_playlist, fresh);
                ReplaceMetadataInCollection(_userPlaylist, fresh);
                if (string.Equals(_nowPlayingPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _ = UpdateNowPlayingPanelAsync(fresh);
                }

                if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                    || string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal)
                    || string.Equals(_currentCategory, "Recent", StringComparison.Ordinal))
                {
                    ApplyCategoryView();
                }

                NotifyCurrentPlaylistWindow();
            }
            catch (Exception ex)
            {
                _ = ShowErrorAsync("刷新标签失败", ex.Message);
            }
        }

        private static void ReplaceMetadataInCollection(
            System.Collections.ObjectModel.ObservableCollection<PlaylistItem> list,
            PlaylistItem fresh)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!string.Equals(list[i].FilePath, fresh.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                PlaylistItem old = list[i];
                old.Title = fresh.Title;
                old.Artist = fresh.Artist;
                old.AlbumArtist = fresh.AlbumArtist;
                old.Album = fresh.Album;
                old.Track = fresh.Track;
                old.Year = fresh.Year;
                old.Duration = fresh.Duration;
                old.DurationText = fresh.DurationText;
                old.Genre = fresh.Genre;
            }
        }

        private void AdjustVolumeBy(double delta)
        {
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
        }

        internal void SeekBySeconds(double seconds)
        {
            MediaPlayer? player = GetPlayer();
            if (player?.Source == null)
            {
                return;
            }

            try
            {
                TimeSpan duration = player.PlaybackSession.NaturalDuration;
                TimeSpan next = player.PlaybackSession.Position + TimeSpan.FromSeconds(seconds);
                if (next < TimeSpan.Zero)
                {
                    next = TimeSpan.Zero;
                }

                if (duration > TimeSpan.Zero && next > duration)
                {
                    next = duration;
                }

                player.PlaybackSession.Position = next;
            }
            catch
            {
            }
        }

        private void ToggleMainWindowVisibility()
        {
            try
            {
                if (AppWindow.IsVisible)
                {
                    AppWindow.Hide();
                }
                else
                {
                    AppWindow.Show();
                    Activate();
                }
            }
            catch
            {
                Activate();
            }
        }

        /// <summary>将歌曲插入当前播放项之后（下一首播放）。</summary>
        internal void PlaySongsNext(IEnumerable<PlaylistItem> songs)
        {
            List<PlaylistItem> incoming = songs
                .Where(s => !string.IsNullOrWhiteSpace(s.FilePath))
                .GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (incoming.Count == 0)
            {
                return;
            }

            string? playingPath = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                ? _userPlaylist[_userPlaylistIndex].FilePath
                : null;

            var incomingPaths = new HashSet<string>(
                incoming.Select(s => s.FilePath),
                StringComparer.OrdinalIgnoreCase);

            var withoutIncoming = _userPlaylist
                .Where(s => !incomingPaths.Contains(s.FilePath))
                .Select(ClonePlaylistItem)
                .ToList();

            int insertAt = 0;
            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                int playingIndex = withoutIncoming.FindIndex(s =>
                    string.Equals(s.FilePath, playingPath, StringComparison.OrdinalIgnoreCase));
                insertAt = playingIndex >= 0 ? playingIndex + 1 : 0;
            }

            var rebuilt = new List<PlaylistItem>(withoutIncoming.Count + incoming.Count);
            rebuilt.AddRange(withoutIncoming.Take(insertAt));
            rebuilt.AddRange(incoming.Select(ClonePlaylistItem));
            rebuilt.AddRange(withoutIncoming.Skip(insertAt));

            for (int i = 0; i < rebuilt.Count; i++)
            {
                rebuilt[i].Index = i + 1;
            }

            bool rebind = ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist);
            if (rebind)
            {
                PlaylistView.ItemsSource = null;
            }

            _userPlaylist = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>(rebuilt);
            if (rebind || string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                PlaylistView.ItemsSource = _userPlaylist;
            }

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                _userPlaylistIndex = FindUserPlaylistIndex(playingPath);
            }

            NotifyCurrentPlaylistWindow();
            NowPlayingText.Text = $"已加入下一首播放：{incoming.Count} 首";
        }

        internal void ToggleFavoriteForCurrent()
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
                return;
            }

            bool fav = TrackStatsStore.ToggleFavorite(path);
            NamedPlaylistStore.SyncFavoritesPlaylist();
            UpdateFavoriteButtonUi();
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                ApplyCategoryView();
            }

            NowPlayingText.Text = fav ? "已添加到我喜欢的音乐" : "已取消喜欢";
        }

        private void UpdateFavoriteButtonUi()
        {
            bool fav = !string.IsNullOrWhiteSpace(_nowPlayingPath)
                && (TrackStatsStore.Get(_nowPlayingPath)?.IsFavorite ?? false);

            if (FavoriteButtonIcon != null)
            {
                FavoriteButtonIcon.Glyph = fav ? "\uEB52" : "\uEB51";
                ToolTipService.SetToolTip(FavoriteButton, fav ? "取消喜欢" : "我喜欢的音乐");
            }
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
            => ToggleFavoriteForCurrent();

        // ---- 播放歌曲信息页（状态条封面进入；左上角倒三角箭头返回） ----
        private void TransportCover_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 点击状态条封面：在主程序（状态条上方）展开播放歌曲信息页
            if (NowPlayingPane == null || string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                return;
            }

            if (_nowPlayingPaneOpen)
            {
                CloseNowPlayingPane();
            }
            else
            {
                OpenNowPlayingPane();
            }
        }

        /// <summary>播放信息页展开/收起动画（XAML 资源内定义的 Storyboard）。</summary>
        private Microsoft.UI.Xaml.Media.Animation.Storyboard? NowPlayingOpenAnimation
            => NowPlayingPane?.Resources["NowPlayingOpenStoryboard"] as Microsoft.UI.Xaml.Media.Animation.Storyboard;
        private Microsoft.UI.Xaml.Media.Animation.Storyboard? NowPlayingCloseAnimation
            => NowPlayingPane?.Resources["NowPlayingCloseStoryboard"] as Microsoft.UI.Xaml.Media.Animation.Storyboard;

        /// <summary>展开播放信息页：淡入 + 轻微放大。</summary>
        private void OpenNowPlayingPane()
        {
            if (NowPlayingPane == null)
            {
                return;
            }

            try
            {
                NowPlayingCloseAnimation?.Stop();
            }
            catch
            {
            }

            NowPlayingPane.Opacity = 0;
            NowPlayingPane.Visibility = Visibility.Visible;
            _nowPlayingPaneOpen = true;
            DispatcherQueue.TryEnqueue(() => UpdateNowPlayingCardLayout());
            try
            {
                NowPlayingOpenAnimation?.Begin();
            }
            catch
            {
                NowPlayingPane.Opacity = 1;
            }
        }

        /// <summary>收起播放信息页：淡出 + 轻微缩小，动画结束后折叠。</summary>
        private void CloseNowPlayingPane()
        {
            if (NowPlayingPane == null || !_nowPlayingPaneOpen)
            {
                return;
            }

            _nowPlayingPaneOpen = false;
            try
            {
                NowPlayingCloseAnimation?.Stop();
                NowPlayingCloseAnimation.Completed -= NowPlayingCloseAnimation_Completed;
                NowPlayingCloseAnimation.Completed += NowPlayingCloseAnimation_Completed;
                NowPlayingOpenAnimation?.Stop();
                NowPlayingCloseAnimation?.Begin();
            }
            catch
            {
                NowPlayingPane.Visibility = Visibility.Collapsed;
                NowPlayingPane.Opacity = 1;
            }
        }

        private void NowPlayingCloseAnimation_Completed(object? sender, object e)
        {
            if (NowPlayingPane != null)
            {
                NowPlayingPane.Visibility = Visibility.Collapsed;
                NowPlayingPane.Opacity = 1;
            }
        }

        private void NowPlayingCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            // 左上角倒三角箭头：收起播放歌曲信息页，返回原音乐库/播放列表视图
            CloseNowPlayingPane();
        }

        /// <summary>状态条封面 hover：高亮描边 + 强调背景，提示可点击展开播放信息页。</summary>
        private void TransportCover_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border b)
            {
                return;
            }

            try
            {
                var hoverBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(ResolveAccentColor());
                b.BorderBrush = hoverBrush;
                b.BorderThickness = new Thickness(2);
                b.Background = ResolveAccentBrush();
            }
            catch
            {
            }
        }

        private void TransportCover_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border b)
            {
                return;
            }

            try
            {
                b.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
                b.BorderThickness = new Thickness(1);
                b.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            }
            catch
            {
            }
        }

        private void SeekBackButton_Click(object sender, RoutedEventArgs e)
            => SeekBySeconds(-5);

        private void SeekForwardButton_Click(object sender, RoutedEventArgs e)
            => SeekBySeconds(5);

        private void FeaturesMoreButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };

            var eq = new MenuFlyoutItem { Text = "均衡器…" };
            eq.Click += (_, _) => EqualizerWindow.ShowOrActivate();
            flyout.Items.Add(eq);

            var find = new MenuFlyoutItem { Text = "查找歌曲…" };
            find.Click += (_, _) => OpenFindSongWindow();
            flyout.Items.Add(find);

            var onlineSearch = new MenuFlyoutItem { Text = "在线搜索…" };
            onlineSearch.Click += (_, _) => OnlineSearchWindow.ShowOrActivate();
            flyout.Items.Add(onlineSearch);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var downloadLyric = new MenuFlyoutItem { Text = "下载当前歌词" };
            downloadLyric.Click += (_, _) => _ = DownloadLyricForCurrentAsync();
            flyout.Items.Add(downloadLyric);

            var batchLyric = new MenuFlyoutItem { Text = "批量下载歌词…" };
            batchLyric.Click += async (_, _) => await BatchDownloadLyricsAsync();
            flyout.Items.Add(batchLyric);

            var dupCheck = new MenuFlyoutItem { Text = "重复文件检测…" };
            dupCheck.Click += (_, _) => DuplicateFilesWindow.ShowOrActivate(this);
            flyout.Items.Add(dupCheck);

            var downloadCover = new MenuFlyoutItem { Text = "下载当前封面" };
            downloadCover.Click += (_, _) => _ = DownloadCoverForCurrentAsync();
            flyout.Items.Add(downloadCover);

            var editLyric = new MenuFlyoutItem { Text = "编辑歌词…" };
            editLyric.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(_nowPlayingPath))
                {
                    LyricsEditorWindow.Show(_nowPlayingPath);
                }
            };
            flyout.Items.Add(editLyric);

            var editTag = new MenuFlyoutItem { Text = "编辑标签…" };
            editTag.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(_nowPlayingPath))
                {
                    TagEditorWindow.Show(_nowPlayingPath);
                }
            };
            flyout.Items.Add(editTag);

            flyout.Items.Add(new MenuFlyoutSeparator());



            var sleepTimer = new MenuFlyoutItem { Text = "睡眠定时器…" };
            sleepTimer.Click += async (_, _) => await ShowSleepTimerDialogAsync();
            flyout.Items.Add(sleepTimer);

            var enginePreview = new MenuFlyoutItem { Text = "音频引擎预览（当前曲目）" };
            enginePreview.Click += async (_, _) => await PreviewWithEngineAsync();
            flyout.Items.Add(enginePreview);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var importM3u = new MenuFlyoutItem { Text = "导入 M3U 播放列表…" };
            importM3u.Click += ImportM3u_Click;
            flyout.Items.Add(importM3u);

            var exportM3u = new MenuFlyoutItem { Text = "导出当前列表为 M3U…" };
            exportM3u.Click += ExportM3u_Click;
            flyout.Items.Add(exportM3u);

            var openCue = new MenuFlyoutItem { Text = "打开 CUE…" };
            openCue.Click += OpenCue_Click;
            flyout.Items.Add(openCue);

            var convert = new MenuFlyoutItem { Text = "转换当前曲目格式…" };
            convert.Click += ConvertCurrent_Click;
            flyout.Items.Add(convert);

            if (sender is FrameworkElement fe)
            {
                flyout.ShowAt(fe);
            }
        }


        /// <summary>批量下载歌词（对齐 MusicPlayer2）：可选范围并跳过已有歌词。</summary>
        private async Task BatchDownloadLyricsAsync()
        {
            var radio = new RadioButtons();
            radio.Items.Add("媒体库所有歌曲");
            radio.Items.Add("当前播放列表");
            radio.Items.Add("我喜欢的音乐");
            radio.SelectedIndex = 0;

            var skipBox = new CheckBox
            {
                Content = "跳过已有歌词的歌曲",
                IsChecked = true,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
            panel.Children.Add(new TextBlock { Text = "选择下载范围", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(radio);
            panel.Children.Add(skipBox);

            var dialog = new ContentDialog
            {
                Title = "批量下载歌词",
                Content = panel,
                PrimaryButtonText = "开始",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            List<PlaylistItem> songs;
            switch (radio.SelectedIndex)
            {
                case 1:
                    songs = _userPlaylist.ToList();
                    break;
                case 2:
                    songs = _playlist.Where(t => TrackStatsStore.Get(t.FilePath)?.IsFavorite ?? false).ToList();
                    break;
                default:
                    songs = _playlist.ToList();
                    break;
            }

            if (songs.Count == 0)
            {
                NowPlayingText.Text = "没有可下载的歌曲";
                return;
            }

            bool skipExisting = skipBox.IsChecked == true;
            int ok = 0;
            int skip = 0;
            for (int i = 0; i < songs.Count; i++)
            {
                PlaylistItem song = songs[i];
                string lrcPath = Path.ChangeExtension(song.FilePath, ".lrc");
                if (skipExisting && System.IO.File.Exists(lrcPath))
                {
                    skip++;
                    continue;
                }

                NowPlayingText.Text = $"正在批量下载歌词 ({i + 1}/{songs.Count})…";
                string? path = await OnlineMusicApi.SearchAndDownloadLyricAsync(song.Title, song.Artist, song.FilePath);
                if (path != null)
                {
                    ok++;
                }
            }

            NowPlayingText.Text = $"批量下载完成：成功 {ok}，跳过 {skip}，共 {songs.Count} 首";
        }


        private async Task ShowSleepTimerDialogAsync()
        {
            var radio = new RadioButtons();
            radio.Items.Add("关闭定时器");
            radio.Items.Add("15 分钟");
            radio.Items.Add("30 分钟");
            radio.Items.Add("60 分钟");
            radio.Items.Add("90 分钟");
            radio.Items.Add("120 分钟");
            radio.Items.Add("当前曲目播完后停止");
            radio.Items.Add("再播放指定曲目数后停止");
            radio.SelectedIndex = 2;

            var numberBox = new NumberBox
            {
                Minimum = 1,
                Maximum = 99,
                Value = 1,
                Header = "曲目数",
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Visibility = Visibility.Collapsed
            };
            radio.SelectionChanged += (_, _) =>
            {
                numberBox.Visibility = radio.SelectedIndex == 7 ? Visibility.Visible : Visibility.Collapsed;
            };

            var panel = new StackPanel { Spacing = 8, MinWidth = 260 };
            panel.Children.Add(new TextBlock { Text = "睡眠定时器：到时自动暂停播放", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(radio);
            panel.Children.Add(numberBox);

            var dialog = new ContentDialog
            {
                Title = "睡眠定时器",
                Content = panel,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _sleepTimer?.Stop();
            _sleepTimer = null;
            _sleepMode = SleepStopMode.None;
            _sleepTracksRemaining = 0;

            int sel = radio.SelectedIndex;
            if (sel == 0)
            {
                NowPlayingText.Text = "睡眠定时器已关闭";
                return;
            }

            if (sel == 6)
            {
                _sleepMode = SleepStopMode.AfterTrack;
                NowPlayingText.Text = "睡眠定时器：当前曲目播完后停止";
                return;
            }

            if (sel == 7)
            {
                int n = (int)Math.Clamp(Math.Round(numberBox.Value), 1, 99);
                _sleepMode = SleepStopMode.AfterTracks;
                _sleepTracksRemaining = n;
                NowPlayingText.Text = $"睡眠定时器：再播放 {n} 首后停止";
                return;
            }

            int minutes = sel switch
            {
                1 => 15,
                2 => 30,
                3 => 60,
                4 => 90,
                _ => 120
            };
            _sleepMode = SleepStopMode.AfterMinutes;
            _sleepTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _sleepTimer.Interval = TimeSpan.FromMinutes(minutes);
            _sleepTimer.Tick += (_, _) => StopForSleep("睡眠定时器到点，播放已暂停");
            _sleepTimer.Start();
            NowPlayingText.Text = $"睡眠定时器已设置：{minutes} 分钟后停止播放";
        }

        /// <summary>播放结束拦截：返回 true 表示已按睡眠定时器停止，调用方不应继续切歌。</summary>
        private bool ConsumeSleepStopIfDue()
        {
            if (_sleepMode == SleepStopMode.AfterTrack)
            {
                StopForSleep("睡眠定时器：当前曲目已播完，播放已停止");
                return true;
            }

            if (_sleepMode == SleepStopMode.AfterTracks)
            {
                _sleepTracksRemaining--;
                if (_sleepTracksRemaining <= 0)
                {
                    StopForSleep("睡眠定时器：指定曲目数已播完，播放已停止");
                    return true;
                }
            }

            return false;
        }

        /// <summary>统一停止播放并清除睡眠定时器状态。</summary>
        private void StopForSleep(string message)
        {
            _sleepTimer?.Stop();
            _sleepTimer = null;
            _sleepMode = SleepStopMode.None;
            _sleepTracksRemaining = 0;
            try
            {
                MediaPlayer? p = GetPlayer();
                p?.Pause();
                _audioEngine?.Pause();
            }
            catch
            {
            }

            NowPlayingText.Text = message;
        }


        /// <summary>音频引擎（AudioGraph）预览：验证真实均衡器与新播放管线。</summary>
        private async Task PreviewWithEngineAsync()
        {
            // 先暂停正在播放的 MediaPlayer，避免预览与播放双声
            try
            {
                MediaPlayer? playing = GetPlayer();
                if (playing != null && playing.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    playing.Pause();
                }
            }
            catch
            {
            }

            string? path = _nowPlayingPath;
            if (string.IsNullOrWhiteSpace(path) && _currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                path = _playlist[_currentIndex].FilePath;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                NowPlayingText.Text = "无当前曲目";
                return;
            }

            _audioEngine ??= new AudioPlaybackEngine();
            _audioEngine.PlaybackEnded -= EnginePreviewEnded;
            _audioEngine.PlaybackEnded += EnginePreviewEnded;

            try
            {
                _audioEngine.SetEqualizer(EqualizerStore.Load().BandGains);
            }
            catch
            {
            }

            bool ok = await _audioEngine.PlayFileAsync(path);
            NowPlayingText.Text = ok
                ? "音频引擎预览中（含真实均衡器）"
                : "引擎预览失败（系统可能不支持该格式，见输出）";
        }

        private void EnginePreviewEnded()
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (NowPlayingText != null)
                {
                    NowPlayingText.Text = "引擎预览播放结束";
                }
            });
        }

        private void OpenFindSongWindow()
        {
            var tracks = _playlist.Select(p => (p.Title, p.Artist, p.Album, p.FilePath));
            FindSongWindow.Show(
                tracks,
                onPlay: path =>
                {
                    PlaylistItem? item = FindLibraryItemByPath(path);
                    if (item != null)
                    {
                        PlayPlaylistItem(item);
                    }
                },
                onAddToPlaylist: path =>
                {
                    PlaylistItem? item = FindLibraryItemByPath(path);
                    if (item != null)
                    {
                        AddSongsToUserPlaylist(new[] { item });
                    }
                },
                onPlayNext: path =>
                {
                    PlaylistItem? item = FindLibraryItemByPath(path);
                    if (item != null)
                    {
                        PlaySongsNext(new[] { item });
                    }
                });
        }

        private PlaylistItem? FindLibraryItemByPath(string path)
        {
            int index = FindLibraryIndex(path);
            return index >= 0 ? _playlist[index] : null;
        }

        private async void ImportM3u_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".m3u");
                picker.FileTypeFilter.Add(".m3u8");
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                List<string> paths = M3uPlaylistIO.Parse(file.Path, existingOnly: true);
                if (paths.Count == 0)
                {
                    await ShowErrorAsync("导入 M3U", "未找到有效音频路径。");
                    return;
                }

                await AddFilesToLibraryAsync(paths);
                List<PlaylistItem> items = paths
                    .Select(FindLibraryItemByPath)
                    .Where(i => i != null)
                    .Cast<PlaylistItem>()
                    .ToList();
                AddSongsToUserPlaylist(items);
                NowPlayingText.Text = $"已导入 M3U：{items.Count} 首";
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("导入 M3U 失败", ex.Message);
            }
        }

        private async void ExportM3u_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileSavePicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedFileName = "playlist";
                picker.FileTypeChoices.Add("M3U8 播放列表", new List<string> { ".m3u8" });
                Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                var paths = _userPlaylist.Select(s => s.FilePath).ToList();
                var entries = _userPlaylist
                    .Select(s => (s.FilePath, s.Title, s.Artist, s.Duration.TotalSeconds))
                    .ToList();
                M3uPlaylistIO.WriteM3u8(file.Path, paths, entries);
                NowPlayingText.Text = "已导出 M3U：" + file.Path;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("导出 M3U 失败", ex.Message);
            }
        }

        private async void OpenCue_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".cue");
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                List<CueTrack> tracks = CueSheetParser.LoadCue(file.Path);
                if (tracks.Count == 0)
                {
                    await ShowErrorAsync("打开 CUE", "未能解析曲目。");
                    return;
                }

                var cueItems = new List<PlaylistItem>();
                foreach (CueTrack cueTrack in tracks)
                {
                    if (string.IsNullOrWhiteSpace(cueTrack.FilePath) || !System.IO.File.Exists(cueTrack.FilePath))
                    {
                        continue;
                    }

                    PlaylistItem cueItem = CreatePlaylistItemFromPath(cueTrack.FilePath);
                    if (!string.IsNullOrWhiteSpace(cueTrack.Title))
                    {
                        cueItem.Title = cueTrack.Title;
                    }

                    if (!string.IsNullOrWhiteSpace(cueTrack.Artist))
                    {
                        cueItem.Artist = cueTrack.Artist;
                        cueItem.AlbumArtist = cueTrack.Artist;
                    }

                    cueItem.StartTimeSeconds = cueTrack.StartTime.TotalSeconds;
                    cueItems.Add(cueItem);
                }

                if (cueItems.Count == 0)
                {
                    await ShowErrorAsync("打开 CUE", "CUE 引用的音频文件不存在。");
                    return;
                }

                AddSongsToUserPlaylist(cueItems);
                NowPlayingText.Text = $"已从 CUE 导入 {cueItems.Count} 轨";
                PlayPlaylistItem(cueItems[0]);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("打开 CUE 失败", ex.Message);
            }
        }

        private async void ConvertCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                return;
            }

            try
            {
                var picker = new FileSavePicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_nowPlayingPath);
                picker.FileTypeChoices.Add("WAV", new List<string> { ".wav" });
                picker.FileTypeChoices.Add("MP3", new List<string> { ".mp3" });
                picker.FileTypeChoices.Add("FLAC", new List<string> { ".flac" });
                picker.FileTypeChoices.Add("OGG", new List<string> { ".ogg" });
                Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                NowPlayingText.Text = "正在转换…";
                string format = Path.GetExtension(file.Path).TrimStart('.').ToLowerInvariant();
                (bool ok, string message) = await FormatConvertService.ConvertAsync(_nowPlayingPath, file.Path, format);
                NowPlayingText.Text = ok ? "转换完成：" + file.Path : "转换失败";
                if (!ok)
                {
                    await ShowErrorAsync("格式转换", message);
                }
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("格式转换失败", ex.Message);
            }
        }

        private async Task DownloadLyricForCurrentAsync()
        {
            if (string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                return;
            }

            PlaylistItem? item = FindLibraryItemByPath(_nowPlayingPath)
                ?? (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                    ? _userPlaylist[_userPlaylistIndex]
                    : null);
            if (item == null)
            {
                return;
            }

            NowPlayingText.Text = "正在下载歌词…";
            string? path = await OnlineMusicApi.SearchAndDownloadLyricAsync(item.Title, item.Artist, item.FilePath);
            if (path == null)
            {
                NowPlayingText.Text = "未找到可下载的歌词";
                return;
            }

            List<LyricLine> lyrics = await Task.Run(() => LyricsLoader.LoadForAudio(item.FilePath));
            if (string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                BuildLyricsUi(lyrics);
            }

            NowPlayingText.Text = "歌词已下载：" + Path.GetFileName(path);
        }

        private async Task DownloadCoverForCurrentAsync()
        {
            if (string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                return;
            }

            PlaylistItem? item = FindLibraryItemByPath(_nowPlayingPath);
            if (item == null)
            {
                return;
            }

            NowPlayingText.Text = "正在下载封面…";
            bool ok = await OnlineMusicApi.DownloadAndEmbedCoverAsync(item.Title, item.Artist, item.FilePath);
            if (ok)
            {
                InvalidateCoverCache(item.FilePath);
            }

            if (!ok)
            {
                NowPlayingText.Text = "未找到可下载的封面";
                return;
            }

            await UpdateNowPlayingPanelAsync(item);
            NowPlayingText.Text = "封面已更新";
        }

        private void ScrobblePreviousIfAny()
        {
            try
            {
                if (_lastPlayedForScrobble == null || _lastPlayStartUtc == default)
                {
                    return;
                }

                PlaylistItem prev = _lastPlayedForScrobble;
                LastFmScrobbler.QueueScrobble(new LastFmTrackInfo
                {
                    Artist = prev.Artist,
                    Title = prev.Title,
                    Album = prev.Album,
                    DurationSeconds = (int)prev.Duration.TotalSeconds
                }, _lastPlayStartUtc);
                _ = LastFmScrobbler.FlushQueueAsync();
            }
            catch
            {
            }
            finally
            {
                _lastPlayedForScrobble = null;
                _lastPlayStartUtc = default;
            }
        }

        private void RecordPlaybackStatsOnStart(PlaylistItem item)
        {
            try
            {
                _lastPlayedForScrobble = item;
                _lastPlayStartUtc = DateTime.UtcNow;
                TrackStatsStore.RecordPlayStart(item.FilePath);
                _listenSamplePath = item.FilePath;
                _lastListenSampleUtc = DateTime.UtcNow;
                UpdateFavoriteButtonUi();

                if (AppSettingsStore.Load() is { EnableLastFm: true, LastFmNowPlaying: true })
                {
                    LastFmScrobbler.QueueNowPlaying(new LastFmTrackInfo
                    {
                        Artist = item.Artist,
                        Title = item.Title,
                        Album = item.Album,
                        DurationSeconds = (int)item.Duration.TotalSeconds
                    });
                    _ = LastFmScrobbler.FlushQueueAsync();
                }
            }
            catch
            {
            }
        }

        private void TickFeaturePlaybackExtras(TimeSpan position)
        {
            MediaPlayer? player = GetPlayer();
            if (player?.Source == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_listenSamplePath)
                && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                DateTime now = DateTime.UtcNow;
                double delta = (now - _lastListenSampleUtc).TotalSeconds;
                if (delta >= 1)
                {
                    TrackStatsStore.AddListenSeconds(_listenSamplePath, delta);
                    _lastListenSampleUtc = now;
                }
            }
        }

        private async Task MaybeAutoDownloadExtrasAsync(PlaylistItem item, List<LyricLine> existingLyrics, byte[]? coverBytes)
        {
            AppSettingsState settings = AppSettingsStore.Load();
            bool tagsFull = !string.IsNullOrWhiteSpace(item.Title)
                && !string.Equals(item.Title, Path.GetFileNameWithoutExtension(item.FilePath), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.Artist)
                && !string.Equals(item.Artist, "未知艺术家", StringComparison.Ordinal);

            if (settings.AutoDownloadOnlyWhenTagFull && !tagsFull)
            {
                return;
            }

            if (settings.AutoDownloadLyrics && existingLyrics.Count == 0)
            {
                string? lyricPath = await OnlineMusicApi.SearchAndDownloadLyricAsync(item.Title, item.Artist, item.FilePath);
                if (lyricPath != null && string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    List<LyricLine> lyrics = await Task.Run(() => LyricsLoader.LoadForAudio(item.FilePath));
                    BuildLyricsUi(lyrics);
                }
            }

            if (settings.AutoDownloadCover && (coverBytes == null || coverBytes.Length == 0))
            {
                bool ok = await OnlineMusicApi.DownloadAndEmbedCoverAsync(item.Title, item.Artist, item.FilePath);
                if (ok)
                {
                    InvalidateCoverCache(item.FilePath);
                }

                if (ok && string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    await UpdateNowPlayingPanelAsync(item);
                }
            }
        }

        private void ApplyFavoritesOrRecentCategory()
        {
            LibraryPaneTitle.Visibility = Visibility.Visible;
            MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Visible;
            SetSongSortUiForCategory(isUserPlaylist: false);
            AlbumSortButton.Visibility = Visibility.Collapsed;
            PlaylistListBorder.Visibility = Visibility.Visible;
            AlbumListBorder.Visibility = Visibility.Collapsed;
            ArtistListBorder.Visibility = Visibility.Collapsed;
            FolderListBorder.Visibility = Visibility.Collapsed;
            CloseAlbumDetailUi();
            CloseArtistDetailUi();

            IReadOnlyList<string> paths = string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal)
                ? TrackStatsStore.GetAllFavorites()
                : TrackStatsStore.GetRecentlyPlayed(100, AppSettingsStore.Load().RecentPlayedRangeDays);

            var items = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (string path in paths)
            {
                PlaylistItem? fromLib = FindLibraryItemByPath(path);
                if (fromLib != null)
                {
                    items.Add(ClonePlaylistItem(fromLib));
                }
                else if (System.IO.File.Exists(path))
                {
                    try
                    {
                        items.Add(CreatePlaylistItemFromPath(path));
                    }
                    catch
                    {
                    }
                }
            }

            RenumberCollection(items);
            PlaylistView.ItemsSource = items;
            LibraryPaneTitle.Text = string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal)
                ? "我喜欢的音乐"
                : "最近播放";
        }
        /// <summary>播放最多：按播放次数降序显示媒体库歌曲。</summary>
        private void ApplyMostPlayedCategory()
        {
            LibraryPaneTitle.Visibility = Visibility.Visible;
            MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Visible;
            SetSongSortUiForCategory(isUserPlaylist: false);
            AlbumSortButton.Visibility = Visibility.Collapsed;
            PlaylistListBorder.Visibility = Visibility.Visible;
            AlbumListBorder.Visibility = Visibility.Collapsed;
            ArtistListBorder.Visibility = Visibility.Collapsed;
            FolderListBorder.Visibility = Visibility.Collapsed;
            CloseAlbumDetailUi();
            CloseArtistDetailUi();

            var col = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (PlaylistItem track in _playlist)
            {
                int count = TrackStatsStore.Get(track.FilePath)?.PlayCount ?? 0;
                if (count > 0)
                {
                    col.Add(ClonePlaylistItem(track));
                }
            }

            var sorted = col
                .Select(x => new { Item = x, Count = TrackStatsStore.Get(x.FilePath)?.PlayCount ?? 0 })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Item)
                .ToList();

            var result = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (PlaylistItem item in sorted)
            {
                result.Add(item);
            }

            RenumberCollection(result);
            PlaylistView.ItemsSource = result;
            LibraryPaneTitle.Text = "播放最多";
        }


        private void AppendPlaylistContextFeatureItems(MenuFlyout flyout, PlaylistItem song, bool inUserPlaylist)
        {
            var playNext = new MenuFlyoutItem { Text = "下一首播放" };
            playNext.Icon = new FontIcon { Glyph = "\uE893" };
            playNext.Click += (_, _) => PlaySongsNext(new[] { song });
            flyout.Items.Insert(1, playNext);

            bool isFav = TrackStatsStore.Get(song.FilePath)?.IsFavorite ?? false;
            var fav = new MenuFlyoutItem { Text = isFav ? "取消喜欢" : "添加到我喜欢的音乐" };
            fav.Icon = new FontIcon { Glyph = isFav ? "\uEB52" : "\uEB51" };
            fav.Click += (_, _) =>
            {
                TrackStatsStore.ToggleFavorite(song.FilePath);
                NamedPlaylistStore.SyncFavoritesPlaylist();
                UpdateFavoriteButtonUi();
                if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
                {
                    ApplyCategoryView();
                }
            };
            flyout.Items.Add(fav);

            var ratingFlyout = new MenuFlyoutSubItem { Text = "评分" };
            for (int r = 0; r <= 5; r++)
            {
                int rating = r;
                var item = new MenuFlyoutItem { Text = rating == 0 ? "未评分" : new string('★', rating) };
                item.Click += (_, _) => TrackStatsStore.SetRating(song.FilePath, rating);
                ratingFlyout.Items.Add(item);
            }

            flyout.Items.Add(ratingFlyout);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var tag = new MenuFlyoutItem { Text = "编辑标签…" };
            tag.Click += (_, _) => TagEditorWindow.Show(song.FilePath);
            flyout.Items.Add(tag);

            var lyric = new MenuFlyoutItem { Text = "编辑歌词…" };
            lyric.Click += (_, _) => LyricsEditorWindow.Show(song.FilePath);
            flyout.Items.Add(lyric);

            var dlLyric = new MenuFlyoutItem { Text = "下载歌词" };
            dlLyric.Click += async (_, _) =>
            {
                string? path = await OnlineMusicApi.SearchAndDownloadLyricAsync(song.Title, song.Artist, song.FilePath);
                NowPlayingText.Text = path != null ? "歌词已下载" : "未找到歌词";
            };
            flyout.Items.Add(dlLyric);

            var dlCover = new MenuFlyoutItem { Text = "下载封面" };
            dlCover.Click += async (_, _) =>
            {
                bool ok = await OnlineMusicApi.DownloadAndEmbedCoverAsync(song.Title, song.Artist, song.FilePath);
                NowPlayingText.Text = ok ? "封面已更新" : "未找到封面";
            };
            flyout.Items.Add(dlCover);

            var onlineSearch = new MenuFlyoutItem { Text = "在线搜索…" };
            onlineSearch.Icon = new FontIcon { Glyph = "" };
            onlineSearch.Click += (_, _) => OnlineSearchWindow.ShowOrActivate(song.Title);
            flyout.Items.Add(onlineSearch);


        }

        private void AppendHamburgerFeatureItems(MenuFlyout flyout)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            var find = new MenuFlyoutItem { Text = "查找歌曲…" };
            find.Icon = new FontIcon { Glyph = "\uE721" };
            find.Click += (_, _) => OpenFindSongWindow();
            flyout.Items.Add(find);

            var eq = new MenuFlyoutItem { Text = "均衡器…" };
            eq.Icon = new FontIcon { Glyph = "\uE9E9" };
            eq.Click += (_, _) => EqualizerWindow.ShowOrActivate();
            flyout.Items.Add(eq);

            var tools = new MenuFlyoutSubItem { Text = "工具" };
            var importM3u = new MenuFlyoutItem { Text = "导入 M3U…" };
            importM3u.Click += ImportM3u_Click;
            tools.Items.Add(importM3u);
            var exportM3u = new MenuFlyoutItem { Text = "导出 M3U…" };
            exportM3u.Click += ExportM3u_Click;
            tools.Items.Add(exportM3u);
            var cue = new MenuFlyoutItem { Text = "打开 CUE…" };
            cue.Click += OpenCue_Click;
            tools.Items.Add(cue);
            flyout.Items.Add(tools);
        }

        private async Task AddFilesToLibraryAsync(IEnumerable<string> paths)
        {
            // 复用现有导入逻辑：走批量添加入口
            List<string> list = paths.Where(System.IO.File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0)
            {
                return;
            }

            int before = _playlist.Count;
            foreach (string path in list)
            {
                if (FindLibraryIndex(path) >= 0)
                {
                    continue;
                }

                try
                {
                    _playlist.Add(CreatePlaylistItemFromPath(path));
                }
                catch
                {
                }
            }

            if (_playlist.Count != before)
            {
                RenumberCollection(_playlist);
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath));
            }
        }
    }
}
