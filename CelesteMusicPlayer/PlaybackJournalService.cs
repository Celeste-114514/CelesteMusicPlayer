using System;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放台账的宿主事实。服务只做记账，不读引擎内部、不碰控件。
    /// </summary>
    internal interface IPlaybackJournalHost
    {
        /// <summary>当前播放中的文件路径（没在播就是空）。</summary>
        string? NowPlayingPath { get; }

        /// <summary>当前播放位置（写进历史的是「这一遍听了多久」）。</summary>
        double PositionSeconds { get; }

        /// <summary>写历史用的键：优先当前曲目，退回落盘路径。</summary>
        string HistoryKey { get; }

        /// <summary>底层播放器是否有源。</summary>
        bool HasSource { get; }

        /// <summary>底层播放器此刻是否在播。</summary>
        bool IsPlaying { get; }

        /// <summary>把曲目记进音频链路事件（极客/终端页看的那条流水）。</summary>
        void NoteChainTrack(string title, string artist);

        /// <summary>刷新收藏按钮状态。</summary>
        void RefreshFavoriteButton();

        /// <summary>历史写完后通知网页刷新「最近在听」。</summary>
        void RefreshHomeAfterHistory();
    }

    /// <summary>
    /// 播放台账：一次播放从「开始」到「离开」要记的三本账。
    ///
    /// ① <b>统计</b>：播放次数 +1、听歌秒数累加（按秒采样，不是按 tick 累加，避免卡顿算重）。
    /// ② <b>历史</b>：切歌/播完/停止时写进 SQLite 的 playback_history（标题 + 这一遍听了多少秒 + 是否播完）。
    /// ③ <b>Last.fm</b>：起播广播 Now Playing，离开时回传 scrobble（总开关关掉就一条都不发）。
    ///
    /// W13：从 MainWindow.Features.cs / MainWindow.PlayHistory.cs 搬出来。
    /// 这里只保留**记账规则与去重**（同一首不重复计数、采样间隔不小于 1 秒），
    /// 具体落盘仍然是 TrackStatsStore / LibraryDb / LastFmScrobbler 各自的事。
    /// </summary>
    internal sealed class PlaybackJournalService(IPlaybackJournalHost host)
    {
        private DateTime _lastListenSampleUtc = DateTime.UtcNow;
        private string? _listenSamplePath;
        private PlaylistItem? _lastPlayedForScrobble;
        private DateTime _lastPlayStartUtc;

        /// <summary>起播：切歌就调一次（首次播放与无缝自动下一首都走这里）。</summary>
        internal void RecordStart(PlaylistItem item)
        {
            try
            {
                // B4：切歌 = 链路事件（首次播放与无缝自动下一首都走这里）
                host.NoteChainTrack(item.Title, item.Artist);

                _lastPlayedForScrobble = item;
                _lastPlayStartUtc = DateTime.UtcNow;
                TrackStatsStore.RecordPlayStart(item.PersistenceKey);
                _listenSamplePath = item.PersistenceKey;
                _lastListenSampleUtc = DateTime.UtcNow;
                host.RefreshFavoriteButton();

                if (AppSettingsStore.Load() is { EnableLastFm: true, LastFmNowPlaying: true })
                {
                    LastFmScrobbler.QueueNowPlaying(new LastFmTrackInfo
                    {
                        Artist = item.Artist,
                        Title = item.Title,
                        Album = item.Album,
                        DurationSeconds = (int)item.Duration.TotalSeconds
                    });
                    _ = LastFmScrobbler.FlushQueueAsync();
                }
            }
            catch (Exception caught) { StartupLog.WriteException("PlaybackJournal.RecordStart", caught); }
        }

        /// <summary>
        /// 离开上一首时回传 scrobble。Last.fm 总开关关掉时不发 ——
        /// 早期只门控了「正在播放」广播，漏了这里，表现为关了还在涨播放计数。
        /// </summary>
        internal void ScrobblePrevious()
        {
            try
            {
                if (_lastPlayedForScrobble == null || _lastPlayStartUtc == default)
                {
                    return;
                }

                if (!AppSettingsStore.Load().EnableLastFm)
                {
                    return;
                }

                PlaylistItem prev = _lastPlayedForScrobble;
                LastFmScrobbler.QueueScrobble(new LastFmTrackInfo
                {
                    Artist = prev.Artist,
                    Title = prev.Title,
                    Album = prev.Album,
                    DurationSeconds = (int)prev.Duration.TotalSeconds
                }, _lastPlayStartUtc);
                _ = LastFmScrobbler.FlushQueueAsync();
            }
            catch (Exception caught) { StartupLog.WriteException("PlaybackJournal.ScrobblePrevious", caught); }
            finally
            {
                _lastPlayedForScrobble = null;
                _lastPlayStartUtc = default;
            }
        }

        /// <summary>听歌秒数采样：每 ≥1 秒累加一次实际经过的时间（暂停/无源时不记）。</summary>
        internal void SampleListenSeconds()
        {
            if (!host.HasSource) return;

            if (!string.IsNullOrWhiteSpace(_listenSamplePath) && host.IsPlaying)
            {
                DateTime now = DateTime.UtcNow;
                double delta = (now - _lastListenSampleUtc).TotalSeconds;
                if (delta >= 1)
                {
                    TrackStatsStore.AddListenSeconds(_listenSamplePath, delta);
                    _lastListenSampleUtc = now;
                }
            }
        }

        /// <summary>记录当前正在播放的曲目（切歌/播完/停止时调用）。completed=true 表示自然播完。</summary>
        internal void RecordHistory(bool completed)
        {
            try
            {
                string? path = host.NowPlayingPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                string title = Path.GetFileNameWithoutExtension(path);
                double seconds = host.PositionSeconds > 0 ? host.PositionSeconds : 0;
                LibraryDb.RecordPlayback(host.HistoryKey, title, seconds, completed);
                host.RefreshHomeAfterHistory();
            }
            catch (Exception caught) { StartupLog.WriteException("PlaybackJournal.RecordHistory", caught); }
        }
    }
}
