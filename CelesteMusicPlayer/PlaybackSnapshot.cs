namespace CelesteMusicPlayer
{
    // Immutable wire contract shared by playback views. Camel-case names retain
    // the existing JSON protocol, independent of serializer naming policies.
    internal sealed record PlaybackSnapshot
    {
        public string trackId { get; init; } = "";
        public string title { get; init; } = "未在播放";
        public string artist { get; init; } = "";
        public string album { get; init; } = "";
        public double duration { get; init; }
        public double position { get; init; }
        public bool playing { get; init; }
        public double volume { get; init; }
        public bool volumeEnabled { get; init; }
        public bool favorite { get; init; }
        public string playbackOrder { get; init; } = "";
        public bool desktopLyrics { get; init; }
        public bool desktopLyricsLocked { get; init; }
        public string coverUrl { get; init; } = "";
        public string quality { get; init; } = "";
        public string source { get; init; } = "";
        public uint trackNumber { get; init; }
        public bool shuffle { get; init; }
        public bool repeat { get; init; }
        public string outputMode { get; init; } = "";
        public string outputDevice { get; init; } = "";
        public bool busy { get; init; }
        public string error { get; init; } = "";
    }

    internal interface IPlaybackStateSource
    {
        PlaybackSnapshot Capture();
    }
}
