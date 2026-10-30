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

    /// <summary>
    /// 校验"播放队列右键菜单"带来的条目引用：修订号必须一致、每个下标必须在界内且曲目身份对得上
    /// （CUE 分轨按身份区分）。返回去重后的下标（保持首次出现顺序）。任何对不上都抛同一条提示。
    /// </summary>
    internal static int[] ResolveQueueIndices<T>(IReadOnlyList<T> items, int revision, int expectedRevision, (int Index, string TrackId)[] refs, Func<T, string> key) where T : class
    {
        if (revision != expectedRevision) throw new InvalidOperationException("播放队列已变化，请重新打开菜单。");
        var indices = new List<int>(refs.Length);
        foreach (var (index, trackId) in refs)
        {
            if (index < 0 || index >= items.Count || key(items[index]) != trackId)
                throw new InvalidOperationException("播放队列已变化，请重新打开菜单。");
            indices.Add(index);
        }
        return indices.Distinct().ToArray();
    }

    /// <summary>把网页给的曲目 id 解析成对象：按首次出现去重，任何一个查不到都抛"歌曲已不在曲库中"。</summary>
    internal static List<T> ResolveById<T>(IEnumerable<string?> ids, IReadOnlyDictionary<string, T> map) where T : class
    {
        var result = new List<T>();
        foreach (string? id in ids.Distinct())
        {
            if (id == null || !map.TryGetValue(id, out T? song)) throw new InvalidOperationException("歌曲已不在曲库中，请刷新。");
            result.Add(song);
        }
        return result;
    }

    /// <summary>
    /// 队列右键的"移除/置顶"：**原地**改列表（ ObservableCollection 的事件照常发给绑定的视图），
    /// 返回播放中曲目在新顺序里的下标（-1 = 没了或本来就没在播）。pinItems 置顶保持原有相对顺序。
    /// </summary>
    internal static int RemoveAndPin<T>(IList<T> items, int[] removeIndices, T[] pinItems, T? playing) where T : class
    {
        foreach (int index in removeIndices.OrderByDescending(i => i)) items.RemoveAt(index);
        for (int i = pinItems.Length - 1; i >= 0; i--) items.Insert(0, pinItems[i]);
        return playing == null ? -1 : items.IndexOf(playing);
    }
}
