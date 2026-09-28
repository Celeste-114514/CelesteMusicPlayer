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

                    _geekNowPlayingTinted.Clear();
                    _geekNowPlayingActive = false;
                    _geekNowPlayingPhosphor = null;

                    RefreshLyricColorsForGeek();
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ApplyGeekNowPlaying", caught);
            }
        }

        /// <summary>ASCII 字符键统一磷光色：顶栏功能键 + 底部播放条按键都在 _geekAsciiButtons 里。</summary>
        private void TintAsciiKeys(Color phosphor)
        {
            SolidColorBrush brush = new(phosphor);
            foreach (Button button in _geekAsciiButtons.Keys)
            {
                GeekTint(button, Control.ForegroundProperty, brush);
            }
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
            if (_lyricTextBlocks.Count == 0)
            {
                return;
            }

            SyncLyricsToPosition(GetPlayer()?.PlaybackSession.Position ?? TimeSpan.Zero, force: true);
        }
    }
}
