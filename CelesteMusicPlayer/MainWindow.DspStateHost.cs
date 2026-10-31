using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer;

public sealed partial class MainWindow
{
    // One editing session for web DSP, device profiles and the legacy presentation adapter.
    private readonly EqEditSession EqState = new();
    private readonly DspApplyService DspApply = new();
    private readonly DspExtraParameters ExtraParameters = new();
    private readonly DspRgParameters RgParameters = new();
    private readonly DspSrcParameters SrcParameters = new();
    private readonly DspCompParameters CompParameters = new();
    private readonly DspFieldParameters FieldParameters = new();
    private readonly DspMatrixParameters MatrixParameters = new();
    private readonly DspFirParameters FirParameters = new();
    private readonly DspRackParameters RackParameters = new();
    private readonly DspProfileCoordinator ProfileCoordinator = new();

    /// <summary>Modules whose business values already live in a model instead of their controls.</summary>
    private IEnumerable<string> ProjectedControls => WebDspModuleControls["channel"]
        .Concat(WebDspModuleControls["headroom"]).Concat(WebDspModuleControls["rg"])
        .Concat(WebDspModuleControls["src"]).Concat(WebDspModuleControls["comp"])
        .Concat(WebDspModuleControls["stereo"]).Concat(WebDspModuleControls["matrix"])
        .Concat(WebDspModuleControls["fir"]);

    /// <summary>Which model owns a control name (null = still on the legacy control adapter).</summary>
    private IDspParameterModel? ModelOf(string name)
    {
        if (ExtraParameters.Owns(name)) return ExtraParameters;
        if (RgParameters.Owns(name)) return RgParameters;
        if (SrcParameters.Owns(name)) return SrcParameters;
        if (CompParameters.Owns(name)) return CompParameters;
        if (FieldParameters.Owns(name)) return FieldParameters;
        if (MatrixParameters.Owns(name)) return MatrixParameters;
        return FirParameters.Owns(name) ? FirParameters : null;
    }

    private bool ModelOwns(string name) => ModelOf(name) != null;

    private object DescribeModelControl(string name) =>
        (ModelOf(name) ?? throw new ArgumentException(name)).Describe(name);

    private bool SetModelControl(string name, JsonElement message) =>
        (ModelOf(name) ?? throw new ArgumentException(name)).Set(name, message);

    private static JsonElement SwitchMessage(bool value) => JsonSerializer.SerializeToElement(new { value });

    private static JsonElement NumberMessage(double value) => JsonSerializer.SerializeToElement(new { value });

    // Presentation adapter only. Guarded writes cannot feed back into the parameter model.
    private void ProjectExtraParameters()
    {
        bool loading = _audioFxLoading, syncing = _dspPowerSyncing;
        _audioFxLoading = true; _dspPowerSyncing = true;
        try
        {
            foreach (string name in ProjectedControls)
            {
                if (!ModelOwns(name)) continue;
                var descriptor = JsonSerializer.SerializeToElement(DescribeModelControl(name));
                switch (WebDspControl(name))
                {
                    case Slider slider: slider.Value = descriptor.GetProperty("value").GetDouble(); break;
                    case ToggleSwitch toggle: toggle.IsOn = descriptor.GetProperty("value").GetBoolean(); break;
                    case ComboBox combo: combo.SelectedIndex = descriptor.GetProperty("index").GetInt32(); break;
                }
            }
        }
        finally { _audioFxLoading = loading; _dspPowerSyncing = syncing; }
        AudioFxSafetyHeadroomLabel.Text = "余量 (dB)：" + FormatHelper.FormatAudioFxDb(ExtraParameters.Safety.HeadroomDb);
        RefreshCrossfeedCutoffReadout(); RefreshDspXfeedLevelReadout(); RedrawDspXfeedCurve(); UpdateDspChannelBars();
        AudioFxRgPreampLabel.Text = "额外增益 (dB)：" + FormatHelper.FormatAudioFxDb(RgParameters.Current.PreampDb);
        RefreshAudioFxRgInfo();
        RefreshFirReadouts();
    }

