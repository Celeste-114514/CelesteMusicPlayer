using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer;

internal sealed class NetworkSyncJob
{
    public string SourceId { get; set; } = "";
    public string Phase { get; set; } = "idle";
    public int Discovered { get; set; }
    public string Error { get; set; } = "";
    public bool Running { get; set; }
    internal CancellationTokenSource Cancellation { get; } = new();
}

internal static class NetworkLibraryService
{
    internal static readonly object MutationGate = new();
    private static readonly ConcurrentDictionary<string, NetworkSyncJob> Jobs = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Downloads = new();
    private static readonly ConcurrentDictionary<string, int> Leases = new(StringComparer.OrdinalIgnoreCase);
    internal static event Action? Changed;
    internal static void NotifyChanged() => Changed?.Invoke();
    internal static string CacheRoot => Path.Combine(AppSettingsStore.GetConfigDirectory(), "NetworkCache");
    internal static NetworkSyncJob Job(string sourceId) => Jobs.TryGetValue(sourceId, out var job) ? job : new() { SourceId = sourceId };
    internal static void Cancel(string id) { if (Jobs.TryGetValue(id, out var job)) job.Cancellation.Cancel(); }
    internal static void StartSync(string id)
    {
        lock (MutationGate)
        {
            if (Job(id).Running) throw new InvalidOperationException("该来源已经在同步。");
            var source = NetworkLibraryStore.Source(id);
            if (!source.Enabled) throw new InvalidOperationException("该来源已停用。");
            var job = new NetworkSyncJob { SourceId = id, Running = true, Phase = "scanning" }; Jobs[id] = job;
            _ = Task.Run(async () =>
            {
                try
                {
                    var provider = NetworkSourceProvider.For(source);
                    var entries = new List<NetworkEntry>();
                    await foreach (var entry in provider.ScanAsync(source, job.Cancellation.Token))
                    {
                        entries.Add(entry); job.Discovered = entries.Count;
                    }
                    job.Phase = "cue";
                    var tracks = await BuildTracksAsync(source, entries, provider, job.Cancellation.Token);
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    lock (MutationGate)
                    {
                        var current = NetworkLibraryStore.Source(id);
                        if (current.Revision != source.Revision || !current.Enabled) throw new OperationCanceledException();
                        var old = NetworkLibraryStore.Tracks(id).ToDictionary(t => t.Id, StringComparer.Ordinal);
                        var seen = tracks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                        foreach (var track in tracks) { track.Indexed = true; if (old.TryGetValue(track.Id, out var prior)) track.Pinned = prior.Pinned; }
                        // Only a fully completed scan can mark unseen records missing. Cancel/failure never reaches here.
                        foreach (var missing in old.Values.Where(t => t.Indexed && !seen.Contains(t.Id))) { missing.Missing = true; tracks.Add(missing); }
                        NetworkLibraryStore.Put(tracks);
                        current.LastSync = DateTimeOffset.UtcNow; current.LastError = ""; NetworkLibraryStore.SaveSource(current);
                    }
                    job.Phase = "completed"; NotifyChanged();
                }
                catch (OperationCanceledException) { job.Phase = "cancelled"; }
                catch (Exception ex)
                {
                    job.Phase = "failed"; job.Error = Friendly(ex);
                    lock (MutationGate)
                    {
                        var current = NetworkLibraryStore.Source(id);
                        if (current.Revision == source.Revision) { current.LastError = job.Error; NetworkLibraryStore.SaveSource(current); }
                    }
                }
                finally { job.Running = false; }
            });
        }
    }
    internal static string Friendly(Exception error) => error switch
    {
        System.Net.Http.HttpRequestException => "网络连接失败，请检查地址、网络和代理。",
        OperationCanceledException => "操作已取消或连接超时。",
        UnauthorizedAccessException => "文件或挂载目录无访问权限。",
        _ => error.Message
    };
    internal static NetworkTrack MakeTrack(NetworkSource source, NetworkEntry entry)
    {
        string id = NetworkIdentity.TrackId(source.Id, entry.Key);
        var old = NetworkLibraryStore.Track(id);
        return new NetworkTrack { Id = id, SourceId = source.Id, Entry = entry, Indexed = old?.Indexed ?? false, Pinned = old?.Pinned ?? false };
    }
    internal static async Task<NetworkTrack[]> BrowseAsync(string sourceId, string path, CancellationToken ct)
    {
        var source = NetworkLibraryStore.Source(sourceId);
        if (!source.Enabled) throw new InvalidOperationException("该来源已停用。");
        var provider = NetworkSourceProvider.For(source);
        var entries = await provider.BrowseAsync(source, path, ct);
        var tracks = await BuildTracksAsync(source, entries, provider, ct);
        lock (MutationGate)
        {
            if (NetworkLibraryStore.Source(sourceId).Revision != source.Revision) throw new InvalidOperationException("来源已修改，请重新浏览。");
            NetworkLibraryStore.Put(tracks);
        }
        return tracks.ToArray();
    }
    internal static async Task<List<NetworkTrack>> BuildTracksAsync(NetworkSource source, List<NetworkEntry> entries, NetworkSourceProvider provider, CancellationToken ct)
        => await BuildTracksAsync(source, entries.ToArray(), provider, ct);
    private static async Task<List<NetworkTrack>> BuildTracksAsync(NetworkSource source, NetworkEntry[] entries, NetworkSourceProvider provider, CancellationToken ct)
    {
        var audio = entries.Where(e => !e.Folder && NetworkIdentity.Audio.Contains(NetworkIdentity.Extension(e))).Where(e => NetworkIdentity.Audio.Contains(Path.GetExtension(e.Name)) || e.Title.Length > 0).ToArray();
        var result = audio.Select(e => MakeTrack(source, e)).ToList();
        if (!provider.Sidecars || !AppSettingsStore.Load().AutoApplyCueOnScan) return result;
        foreach (var cue in entries.Where(e => !e.Folder && Path.GetExtension(e.Name).Equals(".cue", StringComparison.OrdinalIgnoreCase)))
        {
            if (cue.Size > 2_000_000) throw new InvalidOperationException("CUE 文件超过 2 MB，请先检查配套文件。");
            string temporary = Path.Combine(Path.GetTempPath(), "celeste-cue-" + Guid.NewGuid().ToString("N"));
            try
            {
                await provider.DownloadAsync(source, cue, temporary, ct);
                if (new FileInfo(temporary).Length > 2_000_000) throw new InvalidOperationException("CUE 文件过大。");
                string text;
                byte[] bytes = await File.ReadAllBytesAsync(temporary, ct);
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); text = Encoding.GetEncoding(936).GetString(bytes); }
                var segments = CueSourceParser.Parse(text);
                foreach (var group in segments.GroupBy(s => s.File, StringComparer.Ordinal))
                {
                    string parent = cue.Path.Contains('/') ? cue.Path.Substring(0, cue.Path.LastIndexOf('/')) : "/";
                    string target = NetworkIdentity.Join(parent, group.Key);
                    var asset = audio.FirstOrDefault(e => e.Path == target);
                    if (asset == null) continue;
                    var ordered = group.OrderBy(s => s.Frame).ToArray();
                    result.RemoveAll(t => t.Entry.Key == asset.Key && !t.Cue);
                    for (int i = 0; i < ordered.Length; i++)
                    {
                        var segment = ordered[i];
                        var track = MakeTrack(source, asset);
                        track.Id = NetworkIdentity.TrackId(source.Id, "cue:" + cue.Key + ":" + asset.Key + ":" + segment.Number);
                        var old = NetworkLibraryStore.Track(track.Id);
                        track.Pinned = old?.Pinned ?? false; track.Indexed = old?.Indexed ?? false;
                        track.Cue = true; track.CueKey = cue.Key; track.StartFrame = segment.Frame;
                        track.EndFrame = i + 1 < ordered.Length ? ordered[i + 1].Frame : (long)Math.Round(asset.Duration * 75);
                        track.Title = segment.Title; track.Artist = segment.Artist; track.Track = segment.Number;
                        if (track.EndFrame > 0 && track.EndFrame <= track.StartFrame) throw new InvalidOperationException("CUE 分轨边界无效。");
                        result.Add(track);
                    }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); if (File.Exists(temporary + ".part")) File.Delete(temporary + ".part"); }
        }
        return result;
    }
    internal static string CachedPath(NetworkTrack track) => track.SourceId.Length == 0 ? track.LocalPath
        : Path.Combine(CacheRoot, track.SourceId, NetworkIdentity.Hash(track.Entry.Key) + "-" + NetworkIdentity.Hash(track.Entry.Version + ":" + track.Entry.Size).Substring(0, 12) + NetworkIdentity.Extension(track.Entry));
    internal static async Task<string> ResolveAsync(NetworkTrack track, CancellationToken ct)
    {
        if (track.SourceId.Length == 0) return track.LocalPath;
        string path = CachedPath(track);
        if (File.Exists(path) && new FileInfo(path).Length > 0) { Touch(path); return path; }
        var source = NetworkLibraryStore.Source(track.SourceId);
        if (!source.Enabled) throw new InvalidOperationException("该来源已停用，且曲目未缓存。");
        if (track.Missing) throw new InvalidOperationException("服务器上已找不到该曲目。");
        var gate = Downloads.GetOrAdd(path, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await NetworkSourceProvider.For(source).DownloadAsync(source, track.Entry, temporary, ct);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length <= 0 || track.Entry.Size > 0 && new FileInfo(temporary).Length != track.Entry.Size)
                throw new IOException("下载大小不完整，未写入播放缓存。");
            ct.ThrowIfCancellationRequested();
            if (NetworkLibraryStore.Source(source.Id).Revision != source.Revision) throw new OperationCanceledException();
            File.Move(temporary, path, true); Touch(path); TrimCache(path); return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(temporary + ".part")) File.Delete(temporary + ".part");
            gate.Release();
        }
    }
    internal static IDisposable Lease(string path)
    {
        Leases.AddOrUpdate(path, 1, (_, count) => count + 1); return new CacheLease(path);
    }
    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
    }
    private static void TrimCache(string keep)
    {
        long budget = Math.Max(128, AppSettingsStore.Load().WebDavCacheLimitMb) * 1024L * 1024L;
        var files = Directory.EnumerateFiles(CacheRoot, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)).Select(path => new FileInfo(path)).ToArray();
        long size = files.Sum(file => file.Length);
        foreach (var file in files.OrderBy(file => file.LastAccessTimeUtc))
        {
            if (size <= budget) break;
            if (file.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase) || Leases.TryGetValue(file.FullName, out var leases) && leases > 0) continue;
            try { long bytes = file.Length; file.Delete(); size -= bytes; } catch (IOException) { }
        }
    }
    private sealed class CacheLease(string path) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Leases.AddOrUpdate(path, 0, (_, count) => Math.Max(0, count - 1)); } }
    }
    internal static int ClearCache()
    {
        int count = 0;
        if (!Directory.Exists(CacheRoot)) return count;
        foreach (string file in Directory.EnumerateFiles(CacheRoot, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || Leases.TryGetValue(file, out var leases) && leases > 0) continue;
            try { File.Delete(file); count++; } catch (IOException) { }
        }
        return count;
    }
}

