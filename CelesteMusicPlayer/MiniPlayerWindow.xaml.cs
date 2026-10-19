using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 迷你播放器（2026-10-19 推倒重写版）—— 主程序的一条横向小条（480×118 固定）。
    ///
    /// 铁律 = **单向数据流**：本窗口一行播放逻辑都没有，也不认识 MainWindow 的任何字段。
    ///   · 状态：订阅 <see cref="MiniPlayerBridge.StateChanged"/>，来什么画什么；
    ///   · 命令：全部走 <see cref="MiniPlayerBridge"/> 的几个静态方法，由主窗口去执行。
    /// 旧版（已删除，备份 _celeste_mini_removed_20261019）反过来抓着主窗口 27 处内部方法，
    /// 主窗口动一点它就出错 —— 用户说的"很多 bug"就是这么来的。
    ///
    /// 功能（用户 10-19 点单）：播放/暂停、上一首/下一首、可拖进度条、双击回主窗口、
    /// 始终置顶、音量、播放队列、一行歌词。底色三选（设置 MiniPlayerBackground，
    /// 左上角「底」字按钮循环切换）：封面压暗 / 固定深色 / 毛玻璃。
    ///
    /// 移动窗口 = 按住空白处拖：轮询定时器自己读光标挪窗（零散播放窗口同款定案配方，
    /// 本机实测三条常规路都不通，详见 StartPollingDrag 注释）。
    /// </summary>
    public sealed partial class MiniPlayerWindow : Window
    {
        private bool _uiReady;
        private string _bgMode = "Cover";

        // Cover 态：封面主色压出来的纱罩渐变（取不到色兜底 82% 黑）
        private Brush? _coverTintBrush;
        private int _coverTintToken;

        // 封面加载防串台（切歌快时旧回调不许覆盖新封面）
        private int _coverToken;
        private byte[]? _shownCoverBytes;

        // 进度条拖动态（指针事件必须代码挂 + handledEventsToo，见 HookProgressSliderPointer）
        private bool _userSeeking;
        private uint _progressPointerId;
        private bool _progressOnThumb;

        // 音量条同步守卫（程序回填值时不许触发命令）
        private bool _syncingVolume;
        private bool _syncingQueueSelection;

        // 双击判定（连续两次按下、光标没动 → 回主窗口）
        private long _lastPressTick;
        private int _lastPressX;
        private int _lastPressY;

        // 自检日志只打一次（见 ApplySnapshot）
        private bool _stateLogged;

        // ----------------------------------------------------------------
        // 扩展面板（歌词 / 播放队列，2026-10-19 追加；10-19 帧率优化 + 锯齿修复重做）
        //
        // 动画 = 逐帧 **AppWindow.Resize**（窗高即可见高，面板高度同步跟住）：
        //   展开 220ms ease-out（先冲后缓）/ 收起 180ms ease-in。
        //
        // ⚠ 为什么又回到逐帧 resize（最初版实测 ~31ms/帧 ≈ 32fps，用户报卡）：
        //   当年卡的主因是 **16ms 轮询定时器**（系统计时粒度 ~15.6ms，有效帧间隔被拖到
        //   ~30ms 还抖动），不是 resize 本身。现在主驱动换成帧同步
        //   （CompositionTarget.Rendering，合成器每帧回调、与显示刷新同拍），resize 每帧
        //   一次 layout 的代价在 60fps 预算内（窗口内容少）；动画期间把重内容
        //   （歌词滚动区 / 队列列表）折叠掉，每帧只剩几个 Border 在排，帧率稳。
        // ⚠ 圆角不再用 SetWindowRgn（1 位掩码圆弧必锯齿，用户 10-19 报「四角有像素点」）：
        //   圆角归 DWM（DWMWCP_ROUND，8px@96dpi 带 AA），见 MiniPlayerWindow.Chrome.cs。
        //   区域机制已整体删除 —— 没有区域就意味着"撑窗 + 裁可见高"那套也没了，
        //   可见高直接等于窗高，动画天然正确（切页缩小也不再误判成收起）。
        // ⚠ 驱动必须**帧同步**：Rendering 真正跑满 60fps；16ms 定时器降级为**兜底**
        //   （窗口被全屏程序遮住时 Rendering 可能暂停，保证动画跑得完）。
        // ----------------------------------------------------------------
        private const int BarWindowHeightDip = 118;   // 收起态窗高（小条）
        private const int PanelGapDip = 6;            // 小条与面板之间的间隙
        private const int LyricPanelHeightDip = 204;  // 歌词面板可见高
        private const int QueuePanelHeightDip = 264;  // 队列面板可见高
        private const int PanelExpandMs = 220;
        private const int PanelCollapseMs = 180;

        /// <summary>
        /// 当前面板页：""=收起 / "Lyrics" / "Queue"。
        /// </summary>
        private string _panelMode = "";

        /// <summary>
        /// 当前「可见高」（DIP，含小条）—— 现在窗高即可见高（没有裁剪区了），
        /// 它只是动画的起点真相源： idle 时恒等于 AppWindow 高度（DIP）。
        /// </summary>
        private int _visibleHeightDip = BarWindowHeightDip;

        /// <summary>面板动画进行中（Changed 事件每帧触发，这时不要推 DWM 属性）。</summary>
        private bool _panelAnimating;

        /// <summary>动画期间重内容（歌词滚动区/队列列表）已折叠；展开过 45% 后再放出来。</summary>
        private bool _panelContentRestored;

        private int _panelAnimFromWin;
        private int _panelAnimToWin;
        /// <summary>
        /// 动画时钟（QPC，微秒级）。不能用 TickCount64：它的粒度就是系统计时粒度
        /// ~15.6ms，60fps 下算出来的进度会台阶式跳，动画看起来一抖一抖。
        /// </summary>
        private readonly System.Diagnostics.Stopwatch _panelAnimClock = new();
        private int _panelAnimFrames;
        private int _panelAnimLastAppliedWin = -1;
        private double _panelAnimLastFrameMs;
        private double _panelAnimMaxGapMs;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _panelTimer;

        // 歌词面板：源引用变了才重建行；下标变了只换两行样式（不整表刷新）
        private IReadOnlyList<LyricLine>? _lyricSource;
        private readonly List<TextBlock> _lyricRows = new();
        private int _lyricIndexShown = -2;
        private bool _lyricsSynced;
        private static readonly SolidColorBrush LyricDimBrush =
            new(Windows.UI.Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush LyricCurrentBrush =
            new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush LyricHintBrush =
            new(Windows.UI.Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));

        // 队列面板：投影用的原始标题表（内容变了才重投影 + 重绑）
        private List<string>? _queueSourceTitles;
        private List<QueueRow>? _queueRows;
        private int _queueIndexShown = -2;

        /// <summary>队列面板一行（编号列：普通行显示序号，当前行显示播放字形）。</summary>
        private sealed class QueueRow
        {
            public string MarkerText { get; init; } = "";
            public string MarkerGlyph { get; init; } = "";
            public string Title { get; init; } = "";
        }

        // 轮询式拖动
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragTimer;
        private int _dragCursorX;
        private int _dragCursorY;
        private int _dragWinX;
        private int _dragWinY;

        public MiniPlayerWindow()
        {
            InitializeComponent();
            // 与主窗口同一处置位：引擎层可能在后台线程被懒构造，取不到当前线程队列会抛
            HiFiOutputBackend.UiDispatcherQueue = this.DispatcherQueue;
            WindowIconHelper.Apply(this);
            Title = "CelesteMusicPlayer";

            // 毛玻璃背板：Frosted 态透出它（被 65% 深色纱盖住，保白字可读）
            try
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.ctor.backdrop", caught);
            }

            // 底色：设置项 MiniPlayerBackground（封面 / 深色 / 毛玻璃，默认封面）
            AppSettingsState settings = AppSettingsStore.Load();
            _bgMode = settings.MiniPlayerBackground is "Dark" or "Frosted"
                ? settings.MiniPlayerBackground
                : "Cover";
            ApplyBackgroundMode(_bgMode, persist: false);

            // 置顶初值（写回用同一个字段，两边一致）
            _alwaysOnTop = settings.MiniPlayerAlwaysOnTop;

            // 滑块指针事件：Slider 内部会把 PointerPressed/Released 标成 Handled，
            // XAML 挂的处理器收不到 → 拖动失效。必须代码挂 + handledEventsToo。
            HookProgressSliderPointer();

            // 窗口 chrome：去系统标题栏、无最大化、固定大小；关闭是自绘按钮
            var presenter = OverlappedPresenter.Create();
            presenter.IsResizable = false;
            presenter.IsMinimizable = true;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            AppWindow.SetPresenter(presenter);

            ExtendsContentIntoTitleBar = false;
            TryCollapseTitleBar();

            AppWindow.Changed += MiniAppWindow_Changed;

            ResizeToDips(480, 118);
            CenterOnScreen();

            if (Content is FrameworkElement root)
            {
                root.Loaded += (_, _) =>
                {
                    _uiReady = true;
                    ApplyWindowChromeNative();
                    ApplyTopMostState(persist: false);
                    // 开窗先拿一份当前状态画上（不用等下一个 tick）
                    ApplySnapshot(MiniPlayerBridge.Current);
                    UpdatePanelButtons();
                };
            }

            // 订阅主窗口推来的状态（同一个 UI 线程，直接画）
            MiniPlayerBridge.StateChanged += OnStateChanged;

            Closed += (_, _) =>
            {
                MiniPlayerBridge.StateChanged -= OnStateChanged;
                _dragTimer?.Stop();
                // 面板动画两条驱动都摘掉：Rendering 是静态事件，不退订会泄漏 +
                // 闭窗后事件还在打（摸已释放的 XAML）；定时器留着也会继续步进。
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPanelRendering;
                _panelTimer?.Stop();
                RemoveWindowSubclassIfNeeded();
            };
        }

        // =====================================================================
        // 状态应用（来什么画什么）
        // =====================================================================

        private void OnStateChanged(MiniPlayerSnapshot snap)
        {
            // 订阅就是在 UI 线程（主窗口的 tick 推过来的），直接画
            ApplySnapshot(snap);
        }

        private void ApplySnapshot(MiniPlayerSnapshot snap)
        {
            if (!_uiReady || snap == null)
            {
                return;
            }

            // 一次性自检日志：第一条带歌曲信息的推送落屏时打一行（之后每 200ms 都推，不打）。
            // 排查"迷你条开了但是不显示歌名/歌词"时看这行在不在、值对不对。
            if (!_stateLogged && !string.IsNullOrEmpty(snap.Title) && snap.Title != "未在播放")
            {
                _stateLogged = true;
                StartupLog.Write(
                    $"迷你播放器状态已应用 标题={snap.Title} 艺术家={snap.Artist} " +
                    $"歌词={(string.IsNullOrEmpty(snap.LyricLine) ? "无" : "有")} " +
                    $"队列={snap.QueueTitles.Count} 时长={snap.DurationSeconds:F0}s 播放中={snap.IsPlaying}");
            }

            Safe(() =>
            {
                TitleText.Text = string.IsNullOrWhiteSpace(snap.Title) ? "未在播放" : snap.Title;
                ArtistText.Text = snap.Artist ?? "";
                LyricText.Text = snap.LyricLine ?? "";
                PlayPauseIcon.Glyph = snap.IsPlaying ? "\uE769" : "\uE768";

                // 封面：主窗口已解码好的 ImageSource 直接复用（迷你不再自己解码一遍）
                if (!ReferenceEquals(CoverImage.Source, snap.Cover))
                {
                    CoverImage.Source = snap.Cover;
                }

                // 封面字节变了才重算背景模糊 + 取色（切歌才动，不每 tick 跑）。
                // ⚠ 不能只比引用：没有内嵌封面时主窗口每次现抽一份新数组，引用恒变。
                //   按内容比（长度 + 首/中/尾各一字节），同一首歌不重复算。
                if (!SameCoverBytes(_shownCoverBytes, snap.CoverBytes))
                {
                    _shownCoverBytes = snap.CoverBytes;
                    _ = LoadCoverBackgroundAsync(snap.CoverBytes);
                }

                // 进度（用户正在拖的时候不抢）
                double total = snap.DurationSeconds;
                if (total <= 0 || double.IsNaN(total))
                {
                    ProgressSlider.Maximum = 100;
                    if (!_userSeeking)
                    {
                        ProgressSlider.Value = 0;
                        CurrentTimeText.Text = "--:--";
                        TotalTimeText.Text = "--:--";
                    }
                }
                else
                {
                    double pos = Math.Clamp(snap.PositionSeconds, 0, total);
                    ProgressSlider.Maximum = total;
                    if (!_userSeeking)
                    {
                        ProgressSlider.Value = pos;
                        CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(pos));
                    }
                    TotalTimeText.Text = FormatTime(TimeSpan.FromSeconds(total));
                }

                // 音量条回填（ Flyout 开没开都同步，守卫防命令回灌）
                _syncingVolume = true;
                try
                {
                    double vol = Math.Clamp(snap.Volume, 0, 100);
                    if (Math.Abs(VolumeSlider.Value - vol) >= 0.5)
                    {
                        VolumeSlider.Value = vol;
                    }
                    VolumeValueText.Text = ((int)Math.Round(vol)).ToString(CultureInfo.CurrentCulture);
                }
                finally
                {
                    _syncingVolume = false;
                }

                // ---- 扩展面板：歌词 ----
                // 源引用变了（切歌）才整表重建；下标变了只换两行样式 + 滚动，不整表刷新。
                if (!ReferenceEquals(_lyricSource, snap.LyricLines))
                {
                    _lyricSource = snap.LyricLines;
                    RebuildLyricRows(snap.LyricLines);
                }

                if (snap.LyricIndex != _lyricIndexShown)
                {
                    _lyricIndexShown = snap.LyricIndex;
                    UpdateLyricHighlight(snap.LyricIndex);
                    if (_panelMode == "Lyrics")
                    {
                        ScrollLyricToCurrent(smooth: true);
                    }
                }

                // ---- 扩展面板：播放队列 ----
                // 开着队列页才刷（内容或当前项变了才重投影，避免打断用户点选）。
                if (_panelMode == "Queue"
                    && (_queueSourceTitles == null
                        || !ListsEqual(_queueSourceTitles, snap.QueueTitles)
                        || snap.QueueIndex != _queueIndexShown))
                {
                    _queueSourceTitles = new List<string>(snap.QueueTitles);
                    _queueIndexShown = snap.QueueIndex;
                    RebuildQueueRows(snap.QueueTitles, snap.QueueIndex);
                    PanelTitleText.Text = "播放队列（" + snap.QueueTitles.Count + "）";
                }
            });
        }

        private static bool ListsEqual(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 两份封面字节是否"同一份"（长度 + 首/中/尾各比一字节，够判换歌又不必全量比）。
        /// 没有内嵌封面时主窗口每次现抽新数组，引用恒变但内容相同 —— 按内容比才不会每 tick 重算。
        /// </summary>
        private static bool SameCoverBytes(byte[]? a, byte[]? b)
        {
            bool aEmpty = a is not { Length: > 0 };
            bool bEmpty = b is not { Length: > 0 };
            if (aEmpty || bEmpty)
            {
                return aEmpty && bEmpty;
            }

            if (a!.Length != b!.Length)
            {
                return false;
            }

            int mid = a.Length / 2;
            return a[0] == b[0] && a[mid] == b[mid] && a[^1] == b[^1];
        }

        // =====================================================================
        // Cover 态背景：封面模糊铺底 + 主色压暗纱罩（与零散播放窗口同一套口径）
        // =====================================================================

        /// <summary>换歌时把封面模糊版铺到背景（Cover 态），并重算压暗纱罩。</summary>
        private async Task LoadCoverBackgroundAsync(byte[]? bytes)
        {
            int token = ++_coverToken;

            // 纱罩取色（没有封面 → 兜底 82% 黑，见 ApplyCoverScrim）
            Brush? brush = null;
            if (bytes is { Length: > 0 })
            {
                Windows.UI.Color? dominant = await CoverTint.AnalyzeAsync(bytes);
                if (token != _coverToken)
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

            if (token != _coverToken)
            {
                return;
            }

            _coverTintBrush = brush;
            if (_bgMode == "Cover")
            {
                ApplyCoverScrim();
            }

            // 模糊大图（与主程序整窗背景同一实现；模糊失败不影响纱罩，封面图不铺而已）
            if (bytes is { Length: > 0 } && _bgMode == "Cover")
            {
                try
                {
                    byte[]? blurred = await Task.Run(() =>
                        AlbumArtBackground.CreateHeavilyBlurredPng(bytes, workSize: 96, blurRadius: 2));
                    if (token != _coverToken)
                    {
                        return;
                    }

                    BackgroundCoverImage.Source = blurred is { Length: > 0 }
                        ? await DecodeBitmapAsync(blurred)
                        : null;
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.LoadCoverBackgroundAsync", caught);
                }
            }
            else if (_bgMode != "Cover")
            {
                BackgroundCoverImage.Source = null;
            }
        }

        private void ApplyCoverScrim()
        {
            BackgroundScrim.Fill = _coverTintBrush
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(209, 0, 0, 0)); // 82% 黑兜底
            BackgroundScrim.Opacity = 1;
        }

        /// <summary>字节 → BitmapImage（须在 UI 线程调用）。</summary>
        private static async Task<BitmapImage?> DecodeBitmapAsync(byte[] bytes)
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
                global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.DecodeBitmapAsync", caught);
                return null;
            }
        }

        // =====================================================================
        // 底色三态（封面压暗 / 深色 / 毛玻璃）
        // =====================================================================

        private void ApplyBackgroundMode(string mode, bool persist)
        {
            _bgMode = mode;
            RootGrid.RequestedTheme = ElementTheme.Dark; // 三态全是深色底，白字白条

            if (mode == "Cover")
            {
                RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                ApplyCoverScrim();
                BackgroundCoverImage.Opacity = 1;
            }
            else if (mode == "Frosted")
            {
                // 毛玻璃：RootGrid 只铺一层 65% 深色纱，透出 DesktopAcrylic 背板
                RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(165, 16, 16, 22));
                BackgroundScrim.Opacity = 0;
                BackgroundCoverImage.Opacity = 0;
                EnsureAcrylicBackdrop();
            }
            else
            {
                RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 16, 22));
                BackgroundScrim.Opacity = 0;
                BackgroundCoverImage.Opacity = 0;
            }

            PushDwmFrameColor(mode);

            if (persist)
            {
                try
                {
                    AppSettingsState s = AppSettingsStore.Load();
                    s.MiniPlayerBackground = mode;
                    AppSettingsStore.Save(s);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.ApplyBackgroundMode", caught);
                }
            }
        }

        private void BackgroundModeButton_Click(object sender, RoutedEventArgs e)
        {
            string next = _bgMode switch
            {
                "Cover" => "Dark",
                "Dark" => "Frosted",
                _ => "Cover"
            };
            ApplyBackgroundMode(next, persist: true);
        }

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
                global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.EnsureAcrylicBackdrop", caught);
            }
        }

        /// <summary>
        /// 把系统标题栏区折叠成 0 高并全透明（零散播放窗口同款配方）：
        /// ExtendsContentIntoTitleBar=true 让内容区伸进标题栏位，PreferredHeightOption=Collapsed
        /// 把该区高度压成 0，图标与系统菜单隐藏。配合构造函数里的
        /// SetBorderAndTitleBar(false,false) 一起去掉边框+标题栏。
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
                    global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.ProgressSlider_PointerPressed", caught);
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
                global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.ProgressSlider_PointerReleased", caught);
            }

            CommitSeek();
        }

        private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            CommitSeek();
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
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(ProgressSlider.Value));
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
                MiniPlayerBridge.Seek(seconds);
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(seconds));
            });
        }

        // =====================================================================
        // 传输按钮 / 音量 / 队列（全部只发命令，执行在主窗口）
        // =====================================================================

        private void PreviousButton_Click(object sender, RoutedEventArgs e) => MiniPlayerBridge.Previous();

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => MiniPlayerBridge.TogglePlayPause();

        private void NextButton_Click(object sender, RoutedEventArgs e) => MiniPlayerBridge.Next();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_uiReady || _syncingVolume)
            {
                return;
            }

            VolumeValueText.Text = ((int)Math.Round(VolumeSlider.Value)).ToString(CultureInfo.CurrentCulture);
            MiniPlayerBridge.SetVolume(VolumeSlider.Value);
        }

        private void LyricsButton_Click(object sender, RoutedEventArgs e) => TogglePanel("Lyrics");

        private void QueueButton_Click(object sender, RoutedEventArgs e) => TogglePanel("Queue");

        private void CollapsePanelButton_Click(object sender, RoutedEventArgs e) => CollapsePanel();

        /// <summary>点小条上的「词/队」：同一页再点一次 = 收起；点另一页 = 切页（都带动画）。</summary>
        private void TogglePanel(string mode)
        {
            if (_panelMode == mode)
            {
                CollapsePanel();
                return;
            }

            ExpandPanel(mode);
        }

        private void PanelQueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingQueueSelection || !_uiReady)
            {
                return;
            }

            int index = PanelQueueList.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            MiniPlayerBridge.PlayQueueAt(index);
        }

        // =====================================================================
        // 扩展面板（歌词 / 播放队列）—— 2026-10-19 追加；10-19 帧率优化 + 锯齿修复重做
        //
        // 动画 = 帧同步驱动（CompositionTarget.Rendering 主 + 16ms 定时器兜底）逐帧
        //   AppWindow.Resize：展开 220ms ease-out cubic（先冲后缓）/ 收起 180ms ease-in quad。
        // 每帧窗高 = 可见高，面板高度同步跟住；内容淡入淡出由 PanelSurface.Opacity 承担，
        // 重内容（歌词滚动区/队列列表）动画期间折叠、过半再放出来保帧率。
        // 圆角归 DWM（DWMWCP_ROUND，8px@96dpi 带 AA）—— SetWindowRgn 1 位掩码圆弧必锯齿，
        // 已整体删除，见 MiniPlayerWindow.Chrome.cs。
        // =====================================================================

        /// <summary>该页面对应的展开态窗高（DIP）。</summary>
        private static int PanelWindowHeight(string mode) => mode switch
        {
            "Lyrics" => BarWindowHeightDip + PanelGapDip + LyricPanelHeightDip,
            "Queue" => BarWindowHeightDip + PanelGapDip + QueuePanelHeightDip,
            _ => BarWindowHeightDip,
        };

        private void ExpandPanel(string mode)
        {
            Safe(() =>
            {
                _panelMode = mode;
                ExpansionPanel.Visibility = Visibility.Visible;
                PanelSurface.Opacity = 0;

                // 页内容就位 + 立刻画一份当前状态（不用等下一个 200ms 的 tick）
                PreparePanelContent(mode);
                ApplySnapshot(MiniPlayerBridge.Current);
                UpdatePanelButtons();

                // 动画期间重内容（歌词滚动区 / 队列列表）先折叠：每帧只剩面板壳在排，
                // 这是逐帧 resize 下稳住 60fps 的关键；展开过 45% 后由动画放出来
                // （那会儿 Opacity 已经起来，看不出切换）。
                _panelContentRestored = false;
                SetPanelContentVisible(false);

                // 逐帧 resize：窗口从当前高长到目标全高，面板高度每帧跟住（见 StepPanelAnimation）
                _panelAnimating = true;

                int from = _visibleHeightDip;
                int to = PanelWindowHeight(mode);
                StartupLog.Write(
                    "迷你播放器扩展区打开 模式=" + (mode == "Lyrics" ? "歌词" : "队列") +
                    $" 窗高 {from}→{to} 歌词行={_lyricRows.Count}");
                StartPanelAnimation(from, to);
            });
        }

        private void CollapsePanel()
        {
            Safe(() =>
            {
                if (_panelMode == "" && ExpansionPanel.Visibility == Visibility.Collapsed && !_panelAnimating)
                {
                    return;
                }

                StartupLog.Write("迷你播放器扩展区收起");
                // 收起 = 逐帧把窗口缩回 118（先上锁，Changed 每帧触发时别推 DWM 属性）
                _panelAnimating = true;
                StartPanelAnimation(_visibleHeightDip, BarWindowHeightDip);
            });
        }

        /// <summary>切页标题 + 两个视图的显隐 + 内容预热（队列页立即投影一份）。</summary>
        private void PreparePanelContent(string mode)
        {
            MiniPlayerSnapshot snap = MiniPlayerBridge.Current;
            bool lyrics = mode == "Lyrics";
            PanelTitleText.Text = lyrics ? "歌词" : "播放队列（" + snap.QueueTitles.Count + "）";
            LyricsScroll.Visibility = lyrics ? Visibility.Visible : Visibility.Collapsed;
            PanelQueueList.Visibility = lyrics ? Visibility.Collapsed : Visibility.Visible;

            if (!lyrics)
            {
                _queueSourceTitles = new List<string>(snap.QueueTitles);
                _queueIndexShown = snap.QueueIndex;
                RebuildQueueRows(snap.QueueTitles, snap.QueueIndex);
            }
        }

        private void StartPanelAnimation(int fromWin, int toWin)
        {
            _panelAnimFromWin = fromWin;
            _panelAnimToWin = toWin;
            _panelAnimFrames = 0;
            _panelAnimLastAppliedWin = -1;
            _panelAnimLastFrameMs = 0;
            _panelAnimMaxGapMs = 0;
            _panelAnimClock.Restart();

            // 主驱动：合成器每帧回调（与显示刷新同拍，60fps 的关键）。
            // ⚠ Rendering 是**静态事件**：先减后加防重复订阅；窗口关闭时必须退订
            //   （否则闭窗口后事件还在打，泄漏 + 摸到已释放的 XAML）。
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPanelRendering;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnPanelRendering;

            // 兜底驱动：16ms 轮询定时器。窗口被全屏程序遮住时 Rendering 可能暂停，
            // 定时器保证动画一定跑得完。两步进都是幂等的（按时钟算状态，重复触发无害）。
            _panelTimer ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _panelTimer.Stop();
            _panelTimer.Interval = TimeSpan.FromMilliseconds(16);
            _panelTimer.Tick -= OnPanelTimerTick;
            _panelTimer.Tick += OnPanelTimerTick;
            _panelTimer.Start();

            // 立刻走一帧，不用等第一个事件到来
            StepPanelAnimation();
        }

        private void OnPanelRendering(object sender, object e) => StepPanelAnimation();

        private void OnPanelTimerTick(object? sender, object? args) => StepPanelAnimation();

        /// <summary>
        /// 动画步进（两条驱动共用，幂等）：按时钟算当前窗高，resize 窗口 + 面板高度同步 +
        /// 淡入淡出。只数"画面真的动了"的帧 —— 两条驱动都可能触发，按去重后的窗高统计，
        /// 平均值才反映真实帧率（否则会被兜底定时器注水）。
        /// </summary>
        private void StepPanelAnimation()
        {
            // ⚠ 判据用"目标是不是收起态"，不是"to 是否大于 from"：切页时窗高可能变小
            //   （队列 388 → 歌词 328），那也是展开语义。旧版按方向判，把切页缩小误判成
            //   收起，动画一结束面板直接被摘掉 —— 队列页点「词」面板会整个消失。
            bool collapsing = _panelAnimToWin <= BarWindowHeightDip;
            int durationMs = collapsing ? PanelCollapseMs : PanelExpandMs;
            double elapsed = _panelAnimClock.Elapsed.TotalMilliseconds;
            double t = Math.Clamp(elapsed / durationMs, 0, 1);
            // 展开先冲后缓（ease-out cubic），收起先缓后快（ease-in quad）
            double eased = collapsing ? t * t : 1 - Math.Pow(1 - t, 3);

            int win = (int)Math.Round(_panelAnimFromWin + (_panelAnimToWin - _panelAnimFromWin) * eased);
            int lo = Math.Min(_panelAnimFromWin, _panelAnimToWin);
            int hi = Math.Max(_panelAnimFromWin, _panelAnimToWin);
            win = Math.Clamp(win, lo, hi);

            if (win != _panelAnimLastAppliedWin)
            {
                _panelAnimFrames++;
                double gap = elapsed - _panelAnimLastFrameMs;
                if (_panelAnimLastAppliedWin >= 0 && gap > _panelAnimMaxGapMs)
                {
                    _panelAnimMaxGapMs = gap;
                }
                _panelAnimLastFrameMs = elapsed;
                _panelAnimLastAppliedWin = win;
            }

            _visibleHeightDip = win;

            // 每帧三件事：resize 窗口到当前高、面板高度跟住、面板淡入淡出。
            // 重内容已折叠（SetPanelContentVisible），每帧只剩面板壳参与布局。
            ResizeToDips(480, win);
            ExpansionPanel.Height = Math.Max(0, win - BarWindowHeightDip - PanelGapDip);

            // 内容随进度淡入 / 淡出（展开到 45% 之后才显，收起前 1/3 就隐）
            double vis = collapsing
                ? Math.Clamp(t / 0.35, 0, 1)
                : Math.Clamp((t - 0.45) / 0.55, 0, 1);
            PanelSurface.Opacity = vis;

            // 重内容显隐与 Opacity 对齐：展开过 45% 放出来（那之前面板还是透明的），
            // 收起淡出到 0 就折起来（之后面板不可见，折不折看不出来）。
            if (collapsing)
            {
                if (vis <= 0 && _panelContentRestored)
                {
                    _panelContentRestored = false;
                    SetPanelContentVisible(false);
                }
            }
            else if (vis > 0 && !_panelContentRestored)
            {
                _panelContentRestored = true;
                SetPanelContentVisible(true);
            }

            if (t >= 1)
            {
                // 摘两条驱动（静态事件必须退订，否则泄漏）
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnPanelRendering;
                _panelTimer?.Stop();
                _panelAnimating = false;

                if (collapsing)
                {
                    // 收尾：窗高本帧已收回 118，面板摘掉不占布局
                    ExpansionPanel.Visibility = Visibility.Collapsed;
                    _panelMode = "";
                    UpdatePanelButtons();
                    _visibleHeightDip = BarWindowHeightDip;
                }
                else
                {
                    // 展开/切页收尾：内容放出来，等最终布局落定再把当前行/当前项滚进视野
                    _panelContentRestored = true;
                    SetPanelContentVisible(true);
                    if (_panelMode == "Lyrics")
                    {
                        RunAfterPanelLayout(() => ScrollLyricToCurrent(smooth: false));
                    }
                    else
                    {
                        RunAfterPanelLayout(ScrollQueueToCurrent);
                    }
                }

                // 帧率验收行：帧数 / 平均帧间隔 / 最大帧间隔（不逐帧打日志，省每帧文件 I/O）
                double avgGap = _panelAnimFrames > 1
                    ? elapsed / (_panelAnimFrames - 1)
                    : elapsed;
                StartupLog.Write(
                    "迷你播放器扩展区动画完成 模式=" +
                    (_panelMode == "" ? "收起" : _panelMode == "Lyrics" ? "歌词" : "队列") +
                    $" 可见高={win} 帧数={_panelAnimFrames} 总时长={elapsed:F0}ms " +
                    $"平均帧间隔={avgGap:F1}ms 最大帧间隔={_panelAnimMaxGapMs:F1}ms");
            }
        }

        /// <summary>
        /// 动画期间重内容的显隐（歌词滚动区 / 队列列表二选一，另一页 PreparePanelContent
        /// 已经折好了）。折叠 = 不参与布局，每帧只剩面板壳；显隐时机与 PanelSurface.Opacity
        /// 对齐，用户看不出切换。
        /// </summary>
        private void SetPanelContentVisible(bool visible)
        {
            Visibility v = visible ? Visibility.Visible : Visibility.Collapsed;
            if (_panelMode == "Lyrics")
            {
                LyricsScroll.Visibility = v;
            }
            else if (_panelMode == "Queue")
            {
                PanelQueueList.Visibility = v;
            }
        }

        /// <summary>小条上两个面板按钮的选中态 + 提示语（开着哪页就把哪个点亮）。</summary>
        private void UpdatePanelButtons()
        {
            Safe(() =>
            {
                var active = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
                var idle = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
                LyricsButton.Background = _panelMode == "Lyrics" ? active : idle;
                QueueButton.Background = _panelMode == "Queue" ? active : idle;
                ToolTipService.SetToolTip(LyricsButton, _panelMode == "Lyrics" ? "收起歌词面板" : "歌词面板");
                ToolTipService.SetToolTip(QueueButton, _panelMode == "Queue" ? "收起播放队列面板" : "播放队列面板");
            });
        }

        // ----------------------------------------------------------------
        // 歌词面板内容
        // ----------------------------------------------------------------

        private void RebuildLyricRows(IReadOnlyList<LyricLine>? lines)
        {
            Safe(() =>
            {
                LyricsRows.Children.Clear();
                _lyricRows.Clear();
                _lyricsSynced = false;

                if (lines is { Count: > 0 })
                {
                    foreach (LyricLine line in lines)
                    {
                        if (line.Time > TimeSpan.Zero)
                        {
                            _lyricsSynced = true;
                            break;
                        }
                    }
                }

                if (lines == null || lines.Count == 0)
                {
                    // 没有歌词：给一行提示（占位行，不高亮、不跳转）
                    TextBlock hint = NewLyricRow("（暂无歌词）", LyricHintBrush, 12.5, current: false);
                    LyricsRows.Children.Add(hint);
                    _lyricRows.Add(hint);
                }
                else
                {
                    for (int i = 0; i < lines.Count; i++)
                    {
                        TextBlock row = NewLyricRow(lines[i].Text ?? "", LyricDimBrush, 12.5, current: false);
                        if (_lyricsSynced)
                        {
                            int index = i; // 点一行 → 跳到该句（只有带时间戳的歌词才挂）
                            row.Tapped += (_, _) =>
                            {
                                TimeSpan time = lines[index].Time;
                                StartupLog.Write(
                                    $"迷你播放器歌词面板点击跳转 行={index} 时间={time.ToString(@"mm\:ss")}");
                                MiniPlayerBridge.Seek(time.TotalSeconds);
                            };
                        }

                        LyricsRows.Children.Add(row);
                        _lyricRows.Add(row);
                    }
                }

                _lyricIndexShown = -2; // 强制下一次高亮刷新
                StartupLog.Write($"迷你播放器歌词面板重建 行数={_lyricRows.Count} 可跳转={_lyricsSynced}");
            });
        }

        private static TextBlock NewLyricRow(string text, Brush brush, double fontSize, bool current)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = brush,
                FontWeight = current
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 2, 0),
            };
        }

        /// <summary>高亮当前行（只动旧/新两行，不整表刷样式）。</summary>
        private void UpdateLyricHighlight(int index)
        {
            Safe(() =>
            {
                if (_lyricIndexShown >= 0 && _lyricIndexShown < _lyricRows.Count)
                {
                    ApplyLyricRowStyle(_lyricRows[_lyricIndexShown], current: false);
                }

                if (index >= 0 && index < _lyricRows.Count)
                {
                    ApplyLyricRowStyle(_lyricRows[index], current: true);
                }
            });
        }

        private static void ApplyLyricRowStyle(TextBlock row, bool current)
        {
            row.Foreground = current ? LyricCurrentBrush : LyricDimBrush;
            row.FontSize = current ? 14 : 12.5;
            row.FontWeight = current
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }

        /// <summary>把当前歌词行滚到视口上 1/3 处（下面留出看接下来的行）。</summary>
        private void ScrollLyricToCurrent(bool smooth)
        {
            // 动画期间歌词区是折叠的：量不到真实位置，跳过（动画收尾会补滚一次）
            if (LyricsScroll.Visibility != Visibility.Visible)
            {
                return;
            }

            int index = _lyricIndexShown;
            if (index < 0 || index >= _lyricRows.Count)
            {
                return;
            }

            Safe(() =>
            {
                FrameworkElement row = _lyricRows[index];
                GeneralTransform transform = row.TransformToVisual(LyricsScroll);
                Windows.Foundation.Point top = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                double target = top.Y + LyricsScroll.VerticalOffset - LyricsScroll.ViewportHeight / 3.0;
                LyricsScroll.ChangeView(null, Math.Max(0, target), null, !smooth);
            });
        }

        /// <summary>
        /// 内容刚放出来还没有布局：等一帧量到真实位置再执行（否则滚了个寂寞）。
        /// LayoutUpdated 每次布局都会触发，进一次就摘（只认接下来第一帧）。
        /// </summary>
        private void RunAfterPanelLayout(Action action)
        {
            void OnLayout(object? s, object e)
            {
                ExpansionPanel.LayoutUpdated -= OnLayout;
                action();
            }

            ExpansionPanel.LayoutUpdated += OnLayout;
        }

        // ----------------------------------------------------------------
        // 队列面板内容
        // ----------------------------------------------------------------

        private void RebuildQueueRows(List<string> titles, int currentIndex)
        {
            Safe(() =>
            {
                var rows = new List<QueueRow>(titles.Count);
                for (int i = 0; i < titles.Count; i++)
                {
                    bool current = i == currentIndex;
                    rows.Add(new QueueRow
                    {
                        // 普通行显示序号；当前行显示播放字形（E768，程序里在用的字形）
                        MarkerText = current ? "" : (i + 1).ToString(CultureInfo.CurrentCulture),
                        MarkerGlyph = current ? "\uE768" : "",
                        Title = titles[i] ?? "",
                    });
                }

                PanelQueueList.ItemsSource = rows;
                _queueRows = rows;

                // 当前项滚进视野（只滚不选中：选中会触发 SelectionChanged 命令）。
                // 动画期间列表是折叠的：滚不动，交给动画收尾的 ScrollQueueToCurrent 补。
                if (currentIndex >= 0 && currentIndex < rows.Count
                    && PanelQueueList.Visibility == Visibility.Visible)
                {
                    PanelQueueList.ScrollIntoView(rows[currentIndex]);
                }
            });
        }

        /// <summary>把当前播放项滚进视野（动画收尾、内容刚放出来时调）。</summary>
        private void ScrollQueueToCurrent()
        {
            if (_queueRows == null || PanelQueueList.Visibility != Visibility.Visible)
            {
                return;
            }

            int index = _queueIndexShown;
            if (index >= 0 && index < _queueRows.Count)
            {
                PanelQueueList.ScrollIntoView(_queueRows[index]);
            }
        }

        // =====================================================================
        // 始终置顶
        // =====================================================================

        private bool _alwaysOnTop = true;

        private void TopMostButton_Click(object sender, RoutedEventArgs e)
        {
            _alwaysOnTop = !_alwaysOnTop;
            ApplyTopMostState(persist: true);
        }

        private void ApplyTopMostState(bool persist)
        {
            Safe(() =>
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = _alwaysOnTop;
                }

                // 钉住 = 实心钉字形；取消 = 开口钉字形
                TopMostIcon.Glyph = _alwaysOnTop ? "\uE718" : "\uE77F";
                ToolTipService.SetToolTip(TopMostButton, _alwaysOnTop ? "取消始终置顶" : "始终置顶");
            });

            if (persist)
            {
                try
                {
                    AppSettingsState s = AppSettingsStore.Load();
                    s.MiniPlayerAlwaysOnTop = _alwaysOnTop;
                    AppSettingsStore.Save(s);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.ApplyTopMostState", caught);
                }
            }
        }

        // =====================================================================
        // 窗口拖动（空白处按住拖；按钮/滑条/列表自带处理不会起拖）
        //
        // 2026-10-19 零散播放窗口连报两次「没有区域可以拖动」后定案的配方：
        // 按下那一下进了处理器就够了，之后每帧自己读光标、自己挪窗，松手自己停。
        // 三条常规路在那窗口全不通（PointerMoved 收不到 / SendMessage 冒充标题栏没人理 /
        // SetCapture 到顶层窗口窗不动），这里照抄同一套，别再改回事件跟踪。
        //
        // 双击（连按两次、光标没动）= 把主窗口叫到前台（用户点单功能）。
        // =====================================================================

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 面板内部一律不起拖、不判双击：滚动 / 点歌词行 / 点队列项 / 点收起都归面板自己。
            // ⚠ 必须整棵上溯认面板：歌词行埋在 ScrollViewer→Presenter→StackPanel→TextBlock
            //   四层以上，IsControlElement 只查 3 层查不到，会把点歌词行误判成拖窗。
            if (e.OriginalSource is DependencyObject panelSrc && IsInsideExpansionPanel(panelSrc))
            {
                return;
            }

            // 命中任何交互控件都不起拖，否则点按钮会变成拖窗
            if (e.OriginalSource is DependencyObject src && IsControlElement(src))
            {
                return;
            }

            // 双击判定：两次按下间隔 < 350ms 且光标没动 → 回主窗口
            // （不依赖 DoubleTapped 事件，本类窗口的输入路由以"按下一定能进"为准）
            long now = Environment.TickCount64;
            if (GetCursorPos(out POINT cursor))
            {
                if (now - _lastPressTick < 350
                    && Math.Abs(cursor.X - _lastPressX) < 6
                    && Math.Abs(cursor.Y - _lastPressY) < 6)
                {
                    _lastPressTick = 0;
                    MiniPlayerBridge.BringMainWindowToFront();
                    return;
                }

                _lastPressTick = now;
                _lastPressX = cursor.X;
                _lastPressY = cursor.Y;
            }

            StartPollingDrag();
        }

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
            // 左键松开了就收工（不依赖 PointerReleased，双保险）
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
        /// 命中控件就不起拖。只能往上查**有限层数**（3 层）：
        /// 无限上溯会追到窗口内容根的滚动容器，把顶栏判成控件（零散窗口实测教训），
        /// 只查 3 层够覆盖"点在图标/文字上、父级才是按钮"。
        /// </summary>
        private static bool IsControlElement(DependencyObject src)
        {
            const int MaxDepth = 3;

            DependencyObject? node = src;
            int depth = 0;
            while (node != null && depth <= MaxDepth)
            {
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

        /// <summary>
        /// 按下点是否落在扩展面板里（整棵上溯认 <see cref="ExpansionPanel"/>，不限层数）。
        /// 与 <see cref="IsControlElement"/> 的 3 层限制是两个用途：那个防"点按钮变拖窗"，
        /// 这个防"点歌词行/队列项变拖窗"——面板里的行容器埋得深，3 层查不到。
        /// </summary>
        private bool IsInsideExpansionPanel(DependencyObject? node)
        {
            int depth = 0;
            while (node != null && depth < 32)
            {
                if (ReferenceEquals(node, ExpansionPanel))
                {
                    return true;
                }

                node = VisualTreeHelper.GetParent(node);
                depth++;
            }

            return false;
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
                global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow", caught);
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

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
