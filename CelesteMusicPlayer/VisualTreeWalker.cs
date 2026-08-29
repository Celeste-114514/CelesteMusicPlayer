using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>视觉树查找与选择集工具：从 MainWindow 里抽出的无状态静态方法。
    /// 注意：类名不能叫 VisualTreeHelper —— 那会遮蔽框架自带的
    /// Microsoft.UI.Xaml.Media.VisualTreeHelper，导致本类内部以及其他文件中
    /// 所有对框架 API（GetParent/GetChild/GetChildrenCount）的调用全部解析到本类而编译失败。
    /// </summary>
    internal static class VisualTreeWalker
    {
        public static Panel? FindItemsPanel(DependencyObject root)
        {
            if (root is ItemsStackPanel stack)
            {
                return stack;
            }

            if (root is ItemsWrapGrid wrap)
            {
                return wrap;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                Panel? found = FindItemsPanel(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        public static ListViewItem? FindAncestorListViewItem(DependencyObject start)
        {
            DependencyObject? current = start;
            while (current != null)
            {
                if (current is ListViewItem item)
                {
                    return item;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        public static Border? FindTaggedBorder(DependencyObject root, string tag)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is Border border
                    && border.Tag is string t
                    && string.Equals(t, tag, StringComparison.Ordinal))
                {
                    return border;
                }

                Border? nested = FindTaggedBorder(child, tag);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        public static ArtistEntry? FindArtistEntry(DependencyObject? start)
        {
            DependencyObject? current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe)
                {
                    if (fe.DataContext is ArtistEntry fromContext)
                    {
                        return fromContext;
                    }

                    if (fe is GridViewItem { Content: ArtistEntry fromContent })
                    {
                        return fromContent;
                    }
                }

                current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        /// <summary>从单元格向上找所属的 PlaylistItem（兼容 x:Bind 时 DataContext 为空的情况）</summary>
        public static PlaylistItem? FindPlaylistItem(DependencyObject start)
        {
            DependencyObject? current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe)
                {
                    if (fe.DataContext is PlaylistItem fromContext)
                    {
                        return fromContext;
                    }

                    if (fe is ListViewItem { Content: PlaylistItem fromContent })
                    {
                        return fromContent;
                    }
                }

                current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        public static HashSet<object>? BuildSelectedItemsLookup(ListViewBase list)
        {
            int count = list.SelectedItems.Count;
            if (count <= 64)
            {
                return null;
            }

            var set = new HashSet<object>();
            foreach (object item in list.SelectedItems)
            {
                set.Add(item);
            }

            return set;
        }

        public static bool IsItemSelected(ListViewBase list, object item, HashSet<object>? selectedSet)
        {
            if (selectedSet != null)
            {
                return selectedSet.Contains(item);
            }

            return list.SelectedItems.Contains(item);
        }
    }
}