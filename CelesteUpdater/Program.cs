using System.Diagnostics;
using System.Globalization;

namespace CelesteUpdater;

/// <summary>
/// 独立的「更新助手」：由主程序（CelesteMusicPlayer）在下载完安装包后启动，
/// 然后主程序立即退出。本助手：
///   1. 等待主程序进程完全退出（释放 CelesteMusicPlayer.exe 文件锁）；
///   2. 运行 NSIS 安装包（更新模式下带 /UPDATE，避免完成页重复启动主程序）；
///   3. 安装成功后自动重启主程序；
///   4. 清理刚下载的安装包。
/// 全程用户无需知道安装包下到了哪里，也无需手动启动它。
/// 参考 DeskBox.Updater 的实现思路。
/// </summary>
internal static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CelesteMusicPlayer",
        "CelesteUpdater.log");

    private static int Main(string[] args)
    {
        try
        {
            UpdateOptions options = UpdateOptions.Parse(args);

            if (string.IsNullOrWhiteSpace(options.InstallerPath) || !File.Exists(options.InstallerPath))
            {
                Log("Installer not found: " + (options.InstallerPath ?? "<null>"));
                return 2;
            }

            // 第一步：等主程序退出，确保文件锁已释放、可被覆盖安装。
            WaitForParentExit(options.ParentProcessId);

            // 兜底：再确认一遍主程序真的退出了（防止极端情况下文件仍被占用）。
            EnsureAppClosed();

            // 第二步：运行安装包。
            int exitCode = RunInstaller(options);
            bool succeeded = exitCode == 0;
            Log($"Installer exited with code {exitCode} (succeeded={succeeded}).");

            // 第三步：安装成功后自动重启主程序（更新模式由本助手负责，避免 NSIS 完成页重复启动）。
            if (succeeded && !string.IsNullOrWhiteSpace(options.AppPath) && File.Exists(options.AppPath))
            {
                _ = RestartApp(options.AppPath);
            }

            // 第四步：清理刚下载的安装包（临时目录里的那份）。
            if (succeeded)
            {
                TryDelete(options.InstallerPath);
            }

            return exitCode;
        }
        catch (Exception ex)
        {
            Log("Fatal: " + ex);
            return 1;
        }
    }

    private static void WaitForParentExit(int parentProcessId)
    {
        if (parentProcessId <= 0)
        {
            return;
        }

        try
        {
            using var parentProcess = Process.GetProcessById(parentProcessId);
            Log($"Waiting for main process {parentProcessId} to exit.");
            parentProcess.WaitForExit(milliseconds: 60_000);
        }
        catch (ArgumentException)
        {
            // 进程已经不在了（主程序已退出），直接继续。
            Log("Parent process already exited.");
        }
        catch (Exception ex)
        {
            Log("Wait parent failed: " + ex.Message);
        }
    }

    private static void EnsureAppClosed()
    {
        try
        {
            // 与 NSIS 安装包里的 taskkill 互为兜底：确保没有残留的 CelesteMusicPlayer 进程占着文件。
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/F /IM CelesteMusicPlayer.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(5_000);
        }
        catch (Exception ex)
        {
            Log("EnsureAppClosed best-effort taskkill failed (ignored): " + ex.Message);
        }
    }

    private static int RunInstaller(UpdateOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.InstallerPath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(options.InstallerPath) ?? Environment.CurrentDirectory
        };

        if (options.Silent)
        {
            // NSIS 静默安装开关
            startInfo.ArgumentList.Add("/S");
            startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
            startInfo.ArgumentList.Add("/NORESTART");
        }

        if (options.UpdateMode)
        {
            // 告诉 NSIS「这是从应用内更新启动的」，完成页不要再自动运行主程序，
            // 改由本助手在装完后负责重启。
            startInfo.ArgumentList.Add("/UPDATE");
        }

        Log($"Starting installer: {options.InstallerPath} (silent={options.Silent}, update={options.UpdateMode})");
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Log("Installer process did not start.");
            return 3;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static bool RestartApp(string appPath)
    {
        try
        {
            Log($"Restarting app: {appPath}");
            Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory
            });
            return true;
        }
        catch (Exception ex)
        {
            Log("Restart failed: " + ex.Message);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Log("Deleted installer: " + path);
            }
        }
        catch (Exception ex)
        {
            Log("Failed to delete installer (ignored): " + ex.Message);
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private sealed class UpdateOptions
    {
        public int ParentProcessId { get; private init; }
        public string InstallerPath { get; private init; } = string.Empty;
        public string AppPath { get; private init; } = string.Empty;
        public bool Silent { get; private init; }
        public bool UpdateMode { get; private init; }

        public static UpdateOptions Parse(IReadOnlyList<string> args)
        {
            int parentProcessId = 0;
            string installerPath = string.Empty;
            string appPath = string.Empty;
            bool silent = false;
            bool updateMode = false;

            for (int index = 0; index < args.Count; index++)
            {
                string arg = args[index];
                if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase))
                {
                    silent = true;
                    continue;
                }

                if (string.Equals(arg, "--update", StringComparison.OrdinalIgnoreCase))
                {
                    updateMode = true;
                    continue;
                }

                if (index + 1 >= args.Count)
                {
                    continue;
                }

                string value = args[++index];
                if (string.Equals(arg, "--pid", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPid))
                {
                    parentProcessId = parsedPid;
                }
                else if (string.Equals(arg, "--installer", StringComparison.OrdinalIgnoreCase))
                {
                    installerPath = value;
                }
                else if (string.Equals(arg, "--app", StringComparison.OrdinalIgnoreCase))
                {
                    appPath = value;
                }
            }

            return new UpdateOptions
            {
                ParentProcessId = parentProcessId,
                InstallerPath = installerPath,
                AppPath = appPath,
                Silent = silent,
                UpdateMode = updateMode
            };
        }
    }
}
