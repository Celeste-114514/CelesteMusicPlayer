using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 音乐库路径守卫：把应用自己的内部缓存目录挡在音乐库之外 ——
    /// 扫描、会话恢复、手动添加，所有「进媒体库」的入口统一过滤。
    ///
    /// 背景（2026-09-26 用户报 bug）：DSD 预加载缓存（默认 D:\CelesteDsdCache）里的
    /// *.dop24.wav 曾被写进曲库会话（library_session.file_path），于是每次重启都把
    /// 这批无标签缓存 WAV 恢复进音乐库，专辑艺术家页多出一个「未知艺术家」。
    /// 用户把缓存文件夹手动加进媒体库再删除只能清掉当次列表，重启后又被会话恢复回来。
    /// 这些文件是程序内部产物（字节内容就是 DoP 帧流，不能当普通音乐管理），
    /// 任何时候都不该被扫描 / 收录 / 持久化进曲库。
    ///
    /// 两级口径（2026-09-26 用户明确要求）：
    ///   · 纯内部产物（DSD DoP 缓存 / FFmpeg 转码缓存）—— 哪儿都不去：
    ///     媒体库、曲库会话、播放队列持久化，全部挡住。
    ///   · WebDAV 下载缓存 —— 不进媒体库（网络音乐归网络音乐库视图管，
    ///     本地曲库只收用户自己的音乐文件夹），但允许留在播放队列里
    ///     （那是用户自己选来播的曲目；队列是瞬态播放状态，不是曲库）。
    /// </summary>
    internal static class LibraryPathGuard
    {
        /// <summary>DSD 整轨预加载缓存文件扩展名（DoP24 紧凑容器 WAV）。</summary>
        public const string DopCacheExtension = ".dop24.wav";

        /// <summary>
        /// 缓存根目录快照。用原子引用而非锁：AppSettingsStore.Changed 在设置保存持锁期内回调，
        /// 若这里也用锁，会和「算根目录 → 读设置」形成锁序交叉，有死锁风险。
        /// 竞态下最坏只是重复算一次，无害。
        /// </summary>
        private static List<string>? _cachedRoots;

        static LibraryPathGuard()
        {
            // 设置变更（用户改了缓存目录）后立即失效重算
            AppSettingsStore.Changed += () => Volatile.Write(ref _cachedRoots, null);
        }

        private static string Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.Trim();
            try
            {
                trimmed = Path.GetFullPath(trimmed);
            }
            catch
            {
                // 非法路径按原样比较
            }

            return Path.TrimEndingDirectorySeparator(trimmed);
        }

        /// <summary>WebDAV 下载缓存实际根目录（设置里为空就用默认）。</summary>
        private static string WebDavRoot
        {
            get
            {
                string? custom = AppSettingsStore.Load().WebDavCacheDir;
                return Normalize(string.IsNullOrWhiteSpace(custom) ? WebDavCache.DefaultRoot : custom);
            }
        }

        /// <summary>纯内部缓存根目录：DSD DoP 缓存 + FFmpeg 转码缓存。</summary>
        public static IReadOnlyList<string> CacheRoots
        {
            get
            {
                List<string>? cached = Volatile.Read(ref _cachedRoots);
                if (cached != null)
                {
                    return cached;
                }

                var roots = new List<string>();

                void Add(string? p)
                {
                    string n = Normalize(p);
                    if (n.Length > 0 && !roots.Any(r => string.Equals(r, n, StringComparison.OrdinalIgnoreCase)))
                    {
                        roots.Add(n);
                    }
                }

                // DSD 整轨 DoP WAV 缓存（默认 D:\CelesteDsdCache，设置里可改）
                Add(DsdPreloadService.CacheRoot);

                // FFmpeg 转码缓存（%LOCALAPPDATA%\CelesteMusicPlayer\TranscodeCache）
                Add(FfmpegDecoderBackend.TranscodeCacheDir);

                Volatile.Write(ref _cachedRoots, roots);
                return roots;
            }
        }

        /// <summary>应被挡在媒体库之外的缓存根目录全集（DSD / 转码 / WebDAV）。</summary>
        public static IReadOnlyList<string> LibraryExcludedRoots
        {
            get
            {
                var all = new List<string>(CacheRoots);
                string webdav = WebDavRoot;
                if (webdav.Length > 0 && !all.Any(r => string.Equals(r, webdav, StringComparison.OrdinalIgnoreCase)))
                {
                    all.Add(webdav);
                }

                return all;
            }
        }

        private static bool IsUnderAnyRoot(string normalized)
        {
            foreach (string root in CacheRoots)
            {
                if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>该目录是否是（或位于）内部缓存目录（DSD / 转码 / WebDAV 缓存）。</summary>
        public static bool IsInternalCacheDir(string? dir)
        {
            string n = Normalize(dir);
            if (n.Length == 0)
            {
                return false;
            }

            if (IsUnderAnyRoot(n))
            {
                return true;
            }

            string webdav = WebDavRoot;
            return webdav.Length > 0
                && (string.Equals(n, webdav, StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith(webdav + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 纯内部产物：DSD DoP 缓存文件 / 转码缓存 / 任何 *.dop24.wav。
        /// 这些连播放队列都不进（用户没有理由直接播它们）。
        /// </summary>
        public static bool IsInternalCacheFile(string? path)
        {
            string n = Normalize(path);
            if (n.Length == 0)
            {
                return false;
            }

            // 名字兜底：不管缓存目录配在哪，*.dop24.wav 一律认作内部产物
            if (n.EndsWith(DopCacheExtension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IsUnderAnyRoot(n);
        }

        /// <summary>
        /// WebDAV 下载缓存文件（用户的网络音乐库内容，本地唯一副本）。
        /// 不进媒体库，但允许留在播放队列。
        /// </summary>
        public static bool IsWebDavCacheFile(string? path)
        {
            string n = Normalize(path);
            if (n.Length == 0 || IsInternalCacheFile(n))
            {
                return false;
            }

            string root = WebDavRoot;
            if (root.Length == 0)
            {
                return false;
            }

            return string.Equals(n, root, StringComparison.OrdinalIgnoreCase)
                || n.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 是否应被挡在媒体库之外：纯内部产物 + WebDAV 下载缓存。
        /// 曲库扫描、会话恢复、手动添加、标签索引，统一用这个口径。
        /// </summary>
        public static bool IsLibraryExcludedFile(string? path)
        {
            return IsInternalCacheFile(path) || IsWebDavCacheFile(path);
        }
    }
}
