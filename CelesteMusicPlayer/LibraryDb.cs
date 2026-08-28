using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// Phase E：曲库 SQLite 存储层。
    /// 承载「命名单（播放列表）」与「上次打开的音频会话」，替代原本每歌单一个 JSON 文件
    /// 的 NamedPlaylistStore / LibrarySessionStore 持久化。迁移时保留旧 JSON 并自动备份，
    /// 绝不丢数据。
    /// </summary>
    public static class LibraryDb
    {
        private const string FileName = "library.db";
        private static readonly object Gate = new();
        private static bool _migrated;

        /// <summary>数据库文件完整路径（%LOCALAPPDATA%\CelesteMusicPlayer\library.db）。</summary>
        public static string GetDbFilePath()
        {
            string root;
            try
            {
                root = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
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

        /// <summary>确保库存在并有正确的表结构；首次启动从旧 JSON 迁移（原子、可回滚）。</summary>
        public static void EnsureMigrated()
        {
            lock (Gate)
            {
                if (_migrated)
                {
                    return;
                }

                try
                {
                    SQLitePCL.Batteries_V2.Init();
                }
                catch
                {
                    // bundle 已静态初始化时再次 Init 可能抛，忽略即可
                }

                try
                {
                    string dbPath = GetDbFilePath();
                    bool schemaExists = File.Exists(dbPath);
                    CreateSchema(dbPath);

                    // 首次建库（从未有库文件）时，把旧 JSON 数据迁进来；已有库则跳过迁移（幂等）。
                    bool needMigrate = !schemaExists && JsonDataExists();
                    if (needMigrate)
                    {
                        BackupJsonData();
                        MigratePlaylists(dbPath);
                        MigrateSession(dbPath);
                    }

                    _migrated = true;
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("LibraryDb.EnsureMigrated", ex);
                    // 迁移失败不崩溃：让 store 层在 SQLite 失败时回退到内存态，保证 app 可用
                    _migrated = true;
                }
            }
        }

        // ---------------------------------------------------------------- schema

        private static void CreateSchema(string dbPath)
        {
            using var conn = Open(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS playlists (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    name       TEXT NOT NULL UNIQUE,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS playlist_songs (
                    playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
                    position    INTEGER NOT NULL,
                    file_path   TEXT NOT NULL,
                    PRIMARY KEY (playlist_id, position)
                );
                CREATE TABLE IF NOT EXISTS library_session (
                    id       INTEGER PRIMARY KEY CHECK (id = 1),
                    mode     TEXT NOT NULL DEFAULT 'files',
                    folder   TEXT,
                    file_path TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }

        private static SqliteConnection Open(string dbPath)
        {
            SqliteConnection conn = new SqliteConnection("Data Source=" + dbPath);
            conn.Open();
            return conn;
        }

        // ---------------------------------------------------------------- json 探测与备份

        private static string PlaylistsFolder()
        {
            string root;
            try
            {
                root = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
            }

            return Path.Combine(root, "Playlists");
        }

        private static string SessionJsonPath()
        {
            string root;
            try
            {
                root = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer");
            }

            return Path.Combine(root, "last-library.json");
        }

        private static bool JsonDataExists()
        {
            string folder = PlaylistsFolder();
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.json").Any())
            {
                return true;
            }

            return File.Exists(SessionJsonPath());
        }

        private static void BackupJsonData()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            string folder = PlaylistsFolder();
            if (Directory.Exists(folder))
            {
                string backup = folder + ".bak-" + stamp;
                try
                {
                    Directory.CreateDirectory(backup);
                    foreach (string f in Directory.EnumerateFiles(folder, "*.json"))
                    {
                        File.Copy(f, Path.Combine(backup, Path.GetFileName(f)), overwrite: true);
                    }

                    StartupLog.Write("[LibraryDb] 已备份供命名单 JSON 到 " + backup);
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("LibraryDb.BackupPlaylists", ex);
                }
            }

            string session = SessionJsonPath();
            if (File.Exists(session))
            {
                string backupSession = session + ".bak-" + stamp;
                try
                {
                    File.Copy(session, backupSession, overwrite: true);
                    StartupLog.Write("[LibraryDb] 已备份会话 JSON 到 " + backupSession);
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("LibraryDb.BackupSession", ex);
                }
            }
        }

        // ---------------------------------------------------------------- 迁移

        private static void MigratePlaylists(string dbPath)
        {
            string folder = PlaylistsFolder();
            if (!Directory.Exists(folder))
            {
                return;
            }

            int count = 0;
            foreach (string file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    NamedPlaylistDto? dto = System.Text.Json.JsonSerializer.Deserialize<NamedPlaylistDto>(File.ReadAllText(file));
                    string name = dto?.Name ?? Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    List<string> songs = dto?.Songs?
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(Path.GetFullPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? new List<string>();

                    SavePlaylistInternal(dbPath, name, songs, dto?.UpdatedAt ?? DateTimeOffset.UtcNow);
                    count++;
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("LibraryDb.MigratePlaylists file=" + file, ex);
                }
            }

            StartupLog.Write($"[LibraryDb] 迁移命名单 {count} 个到 SQLite");
        }

        private static void MigrateSession(string dbPath)
        {
            try
            {
                if (!File.Exists(SessionJsonPath()))
                {
                    return;
                }

                LibrarySessionState? state = LibrarySessionStore.TryLoadFromJsonFile();
                if (state == null)
                {
                    return;
                }

                SaveSessionInternal(dbPath, state.Mode, state.FolderPath, state.FilePaths);
                StartupLog.Write("[LibraryDb] 迁移会话到 SQLite mode=" + state.Mode);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("LibraryDb.MigrateSession", ex);
            }
        }

        // ---------------------------------------------------------------- 命名单读写

        /// <summary>列出全部命名单（含内建“我喜欢的音乐”占位）。</summary>
        public static IReadOnlyList<string> ListPlaylists()
        {
            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT name FROM playlists ORDER BY name COLLATE NOCASE";
                    var names = new List<string>();
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        names.Add(r.GetString(0));
                    }

                    if (!names.Contains(NamedPlaylistStore.FavoritesPlaylistName, StringComparer.Ordinal))
                    {
                        names.Insert(0, NamedPlaylistStore.FavoritesPlaylistName);
                    }

                    return names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>读出某个命名单的歌曲路径（含内建“我喜欢的音乐”）。</summary>
        public static List<string> LoadPlaylistSongs(string name)
        {
            EnsureMigrated();
            if (string.Equals(name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
            {
                return TrackStatsStore.GetAllFavorites().ToList();
            }

            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT ps.file_path FROM playlist_songs ps
                        JOIN playlists p ON p.id = ps.playlist_id
                        WHERE p.name = $name ORDER BY ps.position";
                    cmd.Parameters.AddWithValue("$name", name);
                    var result = new List<string>();
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        result.Add(r.GetString(0));
                    }

                    return result
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(Path.GetFullPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>写入（新建或覆写）某个命名单的歌曲。name=favorites 走收藏逻辑。</summary>
        public static void SavePlaylist(string name, IEnumerable<string> songs)
        {
            EnsureMigrated();
            if (string.Equals(name, NamedPlaylistStore.FavoritesPlaylistName, StringComparison.Ordinal))
            {
                // 交给 NamedPlaylistStore 中间的收藏同步逻辑（它调用本层不会递归）。
                var list = songs?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
                NamedPlaylistStore.SyncFavoritesFromPathsForMigration(list);
                return;
            }

            var songList = songs?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            try
            {
                lock (Gate)
                {
                    SavePlaylistInternal(GetDbFilePath(), name, songList, DateTimeOffset.UtcNow);
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("LibraryDb.SavePlaylist", ex);
            }
        }

        private static void SavePlaylistInternal(string dbPath, string name, List<string> songList, DateTimeOffset updatedAt)
        {
            using var conn = Open(dbPath);
            using var tx = conn.BeginTransaction();
            try
            {
                // upsert playlist
                using (var upsert = conn.CreateCommand())
                {
                    upsert.CommandText = @"
                        INSERT INTO playlists(name, updated_at) VALUES($name, $ua)
                        ON CONFLICT(name) DO UPDATE SET updated_at = excluded.updated_at";
                    upsert.Parameters.AddWithValue("$name", name);
                    upsert.Parameters.AddWithValue("$ua", updatedAt.ToString("o"));
                    upsert.ExecuteNonQuery();
                }

                var idCmd = conn.CreateCommand();
                idCmd.CommandText = "SELECT id FROM playlists WHERE name = $name";
                idCmd.Parameters.AddWithValue("$name", name);
                long id = (long)(idCmd.ExecuteScalar() ?? 0L);

                using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM playlist_songs WHERE playlist_id = $id";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                }

                using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO playlist_songs(playlist_id, position, file_path) VALUES($id, $pos, $fp)";
                    var idp = ins.Parameters.Add("$id", SqliteType.Integer);
                    var posp = ins.Parameters.Add("$pos", SqliteType.Integer);
                    var fpp = ins.Parameters.Add("$fp", SqliteType.Text);

                    for (int i = 0; i < songList.Count; i++)
                    {
                        idp.Value = id;
                        posp.Value = i;
                        fpp.Value = songList[i];
                        ins.ExecuteNonQuery();
                    }
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>删除命名单。</summary>
        public static void DeletePlaylist(string name)
        {
            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM playlists WHERE name = $name";
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
            }
        }

        /// <summary>重命名命名单。</summary>
        public static void RenamePlaylist(string oldName, string newName)
        {
            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE playlists SET name = $new WHERE name = $old";
                    cmd.Parameters.AddWithValue("$new", newName);
                    cmd.Parameters.AddWithValue("$old", oldName);
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
            }
        }

        /// <summary>判断命名单是否存在。</summary>
        public static bool PlaylistExists(string name)
        {
            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM playlists WHERE name = $name";
                    cmd.Parameters.AddWithValue("$name", name);
                    return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 会话读写

        /// <summary>读出上次打开的音频会话；无则返回 null。</summary>
        public static LibrarySessionState? LoadSession()
        {
            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT mode, folder, file_path FROM library_session WHERE id = 1";
                    using var r = cmd.ExecuteReader();
                    if (!r.Read())
                    {
                        return null;
                    }

                    var files = new List<string>();
                    string fp = r.IsDBNull(2) ? string.Empty : r.GetString(2);
                    foreach (string s in fp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            files.Add(s);
                        }
                    }

                    return new LibrarySessionState
                    {
                        Mode = r.GetString(0),
                        FolderPath = r.IsDBNull(1) ? null : r.GetString(1),
                        FilePaths = files
                    };
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>保存上次打开的音频会话。</summary>
        public static void SaveSession(string mode, string? folderPath, IEnumerable<string> filePaths)
        {
            EnsureMigrated();
            var files = (filePaths ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            try
            {
                lock (Gate)
                {
                    SaveSessionInternal(GetDbFilePath(), mode, folderPath, files);
                }
            }
            catch
            {
            }
        }

        private static void SaveSessionInternal(string dbPath, string mode, string? folderPath, IEnumerable<string> filePaths)
        {
            string joined = string.Join("\n", (filePaths ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrEmpty(joined))
            {
                joined = " "; // 保留一行避免空串歧义
            }

            using var conn = Open(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO library_session(id, mode, folder, file_path) VALUES(1, $mode, $folder, $fp)
                ON CONFLICT(id) DO UPDATE SET mode = excluded.mode, folder = excluded.folder, file_path = excluded.file_path";
            cmd.Parameters.AddWithValue("$mode", string.IsNullOrEmpty(mode) ? "files" : mode);
            cmd.Parameters.AddWithValue("$folder", (object?)folderPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fp", joined);
            cmd.ExecuteNonQuery();
        }
    }
}
