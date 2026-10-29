using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对网页各服务的构造点。
    ///
    /// W13：这里**只**负责把曲库快照能力接到服务上，业务计算本身在
    /// <see cref="WebHomeService"/> 等独立类里。随着更多域搬出去，这里会逐条减少。
    /// </summary>
    public sealed partial class MainWindow
    {
        private WebHomeService? _webHome;

        internal WebHomeService Home =>
            LazyInitializer.EnsureInitialized(ref _webHome, () => new WebHomeService(() => Library.AllTracks(), WebLibraryService.Albums, WebTrackIdentity.Of));
    }
}
