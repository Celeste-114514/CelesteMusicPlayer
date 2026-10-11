// 主题文件（.celeste-theme）设置侧操作（皮肤系统第 3 步 · 合闸）
//
// 背景：第 1/2 步把「主题是数据」的基础设施都写好了——CelesteTheme.cs（文件格式 + 令牌目录 +
// 校验）、CelesteTokenApplier.cs（主题 JSON → 元素级资源字典）、WebUI 四页的 CSS 变量通道。
// 但一直没合闸：ApplyCelesteThemeAsync 零调用方、CelesteThemeName 从来没被任何 UI 写过、
// CelesteThemeStore.ListThemes/Save 零调用方。本文件补上设置侧入口，让用户能
// 选 / 存 / 导入 / 导出主题文件；应用本身仍走 MainWindow.CelesteTheme.cs 的
// ApplyCelesteThemeAsync（只挂元素级资源，绝不碰全局资源 → 不会 0xc000027b）。

using System;
using System.Diagnostics;
using System.IO;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private string? _lastAppliedThemeName;
        private bool _themeAppliedOnce;

        /// <summary>
        /// 把盘上的 CelesteThemeName 应用到界面。启动时（MainWindow_Loaded）与
        /// 设置页改完主题文件时调用。测试皮肤激活时主题文件就是皮肤配色来源，
        /// 由调用方负责随后重刷令牌皮肤（见 ApplyTokenSkin）。
        /// </summary>
        internal void ReloadThemeFromSettings()
        {
            try
            {
                string? name = AppSettingsStore.Load().CelesteThemeName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = null;
                }

                // 同一主题不重复应用：设置页很多开关变更都会走到这里，
                // 每次都重挂字典 + 推网页是纯浪费。
                if (_themeAppliedOnce
                    && string.Equals(_lastAppliedThemeName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _lastAppliedThemeName = name;
                _themeAppliedOnce = true;
                _ = ApplyCelesteThemeAsync(name);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ReloadThemeFromSettings", caught);
            }
        }

        /// <summary>把当前生效主题（设置里选的文件，或内置默认）另存为一个新主题文件。</summary>
        internal static bool SaveCurrentThemeAs(string name, out string? error)
        {
            error = null;
            try
            {
                var theme = Instance?.LoadCelesteActiveTheme() ?? CelesteThemeStore.CreateDefault();
                theme.Name = name;
                if (!CelesteThemeStore.Save(theme, out error))
                {
                    return false;
                }

                StartupLog.Write("[Theme] 主题已另存为 " + name);
                return true;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.SaveCurrentThemeAs", caught);
                error = caught.Message;
                return false;
            }
        }

        /// <summary>
        /// 从外部路径导入主题文件。先校验后落盘——坏主题（颜色不是颜色、数值超范围）
        /// 直接拒绝，绝不把界面改成不可用状态。返回是否成功；成功时给出落库后的主题名。
        /// </summary>
        internal static bool ImportThemeFile(string path, out string? savedName, out string? error)
        {
            savedName = null;
            error = null;
            try
            {
                var json = File.ReadAllText(path);
                var theme = System.Text.Json.JsonSerializer.Deserialize<CelesteThemeFile>(json);
                var result = CelesteThemeStore.Validate(theme);
                if (!result.Ok)
                {
                    error = string.Join("；", result.Errors);
                    return false;
                }

                if (string.IsNullOrWhiteSpace(theme!.Name))
                {
                    theme.Name = Path.GetFileNameWithoutExtension(path);
                }

                if (!CelesteThemeStore.Save(theme, out error))
                {
                    return false;
                }

                savedName = theme.Name;
                StartupLog.Write("[Theme] 已导入主题 " + theme.Name + " ← " + path);
                return true;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ImportThemeFile", caught);
                error = caught.Message;
                return false;
            }
        }

        /// <summary>把指定主题（空 = 内置默认）序列化写到目标路径，供用户分享。</summary>
        internal static bool ExportThemeFile(string name, string targetPath, out string? error)
        {
            error = null;
            try
            {
                var theme = string.IsNullOrWhiteSpace(name)
                    ? CelesteThemeStore.CreateDefault()
                    : CelesteThemeStore.Load(name);
                if (theme == null)
                {
                    error = "主题不存在：" + name;
                    return false;
                }

                File.WriteAllText(targetPath, CelesteThemeStore.Serialize(theme));
                StartupLog.Write("[Theme] 已导出主题 " + (string.IsNullOrWhiteSpace(name) ? "(内置默认)" : name) + " → " + targetPath);
                return true;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ExportThemeFile", caught);
                error = caught.Message;
                return false;
            }
        }

        /// <summary>资源管理器打开 Themes 目录（不存在则先建）。</summary>
        internal static void OpenThemesDirectory()
        {
            try
            {
                var dir = CelesteThemeStore.ThemesDirectory;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.OpenThemesDirectory", caught);
            }
        }
    }
}
