using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    public sealed record LyricCandidate(
        string Source,
        string SongId,
        string Title,
        string Artist,
        string Album,
        int DurationMs,
        bool HasTranslation)
    {
        public int Index { get; set; }

        public string DurationText
        {
            get
            {
                if (DurationMs <= 0)
                {
                    return "-";
                }

                var t = TimeSpan.FromMilliseconds(DurationMs);
                return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
            }
        }

        public string OnlineUrl =>
            string.Equals(Source, OnlineLyricDownload.SourceQQ, StringComparison.Ordinal)
                ? $"https://y.qq.com/n/ryqq/songDetail/{Uri.EscapeDataString(SongId)}"
                : string.Empty;
    }

    public enum LyricWriteTarget
    {
        Sidecar,
        TagPlain,
        TagTimestamped,
        Both
    }

    public sealed class LyricBatchItem : INotifyPropertyChanged
    {
        public LyricBatchItem(int index, string title, string artist)
        {
            Index = index;
            Title = title;
            Artist = artist;
        }

        public int Index { get; }

        public string Title { get; }

        public string Artist { get; }

        private string _status = "等待中";

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value)
                {
                    return;
                }

                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed record LyricBatchSong(string FilePath, string Title, string Artist, string Album, string FileName);

    public sealed record LyricBatchOptions(
        string Source,
        bool IncludeTranslation,
        bool SkipExisting,
        bool SaveToSongFolder,
        string LyricFolder);

    public sealed record LyricBatchProgress(int Index, int Done, int Total, string Status);

    public sealed record LyricBatchSummary(
        int Total, int Saved, int Skipped, int NoLyric, int NoMatch, int Failed, bool Canceled);

    public static class OnlineLyricDownload
    {
        public const string SourceLrclib = "LRClib";
        public const string SourceQQ = "QQ";

        private static readonly HttpClient Http = CreateClient();

        private static readonly SemaphoreSlim QqGate = new(1, 1);

        private static long QqLastTick;

        private static readonly Regex LrcTimestampRegex = new(
            @"\[\d+:\d+(?:[.:]\d+)?\]",
            RegexOptions.Compiled);

        private static readonly Regex LrcMetaTagRegex = new(
            @"^\s*\[(ti|ar|al|by|offset|au|length|re|ve|id)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return client;
        }

        public static LyricWriteTarget ParseWriteTarget(string? s) => s switch
        {
            "TagPlain" => LyricWriteTarget.TagPlain,
            "TagTimestamped" => LyricWriteTarget.TagTimestamped,
            "Both" => LyricWriteTarget.Both,
            _ => LyricWriteTarget.Sidecar
        };

        public static async Task<IReadOnlyList<LyricCandidate>> SearchAsync(
            string source,
            string keyword,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return Array.Empty<LyricCandidate>();
            }

            try
            {
                return string.Equals(source, SourceQQ, StringComparison.OrdinalIgnoreCase)
                    ? await SearchQQAsync(keyword, cancellationToken).ConfigureAwait(false)
                    : await SearchLrclibAsync(keyword, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<LyricCandidate>();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineLyricDownload.SearchAsync", caught);
                return Array.Empty<LyricCandidate>();
            }
        }

        private static async Task<IReadOnlyList<LyricCandidate>> SearchQQAsync(
            string keyword,
            CancellationToken cancellationToken)
        {
            string url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp"
                + "?p=1&n=30&format=json&w=" + Uri.EscapeDataString(keyword);

            string json = await QqRequestAsync(url, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
            {
                return Array.Empty<LyricCandidate>();
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("song", out JsonElement song)
                || song.ValueKind != JsonValueKind.Object
                || !song.TryGetProperty("list", out JsonElement list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<LyricCandidate>();
            }

            List<LyricCandidate> result = new();
            foreach (JsonElement item in list.EnumerateArray())
            {
                string? mid = item.TryGetProperty("songmid", out JsonElement midEl)
                    ? midEl.GetString()
                    : null;
                if (string.IsNullOrEmpty(mid))
                {
                    continue;
                }

                string title = item.TryGetProperty("songname", out JsonElement nameEl)
                    ? nameEl.GetString() ?? string.Empty
                    : string.Empty;
                string album = item.TryGetProperty("albumname", out JsonElement albumEl)
                    ? albumEl.GetString() ?? string.Empty
                    : string.Empty;
                int durationSec = item.TryGetProperty("interval", out JsonElement durEl)
                    && durEl.TryGetInt32(out int d)
                    ? d
                    : 0;

                string artist = string.Empty;
                if (item.TryGetProperty("singer", out JsonElement singer)
                    && singer.ValueKind == JsonValueKind.Array)
                {
                    artist = string.Join(" / ", singer.EnumerateArray()
                        .Select(s => s.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty)
                        .Where(n => !string.IsNullOrEmpty(n)));
                }

                result.Add(new LyricCandidate(SourceQQ, mid!, title, artist, album, durationSec * 1000, false));
            }

            return result;
        }

        private static async Task<IReadOnlyList<LyricCandidate>> SearchLrclibAsync(
            string keyword,
            CancellationToken cancellationToken)
        {
            string url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(keyword);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", OnlineMusicApi.CelesteUserAgent);
            using HttpResponseMessage resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Array.Empty<LyricCandidate>();
            }

            string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<LyricCandidate>();
            }

            List<LyricCandidate> result = new();
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                string id = item.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetRawText()
                    : string.Empty;
                string title = item.TryGetProperty("trackName", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
                string artist = item.TryGetProperty("artistName", out JsonElement a) ? a.GetString() ?? string.Empty : string.Empty;
                string album = item.TryGetProperty("albumName", out JsonElement al) ? al.GetString() ?? string.Empty : string.Empty;
                double duration = item.TryGetProperty("duration", out JsonElement du) && du.TryGetDouble(out double dv)
                    ? dv
                    : 0;

                result.Add(new LyricCandidate(SourceLrclib, id, title, artist, album,
                    duration > 0 ? (int)Math.Round(duration * 1000) : 0, false));
            }

            return result;
        }

        public static async Task<string> FetchLyricAsync(
            LyricCandidate candidate,
            bool includeTranslation,
            CancellationToken cancellationToken = default)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.SongId))
            {
                return string.Empty;
            }

            try
            {
                return string.Equals(candidate.Source, SourceQQ, StringComparison.OrdinalIgnoreCase)
                    ? await FetchQQLyricAsync(candidate, includeTranslation, cancellationToken).ConfigureAwait(false)
                    : await FetchLrclibLyricAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineLyricDownload.FetchLyricAsync", caught);
                return string.Empty;
            }
        }

        private static async Task<string> FetchQQLyricAsync(
            LyricCandidate candidate,
            bool includeTranslation,
            CancellationToken cancellationToken)
        {
            string url = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"
                + "?songmid=" + Uri.EscapeDataString(candidate.SongId)
                + "&format=json&nobase64=1";

            string json = await QqRequestAsync(url, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
            {
                return string.Empty;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (root.TryGetProperty("retcode", out JsonElement rc) && rc.TryGetInt32(out int code) && code != 0)
            {
                return string.Empty;
            }

            string lyric = root.TryGetProperty("lyric", out JsonElement ly) && ly.ValueKind == JsonValueKind.String
                ? ly.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(lyric))
            {
                return string.Empty;
            }

            if (includeTranslation
                && root.TryGetProperty("trans", out JsonElement tr)
                && tr.ValueKind == JsonValueKind.String)
            {
                string trans = tr.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(trans))
                {
                    lyric = lyric.TrimEnd() + "\r\n\r\n" + trans.Trim();
                }
            }

            return lyric;
        }

        private static async Task<string> FetchLrclibLyricAsync(
            LyricCandidate candidate,
            CancellationToken cancellationToken)
        {
            return await OnlineMusicApi.GetLyricFromLrclibAsync(
                candidate.Artist,
                candidate.Title,
                candidate.Album,
                candidate.DurationMs > 0 ? candidate.DurationMs / 1000.0 : (double?)null,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string> QqRequestAsync(string url, CancellationToken cancellationToken)
        {
            await QqGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long now = Environment.TickCount64;
                long wait = 1100 - (now - QqLastTick);
                if (wait > 0)
                {
                    await Task.Delay((int)Math.Min(wait, 5000), cancellationToken).ConfigureAwait(false);
                }

                QqLastTick = Environment.TickCount64;

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
                using HttpResponseMessage resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                return await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                QqGate.Release();
            }
        }

        public static int PickBestMatch(
            IReadOnlyList<LyricCandidate> list,
            string title,
            string artist,
            string album,
            string fileName)
        {
            if (list == null || list.Count == 0)
            {
                return -1;
            }

            string t = NormalizeForMatch(title);
            string a = NormalizeForMatch(artist);
            string al = NormalizeForMatch(album);
            string fn = NormalizeForMatch(fileName);

            double bestWeight = -1;
            int bestIndex = -1;
            for (int i = 0; i < list.Count; i++)
            {
                LyricCandidate item = list[i];
                double weight = 0;
                weight += Similarity(t, NormalizeForMatch(item.Title)) * 0.4;
                weight += Similarity(a, NormalizeForMatch(item.Artist)) * 0.4;
                weight += Similarity(al, NormalizeForMatch(item.Album)) * 0.3;
                weight += Similarity(fn, NormalizeForMatch(item.Title)) * 0.3;
                weight += Similarity(fn, NormalizeForMatch(item.Artist)) * 0.3;
                weight += (1 - i * 0.02) * 0.05;

                if (weight > bestWeight)
                {
                    bestWeight = weight;
                    bestIndex = i;
                }
            }

            return bestWeight < 0.3 ? -1 : bestIndex;
        }

        private static string NormalizeForMatch(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            char[] buf = new char[s.Length];
            int n = 0;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                char v = (c >= '！' && c <= '～') ? (char)(c - 0xFEE0) : c;
                buf[n++] = char.ToLowerInvariant(v);
            }

            return new string(buf, 0, n);
        }

        private static double Similarity(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0)
            {
                return 1.0;
            }

            if (a.Length == 0 || b.Length == 0)
            {
                return 0.0;
            }

            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return 1.0;
            }

            if (a.Length > 400 || b.Length > 400)
            {
                return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)
                    ? 0.6
                    : 0.0;
            }

            return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
        }

        private static int Levenshtein(string a, string b)
        {
            int[] prev = new int[b.Length + 1];
            int[] curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
            {
                prev[j] = j;
            }

            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }

                (prev, curr) = (curr, prev);
            }

            return prev[b.Length];
        }

        public static string StripLrcTimestamps(string? lrc)
        {
            if (string.IsNullOrWhiteSpace(lrc))
            {
                return string.Empty;
            }

            StringBuilder sb = new();
            foreach (string rawLine in lrc!.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (LrcMetaTagRegex.IsMatch(line))
                {
                    continue;
                }

                string cleaned = LrcTimestampRegex.Replace(line, string.Empty).Trim();
                if (cleaned.Length == 0)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(cleaned);
            }

            return sb.ToString();
        }

        public static async Task<(bool Ok, string Message)> WriteLyricAsync(
            string audioPath,
            string lyric,
            LyricWriteTarget target,
            bool saveToSongFolder,
            string lyricFolder,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            {
                return (false, "音频文件不存在，未写入。");
            }

            if (string.IsNullOrWhiteSpace(lyric))
            {
                return (false, "歌词内容为空，未写入。");
            }

            List<string> done = new();
            bool wantSidecar = target is LyricWriteTarget.Sidecar or LyricWriteTarget.Both;
            bool wantTag = target is not LyricWriteTarget.Sidecar;

            try
            {
                if (wantSidecar)
                {
                    string? dir = saveToSongFolder || string.IsNullOrWhiteSpace(lyricFolder)
                        ? Path.GetDirectoryName(audioPath)
                        : lyricFolder;
                    if (string.IsNullOrEmpty(dir))
                    {
                        return (false, "拿不到歌词保存目录。");
                    }

                    Directory.CreateDirectory(dir);
                    string lrcPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(audioPath) + ".lrc");
                    await File.WriteAllTextAsync(lrcPath, lyric, new UTF8Encoding(false), cancellationToken)
                        .ConfigureAwait(false);
                    done.Add("外挂 .lrc");
                }

                if (wantTag)
                {
                    string text = target is LyricWriteTarget.TagTimestamped ? lyric : StripLrcTimestamps(lyric);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return (false, "剥掉时间戳后歌词为空，未写入标签。");
                    }

                    TagEditModel model = TagEditorService.ReadTagRaw(audioPath);
                    if (string.Equals(model.Lyrics ?? string.Empty, text, StringComparison.Ordinal))
                    {
                        done.Add("标签歌词原本就一致，未改动");
                    }
                    else
                    {
                        model.Lyrics = text;
                        model.MarkDirty(TagField.Lyrics);
                        TagSaveResult saved = TagEditorService.TrySaveTag(audioPath, model);
                        if (!saved.Ok)
                        {
                            return (false, "写歌词标签失败：" + (saved.Error ?? "原因见日志"));
                        }

                        done.Add(target is LyricWriteTarget.TagTimestamped ? "歌词标签（保留时间戳）" : "歌词标签（纯文本）");
                    }
                }

                return (true, done.Count > 0 ? "已写入：" + string.Join(" + ", done) : "没有需要写入的内容。");
            }
            catch (OperationCanceledException)
            {
                return (false, "已取消。");
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineLyricDownload.WriteLyricAsync", caught);
                return (false, "写入失败：" + caught.Message);
            }
        }

        private static string ResolveSidecarPath(string audioPath, LyricBatchOptions options)
        {
            string dir = options.SaveToSongFolder || string.IsNullOrWhiteSpace(options.LyricFolder)
                ? Path.GetDirectoryName(audioPath) ?? string.Empty
                : options.LyricFolder;
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(audioPath) + ".lrc");
        }

        private static string BuildBatchKeyword(LyricBatchSong song)
        {
            if (string.IsNullOrWhiteSpace(song.Title))
            {
                return song.FileName;
            }

            if (string.IsNullOrWhiteSpace(song.Artist))
            {
                return song.Title;
            }

            return song.Artist + " " + song.Title;
        }

        public static async Task<LyricBatchSummary> BatchDownloadAsync(
            IReadOnlyList<LyricBatchSong> songs,
            LyricBatchOptions options,
            IProgress<LyricBatchProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            int saved = 0;
            int skipped = 0;
            int noLyric = 0;
            int noMatch = 0;
            int failed = 0;
            bool canceled = false;

            for (int i = 0; i < songs.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                LyricBatchSong song = songs[i];
                progress?.Report(new LyricBatchProgress(i, i, songs.Count, "下载中…"));

                string status;
                try
                {
                    if (options.SkipExisting && File.Exists(ResolveSidecarPath(song.FilePath, options)))
                    {
                        skipped++;
                        progress?.Report(new LyricBatchProgress(i, i + 1, songs.Count, "已跳过"));
                        continue;
                    }

                    string keyword = BuildBatchKeyword(song);
                    IReadOnlyList<LyricCandidate> hits =
                        await SearchAsync(options.Source, keyword, cancellationToken).ConfigureAwait(false);
                    if (hits.Count == 0)
                    {
                        noMatch++;
                        progress?.Report(new LyricBatchProgress(i, i + 1, songs.Count, "没找到"));
                        continue;
                    }

                    int best = PickBestMatch(hits, song.Title, song.Artist, song.Album, song.FileName);
                    if (best < 0)
                    {
                        noMatch++;
                        progress?.Report(new LyricBatchProgress(i, i + 1, songs.Count, "没有匹配"));
                        continue;
                    }

                    string lyric = await FetchLyricAsync(
                        hits[best], options.IncludeTranslation, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(lyric))
                    {
                        noLyric++;
                        progress?.Report(new LyricBatchProgress(i, i + 1, songs.Count, "无歌词"));
                        continue;
                    }

                    (bool ok, _) = await WriteLyricAsync(
                        song.FilePath, lyric, LyricWriteTarget.Sidecar,
                        options.SaveToSongFolder, options.LyricFolder, cancellationToken).ConfigureAwait(false);
                    if (ok)
                    {
                        saved++;
                        status = "已保存";
                    }
                    else
                    {
                        failed++;
                        status = "写盘失败";
                    }
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("OnlineLyricDownload.BatchDownloadAsync", caught);
                    failed++;
                    status = "出错";
                }

                progress?.Report(new LyricBatchProgress(i, i + 1, songs.Count, status));
            }

            return new LyricBatchSummary(songs.Count, saved, skipped, noLyric, noMatch, failed, canceled);
        }
    }
}
