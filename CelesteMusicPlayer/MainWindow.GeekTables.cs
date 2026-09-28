using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// C 区主区表格化：曲库(歌曲) / 播放列表两个浏览视图在极客界面下
    /// 从「卡片 + 封面」重塑为「纯文字等宽表格」—— 无封面、列头大写 + 细线下划线、行间表格线。
    ///
    /// ⚠ 专辑墙 / 艺术家墙**不参与表格化**（用户拍板 2026-09-29）：这两面浏览墙保留经典皮肤的
    /// 封面卡片样式，极客下只把专辑封面磨成直角、艺术家头像保持圆形。改动点在 UiTheme.cs 的
    /// ApplyGeekAlbumCardFrame。（历史版本曾把四面墙全换成文字行，等于删掉封面，已废弃。）
    ///
    /// 两条路子，都沿用 GeekBrowse 那套「原值备份 → 切走还原」：
    ///   1. 歌曲列表（本来就是列表）：行内封面隐藏 + 封面列宽归零走 ApplyGeekRowDetailChrome
    ///      （UiTheme.cs，虚拟化晚实现的行也不会漏），本文件只负责往固定列头行填内容；
    ///   2. 三堵卡片墙（专辑墙 / 艺术家墙 / 播放列表墙，外加艺术家详情里那张专辑墙）：
    ///      ItemsWrapGrid 是横向环绕排布，做不成"一行一条"，所以整块换
    ///      ItemsPanel(垂直 ItemsStackPanel) + ItemTemplate(文字行) + ItemContainerStyle，
    ///      原值存备份，退出极客逐项赋回。文字行模板是 XAML 里编译好的
    ///      （x:Bind 编译绑定，比运行时拼 XAML 稳），放在 MainContentGrid.Resources。
    ///
    /// 列头不挂 grid.Header：MS 文档明确 GridView 默认把 Header 渲染在左侧（ListView 才在顶部），
    /// 列头会跑到墙边上。改为各墙父容器里的专用列头 Border（XAML 预置，经典模式无 Child=零高度），
    /// 极客下由这里填 Child，退出时清空。专辑/艺术家墙的列头 Border 还 x:Bind 了墙自身的
    /// Visibility —— 进详情页墙被收起时列头跟着藏，返回自动恢复，不用逐个 Visibility 赋值点打补丁。
    ///
    /// 切换是幂等的：重复 ApplyGeekTables(true) 只在第一次生效；还原只在真的有备份时做。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>卡片墙切极客前的原值（进极客时记下，出极客时赋回）。</summary>
        private sealed class GeekWallBackup
        {
            public DataTemplate? ItemTemplate;
            public ItemsPanelTemplate? ItemsPanel;
            public Style? ContainerStyle;
            public object? Header;
        }

        /// <summary>极客表格是否已生效（幂等闸门；半路出错时按实际备份还原，不留残状态）。</summary>
        private bool _geekTablesActive;

        /// <summary>各卡片墙的极客前原值：墙 → 原值。只有 ConvertWall 成功记入的才会被还原。</summary>
        private readonly Dictionary<GridView, GeekWallBackup> _geekWallBackups = new();

        /// <summary>列头底部细线：比行表格线略亮一档，读起来才是"表头分隔"而不是普通行线。</summary>
        private static readonly Color GeekHeaderRuleColor = Color.FromArgb(38, 0xFF, 0xFF, 0xFF);

        /// <summary>MainContentGrid.Resources 里的极客表格资源键。</summary>
        private const string GeekVerticalPanelKey = "GeekVerticalItemsPanel";
        private const string GeekAlbumRowTemplateKey = "GeekAlbumRowTemplate";
        private const string GeekArtistRowTemplateKey = "GeekArtistRowTemplate";
        private const string GeekPlaylistCardRowTemplateKey = "GeekPlaylistCardRowTemplate";

        /// <summary>
        /// C 区表格化总入口：geek=true 把四视图换成纯文字表格，false 逐项还原。
        /// 由 ApplyGeekChrome 在界面风格切换时调用一次。
        /// </summary>
        internal void ApplyGeekTables(bool geek)
        {
            try
            {
                if (geek == _geekTablesActive)
                {
                    return;
                }

                _geekTablesActive = geek;

                if (geek)
                {
                    EnterGeekTables();
                }
                else
                {
                    ExitGeekTables();
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ApplyGeekTables", caught);
            }
        }

        private void EnterGeekTables()
        {
            // 1) 卡片墙 → 文字表格。列头只在墙真的换成了表格时才填
            //    （资源缺失导致转换失败时保持原样，不挂孤儿列头）。
            //
            //    ⚠ 专辑墙 / 艺术家墙**不再表格化**（用户拍板 2026-09-29）：浏览页面要保留
            //    经典皮肤那套「封面卡片墙」的样子，极客下只接受两条最小改动 ——
            //      专辑卡：封面从 10px 圆角磨成直角（AlbumCoverFrame，见 UiTheme.ApplyGeekAlbumCardFrame）
            //      艺术家卡：头像保持圆形，其余原样
            //    早期版本把这两面墙整个换成纯文字表格，等于把封面 / 头像删了，用户明确否掉。
            //    仍然表格化的只剩播放列表墙，以及艺术家详情页里那张专辑墙（属详情页，非浏览页）。
            if (ConvertWallToTable(PlaylistWallGridView, GeekPlaylistCardRowTemplateKey))
            {
                GeekPlaylistWallHeader.Child = BuildGeekPlaylistHeader();
            }

            if (ConvertWallToTable(ArtistAlbumGridView, GeekAlbumRowTemplateKey))
            {
                GeekArtistAlbumWallHeader.Child = BuildGeekAlbumHeader();
            }

            // 2) 歌曲面板（歌曲 / 我喜欢的 / 评分 / 最近 / 用户播放列表共用）：往固定列头行填内容。
            //    行内封面隐藏与封面列归零由 ApplyGeekRowDetailChrome 逐行处理，不在这里。
            if (PlaylistView != null)
            {
                GeekSongsHeader.Child = BuildGeekSongsHeader();
            }
        }

        private void ExitGeekTables()
        {
            foreach (KeyValuePair<GridView, GeekWallBackup> pair in _geekWallBackups)
            {
                GridView grid = pair.Key;
                GeekWallBackup backup = pair.Value;
                grid.ItemsPanel = backup.ItemsPanel;
                grid.ItemTemplate = backup.ItemTemplate;
                grid.ItemContainerStyle = backup.ContainerStyle;
                grid.Header = backup.Header;
            }

            _geekWallBackups.Clear();

            // 列头 Border 清空 Child：无内容即零高度，经典模式不可见（Visibility 不动它，
            // 专辑/艺术家墙的列头 Visibility 绑着墙自身，清 Child 就够）。
            ClearGeekHeader(GeekAlbumWallHeader);
            ClearGeekHeader(GeekArtistWallHeader);
            ClearGeekHeader(GeekPlaylistWallHeader);
            ClearGeekHeader(GeekArtistAlbumWallHeader);
            ClearGeekHeader(GeekSongsHeader);

            // 多选退出时会把"当时的容器样式"当默认样式缓存（??= 只抓第一次）。
            // 极客换过专辑墙的容器样式，若不清掉这个缓存，退出极客之后再进多选，
            // 退出多选时会把极客样式恢复成默认样式（经典模式下一条 Margin=0 的表格行混进卡片墙）。
            // 置空让下次多选重新抓当前真值。
            _libraryAlbumItemDefaultStyle = null;
            _artistAlbumItemDefaultStyle = null;
        }

        /// <summary>清空列头内容；XAML 被改坏（字段为 null）时静默跳过，不影响其它还原。</summary>
        private static void ClearGeekHeader(Border? header)
        {
            if (header != null)
            {
                header.Child = null;
            }
        }

        /// <summary>
        /// 把一堵卡片墙换成极客文字表格：备份原值 → 换垂直面板 / 文字行模板 / 表格容器样式。
        /// 返回 false 表示没换（视图或资源缺失），调用方据此不挂列头。
        /// </summary>
        private bool ConvertWallToTable(GridView grid, string templateKey)
        {
            if (grid == null || MainContentGrid == null)
            {
                return false;
            }

            if (!MainContentGrid.Resources.TryGetValue(templateKey, out object? templateObject)
                || templateObject is not DataTemplate template
                || !MainContentGrid.Resources.TryGetValue(GeekVerticalPanelKey, out object? panelObject)
                || panelObject is not ItemsPanelTemplate panel)
            {
                // 资源缺失（XAML 被改坏）时保持原样，不影响其它视图
                return false;
            }

            // 先记原值再赋值：即使中途出错，还原时也能把每一项放回去
            _geekWallBackups[grid] = new GeekWallBackup
            {
                ItemTemplate = grid.ItemTemplate,
                ItemsPanel = grid.ItemsPanel,
                ContainerStyle = grid.ItemContainerStyle,
                Header = grid.Header,
            };

            grid.ItemsPanel = panel;
            grid.ItemTemplate = template;
            grid.ItemContainerStyle = BuildGeekWallContainerStyle();
            return true;
        }

        /// <summary>极客表格的容器样式：零边距、直角、透明底、内容拉伸铺满（一行一条的关键）。</summary>
        private static Style BuildGeekWallContainerStyle()
        {
            Style style = new Style(typeof(GridViewItem));
            style.Setters.Add(new Setter(GridViewItem.MarginProperty, new Thickness(0)));
            style.Setters.Add(new Setter(GridViewItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(GridViewItem.CornerRadiusProperty, new CornerRadius(0)));
            style.Setters.Add(new Setter(GridViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(GridViewItem.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            style.Setters.Add(new Setter(GridViewItem.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))));
            style.Setters.Add(new Setter(GridViewItem.BorderThicknessProperty, new Thickness(0)));
            return style;
        }

        /// <summary>
        /// 构建极客列头（Grid，挂到 XAML 预置的列头 Border 上）：等宽、小字号、暗一档 + 底部 1px 细线。
        /// 列宽约定：&gt;=0 固定像素；-1 = Star；-2 = Auto。Label 为空只占位不建文字
        /// （歌曲表的封面列归零后要留一个 0 宽列才能和行对齐）。
        /// rightPad 跟行内对应 TextBlock 的右侧 Margin 对齐（歌曲行时长列避开滚动条留了 16）。
        /// </summary>
        private static Grid BuildGeekTableHeader((double Width, string Label, bool Right)[] columns, Thickness margin, double rightPad)
        {
            Grid grid = new Grid
            {
                ColumnSpacing = 12,
                Margin = margin,
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            };

            for (int i = 0; i < columns.Length; i++)
            {
                (double width, string label, bool right) = columns[i];

                GridLength columnWidth = width switch
                {
                    -1 => new GridLength(1, GridUnitType.Star),
                    -2 => GridLength.Auto,
                    _ => new GridLength(Math.Max(0, width)),
                };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = columnWidth });

                if (string.IsNullOrEmpty(label))
                {
                    continue;
                }

                TextBlock cell = new TextBlock
                {
                    Text = label,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    // 右对齐列右侧留白与行内对应 TextBlock 的 Margin 对齐
                    Margin = right ? new Thickness(0, 0, rightPad, 0) : new Thickness(0),
                };
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }

            // 列头细线：铺满整行底部
            Border rule = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(GeekHeaderRuleColor),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsHitTestVisible = false,
            };
            Grid.SetColumnSpan(rule, 99);
            grid.Children.Add(rule);

            return grid;
        }

        /// <summary>歌曲表面头（行结构：序号40 | 封面→0 | 信息* | 时长Auto；行底板 Padding=10,8；时长列右侧留白 16 避滚动条）。</summary>
        private static Grid BuildGeekSongsHeader()
            => BuildGeekTableHeader(new[]
            {
                (40.0, "#", false),
                (0.0, string.Empty, false),
                (-1.0, "TRACK", false),
                (-2.0, "TIME", true),
            }, new Thickness(10, 2, 10, 4), 16);

        /// <summary>专辑表面头（行结构：名称* | 艺术家180 | 曲目数72 | 总时长72；行底板 Padding=8,7）。</summary>
        private static Grid BuildGeekAlbumHeader()
            => BuildGeekTableHeader(new[]
            {
                (-1.0, "ALBUM", false),
                (180.0, "ARTIST", false),
                (72.0, "TRACKS", true),
                (72.0, "TIME", true),
            }, new Thickness(8, 4, 8, 4), 0);

        /// <summary>艺术家表面头（行结构：名称* | 曲目数72）。</summary>
        private static Grid BuildGeekArtistHeader()
            => BuildGeekTableHeader(new[]
            {
                (-1.0, "ARTIST", false),
                (72.0, "TRACKS", true),
            }, new Thickness(8, 4, 8, 4), 0);

        /// <summary>播放列表墙表头（行结构：名称* | 歌曲数72）。</summary>
        private static Grid BuildGeekPlaylistHeader()
            => BuildGeekTableHeader(new[]
            {
                (-1.0, "PLAYLIST", false),
                (72.0, "SONGS", true),
            }, new Thickness(8, 4, 8, 4), 0);
    }
}
