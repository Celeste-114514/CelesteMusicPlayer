using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 在线标签候选（一条搜索结果）。MusicBrainz = 发行（专辑粒度，封面走 Cover Art Archive），
    /// iTunes = 曲目（字段最全：年份/曲目号/碟片/流派/封面都有）。歌词不走这里，单独走 LRClib。
    /// </summary>
    public sealed class OnlineTagCandidate
    {
        public int Index { get; set; }

        public string Source { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        public string AlbumArtist { get; set; } = string.Empty;

        public uint Year { get; set; }

        public uint Track { get; set; }

        public uint TrackCount { get; set; }

        public uint Disc { get; set; }

        public uint DiscCount { get; set; }

        public string Genre { get; set; } = string.Empty;

        public string CoverUrl { get; set; } = string.Empty;

        public int DurationMs { get; set; }

        public bool DetailLoaded { get; set; }

        public string YearText => Year == 0 ? "-" : Year.ToString(CultureInfo.InvariantCulture);

        public string SourceLabel => Source switch
        {
            _ when string.Equals(Source, OnlineTagFill.SourceItunes, StringComparison.OrdinalIgnoreCase) => "iTunes",
            _ when string.Equals(Source, OnlineTagFill.SourceDiscogs, StringComparison.OrdinalIgnoreCase) => "Discogs",
            _ when string.Equals(Source, OnlineTagFill.SourceLastFm, StringComparison.OrdinalIgnoreCase) => "Last.fm",
            _ => "MB"
        };

        public string DurationText
        {
            get
            {
                if (DurationMs <= 0)
                {
                    return "-";
                }

                TimeSpan t = TimeSpan.FromMilliseconds(DurationMs);
                return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
            }
        }
    }

    /// <summary>在线标签搜索/详情/封面/歌词。免费源：MusicBrainz + Cover Art Archive + iTunes + LRClib；
    /// 有 Key 源：Discogs（极详尽发行元数据+高清封面）、Last.fm（流派/封面）、fanart.tv（HD 封面/艺人图，按 MBID 取）。</summary>
    public static class OnlineTagFill
    {
        public const string SourceAll = "All";
        public const string SourceBoth = "Both";
        public const string SourceMusicBrainz = "MusicBrainz";
        public const string SourceItunes = "iTunes";
        public const string SourceDiscogs = "Discogs";
        public const string SourceLastFm = "Last.fm";

        private static readonly HttpClient Http = CreateClient();

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

        // =====================================================================
        // 搜索（MB 与 iTunes 并行；MB 走官方限速闸，iTunes 免 key）
        // =====================================================================

        public static async Task<IReadOnlyList<OnlineTagCandidate>> SearchAsync(
            string source,
            string title,
            string artist,
            string album,
            CancellationToken cancellationToken = default)
        {
            var merged = new List<OnlineTagCandidate>();
            string t = (title ?? string.Empty).Trim();
            string a = (artist ?? string.Empty).Trim();
            string al = (album ?? string.Empty).Trim();
            if (t.Length == 0 && a.Length == 0 && al.Length == 0)
            {
                return merged;
            }

            bool wantAll = string.Equals(source, SourceAll, StringComparison.OrdinalIgnoreCase);
            bool wantMb = wantAll
                || string.Equals(source, SourceBoth, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, SourceMusicBrainz, StringComparison.OrdinalIgnoreCase);
            bool wantItunes = wantAll
                || string.Equals(source, SourceBoth, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, SourceItunes, StringComparison.OrdinalIgnoreCase);
            bool wantDiscogs = wantAll
                || string.Equals(source, SourceDiscogs, StringComparison.OrdinalIgnoreCase);
            bool wantLastFm = wantAll
                || string.Equals(source, SourceLastFm, StringComparison.OrdinalIgnoreCase);

            var tasks = new List<Task<IReadOnlyList<OnlineTagCandidate>>>();
            if (wantMb)
            {
                tasks.Add(SearchMusicBrainzAsync(t, a, al, cancellationToken));
            }

            if (wantItunes)
            {
                tasks.Add(SearchItunesAsync(t, a, al, cancellationToken));
            }

            if (wantDiscogs)
            {
                tasks.Add(SearchDiscogsAsync(t, a, al, cancellationToken));
            }

            if (wantLastFm)
            {
                tasks.Add(SearchLastFmAsync(t, a, al, cancellationToken));
            }

            IReadOnlyList<OnlineTagCandidate>[] parts = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (IReadOnlyList<OnlineTagCandidate> part in parts)
            {
                merged.AddRange(part);
            }

            for (int i = 0; i < merged.Count; i++)
            {
                merged[i].Index = i + 1;
            }

            return merged;
        }

        /// <summary>检查指定来源里需要 Key 的平台是否已配置 Key，返回未配置的平台名（用于界面提示）；全配齐返回 null。</summary>
        public static string? CheckKeyAvailability(string source)
        {
            bool wantAll = string.Equals(source, SourceAll, StringComparison.OrdinalIgnoreCase);
            bool wantDiscogs = wantAll
                || string.Equals(source, SourceDiscogs, StringComparison.OrdinalIgnoreCase);
            bool wantLastFm = wantAll
                || string.Equals(source, SourceLastFm, StringComparison.OrdinalIgnoreCase);

            var missing = new List<string>();
            var settings = AppSettingsStore.Load();
            if (wantDiscogs && string.IsNullOrWhiteSpace(settings.DiscogsConsumerKey))
            {
                missing.Add("Discogs");
            }

            if (wantLastFm && string.IsNullOrWhiteSpace(settings.LastFmApiKey))
            {
                missing.Add("Last.fm");
            }

            if (missing.Count == 0)
            {
                return null;
            }

            return "以下平台未配置 Key，已跳过：" + string.Join("、", missing)
                + "（设置里填好 Key 后即可使用）。";
        }

        /// <summary>加权选最佳：标题/艺术家/专辑分别算相似度，返回候选下标；都不像返回 -1。</summary>
        public static int PickBestMatch(
            IReadOnlyList<OnlineTagCandidate> candidates, string title, string artist, string album, string fileName)
        {
            string wantTitle = Normalize(title);
            if (wantTitle.Length == 0)
            {
                wantTitle = Normalize(fileName);
            }

            string wantArtist = Normalize(artist);
            string wantAlbum = Normalize(album);

            int best = -1;
            double bestScore = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                OnlineTagCandidate c = candidates[i];
                double score = 0;
                if (wantTitle.Length > 0)
                {
                    score += 0.5 * Similarity(wantTitle, Normalize(c.Title));
                }

                if (wantArtist.Length > 0)
                {
                    score += 0.3 * Similarity(wantArtist, Normalize(c.Artist));
                }

                if (wantAlbum.Length > 0)
                {
                    score += 0.2 * Similarity(wantAlbum, Normalize(c.Album));
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            return bestScore >= 0.45 ? best : -1;
        }

        private static double Similarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0)
            {
                return 0;
            }

            if (a == b)
            {
                return 1;
            }

            int distance = Levenshtein(a, b);
            int max = Math.Max(a.Length, b.Length);
            double ratio = 1.0 - (double)distance / max;
            if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            {
                ratio = Math.Max(ratio, 0.85);
            }

            return ratio;
        }

        private static int Levenshtein(string a, string b)
        {
            int n = a.Length;
            int m = b.Length;
            if (n == 0)
            {
                return m;
            }

            if (m == 0)
            {
                return n;
            }

            int[] prev = new int[m + 1];
            int[] curr = new int[m + 1];
            for (int j = 0; j <= m; j++)
            {
                prev[j] = j;
            }

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }

                (prev, curr) = (curr, prev);
            }

            return prev[m];
        }

        /// <summary>MusicBrainz 发行搜索（release 粒度）：标题/艺术家/专辑艺术家/年份/封面（CAA release-group）。</summary>
        private static async Task<IReadOnlyList<OnlineTagCandidate>> SearchMusicBrainzAsync(
            string title, string artist, string album, CancellationToken cancellationToken)
        {
            var results = new List<OnlineTagCandidate>();
            try
            {
                string query;
                if (artist.Length > 0 && (title.Length > 0 || album.Length > 0))
                {
                    string releaseTerm = title.Length > 0 ? title : album;
                    query = $"artist:\"{artist}\" AND release:\"{releaseTerm}\"";
                }
                else if (artist.Length > 0)
                {
                    query = $"artist:\"{artist}\"";
                }
                else
                {
                    query = title.Length > 0 ? $"release:\"{title}\"" : $"release:\"{album}\"";
                }

                string url = "https://musicbrainz.org/ws/2/release/?query="
                    + Uri.EscapeDataString(query)
                    + "&limit=20&fmt=json";

                using HttpResponseMessage resp = await OnlineMusicApi.MusicBrainzGetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("releases", out JsonElement releases)
                    || releases.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                foreach (JsonElement r in releases.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string mbid = TryGetString(r, "id");
                    string name = TryGetString(r, "title");
                    if (string.IsNullOrWhiteSpace(mbid) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string albumArtist = string.Join(" / ", ReadArtistCredit(r));
                    uint year = ParseYear(TryGetString(r, "date"));

                    string groupId = string.Empty;
                    if (r.TryGetProperty("release-group", out JsonElement rg))
                    {
                        groupId = TryGetString(rg, "id");
                    }

                    results.Add(new OnlineTagCandidate
                    {
                        Source = SourceMusicBrainz,
                        Id = mbid,
                        Title = name,
                        Artist = albumArtist,
                        Album = name,
                        AlbumArtist = albumArtist,
                        Year = year,
                        CoverUrl = string.IsNullOrWhiteSpace(groupId)
                            ? string.Empty
                            : $"https://coverartarchive.org/release-group/{groupId}/front"
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineTagFill.SearchMusicBrainzAsync", caught);
            }

            return results;
        }

        /// <summary>iTunes 曲目搜索（免 key）：字段最全的一条路。</summary>
        private static async Task<IReadOnlyList<OnlineTagCandidate>> SearchItunesAsync(
            string title, string artist, string album, CancellationToken cancellationToken)
        {
            var results = new List<OnlineTagCandidate>();
            try
            {
                string term = string.Join(" ", new[] { title, artist, album }
                    .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                if (term.Length == 0)
                {
                    return results;
                }

                string url = "https://itunes.apple.com/search?media=music&entity=song&limit=25&term="
                    + Uri.EscapeDataString(term);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out JsonElement resultsEl)
                    || resultsEl.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                foreach (JsonElement s in resultsEl.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string trackId = TryGetString(s, "trackId");
                    string name = TryGetString(s, "trackName");
                    if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string cover = TryGetString(s, "artworkUrl100");
                    if (cover.Contains("100x100bb", StringComparison.OrdinalIgnoreCase))
                    {
                        cover = cover.Replace("100x100bb", "1200x1200bb", StringComparison.OrdinalIgnoreCase);
                    }

                    results.Add(new OnlineTagCandidate
                    {
                        Source = SourceItunes,
                        Id = trackId,
                        Title = name,
                        Artist = TryGetString(s, "artistName"),
                        Album = TryGetString(s, "collectionName"),
                        AlbumArtist = TryGetString(s, "collectionArtistName"),
                        Year = ParseYear(TryGetString(s, "releaseDate")),
                        Track = TryGetUInt(s, "trackNumber"),
                        TrackCount = TryGetUInt(s, "trackCount"),
                        Disc = TryGetUInt(s, "discNumber"),
                        DiscCount = TryGetUInt(s, "discCount"),
                        Genre = TryGetString(s, "primaryGenreName"),
                        CoverUrl = cover,
                        DurationMs = TryGetInt(s, "trackTimeMillis"),
                        DetailLoaded = true
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineTagFill.SearchItunesAsync", caught);
            }

            return results;
        }

        // =====================================================================
        // Discogs（有 Key）：极详尽发行元数据（year/genre/style/label/country/高清封面）
        // 鉴权走简化头「Discogs key=…, secret=…」，免完整 OAuth 流程。
        // =====================================================================

        private static async Task<IReadOnlyList<OnlineTagCandidate>> SearchDiscogsAsync(
            string title, string artist, string album, CancellationToken cancellationToken)
        {
            var results = new List<OnlineTagCandidate>();
            try
            {
                var settings = AppSettingsStore.Load();
                if (string.IsNullOrWhiteSpace(settings.DiscogsConsumerKey)
                    || string.IsNullOrWhiteSpace(settings.DiscogsConsumerSecret))
                {
                    return results;
                }

                string q = string.Join(" ", new[] { title, artist, album }
                    .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                if (q.Length == 0)
                {
                    return results;
                }

                string url = "https://api.discogs.com/database/search?type=release&per_page=20&q="
                    + Uri.EscapeDataString(q);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Authorization",
                    $"Discogs key={settings.DiscogsConsumerKey}, secret={settings.DiscogsConsumerSecret}");
                request.Headers.TryAddWithoutValidation("User-Agent", "CelesteMusicPlayer/1.0");
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out JsonElement arr)
                    || arr.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                foreach (JsonElement r in arr.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string id = r.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.Number
                        ? idEl.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : TryGetString(r, "id");
                    string rawTitle = TryGetString(r, "title"); // Discogs 形如 "Artist - Album"
                    (string a, string alb) = ParseDiscogsTitle(rawTitle);
                    uint year = TryGetUInt(r, "year");

                    var genres = new List<string>();
                    CollectStrings(r, "genre", genres);
                    CollectStrings(r, "style", genres);

                    string thumb = TryGetString(r, "thumb");

                    results.Add(new OnlineTagCandidate
                    {
                        Source = SourceDiscogs,
                        Id = id,
                        Title = alb.Length > 0 ? alb : rawTitle,
                        Artist = a,
                        Album = alb,
                        Year = year,
                        Genre = string.Join("; ", genres.Distinct()),
                        CoverUrl = thumb,
                        DetailLoaded = false
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineTagFill.SearchDiscogsAsync", caught);
            }

            return results;
        }

        /// <summary>选中 Discogs 结果后拉发行详情：补高清封面、年份、流派/风格（搜索结果里可能缺）。</summary>
        public static async Task<OnlineTagCandidate?> LoadDiscogsDetailAsync(
            OnlineTagCandidate candidate, CancellationToken cancellationToken = default)
        {
            if (candidate == null || !string.Equals(candidate.Source, SourceDiscogs, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            try
            {
                string url = "https://api.discogs.com/releases/" + Uri.EscapeDataString(candidate.Id);
                var settings = AppSettingsStore.Load();
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Authorization",
                    $"Discogs key={settings.DiscogsConsumerKey}, secret={settings.DiscogsConsumerSecret}");
                request.Headers.TryAddWithoutValidation("User-Agent", "CelesteMusicPlayer/1.0");
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    candidate.DetailLoaded = true;
                    return candidate;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (candidate.Year == 0 && root.TryGetProperty("year", out JsonElement yearEl))
                {
                    uint y = TryGetUInt(root, "year");
                    if (y != 0)
                    {
                        candidate.Year = y;
                    }
                }

                var genres = new List<string>();
                CollectStrings(root, "genres", genres);
                CollectStrings(root, "styles", genres);
                if (genres.Count > 0)
                {
                    candidate.Genre = string.Join("; ", genres.Distinct());
                }

                // 高清封面：取 images 里第一条 uri（大图），覆盖搜索时的缩略图
                if (root.TryGetProperty("images", out JsonElement images) && images.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement img in images.EnumerateArray())
                    {
                        string uri = TryGetString(img, "uri");
                        string type = TryGetString(img, "type");
                        if (uri.Length > 0 && (type == "primary" || type.Length == 0))
                        {
                            candidate.CoverUrl = uri;
                            break;
                        }
                    }
                }

                candidate.DetailLoaded = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineTagFill.LoadDiscogsDetailAsync", caught);
                candidate.DetailLoaded = true;
            }

            return candidate;
        }

        private static (string Artist, string Album) ParseDiscogsTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return (string.Empty, string.Empty);
            }

            int idx = raw.IndexOf(" - ", StringComparison.Ordinal);
            if (idx < 0)
            {
                return (string.Empty, raw.Trim());
            }

            return (raw.Substring(0, idx).Trim(), raw.Substring(idx + 3).Trim());
        }

        // =====================================================================
        // Last.fm（有 Key）：album.getinfo → 流派(top tags) + 高清封面（mega 图）
        // =====================================================================

        private static async Task<IReadOnlyList<OnlineTagCandidate>> SearchLastFmAsync(
            string title, string artist, string album, CancellationToken cancellationToken)
        {
            var results = new List<OnlineTagCandidate>();
            try
            {
                var settings = AppSettingsStore.Load();
                if (string.IsNullOrWhiteSpace(settings.LastFmApiKey))
                {
                    return results;
                }

                // Last.fm 是专辑粒度：用专辑名（没有就用标题）+ 艺术家
                string albumTerm = album.Length > 0 ? album : title;
                if (albumTerm.Length == 0 || artist.Length == 0)
                {
                    return results;
                }

                string url = "https://ws.audioscrobbler.com/2.0/?method=album.getinfo&api_key="
                    + Uri.EscapeDataString(settings.LastFmApiKey)
                    + "&artist=" + Uri.EscapeDataString(artist)
                    + "&album=" + Uri.EscapeDataString(albumTerm)
                    + "&format=json";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "CelesteMusicPlayer/1.0");
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("album", out JsonElement al))
                {
                    return results;
                }

                string name = TryGetString(al, "name");
                string art = TryGetString(al, "artist");
                string mbid = TryGetString(al, "mbid");

                var tags = new List<string>();
                if (al.TryGetProperty("tags", out JsonElement tagsEl)
                    && tagsEl.TryGetProperty("tag", out JsonElement tagArr)
                    && tagArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tg in tagArr.EnumerateArray())
                    {
                        string? n = TryGetString(tg, "name");
                        if (!string.IsNullOrWhiteSpace(n))
                        {
                            tags.Add(n);
                        }
                    }
                }

                // 封面：image 数组里最后一条是 mega（最大）
                string cover = string.Empty;
                if (al.TryGetProperty("image", out JsonElement imgs) && imgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement im in imgs.EnumerateArray())
                    {
                        string u = TryGetString(im, "#text");
                        if (u.Length > 0)
                        {
                            cover = u;
                        }
                    }
                }

                results.Add(new OnlineTagCandidate
                {
                    Source = SourceLastFm,
                    Id = mbid,
                    Title = name.Length > 0 ? name : albumTerm,
                    Artist = art,
                    Album = name,
                    Genre = string.Join("; ", tags.Distinct()),
                    CoverUrl = cover,
                    DetailLoaded = true
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineTagFill.SearchLastFmAsync", caught);
            }

            return results;
        }

        // =====================================================================
        // fanart.tv（有 Key）：按 MusicBrainz ID 取 HD 封面/艺人图，作为封面升级
        // =====================================================================

        public static async Task<string?> FetchFanartCoverAsync(
            string mbid, bool isArtist, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(mbid))
            {
                return null;
            }

            var settings = AppSettingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.FanartTvApiKey))
            {
                return null;
            }

            try
            {
                string url = isArtist
                    ? "https://webservice.fanart.tv/v3/music/" + Uri.EscapeDataString(mbid)
                        + "?api_key=" + Uri.EscapeDataString(settings.FanartTvApiKey)
                    : "https://webservice.fanart.tv/v3/music/albums/" + Uri.EscapeDataString(mbid)
                        + "?api_key=" + Uri.EscapeDataString(settings.FanartTvApiKey);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "CelesteMusicPlayer/1.0");
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                // 专辑：{ "albums": { "<mbid>": [ { "url": ..., "type":"albumcover" } ] } }
                if (!isArtist && root.TryGetProperty("albums", out JsonElement albums)
                    && albums.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in albums.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement it in prop.Value.EnumerateArray())
                            {
                                string type = TryGetString(it, "type");
                                if (type == "albumcover" || type.Length == 0)
                                {
                                    string u = TryGetString(it, "url");
                                    if (u.Length > 0)
                                    {
                                        return u;
                                    }
                                }
                            }
                        }
                    }
                }

                // 艺人：artistthumb[] / artistbackground[]
                if (isArtist)
                {
                    foreach (string key in new[] { "artistthumb", "artistbackground" })
                    {
                        if (root.TryGetProperty(key, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement it in arr.EnumerateArray())
                            {
                                string u = TryGetString(it, "url");
                                if (u.Length > 0)
                                {
                                    return u;
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineTagFill.FetchFanartCoverAsync", caught);
            }

            return null;
        }

        private static void CollectStrings(JsonElement el, string name, List<string> outList)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out JsonElement v)
                || v.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement x in v.EnumerateArray())
            {
                string? s = x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                if (!string.IsNullOrWhiteSpace(s) && !outList.Contains(s))
                {
                    outList.Add(s);
                }
            }
        }

        // =====================================================================
        // MB 发行详情（选中 MB 结果后才拉）：曲目总数/碟片/流派/封面 + 按标题找曲目号
        // =====================================================================

        public static async Task<OnlineTagCandidate?> LoadMusicBrainzDetailAsync(
            OnlineTagCandidate candidate, string localTitle, CancellationToken cancellationToken = default)
        {
            if (candidate == null || !string.Equals(candidate.Source, SourceMusicBrainz, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            try
            {
                string url = "https://musicbrainz.org/ws/2/release/" + Uri.EscapeDataString(candidate.Id)
                    + "?inc=recordings+artist-credits+genres&fmt=json";
                using HttpResponseMessage resp = await OnlineMusicApi.MusicBrainzGetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return candidate;
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("date", out JsonElement dateEl))
                {
                    uint year = ParseYear(dateEl.GetString());
                    if (year != 0)
                    {
                        candidate.Year = year;
                    }
                }

                if (root.TryGetProperty("genres", out JsonElement genres) && genres.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    foreach (JsonElement g in genres.EnumerateArray())
                    {
                        string? n = TryGetString(g, "name");
                        if (!string.IsNullOrWhiteSpace(n))
                        {
                            names.Add(n);
                        }
                    }

                    if (names.Count > 0)
                    {
                        candidate.Genre = string.Join("; ", names);
                    }
                }

                int trackTotal = 0;
                int discTotal = 0;
                int matchedTrack = 0;
                int matchedDisc = 0;
                string want = Normalize(localTitle);

                if (root.TryGetProperty("media", out JsonElement media) && media.ValueKind == JsonValueKind.Array)
                {
                    discTotal = media.GetArrayLength();
                    foreach (JsonElement m in media.EnumerateArray())
                    {
                        int discNo = (int)TryGetUInt(m, "position");
                        if (m.TryGetProperty("tracks", out JsonElement tracks) && tracks.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement tr in tracks.EnumerateArray())
                            {
                                trackTotal++;
                                if (matchedTrack == 0 && tr.TryGetProperty("title", out JsonElement tt))
                                {
                                    string got = Normalize(tt.GetString() ?? string.Empty);
                                    if (want.Length > 0 && (got == want
                                        || (got.Length >= 4 && want.Length >= 4 && got.Contains(want, StringComparison.Ordinal))
                                        || (got.Length >= 4 && want.Length >= 4 && want.Contains(got, StringComparison.Ordinal))))
                                    {
                                        matchedTrack = (int)TryGetUInt(tr, "position");
                                        matchedDisc = discNo;
                                    }
                                }
                            }
                        }
                    }
                }

                if (trackTotal > 0)
                {
                    candidate.TrackCount = (uint)trackTotal;
                }

                if (discTotal > 0)
                {
                    candidate.DiscCount = (uint)discTotal;
                }

                if (matchedTrack > 0)
                {
                    candidate.Track = (uint)matchedTrack;
                    if (matchedDisc > 0)
                    {
                        candidate.Disc = (uint)matchedDisc;
                    }
                }

                if (string.IsNullOrWhiteSpace(candidate.CoverUrl))
                {
                    candidate.CoverUrl = OnlineMusicApi.CoverArtArchiveUrl(candidate.Id, "500") ?? string.Empty;
                }

                candidate.DetailLoaded = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineTagFill.LoadMusicBrainzDetailAsync", caught);
            }

            return candidate;
        }

        // =====================================================================
        // 歌词（LRClib，免 key）：详情页预览 + 写入歌词字段用
        // =====================================================================

        public static async Task<string> FetchLyricAsync(
            string artist, string title, string album, double durationSeconds,
            CancellationToken cancellationToken = default)
        {
            return await OnlineMusicApi.GetLyricFromLrclibAsync(
                artist, title, album, durationSeconds, cancellationToken).ConfigureAwait(false);
        }

        // =====================================================================
        // 封面字节（预览与写入共用）
        // =====================================================================

        public static async Task<byte[]?> DownloadCoverBytesAsync(
            string coverUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                return null;
            }

            try
            {
                using HttpResponseMessage resp = await Http.GetAsync(coverUrl, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                return await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineTagFill.DownloadCoverBytesAsync", caught);
                return null;
            }
        }

        // =====================================================================
        // 小工具
        // =====================================================================

        private static List<string> ReadArtistCredit(JsonElement release)
        {
            var names = new List<string>();
            if (release.TryGetProperty("artist-credit", out JsonElement ac) && ac.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in ac.EnumerateArray())
                {
                    string? v = TryGetString(c, "name");
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        names.Add(v);
                    }
                }
            }

            return names;
        }

        private static string TryGetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out JsonElement v))
            {
                return string.Empty;
            }

            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        }

        private static uint TryGetUInt(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out JsonElement v))
            {
                return 0;
            }

            if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out uint n))
            {
                return n;
            }

            return 0;
        }

        private static int TryGetInt(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out JsonElement v))
            {
                return 0;
            }

            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
            {
                return n;
            }

            return 0;
        }

        private static uint ParseYear(string? date)
        {
            if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
            {
                return 0;
            }

            return uint.TryParse(date.AsSpan(0, 4), out uint y) ? y : 0;
        }

        private static string Normalize(string? s)
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
    }
}
