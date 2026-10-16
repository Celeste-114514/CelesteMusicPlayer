using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 极简换行面板（链路面板「技术档案」胶囊组用）。
    ///
    /// WinUI 3 没有内置 WrapPanel（UWP 的 WrapPanel 没跟过来），为了不新增 NuGet 依赖
    /// 自己写一个够用的：从左到右排，排不下就换行，行高取该行最高子项。
    /// 只服务于静态小徽标，不做虚拟化。
    /// </summary>
    public sealed class ChainWrapPanel : Panel
    {
        /// <summary>同一行内相邻子项的水平间距（px）。</summary>
        public double ColumnSpacing { get; set; } = 5;

        /// <summary>行与行的垂直间距（px）。</summary>
        public double RowSpacing { get; set; } = 5;

        protected override Size MeasureOverride(Size availableSize)
        {
            double limit = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
                ? 100000
                : availableSize.Width;

            double x = 0;
            double y = 0;
            double rowHeight = 0;

            foreach (UIElement child in Children)
            {
                child.Measure(new Size(limit, availableSize.Height));
                Size desired = child.DesiredSize;

                if (x > 0 && x + desired.Width > limit)
                {
                    x = 0;
                    y += rowHeight + RowSpacing;
                    rowHeight = 0;
                }

                x += desired.Width + ColumnSpacing;
                rowHeight = Math.Max(rowHeight, desired.Height);
            }

            return new Size(limit, y + rowHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            double y = 0;
            double rowHeight = 0;

            foreach (UIElement child in Children)
            {
                Size desired = child.DesiredSize;

                if (x > 0 && x + desired.Width > finalSize.Width)
                {
                    x = 0;
                    y += rowHeight + RowSpacing;
                    rowHeight = 0;
                }

                child.Arrange(new Windows.Foundation.Rect(x, y, desired.Width, desired.Height));
                x += desired.Width + ColumnSpacing;
                rowHeight = Math.Max(rowHeight, desired.Height);
            }

            return finalSize;
        }
    }
}
