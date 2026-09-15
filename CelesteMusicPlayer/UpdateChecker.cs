using System;
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
            public UpdateInfo(string tag, string? setupUrl)
            {
                Tag = tag;
                SetupUrl = setupUrl;
            }

            /// <summary>版本 tag，如 "v26.9.14"。</summary>
            public string Tag { get; }

            /// <summary>安装包（Setup-*.exe）下载地址；无匹配资产为 null。</summary>
            public string? SetupUrl { get; }
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

        /// <summary>
        /// 从 GitHub Releases latest 接口读取 tag_name 与安装包下载地址。
        /// 返回 (tag, setupUrl)；网络异常或解析失败两项均为 null。
        /// </summary>
        public static async Task<(string? Tag, string? SetupUrl)> FetchLatestAsync()
        {
            string? setupUrl = null;
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

                // 在 assets 里找安装包：优先 CelesteMusicPlayer-Setup-*.exe，其次任何 *.exe 里的 Setup/Install
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

                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("Install", StringComparison.OrdinalIgnoreCase)))
                        {
                            setupUrl = url;
                            break;
                        }
                    }

                    // 兜底：没找到 Setup 命名，退而求其次取第一个 .exe（可能为绿色版/自解压包）
                    if (setupUrl == null)
                    {
                        foreach (JsonElement asset in assets.EnumerateArray())
                        {
                            string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                            string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                            if (!string.IsNullOrEmpty(name) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrEmpty(url))
                            {
                                setupUrl = url;
                                break;
                            }
                        }
                    }
                }

                return (tag, setupUrl);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("UpdateChecker.FetchLatest", caught);
                return (null, null);
            }
        }

        /// <summary>
        /// 检查更新（拉取 + 比较）。发现更新的版本时写入 <see cref="LatestAvailable"/> 并返回它；
        /// 否则清空 LatestAvailable 并返回 null（无法区分「已最新」与「检查失败」）。
        /// </summary>
        public static async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            (string? tag, string? setupUrl) = await FetchLatestAsync();
            if (string.IsNullOrEmpty(tag) || CompareVersionStrings(tag, CurrentVersionText()) <= 0)
            {
                LatestAvailable = null;
                return null;
            }

            LatestAvailable = new UpdateInfo(tag, setupUrl);
            return LatestAvailable;
        }
    }
}
