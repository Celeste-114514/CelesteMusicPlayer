using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    /// <summary>智能播放列表规则类型。</summary>
    public enum SmartPlaylistKind
    {
        Rating,        // 评分 ≥ N 星
        Genre,         // 按流派
        Artist,        // 按专辑艺术家
        Decade,        // 按年代（如 1990 = 1990-1999）
        MostPlayed,    // 最常播放（Top N）
        RecentlyAdded  // 最近加入（Top N，以文件 mtime 作代理）
    }

    /// <summary>一条智能播放列表定义（规则 + 名称 + 数量上限）。</summary>
    public sealed class SmartPlaylistDef
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public SmartPlaylistKind Kind { get; set; } = SmartPlaylistKind.Rating;
        public string Argument { get; set; } = string.Empty;
        public int Limit { get; set; } = 100;

        public SmartPlaylistDef Clone() => new()
        {
            Id = Id,
            Name = Name,
            Kind = Kind,
            Argument = Argument,
            Limit = Limit
        };
    }

    /// <summary>
    /// 智能播放列表存储：定义列表持久化到 %LOCALAPPDATA%\CelesteMusicPlayer\smart-playlists.json。
    /// 仅读写配置 JSON，不触碰音频输出字节流。规则在「播放」时按当前曲库实时解析（随库更新）。
    /// </summary>
    public static class SmartPlaylistStore
    {
        private const string FileName = "smart-playlists.json";
        private static readonly object Gate = new();

        private sealed class SmartPlaylistStoreState
        {
            public List<SmartPlaylistDef> Defs { get; set; } = new();
        }

        private static SmartPlaylistStoreState _cache;

        private static string GetFilePath()
        {
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        public static List<SmartPlaylistDef> LoadAll()
        {
            lock (Gate)
            {
                if (_cache == null)
                {
                    _cache = JsonFile.Read(GetFilePath(), new SmartPlaylistStoreState());
                    _cache.Defs ??= new List<SmartPlaylistDef>();
                }

                return JsonFile.DeepClone(_cache.Defs);
            }
        }

        public static void SaveAll(List<SmartPlaylistDef> defs)
        {
            lock (Gate)
            {
                _cache = new SmartPlaylistStoreState
                {
                    Defs = defs?.Select(d => d.Clone()).ToList() ?? new List<SmartPlaylistDef>()
                };
                JsonFile.Write(GetFilePath(), _cache);
            }
        }

        public static void Upsert(SmartPlaylistDef def)
        {
            if (def == null) return;
            var all = LoadAll();
            int idx = all.FindIndex(d => d.Id == def.Id);
            if (idx >= 0) all[idx] = def.Clone();
            else all.Add(def.Clone());
            SaveAll(all);
        }

        public static void Remove(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var all = LoadAll();
            all.RemoveAll(d => d.Id == id);
            SaveAll(all);
        }

        /// <summary>规则的人类可读描述。</summary>
        public static string Describe(SmartPlaylistDef def)
        {
            if (def == null) return string.Empty;
            switch (def.Kind)
            {
                case SmartPlaylistKind.Rating: return "评分 ≥ " + (string.IsNullOrWhiteSpace(def.Argument) ? "?" : def.Argument) + " 星";
                case SmartPlaylistKind.Genre: return "流派 = " + (def.Argument.Length == 0 ? "?" : def.Argument);
                case SmartPlaylistKind.Artist: return "艺术家 = " + (def.Argument.Length == 0 ? "?" : def.Argument);
                case SmartPlaylistKind.Decade: return "年代 = " + (def.Argument.Length == 0 ? "?" : def.Argument) + "s";
                case SmartPlaylistKind.MostPlayed: return "最常播放（Top " + def.Limit + "）";
                case SmartPlaylistKind.RecentlyAdded: return "最近加入（Top " + def.Limit + "）";
                default: return def.Kind.ToString();
            }
        }

        /// <summary>按规则实时解析出曲目路径列表（每次调用都查当前曲库，随库更新）。</summary>
        public static List<string> Resolve(SmartPlaylistDef def)
        {
            if (def == null) return new List<string>();
            int limit = def.Limit > 0 ? def.Limit : 200;
            try
            {
                switch (def.Kind)
                {
                    case SmartPlaylistKind.Rating:
                        {
                            int minRating = int.TryParse(def.Argument, out int mr) && mr >= 1 ? mr : 1;
                            var lib = LibraryDb.GetAllTrackPaths();
                            var rated = new List<string>();
                            for (int r = minRating; r <= 5; r++)
                            {
                                rated.AddRange(TrackStatsStore.GetPathsByRating(r));
                            }

                            return rated.Where(p => lib.Contains(p)).Take(limit).ToList();
                        }

                    case SmartPlaylistKind.Genre:
                        return LibraryDb.GetTrackPathsByGenre(def.Argument, limit);

                    case SmartPlaylistKind.Artist:
                        return LibraryDb.GetTrackPathsByArtist(def.Argument, limit);

                    case SmartPlaylistKind.Decade:
                        {
                            int dec = int.TryParse(def.Argument, out int d) && d >= 1900 ? d : 1990;
                            return LibraryDb.GetTrackPathsByDecade(dec, limit);
                        }

                    case SmartPlaylistKind.MostPlayed:
                        {
                            var lib = LibraryDb.GetAllTrackPaths();
                            return TrackStatsStore.Load()
                                .Where(e => e.Value.PlayCount > 0 && lib.Contains(e.Value.FilePath))
                                .OrderByDescending(e => e.Value.PlayCount)
                                .Select(e => e.Value.FilePath)
                                .Take(limit)
                                .ToList();
                        }

                    case SmartPlaylistKind.RecentlyAdded:
                        return LibraryDb.GetRecentTrackPaths(limit);

                    default:
                        return new List<string>();
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SmartPlaylistStore.Resolve", caught);
                return new List<string>();
            }
        }
    }
}
