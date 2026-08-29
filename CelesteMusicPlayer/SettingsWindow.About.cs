using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 选项设置 → 「关于」面板逻辑：版本号 + 检查更新（GitHub Releases latest API）。
    /// </summary>
    public sealed partial class SettingsWindow
    {
        private const string GithubReleasesApi =
            "https://api.github.com/repos/Celeste-114514/CelesteMusicPlayer/releases/latest";

        /// <summary>当前程序集版本号字符串（如 "26.8.29.0"）。</summary>
        private static string CurrentVersionText()
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "未知";
        }

        /// <summary>
        /// 比较两个版本字符串（支持 v 前缀和 -beta/-rc 后缀）。
        /// 返回值：&lt;0 / 0 / &gt;0 表示 a 比 b 旧/相等/新。
        /// </summary>
        private static int CompareVersionStrings(string a, string b)
        {
            Version va = ParseLoose(a);
            Version vb = ParseLoose(b);
            return va.CompareTo(vb);
        }

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

            // Version 只支持 2-4 段，补齐
            return parsed.Build < 0
                ? new Version(parsed.Major, parsed.Minor, 0)
                : parsed;
        }

        /// <summary>从 GitHub Releases latest 接口读取 tag_name（如 "v26.9.1"）。</summary>
        private async System.Threading.Tasks.Task<string?> FetchLatestVersionAsync()
        {
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteMusicPlayer/" + CurrentVersionText());
                string json = await http.GetStringAsync(GithubReleasesApi);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tag_name", out JsonElement tag) && tag.ValueKind == JsonValueKind.String)
                {
                    return tag.GetString();
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.About.FetchLatest", caught);
            }

            return null;
        }

        private void AboutCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (AboutUpdateStatusText == null)
            {
                return;
            }

            AboutCheckUpdateButton.IsEnabled = false;
            string currentVer = CurrentVersionText();
            AboutUpdateStatusText.Text = "正在检查更新…";

            _ = CheckUpdateAsync(currentVer);
        }

        private async System.Threading.Tasks.Task CheckUpdateAsync(string currentVer)
        {
            try
            {
                string? latestTag = await FetchLatestVersionAsync();
                if (string.IsNullOrEmpty(latestTag))
                {
                    AboutUpdateStatusText.Text = "检查更新失败：网络不通或接口异常，请稍后重试。";
                    return;
                }

                int cmp = CompareVersionStrings(latestTag, currentVer);
                if (cmp > 0)
                {
                    AboutUpdateStatusText.Text = $"发现新版本 {latestTag}（当前 {currentVer}）。点击右侧「GitHub Releases ↗」下载。";
                }
                else
                {
                    AboutUpdateStatusText.Text = $"已是最新版本（{currentVer}）。";
                }
            }
            finally
            {
                AboutCheckUpdateButton.IsEnabled = true;
            }
        }
    }
}
