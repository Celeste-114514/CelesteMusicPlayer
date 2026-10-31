using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Color = Windows.UI.Color;
using System.IO;

namespace CelesteMusicPlayer;

/// <summary>W13 第三十四刀（2026-10-31）：窗口生命周期：首激活/关闭序列/窗口框架配置/背景视频的窗口状态跟随。</summary>
public sealed partial class MainWindow
{
        // 视频背景「最小化暂停」：最小化时暂停解码省电，还原后继续
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        private bool _windowChromeConfigured;

        // W13 第三十四刀：窗口生命周期

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

        private void ApplyStartupOverlayWindows()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            if (settings.OpenDesktopLyricsOnStartup)
            {
                SetDesktopLyricsEnabled(true, persistPreference: false);
            }

            // 开机自动打开迷你播放器：随迷你播放器一起移除（2026-10-19 删掉重写）
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
    }
