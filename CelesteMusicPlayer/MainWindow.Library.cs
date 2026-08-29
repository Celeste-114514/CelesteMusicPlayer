using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shapes = Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
// TagLibSharp：包名 TagLibSharp，命名空间 TagLib
using TagLib;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Color = Windows.UI.Color;


namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {

        /// <summary>启动时恢复上次打开的文件夹或音频文件列表。</summary>
        private async Task RestoreLastLibraryAsync()
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
                    paths = await Task.Run(() => EnumerateAudioFiles(folderPath).ToArray());
                    if (paths.Length == 0)
                    {
                        await RestoreLastPlayingTrackAsync();
                        ApplyStartupOverlayWindows();
                        return;
                    }

                    await LoadLibraryFilesAsync(paths);
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
        /// 选择文件夹：递归扫描其中所有支持的音频，再交给 LoadAndAddFiles。
        /// </summary>
        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FolderPicker picker = new();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                // FolderPicker 至少要加一个过滤器，常用 "*"
                picker.FileTypeFilter.Add("*");

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
                {
                    return;
                }

                // 后台枚举路径，避免大文件夹卡住 UI 太久；读标签仍在 LoadAndAddFiles
                string folderPath = folder.Path;
                _browseFolderPath = folderPath;

                string[] paths = await System.Threading.Tasks.Task.Run(() =>
                    EnumerateAudioFiles(folderPath).ToArray());

                LibrarySessionStore.SaveFolder(folderPath, paths);
                if (_currentCategory == "Folders")
                {
                    RefreshFolderBrowserRoots();
                }

                if (paths.Length == 0)
                {
                    await ShowErrorAsync("未找到音频", "该文件夹（含子文件夹）中没有支持的音频文件。");
                    return;
                }

                LoadAndAddFiles(paths, persist: false, replace: true);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("选择文件夹失败", ex.Message);
            }
        }


        private async void RescanLocalLibraryButton_Click(object sender, RoutedEventArgs e)
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
                    paths = await Task.Run(() => FilterLibraryPaths(EnumerateAudioFiles(scanRoot)));
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
                NowPlayingText.Text = $"已重新扫描，共 {_playlist.Count} 首";
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

            bool rebindPlaylistView = ReferenceEquals(PlaylistView.ItemsSource, _playlist);
            if (rebindPlaylistView)
            {
                PlaylistView.ItemsSource = null;
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

                DispatcherQueue.TryEnqueue(() => NowPlayingText.Text = $"扫描完成，共 {built.Count} 首");
                foreach (PlaylistItem item in built)
                {
                    _playlist.Add(item);
                }
            }

            if (rebindPlaylistView
                || string.Equals(_currentCategory, "Songs", StringComparison.Ordinal))
            {
                PlaylistView.ItemsSource = _playlist;
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
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath));
            }
        }


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

            bool rebounded = ReferenceEquals(PlaylistView.ItemsSource, _playlist);
            if (rebounded)
            {
                PlaylistView.ItemsSource = null;
            }

            _playlist.Clear();
            foreach (PlaylistItem it in built.Items)
            {
                _playlist.Add(it);
            }

            if (rebounded)
            {
                PlaylistView.ItemsSource = _playlist;
            }

            NowPlayingText.Text = $"已加载 {built.Items.Count} 首，共 {_playlist.Count} 首";
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


        /// <summary>递归枚举文件夹内所有音频路径</summary>
        private static IEnumerable<string> EnumerateAudioFiles(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                yield break;
            }

            // 逐目录递归:某个受保护子目录(无权限)只跳过该目录,不影响其它文件
            foreach (string path in EnumerateAudioFilesRecursive(folderPath))
            {
                yield return path;
            }
        }


        private static IEnumerable<string> EnumerateAudioFilesRecursive(string folder)
        {
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(folder);
                files = Directory.GetFiles(folder);
            }
            catch
            {
                yield break;
            }

            foreach (string path in files)
            {
                string ext = Path.GetExtension(path);
                if (AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }

            foreach (string sub in subDirs)
            {
                foreach (string path in EnumerateAudioFilesRecursive(sub))
                {
                    yield return path;
                }
            }
        }


        private void NavTagSortButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "TagSort";
                ApplyCategoryView();
            });
        }


        /// <summary>删除用户预设（从保存预at预设对话框里的删除按钮后调用）。</summary>
        /// <summary>「管理（删除）我的预设」流程：列出用户预设，选一个确认删除。</summary>
        private async Task DeleteAudioFxUserPresetFlow()
        {
            var presets = EqUserPresetStore.Load();
            if (presets.Count == 0)
            {
                NowPlayingText.Text = "还没有已保存的用户预设";
                return;
            }

            var listView = new ListView
            {
                Height = 260,
                SelectionMode = ListViewSelectionMode.Single
            };
            foreach (var p in presets)
            {
                listView.Items.Add(new ListViewItem { Content = string.IsNullOrWhiteSpace(p.PresetName) ? "未命名" : p.PresetName, Tag = p.PresetId });
            }

            var dialog = new ContentDialog
            {
                Title = "删除我的预设",
                Content = listView,
                PrimaryButtonText = "删除所选",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content?.XamlRoot ?? AudioFxBorder.XamlRoot
            };
            ContentDialogResult r;
            try { r = await dialog.ShowAsync(); }
            catch { r = ContentDialogResult.None; }
            if (r != ContentDialogResult.Primary || listView.SelectedItem is not ListViewItem { Tag: string selId })
            {
                return;
            }

            string selName = (listView.SelectedItem as ListViewItem)?.Content?.ToString() ?? "预设";
            await DeleteAudioFxUserPreset(selId, selName);
        }


        /// <summary>删除单个用户预设（带确认）。</summary>
        private async Task DeleteAudioFxUserPreset(string presetId, string name)
        {
            var dialog = new ContentDialog
            {
                Title = "删除预设",
                Content = "删除预设「" + name + "」？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content?.XamlRoot ?? AudioFxBorder.XamlRoot
            };
            ContentDialogResult r;
            try { r = await dialog.ShowAsync(); }
            catch { r = ContentDialogResult.None; }
            if (r != ContentDialogResult.Primary) return;

            EqUserPresetStore.Delete(presetId);
            if (string.Equals(_audioFxEq.PresetId, presetId, StringComparison.Ordinal))
            {
                _audioFxEq.PresetId = "custom";
                _audioFxEq.PresetName = "自定义";
            }

            RefreshAudioFxUserPresetItems();
            NowPlayingText.Text = "已删除预设：" + name;
        }


        // ---------------- 标签排序板块（按曲目元数据字段逐级分组钻取） ----------------

        private void BreakoutTagSortView()
        {
            TagSortBorder.Visibility = Visibility.Visible;
            ApplyTagSortFieldButtons();
            ShowTagSortClassWall();
        }


        /// <summary>高亮当前分类字段按钮（五个维度切换）。</summary>
        private void ApplyTagSortFieldButtons()
        {
            var accent = ResolveAccentBrush();
            var fg = ResolveContrastingForeground(accent);
            var idleBg = ResolveCapsuleFillBrush();
            var border = ResolveNavCapsuleBorderBrush();
            void Update(Button b)
            {
                bool active = string.Equals(b.Tag as string, _tagSortClassField, StringComparison.Ordinal);
                if (active) { b.Background = accent; b.Foreground = fg; b.BorderThickness = new Thickness(0); }
                else { b.Background = idleBg; b.ClearValue(Control.ForegroundProperty); b.BorderThickness = new Thickness(1); b.BorderBrush = border; }
            }
            Update(TagSortFieldArtistButton);
            Update(TagSortFieldAlbumArtistButton);
            Update(TagSortFieldAlbumButton);
            Update(TagSortFieldGenreButton);
            Update(TagSortFieldYearButton);
        }


        private void TagSortFieldBoundButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string field)
            {
                _tagSortClassField = field;
                _tagSortClassValue = string.Empty;
                ApplyTagSortFieldButtons();
                ShowTagSortClassWall();
            }
        }


        // 标签排序分类封面：封面字节内存缓存 + 并发控制（缓存命中免重复读文件/解码，避免大量分类同时打满线程池与 UI 线程）
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> TagSortCoverBytesCache = new();
        private const int TagSortCoverBytesCacheMax = 1024; // 上限：超量则清空，避免几千个分类封面字节常驻内存（之前无上限 → 内存泄漏）
        private static readonly System.Threading.SemaphoreSlim TagSortCoverGate = new(4);

        private async System.Threading.Tasks.Task LoadTagSortCategoryCoverAsync(TagSortCategoryEntry entry)
        {
            try
            {
                string key = entry?.FirstFilePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                byte[]? bytes = null;
                await TagSortCoverGate.WaitAsync(); // 限流并发提取，避免几十个专辑同时解码拖慢 UI
                try
                {
                    if (!TagSortCoverBytesCache.TryGetValue(key, out bytes) || bytes == null || bytes.Length == 0)
                    {
                        bytes = await Task.Run(() => ExtractCoverBytes(key));
                        if (bytes is { Length: > 0 })
                        {
                            TagSortCoverBytesCache[key] = bytes;
                            if (TagSortCoverBytesCache.Count >= TagSortCoverBytesCacheMax)
                            {
                                TagSortCoverBytesCache.Clear(); // 容量上限保护（与 Library2.cs 的 CoverBytesCache 策略一致）
                            }
                        }
                    }
                }
                finally
                {
                    TagSortCoverGate.Release();
                }

                if (bytes is { Length: > 0 })
                {
                    var bmp = await CreateBitmapFromBytesAsync(bytes);
                    if (bmp != null) entry.Cover = bmp;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void TagSortClassGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is TagSortCategoryEntry entry)
            {
                _tagSortClassValue = entry.Name;
                _tagSortPanelMode = "Songs";
                ShowTagSortPanel();
            }
        }


        /// <summary>进入分类面板：过滤当前分类下的曲目，按 _tagSortPanelMode 展示。</summary>
        private void ShowTagSortPanel()
        {
            TagSortClassScroll.Visibility = Visibility.Collapsed;
            TagSortPanel.Visibility = Visibility.Visible;
            _tagSortClassSongs.Clear();
            foreach (var p in _playlist)
            {
                if (string.Equals(TagSortFieldVal(p, _tagSortClassField), _tagSortClassValue, StringComparison.Ordinal))
                {
                    _tagSortClassSongs.Add(p);
                }
            }
            TagSortPanelTitle.Text = TagSortClassFieldLabel(_tagSortClassField) + "：" + _tagSortClassValue;
            ApplyTagSortPanelMode();
        }


        private static string TagSortClassFieldLabel(string field)
        {
            return field switch
            {
                "Artist" => "艺术家", "AlbumArtist" => "专辑艺术家", "Album" => "专辑",
                "Genre" => "流派", "Year" => "年份", _ => field
            };
        }


        /// <summary>排序字段的中文标签（给排序依据状态显示用）。</summary>
        private static string TagSortFieldLabel(string field)
        {
            return field switch
            {
                "Artist" => "艺术家", "AlbumArtist" => "专辑艺术家", "Album" => "专辑",
                "Genre" => "流派", "Year" => "年份", "Title" => "标题",
                "Track" => "音轨号", "Disc" => "碟片号", _ => field
            };
        }


        private void TagSortPanelBackButton_Click(object sender, RoutedEventArgs e)
        {
            _tagSortClassValue = string.Empty;
            ShowTagSortClassWall();
        }


        private void TagSortViewModeItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string mode)
            {
                _tagSortPanelMode = mode;
                ApplyTagSortPanelMode();
            }
        }


        private void TagSortPanelGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            // 专辑/艺术家视角：点某项 → 钻取到该值的曲目列表（切到“曲目”视角显示该子集）
            if (e.ClickedItem is TagSortCategoryEntry cat)
            {
                string field = cat.Sub == "Artist" ? "Artist" : "Album";
                _tagSortPanelMode = "Songs";
                // 过滤当前分类下再按该子值
                var filtered = _tagSortClassSongs.Where(p => string.Equals(
                    field == "Artist" ? (p.Artist ?? "") : (p.Album ?? ""), cat.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                _tagSortClassSongs.Clear();
                foreach (var s in filtered) _tagSortClassSongs.Add(s);
                TagSortPanelTitle.Text = field == "Artist" ? "艺术家：" : "专辑：" + cat.Name;
                ApplyTagSortPanelMode();
            }
        }


        private void TagSortClassGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode) return;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is TagSortCategoryEntry entry)
            {
                ShowTagSortCategoryMenu(entry, TagSortClassGridView, e.GetPosition(TagSortClassGridView));
            }
        }


        private void TagSortPanelGridView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode) return;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is TagSortCategoryEntry entry)
            {
                var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

                var multi = new MenuFlyoutItem { Text = "多选", Icon = new FontIcon { Glyph = "\uE700" } };
                multi.Click += (_, _) => EnterTagSortPanelGridMultiSelect();
                flyout.Items.Add(multi);

                var play = new MenuFlyoutItem
                {
                    Text = entry.Sub == "Artist" ? "播放该艺术家" : "播放该专辑",
                    Icon = new FontIcon { Glyph = "\uE768" }
                };
                play.Click += (_, _) =>
                {
                    var songs = CollectTagSortCategorySongs(entry);
                    if (songs.Count > 0)
                    {
                        if (entry.Sub == "Album")
                        {
                            var a = BuildTagSortAlbumEntry(entry);
                            PlayAlbum(a, replacePlaylist: true);
                        }
                        else
                        {
                            PlayPlaylistItem(songs[0]);
                        }
                    }
                };
                flyout.Items.Add(play);

                var addQueue = new MenuFlyoutItem { Text = "添加到播放队列", Icon = new FontIcon { Glyph = "\uE710" } };
                addQueue.Click += (_, _) => AddSongsToUserPlaylist(CollectTagSortCategorySongs(entry));
                flyout.Items.Add(addQueue);

                if (entry.Sub == "Album")
                {
                    var albumEntry = BuildTagSortAlbumEntry(entry);

                    var wallAlbum = new MenuFlyoutItem { Text = "添加到播放列表", Icon = new FontIcon { Glyph = "\uE8B7" } };
                    wallAlbum.Click += (_, _) => _ = ShowNamedPlaylistPickerAsync(GetTracksForAlbum(albumEntry));
                    flyout.Items.Add(wallAlbum);

                    // 批量下载歌词 / 封面 / 复制专辑信息 / 打开专辑详情（复用专辑墙通用项）
                    AppendAlbumContextItems(flyout, albumEntry, fromArtist: false);
                }

                flyout.ShowAt(TagSortPanelGridView, e.GetPosition(TagSortPanelGridView));
            }
        }


        /// <summary>把标签排序面板的专辑项构造成 AlbumEntry（Artist 留空 → 按专辑名匹配曲目）供复用现有专辑操作。</summary>
        private AlbumEntry BuildTagSortAlbumEntry(TagSortCategoryEntry entry)
        {
            return new AlbumEntry
            {
                Name = entry.Name,
                Artist = string.Empty,
                CoverSourcePath = entry.FirstFilePath
            };
        }


        /// <summary>标签排序面板专辑/艺术家网格进入多选：勾选多张专辑/艺术家，主按钮添加到播放队列。</summary>
        private void EnterTagSortPanelGridMultiSelect()
        {
            if (_multiSelectTargetList != null) ExitSongMultiSelectUiOnly();
            if (_multiSelectFolderList != null) ExitFolderMultiSelectUiOnly();
            _multiSelectAlbumGrid = TagSortPanelGridView;
            _multiSelectTargetList = null;
            _multiSelectFolderList = null;
            _isMultiSelectMode = true;
            TagSortPanelGridView.SelectionMode = ListViewSelectionMode.Multiple;
            TagSortPanelGridView.IsItemClickEnabled = false;

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择项目";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
        }


        /// <summary>分类墙 / 面板网格项的右键菜单：播放该分类全部 / 添加到播放队列。</summary>
        private void ShowTagSortCategoryMenu(TagSortCategoryEntry entry, FrameworkElement anchor, Windows.Foundation.Point point)
        {
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
            bool isAlbumField = string.Equals(_tagSortClassField, "Album", StringComparison.Ordinal);

            var multi = new MenuFlyoutItem { Text = "多选", Icon = new FontIcon { Glyph = "\uE700" } };
            multi.Click += (_, _) => EnterTagSortClassWallMultiSelect();
            flyout.Items.Add(multi);

            var play = new MenuFlyoutItem
            {
                Text = isAlbumField ? "播放该专辑" : "播放该分类",
                Icon = new FontIcon { Glyph = "\uE768" }
            };
            play.Click += (_, _) =>
            {
                var songs = CollectTagSortCategorySongs(entry);
                if (songs.Count == 0) return;
                if (isAlbumField)
                {
                    PlayAlbum(BuildTagSortAlbumEntry(entry), replacePlaylist: true);
                }
                else
                {
                    PlayPlaylistItem(songs[0]);
                }
            };
            flyout.Items.Add(play);

            var addQueue = new MenuFlyoutItem { Text = "添加到播放队列", Icon = new FontIcon { Glyph = "\uE710" } };
            addQueue.Click += (_, _) => AddSongsToUserPlaylist(CollectTagSortCategorySongs(entry));
            flyout.Items.Add(addQueue);

            if (isAlbumField)
            {
                var albumEntry = BuildTagSortAlbumEntry(entry);
                var wallAlbum = new MenuFlyoutItem { Text = "添加到播放列表", Icon = new FontIcon { Glyph = "\uE8B7" } };
                wallAlbum.Click += (_, _) => _ = ShowNamedPlaylistPickerAsync(GetTracksForAlbum(albumEntry));
                flyout.Items.Add(wallAlbum);
                AppendAlbumContextItems(flyout, albumEntry, fromArtist: false); // 批量歌词/封面/复制信息/打开详情
            }

            flyout.ShowAt(anchor, point);
        }


        /// <summary>分类墙进入多选：把 TagSortClassGridView 设多选、勾选分组项；主按钮将选中分类曲目加入播放队列。</summary>
        private void EnterTagSortClassWallMultiSelect()
        {
            if (_multiSelectTargetList != null) ExitSongMultiSelectUiOnly();
            if (_multiSelectFolderList != null) ExitFolderMultiSelectUiOnly();
            _multiSelectAlbumGrid = TagSortClassGridView;
            _multiSelectTargetList = null;
            _multiSelectFolderList = null;
            _isMultiSelectMode = true;
            TagSortClassGridView.SelectionMode = ListViewSelectionMode.Multiple;
            TagSortClassGridView.IsItemClickEnabled = false;

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择项目";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
        }


        // ---------------- 排序方式（排序主面板歌曲顺序） ----------------

        private void TagSortPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TagSortPresetCombo.SelectedItem is ComboBoxItem cbi)
            {
                string key = cbi.Content?.ToString() ?? string.Empty;
                _tagSortCustom = PresetToFields(key);
                _tagSortPreset = key;
                ApplyTagSortToLibrary();
                WriteTagSortStatus();
            }
        }


        private void TagSortOrderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                // 由用户点击触发（Click 不会因程序化改 IsChecked 而重入）。
                bool? asc = TagSortAscButton?.IsChecked == true;
                bool? desc = TagSortDescButton?.IsChecked == true;
                if ((e.OriginalSource is FrameworkElement fe && ReferenceEquals(fe, TagSortDescButton))
                    || desc == true)
                {
                    _tagSortAscending = false;
                    if (TagSortAscButton != null) TagSortAscButton.IsChecked = false;
                    if (TagSortDescButton != null) TagSortDescButton.IsChecked = true;
                }
                else if (asc == true)
                {
                    _tagSortAscending = true;
                    if (TagSortDescButton != null) TagSortDescButton.IsChecked = false;
                    if (TagSortAscButton != null) TagSortAscButton.IsChecked = true;
                }

                ApplyTagSortToLibrary();
                WriteTagSortStatus();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TagSortOrderClick", ex);
            }
        }


        private void TagSortCustomSortButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new CustomSortOrderWindow(_tagSortCustom, _tagSortAscending);
            win.SortConfirmed += (fields, asc) =>
            {
                _tagSortCustom = fields;
                _tagSortAscending = asc;
                _tagSortPreset = "自定义";
                TagSortPresetCombo.SelectedItem = null;
                ApplyTagSortToLibrary();
                WriteTagSortStatus();
            };
            win.Activate();
        }


        /// <summary>把当前排序方式应用到轨道库（主面板歌曲顺序）。</summary>
        private void ApplyTagSortToLibrary()
        {
            if (_tagSortCustom == null || _tagSortCustom.Count == 0)
            {
                return;
            }

            IOrderedEnumerable<PlaylistItem> sorted = _tagSortAscending
                ? _playlist.OrderBy(p => TagSortFieldVal(p, _tagSortCustom[0].field), StringComparer.CurrentCultureIgnoreCase)
                : _playlist.OrderByDescending(p => TagSortFieldVal(p, _tagSortCustom[0].field), StringComparer.CurrentCultureIgnoreCase);

            for (int i = 1; i < _tagSortCustom.Count; i++)
            {
                bool a = _tagSortCustom[i].asc;
                sorted = a
                    ? sorted.ThenBy(p => TagSortFieldVal(p, _tagSortCustom[i].field), StringComparer.CurrentCultureIgnoreCase)
                    : sorted.ThenByDescending(p => TagSortFieldVal(p, _tagSortCustom[i].field), StringComparer.CurrentCultureIgnoreCase);
            }

            var list = sorted.ToList();
            // 全量重排时临时解绑，避免逐条 Clear/Add 触发 UI 反复重建
            bool rebounded = ReferenceEquals(PlaylistView.ItemsSource, _playlist);
            if (rebounded)
            {
                PlaylistView.ItemsSource = null;
            }

            _playlist.Clear();
            foreach (var p in list) _playlist.Add(p);
            if (rebounded)
            {
                PlaylistView.ItemsSource = _playlist;
            }

            RenumberCollection(_playlist);
            if (string.Equals(_currentCategory, "Songs", StringComparison.Ordinal))
            {
                PlaylistView.ItemsSource = _playlist; // 歌曲分类主列表即按此顺序
            }
        }


        private void WriteTagSortStatus()
        {
            string desc = _tagSortPreset;
            var tags = _tagSortCustom.Count == 0 ? new List<(string field, bool asc)>() : _tagSortCustom;
            if (tags.Count > 0)
            {
                desc += "（" + string.Join(" → ", tags.Select(t => TagSortFieldLabel(t.field) + (t.asc ? "↑" : "↓"))) + "）";
            }
            TagSortSortStatusText.Text = "当前排序依据：" + desc;
        }


        private void LibraryPaneRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            PointerUpdateKind kind = e.GetCurrentPoint(LibraryPaneRoot).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.XButton1Pressed)
            {
                NavigateLibraryHistoryBack();
                e.Handled = true;
            }
            else if (kind == PointerUpdateKind.XButton2Pressed)
            {
                NavigateLibraryHistoryForward();
                e.Handled = true;
            }
        }


        private LibraryNavState CaptureLibraryNavState()
            => new()
            {
                Category = _currentCategory,
                ArtistName = _openedArtist?.Name,
                AlbumName = _openedAlbum?.Name,
                AlbumFromArtist = _albumOpenedFromArtist,
                UsesAlbumArtist = _artistDetailUsesAlbumArtist
            };


        private void NavigateLibraryHistoryBack()
        {
            if (_navBackStack.Count == 0)
            {
                return;
            }

            LibraryNavState current = CaptureLibraryNavState();
            LibraryNavState target = _navBackStack[^1];
            _navBackStack.RemoveAt(_navBackStack.Count - 1);
            _navForwardStack.Add(current);
            RestoreLibraryNavState(target);
        }


        private void NavigateLibraryHistoryForward()
        {
            // 从未前进过 / 前进栈为空：保持当前界面
            if (_navForwardStack.Count == 0)
            {
                return;
            }

            LibraryNavState current = CaptureLibraryNavState();
            LibraryNavState target = _navForwardStack[^1];
            _navForwardStack.RemoveAt(_navForwardStack.Count - 1);
            _navBackStack.Add(current);
            RestoreLibraryNavState(target);
        }


        private void RestoreLibraryNavState(LibraryNavState state)
        {
            _suppressNavHistory = true;
            try
            {
                ExitMultiSelectMode();
                _currentCategory = state.Category;
                ApplyCategoryView();

                if (!string.IsNullOrWhiteSpace(state.ArtistName)
                    && (string.Equals(state.Category, "Artists", StringComparison.Ordinal)
                        || string.Equals(state.Category, "AlbumArtists", StringComparison.Ordinal)))
                {
                    _artistDetailUsesAlbumArtist = state.UsesAlbumArtist
                        || string.Equals(state.Category, "AlbumArtists", StringComparison.Ordinal);
                    ArtistEntry? artist = _artists.FirstOrDefault(a =>
                        string.Equals(a.Name, state.ArtistName, StringComparison.CurrentCultureIgnoreCase));
                    if (artist == null)
                    {
                        artist = new ArtistEntry { Name = state.ArtistName };
                        _artists.Add(artist);
                    }

                    OpenArtistDetailCore(artist);
                }

                if (!string.IsNullOrWhiteSpace(state.AlbumName))
                {
                    AlbumEntry? album = FindAlbumEntryByName(state.AlbumName);
                    if (album != null)
                    {
                        OpenAlbumDetailCore(album, state.AlbumFromArtist);
                    }
                }

                _navCurrent = CaptureLibraryNavState();
                UpdateLibraryNavHighlight();
            }
            finally
            {
                _suppressNavHistory = false;
            }
        }


        private AlbumEntry? FindAlbumEntryByName(string albumName)
        {
            AlbumEntry? fromWall = _albums.FirstOrDefault(a =>
                string.Equals(a.Name, albumName, StringComparison.CurrentCultureIgnoreCase));
            if (fromWall != null)
            {
                return fromWall;
            }

            AlbumEntry? fromArtist = _artistAlbums.FirstOrDefault(a =>
                string.Equals(a.Name, albumName, StringComparison.CurrentCultureIgnoreCase));
            if (fromArtist != null)
            {
                return fromArtist;
            }

            List<PlaylistItem> tracks = _playlist
                .Where(t => string.Equals(t.Album, albumName, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            return tracks.Count == 0 ? null : BuildAlbumEntriesFromTracks(tracks).FirstOrDefault();
        }


        // =====================================================================
        // 库搜索（歌曲 / 专辑 / 文件夹）
        // =====================================================================

        private void UpdateLibrarySearchUi()
        {
            if (LibrarySearchPanel == null)
            {
                return;
            }

            bool show = !_isMultiSelectMode
                && _openedAlbum == null
                && _openedArtist == null
                && (_currentCategory is "Songs" or "Albums" or "Artists" or "AlbumArtists" or "Folders" or "UserPlaylist" or "Favorites" or "Recent" or "Ratings");

            LibrarySearchPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                if (FolderSearchNavPanel != null)
                {
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                }

                if (_currentCategory != "Folders")
                {
                    ClearFolderSearchHighlightOnly();
                }

                return;
            }

            LibrarySearchBox.PlaceholderText = _currentCategory switch
            {
                "Songs" => "搜索标题、专辑、艺术家",
                "Albums" => "搜索专辑、艺术家",
                "Artists" => "搜索艺术家",
                "AlbumArtists" => "搜索专辑艺术家",
                "Folders" => "搜索文件名",
                "UserPlaylist" => "搜索标题、艺术家、专辑、年份",
                _ => "搜索"
            };

            ApplyLibrarySearchNow();
        }


        private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _librarySearchText = LibrarySearchBox.Text ?? string.Empty;

            if (_librarySearchDebounceTimer == null)
            {
                _librarySearchDebounceTimer = DispatcherQueue.CreateTimer();
                _librarySearchDebounceTimer.IsRepeating = false;
                _librarySearchDebounceTimer.Interval = TimeSpan.FromMilliseconds(180);
                _librarySearchDebounceTimer.Tick += (_, _) => ApplyLibrarySearchNow();
            }

            _librarySearchDebounceTimer.Stop();
            _librarySearchDebounceTimer.Start();
        }


        private void ApplyLibrarySearchNow()
        {
            switch (_currentCategory)
            {
                case "Songs":
                    ApplySongsSearchFilter();
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    ClearFolderSearchHighlightOnly();
                    break;
                case "Albums":
                    ApplyAlbumsSearchFilter();
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    ClearFolderSearchHighlightOnly();
                    break;
                case "Artists":
                case "AlbumArtists":
                    ApplyArtistsSearchFilter();
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    ClearFolderSearchHighlightOnly();
                    break;
                case "Folders":
                    ApplyFolderSearch();
                    break;
                case "UserPlaylist":
                    ApplyUserPlaylistSearchFilter();
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    ClearFolderSearchHighlightOnly();
                    break;
                default:
                    FolderSearchNavPanel.Visibility = Visibility.Collapsed;
                    break;
            }
        }


        private void NavigateFolderSearchMatch(int delta)
        {
            if (_folderSearchMatches.Count == 0)
            {
                return;
            }

            int next = _folderSearchIndex + delta;
            if (next < 0)
            {
                next = _folderSearchMatches.Count - 1;
            }
            else if (next >= _folderSearchMatches.Count)
            {
                next = 0;
            }

            NavigateFolderSearchMatchToIndex(next);
        }


        private void NavigateFolderSearchMatchToIndex(int index)
        {
            if (index < 0 || index >= _folderSearchMatches.Count)
            {
                return;
            }

            _folderSearchIndex = index;
            string targetPath = _folderSearchMatches[index];
            _folderSearchHighlightPath = targetPath;
            UpdateFolderSearchNavUi();

            // PDF 式：先收起全部，再展开到目标歌曲所在路径
            RefreshFolderBrowserRoots();
            ExpandFolderPathToFile(targetPath);

            FolderBrowserItem? item = _folderBrowserItems.FirstOrDefault(i =>
                !i.IsFolder
                && string.Equals(i.FullPath, targetPath, StringComparison.OrdinalIgnoreCase));

            if (item != null)
            {
                try
                {
                    FolderBrowserView.SelectedItem = item;
                    FolderBrowserView.ScrollIntoView(item);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }

            RefreshFolderBrowserSelectionChrome();
        }


        private void ExpandFolderPathToFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(_browseFolderPath) || string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            string root = Path.GetFullPath(_browseFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullFile = Path.GetFullPath(filePath);
            string? parent = Path.GetDirectoryName(fullFile);
            if (string.IsNullOrEmpty(parent))
            {
                return;
            }

            string parentFull = Path.GetFullPath(parent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!parentFull.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string relative = parentFull.Length == root.Length
                ? string.Empty
                : parentFull[(root.Length + 1)..];

            if (string.IsNullOrEmpty(relative))
            {
                return;
            }

            string[] segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            string current = root;
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                FolderBrowserItem? folder = _folderBrowserItems.FirstOrDefault(i =>
                    i.IsFolder
                    && string.Equals(i.FullPath, current, StringComparison.OrdinalIgnoreCase));

                if (folder == null)
                {
                    break;
                }

                if (!folder.IsExpanded)
                {
                    ToggleFolderExpand(folder);
                }
            }
        }


        private void UpdateFolderSearchNavUi()
        {
            if (FolderSearchNavPanel == null || FolderSearchCountText == null)
            {
                return;
            }

            bool show = _currentCategory == "Folders"
                && !_isMultiSelectMode
                && _folderSearchMatches.Count > 0
                && !string.IsNullOrWhiteSpace(_librarySearchText);

            FolderSearchNavPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
            {
                FolderSearchCountText.Text = "0/0";
                return;
            }

            FolderSearchCountText.Text = $"{_folderSearchIndex + 1}/{_folderSearchMatches.Count}";
        }


        private void ClearFolderSearchHighlightOnly()
        {
            if (_folderSearchHighlightPath == null && _folderSearchMatches.Count == 0)
            {
                return;
            }

            _folderSearchHighlightPath = null;
            _folderSearchMatches.Clear();
            _folderSearchIndex = -1;
            if (FolderBrowserView != null && _currentCategory == "Folders")
            {
                RefreshFolderBrowserSelectionChrome();
            }
        }

        /// <summary>刷新媒体库：重新枚举根列表，并重载当前选中项的详情。</summary>
        private void RefreshMediaFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentCategory != "Folders")
            {
                return;
            }

            RefreshFolderBrowserRoots();
            if (FolderBrowserView.SelectedItem is FolderBrowserItem f)
            {
                LoadMediaFolderSongs(f);
            }
        }


        /// <summary>「添加文件夹」：选择文件夹加入媒体库(LibraryWatchFolders)并刷新根列表。</summary>
        private async void AddMediaFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker();
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add("*");

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
                {
                    return;
                }

                AppSettingsState settings = AppSettingsStore.Load();
                if (settings.LibraryWatchFolders == null)
                {
                    settings.LibraryWatchFolders = new List<string>();
                }

                string fp = folder.Path;
                if (!settings.LibraryWatchFolders.Contains(fp, StringComparer.OrdinalIgnoreCase))
                {
                    settings.LibraryWatchFolders.Add(fp);
                }

                AppSettingsStore.Save(settings);

                // 把该文件夹的音频加入主库
                string[] paths = await System.Threading.Tasks.Task.Run(() => EnumerateAudioFiles(fp).ToArray());
                LoadAndAddFiles(paths, persist: false);

                if (_currentCategory == "Folders")
                {
                    RefreshFolderBrowserRoots();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        /// <summary>显示媒体库根（设置里的文件夹列表）或其直接子项；未配置则回退旧单根。</summary>
        private void RefreshFolderBrowserRoots()
        {
            _folderBrowserItems.Clear();

            List<string> roots = AppSettingsStore.Load().LibraryWatchFolders
                ?.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .ToList() ?? new List<string>();

            if (roots.Count == 0)
            {
                // 未配置媒体库：回退旧的单文件夹树
                if (string.IsNullOrWhiteSpace(_browseFolderPath) || !Directory.Exists(_browseFolderPath))
                {
                    FolderBrowserView.Visibility = Visibility.Collapsed;
                    FolderBrowserEmptyHint.Visibility = Visibility.Visible;
                    return;
                }

                FolderBrowserEmptyHint.Visibility = Visibility.Collapsed;
                FolderBrowserView.Visibility = Visibility.Visible;
                foreach (FolderBrowserItem child in EnumerateFolderChildren(_browseFolderPath, depth: 0))
                {
                    _folderBrowserItems.Add(child);
                }

                return;
            }

            // 多根媒体库：每个根显示完整路径，点击展开其下树
            FolderBrowserEmptyHint.Visibility = Visibility.Collapsed;
            FolderBrowserView.Visibility = Visibility.Visible;
            foreach (string root in roots)
            {
                _folderBrowserItems.Add(new FolderBrowserItem
                {
                    DisplayName = root,
                    FullPath = root,
                    IsFolder = true,
                    Depth = 0
                });
            }
        }


        private void FolderBrowserItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // 多选时：整行留给 ListView 选中；展开只走左侧箭头
            if (_isMultiSelectMode && _multiSelectFolderList != null)
            {
                return;
            }

            if (sender is not FrameworkElement { DataContext: FolderBrowserItem item })
            {
                return;
            }

            FolderBrowserView.SelectedItem = item;

            if (item.IsFolder)
            {
                ToggleFolderExpand(item);
                e.Handled = true;
            }
        }


        /// <summary>左侧朝下箭头：展开/折叠；多选时不改变选中状态。</summary>
        private void FolderBrowserChevron_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe)
            {
                return;
            }

            FolderBrowserItem? item = fe.DataContext as FolderBrowserItem
                ?? FindFolderBrowserItem(fe);
            if (item == null || !item.IsFolder)
            {
                return;
            }

            ToggleFolderExpand(item);
            // 点箭头：同时加载该文件夹歌曲到右侧详情区
            LoadMediaFolderSongs(item);
            e.Handled = true;
        }


        private void ToggleFolderExpand(FolderBrowserItem item)
        {
            int index = _folderBrowserItems.IndexOf(item);
            if (index < 0)
            {
                return;
            }

            if (item.IsExpanded)
            {
                CollapseFolderAt(index);
                item.IsExpanded = false;
                return;
            }

            List<FolderBrowserItem> children = EnumerateFolderChildren(item.FullPath, item.Depth + 1);
            for (int i = 0; i < children.Count; i++)
            {
                _folderBrowserItems.Insert(index + 1 + i, children[i]);
            }

            item.ChildrenLoaded = true;
            item.IsExpanded = true;
        }


        private void CollapseFolderAt(int index)
        {
            int depth = _folderBrowserItems[index].Depth;
            int removeAt = index + 1;
            while (removeAt < _folderBrowserItems.Count && _folderBrowserItems[removeAt].Depth > depth)
            {
                _folderBrowserItems.RemoveAt(removeAt);
            }
        }


        /// <summary>枚举一层：子文件夹 + 支持的音频文件（不递归）</summary>
        private static List<FolderBrowserItem> EnumerateFolderChildren(string folderPath, int depth)
        {
            var result = new List<FolderBrowserItem>();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return result;
            }

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    try
                    {
                        System.IO.FileAttributes attrs = System.IO.File.GetAttributes(dir);
                        if ((attrs & System.IO.FileAttributes.Hidden) != 0 ||
                            (attrs & System.IO.FileAttributes.System) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    result.Add(new FolderBrowserItem
                    {
                        DisplayName = name,
                        FullPath = dir,
                        IsFolder = true,
                        Depth = depth
                    });
                }
            }
            catch
            {
                // 无权限等：跳过子目录
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string ext = Path.GetExtension(file);
                    if (!AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new FolderBrowserItem
                    {
                        DisplayName = Path.GetFileName(file),
                        FullPath = file,
                        IsFolder = false,
                        Depth = depth
                    });
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            return result;
        }


        private void FolderBrowserView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (_isMultiSelectMode)
            {
                return;
            }

            var fe = e.OriginalSource as FrameworkElement;
            var item = fe?.DataContext as FolderBrowserItem ?? FolderBrowserView.SelectedItem as FolderBrowserItem;
            if (item == null)
            {
                return;
            }

            if (item.IsFolder)
            {
                // 双击文件夹：加载其内歌曲到右侧详情区
                LoadMediaFolderSongs(item);
                return;
            }

            PlaylistItem? track = EnsureTrackInLibrary(item.FullPath);
            if (track != null)
            {
                PlayPlaylistItem(track);
            }
        }


        private void FolderBrowserView_SelectionChromeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionUiUpdates)
            {
                return;
            }

            RefreshFolderBrowserSelectionChrome();
            UpdateSelectAllMultiSelectButtonState();
            UpdateMediaDetails(FolderBrowserView.SelectedItem as FolderBrowserItem);
        }


        /// <summary>单击选中：只更新台头，不立即枚举歌曲（大文件夹避免卡顿）。歌曲在双击/箭头时加载。</summary>
        private void UpdateMediaDetails(FolderBrowserItem? item)
        {
            if (MediaDetailsHeader == null || MediaDetailsList == null)
            {
                return;
            }

            if (item == null)
            {
                MediaDetailsHeader.Text = "选择文件夹查看歌曲";
                MediaDetailsList.ItemsSource = null;
                MediaDetailsEmptyHint.Visibility = MediaDetailsList.Visibility = Visibility.Collapsed;
                return;
            }

            MediaDetailsHeader.Text = item.FullPath;
            MediaDetailsList.ItemsSource = null;
            MediaDetailsEmptyHint.Visibility = Visibility.Visible;
            MediaDetailsList.Visibility = Visibility.Visible;
            MediaDetailsEmptyHint.Text = item.IsFolder ? "双击文件夹查看歌曲" : "双击查看";
        }


        private void FolderBrowserView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is FolderBrowserItem item && args.ItemContainer is ListViewItem container)
            {
                ApplyFolderBrowserItemSelectionChrome(container, item);
            }
        }


        private void FolderBrowserView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            FolderBrowserItem? item = FindFolderBrowserItem(e.OriginalSource as DependencyObject);
            if (item == null)
            {
                return;
            }

            if (!_isMultiSelectMode)
            {
                FolderBrowserView.SelectedItem = item;
            }

            if (item.IsFolder)
            {
                ShowFolderItemContextMenu(item, e);
            }
            else
            {
                ShowFolderAudioContextMenu(item, e);
            }

            e.Handled = true;
        }


        private FolderBrowserItem? FindFolderBrowserItem(DependencyObject? source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is FrameworkElement { DataContext: FolderBrowserItem fromContext })
                {
                    return fromContext;
                }

                if (current is ListViewItem container)
                {
                    return FolderBrowserView.ItemFromContainer(container) as FolderBrowserItem;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }


        private void ShowFolderItemContextMenu(FolderBrowserItem folder, RightTappedRoutedEventArgs e)
        {
            FolderBrowserItem folderRef = folder;
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

            var playItem = new MenuFlyoutItem { Text = "播放该文件夹内的音频" };
            playItem.Icon = new FontIcon { Glyph = "\uE768" };
            playItem.Click += (_, _) =>
            {
                ExitMultiSelectMode();
                PlayFolderAudio(folderRef.FullPath, replacePlaylist: true);
            };

            var multiItem = new MenuFlyoutItem { Text = "多选" };
            multiItem.Icon = new FontIcon { Glyph = "\uE700" };
            multiItem.Click += (_, _) => EnterFolderMultiSelectMode(folderRef);

            var addItem = new MenuFlyoutItem { Text = "添加至播放队列" };
            addItem.Icon = new FontIcon { Glyph = "\uE710" };
            addItem.Click += (_, _) => PlayFolderAudio(folderRef.FullPath, replacePlaylist: false);

            flyout.Items.Add(playItem);
            flyout.Items.Add(multiItem);
            flyout.Items.Add(addItem);

            // 从媒体库删除（仅对配置的媒体库根文件夹可用）
            AppSettingsState settings = AppSettingsStore.Load();
            bool isMediaRoot = settings.LibraryWatchFolders?.Contains(folderRef.FullPath, StringComparer.OrdinalIgnoreCase) == true;
            if (isMediaRoot)
            {
                var removeItem = new MenuFlyoutItem { Text = "从媒体库中删除" };
                removeItem.Icon = new FontIcon { Glyph = "\uE74D" };
                removeItem.Click += async (_, _) =>
                {
                    var dialog = new ContentDialog
                    {
                        Title = "从媒体库中删除",
                        Content = $"将该文件夹移出媒体库？\n\n{folderRef.FullPath}\n\n是否同时把该文件夹内的音频移到回收站？",
                        PrimaryButtonText = "删除（移到回收站）",
                        SecondaryButtonText = "仅移出媒体库",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = Content.XamlRoot
                    };
                    ApplyDialogAccent(dialog);

                    ContentDialogResult r;
                    try
                    {
                        r = await dialog.ShowAsync();
                    }
                    catch
                    {
                        r = ContentDialogResult.None;
                    }

                    if (r == ContentDialogResult.None)
                    {
                        return;
                    }

                    if (r == ContentDialogResult.Primary)
                    {
                        // 删除源文件（移到回收站）
                        foreach (string p in EnumerateAudioFiles(folderRef.FullPath))
                        {
                            if (System.IO.File.Exists(p))
                            {
                                try
                                {
                                    MoveToRecycleBin(p);
                                }
                                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                            }
                        }
                    }

                    settings.LibraryWatchFolders?.RemoveAll(q =>
                        string.Equals(q, folderRef.FullPath, StringComparison.OrdinalIgnoreCase));
                    AppSettingsStore.Save(settings);
                    RefreshFolderBrowserRoots();
                };
                flyout.Items.Add(removeItem);
            }

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(FolderBrowserView, e.GetPosition(FolderBrowserView));
            }
        }


        private void ShowFolderAudioContextMenu(FolderBrowserItem fileItem, RightTappedRoutedEventArgs e)
        {
            PlaylistItem? track = EnsureTrackInLibrary(fileItem.FullPath);
            if (track == null)
            {
                return;
            }

            _contextMenuSong = track;
            var flyout = BuildPlaylistItemContextMenu(track, false, () => EnterFolderMultiSelectMode(fileItem));

            if (e.OriginalSource is FrameworkElement fe)
            {
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
            else
            {
                flyout.ShowAt(FolderBrowserView, e.GetPosition(FolderBrowserView));
            }
        }


        /// <summary>深度优先：子文件夹顺序与界面一致，文件夹内音频按文件名排序。</summary>
        private static List<string> EnumerateAudioFilesRecursiveOrdered(string folderPath)
        {
            var result = new List<string>();
            CollectAudioFilesRecursiveOrdered(folderPath, result);
            return result;
        }


        private static void CollectAudioFilesRecursiveOrdered(string folderPath, List<string> sink)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            try
            {
                foreach (string dir in Directory.EnumerateDirectories(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    try
                    {
                        System.IO.FileAttributes attrs = System.IO.File.GetAttributes(dir);
                        if ((attrs & System.IO.FileAttributes.Hidden) != 0 ||
                            (attrs & System.IO.FileAttributes.System) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    CollectAudioFilesRecursiveOrdered(dir, sink);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                foreach (string file in Directory.EnumerateFiles(folderPath)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string ext = Path.GetExtension(file);
                    if (AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        sink.Add(file);
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void EnterFolderMultiSelectMode(FolderBrowserItem? preselect)
        {
            if (_multiSelectTargetList != null)
            {
                ExitSongMultiSelectUiOnly();
            }

            if (_multiSelectAlbumGrid != null)
            {
                ExitAlbumMultiSelectUiOnly();
            }

            _folderItemDefaultStyle ??= FolderBrowserView.ItemContainerStyle;
            _multiSelectFolderList = FolderBrowserView;
            _multiSelectTargetList = null;
            _multiSelectAlbumGrid = null;
            _isMultiSelectMode = true;

            SetListSelectionMode(FolderBrowserView, ListViewSelectionMode.Multiple);

            LibraryPaneTitle.Visibility = Visibility.Collapsed;
            SongSortPanel.Visibility = Visibility.Collapsed;
            AlbumSortPanel.Visibility = Visibility.Collapsed;
            MultiSelectTitlePanel.Visibility = Visibility.Visible;
            MultiSelectTitleText.Text = "选择项目";
            MultiSelectActionBar.Visibility = Visibility.Visible;
            ConfigureMultiSelectPrimaryAction();
            UpdateSelectAllMultiSelectButtonState();
            UpdateUserPlaylistActionBarVisibility();
            ApplyAccentSelectionResources(FolderBrowserView);
            ApplyMultiSelectFolderItemStyle();
            UpdateLibrarySearchUi();

            if (preselect != null)
            {
                try
                {
                    FolderBrowserView.SelectedItems.Add(preselect);
                }
                catch
                {
                    FolderBrowserView.SelectedItem = preselect;
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshFolderBrowserSelectionChrome();
                UpdateSelectAllMultiSelectButtonState();
            });
        }


        private void ApplyMultiSelectFolderItemStyle()
        {
            ApplyAccentSelectionResources(FolderBrowserView);
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
            style.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ListViewItem.MinHeightProperty, 36.0));
            style.Setters.Add(new Setter(ListViewItem.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
            style.Setters.Add(new Setter(ListViewItem.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0, 2, 0, 2)));
            style.Setters.Add(new Setter(ListViewItem.BorderThicknessProperty, new Thickness(0)));
            FolderBrowserView.ItemContainerStyle = style;
            RefreshFolderBrowserSelectionChrome();
        }


        private void ApplyFolderBrowserItemSelectionChrome(
            ListViewItem container,
            FolderBrowserItem item,
            HashSet<object>? selectedSet = null)
        {
            Brush accent = ResolveAccentBrush();
            Brush selectedFg = ResolveContrastingForeground(accent);
            bool multiOnThisList = _isMultiSelectMode && _multiSelectFolderList == FolderBrowserView;
            Brush unselectedBg = multiOnThisList
                ? CreateMultiSelectFrostBrush()
                : new SolidColorBrush(Colors.Transparent);

            container.Background = new SolidColorBrush(Colors.Transparent);
            container.CornerRadius = new CornerRadius(8);
            container.BorderThickness = new Thickness(0);
            DisableContainerSelectionCheckMark(container);

            bool selected = multiOnThisList
                ? IsItemSelected(FolderBrowserView, item, selectedSet)
                : ReferenceEquals(FolderBrowserView.SelectedItem, item);

            bool searchHit = !multiOnThisList
                && !string.IsNullOrEmpty(_folderSearchHighlightPath)
                && string.Equals(item.FullPath, _folderSearchHighlightPath, StringComparison.OrdinalIgnoreCase);

            Border? chrome = FindTaggedBorder(container, "FolderRowChrome");
            if (chrome != null)
            {
                chrome.MinHeight = 36;
                chrome.CornerRadius = new CornerRadius(8);
                chrome.VerticalAlignment = VerticalAlignment.Stretch;
                if (FolderBrowserView != null && FolderBrowserView.ActualWidth > 0)
                {
                    chrome.Width = FolderBrowserView.ActualWidth; // 行内容铺满整行，选中矩形右侧铺满
                }
                if (selected || searchHit)
                {
                    chrome.Background = accent;
                    ApplyForegroundToDescendants(chrome, selectedFg);
                }
                else
                {
                    chrome.Background = unselectedBg;
                    ClearForegroundOnDescendants(chrome);
                }
            }
            else if (selected || searchHit)
            {
                container.Background = accent;
                container.Foreground = selectedFg;
            }
            else
            {
                container.Background = unselectedBg;
                container.ClearValue(Control.ForegroundProperty);
            }
        }


        /// <summary>汇总唯一艺术家 / 专辑艺术家，按名字升序；加载已保存的自定义头像</summary>
        private async Task RefreshArtistViewAsync()
        {
            _artists.Clear();
            bool albumArtistMode = string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);

            if (_playlist.Count == 0)
            {
                return;
            }

            var groups = _playlist
                .GroupBy(
                    p => albumArtistMode ? p.AlbumArtist : p.Artist,
                    StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            foreach (var group in groups)
            {
                var entry = new ArtistEntry
                {
                    Name = albumArtistMode ? group.First().AlbumArtist : group.First().Artist,
                    TrackCount = group.Count()
                };
                _artists.Add(entry);
            }

            ApplyArtistsSearchFilter();

            foreach (ArtistEntry entry in _artists.ToList())
            {
                if (_currentCategory != "Artists" && _currentCategory != "AlbumArtists")
                {
                    return;
                }

                entry.AvatarImage = await ResolveArtistAvatarAsync(entry.Name, albumArtistMode);
            }
        }


        private static string ArtistAvatarStoreKey(string artistName, bool albumArtistMode)
            => albumArtistMode ? "aa|" + artistName.Trim() : artistName.Trim();

        /// <summary>
        /// 自定义头像优先；否则取该艺术家年份最晚专辑中音轨 1 的封面。
        /// </summary>
        private async Task<BitmapImage?> ResolveArtistAvatarAsync(string artistName, bool? albumArtistMode = null)
        {
            bool useAlbumArtist = albumArtistMode
                ?? (_artistDetailUsesAlbumArtist
                    || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal));
            string storeKey = ArtistAvatarStoreKey(artistName, useAlbumArtist);
            BitmapImage? custom = await ArtistAvatarStore.TryLoadAsync(storeKey);
            if (custom != null)
            {
                return custom;
            }

            return await ResolveArtistDefaultAvatarAsync(artistName, useAlbumArtist);
        }


        private async Task<BitmapImage?> ResolveArtistDefaultAvatarAsync(string artistName, bool useAlbumArtist)
        {
            // 默认优先使用网络头像（网易云），失败再回退本地专辑封面
            BitmapImage? web = await TryLoadWebArtistAvatarAsync(artistName);
            if (web != null)
            {
                return web;
            }

            List<PlaylistItem> tracks = _playlist
                .Where(t => TrackMatchesArtistName(t, artistName, useAlbumArtist))
                .ToList();
            if (tracks.Count == 0)
            {
                return null;
            }

            AlbumEntry? latestAlbum = BuildAlbumEntriesFromTracks(tracks)
                .OrderByDescending(a => a.Year)
                .ThenByDescending(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();
            if (latestAlbum == null || string.IsNullOrWhiteSpace(latestAlbum.CoverSourcePath))
            {
                return null;
            }

            byte[]? bytes = await Task.Run(() => ExtractCoverBytes(latestAlbum.CoverSourcePath));
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            return await CreateBitmapFromBytesAsync(bytes);
        }



        private static readonly Dictionary<string, BitmapImage?> WebArtistAvatarCache = new();
        private static readonly SemaphoreSlim WebArtistAvatarGate = new(1, 1);

        /// <summary>按歌手名从网络加载头像（缓存，失败返回 null）。</summary>
        private static async Task<BitmapImage?> TryLoadWebArtistAvatarAsync(string artistName)
        {
            string key = artistName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (WebArtistAvatarCache.TryGetValue(key, out BitmapImage? cached))
            {
                return cached;
            }

            await WebArtistAvatarGate.WaitAsync();
            try
            {
                if (WebArtistAvatarCache.TryGetValue(key, out cached))
                {
                    return cached;
                }

                // 磁盘缓存命中（重启后免联网搜索）：加载后回填内存缓存
                BitmapImage? fromDisk = await ArtistAvatarStore.TryLoadWebAsync(key);
                if (fromDisk != null)
                {
                    WebArtistAvatarCache[key] = fromDisk;
                    return fromDisk;
                }

                BitmapImage? result = null;
                try
                {
                    string? url = await OnlineMusicApi.SearchArtistAvatarUrlAsync(key);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                        byte[] bytes = await http.GetByteArrayAsync(url);
                        if (bytes.Length > 0)
                        {
                            result = await CreateBitmapFromBytesAsync(bytes);
                            // 持久化到磁盘缓存，下次打开（含重启后）不再联网搜索
                            await ArtistAvatarStore.SaveWebAsync(key, bytes);
                        }
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                if (WebArtistAvatarCache.Count > 500)
                {
                    WebArtistAvatarCache.Clear();
                }

                WebArtistAvatarCache[key] = result;
                return result;
            }
            finally
            {
                WebArtistAvatarGate.Release();
            }
        }

        /// <summary>圆形头像上右键：选择本地头像 / 恢复默认</summary>
        private void ArtistAvatar_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ArtistEntry? artist = FindArtistEntry(sender as DependencyObject);
            if (artist == null)
            {
                return;
            }

            ShowArtistAvatarFlyout(artist, sender as FrameworkElement, e);
        }


        private void ArtistDetailAvatar_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (_openedArtist == null)
            {
                return;
            }

            ShowArtistAvatarFlyout(_openedArtist, sender as FrameworkElement, e);
        }


        private void ShowArtistAvatarFlyout(ArtistEntry artist, FrameworkElement? element, RightTappedRoutedEventArgs e)
        {
            _avatarContextArtist = artist;

            var flyout = new MenuFlyout();

            var playWorks = new MenuFlyoutItem { Text = "播放该艺术家的作品" };
            playWorks.Icon = new FontIcon { Glyph = "\uE768" };
            playWorks.Click += (_, _) =>
            {
                if (_avatarContextArtist != null)
                {
                    PlayArtistWorks(_avatarContextArtist.Name, replacePlaylist: true);
                }
            };
            flyout.Items.Add(playWorks);

            var addWorks = new MenuFlyoutItem { Text = "添加该艺术家的作品至播放列表" };
            addWorks.Icon = new FontIcon { Glyph = "\uE710" };
            addWorks.Click += (_, _) =>
            {
                if (_avatarContextArtist != null)
                {
                    _ = ShowNamedPlaylistPickerAsync(GetTracksForArtist(_avatarContextArtist.Name, useCurrentSongSort: true));
                }
            };
            flyout.Items.Add(addWorks);

            flyout.Items.Add(new MenuFlyoutSeparator());

            var webAvatarItem = new MenuFlyoutItem { Text = "从网络获取头像…" };
            webAvatarItem.Icon = new FontIcon { Glyph = "\uE774" };
            webAvatarItem.Click += async (_, _) => await DownloadArtistAvatarFromWebAsync(_avatarContextArtist);
            flyout.Items.Add(webAvatarItem);

            var selectItem = new MenuFlyoutItem { Text = "从本地选择艺术家头像" };
            selectItem.Click += SelectArtistAvatarMenu_Click;
            flyout.Items.Add(selectItem);

            var restoreItem = new MenuFlyoutItem { Text = "恢复默认头像" };
            restoreItem.Click += RestoreArtistAvatarMenu_Click;
            flyout.Items.Add(restoreItem);

            if (element != null)
            {
                flyout.ShowAt(element, e.GetPosition(element));
            }

            e.Handled = true;
        }


        private static ArtistEntry? FindArtistEntry(DependencyObject? start)
        {
            DependencyObject? current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe)
                {
                    if (fe.DataContext is ArtistEntry fromContext)
                    {
                        return fromContext;
                    }

                    if (fe is GridViewItem { Content: ArtistEntry fromContent })
                    {
                        return fromContent;
                    }
                }

                current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }


        private async Task DownloadArtistAvatarFromWebAsync(ArtistEntry? artist)
        {
            if (artist == null)
            {
                return;
            }

            try
            {
                NowPlayingText.Text = "正在从网络获取头像…";
                string? imageUrl = await OnlineMusicApi.SearchArtistAvatarUrlAsync(artist.Name);
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    NowPlayingText.Text = "未找到该艺术家的头像";
                    return;
                }

                string tmp = Path.Combine(Path.GetTempPath(), "celeste-avatar-" + Guid.NewGuid().ToString("N") + ".jpg");
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                {
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CelesteMusicPlayer/1.0");
                    byte[] bytes = await http.GetByteArrayAsync(imageUrl);
                    await System.IO.File.WriteAllBytesAsync(tmp, bytes);
                }

                bool albumArtistMode = _artistDetailUsesAlbumArtist
                    || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);
                var editor = new ArtistAvatarEditorWindow(ArtistAvatarStoreKey(artist.Name, albumArtistMode), tmp);
                _artistAvatarEditorWindow = editor;
                editor.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_artistAvatarEditorWindow, editor))
                    {
                        _artistAvatarEditorWindow = null;
                    }

                    NowPlayingText.Text = string.Empty;
                };
                editor.AvatarConfirmed += image =>
                {
                    artist.AvatarImage = image;
                    ApplyArtistAvatarToDetailIfOpen(artist, image);
                };
                editor.Activate();
            }
            catch
            {
                NowPlayingText.Text = "获取头像失败";
            }
        }
    }
}
