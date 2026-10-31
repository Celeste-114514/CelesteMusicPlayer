using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Color = Windows.UI.Color;
using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CelesteMusicPlayer;

/// <summary>W13 第三十四刀（2026-10-31）：曲库加载与监听：恢复上次曲库、重扫、批量建项、文件监听与标签保存后刷新、功能挂接与热键。</summary>
public sealed partial class MainWindow
{
        private readonly LibraryWatchService _libraryWatch = new();
        private bool _featuresInitialized;
        /// <summary>后台构建曲库的结果：条目本体 + 需要写回索引的新解析项 + 已失效路径。</summary>
        private sealed class PlaylistBuildResult
        {
            public System.Collections.Generic.List<PlaylistItem> Items = new();

            /// <summary>索引命中数量（未解析标签直接复用的条数），仅用于日志观测。</summary>
            public int IndexHits;

            /// <summary>本次真实解析过标签的条目（索引缺失或已失效），需要写回 tracks 表。</summary>
            public System.Collections.Generic.List<LibraryDb.TrackMeta> Fresh = new();

            /// <summary>路径已不存在（文件被移走/删除），需要从 tracks 表清理。</summary>
            public System.Collections.Generic.List<string> Missing = new();
        }



        // W13 第三十四刀：曲库加载

        /// <summary>
        /// 启动时恢复曲库：先恢复上次打开的文件夹 / 文件列表，
        /// 再把手动加入音乐库的散装文件补进来（它们不在任何被扫描的文件夹里）。
        /// </summary>
        private async Task RestoreLastLibraryAsync()
        {
            // 一次性自愈：清掉历史版本写进曲库会话/索引的内部缓存路径（DSD DoP 缓存等），
            // 否则每次重启都会把缓存文件夹恢复进音乐库（表现为专辑艺术家页多出「未知艺术家」）。
            LibrarySessionStore.SelfHealInternalCachePaths();

            await RestoreLastLibraryCoreAsync();
            await AppendManualLibraryFilesAsync();
            await AppendMediaFolderTracksAsync();

            // 启动时「恢复播放」跑在曲库补齐之前：那一刻 _playlist 还是空的，
            // 状态栏按钮会把刚开始放的这首判成"不在音乐库"，然后就一直挂在「添加到音乐库」上
            // （媒体库文件夹是几秒后才补进来的，补完没人重算）。
            // 曲库定型后在这里统一重算一次，顺便覆盖手动加入 / 媒体库文件夹两条补入路径。
            UpdateAddToLibraryButtonVisibility();
        }

        /// <summary>恢复上次打开的文件夹或音频文件列表。</summary>
        private async Task RestoreLastLibraryCoreAsync()
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                if (!settings.RestoreLibrary)
                {
                    await RestoreLastPlayingTrackAsync();
                    ApplyStartupOverlayWindows();
                    return;
                }

                LibrarySessionState? state = LibrarySessionStore.TryLoad();
                if (state == null)
                {
                    await RestoreLastPlayingTrackAsync();
                    ApplyStartupOverlayWindows();
                    return;
                }

                // 文件夹分类根目录：只要曾选择过文件夹就恢复
                if (!string.IsNullOrWhiteSpace(state.FolderPath) && Directory.Exists(state.FolderPath))
                {
                    _browseFolderPath = state.FolderPath;
                }

