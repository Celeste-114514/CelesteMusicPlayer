using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第三十刀（2026-10-31）：主题文件应用链宿主。
/// 从壳 <c>MainWindow.CelesteTheme.cs</c> / <c>ThemeFiles.cs</c> / <c>ShellPages.cs</c> /
/// <c>UiTheme.cs</c> 搬出：启动完成回调、应用主题文件（元素级令牌字典，绝不碰全局资源）、
/// 当前生效主题解析、深浅模式判定、令牌字典挂载与键数统计、换主题后刷详情页、
/// 以及两个「强调条跟随主题」的空实现（旧原生面板已下线）。
/// </summary>
public sealed partial class MainWindow
{
    private ResourceDictionary? _celesteTokenDictionary;
    private string? _celesteAppliedThemeName;
    private string? _lastAppliedThemeName;
    private bool _themeAppliedOnce;

    /// <summary>主题应用完成。参数是主题名，null=恢复默认。</summary>
    public event Action<string?>? CelesteThemeApplied;

    /// <summary>当前已应用到界面的主题名；null 表示用的是内置默认。</summary>
    public string? AppliedCelesteThemeName => _celesteAppliedThemeName;

        private void MainWindow_Loaded(dynamic sender, dynamic e)
        {
            StartupLog.Write("MainWindow_Loaded begin");
            try
            {
                InitializePlayerAndTimers();
                // The web shell is the sole product UI; legacy controls remain the backing session.
                OpenWebShell();
                ApplyTaskbarPlayerPreference();
                // 主题系统合闸（皮肤系统第 3 步）：启动时把设置里选的主题文件应用到界面。
                // 内部有「同名不重复应用」守卫，设置页后续变更再调也无额外开销。
                ReloadThemeFromSettings();
                // 任务栏缩略图按钮：必须在 Loaded 后注册，任务栏按钮就绪时才生效。
                // 立即调一次 Add()（首次准备 + 第一次尝试 AddButtons），
                // 然后启动一个短间隔 pump timer 让 ITaskbarThumbnailButtons.Pump() 推动延迟重试，
                // 覆盖 Explorer 任务栏图标 loaded→redraw 的临界窗口（~3 秒）。
                _taskbarButtons?.Add();
                StartThumbnailButtonsPump();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MainWindow_Loaded", ex);
            }
            finally
            {
                StartupLog.Write("MainWindow_Loaded end");
                // 2026-10-07 内存排查：窗口渲染层就绪后的基线。
                // 与 OnLaunched 那行相减 = WinUI 渲染 + 控件树 + 播放器的固定开销。
                MemProbe.Log("MainWindow已加载");
            }
        }

        // W13 第三十刀：主题应用主入口与令牌字典

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

                StartupLog.Write("[Theme] 已应用主题=" + (themeName ?? "(内置默认)") +
                                 " 模式=" + (dark ? "深色" : "浅色") + " 名称=" + theme?.Name);

                // ---- 3) 让代码里动态建控件的地方重新取一次值 ----
                RefreshDetailPagesForTheme();
                CelesteThemeApplied?.Invoke(themeName);

                // 设置面板的强调条也跟着重刷（阶段 0 新增的板块，走同一个取色入口）
                ReapplySettingsAccent();

                // 标签板块的子导航强调条同样重刷（阶段 5a 新增，走同一个取色入口）
                ReapplyTagPanelAccent();

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ApplyCelesteThemeAsync", ex);
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

        /// <summary>
        /// 当前是不是深色模式。极客皮肤强制深色——它的磷光配色只有深色一套。
        /// </summary>
        // internal：同上，令牌皮肤静态解析深浅用。
        internal bool ResolveIsDark()
        {
            try
            {
                if (IsGeekUiStyleActive()) return true;
                return Application.Current.RequestedTheme == ElementTheme.Dark;
            }
            catch
            {
                return Application.Current.RequestedTheme == ElementTheme.Dark;
            }
        }

        /// <summary>
        /// 当前生效主题：优先用户选定的主题文件，否则用内置默认。
        /// ⚠ MainWindow.WebHost.cs 里原本也有一份，装回时已删除，别再拷回来（CS0111 重复定义）。
        /// </summary>
        internal CelesteThemeFile? LoadCelesteActiveTheme()
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
        /// 把盘上的 CelesteThemeName 应用到界面。启动时（MainWindow_Loaded）与
        /// 设置页改完主题文件时调用。测试皮肤激活时主题文件就是皮肤配色来源，
        /// 由调用方负责随后重刷界面主题（换主题文件后需重新应用一次）。
        /// </summary>
        internal void ReloadThemeFromSettings()
        {
            try
            {
                string? name = AppSettingsStore.Load().CelesteThemeName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = null;
                }

                // 同一主题不重复应用：设置页很多开关变更都会走到这里，
                // 每次都重挂字典 + 推网页是纯浪费。
                if (_themeAppliedOnce
                    && string.Equals(_lastAppliedThemeName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _lastAppliedThemeName = name;
                _themeAppliedOnce = true;
                _ = ApplyCelesteThemeAsync(name);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ReloadThemeFromSettings", caught);
            }
        }

        /// <summary>
        /// 主题 / 皮肤切换后让设置面板跟上。面板内部是 NavigationView（走系统刷子），
        /// 不需要像音效处理那样手动重刷强调条，这里保留方法是为了不改旧调用点。
        /// 等将来把面板换成 ShellPageHost 那套时，这里再改成真的重刷。
        /// </summary>
        internal void ReapplySettingsAccent()
        {
        }

        /// <summary>主题 / 皮肤切换后重刷标签板块子导航的强调条。</summary>
        internal void ReapplyTagPanelAccent()
        {
            // Tags use the web shell theme tokens.
        }
    }
