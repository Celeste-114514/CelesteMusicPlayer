using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TagLib;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// ReplayGain 响度归一化服务。
    /// 取值顺序：本地缓存 → 文件内嵌 ReplayGain 标签（TXXX / VorbisComment / APE）→ ffmpeg ebur128 实测计算。
    /// 参考响度为 -18 LUFS（ReplayGain 2.0 常见参考），并做峰值防削波。
    /// </summary>
    internal static class ReplayGainService
    {
        private const double ReferenceLufs = -18.0;
        private const double HeadroomDb = -0.2;

        private static readonly object CacheGate = new();
        private static Dictionary<string, CacheEntry>? _cache;

        private sealed class CacheEntry
        {
            public long MtimeTicks { get; set; }
            public long Size { get; set; }
            public double GainDb { get; set; }
            public double Peak { get; set; }
        }

        private static string CachePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch
                {
                }

                return Path.Combine(dir, "replaygain-cache.json");
            }
        }

        /// <summary>同步快路径：缓存或内嵌标签（不启动 ffmpeg）。</summary>
        public static (double GainDb, double Peak)? TryGetQuick(string path)
        {
            if (TryGetCached(path, out CacheEntry entry))
            {
                return (entry.GainDb, entry.Peak);
            }

            return ReadTagGain(path);
        }

        /// <summary>dB 增益 + 峰值 → 音量线性倍率（含防削波）。</summary>
        public static double GainToScale(double gainDb, double peak)
        {
            double safe = gainDb;
            if (peak > 0.0001)
            {
                double peakDb = 20.0 * Math.Log10(peak);
                if (safe + peakDb > HeadroomDb)
                {
                    safe = HeadroomDb - peakDb;
                }
            }

            return Math.Pow(10.0, safe / 20.0);
        }

        /// <summary>用内置 ffmpeg 的 ebur128 滤镜计算整曲集成响度与峰值。</summary>
        public static async Task<(double GainDb, double Peak)?> ComputeWithFfmpegAsync(
            string path,
            string? ffmpegPath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !System.IO.File.Exists(ffmpegPath) || !System.IO.File.Exists(path))
            {
                return null;
            }

            try
            {
                var psi = new ProcessStartInfo(ffmpegPath)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-nostdin");
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(path);
                psi.ArgumentList.Add("-af");
                psi.ArgumentList.Add("ebur128=peak=true");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("null");
                psi.ArgumentList.Add("-");

                using Process proc = Process.Start(psi)!;
                string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                try
                {
                    await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    throw;
                }

                double? integrated = null;
                double? peakDb = null;
                foreach (string rawLine in stderr.Split('\n'))
                {
                    string line = rawLine.Trim();
                    Match mI = Regex.Match(line, @"I:\s*(-?[\d.]+)\s+LUFS");
                    if (mI.Success)
                    {
                        double v = double.Parse(mI.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (v > -70.0) // 过滤 -inf / 静音
                        {
                            integrated = v;
                        }
                    }

                    Match mP = Regex.Match(line, @"Peak:\s*(-?[\d.]+)\s+dBFS");
                    if (mP.Success)
                    {
                        peakDb = double.Parse(mP.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                }

                if (integrated == null)
                {
                    return null;
                }

                double gain = ReferenceLufs - integrated.Value;
                double peak = peakDb.HasValue ? Math.Pow(10.0, peakDb.Value / 20.0) : 1.0;
                if (peakDb.HasValue && gain + peakDb.Value > HeadroomDb)
                {
                    gain = HeadroomDb - peakDb.Value;
                }

                return (gain, peak);
            }
            catch
            {
                return null;
            }
        }

        public static void Cache(string path, double gainDb, double peak)
        {
            try
            {
                lock (CacheGate)
                {
                    Dictionary<string, CacheEntry> map = LoadCacheLocked();
                    System.IO.FileInfo fi = new(path);
                    map[path] = new CacheEntry
                    {
                        MtimeTicks = fi.LastWriteTimeUtc.Ticks,
                        Size = fi.Length,
                        GainDb = gainDb,
                        Peak = peak
                    };
                    SaveCacheLocked(map);
                }
            }
            catch
            {
            }
        }

        private static bool TryGetCached(string path, out CacheEntry entry)
        {
            entry = null!;
            try
            {
                lock (CacheGate)
                {
                    Dictionary<string, CacheEntry> map = LoadCacheLocked();
                    if (map.TryGetValue(path, out CacheEntry? cached))
                    {
                        FileInfo fi = new(path);
                        if (cached.MtimeTicks == fi.LastWriteTimeUtc.Ticks && cached.Size == fi.Length)
                        {
                            entry = cached;
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static Dictionary<string, CacheEntry> LoadCacheLocked()
        {
            if (_cache != null)
            {
                return _cache;
            }

            try
            {
                if (System.IO.File.Exists(CachePath))
                {
                    string json = System.IO.File.ReadAllText(CachePath);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json)
                             ?? new Dictionary<string, CacheEntry>();
                    return _cache;
                }
            }
            catch
            {
            }

            _cache = new Dictionary<string, CacheEntry>();
            return _cache;
        }

        private static void SaveCacheLocked(Dictionary<string, CacheEntry> map)
        {
            _cache = map;
            try
            {
                System.IO.File.WriteAllText(CachePath, JsonSerializer.Serialize(map));
            }
            catch
            {
            }
        }

        /// <summary>读取文件内嵌 ReplayGain 标签（优先 FLAC/OGG 的 VorbisComment，其次 APE、ID3v2）。</summary>
        private static (double GainDb, double Peak)? ReadTagGain(string path)
        {
            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                {
                    string? g = xiph.GetFirstField("REPLAYGAIN_TRACK_GAIN");
                    string? p = xiph.GetFirstField("REPLAYGAIN_TRACK_PEAK");
                    if (ParseGainPeak(g, p, out (double GainDb, double Peak) r))
                    {
                        return r;
                    }
                }

                if (file.GetTag(TagLib.TagTypes.Ape) is TagLib.Ape.Tag ape)
                {
                    string? g = ape.GetItem("REPLAYGAIN_TRACK_GAIN")?.ToString();
                    string? p = ape.GetItem("REPLAYGAIN_TRACK_PEAK")?.ToString();
                    if (ParseGainPeak(g, p, out (double GainDb, double Peak) r))
                    {
                        return r;
                    }
                }

                if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                {
                    foreach (TagLib.Id3v2.UserTextInformationFrame frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                    {
                        if (frame.Description != null
                            && frame.Description.Equals("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase)
                            && frame.Text != null && frame.Text.Length > 0)
                        {
                            string? peakText = null;
                            foreach (TagLib.Id3v2.UserTextInformationFrame f2 in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                            {
                                if (f2.Description != null
                                    && f2.Description.Equals("REPLAYGAIN_TRACK_PEAK", StringComparison.OrdinalIgnoreCase)
                                    && f2.Text != null && f2.Text.Length > 0)
                                {
                                    peakText = f2.Text[0];
                                    break;
                                }
                            }

                            if (ParseGainPeak(frame.Text[0], peakText, out (double GainDb, double Peak) r))
                            {
                                return r;
                            }

                            break;
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool ParseGainPeak(string? gainText, string? peakText, out (double GainDb, double Peak) result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(gainText))
            {
                return false;
            }

            string g = gainText.Trim().Replace('\u2212', '-').Replace("dB", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!double.TryParse(g, NumberStyles.Float, CultureInfo.InvariantCulture, out double gainDb))
            {
                return false;
            }

            double peak = 1.0;
            if (!string.IsNullOrWhiteSpace(peakText)
                && double.TryParse(peakText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedPeak)
                && parsedPeak > 0 && parsedPeak <= 10.0)
            {
                peak = parsedPeak;
            }

            result = (gainDb, peak);
            return true;
        }
    }
}
