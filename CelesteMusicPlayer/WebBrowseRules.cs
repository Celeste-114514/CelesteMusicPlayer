using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer;

// Pure rules shared by the web adapter and its regression checks.
internal static class WebBrowseRules
{
    internal static string Folder(string filePath, string[] roots)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "";
        foreach (string root in roots.OrderByDescending(root => root.Length))
        {
            string relative = Path.GetRelativePath(root, dir);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) continue;
            string label = Path.GetFileName(root);
            if (string.IsNullOrEmpty(label)) label = root.TrimEnd('\\', '/');
            if (roots.Count(other => string.Equals(Path.GetFileName(other), Path.GetFileName(root), StringComparison.OrdinalIgnoreCase)) > 1)
                label += " (" + root.Replace('\\', '›').Replace('/', '›') + ")";
            return relative == "." ? label : Path.Combine(label, relative);
        }
        return string.IsNullOrEmpty(dir) ? "未知文件夹" : dir;
    }

    internal static List<T> Insert<T>(IReadOnlyList<T> source, T[] songs, T? playing, Func<T, string> key, Func<T,T> clone, bool next, bool insertBegin) where T : class
    {
        var incoming = songs.DistinctBy(key).ToArray();
        var ids = incoming.Select(key).ToHashSet(StringComparer.Ordinal);
        var kept = source.Where(song => !ids.Contains(key(song)) || next && ReferenceEquals(song, playing)).ToList();
        int insertAt = next ? (playing == null ? 0 : kept.IndexOf(playing) + 1) : insertBegin ? 0 : kept.Count;
        kept.InsertRange(insertAt, incoming.Select(clone));
        return kept;
    }
}
