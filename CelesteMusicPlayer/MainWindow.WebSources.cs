using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _sourcePlaybackCancellation;
    private IDisposable? _sourcePlaybackLease;
    private IDisposable? _sourcePreloadLease;
    // 「.ref → 曲目 id → 曲库快照」的解析在 TrackReference（静态、无实例状态）；
    // 这里只留转发，播放域和网页命令都还认这两个名字。
    internal static PlaylistItem? ResolveSourceTrack(string id) => TrackReference.Resolve(id);
    private static PlaylistItem? SourceFromReference(string path) => TrackReference.FromReference(path);
    private void PrepareSourcePlayback(PlaylistItem item)
    {
        _sourcePlaybackCancellation?.Cancel();
        if (item.SourceId.Length == 0) { _sourcePlaybackLease?.Dispose(); _sourcePlaybackLease = null; return; }
        var previous = _sourcePlaybackLease;
        _sourcePlaybackLease = NetworkLibraryService.Lease(item.FilePath);
        if (previous != null) _ = Task.Delay(5000).ContinueWith(_ => previous.Dispose());
    }
    private async Task PlaySourceAsync(PlaylistItem item)
    {
        _sourcePlaybackCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _sourcePlaybackCancellation = cancellation;
        try
        {
            var track = NetworkLibraryStore.Track(item.SourceTrackId) ?? throw new InvalidOperationException("曲目索引已不存在。");
            NetworkLibraryStore.Pin(track.Id);
            _webShellWindow?.NotifySourceStatus("正在下载原始音频：" + item.Title);
            string file = await NetworkLibraryService.ResolveAsync(track, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_sourcePlaybackCancellation, cancellation)) return;
            item.FilePath = file;
            if (item.Duration.TotalSeconds <= 0 || item.Title == Path.GetFileNameWithoutExtension(track.Entry.Name))
            {
                var tagged = await Task.Run(() => CreatePlaylistItemFromPath(file));
                if (!track.Cue) { item.Duration = tagged.Duration; item.Title = tagged.Title; item.Artist = tagged.Artist; item.Album = tagged.Album; item.AlbumArtist = tagged.AlbumArtist; }
                else if (item.Duration.TotalSeconds <= 0) item.Duration = TimeSpan.FromSeconds(Math.Max(0, tagged.Duration.TotalSeconds - item.StartTimeSeconds));
            }
            cancellation.Token.ThrowIfCancellationRequested();
            _sourcePlaybackCancellation = null;
            StartPlayback(item);
            _webShellWindow?.NotifySourceStatus("已取得原始音频：" + item.Title);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _webShellWindow?.NotifySourceStatus("播放失败：" + NetworkLibraryService.Friendly(ex)); }
        finally { if (ReferenceEquals(_sourcePlaybackCancellation, cancellation)) _sourcePlaybackCancellation = null; }
    }
    internal async Task<object> WebSourcesAsync(JsonElement msg)
    {
        NetworkLibraryStore.MigrateWebDav();
        string operation = ReadString(msg, "operation"), id = ReadString(msg, "sourceId");
        try
        {
            switch (operation)
            {
                case "list": return SourceSnapshot();
                case "save":
                {
                    lock (NetworkLibraryService.MutationGate)
                    {
                        var source = id.Length == 0 ? new NetworkSource() : NetworkLibraryStore.Source(id);
                        NetworkLibraryService.Cancel(source.Id);
                        if (id.Length > 0 && msg.TryGetProperty("revision", out var revision) && revision.GetInt32() != source.Revision) throw new InvalidOperationException("来源已被其他窗口修改，请重新打开编辑。");
                        string provider = ReadString(msg, "provider");
                        if (!NetworkSourceProvider.Providers.Contains(provider)) throw new ArgumentException("请选择来源类型。");
                        if (id.Length > 0 && source.Provider != provider) throw new ArgumentException("已有来源不能变更类型，请新建连接。");
                        source.Provider = provider; source.Name = ReadString(msg, "name").Trim(); source.Endpoint = ReadString(msg, "endpoint").Trim();
                        if (source.Provider == "baidu" && source.Endpoint.Length == 0) source.Endpoint = "https://pan.baidu.com";
                        if (source.Name.Length is < 1 or > 100 || source.Endpoint.Length == 0) throw new ArgumentException("请填写来源名称和地址。");
                        if (provider is not ("smb" or "sshfs"))
                        {
                            if (!Uri.TryCreate(source.Endpoint, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
                                throw new ArgumentException("请输入 http(s) 服务器地址，账号与密码请填在独立字段。");
                        }
                        else if (!Path.IsPathFullyQualified(source.Endpoint)) throw new ArgumentException("请使用完整 UNC、映射盘或 SSHFS 挂载路径。");
                        source.Root = ReadString(msg, "root"); source.Username = ReadString(msg, "username"); source.UserId = ReadString(msg, "userId");
                        source.Auth = ReadString(msg, "auth") == "token" ? "token" : "password"; source.ClientId = ReadString(msg, "clientId");
                        if (msg.TryGetProperty("enabled", out var enabled)) source.Enabled = enabled.GetBoolean();
                        foreach (var pair in new[] { ("secret", 0), ("refreshToken", 1), ("clientSecret", 2) })
                        {
                            string value = ReadString(msg, pair.Item1);
                            if (value.Length == 0) continue;
                            string protectedValue = NetworkSecrets.Protect(value);
                            if (pair.Item2 == 0) source.ProtectedSecret = protectedValue;
                            else if (pair.Item2 == 1) source.ProtectedRefreshToken = protectedValue;
                            else source.ProtectedClientSecret = protectedValue;
                        }
                        source.Revision++; NetworkLibraryStore.SaveSource(source); NetworkLibraryService.NotifyChanged();
                        return SourceSnapshot(source.Id);
                    }
                }
                case "toggle":
                    lock (NetworkLibraryService.MutationGate)
                    {
                        var source = NetworkLibraryStore.Source(id); NetworkLibraryService.Cancel(id); source.Enabled = !source.Enabled; source.Revision++;
                        NetworkLibraryStore.SaveSource(source); NetworkLibraryService.NotifyChanged(); return SourceSnapshot(id);
                    }
                case "sync": NetworkLibraryService.StartSync(id); return SourceSnapshot(id);
                case "cancel": NetworkLibraryService.Cancel(id); return SourceSnapshot(id);
                case "cancelDownload": _sourcePlaybackCancellation?.Cancel(); return new { cancelled = true };
                case "clearCache": return new { count = NetworkLibraryService.ClearCache() };
                case "test":
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                    var source = NetworkLibraryStore.Source(id);
                    var entries = await NetworkSourceProvider.For(source).BrowseAsync(source, "", timeout.Token);
                    return new { ok = true, message = "连接成功，根目录 " + entries.Length + " 项。" };
                }
                case "browse":
                {
                    var source = NetworkLibraryStore.Source(id); string path = ReadString(msg, "path");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var provider = NetworkSourceProvider.For(source); var entries = await provider.BrowseAsync(source, path, timeout.Token);
                    var tracks = await NetworkLibraryService.BuildTracksAsync(source, entries.ToList(), provider, timeout.Token);
                    if (NetworkLibraryStore.Source(id).Revision != source.Revision) throw new InvalidOperationException("来源已修改，请重新浏览。");
                    NetworkLibraryStore.Put(tracks);
                    return new { sourceId = id, path, folders = entries.Where(e => e.Folder).Select(e => new { path = e.Key, name = e.Name }).ToArray(),
                        tracks = tracks.Select(t => { var item = WebLibraryService.FromNetwork(t, source); return new { id = t.Id, title = item.Title, artist = item.Artist, album = item.Album,
                            duration = item.Duration.TotalSeconds, cue = t.Cue, cached = File.Exists(item.FilePath), coverUrl = "https://celeste-cover.internal/cover/" + t.Id }; }).ToArray() };
                }
                case "pin": NetworkLibraryStore.Pin(ReadString(msg, "trackId")); NetworkLibraryService.NotifyChanged(); return SourceSnapshot(id);
                case "authorize":
                    var authSource = NetworkLibraryStore.Source(id);
                    if (authSource.Provider != "baidu") throw new ArgumentException("仅百度来源使用此授权入口。");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BaiduSourceProvider.AuthorizeUrl(authSource)) { UseShellExecute = true });
                    return new { opened = true };
                case "exchange":
                {
                    var source = NetworkLibraryStore.Source(id);
                    if (source.Provider != "baidu") throw new ArgumentException("仅百度来源使用授权码。");
                    await BaiduSourceProvider.ExchangeAsync(source, ReadString(msg, "code"), CancellationToken.None); return SourceSnapshot(id);
                }
                default: throw new ArgumentException("未知的网络来源操作。");
            }
        }
        catch (Exception ex) { throw new InvalidOperationException(NetworkLibraryService.Friendly(ex)); }
    }
    private static object SourceSnapshot(string selectedId = "")
    {
        var tracks = NetworkLibraryStore.Tracks();
        return new { selectedId, providers = NetworkSourceProvider.Providers, sources = NetworkLibraryStore.Sources().Select(s => new
        {
            id = s.Id, provider = s.Provider, name = s.Name, endpoint = s.Endpoint, root = s.Root, username = s.Username, auth = s.Auth, userId = s.UserId,
            clientId = s.ClientId, enabled = s.Enabled, hasSecret = s.ProtectedSecret.Length > 0, hasRefresh = s.ProtectedRefreshToken.Length > 0,
            hasClientSecret = s.ProtectedClientSecret.Length > 0, count = tracks.Count(t => t.SourceId == s.Id && (t.Indexed || t.Pinned) && !t.Missing),
            lastSync = s.LastSync, error = s.LastError, revision = s.Revision, job = new { running = NetworkLibraryService.Job(s.Id).Running, phase = NetworkLibraryService.Job(s.Id).Phase, discovered = NetworkLibraryService.Job(s.Id).Discovered, error = NetworkLibraryService.Job(s.Id).Error }
        }).ToArray() };
    }
}
