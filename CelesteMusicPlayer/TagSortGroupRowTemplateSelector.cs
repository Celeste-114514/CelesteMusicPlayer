using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 分组浏览（模块 C）行模板选择器：组头用组头模板、歌曲用歌曲模板。
    /// WinUI3 的 DataTemplateSelector 是普通类（不从 DependencyObject 继承），
    /// 所以这里用 CLR 自动属性；XAML 里通过属性元素语法 <local:Selector.GroupHeaderTemplate> 赋值。
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
