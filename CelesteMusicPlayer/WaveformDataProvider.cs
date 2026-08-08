using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 歌曲波形数据提供器：用内置 ffmpeg 解码音频，抽取低采样率峰值，
    /// 供「波形进度条」样式绘制。结果按路径缓存（上限 30 首）。
    /// </summary>
    internal static class WaveformDataProvider
    {
        private static readonly Dictionary<string, float[]> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private const int MaxCacheEntries = 30;

        public static async Task<float[]> GetWaveformAsync(string path, int bucketCount, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Array.Empty<float>();
            }

            if (Cache.TryGetValue(path, out float[]? hit) && hit.Length == bucketCount)
            {
                return hit;
            }

            await Gate.WaitAsync(ct);
            try
            {
                if (Cache.TryGetValue(path, out hit) && hit.Length == bucketCount)
                {
                    return hit;
                }

                float[] result = await DecodeAsync(path, bucketCount, ct);
                if (result.Length > 0)
                {
                    Cache[path] = result;
                    if (Cache.Count > MaxCacheEntries)
                    {
                        using Dictionary<string, float[]>.Enumerator e = Cache.GetEnumerator();
                        if (e.MoveNext())
                        {
                            Cache.Remove(e.Current.Key);
                        }
                    }
                }

                return result;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<float[]> DecodeAsync(string path, int bucketCount, CancellationToken ct)
        {
            string? ffmpeg = AudioPlaybackEngine.FindFfmpeg();
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(path))
            {
                return Array.Empty<float>();
            }

            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = string.Format("-i \"{0}\" -f s16le -ac 1 -ar 8000 -acodec pcm_s16le pipe:1", path)
            };

            try
            {
                using Process proc = Process.Start(psi)!;
                _ = proc.StandardError.ReadToEndAsync(); // 丢弃 stderr，防止管道阻塞
                var peaks = new List<float>(2_000_000);
                var buf = new byte[8192];
                while (true)
                {
                    int n = await proc.StandardOutput.BaseStream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                    if (n <= 0)
                    {
                        break;
                    }

                    for (int i = 0; i + 1 < n; i += 2)
                    {
                        short s = (short)(buf[i] | (buf[i + 1] << 8));
                        peaks.Add(Math.Abs(s / 32768f));
                    }
                }

                if (peaks.Count < bucketCount)
                {
                    StartupLog.Write("波形解码失败: 样本过少 " + peaks.Count);
                    return Array.Empty<float>();
                }

                // 分桶用 RMS(均方根):能体现音量起伏,避免全部接近满幅
                var buckets = new float[bucketCount];
                var bucketSums = new double[bucketCount];
                var bucketCounts = new int[bucketCount];
                for (int i = 0; i < peaks.Count; i++)
                {
                    int b = (int)((long)i * bucketCount / peaks.Count);
                    if (b >= bucketCount)
                    {
                        b = bucketCount - 1;
                    }

                    double v = peaks[i];
                    bucketSums[b] += v * v;
                    bucketCounts[b]++;
                }

                for (int b = 0; b < bucketCount; b++)
                {
                    buckets[b] = bucketCounts[b] > 0 ? (float)Math.Sqrt(bucketSums[b] / bucketCounts[b]) : 0f;
                }

                // 分位数归一化:取 95 分位作为基准,去掉少数异常峰值,让起伏更明显
                var sorted = new float[buckets.Length];
                Array.Copy(buckets, sorted, buckets.Length);
                Array.Sort(sorted);
                float p95 = sorted.Length > 0 ? sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))] : 0f;
                float baseVal = Math.Max(p95, 0.01f);
                for (int i = 0; i < buckets.Length; i++)
                {
                    float v = Math.Min(1f, buckets[i] / baseVal);
                    buckets[i] = 0.08f + 0.92f * v;
                }

                StartupLog.Write("波形解码完成: " + Path.GetFileName(path) + " buckets=" + buckets.Length);
                return buckets;
            }
            catch (Exception ex)
            {
                StartupLog.Write("波形解码异常: " + ex.Message);
                return Array.Empty<float>();
            }
        }
    }
}
