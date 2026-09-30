using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 一屏浏览里的一层分类框。左边第一层是「流派」这样的一整个人群，
    /// 第二层只装第一层筛完还剩的人，往下依次类推。
    /// </summary>
    /// <remarks>⚠ 必须是顶层类：XAML 的 x:DataType 解析不了嵌套在 MainWindow 里的类型。</remarks>
    public sealed class TagSortFacetBox : INotifyPropertyChanged
    {
        public string Key { get; init; } = string.Empty;

        public string Label { get; init; } = string.Empty;

        /// <summary>层级序号（1 起）。第 2 层的候选集来自第 1 层已选值筛完的结果。</summary>
        public int Level { get; init; }

        /// <summary>框标题：层级 + 分类名，如「2 · 年份」。</summary>
        public string Title => Level > 0 ? $"{Level} · {Label}" : Label;

        /// <summary>框内每一行 = 该维度的一个取值（上一层条件下还剩多少首）。</summary>
        public ObservableCollection<TagSortFacetEntry> Entries { get; } = new();

        /// <summary>本框选中的取值。框内多个值是「或」的关系。</summary>
        public HashSet<string> SelectedValues { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasSelection => SelectedValues.Count > 0;

        public string CountText => $"{Entries.Count} 项";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void RaiseSummaryChanged()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CountText));
        }
    }

    /// <summary>分类框内一行「文件夹」= 该分类维度的一个取值。</summary>
    public sealed class TagSortFacetEntry : INotifyPropertyChanged
    {
        public string Value { get; init; } = string.Empty;

        private int _count;

        /// <summary>当前层级条件下该取值的曲目数。</summary>
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

        private bool _isSelected;

        /// <summary>是否选中（框内多选，选中 = 或）。选中态由数据驱动，不依赖 ListView 自己的选中。</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
                OnPropertyChanged(nameof(MarkGlyph));
                OnPropertyChanged(nameof(MarkBrush));
                OnPropertyChanged(nameof(RowBackground));
            }
        }

        /// <summary>没选 = 文件夹图标，选上了 = 对勾。</summary>
        public string MarkGlyph => _isSelected ? "\uE73E" : "\uE8B7";

        public Brush? MarkBrush => _isSelected ? _accent : _idleMark;

        public Brush? RowBackground => _isSelected ? _accentSoft : _transparent;

        private Brush? _accent;
        private Brush? _accentSoft;
        private Brush? _idleMark;
        private readonly Brush _transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        /// <summary>行配色由面板统一下发（跟随主题/极客磷光色）。</summary>
        public void InitBrushes(Brush? accent, Brush? accentSoft, Brush? idleMark)
        {
            _accent = accent;
            _accentSoft = accentSoft;
            _idleMark = idleMark;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 标签排序板块 · 一屏浏览（重构阶段 2，2026-10-01）：
    /// 左栏若干层分类框（一层是一个分类维度，行式文件夹），右栏常驻歌单。
    /// 点左栏一层里的一项，右栏立刻跟着变；再点「＋ 选择分类字段」往下一层加框，
    /// 新框只装上一层筛完剩下的曲目（级联钻取）。
    /// 改前面某一层的选择 → 后面几层的筛选前提已经变了，整段收起。
    /// </summary>
    public sealed partial class MainWindow
    {
        // ---------- 状态 ----------

        /// <summary>左栏分类框（按层级顺序；改动后整段重建）。</summary>
        private List<TagSortFacetBox> _tagSortFacetBoxes = new();

        /// <summary>用户自己加的分类框字段顺序（持久化到设置）。空 = 左侧一个框都不摆。</summary>
        private readonly List<string> _tagSortFacetFields = new();

        private bool _tagSortFacetFieldsLoaded;

        /// <summary>右栏歌单数据源（各框筛选 + 列排序后的结果）。</summary>
        private readonly ObservableCollection<PlaylistItem> _tagSortBrowseSongs = new();

        /// <summary>一屏浏览是否为当前显示视图（驱动列头/排序事件走哪套刷新）。</summary>
        private bool _tagSortBrowseActive;

        /// <summary>可加的分类框字段：取值不会泛滥的那些（标题/文件名这些不适合，留给分组浏览）。</summary>
        private static IEnumerable<TagSortFields.FieldDef> TagSortFacetCandidates
            => TagSortFields.All.Where(f => f.Cardinality == TagSortFields.Cardinality.Low);

        // ---------- 视图切换 ----------

        /// <summary>进入一屏浏览视图（从 BreakoutTagSortView 或旧分类墙的「一屏浏览」按钮）。</summary>
        private void ShowTagSortBrowse()
        {
            if (TagSortBrowseRoot == null)
            {
                return;
            }

            if (!_tagSortFacetFieldsLoaded)
            {
                _tagSortFacetFields.AddRange(AppSettingsStore.Load().TagSortFacetFields ?? new List<string>());
                _tagSortFacetFieldsLoaded = true;
            }

            _tagSortBrowseActive = true;
            TagSortBrowseRoot.Visibility = Visibility.Visible;
            TagSortClassScroll.Visibility = Visibility.Collapsed;
            TagSortPanel.Visibility = Visibility.Collapsed;

            RebuildTagSortFacetBoxes(preserveSelection: true);
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

        /// <summary>
        /// 按字段顺序重建每一层框：第 N 层的候选集 = 前 N-1 层选完之后剩下的曲目。
        /// preserveSelection=true 时尽量保住已有的选中值（值在新候选集里没了就丢掉）。
        /// </summary>
        private void RebuildTagSortFacetBoxes(bool preserveSelection)
        {
            var keep = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (preserveSelection)
            {
                foreach (TagSortFacetBox old in _tagSortFacetBoxes)
                {
                    if (old.HasSelection)
                    {
                        keep[old.Key] = new HashSet<string>(old.SelectedValues, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }

            Brush? accent = ResolveAccentBrush();
            Brush accentSoft = MakeAlphaBrush(accent, 0.18);
            Brush idleMark = ResolveThemeBrush("TextFillColorTertiaryBrush", Windows.UI.Color.FromArgb(255, 130, 130, 130));

            var boxes = new List<TagSortFacetBox>();
            IEnumerable<PlaylistItem> scope = _playlist;

            foreach (string key in _tagSortFacetFields)
            {
                TagSortFields.FieldDef? def = TagSortFields.Find(key);
                if (def == null)
                {
                    continue;
                }

                // 本层能选的，只有在上一层筛完之后还剩下的曲目里出现过的取值
                List<PlaylistItem> candidates = scope.ToList();
                var box = new TagSortFacetBox { Key = key, Label = def.Label, Level = boxes.Count + 1 };

                if (keep.TryGetValue(key, out HashSet<string>? prev))
                {
                    foreach (string value in prev)
                    {
                        box.SelectedValues.Add(value);
                    }
                }

                foreach ((string value, int count) in GroupTagSortFacetValues(candidates, key))
                {
                    var entry = new TagSortFacetEntry { Value = value, Count = count };
                    entry.InitBrushes(accent, accentSoft, idleMark);
                    entry.IsSelected = box.SelectedValues.Contains(value);
                    box.Entries.Add(entry);
                }

                // 上一层换过选择之后，本层原来选的值可能已经不在了 → 丢掉，别让右栏筛成空
                if (box.HasSelection)
                {
                    box.SelectedValues.RemoveWhere(v => !box.Entries.Any(e => string.Equals(e.Value, v, StringComparison.OrdinalIgnoreCase)));
                }

                boxes.Add(box);
                scope = FilterSongsByBox(candidates, box);
            }

            _tagSortFacetBoxes = boxes;
            if (TagSortFacetPanel != null)
            {
                TagSortFacetPanel.ItemsSource = _tagSortFacetBoxes;
            }

            UpdateTagSortFacetChrome();
        }

        /// <summary>某字段在给定曲目里的取值与数量（数值字段按数值排，未知排最后；其余按名字）。</summary>
        private static List<(string Value, int Count)> GroupTagSortFacetValues(List<PlaylistItem> songs, string key)
        {
            List<(string Value, int Count)> pairs = songs
                .GroupBy(p => TagSortFieldVal(p, key), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Value: g.Key, Count: g.Count()))
                .ToList();

            if (TagSortFields.IsNumeric(key))
            {
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

            return pairs;
        }

        /// <summary>按某一层已选的取值筛曲目（框内多值 = 或，没选 = 不参与）。</summary>
        private static IEnumerable<PlaylistItem> FilterSongsByBox(IEnumerable<PlaylistItem> songs, TagSortFacetBox box)
        {
            if (!box.HasSelection)
            {
                return songs;
            }

            HashSet<string> selected = box.SelectedValues;
            return songs.Where(p => selected.Contains(TagSortFieldVal(p, box.Key)));
        }

        /// <summary>空态提示 / 「＋」是否还有字段可加。</summary>
        private void UpdateTagSortFacetChrome()
        {
            if (TagSortFacetEmptyHint != null)
            {
                TagSortFacetEmptyHint.Visibility = _tagSortFacetBoxes.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (TagSortAddFacetButton != null)
            {
                var used = new HashSet<string>(_tagSortFacetFields, StringComparer.OrdinalIgnoreCase);
                bool anyLeft = TagSortFacetCandidates.Any(f => !used.Contains(f.Key));
                TagSortAddFacetButton.IsEnabled = anyLeft;
                ToolTipService.SetToolTip(TagSortAddFacetButton,
                    anyLeft ? "加一层分类框（下一层只装上一层剩下的）" : "能加的字段都用上了");
            }
        }

        // ---------- 筛选与联动 ----------

        /// <summary>各层选择串起来（层与层之间是「且」），结果刷到右栏歌单。</summary>
        private void ApplyTagSortBrowseFilter()
        {
            IEnumerable<PlaylistItem> songs = _playlist;
            foreach (TagSortFacetBox box in _tagSortFacetBoxes)
            {
                songs = FilterSongsByBox(songs, box);
            }

            List<PlaylistItem> ordered = SortTagSortPanelSongs(songs.ToList());
            _tagSortBrowseSongs.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Index = i + 1;
                _tagSortBrowseSongs.Add(ordered[i]);
            }

            if (TagSortBrowseSongList != null
                && !ReferenceEquals(TagSortBrowseSongList.ItemsSource, _tagSortBrowseSongs))
            {
                TagSortBrowseSongList.ItemsSource = _tagSortBrowseSongs;
            }

            if (TagSortBrowseCountText != null)
            {
                TagSortBrowseCountText.Text = ordered.Count > 0
                    ? $"共 {ordered.Count} 首"
                    : "没有符合条件的歌曲";
            }

            if (TagSortBrowseClearButton != null)
            {
                TagSortBrowseClearButton.Visibility = _tagSortFacetBoxes.Any(b => b.HasSelection)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (TagSortBrowseEmptyHint != null)
            {
                TagSortBrowseEmptyHint.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ---------- 分类框交互 ----------

        /// <summary>点框内一行：选上/取消这个取值。</summary>
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

            if (!box.SelectedValues.Remove(entry.Value))
            {
                box.SelectedValues.Add(entry.Value);
            }

            entry.IsSelected = box.SelectedValues.Contains(entry.Value);
            OnTagSortFacetSelectionChanged(box);
        }

        /// <summary>
        /// 某一层选择变了：如果它不是最后一层，后面几层的前提已经作废，整段收起。
        /// 最后一层改动不影响别人，只刷右栏。
        /// </summary>
        private void OnTagSortFacetSelectionChanged(TagSortFacetBox box)
        {
            int index = _tagSortFacetBoxes.IndexOf(box);
            if (index < 0)
            {
                return;
            }

            if (index < _tagSortFacetBoxes.Count - 1)
            {
                _tagSortFacetFields.RemoveRange(index + 1, _tagSortFacetFields.Count - index - 1);
                SaveTagSortFacetFields();
                RebuildTagSortFacetBoxes(preserveSelection: true);
            }

            box.RaiseSummaryChanged();
            ApplyTagSortBrowseFilter();
        }

        /// <summary>「＋ 选择分类字段」：列出还没用上的分类字段。</summary>
        private void TagSortAddFacetButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            var used = new HashSet<string>(_tagSortFacetFields, StringComparer.OrdinalIgnoreCase);
            var menu = new MenuFlyout();
            foreach (TagSortFields.FieldDef def in TagSortFacetCandidates)
            {
                if (used.Contains(def.Key))
                {
                    continue;
                }

                var item = new MenuFlyoutItem { Text = def.Label, Tag = def.Key };
                item.Click += TagSortAddFacetItem_Click;
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
            {
                menu.Items.Add(new MenuFlyoutItem { Text = "能加的字段都用上了", IsEnabled = false });
            }

            menu.ShowAt(button);
        }

        private void TagSortAddFacetItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string key)
            {
                return;
            }

            if (_tagSortFacetFields.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _tagSortFacetFields.Add(key);
            SaveTagSortFacetFields();
            RebuildTagSortFacetBoxes(preserveSelection: true);
            ApplyTagSortBrowseFilter();
        }

        /// <summary>收起某一层（后面的层保留，候选集重算）。</summary>
        private void TagSortFacetRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string key)
            {
                return;
            }

            _tagSortFacetFields.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            SaveTagSortFacetFields();
            RebuildTagSortFacetBoxes(preserveSelection: true);
            ApplyTagSortBrowseFilter();
        }

        /// <summary>清掉所有层的选择（框还留着）。</summary>
        private void TagSortBrowseClearButton_Click(object sender, RoutedEventArgs e)
        {
            RebuildTagSortFacetBoxes(preserveSelection: false);
            ApplyTagSortBrowseFilter();
        }

        private void SaveTagSortFacetFields()
            => AppSettingsStore.Update(s => s.TagSortFacetFields = _tagSortFacetFields.ToList());

        // ---------- 配色小工具 ----------

        private static Brush MakeAlphaBrush(Brush? source, double alpha)
        {
            if (source is SolidColorBrush solid)
            {
                Windows.UI.Color c = solid.Color;
                return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(255 * alpha), c.R, c.G, c.B));
            }

            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private static Brush ResolveThemeBrush(string key, Windows.UI.Color fallback)
        {
            if (Application.Current?.Resources != null
                && Application.Current.Resources.TryGetValue(key, out object value)
                && value is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(fallback);
        }

        // ---------- 顶栏按钮 ----------

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
