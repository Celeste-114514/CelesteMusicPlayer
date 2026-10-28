using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer.ThinHost;

/// <summary>
/// W6 第二步：曲库/媒体库边界自检。
/// 与 SettingsBoundary 同一套思路：把「以前靠人记」的曲库层行为变成机器检查。
/// 这些类型编译进了本 PoC（无 WinUI/WinRT），能编过本身就是边界成立的证明；
/// 这里再对行为做一轮真实读写验证。
///
/// 数据安全红线（全部照做）：
///   · 曲库只写 LibraryDb.TestDbOverride 指定的临时库，绝不开用户真实 library.db；
///   · JSON 往返只写本自检自己的临时目录；
///   · AppSettingsStore.Load() 只被只读地触达（PurgeInternalCacheTracks → LibraryPathGuard），
///     自检自身不写任何设置；
///   · 结束后把 TestDbOverride 归零，尽力删除临时目录。
/// </summary>
internal static class LibraryBoundary
{
    internal sealed record Result(
        bool Passed,
        int Checks,
        IReadOnlyList<string> Details);

    internal static Result Run()
    {
        var details = new List<string>();
        int checks = 0;
        string tempRoot = Path.Combine(Path.GetTempPath(), "CelestePocLibraryBoundary-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string dbPath = Path.Combine(tempRoot, "library-test.db");
        Directory.CreateDirectory(tempRoot);

        try
        {
            // ---- 1) 临时库自举：TestDbOverride 生效、迁移可重建 ----
            LibraryDb.TestDbOverride = dbPath;
            LibraryDb.ResetMigratedForTest();
            Check(details, ref checks, "override", LibraryDb.GetDbFilePath() == dbPath, "TestDbOverride 被采用");
            LibraryDb.EnsureMigrated();
            Check(details, ref checks, "migrate", File.Exists(dbPath), "临时库文件已创建（迁移跑通）");

            // ---- 2) 命名单（播放列表）往返 ----
            string[] songs = { @"C:\Music\a.flac", @"C:\Music\b.wav", @"C:\Music\c.mp3" };
            LibraryDb.SavePlaylist("poc-list", songs);
            Check(details, ref checks, "playlist.exists", LibraryDb.PlaylistExists("poc-list"));
            var loaded = LibraryDb.LoadPlaylistSongs("poc-list");
            Check(details, ref checks, "playlist.roundtrip", loaded.Count == songs.Length && loaded.SequenceEqual(songs));
            Check(details, ref checks, "playlist.listed", LibraryDb.ListPlaylists().Contains("poc-list"));
            LibraryDb.RenamePlaylist("poc-list", "poc-list-2");
            Check(details, ref checks, "playlist.rename", LibraryDb.PlaylistExists("poc-list-2") && !LibraryDb.PlaylistExists("poc-list"));
            LibraryDb.DeletePlaylist("poc-list-2");
            Check(details, ref checks, "playlist.delete", !LibraryDb.PlaylistExists("poc-list-2"));

            // ---- 3) 曲库会话（上次打开的文件夹/文件）往返 ----
            string[] sessionFiles = { @"C:\Music\x.flac", @"D:\Media\y.dsf" };
            LibraryDb.SaveSession("files", null, sessionFiles);
            var session = LibraryDb.LoadSession();
            Check(details, ref checks, "session.roundtrip",
                session != null && session.Mode == "files" && session.FolderPath == null && session.FilePaths.SequenceEqual(sessionFiles));

            // ---- 4) 播放历史往返 + 清空 ----
            LibraryDb.RecordPlayback(@"C:\Music\a.flac", "测试曲目", 42.5, completed: true);
            var history = LibraryDb.LoadPlaybackHistory(10);
            var hit = history.FirstOrDefault(h => h.FilePath == @"C:\Music\a.flac");
            Check(details, ref checks, "history.roundtrip", hit != null && hit.Title == "测试曲目" && Math.Abs(hit.PlayedSeconds - 42.5) < 0.001);
            LibraryDb.ClearPlaybackHistory();
            Check(details, ref checks, "history.clear", LibraryDb.LoadPlaybackHistory(10).Count == 0);

            // ---- 5) 标签索引往返 + 内部缓存清除（走 LibraryPathGuard 全口径）----
            string normalTrack = @"C:\Music\a.flac";
            string cacheTrack = Path.Combine(AppCachePaths.TranscodeCacheDir, "sub", "song.wav");
            LibraryDb.UpsertTracks(new[]
            {
                NewTrack(normalTrack, "正常曲"),
                NewTrack(cacheTrack, "转码缓存产物")
            });
            var index = LibraryDb.LoadTrackIndex(new[] { (normalTrack, 12345L, 678L), (cacheTrack, 12345L, 678L) });
            Check(details, ref checks, "trackindex.roundtrip",
                index.ContainsKey(normalTrack) && index[normalTrack].Title == "正常曲"
                && index.ContainsKey(cacheTrack) && index[cacheTrack].Title == "转码缓存产物");

            // 路径守卫：*.dop24.wav 名字兜底（不依赖任何设置）、正常音乐不拦
            Check(details, ref checks, "guard.dopname", LibraryPathGuard.IsInternalCacheFile(@"X:\anywhere\deep\track.dop24.wav"));
            Check(details, ref checks, "guard.normal", !LibraryPathGuard.IsInternalCacheFile(normalTrack));
            Check(details, ref checks, "guard.transcode", LibraryPathGuard.IsInternalCacheFile(cacheTrack));

            int purged = LibraryDb.PurgeInternalCacheTracks();
            var afterPurge = LibraryDb.LoadTrackIndex(new[] { (normalTrack, 12345L, 678L) });
            Check(details, ref checks, "purge", purged >= 1 && !afterPurge.ContainsKey(cacheTrack) && afterPurge.ContainsKey(normalTrack),
                $"清掉 {purged} 条内部缓存产物，正常曲保留");

            // ---- 6) JSON 读写往返 + DeepClone（临时目录，不碰真实文件）----
            string jsonPath = Path.Combine(tempRoot, "roundtrip.json");
            var payload = new NamedPlaylistDto { Name = "poc", Songs = { @"C:\1.flac", @"C:\2.flac" } };
            JsonFile.Write(jsonPath, payload);
            var back = JsonFile.Read<NamedPlaylistDto>(jsonPath, new NamedPlaylistDto());
            Check(details, ref checks, "json.roundtrip", back.Name == "poc" && back.Songs.Count == 2);
            var cloned = JsonFile.DeepClone(payload);
            cloned.Songs[0] = "changed";
            Check(details, ref checks, "json.deepclone", payload.Songs[0] == @"C:\1.flac" && cloned.Songs[0] == "changed");

            // ---- 7) 路径真值确定性（AppCachePaths）----
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Check(details, ref checks, "paths.transcode", AppCachePaths.TranscodeCacheDir == Path.Combine(localAppData, "CelesteMusicPlayer", "TranscodeCache"));
            Check(details, ref checks, "paths.webdav", AppCachePaths.WebDavCacheDefaultRoot == Path.Combine(localAppData, "CelesteMusicPlayer", "WebDavCache"));
            string expectedDsd = Directory.Exists(@"D:\")
                ? @"D:\CelesteDsdCache"
                : Path.Combine(localAppData, "CelesteMusicPlayer", "DsdCache");
            Check(details, ref checks, "paths.dsd", AppCachePaths.DsdDefaultCacheRoot == expectedDsd);
        }
        finally
        {
            // 归零，别把临时库路径泄漏给后续调用
            LibraryDb.TestDbOverride = null;
            LibraryDb.ResetMigratedForTest();
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* 尽力清理，失败不影响结论 */ }
        }

        bool passed = details.All(d => !d.EndsWith("= FAIL", StringComparison.Ordinal));
        return new Result(passed, checks, details);
    }

    private static LibraryDb.TrackMeta NewTrack(string path, string title) => new()
    {
        FilePath = path,
        MtimeUtc = 12345,
        Size = 678,
        Title = title,
        Artist = "poc",
        AlbumArtist = "poc",
        Album = "poc-album",
        Track = 1,
        Disc = 1,
        Year = 2026,
        Genre = "test",
        DurationTicks = 1_000_000
    };

    private static void Check(List<string> details, ref int checks, string name, bool ok, string? note = null)
    {
        checks++;
        string line = $"{name} = {(ok ? "OK" : "FAIL")}{(note == null ? "" : "（" + note + "）")}";
        details.Add(line);
    }
}
