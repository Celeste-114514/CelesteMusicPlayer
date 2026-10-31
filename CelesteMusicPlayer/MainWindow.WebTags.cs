using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        // W13：标签页的业务本体在 WebTagService（在线搜索 / 歌词编排 / 批量改名 / 封面
        // / 工作集列表），文件 I/O 在 WebTagFileService。这里只剩转发 + 五条宿主能力。
        private WebTagService? _tagService;

        private WebTagService Tags =>
            LazyInitializer.EnsureInitialized(ref _tagService, () => new WebTagService(new WebTagHost(this)));

        internal Task<object> WebTagsAsync(JsonElement msg, IReadOnlyDictionary<string, PlaylistItem> tracks)
            => Tags.ExecuteAsync(msg, tracks);

        internal Task<(PlaylistItem[] Tracks, string Directory, bool Canceled)> WebTagAddFilesAsync(bool folder, object host)
            => Tags.AddFilesAsync(folder, host);

        internal PlaylistItem WebTagFromPath(string path) => CreatePlaylistItemFromPath(path);

        /// <summary>网页命令共享的 JSON 整数读取（DSP 那条命令也在用，别搬进服务里）。</summary>
        private static int ReadInt(JsonElement msg, string property, int fallback)
        {
            if (!msg.TryGetProperty(property, out var element))
                return fallback;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number))
                return number;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out int parsed))
                return parsed;
            return fallback;
        }

        /// <summary>
        /// 标签服务够不着的东西只有五样：播放队列（模型本来就是独立类型）、
        /// 界面当前绑定的行集合（重命名要跟着改路径）、正在播放的路径（禁止改名）、
        /// 队列落盘、元数据刷新。别把 <c>_tracks</c> 之类的现成属性加进来 —— 那些能从参数拿。
        /// </summary>
        private sealed class WebTagHost(MainWindow owner) : IWebTagHost
        {
            public PlaybackQueue Queue => owner._queue;

            public IReadOnlyList<PlaylistItem> VisibleRows => owner._playlist;

            public string? NowPlayingPath => owner._nowPlayingPath;

            public void SavePlayQueue() => owner.SavePlayQueue();

            public void OnTagsSaved(string path) => owner.OnTagsSaved(path);
        }
    }
}
