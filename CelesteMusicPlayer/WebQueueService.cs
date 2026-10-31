using System;
using System.Linq;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace CelesteMusicPlayer;

/// <summary>
/// 网页队列页（<c>queue</c>）与「更多」菜单（<c>more</c>）需要的宿主能力。
///
/// W13 的规矩：**只放服务自己造不出来的**。队列模型（<see cref="PlaybackQueue"/>）
/// 和播放顺序解析器本身就是独立类型，宿主整个交出来；剩下四条是真做不了——
/// 真播放、界面刷新、外部程序（在资源管理器里显示）。
/// </summary>
internal interface IWebQueueHost
{
    /// <summary>播放队列（模型在 <see cref="PlaybackQueue"/>，含网页用的修订号快照）。</summary>
    PlaybackQueue Queue { get; }

    /// <summary>当前播放顺序：队列页要照着它算「上一首 / 下一首」的预览。</summary>
    PlaybackOrderResolver OrderResolver { get; }

    /// <summary>正在播放的那首（「更多」菜单只对它生效）。</summary>
    PlaylistItem? CurrentTrack { get; }

    /// <summary>点队列里某一行 → 真起播。这条只有引擎那边能做。</summary>
    void PlayQueueIndex(int index);

    /// <summary>队列顺序改完之后刷新界面（重编号 / 通知副窗 / 用户播放列表重新过滤）。</summary>
    void RefreshQueueUi();

    /// <summary>在资源管理器里显示文件。</summary>
    void RevealInExplorer(string filePath);
}

/// <summary>
/// 网页队列页与「更多」菜单的全部业务（<c>queue</c> / <c>more</c> 两条命令）。
///
/// W13：从 <c>MainWindow.WebTransport.cs</c> 搬出。原来那两条命令里，
/// 真正跟窗口有关的只有「起播」「刷新界面」「弹资源管理器」，
/// 其余（分页、摘要、修订号冲突检测、上一首/下一首预览、播放列表归档）都是纯逻辑。
///
/// ⚠ 修订号语义别改：<see cref="PlaybackQueue.WebSnapshot"/> 只在内容变化时才 +1，
/// 网页每 250ms 轮询一次，若每次都 +1 会永远判「队列已变化」。
/// </summary>
internal sealed class WebQueueService(IWebQueueHost host)
{
    /// <summary>队列页：取快照，或按行播放 / 拖动换序。</summary>
    internal object QueueSnapshot(JsonElement msg)
    {
        // 修订号与快照由队列自己维护：内容变了才 +1，轮询不会把修订号刷爆。
        var (items, revision) = host.Queue.WebSnapshot();
        string operation = msg.GetProperty("operation").GetString() ?? "";
        if (operation == "play")
        {
            int index = msg.GetProperty("index").GetInt32();
            if (msg.GetProperty("revision").GetInt32() != revision || index < 0 || index >= items.Length
                || WebTrackIdentity.Of(items[index]) != msg.GetProperty("trackId").GetString())
                throw new InvalidOperationException("播放队列已变化，请刷新后重试。");
            host.PlayQueueIndex(index);
        }
        else if (operation == "move")
        {
            int from = msg.GetProperty("from").GetInt32(), to = msg.GetProperty("to").GetInt32();
            if (msg.GetProperty("revision").GetInt32() != revision || from < 0 || from >= items.Length
                || to < 0 || to >= items.Length || WebTrackIdentity.Of(items[from]) != msg.GetProperty("trackId").GetString())
                throw new InvalidOperationException("播放队列已变化，请刷新后重新拖动。");
            if (from != to)
            {
                // Preserve the exact occurrence, including duplicate songs and CUE segments. Never restart audio.
                host.Queue.Index = WebQueueRules.Move(host.Queue.Items, from, to, host.Queue.Index);
                host.RefreshQueueUi();
                items = host.Queue.Items.ToArray();
                host.Queue.Touch();
                revision = host.Queue.Revision;
            }
        }
        else if (operation != "get") throw new ArgumentException("未知的队列操作。");
        int offset = msg.TryGetProperty("offset", out var value) ? value.GetInt32() : 0;
        if (offset < 0 || offset > items.Length) offset = 0;

