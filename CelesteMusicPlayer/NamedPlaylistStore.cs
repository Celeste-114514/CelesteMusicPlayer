using System;
using System.Collections.Generic;
using System.Linq;

namespace CelesteMusicPlayer
{
    public sealed class NamedPlaylistDto
    {
        public string Name { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public List<string> Songs { get; set; } = new();
    }

    /// <summary>命名单存储：底层复用 SQLite（LibraryDb），对外接口保持不变。</summary>
    public static class NamedPlaylistStore
    {
        public const string FavoritesPlaylistName = "我喜欢的音乐";

        public static IReadOnlyList<string> List() => LibraryDb.ListPlaylists();

        public static void Create(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Playlist name is required.", nameof(name));
            }

            if (LibraryDb.PlaylistExists(name))
            {
                return;
            }

            SaveSongs(name, Array.Empty<string>());
        }

        public static void Rename(string oldName, string newName)
        {
            if (string.Equals(oldName, FavoritesPlaylistName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Built-in favorites playlist cannot be renamed.");
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new ArgumentException("New name is required.", nameof(newName));
            }

            if (!LibraryDb.PlaylistExists(oldName))
            {
                throw new System.IO.FileNotFoundException("Playlist not found.", oldName);
            }

            if (LibraryDb.PlaylistExists(newName))
            {
                throw new System.IO.IOException("A playlist with that name already exists.");
            }

            LibraryDb.RenamePlaylist(oldName, newName);
        }

        public static void Delete(string name)
        {
            if (string.Equals(name, FavoritesPlaylistName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Built-in favorites playlist cannot be deleted.");
            }

            LibraryDb.DeletePlaylist(name);
        }

        public static List<string> LoadSongs(string name)
        {
            if (string.Equals(name, FavoritesPlaylistName, StringComparison.Ordinal))
            {
                return TrackStatsStore.GetAllFavorites().ToList();
            }

            return LibraryDb.LoadPlaylistSongs(name);
        }

        public static void SaveSongs(string name, IEnumerable<string> songs)
        {
            if (string.Equals(name, FavoritesPlaylistName, StringComparison.Ordinal))
            {
                SyncFavoritesFromPaths(songs);
                return;
            }

            LibraryDb.SavePlaylist(name, songs);
        }

        public static void SyncFavoritesPlaylist()
        {
            SaveSongs(FavoritesPlaylistName, TrackStatsStore.GetAllFavorites());
        }

        /// <summary>供 SQLite 迁移阶段调用：把迁移来的收藏歌单同步到收藏统计（内部）。</summary>
        internal static void SyncFavoritesFromPathsForMigration(IEnumerable<string> songs)
        {
            SyncFavoritesFromPaths(songs);
        }

        private static void SyncFavoritesFromPaths(IEnumerable<string> songs)
        {
            var desired = songs
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(System.IO.Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, TrackStatsEntry> stats = TrackStatsStore.Load();
            foreach (TrackStatsEntry entry in stats.Values)
            {
                bool shouldFavorite = desired.Contains(entry.FilePath);
                if (entry.IsFavorite != shouldFavorite)
                {
                    TrackStatsStore.SetFavorite(entry.FilePath, shouldFavorite);
                }
            }

            foreach (string path in desired)
            {
                TrackStatsStore.SetFavorite(path, true);
            }
        }
    }
}
