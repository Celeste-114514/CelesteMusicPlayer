using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 专辑详情页的 DSD 预加载入口（2026-09-24）。
    /// 背景：FiiO KA13 上实时装箱播 DSD 会在固定位置卡顿；同一首预先装箱好的 WAV 直读则不卡（绿灯）。
    /// 所以在专辑页提供"预加载整专辑 DSD"按钮，把整专辑 DSF 提前转成缓存文件，播放时命中缓存即直读。
    /// 缓存目录由用户在选项设置里指定，可单独/整专辑清除；未命中缓存时播放行为完全不变。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly List<string> _albumDetailDsdFiles = new();
        private CancellationTokenSource? _dsdPreloadCts;
        private bool _dsdPreloadRunning;







    }
}
