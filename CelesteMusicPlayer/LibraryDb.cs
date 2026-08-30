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

        /// <summary>测试隔离：非空时数据库文件改用该路径（回归测试用临时库，避免污染真实数据）。</summary>
        internal static string? TestDbOverride { get; set; }

        /// <summary>测试隔离：强制重新走一次 EnsureMigrated（配合 TestDbOverride 换库后重建 schema）。</summary>
        internal static void ResetMigratedForTest()
        {
            lock (Gate)
            {
                _migrated = false;
            }
        }

        /// <summary>数据库文件完整路径（%LOCALAPPDATA%\CelesteMusicPlayer\library.db）。</summary>
        public static string GetDbFilePath()
        {
            if (!string.IsNullOrEmpty(TestDbOverride))
            {
                return TestDbOverride;
            }

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
                -- 阶段4：曲库标签索引。避免每次启动都对全部音频重新解析标签（TagLib 解析是启动最重的部分）。
                -- mtime_utc + size 作为「文件是否被改动过」的校验键，二者一致才认为缓存有效。
                CREATE TABLE IF NOT EXISTS tracks (
                    file_path      TEXT PRIMARY KEY,
                    mtime_utc      INTEGER NOT NULL DEFAULT 0,
                    size           INTEGER NOT NULL DEFAULT 0,
                    title          TEXT NOT NULL DEFAULT '',
                    artist         TEXT NOT NULL DEFAULT '',
                    album_artist   TEXT NOT NULL DEFAULT '',
                    album          TEXT NOT NULL DEFAULT '',
                    track_no       INTEGER NOT NULL DEFAULT 0,
                    disc_no        INTEGER NOT NULL DEFAULT 0,
                    year           INTEGER NOT NULL DEFAULT 0,
                    genre          TEXT NOT NULL DEFAULT '',
                    duration_ticks INTEGER NOT NULL DEFAULT 0,
                    updated_at     INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS playback_history (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_path     TEXT NOT NULL,
                    title         TEXT NOT NULL,
                    played_at_utc TEXT NOT NULL,
                    played_seconds REAL NOT NULL DEFAULT 0,
                    completed     INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_playback_history_time
                    ON playback_history (played_at_utc DESC);
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

        // ---------------------------------------------------------------- 播放历史

        /// <summary>播放历史条目（最近播放，时间倒序）。</summary>
        public sealed class PlaybackHistoryEntry
        {
            public string FilePath { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public DateTime PlayedAtUtc { get; set; }
            public double PlayedSeconds { get; set; }
            public bool Completed { get; set; }
        }

        /// <summary>记录一次播放（切歌/播完/停止时调用）。失败静默（不影响播放）。</summary>
        public static void RecordPlayback(string path, string title, double playedSeconds, bool completed)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO playback_history(file_path, title, played_at_utc, played_seconds, completed)
                        VALUES($fp, $title, $at, $sec, $done)";
                    cmd.Parameters.AddWithValue("$fp", path);
                    cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title);
                    cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("$sec", Math.Max(0, playedSeconds));
                    cmd.Parameters.AddWithValue("$done", completed ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
            }
        }

        /// <summary>加载最近播放历史（时间倒序）。失败返回空列表。</summary>
        public static List<PlaybackHistoryEntry> LoadPlaybackHistory(int limit = 100)
        {
            EnsureMigrated();
            var result = new List<PlaybackHistoryEntry>();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT file_path, title, played_at_utc, played_seconds, completed
                        FROM playback_history
                        ORDER BY played_at_utc DESC
                        LIMIT $limit";
                    cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        result.Add(new PlaybackHistoryEntry
                        {
                            FilePath = r.GetString(0),
                            Title = r.GetString(1),
                            PlayedAtUtc = DateTime.TryParse(r.GetString(2), out var t) ? t : DateTime.MinValue,
                            PlayedSeconds = r.GetDouble(3),
                            Completed = r.GetInt64(4) != 0
                        });
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        /// <summary>清空播放历史。失败静默。</summary>
        public static void ClearPlaybackHistory()
        {
            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM playback_history";
                    cmd.ExecuteNonQuery();
                }
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------- tracks（曲库标签索引）

        /// <summary>一条音频的标签快照，外加用于判定缓存是否仍然有效的文件指纹（修改时间 + 大小）。</summary>
        public sealed class TrackMeta
        {
            public string FilePath { get; set; } = string.Empty;

            /// <summary>文件最后修改时间（UTC 时间戳，秒或更细粒度由调用方决定）。</summary>
            public long MtimeUtc { get; set; }

            public long Size { get; set; }

            public string Title { get; set; } = string.Empty;

            public string Artist { get; set; } = string.Empty;

            public string AlbumArtist { get; set; } = string.Empty;

            public string Album { get; set; } = string.Empty;

            public uint Track { get; set; }

            public uint Disc { get; set; }

            public uint Year { get; set; }

            public string Genre { get; set; } = string.Empty;

            public long DurationTicks { get; set; }
        }

        /// <summary>批量查询已缓存的标签，只返回指纹仍然匹配的条目（文件被改动过则视为失效）。</summary>
        /// <param name="stamps">每个待加载文件的 (路径, 修改时间UtcTicks, 大小)。</param>
        public static Dictionary<string, TrackMeta> LoadTrackIndex(IEnumerable<(string path, long mtimeUtc, long size)> stamps)
        {
            var result = new Dictionary<string, TrackMeta>(StringComparer.OrdinalIgnoreCase);
            EnsureMigrated();

            // 用指纹表在内存里比对，避免为校验把每个文件都拼进 SQL
            var fingerprint = new Dictionary<string, (long mt, long sz)>(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, long mtimeUtc, long size) in stamps)
            {
                fingerprint[path] = (mtimeUtc, size);
            }

            if (fingerprint.Count == 0)
            {
                return result;
            }

            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    foreach (List<string> chunk in Chunk(fingerprint.Keys, SqlChunkSize))
                    {
                        using var cmd = conn.CreateCommand();
                        string placeholders = string.Join(",", Enumerable.Range(0, chunk.Count).Select(i => "$p" + i));
                        for (int i = 0; i < chunk.Count; i++)
                        {
                            cmd.Parameters.AddWithValue("$p" + i, chunk[i]);
                        }

                        cmd.CommandText =
                            "SELECT file_path, mtime_utc, size, title, artist, album_artist, album, " +
                            "track_no, disc_no, year, genre, duration_ticks " +
                            "FROM tracks WHERE file_path IN (" + placeholders + ")";

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            string path = reader.GetString(0);
                            long mt = reader.GetInt64(1);
                            long sz = reader.GetInt64(2);

                            // 指纹不一致 = 文件被替换/重新打过标签，缓存作废
                            if (!fingerprint.TryGetValue(path, out var fp) || fp.mt != mt || fp.sz != sz)
                            {
                                continue;
                            }

                            result[path] = new TrackMeta
                            {
                                FilePath = path,
                                MtimeUtc = mt,
                                Size = sz,
                                Title = reader.GetString(3),
                                Artist = reader.GetString(4),
                                AlbumArtist = reader.GetString(5),
                                Album = reader.GetString(6),
                                Track = (uint)reader.GetInt64(7),
                                Disc = (uint)reader.GetInt64(8),
                                Year = (uint)reader.GetInt64(9),
                                Genre = reader.GetString(10),
                                DurationTicks = reader.GetInt64(11)
                            };
                        }
                    }
                }
            }
            catch
            {
                // 索引不可用不应影响启动：调用方会退回逐个解析标签
            }

            return result;
        }

        /// <summary>批量写入/更新标签索引。失败静默（索引只是加速手段，不是数据源）。</summary>
        public static void UpsertTracks(IEnumerable<TrackMeta> metas)
        {
            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    foreach (List<TrackMeta> chunk in Chunk(metas, SqlChunkSize))
                    {
                        if (chunk.Count == 0)
                        {
                            continue;
                        }

                        using var tx = conn.BeginTransaction();
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
                            INSERT INTO tracks(file_path, mtime_utc, size, title, artist, album_artist, album,
                                               track_no, disc_no, year, genre, duration_ticks, updated_at)
                            VALUES($fp, $mt, $sz, $ti, $ar, $aa, $al, $tn, $dn, $yr, $ge, $du, $ua)
                            ON CONFLICT(file_path) DO UPDATE SET
                                mtime_utc = excluded.mtime_utc,
                                size = excluded.size,
                                title = excluded.title,
                                artist = excluded.artist,
                                album_artist = excluded.album_artist,
                                album = excluded.album,
                                track_no = excluded.track_no,
                                disc_no = excluded.disc_no,
                                year = excluded.year,
                                genre = excluded.genre,
                                duration_ticks = excluded.duration_ticks,
                                updated_at = excluded.updated_at";

                        var fp = cmd.Parameters.Add("$fp", SqliteType.Text);
                        var mt = cmd.Parameters.Add("$mt", SqliteType.Integer);
                        var sz = cmd.Parameters.Add("$sz", SqliteType.Integer);
                        var ti = cmd.Parameters.Add("$ti", SqliteType.Text);
                        var ar = cmd.Parameters.Add("$ar", SqliteType.Text);
                        var aa = cmd.Parameters.Add("$aa", SqliteType.Text);
                        var al = cmd.Parameters.Add("$al", SqliteType.Text);
                        var tn = cmd.Parameters.Add("$tn", SqliteType.Integer);
                        var dn = cmd.Parameters.Add("$dn", SqliteType.Integer);
                        var yr = cmd.Parameters.Add("$yr", SqliteType.Integer);
                        var ge = cmd.Parameters.Add("$ge", SqliteType.Text);
                        var du = cmd.Parameters.Add("$du", SqliteType.Integer);
                        var ua = cmd.Parameters.Add("$ua", SqliteType.Integer);

                        long now = DateTime.UtcNow.Ticks;
                        foreach (TrackMeta m in chunk)
                        {
                            fp.Value = m.FilePath;
                            mt.Value = m.MtimeUtc;
                            sz.Value = m.Size;
                            ti.Value = m.Title ?? string.Empty;
                            ar.Value = m.Artist ?? string.Empty;
                            aa.Value = m.AlbumArtist ?? string.Empty;
                            al.Value = m.Album ?? string.Empty;
                            tn.Value = (long)m.Track;
                            dn.Value = (long)m.Disc;
                            yr.Value = (long)m.Year;
                            ge.Value = m.Genre ?? string.Empty;
                            du.Value = m.DurationTicks;
                            ua.Value = now;
                            cmd.ExecuteNonQuery();
                        }

                        tx.Commit();
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>删除指定路径的索引记录（文件已被移走/删除时使用）。失败静默。</summary>
        public static void DeleteTracks(IEnumerable<string> paths)
        {
            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    foreach (List<string> chunk in Chunk(paths, SqlChunkSize))
                    {
                        if (chunk.Count == 0)
                        {
                            continue;
                        }

                        using var tx = conn.BeginTransaction();
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tx;
                        string placeholders = string.Join(",", Enumerable.Range(0, chunk.Count).Select(i => "$p" + i));
                        for (int i = 0; i < chunk.Count; i++)
                        {
                            cmd.Parameters.AddWithValue("$p" + i, chunk[i]);
                        }

                        cmd.CommandText = "DELETE FROM tracks WHERE file_path IN (" + placeholders + ")";
                        cmd.ExecuteNonQuery();
                        tx.Commit();
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>索引中的曲目总数（日志/诊断用）。失败返回 -1。</summary>
        public static int CountTracks()
        {
            EnsureMigrated();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM tracks";
                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>SQL 参数个数上限的保守分块大小。</summary>
        private const int SqlChunkSize = 400;

        /// <summary>取全部曲目（含专辑信息），供 ReplayGain 扫描按专辑分组。失败返回空列表。</summary>
        public static List<RgScanInput> GetAllTracksForScan()
        {
            var list = new List<RgScanInput>();
            try
            {
                lock (Gate)
                {
                    using var conn = Open(GetDbFilePath());
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT file_path, album, album_artist FROM tracks";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        list.Add(new RgScanInput
                        {
                            FilePath = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                            Album = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                            AlbumArtist = r.IsDBNull(2) ? string.Empty : r.GetString(2)
                        });
                    }
                }
            }
            catch (Exception caught) { StartupLog.WriteException("LibraryDb.GetAllTracksForScan", caught); }
            return list;
        }

        private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size)
        {
            List<T> bucket = new(size);
            foreach (T item in source)
            {
                bucket.Add(item);
                if (bucket.Count >= size)
                {
                    yield return bucket;
                    bucket = new List<T>(size);
                }
            }

            if (bucket.Count > 0)
            {
                yield return bucket;
            }
        }
    }
}
