using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>耳机校正（OPRA）独立窗口：从 Roon 开源 OPRA 数据库搜索耳机型号并应用其校正 EQ 曲线。</summary>
    public sealed partial class HeadphoneCorrectionWindow : Window
    {
        private static HeadphoneCorrectionWindow? _instance;
        private readonly OpraService _opra = new();
        private CancellationTokenSource? _cts;
        private List<OpraSearchResult> _results = new();
        private OpraSearchResult? _selectedProduct;
        private List<OpraProductEqSummary> _eqs = new();
        private OpraProductEqSummary? _selectedEq;

        public HeadphoneCorrectionWindow()
        {
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "耳机校正 · OPRA";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new SizeInt32(1360, 830));

            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();
            _ = LoadDatabaseAsync();

            Closed += (_, _) =>
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                _selectedProduct = null;
                _selectedEq = null;
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            };
        }

        public static void OpenOrActivate()
        {
            if (_instance != null)
            {
                _instance.Activate();
                return;
            }

            _instance = new HeadphoneCorrectionWindow();
            _instance.Activate();
        }

        private void ConfigureTitleBarButtons()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }

        private void ApplyBackdropFromSettings()
        {
            AppSettingsState s = AppSettingsStore.Load();
            if (s.EnableFrostedGlass)
            {
                FrostedGlass.ApplyWindowBackdrop(this);
            }
            else
            {
                SystemBackdrop = null;
            }
        }

        private async Task LoadDatabaseAsync(CancellationToken ct = default)
        {
            StatusText.Text = "正在下载 OPRA 数据库…";
            try
            {
                OpraStatus status = await _opra.EnsureLoadedAsync(refresh: false, ct);
                StatusText.Text =
                    $"OPRA 已就绪：{status.VendorCount} 厂商 / {status.ProductCount} 型号 / {status.EqCount} 条曲线" +
                    (status.Source == "network" ? "（已联网下载）" : "（使用本地缓存）");
            }
            catch (Exception ex)
            {
                StatusText.Text = "加载 OPRA 数据库失败：" + ex.Message;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SearchButton_Click(this, new RoutedEventArgs());
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            string q = SearchBox.Text?.Trim() ?? string.Empty;
            if (q.Length == 0)
            {
                StatusText.Text = "请输入耳机型号 / 品牌关键词";
                return;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            try
            {
                OpraStatus status = await _opra.EnsureLoadedAsync(false, _cts.Token);
                if (status.EqCount == 0)
                {
                    StatusText.Text = "OPRA 数据库为空，请检查网络后重试。";
                    return;
                }

                SearchButton.IsEnabled = false;
                SearchStatusText.Text = "搜索中…";
                var results = await _opra.SearchAsync(q, 24, ct: _cts.Token);
                _results = results;
                ResultList.ItemsSource = _results;
                SearchStatusText.Text = results.Count == 0 ? "未找到匹配的耳机（换个品牌/型号试试）" : $"找到 {results.Count} 款（点击选曲线）";
                ClearDetail();
            }
            catch (OperationCanceledException caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HeadphoneCorrectionWindow.xaml.cs", caught); }
            catch (Exception ex)
            {
                SearchStatusText.Text = "搜索失败：" + ex.Message;
            }
            finally
            {
                SearchButton.IsEnabled = true;
            }
        }

        private void ResultList_ItemClick(object sender, ItemClickEventArgs e)        {
            if (e.ClickedItem is OpraSearchResult r)
            {
                _selectedProduct = r;
                _eqs = _opra.GetEqsForProduct(r.ProductId);
                EqList.ItemsSource = _eqs;
                DetailTitle.Text = r.Name;
                DetailSubTitle.Text = r.VendorName + (string.IsNullOrEmpty(r.Subtype) ? "" : " · " + r.Subtype) + $"（{r.EqCount} 条曲线）";
                EqStatusText.Text = _eqs.Count == 0 ? "该型号暂无可用曲线" : "选择一条曲线后点击「应用」";
                EqApplyButton.IsEnabled = false;
                _selectedEq = null;
            }
        }

        private void EqList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is OpraProductEqSummary s)
            {
                _selectedEq = s;
                EqApplyButton.IsEnabled = true;
                EqDetailText.Text = $"作者：{s.Author}\n滤波段数：{s.BandCount}\n预增益：{(s.PreampDb >= 0 ? "+" : "")}{s.PreampDb:0.##} dB";
            }
        }

        private async void EqApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedProduct == null || _selectedEq == null)
            {
                return;
            }

            try
            {
                EqApplyButton.IsEnabled = false;
                EqStatusText.Text = "正在解析并应用…";
                OpraCorrection? corr = _opra.BuildCorrection(_selectedEq.EqId);
                if (corr == null)
                {
                    EqStatusText.Text = "该曲线无法解析（参数格式不支持）。";
                    return;
                }

                if (MainWindow.Instance != null)
                {
                    MainWindow.Instance.ApplyOpraCurve(corr.Curve);
                }
                else
                {
                    // 主窗口未就绪：仍持久化，下次播放加载。
                    EqCurveStore.Save(corr.Curve);
                }

                EqStatusText.Text = $"已应用：{corr.ProductVendorAndName()}（{corr.ImportedBandCount} 段 + {corr.Curve.PreampDb} dB）\n提示：可在主窗口「音效处理」面板查看/微调，开启 EQ 后输出非 bit-perfect。";
            }
            catch (Exception ex)
            {
                EqStatusText.Text = "应用失败：" + ex.Message;
            }
            finally
            {
                EqApplyButton.IsEnabled = true;
            }
        }

        private void ClearDetail()
        {
            _eqs = new List<OpraProductEqSummary>();
            EqList.ItemsSource = _eqs;
            DetailTitle.Text = string.Empty;
            DetailSubTitle.Text = string.Empty;
            EqDetailText.Text = string.Empty;
            EqStatusText.Text = string.Empty;
            EqApplyButton.IsEnabled = false;
        }
    }

    internal static class OpraCorrectionExtensions
    {
        /// <summary>"厂商 / 型号 / 作者" 展示名。</summary>
        public static string ProductVendorAndName(this OpraCorrection c)
            => string.Join(" / ", new[] { c.VendorName, c.ProductName, c.Author });
    }
}
