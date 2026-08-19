using System;
using System.Collections.Generic;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 自定义排序窗口（设置窗口风格）：最多 5 级，按顺序选一个音频元数据标签字段（每级可设升序/降序）。
    /// </summary>
    public sealed partial class CustomSortOrderWindow : Window
    {
        private static readonly string[] FieldLabels = { "艺术家", "专辑艺术家", "专辑", "流派", "年份", "标题", "音轨号", "碟片号" };
        private static readonly string[] FieldKeys = { "Artist", "AlbumArtist", "Album", "Genre", "Year", "Title", "Track", "Disc" };
        private const int MaxLevels = 5;

        private readonly List<ComboBox> _fieldCombos = new();
        private readonly List<ToggleButton> _ascButtons = new();
        private readonly List<ToggleButton> _descButtons = new();
        private readonly List<(string field, bool asc)> _initial;
        private readonly bool _initialAsc;

        /// <summary>用户确认排序链（最多 5 级）与整体升序/降序标识。</summary>
        public event Action<List<(string field, bool asc)>, bool>? SortConfirmed;

        public CustomSortOrderWindow(List<(string field, bool asc)> initial, bool initialAsc)
        {
            _initial = initial ?? new();
            _initialAsc = initialAsc;
            InitializeComponent();
            WindowIconHelper.Apply(this);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 560));
            Title = "自定义排序";

            BuildRows();
            GlobalAscButton.IsChecked = _initialAsc;
            GlobalDescButton.IsChecked = !_initialAsc;
        }

        private void BuildRows()
        {
            for (int i = 0; i < MaxLevels; i++)
            {
                var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

                var numPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                numPanel.Children.Add(new FontIcon { Glyph = "\uE8FD", FontSize = 14, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
                numPanel.Children.Add(new TextBlock { Text = (i + 1).ToString(), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6, FontSize = 14 });
                Grid.SetColumn(numPanel, 0);

                var combo = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var label in FieldLabels) combo.Items.Add(label);
                if (i < _initial.Count)
                {
                    int keyIdx = Array.IndexOf(FieldKeys, _initial[i].field);
                    if (keyIdx >= 0) combo.SelectedIndex = keyIdx;
                }
                _fieldCombos.Add(combo);
                Grid.SetColumn(combo, 1);

                var order = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                var asc = new ToggleButton { Content = "升序", IsChecked = true, MinWidth = 64, Padding = new Thickness(8, 3, 8, 3) };
                var desc = new ToggleButton { Content = "降序", MinWidth = 64, Padding = new Thickness(8, 3, 8, 3) };
                bool initialAscLevel = i < _initial.Count ? _initial[i].asc : true;
                if (initialAscLevel) { asc.IsChecked = true; desc.IsChecked = false; }
                else { asc.IsChecked = false; desc.IsChecked = true; }
                _ascButtons.Add(asc);
                _descButtons.Add(desc);
                order.Children.Add(asc);
                order.Children.Add(desc);
                Grid.SetColumn(order, 2);

                row.Children.Add(numPanel);
                row.Children.Add(combo);
                row.Children.Add(order);
                SortGrid.Children.Add(row);
            }

            for (int i = 0; i < MaxLevels; i++)
            {
                int idx = i;
                _ascButtons[idx].Click += (_, _) => SetLevelOrder(idx, true);
                _descButtons[idx].Click += (_, _) => SetLevelOrder(idx, false);
            }
        }

        private void SetLevelOrder(int idx, bool asc)
        {
            if (idx < 0 || idx >= _ascButtons.Count) return;
            _ascButtons[idx].IsChecked = asc;
            _descButtons[idx].IsChecked = !asc;
        }

        private void GlobalOrderChanged(object sender, RoutedEventArgs e)
        {
            // Click 事件（不会因程序化改 IsChecked 重入）：根据点击的按钮同步另一个为相反状态
            if (sender == GlobalDescButton)
            {
                GlobalAscButton.IsChecked = false;
                GlobalDescButton.IsChecked = true;
            }
            else
            {
                GlobalDescButton.IsChecked = false;
                GlobalAscButton.IsChecked = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var fields = new List<(string field, bool asc)>();
            foreach (var combo in _fieldCombos)
            {
                var key = combo.SelectedIndex >= 0 && combo.SelectedIndex < FieldKeys.Length
                    ? FieldKeys[combo.SelectedIndex] : null;
                if (key == null) continue;
                int j = _fieldCombos.IndexOf(combo);
                bool asc = _ascButtons[j].IsChecked == true;
                fields.Add((key, asc));
            }
            bool global = GlobalAscButton.IsChecked == true;
            SortConfirmed?.Invoke(fields, global);
            this.Close();
        }
    }
}
