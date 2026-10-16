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

        /// <summary>
        /// 收起动画 Completed 的订阅去重：早期版本每次收起都 += 一个新 handler，
        /// 多点几次就会叠加（同一收起动作被重复处理）。先退订再订，只留一个。
        /// </summary>
        private EventHandler<object>? _audioChainCloseCompleted;

        /// <summary>
        /// 音频链路面板入口（2026-10-17 起：标题栏右上角按钮；播放页那个同名按钮已隐藏）。
        /// 按钮当开关用：再点一次收起。
        /// </summary>
        private void AudioChainButton_Click(object sender, RoutedEventArgs e)
        {
            StartupLog.Write($"[链路] 按钮点击 当前可见={(AudioChainOverlayHost?.Visibility.ToString() ?? "null")}");
            if (AudioChainOverlayHost == null)
            {
                return;
            }

            // 已经开着就什么都不做（2026-10-17 用户实测：再点一次面板消失，被当成"点不出来"）。
            // 关闭走三处：面板右上角 X、点面板外的遮罩、再点本按钮之外的任何关闭动作。
            if (AudioChainOverlayHost.Visibility == Visibility.Visible)
            {
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
                    // 底部「打开音频设置」：标题栏那个按钮改成链路入口之后，设置面板从这里进
                    AudioChainPanel.OpenAudioSettingsRequested += (_, _) =>
                    {
                        HideAudioChainPanel();
                        OpenAudioSettingsPanel();
                    };
                    _audioChainCloseHooked = true;
                }

                // 每次打开都回到默认折叠态（折叠状态不持久化，免得用户忘了收起觉得乱）
                AudioChainPanel.ResetGroupStates();

                // 打开本身也是一条链路事件：时间线先有内容，也便于确认面板真的在刷新
                AudioSignalPath.RecordEvent("system", "打开音频链路面板");

                AudioChainOverlayHost.Visibility = Visibility.Visible;
                AudioChainOverlayHost.IsHitTestVisible = true;
                AudioChainPanelTransform.TranslateX = AudioChainPanelWidth;

                // ⚠ 同族教训（10-16 三次）：XAML 故事板的 TargetName 在本仓有解析失败案，
                // Begin 会抛 COMException，动画不跑 → 面板停在 420px 偏移处、完全在窗口外看不见。
                // 开合两条路径都先用代码把动画目标锁死；万一还是抛，直接把 TranslateX 落到 0 兜底。
                RetargetStoryboard(AudioChainOpenStoryboard, "chainOpen");
                try
                {
                    AudioChainOpenStoryboard?.Begin();
                }
                catch (Exception sbEx)
                {
                    StartupLog.Write("[链路] 打开动画 Begin 失败，改用直接落位: " + sbEx.Message);
                    AudioChainPanelTransform.TranslateX = 0;
                }

                StartupLog.Write($"[链路] 打开完成 可见={AudioChainOverlayHost.Visibility} TranslateX={AudioChainPanelTransform.TranslateX}");

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
                    // 订阅去重：每次收起都 += 会叠加 handler
                    if (_audioChainCloseCompleted != null)
                    {
                        AudioChainCloseStoryboard.Completed -= _audioChainCloseCompleted;
                    }
                    _audioChainCloseCompleted = (_, _) =>
                    {
                        AudioChainOverlayHost.Visibility = Visibility.Collapsed;
                        AudioChainOverlayHost.IsHitTestVisible = false;
                    };
                    AudioChainCloseStoryboard.Completed += _audioChainCloseCompleted;

                    RetargetStoryboard(AudioChainCloseStoryboard, "chainClose");
                    try
                    {
                        AudioChainCloseStoryboard.Begin();
                    }
                    catch (Exception sbEx)
                    {
                        StartupLog.Write("[链路] 收起动画 Begin 失败，直接折叠: " + sbEx.Message);
                        AudioChainOverlayHost.Visibility = Visibility.Collapsed;
                        AudioChainOverlayHost.IsHitTestVisible = false;
                    }
                }
                else
                {
                    AudioChainOverlayHost.Visibility = Visibility.Collapsed;
                    AudioChainOverlayHost.IsHitTestVisible = false;
                }
                StartupLog.Write($"[链路] 收起完成 可见={AudioChainOverlayHost.Visibility}");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.HideAudioChainPanel", caught);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // B4：真实事件接线（2026-10-17）
        //
        // 面板的「输出记录」之前只有「打开面板」一条，因为没人往事件环里写东西。
        // 这里给各类真实事件一个统一的记账口：记一条 + 面板开着就刷新一次。
        // ⚠ 全部只在 UI 线程调用（调用点都是 XAML 事件处理器）；
        //   引擎侧（后台线程）直接用 AudioSignalPath.RecordEvent，那个带 lock。
        // ─────────────────────────────────────────────────────────────

        private string? _chainLastDevice;
        private bool _chainDeviceSeen;
        private string? _chainLastMode;
        private bool _chainModeSeen;

        /// <summary>记一条链路事件；面板开着就顺手刷一次（关着就只记账，不打扰）。</summary>
        internal void NoteChain(string kind, string text)
        {
            try
            {
                AudioSignalPath.RecordEvent(kind, text);
                RefreshAudioChainPanelIfOpen();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.NoteChain", caught);
            }
        }

        /// <summary>切歌（首次播放与无缝自动下一首都汇到 RecordPlaybackStatsOnStart）。</summary>
        internal void NoteChainTrack(string? title, string? artist)
        {
            string label = string.IsNullOrWhiteSpace(title) ? "未知曲目" : title!;
            if (!string.IsNullOrWhiteSpace(artist))
            {
                label = artist + " - " + label;
            }

            NoteChain("track", "开始播放：" + label);
        }

        /// <summary>
        /// 输出设备切换。启动恢复也会走 ApplyOutputDeviceAsync，
        /// 所以第一次只记住当前值、不记事件（那不是"切换"）。
        /// </summary>
        internal void NoteChainDevice(string? deviceId)
        {
            string key = string.IsNullOrWhiteSpace(deviceId) ? "系统默认" : deviceId!;
            bool changed = _chainDeviceSeen && !string.Equals(_chainLastDevice, key, StringComparison.Ordinal);
            _chainLastDevice = key;
            _chainDeviceSeen = true;

            if (!changed)
            {
                return;
            }

            string shown = key.Length > 24 ? key.Substring(0, 24) + "…" : key;
            NoteChain("device", "输出设备切换 → " + shown);
        }

        /// <summary>输出模式切换（共享 / 独占 / ASIO）。同设备切换一样，首次只记值。</summary>
        internal void NoteChainMode(string? settingsMode)
        {
            string key = AudioSignalPath.DescribeOutputMode(settingsMode);
            bool changed = _chainModeSeen && !string.Equals(_chainLastMode, key, StringComparison.Ordinal);
            _chainLastMode = key;
            _chainModeSeen = true;

            if (!changed)
            {
                return;
            }

            NoteChain("mode", "输出模式切换 → " + key);
        }

        /// <summary>变速（倍率变化，文案与面板口径一致）。</summary>
        internal void NoteChainTempo(double rate)
        {
            NoteChain("tempo", $"播放速度 {rate:0.##}×");
        }

        /// <summary>
        /// DSP 变化。滑块拖动会连续回调，用内容去重（同值不重复记），
        /// 否则输出记录会被刷成一整屏一样的行。
        /// </summary>
        internal void NoteChainDsp(string key, string text)
        {
            try
            {
                AudioSignalPath.RecordEventDedup(key, "dsp", text);
                RefreshAudioChainPanelIfOpen();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.NoteChainDsp", caught);
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
