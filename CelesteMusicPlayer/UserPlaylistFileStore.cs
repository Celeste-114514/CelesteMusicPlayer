using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CelesteMusicPlayer
{
    /// <summary>播放列表文件中的一首歌（按路径恢复）。</summary>
    public sealed class UserPlaylistSongDto
    {
        public string FilePath { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        public uint Year { get; set; }

        public double DurationSeconds { get; set; }
    }

    /// <summary>播放列表文件根对象。</summary>
    public sealed class UserPlaylistFileDto
    {
        public string Name { get; set; } = "播放列表";

        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

        public List<UserPlaylistSongDto> Songs { get; set; } = new();
    }

    /// <summary>播放列表本地文件：保存在软件根目录下的 PlayList 文件夹。</summary>
    public static class UserPlaylistFileStore
    {
        public const string FileExtension = ".json";

        public static string EnsurePlayListFolder()
        {
            string root = Path.Combine(AppContext.BaseDirectory, "PlayList");
            Directory.CreateDirectory(root);
            return root;
        }

        public static void SaveToPath(string filePath, UserPlaylistFileDto dto)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(filePath, json);
        }

        public static UserPlaylistFileDto? LoadFromPath(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<UserPlaylistFileDto>(json);
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "播放列表";
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name.Trim();
        }
    }
}
