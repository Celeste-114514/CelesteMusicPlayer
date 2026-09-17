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

        /// <summary>当前程序集版本号字符串（如 "26.9.10.2"）。逻辑已抽到 UpdateChecker，这里只做转发。</summary>
        private static string CurrentVersionText() => UpdateChecker.CurrentVersionText();

        /// <summary>
        /// 比较两个版本字符串（支持 v 前缀和 -beta/-rc 后缀）。
        /// 返回值：&lt;0 / 0 / &gt;0 表示 a 比 b 旧/相等/新。
        /// </summary>
        private static int CompareVersionStrings(string a, string b) => UpdateChecker.CompareVersionStrings(a, b);

        /// <summary>
        /// 从 GitHub Releases latest 接口读取 tag_name 与安装包下载地址。
        /// 成功返回 tag（如 "v26.9.1"），并把 Setup-*.exe 的下载 URL 写入 _latestSetupUrl；
        /// 失败返回 null。实际网络请求走 UpdateChecker.FetchLatestAsync。
        /// </summary>
        private async System.Threading.Tasks.Task<string?> FetchLatestVersionAsync()
        {
            (string? tag, string? setupUrl) = await UpdateChecker.FetchLatestAsync();
            _latestSetupUrl = setupUrl;
            return tag;
        }

        /// <summary>
        /// 关于面板打开时，若启动期自动检查已发现新版本，直接把状态显示出来（不用等用户手动点「检查更新」）。
        /// 由 ShowPanel("About") 调用。
        /// </summary>
        private void RefreshUpdateStatusFromCache()
        {
            if (AboutUpdateStatusText == null)
            {
                return;
            }

            UpdateChecker.UpdateInfo? info = UpdateChecker.LatestAvailable;
            if (info == null)
            {
                return;
            }

            string currentVer = UpdateChecker.CurrentVersionText();
            if (UpdateChecker.CompareVersionStrings(info.Tag, currentVer) <= 0)
            {
                return;
            }

            if (!string.IsNullOrEmpty(info.SetupUrl))
            {
                AboutUpdateStatusText.Text = $"发现新版本 {info.Tag}（当前 {currentVer}）。点击「下载更新」下载安装包。";
                AboutDownloadUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                AboutUpdateStatusText.Text = $"发现新版本 {info.Tag}（当前 {currentVer}）。该版本未附带安装包，请到「GitHub Releases ↗」手动下载。";
                AboutDownloadUpdateButton.Visibility = Visibility.Collapsed;
            }
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

                // 弹出安装向导（NSIS 会读注册表 InstallLocation 自动定位原目录覆盖安装），
                // 并在「安装完成后自动删掉刚下载的安装包」，不留在硬盘上。
                // 关键：用脱钩的 cmd 进程启动安装向导并 /wait 它结束，结束后再删除安装包。
                // 本程序随后退出不影响这个 cmd —— 它仍在后台跑，所以「装完才删」这件事一定能做完。
                try
                {
                    bool launched = LaunchInstallerWithCleanup(targetPath, tmpDir);
                    if (launched)
                    {
                        AboutUpdateStatusText.Text = "安装向导已启动，本程序即将退出以释放文件并完成更新…";
                        // 稍等安装向导真正拉起后，主动退出本程序，释放被占用的 exe/dll 句柄，
                        // 确保 NSIS 能顺利覆盖安装（安装包内部也会再 taskkill 一次作为兜底）。
                        await System.Threading.Tasks.Task.Delay(800);
                        Microsoft.UI.Xaml.Application.Current?.Exit();
                        // 兜底：若上面的优雅退出未能真正终止进程，强制结束以释放文件锁，
                        // 避免安装包因文件被占用而更新失败。
                        Environment.Exit(0);
                    }
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

        /// <summary>
        /// 启动安装向导，并安排「安装完成后自动删除刚下载的安装包与临时目录」。
        /// 实现：用一个**脱钩的 cmd 进程**执行 <c>start /wait 安装包 &amp;&amp; del 安装包</c>。
        /// cmd 进程不隶属于本程序（无控制台/无窗口），本程序随后退出也不会杀掉它，
        /// 于是它能等到安装向导真正结束才删除安装包 —— 这是「装完才删」能落地的关键。
        /// 返回 true 表示安装向导已成功拉起（无论后续安装成败，本程序都应退出让位）。
        /// </summary>
        private static bool LaunchInstallerWithCleanup(string installerPath, string tmpDir)
        {
            string qi = "\"" + installerPath + "\"";
            string qd = "\"" + tmpDir + "\"";
            // start 的第一个带引号参数会被当成窗口标题，故用一个空 "" 占位；
            // 安装成功（退出码 0）才删安装包，取消/失败则保留以便用户重试；
            // 末尾再尽力删掉空掉的临时目录（失败无所谓，2&gt;nul 吞掉报错）。
            string args = "/c start \"\" /wait " + qi + " && del /f /q " + qi + " & rmdir /q " + qd + " 2>nul";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = args,
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.About.LaunchInstaller", caught);
                // 清理机制起不来不应耽误更新本身：退回「直接启动安装向导」的旧行为，
                // 安装包会留着，下次启动下载前会被同名残留清理逻辑删掉，不会无限堆积。
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = installerPath, UseShellExecute = true });
                    return true;
                }
                catch
                {
                    return false;
                }
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
