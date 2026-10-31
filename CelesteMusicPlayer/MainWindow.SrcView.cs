// MainWindow.SrcView.cs
// SRC 页的可视化：用法卡片 / 信号链路图 / 重采样滤波器示意曲线。
//
// ⚠ 诚实性红线：这张滤波器曲线是「示意图」，不是实测。
//   真实响应由 WDL 重采样器按质量档位生成的具体系数决定，本项目没有把系数导出来画真曲线的通道。
//   所以：① 卡片标题就写「示意图」 ② 图下方用文字明确说清它不实测 ③ 不标任何具体 dB 数值当真值。
//   只画「通带保持平坦 → 过渡带快速掉下去」这个所有重采样滤波器共有的形状，
//   以及质量档位越高过渡带越窄这个定性关系。
//
// 参数真值仍在 AppSettingsStore（MainWindow.Src.cs 负责读写），本文件只读不写。

using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>示意曲线：各质量档位对应的过渡带起点（相对 Nyquist 的比例）。
        /// 只是定性关系 —— 档位越高过渡带越窄，不是实测值。</summary>
        private static readonly (string Key, double KneeStart)[] SrcFilterKnees =
        {
            (ResamplingSourceProvider.QualityLowLatency, 0.78),
            (ResamplingSourceProvider.QualityBalanced, 0.88),
            (ResamplingSourceProvider.QualityTransparent, 0.94),
        };

        internal void RedrawSrcCanvas()
        {
        }

        /// <summary>
        /// 从引擎的会话描述里解析源/目标采样率（形如 "44.1k→96k（balanced）"）。
        /// 引擎没把这两个值做成结构化字段，只能这么取；解析不到就返回 false，调用方显示"未知"。
        /// </summary>
        private bool TryParseSrcRates(out int srcHz, out int outHz)
        {
            srcHz = 0;
            outHz = 0;

            try
            {
                string desc = _audioEngine?.SrcStateDescription ?? string.Empty;
                Match m = Regex.Match(desc, @"(\d+(?:[.,]\d+)?)k\s*→\s*(\d+(?:[.,]\d+)?)k");
                if (!m.Success)
                {
                    return false;
                }

                double a = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                double b = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                srcHz = (int)Math.Round(a * 1000);
                outHz = (int)Math.Round(b * 1000);
                return srcHz > 0 && outHz > 0;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.TryParseSrcRates", caught);
                return false;
            }
        }

    }
}
