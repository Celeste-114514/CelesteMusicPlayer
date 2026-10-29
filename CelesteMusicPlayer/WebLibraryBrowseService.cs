using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 曲库浏览面：`browse` 命令（列表 + 播放列表 + 收藏/评分）与「集合详情」聚合。
    ///
    /// W13：从 <c>MainWindow.WebBrowse.cs</c> 与 <c>MainWindow.WebCollections.cs</c> 整块搬出。
    /// 这一块**零播放、零 UI**，只读曲库与文件头，所以不接任何宿主口子 ——
    /// 唯一的外部依赖是 <see cref="WebLibraryService"/>（曲目 id、曲库快照）。
    ///
    /// 注意「集合详情」里的音质统计：它只读文件头，不整轨解码，也不新开 ffmpeg；
    /// ffmpeg 只在前两种方式都判不出时兜底，且有 4 秒上限与取消注册。别改成整轨探测。
    /// </summary>
    internal sealed class WebLibraryBrowseService(WebLibraryService library)
    {
        private sealed record WebAudioFact(long stamp, int rate, int bits, string codec, bool? lossless);

        /// <summary>按「文件 + 最后写入时间」缓存音质事实，避免反复开文件；上限 8192 条后整体清空。</summary>
        private readonly ConcurrentDictionary<string, WebAudioFact> _facts = new(StringComparer.OrdinalIgnoreCase);

        // —— browse ——

        internal object Browse(JsonElement msg)
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
                var track = library.AllTracks().FirstOrDefault(item => WebTrackIdentity.Of(item) == id) ?? throw new ArgumentException("曲目已不在曲库中。");
                if (operation == "favorite") TrackStatsStore.SetFavorite(track.PersistenceKey, msg.GetProperty("value").GetBoolean());
                else { int rating = msg.GetProperty("value").GetInt32(); if (rating is < 0 or > 5) throw new ArgumentException("评分必须为 0–5。"); TrackStatsStore.SetRating(track.PersistenceKey, rating); }
            }
            else if (operation != "get") throw new ArgumentException("未知的曲库操作。");
            var stats = TrackStatsStore.Load();
            var tracks = library.AllTracks();
            var roots = (AppSettingsStore.Load().LibraryWatchFolders ?? new()).Where(Directory.Exists)
                .Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator).OrderByDescending(path => path.Length).ToArray();
            return new
            {
                tracks = tracks.Select(item => { stats.TryGetValue(Path.GetFullPath(item.PersistenceKey), out var entry);
                    bool sharedCueStats = entry == null && item.IsCueSegment && stats.TryGetValue(Path.GetFullPath(item.FilePath), out entry);
                    return new {
                    id = WebTrackIdentity.Of(item), folder = WebFolder(item, roots),
                    favorite = entry?.IsFavorite ?? false, rating = entry?.Rating ?? 0, playCount = sharedCueStats ? 0 : entry?.PlayCount ?? 0,
                    lastPlayed = entry?.LastPlayedUtc?.ToUniversalTime().ToString("O") ?? ""
                }; }).ToArray(),
                playlists = NamedPlaylistStore.List().Where(name => name != NamedPlaylistStore.FavoritesPlaylistName).Select(name => {
                    var paths = NamedPlaylistStore.LoadSongs(name); var ids = paths.SelectMany(path => tracks.Where(track => string.Equals(track.PersistenceKey, path, StringComparison.OrdinalIgnoreCase)).Select(WebTrackIdentity.Of)).Distinct().ToArray();
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

        // —— 集合详情（专辑 / 艺术家 / 专辑艺人）——

        internal async Task<object> CollectionOverviewAsync(string category, string name, CancellationToken cancellation)
        {
            if (name.Length > 2048 || category is not ("artists" or "albumArtists" or "albums")) throw new ArgumentException("未知的曲库分类。");
            string Key(PlaylistItem item) => category == "albums" ? item.Album : category == "albumArtists" ? item.AlbumArtist : item.Artist;
            var tracks = library.AllTracks().Where(item => string.Equals(Key(item), name, StringComparison.CurrentCultureIgnoreCase)).Select(item => item.Clone()).ToArray();
            if (tracks.Length == 0) throw new InvalidOperationException("曲库已变化，请重新打开详情。");
            return await Task.Run<object>(() =>
            {
                int lossless = 0, lossy = 0, unknown = 0;
                var known = new Dictionary<string, WebAudioFact>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in tracks)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!known.TryGetValue(item.FilePath, out var fact)) { fact = ReadAudioFact(item, cancellation); known[item.FilePath] = fact; }
                    if (fact.lossless == true) lossless++; else if (fact.lossless == false) lossy++; else unknown++;
                }
                var pcm = known.Values.Where(fact => fact.rate >= 8000 && !fact.codec.StartsWith("dsd", StringComparison.OrdinalIgnoreCase)).OrderByDescending(fact => fact.rate).ThenByDescending(fact => fact.bits).FirstOrDefault();
                var dsd = known.Values.Where(fact => fact.codec.Contains("dsd", StringComparison.OrdinalIgnoreCase) && fact.rate >= 1_000_000).OrderByDescending(fact => fact.rate).FirstOrDefault();
                var specs = new List<string>();
                if (pcm != null) specs.Add((pcm.bits > 0 ? pcm.bits + "bit" : "位深未知") + " / " + (pcm.rate / 1000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "kHz");
                if (dsd != null) specs.Add("DSD / " + (dsd.rate / 1_000_000d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "MHz");
                uint[] years = tracks.Where(item => item.Year > 0).Select(item => item.Year).ToArray();
                return new
                {
                    songCount = tracks.Length, albumCount = tracks.Select(item => item.Album).Distinct(StringComparer.CurrentCultureIgnoreCase).Count(),
                    duration = tracks.Sum(item => item.Duration.TotalSeconds), yearFrom = years.Length > 0 ? years.Min() : 0, yearTo = years.Length > 0 ? years.Max() : 0,
                    losslessCount = lossless, lossyCount = lossy, unknownCount = unknown,
                    losslessPercent = lossless + lossy > 0 ? (double?)Math.Round(lossless * 100d / (lossless + lossy), 1) : null,
                    highestSpec = string.Join(" · ", specs),
                    localCount = tracks.Count(item => !item.IsRemote), remoteCount = tracks.Count(item => item.IsRemote),
                    genres = tracks.Where(item => !string.IsNullOrWhiteSpace(item.Genre) && item.Genre != "未知流派").GroupBy(item => item.Genre, StringComparer.CurrentCultureIgnoreCase).OrderByDescending(group => group.Count()).Take(5).Select(group => group.Key).ToArray()
                };
            }, cancellation);
        }

        private static bool? Lossless(string value)
        {
            string codec = value.ToLowerInvariant();
            if (codec.Contains("hybrid")) return null;
            if (new[] { "aac", "mp3", "mpeg", "vorbis", "opus", "musepack", "ac-3", "ac3", "eac3", "lossy" }.Any(codec.Contains)
                && !codec.Contains("alac") && !codec.Contains("lossless")) return false;
            if (new[] { "flac", "alac", "apple lossless", "lossless", "pcm", "wavpack", "monkey", "ape", "true audio", "tta", "truehd", "mlp", "dsd" }.Any(codec.Contains)) return true;
            return null;
        }

        private WebAudioFact ReadAudioFact(PlaylistItem item, CancellationToken cancellation)
        {
            string path = item.FilePath;
            try
            {
                if (!item.IsRemote && Path.GetExtension(path).ToLowerInvariant() is ".dsf" or ".dff")
                {
                    uint nativeRate = DsdProbe.ProbeFreqHz(path);
                    if (nativeRate > 0) return new WebAudioFact(0, checked((int)nativeRate), 1, "dsd", true);
                }
                if (AudioInfoFormatter.TryPeekFacts(path, out int rate, out int bits, out string codec) && rate >= 8000 && bits > 0)
                    return new WebAudioFact(0, rate, bits, codec, Lossless(codec));
                if (item.IsRemote) return new WebAudioFact(0, 0, 0, "", null);
                long stamp = File.GetLastWriteTimeUtc(path).Ticks;
                if (_facts.TryGetValue(path, out var cached) && cached.stamp == stamp) return cached;
                // Header/tag reads only: no full decode and no new ffmpeg process.
                using var file = TagLib.File.Create(path);
                var properties = file.Properties;
                codec = properties.Description ?? "";
                rate = properties.AudioSampleRate >= 8000 ? properties.AudioSampleRate : 0;
                bits = properties.BitsPerSample;
                if (rate == 0 || bits == 0 || Lossless(codec) == null)
                {
                    var probed = ProbeHeader(path, cancellation);
                    if (probed.rate > 0) rate = probed.rate;
                    if (probed.bits > 0) bits = probed.bits;
                    if (probed.codec.Length > 0) codec = probed.codec;
                }
                var result = new WebAudioFact(stamp, rate, bits, codec, Lossless(codec));
                if (_facts.Count >= 8192) _facts.Clear();
                _facts[path] = result;
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                cancellation.ThrowIfCancellationRequested();
                if (!item.IsRemote && File.Exists(path))
                {
                    try { var probed = ProbeHeader(path, cancellation) with { stamp = File.GetLastWriteTimeUtc(path).Ticks }; _facts[path] = probed; return probed; }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                return new WebAudioFact(0, 0, 0, "", null);
            }
        }

        private static WebAudioFact ProbeHeader(string path, CancellationToken cancellation)
        {
            string? executable = AudioPlaybackEngine.FindFfmpeg();
            if (executable == null) return new(0, 0, 0, "", null);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "-hide_banner", "-nostdin", "-i", path }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process == null) return new(0, 0, 0, "", null);
            using var registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(4000)) { try { process.Kill(true); } catch { } return new(0, 0, 0, "", null); }
            cancellation.ThrowIfCancellationRequested();
            string line = error.GetAwaiter().GetResult().Split('\n').FirstOrDefault(value => value.Contains("Audio:")) ?? "";
            string codec = Regex.Match(line, @"Audio:\s*([a-zA-Z0-9_]+)").Groups[1].Value;
            int.TryParse(Regex.Match(line, @"(\d+)\s*Hz").Groups[1].Value, out int rate);
            if (codec.StartsWith("dsd", StringComparison.OrdinalIgnoreCase)) rate = checked(rate * 8);
            return new(0, rate, AudioInfoFormatter.ParseBitDepth(line, codec), codec, Lossless(codec));
        }
    }
}
