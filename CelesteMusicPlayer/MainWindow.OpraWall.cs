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

        /// <summary>来源筛选：null=全部，"community"=只看社区测量，"opra"=只看 OPRA 库。</summary>
        private string? _opraSourceFilter;

        /// <summary>OPRA 库没有独立的来源字段，只能按作者名保守判断（命中 autoeq 才算社区测量）。</summary>
        private static bool IsCommunityAuthor(string? author)
            => author != null && author.Contains("autoeq", StringComparison.OrdinalIgnoreCase);

        /// <summary>点来源徽章 = 在「只看这一类 / 看全部」之间切换。</summary>
        private void OpraSourceBadge_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                if (_opraPreviewEq == null)
                {
                    return;
                }

                string thisSource = IsCommunityAuthor(_opraPreviewEq.Author) ? "community" : "opra";
                _opraSourceFilter = _opraSourceFilter == thisSource ? null : thisSource;
                ApplyOpraEqFilter();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.OpraSourceBadge_Tapped", caught);
            }
        }

        /// <summary>按当前来源筛选重铺曲线列表（列表本体来自 _opraEqs，不重新查库）。</summary>
        private void ApplyOpraEqFilter()
        {
            if (OpraEqList == null)
            {
                return;
            }

            List<OpraProductEqSummary> shown = _opraEqs;
            if (_opraSourceFilter != null)
            {
                bool wantCommunity = _opraSourceFilter == "community";
                shown = _opraEqs.Where(x => IsCommunityAuthor(x.Author) == wantCommunity).ToList();
            }

            OpraEqList.ItemsSource = shown;
            UpdateOpraSourceBadge();

            if (OpraEqStatusText != null && _opraSourceFilter != null && shown.Count == 0)
            {
                OpraEqStatusText.Text = _opraSourceFilter == "community"
                    ? "该型号没有社区测量的曲线，再点一次来源徽章可看全部。"
                    : "该型号没有 OPRA 库自有曲线，再点一次来源徽章可看全部。";
            }
        }

        /// <summary>刷新来源徽章的文字与选中态（选中 = 正在按该来源筛选）。</summary>
        private void UpdateOpraSourceBadge()
        {
            if (OpraSampleBadge == null || OpraSampleBadgeText == null)
            {
                return;
            }

            bool community = _opraPreviewEq != null && IsCommunityAuthor(_opraPreviewEq.Author);
            string label = community ? "社区测量" : "OPRA 库";
            bool active = _opraSourceFilter != null
                && _opraSourceFilter == (community ? "community" : "opra");

            OpraSampleBadge.Visibility = Visibility.Visible;
            OpraSampleBadgeText.Text = active
                ? label + " · 已筛选（点此看全部）"
                : label + " · 点此只看这类";

            // 主动筛查时加一圈强调边框，让「点了有效果」看得见
            OpraSampleBadge.BorderThickness = active ? new Thickness(1) : new Thickness(0);
            OpraSampleBadge.BorderBrush = active
                ? new SolidColorBrush(DspAccentColor())
                : null;
            OpraSampleBadge.Opacity = active ? 1.0 : 0.85;
        }

        /// <summary>当前主题/皮肤下的强调色（复用 DspAccentBrush：极客下自动是磷光色）。</summary>
        private Color DspAccentColor()
            => DspAccentBrush() is SolidColorBrush scb
                ? scb.Color
                : Color.FromArgb(255, 0x0F, 0x76, 0x6E);

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
                bool autoEq = IsCommunityAuthor(eq.Author);

                if (OpraAutoEqBadge != null)
                {
                    OpraAutoEqBadge.Visibility = autoEq ? Visibility.Visible : Visibility.Collapsed;
                }

                // 来源徽章由 UpdateOpraSourceBadge 统一刷新（含「点此筛选」文案与选中态）
                UpdateOpraSourceBadge();

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
            _opraSourceFilter = null;

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

                // ── 纵轴贴合曲线真实范围 ──
                // OPRA 曲线的预增益几乎都是负的（防止叠加增益把峰值顶到削波），
                // 所以「实际响应」整条会沉到 0 dB 以下。若照旧的 ±对称刻度画，
                // 形状会被压扁成贴着 0 的一条线 —— 这里改成贴合 min/max，
                // 并额外画一条「形状」虚线（扣掉预增益），两者差别一眼可见。
                int samples = 240;
                var freqs = new double[samples];
                var shapeDb = new double[samples];
                double lo = 0, hi = 0;
                for (int i = 0; i < samples; i++)
                {
                    double f = Math.Pow(10.0, logMin + (logMax - logMin) * i / (samples - 1));
                    double s = 0;
                    foreach (EqBand b in corr.Curve.Bands)
                    {
                        if (b.Enabled)
                        {
                            s += ApproxBandGain(f, b);
                        }
                    }

                    freqs[i] = f;
                    shapeDb[i] = s;
                    double o = s + corr.Curve.PreampDb;
                    if (i == 0)
                    {
                        lo = Math.Min(s, o);
                        hi = Math.Max(s, o);
                    }
                    else
                    {
                        lo = Math.Min(lo, Math.Min(s, o));
                        hi = Math.Max(hi, Math.Max(s, o));
                    }
                }

                lo -= 1.0;
                hi += 1.0;
                if (hi - lo < 8.0)
                {
                    double mid = (hi + lo) / 2;
                    lo = mid - 4.0;
                    hi = mid + 4.0;
                }

                double span = hi - lo;
                double X(double f) => padL + (Math.Log10(Math.Clamp(f, fMin, fMax)) - logMin) / (logMax - logMin) * plotW;
                double Y(double db) => padT + (hi - db) / span * plotH;

                bool dark = IsDspCanvasHostDark(OpraPreviewHost);
                Color gridWeak = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
                Color gridStrong = dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(74, 0, 0, 0);
                Color labelColor = dark ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(145, 30, 30, 30);
                Color accent = DspAccentColor();
                bool zeroVisible = lo + 0.5 < 0 && 0 < hi - 0.5;

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

                // 0 dB 基准线：只有落在可视区内才画（画在框外会误导）
                if (zeroVisible)
                {
                    children.Add(new Shapes.Line
                    {
                        X1 = padL, Y1 = Y(0), X2 = padL + plotW, Y2 = Y(0),
                        Stroke = new SolidColorBrush(gridStrong), StrokeThickness = 1.4
                    });
                }

                var ticks = new SortedSet<double>();
                ticks.Add(Math.Round(hi - 0.5));
                ticks.Add(Math.Round(lo + 0.5));
                if (zeroVisible)
                {
                    ticks.Add(0);
                }

                foreach (double db in ticks)
                {
                    children.Add(MakeOpraLabel(
                        (db > 0 ? "+" : "") + db.ToString("0"), 0, Y(db) - 7, 38, TextAlignment.Right, labelColor));
                }

                var shapePts = new PointCollection();
                var outPts = new PointCollection();
                for (int i = 0; i < samples; i++)
                {
                    shapePts.Add(new Windows.Foundation.Point(X(freqs[i]), Y(shapeDb[i])));
                    outPts.Add(new Windows.Foundation.Point(X(freqs[i]), Y(shapeDb[i] + corr.Curve.PreampDb)));
                }

                // 面积只在 0 线可见时填（否则会糊成一大块色）
                if (zeroVisible)
                {
                    var area = new PointCollection();
                    area.Add(new Windows.Foundation.Point(X(fMin), Y(0)));
                    foreach (Windows.Foundation.Point p in outPts)
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
                }

                // 形状虚线（不含预增益）：这才是「哪里抬、哪里压」
                // ⚠ 每个 Shape 各自 new 一份几何/集合：WinRT 的 setter 会接管传入实例，共用会抛异常
                children.Add(new Shapes.Polyline
                {
                    Points = shapePts,
                    Stroke = new SolidColorBrush(Color.FromArgb((byte)(dark ? 150 : 130), accent.R, accent.G, accent.B)),
                    StrokeThickness = 1.4,
                    StrokeDashArray = new DoubleCollection { 4, 4 },
                    StrokeLineJoin = PenLineJoin.Round,
                    Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(padL - 2, padT - 2, plotW + 4, plotH + 4) }
                });

                children.Add(new Shapes.Polyline
                {
                    Points = outPts,
                    Stroke = new SolidColorBrush(accent),
                    StrokeThickness = 2.2,
                    StrokeLineJoin = PenLineJoin.Round,
                    Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(padL - 2, padT - 2, plotW + 4, plotH + 4) }
                });

                if (OpraPreviewNote != null)
                {
                    // 报「形状」值（不含预增益）：预增益是防止叠加削波用的整体下移，
                    // 混进来会让人以为低音被压了，而实际只是整条曲线一起沉下去。
                    double loDb = SumOpraGainAt(corr.Curve, 60.0) - corr.Curve.PreampDb;
                    double hiDb = SumOpraGainAt(corr.Curve, 8000.0) - corr.Curve.PreampDb;
                    OpraPreviewNote.Text =
                        $"共 {corr.Curve.Bands.Count} 段。实线 = 套用后的实际响应，虚线 = 校正形状（不含预增益）。"
                        + $"预增益 {(corr.Curve.PreampDb >= 0 ? "+" : "")}{corr.Curve.PreampDb:0.#} dB 是负数在这是正常的："
                        + "它是为了防止几段增益叠起来顶到削波，整条曲线被一起压低，不代表低音也被压了 —— 看虚线才知道抬了哪、压了哪。"
                        + $"60Hz {(loDb >= 0 ? "+" : "")}{loDb:0.#} dB，8kHz {(hiDb >= 0 ? "+" : "")}{hiDb:0.#} dB。";
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
