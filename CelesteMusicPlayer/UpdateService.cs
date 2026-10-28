using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 一次更新走到哪一步。**状态是唯一真相**：界面不靠「下载按钮还在不在」猜进度，
    /// 也不靠「上次是不是点了下载」记手续。
    /// </summary>
    public enum UpdateStage
    {
        Idle,
        Checking,
        Available,
        Downloading,
        Verifying,
        ReadyToInstall,
        PreparingExit,
        Installing,
        VerifyingLaunch,
        Completed,
        Cancelled,
        Failed
    }

    /// <summary>
    /// 一次更新事务的落盘记录。写进 <c>%LOCALAPPDATA%\CelesteMusicPlayer\update\&lt;事务id&gt;\transaction.json</c>，
    /// 主程序退出后由更新助手读这份文件继续干活 —— 助手不解析命令行里的自由文本，只认这份记录。
    /// </summary>
    public sealed class UpdateTransaction
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
        public string Stage { get; set; } = "idle";
        public string CreatedUtc { get; set; } = string.Empty;
        public string UpdatedUtc { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public string PackagePath => string.IsNullOrEmpty(PackageFileName) || string.IsNullOrEmpty(DirectoryPath)
            ? string.Empty
            : Path.Combine(DirectoryPath, PackageFileName);

        /// <summary>不序列化：仅运行时用来拼路径（序列化进去会让助手看到一台机器上的绝对路径）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DirectoryPath { get; set; } = string.Empty;
    }

    /// <summary>给界面（网页 / 原生面板）看的一份快照。全部字段一次给全，不让界面自己拼状态。</summary>
    public sealed class UpdateStatus
    {
        public UpdateStage Stage { get; set; } = UpdateStage.Idle;
        public string CurrentVersion { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string LatestTag { get; set; } = string.Empty;
        public string PackageKind { get; set; } = string.Empty;
        public string ChecksumState { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string ReleaseUrl { get; set; } = string.Empty;
        public bool HasUpdate { get; set; }
        public bool PrereleaseIgnored { get; set; }
        public bool Stale { get; set; }
        public string LastKnownTag { get; set; } = string.Empty;
        public string LastKnownReleaseUrl { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long BytesTotal { get; set; }
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public string InstallDirectory { get; set; } = string.Empty;
        public string CheckedAt { get; set; } = string.Empty;

        public bool CanDownload => Stage == UpdateStage.Available && !string.IsNullOrEmpty(DownloadUrl) && ChecksumState == "verified";
        public bool CanCancel => Stage == UpdateStage.Downloading || Stage == UpdateStage.Verifying;
        public bool CanInstall => Stage == UpdateStage.ReadyToInstall;

        public string StageName => Stage switch
        {
            UpdateStage.Idle => "idle",
            UpdateStage.Checking => "checking",
            UpdateStage.Available => "available",
            UpdateStage.Downloading => "downloading",
            UpdateStage.Verifying => "verifying",
            UpdateStage.ReadyToInstall => "ready",
            UpdateStage.PreparingExit => "preparing",
            UpdateStage.Installing => "installing",
            UpdateStage.VerifyingLaunch => "verify-launch",
            UpdateStage.Completed => "completed",
            UpdateStage.Cancelled => "cancelled",
            _ => "failed"
        };

        public UpdateStatus Clone() => (UpdateStatus)MemberwiseClone();
    }

    /// <summary>
    /// 更新链路的唯一实现（下载 → 校验 → 交给助手安装）。原生面板与网页都只是它的视图。
    ///
    /// 三条硬规矩（对应方案 5.2）：
    /// 1. 下载先写 <c>.part</c>，哈希对上才原子改名成正式文件 —— 半截文件永远不会被当成安装包；
    /// 2. 每个事务一个独立目录，事务记录落盘，主程序退出后助手照着记录继续；
    /// 3. 不接受界面直接指定 exe 路径来执行 —— 能装的只有本服务自己下载并校验过的那一枚。
    /// </summary>
    public sealed class UpdateService
    {
        public static UpdateService Shared { get; } = new UpdateService();

        /// <summary>状态变了。可能在后台线程触发，界面自己负责切回 UI 线程。</summary>
        public event Action<UpdateStatus>? Changed;

        private readonly object _gate = new object();
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions { WriteIndented = true };
        private UpdateStatus _status = new UpdateStatus();
        private UpdateChecker.UpdateInfo? _candidate;
        private UpdateTransaction? _transaction;
        private CancellationTokenSource? _downloadCts;
        private long _lastRaisedTicks;

        private UpdateService()
        {
            try
            {
                _status.CurrentVersion = UpdateChecker.CurrentVersionText();
                LogLastInstallResult();
                RestorePendingTransaction();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateService.Init", caught);
            }
        }

        public UpdateStatus Current
        {
            get { lock (_gate) { return _status.Clone(); } }
        }

        /// <summary>事务根目录。所有下载与记录都关在这里，出问题只删这一棵树。</summary>
        public static string TransactionRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CelesteMusicPlayer",
            "update");

        // —— 检查 ——

        public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
        {
            SetStatus(status =>
            {
                status.Stage = UpdateStage.Checking;
                status.ErrorCode = string.Empty;
                status.ErrorMessage = string.Empty;
            }, force: true);

            UpdateChecker.UpdateCheckResult result;
            try
            {
                result = await UpdateChecker.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateService.Check", caught);
                return Fail("check", "检查更新时出现异常：" + caught.Message);
            }

            lock (_gate)
            {
                _candidate = result.Update;
                _status.CurrentVersion = result.CurrentVersion;
                _status.LatestTag = result.LatestTag ?? string.Empty;
                _status.PrereleaseIgnored = result.PrereleaseIgnored;
                _status.CheckedAt = result.CheckedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                _status.Stale = result.Status == UpdateCheckStatus.Failed && result.LastKnownUpdate != null;
                _status.LastKnownTag = result.LastKnownUpdate?.Tag ?? string.Empty;
                _status.LastKnownReleaseUrl = result.LastKnownUpdate?.ReleaseUrl ?? string.Empty;

                if (result.Status == UpdateCheckStatus.Available && result.Update != null)
                {
                    UpdateChecker.UpdateInfo info = result.Update;
                    _status.Stage = UpdateStage.Available;
                    _status.Version = info.Tag;
                    _status.PackageKind = info.PackageKindText;
                    _status.ChecksumState = ChecksumName(info.ChecksumState);
                    _status.DownloadUrl = info.SetupUrl ?? string.Empty;
                    _status.ReleaseUrl = info.ReleaseUrl ?? string.Empty;
                    _status.HasUpdate = true;
                    _status.ErrorCode = string.Empty;
                    _status.ErrorMessage = string.Empty;
                }
                else if (result.Status == UpdateCheckStatus.Available)
                {
                    // 有新版却没有可用安装包：只能指发布页，不假装能装。
                    _status.Stage = UpdateStage.Available;
                    _status.Version = result.Update?.Tag ?? result.LatestTag ?? string.Empty;
                    _status.HasUpdate = true;
                    _status.DownloadUrl = string.Empty;
                    _status.ChecksumState = string.Empty;
                }
                else if (result.Status == UpdateCheckStatus.UnsupportedPackage)
                {
                    _status.Stage = UpdateStage.Available;
                    _status.Version = result.Update?.Tag ?? result.LatestTag ?? string.Empty;
                    _status.HasUpdate = true;
                    _status.DownloadUrl = string.Empty;
                    _status.ChecksumState = string.Empty;
                    _status.ErrorCode = "unsupported";
                    _status.ErrorMessage = "这次发布没有适配当前安装方式的安装包。";
                }
                else if (result.Status == UpdateCheckStatus.Failed)
                {
                    _status.Stage = UpdateStage.Failed;
                    _status.HasUpdate = false;
                    _status.ErrorCode = result.ErrorCategory ?? "network";
                    _status.ErrorMessage = result.ErrorMessage ?? "检查更新失败。";
                }
                else
                {
                    _status.Stage = UpdateStage.Idle;
                    _status.HasUpdate = false;
                    _status.Version = string.Empty;
                    _status.DownloadUrl = string.Empty;
                }

                UpdateStatus snapshot = _status.Clone();
                Raise(snapshot, force: true);
                return snapshot;
            }
        }

        // —— 下载 ——

        /// <summary>
        /// 下载并校验。只能下「本次检查确认过、哈希可验证」的那一枚安装包；
        /// 缺哈希时直接拒绝，不给「先下下来再说」的口子。
        /// </summary>
        public async Task<UpdateStatus> DownloadAsync()
        {
            UpdateChecker.UpdateInfo? info;
            string currentVersion;
            lock (_gate)
            {
                if (_status.Stage == UpdateStage.Downloading || _status.Stage == UpdateStage.Verifying)
                    return _status.Clone();
                info = _candidate;
                currentVersion = _status.CurrentVersion;

                if (info == null || string.IsNullOrEmpty(info.SetupUrl))
                    return Fail("no-package", "没有可下载的安装包，请到发布页手动下载。");
                if (info.ChecksumState != UpdateChecksumState.Verified || string.IsNullOrEmpty(info.ExpectedSha256))
                    return Fail("no-checksum", "这次发布缺少可用的安装包哈希清单，无法校验完整性，请到发布页手动下载。");
                if (string.Equals(info.Tag, currentVersion, StringComparison.OrdinalIgnoreCase))
                    return Fail("same-version", "当前已经是这个版本，不需要下载。");
            }

            UpdateTransaction transaction = CreateTransaction(info, currentVersion);
            using CancellationTokenSource cts = new CancellationTokenSource();
            lock (_gate) { _downloadCts = cts; }

            SetStatus(status =>
            {
                status.Stage = UpdateStage.Downloading;
                status.Version = info!.Tag;
                status.TransactionId = transaction.Id;
                status.DownloadUrl = info.SetupUrl!;
                status.InstallDirectory = transaction.InstallDirectory;
                status.BytesReceived = 0;
                status.BytesTotal = 0;
                status.ErrorCode = string.Empty;
                status.ErrorMessage = string.Empty;
            }, force: true);

            string partPath = transaction.PackagePath + ".part";
            string finalPath = transaction.PackagePath;
            bool completed = false;

            try
            {
                // 共享实例，**不能 using**：Dispose 会把进程内所有下载的 HttpClient 一起废掉。
                // User-Agent 由 HttpClients.Create 统一给过，这里不再二次添加（重复头会让请求被判非法）。
                HttpClient http = HttpClients.LongRunning;

                using HttpResponseMessage response = await http
                    .GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long? total = response.Content.Headers.ContentLength;
                await using (Stream stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                await using (FileStream file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                        received += read;
                        ReportProgress(received, total ?? 0);
                    }
                }

                SetStatus(status =>
                {
                    status.Stage = UpdateStage.Verifying;
                    status.BytesReceived = new FileInfo(partPath).Length;
                }, force: true);

                string actual = await ComputeSha256Async(partPath, cts.Token).ConfigureAwait(false);
                if (!string.Equals(actual, info.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partPath);
                    StartupLog.Write("[update] 哈希不符 expected=" + info.ExpectedSha256 + " actual=" + actual);
                    return Fail("hash-mismatch", "安装包哈希与官方发布不一致，已拒绝安装并删除文件。");
                }

                // 校验通过才改名：任何时刻磁盘上要么是完整的正式文件，要么什么都不是。
                if (File.Exists(finalPath)) TryDelete(finalPath);
                File.Move(partPath, finalPath);
                completed = true;

                transaction.Stage = UpdateStage.ReadyToInstall.ToString();
                transaction.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                Save(transaction);

                lock (_gate) { _transaction = transaction; }

                SetStatus(status =>
                {
                    status.Stage = UpdateStage.ReadyToInstall;
                    status.BytesReceived = new FileInfo(finalPath).Length;
                    status.BytesTotal = status.BytesReceived;
                    status.TransactionId = transaction.Id;
                    status.ErrorCode = string.Empty;
                    status.ErrorMessage = string.Empty;
                }, force: true);

                return Current;
            }
            catch (OperationCanceledException)
            {
                if (!completed)
                {
                    TryDelete(partPath);
                    // 取消后连事务目录一起收掉：留一个没有安装包的空目录，
                    // 下次启动扫描时还得逐个打开判断，纯属垃圾。
                    TryDeleteDirectory(transaction.DirectoryPath);
                    lock (_gate) { _transaction = null; }
                }

                SetStatus(status =>
                {
                    status.Stage = UpdateStage.Cancelled;
                    status.BytesReceived = 0;
                    status.ErrorCode = "cancelled";
                    status.ErrorMessage = "下载已取消。";
                }, force: true);
                return Current;
            }
            catch (Exception caught)
            {
                if (!completed) TryDelete(partPath);
                StartupLog.WriteException("UpdateService.Download", caught);
                string reason = caught switch
                {
                    HttpRequestException => "网络中断或下载地址失效。",
                    TaskCanceledException => "连接超时或网络中断。",
                    IOException => "安装包文件被占用（常见于安全软件正在扫描）。",
                    UnauthorizedAccessException => "没有写入更新目录的权限。",
                    _ => caught.Message
                };
                return Fail("download", "下载失败：" + reason);
            }
            finally
            {
                lock (_gate) { _downloadCts = null; }
                CleanupOldTransactions(transaction.Id);
            }
        }

        /// <summary>取消正在进行的下载。取消不退出播放、不删已校验好的包。</summary>
        public void CancelDownload()
        {
            CancellationTokenSource? cts;
            lock (_gate) { cts = _downloadCts; }
            try { cts?.Cancel(); }
            catch (Exception caught) { StartupLog.WriteException("UpdateService.Cancel", caught); }
        }

        // —— 安装 ——

        /// <summary>
        /// 准备退出并把安装交给助手。返回 true 表示**助手已握手就绪**，调用方可以退出主程序；
        /// 返回 false 表示交接没成功，调用方必须留下（否则用户会既没装成也没程序）。
        /// </summary>
        public async Task<bool> BeginInstallAsync(Func<Task>? flushState)
        {
            UpdateTransaction? transaction;
            lock (_gate) { transaction = _transaction; }

            if (transaction == null || !File.Exists(transaction.PackagePath))
            {
                Fail("no-package", "没有已校验的安装包，无法安装。");
                return false;
            }

            SetStatus(status => { status.Stage = UpdateStage.PreparingExit; }, force: true);

            try
            {
                if (flushState != null)
                    await flushState().ConfigureAwait(true);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateService.FlushState", caught);
                // 落盘失败不阻断安装：设置本身每次改动就存过了，这里只是最后补一刀。
            }

            string helperDir = PrepareHelper();
            if (string.IsNullOrEmpty(helperDir))
            {
                Fail("no-helper", "更新助手缺失，无法自动安装。已保留安装包，可手动运行。");
                return false;
            }

            transaction.Stage = UpdateStage.Installing.ToString();
            transaction.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Save(transaction);

            string readyFile = Path.Combine(helperDir, transaction.Id + ".ready");
            TryDelete(readyFile);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.Combine(helperDir, "CelesteUpdater.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = helperDir
                };
                psi.ArgumentList.Add("--transaction");
                psi.ArgumentList.Add(Path.Combine(transaction.DirectoryPath, "transaction.json"));
                psi.ArgumentList.Add("--pid");
                psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--ready");
                psi.ArgumentList.Add(readyFile);
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateService.LaunchHelper", caught);
                Fail("helper-launch", "无法启动更新助手：" + caught.Message);
                return false;
            }

            // 握手：助手确认参数可用后写 ready 文件，我们才退出。
            // 「Process.Start 成功」不等于交接成功 —— 助手可能立刻因为包不存在而退出。
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (File.Exists(readyFile))
                {
                    SetStatus(status => { status.Stage = UpdateStage.Installing; }, force: true);
                    return true;
                }

                if (attempt == 60)
                {
                    // 6 秒还没握手：多半是助手起不来（被杀软拦/缺运行时），再等也无意义。
                    break;
                }

                await Task.Delay(100).ConfigureAwait(true);
            }

            Fail("helper-handshake", "更新助手没有回应，安装未开始。安装包已保留，可手动运行。");
            return false;
        }

        /// <summary>用户主动放弃这次下载/安装（例如装了别的版本）。清掉事务目录。</summary>
        public void Discard()
        {
            lock (_gate)
            {
                if (_transaction != null)
                {
                    TryDeleteDirectory(_transaction.DirectoryPath);
                    _transaction = null;
                }

                _status = new UpdateStatus
                {
                    Stage = UpdateStage.Idle,
                    CurrentVersion = UpdateChecker.CurrentVersionText()
                };
            }

            Raise(Current, force: true);
        }

        /// <summary>把当前状态推送出去（外壳重新连上时补一次）。</summary>
        public void Push() => Raise(Current, force: true);

        // —— 内部 ——

        private UpdateStatus Fail(string code, string message)
        {
            lock (_gate)
            {
                _status.Stage = UpdateStage.Failed;
                _status.ErrorCode = code;
                _status.ErrorMessage = message;
                _status.BytesReceived = 0;
            }

            StartupLog.Write("[update] 失败：" + code + " " + message);
            return Raise(Current, force: true) ?? Current;
        }

        private UpdateTransaction CreateTransaction(UpdateChecker.UpdateInfo info, string currentVersion)
        {
            string id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                        + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            string dir = Path.Combine(TransactionRoot, id);
            Directory.CreateDirectory(dir);

            string fileName = "CelesteMusicPlayer-Setup-" + info.Tag.TrimStart('v', 'V') + ".exe";
            try
            {
                string fromUrl = Path.GetFileName(new Uri(info.SetupUrl!).AbsolutePath);
                if (!string.IsNullOrWhiteSpace(fromUrl)) fileName = fromUrl;
            }
            catch (Exception caught) { StartupLog.WriteException("UpdateService.PackageName", caught); }

            var transaction = new UpdateTransaction
            {
                Id = id,
                Version = info.Tag,
                PackageKind = info.PackageKind,
                ExpectedSha256 = info.ExpectedSha256 ?? string.Empty,
                DownloadUrl = info.SetupUrl ?? string.Empty,
                PackageFileName = fileName,
                InstallDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                RestartPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CelesteMusicPlayer.exe"),
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                DirectoryPath = dir
            };

            Save(transaction);
            return transaction;
        }

        private void Save(UpdateTransaction transaction)
        {
            try
            {
                Directory.CreateDirectory(transaction.DirectoryPath);
                File.WriteAllText(
                    Path.Combine(transaction.DirectoryPath, "transaction.json"),
                    JsonSerializer.Serialize(transaction, _json));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateService.SaveTransaction", caught);
            }
        }

        /// <summary>
        /// 更新助手装完会留一份结果文件。启动时读走并记日志 —— 用户报「更新完没反应」时，
        /// 这是唯一能证明「安装包跑了没 / 装到哪一步崩的」的证据。读完就删，只报一次。
        /// </summary>
        private static void LogLastInstallResult()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer",
                    "update-last-result.json");
                if (!File.Exists(path)) return;

                string text = File.ReadAllText(path);
                File.Delete(path);

                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                bool succeeded = root.TryGetProperty("Succeeded", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;
                string version = root.TryGetProperty("Version", out JsonElement v) ? v.GetString() ?? string.Empty : string.Empty;
                string errorCode = root.TryGetProperty("ErrorCode", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
                string errorMessage = root.TryGetProperty("ErrorMessage", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
                bool restarted = root.TryGetProperty("Restarted", out JsonElement r) && r.ValueKind == JsonValueKind.True;

                if (succeeded)
                {
                    StartupLog.Write($"[update] 上次安装成功（目标 {version}），主程序{(restarted ? "已自动重启" : "未能自动重启")}。");
                }
                else
                {
                    StartupLog.Write($"[update] 上次安装失败：{errorCode} {errorMessage}（目标 {version}）。安装包与事务记录已保留。");
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateService.Result", caught);
            }
        }

        /// <summary>上次已经下好但没装成（用户重启了程序）：恢复成「可以安装」，不用重下。</summary>
        private void RestorePendingTransaction()
        {
            if (!Directory.Exists(TransactionRoot)) return;

            string[] dirs = Directory.GetDirectories(TransactionRoot);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            for (int index = dirs.Length - 1; index >= 0; index--)
            {
                string record = Path.Combine(dirs[index], "transaction.json");
                if (!File.Exists(record)) continue;

                UpdateTransaction? transaction;
                try
                {
                    transaction = JsonSerializer.Deserialize<UpdateTransaction>(File.ReadAllText(record));
                }
                catch (Exception caught)
                {
                    StartupLog.WriteException("UpdateService.RestoreTransaction", caught);
                    continue;
                }

                if (transaction == null) continue;
                transaction.DirectoryPath = dirs[index];
                if (!File.Exists(transaction.PackagePath)) continue;

                lock (_gate)
                {
                    _transaction = transaction;
                    _status.Stage = UpdateStage.ReadyToInstall;
                    _status.Version = transaction.Version;
                    _status.PackageKind = string.Equals(transaction.PackageKind, UpdateChecker.VariantSelfContained, StringComparison.OrdinalIgnoreCase) ? "自包含版" : "框架依赖版";
                    _status.TransactionId = transaction.Id;
                    _status.InstallDirectory = transaction.InstallDirectory;
                    _status.BytesReceived = new FileInfo(transaction.PackagePath).Length;
                    _status.BytesTotal = _status.BytesReceived;
                    _status.CheckedAt = DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    StartupLog.Write("[update] 恢复上次未安装的包 " + transaction.Version + " → " + transaction.PackagePath);
                }

                return;
            }
        }

        private static void CleanupOldTransactions(string keepId)
        {
            try
            {
                if (!Directory.Exists(TransactionRoot)) return;
                foreach (string dir in Directory.GetDirectories(TransactionRoot))
                {
                    if (string.Equals(Path.GetFileName(dir), keepId, StringComparison.OrdinalIgnoreCase)) continue;
                    TryDeleteDirectory(dir);
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateService.Cleanup", caught);
            }
        }

        private static string PrepareHelper()
        {
            try
            {
                string source = AppContext.BaseDirectory;
                string helperExe = Path.Combine(source, "CelesteUpdater.exe");
                if (!File.Exists(helperExe)) return string.Empty;

                string helperDir = Path.Combine(
                    TransactionRoot,
                    "helper-" + DateTimeOffset.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                    + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(helperDir);

                foreach (string file in Directory.EnumerateFiles(source, "CelesteUpdater.*", SearchOption.TopDirectoryOnly))
                    File.Copy(file, Path.Combine(helperDir, Path.GetFileName(file)), overwrite: true);

                return File.Exists(Path.Combine(helperDir, "CelesteUpdater.exe")) ? helperDir : string.Empty;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateService.PrepareHelper", caught);
                return string.Empty;
            }
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static string ChecksumName(UpdateChecksumState state) => state switch
        {
            UpdateChecksumState.Verified => "verified",
            UpdateChecksumState.Unverifiable => "unverifiable",
            _ => "unpublished"
        };

        private void ReportProgress(long received, long total)
        {
            lock (_gate)
            {
                _status.BytesReceived = received;
                _status.BytesTotal = total;
            }

            Raise(Current, force: false);
        }

        private void SetStatus(Action<UpdateStatus> mutate, bool force)
        {
            UpdateStatus snapshot;
            lock (_gate)
            {
                mutate(_status);
                snapshot = _status.Clone();
            }

            Raise(snapshot, force);
        }

        /// <summary>进度按 100ms 节流：下载 80MB 会读上万次，每次都跨进程推给网页会拖慢下载本身。</summary>
        private UpdateStatus? Raise(UpdateStatus snapshot, bool force)
        {
            if (!force)
            {
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastRaisedTicks) < 100) return snapshot;
                Interlocked.Exchange(ref _lastRaisedTicks, now);
            }
            else
            {
                Interlocked.Exchange(ref _lastRaisedTicks, Environment.TickCount64);
            }

            try { Changed?.Invoke(snapshot); }
            catch (Exception caught) { StartupLog.WriteException("UpdateService.Changed", caught); }
            return snapshot;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception caught) { StartupLog.WriteException("UpdateService.Delete", caught); }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception caught) { StartupLog.WriteException("UpdateService.DeleteDirectory", caught); }
        }
    }
}
