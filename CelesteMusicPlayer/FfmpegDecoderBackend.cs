using System;
using System.Collections.Concurrent;
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

        /// <summary>
        /// 最近一次 BuildTranscodeArgs 探测到的「源文件原始格式」（如 "44100hz / 24bit / 2声道"），
        /// 即未经任何转码的源本身规格；探测失败或走 DSD 分支时为 null。
        /// 用途：链路显示与 bit-perfect 徽章把它与「实际送链路的 WAV 格式」比对，
        /// 一旦源被悄悄降级（探测失败回退 16bit、设备不认时的重采样回退），显示必须现形，不允许谎报直通。
        /// 静态属性：转码参数构造与播放在同一线程链路上串行发生，按"最近一次"语义使用。
        /// </summary>
        public static string? LastOriginalSourceDescription { get; private set; }

        /// <summary>
        /// 最近一次探测到的「源文件真实格式」（结构化），与 <see cref="LastOriginalSourceDescription"/>
        /// 同源同步写入（探测失败 / DSD 分支时为 null）。供链路结构化状态（ChainFormatState.SourceFile）
        /// 与 bit-perfect 徽标比对使用——徽标判标志位，不再从描述串反解析。
        /// 语义同为"最近一次"：转码参数构造与播放在同一线程链路上串行发生。
        /// </summary>
        public static AudioFormat? LastProbedSourceFormat { get; private set; }

        /// <summary>
        /// 最近一次转码参数构建是否走了「探测失败兜底」（固定 16bit/44.1kHz/立体声）。
        /// DSD 分支（直转 PCM/容器率）不置位——那是设计路径，不是降级。
        /// 链路结构化状态据此区分 FailedFallback 与设计路径，避免 DSD 歌被误标"兜底降级"。
        /// </summary>
        public static bool LastTranscodeWasFallback { get; private set; }

        /// <summary>
        /// 最近一次转码参数构建时的独占协商计划（阶段二：HiFiOutputBackend.ProbeExclusivePlan 的结果）。
        /// 语义同为"最近一次"：转码参数构造与播放在同一线程链路上串行发生。
        /// 非独占模式 / 未探测时为 null；PlayWavAsync 据此把"设备不支持 96k"这类原因写进链路状态上屏。
        /// </summary>
        public static HiFiOutputBackend.ExclusivePlan? LastTranscodePlan { get; private set; }

        // ===== 探测结果缓存 + 缓存文件访问时间（2026-09-21 加） =====

        /// <summary>源格式探测结果缓存：key = 路径|最后修改时间|文件长度。
        /// 旧实现每一首歌起播都要 spawn 一次 ffmpeg 去探测，而预加载下一首会再探测一次——
        /// 同一首歌一次播放周期里被探测两遍，且是同步阻塞（WaitForExit 最多 3 秒），
        /// 在 UI 线程上就是实打实的卡死。缓存后同一文件同一版本只探测一次。</summary>
        private static readonly ConcurrentDictionary<string, (int Rate, int Channels, int Bits)> s_probeCache
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>缓存 WAV 的最近使用时刻（Environment.TickCount64）。清理时跳过最近用过的，
        /// 保证「正在播的、刚预载的」永远不会被删（旧实现删最旧的一半，可能把在播文件删掉）。</summary>
        private static readonly ConcurrentDictionary<string, long> s_cacheLastUse
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>上次清理时刻，用于限流（避免每转一首歌就扫一次整个缓存目录 + 删一批文件）。</summary>
        private static long s_lastTrimTick;

        /// <summary>标记某个缓存 WAV 正在被使用（播放/预载命中时调用），清理时会跳过它。</summary>
        public static void MarkCacheUsed(string? wavPath)
        {
            if (string.IsNullOrWhiteSpace(wavPath))
            {
                return;
            }

            s_cacheLastUse[wavPath] = Environment.TickCount64;
            // 防止无限增长：超过 256 条就整体丢弃重建（条目只在播放期间有意义）
            if (s_cacheLastUse.Count > 256)
            {
                s_cacheLastUse.Clear();
                s_cacheLastUse[wavPath] = Environment.TickCount64;
            }
        }

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
            string transcodeArgs = await BuildTranscodeArgsAsync(path, partial, outputMode, devicePreference);
            string key = GetCacheKey(path, transcodeArgs);
            string cachedWav = Path.Combine(cacheDir, key + ".wav");
            string targetWav;

            if (File.Exists(cachedWav))
            {
                targetWav = cachedWav;
                MarkCacheUsed(cachedWav); // 标记为在播，缓存清理时跳过
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
                string transcodeArgs = await BuildTranscodeArgsAsync(path, partial, outputMode, devicePreference);
                string key = GetCacheKey(path, transcodeArgs);
                string cachedWav = Path.Combine(cacheDir, key + ".wav");
                if (File.Exists(cachedWav))
                {
                    MarkCacheUsed(cachedWav); // 预载命中也算在用，别被清理删掉
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

                MarkCacheUsed(cachedWav);
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
        private async Task<string> BuildTranscodeArgsAsync(string srcPath, string dstPath, HiFiOutputBackend.OutputMode outputMode, string? devicePreference)
        {
            string ext = Path.GetExtension(srcPath).ToLowerInvariant();
            LastOriginalSourceDescription = null; // 每首歌重新探测，防止上一首的残留造成误判
            LastProbedSourceFormat = null;
            LastTranscodeWasFallback = false;
            LastTranscodePlan = null; // 阶段二：协商计划同样只对"最近一次"有效，先清防残留
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

            // 异步探测（2026-09-21 改）：旧实现在这里同步 spawn ffmpeg 并 ReadToEnd + WaitForExit(3000)，
            // 起播与预加载各跑一次，阻塞调用线程最多 3 秒 —— 这是"MP3 也卡"的主要来源之一。
            var srcFmt = await ProbeSourceFormatAsync(srcPath);
            if (srcFmt is (int rate, int ch, int bits) && rate > 0 && ch > 0)
            {
                // 记录源文件原始格式（未经转码），供链路显示与 bit-perfect 诚实判定比对
                LastOriginalSourceDescription = rate + "hz / " + bits + "bit / " + ch + "声道";
                LastProbedSourceFormat = new AudioFormat(rate, bits, ch, false);
                StartupLog.Write($"[转码] 源探测 {System.IO.Path.GetFileName(srcPath)} → {rate}hz/{bits}bit/{ch}ch");

                // 共享模式：采样率对齐设备 MixFormat，声道固定 2（立体声），输出统一用 pcm_f32le（IEEE float）。
                // 声道不再跟随设备 MixFormat：部分设备（HDMI/DP 外接显示器）会报告 6/8 声道，
                // 转出的多声道 WAV 在立体声设备的共享模式下会被 IAudioClient::Initialize 拒绝
                // （E_INVALIDARG / "Value does not fall within the expected range"），整首歌播放失败；
                // 且多声道缓存体积是立体声的数倍。音乐源本身是立体声，共享模式下由系统混音器处理即可。
                if (outputMode == HiFiOutputBackend.OutputMode.WasapiShared)
                {
                    var mix = HiFiOutputBackend.GetDeviceMixFormat(devicePreference);
                    if (mix is (int mr, _, _, _) && mr > 0)
                    {
                        return string.Format("-y -i \"{0}\" -vn -c:a pcm_f32le -ar {1} -ac 2 \"{2}\"", srcPath, mr, dstPath);
                    }

                    // 设备 MixFormat 探测失败兜底：固定 48k/2ch float32（系统共享普遍支持）
                    return string.Format("-y -i \"{0}\" -vn -c:a pcm_f32le -ar 48000 -ac 2 \"{1}\"", srcPath, dstPath);
                }

                // 严格按源位深输出（bit-perfect）：16→s16le、24→s24le、32→s32le。
                string enc = bits switch { <= 16 => "pcm_s16le", <= 24 => "pcm_s24le", _ => "pcm_s32le" };
                // 阶段二：独占模式先问 DAC 吃得下源采样率吗（探测按设备缓存，仅首次有 COM 开销）。
                //   吃得下 → 目标率=源率，参数与阶段二之前逐字节一致（同一首歌缓存键不变、不重转码）；
                //   吃不下 → 选 ≤源率 的最高支持档，注入 -ar；缓存键自动分开（GetCacheKey 含参数指纹）。
                //   探测失败 → 返回 null，保守放行源率，由引擎现有 MixFormat 兜底重转（可播优先，非本处职责）。
                if (outputMode == HiFiOutputBackend.OutputMode.WasapiExclusive)
                {
                    var plan = HiFiOutputBackend.ProbeExclusivePlan(devicePreference, rate, Math.Min(ch, 2));
                    LastTranscodePlan = plan;
                    if (plan != null && plan.TargetRate != rate)
                    {
                        StartupLog.Write("[链路] 转码目标率 " + rate + "→" + plan.TargetRate + "（" + plan.Reason + "）");
                        return string.Format("-y -i \"{0}\" -vn -acodec {1} -ar {2} \"{3}\"", srcPath, enc, plan.TargetRate, dstPath);
                    }

                    StartupLog.Write("[链路] 目标率=源率 " + rate + "（设备支持，源率直通）");
                }

                return string.Format("-y -i \"{0}\" -vn -acodec {1} \"{2}\"", srcPath, enc, dstPath);
            }

            // 探测失败回退：固定 16bit/44.1kHz/立体声，保证可播。
            // 注意：高于 16bit/44.1kHz 的源在这里会被静默降级，日志必须留痕，链路显示据此标注"转码已降级"。
            LastOriginalSourceDescription = null;
            LastProbedSourceFormat = null;
            LastTranscodeWasFallback = true;
            StartupLog.Write("[转码警告] 源格式探测失败，按 16bit/44100Hz/立体声兜底（高于此规格的源会被降级）：" + srcPath);
            return string.Format("-y -i \"{0}\" -vn -acodec pcm_s16le -ar 44100 -ac 2 \"{1}\"", srcPath, dstPath);
        }

        /// <summary>探测缓存 key：路径 + 最后修改时间 + 文件长度。三者任一变化都视为源文件变了，重新探测。</summary>
        private static string MakeProbeKey(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return path + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
            }
            catch
            {
                return path; // 取不到文件信息时不缓存（每次都探测）
            }
        }

        /// <summary>用 ffmpeg -i 探测源音频格式，返回 (采样率, 声道数, 位深)；探测失败返回 null。
        /// 异步 + 结果缓存（2026-09-21 改）：不再同步阻塞调用线程。</summary>
        private static async Task<(int Rate, int Channels, int Bits)?> ProbeSourceFormatAsync(string path)
        {
            try
            {
                string? ffmpeg = AudioPlaybackEngine.FindFfmpeg();
                if (string.IsNullOrWhiteSpace(ffmpeg))
                {
                    return null;
                }

                string key = MakeProbeKey(path);
                if (s_probeCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    Arguments = "-i \"" + path + "\""
                };

                string stderr;
                using (var proc = Process.Start(psi)!)
                {
                    // 探测也要降优先级：它是起播路径上的进程，不该和 WASAPI 渲染线程抢 CPU
                    try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }

                    Task<string> readTask = proc.StandardError.ReadToEndAsync();
                    Task waitTask = proc.WaitForExitAsync();
                    // 兜底超时：源文件在云盘/网络盘上时 ffmpeg 可能长时间不返回，
                    // 不能让它把起播流程无限挂住（旧实现是 WaitForExit(3000) 硬超时）。
                    Task timeoutTask = Task.Delay(5000);
                    Task done = await Task.WhenAny(Task.WhenAll(readTask, waitTask), timeoutTask);
                    if (done == timeoutTask)
                    {
                        try { proc.Kill(true); } catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
                        StartupLog.Write("[转码警告] 源格式探测超时(5s)，按兜底规格转码：" + path);
                        return null;
                    }

                    stderr = await readTask;
                }

                var parsed = ParseStreamFormat(stderr);
                if (parsed.HasValue)
                {
                    s_probeCache[key] = parsed.Value;
                    if (s_probeCache.Count > 512)
                    {
                        s_probeCache.Clear(); // 粗放但够用：曲库再大也不会累积到内存问题
                    }
                }

                return parsed;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从 ffmpeg -i 的 stderr 里解析音轨格式（采样率/声道/位深）；解析不出返回 null。</summary>
        private static (int Rate, int Channels, int Bits)? ParseStreamFormat(string stderr)
        {
            if (string.IsNullOrEmpty(stderr))
            {
                return null;
            }

            {
                // ===== 只解析 "Stream #0:N: Audio:" 这一行，绝不能对整段 stderr 做正则 =====
                //
                // 本项目内置的 ffmpeg 是裁剪版自定义构建，stderr 顶部的 configuration: 横幅用
                // --enable-encoder='pcm_s16le,pcm_s16be,pcm_s24le,pcm_s32le,...' 的形式罗列全部编解码器。
                // 旧实现对整段 stderr 匹配，被横幅抢先命中（2026-09-21 实测根因）：
                //   · 位深：s(\d+) 命中横幅里的 pcm_s16le → 24bit 源被误判成 16bit，
                //     转码按 16bit 输出，独占模式把 16bit WAV 当"源直通"，
                //     于是 24bit 歌曲在链路里显示 16bit 还谎称 bit-perfect；
                //   · 声道：(mono|stereo|...|6\.1|...) 命中 libavutil 61. 6.100 的版本号 → 声道被误判成 7。
                // 先定位音频流描述行再取数，横幅 / 版本号 / 元数据都干扰不到。
                // 注意：这与 ffmpeg 版本无关——任何显式列出编码器名的构建（定制 slim 版必然如此）都有此横幅，
                // 所以换 ffmpeg 二进制治不了这个病，必须按行解析。
                string? streamLine = null;
                foreach (var line in stderr.Split('\n'))
                {
                    if (line.Contains("Stream #", StringComparison.Ordinal)
                        && line.Contains("Audio:", StringComparison.Ordinal))
                    {
                        streamLine = line;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(streamLine))
                {
                    return null;
                }

                int rate = 0, channels = 0, bits = 0;
                var mRate = Regex.Match(streamLine, @"(\d+)\s*Hz");
                if (mRate.Success)
                {
                    rate = int.Parse(mRate.Groups[1].Value);
                }

                var mCh = Regex.Match(streamLine, @"(mono|stereo|2\.1|5\.1|6\.1|7\.1)");
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
                else
                {
                    // 个别容器只写 "N channels"（部分 mov/mkv 音轨）
                    var mCh2 = Regex.Match(streamLine, @"(\d+)\s*channels?");
                    if (mCh2.Success)
                    {
                        channels = int.Parse(mCh2.Groups[1].Value);
                    }
                }

                // 位深优先级（全部只在流信息行内匹配，碰不到横幅）：
                //   1) 括号里的 "NN bit" —— FLAC/ALAC/WavPack 的 24bit 源显示 s32 (24 bit)，这是真实位深；
                //      （旧正则写的是复数 "NN bits"，匹配不上 ffmpeg 的单数 "(24 bit)" 写法，只能走 s(\d+) 兜底，
                //        于是每次都命中横幅里的 pcm_s16le —— 两个缺陷叠加才是完整根因）
                //   2) "NN bits" —— 旧版 ffmpeg 的复数写法，保留兼容；
                //   3) 采样格式 s16/s24/s32 —— 纯 PCM WAV 只显示 s16；
                //      （\b 词边界 + 结尾可选 p，保证 pcm_s16le 这类编码器名、s302m 这类冷门编码器名不会被误中：
                //        s 前面是下划线/后面紧跟字母时都不是词边界；s16p/s32p 这类平面写法也能取到）
                //   4) fltp / flt —— **解码器的内部浮点采样格式，不是源的位深**（2026-09-22 修正）。
                //      MP3/AAC/Opus/Vorbis/WMA 等有损格式的解码器一律输出 fltp，但这不代表源有 32bit：
                //      这些格式的有效精度就是 16bit，把 fltp 当 32bit 会让转码器选 pcm_s32le，
                //      WAV 缓存体积直接翻倍——5 分钟 MP3 从 ~53MB 涨到 ~106MB，越过 OpenWaveSource
                //      的 64MB 阈值，从「整首读进内存」掉进「8MB 流式读盘」分支，播放时每 12ms 打一次磁盘。
                //      这就是"MP3 明明很小也卡"的直接原因（日志里所有 .mp3 都被探测成 32bit 可佐证）。
                //      修正：无括号位深声明 + 浮点输出 → 按 16bit 处理（有损源的原生精度，体积回到内存友好区间）。
                //      无损源（FLAC/ALAC 24bit）都带 "(24 bit)" 括号，走分支 1，不受此影响。
                var mBitsParen = Regex.Match(streamLine, @"\((\d+)\s*bit\)");
                if (mBitsParen.Success)
                {
                    bits = int.Parse(mBitsParen.Groups[1].Value);
                }
                else
                {
                    var mBits = Regex.Match(streamLine, @"(\d+)\s*bits?");
                    if (mBits.Success)
                    {
                        bits = int.Parse(mBits.Groups[1].Value);
                    }
                    else
                    {
                        var mBits2 = Regex.Match(streamLine, @"\bs(\d+)(p)?\b");
                        if (mBits2.Success)
                        {
                            bits = int.Parse(mBits2.Groups[1].Value);
                        }
                        else if (streamLine.Contains("fltp", StringComparison.Ordinal)
                              || streamLine.Contains("flt,", StringComparison.Ordinal))
                        {
                            // 浮点解码输出 ≠ 源位深，真实位深要看 codec 是什么，不能一律 16bit：
                            //  · 文件本身就是浮点存储（pcm_f32le / pcm_f64le / float 编码的 FLAC）
                            //    → 尊重真实精度，按 32 / 64bit 转码。降级到 16bit 是**真丢精度**，
                            //      对音频播放器来说不能接受；
                            //  · mp3 / aac / vorbis / opus / wma 等有损编码：ISO 规范下解码输出
                            //    就是 16bit PCM，ffmpeg 内部用 float 只是实现方式 → 16bit。
                            //    这既是真实值，也避免 WAV 缓存体积翻倍越过阈值掉进流式读盘；
                            //  · 其余未知 → 16bit 安全默认（宁可保守，也不要撑爆缓存）。
                            if (streamLine.Contains("pcm_f64", StringComparison.Ordinal))
                            {
                                bits = 64;
                            }
                            else if (streamLine.Contains("pcm_f32", StringComparison.Ordinal))
                            {
                                bits = 32;
                            }
                            else if (streamLine.Contains("flac", StringComparison.Ordinal))
                            {
                                // FLAC 支持 float 样本；整数样本不会走到这一支（会是 s16/s32 (24 bit)）
                                bits = 32;
                            }
                            else
                            {
                                bits = 16;
                            }
                        }
                    }
                }

                if (rate <= 0 || channels <= 0 || bits <= 0)
                {
                    return null;
                }

                return (rate, channels, bits);
            }
        }

        /// <summary>转码缓存目录（%LOCALAPPDATA%\CelesteMusicPlayer\TranscodeCache）。</summary>
        internal static string TranscodeCacheDir => GetCacheDir();

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
                // 旧正则写的是 \?\w+\.partial\.[^"]*，只能匹配 "\xxx.partial.wav" 这类开头；
                // 而 partial 是绝对路径（C:\...\abcd.partial.wav，带盘符），替换永远不命中 →
                // 每次转码 key 都不同 → 缓存永不命中 → 同一首歌每播一次都整首重转码
                // （实测缓存目录里同一首歌堆了 3 份 16bit WAV；云盘源还会放大成播放卡顿）。
                // 改为匹配任意盘的绝对路径：引号内任意字符 + .partial.wav + 引号（2026-09-21 修）。
                string sig = Regex.Replace(
                    transcodeArgs, @"""[^""]*\.partial\.wav""", "\"OUT.wav\"");
                string sigHash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sig)));
                return hash + "_" + fi.LastWriteTimeUtc.Ticks + "_" + sigHash.Substring(0, 12);
            }
            catch
            {
                return Guid.NewGuid().ToString("N");
            }
        }

        /// <summary>缓存超限时的温和清理（2026-09-21 重写）。
        /// 旧策略的问题：超限就「一次性删掉最旧的一半」。实测一次删掉 21 个文件 / 958MB，
        /// 这场持续数秒的大批量删除正好和正在读盘的播放抢 I/O —— 播放卡死的直接来源。
        /// 新策略四条：① 按最久未用（LRU）排序；② 每次最多删 64MB，只降到低水位（上限 85%），
        /// 剩下的留给下一次；③ 两次清理间隔 ≥30s；④ 正在播 / 刚预载的文件跳过不删。</summary>
        private static void TrimCache(string cacheDir)
        {
            try
            {
                if (!Directory.Exists(cacheDir))
                {
                    return;
                }

                // ③ 限流：距上次清理不足 30s 直接返回，别每转一首歌就扫一遍目录
                long now = Environment.TickCount64;
                if (now - s_lastTrimTick < 30_000)
                {
                    return;
                }

                s_lastTrimTick = now;
                long maxBytes = CacheLimitBytes();
                string[] all = Directory.GetFiles(cacheDir, "*.wav");
                long total = all.Sum(SafeFileLength);
                if (total <= maxBytes)
                {
                    return;
                }

                // ① LRU：先删最久没被用过的；④ 在用的（当前播放 + 预载的下一首）永不进候选
                var candidates = all
                    .Where(f => !IsCacheInUse(f))
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => LastUseTick(f.FullName))
                    .ThenBy(f => f.LastWriteTimeUtc)
                    .ToList();

                // ② 只降到低水位，且本次最多删 64MB
                long lowWater = (long)(maxBytes * 0.85);
                long budget = Math.Min(total - lowWater, 64L * 1024 * 1024);
                long removedBytes = 0;
                int removedCount = 0;
                foreach (var f in candidates)
                {
                    if (removedBytes >= budget)
                    {
                        break;
                    }

                    try
                    {
                        removedBytes += SafeFileLength(f.FullName);
                        f.Delete();
                        removedCount++;
                    }
                    catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
                }

                if (removedCount > 0)
                {
                    StartupLog.Write($"[TrimCache] 温和清理：删 {removedCount} 个 / 释放 {removedBytes / (1024.0 * 1024.0):0.0}MB"
                        + $"（上限 {maxBytes / (1024.0 * 1024.0):0.0}MB，清理前 {total / (1024.0 * 1024.0):0.0}MB，本次预算 {budget / (1024.0 * 1024.0):0.0}MB）");
                }
            }
            catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
        }

        /// <summary>缓存上限（字节），读设置项 TranscodeCacheLimitMb；未配置或非法值回落 2GB。</summary>
        private static long CacheLimitBytes()
        {
            try
            {
                int mb = AppSettingsStore.Load().TranscodeCacheLimitMb;
                if (mb >= 64)
                {
                    return (long)mb * 1024 * 1024;
                }
            }
            catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }

            return DefaultCacheLimitBytes;
        }

        private const long DefaultCacheLimitBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>该缓存 WAV 是否正在被使用（最近 5 分钟内被 MarkCacheUsed 标记过）。</summary>
        private static bool IsCacheInUse(string path)
        {
            return s_cacheLastUse.TryGetValue(path, out long tick)
                && Environment.TickCount64 - tick < 5 * 60 * 1000L;
        }

        /// <summary>该缓存 WAV 最近一次被使用的时刻；从未用过返回 0（优先被清理）。</summary>
        private static long LastUseTick(string path)
        {
            return s_cacheLastUse.TryGetValue(path, out long tick) ? tick : 0;
        }

        /// <summary>设置页用：统计转码缓存占用（字节数 + 文件数）。</summary>
        public static (long Bytes, int Count) GetTranscodeCacheUsage()
        {
            try
            {
                string dir = GetCacheDir();
                if (!Directory.Exists(dir))
                {
                    return (0, 0);
                }

                long total = 0;
                int count = 0;
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    total += SafeFileLength(f);
                    count++;
                }

                return (total, count);
            }
            catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }

            return (0, 0);
        }

        /// <summary>设置页用：清空转码缓存。返回 (删除文件数, 释放字节数)。
        /// 正在播放 / 已预载的曲目缓存会保留（删掉会让在播的曲子读不到数据），UI 上如实说明。</summary>
        public static (int Count, long Bytes) ClearTranscodeCache()
        {
            int count = 0;
            long bytes = 0;
            try
            {
                string dir = GetCacheDir();
                if (!Directory.Exists(dir))
                {
                    return (0, 0);
                }

                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    if (IsCacheInUse(f))
                    {
                        continue; // 在播/预载的，本次跳过
                    }

                    try
                    {
                        bytes += SafeFileLength(f);
                        File.Delete(f);
                        count++;
                    }
                    catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }
                }

                s_lastTrimTick = 0; // 清过了，允许下次立即触发温和清理
                StartupLog.Write($"[缓存] 用户手动清理转码缓存：删除 {count} 个，释放 {bytes / (1024.0 * 1024.0):0.0}MB");
            }
            catch (Exception caught) { StartupLog.WriteException("FfmpegDecoderBackend.cs", caught); }

            return (count, bytes);
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