    // Legacy editors may still raise input events. Import only the field that changed,
    // rather than reconstructing all business parameters from controls on every EQ edit.
    private void ImportExtraParameterInput(object sender)
    {
        if (_audioFxLoading || !_audioFxPanelReady) return;
        foreach (string name in ProjectedControls)
        {
            if (name.StartsWith("DspPower", StringComparison.Ordinal) || !ModelOwns(name)) continue;
            if (!ReferenceEquals(WebDspControl(name), sender)) continue;
            var message = sender switch
            {
                Slider slider => JsonSerializer.SerializeToElement(new { value = slider.Value }),
                ToggleSwitch toggle => JsonSerializer.SerializeToElement(new { value = toggle.IsOn }),
                ComboBox combo => JsonSerializer.SerializeToElement(new { index = combo.SelectedIndex }),
                _ => default
            };
            if (message.ValueKind == JsonValueKind.Object) SetModelControl(name, message);
            return;
        }

    }

    // DSP 装载中旗标（2026-10-31 从 MainWindow.EqDsp.cs 搬来）：DspStateHost 投影网页状态在读写。
    private bool _audioFxLoading;
    // DSP 电源开关防回环旗标（2026-10-31 从 MainWindow.DspPower.cs 搬来）：同上，DspStateHost 在读写。
    private bool _dspPowerSyncing;

        // DSP 面板就绪旗标（2026-10-31 从 MainWindow.EqDsp.cs 搬来）：DspStateHost/Playback 读写。

        private bool _audioFxPanelReady;

        // 机架就绪旗标（2026-10-31 从 MainWindow.DspRack.cs 搬来）。

        private bool _dspRackReady;

        // ==== 模块启用判定族（2026-10-31 从 MainWindow.DspNav.cs / DspRack.cs 搬来）====
        // WebDsp 桥按 DspActive* 下标读圆点状态；模型与状态容器（EqState/ExtraParameters 等）都在本文件。

        /// <summary>各模块当前是否参与处理（索引与导航顺序一致；末位为监控页恒亮）。</summary>
        private bool[] DspModuleActive()
        {
            // 圆点语义 = "正在参与处理"。余量(负增益)才是安全模块真正逐样本处理的情形；
            // 限幅开关单独开着只待命（源 PCM 不超 ±1 时无需削波），不能点亮圆点
            // （2026-09-22 用户实测"限幅没开却显示开了"的同类口径问题，已对齐）。
            bool headroom = Math.Abs(ExtraParameters.Safety.HeadroomDb) > 0.01;

            int srcHz = SrcParameters.TargetHz;

            bool eq = EqState.Current != null && EqState.Current.Enabled && EqState.Current.HasEffect();
            bool fir = FirParameters.Current.Enabled;
            bool ch = ExtraParameters.Channel.Enabled;
            bool rg = RgParameters.Current.Mode != ReplayGainMode.Off;

            // 顺序与 BuildDspNav 元数据表一致（具名下标见 DspActive* 常量）：
            // 听音方案 / 机架编排 / 余量 / RG / SRC / EQ / 压缩 / 声道 / 声场 / 矩阵 / FIR / 监控
            // （耳机校正 2026-10-01 并入 EQ 页，不再占下标；链路条里的「耳机校正」按曲线直读）
            // 方案页圆点 = 当前设置属于某份已保存方案（是"当前状态"，不是处理模块，故跟着方案走）；
            // 机架编排恒 false：编排顺序不是"效果开关"，改顺序不产生处理，点不亮圆点
            bool comp = DspCompActive();
            bool field = DspFieldActive();
            bool matrix = DspMatrixActive();
            // Crossfeed 2026-10-01 并回「声道工具」页，不再单独亮圆点：
            // 它本来就是「声道平衡」模块的子能力（父开关关着时根本不会下发到内核，
            // 见 ManagedDspSourceProvider.UpdateChannel 的「state.Enabled 才下发 crossfeed」），
            // 自己开 Crossfeed 时联动会把父开关一并打开，所以父开关状态就够代表了。

            return new[]
            {
                !string.IsNullOrEmpty(ProfileCoordinator.ActiveName), false, headroom, rg, srcHz > 0, eq,
                comp, ch, field, matrix, fir, true
            };
        }

