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
        private CancellationTokenSource? _opraCts;
        private bool _opraDbLoadStarted;
        private List<OpraSearchResult> _opraResults = new();
        private OpraSearchResult? _opraSelectedProduct;
        private List<OpraProductEqSummary> _opraEqs = new();
        private OpraProductEqSummary? _opraSelectedEq;

        /// <summary>第一次在 EQ 页展开耳机校正时加载 OPRA 数据库（联网失败自动用本地缓存）。</summary>
        private void EnsureOpraLoaded()
        {
            if (_opraDbLoadStarted)
            {
                return;
            }

            _opraDbLoadStarted = true;
            _ = LoadOpraDatabaseAsync();
        }

        private async Task LoadOpraDatabaseAsync()
        {
            try
            {

                OpraStatus status = await _opra.EnsureLoadedAsync(refresh: false);

                // 数据库就绪才铺品牌墙（厂商与型号数都来自这个库）
                BuildOpraVendorWall();
            }
            catch (Exception ex)
            {
            }
        }






    }

    internal static class OpraCorrectionExtensions
    {
        /// <summary>"厂商 / 型号 / 作者" 展示名。</summary>
        public static string ProductVendorAndName(this OpraCorrection c)
            => string.Join(" / ", new[] { c.VendorName, c.ProductName, c.Author });
    }
}
