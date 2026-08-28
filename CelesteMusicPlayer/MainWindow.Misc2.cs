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

        /// <summary>
        /// 列宽适配中间区域：按首选比例缩放，使标题～时长始终铺满中间可视宽度。
        /// </summary>
        private void FitColumnsToAvailableWidth()
        {
            if (_isDraggingColumnSplitter)
            {
                return;
            }

            double available = GetPlaylistColumnsViewportWidth();
            if (available <= 0)
            {
                return;
            }

            // 4 根列间间隙 + 时长右侧留白（避免胶囊右圆角被裁切）
            const double splitterTotal = HorizontalResizeSplitter.HitWidth * 4;
            const double endPad = 12;
            double usable = Math.Max(0, available - splitterTotal - endPad);

            // 首选比例（与默认密度一致）
            const double prefTitle = 140;
            const double prefArtist = 110;
            const double prefAlbum = 110;
            const double prefYear = 52;
            const double prefDuration = 60;
            const double preferredSum = prefTitle + prefArtist + prefAlbum + prefYear + prefDuration;

            double scale = usable / preferredSum;
            double title = Math.Max(48, prefTitle * scale);
            double artist = Math.Max(48, prefArtist * scale);
            double album = Math.Max(48, prefAlbum * scale);
            double year = Math.Max(40, prefYear * scale);
            double duration = Math.Max(40, prefDuration * scale);

            // Min 截断后校正总和，确保刚好铺满
            double again = title + artist + album + year + duration;
            if (again > usable)
            {
                double deficit = again - usable;
                double take = Math.Min(deficit, Math.Max(0, title - 48));
                title -= take;
                deficit -= take;
                if (deficit > 0)
                {
                    take = Math.Min(deficit, Math.Max(0, artist - 48));
                    artist -= take;
                    deficit -= take;
                }

                if (deficit > 0)
                {
                    take = Math.Min(deficit, Math.Max(0, album - 48));
                    album -= take;
                    deficit -= take;
                }

                if (deficit > 0)
                {
                    take = Math.Min(deficit, Math.Max(0, year - 40));
                    year -= take;
                    deficit -= take;
                }

                if (deficit > 0)
                {
                    duration = Math.Max(40, duration - deficit);
                }
            }
            else if (usable - again > 0.5)
            {
                // 剩余补给标题列，保证铺满中间区域
                title += usable - again;
            }

            var w = PlaylistColumnWidths.Instance;
            w.Title = title;
            w.Artist = artist;
            w.Album = album;
            w.Year = year;
            w.Duration = duration;
            SyncHeaderColumnsFromState();
            if (HeaderEndPadCol != null
                && (!HeaderEndPadCol.Width.IsAbsolute
                    || Math.Abs(HeaderEndPadCol.Width.Value - endPad) >= 0.5))
            {
                HeaderEndPadCol.Width = new GridLength(endPad);
            }
        }


        // =====================================================================
        // 播放列表列：可拖分割线
        // =====================================================================

        private void SyncHeaderColumnsFromState()
        {
            var w = PlaylistColumnWidths.Instance;
            SetColumnWidthIfChanged(HeaderTitleCol, w.Title);
            SetColumnWidthIfChanged(HeaderArtistCol, w.Artist);
            SetColumnWidthIfChanged(HeaderAlbumCol, w.Album);
            SetColumnWidthIfChanged(HeaderYearCol, w.Year);
            SetColumnWidthIfChanged(HeaderDurationCol, w.Duration);
        }


        private static void SetColumnWidthIfChanged(ColumnDefinition column, double width)
        {
            if (column.Width.IsAbsolute && Math.Abs(column.Width.Value - width) < 0.5)
            {
                return;
            }

            column.Width = new GridLength(width);
        }


        private void ColumnSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not FrameworkElement splitter || splitter.Tag is not string pair)
            {
                return;
            }

            _isDraggingColumnSplitter = true;
            _columnSplitPair = pair;
            _columnSplitStartX = e.GetCurrentPoint(PlaylistHeaderGrid).Position.X;

            GetColumnWidths(pair, out _columnLeftStartWidth, out _columnRightStartWidth);
            splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        }


        private void ColumnSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingColumnSplitter || _columnSplitPair == null)
            {
                return;
            }

            double delta = e.GetCurrentPoint(PlaylistHeaderGrid).Position.X - _columnSplitStartX;

            const double minLeft = 48;
            const double minRight = 40;

            double left = Math.Max(minLeft, _columnLeftStartWidth + delta);
            double right = Math.Max(minRight, _columnRightStartWidth - delta);
            double total = _columnLeftStartWidth + _columnRightStartWidth;

            if (left + right > total)
            {
                right = total - left;
            }

            if (right < minRight)
            {
                right = minRight;
                left = total - right;
            }

            if (left < minLeft)
            {
                left = minLeft;
                right = total - left;
            }

            SetColumnWidths(_columnSplitPair, left, right);
            e.Handled = true;
        }


        private void ColumnSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingColumnSplitter = false;
            _columnSplitPair = null;
            if (sender is UIElement el)
            {
                try
                {
                    el.ReleasePointerCapture(e.Pointer);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }

            e.Handled = true;
        }


        private void ColumnSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDraggingColumnSplitter = false;
            _columnSplitPair = null;
        }


        private static void GetColumnWidths(string pair, out double left, out double right)
        {
            var w = PlaylistColumnWidths.Instance;
            switch (pair)
            {
                case "Title|Artist":
                    left = w.Title;
                    right = w.Artist;
                    break;
                case "Artist|Album":
                    left = w.Artist;
                    right = w.Album;
                    break;
                case "Album|Year":
                    left = w.Album;
                    right = w.Year;
                    break;
                case "Year|Duration":
                    left = w.Year;
                    right = w.Duration;
                    break;
                default:
                    left = w.Year;
                    right = w.Duration;
                    break;
            }
        }


        private void SetColumnWidths(string pair, double left, double right)
        {
            var w = PlaylistColumnWidths.Instance;
            switch (pair)
            {
                case "Title|Artist":
                    w.Title = left;
                    w.Artist = right;
                    HeaderTitleCol.Width = w.TitleLength;
                    HeaderArtistCol.Width = w.ArtistLength;
                    break;
                case "Artist|Album":
                    w.Artist = left;
                    w.Album = right;
                    HeaderArtistCol.Width = w.ArtistLength;
                    HeaderAlbumCol.Width = w.AlbumLength;
                    break;
                case "Album|Year":
                    w.Album = left;
                    w.Year = right;
                    HeaderAlbumCol.Width = w.AlbumLength;
                    HeaderYearCol.Width = w.YearLength;
                    break;
                case "Year|Duration":
                    w.Year = left;
                    w.Duration = right;
                    HeaderYearCol.Width = w.YearLength;
                    HeaderDurationCol.Width = w.DurationLength;
                    break;
            }
        }


        private void HideHoverTip()
        {
            if (_activeHoverTip != null)
            {
                _activeHoverTip.IsOpen = false;
                _activeHoverTip = null;
            }

            if (_hoverElement != null)
            {
                ToolTipService.SetToolTip(_hoverElement, null);
            }
        }
    }
}
