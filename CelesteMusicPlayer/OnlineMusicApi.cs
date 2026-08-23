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
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
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
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

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
            catch
            {
            }

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
            catch
            {
            }

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
            catch
            {
            }

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
            catch
            {
            }

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
                "QQ" => await GetQqLyricAsync(song.SongId, cancellationToken).ConfigureAwait(false),
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

        private static async Task<string> GetQqLyricAsync(string songmid, CancellationToken cancellationToken)
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
                if (doc.RootElement.TryGetProperty("lyric", out JsonElement lyricEl))
                {
                    string? b64 = lyricEl.GetString();
                    if (!string.IsNullOrWhiteSpace(b64))
                    {
                        byte[] bytes = Convert.FromBase64String(b64);
                        return Encoding.UTF8.GetString(bytes);
                    }
                }
            }
            catch
            {
            }

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
            catch
            {
            }

            return string.Empty;
        }

        // =====================================================================
        // 歌词下载（组合）
        // =====================================================================

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

                OnlineSongResult best = hits[0];
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
        public static async Task<string?> SearchArtistAvatarUrlAsync(string artistName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(artistName))
            {
                return null;
            }

            try
            {
                // 头像搜索曾尝试切换 Apple Music（iTunes Search API）。实测其 musicArtist 实体
                // 返回结果不含任何 artworkUrl 字段——Apple 不在公开 search API 暴露"艺术家头像"，
                // 需授权/抓取页面，故不可行；维持网易云（music.163.com type=100 艺术家搜索取 picUrl）。
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
            catch
            {
            }

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
                    catch
                    {
                    }
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
                    try { File.Delete(tmpSaved); } catch { }
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
                catch { }

                return true;
            }
            catch
            {
                return false;
            }
        }

        // =====================================================================
        // QQ 音乐直链下载（去 bot：本机直连 u.y.qq.com musicu.fcg 换取 vkey 直链）
        // =====================================================================

        /// <summary>QQ 音乐直链结果：Url 可直接 GET（附 Headers），空 Error 表示成功。</summary>
        public sealed record OnlineDownloadLink(
            string Url,
            Dictionary<string, string> Headers,
            string Extension,
            int? Bitrate,
            string? Error);

        private sealed record QqPlaybackTarget(string Module, string Method, bool Modern);

        private static readonly QqPlaybackTarget[] QqVkeyEndpoints =
        {
            new("music.vkey.GetVkey", "UrlGetVkey", true),
            new("vkey.GetVkeyServer", "CgiGetVkey", false),
        };

        private static readonly string[] QqLegacyPlatforms = { "20", "yqq" };

        /// <summary>
        /// 获取 QQ 音乐直链（默认 128k standard；有已保存 Cookie 时可尝试更高码率）。
        /// 使用 ECHO(Electron) QQMusicStreamingProvider 同款机制：先 songmid 取 media_mid，
        /// 再 POST u.y.qq.com/cgi-bin/musicu.fcg 的 music.vkey.GetVkey 换直链。
        /// </summary>
        public static async Task<OnlineDownloadLink?> GetQqDownloadLinkAsync(
            string songMid,
            CancellationToken cancellationToken = default,
            bool preferLossless = false)
        {
            if (string.IsNullOrWhiteSpace(songMid))
            {
                return new OnlineDownloadLink(string.Empty, new(), "mp3", null, "无效的歌曲 ID");
            }

            try
            {
                // 0) 先读 cookie，detail 与 vkey 请求都会带上（会员码率需要）。
                string cookie = AppSettingsStore.Load().QqCookie?.Trim() ?? string.Empty;

                // 1) 先走歌曲详情接口抓取 media_mid（用于构造 filename）。
                string mediaMid = string.Empty;
                using (HttpRequestMessage detailReq = new(HttpMethod.Get,
                           "https://c.y.qq.com/v8/fcg-bin/fcg_play_single_song.fcg?tpl=yqq_song_detail&format=json&songmid="
                           + Uri.EscapeDataString(songMid)))
                {
                    SetQqHeaders(detailReq, cookie);
                    using HttpResponseMessage detailResp = await Http.SendAsync(detailReq, cancellationToken).ConfigureAwait(false);
                    if (detailResp.IsSuccessStatusCode)
                    {
                        string detailJson = await detailResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        mediaMid = ExtractQqMediaMid(detailJson);
                    }
                }

                string primaryId = string.IsNullOrWhiteSpace(mediaMid) ? songMid : mediaMid;
                string uin = QqUinFromCookie(cookie);
                string guid = QqGuidFromCookie(cookie, uin);
                long gtk = QqGtkFromCookie(cookie);

                // 2) 按 ECHO 降级链逐个码率尝试换取直链。
                //    无 cookie 只试 128k(standard)；有 cookie 试 lossless→high→standard 降级。
                var qualities = new List<(string Prefix, string Extension, int Bitrate)>();
                if (preferLossless || cookie.Length > 0)
                {
                    qualities.Add(("F000", "flac", 999000));
                }
                if (cookie.Length > 0)
                {
                    qualities.Add(("M800", "mp3", 320000));
                }
                qualities.Add(("M500", "mp3", 128000));

                foreach (var (prefix, extension, bitrate) in qualities)
                {
                    string filename = $"{prefix}{primaryId}.{extension}";
                    string? resolved = await TryResolveQqUrlAsync(
                        songMid, filename, uin, guid, gtk, cookie, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        var headers = new Dictionary<string, string>
                        {
                            ["Referer"] = "https://y.qq.com/",
                            ["Origin"] = "https://y.qq.com",
                        };
                        if (!string.IsNullOrWhiteSpace(cookie))
                        {
                            headers["Cookie"] = cookie;
                        }

                        return new OnlineDownloadLink(resolved, headers, extension, bitrate, null);
                    }
                }

                return new OnlineDownloadLink(string.Empty, new(), "mp3", null,
                    cookie.Length > 0
                        ? "QQ 音乐未返回播放直链（可能是会员/受限曲；若你是会员，请更新 QQ Cookie 后重试）。"
                        : "QQ 音乐未返回播放直链（可能是会员/受限曲；请在设置里保存 QQ Cookie 后重试，或换一首免费曲）。");
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                return new OnlineDownloadLink(string.Empty, new(), "mp3", null, ex.Message);
            }
        }

        /// <summary>对单个 filename 依次尝试各 vkey 端点/platform，返回直链或空。</summary>
        private static async Task<string?> TryResolveQqUrlAsync(
            string songMid,
            string filename,
            string uin,
            string guid,
            long gtk,
            string cookie,
            CancellationToken cancellationToken)
        {
            foreach (QqPlaybackTarget ep in QqVkeyEndpoints)
            {
                object?[] platforms = ep.Modern
                    ? new object?[] { null }
                    : QqLegacyPlatforms.Cast<object?>().ToArray();

                foreach (object? platform in platforms)
                {
                    var param = new Dictionary<string, object?>
                    {
                        ["guid"] = guid,
                        ["songmid"] = new[] { songMid },
                        ["filename"] = new[] { filename },
                        ["songtype"] = new[] { 0 },
                        ["uin"] = uin,
                    };
                    if (ep.Modern)
                    {
                        param["ctx"] = 0;
                    }
                    else
                    {
                        param["loginflag"] = 1;
                        if (platform != null)
                        {
                            param["platform"] = platform;
                        }
                    }

                    var body = new Dictionary<string, object?>
                    {
                        ["req_0"] = new Dictionary<string, object?>
                        {
                            ["module"] = ep.Module,
                            ["method"] = ep.Method,
                            ["param"] = param,
                        },
                        ["comm"] = ep.Modern
                            ? new Dictionary<string, object?>
                            {
                                ["uin"] = uin,
                                ["format"] = "json",
                                ["ct"] = 24,
                                ["cv"] = 4747474,
                                ["platform"] = "yqq.json",
                                ["chid"] = "0",
                                ["g_tk"] = gtk,
                                ["g_tk_new_20200303"] = gtk,
                                ["inCharset"] = "utf-8",
                                ["outCharset"] = "utf-8",
                                ["notice"] = 0,
                                ["needNewCode"] = 1,
                            }
                            : new Dictionary<string, object?>
                            {
                                ["uin"] = uin,
                                ["format"] = "json",
                                ["ct"] = 24,
                                ["cv"] = 0,
                            },
                    };

                    string payloadJson = JsonSerializer.Serialize(body);
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
                    request.Content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json");
                    SetQqHeaders(request, cookie);

                    using HttpResponseMessage resp = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    (string? purl, string? sip, int _) = ExtractQqVkey(json);
                    if (!string.IsNullOrWhiteSpace(purl))
                    {
                        return purl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? purl
                            : (string.IsNullOrWhiteSpace(sip) ? "https://isure.stream.qqmusic.qq.com/" : sip) + purl;
                    }
                }
            }

            return null;
        }

        private static void SetQqHeaders(HttpRequestMessage request, string cookie = "")
        {
            request.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
        }

        private static string ExtractQqMediaMid(string detailJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(detailJson);
                if (doc.RootElement.TryGetProperty("data", out JsonElement data)
                    && data.ValueKind == JsonValueKind.Array
                    && data.GetArrayLength() > 0
                    && data[0].TryGetProperty("file", out JsonElement file))
                {
                    if (file.TryGetProperty("media_mid", out JsonElement mm) && mm.ValueKind == JsonValueKind.String)
                    {
                        string? v = mm.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            return v;
                        }
                    }

                    if (file.TryGetProperty("strMediaMid", out JsonElement sm))
                    {
                        string? v = sm.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            return v;
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static (string? Purl, string? Sip, int Result) ExtractQqVkey(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("req_0", out JsonElement req0)
                    || !req0.TryGetProperty("data", out JsonElement data))
                {
                    return (null, null, 0);
                }

                string? sip = null;
                if (data.TryGetProperty("sip", out JsonElement sipEl) && sipEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement e in sipEl.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String)
                        {
                            string? s = e.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                sip = s;
                                break;
                            }
                        }
                    }
                }

                int resultCode = 0;
                if (data.TryGetProperty("midurlinfo", out JsonElement mi) && mi.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in mi.EnumerateArray())
                    {
                        if (item.TryGetProperty("result", out JsonElement rEl) && rEl.TryGetInt32(out int rc))
                        {
                            if (rc != 0)
                            {
                                resultCode = rc;
                            }
                        }

                        if (item.TryGetProperty("purl", out JsonElement purlEl))
                        {
                            string? p = purlEl.GetString();
                            if (!string.IsNullOrWhiteSpace(p))
                            {
                                return (p, sip, resultCode);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return (null, null, 0);
        }

        /// <summary>据 QQ vkey result 码生成面向用户的诊断信息（对齐 ECHO QQMusicStreamingProvider）。</summary>
        private static string QqVkeyMessage(int result, bool hasCookie)
        {
            if (result == 104003 && !hasCookie)
            {
                return "QQ 音乐不会 128k 播放地址（可能是会员/VIP 曲目）；请在设置里登录/保存有效 QQ Cookie 后重试。";
            }
            if (result == 104003)
            {
                return "QQ 音乐返回无播放权限（104003）；请确认是已开通会员的账号并更新 Cookie。";
            }
            if (result == 104013)
            {
                return "QQ 音乐限制当前设备播放（104013）；请稍后重试或更新 Cookie。";
            }
            if (result > 0)
            {
                return $"QQ 音乐未返回播放直链（错误码 {result}）";
            }

            return "QQ 音乐未返回播放直链。";
        }

        private static string QqUinFromCookie(string cookie)
        {
            string? value = CookieValue(cookie, "uin", "qqmusic_uin", "p_uin", "pt2gguin", "loginUin", "wxuin");
            if (value != null)
            {
                string? m = Regex_MatchDigits(value);
                if (!string.IsNullOrWhiteSpace(m))
                {
                    return m;
                }
            }

            return "0";
        }

        private static string QqGuidFromCookie(string cookie, string uin)
        {
            string? raw = CookieValue(cookie, "pgv_pvid", "qqmusic_guid", "guid");
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string digits = new string(raw.Where(char.IsDigit).ToArray());
                if (digits.Length > 0)
                {
                    return digits;
                }
            }

            return StableNumericId(uin != "0" ? uin : (cookie ?? "qqmusic"));
        }

        private static long QqGtkFromCookie(string cookie)
        {
            // 复刻 ECHO qqGtkFromCookie 的 JS 语义：hash 为 number，每轮 `hash << 5`
            // 先把 hash 转 int32 再左移 5（32 位回绕），结果与字符码相加回到 hash。
            // 由于每轮 ToInt32 截掉高位，hash 始终在 int32×33 量级，number 全程精确。
            string skey = CookieValue(cookie, "qqmusic_key", "qm_keyst", "music_key", "p_skey", "skey") ?? string.Empty;
            double hash = 5381;
            foreach (char c in skey)
            {
                int h32 = unchecked((int)hash); // JS ToInt32(hash)
                double shifted = h32 << 5;      // int32 左移 5（回绕），转回 double
                hash += shifted + c;
            }

            // JS: hash & 0x7fffffff → ToInt32(hash) & 0x7fffffff
            return unchecked((int)hash) & 0x7fffffffL;
        }

        private static string? CookieValue(string cookie, params string[] names)
        {
            if (string.IsNullOrEmpty(cookie))
            {
                return null;
            }

            int start;
            foreach (string name in names)
            {
                int i = cookie.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
                if (i < 0)
                {
                    continue;
                }

                start = i + name.Length + 1;
                int end = cookie.IndexOf(';', start);
                if (end < 0)
                {
                    end = cookie.Length;
                }

                string val = cookie.Substring(start, end - start).Trim();
                return Uri.UnescapeDataString(val);
            }

            return null;
        }

        private static string Regex_MatchDigits(string value)
        {
            // 对应 JS match(/o?(\d+)/)：取首个连续数字块（允许前缀 o）。
            var sb = new StringBuilder();
            bool started = false;
            foreach (char c in value)
            {
                if (char.IsDigit(c))
                {
                    started = true;
                    sb.Append(c);
                }
                else if (started)
                {
                    break;
                }
            }

            return sb.ToString();
        }

        private static string StableNumericId(string value)
        {
            int hash = -2128831035; // 2166136261 有符号
            foreach (char c in value)
            {
                hash ^= c;
                hash = unchecked(hash * 16777619);
            }

            uint u = (uint)hash;
            return (100000000 + (u % 900000000)).ToString();
        }

        // =====================================================================
        // 网易云直链下载（去 bot：本机走公开播放 URL 接口换直链；免费曲 128k/320k 无需登录）
        // =====================================================================
        // 注意：网易云 weapi/eapi 播放端点（/weapi/song/enhance/player/url）实测已失效（返回 200 空 body），
        // 公开接口 /api/song/enhance/player/url?ids=[id]&br= 仍可用：
        //   - 免费曲：128k/320k 直链直接返回（无需 Cookie）；
        //   - 会员/VIP 曲：url=null（fee>0 或 code=-110），需有效 MUSIC_U Cookie 解锁。

        /// <summary>
        /// 获取网易云音频直链（默认 128k；免费曲无 Cookie 即可，会员曲需设置里保存有效 NetEaseCookie/MUSIC_U）。
        /// </summary>
        public static async Task<OnlineDownloadLink?> GetNetEaseDownloadLinkAsync(
            string songId,
            CancellationToken cancellationToken = default,
            bool preferLossless = false)
        {
            if (!long.TryParse(songId, out long id) || id <= 0)
            {
                return new OnlineDownloadLink(string.Empty, new(), "mp3", null, "无效的网易云歌曲 ID");
            }

            string cookie = AppSettingsStore.Load().NetEaseCookie?.Trim() ?? string.Empty;

            // 降级链：preferLossless→先 999k(flac)；否则 320k → 128k 兜底（免费曲无 cookie 也能拿 320k）。
            var bitrates = new List<int>();
            if (preferLossless)
            {
                bitrates.Add(999000);
            }
            bitrates.Add(320000);
            bitrates.Add(128000);

            string? lastError = "网易云未返回播放直链。";
            try
            {
                foreach (int bitrate in bitrates)
                {
                    var (url, ext, returnedBr, err) = await TryResolveNetEaseUrlAsync(songId, bitrate, cookie, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        var headers = new Dictionary<string, string>
                        {
                            ["Referer"] = "https://music.163.com/",
                            ["Origin"] = "https://music.163.com",
                        };
                        if (!string.IsNullOrWhiteSpace(cookie))
                        {
                            headers["Cookie"] = cookie;
                        }

                        return new OnlineDownloadLink(url, headers, ext, returnedBr, null);
                    }

                    if (err != null)
                    {
                        lastError = err;
                    }
                }

                return new OnlineDownloadLink(string.Empty, new(), "mp3", null, lastError);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                return new OnlineDownloadLink(string.Empty, new(), "mp3", null, ex.Message);
            }
        }

        /// <summary>对单个码率请求公开播放 URL 接口并解析直链。成功返回 (url,ext,bitrate,null)；失败返回 (null,_,_,错误)。</summary>
        private static async Task<(string? Url, string Ext, int? Bitrate, string? Error)> TryResolveNetEaseUrlAsync(
            string songId,
            int bitrate,
            string cookie,
            CancellationToken cancellationToken)
        {
            try
            {
                string url = $"https://music.163.com/api/song/enhance/player/url?ids=[{songId}]&br={bitrate}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
                request.Headers.TryAddWithoutValidation("Origin", "https://music.163.com");
                request.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                if (!string.IsNullOrWhiteSpace(cookie))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);
                }

                using HttpResponseMessage resp = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return (null, "mp3", null, $"网易云请求失败 HTTP {(int)resp.StatusCode}");
                }

                string json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return (null, "mp3", null, "网易云音频接口未返回内容（可能被风控或暂时不可用，请稍后重试）。");
                }

                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement data)
                    || data.ValueKind != JsonValueKind.Array
                    || data.GetArrayLength() == 0)
                {
                    return (null, "mp3", null, "网易云未返回歌曲数据。");
                }

                JsonElement item = data[0];
                string? resolved = item.TryGetProperty("url", out JsonElement urlEl) ? urlEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    bool isVip = item.TryGetProperty("fee", out JsonElement feeEl)
                        && feeEl.TryGetInt32(out int feeV) && feeV > 0;
                    return (null, "mp3", null, isVip
                        ? "该曲为会员/VIP 曲目；请在设置里保存有效的网易云 Cookie 后重试。"
                        : "网易云未返回播放直链（当前 Cookie 可能失效，请在设置里更新后重试）。");
                }

                string type = item.TryGetProperty("type", out JsonElement typeEl) ? (typeEl.GetString() ?? "mp3") : "mp3";
                string level = item.TryGetProperty("level", out JsonElement levelEl) ? (levelEl.GetString() ?? "standard") : "standard";
                string extension = level is "lossless" or "hires" || type == "flac" ? "flac" : "mp3";
                int? returnedBr = item.TryGetProperty("br", out JsonElement brEl) ? brEl.GetInt32() : null;

                return (resolved, extension, returnedBr, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, "mp3", null, ex.Message);
            }
        }
    }
}
