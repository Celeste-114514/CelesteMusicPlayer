using System;
using System.Collections.Generic;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>逐曲续播书签状态：曲目路径 → 上次播放到的进度（秒）。</summary>
    public sealed class TrackPositionState
    {
        public Dictionary<string, double> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>为每首歌记住上次播放到的位置，下次播到该曲时自动从书签处续播。
    /// 仅在内存缓存 + 落盘，不触碰输出字节流（bit-perfect 不受影响）。</summary>
    public static class TrackPositionStore
    {
        private static readonly object Gate = new();
        private static TrackPositionState? _cache;

        private static string GetFilePath() => Path.Combine(AppSettingsStore.GetConfigDirectory(), "track-positions.json");

        private static TrackPositionState Cache()
        {
            if (_cache == null)
            {
                _cache = JsonFile.Read(GetFilePath(), new TrackPositionState());
            }

            return _cache;
        }

        public static double Get(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            lock (Gate)
            {
                TrackPositionState s = Cache();
                return s.Positions.TryGetValue(path, out double v) ? v : 0;
            }
        }

        public static void Set(string path, double seconds)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            lock (Gate)
            {
                TrackPositionState s = Cache();
                s.Positions[path] = Math.Max(0, seconds);
                JsonFile.Write(GetFilePath(), s);
            }
        }

        public static void Remove(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            lock (Gate)
            {
                TrackPositionState s = Cache();
                if (s.Positions.Remove(path))
                {
                    JsonFile.Write(GetFilePath(), s);
                }
            }
        }
    }
}
