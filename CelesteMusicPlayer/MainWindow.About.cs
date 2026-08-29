using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 设置面板「关于」分区：软件介绍 / 版本号 / 检查更新（GitHub Releases）/ 作者信息。
    /// 检查更新：请求 GitHub Releases latest 接口拿 tag_name，与当前程序集版本对比。
    /// 网络失败或解析失败时给出可读提示，不影响任何播放功能。
    /// </summary>
    public sealed partial class MainWindow
    {
        private const string GithubRepo = "Celeste-114514/CelesteMusicPlayer";
        private const string GithubReleasesApi = "https://api.github.com/repos/" + GithubRepo + "/releases/latest";
        private const string GithubReleasesUrl = "https://github.com/" + GithubRepo + "/releases";

        /// <summary>填充版本号等静态信息（构造函数调用）。</summary>
        private void InitializeAboutUi()
        {
            try
            {
                if (AboutVersionText != null)
                {
                    AboutVersionText.Text = "版本 " + CurrentVersionText();
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.About.cs", caught);
            }
        }

        /// <summary>当前版本号（取程序集版本前三段，如 26.8.29）。</summary>
        private static string CurrentVersionText()
        {
            try
            {
                Version? v = Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null)
                {
                    return "v" + v.Major + "." + v.Minor + "." + v.Build;
                }
            }
            catch
            {
            }

            return "v26.8.29";
        }

        /// <summary>检查更新：请求 GitHub Releases latest，与当前版本对比后更新状态文本。</summary>
        private async void AboutCheckUpdate_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (AboutUpdateStatusText == null)
            {
                return;
            }

            string? latestTag = null;
            try
            {
                AboutUpdateStatusText.Text = "正在检查…";
                AboutCheckUpdateButton.IsEnabled = false;
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteMusicPlayer/" + CurrentVersionText());
                string json = await http.GetStringAsync(GithubReleasesApi);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tag_name", out JsonElement tag))
                {
                    latestTag = tag.GetString();
                }
            }
            catch
            {
                AboutUpdateStatusText.Text = "检查失败（网络不可用或接口限流），请稍后再试。";
                return;
            }
            finally
            {
                AboutCheckUpdateButton.IsEnabled = true;
            }

            if (string.IsNullOrWhiteSpace(latestTag))
            {
                AboutUpdateStatusText.Text = "未能获取版本信息，请到 GitHub Releases 查看。";
                return;
            }

            // 对比版本：tag 形如 "v26.8.30"，去前缀后与当前版本比较
            string latest = latestTag.TrimStart('v', 'V');
            string current = CurrentVersionText().TrimStart('v', 'V');
            if (TryParseVersion(latest) is Version lv && TryParseVersion(current) is Version cv)
            {
                AboutUpdateStatusText.Text = lv > cv
                    ? "发现新版本 " + latestTag + "，可前往 GitHub Releases 下载。"
                    : "已是最新版本（" + latestTag + "）。";
            }
            else
            {
                AboutUpdateStatusText.Text = "最新版本 " + latestTag + "（GitHub Releases 可下载）。";
            }
        }

        private static Version? TryParseVersion(string text)
        {
            // tag 可能带后缀（如 26.8.30-beta）→ 只取前 3 段数字
            string[] parts = text.Split('-', '.');
            if (parts.Length < 3)
            {
                return null;
            }

            if (int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b) && int.TryParse(parts[2], out int c))
            {
                return new Version(a, b, c);
            }

            return null;
        }
    }
}
