using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网络音乐本地缓存。
    ///
    /// 设计要点（也是本方案最关键的一条）：**先把网络文件完整下载到本地磁盘，再交给现有播放链路**。
    /// 这样播放引擎、DSP、bit-perfect 判断、标签解析、封面读取统统把它当普通本地文件，
    /// 一行都不用改 —— 换取的是首次播放要等一会儿（一首 40MB 的 FLAC 视网速几秒到几十秒）。
    ///
    /// 为什么不直接流式播 http:// —— 那会打乱现有架构：精确 seek、无缝预载、bit-perfect 判定
    /// 全都建立在"本地可随机读取"之上，而且 MediaPlayer 不支持 WebDAV 认证。
    ///
    /// 缓存目录结构：&lt;根&gt;\&lt;主机名&gt;\&lt;路径哈希前16位&gt;_&lt;原文件名&gt;
    ///   · 保留原文件名和扩展名 —— 扩展名决定播放引擎/ffmpeg 怎么解码，不能丢。
    ///   · 加路径哈希前缀 —— 不同目录下的同名文件不会互相覆盖。
    ///
    /// 清理策略：超过上限（默认 4GB）时按"最久未访问"先删，每次下载完检查一次。
    /// </summary>
    internal static class WebDavCache
    {
        /// <summary>同一文件的并发下载只跑一次（用户连点两下、或预载与点击撞上）。</summary>
        private static readonly Dictionary<string, SemaphoreSlim> Gates =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly object GateLock = new();

        /// <summary>默认缓存根目录。</summary>
        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CelesteMusicPlayer",
            "WebDavCache");

        /// <summary>实际使用的缓存根目录（设置里为空就用默认）。</summary>
        public static string GetRoot(WebDavLocation location)
        {
            string custom = AppSettingsStore.Load().WebDavCacheDir;
            string root = string.IsNullOrWhiteSpace(custom) ? DefaultRoot : custom.Trim();
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("WebDavCache.cs", caught);
            }

            return root;
        }

        /// <summary>某个网络位置对应的缓存子目录（按主机名分组，方便用户自己去看/手动清）。</summary>
        public static string GetLocationFolder(WebDavLocation location)
        {
            string host = location.ShortName;
            foreach (char bad in Path.GetInvalidFileNameChars())
            {
                host = host.Replace(bad, '_');
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                host = "server";
            }

            return Path.Combine(GetRoot(location), host);
        }

        /// <summary>算出某个远程文件的本地缓存路径（不保证文件已存在）。</summary>
        public static string GetCachePath(WebDavLocation location, string relativePath)
        {
            string rel = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');

            string name = Path.GetFileName(rel);
            foreach (char bad in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(bad, '_');
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = "track";
            }

            return Path.Combine(GetLocationFolder(location), Hash(rel) + "_" + name);
        }

        /// <summary>短哈希：同一路径恒定，换服务器/换目录不会撞。</summary>
        private static string Hash(string text)
        {
            byte[] bytes = SHA1.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            var sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 是否已缓存。判据：文件存在，且（服务器给了大小时）大小一致 ——
        /// 大小是免费的完整性校验，能挡住"上次下到一半断了"的残file。
        /// </summary>
        public static bool IsCached(WebDavLocation location, WebDavEntry entry, out string localPath)
        {
            localPath = GetCachePath(location, entry.RelativePath);

            var info = new FileInfo(localPath);
            if (!info.Exists || info.Length <= 0)
            {
                return false;
            }

            if (entry.Size > 0 && info.Length != entry.Size)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 取本地路径：已缓存直接返回（并刷新访问时间），否则下载。
        /// </summary>
        public static async Task<string> GetOrDownloadAsync(
            WebDavLocation location, WebDavEntry entry,
            IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (IsCached(location, entry, out string cached))
            {
                Touch(cached);
                progress?.Report(1.0);
                return cached;
            }

            string localPath = GetCachePath(location, entry.RelativePath);
            SemaphoreSlim gate = GetGate(localPath);

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 等锁期间可能已被另一路下完，再查一次，避免重复下载。
                if (IsCached(location, entry, out cached))
                {
                    Touch(cached);
                    progress?.Report(1.0);
                    return cached;
                }

                using WebDavClient client = location.CreateClient();
                await client.DownloadAsync(entry.RelativePath, localPath, progress, ct).ConfigureAwait(false);

                Touch(localPath);
                return localPath;
            }
            finally
            {
                gate.Release();
            }
        }

        private static SemaphoreSlim GetGate(string key)
        {
            lock (GateLock)
            {
                if (!Gates.TryGetValue(key, out SemaphoreSlim? gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    Gates[key] = gate;
                }

                return gate;
            }
        }

        /// <summary>刷新"最后访问时间"，清理时以它为准。</summary>
        private static void Touch(string path)
        {
            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("WebDavCache.cs", caught);
            }
        }

        /// <summary>缓存占用的字节数与文件数。</summary>
        public static (long Bytes, int Count) GetUsage(WebDavLocation location)
        {
            long total = 0;
            int count = 0;

            try
            {
                string folder = GetLocationFolder(location);
                if (!Directory.Exists(folder))
                {
                    return (0, 0);
                }

                foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(f).Length;
                        count++;
                    }
                    catch
                    {
                        // 单个文件统计失败不影响整体（可能正被别的进程占用）。
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("WebDavCache.cs", caught);
            }

            return (total, count);
        }

        /// <summary>把占用数字变成人话（"1.2 GB / 3 个文件"）。</summary>
        public static string DescribeUsage(WebDavLocation location)
        {
            (long bytes, int count) = GetUsage(location);
            if (count == 0)
            {
                return "暂无缓存";
            }

            return FormatSize(bytes) + " · " + count + " 个文件";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            double kb = bytes / 1024.0;
            if (kb < 1024)
            {
                return kb.ToString("0.#") + " KB";
            }

            double mb = kb / 1024.0;
            if (mb < 1024)
            {
                return mb.ToString("0.#") + " MB";
            }

            return (mb / 1024.0).ToString("0.##") + " GB";
        }

        /// <summary>
        /// 超出上限时按最久未访问顺序删，直到降到上限以内。
        /// 每次下载完调一次 —— 摊销成本，用户不会感觉到。
        /// </summary>
        public static void EnforceLimit(WebDavLocation location)
        {
            try
            {
                int limitMb = AppSettingsStore.Load().WebDavCacheLimitMb;
                if (limitMb <= 0)
                {
                    return;
                }

                long limit = (long)limitMb * 1024 * 1024;
                string folder = GetLocationFolder(location);
                if (!Directory.Exists(folder))
                {
                    return;
                }

                var files = new List<FileInfo>();
                foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        files.Add(new FileInfo(f));
                    }
                    catch
                    {
                        // 忽略：统计不到的文件不参与清理决策。
                    }
                }

                long total = files.Sum(f => f.Length);
                if (total <= limit)
                {
                    return;
                }

                foreach (FileInfo f in files.OrderBy(f => f.LastAccessTimeUtc))
                {
                    if (total <= limit)
                    {
                        break;
                    }

                    long size = f.Length;
                    try
                    {
                        f.Delete();
                        total -= size;
                        StartupLog.Write("WebDav 缓存清理: " + f.Name);
                    }
                    catch
                    {
                        // 正在播放的那个文件可能被占用，跳过即可（下次再来）。
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("WebDavCache.cs", caught);
            }
        }

        /// <summary>清空缓存，返回删掉的文件数。</summary>
        public static int Clear(WebDavLocation location)
        {
            int removed = 0;

            try
            {
                string folder = GetLocationFolder(location);
                if (!Directory.Exists(folder))
                {
                    return 0;
                }

                foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(f);
                        removed++;
                    }
                    catch
                    {
                        // 正在播放中的文件删不掉 —— 跳过，别让整个清理失败。
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("WebDavCache.cs", caught);
            }

            return removed;
        }
    }
}
