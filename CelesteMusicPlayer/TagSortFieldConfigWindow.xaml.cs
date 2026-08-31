using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 分类字段配置窗口（模块 B）：勾选出现在分类按钮组的字段并调整顺序。
    /// 确认后通过 <see cref="FieldsConfirmed"/> 回调返回有序字段 Key 列表。
    /// </summary>
    public sealed partial class TagSortFieldConfigWindow : Window
    {
        private readonly List<CheckBox> _fieldChecks = new();   // Tag=字段 Key
        private readonly List<string> _visibleKeys = new();
        private readonly List<Button> _upButtons = new();
        private readonly List<Button> _downButtons = new();

        public event Action<List<string>>? FieldsConfirmed;

        public TagSortFieldConfigWindow(List<string> initial)
        {
            var src = initial is { Count: > 0 } ? initial : TagSortFields.DefaultCategoryFields.ToList();
            _visibleKeys.AddRange(src);

            InitializeComponent();
            WindowIconHelper.Apply(this);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 600));
            Title = "分类字段配置";

            BuildFieldChecks();
            RebuildOrderPanel();
        }

        private void BuildFieldChecks()
        {
            foreach (var def in TagSortFields.All)
            {
                var check = new CheckBox
                {
                    Content = def.Label,
                    Tag = def.Key,
                    IsChecked = _visibleKeys.Contains(def.Key),
                    MinWidth = 150,
                    Margin = new Thickness(0, 1, 0, 1),
                    FontSize = 13,
                };
                check.Checked += (_, _) => { OnCheckedChanged(def.Key, true); };
                check.Unchecked += (_, _) => { OnCheckedChanged(def.Key, false); };
                _fieldChecks.Add(check);
                (def.Tech ? TechChecks : MetaChecks).Children.Add(check);
            }
        }

        private void OnCheckedChanged(string key, bool visible)
        {
            if (visible)
            {
                if (!_visibleKeys.Contains(key)) _visibleKeys.Add(key);
            }
            else
            {
                _visibleKeys.Remove(key);
            }
            RebuildOrderPanel();
        }

        private void RebuildOrderPanel()
        {
            OrderPanel.Children.Clear();
            _upButtons.Clear();
            _downButtons.Clear();

            for (int i = 0; i < _visibleKeys.Count; i++)
            {
                string key = _visibleKeys[i];
                var def = TagSortFields.Find(key);
                string label = def?.Label ?? key;

                var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

                var labelText = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                };
                Grid.SetColumn(labelText, 0);
                row.Children.Add(labelText);

                var up = new Button { Content = "上移", Padding = new Thickness(8, 3, 8, 3), MinWidth = 56 };
                var down = new Button { Content = "下移", Padding = new Thickness(8, 3, 8, 3), MinWidth = 56 };
                Grid.SetColumn(up, 1);
                Grid.SetColumn(down, 2);
                row.Children.Add(up);
                row.Children.Add(down);

                int idx = i;
                up.Click += (_, _) => MoveField(idx, -1);
                down.Click += (_, _) => MoveField(idx, +1);

                OrderPanel.Children.Add(row);
                _upButtons.Add(up);
                _downButtons.Add(down);
            }

            for (int i = 0; i < _upButtons.Count; i++)
            {
                _upButtons[i].IsEnabled = i > 0;
                _downButtons[i].IsEnabled = i < _upButtons.Count - 1;
            }
        }

        private void MoveField(int idx, int delta)
        {
            int target = idx + delta;
            if (target < 0 || target >= _visibleKeys.Count) return;
            (_visibleKeys[idx], _visibleKeys[target]) = (_visibleKeys[target], _visibleKeys[idx]);
            RebuildOrderPanel();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _visibleKeys.Clear();
            _visibleKeys.AddRange(TagSortFields.DefaultCategoryFields);
            foreach (var check in _fieldChecks)
            {
                check.IsChecked = _visibleKeys.Contains(check.Tag as string);
            }
            RebuildOrderPanel();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            FieldsConfirmed?.Invoke(_visibleKeys.Count > 0
                ? _visibleKeys.ToList()
                : TagSortFields.DefaultCategoryFields.ToList());
            this.Close();
        }
    }
}
