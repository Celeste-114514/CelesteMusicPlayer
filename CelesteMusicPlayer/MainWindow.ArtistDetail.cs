using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        /// <summary>右栏当前页签：Songs / Albums / Avatar。</summary>
        private string _artistDetailTab = "Songs";

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

        private void ArtistDetailTabAvatarButton_Click(object sender, RoutedEventArgs e)
            => ApplyArtistDetailTab("Avatar");

        /// <summary>切右栏页签（歌曲列表 / 专辑墙 / 头像），同一时刻只显示一个。</summary>
        private void ApplyArtistDetailTab(string tab)
        {
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

            if (ArtistAvatarPanel != null)
            {
                ArtistAvatarPanel.Visibility = (!songs && !albums) ? Visibility.Visible : Visibility.Collapsed;
            }

            // 选中态（2026-10-12 方案B 文字页签）：当前页签文字提亮 + 底部强调色指示条，
            // 其余页签用次级文字色、指示条收起。早前按钮形态把未选中 Background/Foreground 赋 null，
            // 回落到默认按钮 chrome 后未选中的字跟背景同色看不见（用户实测反馈）。
            ApplyTabChrome(ArtistDetailTabSongsButton, ArtistDetailTabSongsUnderline, songs);
            ApplyTabChrome(ArtistDetailTabAlbumsButton, ArtistDetailTabAlbumsUnderline, albums);
            ApplyTabChrome(ArtistDetailTabAvatarButton, ArtistDetailTabAvatarUnderline, !songs && !albums);
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
                // 显式给主题文字色，不赋 null：赋 null 会回落到默认按钮样式，
                // 深色主题/令牌皮肤下出现「字和背景同色」。
                button.Foreground = active
                    ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                    : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

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
            _artistAvatarPanel.CloseRequested += () => ApplyArtistDetailTab("Songs");

            if (ArtistAvatarPanel != null)
            {
                ArtistAvatarPanel.Children.Add(_artistAvatarPanel);
            }
        }

        /// <summary>
        /// 从头像右键菜单进入「在线搜索网络头像」：把头像面板塞进右栏并切到该页签。
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

                if (ArtistDetailTabAvatarButton != null)
                {
                    ArtistDetailTabAvatarButton.Visibility = Visibility.Visible;
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

                ApplyArtistDetailTab("Avatar");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ArtistDetail.cs.OpenArtistAvatarPanel", caught);
                // 不能静默：面板没打开成功时用户会以为菜单坏了（2026-10-12 实测反馈）
                _ = ShowErrorAsync("头像面板打开失败", caught.Message);
            }
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
                    return;
                }

                if (string.IsNullOrWhiteSpace(artistName))
                {
                    ArtistDetailOverviewText.Text = string.Empty;
                    return;
                }

                if (ArtistDetailOverviewSourceText != null)
                {
                    ArtistDetailOverviewSourceText.Text = "艺术家概览 · 网易云音乐";
                }

                // 与当前打开的艺术家不一致（用户已经切走）就不再往界面上写
                if (_openedArtist == null
                    || !string.Equals(_openedArtist.Name, artistName, StringComparison.CurrentCultureIgnoreCase))
                {
                    return;
                }

                if (ArtistOverviewCache.TryGetValue(artistName, out string? cached))
                {
                    ArtistDetailOverviewText.Text = string.IsNullOrWhiteSpace(cached)
                        ? "暂无该艺术家的概览"
                        : cached;
                    return;
                }

                ArtistDetailOverviewText.Text = "正在获取艺术家概览…";

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
                StartupLog.WriteException("MainWindow.ArtistDetail.cs.RefreshArtistOverviewAsync", caught);
                if (ArtistDetailOverviewText != null)
                {
                    ArtistDetailOverviewText.Text = "概览获取失败";
                }
            }
        }
    }
}
