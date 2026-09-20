using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 与界面无关的「检查更新」核心逻辑：读取 GitHub Releases latest、比较版本号。
    /// 设置窗口「关于」面板的手动检查，与启动时自动检查，共用这一份，避免两份逻辑漂移。
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>GitHub Releases latest 接口（与关于面板一致）。</summary>
        public const string GithubReleasesApi =
            "https://api.github.com/repos/Celeste-114514/CelesteMusicPlayer/releases/latest";

        /// <summary>最近一次发现的新版本（仅当比当前版本新才非空）。关于面板据此直接展示状态，不必等用户手动点检查。</summary>
        public sealed class UpdateInfo
        {
            public UpdateInfo(string tag, string? setupUrl, string packageKind, string? expectedSha256)
            {
                Tag = tag;
                SetupUrl = setupUrl;
                PackageKind = packageKind;
                ExpectedSha256 = expectedSha256;
            }

            /// <summary>版本 tag，如 "v26.9.14"。</summary>
            public string Tag { get; }

            /// <summary>安装包（Setup-*.exe）下载地址；无匹配资产为 null。</summary>
            public string? SetupUrl { get; }

            /// <summary>所选安装包的变体："sc"=自包含，"fd"=框架依赖。</summary>
            public string PackageKind { get; }

            /// <summary>
            /// 所选安装包的期望 SHA-256（来自 release 的 SHA256SUMS.txt，小写十六进制）。
            /// 取不到为 null，此时应用放弃校验直接安装，不阻断更新。
            /// </summary>
            public string? ExpectedSha256 { get; }

            /// <summary>供 UI 展示的包型中文名。</summary>
            public string PackageKindText => PackageKind == "sc" ? "自包含版" : "框架依赖版";
        }

        /// <summary>启动期自动检查发现的新版本；未发现有更新时为 null。</summary>
        public static UpdateInfo? LatestAvailable { get; private set; }

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
        /// 返回值：&lt;0 / 0 / &gt;0 表示 a 比 b 旧/相等/新。
        /// </summary>
        public static int CompareVersionStrings(string a, string b)
            => ParseLoose(a).CompareTo(ParseLoose(b));

        private static Version ParseLoose(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Version(0, 0, 0, 0);
            }

            string trimmed = raw.Trim().TrimStart('v', 'V');
            int dash = trimmed.IndexOf('-');
            if (dash >= 0)
            {
                trimmed = trimmed.Substring(0, dash);
            }

            if (!Version.TryParse(trimmed, out Version? parsed) || parsed == null)
            {
                return new Version(0, 0, 0, 0);
            }

            // 规范化到 4 段、缺失段补 0。Version 的 -1 会被 CompareTo 当成「更小」，
            // 不补零会导致 26.9.13（Revision=-1）被误判成比 26.9.13.1「更旧」。
            int major = parsed.Major < 0 ? 0 : parsed.Major;
            int minor = parsed.Minor < 0 ? 0 : parsed.Minor;
            int build = parsed.Build < 0 ? 0 : parsed.Build;
            int revision = parsed.Revision < 0 ? 0 : parsed.Revision;
            return new Version(major, minor, build, revision);
        }

        /// <summary>安装变体常量：sc=自包含，fd=框架依赖。</summary>
        public const string VariantSelfContained = "sc";
        public const string VariantFrameworkDependent = "fd";

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

        public static async Task<UpdateInfo?> FetchLatestAsync()
        {
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteMusicPlayer/" + CurrentVersionText());
                string json = await http.GetStringAsync(GithubReleasesApi);
                using var doc = JsonDocument.Parse(json);

                string? tag = null;
                if (doc.RootElement.TryGetProperty("tag_name", out JsonElement tagEl) && tagEl.ValueKind == JsonValueKind.String)
                {
                    tag = tagEl.GetString();
                }

                string variant = InstalledVariant();

                string? fdUrl = null;        // 框架依赖安装包：Setup-<ver>.exe（不含 -SC-）
                string? scUrl = null;        // 自包含安装包：Setup-SC-<ver>.exe
                string? fallbackUrl = null;  // 任意 .exe 资产，最后兜底
                string? sumsUrl = null;      // SHA256SUMS.txt

                if (doc.RootElement.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
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
                            sumsUrl ??= url;
                            continue;
                        }

                        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        fallbackUrl ??= url;

                        bool isSetup = name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Install", StringComparison.OrdinalIgnoreCase);
                        if (!isSetup)
                        {
                            continue;
                        }

                        if (name.Contains("-SC-", StringComparison.OrdinalIgnoreCase))
                        {
                            scUrl ??= url;
                        }
                        else
                        {
                            fdUrl ??= url;
                        }
                    }
                }

                // 按已装变体选包：自包含优先 -SC- 包；框架依赖只取非 -SC- 包，
                // 避免框架依赖用户下到 3 倍体积的自包含包。两侧都有最终兜底。
                string? setupUrl = variant == "sc"
                    ? (scUrl ?? fdUrl ?? fallbackUrl)
                    : (fdUrl ?? fallbackUrl);

                // SHA256SUMS.txt：取到就解析出所选安装包的期望哈希；取不到不阻断更新
                string? expectedSha256 = null;
                if (!string.IsNullOrEmpty(sumsUrl) && !string.IsNullOrEmpty(setupUrl))
                {
                    expectedSha256 = await FetchExpectedSha256Async(http, sumsUrl, setupUrl);
                }

                return new UpdateInfo(tag ?? string.Empty, setupUrl, variant, expectedSha256);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateChecker.FetchLatest", caught);
                return null;
            }
        }

        /// <summary>
        /// 下载 SHA256SUMS.txt 并解析出 setupUrl 对应文件名的哈希行（格式：&lt;hash&gt;  &lt;filename&gt;）。
        /// 任何失败都返回 null（不阻断更新，仅放弃校验）。
        /// </summary>
        private static async Task<string?> FetchExpectedSha256Async(HttpClient http, string sumsUrl, string setupUrl)
        {
            try
            {
                string fileName = Path.GetFileName(new Uri(setupUrl).AbsolutePath);
                if (string.IsNullOrEmpty(fileName))
                {
                    return null;
                }

                string text = await http.GetStringAsync(sumsUrl);
                foreach (string rawLine in text.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    int sep = line.IndexOf(' ');
                    if (sep <= 0)
                    {
                        continue;
                    }

                    string hash = line.Substring(0, sep).Trim();
                    string name = line.Substring(sep).Trim();
                    if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) && hash.Length == 64)
                    {
                        return hash.ToLowerInvariant();
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateChecker.FetchSha256Sums", caught);
            }

            return null;
        }

        /// <summary>
        /// 检查更新（拉取 + 比较）。发现更新的版本时写入 <see cref="LatestAvailable"/> 并返回它；
        /// 否则清空 LatestAvailable 并返回 null（无法区分「已最新」与「检查失败」）。
        /// </summary>
        public static async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            UpdateInfo? info = await FetchLatestAsync();
            if (info == null || string.IsNullOrEmpty(info.Tag)
                || CompareVersionStrings(info.Tag, CurrentVersionText()) <= 0)
            {
                LatestAvailable = null;
                return null;
            }

            LatestAvailable = info;
            return LatestAvailable;
        }
    }
}
