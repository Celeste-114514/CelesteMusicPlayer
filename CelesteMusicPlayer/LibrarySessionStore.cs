using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.Storage;

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

    /// <summary>把上次读取的音频/文件夹路径存到本地，下次启动恢复。</summary>
    public static class LibrarySessionStore
    {
        private const string FileName = "last-library.json";

        private static string GetFilePath()
        {
            string root;
            try
            {
                root = ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
            }

            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static void Save(LibrarySessionState state)
        {
            try
            {
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(GetFilePath(), json);
            }
            catch
            {
                // 持久化失败不影响播放
            }
        }

        public static void SaveFolder(string folderPath, IEnumerable<string> filePaths)
        {
            Save(new LibrarySessionState
            {
                Mode = "folder",
                FolderPath = folderPath,
                FilePaths = new List<string>(filePaths)
            });
        }

        public static void SaveFiles(IEnumerable<string> filePaths)
        {
            // 保留已选浏览文件夹，避免「选文件」后文件夹分类丢失根目录
            string? folderPath = TryLoad()?.FolderPath;
            Save(new LibrarySessionState
            {
                Mode = "files",
                FolderPath = folderPath,
                FilePaths = new List<string>(filePaths)
            });
        }

        public static LibrarySessionState? TryLoad()
        {
            try
            {
                string path = GetFilePath();
                if (!File.Exists(path))
                {
                    return null;
                }

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<LibrarySessionState>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
