using System.Threading;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对曲库读模型服务的构造点。
    ///
    /// W13：曲目身份、曲目快照、专辑分组、文件夹节点登记簿都在
    /// <see cref="WebLibraryService"/> 里；这里只把「本地曲库」这一个数据源接过去。
    /// 等曲库集合本身搬出 MainWindow，这支委托就换成直接持有。
    /// </summary>
    public sealed partial class MainWindow
    {
        private WebLibraryService? _webLibrary;

        internal WebLibraryService Library =>
            LazyInitializer.EnsureInitialized(ref _webLibrary, () => new WebLibraryService(() => LibraryTracks));
    }
}
