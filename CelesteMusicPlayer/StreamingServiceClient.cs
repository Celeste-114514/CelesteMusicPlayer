using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>从 WSL 插件服务(streamingserver)获取在线歌词/搜索/下载的 HTTP 客户端。
    /// 地址由设置「流媒体」板块配置（如 http://<WSL-IP>:21010）。</summary>
    public static class StreamingServiceClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

        /// <summary>服务基址（如 http://172.20.55.125:21010），未配置为空。</summary>
        public static string? ServiceBaseUrl { get; set; }

        public static string? ResolveBase()
        {
            string? u = ServiceBaseUrl;
            if (string.IsNullOrWhiteSpace(u))
            {
                return null;
            }
            return u.Trim().TrimEnd('/');
        }

        /// <summary>当某平台的请求失败（可能未登录/凭证失效）时，追加给用户的提醒文案。</summary>
        public static string CookieReminderHint(string platform)
        {
            var st = AppSettingsStore.Load();
            bool has = platform switch
            {
                "NetEase" or "netease" => !string.IsNullOrWhiteSpace(st.NetEaseCookie),
                "QQ" or "qqmusic" => !string.IsNullOrWhiteSpace(st.QqCookie),
                "iTunes" or "applemusic" => !string.IsNullOrWhiteSpace(st.AppleMusicCookie),
                _ => false
            };
            return has
                ? "（若仍提示未授权，Cookie 可能已失效，请到 设置→流媒体 更新）"
                : "（该平台需登录，请到 设置→流媒体 粘贴浏览器登录后的 Cookie）";
        }

        public sealed record PingResult(bool Ok, long? Time);
        public sealed record PlatformResult(bool Ok, string[] Platforms, string? Error);
        public sealed record SearchTrack(string Id, string Platform, string Title, string Album, string CoverUrl, string Artist);
        public sealed record LyricResult(bool Ok, string? Plain, List<LyricLine>? Timestamped, string? RawTtml, string? Error);
        public sealed record DownloadResult(bool Ok, string? Url, string[]? Urls, Dictionary<string, string>? Headers, string? Format, int? Bitrate, long? Size, string? Error);

        public static async Task<PingResult?> PingAsync(CancellationToken ct = default)
        {
            string? b = ResolveBase();
            if (b == null)
            {
                return null;
            }
            try
            {
                using var resp = await Http.GetAsync(b + "/api/ping", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return new PingResult(false, null);
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return new PingResult(doc.RootElement.TryGetProperty("ok", out var o) && o.GetBoolean(),
                    doc.RootElement.TryGetProperty("time", out var t) ? t.GetInt64() : null);
            }
            catch
            {
                return null;
            }
        }

        public static async Task<PlatformResult?> GetPlatformsAsync(CancellationToken ct = default)
        {
            string? b = ResolveBase();
            if (b == null)
            {
                return null;
            }
            try
            {
                using var resp = await Http.GetAsync(b + "/api/platforms", ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var root = doc.RootElement;
                bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                var plats = new List<string>();
                if (root.TryGetProperty("platforms", out var arr))
                {
                    foreach (var p in arr.EnumerateArray())
                    {
                        plats.Add(p.GetString() ?? "");
                    }
                }
                string? err = root.TryGetProperty("error", out var er) ? er.GetString() : null;
                return new PlatformResult(ok, plats.ToArray(), err);
            }
            catch (Exception ex)
            {
                return new PlatformResult(false, Array.Empty<string>(), ex.Message);
            }
        }

        public static async Task<List<SearchTrack>?> SearchAsync(string platform, string query, int limit, CancellationToken ct = default)
        {
            string? b = ResolveBase();
            if (b == null)
            {
                return null;
            }
            try
            {
                string url = b + "/api/search?platform=" + Uri.EscapeDataString(platform)
                    + "&q=" + Uri.EscapeDataString(query) + "&limit=" + limit;
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("tracks", out var arr))
                {
                    return null;
                }

                var list = new List<SearchTrack>();
                foreach (var s in arr.EnumerateArray())
                {
                    list.Add(new SearchTrack(
                        s.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        s.TryGetProperty("platform", out var pf) ? pf.GetString() ?? platform : platform,
                        s.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                        (s.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object && al.TryGetProperty("title", out var albumT)) ? albumT.GetString() ?? "" : "",
                        (s.TryGetProperty("album", out var al2) && al2.ValueKind == JsonValueKind.Object && al2.TryGetProperty("cover_url", out var cv)) ? cv.GetString() ?? "" : "",
                        s.TryGetProperty("artists", out var ars) && ars.ValueKind == JsonValueKind.Array && ars.GetArrayLength() > 0
                            ? (ars[0].TryGetProperty("name", out var an) ? an.GetString() ?? "" : "")
                            : ""));
                }

                return list;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>取歌词。Timestamped 为逐行(时间+文本)，可转 LRC；Apple Music 还返回 RawTtml(逐字)。</summary>
        public static async Task<LyricResult?> GetLyricAsync(string platform, string id, CancellationToken ct = default)
        {
            string? b = ResolveBase();
            if (b == null)
            {
                return null;
            }
            try
            {
                string url = b + "/api/lyric?platform=" + Uri.EscapeDataString(platform) + "&id=" + Uri.EscapeDataString(id);
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return new LyricResult(false, null, null, null, "HTTP " + (int)resp.StatusCode);
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var root = doc.RootElement;
                bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                string? err = root.TryGetProperty("error", out var er) ? er.GetString() : null;
                if (!ok || !root.TryGetProperty("lyrics", out var ly))
                {
                    return new LyricResult(false, null, null, null, err);
                }

                string? plain = ly.TryGetProperty("plain", out var pl) ? pl.GetString() : null;
                string? ttml = ly.TryGetProperty("raw_ttml", out var tt) ? tt.GetString() : null;
                var lines = new List<LyricLine>();
                if (ly.TryGetProperty("timestamped", out var ts) && ts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ts.EnumerateArray())
                    {
                        double sec = item.TryGetProperty("time", out var ti) && ti.ValueKind == JsonValueKind.Number
                            ? ti.GetDouble()
                            : 0;
                        string? text = item.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }
                        lines.Add(new LyricLine { Time = TimeSpan.FromSeconds(sec), Text = text });
                    }
                }

                return new LyricResult(true, plain, lines.Count > 0 ? lines : null, ttml, null);
            }
            catch (Exception ex)
            {
                return new LyricResult(false, null, null, null, ex.Message);
            }
        }

        public static async Task<DownloadResult?> GetDownloadAsync(string platform, string id, string quality, CancellationToken ct = default)
        {
            string? b = ResolveBase();
            if (b == null)
            {
                return null;
            }
            try
            {
                string url = b + "/api/download?platform=" + Uri.EscapeDataString(platform)
                    + "&id=" + Uri.EscapeDataString(id) + "&quality=" + Uri.EscapeDataString(quality);
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    return new DownloadResult(false, null, null, null, null, null, null, "HTTP " + (int)resp.StatusCode);
                }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var root = doc.RootElement;
                if (!(root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean()))
                {
                    string? e = root.TryGetProperty("error", out var ee) ? ee.GetString() : null;
                    return new DownloadResult(false, null, null, null, null, null, null, e);
                }

                string urlDl = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var urls = new List<string>();
                if (root.TryGetProperty("urls", out var us) && us.ValueKind == JsonValueKind.Array)
                {
                    foreach (var x in us.EnumerateArray())
                    {
                        urls.Add(x.GetString() ?? "");
                    }
                }

                var headers = new Dictionary<string, string>();
                if (root.TryGetProperty("headers", out var hd) && hd.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pr in hd.EnumerateObject())
                    {
                        headers[pr.Name] = pr.Value.ToString();
                    }
                }

                string? fmt = root.TryGetProperty("format", out var f) ? f.GetString() : null;
                int? bit = root.TryGetProperty("bitrate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetInt32() : null;
                long? size = root.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : null;
                return new DownloadResult(true, urlDl, urls.ToArray(), headers, fmt, bit, size, null);
            }
            catch (Exception ex)
            {
                return new DownloadResult(false, null, null, null, null, null, null, ex.Message);
            }
        }
    }
}
