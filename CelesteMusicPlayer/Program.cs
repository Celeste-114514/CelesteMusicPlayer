using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using WinRT;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 自定义入口：先固定工作目录到 exe 所在目录，避免资源管理器/快捷方式启动时
    /// 当前目录不是输出目录导致依赖查找失败（VS 调试时常不复现）。
    /// </summary>
    public static class Program
    {
        private const uint MbIconError = 0x00000010;
        private const uint MbIconWarning = 0x00000030;

        private const string DotNetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/9.0";
        private const string WinAppSdkDownloadUrl = "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads";

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

        [STAThread]
        private static void Main(string[] args)
        {
            string baseDir = AppContext.BaseDirectory;
            try
            {
                Directory.SetCurrentDirectory(baseDir);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }

            StartupLog.Write("Main begin, BaseDirectory=" + baseDir);

            // 框架依赖 + 非打包(Unpackaged) 发布时，Windows App SDK 运行时由目标机安装提供，
            // 不能把 BASE_DIRECTORY 指向 exe 目录（那里没有运行时）；自包含发布、或打包(MSIX)发布时
            // 运行时随包/在包内可寻址，沿用旧行为指过去。
            bool runtimeBundled = File.Exists(Path.Combine(baseDir, "Microsoft.WindowsAppRuntime.dll"));
            bool packaged = IsPackaged();
            if (runtimeBundled || packaged)
            {
                Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", baseDir);
            }

            // 框架依赖模式下，缺失 .NET 9 / Windows App SDK 运行时会导致托管代码都起不来，
            // 因此在“能跑托管代码”的前提下提前检测并给出安装指引（纯缺 .NET 9 时由系统 apphost 提示）。
            if (!runtimeBundled && !packaged && !CheckRuntimeDependencies(out string missing))
            {
                ShowRuntimePrompt(missing);
                return;
            }

            try
            {
                ComWrappersSupport.InitializeComWrappers();
                StartupLog.Write("ComWrappers initialized");
                Application.Start(static _ =>
                {
                    DispatcherQueueSynchronizationContext context =
                        new(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    StartupLog.Write("Creating App");
                    new App();
                });
                StartupLog.Write("Application.Start returned");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("Main", ex);
                // 启动期异常若与运行时缺失相关（框架依赖模式常见），给出安装指引而不是笼统的“启动失败”。
                if (!runtimeBundled && IsRuntimeMissingException(ex))
                {
                    ShowRuntimePrompt("未能初始化 Windows App SDK / .NET 运行环境。");
                    return;
                }

                try
                {
                    MessageBoxW(
                        0,
                        "启动失败：\n" + ex.Message + "\n\n请把同目录下 CelesteMusicPlayer.log 发给开发者。",
                        "CelesteMusicPlayer",
                        MbIconError);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }
            }
        }

        /// <summary>是否以打包(MSIX)方式运行。非打包(Unpackaged)下访问 Package.Current 会抛异常，属预期。</summary>
        private static bool IsPackaged()
        {
            try
            {
                return Windows.ApplicationModel.Package.Current != null;
            }
            catch
            {
                // 非打包模式必然抛异常（E_NOTIMPL），静默即可，不写日志。
                return false;
            }
        }

        /// <summary>检测框架依赖所需的运行时是否已安装；返回 false 时 missing 给出缺失项说明。</summary>
        private static bool CheckRuntimeDependencies(out string missing)
        {
            missing = string.Empty;
            try
            {
                // Windows App SDK 运行时（非自包含）：自 1.8 起以 MSIX 框架包安装
                // （包名 Microsoft.WindowsAppRuntime.1.8，或 CBS 变体），不写注册表键。
                // 因此先枚举系统已注册 MSIX 包判断主版本 1.8 是否就位，注册表仅作回退。
                bool winAppSdkOk = false;
                try
                {
                    var pkgManager = new PackageManager();
                    foreach (Package pkg in pkgManager.FindPackages())
                    {
                        string name = pkg.Name ?? string.Empty;
                        if (name.StartsWith("Microsoft.WindowsAppRuntime.1.8", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("Microsoft.WindowsAppRuntime.CBS.1.8", StringComparison.OrdinalIgnoreCase))
                        {
                            winAppSdkOk = true;
                            break;
                        }
                    }
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }

                // 回退：老版本运行时/个别系统通过注册表 HKLM\SOFTWARE\Microsoft\WindowsAppRuntime 定位。
                if (!winAppSdkOk)
                {
                    try
                    {
                        using (RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsAppRuntime"))
                        {
                            if (root != null)
                            {
                                foreach (string ver in root.GetSubKeyNames())
                                {
                                    using RegistryKey? verKey = root.OpenSubKey(ver);
                                    if (verKey != null && verKey.GetValue("BaseDirectory") is string baseDir && baseDir.Length > 0)
                                    {
                                        winAppSdkOk = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }
                }

                if (!winAppSdkOk)
                {
                    missing = "Windows App SDK 运行时（1.8）未安装。";
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }

            return string.IsNullOrEmpty(missing);
        }

        private static bool IsRuntimeMissingException(Exception ex)
        {
            if (ex is COMException com && (uint)com.HResult == 0x8000FFFF)
            {
                return true;
            }

            string msg = (ex.Message ?? string.Empty) + " " + (ex.GetType().FullName ?? string.Empty);
            return msg.Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Windows App SDK", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("framework", StringComparison.OrdinalIgnoreCase);
        }

        private static void ShowRuntimePrompt(string detail)
        {
            string text = "CelesteMusicPlayer 需要以下运行环境才能启动：\n\n"
                + "• .NET 9 桌面运行时\n  下载：" + DotNetDownloadUrl + "\n\n"
                + "• Windows App SDK 运行时（1.8）\n  下载：" + WinAppSdkDownloadUrl + "\n\n"
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : detail + "\n\n")
                + "请安装以上运行环境后重新启动本程序。";
            try
            {
                MessageBoxW(0, text, "缺少运行环境", MbIconWarning);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("Program.cs", caught); }
        }
    }
}
