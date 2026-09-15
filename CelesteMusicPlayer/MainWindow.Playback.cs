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
using System.Linq;
using System.Threading.Tasks;
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

        /// <summary>上一次同步过的播放状态（null = 还没同步过，首次无条件套用）。
        /// 用于 SyncTransportPlayState 去重，避免每 200ms 都写一遍控件。</summary>
        private bool? _lastSyncedPlayingState;

        /// <summary>本次运行是否真正开始过播放（StartPlayback 里置 true）。
        /// 用来区分"用户点了播放后失败"（要弹窗）和"启动时某个陈旧源自己失败"（只记日志，不弹窗误报）。</summary>
        private bool _anyPlaybackStarted;

        /// <summary>
        /// 待应用的起始播放位置（秒）。用于「启动续播 / 就绪态下点歌词、拖进度条」：
        /// 此刻还没有播放会话（引擎未建、MediaPlayer 无源），没法直接 seek，
        /// 只能先记下来，等 PlayExtendedWithEngineAsync 起播成功后立刻定位过去。
        /// 应用后立即清零，避免影响下一首。
        /// </summary>
        private double _pendingStartSeconds;

        /// <summary>配合 _pendingStartSeconds：起播定位后是否立即暂停（点进度条且设置=跳转并暂停时使用）。</summary>
        private bool _pendingStartPauseAfter;
        /// <summary>
        /// _pendingStartSeconds 属于哪首歌。防止"就绪态记了位置、用户却点了别的歌"时把位置串到别的歌上。
        /// </summary>
        private string? _pendingStartPath;

        /// <summary>
        /// 是否正在起播（已调 StartPlayback、引擎还没真正起来）。
        /// 这段时间里引擎播的还是上一首，位置与"当前曲目"对不上 → 暂停写进度，只写队列。
        /// </summary>
        private bool _startPlaybackInFlight;

        private float[]? _waveformData;
        private string? _waveformPath;
        private string _progressBarStyle = "Gradient";
        // 主题波形强调色（ResolveAccentColor 的缓存，避免频繁解析）
        private static Color _waveAccentColor = Color.FromArgb(255, 0, 120, 212);
        private SystemMediaTransportControls? _engineSmtc;
        private long _lastSmtcTimelineMs; // SMTC timeline 限频（约 500ms 一次）
        private long _lastSmtcTimelineLogMs; // SMTC timeline 诊断日志限频（约 3s 一次）
        /// <summary>SMTC 专用宿主 MediaPlayer：挂静音循环源激活播放会话，独立于主播放器，避免污染主播放的事件/UI。</summary>
        private MediaPlayer? _smtcHost;
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

            // 页内播放条上的进度条（NowPlayingProgressSlider）必须做同样的事：
            // Slider 内部会把 PointerPressed/Released 标成 Handled，XAML 里挂（或在代码里用 +=）都收不到，
            // 结果是 _isUserSeeking 永远是 false —— 拖动时播放位置镜像会把滑块每秒改回去
            // （表现为"不跟手、松手后跳回原进度"），松手也不会真正 seek。
            if (NowPlayingProgressSlider != null)
            {
                NowPlayingProgressSlider.AddHandler(
                    UIElement.PointerPressedEvent,
                    new PointerEventHandler(NowPlayingProgressSlider_PointerPressed),
                    handledEventsToo: true);
                NowPlayingProgressSlider.AddHandler(
                    UIElement.PointerReleasedEvent,
                    new PointerEventHandler(NowPlayingProgressSlider_PointerReleased),
                    handledEventsToo: true);
                NowPlayingProgressSlider.AddHandler(
                    UIElement.PointerCaptureLostEvent,
                    new PointerEventHandler(NowPlayingProgressSlider_PointerCaptureLost),
                    handledEventsToo: true);
            }

            _positionTimer = DispatcherQueue.CreateTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(200);
            _positionTimer.Tick += PositionTimer_Tick;
            _positionTimer.Start();

            // 黑胶布局的唱片/唱臂状态轮询（内部有布局判断，非黑胶布局时等于空转一次布尔比较）
            EnsureVinylMotionTimer();

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

            // 启动带进来的外部文件（双击打开）排在曲库恢复之后播，避免和启动续播抢链路
            _ = RestoreLastLibraryThenPendingFileAsync();
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
                // 音量滑条在共享与 HiFi 独占下都可用：HiFi 下调 DAC 设备/驱动级主音量（不破坏 bit-perfect），
                // 用保存音量回填，避免切模式/重启后音量跳回默认值（此前误加 IsHiFiModeSelected 条件，
                // 导致共享模式重启后音量不回填、停在 XAML 默认 80%）。
                VolumeSlider.Value = Math.Clamp(settings.Volume, 0, 100);
                _volumeToSave = Math.Clamp(settings.Volume, 0, 100); // 启动即同步，避免退出时以旧/0 值写盘
                // 回填完成：此后（用户真实拖动等）音量变化才允许写盘。
                // 关键：XAML 里 VolumeSlider 的 Value 默认值在 InitializeComponent 时触发 ValueChanged，
                // 若不加此闸门，那个默认值（原 80）会在启动回填前抢先写盘，覆盖用户上次保存的音量。
                _volumeStartupApplied = true;
            }
            finally
            {
                _applyingSettingsVolume = false;
            }

            if (Enum.TryParse(settings.PlaybackOrder, ignoreCase: true, out PlaybackOrder order))
            {
                _orderResolver.Order = order;
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

            // 按设备记忆：应用设备后套用该设备的 DSP 配置档（无存档则跳过，不覆盖全局配置）
            ApplyDeviceDspProfileIfEnabled(deviceId);
        }


        private bool _isUpdatingNowPlayingLayout;

        /// <summary>
        /// <summary>
        /// 播放信息页：大封面尺寸随面板高度自适应；并按布局（经典 / 水面）把封面列 / 信息块 / 歌词块
        /// 摆进 NowPlayingBody 的对应单元格，不重新挂接控件。
        ///   经典：封面+信息叠在左侧居中、歌词在右半区（保持原有体验）；
        ///   水面：封面在左下居中、信息在右上、歌词在右下。
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

                // 波形高度：水面布局先算出来 —— 封面尺寸要按「水面线」反推（见下），经典固定 40。
                // 上下加高（用户反馈"上下太短"）。
                double waveformHeight = _layoutIsWater
                    ? Math.Max(110, Math.Min(paneHeight * 0.22, 170))
                    : 40;

                // 大封面：经典按面板高度 50%，上限 340、下限 240；
                // 水面布局用户要求更大封面；剧场居中要抢眼；歌词布局故意做小（当"小唱片"）。
                double coverSize = _layoutIsWater
                    ? Math.Clamp(paneHeight * 0.62, 300, 420)
                    : _layoutIsLyrics
                        // 歌词布局：封面只是左上角的一枚"小唱片"，别抢歌词的风头。
                        // 用户反馈"还是太大"→ 再缩一档（原 0.19 / 92~156）。
                        ? Math.Clamp(paneHeight * 0.13, 64, 104)
                        : _layoutIsStage
                            ? Math.Clamp(Math.Min(paneHeight * 0.58, paneWidth * 0.30), 200, 360)
                            : _layoutIsCenter
                                ? Math.Clamp(paneHeight * 0.42, 200, 320)
                                : Math.Clamp(paneHeight * 0.5, 240, 340);
                // 取整：避免非整数宽度导致封面/倒影在亚像素层面差 1px（封面倒影右侧错位）。
                coverSize = Math.Round(coverSize);
                if (_layoutIsWater)
                {
                    // 水面：封面底边被钉在「水面线」= 波形中线（见下方水面定位）。
                    // 于是封面顶边 = bodyH/2 + 波形高/2 − 封面高，必须留出余量，
                    // 否则封面会被面板顶部裁掉。bodyH 用「面板高 − 上下内边距」保守估计。
                    double bodyEstimate = Math.Max(0, paneHeight - 32);
                    double coverCap = bodyEstimate * 0.5 + waveformHeight * 0.5 - 8;
                    coverSize = Math.Round(Math.Min(coverSize, Math.Max(200, coverCap)));
                }

                NowPlayingCoverBorder.Width = coverSize;
                NowPlayingCoverBorder.Height = coverSize;
                // 亚像素对齐 + 定位去歧义：
                // 1) 封面列宽度固定 = 封面宽 → 列内水平定位不再依赖"居中"计算
                //    （否则倒影的左外边距会把 StackPanel 撑宽，封面又被重新居中，来回漂移）；
                // 2) 封面与倒影都左对齐到列，左边缘由同一个 x=0 决定 → 左右必定重合；
                // 3) 两者都开布局取整，消除小数宽度的取整差。
                if (NowPlayingCoverColumn != null)
                {
                    NowPlayingCoverColumn.Width = coverSize;
                    NowPlayingCoverColumn.UseLayoutRounding = true;
                }
                if (NowPlayingCoverBorder != null)
                {
                    NowPlayingCoverBorder.HorizontalAlignment = HorizontalAlignment.Left;
                }
                if (NowPlayingCoverReflection != null) NowPlayingCoverReflection.UseLayoutRounding = true;

                // 水面线（上移量）：封面列放进 Row1、顶对齐，并把整列往上推，
                // 使「封面底边」正好落在 Row1 顶部 + 波形高/2 = 波形中线。
                // 波形中线同时也是波形倒影的起点 → 封面倒影与波形倒影从此在同一条水平线上。
                // 这是解析解（不依赖测量），窗口任意高度都严格成立。
                double waterCoverLift = (waveformHeight * 0.5) - coverSize;

                // 波形宽度：水面布局与右侧信息列同宽（左对齐），经典与封面同宽。
                double rightColWidth = Math.Max(0, paneWidth * 0.46);
                double waveformWidth = _layoutIsWater
                    ? Math.Min(rightColWidth, 520)
                    : coverSize;

                // 播放条宽度：两种布局统一「横跨左右、内嵌在面板底部」——
                // 吃满面板宽度（扣掉左右内边距），封顶 900，避免"进度条太短"。
                double transportWidth = Math.Min(Math.Max(320, paneWidth - 48), 900);

                if (_layoutIsWater)
                {
                    WaveformCanvas.Width = waveformWidth;
                    WaveformCanvas.HorizontalAlignment = HorizontalAlignment.Left;
                    WaveformCanvas.Height = waveformHeight;
                    WaveformCanvas.Margin = new Thickness(0, 0, 0, 0);
                    WaveformCanvas.VerticalAlignment = VerticalAlignment.Top;
                }
                else
                {
                    // 经典：波形在封面正下方居中
                    WaveformCanvas.Width = waveformWidth;
                    WaveformCanvas.HorizontalAlignment = HorizontalAlignment.Center;
                    WaveformCanvas.Height = waveformHeight;
                    WaveformCanvas.Margin = new Thickness(0);
                    WaveformCanvas.VerticalAlignment = VerticalAlignment.Top;
                }

                // 播放条：两种布局都横跨面板、内嵌在底部、常显不淡出
                if (NowPlayingFloatingBar != null)
                {
                    NowPlayingFloatingBar.Width = transportWidth;
                    NowPlayingFloatingBar.MaxWidth = transportWidth;
                    NowPlayingFloatingBar.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingFloatingBar.VerticalAlignment = VerticalAlignment.Bottom;
                    NowPlayingFloatingBar.Margin = new Thickness(0, 0, 0, _layoutIsWater ? 4 : 8);
                }
                // 黑胶布局：唱盘尺寸随面板自适应（唱盘在左半区，信息与歌词在右半区）
                UpdateVinylGeometry(paneWidth, paneHeight);

                // 歌词区底部要留出的高度（内嵌播放条约 88px；水面没有歌词，留 0）。
                double transportReserve = _layoutIsWater ? 0 : 88;
                // 水面倒影的宽/高/裁剪跟随封面尺寸（窗口改变大小时必须同步，否则倒影会错位）
                UpdateNowPlayingReflectionGeometry();
                // 剧场是三栏（歌词 / 封面 / 信息），需要临时把网格切成 3 列；
                // 歌词布局是「上头一行、下面整幅歌词」，需要临时把第一行改成按内容高度（Auto）。
                UpdateNowPlayingBodyColumns(coverSize);
                UpdateNowPlayingBodyRows();

                // 文本换行最大宽度：
                //   水面   —— 跟右侧整列宽（信息靠左贴着列边）；
                //   歌词   —— 封面右侧那一整栏（小唱片在左上、信息紧挨其右）；
                //   剧场   —— 约三分之一栏宽（信息在最右栏）；
                //   居中   —— 面板宽度的 60%（整屏居中的标题，不想到处断行）；
                //   经典 / 黑胶 / 镜像 —— 面板宽度的一部分（居中排版，太宽不好看）。
                bool leftAlignInfo = _layoutIsWater || _layoutIsLyrics;
                // 歌词布局：封面列被压成「封面宽 + 24」的固定窄列，剩下的全给信息栏。
                double lyricsInfoWidth = Math.Max(180, paneWidth - coverSize - 88);
                double textMax = _layoutIsLyrics
                    ? lyricsInfoWidth
                    : _layoutIsStage
                        ? Math.Max(0, paneWidth * 0.28)
                        : _layoutIsCenter
                            ? Math.Max(0, paneWidth * 0.6)
                            : leftAlignInfo
                                ? Math.Max(0, rightColWidth)
                                : Math.Max(0, Math.Min(paneWidth * 0.42, 460));
                NowPlayingTitleText.MaxWidth = textMax;
                NowPlayingAudioInfoText.MaxWidth = textMax;
                SignalChainInfoText.MaxWidth = textMax;
                NowPlayingArtistText.MaxWidth = textMax;
                NowPlayingAlbumText.MaxWidth = textMax;
                if (NowPlayingArtistLinkButton != null)
                {
                    NowPlayingArtistLinkButton.MaxWidth = textMax;
                    NowPlayingArtistLinkButton.Width = textMax;
                }
                if (NowPlayingAlbumLinkButton != null)
                {
                    NowPlayingAlbumLinkButton.MaxWidth = textMax;
                    NowPlayingAlbumLinkButton.Width = textMax;
                }
                if (NowPlayingMetaPanel != null)
                {
                    NowPlayingMetaPanel.MaxWidth = textMax + 16;
                    NowPlayingMetaPanel.Width = double.NaN;
                    // 水面：信息左对齐、靠下；其余布局居中
                    TextAlignment infoAlign = leftAlignInfo ? TextAlignment.Left : TextAlignment.Center;
                    NowPlayingMetaPanel.HorizontalAlignment = leftAlignInfo ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                    // 只有水面需要"贴底"（信息压在波形上方）；歌词布局是顶部一行，由下面的分支改成 Top。
                    NowPlayingMetaPanel.VerticalAlignment = _layoutIsWater ? VerticalAlignment.Bottom : VerticalAlignment.Center;
                    NowPlayingTitleText.TextAlignment = infoAlign;
                    NowPlayingArtistText.TextAlignment = infoAlign;
                    NowPlayingAlbumText.TextAlignment = infoAlign;
                    NowPlayingAudioInfoText.TextAlignment = infoAlign;
                    SignalChainInfoText.TextAlignment = infoAlign;
                    // 歌手/专辑是 Button 包裹的：文本在按钮内部的对齐由 HorizontalContentAlignment 控制（默认居中），
                    // 只设 TextAlignment 不会让按钮内文本靠左，必须同时把按钮内容对齐也改成 Left。
                    HorizontalAlignment btnAlign = leftAlignInfo ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                    HorizontalAlignment btnContentAlign = leftAlignInfo ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                    NowPlayingArtistLinkButton.HorizontalAlignment = btnAlign;
                    NowPlayingArtistLinkButton.HorizontalContentAlignment = btnContentAlign;
                    NowPlayingAlbumLinkButton.HorizontalAlignment = btnAlign;
                    NowPlayingAlbumLinkButton.HorizontalContentAlignment = btnContentAlign;
                    if (NowPlayingArtistAlbumRow != null)
                    {
                        NowPlayingArtistAlbumRow.HorizontalAlignment = btnAlign;
                    }
                }

                // 歌词在歌词区宽内真正换行。
                // 歌词布局例外：歌词是主角、整幅居中显示，宽度按面板的 62% 给（封顶 720），
                // 不再跟随信息栏宽度 —— 否则一句歌词会被拉到跟窗口一样宽，反而不好读。
                double lyricMax = _layoutIsLyrics
                    ? Math.Max(240, Math.Min(paneWidth * 0.62, 720))
                    : Math.Max(0, textMax - 16);
                if (LyricsPanel != null)
                {
                    // 每行是 Grid → Border（圆角框）→ 歌词 TextBlock，
                    // 所以换行宽度要下钻两层；Border 左右各 14 的内边距也要扣掉，否则长句会顶到圆角边上。
                    double rowTextMax = Math.Max(160, lyricMax - 28);
                    foreach (var child in LyricsPanel.Children)
                    {
                        if (child is Microsoft.UI.Xaml.Controls.TextBlock tb)
                        {
                            tb.MaxWidth = lyricMax;
                        }
                        else if (child is Grid lyricRow)
                        {
                            foreach (var inner in lyricRow.Children)
                            {
                                if (inner is Border frame && frame.Child is Microsoft.UI.Xaml.Controls.TextBlock rowText)
                                {
                                    rowText.MaxWidth = rowTextMax;
                                }
                            }
                        }
                    }
                }

                // 旧的「位移 1/4 中轴」变换不再使用：用 Grid 单元格定位，变换归零避免残留偏移。
                if (LyricsShift != null)
                {
                    LyricsShift.TranslateX = 0;
                }

                // 按布局把各块摆进 NowPlayingBody 的单元格（默认 2 列 × 2 行，剧场临时 3 列）。
                // 先把跨列/跨行归位：上一轮布局可能把某块设成跨两列（如「居中」的封面与信息），
                // 不重置的话切到其它布局会残留跨列，位置全乱。
                Grid.SetColumnSpan(NowPlayingCoverColumn, 1);
                Grid.SetRowSpan(NowPlayingCoverColumn, 1);
                Grid.SetColumnSpan(NowPlayingMetaPanel, 1);
                Grid.SetRowSpan(NowPlayingMetaPanel, 1);
                Grid.SetColumnSpan(LyricsSection, 1);
                Grid.SetRowSpan(LyricsSection, 1);
                Grid.SetColumnSpan(WaveformCanvas, 1);
                Grid.SetRowSpan(WaveformCanvas, 1);

                if (_layoutIsWater)
                {
                    // 封面列：放进 Row1、顶对齐，再用负 Margin 把整列上推 waterCoverLift，
                    // 使封面底边正好落在波形中线上（= 水面线）→ 封面倒影与波形倒影同一条水平线。
                    // 用负 Margin 而不是 Translation：让布局层就知道真实位置，避免与其他元素打架。
                    Grid.SetColumn(NowPlayingCoverColumn, 0);
                    Grid.SetRow(NowPlayingCoverColumn, 1);
                    Grid.SetRowSpan(NowPlayingCoverColumn, 1);
                    NowPlayingCoverColumn.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingCoverColumn.VerticalAlignment = VerticalAlignment.Top;
                    NowPlayingCoverColumn.Margin = new Thickness(0, waterCoverLift, 0, 0);

                    // 信息块：右上半，靠下、左对齐（波形在下方）
                    Grid.SetColumn(NowPlayingMetaPanel, 1);
                    Grid.SetRow(NowPlayingMetaPanel, 0);
                    Grid.SetRowSpan(NowPlayingMetaPanel, 1);
                    Grid.SetColumnSpan(NowPlayingMetaPanel, 1);
                    NowPlayingMetaPanel.HorizontalAlignment = HorizontalAlignment.Left;
                    NowPlayingMetaPanel.VerticalAlignment = VerticalAlignment.Bottom;
                    NowPlayingMetaPanel.Margin = new Thickness(0);

                    // 歌词块：水面布局内不显示
                    LyricsSection.Visibility = Visibility.Collapsed;

                    // 波形：右下，左端与上方信息块左端对齐（对齐/宽度已在上面统一设置，这里只定位单元格）
                    Grid.SetColumn(WaveformCanvas, 1);
                    Grid.SetRow(WaveformCanvas, 1);
                    Grid.SetColumnSpan(WaveformCanvas, 1);

                    // 播放条：与经典布局一致 —— 横跨左右两列、内嵌在面板底部
                    Grid.SetColumn(NowPlayingFloatingBar, 0);
                    Grid.SetRow(NowPlayingFloatingBar, 1);
                    Grid.SetColumnSpan(NowPlayingFloatingBar, 2);
                    NowPlayingFloatingBar.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingFloatingBar.VerticalAlignment = VerticalAlignment.Bottom;
                }
                else if (_layoutIsVinyl)
                {
                    // 黑胶：唱盘占左半区，歌曲信息在右上，歌词在右下，波形已隐藏。
                    // 用户反馈「封面 / 信息 / 歌词都太靠下」→ 三块统一上移：
                    // 唱盘居中后底部多留 96（等效上移 48），信息从「Row0 贴底」改成「Row0 居中」，
                    // 歌词在播放条预留高度之上再多抬 32。
                    if (VinylStage != null)
                    {
                        Grid.SetColumn(VinylStage, 0);
                        Grid.SetRow(VinylStage, 0);
                        Grid.SetRowSpan(VinylStage, 2);
                        VinylStage.HorizontalAlignment = HorizontalAlignment.Center;
                        VinylStage.VerticalAlignment = VerticalAlignment.Center;
                        VinylStage.Margin = new Thickness(0, 0, 0, 96);
                    }

                    Grid.SetColumn(NowPlayingMetaPanel, 1);
                    Grid.SetRow(NowPlayingMetaPanel, 0);
                    Grid.SetRowSpan(NowPlayingMetaPanel, 1);
                    NowPlayingMetaPanel.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingMetaPanel.VerticalAlignment = VerticalAlignment.Center;
                    NowPlayingMetaPanel.Margin = new Thickness(0, 0, 0, 8);

                    Grid.SetColumn(LyricsSection, 1);
                    Grid.SetRow(LyricsSection, 1);
                    Grid.SetRowSpan(LyricsSection, 1);
                    LyricsSection.HorizontalAlignment = HorizontalAlignment.Center;
                    LyricsSection.VerticalAlignment = VerticalAlignment.Center;
                    // 底部留出播放条高度 + 额外 32 → 歌词既不会压进度条，整体也抬上来了
                    LyricsSection.Margin = new Thickness(0, 0, 0, transportReserve + 32);
                    LyricsSection.Visibility = Visibility.Visible;

                    // 播放条：横跨左右两列、内嵌在面板底部
                    Grid.SetColumn(NowPlayingFloatingBar, 0);
                    Grid.SetRow(NowPlayingFloatingBar, 1);
                    Grid.SetColumnSpan(NowPlayingFloatingBar, 2);
                    NowPlayingFloatingBar.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingFloatingBar.VerticalAlignment = VerticalAlignment.Bottom;
                }
                else if (_layoutIsStage)
                {
                    // 剧场（三栏）：左歌词 / 中封面 + 频谱 / 右信息。
                    // 封面成为视觉中心，左边是"音乐在唱什么"，右边是"这是哪张唱片"。
                    Grid.SetColumn(LyricsSection, 0);
                    Grid.SetRow(LyricsSection, 0);
                    Grid.SetRowSpan(LyricsSection, 2);
                    LyricsSection.HorizontalAlignment = HorizontalAlignment.Center;
                    LyricsSection.VerticalAlignment = VerticalAlignment.Center;
                    LyricsSection.Margin = new Thickness(0, 0, 0, transportReserve);
                    LyricsSection.Visibility = Visibility.Visible;

                    Grid.SetColumn(NowPlayingCoverColumn, 1);
                    Grid.SetRow(NowPlayingCoverColumn, 0);
                    Grid.SetRowSpan(NowPlayingCoverColumn, 1);
                    NowPlayingCoverColumn.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingCoverColumn.VerticalAlignment = VerticalAlignment.Bottom;
                    NowPlayingCoverColumn.Margin = new Thickness(0, 0, 0, 12);

                    Grid.SetColumn(WaveformCanvas, 1);
                    Grid.SetRow(WaveformCanvas, 1);
                    Grid.SetColumnSpan(WaveformCanvas, 1);
                    WaveformCanvas.HorizontalAlignment = HorizontalAlignment.Center;
                    WaveformCanvas.VerticalAlignment = VerticalAlignment.Top;

                    Grid.SetColumn(NowPlayingMetaPanel, 2);
                    Grid.SetRow(NowPlayingMetaPanel, 0);
                    Grid.SetRowSpan(NowPlayingMetaPanel, 2);
                    NowPlayingMetaPanel.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingMetaPanel.VerticalAlignment = VerticalAlignment.Center;
                    NowPlayingMetaPanel.Margin = new Thickness(0, 0, 0, transportReserve);

                    PlayBarFullWidth(3);
                }
                else if (_layoutIsLyrics)
                {
                    // 歌词（2026-09-16 改版）：
                    //   左上角 = 一枚小唱片；它右边同一行 = 歌曲信息（左对齐、跟封面顶部齐平）；
                    //   下面整幅 = 歌词，水平垂直都居中 —— 歌词是这个布局唯一的主角。
                    Grid.SetColumn(NowPlayingCoverColumn, 0);
                    Grid.SetRow(NowPlayingCoverColumn, 0);
                    Grid.SetRowSpan(NowPlayingCoverColumn, 1);
                    NowPlayingCoverColumn.HorizontalAlignment = HorizontalAlignment.Left;
                    NowPlayingCoverColumn.VerticalAlignment = VerticalAlignment.Top;
                    NowPlayingCoverColumn.Margin = new Thickness(16, 14, 0, 0);

                    // 信息：封面右侧，顶部与封面对齐（左列是「封面宽 + 24」的窄列，这里自然紧贴）
                    Grid.SetColumn(NowPlayingMetaPanel, 1);
                    Grid.SetRow(NowPlayingMetaPanel, 0);
                    Grid.SetRowSpan(NowPlayingMetaPanel, 1);
                    NowPlayingMetaPanel.HorizontalAlignment = HorizontalAlignment.Left;
                    NowPlayingMetaPanel.VerticalAlignment = VerticalAlignment.Top;
                    NowPlayingMetaPanel.Margin = new Thickness(20, 14, 0, 12);

                    // 歌词：占满下方整幅，居中
                    Grid.SetColumn(LyricsSection, 0);
                    Grid.SetRow(LyricsSection, 1);
                    Grid.SetColumnSpan(LyricsSection, 2);
                    Grid.SetRowSpan(LyricsSection, 1);
                    LyricsSection.HorizontalAlignment = HorizontalAlignment.Center;
                    LyricsSection.VerticalAlignment = VerticalAlignment.Center;
                    LyricsSection.Margin = new Thickness(0, 0, 0, transportReserve);
                    LyricsSection.Visibility = Visibility.Visible;

                    PlayBarFullWidth(2);
                }
                else if (_layoutIsMirror)
                {
                    // 镜像：把经典整体左右翻过来 —— 歌词在左，封面 + 频谱 + 信息在右。
                    // 视觉重心从左边挪到右边，背景图主体偏左时特别耐看。
                    Grid.SetColumn(LyricsSection, 0);
                    Grid.SetRow(LyricsSection, 0);
                    Grid.SetRowSpan(LyricsSection, 2);
                    LyricsSection.HorizontalAlignment = HorizontalAlignment.Center;
                    LyricsSection.VerticalAlignment = VerticalAlignment.Center;
                    LyricsSection.Margin = new Thickness(0, 0, 0, transportReserve);
                    LyricsSection.Visibility = Visibility.Visible;

                    Grid.SetColumn(NowPlayingCoverColumn, 1);
                    Grid.SetRow(NowPlayingCoverColumn, 0);
                    Grid.SetRowSpan(NowPlayingCoverColumn, 1);
                    NowPlayingCoverColumn.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingCoverColumn.VerticalAlignment = VerticalAlignment.Bottom;
                    NowPlayingCoverColumn.Margin = new Thickness(0, 0, 0, 12);

                    Grid.SetColumn(WaveformCanvas, 1);
                    Grid.SetRow(WaveformCanvas, 1);
                    Grid.SetColumnSpan(WaveformCanvas, 1);
                    WaveformCanvas.HorizontalAlignment = HorizontalAlignment.Center;
                    WaveformCanvas.VerticalAlignment = VerticalAlignment.Top;

                    Grid.SetColumn(NowPlayingMetaPanel, 1);
                    Grid.SetRow(NowPlayingMetaPanel, 1);
                    Grid.SetRowSpan(NowPlayingMetaPanel, 1);
                    NowPlayingMetaPanel.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingMetaPanel.VerticalAlignment = VerticalAlignment.Top;
                    NowPlayingMetaPanel.Margin = new Thickness(0, waveformHeight + 12, 0, transportReserve);

                    PlayBarFullWidth(2);
                }
                else if (_layoutIsCenter)
                {
                    // 居中（上下结构）：封面在上居中最显眼，信息、歌词依次居中往下。
                    // 窗口缩放时最稳的一种，也不用担心左右两栏互相挤。
                    Grid.SetColumn(NowPlayingCoverColumn, 0);
                    Grid.SetRow(NowPlayingCoverColumn, 0);
                    Grid.SetColumnSpan(NowPlayingCoverColumn, 2);
                    Grid.SetRowSpan(NowPlayingCoverColumn, 1);
                    NowPlayingCoverColumn.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingCoverColumn.VerticalAlignment = VerticalAlignment.Bottom;
                    NowPlayingCoverColumn.Margin = new Thickness(0, 0, 0, 8);

                    Grid.SetColumn(NowPlayingMetaPanel, 0);
                    Grid.SetRow(NowPlayingMetaPanel, 1);
                    Grid.SetColumnSpan(NowPlayingMetaPanel, 2);
                    Grid.SetRowSpan(NowPlayingMetaPanel, 1);
                    NowPlayingMetaPanel.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingMetaPanel.VerticalAlignment = VerticalAlignment.Top;
                    NowPlayingMetaPanel.Margin = new Thickness(0, 4, 0, 0);

                    // 歌词紧跟信息块下方：用信息块的实测高度推开，避免写死数字导致重叠
                    // （首次布局时 ActualHeight 可能还是 0，给个 96 的兜底值）。
                    double metaHeight = NowPlayingMetaPanel != null && NowPlayingMetaPanel.ActualHeight > 0
                        ? NowPlayingMetaPanel.ActualHeight
                        : 96;
                    Grid.SetColumn(LyricsSection, 0);
                    Grid.SetRow(LyricsSection, 1);
                    Grid.SetColumnSpan(LyricsSection, 2);
                    Grid.SetRowSpan(LyricsSection, 1);
                    LyricsSection.HorizontalAlignment = HorizontalAlignment.Center;
                    LyricsSection.VerticalAlignment = VerticalAlignment.Top;
                    LyricsSection.Margin = new Thickness(0, metaHeight + 16, 0, transportReserve);
                    LyricsSection.Visibility = Visibility.Visible;

                    PlayBarFullWidth(2);
                }
                else
                {
                    // 经典：封面贴在左半区上半的底部，波形紧跟其下，信息块再紧跟波形下方
                    // —— 三块贴成一组，修掉"信息离封面/波形太远、中间一大片空"的问题。
                    Grid.SetColumn(NowPlayingCoverColumn, 0);
                    Grid.SetRow(NowPlayingCoverColumn, 0);
                    Grid.SetRowSpan(NowPlayingCoverColumn, 1);
                    NowPlayingCoverColumn.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingCoverColumn.VerticalAlignment = VerticalAlignment.Bottom;
                    // 底边留 12px：封面与下方波形之间有一点呼吸感（原来贴死，波形紧贴封面底边）
                    NowPlayingCoverColumn.Margin = new Thickness(0, 0, 0, 12);

                    Grid.SetColumn(NowPlayingMetaPanel, 0);
                    Grid.SetRow(NowPlayingMetaPanel, 1);
                    Grid.SetRowSpan(NowPlayingMetaPanel, 1);
                    NowPlayingMetaPanel.HorizontalAlignment = HorizontalAlignment.Center;
                    // 顶对齐 + 下移"波形高 + 12" → 紧贴波形下方（原来贴到 Row1 底部，中间空一大截）
                    NowPlayingMetaPanel.VerticalAlignment = VerticalAlignment.Top;
                    // 底部也留出播放条占位，防止信息过长时钻到内嵌播放条下面
                    NowPlayingMetaPanel.Margin = new Thickness(0, waveformHeight + 12, 0, transportReserve);

                    Grid.SetColumn(LyricsSection, 1);
                    Grid.SetRow(LyricsSection, 0);
                    Grid.SetRowSpan(LyricsSection, 2);
                    LyricsSection.HorizontalAlignment = HorizontalAlignment.Center;
                    LyricsSection.VerticalAlignment = VerticalAlignment.Center;
                    // 歌词区底部留出播放条高度 → 歌词不会压在进度条上
                    LyricsSection.Margin = new Thickness(0, 0, 0, transportReserve);
                    LyricsSection.Visibility = Visibility.Visible;

                    // 波形：左下，封面列下方
                    Grid.SetColumn(WaveformCanvas, 0);
                    Grid.SetRow(WaveformCanvas, 1);
                    Grid.SetColumnSpan(WaveformCanvas, 1);
                    WaveformCanvas.HorizontalAlignment = HorizontalAlignment.Center;
                    WaveformCanvas.VerticalAlignment = VerticalAlignment.Top;

                    // 播放条：内嵌在面板底部，横跨左右两列居中
                    Grid.SetColumn(NowPlayingFloatingBar, 0);
                    Grid.SetRow(NowPlayingFloatingBar, 1);
                    Grid.SetColumnSpan(NowPlayingFloatingBar, 2);
                    NowPlayingFloatingBar.HorizontalAlignment = HorizontalAlignment.Center;
                    NowPlayingFloatingBar.VerticalAlignment = VerticalAlignment.Bottom;
                }
            }
            finally
            {
                _isUpdatingNowPlayingLayout = false;
            }
        }

        /// <summary>
        /// 播放页主体网格的列数：默认 2 列；「剧场」是三栏（歌词 / 封面 / 信息），临时切成 3 列。
        /// 第三列在 XAML 里宽度为 0（平时完全不占位），只在剧场布局启用，切回其它布局自动收掉。
        /// 「歌词」布局把第一列压成「封面宽 + 24」的窄列，剩下的全给第二列（封面在左、信息紧挨其右）。
        /// </summary>
        private void UpdateNowPlayingBodyColumns(double coverSize = 0)
        {
            if (NowPlayingBody?.ColumnDefinitions == null || NowPlayingBody.ColumnDefinitions.Count < 3)
            {
                return;
            }

            var cols = NowPlayingBody.ColumnDefinitions;
            if (_layoutIsStage)
            {
                // 中间那栏（封面）稍宽一点，让封面真正成为视觉中心
                cols[0].Width = new GridLength(1.05, GridUnitType.Star);
                cols[1].Width = new GridLength(1.30, GridUnitType.Star);
                cols[2].Width = new GridLength(1.05, GridUnitType.Star);
            }
            else if (_layoutIsLyrics)
            {
                // 左列只放那枚小唱片（封面宽 + 一点间距），右列吃满剩下的宽度放信息。
                cols[0].Width = new GridLength(coverSize + 24);
                cols[1].Width = new GridLength(1, GridUnitType.Star);
                cols[2].Width = new GridLength(0);
            }
            else
            {
                cols[0].Width = new GridLength(1, GridUnitType.Star);
                cols[1].Width = new GridLength(1, GridUnitType.Star);
                cols[2].Width = new GridLength(0);
            }
        }

        /// <summary>
        /// 播放页主体网格的行：默认上下两行等分（Star/Star）。
        /// 「歌词」布局改成「上行按内容高度（Auto，只放小唱片 + 信息）/ 下行吃满剩余（整幅歌词）」，
        /// 否则上面那一行会白白占掉半屏，歌词被挤到很窄的一条里。
        /// </summary>
        private void UpdateNowPlayingBodyRows()
        {
            if (NowPlayingBody?.RowDefinitions == null || NowPlayingBody.RowDefinitions.Count < 2)
            {
                return;
            }

            var rows = NowPlayingBody.RowDefinitions;
            if (_layoutIsLyrics)
            {
                rows[0].Height = GridLength.Auto;
                rows[1].Height = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                rows[0].Height = new GridLength(1, GridUnitType.Star);
                rows[1].Height = new GridLength(1, GridUnitType.Star);
            }
        }

        /// <summary>把内嵌播放条铺在面板底部，横跨指定的列数（剧场 3 列，其余 2 列）。</summary>
        private void PlayBarFullWidth(int columnSpan)
        {
            if (NowPlayingFloatingBar == null)
            {
                return;
            }

            Grid.SetColumn(NowPlayingFloatingBar, 0);
            Grid.SetRow(NowPlayingFloatingBar, 1);
            Grid.SetColumnSpan(NowPlayingFloatingBar, columnSpan);
            NowPlayingFloatingBar.HorizontalAlignment = HorizontalAlignment.Center;
            NowPlayingFloatingBar.VerticalAlignment = VerticalAlignment.Bottom;
        }

        /// <summary>
        /// 当前是否"真的在出声"。引擎优先、其次 MediaPlayer —— 两条播放路径都问一遍，
        /// 避免只问一条导致状态判断反了。
        /// </summary>
        private bool IsPlaybackActuallyPlaying()
        {
            try
            {
                if (_audioEngine != null && (_usingEnginePlayback || _audioEngine.IsPlaying))
                {
                    return _audioEngine.IsPlaying && !_isEnginePaused;
                }

                var session = GetPlayer()?.PlaybackSession;
                return session != null && session.PlaybackState == MediaPlaybackState.Playing;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把「主播放按钮 / 页内播放按钮 / 任务栏按钮」的图标同步到真实播放状态。
        /// 为什么需要它：引擎播放（HiFi / 独占 / ASIO）与 MediaPlayer 是两条路，
        /// 播放状态变更事件覆盖不全 —— 自动切下一首、暂停后恢复、独占重建会话等都可能漏掉事件，
        /// 于是偶尔会出现"歌在响、按钮却还是 ▶"的情况。
        /// 这里由 200ms 的位置定时器兜一次底，只在状态真的变了才写控件，开销可忽略。
        /// </summary>
        private void SyncTransportPlayState()
        {
            bool playing = IsPlaybackActuallyPlaying();
            if (_lastSyncedPlayingState == playing)
            {
                return;
            }

            _lastSyncedPlayingState = playing;
            string glyph = playing ? "\uE769" : "\uE768";   // E769 = 暂停，E768 = 播放
            if (PlayPauseIcon != null)
            {
                PlayPauseIcon.Glyph = glyph;
            }
            if (FloatingPlayPauseIcon != null)
            {
                FloatingPlayPauseIcon.Glyph = glyph;
            }

            try
            {
                _taskbarButtons?.UpdatePlayPause(playing);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.SyncTransportPlayState", caught);
            }
        }

        /// <summary>信息卡：更深毛玻璃 + 阴影立体感</summary>
        private void ApplyNowPlayingCardChrome()
        {
            if (NowPlayingPane != null)
            {
                // 无外框：透明背景、无圆角（右侧面板不显示独立边框）。
                // ⚠️ 必须用"透明画刷"而不是 null：WinUI 里 Background=null 的元素不参与命中测试，
                // 点击会直接漏到它下面的音乐库界面（表现为播放页盖在上面，却能点到下面的「排序」）。
                // 透明画刷视觉上完全一样，但会正常拦住鼠标。
                NowPlayingPane.Background = new SolidColorBrush(Colors.Transparent);
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

                // 优先恢复整张播放队列（关闭再开仍在）
                PlayQueueState? queue = PlayQueueStore.TryLoad();
                if (queue != null && queue.Paths.Count > 0)
                {
                    var rebuilt = new List<PlaylistItem>(queue.Paths.Count);
                    foreach (string p in queue.Paths)
                    {
                        if (string.IsNullOrWhiteSpace(p) || !System.IO.File.Exists(p))
                        {
                            continue;
                        }

                        int libIdx = FindLibraryIndex(p);
                        rebuilt.Add(libIdx >= 0 ? _playlist[libIdx] : CreatePlaylistItemFromPath(p));
                    }

                    if (rebuilt.Count > 0)
                    {
                        for (int i = 0; i < rebuilt.Count; i++)
                        {
                            rebuilt[i].Index = i + 1;
                        }

                        _userPlaylist = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>(rebuilt);
                        if (ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist) == false)
                        {
                            PlaylistView.ItemsSource = _userPlaylist;
                        }

                        int idx = (queue.CurrentIndex >= 0 && queue.CurrentIndex < _userPlaylist.Count)
                            ? queue.CurrentIndex : 0;
                        await PrepareTrackPausedAsync(idx);
                        return;
                    }
                }

                // 兼容旧版：仅记住了单曲
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

                await PrepareTrackPausedAsync(userIdx);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复播放曲目失败: {ex.Message}");
            }
        }


        private async Task PrepareTrackPausedAsync(int userPlaylistIndex)
        {
            if (userPlaylistIndex < 0 || userPlaylistIndex >= _userPlaylist.Count)
            {
                return;
            }

            PlaylistItem item = _userPlaylist[userPlaylistIndex];
            _userPlaylistIndex = userPlaylistIndex;
            _currentIndex = FindLibraryIndex(item.FilePath);

            // ★ 记住上次听到哪儿：存档里存了退出时的进度，点播放时从这里接着播。
            // 以前这里只恢复"是哪一首"，位置被丢掉 → 每次启动都从头开始。
            _pendingStartSeconds = ReadSavedPositionSeconds(item.FilePath, item.Duration);
            _pendingStartPauseAfter = false;
            _pendingStartPath = _pendingStartSeconds > 0 ? item.FilePath : null;

            NowPlayingText.Text = "已就绪：" + item.Title + " - " + item.Artist;

            // 就绪态也要把进度条/总时长/波形准备好：否则进度条停在默认的 0~100，
            // 总时长显示 00:00，点播放前拖不了进度条，波形也是空的。
            PreparePausedProgressUi(item);
            HighlightUserPlaylistItem(userPlaylistIndex, item);
            // 收藏（心形）状态：以前只在真正开播后才同步，启动恢复的那首会显示成未收藏
            UpdateFavoriteButtonUi();

            await UpdateNowPlayingPanelAsync(item);

            // 启动续播时同步写入 SMTC（状态=暂停/就绪），让系统媒体浮窗在启动后即显示歌名/歌手/封面，
            // 与双击播放后的显示保持一致；否则启动恢复上次播放时浮窗只剩程序图标。
            ConfigureEngineSmtc(item, playing: false);
            // 顺带把时长/进度写进系统时间轴，这样系统浮窗能显示总时长并可以拖定位
            // （就绪态引擎还没建，时长从曲目标签来，所以显式传进去）。
            UpdateSmtcTimeline(TimeSpan.FromSeconds(_pendingStartSeconds), item.Duration);

            // 「启动后自动播放」：走引擎路径（与双击播放一致），这样 DSP 链、实时电平表、
            // SMTC 播放状态/进度才会全部正常。此前这里走 MediaPlayer 的 player.Play()，
            // 而 MediaPlayer 路径没有 DSP 链 → LevelMeterChannels 恒 0 → 电平表不显示，
            // 且 SMTC 状态也不会更新（与之前启动续播 SMTC 卡「暂停」同源）。
            if (AppSettingsStore.Load().AutoPlayWhenStart)
            {
                StartPlayback(item);
                NotifyCurrentPlaylistWindow();
                _miniPlayerWindow?.RefreshFromOwner();
                return;
            }

            // 扩展格式（APE/WavPack 等）：系统 Media Foundation 无法解码，启动时不预加载，
            // 避免触发 MediaFailed 弹窗；点击播放时由 FFmpeg 引擎转码播放。
            if (AudioPlaybackEngine.NeedsFfmpeg(item.FilePath) || SacdIsoExtractor.IsSacdIso(item.FilePath))
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

            // 不再把歌曲预加载进 MediaPlayer：Media Foundation 认不出的格式（DSD、部分高规格
            // WAV/FLAC 等）会立刻触发 MediaFailed → 弹「无法播放 SourceNotSupported」，
            // 而双击播放走引擎路径一切正常 —— 这就是启动报错、双击却能播的原因。
            // 就绪态的 SMTC 展示已由 ConfigureEngineSmtc 负责；点播放按钮由
            // PlayPauseButton_Click 转走引擎路径（按 _userPlaylistIndex 恢复到这首）。
            NotifyCurrentPlaylistWindow();
            _miniPlayerWindow?.RefreshFromOwner();
        }


        /// <summary>
        /// 读上次存档里这首歌听到哪儿了。文件不匹配 / 位置太小 / 已经听到底都当作从头开始。
        /// </summary>
        private static double ReadSavedPositionSeconds(string filePath, TimeSpan duration)
        {
            try
            {
                PlaybackSessionState? saved = PlaybackSessionStore.TryLoad();
                if (saved == null
                    || string.IsNullOrWhiteSpace(saved.FilePath)
                    || !string.Equals(saved.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                double pos = saved.PositionSeconds;
                if (pos <= 1)
                {
                    return 0;
                }

                // 上次已经听到底了（离结尾不到 2 秒）→ 从头开始，否则一点播放就立刻切下一首。
                if (duration.TotalSeconds > 1 && pos > duration.TotalSeconds - 2)
                {
                    return 0;
                }

                return pos;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Playback.cs", caught); }
            return 0;
        }


        /// <summary>
        /// 就绪态（还没开播）也要把进度条、总时长、波形准备好。
        /// 以前这些只在真正开播时才做 → 启动后进度条是默认的 0~100、总时长 00:00、拖了也没用。
        /// </summary>
        private void PreparePausedProgressUi(PlaylistItem item)
        {
            try
            {
                _progressBarStyle = AppSettingsStore.Load().ProgressBarStyle;

                double duration = item.Duration.TotalSeconds;
                if (duration > 1)
                {
                    ProgressSlider.Maximum = duration;
                    TotalTimeText.Text = FormatTime(item.Duration);
                }

                double start = Math.Clamp(_pendingStartSeconds, 0, duration > 1 ? duration : 0);
                _isUpdatingProgressUi = true;
                try
                {
                    ProgressSlider.Value = start;
                }
                finally
                {
                    _isUpdatingProgressUi = false;
                }

                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(start));
                RedrawProgressStyle();

                // 波形：就绪态也解码一份，别等到点播放才出现
                _waveformPath = null;
                LoadWaveformForCurrentAsync(item.FilePath);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Playback.cs", caught); }
        }


        /// <summary>就绪态下把播放列表里的当前项高亮并滚动到可见位置（与真正开播时保持一致）。</summary>
        private void HighlightUserPlaylistItem(int userPlaylistIndex, PlaylistItem item)
        {
            try
            {
                if (_isMultiSelectMode)
                {
                    return;
                }

                if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal)
                    && ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist)
                    && userPlaylistIndex >= 0
                    && userPlaylistIndex < _userPlaylist.Count)
                {
                    PlaylistView.SelectedIndex = userPlaylistIndex;
                    PlaylistView.ScrollIntoView(item);
                }
                else if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                    && _currentIndex >= 0
                    && _playlist.Count > _currentIndex
                    && ReferenceEquals(PlaylistView.ItemsSource, _playlist))
                {
                    PlaylistView.SelectedIndex = _currentIndex;
                    PlaylistView.ScrollIntoView(_playlist[_currentIndex]);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Playback.cs", caught); }
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
                if (item != null)
                {
                    // 只有真正播过（正在播 / 暂停着 / MediaPlayer 有源）才写进度。
                    // 启动后一直没点播放就关掉程序时，GetLivePositionSeconds() 恒为 0，
                    // 会把上次记住的进度覆盖成 0 —— 这是"续播失效"的元凶之一。
                    // 起播途中（引擎还播着上一首）也不写，否则会把上一首的进度记到新歌头上。
                    MediaPlayer? mp = GetPlayer();
                    bool hasSession = !_startPlaybackInFlight
                                      && ((_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
                                          || (mp != null && mp.Source != null));
                    if (hasSession)
                    {
                        PlaybackSessionStore.Save(item.FilePath, GetLivePositionSeconds());
                    }
                }

                SavePlayQueue();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>取当前播放进度（秒）：优先引擎（FFmpeg/NAudio 路径），回退 MediaPlayer。</summary>
        private double GetLivePositionSeconds()
        {
            try
            {
                if (_audioEngine != null && (_audioEngine.IsPlaying || _isEnginePaused))
                {
                    return _audioEngine.Position.TotalSeconds;
                }

                MediaPlayer? mp = GetPlayer();
                if (mp?.Source != null)
                {
                    return mp.PlaybackSession.Position.TotalSeconds;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Playback.cs", caught); }
            return 0;
        }


        private void SavePlayQueue()
        {
            try
            {
                if (_userPlaylist.Count == 0)
                {
                    PlayQueueStore.Clear();
                    return;
                }

                var state = new PlayQueueState
                {
                    Paths = new List<string>(_userPlaylist.Count),
                    CurrentIndex = _userPlaylistIndex
                };
                foreach (PlaylistItem p in _userPlaylist)
                {
                    if (!string.IsNullOrWhiteSpace(p.FilePath))
                    {
                        state.Paths.Add(p.FilePath);
                    }
                }

                PlayQueueStore.Save(state);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Playback.cs", caught); }
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


        /// <summary>
        /// 由索引缓存的元数据直接构造条目（不打开音频文件，省掉整个 TagLib 解析）。
        /// 字段语义必须与 CreatePlaylistItemFromPath 严格一致，否则同一首歌
        /// 「走索引」和「真解析」会显示成两样。
        /// </summary>
        private static PlaylistItem CreatePlaylistItemFromMeta(LibraryDb.TrackMeta meta)
        {
            return new PlaylistItem
            {
                Title = string.IsNullOrWhiteSpace(meta.Title)
                    ? Path.GetFileNameWithoutExtension(meta.FilePath)
                    : meta.Title,
                Artist = string.IsNullOrWhiteSpace(meta.Artist) ? "未知艺术家" : meta.Artist,
                AlbumArtist = string.IsNullOrWhiteSpace(meta.AlbumArtist) ? "未知艺术家" : meta.AlbumArtist,
                Album = string.IsNullOrWhiteSpace(meta.Album) ? "未知专辑" : meta.Album,
                Track = meta.Track,
                Disc = meta.Disc,
                Year = meta.Year,
                Genre = string.IsNullOrWhiteSpace(meta.Genre) ? "未知流派" : meta.Genre,
                Duration = meta.DurationTicks > 0 ? TimeSpan.FromTicks(meta.DurationTicks) : TimeSpan.Zero,
                FilePath = meta.FilePath,
                Rating = TrackStatsStore.Get(meta.FilePath)?.Rating ?? 0
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
            AudioFxRgInfoText.Text = $"当前曲目：Track {FormatHelper.FormatAudioFxDb(d.TrackGainDb)} dB / Album {FormatHelper.FormatAudioFxDb(d.AlbumGainDb)} dB / peak {d.Peak:0.###}";
        }


        /// <summary>按分类字段值取归一化键（Artist/AlbumArtist 等；统一走 TagSortFields.Value，支持技术字段）。</summary>
        private static string TagSortFieldVal(PlaylistItem p, string field)
            => TagSortFields.Value(p, field);


        /// <summary>刷新分类墙：按 _tagSortClassField 分组 _playlist，每组分封面（首曲封面）。高基数时只显示前 N 个，底部提示加载更多/去分组浏览。</summary>
        private void ShowTagSortClassWall()
        {
            TagSortPanel.Visibility = Visibility.Collapsed;
            TagSortClassScroll.Visibility = Visibility.Visible;

            // 1. 算全部分组（排序后存到 _tagSortClassWallAll）
            _tagSortClassWallAll = _playlist
                .GroupBy(p => TagSortFieldVal(p, _tagSortClassField), StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new TagSortCategoryEntry
                {
                    Name = g.Key,
                    Count = g.Count(),
                    FirstFilePath = g.First().FilePath
                })
                .ToList();

            // 2. 先清空封面字节缓存里残留的旧条目，避免本次分组与上次分类字段不同时大量无效字节残留
            TagSortCoverBytesCache.Clear();

            // 3. 重置可见数 + 渲染
            _tagSortClassWallShown = 0;
            RenderTagSortClassWallSlice();
        }


        /// <summary>渲染分类墙前 N 张卡片（N = _tagSortClassWallShown）+ 刷新底部提示条 + 分批异步加载封面。</summary>
        private void RenderTagSortClassWallSlice()
        {
            int total = _tagSortClassWallAll.Count;
            int show = Math.Min(_tagSortClassWallShown > 0 ? _tagSortClassWallShown : TagSortClassWallInitialMax, total);
            var slice = _tagSortClassWallAll.Take(show).ToList();
            TagSortClassGridView.ItemsSource = slice;

            // 4. 异步加载当前可见卡片的封面（分批限流：8/批、间隔 30ms，避免 UI 抖动）
            _ = LoadClassWallCoversBatchedAsync(slice);

            // 5. 更新底部"加载更多"提示条
            UpdateClassWallOverflowBar(total, show);
        }


        private async System.Threading.Tasks.Task LoadClassWallCoversBatchedAsync(IReadOnlyList<TagSortCategoryEntry> slice)
        {
            const int batchSize = 8;
            const int batchDelayMs = 30;
            for (int i = 0; i < slice.Count; i += batchSize)
            {
                int end = Math.Min(i + batchSize, slice.Count);
                for (int j = i; j < end; j++)
                {
                    _ = LoadTagSortCategoryCoverAsync(slice[j]);
                }
                await System.Threading.Tasks.Task.Delay(batchDelayMs);
            }
        }


        private void UpdateClassWallOverflowBar(int total, int shown)
        {
            if (total <= shown)
            {
                TagSortClassWallOverflowBar.Visibility = Visibility.Collapsed;
                return;
            }

            // 高基数字段时，给更明确的提示
            bool highCardinality = TagSortFields.Find(_tagSortClassField)?.Cardinality == TagSortFields.Cardinality.High;
            string advice = highCardinality
                ? "当前分类字段基数过高（每首曲目几乎都不同）。建议改为低基数字段（如流派/年份/格式），或使用「分组浏览」按字段分组查看。"
                : "分类数量较多，仅显示部分卡片以避免内存占用过高。";

            TagSortClassWallOverflowText.Text = $"共 {total} 个分类，已显示前 {shown} 个。\n{advice}";
            TagSortClassWallLoadMoreButton.Content = shown + TagSortClassWallLoadMoreStep <= total
                ? $"再加载 {Math.Min(TagSortClassWallLoadMoreStep, total - shown)} 个"
                : "加载全部剩余";
            TagSortClassWallOverflowBar.Visibility = Visibility.Visible;
        }


        private void TagSortClassWallLoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            _tagSortClassWallShown = Math.Min(_tagSortClassWallShown + TagSortClassWallLoadMoreStep, _tagSortClassWallAll.Count);
            RenderTagSortClassWallSlice();
        }


        /// <summary>「去分组浏览」按钮（顶部常驻 / 溢出条）：从分类墙直接进入分组浏览视图（模块 C）。
        /// 关键：必须显式隐藏分类墙、显示面板——否则只切内部子面板而父容器仍是 Collapsed，表现为"点了没反应"。</summary>
        private void TagSortClassWallSwitchToGroupButton_Click(object sender, RoutedEventArgs e)
        {
            TagSortClassScroll.Visibility = Visibility.Collapsed;
            TagSortPanel.Visibility = Visibility.Visible;
            // 进入分组浏览时，让分组字段与当前分类墙保持一致（所见即所得）
            _tagSortGroupFields = new List<string> { _tagSortClassField };
            _tagSortPanelMode = "GroupBy";
            ApplyTagSortPanelMode();
        }


        /// <summary>按当前面板视角（Songs/Albums/Artists/Sort）渲染内容区。</summary>
        private void ApplyTagSortPanelMode()
        {
            TagSortPanelGridView.Visibility = Visibility.Collapsed;
            TagSortSongListRoot.Visibility = Visibility.Collapsed;
            TagSortSortPanel.Visibility = Visibility.Collapsed;
            TagSortGroupPanel.Visibility = Visibility.Collapsed;
            TagSortViewModeButton.Content = _tagSortPanelMode switch
            {
                "Albums" => "专辑", "Artists" => "艺术家", "Sort" => "排序方式", "GroupBy" => "分组浏览", _ => "曲目"
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
                _ = LoadClassWallCoversBatchedAsync(albums); // 批处理：避免一次性并发加载所有封面
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
                _ = LoadClassWallCoversBatchedAsync(artists); // 批处理：避免一次性并发加载所有封面
            }
            else if (_tagSortPanelMode == "Sort")
            {
                TagSortSortPanel.Visibility = Visibility.Visible;
                WriteTagSortStatus();
            }
            else if (_tagSortPanelMode == "Songs")
            {
                var ordered = SortTagSortPanelSongs(_tagSortClassSongs.ToList());
                var songs = new ObservableCollection<PlaylistItem>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    ordered[i].Index = i + 1;
                    songs.Add(ordered[i]);
                }
                RebuildTagSortColumnHeaders();
                TagSortPanelSongListView.ItemsSource = songs;
                TagSortSongListRoot.Visibility = Visibility.Visible;
            }
            else if (_tagSortPanelMode == "GroupBy")
            {
                // 模块 C：分组浏览
                // 把字段下拉同步到当前字段（首次进入时）
                SyncTagSortGroupFieldCombo();
                RebuildTagSortGroupRows();
                TagSortGroupPanel.Visibility = Visibility.Visible;
            }
        }


        /// <summary>把 TagSortGroupPresetCombo 当前选项同步到 _tagSortGroupFields（避免 ComboBox 触发重建）。</summary>
        private void SyncTagSortGroupFieldCombo()
        {
            if (TagSortGroupPresetCombo == null) return;
            string current = string.Join(",", _tagSortGroupFields);
            // 1) 命中某预设 → 高亮该预设
            foreach (var obj in TagSortGroupPresetCombo.Items)
            {
                if (obj is ComboBoxItem ci && ci.Tag is string tag
                    && string.Equals(tag, current, StringComparison.Ordinal))
                {
                    if (TagSortGroupPresetCombo.SelectedItem != ci)
                    {
                        TagSortGroupPresetCombo.SelectedItem = ci;
                    }
                    return;
                }
            }
            // 2) 未匹配预设（自定义字段序列）→ 高亮“自定义（已保存）”项
            foreach (var obj in TagSortGroupPresetCombo.Items)
            {
                if (obj is ComboBoxItem ci && string.Equals(ci.Tag as string, "__custom__", StringComparison.Ordinal))
                {
                    if (TagSortGroupPresetCombo.SelectedItem != ci)
                    {
                        TagSortGroupPresetCombo.SelectedItem = ci;
                    }
                    return;
                }
            }
            // 3) 兜底：无匹配也无“自定义”项时取消选中
            TagSortGroupPresetCombo.SelectedItem = null;
        }


        // ============================================================
        // 模块 C：分组浏览（列表内多级分组、组头折叠/展开）
        // ============================================================

        /// <summary>构建/重建分组浏览：按 _tagSortGroupFields 序列对整库 _playlist 多级分组，生成可折叠层级树。
        /// 高基数字段（标题/文件名等）所在层级默认折叠，仅显示组头，避免一次性渲染海量行导致卡顿/爆内存。</summary>
        private void RebuildTagSortGroupRows()
        {
            // 把扁平化结果绑定到列表（只设一次引用即可，后续 Clear/Add 会由 ObservableCollection 自动反映）
            if (TagSortGroupListView != null && TagSortGroupListView.ItemsSource != _tagSortGroupFlatRows)
            {
                TagSortGroupListView.ItemsSource = _tagSortGroupFlatRows;
            }

            _tagSortGroupTree.Clear();
            _tagSortSongIndent.Clear();
            _tagSortGroupFlatRows.Clear();

            if (_tagSortGroupFields == null || _tagSortGroupFields.Count == 0)
            {
                return;
            }

            var roots = BuildGroupTree(_playlist.ToList(), 0, _tagSortGroupFields);
            _tagSortGroupTree.AddRange(roots);
            FlattenGroupTree(_tagSortGroupFlatRows);
        }


        private const int TagSortGroupIndentStep = 22;

        /// <summary>递归按字段序列分组，返回当前层级的分组节点列表。末级分组直接挂 Songs。</summary>
        private List<TagSortGroupHeader> BuildGroupTree(List<PlaylistItem> items, int level, List<string> fields)
        {
            string field = fields[level];
            bool isLast = level == fields.Count - 1;
            string fieldLabel = TagSortFields.Find(field)?.Label ?? field;
            bool autoCollapse = TagSortFields.Find(field)?.Cardinality == TagSortFields.Cardinality.High;

            var groups = items
                .GroupBy(p => TagSortFields.Value(p, field), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var result = new List<TagSortGroupHeader>(groups.Count);
            foreach (var g in groups)
            {
                var node = new TagSortGroupHeader
                {
                    Field = field,
                    FieldLabel = fieldLabel,
                    Value = g.Key,
                    Depth = level,
                    Indent = level * TagSortGroupIndentStep,
                    Count = g.Count(),
                    IsExpanded = !autoCollapse,
                };
                if (isLast)
                {
                    node.Songs = g.ToList();
                }
                else
                {
                    node.Children = BuildGroupTree(g.ToList(), level + 1, fields);
                }
                result.Add(node);
            }
            return result;
        }


        /// <summary>从已建好的树按当前展开状态重新扁平化（不重新 GroupBy）。</summary>
        private void FlattenGroupTree(ObservableCollection<object> rows)
        {
            rows.Clear();
            foreach (var root in _tagSortGroupTree)
            {
                FlattenNode(root, rows);
            }
        }

        private void FlattenNode(TagSortGroupHeader node, ObservableCollection<object> rows)
        {
            rows.Add(node);
            if (!node.IsExpanded) return;
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    FlattenNode(child, rows);
                }
            }
            else if (node.Songs != null)
            {
                int songIndent = (node.Depth + 1) * TagSortGroupIndentStep;
                foreach (var s in node.Songs)
                {
                    _tagSortSongIndent[s] = songIndent;
                    rows.Add(s);
                }
            }
        }


        /// <summary>切换某组的展开/折叠（用节点引用，避免同名组冲突）。</summary>
        private void ToggleTagSortNode(TagSortGroupHeader node)
        {
            node.IsExpanded = !node.IsExpanded;
            FlattenGroupTree(_tagSortGroupFlatRows);
        }

        /// <summary>整组替换播放队列并从该组第一首播放（递归收集节点下全部歌曲）。</summary>
        private void PlayTagSortGroup(TagSortGroupHeader node)
        {
            var songs = CollectNodeSongs(node);
            if (songs.Count == 0) return;
            _userPlaylist.Clear();
            AddSongsToUserPlaylist(songs);
            PlayUserPlaylistAt(0);
        }

        private List<PlaylistItem> CollectNodeSongs(TagSortGroupHeader node)
        {
            var list = new List<PlaylistItem>();
            if (node.Songs != null) list.AddRange(node.Songs);
            if (node.Children != null)
            {
                foreach (var c in node.Children) list.AddRange(CollectNodeSongs(c));
            }
            return list;
        }


        private void SetAllNodesExpanded(bool expanded)
        {
            foreach (var root in _tagSortGroupTree) SetNodeExpanded(root, expanded);
        }

        private void SetNodeExpanded(TagSortGroupHeader node, bool expanded)
        {
            node.IsExpanded = expanded;
            if (node.Children != null)
            {
                foreach (var c in node.Children) SetNodeExpanded(c, expanded);
            }
        }



        private void TagSortGroupPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TagSortGroupPresetCombo?.SelectedItem is ComboBoxItem item
                && item.Tag is string fields)
            {
                if (fields == "__custom__")
                {
                    // 返回已保存的自定义快照（切到预设时也不会被覆盖）
                    _tagSortGroupFields = _tagSortGroupCustom.ToList();
                    _tagSortGroupActivePreset = "__custom__";
                }
                else
                {
                    _tagSortGroupFields = fields.Split(',').ToList();
                    _tagSortGroupActivePreset = fields;
                }
                RebuildTagSortGroupRows();
                AppSettingsStore.Update(s =>
                {
                    s.TagSortGroupFields = _tagSortGroupCustom;
                    s.TagSortGroupActivePreset = _tagSortGroupActivePreset;
                });
            }
        }

        private void TagSortGroupCustomButton_Click(object sender, RoutedEventArgs e)
        {
            // 打开自定义窗口时以“已保存的自定义快照”为初值，便于在原有自定义基础上继续编辑
            var win = new TagSortGroupFieldsWindow(_tagSortGroupCustom);
            win.FieldsConfirmed += fields =>
            {
                _tagSortGroupFields = fields;
                _tagSortGroupCustom = fields.ToList();   // 更新自定义快照
                _tagSortGroupActivePreset = "__custom__";
                RebuildTagSortGroupRows();
                SyncTagSortGroupFieldCombo();           // 让下拉高亮“自定义（已保存）”
                AppSettingsStore.Update(s =>
                {
                    s.TagSortGroupFields = _tagSortGroupCustom;
                    s.TagSortGroupActivePreset = _tagSortGroupActivePreset;
                });
            };
            win.Activate();
        }


        private void TagSortGroupExpandAll_Click(object sender, RoutedEventArgs e)
        {
            SetAllNodesExpanded(true);
            FlattenGroupTree(_tagSortGroupFlatRows);
        }


        private void TagSortGroupCollapseAll_Click(object sender, RoutedEventArgs e)
        {
            SetAllNodesExpanded(false);
            FlattenGroupTree(_tagSortGroupFlatRows);
        }


        private void TagSortGroupListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var fe = e.OriginalSource as FrameworkElement;
            if (fe?.DataContext is TagSortGroupHeader header)
            {
                // 双击组头 = 整组播放（递归收集该节点下全部歌曲）
                PlayTagSortGroup(header);
                e.Handled = true;
                return;
            }

            if (fe?.DataContext is PlaylistItem song)
            {
                // 双击歌曲 = 从该首开始，播放其所属末级分组下的全部歌曲
                if (_tagSortGroupFields == null || _tagSortGroupFields.Count == 0) return;
                string lastField = _tagSortGroupFields[^1];
                string groupVal = TagSortFields.Value(song, lastField);
                var playlist = _playlist
                    .Where(p => string.Equals(TagSortFields.Value(p, lastField), groupVal, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                int startIdx = playlist.FindIndex(p => string.Equals(p.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase));
                if (startIdx < 0) startIdx = 0;
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(playlist);
                PlayUserPlaylistAt(startIdx);
                e.Handled = true;
            }
        }


        private void TagSortGroupListView_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            var fe = e.OriginalSource as FrameworkElement;

            // 组头：与文件夹浏览器右键菜单一致（整组播放 / 多选 / 整组加入播放队列）
            if (fe?.DataContext is TagSortGroupHeader header)
            {
                var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

                var play = new MenuFlyoutItem { Text = "整组播放" };
                play.Icon = new FontIcon { Glyph = "\uE768" };
                play.Click += (_, _) => PlayTagSortGroup(header);
                menu.Items.Add(play);

                var multi = new MenuFlyoutItem { Text = "多选" };
                multi.Icon = new FontIcon { Glyph = "\uE700" };
                multi.Click += (_, _) => EnterMultiSelectModeFrom(TagSortGroupListView);
                menu.Items.Add(multi);

                var groupSongs = CollectNodeSongs(header);
                if (groupSongs.Count > 0)
                {
                    var queue = new MenuFlyoutItem { Text = "整组加入播放队列" };
                    queue.Icon = new FontIcon { Glyph = "\uE710" };
                    queue.Click += (_, _) => AddSongsToUserPlaylist(groupSongs.ToList());
                    menu.Items.Add(queue);
                }

                menu.ShowAt(TagSortGroupListView, e.GetPosition(TagSortGroupListView));
                e.Handled = true;
                return;
            }

            // 歌曲：复用主库右键菜单（播放 / 入队 / 加入播放列表 / 属性...）
            if (fe?.DataContext is PlaylistItem song)
            {
                _multiSelectTargetList = TagSortGroupListView;
                var flyout = BuildPlaylistItemContextMenu(song, inUserPlaylist: false,
                    multiSelectAction: () => EnterMultiSelectModeFrom(TagSortGroupListView));
                flyout.ShowAt(TagSortGroupListView, e.GetPosition(TagSortGroupListView));
                e.Handled = true;
            }
        }


        /// <summary>组头点击：模仿 FolderBrowserItem_Tapped —— 选中该行并展开/折叠分组。</summary>
        private void TagSortGroupHeader_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: TagSortGroupHeader header }) return;
            TagSortGroupListView.SelectedItem = header;
            ToggleTagSortNode(header);
            e.Handled = true;
        }


        /// <summary>组头左侧箭头点击：展开/折叠（阻止冒泡到行 Tapped，避免二次 toggle）。</summary>
        private void TagSortGroupChevron_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is TagSortGroupHeader header)
            {
                ToggleTagSortNode(header);
            }
            e.Handled = true;
        }


        private void TagSortGroupListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshTagSortGroupSelectionChrome();
        }


        /// <summary>分组浏览选中高亮：模仿文件夹浏览器 ApplyFolderBrowserItemSelectionChrome。
        /// 选中行铺满强调色胶囊、文字反色；组头未选中显浅白胶囊，歌曲未选中透明。</summary>
        private void ApplyTagSortGroupRowSelectionChrome(ListViewItem container, object item, HashSet<object>? selectedSet)
        {
            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ColorHelper.ResolveContrastingForeground(accent);

            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(10);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            bool selected = VisualTreeWalker.IsItemSelected(TagSortGroupListView, item, selectedSet);

            Border? chrome = VisualTreeWalker.FindTaggedBorder(container, "TagSortGroupRowChrome");
            if (chrome != null)
            {
                chrome.MinHeight = 36;
                chrome.CornerRadius = new CornerRadius(10);
                chrome.VerticalAlignment = VerticalAlignment.Stretch;
                // 选中矩形铺满整行（与文件夹面板一致），但右侧给垂直滚动条留 16px，不顶到滚动条
                if (TagSortGroupListView.ActualWidth > 0)
                {
                    chrome.Width = Math.Max(0, TagSortGroupListView.ActualWidth - 16);
                }
                if (selected)
                {
                    chrome.Background = accent;
                    ApplyForegroundToDescendants(chrome, selectedFg);
                }
                else
                {
                    chrome.Background = item is TagSortGroupHeader
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 255, 255, 255))
                        : new SolidColorBrush(Colors.Transparent);
                    ClearForegroundOnDescendants(chrome);
                }
            }
            else if (selected)
            {
                container.Background = accent;
                container.Foreground = selectedFg;
            }
            else
            {
                container.Background = new SolidColorBrush(Colors.Transparent);
                container.ClearValue(Control.ForegroundProperty);
            }
        }


        private void RefreshTagSortGroupSelectionChrome()
        {
            if (TagSortGroupListView == null) return;
            var selectedSet = VisualTreeWalker.BuildSelectedItemsLookup(TagSortGroupListView);
            foreach (ListViewItem container in EnumerateRealizedListViewItems(TagSortGroupListView))
            {
                if (TagSortGroupListView.ItemFromContainer(container) is object item)
                {
                    ApplyTagSortGroupRowSelectionChrome(container, item, selectedSet);
                }
            }
        }


        private void TagSortGroupList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (TagSortGroupListView == null) return;
            var selectedSet = VisualTreeWalker.BuildSelectedItemsLookup(TagSortGroupListView);
            if (args.Item is object item && args.ItemContainer is ListViewItem container)
            {
                // 缩进：用 Padding.Left，使内容右移但选中背景仍铺满整行（与文件夹面板层级一致）
                int indent = 0;
                if (item is TagSortGroupHeader h) indent = h.Indent;
                else if (item is PlaylistItem s && _tagSortSongIndent.TryGetValue(s, out int si)) indent = si;
                var chrome = VisualTreeWalker.FindTaggedBorder(container, "TagSortGroupRowChrome");
                if (chrome != null) chrome.Padding = new Thickness(indent, 0, 0, 0);
                ApplyTagSortGroupRowSelectionChrome(container, item, selectedSet);
            }
        }


        /// <summary>标签排序信息列表「播放当前列表歌曲」：按当前列表顺序替换播放队列并从第一首播放。</summary>
        private void TagSortPanelPlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tagSortPanelMode == "GroupBy")
            {
                // 分组浏览：播放整库（即当前分组字段下的全部歌曲）
                if (_playlist.Count == 0) return;
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(_playlist.ToList());
                PlayUserPlaylistAt(0);
            }
            else
            {
                // 进入某分类后的 Songs/Albums/Artists 视角：播放该分类下的歌曲
                if (_tagSortClassSongs.Count == 0) return;
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(_tagSortClassSongs.ToList());
                PlayUserPlaylistAt(0);
            }
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
                if (container.ContentTemplateRoot is Border rowBorder
                    && rowBorder.Child is Grid rowGrid)
                {
                    if (!Equals(rowGrid.Tag, _tagSortColumnVersion))
                    {
                        rowGrid.Tag = _tagSortColumnVersion;
                        BuildTagSortSongRow(rowGrid, song);
                    }
                    else
                    {
                        UpdateTagSortSongRow(rowGrid, song);
                    }
                }
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
                        ApplyCoverFrame(PlaylistDetailCover, PlaylistDetailCoverImage);
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
                song = VisualTreeWalker.FindPlaylistItem(source);
                if (song == null)
                {
                    ListViewItem? container = VisualTreeWalker.FindAncestorListViewItem(source);
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
            ColorHelper.ApplyDialogAccent(dlg);
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
            ColorHelper.ApplyDialogAccent(dlg);
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

                // 按专辑真实顺序（Disc → Track）排列，避免纯文件名字母序把第 1 首排到别处
                songs = OrderAlbumTracks(songs);

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
                ColorHelper.ApplyDialogAccent(dialog);
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
            Brush selectedFg = ColorHelper.ResolveContrastingForeground(accent);
            bool selected = list.SelectionMode == ListViewSelectionMode.Multiple
                ? list.SelectedItems.Contains(song)
                : ReferenceEquals(list.SelectedItem, song);

            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            Border? chrome = VisualTreeWalker.FindTaggedBorder(container, "SongRowChrome");
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
                ColorHelper.ApplyDialogAccent(dialog);
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

            // 按专辑真实顺序播放：优先 Disc（碟号）→ Track（轨道号），
            // 无轨道号的曲目回退到标题排序并排在末尾（避免纯文件名字母序把第 1 首排到别处）。
            tracks = OrderAlbumTracks(tracks);

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


        /// <summary>
        /// 按专辑真实顺序排序：有轨道号的按 Disc → Track；无轨道号（Track==0）回退到
        /// 标题排序并统一排在末尾。这样多 CD 合辑也能按碟序连续播放。
        /// </summary>
        private static List<PlaylistItem> OrderAlbumTracks(List<PlaylistItem> tracks)
        {
            bool anyTrack = tracks.Any(t => t.Track > 0);
            if (!anyTrack)
            {
                // 整组都没有轨道号：退回标题排序（比纯文件名字母序更接近用户预期）
                return tracks
                    .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            return tracks
                .OrderBy(t => t.Track == 0 ? 1 : 0)          // 有轨道号的在前
                .ThenBy(t => t.Disc)                         // 碟号
                .ThenBy(t => t.Track)                        // 轨道号
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
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

            return OrderAlbumTracks(GetOrImportTracksByPaths(paths));
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
