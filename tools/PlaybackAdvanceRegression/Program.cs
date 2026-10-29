using CelesteMusicPlayer;

// 「下一首往哪走」的穷举用例。纯函数，不需要声卡。
int failures = 0;

void Check(string name, int? actual, int? expected)
{
    bool ok = actual == expected;
    if (!ok) failures++;
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + " 实际=" + (actual?.ToString() ?? "null") + " 期望=" + (expected?.ToString() ?? "null"));
}

Func<int, int> Fixed(int value) => _ => value;

// 顺序播放：走到末尾即停
Check("顺序 0→1", LibraryAdvanceRule.NextIndex(PlaybackOrder.Sequential, 3, 0, true, Fixed(0)), 1);
Check("顺序 1→2", LibraryAdvanceRule.NextIndex(PlaybackOrder.Sequential, 3, 1, true, Fixed(0)), 2);
Check("顺序 末尾停", LibraryAdvanceRule.NextIndex(PlaybackOrder.Sequential, 3, 2, true, Fixed(0)), null);

// 列表循环：末尾回卷
Check("循环 0→1", LibraryAdvanceRule.NextIndex(PlaybackOrder.ListLoop, 3, 0, true, Fixed(0)), 1);
Check("循环 末尾→0", LibraryAdvanceRule.NextIndex(PlaybackOrder.ListLoop, 3, 2, true, Fixed(0)), 0);

// 单曲循环：停在原地
Check("单曲循环 停在1", LibraryAdvanceRule.NextIndex(PlaybackOrder.TrackLoop, 3, 1, true, Fixed(0)), 1);

// 单曲播放：自动与手点都不续播
Check("单曲播放 自动不续", LibraryAdvanceRule.NextIndex(PlaybackOrder.TrackOnce, 3, 1, true, Fixed(0)), null);
Check("单曲播放 手点也不续", LibraryAdvanceRule.NextIndex(PlaybackOrder.TrackOnce, 3, 1, false, Fixed(0)), null);

// 随机：只有一首就重播；多首时避开当前
Check("随机 单曲", LibraryAdvanceRule.NextIndex(PlaybackOrder.Random, 1, 0, true, Fixed(0)), 0);
Check("随机 撞到当前则顺延", LibraryAdvanceRule.NextIndex(PlaybackOrder.Random, 3, 1, true, Fixed(1)), 2);
Check("随机 未撞则照用", LibraryAdvanceRule.NextIndex(PlaybackOrder.Random, 3, 1, true, Fixed(0)), 0);
Check("随机 撞到末尾回卷", LibraryAdvanceRule.NextIndex(PlaybackOrder.Random, 3, 2, true, Fixed(2)), 0);

// 边界
Check("空列表", LibraryAdvanceRule.NextIndex(PlaybackOrder.ListLoop, 0, 0, true, Fixed(0)), null);
Check("无在播下标", LibraryAdvanceRule.NextIndex(PlaybackOrder.ListLoop, 3, -1, true, Fixed(0)), null);

Console.WriteLine(failures == 0 ? "ALL PASS" : ("FAILURES=" + failures));
return failures == 0 ? 0 : 1;
