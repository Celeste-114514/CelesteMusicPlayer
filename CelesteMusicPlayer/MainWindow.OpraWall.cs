// MainWindow.OpraWall.cs
// 耳机校正（OPRA）区块的易用性补丁：热门品牌墙 / 最近与收藏 / AutoEq 徽章 / 曲线预览。
// 区块在「参数 EQ」页下半部分（2026-10-01 并入 EQ 页）。
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
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.OpraSourceBadge_Tapped", caught);
            }
        }


        // ─────────────────────────────────────────────────────────────
        // 热门品牌墙
        // ─────────────────────────────────────────────────────────────

        /// <summary>数据库加载完成后按「型号数量」倒序铺一遍品牌芯片。</summary>
        private void BuildOpraVendorWall()
        {
            try
            {

                List<OpraVendor> vendors = _opra.GetTopVendors(14);
                if (vendors.Count == 0)
                {

                    return;
                }

            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.BuildOpraVendorWall", caught);
            }
        }

        /// <summary>应用成功后把这条记进「最近用过」（收藏不动）。</summary>
        private void NoteOpraApplied(OpraCorrection corr)
        {
            OpraHistoryStore.AddRecent(new OpraHistoryItem(
                corr.EqId, corr.ProductId, corr.ProductName, corr.VendorName,
                corr.Author, corr.ImportedBandCount, corr.Curve.PreampDb));

        }

        // ─────────────────────────────────────────────────────────────
        // 徽章 + 曲线预览
        // ─────────────────────────────────────────────────────────────

        internal void RedrawOpraCanvas()
        {
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
