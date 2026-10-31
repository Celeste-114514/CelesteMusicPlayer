using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十六刀（2026-10-31）：正在播放进度条的拖拽/跳转手势。
/// 从壳 <c>MainWindow.Features.cs</c> 搬出：按下暂停刷新、松开/丢捕获时提交 seek。
/// （注：不能放进 MainWindow.NowPlayingLayout.cs —— 那个文件在 csproj 的
/// <c>_w13_npcluster</c> 排除块里，不参与编译。）
/// </summary>
public sealed partial class MainWindow
{

        // 悬浮进度条交互：复用真实进度条的拖拽 / 跳转逻辑
        private void NowPlayingProgressSlider_PointerPressed(dynamic sender, dynamic e)
        {
            _isUserSeeking = true;
            _seekGestureConsumed = false;
        }

        private void NowPlayingProgressSlider_PointerReleased(dynamic sender, dynamic e)
            => EndNowPlayingSeekGesture();

        private void NowPlayingProgressSlider_PointerCaptureLost(dynamic sender, dynamic e)
            => EndNowPlayingSeekGesture();

        /// <summary>
        /// 页内进度条：一次拖动只跳转一次（PointerReleased + PointerCaptureLost 都会触发，
        /// 重复 seek 会卡顿并重复触发下一首无缝预加载 —— 就是"顿一下响两声"的来源）。
        /// </summary>
        private void EndNowPlayingSeekGesture()
        {
            if (!_isUserSeeking || _seekGestureConsumed)
            {
                _isUserSeeking = false;
                return;
            }

            _seekGestureConsumed = true;
            try
            {
                if (NowPlayingProgressSlider != null && ProgressSlider != null)
                {
                    double target = NowPlayingProgressSlider.Value;
                    if (Math.Abs(ProgressSlider.Value - target) >= 0.01)
                    {
                        ProgressSlider.Value = target;
                    }
                }

                SeekToSliderValue();
            }
            finally
            {
                _isUserSeeking = false;
            }
        }
    }
