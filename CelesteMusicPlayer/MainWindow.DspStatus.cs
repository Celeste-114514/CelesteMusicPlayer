using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DSP 板块顶部状态条（<see cref="DspDeviceNameText"/> / <see cref="DspOutputModeText"/> /
    /// <see cref="DspSessionFormatText"/>）：打开面板就能看到"现在正从哪台设备、以哪种模式出声"。
    ///
    /// 数据口径（都与既有面板一致，不另起炉灶）：
    /// · 设备   = 设置里的 OutputDeviceId 反查 WASAPI 设备列表，空 = 系统默认；ASIO 模式存的就是驱动名；
    /// · 模式   = 设置 OutputMode（共享 / WASAPI 独占·内核 / ASIO），内核名复用 EngineDisplayName；
    /// · 会话   = 引擎 ActualOutputFormat（设备端协商结果），未播放时显"未在播放"；
    /// · 状态点 = DSD（ChainFormat.IsDsdPath）/ 变速（TempoScale≠1）/ 直通或 DSP 生效
    ///   （DspBypassToggle 旁路或 IsDspActiveForBadge 判定，和 bit-perfect 徽章同口径）。
    ///
    /// 刷新入口：打开面板（LoadAudioFxUiFromStore）、起播切歌（UpdateSignalChainDisplay）、
    /// 改旁路/DSP 开关（UpdateDspBitPerfectUi）。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>刷新 DSP 板块顶部「设备 / 输出模式 / 会话格式」状态条。</summary>
        internal void UpdateDspDeviceStatus()
        {
            if (DspDeviceNameText == null || DspOutputModeText == null || DspSessionFormatText == null)
            {
                return;
            }

            try
            {
                AppSettingsState s = AppSettingsStore.Load();
                DspDeviceNameText.Text = "正在使用：" + ResolveCurrentOutputDeviceLabel(s);

                string mode = s.OutputMode ?? "Shared";
                string modeText;
                if (string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase))
                {
                    modeText = "输出模式：ASIO 直出";
                }
                else if (string.Equals(mode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase))
                {
                    modeText = "输出模式：WASAPI 独占 · " + EngineDisplayName(s.ExclusiveEngine ?? "self");
                }
                else
                {
                    modeText = "输出模式：共享（WASAPI，经系统混音）";
                }

                DspOutputModeText.Text = modeText;

                // 会话格式：引擎没起（未播放 / 刚开面板）时不给猜测值，明说"未在播放"
                var chain = _audioEngine?.ChainFormat;
                bool hasSession = chain is { HasSession: true };
                string fmt = hasSession ? (_audioEngine?.ActualOutputFormat ?? string.Empty) : string.Empty;
                string head = !string.IsNullOrWhiteSpace(fmt)
                    ? "会话 " + fmt
                    : (hasSession ? "会话（格式探测中）" : "未在播放");

                var chips = new List<string>(3);
                if (chain is { IsDsdPath: true })
                {
                    chips.Add("DSD");
                }

                double scale = TempoScale();
                if (Math.Abs(scale - 1.0) > 0.001)
                {
                    chips.Add("变速 " + scale.ToString("0.##") + "x");
                }

                bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;
                chips.Add(bypass || !IsDspActiveForBadge() ? "直通" : "DSP 生效");

                DspSessionFormatText.Text = head + " · " + string.Join(" · ", chips);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.DspStatus.cs", caught);
                if (DspSessionFormatText != null)
                {
                    DspSessionFormatText.Text = "状态：读取失败";
                }
            }
        }

        /// <summary>
        /// 当前输出设备显示名。WASAPI：按设置的设备 ID 反查枚举列表，空 = 系统默认设备；
        /// ASIO：设置里存的 OutputDeviceId 就是驱动名。设备被拔掉时如实说，不显示一串 ID。
        /// </summary>
        private static string ResolveCurrentOutputDeviceLabel(AppSettingsState s)
        {
            try
            {
                string mode = s.OutputMode ?? "Shared";
                string id = s.OutputDeviceId ?? string.Empty;

                if (string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(id) ? "ASIO 默认驱动" : id;
                }

                var devices = HiFiOutputBackend.EnumerateWasapiDevices();
                if (string.IsNullOrWhiteSpace(id))
                {
                    string defId = HiFiOutputBackend.GetDefaultWasapiDeviceId();
                    foreach ((string did, string name) in devices)
                    {
                        if (string.Equals(did, defId, StringComparison.OrdinalIgnoreCase))
                        {
                            return name + "（系统默认）";
                        }
                    }

                    return "系统默认输出";
                }

                foreach ((string did, string name) in devices)
                {
                    if (string.Equals(did, id, StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }

                return "设备不在当前列表（可能已拔出）";
            }
            catch
            {
                return "（设备读取失败）";
            }
        }
    }
}
