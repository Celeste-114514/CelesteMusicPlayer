using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 标签排序分组浏览（模块 C）一行：表示「按某字段分组的某个组」，携带组内歌曲引用。
    /// 列表用扁平化方式渲染组头行 + 歌曲行；折叠=从 FlatRows 移除组内歌曲，展开=插回。
    /// </summary>
    public sealed class TagSortGroupHeader : INotifyPropertyChanged
    {
        private bool _isExpanded = true;

        /// <summary>分组字段 Key（如 "Artist"），供显示。</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>字段显示名（如"艺术家"）。</summary>
        public string FieldLabel { get; set; } = string.Empty;

        /// <summary>组值（如艺术家名"周杰伦"）。</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>组内歌曲数。</summary>
        public int Count { get; set; }

        public string CountText => Count + " 首";

        /// <summary>组头折叠图标（▾ 展开 / ▸ 折叠）。</summary>
        public string Glyph => IsExpanded ? "\u25BE" : "\u25B8";

        /// <summary>是否展开（默认展开）。折叠时 FlatRows 不含组内歌曲行。</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Glyph));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
