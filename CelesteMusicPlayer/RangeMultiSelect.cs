using System;
using System.Runtime.CompilerServices;
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
        // 用 ConditionalWeakTable<ListViewBase, StrongBox<int>> 而不是 Dictionary<ListViewBase, int>：
        // 静态字典会永久持有 ListView 强引用，窗口反复开关时连同其可视化树一起无法回收
        // （「播放长音频内存暴涨」同类问题的另一处表现）。ConditionalWeakTable 的条目
        // 随 key 被 GC 一并消亡，无需也无法手动清理。
        // TValue 必须是引用类型，故用 StrongBox<int> 承载锚点索引。
        private static readonly ConditionalWeakTable<ListViewBase, StrongBox<int>> AnchorIndex = new();

        // 重入深度而非 bool：批量 SelectedItems.Add 过程中 SelectionChanged 可能同步重入，
        // 用 bool 会被外层提前清掉导致范围选择自我触发。计数保证嵌套层级全部退出后才复位。
        private static int _suppressDepth;

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
            if (_suppressDepth > 0)
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

            bool hasAnchor = AnchorIndex.TryGetValue(list, out StrongBox<int>? anchorBox);
            int anchor = anchorBox?.Value ?? -1;

            if (index >= 0 && hasAnchor && anchor >= 0 && anchor < list.Items.Count
                && anchor != index && IsShiftDown())
            {
                int from = Math.Min(anchor, index);
                int to = Math.Max(anchor, index);

                _suppressDepth++;
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
                    _suppressDepth--;
                }
            }

            if (index >= 0)
            {
                // 已存在则更新盒子内的值，不存在则新增。不能用索引器赋值——
                // ConditionalWeakTable 首次写入时 key 不存在，索引器会抛 KeyNotFoundException。
                if (AnchorIndex.TryGetValue(list, out StrongBox<int>? existing) && existing != null)
                {
                    existing.Value = index;
                }
                else
                {
                    AnchorIndex.Add(list, new StrongBox<int>(index));
                }
            }
        }
    }
}
