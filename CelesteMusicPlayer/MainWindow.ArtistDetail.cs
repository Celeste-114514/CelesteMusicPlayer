using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 艺术家详情页的改版部分（2026-10-12）：
    /// ① 左栏 = 矩形头像（立体阴影）+ 艺术家名 + 在线概览
    /// ② 右栏 = 歌曲列表 / 专辑墙 / 头像 三个页签切换
    /// ③ 头像右键 → 在线搜图 + 裁剪，落在这块右栏里（不再弹独立窗口）
    ///
    /// 放在单独的分部文件里，避免 MainWindow.xaml.cs 继续膨胀。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>右栏当前页签：Songs / Albums（头像不再是页签，见 _artistAvatarOpen）。</summary>
        private string _artistDetailTab = "Songs";

        /// <summary>
        /// 头像编辑面板是否开着（2026-10-12 第二轮实测改）：头像不与歌曲/专辑并列成页签，
        /// 改成按需打开的面板 —— 开着的时候右栏页签条整条隐藏、只露面版 + 面板上的「← 返回」，
        /// 返回后回到歌曲页签。从头像右键菜单进入时置 true。
        /// </summary>
        private bool _artistAvatarOpen;

        private ArtistAvatarEditPanelControl? _artistAvatarPanel;

        /// <summary>艺术家概览缓存（联网拿一次就存着，切来切去不再重复请求）。</summary>
        private static readonly Dictionary<string, string?> ArtistOverviewCache =
            new(StringComparer.CurrentCultureIgnoreCase);

        private static readonly SemaphoreSlim ArtistOverviewGate = new(1, 1);

        /// <summary>
        /// 矩形头像的立体阴影：WinUI3 的 ThemeShadow 需要宿主容器留出空间，
        /// 所以头像外框在 XAML 里带了 12px 边距（阴影会被裁掉否则）。
        /// </summary>
        private void ApplyArtistDetailAvatarShadow()
        {
            try
            {
                if (ArtistDetailAvatarFrame == null)
                {
                    return;
                }

                if (ArtistDetailAvatarFrame.Shadow is not ThemeShadow)
                {
                    ArtistDetailAvatarFrame.Shadow = new ThemeShadow();
                }

                ArtistDetailAvatarFrame.Translation = new System.Numerics.Vector3(0, 6, 32);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ArtistDetail.cs.ApplyArtistDetailAvatarShadow", caught);
            }
        }

        // ── 右栏页签切换 ────────────────────────────────────────────
        private void ArtistDetailTabSongsButton_Click(object sender, RoutedEventArgs e)
            => ApplyArtistDetailTab("Songs");

        private void ArtistDetailTabAlbumsButton_Click(object sender, RoutedEventArgs e)
            => ApplyArtistDetailTab("Albums");

        /// <summary>切右栏页签（歌曲列表 / 专辑墙），同一时刻只显示一个。</summary>
        private void ApplyArtistDetailTab(string tab)
        {
            // 头像面板开着时如果有人从外部直接切页签（页签条藏起来了，正常点不到；
            // 这里是命令/代码路径的兜底），先把头像模式收掉再切。
            // CloseArtistAvatarPanel 内部会再调一次本方法（那时 _artistAvatarOpen 已 false）。
            if (_artistAvatarOpen)
            {
                CloseArtistAvatarPanel();
                return;
            }

            _artistDetailTab = tab;

            bool songs = string.Equals(tab, "Songs", StringComparison.Ordinal);
            bool albums = string.Equals(tab, "Albums", StringComparison.Ordinal);

            if (ArtistSongsFrostPanel != null)
            {
                ArtistSongsFrostPanel.Visibility = songs ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ArtistAlbumPanel != null)
            {
                ArtistAlbumPanel.Visibility = albums ? Visibility.Visible : Visibility.Collapsed;
            }

            // 选中态（2026-10-12 方案B 文字页签）：当前页签文字提亮 + 底部强调色指示条，
            // 其余页签用次级文字色、指示条收起。早前按钮形态把未选中 Background/Foreground 赋 null，
            // 回落到默认按钮 chrome 后未选中的字跟背景同色看不见（用户实测反馈）。
            ApplyTabChrome(ArtistDetailTabSongsButton, ArtistDetailTabSongsUnderline, songs);
            ApplyTabChrome(ArtistDetailTabAlbumsButton, ArtistDetailTabAlbumsUnderline, albums);
        }

        /// <summary>页签外观：透明底文字选项卡。强调色一律走 DspAccentBrush()（带极客/令牌皮肤分支）。</summary>
        private void ApplyTabChrome(Button? button, Border? underline, bool active)
        {
            if (button == null)
            {
                return;
            }

            try
            {
                // 文字色不读 TextFillColorPrimaryBrush / TextFillColorSecondaryBrush：
                // 令牌皮肤会把这两个全局刷子覆写成令牌色（为深色皮肤设计），
                // 浅色主题下页签字就不是黑的了（2026-10-12 用户实测）。
                // 主题也不能读 Application.Current.RequestedTheme：外壳（MainWindow 的
                // Content 根）会按「界面风格」显式设置 RequestedTheme
                // （见 UiTheme.ApplyUiStyleMode），App 级主题与元素实际主题可能不一致 ——
                // 经典浅色下页签字被赋成白色、鼠标悬停反而变黑，根因就是这个（用户实测）。
                // 按当前主题直接给固定色：浅色 = 黑 / 60% 黑，深色 = 白 / 60% 白，
                // 任何皮肤下都保证可读；「选中提亮、未选中次级」的语义不变。
                // ⚠️ 别把这几个 Color 写成嵌套三元：Roslyn NullableWalker 分析
                // 二元嵌套条件表达式时会深递归，偶发原生崩 0xC0000005（2026-10-12 实测，
                // publish 连崩 4 次，拆开就过）。
                bool light = IsLightThemeFor(button);
                byte alpha = active ? (byte)255 : (byte)153;
                Color textColor = light
                    ? Color.FromArgb(alpha, 0, 0, 0)
                    : Color.FromArgb(alpha, 255, 255, 255);
                button.Foreground = new SolidColorBrush(textColor);

                if (underline != null)
                {
                    underline.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                    if (active)
                    {
                        underline.Background = DspAccentBrush();
                    }
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ArtistDetail.cs.ApplyTabChrome", caught);
            }
        }

        /// <summary>
        /// 判断元素当前实际处于浅色还是深色主题。
        /// 不能直接读 Application.Current.RequestedTheme：外壳（MainWindow 的 Content 根）
        /// 会按「界面风格」显式设置 RequestedTheme（UiTheme.ApplyUiStyleMode），
        /// App 级主题与元素实际主题可能不一致 —— 经典浅色下页签字被赋成白色就是这个原因
        /// （2026-10-12 用户实测）。从自身往上找第一个显式指定了 Light/Dark 的祖先；
        /// 一路都是 Default（跟随系统）时才回落到 App 级主题。
        /// </summary>
        private static bool IsLightThemeFor(FrameworkElement element)
        {
            DependencyObject? current = element;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.RequestedTheme != ElementTheme.Default)
                {
                    return fe.RequestedTheme == ElementTheme.Light;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return Application.Current.RequestedTheme == ApplicationTheme.Light;
        }

        // ── 头像页签：在线搜图 + 裁剪 ───────────────────────────────
        private void EnsureArtistAvatarPanel()
        {
            if (_artistAvatarPanel != null)
            {
                return;
            }

            _artistAvatarPanel = new ArtistAvatarEditPanelControl();
            _artistAvatarPanel.AvatarConfirmed += image =>
            {
                // 保存成功后：刷新左栏矩形头像 + 艺术家墙里的圆形头像
                if (_openedArtist != null)
                {
                    _openedArtist.AvatarImage = image;
                    ApplyArtistAvatarToDetailIfOpen(_openedArtist, image);
                }

                // 艺术家墙上的圆形头像走的是条目自己的 AvatarImage，下次回到墙时会重读；
                // 这里不强行整墙重绘（与「恢复默认头像」既有行为一致）。
            };
            _artistAvatarPanel.CloseRequested += CloseArtistAvatarPanel;

            if (ArtistAvatarPanel != null)
            {
                ArtistAvatarPanel.Children.Add(_artistAvatarPanel);
            }
        }

        /// <summary>
        /// 从头像右键菜单进入「在线搜索网络头像」/「选择本地图片」：
        /// 头像面板按需打开（2026-10-12 第二轮实测改：不再与歌曲/专辑并列成页签）。
        /// 打开时右栏页签条整条隐藏，只露面版 + 面板自己的「← 返回」按钮。
        /// localPath 不为空时跳过联网搜索，直接裁本地这张图。
        /// </summary>
        internal void OpenArtistAvatarPanel(ArtistEntry artist, string? localPath = null)
        {
            try
            {
                EnsureArtistAvatarPanel();
                if (_artistAvatarPanel == null)
                {
                    return;
                }

                _artistAvatarOpen = true;

                // 头像开着的时候页签条整条藏起来：头像不是与歌曲/专辑并列的第三个页签
                if (ArtistDetailTabStrip != null)
                {
                    ArtistDetailTabStrip.Visibility = Visibility.Collapsed;
                }

                bool albumArtistMode = _artistDetailUsesAlbumArtist
                    || string.Equals(_currentCategory, "AlbumArtists", StringComparison.Ordinal);
                string storeKey = ArtistAvatarStoreKey(artist.Name, albumArtistMode);

                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    _artistAvatarPanel.BeginWithLocalFile(artist.Name, storeKey, localPath);
                }
                else
                {
                    _artistAvatarPanel.BeginFor(artist.Name, storeKey);
                }

                // 右栏只露面版：歌曲列表 / 专辑墙收起
                if (ArtistSongsFrostPanel != null)
                {
                    ArtistSongsFrostPanel.Visibility = Visibility.Collapsed;
                }

                if (ArtistAlbumPanel != null)
                {
                    ArtistAlbumPanel.Visibility = Visibility.Collapsed;
                }

                if (ArtistAvatarPanel != null)
                {
                    ArtistAvatarPanel.Visibility = Visibility.Visible;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ArtistDetail.cs.OpenArtistAvatarPanel", caught);
                // 不能静默：面板没打开成功时用户会以为菜单坏了（2026-10-12 实测反馈）
                _ = ShowErrorAsync("头像面板打开失败", caught.Message);
            }
        }

        /// <summary>
        /// 关闭头像面板（面板上的「← 返回」按钮触发）：恢复页签条，回到歌曲列表。
        /// 幂等，任何时候调用都安全。
        /// </summary>
        private void CloseArtistAvatarPanel()
        {
            _artistAvatarOpen = false;

            if (ArtistAvatarPanel != null)
            {
                ArtistAvatarPanel.Visibility = Visibility.Collapsed;
            }

            if (ArtistDetailTabStrip != null)
            {
                ArtistDetailTabStrip.Visibility = Visibility.Visible;
            }

            ApplyArtistDetailTab("Songs");
        }

        // ── 艺术家概览（在线简介） ──────────────────────────────────
        /// <summary>
        /// 拉艺术家概览并显示在左栏头像下方。
        /// 目前只有网易云一路（用户要求「后续再加别的平台的设置」），
        /// 平台分支写在 OnlineMusicApi.GetArtistOverviewAsync 里，以后加平台改那一处即可。
        /// </summary>
        internal async Task RefreshArtistOverviewAsync(string artistName)
        {
            try
            {
                if (ArtistDetailOverviewText == null)
                {
                    AppLog.Warn("net", "artist-overview: 概览文本控件为 null，直接返回");
                    return;
                }

                if (string.IsNullOrWhiteSpace(artistName))
                {
                    ArtistDetailOverviewText.Text = string.Empty;
                    return;
                }

                if (ArtistDetailOverviewSourceText != null)
                {
                    // 2026-10-12 第二轮起酷狗优先（网易云老接口被间歇风控），兜底网易云
                    ArtistDetailOverviewSourceText.Text = "艺术家概览 · 酷狗音乐";
                }

                // 与当前打开的艺术家不一致（用户已经切走）就不再往界面上写
                if (_openedArtist == null
                    || !string.Equals(_openedArtist.Name, artistName, StringComparison.CurrentCultureIgnoreCase))
                {
                    AppLog.Info("net", "artist-overview: 已切走，跳过 name=" + artistName);
                    return;
                }

                if (ArtistOverviewCache.TryGetValue(artistName, out string? cached))
                {
                    AppLog.Info("net", "artist-overview: 缓存命中 name=" + artistName
                        + " len=" + (cached?.Length ?? 0));
                    ArtistDetailOverviewText.Text = string.IsNullOrWhiteSpace(cached)
                        ? "暂无该艺术家的概览"
                        : cached;
                    return;
                }

                ArtistDetailOverviewText.Text = "正在获取艺术家概览…";
                AppLog.Info("net", "artist-overview: 开始请求 name=" + artistName);

                await ArtistOverviewGate.WaitAsync();
                string? brief;
                try
                {
                    brief = await OnlineMusicApi.GetArtistOverviewAsync("NetEase", artistName, CancellationToken.None);
                }
                finally
                {
                    ArtistOverviewGate.Release();
                }

                AppLog.Info("net", "artist-overview: 请求返回 name=" + artistName
                    + " brief=" + (brief == null ? "null" : "len=" + brief.Length.ToString()));

                ArtistOverviewCache[artistName] = brief;

                if (_openedArtist == null
                    || !string.Equals(_openedArtist.Name, artistName, StringComparison.CurrentCultureIgnoreCase))
                {
                    return;
                }

                ArtistDetailOverviewText.Text = string.IsNullOrWhiteSpace(brief)
                    ? "暂无该艺术家的概览"
                    : brief;
            }
            catch (Exception caught)
            {
                AppLog.Error("net", "artist-overview: 异常 name=" + artistName, caught);
                StartupLog.WriteException("MainWindow.ArtistDetail.cs.RefreshArtistOverviewAsync", caught);
                if (ArtistDetailOverviewText != null)
                {
                    ArtistDetailOverviewText.Text = "概览获取失败";
                }
            }
        }
    }
}
