using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 主窗口全屏功能：标题栏右上角「全屏」按钮 + Esc 退出。
    /// 全屏逻辑本身在 FullScreenController（自包含，可整体复制到别的 WinUI 3 项目）。
    /// 这里只负责：接线按钮、按状态换图标和提示。
    /// </summary>
    public sealed partial class MainWindow
    {
        private FullScreenController? _fullScreen;

        /// <summary>
        /// 在 MainWindow 构造函数 InitializeComponent() 之后调用。
        /// 必须早于用户点按钮；控制器内部已把 UI 线程 DispatcherQueue 缓存下来。
        /// </summary>
        private void InitializeFullScreen()
        {
            try
            {
                _fullScreen = new FullScreenController(this);
                _fullScreen.StateChanged += OnFullScreenStateChanged;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.InitializeFullScreen", caught);
            }
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            _fullScreen?.Toggle();
        }

        /// <summary>
        /// 全屏状态变化：切换按钮图标与提示。
        /// 进入全屏 → 显示「退出全屏」图标；退出 → 显示「全屏」图标。
        /// </summary>
        private void OnFullScreenStateChanged(bool isFullScreen)
        {
            if (FullScreenIcon != null)
            {
                // E740 = 进入全屏，E73F = 退出全屏（还原窗口）
                FullScreenIcon.Glyph = isFullScreen ? "\uE73F" : "\uE740";
            }

            if (FullScreenButton != null)
            {
                ToolTipService.SetToolTip(FullScreenButton, isFullScreen ? "退出全屏" : "全屏");
            }
        }
    }
}
