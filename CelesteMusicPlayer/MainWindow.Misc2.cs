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
            }
        }


        /// <summary>
        /// 列宽适配的帧合并：窗口缩放时 SizeChanged / AppWindow.Changed 每个 tick 都灌进来，
        /// 每次都改 5 根列宽 = 每次都触发整个播放列表（表头 + 已实现行）重排，
        /// 就是「主UI改变大小卡」的根源（2026-10-13 用户实测，O(n²) 修掉之后仍然卡）。
        /// 统一走 ~16ms 定时器：一个显示帧只应用一次，最终尺寸不变。
        /// </summary>
        private DispatcherQueueTimer? _fitColumnsTimer;

        private void ScheduleFitColumns()
        {
            if (_fitColumnsTimer == null)
            {
                _fitColumnsTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _fitColumnsTimer.Interval = TimeSpan.FromMilliseconds(16);
                _fitColumnsTimer.Tick += FitColumnsTimer_Tick;
            }
            _fitColumnsTimer.Stop();
            _fitColumnsTimer.Start();
        }

        private void FitColumnsTimer_Tick(dynamic sender, dynamic e)
        {
            sender.Stop();
            FitColumnsToAvailableWidth();
        }


        // =====================================================================
        // 播放列表列：可拖分割线
        // =====================================================================













    }
}
