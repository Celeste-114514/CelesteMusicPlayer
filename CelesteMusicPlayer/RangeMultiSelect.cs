using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 列表「Shift 连选一段 / Ctrl 跳着选」的通用支持。
    /// 背景：本应用的列表多选用的是 WinUI 的 Multiple 模式——普通单击与 Ctrl+单击本身就是
    /// 「切换选中」，跳着多选天然可用；只有「Shift 点一下选一整段」是 Multiple 模式不提供的，
    /// 这里补上。做法是记录每次点击的锚点索引，检测到 Shift 按下时把锚点到当前项之间全选。
    /// 不改成 Extended 模式，是因为 Extended 会让普通单击变成「只留这一项」，破坏现有手感。
    /// </summary>
    internal static class RangeMultiSelect
    {
        private static readonly Dictionary<ListViewBase, int> AnchorIndex = new();
        private static bool _suppressRangeSelection;

        /// <summary>列表当前是否处于可多选的选择模式（Multiple / Extended 均算）。</summary>
        internal static bool IsMultiSelectMode(ListViewBase? list)
            => list != null
                && (list.SelectionMode == ListViewSelectionMode.Multiple
                    || list.SelectionMode == ListViewSelectionMode.Extended);

        /// <summary>给列表挂上范围多选（可重复调用，不会重复订阅）。</summary>
        internal static void Attach(ListViewBase? list)
        {
            if (list == null)
            {
                return;
            }

            list.SelectionChanged -= OnSelectionChanged;
            list.SelectionChanged += OnSelectionChanged;
        }

        private static bool IsShiftDown()
        {
            try
            {
                var state = Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift);
                return state.HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("RangeMultiSelect.cs", caught);
                return false;
            }
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressRangeSelection)
            {
                return;
            }

            if (sender is not ListViewBase list || !IsMultiSelectMode(list))
            {
                return;
            }

            int index = -1;
            if (e.AddedItems.Count > 0)
            {
                index = list.Items.IndexOf(e.AddedItems[0]);
            }

            if (index < 0 && list.SelectedIndex >= 0)
            {
                index = list.SelectedIndex;
            }

            bool hasAnchor = AnchorIndex.TryGetValue(list, out int anchor);

            if (index >= 0 && hasAnchor && anchor >= 0 && anchor < list.Items.Count
                && anchor != index && IsShiftDown())
            {
                int from = Math.Min(anchor, index);
                int to = Math.Max(anchor, index);

                _suppressRangeSelection = true;
                try
                {
                    for (int i = from; i <= to && i < list.Items.Count; i++)
                    {
                        object? item = list.Items[i];
                        if (item != null && !list.SelectedItems.Contains(item))
                        {
                            list.SelectedItems.Add(item);
                        }
                    }
                }
                catch (Exception caught)
                {
                    StartupLog.WriteException("RangeMultiSelect.cs", caught);
                }
                finally
                {
                    _suppressRangeSelection = false;
                }
            }

            if (index >= 0)
            {
                AnchorIndex[list] = index;
            }
        }
    }
}
