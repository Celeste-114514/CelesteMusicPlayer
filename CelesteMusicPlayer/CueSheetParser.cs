using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CelesteMusicPlayer
{
    public sealed class CueTrack
    {
        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        public TimeSpan StartTime { get; set; }

        public string FilePath { get; set; } = string.Empty;
    }

    public static class CueSheetParser
    {
        private static readonly Regex FileRegex = new(
            @"^FILE\s+""(?<path>.+)""\s+(?<type>\S+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TrackRegex = new(
            @"^TRACK\s+(?<num>\d+)\s+(?<type>\S+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex IndexRegex = new(
            @"^INDEX\s+(?<index>\d+)\s+(?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TitleRegex = new(
            @"^TITLE\s+""(?<title>.+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PerformerRegex = new(
            @"^PERFORMER\s+""(?<performer>.+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static List<CueTrack> LoadCue(string cuePath)
        {
            var tracks = new List<CueTrack>();
            if (string.IsNullOrWhiteSpace(cuePath) || !File.Exists(cuePath))
            {
                return tracks;
            }

            string cueDir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? string.Empty;
            string[] lines = File.ReadAllLines(cuePath);

            string? currentFile = null;
            string? sheetPerformer = null;
            string? sheetTitle = null;
            CueTrack? currentTrack = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                Match fileMatch = FileRegex.Match(line);
                if (fileMatch.Success)
                {
                    currentFile = ResolveAudioPath(fileMatch.Groups["path"].Value, cueDir);
                    continue;
                }

                Match performerMatch = PerformerRegex.Match(line);
                if (performerMatch.Success)
                {
                    if (currentTrack == null)
                    {
                        sheetPerformer = performerMatch.Groups["performer"].Value;
                    }
                    else
                    {
                        currentTrack.Artist = performerMatch.Groups["performer"].Value;
                    }

                    continue;
                }

                Match titleMatch = TitleRegex.Match(line);
                if (titleMatch.Success)
                {
                    if (currentTrack == null)
                    {
                        sheetTitle = titleMatch.Groups["title"].Value;
                    }
                    else
                    {
                        currentTrack.Title = titleMatch.Groups["title"].Value;
                    }

                    continue;
                }

                Match trackMatch = TrackRegex.Match(line);
                if (trackMatch.Success)
                {
                    if (currentTrack != null)
                    {
                        FinalizeTrack(currentTrack, currentFile, sheetPerformer, sheetTitle);
                        tracks.Add(currentTrack);
                    }

                    currentTrack = new CueTrack();
                    continue;
                }

                if (currentTrack != null)
                {
                    Match indexMatch = IndexRegex.Match(line);
                    if (indexMatch.Success
                        && indexMatch.Groups["index"].Value == "01"
                        && TryParseCueTime(indexMatch.Groups["time"].Value, out TimeSpan start))
                    {
                        currentTrack.StartTime = start;
                    }
                }
            }

            if (currentTrack != null)
            {
                FinalizeTrack(currentTrack, currentFile, sheetPerformer, sheetTitle);
                tracks.Add(currentTrack);
            }

            return tracks;
        }

        /// <summary>
        /// 取音频文件同目录同名 .cue（"album.flac" → "album.cue"），解析后只保留
        /// 引用该音频的轨，按开始时间排序。这是主流抓轨工具（EAC / CUERipper / foobar）
        /// 的标准命名，覆盖绝大多数情况。
        /// 没有 .cue、解析失败、或引用轨不足 2 条时返回 null（调用方按整曲处理）。
        /// </summary>
        public static List<CueTrack>? LoadCueForAudio(string audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return null;
            }

            try
            {
                string cuePath = Path.ChangeExtension(audioPath, ".cue");
                if (!File.Exists(cuePath))
                {
                    return null;
                }

                List<CueTrack> all = LoadCue(cuePath);
                if (all.Count < 2)
                {
                    return null; // 只有一轨的 cue 没有拆分意义
                }

                var mine = all
                    .Where(t => !string.IsNullOrWhiteSpace(t.FilePath)
                        && string.Equals(t.FilePath, audioPath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.StartTime)
                    .ToList();

                return mine.Count >= 2 ? mine : null;
            }
            catch
            {
                return null;
            }
        }

        private static void FinalizeTrack(CueTrack track, string? audioFile, string? sheetPerformer, string? sheetTitle)
        {
            if (string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(sheetPerformer))
            {
                track.Artist = sheetPerformer;
            }

            if (string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(sheetTitle))
            {
                track.Title = sheetTitle;
            }

            if (!string.IsNullOrWhiteSpace(audioFile))
            {
                track.FilePath = audioFile;
            }
        }

        private static string ResolveAudioPath(string relativeOrAbsolute, string cueDir)
        {
            if (Path.IsPathRooted(relativeOrAbsolute))
            {
                return Path.GetFullPath(relativeOrAbsolute);
            }

            return Path.GetFullPath(Path.Combine(cueDir, relativeOrAbsolute));
        }

        private static bool TryParseCueTime(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            string[] parts = value.Split(':');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int frames))
            {
                return false;
            }

            double totalSeconds = minutes * 60 + seconds + frames / 75.0;
            time = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }
    }
}
