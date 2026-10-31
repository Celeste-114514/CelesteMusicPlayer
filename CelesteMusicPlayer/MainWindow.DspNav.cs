using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 音效处理页面：左侧「处理链路」模块导航 + 右侧模块内容分区。
    /// 2026-09-29 起对齐 ECHO DSP 模块集：左导航按信号链分 6 组
    /// （输入 / 采样率 / 塑形 / 声道 / 空间 / 输出安全），导航项由本文件的元数据表
    /// 动态生成——新增模块只需在 <see cref="BuildDspNav"/> 里加一行，不再手写 XAML。
    /// 置顶两项（听音方案 / DSP 机架编排）不挂组标题，排在分组之上。
    /// 本文件只做页面切换与状态指示，不改动任何 DSP 处理逻辑。
    /// </summary>
    public sealed partial class MainWindow
    {
        // 页面索引。早期写成硬编码常量（Safety=7 / Fir=6 / Channel=5 / Eq=3），
        // 在导航中间插入新模块后必然错位；改为按 Page 引用从已构建的元数据表反查，
        // 增删页面/调整顺序都不需要再同步这里（导航未构建时返回 -1，调用处均能容忍）。
        private int DspPageSafetyIndex => FindDspPageIndex(DspPageSafety);
        // ReplayGain 页（2026-10-12 阶段 2：批量扫描并入本页，需要一个能跳转过去的索引）
        private int DspPageRgIndex => FindDspPageIndex(DspPageRg);
        private int DspPageFirIndex => FindDspPageIndex(DspPageFir);
        private int DspPageChannelIndex => FindDspPageIndex(DspPageChannel);
        private int DspPageEqIndex => FindDspPageIndex(DspPageEq);
        private int DspPageCompressorIndex => FindDspPageIndex(DspPageCompressor);
        private int DspPageSrcIndex => FindDspPageIndex(DspPageSrc);
        private int DspPageMatrixIndex => FindDspPageIndex(DspPageMatrix);
        private int DspPageFieldIndex => FindDspPageIndex(DspPageStereoField);
        private int DspPageRackIndex => FindDspPageIndex(DspPageRack);
        private int DspPageProfilesIndex => FindDspPageIndex(DspPageProfiles);

        // DspModuleActive() 返回数组的具名下标，顺序与 BuildDspNav 元数据表一致。
        // 「链路条 / 活跃 DSP 清单」曾按 names[i] ↔ active[i] 直接对下标取用，机架编排置顶后就整体错位
        // （余量被读成机架编排的恒 false）。改成具名下标显式对应，之后再插页面不会再牵连这两处读数。
        // ⚠ 2026-10-01：Crossfeed 从独立页并回「声道工具」页；同日耳机校正并入 EQ 页，
        // 导航 13 → 12 项，下面整体再前移一位。开关控件都还在，只是不再单独占一页。

        /// <summary>在导航表里查某个页面当前的下标；未构建 / 页面为空时返回 -1。</summary>
        private int FindDspPageIndex(StackPanel? page)
        {
            if (page == null)
            {
                return -1;
            }

            for (int i = 0; i < _dspNavEntries.Count; i++)
            {
                if (ReferenceEquals(_dspNavEntries[i].Page, page))
                {
                    return i;
                }
            }

            return -1;
        }

        private int _dspPageIndex = -1;
        private DispatcherQueueTimer? _dspMonitorTimer;


        /// <summary>导航项运行时态：标题 / 所属组 / 右侧页面 / 徽章控件。</summary>
        private sealed class DspNavEntry
        {
            public required string Title { get; init; }
            public required string Group { get; init; }
            public StackPanel? Page { get; init; }
            public Border? Badge { get; init; }
            public TextBlock? BadgeText { get; init; }
            public Button? Button { get; set; }
            public Border? Bar { get; set; }
            public TextBlock? Label { get; set; }
            public Border? Dot { get; set; }
        }

        private readonly List<DspNavEntry> _dspNavEntries = new();
        private bool _dspNavBuilt;

        /// <summary>首次进入音效页面时构建导航、定位到第一个模块，并刷新各模块启用指示。</summary>
        private void InitDspNav()
        {
            InitRoomCorrectionTrimUi();
            LoadDspRackUi();
            _dspRackReady = true;


            // 先备好方案页状态（徽章/圆点要用协调器里的生效方案名），再统一刷指示
            RefreshDspProfileUi();
            UpdateDspNavIndicators();
            if (_dspPageIndex < 0)
            {
            }
        }

        // ---------- 模块启用指示（左侧小圆点）----------



        // ---------- 模块页 Hero 状态徽章 ----------

        /// <summary>刷新每个模块页右上角的徽章：绿 = 正在生效，灰 = 未启用，琥珀 = 已旁路。
        /// 数组顺序必须与 BuildDspNav 元数据表、DspModuleActive() 保持一致。
        /// 2026-09-30 起 10 个可开关模块的徽章已换成页头电源开关（MainWindow.DspPower.cs），
        /// 对应位置填 null，循环里跳过；下标保持不变以维持与 active[] 的对应关系。</summary>
        private void UpdateDspHeroBadges()
        {
            Border?[] badges =
            {
                DspBadgeProfiles, DspBadgeRack, null, null, null, null,
                null, null, null, null, null,
                DspBadgeSafety
            };
            TextBlock?[] texts =
            {
                DspBadgeProfilesText, DspBadgeRackText, null, null, null, null,
                null, null, null, null, null,
                DspBadgeSafetyText
            };

            int safetyIndex = DspPageSafetyIndex;
            bool[] active = DspModuleActive();
            bool bypass = RackParameters.Bypassed;

            // 末位是监控页，恒亮（它显示的是状态而不是处理模块）
            for (int i = 0; i < badges.Length; i++)
            {
                Border? badge = badges[i];
                TextBlock? text = texts[i];
                if (badge == null || text == null)
                {
                    continue;
                }

                // 方案页显示的是"当前设置来自哪份方案"，不是开关状态：不参与"生效/旁路"口径。
                // 按控件本身认，不看下标 —— 导航未构建时下标是 -1，这里也不会误走通用分支。
                if (ReferenceEquals(badge, DspBadgeProfiles))
                {
                    ApplyDspProfileBadge();
                    continue;
                }

                bool on = i == safetyIndex || i == DspPageRackIndex || active[i];
                if (bypass && i != safetyIndex)
                {
                    SetDspBadgeState(badge, text, "bypassed", "已旁路");
                }
                else if (on)
                {
                    SetDspBadgeState(badge, text, "on",
                        i == safetyIndex ? "监控中"
                        : i == DspPageRackIndex ? (DspRackOrderIsDefault() ? "默认顺序" : "自定义")
                        : "生效中");
                }
                else
                {
                    SetDspBadgeState(badge, text, "off", "未启用");
                }
            }
        }

        // ---------- 余量快捷预设 ----------


        // ---------- FIR 快捷 trim / 安全启用 ----------

        private void DspFirTrimQuick_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag || !double.TryParse(tag, out double db))
            {
                return;
            }

            if (SetModelControl("RoomTrimSlider", NumberMessage(db)))
            {
                ApplyFirParameters();
            }
        }

        /// <summary>先把 trim 降到 -6 dB 再启用卷积，避免一开就爆音（对齐 ECHO enableFirSafely）。</summary>
        private void DspFirSafeEnable_Click(object sender, RoutedEventArgs e)
        {
            FirSafeEnableCore();
        }

        /// <summary>安全启用卷积核心（网页「音效处理」页直接调）。</summary>
        internal void FirSafeEnableCore()
        {
            // IrReady = 已选 IR 且文件还在盘上（原来只看路径非空，文件被删时也会照样开起来）
            if (!FirParameters.IrReady)
            {
                if (RoomClipRiskText != null)
                {
                }

                return;
            }

            double trim = Math.Min(Math.Round(FirParameters.Current.TrimDb, 1), -6.0);
            SetModelControl("RoomTrimSlider", NumberMessage(trim));
            SetModelControl("RoomIrEnabledToggle", SwitchMessage(true));
            ApplyFirParameters();

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


            double abs = Math.Abs(skewDb);
        }

        // ---------- 输出链路条 ----------

        /// <summary>刷新「输出 · 安全监控」页的链路条：输入 → 余量 → 处理 → 输出。</summary>
        private void UpdateDspSafetyChain()
        {
            if (DspChainInputText == null)
            {
                return;
            }

            bool bypass = RackParameters.Bypassed;

            string src = SrcSessionStateText != null ? SrcSessionStateText.Text : string.Empty;

            double headroom = AudioFxSafetyHeadroomSlider?.Value ?? 0;

            // 顺序按信号链：余量 / RG / SRC / EQ / 耳机校正 / 声道工具（含 Crossfeed）/ FIR
            // 下标用 DspActive* 具名常量显式对应 —— 曾经的 names[i] ↔ active[i] 在页面增删后会整体错位
            bool[] active = DspModuleActive();
            var on = new List<string>();
            if (DspActiveHeadroom < active.Length && active[DspActiveHeadroom])
            {
                on.Add("余量");
            }

            if (DspActiveRg < active.Length && active[DspActiveRg])
            {
                on.Add("ReplayGain");
            }

            if (DspActiveSrc < active.Length && active[DspActiveSrc])
            {
                on.Add("SRC");
            }

            if (DspActiveEq < active.Length && active[DspActiveEq])
            {
                on.Add("EQ");
            }

            // 耳机校正 2026-10-01 并入 EQ 页，不再占导航下标：按当前 EQ 曲线直读（与下面 Crossfeed 同处理）
            if (_opraApplied)
            {
                on.Add("耳机校正");
            }

            if (DspActiveChannel < active.Length && active[DspActiveChannel])
            {
                on.Add("声道工具");
            }

            // Crossfeed 2026-10-01 并回声道工具页后不再单独占一个下标，
            // 这里按控件直读，好在链路条上仍然看得见它有没有真的在跑
            if (AudioFxChannelCrossfeedToggle?.IsOn == true && (AudioFxChannelCrossfeedSlider?.Value ?? 0) > 0.1)
            {
                on.Add("Crossfeed");
            }

            if (DspActiveFir < active.Length && active[DspActiveFir])
            {
                on.Add("FIR");
            }


            int clip = _audioEngine?.OutputClipCount ?? 0;
            float peak = _audioEngine?.OutputPeakDbfs ?? float.NegativeInfinity;
        }

        // ---------- 房间校正（FIR）：IR 信息 / 微调余量 trim / 削波风险 ----------

        private void RoomTrimSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_audioFxLoading || !_audioFxPanelReady)
            {
                return;
            }

            // trim 是「实时生效」的那一个：拖动即进模型并落盘下发（与旧行为一致）
            UpdateRoomTrimLabel();
            ApplyFirParameters();
            UpdateDspBitPerfectUi();
        }

        /// <summary>从模型初始化 trim 滑杆（只在加载面板时调用一次，避免打断拖动）。</summary>
        private void InitRoomCorrectionTrimUi()
        {
            // 面板初始化这一步就是模型的首次装载点（进入 FIR 页时 LoadRoomCorrectionIntoPanel 会再装一次，幂等）
            FirParameters.Load(RoomCorrectionStore.Load());

            if (RoomTrimSlider != null)
            {
            }

            UpdateRoomTrimLabel();
            RefreshRoomCorrectionInfo();
        }

        // ---------- 输出安全监控 ----------

        private void OutMonitorReset_Click(object sender, RoutedEventArgs e)
        {
            ResetOutputMonitorCore();
        }

        /// <summary>重置输出统计核心（网页「音效处理」页直接调）。</summary>
        internal void ResetOutputMonitorCore()
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


            // 活跃 DSP 清单：按信号链顺序列出正在参与处理的模块（具名下标，见 DspActive* 常量）
            string[] names = { "余量/限幅", "ReplayGain", "SRC 升频", "参数 EQ", "声道工具", "FIR 卷积" };
            int[] slots =
            {
                DspActiveHeadroom, DspActiveRg, DspActiveSrc, DspActiveEq,
                DspActiveChannel, DspActiveFir
            };
            bool[] active = DspModuleActive();
            var on = new List<string>();
            for (int i = 0; i < names.Length && i < slots.Length && slots[i] < active.Length; i++)
            {
                if (active[slots[i]])
                {
                    on.Add(names[i]);
                }
            }

            // 耳机校正曲线寄存在 EQ 里（2026-10-01 并入 EQ 页）：按曲线直读，插在「参数 EQ」之后保持链序
            if (_opraApplied)
            {
                int eqAt = on.IndexOf("参数 EQ");
                on.Insert(eqAt >= 0 ? eqAt + 1 : on.Count, "耳机校正");
            }

            bool bypass = RackParameters.Bypassed;

            // 采样率链路：沿用 SRC 会话状态文本（未升频时显示直出）
            string src = SrcSessionStateText != null ? SrcSessionStateText.Text : string.Empty;

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
