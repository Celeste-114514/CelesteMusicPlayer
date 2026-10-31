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
