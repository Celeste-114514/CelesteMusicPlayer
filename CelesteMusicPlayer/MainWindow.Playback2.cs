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
            if (track.SourceTrackId.Length > 0)
            {
                NetworkLibraryStore.Pin(track.SourceTrackId);
                int sourceIndex = _queue.Items.ToList().FindIndex(item => item.SourceTrackId == track.SourceTrackId);
                if (sourceIndex < 0)
                {
                    AddSongsToUserPlaylist(new[] { track });
                    sourceIndex = _queue.Items.ToList().FindIndex(item => item.SourceTrackId == track.SourceTrackId);
                }
                if (sourceIndex >= 0) PlayUserPlaylistAt(sourceIndex); else StartPlayback(track);
                return;
            }
            // 若已在该播放队列中：保持原顺序，仅定位到它播放（不再把它移到最前）。
            // CUE 分轨：同一文件有多轨，按「路径+起点」精确匹配，否则点第 3 轨会播第 1 轨。
            bool byStart = track.IsCueSegment || track.StartTimeSeconds > 0;
            int existing = FindUserPlaylistIndex(track.FilePath, track.StartTimeSeconds, byStart);
            if (existing >= 0)
            {
                PlayUserPlaylistAt(existing);
                return;
            }

            // 不在播放队列：加入队列（按现有 InsertPlaylistAtBegin 设置决定位置，通常放最前）再播放。
            AddSongsToUserPlaylist(new[] { track });
            int userIndex = FindUserPlaylistIndex(track.FilePath, track.StartTimeSeconds, byStart);
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
                _queue.Index = -1;
            }
        }

        private int FindUserPlaylistIndex(string filePath)
            => FindUserPlaylistIndex(filePath, 0, matchStart: false);

        /// <summary>
        /// 按路径（+分轨起点）找播放队列下标。
        /// matchStart 为 true 时要求 CUE 分轨起点也匹配 —— 同一文件的多轨路径相同，
        /// 只按路径找会永远命中第一轨（点第 3 轨却播第 1 轨）。
        /// </summary>
        private int FindUserPlaylistIndex(string filePath, double wantStart, bool matchStart)
        {
            for (int i = 0; i < _queue.Items.Count; i++)
            {
                if (!string.Equals(_queue.Items[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase) && !string.Equals(_queue.Items[i].PersistenceKey, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!matchStart || Math.Abs(_queue.Items[i].StartTimeSeconds - wantStart) < 0.5)
                {
                    return i;
                }
            }

            return -1;
        }

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

            ColorHelper.ApplyDialogAccent(dialog);
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

            string? playingPath = _queue.Index >= 0 && _queue.Index < _queue.Items.Count
                ? _queue.Items[_queue.Index].FilePath
                : null;

            SortCollection(_queue.Items, playingPath);
            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

        private ObservableCollection<PlaylistItem> GetActiveSongCollection()
            => string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal)
                ? _queue.Items
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

            // 单趟排序：用 List.Sort + 专用比较器，一次比较即完成「主字段 → 标题」排序与升/降序，
            // 避免 OrderBy/ThenBy/Reverse/ToList 的多趟中间分配与多次比较（大列表卡顿优化）。
            List<PlaylistItem> sorted = new List<PlaylistItem>(target.Count);
            sorted.AddRange(target);
            sorted.Sort(new PlaylistItemSortComparer(_sortField, _sortAscending));

            // 整表替换而非 Clear + N 次 Add：先解绑 ItemsSource，让下面的清空/填充不再逐条驱动 UI
            // 虚拟化；原地重建内容后重新绑定，只触发一次整表渲染（复用项目已有模式 Playback2.cs:1897）。
            // 注意 _playlist 是 readonly 字段不能重新赋值，故主列表走“解绑→原地清空/填充→重绑”路径；
            // _queue.Items 非 readonly，可直接换新集合。
            bool isUser = ReferenceEquals(target, _queue.Items);
            bool rebind = ReferenceEquals(_playlistViewSource, target);

            if (rebind) _playlistViewSource = null;

            ObservableCollection<PlaylistItem> effective;
            if (isUser)
            {
                _queue.Replace(sorted);
                effective = _queue.Items;
            }
            else
            {
                target.Clear();
                foreach (PlaylistItem item in sorted) target.Add(item);
                effective = target;
            }

            if (rebind || (isUser && _currentCategory == "UserPlaylist"))
            {
                _playlistViewSource = effective;
            }

            RenumberCollection(effective);
            SyncIndicesAfterSort(effective, preservePlayingPath);
        }

        private void SyncIndicesAfterSort(ObservableCollection<PlaylistItem> target, string? preservePlayingPath)
        {
            if (ReferenceEquals(target, _playlist))
            {
                UpdateCurrentIndexByPath(preservePlayingPath);
            }
            else if (ReferenceEquals(target, _queue.Items) && !string.IsNullOrEmpty(preservePlayingPath))
            {
                _queue.Index = FindUserPlaylistIndex(preservePlayingPath);
            }
        }

        /// <summary>排序专用比较器：单趟完成「主字段 → 标题」比较与升/降序，
        /// 替代 OrderBy/ThenBy/Reverse 的多趟分配（大列表卡顿优化）。</summary>
        private sealed class PlaylistItemSortComparer : IComparer<PlaylistItem>
        {
            private static readonly StringComparer Ignore = StringComparer.OrdinalIgnoreCase;
            private readonly SortField _field;
            private readonly bool _ascending;

            public PlaylistItemSortComparer(SortField field, bool ascending)
            {
                _field = field;
                _ascending = ascending;
            }

            public int Compare(PlaylistItem? x, PlaylistItem? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x is null) return -1;
                if (y is null) return 1;

                int c = _field switch
                {
                    SortField.Artist => Ignore.Compare(x.Artist, y.Artist),
                    SortField.Album => Ignore.Compare(x.Album, y.Album),
                    SortField.Year => x.Year.CompareTo(y.Year),
                    SortField.Duration => x.Duration.CompareTo(y.Duration),
                    SortField.Genre => Ignore.Compare(x.Genre, y.Genre),
                    SortField.Track => x.Track.CompareTo(y.Track),
                    SortField.FilePath => Ignore.Compare(x.FilePath, y.FilePath),
                    _ => Ignore.Compare(x.Title, y.Title)
                };
                // 次要关键字：标题，同字段时顺序更稳定
                if (c == 0) c = Ignore.Compare(x.Title, y.Title);
                return _ascending ? c : -c;
            }
        }

        private static void RenumberCollection(ObservableCollection<PlaylistItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Index = i + 1;
            }
        }

        /// <summary>将歌曲移到用户播放列表最前（已在最前则不变）。</summary>
        internal void PinSongToUserPlaylistTop(PlaylistItem song)
        {
            int index = FindUserPlaylistIndex(song.FilePath);
            if (index <= 0)
            {
                return;
            }

            string? playingPath = _queue.Index >= 0 && _queue.Index < _queue.Items.Count
                ? _queue.Items[_queue.Index].FilePath
                : null;

            PlaylistItem item = _queue.Items[index];
            _queue.Items.RemoveAt(index);
            _queue.Items.Insert(0, item);
            RenumberCollection(_queue.Items);

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                _queue.Index = FindUserPlaylistIndex(playingPath);
            }

            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

        internal void ShowCurrentPlaylistWindow()
        {
            ShowWebPage("queue");
        }

        /// <summary>打开播放队列窗口（复用主播放列表，聚焦接下来的待播曲目）。</summary>
        internal void ShowPlayQueueWindow()
        {
            ShowWebPage("queue");
        }

        internal void NotifyCurrentPlaylistWindow()
            => NotifyWebHomeChanged();

        internal ObservableCollection<PlaylistItem> UserPlaylist => _queue.Items;

        /// <summary>媒体库全部歌曲（供曲库健康等面板读取）。</summary>
        internal IReadOnlyList<PlaylistItem> LibraryTracks => _playlist;

        internal int UserPlaylistPlayingIndex => _queue.Index;

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

            // 同步清掉「手动加入音乐库」的记录，否则重启后这些被移除的歌又会被补回来
            RemoveFromManualLibraryFiles(dead);

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
            if (_queue.Items.Count == 0)
            {
                return;
            }

            PlayUserPlaylistAt(0);
            NotifyCurrentPlaylistWindow();
        }

        private void RemoveSongsFromUserPlaylist(IEnumerable<PlaylistItem> songs)
        {
            var paths = new HashSet<string>(
                songs.Select(s => s.FilePath),
                StringComparer.OrdinalIgnoreCase);

            for (int i = _queue.Items.Count - 1; i >= 0; i--)
            {
                if (paths.Contains(_queue.Items[i].FilePath))
                {
                    _queue.Items.RemoveAt(i);
                    if (i < _queue.Index)
                    {
                        _queue.Index--;
                    }
                }
            }

            if (_queue.Index >= _queue.Items.Count)
            {
                _queue.Index = _queue.Items.Count - 1;
            }

            RenumberCollection(_queue.Items);
            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

        internal async Task SaveUserPlaylistAsync(XamlRoot xamlRoot)
        {
            try
            {
                if (_queue.Items.Count == 0)
                {
                    await ShowErrorAsync("保存播放列表", "当前播放列表为空。");
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
                    Songs = _queue.Items.Select(s => new UserPlaylistSongDto
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
                await ShowErrorAsync("保存播放列表失败", ex.Message);
            }
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
                    await ShowErrorAsync("打开播放列表失败", "文件格式无效。");
                    return;
                }

                ApplyLoadedUserPlaylist(dto, navigateToPlaylist);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("打开播放列表失败", ex.Message);
            }
        }

        internal async Task ClearUserPlaylistAsync(XamlRoot xamlRoot)
        {
            if (_queue.Items.Count == 0)
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

            ColorHelper.ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _queue.Items.Clear();
            _queue.Index = -1;
            PlayQueueStore.Clear();
            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

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

            ColorHelper.ApplyDialogAccent(dialog);
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
                await ShowErrorAsync("打开播放列表", "PlayList 文件夹中还没有已保存的播放列表。");
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

            ColorHelper.ApplyDialogAccent(dialog);
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
            _queue.Items.Clear();
            foreach (UserPlaylistSongDto song in dto.Songs)
            {
                if (string.IsNullOrWhiteSpace(song.FilePath) || !System.IO.File.Exists(song.FilePath))
                {
                    continue;
                }

                if (_queue.Items.Any(p =>
                        string.Equals(p.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                PlaylistItem? fromLibrary = _playlist.FirstOrDefault(p =>
                    string.Equals(p.FilePath, song.FilePath, StringComparison.OrdinalIgnoreCase));

                if (fromLibrary != null)
                {
                    _queue.Items.Add(ClonePlaylistItem(fromLibrary));
                    continue;
                }

                TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, song.DurationSeconds));
                _queue.Items.Add(new PlaylistItem
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

            RenumberCollection(_queue.Items);
            NotifyCurrentPlaylistWindow();

            if (!navigateToPlaylist)
            {
                return;
            }

            CommitLibraryNavigation(() =>
            {
                _currentCategory = "UserPlaylist";
                ApplyCategoryViewData();
            });
        }

        /// <summary>
        /// 按路径弹“添加到播放列表”选择器：选命名单或新建，把路径追加进去。
        /// 命名单只存路径，不碰音乐库，所以库外的歌也能进（标签面板「排序编号」页走这条）。
        /// </summary>
        internal async System.Threading.Tasks.Task AddPathsToNamedPlaylistAsync(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0) return;

            var distinct = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct.Count == 0) return;

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

            ColorHelper.ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            string? chosen = (panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string)?.Trim();
            string? typed = newNameBox.Text?.Trim();
            string? target = string.IsNullOrEmpty(typed) ? chosen : typed;
            if (string.IsNullOrEmpty(target)) return;

            var merged = NamedPlaylistStore
                .LoadSongs(target)
                .Concat(distinct)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            NamedPlaylistStore.SaveSongs(target, merged);
            PlaylistLibraryService.Refresh();
            StartupLog.Write("已添加到播放列表: " + target + " (+" + distinct.Count + " 首)");
        }

        private void AddSongsToUserPlaylist(IEnumerable<PlaylistItem> songs)
        {
            List<PlaylistItem> incoming = songs
                .Where(s => !string.IsNullOrWhiteSpace(s.FilePath))
                // 分组键带 CUE 分轨起点：同一文件的多轨路径相同，只按路径去重会折叠成一条
                .GroupBy(WebTrackIdentity.Of, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (incoming.Count == 0)
            {
                return;
            }

            string? playingPath = _queue.Index >= 0 && _queue.Index < _queue.Items.Count
                ? WebTrackIdentity.Of(_queue.Items[_queue.Index])
                : null;

            var incomingPaths = new HashSet<string>(
                incoming.Select(WebTrackIdentity.Of),
                StringComparer.OrdinalIgnoreCase);

            // 保留未出现在本批次中的旧项（相对顺序不变）
            var kept = new List<PlaylistItem>(_queue.Items.Count);
            foreach (PlaylistItem existing in _queue.Items)
            {
                if (!incomingPaths.Contains(WebTrackIdentity.Of(existing)))
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
            bool rebindPlaylistView = ReferenceEquals(_playlistViewSource, _queue.Items);
            if (rebindPlaylistView)
            {
                _playlistViewSource = null;
            }

            _queue.Replace(rebuilt);

            if (rebindPlaylistView
                || string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                _playlistViewSource = _queue.Items;
            }

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                _queue.Index = _queue.Items.ToList().FindIndex(item => WebTrackIdentity.Of(item) == playingPath);
            }

            NotifyCurrentPlaylistWindow();
            if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                ApplyUserPlaylistSearchFilter();
            }
        }

        /// <summary>将歌曲文件移入回收站，并从各列表移除。</summary>
        private async Task DeleteSongFromDiskAsync(PlaylistItem item, XamlRoot? hostRoot = null)
        {
            if (AppSettingsStore.Load().DisableDeleteFromDisk)
            {
                return;
            }

            if (_webShellWindow != null)
            {
                if (System.Windows.Forms.MessageBox.Show(_webShellWindow, $"确定将以下文件移到回收站吗？\n\n{item.FilePath}", "从磁盘删除", System.Windows.Forms.MessageBoxButtons.OKCancel, System.Windows.Forms.MessageBoxIcon.Warning, System.Windows.Forms.MessageBoxDefaultButton.Button2) != System.Windows.Forms.DialogResult.OK) return;
            }
            else
            {
            ContentDialog dialog = new()
            {
                Title = "从磁盘删除",
                Content = $"确定将以下文件移到回收站吗？\n\n{item.FilePath}",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = hostRoot ?? Content?.XamlRoot
            };

            ColorHelper.ApplyDialogAccent(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

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
                    _currentPlayingItem = null;
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
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("删除失败", ex.Message);
            }
        }

        /// <summary>从当前曲库、用户播放列表与统计记录中移除该歌曲。</summary>
        private void RemoveSongFromAllCollections(PlaylistItem item)
        {
            // 手动加入音乐库的歌被移除时一并清掉记录，避免重启后被补回来
            RemoveFromManualLibraryFiles(new[] { item.FilePath });

            bool curWasDeleted = _currentIndex >= 0 && _currentIndex < _playlist.Count
                && string.Equals(_playlist[_currentIndex].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase);
            bool userWasDeleted = _queue.Index >= 0 && _queue.Index < _queue.Items.Count
                && string.Equals(_queue.Items[_queue.Index].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase);

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

            for (int i = _queue.Items.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_queue.Items[i].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    _queue.Items.RemoveAt(i);
                    if (i < _queue.Index)
                    {
                        _queue.Index--;
                    }
                }
            }

            if (curWasDeleted)
            {
                _currentIndex = -1;
            }

            if (userWasDeleted)
            {
                _queue.Index = -1;
            }

            RenumberCollection(_playlist);
            RenumberCollection(_queue.Items);
            _ = RefreshAlbumViewAsync();
            _ = RefreshArtistViewAsync();
        }

        private static PlaylistItem ClonePlaylistItem(PlaylistItem song) => song.Clone();

        // 多选范围选择挂接（2026-10-31 从 MainWindow.Misc.cs 搬来）：多选域随 Playback2。

        /// <summary>给列表挂上 Shift 连选一段 / Ctrl 跳选（转发到共用实现 RangeMultiSelect）。</summary>
        internal static void AttachRangeMultiSelect(ListViewBase? list)
            => RangeMultiSelect.Attach(list);
    }
}
