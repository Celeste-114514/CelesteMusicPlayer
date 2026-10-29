using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对热键服务的构造点。
    /// W13：读写与校验在 <see cref="HotkeysService"/>，这里只把「重启注册」这个动作接过去
    /// （必须回到 UI 线程：热键挂在线程消息循环上）。
    /// </summary>
    public sealed partial class MainWindow
    {
        private HotkeysService? _hotkeysService;

        internal HotkeysService Hotkeys =>
            LazyInitializer.EnsureInitialized(
                ref _hotkeysService,
                () => new HotkeysService(() => DispatcherQueue.TryEnqueue(RestartGlobalHotkeysFromSettings)));
    }
}
