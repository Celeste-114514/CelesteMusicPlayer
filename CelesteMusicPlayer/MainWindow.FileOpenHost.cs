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
            PlayPlaylistItem(cueItems[0]);
        }


    }
