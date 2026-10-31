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

        /// <summary>
        /// 收起动画 Completed 的订阅去重：早期版本每次收起都 += 一个新 handler，
        /// 多点几次就会叠加（同一收起动作被重复处理）。先退订再订，只留一个。
        /// </summary>
        private EventHandler<object>? _audioChainCloseCompleted;

        /// <summary>打开动画的兜底复查定时器（只跑一次，用完即弃）。</summary>
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _chainOpenFallbackTimer;

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
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.NoteChainDsp", caught);
            }
        }

    }
}
