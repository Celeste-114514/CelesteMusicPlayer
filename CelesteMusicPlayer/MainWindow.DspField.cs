// MainWindow.DspField.cs
// 立体声场页的 M/S 声像圆环：可拖动 + 中间/两侧实际增益读数 + 相关度估算。
//
// 内核口径（native/dsp-core/audio-engine/SpatialDspProcessor.cpp:62-79）：
//   mid  = 0.5*(L+R) * 10^(centerDb/20)
//   side = 0.5*(L-R) * 10^(sideDb/20) * width
//   L' = mid + side , R' = mid - side
// 所以「两侧的实际增益」= sideDb + 20*log10(width) —— 宽度是乘在非 dB 之前的系数，
// 不是又一条 dB。这就是本页读数要单独算出来给用户看的原因：滑杆上的 −18..+18 dB
// 并不是两侧最终拿到的量，width 会再乘一道。
//
// 职责边界：参数真值只有一份（DspRackStore），加载/下发/预设在 MainWindow.DspRack.cs，
// 本文件只做「读滑杆 → 画出来 / 把拖动翻译成滑杆值」。
// 面板就绪守卫沿用 _dspRackReady（XAML 初值会在解析期就地触发事件，不挡就进程秒崩）。

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 圆环拖动期间置 true：滑杆的 ValueChanged 会一路触发到 PushDspRackToEngine
        /// （写 dsp-rack.json + 下发内核），拖一次要写几百次盘。拖动期间由本标志压住，
        /// 松手（PointerReleased）时统一补一次下发。
        /// </summary>


        /// <summary>拖动杆的垂直分量能影响的中间/两侧偏向范围（dB）。刻意留±6，避免拖一把就极端。</summary>
        private const double FieldDragTiltDb = 6.0;

        /// <summary>
        /// 估算输出相关度时的「输入左右相关度」假设值。
        /// 真实值取决于当前这首歌，只能取一个常见混音的代表值，UI 上如实写明是估算。
        /// </summary>
        private const double FieldAssumedInputCorrelation = 0.5;






        /// <summary>圆环几何：圆心 + 半径（画布较短边的 40% 上下，留出标签空间）。</summary>
        private (double cx, double cy, double r) GetDspFieldRingGeometry()
        {
            Canvas? canvas = DspFieldRingCanvas;
            double w = canvas?.ActualWidth ?? 0;
            double h = canvas?.ActualHeight ?? 0;

            double cx = w / 2.0;
            double cy = h / 2.0;
            double r = Math.Min(w, h) / 2.0 - 20.0;
            return (cx, cy, Math.Max(0.0, r));
        }
    }
}
