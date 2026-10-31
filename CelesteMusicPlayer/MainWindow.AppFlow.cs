using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 应用生命周期行为（2026-10-31 从壳文件整族搬来）：定时停止、托盘显隐、退出流程、收藏状态。
    /// 这些是活功能：托盘是每天用的入口，定时停止由托盘菜单/设置触发，收藏状态同步到任务栏缩略图。
    /// 依赖的状态字段（_sleepTimer 族 / _exitStarted / _taskbarButtons / _trayIcon）随批搬入或在
    /// xaml.cs / TaskbarPlayer.cs（活文件）。
    /// </summary>
    public sealed partial class MainWindow
    {
        // 定时停止 + 全局热键重启 + 收藏状态（2026-10-31 从 MainWindow.Features.cs 搬来）；SleepStopMode 枚举与 _sleep* 字段随批搬入本文件。

        /// <summary>应用睡眠定时选择（对话框与面板页签共用）。sel 口径与旧对话框 RadioButtons 一致。</summary>
        private void ApplySleepSelection(int sel, double trackCountValue, double customMinutes = 0)
        {
            _sleepTimer?.Stop();
            _sleepTimer = null;
            _sleepMode = SleepStopMode.None;
            _sleepTracksRemaining = 0;
            _sleepMinutes = 0;
            _sleepCustomMinutes = 0;

            if (sel == 0)
            {
                NowPlayingText.Text = "睡眠定时器已关闭";
                return;
            }

            if (sel == 6)
            {
                _sleepMode = SleepStopMode.AfterTrack;
                NowPlayingText.Text = "睡眠定时器：当前曲目播完后停止";
                return;
            }

            if (sel == 7)
            {
                int n = (int)Math.Clamp(Math.Round(trackCountValue), 1, 99);
                _sleepMode = SleepStopMode.AfterTracks;
                _sleepTracksRemaining = n;
                NowPlayingText.Text = $"睡眠定时器：再播放 {n} 首后停止";
                return;
            }

            double minutes = sel == 8 ? customMinutes : sel switch
            {
                1 => 15,
                2 => 30,
                3 => 60,
                4 => 90,
                _ => 120
            };
            _sleepMode = SleepStopMode.AfterMinutes;
            _sleepMinutes = (int)Math.Ceiling(minutes);
            _sleepCustomMinutes = sel == 8 ? minutes : 0;
            _sleepTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _sleepTimer.Interval = TimeSpan.FromMinutes(minutes);
            _sleepTimer.Tick += (_, _) => StopForSleep("睡眠定时器到点，播放已暂停");
            _sleepTimer.Start();
            NowPlayingText.Text = $"睡眠定时器已设置：{minutes} 分钟后停止播放";
        }

        /// <summary>统一停止播放并清除睡眠定时器状态。</summary>
        private void StopForSleep(string message)
        {
            _sleepTimer?.Stop();
            _sleepTimer = null;
            _sleepMode = SleepStopMode.None;
            _sleepMinutes = 0;
            _sleepCustomMinutes = 0;
            _sleepTracksRemaining = 0;
            try
            {
                MediaPlayer? p = GetPlayer();
                p?.Pause();
                _audioEngine?.Pause();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            NowPlayingText.Text = message;
        }

        private void RestartGlobalHotkeysFromSettings()
        {
            try
            {
                _hotkeys?.StopListening();
                _hotkeys?.Dispose();
                _hotkeys = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            if (!AppSettingsStore.Load().EnableGlobalHotkeys)
            {
                return;
            }

            try
            {
                _hotkeys = new GlobalHotkeyService();
                _hotkeys.PlayPause += () => DispatcherQueue.TryEnqueue(TogglePlayPausePublic);
                _hotkeys.Next += () => DispatcherQueue.TryEnqueue(PlayNext);
                _hotkeys.Previous += () => DispatcherQueue.TryEnqueue(PlayPrevious);
                _hotkeys.VolumeUp += () => DispatcherQueue.TryEnqueue(() => AdjustVolumeBy(5));
                _hotkeys.VolumeDown += () => DispatcherQueue.TryEnqueue(() => AdjustVolumeBy(-5));
                _hotkeys.SeekForward += () => DispatcherQueue.TryEnqueue(() => SeekBySeconds(5));
                _hotkeys.SeekBack += () => DispatcherQueue.TryEnqueue(() => SeekBySeconds(-5));
                _hotkeys.ToggleDesktopLyrics += () => DispatcherQueue.TryEnqueue(() =>
                    SetDesktopLyricsEnabled(!_desktopLyricsEnabled));
                _hotkeys.ToggleFavorite += () => DispatcherQueue.TryEnqueue(ToggleFavoriteForCurrent);
                _hotkeys.ShowHideMain += () => DispatcherQueue.TryEnqueue(ToggleMainWindowVisibility);
                _hotkeys.Stop += () => DispatcherQueue.TryEnqueue(() => GetPlayer()?.Pause());
                _hotkeys.Start();
                _hotkeys.ApplyBindings(AppSettingsStore.Load().CustomHotkeys);
            }
            catch (Exception ex)
            {
                StartupLog.Write("GlobalHotkey start failed: " + ex.Message);
            }
        }

        private void UpdateFavoriteButtonUi()
        {
            bool fav = !string.IsNullOrWhiteSpace(_nowPlayingPath)
                && (TrackStatsStore.Get(_nowPlayingPath)?.IsFavorite ?? false);

            if (FavoriteButtonIcon != null)
            {
                FavoriteButtonIcon.Glyph = fav ? "\uEB52" : "\uEB51";
                ToolTipService.SetToolTip(FavoriteButton, fav ? "取消喜欢" : "我喜欢的音乐");
            }

            // 极客模式：图标被字符 ♡/♥ 盖着 —— 心形也要跟着喜欢状态变（空心/实心红）。
            if (GeekUiStyleCached)
            {
                SetGeekFavoriteState(fav);
            }

            // 任务栏缩略图按钮：把当前曲目的收藏状态同步到 thumbar
            // （启动恢复上次播放时，thumbar 加载完成就会用对的图标）
            _taskbarButtons?.UpdateFavorite(fav);
        }
        // 托盘显隐（2026-10-31 从 MainWindow.UiTheme.cs 搬来）：RestoreMainWindow 在 WindowCoordination.cs（活），ExitApplication 随批搬入本文件。

        internal void RestoreFromTray()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    StartupLog.Write("托盘: RestoreFromTray 收到点击");
                    // 统一走主界面恢复（建/还原/显示/拉前台），别在这里另写一套 Show。
                    RestoreMainWindow();
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
        // 退出流程（2026-10-31 从 MainWindow.Misc.cs 搬来）：PersistPlaybackSession 在 Playback.cs（活）、MarkAppCleanExit 在 AppSettingsStore（活）、_exitStarted 字段随批搬入。

        private void ExitApplication()
        {
            if (_exitStarted) return;
            _exitStarted = true;
            // 先标记允许关闭，避免任何 Closing 处理器再弹“最小化到托盘还是退出”的对话框
            _allowClose = true;

            try { _taskbarPlayer?.Dispose(); _taskbarPlayer = null; }
            catch (Exception ex) { StartupLog.WriteException("TaskbarPlayer.Exit", ex); }

            // 立刻销毁托盘/任务栏图标：用户一点“退出”，图标马上消失
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

            // 持久化关键状态（干净退出标记 / 续播进度），失败也不影响退出
            try { AppSettingsStore.MarkAppCleanExit(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            try { PersistPlaybackSession(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try { Close(); } catch (Exception ex) { StartupLog.WriteException("MainWindow.ExitCleanup", ex); }

            // 直接、强制结束进程。不调用 Close()/Application.Current.Exit()，
            // 因为它们在 WinUI3 里可能卡在窗口拆除或资源释放上几秒，导致
            // “托盘图标已没、主程序却还停好几秒”。Environment.Exit(0) 是进程级强杀，
            // 立即终止所有线程与窗口，干净利落。
            Environment.Exit(0);
        }
        // 极客收藏状态（2026-10-31 从 MainWindow.GeekShell.cs 搬来）：操作极客 ASCII 按钮标签（占位控件），随 UpdateFavoriteButtonUi 一起。

        /// <summary>
        /// 喜欢状态变化时同步心形键：♡ 空心 / ♥ 实心红 —— 形状加颜色双重区分，
        /// 和经典模式"实心红心"的语义一致。
        /// </summary>
        internal void SetGeekFavoriteState(bool fav)
        {
            TextBlock? label = FavoriteButton != null
                               && _geekAsciiButtons.TryGetValue(FavoriteButton, out GeekButtonBackup? backup)
                ? backup.Label
                : null;
            if (label == null)
            {
                return;
            }

            label.Text = fav ? "♥" : "♡";
            label.Foreground = fav
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x4D, 0x4D))
                : new SolidColorBrush(PhosphorColor(AppSettingsStore.Load().GeekPhosphorColor));
        }

        // 定时停止族 + 全局热键 + 字段（2026-10-31 从 MainWindow.Features.cs 搬来）

        private GlobalHotkeyService? _hotkeys;

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sleepTimer;

        /// <summary>睡眠定时器停止模式。</summary>
        private enum SleepStopMode
        {
            None,
            AfterMinutes,
            AfterTrack,
            AfterTracks
        }

        private SleepStopMode _sleepMode = SleepStopMode.None;

        private int _sleepTracksRemaining;

        private int _sleepMinutes;   // AfterMinutes 模式下的分钟数（0=未设置）；面板回显用

        private double _sleepCustomMinutes;

        // 退出流程标记（2026-10-31 随 ExitApplication 从 MainWindow.Misc.cs 搬来）
        private bool _exitStarted;
        // 托盘「显示/隐藏主窗口」（2026-10-31 从 MainWindow.Features.cs 搬来）：纯窗口操作，随托盘域。

        private void ToggleMainWindowVisibility()
        {
            try
            {
                if (AppWindow.IsVisible)
                {
                    AppWindow.Hide();
                }
                else
                {
                    AppWindow.Show();
                    Activate();
                }
            }
            catch
            {
                Activate();
            }
        }
        // 极客 ASCII 按钮备份（2026-10-31 随 SetGeekFavoriteState 从 MainWindow.GeekShell.cs 搬来）。
        /// <summary>被 ASCII 化过的按钮 → 原始内容与图标可见性备份（切风格时还原用）。</summary>
        private readonly Dictionary<Button, GeekButtonBackup> _geekAsciiButtons = new();

        private sealed class GeekButtonBackup
        {
            public object? Content;
            public TextBlock? Label;

            /// <summary>纯图标按钮外面套的字符宿主 Grid（还原时整包换回原内容）。</summary>
            public Grid? Host;

            /// <summary>被藏掉的图标 → 原可见性。用字典去重：补藏可能对同一个图标跑多次。</summary>
            public Dictionary<UIElement, Visibility> Icons = new();
        }
    }
}
