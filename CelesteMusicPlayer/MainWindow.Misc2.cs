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
        }

        // =====================================================================
        // 播放列表列：可拖分割线
        // =====================================================================

    }
}
