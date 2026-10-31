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
                if (OpraStatusText != null)
                {
                }

                OpraStatus status = await _opra.EnsureLoadedAsync(refresh: false);
                if (OpraStatusText != null)
                {
                }

                // 数据库就绪才铺品牌墙（厂商与型号数都来自这个库）
                BuildOpraVendorWall();
            }
            catch (Exception ex)
            {
                if (OpraStatusText != null)
                {
                }
            }
        }

        private void OpraSearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter && OpraSearchButton != null)
            {
                OpraSearchButton_Click(OpraSearchButton, new Microsoft.UI.Xaml.RoutedEventArgs());
            }
        }

        private async void OpraSearchButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            string q = OpraSearchBox?.Text?.Trim() ?? string.Empty;
            if (q.Length == 0)
            {
                if (OpraStatusText != null)
                {
                }

                return;
            }

            _opraCts?.Cancel();
            _opraCts?.Dispose();
            _opraCts = new CancellationTokenSource();
            try
            {
                OpraStatus status = await _opra.EnsureLoadedAsync(false, _opraCts.Token);
                if (status.EqCount == 0)
                {
                    if (OpraStatusText != null)
                    {
                    }

                    return;
                }

                if (OpraSearchButton != null)
                {
                }

                if (OpraSearchStatusText != null)
                {
                }

                var results = await _opra.SearchAsync(q, 24, ct: _opraCts.Token);
                _opraResults = results;
                if (OpraResultList != null)
                {
                }

                if (OpraSearchStatusText != null)
                {
                }

                ClearOpraDetail();
            }
            catch (OperationCanceledException)
            {
                // 用户又发起了一次新搜索：旧搜索静默退出
            }
            catch (Exception ex)
            {
                if (OpraSearchStatusText != null)
                {
                }
            }
            finally
            {
                if (OpraSearchButton != null)
                {
                }
            }
        }

        private void OpraResultList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is OpraSearchResult r)
            {
                _opraSelectedProduct = r;
                _opraEqs = _opra.GetEqsForProduct(r.ProductId);

                // 走筛选入口而不是直接铺列表：换型号时用户选过的来源筛选保持生效
                ApplyOpraEqFilter();

                if (OpraDetailTitle != null)
                {
                }

                if (OpraDetailSubTitle != null)
                {
                }

                if (OpraEqStatusText != null)
                {
                }

                if (OpraApplyButton != null)
                {
                }

                // 换了型号，旧的曲线预览/徽章/收藏状态全部作废
                ClearOpraPreview();
                _opraSelectedEq = null;
            }
        }

        private void OpraEqList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is OpraProductEqSummary s)
            {
                _opraSelectedEq = s;
                if (OpraApplyButton != null)
                {
                }

                if (OpraEqDetailText != null)
                {
                }

                // 选中即预览（徽章 + 曲线形状 + 收藏按钮状态一起更新）
                if (_opraSelectedProduct != null)
                {
                    UpdateOpraPreview(_opraSelectedProduct, s);
                }
            }
        }

        private void OpraApplyButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_opraSelectedProduct == null || _opraSelectedEq == null)
            {
                return;
            }

            try
            {
                if (OpraApplyButton != null)
                {
                }

                if (OpraEqStatusText != null)
                {
                }

                OpraCorrection? corr = _opra.BuildCorrection(_opraSelectedEq.EqId);
                if (corr == null)
                {
                    if (OpraEqStatusText != null)
                    {
                    }

                    return;
                }

                ApplyOpraCurve(corr.Curve);
                if (OpraEqStatusText != null)
                {
                }

                // 记进「最近用过」，下次进来不用再搜一遍
                NoteOpraApplied(corr);
            }
            catch (Exception ex)
            {
                if (OpraEqStatusText != null)
                {
                }
            }
            finally
            {
                if (OpraApplyButton != null)
                {
                }
            }
        }

        private void ClearOpraDetail()
        {
            ClearOpraPreview();
            _opraEqs = new List<OpraProductEqSummary>();
            if (OpraEqList != null)
            {
            }

            if (OpraDetailTitle != null)
            {
            }

            if (OpraDetailSubTitle != null)
            {
            }

            if (OpraEqDetailText != null)
            {
            }

            if (OpraEqStatusText != null)
            {
            }

            if (OpraApplyButton != null)
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
