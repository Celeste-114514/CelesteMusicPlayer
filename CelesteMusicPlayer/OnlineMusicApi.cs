using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>歌曲搜索结果：Source 为 iTunes / MusicBrainz。</summary>
    public sealed record OnlineSongResult(
        string Source,
        string SongId,
        string Name,
        string Artist,
        string Album,
        string CoverUrl);

    /// <summary>
    /// 在线歌词 / 封面 / 元数据下载。2026-10-13 合规整改后只保留开放/官方接口：
    /// iTunes Search API（官方）、MusicBrainz（开放）、Last.fm / Discogs / fanart.tv（需用户自填 key）。
    /// 网易云 / QQ / 酷狗等逆向非官方接口已全部移除。
    /// </summary>
    public static class OnlineMusicApi
    {
        internal static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return client;
        }

        /// <summary>
        /// 解析当前选用的在线平台。2026-10-13 合规整改后只保留 MusicBrainz 与 iTunes：
        /// 网易云 / QQ / 酷狗均为逆向非官方接口（且网易云实测已上风控返回 -462），全部移除。
        /// </summary>
        private static string ResolveSource()
        {
            string s = AppSettingsStore.Load().OnlineSearchDefaultSource;
            return string.Equals(s, "iTunes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "MusicBrainz", StringComparison.OrdinalIgnoreCase)
                ? s
                : "MusicBrainz";
        }

        // =====================================================================
        // 搜索
        // =====================================================================

        public static async Task<IReadOnlyList<OnlineSongResult>> SearchSongsAsync(
            string source,
            string title,
            string artist,
            CancellationToken cancellationToken = default)
        {
            return source switch
            {
                "iTunes" => await SearchItunesSongsAsync(title, artist, cancellationToken).ConfigureAwait(false),
                _ => await SearchMusicBrainzSongsAsync(title, artist, cancellationToken).ConfigureAwait(false)
            };
        }

        /// <summary>
        /// 按歌手名搜索「艺术家」候选（头像搜索窗口用）。返回结果里 Name=歌手名、CoverUrl=歌手头像。
        /// 与 SearchSongsAsync（歌曲搜索）不同，这里走各平台的歌手/艺术家搜索接口，直接命中歌手本人头像，
        /// 避免用歌曲名搜索接口搜歌手名时混入一堆无关歌曲封面。
        /// 复用 OnlineSongResult：Source=平台、SongId=歌手id、Name=歌手名、CoverUrl=头像URL。
        /// </summary>
        public static async Task<IReadOnlyList<OnlineSongResult>> SearchArtistsAsync(
            string source,
            string query,
            CancellationToken cancellationToken = default)
        {
            return source switch
            {
                "iTunes" => await SearchItunesArtistsAsync(query, cancellationToken).ConfigureAwait(false),
                _ => await SearchMusicBrainzArtistsAsync(query, cancellationToken).ConfigureAwait(false)
            };
        }

        /// <summary>
        /// 艺术家概览（简介）。
        /// 2026-10-13 合规整改后改为三层兜底，全部为开放/官方接口且国内直连可达：
        ///   ① Last.fm artist.getInfo 的 bio（官方授权内容，需用户自己的 API key）
        ///   ② MusicBrainz artist annotation（社区维护，零门槛）
        ///   ③ Discogs artist profile（需用户自己的 token）
        /// 拿不到就返回 null（调用方显示「暂无该艺术家的概览」），不抛异常。
        /// </summary>
        public static async Task<string?> GetArtistOverviewAsync(
            string artistName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return null;
            }

            return await GetOverviewFromOpenSourcesAsync(artistName, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 艺术家简介三层兜底调度：Last.fm bio → MusicBrainz annotation → Discogs profile。
        /// 任一层拿到非空内容即返回，全部落空返回 null。
        /// </summary>
        private static async Task<string?> GetOverviewFromOpenSourcesAsync(
            string artistName,
            CancellationToken cancellationToken)
        {
            string? brief = await GetLastFmArtistBioAsync(artistName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(brief))
            {
                return brief;
            }

            brief = await GetMusicBrainzAnnotationAsync(artistName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(brief))
            {
                return brief;
            }

            return await GetDiscogsArtistProfileAsync(artistName, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>① Last.fm artist.getInfo 的 bio 摘要（需用户在设置里填自己的 API key）。</summary>
        private static async Task<string?> GetLastFmArtistBioAsync(
            string artistName,
            CancellationToken cancellationToken)
        {
            string key = AppSettingsStore.Load().LastFmApiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            try
            {
                string url = "https://ws.audioscrobbler.com/2.0/?method=artist.getinfo&artist="
                    + Uri.EscapeDataString(artistName.Trim())
                    + "&api_key=" + Uri.EscapeDataString(key)
                    + "&format=json&autocorrect=1";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", CelesteUserAgent);
                using HttpResponseMessage resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("artist", out JsonElement artist)
                    || !artist.TryGetProperty("bio", out JsonElement bio))
                {
                    return null;
                }

                if (bio.TryGetProperty("summary", out JsonElement summary)
                    && summary.ValueKind == JsonValueKind.String)
                {
                    string? s = StripHtml(summary.GetString());
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                }

                if (bio.TryGetProperty("content", out JsonElement content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    return StripHtml(content.GetString());
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught);
                return null;
            }
        }

        /// <summary>② MusicBrainz artist annotation（社区维护简介，零门槛）。</summary>
        private static async Task<string?> GetMusicBrainzAnnotationAsync(
            string artistName,
            CancellationToken cancellationToken)
        {
            try
            {
                string searchUrl = "https://musicbrainz.org/ws/2/artist/?query="
                    + Uri.EscapeDataString("artist:\"" + artistName.Trim() + "\"")
                    + "&fmt=json&limit=1";

                string? mbid = null;
                using (HttpResponseMessage sResp = await MusicBrainzGetAsync(searchUrl, cancellationToken).ConfigureAwait(false))
                {
                    if (sResp.IsSuccessStatusCode)
                    {
                        string sJson = await sResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using JsonDocument sDoc = JsonDocument.Parse(sJson);
                        if (sDoc.RootElement.TryGetProperty("artists", out JsonElement artists)
                            && artists.ValueKind == JsonValueKind.Array
                            && artists.GetArrayLength() > 0
                            && artists[0].TryGetProperty("id", out JsonElement idElem)
                            && idElem.ValueKind == JsonValueKind.String)
                        {
                            mbid = idElem.GetString();
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(mbid))
                {
                    return null;
                }

                string url = "https://musicbrainz.org/ws/2/artist/" + mbid + "?inc=annotation&fmt=json";
                using HttpResponseMessage resp = await MusicBrainzGetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("annotation", out JsonElement ann)
                    && ann.ValueKind == JsonValueKind.String)
                {
                    return StripHtml(ann.GetString());
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught);
                return null;
            }
        }

        /// <summary>③ Discogs artist profile（需用户在设置里填自己的 token）。</summary>
        private static async Task<string?> GetDiscogsArtistProfileAsync(
            string artistName,
            CancellationToken cancellationToken)
        {
            string token = AppSettingsStore.Load().DiscogsToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            try
            {
                string searchUrl = "https://api.discogs.com/database/search?q="
                    + Uri.EscapeDataString(artistName.Trim())
                    + "&type=artist&per_page=1";
                using var sReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                sReq.Headers.TryAddWithoutValidation("User-Agent", CelesteUserAgent);
                sReq.Headers.TryAddWithoutValidation("Authorization", "Discogs token=" + token);
                using HttpResponseMessage sResp = await Http.SendAsync(sReq, cancellationToken).ConfigureAwait(false);
                if (!sResp.IsSuccessStatusCode)
                {
                    return null;
                }

                string sJson = await sResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument sDoc = JsonDocument.Parse(sJson);
                if (!sDoc.RootElement.TryGetProperty("results", out JsonElement results)
                    || results.ValueKind != JsonValueKind.Array
                    || results.GetArrayLength() == 0
                    || !results[0].TryGetProperty("id", out JsonElement idElem))
                {
                    return null;
                }

                string url = "https://api.discogs.com/artists/" + idElem.ToString();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", CelesteUserAgent);
                req.Headers.TryAddWithoutValidation("Authorization", "Discogs token=" + token);
                using HttpResponseMessage resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("profile", out JsonElement profile)
                    && profile.ValueKind == JsonValueKind.String)
                {
                    string? s = StripHtml(profile.GetString());
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught);
                return null;
            }
        }

        /// <summary>
        /// 所有对外请求统一使用的 User-Agent。
        /// MusicBrainz 官方强制要求格式为「应用名/版本 (联系方式)」，
        /// 用默认 UA（python-requests / Go-http-client 之类）会被直接拒绝。
        /// </summary>
        public const string CelesteUserAgent =
            "CelesteMusicPlayer/1.0 (https://github.com/Celeste-114514/CelesteMusicPlayer)";

        /// <summary>
        /// 从 LRClib 取歌词。LRClib 是开放歌词库，免注册免 key，仅要求请求自报身份。
        /// 优先带时间轴的 syncedLyrics，没有则退回 plainLyrics。
        /// </summary>
        internal static async Task<string> GetLyricFromLrclibAsync(
            string? artist, string? title, string? album, double? durationSeconds,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
            {
                return string.Empty;
            }

            try
            {
                var sb = new StringBuilder("https://lrclib.net/api/get?");
                sb.Append("track_name=").Append(Uri.EscapeDataString(title ?? string.Empty));
                sb.Append("&artist_name=").Append(Uri.EscapeDataString(artist ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(album))
                {
                    sb.Append("&album_name=").Append(Uri.EscapeDataString(album));
                }

                if (durationSeconds.HasValue && durationSeconds.Value >= 1 && durationSeconds.Value <= 3600)
                {
                    sb.Append("&duration=").Append((int)Math.Round(durationSeconds.Value));
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, sb.ToString());
                req.Headers.TryAddWithoutValidation("User-Agent", CelesteUserAgent);
                using HttpResponseMessage resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return string.Empty;
                }

                if (root.TryGetProperty("syncedLyrics", out JsonElement synced)
                    && synced.ValueKind == JsonValueKind.String)
                {
                    string? s = synced.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s!;
                    }
                }

                if (root.TryGetProperty("plainLyrics", out JsonElement plain)
                    && plain.ValueKind == JsonValueKind.String)
                {
                    return plain.GetString() ?? string.Empty;
                }

                return string.Empty;
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught);
                return string.Empty;
            }
        }

        /// <summary>在 JSON 树里递归找 briefDesc 字段（容错：不同接口返回层级不一样）。</summary>
        private static bool TryFindBriefDesc(JsonElement node, out string? brief)
        {
            brief = null;
            if (node.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (node.TryGetProperty("briefDesc", out JsonElement direct))
            {
                brief = direct.GetString();
                return true;
            }

            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryFindBriefDesc(property.Value, out string? found) && !string.IsNullOrWhiteSpace(found))
                {
                    brief = found;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// iTunes 精确区分艺人：先 entity=musicArtist 定位候选歌手（避免同名混淆），
        /// 再对每个歌手取热门歌曲封面 artworkUrl100→600 作头像。
        /// </summary>
        private static async Task<IReadOnlyList<OnlineSongResult>> SearchItunesArtistsAsync(
            string query,
            CancellationToken cancellationToken)
        {
            var results = new List<OnlineSongResult>();
            try
            {
                string locateUrl = "https://itunes.apple.com/search?term={0}&entity=musicArtist&limit=10";
                locateUrl = string.Format(locateUrl, Uri.EscapeDataString(query.Trim()));
                using HttpRequestMessage locateReq = new(HttpMethod.Get, locateUrl);
                locateReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                using HttpResponseMessage locateResp = await Http.SendAsync(locateReq, cancellationToken).ConfigureAwait(false);
                if (!locateResp.IsSuccessStatusCode)
                {
                    return results;
                }

                string locateJson = await locateResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument locateDoc = JsonDocument.Parse(locateJson);
                if (!locateDoc.RootElement.TryGetProperty("results", out JsonElement res)
                    || res.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonElement artist in res.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? artistId = artist.TryGetProperty("artistId", out JsonElement idEl) ? idEl.GetInt64().ToString() : string.Empty;
                    string? exactName = artist.TryGetProperty("artistName", out JsonElement an) ? an.GetString() : null;
                    if (string.IsNullOrWhiteSpace(exactName) || !seenNames.Add(exactName))
                    {
                        continue;
                    }

                    // 取该歌手热门歌曲封面作头像
                    string? cover = await GetItunesArtistCoverAsync(exactName, cancellationToken).ConfigureAwait(false);
                    results.Add(new OnlineSongResult("iTunes", artistId ?? string.Empty, exactName, string.Empty, string.Empty, cover ?? string.Empty));

                    if (results.Count >= 10)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return results;
        }

        private static async Task<string?> GetItunesArtistCoverAsync(string artistName, CancellationToken cancellationToken)
        {
            try
            {
                string trackUrl = "https://itunes.apple.com/search?term={0}&entity=musicTrack&attribute=artistTerm&limit=1";
                trackUrl = string.Format(trackUrl, Uri.EscapeDataString(artistName.Trim()));
                using HttpRequestMessage trackReq = new(HttpMethod.Get, trackUrl);
                trackReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                using HttpResponseMessage trackResp = await Http.SendAsync(trackReq, cancellationToken).ConfigureAwait(false);
                if (!trackResp.IsSuccessStatusCode)
                {
                    return null;
                }

                string trackJson = await trackResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument trackDoc = JsonDocument.Parse(trackJson);
                if (!trackDoc.RootElement.TryGetProperty("results", out JsonElement tres)
                    || tres.ValueKind != JsonValueKind.Array
                    || tres.GetArrayLength() == 0
                    || !tres[0].TryGetProperty("artworkUrl100", out JsonElement art))
                {
                    return null;
                }

                string? cover = art.GetString();
                return string.IsNullOrWhiteSpace(cover) ? null : cover.Replace("100x100bb", "600x600bb");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// MusicBrainz 艺术家搜索：/ws/2/artist 返回艺术家实体，但头像不在 MusicBrainz 内
        /// （头像通常在 fanart.tv/Wikipedia，需额外 key 或不稳定）。故本平台头像留空，
        /// 由头像窗口在展示时对「无头像」结果降级/过滤。歌手头像主力源是 iTunes。
        /// </summary>
        private static async Task<IReadOnlyList<OnlineSongResult>> SearchMusicBrainzArtistsAsync(
            string query,
            CancellationToken cancellationToken)
        {
            var results = new List<OnlineSongResult>();
            try
            {
                string url = "https://musicbrainz.org/ws/2/artist/?query={0}&limit=20&fmt=json";
                url = string.Format(url, Uri.EscapeDataString(query.Trim()));
                using HttpRequestMessage req = new(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "CelesteMusicPlayer/1.0 (https://github.com/Celeste-114514/CelesteMusicPlayer)");
                using HttpResponseMessage response = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("artists", out JsonElement artists) || artists.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                foreach (JsonElement a in artists.EnumerateArray())
                {
                    string mbid = a.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    string name = a.TryGetProperty("name", out JsonElement nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    // 优先取「人物/乐队」类型，跳过纯「专辑/单曲」等非艺术家实体
                    string type = a.TryGetProperty("type", out JsonElement tEl) ? tEl.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (type is not ("Person" or "Group"))
                    {
                        continue;
                    }

                    results.Add(new OnlineSongResult("MusicBrainz", mbid, name, string.Empty, string.Empty, string.Empty));
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return results;
        }

        private static string BuildQuery(string title, string artist)
        {
            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            return query.Trim();
        }

        /// <summary>
        /// iTunes Store Search API 搜索曲目（公开无鉴权）。用于标签编辑/在线搜索的元数据候选。
        /// 端点：https://itunes.apple.com/search?term=...&media=music&entity=song
        /// </summary>
        private static async Task<IReadOnlyList<OnlineSongResult>> SearchItunesSongsAsync(
            string title,
            string artist,
            CancellationToken cancellationToken)
        {
            var results = new List<OnlineSongResult>();
            try
            {
                string url = "https://itunes.apple.com/search?media=music&entity=song&limit=25&term={0}";
                url = string.Format(url, Uri.EscapeDataString(BuildQuery(title, artist)));
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out JsonElement resultsEl))
                {
                    return results;
                }

                foreach (JsonElement s in resultsEl.EnumerateArray())
                {
                    string trackId = s.TryGetProperty("trackId", out JsonElement idEl)
                        ? idEl.GetInt64().ToString()
                        : string.Empty;
                    string name = s.TryGetProperty("trackName", out JsonElement nEl)
                        ? nEl.GetString() ?? string.Empty
                        : string.Empty;
                    string singer = s.TryGetProperty("artistName", out JsonElement aEl)
                        ? aEl.GetString() ?? string.Empty
                        : string.Empty;
                    string album = s.TryGetProperty("collectionName", out JsonElement alEl)
                        ? alEl.GetString() ?? string.Empty
                        : string.Empty;
                    string cover = s.TryGetProperty("artworkUrl100", out JsonElement cEl)
                        ? cEl.GetString() ?? string.Empty
                        : string.Empty;
                    // 提高封面分辨率：100x100bb -> 1200x1200bb（Apple artworkUrl 尺寸上限 1200）
                    if (cover.Contains("100x100bb"))
                    {
                        cover = cover.Replace("100x100bb", "1200x1200bb");
                    }

                    if (!string.IsNullOrWhiteSpace(trackId) && !string.IsNullOrWhiteSpace(name))
                    {
                        results.Add(new OnlineSongResult("iTunes", trackId, name, singer, album, cover));
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return results;
        }


        /// <summary>
        /// MusicBrainz 公开搜索（免 key）：返回专辑粒度，封面走 Cover Art Archive（coverartarchive.org）。
        /// endpoint musicbrainz.org/ws/2/release。仅作元数据/封面候选（不提供下载与歌词）。
        /// </summary>
        private static async Task<IReadOnlyList<OnlineSongResult>> SearchMusicBrainzSongsAsync(
            string title,
            string artist,
            CancellationToken cancellationToken)
        {
            var results = new List<OnlineSongResult>();
            try
            {
                // 专辑粒度：封面直接来自 Cover Art Archive，最适合补封面/专辑标签。
                string query = string.IsNullOrWhiteSpace(artist)
                    ? Uri.EscapeDataString(title)
                    : Uri.EscapeDataString($"artist:{artist} AND release:{title}");
                string url = $"https://musicbrainz.org/ws/2/release/?query={query}&limit=20&fmt=json";
                using HttpRequestMessage req = new(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "CelesteMusicPlayer/1.0 (https://github.com/Celeste-114514/CelesteMusicPlayer)");
                using HttpResponseMessage response = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("releases", out JsonElement releases) || releases.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                foreach (JsonElement r in releases.EnumerateArray())
                {
                    string mbid = r.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    string name = r.TryGetProperty("title", out JsonElement nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    string albumArtist = string.Empty;
                    if (r.TryGetProperty("artist-credit", out JsonElement ac) && ac.ValueKind == JsonValueKind.Array)
                    {
                        var names = new List<string>();
                        foreach (JsonElement c in ac.EnumerateArray())
                        {
                            if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("name", out JsonElement cn))
                            {
                                string? v = cn.GetString();
                                if (!string.IsNullOrWhiteSpace(v))
                                {
                                    names.Add(v);
                                }
                            }
                        }

                        albumArtist = string.Join(" / ", names);
                    }

                    string groupId = r.TryGetProperty("release-group", out JsonElement rg)
                        && rg.TryGetProperty("id", out JsonElement gidEl)
                        ? gidEl.GetString() ?? string.Empty : string.Empty;
                    string cover = string.IsNullOrWhiteSpace(groupId)
                        ? string.Empty
                        : $"https://coverartarchive.org/release-group/{groupId}/front";

                    if (!string.IsNullOrWhiteSpace(mbid) && !string.IsNullOrWhiteSpace(name))
                    {
                        results.Add(new OnlineSongResult("MusicBrainz", mbid, name, albumArtist, name, cover));
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return results;
        }

        // =====================================================================
        // 歌词
        // =====================================================================

        /// <summary>
        /// 取歌词。2026-10-13 合规整改后统一走 LRClib（开放歌词库，免注册免 key）。
        /// source 参数保留仅为兼容旧调用点，不再用于区分平台。
        /// LRClib 无翻译歌词，includeTranslation 当前无对应能力，保留参数不改变行为。
        /// </summary>
        public static async Task<string> GetLyricAsync(
            string source,
            OnlineSongResult song,
            bool includeTranslation,
            CancellationToken cancellationToken = default)
        {
            return await GetLyricFromLrclibAsync(
                song.Artist,
                song.Name,
                song.Album,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        // =====================================================================
        // 歌词下载（组合）
        // =====================================================================

        /// <summary>
        /// 「询问」歌词保存策略的确认回调：传入候选 .lrc 路径，返回是否落盘。
        /// 由 UI 层（MainWindow）注入；为 null 时「询问」按「自动」处理。
        /// </summary>
        public static Func<string, Task<bool>>? LyricSaveConfirm;

        public static async Task<string?> SearchAndDownloadLyricAsync(
            string title,
            string artist,
            string saveBesideAudioPath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(saveBesideAudioPath))
            {
                return null;
            }

            try
            {
                string source = ResolveSource();
                IReadOnlyList<OnlineSongResult> hits =
                    await SearchSongsAsync(source, title, artist, cancellationToken).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return null;
                }

                // 不能直接取 hits[0]：搜索接口按热度/相关度排序，第一条常常是翻唱、
                // 同名不同歌手的另一首歌。必须按标题/歌手打分挑最贴近本地标签的那个。
                OnlineSongResult best = PickBestMatch(hits, title, artist);
                string lyric = await GetLyricAsync(
                    source,
                    best,
                    AppSettingsStore.Load().ShowLyricTranslate,
                    cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(lyric))
                {
                    return null;
                }

                string? lrcPath = ResolveLyricSavePath(saveBesideAudioPath);
                if (lrcPath == null)
                {
                    // 保存策略为「不保存」：仅本次会话使用（上层直接忽略返回值）
                    return null;
                }

                // 「询问」策略：歌词找到了才问是否落盘（先问后写，反悔不留垃圾文件）。
                // 回调未注入（非 UI 上下文调用）时按「自动」处理。
                if (LyricSaveConfirm != null
                    && string.Equals(AppSettingsStore.Load().LyricSavePolicy, "Ask", StringComparison.OrdinalIgnoreCase))
                {
                    bool confirmed = await LyricSaveConfirm(lrcPath).ConfigureAwait(false);
                    if (!confirmed)
                    {
                        return null; // 用户选不保存：仅本次会话使用
                    }
                }

                string? dir = Path.GetDirectoryName(lrcPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await File.WriteAllTextAsync(lrcPath, lyric, cancellationToken).ConfigureAwait(false);
                return lrcPath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从搜索结果里挑最贴近本地曲目的那一条。
        /// 搜索接口按热度/相关度排序，第一条常常是翻唱、现场版或同名不同歌手的另一首歌，
        /// 直接取 hits[0] 就会下到别人的歌词。这里按标题、歌手加权打分挑最像的；
        /// 全都对不上时靠位置分退回第一条，不会比原来的行为更差。
        /// </summary>
        private static OnlineSongResult PickBestMatch(
            IReadOnlyList<OnlineSongResult> hits,
            string title,
            string artist)
        {
            if (hits == null || hits.Count == 0)
            {
                return null!;
            }

            if (hits.Count == 1)
            {
                return hits[0];
            }

            string wantTitle = NormalizeForMatch(title);
            string wantArtist = NormalizeForMatch(artist);

            int bestScore = int.MinValue;
            OnlineSongResult best = hits[0];

            for (int i = 0; i < hits.Count; i++)
            {
                OnlineSongResult h = hits[i];
                string gotTitle = NormalizeForMatch(h.Name);
                string gotArtist = NormalizeForMatch(h.Artist);

                int score = 0;

                // 标题：完全一致最高；其次互相包含，用来容纳「歌名」vs「歌名 (Live)」这类差异
                if (wantTitle.Length > 0 && gotTitle.Length > 0)
                {
                    if (string.Equals(wantTitle, gotTitle, StringComparison.Ordinal))
                    {
                        score += 100;
                    }
                    else if (gotTitle.Contains(wantTitle, StringComparison.Ordinal)
                             || wantTitle.Contains(gotTitle, StringComparison.Ordinal))
                    {
                        score += 55;
                    }
                }

                // 歌手：只在本地有歌手标签时参与，避免缺标签时误伤
                if (wantArtist.Length > 0 && gotArtist.Length > 0)
                {
                    if (string.Equals(wantArtist, gotArtist, StringComparison.Ordinal))
                    {
                        score += 60;
                    }
                    else if (gotArtist.Contains(wantArtist, StringComparison.Ordinal)
                             || wantArtist.Contains(gotArtist, StringComparison.Ordinal))
                    {
                        score += 30;
                    }
                }

                // 搜索结果本身已按相关度排序，给靠前的一点位置分用于打破平局
                score += Math.Max(0, 10 - i);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = h;
                }
            }

            return best;
        }

        /// <summary>歌词/曲目比对用的归一化：去空白、全角转半角、统一小写。</summary>
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

                // 全角 ASCII 区间 → 半角
                char v = (c >= '！' && c <= '～') ? (char)(c - 0xFEE0) : c;
                buf[n++] = char.ToLowerInvariant(v);
            }

            return new string(buf, 0, n);
        }

        /// <summary>按设置决定歌词保存位置；返回 null 表示不保存（LyricSavePolicy=None 或未开启保存到歌曲目录）。</summary>
        private static string? ResolveLyricSavePath(string audioPath)
        {
            AppSettingsState s = AppSettingsStore.Load();
            if (string.Equals(s.LyricSavePolicy, "None", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string name = Path.GetFileNameWithoutExtension(audioPath);
            if (!string.IsNullOrWhiteSpace(s.LyricFolder))
            {
                return Path.Combine(s.LyricFolder, name + ".lrc");
            }

            if (s.SaveLyricToSongFolder)
            {
                string dir = Path.GetDirectoryName(audioPath) ?? string.Empty;
                return Path.Combine(dir, name + ".lrc");
            }

            return null;
        }

        // =====================================================================
        // 封面
        // =====================================================================

        public static async Task<string?> GetCoverUrlAsync(OnlineSongResult song, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(song.CoverUrl))
            {
                return song.CoverUrl;
            }

            // 2026-10-13 合规整改：网易云封面接口已移除，无内嵌 URL 时返回 null。
            return null;
        }

        /// <summary>
        /// 按歌手名搜索艺术家头像 URL。2026-10-13 起只走 iTunes（官方 Search API）：
        /// musicArtist 实体本身不含 artworkUrl，故取其热门歌曲封面（专辑封面）作头像，
        /// 优点是艺人 id 精确（如「東京事変」与「椎名林檎」不再混淆，各用各的专辑图）。
        /// </summary>
        public static async Task<string?> SearchArtistAvatarUrlAsync(string artistName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return null;
            }

            return await SearchItunesArtistAvatarUrlAsync(artistName, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>iTunes 精确区分艺人：先按歌手名找 artistId/英文名，再查该艺术家热门歌曲取封面（artworkUrl100→600）。</summary>
        private static async Task<string?> SearchItunesArtistAvatarUrlAsync(
            string artistName,
            CancellationToken cancellationToken)
        {
            try
            {
                // 1) 定位艺术家：musicArtist 返回精确 artistId（不混淆同名/关联艺人）。
                string locateUrl = "https://itunes.apple.com/search?term={0}&entity=musicArtist&limit=1";
                locateUrl = string.Format(locateUrl, Uri.EscapeDataString(artistName.Trim()));
                using HttpRequestMessage locateReq = new(HttpMethod.Get, locateUrl);
                locateReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                using HttpResponseMessage locateResp = await Http.SendAsync(locateReq, cancellationToken).ConfigureAwait(false);
                if (!locateResp.IsSuccessStatusCode)
                {
                    return null;
                }

                string locateJson = await locateResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument locateDoc = JsonDocument.Parse(locateJson);
                if (!locateDoc.RootElement.TryGetProperty("results", out JsonElement res)
                    || res.ValueKind != JsonValueKind.Array
                    || res.GetArrayLength() == 0)
                {
                    return null;
                }

                JsonElement artist = res[0];
                string? exactName = artist.TryGetProperty("artistName", out JsonElement an) ? an.GetString() : null;
                if (string.IsNullOrWhiteSpace(exactName))
                {
                    return null;
                }

                // 2) 用精确英文名查该艺术家热门歌曲，取其封面 artworkUrl100 → 600x600。
                string trackUrl = "https://itunes.apple.com/search?term={0}&entity=musicTrack&attribute=artistTerm&limit=1";
                trackUrl = string.Format(trackUrl, Uri.EscapeDataString(exactName.Trim()));
                using HttpRequestMessage trackReq = new(HttpMethod.Get, trackUrl);
                trackReq.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                using HttpResponseMessage trackResp = await Http.SendAsync(trackReq, cancellationToken).ConfigureAwait(false);
                if (!trackResp.IsSuccessStatusCode)
                {
                    return null;
                }

                string trackJson = await trackResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument trackDoc = JsonDocument.Parse(trackJson);
                if (!trackDoc.RootElement.TryGetProperty("results", out JsonElement tres)
                    || tres.ValueKind != JsonValueKind.Array
                    || tres.GetArrayLength() == 0
                    || !tres[0].TryGetProperty("artworkUrl100", out JsonElement art))
                {
                    return null;
                }

                string? cover = art.GetString();
                if (string.IsNullOrWhiteSpace(cover))
                {
                    return null;
                }

                // 提高分辨率：100x100bb -> 600x600bb（Apple artworkUrl 上限 1200，600 够清晰）
                return cover.Replace("100x100bb", "600x600bb");
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return null;
        }

        /// <summary>把歌词下载到指定文件夹（搜索窗口用）：先搜索歌曲再取歌词，返回保存路径。</summary>
        public static async Task<string?> DownloadLyricToFolderAsync(
            string source,
            string title,
            string artist,
            string folder,
            bool includeTranslation,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            try
            {
                // 缓存：目标 .lrc 已存在 → 直接返回，不再联网搜索/下载。
                string cachedPath = Path.Combine(folder, SanitizeFileName(title + " - " + artist) + ".lrc");
                if (File.Exists(cachedPath))
                {
                    return cachedPath;
                }

                IReadOnlyList<OnlineSongResult> hits = await SearchSongsAsync(source, title, artist, cancellationToken).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return null;
                }

                string lyric = await GetLyricAsync(source, hits[0], includeTranslation, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(lyric))
                {
                    return null;
                }

                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(cachedPath, lyric, cancellationToken).ConfigureAwait(false);
                return cachedPath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把封面下载到指定文件夹（搜索窗口用）：先搜索歌曲再取封面，返回保存路径。</summary>
        public static async Task<string?> DownloadCoverToFolderAsync(
            string source,
            string title,
            string artist,
            string folder,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            try
            {
                IReadOnlyList<OnlineSongResult> hits = await SearchSongsAsync(source, title, artist, cancellationToken).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return null;
                }

                string? coverUrl = await GetCoverUrlAsync(hits[0], cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(coverUrl))
                {
                    return null;
                }

                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, SanitizeFileName(title + " - " + artist) + ".jpg");
                return await DownloadCoverAsync(coverUrl, path, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "未知歌曲";
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            name = name.Trim().TrimEnd('.', ' ');
            return string.IsNullOrWhiteSpace(name) ? "未知歌曲" : name;
        }

        public static async Task<string?> DownloadCoverAsync(
            string coverUrl,
            string savePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                return null;
            }

            // 缓存：目标文件已存在 → 直接返回，不再联网下载。
            if (File.Exists(savePath))
            {
                return savePath;
            }

            try
            {
                using HttpResponseMessage response = await Http.GetAsync(coverUrl, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string? dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream file = File.Create(savePath);
                await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                return savePath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按设置决定封面保存位置；返回 null 表示不保存外置封面（仅嵌入标签）。</summary>
        private static string? ResolveCoverSavePath(string audioPath)
        {
            AppSettingsState s = AppSettingsStore.Load();
            string name = Path.GetFileNameWithoutExtension(audioPath);
            if (!string.IsNullOrWhiteSpace(s.CoverFolder))
            {
                return Path.Combine(s.CoverFolder, name + ".jpg");
            }

            if (s.SaveCoverToSongFolder)
            {
                string dir = Path.GetDirectoryName(audioPath) ?? string.Empty;
                return Path.Combine(dir, name + ".jpg");
            }

            return null;
        }

        /// <summary>按当前来源搜索并下载封面到指定位置，同时尝试嵌入到标签。</summary>
        public static async Task<bool> DownloadAndEmbedCoverAsync(
            string title,
            string artist,
            string audioPath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            {
                return false;
            }

            try
            {
                string source = ResolveSource();
                IReadOnlyList<OnlineSongResult> hits =
                    await SearchSongsAsync(source, title, artist, cancellationToken).ConfigureAwait(false);
                if (hits.Count == 0)
                {
                    return false;
                }

                OnlineSongResult song = hits[0];
                string? coverUrl = song.CoverUrl;
                if (string.IsNullOrWhiteSpace(coverUrl))
                {
                    // 2026-10-13 合规整改：网易云封面接口已移除，不再补取。
                    coverUrl = null;
                }

                if (string.IsNullOrWhiteSpace(coverUrl))
                {
                    return false;
                }

                string? coverBeside = ResolveCoverSavePath(audioPath);
                byte[]? bytes = null;
                if (coverBeside != null)
                {
                    string? saved = await DownloadCoverAsync(coverUrl, coverBeside, cancellationToken).ConfigureAwait(false);
                    if (saved != null && File.Exists(saved))
                    {
                        bytes = await File.ReadAllBytesAsync(saved, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (bytes == null || bytes.Length == 0)
                {
                    // 不保存外置封面时：临时下载仅用于嵌入标签
                    string temp = Path.Combine(
                        Path.GetTempPath(),
                        "celeste-cover-" + Guid.NewGuid().ToString("N") + ".jpg");
                    string? tmpSaved = await DownloadCoverAsync(coverUrl, temp, cancellationToken).ConfigureAwait(false);
                    if (tmpSaved == null || !File.Exists(tmpSaved))
                    {
                        return false;
                    }

                    bytes = await File.ReadAllBytesAsync(tmpSaved, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        File.Delete(tmpSaved);
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }
                }

                if (bytes == null || bytes.Length == 0)
                {
                    return false;
                }

                try
                {
                    using TagLib.File tagFile = TagLib.File.Create(audioPath);
                    tagFile.Tag.Pictures = new TagLib.IPicture[]
                    {
                        new TagLib.Picture(new TagLib.ByteVector(bytes))
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = "image/jpeg",
                            Description = "Cover"
                        }
                    };
                    tagFile.Save();
                }
                catch
                {
                    // 外置封面已保存即可
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把指定封面 URL 下载并嵌入到音频标签（供标签编辑器使用选中结果封面，避免二次搜索）。</summary>
        public static async Task<bool> EmbedCoverUrlAsync(
            string audioPath,
            string coverUrl,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath) || string.IsNullOrWhiteSpace(coverUrl))
            {
                return false;
            }

            try
            {
                string? savePath = ResolveCoverSavePath(audioPath);
                byte[]? bytes = null;
                if (savePath != null)
                {
                    string? saved = await DownloadCoverAsync(coverUrl, savePath, cancellationToken).ConfigureAwait(false);
                    if (saved != null && File.Exists(saved))
                    {
                        bytes = await File.ReadAllBytesAsync(saved, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (bytes == null || bytes.Length == 0)
                {
                    string temp = Path.Combine(Path.GetTempPath(), "celeste-cover-" + Guid.NewGuid().ToString("N") + ".jpg");
                    string? tmpSaved = await DownloadCoverAsync(coverUrl, temp, cancellationToken).ConfigureAwait(false);
                    if (tmpSaved == null || !File.Exists(tmpSaved))
                    {
                        return false;
                    }

                    bytes = await File.ReadAllBytesAsync(tmpSaved, cancellationToken).ConfigureAwait(false);
                    try { File.Delete(tmpSaved); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }
                }

                if (bytes == null || bytes.Length == 0)
                {
                    return false;
                }

                try
                {
                    using TagLib.File tagFile = TagLib.File.Create(audioPath);
                    tagFile.Tag.Pictures = new TagLib.IPicture[]
                    {
                        new TagLib.Picture(new TagLib.ByteVector(bytes))
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = "image/jpeg",
                            Description = "Cover"
                        }
                    };
                    tagFile.Save();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>MusicBrainz 限速闸门：官方硬性要求每秒最多 1 个请求，超限会封 IP。</summary>
        private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
        private static long _musicBrainzNextFreeTick;

        /// <summary>带限速的 MusicBrainz GET。串行排队 + 最小 1.1 秒间隔。internal：在线标签页也走这把闸。</summary>
        internal static async Task<HttpResponseMessage> MusicBrainzGetAsync(
            string url,
            CancellationToken cancellationToken)
        {
            await MusicBrainzGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                long wait = _musicBrainzNextFreeTick - Environment.TickCount64;
                if (wait > 0)
                {
                    await Task.Delay((int)Math.Min(wait, 2000), cancellationToken).ConfigureAwait(false);
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", CelesteUserAgent);
                HttpResponseMessage resp = await Http.SendAsync(req, cancellationToken).ConfigureAwait(false);
                _musicBrainzNextFreeTick = Environment.TickCount64 + 1100;
                return resp;
            }
            finally
            {
                MusicBrainzGate.Release();
            }
        }

        /// <summary>去掉 bio 里夹带的 HTML 标签与尾部来源链接。</summary>
        private static string StripHtml(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            string text = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", string.Empty);
            text = System.Net.WebUtility.HtmlDecode(text);

            int cut = text.IndexOf("Read more", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0)
            {
                text = text.Substring(0, cut);
            }

            return text.Trim();
        }

        /// <summary>Cover Art Archive 封面 URL（按 release MBID 直接拼，免 key）。</summary>
        public static string? CoverArtArchiveUrl(string? releaseMbid, string size = "500")
        {
            if (string.IsNullOrWhiteSpace(releaseMbid))
            {
                return null;
            }

            return "https://coverartarchive.org/release/" + releaseMbid + "/front-" + size;
        }
    }
}
