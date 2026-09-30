using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 耳机校正（OPRA）内嵌页：搜索 / 选曲线 / 应用全流程在 DSP 板块「耳机校正」页完成。
    /// 2026-09-30 由原独立窗口 HeadphoneCorrectionWindow 迁入（用户要求整合进主程序），
    /// 服务层 OpraService 与「应用后走 ApplyOpraCurve」的链路不变。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly OpraService _opra = new();
        private CancellationTokenSource? _opraCts;
        private bool _opraDbLoadStarted;
        private List<OpraSearchResult> _opraResults = new();
        private OpraSearchResult? _opraSelectedProduct;
        private List<OpraProductEqSummary> _opraEqs = new();
        private OpraProductEqSummary? _opraSelectedEq;

        /// <summary>第一次进入耳机校正页时加载 OPRA 数据库（联网失败自动用本地缓存）。</summary>
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
                    OpraStatusText.Text = "正在下载 OPRA 数据库…";
                }

                OpraStatus status = await _opra.EnsureLoadedAsync(refresh: false);
                if (OpraStatusText != null)
                {
                    OpraStatusText.Text =
                        $"OPRA 已就绪：{status.VendorCount} 厂商 / {status.ProductCount} 型号 / {status.EqCount} 条曲线"
                        + (status.Source == "network" ? "（已联网下载）" : "（使用本地缓存）");
                }

                // 数据库就绪才铺品牌墙（厂商与型号数都来自这个库）
                BuildOpraVendorWall();
            }
            catch (Exception ex)
            {
                if (OpraStatusText != null)
                {
                    OpraStatusText.Text = "加载 OPRA 数据库失败：" + ex.Message;
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
                    OpraStatusText.Text = "请输入耳机型号 / 品牌关键词";
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
                        OpraStatusText.Text = "OPRA 数据库为空，请检查网络后重试。";
                    }

                    return;
                }

                if (OpraSearchButton != null)
                {
                    OpraSearchButton.IsEnabled = false;
                }

                if (OpraSearchStatusText != null)
                {
                    OpraSearchStatusText.Text = "搜索中…";
                }

                var results = await _opra.SearchAsync(q, 24, ct: _opraCts.Token);
                _opraResults = results;
                if (OpraResultList != null)
                {
                    OpraResultList.ItemsSource = _opraResults;
                }

                if (OpraSearchStatusText != null)
                {
                    OpraSearchStatusText.Text = results.Count == 0
                        ? "未找到匹配的耳机（换个品牌/型号试试）"
                        : $"找到 {results.Count} 款（点击选曲线）";
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
                    OpraSearchStatusText.Text = "搜索失败：" + ex.Message;
                }
            }
            finally
            {
                if (OpraSearchButton != null)
                {
                    OpraSearchButton.IsEnabled = true;
                }
            }
        }

        private void OpraResultList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is OpraSearchResult r)
            {
                _opraSelectedProduct = r;
                _opraEqs = _opra.GetEqsForProduct(r.ProductId);
                if (OpraEqList != null)
                {
                    OpraEqList.ItemsSource = _opraEqs;
                }

                if (OpraDetailTitle != null)
                {
                    OpraDetailTitle.Text = r.Name;
                }

                if (OpraDetailSubTitle != null)
                {
                    OpraDetailSubTitle.Text = r.VendorName
                        + (string.IsNullOrEmpty(r.Subtype) ? "" : " · " + r.Subtype)
                        + $"（{r.EqCount} 条曲线）";
                }

                if (OpraEqStatusText != null)
                {
                    OpraEqStatusText.Text = _opraEqs.Count == 0 ? "该型号暂无可用曲线" : "选择一条曲线后点击「应用」";
                }

                if (OpraApplyButton != null)
                {
                    OpraApplyButton.IsEnabled = false;
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
                    OpraApplyButton.IsEnabled = true;
                }

                if (OpraEqDetailText != null)
                {
                    OpraEqDetailText.Text = $"作者：{s.Author}\n滤波段数：{s.BandCount}\n预增益：{(s.PreampDb >= 0 ? "+" : "")}{s.PreampDb:0.##} dB";
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
                    OpraApplyButton.IsEnabled = false;
                }

                if (OpraEqStatusText != null)
                {
                    OpraEqStatusText.Text = "正在解析并应用…";
                }

                OpraCorrection? corr = _opra.BuildCorrection(_opraSelectedEq.EqId);
                if (corr == null)
                {
                    if (OpraEqStatusText != null)
                    {
                        OpraEqStatusText.Text = "该曲线无法解析（参数格式不支持）。";
                    }

                    return;
                }

                ApplyOpraCurve(corr.Curve);
                if (OpraEqStatusText != null)
                {
                    OpraEqStatusText.Text = $"已应用：{corr.ProductVendorAndName()}（{corr.ImportedBandCount} 段 + {corr.Curve.PreampDb} dB）\n提示：可在「参数 EQ」页查看/微调，开启 EQ 后输出非 bit-perfect。";
                }

                // 记进「最近用过」，下次进来不用再搜一遍
                NoteOpraApplied(corr);
            }
            catch (Exception ex)
            {
                if (OpraEqStatusText != null)
                {
                    OpraEqStatusText.Text = "应用失败：" + ex.Message;
                }
            }
            finally
            {
                if (OpraApplyButton != null)
                {
                    OpraApplyButton.IsEnabled = true;
                }
            }
        }

        private void ClearOpraDetail()
        {
            ClearOpraPreview();
            _opraEqs = new List<OpraProductEqSummary>();
            if (OpraEqList != null)
            {
                OpraEqList.ItemsSource = _opraEqs;
            }

            if (OpraDetailTitle != null)
            {
                OpraDetailTitle.Text = string.Empty;
            }

            if (OpraDetailSubTitle != null)
            {
                OpraDetailSubTitle.Text = string.Empty;
            }

            if (OpraEqDetailText != null)
            {
                OpraEqDetailText.Text = string.Empty;
            }

            if (OpraEqStatusText != null)
            {
                OpraEqStatusText.Text = string.Empty;
            }

            if (OpraApplyButton != null)
            {
                OpraApplyButton.IsEnabled = false;
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
