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
    /// <summary>歌曲搜索结果：Source 为 NetEase / QQ / iTunes。</summary>
    public sealed record OnlineSongResult(
        string Source,
        string SongId,
        string Name,
        string Artist,
        string Album,
        string CoverUrl);

    /// <summary>
    /// 在线歌词 / 封面下载。按设置 LyricDownloadService 选择来源：
    /// NetEase（网易云）、QQ（QQ音乐）、Kugou（酷狗音乐）。
    /// </summary>
    public static class OnlineMusicApi
    {
        private static readonly HttpClient Http = CreateClient();
        private const string NetEaseReferer = "https://music.163.com/";
        private const string QqReferer = "https://y.qq.com/portal/player.html";

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            // ⚠️ UA 里不要带应用名（如 "CelesteMusicPlayer/1.0"）：网易云的老公开接口
            // （music.163.com/api/*）对带自定义标识的 UA 上风控，返回 {"code":-462}
            // 滑块验证页（HTTP 200 但没有 artists 字段），解析出来就是 0 命中 ——
            // 2026-10-12 实测：换标准浏览器 UA 后立刻恢复， curl 带同样的应用 UA 也被拦。
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", NetEaseReferer);
            return client;
        }

        private static string ResolveSource()
        {
            string s = AppSettingsStore.Load().LyricDownloadService;
            return string.Equals(s, "QQ", StringComparison.Ordinal) ? s : "NetEase";
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
                "QQ" => await SearchQqSongsAsync(title, artist, cancellationToken).ConfigureAwait(false),
                "iTunes" => await SearchItunesSongsAsync(title, artist, cancellationToken).ConfigureAwait(false),
                "MusicBrainz" => await SearchMusicBrainzSongsAsync(title, artist, cancellationToken).ConfigureAwait(false),
                _ => await SearchNetEaseSongsAsync(title, artist, cancellationToken).ConfigureAwait(false)
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
                "QQ" => await SearchQqArtistsAsync(query, cancellationToken).ConfigureAwait(false),
                "iTunes" => await SearchItunesArtistsAsync(query, cancellationToken).ConfigureAwait(false),
                "MusicBrainz" => await SearchMusicBrainzArtistsAsync(query, cancellationToken).ConfigureAwait(false),
                _ => await SearchNetEaseArtistsAsync(query, cancellationToken).ConfigureAwait(false)
            };
        }

        /// <summary>
        /// 艺术家概览（简介）。2026-10-12 第二轮改版起酷狗优先、网易云兜底：
        /// 网易云的老公开接口（search/get + artist/head/info）对程序化访问上了风控，
        /// 间歇性返回 {"code":-462}（滑块验证页，HTTP 200 但没有 artists），
        /// .NET / curl 都无法稳定绕过（实测连 curl 都被概率性拦）。
        /// 酷狗 mobilecdn 的歌手接口没有风控、国内直连稳定，且简介是中文。
        /// source 参数预留（用户说过后续再加别的平台的设置），当前两种都走同一条链路。
        /// 拿不到就返回 null（调用方显示「暂无该艺术家的概览」），不抛异常。
        /// </summary>
        public static async Task<string?> GetArtistOverviewAsync(
            string source,
            string artistName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return null;
            }

            string? brief = await GetKugouArtistBriefAsync(artistName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(brief))
            {
                return brief;
            }

            AppLog.Info("net", "artist-overview: 酷狗无简介，回退网易云 name=" + artistName);
            return await GetNetEaseArtistBriefAsync(artistName, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 酷狗歌手简介：先按名字搜歌手拿 singerid，再取 info 里的 profile。
        /// mobilecdn 这套接口没有风控、国内直连稳定（2026-10-12 实测，椎名林檎 /
        /// 周杰伦都能拿到中文简介）。酷狗对 Referer 敏感，单独发自己的 Referer。
        /// </summary>
        private static async Task<string?> GetKugouArtistBriefAsync(
            string artistName,
            CancellationToken cancellationToken)
        {
            try
            {
                string searchUrl = "http://mobilecdn.kugou.com/api/v3/search/singer?keyword="
                    + Uri.EscapeDataString(artistName.Trim()) + "&page=1&pagesize=5";
                using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchRequest.Headers.TryAddWithoutValidation("Referer", "http://www.kugou.com/");
                using HttpResponseMessage searchResponse =
                    await Http.SendAsync(searchRequest, cancellationToken).ConfigureAwait(false);
                if (!searchResponse.IsSuccessStatusCode)
                {
                    AppLog.Info("net", "kugou-brief: 歌手搜索 http="
                        + ((int)searchResponse.StatusCode).ToString());
                    return null;
                }

                string searchJson = await searchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                string? singerId = null;
                string? singerName = null;
                using (JsonDocument doc = JsonDocument.Parse(searchJson))
                {
                    if (doc.RootElement.TryGetProperty("data", out JsonElement data)
                        && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement s in data.EnumerateArray())
                        {
                            singerId = s.TryGetProperty("singerid", out JsonElement idEl)
                                ? idEl.ToString()
                                : null;
                            singerName = s.TryGetProperty("singername", out JsonElement nEl)
                                ? nEl.GetString()
                                : null;
                            if (!string.IsNullOrEmpty(singerId))
                            {
                                break;
                            }
                        }
                    }
                }

                AppLog.Info("net", "kugou-brief: 搜索歌手 name=" + artistName
                    + " 选中=" + (singerName ?? "无") + " id=" + (singerId ?? "无"));

                if (string.IsNullOrEmpty(singerId))
                {
                    return null;
                }

                string infoUrl = "http://mobilecdn.kugou.com/api/v3/singer/info?singerid="
                    + Uri.EscapeDataString(singerId) + "&with_res=true";
                using var infoRequest = new HttpRequestMessage(HttpMethod.Get, infoUrl);
                infoRequest.Headers.TryAddWithoutValidation("Referer", "http://www.kugou.com/");
                using HttpResponseMessage infoResponse =
                    await Http.SendAsync(infoRequest, cancellationToken).ConfigureAwait(false);
                if (!infoResponse.IsSuccessStatusCode)
                {
                    AppLog.Info("net", "kugou-brief: info http="
                        + ((int)infoResponse.StatusCode).ToString());
                    return null;
                }

                string infoJson = await infoResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument infoDoc = JsonDocument.Parse(infoJson);
                if (infoDoc.RootElement.TryGetProperty("data", out JsonElement singer)
                    && singer.TryGetProperty("profile", out JsonElement profileEl))
                {
                    string? profile = profileEl.GetString();
                    if (!string.IsNullOrWhiteSpace(profile))
                    {
                        AppLog.Info("net", "kugou-brief: 命中 id=" + singerId
                            + " profileLen=" + profile.Trim().Length.ToString());
                        return profile.Trim();
                    }
                }

                AppLog.Info("net", "kugou-brief: 无 profile id=" + singerId);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return null;
        }

        /// <summary>
        /// 网易云歌手简介：先按名字搜到歌手 id，再取该歌手的 briefDesc。
        /// ⚠️ 用的是老的公开 API（与搜索同一批），不保证长期可用 —— 失败一律返回 null，
        /// 界面上只是「暂无概览」，不影响其它功能。
        /// </summary>
        private static async Task<string?> GetNetEaseArtistBriefAsync(
            string artistName,
            CancellationToken cancellationToken)
        {
            try
            {
                IReadOnlyList<OnlineSongResult> hits =
                    await SearchNetEaseArtistsAsync(artistName, cancellationToken).ConfigureAwait(false);

                if (hits.Count == 0)
                {
                    // 网易云对程序化访问是概率性风控：命中时就返回 {"code":-462} 验证页
                    // （HTTP 200、无 artists），解析出来就是 0 个歌手。退避 1.2s 再试一次，
                    // 多数情况第二发就过（2026-10-12 实测，curl 同 IP 也被间歇性拦）。
                    AppLog.Info("net", "netease-brief: 首轮 0 命中，1.2s 后重试 name=" + artistName);
                    try
                    {
                        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    hits = await SearchNetEaseArtistsAsync(artistName, cancellationToken).ConfigureAwait(false);
                }

                string? id = null;
                foreach (OnlineSongResult h in hits)
                {
                    if (!string.IsNullOrWhiteSpace(h.SongId))
                    {
                        id = h.SongId;
                        break;
                    }
                }

                AppLog.Info("net", "netease-brief: 搜索歌手 name=" + artistName
                    + " hits=" + hits.Count.ToString() + " 选中id=" + (id ?? "无"));

                if (string.IsNullOrEmpty(id))
                {
                    return null;
                }

                string url = "https://music.163.com/api/artist/head/info/get?id=" + Uri.EscapeDataString(id);
                using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                AppLog.Info("net", "netease-brief: head/info id=" + id
                    + " http=" + ((int)response.StatusCode).ToString());
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                // 两种返回形态都兼容：{ artist:{briefDesc} } 与 { data:{ artist:{briefDesc} } }
                if (TryFindBriefDesc(doc.RootElement, out string? brief) && !string.IsNullOrWhiteSpace(brief))
                {
                    AppLog.Info("net", "netease-brief: 命中 id=" + id
                        + " briefLen=" + brief.Length.ToString());
                    return brief.Trim();
                }

                AppLog.Info("net", "netease-brief: head/info 里没有 briefDesc id=" + id);
            }
            catch (Exception caught)
            {
                AppLog.Error("net", "netease-brief: 异常 name=" + artistName, caught);
                global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught);
            }

            return null;
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

        /// <summary>网易云 type=100 歌手搜索：返回多个歌手的真实头像 picUrl（含别名）。</summary>
        private static async Task<IReadOnlyList<OnlineSongResult>> SearchNetEaseArtistsAsync(
            string query,
            CancellationToken cancellationToken)
        {
            var results = new List<OnlineSongResult>();
            try
            {
                string url = "https://music.163.com/api/search/get?s={0}&type=100&limit=20";
                url = string.Format(url, Uri.EscapeDataString(query.Trim()));
                using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out JsonElement result)
                    || !result.TryGetProperty("artists", out JsonElement artists)
                    || artists.ValueKind != JsonValueKind.Array)
                {
                    return results;
                }

                foreach (JsonElement a in artists.EnumerateArray())
                {
                    string id = a.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt64(out long aid)
                        ? aid.ToString() : string.Empty;
                    string name = a.TryGetProperty("name", out JsonElement nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    string pic = a.TryGetProperty("picUrl", out JsonElement pEl) ? pEl.GetString() ?? string.Empty : string.Empty;

                    // 部分结果 picUrl 可能为空，跳过；有别名时拼进显示名便于区分。
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string displayName = name;
                    if (a.TryGetProperty("alias", out JsonElement aliasEl) && aliasEl.ValueKind == JsonValueKind.Array
                        && aliasEl.GetArrayLength() > 0)
                    {
                        string? first = aliasEl[0].GetString();
                        if (!string.IsNullOrWhiteSpace(first))
                        {
                            displayName = name + "（" + first + "）";
                        }
                    }

                    results.Add(new OnlineSongResult("NetEase", id, displayName, string.Empty, string.Empty, pic));
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return results;
        }

        /// <summary>QQ 音乐歌手搜索：优先取 zhida_singer（精确命中歌手的头像），否则回退歌手列表头像。</summary>
        private static async Task<IReadOnlyList<OnlineSongResult>> SearchQqArtistsAsync(
            string query,
            CancellationToken cancellationToken)
        {
            var results = new List<OnlineSongResult>();
            try
            {
                string url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={0}&format=json&p=1&n=20&cr=1"
                    + "&g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8&outCharset=utf-8&notice=0"
                    + "&platform=yqq.json&needNewCode=0&catZhida=1&t=0";
                url = string.Format(url, Uri.EscapeDataString(query.Trim()));
                using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data))
                {
                    return results;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1) 精确命中歌手：data.zhida.zhida_singer（含 singerName + singerPic）
                if (data.TryGetProperty("zhida", out JsonElement zhida)
                    && zhida.TryGetProperty("zhida_singer", out JsonElement zs)
                    && zs.ValueKind == JsonValueKind.Object)
                {
                    string name = zs.TryGetProperty("singerName", out JsonElement nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
                    string mid = zs.TryGetProperty("singerMID", out JsonElement midEl) ? midEl.GetString() ?? string.Empty : string.Empty;
                    string pic = zs.TryGetProperty("singerPic", out JsonElement pEl) ? pEl.GetString() ?? string.Empty : string.Empty;

                    if (!string.IsNullOrWhiteSpace(name) && seen.Add("zhida:" + name))
                    {
                        results.Add(new OnlineSongResult("QQ", mid, name, string.Empty, string.Empty, pic));
                    }
                }

                // 2) 兜底：歌曲列表里的歌手（singer 数组带 mid，头像用 singerPic 模板）
                if (data.TryGetProperty("song", out JsonElement songObj)
                    && songObj.TryGetProperty("list", out JsonElement list)
                    && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement s in list.EnumerateArray())
                    {
                        if (!s.TryGetProperty("singer", out JsonElement singers) || singers.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (JsonElement a in singers.EnumerateArray())
                        {
                            string name = a.TryGetProperty("name", out JsonElement nmEl) ? nmEl.GetString() ?? string.Empty : string.Empty;
                            string mid = a.TryGetProperty("mid", out JsonElement midEl) ? midEl.GetString() ?? string.Empty : string.Empty;
                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mid))
                            {
                                continue;
                            }

                            string pic = $"https://y.gtimg.cn/music/photo_new/T001R300x300M000{mid}.jpg";
                            if (seen.Add("song:" + name))
                            {
                                results.Add(new OnlineSongResult("QQ", mid, name, string.Empty, string.Empty, pic));
                            }
                        }

                        if (results.Count >= 20)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return results;
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
        /// 由头像窗口在展示时对「无头像」结果降级/过滤。歌手搜索主力仍是网易云/QQ/iTunes。
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

        private static async Task<IReadOnlyList<OnlineSongResult>> SearchNetEaseSongsAsync(
            string title,
            string artist,
            CancellationToken cancellationToken)
        {
            var results = new List<OnlineSongResult>();
            try
            {
                string url = "https://music.163.com/api/search/get?s={0}&type=1&limit=20";
                url = string.Format(url, Uri.EscapeDataString(BuildQuery(title, artist)));
                using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out JsonElement result)
                    || !result.TryGetProperty("songs", out JsonElement songs))
                {
                    return results;
                }

                foreach (JsonElement song in songs.EnumerateArray())
                {
                    long id = song.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt64(out long parsedId)
                        ? parsedId
                        : 0;
                    string name = song.TryGetProperty("name", out JsonElement nameEl)
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;
                    string album = song.TryGetProperty("album", out JsonElement albumEl)
                        && albumEl.TryGetProperty("name", out JsonElement albumNameEl)
                        ? albumNameEl.GetString() ?? string.Empty
                        : string.Empty;

                    string songArtist = string.Empty;
                    if (song.TryGetProperty("artists", out JsonElement artistsEl) && artistsEl.ValueKind == JsonValueKind.Array)
                    {
                        var names = new List<string>();
                        foreach (JsonElement a in artistsEl.EnumerateArray())
                        {
                            if (a.TryGetProperty("name", out JsonElement artistNameEl))
                            {
                                string? n = artistNameEl.GetString();
                                if (!string.IsNullOrWhiteSpace(n))
                                {
                                    names.Add(n);
                                }
                            }
                        }

                        songArtist = string.Join(" / ", names);
                    }

                    if (id > 0 && !string.IsNullOrWhiteSpace(name))
                    {
                        results.Add(new OnlineSongResult("NetEase", id.ToString(), name, songArtist, album, string.Empty));
                    }
                }

                // 批量补齐封面：搜索接口的 album 不含 picUrl（仅 picId），
                // 用歌曲详情接口一次拉取多首封面的 picUrl 回填结果。
                if (results.Count > 0)
                {
                    try
                    {
                        string ids = string.Join(",", results.Select(r => r.SongId));
                        using HttpResponseMessage detailResp = await Http.GetAsync(
                            "https://music.163.com/api/song/detail/?ids=[" + ids + "]", cancellationToken).ConfigureAwait(false);
                        if (detailResp.IsSuccessStatusCode)
                        {
                            string detailJson = await detailResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                            using JsonDocument detailDoc = JsonDocument.Parse(detailJson);
                            if (detailDoc.RootElement.TryGetProperty("songs", out JsonElement detailSongs)
                                && detailSongs.ValueKind == JsonValueKind.Array)
                            {
                                var covers = new Dictionary<string, string>();
                                foreach (JsonElement s in detailSongs.EnumerateArray())
                                {
                                    if (s.TryGetProperty("id", out JsonElement idEl)
                                        && idEl.TryGetInt64(out long sid)
                                        && s.TryGetProperty("album", out JsonElement alEl)
                                        && alEl.TryGetProperty("picUrl", out JsonElement picEl))
                                    {
                                        string? pic = picEl.GetString();
                                        if (!string.IsNullOrWhiteSpace(pic))
                                        {
                                            covers[sid.ToString()] = pic;
                                        }
                                    }
                                }

                                if (covers.Count > 0)
                                {
                                    for (int i = 0; i < results.Count; i++)
                                    {
                                        if (covers.TryGetValue(results[i].SongId, out string? pic))
                                        {
                                            results[i] = results[i] with { CoverUrl = pic };
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return results;
        }

        private static async Task<IReadOnlyList<OnlineSongResult>> SearchQqSongsAsync(
            string title,
            string artist,
            CancellationToken cancellationToken)
        {
            var results = new List<OnlineSongResult>();
            try
            {
                string url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={0}&format=json&p=1&n=20&cr=1"
                    + "&g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8&outCharset=utf-8&notice=0"
                    + "&platform=yqq.json&needNewCode=0";
                url = string.Format(url, Uri.EscapeDataString(BuildQuery(title, artist)));
                using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data)
                    || !data.TryGetProperty("song", out JsonElement songObj)
                    || !songObj.TryGetProperty("list", out JsonElement list))
                {
                    return results;
                }

                foreach (JsonElement s in list.EnumerateArray())
                {
                    string songmid = s.TryGetProperty("songmid", out JsonElement midEl)
                        ? midEl.GetString() ?? string.Empty
                        : string.Empty;
                    string name = s.TryGetProperty("songname", out JsonElement nameEl)
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;
                    string album = s.TryGetProperty("albumname", out JsonElement albumEl)
                        ? albumEl.GetString() ?? string.Empty
                        : string.Empty;
                    string albummid = s.TryGetProperty("albummid", out JsonElement amidEl)
                        ? amidEl.GetString() ?? string.Empty
                        : string.Empty;

                    string singer = string.Empty;
                    if (s.TryGetProperty("singer", out JsonElement singers) && singers.ValueKind == JsonValueKind.Array)
                    {
                        var names = new List<string>();
                        foreach (JsonElement a in singers.EnumerateArray())
                        {
                            if (a.TryGetProperty("name", out JsonElement nEl))
                            {
                                string? n = nEl.GetString();
                                if (!string.IsNullOrWhiteSpace(n))
                                {
                                    names.Add(n);
                                }
                            }
                        }

                        singer = string.Join(" / ", names);
                    }

                    if (!string.IsNullOrWhiteSpace(songmid) && !string.IsNullOrWhiteSpace(name))
                    {
                        string cover = string.IsNullOrWhiteSpace(albummid)
                            ? string.Empty
                            : $"https://y.gtimg.cn/music/photo_new/T002R300x300M000{albummid}.jpg";
                        results.Add(new OnlineSongResult("QQ", songmid, name, singer, album, cover));
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return results;
        }

        private static async Task<IReadOnlyList<OnlineSongResult>> SearchKugouSongsAsync(
            string title,
            string artist,
            CancellationToken cancellationToken)
        {
            var results = new List<OnlineSongResult>();
            try
            {
                string url = "https://songsearch.kugou.com/song_search_v2?keyword={0}&page=1&pagesize=20";
                url = string.Format(url, Uri.EscapeDataString(BuildQuery(title, artist)));
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.kugou.com/");
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return results;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data)
                    || !data.TryGetProperty("lists", out JsonElement lists))
                {
                    return results;
                }

                foreach (JsonElement s in lists.EnumerateArray())
                {
                    string hash = s.TryGetProperty("FileHash", out JsonElement hashEl)
                        ? hashEl.GetString() ?? string.Empty
                        : string.Empty;
                    string name = s.TryGetProperty("SongName", out JsonElement nameEl)
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;
                    string singer = s.TryGetProperty("SingerName", out JsonElement singerEl)
                        ? singerEl.GetString() ?? string.Empty
                        : string.Empty;
                    string album = s.TryGetProperty("AlbumName", out JsonElement albumEl)
                        ? albumEl.GetString() ?? string.Empty
                        : string.Empty;
                    string image = s.TryGetProperty("Image", out JsonElement imgEl)
                        ? imgEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (image.Contains("{size}"))
                    {
                        image = image.Replace("{size}", "400");
                    }

                    if (!string.IsNullOrWhiteSpace(hash) && !string.IsNullOrWhiteSpace(name))
                    {
                        results.Add(new OnlineSongResult("Kugou", hash, name, singer, album, image));
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return results;
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

        public static async Task<string> GetLyricAsync(
            string source,
            OnlineSongResult song,
            bool includeTranslation,
            CancellationToken cancellationToken = default)
        {
            return source switch
            {
                "QQ" => await GetQqLyricAsync(song.SongId, includeTranslation, cancellationToken).ConfigureAwait(false),
                // 酷狗有独立的歌词接口，必须走自己的实现：它的 SongId 是 hash，
                // 若误落到下面的网易云分支会被 long.TryParse 判为非法而永远拿不到歌词。
                "Kugou" => await GetKugouLyricAsync(song.Name, song.SongId, cancellationToken).ConfigureAwait(false),
                "MusicBrainz" or "iTunes" => string.Empty,
                _ => await GetNetEaseLyricAsync(song.SongId, includeTranslation, cancellationToken).ConfigureAwait(false)
            };
        }

        private static async Task<string> GetNetEaseLyricAsync(
            string songId,
            bool includeTranslation,
            CancellationToken cancellationToken)
        {
            if (!long.TryParse(songId, out long id) || id <= 0)
            {
                return string.Empty;
            }

            try
            {
                string url = $"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1";
                using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                string lrc = string.Empty;
                string tlyric = string.Empty;

                if (doc.RootElement.TryGetProperty("lrc", out JsonElement lrcEl)
                    && lrcEl.TryGetProperty("lyric", out JsonElement lyricEl))
                {
                    lrc = lyricEl.GetString() ?? string.Empty;
                }

                if (doc.RootElement.TryGetProperty("tlyric", out JsonElement tlyricEl)
                    && tlyricEl.TryGetProperty("lyric", out JsonElement transEl))
                {
                    tlyric = transEl.GetString() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(lrc))
                {
                    return includeTranslation ? tlyric : string.Empty;
                }

                if (string.IsNullOrWhiteSpace(tlyric) || !includeTranslation)
                {
                    return lrc;
                }

                return lrc.TrimEnd() + Environment.NewLine + tlyric.TrimStart();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task<string> GetQqLyricAsync(string songmid, bool includeTranslation, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(songmid))
            {
                return string.Empty;
            }

            try
            {
                string url = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={0}"
                    + "&format=json&g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8&outCharset=utf-8"
                    + "&notice=0&platform=yqq.json&needNewCode=0";
                url = string.Format(url, Uri.EscapeDataString(songmid));
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Referer", QqReferer);
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                json = json.Trim();
                if (json.StartsWith("MusicJsonCallback", StringComparison.OrdinalIgnoreCase))
                {
                    int start = json.IndexOf('(');
                    int end = json.LastIndexOf(')');
                    if (start >= 0 && end > start)
                    {
                        json = json.Substring(start + 1, end - start - 1);
                    }
                }

                using JsonDocument doc = JsonDocument.Parse(json);
                string lrc = string.Empty;
                string tlyric = string.Empty;

                if (doc.RootElement.TryGetProperty("lyric", out JsonElement lyricEl))
                {
                    string? b64 = lyricEl.GetString();
                    if (!string.IsNullOrWhiteSpace(b64))
                    {
                        byte[] bytes = Convert.FromBase64String(b64);
                        lrc = Encoding.UTF8.GetString(bytes);
                    }
                }

                // QQ 翻译歌词在 trans 字段（同样 base64）。之前漏抓，导致 QQ 源的双语歌词只有原文。
                if (includeTranslation
                    && doc.RootElement.TryGetProperty("trans", out JsonElement transEl))
                {
                    string? tb64 = transEl.GetString();
                    if (!string.IsNullOrWhiteSpace(tb64))
                    {
                        try
                        {
                            byte[] tbytes = Convert.FromBase64String(tb64);
                            tlyric = Encoding.UTF8.GetString(tbytes);
                        }
                        catch (Exception) { tlyric = string.Empty; }
                    }
                }

                if (string.IsNullOrWhiteSpace(lrc))
                {
                    return string.IsNullOrWhiteSpace(tlyric) ? string.Empty : tlyric;
                }

                if (string.IsNullOrWhiteSpace(tlyric))
                {
                    return lrc;
                }

                return lrc.TrimEnd() + Environment.NewLine + tlyric.TrimStart();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return string.Empty;
        }

        private static async Task<string> GetKugouLyricAsync(
            string name,
            string fileHash,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(fileHash))
            {
                return string.Empty;
            }

            try
            {
                string searchUrl = "https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={0}&hash={1}";
                searchUrl = string.Format(searchUrl, Uri.EscapeDataString(name ?? string.Empty), Uri.EscapeDataString(fileHash));
                using HttpRequestMessage req1 = new(HttpMethod.Get, searchUrl);
                req1.Headers.TryAddWithoutValidation("Referer", "https://www.kugou.com/");
                using HttpResponseMessage res1 = await Http.SendAsync(req1, cancellationToken).ConfigureAwait(false);
                if (!res1.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                string json1 = await res1.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc1 = JsonDocument.Parse(json1);
                if (!doc1.RootElement.TryGetProperty("candidates", out JsonElement candidates)
                    || candidates.GetArrayLength() == 0)
                {
                    return string.Empty;
                }

                JsonElement first = candidates[0];
                string id = first.TryGetProperty("id", out JsonElement idEl)
                    ? idEl.GetString() ?? string.Empty
                    : string.Empty;
                string accessKey = first.TryGetProperty("accesskey", out JsonElement keyEl)
                    ? keyEl.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(accessKey))
                {
                    return string.Empty;
                }

                string dlUrl = "https://lyrics.kugou.com/download?ver=1&client=pc&id={0}&accesskey={1}&fmt=lrc";
                dlUrl = string.Format(dlUrl, Uri.EscapeDataString(id), Uri.EscapeDataString(accessKey));
                using HttpRequestMessage req2 = new(HttpMethod.Get, dlUrl);
                req2.Headers.TryAddWithoutValidation("Referer", "https://www.kugou.com/");
                using HttpResponseMessage res2 = await Http.SendAsync(req2, cancellationToken).ConfigureAwait(false);
                if (!res2.IsSuccessStatusCode)
                {
                    return string.Empty;
                }

                string json2 = await res2.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc2 = JsonDocument.Parse(json2);
                if (doc2.RootElement.TryGetProperty("content", out JsonElement contentEl))
                {
                    string? b64 = contentEl.GetString();
                    if (!string.IsNullOrWhiteSpace(b64))
                    {
                        byte[] bytes = Convert.FromBase64String(b64);
                        return Encoding.UTF8.GetString(bytes);
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return string.Empty;
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

        private static async Task<string?> GetNetEaseCoverUrlAsync(string songId, CancellationToken cancellationToken)
        {
            if (!long.TryParse(songId, out long id) || id <= 0)
            {
                return null;
            }

            try
            {
                string url = $"https://music.163.com/api/song/detail/?ids=[{id}]";
                using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("songs", out JsonElement songs)
                    || songs.GetArrayLength() == 0)
                {
                    return null;
                }

                JsonElement song = songs[0];
                if (!song.TryGetProperty("album", out JsonElement album)
                    || !album.TryGetProperty("picUrl", out JsonElement picUrlEl))
                {
                    return null;
                }

                return picUrlEl.GetString();
            }
            catch
            {
                return null;
            }
        }

        public static async Task<string?> GetCoverUrlAsync(OnlineSongResult song, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(song.CoverUrl))
            {
                return song.CoverUrl;
            }

            return await GetNetEaseCoverUrlAsync(song.SongId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>按歌手名搜索网络头像（当前使用网易云歌手搜索，返回头像 URL）。</summary>
        /// <summary>按设置的面像来源平台搜索艺术家头像 URL（NetEase 默认 / iTunes 精确区分艺人）。
        /// iTunes 的 musicArtist 实体本身不含 artworkUrl，故取其热门歌曲封面（专辑封面）作头像；
        /// 优点是艺人 id 精确（如「東京事変」与「椎名林檎」不再混淆，各用各的专辑图）。</summary>
        public static async Task<string?> SearchArtistAvatarUrlAsync(string artistName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return null;
            }

            string source = AppSettingsStore.Load().ArtistAvatarSource;
            if (string.Equals(source, "iTunes", StringComparison.OrdinalIgnoreCase))
            {
                return await SearchItunesArtistAvatarUrlAsync(artistName, cancellationToken).ConfigureAwait(false);
            }

            return await SearchNetEaseArtistAvatarUrlAsync(artistName, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>网易云 type=100 真实歌手头像（picUrl）。对部分日系歌手（如東京市変/椎名林檎）可能返回同组艺人导致混淆。</summary>
        private static async Task<string?> SearchNetEaseArtistAvatarUrlAsync(
            string artistName,
            CancellationToken cancellationToken)
        {
            try
            {
                string url = "https://music.163.com/api/search/get?s={0}&type=100&limit=1";
                url = string.Format(url, Uri.EscapeDataString(artistName.Trim()));
                using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out JsonElement result)
                    || !result.TryGetProperty("artists", out JsonElement artists)
                    || artists.GetArrayLength() == 0)
                {
                    return null;
                }

                if (artists[0].TryGetProperty("picUrl", out JsonElement pic))
                {
                    string? u = pic.GetString();
                    return string.IsNullOrWhiteSpace(u) ? null : u;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("OnlineMusicApi.cs", caught); }

            return null;
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
                    coverUrl = await GetNetEaseCoverUrlAsync(song.SongId, cancellationToken).ConfigureAwait(false);
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

    }
}
