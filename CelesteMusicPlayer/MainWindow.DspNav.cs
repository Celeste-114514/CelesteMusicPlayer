using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 音效处理页面：左侧「处理链路」模块导航 + 右侧模块内容分区。
    /// 顺序按信号链：输入余量 → 采样率 SRC → 参数 EQ → 耳机校正 → FIR → 声道 → ReplayGain → 输出安全监控。
    /// 本文件只做页面切换与状态指示，不改动任何 DSP 处理逻辑。
    /// </summary>
    public sealed partial class MainWindow
    {
        private const int DspPageCount = 8;
        private const int DspPageSafetyIndex = 7;
        private const int DspPageFirIndex = 4;
        private const int DspPageChannelIndex = 5;

        private int _dspPageIndex = -1;
        private DispatcherQueueTimer? _dspMonitorTimer;

        /// <summary>耳机校正（OPRA）曲线是否已应用到当前 EQ。</summary>
        private bool _opraApplied;

        // ---------- 页面切换 ----------

        private void DspNavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && int.TryParse(tag, out int idx))
            {
                SelectDspPage(idx);
            }
        }

        /// <summary>切换到指定模块页面（0..7）。</summary>
        private void SelectDspPage(int idx)
        {
            if (idx < 0 || idx >= DspPageCount)
            {
                return;
            }

            _dspPageIndex = idx;

            StackPanel[] pages =
            {
                DspPageHeadroom, DspPageSrc, DspPageEq, DspPageOpra,
                DspPageFir, DspPageChannel, DspPageRg, DspPageSafety
            };
            Border[] bars =
            {
                DspNavBar0, DspNavBar1, DspNavBar2, DspNavBar3,
                DspNavBar4, DspNavBar5, DspNavBar6, DspNavBar7
            };
            TextBlock[] labels =
            {
                DspNavLabel0, DspNavLabel1, DspNavLabel2, DspNavLabel3,
                DspNavLabel4, DspNavLabel5, DspNavLabel6, DspNavLabel7
            };

            for (int i = 0; i < DspPageCount; i++)
            {
                bool on = i == idx;
                pages[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                bars[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                labels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                labels[i].Opacity = on ? 1.0 : 0.72;
            }

            // 只有在「输出安全监控」页才跑 500ms 刷新定时器，其它页面不占 UI 线程
            EnsureDspMonitorTimer(idx == DspPageSafetyIndex);
            if (idx == DspPageSafetyIndex)
            {
                UpdateDspOutputMonitor();
                UpdateDspSafetyChain();
            }

            if (idx == DspPageFirIndex)
            {
                RefreshRoomCorrectionInfo();
            }

            if (idx == DspPageChannelIndex)
            {
                UpdateDspChannelBars();
            }
        }

        /// <summary>首次进入音效页面时定位到第一个模块，并刷新各模块启用指示。</summary>
        private void InitDspNav()
        {
            InitRoomCorrectionTrimUi();
            UpdateDspNavIndicators();
            if (_dspPageIndex < 0)
            {
                SelectDspPage(0);
            }
        }

        // ---------- 模块启用指示（左侧小圆点）----------

        /// <summary>各模块当前是否参与处理（索引与导航顺序一致；末位为监控页恒亮）。</summary>
        private bool[] DspModuleActive()
        {
            // 圆点语义 = "正在参与处理"。余量(负增益)才是安全模块真正逐样本处理的情形；
            // 限幅开关单独开着只待命（源 PCM 不超 ±1 时无需削波），不能点亮圆点
            // （2026-09-22 用户实测"限幅没开却显示开了"的同类口径问题，已对齐）。
            bool headroom = AudioFxSafetyHeadroomSlider != null && Math.Abs(AudioFxSafetyHeadroomSlider.Value) > 0.01;

            int srcHz = 0;
            if (SrcRateCombo != null && SrcRateCombo.SelectedIndex >= 0 && SrcRateCombo.SelectedIndex < SrcRateOptions.Length)
            {
                srcHz = SrcRateOptions[SrcRateCombo.SelectedIndex].Hz;
            }

            bool eq = _audioFxEq != null && _audioFxEq.Enabled && _audioFxEq.HasEffect();
            bool fir = RoomCorrectionStore.Load().Enabled;
            bool ch = AudioFxChannelToggle != null && AudioFxChannelToggle.IsOn;
            bool rg = ReplayGainStore.Load().Mode != ReplayGainMode.Off;

            return new[] { headroom, srcHz > 0, eq, _opraApplied, fir, ch, rg, true };
        }

        /// <summary>刷新左侧导航圆点：绿 = 该模块正在参与处理，灰 = 未启用。</summary>
        private void UpdateDspNavIndicators()
        {
            Border[] dots =
            {
                DspNavDot0, DspNavDot1, DspNavDot2, DspNavDot3,
                DspNavDot4, DspNavDot5, DspNavDot6, DspNavDot7
            };

            bool[] active = DspModuleActive();
            for (int i = 0; i < dots.Length && i < active.Length; i++)
            {
                dots[i].Background = new SolidColorBrush(active[i]
                    ? Color.FromArgb(255, 0x3B, 0x6D, 0x11)
                    : Color.FromArgb(255, 0xB4, 0xB2, 0xA9));
            }

            if (OutMonitorActiveDspText != null)
            {
                UpdateDspOutputMonitor();
            }

            UpdateDspHeroBadges();
            UpdateDspChannelBars();
        }

        // ---------- 模块页 Hero 状态徽章 ----------

        /// <summary>刷新每个模块页右上角的徽章：绿 = 正在生效，灰 = 未启用，琥珀 = 已旁路。</summary>
        private void UpdateDspHeroBadges()
        {
            Border[] badges =
            {
                DspBadgeHeadroom, DspBadgeSrc, DspBadgeEq, DspBadgeOpra,
                DspBadgeFir, DspBadgeChannel, DspBadgeRg, DspBadgeSafety
            };
            TextBlock[] texts =
            {
                DspBadgeHeadroomText, DspBadgeSrcText, DspBadgeEqText, DspBadgeOpraText,
                DspBadgeFirText, DspBadgeChannelText, DspBadgeRgText, DspBadgeSafetyText
            };

            bool[] active = DspModuleActive();
            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;

            for (int i = 0; i < badges.Length; i++)
            {
                if (badges[i] == null || texts[i] == null)
                {
                    continue;
                }

                // 末位是监控页，恒亮（它显示的是状态而不是处理模块）
                bool on = i == DspPageSafetyIndex || active[i];
                if (bypass && i != DspPageSafetyIndex)
                {
                    badges[i].Background = new SolidColorBrush(Color.FromArgb(255, 0xC0, 0x7A, 0x1A));
                    texts[i].Text = "已旁路";
                }
                else if (on)
                {
                    badges[i].Background = new SolidColorBrush(Color.FromArgb(255, 0x3B, 0x6D, 0x11));
                    texts[i].Text = i == DspPageSafetyIndex ? "监控中" : "生效中";
                }
                else
                {
                    badges[i].Background = new SolidColorBrush(Color.FromArgb(255, 0xB4, 0xB2, 0xA9));
                    texts[i].Text = "未启用";
                }
            }
        }

        // ---------- 余量快捷预设 ----------

        private void DspHeadroomPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag || !double.TryParse(tag, out double db))
            {
                return;
            }

            if (AudioFxSafetyHeadroomSlider == null)
            {
                return;
            }

            AudioFxSafetyHeadroomSlider.Value = Math.Clamp(db, AudioFxSafetyHeadroomSlider.Minimum, AudioFxSafetyHeadroomSlider.Maximum);
            ApplyDspToEngine();
            UpdateDspNavIndicators();
        }

        // ---------- FIR 快捷 trim / 安全启用 ----------

        private void DspFirTrimQuick_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag || !double.TryParse(tag, out double db))
            {
                return;
            }

            if (RoomTrimSlider != null)
            {
                RoomTrimSlider.Value = Math.Clamp(db, RoomTrimSlider.Minimum, RoomTrimSlider.Maximum);
            }
        }

        /// <summary>先把 trim 降到 -6 dB 再启用卷积，避免一开就爆音（对齐 ECHO enableFirSafely）。</summary>
        private void DspFirSafeEnable_Click(object sender, RoutedEventArgs e)
        {
            RoomCorrectionState st = RoomCorrectionStore.Load();
            if (string.IsNullOrWhiteSpace(st.IrPath))
            {
                if (RoomClipRiskText != null)
                {
                    RoomClipRiskText.Text = "还没导入 IR：先点上方「打开房间校正」导入脉冲响应 WAV，再启用卷积。";
                }

                return;
            }

            st.Enabled = true;
            st.TrimDb = Math.Min(Math.Round(st.TrimDb, 1), -6.0);
            RoomCorrectionStore.Save(st);
            _audioEngine?.SetRoomCorrection(st);

            if (RoomTrimSlider != null)
            {
                RoomTrimSlider.Value = st.TrimDb;
            }

            RefreshRoomCorrectionInfo();
            UpdateDspNavIndicators();
            UpdateDspBitPerfectUi();
        }

        // ---------- 声道左右偏差可视化 ----------

        /// <summary>用两条进度条显示左右输出电平差：越长代表该侧越响，居中时两条等长。</summary>
        private void UpdateDspChannelBars()
        {
            if (DspChannelLeftBar == null || DspChannelRightBar == null || DspChannelSkewText == null)
            {
                return;
            }

            double balance = AudioFxChannelBalanceSlider?.Value ?? 0;
            double leftGain = AudioFxChannelLeftGainSlider?.Value ?? 0;
            double rightGain = AudioFxChannelRightGainSlider?.Value ?? 0;

            // 平衡 -1..1 折算成最多 ±6 dB 的等效偏差，再加上两侧手动增益差
            double skewDb = (rightGain - leftGain) + (balance * 6.0);
            double leftPct = Math.Clamp(50 - (skewDb * 4.0), 8, 92);
            double rightPct = Math.Clamp(50 + (skewDb * 4.0), 8, 92);

            DspChannelLeftBar.Value = leftPct;
            DspChannelRightBar.Value = rightPct;

            double abs = Math.Abs(skewDb);
            DspChannelSkewText.Text = abs < 0.05
                ? "居中"
                : (skewDb > 0 ? "偏右 " : "偏左 ") + abs.ToString("0.0") + " dB";
        }

        // ---------- 输出链路条 ----------

        /// <summary>刷新「输出 · 安全监控」页的链路条：输入 → 余量 → 处理 → 输出。</summary>
        private void UpdateDspSafetyChain()
        {
            if (DspChainInputText == null)
            {
                return;
            }

            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;

            string src = SrcSessionStateText != null ? SrcSessionStateText.Text : string.Empty;
            DspChainInputText.Text = (!string.IsNullOrWhiteSpace(src) && src != "—")
                ? src
                : "原始采样率直出";

            double headroom = AudioFxSafetyHeadroomSlider?.Value ?? 0;
            DspChainHeadroomText.Text = FormatHelper.FormatAudioFxDb(headroom) + " dB";

            string[] names = { "余量", "SRC", "EQ", "OPRA", "FIR", "声道", "RG" };
            bool[] active = DspModuleActive();
            var on = new List<string>();
            for (int i = 0; i < names.Length && i < active.Length; i++)
            {
                if (active[i])
                {
                    on.Add(names[i]);
                }
            }

            DspChainProcessText.Text = bypass ? "全部旁路" : (on.Count == 0 ? "无（直通）" : string.Join(" → ", on));

            int clip = _audioEngine?.OutputClipCount ?? 0;
            float peak = _audioEngine?.OutputPeakDbfs ?? float.NegativeInfinity;
            DspChainOutputText.Text = clip > 0
                ? "已削波"
                : (peak > -0.5f ? "接近满刻度" : "正常");
        }

        // ---------- 房间校正（FIR）：IR 信息 / 微调余量 trim / 削波风险 ----------

        private void RoomTrimSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (RoomTrimLabel != null)
            {
                RoomTrimLabel.Text = "微调余量 (trim)：" + FormatHelper.FormatAudioFxDb(e.NewValue) + " dB";
            }

            if (_audioFxLoading || !_audioFxPanelReady)
            {
                return;
            }

            RoomCorrectionState st = RoomCorrectionStore.Load();
            st.TrimDb = Math.Round(e.NewValue, 1);
            RoomCorrectionStore.Save(st);
            _audioEngine?.SetRoomCorrection(st);
            UpdateDspBitPerfectUi();
        }

        /// <summary>从持久化状态初始化 trim 滑杆（只在加载面板时调用一次，避免打断拖动）。</summary>
        private void InitRoomCorrectionTrimUi()
        {
            RoomCorrectionState st = RoomCorrectionStore.Load();
            if (RoomTrimSlider != null)
            {
                RoomTrimSlider.Value = Math.Clamp(st.TrimDb, -24, 6);
            }

            if (RoomTrimLabel != null)
            {
                RoomTrimLabel.Text = "微调余量 (trim)：" + FormatHelper.FormatAudioFxDb(st.TrimDb) + " dB";
            }

            RefreshRoomCorrectionInfo();
        }

        /// <summary>刷新 IR 信息与削波风险提示。</summary>
        private void RefreshRoomCorrectionInfo()
        {
            if (RoomIrInfoText == null)
            {
                return;
            }

            RoomCorrectionState st = RoomCorrectionStore.Load();
            if (!st.Enabled || string.IsNullOrWhiteSpace(st.IrPath))
            {
                RoomIrInfoText.Text = "未导入 IR：点上方卡片的「打开房间校正」导入脉冲响应 WAV。";
                if (RoomClipRiskText != null)
                {
                    RoomClipRiskText.Text = "削波风险：—";
                }

                return;
            }

            string name = string.IsNullOrWhiteSpace(st.IrName) ? System.IO.Path.GetFileName(st.IrPath) : st.IrName;
            int taps = st.IrTapCount > 0 ? st.IrTapCount : (_audioEngine?.ConvolutionIrTaps ?? 0);
            int latencyFrames = _audioEngine?.ConvolutionLatencyFrames ?? 0;
            double latencyMs = latencyFrames > 0 && st.IrSampleRate > 0 ? latencyFrames * 1000.0 / st.IrSampleRate : 0;
            string chText = st.IrChannels >= 2 ? "立体声" : st.IrChannels == 1 ? "单声道" : "—";

            RoomIrInfoText.Text = "IR：" + name
                + " ｜ 长度 " + (taps > 0 ? taps + " taps" : "—")
                + " ｜ 源采样率 " + (st.IrSampleRate > 0 ? st.IrSampleRate + " Hz" : "—")
                + " ｜ 声道 " + chText
                + " ｜ 卷积延迟 " + (latencyMs > 0 ? latencyMs.ToString("F1") + " ms" : "—");

            if (RoomClipRiskText != null)
            {
                bool risk = _audioEngine?.ConvolutionClippingRisk ?? false;
                RoomClipRiskText.Text = risk
                    ? "⚠ 卷积输出已达到满刻度（已自动钳位）：把 trim 调低，或到「输入 · 余量」加大余量。"
                    : "削波风险：无";
            }
        }

        // ---------- 输出安全监控 ----------

        private void OutMonitorReset_Click(object sender, RoutedEventArgs e)
        {
            _audioEngine?.ResetOutputStats();
            UpdateDspOutputMonitor();
        }

        /// <summary>刷新「输出 · 安全监控」页的数值。数据全部来自引擎侧真实统计。</summary>
        private void UpdateDspOutputMonitor()
        {
            if (OutMonitorPeakText == null)
            {
                return;
            }

            float peak = _audioEngine?.OutputPeakDbfs ?? float.NegativeInfinity;
            int clip = _audioEngine?.OutputClipCount ?? 0;

            OutMonitorPeakText.Text = float.IsInfinity(peak) ? "—（无数据）" : peak.ToString("F1") + " dBFS";
            OutMonitorClipText.Text = clip == 0 ? "0 次" : clip + " 次";
            OutMonitorOverloadText.Text = clip > 0 ? "⚠ 已削波" : (peak > -0.5f ? "接近满刻度" : "正常");

            // 活跃 DSP 清单：按信号链顺序列出正在参与处理的模块
            string[] names = { "余量/限幅", "SRC 升频", "参数 EQ", "耳机校正", "FIR 卷积", "声道工具", "ReplayGain" };
            bool[] active = DspModuleActive();
            var on = new List<string>();
            for (int i = 0; i < names.Length && i < active.Length; i++)
            {
                if (active[i])
                {
                    on.Add(names[i]);
                }
            }

            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;
            OutMonitorActiveDspText.Text = bypass
                ? "无（DSP 总旁路中）"
                : (on.Count == 0 ? "无" : string.Join("、", on));

            // 采样率链路：沿用 SRC 会话状态文本（未升频时显示直出）
            string src = SrcSessionStateText != null ? SrcSessionStateText.Text : string.Empty;
            OutMonitorChainText.Text = string.IsNullOrWhiteSpace(src) || src == "—"
                ? "未升频（直出原始采样率）"
                : src;

            // 链路条与上面的监控数值同源，一起刷新
            UpdateDspSafetyChain();
        }

        /// <summary>仅监控页可见时运行的 500ms 刷新定时器。</summary>
        private void EnsureDspMonitorTimer(bool enable)
        {
            if (_dspMonitorTimer == null)
            {
                DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
                if (queue == null)
                {
                    return;
                }

                _dspMonitorTimer = queue.CreateTimer();
                _dspMonitorTimer.Interval = TimeSpan.FromMilliseconds(500);
                _dspMonitorTimer.Tick += (s, a) => UpdateDspOutputMonitor();
            }

            if (enable)
            {
                _dspMonitorTimer.Start();
            }
            else
            {
                _dspMonitorTimer.Stop();
            }
        }
    }
}
