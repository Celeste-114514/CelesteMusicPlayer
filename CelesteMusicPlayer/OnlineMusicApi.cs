using System;
using System.Collections.Generic;
using System.IO;
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
                string path = Path.Combine(folder, SanitizeFileName(title + " - " + artist) + ".lrc");
                await File.WriteAllTextAsync(path, lyric, cancellationToken).ConfigureAwait(false);
                return path;
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
    }
}
