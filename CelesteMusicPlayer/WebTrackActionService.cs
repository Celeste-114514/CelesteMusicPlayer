using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace CelesteMusicPlayer;

/// <summary>
/// 网页曲目动作（<c>trackAction</c>）与播放列表动作（<c>browse</c>）需要宿主提供的能力。
///
/// W13 的规矩：这里**只放服务自己造不出来的东西**——真实播放、删盘、外部程序、
/// 页面快照回包。曲库快照用属性取（同 <see cref="WebLibraryService"/> 的口径），
/// 队列整个交出去（<see cref="PlaybackQueue"/> 本来就是独立类型）。
/// 千万别因为「顺手」把宿主的现成属性加进来，否则服务会慢慢长回 MainWindow。
/// </summary>
internal interface IWebTrackActionHost
{
    /// <summary>播放队列（服务自己改内容，界面刷新交给 <see cref="RefreshQueueUi"/>）。</summary>
    PlaybackQueue Queue { get; }

    /// <summary>媒体库全部歌曲快照。</summary>
    PlaylistItem[] AllLibraryTracks { get; }

    /// <summary>媒体库按路径索引的曲目（删除/移动后仍然指向磁盘上那首歌）。</summary>
    IReadOnlyDictionary<string, PlaylistItem> LibraryFolderTracks { get; }

    /// <summary>按队列下标开始播放（队列已就位时走它，不重建队列）。</summary>
    void PlayQueueIndex(int index);

    /// <summary>播单曲：宿主自己判定走「曲库曲目」还是「临时曲目」通道。</summary>
    void PlaySong(PlaylistItem song);

    /// <summary>队列内容被就地改过（插入/移除/置顶）之后：重编号 + 通知网页主页 + 用户播放列表过滤。</summary>
    void RefreshQueueUi();

    /// <summary>喜欢的图标/任务栏缩略图按钮跟着当前曲目刷新。</summary>
    void RefreshFavoriteChrome();

    /// <summary>打开标签编辑器（网页标签页）。</summary>
    void ShowTagEditor(IEnumerable<string> persistenceKeys);

    /// <summary>在资源管理器里定位文件。</summary>
    void RevealInExplorer(string filePath);

    /// <summary>把主窗口提到前台（打开标签页之后）。</summary>
    void ActivateWindow();

    /// <summary>从磁盘删除一首（带确认对话框，XamlRoot 是宿主内部细节）。</summary>
    Task DeleteFromDiskAsync(PlaylistItem song);

    /// <summary>曲库页快照回包（还在 MainWindow 里）。</summary>
    object BrowseSnapshot(JsonElement msg);

    /// <summary>把命名播放列表装进队列并开始播放。</summary>
    Task LoadNamedPlaylistAndPlayAsync(string name);

    /// <summary>把命名播放列表追加到队列。</summary>
    void AddNamedPlaylistToQueue(string name);

    /// <summary>播放 .cue 镜像。</summary>
    Task PlayCueFileAsync(string path);
}

/// <summary>
/// 网页曲目动作 + 播放列表动作。
///
/// W13：从 <c>MainWindow.WebBrowseActions.cs</c> 搬出。这里零界面代码：
/// 播放、删盘、资源管理器、剪贴板之外的动作全在宿主；剪贴板是 WinRT，不需要窗口。
///
/// 两条命令共用 <see cref="WebOperationGate"/>（和目录浏览同一把锁）：
/// 挑选对话框这种「挂住等用户」的操作必须互斥，否则会叠出两个对话框。
/// </summary>
internal sealed class WebTrackActionService(IWebTrackActionHost host, WebOperationGate busy)
{
    /// <summary>播放列表封面/导入导出/复制：零播放零队列，本来就在独立服务里。</summary>
    private readonly WebPlaylistMediaService _media = new();

    private static string PlaylistName(JsonElement msg)
    {
        string name = msg.GetProperty("name").GetString() ?? "";
        if (!NamedPlaylistStore.List().Contains(name)) throw new InvalidOperationException("播放列表已不存在，请刷新。");
        return name;
    }

