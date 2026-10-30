using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>目录里的一项（子文件夹或音频文件）。不依赖界面类型，服务层可以直接用。</summary>
    internal readonly record struct FolderScanEntry(string DisplayName, string FullPath, bool IsFolder, int Depth);

    /// <summary>
    /// 磁盘侧三件事：认得哪些扩展名、怎么走目录、同一批曲目怎么排。
    ///
    /// W13：从 <c>MainWindow.Library.cs</c> / <c>MainWindow.Playback.cs</c> 搬出来。
    /// 原生曲库扫描与网页目录命令共用这一份 —— 两边各写一份的话，迟早一边加了新格式另一边看不到。
    /// 这里只用 <see cref="LibraryPathGuard"/>（同样是纯判断），不碰界面、不碰播放。
    /// </summary>
    internal static class LibraryFolderScan
    {
        /// <summary>受支持的音频扩展名（含 DSD 与老式模块格式）。</summary>
        internal static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".m4a", ".flac", ".wma", ".ogg", ".aac",
            ".ape", ".wv", ".tta", ".mpc", ".tak", ".opus",
            ".dsf", ".dff",
            ".mp2", ".amr", ".au", ".mod", ".s3m", ".xm"
        };

        // ---- 递归收集 ---------------------------------------------------------------

        /// <summary>递归枚举文件夹内所有音频路径（顺序按文件系统返回，不排序）。</summary>
        internal static IEnumerable<string> AudioFiles(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                yield break;
            }

            // 内部缓存目录（DSD DoP 缓存 / 转码缓存 / WebDAV 缓存）不是用户音乐，一律不扫
            if (LibraryPathGuard.IsInternalCacheDir(folderPath))
            {
                yield break;
            }

            // 逐目录递归:某个受保护子目录(无权限)只跳过该目录,不影响其它文件
            foreach (string path in AudioFilesRecursive(folderPath))
            {
                yield return path;
            }
        }

        private static IEnumerable<string> AudioFilesRecursive(string folder)
        {
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(folder);
                files = Directory.GetFiles(folder);
            }
            catch
            {
                yield break;
            }

            foreach (string path in files)
            {
                string ext = Path.GetExtension(path);
                if (AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                    && !LibraryPathGuard.IsLibraryExcludedFile(path))
                {
                    yield return path;
                }
            }

            foreach (string sub in subDirs)
            {
                if (LibraryPathGuard.IsInternalCacheDir(sub))
                {
                    continue;
                }

                foreach (string path in AudioFilesRecursive(sub))
                {
                    yield return path;
                }
            }
        }

        /// <summary>深度优先：子文件夹顺序与界面一致，文件夹内音频按文件名排序。</summary>
        internal static List<string> AudioFilesRecursiveOrdered(string folderPath)
        {
            var result = new List<string>();
            CollectAudioFilesRecursiveOrdered(folderPath, result);
            return result;
        }

        private static void CollectAudioFilesRecursiveOrdered(string folderPath, List<string> sink)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            // 内部缓存目录（DSD DoP 缓存 / 转码缓存 / WebDAV 缓存）不是用户音乐，一律跳过
            if (LibraryPathGuard.IsInternalCacheDir(folderPath))
            {
                return;
            }

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    try
                    {
                        System.IO.FileAttributes attrs = System.IO.File.GetAttributes(dir);
                        if ((attrs & System.IO.FileAttributes.Hidden) != 0 ||
                            (attrs & System.IO.FileAttributes.System) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    CollectAudioFilesRecursiveOrdered(dir, sink);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("LibraryFolderScan.cs", caught); }

            try
            {
                foreach (string file in Directory.EnumerateFiles(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string ext = Path.GetExtension(file);
                    if (AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                        && !LibraryPathGuard.IsLibraryExcludedFile(file))
                    {
                        sink.Add(file);
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("LibraryFolderScan.cs", caught); }
        }

        // ---- 列一层（给目录树用，不递归） -------------------------------------------

        /// <summary>枚举一层：子文件夹 + 支持的音频文件（不递归）。隐藏/系统项跳过。</summary>
        internal static List<FolderScanEntry> Children(string folderPath, int depth)
        {
            var result = new List<FolderScanEntry>();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return result;
            }

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    try
                    {
                        System.IO.FileAttributes attrs = System.IO.File.GetAttributes(dir);
                        if ((attrs & System.IO.FileAttributes.Hidden) != 0 ||
                            (attrs & System.IO.FileAttributes.System) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    result.Add(new FolderScanEntry(name, dir, true, depth));
                }
            }
            catch
            {
                // 无权限等：跳过子目录
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string ext = Path.GetExtension(file);
                    if (!AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new FolderScanEntry(Path.GetFileName(file), file, false, depth));
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("LibraryFolderScan.cs", caught); }

            return result;
        }

        // ---- 排序 -------------------------------------------------------------------

        /// <summary>
        /// 一批曲目按「碟号 → 轨道号 → 标题」排；整组都没有轨道号时退回标题排序
        /// （比纯文件名字母序更接近用户预期）。
        /// </summary>
        internal static List<PlaylistItem> OrderAlbumTracks(List<PlaylistItem> tracks)
        {
            bool anyTrack = tracks.Any(t => t.Track > 0);
            if (!anyTrack)
            {
                return tracks
                    .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            return tracks
                .OrderBy(t => t.Track == 0 ? 1 : 0)          // 有轨道号的在前
                .ThenBy(t => t.Disc)                         // 碟号
                .ThenBy(t => t.Track)                        // 轨道号
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
