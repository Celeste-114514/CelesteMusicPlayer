using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CelesteMusicPlayer;
using Xunit;

namespace TagEditTests;

public class WebDspTagRulesTests
{
    [Theory]
    [InlineData("flat")][InlineData("pop")][InlineData("rock")][InlineData("jazz")]
    [InlineData("classical")][InlineData("bass")][InlineData("soft")][InlineData("simple_bass")]
    public void PresetsAlignWithUnchangedAxis(string id)
    {
        var state = EqCurveState.CreatePreset(id);
        Assert.Equal(32d, state.Bands[0].FrequencyHz);
        Assert.Equal(64d, state.Bands[1].FrequencyHz);
    }

    [Fact]
    public void LegacyTenBandCustomMigratesWithoutChangingGain()
    {
        var state = EqCurveState.Default(); state.PresetId = "custom";
        state.Bands[0].FrequencyHz = 31; state.Bands[1].FrequencyHz = 62;
        state.Bands[0].GainDb = 3.5; state.Normalize();
        Assert.Equal(32d, state.Bands[0].FrequencyHz); Assert.Equal(64d, state.Bands[1].FrequencyHz);
        Assert.Equal(3.5, state.Bands[0].GainDb);
    }

    [Fact]
    public void ImportedAndArbitraryCustomFrequenciesArePreserved()
    {
        var state = EqCurveState.Default(); state.PresetId = "opra-test";
        state.Bands[0].FrequencyHz = 31; state.Bands[1].FrequencyHz = 62; state.Normalize();
        Assert.Equal(31d, state.Bands[0].FrequencyHz); Assert.Equal(62d, state.Bands[1].FrequencyHz);
        state.PresetId = "custom"; state.Bands[2].FrequencyHz = 130; state.Normalize();
        Assert.Equal(31d, state.Bands[0].FrequencyHz); Assert.Equal(130d, state.Bands[2].FrequencyHz);
    }

    private static KeyValuePair<string, PlaylistItem>[] Sort(string rules, KeyValuePair<string, PlaylistItem>[] rows, Func<string,string,object>? read = null)
    {
        using var json = JsonDocument.Parse("{\"sortRules\":" + rules + "}");
        var method = typeof(EqCurveState).Assembly.GetType("CelesteMusicPlayer.WebTagSortRules", true)!.GetMethod("Sort", BindingFlags.Static | BindingFlags.NonPublic)!;
        read ??= (_,_) => throw new Exception("Library-only sort must not read files");
        return ((IEnumerable<KeyValuePair<string, PlaylistItem>>)method.Invoke(null, new object[] { rows, json.RootElement, read })!).ToArray();
    }
    private static KeyValuePair<string, PlaylistItem> Row(string id, string artist, uint track) => new(id, new PlaylistItem { Artist=artist, Track=track, FilePath=id+".flac" });

    [Fact]
    public void MultiKeySortIsNumericAndStableAndClearRestoresAddedOrder()
    {
        var rows = new[] { Row("a","A",2),Row("b","A",10),Row("c","B",1),Row("d","A",2) };
        Assert.Equal(new[] { "b","a","d","c" }, Sort("[{\"key\":\"artist\"},{\"key\":\"track\",\"descending\":true}]",rows).Select(r=>r.Key));
        Assert.Equal(rows.Select(r=>r.Key), Sort("[]", rows).Select(r=>r.Key));
    }

    [Fact]
    public void FileOnlySortReadsWholeWorkspaceBeforePaging()
    {
        var rows = new[] { Row("a","A",1),Row("b","A",2),Row("c","A",3) }; int reads=0;
        var sorted = Sort("[{\"key\":\"bpm\"}]",rows,(id,_)=>{reads++;return new { bpm=id=="a"?120:id=="b"?90:100 };});
        Assert.Equal(3,reads); Assert.Equal(new[] { "b","c","a" },sorted.Select(r=>r.Key));
        Assert.Equal("b",sorted.Take(1).Single().Key);
    }
}
