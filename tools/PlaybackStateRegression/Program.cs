using System;
using System.Linq;
using System.Text.Json;
using CelesteMusicPlayer;

static void Check(bool value, string note) { if (!value) throw new Exception(note); }
var first = new PlaybackSnapshot { trackId="track-1", title="Breathe", artist="Pink Floyd", album="Moon", duration=169.05, position=21.6, playing=true, volume=88, volumeEnabled=false, favorite=true, playbackOrder="Random", desktopLyrics=true, desktopLyricsLocked=false, taskbarPlayer=true, coverUrl="https://celeste-cover.internal/cover/track-1", quality="DSD64", source="LOCAL", trackNumber=2, shuffle=true, repeat=false, outputMode="WasapiExclusive", outputDevice="DAC", busy=false, error="" };
// Freeze the pre-extraction contract independently of the new model's defaults.
const string keys="trackId,title,artist,album,duration,position,playing,volume,volumeEnabled,favorite,playbackOrder,desktopLyrics,desktopLyricsLocked,taskbarPlayer,coverUrl,quality,source,trackNumber,shuffle,repeat,outputMode,outputDevice,busy,error";
using var json=JsonDocument.Parse(JsonSerializer.Serialize(first));
Check(json.RootElement.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(keys.Split(',').Order()),"Wire keys changed");
Check(json.RootElement.GetProperty("trackNumber").GetUInt32()==2 && json.RootElement.GetProperty("volume").GetDouble()==88,"Numeric wire types changed");
Check(json.RootElement.GetProperty("playing").GetBoolean() && !json.RootElement.GetProperty("volumeEnabled").GetBoolean(),"Direct-output state changed");
Check(json.RootElement.GetProperty("taskbarPlayer").GetBoolean(), "Taskbar ON state was lost");
var source = new Source(first);
IPlaybackStateSource web=source, taskbar=source;
var before=web.Capture();source.Current=first with { trackId="track-2", title="狗尿馆", position=0, playing=false, busy=true };
Check(taskbar.Capture().trackId=="track-2" && web.Capture().busy,"Views did not observe the same source");
Check(before.trackId=="track-1" && before.position==21.6 && before.playing,"Old snapshot was mutated by a track change");
var restored=JsonSerializer.Deserialize<PlaybackSnapshot>(JsonSerializer.Serialize(source.Current));
Check(restored==source.Current,"Snapshot serialization lost data");
Check(new PlaybackSnapshot().title=="未在播放" && new PlaybackSnapshot().trackId=="","Empty playback state changed");
Console.WriteLine("PASS: existing JSON keys/types, immutable track transitions, shared source, roundtrip and empty state");
sealed class Source(PlaybackSnapshot initial) : IPlaybackStateSource
{
 public PlaybackSnapshot Current { get; set; }=initial;
 public PlaybackSnapshot Capture()=>Current;
}
