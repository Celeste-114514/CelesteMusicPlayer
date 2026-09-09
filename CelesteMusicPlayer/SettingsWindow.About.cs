using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 选项设置 → 「关于」面板逻辑：版本号 + 检查更新（GitHub Releases latest API）
    /// + 应用内下载安装包（读 release assets 里的 Setup-*.exe）后弹出安装向导覆盖安装。
    /// </summary>
    public sealed partial class SettingsWindow
    {
        private const string GithubReleasesApi =
            "https://api.github.com/repos/Celeste-114514/CelesteMusicPlayer/releases/latest";

        /// <summary>当前发现的最新版本 tag（如 "v26.9.11"），未检查或检查失败为 null。</summary>
        private string? _latestTag;

        /// <summary>最新版本安装包（Setup-*.exe）的下载 URL，无匹配资产为 null。</summary>
        private string? _latestSetupUrl;

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

        /// <summary>
        /// 从 GitHub Releases latest 接口读取 tag_name 与安装包下载地址。
        /// 成功返回 tag（如 "v26.9.1"），并把 Setup-*.exe 的下载 URL 写入 _latestSetupUrl；
        /// 失败返回 null。
        /// </summary>
        private async System.Threading.Tasks.Task<string?> FetchLatestVersionAsync()
        {
            _latestSetupUrl = null;
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
                            _latestSetupUrl = url;
                            break;
                        }
                    }

                    // 兜底：没找到 Setup 命名，退而求其次取第一个 .exe（可能为绿色版/自解压包）
                    if (_latestSetupUrl == null)
                    {
                        foreach (JsonElement asset in assets.EnumerateArray())
                        {
                            string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                            string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                            if (!string.IsNullOrEmpty(name) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrEmpty(url))
                            {
                                _latestSetupUrl = url;
                                break;
                            }
                        }
                    }
                }

                return tag;
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
                _latestTag = latestTag;
                if (string.IsNullOrEmpty(latestTag))
                {
                    AboutUpdateStatusText.Text = "检查更新失败：网络不通或接口异常，请稍后重试。";
                    return;
                }

                int cmp = CompareVersionStrings(latestTag, currentVer);
                if (cmp > 0)
                {
                    if (!string.IsNullOrEmpty(_latestSetupUrl))
                    {
                        AboutUpdateStatusText.Text = $"发现新版本 {latestTag}（当前 {currentVer}）。点击「下载更新」下载安装包。";
                        AboutDownloadUpdateButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        AboutUpdateStatusText.Text = $"发现新版本 {latestTag}（当前 {currentVer}）。该版本未附带安装包，请到「GitHub Releases ↗」手动下载。";
                        AboutDownloadUpdateButton.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    AboutUpdateStatusText.Text = $"已是最新版本（{currentVer}）。";
                    AboutDownloadUpdateButton.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                AboutCheckUpdateButton.IsEnabled = true;
            }
        }

        private void AboutDownloadUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_latestSetupUrl) || string.IsNullOrEmpty(_latestTag))
            {
                return;
            }

            _ = DownloadAndLaunchInstallerAsync(_latestSetupUrl, _latestTag);
        }

        /// <summary>
        /// 应用内下载安装包到临时目录（带进度），下载完成后弹出安装向导（覆盖安装到原路径）。
        /// </summary>
        private async System.Threading.Tasks.Task DownloadAndLaunchInstallerAsync(string url, string tag)
        {
            string fileName = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "CelesteMusicPlayer-Setup.exe";
            }

            string tmpDir = Path.Combine(Path.GetTempPath(), "CelesteMusicPlayerUpdate");
            try
            {
                Directory.CreateDirectory(tmpDir);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.About.MkTemp", caught);
                AboutUpdateStatusText.Text = "下载失败：无法创建临时目录。";
                return;
            }

            string targetPath = Path.Combine(tmpDir, fileName);

            // 清理同名的残留旧文件，避免重复下载时覆盖失败
            try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }

            AboutDownloadUpdateButton.IsEnabled = false;
            AboutCheckUpdateButton.IsEnabled = false;
            AboutDownloadProgress.Visibility = Visibility.Visible;
            AboutDownloadProgress.Value = 0;
            AboutUpdateStatusText.Text = $"正在下载 {fileName} …";

            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromMinutes(30);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CelesteMusicPlayer/" + CurrentVersionText());

                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long? total = response.Content.Headers.ContentLength;
                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read);
                    downloaded += read;
                    if (total.HasValue && total.Value > 0)
                    {
                        int pct = (int)Math.Clamp(downloaded * 100 / total.Value, 0, 100);
                        AboutDownloadProgress.Value = pct;
                        AboutUpdateStatusText.Text = $"正在下载 {fileName} … {pct}%（{FormatBytes(downloaded)} / {FormatBytes(total.Value)}）";
                    }
                    else
                    {
                        AboutUpdateStatusText.Text = $"正在下载 {fileName} … 已下载 {FormatBytes(downloaded)}";
                    }
                }

                AboutDownloadProgress.Value = 100;
                AboutUpdateStatusText.Text = $"下载完成，正在启动安装向导（{tag}）。安装过程中请按提示操作，程序将覆盖安装到原目录。";

                // 弹出安装向导（NSIS 会读注册表 InstallLocation 自动定位原目录覆盖安装）
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = targetPath,
                        UseShellExecute = true,
                    };
                    Process.Start(psi);
                }
                catch (Exception caught)
                {
                    StartupLog.WriteException("SettingsWindow.About.LaunchInstaller", caught);
                    AboutUpdateStatusText.Text = $"下载完成，但无法自动启动安装程序。请手动运行：\n{targetPath}";
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.About.Download", caught);
                AboutUpdateStatusText.Text = "下载失败：网络中断或下载地址失效，请稍后重试或到「GitHub Releases ↗」手动下载。";
                AboutDownloadProgress.Visibility = Visibility.Collapsed;
            }
            finally
            {
                AboutDownloadUpdateButton.IsEnabled = true;
                AboutCheckUpdateButton.IsEnabled = true;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }
            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0.0") + " KB";
            }
            return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }
    }
}
