using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 统一毛玻璃：窗口 Desktop Acrylic；面板用更深、更糊的 Acrylic + 高光描边。
    /// </summary>
    internal static class FrostedGlass
    {
        public static Color DefaultTint { get; } = Color.FromArgb(255, 36, 30, 42);

        /// <summary>
        /// 经典界面模式（null = 现有背景图式 UI；"Light" / "Dark" / "System" = 经典不透明界面）。
        /// 由 MainWindow.ApplyUiStyleMode 设置。置为非 null 后，本类产出的所有面板画刷
        /// 自动变成与明暗主题匹配的不透明纯色——面板代码一行都不用改。
        /// </summary>
        public static string? ClassicMode { get; set; }

        /// <summary>
        /// 经典界面是否浅色。
        /// 注意：MainWindow.ApplyUiStyleMode 存进来的是归一化后的 "Light" / "Dark"
        ///（不是 "ClassicLight"），这里把两种写法都认，避免模式串对不上导致浅色模式被当成深色。
        /// </summary>
        private static bool ClassicIsLight
            => ClassicMode is "Light" or "ClassicLight"
               || (ClassicMode is "System" or "ClassicSystem"
                   && Application.Current.RequestedTheme == ApplicationTheme.Light);

        public static void ApplyWindowBackdrop(Window window)
        {
            // 先按主程序的界面风格把明暗主题下发到这个窗口（设置页/均衡器/标签编辑等弹窗都走这里）
            ApplyWindowTheme(window);

            // 经典界面统一不透明，弹窗也不再用亚克力
            if (ClassicMode != null)
            {
                try
                {
                    window.SystemBackdrop = null;
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FrostedGlass.cs", caught); }

                return;
            }

            try
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
                return;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FrostedGlass.cs", caught); }

            try
            {
                window.SystemBackdrop = new MicaBackdrop();
            }
            catch
            {
                window.SystemBackdrop = null;
            }
        }

        /// <summary>
        /// 把主程序的界面风格（经典浅色/深色/跟随系统）应用到任意窗口：
        /// 让弹出的设置页、各种编辑窗口跟着一起分深浅色；背景图式风格下保持跟随系统。
        /// </summary>
        public static void ApplyWindowTheme(Window window)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                if (window.Content is FrameworkElement root)
                {
                    root.RequestedTheme = ClassicMode switch
                    {
                        "Light" or "ClassicLight" => ElementTheme.Light,
                        "Dark" or "ClassicDark" => ElementTheme.Dark,
                        // 极客界面：恒为深色（近黑终端风）
                        "Geek" => ElementTheme.Dark,
                        _ => ElementTheme.Default,
                    };
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FrostedGlass.cs", caught); }

            ApplyWindowTitleBarTheme(window);
            ApplyWindowRootBackground(window);

            // 各窗口构造顺序不一致：设置页、取色器是"先 ApplyBackdrop 再写死标题栏配色"，
            // 后写的会把主题色盖掉。这里挂一次激活回调，在窗口真正显示前再刷一遍，
            // 保证经典模式下标题栏一定是当前主题的配色（背景图式下本方法什么都不做）。
            try
            {
                if (!ThemedWindows.TryGetValue(window, out _))
                {
                    ThemedWindows.Add(window, window);
                    window.Activated += (sender, _) =>
                    {
                        if (sender is Window activated)
                        {
                            ApplyWindowTitleBarTheme(activated);
                        }
                    };
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FrostedGlass.cs", caught); }
        }

        /// <summary>已挂过激活回调的窗口，避免重复挂（弱引用表，窗口关了自动释放）。</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> ThemedWindows = new();

        /// <summary>被本类刷过底色的窗口根面板，切回背景图式时用来还原成"透明 + 亚克力"。</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Panel, object> PaintedRoots = new();

        /// <summary>
        /// 窗口根面板底色。
        /// 各弹窗的根 Grid 都是透明、靠窗口亚克力透出来的；经典界面把亚克力关掉了，
        /// 不给不透明底就会露出系统默认底色（可能正好是另一个主题的颜色）。这里显式补上，
        /// 切回背景图式时再撤掉，保证两套界面互不残留。
        /// </summary>
        private static void ApplyWindowRootBackground(Window window)
        {
            if (window.Content is not Panel panel)
            {
                return;
            }

            try
            {
                if (ClassicMode != null)
                {
                    panel.Background = new SolidColorBrush(ClassicMode == "Geek"
                        ? Color.FromArgb(255, 11, 15, 11)          // 极客：与主窗口 RootShell 同一款近黑
                        : ClassicIsLight
                            ? Color.FromArgb(255, 243, 243, 243)
                            : Color.FromArgb(255, 32, 32, 32));
                    PaintedRoots.AddOrUpdate(panel, panel);
                }
                else if (PaintedRoots.TryGetValue(panel, out _))
                {
                    panel.ClearValue(Panel.BackgroundProperty);
                    PaintedRoots.Remove(panel);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FrostedGlass.cs", caught); }
        }

        /// <summary>
        /// 系统标题栏（最小化 / 最大化 / 关闭那三个按钮）的配色。
        /// 只在经典界面下介入 —— 背景图式界面保持各窗口原有配色，一个字都不动。
        /// </summary>
        private static void ApplyWindowTitleBarTheme(Window window)
        {
            if (ClassicMode == null)
            {
                return;
            }

            try
            {
                if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
                {
                    return;
                }

                Windows.UI.Color transparent = Color.FromArgb(0, 0, 0, 0);
                Microsoft.UI.Windowing.AppWindowTitleBar bar = window.AppWindow.TitleBar;
                bar.ButtonBackgroundColor = transparent;
                bar.ButtonInactiveBackgroundColor = transparent;

                if (ClassicIsLight)
                {
                    bar.ButtonHoverBackgroundColor = Color.FromArgb(24, 0, 0, 0);
                    bar.ButtonPressedBackgroundColor = Color.FromArgb(40, 0, 0, 0);
                    bar.ButtonForegroundColor = Color.FromArgb(255, 28, 28, 28);
                    bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 132, 132, 132);
                    bar.ButtonHoverForegroundColor = Color.FromArgb(255, 0, 0, 0);
                    bar.ButtonPressedForegroundColor = Color.FromArgb(255, 0, 0, 0);
                }
                else
                {
                    bar.ButtonHoverBackgroundColor = Color.FromArgb(36, 255, 255, 255);
                    bar.ButtonPressedBackgroundColor = Color.FromArgb(60, 255, 255, 255);
                    bar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
                    bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
                    bar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
                    bar.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 255, 255);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("FrostedGlass.cs", caught); }
        }

        /// <summary>
        /// TintOpacity 越低，背后内容的模糊感越明显。
        /// </summary>
        public static Brush CreateBrush(
            double tintOpacity = 0.38,
            double luminosityOpacity = 0.62,
            Color? tint = null)
        {
            // 经典界面：面板一律不透明纯色（浅色=白卡，深色=炭灰卡），不再透出背后内容
            if (ClassicMode != null)
            {
                if (ClassicMode == "Geek")
                {
                    // 极客界面：近黑面板，比主底(#0B0F0B)亮一档带层次，微绿调呼应终端
                    return new SolidColorBrush(Color.FromArgb(255, 22, 27, 22));
                }

                return new SolidColorBrush(
                    ClassicIsLight
                        ? Color.FromArgb(255, 255, 255, 255)
                        : Color.FromArgb(255, 45, 45, 45));
            }

            Color t = tint ?? DefaultTint;
            try
            {
                return new AcrylicBrush
                {
                    TintColor = t,
                    TintOpacity = tintOpacity,
                    TintLuminosityOpacity = luminosityOpacity,
                    FallbackColor = Color.FromArgb(
                        (byte)Math.Clamp((int)(tintOpacity * 230 + 40), 60, 210),
                        t.R,
                        t.G,
                        t.B)
                };
            }
            catch
            {
                return new SolidColorBrush(
                    Color.FromArgb(
                        (byte)Math.Clamp((int)(tintOpacity * 230 + 40), 60, 210),
                        t.R,
                        t.G,
                        t.B));
            }
        }

        /// <summary>主界面信息卡 / 歌词区等：更糊、更突出。</summary>
        public static Brush CreatePanelBrush(Color? tint = null)
            => CreateBrush(0.28, 0.72, tint ?? DefaultTint);

        /// <summary>迷你播放器：低 Tint，透出壁纸 Desktop Acrylic。</summary>
        public static Brush CreateMiniPlayerBrush()
            => CreateBrush(0.16, 0.88, Color.FromArgb(255, 32, 26, 40));

        /// <summary>仅作轻微压暗，不挡系统毛玻璃。</summary>
        public static Brush CreateMiniPlayerDimOverlay()
            => new SolidColorBrush(Color.FromArgb(72, 18, 14, 24));

        public static void StyleElevatedPanel(Border panel, CornerRadius? radius = null)
        {
            if (panel == null)
            {
                return;
            }

            panel.Background = CreatePanelBrush();
            // 不用高光描边：与 ThemeShadow 叠在一起容易出现细白线
            panel.BorderBrush = null;
            panel.BorderThickness = new Thickness(0);
            panel.CornerRadius = radius ?? new CornerRadius(10);

            try
            {
                if (panel.Shadow is not ThemeShadow)
                {
                    panel.Shadow = new ThemeShadow();
                }

                panel.Translation = new System.Numerics.Vector3(0, 4, 48);
            }
            catch
            {
                panel.Translation = new System.Numerics.Vector3(0, 0, 0);
            }
        }
    }
}
