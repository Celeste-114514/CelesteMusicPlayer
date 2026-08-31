using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 曲目列表列配置窗口（模块 A）：勾选显示列、调整顺序与宽度。
    /// 确认后通过 <see cref="ColumnsConfirmed"/> 回调返回新配置。
    /// </summary>
    public sealed partial class TagSortColumnConfigWindow : Window
    {
        private readonly List<CheckBox> _fieldChecks = new();     // 全字段勾选框（Tag=字段 Key）
        private readonly List<string> _visibleKeys = new();       // 当前勾选列的有序 Key
        private readonly Dictionary<string, int> _weightByKey = new(); // 每列宽度档 1/2/3
        private readonly List<Button> _upButtons = new();
        private readonly List<Button> _downButtons = new();
        private readonly Dictionary<string, ComboBox> _widthByKey = new();

        public event Action<List<ListColumnSpec>>? ColumnsConfirmed;

        public TagSortColumnConfigWindow(List<ListColumnSpec> initial)
        {
            var src = initial ?? TagSortFields.DefaultColumns();
            foreach (var c in src)
            {
                if (!_weightByKey.ContainsKey(c.Key)) _weightByKey[c.Key] = Math.Clamp(c.Weight, 1, 3);
                if (c.Visible) _visibleKeys.Add(c.Key);
            }

            InitializeComponent();
            WindowIconHelper.Apply(this);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 620));
            Title = "选择列";

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
            _widthByKey.Clear();

            for (int i = 0; i < _visibleKeys.Count; i++)
            {
                string key = _visibleKeys[i];
                var def = TagSortFields.Find(key);
                string label = def?.Label ?? key;

                var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

                var labelText = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(labelText, 0);
                row.Children.Add(labelText);

                var width = new ComboBox
                {
                    SelectedIndex = Math.Clamp(_weightByKey.TryGetValue(key, out int w) ? w - 1 : 1, 0, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                width.Items.Add("窄");
                width.Items.Add("中");
                width.Items.Add("宽");
                string wk = key;
                width.SelectionChanged += (_, _) =>
                {
                    if (width.SelectedIndex >= 0) _weightByKey[wk] = width.SelectedIndex + 1;
                };
                Grid.SetColumn(width, 1);
                row.Children.Add(width);
                _widthByKey[key] = width;

                var up = new Button { Content = "上移", Padding = new Thickness(8, 3, 8, 3), MinWidth = 56 };
                var down = new Button { Content = "下移", Padding = new Thickness(8, 3, 8, 3), MinWidth = 56 };
                Grid.SetColumn(up, 2);
                Grid.SetColumn(down, 3);
                row.Children.Add(up);
                row.Children.Add(down);

                int idx = i;
                up.Click += (_, _) => MoveColumn(idx, -1);
                down.Click += (_, _) => MoveColumn(idx, +1);

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

        private void MoveColumn(int idx, int delta)
        {
            int target = idx + delta;
            if (target < 0 || target >= _visibleKeys.Count) return;
            (_visibleKeys[idx], _visibleKeys[target]) = (_visibleKeys[target], _visibleKeys[idx]);
            RebuildOrderPanel();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var defs = TagSortFields.DefaultColumns();
            _visibleKeys.Clear();
            _weightByKey.Clear();
            foreach (var c in defs)
            {
                _visibleKeys.Add(c.Key);
                _weightByKey[c.Key] = Math.Clamp(c.Weight, 1, 3);
            }
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
            var cols = _visibleKeys
                .Select(key => new ListColumnSpec
                {
                    Key = key,
                    Weight = _weightByKey.TryGetValue(key, out int w) ? Math.Clamp(w, 1, 3) : 2,
                    Visible = true,
                })
                .ToList();
            ColumnsConfirmed?.Invoke(cols.Count > 0 ? cols : TagSortFields.DefaultColumns());
            this.Close();
        }
    }
}
