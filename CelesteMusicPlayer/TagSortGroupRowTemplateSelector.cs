using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 分组浏览（模块 C）行模板选择器：组头用组头模板、歌曲用歌曲模板。
    /// 共享 TagSortSongListView，避免 XAML 里写两份 ListView 各自管数据。
    /// </summary>
    public sealed class TagSortGroupRowTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? GroupHeaderTemplate { get; set; }
        public DataTemplate? SongRowTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item)
            => item is TagSortGroupHeader ? GroupHeaderTemplate : SongRowTemplate;

        protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }
}
