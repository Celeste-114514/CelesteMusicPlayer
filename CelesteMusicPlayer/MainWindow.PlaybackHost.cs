using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对正式播放会话的接线（W13 第一刀）。
    ///
    /// 引擎的所有权在会话里；这里只保留一个只读引用给 145 处既有调用点用，
    /// 免得为了搬一个字段改遍整个播放域。销毁统一走 <see cref="PlaybackSession.ResetEngine"/>。
    /// </summary>
    public sealed partial class MainWindow
    {
        private PlaybackSession? _playbackSession;

        internal PlaybackSession Session =>
            LazyInitializer.EnsureInitialized(ref _playbackSession, () =>
            {
                var session = new PlaybackSession(OutputMode);
                // 播完推进下一首、无缝切轨换标题，都还是界面这边的活；会话只发事实。
                session.Ended += OnEnginePlaybackEnded;
                session.SeamlessChanged += OnSeamlessTrackChanged;
                return session;
            });

        /// <summary>引擎由会话持有；外部只读（切歌、读数、面板都用它）。</summary>
        private AudioPlaybackEngine? _audioEngine => Session.Engine;

        /// <summary>
        /// 退出时销毁引擎。**不能写成 `Session.ResetEngine()`**：会话是懒建的，
        /// 那样会在"从没播过歌"的退出路径上凭空造一个引擎再销毁。
        /// </summary>
        internal void ResetPlaybackEngine() => _playbackSession?.ResetEngine();
    }
}
