using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>一次检查更新的结论。失败和「已是最新」必须能分开，否则界面只能靠猜。</summary>
    public enum UpdateCheckStatus
    {
        /// <summary>确认没有新版本。</summary>
        UpToDate,

        /// <summary>有新版本，并且找到了适配当前安装方式的安装包。</summary>
        Available,

        /// <summary>检查没完成：网络、超时、接口异常、版本号看不懂。</summary>
        Failed,

        /// <summary>确实有新版本，但这次发布没有适配当前安装方式/架构的安装包。</summary>
        UnsupportedPackage
    }

    /// <summary>安装包哈希清单对这枚安装包的可验证性。</summary>
    public enum UpdateChecksumState
    {
        /// <summary>发布里没有 SHA256SUMS.txt。</summary>
        NotPublished,

        /// <summary>清单里有这一行，且哈希格式合法。</summary>
        Verified,

        /// <summary>清单存在，但没有这枚安装包的行，或哈希格式不合法。</summary>
        Unverifiable
    }

    /// <summary>
    /// 与界面无关的「检查更新」核心逻辑：读取 GitHub Releases latest、比较版本号、挑安装包。
    /// 设置窗口「关于」面板、网页设置页、启动时自动检查共用这一份，避免多份逻辑漂移。
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>GitHub Releases latest 接口（与关于面板一致）。</summary>
        public const string GithubReleasesApi =
            "https://api.github.com/repos/Celeste-114514/CelesteMusicPlayer/releases/latest";

        /// <summary>单次检查的总时间预算（含拉取哈希清单）。</summary>
        private static readonly TimeSpan CheckBudget = TimeSpan.FromSeconds(15);

        /// <summary>最近一次成功确认的新版本；检查失败时保持上一次结果，供界面显示「上次发现」。</summary>
        public static UpdateInfo? LatestAvailable { get; private set; }

        /// <summary>最近一次检查的结论（成功或失败都记）。未检查过为 null。</summary>
        public static UpdateCheckResult? LastResult { get; private set; }

        /// <summary>发现的新版本（仅当比当前版本新才非空）。</summary>
        public sealed class UpdateInfo
        {
            public UpdateInfo(
                string tag,
                string? setupUrl,
                string packageKind,
                string? expectedSha256,
                UpdateChecksumState checksumState,
                string? releaseUrl,
                bool isPrerelease)
            {
                Tag = tag;
                SetupUrl = setupUrl;
                PackageKind = packageKind;
                ExpectedSha256 = expectedSha256;
                ChecksumState = checksumState;
                ReleaseUrl = releaseUrl;
                IsPrerelease = isPrerelease;
            }

            /// <summary>版本 tag，如 "v26.9.14"。</summary>
            public string Tag { get; }

            /// <summary>安装包（CelesteMusicPlayer-Setup-*.exe）下载地址；无匹配资产为 null。</summary>
            public string? SetupUrl { get; }

            /// <summary>所选安装包的变体："sc"=自包含，"fd"=框架依赖。</summary>
            public string PackageKind { get; }

            /// <summary>所选安装包的期望 SHA-256（来自 SHA256SUMS.txt，小写十六进制）；取不到为 null。</summary>
            public string? ExpectedSha256 { get; }

            /// <summary>这枚安装包的哈希是否可验证。缺清单/缺条目时界面要提示手动下载，不能假装校验过。</summary>
            public UpdateChecksumState ChecksumState { get; }

            /// <summary>Release 页面地址（人工下载入口）。</summary>
            public string? ReleaseUrl { get; }

            /// <summary>这一版是否为预发布。</summary>
            public bool IsPrerelease { get; }

            /// <summary>供 UI 展示的包型中文名。</summary>
            public string PackageKindText => PackageKind == UpdateVariants.SelfContained ? "自包含版" : "框架依赖版";
        }

        /// <summary>一次检查的完整结论。</summary>
        public sealed class UpdateCheckResult
        {
            internal UpdateCheckResult(UpdateCheckStatus status, string currentVersion, DateTime checkedAt)
            {
                Status = status;
                CurrentVersion = currentVersion;
                CheckedAt = checkedAt;
            }

            public UpdateCheckStatus Status { get; }

            public string CurrentVersion { get; }

            public DateTime CheckedAt { get; }

            /// <summary>服务端返回的 tag（即使解析失败或因为是预览版被忽略，也带回来便于排查）。</summary>
            public string? LatestTag { get; internal set; }

            /// <summary>发现的新版本；仅 Available / UnsupportedPackage 时有值。</summary>
            public UpdateInfo? Update { get; internal set; }

            /// <summary>失败时的分类：network / timeout / http / rate-limit / json / version-parse / unknown。</summary>
            public string? ErrorCategory { get; internal set; }

            /// <summary>失败原因（已转成能直接给人看的中文）。</summary>
            public string? ErrorMessage { get; internal set; }

            /// <summary>最新发布是预览版、稳定通道没收下它。</summary>
            public bool PrereleaseIgnored { get; internal set; }

            /// <summary>失败时，上一次成功确认过的新版本（界面可以说明「上次发现 X」）。</summary>
            public UpdateInfo? LastKnownUpdate { get; internal set; }

            public bool HasUpdate => Status == UpdateCheckStatus.Available || Status == UpdateCheckStatus.UnsupportedPackage;
        }

        /// <summary>当前程序集版本号（如 "26.9.13" 或 "26.9.13.2"）。</summary>
        public static string CurrentVersionText()
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null)
            {
                return "未知";
            }

            // 完整输出 4 段版本号（含修订号），否则 26.9.13.2 会显示成 26.9.13，
            // 与最初的 26.9.13 无法区分，用户会误以为「没更新」。
            if (v.Revision > 0)
            {
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }

            return $"{v.Major}.{v.Minor}.{v.Build}";
        }

        /// <summary>
        /// 比较两个版本字符串（支持 v 前缀与 -beta/-rc 后缀）。
        /// 返回值：&lt;0 / 0 / &gt;0 表示 a 比 b 旧/相等/新。任一方解析失败时退回按 0 处理（保持旧行为）。
        /// </summary>
        public static int CompareVersionStrings(string a, string b)
        {
            ReleaseVersion? left = ParseOrZero(a);
            ReleaseVersion? right = ParseOrZero(b);
            return left!.CompareTo(right);
        }

        private static readonly ReleaseVersion ZeroVersion = CreateZeroVersion();

        private static ReleaseVersion CreateZeroVersion()
        {
            ReleaseVersion.TryParse("0.0.0", out ReleaseVersion? zero);
            return zero!;
        }

        private static ReleaseVersion ParseOrZero(string raw)
            => ReleaseVersion.TryParse(raw, out ReleaseVersion? parsed) && parsed != null ? parsed : ZeroVersion;

        /// <summary>安装变体常量：sc=自包含，fd=框架依赖。</summary>
        public const string VariantSelfContained = UpdateVariants.SelfContained;
        public const string VariantFrameworkDependent = UpdateVariants.FrameworkDependent;

        /// <summary>
        /// 读取当前安装变体：安装目录下 install-variant.txt（NSIS 安装时写入，内容 "fd"/"sc"）。
        /// 文件不存在（旧安装/绿色版）时按框架依赖处理，与历史行为一致。
        /// </summary>
        public static string InstalledVariant()
        {
            try
            {
                string marker = Path.Combine(AppContext.BaseDirectory, "install-variant.txt");
                if (File.Exists(marker))
                {
                    string v = File.ReadAllText(marker).Trim();
                    if (string.Equals(v, VariantSelfContained, StringComparison.OrdinalIgnoreCase))
                    {
                        return VariantSelfContained;
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateChecker.InstalledVariant", caught);
            }

            return VariantFrameworkDependent;
        }

        /// <summary>当前进程架构（x64 / x86 / arm64），用于排除装不上的包。</summary>
        public static string CurrentArchitecture()
            => UpdateAssetSelector.NormalizeArch(RuntimeInformation.ProcessArchitecture.ToString());

        /// <summary>/ 当前构建是否稳定通道：装配信息里带 "-" 的预发布构建才收预览版。</summary>
        public static bool StableChannel
        {
            get
            {
                string? informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                return informational == null || informational.IndexOf('-') < 0;
            }
        }

        private sealed class ReleaseEnvelope
        {
            public string Tag = string.Empty;
            public string? ReleaseUrl;
            public bool Prerelease;
            public string? SumsUrl;
            public List<UpdateAsset> Assets = new();
        }

        /// <summary>抓取失败时带分类抛出，调用方统一转成 Failed 结论。</summary>
        private sealed class UpdateFetchException : Exception
        {
            public UpdateFetchException(string category, string message)
                : base(message)
                => Category = category;

            public string Category { get; }
        }

        /// <summary>
        /// 拉取 Release 元数据（tag、资产、哈希清单地址）。失败抛 <see cref="UpdateFetchException"/>。
        /// </summary>
        private static async Task<ReleaseEnvelope> FetchReleaseAsync(CancellationToken cancellationToken)
        {
            HttpClient http = HttpClients.Default;
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(GithubReleasesApi, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                throw new UpdateFetchException("timeout", "连接 GitHub 超时，请稍后重试。");
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateFetchException("network", "连不上 GitHub（" + ex.Message + "），请检查网络或代理。");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden
                    && response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? remaining)
                    && string.Join(",", remaining).Trim() == "0")
                {
                    throw new UpdateFetchException("rate-limit", "GitHub 接口调用次数已用完，请过一会儿再试。");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new UpdateFetchException(
                        "http",
                        $"GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}，请稍后重试。");
                }

                string json;
                try
                {
                    json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                try
                {
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;
                    var envelope = new ReleaseEnvelope();
                    if (root.TryGetProperty("tag_name", out JsonElement tagEl) && tagEl.ValueKind == JsonValueKind.String)
                    {
                        envelope.Tag = tagEl.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("html_url", out JsonElement htmlEl) && htmlEl.ValueKind == JsonValueKind.String)
                    {
                        envelope.ReleaseUrl = htmlEl.GetString();
                    }

                    if (root.TryGetProperty("prerelease", out JsonElement preEl)
                        && (preEl.ValueKind == JsonValueKind.True || preEl.ValueKind == JsonValueKind.False))
                    {
                        envelope.Prerelease = preEl.ValueKind == JsonValueKind.True;
                    }

                    if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement asset in assets.EnumerateArray())
                        {
                            string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                            string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                            {
                                continue;
                            }

                            if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                            {
                                envelope.SumsUrl ??= url;
                                continue;
                            }

                            envelope.Assets.Add(new UpdateAsset(name, url));
                        }
                    }

                    return envelope;
                }
                catch (JsonException ex)
                {
                    throw new UpdateFetchException("json", "GitHub 返回的数据看不懂（" + ex.Message + "）。");
                }
            }
        }

        /// <summary>
        /// 读取哈希清单文本；失败返回 null（只影响「能否校验」，不影响「有没有新版」）。
        /// 清单很小，单独再给 5 秒预算：超时按「无法校验」处理，不让它把已经确认的新版本一起判成失败。
        /// </summary>
        private static async Task<string?> FetchChecksumTextAsync(string url, CancellationToken cancellationToken)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                return await HttpClients.Default.GetStringAsync(url, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateChecker.FetchSha256Sums", caught);
                return null;
            }
        }

        private static string? FileNameOf(string url)
        {
            try
            {
                return Path.GetFileName(new Uri(url).AbsolutePath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 检查更新（拉取 + 比较 + 选包 + 校验清单），返回明确结论。
        /// 失败不会清掉 <see cref="LatestAvailable"/>，「已是最新」才会。
        /// </summary>
        public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            string current = CurrentVersionText();
            DateTime now = DateTime.Now;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(CheckBudget);

            try
            {
                ReleaseEnvelope release = await FetchReleaseAsync(budget.Token).ConfigureAwait(false);
                if (!ReleaseVersion.TryParse(release.Tag, out ReleaseVersion? latest) || latest == null)
                {
                    return Fail("version-parse", $"发布版本号无法识别：{(string.IsNullOrEmpty(release.Tag) ? "(空)" : release.Tag)}", release);
                }

                if (!ReleaseVersion.TryParse(current, out ReleaseVersion? installed) || installed == null)
                {
                    return Fail("version-parse", $"本机版本号无法识别：{current}", release);
                }

                int comparison = latest.CompareTo(installed);
                if (comparison <= 0)
                {
                    // 服务端明确说没有更新的版本：这才是真正可以清掉「已知新版本」的时机。
                    LatestAvailable = null;
                    var upToDate = new UpdateCheckResult(UpdateCheckStatus.UpToDate, current, now) { LatestTag = release.Tag };
                    LastResult = upToDate;
                    return upToDate;
                }

                bool isPrerelease = release.Prerelease || latest.IsPrerelease;
                if (isPrerelease && StableChannel)
                {
                    // 稳定通道不收预览版：状态仍是「已是最新」，但要说清楚发现过预览版。
                    var ignored = new UpdateCheckResult(UpdateCheckStatus.UpToDate, current, now)
                    {
                        LatestTag = release.Tag,
                        PrereleaseIgnored = true
                    };
                    LastResult = ignored;
                    return ignored;
                }

                UpdateAsset? setup = UpdateAssetSelector.SelectSetup(release.Assets, InstalledVariant(), CurrentArchitecture());
                if (setup == null)
                {
                    var missing = new UpdateInfo(
                        release.Tag,
                        null,
                        InstalledVariant(),
                        null,
                        UpdateChecksumState.NotPublished,
                        release.ReleaseUrl,
                        isPrerelease);
                    LatestAvailable = missing;
                    var unsupported = new UpdateCheckResult(UpdateCheckStatus.UnsupportedPackage, current, now)
                    {
                        LatestTag = release.Tag,
                        Update = missing
                    };
                    LastResult = unsupported;
                    return unsupported;
                }

                string? expected = null;
                UpdateChecksumState checksum = UpdateChecksumState.NotPublished;
                if (!string.IsNullOrEmpty(release.SumsUrl))
                {
                    checksum = UpdateChecksumState.Unverifiable;
                    string? text = await FetchChecksumTextAsync(release.SumsUrl, budget.Token).ConfigureAwait(false);
                    string? fileName = FileNameOf(setup.Url);
                    if (text != null && fileName != null
                        && ChecksumManifest.TryFind(text, fileName, out string? hash) == ChecksumLookup.Found)
                    {
                        expected = hash;
                        checksum = UpdateChecksumState.Verified;
                    }
                }

                var info = new UpdateInfo(release.Tag, setup.Url, InstalledVariant(), expected, checksum, release.ReleaseUrl, isPrerelease);
                LatestAvailable = info;
                var available = new UpdateCheckResult(UpdateCheckStatus.Available, current, now)
                {
                    LatestTag = release.Tag,
                    Update = info
                };
                LastResult = available;
                return available;
            }
            catch (UpdateFetchException ex)
            {
                return Fail(ex.Category, ex.Message, null);
            }
            catch (OperationCanceledException)
            {
                return Fail("timeout", "检查更新超时或已取消，请稍后重试。", null);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("UpdateChecker.Check", ex);
                return Fail("unknown", "检查更新时出现意外问题：" + ex.Message, null);
            }

            UpdateCheckResult Fail(string category, string message, ReleaseEnvelope? release)
            {
                var failed = new UpdateCheckResult(UpdateCheckStatus.Failed, current, now)
                {
                    LatestTag = release?.Tag,
                    ErrorCategory = category,
                    ErrorMessage = message,
                    // 失败时保留上次成功确认的新版本，不能让一次断网把「有新版本」擦掉。
                    LastKnownUpdate = LatestAvailable
                };
                LastResult = failed;
                return failed;
            }
        }

        /// <summary>
        /// 只拉取「最新发布长什么样」，不比较版本。给旧原生关于面板用：它们自己比较并展示。
        /// 失败返回 null（旧面板据此显示「检查更新失败」）。
        /// </summary>
        public static async Task<UpdateInfo?> FetchLatestAsync()
        {
            using var budget = new CancellationTokenSource(CheckBudget);
            try
            {
                ReleaseEnvelope release = await FetchReleaseAsync(budget.Token).ConfigureAwait(false);
                bool isPrerelease = release.Prerelease
                    || (ReleaseVersion.TryParse(release.Tag, out ReleaseVersion? parsed) && parsed != null && parsed.IsPrerelease);
                UpdateAsset? setup = UpdateAssetSelector.SelectSetup(release.Assets, InstalledVariant(), CurrentArchitecture());

                string? expected = null;
                UpdateChecksumState checksum = UpdateChecksumState.NotPublished;
                if (setup != null && !string.IsNullOrEmpty(release.SumsUrl))
                {
                    checksum = UpdateChecksumState.Unverifiable;
                    string? text = await FetchChecksumTextAsync(release.SumsUrl, budget.Token).ConfigureAwait(false);
                    string? fileName = FileNameOf(setup.Url);
                    if (text != null && fileName != null
                        && ChecksumManifest.TryFind(text, fileName, out string? hash) == ChecksumLookup.Found)
                    {
                        expected = hash;
                        checksum = UpdateChecksumState.Verified;
                    }
                }

                return new UpdateInfo(release.Tag, setup?.Url, InstalledVariant(), expected, checksum, release.ReleaseUrl, isPrerelease);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateChecker.FetchLatest", caught);
                return null;
            }
        }

        /// <summary>
        /// 兼容旧调用：返回发现的新版本，没有或失败都返回 null。
        /// 新代码请用 <see cref="CheckAsync"/>，能区分「已是最新」和「检查失败」。
        /// </summary>
        public static async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            UpdateCheckResult result = await CheckAsync().ConfigureAwait(false);
            return result.HasUpdate ? result.Update : null;
        }
    }
}
