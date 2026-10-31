using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// ReplayGain 批量扫描（2026-10-12 阶段 2）：原先是独立窗口 ReplayGainScanWindow，
    /// 现在内嵌到「音效处理 → 响度 · ReplayGain」页。
    /// 扫描逻辑照搬原窗口（同一个 ReplayGainScanner），只把 UI 读写换成主窗口内的控件。
    /// 铁律不变：只写标签，绝不改动音频数据。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly ReplayGainScanner _rgScanScanner = new();






    }
}
