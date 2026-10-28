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

    /// <summary>播放顺序 / 循环方式。</summary>
    /// <remarks>
    /// 2026-10-28 W6：这个枚举原先定义在 <c>MainWindow.xaml.cs</c> 里，而设置存储要引用它 ——
    /// 等于"读一个设置就得把整个主窗口拖进来"。移到这个不依赖界面的文件后，
    /// 设置读写对非 WinUI 宿主也可用。命名空间没变，所有调用点都不用改。
    /// </remarks>
    public enum PlaybackOrder
    {
        /// <summary>顺序播放：按列表顺序播完即停</summary>
        Sequential,
        /// <summary>随机播放：每首结束后随机选下一首</summary>
        Random,
        /// <summary>列表循环：按列表顺序循环</summary>
        ListLoop,
        /// <summary>单曲循环：重复当前曲目</summary>
        TrackLoop,
        /// <summary>单曲播放：播完当前曲目后停止</summary>
        TrackOnce
    }
}
