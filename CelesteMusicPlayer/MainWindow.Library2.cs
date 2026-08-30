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

        private async void SelectArtistAvatarMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_avatarContextArtist == null)
            {
                return;
            }

            ArtistEntry artist = _avatarContextArtist;

            try
            {
                FileOpenPicker picker = new();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null || string.IsNullOrWhiteSpace(file.Path))
                {
                    return;
                }

                var editor = new ArtistAvatarEditorWindow(
                    ArtistAvatarStoreKey(artist.Name, _artistDetailUsesAlbumArtist
                        || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal)),
                    file.Path);
                _artistAvatarEditorWindow = editor;
                editor.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_artistAvatarEditorWindow, editor))
                    {
                        _artistAvatarEditorWindow = null;
                    }
                };
                editor.AvatarConfirmed += image =>
                {
                    artist.AvatarImage = image;
                    ApplyArtistAvatarToDetailIfOpen(artist, image);
                };
                editor.Activate();
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("选择头像失败", ex.Message);
            }
        }


        private async void RestoreArtistAvatarMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_avatarContextArtist == null)
            {
                return;
            }

            ArtistEntry artist = _avatarContextArtist;
            try
            {
                bool albumArtistMode = _artistDetailUsesAlbumArtist
                    || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);
                ArtistAvatarStore.DeleteCustomAvatar(ArtistAvatarStoreKey(artist.Name, albumArtistMode));
                BitmapImage? image = await ResolveArtistDefaultAvatarAsync(artist.Name, albumArtistMode);
                artist.AvatarImage = image;
                ApplyArtistAvatarToDetailIfOpen(artist, image);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("恢复默认头像失败", ex.Message);
            }
        }


        private void ApplyArtistAvatarToDetailIfOpen(ArtistEntry artist, BitmapImage? image)
        {
            if (_openedArtist == null
                || !string.Equals(_openedArtist.Name, artist.Name, StringComparison.CurrentCultureIgnoreCase))
            {
                return;
            }

            ArtistDetailAvatarBrush.ImageSource = image;
            ArtistDetailAvatarPlaceholder.Visibility =
                image == null ? Visibility.Visible : Visibility.Collapsed;
        }


        private void ArtistGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ArtistEntry artist)
            {
                if (_currentCategory is "Genres" or "Years")
                {
                    OpenGenreYearSongs(artist.Name);
                }
                else
                {
                    OpenArtistDetail(artist);
                }
            }
        }


        private void OpenArtistDetail(ArtistEntry artist)
            => CommitLibraryNavigation(() => OpenArtistDetailCore(artist));

        private void OpenArtistDetailCore(ArtistEntry artist)
        {
            _openedArtist = artist;
            _artistDetailUsesAlbumArtist = string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);
            _artistSongSortMode = ArtistSongSortMode.Title;
            _artistAlbumSortMode = ArtistAlbumSortMode.Title;
            _artistAlbumSortAscending = true;
            ArtistSongSortButton.Content = "排序：标题";
            ArtistAlbumSortFieldText.Text = "排序：专辑（标题）";
            ArtistAlbumSortOrderText.Text = "升序";

            // 进入详情页即清除墙内选中项，返回后不再残留主题色选中框
            ArtistGridView.SelectedItem = null;
            ArtistGridView.Visibility = Visibility.Collapsed;
            ArtistDetailPanel.Visibility = Visibility.Visible;
            LibraryPaneTitle.Text = artist.Name;

            ArtistDetailNameText.Text = artist.Name;
            ArtistDetailAvatarBrush.ImageSource = artist.AvatarImage;
            ArtistDetailAvatarPlaceholder.Visibility =
                artist.AvatarImage == null ? Visibility.Visible : Visibility.Collapsed;

            // 若头像未加载(如从超链接/播放面板进入用的是缓存或新建条目)，异步补齐后再应用，确保头像显示
            if (artist.AvatarImage == null)
            {
                _ = LoadArtistDetailAvatarAsync(artist);
            }

            RebuildArtistTracks();
            _ = RebuildArtistAlbumsAsync();
            ApplyArtistSongsFrostChrome();
            UpdateLibrarySearchUi();
        }


        /// <summary>异步加载艺术家头像并应用到详情页（超链接/播放面板进入时头像未填充的情况）。</summary>
        private async System.Threading.Tasks.Task LoadArtistDetailAvatarAsync(ArtistEntry artist)
        {
            try
            {
                BitmapImage? image = await ResolveArtistAvatarAsync(artist.Name, _artistDetailUsesAlbumArtist);
                if (image == null || !ReferenceEquals(_openedArtist, artist))
                {
                    return; // 已切到其它艺术家或加载失败
                }

                artist.AvatarImage = image;
                ArtistDetailAvatarBrush.ImageSource = image;
                ArtistDetailAvatarPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void ArtistAlbumGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            AlbumEntry? album = null;
            if (e.OriginalSource is DependencyObject source)
            {
                DependencyObject? current = source;
                while (current != null)
                {
                    if (current is FrameworkElement { DataContext: AlbumEntry fromContext })
                    {
                        album = fromContext;
                        break;
                    }

                    if (current is GridViewItem container)
                    {
                        album = ArtistAlbumGridView.ItemFromContainer(container) as AlbumEntry;
                        break;
                    }

                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (album == null)
            {
                return;
            }

            AlbumEntry albumRef = album;
            if (!_isMultiSelectMode)
            {
                ArtistAlbumGridView.SelectedItem = albumRef;
            }

            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放该专辑" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                ExitMultiSelectMode();
                PlayAlbum(albumRef, replacePlaylist: true);
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) => EnterAlbumWallMultiSelectMode(ArtistAlbumGridView, albumRef);

            var addItem = new MenuFlyoutItem { Text = "添加至播放队列" };
            addItem.Icon = new FontIcon { Glyph = "\uE710" };
            addItem.Click += (_, _) => AddSongsToUserPlaylist(GetTracksForAlbum(albumRef));

            flyout.Items.Add(playItem);
            flyout.Items.Add(multiItem);
            flyout.Items.Add(addItem);
            var wallAlbumItem = new MenuFlyoutItem { Text = "添加到播放列表" };
            wallAlbumItem.Icon = new FontIcon { Glyph = "" };
            wallAlbumItem.Click += (_, _) => _ = ShowNamedPlaylistPickerAsync(GetTracksForAlbum(albumRef));
            flyout.Items.Add(wallAlbumItem);

            AppendAlbumContextItems(flyout, albumRef, fromArtist: true);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(ArtistAlbumGridView, e.GetPosition(ArtistAlbumGridView));
            }

            e.Handled = true;
        }


        private void CloseArtistDetailUi()
        {
            ExitMultiSelectMode();
            _openedArtist = null;
            _artistDetailUsesAlbumArtist = false;
            _artistTracks.Clear();
            _artistAlbums.Clear();
            ArtistDetailPanel.Visibility = Visibility.Collapsed;
            ArtistGridView.Visibility = Visibility.Visible;
            ArtistDetailAvatarBrush.ImageSource = null;
            ArtistDetailAvatarPlaceholder.Visibility = Visibility.Visible;
            _albumOpenedFromArtist = false;
        }


        private void ArtistDetailBackButton_Click(object sender, RoutedEventArgs e)
        {
            CommitLibraryNavigation(() =>
            {
                CloseArtistDetailUi();
                if (_currentCategory == "Artists")
                {
                    LibraryPaneTitle.Text = "艺术家";
                }
                else if (_currentCategory == "AlbumArtists")
                {
                    LibraryPaneTitle.Text = "专辑艺术家";
                }

                UpdateLibrarySearchUi();
            });
        }


        private async Task RebuildArtistAlbumsAsync()
        {
            _artistAlbums.Clear();
            if (_openedArtist == null)
            {
                return;
            }

            string artistName = _openedArtist.Name;
            List<PlaylistItem> tracks = _playlist
                .Where(t => TrackMatchesArtistName(t, artistName, _artistDetailUsesAlbumArtist))
                .ToList();

            List<AlbumEntry> entries = BuildAlbumEntriesFromTracks(tracks);
            entries = ApplyArtistAlbumSort(entries);

            foreach (AlbumEntry entry in entries)
            {
                _artistAlbums.Add(entry);
            }

            foreach (AlbumEntry entry in entries)
            {
                if (_openedArtist == null ||
                    !string.Equals(_openedArtist.Name, artistName, StringComparison.CurrentCultureIgnoreCase))
                {
                    return;
                }

                byte[]? bytes = await Task.Run(() => ExtractCoverBytes(entry.CoverSourcePath));
                if (bytes == null || bytes.Length == 0)
                {
                    continue;
                }

                BitmapImage? image = await CreateBitmapFromBytesAsync(bytes);
                if (image != null)
                {
                    entry.CoverImage = image;
                    if (_openedAlbum != null &&
                        string.Equals(_openedAlbum.Name, entry.Name, StringComparison.CurrentCultureIgnoreCase))
                    {
                        AlbumDetailCoverImage.Source = image;
                    }
                }
            }
        }


        private List<AlbumEntry> ApplyArtistAlbumSort(List<AlbumEntry> source)
        {
            bool asc = _artistAlbumSortAscending;
            return _artistAlbumSortMode switch
            {
                ArtistAlbumSortMode.Year => asc
                    ? source
                        .OrderBy(a => a.Year == 0 ? uint.MaxValue : a.Year)
                        .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList()
                    : source
                        .OrderByDescending(a => a.Year)
                        .ThenByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList(),
                _ => asc
                    ? source
                        .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList()
                    : source
                        .OrderByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList()
            };
        }


        private void ArtistAlbumSortFieldButton_Click(object sender, RoutedEventArgs e)
        {
            _artistAlbumSortMode = _artistAlbumSortMode == ArtistAlbumSortMode.Title
                ? ArtistAlbumSortMode.Year
                : ArtistAlbumSortMode.Title;
            ArtistAlbumSortFieldText.Text = _artistAlbumSortMode == ArtistAlbumSortMode.Year
                ? "排序：专辑（时间）"
                : "排序：专辑（标题）";
            RefreshArtistAlbumListOrder();
        }


        private void ArtistAlbumSortOrderButton_Click(object sender, RoutedEventArgs e)
        {
            _artistAlbumSortAscending = !_artistAlbumSortAscending;
            ArtistAlbumSortOrderText.Text = _artistAlbumSortAscending ? "升序" : "降序";
            RefreshArtistAlbumListOrder();
        }


        private void RefreshArtistAlbumListOrder()
        {
            if (_artistAlbums.Count <= 1)
            {
                return;
            }

            List<AlbumEntry> sorted = ApplyArtistAlbumSort(_artistAlbums.ToList());
            _artistAlbums.Clear();
            foreach (AlbumEntry entry in sorted)
            {
                _artistAlbums.Add(entry);
            }
        }


        private void ArtistAlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_isMultiSelectMode && ReferenceEquals(_multiSelectAlbumGrid, ArtistAlbumGridView))
            {
                return;
            }

            if (e.ClickedItem is AlbumEntry album)
            {
                OpenAlbumDetail(album, fromArtist: true);
            }
        }


        private void ArtistAlbumGridView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshAlbumWallSelectionChrome(ArtistAlbumGridView, _artistAlbums);
            UpdateSelectAllMultiSelectButtonState();
        }


        private void ArtistAlbumGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is AlbumEntry album && args.ItemContainer is GridViewItem container)
            {
                ApplyAlbumGridItemSelectionChrome(ArtistAlbumGridView, container, album);
            }
        }


        private int FindLibraryIndex(string filePath)
        {
            for (int i = 0; i < _playlist.Count; i++)
            {
                if (string.Equals(_playlist[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }


        /// <summary>
        /// 从曲库汇总唯一专辑；封面优先取该专辑音轨号为 1 的文件。
        /// </summary>
        private async Task RefreshAlbumViewAsync()
        {
            _albums.Clear();

            if (_playlist.Count == 0)
            {
                return;
            }

            List<AlbumEntry> entries = BuildAlbumEntriesFromTracks(_playlist);
            entries = ApplyAlbumSortToList(entries);

            foreach (AlbumEntry entry in entries)
            {
                _albums.Add(entry);
            }

            ApplyAlbumsSearchFilter();

            foreach (AlbumEntry entry in entries)
            {
                if (_currentCategory != "Albums")
                {
                    return;
                }

                byte[]? bytes = await Task.Run(() => ExtractCoverBytes(entry.CoverSourcePath));
                if (bytes == null || bytes.Length == 0)
                {
                    continue;
                }

                BitmapImage? image = await CreateBitmapFromBytesAsync(bytes);
                if (image != null)
                {
                    entry.CoverImage = image;
                    if (_openedAlbum != null &&
                        string.Equals(_openedAlbum.Name, entry.Name, StringComparison.CurrentCultureIgnoreCase))
                    {
                        AlbumDetailCoverImage.Source = image;
                    }
                }
            }
        }


        private void AlbumSortMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
            {
                return;
            }

            _albumSortMode = tag switch
            {
                "Artist" => AlbumSortMode.Artist,
                "Year" => AlbumSortMode.Year,
                "Added" => AlbumSortMode.Added,
                "Random" => AlbumSortMode.Random,
                "TrackCount" => AlbumSortMode.TrackCount,
                "TotalDuration" => AlbumSortMode.TotalDuration,
                _ => AlbumSortMode.Title
            };

            UpdateAlbumSortButtonsUi();
            ResortAlbumsInPlace();
        }


        /// <summary>升序/降序切换。</summary>
        private void AlbumSortOrderButton_Click(object sender, RoutedEventArgs e)
        {
            // Random 排序无方向概念，切换无意义；仍允许翻转但不影响结果
            _albumSortAscending = !_albumSortAscending;
            UpdateAlbumSortButtonsUi();
            ResortAlbumsInPlace();
        }


        private void UpdateAlbumSortButtonsUi()
        {
            AlbumSortButton.Content = GetAlbumSortFieldName(_albumSortMode);
            if (AlbumSortOrderButton != null)
            {
                AlbumSortOrderButton.Content = _albumSortAscending ? "升序" : "降序";
            }
        }


        private static string GetAlbumSortFieldName(AlbumSortMode mode) => mode switch
        {
            AlbumSortMode.Artist => "按艺术家",
            AlbumSortMode.Year => "按发行年份",
            AlbumSortMode.Added => "按添加时间",
            AlbumSortMode.Random => "随机",
            AlbumSortMode.TrackCount => "按专辑曲目数",
            AlbumSortMode.TotalDuration => "按专辑总时长",
            _ => "按专辑名称"
        };


        private void ResortAlbumsInPlace()
        {
            if (_albums.Count <= 1)
            {
                ApplyAlbumsSearchFilter();
                return;
            }

            List<AlbumEntry> sorted = ApplyAlbumSortToList(_albums.ToList());
            _albums.Clear();
            foreach (AlbumEntry entry in sorted)
            {
                _albums.Add(entry);
            }

            ApplyAlbumsSearchFilter();
        }


        private List<AlbumEntry> ApplyAlbumSortToList(List<AlbumEntry> source)
        {
            bool asc = _albumSortAscending;
            IOrderedEnumerable<AlbumEntry> ordered = _albumSortMode switch
            {
                AlbumSortMode.Artist =>
                    asc ? source.OrderBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase)
                          : source.OrderByDescending(a => a.Artist, StringComparer.CurrentCultureIgnoreCase),
                AlbumSortMode.Year =>
                    asc ? source.OrderBy(a => a.Year == 0 ? uint.MaxValue : a.Year)
                          : source.OrderByDescending(a => a.Year),
                AlbumSortMode.Added =>
                    asc ? source.OrderBy(a => a.SortIndex)
                          : source.OrderByDescending(a => a.SortIndex),
                AlbumSortMode.Random =>
                    source.OrderBy(_ => System.Guid.NewGuid()),
                AlbumSortMode.TrackCount =>
                    asc ? source.OrderBy(a => a.TrackCount).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                          : source.OrderByDescending(a => a.TrackCount).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
                AlbumSortMode.TotalDuration =>
                    asc ? source.OrderBy(a => a.TotalDuration.Ticks).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                          : source.OrderByDescending(a => a.TotalDuration.Ticks).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
                _ =>
                    asc ? source.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                          : source.OrderByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            };

            return ordered.ToList();
        }


        private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_isMultiSelectMode && ReferenceEquals(_multiSelectAlbumGrid, AlbumGridView))
            {
                return;
            }

            if (e.ClickedItem is AlbumEntry album)
            {
                OpenAlbumDetail(album, fromArtist: false);
            }
        }


        private void AlbumGridView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshAlbumWallSelectionChrome(AlbumGridView, _albums);
            UpdateSelectAllMultiSelectButtonState();
        }


        private void AlbumGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is AlbumEntry album && args.ItemContainer is GridViewItem container)
            {
                ApplyAlbumGridItemSelectionChrome(AlbumGridView, container, album);
            }
        }



        /// <summary>专辑墙右键附加操作（音乐库专辑墙 / 艺术家详情专辑共用）。</summary>
        private void AppendAlbumContextItems(MenuFlyout flyout, AlbumEntry album, bool fromArtist)
        {
            var dlLyric = new MenuFlyoutItem { Text = "批量下载歌词" };
            dlLyric.Icon = new FontIcon { Glyph = "" };
            dlLyric.Click += async (_, _) =>
            {
                var tracks = GetTracksForAlbum(album);
                int ok = 0;
                for (int i = 0; i < tracks.Count; i++)
                {
                    PlaylistItem song = tracks[i];
                    NowPlayingText.Text = $"正在批量下载歌词 ({i + 1}/{tracks.Count})…";
                    string? path = await OnlineMusicApi.SearchAndDownloadLyricAsync(song.Title, song.Artist, song.FilePath);
                    if (path != null)
                    {
                        ok++;
                    }
                }

                NowPlayingText.Text = $"歌词下载完成：{ok}/{tracks.Count}";
            };
            flyout.Items.Add(dlLyric);

            var dlCover = new MenuFlyoutItem { Text = "批量下载封面" };
            dlCover.Icon = new FontIcon { Glyph = "" };
            dlCover.Click += async (_, _) =>
            {
                var tracks = GetTracksForAlbum(album);
                int ok = 0;
                for (int i = 0; i < tracks.Count; i++)
                {
                    PlaylistItem song = tracks[i];
                    NowPlayingText.Text = $"正在批量下载封面 ({i + 1}/{tracks.Count})…";
                    if (await OnlineMusicApi.DownloadAndEmbedCoverAsync(song.Title, song.Artist, song.FilePath))
                    {
                        InvalidateCoverCache(song.FilePath);
                        ok++;
                    }
                }

                NowPlayingText.Text = $"封面下载完成：{ok}/{tracks.Count}";
            };
            flyout.Items.Add(dlCover);

            var copyInfo = new MenuFlyoutItem { Text = "复制专辑信息" };
            copyInfo.Icon = new FontIcon { Glyph = "" };
            copyInfo.Click += (_, _) =>
            {
                var data = new DataPackage();
                data.SetText(string.IsNullOrWhiteSpace(album.Artist) ? album.Name : album.Name + " - " + album.Artist);
                Clipboard.SetContent(data);
                NowPlayingText.Text = "已复制专辑信息";
            };
            flyout.Items.Add(copyInfo);

            var openDetail = new MenuFlyoutItem { Text = "打开专辑详情" };
            openDetail.Icon = new FontIcon { Glyph = "" };
            openDetail.Click += (_, _) => OpenAlbumDetail(album, fromArtist);
            flyout.Items.Add(openDetail);
        }

        private void AlbumGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            AlbumEntry? album = null;
            if (e.OriginalSource is DependencyObject source)
            {
                DependencyObject? current = source;
                while (current != null)
                {
                    if (current is FrameworkElement { DataContext: AlbumEntry fromContext })
                    {
                        album = fromContext;
                        break;
                    }

                    if (current is GridViewItem container)
                    {
                        album = AlbumGridView.ItemFromContainer(container) as AlbumEntry;
                        break;
                    }

                    current = VisualTreeHelper.GetParent(current);
                }
            }

            if (album == null)
            {
                return;
            }

            AlbumEntry albumRef = album;
            if (!_isMultiSelectMode)
            {
                AlbumGridView.SelectedItem = albumRef;
            }

            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放该专辑" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                ExitMultiSelectMode();
                PlayAlbum(albumRef, replacePlaylist: true);
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) => EnterAlbumWallMultiSelectMode(AlbumGridView, albumRef);

            var addItem = new MenuFlyoutItem { Text = "添加至播放队列" };
            addItem.Icon = new FontIcon { Glyph = "\uE710" };
            addItem.Click += (_, _) => AddSongsToUserPlaylist(GetTracksForAlbum(albumRef));

            flyout.Items.Add(playItem);
            flyout.Items.Add(multiItem);
            flyout.Items.Add(addItem);
            var wallAlbumItem = new MenuFlyoutItem { Text = "添加到播放列表" };
            wallAlbumItem.Icon = new FontIcon { Glyph = "" };
            wallAlbumItem.Click += (_, _) => _ = ShowNamedPlaylistPickerAsync(GetTracksForAlbum(albumRef));
            flyout.Items.Add(wallAlbumItem);

            AppendAlbumContextItems(flyout, albumRef, fromArtist: false);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(AlbumGridView, e.GetPosition(AlbumGridView));
            }

            e.Handled = true;
        }


        private void OpenAlbumDetail(AlbumEntry album, bool fromArtist)
            => CommitLibraryNavigation(() => OpenAlbumDetailCore(album, fromArtist));

        private void OpenAlbumDetailCore(AlbumEntry album, bool fromArtist)
        {
            _openedAlbum = album;
            _albumOpenedFromArtist = fromArtist;

            if (fromArtist)
            {
                ArtistListBorder.Visibility = Visibility.Collapsed;
                AlbumListBorder.Visibility = Visibility.Visible;
                AlbumDetailBackButton.Content = "← 返回艺术家";
            }
            else
            {
                AlbumDetailBackButton.Content = "← 返回专辑";
            }

            AlbumGridView.Visibility = Visibility.Collapsed;
            AlbumDetailPanel.Visibility = Visibility.Visible;
            AlbumSortPanel.Visibility = Visibility.Collapsed;
            LibraryPaneTitle.Text = album.Name;
            UpdateLibrarySearchUi();

            AlbumDetailCoverImage.Source = album.CoverImage;
            AlbumDetailNameText.Text = album.Name;
            AlbumDetailArtistText.Text = album.Artist;
            if (AlbumDetailSubInfoText != null)
            {
                // 行2：艺术家(超链接) | 发行时间 | 曲目数
                AlbumDetailSubInfoText.Text =
                    " | " + album.YearText + " | " + album.TrackCount + " 首";
            }

            _albumTracks.Clear();
            List<PlaylistItem> tracks = _playlist
                .Where(t => string.Equals(t.Album, album.Name, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (PlaylistItem track in tracks)
            {
                _albumTracks.Add(track);
            }

            // DSD 专辑提示：专辑内全部曲目为 DSF/DFF 时显示
            if (AlbumDetailDsdHint != null)
            {
                bool allDsd = tracks.Count > 0 && tracks.All(t => IsDsdFile(t.FilePath));
                AlbumDetailDsdHint.Visibility = allDsd ? Visibility.Visible : Visibility.Collapsed;
            }

            // 行3：编码器 | 位深/采样率 | 总时长（在后台线程算质量行，避免 CD 大专辑打开卡顿）
            if (AlbumDetailTechText != null)
            {
                AlbumDetailTechText.Text = string.Empty;
                _ = FillAlbumTechTextAsync(album, tracks);
            }

            // Apple Music 风格：按碟片分组显示（每组以 CD{n} 标题行开头）
            AlbumTrackListView.ItemsSource = BuildAlbumGroupedView(_albumTracks);
        }


        /// <summary>把专辑内歌曲按播放顺序（碟号→音轨）添加到某个播放列表。</summary>
        private void AlbumDetailAddToNamedListButton_Click(object sender, RoutedEventArgs e)
        {
            if (_albumTracks.Count == 0)
            {
                return;
            }

            _ = ShowNamedPlaylistPickerAsync(_albumTracks.ToList());
        }


        // ---- 专辑艺术家超链接：悬停显示下划线，点击进入对应专辑艺术家详情页 ----
        private void AlbumDetailArtistLink_PointerEntered(object sender, PointerRoutedEventArgs e)        {
            if (AlbumDetailArtistText != null)
            {
                AlbumDetailArtistText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
            }
        }


        private void AlbumDetailArtistLink_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (AlbumDetailArtistText != null)
            {
                AlbumDetailArtistText.TextDecorations = Windows.UI.Text.TextDecorations.None;
            }
        }


        private void AlbumDetailArtistLink_Click(object sender, RoutedEventArgs e)
        {
            string? artistName = string.IsNullOrWhiteSpace(_openedAlbum?.Artist) ? null : _openedAlbum.Artist;
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return;
            }

            // 进入“专辑艺术家”详情页：先切分类，再打开对应艺术家的详情
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "AlbumArtists";
                ApplyCategoryView(); // 真正显示专辑艺术家分类的视图根，否则右侧面板不切换(仅左侧高亮)
                ArtistEntry? artist = _artists.FirstOrDefault(a =>
                    string.Equals(a.Name, artistName, StringComparison.CurrentCultureIgnoreCase));
                if (artist == null)
                {
                    artist = new ArtistEntry { Name = artistName };
                    _artists.Add(artist);
                }

                OpenArtistDetailCore(artist);
                UpdateLibraryNavHighlight();
            });
        }


        private void AlbumDetailBackButton_Click(object sender, RoutedEventArgs e)
        {
            CommitLibraryNavigation(() =>
            {
                if (_albumOpenedFromArtist && _openedArtist != null)
                {
                    CloseAlbumDetailUi();
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Visible;
                    ArtistGridView.Visibility = Visibility.Collapsed;
                    ArtistDetailPanel.Visibility = Visibility.Visible;
                    LibraryPaneTitle.Text = _openedArtist.Name;
                    UpdateLibrarySearchUi();
                    return;
                }

                CloseAlbumDetailUi();
                if (_currentCategory == "Albums")
                {
                    LibraryPaneTitle.Text = "专辑";
                    AlbumSortPanel.Visibility = Visibility.Visible;
                }

                UpdateLibrarySearchUi();
            });
        }


        private void CloseAlbumDetailUi()
        {
            _openedAlbum = null;
            _albumOpenedFromArtist = false;
            _albumTracks.Clear();
            AlbumDetailPanel.Visibility = Visibility.Collapsed;
            AlbumGridView.Visibility = Visibility.Visible;
            AlbumDetailCoverImage.Source = null;
            AlbumDetailBackButton.Content = "← 返回专辑";
        }


        /// <summary>
        /// 提取封面字节（可在后台线程调用）。按 UseInnerCoverFirst 设置决定
        /// 内嵌标签封面与外置封面（folder.jpg / cover.jpg / 同名图）的优先级。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]?> CoverBytesCache = new();
        private const int CoverBytesCacheMax = 200;

        internal static byte[]? ExtractCoverBytes(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return null;
            }

            if (CoverBytesCache.TryGetValue(audioPath, out byte[]? cached))
            {
                return cached;
            }

            byte[]? inner = TryLoadInnerCover(audioPath);
            byte[]? outer = TryLoadOuterCover(audioPath);
            byte[]? result = AppSettingsStore.Load().UseInnerCoverFirst ? inner ?? outer : outer ?? inner;

            if (CoverBytesCache.Count >= CoverBytesCacheMax)
            {
                CoverBytesCache.Clear();
            }

            CoverBytesCache[audioPath] = result;
            return result;
        }


        /// <summary>专辑墙多选（音乐库专辑 / 艺术家详情专辑）。</summary>
        private void EnterAlbumWallMultiSelectMode(GridView grid, AlbumEntry? preselect)
        {
            if (_multiSelectTargetList != null)
            {
                ExitSongMultiSelectUiOnly();
            }

            if (_multiSelectFolderList != null)
            {
                ExitFolderMultiSelectUiOnly();
            }

            if (ReferenceEquals(grid, ArtistAlbumGridView))
            {
                _artistAlbumItemDefaultStyle ??= ArtistAlbumGridView.ItemContainerStyle;
            }
            else
            {
                _libraryAlbumItemDefaultStyle ??= AlbumGridView.ItemContainerStyle;
            }

            _multiSelectAlbumGrid = grid;
            _multiSelectTargetList = null;
            _multiSelectFolderList = null;
            _isMultiSelectMode = true;

            SetGridSelectionMode(grid, ListViewSelectionMode.Multiple);
            grid.IsItemClickEnabled = false;

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            AlbumSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择专辑";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
            UpdateUserPlaylistActionBarVisibility();
            ApplyAccentSelectionResources(grid);
            ApplyMultiSelectAlbumItemStyle(grid);
            UpdateLibrarySearchUi();

            if (preselect != null)
            {
                try
                {
                    grid.SelectedItems.Add(preselect);
                }
                catch
                {
                    grid.SelectedItem = preselect;
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshAlbumWallSelectionChrome(grid, GetAlbumCollectionForGrid(grid));
                UpdateSelectAllMultiSelectButtonState();
            });
        }


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
                ArtistAlbumGridView.ItemContainerStyle = _artistAlbumItemDefaultStyle;
            }
            else if (ReferenceEquals(grid, AlbumGridView) && _libraryAlbumItemDefaultStyle != null)
            {
                AlbumGridView.ItemContainerStyle = _libraryAlbumItemDefaultStyle;
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
                FolderBrowserView.ItemContainerStyle = _folderItemDefaultStyle;
            }

            ApplyAccentSelectionResources(FolderBrowserView);
            _multiSelectFolderList = null;
        }


        private void MultiSelectEditTagsButton_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedMultiSelectSongs();
            if (items.Count == 0)
            {
                return;
            }

            TagEditorWindow.ShowBatch(items.Select(i => i.FilePath).ToList());
        }


        private async void MultiSelectDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {

            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            if (AppSettingsStore.Load().DisableDeleteFromDisk)
            {
                NowPlayingText.Text = "已在设置中禁用从磁盘删除";
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "删除歌曲",
                Content = $"确定要将选中的 {selected.Count} 首歌曲移动到回收站吗？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            ColorHelper.ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            foreach (PlaylistItem song in selected)
            {
                await DeleteSongFromDiskAsync(song);
            }

            NowPlayingText.Text = $"已将 {selected.Count} 首歌曲移入回收站";
            ExitMultiSelectMode();
        
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }}


        /// <summary>
        /// 左侧分类：歌曲/专辑/艺术家/文件夹为圆角选中；
        /// 播放列表为胶囊框，选中时填主题色、文字对比色。
        /// </summary>
        private void UpdateLibraryNavHighlight()
        {
            Brush accent = ResolveAccentBrush();
            Brush fg = ColorHelper.ResolveContrastingForeground(accent);
            var transparent = new SolidColorBrush(Colors.Transparent);
            Brush capsuleIdle = ResolveCapsuleFillBrush();
            Brush capsuleBorder = ResolveNavCapsuleBorderBrush();

            Button[] libraryButtons =
            {
                NavSongsButton,
                NavAlbumsButton,
                NavArtistsButton,
                NavAlbumArtistsButton,
                NavFoldersButton,
                NavFavoritesButton,
                NavRatingsButton,
                NavRecentButton,
                NavPlaylistWallButton,
                NavGenreButton,
                NavYearButton
            };

            foreach (Button button in libraryButtons)
            {
                button.CornerRadius = new CornerRadius(8);
                button.BorderThickness = new Thickness(0);
                string tag = button.Tag as string ?? string.Empty;
                bool active = string.Equals(_currentCategory, tag, StringComparison.Ordinal)
                    || (tag == "Genres" && _currentCategory is "Genres" or "GenreSongs")
                    || (tag == "Years" && _currentCategory is "Years" or "YearSongs");
                if (active)
                {
                    button.Background = accent;
                    button.Foreground = fg;
                }
                else
                {
                    button.Background = transparent;
                    button.ClearValue(Control.ForegroundProperty);
                }
            }

            const double playlistCapsuleHeight = 40;
            UserPlaylistNavButton.Height = playlistCapsuleHeight;
            UserPlaylistNavButton.MinHeight = playlistCapsuleHeight;
            UserPlaylistNavButton.CornerRadius = new CornerRadius(playlistCapsuleHeight / 2.0);
            UserPlaylistNavButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            bool playlistActive = string.Equals(_currentCategory, "PlaylistWall", StringComparison.Ordinal);
            if (playlistActive)
            {
                UserPlaylistNavButton.Background = accent;
                UserPlaylistNavButton.Foreground = fg;
                UserPlaylistNavButton.BorderThickness = new Thickness(0);
                UserPlaylistNavButton.ClearValue(Control.BorderBrushProperty);
            }
            else
            {
                UserPlaylistNavButton.Background = capsuleIdle;
                UserPlaylistNavButton.BorderThickness = new Thickness(1);
                UserPlaylistNavButton.BorderBrush = capsuleBorder;
                UserPlaylistNavButton.ClearValue(Control.ForegroundProperty);
            }

            // 标签排序胶囊按钮（与播放列表同款）
            NavTagSortButton.CornerRadius = new CornerRadius(playlistCapsuleHeight / 2.0);
            NavTagSortButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            bool tagSortActive = string.Equals(_currentCategory, "TagSort", StringComparison.Ordinal);
            if (tagSortActive)
            {
                NavTagSortButton.Background = accent;
                NavTagSortButton.Foreground = fg;
                NavTagSortButton.BorderThickness = new Thickness(0);
                NavTagSortButton.ClearValue(Control.BorderBrushProperty);
            }
            else
            {
                NavTagSortButton.Background = capsuleIdle;
                NavTagSortButton.BorderThickness = new Thickness(1);
                NavTagSortButton.BorderBrush = capsuleBorder;
                NavTagSortButton.ClearValue(Control.ForegroundProperty);
            }

            // 音效处理胶囊按钮（占位，后续接入 ECHO 音效页面）
            if (NavAudioFxButton != null)
            {
                NavAudioFxButton.CornerRadius = new CornerRadius(playlistCapsuleHeight / 2.0);
                NavAudioFxButton.HorizontalContentAlignment = HorizontalAlignment.Center;
                bool fxActive = string.Equals(_currentCategory, "AudioFX", StringComparison.Ordinal);
                if (fxActive)
                {
                    NavAudioFxButton.Background = accent;
                    NavAudioFxButton.Foreground = fg;
                    NavAudioFxButton.BorderThickness = new Thickness(0);
                    NavAudioFxButton.ClearValue(Control.BorderBrushProperty);
                }
                else
                {
                    NavAudioFxButton.Background = capsuleIdle;
                    NavAudioFxButton.BorderThickness = new Thickness(1);
                    NavAudioFxButton.BorderBrush = capsuleBorder;
                    NavAudioFxButton.ClearValue(Control.ForegroundProperty);
                }
            }
        }


        private void ApplyMultiSelectAlbumItemStyle(GridView grid)
        {
            ApplyAccentSelectionResources(grid);

            double margin = ReferenceEquals(grid, AlbumGridView) ? 8 : 6;
            var style = new Style(typeof(GridViewItem));
            style.Setters.Add(new Setter(GridViewItem.MarginProperty, new Thickness(margin)));
            style.Setters.Add(new Setter(GridViewItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(GridViewItem.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(GridViewItem.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(GridViewItem.BorderThicknessProperty, new Thickness(0)));
            grid.ItemContainerStyle = style;
            RefreshAlbumWallSelectionChrome(grid, GetAlbumCollectionForGrid(grid));
        }


        private void RefreshFolderBrowserSelectionChrome()
        {
            HashSet<object>? selectedSet = VisualTreeWalker.BuildSelectedItemsLookup(FolderBrowserView);
            foreach (ListViewItem container in EnumerateRealizedListViewItems(FolderBrowserView))
            {
                if (FolderBrowserView.ItemFromContainer(container) is FolderBrowserItem item)
                {
                    ApplyFolderBrowserItemSelectionChrome(container, item, selectedSet);
                }
            }
        }


        private void RefreshArtistAlbumSelectionChrome()
            => RefreshAlbumWallSelectionChrome(ArtistAlbumGridView, _artistAlbums);

        private void RefreshAlbumWallSelectionChrome(GridView grid, IEnumerable<AlbumEntry> albums)
        {
            HashSet<object>? selectedSet = VisualTreeWalker.BuildSelectedItemsLookup(grid);
            bool anyRealized = false;
            foreach (GridViewItem container in EnumerateRealizedGridViewItems(grid))
            {
                anyRealized = true;
                if (grid.ItemFromContainer(container) is AlbumEntry album)
                {
                    ApplyAlbumGridItemSelectionChrome(grid, container, album, selectedSet);
                }
            }

            // 面板尚未生成时回退（极少见）
            if (!anyRealized)
            {
                foreach (AlbumEntry album in albums)
                {
                    if (grid.ContainerFromItem(album) is GridViewItem container)
                    {
                        ApplyAlbumGridItemSelectionChrome(grid, container, album, selectedSet);
                    }
                }
            }
        }


        private void ApplyAlbumGridItemSelectionChrome(
            GridView grid,
            GridViewItem container,
            AlbumEntry album,
            HashSet<object>? selectedSet = null)
        {
            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ColorHelper.ResolveContrastingForeground(accent);
            bool multiOnThisGrid = _isMultiSelectMode && ReferenceEquals(_multiSelectAlbumGrid, grid);
            Brush unselectedBg = multiOnThisGrid
                ? CreateMultiSelectFrostBrush()
                : new SolidColorBrush(Colors.Transparent);

            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            bool selected = multiOnThisGrid
                ? VisualTreeWalker.IsItemSelected(grid, album, selectedSet)
                : ReferenceEquals(grid.SelectedItem, album);

            Border? chrome = VisualTreeWalker.FindTaggedBorder(container, "AlbumRowChrome");
            if (chrome != null)
            {
                chrome.CornerRadius = new CornerRadius(8);
                if (selected)
                {
                    chrome.Background = accent;
                    ApplyForegroundToDescendants(chrome, selectedFg);
                }
                else
                {
                    chrome.Background = unselectedBg;
                    ClearForegroundOnDescendants(chrome);
                }
            }
            else if (selected)
            {
                container.Background = accent;
                container.Foreground = selectedFg;
            }
            else
            {
                container.Background = unselectedBg;
                container.ClearValue(Control.ForegroundProperty);
            }
        }


        private async Task ApplyAlbumArtBackgroundAsync(byte[]? coverBytes, string forPath)
        {
            if (AlbumArtBackgroundImage == null)
            {
                return;
            }

            AppSettingsState settings = AppSettingsStore.Load();
            if (!settings.EnableBackground || !settings.AlbumCoverAsBackground)
            {
                ClearAlbumArtBackground();
                return;
            }

            if (coverBytes == null || coverBytes.Length == 0)
            {
                ClearAlbumArtBackground();
                return;
            }

            int blurRadius = settings.BackgroundGaussBlur ? settings.GaussBlurRadius : 0;
            byte[]? blurred = await Task.Run(() =>
                blurRadius > 0
                    ? AlbumArtBackground.CreateHeavilyBlurredPng(coverBytes, blurRadius: blurRadius)
                    : coverBytes);
            if (_nowPlayingPath != forPath || blurred == null || blurred.Length == 0)
            {
                return;
            }

            BitmapImage? image = await CreateBitmapFromBytesAsync(blurred);
            if (_nowPlayingPath != forPath)
            {
                return;
            }

            AlbumArtBackgroundImage.Source = image;
            if (AlbumArtBackgroundScrim != null)
            {
                AlbumArtBackgroundScrim.Opacity = 1;
            }
        }


        private void ClearAlbumArtBackground()
        {
            if (AlbumArtBackgroundImage != null)
            {
                AlbumArtBackgroundImage.Source = null;
            }
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
    }
}
