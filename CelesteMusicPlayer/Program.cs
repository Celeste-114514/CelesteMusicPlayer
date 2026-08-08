using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
            catch
            {
            }

            Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", baseDir);
            StartupLog.Write("Main begin, BaseDirectory=" + baseDir);

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
                try
                {
                    MessageBoxW(
                        0,
                        "启动失败：\n" + ex.Message + "\n\n请把同目录下 CelesteMusicPlayer.log 发给开发者。",
                        "CelesteMusicPlayer",
                        MbIconError);
                }
                catch
                {
                }
            }
        }
    }
}
