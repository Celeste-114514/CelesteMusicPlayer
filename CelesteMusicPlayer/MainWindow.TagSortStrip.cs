using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace CelesteMusicPlayer
{
    /// <summary>字段条上的一颗胶囊：内部用字段 Key，界面显示中文名。</summary>
    internal sealed class TagSortFieldChip
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
    }

    /// <summary>
    /// 分组浏览的「内联字段条」（2026-10-12 阶段 1）：原「自定义…」弹窗换成主界面里的一条胶囊横排。
    /// 三条操作路径，最后都走同一个落盘出口：
    ///   ① 左右拖动胶囊 → DragItemsCompleted
    ///   ② 右键 → 前移 / 后移 / 删除
    ///   ③ 最右「＋」→ 从尚未添加的字段里挑一个追加
    /// 落盘行为与原来弹窗「确认」后完全一致（自定义快照 + 预设切到「自定义（已保存）」+ 重建分组）。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly ObservableCollection<TagSortFieldChip> _groupFieldChips = new();
        private bool _groupFieldStripWired;

        /// <summary>按当前字段列表重建胶囊。只做展示，不落盘（落盘只在用户真的改动之后）。</summary>
        private void RebuildGroupFieldStrip()
        {
            try
            {
                if (TagSortGroupFieldStrip == null)
                {
                    return;
                }

                if (!_groupFieldStripWired)
                {
                    _groupFieldStripWired = true;
                    TagSortGroupFieldStrip.ItemsSource = _groupFieldChips;
                }

                _groupFieldChips.Clear();
                foreach (string key in _tagSortGroupFields)
                {
                    _groupFieldChips.Add(new TagSortFieldChip
                    {
                        Key = key,
                        Name = TagSortFields.Find(key)?.Label ?? key
                    });
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("RebuildGroupFieldStrip", ex);
            }
        }

        /// <summary>字段条发生任何改动后的统一出口：读回顺序 → 写自定义快照 → 重建分组 → 落盘。</summary>
        private void CommitGroupFieldsFromStrip()
        {
            try
            {
                List<string> fields = _groupFieldChips.Select(c => c.Key).ToList();

                // 一个都不留会让分组浏览无从下手，兜回与旧弹窗一致的默认值
                if (fields.Count == 0)
                {
                    fields = new List<string> { "Artist", "Album" };
                    _groupFieldChips.Clear();
                    foreach (string key in fields)
                    {
                        _groupFieldChips.Add(new TagSortFieldChip
                        {
                            Key = key,
                            Name = TagSortFields.Find(key)?.Label ?? key
                        });
                    }
                }

                _tagSortGroupFields = fields;
                _tagSortGroupCustom = fields.ToList();
                _tagSortGroupActivePreset = "__custom__";

                RebuildTagSortGroupRows();
                SyncTagSortGroupFieldCombo();
                AppSettingsStore.Update(s =>
                {
                    s.TagSortGroupFields = _tagSortGroupCustom;
                    s.TagSortGroupActivePreset = _tagSortGroupActivePreset;
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CommitGroupFieldsFromStrip", ex);
            }
        }

        /// <summary>拖动结束：ListView 已经把源集合排好序，这里只需落盘。</summary>
        private void TagSortGroupFieldStrip_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            CommitGroupFieldsFromStrip();
        }

        private void TagSortGroupFieldStrip_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            try
            {
                // 右键点在胶囊的文字/边框上都算，取不到就退回当前选中项
                TagSortFieldChip? chip = (e.OriginalSource as FrameworkElement)?.DataContext as TagSortFieldChip
                                         ?? TagSortGroupFieldStrip.SelectedItem as TagSortFieldChip;
                if (chip == null)
                {
                    return;
                }

                int index = _groupFieldChips.IndexOf(chip);
                if (index < 0)
                {
                    return;
                }

                MenuFlyout menu = new();
                MenuFlyoutItem up = new() { Text = "前移", IsEnabled = index > 0 };
                up.Click += (_, _) => MoveGroupFieldChip(index, -1);
                MenuFlyoutItem down = new() { Text = "后移", IsEnabled = index < _groupFieldChips.Count - 1 };
                down.Click += (_, _) => MoveGroupFieldChip(index, 1);
                MenuFlyoutItem remove = new() { Text = "删除", IsEnabled = _groupFieldChips.Count > 1 };
                remove.Click += (_, _) =>
                {
                    if (index >= 0 && index < _groupFieldChips.Count && _groupFieldChips.Count > 1)
                    {
                        _groupFieldChips.RemoveAt(index);
                        CommitGroupFieldsFromStrip();
                    }
                };

                menu.Items.Add(up);
                menu.Items.Add(down);
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(remove);
                menu.ShowAt(TagSortGroupFieldStrip, e.GetPosition(TagSortGroupFieldStrip));
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TagSortGroupFieldStrip_RightTapped", ex);
            }
        }

        private void MoveGroupFieldChip(int index, int delta)
        {
            int target = index + delta;
            if (index < 0 || target < 0 || index >= _groupFieldChips.Count || target >= _groupFieldChips.Count)
            {
                return;
            }

            // ObservableCollection 没有 Move，用「取出再插入」表达同一个结果
            TagSortFieldChip chip = _groupFieldChips[index];
            _groupFieldChips.RemoveAt(index);
            _groupFieldChips.Insert(target, chip);
            CommitGroupFieldsFromStrip();
        }

        private void TagSortGroupFieldAddButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MenuFlyout menu = new();
                foreach (TagSortFields.FieldDef def in TagSortFields.All)
                {
                    if (_groupFieldChips.Any(c => string.Equals(c.Key, def.Key, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    MenuFlyoutItem item = new() { Text = def.Label };
                    string key = def.Key;
                    item.Click += (_, _) =>
                    {
                        _groupFieldChips.Add(new TagSortFieldChip { Key = key, Name = def.Label });
                        CommitGroupFieldsFromStrip();
                    };
                    menu.Items.Add(item);
                }

                if (menu.Items.Count == 0)
                {
                    menu.Items.Add(new MenuFlyoutItem { Text = "全部字段都已添加", IsEnabled = false });
                }

                menu.ShowAt(TagSortGroupFieldAddButton);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TagSortGroupFieldAddButton_Click", ex);
            }
        }

        // ---------- 分类字段按钮组：同样的内联改法（阶段 1-b） ----------
        // 顶部「分类」按钮组过去靠「＋」弹独立窗口配置；现在就地编辑：
        // 拖动按钮改顺序、右键（前移/后移/删除）、「＋」直接下拉添加。落盘出口与旧窗口一致。

        private bool _categoryDropWired;

        /// <summary>给分类字段按钮挂上拖动与右键（在 BuildTagSortFieldButtons 里逐个调用）。</summary>
        private void ConfigureCategoryFieldButton(Button button, string key)
        {
            try
            {
                button.CanDrag = true;
                button.DragStarting += (_, e) =>
                {
                    e.Data.SetText(key);
                    e.Data.RequestedOperation = DataPackageOperation.Move;
                };
                button.RightTapped += (_, e) => ShowCategoryFieldMenu(button, key);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ConfigureCategoryFieldButton", ex);
            }
        }

        /// <summary>把按钮组本身变成放置目标。整个面板只挂一次，重建按钮时不用重复挂。</summary>
        private void EnsureCategoryFieldDropWired()
        {
            if (_categoryDropWired || TagSortFieldButtonsPanel == null)
            {
                return;
            }

            _categoryDropWired = true;
            TagSortFieldButtonsPanel.AllowDrop = true;

            TagSortFieldButtonsPanel.DragOver += (_, e) =>
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                if (e.DragUIOverride != null)
                {
                    e.DragUIOverride.IsCaptionVisible = false;
                }
            };

            TagSortFieldButtonsPanel.Drop += async (_, e) =>
            {
                try
                {
                    if (e.DataView == null)
                    {
                        return;
                    }

                    string key = await e.DataView.GetTextAsync();
                    if (string.IsNullOrEmpty(key))
                    {
                        return;
                    }

                    MoveCategoryField(key, ResolveCategoryDropIndex(e.GetPosition(TagSortFieldButtonsPanel)));
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("CategoryFieldDrop", ex);
                }
            };
        }

        /// <summary>按指针位置算插入下标：越过多少个按钮的中心，就插到第几位。</summary>
        private int ResolveCategoryDropIndex(Point point)
        {
            int index = 0;
            foreach (UIElement child in TagSortFieldButtonsPanel.Children)
            {
                if (child is not Button button || button.Tag is not string tag || tag == "__config__")
                {
                    continue;
                }

                Point topLeft = button.TransformToVisual(TagSortFieldButtonsPanel).TransformPoint(new Point(0, 0));
                if (point.X > topLeft.X + (button.ActualWidth / 2))
                {
                    index++;
                }
            }

            return index;
        }

        private void MoveCategoryField(string key, int insertAt)
        {
            List<string> list = _tagSortCategoryFields.ToList();
            int from = list.IndexOf(key);
            if (from < 0)
            {
                return;
            }

            list.RemoveAt(from);
            if (insertAt > from)
            {
                insertAt--;
            }

            insertAt = Math.Clamp(insertAt, 0, list.Count);
            list.Insert(insertAt, key);

            // 顺序没变就别重建（拖动时经常原地放下）
            if (list.SequenceEqual(_tagSortCategoryFields))
            {
                return;
            }

            CommitCategoryFields(list);
        }

        private void ShowCategoryFieldMenu(Button button, string key)
        {
            try
            {
                int index = _tagSortCategoryFields.IndexOf(key);
                if (index < 0)
                {
                    return;
                }

                MenuFlyout menu = new();
                MenuFlyoutItem up = new() { Text = "前移", IsEnabled = index > 0 };
                up.Click += (_, _) => MoveCategoryField(key, index - 1);
                MenuFlyoutItem down = new() { Text = "后移", IsEnabled = index < _tagSortCategoryFields.Count - 1 };
                down.Click += (_, _) => MoveCategoryField(key, index + 2);
                MenuFlyoutItem remove = new() { Text = "删除", IsEnabled = _tagSortCategoryFields.Count > 1 };
                remove.Click += (_, _) =>
                {
                    if (_tagSortCategoryFields.Count <= 1)
                    {
                        return;
                    }

                    List<string> list = _tagSortCategoryFields.ToList();
                    list.Remove(key);
                    CommitCategoryFields(list);
                };

                menu.Items.Add(up);
                menu.Items.Add(down);
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(remove);
                menu.ShowAt(button);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ShowCategoryFieldMenu", ex);
            }
        }

        /// <summary>「＋」不再弹窗口，直接下拉列出尚未添加的字段。</summary>
        private void ShowCategoryFieldAddMenu(Button plus)
        {
            try
            {
                MenuFlyout menu = new();
                foreach (TagSortFields.FieldDef def in TagSortFields.All)
                {
                    if (_tagSortCategoryFields.Contains(def.Key))
                    {
                        continue;
                    }

                    MenuFlyoutItem item = new() { Text = def.Label };
                    string key = def.Key;
                    item.Click += (_, _) =>
                    {
                        List<string> list = _tagSortCategoryFields.ToList();
                        list.Add(key);
                        CommitCategoryFields(list);
                    };
                    menu.Items.Add(item);
                }

                if (menu.Items.Count == 0)
                {
                    menu.Items.Add(new MenuFlyoutItem { Text = "全部字段都已添加", IsEnabled = false });
                }

                menu.ShowAt(plus);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ShowCategoryFieldAddMenu", ex);
            }
        }

        /// <summary>分类字段改动的统一出口：与旧「分类字段配置」窗口确认后完全同款。</summary>
        private void CommitCategoryFields(List<string> fields)
        {
            try
            {
                if (fields.Count == 0)
                {
                    return;
                }

                _tagSortCategoryFields = fields;
                if (!fields.Contains(_tagSortClassField))
                {
                    _tagSortClassField = fields[0];
                }

                AppSettingsStore.Update(s => s.TagSortCategoryFields = fields.ToList());
                BuildTagSortFieldButtons();
                ShowTagSortClassWall();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CommitCategoryFields", ex);
            }
        }
    }
}
