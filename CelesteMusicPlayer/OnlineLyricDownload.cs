using System;
using System.Collections.Generic;
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
    /// <summary>歌词候选：某来源里的一首歌（搜索接口返回的一条）。</summary>
    public sealed record LyricCandidate(
        string Source,
        string SongId,
        string Title,
        string Artist,
        string Album,
        int DurationMs,
        bool HasTranslation)
    {
        /// <summary>结果列表里的行号（1 起），填充列表时赋值，仅用于显示。</summary>
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

        /// <summary>在线查看地址（QQ 有歌曲页；LRClib 没有稳定的单曲网页，返回空 = 按钮置灰）。</summary>
        public string OnlineUrl =>
            string.Equals(Source, OnlineLyricDownload.SourceQQ, StringComparison.Ordinal)
                ? $"https://y.qq.com/n/ryqq/songDetail/{Uri.EscapeDataString(SongId)}"
                : string.Empty;
    }

    /// <summary>
    /// 歌词写入口径（2026-10-14 用户拍板：写标签时让用户自己选）。
    /// 背景：ID3v2 的 UNSYNCEDLYRICS（USLT）、Vorbis 的 UNSYNCEDLYRICS、MP4 的 ©lyr
    /// 都是「纯文本」字段；把带 [00:12.34] 的 LRC 塞进去是 ECHO 类播放器认的潜规则，
    /// 不是标准行为——所以三种口径都留给用户选。
    /// </summary>
    public enum LyricWriteTarget
    {
        /// <summary>外挂 .lrc（带时间轴，所有播放器都认，通用性最好）。</summary>
        Sidecar,

        /// <summary>写入歌词标签 = 纯文本（剥掉时间戳）。标准语义。</summary>
        TagPlain,

        /// <summary>写入歌词标签但保留时间戳文本。兼容 ECHO 这类把字段内容按 LRC 解析的播放器。</summary>
        TagTimestamped,

        /// <summary>外挂 .lrc + 标签纯文本，两头都顾上。</summary>
        Both
    }

    /// <summary>
    /// 在线歌词下载（标签面板「歌词下载」页的后端）。
    /// 来源：LRClib（开放歌词库，免 key 零门槛，主力）+ QQ 音乐（客户端老接口，非官方，
    /// 2026-10-14 实测可用，限速 1 次/秒；随时可能被毙，界面上已注明）。
    /// 网易云 2026-10-14 实测全接口 -462 风控（要滑块验证），不做。
    /// </summary>
    public static class OnlineLyricDownload
    {
        public const string SourceLrclib = "LRClib";
        public const string SourceQQ = "QQ";

        private static readonly HttpClient Http = CreateClient();

        /// <summary>QQ 接口限速闸：全程只允许一个 QQ 请求在飞，间隔 ≥1.1s。</summary>
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
            // ⚠️ UA 必须带标准浏览器标识：QQ 的客户端接口对非浏览器 UA 直接拒。
            // 别在 UA 里写应用名（2026-10-12 网易云实测：带自定义标识的 UA 被风控）。
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

        // =====================================================================
        // 搜索
        // =====================================================================

        /// <summary>按来源搜候选歌曲。keyword 为空时返回空列表。</summary>
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

        /// <summary>
        /// QQ 音乐搜索。2026-10-14 实测可用（返回 data.song.list，字段
        /// songname/songmid/albumname/interval/singer）。这是客户端老接口，
        /// 无 SLA——哪天返回结构变了这里解析出来就是 0 条，状态栏会说明。
        /// </summary>
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

        /// <summary>
        /// LRClib 搜索候选。开放接口免 key，只要求 UA 自报身份。
        /// 返回候选不含歌词正文（歌词在 FetchLyricAsync 时按 /api/get 精确取），
        /// 这样搜索结果列表和 QQ 的行为保持一致。
        /// </summary>
        private static async Task<IReadOnlyList<LyricCandidate>> SearchLrclibAsync(
            string keyword,
            CancellationToken cancellationToken)
        {
            // LRClib 的搜索按「歌名 + 艺术家」效果最好；UI 传入的 keyword 已是这种组合。
            // 这里简单地把 keyword 整个交给 track_name，artist_name 留空——LRClib 的
            // 搜索引擎对混合串也能命中，且 /api/get 那一步还有精确匹配兜底。
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

        // =====================================================================
        // 取歌词
        // =====================================================================

        /// <summary>
        /// 取某个候选的歌词正文（LRC 文本，优先带时间轴的）。
        /// LRClib 优先 syncedLyrics，没有则 plainLyrics；QQ 取 lyric 字段，
        /// includeTranslation 且存在 trans 时把译文附在后面（与 MusicPlayer2 一致）。
        /// 取不到返回空串（调用方负责在状态栏说明）。
        /// </summary>
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

            // retcode != 0（常见 1101）= 这首歌没有歌词或 mid 无效，按「没有」处理
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

        /// <summary>带 Referer 的 QQ GET（歌词/搜索接口都要求 Referer: https://y.qq.com/）。</summary>
        private static async Task<string> QqRequestAsync(string url, CancellationToken cancellationToken)
        {
            await QqGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 限速：两次 QQ 请求之间至少隔 1.1s（对端没公布限流阈值，保守起见）
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

        // =====================================================================
        // 匹配（移植 MusicPlayer2 的 SelectMatchedItem：加权编辑距离相似度）
        // =====================================================================

        /// <summary>
        /// 从候选里挑最匹配的一项，返回索引；全都对不上返回 -1（权值上限 1.0，阈值 0.3）。
        /// 权值（与 MusicPlayer2 一致）：标题 0.4、艺术家 0.4、专辑 0.3、
        /// 文件名 vs 标题 0.3、文件名 vs 艺术家 0.3、列表顺序 0.05。
        /// </summary>
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

        /// <summary>匹配用归一化：去空白、全角转半角、小写。</summary>
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

        /// <summary>编辑距离相似度：1 - 距离/最大长度。双方都空 = 1.0，一方空 = 0.0。</summary>
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

            // 超长串不做完整 DP（标题不可能这么长，防御性兜底）
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

        // =====================================================================
        // 写入
        // =====================================================================

        /// <summary>
        /// 剥掉 LRC 的时间戳与 [ti:]/[ar:] 等元信息行，得到纯文本歌词
        /// （写 UNSYNCEDLYRICS 这类纯文本字段时用）。
        /// </summary>
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

        /// <summary>
        /// 把歌词写到目标位置。target 含外挂时写同名 .lrc（UTF-8 无 BOM）；
        /// 含标签时走 TagEditorService（只动歌词一个字段，带失败原因返回）。
        /// 返回值：(是否成功, 给人看的一句话)。
        /// </summary>
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
    }
}
