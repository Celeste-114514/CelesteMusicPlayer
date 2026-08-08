using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    public sealed class NamedPlaylistDto
    {
        public string Name { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        public List<string> Songs { get; set; } = new();
    }

    public static class NamedPlaylistStore
    {
        public const string FavoritesPlaylistName = "我喜欢的音乐";

        private static string GetPlaylistsFolder()
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

            string folder = Path.Combine(root, "Playlists");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string GetPlaylistPath(string name)
        {
            string safe = SanitizeFileName(name);
            return Path.Combine(GetPlaylistsFolder(), safe + ".json");
        }

        public static IReadOnlyList<string> List()
        {
            string folder = GetPlaylistsFolder();
            var names = new List<string>();
            foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    NamedPlaylistDto? dto = JsonSerializer.Deserialize<NamedPlaylistDto>(File.ReadAllText(file));
                    names.Add(dto?.Name ?? Path.GetFileNameWithoutExtension(file));
                }
                catch
                {
                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }

            if (!names.Contains(FavoritesPlaylistName, StringComparer.Ordinal))
            {
                names.Insert(0, FavoritesPlaylistName);
            }

            return names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static void Create(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Playlist name is required.", nameof(name));
            }

            string path = GetPlaylistPath(name);
            if (File.Exists(path))
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

            string oldPath = GetPlaylistPath(oldName);
            string newPath = GetPlaylistPath(newName);
            if (!File.Exists(oldPath))
            {
                throw new FileNotFoundException("Playlist not found.", oldPath);
            }

            if (File.Exists(newPath))
            {
                throw new IOException("A playlist with that name already exists.");
            }

            List<string> songs = LoadSongs(oldName);
            File.Delete(oldPath);
            SaveSongs(newName, songs);
        }

        public static void Delete(string name)
        {
            if (string.Equals(name, FavoritesPlaylistName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Built-in favorites playlist cannot be deleted.");
            }

            string path = GetPlaylistPath(name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public static List<string> LoadSongs(string name)
        {
            if (string.Equals(name, FavoritesPlaylistName, StringComparison.Ordinal))
            {
                return TrackStatsStore.GetAllFavorites().ToList();
            }

            string path = GetPlaylistPath(name);
            if (!File.Exists(path))
            {
                return new List<string>();
            }

            try
            {
                NamedPlaylistDto? dto = JsonSerializer.Deserialize<NamedPlaylistDto>(File.ReadAllText(path));
                return dto?.Songs?
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void SaveSongs(string name, IEnumerable<string> songs)
        {
            if (string.Equals(name, FavoritesPlaylistName, StringComparison.Ordinal))
            {
                SyncFavoritesFromPaths(songs);
                return;
            }

            var dto = new NamedPlaylistDto
            {
                Name = name,
                UpdatedAt = DateTimeOffset.UtcNow,
                Songs = songs?
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    ?? new List<string>()
            };

            string path = GetPlaylistPath(name);
            string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static void SyncFavoritesPlaylist()
        {
            SaveSongs(FavoritesPlaylistName, TrackStatsStore.GetAllFavorites());
        }

        private static void SyncFavoritesFromPaths(IEnumerable<string> songs)
        {
            HashSet<string> desired = songs
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(Path.GetFullPath)
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

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "playlist";
            }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name.Trim();
        }
    }
}
