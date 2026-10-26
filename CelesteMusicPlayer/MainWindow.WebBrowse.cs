using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        internal object WebBrowse(JsonElement msg)
        {
            string operation = msg.GetProperty("operation").GetString() ?? "get";
            if (operation == "createPlaylist")
            {
                string name = (msg.GetProperty("name").GetString() ?? "").Trim();
                if (name.Length is < 1 or > 120 || name.Any(char.IsControl)) throw new ArgumentException("请输入 1–120 字的播放列表名称。");
                if (NamedPlaylistStore.List().Contains(name)) throw new ArgumentException("播放列表名称已存在。");
                NamedPlaylistStore.Create(name);
            }
            else if (operation is "favorite" or "rating")
            {
                string id = msg.GetProperty("trackId").GetString() ?? "";
                var track = WebAllTracks().FirstOrDefault(item => WebTrackId(item) == id) ?? throw new ArgumentException("曲目已不在曲库中。");
                if (operation == "favorite") TrackStatsStore.SetFavorite(track.PersistenceKey, msg.GetProperty("value").GetBoolean());
                else { int rating = msg.GetProperty("value").GetInt32(); if (rating is < 0 or > 5) throw new ArgumentException("评分必须为 0–5。"); TrackStatsStore.SetRating(track.PersistenceKey, rating); }
            }
            else if (operation != "get") throw new ArgumentException("未知的曲库操作。");
            var stats = TrackStatsStore.Load();
            var tracks = WebAllTracks();
            var roots = (AppSettingsStore.Load().LibraryWatchFolders ?? new()).Where(Directory.Exists)
                .Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator).OrderByDescending(path => path.Length).ToArray();
            return new
            {
                tracks = tracks.Select(item => { stats.TryGetValue(Path.GetFullPath(item.PersistenceKey), out var entry);
                    bool sharedCueStats = entry == null && item.IsCueSegment && stats.TryGetValue(Path.GetFullPath(item.FilePath), out entry);
                    return new {
                    id = WebTrackId(item), folder = WebFolder(item, roots),
                    favorite = entry?.IsFavorite ?? false, rating = entry?.Rating ?? 0, playCount = sharedCueStats ? 0 : entry?.PlayCount ?? 0,
                    lastPlayed = entry?.LastPlayedUtc?.ToUniversalTime().ToString("O") ?? ""
                }; }).ToArray(),
                playlists = NamedPlaylistStore.List().Where(name => name != NamedPlaylistStore.FavoritesPlaylistName).Select(name => {
                    var paths = NamedPlaylistStore.LoadSongs(name); var ids = paths.SelectMany(path => tracks.Where(track => string.Equals(track.PersistenceKey, path, StringComparison.OrdinalIgnoreCase)).Select(WebTrackId)).Distinct().ToArray();
                    string? cover = PlaylistLibraryService.CustomCoverPath(name);
                    string stamp = cover == null ? "default" : File.GetLastWriteTimeUtc(cover).Ticks.ToString();
                    return new { name, trackIds = ids, missingCount = paths.Count(path => !tracks.Any(track => string.Equals(track.PersistenceKey, path, StringComparison.OrdinalIgnoreCase))),
                        coverUrl = "https://celeste-cover.internal/playlist/" + Uri.EscapeDataString(name) + "?v=" + stamp };
                }).ToArray()
            };
        }

        private static string WebFolder(PlaylistItem item, string[] roots)
        {
            if (item.IsRemote) return item.SourceName.Length > 0 ? item.SourceName + "/" + (item.Album ?? "") : "远程音乐";
            return WebBrowseRules.Folder(item.FilePath, roots);
        }
    }
}
