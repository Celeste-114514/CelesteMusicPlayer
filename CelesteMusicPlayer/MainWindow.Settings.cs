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
                        VolumeSlider.Value = softVol ? Math.Clamp(settings.Volume, 0, 100) : VolumeSlider.Maximum;
                        MediaPlayer? hifiPlayer = GetPlayer();
                        if (hifiPlayer != null)
                        {
                            hifiPlayer.Volume = softVol ? settings.Volume / 100.0 : 1.0; // 引擎路径下 MediaPlayer 常停用，兜底置满
                        }

                        if (softVol)
                        {
                            _audioEngine?.SetVolume(settings.Volume / 100.0);
                        }

                        UpdateVolumeIcon(VolumeSlider.Value);
                    }
                    else
                    {
                        VolumeSlider.Value = Math.Clamp(settings.Volume, 0, 100);
                        _volumeToSave = Math.Clamp(settings.Volume, 0, 100); // 启动同步，避免退出误写
                        MediaPlayer? player = GetPlayer();
                        if (player != null)
                        {
                            player.Volume = VolumeSlider.Value / 100.0;
                        }

                        UpdateVolumeIcon(VolumeSlider.Value);
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

                ApplyUiStyleMode(settings); // 界面风格（图景/经典浅色/深色/跟随系统/极客）；图景模式内部回落毛玻璃开关
                // WebAppearanceService applies the native backdrop.
                ApplyExtendedSettingsLive(settings);
            });
        }


        private void ApplyFrostedGlassPreference(bool enabled)
        {
            try
            {
                FrostedGlass.BackdropEnabled = enabled;
                if (enabled)
                {
                    FrostedGlass.ApplyWindowBackdrop(this);
                }
                else
                {
                    SystemBackdrop = null;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>「选项」按钮：多选态的多选操作。</summary>
        private void MediaOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            List<PlaylistItem> sel = MediaDetailsList?.SelectedItems.OfType<PlaylistItem>().ToList() ?? new List<PlaylistItem>();
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var add = new MenuFlyoutItem { Text = $"加入播放队列（{sel.Count}）" };
            add.Icon = new FontIcon { Glyph = "\uE710" };
            add.Click += (_, _) =>
            {
                foreach (PlaylistItem s in sel)
                {
                    AddToUserPlaylistBack(s);
                }
            };
            flyout.Items.Add(add);

            var edit = new MenuFlyoutItem { Text = $"标签编辑（{sel.Count}）" };
            edit.Icon = new FontIcon { Glyph = "\uE8D2" };
            edit.Click += (_, _) => OpenTagEditForFiles(sel.Select(i => i.FilePath).ToList());
            flyout.Items.Add(edit);

            var del = new MenuFlyoutItem { Text = $"从媒体库中删除（{sel.Count}）" };
            del.Icon = new FontIcon { Glyph = "\uE74D" };
            del.Click += (_, _) => _ = DeleteMediaSongsConfirmAsync(sel);
            flyout.Items.Add(del);

            var exit = new MenuFlyoutItem { Text = "退出多选" };
            exit.Icon = new FontIcon { Glyph = "\uE711" };
            exit.Click += (_, _) => ExitMediaMultiSelect();
            flyout.Items.Add(exit);

            flyout.ShowAt(MediaOptionsButton, new Windows.Foundation.Point(0, 0));
        }


    }
}