internal static class CueSourceParser
{
    internal sealed record Segment(string File, int Number, long Frame, string Title, string Artist);
    internal static Segment[] Parse(string text)
    {
        string file = "", title = "", artist = "", sheetArtist = ""; int number = 0;
        var result = new List<Segment>();
        foreach (string line in text.TrimStart('\ufeff').Split('\n').Select(s => s.Trim()))
        {
            var match = Regex.Match(line, "^FILE\\s+\"(.+)\"\\s+\\S+", RegexOptions.IgnoreCase);
            if (match.Success) { file = match.Groups[1].Value; number = 0; continue; }
            match = Regex.Match(line, "^TRACK\\s+(\\d+)\\s+AUDIO", RegexOptions.IgnoreCase);
            if (match.Success) { number = int.Parse(match.Groups[1].Value); title = ""; artist = sheetArtist; continue; }
            match = Regex.Match(line, "^(TITLE|PERFORMER)\\s+\"(.*)\"", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                if (match.Groups[1].Value.Equals("TITLE", StringComparison.OrdinalIgnoreCase)) title = match.Groups[2].Value;
                else { artist = match.Groups[2].Value; if (number == 0) sheetArtist = artist; }
                continue;
            }
            match = Regex.Match(line, "^INDEX\\s+01\\s+(\\d+):(\\d{2}):(\\d{2})", RegexOptions.IgnoreCase);
            if (!match.Success || number == 0 || file.Length == 0) continue;
            int minutes = int.Parse(match.Groups[1].Value), seconds = int.Parse(match.Groups[2].Value), frames = int.Parse(match.Groups[3].Value);
            if (seconds >= 60 || frames >= 75) throw new ArgumentException("CUE 时间格式无效。");
            result.Add(new(file, number, ((long)minutes * 60 + seconds) * 75 + frames, title, artist));
        }
        return result.ToArray();
    }
}
