// MainWindow.OpraWall.cs
// 耳机校正（OPRA）页的易用性补丁：热门品牌墙 / 最近与收藏 / AutoEq 徽章 / 曲线预览。
//
// 这四件事都不是新的 DSP 能力，纯粹是「少打几次字、少点几次鼠标」：
//   · 品牌墙：OPRA 库里型号很多，多数人只想找自己手上那副耳机，按品牌筛是最快的入口
//   · 最近/收藏：校正曲线不常换，换季或换耳机时才动，不该每次都重新搜一遍
//   · AutoEq 徽章：同一副耳机常常同时有 OPRA 官方曲线和 AutoEq 贡献的曲线，
//     两者口径不同（测量机构 vs 社区汇总），值得在界面上标出来让用户自己选
//   · 曲线预览：应用之前先看一眼形状，避免「点下去才知道压成了什么样」
//
// 数据真值仍在 OpraService（内存数据库）与 OpraHistoryStore（本地 JSON），
// 本文件只负责取出来显示，不持有第二份镜像。

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>预览曲线当前画的是哪条（收藏 / 重画要用）。</summary>
        private OpraProductEqSummary? _opraPreviewEq;
        private OpraSearchResult? _opraPreviewProduct;

        // ─────────────────────────────────────────────────────────────
        // 热门品牌墙
        // ─────────────────────────────────────────────────────────────

        /// <summary>数据库加载完成后按「型号数量」倒序铺一遍品牌芯片。</summary>
        private void BuildOpraVendorWall()
        {
            try
            {
                if (OpraVendorWall == null)
                {
                    return;
                }

                List<OpraVendor> vendors = _opra.GetTopVendors(14);
                if (vendors.Count == 0)
                {
                    OpraVendorWall.ItemsSource = null;
                    if (OpraVendorWallNote != null)
                    {
                        OpraVendorWallNote.Text = "OPRA 数据库还没加载好，先等一下（或检查网络）。";
                    }

                    return;
                }

                OpraVendorWall.ItemsSource = vendors
                    .Select(v => new OpraVendorChip(v.Name, _opra.GetProductCountByVendor(v.Id)))
                    .ToList();

                if (OpraVendorWallNote != null)
                {
                    OpraVendorWallNote.Text = $"共 {vendors.Count} 个热门品牌（括号里是该品牌收录的型号数）。点一下直接搜。";
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.BuildOpraVendorWall", caught);
            }
        }

        private void OpraVendorWall_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not OpraVendorChip chip)
            {
                return;
            }

            if (OpraSearchBox != null)
            {
                OpraSearchBox.Text = chip.Name;
            }

            if (OpraSearchButton != null)
            {
                OpraSearchButton_Click(OpraSearchButton, new RoutedEventArgs());
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 最近用过 / 收藏
        // ─────────────────────────────────────────────────────────────

        /// <summary>把收藏（★）和最近（🕘）拼成一个列表显示；两份都没有时整卡隐藏。</summary>
        private void RefreshOpraRecentList()
        {
            try
            {
                if (OpraRecentList == null || OpraRecentCard == null)
                {
                    return;
                }

                OpraHistoryState st = OpraHistoryStore.Load();
                var entries = new List<OpraRecentEntry>();

                foreach (OpraHistoryItem f in st.Favorites)
                {
                    entries.Add(OpraRecentEntry.From(f, mark: "★"));
                }

                foreach (OpraHistoryItem r in st.Recent)
                {
                    // 已经以收藏身份出现过的不重复列
                    if (st.Favorites.Any(x => x.EqId == r.EqId))
                    {
                        continue;
                    }

                    entries.Add(OpraRecentEntry.From(r, mark: "🕘"));
                }

                OpraRecentList.ItemsSource = entries;
                OpraRecentCard.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RefreshOpraRecentList", caught);
            }
        }

        private void OpraRecentList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not OpraRecentEntry entry)
            {
                return;
            }

            // 历史条目只记住了型号和曲线 ID，型号本体还在 OPRA 库里 —— 直接按型号名搜一遍，
            // 搜不到就说明本地缓存被清过或该条已被上游下架，如实告诉用户。
            if (!string.IsNullOrWhiteSpace(entry.ProductName) && OpraSearchBox != null)
            {
                OpraSearchBox.Text = entry.ProductName;
                OpraSearchButton_Click(OpraSearchButton ?? new Button(), new RoutedEventArgs());

                if (OpraEqStatusText != null)
                {
                    OpraEqStatusText.Text = $"已从历史里找到「{entry.Title}」，在右边选曲线即可应用。";
                }
            }
        }

        private void OpraFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_opraPreviewEq == null || _opraPreviewProduct == null)
            {
                return;
            }

            try
            {
                OpraHistoryItem item = new(
                    _opraPreviewEq.EqId,
                    _opraPreviewProduct.ProductId,
                    _opraPreviewProduct.Name,
                    _opraPreviewProduct.VendorName,
                    _opraPreviewEq.Author,
                    _opraPreviewEq.BandCount,
                    _opraPreviewEq.PreampDb);

                bool nowFavorite = OpraHistoryStore.ToggleFavorite(item);
                if (OpraFavoriteButton != null)
                {
                    OpraFavoriteButton.Content = nowFavorite ? "已收藏" : "收藏";
                }

                RefreshOpraRecentList();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.OpraFavoriteButton_Click", caught);
            }
        }

        /// <summary>应用成功后把这条记进「最近用过」（收藏不动）。</summary>
        private void NoteOpraApplied(OpraCorrection corr)
        {
            OpraHistoryStore.AddRecent(new OpraHistoryItem(
                corr.EqId, corr.ProductId, corr.ProductName, corr.VendorName,
                corr.Author, corr.ImportedBandCount, corr.Curve.PreampDb));

            RefreshOpraRecentList();
        }

        // ─────────────────────────────────────────────────────────────
        // 徽章 + 曲线预览
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 选中一条曲线后更新徽章并画预览。
        /// AutoEq 的判断只能靠作者字段里是否含 "autoeq" —— OPRA 库没有单独的来源字段，
        /// 这是一条保守的字符串匹配：命中就标，不命中不硬猜。
        /// </summary>
        private void UpdateOpraPreview(OpraSearchResult product, OpraProductEqSummary eq)
        {
            _opraPreviewProduct = product;
            _opraPreviewEq = eq;

            try
            {
                bool autoEq = eq.Author != null
                    && eq.Author.Contains("autoeq", StringComparison.OrdinalIgnoreCase);

                if (OpraAutoEqBadge != null)
                {
                    OpraAutoEqBadge.Visibility = autoEq ? Visibility.Visible : Visibility.Collapsed;
                }

                if (OpraSampleBadge != null && OpraSampleBadgeText != null)
                {
                    OpraSampleBadge.Visibility = Visibility.Visible;
                    OpraSampleBadgeText.Text = autoEq ? "社区测量" : "OPRA 库";
                }

                if (OpraFavoriteButton != null)
                {
                    OpraFavoriteButton.IsEnabled = true;
                    OpraFavoriteButton.Content = OpraHistoryStore.IsFavorite(eq.EqId) ? "已收藏" : "收藏";
                }

                RedrawOpraPreview();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateOpraPreview", caught);
            }
        }

        private void ClearOpraPreview()
        {
            _opraPreviewEq = null;
            _opraPreviewProduct = null;

            if (OpraAutoEqBadge != null)
            {
                OpraAutoEqBadge.Visibility = Visibility.Collapsed;
            }

            if (OpraSampleBadge != null)
            {
                OpraSampleBadge.Visibility = Visibility.Collapsed;
            }

            if (OpraFavoriteButton != null)
            {
                OpraFavoriteButton.IsEnabled = false;
                OpraFavoriteButton.Content = "收藏";
            }

            if (OpraPreviewCanvas != null)
            {
                OpraPreviewCanvas.Children.Clear();
            }

            if (OpraPreviewNote != null)
            {
                OpraPreviewNote.Text = "在上面选一条曲线，这里就画出它把哪些频率抬起来、哪些压下去。";
            }
        }

        private void OpraPreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawOpraPreview();
        }

        internal void RedrawOpraCanvas()
        {
            RedrawOpraPreview();
        }

        /// <summary>
        /// 画选中曲线的总响应（preamp + 各段叠加）。
        /// 段响应复用 EQ 曲线那个 ApproxBandGain —— 预览和应用后 EQ 页看到的形状是同一套算法，
        /// 不会出现「预览长这样、应用完变那样」。
        /// </summary>
        private void RedrawOpraPreview()
        {
            Canvas? canvas = OpraPreviewCanvas;
            if (canvas == null || canvas.ActualWidth <= 2 || canvas.ActualHeight <= 2)
            {
                return;
            }

            if (_opraPreviewEq == null)
            {
                canvas.Children.Clear();
                return;
            }

            try
            {
                OpraCorrection? corr = _opra.BuildCorrection(_opraPreviewEq.EqId);
                if (corr?.Curve.Bands == null)
                {
                    canvas.Children.Clear();
                    if (OpraPreviewNote != null)
                    {
                        OpraPreviewNote.Text = "这条曲线解析不出来，没法预览。";
                    }

                    return;
                }

                double w = canvas.ActualWidth;
                double h = canvas.ActualHeight;
                double padL = 44, padR = 14, padT = 12, padB = 22;
                double plotW = w - padL - padR;
                double plotH = h - padT - padB;
                if (plotW <= 2 || plotH <= 2)
                {
                    return;
                }

                const double fMin = 20.0, fMax = 20000.0;
                double logMin = Math.Log10(fMin), logMax = Math.Log10(fMax);

                // 纵轴自适应：至少 ±6dB，避免小幅度曲线看起来像平地
                double peak = Math.Abs(corr.Curve.PreampDb);
                foreach (EqBand b in corr.Curve.Bands)
                {
                    if (Math.Abs(b.GainDb) > peak)
                    {
                        peak = Math.Abs(b.GainDb);
                    }
                }

                double maxDb = Math.Max(6.0, Math.Ceiling(peak / 3.0) * 3.0);

                double X(double f) => padL + (Math.Log10(Math.Clamp(f, fMin, fMax)) - logMin) / (logMax - logMin) * plotW;
                double Y(double db) => padT + plotH / 2 - (db / maxDb) * (plotH / 2);

                bool dark = IsDspCanvasHostDark(OpraPreviewHost);
                Color gridWeak = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
                Color gridStrong = dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(74, 0, 0, 0);
                Color labelColor = dark ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(145, 30, 30, 30);
                Color accent = dark ? Color.FromArgb(255, 0x2e, 0x71, 0x68) : Color.FromArgb(255, 0x0f, 0x76, 0x6e);
                if (GeekDspAccentColor() is Color ph)
                {
                    accent = ph;
                }

                var children = canvas.Children;
                children.Clear();

                foreach (double f in new[] { 20.0, 50.0, 100.0, 200.0, 500.0, 1000.0, 2000.0, 5000.0, 10000.0, 20000.0 })
                {
                    children.Add(new Shapes.Line
                    {
                        X1 = X(f), Y1 = padT, X2 = X(f), Y2 = padT + plotH,
                        Stroke = new SolidColorBrush(gridWeak), StrokeThickness = 1
                    });
                    string t = f >= 1000 ? (f / 1000.0).ToString("0.#") + "k" : f.ToString("0");
                    children.Add(MakeOpraLabel(t, X(f) - 16, padT + plotH + 4, 32, TextAlignment.Center, labelColor));
                }

                // 0 dB 基准线加粗，横轴刻度给 ±maxDb 和 0
                children.Add(new Shapes.Line
                {
                    X1 = padL, Y1 = Y(0), X2 = padL + plotW, Y2 = Y(0),
                    Stroke = new SolidColorBrush(gridStrong), StrokeThickness = 1.4
                });
                foreach (double db in new[] { maxDb, 0.0, -maxDb })
                {
                    children.Add(MakeOpraLabel(
                        (db > 0 ? "+" : "") + db.ToString("0"), 0, Y(db) - 7, 38, TextAlignment.Right, labelColor));
                }

                var pts = new PointCollection();
                int samples = 220;
                for (int i = 0; i < samples; i++)
                {
                    double f = Math.Pow(10.0, logMin + (logMax - logMin) * i / (samples - 1));
                    double g = corr.Curve.PreampDb;
                    foreach (EqBand b in corr.Curve.Bands)
                    {
                        if (b.Enabled)
                        {
                            g += ApproxBandGain(f, b);
                        }
                    }

                    pts.Add(new Windows.Foundation.Point(X(f), Y(g)));
                }

                var area = new PointCollection();
                area.Add(new Windows.Foundation.Point(X(fMin), Y(0)));
                foreach (Windows.Foundation.Point p in pts)
                {
                    area.Add(p);
                }

                area.Add(new Windows.Foundation.Point(X(fMax), Y(0)));
                children.Add(new Shapes.Polygon
                {
                    Points = area,
                    Fill = new SolidColorBrush(Color.FromArgb((byte)(dark ? 40 : 34), accent.R, accent.G, accent.B)),
                    Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(padL, padT, plotW, plotH) }
                });
                children.Add(new Shapes.Polyline
                {
                    Points = pts,
                    Stroke = new SolidColorBrush(accent),
                    StrokeThickness = 2.2,
                    StrokeLineJoin = PenLineJoin.Round,
                    Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(padL - 2, padT - 2, plotW + 4, plotH + 4) }
                });

                if (OpraPreviewNote != null)
                {
                    double lo = SumOpraGainAt(corr.Curve, 60.0);
                    double hi = SumOpraGainAt(corr.Curve, 8000.0);
                    OpraPreviewNote.Text = $"共 {corr.Curve.Bands.Count} 段（预增益 {(corr.Curve.PreampDb >= 0 ? "+" : "")}{corr.Curve.PreampDb:0.#} dB）："
                        + $"低音 60Hz {(lo >= 0 ? "+" : "")}{lo:0.#} dB，高音 8kHz {(hi >= 0 ? "+" : "")}{hi:0.#} dB。";
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RedrawOpraPreview", caught);
            }
        }

        private static double SumOpraGainAt(EqCurveState curve, double freq)
        {
            double g = curve.PreampDb;
            foreach (EqBand b in curve.Bands)
            {
                if (b.Enabled)
                {
                    g += ApproxBandGain(freq, b);
                }
            }

            return g;
        }

        private static UIElement MakeOpraLabel(string text, double x, double y, double width, TextAlignment align, Color color)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new SolidColorBrush(color),
                Width = width,
                TextAlignment = align
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            return tb;
        }
    }

    /// <summary>品牌墙上的一颗芯片（x:Bind x:DataType 只认顶层类型，参照 DspRackRow 的做法）。</summary>
    public sealed class OpraVendorChip
    {
        public string Name { get; }
        public int Count { get; }
        public string CountLabel => "(" + Count + ")";

        public OpraVendorChip(string name, int count)
        {
            Name = name;
            Count = count;
        }
    }

    /// <summary>「最近用过 / 收藏」列表里的一行。</summary>
    public sealed class OpraRecentEntry
    {
        public string Mark { get; }
        public string Title { get; }
        public string SubTitle { get; }
        public string EqId { get; }
        public string ProductId { get; }
        public string ProductName { get; }
        public string VendorName { get; }
        public string Author { get; }
        public int BandCount { get; }
        public double PreampDb { get; }

        private OpraRecentEntry(string mark, string title, string subTitle, string eqId,
            string productId, string productName, string vendorName, string author, int bandCount, double preampDb)
        {
            Mark = mark;
            Title = title;
            SubTitle = subTitle;
            EqId = eqId;
            ProductId = productId;
            ProductName = productName;
            VendorName = vendorName;
            Author = author;
            BandCount = bandCount;
            PreampDb = preampDb;
        }

        public static OpraRecentEntry From(OpraHistoryItem item, string mark)
            => new(mark,
                string.IsNullOrEmpty(item.VendorName) ? item.ProductName : item.VendorName + " " + item.ProductName,
                $"作者 {item.Author} · {item.BandCount} 段",
                item.EqId, item.ProductId, item.ProductName, item.VendorName,
                item.Author, item.BandCount, item.PreampDb);
    }
}
