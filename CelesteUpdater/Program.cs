using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CelesteUpdater;

/// <summary>
/// 独立的「更新助手」：主程序下载并校验完安装包后启动本助手，然后主程序退出。
///
/// 两种启动方式：
///   A. 事务模式（现在的正式路径）：
///      --transaction &lt;transaction.json&gt; --pid &lt;主程序pid&gt; --ready &lt;握手文件&gt;
///      事务文件由主程序写，本助手只认这份记录，不解析自由文本；
///   B. 旧参数模式（--installer / --app / --silent / --update / --pid）：
///      保留兼容，供老版本主程序或手动调用使用。
///
/// 硬规矩：
///   1. 参数与安装包都验过之后才写握手文件 —— 主程序看到握手才退出，否则它留在原地继续可用；
///   2. 只结束「那一个」主程序进程（按 pid + 启动时间核实），不再按映像名全杀；
///   3. 装完要验产物（主程序 + 主界面资源 + 版本没倒退），验过才重启；
///   4. 无论成败都写一份结果文件，下次主程序启动时读走并记日志。
/// </summary>
internal static class Program
{
    private const string AppExeName = "CelesteMusicPlayer.exe";
    private const int ParentExitTimeoutMs = 90_000;
    private const int InstallerTimeoutMs = 10 * 60 * 1000;

    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CelesteMusicPlayer");

    private static readonly string LogPath = Path.Combine(DataRoot, "CelesteUpdater.log");
    private static readonly string ResultPath = Path.Combine(DataRoot, "update-last-result.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static int Main(string[] args)
    {
        try
        {
            UpdateRequest request = UpdateRequest.Parse(args);
            Log("=== CelesteUpdater 启动：" + request.Describe() + " ===");

            if (request.IsTransactionMode)
            {
                return RunTransaction(request);
            }

            return RunLegacy(request);
        }
        catch (Exception ex)
        {
            Log("Fatal: " + ex);
            return 1;
        }
    }

    // ———————————————————— 事务模式 ————————————————————

    private static int RunTransaction(UpdateRequest request)
    {
        string recordPath = Path.GetFullPath(request.TransactionPath);
        if (!File.Exists(recordPath))
        {
            Log("事务文件不存在：" + recordPath);
            return 2;
        }

        UpdateTransaction? transaction = LoadTransaction(recordPath);
        if (transaction == null)
        {
            Log("事务文件读不出来：" + recordPath);
            return 3;
        }

        transaction.DirectoryPath = Path.GetDirectoryName(recordPath) ?? string.Empty;
        string packagePath = transaction.PackagePath;
        Log($"事务 {transaction.Id} 版本 {transaction.Version}，安装包 {packagePath}");

        if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
        {
            WriteResult(transaction, false, "no-package", "安装包不在事务目录里：" + packagePath, 0, false);
            // 包都没了，事务目录留着只会让主程序下次启动时以为「有个能装的包」。
            CleanupTransaction(transaction.DirectoryPath, packagePath);
            return 4;
        }

        // 下载时校验过一次，这里再算一遍：从「校验通过」到「真的安装」之间，
        // 文件仍可能被杀软隔离、被磁盘写坏、被别的程序改名。装之前确认一次最便宜。
        if (!VerifyPackage(packagePath, transaction.ExpectedSha256))
        {
            WriteResult(transaction, false, "checksum", "安装包哈希与事务记录不一致，已中止安装。", 0, false);
            // 哈希不对的包必须删掉，绝不能留在原地被当成「已校验，可安装」。
            CleanupTransaction(transaction.DirectoryPath, packagePath);
            return 5;
        }

        string installDir = ResolveInstallDirectory(transaction);
        if (string.IsNullOrEmpty(installDir))
        {
            WriteResult(transaction, false, "no-install-dir", "无法确定安装目录，已中止安装。", 0, false);
            return 6;
        }

        string installExe = Path.Combine(installDir, AppExeName);
        string versionBefore = ReadFileVersion(installExe);

        // —— 握手：验到这里才敢让主程序退出 ——
        if (!WriteReady(request.ReadyPath))
        {
            Log("握手文件写不了，主程序不会退出：" + request.ReadyPath);
            return 7;
        }

        if (!WaitForAppExit(request.ParentProcessId, transaction, installExe, out string exitProblem))
        {
            WriteResult(transaction, false, "app-still-running", exitProblem, 0, false);
            // 主程序没退出就不装 —— 此时主程序还活着，用户至少还能用。
            return 8;
        }

        InstallerRun run = RunInstaller(packagePath, installDir);
        Log($"安装包退出码 {run.ExitCode}，耗时 {run.ElapsedMs} ms{(run.TimedOut ? "（超时强杀）" : string.Empty)}");

        if (run.ExitCode != 0)
        {
            WriteResult(transaction, false, "installer-failed",
                $"安装包返回 {run.ExitCode}{(run.TimedOut ? "（超时）" : string.Empty)}。",
                run.ExitCode, false);
            // 装失败也要把程序拉起来，不能把用户扔在一个「装了一半」的后台里。
            RestartApp(installDir, transaction.RestartPath);
            return 9;
        }

        if (!VerifyInstall(installDir, transaction.Version, versionBefore, out string problem))
        {
            WriteResult(transaction, false, "verify-install", problem, run.ExitCode, false);
            RestartApp(installDir, transaction.RestartPath);
            return 10;
        }

        bool restarted = RestartApp(installDir, transaction.RestartPath);

        // 装成功才删安装包与事务目录；失败时整套留着，主程序下次启动还能看到并给用户手动装。
        CleanupTransaction(transaction.DirectoryPath, packagePath);
        WriteResult(transaction, true, string.Empty, string.Empty, run.ExitCode, restarted);
        return restarted ? 0 : 11;
    }

    private static UpdateTransaction? LoadTransaction(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            Log("解析事务文件失败：" + ex.Message);
            return null;
        }
    }

