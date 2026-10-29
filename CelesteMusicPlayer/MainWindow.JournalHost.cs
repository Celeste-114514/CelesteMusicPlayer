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

        bool IPlaybackJournalHost.HasSource => GetPlayer()?.Source != null;

        bool IPlaybackJournalHost.IsPlaying => GetPlayer()?.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;

        void IPlaybackJournalHost.NoteChainTrack(string title, string artist) => NoteChainTrack(title, artist);

        void IPlaybackJournalHost.RefreshFavoriteButton() => UpdateFavoriteButtonUi();

        void IPlaybackJournalHost.RefreshHomeAfterHistory() => NotifyWebHomeChanged();
    }
}
