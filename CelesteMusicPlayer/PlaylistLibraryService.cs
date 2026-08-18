using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>封面墙卡片背后的命名单数据（名称 + 有序歌曲路径）。</summary>
    public sealed class LibraryPlaylist
    {
        public string Name { get; set; } = string.Empty;
        public List<string> TrackFilePaths { get; set; } = new();
    }

    /// <summary>播放列表库（封面墙数据源）。底层复用 NamedPlaylistStore 持久化。</summary>
    public static class PlaylistLibraryService
    {
        /// <summary>绑定到封面墙 UI 的命名单集合。</summary>
        public static ObservableCollection<LibraryPlaylist> Items { get; } = new();

        private static bool _loaded;

        /// <summary>加载（幂等；首次调用从 NamedPlaylistStore 拉取）。</summary>
        public static void Load()
        {
            if (_loaded) return;
            Refresh();
            _loaded = true;
        }

        /// <summary>从 NamedPlaylistStore 刷新所有命名单（含歌曲数、封面来源=首曲）。</summary>
        public static void Refresh()
        {
            Items.Clear();
            foreach (string name in NamedPlaylistStore.List())
            {
                var p = new LibraryPlaylist { Name = name };
                try
                {
                    p.TrackFilePaths.AddRange(NamedPlaylistStore.LoadSongs(name));
                }
                catch
                {
                }

                Items.Add(p);
            }
        }

        /// <summary>把给定曲目另存为命名单（自动去重、去重名）。</summary>
        public static LibraryPlaylist? SaveAsNew(IEnumerable<string> paths, string baseName)
        {
            var list = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            if (list.Count == 0 || string.IsNullOrWhiteSpace(baseName)) return null;

            string candidate = baseName.Trim();
            int n = 2;
            var existing = NamedPlaylistStore.List();
            while (existing.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidate = baseName.Trim() + " (" + n + ")";
                n++;
            }

            NamedPlaylistStore.SaveSongs(candidate, list);
            Refresh();
            return Items.FirstOrDefault(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase));
        }

        public static void Remove(string name)
        {
            if (string.Equals(name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal)) return;
            NamedPlaylistStore.Delete(name);
            Refresh();
        }

        /// <summary>命中单歌曲路径（空则新建默认占位）。</summary>
        public static IReadOnlyList<string> Tracks(string name) => NamedPlaylistStore.LoadSongs(name);

        /// <summary>命中单首个存在的文件路径（选封面用）；无则 null。</summary>
        public static string? FirstExistingTrack(string name)
        {
            foreach (string path in Tracks(name))
            {
                if (System.IO.File.Exists(path)) return path;
            }
            return null;
        }

        // ---- 命中单自定义封面 ----
        private static string CoversRoot
        {
            get
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
                string dir = System.IO.Path.Combine(root, "PlaylistCovers");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string CoverFile(string name)
        {
            string safe = string.IsNullOrWhiteSpace(name) ? "playlist" : name;
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c, '_');
            }
            return System.IO.Path.Combine(CoversRoot, safe + ".jpg");
        }

        /// <summary>命中单自定义封面文件路径（未设置返回 null）。</summary>
        public static string? CustomCoverPath(string name)
        {
            try
            {
                string p = CoverFile(name);
                return System.IO.File.Exists(p) ? p : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写入命中单自定义封面。</summary>
        public static void WriteCustomCover(string name, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;
            System.IO.File.WriteAllBytes(CoverFile(name), bytes);
        }

        /// <summary>删除命中单自定义封面（恢复首曲默认）。</summary>
        public static void ClearCustomCover(string name)
        {
            try
            {
                string p = CoverFile(name);
                if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
            }
            catch
            {
            }
        }
    }
}
