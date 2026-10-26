using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer;

internal sealed class BaiduSourceProvider : NetworkSourceProvider
{
    internal override bool Sidecars => true;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshGates = new();
    internal static string AuthorizeUrl(NetworkSource source)
    {
        if (source.ClientId.Length == 0) throw new ArgumentException("请先填写 Celeste 使用的百度应用 App Key，再保存来源。");
        return Query("https://openapi.baidu.com/oauth/2.0/authorize", new Dictionary<string, string>
        { ["response_type"] = "code", ["client_id"] = source.ClientId, ["redirect_uri"] = "oob", ["scope"] = "basic,netdisk", ["display"] = "popup" });
    }
    internal static async Task ExchangeAsync(NetworkSource source, string code, CancellationToken ct)
    {
        await TokenAsync(source, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = "oob" }, ct);
    }
    private static async Task TokenAsync(NetworkSource source, Dictionary<string, string> args, CancellationToken ct)
    {
        if (source.ClientId.Length == 0 || source.ClientSecret.Length == 0) throw new ArgumentException("百度授权需要当前应用的 App Key 和 Secret Key；也可以手动填入已取得的 Access Token。");
        args["client_id"] = source.ClientId; args["client_secret"] = source.ClientSecret;
        var request = new HttpRequestMessage(HttpMethod.Post, "https://openapi.baidu.com/oauth/2.0/token") { Content = new FormUrlEncodedContent(args) };
        var json = await JsonAsync(source, request, ct);
        string token = Text(json, "access_token");
        if (token.Length == 0) throw new InvalidOperationException("百度授权失败，请重新获取授权码并检查应用配置。");
        var current = NetworkLibraryStore.Source(source.Id);
        if (current.Revision != source.Revision) throw new InvalidOperationException("来源配置已变化，请重试授权。");
        source.ProtectedSecret = NetworkSecrets.Protect(token);
        string refresh = Text(json, "refresh_token");
        if (refresh.Length > 0) source.ProtectedRefreshToken = NetworkSecrets.Protect(refresh);
        NetworkLibraryStore.SaveSource(source);
    }
    private static async Task RefreshAsync(NetworkSource source, string expired, CancellationToken ct)
    {
        var gate = RefreshGates.GetOrAdd(source.Id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            var current = NetworkLibraryStore.Source(source.Id);
            if (current.Revision != source.Revision) throw new InvalidOperationException("来源配置已变化，请重试。");
            if (current.Secret != expired) { source.ProtectedSecret = current.ProtectedSecret; source.ProtectedRefreshToken = current.ProtectedRefreshToken; return; }
            if (current.RefreshToken.Length == 0) throw new InvalidOperationException("百度授权已过期，请重新授权。");
            await TokenAsync(source, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = current.RefreshToken }, ct);
        }
        finally { gate.Release(); }
    }
    private static async Task<JsonElement> ApiAsync(NetworkSource source, string api, Dictionary<string, string> args, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            string token = source.Secret;
            if (token.Length == 0) throw new InvalidOperationException("请先完成百度授权或填写 Access Token。");
            args["access_token"] = token;
            var json = await JsonAsync(source, new HttpRequestMessage(HttpMethod.Get, Query(Url(source, "rest/2.0/xpan/" + api), args)), ct);
            int errno = (int)Number(json, "errno");
            if (errno == 0 && !json.TryGetProperty("error_code", out _)) return json;
            int error = (int)Number(json, "error_code");
            if (attempt == 0 && (errno is -6 or 110 or 111 || error is 110 or 111)) { await RefreshAsync(source, token, ct); continue; }
            throw new InvalidOperationException("百度接口失败（代码 " + (errno != 0 ? errno : error) + "），请检查授权、文件权限或稍后重试。");
        }
    }
    internal override async Task<NetworkEntry[]> BrowseAsync(NetworkSource source, string path, CancellationToken ct)
    {
        string folder = NetworkIdentity.Normalize(path.Length == 0 ? source.Root : path); var list = new List<NetworkEntry>();
        for (int offset = 0; ; offset += 1000)
        {
            var json = await ApiAsync(source, "file", new() { ["method"] = "list", ["dir"] = folder, ["start"] = offset.ToString(), ["limit"] = "1000", ["order"] = "name" }, ct);
            var batch = Array(json, "list");
            foreach (var item in batch)
            {
                string itemPath = Text(item, "path"), name = Text(item, "server_filename");
                bool isDirectory = Number(item, "isdir") == 1;
                list.Add(new NetworkEntry { Key = isDirectory ? itemPath : Text(item, "fs_id"), Path = itemPath, Name = name, Folder = isDirectory,
                    Size = (long)Number(item, "size"), Version = Text(item, "server_mtime"), Extension = Path.GetExtension(name) });
            }
            if (batch.Length < 1000) break;
            if (offset > 1_000_000) throw new InvalidOperationException("百度目录分页异常，旧索引保留。");
        }
        return list.ToArray();
    }
    private static bool DownloadHost(NetworkSource source, Uri uri) => uri.Scheme == "https" && (uri.Host == "baidu.com" || uri.Host.EndsWith(".baidu.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".baidupcs.com", StringComparison.OrdinalIgnoreCase))
        || new Uri(source.Endpoint).IsLoopback && uri.IsLoopback;
    internal override async Task DownloadAsync(NetworkSource source, NetworkEntry entry, string destination, CancellationToken ct)
    {
        // Resolve afresh on every download; never persist an expiring dlink as a song identity.
        var json = await ApiAsync(source, "multimedia", new() { ["method"] = "filemetas", ["fsids"] = JsonSerializer.Serialize(new[] { long.Parse(entry.Key) }), ["dlink"] = "1" }, ct);
        string link = Array(json, "list").Select(x => Text(x, "dlink")).FirstOrDefault() ?? "";
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !DownloadHost(source, uri)) throw new InvalidOperationException("百度未返回可信的下载地址。");
        link = Query(link, new Dictionary<string, string> { ["access_token"] = source.Secret });
        for (int redirect = 0; redirect < 6; redirect++)
        {
            using var client = Http(source); client.DefaultRequestHeaders.UserAgent.Clear(); client.DefaultRequestHeaders.UserAgent.ParseAdd("pan.baidu.com");
            using var response = await client.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(new Uri(link), location);
                if (!DownloadHost(source, uri)) throw new InvalidOperationException("下载重定向地址不可信。");
                link = uri.ToString(); continue;
            }
            Check(response);
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("json") || type.Contains("html")) throw new InvalidOperationException("百度返回了错误页面，音频未下载。");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, ct);
            if (output.Length <= 0 || entry.Size > 0 && output.Length != entry.Size) throw new IOException("百度下载不完整，请重试。");
            return;
        }
        throw new InvalidOperationException("百度下载重定向过多。");
    }
}
