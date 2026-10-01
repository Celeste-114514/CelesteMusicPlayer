using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 极客界面下「磷光色统一」（设置项「极客磷光色」，默认琥珀，可选荧光绿 / 青）。
    /// 覆盖三块：
    ///   1. ASCII 字符键：被 WrapButtonAscii 包过的所有按钮（顶栏功能键 + 底部播放条）前景色磷光化，
    ///      树里的 FontIcon / 文字自动继承；喜欢键实心红心是 label 级显式色，不受影响；
    ///   2. 播放歌曲信息页 NowPlayingPane：三个按钮 + 标题 / 歌手 / 专辑 / 音频信息 / 信号链，
    ///      层次用画刷 alpha 区分（标题满色 → 歌手专辑 85% → 音频信息 70% → 信号链 55%），
    ///      叠在元素自带 Opacity 上，不碰布局；
    ///   3. 歌词（当前行 / 邻近 / 远处 / 逐字）：在 Playback3 与 Lyrics 里按 GeekLyricAccentColor 分流。
    ///
    /// 还原为什么单独做：NowPlayingPane 与 BottomTransportBar 都是 MainContentGrid 的**兄弟节点**，
    /// ApplyGeekBrowseSkin 的整树还原（只扫 MainContentGrid）覆盖不到；
    /// 这里按「上过色的元素清单」逐个还原，不依赖它们在可视树里的位置
    ///（播放页 Collapsed 时元素照样在清单里，不会漏还原）。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>极客期间被我上过色的元素清单（退出时逐个还原，不整树扫）。</summary>
        private readonly List<DependencyObject> _geekNowPlayingTinted = new();

        /// <summary>
        /// 进度条（播放页底部那条）的主题资源键：Slider 模板里的"已播放填充 / 滑块圆环"
        /// 不读 Foreground 而是读这些键 —— 只改 Foreground 会出现"圆环变色、填充不变"。
        /// 键写在这里是为了退出极客时能逐个摘干净。
        /// </summary>
        private static readonly string[] SliderAccentResourceKeys =
        {
            "SliderTrackValueFill",
            "SliderTrackValueFillPointerOver",
            "SliderTrackValueFillPressed",
            "SliderTrackValueFillDisabled",
            "SliderThumbBackground",
            "SliderThumbBackgroundPointerOver",
            "SliderThumbBackgroundPressed",
            "SliderThumbBackgroundDisabled",
            "SliderThumbBackgroundFocused",
        };

        /// <summary>当前生效的磷光色；null = 极客没开。设置里换了色只重染，不重复刷整页。</summary>
        private Color? _geekNowPlayingPhosphor;

        private bool _geekNowPlayingActive;

        /// <summary>
        /// 磷光色统一总入口：由 ApplyGeekChrome 在界面风格切换、以及设置保存（AppSettingsStore.Changed）
        /// 时调用。幂等：已生效且磷光色没变 → 直接返回（设置保存会反复触发 ApplyGeekChrome）。
        /// </summary>
        internal void ApplyGeekNowPlaying(bool geek)
        {
            try
            {
                if (geek)
                {
                    Color phosphor = PhosphorColor(AppSettingsStore.Load().GeekPhosphorColor);
                    bool colorChanged = _geekNowPlayingPhosphor != phosphor;
                    _geekNowPlayingPhosphor = phosphor;

                    if (_geekNowPlayingActive && !colorChanged)
                    {
                        return;
                    }

                    TintAsciiKeys(phosphor);
                    TintNowPlayingPane(phosphor);
                    _geekNowPlayingActive = true;

                    // 歌词行是运行时长出来的：切风格 / 换磷光色都要立刻重染，不能等下一个 tick
                    RefreshLyricColorsForGeek();
                }
                else
                {
                    if (!_geekNowPlayingActive)
                    {
                        return;
                    }

                    foreach (DependencyObject element in _geekNowPlayingTinted)
                    {
                        GeekRestoreSubtree(element);
                    }

                    // 进度条的颜色是写进它自己的资源字典的：GeekRestoreSubtree 只还原依赖属性，
                    // 这几个键得单独摘掉，否则切回经典后进度条仍是磷光色
                    RemoveSliderAccentOverrides();

                    // 悬浮条字符进度条收回，滑块恢复不透明（拖动时一直被 Opacity=0 挡着视觉）
                    if (GeekFloatingProgressText != null)
                    {
                        GeekFloatingProgressText.Visibility = Visibility.Collapsed;
                        GeekFloatingProgressText.Inlines.Clear();
                    }

                    if (NowPlayingProgressSlider != null)
                    {
                        NowPlayingProgressSlider.Opacity = 1;
                    }

                    _geekNowPlayingTinted.Clear();
                    _geekNowPlayingActive = false;
                    _geekNowPlayingPhosphor = null;
                    _geekGlyphBrush = null;

                    RefreshLyricColorsForGeek();
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ApplyGeekNowPlaying", caught);
            }
        }

        /// <summary>当前按键色画刷（补色复用，免得每秒新建）：图形图标模式=白，字符键模式=磷光色。</summary>
        private SolidColorBrush? _geekGlyphBrush;

        /// <summary>极客下图标的统一色：白色。和其他皮肤（图景 / 经典）保持一致。</summary>
        private static readonly Color GeekIconWhite = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        /// <summary>顶栏功能键 + 底部播放条按键上色。
        /// 图形图标模式：图标一律白色（跟其他皮肤同一套图标、同一套颜色），
        ///   只刷「自己没写死颜色」的图标 —— 业务显式设色的（实心红心之类）原样保留；
        /// 字符键模式：字符沿用磷光色，靠按钮 Foreground 往下继承就够。</summary>
        private void TintAsciiKeys(Color phosphor)
        {
            bool iconMode = _geekAsciiIconMode == true;
            _geekGlyphBrush = new SolidColorBrush(iconMode ? GeekIconWhite : phosphor);

            foreach (Button button in _geekAsciiButtons.Keys)
            {
                if (iconMode)
                {
                    // 只刷图标本身，按钮 Foreground 不动（动了会连带把按钮里的文字也染了）
                    TintIconsInSubtree(button, _geekGlyphBrush, onlyIfUnset: true);
                }
                else
                {
                    GeekTint(button, Control.ForegroundProperty, _geekGlyphBrush);
                }
            }
        }

        /// <summary>把一棵子树里的图标字形直接刷成指定颜色（不依赖 Foreground 继承）。
        /// onlyIfUnset=true 时只刷「自己没写死前景色」的图标，业务显式设色的不动。</summary>
        internal void TintIconsInSubtree(DependencyObject node, SolidColorBrush brush, bool onlyIfUnset = false)
        {
            if (node is Microsoft.UI.Xaml.Controls.FontIcon or Microsoft.UI.Xaml.Controls.SymbolIcon)
            {
                bool alreadyColored = node.ReadLocalValue(Microsoft.UI.Xaml.Controls.IconElement.ForegroundProperty)
                                      != DependencyProperty.UnsetValue;

                if (!onlyIfUnset || !alreadyColored)
                {
                    GeekTint(node as FrameworkElement, Microsoft.UI.Xaml.Controls.IconElement.ForegroundProperty, brush);
                }

                // 图标本身是叶子（FontIcon 的 Glyph 不建子元素），不用再往下走
                return;
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                TintIconsInSubtree(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), brush, onlyIfUnset);
            }
        }

        /// <summary>
        /// 每秒自愈补色。图标模式下令牌可能被业务代码重建内容（播放 / 暂停换字形、
        /// 切歌重建播放条），重建出来的图标是新元素、没有局部前景色 ——
        /// 这里持续补，最慢一秒内颜色回来。非图形图标模式零开销。
        /// </summary>
        internal void RetintGeekIcons()
        {
            if (_geekAsciiIconMode != true || _geekGlyphBrush == null)
            {
                return;
            }

            foreach (Button button in _geekAsciiButtons.Keys)
            {
                TintIconsInSubtree(button, _geekGlyphBrush, onlyIfUnset: true);
            }

            // 左栏分类图标（图形图标模式下保留原图标，颜色同样走白色）
            foreach (NavItemRef item in NavItems)
            {
                if (item.Glyph is Microsoft.UI.Xaml.Controls.IconElement navIcon
                    && navIcon.Visibility == Visibility.Visible)
                {
                    WhitenGeekIcon(navIcon, _geekGlyphBrush);
                }
            }

            // 播放信息页标题栏三个按钮：整个按钮被染成磷光色，里面图标要拉回白色
            WhitenGeekIconIn(NowPlayingCollapseButton, _geekGlyphBrush);
            WhitenGeekIconIn(NowPlayingVisualButton, _geekGlyphBrush);
            WhitenGeekIconIn(NowPlayingLayoutButton, _geekGlyphBrush);
        }

        /// <summary>单个图标刷成极客统一色（白色）。自己写死过颜色的图标不动。</summary>
        private void WhitenGeekIcon(Microsoft.UI.Xaml.Controls.IconElement? icon, SolidColorBrush brush)
        {
            if (icon == null)
            {
                return;
            }

            if (icon.ReadLocalValue(Microsoft.UI.Xaml.Controls.IconElement.ForegroundProperty)
                != DependencyProperty.UnsetValue)
            {
                return;
            }

            GeekTint(icon as FrameworkElement, Microsoft.UI.Xaml.Controls.IconElement.ForegroundProperty, brush);
        }

        /// <summary>把一个按钮里的图标刷成极客统一色（白色）。</summary>
        private void WhitenGeekIconIn(FrameworkElement? button, SolidColorBrush brush)
        {
            if (button == null)
            {
                return;
            }

            TintIconsInSubtree(button, brush, onlyIfUnset: true);
        }

        /// <summary>播放歌曲信息页：三个按钮 + 四层文字（标题 → 歌手专辑 → 音频信息 → 信号链）。</summary>
        private void TintNowPlayingPane(Color phosphor)
        {
            // alpha 分层只动画刷不动 Opacity：音频信息 / 信号链自带 Opacity(0.85 / 0.7)，叠上去刚好
            SolidColorBrush bright = new(Color.FromArgb(255, phosphor.R, phosphor.G, phosphor.B));
            SolidColorBrush meta = new(Color.FromArgb(0xD9, phosphor.R, phosphor.G, phosphor.B));   // 歌手 / 专辑 85%
            SolidColorBrush info = new(Color.FromArgb(0xB3, phosphor.R, phosphor.G, phosphor.B));   // 音频信息 70%
            SolidColorBrush chain = new(Color.FromArgb(0x8C, phosphor.R, phosphor.G, phosphor.B));  // 信号链 55%

            // 三个按钮：Foreground 会继承给树里的 FontIcon 与说明文字（它们都没写死前景色）
            GeekTint(NowPlayingCollapseButton, Control.ForegroundProperty, bright);
            GeekTint(NowPlayingVisualButton, Control.ForegroundProperty, bright);
            GeekTint(NowPlayingLayoutButton, Control.ForegroundProperty, bright);

            // 图形图标模式：按钮整体是磷光色，里面那颗图标拉回白色（和其他皮肤一致）
            if (_geekAsciiIconMode == true)
            {
                var iconWhite = new SolidColorBrush(GeekIconWhite);
                WhitenGeekIconIn(NowPlayingCollapseButton, iconWhite);
                WhitenGeekIconIn(NowPlayingVisualButton, iconWhite);
                WhitenGeekIconIn(NowPlayingLayoutButton, iconWhite);
            }

            // 标题最亮
            GeekTint(NowPlayingTitleText, TextBlock.ForegroundProperty, bright);

            // 歌手 / 专辑：链接按钮里的 TextBlock 自己写着主题资源前景色，必须直接改 TextBlock
            GeekTint(NowPlayingArtistText, TextBlock.ForegroundProperty, meta);
            GeekTint(NowPlayingAlbumText, TextBlock.ForegroundProperty, meta);

            GeekTint(NowPlayingAudioInfoText, TextBlock.ForegroundProperty, info);
            GeekTint(SignalChainInfoText, TextBlock.ForegroundProperty, chain);

            // 播放页底部那条进度条：默认跟主题强调色走，极客下必须跟磷光色一起变。
            // 模板里的填充/圆环读的是上面那串资源键，所以画刷要写进它自己的资源字典。
            if (NowPlayingProgressSlider != null)
            {
                GeekTint(NowPlayingProgressSlider, Control.ForegroundProperty, bright);
                foreach (string key in SliderAccentResourceKeys)
                {
                    NowPlayingProgressSlider.Resources[key] = bright;
                }
            }

            // 悬浮控制条（播放页底部那条）的进度条：字符化——滑块 Opacity=0 照常可拖，
            // 上面盖一行 Consolas 字符块（用户 2026-09-29 反馈「详情页进度条没跟随极客样式」）。
            // 底部常驻条那一套（GeekProgressText）在 GeekTransport.cs，这里是悬浮条的同款实现。
            if (GeekFloatingProgressText != null)
            {
                GeekFloatingProgressText.Visibility = Visibility.Visible;
                GeekFloatingProgressText.Foreground = bright;
                if (!_geekFloatingTextSized)
                {
                    // 字符条自身尺寸变了（首次布局 / 悬浮条宽度重算）要重排格数
                    GeekFloatingProgressText.SizeChanged += (_, _) => UpdateGeekFloatingProgressText();
                    _geekFloatingTextSized = true;
                }
            }

            if (NowPlayingProgressSlider != null)
            {
                // 滑块透明但**照常可拖**：拖动逻辑（PointerPressed/Released + seek）一行不改，
                // 只是看不见圆头了。恢复见 ApplyGeekNowPlaying 的 else 分支。
                NowPlayingProgressSlider.Opacity = 0;
                if (!_geekFloatingSliderSized)
                {
                    // 格数按滑块实测宽度算：滑块尺寸一变就重排字符条
                    NowPlayingProgressSlider.SizeChanged += (_, _) => UpdateGeekFloatingProgressText();
                    _geekFloatingSliderSized = true;
                }
            }

            UpdateGeekFloatingProgressText();
        }

        private bool _geekFloatingTextSized;
        private bool _geekFloatingSliderSized;

        /// <summary>
        /// 悬浮控制条的字符进度条：读 NowPlayingProgressSlider 的 Value/Maximum/实测宽度，
        /// 按等宽字格数（宽/0.55em，clamp 12~220）画 █ 实心 + 半透明，画法与主界面
        /// GeekProgressText 完全一致（SetGeekBarText）。字符条 Collapsed（非极客）时直接跳过。
        /// </summary>
        private void UpdateGeekFloatingProgressText()
        {
            if (GeekFloatingProgressText == null || GeekFloatingProgressText.Visibility != Visibility.Visible)
            {
                return;
            }

            if (NowPlayingProgressSlider == null)
            {
                return;
            }

            double max = NowPlayingProgressSlider.Maximum;
            double ratio = max > 0 ? Math.Clamp(NowPlayingProgressSlider.Value / max, 0.0, 1.0) : 0;

            // 参考宽度拿滑块实测宽度（两者同格、完全同长）；布局未定时回落字符条自身宽度
            double width = NowPlayingProgressSlider.ActualWidth;
            if (width <= 40)
            {
                width = GeekFloatingProgressText.ActualWidth;
            }

            if (width <= 40)
            {
                return; // 布局还没定：等 SizeChanged / 下一次进度跳动再画
            }

            double perChar = Math.Max(1.0, GeekFloatingProgressText.FontSize * 0.55);
            int blocks = (int)Math.Clamp(Math.Round(width / perChar), 12, 220);

            int filled = (int)Math.Round(ratio * blocks);
            filled = Math.Clamp(filled, 0, blocks);

            SetGeekBarText(GeekFloatingProgressText, string.Empty, filled, blocks);
        }

        /// <summary>摘掉进度条上的极客配色（还原分支用）。</summary>
        private void RemoveSliderAccentOverrides()
        {
            if (NowPlayingProgressSlider == null)
            {
                return;
            }

            foreach (string key in SliderAccentResourceKeys)
            {
                NowPlayingProgressSlider.Resources.Remove(key);
            }
        }

        /// <summary>记原值 + 写极客值 + 进还原清单（三连，漏一个退出极客就会留残色）。</summary>
        private void GeekTint(FrameworkElement? element, DependencyProperty property, Brush brush)
        {
            if (element == null)
            {
                return;
            }

            GeekStore(element, property, brush);
            if (!_geekNowPlayingTinted.Contains(element))
            {
                _geekNowPlayingTinted.Add(element);
            }
        }

        /// <summary>
        /// 歌词磷光色：非极客返回 null（歌词走主题强调色链，行为与以前完全一致）。
        /// 切风格 / 换磷光色时由 ApplyGeekNowPlaying 触发一次强制重染。
        /// </summary>
        internal Color? GeekLyricAccentColor()
            => _geekNowPlayingActive && _geekNowPlayingPhosphor is Color color ? color : (Color?)null;

        /// <summary>磷光色叠加 alpha 的画刷（歌词分层用：邻近 / 远处 / 未唱字）。</summary>
        internal static SolidColorBrush PhosphorShade(Color baseColor, byte alpha)
            => new(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));

        /// <summary>立刻按当前风格重染歌词（极客开关 / 磷光色变更时调用；没加载歌词时是空操作）。</summary>
        private void RefreshLyricColorsForGeek()
        {
            // 终端布局歌词面板：换磷光色时重染（主歌词页空/非空都不影响它）
            if (_layoutIsTerminal)
            {
                RetintTerminalLyrics();
            }

            if (_lyricTextBlocks.Count == 0)
            {
                return;
            }

            SyncLyricsToPosition(GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero, force: true);
        }
    }
}
