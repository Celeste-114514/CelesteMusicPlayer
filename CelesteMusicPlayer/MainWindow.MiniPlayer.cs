using System;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 迷你播放器的主窗口侧入口（2026-10-19 重写版）。
    ///
    /// 分工（单向数据流，见 <see cref="MiniPlayerBridge"/>）：
    ///   · 本文件只管「开窗 / 关窗 / 挂桥 / 按 tick 推状态」四件事；
    ///   · 窗口里没有任何播放逻辑，命令全部经 Bridge 转回主窗口执行；
    ///   · 主窗口内部实现随便改，只要 Bridge 引用的那几个 public 方法还在，迷你就不受影响。
    ///
    /// 入口：标题栏右侧按钮列的迷你图标、托盘「迷你播放器」、设置「启动时打开」。
    /// </summary>
    public sealed partial class MainWindow
    {
        private MiniPlayerWindow? _miniPlayerWindow;

        /// <summary>打开（或激活）迷你播放器。单实例：开着就置顶，关了就重建。</summary>
        internal void ShowMiniPlayerWindow()
        {
            try
            {
                if (_miniPlayerWindow != null)
                {
                    _miniPlayerWindow.Activate();
                    return;
                }

                _miniPlayerWindow = new MiniPlayerWindow();
                _miniPlayerWindow.Closed += (_, _) =>
                {
                    _miniPlayerWindow = null;
                };
                _miniPlayerWindow.Activate();
                StartupLog.Write("迷你播放器已打开");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ShowMiniPlayerWindow", caught);
            }
        }

        /// <summary>标题栏按钮列入口（MainWindow.xaml TitleBarFloatingButtons）。</summary>
        private void MiniPlayerButton_Click(object sender, RoutedEventArgs e) => ShowMiniPlayerWindow();

        /// <summary>设置里开了「启动时打开迷你播放器」时，首次激活后调一次。</summary>
        internal void OpenMiniPlayerIfStartupEnabled()
        {
            try
            {
                if (AppSettingsStore.Load().OpenMiniPlayerOnStartup)
                {
                    ShowMiniPlayerWindow();
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.OpenMiniPlayerIfStartupEnabled", caught);
            }
        }
    }
}
