using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace CelesteMusicPlayer
{
    /// <summary>音频格式信息格式化（HiFi 显示：编码器 / 位深 / 采样率 / 码率 / 声道）。</summary>
    public static class AudioInfoFormatter
    {
        // 每个文件只解析一次并缓存（含 ffmpeg 兜底开销昂贵的那些格式），避免列表/启动反复读盘与反复起进程。
        private readonly record struct AudioInfoData(int Rate, int Bits, int Kbps, int Channels, string Codec);

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
                if (!TryGetPartsCached(path, out int rate, out int bits, out int kbps, out int channels, out string codec))
                {
                    return null;
                }

                string encoder = ResolveCodecName(codec, Path.GetExtension(path), rate);
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
        /// 状态条第三行短格式信息："Free Lossless Audio Codec · 16bit/44.1kHz · 1411kbps"。
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
                if (!TryGetPartsCached(path, out int rate, out int bits, out int kbps, out _, out string codec))
                {
                    return string.Empty;
                }

                string encoder = ResolveCodecName(codec, Path.GetExtension(path), rate);
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
        /// 歌曲面板第三行的格式胶囊内容（每段一个胶囊）：编码器 / 位深·采样率 / 比特率。
        /// 如 ["Free Lossless Audio Codec","16bit/44.1kHz","1411kbps"]；读取失败返回空列表。
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
                if (!TryGetPartsCached(path, out int rate, out int bits, out int kbps, out _, out string codec))
                {
                    return result;
                }

                string encoder = ResolveCodecName(codec, Path.GetExtension(path), rate);
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
        /// 编码器 + 位深/采样率质量行，如 "MPEG-4 ALAC · 16bit/44.1kHz"、"Free Lossless Audio Codec · 24bit/96kHz"、"DSD128 · 2.82MHz"。
        /// 编码器按真实音频编码器（ffmpeg 探测，m4a 能区分 AAC / ALAC）显示规范名；读取失败返回空串。
        /// </summary>
        public static string FormatQualityLine(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            try
            {
                if (!TryGetPartsCached(path, out int rate, out int bits, out int kbps, out _, out string codec))
                {
                    return string.Empty;
                }

                string encoder = ResolveCodecName(codec, Path.GetExtension(path), rate);
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

        /// <summary>
        /// 把真实音频编码器（ffmpeg 报的 codec 名，或容器/扩展名推断）映射成用户可读的「编码器名」。
        /// 优先用 ffmpeg 探到的真实编码器；探不到时退回按容器猜测（对 flac/mp3/wav 这类单义容器仍然准确）。
        /// 关键：显示的是编码器（codec）而不是容器格式 —— 比如 m4a 既可能是 AAC 也可能是 ALAC，
        /// 只有看编码器才分得清；ffmpeg 报的 "pcm_s16le" 之类带后缀的名字不会原样显示（统一成 "Linear PCM"）。
        /// </summary>
        private static string ResolveCodecName(string codec, string ext, int rate)
        {
            if (!string.IsNullOrEmpty(codec))
            {
                string? mapped = MapCodecName(codec, rate);
                if (mapped != null)
                {
                    return mapped;
                }
            }

            return MapExtensionName(ext, rate);
        }

        /// <summary>ffmpeg 报的编码器 token（如 alac / flac / aac / mp3 / pcm_s16le / dsd_lsbf）→ 规范显示名。</summary>
        private static string? MapCodecName(string codec, int rate)
        {
            string c = codec.Trim().ToLowerInvariant();
            if (c.Length == 0)
            {
                return null;
            }

            // DSD 系列：按采样率给 DSD64/128/256/512
            if (c.StartsWith("dsd"))
            {
                return ResolveDsdName(rate);
            }

            // PCM 系列（pcm_s16le / pcm_f32le / fltp 等）：统一叫 Linear PCM，不显示 ffmpeg 后缀
            if (c.StartsWith("pcm") || c is "flt" or "fltp")
            {
                return "Linear PCM";
            }

            if (CodecDisplayNames.TryGetValue(c, out string? name) && name != null)
            {
                return name;
            }

            // 不认识的编码器：不臆造，退回按容器名显示
            return null;
        }

        /// <summary>常见 ffmpeg 编码器 token → 规范显示名（与用户给的样式一致）。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> CodecDisplayNames =
            new(System.StringComparer.OrdinalIgnoreCase)
            {
                ["alac"] = "MPEG-4 ALAC",
                ["flac"] = "Free Lossless Audio Codec",
                ["mp3"] = "MPEG 1 Layer III",
                ["mp2"] = "MPEG 1 Layer II",
                ["aac"] = "MPEG-4 AAC",
                ["ac3"] = "Dolby Digital (AC-3)",
                ["eac3"] = "Dolby Digital Plus",
                ["opus"] = "Opus",
                ["vorbis"] = "Vorbis",
                ["dts"] = "DTS",
                ["truehd"] = "Dolby TrueHD",
                ["mlp"] = "Meridian Lossless Packing",
                ["ape"] = "Monkey's Audio",
                ["wavpack"] = "WavPack",
                ["tta"] = "True Audio",
                ["tak"] = "TAK",
                ["mpc"] = "Musepack",
                ["musepack"] = "Musepack",
                ["wmav1"] = "Windows Media Audio",
                ["wmav2"] = "Windows Media Audio",
                ["amr_nb"] = "AMR-NB",
                ["amr_wb"] = "AMR-WB",
                ["speex"] = "Speex",
                ["cook"] = "Cook",
                ["atrac1"] = "ATRAC1",
                ["atrac3"] = "ATRAC3",
                ["atrac3p"] = "ATRAC3+",
                ["ilbc"] = "iLBC",
                ["g722"] = "G.722",
                ["g726"] = "G.726",
                ["sbc"] = "SBC",
                ["aptx"] = "aptX",
            };

        /// <summary>按容器/扩展名推断的编码器显示名（ffmpeg 探不到时的兜底，单义容器仍然准确）。</summary>
        private static string MapExtensionName(string ext, int rate)
        {
            ext = (ext?.TrimStart('.').Trim() ?? "").ToLowerInvariant();
            switch (ext)
            {
                case "flac":
                    return "Free Lossless Audio Codec";
                case "mp3":
                    return "MPEG 1 Layer III";
                case "mp2":
                    return "MPEG 1 Layer II";
                case "alac":
                    return "MPEG-4 ALAC";
                // MPEG-4 家族是「AAC 或 ALAC」二义容器：尽量用 ffmpeg 探真实编码器；
                // 探不到时保守显示 MPEG-4 ALAC（最佳猜测，不臆造）。
                case "m4a":
                case "mp4":
                case "m4b":
                case "m4v":
                case "mov":
                case "3gp":
                    return "MPEG-4 ALAC";
                case "wav":
                case "wave":
                    return "Linear PCM";
                case "aac":
                    return "MPEG-4 AAC";
                case "ape":
                    return "Monkey's Audio";
                case "ogg":
                    return "Vorbis";
                case "opus":
                    return "Opus";
                case "dsf":
                case "dff":
                    return ResolveDsdName(rate);
                case "wv":
                    return "WavPack";
                case "tta":
                    return "True Audio";
                case "tak":
                    return "TAK";
                case "mpc":
                    return "Musepack";
                case "wma":
                    return "Windows Media Audio";
                case "mka":
                    return "Matroska Audio";
                case "caf":
                    return "Core Audio Format";
                case "aif":
                case "aiff":
                    return "AIFF";
                default:
                    return string.IsNullOrEmpty(ext) ? string.Empty : ext.ToUpperInvariant();
            }
        }

        /// <summary>这些容器里「编码器」与「扩展名」未必一致（m4a 可能是 AAC 也可能是 ALAC），
        /// 必须靠 ffmpeg 真正探测，不能只按扩展名猜。</summary>
        private static bool IsCodecAmbiguousContainer(string ext)
        {
            switch (ext)
            {
                case "m4a":
                case "mp4":
                case "m4b":
                case "m4v":
                case "mov":
                case "3gp":
                case "mka":
                case "ogg":
                    return true;
                default:
                    return false;
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
        private static bool TryGetPartsCached(string path, out int rate, out int bits, out int kbps, out int channels, out string codec)
        {
            rate = 0;
            bits = 0;
            kbps = 0;
            channels = 0;
            codec = string.Empty;
            if (InfoCache.TryGetValue(path, out AudioInfoData hit))
            {
                rate = hit.Rate;
                bits = hit.Bits;
                kbps = hit.Kbps;
                channels = hit.Channels;
                codec = hit.Codec;
                return true;
            }

            if (!ProbePartsUncached(path, out int r, out int b, out int k, out int c, out string cd))
            {
                return false;
            }

            if (InfoCache.Count >= InfoCacheMax)
            {
                InfoCache.Clear();
            }

            InfoCache[path] = new AudioInfoData(r, b, k, c, cd);
            rate = r;
            bits = b;
            kbps = k;
            channels = c;
            codec = cd;
            return true;
        }

        /// <summary>读取采样率/位深/码率/声道/编码器；TagLib 读不全或容器二义时用 ffmpeg 兜底。</summary>
        private static bool ProbePartsUncached(string path, out int rate, out int bits, out int kbps, out int channels, out string codec)
        {
            rate = 0;
            bits = 0;
            kbps = 0;
            channels = 0;
            codec = string.Empty;
            string ext = (Path.GetExtension(path)?.TrimStart('.').Trim() ?? "").ToLowerInvariant();
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
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioInfoFormatter.cs", caught); }

                // 需要起 ffmpeg 的两种情况：
                //  1) TagLib 没读全（位深/码率缺失）—— 与原逻辑一致；
                //  2) 容器二义（m4a/mp4/mka/ogg 等），必须探真实编码器才能分清 AAC/ALAC 等。
                if (bits <= 0 || kbps <= 0 || IsCodecAmbiguousContainer(ext))
                {
                    ProbeWithFfmpeg(path, ref rate, ref bits, ref kbps, ref channels, ref codec);
                }

                // 不做"有损一律 16bit"这类按容器/类别的钳制——那会把真实信息抹掉：
                // WMA Lossless、DTS-HD、E-AC3 等有损容器里同样可以装 24bit 有效精度，
                // 32bit float 的 WAV/FLAC 真实位深就是 32。位深一律以探测到的真实值为准，
                // 探测不到就不显示（见 ParseBitDepth：未知返回 0，由调用方省略该字段）。

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把采样率格式化成 HiFi 播放器常见的写法：44100→"44.1kHz"、96000→"96kHz"、2822400→"2.82MHz"。</summary>
        /// <summary>采样率是否可能是真实值。真实音频最低 8kHz（DSD 更高），
        /// TagLib 偶尔返回 1/0 这类脏值，不能拿去显示，否则会出现"1Hz"。</summary>
        private static bool IsPlausibleSampleRate(int rate) => rate >= 8000;

        private static string FormatSampleRate(int rate)
        {
            if (!IsPlausibleSampleRate(rate))
            {
                return string.Empty; // 脏值宁可不显示，也不显示成"1Hz"
            }

            if (rate >= 1_000_000)
            {
                return TrimTrailingZero(rate / 1_000_000.0) + "MHz";
            }

            if (rate >= 1000)
            {
                return TrimTrailingZero(rate / 1000.0) + "kHz";
            }

            return rate + "Hz";
        }

        private static string TrimTrailingZero(double v)
            => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        private static string FormatChannels(int channels)
        {
            return channels switch
            {
                1 => "单声道",
                2 => "双声道",
                _ => channels + " 声道"
            };
        }

        /// <summary>用内置 ffmpeg -i 输出解析采样率/位深/码率/声道/编码器（robust 于 TagLib 读不到的格式）。</summary>
        private static void ProbeWithFfmpeg(string path, ref int rate, ref int bits, ref int kbps, ref int channels, ref string codec)
        {
            string? ffmpeg = AudioPlaybackEngine.FindFfmpeg();
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

                    // 编码器：Audio: 之后第一个 token（如 alac / aac / mp3 / pcm_s16le / dsd_lsbf）
                    if (string.IsNullOrEmpty(codec))
                    {
                        Match mc = Regex.Match(line, @"Audio:\s*([a-zA-Z0-9_]+)");
                        if (mc.Success)
                        {
                            codec = mc.Groups[1].Value.ToLowerInvariant();
                        }
                    }

                    // 码率： 例如 "128 kb/s"（有的音频行内不含，需另找）
                    Match mb = Regex.Match(line, @"(\d+)\s*kb/s");
                    if (mb.Success && kbps <= 0)
                    {
                        kbps = int.Parse(mb.Groups[1].Value);
                    }

                    // 采样率
                    // 注意判据不是 rate <= 0：TagLib 对少数 ALAC/m4a 会返回 1 这种「>0 但绝无可能」
                    // 的值（用户实机：192kHz 的曲子显示成 1Hz），必须按"是否可能是真值"判断，
                    // 否则 ffmpeg 探到的真实采样率会被这个脏值挡住、永远用不上。
                    Match mr = Regex.Match(line, @"(\d+)\s*Hz");
                    if (mr.Success && !IsPlausibleSampleRate(rate))
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

                    // 位深：只认探测到的真实值，探测不到就留 0（调用方省略该字段）。
                    // 判定优先级与理由见 ParseBitDepth。
                    if (bits <= 0)
                    {
                        int parsed = ParseBitDepth(line, codec);
                        if (parsed > 0)
                        {
                            bits = parsed;
                        }
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioInfoFormatter.cs", caught); }
        }

        /// <summary>从 ffmpeg 的 "Stream #…: Audio: …" 行解析位深；拿不到真实值时返回 0（调用方省略该字段）。
        /// 这是音频播放器，规格显示必须对得起用户——**能拿到真实值就用真实值，宁可空着也不虚标**。
        /// 判定优先级：
        ///  1) 括号声明 <c>(24 bit)</c> —— ffmpeg 对整数打包格式给出的**权威有效位深**。
        ///     24bit 的 FLAC/ALAC 报成 "s32 (24 bit)"：32 只是容器位宽，24 才是真实精度。
        ///     （旧实现直接匹配 s32，把 24bit 无损虚标成 32bit，2026-09-22 修。）
        ///  2) 整数采样格式 s16/s24/s32/u8 及其平面变体 —— 位深与之一一对应。
        ///  3) 浮点 fltp/flt/dbl —— 这是**解码器内部格式**，真实位深要看 codec 是什么：
        ///     · pcm_f32le / pcm_f64le：文件本身就是浮点存储 → 真实 32 / 64bit float；
        ///     · flac（float 样本）→ 32；
        ///     · mp3 / aac / vorbis / opus 等有损编码：ISO 规范里解码输出就是 16bit PCM，
        ///       ffmpeg 内部用 float 只是它的实现方式，不代表源是浮点 → 16bit（这是真实值，不是猜测）；
        ///     · dsd_*：1bit 脉冲密度调制 → 1bit。
        ///  4) 未知 codec 或没匹配上 → 0，不显示。
        /// **不做"有损一律 16bit"这类按容器/类别的钳制**：WMA Lossless、DTS-HD、E-AC3
        /// 这类有损容器里同样可以装 24bit 有效精度，一刀切会把真实信息抹掉。</summary>
        private static int ParseBitDepth(string line, string codec)
        {
            // 1) 权威有效位深声明
            Match mp = Regex.Match(line, @"\((\d+)\s*bit\)");
            if (mp.Success && int.TryParse(mp.Groups[1].Value, out int declared) && declared > 0)
            {
                return declared;
            }

            // 2) 整数采样格式（s16p 必须排在 s16 之前，否则只匹配到前缀）
            Match mi = Regex.Match(line, @"\b(s16p|s24p|s32p|s08p|u16p|u24p|u32p|u08p|s16|s24|s32|s08|u16|u24|u32|u08)\b");
            if (mi.Success)
            {
                return mi.Value switch
                {
                    "s08" or "s08p" or "u08" or "u08p" => 8,
                    "s16" or "s16p" or "u16" or "u16p" => 16,
                    "s24" or "s24p" or "u24" or "u24p" => 24,
                    _ => 32
                };
            }

            // 3) 浮点 / DSD：真实位深取决于 codec，而不是"有损还是无损"
            bool isFloat = Regex.IsMatch(line, @"\b(fltp?|dblp?|dbl)\b");
            bool isDsd = codec.StartsWith("dsd", StringComparison.Ordinal);
            if (!isFloat && !isDsd)
            {
                return 0;
            }

            return codec switch
            {
                "pcm_f32le" or "pcm_f32be" => 32,
                "pcm_f64le" or "pcm_f64be" => 64,
                "flac" => 32,                       // FLAC 支持 float 样本；整数样本走分支 1/2
                "dsd_lsbf" or "dsd_msbf"
                    or "dsd_lsbf_planar" or "dsd_msbf_planar" => 1,
                // ISO/IEC 规范下，这些有损编码的解码输出就是 16bit PCM
                "mp3" or "mp1" or "mp2" or "mp3adu" or "mp3on4"
                    or "aac" or "aac_latm" or "vorbis" or "opus"
                    or "musepack" or "mpc" or "mpc7" or "mpc8"
                    or "wma" or "wmav1" or "wmav2"
                    or "atrac3" or "atrac3p" or "cook" or "speex" => 16,
                _ => 0                              // 未知：不显示，也不猜
            };
        }

    }
}
