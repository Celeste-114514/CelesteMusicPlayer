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

        /// <summary>返回 "格式 · 采样率 · 位深 · 码率 · 声道"；读取失败返回 null。</summary>
        public static string? Format(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                string ext = (Path.GetExtension(path)?.TrimStart('.').Trim() ?? "未知").ToUpperInvariant();
                int rate = 0, bits = 0, kbps = 0, channels = 0;

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

                // TagLib 读不全时用 ffmpeg 兜底（位深/码率/声道）
                if (bits <= 0 || kbps <= 0)
                {
                    ProbeWithFfmpeg(path, ref rate, ref bits, ref kbps, ref channels);
                }

                var parts = new System.Collections.Generic.List<string>();
                parts.Add(ext);
                if (rate > 0)
                {
                    parts.Add(FormatSampleRate(rate));
                }

                if (bits > 0)
                {
                    parts.Add(bits + " bit");
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

        private static string FormatSampleRate(int rate)
        {
            if (rate >= 1000)
            {
                double khz = rate / 1000.0;
                return khz.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz";
            }

            return rate + " Hz";
        }

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
