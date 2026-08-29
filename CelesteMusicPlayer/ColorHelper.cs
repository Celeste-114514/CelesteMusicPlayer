using System;
using System.Collections.Generic;
using Microsoft.UI; // Colors（WinUI3 的 Colors 在 Microsoft.UI，不在 Windows.UI）
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>颜色与画刷工具：从 MainWindow 里抽出的无状态静态方法。
    /// 这些方法原本是 MainWindow 的 private static，编译器已保证它们不碰实例状态，
    /// 搬到这里只是换了个归属，行为不变。
    /// </summary>
    internal static class ColorHelper
    {
        public static int MakeColorRef(Color c)
        {
            // Windows COLORREF = 0x00 BB GG RR
            return unchecked((int)((uint)c.B | ((uint)c.G << 8) | ((uint)c.R << 16)));
        }

        public static bool IsNearWhite(Color color)
        {
            return color.R >= 220 && color.G >= 220 && color.B >= 220;
        }

        public static bool TryGetBrushColor(Brush? brush, out Color color)
        {
            if (brush is SolidColorBrush solid)
            {
                color = solid.Color;
                return true;
            }

            if (brush is AcrylicBrush acrylic)
            {
                color = acrylic.TintColor;
                return true;
            }

            color = default;
            return false;
        }

        public static bool TryGetThemeColor(FrameworkElement? element, string key, out Color color)
        {
            color = default;
            try
            {
                object? value = null;
                if (element != null && element.Resources.TryGetValue(key, out object local))
                {
                    value = local;
                }
                else if (Application.Current.Resources.TryGetValue(key, out object app))
                {
                    value = app;
                }

                if (value is Color c)
                {
                    color = c;
                    return true;
                }

                if (value is SolidColorBrush solid)
                {
                    color = solid.Color;
                    return true;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            return false;
        }

        /// <summary>给 ContentDialog 的按钮设置当前主题色（局部资源覆盖，不改全局、避免运行时覆盖 Application.Resources 崩溃）。</summary>
        public static void ApplyDialogAccent(Microsoft.UI.Xaml.Controls.ContentDialog dlg)
        {
            try
            {
                Windows.UI.Color accent = ThemeColorService.CurrentAccent;
                var accentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent);
                Windows.UI.Color pressed = Windows.UI.Color.FromArgb(255, (byte)(accent.R * 0.7), (byte)(accent.G * 0.7), (byte)(accent.B * 0.7));
                Windows.UI.Color disabled = Windows.UI.Color.FromArgb(255, (byte)(accent.R * 0.3), (byte)(accent.G * 0.3), (byte)(accent.B * 0.3));
                dlg.Resources["AccentButtonBackground"] = accentBrush;
                dlg.Resources["AccentButtonBackgroundPointerOver"] = accentBrush;
                dlg.Resources["AccentButtonBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(pressed);
                dlg.Resources["AccentButtonBackgroundDisabled"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(disabled);
                dlg.Resources["AccentButtonForeground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }

        /// <summary>解析 "#RRGGBB" 十六进制颜色。</summary>
        public static Color? ParseHexColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return null;
            }

            string h = hex.Trim().TrimStart('#');
            if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int value))
            {
                return null;
            }

            return Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }

        /// <summary>主题色偏深用白字，偏浅用黑字。</summary>
        public static Brush ResolveContrastingForeground(Brush background)
        {
            Color color = Colors.DodgerBlue;
            if (background is SolidColorBrush solid)
            {
                color = solid.Color;
            }
            else if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object? colorObj)
                && colorObj is Color accent)
            {
                color = accent;
            }

            double luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
            return new SolidColorBrush(luminance < 140 ? Colors.White : Colors.Black);
        }
    }
}