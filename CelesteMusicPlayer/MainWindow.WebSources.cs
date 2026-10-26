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
    internal PlaylistItem[] WebAllTracks()
    {
        var local = LibraryTracks.ToArray();
        // Capture local CUE references so a playlist can restore one segment even outside the current folder.
        var cues = local.Where(t => t.IsCueSegment && t.SourceId.Length == 0 && t.SourceTrackId.Length > 0).Select(t => new NetworkTrack
        {
            Id = t.SourceTrackId, LocalPath = t.FilePath, Cue = true, StartFrame = (long)Math.Round(t.StartTimeSeconds * 75),
            EndFrame = t.Duration.TotalSeconds > 0 ? (long)Math.Round((t.StartTimeSeconds + t.Duration.TotalSeconds) * 75) : 0,
            Title = t.Title, Artist = t.Artist, Track = (int)t.Track,
            Entry = new NetworkEntry { Name = Path.GetFileName(t.FilePath), Title = t.Title, Artist = t.Artist, Album = t.Album, AlbumArtist = t.AlbumArtist, Track = (int)t.Track, Disc = (int)t.Disc }
        }).ToArray();
        if (cues.Length > 0) NetworkLibraryStore.Put(cues.Select(cue => { cue.Pinned = NetworkLibraryStore.Track(cue.Id)?.Pinned ?? false; return cue; }));
        var localIds = local.Select(WebTrackId).ToHashSet(StringComparer.Ordinal);
        var sources = NetworkLibraryStore.Sources().ToDictionary(s => s.Id);
        var remote = NetworkLibraryStore.Tracks().Where(t => !localIds.Contains(t.Id) && (t.Indexed || t.Pinned))
            .Select(t => SourcePlaylistItem(t, sources.GetValueOrDefault(t.SourceId)));
        return local.Concat(remote).ToArray();
    }
    private static PlaylistItem SourcePlaylistItem(NetworkTrack track, NetworkSource? source = null)
    {
        string title = track.Title.Length > 0 ? track.Title : track.Entry.Title.Length > 0 ? track.Entry.Title : Path.GetFileNameWithoutExtension(track.Entry.Name);
        double duration = track.Cue ? Math.Max(0, (track.EndFrame - track.StartFrame) / 75.0) : track.Entry.Duration;
        return new PlaylistItem { SourceId = track.SourceId, SourceTrackId = track.Id, SourceName = source?.Name ?? (track.SourceId.Length == 0 ? "本地" : "已停用来源"),
            FilePath = NetworkLibraryService.CachedPath(track), RemotePath = track.SourceId.Length > 0 ? track.Entry.Path : "", IsCueSegment = track.Cue,
            StartTimeSeconds = track.StartFrame / 75.0, Duration = TimeSpan.FromSeconds(duration), Title = title,
            Artist = track.Artist.Length > 0 ? track.Artist : track.Entry.Artist.Length > 0 ? track.Entry.Artist : "未知艺术家",
            Album = track.Entry.Album.Length > 0 ? track.Entry.Album : source?.Name ?? "未知专辑", AlbumArtist = track.Entry.AlbumArtist.Length > 0 ? track.Entry.AlbumArtist : track.Entry.Artist,
            Track = (uint)Math.Max(0, track.Track > 0 ? track.Track : track.Entry.Track), Disc = (uint)Math.Max(0, track.Entry.Disc),
            RemoteHint = track.Missing ? "文件缺失" : File.Exists(NetworkLibraryService.CachedPath(track)) ? "已缓存" : "未缓存" };
    }
    internal static PlaylistItem? ResolveSourceTrack(string id)
    {
        var record = NetworkLibraryStore.Track(id);
        if (record == null) return null;
        var source = record.SourceId.Length == 0 ? null : NetworkLibraryStore.Sources().FirstOrDefault(s => s.Id == record.SourceId);
        return SourcePlaylistItem(record, source);
    }
    private static string SourceReferenceId(string path) => Path.GetExtension(path).Equals(".ref", StringComparison.OrdinalIgnoreCase)
        && Path.GetFullPath(path).StartsWith(Path.Combine(AppSettingsStore.GetConfigDirectory(), "track-refs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        ? Path.GetFileNameWithoutExtension(path) : "";
    private static PlaylistItem? SourceFromReference(string path)
    {
        string id = SourceReferenceId(path); return id.Length == 0 ? null : ResolveSourceTrack(id);
    }
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
                        tracks = tracks.Select(t => { var item = SourcePlaylistItem(t, source); return new { id = t.Id, title = item.Title, artist = item.Artist, album = item.Album,
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
