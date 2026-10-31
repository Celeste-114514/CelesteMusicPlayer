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
            _dspRackReady = true;


            // 先备好方案页状态（徽章/圆点要用协调器里的生效方案名），再统一刷指示
            RefreshDspProfileUi();
            UpdateDspNavIndicators();
            if (_dspPageIndex < 0)
            {
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

                return;
            }

            double trim = Math.Min(Math.Round(FirParameters.Current.TrimDb, 1), -6.0);
            SetModelControl("RoomTrimSlider", NumberMessage(trim));
            SetModelControl("RoomIrEnabledToggle", SwitchMessage(true));
            ApplyFirParameters();

            UpdateDspNavIndicators();
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
        }

        /// <summary>从模型初始化 trim 滑杆（只在加载面板时调用一次，避免打断拖动）。</summary>
        private void InitRoomCorrectionTrimUi()
        {
            // 面板初始化这一步就是模型的首次装载点（进入 FIR 页时 LoadRoomCorrectionIntoPanel 会再装一次，幂等）
            FirParameters.Load(RoomCorrectionStore.Load());


            UpdateRoomTrimLabel();
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
        }
    }
}
