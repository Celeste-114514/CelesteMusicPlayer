// 主题应用主入口（皮肤系统第 2 步 · 把主题真正刷到界面上）
//
// 一次切换主题要做三件事：
//   1) 往 RootShell.Resources 挂新的令牌字典（原生页 {ThemeResource Celeste.*} 自动跟着变）
//   2) 通知 WebView2 侧注入同名 CSS 变量（两套渲染层共用同一份数据）—— 第 3 步接上
//   3) 广播事件，让代码里动态建的控件（详情页封面/按钮等）重新取一次值
//
// ⚠ 为什么挂 RootShell.Resources 而不是 Application.Resources：
//   窗口渲染后改全局资源键会触发 WinUI 原生崩溃 0xc000027b，托管 catch 拦不住。
//   元素级 Resources 只影响本子树，是安全做法（极客皮肤 AudioFxBorder.Resources 同理）。
// ⚠ 为什么 XAML 里必须写 {ThemeResource} 而不是 {StaticResource}：
//   StaticResource 在换字典时不刷新，页面会停留在旧配色；ThemeResource 才会重新求值。

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        // ---- 主题色解析（2026-10-18 从已删除的 MainWindow.TokenSkin.cs 迁出）----
        // 这套方法不属于「测试皮肤」：它是「设置页主题色」与「主题文件自带 Accent.Solid」
        // 两条链的唯一真相源，设置页「快速调整当前主题」的色块回填直接问它，必须保留。

        /// <summary>
        /// 当前生效的主题色（"#RRGGBB"）。Custom=设置里的自定义色；跟随系统=主题文件
        /// Accent.Solid（Light 优先回落 Dark），文件没写则当前系统主题色。
        /// </summary>
        internal static string ResolveEffectiveAccentHex(CelesteThemeFile? theme)
        {
            var settings = AppSettingsStore.Load();
            if (settings.AccentSource == "Custom")
            {
                return string.IsNullOrWhiteSpace(settings.CustomAccentColor)
                    ? "#0078D4"
                    : settings.CustomAccentColor;
            }

            if (TryGetThemeAccentHex(theme, out string fromFile))
            {
                return fromFile;
            }

            return SystemAccentHex();
        }

        /// <summary>读主题文件自带的 Accent.Solid（Light 优先回落 Dark）。没写/为空返回 false。</summary>
        private static bool TryGetThemeAccentHex(CelesteThemeFile? theme, out string hex)
        {
            if (theme != null)
            {
                if (theme.Light.TryGetValue(CelesteTokenCatalog.AccentSolid, out var light)
                    && !string.IsNullOrWhiteSpace(light))
                {
                    hex = light.Trim();
                    return true;
                }
                if (theme.Dark.TryGetValue(CelesteTokenCatalog.AccentSolid, out var dark)
                    && !string.IsNullOrWhiteSpace(dark))
                {
                    hex = dark.Trim();
                    return true;
                }
            }
            hex = "";
            return false;
        }

        /// <summary>系统主题色 → "#RRGGBB"。只有「跟随系统」且主题文件没写强调色时才走到。</summary>
        private static string SystemAccentHex()
        {
            var c = ThemeColorService.CurrentAccent;
            return string.Concat("#", c.R.ToString("X2"), c.G.ToString("X2"), c.B.ToString("X2"));
        }
    }
}
