using System;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private PlaybackCommandRouter? _playbackCommands;
        internal bool ExecutePlaybackCommand(string? command)
        {
            if (!DispatcherQueue.HasThreadAccess)
                throw new InvalidOperationException("播放命令必须经宿主 UI 队列执行。");
            _playbackCommands ??= new PlaybackCommandRouter(() => IsEnginePlayingNow,
                PreviousPublic, TogglePlayPausePublic, NextPublic);
            return _playbackCommands.TryExecute(command);
        }
    }
}