    private static bool VerifyPackage(string packagePath, string expectedSha256)
    {
        try
        {
            var info = new FileInfo(packagePath);
            if (info.Length <= 0)
            {
                Log("安装包是空文件。");
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                // 没有哈希（老事务）就别假装校验过，明确记一笔。
                Log("事务里没有哈希，跳过校验，仅确认文件存在且非空。");
                return true;
            }

            string actual = ComputeSha256(packagePath);
            bool ok = string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
            Log(ok
                ? $"哈希校验通过（{info.Length} 字节）。"
                : $"哈希不一致：期望 {expectedSha256}，实际 {actual}");
            return ok;
        }
        catch (Exception ex)
        {
            Log("校验安装包时出错：" + ex.Message);
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ResolveInstallDirectory(UpdateTransaction transaction)
    {
        // 优先用事务里记的安装目录；它不可用时退回主程序自己的路径。
        if (!string.IsNullOrWhiteSpace(transaction.InstallDirectory) &&
            Path.IsPathFullyQualified(transaction.InstallDirectory) &&
            Directory.Exists(transaction.InstallDirectory))
        {
            return Path.GetFullPath(transaction.InstallDirectory);
        }

        if (!string.IsNullOrWhiteSpace(transaction.RestartPath))
        {
            string? dir = Path.GetDirectoryName(transaction.RestartPath);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Log("事务里的安装目录不可用，改用主程序所在目录：" + dir);
                return Path.GetFullPath(dir);
            }
        }

        return string.Empty;
    }

    private static bool WriteReady(string readyPath)
    {
        if (string.IsNullOrWhiteSpace(readyPath))
        {
            // 没要求握手（手动调试），不算错误。
            return true;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(readyPath))!);
            File.WriteAllText(readyPath, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            Log("已写握手文件：" + readyPath);
            return true;
        }
        catch (Exception ex)
        {
            Log("写握手文件失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 等主程序退出。不再「按映像名全杀」：先按 pid 等，等不到再按 pid + 启动时间核实身份，
    /// 只有确认是同一次启动的进程才结束它。这样即使用户同时开着别的副本，也不会被误杀。
    /// </summary>
    private static bool WaitForAppExit(int parentProcessId, UpdateTransaction transaction, string installExe, out string problem)
    {
        problem = string.Empty;
        DateTime? parentStart = null;

        if (parentProcessId > 0)
        {
            try
            {
                using Process parent = Process.GetProcessById(parentProcessId);
                parentStart = parent.StartTime;
                Log($"等待主程序 pid={parentProcessId} 退出（启动于 {parentStart:HH:mm:ss}）。");
                if (parent.WaitForExit(ParentExitTimeoutMs))
                {
                    Log("主程序已退出。");
                }
                else
                {
                    Log("等主程序退出超时，尝试温和关闭。");
                    TryCloseMainWindow(parentProcessId, parentStart);
                }
            }
            catch (ArgumentException)
            {
                Log("主程序已经不在（pid 不存在）。");
            }
            catch (Exception ex)
            {
                Log("等待主程序退出时出错（继续尝试）：" + ex.Message);
            }
        }

        // 双重确认：文件能不能独占打开，比「进程表里看不见」更可靠。
        if (!WaitForFileUnlocked(installExe, TimeSpan.FromSeconds(20)))
        {
            problem = "主程序仍在运行，无法覆盖安装文件。";
            KillSamePathInstances(installExe);
            if (!WaitForFileUnlocked(installExe, TimeSpan.FromSeconds(15)))
            {
                Log("文件仍被占用，放弃安装：" + installExe);
                return false;
            }
        }

        Log("安装文件已可写。");
        return true;
    }

    private static void TryCloseMainWindow(int processId, DateTime? expectedStart)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (expectedStart.HasValue && !SameStart(process, expectedStart.Value))
            {
                Log("pid 已被别的进程复用，不关闭。");
                return;
            }

            if (process.CloseMainWindow())
            {
                Log("已发送关闭窗口请求。");
                process.WaitForExit(15_000);
            }
        }
        catch (Exception ex)
        {
            Log("温和关闭失败：" + ex.Message);
        }
    }

    private static bool SameStart(Process process, DateTime expected)
    {
        try
        {
            return Math.Abs((process.StartTime - expected).TotalSeconds) < 2;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按「可执行文件路径」核实身份，只结束后台残留的同路径实例。</summary>
    private static void KillSamePathInstances(string installExe)
    {
        string full = Path.GetFullPath(installExe);
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExeName)))
        {
            try
            {
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* 权限/位数不匹配，跳过 */ }
                if (path == null || !string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Log($"结束同路径残留进程 pid={process.Id}：{path}");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            catch (Exception ex)
            {
                Log("结束残留进程失败（忽略）：" + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool WaitForFileUnlocked(string path, TimeSpan timeout)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using FileStream probe = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }

        return false;
    }

    private static InstallerRun RunInstaller(string packagePath, string installDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = packagePath,
            // 安装包声明 RequestExecutionLevel user，不需要 UAC；
            // 用 false 才能拿到真实进程句柄并可靠地等它退出（UseShellExecute=true 时句柄可能是壳进程）。
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(packagePath) ?? Environment.CurrentDirectory
        };

        // NSIS：/S 静默，/SUPPRESSMSGBOXES 不弹框，/NORESTART 不动系统，
        // /UPDATE 让完成页别自己拉起主程序（重启由本助手负责）。
        // /D= 必须是最后一个参数且不加引号 —— NSIS 会把余下整行当路径，加引号反而装到带引号的目录里。
        string arguments = "/S /SUPPRESSMSGBOXES /NORESTART /UPDATE";
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            arguments += " /D=" + installDir.TrimEnd('\\');
        }

        startInfo.Arguments = arguments;
        Log($"运行安装包：{packagePath} 参数：{arguments}");

        var watch = Stopwatch.StartNew();
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return new InstallerRun(3, false, watch.ElapsedMilliseconds);
            }

            if (!process.WaitForExit(InstallerTimeoutMs))
            {
                Log("安装包超过 10 分钟没有结束，强杀。");
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Log("强杀失败：" + ex.Message); }
                return new InstallerRun(-1, true, watch.ElapsedMilliseconds);
            }

