using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 解码后端抽象：把"音频文件 → PCM WAV"这一段从播放引擎里解耦出来。
    /// 输出层（HiFiOutputBackend）只认 WAV，不关心 WAV 由哪个后端产出，
    /// 因此切换后端（FFmpeg / 精简 FFmpeg / …）不影响独占 / ASIO / bit-perfect / DoP / 卷积等 HIFI 能力。
    /// </summary>
    public interface IDecoderBackend
    {
        /// <summary>失败回调：转码失败时由实现方调用，用于把原因上报给引擎（设置 LastError + 触发 Failed 事件）。</summary>
        Action<Exception>? FailureHandler { get; set; }

        /// <summary>该后端能否解码此扩展名（false 时上层应回退到其他后端或提示）。</summary>
        bool CanDecode(string path);

        /// <summary>把文件转码为缓存 WAV，返回缓存路径；失败返回 null（原因经 FailureHandler 上报）。</summary>
        Task<string?> TranscodeToWavAsync(string inputPath, HiFiOutputBackend.OutputMode outputMode, string? devicePreference, Action<string>? status);

        /// <summary>确保已转码为缓存 WAV（供无缝预加载复用）；失败返回 null。</summary>
        Task<string?> EnsureCachedWavAsync(string inputPath, HiFiOutputBackend.OutputMode outputMode, string? devicePreference, Action<string>? status);

        /// <summary>用后端探测源文件真实音轨时长（秒）；失败返回 TimeSpan.Zero。</summary>
        Task<TimeSpan> ProbeSourceDurationAsync(string path);

        /// <summary>执行一次原生 ffmpeg 参数转码（供输出层做设备兼容回退用）。</summary>
        Task<bool> RunFfmpegAsync(string args, Action<string>? status = null);
    }

    /// <summary>
    /// FFmpeg 解码后端（阶段 A / 路线二默认实现）：把"音频文件 → PCM WAV"这一段
    /// 从 AudioPlaybackEngine 抽出来。逻辑与原实现逐字一致，仅做归属迁移，零行为变化。
    /// 失败通过 <see cref="FailureHandler"/> 上报，由引擎转成 LastError + Failed 事件。
    /// </summary>
    internal sealed class FfmpegDecoderBackend : IDecoderBackend
    {
        /// <summary>失败回调：由引擎在构造时挂接，转码失败时用于设置 LastError 并触发 Failed 事件。</summary>
        public Action<Exception>? FailureHandler { get; set; }

        /// <summary>该后端能解码的格式（与 AudioPlaybackEngine.NeedsFfmpeg 一致的全量白名单）。</summary>
        public bool CanDecode(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return Path.GetExtension(path).ToLowerInvariant() is
                ".ape" or ".wv" or ".tta" or ".mpc" or ".tak" or
                ".dsf" or ".dff" or
                ".opus" or ".mp2" or ".amr" or ".au" or ".cda" or ".mod" or ".s3m" or ".xm";
        }

        public async Task<string?> TranscodeToWavAsync(string path, HiFiOutputBackend.OutputMode outputMode, string? devicePreference, Action<string>? status)
        {
            string? ffmpeg = AudioPlaybackEngine.FindFfmpeg();
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                FailureHandler?.Invoke(new Exception("未找到内置 ffmpeg.exe"));
                return null;
            }

            if (!File.Exists(path))
            {
                FailureHandler?.Invoke(new Exception("文件不存在：" + path));
                return null;
            }

            string cacheDir = GetCacheDir();
            string partial = Path.Combine(cacheDir, Guid.NewGuid().ToString("N") + ".partial.wav");
            string transcodeArgs = BuildTranscodeArgs(path, partial, outputMode, devicePreference);
            string key = GetCacheKey(path, transcodeArgs);
            string cachedWav = Path.Combine(cacheDir, key + ".wav");
            string targetWav;

            if (File.Exists(cachedWav))
            {
                targetWav = cachedWav;
                status?.Invoke("正在播放（已缓存）…");
            }
            else
            {
                Directory.CreateDirectory(cacheDir);
                // 注意：临时文件必须以 .wav 结尾，ffmpeg 靠扩展名判断输出格式
                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    Arguments = transcodeArgs
                };

                try
                {
                    double totalSeconds = 0;
                    int lastPct = -1;
                    var errLines = new List<string>();
                    int exitCode;
                    using (Process proc = Process.Start(psi)!)
                    {
                        // 转码很耗 CPU；降为低优先级，避免抢占 WASAPI 独占（Pro Audio）渲染线程造成播放卡顿
                        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
                        proc.ErrorDataReceived += (_, e) =>
                        {
                            if (e.Data == null)
                            {
                                return;
                            }

                            errLines.Add(e.Data);
                            if (errLines.Count > 80)
                            {
                                errLines.RemoveAt(0);
                            }

                            if (totalSeconds <= 1 && e.Data.Contains("Duration:", StringComparison.Ordinal))
                            {
                                totalSeconds = ParseDurationSeconds(e.Data);
                            }

                            int pct = ParseProgressPercent(e.Data, totalSeconds);
                            if (pct >= 0 && pct != lastPct)
                            {
                                lastPct = pct;
                                status?.Invoke($"正在用 FFmpeg 转码… {pct}%");
                            }
                        };
                        proc.BeginErrorReadLine();
                        await proc.WaitForExitAsync();
                        exitCode = proc.ExitCode;
                    }

                    if (exitCode != 0 || !File.Exists(partial))
                    {
                        string detail = FirstError(string.Join("\n", errLines));
                        FailureHandler?.Invoke(new Exception("FFmpeg 转码失败：" + (string.IsNullOrWhiteSpace(detail) ? "未知原因" : detail)));
                        try
                        {
                            File.Delete(partial);
                        }
                        catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }

                        return null;
                    }

                    try
                    {
                        File.Move(partial, cachedWav);
                    }
                    catch
                    {
                        try
                        {
                            File.Delete(partial);
                        }
                        catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
                    }

                    targetWav = cachedWav;
                    TrimCache(cacheDir);
                }
                catch (Exception ex)
                {
                    FailureHandler?.Invoke(ex);
                    return null;
                }
            }

            return targetWav;
        }

        public async Task<string?> EnsureCachedWavAsync(string path, HiFiOutputBackend.OutputMode outputMode, string? devicePreference, Action<string>? status)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                string cacheDir = GetCacheDir();
                string partial = Path.Combine(cacheDir, Guid.NewGuid().ToString("N") + ".partial.wav");
                string transcodeArgs = BuildTranscodeArgs(path, partial, outputMode, devicePreference);
                string key = GetCacheKey(path, transcodeArgs);
                string cachedWav = Path.Combine(cacheDir, key + ".wav");
                if (File.Exists(cachedWav))
                {
                    return cachedWav;
                }

                Directory.CreateDirectory(cacheDir);
                if (!await RunFfmpegAsync(transcodeArgs, status))
                {
                    return null;
                }

                if (!File.Exists(partial))
                {
                    return null;
                }

                try
                {
                    File.Move(partial, cachedWav);
                }
                catch
                {
                    try
                    {
                        File.Delete(partial);
                    }
                    catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
                }

                TrimCache(cacheDir);
                return cachedWav;
            }
            catch
            {
                return null;
            }
        }

        public async Task<TimeSpan> ProbeSourceDurationAsync(string path)
        {
            try
            {
                string? ffmpeg = AudioPlaybackEngine.FindFfmpeg();
                if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(path))
                {
                    return TimeSpan.Zero;
                }

                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    Arguments = "-i \"" + path + "\""
                };

                using (Process proc = Process.Start(psi)!)
                {
                    string stderr = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    foreach (var line in stderr.Split('\n'))
                    {
                        double sec = ParseDurationSeconds(line);
                        if (sec > 0)
                        {
                            return TimeSpan.FromSeconds(sec);
                        }
                    }
                }
            }
            catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }

            return TimeSpan.Zero;
        }

        public async Task<bool> RunFfmpegAsync(string args, Action<string>? status = null)
        {
            string? ffmpeg = AudioPlaybackEngine.FindFfmpeg();
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                FailureHandler?.Invoke(new Exception("未找到内置 ffmpeg.exe"));
                return false;
            }

            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                Arguments = args
            };

            var errLines = new List<string>();
            double totalSeconds = 0;
            int lastPct = -1;

            try
            {
                using (Process proc = Process.Start(psi)!)
                {
                    // 转码很耗 CPU；降为低优先级，避免抢占 WASAPI 独占（Pro Audio）渲染线程造成播放卡顿
                    try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
                    proc.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data == null)
                        {
                            return;
                        }

                        errLines.Add(e.Data);
                        if (errLines.Count > 80)
                        {
                            errLines.RemoveAt(0);
                        }

                        if (totalSeconds <= 1 && e.Data.Contains("Duration:", StringComparison.Ordinal))
                        {
                            totalSeconds = ParseDurationSeconds(e.Data);
                        }

                        int pct = ParseProgressPercent(e.Data, totalSeconds);
                        if (pct >= 0 && pct != lastPct)
                        {
                            lastPct = pct;
                            status?.Invoke($"正在用 FFmpeg 转码… {pct}%");
                        }
                    };
                    proc.BeginErrorReadLine();
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode != 0)
                    {
                        string detail = FirstError(string.Join("\n", errLines));
                        FailureHandler?.Invoke(new Exception("FFmpeg 转码失败：" + (string.IsNullOrWhiteSpace(detail) ? "未知原因" : detail)));
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                FailureHandler?.Invoke(ex);
                return false;
            }
        }

        // ---- 以下为原 AudioPlaybackEngine 的私有纯函数，整段迁移，逻辑不变 ----

        /// <summary>构建 ffmpeg 转码参数：按源位深输出原生 PCM（16bit→s16le / 24bit→s24le / 32bit→s32le），
        /// 保留源采样率与声道，供 WaveFileReader 原样直通（严格 bit-perfect）。探测失败回退 16/44.1/2。</summary>
        private string BuildTranscodeArgs(string srcPath, string dstPath, HiFiOutputBackend.OutputMode outputMode, string? devicePreference)
        {
            string ext = Path.GetExtension(srcPath).ToLowerInvariant();
            if (ext is ".dsf" or ".dff")
            {
                // 共享模式（系统混音/共享）：统一折叠为 16bit/44.1kHz PCM，保证设备/系统可播（非 bit-perfect，可听优先）。
                // WASAPI 独占 / ASIO：高质量 PCM，输出 DSD 原生容器率（DSD64→176400Hz、DSD128→352800Hz）。
                if (outputMode == HiFiOutputBackend.OutputMode.WasapiShared)
                {
                    return string.Format("-y -i \"{0}\" -vn -c:a pcm_s16le -ar 44100 -ac 2 \"{1}\"", srcPath, dstPath);
                }

                return string.Format("-y -i \"{0}\" -vn -c:a pcm_s32le -ar 352800 -sample_fmt s32 \"{1}\"", srcPath, dstPath);
            }

            var srcFmt = ProbeSourceFormat(srcPath);
            if (srcFmt is (int rate, int ch, int bits) && rate > 0 && ch > 0)
            {
                // 共享模式：折叠到设备 MixFormat（采样率/声道），输出统一用 pcm_f32le（IEEE float）。
                if (outputMode == HiFiOutputBackend.OutputMode.WasapiShared)
                {
                    var mix = HiFiOutputBackend.GetDeviceMixFormat(devicePreference);
                    if (mix is (int mr, int mc, _, _) && mr > 0 && mc > 0)
                    {
                        return string.Format("-y -i \"{0}\" -vn -c:a pcm_f32le -ar {1} -ac {2} \"{3}\"", srcPath, mr, mc, dstPath);
                    }

                    // 设备 MixFormat 探测失败兜底：固定 48k/2ch float32（系统共享普遍支持）
                    return string.Format("-y -i \"{0}\" -vn -c:a pcm_f32le -ar 48000 -ac 2 \"{1}\"", srcPath, dstPath);
                }

                // 严格按源位深输出（bit-perfect）：16→s16le、24→s24le、32→s32le。
                string enc = bits switch { <= 16 => "pcm_s16le", <= 24 => "pcm_s24le", _ => "pcm_s32le" };
                return string.Format("-y -i \"{0}\" -vn -acodec {1} \"{2}\"", srcPath, enc, dstPath);
            }

            // 探测失败回退：固定 16bit/44.1kHz/立体声，保证可播。
            return string.Format("-y -i \"{0}\" -vn -acodec pcm_s16le -ar 44100 -ac 2 \"{1}\"", srcPath, dstPath);
        }

        /// <summary>用 ffmpeg -i 探测源音频格式，返回 (采样率, 声道数, 位深)；探测失败返回 null。</summary>
        private static (int Rate, int Channels, int Bits)? ProbeSourceFormat(string path)
        {
            try
            {
                string? ffmpeg = AudioPlaybackEngine.FindFfmpeg();
                if (string.IsNullOrWhiteSpace(ffmpeg))
                {
                    return null;
                }

                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    Arguments = "-i \"" + path + "\""
                };

                using var proc = Process.Start(psi)!;
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(3000);

                int rate = 0, channels = 0, bits = 0;
                var mRate = Regex.Match(stderr, @"(\d+)\s*Hz");
                if (mRate.Success)
                {
                    rate = int.Parse(mRate.Groups[1].Value);
                }

                var mCh = Regex.Match(stderr, @"(mono|stereo|2\.1|5\.1|6\.1|7\.1)");
                if (mCh.Success)
                {
                    channels = mCh.Groups[1].Value switch
                    {
                        "mono" => 1,
                        "stereo" => 2,
                        "2.1" => 3,
                        "5.1" => 6,
                        "6.1" => 7,
                        "7.1" => 8,
                        _ => 0
                    };
                }

                // 位深优先取 "NN bits"（24bit 源 ffmpeg 会显示 s32, 24 bits），没有再取 s16/s24 采样格式。
                var mBits = Regex.Match(stderr, @"(\d+)\s*bits");
                if (mBits.Success)
                {
                    bits = int.Parse(mBits.Groups[1].Value);
                }
                else
                {
                    var mBits2 = Regex.Match(stderr, @"s(\d+)");
                    if (mBits2.Success)
                    {
                        bits = int.Parse(mBits2.Groups[1].Value);
                    }
                }

                if (rate <= 0 || channels <= 0 || bits <= 0)
                {
                    return null;
                }

                return (rate, channels, bits);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>转码缓存目录（%LOCALAPPDATA%\CelesteMusicPlayer\TranscodeCache）。</summary>
        private static string GetCacheDir()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "CelesteMusicPlayer", "TranscodeCache");
        }

        /// <summary>缓存键：源路径哈希 + 最后修改时间 + 转码参数指纹（参数变化时自动失效，如位深/采样率调整）。</summary>
        private static string GetCacheKey(string sourcePath, string transcodeArgs)
        {
            try
            {
                var fi = new FileInfo(sourcePath);
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())));
                // 转码参数里含随机临时输出路径，统一替换成固定占位符后再哈希，
                // 使"转码策略"（codec/-ar/-ac 等）决定 key，而非每次不同的临时路径。
                string sig = Regex.Replace(
                    transcodeArgs, @"""\\?\w+\.partial\.[^""]*""", "\"OUT.wav\"");
                string sigHash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sig)));
                return hash + "_" + fi.LastWriteTimeUtc.Ticks + "_" + sigHash.Substring(0, 12);
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }

        /// <summary>缓存超限(默认 2GB)时删除最旧文件。</summary>
        private static void TrimCache(string cacheDir, long maxBytes = 2L * 1024 * 1024 * 1024)
        {
            try
            {
                if (!Directory.Exists(cacheDir))
                {
                    return;
                }

                var files = Directory.GetFiles(cacheDir, "*.wav")
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();
                long total = files.Sum(f => SafeFileLength(f.FullName));

                // 健康清理策略：只有超过上限才触发；一次性删除「最早写入」的一半文件，
                // 把缓存体量直接压到一半，而不是逐个删到刚好 ≤ 上限——
                // 避免频繁的全量扫描/删除 I/O 抖动，也给后续写入留出更大缓冲。
                if (total <= maxBytes)
                {
                    return;
                }

                int removeCount = Math.Max(1, files.Count / 2);
                long removedBytes = 0;
                for (int i = 0; i < removeCount && i < files.Count; i++)
                {
                    try
                    {
                        removedBytes += SafeFileLength(files[i].FullName);
                        files[i].Delete();
                    }
                    catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
                }

                StartupLog.Write($"[TrimCache] 超上限(max={maxBytes / (1024.0 * 1024.0):0.0}MB)，清理最早 {removeCount} 个文件，释放约 {removedBytes / (1024.0 * 1024.0):0.0}MB，删除前总计 {total / (1024.0 * 1024.0):0.0}MB");
            }
            catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
        }

        /// <summary>解析 ffmpeg 输出中的 "Duration: HH:MM:SS.xx"。</summary>
        private static double ParseDurationSeconds(string line)
        {
            try
            {
                int idx = line.IndexOf("Duration:", StringComparison.Ordinal);
                if (idx < 0)
                {
                    return 0;
                }

                string seg = line.Substring(idx + 9).Trim();
                int comma = seg.IndexOf(',');
                if (comma > 0)
                {
                    seg = seg.Substring(0, comma);
                }

                string[] parts = seg.Trim().Split(':');
                if (parts.Length == 3
                    && double.TryParse(parts[0], out double h)
                    && double.TryParse(parts[1], out double m)
                    && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out double s))
                {
                    return h * 3600 + m * 60 + s;
                }
            }
            catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }

            return 0;
        }

        /// <summary>解析 ffmpeg 进度行 out_time_ms/out_time_us，返回百分比(-1 表示无法计算)。</summary>
        private static int ParseProgressPercent(string line, double totalSeconds)
        {
            try
            {
                bool isMs = line.StartsWith("out_time_ms=", StringComparison.Ordinal);
                bool isUs = line.StartsWith("out_time_us=", StringComparison.Ordinal);
                if (!isMs && !isUs)
                {
                    return -1;
                }

                int eq = line.IndexOf('=');
                if (eq < 0 || !long.TryParse(line.Substring(eq + 1).Trim(), out long value))
                {
                    return -1;
                }

                if (totalSeconds <= 1)
                {
                    return -1;
                }

                double seconds = isMs ? value / 1000.0 : value / 1_000_000.0;
                int pct = (int)(seconds / totalSeconds * 100);
                return Math.Clamp(pct, 0, 100);
            }
            catch
            {
                return -1;
            }
        }

        private static string FirstError(string stderr)
        {
            foreach (string line in stderr.Split('\n'))
            {
                string s = line.Trim();
                if (s.Contains("Error", StringComparison.OrdinalIgnoreCase))
                {
                    return s.Length > 120 ? s.Substring(0, 120) : s;
                }
            }

            string t = stderr.Trim();
            return t.Length > 120 ? t.Substring(0, 120) : t;
        }

        private static long SafeFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }
    }
}
