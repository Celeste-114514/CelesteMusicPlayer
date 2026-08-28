using System;
using System.Collections.Generic;

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
        public static void Save(LibrarySessionState state)
        {
            LibraryDb.SaveSession(state?.Mode ?? "files", state?.FolderPath, state?.FilePaths);
        }

        public static void SaveFolder(string folderPath, IEnumerable<string> filePaths)
        {
            LibraryDb.SaveSession("folder", folderPath, filePaths);
        }

        public static void SaveFiles(IEnumerable<string> filePaths)
        {
            // 保留已选浏览文件夹，避免「选文件」后文件夹分类丢失根目录
            string? folderPath = TryLoad()?.FolderPath;
            LibraryDb.SaveSession("files", folderPath, filePaths);
        }

        public static LibrarySessionState? TryLoad() => LibraryDb.LoadSession();

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
