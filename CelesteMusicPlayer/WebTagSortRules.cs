using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CelesteMusicPlayer;

internal static class WebTagSortRules
{
    // Rules apply to the entire explicit workspace before paging, including file-only fields.
    internal static IEnumerable<KeyValuePair<string, PlaylistItem>> Sort(
        KeyValuePair<string, PlaylistItem>[] items, JsonElement msg, Func<string, string, object> readMetadata)
    {
        if (!msg.TryGetProperty("sortRules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return items;
        var fields = new HashSet<string>(new[] { "title", "artist", "albumArtist", "album", "year", "track", "genre", "duration", "file", "format", "spec", "bitrate", "fileSize", "composer", "disc", "trackCount", "bpm", "comment" });
        var keys = rules.EnumerateArray().Take(18).Select(r => (Key: r.GetProperty("key").GetString() ?? "", Desc: r.TryGetProperty("descending", out var d) && d.ValueKind == JsonValueKind.True)).ToArray();
        if (keys.Any(r => !fields.Contains(r.Key))) throw new ArgumentException("不支持的排序字段。");
        if (keys.Length == 0) return items;
        bool readTags = keys.Any(r => r.Key is "spec" or "bitrate" or "fileSize" or "composer" or "trackCount" or "bpm" or "comment");
        var metadata = new Dictionary<string, JsonElement>();
        if (readTags)
            foreach (var pair in items)
                if (!pair.Value.IsRemote) metadata[pair.Key] = JsonSerializer.SerializeToElement(readMetadata(pair.Key, pair.Value.FilePath));
        object Value(KeyValuePair<string, PlaylistItem> pair, string key)
        {
            var p = pair.Value;
            switch (key)
            {
                case "title": return p.Title ?? "";
                case "artist": return p.Artist ?? "";
                case "albumArtist": return p.AlbumArtist ?? "";
                case "album": return p.Album ?? "";
                case "year": return (double)p.Year;
                case "track": return (double)p.Track;
                case "disc": return (double)p.Disc;
                case "genre": return p.Genre ?? "";
                case "duration": return p.Duration.TotalSeconds;
                case "file": return Path.GetFileName(p.FilePath);
                case "format": return Path.GetExtension(p.FilePath);
            }
            if (!metadata.TryGetValue(pair.Key, out var data)) return key is "spec" or "trackCount" or "bpm" or "bitrate" or "fileSize" ? (object)0d : "";
            string Get(string k) => data.TryGetProperty(k, out var v) ? v.ToString() : "";
            double Number(string text) => double.TryParse(text.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (key == "spec") return Number(Get("sampleRate")) * 1000 + Number(Get("bitDepth"));
            if (key is "trackCount" or "bpm" or "bitrate" or "fileSize") return Number(Get(key));
            return Get(key);
        }
        var comparer = Comparer<object>.Create((a,b) => a is double x && b is double y ? x.CompareTo(y) : StringComparer.CurrentCultureIgnoreCase.Compare(a?.ToString(), b?.ToString()));
        IOrderedEnumerable<KeyValuePair<string, PlaylistItem>>? ordered = null;
        foreach (var rule in keys)
        {
            Func<KeyValuePair<string, PlaylistItem>, object> selector = p => Value(p, rule.Key);
            ordered = ordered == null ? (rule.Desc ? items.OrderByDescending(selector, comparer) : items.OrderBy(selector, comparer))
                : (rule.Desc ? ordered.ThenByDescending(selector, comparer) : ordered.ThenBy(selector, comparer));
        }
        return ordered ?? (IEnumerable<KeyValuePair<string, PlaylistItem>>)items;
    }
}