    /// <summary>播放列表动作：封面、复制、导入导出、整表播放/入队、打开 .cue。</summary>
    internal async Task<object> BrowseAsync(JsonElement msg, object window)
    {
        string operation = msg.GetProperty("operation").GetString() ?? "get";
        // 这几个是纯查询，不占互斥区（和旧实现一致）。
        if (operation is "get" or "createPlaylist" or "favorite" or "rating") return host.BrowseSnapshot(msg);

        using IDisposable lease = busy.TryEnter() ?? throw new InvalidOperationException("请先完成当前操作。");
        switch (operation)
        {
            case "setCover": await _media.SetCoverAsync(msg, window).ConfigureAwait(true); break;
            case "resetCover": _media.ResetCover(msg); break;
            case "searchCover": await _media.SearchCoverAsync(msg).ConfigureAwait(true); break;
            case "playPlaylist": await host.LoadNamedPlaylistAndPlayAsync(PlaylistName(msg)).ConfigureAwait(true); break;
            case "queuePlaylist": host.AddNamedPlaylistToQueue(PlaylistName(msg)); break;
            case "copyPlaylist": await _media.CopyAsync(msg, window).ConfigureAwait(true); break;
            case "exportPlaylist": await _media.ExportAsync(msg, window).ConfigureAwait(true); break;
            case "importM3u": await _media.ImportAsync(msg, window).ConfigureAwait(true); break;
            case "openCue":
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".cue");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, NativeMainWebWindow.HandleOf(window));
                var file = await picker.PickSingleFileAsync();
                if (file != null) await host.PlayCueFileAsync(file.Path).ConfigureAwait(true);
                break;
            }
            default: throw new ArgumentException("未知的曲库操作。");
        }
        PlaylistLibraryService.Refresh();
        return host.BrowseSnapshot(JsonSerializer.SerializeToElement(new { operation = "get" }));
    }

    /// <summary>曲目动作：右键菜单能点的都在这里。选的永远是点中的那几行，不是正在播的那首。</summary>
    internal async Task<object> TrackActionAsync(JsonElement msg, object window)
    {
        string operation = msg.GetProperty("operation").GetString() ?? "get";
        bool fromQueue = msg.TryGetProperty("queue", out var q) && q.GetBoolean();
        PlaylistItem[] songs;
        int[] indices = Array.Empty<int>();
        if (fromQueue)
        {
            var (items, revision) = host.Queue.WebSnapshot();
            var refs = msg.GetProperty("entries").EnumerateArray()
                .Select(entry => (entry.GetProperty("index").GetInt32(), entry.GetProperty("trackId").GetString() ?? ""))
                .ToArray();
            indices = WebBrowseRules.ResolveQueueIndices(items, revision, msg.GetProperty("revision").GetInt32(), refs, WebTrackIdentity.Of);
            songs = indices.Select(i => items[i]).ToArray();
        }
        else
        {
            var ids = msg.GetProperty("trackIds").EnumerateArray().Select(id => id.GetString()).ToArray();
            var map = host.AllLibraryTracks.GroupBy(WebTrackIdentity.Of).ToDictionary(group => group.Key, group => group.First());
            foreach (var entry in host.LibraryFolderTracks) if (!map.ContainsKey(entry.Key) && File.Exists(entry.Value.FilePath)) map[entry.Key] = entry.Value;
            songs = WebBrowseRules.ResolveById(ids, map).ToArray();
        }
        if (songs.Length is < 1 or > 50000) throw new ArgumentException("请选择歌曲。");
        switch (operation)
        {
            case "get": return new { favorite = songs.All(song => TrackStatsStore.Get(song.PersistenceKey)?.IsFavorite == true),
                canDelete = !AppSettingsStore.Load().DisableDeleteFromDisk && songs.All(song => !song.IsRemote), count = songs.Length };
            case "play": if (fromQueue) host.PlayQueueIndex(indices[0]); else host.PlaySong(songs[0]); break;
            case "next": InsertQueue(songs, next: true); break;
            case "addQueue": InsertQueue(songs, next: false); break;
            case "addPlaylist":
            {
                string? target = await _media.PickTargetPlaylistAsync(window).ConfigureAwait(true);
                if (target != null) NamedPlaylistStore.SaveSongs(target, NamedPlaylistStore.LoadSongs(target).Concat(songs.Select(song => song.PersistenceKey)).Distinct(StringComparer.OrdinalIgnoreCase));
                PlaylistLibraryService.Refresh(); break;
            }
            case "pin":
            case "removeQueue":
            {
                if (!fromQueue) throw new ArgumentException("请在播放队列中操作。");
                PlaybackQueue queue = host.Queue;
                var playing = queue.Index >= 0 && queue.Index < queue.Items.Count ? queue.Items[queue.Index] : null;
                // 原地改列表（视图事件照常发）；置顶用的 songs 是移除前解析出的对象，被移除再插回队首 = 置顶语义。
                queue.Index = WebBrowseRules.RemoveAndPin(queue.Items, indices,
                    operation == "pin" ? songs : Array.Empty<PlaylistItem>(), playing);
                host.RefreshQueueUi();
                break;
            }
            case "favorite":
                foreach (var song in songs) TrackStatsStore.SetFavorite(song.PersistenceKey, msg.GetProperty("value").GetBoolean());
                NamedPlaylistStore.SyncFavoritesPlaylist(); host.RefreshFavoriteChrome(); break;
            case "rating":
                int rating = msg.GetProperty("value").GetInt32();
                if (rating is < 0 or > 5) throw new ArgumentException("评分必须为 0–5。");
                foreach (var song in songs) TrackStatsStore.SetRating(song.PersistenceKey, rating);
                break;
            case "tags": host.ShowTagEditor(songs.Select(song => song.PersistenceKey)); host.ActivateWindow(); break;
            case "reveal": if (songs[0].IsRemote) throw new InvalidOperationException("远程歌曲没有本地文件夹。"); host.RevealInExplorer(songs[0].FilePath); break;
            case "copy": var data = new DataPackage(); data.SetText(string.Join(Environment.NewLine, songs.Select(song => song.Title + " - " + song.Artist))); Clipboard.SetContent(data); break;
            case "delete":
                if (AppSettingsStore.Load().DisableDeleteFromDisk || songs.Any(song => song.IsRemote)) throw new InvalidOperationException("当前歌曲不允许从磁盘删除。");
                foreach (var song in songs.DistinctBy(song => song.FilePath, StringComparer.OrdinalIgnoreCase)) await host.DeleteFromDiskAsync(song).ConfigureAwait(true);
                break;
            default: throw new ArgumentException("未知的歌曲操作。");
        }
        return new { message = "操作已完成" };
    }

    /// <summary>把歌插进队列（「下一首播放」/「添加到队列」）。目录浏览的 queue 动作也走这里。</summary>
    internal void InsertQueue(PlaylistItem[] songs, bool next)
    {
        foreach (var song in songs.Where(s => s.SourceTrackId.Length > 0)) NetworkLibraryStore.Pin(song.SourceTrackId);
        PlaybackQueue queue = host.Queue;
        // CUE tracks share a file. Moving one must not remove its sibling tracks.
        var playing = queue.Index >= 0 && queue.Index < queue.Items.Count ? queue.Items[queue.Index] : null;
        var kept = WebBrowseRules.Insert(queue.Items.ToArray(), songs, playing, WebTrackIdentity.Of, song => song.Clone(), next, AppSettingsStore.Load().InsertPlaylistAtBegin);
        queue.Replace(kept);
        queue.Index = playing == null ? -1 : queue.Items.IndexOf(playing);
        if (queue.Index < 0 && playing != null)
            queue.Index = kept.FindIndex(song => WebTrackIdentity.Of(song) == WebTrackIdentity.Of(playing));
        host.RefreshQueueUi();
    }
}
