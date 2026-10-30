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

// —— 起播结果分类（PlaybackKindRule，W13 第二刀抽出的纯规则）——
void CheckKind(string name, PlaybackKind actual, PlaybackKind expected)
{
    bool ok = actual == expected; if (!ok) failures++;
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + " 实际=" + actual + " 期望=" + expected);
}
void CheckText(string name, string actual, string expected)
{
    bool ok = actual == expected; if (!ok) failures++;
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name + " 实际=[" + actual + "]");
}

// DoP 直出优先：只要后端说直出，就不能再报"转码"
CheckKind("DoP 直出", PlaybackKindRule.Of(true, true), PlaybackKind.DsdDirect);
CheckKind("DoP 直出不看源标记", PlaybackKindRule.Of(true, false), PlaybackKind.DsdDirect);
CheckKind("DSD 回退 PCM", PlaybackKindRule.Of(false, true), PlaybackKind.DsdTranscoded);
CheckKind("普通 PCM", PlaybackKindRule.Of(false, false), PlaybackKind.Pcm);

// 格式描述的三级回退：源 → 实际输出 → "PCM"，任何情况都不吐空串
CheckText("描述 源优先", PlaybackKindRule.PcmDescription("DSD 1-bit 原生（已转 PCM）", "32bit/384kHz"), "DSD 1-bit 原生（已转 PCM）");
CheckText("描述 退到实际输出", PlaybackKindRule.PcmDescription(null, "32bit/384kHz"), "32bit/384kHz");
CheckText("描述 全空说 PCM", PlaybackKindRule.PcmDescription("  ", null), "PCM");
CheckText("描述 空串不吐空格", PlaybackKindRule.PcmDescription(null, ""), "PCM");

Console.WriteLine(failures == 0 ? "ALL PASS" : ("FAILURES=" + failures));
return failures == 0 ? 0 : 1;
