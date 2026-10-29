using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 「队列为空时，媒体库列表该往哪首走」的纯规则。
    ///
    /// 为什么单独成类：这段判断以前长在 MainWindow 的 switch 里，跟播放动作缠在一起，
    /// 只能靠真放歌去验证随机/循环对不对。抽出来之后它是纯函数，可以穷举用例。
    ///
    /// 返回 null = 本次不续播（例如单曲播放模式、顺序模式已到最后一首）。
    /// 返回的下标由调用方负责起播，本类不碰引擎。
    /// </summary>
    internal static class LibraryAdvanceRule
    {
        /// <param name="order">当前播放顺序。</param>
        /// <param name="count">媒体库列表长度。</param>
        /// <param name="currentIndex">当前下标（&lt;0 视为没有在播）。</param>
        /// <param name="autoAdvance">true = 自动续播（一首播完），false = 用户点「下一首」。</param>
        /// <param name="nextRandom">随机源注入（测试时可固定），入参是上限，返回 [0,count)。</param>
        internal static int? NextIndex(PlaybackOrder order, int count, int currentIndex, bool autoAdvance, Func<int, int> nextRandom)
        {
            if (count <= 0 || currentIndex < 0)
            {
                return null;
            }

            switch (order)
            {
                // 单曲播放：不管自动还是手点，都不往下一首走
                case PlaybackOrder.TrackOnce:
                    return null;

                // 单曲循环：停在当前这首
                case PlaybackOrder.TrackLoop:
                    return currentIndex;

                // 顺序播放：走到末尾就停，不回卷
                case PlaybackOrder.Sequential:
                    return currentIndex + 1 < count ? currentIndex + 1 : null;

                // 随机：只有一首就重播这首；多首时避开当前这首
                case PlaybackOrder.Random:
                    if (count == 1)
                    {
                        return currentIndex;
                    }

                    int next = nextRandom(count);
                    return next == currentIndex ? (next + 1) % count : next;

                // 列表循环（默认）：走到末尾回第一首
                default:
                    return (currentIndex + 1) % count;
            }
        }
    }
}
