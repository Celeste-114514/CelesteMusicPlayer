using System;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 「在线曲目引用」的解析：`.ref` 文件 → 曲目 id → 曲库快照。
    ///
    /// W13：原来散在 <c>MainWindow.WebSources.cs</c> 里的三个静态方法搬到这里。
    /// 它们本来就没有实例状态，放在 MainWindow 上只会让播放列表导入导出这类纯文件逻辑
    /// 反过来去够 MainWindow。
    /// </summary>
    internal static class TrackReference
    {
        /// <summary>没有来源 id 不是错误：普通本地曲目本来就不该命中。</summary>
        internal static string ReferenceId(string path)
        {
            if (!Path.GetExtension(path).Equals(".ref", StringComparison.OrdinalIgnoreCase)) return "";
            string directory = Path.Combine(AppSettingsStore.GetConfigDirectory(), "track-refs") + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(directory, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(path) : "";
        }

        internal static PlaylistItem? FromReference(string path)
        {
            string id = ReferenceId(path);
            return id.Length == 0 ? null : Resolve(id);
        }

        internal static PlaylistItem? Resolve(string id)
        {
            var record = NetworkLibraryStore.Track(id);
            if (record == null) return null;
            var source = record.SourceId.Length == 0 ? null : NetworkLibraryStore.Sources().FirstOrDefault(s => s.Id == record.SourceId);
            return WebLibraryService.FromNetwork(record, source);
        }
    }
}
