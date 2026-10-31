// MainWindow.DspMatrix.cs
// 声道矩阵页的 2×2 信号流图：把四个混合系数画成「输入 L/R → 输出 L/R」的四条通路。
//
// 职责边界（同 MainWindow.DspComp.cs 的约定）：
//   · 参数真值只有一份，在 DspRackStore；加载/下发/预设都在 MainWindow.DspRack.cs，
//     本文件只负责「当场读滑杆 → 画出来」，不持有第二份镜像。
//   · 面板就绪守卫沿用 DspRack.cs 的 _dspRackReady —— XAML 滑杆的 Value 初值会在
//     InitializeComponent 解析阶段就地触发 ValueChanged，不挡就是进程秒崩（0xC000027B）。
//
// 为什么必须有这张图：四个系数两两交叉，用户在滑杆上很难建立「哪一路 feeds 哪一路」的直觉，
// 尤其是「左 → 右」这种对角项。图把这件事变成看得见的东西。

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>节点圆点直径。</summary>
        private const double MatrixNodeDiameter = 34.0;

        /// <summary>两条对角弧在中点的分离距离（px）。不分开的话会重成一条线。</summary>
        private const double MatrixArcSpread = 26.0;





    }
}
