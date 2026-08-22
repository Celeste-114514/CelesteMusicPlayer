using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    public sealed class TrackStatsEntry
    {
        public string FilePath { get; set; } = string.Empty;

        public bool IsFavorite { get; set; }

        /// <summary>0–5</summary>
        public int Rating { get; set; }

        public int PlayCount { get; set; }

        public double ListenSeconds { get; set; }

        public DateTime? LastPlayedUtc { get; set; }
    }

    public static class TrackStatsStore
    {
        private const string FileName = "track-stats.json";
        private static Dictionary<string, TrackStatsEntry>? _cache;
        private static readonly object Gate = new();
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

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

        public static Dictionary<string, TrackStatsEntry> Load()
        {
            lock (Gate)
            {
                if (_cache != null)
                {
                    return CloneDictionary(_cache);
                }

                try
                {
                    string path = GetFilePath();
                    if (File.Exists(path))
                    {
                        List<TrackStatsEntry>? list = JsonSerializer.Deserialize<List<TrackStatsEntry>>(File.ReadAllText(path));
                        _cache = BuildDictionary(list ?? new List<TrackStatsEntry>());
                    }
                    else
                    {
                        _cache = new Dictionary<string, TrackStatsEntry>(PathComparer);
                    }
                }
                catch
                {
                    _cache = new Dictionary<string, TrackStatsEntry>(PathComparer);
                }

                return CloneDictionary(_cache);
            }
        }

        public static void Save(Dictionary<string, TrackStatsEntry> entries)
        {
            lock (Gate)
            {
                _cache = BuildDictionary(entries.Values);
                SaveCore(_cache);
            }
        }

        private static void SaveCore(Dictionary<string, TrackStatsEntry> entries)
        {
            try
            {
                string json = JsonSerializer.Serialize(entries.Values.ToList(), new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(GetFilePath(), json);
            }
            catch
            {
            }
        }

        private static Dictionary<string, TrackStatsEntry> BuildDictionary(IEnumerable<TrackStatsEntry> entries)
        {
            var dict = new Dictionary<string, TrackStatsEntry>(PathComparer);
            foreach (TrackStatsEntry entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.FilePath))
                {
                    continue;
                }

                entry.FilePath = Path.GetFullPath(entry.FilePath);
                entry.Rating = Math.Clamp(entry.Rating, 0, 5);
                entry.PlayCount = Math.Max(0, entry.PlayCount);
                entry.ListenSeconds = Math.Max(0, entry.ListenSeconds);
                dict[entry.FilePath] = entry;
            }

            return dict;
        }

        private static Dictionary<string, TrackStatsEntry> CloneDictionary(Dictionary<string, TrackStatsEntry> source)
        {
            var clone = new Dictionary<string, TrackStatsEntry>(PathComparer);
            foreach (KeyValuePair<string, TrackStatsEntry> pair in source)
            {
                clone[pair.Key] = CloneEntry(pair.Value);
            }

            return clone;
        }

        private static TrackStatsEntry CloneEntry(TrackStatsEntry e) => new()
        {
            FilePath = e.FilePath,
            IsFavorite = e.IsFavorite,
            Rating = e.Rating,
            PlayCount = e.PlayCount,
            ListenSeconds = e.ListenSeconds,
            LastPlayedUtc = e.LastPlayedUtc
        };

        private static TrackStatsEntry GetOrCreateEntry(Dictionary<string, TrackStatsEntry> dict, string filePath)
        {
            string key = Path.GetFullPath(filePath);
            if (!dict.TryGetValue(key, out TrackStatsEntry? entry))
            {
                entry = new TrackStatsEntry { FilePath = key };
                dict[key] = entry;
            }

            return entry;
        }

        private static void Mutate(Action<Dictionary<string, TrackStatsEntry>> mutator)
        {
            Dictionary<string, TrackStatsEntry> dict = Load();
            mutator(dict);
            Save(dict);
        }

        public static TrackStatsEntry? Get(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            string key = Path.GetFullPath(filePath);
            Dictionary<string, TrackStatsEntry> dict = Load();
            return dict.TryGetValue(key, out TrackStatsEntry? entry) ? CloneEntry(entry) : null;
        }

        public static void SetFavorite(string filePath, bool isFavorite)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            Mutate(dict =>
            {
                TrackStatsEntry entry = GetOrCreateEntry(dict, filePath);
                entry.IsFavorite = isFavorite;
            });
        }

        public static bool ToggleFavorite(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            bool newValue = false;
            Mutate(dict =>
            {
                TrackStatsEntry entry = GetOrCreateEntry(dict, filePath);
                entry.IsFavorite = !entry.IsFavorite;
                newValue = entry.IsFavorite;
            });
            return newValue;
        }

        public static void SetRating(string filePath, int rating)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            rating = Math.Clamp(rating, 0, 5);
            Mutate(dict =>
            {
                TrackStatsEntry entry = GetOrCreateEntry(dict, filePath);
                entry.Rating = rating;
            });
        }

        /// <summary>按评分过滤媒体库路径。minRating 传 0 表示"未评分"（Rating==0）；1..5 表示精确评分。</summary>
        public static IReadOnlyList<string> GetPathsByRating(int ratingValue)
        {
            return Load()
                .Values
                .Where(e => ratingValue == 0 ? e.Rating == 0 : e.Rating == ratingValue)
                .Select(e => e.FilePath)
                .ToList();
        }

        public static void RecordPlayStart(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            Mutate(dict =>
            {
                TrackStatsEntry entry = GetOrCreateEntry(dict, filePath);
                entry.PlayCount++;
                entry.LastPlayedUtc = DateTime.UtcNow;
            });
        }

        private static readonly Dictionary<string, double> PendingListen = new(PathComparer);
        private static DateTime _lastListenFlushUtc = DateTime.UtcNow;

        /// <summary>累计收听秒数(内存中),每 30 秒或退出/切歌时写盘一次,避免每秒全量写盘。</summary>
        public static void AddListenSeconds(string filePath, double seconds)
        {
            if (string.IsNullOrWhiteSpace(filePath) || seconds <= 0)
            {
                return;
            }

            lock (Gate)
            {
                string key = Path.GetFullPath(filePath);
                PendingListen.TryGetValue(key, out double existing);
                PendingListen[key] = existing + seconds;
                if ((DateTime.UtcNow - _lastListenFlushUtc).TotalSeconds >= 30)
                {
                    FlushPendingListenLocked();
                }
            }
        }

        /// <summary>立即把累计的收听秒数写盘(退出/切歌时调用)。</summary>
        public static void Flush()
        {
            lock (Gate)
            {
                FlushPendingListenLocked();
            }
        }

        private static void FlushPendingListenLocked()
        {
            if (PendingListen.Count == 0)
            {
                return;
            }

            try
            {
                Dictionary<string, TrackStatsEntry> dict = Load();
                foreach (KeyValuePair<string, double> pair in PendingListen)
                {
                    TrackStatsEntry entry = GetOrCreateEntry(dict, pair.Key);
                    entry.ListenSeconds += pair.Value;
                }

                PendingListen.Clear();
                _lastListenFlushUtc = DateTime.UtcNow;
                _cache = dict;
                SaveCore(dict);
            }
            catch
            {
            }
        }

        public static IReadOnlyList<string> GetAllFavorites()
        {
            return Load()
                .Values
                .Where(e => e.IsFavorite)
                .OrderByDescending(e => e.LastPlayedUtc ?? DateTime.MinValue)
                .Select(e => e.FilePath)
                .ToList();
        }

        public static IReadOnlyList<string> GetRecentlyPlayed(int maxCount = 50, int withinDays = 0)
        {
            DateTime? cutoff = withinDays > 0
                ? DateTime.UtcNow.Date.AddDays(-(withinDays - 1))
                : null;

            return Load()
                .Values
                .Where(e => e.LastPlayedUtc.HasValue)
                .Where(e => cutoff == null || e.LastPlayedUtc >= cutoff)
                .OrderByDescending(e => e.LastPlayedUtc)
                .Take(Math.Max(1, maxCount))
                .Select(e => e.FilePath)
                .ToList();
        }

        public static void ClearRecentlyPlayed()
        {
            Mutate(dict =>
            {
                foreach (TrackStatsEntry entry in dict.Values)
                {
                    entry.LastPlayedUtc = null;
                }
            });
        }
    }
}
