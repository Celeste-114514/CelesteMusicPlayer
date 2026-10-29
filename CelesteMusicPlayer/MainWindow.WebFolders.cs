using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer;

public sealed partial class MainWindow
{
    // W13：节点登记簿、曲目 id 与封面路径都在 WebLibraryService 里；这里只管命令与播放。
    internal string? WebFolderCoverPath(string id) => Library.CoverPath(id);

    internal async Task<object> WebFoldersAsync(JsonElement msg, object host)
    {
        string operation = msg.GetProperty("operation").GetString() ?? "roots";
        if (operation == "add")
        {
            if (_webBrowseBusy) throw new InvalidOperationException("请先完成当前操作。");
            _webBrowseBusy = true;
            try
            {
                var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, NativeMainWebWindow.HandleOf(host));
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null) await AddFolderToLibraryAsync(folder.Path);
            }
            finally { _webBrowseBusy = false; }
            operation = "roots";
        }
        if (operation == "roots")
        {
            Library.ResetNodes();
            var roots = (AppSettingsStore.Load().LibraryWatchFolders ?? new()).Where(Directory.Exists)
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            object[] nodes;
            if (roots.Length > 0) nodes = roots.Select(root => Library.RegisterNode(new FolderBrowserItem { DisplayName = root, FullPath = root, IsFolder = true })).ToArray();
            else if (!string.IsNullOrWhiteSpace(_browseFolderPath) && Directory.Exists(_browseFolderPath))
            {
                var children = await Task.Run(() => EnumerateFolderChildren(_browseFolderPath, 0));
                nodes = children.Select(Library.RegisterNode).ToArray();
            }
            else nodes = Array.Empty<object>();
            return new { nodes, total = LibraryTracks.Count, rootCount = roots.Length };
        }
        string id = msg.GetProperty("nodeId").GetString() ?? "";
        if (!Library.TryGetNode(id, out var node)) throw new InvalidOperationException("目录已刷新，请重新选择。");
        int epoch = Library.Epoch;
        if (operation == "children")
        {
            if (!node.IsFolder) throw new ArgumentException("该条目不是文件夹。");
            var children = await Task.Run(() => EnumerateFolderChildren(node.FullPath, 0));
            if (epoch != Library.Epoch) throw new InvalidOperationException("目录已刷新。");
            return new { nodes = children.Select(Library.RegisterNode).ToArray() };
        }
        if (operation is "songs" or "play" or "queue")
        {
            var library = LibraryTracks.ToArray().Where(track => !track.IsRemote).GroupBy(track => track.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            var songs = await Task.Run(() =>
            {
                var paths = node.IsFolder ? EnumerateAudioFiles(node.FullPath).ToArray() : File.Exists(node.FullPath) ? new[] { node.FullPath } : Array.Empty<string>();
                var result = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (library.TryGetValue(path, out var known)) result.AddRange(known.Select(ClonePlaylistItem));
                    else try { result.Add(CreatePlaylistItemFromPath(path)); } catch (Exception ex) { StartupLog.WriteException("WebFolders.Metadata", ex); }
                }
                return OrderAlbumTracks(result);
            });
            if (epoch != Library.Epoch) throw new InvalidOperationException("目录已刷新，请重新选择。");
            Library.RememberTracks(songs);
            if (operation == "play" && songs.Count > 0)
            {
                if (!node.IsFolder) PlayPlaylistItem(songs[0]);
                else { _userPlaylist.Clear(); _userPlaylistIndex = -1; WebInsertQueue(songs.ToArray(), next: false); PlayUserPlaylistAt(0); }
            }
            else if (operation == "queue") WebInsertQueue(songs.ToArray(), next: false);
            return new { nodeId = id, path = node.FullPath, tracks = songs.Select(song => new {
                id = WebTrackIdentity.Of(song), title = Path.GetFileName(song.FilePath), songTitle = song.Title, artist = song.Artist, album = song.Album,
                duration = song.Duration.TotalSeconds, coverUrl = "https://celeste-cover.internal/cover/" + WebTrackIdentity.Of(song)
            }).ToArray() };
        }
        throw new ArgumentException("未知的文件夹操作。");
    }
}
