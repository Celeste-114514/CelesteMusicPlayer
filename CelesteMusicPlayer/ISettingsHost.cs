using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 设置域对宿主的要求（W13）。
    ///
    /// 把网页设置命令从 <c>MainWindow</c> 搬出去之前，先盘了一遍每个操作到底卡在哪：
    /// 对话框要窗口句柄、凭据要 DPAPI、导入导出要文件选择器 —— 这些都能在服务内部自己完成
    /// （句柄由静态的 <see cref="NativeMainWebWindow.HandleOf"/> 从调用方窗口取，不需要宿主参与）。
    ///
    /// 真正非宿主不可的只剩四件事：回 UI 线程、把改动落到真实服务、重启目录监视、读曲库快照。
    /// 这里刻意只留这四条，不要把 MainWindow 的属性顺手加进来 —— 口子一宽，
    /// 服务就会重新长回宿主身上，等于没搬。
    /// </summary>
    internal interface ISettingsHost
    {
        /// <summary>曲库快照。设置页只读它算「这个目录贡献了几首歌」。</summary>
        IReadOnlyList<PlaylistItem> LibraryTracks { get; }

        /// <summary>回到 UI 线程执行。窗口、引擎、主题只认这条线程。</summary>
        Task RunOnUiAsync(Action action);

        /// <summary>把改过的字段接到真实服务上（音量、主题、热键、SMTC、目录监视……）。</summary>
        Task ApplyChangesAsync(string[] ids);

        /// <summary>监视目录增删之后重启目录监视服务。</summary>
        Task RestartLibraryWatchAsync();
    }
}
