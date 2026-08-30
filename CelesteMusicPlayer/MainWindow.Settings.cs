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

        /// <summary>把设置里的输出模式映射到引擎 HiFi 后端（Shared / WasapiExclusive / Asio）。</summary>
        private void ApplyEngineOutputMode(AppSettingsState settings)
        {
            HiFiOutputBackend.OutputMode mode = string.Equals(settings.OutputMode, "WasapiExclusive", System.StringComparison.OrdinalIgnoreCase)
                ? HiFiOutputBackend.OutputMode.WasapiExclusive
                : string.Equals(settings.OutputMode, "Asio", System.StringComparison.OrdinalIgnoreCase)
                    ? HiFiOutputBackend.OutputMode.Asio
                    : HiFiOutputBackend.OutputMode.WasapiShared;
            _audioEngine?.SetOutputMode(mode);
        }


        /// <summary>设置页变更后即时生效。</summary>
        internal void ApplySettingsLive(AppSettingsState settings)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                bool hifi = IsHiFiModeSelected();
                _applyingSettingsVolume = true;
                try
                {
                    if (hifi)
                    {
                        // HiFi 独占（bit-perfect）：数字音量恒 100%，设备主音量（DAC 级）随滑块可调。
                        // 独占：软件音量条固定 100%；实际音量由系统托盘(DAC 设备主音量)控制，bit-perfect 保真。
                        VolumeSlider.Value = VolumeSlider.Maximum;
                        _volumeToSave = VolumeSlider.Maximum;
                        MediaPlayer? hifiPlayer = GetPlayer();
                        if (hifiPlayer != null)
                        {
                            hifiPlayer.Volume = 1.0; // 引擎路径下 MediaPlayer 常停用，兜底置满
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

                ApplyFrostedGlassPreference(settings.EnableFrostedGlass);
                _miniPlayerWindow?.SetAlwaysOnTop(settings.MiniPlayerAlwaysOnTop);
                _miniPlayerWindow?.ApplyBackdropPreference(settings.EnableFrostedGlass);
                SettingsWindow.ApplyBackdropIfOpen();
                ApplyExtendedSettingsLive(settings);
            });
        }


        internal void ApplyOverlayPreferenceFromSettings(AppSettingsState settings)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (settings.OpenMiniPlayerOnStartup != _miniPlayerEnabled)
                {
                    SetMiniPlayerEnabled(settings.OpenMiniPlayerOnStartup, persistPreference: false);
                }

                if (settings.OpenDesktopLyricsOnStartup != _desktopLyricsEnabled)
                {
                    SetDesktopLyricsEnabled(settings.OpenDesktopLyricsOnStartup, persistPreference: false);
                }
            });
        }


        private void ApplyFrostedGlassPreference(bool enabled)
        {
            try
            {
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


        private void ApplyAudioChannelFromSettings()
        {
            MediaPlayer? player = GetPlayer();
            if (player == null)
            {
                return;
            }

            string channel = AppSettingsStore.Load().AudioChannel;
            player.AudioBalance = channel switch
            {
                "Left" => -1f,
                "Right" => 1f,
                _ => 0f
            };
        }


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

            var edit = new MenuFlyoutItem { Text = $"编辑标签（{sel.Count}）" };
            edit.Icon = new FontIcon { Glyph = "\uE8D2" };
            edit.Click += (_, _) => TagEditorWindow.ShowBatch(sel.Select(i => i.FilePath).ToList());
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


        private void ConfigureMultiSelectPrimaryAction()
        {
            bool isUserPlaylist = _multiSelectTargetList == PlaylistView
                && string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal);
            bool isNamedPlaylistDetail = _multiSelectTargetList == PlaylistDetailListView;
            bool isTagSortSongs = _multiSelectTargetList == TagSortPanelSongListView;
            bool isDetailSongList = _multiSelectTargetList == AlbumTrackListView
                || _multiSelectTargetList == ArtistTrackListView;
            if (isUserPlaylist)
            {
                MultiSelectPrimaryActionIcon.Glyph = "\uE74D"; // Delete
                MultiSelectPrimaryActionText.Text = "从播放队列中删除";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲从播放列表移除");
            }
            else if (isNamedPlaylistDetail)
            {
                // 命中单详情页多选 → 从当前命名单删除勾选的歌
                MultiSelectPrimaryActionIcon.Glyph = "\uE74D"; // Delete
                MultiSelectPrimaryActionText.Text = "从播放队列中删除";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲从当前命名单移除");
            }
            else if (isTagSortSongs)
            {
                // 标签排序面板曲目多选 → 添加到播放队列
                MultiSelectPrimaryActionIcon.Glyph = "\uE710";
                MultiSelectPrimaryActionText.Text = "添加至播放队列";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲按顺序加入播放队列");
            }
            else if (isDetailSongList)
            {
                // 专辑/艺术家/专辑艺术家详情页多选歌曲 → 添加到播放列表（列表墙/命名单）
                MultiSelectPrimaryActionIcon.Glyph = "\uE8B7";
                MultiSelectPrimaryActionText.Text = "添加到播放列表";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "把选中的歌曲添加到播放列表（列表墙）");
            }
            else if (_multiSelectAlbumGrid != null)
            {
                MultiSelectPrimaryActionIcon.Glyph = "\uE710";
                MultiSelectPrimaryActionText.Text = "添加至播放队列";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "按当前专辑顺序、音轨号将选中专辑加入播放队列");
            }
            else if (_multiSelectFolderList != null)
            {
                MultiSelectPrimaryActionIcon.Glyph = "\uE710";
                MultiSelectPrimaryActionText.Text = "添加至播放队列";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中文件夹/音频按顺序加入播放队列");
            }
            else
            {
                MultiSelectPrimaryActionIcon.Glyph = "\uE710"; // Add
                MultiSelectPrimaryActionText.Text = "添加至播放队列";
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲添加至播放队列");
            }

            // “添加到播放列表”按钮：多选界面右下角，与“添加至播放队列”并列显示（支持歌曲/专辑/文件夹统一添加）
            if (MultiSelectAddToPlaylistButton != null)
            {
                MultiSelectAddToPlaylistButton.Visibility = Visibility.Visible;
            }

            if (MultiSelectDeleteMenuItem != null)
            {
                MultiSelectDeleteMenuItem.IsEnabled = !AppSettingsStore.Load().DisableDeleteFromDisk;
            }
        }
    }
}
