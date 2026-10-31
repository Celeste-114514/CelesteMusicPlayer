using System.Text.Json;
using System.Threading;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        // W13：队列页与「更多」菜单的业务在 WebQueueService，这里只剩宿主能力 + 转发。
        private WebQueueService? _queueService;

        private WebQueueService QueuePages =>
            LazyInitializer.EnsureInitialized(ref _queueService,
                () => new WebQueueService(new WebQueueHost(this)));

        internal object WebQueue(JsonElement msg) => QueuePages.QueueSnapshot(msg);

        internal object WebMore(JsonElement msg) => QueuePages.MoreMenu(msg);

        /// <summary>
        /// 宿主要给队列服务的那几样东西：队列模型、播放顺序、当前曲目、真起播、界面刷新、外部程序。
        /// 别往这里加「服务顺手能读到的状态」—— 队列整个交出去了，快照由它自己算。
        /// </summary>
        private sealed class WebQueueHost(MainWindow owner) : IWebQueueHost
        {
            public PlaybackQueue Queue => owner._queue;

            public PlaybackOrderResolver OrderResolver => owner._orderResolver;

            public PlaylistItem? CurrentTrack => owner.GetCurrentPlayingItem();

            public void PlayQueueIndex(int index) => owner.PlayUserPlaylistAt(index);

            public void RefreshQueueUi() => owner.RefreshQueueUi();

            public void RevealInExplorer(string filePath) => OpenFileLocationInExplorer(filePath);
        }
    }
}
