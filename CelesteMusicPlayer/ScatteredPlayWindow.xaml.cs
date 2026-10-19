using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 零散播放窗口（2026-10-18 用户定案；10-18 深夜重构为自持引擎独立播放器）。
    ///
    /// 定位 = 主程序「播放信息页面」的独立克隆，但**完全不依附主程序**：
    /// · 自己的音频引擎实例（FFmpeg 解码 + 与主程序同一套 DSP 底层；主程序设过的
    ///   输出模式/设备/EQ/DSP 配置档等共享设置由引擎与后端自己从盘上读，自动继承）；
    /// · 自己的纯内存队列（_queue）：换歌 / 下一首 / 播完切换全自己管，
    ///   主程序的播放队列（_userPlaylist）一根头发都不动；
        /// · 扒全播放页：滚动歌词（高亮 + 逐行滚动）+ 可视化（40 段频谱柱 + 峰值帽）；
        /// · 十段均衡器：10 段各一根滑条，写与主程序同一条 equalizer.json；
        /// · 设置菜单：打开文件位置 / 文件属性 / 倍速播放（作用于当前播放的歌）；
        /// · 音量条常驻主界面（口径同主程序：独占/ASIO 未开软体积时钉 100%，保 bit-perfect）。
    ///
    /// 入口（三条，都进同一个队列）：
    /// ① 冷启动双击文件关联 —— App 直接开本窗口，主窗口都不建、曲库更不会读；
    /// ② 主程序在跑时双击/转交 —— 只开本窗口，不抢主窗口焦点、不动它的队列；
    /// ③ 主程序托盘「零散播放」/窗口内打开文件/拖放。
    /// 队列只在内存里：不写 SQLite、不入音乐库、不进下次启动的续播。
    /// </summary>
    public sealed partial class ScatteredPlayWindow : Window
    {
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

        private bool _uiReady;
        private string _bgMode = "Cover";

        // Cover 态的压暗纱罩（从封面主色算出来；没有封面时为 null → 退回纯黑纱）。
        // 见 UpdateCoverTintAsync —— 与迷你播放器同一套口径（CoverTint）。
        private Microsoft.UI.Xaml.Media.Brush? _coverTintBrush;
        private int _coverTintToken;

        // 进度条拖动态（指针事件必须代码挂 + handledEventsToo，见 HookProgressSliderPointer）
        private bool _userSeeking;
        private uint _progressPointerId;
        private bool _progressOnThumb;
        private string _shownPath = "";

        // 窗口拖动态
        private bool _isDragging;
        private uint _dragPointerId;
        private int _dragCursorStartX;
        private int _dragCursorStartY;
        private int _dragWindowStartX;
        private int _dragWindowStartY;

        // 队列面板
        private bool _syncingQueueSelection;

        // 封面加载防串台（切歌快时旧回调不许覆盖新封面）
        private int _coverToken;

        public ScatteredPlayWindow()
        {
            InitializeComponent();
            // 与 MainWindow 同一处置位：把 UI 线程 DispatcherQueue 缓存给引擎层
            // （HiFiOutputBackend 可能在后台线程被懒构造，取不到当前线程队列会抛）。
            // 零散直达冷启动不建 MainWindow，这行不补 → 起播必炸
            // 「HiFiOutputBackend 必须在 UI 线程构造，且 UiDispatcherQueue 尚未设置」。
            // 两个窗口同在一条 UI 线程，主程序在跑时覆盖也是同一个值，无害。
            HiFiOutputBackend.UiDispatcherQueue = this.DispatcherQueue;
            WindowIconHelper.Apply(this);
            // 窗口名与主程序保持一致（2026-10-18 用户要求）
            Title = "CelesteMusicPlayer";

            // 毛玻璃背板：Cover 态透出它（被封面图盖住大半），深浅态被纯色底盖住
            try
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.ctor.backdrop", caught);
            }

            AppSettingsState settings = AppSettingsStore.Load();
            _bgMode = settings.ScatteredPlayBackground is "Dark" or "Light" or "Frosted"
                ? settings.ScatteredPlayBackground
                : "Cover";
            ApplyBackgroundMode(_bgMode, persist: false);

            // 播放顺序：初值取全局设置（与主程序同一个偏好），本窗口内切换即写回
            _order = Enum.TryParse(settings.PlaybackOrder, ignoreCase: true, out PlaybackOrder order)
                ? order
                : PlaybackOrder.ListLoop;
            PlaybackOrderIcon.Glyph = OrderGlyph(_order);

            // EQ：从与主程序同一条 store 读初值
            LoadEqFromStore();

            // 音量滑条：从共享设置回填（锁定时钉 100）
            LoadVolumeFromSettings();
            RefreshRateButton();

            // 滑块指针事件：Slider 内部会把 PointerPressed/Released 标成 Handled，
            // XAML 挂的处理器收不到 → 拖动失效。必须代码挂 + handledEventsToo。
            HookProgressSliderPointer();

            // 按下探针（诊断用，见 DragProbe_PointerPressed）：handledEventsToo 什么都收，
            // 只记日志不改路由——排查「窗口没有区域可以拖动」时区分
            // 「按下没到 XAML」和「到了但被控件吃掉」
            RootGrid.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(DragProbe_PointerPressed),
                handledEventsToo: true);

            // 窗口 chrome（2026-10-18 用户拍板）：去系统标题栏、无最大化、固定大小；
            // 最小化/关闭是顶栏自绘按钮（见 XAML  MinimizeButton / CloseButton）。
            // SetBorderAndTitleBar(false,false) 去掉边框与标题栏，再把标题栏区折叠成
            // 0 高、图标与系统菜单隐藏 —— 迷你播放器同款配方，本机实测可用。
            var presenter = OverlappedPresenter.Create();
            presenter.IsResizable = false;   // 固定窗口大小，不给拖拽缩放（用户要求）
            presenter.IsMinimizable = true;  // 保留最小化（自绘按钮走 ShowWindow，任务栏图标仍在）
            presenter.IsMaximizable = false; // 无最大化按钮（用户要求）
            presenter.SetBorderAndTitleBar(false, false);
            AppWindow.SetPresenter(presenter);

            ExtendsContentIntoTitleBar = false;
            TryCollapseTitleBar();

            // 尺寸变化（DPI 切换/意外最大化恢复）时重打裁剪区：剪白边不能只打一次
            AppWindow.Changed += ScatteredAppWindow_Changed;

            // 比上一版略高：要塞进歌词区 + 可视化条
            ResizeToDips(540, 740);
            CenterOnScreen();

            if (Content is FrameworkElement root)
            {
                root.Loaded += (_, _) =>
                {
                    _uiReady = true;
                    // 原生层去边框：剥窗口样式 + 子类化吞非客户区绘制 + 裁剪区剪掉丙烯背板白边。
                    // WinUI 层 presenter 只去标题栏，系统那圈 1px 白描边还在（用户实测），
                    // 完整配方见 ScatteredPlayWindow.Chrome.cs（迷你播放器同款）。
                    ApplyWindowChromeNative();
                    RefreshAll();
                };
            }

            _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += (_, _) => RefreshTick();
            _timer.Start();

            // 面板开启动画兜底定时器（只建一次，Tick 在构造时挂；开面板时 Stop+Start 重启）
            _panelFallbackTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _panelFallbackTimer.Interval = TimeSpan.FromMilliseconds(400);
            _panelFallbackTimer.Tick += (_, _) => PanelFallbackTick();

            Closed += (_, _) =>
            {
                try
                {
                    _timer?.Stop();
                    _panelFallbackTimer?.Stop();
                    StopVisualizerTimer();
                    // 摘子类化：不摘的话关闭后 WndProc 还会被持有一段时间
                    RemoveWindowSubclassIfNeeded();
                    // EQ：最后一次拖动可能还在防抖窗口里，关窗前无条件落盘
                    SaveEqNow();
                    _eqSaveTimer?.Stop();
                    // 音量：同理，把防抖窗里最后一次改动写掉
                    SaveVolumeNow();
                    _volumeSaveTimer?.Stop();
                    // 关窗即停：不留下一个没有界面的播放器
                    ShutdownEngine();
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.Closed", caught);
                }
            };
        }

        // =====================================================================
        // 背景三态
        // =====================================================================

        /// <summary>
        /// 切换背景模式。Cover 态整窗恒深色（压暗纱罩保证白字可读 —— 用户拍板：
        /// 浅色封面上不能出现白底看不见控件的情况）；Dark/Light/Frosted 是另外三态，
        /// 窗口主题跟着切（Frosted 走深色主题），ThemeResource 的文字/描边自动反色。
        /// </summary>
        private void ApplyBackgroundMode(string mode, bool persist)
        {
            _bgMode = mode;
            RootGrid.RequestedTheme = mode == "Light" ? ElementTheme.Light : ElementTheme.Dark;

            if (mode == "Cover")
            {
                RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                // 压暗纱罩：优先用封面主色压出来的渐变（迷你播放器那套），
                // 没取到色就退回 82% 纯黑（比原来的 65% 深 —— 用户报浅色封面上白条看不见）
                ApplyCoverScrim();
                BackgroundCoverImage.Opacity = 1;
                BackgroundModeText.Text = "背景：封面";
            }
            else if (mode == "Dark")
            {
                RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 16, 22));
                BackgroundScrim.Opacity = 0;
                BackgroundCoverImage.Opacity = 0;
                BackgroundModeText.Text = "背景：深色";
            }
            else if (mode == "Frosted")
            {
                // 毛玻璃：RootGrid 只铺一层 65% 深色纱，透出构造时备好的 DesktopAcrylic 背板
                // （背板把窗后的桌面模糊/提亮，深色纱保证白字可读；主题走深色，控件自动反色）
                RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(165, 16, 16, 22));
                BackgroundScrim.Opacity = 0;
                BackgroundCoverImage.Opacity = 0;
                BackgroundModeText.Text = "背景：毛玻璃";
                EnsureAcrylicBackdrop();
            }
            else
            {
                RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 243, 243, 246));
                BackgroundScrim.Opacity = 0;
                BackgroundCoverImage.Opacity = 0;
                BackgroundModeText.Text = "背景：浅色";
            }

            ApplyTitleBarColors(mode);
            ApplyVisualizerColors();

            if (persist)
            {
                try
                {
                    AppSettingsState s = AppSettingsStore.Load();
                    s.ScatteredPlayBackground = mode;
                    AppSettingsStore.Save(s);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.ApplyBackgroundMode", caught);
                }
            }
        }

        /// <summary>标题栏配色跟着背景模式走（系统标题栏不跟元素主题，得手动刷）。</summary>
        private void ApplyTitleBarColors(string mode)
        {
            Safe(() =>
            {
                AppWindowTitleBar bar = AppWindow.TitleBar;
                if (mode == "Light")
                {
                    bar.BackgroundColor = Windows.UI.Color.FromArgb(255, 243, 243, 246);
                    bar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 243, 243, 246);
                    bar.ForegroundColor = Windows.UI.Color.FromArgb(255, 26, 26, 30);
                    bar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 120, 120, 128);
                }
                else
                {
                    bar.BackgroundColor = Windows.UI.Color.FromArgb(255, 16, 16, 22);
                    bar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 16, 16, 22);
                    bar.ForegroundColor = Windows.UI.Color.FromArgb(255, 232, 232, 238);
                    bar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 120, 120, 132);
                }

                // DWM 边框/标题栏颜色跟着背景模式走：万一原生层还有残留 1px 描边，
                // 也融进当前背景看不见（详见 ScatteredPlayWindow.Chrome.cs）
                PushDwmFrameColor(mode);
            });
        }

        /// <summary>毛玻璃态确保丙烯背板在（构造时设过；万一当年失败，进毛玻璃态时补一次）。</summary>
        private void EnsureAcrylicBackdrop()
        {
            try
            {
                if (SystemBackdrop == null)
                {
                    SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.EnsureAcrylicBackdrop", caught);
            }
        }

        /// <summary>
        /// Cover 态的压暗纱罩（2026-10-19）：
        /// 从封面采主色 → 保留色相/饱和度、亮度强制压到 28%/13%（CoverTint，与迷你播放器同一套），
        /// 再做成左上 78% → 右下 86% 的半透明渐变盖在封面上。
        /// 这样封面还能透出约两成（看得出是封面、还跟着封面变色），但底下一定够暗，
        /// 白色进度条/白字在任何封面上都读得出来（用户报的「封面背景上白色元素看不清」）。
        /// </summary>
        private async Task UpdateCoverTintAsync(byte[]? bytes, int token)
        {
            int tintToken = ++_coverTintToken;
            Microsoft.UI.Xaml.Media.Brush? brush = null;

            if (bytes is { Length: > 0 })
            {
                Windows.UI.Color? dominant = await CoverTint.AnalyzeAsync(bytes);
                if (tintToken != _coverTintToken)
                {
                    return;
                }

                if (dominant != null)
                {
                    (Windows.UI.Color start, Windows.UI.Color end) = CoverTint.BuildDimGradient(dominant.Value);
                    var gradient = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0),
                        EndPoint = new Windows.Foundation.Point(1, 1),
                    };
                    gradient.GradientStops.Add(new GradientStop
                    {
                        Color = Windows.UI.Color.FromArgb(199, start.R, start.G, start.B),
                        Offset = 0,
                    });
                    gradient.GradientStops.Add(new GradientStop
                    {
                        Color = Windows.UI.Color.FromArgb(219, end.R, end.G, end.B),
                        Offset = 1,
                    });
                    brush = gradient;
                }
            }

            if (tintToken != _coverTintToken)
            {
                return;
            }

            _coverTintBrush = brush;
            if (_bgMode == "Cover")
            {
                ApplyCoverScrim();
            }
        }

        /// <summary>把当前纱罩画刷刷上去（有取色渐变用渐变，没有就 82% 纯黑兜底）。</summary>
        private void ApplyCoverScrim()
        {
            BackgroundScrim.Fill = _coverTintBrush
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(209, 0, 0, 0)); // 82% 黑
            BackgroundScrim.Opacity = 1;
        }

        private void BackgroundModeButton_Click(object sender, RoutedEventArgs e)
        {
            string next = _bgMode switch
            {
                "Cover" => "Dark",
                "Dark" => "Light",
                "Light" => "Frosted",
                _ => "Cover"
            };
            ApplyBackgroundMode(next, persist: true);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 把系统标题栏区折叠成 0 高并全透明（迷你播放器同款配方）：
        /// ExtendsContentIntoTitleBar=true 让内容区伸进标题栏位，PreferredHeightOption=Collapsed
        /// 把该区高度压成 0，图标与系统菜单隐藏 —— 视觉上不再有系统标题栏。
        /// 配合构造函数里的 SetBorderAndTitleBar(false,false) 一起去掉边框+标题栏。
        /// </summary>
        private void TryCollapseTitleBar()
        {
            Safe(() =>
            {
                AppWindowTitleBar bar = AppWindow.TitleBar;
                bar.ExtendsContentIntoTitleBar = true;
                bar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
                bar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

                Windows.UI.Color transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                bar.BackgroundColor = transparent;
                bar.InactiveBackgroundColor = transparent;
                bar.ButtonBackgroundColor = transparent;
                bar.ButtonInactiveBackgroundColor = transparent;
                bar.ButtonHoverBackgroundColor = transparent;
                bar.ButtonPressedBackgroundColor = transparent;
                bar.ForegroundColor = transparent;
                bar.InactiveForegroundColor = transparent;
                bar.ButtonForegroundColor = transparent;
                bar.ButtonInactiveForegroundColor = transparent;
            });
        }

        /// <summary>自绘最小化按钮：系统标题栏没了，最小化走 ShowWindow（任务栏图标仍在，无最大化）。</summary>
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                ShowWindow(hwnd, SwShowMinimized);
            });
        }

        // =====================================================================
        // 状态刷新（250ms tick）
        // =====================================================================

        private void RefreshTick()
        {
            Safe(() =>
            {
                PlaylistItem? item = CurrentItem;

                if (item == null)
                {
                    if (_shownPath.Length != 0)
                    {
                        _shownPath = "";
                        TitleText.Text = "未在播放";
                        ArtistAlbumText.Text = "";
                        AudioInfoText.Text = "";
                        AudioInfoText.Visibility = Visibility.Collapsed;
                        CoverImage.Source = null;
                        BackgroundCoverImage.Source = null;
                        ClearLyricsUi();
                        StatusText.Text = DefaultHintText;
                    }
                }
                else if (!string.Equals(item.FilePath, _shownPath, StringComparison.OrdinalIgnoreCase))
                {
                    _shownPath = item.FilePath;
                    TitleText.Text = string.IsNullOrWhiteSpace(item.Title)
                        ? Path.GetFileNameWithoutExtension(item.FilePath)
                        : item.Title;
                    ArtistAlbumText.Text = BuildArtistAlbumLine(item);
                    _ = LoadTrackExtrasAsync(item);
                    RefreshQueueList();
                }

                RefreshTransportState();

                if (_engine != null)
                {
                    UpdateProgressUi(_engine.Position, _engine.Duration);
                }
                else
                {
                    UpdateProgressUi(TimeSpan.Zero, TimeSpan.Zero);
                }

                SyncLyricsToPosition(CurrentPosition);
            });
        }

        /// <summary>整条刷新（开窗、拖放入队后调用）。</summary>
        public void RefreshAll()
        {
            _shownPath = "";
            RefreshQueueList();
            RefreshTick();
        }

        private const string DefaultHintText =
            "把音频文件拖到这里即可加入队列 · 双击关联文件也会进到这里 · 本队列只在内存里，不写音乐库";

        private static string BuildArtistAlbumLine(PlaylistItem item)
        {
            string artist = string.IsNullOrWhiteSpace(item.Artist) ? "未知艺术家" : item.Artist;
            string album = string.IsNullOrWhiteSpace(item.Album) ? "未知专辑" : item.Album;
            return artist + " · " + album;
        }

        private void RefreshTransportState()
        {
            Safe(() =>
            {
                bool playing = _engine?.IsPlaying == true;
                PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
            });
        }

        private void UpdateProgressUi(TimeSpan position, TimeSpan duration)
        {
            Safe(() =>
            {
                double total = duration.TotalSeconds;
                if (total <= 0 || double.IsNaN(total))
                {
                    ProgressSlider.Maximum = 100;
                    if (!_userSeeking)
                    {
                        ProgressSlider.Value = 0;
                        TotalTimeText.Text = "--:--";
                        CurrentTimeText.Text = "--:--";
                    }
                }
                else
                {
                    double pos = Math.Clamp(position.TotalSeconds, 0, total);
                    ProgressSlider.Maximum = total;
                    if (!_userSeeking)
                    {
                        ProgressSlider.Value = pos;
                        CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(pos));
                    }
                    TotalTimeText.Text = FormatTime(duration);
                }
            });
        }

        /// <summary>换歌时把封面模糊版铺到背景（Cover 态）。模糊与主程序整窗背景同一实现。</summary>
        private async Task LoadTrackExtrasAsync(PlaylistItem item)
        {
            int token = ++_coverToken;

            // 技术信息（格式/位深/采样率）：与主程序同一格式化器
            try
            {
                string? info = await Task.Run(() => AudioInfoFormatter.Format(item.FilePath));
                if (token != _coverToken) return;
                AudioInfoText.Text = info ?? string.Empty;
                AudioInfoText.Visibility = string.IsNullOrWhiteSpace(AudioInfoText.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.LoadTrackExtras.info", caught);
            }

            // 封面（TagLib 内嵌图）→ 前景小图 + Cover 态背景大图
            try
            {
                byte[]? bytes = await Task.Run(() => LoadCoverBytes(item.FilePath));
                if (token != _coverToken) return;

                BitmapImage? cover = bytes is { Length: > 0 }
                    ? await DecodeBitmapAsync(bytes, 0)
                    : null;
                if (token != _coverToken) return;
                CoverImage.Source = cover;

                // 2026-10-19：Cover 态的压暗纱罩跟着封面取色走（迷你播放器那套）
                _ = UpdateCoverTintAsync(bytes, token);

                if (_bgMode == "Cover")
                {
                    if (bytes is { Length: > 0 })
                    {
                        byte[]? blurred = await Task.Run(() =>
                            AlbumArtBackground.CreateHeavilyBlurredPng(bytes, workSize: 96, blurRadius: 2));
                        if (token != _coverToken) return;
                        BitmapImage? bg = blurred is { Length: > 0 }
                            ? await DecodeBitmapAsync(blurred, 0)
                            : null;
                        if (token != _coverToken) return;
                        BackgroundCoverImage.Source = bg;
                    }
                    else
                    {
                        BackgroundCoverImage.Source = null;
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.LoadTrackExtras.cover", caught);
            }

            // 歌词（与主程序同一加载器：同名 .lrc / 内嵌，尊重设置里的来源优先级）
            try
            {
                List<LyricLine> lines = await Task.Run(() => LyricsLoader.LoadForAudio(item.FilePath));
                if (token != _coverToken) return;
                BuildLyricsUi(lines);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.LoadTrackExtras.lyrics", caught);
            }
        }

        /// <summary>内嵌封面字节（取不到返回 null）。TagLib 只读标签，不动音频。</summary>
        private static byte[]? LoadCoverBytes(string path)
        {
            try
            {
                using TagLib.File tagFile = TagLib.File.Create(path);
                TagLib.IPicture[]? pictures = tagFile.Tag.Pictures;
                // 与主程序取封面同一写法（TagEditorService）：IPicture.Data 是 ByteVector，
                // .Count 判空、.Data 出 byte[]。数组没有 .Count 属性（会解析成 LINQ 方法组）。
                return pictures?[0]?.Data?.Count > 0 ? pictures[0].Data.Data : null;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.LoadCoverBytes", caught);
            }

            return null;
        }

        /// <summary>字节 → BitmapImage（须在 UI 线程调用）。</summary>
        private static async Task<BitmapImage?> DecodeBitmapAsync(byte[] bytes, int maxPixel = 0)
        {
            try
            {
                var image = new BitmapImage();
                using Windows.Storage.Streams.InMemoryRandomAccessStream stream = new();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                await image.SetSourceAsync(stream);
                return image;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.DecodeBitmapAsync", caught);
                return null;
            }
        }

        // =====================================================================
        // 进度条（拖动 / 点击落点 / 松手提交）
        // =====================================================================

        private void HookProgressSliderPointer()
        {
            Safe(() =>
            {
                ProgressSlider.AddHandler(
                    UIElement.PointerPressedEvent,
                    new PointerEventHandler(ProgressSlider_PointerPressed),
                    handledEventsToo: true);
                ProgressSlider.AddHandler(
                    UIElement.PointerMovedEvent,
                    new PointerEventHandler(ProgressSlider_PointerMoved),
                    handledEventsToo: true);
                ProgressSlider.AddHandler(
                    UIElement.PointerExitedEvent,
                    new PointerEventHandler(ProgressSlider_PointerExited),
                    handledEventsToo: true);
                ProgressSlider.AddHandler(
                    UIElement.PointerReleasedEvent,
                    new PointerEventHandler(ProgressSlider_PointerReleased),
                    handledEventsToo: true);
                ProgressSlider.AddHandler(
                    UIElement.PointerCaptureLostEvent,
                    new PointerEventHandler(ProgressSlider_PointerCaptureLost),
                    handledEventsToo: true);
            });
        }

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 起拖标志先立：下面的落点赋值会触发 ValueChanged，赶在标志前面的话
            // 时间文本不会跟着跳，看起来就是"点了没反应"。
            _userSeeking = true;
            _progressPointerId = e.Pointer.PointerId;
            _progressOnThumb = IsInsideThumb(e.OriginalSource);

            // 点在滑块圆头上 → 原生拖动照常工作，松手时提交；
            // 点在轨道上 → 原生 Slider 只负责瞬移，按住继续拖它不管，这里自己算落点并捕获指针。
            if (!_progressOnThumb)
            {
                SeekFromPointer(e);
                try
                {
                    ProgressSlider.CapturePointer(e.Pointer);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.ProgressSlider_PointerPressed", caught);
                }
            }
        }

        private void ProgressSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_userSeeking && !_progressOnThumb && e.Pointer.PointerId == _progressPointerId)
            {
                SeekFromPointer(e);
            }
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // 撒手撒在窗口外且没人捕获指针时 Released 永远不会来，_userSeeking 会卡死。
            if (_userSeeking
                && (ProgressSlider.PointerCaptures == null || ProgressSlider.PointerCaptures.Count == 0))
            {
                CommitSeek();
            }
        }

        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                ProgressSlider.ReleasePointerCapture(e.Pointer);
            }
            catch (Exception caught)
            {
                // 没捕获过（原生滑块自己在管）就会走到这里，属正常路径
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.ProgressSlider_PointerReleased", caught);
            }

            CommitSeek();
        }

        private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            CommitSeek();
        }

        /// <summary>XAML 挂的 ValueChanged：只在用户拖动时实时跟时间文本，程序刷新不抢。</summary>
        private void ProgressSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_uiReady || !_userSeeking)
            {
                return;
            }

            CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(ProgressSlider.Value));
        }

        private void SeekFromPointer(PointerRoutedEventArgs e)
        {
            Safe(() =>
            {
                if (ProgressSlider.Maximum <= 0)
                {
                    return;
                }

                double px = e.GetCurrentPoint(ProgressSlider).Position.X;
                double ratio = Math.Clamp(px / Math.Max(1, ProgressSlider.ActualWidth), 0, 1);
                ProgressSlider.Value = ratio * ProgressSlider.Maximum;
            });
        }

        private static bool IsInsideThumb(object? originalSource)
        {
            DependencyObject? node = originalSource as DependencyObject;
            while (node != null)
            {
                if (node is Microsoft.UI.Xaml.Controls.Primitives.Thumb)
                {
                    return true;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return false;
        }

        private void CommitSeek()
        {
            if (!_userSeeking)
            {
                return;
            }

            _userSeeking = false;
            Safe(() =>
            {
                double seconds = ProgressSlider.Value;
                SeekSeconds(seconds);
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(seconds));
            });
        }

        // =====================================================================
        // 传输按钮 / 打开文件
        // =====================================================================

        private void PreviousButton_Click(object sender, RoutedEventArgs e) => Safe(PlayPrevious);

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => Safe(TogglePlayPause);

        private void NextButton_Click(object sender, RoutedEventArgs e) => Safe(PlayNext);

        private void PlaybackOrderButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                PlaybackOrder[] cycle =
                {
                    PlaybackOrder.Sequential,
                    PlaybackOrder.Random,
                    PlaybackOrder.ListLoop,
                    PlaybackOrder.TrackLoop,
                    PlaybackOrder.TrackOnce
                };

                int index = Array.IndexOf(cycle, _order);
                _order = cycle[index < 0 ? 0 : (index + 1) % cycle.Length];
                PlaybackOrderIcon.Glyph = OrderGlyph(_order);

                // 与主程序同一个偏好字段：两边看到的播放顺序一致
                try
                {
                    AppSettingsState s = AppSettingsStore.Load();
                    s.PlaybackOrder = _order.ToString();
                    AppSettingsStore.Save(s);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.PlaybackOrderButton_Click", caught);
                }
            });
        }

        private static string OrderGlyph(PlaybackOrder order) => order switch
        {
            PlaybackOrder.Sequential => "\uE8FD",
            PlaybackOrder.Random => "\uE8B1",
            PlaybackOrder.ListLoop => "\uE8EE",
            PlaybackOrder.TrackLoop => "\uE8ED",
            PlaybackOrder.TrackOnce => "\uE72A",
            _ => "\uE8EE"
        };

        private async void OpenFilesButton_Click(object sender, RoutedEventArgs e)
        {
            string[]? files = await PickAudioFilesAsync();
            if (files == null || files.Length == 0)
            {
                return;
            }

            // 追加进队列（提示语就是「加入队列」）；当前没在播会自动起播第一首
            EnqueueFiles(files, playNow: false);
            RefreshAll();
        }

        /// <summary>多选音频文件（过滤器与文件关联注册的扩展名一致）。</summary>
        private async Task<string[]?> PickAudioFilesAsync()
        {
            try
            {
                FileOpenPicker picker = new();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                foreach (string ext in QuickPlayLauncher.PlayableExtensions)
                {
                    picker.FileTypeFilter.Add(ext);
                }

                IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    return null;
                }

                return files
                    .Where(f => !string.IsNullOrWhiteSpace(f.Path))
                    .Select(f => f.Path)
                    .ToArray();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.PickAudioFilesAsync", caught);
                return null;
            }
        }

        // =====================================================================
        // 队列面板
        // =====================================================================

        // =====================================================================
        // 队列面板 / EQ 面板（原地视图切换 + 开启动画）
        //
        // 2026-10-18 用户拍板：旧形态是 RootGrid 级底部浮层，一打开就盖住进度条/传输行
        // （「和原界面重叠」）。改为「原地视图」：面板占主体那一行（Grid.Row=1），
        // 打开时封面/可视化/歌词整块让位，进度/传输/状态行原样留在下面。
        // 开启动画 = 上移 24px + 淡入；代码建 Storyboard + SetTarget 锁死目标
        // （XAML TargetName 在本仓有解析失败案），Collapsed→Visible 同帧 Begin 会被吞，
        // 延后一帧再演，另加 400ms 兜底复查。
        // =====================================================================

        private bool _queuePanelOpen;
        private bool _eqPanelOpen;
        private Storyboard? _panelStoryboard;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _panelFallbackTimer;

        private void QueueButton_Click(object sender, RoutedEventArgs e)
        {
            if (_queuePanelOpen)
            {
                CloseQueuePanel();
            }
            else
            {
                OpenQueuePanel();
            }
        }

        private void QueueCloseButton_Click(object sender, RoutedEventArgs e) => CloseQueuePanel();

        /// <summary>打开队列面板：主体区让位，进度/传输行不被盖（原地视图）。</summary>
        private void OpenQueuePanel()
        {
            Safe(() =>
            {
                if (_eqPanelOpen)
                {
                    CloseEqPanel();
                }

                MainContentGrid.Visibility = Visibility.Collapsed;
                QueuePanel.Visibility = Visibility.Visible;
                _queuePanelOpen = true;
                // 注意：RefreshQueueList 内部有可见性守卫，必须在 Visible 之后调才会真刷新
                RefreshQueueList();
                AnimatePanelIn(QueuePanel, QueuePanelTransform);
            });
        }

        private void CloseQueuePanel()
        {
            Safe(() =>
            {
                _queuePanelOpen = false;
                StopPanelAnimation();
                QueuePanel.Visibility = Visibility.Collapsed;
                if (!_eqPanelOpen)
                {
                    MainContentGrid.Visibility = Visibility.Visible;
                }
            });
        }

        /// <summary>关 EQ 面板（EQ 开闭的其它入口在 ScatteredPlayWindow.Eq.cs）。</summary>
        private void CloseEqPanel()
        {
            Safe(() =>
            {
                _eqPanelOpen = false;
                StopPanelAnimation();
                EqPanel.Visibility = Visibility.Collapsed;
                if (!_queuePanelOpen)
                {
                    MainContentGrid.Visibility = Visibility.Visible;
                }
            });
        }

        /// <summary>
        /// 面板开启动画：上移 24px + 淡入（220ms）。先 Stop 旧故事板再设初值 ——
        /// 动画完结后 HoldEnd 会锁住属性，不 Stop 的话本地赋值被吞。
        /// </summary>
        private void AnimatePanelIn(Border panel, TranslateTransform transform)
        {
            StopPanelAnimation();

            transform.Y = 24;
            panel.Opacity = 0;

            var storyboard = new Storyboard();

            var slide = new DoubleAnimation
            {
                From = 24,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "Y");

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(160))
            };
            Storyboard.SetTarget(fade, panel);
            Storyboard.SetTargetProperty(fade, "Opacity");

            storyboard.Children.Add(slide);
            storyboard.Children.Add(fade);
            _panelStoryboard = storyboard;

            // Collapsed→Visible 同帧 Begin 会被静默吞掉（本项目实测三次）：延后一帧再开演
            // （本文件没有 using Microsoft.UI.Dispatching，DispatcherQueue 简单名会解析到
            //   Window 实例属性 → CS0176，必须全限定；同文件构造函数两处同样写法）
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
            {
                Safe(() =>
                {
                    storyboard.Begin();
                    _panelFallbackTimer?.Stop();
                    _panelFallbackTimer?.Start();
                });
            });
        }

        /// <summary>兜底复查：动画被吞了就手动落位（只落还开着的那一个）。</summary>
        private void PanelFallbackTick()
        {
            Safe(() =>
            {
                if (_queuePanelOpen)
                {
                    QueuePanelTransform.Y = 0;
                    QueuePanel.Opacity = 1;
                }

                if (_eqPanelOpen)
                {
                    EqPanelTransform.Y = 0;
                    EqPanel.Opacity = 1;
                }
            });
        }

        /// <summary>停掉进行中的开启动画（关面板/重新开面板前调用）。</summary>
        private void StopPanelAnimation()
        {
            Safe(() =>
            {
                _panelStoryboard?.Stop();
                _panelStoryboard = null;
            });
        }

        /// <summary>把零散队列（纯内存）拉进来显示。</summary>
        private void RefreshQueueList()
        {
            Safe(() =>
            {
                QueueTitleText.Text = "播放队列（" + _queue.Count + "）";
                if (QueuePanel.Visibility != Visibility.Visible)
                {
                    return;
                }

                QueueList.ItemsSource = _queue.ToList();

                _syncingQueueSelection = true;
                try
                {
                    QueueList.SelectedIndex = _queueIndex >= 0 && _queueIndex < _queue.Count ? _queueIndex : -1;
                }
                finally
                {
                    _syncingQueueSelection = false;
                }
            });
        }

        private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingQueueSelection || !_uiReady)
            {
                return;
            }

            int index = QueueList.SelectedIndex;
            if (index < 0 || index >= _queue.Count)
            {
                return;
            }

            Safe(() => PlayQueueAt(index));
            RefreshAll();
        }

        // =====================================================================
        // 窗口拖动（从非交互区按住拖；按钮/滑块/列表自带处理不会起拖）
        //
        // 2026-10-19 用户连报两次「没有区域可以鼠标点击拖动」：
        //   · 顶栏 DragBar 有名字、有透明背景、单独挂事件，中间画了根把手条做提示；
        //   · 真正的移动改到 **Win32 层**（按下 → SetCapture 到顶层窗口 → 子类化里
        //     WM_MOUSEMOVE 用 SetWindowPos 挪窗）。原因：本窗口 XAML 侧的指针路由是坏的，
        //     自动化探测证明按下事件根本不进处理器（两套 XAML 方案都 dx=dy=0）。
        //     详见 Chrome.cs 的 ScatteredSubclassProc 与 StartWin32Drag。
        // 两条入口（RootGrid 空白处 / DragBar）共用同一段起拖判定。
        // =====================================================================

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
            => TryBeginDrag(e);

        private void DragBar_PointerPressed(object sender, PointerRoutedEventArgs e)
            => TryBeginDrag(e);

        private void DragBar_PointerMoved(object sender, PointerRoutedEventArgs e)
            => RootGrid_PointerMoved(sender, e);

        private void DragBar_PointerReleased(object sender, PointerRoutedEventArgs e)
            => RootGrid_PointerReleased(sender, e);

        private void DragBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
            => RootGrid_PointerCaptureLost(sender, e);

        /// <summary>
        /// 起拖判定：只负责判断"这一下按在空白处还是控件上"，
        /// 判定过了就交给 StartWin32Drag（Win32 层）去做真正的移动。
        /// </summary>
        private void TryBeginDrag(PointerRoutedEventArgs e)
        {
            // ⚠ 这里曾经有一句「鼠标必须 IsLeftButtonPressed」的判断，2026-10-19 实测
            // 它在本窗口恒为 false —— RootGrid 开了 AllowDrop（要接拖放进来的文件），
            // 鼠标按下走的是拖放那条输入路径，PointerPoint 上的按钮状态读不到。
            // 结果就是每一次按下都在这里静默 return，表现成「完全拖不动」。
            // PointerPressed 本身就代表"这一下按下已经发生了"，不需要再确认左键。

            Safe(() =>
            {
                string srcType = e.OriginalSource?.GetType().Name ?? "null";
                string srcName = (e.OriginalSource as FrameworkElement)?.Name ?? "";
                StartupLog.Write($"拖动起手 src={srcType}#{srcName}");
            });

            // 命中任何交互控件都不起拖，否则点按钮会变成拖窗
            if (e.OriginalSource is DependencyObject src && IsControlElement(src))
            {
                return;
            }

            // 移动交给轮询定时器（见 StartPollingDrag 的说明）
            StartPollingDrag();
            e.Handled = true;
        }

        // ---------------------------------------------------------------- 轮询式拖动

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragTimer;
        private int _dragCursorX;
        private int _dragCursorY;
        private int _dragWinX;
        private int _dragWinY;

        /// <summary>
        /// 起拖（2026-10-19 定案）：**自己按帧读光标位置挪窗口**，不依赖任何指针事件。
        ///
        /// 为什么非要绕这么一圈：本窗口实测三条常规路都走不通——
        ///   ① XAML 手动跟踪（PointerMoved + AppWindow.Move）：PointerMoved 收不到；
        ///   ② SendMessage 冒充标题栏按下：鼠标消息是发给 XAML 子窗口的，顶层窗口没人理；
        ///   ③ SetCapture 到顶层窗口 + 子类化收 WM_MOUSEMOVE：按下能进、捕获也调了，窗口仍不动。
        /// 轮询是唯一不依赖"事件能不能送到"的做法：只要按下那一下进来了（实测能进），
        /// 之后每帧自己读光标、自己挪，松手（左键抬起）自己停。
        /// </summary>
        private void StartPollingDrag()
        {
            Safe(() =>
            {
                if (!GetCursorPos(out POINT cursor))
                {
                    return;
                }

                PointInt32 pos = AppWindow.Position;
                _dragCursorX = cursor.X;
                _dragCursorY = cursor.Y;
                _dragWinX = pos.X;
                _dragWinY = pos.Y;

                // ⚠ 必须全限定：本文件没有 using Microsoft.UI.Dispatching，
                // 裸写 DispatcherQueue 会被解析成 Window 的实例属性 → CS0176（项目老坑）。
                _dragTimer ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
                _dragTimer.Stop();
                _dragTimer.Interval = TimeSpan.FromMilliseconds(16);
                _dragTimer.Tick -= OnDragTimerTick;
                _dragTimer.Tick += OnDragTimerTick;
                _dragTimer.Start();
            });
        }

        private void OnDragTimerTick(object? sender, object? args)
        {
            // 左键松开了就收工（不依赖 PointerReleased —— 那个在本窗口同样靠不住）
            if (GetAsyncKeyState(VkLButton) >= 0)
            {
                StopPollingDrag();
                return;
            }

            if (!GetCursorPos(out POINT cursor))
            {
                return;
            }

            int dx = cursor.X - _dragCursorX;
            int dy = cursor.Y - _dragCursorY;
            if (dx == 0 && dy == 0)
            {
                return;
            }

            AppWindow.Move(new PointInt32(_dragWinX + dx, _dragWinY + dy));
        }

        private void StopPollingDrag()
        {
            if (_dragTimer == null)
            {
                return;
            }

            _dragTimer.Stop();
            _dragTimer.Tick -= OnDragTimerTick;
        }

        private const int VkLButton = 0x01;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// 诊断探针（10-18 深夜「窗口没有区域可以拖动」排查用）：在 RootGrid 上挂
        /// handledEventsToo 的按下探针——无论事件被哪个子元素标记 Handled 都能看到。
        /// 用来区分两种坏法：「按下根本没到 XAML」vs「到了但被控件吃掉」。
        /// 探针只记日志不改路由，可长期留着。
        /// </summary>
        private void DragProbe_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Safe(() =>
            {
                string srcType = e.OriginalSource?.GetType().Name ?? "null";
                string srcName = (e.OriginalSource as FrameworkElement)?.Name ?? "";
                StartupLog.Write($"按下探针 src={srcType}#{srcName} handled={e.Handled}");
            });
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging || e.Pointer.PointerId != _dragPointerId)
            {
                return;
            }

            if (!GetCursorPos(out POINT cursor))
            {
                return;
            }

            AppWindow.Move(new PointInt32(
                _dragWindowStartX + (cursor.X - _dragCursorStartX),
                _dragWindowStartY + (cursor.Y - _dragCursorStartY)));
            e.Handled = true;
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerId != _dragPointerId)
            {
                return;
            }

            EndDrag();
        }

        private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            EndDrag();
        }

        private void EndDrag()
        {
            // 只清标志位：指针捕获由框架在 PointerReleased / CaptureLost 时自动释放
            // （迷你播放器同款做法，显式 ReleasePointerCapture 反而要多构造一个 Pointer）。
            _isDragging = false;
            _dragPointerId = 0;
        }

        private static bool IsControlElement(DependencyObject src)
        {
            // ⚠ 只能往上查**有限层数**（这里取 3 层）：
            // 2026-10-19 实测这个判断把顶栏 DragBar 判成了"控件"（日志：判为控件=True），
            // 因为一直往上追到了窗口内容根里某个容器（滚动区一类），于是每一次按下都被
            // "命中控件不起拖"挡掉 —— 这正是用户连报两次「拖不动」的根因。
            // 只查 3 层：够覆盖"点在图标/文字上、父级才是按钮"这类情况，
            // 又不会追到跟本窗口布局无关的祖级容器上去。
            const int MaxDepth = 3;

            DependencyObject? node = src;
            int depth = 0;
            while (node != null && depth <= MaxDepth)
            {
                // 列表 / 滚动区 / 输入框也豁免：在队列列表或歌词上按住想滚一下，
                // 结果整窗被拖着跑（迷你播放器实测反馈过的教训）。
                if (node is Button
                    || node is Slider
                    || node is ListViewBase
                    || node is Microsoft.UI.Xaml.Controls.Primitives.SelectorItem
                    || node is ScrollViewer
                    || node is TextBox)
                {
                    return true;
                }

                node = VisualTreeHelper.GetParent(node);
                depth++;
            }

            return false;
        }

        // =====================================================================
        // 拖放文件入队
        // =====================================================================

        private void RootGrid_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView != null && e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
        }

        private async void RootGrid_Drop(object sender, DragEventArgs e)
        {
            if (e.DataView == null || !e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                List<string> files = new();
                foreach (IStorageItem item in items)
                {
                    if (item is StorageFile file && QuickPlayLauncher.IsPlayableFile(file.Path))
                    {
                        files.Add(file.Path);
                    }
                }

                if (files.Count > 0)
                {
                    EnqueueFiles(files, playNow: false);
                    RefreshAll();
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.RootGrid_Drop", caught);
            }
            finally
            {
                deferral.Complete();
            }
        }

        // =====================================================================
        // 基础设施
        // =====================================================================

        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow", caught);
            }
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
            {
                return t.ToString(@"h\:mm\:ss");
            }

            return t.ToString(@"mm\:ss");
        }

        private void ResizeToDips(int widthDip, int heightDip)
        {
            Safe(() =>
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                double scale = dpi / 96.0;
                AppWindow.Resize(new SizeInt32(
                    (int)Math.Round(widthDip * scale),
                    (int)Math.Round(heightDip * scale)));
            });
        }

        private void CenterOnScreen()
        {
            Safe(() =>
            {
                DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                RectInt32 work = display.WorkArea;
                int w = AppWindow.Size.Width;
                int h = AppWindow.Size.Height;
                int x = work.X + (work.Width - w) / 2;
                int y = work.Y + (work.Height - h) / 2;
                AppWindow.Move(new PointInt32(x, Math.Max(work.Y, y)));
            });
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(nint hWnd);

        /// <summary>SW_SHOWMINIMIZED：自绘最小化按钮用（系统标题栏已去）。</summary>
        private const int SwShowMinimized = 2;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        // ---- 窗口拖动：系统移动循环（冒充标题栏按下，WPF DragMove 同款原理）----

        private const uint WmNcLButtonDown = 0x00A1;
        private static readonly nint HtCaption = new(2);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

        /// <summary>屏幕坐标打包成 WM_NCLBUTTONDOWN 的 lParam（低 16 位 x、高 16 位 y）。</summary>
        private static nint MakeLParam(int x, int y)
        {
            uint ux = (uint)(x & 0xFFFF);
            uint uy = (uint)(y & 0xFFFF);
            return (nint)unchecked((long)((uy << 16) | ux));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
