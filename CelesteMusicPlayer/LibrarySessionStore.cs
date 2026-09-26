using System;
using System.Collections.Generic;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>上次打开的音频来源：文件夹或文件列表。</summary>
    public sealed class LibrarySessionState
    {
        /// <summary>"folder" 或 "files"</summary>
        public string Mode { get; set; } = "files";

        public string? FolderPath { get; set; }

        public List<string> FilePaths { get; set; } = new();
    }

    /// <summary>把上次读取的音频/文件夹路径存到本地，下次启动恢复（底层 SQLite）。</summary>
    public static class LibrarySessionStore
    {
        /// <summary>本进程是否已做过一次内部缓存残留自愈（每个进程只跑一次）。</summary>
        private static bool _cacheHealDone;

        /// <summary>
        /// 一次性自愈：把历史版本残留的内部缓存路径（DSD DoP 缓存 / 转码缓存 WAV）
        /// 从曲库会话和标签索引里清掉。
        /// 2026-09-26 之前的版本会把 DSD 预加载缓存 WAV 存进 library_session，
        /// 导致每次重启都把缓存文件夹恢复进音乐库（专辑艺术家页多出「未知艺术家」）。
        /// 返回清掉的路径条数（0=没有残留或已清过）。
        /// </summary>
        public static int SelfHealInternalCachePaths()
        {
            if (_cacheHealDone)
            {
                return 0;
            }

            _cacheHealDone = true;

            try
            {
                // 直接读原始行（不过滤），才能发现残留
                LibrarySessionState? raw = LibraryDb.LoadSession();
                if (raw == null)
                {
                    return 0;
                }

                List<string> files = raw.FilePaths ?? new List<string>();
                List<string> clean = files
                    .Where(p => !LibraryPathGuard.IsLibraryExcludedFile(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                int removed = files.Count - clean.Count;

                string? folder = raw.FolderPath;
                bool folderWasCache = LibraryPathGuard.IsInternalCacheDir(folder);
                if (folderWasCache)
                {
                    folder = null;
                }

                if (removed > 0 || folderWasCache)
                {
                    LibraryDb.SaveSession(raw.Mode, folder, clean);
                    StartupLog.Write($"[library] 自愈：清掉曲库会话里残留的内部缓存路径 {removed} 条"
                        + (folderWasCache ? "（会话文件夹曾是缓存目录，已清除）" : string.Empty));
                }

                if (removed > 0)
                {
                    // 标签索引里同样清掉这些内部产物（SQLite tracks 表）
                    int purged = LibraryDb.PurgeInternalCacheTracks();
                    if (purged > 0)
                    {
                        StartupLog.Write($"[library] 自愈：标签索引清除内部缓存记录 {purged} 条");
                    }
                }

                // 「手动加入音乐库」记录里若残留内部缓存路径，一并清掉
                try
                {
                    AppSettingsState settings = AppSettingsStore.Load();
                    List<string> manual = settings.ManualLibraryFiles ?? new List<string>();
                    int manualRemoved = manual.RemoveAll(p => LibraryPathGuard.IsLibraryExcludedFile(p));
                    if (manualRemoved > 0)
                    {
                        settings.ManualLibraryFiles = manual;
                        AppSettingsStore.Save(settings);
                        StartupLog.Write($"[library] 自愈：手动加入记录清除内部缓存路径 {manualRemoved} 条");
                    }
                }
                catch (Exception caught)
                {
                    StartupLog.WriteException("LibrarySessionStore.SelfHealInternalCachePaths(manual)", caught);
                }

                return removed;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("LibrarySessionStore.SelfHealInternalCachePaths", caught);
                return 0;
            }
        }

        public static void Save(LibrarySessionState state)
        {
            SaveSession(state?.Mode ?? "files", state?.FolderPath, state?.FilePaths);
        }

        public static void SaveFolder(string folderPath, IEnumerable<string> filePaths)
        {
            SaveSession("folder", folderPath, filePaths);
        }

        public static void SaveFiles(IEnumerable<string> filePaths)
        {
            // 保留已选浏览文件夹，避免「选文件」后文件夹分类丢失根目录
            string? folderPath = TryLoad()?.FolderPath;
            SaveSession("files", folderPath, filePaths);
        }

        /// <summary>
        /// 保存会话：内部缓存产物（DSD DoP 缓存 / 转码缓存 / WebDAV 下载缓存）一律剔除，
        /// 防止它们被持久化后又在下次启动恢复进音乐库。
        /// </summary>
        private static void SaveSession(string mode, string? folderPath, IEnumerable<string>? filePaths)
        {
            var files = (filePaths ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Where(s => !LibraryPathGuard.IsLibraryExcludedFile(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            LibraryDb.SaveSession(mode, folderPath, files);
        }

        public static LibrarySessionState? TryLoad()
        {
            LibrarySessionState? state = LibraryDb.LoadSession();
            if (state == null)
            {
                return null;
            }

            // 读侧也过滤：历史残留（修复前写进库的）在这一层被挡住，不进音乐库
            List<string> files = state.FilePaths ?? new List<string>();
            List<string> clean = files
                .Where(p => !LibraryPathGuard.IsLibraryExcludedFile(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool folderWasCache = LibraryPathGuard.IsInternalCacheDir(state.FolderPath);

            if (clean.Count == files.Count && !folderWasCache)
            {
                return state;
            }

            return new LibrarySessionState
            {
                Mode = state.Mode,
                FolderPath = folderWasCache ? null : state.FolderPath,
                FilePaths = clean
            };
        }

        /// <summary>仅供 SQLite 迁移读取旧 JSON 会话文件（避免迁移时走新层读到空库）。</summary>
        internal static LibrarySessionState? TryLoadFromJsonFile()
        {
            try
            {
                string root;
                try
                {
                    root = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                }
                catch
                {
                    root = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CelesteMusicPlayer");
                }

                string path = System.IO.Path.Combine(root, "last-library.json");
                if (!System.IO.File.Exists(path))
                {
                    return null;
                }

                string json = System.IO.File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<LibrarySessionState>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
