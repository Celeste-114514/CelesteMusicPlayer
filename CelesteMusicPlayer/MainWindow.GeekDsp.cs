using Microsoft.UI; // Microsoft.UI.Colors（WinAppSDK 桌面项目里 Colors 在这个命名空间，对齐 ThemeColorService 的写法）
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 极客皮肤下「音效处理 DSP 面板」的磷光染色。
    ///
    /// ── 为什么 DSP 面板以前在极客下是蓝的 ──
    /// 非极客皮肤里的「主题色」是由 ThemeColorService 在 App 启动（窗口创建前）一次性写进
    /// <b>Application.Current.Resources</b> 的一整套 accent 键，于是所有写
    /// {ThemeResource AccentFillColorSecondaryBrush} / SliderTrackValueFill 的控件全自动跟随。
    /// 极客模式则明确禁止碰全局资源 —— 运行时改 App 级系统键会触发 WinUI 原生崩溃 0xc000027b
    /// （托管 try-catch 拦不住，见 ThemeColorService 与 UiTheme.ApplyGeekChrome 的注释）。
    /// 这条铁律的代价就是：极客下没有全局 accent 可用，DSP 面板长期只分到「进行时的手工逐元素染色」，
    /// 而那五个 ApplyGeek* 函数恰好一个都没覆盖它，于是同一屏出现「琥珀顶栏 + 蓝色 DSP 面板」。
    ///
    /// ── 本文件的做法 ──
    /// 把 ThemeColorService 覆盖的那整套 accent 键，原样写成 **AudioFxBorder.Resources 上的元素级副本**，
    /// 值换成磷光色。好处：
    ///   1. 面板内所有吃 accent 主题的控件（徽章 / Slider / ToggleSwitch / CheckBox / RadioButton /
    ///      ListView 选中态 …）自动变磷光，覆盖面与「非极客下主题色作用在这些控件上的效果」等价；
    ///   2. 不用逐个控件 GeekStore 备份依赖属性，还原只需 Remove 这批键，不会漏；
    ///   3. 绝不接触 Application.Current.Resources，避开 0xc000027b。
    ///
    /// ── 两个必须单独处理的例外（元素级资源只影响之后新建的元素）──
    ///   · 已在树上的 Slider / ToggleSwitch：必须重建一次控件模板才认新值
    ///     （照抄 ThemeColorService.ApplySliderAccent 里已验证的手法：清 Template → 装回）。
    ///   · 纯代码画的元素：EQ 曲线 Canvas、导航选中条 Bar / 状态圆点 Dot —— 它们不读 ThemeResource，
    ///     改由代码侧的 <see cref="GeekDspAccentColor"/> 分支接管。
    /// </summary>
    public sealed partial class MainWindow
    {
        private bool _geekDspActive;
        private Color? _geekDspPhosphor;

        /// <summary>
        /// DSP 面板当前的强调色：极客模式返回磷光色，非极客返回 null（含义＝沿用系统/自定义主题色）。
        /// 面板里凡是「要跟强调色走」的地方一律问它，不要自己读 Application.Resources。
        /// </summary>
        internal Color? GeekDspAccentColor()
            => _geekDspActive && _geekDspPhosphor is Color phosphor ? phosphor : (Color?)null;

        /// <summary>面板强调色画刷：极客下给磷光，否则回落系统主题色（与全局行为一致）。</summary>
        internal Brush DspAccentBrush()
            => GeekDspAccentColor() is Color accent ? new SolidColorBrush(accent) : ResolveAccentBrush();

        /// <summary>
        /// ThemeColorService 在非极客下注入的全部 accent 资源键。
        /// 极客化的写入与还原都用同一份清单，杜绝「加的时候少写一个、退的时候漏删一个」。
        /// </summary>
        private static readonly string[] GeekDspAccentResourceKeys =
        {
            "SystemAccentColor",
            "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",

            "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "AccentFillColorDisabledBrush", "AccentFillColorSelectedTextBackgroundBrush",

            "AccentButtonBackground", "AccentButtonBackgroundPointerOver",
            "AccentButtonBackgroundPressed", "AccentButtonBackgroundDisabled",
            "AccentButtonForeground", "AccentButtonForegroundPointerOver",
            "AccentButtonForegroundPressed", "AccentButtonForegroundDisabled",

            "SliderTrackValueFill", "SliderTrackValueFillPointerOver",
            "SliderTrackValueFillPressed", "SliderTrackValueFillDisabled",
            "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillDisabled",
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver",
            "SliderThumbBackgroundPressed", "SliderThumbBackgroundDisabled",
            "SliderThumbBorderBrush", "SliderThumbBorderBrushPointerOver",
            "SliderThumbBorderBrushPressed", "SliderThumbBorderBrushDisabled",

            "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver",
            "ToggleSwitchFillOnPressed", "ToggleSwitchFillOnDisabled",

            "CheckBoxCheckBackgroundStroke", "CheckBoxCheckBackgroundStrokePointerOver",
            "CheckBoxCheckBackgroundStrokePressed",
            "CheckBoxCheckBackgroundFill", "CheckBoxCheckBackgroundFillPointerOver",
            "CheckBoxCheckBackgroundFillPressed",
            "CheckBoxCheckGlyphForeground",

            "RadioButtonCheckBackgroundStroke", "RadioButtonCheckBackgroundStrokePointerOver",
            "RadioButtonCheckBackgroundFill",

            "SystemControlForegroundAccentBrush", "SystemControlBackgroundAccentBrush",
            "SystemControlHighlightAccentBrush", "SystemControlHighlightAltAccentBrush",
            "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush",
            "SystemControlHighlightListAccentHighBrush",
            "AccentTextFillColorPrimaryBrush",

            "ListViewItemBackgroundSelected", "ListViewItemBackgroundSelectedPointerOver",
            "ListViewItemBackgroundSelectedPressed",
            "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundPressed",
            "GridViewItemBackgroundSelected", "GridViewItemBackgroundSelectedPointerOver",
            "GridViewItemBackgroundPointerOver", "GridViewItemBackgroundPressed",
        };

        /// <summary>
        /// DSP 面板磷光染色总入口：由 ApplyGeekChrome 在切换界面风格时调用。幂等 —— 设置保存会反复触发，
        /// 但改显示在面板藏起来时（Visibility=Collapsed）也是安全的，重新切出来时资源已经就位。
        /// </summary>
        internal void ApplyGeekDsp(bool geek)
        {
            try
            {
                if (AudioFxBorder == null)
                {
                    return;
                }

                if (geek)
                {
                    Color phosphor = GeekPhosphorColor();
                    WriteGeekDspAccentResources(phosphor);
                    _geekDspPhosphor = phosphor;
                    _geekDspActive = true;
                }
                else
                {
                    if (!_geekDspActive)
                    {
                        return;
                    }

                    RemoveGeekDspAccentResources();
                    _geekDspActive = false;
                    _geekDspPhosphor = null;
                }

                // 已经在树上的 Slider / ToggleSwitch：重建模板才能让局部资源立刻见效
                ReloadDspControlTemplates();

                // 纯代码绘制的部分，重刷一遍
                RefreshDspNavGeekAccent();
                RedrawAudioFxEqCurve();
                RefreshDspRackRows();
                RedrawDspCompCanvas();
                RedrawDspMatrixCanvas();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ApplyGeekDsp", caught);
            }
        }

        /// <summary>把磷光色当作 accent 写进 AudioFxBorder 的元素级资源字典。</summary>
        private void WriteGeekDspAccentResources(Color accent)
        {
            Color light1 = MixToward(accent, Colors.White, 0.72);
            Color light2 = MixToward(accent, Colors.White, 0.85);
            Color light3 = MixToward(accent, Colors.White, 0.93);
            Color dark1 = MixToward(accent, Colors.Black, 0.40);
            Color dark2 = MixToward(accent, Colors.Black, 0.70);
            Color dark3 = MixToward(accent, Colors.Black, 0.85);

            var accentBrush = new SolidColorBrush(accent);
            var light1Brush = new SolidColorBrush(light1);
            var light2Brush = new SolidColorBrush(light2);
            var dark1Brush = new SolidColorBrush(dark1);
            var dark2Brush = new SolidColorBrush(dark2);

            (string Key, object Value)[] pairs =
            {
                ("SystemAccentColor", accent),
                ("SystemAccentColorLight1", light1),
                ("SystemAccentColorLight2", light2),
                ("SystemAccentColorLight3", light3),
                ("SystemAccentColorDark1", dark1),
                ("SystemAccentColorDark2", dark2),
                ("SystemAccentColorDark3", dark3),

                ("AccentFillColorDefaultBrush", accentBrush),
                ("AccentFillColorSecondaryBrush", light1Brush),
                ("AccentFillColorTertiaryBrush", light2Brush),
                ("AccentFillColorDisabledBrush", new SolidColorBrush(Color.FromArgb(102, accent.R, accent.G, accent.B))),
                ("AccentFillColorSelectedTextBackgroundBrush", accentBrush),

                ("AccentButtonBackground", accentBrush),
                ("AccentButtonBackgroundPointerOver", light1Brush),
                ("AccentButtonBackgroundPressed", dark1Brush),
                ("AccentButtonBackgroundDisabled", dark2Brush),
                ("AccentButtonForeground", new SolidColorBrush(Colors.White)),
                ("AccentButtonForegroundPointerOver", new SolidColorBrush(Colors.White)),
                ("AccentButtonForegroundPressed", new SolidColorBrush(Colors.White)),
                ("AccentButtonForegroundDisabled", new SolidColorBrush(Color.FromArgb(128, 255, 255, 255))),

                ("SliderTrackValueFill", accentBrush),
                ("SliderTrackValueFillPointerOver", light1Brush),
                ("SliderTrackValueFillPressed", dark1Brush),
                ("SliderTrackValueFillDisabled", dark2Brush),
                ("SliderTrackFill", new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B))),
                ("SliderTrackFillPointerOver", new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B))),
                ("SliderTrackFillDisabled", new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B))),
                ("SliderThumbBackground", accentBrush),
                ("SliderThumbBackgroundPointerOver", light1Brush),
                ("SliderThumbBackgroundPressed", dark1Brush),
                ("SliderThumbBackgroundDisabled", dark2Brush),
                ("SliderThumbBorderBrush", new SolidColorBrush(Colors.Transparent)),
                ("SliderThumbBorderBrushPointerOver", new SolidColorBrush(Colors.Transparent)),
                ("SliderThumbBorderBrushPressed", new SolidColorBrush(Colors.Transparent)),
                ("SliderThumbBorderBrushDisabled", new SolidColorBrush(Colors.Transparent)),

                ("ToggleSwitchFillOn", accentBrush),
                ("ToggleSwitchFillOnPointerOver", light1Brush),
                ("ToggleSwitchFillOnPressed", dark1Brush),
                ("ToggleSwitchFillOnDisabled", dark2Brush),

                ("CheckBoxCheckBackgroundStroke", accentBrush),
                ("CheckBoxCheckBackgroundStrokePointerOver", light1Brush),
                ("CheckBoxCheckBackgroundStrokePressed", dark1Brush),
                ("CheckBoxCheckBackgroundFill", accentBrush),
                ("CheckBoxCheckBackgroundFillPointerOver", light1Brush),
                ("CheckBoxCheckBackgroundFillPressed", dark1Brush),
                ("CheckBoxCheckGlyphForeground", new SolidColorBrush(Colors.White)),

                ("RadioButtonCheckBackgroundStroke", accentBrush),
                ("RadioButtonCheckBackgroundStrokePointerOver", light1Brush),
                ("RadioButtonCheckBackgroundFill", accentBrush),

                ("SystemControlForegroundAccentBrush", accentBrush),
                ("SystemControlBackgroundAccentBrush", accentBrush),
                ("SystemControlHighlightAccentBrush", accentBrush),
                ("SystemControlHighlightAltAccentBrush", accentBrush),
                ("SystemControlHighlightListAccentLowBrush", new SolidColorBrush(Color.FromArgb(51, accent.R, accent.G, accent.B))),
                ("SystemControlHighlightListAccentMediumBrush", new SolidColorBrush(Color.FromArgb(102, accent.R, accent.G, accent.B))),
                ("SystemControlHighlightListAccentHighBrush", new SolidColorBrush(Color.FromArgb(153, accent.R, accent.G, accent.B))),
                ("AccentTextFillColorPrimaryBrush", accentBrush),

                ("ListViewItemBackgroundSelected", new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B))),
                ("ListViewItemBackgroundSelectedPointerOver", new SolidColorBrush(Color.FromArgb(96, accent.R, accent.G, accent.B))),
                ("ListViewItemBackgroundSelectedPressed", new SolidColorBrush(Color.FromArgb(128, accent.R, accent.G, accent.B))),
                ("ListViewItemBackgroundPointerOver", new SolidColorBrush(Colors.Transparent)),
                ("ListViewItemBackgroundPressed", new SolidColorBrush(Colors.Transparent)),
                ("GridViewItemBackgroundSelected", new SolidColorBrush(Color.FromArgb(64, accent.R, accent.G, accent.B))),
                ("GridViewItemBackgroundSelectedPointerOver", new SolidColorBrush(Color.FromArgb(96, accent.R, accent.G, accent.B))),
                ("GridViewItemBackgroundPointerOver", new SolidColorBrush(Colors.Transparent)),
                ("GridViewItemBackgroundPressed", new SolidColorBrush(Colors.Transparent)),
            };

            ResourceDictionary res = AudioFxBorder.Resources;
            foreach ((string key, object value) in pairs)
            {
                res[key] = value;
            }
        }

        /// <summary>摘掉极客写入的 accent 资源键：退出极客后 DSP 面板回落到系统/自定义主题色。</summary>
        private void RemoveGeekDspAccentResources()
        {
            ResourceDictionary res = AudioFxBorder!.Resources;
            foreach (string key in GeekDspAccentResourceKeys)
            {
                res.Remove(key);
            }
        }

        /// <summary>
        /// 重建面板内 Slider / ToggleSwitch 的控件模板。
        /// 元素级资源字典只影响「之后新建」的元素 —— 已经渲染好的滑块不会自己重新求值，
        /// 必须手动把模板卸下来再装回去（ThemeColorService.ApplySliderAccent 里的现成手法）。
        /// </summary>
        private void ReloadDspControlTemplates()
        {
            ReloadTemplateOfType<Slider>(AudioFxBorder);
            ReloadTemplateOfType<ToggleSwitch>(AudioFxBorder);
        }

        private static void ReloadTemplateOfType<T>(DependencyObject? node) where T : Control
        {
            if (node == null)
            {
                return;
            }

            if (node is T control)
            {
                try
                {
                    ControlTemplate? template = control.Template;
                    if (template != null)
                    {
                        control.SetValue(Control.TemplateProperty, null);
                        control.Template = template;
                    }
                }
                catch (Exception caught)
                {
                    StartupLog.WriteException("MainWindow.ReloadTemplateOfType", caught);
                }
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                ReloadTemplateOfType<T>(VisualTreeHelper.GetChild(node, i));
            }
        }

        /// <summary>
        /// 左侧导航选中条：它是代码里直接给 Background 赋值的 Border，吃不到 ThemeResource，
        /// 换色 / 切风格时得由这里重新刷成当前强调色。
        /// </summary>
        private void RefreshDspNavGeekAccent()
        {
            Brush accent = DspAccentBrush();
            foreach (DspNavEntry entry in _dspNavEntries)
            {
                if (entry.Bar != null)
                {
                    entry.Bar.Background = accent;
                }
            }

            // 状态圆点也走强调色链（内部会在极客下换一套更暗的熄灭色）
            UpdateDspNavIndicators();
        }

        /// <summary>往目标色按比例混色（t=0 原色，t=1 目标色）。对齐 ThemeColorService 的 MixWithWhite / MixWithBlack。</summary>
        private static Color MixToward(Color c, Color target, double t)
            => Color.FromArgb(
                255,
                (byte)(c.R + (target.R - c.R) * t),
                (byte)(c.G + (target.G - c.G) * t),
                (byte)(c.B + (target.B - c.B) * t));

        /// <summary>
        /// 模块页徽章的配色（state: "on" = 生效中，其余 = 未启用）。
        /// 「已旁路」的琥珀警示色不在本函数里 —— 语义色不参与主题化，否则就失去警示作用了。
        /// 极客下「生效中」改走磷光，等价于其它皮肤里这块由主题色负责强调的效果；
        /// 字形也要跟着改：极客是暗界面、默认前景是白的，浅底上会糊成一团，所以显式钉一层深色。
        /// </summary>
        private void SetDspBadgeState(Border badge, TextBlock text, string state, string caption)
        {
            text.Text = caption;

            Color on = Color.FromArgb(255, 0x3B, 0x6D, 0x11);
            Color off = Color.FromArgb(255, 0xB4, 0xB2, 0xA9);

            // 「已旁路」是语义警示色，不参与主题化（否则失去警示作用）
            if (state == "bypassed")
            {
                badge.Background = new SolidColorBrush(Color.FromArgb(255, 0xC0, 0x7A, 0x1A));
                text.Foreground = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
                return;
            }

            if (GeekDspAccentColor() is Color phosphor)
            {
                if (state == "on")
                {
                    badge.Background = new SolidColorBrush(MixToward(phosphor, Colors.White, 0.72));
                    text.Foreground = new SolidColorBrush(MixToward(phosphor, Colors.Black, 0.80));
                    return;
                }

                off = Color.FromArgb(255, 0x4A, 0x4A, 0x42);
            }

            badge.Background = new SolidColorBrush(state == "on" ? on : off);
            text.Foreground = null; // null = 回落主题默认前景
        }
    }
}
