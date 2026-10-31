using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
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

    // DSP 装载中旗标（2026-10-31 从 MainWindow.EqDsp.cs 搬来）：DspStateHost 投影网页状态在读写。
    private bool _audioFxLoading;
    // DSP 电源开关防回环旗标（2026-10-31 从 MainWindow.DspPower.cs 搬来）：同上，DspStateHost 在读写。

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
        // DSP 批 2：房间校正应用 + bit-perfect 状态核心离壳

        /// <summary>应用房间校正（卷积 FIR）状态到引擎 + 刷新 bit-perfect 提示 + 链路显示。
        /// 由 RoomCorrectionWindow 调用（播放中实时生效）。</summary>
        internal void ApplyRoomCorrection(RoomCorrectionState state)
        {
            if (state == null)
            {
                return;
            }

            _audioEngine?.SetRoomCorrection(state);
            UpdateSignalChainDisplay();
            StartupLog.Write($"房间校正已应用: enabled={state.Enabled} ir={state.IrPath} gain={state.GainDb}dB");
        }

        /// <summary>
        /// 把开关状态同步成各模块真实状态（导航指示刷新的末端一步）。
        /// 全程 <see cref="_dspPowerSyncing"/> 守卫，避免回写 IsOn 时二次触发 handler 形成回路。
        /// </summary>

        // 各「开关关掉再打开」要恢复的上次值（0/关闭 = 电源断开，记忆的是断开前的档位）
        // ReplayGain / SRC 不在其中：它们的「断开前档位」由各自的参数模型自己持有。
        private double _dspHeadroomMemory { get => ExtraParameters.HeadroomMemory; set => ExtraParameters.HeadroomMemory = value; }
        private double _dspXfeedMemory { get => ExtraParameters.CrossfeedMemory; set => ExtraParameters.CrossfeedMemory = value; } // 耳机 Crossfeed 的强度百分比
        // DSP 批 2：bit-perfect 徽章口径三件套离壳

        // DSP 批 3：EQ 撤销/快照族的干净伴生离壳

        /// <summary>单段在给定频率处的近似幅度贡献（dB）。峰值/架近似用理想带响应，低通/高通/切除按阶近似。</summary>
        private static double ApproxBandGain(double f, EqBand b)
        {
            switch (b.FilterType)
            {
                case EqFilterType.LowPass:
                    var cutoff = Math.Max(20, b.FrequencyHz);
                    if (f >= cutoff) { double x = (f / cutoff); return -6.0 * Math.Log10(1 + x * x); }
                    return 0;
                case EqFilterType.HighPass:
                    var hc = Math.Max(20, b.FrequencyHz);
                    if (f <= hc) { double x = (hc / f); return -6.0 * Math.Log10(1 + x * x); }
                    return 0;
                case EqFilterType.Notch:
                    double dNotch = Math.Abs(Log2(f / b.FrequencyHz));
                    double n = b.Q <= 0 ? 1 : b.Q;
                    if (dNotch <= 0.5 / n) return -6 * Math.Min(1, (0.5 / n - dNotch) * n * 2);
                    return 0;
                case EqFilterType.LowShelf:
                case EqFilterType.HighShelf:
                {
                    double gain = Math.Clamp(b.GainDb, -24, 24);
                    double pivot = b.FilterType == EqFilterType.LowShelf ? 200 : 4000;
                    double dPivot = Math.Abs(Log2(f / pivot));
                    // 简化搁架曲线：靠近目标频段趋近 gain
                    double reach = b.FilterType == EqFilterType.LowShelf ? (f < pivot ? 0 : dPivot) : (f > pivot ? 0 : dPivot);
                    double ratio = Math.Clamp(reach > 0 ? 1.0 - 0.05 : 1.0, 0, 1);
                    // 用频率偏移近似：目标频段外逐渐累积到 gain
                    double extent = Math.Abs(Log2(f / b.FrequencyHz));
                    return gain * Math.Clamp(1.0 / (1.0 + 0.5 * extent), 0.2, 1.0) * ratio;
                }
                default: // Peaking / 其它
                {
                    if (Math.Abs(b.GainDb) < 0.01) return 0;
                    double w = b.FrequencyHz;
                    double x = Math.Log10(f / w) * 6; // 以 octave 计
                    double q = Math.Max(0.1, b.Q);
                    // 峰值带宽 ~ f/Q，用高斯近似
                    double sigma = q > 0 ? 0.7 * (1.0 / q) : 0.6; // octaves
                    return b.GainDb * Math.Exp(-(x * x) / (2 * sigma * sigma));
                }
            }
        }

        private static bool IsShiftDown()
        {
            try
            {
                var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
                return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            }
            catch { return false; }
        }

        /// <summary>推一份「修改前」快照进撤销栈。coalesce=true 时 400ms 内的连续编辑算一步（滑杆拖动 / 连击）。</summary>
        private void PushEqUndoSnapshot(bool coalesce = false)
        {
            EqState.PushUndo(coalesce);
        }

        /// <summary>撤销 EQ 修改核心（网页「音效处理」页直接调）。返回结果消息。</summary>
        internal string UndoAudioFxEqCore()
        {
            if (!EqState.Undo()) return "没有可撤销的 EQ 修改";
            SetAudioFxEqFromSnapshot(EqState.Current);
            string msg = "已撤销 EQ 修改（还可撤销 " + EqState.UndoCount + " 步）";
            return msg;
        }

        /// <summary>重做 EQ 修改核心（网页「音效处理」页直接调）。返回结果消息。</summary>
        internal string RedoAudioFxEqCore()
        {
            if (!EqState.Redo()) return "没有可重做的 EQ 修改";
            SetAudioFxEqFromSnapshot(EqState.Current);
            const string msg = "已重做 EQ 修改";
            return msg;
        }

        /// <summary>
        /// 一句话说出"现在链路上有哪些 DSP 在改样本"，给链路面板的输出记录用。
        /// 口径与 AudioSignalPath 的判定同源（都以各 Store 为准，不看 UI 控件当前值）。
        /// </summary>
        private static string DescribeDspChainSummary()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (EqCurveStore.Load().HasEffect())
            {
                parts.Add("EQ");
            }

            var room = RoomCorrectionStore.Load();
            if (room.Enabled && !string.IsNullOrWhiteSpace(room.IrPath))
            {
                parts.Add("房间校正");
            }

            if (ReplayGainStore.Load().Mode != ReplayGainMode.Off)
            {
                parts.Add("ReplayGain");
            }

            var rack = DspRackStore.Load();
            if (rack.Compressor.IsActive)
            {
                parts.Add("压缩");
            }

            if (rack.StereoField.IsActive)
            {
                parts.Add("声场");
            }

            if (rack.Matrix.IsActive)
            {
                parts.Add("矩阵");
            }

            var extra = DspExtraStore.Load();
            if (extra.ChannelBalance.IsActive)
            {
                parts.Add("声道平衡");
            }

            if (extra.Safety.AffectsBits)
            {
                parts.Add("余量");
            }

            return parts.Count == 0 ? "全部关闭（直通）" : string.Join("、", parts) + " 生效";
        }

        private static double Log2(double v) => Math.Log(v) / Math.Log(2.0);
        // ---- DSP 批 3：EQ 撤销/快照/A-B/自动增益 + 引擎下发核心离壳 ----
        // 2026-10-31 W13 口径：只保留引擎与撤销栈行为；旧原生面板的投影/重绘调用随旧壳退役。

        // EQ 曲线编辑器的坐标常量（ApproxBandGain / 自动增益仍在用）
        private const double EqCurveFreqMin = 20.0, EqCurveFreqMax = 20000.0;
        private const double EqCurveGainMax = 24.0;
        private int _audioFxEqSelected = -1;

        /// <summary>用一份曲线快照整体替换当前状态并同步引擎与撤销栈（撤销/重做/A-B 共用）。
        /// 旧面板投影（开关/预设框/曲线重绘/段编辑器）随旧壳退役。</summary>
        private void SetAudioFxEqFromSnapshot(EqCurveState snapshot)
        {
            if (snapshot == null) return;
            _audioFxLoading = true;
            try
            {
                EqState.Current = snapshot;
                _audioFxEqSelected = snapshot.Bands.Count > 0
                    ? Math.Clamp(_audioFxEqSelected < 0 ? 0 : _audioFxEqSelected, 0, snapshot.Bands.Count - 1)
                    : -1;
            }
            finally
            {
                _audioFxLoading = false;
            }

            ApplyDspToEngine();
        }

        /// <summary>A/B 对比核心（网页「音效处理」页直接调；网页点击不含 Shift，走普通分支）。返回结果消息。</summary>
        internal string ToggleAudioFxEqAbCore()
        {
            EqAbTransition change = EqState.ToggleAb(IsShiftDown());
            string msg;
            switch (change)
            {
                case EqAbTransition.Cancelled:
                    msg = "A/B 对比已取消";
                    ApplyDspToEngine();
                    break;
                case EqAbTransition.Armed:
                    msg = "已把当前曲线存为参考 A；继续随意调整，再点本按钮在 A / 调整后 B 间切换试听";
                    break;
                case EqAbTransition.ShowingA:
                    msg = "A/B 对比：正在试听参考曲线 A（此刻改曲线只作用于 A）";
                    SetAudioFxEqFromSnapshot(EqState.Current);
                    break;
                default:
                    msg = "A/B 对比：已回到你的曲线 B";
                    SetAudioFxEqFromSnapshot(EqState.Current);
                    break;
            }
            return msg;
        }

        /// <summary>自动增益核心（网页「音效处理」页直接调）。返回结果消息。</summary>
        internal string AutoGainAudioFxEqCore()
        {
            PushEqUndoSnapshot();
            // 估算峰值叠加增益：所有 band 在任一频率的最大正贡献 + headroom
            double peak = 0;
            double logMin = Log2(EqCurveFreqMin), logMax = Log2(EqCurveFreqMax);
            double dAddFreq = Math.Pow(EqCurveFreqMax / EqCurveFreqMin, 1.0 / 200.0);
            double f = EqCurveFreqMin;
            for (int i = 0; i <= 200; i++)
            {
                double g = 0;
                foreach (var b in EqState.Current.Bands) { if (b is { Enabled: true }) g += ApproxBandGain(f, b); }
                peak = Math.Max(peak, g);
                f *= dAddFreq;
            }

            double preampDb = -Math.Max(0, peak);
            EqState.Current.PreampDb = Math.Clamp(preampDb, -24, 24);
            ApplyDspToEngine();
            string msg = "自动增益：preamp = " + FormatHelper.FormatAudioFxDb(EqState.Current.PreampDb) + " dB";
            return msg;
        }

        /// <summary>收集面板当前状态 → 持久化 + 应用到播放引擎。</summary>
        private void ApplyDspToEngine()
        {
            // 面板未就绪（启动阶段控件以 XAML 默认值加载触发的 Toggled 等）不得持久化/应用，
            // 否则会用默认的“打开”状态覆盖盘上用户上次关闭的设置，导致“关闭后重启又打开”。
            if (!_audioFxPanelReady)
            {
                return;
            }

            // 诊断：记录触发保存的调用来源，便于定位“关闭后又变回打开”是被谁触发的。
            string trigger = "";
            try
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
                for (int i = 0; i < st.GetFrames()?.Length; i++)
                {
                    System.Reflection.MethodBase? m = st.GetFrame(i)?.GetMethod();
                    string? decl = m?.DeclaringType?.Name;
                    if (decl != null && decl != "MainWindow" && decl != "AppWindow" && !decl.StartsWith("<>c"))
                    {
                        trigger = decl + "." + m.Name;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(trigger) && st.GetFrames()?.Length > 0)
                {
                    System.Reflection.MethodBase? m0 = st.GetFrame(0)?.GetMethod();
                    trigger = m0?.DeclaringType?.Name + "." + m0?.Name;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            DspExtraState extra = ExtraParameters.Snapshot();
            var ch = extra.ChannelBalance;
            var safety = extra.Safety;
            DspApply.Apply(EqState.Current, EqState.Previewing, extra, _audioEngine);
            StartupLog.Write($"[DSP] 已保存 eq(Enabled={EqState.Current.Enabled},bands={EqState.Current.Bands.Count}) ch(Enabled={ch.Enabled}) limiter={safety.EnableLimiter}  触发={trigger}");

            // 任何 DSP 改动都同步刷新左侧导航圆点 / 模块页徽章 / 左右偏差条
            UpdateDspNavIndicators();

            // B4：DSP 变化 = 链路事件（内容去重：拖滑块连续回调同值不会刷屏）
            NoteChainDsp("dsp", "DSP " + DescribeDspChainSummary());
        }

        // DSP 批 5：极客强调色/徽章状态离壳

        /// <summary>
        /// DSP 面板当前的强调色：极客模式返回磷光色，非极客返回 null（含义＝沿用系统/自定义主题色）。
        /// 面板里凡是「要跟强调色走」的地方一律问它，不要自己读 Application.Resources。
        /// </summary>
        internal Color? GeekDspAccentColor()
            => _geekDspActive && _geekDspPhosphor is Color phosphor ? phosphor : (Color?)null;

        /// <summary>面板强调色画刷：极客下给磷光，否则回落系统主题色（与全局行为一致）。</summary>
        internal Brush DspAccentBrush()
            => GeekDspAccentColor() is Color accent ? new SolidColorBrush(accent) : ResolveAccentBrush();

        // DSP 批 5：EQ 简单模式族与面板构建离壳

        /// <param name="restoreFromStore">true=从持久化恢复上次滑块值；false=清空（用于重置按钮）。</param>
        private void SyncAudioFxEqSimpleFromState(bool restoreFromStore = true)
        {
            // 简单模式各滑块值：默认从持久化恢复（避免重启后回到 0），重置时清空。
            // 进出都保留调用方原有的 _audioFxLoading：本方法会被 LoadAudioFxUiFromStore 的
            // "整段加载"包裹调用，直接置 false 会把外层守卫提前解除，导致后半段控件赋值
            // 当场触发回写（半载状态写盘）。2026-09-30 听音方案重载面板时踩到，改为保存/恢复。
            bool prevLoading = _audioFxLoading;
            _audioFxLoading = true;
            try
            {
                if (restoreFromStore)
                {
                    var t = SimpleEqStore.Load();
                    _eqSimpleBass = t.Bass;
                    _eqSimpleVocal = t.Vocal;
                    _eqSimpleAir = t.Air;
                    _eqSimpleWarm = t.Warm;
                }
                else
                {
                    _eqSimpleBass = _eqSimpleVocal = _eqSimpleAir = _eqSimpleWarm = 0;
                    SimpleEqStore.Save(new SimpleEqState());
                }

            }
            finally { _audioFxLoading = prevLoading; }
        }

        private static void AppendSimple(EqCurveState s, string tone, double strength)
        {
            double[] fr = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
            foreach (double f in fr)
            {
                double g = 0;
                if (tone == "bass")
                {
                    if (f <= 80) g += 2.5 * strength;
                    else if (f <= 160) g += 1.6 * strength;
                    else if (f <= 315) g += 0.7 * strength;
                    else if (f >= 10000) g += -0.4 * strength;
                }
                else if (tone == "vocal")
                {
                    if (f >= 800 && f <= 2500) g += 1.7 * strength;
                    else if (f >= 315 && f < 800) g += 0.7 * strength;
                    else if (f >= 5000 && f <= 8000) g += -0.8 * strength;
                    else if (f <= 80) g += -0.4 * strength;
                }
                else if (tone == "air")
                {
                    if (f >= 10000) g += 2.0 * strength;
                    else if (f >= 5000) g += 1.1 * strength;
                    else if (f <= 160) g += -0.5 * strength;
                }
                else if (tone == "warm")
                {
                    if (f <= 125) g += 1.4 * strength;
                    else if (f >= 4000) g += -0.9 * strength;
                    else if (f >= 250 && f <= 1000) g += 0.4 * strength;
                }

                g = Math.Round(Math.Clamp(g, -12, 12) * 10) / 10;
                s.Bands.Add(new EqBand { Enabled = Math.Abs(g) > 0.01, FrequencyHz = f, GainDb = g, Q = 1.0, FilterType = EqFilterType.Peaking });
            }
        }
        // ---- DSP 批 5：极客 DSP 状态 / EQ 外部曲线入口 / 简单模式 / 预设保存 / 面板构建 ----
        private bool _geekDspActive;
        private Color? _geekDspPhosphor;
        private bool _audioFxEqBuilt;
        private double _eqSimpleBass, _eqSimpleVocal, _eqSimpleAir, _eqSimpleWarm;

        /// <summary>应用 OPRA 耳机校正曲线到播放器 EQ。</summary>
        internal void ApplyOpraCurve(EqCurveState curve)
        {
            // 「耳机校正生效」由 _opraApplied 计算属性按曲线 id 直读，这里不再赋值
            ApplyEqCurveToPlayer(curve);
        }

        /// <summary>把一条曲线应用到播放器（曲线状态 + 持久化 + DSP 实时生效 + 链路显示）。
        /// OPRA 耳机校正、Equalizer APO 导入等「外部曲线入口」共用这一条路径。
        /// 2026-10-31 W13：旧面板同步块（开关/预设框/曲线重绘）随旧壳退役。</summary>
        internal void ApplyEqCurveToPlayer(EqCurveState curve)
        {
            if (curve == null)
            {
                return;
            }

            curve.Enabled = true;
            PushEqUndoSnapshot();
            EqState.Current = curve;
            EqCurveStore.Save(curve);

            _audioEngine?.SetEqCurve(curve);
            UpdateSignalChainDisplay();
            StartupLog.Write("EQ 曲线已应用: " + curve.PresetId + " bands=" + (curve.Bands?.Count ?? 0) + " preamp=" + curve.PreampDb);
        }

        /// <summary>保存 EQ 预设核心（网页「音效处理」页直接调，name 由网页输入框给）。
        /// 返回给调用方一句结果消息（空名 = 没保存）。旧面板下拉刷新随旧壳退役。</summary>
        internal async Task<string> SaveAudioFxEqPresetCoreAsync(string rawName)
        {
            string name = (rawName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                const string bad = "预设名称不能为空，未保存";
                return bad;
            }

            var toSave = EqState.Current.Clone();
            toSave.PresetName = name.Length > 40 ? name.Substring(0, 40) : name;
            toSave.PresetId = "user:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string newId = EqUserPresetStore.Upsert(toSave);

            string msg = "EQ 预设已保存：" + toSave.PresetName;
            return msg;
        }

        /// <summary>从持久化状态加载音效面板：模型装载 + 控件投影 + 就绪放行 + 导航初始化。
        /// 2026-10-31 W13：EQ 曲线编辑器/预设下拉等旧面板同步随旧壳退役，只保留模型与投影。</summary>
        private void LoadAudioFxUiFromStore()
        {
            _audioFxLoading = true;
            try
            {
                EqState.Current = EqCurveStore.Load();
                SyncAudioFxEqSimpleFromState();

                DspExtraState extra = DspExtraStore.Load();
                ExtraParameters.Load(extra);
                var ch = extra.ChannelBalance;
                ReplayGainState rglog = ReplayGainStore.Load();
                StartupLog.Write($"[DSP] 加载读盘 eq.Enabled={EqState.Current.Enabled} limiter={extra.Safety?.EnableLimiter} rg.Mode={rglog.Mode}");
                // Crossfeed 已独立成页：强度和截止的两处读数要在加载时同步一次
                // 老存档没有这个字段 → 反序列化得到默认 700，正好等于旧版写死值，行为不变

                var safety = extra.Safety;

                // ReplayGain：先装载模型，再让控件照模型投影（模型是唯一参数来源）
                RgParameters.Load(ReplayGainStore.Load());
                RefreshAudioFxRgInfo();
            }
            finally
            {
                _audioFxLoading = false;
            }

            // 面板已从盘加载完成，之后才允许 DSP handler 持久化/应用（启动阶段误触发需屏蔽）。
            _audioFxPanelReady = true;

            // 模块导航：默认定位第一个模块，并按当前状态点亮各模块圆点
            InitDspNav();
        }

        /// <summary>简单模式四滑杆 → EQ 曲线（coalesce 进撤销栈，直接下发引擎）。</summary>
        private void ApplySimpleTones()
        {
            PushEqUndoSnapshot(coalesce: true);
            var s = new EqCurveState { Enabled = EqState.Current.Enabled, PreampDb = EqState.Current.PreampDb, PresetId = "simple", PresetName = "简单模式" };
            SimpleEqStore.Save(new SimpleEqState { Bass = _eqSimpleBass, Vocal = _eqSimpleVocal, Air = _eqSimpleAir, Warm = _eqSimpleWarm });
            if (_eqSimpleBass > 0.01) AppendSimple(s, "bass", _eqSimpleBass);
            if (_eqSimpleVocal > 0.01) AppendSimple(s, "vocal", _eqSimpleVocal);
            if (_eqSimpleAir > 0.01) AppendSimple(s, "air", _eqSimpleAir);
            if (_eqSimpleWarm > 0.01) AppendSimple(s, "warm", _eqSimpleWarm);
            if (s.Bands.Count == 0) s.Bands.Add(new EqBand { Enabled = true, FrequencyHz = 1000, GainDb = 0 });

            EqState.Current = s;
            _audioFxEqSelected = EqState.Current.Bands.Count > 0 ? 0 : -1;
            ApplyDspToEngine();
        }

        // DSP 批 5 收尾：设备配置档自动套用离壳

        /// <summary>按设备记忆：若开启且当前设备有已存配置档，则套用该设备的 DSP 配置（不碰音频字节流）。</summary>
        private void ApplyDeviceDspProfileIfEnabled(string deviceId)
        {
            try
            {
                if (!DeviceDspProfileStore.IsEnabled()) return;
                if (string.IsNullOrWhiteSpace(deviceId)) return;            // 系统默认设备不按设备记忆
                if (!DeviceDspProfileStore.HasProfile(deviceId)) return;   // 无存档则用全局配置，避免被默认覆盖
                var profile = DeviceDspProfileStore.GetProfile(deviceId);
                DeviceDspProfileStore.ApplyToStores(profile);
                // 重新填充音效面板控件 + 内存 EQ 曲线（与启动加载同口径）
                LoadAudioFxUiFromStore();
                // 推送到引擎（内部已按 _audioFxPanelReady 守卫；未就绪时仅写盘不应用）
                ApplyDspToEngine();
                // 独立的 10 段均衡器与房间校正由引擎直接读 store
                _audioEngine?.SetEqualizer(EqualizerStore.Load().BandGains);
                StartupLog.Write($"[DSP] 已套用设备配置档：{deviceId}");
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }
    }
