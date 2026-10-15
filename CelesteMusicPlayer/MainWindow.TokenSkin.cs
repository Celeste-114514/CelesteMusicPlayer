// 令牌测试皮肤（皮肤系统阶段 1 · 平行于经典的「测试皮肤」）
//
// 定位：用户要一个「基于经典、但全部颜色来自主题文件令牌」的皮肤，放在设置里随时
//       和经典对比；原生经典/图景/极客的渲染路径一行不动。
//
// 三条实现腿：
//   ① 系统刷子键覆盖字典：挂在 RootShell.Resources.MergedDictionaries，键就是系统刷子名
//      （CardStrokeColorDefaultBrush 等），值=令牌色。XAML 里 ~370 处 {ThemeResource 系统刷子}
//      在 RootShell 子树内自动改查这本字典 → 整树换色零 XAML 改动。摘掉字典即还原。
//      ⚠ 只影响主窗口子树；二级窗口（设置页等）保持系统色，当"控制面板"用。
//   ② FrostedGlass 取色钩子：经典分支里写死的面板白/炭灰、窗口根底色、标题栏前景色，
//      令牌激活时改问令牌要（钩子为 null 时原样走经典硬编码，退出皮肤无残留）。
//   ③ 代码取色函数分支：ResolveUiBaseTintColor / ResolveAccentBrush / ResolveCapsuleFillBrush /
//      ResolveClassicRowSelectionColor / ResolveRowHoverBrush / CreateMultiSelectFrostBrush /
//      行前景色，令牌激活时返回令牌值（调用方无需知道皮肤存在）。
//
// 与经典模式的关系：Token 模式=经典模式全套行为（不透明面板/隐藏背景层/彩色导航图标）
//   + 上面三条令牌注入口。FrostedGlass.ClassicMode 照常存 "Light"/"Dark"，所以所有
//   `if (FrostedGlass.ClassicMode != null)` 的既有分支全部复用，不新写平行逻辑。
//
// 明暗口径：Token 跟随系统（同「经典·跟随系统」）；深浅两组令牌由 CelesteTokenCatalog 定义。
// 已知边界（阶段 2+ 再收）：形状（圆角）只有代码侧已接令牌的两处（封面/面板），
//   XAML 里写死的 CornerRadius 要等逐页令牌化；运行中切换 Windows 深浅色不热刷新
//   （经典模式同样不刷新，非本次回归）。

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>设置项「界面风格」里的令牌测试皮肤。</summary>
        internal const string UiStyleToken = "Token";

        /// <summary>当前是否处于令牌测试皮肤。低频路径（切风格、菜单、一次性计算）用这个，读的是设置真值。</summary>
        internal static bool IsTokenSkinActive()
        {
            try
            {
                return AppSettingsStore.Load().UiStyleMode == UiStyleToken;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 令牌皮肤的进程内缓存（第一次问的时候读一次设置，之后沿用）。
        /// 给"每条列表行都要判断一次"的高频路径用：行前景色、行分隔线、选中底色等。
        /// AppSettingsStore.Load() 每次都会 Clone 一整个设置对象，3832 首曲库逐行调用会拖慢滚动。
        /// 界面风格切换时由 ApplyUiStyleMode 立即刷新，不会读到过期值。
        /// </summary>
        private static bool? _tokenSkinCached;

        /// <summary>高频路径读这个（详见 _tokenSkinCached 注释）。</summary>
        internal static bool TokenSkinCached => _tokenSkinCached ??= IsTokenSkinActive();

        internal static void RefreshTokenSkinCache(bool token) => _tokenSkinCached = token;

        // ---------------- 覆盖字典：系统刷子键 → 令牌 ----------------

        /// <summary>
        /// 系统刷子资源键 → 主题令牌名。按 XAML 实际引用量排序（CardStroke 378 次居首）。
        /// 只收 Brush 型键；Color 型键（SubtleFillColorSecondary 等）全部用在二级窗口，
        /// 不在 RootShell 子树内，覆写了也不生效，故不收。
        /// SystemFillColorCautionBrush（危险红，36 次）无对应令牌，保留系统色。
        /// ButtonDangerBackgroundBrush 是 App.xaml 自定义语义键（按钮三档底色之"危险"），
        /// 经典模式为固定红，令牌模式下由此改查 Button.Danger。
        /// </summary>
        private static readonly (string SystemKey, string Token)[] TokenBrushMap =
        {
            ("CardStrokeColorDefaultBrush",          CelesteTokenCatalog.CardStroke),
            ("CardBackgroundFillColorDefaultBrush",  CelesteTokenCatalog.CardBackground),
            ("SubtleFillColorSecondaryBrush",        CelesteTokenCatalog.CardBackgroundHover),
            ("TextFillColorPrimaryBrush",            CelesteTokenCatalog.TextPrimary),
            ("AccentFillColorSecondaryBrush",        CelesteTokenCatalog.AccentSolid),
            ("TextFillColorSecondaryBrush",          CelesteTokenCatalog.TextSecondary),
            ("AccentFillColorDefaultBrush",          CelesteTokenCatalog.AccentSolid),
            ("LayerFillColorDefaultBrush",           CelesteTokenCatalog.OverlayBackground),
            ("TextFillColorTertiaryBrush",           CelesteTokenCatalog.TextDisabled),
            ("DividerStrokeColorDefaultBrush",       CelesteTokenCatalog.Divider),
            ("ControlFillColorDefaultBrush",         CelesteTokenCatalog.InputBackground),
            ("CardBackgroundFillColorSecondaryBrush",CelesteTokenCatalog.CardBackgroundHover),
            ("SystemControlHighlightListAccentLowBrush", CelesteTokenCatalog.SelectionBackground),
            ("TextOnAccentFillColorPrimaryBrush",    CelesteTokenCatalog.AccentText),
            ("ControlStrokeColorDefaultBrush",       CelesteTokenCatalog.InputStroke),
            ("ControlFillColorSecondaryBrush",       CelesteTokenCatalog.InputBackground),
            ("ApplicationPageBackgroundThemeBrush",  CelesteTokenCatalog.AppBackground),
            ("SystemControlHighlightListLowBrush",   CelesteTokenCatalog.SelectionBackground),
            ("SolidBackgroundFillColorBaseBrush",    CelesteTokenCatalog.AppBackground),
            ("AcrylicInAppFillColorDefaultBrush",    CelesteTokenCatalog.PanelBackground),
            ("ButtonDangerBackgroundBrush",          CelesteTokenCatalog.ButtonDanger),
        };

        private ResourceDictionary? _tokenSkinDict;
        private string _tokenSkinDictKey = "";

        /// <summary>进入令牌测试皮肤：挂 FrostedGlass 钩子 + 建覆盖字典。由 ApplyUiStyleMode 调。</summary>
        internal void ApplyTokenSkin()
        {
            // 钩子捕获本窗口实例；RemoveTokenSkin 负责摘，退出皮肤不留残留
            FrostedGlass.TokenPanelColor = () => ResolveTokenSkinColor(CelesteTokenCatalog.PanelBackground);
            FrostedGlass.TokenRootColor = () => ResolveTokenSkinColor(CelesteTokenCatalog.AppBackground);
            FrostedGlass.TokenTitleBarForeground = () => ResolveTokenSkinColor(CelesteTokenCatalog.TextPrimary);
            RebuildTokenSkinDictionary(force: false);
            StartupLog.Write("[TokenSkin] 测试皮肤已激活（覆盖字典 + FrostedGlass 钩子）");
        }

        /// <summary>离开令牌测试皮肤：摘钩子 + 撤覆盖字典。任何模式下调用都安全（幂等）。</summary>
        internal void RemoveTokenSkin()
        {
            FrostedGlass.TokenPanelColor = null;
            FrostedGlass.TokenRootColor = null;
            FrostedGlass.TokenTitleBarForeground = null;

            if (_tokenSkinDict != null)
            {
                try { RootShell?.Resources.MergedDictionaries.Remove(_tokenSkinDict); }
                catch { /* 已经不在里面了，无所谓 */ }
            }
            _tokenSkinDict = null;
            _tokenSkinDictKey = "";
        }

        /// <summary>
        /// 主题文件换名/改内容后刷新（ApplyCelesteThemeAsync 调）。
        /// force=true 无视缓存重新读盘——用户可能刚编辑过同名主题文件。
        /// </summary>
        internal void RefreshTokenSkinIfActive()
        {
            if (!IsTokenSkinActive())
            {
                return;
            }
            RebuildTokenSkinDictionary(force: true);
        }

        /// <summary>
        /// 重建（或按缓存跳过）系统刷子覆盖字典。键=主题名|深浅，三者都没变就不动，
        /// 避免设置页每次保存都整树重算。字典整本换掉会触发全树 ThemeResource 重求值。
        /// </summary>
        private void RebuildTokenSkinDictionary(bool force)
        {
            var host = RootShell;
            if (host == null)
            {
                return;
            }

            try
            {
                bool dark = ResolveIsDark();
                string name = AppSettingsStore.Load().CelesteThemeName ?? "";
                string key = name + "|" + (dark ? "D" : "L");
                if (!force && _tokenSkinDict != null && _tokenSkinDictKey == key)
                {
                    return;
                }

                var theme = LoadCelesteActiveTheme() ?? CelesteThemeStore.CreateDefault();
                var dict = new ResourceDictionary();
                foreach (var (systemKey, tokenName) in TokenBrushMap)
                {
                    var raw = CelesteThemeStore.Resolve(theme, tokenName, dark);
                    var c = CelesteTokenApplier.ParseColor(raw ?? "");
                    if (c.HasValue)
                    {
                        dict[systemKey] = new SolidColorBrush(c.Value);
                    }
                }

                // 先摘旧的再挂新的：同一时刻只允许一本"我们的"覆盖字典
                if (_tokenSkinDict != null)
                {
                    try { host.Resources.MergedDictionaries.Remove(_tokenSkinDict); }
                    catch { /* 已经不在里面了，无所谓 */ }
                }
                host.Resources.MergedDictionaries.Add(dict);
                _tokenSkinDict = dict;
                _tokenSkinDictKey = key;

                StartupLog.Write("[TokenSkin] 覆盖字典已挂载 键数=" + dict.Keys.Count +
                                 " 深浅=" + (dark ? "深" : "浅") + " 主题=" + (name.Length == 0 ? "(内置默认)" : name));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RebuildTokenSkinDictionary", caught);
            }
        }

        // ---------------- 令牌取色（代码侧 resolver 共用） ----------------

        // 静态缓存：ResolveAccentBrush 每行都会问一次，不能每次解析主题 JSON。
        private static CelesteThemeFile? _tokenColorTheme;
        private static string _tokenColorKey = "";
        private static bool _tokenColorDark;

        /// <summary>
        /// 取一个令牌的当前颜色（按生效主题文件 + 当前深浅）。解析失败返回 null，
        /// 调用方据此回落到经典行为——宁可这个令牌不生效，也不要把界面涂崩。
        /// </summary>
        internal static Color? ResolveTokenSkinColor(string tokenName)
        {
            var window = Instance;
            if (window == null)
            {
                return null;
            }

            try
            {
                bool dark = window.ResolveIsDark();
                string name = AppSettingsStore.Load().CelesteThemeName ?? "";
                string key = name + "|" + (dark ? "D" : "L");
                if (_tokenColorTheme == null || _tokenColorKey != key)
                {
                    _tokenColorTheme = window.LoadCelesteActiveTheme() ?? CelesteThemeStore.CreateDefault();
                    _tokenColorKey = key;
                    _tokenColorDark = dark;
                }

                var raw = CelesteThemeStore.Resolve(_tokenColorTheme, tokenName, _tokenColorDark);
                return CelesteTokenApplier.ParseColor(raw ?? "");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ResolveTokenSkinColor", caught);
                return null;
            }
        }

        /// <summary>主题文件热替换后让取色缓存失效（RefreshTokenSkinIfActive 一并调）。</summary>
        internal static void InvalidateTokenColorCache()
        {
            _tokenColorTheme = null;
            _tokenColorKey = "";
        }

        /// <summary>选中族令牌 + 透明度档：悬停 40% / 多选底 30% / 胶囊 55% / 选中 100%。
        /// 经典皮肤这四层是手写灰阶透明度；令牌皮肤统一从 Selection.Background 派生，
        /// 主题作者改一个值就能整套调选中族观感。</summary>
        internal static SolidColorBrush? ResolveTokenSelectionBrush(byte alpha)
        {
            var c = ResolveTokenSkinColor(CelesteTokenCatalog.SelectionBackground);
            if (!c.HasValue)
            {
                return null;
            }
            return new SolidColorBrush(Color.FromArgb(alpha, c.Value.R, c.Value.G, c.Value.B));
        }
    }
}
