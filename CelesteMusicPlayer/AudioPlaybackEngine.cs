using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Devices;
using Windows.Media.Effects;
using Windows.Media.Render;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 自建音频播放引擎（阶段 1）：
    /// AudioGraph + AudioFileInputNode + AudioDeviceOutputNode + 真实 10 段均衡器（EqualizerEffectDefinition）。
    /// 阶段 2 将扩展为 FFmpeg 解码 + AudioFrameInputNode，以支持 APE / WavPack / TTA 等系统不支持的格式。
    /// </summary>
    public sealed class AudioPlaybackEngine : IDisposable
    {
        private AudioGraph? _graph;
        private AudioFileInputNode? _inputNode;
        private AudioDeviceOutputNode? _deviceNode;
        private DispatcherQueueTimer? _positionTimer;
        private DateTime _playStartUtc;
        private TimeSpan _pausedPosition;
        private bool _isPlaying;
        private bool _disposed;
        private string? _outputDeviceId;
        private Microsoft.UI.Dispatching.DispatcherQueue? _graphDispatcher;
        private AudioFileInputNode? _nextGraphNode;
        private double[]? _equalizerGains;
        private int _playGeneration; // 播放代次：每次新开播/无缝切换递增，用于识别 await 期间的过期预加载

        /// <summary>当前输出设备 ID（null = 系统默认）。</summary>
        public string? OutputDeviceId { get; private set; }

        /// <summary>HiFi 输出时实际协商的输出格式（WASAPI 设备端），否则 null。</summary>
        public string? ActualOutputFormat => _hifiOut?.ActualOutputFormat;

        /// <summary>当前播放源的原始格式描述（WAV 直通源）。</summary>
        public string? SourceFormatDescription => _hifiOut?.SourceFormatDescription;

        /// <summary>读取实时电平快照（post-DSP 信号）到调用方数组。返回是否取到
        /// （未播放或 DSD 直出时为 false）。UI 线程调用。</summary>
        public bool TryGetLevels(float[] peakOut, float[] rmsOut) => _hifiOut?.TryGetLevels(peakOut, rmsOut) ?? false;

        /// <summary>电平表声道数（0 = 当前无可测电平）。</summary>
        public int LevelMeterChannels => _hifiOut?.LevelMeterChannels ?? 0;

        private string? _devicePreference;
        private HiFiOutputBackend? _hifiOut;
        private NaudioDsdBackend? _dsdNaudioBackend; // A/B 诊断：NAudio WasapiOut 播 DoP 的后端（DsdUseNaudioOutput=true 时用）
        private HiFiOutputBackend.OutputMode _outputMode = HiFiOutputBackend.OutputMode.WasapiShared;

        /// <summary>是否为 HiFi 输出后端主导播放（三模式统一走 NAudio/HiFi DSP 链）。
        /// 恒为 true：共享 / WASAPI 独占 / ASIO 都经 HiFiOutputBackend 输出，使曲线 EQ / 声道 / 限幅实时生效。</summary>
        public bool IsHiFiMode => true;

        /// <summary>设置输出模式：独占模式（WASAPI Exclusive / ASIO）时全部曲目经 NAudio 输出。</summary>
        public void SetOutputMode(HiFiOutputBackend.OutputMode mode)
        {
            _outputMode = mode;
        }

        /// <summary>记录用户偏好的输出设备 ID（在下次重建 graph 时应用）。</summary>
        public void SetOutputDevicePreference(string? deviceId)
        {
            _devicePreference = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        }

        /// <summary>播放位置变化（约 250ms 一次）。</summary>
        public event Action<TimeSpan>? PositionChanged;

        /// <summary>文件播放结束。</summary>
        public event Action? PlaybackEnded;

        /// <summary>共享/ASIO 无缝切到下一首（上层更新标题/时长并继续预加载下下首）。</summary>
        public event Action? SeamlessTrackChanged;
        /// <summary>失败（初始化/打开文件/设置均衡器等）。</summary>
        public event Action<Exception>? Failed;

        /// <summary>记录 LastError 并触发失败事件。</summary>
        private void RaiseFailed(Exception ex)
        {
            LastError = ex.Message;
            Failed?.Invoke(ex);
        }

        public bool IsPlaying => _isPlaying;

        public TimeSpan Duration { get; private set; }

        public TimeSpan Position { get; private set; }

        /// <summary>最近一次失败的具体原因（用于 UI 提示）。</summary>
        public string? LastError { get; private set; }

        private string? _lastTempWav;

        /// <summary>内置 ffmpeg.exe 路径（Assets\ffmpeg\ffmpeg.exe 或 exe 同目录）。</summary>
        public static string? FindFfmpeg()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c))
                {
                    return c;
                }
            }

            return null;
        }

        /// <summary>系统 Media Foundation 不支持、需要 FFmpeg 转码的扩展名。</summary>
        public static bool NeedsFfmpeg(string path)
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

        /// <summary>用内置 FFmpeg 把文件转成临时 WAV 后播放（支持 APE/WavPack/TTA 等系统不支持的格式）。</summary>
        public async Task<bool> PlayFileWithFfmpegAsync(string path, Action<string>? status = null)
        {
            // DSD（DSF/DFF）输出策略：
            //   - 设置「DSD 输出模式 = DoP 直出」且当前为 WASAPI 独占 / ASIO 时，走 DoP 原生直出
            //     （DSD 1-bit 封进 176.4k..1411.2k DoP 容器直通 DAC，bit-perfect，不经 DSP/转码）；
            //   - 其余情况（共享模式、或用户选了转 PCM）走 ffmpeg 转 PCM：
            //     · 共享模式：16bit/44100Hz（系统可播，可听优先）；
            //     · WASAPI 独占 / ASIO：高质量 pcm_s32le @ 352800Hz。

            bool preferDop = string.Equals(AppSettingsStore.Load().DsdOutputMode, "Dop", StringComparison.OrdinalIgnoreCase);
            if (preferDop && _outputMode != HiFiOutputBackend.OutputMode.WasapiShared && IsDsdFile(path))
            {
                bool dopOk = await TryPlayDsdPreloadAsync(path, status).ConfigureAwait(false);
                if (dopOk)
                {
                    return true;
                }

                // DoP 直出失败（DAC 不支持 DoP/设备无法协商等）：降级转 PCM，保证可播。
                StartupLog.Write("DSD DoP 直出失败，降级转 PCM: " + path + " err=" + (LastError ?? ""));
                status?.Invoke("DoP 直出不可用，转 PCM…");
            }

            string? ffmpeg = FindFfmpeg();
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                RaiseFailed(new Exception("未找到内置 ffmpeg.exe"));
                return false;
            }

            if (!File.Exists(path))
            {
                RaiseFailed(new Exception("文件不存在：" + path));
                return false;
            }

            string cacheDir = GetCacheDir();
            string partial = Path.Combine(cacheDir, Guid.NewGuid().ToString("N") + ".partial.wav");
            string transcodeArgs = BuildTranscodeArgs(path, partial);
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
                    var errLines = new System.Collections.Generic.List<string>();
                    int exitCode;
                    using (Process proc = Process.Start(psi)!)
                    {
                        // 转码很耗 CPU；降为低优先级，避免抢占 WASAPI 独占（Pro Audio）渲染线程造成播放卡顿
                        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
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
                        RaiseFailed(new Exception("FFmpeg 转码失败：" + (string.IsNullOrWhiteSpace(detail) ? "未知原因" : detail)));
                        try
                        {
                            File.Delete(partial);
                        }
                        catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }

                        return false;
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
                        catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
                    }

                    targetWav = cachedWav;
                    TrimCache(cacheDir);
                }
                catch (Exception ex)
                {
                    RaiseFailed(ex);
                    return false;
                }
            }

            CleanupTempWav();
            _lastTempWav = null; // 缓存文件不随临时清理

            // HiFi 独占模式：转码后的 PCM WAV 直接经 NAudio 输出（WASAPI 独占 / ASIO）
            if (IsHiFiMode)
            {
                bool ok = PlayWavHiFi(targetWav);
                if (!ok)
                {
                    // 源格式（尤其是 DSD 转出的高采样率 PCM，如 DSD128→705.6kHz）设备不认时，
                    // 做重采样回退以保证可播。ASIO 驱动常不支持 705.6kHz，直接降到 44.1k 损失过大；
                    // 改为按 44.1k 家族候选采样率从高到低尝试，尽量用最高受支持档位（如 352.8/176.4/88.2k）。
                    if (_outputMode == HiFiOutputBackend.OutputMode.Asio)
                    {
                        // DSD/44.1k 家族受支持档候选（前向兼容把 44.1k 精确倍率优先）
                        int[] cands = { 352800, 176400, 88200, 44100 };
                        foreach (int rate in cands)
                        {
                            string fallback = Path.Combine(Path.GetDirectoryName(targetWav) ?? cacheDir, $"{key}.{rate}f.wav");
                            string args2 = string.Format("-y -i \"{0}\" -vn -c:a pcm_s32le -ar {1} -ac 2 \"{2}\"", path, rate, fallback);
                            if (!await RunFfmpegAsync(args2, status) || !File.Exists(fallback))
                            {
                                continue;
                            }

                            if (PlayWavHiFi(fallback))
                            {
                                CleanupTempWav();
                                _lastTempWav = fallback;
                                ok = true;
                                break;
                            }

                            try { File.Delete(fallback); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
                        }

                        if (!ok)
                        {
                            // 全部候选失败：退回设备 MixFormat 保底（可能为 16/32bit、常见采样率）
                            int devRate = 0, devCh = 0, devBits = 32; bool devFloat0 = false;
                            var mf0 = HiFiOutputBackend.GetDeviceMixFormat(null);
                            if (mf0 is (int r0, int c0, int b0, bool f0) && r0 > 0 && (c0 == 1 || c0 == 2))
                            {
                                devRate = r0; devCh = c0; devBits = b0 > 0 ? b0 : devBits; devFloat0 = f0;
                            }

                            if (devRate > 0 && devCh > 0)
                            {
                                string fallback = Path.Combine(Path.GetDirectoryName(targetWav) ?? cacheDir, key + ".fallback.wav");
                                string enc2 = devFloat0 ? "pcm_f32le" : (devBits <= 16 ? "pcm_s16le" : "pcm_s32le");
                                string args2 = string.Format("-y -i \"{0}\" -vn -c:a {1} -ar {2} -ac {3} \"{4}\"", path, enc2, devRate, devCh, fallback);
                                if (await RunFfmpegAsync(args2, status) && File.Exists(fallback))
                                {
                                    CleanupTempWav();
                                    _lastTempWav = fallback;
                                    ok = PlayWavHiFi(fallback);
                                }
                            }
                        }
                    }
                    else
                    {
                        // WASAPI 独占：按设备 MixFormat 重转一次（保证可播）
                        int devRate = 0, devCh = 0, devBits = 0; bool devFloat = false;
                        var mf = HiFiOutputBackend.GetDeviceMixFormat(_devicePreference);
                        if (mf is (int r, int c, int b, bool fl) && r > 0 && c > 0)
                        {
                            devRate = r; devCh = c; devBits = b; devFloat = fl;
                        }

                        if (devRate > 0 && devCh > 0)
                        {
                            string fallback = Path.Combine(Path.GetDirectoryName(targetWav) ?? cacheDir, key + ".fallback.wav");
                            string enc2 = devFloat ? "pcm_f32le" : (devBits <= 16 ? "pcm_s16le" : "pcm_s32le");
                            string args2 = string.Format("-y -i \"{0}\" -vn -c:a {1} -ar {2} -ac {3} \"{4}\"", path, enc2, devRate, devCh, fallback);
                            if (await RunFfmpegAsync(args2, status) && File.Exists(fallback))
                            {
                                CleanupTempWav();
                                _lastTempWav = fallback;
                                ok = PlayWavHiFi(fallback);
                            }
                        }
                    }

                    if (!ok)
                    {
                        LastError ??= "HiFi 输出失败";
                        RaiseFailed(new Exception(LastError!));
                    }
                }

                return ok;
            }

            return await PlayFileAsync(targetWav);
        }

        /// <summary>用 HiFiOutputBackend 播放转码后的 PCM WAV（WASAPI 独占 / ASIO）。</summary>
        private bool PlayWavHiFi(string wavPath, bool requireExact = false)
        {
            try
            {
                StopCore();
                _hifiOut ??= new HiFiOutputBackend();
                _hifiOut.PlaybackStopped -= Hifi_PlaybackStopped;
                _hifiOut.PlaybackStopped += Hifi_PlaybackStopped;
                _hifiOut.SeamlessTrackChanged -= Hifi_SeamlessTrackChanged;
                _hifiOut.SeamlessTrackChanged += Hifi_SeamlessTrackChanged;
                _hifiOut.PositionChanged -= Hifi_PositionChanged;
                _hifiOut.PositionChanged += Hifi_PositionChanged;

                bool ok = _hifiOut.PlayWavAsync(wavPath, _outputMode, _devicePreference, requireExact: requireExact);
                StartupLog.Write("HiFi播放 mode=" + _outputMode + " 设备=" + (_hifiOut.OutputDeviceName ?? "?") + " (pref=" + (_devicePreference ?? "默认") + ") ok=" + ok + (ok ? "" : " err=" + (_hifiOut.LastError ?? "")));
                if (!ok)
                {
                    LastError = _hifiOut.LastError ?? "HiFi 输出失败";
                    return false; // 交由上层尝试 MixFormat 回退；均失败时上层再报错。
                }

                Duration = _hifiOut.Duration;
                Position = TimeSpan.Zero;
                _isPlaying = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                RaiseFailed(ex);
                return false;
            }
        }

        private void Hifi_PlaybackStopped()
        {
            _isPlaying = false;
            Position = Duration;
            PlaybackEnded?.Invoke();
        }

        private void Hifi_SeamlessTrackChanged()
        {
            // 由 _hifiOut 无缝切到预加载的下一首；同步当前 Position/Duration 供上层
            Position = TimeSpan.Zero;
            if (_hifiOut != null)
            {
                Duration = _hifiOut.Duration; // 无缝续接后更新为下一首时长，供上层进度条/时长显示
            }

            SeamlessTrackChanged?.Invoke();
        }

        private void Hifi_PositionChanged(TimeSpan pos)
        {
            Position = pos;
            PositionChanged?.Invoke(pos);
        }

        /// <summary>是否为 DSD 文件（DSF/DFF）。</summary>
        private static bool IsDsdFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext is ".dsf" or ".dff";
        }

        /// <summary>DSD/DoP 原生直出（内存预读版）：后台预读线程把 DSF/DFF 解析封装为 DoP 容器帧
        /// 写入内存环形缓冲，独占 render 线程只从内存取帧原样直通 DAC（bit-perfect）。
        /// 不落盘、不走磁盘 I/O 实时读，杜绝"边播边从磁盘读/解 DSD"造成的电流音/卡顿。</summary>
        private async Task<bool> TryPlayDsdPreloadAsync(string dsdPath, Action<string>? status)
        {
            try
            {
                if (!File.Exists(dsdPath))
                {
                    RaiseFailed(new Exception("DSD 文件不存在：" + dsdPath));
                    return false;
                }

                status?.Invoke("DSD 缓冲直出：解析容器…");
                // 同步在调用线程初始化 WASAPI 独占 + DoP 内存源（与 PCM 路径一致，避免 MTA 线程跨线程用 COM 报错）；
                // 真正费时的 DSD 读取/封装由 DoPWaveSource 后台预读线程承担，不阻塞 UI。
                bool played = PlayDsdHiFi(dsdPath, _devicePreference);
                // 播放已启动（含起播预缓冲就绪）→ 清除顶部"解析容器"占位提示，避免整曲残留误导"一直在边解边播"
                if (played)
                {
                    status?.Invoke("");
                }
                StartupLog.Write("DSD 内存预读→独占直通: " + dsdPath + " ok=" + played
                    + (played ? "" : " err=" + (LastError ?? "")));
                return played;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                RaiseFailed(ex);
                return false;
            }
        }

        /// <summary>走 HiFiOutputBackend 的 DSD/DoP 内存预读直出（requireExact，禁降级）。</summary>
        private bool PlayDsdHiFi(string dsdPath, string? deviceId)
        {
            try
            {
                StopCore();
                _hifiOut ??= new HiFiOutputBackend();
                _hifiOut.PlaybackStopped -= Hifi_PlaybackStopped;
                _hifiOut.PlaybackStopped += Hifi_PlaybackStopped;
                _hifiOut.SeamlessTrackChanged -= Hifi_SeamlessTrackChanged;
                _hifiOut.SeamlessTrackChanged += Hifi_SeamlessTrackChanged;
                _hifiOut.PositionChanged -= Hifi_PositionChanged;
                _hifiOut.PositionChanged += Hifi_PositionChanged;

                // A/B 诊断路径：DsdUseNaudioOutput=true 时用 NAudio WasapiOut(独占) 播 DoP，不进原生 render。
                if (AppSettingsStore.Load().DsdUseNaudioOutput)
                {
                    return PlayDsdNaudio(dsdPath, deviceId);
                }

                bool ok = _hifiOut.PlayDsdAsync(dsdPath, deviceId, seekTo: null);
                if (!ok)
                {
                    LastError = _hifiOut.LastError ?? "DSD 内存直出失败";
                    return false;
                }

                Duration = _hifiOut.Duration;
                Position = TimeSpan.Zero;
                _isPlaying = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                RaiseFailed(ex);
                return false;
            }
        }

        /// <summary>A/B：用 NAudio WasapiOut(独占) 直接播 DoP 数据源，判断电流/黄灯是否来自原生 render。
        /// 仅诊断用：Pause/SkipPosition 在本路径降级为停止（不影响原生态走 _hifiOut）。</summary>
        private bool PlayDsdNaudio(string dsdPath, string? deviceId)
        {
            try
            {
                bool hasDev = !string.IsNullOrWhiteSpace(deviceId);
                var dec = DsdDecoderRegistry.Resolve(dsdPath);
                if (dec == null)
                {
                    LastError = "没有可用的 DSD 解码器。";
                    return false;
                }

                var dop = new DoPWaveSource(dec.Open(dsdPath), AppSettingsStore.Load().DsDoP32 ? 32 : 24);
                var backend = new NaudioDsdBackend(dop);
                if (!backend.Start())
                {
                    LastError = "NAudio DSD 播放启动失败";
                    backend.Dispose();
                    return false;
                }

                _dsdNaudioBackend = backend;
                Duration = backend.TotalTime;
                Position = TimeSpan.Zero;
                _isPlaying = true;
                StartupLog.Write("[NAudioDSD] 已用 NAudio WasapiOut(独占) 播 DoP — A/B 电流判断");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                RaiseFailed(ex);
                return false;
            }
        }

        /// <summary>转码缓存目录（%LOCALAPPDATA%\CelesteMusicPlayer\TranscodeCache）。</summary>
        private static string GetCacheDir()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "CelesteMusicPlayer", "TranscodeCache");
        }

        /// <summary>用 ffmpeg -i 探测源音频格式，返回 (采样率, 声道数, 位深)；探测失败返回 null。</summary>
        private static (int Rate, int Channels, int Bits)? ProbeSourceFormat(string path)
        {
            try
            {
                string? ffmpeg = FindFfmpeg();
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
                var mRate = System.Text.RegularExpressions.Regex.Match(stderr, @"(\d+)\s*Hz");
                if (mRate.Success)
                {
                    rate = int.Parse(mRate.Groups[1].Value);
                }

                var mCh = System.Text.RegularExpressions.Regex.Match(stderr, @"(mono|stereo|2\.1|5\.1|6\.1|7\.1)");
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
                var mBits = System.Text.RegularExpressions.Regex.Match(stderr, @"(\d+)\s*bits");
                if (mBits.Success)
                {
                    bits = int.Parse(mBits.Groups[1].Value);
                }
                else
                {
                    var mBits2 = System.Text.RegularExpressions.Regex.Match(stderr, @"s(\d+)");
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

        /// <summary>执行一次 ffmpeg 转码（args 已含输入/输出与格式参数），返回是否成功。</summary>
        private async Task<bool> RunFfmpegAsync(string args, Action<string>? status = null)
        {
            string? ffmpeg = FindFfmpeg();
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                RaiseFailed(new Exception("未找到内置 ffmpeg.exe"));
                return false;
            }

            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                Arguments = args
            };

            var errLines = new System.Collections.Generic.List<string>();
            double totalSeconds = 0;
            int lastPct = -1;

            try
            {
                using (Process proc = Process.Start(psi)!)
                {
                    // 转码很耗 CPU；降为低优先级，避免抢占 WASAPI 独占（Pro Audio）渲染线程造成播放卡顿
                    try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
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
                        RaiseFailed(new Exception("FFmpeg 转码失败：" + (string.IsNullOrWhiteSpace(detail) ? "未知原因" : detail)));
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
                return false;
            }
        }

        /// <summary>构建 ffmpeg 转码参数：按源位深输出原生 PCM（16bit→s16le / 24bit→s24le / 32bit→s32le），
        /// 保留源采样率与声道，供 WaveFileReader 原样直通（严格 bit-perfect）。探测失败回退 16/44.1/2。</summary>
        private string BuildTranscodeArgs(string srcPath, string dstPath)
        {
            string ext = Path.GetExtension(srcPath).ToLowerInvariant();
            if (ext is ".dsf" or ".dff")
            {
                // 共享模式（系统混音/共享）：统一折叠为 16bit/44.1kHz PCM，保证设备/系统可播（非 bit-perfect，可听优先）。
                // WASAPI 独占 / ASIO：高质量 PCM，输出 DSD 原生容器率（DSD64→176400Hz、DSD128→352800Hz）。
                // 之前曾临时降到 176400 验证"高采样率时钟"假设，但实测降与不降都在同一位置卡，已确认与采样率无关，
                // 故恢复为按源 DSD 等级的高分辨率直出（352800Hz 等）。pcm_s32le 锚定位深避免浮点漂移。
                if (_outputMode == HiFiOutputBackend.OutputMode.WasapiShared)
                {
                    return string.Format("-y -i \"{0}\" -vn -c:a pcm_s16le -ar 44100 -ac 2 \"{1}\"", srcPath, dstPath);
                }

                return string.Format("-y -i \"{0}\" -vn -c:a pcm_s32le -ar 352800 -sample_fmt s32 \"{1}\"", srcPath, dstPath);
            }

            var srcFmt = ProbeSourceFormat(srcPath);
            if (srcFmt is (int rate, int ch, int bits) && rate > 0 && ch > 0)
            {
                // 共享模式：折叠到设备 MixFormat（采样率/声道），输出统一用 pcm_f32le（IEEE float）。
                // 实测：ffmpeg 输出 pcm_f32le 写的是标准 float 格式块 → NAudio WaveFileReader 返回 IeeeFloat，
                //   WasapiOut(Shared) 可正常 Init；而 pcm_s32le/pcm_s24le 会写成 WAVE_FORMAT_EXTENSIBLE →
                //   WaveFileReader 返回 Extensible → WasapiOut(Shared) 抛 E_INVALIDARG「value does not fall within the expected range」。
                //   因此共享一律用 float32，规避 16/44.1 ALAC、24/96k FLAC 等所有源无法播放的问题。
                if (_outputMode == HiFiOutputBackend.OutputMode.WasapiShared)
                {
                    var mix = HiFiOutputBackend.GetDeviceMixFormat(_devicePreference);
                    if (mix is (int mr, int mc, _, _) && mr > 0 && mc > 0)
                    {
                        return string.Format("-y -i \"{0}\" -vn -c:a pcm_f32le -ar {1} -ac {2} \"{3}\"", srcPath, mr, mc, dstPath);
                    }

                    // 设备 MixFormat 探测失败兜底：固定 48k/2ch float32（系统共享普遍支持）
                    return string.Format("-y -i \"{0}\" -vn -c:a pcm_f32le -ar 48000 -ac 2 \"{1}\"", srcPath, dstPath);
                }

                // 严格按源位深输出（bit-perfect）：16→s16le、24→s24le、32→s32le。
                // NAudio Extensible 报错仅在 NAudio 的 sample 层；独占用原生 WASAPI 直出，24bit 可全程直通。
                string enc = bits switch { <= 16 => "pcm_s16le", <= 24 => "pcm_s24le", _ => "pcm_s32le" };
                return string.Format("-y -i \"{0}\" -vn -acodec {1} \"{2}\"", srcPath, enc, dstPath);
            }

            // 探测失败回退：固定 16bit/44.1kHz/立体声，保证可播。
            return string.Format("-y -i \"{0}\" -vn -acodec pcm_s16le -ar 44100 -ac 2 \"{1}\"", srcPath, dstPath);
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
                string sig = System.Text.RegularExpressions.Regex.Replace(
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }

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
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
                }

                StartupLog.Write($"[TrimCache] 超上限(max={maxBytes/ (1024.0*1024.0):0.0}MB)，清理最早 {removeCount} 个文件，释放约 {removedBytes / (1024.0*1024.0):0.0}MB，删除前总计 {total / (1024.0*1024.0):0.0}MB");
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
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

        private void CleanupTempWav()
        {
            if (_lastTempWav != null)
            {
                try
                {
                    File.Delete(_lastTempWav);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }

                _lastTempWav = null;
            }
        }

        /// <summary>指定输出设备异步初始化（deviceId 为 null 时用系统默认）。</summary>
        public async Task<bool> InitializeAsync(string? deviceId = null)
        {
            if (_graph != null)
            {
                return true;
            }

            // 未显式指定时用用户偏好
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                deviceId = _devicePreference;
            }

            _outputDeviceId = deviceId;
            if (string.IsNullOrWhiteSpace(_outputDeviceId))
            {
                _outputDeviceId = null;
            }

            try
            {
                var settings = new AudioGraphSettings(AudioRenderCategory.Media);
                if (!string.IsNullOrWhiteSpace(_outputDeviceId))
                {
                    // 通过 PrimaryRenderDevice 指定输出设备（AudioGraph 默认 output node 对齐到该设备）
                    try
                    {
                        Windows.Devices.Enumeration.DeviceInformation? devInfo =
                            await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(_outputDeviceId);
                        if (devInfo != null)
                        {
                            settings.PrimaryRenderDevice = devInfo;
                        }
                    }
                    catch
                    {
                        // 设备不存在/已移除：忽略，用系统默认
                    }
                }

                CreateAudioGraphResult result = await AudioGraph.CreateAsync(settings);
                if (result.Status != AudioGraphCreationStatus.Success)
                {
                    return false;
                }

                _graph = result.Graph;

                CreateAudioDeviceOutputNodeResult dev = await _graph.CreateDeviceOutputNodeAsync();
                if (dev.Status != AudioDeviceNodeCreationStatus.Success)
                {
                    // 指定设备失败时回退到系统默认
                    _outputDeviceId = null;
                    OutputDeviceId = null;
                    CreateAudioDeviceOutputNodeResult fallback = await _graph.CreateDeviceOutputNodeAsync();
                    if (fallback.Status != AudioDeviceNodeCreationStatus.Success)
                    {
                        return false;
                    }

                    dev = fallback;
                }

                _deviceNode = dev.DeviceOutputNode;
                OutputDeviceId = _outputDeviceId;

                _graphDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                _positionTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _positionTimer.Interval = TimeSpan.FromMilliseconds(250);
                _positionTimer.Tick += (_, _) => UpdatePosition();
                return true;
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
                return false;
            }
        }

        public async Task<bool> PlayFileAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (_graph == null && !await InitializeAsync())
            {
                return false;
            }

            StopCore();

            try
            {
                AudioFileInputNode? node = await BuildGraphInputNodeAsync(path);
                if (node == null)
                {
                    return false; // BuildGraphInputNodeAsync 内部已 RaiseFailed
                }

                _inputNode = node;
                Duration = _inputNode.Duration;
                _inputNode.AddOutgoingConnection(_deviceNode);
                _inputNode.FileCompleted += InputNode_FileCompleted;

                _playGeneration++; // 新曲目开播，作废任何 await 中的旧预加载
                _graph.Start();
                _inputNode.Start();
                _playStartUtc = DateTime.UtcNow;
                _pausedPosition = TimeSpan.Zero;
                Position = TimeSpan.Zero;
                _isPlaying = true;
                _positionTimer?.Start();
                return true;
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
                return false;
            }
        }

        /// <summary>预加载下一首到 AudioGraph（共享模式）：创建第二输入节点但保持停止、不连输出。
        /// 当前曲目播完 FileCompleted 时若该节点已就绪则立即接手（无 graph 重建 → 共享模式 gapless）。</summary>
        public async Task<bool> PrepareNextGraphSeamless(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (_graph == null && !await InitializeAsync())
            {
                return false;
            }

            RecycleNextGraphNode(); // 旧的未用预加载先释放

            // 记录发起预加载时的播放代次：await（可能含 ffmpeg 转码，耗时数百 ms~数秒）期间
            // 用户可能切歌/停止 → 代次变化，需丢弃这条"针对旧曲目"的预加载，避免播完错切。
            int gen = _playGeneration;

            try
            {
                var node = await BuildGraphInputNodeAsync(path);
                if (node == null)
                {
                    return false;
                }

                // await 回来后若播放代次已变化（切歌/重播/停止）→ 预加载已失效，回收
                if (gen != _playGeneration || _disposed || _graph == null || _deviceNode == null)
                {
                    try { node.Stop(); node.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
                    return false;
                }

                node.AddOutgoingConnection(_deviceNode); // 连好输出，但不 Start
                node.FileCompleted += InputNode_FileCompleted;
                _nextGraphNode = node;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>构建一个已连接输出、尚未启动的输入节点（复用增益/均衡器配置由调用方各自应用）。</summary>
        private async Task<AudioFileInputNode?> BuildGraphInputNodeAsync(string path)
        {
            StorageFile file;
            try
            {
                file = await StorageFile.GetFileFromPathAsync(path);
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
                return null;
            }

            try
            {
                CreateAudioFileInputNodeResult r = await _graph!.CreateFileInputNodeAsync(file);
                if (r.Status != AudioFileNodeCreationStatus.Success)
                {
                    RaiseFailed(new Exception("无法打开音频文件（格式可能不受系统支持）"));
                    return null;
                }

                return r.FileInputNode;
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
                return null;
            }
        }

        /// <summary>释放未消费的图预加载节点。</summary>
        private void RecycleNextGraphNode()
        {
            if (_nextGraphNode != null)
            {
                try
                {
                    _nextGraphNode.FileCompleted -= InputNode_FileCompleted;
                    _nextGraphNode.RemoveOutgoingConnection(_deviceNode);
                    _nextGraphNode.Stop();
                    _nextGraphNode.Dispose();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }

                _nextGraphNode = null;
            }
        }

        public void Pause()
        {
            if (IsHiFiMode)
            {
                _hifiOut?.Pause();
                _isPlaying = false;
                return;
            }

            if (_graph == null || _inputNode == null || !_isPlaying)
            {
                return;
            }

            _pausedPosition = Position;
            _inputNode.Stop();
            _isPlaying = false;
            _positionTimer?.Stop();
        }

        public void Resume()
        {
            if (IsHiFiMode)
            {
                _hifiOut?.Resume();
                _isPlaying = true;
                return;
            }

            if (_graph == null || _inputNode == null || _isPlaying)
            {
                return;
            }

            _playStartUtc = DateTime.UtcNow;
            _inputNode.Start();
            _isPlaying = true;
            _positionTimer?.Start();
        }

        public void Seek(TimeSpan position)
        {
            if (IsHiFiMode)
            {
                _hifiOut?.Seek(position);
                Position = position;
                return;
            }

            if (_graph == null || _inputNode == null)
            {
                return;
            }

            try
            {
                RecycleNextGraphNode(); // seek 后位置已变，旧预加载作废，由上层按新曲目重新预加载
                _inputNode.Seek(position);
                if (_isPlaying)
                {
                    // Seek 后确保节点保持播放状态
                    _inputNode.Start();
                }

                _playStartUtc = DateTime.UtcNow;
                Position = position;
                _pausedPosition = position;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
        }

        public void Stop()
        {
            if (IsHiFiMode)
            {
                _hifiOut?.Stop();
                _isPlaying = false;
                Position = TimeSpan.Zero;
                return;
            }

            StopCore();
        }

        private double _userVolume = 1.0;

        /// <summary>音量 0..1：通过重建输出连接增益实现（AudioGraph 无全局音量属性）。</summary>
        public void SetVolume(double volume)
        {
            _userVolume = Math.Clamp(volume, 0.0, 1.0);
            if (IsHiFiMode)
            {
                _hifiOut?.SetVolume((float)_userVolume);
                return;
            }

            ApplyOutputGain();
        }

        /// <summary>设置源音频实际时长（元数据/TagLib）。HiFi 引擎用它作为进度/播完上限，规避 DSD 转 PCM 尾部 padding 导致时长越界。</summary>
        public void SetSourceDuration(TimeSpan sourceDuration)
        {
            _hifiOut?.SetSourceDuration(sourceDuration);
        }

        /// <summary>预加载下一首到无缝源。HiFi（独占/ASIO）：同格式字节级续接；共享模式：预建第二个 AudioGraph 输入节点。
        /// 返回是否采纳为无缝预加载。</summary>
        public async Task<bool> PrepareNextSeamless(string nextWavPath)
        {
            if (IsHiFiMode)
            {
                return _hifiOut?.PrepareNextSeamless(nextWavPath) ?? false;
            }

            return await PrepareNextGraphSeamless(nextWavPath);
        }

        /// <summary>用 ffmpeg 探测源文件真实音轨时长（秒）。失败返回 0。
        /// 用于 DSD 转 PCM 时长可能被转码 WAV 尾部 padding 拉长的可靠源时长。</summary>
        public async System.Threading.Tasks.Task<TimeSpan> ProbeSourceDurationAsync(string path)
        {
            try
            {
                string? ffmpeg = FindFfmpeg();
                if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(path)) return TimeSpan.Zero;
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
                    string err = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    foreach (var line in err.Split('\n'))
                    {
                        double sec = ParseDurationSeconds(line);
                        if (sec > 0)
                        {
                            return TimeSpan.FromSeconds(sec);
                        }
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
            return TimeSpan.Zero;
        }

        /// <summary>确保曲目已转码为缓存 WAV，返回缓存路径（供无缝预加载复用；失败返回 null）。</summary>
        public async Task<string?> EnsureCachedWavAsync(string path, Action<string>? status = null)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string cacheDir = GetCacheDir();
                string partial = Path.Combine(cacheDir, Guid.NewGuid().ToString("N") + ".partial.wav");
                string transcodeArgs = BuildTranscodeArgs(path, partial);
                string key = GetCacheKey(path, transcodeArgs);
                string cachedWav = Path.Combine(cacheDir, key + ".wav");
                if (File.Exists(cachedWav)) return cachedWav;

                Directory.CreateDirectory(cacheDir);
                if (!await RunFfmpegAsync(transcodeArgs, status)) return null;
                if (!File.Exists(partial)) return null;
                try { File.Move(partial, cachedWav); }
                catch { try { File.Delete(partial); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); } }
                TrimCache(cacheDir);
                return cachedWav;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>设备主音量标量 0..1；未知返回 -1。</summary>
        public float GetDeviceVolume()
        {
            return IsHiFiMode ? (_hifiOut?.GetDeviceVolume() ?? -1f) : -1f;
        }

        /// <summary>暂停前记录的真实设备主音量（供恢复时回到该值）。</summary>
        public float GetPausedDeviceVolume()
        {
            return IsHiFiMode ? (_hifiOut?.GetPausedDeviceVolume() ?? -1f) : -1f;
        }

        private void ApplyOutputGain()
        {
            if (_graph == null || _inputNode == null || _deviceNode == null)
            {
                return;
            }

            try
            {
                _inputNode.RemoveOutgoingConnection(_deviceNode);
                _inputNode.AddOutgoingConnection(_deviceNode, _userVolume);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
        }

        /// <summary>应用 10 段均衡器增益（dB，-12..12）；null 表示旁路（移除 EQ 效果）。</summary>
        public void SetEqualizer(double[]? gainsDb)
        {
            _equalizerGains = gainsDb == null ? null : (double[])GainsDbClone(gainsDb);
            ApplyEqualizerToNode(_inputNode);
            // HiFi 输出（ASIO/共享 NAudio/原生 WASAPI 独占均可 DSP）：把 EQ 增益转发给后端，由统一 DSP 链处理（非 bit-perfect）。
            _hifiOut?.SetEqualizer(gainsDb);
        }

        /// <summary>应用动态 EQ 曲线状态（DSP 面板用，band 列表 + preamp）。null / 无效果表示直通。播放中实时生效。</summary>
        public void SetEqCurve(EqCurveState? curve)
        {
            _hifiOut?.SetEqCurve(curve);
        }

        /// <summary>设置声道平衡（HiFi 输出的统一 DSP 链；独占/共享/ASIO 均生效，非 bit-perfect）。</summary>
        public void SetChannelBalance(ChannelBalanceState? state)
        {
            _hifiOut?.SetChannelBalance(state);
        }

        /// <summary>设置安全限幅/余量（HiFi 输出的统一 DSP 链）。</summary>
        public void SetSafety(DspSafetyState? state)
        {
            _hifiOut?.SetSafety(state);
        }

        /// <summary>设置 ReplayGain（响度归一化）。播放中实时生效（10ms 平滑渐变）。</summary>
        public void SetReplayGain(ReplayGainState? state, double trackGainDb, double albumGainDb, double peak)
        {
            _hifiOut?.SetReplayGain(state, trackGainDb, albumGainDb, peak);
        }

        /// <summary>对指定输入节点应用当前均衡器增益（新节点/无缝切换后调用）。</summary>
        private void ApplyEqualizerToNode(AudioFileInputNode? node)
        {
            if (_graph == null || node == null)
            {
                return;
            }

            try
            {
                EqualizerEffectDefinition? eq = null;
                for (int i = node.EffectDefinitions.Count - 1; i >= 0; i--)
                {
                    if (node.EffectDefinitions[i] is EqualizerEffectDefinition existing)
                    {
                        eq = existing;
                        node.EffectDefinitions.RemoveAt(i);
                    }
                }

                double[]? gainsDb = _equalizerGains;
                if (gainsDb == null)
                {
                    return;
                }

                eq = new EqualizerEffectDefinition(_graph);
                node.EffectDefinitions.Add(eq);
                for (int i = 0; i < eq.Bands.Count && i < gainsDb.Length; i++)
                {
                    eq.Bands[i].Gain = Math.Clamp(gainsDb[i], -12.0, 12.0);
                }
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
            }
        }

        private static double[] GainsDbClone(double[] src) => (double[])src.Clone();

        private void StopCore()
        {
            RecycleNextGraphNode();
            if (_inputNode != null)
            {
                _inputNode.FileCompleted -= InputNode_FileCompleted;
                _inputNode.Stop();
                _inputNode.Dispose();
                _inputNode = null;
            }

            _isPlaying = false;
            // A/B 诊断后端：停止并释放 NAudio DSD
            try { _dsdNaudioBackend?.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AudioPlaybackEngine.cs", caught); }
            _dsdNaudioBackend = null;
            _positionTimer?.Stop();
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
        }

        private void InputNode_FileCompleted(AudioFileInputNode sender, object args)
        {
            // 若已预加载下一首（共享模式无缝），立即在 UI 线程接手，不触发 PlaybackEnded。
            if (_nextGraphNode != null && _graphDispatcher != null)
            {
                _graphDispatcher.TryEnqueue(() => PlayPreloadedGraphNext(sender));
                return;
            }

            _isPlaying = false;
            _positionTimer?.Stop();
            Position = Duration;
            PlaybackEnded?.Invoke();
        }

        /// <summary>共享模式无缝切换：停/释放当前节点，启动已预加载的下一节点（单一输出会话，避免 graph 重建间隙）。</summary>
        private void PlayPreloadedGraphNext(AudioFileInputNode completed)
        {
            AudioFileInputNode? next = _nextGraphNode;
            _nextGraphNode = null;
            if (next == null || _graph == null || _deviceNode == null || _disposed)
            {
                // 预加载已被回收（如用户手动切歌/停止/seek 后）→ 不当作"播完"误切歌：
                // 只有在确已停止播放时才触发 PlaybackEnded，其余情况（seek/切歌中）忽略本次过期切换。
                if (!_isPlaying && ReferenceEquals(_inputNode, completed))
                {
                    _isPlaying = false;
                    _positionTimer?.Stop();
                    Position = Duration;
                    PlaybackEnded?.Invoke();
                }
                return;
            }

            try
            {
                if (_inputNode != null)
                {
                    _inputNode.FileCompleted -= InputNode_FileCompleted;
                    _inputNode.RemoveOutgoingConnection(_deviceNode);
                    _inputNode.Stop();
                    _inputNode.Dispose();
                }

                _inputNode = next;
                _playGeneration++; // 无缝切到下一首，作废任何 await 中的旧预加载
                next.FileCompleted -= InputNode_FileCompleted; // 已挂过，避免重复
                next.FileCompleted += InputNode_FileCompleted;
                Duration = next.Duration;

                // 切换后重新应用均衡器与增益（新节点默认无 EQ）
                ApplyEqualizerToNode(next);
                ApplyOutputGain();

                next.Start();
                _playStartUtc = DateTime.UtcNow;
                _pausedPosition = TimeSpan.Zero;
                Position = TimeSpan.Zero;
                _isPlaying = true;
                _positionTimer?.Start();
                SeamlessTrackChanged?.Invoke();
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
                _isPlaying = false;
                _positionTimer?.Stop();
                PlaybackEnded?.Invoke();
            }
        }

        private void UpdatePosition()
        {
            if (!_isPlaying || _inputNode == null)
            {
                return;
            }

            Position = _pausedPosition + (DateTime.UtcNow - _playStartUtc);
            if (Position > Duration)
            {
                Position = Duration;
            }

            PositionChanged?.Invoke(Position);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCore();
            _hifiOut?.Dispose();
            _hifiOut = null;
            CleanupTempWav();
            _positionTimer?.Stop();
            _positionTimer = null;
            _graph?.Dispose();
            _graph = null;
            _deviceNode = null;
        }
    }
}
