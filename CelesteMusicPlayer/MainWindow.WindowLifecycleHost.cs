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
    // UI 线程的调度队列：**不要用 Window.DispatcherQueue 判断线程** ——
    // 在非 UI 线程上它会返回那个线程自己的队列，HasThreadAccess 于是误判成 true。
    // 判断「我是不是 UI 线程」只能跟这个启动时捕获的队列比。
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    /// <summary>
    /// 把动作切回主 UI 线程。后台线程（桌面歌词专用线程、WebView2 回调、定时器）
    /// 要动宿主状态时走这里，别直接改字段或碰控件。
    /// </summary>
    internal void RunOnUi(Action action)
    {
        Microsoft.UI.Dispatching.DispatcherQueue? ui = _uiQueue;
        if (ui == null || ui.HasThreadAccess)
        {
            action();
            return;
        }

        ui.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.RunOnUi", ex); }
        });
    }

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


                    // 音量条自绘:尺寸变化/初始绘制/交互

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
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                    // 播放列表列显隐/密度
                    try
                    {
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                    // 进度条样式:启动时读取设置(否则默认显示系统进度条)
                    try
                    {
                        _waveformProgress = AppSettingsStore.Load().WaveformProgress;
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                    // 进度条画布尺寸变化时重绘(首次布局/窗口缩放)

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
                double sliderVolume = Math.Clamp(_volumePercent, 0, VolumePercentMax);
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
    }
