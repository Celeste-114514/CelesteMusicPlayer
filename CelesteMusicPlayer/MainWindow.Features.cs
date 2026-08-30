using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _hotkeys?.StopListening();
                _hotkeys?.Dispose();
                _hotkeys = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _libraryWatch.Stop();
                _libraryWatch.Dispose();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void RestartGlobalHotkeysFromSettings()
        {
            try
            {
                _hotkeys?.StopListening();
                _hotkeys?.Dispose();
                _hotkeys = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _desktopLyricsWindow?.ApplySettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 自定义背景图片：保存后即时应用
            try
            {
                ApplyCustomBackground(settings.CustomBackgroundPath);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 播放列表列显隐/密度：保存后即时应用
            try
            {
                ApplyPlaylistColumnSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 主题色：全局资源键在启动时应用(重启完全生效)；
            // 保存后即时刷新自绘强调元素（选中高亮/导航/正在播放卡/排序按钮）
            try
            {
                _waveAccentColor = ResolveAccentColor();
                StartupLog.Write("主题色应用-波形: " + _waveAccentColor.ToString());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 迷你播放器:刷新强调元素
            try
            {
                _miniPlayerWindow?.RefreshAccentFromOwner();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 音量条(自绘)重绘 + 进度条主题色
            try
            {
                Windows.UI.Color accent2 = ResolveAccentColor();
                DrawVolumeStyle();
                ThemeColorService.ApplySliderAccent(ProgressSlider, accent2);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyCapsuleSortButtonStyle(accent: true);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                UpdateLibraryNavHighlight();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyNowPlayingCardChrome();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyAccentSelectionResources(PlaylistView);
                ApplyAccentSelectionResources(AlbumGridView);
                ApplyAccentSelectionResources(AlbumTrackListView);
                ApplyAccentSelectionResources(ArtistTrackListView);
                ApplyAccentSelectionResources(ArtistAlbumGridView);
                ApplyAccentSelectionResources(FolderBrowserView);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ConfigureSmtcFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyPlaybackRateFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyAudioChannelFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyAlwaysOnTopFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                UpdatePlaybackRateButtonText();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _hotkeys?.ApplyBindings(settings.CustomHotkeys);
                RestartGlobalHotkeysFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                RestartLibraryWatchFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyNavVisibilityFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplySpectrumVisibilityFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyCoverVisibilityFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

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
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            }
            else if (!settings.EnableBackground || !settings.AlbumCoverAsBackground)
            {
                try
                {
                    ClearAlbumArtBackground();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            }

            // 主题色变更：刷新列表，让选中高亮等立即使用新颜色
            try
            {
                ApplyCategoryView();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
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
            // 把增益实际应用到引擎：共享 / ASIO / 原生 WASAPI 独占 均可走统一 DSP 链；启用 EQ 后输出非 bit-perfect。
            _audioEngine?.SetEqualizer(state.BandGains);
            bool any = state.BandGains != null && state.BandGains.Any(g => Math.Abs(g) > 0.01);
            NowPlayingText.Text = any
                ? "均衡器已应用（输出非 bit-perfect）"
                : "均衡器已应用（bit-perfect 直通）";
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
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

            // 任务栏缩略图按钮：fav=true 实心红心 / fav=false 空心轮廓心
            _taskbarButtons?.UpdateFavorite(fav);

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

            // 任务栏缩略图按钮：把当前曲目的收藏状态同步到 thumbar
            // （启动恢复上次播放时，thumbar 加载完成就会用对的图标）
            _taskbarButtons?.UpdateFavorite(fav);
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
            => ToggleFavoriteForCurrent();

        // ---- 播放歌曲信息页（状态条封面进入；左上角倒三角箭头返回） ----
        private void TransportCover_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 点击状态条封面：在主程序（状态条上方）切换播放歌曲信息页（整合式视图切换）
            if (NowPlayingPane == null || string.IsNullOrWhiteSpace(_nowPlayingPath))
            {
                return;
            }

            SetNowPlayingPaneVisible(!_nowPlayingPaneOpen);
        }

        /// <summary>景深切换：切出播放信息页时面板前推、主内容区(含左侧分类)退后变暗；收起则反向恢复。</summary>
        private void SetNowPlayingPaneVisible(bool visible)
        {
            _nowPlayingPaneOpen = visible;
            if (NowPlayingPane == null)
            {
                return;
            }

            var mcBack = MainContentGrid?.Resources["MainContentDepthBackStoryboard"]
                as Microsoft.UI.Xaml.Media.Animation.Storyboard;
            var mcRestore = MainContentGrid?.Resources["MainContentDepthRestoreStoryboard"]
                as Microsoft.UI.Xaml.Media.Animation.Storyboard;
            var depthIn = NowPlayingPane.Resources["NowPlayingDepthInStoryboard"]
                as Microsoft.UI.Xaml.Media.Animation.Storyboard;
            var depthOut = NowPlayingPane.Resources["NowPlayingDepthOutStoryboard"]
                as Microsoft.UI.Xaml.Media.Animation.Storyboard;

            if (visible)
            {
                try
                {
                    mcRestore?.Stop();
                    depthOut?.Stop();
                    nowPlayingDepthOutSubscribed = false;
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

                NowPlayingPane.Opacity = 0;
                NowPlayingPane.Visibility = Visibility.Visible;
                DispatcherQueue.TryEnqueue(() => UpdateNowPlayingCardLayout());
                try
                {
                    mcBack?.Begin();
                    depthIn?.Begin();
                }
                catch
                {
                    NowPlayingPane.Opacity = 1;
                }
            }
            else
            {
                try
                {
                    mcBack?.Stop();
                    depthIn?.Stop();
                    if (!nowPlayingDepthOutSubscribed && depthOut != null)
                    {
                        depthOut.Completed += NowPlayingDepthOut_Completed;
                        nowPlayingDepthOutSubscribed = true;
                    }
                    mcRestore?.Begin();
                    depthOut?.Begin();
                }
                catch
                {
                    NowPlayingPane.Visibility = Visibility.Collapsed;
                    NowPlayingPane.Opacity = 1;
                }
            }
        }

        private bool nowPlayingDepthOutSubscribed;

        /// <summary>收起动画完成后折叠面板。</summary>
        private void NowPlayingDepthOut_Completed(object? sender, object e)
        {
            nowPlayingDepthOutSubscribed = false;
            if (NowPlayingPane != null)
            {
                NowPlayingPane.Visibility = Visibility.Collapsed;
                NowPlayingPane.Opacity = 1;
            }
        }

        /// <summary>状态条封面 hover：封面变暗 + 朝上三角箭头淡入动画，提示可展开。</summary>
        private void TransportCover_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (TransportCoverBorder == null)
            {
                return;
            }

            try
            {
                var arrowIn = TransportCoverBorder.Resources["TransportArrowInStoryboard"]
                    as Microsoft.UI.Xaml.Media.Animation.Storyboard;
                var arrowOut = TransportCoverBorder.Resources["TransportArrowOutStoryboard"]
                    as Microsoft.UI.Xaml.Media.Animation.Storyboard;
                arrowOut?.Stop();
                // 描边轻微高亮，提示可点击
                TransportCoverBorder.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(ResolveAccentColor());
                TransportCoverBorder.BorderThickness = new Thickness(2);
                arrowIn?.Begin();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void TransportCover_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (TransportCoverBorder == null)
            {
                return;
            }

            try
            {
                var arrowIn = TransportCoverBorder.Resources["TransportArrowInStoryboard"]
                    as Microsoft.UI.Xaml.Media.Animation.Storyboard;
                var arrowOut = TransportCoverBorder.Resources["TransportArrowOutStoryboard"]
                    as Microsoft.UI.Xaml.Media.Animation.Storyboard;
                arrowIn?.Stop();
                TransportCoverBorder.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
                TransportCoverBorder.BorderThickness = new Thickness(1);
                arrowOut?.Begin();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void NowPlayingCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            // 左上角返回按钮：收起播放歌曲信息页，恢复主内容区视图
            SetNowPlayingPaneVisible(false);
        }

        private void SeekBackButton_Click(object sender, RoutedEventArgs e)
            => SeekBySeconds(-5);

        private void SeekForwardButton_Click(object sender, RoutedEventArgs e)
            => SeekBySeconds(5);

        private void FeaturesMoreButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };

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

            var rgScan = new MenuFlyoutItem { Text = "ReplayGain 扫描…" };
            rgScan.Click += (_, _) => OpenReplayGainScan();
            flyout.Items.Add(rgScan);

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            NowPlayingText.Text = message;
        }

        /// <summary>打开 ReplayGain 扫描窗口：按范围（整库 / 当前播放列表 / 选中曲目）提供待扫描曲目。</summary>
        private void OpenReplayGainScan()
        {
            var win = new ReplayGainScanWindow(this, scope =>
            {
                if (scope == ReplayGainScanScope.Library)
                {
                    return LibraryDb.GetAllTracksForScan();
                }

                if (scope == ReplayGainScanScope.Playlist)
                {
                    return _playlist
                        .Select(p => new RgScanInput { FilePath = p.FilePath, Album = p.Album, AlbumArtist = p.AlbumArtist })
                        .ToList();
                }

                // Selection：仅对列表里多选的曲目扫描
                return GetSelectedMultiSelectSongs()
                    .Select(p => new RgScanInput { FilePath = p.FilePath, Album = p.Album, AlbumArtist = p.AlbumArtist })
                    .ToList();
            });
            win.Activate();
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

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

            // 1) 若配置了流媒体插件服务（WSL），优先取 Apple Music 真歌词
            if (!string.IsNullOrEmpty(StreamingServiceClient.ServiceBaseUrl))
            {
                try
                {
                    var sRes = await StreamingServiceClient.SearchAsync("applemusic", item.Title, 1);
                    if (sRes is { Count: > 0 })
                    {
                        StreamingServiceClient.LyricResult? ly = await StreamingServiceClient.GetLyricAsync("applemusic", sRes[0].Id);
                        if (ly is { Ok: true })
                        {
                            IReadOnlyList<LyricLine>? built = ly.Timestamped;
                            if ((built == null || built.Count == 0) && !string.IsNullOrWhiteSpace(ly.Plain))
                            {
                                built = LyricsLoader.ParseLrc(ly.Plain);
                            }

                            if (built is { Count: > 0 })
                            {
                                string lrc = LyricsToLrc(built);
                                string lrcPath = System.IO.Path.ChangeExtension(item.FilePath, ".lrc");
                                try { System.IO.File.WriteAllText(lrcPath, lrc); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
                                NowPlayingText.Text = "已从 Apple Music 获取歌词";
                                if (string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                                {
                                    BuildLyricsUi(built.ToList());
                                }
                                return;
                            }
                        }
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
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

        private async void DownloadLyricButton_Click(object sender, RoutedEventArgs e)
        {
            await DownloadLyricForCurrentAsync();
        }

        private static string LyricsToLrc(IEnumerable<LyricLine> lines)
        {
            var sb = new System.Text.StringBuilder();
            foreach (LyricLine l in lines)
            {
                TimeSpan t = l.Time < TimeSpan.Zero ? TimeSpan.Zero : l.Time;
                sb.Append('[').Append(t.ToString(@"mm\:ss\.ff")).Append(']').AppendLine(l.Text);
            }
            return sb.ToString();
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
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

            var items = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                foreach (string path in TrackStatsStore.GetAllFavorites())
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
                        catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
                    }
                }
            }
            else
            {
                // 最近播放 = 播放历史事件流水（每次播放一条记录，含播放时间/时长/是否播完）。
                // 与 LibraryDb 记录点（切歌/播完）对应；双击等交互复用歌曲行通用逻辑。
                foreach (LibraryDb.PlaybackHistoryEntry e in LibraryDb.LoadPlaybackHistory(200))
                {
                    PlaylistItem? item = null;
                    if (System.IO.File.Exists(e.FilePath))
                    {
                        PlaylistItem? fromLib = FindLibraryItemByPath(e.FilePath);
                        if (fromLib != null)
                        {
                            item = ClonePlaylistItem(fromLib);
                        }
                        else
                        {
                            try { item = CreatePlaylistItemFromPath(e.FilePath); }
                            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
                        }
                    }

                    item ??= new PlaylistItem { FilePath = e.FilePath, Title = e.Title };
                    if (!string.IsNullOrWhiteSpace(e.Title)
                        && string.Equals(item.Title, System.IO.Path.GetFileNameWithoutExtension(e.FilePath), StringComparison.OrdinalIgnoreCase))
                    {
                        item.Title = e.Title; // 无内嵌标题时用历史记录的标题
                    }

                    // 第二行显示播放信息："播放于 MM-dd HH:mm - 播放 m:ss · 播完/未播完"
                    DateTime local = e.PlayedAtUtc == DateTime.MinValue
                        ? DateTime.MinValue
                        : e.PlayedAtUtc.ToLocalTime();
                    string timeText = local == DateTime.MinValue ? "—" : local.ToString("MM-dd HH:mm");
                    string durText = e.PlayedSeconds < 1
                        ? "—"
                        : e.PlayedSeconds < 60
                            ? (int)e.PlayedSeconds + " 秒"
                            : TimeSpan.FromSeconds(e.PlayedSeconds).ToString(@"m\:ss");
                    item.Artist = "播放于 " + timeText;
                    item.Album = "播放 " + durText + " · " + (e.Completed ? "播完" : "未播完");
                    items.Add(item);
                }
            }

            RenumberCollection(items);
            PlaylistView.ItemsSource = items;
            LibraryPaneTitle.Text = string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal)
                ? "我喜欢的音乐"
                : "最近播放";
            SetPlaylistEmptyHint(items.Count == 0,
                string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal) ? "暂时没有添加我喜欢的音乐" : "最近没有播放记录");
        }

        /// <summary>歌曲列表空状态提示：空时显示 hint，否则收起。</summary>
        private void SetPlaylistEmptyHint(bool empty, string hint)
        {
            if (PlaylistEmptyHint == null) return;
            PlaylistEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            PlaylistEmptyHint.Text = hint;
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

        // ---------------- 评分分类（未评分 + 1..5 星） ----------------

        /// <summary>右上角刷新当前页面：按当前分类重载对应数据。</summary>
        private bool _windowMaximized;

        /// <summary>设置窗口四角风格（无边框自绘按钮窗口用；ROUND=圆角、DONOTROUND=全屏填满直角）。</summary>
        private void ApplyWindowCorners(bool rounded)
        {
            try
            {
                if (_mainWindowHwnd == IntPtr.Zero) return;
                int corner = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                DwmSetWindowAttributeInt(_mainWindowHwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>无边框 + 自绘按钮的窗口 chrome：保留系统 resize 边框（四边/四角可调大小、四角圆角、最大化到工作区留任务栏），隐藏系统标题栏按钮（caption）。</summary>
        private void MakeWindowBorderless()
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter p)
                {
                    // hasBorder 保留 resize 边框（原生四边调大小 + DWM 圆角 + 最大化到工作区）；
                    // hasTitleBar=false 去掉系统标题栏/最小化/最大化/关闭按钮，由自绘按钮接管。
                    p.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void WindowMinButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void WindowMaxRestoreButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter p)
                {
                    // 最大化状态与图标由 WM_SIZE（OnWindowMaximizeStateChanged）统一同步，
                    // 这样从最大化拖拽还原后按钮也能正确变回“最大化”。
                    if (_windowMaximized) p.Restore();
                    else p.Maximize();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private async void WindowCloseButton_Click(object sender, RoutedEventArgs e)
        {
            // 自绘关闭按钮不能直接 Close()：WinUI 3 的 Window.Close() 不触发 AppWindow.Closing，
            // 会绕过关闭策略（最小化到托盘/每次询问）导致直接退出。统一走 HandleCloseRequestAsync。
            try { await HandleCloseRequestAsync(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void UpdateMaxRestoreIcon()
        {
            try
            {
                if (WindowMaxRestoreIcon != null)
                {
                    WindowMaxRestoreIcon.Glyph = _windowMaximized ? "\uE923" : "\uE922";
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>由 WndProc(W M_SIZE) 同步最大化/还原状态：自绘最大化按钮图标始终反映真实窗口状态（含拖拽还原）。</summary>
        internal void OnWindowMaximizeStateChanged(bool maximized)
        {
            _windowMaximized = maximized;
            UpdateMaxRestoreIcon();
        }

        // ---------------- 音频设置右侧面板（输出模式 / 链路状态 / 专业播放状态） ----------------
        private bool _audioCombosLoading;

        private async void AudioSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // 半透明亚克力 + 高斯模糊背景（AcrylicBrush），覆盖 XAML 的纯色底
            AudioSettingsPanel.Background = FrostedGlass.CreatePanelBrush();
            AudioSettingsOverlayHost.Visibility = Visibility.Visible;
            AudioSettingsOverlayHost.IsHitTestVisible = true;
            AudioSettingsPanelTransform.TranslateX = 360;
            AudioSettingsOpenStoryboard?.Begin();
            await FillAudioSettingsCombosAsync();
            RefreshAudioSettingsPanel();
        }

        private void AudioSettingsCloseButton_Click(object sender, RoutedEventArgs e) => HideAudioSettingsPanel();

        private void AudioSettingsScrim_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => HideAudioSettingsPanel();

        private void HideAudioSettingsPanel()
        {
            if (AudioSettingsCloseStoryboard != null && AudioSettingsOverlayHost.Visibility == Visibility.Visible)
            {
                AudioSettingsCloseStoryboard.Completed += (_, _) =>
                {
                    AudioSettingsOverlayHost.Visibility = Visibility.Collapsed;
                    AudioSettingsOverlayHost.IsHitTestVisible = false;
                };
                AudioSettingsCloseStoryboard.Begin();
            }
            else
            {
                AudioSettingsOverlayHost.Visibility = Visibility.Collapsed;
                AudioSettingsOverlayHost.IsHitTestVisible = false;
            }
        }

        private async System.Threading.Tasks.Task FillAudioSettingsCombosAsync()
        {
            // 全量填充（模式 + 设备），期间置位防止 SelectionChanged 回写中间态
            _audioCombosLoading = true;
            try
            {
                var s = AppSettingsStore.Load();
                string mode = string.IsNullOrWhiteSpace(s.OutputMode) ? "Shared" : s.OutputMode;

                UpdateModeCardHighlight(mode);

                await FillAudioSettingsDevicesAsync();
            }
            finally
            {
                _audioCombosLoading = false;
            }
        }

        private async System.Threading.Tasks.Task FillAudioSettingsDevicesAsync()
        {
            try
            {
                if (AudioOutputDeviceCombo == null)
                {
                    return;
                }

                var s = AppSettingsStore.Load();
                string mode = string.IsNullOrWhiteSpace(s.OutputMode) ? "Shared" : s.OutputMode;
                string selectedId = s.OutputDeviceId ?? string.Empty;
                AudioOutputDeviceCombo.Items.Clear();

                if (string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase))
                {
                    var drivers = HiFiOutputBackend.EnumerateAsioDrivers();
                    if (drivers.Count == 0)
                    {
                        AudioOutputDeviceCombo.Items.Add(new ComboBoxItem { Content = "（未检测到 ASIO 驱动）", Tag = "" });
                    }
                    else
                    {
                        foreach (string d in drivers)
                        {
                            AudioOutputDeviceCombo.Items.Add(new ComboBoxItem { Content = d, Tag = d });
                        }
                    }
                }
                else
                {
                    var devices = HiFiOutputBackend.EnumerateWasapiDevices();
                    string defaultId = HiFiOutputBackend.GetDefaultWasapiDeviceId();
                    AudioOutputDeviceCombo.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "" });
                    foreach ((string id, string name) in devices)
                    {
                        string label = string.Equals(id, defaultId, System.StringComparison.OrdinalIgnoreCase) ? name + " (默认)" : name;
                        AudioOutputDeviceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
                    }
                }

                // 选中已保存设备
                foreach (var o in AudioOutputDeviceCombo.Items)
                {
                    if (o is ComboBoxItem it && it.Tag is string t && string.Equals(t, selectedId ?? "", System.StringComparison.OrdinalIgnoreCase))
                    {
                        AudioOutputDeviceCombo.SelectedItem = it;
                        break;
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private async void AudioModeCard_Click(object sender, RoutedEventArgs e)
        {
            if (_audioCombosLoading)
            {
                return;
            }

            if (sender is not Button b || b.Tag is not string mode)
            {
                return;
            }

            AppSettingsStore.Update(s => s.OutputMode = mode);
            ApplyEngineOutputMode(AppSettingsStore.Load());
            UpdateModeCardHighlight(mode);
            _audioCombosLoading = true;
            try
            {
                await FillAudioSettingsDevicesAsync();
            }
            finally
            {
                _audioCombosLoading = false;
            }

            RefreshAudioSettingsPanel();
        }

        /// <summary>输出模式三张可视化卡片：当前选中的卡片用主题色高亮，其余还原为常态。</summary>
        private void UpdateModeCardHighlight(string mode)
        {
            Windows.UI.Color accent = ResolveAccentColor();
            var activeBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent);
            var activeBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(46, accent.R, accent.G, accent.B));
            var normalBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(60, 120, 120, 120));

            SetModeCard(ModeSharedCard, string.Equals(mode, "Shared", StringComparison.OrdinalIgnoreCase), activeBorder, activeBg, normalBorder);
            SetModeCard(ModeExclusiveCard, string.Equals(mode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase), activeBorder, activeBg, normalBorder);
            SetModeCard(ModeAsioCard, string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase), activeBorder, activeBg, normalBorder);
        }

        private static void SetModeCard(Button? card, bool active, Microsoft.UI.Xaml.Media.Brush activeBorder, Microsoft.UI.Xaml.Media.Brush activeBg, Microsoft.UI.Xaml.Media.Brush normalBorder)
        {
            if (card == null)
            {
                return;
            }

            card.BorderBrush = active ? activeBorder : normalBorder;
            card.BorderThickness = new Microsoft.UI.Xaml.Thickness(active ? 2 : 1);
            card.Background = active ? activeBg : null;
        }

        private async void AudioOutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_audioCombosLoading)
            {
                return;
            }

            if (AudioOutputDeviceCombo.SelectedItem is ComboBoxItem it && it.Tag is string did)
            {
                // 用户手动改选 → 放弃「等设备插回自动切回」的暂存偏好
                ClearPreferredOutputDevice();
                AppSettingsStore.Update(s => s.OutputDeviceId = did);
                await ApplyOutputDeviceAsync(did);
            }

            RefreshAudioSettingsPanel();
        }

        private void RefreshAudioSettingsPanel()
        {
            try
            {
                if (AudioLinkSourceFmt == null)
                {
                    return;
                }

                bool hifi = IsHiFiModeSelected();
                string? src = _audioEngine?.SourceFormatDescription;
                string? outp = _audioEngine?.ActualOutputFormat;

                AudioLinkSourceFmt.Text = string.IsNullOrWhiteSpace(src)
                    ? (hifi ? "（解析中…）" : "MediaPlayer（系统解码）")
                    : src;
                AudioLinkOutputFmt.Text = string.IsNullOrWhiteSpace(outp)
                    ? (hifi ? "（解析中…）" : "系统混音器（Shared）")
                    : outp;
                AudioLinkMode.Text = hifi ? "独占（WASAPI 独占 / ASIO）" : "共享（系统混音）";

                // DSP 摘要
                bool eqOn = EqCurveStore.Load().HasEffect();
                var extra = DspExtraStore.Load();
                bool chOn = extra.ChannelBalance?.IsActive == true;
                bool limiterOn = extra.Safety?.EnableLimiter != false;
                bool rgOn = ReplayGainStore.Load().Mode != ReplayGainMode.Off;
                var active = new System.Collections.Generic.List<string>();
                if (eqOn) active.Add("EQ");
                if (chOn) active.Add("声道");
                if (limiterOn) active.Add("限幅");
                if (rgOn) active.Add("ReplayGain");
                string dsp = active.Count == 0 ? "全部旁路" : string.Join(" / ", active) + "（开）";
                AudioLinkDsp.Text = dsp + (hifi ? " [HiFi 直通链路]" : " [共享链路]");
                AudioLinkBitPerfect.Text = active.Count == 0
                    ? "bit-perfect 直通（需结合输出格式确认，无重采样/音量干预）"
                    : "非 bit-perfect（参与处理方：" + string.Join("、", active) + "）";

                // 专业播放状态
                AudioProPosition.Text =
                    (EnginePositionValue >= TimeSpan.Zero ? EnginePositionValue.ToString(@"mm\:ss") : "--")
                    + " / " + (EngineDurationValue > TimeSpan.Zero ? EngineDurationValue.ToString(@"h\:mm\:ss") : "--");
                AudioProBuffer.Text = string.IsNullOrWhiteSpace(_audioEngine?.OutputDeviceId)
                    ? "系统默认"
                    : _audioEngine.OutputDeviceId;
                AudioProDspChain.Text = "EQ" + (eqOn ? "✓" : "—") + " · 声道" + (chOn ? "✓" : "—") + " · 限幅" + (limiterOn ? "✓" : "—") + " · ReplayGain" + (rgOn ? "✓" : "—");
                // 链路可视化着色 + bit-perfect 徽章
                ApplyLinkVisual(pure: active.Count == 0, activeText: string.Join("、", active));
                // SRC 会话实际状态（源→目标 / 未升频原因）
                RefreshSrcSessionState();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void ApplyLinkVisual(bool pure, string activeText)
        {
            try
            {
                if (LinkDspCapsule == null)
                {
                    return;
                }

                var green = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 160, 67));
                var amber = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 214, 148, 45));
                var greenBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 46, 160, 67));
                var amberBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 214, 148, 45));

                LinkDspCapsule.BorderBrush = pure ? green : amber;
                LinkDspCapsule.BorderThickness = new Microsoft.UI.Xaml.Thickness(2);

                BitPerfectBadge.Background = pure
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 122, 52))
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 178, 116, 28));
                AudioLinkBitPerfectBadgeText.Text = pure ? "✓ bit-perfect · 直通" : "DSP 处理中";
                AudioLinkBitPerfectBadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void RefreshCurrentPageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                switch (_currentCategory)
                {
                    case "Albums": _ = RefreshAlbumViewAsync(); break;
                    case "Artists":
                    case "AlbumArtists": _ = RefreshArtistViewAsync(); break;
                    case "Folders": RefreshFolderBrowserRoots(); break;
                    case "Favorites":
                    case "Recent": ApplyFavoritesOrRecentCategory(); break;
                    case "Ratings": ApplyRatingCategory(); break;
                    case "MostPlayed": ApplyMostPlayedCategory(); break;
                    case "Genres":
                    case "Years": _ = RefreshGenreYearViewAsync(); break;
                    case "PlaylistWall": ApplyPlaylistWallCategory(); break;
                    default: ApplyCategoryView(); break;
                }

                NowPlayingText.Text = "已刷新当前页面";
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>刷新评分分类：按 _ratingFilter 过滤媒体库歌曲并填充列表。</summary>
        private void ApplyRatingCategory()
        {
            UpdateRatingFilterHighlight();

            var col = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (PlaylistItem track in _playlist)
            {
                if (_ratingFilter < 0)
                {
                    // 未点选：显示所有已评分歌曲
                    if (track.Rating > 0) col.Add(ClonePlaylistItem(track));
                }
                else if (track.Rating == _ratingFilter)
                {
                    col.Add(ClonePlaylistItem(track));
                }
            }

            RenumberCollection(col);
            PlaylistView.ItemsSource = col;
            LibraryPaneTitle.Text = "评分";
            SetPlaylistEmptyHint(col.Count == 0, _ratingFilter >= 0
                ? (_ratingFilter == 0 ? "没有未评分的歌曲" : "没有 " + _ratingFilter + " 星评分的歌曲")
                : "还没有给任何歌曲评分");
        }

        private void RatingFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string s && int.TryParse(s, out int rating))
            {
                _ratingFilter = rating;
                ApplyRatingCategory();
            }
        }

        private void UpdateRatingFilterHighlight()
        {
            var accent = ResolveAccentBrush();
            var fg = ColorHelper.ResolveContrastingForeground(accent);
            var idleBg = ResolveCapsuleFillBrush();
            var border = ResolveNavCapsuleBorderBrush();
            (Button Btn, int Val)[] maps =
            {
                (RatingFilter0Button, 0), (RatingFilter1Button, 1), (RatingFilter2Button, 2),
                (RatingFilter3Button, 3), (RatingFilter4Button, 4), (RatingFilter5Button, 5)
            };
            foreach (var (btn, val) in maps)
            {
                bool active = val == _ratingFilter;
                if (active) { btn.Background = accent; btn.Foreground = fg; btn.BorderThickness = new Thickness(0); }
                else { btn.Background = idleBg; btn.ClearValue(Control.ForegroundProperty); btn.BorderThickness = new Thickness(1); btn.BorderBrush = border; }
            }
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
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            }

            if (_playlist.Count != before)
            {
                RenumberCollection(_playlist);
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath));
            }
        }

        #region 输出设备热插拔（USB DAC / 蓝牙耳机插拔自动切换）

        /// <summary>设备被拔掉时暂存的偏好 ID；插回后自动切回。用户手动改选时清空。</summary>
        private string? _preferredOutputDeviceId;

        private bool _deviceHotplugBusy;

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hotplugNoticeTimer;

        /// <summary>启动设备监听（窗口初始化时调用一次）。</summary>
        private void StartAudioDeviceWatcher()
        {
            try
            {
                AudioDeviceWatcher.DevicesChanged += OnAudioDevicesChanged;
                AudioDeviceWatcher.Start();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>设备变化回调。注意：这里跑在非 UI 线程，任何 XAML 访问都必须切回 UI 线程，
        /// 否则会触发 RPC_E_WRONG_THREAD。</summary>
        private void OnAudioDevicesChanged(object? sender, AudioDeviceChangeEventArgs e)
        {
            try
            {
                DispatcherQueue.TryEnqueue(() => _ = HandleAudioDevicesChangedAsync(e));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private async System.Threading.Tasks.Task HandleAudioDevicesChangedAsync(AudioDeviceChangeEventArgs e)
        {
            if (_deviceHotplugBusy)
            {
                return;
            }

            _deviceHotplugBusy = true;
            try
            {
                string savedId = AppSettingsStore.Load().OutputDeviceId ?? string.Empty;

                // 当前选中的设备掉了 → 回退系统默认，并记住它，插回来时自动切回
                bool vanished = savedId.Length > 0 && !e.CurrentIds.Contains(savedId);

                // 之前拔掉的设备又回来了 → 自动切回
                bool returned = !string.IsNullOrEmpty(_preferredOutputDeviceId)
                    && e.Added.Any(id => string.Equals(id, _preferredOutputDeviceId, StringComparison.OrdinalIgnoreCase));

                // 刷新下拉列表（无论哪种变化都要刷新，保证面板里看到的都是当前实际在线的设备）
                _audioCombosLoading = true;
                try
                {
                    await FillAudioSettingsDevicesAsync();
                }
                finally
                {
                    _audioCombosLoading = false;
                }

                if (vanished)
                {
                    _preferredOutputDeviceId = savedId;
                    AppSettingsStore.Update(s => s.OutputDeviceId = string.Empty);
                    await ApplyOutputDeviceAsync(string.Empty);
                    string name = AudioDeviceService.GetDeviceName(savedId) ?? savedId;
                    global::CelesteMusicPlayer.StartupLog.Write("[设备监听] 当前输出设备已移除，回退默认：" + name);
                    ShowHotplugNotice("输出设备「" + name + "」已断开，已切回系统默认。重新插上后会自动切回。");
                }
                else if (returned)
                {
                    string back = _preferredOutputDeviceId!;
                    _preferredOutputDeviceId = null;
                    AppSettingsStore.Update(s => s.OutputDeviceId = back);
                    await ApplyOutputDeviceAsync(back);
                    string name = AudioDeviceService.GetDeviceName(back) ?? back;
                    global::CelesteMusicPlayer.StartupLog.Write("[设备监听] 偏好设备已重新上线，自动切回：" + name);
                    ShowHotplugNotice("检测到「" + name + "」，已自动切回该设备。");
                }

                RefreshAudioSettingsPanel();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            finally
            {
                _deviceHotplugBusy = false;
            }
        }

        /// <summary>显示一条热插拔提示，10 秒后自动隐藏。</summary>
        private void ShowHotplugNotice(string message)
        {
            try
            {
                if (AudioDeviceHotplugText == null)
                {
                    return;
                }

                AudioDeviceHotplugText.Text = message;
                AudioDeviceHotplugText.Visibility = Visibility.Visible;

                _hotplugNoticeTimer ??= DispatcherQueue.CreateTimer();
                _hotplugNoticeTimer.Interval = TimeSpan.FromSeconds(10);
                _hotplugNoticeTimer.Tick -= HotplugNoticeTimer_Tick;
                _hotplugNoticeTimer.Tick += HotplugNoticeTimer_Tick;
                _hotplugNoticeTimer.Stop();
                _hotplugNoticeTimer.Start();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void HotplugNoticeTimer_Tick(object? sender, object e)
        {
            try
            {
                _hotplugNoticeTimer?.Stop();
                if (AudioDeviceHotplugText != null)
                {
                    AudioDeviceHotplugText.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>用户手动改选设备时，放弃「等设备插回自动切回」的暂存偏好。</summary>
        private void ClearPreferredOutputDevice()
        {
            _preferredOutputDeviceId = null;
        }

        #endregion
    }
}