        // 摘要按整份队列算，不是按已经翻出来的那 200 首算：
        // 分页加载到哪儿不影响"共 N 首 / 总时长"，否则滚动一下就变数。
        double totalSeconds = 0, remainingSeconds = 0;
        for (int i = 0; i < items.Length; i++)
        {
            double seconds = Math.Max(0, items[i].Duration.TotalSeconds);
            totalSeconds += seconds;
            if (host.Queue.Index >= 0 && i > host.Queue.Index) remainingSeconds += seconds;
        }

        return new
        {
            revision, total = items.Length, offset, currentIndex = host.Queue.Index,
            order = host.OrderResolver.Order.ToString(),
            summary = new { totalSeconds, remainingSeconds, hasCurrent = host.Queue.Index >= 0 && host.Queue.Index < items.Length },
            previous = WebQueuePreview.Previous(items, host.Queue.Index, host.OrderResolver),
            next = WebQueuePreview.Next(items, host.Queue.Index, host.OrderResolver),
            tracks = items.Skip(offset).Take(200).Select((item, i) => new
            {
                id = WebTrackIdentity.Of(item), index = offset + i, title = item.Title, artist = item.Artist,
                album = item.Album, duration = item.Duration.TotalSeconds,
                // 容器格式用扩展名判（不做位深/码率解析：那要逐个读文件，200 行会让宿主卡住）。
                // 位深和采样率这种要真读文件的，只在当前播放卡片上给（见 celeste.state.quality）。
                format = FormatLabel(item),
                remote = item.IsRemote,
                coverUrl = "https://celeste-cover.internal/cover/" + WebTrackIdentity.Of(item)
            }).ToArray()
        };
    }

    /// <summary>队列列表里的"格式"列：只报容器，不去读文件（读 200 个文件会让队列页卡住）。</summary>
    private static string FormatLabel(PlaylistItem item)
    {
        if (item.IsRemote) return "NET";
        string extension = System.IO.Path.GetExtension(item.FilePath).TrimStart('.').ToUpperInvariant();
        return extension.Length is > 0 and <= 5 ? extension : "";
    }

    /// <summary>「更多」菜单：复制信息 / 在文件夹中显示 / 加入播放列表。</summary>
    internal object MoreMenu(JsonElement msg)
    {
        var current = host.CurrentTrack;
        string operation = msg.GetProperty("operation").GetString() ?? "";
        if (operation == "get") return new { playlists = NamedPlaylistStore.List().ToArray() };
        if (current == null) throw new InvalidOperationException("请先选择一首歌曲。");
        if (msg.GetProperty("trackId").GetString() != WebTrackIdentity.Of(current)) throw new InvalidOperationException("当前歌曲已变化，请重新打开菜单。");
        switch (operation)
        {
            case "copy":
                var data = new DataPackage(); data.SetText(current.Title + " - " + current.Artist); Clipboard.SetContent(data); break;
            case "reveal": host.RevealInExplorer(current.FilePath); break;
            case "addToPlaylist":
                string name = msg.GetProperty("name").GetString() ?? "";
                if (!NamedPlaylistStore.List().Contains(name)) throw new InvalidOperationException("播放列表已不存在。");
                NamedPlaylistStore.SaveSongs(name, NamedPlaylistStore.LoadSongs(name).Append(current.PersistenceKey));
                break;
            default: throw new ArgumentException("此功能还未接入网页。");
        }
        return new { message = operation == "copy" ? "已复制歌名与艺术家" : operation == "reveal" ? "已在文件夹中显示" : "已加入播放列表" };
    }
}
