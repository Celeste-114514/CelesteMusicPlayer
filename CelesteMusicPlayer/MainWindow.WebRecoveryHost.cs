using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对恢复/诊断服务的宿主实现。
    ///
    /// W13 把恢复卡片与诊断的**实现**搬到了 <see cref="WebRecoveryService"/>，
    /// 这里只留它需要向宿主打听 / 请求的那几件事，一共四个成员。
    /// 之所以还挂在 MainWindow 上：队列、引擎、网页外壳的所有权目前仍在这里，
    /// 等服务拆分推进到那一层，这四个口子再跟着换宿主。
    /// </summary>
    public sealed partial class MainWindow : IWebRecoveryHost
    {
        private WebRecoveryService? _recovery;

        /// <summary>恢复 / 诊断服务（本进程内单实例；崩溃记账可能来自非 UI 线程，故用线程安全初始化）。</summary>
        internal WebRecoveryService Recovery =>
            LazyInitializer.EnsureInitialized(ref _recovery, () => new WebRecoveryService(this));

        int IWebRecoveryHost.QueueTrackCount => UserPlaylist.Count;

        bool IWebRecoveryHost.IsPlaying => IsEnginePlayingNow;

        void IWebRecoveryHost.TogglePlayPause() => TogglePlayPausePublic();

        void IWebRecoveryHost.ReloadShellForRecovery() => _webShellWindow?.ReloadShell();
    }
}
