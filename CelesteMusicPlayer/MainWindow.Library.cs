using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shapes = Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
// TagLibSharp：包名 TagLibSharp，命名空间 TagLib
using TagLib;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Color = Windows.UI.Color;


namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {


        /// <summary>删除用户预设（从保存预at预设对话框里的删除按钮后调用）。</summary>
        /// <summary>「管理（删除）我的预设」流程：列出用户预设，选一个确认删除。</summary>
        private async Task DeleteAudioFxUserPresetFlow()
        {
            var presets = EqUserPresetStore.Load();
            if (presets.Count == 0)
            {
                NowPlayingText.Text = "还没有已保存的用户预设";
                return;
            }

            var listView = new ListView
            {
                Height = 260,
                SelectionMode = ListViewSelectionMode.Single
            };
            foreach (var p in presets)
            {
                listView.Items.Add(new ListViewItem { Content = string.IsNullOrWhiteSpace(p.PresetName) ? "未命名" : p.PresetName, Tag = p.PresetId });
            }

            var dialog = new ContentDialog
            {
                Title = "删除我的预设",
                Content = listView,
                PrimaryButtonText = "删除所选",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content?.XamlRoot ?? AudioFxBorder.XamlRoot
            };
            ContentDialogResult r;
            try { r = await dialog.ShowAsync(); }
            catch { r = ContentDialogResult.None; }
            if (r != ContentDialogResult.Primary || listView.SelectedItem is not ListViewItem { Tag: string selId })
            {
                return;
            }

            string selName = (listView.SelectedItem as ListViewItem)?.Content?.ToString() ?? "预设";
            await DeleteAudioFxUserPreset(selId, selName);
        }


        /// <summary>删除单个用户预设（带确认）。</summary>
        private async Task DeleteAudioFxUserPreset(string presetId, string name)
        {
            var dialog = new ContentDialog
            {
                Title = "删除预设",
                Content = "删除预设「" + name + "」？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content?.XamlRoot ?? AudioFxBorder.XamlRoot
            };
            ContentDialogResult r;
            try { r = await dialog.ShowAsync(); }
            catch { r = ContentDialogResult.None; }
            if (r != ContentDialogResult.Primary) return;

            EqUserPresetStore.Delete(presetId);
            if (string.Equals(EqState.Current.PresetId, presetId, StringComparison.Ordinal))
            {
                EqState.Current.PresetId = "custom";
                EqState.Current.PresetName = "自定义";
            }

            RefreshAudioFxUserPresetItems();
            NowPlayingText.Text = "已删除预设：" + name;
        }


        // 原生的鼠标侧键前进/后退（LibraryPaneRoot_PointerPressed + 两个导航助手）已于 2026-10-31 删除：
        // 新外壳在网页层接管侧键（shell.js 的 goBack/goForward），原生这层在隐藏窗口上永远收不到事件。

        private void RestoreLibraryNavState(LibraryNavState state)
        {
            _suppressNavHistory = true;
            try
            {
                ExitMultiSelectMode();
                _currentCategory = state.Category;
                ApplyCategoryView();

                if (!string.IsNullOrWhiteSpace(state.ArtistName)
                    && (string.Equals(state.Category, "Artists", StringComparison.Ordinal)
                        || string.Equals(state.Category, "AlbumArtists", StringComparison.Ordinal)))
                {
                    _artistDetailUsesAlbumArtist = state.UsesAlbumArtist
                        || string.Equals(state.Category, "AlbumArtists", StringComparison.Ordinal);
                    ArtistEntry? artist = _artists.FirstOrDefault(a =>
                        string.Equals(a.Name, state.ArtistName, StringComparison.CurrentCultureIgnoreCase));
                    if (artist == null)
                    {
                        artist = new ArtistEntry { Name = state.ArtistName };
                        _artists.Add(artist);
                    }

                    OpenArtistDetailCore(artist);
                }

                if (!string.IsNullOrWhiteSpace(state.AlbumName))
                {
                    AlbumEntry? album = FindAlbumEntryByName(state.AlbumName);
                    if (album != null)
                    {
                        OpenAlbumDetailCore(album, state.AlbumFromArtist);
                    }
                }

                _navCurrent = CaptureLibraryNavState();
                UpdateLibraryNavHighlight();
            }
            finally
            {
                _suppressNavHistory = false;
            }
        }


        private AlbumEntry? FindAlbumEntryByName(string albumName)
        {
            AlbumEntry? fromWall = _albums.FirstOrDefault(a =>
                string.Equals(a.Name, albumName, StringComparison.CurrentCultureIgnoreCase));
            if (fromWall != null)
            {
                return fromWall;
            }

            AlbumEntry? fromArtist = _artistAlbums.FirstOrDefault(a =>
                string.Equals(a.Name, albumName, StringComparison.CurrentCultureIgnoreCase));
            if (fromArtist != null)
            {
                return fromArtist;
            }

            List<PlaylistItem> tracks = _playlist
                .Where(t => string.Equals(t.Album, albumName, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            return tracks.Count == 0 ? null : BuildAlbumEntriesFromTracks(tracks).FirstOrDefault();
        }


        // =====================================================================
        // 库搜索（歌曲 / 专辑 / 文件夹）
        // =====================================================================

        private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _librarySearchText = LibrarySearchBox.Text ?? string.Empty;

            if (_librarySearchDebounceTimer == null)
            {
                _librarySearchDebounceTimer = DispatcherQueue.CreateTimer();
                _librarySearchDebounceTimer.IsRepeating = false;
                _librarySearchDebounceTimer.Interval = TimeSpan.FromMilliseconds(180);
                _librarySearchDebounceTimer.Tick += (_, _) => ApplyLibrarySearchNow();
            }

            _librarySearchDebounceTimer.Stop();
            _librarySearchDebounceTimer.Start();
        }


        /// <summary>刷新：本地媒体库重新枚举根列表；网络音乐库重新连一次服务器。</summary>
        private void RefreshMediaFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.Equals(_currentCategory, WebDavCategory, StringComparison.Ordinal))
            {
                RefreshWebDavBrowserRoots();
                if (FolderBrowserView.SelectedItem is FolderBrowserItem remote)
                {
                    LoadWebDavFolderSongs(remote);
                }

                return;
            }

            if (_currentCategory != "Folders")
            {
                return;
            }

            RefreshFolderBrowserRoots();
            if (FolderBrowserView.SelectedItem is FolderBrowserItem f)
            {
                LoadMediaFolderSongs(f);
            }
        }


        /// <summary>「添加文件夹」：选择文件夹加入媒体库(LibraryWatchFolders)并刷新根列表。</summary>
        private async void AddMediaFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker();
                nint hwnd = NativeHandle;
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add("*");

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
                {
                    return;
                }

                await AddFolderToLibraryAsync(folder.Path);

                if (_currentCategory == "Folders")
                {
                    RefreshFolderBrowserRoots();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void FolderBrowserItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // 多选时：整行留给 ListView 选中；展开只走左侧箭头
            if (_isMultiSelectMode && _multiSelectFolderList != null)
            {
                return;
            }

            if (sender is not FrameworkElement { DataContext: FolderBrowserItem item })
            {
                return;
            }

            FolderBrowserView.SelectedItem = item;

            if (item.IsRemote)
            {
                // 网络音乐库：展开要发一次网络请求，走异步版本
                _ = ToggleWebDavFolderExpandAsync(item);
                e.Handled = true;
                return;
            }

            if (item.IsFolder)
            {
                ToggleFolderExpand(item);
                e.Handled = true;
            }
        }


        /// <summary>左侧朝下箭头：展开/折叠；多选时不改变选中状态。</summary>
        private void FolderBrowserChevron_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe)
            {
                return;
            }

            FolderBrowserItem? item = fe.DataContext as FolderBrowserItem
                ?? FindFolderBrowserItem(fe);
            if (item == null || !item.IsFolder)
            {
                return;
            }

            if (item.IsRemote)
            {
                _ = ToggleWebDavFolderExpandAsync(item);
                if (item.IsFolder)
                {
                    LoadWebDavFolderSongs(item);
                }

                e.Handled = true;
                return;
            }

            ToggleFolderExpand(item);
            // 点箭头：同时加载该文件夹歌曲到右侧详情区
            LoadMediaFolderSongs(item);
            e.Handled = true;
        }


        private void FolderBrowserView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            var fe = e.OriginalSource as FrameworkElement;
            var item = fe?.DataContext as FolderBrowserItem ?? FolderBrowserView.SelectedItem as FolderBrowserItem;
            if (item == null)
            {
                return;
            }

            if (item.IsRemote)
            {
                // 网络音乐库：双击文件夹 → 列歌；双击文件 → 先下载再播
                if (item.IsFolder)
                {
                    LoadWebDavFolderSongs(item);
                }
                else
                {
                    _ = PlayWebDavItemAsync(new PlaylistItem
                    {
                        Title = System.IO.Path.GetFileNameWithoutExtension(item.DisplayName),
                        FilePath = item.FullPath,
                        RemotePath = item.RemotePath
                    });
                }

                return;
            }

            if (item.IsFolder)
            {
                // 双击文件夹：加载其内歌曲到右侧详情区
                LoadMediaFolderSongs(item);
                return;
            }

            PlaylistItem? track = EnsureTrackInLibrary(item.FullPath);
            if (track != null)
            {
                PlayPlaylistItem(track);
            }
        }


        private void FolderBrowserView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshFolderBrowserSelectionChrome();
            UpdateSelectAllMultiSelectButtonState();
            UpdateMediaDetails(FolderBrowserView.SelectedItem as FolderBrowserItem);
        }


        /// <summary>单击选中：只更新台头，不立即枚举歌曲（大文件夹避免卡顿）。歌曲在双击/箭头时加载。</summary>
        private void UpdateMediaDetails(FolderBrowserItem? item)
        {
            if (MediaDetailsHeader == null || MediaDetailsList == null)
            {
                return;
            }

            if (item == null)
            {
                MediaDetailsHeader.Text = "选择文件夹查看歌曲";
                MediaDetailsList.ItemsSource = null;
                MediaDetailsEmptyHint.Visibility = MediaDetailsList.Visibility = Visibility.Collapsed;
                return;
            }

            MediaDetailsHeader.Text = item.FullPath;
            MediaDetailsList.ItemsSource = null;
            MediaDetailsEmptyHint.Visibility = Visibility.Visible;
            MediaDetailsList.Visibility = Visibility.Visible;

            if (item.IsRemote)
            {
                MediaDetailsEmptyHint.Text = item.IsFolder ? "双击文件夹查看歌曲" : "双击下载并播放";
                return;
            }

            MediaDetailsEmptyHint.Text = item.IsFolder ? "双击文件夹查看歌曲" : "双击查看";
        }


        private void FolderBrowserView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            FolderBrowserItem? item = FindFolderBrowserItem(e.OriginalSource as DependencyObject);
            if (item == null)
            {
                return;
            }

            if (!_isMultiSelectMode)
            {
                FolderBrowserView.SelectedItem = item;
            }

            // 网络音乐库的行：本地那套菜单（打开所在文件夹、加入媒体库…）不适用，
            // 与其弹一堆点不动的项，不如不弹。
            if (item.IsRemote)
            {
                e.Handled = true;
                return;
            }

            if (item.IsFolder)
            {
                ShowFolderItemContextMenu(item, e);
            }
            else
            {
                ShowFolderAudioContextMenu(item, e);
            }

            e.Handled = true;
        }


        private FolderBrowserItem? FindFolderBrowserItem(DependencyObject? source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is FrameworkElement { DataContext: FolderBrowserItem fromContext })
                {
                    return fromContext;
                }

                if (current is ListViewItem container)
                {
                    return FolderBrowserView.ItemFromContainer(container) as FolderBrowserItem;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }


        private void ShowFolderItemContextMenu(FolderBrowserItem folder, RightTappedRoutedEventArgs e)
        {
            FolderBrowserItem folderRef = folder;
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放该文件夹内的音频" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                ExitMultiSelectMode();
                PlayFolderAudio(folderRef.FullPath, replacePlaylist: true);
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) => EnterFolderMultiSelectMode(folderRef);

            var addItem = new MenuFlyoutItem { Text = "添加至播放队列" };
            addItem.Icon = new FontIcon { Glyph = "\uE710" };
            addItem.Click += (_, _) => PlayFolderAudio(folderRef.FullPath, replacePlaylist: false);

            flyout.Items.Add(playItem);
            flyout.Items.Add(multiItem);
            flyout.Items.Add(addItem);

            // 从媒体库删除（仅对配置的媒体库根文件夹可用）
            AppSettingsState settings = AppSettingsStore.Load();
            bool isMediaRoot = settings.LibraryWatchFolders?.Contains(folderRef.FullPath, StringComparer.OrdinalIgnoreCase) == true;
            if (isMediaRoot)
            {
                var removeItem = new MenuFlyoutItem { Text = "从媒体库中删除" };
                removeItem.Icon = new FontIcon { Glyph = "\uE74D" };
                removeItem.Click += async (_, _) =>
                {
                    var dialog = new ContentDialog
                    {
                        Title = "从媒体库中删除",
                        Content = $"将该文件夹移出媒体库？\n\n{folderRef.FullPath}\n\n是否同时把该文件夹内的音频移到回收站？",
                        PrimaryButtonText = "删除（移到回收站）",
                        SecondaryButtonText = "仅移出媒体库",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = Content.XamlRoot
                    };
                    ColorHelper.ApplyDialogAccent(dialog);

                    ContentDialogResult r;
                    try
                    {
                        r = await dialog.ShowAsync();
                    }
                    catch
                    {
                        r = ContentDialogResult.None;
                    }

                    if (r == ContentDialogResult.None)
                    {
                        return;
                    }

                    if (r == ContentDialogResult.Primary)
                    {
                        // 删除源文件（移到回收站）
                        foreach (string p in LibraryFolderScan.AudioFiles(folderRef.FullPath))
                        {
                            if (System.IO.File.Exists(p))
                            {
                                try
                                {
                                    MoveToRecycleBin(p);
                                }
                                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                            }
                        }
                    }

                    settings.LibraryWatchFolders?.RemoveAll(q =>
                        string.Equals(q, folderRef.FullPath, StringComparison.OrdinalIgnoreCase));
                    AppSettingsStore.Save(settings);

                    // 光改设置不够：曲库里已经收录的这些歌不会自己消失，
                    // 必须把该文件夹下的曲目一并移出库，否则"删了文件夹、歌还在我的音乐库里"。
                    await RemoveFolderTracksFromLibraryAsync(folderRef.FullPath);

                    RefreshFolderBrowserRoots();
                };
                flyout.Items.Add(removeItem);
            }

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(FolderBrowserView, e.GetPosition(FolderBrowserView));
            }
        }


        /// <summary>
        /// 「从媒体库中删除」文件夹后，把该文件夹（含子目录）下的曲目一并从曲库清掉。
        /// 只改设置是不够的：已经收录进库的歌不会自己消失，用户会看到「文件夹移走了、歌还在我的音乐库里」。
        /// 同时清掉「手动加入音乐库」的记录，避免重启后这些歌又被补回来；专辑 / 艺术家视图同步重建。
        /// </summary>
        private System.Threading.Tasks.Task RemoveFolderTracksFromLibraryAsync(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
                string prefix = root + Path.DirectorySeparatorChar;

                var deadPaths = new System.Collections.Generic.List<string>();
                var deadItems = new System.Collections.Generic.List<PlaylistItem>();

                foreach (PlaylistItem item in _playlist)
                {
                    string? p = item?.FilePath;
                    if (string.IsNullOrWhiteSpace(p))
                    {
                        continue;
                    }

                    string full;
                    try
                    {
                        full = Path.GetFullPath(p);
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        deadPaths.Add(p);
                        deadItems.Add(item);
                    }
                }

                if (deadPaths.Count == 0)
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                // 清曲库索引（内部会一并清 ManualLibraryFiles 记录，避免重启后被补回来）
                RemoveFilesFromCurrentPlaylist(deadPaths);
                RemoveSongsFromUserPlaylist(deadItems);
                RenumberCollection(_playlist);
                RenumberCollection(_queue.Items);
                NotifyCurrentPlaylistWindow();

                _ = RefreshAlbumViewAsync();
                _ = RefreshArtistViewAsync();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Library.RemoveFolderTracks", caught);
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }


        private void ShowFolderAudioContextMenu(FolderBrowserItem fileItem, RightTappedRoutedEventArgs e)
        {
            PlaylistItem? track = EnsureTrackInLibrary(fileItem.FullPath);
            if (track == null)
            {
                return;
            }

            _contextMenuSong = track;
            var flyout = BuildPlaylistItemContextMenu(track, false, () => EnterFolderMultiSelectMode(fileItem));

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(FolderBrowserView, e.GetPosition(FolderBrowserView));
            }
        }


        private void EnterFolderMultiSelectMode(FolderBrowserItem? preselect)
        {
            if (_multiSelectTargetList != null)
            {
                ExitSongMultiSelectUiOnly();
            }

            if (_multiSelectAlbumGrid != null)
            {
                ExitAlbumMultiSelectUiOnly();
            }

            _folderItemDefaultStyle ??= FolderBrowserView.ItemContainerStyle;
            _multiSelectFolderList = FolderBrowserView;
            _multiSelectTargetList = null;
            _multiSelectAlbumGrid = null;
            _isMultiSelectMode = true;

            SetListSelectionMode(FolderBrowserView, ListViewSelectionMode.Multiple);

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            AlbumSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择项目";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
            UpdateUserPlaylistActionBarVisibility();
            ApplyAccentSelectionResources(FolderBrowserView);
            ApplyMultiSelectFolderItemStyle();
            UpdateLibrarySearchUi();

            if (preselect != null)
            {
                try
                {
                    FolderBrowserView.SelectedItems.Add(preselect);
                }
                catch
                {
                    FolderBrowserView.SelectedItem = preselect;
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshFolderBrowserSelectionChrome();
                UpdateSelectAllMultiSelectButtonState();
            });
        }


        private void ApplyMultiSelectFolderItemStyle()
        {
            ApplyAccentSelectionResources(FolderBrowserView);
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListViewItem.MinHeightProperty, 36.0));
            style.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(ListViewItem.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0, 2, 0, 2)));
            style.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            FolderBrowserView.ItemContainerStyle = style;
            RefreshFolderBrowserSelectionChrome();
        }



        /// <summary>圆形头像上右键：选择本地头像 / 恢复默认</summary>
        private void ArtistAvatar_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ArtistEntry? artist = VisualTreeWalker.FindArtistEntry(sender as DependencyObject);
            if (artist == null)
            {
                return;
            }

            ShowArtistAvatarFlyout(artist, sender as FrameworkElement, e);
        }


        private void ArtistDetailAvatar_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_openedArtist == null)
            {
                return;
            }

            ShowArtistAvatarFlyout(_openedArtist, sender as FrameworkElement, e);
        }


        private void ShowArtistAvatarFlyout(ArtistEntry artist, FrameworkElement? element, RightTappedRoutedEventArgs e)
        {
            _avatarContextArtist = artist;

            var flyout = new MenuFlyout();

            var playWorks = new MenuFlyoutItem { Text = "播放该艺术家的作品" };
            playWorks.Icon = new FontIcon { Glyph = "\uE768" };
            playWorks.Click += (_, _) =>
            {
                if (_avatarContextArtist != null)
                {
                    PlayArtistWorks(_avatarContextArtist.Name, replacePlaylist: true);
                }
            };
            flyout.Items.Add(playWorks);

            var addWorks = new MenuFlyoutItem { Text = "添加该艺术家的作品至播放列表" };
            addWorks.Icon = new FontIcon { Glyph = "\uE710" };
            addWorks.Click += (_, _) =>
            {
                if (_avatarContextArtist != null)
                {
                    _ = ShowNamedPlaylistPickerAsync(GetTracksForArtist(_avatarContextArtist.Name, useCurrentSongSort: true));
                }
            };
            flyout.Items.Add(addWorks);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var webAvatarItem = new MenuFlyoutItem { Text = "在线搜索网络头像…" };
            webAvatarItem.Icon = new FontIcon { Glyph = "\uE774" };
            webAvatarItem.Click += (_, _) => OpenArtistAvatarSearch(_avatarContextArtist);
            flyout.Items.Add(webAvatarItem);

            var selectItem = new MenuFlyoutItem { Text = "从本地选择艺术家头像" };
            selectItem.Click += SelectArtistAvatarMenu_Click;
            flyout.Items.Add(selectItem);

            var restoreItem = new MenuFlyoutItem { Text = "恢复默认头像" };
            restoreItem.Click += RestoreArtistAvatarMenu_Click;
            flyout.Items.Add(restoreItem);

            if (element != null)
            {
                flyout.ShowAt(element, e.GetPosition(element));
            }

            e.Handled = true;
        }


        private void OpenArtistAvatarSearch(ArtistEntry? artist)
        {
            if (artist == null)
            {
                return;
            }

            ArtistEntry captured = artist;

            // 2026-10-12 改版（用户实测反馈）：一律走艺术家详情页右栏内嵌面板，不再弹独立窗口。
            // 从艺术家墙右键进来时详情页没开——先把该艺术家的详情页打开，再落头像页签，
            // 用户要的就是「直接进入详情页然后显示头像面板」。
            // 旧的 ArtistAvatarSearchWindow 窗口类保留作代码兜底，但入口全部摘掉。
            if (ArtistDetailPanel == null || ArtistDetailPanel.Visibility != Visibility.Visible)
            {
                OpenArtistDetail(captured);
            }

            OpenArtistAvatarPanel(captured);
        }
    }
}
