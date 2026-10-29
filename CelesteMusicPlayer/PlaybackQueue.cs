using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放队列（「接下来播放」那一列）。
    ///
    /// W13：从 MainWindow 里两个裸字段（用户播放列表 + 它的播放下标）提升成类型。
    /// 目前只装**队列自身的状态**：曲子、当前索引、给网页看的修订号。
    /// 选下一首、随机/循环、插入规则等调度逻辑仍在 MainWindow，等播放会话整体搬出来时再进来。
    ///
    /// 修订号的意义：网页靠它判断「我手里的队列是不是过期了」。
    /// 拖动/插入/删除之后修订号变化，网页拿旧号提交就被拒，避免用错位去点播。
    /// </summary>
    internal sealed class PlaybackQueue
    {
        private ObservableCollection<PlaylistItem> _items = new();
        private PlaylistItem[] _webSnapshot = Array.Empty<PlaylistItem>();
        private int _revision;

        /// <summary>队列里的曲目（外部直接读写，界面绑定也用它）。</summary>
        internal ObservableCollection<PlaylistItem> Items => _items;

        /// <summary>当前正在播放的下标；-1 = 没有在播的队列项。</summary>
        internal int Index { get; set; } = -1;

        /// <summary>整份替换（排序、恢复、载入播放列表都会用到）。索引由调用方自己维护。</summary>
        internal void Replace(IEnumerable<PlaylistItem> items) =>
            _items = new ObservableCollection<PlaylistItem>(items);

        /// <summary>清空并回到「没有在播的队列项」。</summary>
        internal void Clear()
        {
            _items.Clear();
            Index = -1;
        }

        /// <summary>
        /// 取一份网页用的队列快照 + 当前修订号。内容变了修订号才 +1
        /// （每 250ms 的轮询不会把修订号刷爆）。
        /// </summary>
        internal (PlaylistItem[] Items, int Revision) WebSnapshot()
        {
            var items = _items.ToArray();
            if (!_webSnapshot.SequenceEqual(items))
            {
                _webSnapshot = items;
                _revision++;
            }
            return (items, _revision);
        }

        /// <summary>当前修订号（网页提交操作时带着它做冲突检测）。</summary>
        internal int Revision => _revision;

        /// <summary>
        /// 队列内容刚被就地改过（拖动等）之后，主动记一版快照并让修订号 +1。
        /// 记快照是为了下一次 <see cref="WebSnapshot"/> 不再重复 +1（旧实现就是这个语义）。
        /// </summary>
        internal void Touch()
        {
            _webSnapshot = _items.ToArray();
            _revision++;
        }
    }
}
