using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 标签排序分组浏览（模块 C）一行：表示「按某字段分组的某个组」，携带组内歌曲引用。
    /// 列表用扁平化方式渲染组头行 + 歌曲行；折叠=从 FlatRows 移除组内歌曲，展开=插回。
    ///
    /// 注意：所有可绑定字段必须用 backing field + get/set 自动属性，
    /// 不能用 expression-bodied 只读属性（WinUI3 1.8 的 XamlBind 源码生成器
    /// 在为 OneWay 绑定生成 update setter 时对只读 expression-bodied 属性会触发 WMC9999）。
    /// </summary>
    public sealed class TagSortGroupHeader : INotifyPropertyChanged
    {
        private bool _isExpanded = true;
        private string _glyph = "\uE710";   // 展开=朝下箭头(Segoe MDL2，与文件夹面板同款字体，不会变方块)

        /// <summary>分组字段 Key（如 "Artist"），供显示。</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>字段显示名（如"艺术家"）。</summary>
        public string FieldLabel { get; set; } = string.Empty;

        /// <summary>组值（如艺术家名"周杰伦"）。</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>组内歌曲数。</summary>
        public int Count { get; set; }

        public string CountText => Count + " 首";

        /// <summary>层级深度（0 = 最外层分组）。多字段嵌套分组时用。</summary>
        public int Depth { get; set; }

        /// <summary>子分组节点（非叶子层级）。末级分组此属性为 null。</summary>
        public List<TagSortGroupHeader>? Children { get; set; }

        /// <summary>末级分组的歌曲（叶子节点）。非叶子层级此属性为 null。</summary>
        public List<PlaylistItem>? Songs { get; set; }

        /// <summary>左侧缩进像素（= Depth * 步长），由 ContainerContentChanging 写入行 Padding。</summary>
        public int Indent { get; set; }

        /// <summary>是否为叶子分组（直接挂歌曲）。</summary>
        public bool IsLeaf => Songs != null && Songs.Count > 0;

        /// <summary>组头底色：浅白半透明胶囊，模仿文件夹浏览器「根行」的视觉层级。</summary>
        public Microsoft.UI.Xaml.Media.Brush ChromeBackground =>
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 255, 255, 255));

        /// <summary>组头折叠图标（▾ 展开 / ▸ 折叠）。必须用可写属性 + 通知，
        /// 因为 x:Bind OneWay 在 WinUI3 1.8 上对 expression-bodied 只读属性会触发 WMC9999。</summary>
        public string Glyph
        {
            get => _glyph;
            set
            {
                if (_glyph == value) return;
                _glyph = value;
                OnPropertyChanged();
            }
        }

        /// <summary>是否展开（默认展开）。折叠时 FlatRows 不含组内歌曲行。</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                Glyph = value ? "\uE710" : "\uE70D";   // 展开朝下(E710) / 折叠朝右(E70D)，与文件夹面板一致
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