                string[] paths;
                if (string.Equals(state.Mode, "folder", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(state.FolderPath)
                    && Directory.Exists(state.FolderPath))
                {
                    string folderPath = state.FolderPath;
                    paths = await Task.Run(() => LibraryFolderScan.AudioFiles(folderPath).ToArray());
                    if (paths.Length == 0)
                    {
                        await RestoreLastPlayingTrackAsync();
                        ApplyStartupOverlayWindows();
                        return;
                    }

                    await LoadLibraryFilesAsync(paths);
                    UpdateStartupLoadingStatus($"正在载入曲库 {_playlist.Count} 首…");
                    LibrarySessionStore.SaveFolder(folderPath, paths);
                    await RestoreLastPlayingTrackAsync();
                    ApplyStartupOverlayWindows();
                    return;
                }

                paths = (state.FilePaths ?? new List<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (paths.Length == 0)
                {
                    await RestoreLastPlayingTrackAsync();
                    ApplyStartupOverlayWindows();
                    return;
                }

                await LoadLibraryFilesAsync(paths);
                UpdateStartupLoadingStatus($"正在载入曲库 {_playlist.Count} 首…");
                await RestoreLastPlayingTrackAsync();
                ApplyStartupOverlayWindows();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复曲库失败: {ex.Message}");
                try
                {
                    ApplyStartupOverlayWindows();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }
        }

        /// <summary>
        /// 把「媒体库」里配的每个文件夹的音频补进音乐库。
        /// 之前只有「选择文件夹」那条路会把歌收进库，往媒体库里加文件夹只是记了个路径：
        /// 于是文件夹在媒体库页面看得见，里面的歌却一首都不在「我的音乐库」里。
        /// 这里每次都按设置重扫一遍（文件夹列表本身存在设置里，不用往曲库会话里塞路径），
        /// 只补库里还没有的，已经在库的原样不动。
        /// </summary>
        private async Task AppendMediaFolderTracksAsync()
        {
            try
            {
                List<string> roots = (AppSettingsStore.Load().LibraryWatchFolders ?? new List<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (roots.Count == 0)
                {
                    return;
                }

                List<string> scanned = await System.Threading.Tasks.Task.Run(() =>
                {
                    var list = new List<string>();
                    foreach (string root in roots)
                    {
                        list.AddRange(FilterLibraryPaths(LibraryFolderScan.AudioFiles(root)));
                    }

                    return list;
                });

                if (scanned.Count == 0)
                {
                    return;
                }

                var known = new HashSet<string>(_playlist.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
                string[] missing = scanned
                    .Where(p => !known.Contains(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (missing.Length == 0)
                {
                    StartupLog.Write($"[library] 媒体库文件夹 {roots.Count} 个根，扫到 {scanned.Count} 首，都在库里了");
                    return;
                }

                // 和「恢复上次会话」走同一条构建路径：后台并行取指纹 → SQLite 索引批量命中
                // （命中直接构造条目，不打开音频文件）→ 只对未命中的做 TagLib 解析 →
                // 列表批量插入、不逐条刷 UI → 解析结果写回索引。
                // 以前这里走 LoadAndAddFiles 的 UI 线程逐个同步解析：3609 首要卡十几秒，
                // 而且解析结果不写索引——下次启动原样再卡一遍（2026-10-09 用户实测反馈）。
                var sw = System.Diagnostics.Stopwatch.StartNew();
                PlaylistBuildResult built = await BuildPlaylistItemsAsync(missing, known,
                    (d, t) => DispatcherQueue.TryEnqueue(() =>
                        UpdateStartupLoadingStatus($"正在载入曲库 {System.Math.Min(d, t)}/{t}…")));

                if (built.Items.Count == 0)
                {
                    StartupLog.Write($"[library] 媒体库文件夹 {roots.Count} 个根，扫到 {scanned.Count} 首，无一可解析");
                    return;
                }

                // 记住当前正在播的文件，排序后要能找回下标（与 LoadAndAddFiles 同口径）
                string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                    ? _playlist[_currentIndex].FilePath
                    : null;

                // 大批量插入时临时解绑列表视图，避免逐条 Add 触发 ListView 反复创建/测量容器
                bool rebounded = ReferenceEquals(_playlistViewSource, _playlist);
                if (rebounded)
                {
                    _playlistViewSource = null;
                }

                foreach (PlaylistItem it in CueTrackExpansion.Expand(built.Items))
                {
                    _playlist.Add(it);
                }

                if (rebounded)
                {
                    _playlistViewSource = _playlist;
                }

                // 收尾与 LoadAndAddFiles 一致：按当前分类刷新（播放队列分类只重编号、不重排）
                if (_currentCategory == "Songs" || _currentCategory == "Albums"
                    || _currentCategory == "Artists" || _currentCategory == "AlbumArtists")
                {
                    ApplyCategoryView();
                }
                else if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
                {
                    RenumberCollection(_queue.Items);
                }
                else
                {
                    ApplySort(preservePlayingPath: playingPath);
                }

                StartupLog.Write($"[library] 媒体库文件夹补入 {built.Items.Count} 首，库内共 {_playlist.Count} 首"
                    + $"（索引命中 {built.IndexHits} 首，实际解析 {built.Fresh.Count} 首），耗时 {sw.ElapsedMilliseconds} ms");
                UpdateStartupLoadingStatus($"正在载入曲库 {_playlist.Count} 首…");

                // 索引写回放到后台：不拖慢启动，失败也不影响播放
                if (built.Fresh.Count > 0 || built.Missing.Count > 0)
                {
                    PlaylistBuildResult snapshot = built;
                    _ = System.Threading.Tasks.Task.Run(() => PersistTrackIndex(snapshot));
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Library.cs", caught);
            }
        }

        private async void RescanLocalLibraryButton_Click(dynamic sender, dynamic e)
        {
            if (_libraryRescanInProgress)
            {
                return;
            }

            _libraryRescanInProgress = true;
            try
            {
                try
                {
                    await RescanLocalLibraryCoreAsync();
                }
                catch (Exception ex)
                {
                    await ShowErrorAsync("重新扫描失败", ex.Message);
                }
            }
            finally
            {
                _libraryRescanInProgress = false;
            }
        }

        private async Task RescanLocalLibraryCoreAsync()
        {
            try
            {
                LibrarySessionState? state = LibrarySessionStore.TryLoad();
                string? folderPath = !string.IsNullOrWhiteSpace(state?.FolderPath)
                    ? state!.FolderPath
                    : _browseFolderPath;

                bool folderMode = state != null
                    && string.Equals(state.Mode, "folder", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(folderPath)
                    && Directory.Exists(folderPath);

                string[] paths;
                if (folderMode)
                {
                    string scanRoot = folderPath!;
                    _browseFolderPath = scanRoot;
                    paths = await Task.Run(() => FilterLibraryPaths(LibraryFolderScan.AudioFiles(scanRoot)));
                    LibrarySessionStore.SaveFolder(scanRoot, paths);
                }
                else
                {
                    // 文件列表模式：重读已保存路径；若会话缺失则用当前曲库路径
                    IEnumerable<string> sourcePaths = (state?.FilePaths != null && state.FilePaths.Count > 0)
                        ? state.FilePaths
                        : _playlist.Select(p => p.FilePath);

                    paths = FilterLibraryPaths(sourcePaths);

                    if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
                    {
                        _browseFolderPath = folderPath;
                    }

                    if (paths.Length > 0)
                    {
                        LibrarySessionStore.SaveFiles(paths);
                    }
                }

                if (!string.IsNullOrWhiteSpace(_browseFolderPath))
                {
                    RefreshFolderBrowserRoots();
                }

                if (paths.Length == 0)
                {
                    await ReplaceLibraryWithPaths(Array.Empty<string>(), persist: false);
                    await ShowErrorAsync("重新扫描", "未找到可读取的本地音频。请先通过「选择文件」或「选择文件夹」导入。");
                    return;
                }

                await ReplaceLibraryWithPaths(paths, persist: false);

                // 重扫描会清空列表按文件夹重建，手动加入音乐库的散装文件不在文件夹里，得补回来
                await AppendManualLibraryFilesAsync();
                await AppendMediaFolderTracksAsync();

                // 重新扫描同样会清空重建 _playlist，正在放的那首的库归属要重算
                // （否则扫完还挂着「添加到音乐库」，跟启动时那个 bug 同源）
                UpdateAddToLibraryButtonVisibility();

            }
            catch (Exception ex)
            {
                await ShowErrorAsync("重新扫描失败", ex.Message);
            }
        }

        /// <summary>清空音乐库后按路径重建元数据，并刷新当前分类界面。
        /// 元数据读取放后台分批执行，并在扫描过程中以「扫描 xxx/xxx」更新左上角状态。</summary>
        private async System.Threading.Tasks.Task ReplaceLibraryWithPaths(string[] filePaths, bool persist)
        {
            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;

            bool rebindPlaylistView = ReferenceEquals(_playlistViewSource, _playlist);
            if (rebindPlaylistView)
            {
                _playlistViewSource = null;
            }

            _playlist.Clear();
            _albums.Clear();
            _artists.Clear();
            _albumTracks.Clear();
            _artistTracks.Clear();
            _artistAlbums.Clear();
            CloseAlbumDetailUi();
            CloseArtistDetailUi();

            if (filePaths.Length > 0)
            {
                int total = filePaths.Length;
                var built = new System.Collections.Generic.List<PlaylistItem>(total);
                // 后台分批读取元数据（纯文件/TagLib，不碰 UI），每处理一批派发一次进度到左上角状态
                await Task.Run(() =>
                {
                    var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int done = 0;
                    foreach (string path in filePaths)
                    {
                        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                        {
                            continue;
                        }

                        if (!knownPaths.Add(path))
                        {
                            continue;
                        }

                        try
                        {
                            built.Add(CreatePlaylistItemFromPath(path));
                        }
                        catch (Exception ex)
                        {
                            knownPaths.Remove(path);
                            System.Diagnostics.Debug.WriteLine($"扫描加载失败: {path} → {ex.Message}");
                        }

                        done++;
                        if ((done & 15) == 0)
                        {
                            int d = done;
                            DispatcherQueue.TryEnqueue(() => NowPlayingText.Text = $"扫描 {Math.Min(d, total)}/{total}");
                        }
                    }
                });

                // CUE 分轨展开（设置关时原样返回）：一个整轨文件 → N 轨
                built = CueTrackExpansion.Expand(built);

                DispatcherQueue.TryEnqueue(() => NowPlayingText.Text = $"扫描完成，共 {built.Count} 首");
                foreach (PlaylistItem item in built)
                {
                    _playlist.Add(item);
                }
            }

            if (rebindPlaylistView
                || string.Equals(_currentCategory, "Songs", StringComparison.Ordinal))
            {
                _playlistViewSource = _playlist;
            }

            if (!string.IsNullOrWhiteSpace(playingPath))
            {
                UpdateCurrentIndexByPath(playingPath);
            }
            else
            {
                _currentIndex = -1;
            }

            ApplyCategoryView();

            if (persist)
            {
                // Distinct：CUE 分轨展开后同一文件对应多条，会话里按文件去重（恢复时再展开）
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath).Distinct(StringComparer.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// 后台线程批量构建播放条目：优先命中 SQLite 标签索引，只对「索引里没有、或文件被改动过」的才真正解析标签。
        /// 首次运行索引为空，行为与原来的全量解析一致；之后启动绝大多数文件可跳过 TagLib 解析。
        /// </summary>
        private System.Threading.Tasks.Task<PlaylistBuildResult> BuildPlaylistItemsAsync(
            string[] filePaths,
            System.Collections.Generic.ISet<string> knownPaths,
            System.Action<int, int>? onProgress = null)
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                int total = filePaths.Length;
                var result = new PlaylistBuildResult();

                // 按索引回填，保证结果与输入顺序一致（顺序扫描的原有行为）
                PlaylistItem?[] results = new PlaylistItem?[total];
                bool[] keep = new bool[total];
                long[] mtimes = new long[total];
                long[] sizes = new long[total];

                // 读标签以磁盘 I/O 为主（曲库常在网络盘/云同步盘上），适度并行可显著缩短总耗时；
                // 上限 4 避免机械盘随机寻道劣化与云盘连接被打满。
                var options = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = System.Math.Max(1, System.Math.Min(4, System.Environment.ProcessorCount))
                };

                // 1) 先并行取文件指纹（修改时间 + 大小）。这只是元数据读取，比解析标签便宜得多，
                //    用它判断索引是否仍然有效；mtime 与 size 同时比对，避免云盘只改其一导致误判。
                System.Threading.Tasks.Parallel.For(0, total, options, i =>
                {
                    string path = filePaths[i];
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return;
                    }

                    try
                    {
                        var fi = new System.IO.FileInfo(path);
                        if (!fi.Exists)
                        {
                            return;
                        }

                        mtimes[i] = fi.LastWriteTimeUtc.Ticks;
                        sizes[i] = fi.Length;
                        keep[i] = true;
                    }
                    catch
                    {
                        keep[i] = false;
                    }
                });

                // 2) 去重 + 收集待查指纹
                var stamps = new System.Collections.Generic.List<(string path, long mtimeUtc, long size)>(total);
                for (int i = 0; i < total; i++)
                {
                    if (!keep[i])
                    {
                        if (!string.IsNullOrWhiteSpace(filePaths[i]))
                        {
                            result.Missing.Add(filePaths[i]);
                        }

                        continue;
                    }

                    bool isNew;
                    lock (knownPaths)
                    {
                        isNew = knownPaths.Add(filePaths[i]);
                    }

                    if (!isNew)
                    {
                        keep[i] = false;
                        continue;
                    }

                    stamps.Add((filePaths[i], mtimes[i], sizes[i]));
                }

                // 3) 批量查索引：只返回指纹仍匹配的条目
                System.Collections.Generic.Dictionary<string, LibraryDb.TrackMeta> cached = LibraryDb.LoadTrackIndex(stamps);

                var needScan = new System.Collections.Generic.List<int>();
                for (int i = 0; i < total; i++)
                {
                    if (!keep[i])
                    {
                        continue;
                    }

                    if (cached.TryGetValue(filePaths[i], out LibraryDb.TrackMeta? meta) && meta != null)
                    {
                        results[i] = CreatePlaylistItemFromMeta(meta);
                    }
                    else
                    {
                        needScan.Add(i);
                    }
                }

                result.IndexHits = results.Count(x => x != null);

                // 4) 只有未命中的才真正解析标签
                int done = result.IndexHits;
                var fresh = new System.Collections.Concurrent.ConcurrentBag<LibraryDb.TrackMeta>();
                System.Threading.Tasks.Parallel.For(0, needScan.Count, options, k =>
                {
                    int i = needScan[k];
                    string path = filePaths[i];
                    try
                    {
                        PlaylistItem item = CreatePlaylistItemFromPath(path);
                        results[i] = item;
                        fresh.Add(new LibraryDb.TrackMeta
                        {
                            FilePath = path,
                            MtimeUtc = mtimes[i],
                            Size = sizes[i],
                            Title = item.Title,
                            Artist = item.Artist,
                            AlbumArtist = item.AlbumArtist,
                            Album = item.Album,
                            Track = item.Track,
                            Disc = item.Disc,
                            Year = item.Year,
                            Genre = item.Genre,
                            DurationTicks = item.Duration.Ticks
                        });
                    }
                    catch (System.Exception ex)
                    {
                        lock (knownPaths)
                        {
                            knownPaths.Remove(path);
                        }

                        System.Diagnostics.Debug.WriteLine($"扫描加载失败: {path} → {ex.Message}");
                        return;
                    }

                    int d = System.Threading.Interlocked.Increment(ref done);
                    if ((d & 31) == 0)
                    {
                        onProgress?.Invoke(d, total);
                    }
                });

                for (int i = 0; i < total; i++)
                {
                    PlaylistItem? item = results[i];
                    if (item != null)
                    {
                        result.Items.Add(item);
                    }
                }

                result.Fresh.AddRange(fresh);
                return result;
            });
        }

        /// <summary>启动恢复曲库：后台读标签 + 批量写入列表（不逐条触发 UI 刷新），随后由调用方恢复上次播放。</summary>
        private async System.Threading.Tasks.Task LoadLibraryFilesAsync(string[] paths)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var known = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            PlaylistBuildResult built = await BuildPlaylistItemsAsync(paths, known,
                (d, t) => DispatcherQueue.TryEnqueue(() => NowPlayingText.Text = $"加载 {System.Math.Min(d, t)}/{t}"));

            bool rebounded = ReferenceEquals(_playlistViewSource, _playlist);
            if (rebounded)
            {
                _playlistViewSource = null;
            }

            _playlist.Clear();
            // CUE 分轨展开（设置关时原样返回）；Fresh 写索引用的是文件级元数据，不受影响
            built.Items = CueTrackExpansion.Expand(built.Items);
            foreach (PlaylistItem it in built.Items)
            {
                _playlist.Add(it);
            }

            if (rebounded)
            {
                _playlistViewSource = _playlist;
            }

            StartupLog.Write($"[library] 后台加载完成：扫描 {paths.Length} 个路径，入库 {built.Items.Count} 首"
                + $"（索引命中 {built.IndexHits} 首，实际解析 {built.Fresh.Count} 首），"
                + $"列表共 {_playlist.Count} 首，耗时 {sw.ElapsedMilliseconds} ms");
            ApplyCategoryView();

            // 索引写回放到后台：不拖慢启动，失败也不影响播放
            if (built.Fresh.Count > 0 || built.Missing.Count > 0)
            {
                PlaylistBuildResult snapshot = built;
                _ = System.Threading.Tasks.Task.Run(() => PersistTrackIndex(snapshot));
            }
        }

        /// <summary>把本次新解析的标签写回 SQLite 索引，并清理已失效路径。后台执行，失败静默。</summary>
        private static void PersistTrackIndex(PlaylistBuildResult result)
        {
            try
            {
                if (result.Fresh.Count > 0)
                {
                    LibraryDb.UpsertTracks(result.Fresh);
                }

                // 整盘离线（移动硬盘拔出、云盘未同步）时路径会大面积「不存在」，
                // 此时若照常清理会把整个索引删空，下次上线又得全量重扫。
                // 只在缺失量很小时才清理：宁可留几条陈旧记录，也不误删整库。
                if (result.Missing.Count > 0 && result.Missing.Count <= 2000
                    && result.Missing.Count < System.Math.Max(100, result.Items.Count / 4))
                {
                    LibraryDb.DeleteTracks(result.Missing);
                }
                else if (result.Missing.Count > 0)
                {
                    StartupLog.Write($"[library] 跳过索引清理：缺失 {result.Missing.Count} 条（入库 {result.Items.Count} 首），疑似整盘离线");
                }

                StartupLog.Write($"[library] 索引写回完成：新增/更新 {result.Fresh.Count} 条，索引总数 {LibraryDb.CountTracks()}");
            }
            catch (System.Exception ex)
            {
                StartupLog.Write($"[library] 索引写回失败（不影响播放）：{ex.Message}");
            }
        }

        // W13 第三十四刀：路径过滤

        /// <summary>
        /// 按上次会话的文件夹或文件列表重新扫描，清空并替换当前音乐库展示。
        /// </summary>
        /// <summary>按媒体库设置过滤路径：移除缺失文件 / 忽略过短文件 / 剔除内部缓存产物。</summary>
        private static string[] FilterLibraryPaths(IEnumerable<string> paths)
        {
            AppSettingsState s = AppSettingsStore.Load();
            var result = new List<string>();
            foreach (string path in paths
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // 内部缓存产物（DSD DoP 缓存 / 转码缓存 / WebDAV 下载缓存）不是本地曲库内容，永不进媒体库
                if (LibraryPathGuard.IsLibraryExcludedFile(path))
                {
                    continue;
                }

                if (s.RemoveMissingOnUpdate && !System.IO.File.Exists(path))
                {
                    continue;
                }

                if (s.IgnoreTooShortOnUpdate && s.FileTooShortSec > 0 && System.IO.File.Exists(path))
                {
                    try
                    {
                        using TagLib.File tagFile = TagLib.File.Create(path);
                        if (tagFile.Properties.Duration.TotalSeconds < s.FileTooShortSec)
                        {
                            continue;
                        }
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }

                result.Add(path);
            }

            return result.ToArray();
        }

        // W13 第三十四刀：功能挂接 / 曲库监听 / 标签刷新

        private void InitializeMusicPlayer2Features()
        {
            if (_featuresInitialized)
            {
                return;
            }

            _featuresInitialized = true;

            // EQ is applied by the production web DSP commands.
            // TagEditorWindow.TagsSaved 订阅已随旧窗口删除移除：标签板块的 TagsSaved
            // 在 EnsureTagEditUiBuilt 里边创建面板边订阅（面板是延迟创建的）。

            MediaSession.ConfigureFromSettings();
            ApplyPlaybackRateFromSettings();
            RestartGlobalHotkeysFromSettings();
            RestartLibraryWatchFromSettings();
            AttachRootKeyboardAccelerators();
            UpdateFavoriteButtonUi();
            AppSettingsState boot = AppSettingsStore.Load();
            ApplyNavVisibilityFromSettings(boot);
            ApplySpectrumVisibilityFromSettings(boot);
            ApplyCoverVisibilityFromSettings(boot);
            AttachGlobalMouseWheelVolume();
            AttachLyricSaveConfirm();
        }

        private void DisposeMusicPlayer2Features()
        {
            try
            {

                // 标签面板的 TagsSaved 退订：面板可能还没创建（_tagPanel 为 null 时无从退起，
                // 随窗口一起销毁，无需处理）。

            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _hotkeys?.StopListening();
                _hotkeys?.Dispose();
                _hotkeys = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            try
            {
                _libraryWatch.Stop();
                _libraryWatch.Dispose();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>
        /// 「询问」歌词保存策略的确认弹窗。OnlineMusicApi 内部用了 ConfigureAwait(false)，
        /// 回调可能在后台线程被调用——必须先切回 UI 线程再弹 ContentDialog。
        /// </summary>
        private void AttachLyricSaveConfirm()
        {
            OnlineMusicApi.LyricSaveConfirm = async candidatePath =>
            {
                TaskCompletionSource<bool> tcs = new();
                bool enqueued = DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "保存歌词",
                            Content = "已找到歌词，是否保存到：\n" + Path.GetFileName(candidatePath),
                            PrimaryButtonText = "保存",
                            CloseButtonText = "不保存",
                            DefaultButton = ContentDialogButton.Primary,
                            XamlRoot = Content.XamlRoot
                        };
                        tcs.TrySetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
                    }
                    catch (Exception caught)
                    {
                        global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught);
                        tcs.TrySetResult(false);
                    }
                });

                if (!enqueued)
                {
                    return false; // 主窗口已销毁：不落盘
                }

                return await tcs.Task;
            };
        }

        private void AttachGlobalMouseWheelVolume()
        {
            if (Content is not UIElement root)
            {
                return;
            }

            root.PointerWheelChanged -= Root_PointerWheelChanged;
            root.PointerWheelChanged += Root_PointerWheelChanged;
        }

        private void Root_PointerWheelChanged(dynamic sender, dynamic e)
        {
            if (!AppSettingsStore.Load().GlobalMouseWheelVolume)
            {
                return;
            }

            if (FocusManager.GetFocusedElement(Content.XamlRoot) is Slider)
            {
                return;
            }

            int delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }

            AdjustVolumeBy(delta > 0 ? 2 : -2);
            e.Handled = true;
        }

        private void AttachRootKeyboardAccelerators()
        {
            if (Content is not UIElement root)
            {
                return;
            }

            root.KeyDown -= Root_KeyDown;
            root.KeyDown += Root_KeyDown;
        }

        private void Root_KeyDown(dynamic sender, dynamic e)
        {
            if (e.Handled)
            {
                return;
            }

            // 输入框内不抢快捷键
            if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or AutoSuggestBox)
            {
                return;
            }

            VirtualKey key = e.Key;
            if (key == VirtualKey.Space)
            {
                TogglePlayPausePublic();
                e.Handled = true;
            }
            else if (key == VirtualKey.Left)
            {
                SeekBySeconds(-5);
                e.Handled = true;
            }
            else if (key == VirtualKey.Right)
            {
                SeekBySeconds(5);
                e.Handled = true;
            }
            // 「查找歌曲」已移除（老版本遗留），F / F3 不再绑定任何动作。
        }

