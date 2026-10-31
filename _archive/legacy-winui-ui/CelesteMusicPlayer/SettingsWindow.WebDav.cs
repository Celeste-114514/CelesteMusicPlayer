using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 设置窗口的 WebDAV 网络音乐库板块。
    ///
    /// 单独拆一个 partial 文件，避免继续往 SettingsWindow.xaml.cs（2500+ 行）里堆。
    /// 这一块只负责「配置读写 + 连接测试 + 缓存清理」，浏览/播放逻辑在主窗口的 MainWindow.WebDav.cs。
    /// </summary>
    public sealed partial class SettingsWindow : Window
    {
        /// <summary>把设置里的 WebDAV 配置回填到本页控件。</summary>
        private void LoadWebDavIntoUi(AppSettingsState s)
        {
            SetText(WebDavNameBox, s.WebDavDisplayName);
            SetText(WebDavUrlBox, s.WebDavUrl);
            SetText(WebDavUserBox, s.WebDavUser);

            if (WebDavPasswordBox != null)
            {
                // 设置里存的是 DPAPI 密文，AppSettingsStore.Load() 已解密成明文，这里直接回填；
                // PasswordBox 只显示圆点，不额外暴露。
                WebDavPasswordBox.Password = s.WebDavPassword ?? string.Empty;
            }

            SelectComboByTag(WebDavProxyCombo, s.WebDavProxyMode);
            SetText(WebDavProxyHostBox, s.WebDavProxyHost);
            SetText(WebDavProxyPortBox, s.WebDavProxyPort.ToString(CultureInfo.InvariantCulture));
            SetText(WebDavCacheDirBox, s.WebDavCacheDir);

            if (WebDavCacheLimitNumberBox != null)
            {
                // NumberBox.Value 是 double 且为 NaN 时会把界面搞成空白，先夹一次再赋。
                double limit = s.WebDavCacheLimitMb > 0 ? s.WebDavCacheLimitMb : 4096;
                WebDavCacheLimitNumberBox.Value = Math.Clamp(limit, 128, 102_400);
            }

            UpdateWebDavProxyRowState();
            UpdateWebDavCacheUsage();

            WebDavLocation? loc = WebDavLocation.FromSettings(s);
            if (WebDavStatusText != null)
            {
                WebDavStatusText.Text = loc.IsConfigured
                    ? "已配置：" + loc.ShortName + "（" + loc.ProxyDisplayName + "）"
                    : "还没有配置网络音乐库。填好地址后点「保存并连接」，主界面左侧「媒体库」下方就会出现它。";
            }
        }

        /// <summary>把本页控件的 WebDAV 配置写回设置（供「应用 / 保存并关闭」统一走）。</summary>
        private void PopulateWebDavIntoState(AppSettingsState s)
        {
            if (WebDavUrlBox == null)
            {
                return; // 面板还没初始化（理论上不会，防御一下）
            }

            s.WebDavDisplayName = WebDavNameBox?.Text?.Trim() ?? s.WebDavDisplayName;
            s.WebDavUrl = WebDavUrlBox.Text?.Trim() ?? string.Empty;
            s.WebDavUser = WebDavUserBox?.Text?.Trim() ?? string.Empty;

            // 密码框回填的是明文，直接存即可（落盘时 AppSettingsStore 会走 DPAPI 加密）。
            // 留空 = 真的没有密码（匿名访问），不做「保留旧值」的特殊逻辑 —— 那会让"想清空密码"做不到。
            s.WebDavPassword = WebDavPasswordBox?.Password ?? string.Empty;

            s.WebDavProxyMode = (WebDavProxyCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? s.WebDavProxyMode;
            s.WebDavProxyHost = WebDavProxyHostBox?.Text?.Trim() ?? string.Empty;

            string portText = WebDavProxyPortBox?.Text?.Trim() ?? string.Empty;
            if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) && port > 0 && port <= 65535)
            {
                s.WebDavProxyPort = port;
            }

            s.WebDavCacheDir = WebDavCacheDirBox?.Text?.Trim() ?? string.Empty;

            if (WebDavCacheLimitNumberBox != null && !double.IsNaN(WebDavCacheLimitNumberBox.Value))
            {
                s.WebDavCacheLimitMb = (int)Math.Clamp(Math.Round(WebDavCacheLimitNumberBox.Value), 128, 102_400);
            }
        }

        /// <summary>代理选「手动」时才让地址/端口可填，否则灰掉，少一次误填。</summary>
        private void UpdateWebDavProxyRowState()
        {
            bool manual = string.Equals(
                (WebDavProxyCombo?.SelectedItem as ComboBoxItem)?.Tag as string,
                WebDavLocation.ProxyManual,
                StringComparison.OrdinalIgnoreCase);

            if (WebDavProxyHostBox != null)
            {
                WebDavProxyHostBox.IsEnabled = manual;
            }

            if (WebDavProxyPortBox != null)
            {
                WebDavProxyPortBox.IsEnabled = manual;
            }
        }

        private void UpdateWebDavCacheUsage()
        {
            if (WebDavCacheUsageText == null)
            {
                return;
            }

            try
            {
                WebDavLocation? loc = WebDavLocation.FromSettings(AppSettingsStore.Load());
                if (!loc.IsConfigured)
                {
                    WebDavCacheUsageText.Text = "暂无缓存";
                    return;
                }

                WebDavCacheUsageText.Text = "当前占用：" + WebDavCache.DescribeUsage(loc);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.WebDav.cs", caught);
                WebDavCacheUsageText.Text = "缓存占用读取失败";
            }
        }

        private void WebDavProxyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingUi)
            {
                return;
            }

            UpdateWebDavProxyRowState();
        }

        /// <summary>「测试连接」：用当前表单里填的内容（不落盘）连一次根目录。</summary>
        private async void WebDavTestButton_Click(object sender, RoutedEventArgs e)
        {
            WebDavLocation? loc = ReadWebDavLocationFromUi();
            if (loc == null)
            {
                SetWebDavStatus("请先填写服务器地址（要以 http:// 或 https:// 开头）。");
                return;
            }

            if (WebDavTestButton != null)
            {
                WebDavTestButton.IsEnabled = false;
            }

            SetWebDavStatus("正在连接…");

            try
            {
                // 网络请求必须在后台线程等，别卡 UI。
                WebDavTestResult result = await Task.Run(async () =>
                {
                    using WebDavClient client = loc.CreateClient();
                    return await client.TestConnectionAsync();
                });

                SetWebDavStatus(result.Ok
                    ? result.Headline + Environment.NewLine + result.Detail
                    : result.Headline + Environment.NewLine + result.Detail);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("SettingsWindow.WebDav.cs", caught);
                SetWebDavStatus("测试过程中出错：" + caught.Message);
            }
            finally
            {
                if (WebDavTestButton != null)
                {
                    WebDavTestButton.IsEnabled = true;
                }
            }
        }

        private void WebDavSaveButton_Click(object sender, RoutedEventArgs e)
        {
            WebDavLocation? loc = ReadWebDavLocationFromUi();
            if (loc == null)
            {
                SetWebDavStatus("服务器地址还没填，先填地址再保存。");
                return;
            }

            AppSettingsStore.Update(s => PopulateWebDavIntoState(s));

            // 主窗口左侧的条目要立刻跟着变（名称 / 显示与隐藏）。
            MainWindow.Instance?.RefreshWebDavNavEntry();

            SetWebDavStatus("已保存：" + loc.ShortName + "（" + loc.ProxyDisplayName + "）。" +
                            Environment.NewLine + "现在可以在主界面左侧「媒体库」下方点开它了。");
            UpdateWebDavCacheUsage();
        }

        private async void WebDavDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsState s = AppSettingsStore.Load();
            if (string.IsNullOrWhiteSpace(s.WebDavUrl))
            {
                SetWebDavStatus("当前没有配置网络音乐库，无需删除。");
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "删除网络音乐库？",
                Content = "会清掉地址、账号密码等连接信息，主界面左侧那个条目也会消失。" +
                          Environment.NewLine + "已经下载到本地缓存的文件不会被删除（可在上面点「清理缓存」）。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            ContentDialogResult answer = await dialog.ShowAsync();
            if (answer != ContentDialogResult.Primary)
            {
                return;
            }

            AppSettingsStore.Update(x =>
            {
                x.WebDavUrl = string.Empty;
                x.WebDavDisplayName = string.Empty;
                x.WebDavUser = string.Empty;
                x.WebDavPassword = string.Empty;
                x.WebDavProxyHost = string.Empty;
                x.WebDavProxyMode = WebDavLocation.ProxySystem;
                x.WebDavProxyPort = 7890;
            });

            // 清空表单
            SetText(WebDavNameBox, string.Empty);
            SetText(WebDavUrlBox, string.Empty);
            SetText(WebDavUserBox, string.Empty);
            if (WebDavPasswordBox != null)
            {
                WebDavPasswordBox.Password = string.Empty;
            }

            SelectComboByTag(WebDavProxyCombo, WebDavLocation.ProxySystem);
            SetText(WebDavProxyHostBox, string.Empty);
            SetText(WebDavProxyPortBox, "7890");
            UpdateWebDavProxyRowState();

            // 条目消失；若当前正停在该分类，顺手切回歌曲页，避免停在一个已经不存在的库上。
            MainWindow.Instance?.RefreshWebDavNavEntry();

            SetWebDavStatus("已删除网络音乐库。");
            UpdateWebDavCacheUsage();
        }

        private void WebDavClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsState s = AppSettingsStore.Load();
            WebDavLocation? loc = WebDavLocation.FromSettings(s);
            if (!loc.IsConfigured)
            {
                SetWebDavStatus("还没有配置网络音乐库，没有缓存可清理。");
                return;
            }

            int removed = WebDavCache.Clear(loc);
            SetWebDavStatus(removed > 0
                ? "已清理 " + removed + " 个缓存文件。"
                : "没有可清理的缓存（正在播放的那首会等播完再清）。");
            UpdateWebDavCacheUsage();
        }

        /// <summary>从表单读取一个可用的位置；地址为空返回 null。</summary>
        private WebDavLocation? ReadWebDavLocationFromUi()
        {
            string url = WebDavUrlBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string portText = WebDavProxyPortBox?.Text?.Trim() ?? string.Empty;
            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port <= 0 || port > 65535)
            {
                port = 7890;
            }

            return new WebDavLocation
            {
                Url = url,
                User = WebDavUserBox?.Text?.Trim() ?? string.Empty,
                Password = WebDavPasswordBox?.Password ?? string.Empty,
                ProxyMode = (WebDavProxyCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? WebDavLocation.ProxySystem,
                ProxyHost = WebDavProxyHostBox?.Text?.Trim() ?? string.Empty,
                ProxyPort = port
            };
        }

        private void SetWebDavStatus(string text)
        {
            if (WebDavStatusText != null)
            {
                WebDavStatusText.Text = text;
            }
        }
    }
}
