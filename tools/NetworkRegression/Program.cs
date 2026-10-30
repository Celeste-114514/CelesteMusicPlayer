using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CelesteMusicPlayer;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
AppSettingsStore.Root = Path.Combine(Path.GetTempPath(), "celeste-network-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(AppSettingsStore.Root);
// A real, minimal PCM WAV; downloaded files must match every byte.
using var wave = new MemoryStream();
using (var writer = new BinaryWriter(wave, Encoding.ASCII, true))
{
    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(8036); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(8000); writer.Write(new byte[8000]);
}
byte[] audio = wave.ToArray();
var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
using var listener = new HttpListener(); string endpoint = "http://127.0.0.1:" + port; listener.Prefixes.Add(endpoint + "/"); listener.Start();
bool fail = false, slow = false; int mediaPages = 0, downloads = 0, authCalls = 0;
var requests = new List<string>();
var serving = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync(); } catch { break; }
        _ = Task.Run(async () =>
        {
            try
            {
                string path = context.Request.Url!.AbsolutePath; lock (requests) requests.Add(path);
                if (slow) await Task.Delay(150);
                if (fail) { context.Response.StatusCode = 503; return; }
                object? json = null; byte[]? bytes = null;
                if (context.Request.HttpMethod == "PROPFIND")
                {
                    Check(context.Request.Headers["Depth"] == "1", "WebDAV depth");
                    context.Response.StatusCode = 207; context.Response.ContentType = "application/xml";
                    bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/dav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response><d:response><d:href>/dav/track.wav</d:href><d:propstat><d:prop><d:displayname>track.wav</d:displayname><d:resourcetype/><d:getcontentlength>" + audio.Length + "</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>");
                }
                else if (path.EndsWith("AuthenticateByName"))
                {
                    Interlocked.Increment(ref authCalls);
                    Check(context.Request.Headers["X-Emby-Authorization"]?.Contains("CelesteMusicPlayer") == true, "Media server client identity");
                    json = new { AccessToken = "fixture-session", User = new { Id = "user" } };
                }
                else if (path.EndsWith("/Views")) json = new { Items = new[] { new { Id = "music", Name = "Music", IsFolder = true, Type = "CollectionFolder" } } };
                else if (path == "/Items")
                {
                    Check(context.Request.Headers["X-Emby-Token"] == "fixture-session", "Media auth header");
                    Interlocked.Increment(ref mediaPages);
                    bool second = context.Request.QueryString["StartIndex"] == "300";
                    int count = context.Request.QueryString["Recursive"] == "true" ? second ? 1 : 300 : 1;
                    json = new { Items = Enumerable.Range(second ? 300 : 0, count).Select(i => new { Id = "track" + i, Name = "Track " + i, Type = "Audio", Container = "wav", Album = "Fixture", Artists = new[] { "Artist" }, RunTimeTicks = 5_000_000, Size = audio.Length }).ToArray(), TotalRecordCount = context.Request.QueryString["Recursive"] == "true" ? 301 : 1 };
                }
                else if (path.StartsWith("/rest/") && !path.StartsWith("/rest/2.0/"))
                {
                    var q = context.Request.QueryString;
                    Check(q["t"] == Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("password" + q["s"]))).ToLowerInvariant(), "Subsonic salted auth");
                    object response = path switch
                    {
                        "/rest/getArtists.view" => new { status = "ok", artists = new { index = new[] { new { artist = new[] { new { id = "artist", name = "Artist" } } } } } },
                        "/rest/getArtist.view" => new { status = "ok", artist = new { album = new[] { new { id = "album", name = "Fixture" } } } },
                        "/rest/getAlbumList2.view" => new { status = "ok", albumList2 = new { album = new[] { new { id = "album" } } } },
                        _ => new { status = "ok", album = new { song = new[] { new { id = "song", title = "Track", artist = "Artist", album = "Fixture", suffix = "wav", size = audio.Length, duration = 0.5 } } } }
                    };
                    if (path.EndsWith("download.view")) { bytes = audio; Interlocked.Increment(ref downloads); }
                    else json = new Dictionary<string, object> { ["subsonic-response"] = response };
                }
                else if (path.EndsWith("/xpan/file")) json = new { errno = 0, list = new[] { new { fs_id = 12345, path = "/track.wav", server_filename = "track.wav", isdir = 0, size = audio.Length, server_mtime = 1 } } };
                else if (path.EndsWith("/xpan/multimedia")) json = new { errno = 0, list = new[] { new { dlink = endpoint + "/download" } } };
                else if (path.Contains("/stream")) { Check(context.Request.QueryString["static"] == "true", "Original/static media request"); bytes = audio; Interlocked.Increment(ref downloads); }
                else { bytes = audio; Interlocked.Increment(ref downloads); }
                if (json != null) { bytes = JsonSerializer.SerializeToUtf8Bytes(json); context.Response.ContentType = "application/json"; }
                else if (context.Response.ContentType == null) context.Response.ContentType = "audio/wav";
                bytes ??= Array.Empty<byte>(); context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); context.Response.StatusCode = 500; }
            finally { context.Response.Close(); }
        });
    }
});
try
{
    var key = NetworkIdentity.TrackId("one", "/A/Track.wav");
    Check(key != NetworkIdentity.TrackId("two", "/A/Track.wav"), "Sources isolated");
    Check(key != NetworkIdentity.TrackId("one", "/a/Track.wav"), "Remote case preserved");
    try { NetworkIdentity.Normalize("../../escape"); throw new Exception("Traversal accepted"); } catch (ArgumentException) { }
    string protectedSecret = NetworkSecrets.Protect("password"); Check(!protectedSecret.Contains("password") && NetworkSecrets.Unprotect(protectedSecret) == "password", "DPAPI roundtrip");
    foreach (string provider in new[] { "webdav", "jellyfin", "emby", "subsonic", "baidu" })
    {
        var source = new NetworkSource { Provider = provider, Name = provider, Endpoint = endpoint + (provider == "webdav" ? "/dav" : ""), Username = "user", ProtectedSecret = protectedSecret, ProxyMode = "None" };
        NetworkLibraryStore.SaveSource(source);
        var adapter = NetworkSourceProvider.For(source);
        var browse = await adapter.BrowseAsync(source, "", CancellationToken.None); Check(browse.Length > 0, provider + " browse");
        var scanned = new List<NetworkEntry>(); await foreach (var e in adapter.ScanAsync(source, CancellationToken.None)) scanned.Add(e);
        Check(scanned.Count == (provider is "jellyfin" or "emby" ? 301 : 1), provider + " complete pagination");
        var track = NetworkLibraryService.MakeTrack(source, scanned[0]); NetworkLibraryStore.Put(new[] { track });
        string path = await NetworkLibraryService.ResolveAsync(track, CancellationToken.None);
        Check(File.ReadAllBytes(path).SequenceEqual(audio), provider + " original download");
        string reference = NetworkIdentity.Reference(track.Id); NetworkLibraryStore.Pin(track.Id);
        using (NetworkLibraryService.Lease(path)) { NetworkLibraryService.ClearCache(); Check(File.Exists(path), "Playing file lease survives cache clear"); }
        NetworkLibraryService.ClearCache(); Check(!File.Exists(path), "Cache removed after lease");
        Check(NetworkIdentity.Reference(NetworkLibraryStore.Track(track.Id)!.Id) == reference, "Reference survives cache clear");
        await NetworkLibraryService.ResolveAsync(track, CancellationToken.None);
        var changed = JsonSerializer.Deserialize<NetworkEntry>(JsonSerializer.Serialize(scanned[0]))!; changed.Version = "changed";
        Check(NetworkLibraryService.MakeTrack(source, changed).Id == track.Id, "Metadata/version does not change identity");
        Console.WriteLine("PASS " + provider + " browse / scan / download / cache / stable identity");
    }
    Check(authCalls >= 2 && mediaPages >= 4, "Both media server adapters paginated");
    string mounted = Path.Combine(AppSettingsStore.Root, "mount"); Directory.CreateDirectory(mounted); File.WriteAllBytes(Path.Combine(mounted, "disc.wav"), audio);
    File.WriteAllText(Path.Combine(mounted, "disc.cue"), "PERFORMER \"Artist\"\nFILE \"disc.wav\" WAVE\n TRACK 01 AUDIO\n TITLE \"First\"\n INDEX 01 00:00:00\n TRACK 02 AUDIO\n TITLE \"Second\"\n INDEX 01 00:00:15\n");
    foreach (string provider in new[] { "smb", "sshfs" })
    {
        var source = new NetworkSource { Provider = provider, Name = provider, Endpoint = mounted }; NetworkLibraryStore.SaveSource(source);
        var adapter = NetworkSourceProvider.For(source); var entries = await adapter.BrowseAsync(source, "", CancellationToken.None);
        var tracks = await NetworkLibraryService.BuildTracksAsync(source, entries.ToList(), adapter, CancellationToken.None);
        Check(tracks.Count == 2 && tracks.All(t => t.Cue) && tracks[0].StartFrame == 0 && tracks[1].StartFrame == 15, "CUE first track zero + separate identities");
        Check(tracks.Select(t => t.Id).Distinct().Count() == 2, "CUE identities");
        NetworkLibraryStore.Put(tracks); string a = await NetworkLibraryService.ResolveAsync(tracks[0], CancellationToken.None), b = await NetworkLibraryService.ResolveAsync(tracks[1], CancellationToken.None);
        Check(a == b, "CUE segments share asset download");
        NetworkLibraryService.StartSync(source.Id); while (NetworkLibraryService.Job(source.Id).Running) await Task.Delay(20);
        Check(NetworkLibraryService.Job(source.Id).Phase == "completed", "Mounted source sync");
        Console.WriteLine("PASS " + provider + " mounted adapter / CUE / shared asset / indexed sync");
    }
    var web = NetworkLibraryStore.Sources().First(s => s.Provider == "webdav");
    NetworkLibraryService.StartSync(web.Id); while (NetworkLibraryService.Job(web.Id).Running) await Task.Delay(20);
    int indexed = NetworkLibraryStore.Tracks(web.Id).Count(t => t.Indexed && !t.Missing); Check(indexed == 1, "Initial sync");
    fail = true; NetworkLibraryService.StartSync(web.Id); while (NetworkLibraryService.Job(web.Id).Running) await Task.Delay(20);
    Check(NetworkLibraryService.Job(web.Id).Phase == "failed" && NetworkLibraryStore.Tracks(web.Id).Count(t => t.Indexed && !t.Missing) == indexed, "Failure preserves old index");
    fail = false; slow = true; NetworkLibraryService.StartSync(web.Id); NetworkLibraryService.Cancel(web.Id); while (NetworkLibraryService.Job(web.Id).Running) await Task.Delay(20);
    Check(NetworkLibraryService.Job(web.Id).Phase == "cancelled" && NetworkLibraryStore.Tracks(web.Id).Count(t => t.Indexed && !t.Missing) == indexed, "Cancel preserves old index");
    var parsed = CueSourceParser.Parse("FILE \"one.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:00\nFILE \"two.wav\" WAVE\nTRACK 02 AUDIO\nINDEX 01 00:00:00");
    Check(parsed.Length == 2 && parsed[0].File != parsed[1].File, "Multiple FILE CUE");
    // sources 命令（list/save/revision 冲突/凭据不外泄/统一快照）的线上回包校验
    // 已由 tools/W13SourcesWireProbe.cjs 对真 exe 负责（命令本体在 WebSourcesService，
    // 本工程的桩空壳测不到它 —— 别在这里伪造 schema 再自证）。
    // —— WebDAV 浏览纯规则（2026-10-30 从 MainWindow.WebDav.cs 抽出）——
    Check(WebDavBrowseRules.CombineRemotePath("", "a/b") == "a/b", "root parent joins bare");
    Check(WebDavBrowseRules.CombineRemotePath("music", "music/sub/song.flac") == "music/sub/song.flac", "server full path not double-joined");
    Check(WebDavBrowseRules.CombineRemotePath("/music/", "/sub") == "music/sub", "slashes trimmed both sides");
    Check(WebDavBrowseRules.GetRemoteParent("a/b/c.flac") == "a/b", "parent of nested");
    Check(WebDavBrowseRules.GetRemoteParent("top.flac") == "" && WebDavBrowseRules.GetRemoteParent("") == "", "parent of root is empty");

    var davSettings = new AppSettingsState { WebDavUrl = "http://127.0.0.1/dav", WebDavDisplayName = "规则测试" };
    WebDavLocation davLoc = WebDavLocation.FromSettings(davSettings);
    Check(davLoc.IsConfigured, "dav location configured from stub settings");

    string davRoot = WebDavCache.GetRoot(davLoc);
    WebDavEntry[] davEntries =
    {
        new() { Name = "钢琴曲.flac", RelativePath = "钢琴曲.flac", IsFolder = false, Size = 12345678 },
        new() { Name = "cover.jpg", RelativePath = "cover.jpg", IsFolder = false, Size = 99 },
        new() { Name = "子目录", RelativePath = "子目录", IsFolder = true },
        new() { Name = "第二首.mp3", RelativePath = "第二首.mp3", IsFolder = false, Size = 0 }
    };
    string[] davAudio = { ".mp3", ".flac", ".m4a", ".ogg" };
    List<PlaylistItem> songs = WebDavBrowseRules.BuildSongList(davLoc, davEntries, "", davAudio,
        file => new PlaylistItem { FilePath = file, Title = "标签:" + Path.GetFileNameWithoutExtension(file) });
    Check(songs.Count == 2, "folders and non-audio filtered");
    Check(songs[0].Title == "钢琴曲" && songs[0].Artist == "网络音乐库" && songs[0].Album == "规则测试", "uncached item shape");
    Check(songs[0].RemoteHint == WebDavCache.FormatSize(12345678), "size hint formatted");
    Check(songs[1].RemoteHint == "未缓存" && songs[1].RemotePath == "第二首.mp3", "zero-size hint falls back to uncached");

    // 缓存命中（读本地标签）与坏缓存回退（半截下载 → 当未缓存）
    string cachedPath = WebDavCache.GetCachePath(davLoc, "钢琴曲.flac");
    Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
    File.WriteAllText(cachedPath, "这不是音频文件");
    List<PlaylistItem> broken = WebDavBrowseRules.BuildSongList(davLoc, davEntries, "", davAudio,
        file => throw new InvalidDataException("坏缓存也要能走到"));
    Check(broken[0].Title == "钢琴曲" && broken[0].RemoteHint == WebDavCache.FormatSize(12345678), "corrupt cache falls back to uncached shape");
    File.Delete(cachedPath);

    Check(WebDavBrowseRules.DescribeFailure(new WebDavException("连不上", "超时")) == "连不上：超时", "dav failure headline");
    Check(WebDavBrowseRules.DescribeFailure(new AggregateException(new WebDavException("包装", "内层"))) == "包装：内层", "aggregate unwrapped");
    Console.WriteLine("PASS scan failure / cancellation / multi-FILE / encrypted persistence; all seven providers covered by local fixtures (not real account certification)");
}
finally { listener.Stop(); await serving; }

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        internal List<PlaylistItem> LibraryTracks = new();
        private SmokeWindow? _webShellWindow;
        private static string ReadString(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
        private static string WebTrackId(PlaylistItem item) => item.SourceTrackId;
        private static PlaylistItem CreatePlaylistItemFromPath(string file) => new() { FilePath = file };
        private void StartPlayback(PlaylistItem item) { }
    }
    internal sealed class SmokeWindow { internal void NotifySourceStatus(string text) { } }
    public sealed class PlaylistItem
    {
        public string SourceId = "", SourceTrackId = "", SourceName = "", FilePath = "", RemotePath = "", RemoteHint = "", Title = "", Artist = "", Album = "", AlbumArtist = "";
        public bool IsCueSegment;
        public double StartTimeSeconds;
        public TimeSpan Duration;
        public uint Track, Disc;
        public string PersistenceKey => SourceTrackId.Length > 0 ? NetworkIdentity.Reference(SourceTrackId) : FilePath;
    }
    internal static class AppSettingsStore
    {
        internal static string Root = "";
        internal static string GetConfigDirectory() => Root;
        internal static AppSettingsState Load() => new();
    }
    internal sealed class AppSettingsState
    {
        public string WebDavUrl = "", WebDavDisplayName = "", WebDavUser = "", WebDavPassword = "", WebDavProxyMode = "System", WebDavProxyHost = "";
        public int WebDavProxyPort = 7890;
        public int WebDavCacheLimitMb = 4096;
        public string WebDavCacheDir = "";
        public bool AutoApplyCueOnScan = true;
    }
    internal static class StartupLog { internal static void WriteException(string name, Exception ex) => Console.Error.WriteLine(name + ": " + ex.Message);
        internal static void Write(string text) { } }
    // W13 搬动后的桩：MainWindow.WebSources.cs 现在把配置/浏览/同步转发给 WebSourcesService，
    // 起播/下载留在这里 —— 本工程只测纯逻辑，服务与宿主接口给空壳。
    internal interface ISourcePlaybackHost { void CancelSourceDownload(); }
    internal sealed class WebSourcesService
    {
        public WebSourcesService(object host) { }
        internal Task<object> HandleAsync(System.Text.Json.JsonElement msg) => Task.FromResult<object>(new { });
    }
    // TrackReference 只为让 MainWindow.WebSources.cs 编过；FromNetwork 的真身在 WebLibraryService（拖旧界面模型，不进桩台）。
    internal static class WebLibraryService
    {
        internal static PlaylistItem? FromNetwork(NetworkTrack track, NetworkSource? source = null) => null;
    }
}
