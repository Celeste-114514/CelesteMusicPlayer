using System;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow : IPlaybackStateSource
    {
        internal IPlaybackStateSource PlaybackStateSource => this;
        PlaybackSnapshot IPlaybackStateSource.Capture() => CapturePlaybackState();

        internal PlaybackSnapshot CapturePlaybackState()
        {
            if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("播放状态必须在宿主 UI 队列读取。");
            var item = GetCurrentPlayingItem();
            bool active = IsEngineActiveNow;
            var player = GetPlayer();
            double duration = active ? EngineDurationValue.TotalSeconds : player?.PlaybackSession.NaturalDuration.TotalSeconds ?? item?.Duration.TotalSeconds ?? 0;
            double position = active ? EnginePositionValue.TotalSeconds : player?.PlaybackSession.Position.TotalSeconds ?? 0;
            if (duration <= 0) duration = item?.Duration.TotalSeconds ?? 0;
            if (!active && player?.Source == null)
            {
                duration = item?.Duration.TotalSeconds ?? 0;
                position = ProgressSlider?.Value ?? 0;
            }
            return new PlaybackSnapshot
            {
                trackId = item == null ? "" : WebTrackId(item),
                title = item?.Title ?? "未在播放", artist = item?.Artist ?? "", album = item?.Album ?? "",
                duration = Math.Max(0, duration), position = Math.Max(0, position),
                playing = IsPlaybackActuallyPlaying(), volume = GetVolumePublic(),
                volumeEnabled = !IsHiFiModeSelected() || IsHiFiSoftVolumeUiUnlocked(),
                favorite = IsCurrentFavoritePublic(),
                playbackOrder = _orderResolver.Order.ToString(),
                desktopLyrics = _desktopLyricsEnabled,
                desktopLyricsLocked = _desktopLyricsWindow?.IsLocked == true,
                taskbarPlayer = _taskbarPlayer != null,
                coverUrl = item == null ? "" : "https://celeste-cover.internal/cover/" + WebTrackId(item),
                quality = item?.FormatInfoLine ?? "",
                source = item == null ? "" : item.IsRemote ? "NETWORK" : "LOCAL",
                trackNumber = item?.Track ?? 0,
                shuffle = _orderResolver.Order == PlaybackOrder.Random,
                repeat = _orderResolver.Order is PlaybackOrder.ListLoop or PlaybackOrder.TrackLoop,
                outputMode = _audioEngine?.CurrentOutputMode?.ToString() ?? "",
                outputDevice = _audioEngine?.OutputDeviceName ?? "",
                busy = _startPlaybackInFlight,
                error = _audioEngine?.LastError ?? ""
            };
        }
    }
}
