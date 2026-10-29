using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 队列页的「上一首 / 下一首」预览。
    ///
    /// W13：从 MainWindow.WebTransport 搬出来。纯函数——只看队列、当前下标和播放顺序，
    /// 不读引擎、不推进随机数发生器、不碰 UI。
    ///
    /// 铁律：卡片上写哪一首，点下去就必须是那一首。
    /// 所以随机模式**不真摇**，如实说「随机」；单曲循环如实说这首会继续。
    /// </summary>
    internal static class WebQueuePreview
    {
        /// <summary>
        /// 上一首预览。索引直接用播放器那套 ResolvePreviousIndex，不在网页里另算一遍。
        /// 顺序播放走到第一首时没有上一首（kind=none），循环/随机则回卷到队尾。
        /// </summary>
        internal static object Previous(PlaylistItem[] items, int queueIndex, PlaybackOrderResolver order)
        {
            if (items.Length == 0 || queueIndex < 0 || queueIndex >= items.Length)
            {
                return new { kind = "unknown" };
            }

            int? index = order.ResolvePreviousIndex(items.Length, queueIndex);
            if (index is null || index.Value < 0 || index.Value >= items.Length)
            {
                return new { kind = "none" };
            }

            var item = items[index.Value];
            return new
            {
                // 队列只有一首且允许回卷时，"上一首"就是这首自己
                kind = index.Value == queueIndex ? "self" : "track",
                index = index.Value, title = item.Title, artist = item.Artist, album = item.Album,
                duration = item.Duration.TotalSeconds,
                coverUrl = "https://celeste-cover.internal/cover/" + WebTrackIdentity.Of(item)
            };
        }

        /// <summary>
        /// 下一首预览。只按当前播放顺序推算，不看、也不推进随机数发生器 ——
        /// 随机模式下真去摇一次，等于把真正要播的那首摇走了，预览和实际就对不上。
        /// </summary>
        internal static object Next(PlaylistItem[] items, int queueIndex, PlaybackOrderResolver order)
        {
            const string none = "none";
            string kind;
            int index = -1;
            if (items.Length == 0 || queueIndex < 0 || queueIndex >= items.Length)
            {
                kind = "unknown";
            }
            else
            {
                switch (order.Order)
                {
                    case PlaybackOrder.Random:
                        kind = items.Length == 1 ? "self" : "random";
                        index = items.Length == 1 ? queueIndex : -1;
                        break;
                    case PlaybackOrder.TrackLoop:
                        kind = "self"; index = queueIndex; break;
                    case PlaybackOrder.TrackOnce:
                        kind = none; break;
                    case PlaybackOrder.Sequential:
                        kind = queueIndex + 1 < items.Length ? "track" : none;
                        index = queueIndex + 1;
                        break;
                    default: // ListLoop：走到末尾就回第一首
                        kind = "track"; index = (queueIndex + 1) % items.Length; break;
                }
            }

            if (index >= 0 && index < items.Length)
            {
                var item = items[index];
                return new
                {
                    kind, index, title = item.Title, artist = item.Artist, album = item.Album,
                    duration = item.Duration.TotalSeconds,
                    coverUrl = "https://celeste-cover.internal/cover/" + WebTrackIdentity.Of(item)
                };
            }
            return new { kind };
        }
    }
}
