using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 耳机校正（OPRA）区块：搜索 / 选曲线 / 应用全流程在 DSP 板块「参数 EQ」页下半部分完成。
    /// 2026-09-30 由原独立窗口 HeadphoneCorrectionWindow 迁入，2026-10-01 再并入 EQ 页
    /// （不再单独占一页）；服务层 OpraService 与「应用后走 ApplyOpraCurve」的链路不变。
    /// </summary>
    public sealed partial class MainWindow
    {
        private List<OpraSearchResult> _opraResults = new();
        private List<OpraProductEqSummary> _opraEqs = new();

    }

    internal static class OpraCorrectionExtensions
    {
        /// <summary>"厂商 / 型号 / 作者" 展示名。</summary>
        public static string ProductVendorAndName(this OpraCorrection c)
            => string.Join(" / ", new[] { c.VendorName, c.ProductName, c.Author });
    }
}
