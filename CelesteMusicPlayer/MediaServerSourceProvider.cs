using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer;

internal sealed class MediaServerSourceProvider : NetworkSourceProvider
{
    private static readonly ConcurrentDictionary<string, (string token, string user)> Sessions = new();
    private static string SessionKey(NetworkSource s) => s.Id + ":" + s.Revision;
    private async Task<(string token, string user)> AuthAsync(NetworkSource source, CancellationToken ct)
    {
        if (source.Auth == "token") return (source.Secret, source.UserId);
        if (Sessions.TryGetValue(SessionKey(source), out var known)) return known;
        using var request = Request(source, "Users/AuthenticateByName", "");
        request.Method = HttpMethod.Post;
        request.Content = new StringContent(JsonSerializer.Serialize(new { Username = source.Username, Pw = source.Secret }), Encoding.UTF8, "application/json");
        var response = await JsonAsync(source, request, ct);
        string token = Text(response, "AccessToken");
        string user = response.TryGetProperty("User", out var u) ? Text(u, "Id") : "";
        if (token.Length == 0 || user.Length == 0) throw new InvalidOperationException("服务器未返回有效登录会话。");
        Sessions[SessionKey(source)] = (token, user); return (token, user);
    }
    private static HttpRequestMessage Request(NetworkSource source, string route, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url(source, route));
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", "MediaBrowser Client=\"CelesteMusicPlayer\", Device=\"Windows\", DeviceId=\"Celeste-" + source.Id + "\", Version=\"1.0\"");
        if (token.Length > 0) request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
        return request;
    }
    private async Task<JsonElement> GetAsync(NetworkSource source, string route, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var auth = await AuthAsync(source, ct);
            try { return await JsonAsync(source, Request(source, route, auth.token), ct); }
            catch (InvalidOperationException) when (attempt == 0 && source.Auth != "token") { Sessions.TryRemove(SessionKey(source), out _); }
        }
    }
    private static NetworkEntry Entry(JsonElement item)
    {
        bool folder = Text(item, "IsFolder") == "True" || Text(item, "Type") != "Audio";
        string id = Text(item, "Id"), name = Text(item, "Name");
        return new NetworkEntry { Key = id, Path = id, Name = name, Folder = folder, Title = name,
            Artist = string.Join(" / ", Array(item, "Artists").Select(x => x.ToString())), Album = Text(item, "Album"), AlbumArtist = Text(item, "AlbumArtist"),
            Duration = Number(item, "RunTimeTicks") / 10_000_000, Track = (int)Number(item, "IndexNumber"), Disc = (int)Number(item, "ParentIndexNumber"),
            Extension = Text(item, "Container", "flac"), Size = (long)Number(item, "Size"), Version = Text(item, "DateModified"), CoverKey = id };
    }
    internal override async Task<NetworkEntry[]> BrowseAsync(NetworkSource source, string path, CancellationToken ct)
    {
        var auth = await AuthAsync(source, ct);
        if (path.Length == 0 && source.Root.Length == 0)
        {
            string route = auth.user.Length > 0 ? "Users/" + Uri.EscapeDataString(auth.user) + "/Views" : "Library/VirtualFolders";
            var doc = await GetAsync(source, route, ct);
            var roots = doc.ValueKind == JsonValueKind.Array ? doc.EnumerateArray().ToArray() : Array(doc, "Items");
            return roots.Select(item => { var entry = Entry(item); entry.Key = Text(item, "ItemId", entry.Key); entry.Path = entry.Key; entry.Folder = true; return entry; }).Where(x => x.Key.Length > 0).ToArray();
        }
        return await ReadItemsAsync(source, path.Length == 0 ? source.Root : path, false, ct);
    }
    private async Task<NetworkEntry[]> ReadItemsAsync(NetworkSource source, string parent, bool recursive, CancellationToken ct)
    {
        var auth = await AuthAsync(source, ct); var result = new List<NetworkEntry>();
        for (int offset = 0; ; offset += 300)
        {
            var query = new Dictionary<string, string> { ["StartIndex"] = offset.ToString(), ["Limit"] = "300", ["Recursive"] = recursive ? "true" : "false", ["Fields"] = "Path,MediaSources,DateCreated,DateModified", ["SortBy"] = "SortName", ["SortOrder"] = "Ascending" };
            if (parent.Length > 0) query["ParentId"] = parent;
            if (auth.user.Length > 0) query["UserId"] = auth.user;
            if (recursive) query["IncludeItemTypes"] = "Audio";
            var json = await GetAsync(source, Query("Items", query), ct);
            var batch = Array(json, "Items"); result.AddRange(batch.Select(Entry));
            if (batch.Length < 300 || Number(json, "TotalRecordCount") > 0 && result.Count >= Number(json, "TotalRecordCount")) break;
            if (offset > 1_000_000) throw new InvalidOperationException("服务器分页异常，同步已停止，旧索引保留。");
        }
        return result.ToArray();
    }
    internal override async IAsyncEnumerable<NetworkEntry> ScanAsync(NetworkSource source, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in await ReadItemsAsync(source, source.Root, true, ct)) { ct.ThrowIfCancellationRequested(); yield return item; }
    }
    internal override async Task DownloadAsync(NetworkSource source, NetworkEntry entry, string destination, CancellationToken ct)
    {
        var auth = await AuthAsync(source, ct);
        await DownloadHttpAsync(source, Request(source, "Audio/" + Uri.EscapeDataString(entry.Key) + "/stream?static=true", auth.token), destination, ct);
    }
    internal override async Task<byte[]?> CoverAsync(NetworkSource source, NetworkEntry entry, CancellationToken ct)
    {
        var auth = await AuthAsync(source, ct);
        return await SmallBytesAsync(source, Request(source, "Items/" + Uri.EscapeDataString(entry.CoverKey.Length > 0 ? entry.CoverKey : entry.Key) + "/Images/Primary?MaxWidth=480", auth.token), ct);
    }
}

