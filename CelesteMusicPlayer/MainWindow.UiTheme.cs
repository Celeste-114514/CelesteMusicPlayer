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
        /// <summary>设置页等子窗口用来回写主窗口状态。</summary>
        internal static MainWindow? Instance { get; private set; }


        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartupLog.Write("MainWindow_Loaded begin");
            try
            {
                InitializePlayerAndTimers();
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
            }
        }

        private DispatcherQueueTimer? _thumbPumpTimer;
        private int _thumbPumpTicks;

        private void StartThumbnailButtonsPump()
        {
            if (_thumbPumpTimer != null) return;
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(250);
            int maxTicks = 40; // ~10 秒，足以覆盖 Explorer 任务栏重绘窗口
            timer.Tick += async (s, e) =>
            {
                _thumbPumpTicks++;
                try
                {
                    if (_taskbarButtons != null)
                    {
                        await _taskbarButtons.PumpAsync();
                    }
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("thumbPump.Tick", ex);
                }
                if (_thumbPumpTicks >= maxTicks)
                {
                    timer.Stop();
                    _thumbPumpTimer = null;
                    StartupLog.Write("[thumb] Pump 已停止 ticks=" + _thumbPumpTicks);
                }
            };
            timer.Start();
            _thumbPumpTimer = timer;
            StartupLog.Write("[thumb] Pump 已启动，每 250ms 一次，最多 40 次");
        }


        private void ApplyStartupOverlayWindows()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            if (settings.OpenDesktopLyricsOnStartup)
            {
                SetDesktopLyricsEnabled(true, persistPreference: false);
            }

            if (settings.OpenMiniPlayerOnStartup)
            {
                SetMiniPlayerEnabled(true, persistPreference: false);
            }
        }


        /// <summary>按 DIP 调整窗口客户区大小（内部换算为物理像素）。</summary>
        private void ResizeWindowToDips(int widthDip, int heightDip)
        {
            try
            {
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                double scale = dpi / 96.0;
                AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Round(widthDip * scale),
                    (int)Math.Round(heightDip * scale)));
            }
            catch
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(widthDip, heightDip));
            }
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
                        _waveAccentColor = ThemeColorService.CurrentAccent;
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
                        _progressBarStyle = AppSettingsStore.Load().ProgressBarStyle;
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                    // 进度条画布尺寸变化时重绘(首次布局/窗口缩放)
                    if (ProgressStyleCanvas != null)
                    {
                        ProgressStyleCanvas.SizeChanged -= ProgressStyleCanvas_SizeChanged;
                        ProgressStyleCanvas.SizeChanged += ProgressStyleCanvas_SizeChanged;
                        RedrawProgressStyle();
                    }

                    // 设置窗口里改「波形配色 / 白度」时即时重画进度条，用户当场看到效果（不必切歌或重启）
                    AppSettingsStore.Changed -= OnSettingsChangedForProgressStyle;
                    AppSettingsStore.Changed += OnSettingsChangedForProgressStyle;

                    // 启动即为波形模式:加载选中/第一首歌曲的波形预览(媒体库恢复完成后重试)
                    TryLoadWaveformPreview();
                    _ = RetryWaveformPreviewLaterAsync();
                    _playlist.CollectionChanged -= OnPlaylistForWaveformPreview;
                    _playlist.CollectionChanged += OnPlaylistForWaveformPreview;

                    // 首次激活兜底：确保信息卡波形已绘制（无论是否播放）
                    if (WaveformCanvas != null
                        && (WaveformCanvas.Children.Count == 0 || _waveLevels.All(v => v < 0.05)))
                    {
                        for (int i = 0; i < WaveBarCount; i++)
                        {
                            _waveLevels[i] = IdleLevel(i);
                        }

                        DrawVisualSlot();
                    }
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

            // 运行标记：检测上次异常退出（崩溃/强杀残留 .running）
            AppSettingsStore.MarkAppStart();
            bool unclean = AppSettingsStore.WasUncleanExitLastTime;

            // 设置文件损坏恢复提示：仅本次会话提示一次
            if (AppSettingsStore.SettingsWereRecovered)
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
            else if (unclean)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        await ShowErrorAsync("上次可能异常退出", "检测到上次程序未正常关闭（可能崩溃或被强制结束）。\n若反复出现，请查看设置目录下的 CelesteMusicPlayer.log 排查原因。");
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


        /// <summary>当前是否处于经典不透明界面（ClassicSystem / ClassicLight / ClassicDark）。供各面板刷新处判断。</summary>
        internal static bool IsClassicUiStyleActive()
        {
            string mode;
            try
            {
                mode = AppSettingsStore.Load().UiStyleMode;
            }
            catch
            {
                return false;
            }

            return mode is "ClassicSystem" or "ClassicLight" or "ClassicDark";
        }

        /// <summary>极客界面模式的设置值（设置项「界面风格」下拉里的「极客」）。</summary>
        internal const string UiStyleGeek = "Geek";

        /// <summary>当前是否处于极客界面模式（近黑终端风：等宽字、直角、技术读数）。供各面板刷新处判断。</summary>
        internal static bool IsGeekUiStyleActive()
        {
            string mode;
            try
            {
                mode = AppSettingsStore.Load().UiStyleMode;
            }
            catch
            {
                return false;
            }

            return mode == UiStyleGeek;
        }

        /// <summary>当前是否处于「不透明风格」（经典或极客）：这两类模式下背景图层 / 封面背景 / 预设渐变一律不显示。</summary>
        internal static bool IsOpaqueUiStyleActive() => IsClassicUiStyleActive() || IsGeekUiStyleActive();

        /// <summary>
        /// 极客模式的进程内缓存（第一次问的时候读一次设置，之后沿用）。
        /// 给"每条列表行都要判断一次"的高频路径用：PlaylistItem.FormatChips 的极客包装、行分隔线等。
        /// AppSettingsStore.Load() 每次都会 Clone 一整个设置对象，逐行调用太浪费。
        /// 界面风格切换时由 ApplyUiStyleMode 立即刷新，不会读到过期值。
        /// </summary>
        private static bool? _geekUiStyleCached;

        internal static bool GeekUiStyleCached => _geekUiStyleCached ??= IsGeekUiStyleActive();

        internal static void RefreshGeekUiStyleCache(bool geek) => _geekUiStyleCached = geek;
       
        /// <summary>
        /// 应用界面风格（设置项「界面风格」）：
        /// "" = 现有背景图式（一切照旧，唯一入口回落到毛玻璃开关）；
        /// ClassicSystem / ClassicLight / ClassicDark = 经典不透明界面：
        ///   明暗主题按选项/系统切换，背板关闭、背景图层（封面背景/自定义图片/视频）全部隐藏，
        ///   面板由 FrostedGlass 总闸统一换成不透明纯色，侧栏分类图标变为彩色。
        /// </summary>
        private void ApplyUiStyleMode(AppSettingsState settings)
        {
            try
            {
                string mode = settings.UiStyleMode;
                bool classic = mode is "ClassicSystem" or "ClassicLight" or "ClassicDark";
                // 极客：与经典并列的不透明风格，但走终端那套视觉 —— 近黑底、等宽字、直角、技术读数。
                bool geek = mode == UiStyleGeek;
                bool opaque = classic || geek;

                // 高频路径（列表行）读的是这个缓存值，风格一变立刻同步
                RefreshGeekUiStyleCache(geek);

                // 外壳实际是深还是浅（"跟随系统"按系统当前主题解析；极客恒为深色）。
                // 外壳底色、FrostedGlass 面板配色都以这一个判断为准，避免"跟随系统"落在深色系统上时出现外壳深、面板浅的错配。
                bool darkClassic = geek
                    || mode == "ClassicDark"
                    || (mode == "ClassicSystem" && Application.Current.RequestedTheme == ApplicationTheme.Dark);

                if (Content is FrameworkElement root)
                {
                    root.RequestedTheme = !opaque
                        ? ElementTheme.Default
                        : darkClassic ? ElementTheme.Dark : ElementTheme.Light;
                }

                // 极客专属外观（等宽字 / 直角 / 顶栏读数）：两个分支都要跑，切走时负责还原。
                ApplyGeekChrome(geek);

                if (opaque)
                {
                    // 只存归一化后的 "Light" / "Dark"（极客存 "Geek"）。
                    // 早先这里存的是 "ClassicLight" / "ClassicDark"，和 FrostedGlass 内部判断的字符串对不上，
                    // 结果经典浅色界面下面板被一律涂成深灰 —— 也就是"浅色模式里一片深色底"的根因。
                    FrostedGlass.ClassicMode = geek ? "Geek" : darkClassic ? "Dark" : "Light";

                    // 经典界面与壁纸/背板无关：关掉 Desktop Acrylic，窗口给纯色底
                    SystemBackdrop = null;
                    RootShell.Background = new SolidColorBrush(
                        geek
                            ? Windows.UI.Color.FromArgb(255, 11, 15, 11)
                            : darkClassic
                                ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
                                : Windows.UI.Color.FromArgb(255, 243, 243, 243));

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

        // =====================================================================
        // 极客界面模式（设置「界面风格」里与图景 / 经典并列的第四种）
        // =====================================================================

        private DispatcherQueueTimer? _geekReadoutTimer;

        /// <summary>
        /// 极客界面专属外观：等宽字体 + 直角 + 顶栏实时输出读数。
        /// geek=false 时逐项还原，退出极客模式不留残留。
        /// 只动窗口根元素自己的资源字典和可视树 —— 绝不碰 Application.Current.Resources
        ///（运行时改全局系统键会崩 0xc000027b）；强调色沿用设置里的主题色，不覆盖。
        /// </summary>
        private void ApplyGeekChrome(bool geek)
        {
            try
            {
                if (Content is not FrameworkElement root)
                {
                    return;
                }

                var mono = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");

                if (geek)
                {
                    // 元素级资源覆盖（不是全局资源）：新建的控件/文本走等宽、圆角归零。
                    root.Resources["ContentControlThemeFontFamily"] = mono;
                    root.Resources["ControlCornerRadius"] = new CornerRadius(0);
                    root.Resources["OverlayCornerRadius"] = new CornerRadius(0);
                }
                else
                {
                    root.Resources.Remove("ContentControlThemeFontFamily");
                    root.Resources.Remove("ControlCornerRadius");
                    root.Resources.Remove("OverlayCornerRadius");
                }

                // 资源只影响之后新建的元素，已在树上的这一批得手动刷一遍
                ApplyFontToTree(root, geek ? mono : null);

                if (GeekOutputReadout != null)
                {
                    GeekOutputReadout.Visibility = geek ? Visibility.Visible : Visibility.Collapsed;
                }

                // 右上角工具按钮：极客下直角。PlayAll 在 XAML 里写死 CornerRadius=16 的胶囊款，
                // 主题资源盖不掉显式值，只能逐个改；非极客恢复原值（可来回切）。
                ApplyGeekButtonCorner(PlayAllLibrarySongsButton, geek, 16);
                ApplyGeekButtonCorner(SortFieldButton, geek, 4);
                ApplyGeekButtonCorner(SortOrderButton, geek, 4);
                ApplyGeekButtonCorner(ChangeSortButton, geek, 4);

                // 终端页底板配色跟模式走；播放页布局菜单也要重建（极客下只有终端一项）
                ApplyTerminalChrome();
                RebuildLayoutFlyout();

                // 右侧浏览区重塑：直角 / 细线 / 无药丸（真正把它做成终端风，而不是经典换皮）
                ApplyGeekBrowseSkin(geek);

                EnsureGeekReadoutTimer(geek);
                if (geek)
                {
                    UpdateGeekReadout();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ApplyGeekChrome", caught); }
        }

        /// <summary>极客模式按钮直角化 / 还原。normalRadius 为非极客时的圆角。</summary>
        private static void ApplyGeekButtonCorner(ButtonBase? button, bool geek, double normalRadius)
        {
            if (button == null)
            {
                return;
            }

            button.CornerRadius = geek ? new CornerRadius(0) : new CornerRadius(normalRadius);
        }

        /// <summary>把字体刷到可视树上已有的 TextBlock / Control（font=null 表示还原）。</summary>
        private static void ApplyFontToTree(DependencyObject node, Microsoft.UI.Xaml.Media.FontFamily? font)
        {
            if (node is TextBlock tb)
            {
                if (font == null)
                {
                    tb.ClearValue(TextBlock.FontFamilyProperty);
                }
                else
                {
                    tb.FontFamily = font;
                }
            }
            else if (node is Control c)
            {
                if (font == null)
                {
                    c.ClearValue(Control.FontFamilyProperty);
                }
                else
                {
                    c.FontFamily = font;
                }
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                ApplyFontToTree(VisualTreeHelper.GetChild(node, i), font);
            }
        }

        private void EnsureGeekReadoutTimer(bool enable)
        {
            if (_geekReadoutTimer == null)
            {
                DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
                if (queue == null)
                {
                    return;
                }

                _geekReadoutTimer = queue.CreateTimer();
                _geekReadoutTimer.Interval = TimeSpan.FromMilliseconds(1000);
                _geekReadoutTimer.Tick += (s, a) => UpdateGeekReadout();
            }

            if (enable)
            {
                _geekReadoutTimer.Start();
            }
            else
            {
                _geekReadoutTimer.Stop();
            }
        }

        /// <summary>顶栏那行实时读数：输出格式 → 活跃 DSP → 峰值 / 削波。全是引擎真数据。</summary>
        private void UpdateGeekReadout()
        {
            if (GeekOutputReadout == null || GeekOutputReadout.Visibility != Visibility.Visible)
            {
                return;
            }

            try
            {
                var parts = new List<string>();

                string fmt = _audioEngine?.ActualOutputFormat ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(fmt))
                {
                    parts.Add(fmt);
                }

                string[] names = { "余量", "SRC", "EQ", "OPRA", "FIR", "声道", "RG" };
                bool[] active = DspModuleActive();
                var on = new List<string>();
                for (int i = 0; i < names.Length && i < active.Length; i++)
                {
                    if (active[i])
                    {
                        on.Add(names[i]);
                    }
                }

                parts.Add(on.Count == 0 ? "bit-perfect" : "DSP " + string.Join("›", on));

                float peak = _audioEngine?.OutputPeakDbfs ?? float.NegativeInfinity;
                if (!float.IsInfinity(peak))
                {
                    parts.Add("峰值 " + peak.ToString("0.0") + " dBFS");
                }

                int clip = _audioEngine?.OutputClipCount ?? 0;
                if (clip > 0)
                {
                    parts.Add("削波 " + clip + " 次");
                }

                GeekOutputReadout.Text = string.Join("   ·   ", parts);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UpdateGeekReadout", caught); }
        }

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
                    (NavTagSortButton, Windows.UI.Color.FromArgb(255, 107, 105, 214)),  // 标签排序
                    (UserPlaylistNavButton, Windows.UI.Color.FromArgb(255, 152, 111, 11)), // 自建歌单
                };

                foreach ((Microsoft.UI.Xaml.Controls.Button button, Windows.UI.Color color) in items)
                {
                    Microsoft.UI.Xaml.Controls.FontIcon? icon = NavTagSortButton != null && ReferenceEquals(button, NavTagSortButton) && NavTagSortIcon != null
                        ? NavTagSortIcon
                        : FindFirstFontIcon(button);
                    if (icon == null)
                    {
                        continue;
                    }

                    if (classic)
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

        /// <summary>同上，返回颜色本身（导航项的底色要做淡入动画，得拿到颜色现搓画笔）。</summary>
        internal static Color ResolveClassicRowSelectionColor(FrameworkElement? anchor)
        {
            bool dark = anchor?.ActualTheme == ElementTheme.Dark;
            return dark
                ? Color.FromArgb(58, 255, 255, 255)
                : Color.FromArgb(40, 128, 128, 128);
        }

        /// <summary>
        /// 行内细节按界面风格重塑（极客化浏览面板的行级部分）：
        /// - Tag="FmtChip" 格式标签：极客下去掉药丸底/圆角/描边，变暗灰纯文本（终端曲目列表的味道）；
        /// - Tag="RowCover" 行内封面：极客下直角、去描边；
        /// - Tag 以 "RowChrome" 结尾的行底板：极客下在行底补一条 1px 表格线，整页读起来是一张数据表；
        /// - 行内其它圆角元素（选中块、标签底衬等）一并直角化。
        /// 原值一律走 GeekStore 备份，退出极客时逐项还原（不再手写"经典原值"，避免还原值和模板对不上）。
        /// 由各列表的行样式入口（ApplySongListItemSelectionChrome 等）在行实现/刷新时调用，
        /// 因此列表虚拟化晚实现的行也能被覆盖。
        /// </summary>
        internal void ApplyGeekRowDetailChrome(FrameworkElement? root)
        {
            if (root == null)
            {
                return;
            }

            if (IsGeekUiStyleActive())
            {
                ApplyGeekRowDetailChromeInner(root);
                return;
            }

            GeekRestoreSubtree(root);
        }

        /// <summary>极客行与行之间的表格线：半透明浅色细线。
        /// 之前用不透明暗绿灰(#222A22)，在选中行的半透明白底上反而比背景更暗、看着像一条黑线（用户实测反馈）；
        /// 改成低透明度白后，深底上是淡分隔、浅底上仍是淡线，任何底色都不会变黑。</summary>
        private static readonly Color GeekRowRuleColor = Color.FromArgb(22, 0xFF, 0xFF, 0xFF);

        private void ApplyGeekRowDetailChromeInner(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(node, i) is not DependencyObject child)
                {
                    continue;
                }

                if (child is Border b)
                {
                    string tag = b.Tag as string ?? string.Empty;

                    if (tag == "FmtChip")
                    {
                        GeekStore(b, Border.CornerRadiusProperty, new CornerRadius(0));
                        GeekStore(b, Border.BackgroundProperty, null);
                        GeekStore(b, Border.BorderThicknessProperty, new Thickness(0));
                        GeekStore(b, Border.PaddingProperty, new Thickness(0));
                        if (b.Child is TextBlock chipText)
                        {
                            GeekStore(chipText, TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(255, 0x8B, 0x92, 0x86)));
                            GeekStore(chipText, UIElement.OpacityProperty, 1.0);
                        }
                    }
                    else if (tag == "RowCover")
                    {
                        GeekStore(b, Border.CornerRadiusProperty, new CornerRadius(0));
                        GeekStore(b, Border.BorderThicknessProperty, new Thickness(0));
                    }
                    else
                    {
                        // 行内其它方块（选中块、评分底衬、播放中指示）一律直角
                        if (b.CornerRadius != new CornerRadius(0)
                            && !(b.Width > 0 && Math.Abs(b.Width - b.Height) < 0.5 && b.CornerRadius.TopLeft >= b.Width / 2 - 0.5))
                        {
                            GeekStore(b, Border.CornerRadiusProperty, new CornerRadius(0));
                        }
                    }

                    if (tag.EndsWith("RowChrome", StringComparison.Ordinal))
                    {
                        EnsureGeekRowRule(b);
                    }
                }

                ApplyGeekRowDetailChromeInner(child);
            }
        }

        /// <summary>给一行底板补 1px 表格线（已补过就直接返回；用 Tag="GeekRule" 认领，还原时按同 Tag 摘掉）。</summary>
        private void EnsureGeekRowRule(Border chrome)
        {
            if (chrome.Child is not Panel host)
            {
                return;
            }

            foreach (UIElement existing in host.Children)
            {
                if (existing is Border ruleBorder && ruleBorder.Tag as string == "GeekRule")
                {
                    return;
                }
            }

            Thickness padding = chrome.Padding;
            var rule = new Border
            {
                Tag = "GeekRule",
                Height = 1,
                Background = new SolidColorBrush(GeekRowRuleColor),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(-padding.Left, 0, -padding.Right, 0),
                IsHitTestVisible = false,
            };

            if (host is Grid grid)
            {
                Grid.SetRowSpan(rule, 99);
                Grid.SetColumnSpan(rule, 99);
            }

            host.Children.Add(rule);
        }

        /// <summary>
        /// 经典 / 极客界面统一的列表行选中款：左侧 3px 主题色竖条 + 半透明灰底，文字保持原色。
        /// 极客近黑底上「整行填强调色」过于突兀（用户实测反馈），所有列表统一走这一款。
        /// 返回 true 表示已按该款处理，调用方不再走「整行强调色」分支。
        /// </summary>
        internal static bool TryApplyBarStyleRowSelection(
            Border? chrome,
            bool selected,
            Brush accent,
            Brush unselectedBg)
        {
            if (chrome == null || !IsOpaqueUiStyleActive())
            {
                return false;
            }

            bool dark = chrome.ActualTheme == ElementTheme.Dark;
            chrome.BorderThickness = new Thickness(3, 0, 0, 0);

            // 条样式底色是「半透明灰」而不是强调色，文字必须显式回落到主题前景色。
            // 注意：ListViewItem/GridViewItem 默认模板的 Selected 视觉状态是用 ObjectAnimation
            // 把 ContentPresenter.Foreground 动画成白色（动画优先级高于任何本地值，压不住），
            // 所以选中时必须给行内每个 TextBlock 显式设值（ApplyForegroundToDescendants）；
            // 未选中时 Clear 让行内文字恢复 DataTemplate 各自的主题色层次。
            var rowForeground = new SolidColorBrush(dark
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(255, 0, 0, 0));

            if (selected)
            {
                chrome.Background = new SolidColorBrush(dark
                    ? Color.FromArgb(58, 255, 255, 255)
                    : Color.FromArgb(40, 128, 128, 128));
                chrome.BorderBrush = accent;
                ApplyForegroundToDescendants(chrome, rowForeground);
            }
            else
            {
                chrome.Background = unselectedBg;
                chrome.BorderBrush = new SolidColorBrush(Colors.Transparent);
                ClearForegroundOnDescendants(chrome);
            }

            return true;
        }

        // =====================================================================
        // 左侧导航条目：图标（经典 = 彩色 emoji / 背景图式 = 原单色字形）、
        // 选中高亮（圆角灰底 + 左侧直竖条，切换带淡入动画）、收起时只留图标
        // =====================================================================

        /// <summary>
        /// 一个左侧导航条目。指示条（Indicator）是按钮外面的独立 Border ——
        /// 画在按钮的左边框上会被 CornerRadius 裁成一段弧，用户反馈过"竖条是弧形"。
        /// </summary>
        private sealed class NavItemRef
        {
            public Button Button = null!;
            public Border? Indicator;
            public FrameworkElement? Glyph;   // 背景图式用的单色图标（FontIcon / Path）
            public FrameworkElement? Emoji;   // 经典模式用的彩色 emoji
            public TextBlock? Label;
            public bool IsTool;               // 音效处理 / 标签排序 / 播放列表
        }

        private List<NavItemRef>? _navItems;
        private string? _lastAnimatedNavTag;

        private List<NavItemRef> NavItems => _navItems ??= BuildNavItems();

        private List<NavItemRef> BuildNavItems()
        {
            var items = new List<NavItemRef>();
            AddNavItem(items, NavSongsButton, NavSongsIndicator, false);
            AddNavItem(items, NavAlbumsButton, NavAlbumsIndicator, false);
            AddNavItem(items, NavArtistsButton, NavArtistsIndicator, false);
            AddNavItem(items, NavAlbumArtistsButton, NavAlbumArtistsIndicator, false);
            AddNavItem(items, NavFavoritesButton, NavFavoritesIndicator, false);
            AddNavItem(items, NavRatingsButton, NavRatingsIndicator, false);
            AddNavItem(items, NavRecentButton, NavRecentIndicator, false);
            AddNavItem(items, NavPlaylistWallButton, NavPlaylistWallIndicator, false);
            AddNavItem(items, NavGenreButton, NavGenreIndicator, false);
            AddNavItem(items, NavYearButton, NavYearIndicator, false);
            AddNavItem(items, NavMostPlayedButton, NavMostPlayedIndicator, false);
            AddNavItem(items, NavFoldersButton, NavFoldersIndicator, false);
            AddNavItem(items, NavWebDavButton, NavWebDavIndicator, false);
            AddNavItem(items, NavAudioFxButton, NavAudioFxIndicator, true);
            AddNavItem(items, NavTagSortButton, NavTagSortIndicator, true);
            AddNavItem(items, UserPlaylistNavButton, UserPlaylistNavIndicator, true);

            // 自检：条目里的「字形 / emoji / 文字」是靠遍历按钮内容抓的，抓漏了会静默退化
            // （经典界面下 emoji 不显示、图标还是老的单色字形）。这里留一行日志便于核对。
            int glyph = items.Count(i => i.Glyph != null);
            int emoji = items.Count(i => i.Emoji != null);
            int label = items.Count(i => i.Label != null);
            int bar = items.Count(i => i.Indicator != null);
            StartupLog.Write($"[导航] 条目={items.Count} 字形={glyph} emoji={emoji} 文字={label} 竖条={bar}");
            return items;
        }

        private static void AddNavItem(List<NavItemRef> items, Button? button, Border? indicator, bool isTool)
        {
            if (button == null)
            {
                return;
            }

            var item = new NavItemRef { Button = button, Indicator = indicator, IsTool = isTool };

            // 竖条以自身中心缩放（"长出来"的动画用）。XAML 里不写，省得 15 个条目抄 15 遍。
            if (indicator != null)
            {
                indicator.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                indicator.RenderTransform = new ScaleTransform();
            }

            if (button.Content is DependencyObject content)
            {
                foreach (DependencyObject node in WalkNavContent(content))
                {
                    if (node is TextBlock block)
                    {
                        string? marker = block.Tag as string;
                        if (marker == "NavEmoji")
                        {
                            item.Emoji ??= block;
                        }
                        else if (marker == "NavLabel")
                        {
                            item.Label ??= block;
                        }
                    }
                    else if (item.Glyph == null && node is FontIcon or Shapes.Path)
                    {
                        item.Glyph = (FrameworkElement)node;
                    }
                }
            }

            items.Add(item);
        }

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
        /// 经典界面用彩色 emoji 图标替换原来的单色字形（用户要求"像 emoji 那样的图标"，
        /// 而不是"原有图标加个颜色"）；背景图式界面维持原样。
        /// </summary>
        private void ApplyNavIconStyle(bool classic)
        {
            try
            {
                foreach (NavItemRef item in NavItems)
                {
                    bool useEmoji = classic && item.Emoji != null;
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

        /// <summary>
        /// 播放歌曲信息页展开 / 收起时，左侧音乐库面板要一起让位：
        /// 播放页是盖在整个主内容区上的一层透明板，下面那层不退干净就会透出来（用户反馈过）。
        /// </summary>
        internal void SetLibraryNavHiddenForNowPlaying(bool hidden)
        {
            try
            {
                if (LeftCategoryGrid != null)
                {
                    LeftCategoryGrid.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            catch (Exception caught) { StartupLog.WriteException("MainWindow.UiTheme.cs.SetLibraryNavHiddenForNowPlaying", caught); }
        }


        /// <summary>行悬停底色：经典 / 极客界面用半透明灰（原浅白在浅色主题与近黑底上几乎看不见）。</summary>
        internal static Brush ResolveRowHoverBrush(FrameworkElement? anchor)
        {
            if (!IsOpaqueUiStyleActive())
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
            }

            bool dark = anchor?.ActualTheme == ElementTheme.Dark;
            return new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(34, 255, 255, 255)
                : Windows.UI.Color.FromArgb(24, 128, 128, 128));
        }

        // 左侧音乐库面板的收起/展开功能已按用户要求移除（面板固定展开）。

        private Color ResolveUiBaseTintColor()
        {
            // 经典界面：直接给出经典底色（浅 = 浅灰、深 = 炭灰）。
            // 不能走下面的候选色：浅色主题下那几个键取到的都是"接近白"被跳过，
            // 最后落到深灰回退色，浅色界面里的描边/胶囊/多选底色就会发黑。
            if (FrostedGlass.ClassicMode != null)
            {
                return FrostedGlass.ClassicMode == "Light"
                    ? Color.FromArgb(255, 243, 243, 243)
                    : Color.FromArgb(255, 32, 32, 32);
            }

            // 优先取右侧面板实际底色（用户看到的整体 UI 区域色）
            if (ColorHelper.TryGetBrushColor(NowPlayingPane?.Background, out Color paneColor)
                && paneColor.A > 0
                && !ColorHelper.IsNearWhite(paneColor))
            {
                return Color.FromArgb(255, paneColor.R, paneColor.G, paneColor.B);
            }

            FrameworkElement? anchor = Content as FrameworkElement ?? NowPlayingPaneContent;
            string[] keys =
            {
                "CardBackgroundFillColorDefault",
                "CardBackgroundFillColorDefaultBrush",
                "SolidBackgroundFillColorBase",
                "SolidBackgroundFillColorBaseBrush",
                "ApplicationPageBackgroundThemeBrush"
            };

            foreach (string key in keys)
            {
                if (ColorHelper.TryGetThemeColor(anchor, key, out Color themeColor)
                    && themeColor.A > 0
                    && !ColorHelper.IsNearWhite(themeColor))
                {
                    return Color.FromArgb(255, themeColor.R, themeColor.G, themeColor.B);
                }
            }

            // 深色 Mica / 深灰 UI 回退（勿用浅灰，否则矩形发白）
            return Color.FromArgb(255, 42, 42, 42);
        }


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
            // 极客界面：排序按钮一律直角（用户要求）；经典/图景保持原来的胶囊（半高圆角）。
            // 这是所有排序按钮的总入口，改这里一次覆盖全部复用点。只改形状，填充色不变。
            bool geek = IsGeekUiStyleActive();
            var capsule = geek ? new CornerRadius(0) : new CornerRadius(height / 2.0); // 半高等于半径 → 两头圆、中间直

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
                    geek ? new CornerRadius(0) : new CornerRadius(8),
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


        /// <summary>关闭勾选标记，并尽量给 Presenter 圆角，避免系统方角选中层。</summary>
        private static void SoftenItemPresenterCorners(DependencyObject root)
        {
            // 容器自带选中层(ListView/GridViewItemPresenter)的圆角：极客界面一律直角，
            // 否则默认模板的选中高亮始终是圆角 8 —— 之前这里写死 8，导致极客下行选中框仍圆角
            //（用户实测反馈：歌曲面板及复用该面板的多个页面都有此问题）。经典/图景保持 8。
            var presenterRadius = new CornerRadius(IsGeekUiStyleActive() ? 0 : 8);

            if (root is ListViewItemPresenter listPresenter)
            {
                listPresenter.CornerRadius = presenterRadius;
                listPresenter.SelectionCheckMarkVisualEnabled = false;
                try
                {
                    listPresenter.CheckBrush = new SolidColorBrush(Colors.Transparent);
                    listPresenter.CheckHintBrush = new SolidColorBrush(Colors.Transparent);
                    listPresenter.CheckSelectingBrush = new SolidColorBrush(Colors.Transparent);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                return;
            }

            if (root is GridViewItemPresenter gridPresenter)
            {
                gridPresenter.CornerRadius = presenterRadius;
                gridPresenter.SelectionCheckMarkVisualEnabled = false;
                try
                {
                    gridPresenter.CheckBrush = new SolidColorBrush(Colors.Transparent);
                    gridPresenter.CheckHintBrush = new SolidColorBrush(Colors.Transparent);
                    gridPresenter.CheckSelectingBrush = new SolidColorBrush(Colors.Transparent);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                SoftenItemPresenterCorners(VisualTreeHelper.GetChild(root, i));
            }
        }


        private static void ApplyForegroundToDescendants(DependencyObject root, Brush foreground)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb)
                {
                    tb.Foreground = foreground;
                }
                else if (child is Control control)
                {
                    control.Foreground = foreground;
                }

                ApplyForegroundToDescendants(child, foreground);
            }
        }


        private static void ClearForegroundOnDescendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb)
                {
                    tb.ClearValue(TextBlock.ForegroundProperty);
                }
                else if (child is Control control)
                {
                    control.ClearValue(Control.ForegroundProperty);
                }

                ClearForegroundOnDescendants(child);
            }
        }


                private Brush ResolveAccentBrush()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            if (settings.AccentSource == "Custom")
            {
                return new SolidColorBrush(ColorHelper.ParseHexColor(settings.CustomAccentColor) ?? Color.FromArgb(255, 0, 120, 212));
            }

            if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object? colorObj)
                && colorObj is Color color)
            {
                return new SolidColorBrush(color);
            }

            return new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        }


        private Brush ResolveAccentForegroundBrush()
            => ColorHelper.ResolveContrastingForeground(ResolveAccentBrush());

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            // 注意：此处不再兜底保存 EQ。每次 EQ 调整已由 ApplyDspToEngine → EqCurveStore.Save 落盘；
            // 若在此用进程内 _audioFxEq（启动时是平坦默认，未打开音效面板时不等于盘上值）覆盖保存，
            // 会把用户上次调好的曲线覆盖成平坦 —— 这正是“重启/再次打开后 EQ 还原”的根因。
            PersistDesktopLyricPosition();
            _taskbarProgress?.Dispose();
            _taskbarProgress = null;

            // 背景视频播放器：关窗即释放解码资源
            try
            {
                StopBackgroundMotion();
                _backgroundVideoPlayer?.Dispose();
                _backgroundVideoPlayer = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }

            // 先停电平表定时器：否则窗口销毁后它仍会 tick 并访问已分离的 XAML 元素，
            // 在退出时抛 COMException (0x8000FFFF)。
            try
            {
                _levelMeterTimer?.Stop();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.LevelMeter.cs", caught); }

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
                _audioEngine?.Dispose();
                _audioEngine = null;
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


        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            if (_closePromptOpen)
            {
                return;
            }

            _ = HandleCloseRequestAsync();
        }


        private async Task<CloseWindowAction> ShowCloseChoiceDialogAsync()
        {
            if (Content?.XamlRoot == null)
            {
                return CloseWindowAction.Exit;
            }

            var dontAsk = new CheckBox
            {
                Content = "下次不再询问",
                Margin = new Thickness(0, 8, 0, 0)
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "关闭主界面时，要将播放器缩小到系统托盘继续在后台运行，还是退出播放器？",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(dontAsk);

            var dialog = new ContentDialog
            {
                Title = "关闭 CelesteMusicPlayer",
                Content = panel,
                PrimaryButtonText = "缩小到托盘",
                SecondaryButtonText = "退出播放器",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            // 关闭对话框按钮用当前主题色(ContentDialog 局部资源)
            try
            {
                Windows.UI.Color closeAccent = ThemeColorService.CurrentAccent;
                var closeBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(closeAccent);
                dialog.Resources["AccentButtonBackground"] = closeBrush;
                dialog.Resources["AccentButtonBackgroundPointerOver"] = closeBrush;
                dialog.Resources["AccentButtonBackgroundPressed"] = closeBrush;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            ColorHelper.ApplyDialogAccent(dialog);
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
            {
                return CloseWindowAction.Ask; // 取消：不做事
            }

            CloseWindowAction chosen = result == ContentDialogResult.Primary
                ? CloseWindowAction.MinimizeToTray
                : CloseWindowAction.Exit;

            if (dontAsk.IsChecked == true)
            {
                AppClosePreferences.Save(new AppClosePreferencesState
                {
                    DontAskAgain = true,
                    PreferredAction = chosen == CloseWindowAction.Exit
                        ? nameof(CloseWindowAction.Exit)
                        : nameof(CloseWindowAction.MinimizeToTray)
                });
            }

            return chosen;
        }


        private void MinimizeToTray()
        {
            try
            {
                _trayIcon ??= new AppTrayIcon(this);
                _trayIcon.Show();
                StartupLog.Write("托盘: MinimizeToTray 完成");
                AppWindow.Hide();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MinimizeToTray", ex);
                // 托盘失败则直接退出，避免关不掉
                ExitApplication();
            }
        }


        internal void RestoreFromTray()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    StartupLog.Write("托盘: RestoreFromTray 收到点击");
                    AppWindow.Show();
                    Activate();
                    // 托盘图标常驻：不随窗口恢复隐藏（退出时才清理）
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("RestoreFromTray", ex);
                }
            });
        }


        internal void ExitFromTray()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StartupLog.Write("托盘: ExitFromTray 收到点击");
                ExitApplication();
            });
        }


        private void CloseAllChildWindows()
        {
            try
            {
                if (_currentPlaylistWindow != null)
                {
                    CurrentPlaylistWindow playlist = _currentPlaylistWindow;
                    _currentPlaylistWindow = null;
                    playlist.Close();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                if (_desktopLyricsWindow != null)
                {
                    DesktopLyricsOverlay lyrics = _desktopLyricsWindow;
                    _desktopLyricsWindow = null;
                    _desktopLyricsEnabled = false;
                    lyrics.ClosedByUser -= OnDesktopLyricsClosedByUser;
                    lyrics.Close();
                    lyrics.Dispose();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                SettingsWindow.CloseIfOpen();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                if (_miniPlayerWindow != null)
                {
                    MiniPlayerWindow mini = _miniPlayerWindow;
                    _miniPlayerWindow = null;
                    _miniPlayerEnabled = false;
                    mini.ClosedByUser -= OnMiniPlayerClosedByUser;
                    mini.Close();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                if (_artistAvatarEditorWindow != null)
                {
                    ArtistAvatarEditorWindow editor = _artistAvatarEditorWindow;
                    _artistAvatarEditorWindow = null;
                    editor.Close();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private Color ResolveAccentColor()
        {
            if (ResolveAccentBrush() is SolidColorBrush scb && scb.Color.A > 0)
            {
                return scb.Color;
            }

            return Color.FromArgb(255, 0, 120, 212);
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
                    Width = Math.Max(1, barW - 1),
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


                /// <summary>最近一次当前播放曲目的封面字节：清掉自定义背景后恢复封面背景用。</summary>
        private byte[]? _lastCoverBytes;

        /// <summary>自定义背景图的请求序号：连续换图时丢弃过期结果，避免旧图盖掉新图。</summary>
        private int _customBackgroundRequest;

        /// <summary>应用自定义背景图片(设置里选择)；与封面背景互斥——自定义优先。
        /// 无路径时恢复封面背景（用最近缓存的当前曲目封面）。
        /// 设置里开了「背景高斯模糊」时，自定义图片同样先做模糊再铺满窗口。</summary>
        private Windows.Media.Playback.MediaPlayer? _backgroundVideoPlayer;

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
                _backgroundVideoPlayer.Source = null;
                _backgroundVideoPausedByMinimize = false;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught);
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
                    : WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                if (IsIconic(hwnd))
                {
                    if (!_backgroundVideoPausedByMinimize)
                    {
                        _backgroundVideoPlayer.Pause();
                        _backgroundVideoPausedByMinimize = true;
                        StartupLog.Write("背景视频：窗口最小化，已暂停解码");
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

                _backgroundVideoPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
                CustomBackgroundVideo.Visibility = Visibility.Visible;
                _backgroundVideoPlayer.Play();

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
                DrawVisualSlot();
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
                _miniPlayerWindow?.RefreshAccentFromOwner();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                _desktopLyricsWindow?.ApplySettings(AppSettingsStore.Load());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>将行的逐字 Run 统一重置为指定颜色（用于非当前行）。</summary>
        private void ResetRowRunColors(TextBlock row, byte r, byte g, byte b)
        {
            if (row.Inlines.Count == 0)
            {
                return;
            }

            var brush = new SolidColorBrush(Color.FromArgb(255, r, g, b));
            foreach (Microsoft.UI.Xaml.Documents.Inline inline in row.Inlines)
            {
                if (inline is Microsoft.UI.Xaml.Documents.Run run)
                {
                    run.Foreground = brush;
                }
            }
        }


        private static Color WaveColorFor(int index) => _waveAccentColor;

    }
}
