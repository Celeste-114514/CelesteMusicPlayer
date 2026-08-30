using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// SACD (.iso) 抽取助手：调用外部 sacd_extract.exe 把 SACD 镜像解成逐轨 DSD(DSF) 文件，
    /// 解出的 DSF 直接复用项目既有的 DSD 解码 / DoP 直出 / PCM 转码全链路（bit-perfect 不变）。
    ///
    /// 设计要点：
    /// - 引擎层完全不感知 .iso，只在「播放」时由 MainWindow 懒抽取并就地展开为逐轨 DSF 队列项，
    ///   因此 DoP / 波形 / 续播书签 / 队列持久化等能力对 SACD 全部免费生效。
    /// - sacd_extract.exe 是外部二进制（体积/许可原因不随包发布），需用户自行放到
    ///   Assets\sacd\ 或程序目录；缺失时返回空列表并写日志，由上层给出友好提示。
    /// </summary>
    internal static class SacdIsoExtractor
    {
        /// <summary>该路径是否为 SACD 镜像（.iso）。</summary>
        public static bool IsSacdIso(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>查找 sacd_extract.exe：优先 Assets\sacd\，其次程序目录；不存在返回 null。</summary>
        public static string? FindSacdExtract()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "sacd", "sacd_extract.exe"),
                Path.Combine(AppContext.BaseDirectory, "sacd_extract.exe")
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

        /// <summary>
        /// 把 ISO 解成逐轨 DSF，返回按文件名排序的 DSF 路径列表（失败 / 缺工具 / 不支持返回空列表）。
        /// 结果按 ISO 内容指纹缓存到 %LOCALAPPDATA%\CelesteMusicPlayer\SacdCache，重复播放不重复抽取。
        /// </summary>
        public static async Task<IReadOnlyList<string>> ExtractTracksAsync(string isoPath, Action<string>? status)
        {
            IReadOnlyList<string> empty = Array.Empty<string>();
            try
            {
                string? exe = FindSacdExtract();
                if (exe == null)
                {
                    StartupLog.Write("SACD: 未找到 sacd_extract.exe，无法播放 .iso（请将其放到 Assets\\sacd\\）");
                    return empty;
                }

                if (!File.Exists(isoPath))
                {
                    return empty;
                }

                string outDir = Path.Combine(GetSacdCacheDir(), "sacd_" + HashPath(isoPath));

                // 已抽取过且仍有 DSF：直接复用（不重复跑外部进程）
                var existing = CollectDsf(outDir);
                if (existing.Count > 0)
                {
                    status?.Invoke("正在播放 SACD（已抽取）…");
                    return existing;
                }

                Directory.CreateDirectory(outDir);

                // 第一次尝试立体声轨(-2)；若立体声为空（仅多声道碟）回退多声道(-m)
                if (!await RunExtractAsync(exe, isoPath, outDir, stereo: true, status).ConfigureAwait(false)
                    || CollectDsf(outDir).Count == 0)
                {
                    await RunExtractAsync(exe, isoPath, outDir, stereo: false, status).ConfigureAwait(false);
                }

                var result = CollectDsf(outDir);
                if (result.Count == 0)
                {
                    StartupLog.Write("SACD: 抽取未产生任何 DSF 文件: " + isoPath);
                }

                return result;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SacdIsoExtractor.cs", caught);
                return empty;
            }
        }

        private static async Task<bool> RunExtractAsync(string exe, string isoPath, string outDir, bool stereo, Action<string>? status)
        {
            string channels = stereo ? "-2" : "-m";
            // -2/-m 声道；-s 输出 Sony DSF；-c DST→DSD；-W 覆盖已存在；-i 输入；-o 输出目录
            string args = string.Format("{0} -s -c -W -i \"{1}\" -o \"{2}\"", channels, isoPath, outDir);
            status?.Invoke("正在抽取 SACD（" + (stereo ? "立体声" : "多声道") + "）…");
            StartupLog.Write("SACD extract: " + exe + " " + args);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = outDir,
                Arguments = args
            };

            try
            {
                using var proc = Process.Start(psi)!;
                string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                if (proc.ExitCode != 0)
                {
                    StartupLog.Write("SACD extract 失败(exit=" + proc.ExitCode + "): " + stderr.Trim());
                    return false;
                }

                return true;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SacdIsoExtractor.cs", caught);
                return false;
            }
        }

        private static List<string> CollectDsf(string outDir)
        {
            if (!Directory.Exists(outDir))
            {
                return new List<string>();
            }

            return Directory.EnumerateFiles(outDir, "*.dsf", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string GetSacdCacheDir()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(baseDir, "CelesteMusicPlayer", "SacdCache");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>ISO 内容指纹（路径+大小+修改时间），使同一文件改过内容后缓存自动失效。</summary>
        private static string HashPath(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                string raw = path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                return System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(raw)).Select(b => b.ToString("x2")).Take(8)
                    .Aggregate(string.Empty, (a, b) => a + b);
            }
            catch
            {
                return Path.GetFileNameWithoutExtension(path);
            }
        }
    }
}
