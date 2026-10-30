using System;

namespace CelesteMusicPlayer
{
    /// <summary>起播后这首到底走的是哪条路。界面文案由它决定。</summary>
    internal enum PlaybackKind
    {
        /// <summary>普通 PCM 源。</summary>
        Pcm,
        /// <summary>DSD 源、DoP 直出（bit-perfect）。</summary>
        DsdDirect,
        /// <summary>DSD 源、已转成 PCM 输出（共享 / 建卫拒绝 / 设备协商失败）。</summary>
        DsdTranscoded,
    }

    /// <summary>
    /// 起播结果里"怎么向用户交代"的纯规则（W13 第二刀抽出）。
    ///
    /// 抽出来的理由：这块判断有几个容易悄悄错的分支 —— DoP 直出时不能再说"转码"、
    /// 格式描述读不到时要退到实际输出格式、两者都空时要落到 "PCM"（旧写法在空串时会吐
    /// "已转码为  输出"，两个空格）。这些都用用例钉住，别靠实听碰。
    /// </summary>
    internal static class PlaybackKindRule
    {
        /// <summary>DoP 直出优先；否则看源是不是 DSD，是就是转码，不是就是普通 PCM。</summary>
        internal static PlaybackKind Of(bool dsdDirectOut, bool sourceIsDsd)
            => dsdDirectOut ? PlaybackKind.DsdDirect : sourceIsDsd ? PlaybackKind.DsdTranscoded : PlaybackKind.Pcm;

        /// <summary>源格式优先，读不到退到实际输出格式，再读不到就说 PCM —— 绝不返回空串。</summary>
        internal static string PcmDescription(string? sourceFormatDescription, string? actualOutputFormat)
        {
            if (!string.IsNullOrWhiteSpace(sourceFormatDescription)) return sourceFormatDescription!;
            if (!string.IsNullOrWhiteSpace(actualOutputFormat)) return actualOutputFormat!;
            return "PCM";
        }
    }
}
