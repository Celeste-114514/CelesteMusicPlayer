// 令牌测试皮肤 · 长度类令牌解析（形状腿运行时 TokenShapeRuntime 也用这个）
//
// 历史：圆角档位映射/遍历/原值字典原本在这个文件里，当时只挂主窗口一个根，
// 导致用户实测"有的按钮（独立窗口里的）没跟着变方角"。2026-10-17 全部搬入
// TokenShapeRuntime.cs 并扩成多窗口共享运行时（Attach 模式，12 个窗口全部接入）。
// 这里只剩后端仍需要的"取一个长度令牌当前值"。

using System;
using System.Globalization;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 取一个长度类令牌的当前值（按生效主题文件）。解析失败返回 null，
        /// 调用方据此保持原值，宁可这个令牌不生效，也不要把界面改坏。
        /// </summary>
        internal static double? ResolveTokenSkinDouble(string tokenName)
        {
            var window = Instance;
            if (window == null)
            {
                return null;
            }

            try
            {
                bool dark = window.ResolveIsDark();
                string name = AppSettingsStore.Load().CelesteThemeName ?? "";
                string key = name + "|" + (dark ? "D" : "L");
                if (_tokenColorTheme == null || _tokenColorKey != key)
                {
                    _tokenColorTheme = window.LoadCelesteActiveTheme() ?? CelesteThemeStore.CreateDefault();
                    _tokenColorKey = key;
                    _tokenColorDark = dark;
                }

                string? raw = CelesteThemeStore.Resolve(_tokenColorTheme, tokenName, _tokenColorDark);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    ? value
                    : (double?)null;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ResolveTokenSkinDouble", caught);
                return null;
            }
        }
    }
}
