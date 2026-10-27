using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 托盘右键菜单用的**只读**外观快照。
    ///
    /// 为什么单独一个快照类型：托盘菜单跑在 H.NotifyIcon 的 SecondWindow 承载窗口里，
    /// 那是一个独立的 XAML 树，**不会**继承主窗口的主题。所以要拿到跟主界面一致的观感，
    /// 只能把当前生效的外观解析成一堆具体的画刷/字号，再由 Builder 铺到菜单自己的作用域上。
    ///
    /// 解析入口只有 WebAppearance 这一个（网页外观的唯一真相源），这里不另抄一份配色，
    /// 免得网页换皮、托盘还停在旧颜色上。
    /// </summary>
    internal sealed class TrayTheme
    {
        private TrayTheme(bool dark, Color panel, Color border, Color foreground, Color muted,
                          Color accent, Color hover, Color pressed, CornerRadius radius, FontFamily font, double fontSize)
        {
            Dark = dark;
            Panel = panel;
            Border = border;
            Foreground = foreground;
            Muted = muted;
            Accent = accent;
            Hover = hover;
            Pressed = pressed;
            Radius = radius;
            Font = font;
            FontSize = fontSize;
        }

        internal bool Dark { get; }
        internal Color Panel { get; }
        internal Color Border { get; }
        internal Color Foreground { get; }
        internal Color Muted { get; }
        internal Color Accent { get; }
        internal Color Hover { get; }
        internal Color Pressed { get; }
        internal CornerRadius Radius { get; }
        internal FontFamily Font { get; }
        internal double FontSize { get; }

        /// <summary>菜单打开时的那次 "也可以重建" 判定用不到，只需要它能还原当前外观。</summary>
        internal static TrayTheme Current()
        {
            WebAppearance.State state = WebAppearance.HasDraft() ? WebAppearance.Draft() : WebAppearance.Load();
            string mode = WebAppearance.ResolveMode(state.mode);
            Dictionary<string, string> values = WebAppearance.Resolve(state, mode);

            // 菜单底色优先用专门的 menu-bg；这一支在部分预设里可能是带透明的写法，
            // ParseHexColor 只认 #RRGGBB（解析失败会给出 null），所以一路回退到实心的 surface/panel。
            Color panel = Pick(values, "menu-bg") ?? Pick(values, "surface") ?? Pick(values, "panel") ?? Colors.White;
            return new TrayTheme(
                dark: mode == WebAppearance.Dark,
                panel: panel,
                border: Pick(values, "line") ?? Colors.Gray,
                foreground: Pick(values, "ink") ?? Colors.Black,
                muted: Pick(values, "muted") ?? Colors.DimGray,
                accent: Pick(values, "accent") ?? Colors.DeepSkyBlue,
                hover: Pick(values, "hover") ?? Colors.LightGray,
                pressed: Pick(values, "pressed") ?? Colors.Gray,
                radius: new CornerRadius(Number(values, "radius", 8, 4, 16)),
                font: BuildFont(state),
                fontSize: Math.Clamp(14 * Number(values, "text-scale", 1, 0.8, 1.6), 11, 20));
        }

        private static Color? Pick(Dictionary<string, string> values, string key)
            => values.TryGetValue(key, out string? raw) ? ColorHelper.ParseHexColor(raw) : null;

        /// <summary>取数值型变量（纯数字字符串）。读不出来或超出合理区间就用 fallback。</summary>
        private static double Number(Dictionary<string, string> values, string key, double fallback, double min, double max)
        {
            if (!values.TryGetValue(key, out string? raw)) return fallback;
            string text = raw.Replace("px", "").Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? Math.Clamp(parsed, min, max)
                : fallback;
        }

        /// <summary>
        /// 菜单里的中文必须显示得出来，所以中文优先、西文族兜底。
        /// 相比之下，真正偏技术场景（比如未来的极客预设）会把等宽族排到前面。
        /// </summary>
        private static FontFamily BuildFont(WebAppearance.State state)
        {
            string cjk = string.IsNullOrWhiteSpace(state.fontCjk) ? WebAppearance.DefaultFontCjk : state.fontCjk;
            string ui = string.IsNullOrWhiteSpace(state.fontUi) ? WebAppearance.DefaultFontUi : state.fontUi;
            return new FontFamily("'" + cjk + "','" + ui + "',Segoe UI");
        }
    }
}
