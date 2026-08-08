using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CelesteMusicPlayer
{
    public static class M3uPlaylistIO
    {
        private static readonly Regex ExtInfRegex = new(
            @"#EXTINF:(?<duration>-?\d+(?:\.\d+)?)\s*(?<meta>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static List<string> Parse(string playlistPath, bool existingOnly = true)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(playlistPath) || !File.Exists(playlistPath))
            {
                return results;
            }

            string playlistDir = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? string.Empty;
            string[] lines = File.ReadAllLines(playlistPath, DetectEncoding(playlistPath));

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                string resolved = ResolvePath(line, playlistDir);
                if (!existingOnly || File.Exists(resolved))
                {
                    results.Add(resolved);
                }
            }

            return results;
        }

        public static void WriteM3u8(string playlistPath, IEnumerable<string> filePaths, IEnumerable<(string Path, string Title, string Artist, double DurationSeconds)>? entries = null)
        {
            if (string.IsNullOrWhiteSpace(playlistPath))
            {
                throw new ArgumentException("Playlist path is required.", nameof(playlistPath));
            }

            string? dir = Path.GetDirectoryName(playlistPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("#EXTM3U");

            if (entries != null)
            {
                foreach ((string path, string title, string artist, double durationSeconds) in entries)
                {
                    string display = string.IsNullOrWhiteSpace(artist)
                        ? title
                        : $"{artist} - {title}";
                    int duration = (int)Math.Max(0, durationSeconds);
                    sb.AppendLine($"#EXTINF:{duration},{display}");
                    sb.AppendLine(ToRelativeOrAbsolute(path, dir ?? string.Empty));
                }
            }
            else
            {
                foreach (string path in filePaths)
                {
                    sb.AppendLine("#EXTINF:-1,");
                    sb.AppendLine(ToRelativeOrAbsolute(path, dir ?? string.Empty));
                }
            }

            File.WriteAllText(playlistPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static string ResolvePath(string entry, string playlistDir)
        {
            if (Path.IsPathRooted(entry))
            {
                return Path.GetFullPath(entry);
            }

            return Path.GetFullPath(Path.Combine(playlistDir, entry));
        }

        private static string ToRelativeOrAbsolute(string filePath, string playlistDir)
        {
            string full = Path.GetFullPath(filePath);
            if (!string.IsNullOrEmpty(playlistDir)
                && full.StartsWith(playlistDir, StringComparison.OrdinalIgnoreCase))
            {
                return full[playlistDir.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return full;
        }

        private static Encoding DetectEncoding(string path)
        {
            byte[] bom = new byte[4];
            using FileStream fs = File.OpenRead(path);
            int read = fs.Read(bom, 0, bom.Length);
            if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            {
                return Encoding.UTF8;
            }

            return Encoding.Default;
        }

        /// <summary>解析 #EXTINF 元数据行（供调用方扩展用）。</summary>
        public static bool TryParseExtInf(string line, out double durationSeconds, out string displayTitle)
        {
            durationSeconds = -1;
            displayTitle = string.Empty;
            Match match = ExtInfRegex.Match(line.Trim());
            if (!match.Success)
            {
                return false;
            }

            _ = double.TryParse(match.Groups["duration"].Value, out durationSeconds);
            displayTitle = match.Groups["meta"].Value.Trim();
            return true;
        }
    }
}
