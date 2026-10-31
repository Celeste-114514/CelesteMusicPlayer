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
