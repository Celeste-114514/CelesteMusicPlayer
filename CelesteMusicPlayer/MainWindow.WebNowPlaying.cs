using System;
using System.Linq;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 网页「正在播放」详情页要的一份快照：曲目身份 + 整首歌词 + 当前句。
        ///
        /// 为什么这里不带播放进度：网页每 250ms 收一次 celeste.state，进度和时间都在里面，
        /// 当前句由前端按时间自己算。每帧跨宿主取一次会把这条消息链打爆，
        /// 也没必要为一个高亮位置多开一条 RPC。
        ///
        /// 歌词为空不区分「还没解析完」和「这首歌确实没有」——网页那边看到空会重试两次，
        /// 之后才显示「没有找到歌词」，因为解析是异步的。
        /// </summary>
        internal object WebNowPlayingVisual()
        {
            var values = new float[FormatHelper.WaveBarCount];
            bool playing = IsPlaybackActuallyPlaying();
            bool available = playing && (_audioEngine?.TryGetSpectrum(values) ?? false);
            var samples = new float[256];
            bool samplesAvailable = playing && (_audioEngine?.TryGetSamples(samples) ?? false);
            return new
            {
                samplesAvailable,
                samples = samples.Select(v => samplesAvailable && float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f).ToArray(),
                trackId = GetCurrentPlayingItem() is { } item ? WebTrackId(item) : "",
                available,
                bands = values.Select(v => available && float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f).ToArray()
            };
        }

        internal object WebNowPlaying()
        {
            var item = GetCurrentPlayingItem();
            string trackId = item == null ? string.Empty : WebTrackId(item);
            var lines = GetLyricLinesPublic();
            var rows = lines
                .Select(line => new
                {
                    t = Math.Round(Math.Max(0, line.Time.TotalSeconds), 3),
                    text = line.Text ?? string.Empty,
                    tr = line.IsTranslation
                })
                .ToArray();
            return new
            {
                trackId,
                lyricState = rows.Length > 0 ? "ready" : "none",
                currentIndex = GetCurrentLyricIndexPublic(),
                lines = rows
            };
        }
    }
}
