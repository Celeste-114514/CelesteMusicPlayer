using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer;

public sealed partial class MainWindow : ISourcePlaybackHost
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

    /// <summary>在线来源的配置/浏览/同步在 <see cref="WebSourcesService"/>；这里只借出「取消下载」。</summary>
    private WebSourcesService? _webSources;

    private WebSourcesService SourcesService =>
        LazyInitializer.EnsureInitialized(ref _webSources, () => new WebSourcesService(this));

    internal Task<object> WebSourcesAsync(JsonElement msg) => SourcesService.HandleAsync(msg);

    void ISourcePlaybackHost.CancelSourceDownload() => _sourcePlaybackCancellation?.Cancel();
}
