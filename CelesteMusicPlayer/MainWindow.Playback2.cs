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

        private void AddArtistWorksToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_openedArtist != null)
            {
                AddSongsToUserPlaylist(GetTracksForArtist(_openedArtist.Name, useCurrentSongSort: true));
            }
        }


        private void PlayArtistSongsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_artistTracks.Count == 0)
            {
                return;
            }

            _userPlaylist.Clear();
            AddSongsToUserPlaylist(_artistTracks.ToList());
            PlayUserPlaylistAt(0);
        }


        private void AddArtistSongsToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_artistTracks.Count == 0)
            {
                return;
            }

            AddSongsToUserPlaylist(_artistTracks.ToList());
        }


        private void PlayAllArtistAlbumsButton_Click(object sender, RoutedEventArgs e)
        {
            List<PlaylistItem> tracks = CollectArtistAlbumTracksInOrder();
            if (tracks.Count == 0)
            {
                return;
            }

            _userPlaylist.Clear();
            AddSongsToUserPlaylist(tracks);
            PlayUserPlaylistAt(0);
        }


        /// <summary>歌曲面板「播放所有歌曲」：把当前歌曲列表全部加入播放队列并按当前排序播放。</summary>
        private void PlayAllLibrarySongsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_playlist.Count == 0)
            {
                return;
            }

            _userPlaylist.Clear();
            AddSongsToUserPlaylist(_playlist.ToList());
            PlayUserPlaylistAt(0);
        }


        private void AddAllArtistAlbumsToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            List<PlaylistItem> tracks = CollectArtistAlbumTracksInOrder();
            if (tracks.Count == 0)
            {
                return;
            }

            AddSongsToUserPlaylist(tracks);
        }


        /// <summary>
        /// 按当前界面中的专辑顺序，各专辑内按音轨号收集曲目（去重）。
        /// 例：Album1(曲1,曲2) → Album2(曲a,曲b) ⇒ 曲1,曲2,曲a,曲b。
        /// </summary>
        private List<PlaylistItem> CollectArtistAlbumTracksInOrder()
        {
            return CollectTracksFromAlbumsInDisplayOrder(_artistAlbums);
        }


        /// <summary>按传入专辑集合顺序，各专辑内按音轨号收集曲目。</summary>
        private List<PlaylistItem> CollectTracksFromAlbumsInDisplayOrder(IEnumerable<AlbumEntry> albums)
        {
            var tracks = new List<PlaylistItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AlbumEntry album in albums)
            {
                foreach (PlaylistItem track in GetTracksForAlbum(album))
                {
                    if (seen.Add(track.FilePath))
                    {
                        tracks.Add(track);
                    }
                }
            }

            return tracks;
        }


        private void RebuildArtistTracks()
        {
            _artistTracks.Clear();
            if (_openedArtist == null)
            {
                return;
            }

            string artistName = _openedArtist.Name;
            List<PlaylistItem> tracks = _playlist
                .Where(t => TrackMatchesArtistName(t, artistName, _artistDetailUsesAlbumArtist))
                .ToList();

            foreach (PlaylistItem track in ApplyArtistSongSort(tracks))
            {
                _artistTracks.Add(track);
            }
        }


        private List<PlaylistItem> ApplyArtistSongSort(List<PlaylistItem> tracks)
        {
            Dictionary<string, uint> albumYears = tracks
                .GroupBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Where(t => t.Year > 0).Select(t => t.Year).DefaultIfEmpty(0u).Max(),
                    StringComparer.CurrentCultureIgnoreCase);

            return _artistSongSortMode switch
            {
                ArtistSongSortMode.AlbumTitleThenTrack => tracks
                    .OrderBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                    .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                    .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                ArtistSongSortMode.AlbumYearThenTrack => tracks
                    .OrderBy(t =>
                    {
                        albumYears.TryGetValue(t.Album, out uint y);
                        return y == 0 ? uint.MaxValue : y;
                    })
                    .ThenBy(t => t.Album, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                    .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                    .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                _ => tracks
                    .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            };
        }


        private void ArtistSongSortButton_Click(object sender, RoutedEventArgs e)
        {
            // 点击循环切换三种排序：标题 -> 专辑（标题）-> 专辑（时间）-> 标题
            _artistSongSortMode = _artistSongSortMode switch
            {
                ArtistSongSortMode.Title => ArtistSongSortMode.AlbumTitleThenTrack,
                ArtistSongSortMode.AlbumTitleThenTrack => ArtistSongSortMode.AlbumYearThenTrack,
                _ => ArtistSongSortMode.Title
            };

            ArtistSongSortButton.Content = _artistSongSortMode switch
            {
                ArtistSongSortMode.AlbumTitleThenTrack => "排序：专辑（标题）",
                ArtistSongSortMode.AlbumYearThenTrack => "排序：专辑（时间）",
                _ => "排序：标题"
            };

            RebuildArtistTracks();
        }


        private void ArtistTrackListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            if (ArtistTrackListView.SelectedItem is PlaylistItem track)
            {
                PlayPlaylistItem(track);
            }
        }


        private void ArtistTrackListView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshArtistTrackSelectionChrome();
            UpdateSelectAllMultiSelectButtonState();
        }


        private void ArtistTrackListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(ArtistTrackListView, container, song);
            }
        }


        private void ArtistTrackListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            PlaylistItem? song = null;
            if (e.OriginalSource is DependencyObject source)
            {
                song = FindPlaylistItem(source);
                if (song == null)
                {
                    ListViewItem? container = FindAncestorListViewItem(source);
                    if (container != null)
                    {
                        song = ArtistTrackListView.ItemFromContainer(container) as PlaylistItem;
                    }
                }
            }

            if (song == null)
            {
                return;
            }

            // 右键也先选中，显示主题色圆角
            if (!_isMultiSelectMode)
            {
                ArtistTrackListView.SelectedItem = song;
            }

            _contextMenuSong = song;
            var flyout = BuildPlaylistItemContextMenu(song, false);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(ArtistTrackListView, e.GetPosition(ArtistTrackListView));
            }

            e.Handled = true;
        }


        /// <summary>从曲目集合汇总专辑条目（封面优先音轨 1）</summary>
        private static List<AlbumEntry> BuildAlbumEntriesFromTracks(IEnumerable<PlaylistItem> sourceTracks)
        {
            var groups = sourceTracks
                .GroupBy(p => p.Album, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var entries = new List<AlbumEntry>();
            int sortIndex = 0;
            foreach (var group in groups)
            {
                PlaylistItem coverTrack =
                    group.FirstOrDefault(t => (t.Disc == 0 || t.Disc == 1) && t.Track == 1)
                    ?? group.OrderBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                        .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                        .ThenBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase)
                        .First();

                uint year = group.Where(t => t.Year > 0).Select(t => t.Year).DefaultIfEmpty(0u).Max();
                TimeSpan total = TimeSpan.FromTicks(group.Sum(t => t.Duration.Ticks));

                string artist = group
                    .GroupBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.First().Artist)
                    .FirstOrDefault() ?? "未知艺术家";

                bool allDsd = group.All(t => IsDsdFile(t.FilePath));
                string? dsdExt = allDsd ? DsdExtOf(group.First().FilePath) : null;

                entries.Add(new AlbumEntry
                {
                    Name = group.First().Album,
                    Artist = artist,
                    Year = year,
                    TrackCount = group.Count(),
                    TotalDuration = total,
                    TotalDurationText = FormatTime(total),
                    CoverSourcePath = coverTrack.FilePath,
                    SortIndex = sortIndex++,
                    IsDsd = allDsd
                });
                if (allDsd && entries.Count > 0)
                {
                    entries[^1].SetDsdContainer(dsdExt);
                }
            }

            return entries;
        }


        /// <summary>
        /// 播放单曲：先加入（或提前到）播放列表最前，再按播放列表播放。
        /// </summary>
        private void PlayPlaylistItem(PlaylistItem track)
        {
            // 若已在该播放队列中：保持原顺序，仅定位到它播放（不再把它移到最前）。
            int existing = FindUserPlaylistIndex(track.FilePath);
            if (existing >= 0)
            {
                PlayUserPlaylistAt(existing);
                return;
            }

            // 不在播放队列：加入队列（按现有 InsertPlaylistAtBegin 设置决定位置，通常放最前）再播放。
            AddSongsToUserPlaylist(new[] { track });
            int userIndex = FindUserPlaylistIndex(track.FilePath);
            if (userIndex >= 0)
            {
                PlayUserPlaylistAt(userIndex);
                return;
            }

            int libraryIndex = FindLibraryIndex(track.FilePath);
            if (libraryIndex >= 0)
            {
                PlayLibraryItemAt(libraryIndex, syncUserPlaylistIndex: false);
            }
            else
            {
                StartPlayback(track);
                _userPlaylistIndex = -1;
            }
        }


        private int FindUserPlaylistIndex(string filePath)
        {
            for (int i = 0; i < _userPlaylist.Count; i++)
            {
                if (string.Equals(_userPlaylist[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }


        private List<PlaylistItem> GetTracksForAlbum(AlbumEntry album)
        {
            return _playlist
                .Where(t =>
                    string.Equals(t.Album, album.Name, StringComparison.CurrentCultureIgnoreCase)
                    && (string.IsNullOrWhiteSpace(album.Artist)
                        || string.Equals(t.Artist, album.Artist, StringComparison.CurrentCultureIgnoreCase)))
                .OrderBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                .ThenBy(t => t.Track == 0 ? uint.MaxValue : t.Track)
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }


        /// <summary>
        /// 播放专辑：可替换用户播放列表，并从音轨 1（若无则第一首）开始播放。
        /// </summary>
        private void PlayAlbum(AlbumEntry album, bool replacePlaylist)
        {
            List<PlaylistItem> tracks = GetTracksForAlbum(album);
            if (tracks.Count == 0)
            {
                return;
            }

            PlaylistItem first =
                tracks.FirstOrDefault(t => (t.Disc == 0 || t.Disc == 1) && t.Track == 1) ?? tracks[0];

            if (replacePlaylist)
            {
                _userPlaylist.Clear();
                AddSongsToUserPlaylist(tracks);
                int index = FindUserPlaylistIndex(first.FilePath);
                if (index >= 0)
                {
                    PlayUserPlaylistAt(index);
                }

                return;
            }

            PlayPlaylistItem(first);
        }


        private void AlbumDetailPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_openedAlbum != null)
            {
                PlayAlbum(_openedAlbum, replacePlaylist: true);
            }
        }


        private void AlbumDetailAddToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_openedAlbum != null)
            {
                AddSongsToUserPlaylist(GetTracksForAlbum(_openedAlbum));
            }
        }


        /// <summary>汇总专辑内歌曲质量行（编码器+位深/采样率，取出现最多的组合），如 "FLAC · 16bit/44kHz"。
        /// 只采样前若干首聚合，避免 CD 大专辑逐首解析(含 ffmpeg)导致打开卡顿。</summary>
        private static string BuildAlbumQualityLine(IEnumerable<PlaylistItem> tracks)
        {
            string? quality = tracks
                .Take(15)
                .Select(t => AudioInfoFormatter.FormatQualityLine(t.FilePath))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(quality) ? string.Empty : quality;
        }


        /// <summary>后台聚合专辑质量行并回填行3 TextBlock（避免 CD 大专辑逐首解析卡 UI）。</summary>
        private async System.Threading.Tasks.Task FillAlbumTechTextAsync(AlbumEntry album, IReadOnlyList<PlaylistItem> tracks)
        {
            try
            {
                string quality = await System.Threading.Tasks.Task.Run(() => BuildAlbumQualityLine(tracks));
                string duration = album.TotalDurationText;
                var segs = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(quality))
                {
                    segs.Add(quality.Replace(" · ", " | "));
                }

                if (!string.IsNullOrWhiteSpace(duration))
                {
                    segs.Add(duration);
                }

                string text = string.Join(" | ", segs);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (AlbumDetailTechText != null)
                    {
                        AlbumDetailTechText.Text = text;
                    }
                });
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>构建按碟片分组的视图（Apple Music 风格 CD 标题行）。</summary>
        private System.Collections.IEnumerable BuildAlbumGroupedView(IReadOnlyList<PlaylistItem> tracks)
        {
            bool hasDisc = tracks.Any(t => t.Disc > 0);
            var groups = new List<AlbumDiscGroup>();

            if (!hasDisc)
            {
                // 无碟号：不作分组，纯列表
                var g = new AlbumDiscGroup { Key = string.Empty };
                foreach (PlaylistItem t in tracks)
                {
                    g.Add(t);
                }

                groups.Add(g);
            }
            else
            {
                foreach (var grp in tracks
                    .GroupBy(t => t.Disc == 0 ? uint.MaxValue : t.Disc)
                    .OrderBy(g => g.Key))
                {
                    var g = new AlbumDiscGroup
                    {
                        Key = grp.Key == uint.MaxValue ? "CD?" : "CD" + grp.Key
                    };
                    foreach (PlaylistItem t in grp.OrderBy(x => x.Track == 0 ? uint.MaxValue : x.Track))
                    {
                        g.Add(t);
                    }

                    groups.Add(g);
                }
            }

            var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource
            {
                IsSourceGrouped = true
            };
            cvs.Source = groups;
            return cvs.View;
        }


        private void AlbumTrackListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            if (AlbumTrackListView.SelectedItem is PlaylistItem track)
            {
                PlayPlaylistItem(track);
            }
        }


        private void AlbumTrackListView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshAlbumTrackSelectionChrome();
            UpdateSelectAllMultiSelectButtonState();
        }


        private void AlbumTrackListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(AlbumTrackListView, container, song);
            }
        }


        private void AlbumTrackListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            PlaylistItem? song = null;
            if (e.OriginalSource is DependencyObject source)
            {
                song = FindPlaylistItem(source);
                if (song == null)
                {
                    ListViewItem? container = FindAncestorListViewItem(source);
                    if (container != null)
                    {
                        song = AlbumTrackListView.ItemFromContainer(container) as PlaylistItem;
                    }
                }
            }

            if (song == null)
            {
                return;
            }

            if (!_isMultiSelectMode)
            {
                AlbumTrackListView.SelectedItem = song;
            }

            _contextMenuSong = song;
            var flyout = BuildPlaylistItemContextMenu(song, false);

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(AlbumTrackListView, e.GetPosition(AlbumTrackListView));
            }

            e.Handled = true;
        }


        // =====================================================================
        // 排序
        // =====================================================================

        private void SetSongSortUiForCategory(bool isUserPlaylist)
        {
            Visibility librarySort = isUserPlaylist ? Visibility.Collapsed : Visibility.Visible;
            Visibility playlistSort = isUserPlaylist ? Visibility.Visible : Visibility.Collapsed;
            SortFieldButton.Visibility = librarySort;
            SortOrderButton.Visibility = librarySort;
            ChangeSortButton.Visibility = playlistSort;
        }


        private async void ChangeSortButton_Click(object sender, RoutedEventArgs e)
            => await ChangeUserPlaylistSortAsync(Content.XamlRoot);

        /// <summary>播放列表「更改排序」对话框（主界面与当前播放列表窗口共用）。</summary>
        internal async Task ChangeUserPlaylistSortAsync(XamlRoot xamlRoot)
        {
            var fieldBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = _sortField switch
                {
                    SortField.Album => 1,
                    SortField.Artist => 2,
                    SortField.Year => 3,
                    SortField.Duration => 4,
                    _ => 0
                }
            };
            fieldBox.Items.Add("标题");
            fieldBox.Items.Add("专辑");
            fieldBox.Items.Add("艺术家");
            fieldBox.Items.Add("年份");
            fieldBox.Items.Add("时长");

            var orderBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = _sortAscending ? 0 : 1
            };
            orderBox.Items.Add("升序");
            orderBox.Items.Add("降序");

            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = "若更改排序，原有的顺序则会被打乱",
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.85
            });
            panel.Children.Add(new TextBlock { Text = "排序字段", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(fieldBox);
            panel.Children.Add(new TextBlock { Text = "排序方向", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(orderBox);

            ContentDialog dialog = new()
            {
                Title = "更改排序",
                Content = panel,
                PrimaryButtonText = "应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _sortField = fieldBox.SelectedIndex switch
            {
                1 => SortField.Album,
                2 => SortField.Artist,
                3 => SortField.Year,
                4 => SortField.Duration,
                _ => SortField.Title
            };
            _sortAscending = orderBox.SelectedIndex == 0;

            string? playingPath = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                ? _userPlaylist[_userPlaylistIndex].FilePath
                : null;

            SortCollection(_userPlaylist, playingPath);
            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }


        private static string GetSortFieldDisplayName(SortField field) => field switch
        {
            SortField.Artist => "艺术家",
            SortField.Album => "专辑",
            SortField.Year => "年份",
            SortField.Duration => "时长",
            SortField.Genre => "流派",
            SortField.Track => "音轨号",
            SortField.FilePath => "文件路径",
            _ => "标题"
        };


        private ObservableCollection<PlaylistItem> GetActiveSongCollection()
            => string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal)
                ? _userPlaylist
                : _playlist;

        /// <summary>
        /// 按当前字段与升/降序重排当前中间列表，并刷新序号与当前播放下标。
        /// </summary>
        private void ApplySort(string? preservePlayingPath)
        {
            ObservableCollection<PlaylistItem> target = GetActiveSongCollection();
            SortCollection(target, preservePlayingPath);
            if (_currentCategory == "Songs")
            {
                ApplySongsSearchFilter();
            }
        }


        private void SortCollection(ObservableCollection<PlaylistItem> target, string? preservePlayingPath)
        {
            if (target.Count <= 1)
            {
                RenumberCollection(target);
                SyncIndicesAfterSort(target, preservePlayingPath);
                return;
            }

            IOrderedEnumerable<PlaylistItem> ordered = _sortField switch
            {
                SortField.Artist => target.OrderBy(i => i.Artist, StringComparer.CurrentCultureIgnoreCase),
                SortField.Album => target.OrderBy(i => i.Album, StringComparer.CurrentCultureIgnoreCase),
                SortField.Year => target.OrderBy(i => i.Year),
                SortField.Duration => target.OrderBy(i => i.Duration),
                SortField.Genre => target.OrderBy(i => i.Genre, StringComparer.CurrentCultureIgnoreCase),
                SortField.Track => target.OrderBy(i => i.Track),
                SortField.FilePath => target.OrderBy(i => i.FilePath, StringComparer.CurrentCultureIgnoreCase),
                _ => target.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            };

            // 次要关键字：标题，同字段时顺序更稳定
            ordered = ordered.ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase);

            List<PlaylistItem> sorted = (_sortAscending
                ? ordered.AsEnumerable()
                : ordered.Reverse()).ToList();

            target.Clear();
            foreach (PlaylistItem item in sorted)
            {
                target.Add(item);
            }

            RenumberCollection(target);
            SyncIndicesAfterSort(target, preservePlayingPath);
        }


        private void SyncIndicesAfterSort(ObservableCollection<PlaylistItem> target, string? preservePlayingPath)
        {
            if (ReferenceEquals(target, _playlist))
            {
                UpdateCurrentIndexByPath(preservePlayingPath);
                if (_currentIndex >= 0 && _currentIndex < _playlist.Count
                    && string.Equals(_currentCategory, "Songs", StringComparison.Ordinal))
                {
                    PlaylistView.SelectedIndex = _currentIndex;
                }
            }
            else if (ReferenceEquals(target, _userPlaylist) && !string.IsNullOrEmpty(preservePlayingPath))
            {
                _userPlaylistIndex = FindUserPlaylistIndex(preservePlayingPath);
                if (_userPlaylistIndex >= 0
                    && string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
                {
                    PlaylistView.SelectedIndex = _userPlaylistIndex;
                }
            }
        }


        private void RenumberIndices() => RenumberCollection(_playlist);

        private static void RenumberCollection(ObservableCollection<PlaylistItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Index = i + 1;
            }
        }


        /// <summary>点击播放列表空白区取消当前选中（从主题色背景恢复常态）。</summary>
        private void PlaylistList_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            // 若点击落在某个列表项内，保留选中
            Microsoft.UI.Xaml.DependencyObject? origin = e.OriginalSource as Microsoft.UI.Xaml.DependencyObject;
            while (origin != null)
            {
                if (origin is ListViewItem)
                {
                    return;
                }

                origin = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(origin);
            }

            // 点空白：取消选中
            if (PlaylistView.SelectedItem != null)
            {
                PlaylistView.SelectedItem = null;
            }
        }

        /// <summary>拖拽重排后更新歌曲序号（针对当前显示的集合），并强制刷新使序号立即生效。</summary>
        private void PlaylistView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            try
            {
                if (ReferenceEquals(sender.ItemsSource, _userPlaylist))
                {
                    RenumberCollection(_userPlaylist);
                }
                else if (ReferenceEquals(sender.ItemsSource, _playlist))
                {
                    RenumberCollection(_playlist);
                }
                else if (sender.ItemsSource is System.Collections.ObjectModel.ObservableCollection<PlaylistItem> col)
                {
                    RenumberCollection(col);
                }

                // x:Bind Index 为 OneTime：重设 ItemsSource 强制整体刷新，使拖拽后的序号立即更新
                if (sender is ListView lv)
                {
                    object? src = lv.ItemsSource;
                    lv.ItemsSource = null;
                    lv.ItemsSource = src;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void PlaylistView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            if (PlaylistView.SelectedItem is not PlaylistItem item)
            {
                return;
            }

            // 播放列表界面：按当前顺序播放，不把歌曲提前到最前
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                int index = FindUserPlaylistIndex(item.FilePath);
                if (index >= 0)
                {
                    PlayUserPlaylistAt(index);
                }

                return;
            }

            PlayPlaylistItem(item);
        }


        private void PlaylistView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (PlaylistListBorder.Visibility != Visibility.Visible)
            {
                return;
            }

            PlaylistItem? song = null;
            if (e.OriginalSource is DependencyObject source)
            {
                song = FindPlaylistItem(source);
                if (song == null)
                {
                    ListViewItem? container = FindAncestorListViewItem(source);
                    if (container != null)
                    {
                        song = PlaylistView.ItemFromContainer(container) as PlaylistItem;
                    }
                }
            }

            if (song == null)
            {
                return;
            }

            _contextMenuSong = song;

            // 右键也先选中，主题色圆角与左键一致
            if (!_isMultiSelectMode)
            {
                PlaylistView.SelectedItem = song;
            }

            var flyout = BuildPlaylistItemContextMenu(song, string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal));

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


        /// <summary>构建歌曲右键菜单（所有歌曲列表视图共用）。inUserPlaylist 为 true 时含用户播放列表专属项。</summary>
        private MenuFlyout BuildPlaylistItemContextMenu(PlaylistItem song, bool inUserPlaylist, Action? multiSelectAction = null)
        {
            _contextMenuSong = song;
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                if (_contextMenuSong == null)
                {
                    return;
                }

                ExitMultiSelectMode();
                // 播放列表内：仅播放，不提前到最前；其它界面仍走加入并播放
                if (inUserPlaylist)
                {
                    int index = FindUserPlaylistIndex(_contextMenuSong.FilePath);
                    if (index >= 0)
                    {
                        PlayUserPlaylistAt(index);
                    }
                }
                else
                {
                    PlayPlaylistItem(_contextMenuSong);
                }
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) =>
            {
                if (multiSelectAction != null)
                {
                    multiSelectAction();
                }
                else
                {
                    EnterMultiSelectMode(_contextMenuSong);
                }
            };

            flyout.Items.Add(playItem);

            if (inUserPlaylist)
            {
                var pinItem = new MenuFlyoutItem { Text = "置顶" };
                // Upload：上箭头 + 顶栏横线，近似「横线下朝上箭头」
                pinItem.Icon = new FontIcon { Glyph = "\uE898" };
                pinItem.Click += (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        PinSongToUserPlaylistTop(_contextMenuSong);
                    }
                };
                flyout.Items.Add(pinItem);
            }

            flyout.Items.Add(multiItem);

            if (inUserPlaylist)
            {
                var removeItem = new MenuFlyoutItem { Text = "从播放队列中删除" };
                removeItem.Icon = new FontIcon { Glyph = "\uE74D" };
                removeItem.Click += (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        RemoveSongsFromUserPlaylist(new[] { _contextMenuSong });
                    }
                };
                flyout.Items.Add(removeItem);
            }
            else
            {
                var addItem = new MenuFlyoutItem { Text = "添加至播放队列" };
                addItem.Icon = new FontIcon { Glyph = "\uE710" };
                addItem.Click += (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        AddSongsToUserPlaylist(new[] { _contextMenuSong });
                    }
                };
                flyout.Items.Add(addItem);

                var wallItem = new MenuFlyoutItem { Text = "添加到播放列表", Icon = new FontIcon { Glyph = "\uE8B7" } };
                wallItem.Click += (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        _ = ShowNamedPlaylistPickerAsync(new[] { _contextMenuSong });
                    }
                };
                flyout.Items.Add(wallItem);
            }

            AppendPlaylistContextFeatureItems(flyout, song, inUserPlaylist);

            var deleteDiskItem = new MenuFlyoutItem { Text = "从磁盘删除（回收站）" };
            deleteDiskItem.Icon = new FontIcon { Glyph = "\uE74D" };
            bool disableDelete = AppSettingsStore.Load().DisableDeleteFromDisk;
            if (disableDelete)
            {
                deleteDiskItem.Text = "从磁盘删除（已在设置中禁用）";
                deleteDiskItem.IsEnabled = false;
            }
            else
            {
                deleteDiskItem.Click += async (_, _) =>
                {
                    if (_contextMenuSong != null)
                    {
                        await DeleteSongFromDiskAsync(_contextMenuSong);
                    }
                };
            }

            flyout.Items.Add(deleteDiskItem);

            flyout.Items.Add(CreateOpenFileLocationMenuItem());
            return flyout;
        }


        /// <summary>将歌曲移到用户播放列表最前（已在最前则不变）。</summary>
        internal void PinSongToUserPlaylistTop(PlaylistItem song)
        {
            int index = FindUserPlaylistIndex(song.FilePath);
            if (index <= 0)
            {
                return;
            }

            string? playingPath = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                ? _userPlaylist[_userPlaylistIndex].FilePath
                : null;

            PlaylistItem item = _userPlaylist[index];
            _userPlaylist.RemoveAt(index);
            _userPlaylist.Insert(0, item);
            RenumberCollection(_userPlaylist);

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                _userPlaylistIndex = FindUserPlaylistIndex(playingPath);
            }

            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }


        private void ShowCurrentPlaylistButton_Click(object sender, RoutedEventArgs e)
            => ShowCurrentPlaylistWindow();

        internal void ShowCurrentPlaylistWindow()
        {
            if (_currentPlaylistWindow != null)
            {
                _currentPlaylistWindow.Activate();
                return;
            }

            _currentPlaylistWindow = new CurrentPlaylistWindow(this);
            _currentPlaylistWindow.Closed += (_, _) => _currentPlaylistWindow = null;
            _currentPlaylistWindow.Activate();
        }


        /// <summary>打开播放队列窗口（复用主播放列表，聚焦接下来的待播曲目）。</summary>
        internal void ShowPlayQueueWindow()
        {
            if (QueueWindow != null)
            {
                QueueWindow.Activate();
                return;
            }

            try
            {
                var w = new PlayQueueWindow(this);
                w.Activate();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ShowPlayQueueWindow", ex);
            }
        }


        /// <summary>从播放队列窗口清空整个播放列表（不弹确认）。</summary>
        internal void ClearUserPlaylistPublicFromQueue()
        {
            try
            {
                _userPlaylist.Clear();
                _userPlaylistIndex = -1;
                if (PlaylistView.ItemsSource == _userPlaylist || PlaylistView.ItemsSource != null)
                {
                    ApplyCategoryView();
                }

                StopEngineIfActive();
                MediaPlayer? p = GetPlayer();
                if (p != null)
                {
                    try
                    {
                        p.Source = null;
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                NowPlayingTitleText.Text = "未在播放";
                ResetNowPlayingArtistAlbumLinks();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ClearQueue", ex);
            }
        }


        internal void NotifyCurrentPlaylistWindow()
            => _currentPlaylistWindow?.RefreshFromOwner();

        internal ObservableCollection<PlaylistItem> UserPlaylist => _userPlaylist;

        /// <summary>媒体库全部歌曲（供重复文件检测等窗口使用）。</summary>
        internal IReadOnlyList<PlaylistItem> LibraryTracks => _playlist;

        internal int UserPlaylistPlayingIndex => _userPlaylistIndex;

        internal Brush GetAccentBrush() => ResolveAccentBrush();

        internal Brush GetAccentForegroundBrush() => ResolveAccentForegroundBrush();

        internal Brush GetCapsuleFillBrush() => ResolveCapsuleFillBrush();

        internal Brush GetMultiSelectFrostBrush() => CreateMultiSelectFrostBrush();

        internal int FindUserPlaylistIndexPublic(string filePath) => FindUserPlaylistIndex(filePath);

        internal void PlayUserPlaylistAtPublic(int index) => PlayUserPlaylistAt(index);

        internal void RemoveSongsFromUserPlaylistPublic(IEnumerable<PlaylistItem> songs)
            => RemoveSongsFromUserPlaylist(songs);

        internal static void OpenFileLocationInExplorerPublic(string? filePath)
            => OpenFileLocationInExplorer(filePath);

        /// <summary>当前歌曲分类主列表的快照（供曲库健康等面板读取）。</summary>
        internal System.Collections.Generic.IReadOnlyList<PlaylistItem> GetCurrentPlaylistSnapshot()
            => _playlist.ToList();

        /// <summary>从当前歌曲分类主列表移除指定路径的条目（仅去掉索引，不删除物理文件），并刷新界面。</summary>
        internal void RemoveFilesFromCurrentPlaylist(System.Collections.Generic.IEnumerable<string> paths)
        {
            var dead = new System.Collections.Generic.HashSet<string>(
                paths, System.StringComparer.OrdinalIgnoreCase);
            for (int i = _playlist.Count - 1; i >= 0; i--)
            {
                if (dead.Contains(_playlist[i].FilePath))
                {
                    if (i < _currentIndex)
                    {
                        _currentIndex--;
                    }
                    _playlist.RemoveAt(i);
                }
            }

            if (_currentIndex >= _playlist.Count)
            {
                if (_playlist.Count == 0)
                {
                    _currentIndex = -1;
                    GetPlayer()?.Pause();
                    ClearNowPlayingPanel();
                }
                else
                {
                    _currentIndex = _playlist.Count - 1;
                }
            }
        }


        internal void PlayUserPlaylistFromStart()
        {
            if (_userPlaylist.Count == 0)
            {
                return;
            }

            ExitMultiSelectMode();
            PlayUserPlaylistAt(0);
            NotifyCurrentPlaylistWindow();
        }


        private void EnterMultiSelectMode(PlaylistItem? preselect)
        {
            // 退出专辑 / 文件夹多选，避免两套多选并存
            if (_multiSelectAlbumGrid != null)
            {
                ExitAlbumMultiSelectUiOnly();
            }

            if (_multiSelectFolderList != null)
            {
                ExitFolderMultiSelectUiOnly();
            }

            ListView? target = ResolveMultiSelectTargetList();
            if (target == null)
            {
                return;
            }

            _multiSelectTargetList = target;
            _multiSelectAlbumGrid = null;
            if (target == PlaylistView)
            {
                _playlistItemDefaultStyle ??= PlaylistView.ItemContainerStyle;
            }
            else if (target == ArtistTrackListView)
            {
                _artistTrackItemDefaultStyle ??= ArtistTrackListView.ItemContainerStyle;
            }
            else if (target == AlbumTrackListView)
            {
                _albumTrackItemDefaultStyle ??= AlbumTrackListView.ItemContainerStyle;
            }

            _isMultiSelectMode = true;
            SetListSelectionMode(target, ListViewSelectionMode.Multiple);

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择歌曲";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
            UpdateUserPlaylistActionBarVisibility();
            ApplyAccentSelectionResources(target);
            ApplyMultiSelectItemStyle(target);
            UpdateLibrarySearchUi();
            if (target == PlaylistView)
            {
                ApplyCapsuleSortButtonStyle(accent: true);
            }

            if (preselect != null)
            {
                try
                {
                    target.SelectedItems.Add(preselect);
                }
                catch
                {
                    target.SelectedItem = preselect;
                }
            }

            DispatcherQueue.TryEnqueue(RefreshMultiSelectItemBackgrounds);
        }


        private IReadOnlyList<PlaylistItem> GetMultiSelectSongSource(ListView target)
        {
            if (target == ArtistTrackListView)
            {
                return _artistTracks;
            }

            if (target == AlbumTrackListView)
            {
                return _albumTracks;
            }

            return GetActiveSongCollection();
        }


        private void UpdateUserPlaylistActionBarVisibility()
        {
            bool show = !_isMultiSelectMode
                && string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal)
                && PlaylistListBorder.Visibility == Visibility.Visible;
            UserPlaylistActionBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }


        private void ExitSongMultiSelectUiOnly()
        {
            if (_multiSelectTargetList == null)
            {
                return;
            }

            ListView target = _multiSelectTargetList;
            SetListSelectionMode(target, ListViewSelectionMode.Single);

            if (target == PlaylistView && _playlistItemDefaultStyle != null)
            {
                PlaylistView.ItemContainerStyle = _playlistItemDefaultStyle;
            }
            else if (target == ArtistTrackListView && _artistTrackItemDefaultStyle != null)
            {
                ArtistTrackListView.ItemContainerStyle = _artistTrackItemDefaultStyle;
            }
            else if (target == AlbumTrackListView && _albumTrackItemDefaultStyle != null)
            {
                AlbumTrackListView.ItemContainerStyle = _albumTrackItemDefaultStyle;
            }

            ApplyAccentSelectionResources(target);
            _multiSelectTargetList = null;
        }


        private void SetPlaylistSelectionMode(ListViewSelectionMode mode)
            => SetListSelectionMode(PlaylistView, mode);

        private void MultiSelectPrimaryActionButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            bool isUserPlaylist = _multiSelectTargetList == PlaylistView
                && string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal);
            bool isNamedPlaylistDetail = _multiSelectTargetList == PlaylistDetailListView;
            bool isTagSortSongs = _multiSelectTargetList == TagSortPanelSongListView;
            bool isDetailSongList = _multiSelectTargetList == AlbumTrackListView
                || _multiSelectTargetList == ArtistTrackListView;
            if (isUserPlaylist)
            {
                RemoveSongsFromUserPlaylist(selected);
            }
            else if (isNamedPlaylistDetail)
            {
                // 命中单详情页多选 → 从当前命名单删除勾选的歌
                RemoveSongsFromCurrentPlaylistDetail(selected);
            }
            else if (isDetailSongList)
            {
                // 专辑/艺术家/专辑艺术家详情页多选 → 添加到播放列表（列表墙/命名单）
                _ = ShowNamedPlaylistPickerAsync(selected);
            }
            else
            {
                // 含标签排序面板曲目：添加到播放队列
                AddSongsToUserPlaylist(selected);
            }

            ExitMultiSelectMode();
        }


        private void MultiSelectAddToPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            _ = ShowNamedPlaylistPickerAsync(selected); // 多选专辑 → 添加到播放列表（列表墙/命名单）
            ExitMultiSelectMode();
        }


        /// <summary>获取当前多选模式下选中的歌曲（专辑墙 / 文件夹 / 歌曲列表统一出口）。</summary>
        private List<PlaylistItem> GetSelectedMultiSelectSongs()
        {
            if (_multiSelectAlbumGrid != null)
            {
                GridView grid = _multiSelectAlbumGrid;
                // 标签排序分类墙/面板专辑/艺术家网格：选中项是 TagSortCategoryEntry，按各自字段聚合其曲目
                if (ReferenceEquals(grid, TagSortPanelGridView) || ReferenceEquals(grid, TagSortClassGridView))
                {
                    var cat = grid.SelectedItems.OfType<TagSortCategoryEntry>().ToList();
                    var songs = new List<PlaylistItem>();
                    foreach (var c in cat) songs.AddRange(CollectTagSortCategorySongs(c));
                    return songs;
                }

                var selectedAlbums = grid.SelectedItems.OfType<AlbumEntry>().ToList();
                List<AlbumEntry> ordered = GetAlbumCollectionForGrid(grid)
                    .Where(a => selectedAlbums.Contains(a))
                    .ToList();
                return CollectTracksFromAlbumsInDisplayOrder(ordered);
            }

            if (_multiSelectFolderList != null)
            {
                var selectedItems = FolderBrowserView.SelectedItems.OfType<FolderBrowserItem>().ToList();
                List<FolderBrowserItem> ordered = _folderBrowserItems
                    .Where(i => selectedItems.Contains(i))
                    .ToList();
                return CollectTracksFromSelectedFolderItems(ordered);
            }

            ListView target = _multiSelectTargetList ?? PlaylistView;
            return target.SelectedItems.OfType<PlaylistItem>().ToList();
        }


        private void RemoveSongsFromUserPlaylist(IEnumerable<PlaylistItem> songs)
        {
            var paths = new HashSet<string>(
                songs.Select(s => s.FilePath),
                StringComparer.OrdinalIgnoreCase);

            for (int i = _userPlaylist.Count - 1; i >= 0; i--)
            {
                if (paths.Contains(_userPlaylist[i].FilePath))
                {
                    _userPlaylist.RemoveAt(i);
                    if (i < _userPlaylistIndex)
                    {
                        _userPlaylistIndex--;
                    }
                }
            }

            if (_userPlaylistIndex >= _userPlaylist.Count)
            {
                _userPlaylistIndex = _userPlaylist.Count - 1;
            }

            RenumberCollection(_userPlaylist);
            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }


        private async void SavePlaylistButton_Click(object sender, RoutedEventArgs e)
            => await SaveUserPlaylistAsync(Content.XamlRoot);

        internal async Task SaveUserPlaylistAsync(XamlRoot xamlRoot)
        {
            try
            {
                if (_userPlaylist.Count == 0)
                {
                    await ShowErrorAsync("保存播放列表", "当前播放列表为空。", xamlRoot);
                    return;
                }

                string folder = UserPlaylistFileStore.EnsurePlayListFolder();
                string? name = await AskPlaylistFileNameAsync(xamlRoot);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                string safeName = UserPlaylistFileStore.SanitizeFileName(name);
                string path = Path.Combine(folder, safeName + UserPlaylistFileStore.FileExtension);

                var dto = new UserPlaylistFileDto
                {
                    Name = safeName,
                    SavedAt = DateTimeOffset.Now,
                    Songs = _userPlaylist.Select(s => new UserPlaylistSongDto
                    {
                        FilePath = s.FilePath,
                        Title = s.Title,
                        Artist = s.Artist,
                        Album = s.Album,
                        Year = s.Year,
                        DurationSeconds = s.Duration.TotalSeconds
                    }).ToList()
                };

                UserPlaylistFileStore.SaveToPath(path, dto);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("保存播放列表失败", ex.Message, xamlRoot);
            }
        }


        private async void OpenPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            // “添加到播放列表”：把当前队列歌曲追加到所选/新建命名单（列表墙）
            await ShowNamedPlaylistPickerAsync(_userPlaylist.ToList());
        }


        internal async Task OpenUserPlaylistAsync(XamlRoot xamlRoot, bool navigateToPlaylist)
        {
            try
            {
                string folder = UserPlaylistFileStore.EnsurePlayListFolder();
                string? path = await PickPlaylistPathFromFolderAsync(folder, xamlRoot);
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                UserPlaylistFileDto? dto = UserPlaylistFileStore.LoadFromPath(path);
                if (dto?.Songs == null)
                {
                    await ShowErrorAsync("打开播放列表失败", "文件格式无效。", xamlRoot);
                    return;
                }

                ApplyLoadedUserPlaylist(dto, navigateToPlaylist);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("打开播放列表失败", ex.Message, xamlRoot);
            }
        }


        private async void ClearPlaylistButton_Click(object sender, RoutedEventArgs e)
            => await ClearUserPlaylistAsync(Content.XamlRoot);

        internal async Task ClearUserPlaylistAsync(XamlRoot xamlRoot)
        {
            if (_userPlaylist.Count == 0)
            {
                return;
            }

            ContentDialog dialog = new()
            {
                Title = "清空播放列表",
                Content = "确定清空当前播放列表中的全部歌曲吗？",
                PrimaryButtonText = "清空",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };

            ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            ExitMultiSelectMode();
            _userPlaylist.Clear();
            _userPlaylistIndex = -1;
            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }


        private void PlayUserPlaylistButton_Click(object sender, RoutedEventArgs e)
            => PlayUserPlaylistFromStart();

        private async Task<string?> AskPlaylistFileNameAsync(XamlRoot xamlRoot)
        {
            var box = new Microsoft.UI.Xaml.Controls.TextBox
            {
                Text = "我的播放列表",
                PlaceholderText = "输入播放列表名称"
            };

            ContentDialog dialog = new()
            {
                Title = "保存播放列表",
                Content = box,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            ApplyDialogAccent(dialog);
            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? box.Text?.Trim() : null;
        }


        private async Task<string?> PickPlaylistPathFromFolderAsync(string folderPath, XamlRoot xamlRoot)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folderPath, "*.json")
                    .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                    .ToArray();
            }
            catch
            {
                files = Array.Empty<string>();
            }

            if (files.Length == 0)
            {
                await ShowErrorAsync("打开播放列表", "PlayList 文件夹中还没有已保存的播放列表。", xamlRoot);
                return null;
            }

            var list = new ListView
            {
                ItemsSource = files.Select(Path.GetFileName).ToList(),
                SelectionMode = ListViewSelectionMode.Single,
                Height = 220
            };
            list.SelectedIndex = 0;

            ContentDialog dialog = new()
            {
                Title = "选择播放列表",
                Content = list,
                PrimaryButtonText = "打开",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            ApplyDialogAccent(dialog);
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || list.SelectedIndex < 0)
            {
                return null;
            }

            return files[list.SelectedIndex];
        }


        private void ApplyLoadedUserPlaylist(UserPlaylistFileDto dto, bool navigateToPlaylist = true)
        {
            // 按文件顺序追加，不走「插到最前」逻辑，避免顺序颠倒
            _userPlaylist.Clear();
            foreach (UserPlaylistSongDto song in dto.Songs)
            {
                if (string.IsNullOrWhiteSpace(song.FilePath) || !System.IO.File.Exists(song.FilePath))
                {
                    continue;
                }

                if (_userPlaylist.Any(p =>
                        string.Equals(p.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                PlaylistItem? fromLibrary = _playlist.FirstOrDefault(p =>
                    string.Equals(p.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase));

                if (fromLibrary != null)
                {
                    _userPlaylist.Add(ClonePlaylistItem(fromLibrary));
                    continue;
                }

                TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, song.DurationSeconds));
                _userPlaylist.Add(new PlaylistItem
                {
                    Title = string.IsNullOrWhiteSpace(song.Title)
                        ? Path.GetFileNameWithoutExtension(song.FilePath)
                        : song.Title,
                    Artist = string.IsNullOrWhiteSpace(song.Artist) ? "未知艺术家" : song.Artist,
                    AlbumArtist = string.IsNullOrWhiteSpace(song.Artist) ? "未知艺术家" : song.Artist,
                    Album = string.IsNullOrWhiteSpace(song.Album) ? "未知专辑" : song.Album,
                    Year = song.Year,
                    Duration = duration,
                    FilePath = song.FilePath
                });
            }

            RenumberCollection(_userPlaylist);
            NotifyCurrentPlaylistWindow();

            if (!navigateToPlaylist)
            {
                return;
            }

            CommitLibraryNavigation(() =>
            {
                _currentCategory = "UserPlaylist";
                ApplyCategoryView();
            });
        }


        /// <summary>
        /// 将歌曲插入播放列表最前（保持传入顺序）。
        /// 若已存在则先移除再提前，避免重复。大批量时整表重建，避免反复 Insert(0) 导致卡死/崩溃。
        /// </summary>
        /// <summary>弹“添加到播放列表”选择器（Poweramp 逻辑）：选命名单或新建，把歌曲追加进去。</summary>
        private async System.Threading.Tasks.Task ShowNamedPlaylistPickerAsync(IReadOnlyList<PlaylistItem> songs)
        {
            if (songs == null || songs.Count == 0) return;

            var paths = songs
                .Select(s => s.FilePath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0) return;

            var prompt = new TextBlock
            {
                Text = "选择要添加到的播放列表：",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
            panel.Children.Add(prompt);

            var names = NamedPlaylistStore.List();
            bool first = true;
            foreach (string name in names)
            {
                var rb = new RadioButton { Content = name, GroupName = "__addtopl" };
                rb.Tag = name;
                rb.IsChecked = first;
                first = false;
                panel.Children.Add(rb);
            }

            var newNameBox = new Microsoft.UI.Xaml.Controls.TextBox { PlaceholderText = "或输入新播放列表名称（回车即新建添加）" };
            panel.Children.Add(newNameBox);

            var dialog = new ContentDialog
            {
                Title = "添加到播放列表",
                Content = new ScrollViewer { Content = panel },
                PrimaryButtonText = "添加",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content?.XamlRoot,
            };

            ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            string? chosen = (panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string)?.Trim();
            string? typed = newNameBox.Text?.Trim();
            string? target = string.IsNullOrEmpty(typed) ? chosen : typed;
            if (string.IsNullOrEmpty(target)) return;

            var merged = NamedPlaylistStore
                .LoadSongs(target)
                .Concat(paths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            NamedPlaylistStore.SaveSongs(target, merged);
            PlaylistLibraryService.Refresh();
            StartupLog.Write("已添加到播放列表: " + target + " (+" + paths.Count + " 首)");
        }


        private void AddSongsToUserPlaylist(IEnumerable<PlaylistItem> songs)
        {
            List<PlaylistItem> incoming = songs
                .Where(s => !string.IsNullOrWhiteSpace(s.FilePath))
                .GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (incoming.Count == 0)
            {
                return;
            }

            string? playingPath = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                ? _userPlaylist[_userPlaylistIndex].FilePath
                : null;

            var incomingPaths = new HashSet<string>(
                incoming.Select(s => s.FilePath),
                StringComparer.OrdinalIgnoreCase);

            // 保留未出现在本批次中的旧项（相对顺序不变）
            var kept = new List<PlaylistItem>(_userPlaylist.Count);
            foreach (PlaylistItem existing in _userPlaylist)
            {
                if (!incomingPaths.Contains(existing.FilePath))
                {
                    kept.Add(existing);
                }
            }

            var rebuilt = new List<PlaylistItem>(incoming.Count + kept.Count);
            bool insertBegin = AppSettingsStore.Load().InsertPlaylistAtBegin;
            if (insertBegin)
            {
                foreach (PlaylistItem song in incoming)
                {
                    rebuilt.Add(ClonePlaylistItem(song));
                }

                rebuilt.AddRange(kept);
            }
            else
            {
                rebuilt.AddRange(kept);
                foreach (PlaylistItem song in incoming)
                {
                    rebuilt.Add(ClonePlaylistItem(song));
                }
            }
            for (int i = 0; i < rebuilt.Count; i++)
            {
                rebuilt[i].Index = i + 1;
            }

            // 整表替换，避免 Clear + N 次 Add 触发数千次 UI/集合通知导致卡死崩溃
            bool rebindPlaylistView = ReferenceEquals(PlaylistView.ItemsSource, _userPlaylist);
            if (rebindPlaylistView)
            {
                PlaylistView.ItemsSource = null;
            }

            _userPlaylist = new ObservableCollection<PlaylistItem>(rebuilt);

            if (rebindPlaylistView
                || string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                PlaylistView.ItemsSource = _userPlaylist;
            }

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                _userPlaylistIndex = FindUserPlaylistIndex(playingPath);
            }

            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }


        /// <summary>将歌曲文件移入回收站，并从各列表移除。</summary>
        private async Task DeleteSongFromDiskAsync(PlaylistItem item)
        {
            if (AppSettingsStore.Load().DisableDeleteFromDisk)
            {
                return;
            }

            ContentDialog dialog = new()
            {
                Title = "从磁盘删除",
                Content = $"确定将以下文件移到回收站吗？\n\n{item.FilePath}",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                if (!MoveToRecycleBin(item.FilePath))
                {
                    await ShowErrorAsync("删除失败", "无法将文件移到回收站。");
                    return;
                }

                RemoveSongFromAllCollections(item);
                if (string.Equals(_nowPlayingPath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _nowPlayingPath = null;
                    _currentIndex = -1;
                    try
                    {
                        MediaPlayer? player = GetPlayer();
                        player?.Pause();
                        if (player != null)
                        {
                            player.PlaybackSession.Position = TimeSpan.Zero;
                            player.Source = null;
                        }
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                NotifyCurrentPlaylistWindow();
                NowPlayingText.Text = "已删除：" + item.Title;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("删除失败", ex.Message);
            }
        }


        /// <summary>从当前曲库、用户播放列表与统计记录中移除该歌曲。</summary>
        private void RemoveSongFromAllCollections(PlaylistItem item)
        {
            bool curWasDeleted = _currentIndex >= 0 && _currentIndex < _playlist.Count
                && string.Equals(_playlist[_currentIndex].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase);
            bool userWasDeleted = _userPlaylistIndex >= 0 && _userPlaylistIndex < _userPlaylist.Count
                && string.Equals(_userPlaylist[_userPlaylistIndex].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase);

            for (int i = _playlist.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_playlist[i].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _playlist.RemoveAt(i);
                    if (i < _currentIndex)
                    {
                        _currentIndex--;
                    }
                }
            }

            for (int i = _userPlaylist.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_userPlaylist[i].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _userPlaylist.RemoveAt(i);
                    if (i < _userPlaylistIndex)
                    {
                        _userPlaylistIndex--;
                    }
                }
            }

            if (curWasDeleted)
            {
                _currentIndex = -1;
            }

            if (userWasDeleted)
            {
                _userPlaylistIndex = -1;
            }

            RenumberCollection(_playlist);
            RenumberCollection(_userPlaylist);
            _ = RefreshAlbumViewAsync();
            _ = RefreshArtistViewAsync();
        }


        private static PlaylistItem ClonePlaylistItem(PlaylistItem song)
            => new()
            {
                Title = song.Title,
                Artist = song.Artist,
                AlbumArtist = song.AlbumArtist,
                Album = song.Album,
                Track = song.Track,
                Year = song.Year,
                Genre = song.Genre,
                Duration = song.Duration,
                FilePath = song.FilePath,
                StartTimeSeconds = song.StartTimeSeconds,
                Rating = song.Rating
            };


        /// <summary>
        /// 隐藏系统默认选中底（方角），选中态改由模板内 SongRowChrome / AlbumRowChrome 圆角 Border 绘制。
        /// </summary>
        private void ApplyAccentSelectionResources(FrameworkElement host)
        {
            Brush transparent = new SolidColorBrush(Colors.Transparent);
            Brush accent = ResolveAccentBrush();
            Brush fg = ResolveContrastingForeground(accent);

            string[] backgroundKeys =
            {
                "ListViewItemBackgroundSelected",
                "ListViewItemBackgroundSelectedPointerOver",
                "ListViewItemBackgroundSelectedPressed",
                "ListViewItemBackgroundSelectedDisabled",
                "GridViewItemBackgroundSelected",
                "GridViewItemBackgroundSelectedPointerOver",
                "GridViewItemBackgroundSelectedPressed",
                "GridViewItemBackgroundSelectedDisabled"
            };

            string[] foregroundKeys =
            {
                "ListViewItemForegroundSelected",
                "ListViewItemForegroundSelectedPointerOver",
                "ListViewItemForegroundSelectedPressed",
                "GridViewItemForegroundSelected",
                "GridViewItemForegroundSelectedPointerOver",
                "GridViewItemForegroundSelectedPressed"
            };

            foreach (string key in backgroundKeys)
            {
                host.Resources[key] = transparent;
            }

            foreach (string key in foregroundKeys)
            {
                host.Resources[key] = fg;
            }

            // 关闭系统选中勾（多选时默认会出现在词条左侧）
            host.Resources["ListViewItemSelectionCheckMarkVisualEnabled"] = false;
            host.Resources["GridViewItemSelectionCheckMarkVisualEnabled"] = false;
        }


        /// <summary>
        /// 歌曲列表表头五词条：与「播放所有专辑」同色半透明底，操场形圆角（高 32 / 半径 16）。
        /// </summary>
        private void ApplyPlaylistHeaderChipStyle()
        {
            Brush fill = ResolveCapsuleFillBrush();
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

                chip.Height = 32;
                chip.MinHeight = 32;
                chip.CornerRadius = new CornerRadius(16);
                chip.Background = fill;
                chip.BorderThickness = new Thickness(0);
            }
        }


        /// <summary>
        /// 关闭系统默认选中底（方角），选中态改由模板内 SongRowChrome / AlbumRowChrome 圆角 Border 绘制。
        /// 多选状态下内容铺满由 ListViewItem 覆盖模板的 ContentPresenter=Stretch 保证。
        /// </summary>
        private void ApplyMultiSelectItemStyle(ListView target)
        {
            ApplyAccentSelectionResources(target);

            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListViewItem.MinHeightProperty, 40.0));
            style.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(ListViewItem.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0, 2, 0, 2)));
            style.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            target.ItemContainerStyle = style;
            RefreshAllSongListSelectionChrome();
        }


        private void PlaylistView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshPlaylistSelectionChrome();
            UpdateSelectAllMultiSelectButtonState();

            // 未播放时:预览选中歌曲的波形(波形进度条模式)
            if (string.IsNullOrEmpty(_nowPlayingPath)
                && _progressBarStyle == "Waveform"
                && e.AddedItems.Count > 0
                && e.AddedItems[0] is PlaylistItem selItem
                && !string.Equals(_waveformPath, selItem.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadWaveformForCurrentAsync(selItem.FilePath);
            }
        }


        private void PlaylistView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplySongListItemSelectionChrome(PlaylistView, container, song);
                if (args.InRecycleQueue)
                {
                    return;
                }

                // 行模板尚未实现时（Phase 0）触发小封面异步加载
                if (args.Phase == 0)
                {
                    LoadRowCoverAsync(PlaylistView, container, song);
                }
            }
        }


        /// <summary>异步读取歌曲小封面并填到行模板内的 RowCoverImage；去重 + 封面解码缓存 + 并发限流 + 行已回收则跳过。</summary>
        private async void LoadRowCoverAsync(ListView owner, ListViewItem container, PlaylistItem song)
        {
            if (string.IsNullOrWhiteSpace(song.FilePath))
            {
                return;
            }

            if (!_rowCoverLoading.Add(song.FilePath))
            {
                return;
            }

            try
            {
                // 命中已解码缓存：直接填行（不再 IO/解码，滚动丝滑）
                if (_coverImageCache.TryGetValue(song.FilePath, out var cached))
                {
                    DispatcherQueue.TryEnqueue(() => AttachCover(owner, container, song, cached));
                    return;
                }

                byte[]? bytes = null;
                await _coverLoadGate.WaitAsync();
                try
                {
                    bytes = await System.Threading.Tasks.Task.Run(() => ExtractCoverBytes(song.FilePath));
                }
                finally
                {
                    _coverLoadGate.Release();
                }

                if (bytes is not { Length: > 0 })
                {
                    return;
                }

                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    ms.Position = 0;
                    await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                }

                // 缓存解码结果供重复使用
                _coverImageCache.TryAdd(song.FilePath, bmp);
                DispatcherQueue.TryEnqueue(() => AttachCover(owner, container, song, bmp));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            finally
            {
                _rowCoverLoading.Remove(song.FilePath);
            }
        }


        /// <summary>仅当该行仍由同一容器承载时，把封面填到其 RowCoverImage（避免行复用错位）。</summary>
        private static void AttachCover(ListView owner, ListViewItem container, PlaylistItem song, Microsoft.UI.Xaml.Media.Imaging.BitmapImage bmp)
        {
            try
            {
                if (owner.ContainerFromItem(song) != container)
                {
                    return;
                }

                var img = container.ContentTemplateRoot as FrameworkElement;
                var coverImg = img?.FindName("RowCoverImage") as Microsoft.UI.Xaml.Controls.Image;
                if (coverImg != null)
                {
                    coverImg.Source = bmp;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void RefreshMultiSelectItemBackgrounds() => RefreshAllSongListSelectionChrome();

        private void RefreshAllSongListSelectionChrome()
        {
            RefreshPlaylistSelectionChrome();
            RefreshArtistTrackSelectionChrome();
            RefreshAlbumTrackSelectionChrome();
        }


        private void RefreshSongListSelectionChrome(ListView list)
        {
            if (ReferenceEquals(list, ArtistTrackListView))
            {
                RefreshArtistTrackSelectionChrome();
            }
            else if (ReferenceEquals(list, AlbumTrackListView))
            {
                RefreshAlbumTrackSelectionChrome();
            }
            else
            {
                RefreshPlaylistSelectionChrome();
            }
        }


        private void RefreshAlbumTrackSelectionChrome()
            => RefreshRealizedSongListSelectionChrome(AlbumTrackListView);

        /// <summary>
        /// 歌曲列表 / 多选：选中为圆角主题色矩形（画在 SongRowChrome 上）；
        /// 多选未选中为浅霜色底；再次点击取消选择时恢复常态。
        /// 仅刷新已实现容器，避免对全库做 ContainerFromItem。
        /// </summary>
        private void RefreshPlaylistSelectionChrome()
            => RefreshRealizedSongListSelectionChrome(PlaylistView);

        private void RefreshArtistTrackSelectionChrome()
            => RefreshRealizedSongListSelectionChrome(ArtistTrackListView);

        /// <summary>
        /// 列表宽度变化时，重刷已实现行的宽度，使行内容始终铺满（选中矩形铺满整行、字段对齐）。
        /// </summary>
        private void SongListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ListView list && list.ActualWidth > 0)
            {
                RefreshRealizedSongListSelectionChrome(list);
            }
        }
    }
}
