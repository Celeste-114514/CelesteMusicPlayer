using System;
using System.Collections.Generic;
using System.Linq;
using CelesteMusicPlayer;

static void Check(bool value, string name) { if (!value) throw new Exception(name); }
string[] roots = { @"D:\Music", @"D:\Music\Special" };
Check(WebBrowseRules.Folder(@"D:\Music\song.flac", roots) == "Music", "root-level song");
Check(WebBrowseRules.Folder(@"D:\Music\Rock\Album\song.flac", roots) == @"Music\Rock\Album", "root prefix stripped");
Check(WebBrowseRules.Folder(@"d:\music\Rock\song.flac", roots).EndsWith(@"Music\Rock", StringComparison.OrdinalIgnoreCase), "case-insensitive root");
Check(WebBrowseRules.Folder(@"D:\Music\Special\song.flac", roots) == "Special", "most specific root");
Check(WebBrowseRules.Folder(@"D:\MusicExtra\song.flac", roots) == @"D:\MusicExtra", "sibling prefix not a root match");
Check(WebBrowseRules.Folder(@"\\server\share\Music\Rock\song.flac", new[]{@"\\server\share\Music"}) == @"Music\Rock", "UNC root");
Check(WebBrowseRules.Folder(@"D:\Music\song.flac", new[]{@"D:\Music", @"E:\Music"}) != WebBrowseRules.Folder(@"E:\Music\song.flac", new[]{@"D:\Music", @"E:\Music"}), "same folder name across drives");
Track a=new("disc.flac:0"),b=new("disc.flac:90"),c=new("song.flac:0");
var source=new[]{a,b,c};
var moved=WebBrowseRules.Insert(source,new[]{b},a,t=>t.Id,t=>new Track(t.Id),true,false);
Check(moved.Select(t=>t.Id).SequenceEqual(new[]{a.Id,b.Id,c.Id}), "next retains CUE sibling");
Check(ReferenceEquals(moved[0],a), "current queue object retained");
var same=WebBrowseRules.Insert(source,new[]{a},a,t=>t.Id,t=>new Track(t.Id),true,false);
Check(same.Count==4 && ReferenceEquals(same[0],a) && same[1].Id==a.Id, "current song scheduled next keeps active entry");
var appended=WebBrowseRules.Insert(source,new[]{b,b},a,t=>t.Id,t=>new Track(t.Id),false,false);
Check(appended.Select(t=>t.Id).SequenceEqual(new[]{a.Id,c.Id,b.Id}), "append deduplicates by track including cue offset");
var prepended=WebBrowseRules.Insert(source,new[]{c},a,t=>t.Id,t=>new Track(t.Id),false,true);
Check(prepended.Select(t=>t.Id).SequenceEqual(new[]{c.Id,a.Id,b.Id}), "insert-at-begin preference");
// —— 曲目动作规则（2026-10-30 从 WebTrackActionAsync 抽出）——
static void CheckThrow(string name, Action action, string contains)
{
    try { action(); Console.WriteLine("FAIL " + name + "（没抛）"); }
    catch (Exception ex)
    {
        bool ok = ex.Message.Contains(contains);
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Console.WriteLine("   实际消息: " + ex.Message);
    }
}

// ResolveQueueIndices：修订号 / 越界 / 身份不符 / 去重
var queue = new List<Track> { new("q1"), new("q2"), new("q3") };
Check(WebBrowseRules.ResolveQueueIndices(queue, 7, 7, new[] { (1, "q2"), (0, "q1") }, t => t.Id).SequenceEqual(new[] { 1, 0 }), "indices valid, first-seen order");
Check(WebBrowseRules.ResolveQueueIndices(queue, 7, 7, new[] { (2, "q3"), (2, "q3"), (0, "q1") }, t => t.Id).SequenceEqual(new[] { 2, 0 }), "duplicate refs deduped");
CheckThrow("revision mismatch", () => WebBrowseRules.ResolveQueueIndices(queue, 8, 7, new[] { (0, "q1") }, t => t.Id), "播放队列已变化");
CheckThrow("index out of range", () => WebBrowseRules.ResolveQueueIndices(queue, 7, 7, new[] { (3, "q1") }, t => t.Id), "播放队列已变化");
CheckThrow("identity mismatch (CUE sibling)", () => WebBrowseRules.ResolveQueueIndices(queue, 7, 7, new[] { (1, "q1") }, t => t.Id), "播放队列已变化");

// ResolveById：命中 / 缺失 / 去重 / null 拒绝
var map = new Dictionary<string, Track> { ["a"] = queue[0], ["b"] = queue[1] };
Check(WebBrowseRules.ResolveById(new[] { "a", "b", "a" }, map).Select(t => t.Id).SequenceEqual(new[] { "q1", "q2" }), "byId distinct keeps first-seen");
CheckThrow("byId missing", () => WebBrowseRules.ResolveById(new[] { "a", "zz" }, map), "歌曲已不在曲库中");
CheckThrow("byId null id", () => WebBrowseRules.ResolveById(new string?[] { "a", null }, map), "歌曲已不在曲库中");

// RemoveAndPin：原地移除、置顶顺序、播放下标重算
var items = new List<Track> { new("p0"), new("r1"), new("p2"), new("r3") };
Track playingItem = items[2];
int newIdx = WebBrowseRules.RemoveAndPin(items, new[] { 1, 3 }, Array.Empty<Track>(), playingItem);
Check(items.Select(t => t.Id).SequenceEqual(new[] { "p0", "p2" }) && newIdx == 1, "remove keeps others, playing index recomputed");
Track pin1 = new("pinA"), pin2 = new("pinB");
newIdx = WebBrowseRules.RemoveAndPin(items, Array.Empty<int>(), new[] { pin1, pin2 }, playingItem);
Check(items.Select(t => t.Id).SequenceEqual(new[] { "pinA", "pinB", "p0", "p2" }) && newIdx == 3, "pin to front preserves order");
Check(WebBrowseRules.RemoveAndPin(items, new[] { 3 }, Array.Empty<Track>(), playingItem) == -1, "playing removed -> -1");
Check(WebBrowseRules.RemoveAndPin(items, Array.Empty<int>(), Array.Empty<Track>(), null) == -1, "nothing playing -> -1");

// RemoveAndPin 语义钉死：置顶项若同时在移除名单里 = 移除后重新插回队首（右键"置顶"）
var moved2 = new List<Track> { new("m0"), new("m1"), new("m2") };
Track pinned = moved2[2];
WebBrowseRules.RemoveAndPin(moved2, new[] { 2 }, new[] { pinned }, null);
Check(moved2.Select(t => t.Id).SequenceEqual(new[] { "m2", "m0", "m1" }), "pin item removed then reinserted at top");

Console.WriteLine("PASS: root/direct/nested/case/UNC/boundary/collision folder paths; CUE identity, current entry, next/append/prepend queue rules;");
Console.WriteLine("PASS: track-action rules (queue refs / byId / remove+pin).");
sealed record Track(string Id);
