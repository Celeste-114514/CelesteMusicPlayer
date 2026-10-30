using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>一次「列出目录里的歌」的结果。播放/入队由宿主做，服务只负责把歌备齐。</summary>
    internal sealed record WebFolderSongList(string NodeId, string Path, bool IsFolder, IReadOnlyList<PlaylistItem> Songs);

    /// <summary>
    /// 网页侧目录浏览：`folders` 命令里的 roots / children / songs。
    ///
    /// W13：从 <c>MainWindow.WebFolders.cs</c> 搬出。这里**零播放、零 UI**：
    /// 挑文件夹走宿主的文件对话框、play/queue 也由宿主执行，服务只做「枚举 → 建曲目 → 登记节点」。
    ///
    /// 两个外部依赖都是所有权还在宿主那边的集合/工厂：
    /// 曲库快照用一支委托取（同 <see cref="WebLibraryService"/> 的做法），
    /// 单曲元数据由宿主提供的工厂读（TagLib + 网络引用解析还在宿主）。
    /// </summary>
    internal sealed class WebFolderBrowseService(
        WebLibraryService library,
        Func<IReadOnlyList<PlaylistItem>> libraryTracks,
        Func<string, PlaylistItem> createFromPath)
    {
        /// <summary>
        /// 列根：设置里的媒体库文件夹；一个都没配时退回「上次浏览的文件夹」的直接子项。
        /// 每次调用都整批作废旧节点（epoch +1），旧 id 一律作废。
        /// </summary>
        internal async Task<object> RootsAsync(string? fallbackPath)
        {
            library.ResetNodes();
            string[] roots = (AppSettingsStore.Load().LibraryWatchFolders ?? new()).Where(Directory.Exists)
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            object[] nodes;
            if (roots.Length > 0)
            {
                nodes = roots.Select(root => library.RegisterNode(new FolderScanEntry(root, root, true, 0))).ToArray();
            }
            else if (!string.IsNullOrWhiteSpace(fallbackPath) && Directory.Exists(fallbackPath))
            {
                List<FolderScanEntry> children = await Task.Run(() => LibraryFolderScan.Children(fallbackPath, 0));
                nodes = children.Select(library.RegisterNode).ToArray();
            }
            else
            {
                nodes = Array.Empty<object>();
            }

            return new { nodes, total = libraryTracks().Count, rootCount = roots.Length };
        }

        /// <summary>展开一个文件夹节点（只列一层）。</summary>
        internal async Task<object> ChildrenAsync(string id)
        {
            if (!library.TryGetNode(id, out FolderScanEntry node)) throw new InvalidOperationException("目录已刷新，请重新选择。");
            if (!node.IsFolder) throw new ArgumentException("该条目不是文件夹。");

            int epoch = library.Epoch;
            List<FolderScanEntry> children = await Task.Run(() => LibraryFolderScan.Children(node.FullPath, 0));
            if (epoch != library.Epoch) throw new InvalidOperationException("目录已刷新。");
            return new { nodes = children.Select(library.RegisterNode).ToArray() };
        }

        /// <summary>
        /// 列出一个节点下的曲目（文件夹则递归）：曲库里已有的直接克隆（带评分/收藏），
        /// 没索引过的现读标签，最后统一按碟号/轨道号排序，并登记进节点登记簿供封面取用。
        /// </summary>
        internal async Task<WebFolderSongList> SongsAsync(string id)
        {
            if (!library.TryGetNode(id, out FolderScanEntry node)) throw new InvalidOperationException("目录已刷新，请重新选择。");

            int epoch = library.Epoch;
            var known = libraryTracks().ToArray().Where(track => !track.IsRemote)
                .GroupBy(track => track.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            List<PlaylistItem> songs = await Task.Run(() =>
            {
                string[] paths = node.IsFolder
                    ? LibraryFolderScan.AudioFiles(node.FullPath).ToArray()
                    : File.Exists(node.FullPath) ? new[] { node.FullPath } : Array.Empty<string>();
                var result = new List<PlaylistItem>();
                foreach (string path in paths)
                {
                    if (known.TryGetValue(path, out PlaylistItem[]? hit)) result.AddRange(hit.Select(song => song.Clone()));
                    else
                    {
                        try { result.Add(createFromPath(path)); }
                        catch (Exception ex) { StartupLog.WriteException("WebFolders.Metadata", ex); }
                    }
                }

                return LibraryFolderScan.OrderAlbumTracks(result);
            });
            if (epoch != library.Epoch) throw new InvalidOperationException("目录已刷新，请重新选择。");

            library.RememberTracks(songs);
            return new WebFolderSongList(id, node.FullPath, node.IsFolder, songs);
        }

        /// <summary>把曲目清单翻成网页认识的形状（封面走内部 url，网页拿 id 再回来取）。</summary>
        internal static object Payload(WebFolderSongList list) => new
        {
            nodeId = list.NodeId,
            path = list.Path,
            tracks = list.Songs.Select(song => new
            {
                id = WebTrackIdentity.Of(song),
                title = Path.GetFileName(song.FilePath),
                songTitle = song.Title,
                artist = song.Artist,
                album = song.Album,
                duration = song.Duration.TotalSeconds,
                coverUrl = "https://celeste-cover.internal/cover/" + WebTrackIdentity.Of(song)
            }).ToArray()
        };
    }
}
