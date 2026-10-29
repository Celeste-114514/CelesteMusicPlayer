using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对播放台账的宿主实现。
    /// W13：记账规则在 <see cref="PlaybackJournalService"/>，这里只给事实、收通知。
    /// </summary>
    public sealed partial class MainWindow : IPlaybackJournalHost
    {
        private PlaybackJournalService? _journal;

        internal PlaybackJournalService Journal =>
            LazyInitializer.EnsureInitialized(ref _journal, () => new PlaybackJournalService(this));

        string? IPlaybackJournalHost.NowPlayingPath => _nowPlayingPath;

        double IPlaybackJournalHost.PositionSeconds => EnginePositionValue.TotalSeconds;

        string IPlaybackJournalHost.HistoryKey => GetCurrentPlayingItem()?.PersistenceKey ?? _nowPlayingPath ?? "";

        // 采样判据必须两条播放路径都问：正式播放走引擎（HiFi），MediaPlayer 此时没有源。
        // 旧实现只问 MediaPlayer，导致引擎播放时采样被早退跳过 —— 「累计收听秒数」恒为 0。
        bool IPlaybackJournalHost.HasSource => IsEngineActiveNow || GetPlayer()?.Source != null;

        bool IPlaybackJournalHost.IsPlaying => IsPlaybackActuallyPlaying();

        void IPlaybackJournalHost.NoteChainTrack(string title, string artist) => NoteChainTrack(title, artist);

        void IPlaybackJournalHost.RefreshFavoriteButton() => UpdateFavoriteButtonUi();

        void IPlaybackJournalHost.RefreshHomeAfterHistory() => NotifyWebHomeChanged();
    }
}
