using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI;
using Windows.UI;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第十七刀（2026-10-31）：设置实时应用宿主。
/// 从壳（Settings/Misc2）转活的「设置变更即时生效」执行器：
/// 置顶开关、桌面歌词联动、列表表头列宽同步。
/// 网页设置页（WebSettingsHost）借用的行为入口住这里。
/// 媒体库自动重扫监听暂留壳（RescanLocalLibraryButton_Click 连着 Library.cs 深水区）。
/// </summary>
public sealed partial class MainWindow
{
        private int _recentRangeDaysApplied = -1;
        private double _appliedMainLyricFontSize = -1;
        // ---- 置顶 / 桌面歌词设置联动 ----

        private void ApplyAlwaysOnTopFromSettings()
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = AppSettingsStore.Load().AlwaysOnTop;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        internal void ApplyOverlayPreferenceFromSettings(AppSettingsState settings)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 「设置里改了开机打开迷你播放器」的联动：随迷你播放器一起移除（2026-10-19 删掉重写）

                if (settings.OpenDesktopLyricsOnStartup != _desktopLyricsEnabled)
                {
                    SetDesktopLyricsEnabled(settings.OpenDesktopLyricsOnStartup, persistPreference: false);
                }
            });
        }

        // W13 第三十四刀：设置实时应用（扩展）

        internal void ApplyExtendedSettingsLive(AppSettingsState settings)
        {
            // 歌词与桌面歌词设置最先应用，避免被其它子系统的异常阻断
            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _desktopLyricsWindow?.ApplySettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 自定义背景图片：保存后即时应用
            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 播放列表列显隐/密度：保存后即时应用
            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 进度条样式：保存后即时切换
            try
            {
                bool wasWaveform = _waveformProgress;
                // 恒用波形进度条（2026-10-15 定稿）：设置里的开关已下架，这里不再读旧配置里的 false
                _waveformProgress = true;
                if (_waveformProgress && !wasWaveform)
                {
                    // 刚打开波形开关:立即加载当前播放(或选中)歌曲的波形
                    string? cur = _nowPlayingPath;

                    if (!string.IsNullOrEmpty(cur))
                    {
                        LoadWaveformForCurrentAsync(cur);
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 最近播放范围：保存后即时过滤（0=全部；N=只留最近 N 天，正在看该列表才需要重刷）
            try
            {
                if (_recentRangeDaysApplied != settings.RecentPlayedRangeDays)
                {
                    _recentRangeDaysApplied = settings.RecentPlayedRangeDays;
                    if (string.Equals(_currentCategory, "Recent", StringComparison.Ordinal))
                    {
                        ApplyFavoritesOrRecentCategory();
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 主题色：全局资源键在启动时应用(重启完全生效)；
            // 保存后即时刷新自绘强调元素（选中高亮/导航/正在播放卡/排序按钮）
            try
            {
                _waveAccentColor = ResolveAccentColor();
                StartupLog.Write("主题色应用-波形: " + _waveAccentColor.ToString());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 歌词:用新主题色重新渲染当前行
            try
            {
                TimeSpan lyricPos = _audioEngine?.IsPlaying == true
                    ? EnginePositionValue
                    : (GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);
                SyncLyricsToPosition(lyricPos, force: true);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 迷你播放器:刷新强调元素
            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            // 音量条(自绘)重绘 + 进度条主题色
            try
            {
                Windows.UI.Color accent2 = ResolveAccentColor();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                MediaSession.ConfigureFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyPlaybackRateFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyAlwaysOnTopFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                UpdatePlaybackRateButtonText();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _hotkeys?.ApplyBindings(settings.CustomHotkeys);
                RestartGlobalHotkeysFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                RestartLibraryWatchFromSettings();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyNavVisibilityFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplySpectrumVisibilityFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                ApplyCoverVisibilityFromSettings(settings);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            if (!string.IsNullOrWhiteSpace(_nowPlayingPath)
                && settings.EnableBackground
                && settings.AlbumCoverAsBackground)
            {
                try
                {
                    // 模糊半径变更时重绘背景
                    byte[]? bytes = ExtractCoverBytes(_nowPlayingPath);
                    _ = ApplyAlbumArtBackgroundAsync(bytes, _nowPlayingPath);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            }
            else if (!settings.EnableBackground || !settings.AlbumCoverAsBackground)
            {
                try
                {
                    ClearAlbumArtBackground();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            }

            // 主题色变更：刷新列表，让选中高亮等立即使用新颜色
            try
            {
                ApplyCategoryView();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void ApplyNavVisibilityFromSettings(AppSettingsState settings)
        {

            // 网络音乐库：配了地址才显示（名字取显示名，没填就用主机名）
            ApplyWebDavNavEntry(settings);
        }

        private void ApplySpectrumVisibilityFromSettings(AppSettingsState settings)
        {
            // 「显示频谱」关掉时把可视化整体藏起来；打开时交给 ApplyNowPlayingVisual
            // 按当前模式（波形/频谱柱/径向/示波器）决定到底显示哪一块画布，
            // 避免这里只认 WaveformCanvas、把径向模式也给盖掉。
            if (!settings.ShowSpectrum)
            {

                return;
            }

        }

        private void ApplyCoverVisibilityFromSettings(AppSettingsState settings)
        {

        }

        /// <summary>
        /// 把设置里的变速推给引擎（2026-10-15 改：旧实现设 MediaPlayer 的
        /// PlaybackSession.PlaybackRate——那条路只在 MediaPlayer 直出时有效、音调还会一起变；
        /// 现在统一走托管实时变速，起播/设置变更时都要推一次）。
        /// 引擎还没建时只是个 no-op：起播建会话时 HiFiOutputBackend 会从设置里读同样值灌进去。
        /// </summary>
        private void ApplyPlaybackRateFromSettings()
        {
            try
            {
                AppSettingsState s = AppSettingsStore.Load();
                _audioEngine?.SetPlaybackSpeed(s.PlaybackRate, s.PlaybackRatePreservePitch);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs.ApplyPlaybackRateFromSettings", caught); }
        }

        /// <summary>设置窗口四角风格（无边框自绘按钮窗口用；ROUND=圆角、DONOTROUND=全屏填满直角）。</summary>
        private void ApplyWindowCorners(bool rounded)
        {
            try
            {
                if (_mainWindowHwnd == IntPtr.Zero) return;
                int corner = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                DwmSetWindowAttributeInt(_mainWindowHwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>无边框 + 自绘按钮的窗口 chrome：保留系统 resize 边框（四边/四角可调大小、四角圆角、最大化到工作区留任务栏），隐藏系统标题栏按钮（caption）。</summary>
        private void MakeWindowBorderless()
        {
            try
            {
                if (AppWindow.Presenter is OverlappedPresenter p)
                {
                    // hasBorder 保留 resize 边框（原生四边调大小 + DWM 圆角 + 最大化到工作区）；
                    // hasTitleBar=false 去掉系统标题栏/最小化/最大化/关闭按钮，由自绘按钮接管。
                    p.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        // W13 第三十四刀：设置实时应用入口

        /// <summary>设置页变更后即时生效。</summary>
        internal void ApplySettingsLive(AppSettingsState settings)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                bool hifi = IsHiFiModeSelected();
                _applyingSettingsVolume = true;
                if (_audioEngine != null)
                {
                    // HiFi 软件音量开关切换即时生效（播放中也可切）
                    _audioEngine.SoftwareVolumeEnabled = settings.HiFiSoftwareVolume;
                }
                try
                {
                    if (hifi)
                    {
                        // HiFi 独占（bit-perfect）：默认数字音量恒 100%，设备主音量（DAC 级）随滑块可调；
                        // 设置页开启「HiFi 软件音量」后，音量条解冻并改走 DSP 衰减（失去 bit-perfect，徽标有提示）。
                        // 注意：关闭软件音量时只把 UI 滑块显示成 100%，绝不能改 _volumeToSave——
                        // 否则切到 HiFi 后退出，会把用户在共享模式下的真实音量偏好覆盖成 100，
                        // 下次重启共享模式音量就"重置"了。
                        bool softVol = IsHiFiSoftVolumeUiUnlocked();
                        ApplyVolumePercent(softVol ? settings.Volume : VolumePercentMax);
                        MediaPlayer? hifiPlayer = GetPlayer();

                        if (softVol)
                        {
                            _audioEngine?.SetVolume(settings.Volume / 100.0);
                        }

                    }
                    else
                    {
                        ApplyVolumePercent(settings.Volume);
                        _volumeToSave = Math.Clamp(settings.Volume, 0, 100); // 启动同步，避免退出误写
                        MediaPlayer? player = GetPlayer();

                    }
                }
                finally
                {
                    _applyingSettingsVolume = false;
                }

                if (Enum.TryParse(settings.PlaybackOrder, ignoreCase: true, out PlaybackOrder order)
                    && order != _orderResolver.Order)
                {
                    SetPlaybackOrder(order, persist: false);
                }

                // WebAppearanceService applies the native backdrop.
                ApplyExtendedSettingsLive(settings);
            });
        }

        // W13 第三十四刀：旧设置面板占位

        private void EnsureSettingsUiBuilt()
        {
            // W13：原生设置面板已下线，设置改走网页设置页；保留方法让旧调用点不用动。
        }

        private void EnsureTagEditUiBuilt()
        {
            // W13：原生标签面板（XAML 控件）随旧界面下线，标签编辑改走网页标签页。
            // 这里保留方法是为了让旧调用点不用动，但不再构造任何 XAML 控件。
        }

        // W13 第三十四刀：系统背板

    }
