using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十九刀（2026-10-31）：曲库视图刷新 + 搜索过滤宿主。
/// 从壳 <c>MainWindow.Library2.cs</c> / <c>Library.cs</c> / <c>EqDsp.cs</c> / <c>Misc.cs</c>
/// 搬出的「刷新视图 / 搜索过滤 / 专辑详情入口 / 艺术家头像解析」域：
/// 专辑墙与艺术家列表重建、专辑排序、墙选中框重刷、搜索框 UI 与过滤、按标题/专辑/艺术家筛选、
/// 专辑详情打开（含其本体）、艺术家头像三级回落（本地存档 → 内置默认 → 网络）。
/// 依赖（曲库集合、当前分类、选中框样式、封面缓存）都在活文件里。
/// </summary>
public sealed partial class MainWindow
{
    // 艺术家网络头像缓存：同一艺术家只拉一次；信号量保证并发只跑一个请求。
    private static readonly Dictionary<string, BitmapImage?> WebArtistAvatarCache = new();
    private static readonly SemaphoreSlim WebArtistAvatarGate = new(1, 1);

        // W13 第二十九刀：专辑墙刷新 / 排序 / 详情入口

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
                    }
                }
            }
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

        // W13 第二十九刀：搜索 UI / 艺术家列表 / 头像解析

        private void UpdateLibrarySearchUi()
        {

            bool show = !_isMultiSelectMode
                && _openedAlbum == null
                && _openedArtist == null
                && (_currentCategory is "Songs" or "Albums" or "Artists" or "AlbumArtists" or "Folders" or "UserPlaylist" or "Favorites" or "Recent" or "Ratings");

            if (!show)
            {

                if (_currentCategory != "Folders")
                {
                    ClearFolderSearchHighlightOnly();
                }

                return;
            }

            // 分类切换时刷新搜索框占位文案（原 SetLibrarySearchBasePrompt 随极客皮肤移除，
            // 经典路径就是直接写 PlaceholderText）。
            ApplyLibrarySearchNow();
        }

        private void ApplyLibrarySearchNow()
        {
            switch (_currentCategory)
            {
                case "Songs":
                    ApplySongsSearchFilter();
                    ClearFolderSearchHighlightOnly();
                    break;
                case "Albums":
                    ApplyAlbumsSearchFilter();
                    ClearFolderSearchHighlightOnly();
                    break;
                case "Artists":
                case "AlbumArtists":
                    ApplyArtistsSearchFilter();
                    ClearFolderSearchHighlightOnly();
                    break;
                case "Folders":
                    ApplyFolderSearch();
                    break;
                case "UserPlaylist":
                    ApplyUserPlaylistSearchFilter();
                    ClearFolderSearchHighlightOnly();
                    break;
                default:
                    break;
            }
        }

        private void ClearFolderSearchHighlightOnly()
        {
            if (_folderSearchHighlightPath == null && _folderSearchMatches.Count == 0)
            {
                return;
            }

            _folderSearchHighlightPath = null;
            _folderSearchMatches.Clear();
            _folderSearchIndex = -1;
            if (FolderBrowserView != null && _currentCategory == "Folders")
            {
                RefreshFolderBrowserSelectionChrome();
            }
        }

        /// <summary>汇总唯一艺术家 / 专辑艺术家，按名字升序；加载已保存的自定义头像</summary>
        private async Task RefreshArtistViewAsync()
        {
            _artists.Clear();
            bool albumArtistMode = string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);

            if (_playlist.Count == 0)
            {
                return;
            }

            var groups = _playlist
                .GroupBy(
                    p => albumArtistMode ? p.AlbumArtist : p.Artist,
                    StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (var group in groups)
            {
                var entry = new ArtistEntry
                {
                    Name = albumArtistMode ? group.First().AlbumArtist : group.First().Artist,
                    TrackCount = group.Count()
                };
                _artists.Add(entry);
            }

            ApplyArtistsSearchFilter();

            foreach (ArtistEntry entry in _artists.ToList())
            {
                if (_currentCategory != "Artists" && _currentCategory != "AlbumArtists")
                {
                    return;
                }

                entry.AvatarImage = await ResolveArtistAvatarAsync(entry.Name, albumArtistMode);
            }
        }

        private static string ArtistAvatarStoreKey(string artistName, bool albumArtistMode)
            => albumArtistMode ? "aa|" + artistName.Trim() : artistName.Trim();

        /// <summary>
        /// 自定义头像优先；否则取该艺术家年份最晚专辑中音轨 1 的封面。
        /// </summary>
        private async Task<BitmapImage?> ResolveArtistAvatarAsync(string artistName, bool? albumArtistMode = null)
        {
            bool useAlbumArtist = albumArtistMode
                ?? (_artistDetailUsesAlbumArtist
                    || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal));
            string storeKey = ArtistAvatarStoreKey(artistName, useAlbumArtist);
            BitmapImage? custom = await ArtistAvatarStore.TryLoadAsync(storeKey);
            if (custom != null)
            {
                return custom;
            }

            return await ResolveArtistDefaultAvatarAsync(artistName, useAlbumArtist);
        }

        private async Task<BitmapImage?> ResolveArtistDefaultAvatarAsync(string artistName, bool useAlbumArtist)
        {
            // 默认优先使用网络头像（iTunes），失败再回退本地专辑封面
            BitmapImage? web = await TryLoadWebArtistAvatarAsync(artistName);
            if (web != null)
            {
                return web;
            }

            List<PlaylistItem> tracks = _playlist
                .Where(t => TrackMatchesArtistName(t, artistName, useAlbumArtist))
                .ToList();
            if (tracks.Count == 0)
            {
                return null;
            }

            AlbumEntry? latestAlbum = BuildAlbumEntriesFromTracks(tracks)
                .OrderByDescending(a => a.Year)
                .ThenByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
            if (latestAlbum == null || string.IsNullOrWhiteSpace(latestAlbum.CoverSourcePath))
            {
                return null;
            }

            byte[]? bytes = await Task.Run(() => ExtractCoverBytes(latestAlbum.CoverSourcePath));
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            return await CreateBitmapFromBytesAsync(bytes);
        }

        /// <summary>按歌手名从网络加载头像（缓存，失败返回 null）。</summary>
        private static async Task<BitmapImage?> TryLoadWebArtistAvatarAsync(string artistName)
        {
            string key = artistName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (WebArtistAvatarCache.TryGetValue(key, out BitmapImage? cached))
            {
                return cached;
            }

            await WebArtistAvatarGate.WaitAsync();
            try
            {
                if (WebArtistAvatarCache.TryGetValue(key, out cached))
                {
                    return cached;
                }

                // 磁盘缓存命中（重启后免联网搜索）：加载后回填内存缓存
                BitmapImage? fromDisk = await ArtistAvatarStore.TryLoadWebAsync(key);
                if (fromDisk != null)
                {
                    WebArtistAvatarCache[key] = fromDisk;
                    return fromDisk;
                }

                BitmapImage? result = null;
                try
                {
                    string? url = await OnlineMusicApi.SearchArtistAvatarUrlAsync(key);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        // 共享 HttpClient（P1-6）：8 秒超时改由 CTS 按调用控制，不再每次新建实例
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        byte[] bytes = await HttpClients.Default.GetByteArrayAsync(url, cts.Token);
                        if (bytes.Length > 0)
                        {
                            result = await CreateBitmapFromBytesAsync(bytes);
                            // 持久化到磁盘缓存，下次打开（含重启后）不再联网搜索
                            await ArtistAvatarStore.SaveWebAsync(key, bytes);
                        }
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                if (WebArtistAvatarCache.Count > 500)
                {
                    WebArtistAvatarCache.Clear();
                }

                WebArtistAvatarCache[key] = result;
                return result;
            }
            finally
            {
                WebArtistAvatarGate.Release();
            }
        }

        private void ApplyAlbumsSearchFilter()
        {
            if (_currentCategory != "Albums" || AlbumGridView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(AlbumGridView.ItemsSource, _albums))
                {
                }

                RefreshAlbumWallSelectionChrome(AlbumGridView, _albums);
                return;
            }

            List<AlbumEntry> filtered = _albums
                .Where(a =>
                    ContainsIgnoreCase(a.Name, q)
                    || ContainsIgnoreCase(a.Artist, q))
                .ToList();

            RefreshAlbumWallSelectionChrome(AlbumGridView, filtered);
        }

        private void ApplyArtistsSearchFilter()
        {
            if ((_currentCategory != "Artists" && _currentCategory != "AlbumArtists") || ArtistGridView == null)
            {
                return;
            }

            string q = _librarySearchText.Trim();
            if (string.IsNullOrEmpty(q))
            {
                if (!ReferenceEquals(ArtistGridView.ItemsSource, _artists))
                {
                }

                return;
            }

            List<ArtistEntry> filtered = _artists
                .Where(a => ContainsIgnoreCase(a.Name, q))
                .ToList();

        }

        // W13 第二十九刀：搜索过滤 / 已实现容器枚举

        private static IEnumerable<GridViewItem> EnumerateRealizedGridViewItems(GridView grid)
        {
            Panel? panel = VisualTreeWalker.FindItemsPanel(grid);
            if (panel == null)
            {
                yield break;
            }

            foreach (UIElement child in panel.Children)
            {
                if (child is GridViewItem item)
                {
                    yield return item;
                }
            }
        }
    }
