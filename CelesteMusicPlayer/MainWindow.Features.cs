using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
            AppSettingsState settings = AppSettingsStore.Load();
            bool enable = settings.EnableSmtc;

            // SMTC 统一走独立宿主 MediaPlayer（挂静音循环源激活播放会话），
            // 与 ConfigureEngineSmtc 使用同一个宿主，保证按钮开关和元数据指向同一 SMTC 实例。
            MediaPlayer? host = EnsureSmtcSilentSource();
            if (host == null)
            {
                return;
            }

            SystemMediaTransportControls smtc = host.SystemMediaTransportControls;
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
            // 「查找歌曲」已移除（老版本遗留），F / F3 不再绑定任何动作。
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
                // 按当前可视化档位重绘（波形 / 频谱柱 / 示波器 / 径向），
                // 不能写死 DrawWaveformBars —— 那样切到频谱柱后一换主题色就会被画回波形。
                DrawVisualSlot();
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
                ApplyNowPlayingTitleColor();
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
            SetNavEntryVisibility(NavFavoritesButton, settings.ShowNavFavorites);
            SetNavEntryVisibility(NavRecentButton, settings.ShowNavRecent);
            SetNavEntryVisibility(NavGenreButton, settings.ShowNavGenre);
            SetNavEntryVisibility(NavYearButton, settings.ShowNavYear);

            // 网络音乐库：配了地址才显示（名字取显示名，没填就用主机名）
            ApplyWebDavNavEntry(settings);
        }

        /// <summary>
        /// 显示 / 隐藏一个左侧导航条目。
        /// ⚠️ 必须连外层那个 Grid 一起隐藏，不能只隐藏 Button：
        /// 每个条目是「Grid 包 [Button + 指示条 Border]」，指示条是常驻元素（Height=16、Opacity=0、Visibility=Visible），
        /// 只把 Button 设成 Collapsed 的话 Grid 还留着 16px 高 ——
        /// 用户就看到了「播放队列」和「播放最多」之间凭空多出两格空白（隐藏的流派 + 年份），还把左侧顶出滚动条。
        /// </summary>
        private static void SetNavEntryVisibility(FrameworkElement? button, bool visible)
        {
            if (button == null)
            {
                return;
            }

            Visibility v = visible ? Visibility.Visible : Visibility.Collapsed;
            button.Visibility = v;
            if (button.Parent is FrameworkElement entry)
            {
                entry.Visibility = v;
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
                else if (child is Grid lyricRow)
                {
                    // 每行是 Grid → Border（圆角框）→ 歌词 TextBlock：对齐要下钻两层
                    foreach (var inner in lyricRow.Children)
                    {
                        if (inner is Border frame && frame.Child is TextBlock rowText)
                        {
                            rowText.TextAlignment = align;
                        }
                    }
                }
            }
        }

        private void ApplySpectrumVisibilityFromSettings(AppSettingsState settings)
        {
            // 「显示频谱」关掉时把可视化整体藏起来；打开时交给 ApplyNowPlayingVisual
            // 按当前模式（波形/频谱柱/径向/示波器）决定到底显示哪一块画布，
            // 避免这里只认 WaveformCanvas、把径向模式也给盖掉。
            if (!settings.ShowSpectrum)
            {
                if (WaveformCanvas != null)
                {
                    WaveformCanvas.Visibility = Visibility.Collapsed;
                }

                if (RadialVisualCanvas != null)
                {
                    RadialVisualCanvas.Visibility = Visibility.Collapsed;
                }

                return;
            }

            ApplyNowPlayingVisual();
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
            // HiFi 独占/ASIO 且「HiFi 软件音量」未开启：滑块恒 100%，热键/滚轮不挪位，
            // 避免出现"显示 95% 实际没变"的假状态（此时调音量请用 DAC 旋钮 / 系统音量端）。
            if (IsHiFiModeSelected() && !IsHiFiSoftVolumeUiUnlocked())
            {
                return;
            }

            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
        }

        internal void SeekBySeconds(double seconds)
        {
            // 引擎播放（FFmpeg / HiFi 独占路径）：以前这里只认 MediaPlayer，
            // 而现在所有播放都走引擎 → 快进/快退（键盘、迷你播放器、托盘）其实是完全失效的。
            if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
            {
                try
                {
                    double duration = _audioEngine.Duration.TotalSeconds;
                    double next = _audioEngine.Position.TotalSeconds + seconds;
                    next = Math.Clamp(next, 0, duration > 0 ? duration : next);
                    _audioEngine.Seek(TimeSpan.FromSeconds(next));

                    // seek 会丢弃无缝源里已预加载的下一首，重挂一次
                    if (_userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count)
                    {
                        _ = PreloadSeamlessNextAsync(_userPlaylist[_userPlaylistIndex]);
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player?.Source == null)
            {
                // 启动就绪态：还没播，快进/快退就当作"从这个位置开始播"
                StartPlaybackFromPendingPosition(ProgressSlider.Value + seconds, pauseAfter: false);
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
                // 播放页是盖在整个主内容区上的一层透明板：左侧音乐库面板必须让位，
                // 否则会在播放页底下透出来（用户反馈过）。
                SetLibraryNavHiddenForNowPlaying(true);
                StartupLog.Write($"[深度] 展开播放页：mcBack={(mcBack != null)} depthIn={(depthIn != null)} " +
                                 $"导航面板={LeftCategoryGrid?.Visibility}");
                DispatcherQueue.TryEnqueue(() => UpdateNowPlayingCardLayout());
                // 进入播放页时按当前设置套用布局（经典/水面），并重算倒影尺寸
                ApplyNowPlayingLayout();
                UpdateNowPlayingReflectionGeometry();
                // 沉浸式：隐藏底部常驻状态条，改由页内悬浮控制条承担播放控制
                ShowFloatingTransport();
                try
                {
                    mcBack?.Begin();
                    depthIn?.Begin();
                }
                catch (Exception caught)
                {
                    // 以前这里是空 catch：景深动画一旦起不来（比如 TargetName 解析不到），
                    // 表现就是"播放页底下的左侧分类/分隔线全透出来"，而日志里一个字都没有。
                    global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs.SetNowPlayingPaneVisible(展开)", caught);
                    NowPlayingPane.Opacity = 1;
                }
            }
            else
            {
                // 收起播放页：先把左侧面板放回来，好让"景深恢复"动画把它淡入
                SetLibraryNavHiddenForNowPlaying(false);
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
                    // 退出播放页：恢复底部常驻状态条，收起页内悬浮控制条
                    HideFloatingTransport();
                }
                catch
                {
                    // 动画起不来时手动把状态摆正：播放页收起 + 主内容区不透明（否则会剩下一片"退后"的暗界面）
                    try
                    {
                        mcRestore?.Stop();
                        depthOut?.Stop();
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

                    NowPlayingPane.Visibility = Visibility.Collapsed;
                    NowPlayingPane.Opacity = 1;
                    if (LeftCategoryGrid != null)
                    {
                        LeftCategoryGrid.Opacity = 1;
                    }

                    if (LibraryPaneRoot != null)
                    {
                        LibraryPaneRoot.Opacity = 1;
                    }
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

        // ---- 页内悬浮控制条（进入播放页显示，鼠标静止自动淡出） ----
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _floatingIdleTimer;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _floatingFadeTimer;
        private double _floatingFadeFrom;
        private double _floatingFadeTo;
        private int _floatingFadeStep;
        private bool _floatingShownTarget;
        private bool _floatingMirrorSubscribed;
        private bool _floatingPaneWired;
        private bool _floatingMirror;
        private Visibility _bottomBarVisibilityBefore = Visibility.Visible;

        /// <summary>进入播放页：隐藏底部常驻状态条，显示页内悬浮控制条并同步一次当前播放状态，随后开始闲置淡出计时。</summary>
        private void ShowFloatingTransport()
        {
            try
            {
                // 隐藏底部常驻状态条（记录原状态以便退出时恢复）
                if (BottomTransportBar != null)
                {
                    _bottomBarVisibilityBefore = BottomTransportBar.Visibility;
                    BottomTransportBar.Visibility = Visibility.Collapsed;
                }

                if (NowPlayingFloatingBar == null)
                {
                    return;
                }

                // 订阅真实进度条的变化，把位置/时间镜像到悬浮条（仅一次）
                if (!_floatingMirrorSubscribed && ProgressSlider != null)
                {
                    ProgressSlider.ValueChanged += ProgressSliderMirror_ValueChanged;
                    _floatingMirrorSubscribed = true;
                }
                // 鼠标在播放页内移动时唤醒悬浮条（仅挂一次）
                if (!_floatingPaneWired && NowPlayingPaneContent != null)
                {
                    NowPlayingPaneContent.PointerMoved += NowPlayingPaneContent_PointerMoved;
                    _floatingPaneWired = true;
                }

                SyncFloatingTransport();
                ApplyFloatingBarStyle();
                SetFloatingBarVisible(true, false);

                // 进度条拖动/悬停时的浮动提示默认显示"原始秒数"（如 123.45），换成 分:秒。
                // 用代码赋值而不是 XAML 资源：避免动 XAML 触发本项目偶发的 XamlCompiler 崩溃。
                // ⚠️ 转换器类名是 SecondsToTimeSpanConverter（定义在 MainWindow.xaml.cs），别写错。
                var timeTip = new SecondsToTimeSpanConverter();
                if (NowPlayingProgressSlider != null && NowPlayingProgressSlider.ThumbToolTipValueConverter == null)
                {
                    NowPlayingProgressSlider.ThumbToolTipValueConverter = timeTip;
                }
                // 底部常驻进度条同样换成 分:秒（否则两处提示口径不一致）
                if (ProgressSlider != null && ProgressSlider.ThumbToolTipValueConverter == null)
                {
                    ProgressSlider.ThumbToolTipValueConverter = timeTip;
                }

                // 两种布局都改成"内嵌常显"：不再启动闲置淡出计时（用户要求经典也内嵌）。
                _floatingIdleTimer?.Stop();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ShowFloatingTransport", caught);
            }
        }

        /// <summary>按布局调整播放条样式：水面=内嵌透明（贴在波形下方）；经典=亚克力药丸，内嵌在面板底部。
        /// 两种布局都是"内嵌常显"，不再做鼠标闲置淡出（用户要求经典也内嵌）。</summary>
        private void ApplyFloatingBarStyle()
        {
            if (NowPlayingFloatingBar == null)
            {
                return;
            }

            // 两种布局统一「内嵌」：不要亚克力卡片底、不要描边、不要圆角 ——
            // 带背景+描边的药丸看起来就是「浮在页面上的悬浮条」，用户要的是融进播放页底部。
            // 宽度/边距由 UpdateNowPlayingCardLayout 设置（横跨面板、随宽度变、不被 680 封顶）。
            NowPlayingFloatingBar.Background = new SolidColorBrush(Colors.Transparent);
            NowPlayingFloatingBar.BorderBrush = new SolidColorBrush(Colors.Transparent);
            NowPlayingFloatingBar.BorderThickness = new Thickness(0);
            NowPlayingFloatingBar.CornerRadius = new CornerRadius(0);
            NowPlayingFloatingBar.Padding = new Thickness(0, 0, 0, 10);
        }

        /// <summary>退出播放页：恢复底部常驻状态条，收起悬浮控制条并停止闲置计时。</summary>
        private void HideFloatingTransport()
        {
            try
            {
                _floatingIdleTimer?.Stop();
                if (BottomTransportBar != null)
                {
                    BottomTransportBar.Visibility = _bottomBarVisibilityBefore;
                }
                if (NowPlayingFloatingBar != null)
                {
                    NowPlayingFloatingBar.Visibility = Visibility.Collapsed;
                    NowPlayingFloatingBar.Opacity = 1;
                    NowPlayingFloatingBar.IsHitTestVisible = false;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.HideFloatingTransport", caught);
            }
        }

        /// <summary>把真实进度条的位置 / 时间 / 播放暂停图标镜像到悬浮控制条。</summary>
        private void SyncFloatingTransport()
        {
            if (NowPlayingProgressSlider == null)
            {
                return;
            }
            _floatingMirror = true;
            try
            {
                NowPlayingProgressSlider.Maximum = ProgressSlider?.Maximum ?? 100;
                NowPlayingProgressSlider.Value = ProgressSlider?.Value ?? 0;
                if (FloatingCurrentTime != null) FloatingCurrentTime.Text = CurrentTimeText?.Text ?? "00:00";
                if (FloatingTotalTime != null) FloatingTotalTime.Text = TotalTimeText?.Text ?? "00:00";
                if (FloatingPlayPauseIcon != null) FloatingPlayPauseIcon.Glyph = PlayPauseIcon?.Glyph ?? "\uE768";
            }
            finally
            {
                _floatingMirror = false;
            }
        }

        /// <summary>真实进度条变化时镜像到悬浮条（镜像期间忽略悬浮条自身的 ValueChanged，避免回环）。</summary>
        private void ProgressSliderMirror_ValueChanged(object? sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (NowPlayingProgressSlider == null)
            {
                return;
            }

            // 用户正在拖页内进度条：此时不要用"播放位置"回写滑块，
            // 否则拖到一半会被播放进度拽回去（不跟手 / 松手跳回原处）。
            if (_isUserSeeking)
            {
                return;
            }

            _floatingMirror = true;
            try
            {
                NowPlayingProgressSlider.Maximum = ProgressSlider?.Maximum ?? 100;
                NowPlayingProgressSlider.Value = ProgressSlider?.Value ?? 0;
                if (FloatingCurrentTime != null) FloatingCurrentTime.Text = CurrentTimeText?.Text ?? "00:00";
                if (FloatingTotalTime != null) FloatingTotalTime.Text = TotalTimeText?.Text ?? "00:00";
            }
            finally
            {
                _floatingMirror = false;
            }
        }

        // 悬浮进度条交互：复用真实进度条的拖拽 / 跳转逻辑
        private void NowPlayingProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isUserSeeking = true;
            _seekGestureConsumed = false;
        }

        private void NowPlayingProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
            => EndNowPlayingSeekGesture();

        private void NowPlayingProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
            => EndNowPlayingSeekGesture();

        /// <summary>
        /// 页内进度条：一次拖动只跳转一次（PointerReleased + PointerCaptureLost 都会触发，
        /// 重复 seek 会卡顿并重复触发下一首无缝预加载 —— 就是"顿一下响两声"的来源）。
        /// </summary>
        private void EndNowPlayingSeekGesture()
        {
            if (!_isUserSeeking || _seekGestureConsumed)
            {
                _isUserSeeking = false;
                return;
            }

            _seekGestureConsumed = true;
            try
            {
                if (NowPlayingProgressSlider != null && ProgressSlider != null)
                {
                    double target = NowPlayingProgressSlider.Value;
                    if (Math.Abs(ProgressSlider.Value - target) >= 0.01)
                    {
                        ProgressSlider.Value = target;
                    }
                }

                SeekToSliderValue();
            }
            finally
            {
                _isUserSeeking = false;
            }
        }

        private void NowPlayingProgressSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_floatingMirror)
            {
                return;
            }

            if (!_isUserSeeking)
            {
                return;
            }

            double seconds = Math.Clamp(e.NewValue, 0, NowPlayingProgressSlider?.Maximum ?? e.NewValue);

            // 拖动中：时间文字跟着走（手感），同时把值同步回真实进度条，
            // 让松手时的 SeekToSliderValue() 用的是用户拖到的位置。
            if (FloatingCurrentTime != null)
            {
                FloatingCurrentTime.Text = FormatTime(TimeSpan.FromSeconds(seconds));
            }

            if (ProgressSlider != null && Math.Abs(ProgressSlider.Value - seconds) >= 0.01)
            {
                double keep = ProgressSlider.Value;
                ProgressSlider.Value = seconds;
                // 若真实进度条拒绝该值（Maximum 还没同步等极端情况），回退，避免两处长期不一致
                if (Math.Abs(ProgressSlider.Value - seconds) >= 0.01)
                {
                    ProgressSlider.Value = keep;
                }
            }
        }

        private void NowPlayingPaneContent_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_nowPlayingPaneOpen)
            {
                ResetFloatingIdle();
            }
        }

        private void NowPlayingFloatingBar_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_nowPlayingPaneOpen)
            {
                ResetFloatingIdle();
            }
        }

        private void EnsureFloatingIdleTimer()
        {
            if (_floatingIdleTimer == null)
            {
                _floatingIdleTimer = DispatcherQueue.CreateTimer();
                _floatingIdleTimer.Interval = TimeSpan.FromMilliseconds(2600);
                _floatingIdleTimer.Tick += (_, _) => SetFloatingBarVisible(false, true);
            }
        }

        private void ResetFloatingIdle()
        {
            if (NowPlayingFloatingBar == null)
            {
                return;
            }
            // 播放条已改成"内嵌常显"，两种布局都不再参与闲置淡出；
            // 这里只需保证它处于可见状态（不做淡入动画、不重启计时）。
            if (NowPlayingFloatingBar.Visibility != Visibility.Visible)
            {
                SetFloatingBarVisible(true, false);
            }
        }

        private void SetFloatingBarVisible(bool visible, bool animate)
        {
            if (NowPlayingFloatingBar == null)
            {
                return;
            }
            _floatingShownTarget = visible;
            if (visible)
            {
                if (NowPlayingFloatingBar.Visibility != Visibility.Visible)
                {
                    NowPlayingFloatingBar.Visibility = Visibility.Visible;
                }
                NowPlayingFloatingBar.IsHitTestVisible = true;
            }
            if (!animate)
            {
                _floatingFadeTimer?.Stop();
                NowPlayingFloatingBar.Opacity = visible ? 1 : 0;
                if (!visible)
                {
                    NowPlayingFloatingBar.IsHitTestVisible = false;
                }
                return;
            }
            double from = NowPlayingFloatingBar.Opacity;
            double to = visible ? 1 : 0;
            if (Math.Abs(from - to) < 0.01)
            {
                NowPlayingFloatingBar.Opacity = to;
                if (!visible)
                {
                    NowPlayingFloatingBar.IsHitTestVisible = false;
                }
                return;
            }
            if (_floatingFadeTimer == null)
            {
                _floatingFadeTimer = DispatcherQueue.CreateTimer();
                _floatingFadeTimer.Interval = TimeSpan.FromMilliseconds(16);
                _floatingFadeTimer.Tick += FloatingFadeTick;
            }
            _floatingFadeTimer.Stop();
            _floatingFadeFrom = from;
            _floatingFadeTo = to;
            _floatingFadeStep = 0;
            _floatingFadeTimer.Start();
        }

        private void FloatingFadeTick(object? sender, object e)
        {
            if (NowPlayingFloatingBar == null || _floatingFadeTimer == null)
            {
                return;
            }
            _floatingFadeStep++;
            const int total = 14;
            if (_floatingFadeStep >= total)
            {
                NowPlayingFloatingBar.Opacity = _floatingFadeTo;
                _floatingFadeTimer.Stop();
                if (!_floatingShownTarget)
                {
                    NowPlayingFloatingBar.IsHitTestVisible = false;
                }
                return;
            }
            NowPlayingFloatingBar.Opacity = _floatingFadeFrom
                + (_floatingFadeTo - _floatingFadeFrom) * (_floatingFadeStep / (double)total);
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
                // 移出 hover 后，按"有无封面"恢复边框：有封面→无框，无封面→显示框
                ApplyCoverFrame(TransportCoverBorder, TransportCoverImage);
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

            // 「查找歌曲…」已按用户要求移除（老版本遗留功能，窗口一并删除）。

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

            var openInExplorer = new MenuFlyoutItem { Text = "在资源管理器中打开该歌曲" };
            openInExplorer.Click += (_, _) => OpenFileLocationInExplorer(_nowPlayingPath);
            flyout.Items.Add(openInExplorer);

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

        /// <summary>独占输出内核 A/B 卡片点击：self=自研（默认），echo=ECHO 核心（C++ 原生渲染线程）。</summary>
        private void ExclusiveEngineCard_Click(object sender, RoutedEventArgs e)
        {
            if (_audioCombosLoading)
            {
                return;
            }

            if (sender is not Button b || b.Tag is not string engine)
            {
                return;
            }

            AppSettingsStore.Update(s => s.ExclusiveEngine = engine);
            UpdateEngineCardHighlight(engine);
            StartupLog.Write("[设置] 独占输出内核切换为 " + (engine == "echo" ? "ECHO 核心（实验）" : "自研") + "（下一首歌生效）");
            RefreshAudioSettingsPanel();
        }

        /// <summary>内核两张卡片：选中的用主题色高亮。</summary>
        private void UpdateEngineCardHighlight(string engine)
        {
            Windows.UI.Color accent = ResolveAccentColor();
            var activeBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent);
            var activeBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(46, accent.R, accent.G, accent.B));
            var normalBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(60, 120, 120, 120));

            bool echo = string.Equals(engine, "echo", StringComparison.OrdinalIgnoreCase);
            SetModeCard(EngineSelfCard, !echo, activeBorder, activeBg, normalBorder);
            SetModeCard(EngineEchoCard, echo, activeBorder, activeBg, normalBorder);
        }

        private async void AudioOutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_audioCombosLoading)
            {
                return;
            }

            string? appliedDevice = null;
            if (AudioOutputDeviceCombo.SelectedItem is ComboBoxItem it && it.Tag is string did)
            {
                // 用户手动改选 → 放弃「等设备插回自动切回」的暂存偏好
                appliedDevice = did;
                ClearPreferredOutputDevice();
                AppSettingsStore.Update(s => s.OutputDeviceId = did);
                await ApplyOutputDeviceAsync(did);
            }

            RefreshAudioSettingsPanel();
            // 按设备记忆：切到具体设备时自动套用该设备的 DSP 配置档
            ApplyDeviceDspProfileIfEnabled(appliedDevice);
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
                var chain = _audioEngine?.ChainFormat;

                // 源胶囊两行：源文件真实值 / 实际送链路的转码 WAV（各说各话，绝不混一行看串）
                AudioLinkSourceFmt.Text = BuildSourceFileLine(chain, hifi);
                AudioLinkTranscodeWavFmt.Text = BuildTranscodeWavLine(chain, hifi);
                // 输出胶囊两行：设备端实际格式 / 结论
                AudioLinkOutputFmt.Text = BuildDeviceLine(chain, hifi);
                AudioLinkOutputVerdict.Text = BuildVerdictLine(chain, hifi);
                AudioLinkMode.Text = hifi ? "独占（WASAPI 独占 / ASIO）" : "共享（系统混音）";

                // DSP 摘要（口径与引擎 ManagedDspSourceProvider.RefreshActive 对齐：
                // 只有真正逐样本处理的模块才计入"（开）"；限幅单独开着只待命、不计入）
                bool eqOn = EqCurveStore.Load().HasEffect();
                var extra = DspExtraStore.Load();
                bool chOn = extra.ChannelBalance?.IsActive == true;
                bool limiterOn = extra.Safety?.EnableLimiter != false;    // 限幅开关状态（≠激活：单独开只待命）
                bool hasHeadroom = extra.Safety?.AffectsBits == true;    // 余量≠0：安全模块唯一真正逐样本处理的情形
                bool rgOn = ReplayGainStore.Load().Mode != ReplayGainMode.Off;
                bool volOn = _audioEngine?.IsSoftwareVolumeActive ?? false; // HiFi 软件音量同样是采样级增益
                var active = new System.Collections.Generic.List<string>();
                if (eqOn) active.Add("EQ");
                if (chOn) active.Add("声道");
                if (hasHeadroom) active.Add("余量");
                if (rgOn) active.Add("ReplayGain");
                if (volOn) active.Add("音量");
                // 限幅：开着且已有其它逐样本模块 → 随链生效（防削波）；开着但全链直通 → 待命（不动样本）；
                // 关着 → 完全不介入，摘要里不出现。2026-09-22 用户实测"限幅没开却显示开了"根因：
                // 旧 AffectsBits 口径把"显式关限幅"算成激活（DspState.cs 已修），此处同步改口径。
                string limiterNote = !limiterOn
                    ? string.Empty
                    : (active.Count > 0
                        ? " · 限幅生效（随 DSP 链防削波）"
                        : " · 限幅待命（无其它 DSP 时不做逐样本处理，不影响 bit-perfect）");
                string dsp = active.Count == 0 ? "全部旁路" : string.Join(" / ", active) + "（开）";
                AudioLinkDsp.Text = dsp + limiterNote + (hifi ? " [HiFi 直通链路]" : " [共享链路]");
                // bit-perfect：综合判定（DSP + 输出格式 + 是否共享模式），不再只看 DSP 开关
                bool chainPure = EvaluateBitPerfectChain(out string chainReason, out bool chainConfirmed);
                AudioLinkBitPerfect.Text = chainPure
                    ? (chainConfirmed
                        ? "bit-perfect 直通：输出格式与源一致，无重采样、无 DSP 干预"
                        : "bit-perfect 直通（DSP 已旁路；开始播放后按输出格式复核）")
                    : "非 bit-perfect：" + chainReason
                      + (chainConfirmed ? string.Empty : "（待播放确认）");

                // 专业播放状态
                AudioProPosition.Text =
                    (EnginePositionValue >= TimeSpan.Zero ? EnginePositionValue.ToString(@"mm\:ss") : "--")
                    + " / " + (EngineDurationValue > TimeSpan.Zero ? EngineDurationValue.ToString(@"h\:mm\:ss") : "--");
                AudioProBuffer.Text = string.IsNullOrWhiteSpace(_audioEngine?.OutputDeviceId)
                    ? "系统默认"
                    : _audioEngine.OutputDeviceId;
                // 与上面 DSP 摘要同口径：余量✓=真在处理；限幅✓=随链生效、待=开着但全链直通、—=已关闭
                bool limiterEngaged = limiterOn && active.Count > 0;
                AudioProDspChain.Text = "EQ" + (eqOn ? "✓" : "—") + " · 声道" + (chOn ? "✓" : "—")
                    + " · 余量" + (hasHeadroom ? "✓" : "—")
                    + " · 限幅" + (limiterEngaged ? "✓" : (limiterOn ? "待" : "—")) + " · ReplayGain" + (rgOn ? "✓" : "—");
                // 链路可视化着色 + bit-perfect 徽章（用整链判定，而非仅 DSP 开关）
                ApplyLinkVisual(pure: chainPure, activeText: chainReason);
                // 同步主界面常驻 bit-perfect 徽章
                RefreshMainBitPerfectBadge();
                // 按设备记忆 DSP 配置档：开关与提示同步
                if (DeviceDspMemoryToggle != null)
                {
                    DeviceDspMemoryToggle.IsOn = DeviceDspProfileStore.IsEnabled();
                }

                // 独占输出内核 A/B 卡片高亮同步
                UpdateEngineCardHighlight(AppSettingsStore.Load().ExclusiveEngine);

                if (DeviceDspHint != null)
                {
                    string cur = _audioEngine?.OutputDeviceId;
                    if (string.IsNullOrWhiteSpace(cur))
                    {
                        DeviceDspHint.Text = "当前：系统默认设备（不按设备记忆）。";
                    }
                    else
                    {
                        bool has = DeviceDspProfileStore.HasProfile(cur);
                        DeviceDspHint.Text = (has ? "已为该设备保存配置档。" : "该设备暂无配置档；调好 DSP 后点「保存当前配置为当前设备」。")
                            + " 设备：" + cur;
                    }
                }

                // SRC 会话实际状态（源→目标 / 未升频原因）
                RefreshSrcSessionState();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>链路「源」胶囊第一行：源文件真实格式（探测结构化值；DSD/探测失败如实说明，不编数）。
        /// 绝不让显示说谎——2026-09-21 用户实测 24bit 显示成 16bit 后，本行只认 <see cref="ChainFormatState.SourceFile"/>。</summary>
        private static string BuildSourceFileLine(ChainFormatState? chain, bool hifi)
        {
            if (chain?.SourceFile is AudioFormat sf)
            {
                return "源文件：" + sf.Describe();
            }

            if (!string.IsNullOrWhiteSpace(chain?.SourceFileDescription))
            {
                return "源文件：" + chain!.SourceFileDescription;
            }

            if (chain?.Outcome == TranscodeOutcome.FailedFallback)
            {
                return "源文件：探测失败（已按兜底规格转码）";
            }

            return hifi ? "源文件：（解析中…）" : "源文件：MediaPlayer（系统解码）";
        }

        /// <summary>链路「源」胶囊第二行：实际送链路的转码 WAV（WAV 头真实值 + 转码结果注：
        /// 同格式 / 容器扩容数值无损 / 已重采样 / 已降级 / 探测失败兜底）。</summary>
        private static string BuildTranscodeWavLine(ChainFormatState? chain, bool hifi)
        {
            if (chain?.TranscodeWav is AudioFormat wav)
            {
                return "转码 WAV：" + wav.Describe() + chain.OutcomeNote();
            }

            return hifi ? "转码 WAV：（解析中…）" : "转码 WAV：不经转码（系统解码）";
        }

        /// <summary>链路「输出」胶囊第一行：设备端实际输出格式（结构化协商值；取不到时如实说未知，不假设直通）。</summary>
        private static string BuildDeviceLine(ChainFormatState? chain, bool hifi)
        {
            if (chain?.DeviceOutput is AudioFormat dev)
            {
                return "设备端：" + dev.Describe()
                    + (string.IsNullOrWhiteSpace(chain.DeviceEndpointName) ? string.Empty : "（" + chain.DeviceEndpointName + "）");
            }

            if (!hifi)
            {
                return "设备端：系统混音器（Shared）";
            }

            // 播放过但协商值缺失（如 ASIO 无 OutputWaveFormat）：不能假设直通
            return chain?.HasSession == true ? "设备端：未知（协商值缺失，待确认）" : "设备端：（解析中…）";
        }

        /// <summary>链路「输出」胶囊第二行：结论。先讲转码层事实（重采样/降级），再讲设备端路径，
        /// 两者独立——"设备端源直通"与"转码已重采样"可以同时成立且都必须说。</summary>
        private static string BuildVerdictLine(ChainFormatState? chain, bool hifi)
        {
            if (!hifi)
            {
                return "结论：共享模式——系统混音器必然重采样，非 bit-perfect";
            }

            if (chain == null || !chain.HasSession)
            {
                return "结论：待播放确认";
            }

            var parts = new System.Collections.Generic.List<string>();
            string note = chain.OutcomeNote(); // （已重采样，迁就设备）/（已降级：规格被做低）/（源探测失败…）
            if (note.Length > 0)
            {
                parts.Add("转码" + note);
            }

            parts.Add(chain.VerdictNote());
            return "结论：" + string.Join(" · ", parts);
        }

        /// <summary>计算与音频设置面板徽章同一口径的链路纯净度：任一真正逐样本处理的 DSP
        ///（EQ / 声道平衡 / 余量 / ReplayGain / 房间校正卷积 / HiFi 软件音量）生效即非 bit-perfect；
        /// 限幅单独开/关都只待命不计数；DSP 总旁路优先视为直通。</summary>
        private bool IsBitPerfectPure(out string activeText)
        {
            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;
            if (bypass) { activeText = string.Empty; return true; }

            bool eqOn = EqCurveStore.Load().HasEffect();
            var extra = DspExtraStore.Load();
            bool chOn = extra.ChannelBalance?.IsActive == true;
            // 余量≠0 = 安全模块唯一真正逐样本处理的情形（DspSafetyState.AffectsBits 现口径，2026-09-22 修：
            // 旧口径把"显式关限幅"也算激活，导致用户"限幅没开"却被显示/计成"开"）。限幅单独开关都不计数。
            bool hasHeadroom = extra.Safety?.AffectsBits == true;
            bool rgOn = ReplayGainStore.Load().Mode != ReplayGainMode.Off;
            var room = RoomCorrectionStore.Load();
            bool firOn = room.Enabled && !string.IsNullOrWhiteSpace(room.IrPath);
            // HiFi 软件音量（独占/ASIO + 设置页开关 + 音量≠100%）：DSP 链采样级衰减，同样破坏 bit-perfect。
            bool volOn = _audioEngine?.IsSoftwareVolumeActive ?? false;
            var active = new System.Collections.Generic.List<string>();
            if (eqOn) active.Add("EQ");
            if (chOn) active.Add("声道");
            if (hasHeadroom) active.Add("余量");
            if (rgOn) active.Add("ReplayGain");
            if (firOn) active.Add("房间校正");
            if (volOn) active.Add("音量");
            activeText = active.Count == 0 ? string.Empty : string.Join("、", active);
            return active.Count == 0;
        }

        /// <summary>
        /// 综合判定整条链路是否 bit-perfect（2026-09-22 重写为标志位判定）。
        ///
        /// 地基是 <see cref="ChainFormatState"/>：源文件 / 转码 WAV / 设备端三段真实值 + 判定标志，
        /// 由解码器与输出内核结构化填写。徽标只判这些标志，**绝不从人话描述串反解析数字**——
        /// 2026-09-22 用户实测：描述串嵌源位深，正则从源段解析出 24bit 与源相等 → 绿灯谎报。
        ///
        /// 判定（任一命中即非 bit-perfect）：
        ///   1) 任一 DSP 真正激活（EQ / 声道平衡 / ReplayGain / 余量 / 房间校正卷积 / HiFi 软件音量；
        ///      限幅单独开/关都只待命，不计数）
        ///   2) 共享模式（系统混音器介入；设置层已知，与是否在播无关）
        ///   3) 转码层：WAV 相对源被重采样 / 降位 / 探测失败兜底（<see cref="ChainFormatState.Outcome"/>）
        ///   4) 设备端：协商出的设备率≠WAV 率；或端点数值有损（<see cref="DevicePath.Degraded"/>）/
        ///      非直通转换（<see cref="DevicePath.Resampled"/>）
        ///   5) 播放过但设备协商值缺失（ASIO 等）：不假设直通，按"无法确认"转琥珀
        /// DSD/DoP 路径跳过 PCM 重采样比对（输出 PCM 只是 1-bit 的封装载体），只看设备容器是否直通。
        /// 未播放（HasSession=false）时只按 DSP/共享模式判定，confirmed=false，UI 标注「待播放确认」。
        /// 只读状态用于显示，不改动任何音频字节流。
        /// </summary>
        private bool EvaluateBitPerfectChain(out string reason, out bool confirmed)
        {
            reason = string.Empty;
            confirmed = false;

            var causes = new System.Collections.Generic.List<string>();

            // 1) DSP 是否真正参与处理
            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;
            if (!bypass)
            {
                // 2026-09-22 修：旧条件写反成 IsBitPerfectPure(...) && dspText 非空——
                // 而 IsBitPerfectPure 返回 true 时 dspText 必为空串，条件永假，
                // 导致"开着 EQ 徽标也可能绿灯"的严重谎报。IsBitPerfectPure=false 时才有 activeText。
                if (!IsBitPerfectPure(out string dspText))
                {
                    causes.Add("DSP：" + dspText);
                }
            }

            // 2) 共享模式：Windows 音频引擎介入，格式即使一致也不保证直通。
            //    这是设置层就已知的事实，不依赖播放中的输出格式捕获，未播放时也要判。
            if (!IsHiFiModeSelected())
            {
                causes.Add("系统混音器（共享模式）");
            }

            // 3)4)5) 结构化链路状态：全部判标志位，不反解析任何描述串
            var chain = _audioEngine?.ChainFormat;
            if (chain != null && chain.HasSession)
            {
                if (chain.IsDsdPath)
                {
                    // DSD/DoP：1-bit 原生封装直通，输出 PCM 只是承载，不做重采样比对
                    confirmed = true;
                    if (chain.Device == DevicePath.Degraded)
                    {
                        causes.Add("DSD 设备端容器低于源，数值有损");
                    }
                    else if (chain.Device == DevicePath.Resampled)
                    {
                        causes.Add("DSD 设备端对封装做了转换（非直通）");
                    }
                }
                else
                {
                    // 3) 转码层：实际送链的 WAV 相对源文件发生了什么
                    switch (chain.Outcome)
                    {
                        case TranscodeOutcome.ResampledToDevice:
                            causes.Add("转码已重采样（迁就设备采样率）");
                            break;
                        case TranscodeOutcome.Downsampled:
                            causes.Add("转码降级（规格被做低）");
                            break;
                        case TranscodeOutcome.FailedFallback:
                            causes.Add("源探测失败，按兜底规格转码");
                            break;
                    }

                    // 4) 设备端：协商值 vs 实际送链的 WAV（两者都有才算"已确认"）
                    var dev = chain.DeviceOutput;
                    var wav = chain.TranscodeWav;
                    if (dev != null && wav != null)
                    {
                        confirmed = true;
                        if (dev.Value.Rate != wav.Value.Rate)
                        {
                            causes.Add($"重采样 {wav.Value.Rate}→{dev.Value.Rate}hz");
                        }

                        if (chain.Device == DevicePath.Degraded)
                        {
                            causes.Add("设备端格式低于源，数值有损");
                        }
                        else if (chain.Device == DevicePath.Resampled)
                        {
                            causes.Add("设备端格式转换（非直通）");
                        }
                        else if (chain.Device == DevicePath.Lossless && dev.Value.Bits < wav.Value.Bits)
                        {
                            // 双保险：Lossless 判定说数值无损，但容器位深反而更低时仍要现形
                            causes.Add($"位深转换 {wav.Value.Bits}→{dev.Value.Bits}bit");
                        }
                    }
                    else
                    {
                        // 5) 播放过但设备协商值缺失（ASIO 无 OutputWaveFormat / 内核未回报）：
                        //    不能假设直通，按"无法确认"处理，徽标转琥珀，宁可误报不可漏报。
                        causes.Add("设备端格式未知，无法确认直通");
                        confirmed = true;
                    }
                }
            }

            reason = string.Join(" · ", causes);
            return causes.Count == 0;
        }

        /// <summary>按设备记忆：若开启且当前设备有已存配置档，则套用该设备的 DSP 配置（不碰音频字节流）。</summary>
        private void ApplyDeviceDspProfileIfEnabled(string deviceId)
        {
            try
            {
                if (!DeviceDspProfileStore.IsEnabled()) return;
                if (string.IsNullOrWhiteSpace(deviceId)) return;            // 系统默认设备不按设备记忆
                if (!DeviceDspProfileStore.HasProfile(deviceId)) return;   // 无存档则用全局配置，避免被默认覆盖
                var profile = DeviceDspProfileStore.GetProfile(deviceId);
                DeviceDspProfileStore.ApplyToStores(profile);
                // 重新填充音效面板控件 + 内存 EQ 曲线（与启动加载同口径）
                LoadAudioFxUiFromStore();
                // 推送到引擎（内部已按 _audioFxPanelReady 守卫；未就绪时仅写盘不应用）
                ApplyDspToEngine();
                // 独立的 10 段均衡器与房间校正由引擎直接读 store
                _audioEngine?.SetEqualizer(EqualizerStore.Load().BandGains);
                RefreshAudioSettingsPanel();
                StartupLog.Write($"[DSP] 已套用设备配置档：{deviceId}");
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        private void DeviceDspMemoryToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                bool on = DeviceDspMemoryToggle?.IsOn == true;
                DeviceDspProfileStore.SetEnabled(on);
                // 开启时，若当前设备尚无存档则把当前配置存为它的配置档，立即生效
                string cur = _audioEngine?.OutputDeviceId ?? string.Empty;
                if (on && !string.IsNullOrWhiteSpace(cur) && !DeviceDspProfileStore.HasProfile(cur))
                {
                    DeviceDspProfileStore.SaveProfile(cur, DeviceDspProfileStore.CaptureCurrent());
                }

                RefreshAudioSettingsPanel();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        private void SaveDeviceDspButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string cur = _audioEngine?.OutputDeviceId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(cur))
                {
                    DeviceDspHint.Text = "当前为系统默认设备，无法按设备记忆；请先在「输出设备」里选具体设备。";
                    return;
                }

                DeviceDspProfileStore.SaveProfile(cur, DeviceDspProfileStore.CaptureCurrent());
                DeviceDspHint.Text = "已保存当前配置为设备：" + cur;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        /// <summary>刷新主播放界面常驻的 bit-perfect 徽章（绿=直通 / 琥珀=DSP 处理中）。</summary>
        private void RefreshMainBitPerfectBadge()
        {
            try
            {
                if (MainBitPerfectBadge == null || MainBitPerfectText == null) return;
                // 播放详情页不再显示这条徽章（非直通时会撑成长条），隐藏后就不必再刷。
                if (MainBitPerfectBadge.Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;
                bool pure = EvaluateBitPerfectChain(out string reason, out bool confirmed);
                var green = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 160, 67));
                var amber = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 214, 148, 45));
                MainBitPerfectBadge.Background = pure ? green : amber;
                // confirmed=false = 输出格式还没捕获（未播放）或无法识别：绿灯也要注明"待播放确认"，
                // 不能让人以为已经核实过（2026-09-22 用户实测徽标谎报后收紧口径）。
                MainBitPerfectText.Text = pure
                    ? (confirmed ? "✓ bit-perfect · 直通" : "✓ bit-perfect · 待播放确认")
                    : (confirmed ? "非 bit-perfect · " + reason : "待播放确认 · " + reason);
                MainBitPerfectText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
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
                AudioLinkBitPerfectBadgeText.Text = pure
                    ? "✓ bit-perfect · 直通"
                    : (string.IsNullOrWhiteSpace(activeText) ? "非 bit-perfect" : "非 bit-perfect · " + activeText);
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

            // 「查找歌曲…」已按用户要求移除（老版本遗留功能，窗口一并删除）。

            // 工具：图标用 Segoe Fluent 的「扳手/修复」字形
            var tools = new MenuFlyoutSubItem { Text = "工具" };
            tools.Icon = new FontIcon { Glyph = "\uE90F" };
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
