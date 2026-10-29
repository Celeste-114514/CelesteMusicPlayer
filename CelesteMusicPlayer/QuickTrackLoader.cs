using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace CelesteMusicPlayer;

// Reads only explicitly opened files and their CUE sheets. Never opens the library database.
internal static class QuickTrackLoader
{
    internal static PlaylistItem Read(string path)
    {
        var item = new PlaylistItem { FilePath = Path.GetFullPath(path), Title = Path.GetFileNameWithoutExtension(path) };
        try
        {
            using var file = TagLib.File.Create(path);
            var t = file.Tag;
            item.Title = string.IsNullOrWhiteSpace(t.Title) ? item.Title : t.Title;
            item.Artist = t.FirstPerformer ?? "未知艺术家";
            item.AlbumArtist = t.FirstAlbumArtist ?? item.Artist;
            item.Album = t.Album ?? "未知专辑"; item.Track = t.Track; item.Disc = t.Disc; item.Year = t.Year;
            item.Duration = file.Properties.Duration;
        }
        catch (Exception ex) { StartupLog.WriteException("QuickTrackLoader.Metadata", ex); }
        return item;
    }
    internal static List<PlaylistItem> Load(IEnumerable<string> paths)
    {
        var result = new List<PlaylistItem>();
        foreach (string path in paths.Where(File.Exists).Where(QuickStartupIntent.IsPlayable).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new ArgumentException("播放列表过大。");
                double? cueStart = null, duration = null;
                foreach (string raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("#CELESTE-CUE-START:")) { if (double.TryParse(line.Substring(19), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var start)) cueStart = start; continue; }
                    if (line.StartsWith("#EXTINF:")) { if (double.TryParse(line.Substring(8).Split(',')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var len)) duration = len; continue; }
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, line));
                    if (File.Exists(file) && QuickStartupIntent.IsPlayable(file) && !new[] { ".cue", ".m3u", ".m3u8" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    {
                        var item = Read(file);
                        if (cueStart is >= 0 && double.IsFinite(cueStart.Value) && duration is > 0 && double.IsFinite(duration.Value)) { item.IsCueSegment = true; item.StartTimeSeconds = cueStart.Value; item.Duration = TimeSpan.FromSeconds(duration.Value); item.SourceTrackId = "cue-list-" + NetworkIdentity.Hash(file + "\n" + cueStart.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                        result.Add(item);
                    }
                    cueStart = duration = null;
                }
                continue;
            }
            if (!path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            {
                result.AddRange(CueTrackExpansion.Expand(new List<PlaylistItem> { Read(path) })); continue;
            }
            var tracks = CueSheetParser.LoadCue(path);
            var sources = tracks.Where(t => File.Exists(t.FilePath)).Select(t => t.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, Read, StringComparer.OrdinalIgnoreCase);
            foreach (var t in tracks.Where(t => sources.ContainsKey(t.FilePath)))
            {
                var item = sources[t.FilePath].Clone();
                item.Title = t.Title; item.Artist = t.Artist; item.Album = t.Album; item.AlbumArtist = t.AlbumArtist;
                item.Track = (uint)t.Number; item.IsCueSegment = true; item.StartTimeSeconds = t.StartTime.TotalSeconds;
                var next = tracks.FirstOrDefault(n => n.FilePath.Equals(t.FilePath, StringComparison.OrdinalIgnoreCase) && n.StartTime > t.StartTime);
                item.Duration = TimeSpan.FromSeconds(Math.Max(0, (next?.StartTime.TotalSeconds ?? item.Duration.TotalSeconds) - item.StartTimeSeconds));
                item.SourceTrackId = "cue-" + NetworkIdentity.Hash(Path.GetFullPath(item.FilePath).ToUpperInvariant() + "\n" + item.Track);
                result.Add(item);
            }
        }
        return result.DistinctBy(i => i.PersistenceKey, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
