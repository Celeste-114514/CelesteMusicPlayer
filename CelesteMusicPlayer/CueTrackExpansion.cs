using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 媒体库扫描时按同名 .cue 展开分轨（用户 2026-10-15 定案）。
    /// 约束：不造虚拟曲目、不改表结构 —— SQLite 索引仍是一文件一条，
    /// 展开只发生在内存里的 _playlist：一个整轨音频文件 → N 个 PlaylistItem，
    /// 每个带自己的 StartTimeSeconds（分轨起点，文件时间）与 Duration（到下一轨起点；
    /// 末轨 = 文件时长 − 起点）。播放时引擎按 [起点, 起点+时长] 区间转码，
    /// 进度 / 播完切换 / 拖动 seek 全部天然按"轨"口径，无需上层换算。
    /// 设置「扫描时自动应用 CUE」关掉时整体空转（返回原列表）。
    /// </summary>
    internal static class CueTrackExpansion
    {
        /// <summary>把构建好的曲库条目按同名 cue 展开为分轨（没有 cue 的条目原样保留）。</summary>
        public static List<PlaylistItem> Expand(List<PlaylistItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return items ?? new List<PlaylistItem>();
            }

            if (!AppSettingsStore.Load().AutoApplyCueOnScan)
            {
                return items;
            }

            // 没有 cue 可展开时整体返回原列表（零分配）：曲库里绝大多数文件走这条
            var result = new List<PlaylistItem>(items.Count);
            bool expandedAny = false;

            foreach (PlaylistItem item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.FilePath))
                {
                    continue;
                }

                List<CueTrack>? tracks = null;
                try
                {
                    tracks = CueSheetParser.LoadCueForAudio(item.FilePath);
                }
                catch
                {
                    tracks = null;
                }

                if (tracks == null || tracks.Count == 0)
                {
                    result.Add(item);
                    continue;
                }

                double fileSeconds = item.Duration.TotalSeconds;
                for (int i = 0; i < tracks.Count; i++)
                {
                    CueTrack t = tracks[i];
                    double start = Math.Max(0, t.StartTime.TotalSeconds);
                    // 末轨没有下一轨起点兜底：用文件时长；标签时长缺失(0)则留 0，
                    // 由引擎按转码 WAV 实际长度收尾（SetSourceDuration(0) 不覆盖）。
                    double end = i + 1 < tracks.Count
                        ? Math.Max(start, tracks[i + 1].StartTime.TotalSeconds)
                        : fileSeconds;
                    double length = end > start ? end - start : 0;

                    PlaylistItem track = item.Clone();
                    if (!string.IsNullOrWhiteSpace(t.Title))
                    {
                        track.Title = t.Title;
                    }

                    if (!string.IsNullOrWhiteSpace(t.Artist))
                    {
                        track.Artist = t.Artist;
                        track.AlbumArtist = t.Artist;
                    }

                    track.StartTimeSeconds = start;
                    track.Duration = TimeSpan.FromSeconds(length);
                    track.Track = (uint)(i + 1); // 分轨序号即音轨号，保证专辑视图顺序正确
                    result.Add(track);
                }

                expandedAny = true;
            }

            if (expandedAny)
            {
                try
                {
                    StartupLog.Write("[cue] 分轨展开: " + items.Count + " 个文件 → " + result.Count + " 轨");
                }
                catch
                {
                    // 日志失败不影响展开
                }
            }

            return result;
        }
    }
}
