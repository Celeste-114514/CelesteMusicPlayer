using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer;

internal abstract class NetworkSourceProvider
{
    internal static readonly string[] Providers = { "webdav", "baidu", "jellyfin", "emby", "smb", "sshfs", "subsonic" };
    internal static NetworkSourceProvider For(NetworkSource source) => source.Provider switch
    {
        "webdav" => new WebDavSourceProvider(), "smb" or "sshfs" => new MountedSourceProvider(),
        "jellyfin" or "emby" => new MediaServerSourceProvider(), "subsonic" => new SubsonicSourceProvider(),
        "baidu" => new BaiduSourceProvider(), _ => throw new ArgumentException("不支持的网络来源。")
    };
    internal abstract Task<NetworkEntry[]> BrowseAsync(NetworkSource source, string path, CancellationToken ct);
    internal virtual async IAsyncEnumerable<NetworkEntry> ScanAsync(NetworkSource source, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Queue<string>(); pending.Enqueue(""); var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested(); string path = pending.Dequeue();
            if (!visited.Add(path)) continue;
            foreach (var entry in await BrowseAsync(source, path, ct))
            {
                if (entry.Folder) pending.Enqueue(entry.Key); else yield return entry;
            }
        }
    }
    internal abstract Task DownloadAsync(NetworkSource source, NetworkEntry entry, string destination, CancellationToken ct);
    internal virtual Task<byte[]?> CoverAsync(NetworkSource source, NetworkEntry entry, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    internal virtual bool Sidecars => false;
    internal static string Text(JsonElement obj, string key, string fallback = "") => obj.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : fallback;
    internal static double Number(JsonElement obj, string key) => double.TryParse(Text(obj, key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
    internal static JsonElement[] Array(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : System.Array.Empty<JsonElement>();
    internal static string Query(string url, IEnumerable<KeyValuePair<string, string>> args) => url + (url.Contains('?') ? "&" : "?") + string.Join("&", args.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    internal static string Url(NetworkSource source, string route) => source.Endpoint.TrimEnd('/') + "/" + route.TrimStart('/');
    internal static HttpClient Http(NetworkSource source)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        if (source.ProxyMode == "None") handler.UseProxy = false;
        else if (source.ProxyMode == "Manual") handler.Proxy = new WebProxy(source.ProxyHost, source.ProxyPort);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteMusicPlayer/1.0");
        return client;
    }
    internal static async Task<JsonElement> JsonAsync(NetworkSource source, HttpRequestMessage request, CancellationToken ct)
    {
        using (request) using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            using var client = Http(source); using var response = await client.SendAsync(request, timeout.Token);
            Check(response);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token)); return doc.RootElement.Clone();
        }
    }
    internal static void Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "认证失效，请检查账号或重新登录。",
            HttpStatusCode.Forbidden => "服务器拒绝访问，请检查账号权限。",
            HttpStatusCode.NotFound => "服务器地址或文件不存在。",
            _ => "服务器请求失败，HTTP " + (int)response.StatusCode
        });
    }
    internal static async Task DownloadHttpAsync(NetworkSource source, HttpRequestMessage request, string destination, CancellationToken ct)
    {
        using (request) using (var client = Http(source))
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); Check(response);
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("json") || type.Contains("html")) throw new InvalidOperationException("服务器返回登录/错误页面，未取得原始音频。");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, ct);
            if (output.Length == 0 || response.Content.Headers.ContentLength is long expected && output.Length != expected)
                throw new IOException("下载不完整，请重试。");
        }
    }
    internal static async Task<byte[]?> SmallBytesAsync(NetworkSource source, HttpRequestMessage request, CancellationToken ct)
    {
        using (request) using (var client = Http(source))
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            Check(response);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream(); byte[] buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + read > 20_000_000) throw new IOException("封面文件过大。");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }
}

internal sealed class WebDavSourceProvider : NetworkSourceProvider
{
    internal override bool Sidecars => true;
    private static WebDavClient Client(NetworkSource s) => new(s.Endpoint, s.Username, s.Secret, s.ProxyMode, s.ProxyHost, s.ProxyPort);
    internal override async Task<NetworkEntry[]> BrowseAsync(NetworkSource source, string path, CancellationToken ct)
    {
        string folder = string.IsNullOrEmpty(path) ? NetworkIdentity.Normalize(source.Root) : NetworkIdentity.Normalize(path);
        using var client = Client(source);
        var entries = await client.ListAsync(folder.TrimStart('/'), ct);
        return entries.Select(entry =>
        {
            string key = NetworkIdentity.Join(folder, entry.RelativePath);
            return new NetworkEntry { Key = key, Path = key, Name = entry.Name, Folder = entry.IsFolder, Size = entry.Size,
                Version = entry.ModifiedUtc?.ToString("O") ?? "", Extension = Path.GetExtension(entry.Name) };
        }).ToArray();
    }
    internal override async Task DownloadAsync(NetworkSource source, NetworkEntry entry, string destination, CancellationToken ct)
    {
        using var client = Client(source); await client.DownloadAsync(entry.Path.TrimStart('/'), destination, ct: ct);
    }
}

internal sealed class MountedSourceProvider : NetworkSourceProvider
{
    internal override bool Sidecars => true;
    internal static string Resolve(NetworkSource source, string path)
    {
        string root = Path.GetFullPath(source.Endpoint);
        if (!string.IsNullOrWhiteSpace(source.Root)) root = Path.GetFullPath(Path.Combine(root, source.Root.TrimStart('/', '\\')));
        string relative = NetworkIdentity.Normalize(path).TrimStart('/');
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.Equals(root, full, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路径超出来源根目录。");
        return full;
    }
    internal override Task<NetworkEntry[]> BrowseAsync(NetworkSource source, string path, CancellationToken ct) => Task.Run(() =>
    {
        string root = Resolve(source, path);
        var items = new List<NetworkEntry>();
        foreach (string file in Directory.EnumerateFileSystemEntries(root))
        {
            ct.ThrowIfCancellationRequested(); var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            bool folder = (info.Attributes & FileAttributes.Directory) != 0;
            string key = NetworkIdentity.Join(path, Path.GetFileName(file));
            var item = new NetworkEntry { Key = key, Path = key, Name = Path.GetFileName(file), Folder = folder,
                Size = folder ? 0 : info.Length, Version = info.LastWriteTimeUtc.Ticks.ToString(), Extension = Path.GetExtension(file) };
            if (!folder && NetworkIdentity.Audio.Contains(item.Extension))
            {
                try
                {
                    using var tags = TagLib.File.Create(file); item.Title = tags.Tag.Title ?? "";
                    item.Artist = tags.Tag.FirstPerformer ?? ""; item.Album = tags.Tag.Album ?? ""; item.AlbumArtist = tags.Tag.FirstAlbumArtist ?? "";
                    item.Track = (int)tags.Tag.Track; item.Disc = (int)tags.Tag.Disc; item.Duration = tags.Properties.Duration.TotalSeconds;
                }
                catch (Exception) { /* 文件名仍可浏览；播放时才报告不可解码 */ }
            }
            items.Add(item);
        }
        return items.ToArray();
    }, ct);
    internal override async Task DownloadAsync(NetworkSource source, NetworkEntry entry, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(Resolve(source, entry.Path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, ct);
    }
}
