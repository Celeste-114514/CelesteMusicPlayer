using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        // W13：动作本体在 WebTrackActionService，这里只剩宿主能力 + 转发。
        private readonly WebOperationGate _webBusy = new();
        private WebTrackActionService? _trackActions;

        private WebTrackActionService TrackActions =>
            LazyInitializer.EnsureInitialized(ref _trackActions,
                () => new WebTrackActionService(new WebTrackActionHost(this), _webBusy));

        internal Task<object> WebBrowseAsync(JsonElement msg, object host) => TrackActions.BrowseAsync(msg, host);

        internal Task<object> WebTrackActionAsync(JsonElement msg, object host) => TrackActions.TrackActionAsync(msg, host);

        /// <summary>插队/追加。目录浏览页的 queue 动作也走这里。</summary>
        private void WebInsertQueue(PlaylistItem[] songs, bool next) => TrackActions.InsertQueue(songs, next);

        /// <summary>
        /// 队列内容改完之后：重编号 → 刷新队列视图 → 通知网页主页 → 用户播放列表重新过滤。
        ///
        /// PlaylistView 现在是 <c>LegacyControls</c> 里的编译占位控件（没有视觉树），
        /// 那两句重绑保留原样只是不想让行为漂移；真界面回来时不用重想顺序。
        /// </summary>
        private void RefreshQueueUi()
        {
            _playlistViewSource = null;
            _playlistViewSource = _queue.Items;
            RenumberCollection(_queue.Items);
            NotifyCurrentPlaylistWindow();
            if (_currentCategory == "UserPlaylist") ApplyUserPlaylistSearchFilter();
        }

        /// <summary>
        /// 宿主要给曲目动作服务的那几样东西：真播放、删盘、外部程序、页面快照。
        /// 别往这里加「服务顺手能读到的状态」——曲库快照已经用属性给了，队列整个交出去了。
        /// </summary>
        private sealed class WebTrackActionHost(MainWindow owner) : IWebTrackActionHost
        {
            public PlaybackQueue Queue => owner._queue;

            public PlaylistItem[] AllLibraryTracks => owner.Library.AllTracks();

            public IReadOnlyDictionary<string, PlaylistItem> LibraryFolderTracks => owner.Library.FolderTracks;

            public void PlayQueueIndex(int index) => owner.PlayUserPlaylistAt(index);

            public void PlaySong(PlaylistItem song)
            {
                if (owner.LibraryTracks.Contains(song)) owner.WebPlayTrack(song);
                else owner.PlayPlaylistItem(song);
            }

            public void RefreshQueueUi() => owner.RefreshQueueUi();

            public void RefreshFavoriteChrome() => owner.UpdateFavoriteButtonUi();

            public void ShowTagEditor(IEnumerable<string> keys) => owner.OpenTagEditForFiles(keys);

            public void RevealInExplorer(string filePath) => OpenFileLocationInExplorer(filePath);

            public void ActivateWindow() => owner.Activate();

            public Task DeleteFromDiskAsync(PlaylistItem song) => owner.DeleteSongFromDiskAsync(song, owner.Content?.XamlRoot);

            public object BrowseSnapshot(JsonElement msg) => owner.WebBrowse(msg);

            public Task LoadNamedPlaylistAndPlayAsync(string name) => owner.LoadNamedPlaylistToQueueAndPlayAsync(name);

            public void AddNamedPlaylistToQueue(string name) => owner.AddNamedPlaylistToQueue(name);

            public Task PlayCueFileAsync(string path) => owner.PlayCueFileAsync(path);
        }
    }
}
