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

        private async void SelectArtistAvatarMenu_Click(dynamic sender, dynamic e)
        {
            if (_avatarContextArtist == null)
            {
                return;
            }

            ArtistEntry artist = _avatarContextArtist;

            try
            {
                FileOpenPicker picker = new();
                nint hwnd = NativeHandle;
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

                // 2026-10-12 改版（用户实测反馈）：一律走详情页右栏内嵌裁剪面板，不再弹独立编辑窗口。
                // 详情页没开就先打开该艺术家的详情页，再带着本地图片路径落头像页签。
                // 旧的 ArtistAvatarEditorWindow 窗口类保留作代码兜底，但入口全部摘掉。
                if (ArtistDetailPanel == null || ArtistDetailPanel.Visibility != Visibility.Visible)
                {
                    OpenArtistDetail(artist);
                }

                OpenArtistAvatarPanel(artist, file.Path);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("选择头像失败", ex.Message);
            }
        }


        private async void RestoreArtistAvatarMenu_Click(dynamic sender, dynamic e)
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

            // 排序状态保留在字段里，不在此重置：否则切换页面再返回时，
            // 用户之前选的排序方式会被硬编码回「标题/升序」。
            // 首次进入时字段已是默认值（Title/升序），后续进入沿用上次选择。
            // 同步 UI 文案到当前字段值，避免显示与实际排序不一致。
            ArtistSongSortButton.Content = _artistSongSortMode switch
            {
                ArtistSongSortMode.AlbumTitleThenTrack => "排序：专辑（标题）",
                ArtistSongSortMode.AlbumYearThenTrack => "排序：专辑（时间）",
                _ => "排序：标题"
            };
            ArtistAlbumSortFieldText.Text = _artistAlbumSortMode == ArtistAlbumSortMode.Year
                ? "排序：专辑（时间）"
                : "排序：专辑（标题）";
            ArtistAlbumSortOrderText.Text = _artistAlbumSortAscending ? "升序" : "降序";

            // 进入详情页即清除墙内选中项，返回后不再残留主题色选中框
            ArtistGridView.SelectedItem = null;
            ArtistGridView.Visibility = Visibility.Collapsed;
            ArtistDetailPanel.Visibility = Visibility.Visible;
            LibraryPaneTitle.Text = artist.Name;

            ArtistDetailNameText.Text = artist.Name;
            ArtistDetailAvatarBrush.ImageSource = artist.AvatarImage;
            ArtistDetailAvatarPlaceholder.Visibility =
                artist.AvatarImage == null ? Visibility.Visible : Visibility.Collapsed;

            // 2026-10-12 改版：矩形头像挂立体阴影；右栏页签回到「歌曲列表」。
            // 头像面板按需打开（不与歌曲/专辑并列），这里确保它是关着的、页签条可见。
            ApplyArtistDetailAvatarShadow();
            CloseArtistAvatarPanel();

            // 左栏头像下方的在线概览（Last.fm/MusicBrainz/Discogs），异步拉，拉不到就显示「暂无」
            _ = RefreshArtistOverviewAsync(artist.Name);

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

            // 专辑墙空态提示（2026-10-12：切到「专辑墙」页签看到白板会以为页面坏了）
            if (ArtistAlbumEmptyText != null)
            {
                ArtistAlbumEmptyText.Visibility = entries.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

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

                // 2026-10-16 参考 ECHO：批量封面走分档缓存（Album 320），未命中回落原路径
                BitmapImage? image = await CoverArtService.GetAsync(entry.CoverSourcePath, CoverVariant.Album);
                if (image == null)
                {
                    byte[]? bytes = await Task.Run(() => ExtractCoverBytes(entry.CoverSourcePath));
                    if (bytes == null || bytes.Length == 0)
                    {
                        continue;
                    }

                    image = await CreateBitmapFromBytesAsync(bytes, CoverDecodeTargetPx(AlbumDetailCoverImage, 232));
                }

                if (image != null)
                {
                    entry.CoverImage = image;
                    if (_openedAlbum != null &&
                        string.Equals(_openedAlbum.Name, entry.Name, StringComparison.CurrentCultureIgnoreCase))
                    {
                        AlbumDetailCoverImage.Source = image;
                        ApplyCoverFrame(AlbumDetailCoverBorder, AlbumDetailCoverImage);
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



        /// <summary>专辑墙右键附加操作（音乐库专辑墙 / 艺术家详情专辑共用）。</summary>
        private void AppendAlbumContextItems(MenuFlyout flyout, AlbumEntry album, bool fromArtist)
        {

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


        private void MultiSelectEditTagsButton_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedMultiSelectSongs();
            if (items.Count == 0)
            {
                return;
            }

            OpenTagEditForFiles(items.Select(i => i.FilePath).ToList());
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


        private void RefreshArtistAlbumSelectionChrome()
            => RefreshAlbumWallSelectionChrome(ArtistAlbumGridView, _artistAlbums);

    }
}
