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
        private bool _dspRackReady;

        // 压缩器 GR 轮询（仅压缩器页可见时跑）
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dspCompGrTimer;
        private bool _dspCompGrOn;

        // ─────────────────────────────────────────────────────────────
        // 加载：存档 → UI
        // ─────────────────────────────────────────────────────────────

        /// <summary>把 dsp-rack.json 的当前值灌进面板。面板未构建时不报错（控件引用为空）。</summary>
        private void LoadDspRackUi()
        {
            if (DspCompToggle == null)
            {
                return;
            }

            RackState rack = DspRackStore.Load();
            _dspRackLoading = true;
            try
            {
                var c = rack.Compressor;
                DspCompToggle.IsOn = c.Enabled;
                DspCompThresholdSlider.Value = c.ThresholdDb;
                DspCompRatioSlider.Value = c.Ratio;
                DspCompAttackSlider.Value = c.AttackMs;
                DspCompReleaseSlider.Value = c.ReleaseMs;
                DspCompKneeSlider.Value = c.KneeDb;
                DspCompMakeupSlider.Value = c.MakeupDb;
                DspCompMixSlider.Value = c.Mix * 100.0;

                var f = rack.StereoField;
                DspFieldToggle.IsOn = f.Enabled;
                DspFieldWidthSlider.Value = f.Width * 100.0;
                DspFieldCenterSlider.Value = f.CenterGainDb;
                DspFieldSideSlider.Value = f.SideGainDb;

                var m = rack.Matrix;
                DspMatrixToggle.IsOn = m.Enabled;
                DspMatrixLlSlider.Value = m.LeftToLeft;
                DspMatrixRlSlider.Value = m.RightToLeft;
                DspMatrixLrSlider.Value = m.LeftToRight;
                DspMatrixRrSlider.Value = m.RightToRight;

                RefreshDspCompReadouts();
                RefreshDspFieldReadouts();
                RefreshDspMatrixReadouts();
                RefreshDspRackList(rack.RackOrder);
                RefreshCrossfeedCutoffReadout();

                // 画布不读 ThemeResource、也不参与 XAML 布局期的自动刷新，必须显式画一次。
                // （折叠状态下 ActualWidth=0，Draw 内部会直接返回，切页时由 SelectDspPage 补画）
                RedrawDspCompTransferCurve();
                RedrawDspMatrixFlow();
            }
            finally
            {
                _dspRackLoading = false;
            }
        }

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

            rack.Compressor = new CompressorSettings
            {
                Enabled = DspCompToggle.IsOn,
                ThresholdDb = DspCompThresholdSlider.Value,
                Ratio = DspCompRatioSlider.Value,
                AttackMs = DspCompAttackSlider.Value,
                ReleaseMs = DspCompReleaseSlider.Value,
                KneeDb = DspCompKneeSlider.Value,
                MakeupDb = DspCompMakeupSlider.Value,
                Mix = DspCompMixSlider.Value / 100.0
            };

            rack.StereoField = new StereoFieldSettings
            {
                Enabled = DspFieldToggle.IsOn,
                Width = DspFieldWidthSlider.Value / 100.0,
                CenterGainDb = DspFieldCenterSlider.Value,
                SideGainDb = DspFieldSideSlider.Value
            };

            rack.Matrix = new ChannelMatrixSettings
            {
                Enabled = DspMatrixToggle.IsOn,
                LeftToLeft = DspMatrixLlSlider.Value,
                RightToLeft = DspMatrixRlSlider.Value,
                LeftToRight = DspMatrixLrSlider.Value,
                RightToRight = DspMatrixRrSlider.Value
            };

            // SetRack：Save(dsp-rack.json) + 实时下发；未播放时仅存状态，下次播放会话应用
            _audioEngine?.SetRack(rack);

            UpdateDspNavIndicators();
        }

        // ─────────────────────────────────────────────────────────────
        // 读数刷新（每个控件旁的数值文字）
        // ─────────────────────────────────────────────────────────────

        private void RefreshDspCompReadouts()
        {
            if (DspCompThresholdText == null)
            {
                return;
            }

            DspCompThresholdText.Text = FormatHelper.FormatAudioFxDb(DspCompThresholdSlider.Value);
            DspCompRatioText.Text = DspCompRatioSlider.Value.ToString("0.0") + " : 1";
            DspCompAttackText.Text = DspCompAttackSlider.Value.ToString("0.0") + " ms";
            DspCompReleaseText.Text = DspCompReleaseSlider.Value.ToString("0") + " ms";
            DspCompKneeText.Text = DspCompKneeSlider.Value.ToString("0.0") + " dB";
            DspCompMakeupText.Text = FormatHelper.FormatAudioFxDb(DspCompMakeupSlider.Value);
            DspCompMixText.Text = DspCompMixSlider.Value.ToString("0") + "%";
        }

        private void RefreshDspFieldReadouts()
        {
            if (DspFieldWidthText == null)
            {
                return;
            }

            DspFieldWidthText.Text = DspFieldWidthSlider.Value.ToString("0") + "%";
            DspFieldCenterText.Text = FormatHelper.FormatAudioFxDb(DspFieldCenterSlider.Value);
            DspFieldSideText.Text = FormatHelper.FormatAudioFxDb(DspFieldSideSlider.Value);
        }

        private void RefreshDspMatrixReadouts()
        {
            if (DspMatrixLlText == null)
            {
                return;
            }

            DspMatrixLlText.Text = DspMatrixLlSlider.Value.ToString("0.00");
            DspMatrixRlText.Text = DspMatrixRlSlider.Value.ToString("0.00");
            DspMatrixLrText.Text = DspMatrixLrSlider.Value.ToString("0.00");
            DspMatrixRrText.Text = DspMatrixRrSlider.Value.ToString("0.00");
        }

        private void RefreshCrossfeedCutoffReadout()
        {
            if (AudioFxChannelCrossfeedCutoffText == null || AudioFxChannelCrossfeedCutoffSlider == null)
            {
                return;
            }

            AudioFxChannelCrossfeedCutoffText.Text = AudioFxChannelCrossfeedCutoffSlider.Value.ToString("0") + " Hz";
        }

        // ─────────────────────────────────────────────────────────────
        // 事件：压缩器
        // ─────────────────────────────────────────────────────────────

        private void DspComp_Toggled(object sender, RoutedEventArgs e)
        {
            if (_dspRackLoading)
            {
                return;
            }

            // 关掉时曲线退化为「输出 = 输入」的斜线，打开时恢复压缩曲线 —— 必须重画
            RedrawDspCompTransferCurve();
            PushDspRackToEngine();
        }

        private void DspCompSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // 启动阶段（InitializeComponent 解析 XAML）滑杆的 Value 初值会就地触发本事件，
            // 此时同页后续控件的 x:Name 字段还没连上，直接刷读数会空引用、整个进程秒崩。
            // InitDspNav 完成前（_dspRackReady=false）一律忽略，与 ApplyDspToEngine 的
            // _audioFxPanelReady 守卫同一套路。LoadDspRackUi 期间由它自己显式刷读数，不缺这一次。
            if (!_dspRackReady)
            {
                return;
            }

            RefreshDspCompReadouts();

            // 传递曲线是参数的函数，任何一条滑杆动了都得重画（曲线的形状会跟着变）
            RedrawDspCompTransferCurve();

            if (!_dspRackLoading)
            {
                PushDspRackToEngine();
            }
        }

        /// <summary>压缩器预设：按现成口径一键铺开参数，避免用户面对七条滑杆无从下手。</summary>
        private void DspCompPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag)
            {
                return;
            }

            // (阈值, 比率, 启动, 释放, 拐点, 补偿, 干湿%)
            (double threshold, double ratio, double attack, double release, double knee, double makeup, double mix) preset =
                tag switch
                {
                    "gentle" => (-24.0, 2.0, 20.0, 200.0, 12.0, 0.0, 100.0),
                    "medium" => (-18.0, 4.0, 10.0, 120.0, 6.0, 0.0, 100.0),
                    "heavy" => (-12.0, 8.0, 3.0, 60.0, 3.0, 3.0, 100.0),
                    // 人声：比中速更柔、补偿一点点把齿音之外的部分托回来
                    "vocal" => (-20.0, 3.0, 8.0, 150.0, 8.0, 1.5, 100.0),
                    // 乐器/打击：启动快、压缩比高，压住瞬态但不拖尾
                    "drums" => (-14.0, 6.0, 2.0, 80.0, 3.0, 1.0, 100.0),
                    // 总线：几乎不压，只做兜底粘合并保留动态
                    "bus" => (-22.0, 2.5, 30.0, 300.0, 12.0, 0.0, 100.0),
                    _ => (-18.0, 4.0, 10.0, 120.0, 6.0, 0.0, 100.0)
                };

            DspCompToggle.IsOn = true;
            DspCompThresholdSlider.Value = preset.threshold;
            DspCompRatioSlider.Value = preset.ratio;
            DspCompAttackSlider.Value = preset.attack;
            DspCompReleaseSlider.Value = preset.release;
            DspCompKneeSlider.Value = preset.knee;
            DspCompMakeupSlider.Value = preset.makeup;
            DspCompMixSlider.Value = preset.mix;

            PushDspRackToEngine();

            // 预设一次改写七条滑杆，每条的 ValueChanged 都会触发一次重画；
            // 这里最后统一再画一次即可（Redraw 是全清重建，重复调用无害）。
            RedrawDspCompTransferCurve();
        }

        // 压缩器每拍刷新（仅本页可见时轮询）：压缩量读数 + 8 秒折线 + 入口电平。
        // 绘制全部交给 MainWindow.DspComp.cs，这里只负责把内核读数取出来递过去。
        private void DspCompGr_Tick(object sender, object e)
        {
            UpdateDspCompMeters(_audioEngine?.CompressorGainReductionDb ?? 0f);
        }

        private void EnsureDspCompGrTimer(bool enable)
        {
            if (enable)
            {
                if (!_dspCompGrOn)
                {
                    _dspCompGrOn = true;
                    if (_dspCompGrTimer == null)
                    {
                        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                        _dspCompGrTimer = queue.CreateTimer();
                        _dspCompGrTimer.Interval = TimeSpan.FromMilliseconds(200);
                        _dspCompGrTimer.Tick += DspCompGr_Tick;
                    }

                    _dspCompGrTimer.Start();
                }
            }
            else if (_dspCompGrOn)
            {
                _dspCompGrOn = false;
                _dspCompGrTimer?.Stop();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 事件：立体声场
        // ─────────────────────────────────────────────────────────────

        private void DspField_Toggled(object sender, RoutedEventArgs e)
        {
            if (_dspRackLoading)
            {
                return;
            }

            PushDspRackToEngine();
        }

        private void DspFieldSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // 说明同 DspCompSlider_ValueChanged：解析期初值就地触发，后续控件字段未连接前必须忽略。
            if (!_dspRackReady)
            {
                return;
            }

            RefreshDspFieldReadouts();
            if (!_dspRackLoading)
            {
                PushDspRackToEngine();
            }
        }

        private void DspFieldPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag)
            {
                return;
            }

            // (宽度%, 中间 dB, 两侧 dB)
            (double width, double center, double side) preset = tag switch
            {
                "wide" => (130.0, 0.0, 0.0),
                "vocal" => (100.0, 3.0, 0.0),
                "mono" => (0.0, 0.0, 0.0),
                _ => (100.0, 0.0, 0.0)
            };

            DspFieldToggle.IsOn = true;
            DspFieldWidthSlider.Value = preset.width;
            DspFieldCenterSlider.Value = preset.center;
            DspFieldSideSlider.Value = preset.side;

            PushDspRackToEngine();
        }

        // ─────────────────────────────────────────────────────────────
        // 事件：声道矩阵
        // ─────────────────────────────────────────────────────────────

        private void DspMatrix_Toggled(object sender, RoutedEventArgs e)
        {
            if (_dspRackLoading)
            {
                return;
            }

            // 关掉时流图退化为「旁路的样子」（左→左、右→右 直通），要同步画出来
            RedrawDspMatrixFlow();
            PushDspRackToEngine();
        }

        private void DspMatrixSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // 说明同 DspCompSlider_ValueChanged：解析期初值就地触发，后续控件字段未连接前必须忽略。
            if (!_dspRackReady)
            {
                return;
            }

            RefreshDspMatrixReadouts();

            // 矩阵流图跟着四个系数走（画图内部有 null 守卫，面板未构建时安全返回）
            RedrawDspMatrixFlow();

            if (!_dspRackLoading)
            {
                PushDspRackToEngine();
            }
        }

        private void DspMatrixPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag)
            {
                return;
            }

            // (左→左, 右→左, 左→右, 右→右)
            (double ll, double rl, double lr, double rr) preset = tag switch
            {
                "swap" => (0.0, 1.0, 1.0, 0.0),
                "mono" => (0.5, 0.5, 0.5, 0.5),
                "karaoke" => (1.0, -1.0, -1.0, 1.0),
                // 加宽：把对侧以反相、小比例混回来，抵消一部分左右共同的成分（≈ 削弱中间、放大两侧）。
                // 系数刻意压在 1.25 以内，超过就容易让中间塌陷、人声失真。
                "widen" => (1.25, -0.25, -0.25, 1.25),
                "leftonly" => (1.0, 0.0, 0.0, 0.0),
                "rightonly" => (0.0, 0.0, 0.0, 1.0),
                _ => (1.0, 0.0, 0.0, 1.0)
            };

            DspMatrixToggle.IsOn = true;
            DspMatrixLlSlider.Value = preset.ll;
            DspMatrixRlSlider.Value = preset.rl;
            DspMatrixLrSlider.Value = preset.lr;
            DspMatrixRrSlider.Value = preset.rr;

            PushDspRackToEngine();

            // 流图会跟着四个系数变，没必要等滑杆各自的 ValueChanged 慢慢重画
            RedrawDspMatrixFlow();
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

        /// <summary>行状态点颜色：on=面板强调色（极客磷光/经典 accent）；已旁路=琥珀（语义色，
        /// 不主题化，与模块页 Hero 徽章口径一致）；off=灰（极客下换暗灰，原 #B4B2A9 在深底太扎眼）。</summary>
        private Brush DspRackStateBrush(DspRackState state)
        {
            if (state == DspRackState.On)
            {
                return DspAccentBrush();
            }

            if (state == DspRackState.Bypassed)
            {
                return new SolidColorBrush(Color.FromArgb(255, 0xC0, 0x7A, 0x1A));
            }

            return new SolidColorBrush(_geekDspActive
                ? Color.FromArgb(255, 0x4A, 0x4A, 0x42)
                : Color.FromArgb(255, 0xB4, 0xB2, 0xA9));
        }

        private List<DspRackRow> _dspRackRows = new();

        /// <summary>当前是否被 DSP 总旁路（总旁路时机架行一律显示"已旁路"）。</summary>
        private bool DspRackGlobalBypassed => DspBypassToggle != null && DspBypassToggle.IsOn;

        // ─────────────────────────────────────────────────────────────
        // 事件：机架编排
        // ─────────────────────────────────────────────────────────────

        /// <summary>当前在编辑的机架顺序（模块 ID）。始终与盘上值一致 —— 每次操作立即下发。</summary>
        private int[]? _dspRackOrderDraft;

        private void RefreshDspRackList(int[] order)
        {
            if (DspRackList == null)
            {
                return;
            }

            _dspRackOrderDraft = (int[])order.Clone();
            RenderDspRackList();
        }

        /// <summary>
        /// 渲染链路全景：只列 8 个可排序的内核模块（草稿顺序）。
        /// SRC / 耳机校正 / 输出安全是链路结构决定的上下游，不在机架里、不参与排序——
        /// 它们各自在「采样率」「耳机校正」「输出安全」页面上呈现。
        /// 状态文字/颜色按此刻各模块真实生效情况生成。
        /// </summary>
        private void RenderDspRackList()
        {
            if (DspRackList == null || _dspRackOrderDraft == null)
            {
                return;
            }

            bool bypassed = DspRackGlobalBypassed;
            List<DspRackRow> rows = new();

            // 8 个内核模块：当前草稿顺序（可上下移）
            for (int i = 0; i < _dspRackOrderDraft.Length && i < RackModuleNames.Length; i++)
            {
                int id = _dspRackOrderDraft[i];
                DspRackState state = RackModuleActive(id)
                    ? (bypassed ? DspRackState.Bypassed : DspRackState.On)
                    : DspRackState.Off;
                rows.Add(new DspRackRow
                {
                    SlotLabel = (i + 1) + ".",
                    Name = RackModuleNames[id],
                    Hint = RackModuleHints[id],
                    StateText = state switch
                    {
                        DspRackState.On => "生效中",
                        DspRackState.Bypassed => "已旁路",
                        _ => "未启用"
                    },
                    StateBrush = DspRackStateBrush(state),
                    Movable = true,
                    ModuleId = id
                });
            }

            _dspRackRows = rows;

            int restore = DspRackList.SelectedIndex;
            DspRackList.ItemsSource = null;
            DspRackList.ItemsSource = rows;
            if (restore >= 0 && restore < rows.Count)
            {
                DspRackList.SelectedIndex = restore;
            }

            ShowDspRackHint();
        }

        /// <summary>机架 8 模块此刻是否真正参与处理（供行状态；全局旁路由调用方叠加 Bypassed）。</summary>
        private bool RackModuleActive(int moduleId)
        {
            switch (moduleId)
            {
                case DspCoreInterop.RackEqualizer:
                    return _audioFxEq != null && _audioFxEq.Enabled && _audioFxEq.HasEffect();
                case DspCoreInterop.RackConvolution:
                    return RoomCorrectionStore.Load().Enabled;
                case DspCoreInterop.RackReplayGain:
                    return ReplayGainStore.Load().Mode != ReplayGainMode.Off;
                case DspCoreInterop.RackCompressor:
                    return DspCompActive();
                case DspCoreInterop.RackCrossfeed:
                    return AudioFxChannelCrossfeedToggle != null && AudioFxChannelCrossfeedToggle.IsOn
                        && (AudioFxChannelCrossfeedSlider?.Value ?? 0) > 0.1;
                case DspCoreInterop.RackStereoField:
                    return DspFieldActive();
                case DspCoreInterop.RackChannelMatrix:
                    return DspMatrixActive();
                case DspCoreInterop.RackChannelBalance:
                    return ChannelBalanceActive();
                default:
                    return false;
            }
        }

        /// <summary>声道平衡（机架最后一项）是否产生实际处理：任一非默认值即算。</summary>
        private bool ChannelBalanceActive()
        {
            if (AudioFxChannelToggle == null || !AudioFxChannelToggle.IsOn)
            {
                return false;
            }

            double balance = AudioFxChannelBalanceSlider?.Value ?? 0;
            double lg = AudioFxChannelLeftGainSlider?.Value ?? 0;
            double rg = AudioFxChannelRightGainSlider?.Value ?? 0;
            double ld = AudioFxChannelLeftDelaySlider?.Value ?? 0;
            double rd = AudioFxChannelRightDelaySlider?.Value ?? 0;
            return Math.Abs(balance) > 0.005
                || Math.Abs(lg) > 0.05
                || Math.Abs(rg) > 0.05
                || Math.Abs(ld) > 0.05
                || Math.Abs(rd) > 0.05
                || (AudioFxChannelSwapToggle?.IsOn ?? false)
                || (AudioFxChannelInvertLToggle?.IsOn ?? false)
                || (AudioFxChannelInvertRToggle?.IsOn ?? false)
                || (AudioFxChannelMonoCombo?.SelectedIndex ?? 0) > 0;
        }

        /// <summary>仅重刷行状态（不动顺序）：导航指示刷新 / 皮肤切换时调用。</summary>
        private void RefreshDspRackRows()
        {
            if (DspRackList == null || _dspRackOrderDraft == null)
            {
                return;
            }

            RenderDspRackList();
        }

        private void ShowDspRackHint()
        {
            if (DspRackHintText == null)
            {
                return;
            }

            int index = DspRackList?.SelectedIndex ?? -1;
            if (index >= 0 && _dspRackRows.Count > 0 && index < _dspRackRows.Count)
            {
                DspRackHintText.Text = _dspRackRows[index].Name + "：" + _dspRackRows[index].Hint;
            }
            else
            {
                DspRackHintText.Text = "点选任意一行查看它的作用说明。";
            }

            UpdateDspRackMoveButtons();
        }

        private void DspRackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowDspRackHint();
        }

        /// <summary>上移/下移只在选中可调序行时可用（机架 8 行全部可调）。</summary>
        private void UpdateDspRackMoveButtons()
        {
            if (DspRackUpButton == null || DspRackDownButton == null)
            {
                return;
            }

            int index = DspRackList?.SelectedIndex ?? -1;
            bool movable = index >= 0 && index < _dspRackRows.Count && _dspRackRows[index].Movable;
            DspRackUpButton.IsEnabled = movable && index > 0 && _dspRackRows[index - 1].Movable;
            DspRackDownButton.IsEnabled = movable && index < _dspRackRows.Count - 1 && _dspRackRows[index + 1].Movable;
        }

        private void DspRackUp_Click(object sender, RoutedEventArgs e)
        {
            MoveDspRackEntry(-1);
        }

        private void DspRackDown_Click(object sender, RoutedEventArgs e)
        {
            MoveDspRackEntry(1);
        }

        /// <summary>把选中模块上下移动一格（delta=-1 上移 / +1 下移），立即下发内核。
        /// 机架里只有 8 个可排序模块，没有固定行。</summary>
        private void MoveDspRackEntry(int delta)
        {
            if (DspRackList == null || _dspRackOrderDraft == null)
            {
                return;
            }

            int i = DspRackList.SelectedIndex;
            // 行下标即机架下标：0..7 一一对应 8 个内核模块
            if (i < 0 || i >= _dspRackOrderDraft.Length)
            {
                return;
            }

            int rackIndex = i;
            int j = rackIndex + delta;
            if (rackIndex < 0 || rackIndex >= _dspRackOrderDraft.Length || j < 0 || j >= _dspRackOrderDraft.Length)
            {
                return;
            }

            (_dspRackOrderDraft[rackIndex], _dspRackOrderDraft[j]) = (_dspRackOrderDraft[j], _dspRackOrderDraft[rackIndex]);
            CommitDspRackOrder();
        }

        private void DspRackReset_Click(object sender, RoutedEventArgs e)
        {
            // 直接恢复 ECHO 默认顺序并下发；CommitDspRackOrder 内部会 load→save→重绘
            _dspRackOrderDraft = DefaultDspRackOrder();
            CommitDspRackOrder();
        }

        private static int[] DefaultDspRackOrder() => new[]
        {
            DspCoreInterop.RackEqualizer,
            DspCoreInterop.RackConvolution,
            DspCoreInterop.RackReplayGain,
            DspCoreInterop.RackCompressor,
            DspCoreInterop.RackCrossfeed,
            DspCoreInterop.RackStereoField,
            DspCoreInterop.RackChannelMatrix,
            DspCoreInterop.RackChannelBalance
        };

        /// <summary>把当前草稿顺序写回 store 并下发内核。顺序合法（0..7 的排列）由
        /// RackState.Normalize 兜底；内核若拒绝会自行回退默认并记日志。</summary>
        private void CommitDspRackOrder()
        {
            if (_dspRackOrderDraft == null)
            {
                return;
            }

            RackState rack = DspRackStore.Load();
            rack.RackOrder = (int[])_dspRackOrderDraft.Clone();
            _audioEngine?.SetRack(rack);
            RenderDspRackList();
            UpdateDspNavIndicators();
        }

        // ─────────────────────────────────────────────────────────────
        // 新模块的"是否生效"判定（供导航圆点 / 页徽章）
        // 口径沿用既有约定：并非"开关打开"就算，而是真的对声音产生影响才算。
        // ─────────────────────────────────────────────────────────────

        private bool DspCompActive()
        {
            if (DspCompToggle == null)
            {
                return false;
            }

            return DspCompToggle.IsOn && DspCompMixSlider.Value > 0.1;
        }

        private bool DspFieldActive()
        {
            if (DspFieldToggle == null)
            {
                return false;
            }

            return DspFieldToggle.IsOn
                && (Math.Abs(DspFieldWidthSlider.Value - 100.0) > 0.5
                    || Math.Abs(DspFieldCenterSlider.Value) > 0.05
                    || Math.Abs(DspFieldSideSlider.Value) > 0.05);
        }

        private bool DspMatrixActive()
        {
            if (DspMatrixToggle == null)
            {
                return false;
            }

            return DspMatrixToggle.IsOn
                && (Math.Abs(DspMatrixLlSlider.Value - 1.0) > 0.005
                    || Math.Abs(DspMatrixRlSlider.Value) > 0.005
                    || Math.Abs(DspMatrixLrSlider.Value) > 0.005
                    || Math.Abs(DspMatrixRrSlider.Value - 1.0) > 0.005);
        }

        /// <summary>机架页始终视为"在看"，不点亮"生效"（顺序不是效果开关）。</summary>
        private bool DspRackOrderIsDefault()
        {
            if (_dspRackOrderDraft == null)
            {
                return true;
            }

            int[] dflt = DefaultDspRackOrder();
            for (int i = 0; i < dflt.Length && i < _dspRackOrderDraft.Length; i++)
            {
                if (_dspRackOrderDraft[i] != dflt[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// 机架编排页的一行（链路全景）：8 个内核模块按机架顺序排列，全部可上下移。
    /// SRC / 耳机校正 / 输出安全是链路结构决定的上下游，不占机架槽位。
    /// 状态色在生成行时按当前皮肤算好（极客=磷光，经典=主题 accent），
    /// 皮肤切换时整表重建即可换色（ApplyGeekDsp 收尾调用）。
    /// 顶层类而非 MainWindow 嵌套类——XAML x:Bind 的 x:DataType 只可靠解析顶层类型，
    /// 与项目里 PlaylistItem / AlbumEntry / TagSortCategoryEntry 等行模型的既有做法一致。
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
