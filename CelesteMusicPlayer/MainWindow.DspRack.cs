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

            PushDspRackToEngine();
        }

        private void DspCompSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            RefreshDspCompReadouts();
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
        }

        // 压缩器 GR 实时读数（仅本页可见时轮询）
        private void DspCompGr_Tick(object sender, object e)
        {
            float gr = _audioEngine?.CompressorGainReductionDb ?? 0f;
            if (DspCompGrText != null)
            {
                DspCompGrText.Text = gr.ToString("0.0") + " dB";
            }

            if (DspCompGrBar != null)
            {
                DspCompGrBar.Value = gr;
            }
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

            PushDspRackToEngine();
        }

        private void DspMatrixSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            RefreshDspMatrixReadouts();
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
                _ => (1.0, 0.0, 0.0, 1.0)
            };

            DspMatrixToggle.IsOn = true;
            DspMatrixLlSlider.Value = preset.ll;
            DspMatrixRlSlider.Value = preset.rl;
            DspMatrixLrSlider.Value = preset.lr;
            DspMatrixRrSlider.Value = preset.rr;

            PushDspRackToEngine();
        }

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

        private void RenderDspRackList()
        {
            if (DspRackList == null || _dspRackOrderDraft == null)
            {
                return;
            }

            List<string> items = new();
            for (int i = 0; i < _dspRackOrderDraft.Length && i < RackModuleNames.Length; i++)
            {
                int id = _dspRackOrderDraft[i];
                items.Add((i + 1) + ".  " + RackModuleNames[id]);
            }

            int restore = DspRackList.SelectedIndex;
            DspRackList.ItemsSource = items;
            if (restore >= 0 && restore < items.Count)
            {
                DspRackList.SelectedIndex = restore;
            }

            ShowDspRackHint();
        }

        private void ShowDspRackHint()
        {
            if (DspRackHintText == null)
            {
                return;
            }

            int index = DspRackList?.SelectedIndex ?? -1;
            if (index >= 0 && _dspRackOrderDraft != null && index < _dspRackOrderDraft.Length)
            {
                int id = _dspRackOrderDraft[index];
                DspRackHintText.Text = RackModuleHints[id];
            }
            else
            {
                DspRackHintText.Text = "先选中一个模块，这里会显示它的作用说明。";
            }
        }

        private void DspRackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowDspRackHint();
        }

        private void DspRackUp_Click(object sender, RoutedEventArgs e)
        {
            MoveDspRackEntry(-1);
        }

        private void DspRackDown_Click(object sender, RoutedEventArgs e)
        {
            MoveDspRackEntry(1);
        }

        /// <summary>把选中模块上下移动一格（delta=-1 上移 / +1 下移），立即下发内核。</summary>
        private void MoveDspRackEntry(int delta)
        {
            if (DspRackList == null || _dspRackOrderDraft == null)
            {
                return;
            }

            int i = DspRackList.SelectedIndex;
            int j = i + delta;
            if (i < 0 || i >= _dspRackOrderDraft.Length || j < 0 || j >= _dspRackOrderDraft.Length)
            {
                return;
            }

            (_dspRackOrderDraft[i], _dspRackOrderDraft[j]) = (_dspRackOrderDraft[j], _dspRackOrderDraft[i]);
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
}
