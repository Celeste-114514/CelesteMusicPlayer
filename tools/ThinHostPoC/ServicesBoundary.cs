using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer.ThinHost;

/// <summary>
/// W6 第四步：播放会话/播放顺序/OPRA 历史/Web 规则等纯逻辑服务自检。
/// 两个落盘存储都用内部测试钩子指向临时文件，不碰用户真实数据；
/// 排序器与规则是纯函数，直接在内存里验证。
/// GlobalHotkeyService 只求编译覆盖（隐藏测试里注册系统热键会干扰真机，不实测）。
/// </summary>
internal static class ServicesBoundary
{
    internal sealed record Result(bool Passed, int Checks, IReadOnlyList<string> Details);

    internal static Result Run()
    {
        var details = new List<string>();
        int checks = 0;
        string tempRoot = Path.Combine(Path.GetTempPath(), "CelestePocServicesBoundary-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // ---- 1) 播放顺序决策（纯逻辑）----
            var resolver = new PlaybackOrderResolver { Order = PlaybackOrder.ListLoop };
            Check(details, ref checks, "order.listloop.wrap", resolver.ResolveNextIndex(3, 2, autoAdvance: true) == 0,
                "列表循环：最后一首的下一首回到 0");
            resolver.Order = PlaybackOrder.Sequential;
            Check(details, ref checks, "order.sequential.end", resolver.ResolveNextIndex(3, 2, autoAdvance: true) == null,
                "顺序播放：最后一首之后没有下一首");
            resolver.Order = PlaybackOrder.TrackOnce;
            Check(details, ref checks, "order.trackonce.advance", resolver.ResolveNextIndex(3, 0, autoAdvance: true) == null,
                "单曲一次：自动前进即停止");
            resolver.Order = PlaybackOrder.TrackLoop;
            Check(details, ref checks, "order.trackloop", resolver.ResolveNextIndex(3, 1, autoAdvance: true) == 1,
                "单曲循环：自动前进还在原曲");
            resolver.Order = PlaybackOrder.Random;
            int rnd = resolver.ResolveNextIndex(5, 0, autoAdvance: true) ?? -1;
            Check(details, ref checks, "order.random.range", rnd >= 0 && rnd < 5, $"随机索引={rnd}");

            // ---- 2) 队列移动规则（纯逻辑）----
            var queue = new ObservableCollection<string> { "a", "b", "c", "d" };
            int playing = WebQueueRules.Move(queue, from: 2, to: 0, playingIndex: 3);
            Check(details, ref checks, "queue.move.order",
                string.Join(",", queue) == "c,a,b,d", string.Join(",", queue));
            Check(details, ref checks, "queue.move.playing", playing == 3,
                "被移走的项在播放项之后 → 播放索引不变");
            playing = WebQueueRules.Move(queue, from: 0, to: 3, playingIndex: 3);
            Check(details, ref checks, "queue.move.playing.follows", string.Join(",", queue) == "a,b,d,c" && playing == 2,
                $"移动的就是播放项 → 索引跟到 {playing}");

            // ---- 3) 浏览规则（纯逻辑）----
            string folder = WebBrowseRules.Folder(@"D:\Music\华语\周杰伦", new[] { @"D:\Music" });
            Check(details, ref checks, "browse.folder", folder == Path.Combine("华语", "周杰伦"), folder);
            Check(details, ref checks, "browse.folder.outside",
                WebBrowseRules.Folder(@"E:\Elsewhere\x.flac", new[] { @"D:\Music" }).Length > 0,
                "根之外的文件给个兜底标签不抛");

            // ---- 4) 续播会话往返（临时文件）----
            string sessionPath = Path.Combine(tempRoot, "last-playback.json");
            PlaybackSessionStore.FilePathOverride = sessionPath;
            PlaybackSessionStore.Save(@"C:\Music\a.flac", 123.75);
            var session = PlaybackSessionStore.TryLoad();
            Check(details, ref checks, "playbacksession.roundtrip",
                session?.FilePath == @"C:\Music\a.flac" && Math.Abs(session.PositionSeconds - 123.75) < 0.001);
            PlaybackSessionStore.FilePathOverride = null;

            // ---- 5) OPRA 历史往返（临时文件，钩子自带缓存失效）----
            string opraPath = Path.Combine(tempRoot, "opra-history.json");
            OpraHistoryStore.FilePathOverride = opraPath;
            var item = new OpraHistoryItem("eq-1", "prod-1", "测试曲线", "厂牌", "作者", 10, -3.5);
            OpraHistoryStore.AddRecent(item);
            Check(details, ref checks, "opra.recent", OpraHistoryStore.Load().Recent.Any(x => x.EqId == "eq-1"));
            Check(details, ref checks, "opra.favorite.toggle",
                OpraHistoryStore.ToggleFavorite(item) && OpraHistoryStore.IsFavorite("eq-1") && !OpraHistoryStore.ToggleFavorite(item),
                "收藏 → 取消 → 不再是收藏");
            OpraHistoryStore.FilePathOverride = null;
        }
        finally
        {
            PlaybackSessionStore.FilePathOverride = null;
            OpraHistoryStore.FilePathOverride = null;
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* 尽力清理 */ }
        }

        bool passed = details.All(d => !d.EndsWith("= FAIL", StringComparison.Ordinal));
        return new Result(passed, checks, details);
    }

    private static void Check(List<string> details, ref int checks, string name, bool ok, string? note = null)
    {
        checks++;
        details.Add($"{name} = {(ok ? "OK" : "FAIL")}{(note == null ? "" : "（" + note + "）")}");
    }
}
