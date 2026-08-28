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

        private void SortFieldMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
            {
                return;
            }

            _sortField = tag switch
            {
                "Artist" => SortField.Artist,
                "Album" => SortField.Album,
                "Year" => SortField.Year,
                "Duration" => SortField.Duration,
                "Genre" => SortField.Genre,
                "Track" => SortField.Track,
                "FilePath" => SortField.FilePath,
                _ => SortField.Title
            };

            SortFieldText.Text = "排序：" + GetSortFieldDisplayName(_sortField);

            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;
            ApplySort(playingPath);
        }


        private MenuFlyoutItem CreateOpenFileLocationMenuItem()
        {
            var item = new MenuFlyoutItem { Text = "打开文件位置" };
            item.Icon = new FontIcon { Glyph = "\uE8DA" };
            item.Click += (_, _) =>
            {
                if (_contextMenuSong != null)
                {
                    OpenFileLocationInExplorer(_contextMenuSong.FilePath);
                }
            };
            return item;
        }
    }
}
