using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CelesteMusicPlayer;

internal sealed class NetworkSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "webdav";
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Root { get; set; } = "";
    public string Username { get; set; } = "";
    public string Auth { get; set; } = "password";
    public string UserId { get; set; } = "";
    public string ProtectedSecret { get; set; } = "";
    public string ProtectedRefreshToken { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ProtectedClientSecret { get; set; } = "";
    public string ProxyMode { get; set; } = "System";
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 7890;
    public bool Enabled { get; set; } = true;
    public int Revision { get; set; } = 1;
    public DateTimeOffset? LastSync { get; set; }
    public string LastError { get; set; } = "";
    [JsonIgnore] public string Secret => NetworkSecrets.Unprotect(ProtectedSecret);
    [JsonIgnore] public string RefreshToken => NetworkSecrets.Unprotect(ProtectedRefreshToken);
    [JsonIgnore] public string ClientSecret => NetworkSecrets.Unprotect(ProtectedClientSecret);
}

internal static class NetworkSecrets
{
    internal static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    internal static string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}

internal sealed class NetworkEntry
{
    public string Key { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Folder { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public int Track { get; set; }
    public int Disc { get; set; }
    public double Duration { get; set; }
    public long Size { get; set; }
    public string Version { get; set; } = "";
    public string Extension { get; set; } = "";
    public string CoverKey { get; set; } = "";
}

internal sealed class NetworkTrack
{
    public string Id { get; set; } = "";
    public string SourceId { get; set; } = "";
    public NetworkEntry Entry { get; set; } = new();
    public bool Indexed { get; set; }
    public bool Pinned { get; set; }
    public bool Missing { get; set; }
    public bool Cue { get; set; }
    public string CueKey { get; set; } = "";
    public long StartFrame { get; set; }
    public long EndFrame { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public int Track { get; set; }
    public string LocalPath { get; set; } = "";
}

internal static class NetworkIdentity
{
    internal static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".flac", ".wav", ".mp3", ".aac", ".m4a", ".alac", ".aiff", ".aif", ".ape", ".wv", ".ogg", ".opus", ".wma", ".dsf", ".dff", ".mp4" };
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string TrackId(string source, string key) => "source-" + Hash(source + "\n" + key);
    // Path is a locator, never a download URL; preserve remote case and decode only at protocol boundaries.
    internal static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new ArgumentException("路径超出来源根目录。"); parts.RemoveAt(parts.Count - 1); }
            else { if (part.IndexOf('\0') >= 0) throw new ArgumentException("无效路径。"); parts.Add(part); }
        }
        return "/" + string.Join("/", parts);
    }
    internal static string Join(string parent, string child) => Normalize(parent.TrimEnd('/') + "/" + child);
    internal static string Reference(string id) => System.IO.Path.Combine(AppSettingsStore.GetConfigDirectory(), "track-refs", id + ".ref");
    internal static string Extension(NetworkEntry entry)
    {
        string ext = string.IsNullOrEmpty(entry.Extension) ? System.IO.Path.GetExtension(entry.Name) : "." + entry.Extension.TrimStart('.');
        return Audio.Contains(ext) ? ext.ToLowerInvariant() : ".flac";
    }
}
