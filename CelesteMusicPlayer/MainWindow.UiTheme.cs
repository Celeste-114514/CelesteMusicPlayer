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

                        DrawWaveformBars();
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
                TryApplySystemBackdrop();
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

            try
            {
                // 非打包模式(WindowsPackageType=None)下 ms-appx:/// 不可用,标题栏图标改用文件加载
                string pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
                if (System.IO.File.Exists(pngPath) && AppTitleBarIcon != null)
                {
                    AppTitleBarIcon.Source = new BitmapImage(new Uri(pngPath));
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private Color ResolveUiBaseTintColor()
        {
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
            var capsule = new CornerRadius(height / 2.0); // 半高等于半径 → 两头圆、中间直

            // 排序相关按钮始终使用主题色；accent 参数保留以兼容旧调用
            Brush background = ResolveAccentBrush();
            Brush foreground = ResolveAccentForegroundBrush();

            ApplyCapsuleToControl(SortFieldButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(SortOrderButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(ChangeSortButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(AlbumSortButton, height, capsule, background, foreground);
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


        /// <summary>关闭勾选标记，并尽量给 Presenter 圆角，避免系统方角选中层。</summary>
        private static void SoftenItemPresenterCorners(DependencyObject root)
        {
            if (root is ListViewItemPresenter listPresenter)
            {
                listPresenter.CornerRadius = new CornerRadius(8);
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
                gridPresenter.CornerRadius = new CornerRadius(8);
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

            try
            {
                _volumeSaveTimer?.Stop();
                AppSettingsStore.Update(s => s.Volume = _volumeToSave);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                _audioEngine?.Dispose();
                _audioEngine = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

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
                    // 已播部分:主题色(两端浅色渐变)
                    double t = centerX / Math.Max(1, playedEdge);
                    rect.Fill = new SolidColorBrush(Color.FromArgb(
                        255,
                        (byte)(accent.R + (light.R - accent.R) * t),
                        (byte)(accent.G + (light.G - accent.G) * t),
                        (byte)(accent.B + (light.B - accent.B) * t)));
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


                /// <summary>应用自定义背景图片(设置里选择);无路径时恢复封面背景。</summary>
        private void ApplyCustomBackground(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    CustomBackgroundImage.Source = null;
                    CustomBackgroundImage.Visibility = Visibility.Collapsed;
                    return;
                }

                var bmp = new BitmapImage();
                bmp.DecodePixelWidth = 1920;
                using (System.IO.FileStream fs = System.IO.File.OpenRead(path))
                {
                    bmp.SetSource(fs.AsRandomAccessStream());
                }

                CustomBackgroundImage.Source = bmp;
                CustomBackgroundImage.Visibility = Visibility.Visible;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>主题色变化事件处理:统一刷新信息卡波形/歌词/进度条/迷你播放器/桌面歌词。</summary>
        private void OnThemeColorChanged(Windows.UI.Color accent)
        {
            try
            {
                _waveAccentColor = accent;
                DrawWaveformBars();
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
