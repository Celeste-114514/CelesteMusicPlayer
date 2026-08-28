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

        internal bool IsEnginePlayingNow => _audioEngine?.IsPlaying == true;

        internal bool IsEngineActiveNow => _audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused);

        internal TimeSpan EnginePositionValue => _audioEngine?.Position ?? TimeSpan.Zero;

        internal TimeSpan EngineDurationValue => _audioEngine?.Duration ?? TimeSpan.Zero;

        private static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".m4a", ".flac", ".wma", ".ogg", ".aac",
            ".ape", ".wv", ".tta", ".mpc", ".tak", ".opus",
            ".dsf", ".dff",
            ".mp2", ".amr", ".au", ".mod", ".s3m", ".xm"
        };


        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode)]
        private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll")]
        private static extern nint CallWindowProcW(nint wndProc, nint hWnd, uint msg, nint wParam, nint lParam);
        private string? _genreYearFilter;

        /// <summary>评分分类当前选中的评分数值（0..5；-1 = 未选择，显示全部有评分项）。</summary>
        private int _ratingFilter = -1;
        private readonly ObservableCollection<AlbumEntry> _albums = new();
        private readonly ObservableCollection<PlaylistCardViewModel> _playlistWall = new();
        private readonly ObservableCollection<PlaylistItem> _playlistDetailItems = new();
        private string? _currentPlaylistDetail;
        private static readonly Microsoft.UI.Xaml.Media.Brush PlaylistDetailHoverBg =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
        private readonly ObservableCollection<PlaylistItem> _albumTracks = new();
        private readonly ObservableCollection<ArtistEntry> _artists = new();
        private readonly ObservableCollection<PlaylistItem> _artistTracks = new();
        private readonly ObservableCollection<AlbumEntry> _artistAlbums = new();
        private readonly ObservableCollection<FolderBrowserItem> _folderBrowserItems = new();

        /// <summary>「选择文件夹」选定的根目录；文件夹分类只展示其内容</summary>
        private string? _browseFolderPath;

        private ArtistEntry? _avatarContextArtist;
        private PlaylistItem? _contextMenuSong;
        private bool _isMultiSelectMode;
        private bool _isEnginePaused;
        private bool _usingEnginePlayback;

        private float[]? _waveformData;
        private string? _waveformPath;
        private string _progressBarStyle = "Gradient";
        // 主题波形强调色（ResolveAccentColor 的缓存，避免频繁解析）
        private static Color _waveAccentColor = Color.FromArgb(255, 0, 120, 212);
        private SystemMediaTransportControls? _engineSmtc;
        private long _lastSmtcTimelineMs; // SMTC timeline 限频（约 500ms 一次）
        private Style? _playlistItemDefaultStyle;
        private Style? _artistTrackItemDefaultStyle;
        private Style? _albumTrackItemDefaultStyle;
        private Style? _artistAlbumItemDefaultStyle;
        private Style? _libraryAlbumItemDefaultStyle;
        private Style? _folderItemDefaultStyle;
        /// <summary>多选当前作用的歌曲列表（歌曲库 / 艺术家详情 / 专辑详情曲目）</summary>
        private ListView? _multiSelectTargetList;
        /// <summary>多选当前作用的专辑网格（音乐库专辑 / 艺术家详情专辑）</summary>
        private GridView? _multiSelectAlbumGrid;
        /// <summary>多选当前作用的文件夹浏览列表</summary>
        private ListView? _multiSelectFolderList;
        /// <summary>批量改选中时跳过 SelectionChanged 里的昂贵 UI 刷新</summary>
        private bool _suppressSelectionUiUpdates;
        private Brush? _cachedMultiSelectFrostBrush;

        private int _currentIndex = -1;
        /// <summary>当前播放在用户播放列表中的下标（播放顺序以播放列表为准）</summary>
        private int _userPlaylistIndex = -1;
        private PlaylistItem? _seamlessPreloaded; // 已预加载待无缝接续的下一首记录（供 SeamlessTrackChanged 更新 UI）
        private CurrentPlaylistWindow? _currentPlaylistWindow;
        internal PlayQueueWindow? QueueWindow { get; set; }


        private void InitializePlayerAndTimers()
        {
            if (_mediaPlayer != null)
            {
                return;
            }

            if (PlayerElement == null)
            {
                _ = ShowErrorAsync("初始化失败", "PlayerElement 未生成，请检查 MainWindow.xaml 中的 x:Name。");
                return;
            }

            _mediaPlayer = new MediaPlayer();
            PlayerElement.SetMediaPlayer(_mediaPlayer);
            ApplyAudioChannelFromSettings();
            _mediaPlayer.CommandManager.IsEnabled = false;

            _mediaPlayer.MediaOpened += Player_MediaOpened;
            _mediaPlayer.MediaEnded += Player_MediaEnded;
            _mediaPlayer.MediaFailed += Player_MediaFailed;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;

            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            ApplyStartupPlaybackSettings();
            _mediaPlayer.Volume = VolumeSlider.Value / 100.0;
            UpdateVolumeIcon(VolumeSlider.Value);
            UpdateSignalChainDisplay();
            UpdateDesktopLyricsBadge();
            UpdateMiniPlayerBadge();
            InitializeMusicPlayer2Features();

            ProgressSlider.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(ProgressSlider_PointerPressed),
                handledEventsToo: true);
            ProgressSlider.AddHandler(
                UIElement.PointerReleasedEvent,
                new PointerEventHandler(ProgressSlider_PointerReleased),
                handledEventsToo: true);
            ProgressSlider.AddHandler(
                UIElement.PointerCaptureLostEvent,
                new PointerEventHandler(ProgressSlider_PointerCaptureLost),
                handledEventsToo: true);

            _positionTimer = DispatcherQueue.CreateTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(200);
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            // 悬停提示定时器（满 1 秒才弹出）
            _hoverTipTimer = DispatcherQueue.CreateTimer();
            _hoverTipTimer.IsRepeating = false;
            _hoverTipTimer.Interval = TimeSpan.FromMilliseconds(500);
            _hoverTipTimer.Tick += HoverTipTimer_Tick;

            _waveformTimer = DispatcherQueue.CreateTimer();
            _waveformTimer.Interval = TimeSpan.FromMilliseconds(50);
            _waveformTimer.Tick += WaveformTimer_Tick;
            // 不在启动时常开：仅播放中驱动，避免定时改视觉树导致全窗光标闪烁
            // 未播放时也填充静态频谱，保证信息卡波形始终可见
            for (int i = 0; i < WaveBarCount; i++)
            {
                _waveLevels[i] = IdleLevel(i);
            }

            ApplyPlaybackOrderToPlayer();
            UpdatePlaybackOrderButtonUi();
            ClearNowPlayingPanel();
            ApplyNowPlayingCardChrome();
            if (Content is FrameworkElement root)
            {
                root.ActualThemeChanged += (_, _) =>
                {
                    ApplyNowPlayingCardChrome();
                    ApplyArtistSongsFrostChrome();
                    UpdateLibraryNavHighlight();
                    ApplyAccentSelectionResources(PlaylistView);
                    RefreshPlaylistSelectionChrome();
                    ApplyCapsuleSortButtonStyle(accent: true);
                    ApplyPlaylistHeaderChipStyle();
                };
            }

            NowPlayingPane.SizeChanged += (_, _) => UpdateNowPlayingCardLayout();
            MainContentGrid.SizeChanged += (_, _) => FitColumnsToAvailableWidth();
            AppWindow.Changed += (_, args) =>
            {
                if (args.DidSizeChange || args.DidPresenterChange)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        EnforceMinimumWindowSize();
                        FitColumnsToAvailableWidth();
                        UpdateNowPlayingCardLayout();
                    });
                }
            };

            DispatcherQueue.TryEnqueue(() =>
            {
                FitColumnsToAvailableWidth();
                UpdateNowPlayingCardLayout();
            });

            _ = RestoreLastLibraryAsync();
        }


        /// <summary>音量、播放模式等可立即应用的启动设置。</summary>
        private void ApplyStartupPlaybackSettings()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            // 流媒体插件服务地址（WSL），供在线歌词/搜索调用
            StreamingServiceClient.ServiceBaseUrl = settings.StreamingServiceUrl;
            _applyingSettingsVolume = true;
            try
            {
                if (IsHiFiModeSelected())
                // 音量滑条在共享与 HiFi 独占下都可用：HiFi 下调 DAC 设备/驱动级主音量（不破坏 bit-perfect），
                // 用保存音量回填，避免切模式/重启后音量跳回 100%。
                VolumeSlider.Value = Math.Clamp(settings.Volume, 0, 100);
                _volumeToSave = Math.Clamp(settings.Volume, 0, 100); // 启动即同步，避免退出时以旧/0 值写盘
            }
            finally
            {
                _applyingSettingsVolume = false;
            }

            if (Enum.TryParse(settings.PlaybackOrder, ignoreCase: true, out PlaybackOrder order))
            {
                _playbackOrder = order;
            }

            _ = ApplyOutputDeviceAsync(settings.OutputDeviceId);
            ApplyEngineOutputMode(settings);
        }


        /// <summary>应用 HiFi 输出设备：记录到引擎偏好并设置 MediaPlayer 输出设备。</summary>
        private async System.Threading.Tasks.Task ApplyOutputDeviceAsync(string deviceId)
        {
            try
            {
                _audioEngine?.SetOutputDevicePreference(string.IsNullOrWhiteSpace(deviceId) ? null : deviceId);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                string? devId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
                if (_mediaPlayer == null)
                {
                    return;
                }

                if (devId != null)
                {
                    try
                    {
                        var deviceInfo = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(devId);
                        _mediaPlayer.AudioDevice = deviceInfo;
                    }
                    catch
                    {
                        // 设备不存在/已移除：回退默认
                        _mediaPlayer.AudioDevice = null;
                    }
                }
                else
                {
                    _mediaPlayer.AudioDevice = null;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private bool _isUpdatingNowPlayingLayout;

        /// <summary>
        /// 播放信息页：大封面尺寸随面板高度自适应；波形与封面同宽并居中。
        /// </summary>
        private void UpdateNowPlayingCardLayout()
        {
            if (_isUpdatingNowPlayingLayout)
            {
                return;
            }

            _isUpdatingNowPlayingLayout = true;
            try
            {
            double paneWidth = NowPlayingPane.ActualWidth;
            double paneHeight = NowPlayingPane.ActualHeight;
            if (paneWidth <= 0 || paneHeight <= 0)
            {
                return;
            }

            NowPlayingPaneContent.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, paneWidth, paneHeight)
            };

            // 大封面：按面板高度 50% 居中，上限 340、下限 240
            double coverSize = Math.Clamp(paneHeight * 0.5, 240, 340);
            NowPlayingCoverBorder.Width = coverSize;
            NowPlayingCoverBorder.Height = coverSize;
            WaveformCanvas.Width = coverSize;

            // 左半区：左栏(封面+信息)中轴在主UI左1/4处(paneWidth/4)，宽=左半区-左右各30
            double half = paneWidth / 2.0;
            double sideContentMax = Math.Max(0, half - 60);
            if (NowPlayingLeftColumn != null)
            {
                // 左栏宽固定为半区宽(左右留30)，封面/信息在其中以中轴=paneWidth/4 居中，信息区超宽换行
                NowPlayingLeftColumn.ClearValue(FrameworkElement.MaxWidthProperty);
                NowPlayingLeftColumn.ClearValue(FrameworkElement.MinWidthProperty);
                NowPlayingLeftColumn.MaxWidth = sideContentMax;
                NowPlayingLeftColumn.Width = sideContentMax;
            }
            // 左栏默认居中于 paneWidth/2，位移到 paneWidth/4 → 封面中轴=主UI左1/4
            if (NowPlayingLeftShift != null)
            {
                NowPlayingLeftShift.TranslateX = -paneWidth / 4.0;
            }
            // 信息区：左右各留30(半区宽)，StackPanel 垂直布局会给子无穷宽，故必须给每个 TextBlock/Button 自身设 MaxWidth 才换行
            double infoTextMax = Math.Max(0, sideContentMax - 16);
            NowPlayingTitleText.MaxWidth = infoTextMax;
            NowPlayingAudioInfoText.MaxWidth = infoTextMax;
            SignalChainInfoText.MaxWidth = infoTextMax;
            NowPlayingArtistText.MaxWidth = infoTextMax;
            NowPlayingAlbumText.MaxWidth = infoTextMax;
            if (NowPlayingArtistLinkButton != null)
            {
                NowPlayingArtistLinkButton.MaxWidth = infoTextMax;
                NowPlayingArtistLinkButton.Width = infoTextMax;
            }
            if (NowPlayingAlbumLinkButton != null)
            {
                NowPlayingAlbumLinkButton.MaxWidth = infoTextMax;
                NowPlayingAlbumLinkButton.Width = infoTextMax;
            }
            if (NowPlayingMetaPanel != null)
            {
                NowPlayingMetaPanel.MaxWidth = sideContentMax;
                NowPlayingMetaPanel.Width = sideContentMax - 16;
                NowPlayingMetaPanel.Margin = new Thickness(0);
            }
            // 右半区：歌词中轴在主UI右1/4处(3/4paneWidth)，宽=右半区-左右各30，居中换行
            if (LyricsSection != null)
            {
                LyricsSection.MaxWidth = sideContentMax;
                LyricsSection.Width = sideContentMax;
            }
            if (LyricsShift != null)
            {
                LyricsShift.TranslateX = paneWidth / 4.0;
            }
            // 让每条歌词在歌词区宽内真正换行(居中区宽 - 留边)
            double lyricMax = Math.Max(0, sideContentMax - 16);
            if (LyricsPanel != null)
            {
                foreach (var child in LyricsPanel.Children)
                {
                    if (child is Microsoft.UI.Xaml.Controls.TextBlock tb)
                    {
                        tb.MaxWidth = lyricMax;
                    }
                }
            }
            }
            finally
            {
                _isUpdatingNowPlayingLayout = false;
            }
        }

        /// <summary>信息卡：更深毛玻璃 + 阴影立体感</summary>
        private void ApplyNowPlayingCardChrome()
        {
            if (NowPlayingPane != null)
            {
                // 无外框：透明背景、无圆角（右侧面板不显示独立边框）
                NowPlayingPane.Background = null;
                NowPlayingPane.BorderBrush = null;
                NowPlayingPane.BorderThickness = new Thickness(0);
                NowPlayingPane.CornerRadius = new CornerRadius(0);
                NowPlayingPane.Padding = new Thickness(12);
            }

            ApplyArtistSongsFrostChrome();
        }


        /// <summary>
        /// 右侧信息卡 / 艺术家歌曲区共用的透明毛玻璃（纯色 Tint，无额外高光层）。
        /// </summary>
        private Brush CreateNowPlayingStyleAcrylicBrush()
            => FrostedGlass.CreatePanelBrush(ResolveUiBaseTintColor());

        /// <summary>
        /// 深色 Tint 的 Acrylic：提高霜化/模糊感，色调跟整体 UI，避免发白。
        /// 应用于播放信息页内容面板（底层），后续由封面动态背景覆盖。
        /// </summary>
        private void ApplyNowPlayingCardAcrylic()
        {
            if (NowPlayingPaneContent != null)
            {
                NowPlayingPaneContent.Background = CreateNowPlayingStyleAcrylicBrush();
            }
        }


        /// <summary>外围阴影（由动态背景/遮罩统一处理，无独立阴影层）。</summary>
        private void ApplyNowPlayingCardShadow()
        {
        }


        /// <summary>恢复上次正在播放的歌曲到界面（暂停，不自动开播）。</summary>
        private async Task RestoreLastPlayingTrackAsync()
        {
            try
            {
                if (!AppSettingsStore.Load().RestorePlayback)
                {
                    return;
                }

                PlaybackSessionState? session = PlaybackSessionStore.TryLoad();
                if (session == null
                    || string.IsNullOrWhiteSpace(session.FilePath)
                    || !System.IO.File.Exists(session.FilePath))
                {
                    return;
                }

                string path = session.FilePath;
                PlaylistItem? item = null;
                int userIdx = FindUserPlaylistIndex(path);
                if (userIdx >= 0)
                {
                    item = _userPlaylist[userIdx];
                }
                else
                {
                    int libIdx = FindLibraryIndex(path);
                    if (libIdx >= 0)
                    {
                        item = _playlist[libIdx];
                        AddSongsToUserPlaylist(new[] { item });
                        userIdx = FindUserPlaylistIndex(path);
                    }
                    else
                    {
                        item = CreatePlaylistItemFromPath(path);
                        AddSongsToUserPlaylist(new[] { item });
                        userIdx = FindUserPlaylistIndex(path);
                    }
                }

                if (item == null || userIdx < 0)
                {
                    return;
                }

                await PrepareTrackPausedAsync(userIdx, session.PositionSeconds);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复播放曲目失败: {ex.Message}");
            }
        }


        private async Task PrepareTrackPausedAsync(int userPlaylistIndex, double positionSeconds)
        {
            if (userPlaylistIndex < 0 || userPlaylistIndex >= _userPlaylist.Count)
            {
                return;
            }

            PlaylistItem item = _userPlaylist[userPlaylistIndex];
            _userPlaylistIndex = userPlaylistIndex;
            _currentIndex = FindLibraryIndex(item.FilePath);
            _pendingRestorePositionSeconds = Math.Max(0, positionSeconds);

            NowPlayingText.Text = "已就绪：" + item.Title + " - " + item.Artist;
            await UpdateNowPlayingPanelAsync(item);

            // 扩展格式（APE/WavPack 等）：系统 Media Foundation 无法解码，启动时不预加载，
            // 避免触发 MediaFailed 弹窗；点击播放时由 FFmpeg 引擎转码播放。
            if (AudioPlaybackEngine.NeedsFfmpeg(item.FilePath))
            {
                return;
            }

            // HiFi 独占模式：不把歌曲塞进 MediaPlayer（否则会走共享混音并出现在音量合成器）。
            // 就绪状态保留，由用户点播放时经 StartPlayback 走独占（NAudio）路径。
            if (IsHiFiModeSelected())
            {
                NotifyCurrentPlaylistWindow();
                _miniPlayerWindow?.RefreshFromOwner();
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            try
            {
                MediaSource source = MediaSource.CreateFromUri(CreateFileMediaUri(item.FilePath));
                player.Source = source;
                if (AppSettingsStore.Load().AutoPlayWhenStart)
                {
                    player.Play();
                }
                else
                {
                    player.Pause();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("恢复加载媒体失败: " + ex.Message);
            }

            NotifyCurrentPlaylistWindow();
            _miniPlayerWindow?.RefreshFromOwner();
        }


        private void PersistPlaybackSession()
        {
            try
            {
                if (!AppSettingsStore.Load().RestorePlayback)
                {
                    return;
                }

                PlaylistItem? item = GetCurrentPlayingItem();
                if (item == null)
                {
                    return;
                }

                double pos = GetPlayer()?.PlaybackSession.Position.TotalSeconds ?? 0;
                PlaybackSessionStore.Save(item.FilePath, pos);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }



        private void PlaybackRateButton_Click(object sender, RoutedEventArgs e)
        {
            // 引擎（FFmpeg 转码播放）暂不支持变速
            if (_audioEngine?.IsPlaying == true || _isEnginePaused)
            {
                NowPlayingText.Text = "引擎播放暂不支持变速，请使用系统原生格式";
                return;
            }

            var flyout = new MenuFlyout();
            double current = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
            foreach (double rate in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            {
                double r = rate;
                var item = new MenuFlyoutItem { Text = r.ToString("0.##") + "x" };
                item.Click += (_, _) => SetPlaybackRate(r);
                flyout.Items.Add(item);
            }

            flyout.ShowAt(sender as FrameworkElement);
        }


        private void SetPlaybackRate(double rate)
        {
            AppSettingsStore.Update(s => s.PlaybackRate = Math.Clamp(rate, 0.5, 2.0));
            ApplyPlaybackRateFromSettings();
            UpdatePlaybackRateButtonText();
        }


        private void UpdatePlaybackRateButtonText()
        {
            if (PlaybackRateText == null)
            {
                return;
            }

            double rate = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
            PlaybackRateText.Text = rate.ToString("0.##") + "x";
        }

        private MediaPlayer? GetPlayer() => _mediaPlayer ?? PlayerElement?.MediaPlayer;

        /// <summary>汉堡菜单：选择文件 / 文件夹 / 重新扫描</summary>
        private void SelectLocalAudioButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout
            {
                Placement = FlyoutPlacementMode.Bottom
            };

            var fileItem = new MenuFlyoutItem { Text = "选择文件…" };
            fileItem.Icon = new FontIcon { Glyph = "\uE710" }; // Add
            fileItem.Click += OpenFileButton_Click;

            var folderItem = new MenuFlyoutItem { Text = "选择文件夹…" };
            folderItem.Icon = new FontIcon { Glyph = "\uE8B7" }; // Folder
            folderItem.Click += OpenFolderButton_Click;

            var rescanItem = new MenuFlyoutItem { Text = "重新扫描本地文件" };
            rescanItem.Icon = new FontIcon { Glyph = "\uE72C" }; // Refresh
            rescanItem.Click += RescanLocalLibraryButton_Click;

            flyout.Items.Add(fileItem);
            flyout.Items.Add(folderItem);
            flyout.Items.Add(rescanItem);
            AppendHamburgerFeatureItems(flyout);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var mediaLibItem = new MenuFlyoutItem { Text = "媒体库" };
            mediaLibItem.Icon = new FontIcon { Glyph = "\uE838" }; // MusicLibrary
            mediaLibItem.Click += (_, _) => SettingsWindow.ShowMediaLibrary();
            flyout.Items.Add(mediaLibItem);

            var settingsItem = new MenuFlyoutItem { Text = "选项设置" };
            settingsItem.Icon = new FontIcon { Glyph = "\uE713" };
            settingsItem.Click += (_, _) => SettingsWindow.ShowOrActivate();
            flyout.Items.Add(settingsItem);

            flyout.ShowAt(SelectLocalAudioButton, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.Bottom
            });
        }


        /// <summary>TagLib 读取标题 / 艺术家 / 专辑 / 音轨号 / 年份 / 时长</summary>
        private static PlaylistItem CreatePlaylistItemFromPath(string path)
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            string title = fileName;
            string artist = "未知艺术家";
            string albumArtist = "未知艺术家";
            string album = "未知专辑";
            uint track = 0;
            uint disc = 0;
            uint year = 0;
            string genre = "未知流派";
            TimeSpan duration = TimeSpan.Zero;

            using (TagLib.File tagFile = TagLib.File.Create(path))
            {
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title))
                {
                    title = tagFile.Tag.Title.Trim();
                }

                string? performer = tagFile.Tag.FirstPerformer;
                if (string.IsNullOrWhiteSpace(performer))
                {
                    performer = tagFile.Tag.JoinedPerformers;
                }

                if (!string.IsNullOrWhiteSpace(performer))
                {
                    artist = performer.Trim();
                }

                string? albumPerformer = tagFile.Tag.FirstAlbumArtist;
                if (string.IsNullOrWhiteSpace(albumPerformer))
                {
                    albumPerformer = tagFile.Tag.JoinedAlbumArtists;
                }

                if (!string.IsNullOrWhiteSpace(albumPerformer))
                {
                    albumArtist = albumPerformer.Trim();
                }
                else
                {
                    albumArtist = artist;
                }

                if (!string.IsNullOrWhiteSpace(tagFile.Tag.Album))
                {
                    album = tagFile.Tag.Album.Trim();
                }

                track = tagFile.Tag.Track;
                disc = tagFile.Tag.Disc;
                year = tagFile.Tag.Year;
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstGenre))
                {
                    genre = tagFile.Tag.FirstGenre.Trim();
                }

                duration = tagFile.Properties.Duration;
            }

            return new PlaylistItem
            {
                Title = title,
                Artist = artist,
                AlbumArtist = albumArtist,
                Album = album,
                Track = track,
                Disc = disc,
                Year = year,
                Genre = genre,
                Duration = duration,
                FilePath = path,
                Rating = TrackStatsStore.Get(path)?.Rating ?? 0
            };
        }


        private void UserPlaylistNavButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "PlaylistWall";
                ApplyCategoryView();
            });
        }


        // ---------------- 响度归一化（ReplayGain） ----------------

        private void SelectAudioFxRgMode(ReplayGainMode mode)
        {
            for (int i = 0; i < AudioFxRgModeCombo.Items.Count; i++)
            {
                if (AudioFxRgModeCombo.Items[i] is ComboBoxItem { Tag: ReplayGainMode m } && m == mode)
                {
                    AudioFxRgModeCombo.SelectedIndex = i;
                    return;
                }
            }

            AudioFxRgModeCombo.SelectedIndex = 0;
        }


        private void AudioFxRgPreventClipping_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_audioFxLoading) ApplyReplayGainToEngine();
        }


        private ReplayGainMode CurrentAudioFxRgMode()
        {
            return AudioFxRgModeCombo.SelectedItem is ComboBoxItem { Tag: ReplayGainMode m } ? m : ReplayGainMode.Off;
        }


        /// <summary>收集 ReplayGain 面板状态 → 持久化 + 应用到引擎（带上当前曲目的增益/peak）。</summary>
        private void ApplyReplayGainToEngine()
        {
            // 与 ApplyDspToEngine 同理：面板未就绪时不保存/应用（避免启动阶段默认覆盖）。
            if (!_audioFxPanelReady)
            {
                return;
            }

            var rg = new ReplayGainState
            {
                Mode = CurrentAudioFxRgMode(),
                PreampDb = AudioFxRgPreampSlider.Value,
                PreventClipping = AudioFxRgPreventClippingToggle.IsOn
            };
            ReplayGainStore.Save(rg);

            double tg = _currentRgData?.TrackGainDb ?? 0;
            double ag = _currentRgData?.AlbumGainDb ?? 0;
            double peak = _currentRgData?.Peak ?? 1.0;
            _audioEngine?.SetReplayGain(rg, tg, ag, peak);
            RefreshAudioFxRgInfo();
        }


        /// <summary>刷新当前曲目 ReplayGain 标签信息文本。</summary>
        private void RefreshAudioFxRgInfo()
        {
            if (AudioFxRgInfoText == null) return;
            if (_currentRgData == null)
            {
                AudioFxRgInfoText.Text = "当前曲目：无 ReplayGain 标签";
                return;
            }

            var d = _currentRgData.Value;
            AudioFxRgInfoText.Text = $"当前曲目：Track {FormatAudioFxDb(d.TrackGainDb)} dB / Album {FormatAudioFxDb(d.AlbumGainDb)} dB / peak {d.Peak:0.###}";
        }


        /// <summary>按分类字段值取归一化键（Artist/AlbumArtist 等）。</summary>
        private static string TagSortFieldVal(PlaylistItem p, string field)
        {
            return field switch
            {
                "Artist" => string.IsNullOrWhiteSpace(p.Artist) ? "未知" : p.Artist.Trim(),
                "AlbumArtist" => string.IsNullOrWhiteSpace(p.AlbumArtist) ? "未知" : p.AlbumArtist.Trim(),
                "Album" => string.IsNullOrWhiteSpace(p.Album) ? "未知" : p.Album.Trim(),
                "Genre" => string.IsNullOrWhiteSpace(p.Genre) ? "未知" : p.Genre.Trim(),
                "Year" => p.Year > 0 ? p.Year.ToString() : "未知",
                _ => "未知"
            };
        }


        /// <summary>刷新分类墙：按 _tagSortClassField 分组 _playlist，每组分封面（首曲封面）。</summary>
        private void ShowTagSortClassWall()
        {
            TagSortPanel.Visibility = Visibility.Collapsed;
            TagSortClassScroll.Visibility = Visibility.Visible;
            var entries = _playlist
                .GroupBy(p => TagSortFieldVal(p, _tagSortClassField), StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new TagSortCategoryEntry
                {
                    Name = g.Key,
                    Count = g.Count(),
                    FirstFilePath = g.First().FilePath
                })
                .ToList();
            TagSortClassGridView.ItemsSource = entries;
            foreach (var e in entries)
            {
                _ = LoadTagSortCategoryCoverAsync(e);
            }
        }


        /// <summary>按当前面板视角（Songs/Albums/Artists/Sort）渲染内容区。</summary>
        private void ApplyTagSortPanelMode()
        {
            TagSortPanelGridView.Visibility = Visibility.Collapsed;
            TagSortPanelSongListView.Visibility = Visibility.Collapsed;
            TagSortSortPanel.Visibility = Visibility.Collapsed;
            TagSortViewModeButton.Content = _tagSortPanelMode switch
            {
                "Albums" => "专辑", "Artists" => "艺术家", "Sort" => "排序方式", _ => "曲目"
            };

            if (_tagSortPanelMode == "Albums")
            {
                var albums = _tagSortClassSongs
                    .GroupBy(p => string.IsNullOrWhiteSpace(p.Album) ? "未知" : p.Album, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new TagSortCategoryEntry { Name = g.Key, Count = g.Count(), FirstFilePath = g.First().FilePath, Sub = "Album" })
                    .ToList();
                TagSortPanelGridView.Visibility = Visibility.Visible;
                TagSortPanelGridView.ItemsSource = albums;
                foreach (var e in albums) _ = LoadTagSortCategoryCoverAsync(e);
            }
            else if (_tagSortPanelMode == "Artists")
            {
                var artists = _tagSortClassSongs
                    .GroupBy(p => string.IsNullOrWhiteSpace(p.Artist) ? "未知" : p.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new TagSortCategoryEntry { Name = g.Key, Count = g.Count(), FirstFilePath = g.First().FilePath, Sub = "Artist" })
                    .ToList();
                TagSortPanelGridView.Visibility = Visibility.Visible;
                TagSortPanelGridView.ItemsSource = artists;
                foreach (var e in artists) _ = LoadTagSortCategoryCoverAsync(e);
            }
            else if (_tagSortPanelMode == "Sort")
            {
                TagSortSortPanel.Visibility = Visibility.Visible;
                WriteTagSortStatus();
            }
            else // Songs
            {
                var songs = new ObservableCollection<PlaylistItem>();
                for (int i = 0; i < _tagSortClassSongs.Count; i++)
                {
                    _tagSortClassSongs[i].Index = i + 1;
                    songs.Add(_tagSortClassSongs[i]);
                }
                TagSortPanelSongListView.ItemsSource = songs;
                TagSortPanelSongListView.Visibility = Visibility.Visible;
            }
        }


        /// <summary>标签排序信息列表「播放当前列表歌曲」：按当前列表顺序替换播放队列并从第一首播放。</summary>
        private void TagSortPanelPlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tagSortClassSongs.Count == 0)
            {
                return;
            }

            _userPlaylist.Clear();
            AddSongsToUserPlaylist(_tagSortClassSongs.ToList());
            PlayUserPlaylistAt(0);
        }


        private void TagSortPanelSongListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistItem song)
            {
                PlayPlaylistItem(song);
            }
        }


        private void TagSortPanelSongList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(TagSortPanelSongListView, container, song);
            }
        }


        private void TagSortPanelSongList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshRealizedSongListSelectionChrome(TagSortPanelSongListView);
        }


        private void TagSortPanelSongListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode) return;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistItem song)
            {
                _multiSelectTargetList = TagSortPanelSongListView;
                var flyout = BuildPlaylistItemContextMenu(song, inUserPlaylist: false,
                    multiSelectAction: () => EnterMultiSelectModeFrom(TagSortPanelSongListView));
                flyout.ShowAt(TagSortPanelSongListView, e.GetPosition(TagSortPanelSongListView));
            }
        }


        /// <summary>取分类墙 / 面板网格一个卡片（某字段值）对应的全部曲目。
        /// 优先用 entry.Sub 指定的字段（面板专辑/艺术家视角），否则用当前分类字段。</summary>
        private List<PlaylistItem> CollectTagSortCategorySongs(TagSortCategoryEntry entry)
        {
            string field = string.IsNullOrWhiteSpace(entry.Sub) ? _tagSortClassField : entry.Sub;
            return _playlist
                .Where(p => string.Equals(TagSortFieldVal(p, field), entry.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }


        private void NavPlaylistWallButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "UserPlaylist";
                ApplyCategoryView();
            });
            ApplySwitchPlaylistPausePreference();
        }


        /// <summary>填充播放列表墙（命名单封面卡片）。</summary>
        private void ApplyPlaylistWallCategory()
        {
            _playlistWall.Clear();
            PlaylistLibraryService.Refresh();
            foreach (var p in PlaylistLibraryService.Items)
            {
                // 列表墙只显示用户真实命名单：过滤内建“我喜欢的音乐”与空列表
                if (string.Equals(p.Name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                {
                    continue;
                }

                var vm = new PlaylistCardViewModel
                {
                    Name = p.Name,
                    SongCountText = NamedPlaylistStore.LoadSongs(p.Name).Count + " 首",
                };
                _ = LoadPlaylistWallCoverAsync(vm);
                _playlistWall.Add(vm);
            }

            // 无任何命中单时显示空状态提示
            UpdatePlaylistWallEmptyHint();
        }


        private void UpdatePlaylistWallEmptyHint()
        {
            if (PlaylistWallEmptyHint == null)
            {
                return;
            }

            PlaylistWallEmptyHint.Visibility =
                _playlistWall.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }


        private async System.Threading.Tasks.Task LoadPlaylistWallCoverAsync(PlaylistCardViewModel vm)
        {
            try
            {
                // 优先从列表歌曲取首曲封面（第一首含封面的）；歌曲全无封面时才回落用户手动设的自定义封面。
                byte[]? bytes = await System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (string path in NamedPlaylistStore.LoadSongs(vm.Name))
                    {
                        if (!System.IO.File.Exists(path)) continue;
                        try
                        {
                            byte[]? b = ExtractCoverBytes(path);
                            if (b is { Length: > 0 }) return b;
                        }
                        catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                    }
                    return (byte[]?)null;
                });
                if (bytes is not { Length: > 0 })
                {
                    // 歌曲全无封面：回落到用户手动设置的自定义封面；仍无则以 Cover=null 由 View 图标兜底。
                    string? custom = PlaylistLibraryService.CustomCoverPath(vm.Name);
                    if (!string.IsNullOrWhiteSpace(custom))
                    {
                        bytes = await System.Threading.Tasks.Task.Run(() => System.IO.File.ReadAllBytes(custom));
                    }
                }
                if (bytes is not { Length: > 0 })
                {
                    return;
                }
                var bmp = await CreateBitmapFromBytesAsync(bytes); // 与专辑封面同一机制
                if (bmp != null)
                {
                    vm.Cover = bmp;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void PlaylistWallGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is PlaylistCardViewModel vm)
            {
                ShowPlaylistDetail(vm.Name);
            }
        }


        private void ShowPlaylistDetail(string name)
        {
            _currentPlaylistDetail = name;
            FillPlaylistDetailItems();
            PlaylistDetailNameText.Text = name;
            PlaylistDetailCountText.Text = _playlistDetailItems.Count + " 首";
            PlaylistDetailListView.ItemsSource = _playlistDetailItems;
            _ = LoadPlaylistDetailCoverAsync(_playlistDetailItems.FirstOrDefault()?.FilePath, name);
            ApplyCategoryView();
        }


        private void FillPlaylistDetailItems()
        {
            _playlistDetailItems.Clear();
            if (string.IsNullOrEmpty(_currentPlaylistDetail)) return;
            int ordinal = 1;
            foreach (string path in NamedPlaylistStore.LoadSongs(_currentPlaylistDetail))
            {
                if (!System.IO.File.Exists(path)) continue;
                try
                {
                    var item = CreatePlaylistItemFromPath(path);
                    item.Index = ordinal++;
                    _playlistDetailItems.Add(item);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }
        }


        private void PlaylistDetailListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            // 顺序已由 ObservableCollection 自动更新；保存新顺序回命名单
            if (!string.IsNullOrEmpty(_currentPlaylistDetail))
            {
                for (int i = 0; i < _playlistDetailItems.Count; i++)
                {
                    _playlistDetailItems[i].Index = i + 1; // 拖拽后重排连续序号
                }
                // Index 为 x:Bind OneTime 绑定，重设后需强制刷新才会更新序号
                PlaylistDetailListView.ItemsSource = null;
                PlaylistDetailListView.ItemsSource = _playlistDetailItems;
                NamedPlaylistStore.SaveSongs(_currentPlaylistDetail, _playlistDetailItems.Select(p => p.FilePath));
            }
        }


        private void PlaylistDetailSortMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string field)
            {
                SortPlaylistDetailItems(field);
            }
        }


        /// <summary>按指定字段对命名单详情排序并保存回命名单。排序后重排连续序号。</summary>
        private void SortPlaylistDetailItems(string field)
        {
            if (_playlistDetailItems.Count <= 1) return;
            try
            {
                List<PlaylistItem> sorted = field switch
                {
                    "Title" => _playlistDetailItems
                        .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Title, StringComparer.Ordinal)
                        .ToList(),
                    "Artist" => _playlistDetailItems
                        .OrderBy(p => p.Artist, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Title, StringComparer.Ordinal)
                        .ToList(),
                    "Album" => _playlistDetailItems
                        .OrderBy(p => p.Album, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.Track).ThenBy(p => p.Title, StringComparer.Ordinal).ToList(),
                    "Track" => _playlistDetailItems.OrderBy(p => p.Track).ThenBy(p => p.Title, StringComparer.Ordinal).ToList(),
                    "Year" => _playlistDetailItems.OrderBy(p => p.Year).ThenBy(p => p.Title, StringComparer.Ordinal).ToList(),
                    "Duration" => _playlistDetailItems.OrderBy(p => p.Duration).ThenBy(p => p.Title, StringComparer.Ordinal).ToList(),
                    "FilePath" => _playlistDetailItems
                        .OrderBy(p => p.FilePath, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(p => p.FilePath, StringComparer.Ordinal)
                        .ToList(),
                    _ => _playlistDetailItems.ToList()
                };

                for (int i = 0; i < sorted.Count; i++)
                {
                    sorted[i].Index = i + 1;
                }
                _playlistDetailItems.Clear();
                foreach (var p in sorted) _playlistDetailItems.Add(p);

                if (!string.IsNullOrEmpty(_currentPlaylistDetail))
                {
                    NamedPlaylistStore.SaveSongs(_currentPlaylistDetail, _playlistDetailItems.Select(p => p.FilePath));
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>从当前命名单详情删除勾选的歌曲，重排连续序号并保存回命名单。</summary>
        private void RemoveSongsFromCurrentPlaylistDetail(IEnumerable<PlaylistItem> selected)
        {
            try
            {
                var removes = selected.Select(s => s.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var keep = _playlistDetailItems.Where(p => !removes.Contains(p.FilePath)).ToList();
                _playlistDetailItems.Clear();
                for (int i = 0; i < keep.Count; i++)
                {
                    keep[i].Index = i + 1;
                    _playlistDetailItems.Add(keep[i]);
                }

                if (!string.IsNullOrEmpty(_currentPlaylistDetail))
                {
                    NamedPlaylistStore.SaveSongs(_currentPlaylistDetail, _playlistDetailItems.Select(p => p.FilePath));
                    PlaylistDetailCountText.Text = _playlistDetailItems.Count + " 首";
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private async System.Threading.Tasks.Task LoadPlaylistDetailCoverAsync(string? firstTrack, string name)
        {
            try
            {
                byte[]? bytes = null;
                string? custom = PlaylistLibraryService.CustomCoverPath(name);
                if (!string.IsNullOrWhiteSpace(custom))
                {
                    bytes = await System.Threading.Tasks.Task.Run(() => System.IO.File.ReadAllBytes(custom));
                }
                else if (!string.IsNullOrWhiteSpace(firstTrack))
                {
                    bytes = await System.Threading.Tasks.Task.Run(() => ExtractCoverBytes(firstTrack));
                }

                if (bytes is { Length: > 0 })
                {
                    var bmp = await CreateBitmapFromBytesAsync(bytes); // 与专辑封面同一机制
                    if (bmp != null)
                    {
                        PlaylistDetailCoverImage.Source = bmp;
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void PlaylistDetailBackButton_Click(object sender, RoutedEventArgs e)
        {
            _currentPlaylistDetail = null;
            ApplyCategoryView();
            ApplyPlaylistWallCategory();
        }


        private void PlaylistDetailPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentPlaylistDetail))
            {
                _ = LoadNamedPlaylistToQueueAndPlayAsync(_currentPlaylistDetail);
            }
        }


        private void PlaylistDetailAddQueueButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentPlaylistDetail))
            {
                AddNamedPlaylistToQueue(_currentPlaylistDetail);
            }
        }


        private void PlaylistDetailAddWallButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPlaylistDetail)) return;
            _ = ShowNamedPlaylistPickerAsync(_playlistDetailItems.ToList());
        }


        private void SongRow_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border chrome) return;
            var dc = (sender as FrameworkElement)?.DataContext as PlaylistItem;
            if (dc == null || IsSongInListSelected(chrome, dc)) return;
            chrome.Background = PlaylistDetailHoverBg;
        }


        private void SongRow_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border chrome) return;
            var dc = (sender as FrameworkElement)?.DataContext as PlaylistItem;
            if (dc == null || IsSongInListSelected(chrome, dc)) return;
            chrome.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }


        private bool IsSongInListSelected(DependencyObject node, PlaylistItem song)
        {
            try
            {
                ListView? list = FindAncestorListView(node);
                if (list == null) return false;
                return list.SelectedItems.Contains(song);
            }
            catch
            {
                return false;
            }
        }


        private void PlaylistDetailRow_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border chrome) return;
            var dc = (sender as FrameworkElement)?.DataContext as PlaylistItem;
            if (dc == null || PlaylistDetailRowIsSelected(dc)) return;
            chrome.Background = PlaylistDetailHoverBg;
        }


        private void PlaylistDetailRow_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border chrome) return;
            var dc = (sender as FrameworkElement)?.DataContext as PlaylistItem;
            if (dc == null || PlaylistDetailRowIsSelected(dc)) return;
            chrome.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }


        private bool PlaylistDetailRowIsSelected(PlaylistItem song)
        {
            try
            {
                return PlaylistDetailListView.SelectedItems.Contains(song);
            }
            catch
            {
                return false;
            }
        }


        private void PlaylistDetailListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(PlaylistDetailListView, container, song);
                if (!args.InRecycleQueue && args.Phase == 0)
                {
                    LoadRowCoverAsync(PlaylistDetailListView, container, song);
                }
            }
        }


        private void PlaylistDetailListView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshRealizedSongListSelectionChrome(PlaylistDetailListView);
        }


        private void PlaylistDetailListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            // 仅当实际点到歌曲行时才播放（空白区/容器双击不触发整列表播放）
            PlaylistItem? song = null;
            if (e.OriginalSource is DependencyObject source)
            {
                song = FindPlaylistItem(source);
                if (song == null)
                {
                    ListViewItem? container = FindAncestorListViewItem(source);
                    if (container != null)
                    {
                        song = PlaylistDetailListView.ItemFromContainer(container) as PlaylistItem;
                    }
                }
            }

            if (song != null && !string.IsNullOrEmpty(_currentPlaylistDetail) && !string.IsNullOrEmpty(song.FilePath))
            {
                PlayNamedPlaylistFromTrack(_currentPlaylistDetail, song.FilePath);
            }
        }


        /// <summary>命中单详情双击/指定某首先：把命名单载入当前队列并定位播放该首。</summary>
        internal void PlayNamedPlaylistFromTrack(string name, string filePath)
        {
            try
            {
                var items = new List<PlaylistItem>();
                foreach (string path in NamedPlaylistStore.LoadSongs(name))
                {
                    if (!System.IO.File.Exists(path)) continue;
                    try { items.Add(CreatePlaylistItemFromPath(path)); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                if (items.Count == 0) return;
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(items);
                int index = FindUserPlaylistIndex(filePath);
                PlayUserPlaylistAt(index >= 0 ? index : 0);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PlayNamedPlaylistFromTrack", ex);
            }
        }


        private void PlaylistDetailListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var song = (e.OriginalSource as FrameworkElement)?.DataContext as PlaylistItem;
            if (song == null) return;
            var flyout = BuildPlaylistItemContextMenu(song, false);
            (e.OriginalSource as FrameworkElement)?.DispatcherQueue.TryEnqueue(() => flyout.ShowAt(PlaylistDetailListView, e.GetPosition(PlaylistDetailListView)));
        }


        private void PlaylistWallGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            PlaylistCardViewModel? vm = (e.OriginalSource as FrameworkElement)?.DataContext as PlaylistCardViewModel;
            if (vm == null)
            {
                return;
            }

            var flyout = new MenuFlyout();

            var rename = new MenuFlyoutItem { Text = "重命名", Icon = new FontIcon { Glyph = "\uE8AC" } };
            rename.Click += async (_, _) => await RenamePlaylistFromWallAsync(vm);
            flyout.Items.Add(rename);
            var addToQueue = new MenuFlyoutItem { Text = "添加到播放队列", Icon = new FontIcon { Glyph = "\uE710" } };
            addToQueue.Click += (_, _) => AddNamedPlaylistToQueue(vm.Name);
            flyout.Items.Add(addToQueue);
            var delete = new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { Glyph = "\uE74D" } };
            delete.Click += (_, _) =>
            {
                if (!string.Equals(vm.Name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                {
                    NamedPlaylistStore.Delete(vm.Name);
                    PlaylistLibraryService.ClearCustomCover(vm.Name);
                    PlaylistLibraryService.Refresh();
                    ApplyPlaylistWallCategory();
                }
            };
            flyout.Items.Add(delete);
            var exportOne = new MenuFlyoutItem { Text = "导出（m3u8）", Icon = new FontIcon { Glyph = "\uE896" } };
            exportOne.Click += async (_, _) => await ExportOnePlaylistAsync(vm);
            flyout.Items.Add(exportOne);
            var multi = new MenuFlyoutItem { Text = "多选", Icon = new FontIcon { Glyph = "\uE8B1" } };
            multi.Click += (_, _) => EnterPlaylistWallMultiSelect(vm);
            flyout.Items.Add(multi);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var local = new MenuFlyoutItem { Text = "设置封面（本地图片…）", Icon = new FontIcon { Glyph = "\uE710" } };
            local.Click += async (_, _) => await SetPlaylistCoverFromLocalAsync(vm);
            flyout.Items.Add(local);
            var web = new MenuFlyoutItem { Text = "从网络搜索封面…", Icon = new FontIcon { Glyph = "\uE774" } };
            web.Click += async (_, _) => await SetPlaylistCoverFromWebAsync(vm);
            flyout.Items.Add(web);
            var restore = new MenuFlyoutItem { Text = "恢复默认（首曲封面）", Icon = new FontIcon { Glyph = "\uE74D" } };
            restore.Click += (_, _) =>
            {
                PlaylistLibraryService.ClearCustomCover(vm.Name);
                ApplyPlaylistWallCategory();
            };
            flyout.Items.Add(restore);

            flyout.ShowAt(PlaylistWallGridView, e.GetPosition(PlaylistWallGridView));
        }


        private async System.Threading.Tasks.Task SetPlaylistCoverFromLocalAsync(PlaylistCardViewModel vm)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                using var stream = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms);
                PlaylistLibraryService.WriteCustomCover(vm.Name, ms.ToArray());
                ApplyPlaylistWallCategory();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private async System.Threading.Tasks.Task RenamePlaylistFromWallAsync(PlaylistCardViewModel vm)
        {
            if (string.Equals(vm.Name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
            {
                return;
            }

            var box = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = "新名称", Text = vm.Name, MinWidth = 300 };
            var dlg = new ContentDialog
            {
                Title = "重命名播放列表",
                Content = box,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content?.XamlRoot,
            };
            ApplyDialogAccent(dlg);
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            string newName = box.Text?.Trim() ?? string.Empty;
            if (newName.Length == 0 || string.Equals(newName, vm.Name, StringComparison.Ordinal)) return;
            try
            {
                NamedPlaylistStore.Rename(vm.Name, newName);
            }
            catch
            {
                return;
            }

            PlaylistLibraryService.Refresh();
            ApplyPlaylistWallCategory();
        }


        private async System.Threading.Tasks.Task ExportOnePlaylistAsync(PlaylistCardViewModel vm)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            StorageFolder folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#EXTM3U");
            foreach (string s in NamedPlaylistStore.LoadSongs(vm.Name))
            {
                sb.AppendLine("#EXTINF:-1," + System.IO.Path.GetFileName(s));
                sb.AppendLine(s);
            }
            string safe = vm.Name;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            StorageFile m3uFile = await folder.CreateFileAsync(safe + ".m3u8", Windows.Storage.CreationCollisionOption.ReplaceExisting);
            await Windows.Storage.FileIO.WriteTextAsync(m3uFile, sb.ToString(), Windows.Storage.Streams.UnicodeEncoding.Utf8);
        }


        private void EnterPlaylistWallMultiSelect(PlaylistCardViewModel anchor)
        {
            PlaylistWallGridView.SelectionMode = ListViewSelectionMode.Multiple;
            PlaylistWallGridView.IsItemClickEnabled = false;
            PlaylistWallMultiBar.Visibility = Visibility.Visible;
        }


        private void ExitPlaylistWallMultiSelect()
        {
            PlaylistWallGridView.SelectionMode = ListViewSelectionMode.None;
            PlaylistWallGridView.IsItemClickEnabled = true;
            PlaylistWallMultiBar.Visibility = Visibility.Collapsed;
            PlaylistWallGridView.SelectedItems.Clear();
        }


        private void PlaylistWallMultiExitButton_Click(object sender, RoutedEventArgs e) => ExitPlaylistWallMultiSelect();

        private void PlaylistWallMultiAddToQueue_Click(object sender, RoutedEventArgs e)
        {
            var selected = PlaylistWallGridView.SelectedItems.OfType<PlaylistCardViewModel>().ToList();
            foreach (var vm in selected)
            {
                AddNamedPlaylistToQueue(vm.Name);
            }
            ExitPlaylistWallMultiSelect();
        }


        private void PlaylistWallMultiDelete_Click(object sender, RoutedEventArgs e)
        {
            var selected = PlaylistWallGridView.SelectedItems.OfType<PlaylistCardViewModel>().ToList();
            foreach (var vm in selected)
            {
                if (!string.Equals(vm.Name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                {
                    NamedPlaylistStore.Delete(vm.Name);
                    PlaylistLibraryService.ClearCustomCover(vm.Name);
                }
            }
            PlaylistLibraryService.Refresh();
            ApplyPlaylistWallCategory();
            ExitPlaylistWallMultiSelect();
        }


        private async System.Threading.Tasks.Task SetPlaylistCoverFromWebAsync(PlaylistCardViewModel vm)
        {
            try
            {
                NowPlayingText.Text = "正在从网络搜索封面…";
                string? url = await OnlineMusicApi.SearchArtistAvatarUrlAsync(vm.Name);
                if (string.IsNullOrWhiteSpace(url))
                {
                    NowPlayingText.Text = "未找到封面";
                    return;
                }

                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                byte[] bytes = await http.GetByteArrayAsync(url);
                if (bytes.Length > 0)
                {
                    PlaylistLibraryService.WriteCustomCover(vm.Name, bytes);
                }

                NowPlayingText.Text = string.Empty;
                ApplyPlaylistWallCategory();
            }
            catch
            {
                NowPlayingText.Text = "获取封面失败";
            }
        }


        private async void CreatePlaylistWallButton_Click(object sender, RoutedEventArgs e)
        {
            var box = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = "播放列表名称", MinWidth = 300 };
            var dlg = new ContentDialog
            {
                Title = "新建播放列表",
                Content = box,
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content?.XamlRoot,
            };
            ApplyDialogAccent(dlg);
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            string name = box.Text?.Trim() ?? string.Empty;
            if (name.Length == 0) return;
            try
            {
                NamedPlaylistStore.Create(name);
            }
            catch
            {
                return;
            }

            PlaylistLibraryService.Refresh();
            ApplyPlaylistWallCategory();
        }


        /// <summary>导出全部播放列表为 m3u8：每命中单写一个 .m3u8 到用户选择目录。</summary>
        private async void ExportPlaylistsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;

                int count = 0;
                foreach (string name in NamedPlaylistStore.List())
                {
                    if (string.Equals(name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
                    {
                        continue; // 内建“我喜欢的音乐”不导出为独立 m3u8（属收藏数据）
                    }

                    List<string> songs = NamedPlaylistStore.LoadSongs(name);
                    if (songs.Count == 0) continue;
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("#EXTM3U");
                    foreach (string s in songs)
                    {
                        sb.AppendLine("#EXTINF:-1," + System.IO.Path.GetFileName(s));
                        sb.AppendLine(s);
                    }
                    string safe = name;
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                    StorageFile m3uFile = await folder.CreateFileAsync(safe + ".m3u8", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                    await Windows.Storage.FileIO.WriteTextAsync(m3uFile, sb.ToString(), Windows.Storage.Streams.UnicodeEncoding.Utf8);
                    count++;
                }

                StartupLog.Write("导出播放列表完成: " + count + " 个 .m3u8 → " + folder.Path);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ExportPlaylists", ex);
            }
        }


        private async void SaveQueueToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            await SaveUserPlaylistAsync(Content.XamlRoot);
            PlaylistLibraryService.Refresh();
            ApplyPlaylistWallCategory();
        }


        /// <summary>切换播放列表时：若设置不允许继续播放，则暂停当前播放。</summary>
        private void ApplySwitchPlaylistPausePreference()
        {
            if (AppSettingsStore.Load().ContinueWhenSwitchPlaylist)
            {
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player?.Source != null && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                try
                {
                    player.Pause();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }
        }


        private void OpenGenreYearSongs(string groupName)
        {
            _genreYearFilter = groupName;
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = _currentCategory == "Genres" ? "GenreSongs" : "YearSongs";
                ApplyCategoryView();
            });
        }


        private void ApplySongsSearchFilter()
        {
            if (_currentCategory != "Songs" || PlaylistView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(PlaylistView.ItemsSource, _playlist))
                {
                    PlaylistView.ItemsSource = _playlist;
                }

                RefreshPlaylistSelectionChrome();
                return;
            }

            List<PlaylistItem> filtered = _playlist
                .Where(p =>
                    ContainsIgnoreCase(p.Title, q)
                    || ContainsIgnoreCase(p.Album, q)
                    || ContainsIgnoreCase(p.Artist, q))
                .ToList();

            PlaylistView.ItemsSource = filtered;
            RefreshPlaylistSelectionChrome();
        }


        /// <summary>播放列表搜索：标题 / 艺术家 / 专辑 / 年份。</summary>
        private void ApplyUserPlaylistSearchFilter()
        {
            if (_currentCategory != "UserPlaylist" || PlaylistView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist))
                {
                    PlaylistView.ItemsSource = _userPlaylist;
                }

                RefreshPlaylistSelectionChrome();
                return;
            }

            List<PlaylistItem> filtered = _userPlaylist
                .Where(p => MatchesPlaylistSearch(p, q))
                .ToList();

            PlaylistView.ItemsSource = filtered;
            RefreshPlaylistSelectionChrome();
        }


        internal static bool MatchesPlaylistSearch(PlaylistItem item, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            string q = query.Trim();
            return ContainsIgnoreCase(item.Title, q)
                || ContainsIgnoreCase(item.Artist, q)
                || ContainsIgnoreCase(item.Album, q)
                || ContainsIgnoreCase(item.YearText, q)
                || (item.Year > 0 && ContainsIgnoreCase(item.Year.ToString(), q));
        }


        private void FolderSearchPrevButton_Click(object sender, RoutedEventArgs e)
            => NavigateFolderSearchMatch(-1);

        private void FolderSearchNextButton_Click(object sender, RoutedEventArgs e)
            => NavigateFolderSearchMatch(+1);

        private void ApplyFolderSearch()
        {
            string q = _librarySearchText.Trim();
            _folderSearchMatches.Clear();
            _folderSearchIndex = -1;
            _folderSearchHighlightPath = null;

            if (string.IsNullOrEmpty(q)
                || string.IsNullOrWhiteSpace(_browseFolderPath)
                || !Directory.Exists(_browseFolderPath))
            {
                UpdateFolderSearchNavUi();
                RefreshFolderBrowserSelectionChrome();
                return;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(
                             _browseFolderPath,
                             "*.*",
                             SearchOption.AllDirectories))
                {
                    if (!IsSupportedAudioFile(file))
                    {
                        continue;
                    }

                    string name = Path.GetFileNameWithoutExtension(file);
                    string fullName = Path.GetFileName(file);
                    if (ContainsIgnoreCase(name, q) || ContainsIgnoreCase(fullName, q))
                    {
                        _folderSearchMatches.Add(file);
                    }
                }

                _folderSearchMatches.Sort(StringComparer.CurrentCultureIgnoreCase);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("文件夹搜索失败: " + ex.Message);
            }

            UpdateFolderSearchNavUi();

            if (_folderSearchMatches.Count > 0)
            {
                NavigateFolderSearchMatchToIndex(0);
            }
            else
            {
                RefreshFolderBrowserRoots();
                RefreshFolderBrowserSelectionChrome();
            }
        }


        /// <summary>双击文件夹或点箭头：枚举该文件夹内歌曲并填充右侧详情区。</summary>
        private void LoadMediaFolderSongs(FolderBrowserItem item)
        {
            if (MediaDetailsHeader == null || MediaDetailsList == null)
            {
                return;
            }

            MediaDetailsEmptyHint.Visibility = Visibility.Collapsed;
            MediaDetailsList.Visibility = Visibility.Visible;
            MediaDetailsHeader.Text = item.FullPath;

            // 后台线程枚举+读取标签，避免大文件夹卡 UI
            MediaDetailsList.ItemsSource = null;
            MediaDetailsHeader.Text = item.FullPath + "（加载中…）";

            string loadedPath = item.FullPath;
            bool isFolder = item.IsFolder;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                string[] paths = isFolder
                    ? EnumerateAudioFiles(loadedPath).ToArray()
                    : (System.IO.File.Exists(loadedPath) ? new[] { loadedPath } : Array.Empty<string>());

                var songs = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (!System.IO.File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
                        PlaylistItem p = CreatePlaylistItemFromPath(path);
                        songs.Add(p);
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                for (int i = 0; i < songs.Count; i++)
                {
                    songs[i].Index = i + 1;
                }

                return songs;
            }).ContinueWith(t =>
            {
                try
                {
                    var songs = t.Result;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (MediaDetailsList == null || MediaDetailsHeader == null)
                        {
                            return;
                        }

                        MediaDetailsList.ItemsSource = songs;
                        MediaDetailsList.SelectionMode = ListViewSelectionMode.None;
                        if (MediaOptionsButton != null)
                        {
                            MediaOptionsButton.Visibility = Visibility.Collapsed;
                        }

                        MediaDetailsHeader.Text = item.FullPath;
                        MediaDetailsEmptyHint.Text = "无歌曲";
                        MediaDetailsEmptyHint.Visibility =
                            songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    });
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }


        /// <summary>询问是否把歌曲移入回收站并从磁盘删除。</summary>
        private async System.Threading.Tasks.Task DeleteMediaSongWithConfirmAsync(PlaylistItem song)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "从媒体库中删除",
                    Content = $"确定要把该歌曲移动到回收站并从磁盘删除吗？\n\n{song.FilePath}",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                ApplyDialogAccent(dialog);
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await DeleteSongFromDiskAsync(song);
                    // 重新加载当前详情列表（后台）
                    if (FolderBrowserView.SelectedItem is FolderBrowserItem f)
                    {
                        LoadMediaFolderSongs(f);
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void RefreshMediaSongSelectionChrome()
        {
            if (MediaDetailsList == null)
            {
                return;
            }

            var realized = FindRealizedListViewContainers(MediaDetailsList);
            foreach ((ListViewItem c, PlaylistItem s) in realized)
            {
                ApplyMediaSongSelectionChrome(MediaDetailsList, c, s);
            }
        }


        private static List<(ListViewItem, PlaylistItem)> FindRealizedListViewContainers(ListView list)
        {
            var result = new List<(ListViewItem, PlaylistItem)>();
            try
            {
                var presenter = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(list, 0);
                void Walk(Microsoft.UI.Xaml.DependencyObject node, int depth)
                {
                    if (depth > 12)
                    {
                        return;
                    }

                    int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < n; i++)
                    {
                        var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
                        if (child is ListViewItem lvi)
                        {
                            if (lvi.Content is PlaylistItem p)
                            {
                                result.Add((lvi, p));
                            }
                        }
                        else
                        {
                            Walk(child, depth + 1);
                        }
                    }
                }

                Walk(presenter, 0);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            return result;
        }


        private void ApplyMediaSongSelectionChrome(ListView? list, ListViewItem container, PlaylistItem song)
        {
            if (list == null)
            {
                return;
            }

            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ResolveContrastingForeground(accent);
            bool selected = list.SelectionMode == ListViewSelectionMode.Multiple
                ? list.SelectedItems.Contains(song)
                : ReferenceEquals(list.SelectedItem, song);

            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            Border? chrome = FindTaggedBorder(container, "SongRowChrome");
            if (chrome != null)
            {
                chrome.MinHeight = 40;
                chrome.CornerRadius = new CornerRadius(8);
                chrome.VerticalAlignment = VerticalAlignment.Stretch;
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
                    chrome.Background = new SolidColorBrush(Colors.Transparent);
                    ClearForegroundOnDescendants(chrome);
                }
            }
        }


        /// <summary>多选删除：询问后把选中的歌曲移到回收站并从磁盘删除。</summary>
        private async System.Threading.Tasks.Task DeleteMediaSongsConfirmAsync(IReadOnlyList<PlaylistItem> songs)
        {
            try
            {
                if (songs.Count == 0)
                {
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = "从媒体库中删除",
                    Content = $"确定要把选中的 {songs.Count} 个文件移动到回收站并从磁盘删除吗？",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                ApplyDialogAccent(dialog);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                foreach (PlaylistItem s in songs)
                {
                    await DeleteSongFromDiskAsync(s);
                }

                // 重新加载当前详情列表
                if (FolderBrowserView.SelectedItem is FolderBrowserItem f)
                {
                    LoadMediaFolderSongs(f);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>把单曲加入播放队列末尾。</summary>
        private System.Threading.Tasks.Task AddToUserPlaylistBack(PlaylistItem song)
        {
            try
            {
                _userPlaylist.Add(song);
                RenumberCollection(_userPlaylist);
                if (_currentCategory == "UserPlaylist")
                {
                    PlaylistView.ItemsSource = null;
                    PlaylistView.ItemsSource = _userPlaylist;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            return System.Threading.Tasks.Task.CompletedTask;
        }


        private PlaylistItem? EnsureTrackInLibrary(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                return null;
            }

            PlaylistItem? existing = _playlist.FirstOrDefault(p =>
                string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return existing;
            }

            LoadAndAddFiles(new[] { filePath }, persistAsFiles: true);
            return _playlist.FirstOrDefault(p =>
                string.Equals(p.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        }


        private List<PlaylistItem> GetOrImportTracksByPaths(IReadOnlyList<string> paths)
        {
            var uniquePaths = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (uniquePaths.Count == 0)
            {
                return new List<PlaylistItem>();
            }

            var libraryMap = new Dictionary<string, PlaylistItem>(StringComparer.OrdinalIgnoreCase);
            foreach (PlaylistItem item in _playlist)
            {
                libraryMap.TryAdd(item.FilePath, item);
            }

            var missing = uniquePaths.Where(p => !libraryMap.ContainsKey(p) && System.IO.File.Exists(p)).ToList();
            if (missing.Count > 0)
            {
                LoadAndAddFiles(missing.ToArray(), persistAsFiles: true);
                libraryMap.Clear();
                foreach (PlaylistItem item in _playlist)
                {
                    libraryMap.TryAdd(item.FilePath, item);
                }
            }

            var result = new List<PlaylistItem>(uniquePaths.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in uniquePaths)
            {
                if (!seen.Add(path))
                {
                    continue;
                }

                if (libraryMap.TryGetValue(path, out PlaylistItem? track))
                {
                    result.Add(track);
                }
            }

            return result;
        }


        private void PlayFolderAudio(string folderPath, bool replacePlaylist)
        {
            List<string> paths = EnumerateAudioFilesRecursiveOrdered(folderPath);
            List<PlaylistItem> tracks = GetOrImportTracksByPaths(paths);
            if (tracks.Count == 0)
            {
                return;
            }

            if (replacePlaylist)
            {
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(tracks);
                PlayUserPlaylistAt(0);
            }
            else
            {
                AddSongsToUserPlaylist(tracks);
            }
        }


        private List<PlaylistItem> CollectTracksFromSelectedFolderItems(IEnumerable<FolderBrowserItem> items)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FolderBrowserItem item in items)
            {
                if (item.IsFolder)
                {
                    foreach (string path in EnumerateAudioFilesRecursiveOrdered(item.FullPath))
                    {
                        if (seen.Add(path))
                        {
                            paths.Add(path);
                        }
                    }
                }
                else if (seen.Add(item.FullPath))
                {
                    paths.Add(item.FullPath);
                }
            }

            return GetOrImportTracksByPaths(paths);
        }


        private static bool TrackMatchesArtistName(PlaylistItem track, string artistName, bool useAlbumArtist)
        {
            string key = useAlbumArtist ? track.AlbumArtist : track.Artist;
            return string.Equals(key, artistName, StringComparison.CurrentCultureIgnoreCase);
        }


        private void ApplyArtistSongsFrostChrome()        {
            if (ArtistSongsFrostPanel == null)
            {
                return;
            }

            // 歌曲列表区无边框（与专辑详情页歌曲列表一致）
            ArtistSongsFrostPanel.Background = new SolidColorBrush(Colors.Transparent);
            ArtistSongsFrostPanel.BorderThickness = new Thickness(0);
            ArtistSongsFrostPanel.CornerRadius = new CornerRadius(0);
            ArtistSongsFrostPanel.Background = null;
            ArtistSongsFrostPanel.ClearValue(Border.CornerRadiusProperty);

            if (ArtistTrackListView != null)
            {
                ArtistTrackListView.Background = new SolidColorBrush(Colors.Transparent);
                ArtistTrackListView.BorderThickness = new Thickness(0);
                ApplyAccentSelectionResources(ArtistTrackListView);
            }
        }


        private List<PlaylistItem> GetTracksForArtist(string artistName, bool useCurrentSongSort)
        {
            List<PlaylistItem> tracks = _playlist
                .Where(t => TrackMatchesArtistName(t, artistName, _artistDetailUsesAlbumArtist))
                .ToList();

            if (useCurrentSongSort
                && _openedArtist != null
                && string.Equals(_openedArtist.Name, artistName, StringComparison.CurrentCultureIgnoreCase))
            {
                return ApplyArtistSongSort(tracks);
            }

            return tracks
                .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }


        /// <summary>
        /// replace=true：清空播放列表后写入并从头播放；
        /// replace=false：按「添加至播放队列」规则插到最前。
        /// </summary>
        private void PlayArtistWorks(string artistName, bool replacePlaylist)
        {
            List<PlaylistItem> tracks = GetTracksForArtist(artistName, useCurrentSongSort: true);
            if (tracks.Count == 0)
            {
                return;
            }

            if (replacePlaylist)
            {
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(tracks);
                PlayUserPlaylistAt(0);
            }
            else
            {
                AddSongsToUserPlaylist(tracks);
            }
        }


        private void PlayArtistWorksButton_Click(object sender, RoutedEventArgs e)
        {
            if (_openedArtist != null)
            {
                PlayArtistWorks(_openedArtist.Name, replacePlaylist: true);
            }
        }
    }
}
