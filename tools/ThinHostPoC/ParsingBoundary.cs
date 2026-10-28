using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer.ThinHost;

/// <summary>
/// W6 第三步：歌词/CUE/M3U 解析链边界自检。
/// 全部只写自检专属临时目录，不碰用户真实文件；ParseLrc 纯内存。
/// </summary>
internal static class ParsingBoundary
{
    internal sealed record Result(bool Passed, int Checks, IReadOnlyList<string> Details);

    internal static Result Run()
    {
        var details = new List<string>();
        int checks = 0;
        string tempRoot = Path.Combine(Path.GetTempPath(), "CelestePocParsingBoundary-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            // ---- 1) CUE 解析：合成 cue + 占位音频文件 ----
            string cuePath = Path.Combine(tempRoot, "poc.cue");
            string audioName = "poc-audio.flac";
            File.WriteAllText(Path.Combine(tempRoot, audioName), "placeholder");
            File.WriteAllLines(cuePath, new[]
            {
                "TITLE \"测试专辑\"",
                "PERFORMER \"测试艺术家\"",
                $"FILE \"{audioName}\" WAVE",
                "  TRACK 01 AUDIO",
                "    TITLE \"第一轨\"",
                "    PERFORMER \"甲\"",
                "    INDEX 01 00:00:00",
                "  TRACK 02 AUDIO",
                "    TITLE \"第二轨\"",
                "    INDEX 01 03:30:00"
            });
            var tracks = CueSheetParser.LoadCue(cuePath);
            Check(details, ref checks, "cue.count", tracks.Count == 2);
            Check(details, ref checks, "cue.first",
                tracks.Count > 0 && tracks[0].Title == "第一轨" && tracks[0].Artist == "甲"
                && tracks[0].StartTime == TimeSpan.Zero
                && tracks[0].FilePath.EndsWith(audioName, StringComparison.OrdinalIgnoreCase),
                tracks.Count > 0 ? $"路径={tracks[0].FilePath}" : null);
            Check(details, ref checks, "cue.second",
                tracks.Count > 1 && tracks[1].Title == "第二轨" && tracks[1].StartTime == TimeSpan.FromMinutes(3.5)
                && tracks[1].Artist == "测试艺术家",
                "第二轨无 PERFORMER，继承整表艺术家");
            Check(details, ref checks, "cue.missing", CueSheetParser.LoadCue(Path.Combine(tempRoot, "nope.cue")).Count == 0,
                "cue 文件不存在 → 空列表不抛");

            // ---- 2) M3U8 往返 + EXTINF 解析 ----
            string m3uPath = Path.Combine(tempRoot, "poc.m3u8");
            string songA = Path.Combine(tempRoot, "a.flac");
            string songB = Path.Combine(tempRoot, "b.flac");
            File.WriteAllText(songA, "x"); File.WriteAllText(songB, "x");
            M3uPlaylistIO.WriteM3u8(m3uPath, new[] { songA, songB },
                new List<(string, string, string, double)> { (songA, "曲A", "艺A", 213.5), (songB, "曲B", "艺B", 45) });
            var back = M3uPlaylistIO.Parse(m3uPath, existingOnly: false);
            Check(details, ref checks, "m3u.roundtrip", back.Count == 2 && back[0] == songA && back[1] == songB);
            Check(details, ref checks, "m3u.extinf",
                M3uPlaylistIO.TryParseExtInf("#EXTINF:213,foo - bar", out double dur, out string title) && dur == 213 && title == "foo - bar");
            Check(details, ref checks, "m3u.badextinf", !M3uPlaylistIO.TryParseExtInf("garbage line", out _, out _));

            // ---- 3) LRC 纯文本解析（内存，不读盘）----
            var lines = LyricsLoader.ParseLrc("[00:01.50]第一句\n[00:10.00]第二句\n不是时间行");
            Check(details, ref checks, "lrc.count", lines.Count == 2);
            Check(details, ref checks, "lrc.times",
                lines.Count == 2 && lines[0].Time == TimeSpan.FromSeconds(1.5) && lines[0].Text == "第一句"
                && lines[1].Time == TimeSpan.FromSeconds(10.0) && lines[1].Text == "第二句");
            var empty = LyricsLoader.ParseLrc("");
            Check(details, ref checks, "lrc.empty", empty.Count == 0, "空内容 → 空列表不抛");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* 尽力清理 */ }
        }

        bool passed = details.All(d => !d.Contains("= FAIL", StringComparison.Ordinal));
        return new Result(passed, checks, details);
    }

    private static void Check(List<string> details, ref int checks, string name, bool ok, string? note = null)
    {
        checks++;
        details.Add($"{name} = {(ok ? "OK" : "FAIL")}{(note == null ? "" : "（" + note + "）")}");
    }
}
