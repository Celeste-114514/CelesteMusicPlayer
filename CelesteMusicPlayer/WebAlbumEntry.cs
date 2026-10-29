using System;
namespace CelesteMusicPlayer;
internal sealed class WebAlbumEntry
        {
            public string id { get; init; } = "";
            public string title { get; init; } = "";
            public string sourceId { get; init; } = "";
            public string artist { get; init; } = "";
            public uint year { get; init; }
            public int added { get; init; }
            public int trackCount { get; init; }
            public double duration { get; init; }
            public string coverUrl { get; init; } = "";
            public string[] trackIds { get; init; } = Array.Empty<string>();
        }
