using System;
using System.Diagnostics;
using System.Globalization;
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
        private string? _latestExpectedSha256;

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
            UpdateChecker.UpdateInfo? info = await UpdateChecker.FetchLatestAsync();
            _latestSetupUrl = info?.SetupUrl;
            _latestExpectedSha256 = info?.ExpectedSha256;
            return info?.Tag;
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
                // 本地测试模式：跳过 GitHub，直接用本地安装包让「下载更新」按钮出现并走完整链路。
                if (TryGetDevUpdate(out string devTag, out string devInstaller))
                {
                    _latestTag = devTag;
                    _latestSetupUrl = new Uri(devInstaller).AbsoluteUri;
                    AboutUpdateStatusText.Text = $"【本地测试】模拟发现新版本 {devTag}（当前 {currentVer}）。点击「下载更新」用本地安装包 {Path.GetFileName(devInstaller)} 验证更新链路。";
                    AboutDownloadUpdateButton.Visibility = Visibility.Visible;
                    return;
                }

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

            _ = DownloadAndLaunchInstallerAsync(_latestSetupUrl, _latestTag, _latestExpectedSha256);
        }

        /// <summary>
        /// 本地测试开关：若存在 %LOCALAPPDATA%\CelesteMusicPlayer\dev-update.txt
        /// （第一行 = 假版本号如 v99.0.0，第二行 = 本地安装包绝对路径），
        /// 则「检查更新」跳过 GitHub、「下载更新」用这个本地文件走完整更新链路。
        /// 仅用于验证更新接线（主程序是否正确调用 CelesteUpdater）；文件不存在时对正常用户零影响。
        /// 安装包可指向任意 .exe/.bat（哪怕记事本）来验证"等退出→跑安装包→重启→删包"这套动作，
        /// 也可指向本地真打的 NSIS 包验证版本号真的变了。
        /// </summary>
        private static bool TryGetDevUpdate(out string tag, out string installerPath)
        {
            tag = string.Empty;
            installerPath = string.Empty;
            try
            {
                string marker = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer",
                    "dev-update.txt");
                if (!File.Exists(marker))
                {
                    return false;
                }

                string[] lines = File.ReadAllLines(marker);
                if (lines.Length < 2)
                {
                    return false;
                }

                tag = lines[0].Trim();
                installerPath = lines[1].Trim();
                return !string.IsNullOrWhiteSpace(tag)
                       && !string.IsNullOrWhiteSpace(installerPath)
                       && File.Exists(installerPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 应用内下载安装包到临时目录（带进度），下载完成后启动更新助手（或 cmd 兜底）完成覆盖安装。
        /// 若 url 以 file:// 开头（本地测试模式），跳过网络下载，直接复制本地安装包再走同一套启动逻辑。
        /// </summary>
        private async System.Threading.Tasks.Task DownloadAndLaunchInstallerAsync(string url, string tag, string? expectedSha256)
        {
            bool devLocal = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

            string fileName = Path.GetFileName(devLocal ? new Uri(url).LocalPath : new Uri(url).AbsolutePath);
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

            // 本地测试模式：复制本地安装包到临时目录，跳过 HTTP 下载，直接进启动流程。
            if (devLocal)
            {
                try
                {
                    File.Copy(new Uri(url).LocalPath, targetPath, overwrite: true);
                    AboutDownloadProgress.Value = 100;
                    AboutUpdateStatusText.Text = $"【本地测试】已就位安装包 {fileName}，正在启动更新助手…";
                    await LaunchUpdateAndExitAsync(targetPath, tmpDir);
                }
                catch (Exception caught)
                {
                    StartupLog.WriteException("SettingsWindow.About.DevCopy", caught);
                    AboutUpdateStatusText.Text = "本地测试失败：无法复制本地安装包。";
                    AboutDownloadProgress.Visibility = Visibility.Collapsed;
                }
                finally
                {
                    AboutDownloadUpdateButton.IsEnabled = true;
                    AboutCheckUpdateButton.IsEnabled = true;
                }

                return;
            }

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
                // 安全校验：release 附带 SHA256SUMS.txt 时，下载完成后先比对 SHA-256，不符拒绝执行
                if (!string.IsNullOrEmpty(expectedSha256))
                {
                    AboutUpdateStatusText.Text = "正在校验安装包完整性（SHA-256）…";
                    string actualHash;
                    using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                    using (FileStream fs = File.OpenRead(targetPath))
                    {
                        actualHash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
                    }

                    if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(targetPath); } catch { }
                        StartupLog.Write("SettingsWindow.About.HashMismatch expected=" + expectedSha256 + " actual=" + actualHash);
                        AboutUpdateStatusText.Text = "安全校验失败：安装包哈希与官方发布不一致，已拒绝安装并删除文件。请稍后重试，或前往 GitHub Releases 手动下载。";
                        AboutDownloadProgress.Visibility = Visibility.Collapsed;
                        AboutDownloadUpdateButton.IsEnabled = true;
                        AboutCheckUpdateButton.IsEnabled = true;
                        return;
                    }
                }
                await LaunchUpdateAndExitAsync(targetPath, tmpDir);
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
        /// 启动更新流程并让本程序退出让位：优先用独立更新助手 CelesteUpdater（等本进程退出后再装，
        /// 避免文件占用导致覆盖失败），本机没有助手时退回脱钩 cmd 直接拉起安装向导。
        /// HTTP 下载与本地测试两条路径共用这一段，保证测的就是真实接线。
        /// </summary>
        private async System.Threading.Tasks.Task LaunchUpdateAndExitAsync(string targetPath, string tmpDir)
        {
            // 优先用独立的「更新助手」CelesteUpdater 完成更新：它会先等本程序真正退出，
            // 再运行安装包 —— 从根上避免"安装程序撞上主进程占用 exe → 覆盖失败 → 版本没更新"
            //（用户实测反馈：下载完程序关闭后重开仍是旧版本）。
            // 若本机没有 CelesteUpdater.exe（旧版安装），退回脱钩 cmd 直接启动安装向导兜底。
            try
            {
                bool launched = LaunchInstallerViaUpdater(targetPath, tmpDir)
                                || LaunchInstallerWithCleanup(targetPath, tmpDir);
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

        /// <summary>
        /// 用独立的「更新助手」(CelesteUpdater.exe) 完成更新。
        /// 做法（参考 DeskBox 的更新链路）：先把 CelesteUpdater.* 复制到临时目录
        /// （避免被 NSIS 覆盖安装时锁住助手自身），再以脱离主程序的方式启动它，
        /// 由它负责：等主程序退出 → 跑安装包 → 装完自动重启主程序。
        /// 返回 true 表示已成功拉起更新助手（主程序随后应立即退出让位）。
        /// 若本机没有 CelesteUpdater.exe（旧版安装），返回 false 交由 LaunchInstallerWithCleanup 兜底。
        /// </summary>
        private static bool LaunchInstallerViaUpdater(string installerPath, string tmpDir)
        {
            string baseDir = AppContext.BaseDirectory;
            string helperExe = Path.Combine(baseDir, "CelesteUpdater.exe");
            if (!File.Exists(helperExe))
            {
                return false;
            }

            // 重启时要拉起的主程序路径：优先取当前运行中的进程路径，否则退回安装目录里的 exe。
            string? appPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath))
            {
                appPath = Path.Combine(baseDir, "CelesteMusicPlayer.exe");
            }

            // 把更新助手复制到临时目录（脱离安装目录），避免 NSIS 覆盖安装时锁住它。
            string helperDir = PrepareDetachedUpdaterHelper(baseDir);
            if (string.IsNullOrWhiteSpace(helperDir) ||
                !File.Exists(Path.Combine(helperDir, "CelesteUpdater.exe")))
            {
                return false;
            }

            string helperPath = Path.Combine(helperDir, "CelesteUpdater.exe");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = helperDir
                };
                psi.ArgumentList.Add("--pid");
                psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--installer");
                psi.ArgumentList.Add(installerPath);
                psi.ArgumentList.Add("--app");
                psi.ArgumentList.Add(appPath);
                psi.ArgumentList.Add("--update");
                Process.Start(psi);
                return true;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.About.LaunchUpdater", caught);
                return false;
            }
        }

        /// <summary>
        /// 把 CelesteUpdater.*（exe/dll/deps/runtimeconfig/pdb）复制到
        /// %LOCALAPPDATA%\CelesteMusicPlayer\update-helper\&lt;时间戳&gt;-&lt;pid&gt; 目录，
        /// 返回该目录路径；失败返回空字符串。每次更新用独立目录，旧的在下一次更新时被清理。
        /// </summary>
        private static string PrepareDetachedUpdaterHelper(string appDirectory)
        {
            try
            {
                string helperRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer",
                    "update-helper");
                string helperDir = Path.Combine(
                    helperRoot,
                    $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}");
                Directory.CreateDirectory(helperDir);

                foreach (var sourcePath in Directory.EnumerateFiles(appDirectory, "CelesteUpdater.*", SearchOption.TopDirectoryOnly))
                {
                    string targetPath = Path.Combine(helperDir, Path.GetFileName(sourcePath));
                    File.Copy(sourcePath, targetPath, overwrite: true);
                }

                return helperDir;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.About.PrepareUpdater", caught);
                return string.Empty;
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
