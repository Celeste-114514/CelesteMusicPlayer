// ============================================================================
// MiniPlayerBridge —— 迷你播放器的「状态出口 + 命令入口」（2026-10-19 重写）
//
// 为什么要多这一层：旧版迷你播放器直接抓着主窗口的内部方法反向调用（全项目 27 处
// 挂接），主窗口动一点它就跟着出错 —— 用户说的"很多 bug"就是这么来的。
//
// 新版定成**单向数据流**：
//   主窗口 ──（推一份快照）──▶ 迷你窗口：只负责把状态画出来
//   迷你窗口 ──（发一句命令）──▶ 由主窗口去执行
// 迷你窗口里一行播放逻辑都没有，也不再认识 MainWindow 的任何字段，
// 只认这一份 MiniPlayerSnapshot 和下面这几个命令。
//
// ⚠ 所有方法都假定在 UI 线程调用（主窗口的推送点与命令入口本来就在 UI 线程）。
// ============================================================================

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;

namespace CelesteMusicPlayer
{
    /// <summary>一次推送给迷你窗口的播放状态（拿来就画，不改）。</summary>
    internal sealed class MiniPlayerSnapshot
    {
        public string Title = "";
        public string Artist = "";

        /// <summary>主窗口已经解码好的封面（直接复用，迷你不再自己解码一遍）。</summary>
        public ImageSource? Cover;

        /// <summary>封面原始字节，仅供"底色跟着封面走"取色用。</summary>
        public byte[]? CoverBytes;

        public double PositionSeconds;
        public double DurationSeconds;
        public bool IsPlaying;

        /// <summary>音量 0~100。</summary>
        public double Volume;

        public List<string> QueueTitles = new();
        public int QueueIndex = -1;

        /// <summary>当前这一句歌词（没有歌词时为空）。</summary>
        public string LyricLine = "";

        /// <summary>
        /// 整首歌的歌词行（直接引用主窗口的 <see cref="List{T}"/>，只读，零拷贝）。
        /// 切歌时主窗口换的是整个列表引用（不是原地改），迷你窗按引用比对即可知道要不要重建。
        /// </summary>
        public IReadOnlyList<LyricLine>? LyricLines;

        /// <summary>当前高亮歌词行下标（-1 = 没有）。</summary>
        public int LyricIndex = -1;
    }

    internal static class MiniPlayerBridge
    {
        private static MainWindow? _host;

        /// <summary>最近一次推送的状态（迷你窗口打开时先拿它兜底）。</summary>
        public static MiniPlayerSnapshot Current { get; private set; } = new();

        /// <summary>状态变化通知（UI 线程）。迷你窗口订阅这一个就够。</summary>
        public static event Action<MiniPlayerSnapshot>? StateChanged;

        public static bool HasHost => _host != null;

        /// <summary>有没有迷你窗口在听（没开窗时 tick 不白建快照）。</summary>
        public static bool HasSubscribers => StateChanged != null;

        public static void Attach(MainWindow host)
        {
            _host = host;
        }

        public static void Detach(MainWindow host)
        {
            if (ReferenceEquals(_host, host))
            {
                _host = null;
            }
        }

        /// <summary>
        /// 从主窗口拉一次最新状态并广播出去。
        /// 调用点：播放/暂停/切歌/起播停止等状态变化处，以及主窗口的进度 tick（每秒）。
        /// </summary>
        public static void Push()
        {
            MainWindow? host = _host;
            if (host == null)
            {
                return;
            }

            try
            {
                MiniPlayerSnapshot snap = new();

                PlaylistItem? item = host.GetCurrentPlayingItem();
                snap.Title = item?.Title ?? "未在播放";
                snap.Artist = item?.Artist ?? "";
                // 小封面走 Thumb 160 档（播放栏同款同一张）：迷你窗只显示 72×72，
                // 拿播放页大图（Large 768）硬缩十倍必糊（用户 10-19 报的问题）。
                // 刚切歌瞬间可能还没取到（null），下一个 tick 就补上，迷你窗先显示占位。
                snap.Cover = host.GetCurrentCoverThumbImage();
                snap.CoverBytes = host.GetCurrentCoverBytes();

                // 位置/时长：引擎在跑时用引擎的值（MediaPlayer 可能还挂着上一首的会话）
                if (host.IsEngineActiveNow)
                {
                    snap.PositionSeconds = host.EnginePositionValue.TotalSeconds;
                    snap.DurationSeconds = host.EngineDurationValue.TotalSeconds;
                    snap.IsPlaying = host.IsEnginePlayingNow;
                }
                else
                {
                    MediaPlayer? player = host.GetMediaPlayerPublic();
                    if (player?.Source != null)
                    {
                        snap.PositionSeconds = player.PlaybackSession.Position.TotalSeconds;
                        snap.DurationSeconds = player.PlaybackSession.NaturalDuration.TotalSeconds;
                        snap.IsPlaying =
                            player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                    }
                }

                snap.Volume = host.GetVolumePublic();

                IReadOnlyList<PlaylistItem> queue = host.GetUserPlaylistPublic();
                int count = Math.Min(queue.Count, 200); // 队列再长也只列前 200 首
                for (int i = 0; i < count; i++)
                {
                    snap.QueueTitles.Add(queue[i].Title);
                }

                snap.QueueIndex = host.GetUserPlaylistIndexPublic();
                snap.LyricLine = host.GetCurrentLyricTextPublic();
                // 扩展区（歌词面板）用：整表直接引用 + 当前行下标。
                // 同一份数据主窗口歌词页也在用，这里只读，不复制。
                snap.LyricLines = host.GetLyricLinesPublic();
                snap.LyricIndex = host.GetCurrentLyricIndexPublic();

                Current = snap;
                StateChanged?.Invoke(snap);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MiniPlayerBridge.Push", ex);
            }
        }

        // ---------------------------------------------------------------- 命令
        // 迷你窗口只发命令，执行全在主窗口；执行完立刻回推一次状态，
        // 这样点下去马上就有反馈，不用等下一秒的 tick。

        /// <summary>
        /// 主窗口进度 tick 调这个（有迷你窗口在听才真推）。
        /// 命令入口仍走 <see cref="Push"/>（无条件回推，点下去马上有反馈）。
        /// </summary>
        public static void PushTick()
        {
            if (HasSubscribers)
            {
                Push();
            }
        }

        public static void TogglePlayPause() => Run(h => h.TogglePlayPausePublic());

        public static void Next() => Run(h => h.NextPublic());

        public static void Previous() => Run(h => h.PreviousPublic());

        public static void Seek(double seconds) => Run(h => h.SeekSourceSeconds(seconds));

        public static void SetVolume(double value0To100) => Run(h => h.SetVolumePublic(value0To100));

        public static void PlayQueueAt(int index) => Run(h => h.PlayUserPlaylistAtPublic(index));

        /// <summary>双击迷你窗口 → 把主窗口叫到前台。</summary>
        public static void BringMainWindowToFront()
        {
            // 留一行日志：双击判定埋在按下时序里（不依赖 DoubleTapped 事件），
            // 排查"双击没反应"时看这行在不在 —— 在=命令发了（剩下是系统前台锁的事），
            // 不在=双击判定本身没触发。
            StartupLog.Write("迷你播放器：双击已发出，请求激活主窗口");
            Run(h => h.Activate());
        }

        private static void Run(Action<MainWindow> action)
        {
            MainWindow? host = _host;
            if (host == null)
            {
                return;
            }

            try
            {
                action(host);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MiniPlayerBridge.Command", ex);
            }

            Push();
        }
    }
}
