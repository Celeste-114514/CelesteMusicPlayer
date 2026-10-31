// MainWindow.DspComp.cs
// 压缩器页的专业视图：静态传递曲线 / 入口电平表 / 压缩量历史折线。
//
// 职责边界（与 MainWindow.DspRack.cs 的分工）：
//   · 参数真值只有一份，在 DspRackStore（dsp-rack.json）。加载/下发/预设都在 MainWindow.DspRack.cs，
//     本文件一律「当场读滑杆 → 画出来」，不持有第二份参数镜像，避免和 store 漂移。
//   · GR 轮询定时器也在 DspRack.cs（EnsureDspCompGrTimer / DspCompGr_Tick），
//     Tick 只做「拿读数 → 交给 UpdateDspCompMeters」，绘制全在本文件。
//   · 传递曲线用的压缩公式是从 native/dsp-core/audio-engine/CompressorProcessor.cpp
//     的 computeReductionDb 逐行复刻来的（含软拐点的二次曲线段），所以曲线与内核实际行为一致，
//     不是另一套近似。改内核公式时必须同步改这里。
//
// 面板就绪守卫：所有 handler 都用 DspRack.cs 的 _dspRackReady 挡第一道。
// 理由同 MainWindow.EqDsp.cs：XAML 里 Slider 的 Value 初值、ToggleSwitch 的 IsOn 初值
// 会在 InitializeComponent 解析阶段就地触发事件，此时同页后续控件的 x:Name 字段还没连上，
// 直接访问就是空引用 → 整个进程秒崩（0xC000027B）。

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
// 项目统一约定：Shapes 走别名。WinUI3 的 Shapes 与别处同名类型容易打架，
// 写成别名既能用 Shapes.Line 这种短写法，又不会污染全局类型解析。
using Shapes = Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>传递曲线的纵/横轴范围（dBFS）。入口 -60..0 已覆盖常见母带电平。</summary>
        private const double CompCurveMinDb = -60.0;
        private const double CompCurveMaxDb = 0.0;

        /// <summary>传递曲线的取样点数（整幅宽 ~840px，2 点/px 足够平滑）。</summary>
        private const int CompCurveSamples = 241;

        /// <summary>压缩量历史：200ms 一拍 × 40 拍 = 最近 8 秒。</summary>
        private const int CompGrHistoryMax = 40;

        // 电平取样缓冲（LevelMeter 每声道一份，最多取前 16 声道里最大的）
        private readonly float[] _dspCompPeak = new float[16];
        private readonly float[] _dspCompRms = new float[16];

        /// <summary>最近的压缩量读数序列（dB，正 = 压掉多少），环形增长到常量上限后丢弃最旧的。</summary>
        private readonly List<double> _dspCompGrHistory = new();

        /// <summary>true = 电平表显示峰值，false = 显示 RMS（与页内开关同步）。</summary>
        private bool _dspCompMeterShowsPeak = true;

        // ─────────────────────────────────────────────────────────────
        // 内核公式复刻（CompressorProcessor::computeReductionDb）
        // ─────────────────────────────────────────────────────────────



        /// <summary>
        /// 宿主底色偏暗 = 深色主题。判断方式与 EQ 曲线的 IsEqCurveDarkTheme 一致：
        /// 直接看宿主 Border 的 Background 亮度，不依赖 Application.RequestedTheme
        /// （客户区可以单独指定 RequestedTheme，全局主题未必等于实际渲染色）。
        /// 所有纯代码画的画布（EQ 曲线 / 压缩器曲线 / GR 折线 / 矩阵流图）共用这一个判断。
        /// </summary>
        private bool IsDspCanvasHostDark(Border? host)
        {
            try
            {
                if (host?.Background is SolidColorBrush sb)
                {
                    double lum = (0.299 * sb.Color.R + 0.587 * sb.Color.G + 0.114 * sb.Color.B) / 255.0;
                    return lum < 0.5;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.IsDspCanvasHostDark", caught);
            }

            return true;
        }
    }
}
