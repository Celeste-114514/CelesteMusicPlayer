using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CelesteMusicPlayer
{
    /// <summary>一个分类框 = 一个分类维度（如「流派」）。Entries 为该维度的全部取值（含各自在全曲库的数量）。</summary>
    /// <remarks>⚠ 必须是顶层类：XAML 的 x:DataType 引解析不了嵌套在 MainWindow 里的类型。</remarks>
    public sealed class TagSortFacetBox : INotifyPropertyChanged
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public ObservableCollection<TagSortFacetEntry> Entries { get; } = new();

        private string? _selected;

        /// <summary>当前选中值（阶段 1 单选语义；阶段 2 扩展为多选集合）。</summary>
        public string? Selected
        {
            get => _selected;
            set
            {
                if (!string.Equals(_selected, value, StringComparison.OrdinalIgnoreCase))
                {
                    _selected = value;
                    OnPropertyChanged(nameof(Selected));
                    OnPropertyChanged(nameof(HasSelected));
                }
            }
        }

        public bool HasSelected => !string.IsNullOrEmpty(_selected);

        public string CountText => $"{Entries.Count} 项";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>分类框内一行「文件夹」= 该分类维度的一个取值。</summary>
    public sealed class TagSortFacetEntry : INotifyPropertyChanged
    {
        public string Value { get; init; } = string.Empty;

        private int _count;

        /// <summary>当前筛选条件下该取值的曲目数（随其他框的选择联动刷新）。</summary>
        public int Count
        {
            get => _count;
            set
            {
                if (_count != value)
                {
                    _count = value;
                    OnPropertyChanged(nameof(Count));
                    OnPropertyChanged(nameof(CountText));
                }
            }
        }

        public string CountText => _count > 0 ? _count.ToString() : "0";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 标签排序板块 · 一屏浏览（2026-10-01 重构阶段 1）：
    /// 左栏一排分类框（流派/年份/艺术家，行式文件夹），右栏常驻歌单；
    /// 点框内一项，右栏立刻跟着变，不用点进去、也不用返回。
    /// 数据结构按「框间且、框内或」的多选模型设计，阶段 1 只开放单选交互。
    /// </summary>
    public sealed partial class MainWindow
    {
        // ---------- 状态 ----------

        /// <summary>一屏浏览的分类框列表（绑定到左栏 ItemsControl）。</summary>
        private readonly ObservableCollection<TagSortFacetBox> _tagSortFacetBoxes = new();

        /// <summary>右栏歌单数据源（_playlist 经各框筛选 + 列排序后的结果）。</summary>
        private readonly ObservableCollection<PlaylistItem> _tagSortBrowseSongs = new();

        /// <summary>一屏浏览是否为当前显示视图（驱动列头/排序事件走哪套刷新）。</summary>
        private bool _tagSortBrowseActive;

        /// <summary>框数据是否已构建（_playlist 打开板块时构建一次，同旧分类墙时机一致）。</summary>
        private bool _tagSortFacetsBuilt;

        /// <summary>阶段 1 默认分类框（其余字段在阶段 3 做成可配置）。</summary>
        private static readonly string[] TagSortBrowseDefaultFields = { "Genre", "Year", "Artist" };

        // ---------- 视图切换 ----------

        /// <summary>进入一屏浏览视图（从 BreakoutTagSortView 或旧分类墙的「新版浏览」按钮）。</summary>
        private void ShowTagSortBrowse()
        {
            if (TagSortBrowseRoot == null)
            {
                return;
            }

            if (!_tagSortFacetsBuilt)
            {
                BuildTagSortFacetBoxes();
                _tagSortFacetsBuilt = true;
            }

            _tagSortBrowseActive = true;
            TagSortBrowseRoot.Visibility = Visibility.Visible;
            TagSortClassScroll.Visibility = Visibility.Collapsed;
            TagSortPanel.Visibility = Visibility.Collapsed;

            ApplyTagSortBrowseFilter();
            RebuildTagSortColumnHeaders(TagSortBrowseHeaderGrid);
        }

        /// <summary>回到旧版分类墙视图（保留为可回退入口，重构完成后再评估去留）。</summary>
        private void ShowTagSortLegacyWall()
        {
            _tagSortBrowseActive = false;
            if (TagSortBrowseRoot != null)
            {
                TagSortBrowseRoot.Visibility = Visibility.Collapsed;
            }

            ShowTagSortClassWall();
        }

        // ---------- 分类框构建 ----------

        private void BuildTagSortFacetBoxes()
        {
            _tagSortFacetBoxes.Clear();

            foreach (string key in TagSortBrowseDefaultFields)
            {
                var def = TagSortFields.Find(key);
                if (def == null)
                {
                    continue;
                }

                var box = new TagSortFacetBox { Key = key, Label = def.Label };

                // 全曲库分组一次，得到该维度的取值集合与固定顺序（之后只就地更新计数，不重建条目，
                // 避免刷新计数时丢掉 ListView 的选中态）
                var pairs = _playlist
                    .GroupBy(p => TagSortFieldVal(p, key), StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Value: g.Key, Count: g.Count()))
                    .ToList();

                if (TagSortFields.IsNumeric(key))
                {
                    // 数值字段（年份）按数值大小，未知排在最后
                    pairs.Sort((a, b) =>
                    {
                        bool an = int.TryParse(a.Value, out int av);
                        bool bn = int.TryParse(b.Value, out int bv);
                        if (an && bn) return av.CompareTo(bv);
                        if (an) return -1;
                        if (bn) return 1;
                        return string.Compare(a.Value, b.Value, StringComparison.CurrentCultureIgnoreCase);
                    });
                }
                else
                {
                    pairs.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.CurrentCultureIgnoreCase));
                }

                foreach (var (value, count) in pairs)
                {
                    box.Entries.Add(new TagSortFacetEntry { Value = value, Count = count });
                }

                _tagSortFacetBoxes.Add(box);
            }

            if (TagSortFacetPanel != null && TagSortFacetPanel.ItemsSource == null)
            {
                TagSortFacetPanel.ItemsSource = _tagSortFacetBoxes;
            }
        }

        // ---------- 筛选与联动 ----------

        /// <summary>各框选择应用到右栏歌单，并联动刷新每框的计数（其他框条件下的剩余数量）。</summary>
        private void ApplyTagSortBrowseFilter()
        {
            IEnumerable<PlaylistItem> songs = _playlist;
            foreach (TagSortFacetBox box in _tagSortFacetBoxes)
            {
                if (string.IsNullOrEmpty(box.Selected))
                {
                    continue;
                }

                string selected = box.Selected;
                songs = songs.Where(p => string.Equals(TagSortFieldVal(p, box.Key), selected, StringComparison.OrdinalIgnoreCase));
            }

            List<PlaylistItem> ordered = SortTagSortPanelSongs(songs.ToList());
            _tagSortBrowseSongs.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Index = i + 1;
                _tagSortBrowseSongs.Add(ordered[i]);
            }

            if (TagSortBrowseCountText != null)
            {
                TagSortBrowseCountText.Text = ordered.Count > 0
                    ? $"共 {ordered.Count} 首"
                    : "没有符合条件的歌曲";
            }

            if (TagSortBrowseClearButton != null)
            {
                TagSortBrowseClearButton.Visibility = _tagSortFacetBoxes.Any(b => b.HasSelected)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (TagSortBrowseEmptyHint != null)
            {
                TagSortBrowseEmptyHint.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            RefreshTagSortFacetCounts();
        }

        /// <summary>计数联动：每框显示「在其他框已选条件下」各取值的曲目数（自身选中项保持全量数）。</summary>
        private void RefreshTagSortFacetCounts()
        {
            foreach (TagSortFacetBox box in _tagSortFacetBoxes)
            {
                Dictionary<string, int> counts = CountTagSortValues(box.Key, except: box);
                foreach (TagSortFacetEntry entry in box.Entries)
                {
                    entry.Count = counts.TryGetValue(entry.Value, out int c) ? c : 0;
                }
            }
        }

        /// <summary>按字段统计曲库中各取值的数量；except 非空时先把该框自身的选择从条件里剔除（它看的是自己的候选集）。</summary>
        private Dictionary<string, int> CountTagSortValues(string field, TagSortFacetBox? except)
        {
            IEnumerable<PlaylistItem> songs = _playlist;
            foreach (TagSortFacetBox box in _tagSortFacetBoxes)
            {
                if (box == except || string.IsNullOrEmpty(box.Selected))
                {
                    continue;
                }

                string selected = box.Selected;
                songs = songs.Where(p => string.Equals(TagSortFieldVal(p, box.Key), selected, StringComparison.OrdinalIgnoreCase));
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (PlaylistItem p in songs)
            {
                string value = TagSortFieldVal(p, field);
                counts[value] = counts.TryGetValue(value, out int c) ? c + 1 : 1;
            }

            return counts;
        }

        // ---------- 分类框交互 ----------

        /// <summary>点框内一行：选中该值；再点一次已选中的行 = 取消选择。</summary>
        private void TagSortFacetItemClick(object sender, ItemClickEventArgs e)
        {
            if (sender is not ListView list || list.Tag is not string key)
            {
                return;
            }

            if (e.ClickedItem is not TagSortFacetEntry entry)
            {
                return;
            }

            TagSortFacetBox? box = _tagSortFacetBoxes.FirstOrDefault(b => b.Key == key);
            if (box == null)
            {
                return;
            }

            if (string.Equals(box.Selected, entry.Value, StringComparison.OrdinalIgnoreCase))
            {
                box.Selected = null;
                list.SelectedItem = null;
            }
            else
            {
                box.Selected = entry.Value;
            }

            ApplyTagSortBrowseFilter();
        }

        /// <summary>选择变化兜底同步（键盘操作等不走 ItemClick 的路径）。</summary>
        private void TagSortFacetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListView list || list.Tag is not string key)
            {
                return;
            }

            TagSortFacetBox? box = _tagSortFacetBoxes.FirstOrDefault(b => b.Key == key);
            if (box == null)
            {
                return;
            }

            string? selected = list.SelectedItem is TagSortFacetEntry entry ? entry.Value : null;
            if (!string.Equals(box.Selected, selected, StringComparison.OrdinalIgnoreCase))
            {
                box.Selected = selected;
                ApplyTagSortBrowseFilter();
            }
        }

        private void TagSortBrowseClearButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (TagSortFacetBox box in _tagSortFacetBoxes)
            {
                box.Selected = null;
            }

            ApplyTagSortBrowseFilter();
        }

        private void TagSortBrowseLegacyButton_Click(object sender, RoutedEventArgs e)
            => ShowTagSortLegacyWall();

        private void TagSortBrowsePlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tagSortBrowseSongs.Count == 0)
            {
                return;
            }

            _userPlaylist.Clear();
            AddSongsToUserPlaylist(_tagSortBrowseSongs.ToList());
            PlayUserPlaylistAt(0);
        }

        // ---------- 右栏歌单事件（与旧面板逐曲目列表同一套行为） ----------

        private void TagSortBrowseSongList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistItem song)
            {
                PlayPlaylistItem(song);
            }
        }

        private void TagSortBrowseSongList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container
                && TagSortBrowseSongList != null)
            {
                ApplySongListItemSelectionChrome(TagSortBrowseSongList, container, song);
                if (container.ContentTemplateRoot is Border rowBorder
                    && rowBorder.Child is Grid rowGrid)
                {
                    if (!Equals(rowGrid.Tag, _tagSortColumnVersion))
                    {
                        rowGrid.Tag = _tagSortColumnVersion;
                        BuildTagSortSongRow(rowGrid, song);
                    }
                    else
                    {
                        UpdateTagSortSongRow(rowGrid, song);
                    }
                }
            }
        }

        private void TagSortBrowseSongList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TagSortBrowseSongList != null)
            {
                RefreshRealizedSongListSelectionChrome(TagSortBrowseSongList);
            }
        }

        private void TagSortBrowseSongList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode || TagSortBrowseSongList == null)
            {
                return;
            }

            if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistItem song)
            {
                _multiSelectTargetList = TagSortBrowseSongList;
                var flyout = BuildPlaylistItemContextMenu(song, inUserPlaylist: false,
                    multiSelectAction: () => EnterMultiSelectModeFrom(TagSortBrowseSongList));
                flyout.ShowAt(TagSortBrowseSongList, e.GetPosition(TagSortBrowseSongList));
            }
        }
    }
}
