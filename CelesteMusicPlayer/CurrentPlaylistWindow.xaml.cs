using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 当前播放列表弹出窗口：与主界面「播放列表」功能一致，共享同一份列表数据。
    /// </summary>
    public sealed partial class CurrentPlaylistWindow : Window
    {
        private readonly MainWindow _owner;
        private PlaylistItem? _contextMenuSong;
        private bool _suppressSelectionUi;
        private bool _isMultiSelectMode;
        private string _searchText = string.Empty;
        private DispatcherQueueTimer? _searchDebounceTimer;
        private Style? _defaultItemStyle;

        public CurrentPlaylistWindow(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
            WindowIconHelper.Apply(this);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            Title = "当前播放列表";
            AppWindow.Resize(new Windows.Graphics.SizeInt32(920, 640));
            ConfigureTitleBarButtons();

            _defaultItemStyle = PlaylistView.ItemContainerStyle;
            ApplyChromeStyles();
            RefreshFromOwner();
        }

        internal void RefreshFromOwner()
        {
            ApplySearchFilter(preserveMultiSelect: _isMultiSelectMode);
            if (!_isMultiSelectMode)
            {
                SyncSelectionToPlaying();
            }

            RefreshSelectionChrome();
            UpdateSelectAllButtonState();
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

        private void ApplyChromeStyles()
        {
            Brush accent = _owner.GetAccentBrush();
            Brush accentFg = _owner.GetAccentForegroundBrush();
            Brush capsule = _owner.GetCapsuleFillBrush();

            ApplyCapsule(ChangeSortButton, accent, accentFg);
            ApplyCapsule(SavePlaylistButton, capsule, null);
            ApplyCapsule(OpenPlaylistButton, capsule, null);
            ApplyCapsule(ClearPlaylistButton, capsule, null);
            ApplyCapsule(PlayUserPlaylistButton, capsule, null);
            ApplyCapsule(SelectAllMultiSelectButton, accent, accentFg, cornerRadius: 8);

            foreach (Border? chip in new[]
                     {
                         HeaderTitleChip,
                         HeaderArtistChip,
                         HeaderAlbumChip,
                         HeaderYearChip,
                         HeaderDurationChip
                     })
            {
                if (chip == null)
                {
                    continue;
                }

                chip.Background = capsule;
            }

            ApplyAccentSelectionResources(PlaylistView);
        }

        private static void ApplyCapsule(
            Control control,
            Brush background,
            Brush? foreground,
            double cornerRadius = 16)
        {
            control.Height = 32;
            control.MinHeight = 32;
            control.CornerRadius = new CornerRadius(cornerRadius);
            control.Background = background;
            control.BorderThickness = new Thickness(0);
            if (foreground != null)
            {
                control.Foreground = foreground;
            }
        }

        private void ApplyAccentSelectionResources(FrameworkElement host)
        {
            Brush transparent = new SolidColorBrush(Colors.Transparent);
            Brush fg = _owner.GetAccentForegroundBrush();

            string[] backgroundKeys =
            {
                "ListViewItemBackgroundSelected",
                "ListViewItemBackgroundSelectedPointerOver",
                "ListViewItemBackgroundSelectedPressed",
                "ListViewItemBackgroundSelectedDisabled"
            };
            string[] foregroundKeys =
            {
                "ListViewItemForegroundSelected",
                "ListViewItemForegroundSelectedPointerOver",
                "ListViewItemForegroundSelectedPressed"
            };

            foreach (string key in backgroundKeys)
            {
                host.Resources[key] = transparent;
            }

            foreach (string key in foregroundKeys)
            {
                host.Resources[key] = fg;
            }

            host.Resources["ListViewItemSelectionCheckMarkVisualEnabled"] = false;
        }

        private void PlaylistSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = PlaylistSearchBox.Text ?? string.Empty;
            if (_searchDebounceTimer == null)
            {
                _searchDebounceTimer = DispatcherQueue.CreateTimer();
                _searchDebounceTimer.IsRepeating = false;
                _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(180);
                _searchDebounceTimer.Tick += (_, _) =>
                {
                    ApplySearchFilter(preserveMultiSelect: _isMultiSelectMode);
                    RefreshSelectionChrome();
                    UpdateSelectAllButtonState();
                };
            }

            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void ApplySearchFilter(bool preserveMultiSelect)
        {
            IReadOnlyList<PlaylistItem> source = _owner.UserPlaylist;
            string q = _searchText.Trim();
            object? items;
            if (string.IsNullOrEmpty(q))
            {
                items = source;
            }
            else
            {
                items = source.Where(p => MainWindow.MatchesPlaylistSearch(p, q)).ToList();
            }

            var retained = preserveMultiSelect
                ? PlaylistView.SelectedItems.OfType<PlaylistItem>().ToList()
                : null;

            // 搜索过滤状态下禁用拖拽排序(过滤列表是临时投影,拖拽不会落回真实列表)
            try
            {
                PlaylistView.CanReorderItems = string.IsNullOrEmpty(q);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("CurrentPlaylistWindow.xaml.cs", caught); }

            _suppressSelectionUi = true;
            try
            {
                PlaylistView.ItemsSource = items;
                if (retained != null && retained.Count > 0)
                {
                    foreach (PlaylistItem song in retained)
                    {
                        if (PlaylistView.Items.Contains(song))
                        {
                            try
                            {
                                PlaylistView.SelectedItems.Add(song);
                            }
                            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("CurrentPlaylistWindow.xaml.cs", caught); }
                        }
                    }
                }
            }
            finally
            {
                _suppressSelectionUi = false;
            }
        }

        private void SyncSelectionToPlaying()
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            int index = _owner.UserPlaylistPlayingIndex;
            if (index < 0 || index >= _owner.UserPlaylist.Count)
            {
                return;
            }

            PlaylistItem playing = _owner.UserPlaylist[index];
            if (!PlaylistView.Items.Contains(playing))
            {
                return;
            }

            _suppressSelectionUi = true;
            try
            {
                PlaylistView.SelectedItem = playing;
                PlaylistView.ScrollIntoView(playing);
            }
            finally
            {
                _suppressSelectionUi = false;
            }
        }

        private void PlaylistView_DragItemsCompleted(Microsoft.UI.Xaml.Controls.ListViewBase sender, Microsoft.UI.Xaml.Controls.DragItemsCompletedEventArgs args)
        {
            // 拖拽重排已由 ObservableCollection 自动处理(UserPlaylist 与主窗口共享)
            RefreshSelectionChrome();
            _owner?.RefreshFromPlaylistReorder();
        }

        private void PlaylistView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            if (PlaylistView.SelectedItem is PlaylistItem song)
            {
                int index = _owner.FindUserPlaylistIndexPublic(song.FilePath);
                if (index >= 0)
                {
                    _owner.PlayUserPlaylistAtPublic(index);
                    RefreshSelectionChrome();
                }
            }
        }

        private void PlaylistView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source is not ListViewItem)
            {
                source = VisualTreeHelper.GetParent(source);
            }

            if (source is ListViewItem container
                && PlaylistView.ItemFromContainer(container) is PlaylistItem song)
            {
                _contextMenuSong = song;
                if (!_isMultiSelectMode)
                {
                    PlaylistView.SelectedItem = song;
                }
            }
            else if (PlaylistView.SelectedItem is PlaylistItem selected)
            {
                _contextMenuSong = selected;
            }
            else
            {
                return;
            }

            var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                if (_contextMenuSong == null)
                {
                    return;
                }

                ExitMultiSelect();
                int index = _owner.FindUserPlaylistIndexPublic(_contextMenuSong.FilePath);
                if (index >= 0)
                {
                    _owner.PlayUserPlaylistAtPublic(index);
                    RefreshSelectionChrome();
                }
            };

            var pinItem = new MenuFlyoutItem { Text = "置顶" };
            pinItem.Icon = new FontIcon { Glyph = "\uE898" };
            pinItem.Click += (_, _) =>
            {
                if (_contextMenuSong != null)
                {
                    _owner.PinSongToUserPlaylistTop(_contextMenuSong);
                }
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) => EnterMultiSelect(_contextMenuSong);

            var removeItem = new MenuFlyoutItem { Text = "从播放队列中删除" };
            removeItem.Icon = new FontIcon { Glyph = "\uE74D" };
            removeItem.Click += (_, _) =>
            {
                if (_contextMenuSong != null)
                {
                    _owner.RemoveSongsFromUserPlaylistPublic(new[] { _contextMenuSong });
                }
            };

            var openLoc = new MenuFlyoutItem { Text = "打开文件位置" };
            openLoc.Icon = new FontIcon { Glyph = "\uE8DA" };
            openLoc.Click += (_, _) =>
            {
                if (_contextMenuSong != null)
                {
                    MainWindow.OpenFileLocationInExplorerPublic(_contextMenuSong.FilePath);
                }
            };

            flyout.Items.Add(playItem);
            flyout.Items.Add(pinItem);
            if (!_isMultiSelectMode)
            {
                flyout.Items.Add(multiItem);
            }

            flyout.Items.Add(removeItem);
            flyout.Items.Add(openLoc);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(PlaylistView, e.GetPosition(PlaylistView));
            }

            e.Handled = true;
        }

        private void EnterMultiSelect(PlaylistItem? seed)
        {
            _isMultiSelectMode = true;
            PlaylistView.SelectionMode = ListViewSelectionMode.Multiple;
            ApplyMultiSelectItemStyle();

            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectActionBar.Visibility = Visibility.Visible;
            PlaylistActionBar.Visibility = Visibility.Collapsed;
            PlaylistSearchBox.Visibility = Visibility.Collapsed;
            ChangeSortButton.Visibility = Visibility.Collapsed;

            PlaylistView.SelectedItems.Clear();
            if (seed != null)
            {
                try
                {
                    PlaylistView.SelectedItems.Add(seed);
                }
                catch
                {
                    PlaylistView.SelectedItem = seed;
                }
            }

            UpdateSelectAllButtonState();
            DispatcherQueue.TryEnqueue(RefreshSelectionChrome);
        }

        private void ExitMultiSelectButton_Click(object sender, RoutedEventArgs e)
            => ExitMultiSelect();

        private void ExitMultiSelect()
        {
            if (!_isMultiSelectMode)
            {
                return;
            }

            _isMultiSelectMode = false;
            PlaylistView.SelectionMode = ListViewSelectionMode.Single;
            if (_defaultItemStyle != null)
            {
                PlaylistView.ItemContainerStyle = _defaultItemStyle;
            }

            MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
            MultiSelectActionBar.Visibility = Visibility.Collapsed;
            PlaylistActionBar.Visibility = Visibility.Visible;
            PlaylistSearchBox.Visibility = Visibility.Visible;
            ChangeSortButton.Visibility = Visibility.Visible;

            SyncSelectionToPlaying();
            RefreshSelectionChrome();
        }

        private void SelectAllMultiSelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMultiSelectMode)
            {
                return;
            }

            bool allSelected = AreAllVisibleItemsSelected();
            _suppressSelectionUi = true;
            try
            {
                if (allSelected)
                {
                    PlaylistView.SelectedItems.Clear();
                }
                else
                {
                    PlaylistView.SelectedItems.Clear();
                    foreach (object item in PlaylistView.Items)
                    {
                        if (item is PlaylistItem song)
                        {
                            PlaylistView.SelectedItems.Add(song);
                        }
                    }
                }
            }
            finally
            {
                _suppressSelectionUi = false;
            }

            UpdateSelectAllButtonState();
            RefreshSelectionChrome();
        }

        private void MultiSelectDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = PlaylistView.SelectedItems.OfType<PlaylistItem>().ToList();
            if (selected.Count > 0)
            {
                _owner.RemoveSongsFromUserPlaylistPublic(selected);
            }

            ExitMultiSelect();
        }

        private bool AreAllVisibleItemsSelected()
        {
            int total = PlaylistView.Items.Count;
            if (total == 0)
            {
                return false;
            }

            return PlaylistView.SelectedItems.Count >= total;
        }

        private void UpdateSelectAllButtonState()
        {
            if (SelectAllMultiSelectIcon == null || !_isMultiSelectMode)
            {
                return;
            }

            SelectAllMultiSelectIcon.Glyph = AreAllVisibleItemsSelected() ? "\uE73A" : "\uE739";
        }

        private void ApplyMultiSelectItemStyle()
        {
            ApplyAccentSelectionResources(PlaylistView);
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListViewItem.MinHeightProperty, 40.0));
            style.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(ListViewItem.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0, 2, 0, 2)));
            style.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            PlaylistView.ItemContainerStyle = style;
        }

        private async void ChangeSortButton_Click(object sender, RoutedEventArgs e)
            => await _owner.ChangeUserPlaylistSortAsync(Content.XamlRoot);

        private async void SavePlaylistButton_Click(object sender, RoutedEventArgs e)
            => await _owner.SaveUserPlaylistAsync(Content.XamlRoot);

        private async void OpenPlaylistButton_Click(object sender, RoutedEventArgs e)
            => await _owner.OpenUserPlaylistAsync(Content.XamlRoot, navigateToPlaylist: false);

        private async void ClearPlaylistButton_Click(object sender, RoutedEventArgs e)
            => await _owner.ClearUserPlaylistAsync(Content.XamlRoot);

        private void PlayUserPlaylistButton_Click(object sender, RoutedEventArgs e)
            => _owner.PlayUserPlaylistFromStart();

        private void PlaylistView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUi)
            {
                return;
            }

            UpdateSelectAllButtonState();
            RefreshSelectionChrome();
        }

        private void PlaylistView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplyRowChrome(container, song);
            }
        }

        private void RefreshSelectionChrome()
        {
            for (int i = 0; i < PlaylistView.Items.Count; i++)
            {
                if (PlaylistView.ContainerFromIndex(i) is ListViewItem container
                    && PlaylistView.Items[i] is PlaylistItem song)
                {
                    ApplyRowChrome(container, song);
                }
            }
        }

        private void ApplyRowChrome(ListViewItem container, PlaylistItem song)
        {
            Border? chrome = FindTaggedBorder(container, "SongRowChrome");
            if (chrome == null)
            {
                return;
            }

            bool selected = _isMultiSelectMode
                ? PlaylistView.SelectedItems.Contains(song)
                : ReferenceEquals(PlaylistView.SelectedItem, song);

            Brush unselected = _isMultiSelectMode
                ? _owner.GetMultiSelectFrostBrush()
                : new SolidColorBrush(Colors.Transparent);

            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);

            if (selected)
            {
                chrome.Background = _owner.GetAccentBrush();
            }
            else
            {
                chrome.Background = unselected;
            }
        }

        private static Border? FindTaggedBorder(DependencyObject root, string tag)
        {
            if (root is Border b && string.Equals(b.Tag as string, tag, StringComparison.Ordinal))
            {
                return b;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                Border? found = FindTaggedBorder(VisualTreeHelper.GetChild(root, i), tag);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
