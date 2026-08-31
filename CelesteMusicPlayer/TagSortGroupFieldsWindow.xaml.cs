using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 自定义分组字段窗口（模块 C 阶段 2）：左侧列出全部可用字段，点击加入右侧；
    /// 右侧为自上而下嵌套的分组层级，可上移/下移/移除。确认后通过
    /// <see cref="FieldsConfirmed"/> 回调返回有序字段 Key 列表，供分组浏览重建嵌套树。
    /// 注意：分组浏览允许高基数字段（标题/文件名等）——它们正是分组浏览的主要用途，
    /// 与分类墙（会爆内存）不同，故这里不禁用高基数字段。
    /// </summary>
    public sealed partial class TagSortGroupFieldsWindow : Window
    {
        private readonly List<string> _selected = new();

        public event Action<List<string>>? FieldsConfirmed;

        public TagSortGroupFieldsWindow(List<string> initial)
        {
            var src = initial is { Count: > 0 } ? initial : new List<string> { "Artist", "Album" };
            _selected.AddRange(src);

            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "自定义分组字段";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(940, 620));
            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();

            BuildAvailable();
            RebuildOrderPanel();
        }

        private void ConfigureTitleBarButtons()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }

        private void ApplyBackdropFromSettings()
        {
            AppSettingsState s = AppSettingsStore.Load();
            if (s.EnableFrostedGlass)
            {
                FrostedGlass.ApplyWindowBackdrop(this);
            }
            else
            {
                SystemBackdrop = null;
            }
        }

        private void BuildAvailable()
        {
            AvailablePanel.Children.Clear();
            foreach (var def in TagSortFields.All)
            {
                bool added = _selected.Contains(def.Key);
                var btn = new Button
                {
                    Content = (added ? "✓ " : "+ ") + def.Label,
                    Tag = def.Key,
                    IsEnabled = !added,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 1, 0, 1),
                    FontSize = 13,
                };
                btn.Click += (_, _) => AddField(def.Key);
                AvailablePanel.Children.Add(btn);
            }
        }

        private void AddField(string key)
        {
            if (_selected.Contains(key)) return;
            _selected.Add(key);
            BuildAvailable();
            RebuildOrderPanel();
        }

        private void RebuildOrderPanel()
        {
            OrderPanel.Children.Clear();
            for (int i = 0; i < _selected.Count; i++)
            {
                string key = _selected[i];
                var def = TagSortFields.Find(key);
                string label = def?.Label ?? key;

                var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

                var labelText = new TextBlock
                {
                    Text = $"{i + 1}. {label}",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                };
                Grid.SetColumn(labelText, 0);
                row.Children.Add(labelText);

                var up = new Button { Content = "上移", Padding = new Thickness(6, 3, 6, 3), MinWidth = 48 };
                var down = new Button { Content = "下移", Padding = new Thickness(6, 3, 6, 3), MinWidth = 48 };
                var remove = new Button { Content = "移除", Padding = new Thickness(6, 3, 6, 3), MinWidth = 48 };
                Grid.SetColumn(up, 1);
                Grid.SetColumn(down, 2);
                Grid.SetColumn(remove, 3);
                row.Children.Add(up);
                row.Children.Add(down);
                row.Children.Add(remove);

                int idx = i;
                up.Click += (_, _) => MoveField(idx, -1);
                down.Click += (_, _) => MoveField(idx, +1);
                remove.Click += (_, _) => RemoveField(idx);

                OrderPanel.Children.Add(row);

                up.IsEnabled = i > 0;
                down.IsEnabled = i < _selected.Count - 1;
            }
        }

        private void MoveField(int idx, int delta)
        {
            int target = idx + delta;
            if (target < 0 || target >= _selected.Count) return;
            (_selected[idx], _selected[target]) = (_selected[target], _selected[idx]);
            RebuildOrderPanel();
        }

        private void RemoveField(int idx)
        {
            if (idx < 0 || idx >= _selected.Count) return;
            _selected.RemoveAt(idx);
            BuildAvailable();
            RebuildOrderPanel();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _selected.Clear();
            _selected.AddRange(new[] { "Artist", "Album" });
            BuildAvailable();
            RebuildOrderPanel();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var result = _selected.Count > 0 ? _selected.ToList() : new List<string> { "Artist", "Album" };
            FieldsConfirmed?.Invoke(result);
            this.Close();
        }
    }
}
