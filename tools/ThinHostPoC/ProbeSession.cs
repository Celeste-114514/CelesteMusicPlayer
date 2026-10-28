namespace CelesteMusicPlayer.ThinHost;

// W5 deliberately uses a fake session. These are the actual W1 contracts, not a
// second copy of their command/state semantics and not a second audio engine.
internal sealed class ProbeSession : IPlaybackStateSource
{
    private bool _playing;
    private int _track = 1;
    internal int Toggles { get; private set; }
    internal PlaybackCommandRouter Commands { get; }
    internal ProbeSession() => Commands = new(() => _playing,
        () => _track=Math.Max(1,_track-1), () => { _playing=!_playing; Toggles++; }, () => _track++);
    public PlaybackSnapshot Capture() => new()
    {
        trackId="poc-"+_track,title="模拟曲目 " + _track,artist="W5 验证会话 · 无实际音频",
        playing=_playing,position=38,duration=100,volume=50,volumeEnabled=true,trackNumber=(uint)_track,
        source="POC",playbackOrder="Sequential",outputMode="Simulation"
    };
    internal object Handle(string action) => action switch
    {
        "snapshot" => Capture(),
        "play" or "pause" or "togglePlay" or "next" or "previous" => Execute(action),
        _ => throw new ArgumentException("Unknown PoC action")
    };
    private object Execute(string action) { Commands.TryExecute(action); return Capture(); }
}
