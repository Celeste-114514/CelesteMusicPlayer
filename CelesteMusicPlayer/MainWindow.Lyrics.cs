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
        /// 记录当前曲目的歌词。真正的消费者只有两个：桌面歌词窗口，以及网页歌词区通过
        /// LyricsBridge 读的「当前第几句」。旧界面那套逐行 TextBlock + 白框选中 + 自动吸附
        /// 滚动全部作用在垫片控件上，看不见也点不到，已随 XAML 一起删掉。
        /// </summary>
        private void BuildLyricsUi(List<LyricLine> lyrics)
        {
            _lyricLines = lyrics;
            _currentLyricIndex = -1;
            // 所有调用点都带"当前曲目"守卫（Playback3 换曲 / Features 下载歌词），
            // 所以这份歌词就是对应当前曲子的——记下来，网页歌词区靠它防张冠李戴。
            _lyricsLoadedPath = _nowPlayingPath ?? "";

            if (lyrics.Count == 0)
            {
                ClearLyricsUi("该音频没有歌词");
                return;
            }

            SyncLyricsToPosition(GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero);
            _desktopLyricsWindow?.SetLyrics(_lyricLines);
        }

        private void ClearLyricsUi(string hint)
        {
            _lyricLines = new List<LyricLine>();
            _currentLyricIndex = -1;
            _desktopLyricsWindow?.SetLyrics(_lyricLines);
        }

        // ---- 歌词手动滚动 + 单击跳进度 ----

        // ---- 歌词行选中（经典式：选中 → 右侧时间 + 左侧播放按钮）----

    }
}
