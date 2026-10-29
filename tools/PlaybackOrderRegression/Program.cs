using CelesteMusicPlayer;

// 播放顺序解析器的穷举校验：
// ① 确定性的几种顺序逐条对答案；
// ② 随机的几种只验性质（落在范围内、不连播同一首）；
// ③ 锁住「ResolveNextIndex 与 NextIndexByOrder 刻意不合并」这个行为差异。
int failures = 0;
void Check(string name, bool ok)
{
    if (!ok) failures++;
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
}

PlaybackOrderResolver Make(PlaybackOrder order) => new(new Random(12345)) { Order = order };

var deterministic = new[]
{
    (PlaybackOrder.Sequential, 3, 0, true, (int?)1),
    (PlaybackOrder.Sequential, 3, 1, true, 2),
    (PlaybackOrder.Sequential, 3, 2, true, null),
    (PlaybackOrder.ListLoop, 3, 0, true, 1),
    (PlaybackOrder.ListLoop, 3, 2, true, 0),
    (PlaybackOrder.TrackLoop, 3, 1, true, 1),
    (PlaybackOrder.TrackOnce, 3, 1, true, null),
    (PlaybackOrder.TrackOnce, 3, 1, false, 2),
    (PlaybackOrder.TrackOnce, 3, 2, false, null),
    (PlaybackOrder.ListLoop, 0, 0, true, null),
};

foreach (var (order, count, index, auto, expected) in deterministic)
{
    var r = Make(order);
    Check($"{order} n={count} i={index} auto={auto} → {expected?.ToString() ?? "null"}",
        r.ResolveNextIndex(count, index, auto) == expected);
}

// 随机：只验性质
foreach (int count in new[] { 2, 3, 8 })
{
    var r = Make(PlaybackOrder.Random);
    bool inRange = true, neverSelf = true;
    for (int i = 0; i < count; i++)
    {
        for (int k = 0; k < 40; k++)
        {
            int? next = r.ResolveNextIndex(count, i, true);
            if (next is not int v || v < 0 || v >= count) inRange = false;
            else if (v == i) neverSelf = false;
        }
    }
    Check($"随机 n={count} 落在范围内", inRange);
    Check($"随机 n={count} 不连播同一首", neverSelf);
}

// 关键行为差异（刻意的，别"统一"掉）：
// 随机 + 只有一首 + 下标越界(-1) 时，ResolveNextIndex 按 0 处理返回 0，
// 而 NextIndexByOrder 原样把 baseIndex 吐回来（-1）。
var single = Make(PlaybackOrder.Random);
Check("随机 n=1 i=-1：ResolveNextIndex 归一成 0", single.ResolveNextIndex(1, -1, true) == 0);
Check("随机 n=1 i=-1：NextIndexByOrder 原样返回 -1", Make(PlaybackOrder.Random).NextIndexByOrder(1, -1, true) == -1);

// 上一首：确定性的对答案，其余验范围
var prevCases = new[]
{
    (PlaybackOrder.Sequential, 3, 0, (int?)null),
    (PlaybackOrder.Sequential, 3, 2, 1),
    (PlaybackOrder.ListLoop, 3, 0, 2),
    (PlaybackOrder.ListLoop, 3, 1, 0),
    (PlaybackOrder.TrackLoop, 3, 0, 2),
    (PlaybackOrder.TrackOnce, 3, 0, null),
    (PlaybackOrder.Random, 3, 0, 2),
};
foreach (var (order, count, index, expected) in prevCases)
{
    var r = Make(order);
    Check($"上一首 {order} n={count} i={index} → {expected?.ToString() ?? "null"}", r.ResolvePreviousIndex(count, index) == expected);
}
Check("上一首 空列表", Make(PlaybackOrder.ListLoop).ResolvePreviousIndex(0, 0) == null);

// 全量边界：任何顺序、任何长度、任何下标，结果必须落在范围内（或 null / -1）
bool bounds = true;
foreach (PlaybackOrder order in Enum.GetValues<PlaybackOrder>())
{
    for (int count = 0; count <= 6; count++)
    {
        for (int index = -1; index < Math.Max(count, 1); index++)
        {
            var r = Make(order);
            int? n = r.ResolveNextIndex(count, index, true);
            if (n is int nv && (nv < 0 || nv >= count)) bounds = false;
            int nb = r.NextIndexByOrder(count, index, true);
            if (nb != -1 && (nb < 0 || nb >= count)) bounds = false;
            int? p = r.ResolvePreviousIndex(count, index);
            if (p is int pv && (pv < 0 || pv >= count)) bounds = false;
        }
    }
}
Check("全量边界：结果都在 [0,count) / null / -1", bounds);

Console.WriteLine(failures == 0 ? "ALL PASS" : ("FAILURES=" + failures));
return failures == 0 ? 0 : 1;
