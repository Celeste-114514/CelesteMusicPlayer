using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private bool _webBrowseBusy;

        /// <summary>播放列表的封面/导入导出/复制：零播放零队列，已经在 <see cref="WebPlaylistMediaService"/> 里。</summary>
        private WebPlaylistMediaService PlaylistMedia { get; } = new();

        private static string WebPlaylistName(JsonElement msg)
        {
            string name = msg.GetProperty("name").GetString() ?? "";
            if (!NamedPlaylistStore.List().Contains(name)) throw new InvalidOperationException("播放列表已不存在，请刷新。");
            return name;
        }

        internal async Task<object> WebBrowseAsync(JsonElement msg, object host)
        {
            string operation = msg.GetProperty("operation").GetString() ?? "get";
            if (operation is "get" or "createPlaylist" or "favorite" or "rating") return WebBrowse(msg);
            if (_webBrowseBusy) throw new InvalidOperationException("请先完成当前操作。");
            _webBrowseBusy = true;
            try
            {
                switch (operation)
                {
                    case "setCover": await PlaylistMedia.SetCoverAsync(msg, host).ConfigureAwait(true); break;
                    case "resetCover": PlaylistMedia.ResetCover(msg); break;
                    case "searchCover": await PlaylistMedia.SearchCoverAsync(msg).ConfigureAwait(true); break;
                    case "playPlaylist": await LoadNamedPlaylistToQueueAndPlayAsync(WebPlaylistName(msg)).ConfigureAwait(true); break;
                    case "queuePlaylist": AddNamedPlaylistToQueue(WebPlaylistName(msg)); break;
                    case "copyPlaylist": await PlaylistMedia.CopyAsync(msg, host).ConfigureAwait(true); break;
                    case "exportPlaylist": await PlaylistMedia.ExportAsync(msg, host).ConfigureAwait(true); break;
                    case "importM3u": await PlaylistMedia.ImportAsync(msg, host).ConfigureAwait(true); break;
                    case "openCue":
                    {
                        var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".cue");
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, NativeMainWebWindow.HandleOf(host));
                        var file = await picker.PickSingleFileAsync();
                        if (file != null) await PlayCueFileAsync(file.Path).ConfigureAwait(true);
                        break;
                    }
                    default: throw new ArgumentException("未知的曲库操作。");
                }
                PlaylistLibraryService.Refresh();
                return WebBrowse(JsonSerializer.SerializeToElement(new { operation = "get" }));
            }
            finally { _webBrowseBusy = false; }
        }

        // Context operations resolve the clicked row, never the currently playing track.
        internal async Task<object> WebTrackActionAsync(JsonElement msg, object host)
        {
            string operation = msg.GetProperty("operation").GetString() ?? "get";
            bool fromQueue = msg.TryGetProperty("queue", out var q) && q.GetBoolean();
            PlaylistItem[] songs;
            int[] indices = Array.Empty<int>();
            if (fromQueue)
            {
                WebQueue(JsonSerializer.SerializeToElement(new { operation = "get" }));
                var refs = msg.GetProperty("entries").EnumerateArray()
                    .Select(entry => (entry.GetProperty("index").GetInt32(), entry.GetProperty("trackId").GetString() ?? ""))
                    .ToArray();
                indices = WebBrowseRules.ResolveQueueIndices(_queue.Items, _queue.Revision, msg.GetProperty("revision").GetInt32(), refs, WebTrackIdentity.Of);
                songs = indices.Select(i => _queue.Items[i]).ToArray();
            }
            else
            {
                var ids = msg.GetProperty("trackIds").EnumerateArray().Select(id => id.GetString()).ToArray();
                var map = Library.AllTracks().GroupBy(WebTrackIdentity.Of).ToDictionary(group => group.Key, group => group.First());
                foreach (var entry in Library.FolderTracks) if (!map.ContainsKey(entry.Key) && File.Exists(entry.Value.FilePath)) map[entry.Key] = entry.Value;
                songs = WebBrowseRules.ResolveById(ids, map).ToArray();
            }
            if (songs.Length is < 1 or > 50000) throw new ArgumentException("请选择歌曲。");
            switch (operation)
            {
                case "get": return new { favorite = songs.All(song => TrackStatsStore.Get(song.PersistenceKey)?.IsFavorite == true),
                    canDelete = !AppSettingsStore.Load().DisableDeleteFromDisk && songs.All(song => !song.IsRemote), count = songs.Length };
                case "play": if (fromQueue) PlayUserPlaylistAt(indices[0]); else if (LibraryTracks.Contains(songs[0])) WebPlayTrack(songs[0]); else PlayPlaylistItem(songs[0]); break;
                case "next": WebInsertQueue(songs, next: true); break;
                case "addQueue": WebInsertQueue(songs, next: false); break;
                case "addPlaylist":
                {
                    string? target = await PlaylistMedia.PickTargetPlaylistAsync(host).ConfigureAwait(true);
                    if (target != null) NamedPlaylistStore.SaveSongs(target, NamedPlaylistStore.LoadSongs(target).Concat(songs.Select(song => song.PersistenceKey)).Distinct(StringComparer.OrdinalIgnoreCase));
                    PlaylistLibraryService.Refresh(); break;
                }
                case "pin":
                case "removeQueue":
                {
                    if (!fromQueue) throw new ArgumentException("请在播放队列中操作。");
                    var playing = _queue.Index >= 0 && _queue.Index < _queue.Items.Count ? _queue.Items[_queue.Index] : null;
                    // 原地改列表（视图事件照常发）；置顶用的 songs 是移除前解析出的对象，被移除再插回队首 = 置顶语义。
                    _queue.Index = WebBrowseRules.RemoveAndPin(_queue.Items, indices,
                        operation == "pin" ? songs : Array.Empty<PlaylistItem>(), playing);
                    RenumberCollection(_queue.Items); NotifyCurrentPlaylistWindow();
                    if (_currentCategory == "UserPlaylist") ApplyUserPlaylistSearchFilter();
                    break;
                }
                case "favorite": foreach (var song in songs) TrackStatsStore.SetFavorite(song.PersistenceKey, msg.GetProperty("value").GetBoolean()); NamedPlaylistStore.SyncFavoritesPlaylist(); UpdateFavoriteButtonUi(); break;
                case "rating":
                    int rating = msg.GetProperty("value").GetInt32();
                    if (rating is < 0 or > 5) throw new ArgumentException("评分必须为 0–5。");
                    foreach (var song in songs) TrackStatsStore.SetRating(song.PersistenceKey, rating);
                    break;
                case "tags": OpenTagEditForFiles(songs.Select(song => song.PersistenceKey)); Activate(); break;
                case "reveal": if (songs[0].IsRemote) throw new InvalidOperationException("远程歌曲没有本地文件夹。"); OpenFileLocationInExplorer(songs[0].FilePath); break;
                case "copy": var data = new DataPackage(); data.SetText(string.Join(Environment.NewLine, songs.Select(song => song.Title + " - " + song.Artist))); Clipboard.SetContent(data); break;
                case "delete":
                    if (AppSettingsStore.Load().DisableDeleteFromDisk || songs.Any(song => song.IsRemote)) throw new InvalidOperationException("当前歌曲不允许从磁盘删除。");
                    foreach (var song in songs.DistinctBy(song => song.FilePath, StringComparer.OrdinalIgnoreCase)) await DeleteSongFromDiskAsync(song, Content.XamlRoot).ConfigureAwait(true);
                    break;
                default: throw new ArgumentException("未知的歌曲操作。");
            }
            return new { message = "操作已完成" };
        }

        private void WebInsertQueue(PlaylistItem[] songs, bool next)
        {
            foreach (var song in songs.Where(s => s.SourceTrackId.Length > 0)) NetworkLibraryStore.Pin(song.SourceTrackId);
            // CUE tracks share a file. Moving one must not remove its sibling tracks.
            var playing = _queue.Index >= 0 && _queue.Index < _queue.Items.Count ? _queue.Items[_queue.Index] : null;
            var kept = WebBrowseRules.Insert(_queue.Items.ToArray(), songs, playing, WebTrackIdentity.Of, ClonePlaylistItem, next, AppSettingsStore.Load().InsertPlaylistAtBegin);
            bool rebind = ReferenceEquals(PlaylistView.ItemsSource, _queue.Items) || _currentCategory == "UserPlaylist";
            if (rebind) PlaylistView.ItemsSource = null;
            _queue.Replace(kept);
            if (rebind) PlaylistView.ItemsSource = _queue.Items;
            _queue.Index = playing == null ? -1 : _queue.Items.IndexOf(playing);
            if (_queue.Index < 0 && playing != null)
                _queue.Index = kept.FindIndex(song => WebTrackIdentity.Of(song) == WebTrackIdentity.Of(playing));
            RenumberCollection(_queue.Items); NotifyCurrentPlaylistWindow();
            if (_currentCategory == "UserPlaylist") ApplyUserPlaylistSearchFilter();
        }
    }
}
