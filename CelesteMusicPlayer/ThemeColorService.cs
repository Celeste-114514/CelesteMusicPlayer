using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 主题色服务：在窗口创建前(App.OnLaunched)把自定义强调色覆盖到系统资源键，
    /// 使按钮/滑块/选中高亮等默认控件全部跟随；跟随系统时恢复默认。
    /// 注意：只能在窗口创建前调用——窗口渲染后修改 Application.Resources 的
    /// 系统键会触发 WinUI 原生崩溃(0xc000027b)，托管 try-catch 无法捕获。
    /// </summary>
    internal static class ThemeColorService
    {
        private static Color _currentAccent = Color.FromArgb(255, 0, 120, 212);

        /// <summary>当前生效的主题色(自定义色或系统强调色)。</summary>
        public static Color CurrentAccent => _currentAccent;

        /// <summary>主题色变化时触发(保存设置后广播,各窗口订阅刷新)。</summary>
        public static event Action<Color>? ThemeColorChanged;

        /// <summary>从设置更新当前主题色并广播事件(不改全局资源,安全)。</summary>
        public static void UpdateAccentFromSettings(AppSettingsState settings)
        {
            Color accent;
            if (settings.AccentSource == "Custom")
            {
                accent = ParseHexColor(settings.CustomAccentColor) ?? Color.FromArgb(255, 0, 120, 212);
            }
            else
            {
                accent = ResolveSystemAccent();
            }

            _currentAccent = accent;
            ThemeColorChanged?.Invoke(accent);
        }

        /// <summary>给 Slider 应用主题色:设置控件级局部资源并重建模板(确保立即生效)。</summary>
        public static void ApplySliderAccent(Microsoft.UI.Xaml.Controls.Primitives.RangeBase? sliderBase, Windows.UI.Color accent)
        {
            if (sliderBase is not Microsoft.UI.Xaml.Controls.Slider slider)
            {
                return;
            }

            try
            {
                var accentBrush = new SolidColorBrush(accent);
                var trackBrush = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B));
                slider.Resources["SliderTrackValueFill"] = accentBrush;
                slider.Resources["SliderTrackValueFillPointerOver"] = accentBrush;
                slider.Resources["SliderTrackValueFillPressed"] = accentBrush;
                slider.Resources["SliderThumbBackground"] = accentBrush;
                slider.Resources["SliderThumbBackgroundPointerOver"] = accentBrush;
                slider.Resources["SliderThumbBackgroundPressed"] = new SolidColorBrush(Darken(accent, 0.35));
                slider.Resources["SliderThumbBorderBrush"] = new SolidColorBrush(Colors.Transparent);
                slider.Resources["SliderThumbBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
                slider.Resources["SliderTrackFill"] = trackBrush;
                slider.Resources["SliderTrackFillPointerOver"] = trackBrush;

                // 重建模板:让局部资源立即生效(先保存模板,再清空,再恢复)
                Microsoft.UI.Xaml.Controls.ControlTemplate tpl = slider.Template;
                slider.SetValue(Microsoft.UI.Xaml.Controls.Control.TemplateProperty, null);
                slider.Template = tpl;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("ThemeColorService.cs", caught); }
        }

        private static Color Darken(Color c, double t)
            => Color.FromArgb(255, (byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

        /// <summary>解析系统强调色(从已覆盖的应用资源或系统资源读取)。</summary>
        private static Color ResolveSystemAccent()
        {
            try
            {
                var res = Application.Current.Resources;
                if (res.TryGetValue("SystemAccentColor", out object? c) && c is Color col)
                {
                    return col;
                }

                if (res.TryGetValue("AccentFillColorDefaultBrush", out object? b) && b is SolidColorBrush scb)
                {
                    return scb.Color;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("ThemeColorService.cs", caught); }

            return Color.FromArgb(255, 0, 120, 212);
        }

        public static void ApplyThemeResources(AppSettingsState settings)
        {
            try
            {
                UpdateAccentFromSettings(settings);
                StartupLog.Write("主题色应用: " + (settings.AccentSource == "Custom" ? settings.CustomAccentColor : "跟随系统"));
                if (settings.AccentSource == "Custom")
                {
                    Color accent = ParseHexColor(settings.CustomAccentColor) ?? Color.FromArgb(255, 0, 120, 212);
                    OverrideSystemAccentResources(accent);
                }
                else
                {
                    RestoreSystemAccentResources();
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("ThemeColorService.cs", caught); }
        }

        /// <summary>解析 "#RRGGBB" 十六进制颜色。</summary>
        public static Color? ParseHexColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return null;
            }

            string h = hex.Trim().TrimStart('#');
            if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int value))
            {
                return null;
            }

            return Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }

        /// <summary>用自定义颜色覆盖系统强调色资源键。</summary>
        private static void OverrideSystemAccentResources(Color accent)
        {
            var res = Application.Current.Resources;
            Color light1 = MixWithWhite(accent, 0.72);
            Color light2 = MixWithWhite(accent, 0.85);
            Color light3 = MixWithWhite(accent, 0.93);
            Color dark1 = MixWithBlack(accent, 0.40);
            Color dark2 = MixWithBlack(accent, 0.70);
            Color dark3 = MixWithBlack(accent, 0.85);

            res["SystemAccentColor"] = accent;
            res["SystemAccentColorLight1"] = light1;
            res["SystemAccentColorLight2"] = light2;
            res["SystemAccentColorLight3"] = light3;
            res["SystemAccentColorDark1"] = dark1;
            res["SystemAccentColorDark2"] = dark2;
            res["SystemAccentColorDark3"] = dark3;

            var accentBrush = new SolidColorBrush(accent);
            var light1Brush = new SolidColorBrush(light1);
            var light2Brush = new SolidColorBrush(light2);
            var dark1Brush = new SolidColorBrush(dark1);
            var dark2Brush = new SolidColorBrush(dark2);

            res["AccentFillColorDefaultBrush"] = accentBrush;
            res["AccentFillColorSecondaryBrush"] = light1Brush;
            res["AccentFillColorTertiaryBrush"] = light2Brush;
            res["AccentFillColorDisabledBrush"] = new SolidColorBrush(Color.FromArgb(102, accent.R, accent.G, accent.B));
            res["AccentFillColorSelectedTextBackgroundBrush"] = accentBrush;

            res["AccentButtonBackground"] = accentBrush;
            res["AccentButtonBackgroundPointerOver"] = light1Brush;
            res["AccentButtonBackgroundPressed"] = dark1Brush;
            res["AccentButtonBackgroundDisabled"] = dark2Brush;
            res["AccentButtonForeground"] = new SolidColorBrush(Colors.White);
            res["AccentButtonForegroundPointerOver"] = new SolidColorBrush(Colors.White);
            res["AccentButtonForegroundPressed"] = new SolidColorBrush(Colors.White);
            res["AccentButtonForegroundDisabled"] = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255));

            // —— Slider(进度条/音量条)：纯色主题色轨道 + 实心圆点(无描边避免残缺) ——
            res["SliderTrackValueFill"] = accentBrush;
            res["SliderTrackValueFillPointerOver"] = light1Brush;
            res["SliderTrackValueFillPressed"] = dark1Brush;
            res["SliderTrackValueFillDisabled"] = dark2Brush;
            res["SliderTrackFill"] = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B));
            res["SliderTrackFillPointerOver"] = new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B));
            res["SliderTrackFillDisabled"] = new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B));
            res["SliderThumbBackground"] = accentBrush;
            res["SliderThumbBackgroundPointerOver"] = light1Brush;
            res["SliderThumbBackgroundPressed"] = dark1Brush;
            res["SliderThumbBackgroundDisabled"] = dark2Brush;
            res["SliderThumbBorderBrush"] = new SolidColorBrush(Colors.Transparent);
            res["SliderThumbBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
            res["SliderThumbBorderBrushPressed"] = new SolidColorBrush(Colors.Transparent);
            res["SliderThumbBorderBrushDisabled"] = new SolidColorBrush(Colors.Transparent);

            // —— ToggleSwitch / CheckBox / RadioButton ——
            res["ToggleSwitchFillOn"] = accentBrush;
            res["ToggleSwitchFillOnPointerOver"] = light1Brush;
            res["ToggleSwitchFillOnPressed"] = dark1Brush;
            res["ToggleSwitchFillOnDisabled"] = dark2Brush;
            res["CheckBoxCheckBackgroundStroke"] = accentBrush;
            res["CheckBoxCheckBackgroundStrokePointerOver"] = light1Brush;
            res["CheckBoxCheckBackgroundStrokePressed"] = dark1Brush;
            res["CheckBoxCheckBackgroundFill"] = accentBrush;
            res["CheckBoxCheckBackgroundFillPointerOver"] = light1Brush;
            res["CheckBoxCheckBackgroundFillPressed"] = dark1Brush;
            res["CheckBoxCheckGlyphForeground"] = new SolidColorBrush(Colors.White);
            res["RadioButtonCheckBackgroundStroke"] = accentBrush;
            res["RadioButtonCheckBackgroundStrokePointerOver"] = light1Brush;
            res["RadioButtonCheckBackgroundFill"] = accentBrush;

            // —— 其它强调色旧键 ——
            res["SystemControlForegroundAccentBrush"] = accentBrush;
            res["SystemControlBackgroundAccentBrush"] = accentBrush;
            res["SystemControlHighlightAccentBrush"] = accentBrush;
            res["SystemControlHighlightAltAccentBrush"] = accentBrush;
            res["SystemControlHighlightListAccentLowBrush"] = new SolidColorBrush(Color.FromArgb(51, accent.R, accent.G, accent.B));
            res["SystemControlHighlightListAccentMediumBrush"] = new SolidColorBrush(Color.FromArgb(102, accent.R, accent.G, accent.B));
            res["SystemControlHighlightListAccentHighBrush"] = new SolidColorBrush(Color.FromArgb(153, accent.R, accent.G, accent.B));
            res["AccentTextFillColorPrimaryBrush"] = accentBrush;

            // —— ListViewItem 选中背景 ——
            res["ListViewItemBackgroundSelected"] = new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B));
            res["ListViewItemBackgroundSelectedPointerOver"] = new SolidColorBrush(Color.FromArgb(96, accent.R, accent.G, accent.B));
            res["ListViewItemBackgroundSelectedPressed"] = new SolidColorBrush(Color.FromArgb(128, accent.R, accent.G, accent.B));
            res["ListViewItemBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
            res["ListViewItemBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
            res["GridViewItemBackgroundSelected"] = new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B));
            res["GridViewItemBackgroundSelectedPointerOver"] = new SolidColorBrush(Color.FromArgb(96, accent.R, accent.G, accent.B));
            res["GridViewItemBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
            res["GridViewItemBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
        }

        /// <summary>移除覆盖，恢复系统主题色。</summary>
        private static void RestoreSystemAccentResources()
        {
            var res = Application.Current.Resources;
            string[] keys =
            {
                "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
                "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
                "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
                "AccentFillColorDisabledBrush", "AccentFillColorSelectedTextBackgroundBrush",
                "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
                "AccentButtonBackgroundDisabled", "AccentButtonForeground", "AccentButtonForegroundPointerOver",
                "AccentButtonForegroundPressed", "AccentButtonForegroundDisabled",
                "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
                "SliderTrackValueFillDisabled", "SliderTrackFill", "SliderTrackFillPointerOver",
                "SliderTrackFillDisabled",
                "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
                "SliderThumbBackgroundDisabled", "SliderThumbBorderBrush", "SliderThumbBorderBrushPointerOver",
                "SliderThumbBorderBrushPressed", "SliderThumbBorderBrushDisabled",
                "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
                "ToggleSwitchFillOnDisabled",
                "CheckBoxCheckBackgroundStroke", "CheckBoxCheckBackgroundStrokePointerOver",
                "CheckBoxCheckBackgroundStrokePressed", "CheckBoxCheckBackgroundFill",
                "CheckBoxCheckBackgroundFillPointerOver", "CheckBoxCheckBackgroundFillPressed",
                "CheckBoxCheckGlyphForeground",
                "RadioButtonCheckBackgroundStroke", "RadioButtonCheckBackgroundStrokePointerOver",
                "RadioButtonCheckBackgroundFill",
                "SystemControlForegroundAccentBrush", "SystemControlBackgroundAccentBrush",
                "SystemControlHighlightAccentBrush", "SystemControlHighlightAltAccentBrush",
                "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush",
                "SystemControlHighlightListAccentHighBrush", "AccentTextFillColorPrimaryBrush",
                "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver",
                "ListViewItemBackgroundSelectedPressed", "ListViewItemBackgroundPointerOver",
                "ListViewItemBackgroundPressed",
                "GridViewItemBackgroundSelected", "GridViewItemBackgroundSelectedPointerOver",
                "GridViewItemBackgroundPointerOver", "GridViewItemBackgroundPressed"
            };
            foreach (string key in keys)
            {
                res.Remove(key);
            }
        }

        private static Color MixWithWhite(Color c, double t)
            => Color.FromArgb(255, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

        private static Color MixWithBlack(Color c, double t)
            => Color.FromArgb(255, (byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));
    }
}
