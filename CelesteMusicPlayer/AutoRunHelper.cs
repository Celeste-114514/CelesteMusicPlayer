using System;
using System.IO;
using Microsoft.Win32;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 开机自启动（登录 Windows 后自动启动）注册表开关。
    ///
    /// 背景（2026-09-11 修的 bug）：
    ///   安装包（NSIS，installer/CelesteMusicPlayer.nsi 的 SEC_AUTORUN 组件，向导里默认勾选）
    ///   会直接写 HKCU\...\CurrentVersion\Run\CelesteMusicPlayer。而程序设置里关闭自启动的
    ///   逻辑原先只在「开关值发生变化」时才动注册表 —— 于是安装包留下的那一项永远清不掉，
    ///   表现为「设置里明明是关的，开机却还是自启」。
    ///
    /// 现在改成幂等的 Apply：程序启动时、每次保存设置时都按当前设置值把注册表校正到位。
    /// 只操作自己名下的值（CelesteMusicPlayer），不碰其它启动项。
    /// </summary>
    internal static class AutoRunHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CelesteMusicPlayer";

        /// <summary>按启用/禁用写入或删除自启动项（幂等，可反复调用）。</summary>
        public static void Apply(bool enable)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key == null)
                {
                    return;
                }

                if (enable)
                {
                    string exePath = Environment.ProcessPath
                        ?? Path.Combine(AppContext.BaseDirectory, "CelesteMusicPlayer.exe");
                    key.SetValue(ValueName, $"\"{exePath}\"");
                    StartupLog.Write("[自启动] 已写入注册表项 → " + exePath);
                }
                else
                {
                    // 设置是关的就无条件删除：包括安装包/旧版本遗留的项。
                    bool existed = key.GetValue(ValueName) != null;
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    if (existed)
                    {
                        StartupLog.Write("[自启动] 已清除注册表残留项（设置中为关闭）");
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AutoRunHelper.Apply", caught);
            }
        }

        /// <summary>
        /// 按当前设置校正注册表。程序启动时调一次即可清掉安装包等外部写入的残留项。
        ///
        /// 首次运行（设置文件尚不存在）时特殊处理：
        ///   安装包向导里若勾选了「开机自动启动」，它会先于程序把注册表项写好。
        ///   此时若机械地按设置默认（关）去删，等于让安装包那个勾选项失效。
        ///   所以首次运行时反过来「沿用安装包的选择」并写回设置；此后一律以设置里为准。
        /// </summary>
        public static void SyncWithSettings()
        {
            try
            {
                bool firstRun = !File.Exists(AppSettingsStore.GetSettingsFilePath());
                if (firstRun && IsRegistered())
                {
                    AppSettingsState adopted = AppSettingsStore.Load();
                    adopted.AutoRun = true;
                    AppSettingsStore.Save(adopted);
                    StartupLog.Write("[自启动] 首次运行：沿用安装包勾选的自启动（开）");
                    return;
                }

                Apply(AppSettingsStore.Load().AutoRun);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AutoRunHelper.SyncWithSettings", caught);
            }
        }

        /// <summary>注册表中是否已存在本程序的自启动项（不修改任何东西）。</summary>
        public static bool IsRegistered()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) != null;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AutoRunHelper.IsRegistered", caught);
                return false;
            }
        }
    }
}
