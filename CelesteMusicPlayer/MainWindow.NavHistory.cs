using System;
using System.Collections.Generic;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 曲库浏览历史（鼠标侧键前进/后退，类似资源管理器）。
    /// 2026-10-31 从壳文件整族搬来：状态类与栈字段在 MainWindow.xaml.cs（活文件），
    /// 快照/比较/提交三方法原在 Library.cs 与 EqDsp.cs（壳），谁都能调所以必须离壳。
    /// </summary>
    public sealed partial class MainWindow
    {
        // 导航快照（2026-10-31 从 MainWindow.Library.cs 搬来）：只读分类与详情页状态（字段都在 xaml.cs，活）。

        private LibraryNavState CaptureLibraryNavState()
            => new()
            {
                Category = _currentCategory,
                ArtistName = _openedArtist?.Name,
                AlbumName = _openedAlbum?.Name,
                AlbumFromArtist = _albumOpenedFromArtist,
                UsesAlbumArtist = _artistDetailUsesAlbumArtist
            };
        // 导航历史比较与提交（2026-10-31 从 MainWindow.EqDsp.cs 搬来）：纯栈操作。

        private static bool LibraryNavStatesEqual(LibraryNavState a, LibraryNavState b)
            => string.Equals(a.Category, b.Category, StringComparison.Ordinal)
               && string.Equals(a.ArtistName, b.ArtistName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.AlbumName, b.AlbumName, StringComparison.OrdinalIgnoreCase)
               && a.AlbumFromArtist == b.AlbumFromArtist
               && a.UsesAlbumArtist == b.UsesAlbumArtist;

        /// <summary>
        /// 切换分类视图时对「活数据」的等价副作用（2026-10-31 第十三刀，自壳 ApplyCategoryView 分离）。
        /// 旧版 ApplyCategoryView（壳 Misc.cs，290 行）九成是给旧界面占位控件摆面板（网页看不到）；
        /// 真正影响网页/播放行为的只有两处，本方法逐行保留：
        /// - case "Songs"：强制重置为「标题升序」并 ApplySort —— _playlist 的顺序 = 网页曲库显示顺序
        ///   （LibraryTracks => _playlist，2026-10-31 第七刀查实），同时经 SyncIndicesAfterSort 保住 _currentIndex；
        /// - case "UserPlaylist"：RenumberCollection 重写队列序号（播放列表保持添加顺序，不排序）。
        /// 其余分支（Albums/Artists/Genres/收藏/评分/文件夹等）只刷旧控件与旧集合（_albums/_artists 等无网页读者），
        /// 对网页世界不可观察，活路径不再执行。壳内调用点继续走旧版 ApplyCategoryView，随壳退场。
        /// </summary>
        private void ApplyCategoryViewData()
        {
            switch (_currentCategory)
            {
                case "Songs":
                {
                    string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                        ? _playlist[_currentIndex].FilePath
                        : null;
                    _sortField = SortField.Title;
                    _sortAscending = true;
                    ApplySort(playingPath);
                    break;
                }

                case "UserPlaylist":
                    RenumberCollection(_queue.Items);
                    break;
            }
        }

        /// <summary>执行会改变中间界面的导航，并写入后退栈（清空前进栈）。</summary>
        private void CommitLibraryNavigation(Action navigate)
        {
            if (_suppressNavHistory)
            {
                navigate();
                _navCurrent = CaptureLibraryNavState();
                return;
            }

            LibraryNavState before = _navCurrent ?? CaptureLibraryNavState();
            navigate();
            LibraryNavState after = CaptureLibraryNavState();
            if (!LibraryNavStatesEqual(before, after))
            {
                _navBackStack.Add(before);
                _navForwardStack.Clear();
            }

            _navCurrent = after;
        }
    }
}
