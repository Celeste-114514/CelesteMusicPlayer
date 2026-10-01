using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives; // ButtonBase
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 极客皮肤下的迷你播放器外观。
    ///
    /// 迷你播放器是**独立顶级窗口**，不是 MainWindow 的子树 —— 主窗口那套 ApplyGeekChrome /
    /// GeekRestoreSubtree 一律走不到这里，而它自己以前完全没有极客处理（整文件零引用），
    /// 于是极客模式下它就是「琥珀主界面 + 蓝色实心圆 + 20px 大圆角」的违和存在。所以得自带一份。
    ///
    /// 与主窗口保持同一套规矩：圆角一律归零、等宽字体、强调色由磷光色取代主题色。
    /// 圆角在 XAML 里是写死的显式值（20/16/22/15），主题资源盖不掉，只能逐个改，
    /// 因此这里走「记住原值 → 退出时还原」的路子，保证能来回切、切回去不留痕。
    /// </summary>
    public sealed partial class MiniPlayerWindow
    {
        /// <summary>圆角 / 字体的切换动作：参数 true = 进极客，false = 还原。</summary>
        private readonly List<Action<bool>> _geekMiniJobs = new();

        private bool _geekMiniActive;
        private bool _geekMiniSettingsHooked;

        /// <summary>窗口外框圆角（DIP）。极客下归零，Win32 区域也跟着变方。</summary>
        private double _windowCornerDip = CornerRadiusDip;

        /// <summary>
        /// 订阅设置变化：用户在设置里切风格 / 换磷光色时，开着的迷你条要立刻跟上。
        /// AppSettingsStore.Changed 是静态事件，窗口关闭必须解绑，否则会把本窗口实例一直留在内存里。
        /// </summary>
        private void HookGeekMiniSettings()
        {
            if (_geekMiniSettingsHooked)
            {
                return;
            }

            AppSettingsStore.Changed -= OnSettingsChangedForGeekMini;
            AppSettingsStore.Changed += OnSettingsChangedForGeekMini;
            _geekMiniSettingsHooked = true;
        }

        private void UnhookGeekMiniSettings()
        {
            if (!_geekMiniSettingsHooked)
            {
                return;
            }

            AppSettingsStore.Changed -= OnSettingsChangedForGeekMini;
            _geekMiniSettingsHooked = false;
        }

        private void OnSettingsChangedForGeekMini()
            => Safe(() => ApplyGeekMini(MainWindow.IsGeekUiStyleActive(), force: true));

        /// <summary>
        /// 极客皮肤总入口：构造时调用一次，之后由设置变更驱动。幂等。
        /// force=true 用于「已经在极客里但磷光色换了」的场景 —— 此时 geek 仍是 true，
        /// 普通幂等会直接跳过，颜色就换不过来。
        /// </summary>
        private void ApplyGeekMini(bool geek, bool force = false)
        {
            Safe(() =>
            {
                bool phosphorChanged = force && geek && _geekMiniActive;
                if (geek == _geekMiniActive && !phosphorChanged)
                {
                    return;
                }

                // 先落状态再跑任务：任务里（如字符进度条首次刷新）要读 _geekMiniActive
                // 做守卫，循环后再置位会让那一刷被自己挡掉
                _geekMiniActive = geek;

                EnsureGeekMiniJobs();
                foreach (Action<bool> job in _geekMiniJobs)
                {
                    job(geek);
                }

                _windowCornerDip = geek ? 0 : CornerRadiusDip;
                // 区域按「宽高都没变过」缓存过一次就得作废，否则新的圆角值不会重新应用到窗口
                _lastRegionW = -1;
                _lastRegionH = -1;
                ApplyRoundedWindowRegion();

                // 强调色（滑块 + 播放实心圆）走同一条链路，只是把 accent 换成磷光
                RefreshAccentFromOwner();
            });
        }

        /// <summary>首次调用时按当前可视树登记需要来回切换的元素。</summary>
        private void EnsureGeekMiniJobs()
        {
            if (_geekMiniJobs.Count > 0)
            {
                return;
            }

            var mono = new FontFamily("Consolas");

            // ── 圆角归零（题目名称：这些值都在 XAML 里写死了，必须显式改回来）──
            TrackBorder(ChromeBorder, 20);
            TrackBorder(EdgeMaskBorder, 20);
            TrackBorder(CoverBorder, 16);

            TrackButton(PreviousButton, 16);
            TrackButton(PlayPauseButton, 22);
            TrackButton(NextButton, 16);
            TrackButton(VolumeButton, 15);
            TrackButton(QueueButton, 15);
            TrackButton(CloseMiniButton, 15);

            // ── 等宽字体 ──
            TextBlock[] texts =
            {
                TitleText, ArtistText, CurrentTimeText, TotalTimeText, VolumeValueText
            };
            foreach (TextBlock text in texts)
            {
                TextBlock captured = text;
                _geekMiniJobs.Add(isGeek =>
                {
                    if (captured == null)
                    {
                        return;
                    }

                    // null = 回落主题默认字体
                    captured.FontFamily = isGeek ? mono : null;
                });
            }

            // ── 极客细线描边：卡片边框换掉系统灰 ──
            Microsoft.UI.Xaml.Controls.Border[] frames = { ChromeBorder, EdgeMaskBorder, CoverBorder, QueuePanel };
            foreach (Microsoft.UI.Xaml.Controls.Border frame in frames)
            {
                Microsoft.UI.Xaml.Controls.Border captured = frame;
                SolidColorBrush? saved = null;
                _geekMiniJobs.Add(isGeek =>
                {
                    if (captured == null)
                    {
                        return;
                    }

                    if (isGeek)
                    {
                        saved = captured.BorderBrush as SolidColorBrush;
                        captured.BorderBrush = new SolidColorBrush(GeekMiniHairline);
                    }
                    else
                    {
                        captured.BorderBrush = saved ?? captured.BorderBrush;
                    }
                });
            }

            // ── 极客字符进度条：滑块整体隐形，Consolas 字符块盖在原位 ──
            // 和主窗口底部播放条同一套语言（那边是 GeekProgressText）：极客模式下
            // WinUI 那个带圆头滑块的进度条太"现代 UI"，跟终端风格不搭。拖动逻辑一行不改
            //（指针事件全挂在 Slider 上，字符条 IsHitTestVisible=false 不挡命中）。
            _geekMiniJobs.Add(isGeek =>
            {
                if (ProgressSlider == null)
                {
                    return;
                }

                if (isGeek)
                {
                    EnsureGeekMiniProgressBar();
                    if (_geekMiniProgressText != null)
                    {
                        // 换磷光色（force 重入）时画刷也要跟着换，不然字符条停在旧色上
                        _geekMiniProgressText.Foreground = new SolidColorBrush(MainWindow.GeekPhosphorColor());
                        _geekMiniProgressText.Visibility = Visibility.Visible;
                        ProgressSlider.Opacity = 0;
                        UpdateMiniGeekProgressText();
                    }
                }
                else
                {
                    if (_geekMiniProgressText != null)
                    {
                        _geekMiniProgressText.Visibility = Visibility.Collapsed;
                    }

                    ProgressSlider.Opacity = 1;
                }
            });
        }

        private TextBlock? _geekMiniProgressText;
        private bool _geekMiniProgressHooked;

        /// <summary>按需创建字符进度条（只建一次；之后切风格只切可见性）。</summary>
        private void EnsureGeekMiniProgressBar()
        {
            if (_geekMiniProgressText != null || ProgressRowGrid == null)
            {
                return;
            }

            var bar = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Foreground = new SolidColorBrush(MainWindow.GeekPhosphorColor()),
                Text = string.Empty,
            };
            // 和滑块同格同列：宽度天然一致，垂直居中压在滑轨上
            Grid.SetColumn(bar, 1);
            ProgressRowGrid.Children.Add(bar);
            _geekMiniProgressText = bar;

            if (!_geekMiniProgressHooked)
            {
                // 位置推进（程序性赋值）和用户拖动都会走 ValueChanged，
                // 宽度变化重排格数 —— 全都挂在滑块上，一处刷新两不误
                ProgressSlider.ValueChanged += (_, _) => UpdateMiniGeekProgressText();
                ProgressSlider.SizeChanged += (_, _) => UpdateMiniGeekProgressText();
                _geekMiniProgressHooked = true;
            }
        }

        /// <summary>
        /// 字符进度条刷新：格数按滑块实测宽度算（铺满整行才和原来的滑轨同长）。
        /// 画法复用主窗口的 SetGeekBarText：█ 实心 + 同字形半透明，绝不混字符
        /// （混字符会因缺字被换字体补，基线和宽都对不上，实测错位）。
        /// </summary>
        private void UpdateMiniGeekProgressText()
        {
            if (!_geekMiniActive
                || _geekMiniProgressText == null
                || _geekMiniProgressText.Visibility != Visibility.Visible)
            {
                return;
            }

            double max = ProgressSlider.Maximum;
            double ratio = max > 0 ? Math.Clamp(ProgressSlider.Value / max, 0, 1) : 0;

            double width = ProgressSlider.ActualWidth;
            if (width <= 40)
            {
                width = _geekMiniProgressText.ActualWidth;
            }

            if (width <= 40)
            {
                return; // 布局还没定：等 SizeChanged / 下一次进度跳动再画
            }

            double perChar = Math.Max(1.0, _geekMiniProgressText.FontSize * 0.55);
            int blocks = (int)Math.Clamp(Math.Round(width / perChar), 12, 120);
            int filled = (int)Math.Clamp(Math.Round(ratio * blocks), 0, blocks);

            MainWindow.SetGeekBarText(_geekMiniProgressText, string.Empty, filled, blocks);
        }

        private void TrackBorder(Microsoft.UI.Xaml.Controls.Border? border, double normalRadius)
        {
            if (border == null)
            {
                return;
            }

            Microsoft.UI.Xaml.Controls.Border captured = border;
            _geekMiniJobs.Add(isGeek =>
                captured.CornerRadius = isGeek ? new CornerRadius(0) : new CornerRadius(normalRadius));
        }

        private void TrackButton(ButtonBase? button, double normalRadius)
        {
            if (button == null)
            {
                return;
            }

            ButtonBase captured = button;
            _geekMiniJobs.Add(isGeek =>
                captured.CornerRadius = isGeek ? new CornerRadius(0) : new CornerRadius(normalRadius));
        }

        /// <summary>极客细线色（与主窗口 GeekHairline 同值，迷你播放器拿不到那个 private 常量，这里抄一份）。</summary>
        private static readonly Color GeekMiniHairline = Color.FromArgb(255, 0x2A, 0x33, 0x2A);

        /// <summary>
        /// 当前应当使用的强调色：极客模式返回磷光色，否则沿用原来的主题色逻辑。
        /// RefreshAccentFromOwner 的所有上色都从它取值。
        /// </summary>
        private Color CurrentAccentForMini()
            => _geekMiniActive ? MainWindow.GeekPhosphorColor() : RawSettingsAccentColor();

        /// <summary>
        /// 非极客时的强调色：原 RefreshAccentFromOwner 里的算法，原样搬过来（连「跟随系统」
        /// 也仍是那个兜底蓝，这里不改，免得动到非极客皮肤的既有观感）。
        /// </summary>
        private static Color RawSettingsAccentColor()
        {
            AppSettingsState s = AppSettingsStore.Load();
            return s.AccentSource == "Custom"
                ? (ThemeColorService.ParseHexColor(s.CustomAccentColor) ?? Color.FromArgb(255, 0, 120, 212))
                : Color.FromArgb(255, 0, 120, 212);
        }
    }
}
