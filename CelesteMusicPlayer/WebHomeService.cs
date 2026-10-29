using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页「主页」的聚合计算：最近在听、听歌统计、最近专辑、本周活跃热力图。
    ///
    /// W13：从 MainWindow 搬出来。这里只做「读曲库快照 + 读统计 + 算聚合」，
    /// 不碰控件、不碰播放，曲库从构造时注入的三支委托取（曲目表、专辑分组、曲目 id）。
    /// </summary>
    internal sealed class WebHomeService(
        Func<PlaylistItem[]> allTracks,
        Func<IReadOnlyList<PlaylistItem>, WebAlbumEntry[]> albumsOf,
        Func<PlaylistItem, string> trackId)
    {
        internal async Task<object> HandleAsync(JsonElement msg)
        {
            string operation = msg.GetProperty("operation").GetString() ?? "get";
            int offsetMinutes = Math.Clamp(msg.GetProperty("offsetMinutes").GetInt32(), -840, 840);
            var offset = TimeSpan.FromMinutes(-offsetMinutes);
            if (operation == "dayHistory")
            {
                var dayTracks = allTracks();
                return await Task.Run(() => DayHistory(msg, offset, dayTracks));
            }
            if (operation != "get") throw new ArgumentException("未知的主页操作。");

            // Capture UI-owned collections before leaving the UI thread.
            var tracks = allTracks();
            var stats = TrackStatsStore.Load();
            var albums = albumsOf(tracks);
            string today = msg.GetProperty("today").GetString() ?? "";
            return await Task.Run(() => Snapshot(tracks, stats, albums, today, offset));
        }

        private object Snapshot(PlaylistItem[] tracks, Dictionary<string, TrackStatsEntry> stats,
            WebAlbumEntry[] albums, string today, TimeSpan offset)
        {
            var byPath = tracks.GroupBy(track => Path.GetFullPath(track.PersistenceKey), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var recent = LibraryDb.LoadPlaybackHistory(1000);
            var last = recent.FirstOrDefault(item => byPath.ContainsKey(Path.GetFullPath(item.FilePath)));
            PlaylistItem? lastTrack = last == null ? null : byPath[Path.GetFullPath(last.FilePath)];
            var recentAlbumIds = new List<string>();
            if (lastTrack != null)
            {
                foreach (var history in recent)
                {
                    if (!byPath.TryGetValue(Path.GetFullPath(history.FilePath), out var track)) continue;
                    string id = trackId(track);
                    var album = albums.FirstOrDefault(candidate => candidate.trackIds.Contains(id));
                    if (album != null && !recentAlbumIds.Contains(album.id)) recentAlbumIds.Add(album.id);
                    if (recentAlbumIds.Count >= 12) break;
                }
            }

            var topArtists = tracks.Select(track => new { track, count = StatsFor(stats, track)?.PlayCount ?? 0 })
                .Where(item => item.count > 0).GroupBy(item => item.track.Artist ?? "未知艺术家", StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new { name = group.First().track.Artist ?? "未知艺术家", count = group.Sum(item => item.count),
                    coverUrl = group.First().track == null ? "" : "https://celeste-cover.internal/cover/" + trackId(group.First().track) })
                .OrderByDescending(item => item.count).ThenBy(item => item.name, StringComparer.CurrentCultureIgnoreCase).Take(5).ToArray();
            // Indexed by assignment, not ToDictionary: a cue-split file can yield duplicate ids and must not throw.
            var trackById = new Dictionary<string, PlaylistItem>(StringComparer.Ordinal);
            foreach (var track in tracks) trackById[trackId(track)] = track;
            var topAlbums = albums.Select(album => new { album.id, album.title, album.artist, album.coverUrl,
                count = album.trackIds.Sum(id => trackById.TryGetValue(id, out var track) ? StatsFor(stats, track)?.PlayCount ?? 0 : 0) })
                .Where(album => album.count > 0).OrderByDescending(album => album.count).ThenBy(album => album.title, StringComparer.CurrentCultureIgnoreCase).Take(4).ToArray();

            DateTime todayDate = DateTime.SpecifyKind(DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            DateTime monday = todayDate.AddDays(-(((int)todayDate.DayOfWeek + 6) % 7) - 77);
            DateTime startUtc = new DateTimeOffset(monday, offset).UtcDateTime;
            DateTime endUtc = new DateTimeOffset(todayDate.AddDays(1), offset).UtcDateTime;
            var daily = new Dictionary<string, int>(StringComparer.Ordinal);
            string? activityError = null;
            try
            {
                foreach (var item in LibraryDb.LoadPlaybackHistoryRange(startUtc, endUtc))
                {
                    string day = (item.PlayedAtUtc.ToUniversalTime() + offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    daily[day] = daily.TryGetValue(day, out int count) ? count + 1 : 1;
                }
            }
            catch (Exception ex) { activityError = ex.Message; StartupLog.WriteException("WebHome.Activity", ex); }
            return new
            {
                revision = tracks.Length + ":" + stats.Values.Sum(item => item.PlayCount) + ":" + recent.Count,
                favoriteCount = tracks.Count(track => StatsFor(stats, track)?.IsFavorite == true),
                lastTrackId = lastTrack == null ? "" : trackId(lastTrack), lastTrackTitle = lastTrack?.Title ?? "",
                lastTrackArtist = lastTrack?.Artist ?? "", lastTrackAlbum = lastTrack?.Album ?? "",
                recentAlbumIds = recentAlbumIds.ToArray(), topArtists, topAlbums, dailyCounts = daily, activityError
            };
        }

        private static TrackStatsEntry? StatsFor(Dictionary<string, TrackStatsEntry> stats, PlaylistItem track)
            => stats.TryGetValue(Path.GetFullPath(track.PersistenceKey), out var entry) ? entry : null;

        private object DayHistory(JsonElement msg, TimeSpan offset, PlaylistItem[] current)
        {
            DateTime date = DateTime.SpecifyKind(DateOnly.ParseExact(msg.GetProperty("date").GetString() ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            int cursor = Math.Max(0, msg.GetProperty("cursor").GetInt32());
            int limit = Math.Clamp(msg.GetProperty("limit").GetInt32(), 1, 100);
            DateTime startUtc = new DateTimeOffset(date, offset).UtcDateTime, endUtc = new DateTimeOffset(date.AddDays(1), offset).UtcDateTime;
            var all = LibraryDb.LoadPlaybackHistoryRange(startUtc, endUtc);
            var page = LibraryDb.LoadPlaybackHistoryDay(startUtc, endUtc, cursor, limit);
            return new
            {
                total = all.Count, nextCursor = cursor + page.Count < all.Count ? cursor + page.Count : (int?)null,
                tracks = page.Select(item =>
                {
                    var track = current.FirstOrDefault(candidate => string.Equals(candidate.PersistenceKey, item.FilePath, StringComparison.OrdinalIgnoreCase));
                    return new { trackId = track == null ? "" : trackId(track), title = item.Title,
                        artist = track?.Artist ?? "", fileName = Path.GetFileName(item.FilePath),
                        time = (item.PlayedAtUtc.ToUniversalTime() + offset).ToString("HH:mm", CultureInfo.InvariantCulture) };
                }).ToArray()
            };
        }
    }
}
