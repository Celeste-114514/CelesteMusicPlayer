using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CelesteMusicPlayer
{
    /// <summary>一行带时间戳的歌词</summary>
    public sealed class LyricLine
    {
        public TimeSpan Time { get; init; }

        public string Text { get; init; } = string.Empty;

        /// <summary>逐字开始时间（可选）。长度与 Text 的 UTF-16 长度一致；null 表示无逐字数据。</summary>
        public IReadOnlyList<TimeSpan>? CharTimes { get; init; }

        /// <summary>是否翻译行（对照歌词里紧随原文的译文）。翻译行不高亮主题色，仅作辅助展示。</summary>
        public bool IsTranslation { get; init; }
    }

    /// <summary>解析同名 .lrc / 内嵌纯文本歌词（尊重设置：优先内嵌、歌词文件夹、模糊匹配、隐藏空行）。</summary>
    public static class LyricsLoader
    {
        public static List<LyricLine> LoadForAudio(string audioPath)
        {
            AppSettingsState settings = AppSettingsStore.Load();
            var lines = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return lines;
            }

            if (settings.PreferInnerLyric)
            {
                lines = TryLoadEmbedded(audioPath);
                if (lines.Count > 0)
                {
                    return PostProcess(lines, settings);
                }
            }

            string? lrcPath = FindLrcPath(audioPath, settings);
            if (lrcPath != null)
            {
                try
                {
                    string text = File.ReadAllText(lrcPath, DetectEncoding(lrcPath));
                    lines = ParseLrc(text);
                    if (lines.Count > 0)
                    {
                        return PostProcess(lines, settings);
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("LyricsLoader.cs", caught); }
            }

            if (!settings.PreferInnerLyric)
            {
                lines = TryLoadEmbedded(audioPath);
            }

            return PostProcess(lines, settings);
        }

        private static List<LyricLine> PostProcess(List<LyricLine> lines, AppSettingsState settings)
        {
            if (settings.HideBlankLyricLines)
            {
                lines = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
            }

            return lines;
        }

        private static List<LyricLine> TryLoadEmbedded(string audioPath)
        {
            var lines = new List<LyricLine>();
            try
            {
                using TagLib.File tagFile = TagLib.File.Create(audioPath);
                string? embedded = tagFile.Tag.Lyrics;
                if (string.IsNullOrWhiteSpace(embedded))
                {
                    return lines;
                }

                // 若内嵌本身是 LRC，直接解析
                if (embedded.Contains('[') && Regex.IsMatch(embedded, @"\[\d{1,2}:\d{1,2}"))
                {
                    return ParseLrc(embedded);
                }

                string[] raw = embedded.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToArray();

                TimeSpan duration = tagFile.Properties.Duration;
                if (duration <= TimeSpan.Zero)
                {
                    duration = TimeSpan.FromSeconds(Math.Max(raw.Length, 1) * 4);
                }

                for (int i = 0; i < raw.Length; i++)
                {
                    double t = duration.TotalSeconds * i / Math.Max(raw.Length, 1);
                    lines.Add(new LyricLine
                    {
                        Time = TimeSpan.FromSeconds(t),
                        Text = raw[i]
                    });
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("LyricsLoader.cs", caught); }

            return lines;
        }

        private static string? FindLrcPath(string audioPath, AppSettingsState settings)
        {
            string dir = Path.GetDirectoryName(audioPath) ?? "";
            string name = Path.GetFileNameWithoutExtension(audioPath);
            var candidates = new List<string>
            {
                Path.Combine(dir, name + ".lrc"),
                Path.Combine(dir, name + ".LRC"),
                Path.ChangeExtension(audioPath, ".lrc"),
                Path.ChangeExtension(audioPath, ".LRC")
            };

            if (!string.IsNullOrWhiteSpace(settings.LyricFolder) && Directory.Exists(settings.LyricFolder))
            {
                candidates.Add(Path.Combine(settings.LyricFolder, name + ".lrc"));
                candidates.Add(Path.Combine(settings.LyricFolder, name + ".LRC"));
            }

            foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            if (settings.LyricFuzzyMatch)
            {
                string? fuzzy = FuzzyFindInDir(dir, name);
                if (fuzzy != null)
                {
                    return fuzzy;
                }

                if (!string.IsNullOrWhiteSpace(settings.LyricFolder) && Directory.Exists(settings.LyricFolder))
                {
                    return FuzzyFindInDir(settings.LyricFolder, name);
                }
            }

            return null;
        }

        private static string? FuzzyFindInDir(string dir, string baseName)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "*.lrc");
                string key = NormalizeKey(baseName);
                foreach (string file in files)
                {
                    string n = NormalizeKey(Path.GetFileNameWithoutExtension(file));
                    if (n.Contains(key, StringComparison.OrdinalIgnoreCase)
                        || key.Contains(n, StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("LyricsLoader.cs", caught); }

            return null;
        }

        private static string NormalizeKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            return Regex.Replace(s, @"[\s_\-\[\]\(\)]+", "").ToLowerInvariant();
        }

        public static List<LyricLine> ParseLrc(string content)
        {
            var result = new List<LyricLine>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return result;
            }

            int offsetMs = 0;
            Match offsetMatch = Regex.Match(content, @"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase);
            if (offsetMatch.Success
                && int.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedOffset))
            {
                offsetMs = parsedOffset;
            }

            foreach (string rawLine in content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("[ti:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("[ar:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("[al:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("[by:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                MatchCollection matches = Regex.Matches(line, @"\[(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?\]");
                if (matches.Count == 0)
                {
                    continue;
                }

                int last = matches[^1].Index + matches[^1].Length;
                string text = last < line.Length ? line[last..].Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                // 逐字内联时间戳："歌<00:12.40>词<00:12.50>文"（网易云逐字歌词格式）
                (string plainText, IReadOnlyList<TimeSpan>? charTimes) = SplitCharTimes(text);
                if (string.IsNullOrWhiteSpace(plainText))
                {
                    continue;
                }

                foreach (Match m in matches)
                {
                    if (!int.TryParse(m.Groups[1].Value, out int min) ||
                        !int.TryParse(m.Groups[2].Value, out int sec))
                    {
                        continue;
                    }

                    int ms = 0;
                    if (m.Groups[3].Success)
                    {
                        string frac = m.Groups[3].Value;
                        if (frac.Length == 1)
                        {
                            ms = int.Parse(frac, CultureInfo.InvariantCulture) * 100;
                        }
                        else if (frac.Length == 2)
                        {
                            ms = int.Parse(frac, CultureInfo.InvariantCulture) * 10;
                        }
                        else
                        {
                            ms = int.Parse(frac[..Math.Min(3, frac.Length)], CultureInfo.InvariantCulture);
                        }
                    }

                    TimeSpan time = new TimeSpan(0, 0, min, sec, ms) - TimeSpan.FromMilliseconds(offsetMs);
                    if (time < TimeSpan.Zero)
                    {
                        time = TimeSpan.Zero;
                    }

                    result.Add(new LyricLine
                    {
                        Time = time,
                        Text = plainText,
                        CharTimes = charTimes == null
                            ? null
                            : charTimes.Select(t => t + time).ToArray()
                    });
                }
            }

            var ordered = result
                .OrderBy(l => l.Time)
                .ToList();
            return MarkTranslations(ordered);
        }

        /// <summary>标记对照歌词的翻译行：相邻两行时间差很小（≤0.35s）且都非空时，把后一行视为译文（IsTranslation），
        /// 使滚动歌词高亮停在原文行、翻译行仅作辅助展示。</summary>
        private static List<LyricLine> MarkTranslations(List<LyricLine> lines)
        {
            if (lines.Count < 2)
            {
                return lines;
            }

            var res = new List<LyricLine>(lines.Count);
            bool prevIsTranslation = false;
            for (int i = 0; i < lines.Count; i++)
            {
                LyricLine cur = lines[i];
                bool isTranslation = false;
                if (i > 0 && !string.IsNullOrWhiteSpace(cur.Text))
                {
                    LyricLine prev = lines[i - 1];
                    double gapMs = (cur.Time - prev.Time).TotalMilliseconds;
                    if (!string.IsNullOrWhiteSpace(prev.Text)
                        && gapMs >= -5 && gapMs <= 350
                        && !prevIsTranslation)
                    {
                        isTranslation = true; // 后一行紧随前一行 → 视为译文
                    }
                }

                res.Add(new LyricLine
                {
                    Time = cur.Time,
                    Text = cur.Text,
                    CharTimes = cur.CharTimes,
                    IsTranslation = isTranslation
                });
                prevIsTranslation = isTranslation;
            }

            return res;
        }

        /// <summary>解析逐字内联时间戳："歌&lt;00:12.40&gt;词&lt;00:12.50&gt;文"。
        /// 返回纯文本与相对行首的逐字开始时间（无内联时间戳时 CharTimes 为 null）。</summary>
        private static (string Text, IReadOnlyList<TimeSpan>? CharTimes) SplitCharTimes(string raw)
        {
            if (!raw.Contains('<') || !raw.Contains('>'))
            {
                return (raw, null);
            }

            var sb = new StringBuilder();
            var times = new List<TimeSpan>();
            TimeSpan current = TimeSpan.Zero;
            bool hasInline = false;
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '<')
                {
                    int close = raw.IndexOf('>', i + 1);
                    if (close > i + 1)
                    {
                        string inner = raw.Substring(i + 1, close - i - 1);
                        Match tm = Regex.Match(inner, @"^(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?$");
                        if (tm.Success)
                        {
                            int min = int.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture);
                            int sec = int.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture);
                            int ms = 0;
                            if (tm.Groups[3].Success)
                            {
                                string frac = tm.Groups[3].Value;
                                if (frac.Length == 1)
                                {
                                    ms = int.Parse(frac, CultureInfo.InvariantCulture) * 100;
                                }
                                else if (frac.Length == 2)
                                {
                                    ms = int.Parse(frac, CultureInfo.InvariantCulture) * 10;
                                }
                                else
                                {
                                    ms = int.Parse(frac[..Math.Min(3, frac.Length)], CultureInfo.InvariantCulture);
                                }
                            }

                            current = new TimeSpan(0, 0, min, sec, ms);
                            hasInline = true;
                            i = close + 1;
                            continue;
                        }
                    }
                }

                sb.Append(raw[i]);
                times.Add(current);
                i++;
            }

            string text = sb.ToString();
            if (!hasInline || times.Count != text.Length)
            {
                return (text, null);
            }

            return (text, times);
        }

        private static Encoding DetectEncoding(string path)
        {
            try
            {
                byte[] bom = new byte[3];
                using FileStream fs = File.OpenRead(path);
                int read = fs.Read(bom, 0, 3);
                if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                {
                    return new UTF8Encoding(true);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("LyricsLoader.cs", caught); }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        }
    }
}
