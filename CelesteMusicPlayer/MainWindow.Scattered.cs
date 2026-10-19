using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 零散播放窗口的主窗口侧入口（2026-10-18 用户定案，10-18 深夜重构为自持引擎版）。
    ///
    /// 分工：<see cref="ScatteredPlayWindow"/> 现在是一个**完全自持的独立播放器**——
    /// 自己的音频引擎实例（FFmpeg 解码 + 与主程序同一套 DSP 底层）、自己的纯内存队列、
    /// 自己的歌词/可视化/EQ。主窗口这边只保留「开窗」这一个动作：
    /// 托盘菜单「零散播放」、以及主程序在跑时双击/转交进来的散装文件。
    ///
    /// 不再做（旧版做过、全部移除）：
    /// · 不再把文件塞进 <see cref="_userPlaylist"/>（那就是「改变主程序播放列表」的根因）；
    /// · 不再经手起播/队列/封面/技术信息（窗口自己全管）；
    /// · 冷启动（双击文件、无主程序在跑）时根本不会走到这里 —— App 直接开零散窗口，
    ///   主窗口都不建，曲库更不会读（见 App.TryLaunchScatteredOnly）。
    /// </summary>
    public sealed partial class MainWindow
    {
        private ScatteredPlayWindow? _scatteredWindow;

        /// <summary>打开（或激活）零散播放窗口。单实例：开着就置顶，关了就重建。</summary>
        internal void ShowScatteredWindow()
        {
            try
            {
                if (_scatteredWindow != null)
                {
                    _scatteredWindow.Activate();
                    return;
                }

                _scatteredWindow = new ScatteredPlayWindow();
                _scatteredWindow.Closed += (_, _) =>
                {
                    _scatteredWindow = null;
                };
                _scatteredWindow.Activate();
                StartupLog.Write("零散播放窗口已打开");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ShowScatteredWindow", caught);
            }
        }
    }
}
