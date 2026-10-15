using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 就地弹出的取色浮层（2026-10-12 用户要求）：点设置项里的「自定义 / 调色板」时，
    /// 在**该选项旁边**展开一个浮层展示取色器，而不是弹一个独立窗口。
    ///
    /// 关键性质：浮层是「盖」在界面上的（Flyout 自带这层语义），
    /// **不会把下面的设置文字往下推** —— 这正是用户特意强调的一点
    /// （旧的极客磷光色取色器是往列表里插一行，会把后面的选项全部顶下去）。
    ///
    /// 用法：
    /// <code>
    /// InlineColorPicker.Show(anchorButton, "描边颜色", currentHex,
    ///     onLive: null,                 // 拖动时不实时应用
    ///     onCommit: hex => { ... });    // 点「确定」才应用
    /// </code>
    /// </summary>
    internal static class InlineColorPicker
    {
        /// <summary>在 <paramref name="anchor"/> 旁展开取色浮层。</summary>
        /// <param name="anchor">锚点控件（浮层贴着它展开，通常是被点的那个按钮 / 下拉框）。</param>
        /// <param name="title">浮层标题。</param>
        /// <param name="initialHex">初始颜色 #RRGGBB。</param>
        /// <param name="onLive">拖动取色过程中的实时回调（可为 null = 不实时应用）。</param>
        /// <param name="onCommit">点「确定」时的回调（最终色值）。</param>
        /// <param name="onCancel">点「取消」或点外面关闭时的回调（实时模式下用它回滚，可为 null）。</param>
        /// <param name="withAlpha">是否显示透明度滑杆（默认关，与旧调色板窗口一致）。</param>
        /// <param name="initialAlpha">初始透明度 0–255（仅 withAlpha 时有效）。</param>
        public static void Show(
            FrameworkElement anchor,
            string title,
            string initialHex,
            Action<string>? onLive,
            Action<string>? onCommit,
            Action<string>? onCancel = null,
            bool withAlpha = false,
            byte initialAlpha = 255)
        {
            if (anchor == null)
            {
                return;
            }

            bool updatingUi = false;
            string currentHex = ColorPickerWindow.NormalizeHex(initialHex) ?? "#40B4FF";
            byte currentAlpha = initialAlpha;
            bool committed = false;

            // ── 内容（声明顺序有讲究：后面 lambda 里要用的控件必须先声明）────────────
            var root = new StackPanel { Spacing = 10, Width = 328 };

            root.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            // 预设色块：与旧调色板窗口同一套，老用户不用重新适应
            var presetGrid = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                MaximumRowsOrColumns = 7,
                ItemWidth = 32,
                ItemHeight = 32
            };
            root.Children.Add(presetGrid);

            // 预览色块 + 十六进制输入
            var previewRow = new Grid { ColumnSpacing = 10 };
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var preview = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            Brush? strokeBrush = FindBrush("CardStrokeColorDefaultBrush");
            if (strokeBrush != null)
            {
                preview.BorderBrush = strokeBrush;
            }

            previewRow.Children.Add(preview);

            var hexBox = new TextBox { PlaceholderText = "#RRGGBB", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(hexBox, 1);
            previewRow.Children.Add(hexBox);

            var picker = new ColorPicker
            {
                ColorSpectrumShape = ColorSpectrumShape.Box,
                IsColorChannelTextInputVisible = true,
                IsColorSliderVisible = true,
                IsHexInputVisible = true,
                IsAlphaEnabled = withAlpha,
                IsAlphaSliderVisible = withAlpha,
                IsAlphaTextInputVisible = withAlpha,
                MinWidth = 300
            };

            root.Children.Add(picker);
            root.Children.Add(previewRow);

            // 确定 / 取消
            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancelButton = new Button { Content = "取消", MinWidth = 84, CornerRadius = new CornerRadius(16) };
            var okButton = new Button { Content = "确定", MinWidth = 84, CornerRadius = new CornerRadius(16) };
            Style? accentStyle = FindStyle("ButtonNormalStyle");
            if (accentStyle != null)
            {
                okButton.Style = accentStyle;
            }

            buttonRow.Children.Add(cancelButton);
            buttonRow.Children.Add(okButton);
            root.Children.Add(buttonRow);

            // ── 浮层外壳 ────────────────────────────────────────────
            // Flyout 本身就是「盖在界面上」的层：展开 / 收起都不参与宿主面板的布局，
            // 所以下面的设置文字不会被推走 —— 这正是用户要的效果。
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };

            var presenterStyle = new Style(typeof(FlyoutPresenter));
            presenterStyle.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
            presenterStyle.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, 0.0));
            presenterStyle.Setters.Add(new Setter(FlyoutPresenter.MinHeightProperty, 0.0));
            presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
            flyout.FlyoutPresenterStyle = presenterStyle;

            var shell = new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Child = root
            };
            if (strokeBrush != null)
            {
                shell.BorderBrush = strokeBrush;
            }

            Brush? cardBrush = FindBrush("CardBackgroundFillColorDefaultBrush");
            if (cardBrush != null)
            {
                shell.Background = cardBrush;
            }

            flyout.Content = shell;

            // 预设色块（放在这里建：ApplyHex 已经就绪）
            foreach (string hex in ColorPickerWindow.GetPresetHexes())
            {
                if (!ColorPickerWindow.TryParseHex(hex, out Color pc))
                {
                    continue;
                }

                string normalized = ColorPickerWindow.NormalizeHex(hex) ?? hex;
                var swatch = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 4, 4),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Tag = normalized,
                    Background = new SolidColorBrush(pc)
                };
                ToolTipService.SetToolTip(swatch, normalized);
                swatch.Click += (_, _) => ApplyHex(normalized, currentAlpha, fromUser: true);
                presetGrid.Children.Add(swatch);
            }

            picker.ColorChanged += (_, args) =>
            {
                if (updatingUi)
                {
                    return;
                }

                Color c = args.NewColor;
                currentHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                currentAlpha = c.A;
                hexBox.Text = currentHex;
                preview.Background = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
                onLive?.Invoke(currentHex);
            };

            hexBox.LostFocus += (_, _) =>
            {
                if (updatingUi)
                {
                    return;
                }

                string? normalized = ColorPickerWindow.NormalizeHex(hexBox.Text);
                if (normalized == null)
                {
                    hexBox.Text = currentHex;
                    return;
                }

                ApplyHex(normalized, currentAlpha, fromUser: true);
            };

            okButton.Click += (_, _) =>
            {
                committed = true;
                flyout.Hide();
                onCommit?.Invoke(currentHex);
            };

            cancelButton.Click += (_, _) => flyout.Hide();

            flyout.Closed += (_, _) =>
            {
                // 取消 / 点外面关闭：实时模式下把界面回滚到打开前的颜色
                if (!committed)
                {
                    onCancel?.Invoke(initialHex);
                }
            };

            ApplyHex(currentHex, currentAlpha, fromUser: false);

            FlyoutBase.SetAttachedFlyout(anchor, flyout);
            FlyoutBase.ShowAttachedFlyout(anchor);

            void ApplyHex(string hex, byte alpha, bool fromUser)
            {
                updatingUi = true;
                try
                {
                    if (!ColorPickerWindow.TryParseHex(hex, out Color c))
                    {
                        return;
                    }

                    currentHex = ColorPickerWindow.NormalizeHex(hex) ?? currentHex;
                    currentAlpha = alpha;
                    picker.Color = Color.FromArgb(withAlpha ? alpha : (byte)255, c.R, c.G, c.B);
                    hexBox.Text = currentHex;
                    preview.Background = new SolidColorBrush(c);

                    if (fromUser)
                    {
                        onLive?.Invoke(currentHex);
                    }
                }
                finally
                {
                    updatingUi = false;
                }
            }
        }

        private static Brush? FindBrush(string key)
        {
            try
            {
                return Application.Current.Resources[key] as Brush;
            }
            catch
            {
                return null;
            }
        }

        private static Style? FindStyle(string key)
        {
            try
            {
                return Application.Current.Resources[key] as Style;
            }
            catch
            {
                return null;
            }
        }
    }
}
