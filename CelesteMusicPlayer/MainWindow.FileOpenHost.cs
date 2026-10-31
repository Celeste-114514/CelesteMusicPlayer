using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十八刀（2026-10-31）：文件/文件夹/列表导入入口宿主。
/// 从壳 <c>MainWindow.Features.cs</c> / <c>Misc.cs</c> / <c>Library.cs</c> 搬出：
/// 打开文件、打开文件夹、导入 M3U、播放 CUE（含其本体 PlayCueFileAsync）。
/// 依赖（文件加载、CUE 解析、曲库刷新、选择文件器）都在活文件里。
/// </summary>
public sealed partial class MainWindow
{

        // W13 第二十八刀：导入 M3U / 播放 CUE

        /// <summary>
        /// 导入 M3U/M3U8 播放列表（入口：播放列表墙页眉；2026-10-15 从播放页更多菜单挪来）。
        /// 语义（用户定案）：弹选择器存进某个命名单 —— 不往媒体库塞歌；
        /// 不在音乐库的文件照常进歌单（命名单只存路径，与标签面板「排序编号」同口径），
        /// 摘要里单独提示「有几首不在音乐库」。
        /// </summary>
        private async void ImportM3u_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                nint hwnd = NativeHandle;
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".m3u");
                picker.FileTypeFilter.Add(".m3u8");
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                // existingOnly:false：丢不丢自己算，摘要要报「几首不在音乐库」
                List<string> all = M3uPlaylistIO.Parse(file.Path, existingOnly: false)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (all.Count == 0)
                {
                    await ShowErrorAsync("导入 M3U", "未找到任何条目。");
                    return;
                }

                int notInLibrary = all.Count(p => FindLibraryIndex(p) < 0);

                await AddPathsToNamedPlaylistAsync(all);
                NowPlayingText.Text = notInLibrary > 0
                    ? $"已导入 M3U：{all.Count} 首进入歌单（其中 {notInLibrary} 首不在音乐库，仅存于歌单）"
                    : $"已导入 M3U：{all.Count} 首进入歌单";
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("导入 M3U 失败", ex.Message);
            }
        }

        private async void OpenCue_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                nint hwnd = NativeHandle;
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add(".cue");
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                await PlayCueFileAsync(file.Path);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("打开 CUE 失败", ex.Message);
            }
        }

        /// <summary>
        /// 解析 .cue 分轨表并播放（手动「打开 CUE…」与双击 .cue 文件关联共用）。
        /// 分轨条目带 StartTimeSeconds，由引擎按区间转码播出（见 PlayExtendedWithEngineAsync）。
        /// </summary>
        private async Task PlayCueFileAsync(string cuePath)
        {
            List<CueTrack> tracks = CueSheetParser.LoadCue(cuePath);
            if (tracks.Count == 0)
            {
                await ShowErrorAsync("打开 CUE", "未能解析曲目。");
                return;
            }

            var cueItems = new List<PlaylistItem>();
            foreach (CueTrack cueTrack in tracks)
            {
                if (string.IsNullOrWhiteSpace(cueTrack.FilePath) || !System.IO.File.Exists(cueTrack.FilePath))
                {
                    continue;
                }

                PlaylistItem cueItem = CreatePlaylistItemFromPath(cueTrack.FilePath);
                if (!string.IsNullOrWhiteSpace(cueTrack.Title))
                {
                    cueItem.Title = cueTrack.Title;
                }

                if (!string.IsNullOrWhiteSpace(cueTrack.Artist))
                {
                    cueItem.Artist = cueTrack.Artist;
                    cueItem.AlbumArtist = cueTrack.Artist;
                }

                cueItem.Album = string.IsNullOrWhiteSpace(cueTrack.Album) ? cueItem.Album : cueTrack.Album;
                cueItem.AlbumArtist = string.IsNullOrWhiteSpace(cueTrack.AlbumArtist) ? cueItem.AlbumArtist : cueTrack.AlbumArtist;
                cueItem.Track = (uint)cueTrack.Number;
                cueItem.IsCueSegment = true;
                cueItem.SourceTrackId = "cue-" + NetworkIdentity.Hash(Path.GetFullPath(cueItem.FilePath).ToUpperInvariant() + "\n" + cueItem.Track);
                double start = cueTrack.StartTime.TotalSeconds;
                var nextCue = tracks.FirstOrDefault(t => t.FilePath == cueTrack.FilePath && t.StartTime > cueTrack.StartTime);
                double end = nextCue?.StartTime.TotalSeconds ?? cueItem.Duration.TotalSeconds;
                cueItem.Duration = TimeSpan.FromSeconds(Math.Max(0, end - start));
                cueItem.StartTimeSeconds = start;
                cueItems.Add(cueItem);
            }

            if (cueItems.Count == 0)
            {
                await ShowErrorAsync("打开 CUE", "CUE 引用的音频文件不存在。");
                return;
            }

            AddSongsToUserPlaylist(cueItems);
            NowPlayingText.Text = $"已从 CUE 导入 {cueItems.Count} 轨";
            PlayPlaylistItem(cueItems[0]);
        }

        /// <summary>
        /// 「打开文件」菜单项（Playback.cs:943 动态挂到 fileItem.Click）。
        /// 2026-10-31 曾随纯工具函数一起搬到 MainWindow.Utilities.cs，但它的落点是
        /// LoadAndAddFiles，当时搬走会给工具文件引入壳依赖，已退回。
        /// 2026-10-31 第十四刀：LoadAndAddFiles 已搬 MainWindow.Playback.cs（依赖现全在活文件）。
        /// </summary>
        private async void OpenFileButton_Click(dynamic sender, dynamic e)
        {
            try
            {
                FileOpenPicker picker = new();
                nint hwnd = NativeHandle;
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".m4a");
                picker.FileTypeFilter.Add(".flac");
                picker.FileTypeFilter.Add(".wma");
                picker.FileTypeFilter.Add(".ogg");
                picker.FileTypeFilter.Add(".aac");
                picker.FileTypeFilter.Add(".ape");
                picker.FileTypeFilter.Add(".wv");
                picker.FileTypeFilter.Add(".tta");
                picker.FileTypeFilter.Add(".mpc");
                picker.FileTypeFilter.Add(".tak");
                picker.FileTypeFilter.Add(".opus");
                picker.FileTypeFilter.Add(".dsf");
                picker.FileTypeFilter.Add(".dff");

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    return;
                }

                string[] paths = files
                    .Select(f => f.Path)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

                LoadAndAddFiles(paths, persistAsFiles: true, replace: true);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("打开文件失败", ex.Message);
            }
        }

        /// <summary>
        /// 选择文件夹：递归扫描其中所有支持的音频，再交给 LoadAndAddFiles。
        /// </summary>
        private async void OpenFolderButton_Click(dynamic sender, dynamic e)
        {
            try
            {
                FolderPicker picker = new();
                nint hwnd = NativeHandle;
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
                    LibraryFolderScan.AudioFiles(folderPath).ToArray());

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
    }
