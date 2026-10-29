using System;
using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对系统媒体控件（SMTC）的宿主实现。
    /// W13：静默宿主、元数据、时间轴、按钮分发全在 <see cref="MediaSessionService"/>，
    /// 这里只把「谁在播、下一首是谁」这些事实接过去。
    /// </summary>
    public sealed partial class MainWindow : IMediaSessionHost
    {
        private MediaSessionService? _mediaSession;

        internal MediaSessionService MediaSession =>
            LazyInitializer.EnsureInitialized(ref _mediaSession, () => new MediaSessionService(this));

        void IMediaSessionHost.UpdateTaskbarPlayPause(bool playing) => _taskbarButtons?.UpdatePlayPause(playing);

        byte[]? IMediaSessionHost.ExtractCover(string audioPath) => ExtractCoverBytes(audioPath);

        TimeSpan IMediaSessionHost.EngineDuration => _audioEngine?.Duration ?? TimeSpan.Zero;

        void IMediaSessionHost.TogglePlayPause() => TogglePlayPausePublic();

        void IMediaSessionHost.PlayNext() => PlayNext();

        void IMediaSessionHost.PlayPrevious() => PlayPrevious();

        void IMediaSessionHost.PauseCurrent() => GetPlayer()?.Pause();

        void IMediaSessionHost.RunOnUiThread(Action action) => DispatcherQueue.TryEnqueue(() => action());
    }
}