        /// <summary>刷新左侧导航圆点：绿 = 该模块正在参与处理，灰 = 未启用。</summary>
        private void UpdateDspNavIndicators()
        {
            // 先判一次"当前设置是否已脱离生效方案"：圆点/徽章都要用这个最终状态
            EnsureDspProfileFresh();

            bool[] active = DspModuleActive();
            for (int i = 0; i < _dspNavEntries.Count && i < active.Length; i++)
            {
                Border? dot = _dspNavEntries[i].Dot;
                if (dot == null)
                {
                    continue;
                }

                // 极客下「启用」圆点走磷光色；熄灭色也换一套（原来的 #B4B2A9 在极客深底上偏亮、太扎眼）
                dot.Background = new SolidColorBrush(active[i]
                    ? (GeekDspAccentColor() ?? Color.FromArgb(255, 0x3B, 0x6D, 0x11))
                    : (_geekDspActive ? Color.FromArgb(255, 0x4A, 0x4A, 0x42) : Color.FromArgb(255, 0xB4, 0xB2, 0xA9)));
            }

            if (OutMonitorActiveDspText != null)
            {
                UpdateDspOutputMonitor();
            }

            // 机架编排页的行状态与导航圆点同源，一起刷（页面不可见时刷新无副作用，控件判空即可）
            RefreshDspRackRows();

            UpdateDspHeroBadges();
            // 页头电源开关与徽章/圆点同源同步（10 个可开关模块；同步过程有 _dspPowerSyncing 守卫）
            SyncDspPowerSwitches();
            UpdateDspChannelBars();
        }

        /// <summary>耳机校正（OPRA）曲线是否正作为当前 EQ 曲线。
        /// 2026-10-01 由「应用时置位的字段」改为按曲线 id 直读：换预设 / 重置 / 应用方案
        /// 切走曲线后，链路条与监控页不会再留陈旧态。只有 opra- 前缀的曲线才算
        /// （与 MainWindow.DspProfiles 应用方案时同口径）。</summary>
        private bool _opraApplied => EqState.Current?.PresetId?.StartsWith("opra-", StringComparison.OrdinalIgnoreCase) == true;

        private const int DspActiveProfiles = 0;

        private const int DspActiveRack = 1;

        private const int DspActiveHeadroom = 2;

        private const int DspActiveRg = 3;

        private const int DspActiveSrc = 4;

        private const int DspActiveEq = 5;

        private const int DspActiveComp = 6;

        private const int DspActiveChannel = 7;

        private const int DspActiveField = 8;

        private const int DspActiveMatrix = 9;

        private const int DspActiveFir = 10;

        private const int DspActiveSafety = 11;

        private bool DspCompActive()
        {
            return CompParameters.Current.Enabled && CompParameters.Current.Mix > 0.001;
        }

        private bool DspFieldActive()
        {
            return FieldParameters.Current.Enabled
                && (Math.Abs(FieldParameters.Current.Width - 1.0) > 0.005
                    || Math.Abs(FieldParameters.Current.CenterGainDb) > 0.05
                    || Math.Abs(FieldParameters.Current.SideGainDb) > 0.05);
        }

        private bool DspMatrixActive()
        {
            return MatrixParameters.Current.Enabled
                && (Math.Abs(MatrixParameters.Current.LeftToLeft - 1.0) > 0.005
                    || Math.Abs(MatrixParameters.Current.RightToLeft) > 0.005
                    || Math.Abs(MatrixParameters.Current.LeftToRight) > 0.005
                    || Math.Abs(MatrixParameters.Current.RightToRight - 1.0) > 0.005);
        }

}