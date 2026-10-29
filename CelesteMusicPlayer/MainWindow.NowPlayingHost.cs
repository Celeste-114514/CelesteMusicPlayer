using System.Collections.Generic;
using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对「正在播放」服务的宿主实现。
    ///
    /// W13：「正在播放」的两个投影（快照 / 频谱与立体声）实现已搬到
    /// <see cref="WebNowPlayingService"/>，这里只提供它需要的八个事实。
    /// 等播放会话自己搬出去，这些口子跟着换到会话上。
    /// </summary>
    public sealed partial class MainWindow : INowPlayingHost
    {
        private WebNowPlayingService? _nowPlaying;

        internal WebNowPlayingService NowPlayingService =>
            LazyInitializer.EnsureInitialized(ref _nowPlaying, () => new WebNowPlayingService(this));

        bool INowPlayingHost.IsPlaying => IsPlaybackActuallyPlaying();

        PlaylistItem? INowPlayingHost.CurrentItem => GetCurrentPlayingItem();

        bool INowPlayingHost.TryGetSpectrum(float[] values, out int sampleRate)
        {
            var engine = _audioEngine;
            if (engine != null) return engine.TryGetDetailedSpectrum(values, out sampleRate);
            sampleRate = 0;
            return false;
        }

        bool INowPlayingHost.TryGetSamples(float[] samples) => _audioEngine?.TryGetSamples(samples) ?? false;

        bool INowPlayingHost.TryGetStereoSamples(float[] left, float[] right) =>
            _audioEngine?.TryGetStereoSamples(left, right) ?? false;

        void INowPlayingHost.SetStereoCapture(bool enabled) => _audioEngine?.SetStereoCapture(enabled);

        IReadOnlyList<LyricLine> INowPlayingHost.LyricLines => GetLyricLinesPublic();

        int INowPlayingHost.CurrentLyricIndex => GetCurrentLyricIndexPublic();
    }
}
