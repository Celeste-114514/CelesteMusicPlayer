using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页侧曲库的**读模型**：曲目快照、专辑分组、文件夹浏览的节点登记簿。
    ///
    /// W13：从 MainWindow 搬出来。这里只做「把曲库数据整理成网页要的形状」，
    /// 不碰播放、不挑文件对话框、不改曲库（那些仍由宿主做，通过 <see cref="Host"/> 之外的调用方传入）。
    ///
    /// 本地曲目用构造时注入的一支委托取 —— 因为曲库集合的所有权还在 MainWindow，
    /// 等曲库自己搬出来，这支委托就换成直接持有。
    /// </summary>
    internal sealed class WebLibraryService(Func<IReadOnlyList<PlaylistItem>> localTracks)
    {
        // ---- 文件夹浏览的节点登记簿 -------------------------------------------------
        // 网页只能拿 id 说话（路径不能塞进 url），所以浏览时把节点记下来，回包只给 id。
        // 每次 roots 刷新就整批作废（epoch +1），避免旧 id 指向已经变了的目录。
        private readonly Dictionary<string, FolderScanEntry> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PlaylistItem> _folderTracks = new(StringComparer.Ordinal);

        /// <summary>节点登记簿的纪元。刷新 roots 时 +1，旧回包据此拒绝执行。</summary>
        internal int Epoch { get; private set; }

        /// <summary>浏览时列出来的曲目（网页要按 id 取封面路径）。超过 8000 条整批清掉。</summary>
        internal IReadOnlyDictionary<string, PlaylistItem> FolderTracks => _folderTracks;

        internal static string FolderId(string path) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).ToLowerInvariant();

        /// <summary>把节点记进登记簿，返回网页认识的形状。</summary>
        internal object RegisterNode(FolderScanEntry item)
        {
            string id = FolderId(item.FullPath);
            _nodes[id] = item;
            return new { id, name = item.DisplayName, path = item.FullPath, folder = item.IsFolder };
        }

        internal bool TryGetNode(string id, out FolderScanEntry node) => _nodes.TryGetValue(id, out node);

        /// <summary>roots 刷新：整批作废旧节点与旧曲目。</summary>
        internal void ResetNodes()
        {
            Epoch++;
            _nodes.Clear();
            _folderTracks.Clear();
        }

        internal void RememberTracks(IEnumerable<PlaylistItem> songs)
        {
            if (_folderTracks.Count > 8000) _folderTracks.Clear();
            foreach (var song in songs) _folderTracks[WebTrackIdentity.Of(song)] = song;
        }

        internal string? CoverPath(string id) => _folderTracks.TryGetValue(id, out var track) ? track.FilePath : null;

        // ---- 曲目快照 ---------------------------------------------------------------

        /// <summary>
        /// 网页看到的「全部曲目」= 本地曲库 + 已索引/已固定的网络曲目。
        /// 本地 CUE 片段会顺手登记进网络曲库，这样单段也能被队列和标签引用。
        /// </summary>
        internal PlaylistItem[] AllTracks()
        {
            var local = localTracks().ToArray();
            // Capture local CUE references so a playlist can restore one segment even outside the current folder.
            var cues = local.Where(t => t.IsCueSegment && t.SourceId.Length == 0 && t.SourceTrackId.Length > 0).Select(t => new NetworkTrack
            {
                Id = t.SourceTrackId, LocalPath = t.FilePath, Cue = true, StartFrame = (long)Math.Round(t.StartTimeSeconds * 75),
                EndFrame = t.Duration.TotalSeconds > 0 ? (long)Math.Round((t.StartTimeSeconds + t.Duration.TotalSeconds) * 75) : 0,
                Title = t.Title, Artist = t.Artist, Track = (int)t.Track,
                Entry = new NetworkEntry { Name = Path.GetFileName(t.FilePath), Title = t.Title, Artist = t.Artist, Album = t.Album, AlbumArtist = t.AlbumArtist, Track = (int)t.Track, Disc = (int)t.Disc }
            }).ToArray();
            if (cues.Length > 0) NetworkLibraryStore.Put(cues.Select(cue => { cue.Pinned = NetworkLibraryStore.Track(cue.Id)?.Pinned ?? false; return cue; }));
            var localIds = local.Select(WebTrackIdentity.Of).ToHashSet(StringComparer.Ordinal);
            var sources = NetworkLibraryStore.Sources().ToDictionary(s => s.Id);
            var remote = NetworkLibraryStore.Tracks().Where(t => !localIds.Contains(t.Id) && (t.Indexed || t.Pinned))
                .Select(t => FromNetwork(t, sources.GetValueOrDefault(t.SourceId)));
            return local.Concat(remote).ToArray();
        }

        /// <summary>把网络曲库的一条记录翻成网页/队列用的曲目对象。</summary>
        internal static PlaylistItem FromNetwork(NetworkTrack track, NetworkSource? source = null)
        {
            string title = track.Title.Length > 0 ? track.Title : track.Entry.Title.Length > 0 ? track.Entry.Title : Path.GetFileNameWithoutExtension(track.Entry.Name);
            double duration = track.Cue ? Math.Max(0, (track.EndFrame - track.StartFrame) / 75.0) : track.Entry.Duration;
            return new PlaylistItem
            {
                SourceId = track.SourceId, SourceTrackId = track.Id, SourceName = source?.Name ?? (track.SourceId.Length == 0 ? "本地" : "已停用来源"),
                FilePath = NetworkLibraryService.CachedPath(track), RemotePath = track.SourceId.Length > 0 ? track.Entry.Path : "", IsCueSegment = track.Cue,
                StartTimeSeconds = track.StartFrame / 75.0, Duration = TimeSpan.FromSeconds(duration), Title = title,
                Artist = track.Artist.Length > 0 ? track.Artist : track.Entry.Artist.Length > 0 ? track.Entry.Artist : "未知艺术家",
                Album = track.Entry.Album.Length > 0 ? track.Entry.Album : source?.Name ?? "未知专辑", AlbumArtist = track.Entry.AlbumArtist.Length > 0 ? track.Entry.AlbumArtist : track.Entry.Artist,
                Track = (uint)Math.Max(0, track.Track > 0 ? track.Track : track.Entry.Track), Disc = (uint)Math.Max(0, track.Entry.Disc),
                RemoteHint = track.Missing ? "文件缺失" : File.Exists(NetworkLibraryService.CachedPath(track)) ? "已缓存" : "未缓存"
            };
        }

        // ---- 专辑分组 ---------------------------------------------------------------

        /// <summary>
        /// 按「专辑 + 专辑艺术家 + 来源」分组。专辑 id 也进哈希，
        /// 同一张专辑换了来源就算两张（网络来源的同名专辑不能混）。
        /// </summary>
        internal static WebAlbumEntry[] Albums(IReadOnlyList<PlaylistItem> source)
        {
            return source.GroupBy(item => (Album: item.Album ?? "", Artist: item.AlbumArtist ?? item.Artist ?? "", Source: item.SourceId))
                .Select((group, index) =>
                {
                    var tracks = group.ToArray();
                    var cover = tracks.FirstOrDefault(item => (item.Disc == 0 || item.Disc == 1) && item.Track == 1) ?? tracks[0];
                    string artist = string.IsNullOrWhiteSpace(group.Key.Artist) ? "未知艺术家" : group.Key.Artist;
                    string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("album\n" + group.Key.Source + "\n" + group.Key.Album.ToUpperInvariant() + "\n" + artist.ToUpperInvariant()))).ToLowerInvariant();
                    return new WebAlbumEntry
                    {
                        id = id, sourceId = group.Key.Source, title = group.Key.Album, artist = artist, year = tracks.Where(item => item.Year > 0).Select(item => item.Year).DefaultIfEmpty(0u).Max(),
                        added = index, trackCount = tracks.Length, duration = tracks.Sum(item => item.Duration.TotalSeconds),
                        coverUrl = "https://celeste-cover.internal/cover/" + WebTrackIdentity.Of(cover) + "?size=480",
                        trackIds = tracks.OrderBy(item => item.Disc == 0 ? 1u : item.Disc)
                            .ThenBy(item => item.Track == 0 ? uint.MaxValue : item.Track)
                            .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.StartTimeSeconds)
                            .Select(WebTrackIdentity.Of).ToArray()
                    };
                }).ToArray();
        }
    }
}
