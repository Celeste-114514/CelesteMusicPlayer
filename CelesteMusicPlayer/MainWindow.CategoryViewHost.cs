using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Color = Windows.UI.Color;
using System.Linq;
using System.Collections.ObjectModel;

namespace CelesteMusicPlayer;

/// <summary>W13 第三十四刀（2026-10-31）：分类视图：中间区按分类切换、收藏/最近/最常听/评分视图、空列表提示、导航胶囊取色。</summary>
public sealed partial class MainWindow
{
        private bool _leftNavCollapsed;

        private HorizontalAlignment NavContentAlignment
            => _leftNavCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        // W13 第三十四刀：分类视图

        /// <summary>根据当前左侧分类刷新中间区域</summary>
        private void ApplyCategoryView()
        {
            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;

            UpdateLibraryNavHighlight();

            switch (_currentCategory)
            {
                case "Songs":
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    PlaylistView.ItemsSource = _playlist;

                    _sortField = SortField.Title;
                    _sortAscending = true;
                    ApplySort(playingPath);
                    break;

                case "UserPlaylist":
                    SetSongSortUiForCategory(isUserPlaylist: true);
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    PlaylistView.ItemsSource = _queue.Items;
                    // 播放列表默认不排序：保持添加顺序（后添加批次在前，批内相对顺序不变）
                    RenumberCollection(_queue.Items);
                    ScrollUserPlaylistToPlaying();
                    break;

                case "Albums":
                    UpdateAlbumSortButtonsUi();
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    _ = RefreshAlbumViewAsync();
                    break;

                case "Artists":
                case "AlbumArtists":
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    _ = RefreshArtistViewAsync();
                    break;

                case "Folders":
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    SetFolderBrowserMode(isWebDav: false);
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    RefreshFolderBrowserRoots();
                    break;

                case "WebDav":
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    // 同一个「文件夹」界面复用给网络库：台头换名字、藏掉「添加文件夹」（网络库不是加本地目录）
                    SetFolderBrowserMode(isWebDav: true);
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    RefreshWebDavBrowserRoots();
                    break;

                case "PlaylistWall":
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    if (string.IsNullOrEmpty(_currentPlaylistDetail))
                    {
                        ApplyPlaylistWallCategory();
                    }
                    else
                    {
                    }

                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    break;

                case "Favorites":
                case "Recent":
                    ApplyFavoritesOrRecentCategory();
                    break;

                case "Ratings":
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    ApplyRatingCategory();
                    break;

                case "MostPlayed":
                    ApplyMostPlayedCategory();
                    break;

                case "Genres":
                case "Years":
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    _ = RefreshGenreYearViewAsync();
                    break;

                case "GenreSongs":
                case "YearSongs":
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    var groupedSongs = _currentCategory == "GenreSongs"
                        ? _playlist
                            .Where(t => string.Equals(t.Genre, _genreYearFilter, StringComparison.OrdinalIgnoreCase)
                                        || (string.IsNullOrWhiteSpace(t.Genre) && _genreYearFilter == "未知流派"))
                            .ToList()
                        : _playlist
                            .Where(t => (t.Year > 0 ? t.Year.ToString() : "未知年份") == _genreYearFilter)
                            .ToList();
                    var groupedCollection = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>(groupedSongs);
                    RenumberCollection(groupedCollection);
                    PlaylistView.ItemsSource = groupedCollection;
                    break;

                case "AudioFX":
                    // 音效处理 DSP 工作台（EQ / 声道平衡 / 安全限幅）
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    EnsureAudioFxUiBuilt();
                    LoadAudioFxUiFromStore();
                    UpdateDspBitPerfectUi();
                    break;

                case "Settings":
                    // 设置面板（2026-10-12 阶段 0 骨架）：版式与音效处理一致，分类导航由通用子页框架生成
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    EnsureSettingsUiBuilt();
                    break;

                case "TagEdit":
                    // 标签板块（2026-10-13 阶段 5a）：左标签面板 + 右文件列表，子页导航在面板内部
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    EnsureTagEditUiBuilt();
                    break;
            }

            // 仅「播放队列」分类支持拖拽重排（其它列表保持排序语义不变）
            PlaylistView.CanReorderItems = ReferenceEquals(PlaylistView.ItemsSource, _queue.Items);

            // 空状态提示：仅 Favorites / Recent / Ratings 自己管理；其余分类隐藏
            if (_currentCategory is not ("Favorites" or "Recent" or "Ratings"))
            {
                SetPlaylistEmptyHint(false, string.Empty);
            }

            UpdateUserPlaylistActionBarVisibility();
            UpdateLibrarySearchUi();
        }