        internal void ToggleFavoriteForCurrent()
        {
            string? path = _nowPlayingPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count)
                {
                    path = _queue.Items[_queue.Index].FilePath;
                }
                else if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
                {
                    path = _playlist[_currentIndex].FilePath;
                }
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            bool fav = TrackStatsStore.ToggleFavorite(path);
            NamedPlaylistStore.SyncFavoritesPlaylist();
            UpdateFavoriteButtonUi();
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                ApplyCategoryView();
            }

            // 任务栏缩略图按钮：fav=true 实心红心 / fav=false 空心轮廓心
            _taskbarButtons?.UpdateFavorite(fav);

        }

        private void RestartLibraryWatchFromSettings()
        {
            _libraryWatch.Changed -= LibraryWatch_Changed;
            _libraryWatch.Stop();

            AppSettingsState settings = AppSettingsStore.Load();
            if (!settings.AutoUpdateLibrary)
            {
                return;
            }

            var folders = new List<string>();
            if (settings.LibraryWatchFolders != null)
            {
                folders.AddRange(settings.LibraryWatchFolders.Where(Directory.Exists));
            }

            try
            {
                string? sessionFolder = LibrarySessionStore.TryLoad()?.FolderPath;
                if (!string.IsNullOrWhiteSpace(sessionFolder)
                    && Directory.Exists(sessionFolder)
                    && !LibraryPathGuard.IsInternalCacheDir(sessionFolder)
                    && !folders.Any(f => string.Equals(f, sessionFolder, StringComparison.OrdinalIgnoreCase)))
                {
                    folders.Add(sessionFolder);
                    AppSettingsStore.Update(s =>
                    {
                        s.LibraryWatchFolders ??= new List<string>();
                        if (!s.LibraryWatchFolders.Any(f =>
                                string.Equals(f, sessionFolder, StringComparison.OrdinalIgnoreCase)))
                        {
                            s.LibraryWatchFolders.Add(sessionFolder);
                        }
                    });
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }

            if (folders.Count == 0)
            {
                return;
            }

            _libraryWatch.Changed += LibraryWatch_Changed;
            _libraryWatch.Start(folders);
        }

        private void LibraryWatch_Changed(IReadOnlyList<string> paths)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (paths.Count == 0)
                {
                    return;
                }

                if (!AppSettingsStore.Load().AutoUpdateLibrary)
                {
                    return;
                }

                // 去抖合并:1 秒内的连续变更只触发一次重扫
                _libraryWatchDebounce ??= DispatcherQueue.CreateTimer();
                _libraryWatchDebounce.Interval = TimeSpan.FromMilliseconds(1000);
                _libraryWatchDebounce.IsRepeating = false;
                _libraryWatchDebounce.Tick -= OnLibraryWatchDebounceTick;
                _libraryWatchDebounce.Tick += OnLibraryWatchDebounceTick;
                _libraryWatchDebounce.Start();
            });
        }

        private void OnLibraryWatchDebounceTick(dynamic sender, dynamic e)
        {
            if (_libraryRescanInProgress)
            {
                // 上一次重扫还没完成:稍后重试
                _libraryWatchDebounce?.Start();
                return;
            }

            RescanLocalLibraryButton_Click(null!, new RoutedEventArgs());
        }

        private void OnTagsSaved(string path)
        {
            DispatcherQueue.TryEnqueue(() => RefreshTrackMetadataFromDisk(path));
        }

        private void RefreshTrackMetadataFromDisk(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                PlaylistItem fresh = CreatePlaylistItemFromPath(path);
                ReplaceMetadataInCollection(_playlist, fresh);
                ReplaceMetadataInCollection(_queue.Items, fresh);
                if (string.Equals(_nowPlayingPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _ = UpdateNowPlayingPanelAsync(fresh);
                }

                if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal)
                    || string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal)
                    || string.Equals(_currentCategory, "Recent", StringComparison.Ordinal))
                {
                    ApplyCategoryView();
                }

                NotifyCurrentPlaylistWindow();
            }
            catch (Exception ex)
            {
                _ = ShowErrorAsync("刷新标签失败", ex.Message);
            }
        }

        private static void ReplaceMetadataInCollection(
            System.Collections.ObjectModel.ObservableCollection<PlaylistItem> list,
            PlaylistItem fresh)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!string.Equals(list[i].FilePath, fresh.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                PlaylistItem old = list[i];
                old.Title = fresh.Title;
                old.Artist = fresh.Artist;
                old.AlbumArtist = fresh.AlbumArtist;
                old.Album = fresh.Album;
                old.Track = fresh.Track;
                old.Year = fresh.Year;
                old.Duration = fresh.Duration;
                old.Genre = fresh.Genre;
            }
        }
    }
