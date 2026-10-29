using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// MainWindow 对「曲库浏览面」的接线（W13）。
    ///
    /// <see cref="WebLibraryBrowseService"/> 零播放、零 UI、零宿主口子，
    /// 唯一依赖是曲库快照，所以这里只有两条转发 —— 没有副作用表，也没有 ISettingsHost 那样的接口。
    /// </summary>
    public sealed partial class MainWindow
    {
        private WebLibraryBrowseService? _libraryBrowse;

        internal WebLibraryBrowseService LibraryBrowse =>
            LazyInitializer.EnsureInitialized(ref _libraryBrowse, () => new WebLibraryBrowseService(Library));

        internal object WebBrowse(JsonElement msg) => LibraryBrowse.Browse(msg);

        internal Task<object> WebCollectionOverviewAsync(string category, string name, CancellationToken cancellation)
            => LibraryBrowse.CollectionOverviewAsync(category, name, cancellation);
    }
}
