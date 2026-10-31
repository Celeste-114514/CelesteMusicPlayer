using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十三刀（2026-10-31）：多选模式宿主。
/// 从壳 <c>MainWindow.Misc.cs</c> / <c>EqDsp.cs</c> / <c>Library2.cs</c> / <c>Settings.cs</c>
/// 搬出的「列表多选」一族：选择模式安全切换（None 中转，避免 SelectedItems.Clear 崩溃）、
/// 当前多选目标列表解析、专辑/文件夹多选退出、媒体操作条主按钮语义、行单元格详情文案。
/// 依赖（多选状态字段、默认样式备份、RangeMultiSelect、LegacyControls 控件）都在活文件里。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>列表当前是否处于「可多选」的选择模式（Multiple / Extended 均算）。
    /// 旧代码里散落的「== ListViewSelectionMode.Multiple」判断统一走这里，避免漏改。</summary>
    internal static bool IsMultiSelectSelection(ListViewBase? list)
        => RangeMultiSelect.IsMultiSelectMode(list);

        // W13 第二十三刀：选择模式安全切换

        /// <summary>
        /// 通过 None 中转切换选择模式，安全清空选中项，避免 SelectedItems.Clear 崩溃。
        /// </summary>
        private void SetListSelectionMode(ListView list, ListViewSelectionMode mode)
        {
            try
            {
                list.SelectionMode = ListViewSelectionMode.None;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                list.SelectionMode = mode;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 本应用用主题色圆角底表示选中，关闭 Multiple 模式左侧系统复选框（否则会显示小黑块）
            list.IsMultiSelectCheckBoxEnabled = false;

            try
            {
                list.SelectedItem = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            AttachRangeMultiSelect(list);
        }

        private void SetGridSelectionMode(GridView grid, ListViewSelectionMode mode)
        {
            try
            {
                grid.SelectionMode = ListViewSelectionMode.None;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                grid.SelectionMode = mode;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            grid.IsMultiSelectCheckBoxEnabled = false;

            try
            {
                grid.SelectedItem = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            AttachRangeMultiSelect(grid);
        }

        // W13 第二十三刀：多选退出 + 单元格详情文案

        private void ExitAlbumMultiSelectUiOnly()
        {
            if (_multiSelectAlbumGrid == null)
            {
                return;
            }

            GridView grid = _multiSelectAlbumGrid;
            SetGridSelectionMode(grid, ListViewSelectionMode.Single);
            grid.IsItemClickEnabled = true;
            if (ReferenceEquals(grid, ArtistAlbumGridView) && _artistAlbumItemDefaultStyle != null)
            {
            }
            else if (ReferenceEquals(grid, AlbumGridView) && _libraryAlbumItemDefaultStyle != null)
            {
            }

            ApplyAccentSelectionResources(grid);
            _multiSelectAlbumGrid = null;
        }

        private void ExitFolderMultiSelectUiOnly()
        {
            if (_multiSelectFolderList == null)
            {
                return;
            }

            SetListSelectionMode(FolderBrowserView, ListViewSelectionMode.Single);
            if (_folderItemDefaultStyle != null)
            {
            }

            ApplyAccentSelectionResources(FolderBrowserView);
            _multiSelectFolderList = null;
        }

        /// <summary>根据 TextBlock.Tag 取出对应字段的完整文案</summary>
        private static string? ResolveCellDetailText(FrameworkElement element)
        {
            PlaylistItem? item = VisualTreeWalker.FindPlaylistItem(element);
            if (item == null || element.Tag is not string field)
            {
                return null;
            }

            string label;
            string value;
            switch (field)
            {
                case "Title":
                    label = "标题";
                    value = item.Title;
                    break;
                case "Artist":
                    label = "艺术家";
                    value = item.Artist;
                    break;
                case "Album":
                    label = "专辑";
                    value = item.Album;
                    break;
                case "Year":
                    label = "年份";
                    value = item.YearText;
                    break;
                case "Duration":
                    label = "时长";
                    value = item.DurationText;
                    break;
                default:
                    return null;
            }

            return $"{label}：{value}\n文件：{Path.GetFileName(item.FilePath)}";
        }

        // W13 第二十三刀：当前多选目标列表

        private ListView? ResolveMultiSelectTargetList()
        {
            if (PlaylistDetailBorder.Visibility == Visibility.Visible
                && PlaylistDetailListView != null)
            {
                return PlaylistDetailListView;
            }

            if (AlbumDetailPanel.Visibility == Visibility.Visible
                && AlbumTrackListView != null)
            {
                return AlbumTrackListView;
            }

            if (ArtistDetailPanel.Visibility == Visibility.Visible
                && ArtistTrackListView != null)
            {
                return ArtistTrackListView;
            }

            if (PlaylistListBorder.Visibility == Visibility.Visible)
            {
                return PlaylistView;
            }

            return null;
        }

        // W13 第二十三刀：媒体操作条主按钮语义

        private void ConfigureMultiSelectPrimaryAction()
        {
            bool isUserPlaylist = _multiSelectTargetList == PlaylistView
                && string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal);
            bool isNamedPlaylistDetail = _multiSelectTargetList == PlaylistDetailListView;
            bool isDetailSongList = _multiSelectTargetList == AlbumTrackListView
                || _multiSelectTargetList == ArtistTrackListView;
            if (isUserPlaylist)
            {
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲从播放列表移除");
            }
            else if (isNamedPlaylistDetail)
            {
                // 命中单详情页多选 → 从当前命名单删除勾选的歌
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲从当前命名单移除");
            }
            else if (isDetailSongList)
            {
                // 专辑/艺术家/专辑艺术家详情页多选歌曲 → 主按钮「添加至播放队列」，
                // 右下角副按钮负责「添加到播放列表」（列表墙/命名单），两者不重复。
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲按顺序加入播放队列");
            }
            else if (_multiSelectAlbumGrid != null)
            {
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "按当前专辑顺序、音轨号将选中专辑加入播放队列");
            }
            else if (_multiSelectFolderList != null)
            {
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中文件夹/音频按顺序加入播放队列");
            }
            else
            {
                ToolTipService.SetToolTip(MultiSelectPrimaryActionButton, "将选中歌曲添加至播放队列");
            }

            // “添加到播放列表”按钮：多选界面右下角，与“添加至播放队列”并列显示（支持歌曲/专辑/文件夹统一添加）
            if (MultiSelectAddToPlaylistButton != null)
            {
            }

            if (MultiSelectDeleteMenuItem != null)
            {
            }
        }
    }
