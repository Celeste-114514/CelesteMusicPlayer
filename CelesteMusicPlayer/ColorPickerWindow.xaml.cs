using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>调色板窗口：预设色块 + WinUI ColorPicker。</summary>
    public sealed partial class ColorPickerWindow : Window
    {
        private readonly Action<string>? _onPicked;
        private string _currentHex;
        private bool _updatingUi;
        private static ColorPickerWindow? _instance;

        private static readonly string[] PresetHexes =
        {
            "#40B4FF", "#5B9BD5", "#00B7C3", "#2ECC71", "#27AE60",
            "#F1C40F", "#E67E22", "#E74C3C", "#C0392B", "#9B59B6",
            "#8E44AD", "#3498DB", "#1ABC9C", "#16A085", "#F39C12",
            "#FFFFFF", "#F5F5F5", "#E0E0E0", "#BDBDBD", "#9E9E9E",
            "#757575", "#616161", "#424242", "#212121", "#000000",
            "#FF8A80", "#FF80AB", "#EA80FC", "#B388FF", "#8C9EFF",
            "#82B1FF", "#80D8FF", "#84FFFF", "#A7FFEB", "#B9F6CA"
        };

        public ColorPickerWindow(string title, string initialHex, Action<string>? onPicked)
        {
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = string.IsNullOrWhiteSpace(title) ? "选择颜色" : title;
            TitleText.Text = Title;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.Resize(new SizeInt32(500, 660));

            _onPicked = onPicked;
            _currentHex = NormalizeHex(initialHex) ?? "#40B4FF";

            ApplyBackdrop();
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
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

            BuildPresets();
            ApplyHexToUi(_currentHex);

            Closed += (_, _) =>
            {
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            };
        }

        public static void Show(string title, string initialHex, Action<string> onPicked)
        {
            if (_instance != null)
            {
                try
                {
                    _instance.Close();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("ColorPickerWindow.xaml.cs", caught); }

                _instance = null;
            }

            var win = new ColorPickerWindow(title, initialHex, onPicked);
            _instance = win;
            win.Activate();
        }

        private void BuildPresets()
        {
            PresetPanel.Children.Clear();
            foreach (string hex in PresetHexes)
            {
                if (!TryParseHex(hex, out Color c))
                {
                    continue;
                }

                string normalized = NormalizeHex(hex) ?? hex;
                var btn = new Button
                {
                    Width = 36,
                    Height = 36,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 8, 8),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Tag = normalized,
                    Background = new SolidColorBrush(c)
                };
                ToolTipService.SetToolTip(btn, normalized);
                btn.Click += PresetSwatch_Click;
                PresetPanel.Children.Add(btn);
            }
        }

        private void ApplyBackdrop()
        {
            try
            {
                if (AppSettingsStore.Load().EnableFrostedGlass)
                {
                    FrostedGlass.ApplyWindowBackdrop(this);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("ColorPickerWindow.xaml.cs", caught); }
        }

        private void ApplyHexToUi(string hex)
        {
            _updatingUi = true;
            try
            {
                _currentHex = NormalizeHex(hex) ?? "#40B4FF";
                if (!TryParseHex(_currentHex, out Color color))
                {
                    color = Color.FromArgb(255, 64, 180, 255);
                    _currentHex = "#40B4FF";
                }

                MainColorPicker.Color = color;
                HexTextBox.Text = _currentHex;
                PreviewSwatch.Background = new SolidColorBrush(color);
            }
            finally
            {
                _updatingUi = false;
            }
        }

        private void PresetSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string hex })
            {
                ApplyHexToUi(hex);
            }
        }

        private void MainColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (_updatingUi)
            {
                return;
            }

            Color c = args.NewColor;
            _currentHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            HexTextBox.Text = _currentHex;
            PreviewSwatch.Background = new SolidColorBrush(c);
        }

        private void HexTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_updatingUi)
            {
                return;
            }

            string? normalized = NormalizeHex(HexTextBox.Text);
            if (normalized != null)
            {
                ApplyHexToUi(normalized);
            }
            else
            {
                HexTextBox.Text = _currentHex;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            _onPicked?.Invoke(_currentHex);
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
            => Close();

        internal static string? NormalizeHex(string? hex)
        {
            if (!TryParseHex(hex, out Color c))
            {
                return null;
            }

            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        internal static bool TryParseHex(string? hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex))
            {
                return false;
            }

            string h = hex.Trim();
            if (h.StartsWith('#'))
            {
                h = h[1..];
            }

            if (h.Length == 3)
            {
                h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            }

            if (h.Length != 6
                || !byte.TryParse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
                || !byte.TryParse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
                || !byte.TryParse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                return false;
            }

            color = Color.FromArgb(255, r, g, b);
            return true;
        }
    }
}
