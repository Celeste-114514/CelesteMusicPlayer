using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer;

public sealed partial class MainWindow
{
    // W13：节点的登记与目录枚举都在 WebFolderBrowseService 里；这里只剩「挑文件夹」和 play/queue。
    private WebFolderBrowseService? _folderBrowse;

    private WebFolderBrowseService Folders =>
        LazyInitializer.EnsureInitialized(ref _folderBrowse,
            () => new WebFolderBrowseService(Library, () => LibraryTracks, CreatePlaylistItemFromPath));

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
        if (operation == "roots") return await Folders.RootsAsync(_browseFolderPath);

        string id = msg.GetProperty("nodeId").GetString() ?? "";
        if (operation == "children") return await Folders.ChildrenAsync(id);
        if (operation is "songs" or "play" or "queue")
        {
            WebFolderSongList list = await Folders.SongsAsync(id);
            if (operation == "play" && list.Songs.Count > 0)
            {
                if (!list.IsFolder) PlayPlaylistItem(list.Songs[0]);
                else { _queue.Items.Clear(); _queue.Index = -1; WebInsertQueue(list.Songs.ToArray(), next: false); PlayUserPlaylistAt(0); }
            }
            else if (operation == "queue") WebInsertQueue(list.Songs.ToArray(), next: false);
            return WebFolderBrowseService.Payload(list);
        }
        throw new ArgumentException("未知的文件夹操作。");
    }
}
