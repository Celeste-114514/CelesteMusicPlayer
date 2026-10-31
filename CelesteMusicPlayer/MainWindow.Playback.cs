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
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
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

        // 这俩给界面/历史/迷你播放器用，一律按"原歌曲时间"：引擎里是（可能被 atempo
        // 拉伸过的）WAV 时间，乘倍率还原。要喂引擎 seek 的地方别用这俩。
        internal TimeSpan EnginePositionValue => _audioEngine == null
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(_audioEngine.Position.TotalSeconds * TempoScale());

        internal TimeSpan EngineDurationValue => _audioEngine == null
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(_audioEngine.Duration.TotalSeconds * TempoScale());

        /// <summary>受支持的音频扩展名：单一真相源在 LibraryFolderScan（这里只是给本文件的老调用点留个名字）。</summary>
        private static readonly string[] AudioExtensions = LibraryFolderScan.AudioExtensions;


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
        /// <summary>波形守卫键（CUE 分轨用）：同一文件的多轨波形不同，路径相同，
        /// 用 "路径#起点" 区分，避免切轨后旧回调把上一轨的波形写进来。</summary>
        private string? _waveformRangeKey;
        private bool _waveformProgress;
        // 主题波形强调色（ResolveAccentColor 的缓存，避免频繁解析）
        private static Color _waveAccentColor = Color.FromArgb(255, 0, 120, 212);
        private long _lastSmtcTimelineMs; // SMTC timeline 限频（约 500ms 一次）
        private long _lastSmtcTimelineLogMs; // SMTC timeline 诊断日志限频（约 3s 一次）
        /// <summary>SMTC 专用宿主 MediaPlayer：挂静音循环源激活播放会话，独立于主播放器，避免污染主播放的事件/UI。</summary>
        /// <summary>多选当前作用的歌曲列表（歌曲库 / 艺术家详情 / 专辑详情曲目）</summary>
        private ListView? _multiSelectTargetList;
        /// <summary>多选当前作用的专辑网格（音乐库专辑 / 艺术家详情专辑）</summary>
        private GridView? _multiSelectAlbumGrid;
        /// <summary>多选当前作用的文件夹浏览列表</summary>
        private ListView? _multiSelectFolderList;
        /// <summary>批量改选中时跳过 SelectionChanged 里的昂贵 UI 刷新</summary>
        private Brush? _cachedMultiSelectFrostBrush;

        private int _currentIndex = -1;
        // W13：旧「当前播放列表 / 播放队列」独立窗口随 XAML 界面下线（网页界面自带队列）。


        private void InitializePlayerAndTimers()
        {
            if (_mediaPlayer != null)
            {
                return;
            }

            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.CommandManager.IsEnabled = false;

            _mediaPlayer.MediaOpened += Player_MediaOpened;
            _mediaPlayer.MediaEnded += Player_MediaEnded;
            _mediaPlayer.MediaFailed += Player_MediaFailed;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;

            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            ApplyStartupPlaybackSettings();
            _mediaPlayer.Volume = VolumeSlider.Value / 100.0;
            UpdateSignalChainDisplay();
            UpdateDesktopLyricsBadge();
            InitializeMusicPlayer2Features();


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
            if (Content is FrameworkElement root)
            {
                root.ActualThemeChanged += (_, _) =>
                {
                    RefreshPlaylistSelectionChrome();
                    ApplyPlaylistHeaderChipStyle();
                };
            }

            AppWindow.Changed += (_, args) =>
            {
                if (args.DidSizeChange || args.DidPresenterChange)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        EnforceMinimumWindowSize();
                        ScheduleFitColumns();
                    });
                }
            };

            DispatcherQueue.TryEnqueue(() =>
            {
                FitColumnsToAvailableWidth();
            });

            // 启动带进来的外部文件（双击打开）排在曲库恢复之后播，避免和启动续播抢链路
            _ = RestoreLastLibraryThenPendingFileAsync();
        }


        /// <summary>音量、播放模式等可立即应用的启动设置。</summary>
        private void ApplyStartupPlaybackSettings()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            _applyingSettingsVolume = true;
            try
            {
                // 音量滑条在共享与 HiFi 独占下都可用：HiFi 下调 DAC 设备/驱动级主音量（不破坏 bit-perfect），
                // 用保存音量回填，避免切模式/重启后音量跳回默认值（此前误加 IsHiFiModeSelected 条件，
                // 导致共享模式重启后音量不回填、停在 XAML 默认 80%）。
                _volumeToSave = Math.Clamp(settings.Volume, 0, 100); // 启动即同步，避免退出时以旧/0 值写盘
                // HiFi 独占/ASIO 且未开「HiFi 软件音量」：数字音量不走 DSP，滑条恒显示 100%
                // （实际音量由 DAC 旋钮 / 系统端点音量控制，保 bit-perfect）。
                // 必须与 SetVolumeFromPointer（点一下音量条）和 ApplySettingsLive（设置页改完即时生效）
                // 三处口径一致 —— 此前启动时按保存值回填（如 63%），用户点一下音量条才跳到 100%，
                // 看起来像"启动音量不对/要手动点一下才满"。
                // 注意：只改显示，绝不动 _volumeToSave，否则退出时会把用户在共享模式下的真实偏好覆盖成 100。
                bool hifiVolumeLocked = IsHiFiModeSelected() && !IsHiFiSoftVolumeUiUnlocked();
                VolumeSlider.Value = hifiVolumeLocked ? VolumeSlider.Maximum : Math.Clamp(settings.Volume, 0, 100);
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
            OutputMode.Apply(settings);
        }


        /// <summary>应用 HiFi 输出设备：记录到引擎偏好并设置 MediaPlayer 输出设备。</summary>
        private async System.Threading.Tasks.Task ApplyOutputDeviceAsync(string deviceId)
        {
            // B4：输出设备切换 = 链路事件（启动恢复首次调用只记值，不算"切换"）
            NoteChainDevice(deviceId);

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

            try
            {
                _taskbarButtons?.UpdatePlayPause(playing);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.SyncTransportPlayState", caught);
            }
        }







        /// <summary>恢复上次正在播放的歌曲到界面（暂停，不自动开播）。</summary>
        private async Task RestoreLastPlayingTrackAsync()
        {
            try
            {
                // 安全模式（启动恢复页选的"安全模式"）：这一轮连队列都不恢复，
                // 免得一进来就先读一堆文件、把嫌疑范围搅浑。用户想听自己点就行。
                if (Recovery.SafeModeActive)
                {
                    StartupLog.Write("[recovery] 安全模式：跳过自动续播与队列恢复。");
                    return;
                }

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
                        if (string.IsNullOrWhiteSpace(p) || (!System.IO.File.Exists(p) && SourceFromReference(p) == null))
                        {
                            continue;
                        }

                        // 内部缓存产物不进任何用户可见列表
                        if (LibraryPathGuard.IsInternalCacheFile(p))
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

                        _queue.Replace(rebuilt);
                        if (ReferenceEquals(_playlistViewSource, _queue.Items) == false)
                        {
                            _playlistViewSource = _queue.Items;
                        }

                        int idx = (queue.CurrentIndex >= 0 && queue.CurrentIndex < _queue.Items.Count)
                            ? queue.CurrentIndex : 0;
                        await PrepareTrackPausedAsync(idx);
                        return;
                    }
                }

                // 兼容旧版：仅记住了单曲
                PlaybackSessionState? session = PlaybackSessionStore.TryLoad();
                if (session == null
                    || string.IsNullOrWhiteSpace(session.FilePath)
                    || (!System.IO.File.Exists(session.FilePath) && SourceFromReference(session.FilePath) == null))
                {
                    return;
                }

                string path = session.FilePath;
                PlaylistItem? item = null;
                int userIdx = FindUserPlaylistIndex(path);
                if (userIdx >= 0)
                {
                    item = _queue.Items[userIdx];
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
            if (userPlaylistIndex < 0 || userPlaylistIndex >= _queue.Items.Count)
            {
                return;
            }

            PlaylistItem item = _queue.Items[userPlaylistIndex];
            _queue.Index = userPlaylistIndex;
            _currentIndex = FindLibraryIndex(item.FilePath);

            // ★ 记住上次听到哪儿：存档里存了退出时的进度，点播放时从这里接着播。
            // 以前这里只恢复"是哪一首"，位置被丢掉 → 每次启动都从头开始。
            _pendingStartSeconds = ReadSavedPositionSeconds(item.FilePath, item.Duration);
            _pendingStartPauseAfter = false;
            _pendingStartPath = _pendingStartSeconds > 0 ? item.FilePath : null;


            // 就绪态也要把进度条/总时长/波形准备好：否则进度条停在默认的 0~100，
            // 总时长显示 00:00，点播放前拖不了进度条，波形也是空的。
            PreparePausedProgressUi(item);
            HighlightUserPlaylistItem(userPlaylistIndex, item);
            // 收藏（心形）状态：以前只在真正开播后才同步，启动恢复的那首会显示成未收藏
            UpdateFavoriteButtonUi();

            await UpdateNowPlayingPanelAsync(item);

            // 启动续播时同步写入 SMTC（状态=暂停/就绪），让系统媒体浮窗在启动后即显示歌名/歌手/封面，
            // 与双击播放后的显示保持一致；否则启动恢复上次播放时浮窗只剩程序图标。
            MediaSession.ConfigureTrack(item, playing: false);
            // 顺带把时长/进度写进系统时间轴，这样系统浮窗能显示总时长并可以拖定位
            // （就绪态引擎还没建，时长从曲目标签来，所以显式传进去）。
            MediaSession.UpdateTimeline(TimeSpan.FromSeconds(_pendingStartSeconds), item.Duration);

            // 「启动后自动播放」：走引擎路径（与双击播放一致），这样 DSP 链、实时电平表、
            // SMTC 播放状态/进度才会全部正常。此前这里走 MediaPlayer 的 player.Play()，
            // 而 MediaPlayer 路径没有 DSP 链 → LevelMeterChannels 恒 0 → 电平表不显示，
            // 且 SMTC 状态也不会更新（与之前启动续播 SMTC 卡「暂停」同源）。
            if (AppSettingsStore.Load().AutoPlayWhenStart)
            {
                StartPlayback(item);
                NotifyCurrentPlaylistWindow();
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
                return;
            }

            // 不再把歌曲预加载进 MediaPlayer：Media Foundation 认不出的格式（DSD、部分高规格
            // WAV/FLAC 等）会立刻触发 MediaFailed → 弹「无法播放 SourceNotSupported」，
            // 而双击播放走引擎路径一切正常 —— 这就是启动报错、双击却能播的原因。
            // 就绪态的 SMTC 展示已由 MediaSessionService.ConfigureTrack 负责；点播放按钮由
            // PlayPauseButton_Click 转走引擎路径（按 _queue.Index 恢复到这首）。
            NotifyCurrentPlaylistWindow();
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
                _waveformProgress = AppSettingsStore.Load().WaveformProgress;

                double duration = item.Duration.TotalSeconds;
                if (duration > 1)
                {
                }

                double start = Math.Clamp(_pendingStartSeconds, 0, duration > 1 ? duration : 0);
                _isUpdatingProgressUi = true;
                try
                {
                }
                finally
                {
                    _isUpdatingProgressUi = false;
                }


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
                    && ReferenceEquals(_playlistViewSource, _queue.Items)
                    && userPlaylistIndex >= 0
                    && userPlaylistIndex < _queue.Items.Count)
                {
                }
                else if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                    && _currentIndex >= 0
                    && _playlist.Count > _currentIndex
                    && ReferenceEquals(_playlistViewSource, _playlist))
                {
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
                        PlaybackSessionStore.Save(item.PersistenceKey, GetLivePositionSeconds());
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
                    // 进度条按"原歌曲时间"走：引擎位置是（可能被 atempo 拉伸的）WAV 时间，乘倍率还原
                    return _audioEngine.Position.TotalSeconds * TempoScale();
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
                if (_queue.Items.Count == 0)
                {
                    PlayQueueStore.Clear();
                    return;
                }

                var state = new PlayQueueState
                {
                    Paths = new List<string>(_queue.Items.Count),
                    CurrentIndex = _queue.Index
                };
                foreach (PlaylistItem p in _queue.Items)
                {
                    if (!string.IsNullOrWhiteSpace(p.PersistenceKey)
                        && !LibraryPathGuard.IsInternalCacheFile(p.PersistenceKey))
                    {
                        state.Paths.Add(p.PersistenceKey);
                    }
                }

                PlayQueueStore.Save(state);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Playback.cs", caught); }
        }



        // 2026-10-16：原「播放速度」按钮 + 六档预设菜单（PlaybackRateButton_Click）已删除——
        // 换成音频设置面板「播放速度」页里的拖动滑块（0.01 步进、拖动即时变声）。


        /// <summary>
        /// 当前播放的"源时间刻度"倍率。2026-10-16 起恒为 1.0：变速从"烧 atempo 进转码"
        /// 改成托管侧实时变速（ManagedDspSourceProvider.SpeedStretch）后，转码产物就是原始长度，
        /// 引擎位置本身就是"原歌曲时间"，进度条直接用、不需要再换算。
        /// 保留这个方法与 ChainFormat.TempoShifted 判据是给老代码路径一个明确的 1.0 出口；
        /// 若将来哪条路径又烧了时长进去，这里会自动恢复换算。
        /// 2026-09-30 用户要求：变速不要把进度条总时长拉长（1min 的歌 0.5x 变 2min）。
        /// </summary>
        private double TempoScale()
        {
            try
            {
                if (!_usingEnginePlayback || _audioEngine == null)
                {
                    return 1.0;
                }

                if (_audioEngine.ChainFormat is not { TempoShifted: true })
                {
                    return 1.0;
                }

                double rate = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
                return Math.Abs(rate - 1.0) < 0.001 ? 1.0 : rate;
            }
            catch
            {
                return 1.0;
            }
        }


        /// <summary>
        /// 设播放倍速（2026-10-15 重写为实时）：持久化 + 直接推给引擎的托管变速，
        /// 播放中下一个音频块即变声——不重载、不重转码、不断音。
        /// （旧实现：记住位置→用 ffmpeg atempo 按新倍率整轨重转码→原位重起，拖动一次卡几秒；
        ///  atempo 路径已废，转码恒为原始长度，变速在 ManagedDspSourceProvider 里实时做。）
        /// DSD/DoP 直出不过 DSP 链 → 变速无效，如实说明（转码成 PCM 的 DSD 可以变速）。
        /// </summary>
        private Task SetPlaybackRateAsync(double rate)
        {
            double clamped = Math.Clamp(rate, 0.5, 2.0);
            double prev = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
            if (Math.Abs(clamped - prev) < 0.001)
            {
                return Task.CompletedTask; // 设的就是当前倍率，什么都不做
            }

            bool preserve = AppSettingsStore.Load().PlaybackRatePreservePitch;
            AppSettingsStore.Update(s =>
            {
                s.PlaybackRate = clamped;
                s.PlaybackRatePreservePitch = preserve;
            });
            UpdatePlaybackRateButtonText();
            string rateText = clamped.ToString("0.##") + "x";

            // 实时下发：引擎活着就下一个音频块生效；没在播就只是记设置，起播时自动带上
            _audioEngine?.SetPlaybackSpeed(clamped, preserve);

            // B4：变速 = 链路事件（倍率≠1 时链路面板会标"已重塑采样，非原始数据"）
            NoteChainTempo(clamped);

            if (!IsEngineActiveNow)
            {
                return Task.CompletedTask;
            }

            if (_audioEngine?.IsDsdDirectOut == true)
            {
                // DoP 直出 = 1-bit 位流直通设备，不经 DSP 链，托管变速够不着它
                return Task.CompletedTask;
            }

            StartupLog.Write($"[变速] 实时设为 {clamped:0.##}x 保调={preserve}");
            return Task.CompletedTask;
        }


        private void UpdatePlaybackRateButtonText()
        {

            double rate = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
        }

        private MediaPlayer? GetPlayer() => _mediaPlayer;



        // ---------------------------------------------------------------------
        // 文件加载入口（2026-10-31 第十四刀自壳 Misc.cs 原样搬来，依赖全在活文件：
        // GetPlayer/CreatePlaylistItemFromPath=Playback、ClearNowPlayingPanel=Playback3、
        // StopEngineIfActive=Playback4；尾部 ApplyCategoryView 按第十三刀口径改调活版。
        // ---------------------------------------------------------------------
        /// <summary>根据路径读元数据并加入列表，然后按当前排序规则重排。</summary>
        /// <param name="persist">为 true 时写入上次会话（默认）。</param>
        /// <param name="persistAsFiles">为 true 时按「文件列表」模式保存整份播放列表。</param>
        private void LoadAndAddFiles(string[] filePaths, bool persist = true, bool persistAsFiles = false, bool replace = false)
        {
            if (filePaths == null || filePaths.Length == 0)
            {
                return;
            }

            string? playingPath = null;
            if (replace)
            {
                // 替换模式：停止当前播放并清空媒体库，再载入新内容
                MediaPlayer? player = GetPlayer();
                if (player != null)
                {
                    player.Pause();
                    player.Source = null;
                }

                StopEngineIfActive();
                _playlist.Clear();
                _currentIndex = -1;
                ClearNowPlayingPanel();
            }
            else
            {
                // 记住当前正在播的文件，排序后要能找回下标
                playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                    ? _playlist[_currentIndex].FilePath
                    : null;
            }

            // HashSet 去重，避免大批量导入时对 _playlist 反复线性扫描
            var knownPaths = new HashSet<string>(
                _playlist.Select(i => i.FilePath),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;

            // 大批量导入时临时解绑列表视图，避免逐条 Add 触发 ListView 反复创建/测量容器造成卡顿
            bool rebounded = ReferenceEquals(_playlistViewSource, _playlist);
            if (rebounded)
            {
                _playlistViewSource = null;
            }

            foreach (string path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    continue;
                }

                // 内部缓存产物（DSD DoP 缓存 / 转码缓存 / WebDAV 下载缓存）不是本地曲库内容，不进音乐库
                if (LibraryPathGuard.IsLibraryExcludedFile(path))
                {
                    continue;
                }

                if (!knownPaths.Add(path))
                {
                    continue;
                }

                try
                {
                    PlaylistItem item = CreatePlaylistItemFromPath(path);
                    // CUE 分轨展开：有同名 .cue 的文件按轨加入（设置关闭时原样返回单条）
                    foreach (PlaylistItem track in CueTrackExpansion.Expand(new List<PlaylistItem> { item }))
                    {
                        _playlist.Add(track);
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    knownPaths.Remove(path);
                    System.Diagnostics.Debug.WriteLine($"加载失败: {path} → {ex.Message}");
                }
            }

            // 循环结束恢复绑定；即使下方早返回(added==0)也能保持列表可见
            if (rebounded)
            {
                _playlistViewSource = _playlist;
            }

            if (added == 0)
            {
                if (replace)
                {
                    ApplyCategoryViewData();
                }

                return;
            }

            // 当前在「歌曲」分类时，按标题默认排序刷新列表
            if (_currentCategory == "Songs")
            {
                ApplyCategoryViewData();
            }
            else if (_currentCategory == "Albums")
            {
                ApplyCategoryViewData();
            }
            else if (_currentCategory == "Artists" || _currentCategory == "AlbumArtists")
            {
                ApplyCategoryViewData();
            }
            else if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                // 播放列表保持用户自己的顺序（添加顺序 / 手动拖动的结果）——
                // 这里只是往曲库补歌，不该顺手把播放队列重排一遍。
                // 2026-10-08 用户实测：启动恢复出的播放队列被自动按标题打乱，就是这条分支干的。
                // 用户主动点排序按钮走 SortField/SortOrder 那条路径，不受这里影响。
                RenumberCollection(_queue.Items);
            }
            else
            {
                ApplySort(preservePlayingPath: playingPath);
            }

            // 不自动播放：用户双击或点播放后再开始
            if (added > 0)
            {
            }

            if (persist && persistAsFiles)
            {
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath));
            }
        }

        /// <summary>TagLib 读取标题 / 艺术家 / 专辑 / 音轨号 / 年份 / 时长。
        /// TTA 例外：TagLib 没有 TTA 容器解析器，走 <see cref="CreatePlaylistItemFromTta"/>。</summary>
        internal static PlaylistItem CreatePlaylistItemFromPath(string path)
        {
            if (SourceFromReference(path) is PlaylistItem referenced) return referenced;
            if (TtaTagAccess.IsTtaPath(path)) return CreatePlaylistItemFromTta(path);
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
        /// TTA 专用建条目（2026-10-31 补）：TagLib 没有 TTA 容器解析器，
        /// <c>File.Create("xxx.tta")</c> 直接抛 UnsupportedFormatException，以前这条路
        /// 一抛就被调用方 catch 掉 → 曲库扫描时 TTA 文件**整首丢掉**（实测 100 首素材只进 90 首）。
        /// 现在：标签走 <see cref="TtaTagAccess.Read"/>（文件头 ID3v2 / 文件尾 APEv2），
        /// 时长读 TTA 文件头（不解码）。缺省值口径与非 TTA 分支严格一致。
        /// </summary>
        private static PlaylistItem CreatePlaylistItemFromTta(string path)
        {
            TagEditModel tag = TtaTagAccess.Read(path);
            string fileName = Path.GetFileNameWithoutExtension(path);

            string title = string.IsNullOrWhiteSpace(tag.Title) ? fileName : tag.Title.Trim();

            // 主曲库取 FirstPerformer；多值模型里就是第一个值
            string artist = tag.Performers.Length > 0 && !string.IsNullOrWhiteSpace(tag.Performers[0])
                ? tag.Performers[0].Trim()
                : "未知艺术家";

            string albumArtist = tag.AlbumArtists.Length > 0 && !string.IsNullOrWhiteSpace(tag.AlbumArtists[0])
                ? tag.AlbumArtists[0].Trim()
                : artist;

            string album = string.IsNullOrWhiteSpace(tag.Album) ? "未知专辑" : tag.Album.Trim();
            string genre = tag.Genres.Length > 0 && !string.IsNullOrWhiteSpace(tag.Genres[0])
                ? tag.Genres[0].Trim()
                : "未知流派";

            return new PlaylistItem
            {
                Title = title,
                Artist = artist,
                AlbumArtist = albumArtist,
                Album = album,
                Track = tag.Track,
                Disc = tag.Disc,
                Year = tag.Year,
                Genre = genre,
                Duration = TtaTagAccess.ReadDuration(path),
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






        /// <summary>收集 ReplayGain 面板状态 → 持久化 + 应用到引擎（带上当前曲目的增益/peak）。</summary>
        private void ApplyReplayGainToEngine()
        {
            // 与 ApplyDspToEngine 同理：面板未就绪时不保存/应用（避免启动阶段默认覆盖）。
            if (!_audioFxPanelReady)
            {
                return;
            }

            // 参数来自模型；曲目标签增益/peak 是每首文件的事实，不属于用户设置。
            ReplayGainState rg = RgParameters.Snapshot();
            ReplayGainStore.Save(rg);

            double tg = _currentRgData?.TrackGainDb ?? 0;
            double ag = _currentRgData?.AlbumGainDb ?? 0;
            double peak = _currentRgData?.Peak ?? 1.0;
            _audioEngine?.SetReplayGain(rg, tg, ag, peak);
            RefreshAudioFxRgInfo();

            // B4：ReplayGain 变化 = 链路事件（内容去重，滑前级增益不会刷屏）
            NoteChainDsp("rg", "ReplayGain " + (rg.Mode == ReplayGainMode.Off
                ? "未启用"
                : $"{rg.Mode}，前级 {rg.PreampDb:0.#}dB"));
        }


        /// <summary>刷新当前曲目 ReplayGain 标签信息文本。</summary>
        private void RefreshAudioFxRgInfo()
        {
            if (_currentRgData == null)
            {
                return;
            }

            var d = _currentRgData.Value;
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

        }


        private async System.Threading.Tasks.Task LoadPlaylistWallCoverAsync(PlaylistCardViewModel vm)
        {
            try
            {
                // 优先从列表歌曲取首曲封面（第一首含封面的）；歌曲全无封面时才回落用户手动设的自定义封面。
                // 2026-10-16 参考 ECHO：歌单墙卡片批量出图，先走分档缓存（Album 320），
                // 缓存命中时零 TagLib 抽取；未命中由服务内部生成一份落盘，下次直接读。
                Microsoft.UI.Xaml.Media.Imaging.BitmapImage? bmp = null;
                foreach (string path in NamedPlaylistStore.LoadSongs(vm.Name))
                {
                    if (!System.IO.File.Exists(path)) continue;
                    try
                    {
                        bmp = await CoverArtService.GetAsync(path, CoverVariant.Album);
                        if (bmp != null) break;
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Playback.cs", caught); }
                }
                if (bmp == null)
                {
                    // 歌曲全无封面：回落到用户手动设置的自定义封面；仍无则以 Cover=null 由 View 图标兜底。
                    string? custom = PlaylistLibraryService.CustomCoverPath(vm.Name);
                    if (!string.IsNullOrWhiteSpace(custom))
                    {
                        byte[]? cb = await System.Threading.Tasks.Task.Run(() => System.IO.File.ReadAllBytes(custom));
                        if (cb is { Length: > 0 })
                        {
                            bmp = await CreateBitmapFromBytesAsync(cb, CoverDecodeTargetPx(null, 160)); // 与专辑封面同一机制；歌单墙卡片 160px
                        }
                    }
                }
                if (bmp != null)
                {
                    vm.Cover = bmp;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
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
                _queue.Items.Clear();
                AddSongsToUserPlaylist(items);
                int index = FindUserPlaylistIndex(filePath);
                PlayUserPlaylistAt(index >= 0 ? index : 0);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PlayNamedPlaylistFromTrack", ex);
            }
        }
























        private void ApplySongsSearchFilter()
        {
            if (_currentCategory != "Songs")
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(_playlistViewSource, _playlist))
                {
                    _playlistViewSource = _playlist;
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

            _playlistViewSource = filtered;
            RefreshPlaylistSelectionChrome();
        }


        /// <summary>播放列表搜索：标题 / 艺术家 / 专辑 / 年份。</summary>
        private void ApplyUserPlaylistSearchFilter()
        {
            if (_currentCategory != "UserPlaylist")
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(_playlistViewSource, _queue.Items))
                {
                    _playlistViewSource = _queue.Items;
                }

                RefreshPlaylistSelectionChrome();
                return;
            }

            List<PlaylistItem> filtered = _queue.Items
                .Where(p => MatchesPlaylistSearch(p, q))
                .ToList();

            _playlistViewSource = filtered;
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
















        private static bool TrackMatchesArtistName(PlaylistItem track, string artistName, bool useAlbumArtist)
        {
            string key = useAlbumArtist ? track.AlbumArtist : track.Artist;
            return string.Equals(key, artistName, StringComparison.CurrentCultureIgnoreCase);
        }






        // 播放传输：音量步进与进度跳转（2026-10-31 从 MainWindow.Features.cs 搬来）：网页命令/托盘/热键在调。

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
                    // seconds 是"原歌曲时间"里跳多少（热键 ±5）：先还原成源时间，seek 前再换算回 WAV 时间
                    double scale = TempoScale();
                    double duration = _audioEngine.Duration.TotalSeconds * scale;
                    double next = _audioEngine.Position.TotalSeconds * scale + seconds;
                    next = Math.Clamp(next, 0, duration > 0 ? duration : next);
                    _audioEngine.Seek(TimeSpan.FromSeconds(next / scale));

                    // seek 会丢弃无缝源里已预加载的下一首，重挂一次
                    _ = Session.PreloadNextAsync();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
                return;
            }

            MediaPlayer? player = GetPlayer();
            if (player?.Source == null)
            {
                // 启动就绪态：还没播，快进/快退就当作"从这个位置开始播"
                StartPlaybackFromPendingPosition(Math.Max(0, _pendingStartSeconds + seconds), pauseAfter: false);
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
        // 播完事件处理（2026-10-31 从 MainWindow.Misc.cs 搬来）：MediaPlayer.Ended 的核心回调。

        private void HandleMediaEnded()
        {
            if (ConsumeSleepStopIfDue())
            {
                return;
            }

            switch (_orderResolver.Order)
            {
                case PlaybackOrder.TrackLoop:
                    // IsLooping 为 true 时通常不会触发；兜底再播当前曲
                    if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count)
                    {
                        PlayUserPlaylistAt(_queue.Index);
                    }
                    break;

                case PlaybackOrder.TrackOnce:
                    break;

                case PlaybackOrder.Sequential:
                    PlayNext(autoAdvance: true);
                    break;

                default:
                    PlayNext(autoAdvance: true);
                    break;
            }
        }
        // 列宽工具与悬停提示（2026-10-31 从 MainWindow.Misc2.cs 搬来）。


        private void HideHoverTip()
        {
            if (_activeHoverTip != null)
            {
                _activeHoverTip.IsOpen = false;
                _activeHoverTip = null;
            }

            if (_hoverElement != null)
            {
                ToolTipService.SetToolTip(_hoverElement, null);
            }
        }
    }
}
