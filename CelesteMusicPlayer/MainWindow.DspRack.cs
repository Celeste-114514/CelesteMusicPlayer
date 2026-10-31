// MainWindow.DspRack.cs
// 新增 DSP 模块的 UI 逻辑：动态压缩器 / 立体声场 / 声道矩阵 / DSP 机架编排，
// 以及「耳机 Crossfeed 截止频率」的可调化。
//
// 设计边界（与既有 DSP 面板的关系）：
//   - 这些模块的内核实现早已存在（Stage B 时 celeste_dsp_core.dll 就支持），
//     RackState / CompressorSettings / StereoFieldSettings / ChannelMatrixSettings
//     的状态模型 + 持久化也已在 DspRackState.cs 准备好，一直缺 UI。
//   - 下发通道直接用 _audioEngine.SetRack(RackState)：它会 Save 到 dsp-rack.json
//     并实时下发内核（原子接口，下一个音频块生效），未播放时只存状态，
//     下次播放会话由 BuildDspProvider 应用 —— 无需自己写 provider 推送。
//   - UI 侧的读写经由 DspRackStore（disk-backed cache），不自己持有镜像，
//     避免"面板状态"与"实际生效状态"两份真值漂移。
//
// 加载期保护：XAML 默认值触发的 ValueChanged/Toggled 会早于存档读取到达，
// 必须用 _dspRackLoading 挡掉，否则会用默认值覆盖用户上次的设置。

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>机架模块显示名，下标 = DspCoreInterop.Rack* 常量。</summary>
        private static readonly string[] RackModuleNames =
        {
            "参数 EQ",
            "FIR / 卷积",
            "响度 · ReplayGain",
            "动态压缩器",
            "耳机 Crossfeed",
            "立体声场",
            "声道矩阵",
            "声道平衡"
        };

        /// <summary>机架模块一句话说明，用于编排页选中提示（下标同上）。</summary>
        private static readonly string[] RackModuleHints =
        {
            "31 段参数化均衡（与耳机校正共用同一曲线）。",
            "导入的房间/耳机脉冲响应卷积，延迟最高，适合放在前面。",
            "把不同来源的音量拉到统一响度，应尽早处理以免后续模块互相影响。",
            "压平动态起伏；放在 EQ 之后可避免 EQ 抬高的频率压不下来。",
            "把对侧低频混入本侧，模拟音箱串扰。",
            "在中间/两侧两个维度上调整声场宽度。",
            "2×2 线性混合，可做声道互换、合并或去唱。",
            "左右平衡/增益/延迟/单声道/反相，机架最末端。"
        };

        // 加载期保护：true 期间所有 handler 只更新读数，不回写 store、不下发内核
        private bool _dspRackLoading;

        // 面板读取完成标志（InitDspNav 里置 true）：
        // 在此之前任何 handler 都不准回写——否则 XAML 默认值会在存档读取前覆盖用户设置

        // 压缩器 GR 轮询（仅压缩器页可见时跑）

        // ─────────────────────────────────────────────────────────────
        // 下发：UI → store → 内核
        // ─────────────────────────────────────────────────────────────

        /// <summary>收集三模块的当前参数并下发。<see cref="LoadDspRackUi"/> 期间不得调用。</summary>
        private void PushDspRackToEngine()
        {
            if (!_dspRackReady || _dspRackLoading || DspCompToggle == null)
            {
                return;
            }

            RackState rack = DspRackStore.Load();

            // 三个模块的参数现在都来自模型；控件只是它们的显示投影。
            rack.Compressor = CompParameters.Snapshot();
            rack.StereoField = FieldParameters.Snapshot();
            rack.Matrix = MatrixParameters.Snapshot();

            // SetRack：Save(dsp-rack.json) + 实时下发；未播放时仅存状态，下次播放会话应用
            if (_audioEngine is { } engine) engine.SetRack(rack);
            else DspRackStore.Save(rack);

            UpdateDspNavIndicators();
        }

        // ─────────────────────────────────────────────────────────────
        // 读数刷新（每个控件旁的数值文字）
        // ─────────────────────────────────────────────────────────────

        private void RefreshDspCompReadouts()
        {

        }

        private void RefreshDspFieldReadouts()
        {

        }

        private void RefreshDspMatrixReadouts()
        {

        }

        private void RefreshCrossfeedCutoffReadout()
        {
            if (AudioFxChannelCrossfeedCutoffText == null || AudioFxChannelCrossfeedCutoffSlider == null)
            {
                return;
            }

        }

        // ─────────────────────────────────────────────────────────────
        // 事件：压缩器
        // ─────────────────────────────────────────────────────────────

        /// <summary>把模型里的压缩器参数下发（网页命令与页头电源开关共用）。</summary>
        private void ApplyCompParameters()
        {
            RefreshDspCompReadouts();
            PushDspRackToEngine();
        }

        // ─────────────────────────────────────────────────────────────
        // 事件：立体声场
        // ─────────────────────────────────────────────────────────────

        /// <summary>把模型里的声场参数下发（网页命令与页头电源开关共用）。</summary>
        private void ApplyFieldParameters()
        {
            RefreshDspFieldReadouts();
            PushDspRackToEngine();
        }

        // ─────────────────────────────────────────────────────────────
        // 事件：声道矩阵
        // ─────────────────────────────────────────────────────────────

        /// <summary>把模型里的矩阵参数下发（网页命令与页头电源开关共用）。</summary>
        private void ApplyMatrixParameters()
        {
            RefreshDspMatrixReadouts();
            PushDspRackToEngine();
        }

        // ─────────────────────────────────────────────────────────────
        // 机架编排（链路全景视图）—— 行模型 DspRackRow 见本文件底部顶层类
        // ─────────────────────────────────────────────────────────────

        /// <summary>行状态级别。</summary>
        private enum DspRackState
        {
            Off = 0,
            On = 1,
            Bypassed = 2
        }

        private List<DspRackRow> _dspRackRows = new();

        /// <summary>当前是否被 DSP 总旁路（总旁路时机架行一律显示"已旁路"）。</summary>
        private bool DspRackGlobalBypassed => RackParameters.Bypassed;

        // ─────────────────────────────────────────────────────────────
        // 事件：机架编排
        // ─────────────────────────────────────────────────────────────

        /// <summary>恢复默认机架顺序核心（网页「音效处理」页直接调）。</summary>
        internal void ResetDspRackOrderCore()
        {
            // 恢复出厂顺序并下发；CommitDspRackOrder 内部会 load→save→重绘
            RackParameters.Reset();
            CommitDspRackOrder();
        }

        /// <summary>把模型里的顺序写回 store 并下发内核。顺序合法（0..7 的排列）由模型与
        /// RackState.Normalize 双重兜底；内核若拒绝会自行回退默认并记日志。</summary>
        private void CommitDspRackOrder()
        {
            RackState rack = DspRackStore.Load();
            rack.RackOrder = RackParameters.Snapshot();
            if (_audioEngine is { } engine) engine.SetRack(rack);
            else DspRackStore.Save(rack);
            UpdateDspNavIndicators();
        }

        // ─────────────────────────────────────────────────────────────
        // 新模块的"是否生效"判定（供导航圆点 / 页徽章）
        // 口径沿用既有约定：并非"开关打开"就算，而是真的对声音产生影响才算。
        // ─────────────────────────────────────────────────────────────

    }

    /// <summary>
    /// 机架编排页的一行（链路全景）：8 个内核模块按机架顺序排列，全部可上下移。
    /// SRC / 耳机校正 / 输出安全是链路结构决定的上下游，不占机架槽位。
    /// 状态色在生成行时按当前皮肤算好（极客=磷光，经典=主题 accent），
    /// 皮肤切换时整表重建即可换色（ApplyGeekDsp 收尾调用）。
    /// 顶层类而非 MainWindow 嵌套类——XAML x:Bind 的 x:DataType 只可靠解析顶层类型，
    /// 与项目里 PlaylistItem / AlbumEntry 等行模型的既有做法一致。
    /// </summary>
    public sealed class DspRackRow
    {
        public required string SlotLabel { get; init; }
        public required string Name { get; init; }
        public required string Hint { get; init; }
        public required string StateText { get; init; }
        public required Brush StateBrush { get; init; }

        /// <summary>true = 可上下移（机架 8 行全部可调；保留字段兜底防御）。</summary>
        public bool Movable { get; init; }

        /// <summary>内核模块 ID。</summary>
        public int ModuleId { get; init; } = -1;
    }
}