internal sealed class SubsonicSourceProvider : NetworkSourceProvider
{
    private static string Endpoint(NetworkSource s, string method, Dictionary<string, string>? args = null)
    {
        string salt = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        var query = args ?? new Dictionary<string, string>();
        query["u"] = s.Username; query["s"] = salt;
        query["t"] = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(s.Secret + salt))).ToLowerInvariant();
        query["v"] = "1.16.1"; query["c"] = "CelesteMusicPlayer"; query["f"] = "json";
        return Query(Url(s, "rest/" + method + ".view"), query);
    }
    private static async Task<JsonElement> GetAsync(NetworkSource source, string method, Dictionary<string, string>? args, CancellationToken ct)
    {
        var json = await JsonAsync(source, new HttpRequestMessage(HttpMethod.Get, Endpoint(source, method, args)), ct);
        if (!json.TryGetProperty("subsonic-response", out var response)) throw new InvalidOperationException("该地址没有返回 Subsonic 数据。");
        if (Text(response, "status") != "ok") throw new InvalidOperationException("Subsonic 请求失败：" + (response.TryGetProperty("error", out var error) ? Text(error, "message") : "未知错误"));
        return response.Clone();
    }
    private static NetworkEntry Song(JsonElement song) => new()
    {
        Key = Text(song, "id"), Path = Text(song, "id"), Name = Text(song, "title"), Title = Text(song, "title"),
        Artist = Text(song, "artist"), Album = Text(song, "album"), AlbumArtist = Text(song, "albumArtist", Text(song, "artist")),
        Track = (int)Number(song, "track"), Disc = (int)Number(song, "discNumber"), Duration = Number(song, "duration"),
        Extension = Text(song, "suffix", "flac"), Size = (long)Number(song, "size"), CoverKey = Text(song, "coverArt"), Version = Text(song, "created")
    };
    internal override async Task<NetworkEntry[]> BrowseAsync(NetworkSource source, string path, CancellationToken ct)
    {
        if (path.Length == 0)
        {
            var response = await GetAsync(source, "getArtists", null, ct);
            if (!response.TryGetProperty("artists", out var artists)) return System.Array.Empty<NetworkEntry>();
            return Array(artists, "index").SelectMany(index => Array(index, "artist")).Select(a => new NetworkEntry { Key = "artist:" + Text(a, "id"), Path = "artist:" + Text(a, "id"), Name = Text(a, "name"), Folder = true }).ToArray();
        }
        if (path.StartsWith("artist:", StringComparison.Ordinal))
        {
            var response = await GetAsync(source, "getArtist", new() { ["id"] = path.Substring(7) }, ct);
            return Array(response.GetProperty("artist"), "album").Select(a => new NetworkEntry { Key = "album:" + Text(a, "id"), Path = "album:" + Text(a, "id"), Name = Text(a, "name"), Folder = true }).ToArray();
        }
        if (!path.StartsWith("album:", StringComparison.Ordinal)) throw new ArgumentException("无效 Subsonic 目录。");
        var album = await GetAsync(source, "getAlbum", new() { ["id"] = path.Substring(6) }, ct);
        return Array(album.GetProperty("album"), "song").Select(Song).ToArray();
    }
    internal override async IAsyncEnumerable<NetworkEntry> ScanAsync(NetworkSource source, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int offset = 0; ; offset += 300)
        {
            var page = await GetAsync(source, "getAlbumList2", new() { ["type"] = "alphabeticalByName", ["size"] = "300", ["offset"] = offset.ToString() }, ct);
            var albums = Array(page.GetProperty("albumList2"), "album");
            foreach (var album in albums)
                foreach (var song in await BrowseAsync(source, "album:" + Text(album, "id"), ct)) if (seen.Add(song.Key)) yield return song;
            if (albums.Length < 300) break;
            if (offset > 1_000_000) throw new InvalidOperationException("服务器分页异常，旧索引保留。");
        }
    }
    internal override Task DownloadAsync(NetworkSource source, NetworkEntry entry, string destination, CancellationToken ct)
        => DownloadHttpAsync(source, new HttpRequestMessage(HttpMethod.Get, Endpoint(source, "download", new() { ["id"] = entry.Key })), destination, ct);
    internal override Task<byte[]?> CoverAsync(NetworkSource source, NetworkEntry entry, CancellationToken ct) => entry.CoverKey.Length == 0 ? Task.FromResult<byte[]?>(null)
        : SmallBytesAsync(source, new HttpRequestMessage(HttpMethod.Get, Endpoint(source, "getCoverArt", new() { ["id"] = entry.CoverKey, ["size"] = "480" })), ct);
}
