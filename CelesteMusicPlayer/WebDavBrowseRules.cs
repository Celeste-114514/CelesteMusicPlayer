using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer;

/// <summary>
/// 网络库浏览的纯规则（2026-10-30 从 MainWindow.WebDav.cs 抽出）。
/// 七种来源（webdav/baidu/jellyfin/emby/smb/sshfs/subsonic）的配置、同步与起播在
/// NetworkLibraryService / WebSourcesService；这里只管 WebDAV 树形浏览的
/// 路径数学和「目录条目 → 播放列表条目」的换算。
/// <paramref name="itemFromPath"/> 由调用方注入（MainWindow 的 CreatePlaylistItemFromPath），
/// 本类不认识界面、不碰播放。
/// </summary>
internal static class WebDavBrowseRules
{
    /// <summary>拼出「相对服务器根目录」的完整路径（WebDavEntry.RelativePath 只是相对当前目录的一段）。</summary>
    public static string CombineRemotePath(string parent, string child)
    {
        string p = (parent ?? string.Empty).Trim('/');
        string c = (child ?? string.Empty).Trim('/');
        if (c.StartsWith(p + "/", StringComparison.Ordinal) && !string.IsNullOrEmpty(p))
        {
            return c; // 服务器已经返回了完整路径，别重复拼
        }

        return string.IsNullOrEmpty(p) ? c : p + "/" + c;
    }

    public static string GetRemoteParent(string path)
    {
        string p = (path ?? string.Empty).Trim('/');
        int slash = p.LastIndexOf('/');
        return slash < 0 ? string.Empty : p.Substring(0, slash);
    }

    /// <summary>把 WebDAV 异常翻译成一句人话。</summary>
    public static string DescribeFailure(Exception ex)
    {
        if (ex is WebDavException webDav)
        {
            return webDav.Headline + "：" + webDav.Message;
        }

        if (ex is AggregateException agg && agg.InnerException != null)
        {
            return DescribeFailure(agg.InnerException);
        }

        return ex.Message;
    }

    /// <summary>
    /// 目录条目 → 歌曲列表：跳过文件夹与非音频，远程路径按根拼全。
    /// **不做序号**——原实现里编号在回 UI 线程后做，保持一致。
    /// </summary>
    public static List<PlaylistItem> BuildSongList(WebDavLocation loc, IReadOnlyList<WebDavEntry> entries,
        string folderPath, IReadOnlyCollection<string> audioExtensions, Func<string, PlaylistItem> itemFromPath)
    {
        var list = new List<PlaylistItem>();
        foreach (WebDavEntry e in entries)
        {
            if (e.IsFolder)
            {
                continue;
            }

            if (!audioExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string rel = CombineRemotePath(folderPath, e.RelativePath);
            list.Add(CreateRemoteItem(loc, new WebDavEntry
            {
                Name = e.Name,
                RelativePath = rel,
                IsFolder = false,
                Size = e.Size,
                ModifiedUtc = e.ModifiedUtc
            }, folderPath, itemFromPath));
        }

        return list;
    }

    /// <summary>
    /// 造一个网络曲目条目。已经缓存过的直接读本地标签（曲目信息、时长都是真的）；
    /// 没缓存的（或缓存文件坏了的）只显示文件名和大小 —— 不为了显示时长去下一整首歌。
    /// </summary>
    public static PlaylistItem CreateRemoteItem(WebDavLocation loc, WebDavEntry entry, string folderPath,
        Func<string, PlaylistItem> itemFromPath)
    {
        string localPath = WebDavCache.GetCachePath(loc, entry.RelativePath);

        if (WebDavCache.IsCached(loc, entry, out string cached))
        {
            try
            {
                PlaylistItem tagged = itemFromPath(cached);
                tagged.RemotePath = entry.RelativePath;
                tagged.RemoteHint = "已缓存";
                return tagged;
            }
            catch (Exception caught)
            {
                // 缓存文件坏了（上次没下完等）：当作未缓存处理，下面走通用分支。
                StartupLog.WriteException("WebDavBrowseRules.cs", caught);
            }
        }

        return new PlaylistItem
        {
            Title = Path.GetFileNameWithoutExtension(entry.Name),
            Artist = "网络音乐库",
            Album = string.IsNullOrWhiteSpace(folderPath)
                ? loc.LibraryName
                : Path.GetFileName(folderPath.TrimEnd('/')),
            FilePath = localPath,
            RemotePath = entry.RelativePath,
            RemoteHint = entry.Size > 0 ? WebDavCache.FormatSize(entry.Size) : "未缓存"
        };
    }
}
