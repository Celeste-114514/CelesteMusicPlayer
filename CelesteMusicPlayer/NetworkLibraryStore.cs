using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CelesteMusicPlayer;

// Additive storage: old local index, playlists and statistics stay intact.
internal static class NetworkLibraryStore
{
    private static readonly object Gate = new();
    private static string DbPath => Path.Combine(AppSettingsStore.GetConfigDirectory(), "network-library.db");
    private static SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; CREATE TABLE IF NOT EXISTS sources(id TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS source_tracks(id TEXT PRIMARY KEY,source_id TEXT NOT NULL,json TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_source_tracks_source ON source_tracks(source_id); CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY,value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        return db;
    }
    internal static NetworkSource[] Sources()
    {
        lock (Gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM sources";
            using var reader = cmd.ExecuteReader(); var list = new List<NetworkSource>();
            while (reader.Read()) list.Add(JsonSerializer.Deserialize<NetworkSource>(reader.GetString(0))!);
            return list.ToArray();
        }
    }
    internal static NetworkSource Source(string id) => Sources().FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("来源已不存在。");
    internal static void SaveSource(NetworkSource source)
    {
        lock (Gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO sources(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
            cmd.Parameters.AddWithValue("$id", source.Id); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(source)); cmd.ExecuteNonQuery();
        }
    }
    internal static NetworkTrack[] Tracks(string? sourceId = null)
    {
        lock (Gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = sourceId == null ? "SELECT json FROM source_tracks" : "SELECT json FROM source_tracks WHERE source_id=$source";
            if (sourceId != null) cmd.Parameters.AddWithValue("$source", sourceId);
            using var reader = cmd.ExecuteReader(); var list = new List<NetworkTrack>();
            while (reader.Read()) list.Add(JsonSerializer.Deserialize<NetworkTrack>(reader.GetString(0))!);
            return list.ToArray();
        }
    }
    internal static NetworkTrack? Track(string id)
    {
        lock (Gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM source_tracks WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<NetworkTrack>(json) : null;
        }
    }
    internal static void Put(IEnumerable<NetworkTrack> tracks)
    {
        lock (Gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            foreach (var track in tracks)
            {
                using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO source_tracks(id,source_id,json) VALUES($id,$source,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
                cmd.Parameters.AddWithValue("$id", track.Id); cmd.Parameters.AddWithValue("$source", track.SourceId); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(track)); cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    internal static void Pin(string id)
    {
        lock (Gate) { var track = Track(id); if (track == null || track.Pinned) return; track.Pinned = true; Put(new[] { track }); }
    }
    internal static void MigrateWebDav()
    {
        lock (Gate)
        {
            using var db = Open(); using var check = db.CreateCommand(); check.CommandText = "SELECT value FROM state WHERE key='webdav-migrated'";
            if (check.ExecuteScalar() != null) return;
            var old = WebDavLocation.FromSettings(AppSettingsStore.Load());
            if (old.IsConfigured) SaveSource(new NetworkSource { Id = "legacy-webdav", Provider = "webdav", Name = old.LibraryName,
                Endpoint = old.Url, Username = old.User, ProtectedSecret = NetworkSecrets.Protect(old.Password),
                ProxyMode = old.ProxyMode, ProxyHost = old.ProxyHost, ProxyPort = old.ProxyPort });
            using var mark = db.CreateCommand(); mark.CommandText = "INSERT OR REPLACE INTO state(key,value) VALUES('webdav-migrated','1')"; mark.ExecuteNonQuery();
        }
    }
}