            return new InstallerRun(process.ExitCode, false, watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log("启动安装包失败：" + ex.Message);
            return new InstallerRun(4, false, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 装完验产物。三条都要过：主程序在、主界面资源在、版本没倒退。
    /// 版本比对「不低于要装的版本」即可 —— 修复安装允许版本不变，但不允许倒退。
    /// </summary>
    private static bool VerifyInstall(string installDir, string expectedVersion, string versionBefore, out string problem)
    {
        problem = string.Empty;

        string exe = Path.Combine(installDir, AppExeName);
        if (!File.Exists(exe))
        {
            problem = "安装后没有找到主程序：" + exe;
            return false;
        }

        string shell = Path.Combine(installDir, "WebShell", "index.html");
        if (!File.Exists(shell))
        {
            problem = "安装后主界面资源缺失：" + shell;
            return false;
        }

        string versionAfter = ReadFileVersion(exe);
        Log($"版本：安装前 {Display(versionBefore)} → 安装后 {Display(versionAfter)}，目标 {Display(expectedVersion)}");

        if (!TryParseVersion(expectedVersion, out Version? expected) ||
            !TryParseVersion(versionAfter, out Version? actual))
        {
            Log("版本号解析不了，跳过版本比对（其它检查已通过）。");
            return true;
        }

        if (actual < expected)
        {
            problem = $"安装后版本是 {versionAfter}，低于要装的 {expectedVersion}。";
            return false;
        }

        return true;
    }

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "未知" : value;

    private static string ReadFileVersion(string exePath)
    {
        try
        {
            if (!File.Exists(exePath))
            {
                return string.Empty;
            }

            return FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryParseVersion(string text, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        // 去掉 -beta / -rc1 这类后缀，只留数字与点。
        int cut = value.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0)
        {
            value = value[..cut];
        }

        return Version.TryParse(value, out version);
    }

    private static bool RestartApp(string installDir, string restartPath)
    {
        string appPath = !string.IsNullOrWhiteSpace(restartPath) && File.Exists(restartPath)
            ? restartPath
            : Path.Combine(installDir, AppExeName);

        if (!File.Exists(appPath))
        {
            Log("要重启的主程序不存在：" + appPath);
            return false;
        }

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Log($"重启主程序（第 {attempt} 次）：{appPath}");
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
                Log("重启失败：" + ex.Message);
                Thread.Sleep(1500);
            }
        }

        return false;
    }

    private static void CleanupTransaction(string directory, string packagePath)
    {
        TryDeleteFile(packagePath);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                Log("已清理事务目录：" + directory);
            }
        }
        catch (Exception ex)
        {
            Log("清理事务目录失败（忽略）：" + ex.Message);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
                Log("已删除安装包：" + path);
            }
        }
        catch (Exception ex)
        {
            Log("删除安装包失败（忽略）：" + ex.Message);
        }
    }

    private static void WriteResult(
        UpdateTransaction transaction,
        bool succeeded,
        string errorCode,
        string errorMessage,
        int installerExitCode,
        bool restarted)
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            var result = new UpdateResult
            {
                TransactionId = transaction.Id,
                Version = transaction.Version,
                Succeeded = succeeded,
                ErrorCode = errorCode ?? string.Empty,
                ErrorMessage = errorMessage ?? string.Empty,
                InstallerExitCode = installerExitCode,
                Restarted = restarted,
                InstallDirectory = transaction.InstallDirectory,
                FinishedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            File.WriteAllText(ResultPath, JsonSerializer.Serialize(result, JsonOptions));
            Log(succeeded
                ? "更新成功" + (restarted ? "，已重启主程序。" : "，但重启失败。")
                : $"更新失败：{errorCode} {errorMessage}");
        }
        catch (Exception ex)
        {
            Log("写结果文件失败（忽略）：" + ex.Message);
        }
    }

    // ———————————————————— 旧参数模式（兼容） ————————————————————

    private static int RunLegacy(UpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.InstallerPath) || !File.Exists(request.InstallerPath))
        {
            Log("Installer not found: " + (request.InstallerPath ?? "<null>"));
            return 2;
        }

        WaitForLegacyParentExit(request.ParentProcessId);

        var legacy = new UpdateTransaction
        {
            Id = "legacy",
            Version = string.Empty,
            InstallDirectory = string.IsNullOrWhiteSpace(request.AppPath)
                ? string.Empty
                : Path.GetDirectoryName(request.AppPath) ?? string.Empty,
            RestartPath = request.AppPath ?? string.Empty
        };

        InstallerRun run = RunInstaller(request.InstallerPath, legacy.InstallDirectory);
        Log($"Installer exited with code {run.ExitCode}.");
        bool succeeded = run.ExitCode == 0;

        bool restarted = false;
        if (succeeded && !string.IsNullOrWhiteSpace(request.AppPath) && File.Exists(request.AppPath))
        {
            restarted = RestartApp(legacy.InstallDirectory, request.AppPath);
        }

        if (succeeded)
        {
            TryDeleteFile(request.InstallerPath);
        }

        WriteResult(legacy, succeeded, succeeded ? string.Empty : "installer-failed",
            succeeded ? string.Empty : $"安装包返回 {run.ExitCode}。", run.ExitCode, restarted);

        return run.ExitCode;
    }

    private static void WaitForLegacyParentExit(int parentProcessId)
    {
        if (parentProcessId <= 0)
        {
            return;
        }

        try
        {
            using var parentProcess = Process.GetProcessById(parentProcessId);
            Log($"Waiting for main process {parentProcessId} to exit.");
            parentProcess.WaitForExit(ParentExitTimeoutMs);
        }
        catch (ArgumentException)
        {
            Log("Parent process already exited.");
        }
        catch (Exception ex)
        {
            Log("Wait parent failed: " + ex.Message);
        }
    }

    // ———————————————————— 通用 ————————————————————

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private readonly record struct InstallerRun(int ExitCode, bool TimedOut, long ElapsedMs);

    /// <summary>
    /// 结果文件：主程序下次启动时读走并记进 StartupLog，用户报「更新完没反应」时能查。
    /// 放在事务目录外面 —— 事务目录成功时会被整棵删掉。
    /// </summary>
    private sealed class UpdateResult
    {
        public string TransactionId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public bool Succeeded { get; set; }
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public int InstallerExitCode { get; set; }
        public bool Restarted { get; set; }
        public string InstallDirectory { get; set; } = string.Empty;
        public string FinishedUtc { get; set; } = string.Empty;
    }

    /// <summary>事务记录（与主程序 UpdateService.UpdateTransaction 同结构，只读使用）。</summary>
    private sealed class UpdateTransaction
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string PackageKind { get; set; } = string.Empty;
        public string ExpectedSha256 { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string PackageFileName { get; set; } = string.Empty;
        public string InstallDirectory { get; set; } = string.Empty;
        public string RestartPath { get; set; } = string.Empty;
        public long BytesTotal { get; set; }
        public string Stage { get; set; } = string.Empty;
        public string CreatedUtc { get; set; } = string.Empty;
        public string UpdatedUtc { get; set; } = string.Empty;

        [JsonIgnore]
        public string DirectoryPath { get; set; } = string.Empty;

        [JsonIgnore]
        public string PackagePath => string.IsNullOrEmpty(PackageFileName) || string.IsNullOrEmpty(DirectoryPath)
            ? string.Empty
            : Path.Combine(DirectoryPath, PackageFileName);
    }

    private sealed class UpdateRequest
    {
        public int ParentProcessId { get; private init; }
        public string InstallerPath { get; private init; } = string.Empty;
        public string AppPath { get; private init; } = string.Empty;
        public string TransactionPath { get; private init; } = string.Empty;
        public string ReadyPath { get; private init; } = string.Empty;
        public bool Silent { get; private init; }
        public bool UpdateMode { get; private init; }

        public bool IsTransactionMode => !string.IsNullOrWhiteSpace(TransactionPath);

        public string Describe()
        {
            return IsTransactionMode
                ? $"transaction={TransactionPath} pid={ParentProcessId} ready={ReadyPath}"
                : $"installer={InstallerPath} app={AppPath} pid={ParentProcessId} silent={Silent} update={UpdateMode}";
        }

        public static UpdateRequest Parse(IReadOnlyList<string> args)
        {
            int parentProcessId = 0;
            string installerPath = string.Empty;
            string appPath = string.Empty;
            string transactionPath = string.Empty;
            string readyPath = string.Empty;
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
                else if (string.Equals(arg, "--transaction", StringComparison.OrdinalIgnoreCase))
                {
                    transactionPath = value;
                }
                else if (string.Equals(arg, "--ready", StringComparison.OrdinalIgnoreCase))
                {
                    readyPath = value;
                }
            }

            return new UpdateRequest
            {
                ParentProcessId = parentProcessId,
                InstallerPath = installerPath,
                AppPath = appPath,
                TransactionPath = transactionPath,
                ReadyPath = readyPath,
                Silent = silent,
                UpdateMode = updateMode
            };
        }
    }
}
