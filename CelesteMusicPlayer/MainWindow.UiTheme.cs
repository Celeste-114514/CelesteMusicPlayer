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
using Windows.Graphics.Imaging;
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
        private void MainWindow_Loaded(dynamic sender, dynamic e)
        {
            StartupLog.Write("MainWindow_Loaded begin");
            try
            {
                InitializePlayerAndTimers();
                // The web shell is the sole product UI; legacy controls remain the backing session.
                OpenWebShell();
                ApplyTaskbarPlayerPreference();
                // 主题系统合闸（皮肤系统第 3 步）：启动时把设置里选的主题文件应用到界面。
                // 内部有「同名不重复应用」守卫，设置页后续变更再调也无额外开销。
                ReloadThemeFromSettings();
                // 任务栏缩略图按钮：必须在 Loaded 后注册，任务栏按钮就绪时才生效。
                // 立即调一次 Add()（首次准备 + 第一次尝试 AddButtons），
                // 然后启动一个短间隔 pump timer 让 ITaskbarThumbnailButtons.Pump() 推动延迟重试，
                // 覆盖 Explorer 任务栏图标 loaded→redraw 的临界窗口（~3 秒）。
                _taskbarButtons?.Add();
                StartThumbnailButtonsPump();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MainWindow_Loaded", ex);
            }
            finally
            {
                StartupLog.Write("MainWindow_Loaded end");
                // 2026-10-07 内存排查：窗口渲染层就绪后的基线。
                // 与 OnLaunched 那行相减 = WinUI 渲染 + 控件树 + 播放器的固定开销。
                MemProbe.Log("MainWindow已加载");
            }
        }

        // （2026-10-31 第十八刀：缩略图按钮泵三件已随 NativeTaskbar.cs 域整体转活）

        private void ApplyStartupOverlayWindows()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            if (settings.OpenDesktopLyricsOnStartup)
            {
                SetDesktopLyricsEnabled(true, persistPreference: false);
            }

            // 开机自动打开迷你播放器：随迷你播放器一起移除（2026-10-19 删掉重写）
        }


        /// <summary>窗口小于最小尺寸时强制涨回（1200×760，按 DPI 换算）。
        /// 实际通过 WM_GETMINMAXINFO 系统级锁定最小尺寸，拖到最小后不能再缩小。</summary>
        private void EnforceMinimumWindowSize()
        {
            if (_mainWindowHwnd != IntPtr.Zero)
            {
                SetupMinSizeHooks();
            }
        }


        private bool _windowChromeConfigured;

        private void MainWindow_FirstActivated(object sender, WindowActivatedEventArgs args)
        {
            Activated -= MainWindow_FirstActivated;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    // 设置开了「启动时打开迷你播放器」时，首次激活后把迷你条拉起来
                    OpenMiniPlayerIfStartupEnabled();

                    // 订阅主题色变化事件(统一刷新强调元素)
                    ThemeColorService.ThemeColorChanged -= OnThemeColorChanged;
                    ThemeColorService.ThemeColorChanged += OnThemeColorChanged;

                    // 音量条自绘:尺寸变化/初始绘制/交互
                    if (VolumeStyleCanvas != null)
                    {
                        VolumeStyleCanvas.SizeChanged -= VolumeStyleCanvas_SizeChanged;
                        VolumeStyleCanvas.SizeChanged += VolumeStyleCanvas_SizeChanged;
                        VolumeStyleCanvas.PointerPressed -= VolumeStyleCanvas_PointerPressed;
                        VolumeStyleCanvas.PointerPressed += VolumeStyleCanvas_PointerPressed;
                        VolumeStyleCanvas.PointerMoved -= VolumeStyleCanvas_PointerMoved;
                        VolumeStyleCanvas.PointerMoved += VolumeStyleCanvas_PointerMoved;
                        VolumeStyleCanvas.PointerReleased -= VolumeStyleCanvas_PointerReleased;
                        VolumeStyleCanvas.PointerReleased += VolumeStyleCanvas_PointerReleased;
                        DrawVolumeStyle();
                    }

                    // 缓存波形主题色(信息卡频谱用)
                    try
                    {
                        // 缓存波形主题色(信息卡频谱用)。走 ResolveAccentColor 而不是直接读 CurrentAccent，
                        _waveAccentColor = ResolveAccentColor();
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                    // 进度条悬停提示:分:秒格式(替代默认秒数)
                    try
                    {
                        if (ProgressSlider != null)
                        {
                            ProgressSlider.ThumbToolTipValueConverter = new SecondsToTimeSpanConverter();
                        }
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                    // 自定义背景图片
                    try
                    {
                        ApplyCustomBackground(AppSettingsStore.Load().CustomBackgroundPath);
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                    // 播放列表列显隐/密度
                    try
                    {
                        ApplyPlaylistColumnSettings(AppSettingsStore.Load());
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                    // 进度条样式:启动时读取设置(否则默认显示系统进度条)
                    try
                    {
                        _waveformProgress = AppSettingsStore.Load().WaveformProgress;
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                    // 进度条画布尺寸变化时重绘(首次布局/窗口缩放)
                    if (ProgressStyleCanvas != null)
                    {
                        ProgressStyleCanvas.SizeChanged -= ProgressStyleCanvas_SizeChanged;
                        ProgressStyleCanvas.SizeChanged += ProgressStyleCanvas_SizeChanged;
                        RedrawProgressStyle();
                    }

                    // 2026-10-16 用户反馈：「刚启动程序时进度条没对齐（还是靠右），手动调一下
                    // 窗口大小就对齐了」。根因=**布局尚未落定就量了位置**：
                    // CenterOnElement 用 TransformToVisual 量播放按钮的中心，而启动时
                    // 「按钮组先居中 → 播放按钮位置才确定」是分两帧发生的，第一次量到的是
                    // 按钮组还没摆正时的过渡值；之后没有事件再触发重算，于是就一直错着，
                    // 直到用户拖窗口触发 SizeChanged 才补上。
                    //
                    // ⚠️ 第一版修法是挂 LayoutUpdated 每次布局都校 —— 结果**进度条和按钮持续
                    // 快速抖动**（改 Margin → 触发新一轮布局 → 再校，来回颤），已废弃。
                    // 现在的做法=只在**启动后**排几次延迟校正（200/400/600/800ms），
                    // 覆盖布局落定的那几帧；运行期一律交给 SizeChanged，不再常驻校正。
                    ScheduleStartupTransportCentering();

                    if (PlayPauseButton != null)
                    {
                        PlayPauseButton.SizeChanged -= BottomTransportBar_SizeChanged;
                        PlayPauseButton.SizeChanged += BottomTransportBar_SizeChanged;
                    }

                    // 设置窗口里改「波形配色 / 白度」时即时重画进度条，用户当场看到效果（不必切歌或重启）
                    AppSettingsStore.Changed -= OnSettingsChangedForProgressStyle;
                    AppSettingsStore.Changed += OnSettingsChangedForProgressStyle;

                    // 界面风格热切换（经典家族内互切不弹重启窗）：设置页保存后立刻套用，
                    // 用户切「测试皮肤」当场就能和经典对比，不用重启。DispatcherQueue 中转与
                    // NowPlayingLayout 同一惯例——设置可能从其他窗口保存，不假设在 UI 线程。
                    AppSettingsStore.Changed -= OnSettingsChangedForUiStyle;
                    AppSettingsStore.Changed += OnSettingsChangedForUiStyle;

                    // 启动即为波形模式:加载选中/第一首歌曲的波形预览(媒体库恢复完成后重试)
                    TryLoadWaveformPreview();
                    _ = RetryWaveformPreviewLaterAsync();
                    _playlist.CollectionChanged -= OnPlaylistForWaveformPreview;
                    _playlist.CollectionChanged += OnPlaylistForWaveformPreview;

                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            });
            if (_windowChromeConfigured)
            {
                return;
            }

            _windowChromeConfigured = true;
            StartupLog.Write("MainWindow_FirstActivated");
            try
            {
                ApplyUiStyleMode(AppSettingsStore.Load()); // 含经典界面分支；现有模式回落到 TryApplySystemBackdrop
                ConfigureWindowChrome();
                MakeWindowBorderless(); // 显示后再强制一次无边框，避免 WinUI 重设 caption 样式
                ApplyWindowCorners(true); // 无边框窗口四角圆角
                ApplyBorderColorFromUiTint(); // 任务栏缩略图/窗口描边与 UI 底色一致，去掉"主题色框"
                if (_mainWindowHwnd != IntPtr.Zero)
                {
                    SetWindowPos(_mainWindowHwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);
                }

                StartupLog.Write("Window chrome configured");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ConfigureWindowChrome", ex);
            }

            // 运行标记已经在 App.OnLaunched 里写过（建窗口之前写，XAML 解析阶段崩掉也能留下痕迹），
            // 这里只取结论，不再重复写一遍。
            bool unclean = AppSettingsStore.WasUncleanExitLastTime;

            // 「上次可能异常退出」不再弹原生对话框：那条已经交给网页的启动恢复页
            // （MainWindow.WebRecovery.cs + recovery.js），能给出诊断、安全模式、重载界面。
            // 原生弹窗弹在一个刚被藏起来的主窗口上，用户根本看不见 —— 之前等于什么都没说。
            if (unclean) StartupLog.Write("[recovery] 检测到上次未正常退出，交由网页恢复页处理。");

            // 设置文件损坏恢复提示：仅本次会话提示一次。
            // 恢复页也会带一句同样的说明，同时出现就是同一件事说两遍，所以让给恢复页。
            if (AppSettingsStore.SettingsWereRecovered && !Recovery.ShouldShow())
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await ShowErrorAsync("设置文件已损坏", "设置文件曾损坏，已自动备份恢复为默认设置。\n（备份文件位于设置目录的 .corrupt-* 文件）");
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                });
            }
        }


        /// <summary>应用图标 + 标题栏与内容区合并（系统按钮浮在背景上）</summary>
        private void ConfigureWindowChrome()
        {
            try
            {
                ExtendsContentIntoTitleBar = true;
                SetTitleBar(AppTitleBar); // 自定义标题栏区域可拖动
                MakeWindowBorderless(); // 无边框 + 自绘系统按钮；保留 WS_THICKFRAME 使窗口可自由调节大小
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ExtendsContentIntoTitleBar", ex);
            }

            try
            {
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    AppWindowTitleBar titleBar = AppWindow.TitleBar;
                    titleBar.ButtonBackgroundColor = Colors.Transparent;
                    titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                    titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
                    titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
                    titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
                    titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
                    titleBar.ButtonHoverForegroundColor = Colors.White;
                    titleBar.ButtonPressedForegroundColor = Colors.White;
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TitleBar colors", ex);
            }

            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    AppWindow.SetIcon(iconPath);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 注：以前这里会把 Assets\AppIcon.png 塞进标题栏图标（非打包模式下 ms-appx:/// 不可用）。
            // 现在标题栏图标改成 XAML 里的矢量线稿（圆圈 + 音符），不再需要这张 png，故移除。
        }


        // 极客界面已移除：原 UiStyleGeek 常量与 IsGeekUiStyleActive 保留在 Utilities.cs，恒为 false。
       
        /// <summary>
        /// 最近一次 ApplyUiStyleMode 应用过的模式。设置页保存时对照它：
        /// 经典家族内互切（Classic*/Token）走热切换不弹重启窗；涉极客/图景才提示重启。
        /// </summary>
        private string? _lastAppliedUiStyleMode;

        /// <summary>
        /// 应用界面风格（设置项「界面风格」）：
        /// "" = 现有背景图式（一切照旧，唯一入口回落到毛玻璃开关）；
        /// ClassicSystem / ClassicLight / ClassicDark = 经典不透明界面：
        ///   明暗主题按选项/系统切换，背板关闭、背景图层（封面背景/自定义图片/视频）全部隐藏，
        ///   面板由 FrostedGlass 总闸统一换成不透明纯色，侧栏分类图标变为彩色；
        private void ApplyUiStyleMode(AppSettingsState settings)
        {
            try
            {
                // 毛玻璃开关先同步给 FrostedGlass：经典 / 极客风格下它决定窗口背板与根面板底色
                FrostedGlass.BackdropEnabled = settings.EnableFrostedGlass;

                string mode = settings.UiStyleMode;
                bool classic = mode is "ClassicSystem" or "ClassicLight" or "ClassicDark";
                // 2026-10-31：极客皮肤已整体移除（用户拍板：新外壳不做极客皮肤）。
                // 老存档里的 "Geek" 由 AppSettingsStore 归一到 ClassicSystem，这里不再有分支。
                bool opaque = classic;

                // 记录本次应用的模式：设置页保存时对照它判断要不要热切换（经典家族内互切不弹重启窗）
                _lastAppliedUiStyleMode = mode;

                bool darkClassic = mode == "ClassicDark"
                    || (mode is "ClassicSystem"
                        && Application.Current.RequestedTheme == ElementTheme.Dark);

                if (Content is FrameworkElement root)
                {
                    root.RequestedTheme = !opaque
                        ? ElementTheme.Default
                        : darkClassic ? ElementTheme.Dark : ElementTheme.Light;
                }

                if (opaque)
                {
                    // 只存归一化后的 "Light" / "Dark"。
                    // 早先这里存的是 "ClassicLight" / "ClassicDark"，和 FrostedGlass 内部判断的字符串对不上，
                    // 结果经典浅色界面下面板被一律涂成深灰 —— 也就是"浅色模式里一片深色底"的根因。
                    FrostedGlass.ClassicMode = darkClassic ? "Dark" : "Light";

                    // 背板是否走毛玻璃由「外观设置」开关决定，经典 / 极客风格下同样生效：
                    // 开 = 窗口背板走亚克力透出壁纸（此时不能给不透明底，否则会把毛玻璃盖住）；
                    // 关 = 窗口给纯色底。面板卡片始终不透明，与本开关无关。
                    if (settings.EnableFrostedGlass)
                    {
                        RootShell.ClearValue(Grid.BackgroundProperty);
                        FrostedGlass.ApplyWindowBackdrop(this);
                    }
                    else
                    {
                        SystemBackdrop = null;
                        Color rootBg = darkClassic
                                    ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                                    : Windows.UI.Color.FromArgb(255, 243, 243, 243);
                        RootShell.Background = new SolidColorBrush(rootBg);
                    }

                    // 多选行底色缓存的取色依赖界面基色，切风格必须失效重算
                    _cachedMultiSelectFrostBrush = null;

                    // 背景图层全部隐藏（含视频暂停）
                    if (AlbumArtBackgroundImage != null)
                    {
                        AlbumArtBackgroundImage.Source = null;
                        AlbumArtBackgroundImage.Visibility = Visibility.Collapsed;
                    }

                    if (AlbumArtBackgroundScrim != null)
                    {
                        AlbumArtBackgroundScrim.Opacity = 0;
                    }

                    if (CustomBackgroundImage != null)
                    {
                        CustomBackgroundImage.Source = null;
                        CustomBackgroundImage.Visibility = Visibility.Collapsed;
                    }

                    StopCustomBackgroundVideo();
                    StopBackgroundMotion();

                    // 侧栏分类图标：经典界面专属的彩色 emoji 图标 + 分组线（极客界面保持单色，终端风不上彩）
                    ApplyClassicNavIconColors(classic: classic);
                    ApplyNavIconStyle(classic: classic);

                    // 面板底色/选中样式全部按新主题重刷（顺序：先信息卡底色，再依赖它取色的胶囊/表头）
                    ApplyNowPlayingCardChrome();
                    ApplyArtistSongsFrostChrome();
                    ApplyCapsuleSortButtonStyle(accent: true);
                    ApplyPlaylistHeaderChipStyle();
                    UpdateLibraryNavHighlight();
                    RefreshPlaylistSelectionChrome();
                    RefreshArtistTrackSelectionChrome();
                    ApplyBorderColorFromUiTint();

                    // 主窗口系统按钮（最小化/最大化/关闭）跟着经典主题换色：浅色界面下配深色按钮，
                    // 否则沿用背景图式的浅灰配色会在浅底上"隐身"
                    FrostedGlass.ApplyWindowTheme(this);

                    StartupLog.Write("界面风格：经典模式已应用 " + mode);
                }
                else
                {
                    FrostedGlass.ClassicMode = null;
                    RootShell.ClearValue(Grid.BackgroundProperty);
                    ApplyClassicNavIconColors(classic: false);
                    ApplyNavIconStyle(classic: false);
                    _cachedMultiSelectFrostBrush = null;

                    // 现有界面：一切照旧（毛玻璃开关 + 背景图层恢复）
                    ApplyFrostedGlassPreference(settings.EnableFrostedGlass);
                    ApplyCustomBackground(settings.CustomBackgroundPath);
                    ApplyNowPlayingCardChrome();
                    ApplyArtistSongsFrostChrome();
                    ApplyCapsuleSortButtonStyle(accent: true);
                    ApplyPlaylistHeaderChipStyle();
                    UpdateLibraryNavHighlight();
                    RefreshPlaylistSelectionChrome();
                    RefreshArtistTrackSelectionChrome();
                    ApplyBorderColorFromUiTint();

                    // 从经典切回来时，把系统按钮恢复成背景图式原本的浅灰配色
                    //（经典浅色界面会把它们改成深色，不还原的话回到深色背景上会看不见）
                    try
                    {
                        if (AppWindowTitleBar.IsCustomizationSupported())
                        {
                            AppWindowTitleBar titleBar = AppWindow.TitleBar;
                            titleBar.ButtonBackgroundColor = Colors.Transparent;
                            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
                            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
                            titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
                            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
                            titleBar.ButtonHoverForegroundColor = Colors.White;
                            titleBar.ButtonPressedForegroundColor = Colors.White;
                        }
                    }
                    catch (Exception caught) { StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }

                    StartupLog.Write("界面风格：背景图式已恢复");
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }
        }

        /// <summary>
        /// 设置保存后的界面风格热切换：模式真变了才重跑 ApplyUiStyleMode。
        /// 经典家族内互切（Classic*/Token）由此实现"不重启当场对比"；涉极客/图景的
        /// 切换照样热应用，设置页那边的重启提示逻辑见 SettingsWindow.xaml.cs。
        /// </summary>
        private void OnSettingsChangedForUiStyle()
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var settings = AppSettingsStore.Load();
                        if (string.Equals(settings.UiStyleMode, _lastAppliedUiStyleMode, StringComparison.Ordinal))
                        {
                            return;
                        }
                        ApplyUiStyleMode(settings);
                    }
                    catch (Exception caught)
                    {
                        StartupLog.WriteException("MainWindow.OnSettingsChangedForUiStyle", caught);
                    }
                });
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.OnSettingsChangedForUiStyle", caught);
            }
        }

        // =====================================================================
        // 极客界面模式已于 2026-10-31 整体移除（用户拍板：新外壳不做极客皮肤）。
        // 原实现（等宽字体 / 直角 / 顶栏读数 / 磷光色 / 终端表格）整块删除，
        // 老存档的 "Geek" 由 AppSettingsStore 归一到 ClassicSystem。
        // =====================================================================

        /// <summary>行选中款与容器圆角软化已整体搬到 MainWindow.SelectionChromeHost.cs（W13 第二十二刀）。</summary>

        /// <summary>经典界面专属：侧栏分类按钮图标按类别上彩色（媒体播放器风格）；classic=false 时还原主题前景色。</summary>
        private void ApplyClassicNavIconColors(bool classic)
        {
            try
            {
                (Microsoft.UI.Xaml.Controls.Button button, Windows.UI.Color color)[] items =
                {
                    (NavSongsButton, Windows.UI.Color.FromArgb(255, 0, 120, 212)),      // 歌曲：蓝
                    (NavAlbumsButton, Windows.UI.Color.FromArgb(255, 135, 100, 184)),   // 专辑：紫
                    (NavArtistsButton, Windows.UI.Color.FromArgb(255, 227, 0, 140)),    // 歌手：品红
                    (NavAlbumArtistsButton, Windows.UI.Color.FromArgb(255, 194, 57, 179)), // 专辑歌手
                    (NavFavoritesButton, Windows.UI.Color.FromArgb(255, 247, 99, 12)),  // 收藏：橙
                    (NavRatingsButton, Windows.UI.Color.FromArgb(255, 234, 163, 0)),    // 评分：金黄
                    (NavRecentButton, Windows.UI.Color.FromArgb(255, 0, 183, 195)),     // 最近：青
                    (NavPlaylistWallButton, Windows.UI.Color.FromArgb(255, 16, 124, 16)), // 歌单墙：绿
                    (NavGenreButton, Windows.UI.Color.FromArgb(255, 3, 131, 135)),      // 流派
                    (NavYearButton, Windows.UI.Color.FromArgb(255, 73, 130, 5)),        // 年代
                    (NavMostPlayedButton, Windows.UI.Color.FromArgb(255, 202, 80, 16)), // 最常播放
                    (NavFoldersButton, Windows.UI.Color.FromArgb(255, 116, 77, 169)),   // 文件夹
                    (NavAudioFxButton, Windows.UI.Color.FromArgb(255, 0, 153, 188)),    // 音效
                    (UserPlaylistNavButton, Windows.UI.Color.FromArgb(255, 152, 111, 11)), // 自建歌单
                };

                foreach ((Microsoft.UI.Xaml.Controls.Button button, Windows.UI.Color color) in items)
                {
                    Microsoft.UI.Xaml.Controls.FontIcon? icon = FindFirstFontIcon(button);
                    if (icon == null)
                    {
                        continue;
                    }

                    // 纯色图标模式（设置里关掉 emoji）下经典模式也保持字形跟随文字单色，
                    // 不再涂类别彩色——那就是图景界面的观感。emoji 开启时才上类别彩色。
                    if (classic && AppSettingsStore.Load().NavEmojiIcons)
                    {
                        icon.Foreground = new SolidColorBrush(color);
                    }
                    else
                    {
                        icon.ClearValue(Microsoft.UI.Xaml.Controls.FontIcon.ForegroundProperty);
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }
        }

        private static Microsoft.UI.Xaml.Controls.FontIcon? FindFirstFontIcon(DependencyObject? root)
        {
            if (root == null)
            {
                return null;
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Microsoft.UI.Xaml.Controls.FontIcon icon)
                {
                    return icon;
                }

                Microsoft.UI.Xaml.Controls.FontIcon? nested = FindFirstFontIcon(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        /// <summary>经典界面的"选中"底色：半透明灰（深色主题用浅灰、浅色主题用灰），随明暗自动切。</summary>
        internal static Brush ResolveClassicRowSelectionBrush(FrameworkElement? anchor)
            => new SolidColorBrush(ResolveClassicRowSelectionColor(anchor));

        /// <summary>行选中款与容器圆角软化已整体搬到 MainWindow.SelectionChromeHost.cs（W13 第二十二刀）。</summary>


        // =====================================================================
        // 左侧导航条目：图标（经典 = 彩色 emoji / 背景图式 = 原单色字形）、
        // 选中高亮（圆角灰底 + 左侧直竖条，切换带淡入动画）、收起时只留图标
        // =====================================================================

        // （2026-10-31 第十九刀：NavItemRef 条目模型 + NavItems 懒构建已转活 MainWindow.NavHost.cs）
        private string? _lastAnimatedNavTag;

        /// <summary>
        /// 遍历按钮内容里的元素。刻意先走 Panel.Children：这些 TextBlock/FontIcon 是 XAML 里直接写在
        /// Button.Content 下的，页面还没布局时 VisualTreeHelper 取不到，Panel.Children 一定取得到。
        /// </summary>
        private static IEnumerable<DependencyObject> WalkNavContent(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                DependencyObject node = pending.Pop();
                yield return node;

                if (node is Panel panel)
                {
                    foreach (UIElement child in panel.Children)
                    {
                        pending.Push(child);
                    }
                }
                else
                {
                    int count = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < count; i++)
                    {
                        pending.Push(VisualTreeHelper.GetChild(node, i));
                    }
                }
            }
        }

        /// <summary>
        /// 侧栏分类图标显隐（2026-10-11 用户要求改为设置开关「启用 emoji 分类图标」）：
        /// 开 = 彩色 emoji；关 = 纯色 Fluent 字形（与背景图式同一套，字形跟随文字单色）。
        /// 经典（浅色/深色/跟随系统）与背景图式都按开关走；**极客皮肤不读这个开关** ——
        /// 终端风保持单色，字符键 / 白色图形图标由「极客按键图标」单独决定。
        /// </summary>
        private void ApplyNavIconStyle(bool classic)
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                bool emojiOn = settings.NavEmojiIcons;

                foreach (NavItemRef item in NavItems)
                {
                    bool useEmoji = emojiOn && item.Emoji != null;
                    if (item.Glyph != null)
                    {
                        item.Glyph.Visibility = useEmoji ? Visibility.Collapsed : Visibility.Visible;
                    }

                    if (item.Emoji != null)
                    {
                        item.Emoji.Visibility = useEmoji ? Visibility.Visible : Visibility.Collapsed;
                    }
                }

                if (LibraryNavInnerDivider != null)
                {
                    LibraryNavInnerDivider.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception caught) { StartupLog.WriteException("MainWindow.UiTheme.cs.ApplyNavIconStyle", caught); }
        }

        /// <summary>
        /// 选中条动画：圆角底淡入 + 左侧竖条淡入并"长"出来，观感对齐设置页的左侧导航。
        /// 先把属性写成终态再让动画从 From 播过去 —— 万一动画起不来，界面也是对的。
        /// </summary>
        private void AnimateNavSelection(NavItemRef item, bool animate)
        {
            Border? indicator = item.Indicator;
            if (indicator == null)
            {
                return;
            }

            // 先把终态写死：动画只是"从 From 播到终态"，动画起不来界面也是对的
            indicator.Opacity = 1;
            if (indicator.RenderTransform is ScaleTransform settled)
            {
                settled.ScaleY = 1;
            }

            if (!animate)
            {
                return;
            }

            try
            {
                BeginNavAnimation(indicator, 180, 1);

                // 竖条"长出来"：动 ScaleY（不碰 Height，避免触发无谓的布局重算）
                if (indicator.RenderTransform is ScaleTransform scale)
                {
                    BeginNavAnimation(scale, 200, 1, property: "ScaleY", from: 0.35);
                }

                // 圆角底淡入
                if (item.Button.Background is SolidColorBrush { Opacity: < 1 } pill)
                {
                    BeginNavAnimation(pill, 170, 1);
                }
            }
            catch (Exception caught) { StartupLog.WriteException("MainWindow.UiTheme.cs.AnimateNavSelection", caught); }
        }

        /// <summary>单个双精度属性的淡入动画（代码构造的 Storyboard，目标用对象引用而不是名字）。</summary>
        private static void BeginNavAnimation(
            DependencyObject target,
            int milliseconds,
            double to,
            string property = "Opacity",
            double? from = 0)
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
                }
            };

            if (from.HasValue)
            {
                animation.From = from.Value;
            }

            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, target);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        // 注：原 SetLibraryNavHiddenForNowPlaying（播放页展开时隐藏左侧音乐库面板）已于
        // 2026-10-16 按用户要求删除——播放页覆盖层收窄为只盖右列（Grid.Column="1"），
        // 左栏常显，展开/收起不再需要切换它的 Visibility。


        // 左侧音乐库面板的收起/展开功能已按用户要求移除（面板固定展开）。

        /// <summary>把无边框窗口的 DWM 边框/标题栏描边色设为当前 UI 底色，
        /// 消除任务栏缩略图/窗口四周跟随系统强调色的"主题色框"。</summary>
        private void ApplyBorderColorFromUiTint()
        {
            if (_mainWindowHwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                // COLORREF = ABGR（DWMWA_BORDER_COLOR=34 / DWMWA_CAPTION_COLOR=35）
                int color = ColorHelper.MakeColorRef(ResolveUiBaseTintColor());
                int hwndColor = unchecked((int)(uint)color);
                DwmSetWindowAttributeInt(_mainWindowHwnd, 34, ref hwndColor, 4);
                DwmSetWindowAttributeInt(_mainWindowHwnd, 35, ref hwndColor, 4);
                StartupLog.Write("任务栏/窗口描边色已匹配 UI 底色 ABGR=" + color.ToString("X8"));
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ApplyBorderColorFromUiTint", ex);
            }
        }


        /// <summary>
        /// 排序按钮统一为操场形胶囊（高 32、圆角 16 = 两头半圆），底色为系统主题色。
        /// </summary>
        private void ApplyCapsuleSortButtonStyle(bool accent)
        {
            const double height = 32;
            // 排序按钮：经典/图景保持胶囊（半高圆角）。这是所有排序按钮的总入口，
            // 改这里一次覆盖全部复用点。只改形状，填充色不变。
            var capsule = new CornerRadius(height / 2.0); // 半高等于半径 → 两头圆、中间直

            // 排序相关按钮始终使用主题色；accent 参数保留以兼容旧调用
            Brush background = ResolveAccentBrush();
            Brush foreground = ResolveAccentForegroundBrush();

            ApplyCapsuleToControl(SortFieldButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(SortOrderButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(ChangeSortButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(AlbumSortButton, height, capsule, background, foreground);
            // 专辑页「升序 / 降序」切换按钮：与 AlbumSortButton 同属专辑浏览页，之前漏加导致背景模式（非极客）下
            // 露出默认透明底 + 默认方角。统一纳入胶囊样式入口后，常态为主题色底 + 半高圆角，极客下随全局走直角。
            ApplyCapsuleToControl(AlbumSortOrderButton, height, capsule, background, foreground);
            if (ArtistSongSortButton != null)
            {
                ApplyCapsuleToControl(ArtistSongSortButton, height, capsule, background, foreground);
            }

            if (ArtistAlbumSortFieldButton != null)
            {
                ApplyCapsuleToControl(ArtistAlbumSortFieldButton, height, capsule, background, foreground);
            }

            if (ArtistAlbumSortOrderButton != null)
            {
                ApplyCapsuleToControl(ArtistAlbumSortOrderButton, height, capsule, background, foreground);
            }

            if (SelectAllMultiSelectButton != null)
            {
                Brush selectAllBg = accent ? background : ResolveCapsuleFillBrush();
                Brush? selectAllFg = accent ? foreground : null;
                ApplyCapsuleToControl(
                    SelectAllMultiSelectButton,
                    height,
                    new CornerRadius(8),
                    selectAllBg,
                    selectAllFg);
            }
        }


        private static void ApplyCapsuleToControl(
            Control control,
            double height,
            CornerRadius capsule,
            Brush background,
            Brush? foreground)
        {
            control.Height = height;
            control.MinHeight = height;
            control.CornerRadius = capsule;
            control.Background = background;
            control.BorderThickness = new Thickness(0);
            control.Padding = new Thickness(14, 0, 14, 0);
            // 防止按钮被父容器纵向拉伸导致其高度大于胶囊(32)使文字中心落到胶囊下半 → 视觉偏下；
            // 强制垂直居中对齐 + 相对父容器居中，使文字相对主题胶囊真正垂直居中。
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalContentAlignment = HorizontalAlignment.Center;
            control.VerticalContentAlignment = VerticalAlignment.Center;

            if (foreground != null)
            {
                control.Foreground = foreground;
            }
            else
            {
                control.ClearValue(Control.ForegroundProperty);
            }
        }


        private void MainWindow_Closed(dynamic sender, dynamic e)
        {
            _sourcePlaybackCancellation?.Cancel();
            _sourcePlaybackLease?.Dispose(); ReleasePreloadLease();
            // 注意：此处不再兜底保存 EQ。每次 EQ 调整已由 ApplyDspToEngine → EqCurveStore.Save 落盘；
            // 若在此用进程内 EqState.Current（启动时是平坦默认，未打开音效面板时不等于盘上值）覆盖保存，
            // 会把用户上次调好的曲线覆盖成平坦 —— 这正是“重启/再次打开后 EQ 还原”的根因。
            // 迷你播放器桥接摘除：主窗口没了，别再往已关的窗口上推状态
            MiniPlayerBridge.Detach(this);
            PersistDesktopLyricPosition();
            // WebView2 桌面歌词是独立窗口：主窗口关了必须一起收，否则进程被它吊着不退出。
            try
            {
                if (_desktopLyricsWindow != null)
                {
                    WebDesktopLyricsOverlay lyrics = _desktopLyricsWindow;
                    _desktopLyricsWindow = null;
                    lyrics.ClosedByUser -= OnDesktopLyricsClosedByUser;
                    lyrics.Dispose();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.CloseDesktopLyrics", caught); }
            _taskbarProgress?.Dispose();
            _taskbarProgress = null;

            // 背景视频播放器：关窗即释放解码资源
            try
            {
                StopBackgroundMotion();
                // 2026-10-07：先 Close 源再 Dispose 播放器——MediaPlayer.Dispose() 不会替我们
                //   释放 MediaSource 的解码器（引用计数还有别人），不 Close 等于漏一份。
                CloseBackgroundVideoSource();
                _backgroundVideoPlayer?.Dispose();
                _backgroundVideoPlayer = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }

            // 同理停掉设备监听：窗口销毁后 DeviceWatcher 的 COM 回调还会继续来，
            // 那时再去 TryEnqueue 访问已分离的 XAML 就会崩在退出阶段。
            try
            {
                AudioDeviceWatcher.Stop();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }

            try
            {
                TrackStatsStore.Flush();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 退出兜底写盘：优先用"用户最后一次主动设定的音量"（跨入口唯一真值源），
            // 没动过音量才回退到滑条当前值。避免设置页调过、主界面滑条没同步时被覆盖回默认 80。
            try
            {
                _volumeSaveTimer?.Stop();
                double sliderVolume = VolumeSlider != null ? Math.Clamp(VolumeSlider.Value, 0, 100) : _volumeToSave;
                double liveVolume = Math.Clamp(LastUserVolume >= 0 ? LastUserVolume : sliderVolume, 0, 100);
                AppSettingsStore.Update(s => s.Volume = liveVolume);
                global::CelesteMusicPlayer.StartupLog.Write($"[音量] 退出写盘 = {liveVolume:0.##}（滑条={sliderVolume:0.##}，用户设定={LastUserVolume:0.##}）");
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }

            // ★ 必须先存进度，再销毁引擎：引擎一 Dispose，播放位置就读不出来了，
            // 上次听到哪儿会被写成 0 —— 这是"续播不记进度"的元凶之一。
            PersistPlaybackSession();

            try
            {
                ResetPlaybackEngine();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 队列等其它状态再存一次（此时引擎已销毁，位置部分会自动跳过，不会覆盖上面写进去的进度）
            PersistPlaybackSession();
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }

            try
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                _taskbarButtons?.Dispose();
                _taskbarButtons = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            CloseAllChildWindows();
            DisposeMusicPlayer2Features();
        }


        private void CloseAllChildWindows()
        {
            if (_desktopLyricsWindow is { } lyrics) {
                _desktopLyricsWindow = null; _desktopLyricsEnabled = false;
                lyrics.ClosedByUser -= OnDesktopLyricsClosedByUser; lyrics.Dispose();
            }
        }


        /// <summary>
        /// 设置变更后重画进度条：波形配色（渐变/纯色）与白度都是这里读的，
        /// 不重画的话用户在设置里改完要切歌或重启才看得到。
        /// ⚠️ 回调跑在设置保存的持锁期内，这里只排回 UI 线程，不在锁里做 UI 工作。
        /// </summary>
        private void OnSettingsChangedForProgressStyle()
        {
            try
            {
                DispatcherQueue.TryEnqueue(RedrawProgressStyle);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }
        }


        /// <summary>波形(Poweramp)：波形条已播主题色/未播灰色，当前位置竖线。</summary>
        private void DrawWaveformStyle(Canvas canvas, double w, double h, double ratio, Color accent)
        {
            if (_waveformData == null || _waveformData.Length == 0)
            {
                // 波形未就绪:只画一条中性细线(加载完成后再显示真实波形,不闪占位)
                var idleLine = new Shapes.Rectangle
                {
                    Width = w,
                    Height = 2,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128))
                };
                Canvas.SetTop(idleLine, (h - 2) / 2);
                canvas.Children.Add(idleLine);
                return;
            }

            int n = _waveformData.Length;
            double barW = w / n;
            var unplayedBrush = new SolidColorBrush(Color.FromArgb(70, 150, 150, 150));
            double playedEdge = w * ratio;
            Color light = Lighten(accent, 0.55);

            // 已播部分配色：Solid = 整段统一色（白色度可调），Gradient = 左端主题色 → 右端浅色（原行为）。
            AppSettingsState waveSettings = AppSettingsStore.Load();
            bool solidMode = waveSettings.WaveColorMode == "Solid";
            // 纯色模式所有柱子同色，只建一个画刷复用（避免几百根柱子各建一个画刷）
            Brush? solidBrush = solidMode
                ? new SolidColorBrush(Lighten(accent, waveSettings.WaveSolidWhiteness))
                : null;

            for (int i = 0; i < n; i++)
            {
                double bh = Math.Max(2, _waveformData[i] * h * 0.95);
                var rect = new Shapes.Rectangle
                {
                    // 柱间留白（2026-10-14 用户要求"每条之间稍微宽一点点"）：原来是 1px 缝。
                    // ⚠ 别改太大：进度条总宽固定后 barW 本来就不大，缝一宽柱子就被压成细线
                    // （试过 2px，柱子全变成 1px 细丝）。
                    // 2026-10-16 用户反馈"每根竖线太细"：桶数从 180 降到 120（barW 由约 4 涨到 6），
                    // 缝同步从 1.5 收到 1.2，柱子净宽约 4.8px。下限提到 1.5 防止窄窗口下退化成细丝。
                    Width = Math.Max(1.5, barW - 1.2),
                    Height = bh,
                    RadiusX = 1,
                    RadiusY = 1
                };
                Canvas.SetLeft(rect, i * barW);
                Canvas.SetTop(rect, (h - bh) / 2);

                double centerX = (i + 0.5) * barW;
                if (centerX <= playedEdge)
                {
                    if (solidBrush != null)
                    {
                        // 纯色：整段一个颜色，不随播放位置变化
                        rect.Fill = solidBrush;
                    }
                    else
                    {
                        // 已播部分:主题色(两端浅色渐变)
                        double t = centerX / Math.Max(1, playedEdge);
                        rect.Fill = new SolidColorBrush(Color.FromArgb(
                            255,
                            (byte)(accent.R + (light.R - accent.R) * t),
                            (byte)(accent.G + (light.G - accent.G) * t),
                            (byte)(accent.B + (light.B - accent.B) * t)));
                    }
                }
                else
                {
                    rect.Fill = unplayedBrush;
                }

                canvas.Children.Add(rect);
            }

            // 当前位置细线(echo next 风格:波形上一条细竖线)
            var line = new Shapes.Rectangle
            {
                Width = 2,
                Height = h * 0.96,
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(Colors.White)
            };
            Canvas.SetLeft(line, Math.Clamp(playedEdge - 1, 0, Math.Max(0, w - 2)));
            Canvas.SetTop(line, (h - h * 0.96) / 2);
            canvas.Children.Add(line);
        }


        /// <summary>波形未就绪时的渐变兜底(仅波形模式内部使用)。</summary>
        private void DrawGradientFallback(Canvas canvas, double w, double h, double ratio, Color accent)
        {
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
                grad.GradientStops.Add(new GradientStop { Color = Lighten(accent, 0.55), Offset = 1 });
                fill.Fill = grad;
                Canvas.SetTop(fill, (h - 4) / 2);
                canvas.Children.Add(fill);
            }
        }




        /// <summary>自定义背景图的请求序号：连续换图时丢弃过期结果，避免旧图盖掉新图。</summary>
        private int _customBackgroundRequest;

        /// <summary>应用自定义背景图片(设置里选择)；与封面背景互斥——自定义优先。
        /// 无路径时恢复封面背景（用最近缓存的当前曲目封面）。
        /// 设置里开了「背景高斯模糊」时，自定义图片同样先做模糊再铺满窗口。</summary>
        private Windows.Media.Playback.MediaPlayer? _backgroundVideoPlayer;
        /// <summary>当前视频背景的文件路径。用于「同一路径不重复换源」的判重（2026-10-07 内存修复）。</summary>
        private string? _backgroundVideoPath;

        private static readonly string[] VideoBackgroundExtensions =
        {
            ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".ts", ".mpg", ".mpeg", ".flv"
        };

        private static bool IsVideoBackgroundPath(string path)
        {
            string ext;
            try
            {
                ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            }
            catch (Exception)
            {
                return false;
            }

            foreach (string v in VideoBackgroundExtensions)
            {
                if (v == ext) return true;
            }

            return false;
        }

        /// <summary>停掉并隐藏背景视频（切回图片背景 / 清空背景时调用）。</summary>
        private void StopCustomBackgroundVideo()
        {
            if (CustomBackgroundVideo != null)
            {
                CustomBackgroundVideo.Visibility = Visibility.Collapsed;
            }

            if (_backgroundVideoPlayer == null) return;
            try
            {
                _backgroundVideoPlayer.Pause();
                // 2026-10-07 内存暴涨修复：只把 Source 置 null 只是松掉引用，
                // 底层的 MediaSource（连同它的视频解码器与帧缓存）不会因此释放 ——
                //   必须显式 Close()，否则每换一次背景就漏一份解码资源。
                // 实测：不清的话 14 次换源把非托管内存推到 4.3GB。
                CloseBackgroundVideoSource();
                _backgroundVideoPausedByMinimize = false;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught);
            }
        }

        /// <summary>
        /// 关闭并丢弃当前的 MediaSource（若已持有）。
        ///
        /// 依据微软官方文档（MediaPlayerElement / play-audio-and-video-with-mediaplayer）：
        ///   MediaSource 是 WinRT 原生对象，靠引用计数释放。把 <c>MediaPlayer.Source</c> 置 null
        ///   **只是解除引用，不会** 释放视频解码器与帧缓存——MediaPlayerElement 仍持有它。
        ///   必须依次做：Pause → Source=null → <b>SetMediaPlayer(null)</b> → <b>mediaSource.Dispose()</b>。
        ///   注意释放方法是 Dispose()，不是 Close()（本 SDK 上 MediaSource 没有 Close）。
        ///
        /// 实测（2026-10-07）：不做这一步，14 次换背景就把非托管内存从 104MB 推到 4373MB。
        /// </summary>
        private void CloseBackgroundVideoSource()
        {
            // 先取源（置 null 后就取不到了）
            var src = _backgroundVideoPlayer?.Source;
            if (src == null)
            {
                _backgroundVideoPath = null;
                return;
            }

            try
            {
                _backgroundVideoPlayer!.Pause();
            }
            catch { /* 源可能已失效，忽略 */ }

            try
            {
                _backgroundVideoPlayer.Source = null;
                CustomBackgroundVideo.Source = null;
                // 关键一步：断开 MediaPlayerElement 与 MediaPlayer 的连接，
                //   否则元素仍持有播放器，Dispose 之后它的解码资源不会真正回收。
                CustomBackgroundVideo.SetMediaPlayer(null);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("CloseBackgroundVideoSource.detach", caught);
            }

            try
            {
                if (src is Windows.Media.Core.MediaSource ms)
                {
                    // 官方释放方式：Dispose，不是 Close（本 SDK 上没有 Close）。
                    ms.Dispose();
                    StartupLog.Write("[视频] 已 Dispose 旧 MediaSource，释放解码资源");
                }
                // IMediaPlaybackSource 接口本身不暴露 Dispose；
                //   实践中这里拿到的就是 MediaSource，上面已覆盖。
                //   真出现别的实现类型时，只能断开引用（上面已做）交由 GC/系统回收。
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("CloseBackgroundVideoSource.dispose", caught);
            }
            finally
            {
                _backgroundVideoPath = null;
            }
        }

        // =====================================================================
        // 预设背景的「缓慢移动」：用极轻微的缩放 + 平移让静态图活起来，
        // 用来替代体积大、还可能踩版权的视频背景。由合成线程驱动，几乎不吃 CPU。
        // =====================================================================

        private Microsoft.UI.Xaml.Media.Animation.Storyboard? _backgroundMotionStoryboard;
        private CompositeTransform? _backgroundMotionTransform;

        /// <summary>按设置决定是否给当前背景加/去缓慢移动（仅对内置预设生效）。</summary>
        internal void RefreshBackgroundMotion()
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                bool want = BackgroundPresetGenerator.IsKnownPreset(settings.BackgroundPreset)
                            && settings.BackgroundPresetMotion
                            && !IsOpaqueUiStyleActive();
                ApplyBackgroundMotion(want);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.RefreshBackgroundMotion", caught);
            }
        }

        private void ApplyBackgroundMotion(bool enable)
        {
            StopBackgroundMotion();
            if (!enable || CustomBackgroundImage == null)
            {
                return;
            }

            try
            {
                var transform = new CompositeTransform { CenterX = 0, CenterY = 0 };
                CustomBackgroundImage.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                CustomBackgroundImage.RenderTransform = transform;
                _backgroundMotionTransform = transform;

                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

                // 24 秒一个来回：缓慢放大到 1.06，同时轻微左右漂移，反向后回到原处。
                AddMotion(storyboard, transform, "ScaleX", 1.0, 1.06, 24);
                AddMotion(storyboard, transform, "ScaleY", 1.0, 1.06, 24);
                AddMotion(storyboard, transform, "TranslateX", -14, 14, 31);
                AddMotion(storyboard, transform, "TranslateY", -8, 8, 41);

                _backgroundMotionStoryboard = storyboard;
                storyboard.Begin();
                StartupLog.Write("背景预设：已启用缓慢移动");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ApplyBackgroundMotion", caught);
                StopBackgroundMotion();
            }
        }

        private static void AddMotion(
            Microsoft.UI.Xaml.Media.Animation.Storyboard storyboard,
            CompositeTransform transform,
            string property,
            double from,
            double to,
            double seconds)
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                AutoReverse = true,
                RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
                EnableDependentAnimation = false,
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.SineEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut
                }
            };

            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, transform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(
                animation, "CompositeTransform." + property);
            storyboard.Children.Add(animation);
        }

        private void StopBackgroundMotion()
        {
            try
            {
                _backgroundMotionStoryboard?.Stop();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.StopBackgroundMotion", caught);
            }

            _backgroundMotionStoryboard = null;
            _backgroundMotionTransform = null;

            try
            {
                if (CustomBackgroundImage != null)
                {
                    CustomBackgroundImage.RenderTransform = null;
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.StopBackgroundMotion", caught);
            }
        }

        // —— 视频背景「最小化暂停」：最小化时暂停解码省电，还原后继续 ——
        private bool _backgroundVideoPausedByMinimize;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private void OnAppWindowChangedForBackgroundVideo(AppWindow sender, AppWindowChangedEventArgs args)
        {
            // AppWindow.Changed 回调不在 UI 线程上，且最小化瞬间布局还没稳定，丢到 UI 队列里稍后判
            DispatcherQueue.TryEnqueue(UpdateBackgroundVideoMinimizeState);
        }

        private void UpdateBackgroundVideoMinimizeState()
        {
            if (_backgroundVideoPlayer == null || _backgroundVideoPlayer.Source == null)
            {
                return; // 没在放视频，不用管
            }

            try
            {
                IntPtr hwnd = _mainWindowHwnd != IntPtr.Zero
                    ? _mainWindowHwnd
                    : NativeHandle;
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                // 2026-10-07 记录：这里只覆盖【最小化】。被其它窗口完全遮住时视频仍在后台解码，
                //   属于纯浪费；但 WinUI3 没有可靠的「被遮挡」通知（IsWindowVisible 在被盖住时仍为 true），
                //   要做对需额外接 Win32 钩子（WM_WINDOWPOSCHANGED + 遮挡判定），复杂度与风险都偏高。
                //   当前取舍：先保证「看得见时才解码」（最小化已停）；
                //   后续若确认遮挡解码是主要开销，再单独加钩子，不要塞在这里草率实现。
                if (IsIconic(hwnd))
                {
                    if (!_backgroundVideoPausedByMinimize)
                    {
                        _backgroundVideoPlayer.Pause();
                        _backgroundVideoPausedByMinimize = true;
                        StartupLog.Write("背景视频：窗口最小化，已暂停解码");
                        // 2026-10-07 内存排查：暂停后视频帧缓存是否被释放（MediaPlayerElement
                        // 可能仍留着解码帧）。若这里不降，说明视频是内存大户且没回收路径。
                        MemProbe.Log("视频暂停后");
                    }
                }
                else if (_backgroundVideoPausedByMinimize
                         && CustomBackgroundVideo != null
                         && CustomBackgroundVideo.Visibility == Visibility.Visible)
                {
                    _backgroundVideoPlayer.Play();
                    _backgroundVideoPausedByMinimize = false;
                    StartupLog.Write("背景视频：窗口还原，继续播放");
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught);
            }
        }

        /// <summary>把视频铺满窗口当背景：循环播放、强制静音、不进系统媒体浮层(SMTC)。</summary>
        private void ApplyCustomBackgroundVideo(string path, int request)
        {
            if (request != _customBackgroundRequest) return;

            // 视频与图片互斥：图片层退场
            CustomBackgroundImage.Source = null;
            CustomBackgroundImage.Visibility = Visibility.Collapsed;

            try
            {
                if (_backgroundVideoPlayer == null)
                {
                    _backgroundVideoPlayer = new Windows.Media.Playback.MediaPlayer
                    {
                        IsLoopingEnabled = true,
                        IsMuted = true,
                        AutoPlay = false,
                    };
                    // 关键：不让这个播放器接管系统媒体浮层(SMTC)，否则背景视频会顶掉正歌的"正在播放"卡片
                    _backgroundVideoPlayer.CommandManager.IsEnabled = false;
                    CustomBackgroundVideo.SetMediaPlayer(_backgroundVideoPlayer);

                    // 最小化省电：窗口最小化时暂停视频解码，还原时继续
                    AppWindow.Changed -= OnAppWindowChangedForBackgroundVideo;
                    AppWindow.Changed += OnAppWindowChangedForBackgroundVideo;
                }
                else if (string.Equals(_backgroundVideoPath, path, StringComparison.OrdinalIgnoreCase)
                         && _backgroundVideoPlayer.Source != null)
                {
                    // 2026-10-07 内存暴涨修复：同一个视频被重复「应用」十几次，每次都重建 MediaSource。
                    // MediaSource / MediaPlayer 靠引用计数释放——旧的从不 Close，解码器缓存就一份份堆着。
                    // 实测 14 次调用把非托管内存从 104MB 推到 4373MB（单次跳变 +3.1GB）。
                    // 调用方在曲库刷新 / 背景重算等时机反复请求同一背景，属正常行为 → 这里识别后直接跳过换源。
                    MemProbe.Log("视频背景重复应用(同路径,已跳过换源)");
                    return;
                }

                // 换源前先彻底释放旧源（Pause → Source=null → SetMediaPlayer(null) → MediaSource.Dispose）。
                // 注意 CloseBackgroundVideoSource 里有一步 SetMediaPlayer(null)，
                //   所以之后必须重新挂一次，否则元素与播放器断开、视频不再显示。
                CloseBackgroundVideoSource();
                CustomBackgroundVideo.SetMediaPlayer(_backgroundVideoPlayer);
                _backgroundVideoPath = path;

                _backgroundVideoPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
                CustomBackgroundVideo.Visibility = Visibility.Visible;
                _backgroundVideoPlayer.Play();

                MemProbe.Log("视频背景应用后");

                global::CelesteMusicPlayer.StartupLog.Write("自定义背景：视频已应用 " + path);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught);
                CustomBackgroundVideo.Visibility = Visibility.Collapsed;
            }

            // 与封面背景互斥（同图片逻辑）
            ClearAlbumArtBackground();
        }

        /// <summary>套用主窗口背景（内置预设优先；否则用传入的自定义图片/视频路径）。</summary>
        internal void ApplyCustomBackground(string? path)
        {
            // 经典 / 极客这类不透明界面与壁纸层完全无关：设置变更广播、切歌等任何时机调用到这里，
            // 一律保持图层清空，绝不把图景壁纸铺回窗口（切回背景图式时 ApplyUiStyleMode 会重新调用本方法恢复）。
            if (IsOpaqueUiStyleActive())
            {
                // 请求号 +1：让还在飞行中的旧壁纸任务回来时自行作废
                _customBackgroundRequest++;
                CustomBackgroundImage.Source = null;
                CustomBackgroundImage.Visibility = Visibility.Collapsed;
                StopCustomBackgroundVideo();
                StopBackgroundMotion();
                ClearAlbumArtBackground();
                StartupLog.Write("背景图层：不透明界面（经典/极客）下忽略壁纸更新");
                return;
            }

            int request = ++_customBackgroundRequest;

            // 内置预设优先级高于自定义路径：选中预设时走生成器，避免空路径/不存在路径回退到封面背景。
            string preset = AppSettingsStore.Load().BackgroundPreset;
            if (BackgroundPresetGenerator.IsKnownPreset(preset))
            {
                _ = ApplyCustomBackgroundPresetAsync(preset, request);
                return;
            }

            _ = ApplyCustomBackgroundAsync(path, request);
        }

        private async System.Threading.Tasks.Task ApplyCustomBackgroundAsync(string? path, int request)
        {
            try
            {
                // 经典不透明界面与背景图层完全无关：图片/视频一律不显示（切回现有界面时由 ApplyUiStyleMode 恢复）
                if (IsClassicUiStyleActive())
                {
                    if (request != _customBackgroundRequest) return;

                    CustomBackgroundImage.Source = null;
                    CustomBackgroundImage.Visibility = Visibility.Collapsed;
                    StopCustomBackgroundVideo();
                    return;
                }

                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    if (request != _customBackgroundRequest) return;

                    CustomBackgroundImage.Source = null;
                    CustomBackgroundImage.Visibility = Visibility.Collapsed;
                    StopCustomBackgroundVideo();
                    StopBackgroundMotion();

                    // 恢复封面背景：用最近一次的当前曲目封面重绘（没有就保持空）
                    if (AlbumArtBackgroundImage != null)
                    {
                        _ = ApplyAlbumArtBackgroundAsync(_lastCoverBytes, _nowPlayingPath ?? string.Empty);
                    }
                    return;
                }

                // 视频背景：循环静音铺满，不走图片的模糊管线（那套 GDI+ 模糊只认静态图）
                if (IsVideoBackgroundPath(path))
                {
                    ApplyCustomBackgroundVideo(path, request);
                    return;
                }

                // 图片路径：先把视频层停掉（含后续图片解码失败的情况）
                StopCustomBackgroundVideo();

                AppSettingsState settings = AppSettingsStore.Load();
                int blurRadius = settings.BackgroundGaussBlur ? settings.GaussBlurRadius : 0;

                // 高斯模糊对自定义背景图同样生效（此前漏了这一步，开关只对封面背景起作用）。
                // 顺带解决另一个隐患：4000px 的手机照片直接交给 XAML 会占掉几百 MB 显存，
                // 走模糊通道后会被压到固定尺寸输出。
                byte[]? pixels = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        byte[] raw = System.IO.File.ReadAllBytes(path);
                        if (blurRadius <= 0)
                        {
                            return raw;
                        }

                        // workSize 取 192（封面背景是 96），半径同比放大，让滑块的模糊手感在两种背景上一致
                        return AlbumArtBackground.CreateHeavilyBlurredPng(raw, workSize: 192, blurRadius: blurRadius * 2);
                    }
                    catch (Exception caught)
                    {
                        global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught);
                        return null;
                    }
                });

                // 走到这里还是 null，说明 GDI+ 根本解不了这张图 —— 典型是 WebP / AVIF / HEIC：
                // WinUI 的图片控件走系统解码器（WIC），这些格式都能正常显示，
                // 但 GDI+ 只认 BMP/GIF/JPEG/PNG/TIFF，碰上就抛异常、CreateHeavilyBlurredPng 只能返回 null。
                // 旧代码这时会退回「原图直出」，于是背景照常显示、却完全不模糊
                // —— 用户看到的现象就是「自己设的背景图，高斯模糊不生效」。
                // 这里先让系统解码器把图转成 PNG，再走同一套模糊流程，把格式差异抹平。
                if (pixels == null && blurRadius > 0)
                {
                    global::CelesteMusicPlayer.StartupLog.Write(
                        "自定义背景：GDI+ 解不了码，改用系统解码器兜底 " + path);

                    byte[]? normalized = await NormalizeImageToPngAsync(path);

                    if (normalized != null)
                    {
                        pixels = await System.Threading.Tasks.Task.Run(
                            () => AlbumArtBackground.CreateHeavilyBlurredPng(normalized, workSize: 192, blurRadius: blurRadius * 2));

                        global::CelesteMusicPlayer.StartupLog.Write(
                            "自定义背景：系统解码器兜底结果=" + (pixels != null ? "模糊成功" : "仍然失败"));
                    }
                }

                if (request != _customBackgroundRequest) return;

                BitmapImage? image;
                if (pixels != null && pixels.Length > 0)
                {
                    image = await CreateBitmapFromBytesAsync(pixels);
                }
                else
                {
                    // 模糊失败（图异常/格式怪）时退回直接解码原图，至少有背景可用
                    BitmapImage fallback = new() { DecodePixelWidth = 1920 };
                    using (System.IO.FileStream fs = System.IO.File.OpenRead(path))
                    {
                        fallback.SetSource(fs.AsRandomAccessStream());
                    }

                    image = fallback;
                }

                if (image == null || request != _customBackgroundRequest) return;

                CustomBackgroundImage.Source = image;
                CustomBackgroundImage.Visibility = Visibility.Visible;

                // 互斥：自定义背景显示期间，封面背景（含压暗层）清掉
                ClearAlbumArtBackground();

                // 走自定义图片路径时不需要预设的缓慢移动
                RefreshBackgroundMotion();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }
        }


        /// <summary>
        /// 内置背景预设：调用生成器拿到缓存图片路径，再走自定义图片背景的模糊/显示流程。
        /// 失败时回退到清除自定义背景（恢复封面背景）。
        /// </summary>
        private async System.Threading.Tasks.Task ApplyCustomBackgroundPresetAsync(string preset, int request)
        {
            try
            {
                string? path = BackgroundPresetGenerator.GetPresetPath(preset);
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    StartupLog.Write($"背景预设 {preset} 生成失败，回退到封面背景");
                    await ApplyCustomBackgroundAsync(null, request);
                    return;
                }

                StopCustomBackgroundVideo();

                AppSettingsState settings = AppSettingsStore.Load();
                int blurRadius = settings.BackgroundGaussBlur ? settings.GaussBlurRadius : 0;

                byte[]? pixels = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        byte[] raw = System.IO.File.ReadAllBytes(path);
                        if (blurRadius <= 0)
                        {
                            return raw;
                        }

                        return AlbumArtBackground.CreateHeavilyBlurredPng(raw, workSize: 192, blurRadius: blurRadius * 2);
                    }
                    catch (Exception caught)
                    {
                        global::CelesteMusicPlayer.StartupLog.WriteException($"ApplyCustomBackgroundPresetAsync.{preset}", caught);
                        return null;
                    }
                });

                if (request != _customBackgroundRequest) return;

                BitmapImage? image = null;
                if (pixels != null && pixels.Length > 0)
                {
                    image = await CreateBitmapFromBytesAsync(pixels);
                }

                if (image == null)
                {
                    // 模糊失败时直出原图，至少能有背景
                    BitmapImage fallback = new() { DecodePixelWidth = 1920 };
                    using (System.IO.FileStream fs = System.IO.File.OpenRead(path))
                    {
                        fallback.SetSource(fs.AsRandomAccessStream());
                    }
                    image = fallback;
                }

                if (image == null || request != _customBackgroundRequest) return;

                CustomBackgroundImage.Source = image;
                CustomBackgroundImage.Visibility = Visibility.Visible;
                ClearAlbumArtBackground();

                // 预设背景可选「缓慢移动」，按设置起停
                RefreshBackgroundMotion();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException($"ApplyCustomBackgroundPresetAsync.{preset}", caught);
            }
        }


        /// <summary>
        /// 用系统解码器（WIC）把任意格式的图片转成 PNG 字节，好让 GDI+ 那套模糊代码能处理它。
        ///
        /// 为什么需要这一层：GDI+（System.Drawing）只认 BMP/GIF/JPEG/PNG/TIFF 这些老格式，
        /// 碰上 WebP / AVIF / HEIC 会直接抛异常；而 WinUI 的图片控件走系统解码器，
        /// 这些格式都能正常显示 —— 于是出现「背景图能显示、但模糊不生效」的怪现象。
        /// 先解码再重编码成 PNG，把格式差异抹平，后面就能复用同一套模糊代码。
        ///
        /// 解码时按长边缩到 1024：模糊前的工作尺寸只有 192，没必要让 8000px 的巨图
        /// 在内存里铺成几百 MB。顺带按 EXIF 摆正手机竖拍照片。
        /// </summary>
        private static async System.Threading.Tasks.Task<byte[]?> NormalizeImageToPngAsync(string path)
        {
            try
            {
                using System.IO.FileStream file = System.IO.File.OpenRead(path);
                using var source = file.AsRandomAccessStream();

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(source);
                if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return null;

                double scale = 1024.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight);
                if (scale > 1) scale = 1;

                BitmapTransform transform = new()
                {
                    ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                    ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                using InMemoryRandomAccessStream encoded = new();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();

                if (encoded.Size == 0) return null;

                encoded.Seek(0);
                Windows.Storage.Streams.Buffer buffer = new((uint)encoded.Size);
                await encoded.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.None);

                byte[] bytes = buffer.ToArray();
                return bytes.Length > 0 ? bytes : null;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs.NormalizeImageToPngAsync", caught);
                return null;
            }
        }


        /// <summary>主题色变化事件处理:统一刷新信息卡波形/歌词/进度条/迷你播放器/桌面歌词。</summary>
        private void OnThemeColorChanged(Windows.UI.Color accent)
        {
            try
            {
                _waveAccentColor = accent;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 音量条(自绘)/进度条
            try
            {
                DrawVolumeStyle();
                ThemeColorService.ApplySliderAccent(ProgressSlider, accent);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                TimeSpan pos = _audioEngine?.IsPlaying == true
                    ? EnginePositionValue
                    : (GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);
                SyncLyricsToPosition(pos, force: true);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                _desktopLyricsWindow?.ApplySettings(AppSettingsStore.Load());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

        }


        private static Color WaveColorFor(int index) => _waveAccentColor;

    }
}
