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
            Title = "分类字段配置";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(820, 660));
            ConfigureTitleBarButtons();
            ApplyBackdropFromSettings();

            BuildFieldChecks();
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

        private void BuildFieldChecks()
        {
            foreach (var def in TagSortFields.All)
            {
                bool isHighCardinality = def.Cardinality == TagSortFields.Cardinality.High;
                // 高基数字段：默认不勾选（避免误用爆内存）；用户可手动勾选。
                bool initialChecked = !isHighCardinality && _visibleKeys.Contains(def.Key);

                var check = new CheckBox
                {
                    Content = def.Label + (isHighCardinality ? "  ⚠" : string.Empty),
                    Tag = def.Key,
                    IsChecked = initialChecked,
                    MinWidth = 150,
                    Margin = new Thickness(0, 1, 0, 1),
                    FontSize = 13,
                    Opacity = isHighCardinality ? 0.6 : 1.0,
                };
                check.Checked += (_, _) => { OnCheckedChanged(def.Key, true); };
                check.Unchecked += (_, _) => { OnCheckedChanged(def.Key, false); };
                _fieldChecks.Add(check);
                (def.Tech ? TechChecks : MetaChecks).Children.Add(check);
            }

            // 在底部追加一条高基数提示，避免用户疑惑为什么标题/文件名"勾不动"。
            var hint = new TextBlock
            {
                Text = "⚠ 标记的字段基数过高（如每首歌的标题/文件名都不同），用于分类墙会产生海量卡片并耗尽内存。" +
                       "此类字段请到「分组浏览」（列表内按字段分组、组头可折叠）使用。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
            };
            MetaChecks.Children.Add(hint);
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
