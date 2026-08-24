using System;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    public sealed class PlaybackSessionState
    {
        public string? FilePath { get; set; }
        public double PositionSeconds { get; set; }
    }

    /// <summary>记住上次正在播放的曲目，下次启动时展示（暂停在上次进度）。</summary>
    public static class PlaybackSessionStore
    {
        private const string FileName = "last-playback.json";

        private static string GetFilePath()
        {
            // 与主设置(AppSettingsStore)同源：固定 %LOCALAPPDATA%\CelesteMusicPlayer，
            // 规避 packaged 下 ApplicationData.Current.LocalFolder 路径漂浮导致"保存到 A/读到 B"使续播失效。
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static void Save(string? filePath, double positionSeconds)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    return;
                }

                var state = new PlaybackSessionState
                {
                    FilePath = filePath,
                    PositionSeconds = Math.Max(0, positionSeconds)
                };
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetFilePath(), json);
            }
            catch
            {
            }
        }

        public static PlaybackSessionState? TryLoad()
        {
            try
            {
                string path = GetFilePath();
                if (!File.Exists(path))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<PlaybackSessionState>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }
    }
}
