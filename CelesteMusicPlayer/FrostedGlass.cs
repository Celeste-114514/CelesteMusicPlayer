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

        public static void ApplyWindowBackdrop(Window window)
        {
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
        /// TintOpacity 越低，背后内容的模糊感越明显。
        /// </summary>
        public static Brush CreateBrush(
            double tintOpacity = 0.38,
            double luminosityOpacity = 0.62,
            Color? tint = null)
        {
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
