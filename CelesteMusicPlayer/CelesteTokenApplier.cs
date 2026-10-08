// 主题令牌 → WinUI 资源字典（第 2 步 · 让令牌真正落到原生页面上）
//
// 现状痛点（第 1 步只把数据和通道做完了，页面上一处都没用上）：
//   全项目 21 个 XAML、约 370 处引用 25 个系统刷子（CardStrokeColorDefaultBrush 126 次…），
//   换主题只能改代码。Celeste.* 键零引用 = 有格式没牙。
// 本文件负责：主题 JSON → ResourceDictionary 的 Celeste.* 键，供 XAML 用 {ThemeResource Celeste.*} 引用。
//
// ⚠⚠ 三条铁律（违反会秒崩或切不动，务必遵守）：
//  1. 绝不写 Application.Resources。极客皮肤踩过：窗口渲染后改全局资源键
//     触发 WinUI 原生崩溃 0xc000027b，托管 try-catch 拦不住。
//     本文件只往「元素级 Resources」写（MainWindow.RootShell.Resources）。
//  2. 必须用 ThemeResource 而不是 StaticResource。换字典时 StaticResource 不刷新，
//     页面会停留在旧配色；ThemeResource 才会跟着 ThemeDictionaries 重新求值。
//  3. FontFamily 绝不能赋 null。WinRT 把 null 转成字符串 "Unknown"，XAML 解析抛
//     COMException 0x800F1001，且赋值点当场中断、后面整段循环都不执行
//     （表现是整块 UI 空白、日志干净、极难查）。要"跟随系统"就用 ClearValue 或空串跳过。
//
// 命名映射：令牌 "Panel.Background" → 资源键 "Celeste.Panel.Background"
//          WebView2 侧同一份数据 → CSS 变量 "--celeste-panel-background"

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 把一份主题文件展开成 WinUI 资源字典，挂在元素级 Resources 上。
    /// 深浅两套色各建一个 ThemeDictionary，切换系统深浅色时自动跟着换。
    /// </summary>
    public static class CelesteTokenApplier
    {
        /// <summary>令牌名 → 资源键。Panel.Background → Celeste.Panel.Background</summary>
        public static string ToResourceKey(string tokenName) => "Celeste." + tokenName;

        /// <summary>
        /// 建一棵可直接挂到 FrameworkElement.Resources 的字典。
        /// 根字典自带 Light/ThemeDictionaries/{Default,HighContrast}。
        /// </summary>
        public static ResourceDictionary Build(CelesteThemeFile? theme, bool dark)
        {
            var dict = new ResourceDictionary();

            // ThemeDictionaries 是 WinUI 切深浅色的机制：系统主题一变，
            // 所有 {ThemeResource Celeste.*} 引用自动重新求值，无需重建字典。
            dict.ThemeDictionaries["Default"] = BuildMode(theme, dark: false);
            dict.ThemeDictionaries["HighContrast"] = BuildMode(theme, dark: true);
            if (dark)
            {
                // 兜底：系统深色时用深色组当默认值（ThemeDictionaries 的 Default 键对应浅色）
                dict.ThemeDictionaries["Light"] = BuildMode(theme, dark: false);
            }
            return dict;
        }

        /// <summary>建单个模式的令牌字典。深浅各建一次，互不干扰。</summary>
        private static ResourceDictionary BuildMode(CelesteThemeFile? theme, bool dark)
        {
            var rd = new ResourceDictionary();
            if (theme == null) theme = CelesteThemeStore.CreateDefault();

            foreach (var def in CelesteTokenCatalog.All)
            {
                var raw = CelesteThemeStore.Resolve(theme, def.Name, dark);
                if (string.IsNullOrWhiteSpace(raw)) continue;   // 字体空 = 跟随系统，跳过

                var key = ToResourceKey(def.Name);
                switch (def.Type)
                {
                    case "color":
                        var c = ParseColor(raw);
                        if (c.HasValue) rd[key] = new SolidColorBrush(c.Value);
                        break;

                    case "length":
                        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var len))
                            rd[key] = len.ToString("F0", CultureInfo.InvariantCulture);
                        break;

                    case "duration":
                        rd[key] = raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? raw : raw + "ms";
                        break;

                    case "number":
                        rd[key] = raw;
                        break;

                    case "fontFamily":
                        rd[key] = new FontFamily(raw);
                        break;
                }
            }
            return rd;
        }

        /// <summary>
        /// 解析颜色。接受 #RGB / #RRGGBB / #AARRGGBB / 常见 CSS 颜色名。
        /// 解析不了就返回 null —— 宁可这个令牌缺失，也不要把界面涂成黑色。
        /// </summary>
        public static Color? ParseColor(string raw)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) return null;

            if (s[0] == '#')
            {
                var hex = s[1..];
                if (hex.Length == 3)
                {
                    // #RGB → #RRGGBB
                    return Color.FromArgb(255,
                        Hex2(hex[0], hex[0]), Hex2(hex[1], hex[1]), Hex2(hex[2], hex[2]));
                }
                if (hex.Length == 6)
                {
                    return Color.FromArgb(255,
                        Hex2(hex[0], hex[1]), Hex2(hex[2], hex[3]), Hex2(hex[4], hex[5]));
                }
                if (hex.Length == 8)
                {
                    return Color.FromArgb(Hex2(hex[0], hex[1]),
                        Hex2(hex[2], hex[3]), Hex2(hex[4], hex[5]), Hex2(hex[6], hex[7]));
                }
                return null;
            }

            if (NamedColors.TryGetValue(s.ToLowerInvariant(), out var known)) return known;
            return null;
        }

        private static byte Hex2(char a, char b)
        {
            static int N(char c) => char.IsAsciiHexDigit(c) ? Convert.ToInt32(c.ToString(), 16) : 0;
            return (byte)(N(a) * 16 + N(b));
        }

        /// <summary>
        /// 从元素级资源里读令牌值。
        /// 代码里建控件、设 Brush 时用这个，别手写常量——那样换主题就漏改了。
        /// </summary>
        public static Brush? TryGetBrush(FrameworkElement scope, string tokenName)
        {
            if (scope == null) return null;
            try
            {
                if (scope.Resources.TryGetValue(ToResourceKey(tokenName), out var v) && v is Brush b)
                    return b;
            }
            catch (Exception ex) { StartupLog.WriteException("CelesteTokenApplier.TryGetBrush", ex); }

            // 回退到内置默认，保证「主题没覆盖」时也有合理值而不是 null。
            // ⚠ FrameworkElement.RequestedTheme 的类型是 ElementTheme（Default/Light/Dark），
            //   和 Application.RequestedTheme（ApplicationTheme）不是同一个枚举，不能直接比。
            var def = CelesteTokenCatalog.Find(tokenName);
            if (def?.Type == "color")
            {
                bool dark = scope.RequestedTheme == ElementTheme.Dark;
                var c = ParseColor(dark ? def.DefaultDark : def.DefaultLight);
                if (c.HasValue) return new SolidColorBrush(c.Value);
            }
            return null;
        }

        /// <summary>读长度类令牌（圆角/间距），返回 double，缺省回退令牌默认值。</summary>
        public static double TryGetLength(FrameworkElement scope, string tokenName, double fallback)
        {
            if (scope != null)
            {
                try
                {
                    if (scope.Resources.TryGetValue(ToResourceKey(tokenName), out var v))
                    {
                        if (v is double d) return d;
                        if (v is string s &&
                            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                            return parsed;
                    }
                }
                catch (Exception ex) { StartupLog.WriteException("CelesteTokenApplier.TryGetLength", ex); }
            }
            var def = CelesteTokenCatalog.Find(tokenName);
            if (def != null && double.TryParse(def.DefaultLight, NumberStyles.Float, CultureInfo.InvariantCulture, out var defLen))
                return defLen;
            return fallback;
        }

        /// <summary>CSS 颜色名 → Color。够用即可，主题文件推荐还是用 #RRGGBB。</summary>
        private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.Ordinal)
        {
            ["transparent"] = Color.FromArgb(0, 0, 0, 0),
            ["black"] = Color.FromArgb(255, 0, 0, 0),
            ["white"] = Color.FromArgb(255, 255, 255, 255),
            ["red"] = Color.FromArgb(255, 255, 0, 0),
            ["green"] = Color.FromArgb(255, 0, 128, 0),
            ["blue"] = Color.FromArgb(255, 0, 0, 255),
            ["yellow"] = Color.FromArgb(255, 255, 255, 0),
            ["gray"] = Color.FromArgb(255, 128, 128, 128),
            ["grey"] = Color.FromArgb(255, 128, 128, 128),
            ["silver"] = Color.FromArgb(255, 192, 192, 192),
            ["orange"] = Color.FromArgb(255, 255, 165, 0),
            ["gold"] = Color.FromArgb(255, 255, 215, 0),
            ["pink"] = Color.FromArgb(255, 255, 192, 203),
            ["purple"] = Color.FromArgb(255, 128, 0, 128),
            ["brown"] = Color.FromArgb(255, 165, 42, 42),
            ["cyan"] = Color.FromArgb(255, 0, 255, 255),
            ["magenta"] = Color.FromArgb(255, 255, 0, 255),
            ["lime"] = Color.FromArgb(255, 0, 255, 0),
            ["navy"] = Color.FromArgb(255, 0, 0, 128),
            ["teal"] = Color.FromArgb(255, 0, 128, 128),
            ["olive"] = Color.FromArgb(255, 128, 128, 0),
            ["maroon"] = Color.FromArgb(255, 128, 0, 0),
        };
    }
}
