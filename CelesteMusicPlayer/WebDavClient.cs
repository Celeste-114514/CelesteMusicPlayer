using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>WebDAV 目录项（文件夹或文件）。</summary>
    internal sealed class WebDavEntry
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// 相对「本次列出时请求的那个目录」的路径 —— 通常就是纯文件名（如 "01.flac"）。
        /// ⚠️ 不是相对服务器根目录的完整路径：列 "Music" 时子项返回 "Album" 而不是 "Music/Album"。
        /// 需要完整路径时自己拼（见 MainWindow.WebDav.cs 的 CombineRemotePath）。
        /// </summary>
        public string RelativePath { get; init; } = string.Empty;

        public bool IsFolder { get; init; }

        /// <summary>字节数；文件夹为 0。</summary>
        public long Size { get; init; }

        public DateTimeOffset? ModifiedUtc { get; init; }
    }

    /// <summary>连接测试结果。措辞面向使用者，不暴露堆栈。</summary>
    internal sealed class WebDavTestResult
    {
        public bool Ok { get; init; }

        /// <summary>一句话结论，如「连接成功（412 ms）」「用户名或密码不对」。</summary>
        public string Headline { get; init; } = string.Empty;

        /// <summary>补充说明，失败时给排查方向。</summary>
        public string Detail { get; init; } = string.Empty;

        public IReadOnlyList<WebDavEntry> Items { get; init; } = Array.Empty<WebDavEntry>();
    }

    /// <summary>带「人话结论」的 WebDAV 异常。</summary>
    internal sealed class WebDavException : Exception
    {
        public WebDavException(string headline, string detail, Exception? inner = null)
            : base(detail, inner)
        {
            Headline = headline;
        }

        public string Headline { get; }
    }

    /// <summary>
    /// WebDAV 客户端（PikPak / 群晖 / 坚果云 等通用，读多写少）。
    ///
    /// 只用到三个动作：
    ///   PROPFIND  列目录（Depth:1 取一层）
    ///   GET       下载文件（先写 .part 再改名，避免半截文件被当成完整缓存）
    ///   （HEAD 由下载时的 Content-Length 代替，少一次往返）
    ///
    /// 认证交给 <see cref="HttpClientHandler.Credentials"/>：先匿名发一次，收到 401 挑战后
    /// 由 .NET 按服务器要求重发（Basic / Digest / NTLM 都能协商）。比手工拼 Authorization 头稳，
    /// 因为不同服务端要求的方案不一样。
    ///
    /// 代理：默认「跟随系统」——Windows 上 .NET 会读系统代理设置，所以平时挂着代理的用户
    /// 不用额外配置；也可显式指定地址或彻底关闭（局域网直连 NAS 时用关闭更干净）。
    ///
    /// ⚠️ 本类只做网络读写，绝不参与解码 / DSP。文件下载到本地后，播放链路与本地文件完全一致，
    /// bit-perfect 不受影响 —— 这也是「先落地再播」而不是「直接流式播」的原因。
    /// </summary>
    internal sealed class WebDavClient : IDisposable
    {
        private static readonly XNamespace Dav = "DAV:";

        private const string PropFindBody =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<D:propfind xmlns:D=\"DAV:\"><D:prop>" +
            "<D:displayname/><D:resourcetype/><D:getcontentlength/><D:getlastmodified/>" +
            "</D:prop></D:propfind>";

        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public WebDavClient(string url, string user, string password,
                            string proxyMode, string proxyHost, int proxyPort)
        {
            _baseUrl = (url ?? string.Empty).Trim().TrimEnd('/');

            var handler = new HttpClientHandler
            {
                PreAuthenticate = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            if (!string.IsNullOrEmpty(user))
            {
                handler.Credentials = new NetworkCredential(user, password ?? string.Empty);
            }

            ApplyProxy(handler, proxyMode, proxyHost, proxyPort);

            _http = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteMusicPlayer WebDAV");
        }

        /// <summary>服务器地址（已去掉尾部斜杠）。</summary>
        public string BaseUrl => _baseUrl;

        private static void ApplyProxy(HttpClientHandler handler, string mode, string host, int port)
        {
            switch ((mode ?? "System").Trim().ToLowerInvariant())
            {
                case "none":
                    // 局域网直连 NAS：显式关掉代理，避免系统代理把内网地址也劫走。
                    handler.UseProxy = false;
                    break;

                case "manual":
                    if (!string.IsNullOrWhiteSpace(host))
                    {
                        handler.Proxy = new WebProxy($"http://{host.Trim()}:{port}");
                        handler.UseProxy = true;
                    }
                    break;

                default:
                    // System：什么都不设，走 .NET 默认（Windows 读系统代理）。
                    break;
            }
        }

        /// <summary>
        /// 把相对路径拼成完整 URI。逐段编码，避免中文 / 空格 / 特殊字符导致 404。
        /// （不能用 Uri.EscapeUriString 之类整体转义，否则会连 "https://" 里的斜杠一起处理。）
        /// </summary>
        private Uri BuildUri(string relativePath)
        {
            if (string.IsNullOrEmpty(_baseUrl))
            {
                throw new WebDavException("服务器地址是空的", "请先填写 WebDAV 服务器地址。");
            }

            var sb = new StringBuilder(_baseUrl);
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                foreach (string seg in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.Append('/').Append(Uri.EscapeDataString(seg));
                }
            }
            else
            {
                sb.Append('/');
            }

            if (!Uri.TryCreate(sb.ToString(), UriKind.Absolute, out Uri? uri))
            {
                throw new WebDavException("服务器地址格式不对",
                    $"填的是「{_baseUrl}」。正确格式形如 https://服务器地址/路径 ，必须以 http:// 或 https:// 开头。");
            }

            return uri;
        }

        /// <summary>列出一层目录（不含递归）。relativePath 为空表示服务器根目录。</summary>
        public async Task<IReadOnlyList<WebDavEntry>> ListAsync(string relativePath, CancellationToken ct = default)
        {
            Uri uri = BuildUri(relativePath);

            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
            req.Headers.Add("Depth", "1");
            req.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw WrapNetworkFailure(ex, uri);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    throw FromStatus(resp.StatusCode, uri);
                }

                string xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseMultiStatus(xml, relativePath);
            }
        }

        /// <summary>
        /// 下载到本地文件，返回本地路径。先写 <c>.part</c> 临时文件，下完才改名 —— 中途断了不会留下
        /// 一个"看起来完整"的半截文件被后续当成缓存用。
        /// </summary>
        public async Task DownloadAsync(string relativePath, string localPath,
                                       IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Uri uri = BuildUri(relativePath);

            HttpResponseMessage resp;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw WrapNetworkFailure(ex, uri);
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    throw FromStatus(resp.StatusCode, uri);
                }

                long total = resp.Content.Headers.ContentLength ?? -1L;
                string dir = Path.GetDirectoryName(localPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string temp = localPath + ".part";
                long done = 0;

                await using (Stream src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                                                     1 << 16, useAsync: true))
                {
                    byte[] buf = new byte[1 << 16];
                    int n;
                    while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        if (total > 0)
                        {
                            progress?.Report((double)done / total);
                        }
                    }
                }

                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }
                File.Move(temp, localPath);
            }
        }

        /// <summary>测试连接：列一次根目录，把结果翻译成人话。</summary>
        public async Task<WebDavTestResult> TestConnectionAsync(CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                IReadOnlyList<WebDavEntry> items = await ListAsync(string.Empty, ct).ConfigureAwait(false);
                sw.Stop();

                string preview = items.Count == 0
                    ? "服务器通了，但根目录下没有内容。"
                    : "服务器通了。根目录下有 " + items.Count + " 项，前几项：" +
                      string.Join("、", items.Take(6).Select(x => x.IsFolder ? x.Name + "/" : x.Name));

                return new WebDavTestResult
                {
                    Ok = true,
                    Headline = $"连接成功（{sw.ElapsedMilliseconds} ms）",
                    Detail = preview,
                    Items = items
                };
            }
            catch (OperationCanceledException)
            {
                return new WebDavTestResult { Ok = false, Headline = "已取消", Detail = "连接测试被中止。" };
            }
            catch (WebDavException ex)
            {
                return new WebDavTestResult { Ok = false, Headline = ex.Headline, Detail = ex.Message };
            }
            catch (Exception ex)
            {
                return new WebDavTestResult { Ok = false, Headline = "连接失败", Detail = DescribeFailure(ex) };
            }
        }

        /// <summary>把 multistatus 响应解析成目录项列表（跳过表示目录自身的那一条）。</summary>
        private IReadOnlyList<WebDavEntry> ParseMultiStatus(string xml, string? requestPath)
        {
            var list = new List<WebDavEntry>();

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (Exception ex)
            {
                throw new WebDavException("服务器返回的内容不是 WebDAV 格式",
                    "地址可能指向了一个普通网页，而不是 WebDAV 服务。原始返回无法解析： " + ex.Message, ex);
            }

            string reqPath = (requestPath ?? string.Empty).Trim('/');

            foreach (XElement response in doc.Descendants(Dav + "response"))
            {
                string href = response.Element(Dav + "href")?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                // propstat 里可能有多个状态块，取 200 的那个。
                XElement? prop = response.Elements(Dav + "propstat")
                    .Where(ps => (ps.Element(Dav + "status")?.Value ?? string.Empty).Contains("200", StringComparison.Ordinal))
                    .Select(ps => ps.Element(Dav + "prop"))
                    .FirstOrDefault(p => p != null);

                prop ??= response.Descendants(Dav + "prop").FirstOrDefault();
                if (prop == null)
                {
                    continue;
                }

                bool isFolder = prop.Element(Dav + "resourcetype")?.Element(Dav + "collection") != null;

                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(href);
                }
                catch
                {
                    decoded = href;
                }

                // href 可能带完整 scheme/host，也可能带 baseUrl 里的路径前缀；
                // 统一按"服务器根目录之后的相对路径"归一，和 BuildUri 的输入保持一致。
                string rel = ToRelative(decoded, requestPath);

                // 第一项通常是目录自身（href 等于请求路径）——跳过，否则列表里会多一个"当前目录"。
                if (string.IsNullOrEmpty(rel) || string.Equals(rel.Trim('/'), reqPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = Path.GetFileName(rel.TrimEnd('/'));
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                long size = 0;
                string sizeText = prop.Element(Dav + "getcontentlength")?.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sizeText))
                {
                    _ = long.TryParse(sizeText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                }

                DateTimeOffset? modified = TryParseHttpDate(prop.Element(Dav + "getlastmodified")?.Value);

                list.Add(new WebDavEntry
                {
                    Name = name,
                    RelativePath = rel.Trim('/'),
                    IsFolder = isFolder,
                    Size = size,
                    ModifiedUtc = modified
                });
            }

            // 文件夹在前、同类按名称排——和本地文件夹浏览的观感一致。
            return list
                .OrderByDescending(e => e.IsFolder)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 把 href 归一成「相对服务器根目录的路径」。
        /// 需要处理服务器返回 path 前缀和 baseUrl 里 path 前缀不一致的情况，所以取"baseUrl 之后"的那一段。
        /// </summary>
        private string ToRelative(string href, string? requestPath)
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out Uri? abs))
            {
                href = abs.AbsolutePath;
            }

            string basePath = string.Empty;
            if (Uri.TryCreate(_baseUrl, UriKind.Absolute, out Uri? baseAbs))
            {
                basePath = baseAbs.AbsolutePath.TrimEnd('/');
            }

            string path = href;
            if (!string.IsNullOrEmpty(basePath) &&
                path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(basePath.Length);
            }

            if (string.IsNullOrEmpty(requestPath))
            {
                return path.Trim('/');
            }

            // 返回的是请求路径下的子项：去掉请求前缀，只留最后一段（调用方按需再拼）。
            string prefix = "/" + requestPath.Trim('/') + "/";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(prefix.Length);
            }
            else if (string.Equals(path.Trim('/'), requestPath.Trim('/'), StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return path.Trim('/');
        }

        /// <summary>
        /// 解析 WebDAV 的 getlastmodified 时间。
        ///
        /// 服务器返回的是 HTTP 日期（RFC 1123，形如 "Wed, 17 Sep 2026 10:00:00 GMT"）。
        /// 这里刻意**先去掉开头的星期几**再解析，原因有二：
        ///   1. 星期几是冗余信息（日期本身已经决定星期），丢了不影响结果；
        ///   2. 部分运行环境的日期解析对星期缩写（ddd）匹配不可靠，会连带整串解析失败
        ///      —— 表现为 ModificationTime 永远为空，而其它字段都正常，很难排查。
        /// 去掉之后用显式格式串解析，既绕开该问题，又比宽松解析更可预测。
        /// </summary>
        private static DateTimeOffset? TryParseHttpDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string trimmed = text.Trim();

            // 剥掉 "Wed, " / "Wednesday, " 这类星期前缀（逗号只可能出现在前 10 个字符内）。
            int comma = trimmed.IndexOf(',');
            if (comma >= 0 && comma <= 10)
            {
                trimmed = trimmed.Substring(comma + 1).Trim();
            }

            // GMT/UTC 后缀交给格式串里的 'GMT' / 'UTC' 处理不方便，统一去掉，
            // 反正 WebDAV 时间语义就是 UTC。
            trimmed = trimmed
                .Replace("GMT", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("UTC", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            string[] formats =
            {
                "dd MMM yyyy HH:mm:ss",          // 去掉时区后的 RFC1123
                "dd MMM yyyy HH:mm:ss zzz",      // 带 +0000 形式时区
                "dd MMM yyyy HH:mm:ss K",
            };

            if (DateTimeOffset.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture,
                                             DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                             out DateTimeOffset exact))
            {
                return exact;
            }

            // 兜底：少数非标准实现会发 ISO 8601 之类，宽松解析再试一次。
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset loose))
            {
                return loose;
            }

            return null;
        }

        private static WebDavException FromStatus(HttpStatusCode code, Uri uri)
        {
            string headline = (int)code switch
            {
                401 => "用户名或密码不对",
                403 => "账号没有权限访问这个目录",
                404 => "地址后面的路径不存在",
                405 => "服务器不接受这个操作（可能不是标准 WebDAV 服务）",
                407 => "代理要求额外的账号密码",
                500 or 502 or 503 or 504 => "服务器暂时出错或不可用",
                _ => "服务器返回了错误（HTTP " + (int)code + "）"
            };

            return new WebDavException(headline,
                "请求地址：" + uri + Environment.NewLine +
                "HTTP 状态：" + (int)code + " " + code);
        }

        private static WebDavException WrapNetworkFailure(Exception ex, Uri uri)
        {
            return new WebDavException("连不上服务器", DescribeFailure(ex) + Environment.NewLine + "请求地址：" + uri, ex);
        }

        /// <summary>
        /// 把底层网络异常翻译成"该去检查什么"。这一步对使用者最有用 ——
        /// DNS 失败和密码错误的表现都是"连不上"，但排查方向完全不同。
        /// </summary>
        private static string DescribeFailure(Exception ex)
        {
            Exception inner = ex;
            while (inner.InnerException != null)
            {
                inner = inner.InnerException;
            }

            if (inner is SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    SocketError.HostNotFound =>
                        "找不到这个服务器地址（域名解析失败）。请检查地址是否拼错；如果服务器在境外，确认代理已开启。",
                    SocketError.ConnectionRefused =>
                        "对方拒绝连接。地址或端口可能写错了。",
                    SocketError.TimedOut =>
                        "连接超时。网络到不了服务器 —— 如果平时需要代理才能访问，请在下面把代理设为「跟随系统」或手动填写。",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                        "网络不可达。代理可能没生效，或网络已断开。",
                    _ => "网络错误：" + se.Message + "（" + se.SocketErrorCode + "）"
                };
            }

            if (ex is System.Security.Authentication.AuthenticationException)
            {
                return "HTTPS 证书校验失败。如果服务器用的是自签名证书，先在浏览器里访问一次并信任它。";
            }

            if (ex is TaskCanceledException || ex is TimeoutException)
            {
                return "请求超时（30 秒）。服务器响应太慢或网络不通 —— 检查代理设置。";
            }

            return ex.Message;
        }

        public void Dispose() => _http.Dispose();
    }
}
