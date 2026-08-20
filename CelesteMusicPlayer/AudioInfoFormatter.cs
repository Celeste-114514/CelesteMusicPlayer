using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace CelesteMusicPlayer
{
    /// <summary>音频格式信息格式化（HiFi 显示：采样率 / 位深 / 码率 / 声道）。</summary>
    public static class AudioInfoFormatter
    {
        private static readonly string[] FfmpegCandidates =
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        };

        // 每个文件只解析一次并缓存（含 ffmpeg 兜底开销昂贵的那些格式），避免列表/启动反复读盘与反复起进程。
        private readonly record struct AudioInfoData(int Rate, int Bits, int Kbps, int Channels);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AudioInfoData> InfoCache =
            new(System.StringComparer.OrdinalIgnoreCase);

        private static readonly int InfoCacheMax = 8192;

        /// <summary>返回 "编码器 · 位深/采样率 · 码率 · 声道"；读取失败返回 null。</summary>
        public static string? Format(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                if (!TryGetPartsCached(path, out int rate, out int bits, out int kbps, out int channels))
                {
                    return null;
                }

                string encoder = ResolveEncoderName(path, rate);
                string bitsPart = bits > 0 ? bits + "bit" : string.Empty;
                string ratePart = rate > 0 ? FormatSampleRate(rate) : string.Empty;
                string bitDepth = string.Empty;
                if (bitsPart.Length > 0 && ratePart.Length > 0)
                {
                    bitDepth = bitsPart + "/" + ratePart;
                }
                else if (bitsPart.Length > 0)
                {
                    bitDepth = bitsPart;
                }
                else if (ratePart.Length > 0)
                {
                    bitDepth = ratePart;
                }

                var parts = new System.Collections.Generic.List<string> { encoder };
                if (bitDepth.Length > 0)
                {
                    parts.Add(bitDepth);
                }

                if (kbps > 0)
                {
                    parts.Add(kbps + " kbps");
                }

                if (channels > 0)
                {
                    parts.Add(FormatChannels(channels));
                }

                return parts.Count > 1 ? string.Join(" · ", parts) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 状态条第三行短格式信息："ALAC · 16bit/44kHz · 1411kbps"。
        /// 读取失败返回空串（调用侧据此隐藏该行）。
        /// </summary>
        public static string FormatShortLine(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            try
            {
                if (!TryGetPartsCached(path, out int rate, out int bits, out int kbps, out _))
                {
                    return string.Empty;
                }

                string encoder = ResolveEncoderName(path, rate);
                string bitsPart = bits > 0 ? bits + "bit" : string.Empty;
                string ratePart = rate > 0 ? FormatSampleRate(rate) : string.Empty;
                string bitDepth = string.Empty;
                if (bitsPart.Length > 0 && ratePart.Length > 0)
                {
                    bitDepth = bitsPart + "/" + ratePart;
                }
                else if (bitsPart.Length > 0)
                {
                    bitDepth = bitsPart;
                }
                else if (ratePart.Length > 0)
                {
                    bitDepth = ratePart;
                }

                string kbpsPart = kbps > 0 ? kbps + "kbps" : string.Empty;

                var parts = new System.Collections.Generic.List<string> { encoder };
                if (bitDepth.Length > 0)
                {
                    parts.Add(bitDepth);
                }

                if (kbpsPart.Length > 0)
                {
                    parts.Add(kbpsPart);
                }

                return string.Join(" · ", parts);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 歌曲面板第三行的格式胶囊内容（每段一个胶囊）：格式 / 位深·采样率 / 比特率。
        /// 如 ["FLAC","16bit/44kHz","1411kbps"]；读取失败返回空列表。
        /// </summary>
        public static System.Collections.Generic.IReadOnlyList<string> FormatChips(string path)
        {
            var result = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return result;
            }

            try
            {
                if (!TryGetPartsCached(path, out int rate, out int bits, out int kbps, out _))
                {
                    return result;
                }

                string encoder = ResolveEncoderName(path, rate);
                if (encoder.Length > 0)
                {
                    result.Add(encoder);
                }

                string bitsPart = bits > 0 ? bits + "bit" : string.Empty;
                string ratePart = rate > 0 ? FormatSampleRate(rate) : string.Empty;
                string bitDepth = string.Empty;
                if (bitsPart.Length > 0 && ratePart.Length > 0)
                {
                    bitDepth = bitsPart + "/" + ratePart;
                }
                else if (bitsPart.Length > 0)
                {
                    bitDepth = bitsPart;
                }
                else if (ratePart.Length > 0)
                {
                    bitDepth = ratePart;
                }

                if (bitDepth.Length > 0)
                {
                    result.Add(bitDepth);
                }

                if (kbps > 0)
                {
                    result.Add(kbps + "kbps");
                }

                return result;
            }
            catch
            {
                return result;
            }
        }

        /// <summary>
        /// 编码器 + 位深/采样率质量行，如 "MPEG-4 ALAC · 16bit/44kHz"、"FLAC · 24bit/96kHz"、"DSD128 · 2.82MHz"。
        /// 编码器按容器/扩展名推断（M4A→MPEG-4 ALAC、DSD→DSD64/128/256等）；读取失败返回空串。
        /// </summary>
        public static string FormatQualityLine(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            try
            {
                if (!TryGetPartsCached(path, out int rate, out int bits, out int kbps, out _))
                {
                    return string.Empty;
                }

                string encoder = ResolveEncoderName(path, rate);
                string bitsPart = bits > 0 ? bits + "bit" : string.Empty;
                string ratePart = rate > 0 ? FormatSampleRate(rate) : string.Empty;
                string depth = string.Empty;
                if (bitsPart.Length > 0 && ratePart.Length > 0)
                {
                    depth = bitsPart + "/" + ratePart;
                }
                else if (bitsPart.Length > 0)
                {
                    depth = bitsPart;
                }
                else if (ratePart.Length > 0)
                {
                    depth = ratePart;
                }

                var parts = new System.Collections.Generic.List<string>();
                if (encoder.Length > 0)
                {
                    parts.Add(encoder);
                }

                if (depth.Length > 0)
                {
                    parts.Add(depth);
                }

                return string.Join(" · ", parts);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>根据容器/扩展名 + 采样率推断编码器显示名。</summary>
        private static string ResolveEncoderName(string path, int rate)
        {
            string ext = (Path.GetExtension(path)?.TrimStart('.').Trim() ?? "").ToLowerInvariant();
            switch (ext)
            {
                case "m4a":
                case "mp4":
                    return "MPEG-4 ALAC";
                case "alac":
                    return "ALAC";
                case "flac":
                    return "FLAC";
                case "mp3":
                    return "MP3";
                case "wav":
                    return "WAV/PCM";
                case "aac":
                    return "AAC";
                case "ape":
                    return "APE";
                case "ogg":
                case "opus":
                    return "Ogg/Opus";
                case "dsf":
                case "dff":
                    return ResolveDsdName(rate);
                default:
                    return string.IsNullOrEmpty(ext) ? string.Empty : ext.ToUpperInvariant();
            }
        }

        private static string ResolveDsdName(int rate)
        {
            // DSD 位深率常为 44100/48000 的 64/128/256 倍；部分工具报 PCM 采样率(352800/705600/1411200)
            double ratio441 = rate > 0 ? rate / 44100.0 : 0;
            double ratio48 = rate > 0 ? rate / 48000.0 : 0;
            double ratio = Math.Abs(ratio441 - Math.Round(ratio441)) < 0.02 ? ratio441 : ratio48;
            long n = (long)Math.Round(ratio);
            if (n >= 512)
            {
                return "DSD512";
            }

            if (n >= 256)
            {
                return "DSD256";
            }

            if (n >= 128)
            {
                return "DSD128";
            }

            if (n >= 64)
            {
                return "DSD64";
            }

            return "DSD";
        }

        /// <summary>带缓存的读取：每个路径只解析一次（含 ffmpeg 兜底），随后命中缓存。</summary>
        private static bool TryGetPartsCached(string path, out int rate, out int bits, out int kbps, out int channels)
        {
            rate = 0;
            bits = 0;
            kbps = 0;
            channels = 0;
            if (InfoCache.TryGetValue(path, out AudioInfoData hit))
            {
                rate = hit.Rate;
                bits = hit.Bits;
                kbps = hit.Kbps;
                channels = hit.Channels;
                return true;
            }

            if (!ProbePartsUncached(path, out int r, out int b, out int k, out int c))
            {
                return false;
            }

            if (InfoCache.Count >= InfoCacheMax)
            {
                InfoCache.Clear();
            }

            InfoCache[path] = new AudioInfoData(r, b, k, c);
            rate = r;
            bits = b;
            kbps = k;
            channels = c;
            return true;
        }

        /// <summary>读取采样率/位深/码率/声道；TagLib 读不全时用 ffmpeg 兜底。</summary>
        private static bool ProbePartsUncached(string path, out int rate, out int bits, out int kbps, out int channels)
        {
            rate = 0;
            bits = 0;
            kbps = 0;
            channels = 0;
            try
            {
                try
                {
                    using TagLib.File file = TagLib.File.Create(path);
                    var pr = file.Properties;
                    if (pr != null)
                    {
                        rate = pr.AudioSampleRate;
                        bits = pr.BitsPerSample;
                        kbps = pr.AudioBitrate;
                    }
                }
                catch
                {
                }

                if (bits <= 0 || kbps <= 0)
                {
                    ProbeWithFfmpeg(path, ref rate, ref bits, ref kbps, ref channels);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatSampleRate(int rate)
            => rate + "hz";

        private static string FormatChannels(int channels)
        {
            return channels switch
            {
                1 => "单声道",
                2 => "双声道",
                _ => channels + " 声道"
            };
        }

        /// <summary>用内置 ffmpeg -i 输出解析采样率/位深/码率/声道（robust 于 TagLib 读不到的格式）。</summary>
        private static void ProbeWithFfmpeg(string path, ref int rate, ref int bits, ref int kbps, ref int channels)
        {
            string? ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
            {
                return;
            }

            try
            {
                var psi = new ProcessStartInfo(ffmpeg)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(path);

                using Process proc = Process.Start(psi);
                string err = proc.StandardError.ReadToEnd();
                foreach (string raw in err.Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.Contains("Audio:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // 码率： 例如 "128 kb/s"（有的音频行内不含，需另找）
                    Match mb = Regex.Match(line, @"(\d+)\s*kb/s");
                    if (mb.Success && kbps <= 0)
                    {
                        kbps = int.Parse(mb.Groups[1].Value);
                    }

                    // 采样率
                    Match mr = Regex.Match(line, @"(\d+)\s*Hz");
                    if (mr.Success && rate <= 0)
                    {
                        rate = int.Parse(mr.Groups[1].Value);
                    }

                    // 声道： steroe/5.1/mono
                    Match mch = Regex.Match(line, @"\b(stereo|mono|5\.1|7\.1|quad)\b");
                    if (mch.Success && channels <= 0)
                    {
                        channels = mch.Value.ToLowerInvariant() switch
                        {
                            "mono" => 1,
                            "stereo" => 2,
                            "quad" => 4,
                            "5.1" => 6,
                            "7.1" => 8,
                            _ => 0
                        };
                    }

                    // 位深： s16 / s32 / flt (f32) / s24 （flac 显示为 s16/s24/s32）
                    Match mbp = Regex.Match(line, @"(s16|s24|s32|s08|flt)\b");
                    if (mbp.Success && bits <= 0)
                    {
                        bits = mbp.Value switch
                        {
                            "s08" => 8,
                            "s16" => 16,
                            "s24" => 24,
                            "s32" or "flt" => 32,
                            _ => 0
                        };
                    }
                }
            }
            catch
            {
            }
        }

        private static string? FindFfmpeg()
        {
            foreach (string c in FfmpegCandidates)
            {
                if (File.Exists(c))
                {
                    return c;
                }
            }

            return null;
        }
    }
}
