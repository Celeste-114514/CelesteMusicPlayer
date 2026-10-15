using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 音频链路抽屉面板的宿主逻辑（B 组 B3，2026-10-16）。
    ///
    /// 分工：本文件只管"抽屉"（显隐 / 滑入滑出 / 刷新节拍 / 关闭），
    /// 面板内部的渲染与"拿不到就不显示"由 <see cref="AudioChainPanelControl"/> 负责，
    /// 数据口径由 <see cref="AudioSignalPath"/> 负责。三层不互相越界。
    ///
    /// 与「音频设置右侧滑出面板」同一套机制（Visibility + IsHitTestVisible + TranslateX 动画），
    /// 不新造方案：用户要求"从右侧弹出来一个面板"，这就是本程序里已有的那种弹法。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>抽屉宽度（px）。与 XAML 里 AudioChainPanelHost.Width、两个 Storyboard 的 From/To 必须一致。</summary>
        private const double AudioChainPanelWidth = 420;

        private bool _audioChainCloseHooked;

        private void NowPlayingChainButton_Click(object sender, RoutedEventArgs e)
        {
            if (AudioChainOverlayHost == null)
            {
                return;
            }

            // 再点一次 = 收起（按钮当成开关，避免用户找不到关闭）
            if (AudioChainOverlayHost.Visibility == Visibility.Visible)
            {
                HideAudioChainPanel();
                return;
            }

            OpenAudioChainPanel();
        }

        private void AudioChainScrim_Tapped(object sender, TappedRoutedEventArgs e) => HideAudioChainPanel();

        private void OpenAudioChainPanel()
        {
            try
            {
                if (AudioChainPanel == null || AudioChainOverlayHost == null)
                {
                    return;
                }

                // 关面板的回调只接一次（面板是常驻实例，重复接会重复触发）
                if (!_audioChainCloseHooked)
                {
                    AudioChainPanel.CloseRequested += (_, _) => HideAudioChainPanel();
                    _audioChainCloseHooked = true;
                }

                // 每次打开都回到默认折叠态（折叠状态不持久化，免得用户忘了收起觉得乱）
                AudioChainPanel.ResetGroupStates();

                // 打开本身也是一条链路事件：时间线先有内容，也便于确认面板真的在刷新
                AudioSignalPath.RecordEvent("system", "打开音频链路面板");

                AudioChainOverlayHost.Visibility = Visibility.Visible;
                AudioChainOverlayHost.IsHitTestVisible = true;
                AudioChainPanelTransform.TranslateX = AudioChainPanelWidth;
                AudioChainOpenStoryboard?.Begin();

                // 面板可见期间才跑节拍刷新；收起时立即停（"不可见即停"，绝不空转）
                AudioChainPanel.StartLiveRefresh(() => AudioSignalPath.Capture(_audioEngine, DspRackGlobalBypassed));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.OpenAudioChainPanel", caught);
            }
        }

        private void HideAudioChainPanel()
        {
            try
            {
                if (AudioChainOverlayHost == null)
                {
                    return;
                }

                // 先停刷新再收动画：收起期间不要再抓快照
                AudioChainPanel?.StopLiveRefresh();

                if (AudioChainCloseStoryboard != null && AudioChainOverlayHost.Visibility == Visibility.Visible)
                {
                    AudioChainCloseStoryboard.Completed += (_, _) =>
                    {
                        AudioChainOverlayHost.Visibility = Visibility.Collapsed;
                        AudioChainOverlayHost.IsHitTestVisible = false;
                    };
                    AudioChainCloseStoryboard.Begin();
                }
                else
                {
                    AudioChainOverlayHost.Visibility = Visibility.Collapsed;
                    AudioChainOverlayHost.IsHitTestVisible = false;
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.HideAudioChainPanel", caught);
            }
        }

        /// <summary>
        /// 外部（切歌 / 切设备 / 切输出模式 / 变速 / DSP 开关）可以主动喊一声刷新。
        /// 面板关着就什么都不做；开着也不必重抓——节拍定时器马上就会刷。
        /// </summary>
        internal void RefreshAudioChainPanelIfOpen()
        {
            if (AudioChainOverlayHost == null || AudioChainOverlayHost.Visibility != Visibility.Visible)
            {
                return;
            }

            try
            {
                AudioChainPanel?.Refresh(AudioSignalPath.Capture(_audioEngine, DspRackGlobalBypassed));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.RefreshAudioChainPanelIfOpen", caught);
            }
        }
    }
}
