using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 零散播放窗口的「设置」菜单 + 音量条（2026-10-18 用户要求补齐）：
    /// · 菜单三项（都作用于当前播放的歌）：打开文件位置 / 文件属性 / 倍速播放；
    /// · 音量条常驻主界面（传输行左侧），口径与主程序一致：
    ///   共享模式 = 数字音量跟随滑条；独占/ASIO 未开「HiFi 软件音量」时钉 100%
    ///   （实际音量由设备控制，保 bit-perfect），并给一句说明。
    /// · 倍速写共享设置（主程序同步）+ 实时推本窗口引擎；DSD DoP 直出不变速，如实说明。
    /// </summary>
    public sealed partial class ScatteredPlayWindow
    {
        /// <summary>音量：启动初值回填完成前不响应用户改动（防 XAML 初值反写设置）。</summary>
        private bool _volumeUiReady;

        /// <summary>音量写盘防抖（300ms）；关窗时无条件 flush。&lt;0 = 没有待存改动。</summary>
        private double _volumeToSave = -1;
        private DispatcherQueueTimer? _volumeSaveTimer;

        // =====================================================================
        // 顶栏两个入口（2026-10-19 用户重排）：
        //   「设置」菜单取消 —— 打开文件位置做成左上角文件夹图标按钮，
        //   倍速直接放主界面（按钮上显示当前值，点开选档），文件属性整项删掉。
        // =====================================================================

        private void OpenLocationButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(OpenCurrentFileLocation);
        }

        private void RateButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                // 每次现建菜单：倍速档位的「当前值」勾选自然刷新，不用手动维护状态
                var flyout = new MenuFlyout
                {
                    Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
                };

                double current = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
                foreach (double rate in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
                {
                    double r = rate; // 闭包捕获
                    bool isCurrent = Math.Abs(r - current) < 0.001;
                    var item = new MenuFlyoutItem { Text = (isCurrent ? "✓ " : "") + r.ToString("0.##") + "x" };
                    item.Click += (_, _) => SetScatteredPlaybackRate(r);
                    flyout.Items.Add(item);
                }

                flyout.ShowAt(RateButton);
            });
        }

        /// <summary>倍速按钮上的文字跟着当前值走（初始化与改档后都刷）。</summary>
        private void RefreshRateButton()
        {
            Safe(() =>
            {
                double rate = Math.Clamp(AppSettingsStore.Load().PlaybackRate, 0.5, 2.0);
                RateButtonText.Text = rate.ToString("0.##") + "x";
            });
        }

        /// <summary>在资源管理器中选中并显示当前播放的文件。</summary>
        private void OpenCurrentFileLocation()
        {
            PlaylistItem? item = CurrentItem;
            if (item == null || string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
            {
                StatusText.Text = "当前没有可定位的文件。";
                return;
            }

            // 与主程序 / 当前播放列表窗口同一实现（explorer /select）
            MainWindow.OpenFileLocationInExplorerPublic(item.FilePath);
        }

        /// <summary>设倍速：写共享设置（主程序同步）+ 实时推给本窗口引擎。</summary>
        private void SetScatteredPlaybackRate(double rate)
        {
            Safe(() =>
            {
                double clamped = Math.Clamp(rate, 0.5, 2.0);
                bool preserve = AppSettingsStore.Load().PlaybackRatePreservePitch;
                AppSettingsStore.Update(s => s.PlaybackRate = clamped);
                _engine?.SetPlaybackSpeed(clamped, preserve);
                RefreshRateButton();

                string rateText = clamped.ToString("0.##") + "x";
                if (_engine?.IsDsdDirectOut == true)
                {
                    // DoP 直出 = 1-bit 位流直通设备，不经 DSP 链，托管变速够不着它
                    StatusText.Text = "DSD DoP 直出不变速（直出链路不过处理）；速度已记为 "
                        + rateText + "，下一首 PCM 生效";
                }
                else if (_engine == null)
                {
                    StatusText.Text = "播放速度已设为 " + rateText + "（起播后生效）";
                }
                else
                {
                    StatusText.Text = "播放速度 " + rateText + (preserve ? " · 音调不变" : " · 音调随速度变");
                }

                StartupLog.Write("[零散窗口][变速] 设为 " + clamped.ToString("0.##") + "x 保调=" + preserve);
            });
        }

        // =====================================================================
        // 音量
        // =====================================================================

        /// <summary>构造时回填音量滑条（口径同 EnsureEngine：锁定时钉 100）。</summary>
        private void LoadVolumeFromSettings()
        {
            Safe(() =>
            {
                double value = IsVolumeEngineControlled()
                    ? Math.Clamp(AppSettingsStore.Load().Volume, 0, 100)
                    : 100;

                _volumeUiReady = false;
                try
                {
                    VolumeSlider.Value = value;
                    VolumeValueText.Text = ((int)Math.Round(value)).ToString();
                    UpdateVolumeIcon(value);
                }
                finally
                {
                    _volumeUiReady = true;
                }
            });
        }

        /// <summary>当前输出模式下音量是否由本程序数字控制（共享=是；独占/ASIO 仅开「HiFi 软件音量」时是）。</summary>
        private bool IsVolumeEngineControlled()
        {
            AppSettingsState st = AppSettingsStore.Load();
            bool exclusive = string.Equals(st.OutputMode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(st.OutputMode, "Asio", StringComparison.OrdinalIgnoreCase);
            return !exclusive || st.HiFiSoftwareVolume;
        }

        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_volumeUiReady)
            {
                return; // XAML 初值 / 回填期间，不是用户操作
            }

            Safe(() =>
            {
                double value = Math.Clamp(e.NewValue, 0, 100);
                UpdateVolumeIcon(value);
                VolumeValueText.Text = ((int)Math.Round(value)).ToString();

                if (!IsVolumeEngineControlled())
                {
                    // 独占/ASIO 未开软体积：设备主音量接管，钉 100 并说明（保 bit-perfect）
                    _volumeUiReady = false;
                    try
                    {
                        VolumeSlider.Value = 100;
                    }
                    finally
                    {
                        _volumeUiReady = true;
                    }

                    VolumeValueText.Text = "100";
                    StatusText.Text = "独占/ASIO 输出下音量由设备控制（设置页可开「HiFi 软件音量」用程序内音量）";
                    return;
                }

                _engine?.SetVolume(value / 100.0);
                ScheduleVolumeSave(value);
            });
        }

        /// <summary>喇叭图标三态（与主程序同一组字形：E74F mute / E992 low / E993 mid / E767 high）。</summary>
        private void UpdateVolumeIcon(double volumePercent)
        {
            VolumeIcon.Glyph = volumePercent <= 0.5 ? "\uE74F"
                : volumePercent < 34 ? "\uE992"
                : volumePercent < 67 ? "\uE993"
                : "\uE767";
        }

        /// <summary>音量写盘去抖：停止拖动 300ms 后才保存一次（与主程序同口径）。</summary>
        private void ScheduleVolumeSave(double value)
        {
            _volumeToSave = value;
            if (_volumeSaveTimer == null)
            {
                _volumeSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _volumeSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
                _volumeSaveTimer.Tick += (_, _) =>
                {
                    _volumeSaveTimer.Stop();
                    SaveVolumeNow();
                };
            }

            _volumeSaveTimer.Stop();
            _volumeSaveTimer.Start();
        }

        /// <summary>无条件 flush 待保存音量（关窗时调用）。</summary>
        private void SaveVolumeNow()
        {
            if (_volumeToSave < 0)
            {
                return; // 没搬过滑条，没什么可存
            }

            try
            {
                AppSettingsStore.Update(s => s.Volume = Math.Clamp(_volumeToSave, 0, 100));
                _volumeToSave = -1;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.SaveVolumeNow", caught);
            }
        }
    }
}
