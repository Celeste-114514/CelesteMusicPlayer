// 主题应用主入口（皮肤系统第 2 步 · 把主题真正刷到界面上）
//
// 一次切换主题要做三件事：
//   1) 往 RootShell.Resources 挂新的令牌字典（原生页 {ThemeResource Celeste.*} 自动跟着变）
//   2) 通知 WebView2 侧注入同名 CSS 变量（两套渲染层共用同一份数据）—— 第 3 步接上
//   3) 广播事件，让代码里动态建的控件（详情页封面/按钮等）重新取一次值
//
// ⚠ 为什么挂 RootShell.Resources 而不是 Application.Resources：
//   窗口渲染后改全局资源键会触发 WinUI 原生崩溃 0xc000027b，托管 catch 拦不住。
//   元素级 Resources 只影响本子树，是安全做法（极客皮肤 AudioFxBorder.Resources 同理）。
// ⚠ 为什么 XAML 里必须写 {ThemeResource} 而不是 {StaticResource}：
//   StaticResource 在换字典时不刷新，页面会停留在旧配色；ThemeResource 才会重新求值。

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private ResourceDictionary? _celesteTokenDictionary;
        private string? _celesteAppliedThemeName;

        /// <summary>主题应用完成。参数是主题名，null=恢复默认。</summary>
        public event Action<string?>? CelesteThemeApplied;

        /// <summary>当前已应用到界面的主题名；null 表示用的是内置默认。</summary>
        public string? AppliedCelesteThemeName => _celesteAppliedThemeName;

        /// <summary>
        /// 应用主题。启动时和用户切换主题时都走这里。
        /// 纯元素级操作，不碰全局资源，出错也只是主题没换成功，不会影响程序可用性。
        /// </summary>
        public async Task ApplyCelesteThemeAsync(string? themeName)
        {
            try
            {
                var theme = LoadCelesteActiveTheme();
                _celesteAppliedThemeName = themeName;

                bool dark = ResolveIsDark();

                // ---- 1) 原生页：换掉元素级令牌字典 ----
                ApplyCelesteTokensToTree(theme, dark);

                // ---- 2) 网页：注入同名 CSS 变量（0.5ms 内生效）----
                // 宿主没就绪就跳过（还没打开过网页页，此时不该有实例）
                if (_celesteWebReady && theme != null)
                {
                    await ApplyCelesteThemeToWebAsync(theme, dark);
                }

                StartupLog.Write("[Theme] 已应用主题=" + (themeName ?? "(内置默认)") +
                                 " 模式=" + (dark ? "深色" : "浅色") + " 名称=" + theme?.Name);

                // ---- 3) 让代码里动态建控件的地方重新取一次值 ----
                RefreshDetailPagesForTheme();
                CelesteThemeApplied?.Invoke(themeName);

                // 换完主题让极客皮肤那套也重刷一次（它有自己独立的磷光色逻辑）
                try
                {
                    ApplyGeekDetailPages(IsGeekUiStyleActive());
                }
                catch (Exception ex) { StartupLog.WriteException("ApplyCelesteThemeAsync.geek", ex); }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ApplyCelesteThemeAsync", ex);
            }
        }

        /// <summary>把令牌字典挂到 RootShell（换掉旧的）。窗口未就绪时静默跳过。</summary>
        private void ApplyCelesteTokensToTree(CelesteThemeFile? theme, bool dark)
        {
            var host = RootShell;
            if (host == null) return;

            try
            {
                var dict = CelesteTokenApplier.Build(theme, dark);

                // 先摘掉旧的再挂新的：同一时刻元素级只能有一个"我们的"字典来源。
                if (_celesteTokenDictionary != null)
                {
                    try { host.Resources.MergedDictionaries.Remove(_celesteTokenDictionary); }
                    catch { /* 已经不在里面了，无所谓 */ }
                }

                host.Resources.MergedDictionaries.Add(dict);
                _celesteTokenDictionary = dict;

                StartupLog.Write("[Theme] 令牌字典已挂载，键数=" + CountTokenKeys(dict));
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ApplyCelesteTokensToTree", ex);
            }
        }

        private static int CountTokenKeys(ResourceDictionary dict)
        {
            int n = 0;
            try { n = dict.Keys.Count; } catch { }
            try
            {
                if (dict.ThemeDictionaries.TryGetValue("Default", out var d) && d is ResourceDictionary dr)
                    n += dr.Keys.Count;
            }
            catch { }
            return n;
        }

        /// <summary>
        /// 当前生效主题：优先用户选定的主题文件，否则用内置默认。
        /// ⚠ MainWindow.WebHost.cs 里原本也有一份，装回时已删除，别再拷回来（CS0111 重复定义）。
        /// </summary>
        private CelesteThemeFile? LoadCelesteActiveTheme()
        {
            string? name = null;
            try { name = AppSettingsStore.Load().CelesteThemeName; }
            catch { /* 设置读失败就用默认 */ }
            if (!string.IsNullOrWhiteSpace(name))
            {
                var t = CelesteThemeStore.Load(name!);
                if (t != null) return t;
            }
            return CelesteThemeStore.CreateDefault();
        }

        /// <summary>
        /// 当前是不是深色模式。极客皮肤强制深色——它的磷光配色只有深色一套。
        /// </summary>
        private bool ResolveIsDark()
        {
            try
            {
                if (IsGeekUiStyleActive()) return true;
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
            catch
            {
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
        }

        /// <summary>
        /// 详情页里代码动态建的部分（封面圆角、按钮样式等）在这里跟着主题重刷。
        /// XAML 里写成死值的属性主题盖不掉——这是"皮肤写在代码里"的典型症状，
        /// 令牌化时逐个改成引用 Celeste.*，改完就不用再在这里手动打补丁。
        /// </summary>
        private void RefreshDetailPagesForTheme()
        {
            try
            {
                // ⚠ 传元素本身而不是 this：MainWindow 继承 Window，不是 FrameworkElement，
                //   拿不到 Resources。资源字典挂在 RootShell 上，从后代元素往上能找到。
                // 专辑详情页封面圆角：XAML 原本写死 CornerRadius="14"，主题盖不掉
                if (AlbumDetailCoverBorder != null)
                {
                    AlbumDetailCoverBorder.CornerRadius = new CornerRadius(
                        CelesteTokenApplier.TryGetLength(AlbumDetailCoverBorder, CelesteTokenCatalog.RadiusCover, 14));
                }
                if (ArtistSongsFrostPanel != null)
                {
                    ArtistSongsFrostPanel.CornerRadius = new CornerRadius(
                        CelesteTokenApplier.TryGetLength(ArtistSongsFrostPanel, CelesteTokenCatalog.RadiusMedium, 8));
                }
            }
            catch (Exception ex) { StartupLog.WriteException("RefreshDetailPagesForTheme", ex); }
        }
    }
}