        /// <summary>
        /// 切到「播放队列」页面时，把列表滚到正在播放的那首歌并高亮它。
        /// ItemsSource 刚赋值时 ListView 还没完成布局，ScrollIntoView 会静默失效，
        /// 所以用 DispatcherQueue 延迟到布局完成后执行；无正在播放的曲目则不滚动。
        /// </summary>
        private void ScrollUserPlaylistToPlaying()
        {
            if (PlaylistView == null || _queue.Items == null || _queue.Items.Count == 0)
            {
                return;
            }

            int index = _queue.Index;
            if (index < 0 || index >= _queue.Items.Count)
            {
                return;
            }

            PlaylistItem playing = _queue.Items[index];
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
                {
                    return;
                }

                PlaylistView.ScrollIntoView(playing);
            });
        }

        private async Task RefreshGenreYearViewAsync()
        {
            List<ArtistEntry> entries = await Task.Run(() =>
            {
                if (_currentCategory == "Genres")
                {
                    return _playlist
                        .GroupBy(t => string.IsNullOrWhiteSpace(t.Genre) || t.Genre == "未知流派" ? "未知流派" : t.Genre)
                        .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                        .Select(g => new ArtistEntry { Name = g.Key, TrackCount = g.Count() })
                        .ToList();
                }

                return _playlist
                    .GroupBy(t => t.Year > 0 ? t.Year.ToString() : "未知年份")
                    .OrderByDescending(g => g.Key == "未知年份"
                        ? -1
                        : int.TryParse(g.Key, out int year) ? year : -1)
                    .Select(g => new ArtistEntry { Name = g.Key, TrackCount = g.Count() })
                    .ToList();
            });

            if (_currentCategory is "Genres" or "Years")
            {
                ArtistGridView.ItemsSource = entries;
                RefreshPlaylistSelectionChrome();
            }
        }

        private Brush ResolveNavCapsuleBorderBrush()
        {
            // 经典界面：Application.Resources 里的这些键取到的是"系统主题"那一份色值，
            // 与经典模式选的浅色/深色不一定一致（跟随系统时更明显），所以这里直接按经典主题给色。
            if (FrostedGlass.ClassicMode != null)
            {
                return new SolidColorBrush(FrostedGlass.ClassicMode == "Light"
                    ? Color.FromArgb(28, 0, 0, 0)
                    : Color.FromArgb(46, 255, 255, 255));
            }

            if (Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? card)
                && card is Brush c)
            {
                return c;
            }

            return new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        }

        // W13 第三十四刀：收藏/最常听/评分视图

        private PlaylistItem? FindLibraryItemByPath(string path)
        {
            int index = FindLibraryIndex(path);
            return index >= 0 ? _playlist[index] : null;
        }

        private void ApplyFavoritesOrRecentCategory()
        {
            SetSongSortUiForCategory(isUserPlaylist: false);
            PlaylistListBorder.Visibility = Visibility.Visible;
            CloseAlbumDetailUi();
            CloseArtistDetailUi();

            var items = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                foreach (string path in TrackStatsStore.GetAllFavorites())
                {
                    PlaylistItem? fromLib = FindLibraryItemByPath(path);
                    if (fromLib != null)
                    {
                        items.Add(ClonePlaylistItem(fromLib));
                    }
                    else if (System.IO.File.Exists(path))
                    {
                        try
                        {
                            items.Add(CreatePlaylistItemFromPath(path));
                        }
                        catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
                    }
                }
            }
            else
            {
                // 最近播放 = 播放历史事件流水（每次播放一条记录，含播放时间/时长/是否播完）。
                // 与 LibraryDb 记录点（切歌/播完）对应；双击等交互复用歌曲行通用逻辑。
                // 「最近播放范围」设置：0=全部；N=只显示最近 N 天（本地时区换算成 UTC 比较）。
                int recentRangeDays = AppSettingsStore.Load().RecentPlayedRangeDays;
                DateTime rangeFromUtc = recentRangeDays > 0
                    ? DateTime.UtcNow.AddDays(-recentRangeDays)
                    : DateTime.MinValue;
                foreach (LibraryDb.PlaybackHistoryEntry e in LibraryDb.LoadPlaybackHistory(200))
                {
                    if (e.PlayedAtUtc < rangeFromUtc)
                    {
                        continue; // 早于范围起点，跳过
                    }

                    PlaylistItem? item = null;
                    if (System.IO.File.Exists(e.FilePath))
                    {
                        PlaylistItem? fromLib = FindLibraryItemByPath(e.FilePath);
                        if (fromLib != null)
                        {
                            item = ClonePlaylistItem(fromLib);
                        }
                        else
                        {
                            try { item = CreatePlaylistItemFromPath(e.FilePath); }
                            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
                        }
                    }

                    item ??= new PlaylistItem { FilePath = e.FilePath, Title = e.Title };
                    if (!string.IsNullOrWhiteSpace(e.Title)
                        && string.Equals(item.Title, System.IO.Path.GetFileNameWithoutExtension(e.FilePath), StringComparison.OrdinalIgnoreCase))
                    {
                        item.Title = e.Title; // 无内嵌标题时用历史记录的标题
                    }

                    // 第二行显示播放信息："播放于 MM-dd HH:mm - 播放 m:ss · 播完/未播完"
                    DateTime local = e.PlayedAtUtc == DateTime.MinValue
                        ? DateTime.MinValue
                        : e.PlayedAtUtc.ToLocalTime();
                    string timeText = local == DateTime.MinValue ? "—" : local.ToString("MM-dd HH:mm");
                    string durText = e.PlayedSeconds < 1
                        ? "—"
                        : e.PlayedSeconds < 60
                            ? (int)e.PlayedSeconds + " 秒"
                            : TimeSpan.FromSeconds(e.PlayedSeconds).ToString(@"m\:ss");
                    item.Artist = "播放于 " + timeText;
                    item.Album = "播放 " + durText + " · " + (e.Completed ? "播完" : "未播完");
                    items.Add(item);
                }
            }

            RenumberCollection(items);
            PlaylistView.ItemsSource = items;
            SetPlaylistEmptyHint(items.Count == 0,
                string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal) ? "暂时没有添加我喜欢的音乐" : "最近没有播放记录");
        }

        /// <summary>歌曲列表空状态提示：空时显示 hint，否则收起。</summary>
        private void SetPlaylistEmptyHint(bool empty, string hint)
        {
            if (PlaylistEmptyHint == null) return;
        }

        /// <summary>播放最多：按播放次数降序显示媒体库歌曲。</summary>
        private void ApplyMostPlayedCategory()
        {
            SetSongSortUiForCategory(isUserPlaylist: false);
            PlaylistListBorder.Visibility = Visibility.Visible;
            CloseAlbumDetailUi();
            CloseArtistDetailUi();

            var col = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (PlaylistItem track in _playlist)
            {
                int count = TrackStatsStore.Get(track.FilePath)?.PlayCount ?? 0;
                if (count > 0)
                {
                    col.Add(ClonePlaylistItem(track));
                }
            }

            var sorted = col
                .Select(x => new { Item = x, Count = TrackStatsStore.Get(x.FilePath)?.PlayCount ?? 0 })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Item)
                .ToList();

            var result = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (PlaylistItem item in sorted)
            {
                result.Add(item);
            }

            RenumberCollection(result);
            PlaylistView.ItemsSource = result;
        }

        /// <summary>刷新评分分类：按 _ratingFilter 过滤媒体库歌曲并填充列表。</summary>
        private void ApplyRatingCategory()
        {
            UpdateRatingFilterHighlight();

            var col = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>();
            foreach (PlaylistItem track in _playlist)
            {
                if (_ratingFilter < 0)
                {
                    // 未点选：显示所有已评分歌曲
                    if (track.Rating > 0) col.Add(ClonePlaylistItem(track));
                }
                else if (track.Rating == _ratingFilter)
                {
                    col.Add(ClonePlaylistItem(track));
                }
            }

            RenumberCollection(col);
            PlaylistView.ItemsSource = col;
            SetPlaylistEmptyHint(col.Count == 0, _ratingFilter >= 0
                ? (_ratingFilter == 0 ? "没有未评分的歌曲" : "没有 " + _ratingFilter + " 星评分的歌曲")
                : "还没有给任何歌曲评分");
        }

        private void UpdateRatingFilterHighlight()
        {
            var accent = ResolveAccentBrush();
            var fg = ColorHelper.ResolveContrastingForeground(accent);
            var idleBg = ResolveCapsuleFillBrush();
            var border = ResolveNavCapsuleBorderBrush();
            (Button Btn, int Val)[] maps =
            {
                (RatingFilter0Button, 0), (RatingFilter1Button, 1), (RatingFilter2Button, 2),
                (RatingFilter3Button, 3), (RatingFilter4Button, 4), (RatingFilter5Button, 5)
            };
            foreach (var (btn, val) in maps)
            {
                bool active = val == _ratingFilter;
                if (active) { btn.Background = accent; btn.Foreground = fg; btn.BorderThickness = new Thickness(0); }
                else { btn.Background = idleBg; btn.ClearValue(Control.ForegroundProperty); btn.BorderThickness = new Thickness(1); btn.BorderBrush = border; }
            }
        }
    }
