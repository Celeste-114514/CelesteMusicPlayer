using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对正式播放会话的接线（W13 第一~三刀）。
    ///
    /// 引擎的所有权、权威当前曲目（CurrentItem）、无缝预载状态机都在会话里；
    /// 这里只保留一个只读引用给 145 处既有调用点用。销毁统一走 <see cref="PlaybackSession.ResetEngine"/>。
    /// 宿主口子共三条：NextResolver（"下一首是谁"）、Ended（播完推进）、SeamlessAdvanced（换轨换界面）。
    /// </summary>
    public sealed partial class MainWindow
    {
        private PlaybackSession? _playbackSession;

        internal PlaybackSession Session =>
            LazyInitializer.EnsureInitialized(ref _playbackSession, () =>
            {
                var session = new PlaybackSession(OutputMode);
                // 「下一首是谁」的规则（队列/曲库/播放顺序含随机）住在宿主；会话只管预载与记账。
                session.NextResolver = ResolveSequentialNextItem;
                // 播完推进下一首、无缝切轨换标题，都还是界面这边的活；会话只发事实。
                session.Ended += OnEnginePlaybackEnded;
                session.SeamlessAdvanced += OnSeamlessAdvanced;
                return session;
            });

        /// <summary>引擎由会话持有；外部只读（切歌、读数、面板都用它）。</summary>
        private AudioPlaybackEngine? _audioEngine => Session.Engine;

        /// <summary>
        /// 「当前在播哪首」的权威记录，存储在会话里（StartPlayback 钉下、无缝换轨由会话推进）。
        /// 保留原字段名，既有 7 个调用点一行不动。
        /// </summary>
        private PlaylistItem? _currentPlayingItem
        {
            get => _playbackSession?.CurrentItem;
            set { if (value != null || _playbackSession != null) Session.SetCurrentItem(value); }
        }

        /// <summary>
        /// 退出时销毁引擎。**不能写成 `Session.ResetEngine()`**：会话是懒建的，
        /// 那样会在"从没播过歌"的退出路径上凭空造一个引擎再销毁。
        /// </summary>
        internal void ResetPlaybackEngine() => _playbackSession?.ResetEngine();

        /// <summary>退出时放手预载的网络缓存租约。判空调用，不会凭空造会话。</summary>
        internal void ReleasePreloadLease() => _playbackSession?.ReleasePreloadLease();
    }
}
