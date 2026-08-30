using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放顺序决策：把 MainWindow 里"下一首 / 上一首索引怎么算"的一组实例方法搬到这里。
    /// 只持有两个状态（当前顺序枚举 + 随机源），不碰任何 XAML、不碰 UI 线程。
    /// </summary>
    internal sealed class PlaybackOrderResolver
    {
        private readonly Random _random = new();

        /// <summary>当前播放顺序。默认 ListLoop（与原先 MainWindow 里那个播放顺序字段的初值一致）。</summary>
        public PlaybackOrder Order { get; set; } = PlaybackOrder.ListLoop;

        /// <summary>在当前顺序下取一个随机索引（列表内随机选曲用）。</summary>
        public int NextRandomIndex(int count)
        {
            return count <= 0 ? 0 : _random.Next(count);
        }

        /// <summary>
        /// 解析下一首索引；返回 null 表示"没有下一首"。
        /// baseIndex 为负时按 0 处理（与原实现一致）。
        /// </summary>
        public int? ResolveNextIndex(int count, int baseIndex, bool autoAdvance)
        {
            if (count == 0)
            {
                return null;
            }

            int b = baseIndex >= 0 ? baseIndex : 0;

            switch (Order)
            {
                case PlaybackOrder.TrackOnce:
                    if (autoAdvance)
                    {
                        return null;
                    }
                    return b + 1 < count ? b + 1 : null;

                case PlaybackOrder.TrackLoop:
                    if (autoAdvance)
                    {
                        return b;
                    }
                    return b + 1 < count ? b + 1 : 0;

                case PlaybackOrder.Sequential:
                {
                    int next = b + 1;
                    return next >= count ? null : next;
                }

                case PlaybackOrder.ListLoop:
                    return (b + 1) % count;

                case PlaybackOrder.Random:
                {
                    if (count == 1)
                    {
                        return 0;
                    }

                    int next = _random.Next(count);
                    if (next == b)
                    {
                        next = (next + 1) % count;
                    }
                    return next;
                }

                default:
                    return (b + 1) % count;
            }
        }

        /// <summary>
        /// 与 ResolveNextIndex 同逻辑，但用 -1 表示"没有下一首"（供无缝预加载预测等场景使用）。
        /// 注意两个方法刻意不合并，因为存在行为差异：
        /// 随机模式且只有一首时，本方法返回 baseIndex，而 ResolveNextIndex 返回 0。
        /// 这是 MainWindow 原实现的既有行为，必须原样保留。
        /// </summary>
        public int NextIndexByOrder(int count, int baseIndex, bool autoAdvance)
        {
            if (count <= 0)
            {
                return -1;
            }

            switch (Order)
            {
                case PlaybackOrder.TrackOnce:
                    if (autoAdvance)
                    {
                        return -1; // 单曲只播一遍，不自动续播
                    }
                    return baseIndex + 1 < count ? baseIndex + 1 : -1;

                case PlaybackOrder.TrackLoop:
                    if (autoAdvance)
                    {
                        return baseIndex;
                    }
                    return baseIndex + 1 < count ? baseIndex + 1 : 0;

                case PlaybackOrder.Sequential:
                    return baseIndex + 1 < count ? baseIndex + 1 : -1;

                case PlaybackOrder.Random:
                    if (count == 1)
                    {
                        return baseIndex;
                    }
                    {
                        int next = _random.Next(count);
                        return next == baseIndex ? (next + 1) % count : next;
                    }

                default: // ListLoop 等：循环到列表尾回到开头
                    return (baseIndex + 1) % count;
            }
        }

        /// <summary>解析上一首索引；返回 null 表示"没有上一首"。</summary>
        public int? ResolvePreviousIndex(int count, int baseIndex)
        {
            if (count == 0)
            {
                return null;
            }

            int b = baseIndex >= 0 ? baseIndex : 0;

            switch (Order)
            {
                case PlaybackOrder.Sequential:
                case PlaybackOrder.TrackOnce:
                    return b > 0 ? b - 1 : null;

                case PlaybackOrder.Random:
                case PlaybackOrder.ListLoop:
                case PlaybackOrder.TrackLoop:
                default:
                    return b <= 0 ? count - 1 : b - 1;
            }
        }
    }
}
