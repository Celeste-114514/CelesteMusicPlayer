using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Media.Audio;
using Windows.Media.Effects;
using Windows.Media.Render;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 自建音频播放引擎（阶段 1）：
    /// AudioGraph + AudioFileInputNode + AudioDeviceOutputNode + 真实 10 段均衡器（EqualizerEffectDefinition）。
    /// 阶段 2 将扩展为 FFmpeg 解码 + AudioFrameInputNode，以支持 APE / WavPack / TTA / DSD 等系统不支持的格式。
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

        /// <summary>播放位置变化（约 250ms 一次）。</summary>
        public event Action<TimeSpan>? PositionChanged;

        /// <summary>文件播放结束。</summary>
        public event Action? PlaybackEnded;

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
                ".ape" or ".wv" or ".tta" or ".dsf" or ".dff" or ".mpc" or ".tak" or
                ".opus" or ".mp2" or ".amr" or ".au" or ".cda" or ".mod" or ".s3m" or ".xm";
        }

        /// <summary>用内置 FFmpeg 把文件转成临时 WAV 后播放（支持 APE/WavPack/TTA/DSD 等系统不支持的格式）。</summary>
        public async Task<bool> PlayFileWithFfmpegAsync(string path, Action<string>? status = null)
        {
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
            string key = GetCacheKey(path);
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
                string partial = Path.Combine(cacheDir, key + ".partial.wav");
                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    Arguments = string.Format("-y -i \"{0}\" -vn -acodec pcm_s16le -ar 44100 -ac 2 \"{1}\"", path, partial)
                };

                try
                {
                    double totalSeconds = 0;
                    int lastPct = -1;
                    var errLines = new System.Collections.Generic.List<string>();
                    int exitCode;
                    using (Process proc = Process.Start(psi)!)
                    {
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
                        catch
                        {
                        }

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
                        catch
                        {
                        }
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
            return await PlayFileAsync(targetWav);
        }

        /// <summary>转码缓存目录（%LOCALAPPDATA%\CelesteMusicPlayer\TranscodeCache）。</summary>
        private static string GetCacheDir()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "CelesteMusicPlayer", "TranscodeCache");
        }

        /// <summary>缓存键：源路径哈希 + 最后修改时间（文件变化时自动失效）。</summary>
        private static string GetCacheKey(string sourcePath)
        {
            try
            {
                var fi = new FileInfo(sourcePath);
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())));
                return hash + "_" + fi.LastWriteTimeUtc.Ticks;
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
            catch
            {
            }

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
                foreach (FileInfo f in files)
                {
                    if (total <= maxBytes)
                    {
                        break;
                    }

                    try
                    {
                        total -= f.Length;
                        f.Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
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
                catch
                {
                }

                _lastTempWav = null;
            }
        }

        public async Task<bool> InitializeAsync()
        {
            if (_graph != null)
            {
                return true;
            }

            try
            {
                var settings = new AudioGraphSettings(AudioRenderCategory.Media);
                CreateAudioGraphResult result = await AudioGraph.CreateAsync(settings);
                if (result.Status != AudioGraphCreationStatus.Success)
                {
                    return false;
                }

                _graph = result.Graph;

                CreateAudioDeviceOutputNodeResult dev = await _graph.CreateDeviceOutputNodeAsync();
                if (dev.Status != AudioDeviceNodeCreationStatus.Success)
                {
                    return false;
                }

                _deviceNode = dev.DeviceOutputNode;

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

            StorageFile file;
            try
            {
                file = await StorageFile.GetFileFromPathAsync(path);
            }
            catch (Exception ex)
            {
                RaiseFailed(ex);
                return false;
            }

            try
            {
                CreateAudioFileInputNodeResult r = await _graph!.CreateFileInputNodeAsync(file);
                if (r.Status != AudioFileNodeCreationStatus.Success)
                {
                    RaiseFailed(new Exception("无法打开音频文件（格式可能不受系统支持）"));
                    return false;
                }

                _inputNode = r.FileInputNode;
                Duration = _inputNode.Duration;
                _inputNode.AddOutgoingConnection(_deviceNode);
                _inputNode.FileCompleted += InputNode_FileCompleted;

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

        public void Pause()
        {
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
            if (_graph == null || _inputNode == null)
            {
                return;
            }

            try
            {
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
            catch
            {
            }
        }

        public void Stop()
        {
            StopCore();
        }

        private double _targetVolume = 1.0;

        /// <summary>音量 0..1：通过重建输出连接增益实现（AudioGraph 无全局音量属性）。</summary>
        public void SetVolume(double volume)
        {
            _targetVolume = Math.Clamp(volume, 0.0, 1.0);
            if (_graph == null || _inputNode == null || _deviceNode == null)
            {
                return;
            }

            try
            {
                _inputNode.RemoveOutgoingConnection(_deviceNode);
                _inputNode.AddOutgoingConnection(_deviceNode, _targetVolume);
            }
            catch
            {
            }
        }

        /// <summary>应用 10 段均衡器增益（dB，-12..12）；null 表示旁路（移除 EQ 效果）。</summary>
        public void SetEqualizer(double[]? gainsDb)
        {
            if (_graph == null || _inputNode == null)
            {
                return;
            }

            try
            {
                EqualizerEffectDefinition? eq = null;
                for (int i = _inputNode.EffectDefinitions.Count - 1; i >= 0; i--)
                {
                    if (_inputNode.EffectDefinitions[i] is EqualizerEffectDefinition existing)
                    {
                        eq = existing;
                        _inputNode.EffectDefinitions.RemoveAt(i);
                    }
                }

                if (gainsDb == null)
                {
                    return;
                }

                eq = new EqualizerEffectDefinition(_graph);
                _inputNode.EffectDefinitions.Add(eq);
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

        private void StopCore()
        {
            if (_inputNode != null)
            {
                _inputNode.FileCompleted -= InputNode_FileCompleted;
                _inputNode.Stop();
                _inputNode.Dispose();
                _inputNode = null;
            }

            _isPlaying = false;
            _positionTimer?.Stop();
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
        }

        private void InputNode_FileCompleted(AudioFileInputNode sender, object args)
        {
            _isPlaying = false;
            _positionTimer?.Stop();
            Position = Duration;
            PlaybackEnded?.Invoke();
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
            CleanupTempWav();
            _positionTimer?.Stop();
            _positionTimer = null;
            _graph?.Dispose();
            _graph = null;
            _deviceNode = null;
        }
    }
}
