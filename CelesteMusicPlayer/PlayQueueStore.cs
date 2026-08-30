using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CelesteMusicPlayer
{
    /// <summary>播放队列持久化状态：曲目路径列表 + 当前播放索引 + 当前曲进度（秒）。</summary>
    public sealed class PlayQueueState
    {
        public List<string> Paths { get; set; } = new();
        public int CurrentIndex { get; set; } = -1;
        public double PositionSeconds { get; set; }
    }

    /// <summary>记住整张播放队列，下次启动时恢复（关闭再开仍在）。</summary>
    public static class PlayQueueStore
    {
        private static string GetFilePath() => Path.Combine(AppSettingsStore.GetConfigDirectory(), "play-queue.json");

        public static PlayQueueState? TryLoad() => JsonFile.Read(GetFilePath(), (PlayQueueState?)null);

        public static void Save(PlayQueueState state)
        {
            if (state == null)
            {
                return;
            }

            JsonFile.Write(GetFilePath(), state);
        }

        public static void Clear()
        {
            try
            {
                File.Delete(GetFilePath());
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("PlayQueueStore.cs", caught); }
        }
    }
}
