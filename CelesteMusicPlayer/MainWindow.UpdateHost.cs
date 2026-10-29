using System;
using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对网页更新服务的宿主实现。
    /// W13：状态翻译与命令转发在 <see cref="WebUpdateService"/>，这里只接四支动作。
    /// </summary>
    public sealed partial class MainWindow : IUpdateHost
    {
        private WebUpdateService? _webUpdate;

        internal WebUpdateService Update =>
            LazyInitializer.EnsureInitialized(ref _webUpdate, () => new WebUpdateService(this));

        void IUpdateHost.RunOnUiThread(Action action) => DispatcherQueue.TryEnqueue(() => action());

        void IUpdateHost.PublishUpdateStatus(object wire) => _webShellWindow?.NotifyUpdateStatus(wire);

        void IUpdateHost.FlushBeforeHandoff() => AppSettingsStore.MarkAppCleanExit();

        void IUpdateHost.ExitForInstall() => ExitApplication();
    }
}
