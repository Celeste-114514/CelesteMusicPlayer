using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DSP 各模块页头部的「电源开关」（2026-09-30 用户拍板：原来介绍文字右侧的椭圆徽章改成开关）。
    ///
    /// 设计口径：
    ///  · 开关语义 = 该模块此刻是否参与处理，与左侧导航圆点同源（<see cref="DspModuleActive"/>），
    ///    但圆点要求「真的对声音产生影响」，开关只表达「模块电源」——例如 EQ 开着但曲线是平的，
    ///    开关亮、圆点不亮，这是有意的分工（圆点管听感，开关管配置）。
    ///  · 全局旁路（DspBypassToggle）时开关保持原样，只在旁边显示「已旁路」提示——
    ///    旁路是临时 A/B 状态，不改动各模块自己的开关。
    ///  · 没有电源可表达的页面（听音方案 / 机架编排 / 输出安全）保留原来的信息徽章，不硬凑开关。
    ///
    /// ⚠ 秒崩铁律（本项目反复踩过）：XAML 解析期会就地触发 Toggled/ValueChanged，此时同页
    /// 后续控件的 x:Name 字段还没连上。因此本文件的开关一律不在 XAML 里写 IsOn 初值，
    /// handler 顶部再加 <see cref="_dspPowerReady"/> 守卫；就绪前的同步由
    /// <see cref="SyncDspPowerSwitches"/> 在 InitDspNav 里显式做一次。
    /// </summary>
    public sealed partial class MainWindow
    {
        private readonly List<DspPowerEntry> _dspPowerEntries = new();
        private bool _dspPowerReady;
        private bool _dspPowerSyncing;

        // 各「开关关掉再打开」要恢复的上次值（0/关闭 = 电源断开，记忆的是断开前的档位）
        private double _dspHeadroomMemory = -3.0;
        private int _dspSrcMemoryIndex = 4;   // 96 kHz
        private int _dspRgMemoryIndex = 1;    // 单曲 (Track)
        private double _dspXfeedMemory = 25.0; // 耳机 Crossfeed 的强度百分比

        private sealed class DspPowerEntry
        {
            public required ToggleSwitch Switch { get; init; }
            public TextBlock? Hint { get; init; }

            /// <summary>模块当前是否通电（同步开关状态用）。</summary>
            public required Func<bool> IsOn { get; init; }

            /// <summary>用户拨开关：把模块切到目标通电状态。</summary>
            public required Action<bool> Apply { get; init; }

            /// <summary>返回非 null 时开关禁用，并把该文本显示在提示位（如「先导入 IR」）。</summary>
            public Func<string?>? DisabledHint { get; init; }
        }

        /// <summary>构建页头电源开关表。InitDspNav 里调用一次（此刻 x:Name 字段已连上）。</summary>
        private void InitDspPowerEntries()
        {
            _dspPowerEntries.Clear();

            // ① 输入余量：开关 = 预衰减是否生效（滑杆 0 dB = 不通电）；记忆断开前的衰减量
            AddPower(DspPowerHeadroom, DspPowerHeadroomHint,
                () => AudioFxSafetyHeadroomSlider != null
                    && Math.Abs(AudioFxSafetyHeadroomSlider.Value) > 0.01,
                on =>
                {
                    if (AudioFxSafetyHeadroomSlider == null)
                    {
                        return;
                    }

                    if (on)
                    {
                        double remember = Math.Abs(_dspHeadroomMemory) > 0.01 ? _dspHeadroomMemory : -3.0;
                        AudioFxSafetyHeadroomSlider.Value = Math.Clamp(
                            remember, AudioFxSafetyHeadroomSlider.Minimum, AudioFxSafetyHeadroomSlider.Maximum);
                    }
                    else
                    {
                        if (Math.Abs(AudioFxSafetyHeadroomSlider.Value) > 0.01)
                        {
                            _dspHeadroomMemory = AudioFxSafetyHeadroomSlider.Value;
                        }

                        AudioFxSafetyHeadroomSlider.Value = 0;
                    }
                });

            // ② ReplayGain：开关 = 模式不是「关闭」；记忆上次的单曲/专辑模式
            AddPower(DspPowerRg, DspPowerRgHint,
                () => AudioFxRgModeCombo != null && AudioFxRgModeCombo.SelectedIndex > 0,
                on =>
                {
                    if (AudioFxRgModeCombo == null)
                    {
                        return;
                    }

                    if (on)
                    {
                        int remember = _dspRgMemoryIndex >= 1 && _dspRgMemoryIndex < AudioFxRgModeCombo.Items.Count
                            ? _dspRgMemoryIndex
                            : 1;
                        AudioFxRgModeCombo.SelectedIndex = remember;
                    }
                    else
                    {
                        if (AudioFxRgModeCombo.SelectedIndex > 0)
                        {
                            _dspRgMemoryIndex = AudioFxRgModeCombo.SelectedIndex;
                        }

                        AudioFxRgModeCombo.SelectedIndex = 0;
                    }
                });

            // ③ SRC 升频：开关 = 目标采样率不是「关闭（原采样率）」；记忆上次目标
            AddPower(DspPowerSrc, DspPowerSrcHint,
                () => SrcRateCombo != null && SrcRateCombo.SelectedIndex > 0
                    && SrcRateCombo.SelectedIndex < SrcRateOptions.Length,
                on =>
                {
                    if (SrcRateCombo == null)
                    {
                        return;
                    }

                    if (on)
                    {
                        int remember = _dspSrcMemoryIndex >= 1 && _dspSrcMemoryIndex < SrcRateOptions.Length
                            ? _dspSrcMemoryIndex
                            : 4;
                        SrcRateCombo.SelectedIndex = remember;
                    }
                    else
                    {
                        if (SrcRateCombo.SelectedIndex > 0)
                        {
                            _dspSrcMemoryIndex = SrcRateCombo.SelectedIndex;
                        }

                        SrcRateCombo.SelectedIndex = 0;
                    }
                });

            // ④ 参数 EQ：直接镜像页内 EQ 总开关（它自己会 ApplyDspToEngine + 刷新指示）
            AddPower(DspPowerEq, DspPowerEqHint,
                () => AudioFxEqEnableToggle != null && AudioFxEqEnableToggle.IsOn,
                on =>
                {
                    if (AudioFxEqEnableToggle != null)
                    {
                        AudioFxEqEnableToggle.IsOn = on;
                    }
                });

            // ⑤ 耳机校正：校正曲线寄存在 EQ 里，开关 = 校正已应用且 EQ 通电；
            //    还没应用过曲线时禁用，提示先去右侧选一条应用
            AddPower(DspPowerOpra, DspPowerOpraHint,
                () => _opraApplied && AudioFxEqEnableToggle != null && AudioFxEqEnableToggle.IsOn,
                on =>
                {
                    if (AudioFxEqEnableToggle != null)
                    {
                        AudioFxEqEnableToggle.IsOn = on;
                    }
                },
                () => _opraApplied ? null : "先应用一条校正曲线");

            // ⑥ 动态压缩器：镜像页内模块开关
            AddPower(DspPowerComp, DspPowerCompHint,
                () => DspCompToggle != null && DspCompToggle.IsOn,
                on =>
                {
                    if (DspCompToggle != null)
                    {
                        DspCompToggle.IsOn = on;
                    }
                });

            // ⑦ 耳机 Crossfeed：它是「声道平衡」模块的子能力 —— ManagedDspSourceProvider.UpdateChannel
            //    只在 balance 总开关打开时才把 crossfeed 下发到内核。所以在电源开关上打开它时必须
            //    连带把「声道工具」的总开关也打开，否则用户点了开关没有任何反应。
            //    关掉时只关自己，不去动总开关（总开关还管着平衡/增益/延迟等一堆东西）。
            AddPower(DspPowerXfeed, DspPowerXfeedHint,
                () => AudioFxChannelToggle != null && AudioFxChannelToggle.IsOn
                    && AudioFxChannelCrossfeedToggle != null && AudioFxChannelCrossfeedToggle.IsOn,
                on =>
                {
                    if (AudioFxChannelCrossfeedToggle == null)
                    {
                        return;
                    }

                    Slider? level = AudioFxChannelCrossfeedSlider;

                    if (on)
                    {
                        if (AudioFxChannelToggle != null && !AudioFxChannelToggle.IsOn)
                        {
                            AudioFxChannelToggle.IsOn = true;
                        }

                        // 强度为 0 时就算开了也不会有任何变化，拉回上次的档位（没记录就用 25%）
                        if (level != null && level.Value < 0.1)
                        {
                            level.Value = Math.Clamp(
                                _dspXfeedMemory > 0.1 ? _dspXfeedMemory : 25.0,
                                level.Minimum, level.Maximum);
                        }

                        AudioFxChannelCrossfeedToggle.IsOn = true;
                    }
                    else
                    {
                        if (level != null && level.Value > 0.1)
                        {
                            _dspXfeedMemory = level.Value;
                        }

                        AudioFxChannelCrossfeedToggle.IsOn = false;
                    }
                },
                () => AudioFxChannelToggle != null && !AudioFxChannelToggle.IsOn
                    ? "先打开本页顶部的「声道工具」总开关"
                    : null);

            // ⑧ 声道工具 / ⑨ 立体声场 / ⑩ 声道矩阵：镜像页内模块开关
            AddPower(DspPowerChannel, DspPowerChannelHint,
                () => AudioFxChannelToggle != null && AudioFxChannelToggle.IsOn,
                on =>
                {
                    if (AudioFxChannelToggle != null)
                    {
                        AudioFxChannelToggle.IsOn = on;
                    }
                });

            AddPower(DspPowerField, DspPowerFieldHint,
                () => DspFieldToggle != null && DspFieldToggle.IsOn,
                on =>
                {
                    if (DspFieldToggle != null)
                    {
                        DspFieldToggle.IsOn = on;
                    }
                });

            AddPower(DspPowerMatrix, DspPowerMatrixHint,
                () => DspMatrixToggle != null && DspMatrixToggle.IsOn,
                on =>
                {
                    if (DspMatrixToggle != null)
                    {
                        DspMatrixToggle.IsOn = on;
                    }
                });

            // ⑪ FIR / 房间校正：开关 = 卷积启用且已导入 IR；没导 IR 时禁用
            AddPower(DspPowerFir, DspPowerFirHint,
                () =>
                {
                    RoomCorrectionState st = RoomCorrectionStore.Load();
                    return st.Enabled && !string.IsNullOrWhiteSpace(st.IrPath);
                },
                on =>
                {
                    RoomCorrectionState st = RoomCorrectionStore.Load();
                    if (string.IsNullOrWhiteSpace(st.IrPath))
                    {
                        return;
                    }

                    st.Enabled = on;
                    RoomCorrectionStore.Save(st);
                    _audioEngine?.SetRoomCorrection(st);
                    RefreshRoomCorrectionInfo();
                },
                () =>
                {
                    RoomCorrectionState st = RoomCorrectionStore.Load();
                    return string.IsNullOrWhiteSpace(st.IrPath) ? "先导入脉冲响应 (IR)" : null;
                });
        }

        private void AddPower(ToggleSwitch? sw, TextBlock? hint, Func<bool> isOn, Action<bool> apply, Func<string?>? disabledHint = null)
        {
            if (sw == null)
            {
                return;
            }

            _dspPowerEntries.Add(new DspPowerEntry
            {
                Switch = sw,
                Hint = hint,
                IsOn = isOn,
                Apply = apply,
                DisabledHint = disabledHint
            });
        }

        /// <summary>所有页头电源开关共用一个 Toggled handler（按 sender 反查条目）。</summary>
        private void DspPowerSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            // 解析期就地触发 & 同步回写期间的守卫：这两种情况都不是用户操作，直接忽略
            if (!_dspPowerReady || _dspPowerSyncing)
            {
                return;
            }

            if (sender is not ToggleSwitch sw)
            {
                return;
            }

            DspPowerEntry? entry = _dspPowerEntries.Find(x => ReferenceEquals(x.Switch, sw));
            if (entry == null)
            {
                return;
            }

            entry.Apply(sw.IsOn);

            // 各模块自己的应用路径大多已刷新指示；这里统一再刷一次保证圆点/行状态/链路同步
            UpdateDspNavIndicators();
            UpdateDspBitPerfectUi();
        }

        /// <summary>
        /// 把开关状态同步成各模块真实状态（导航指示刷新的末端一步）。
        /// 全程 <see cref="_dspPowerSyncing"/> 守卫，避免回写 IsOn 时二次触发 handler 形成回路。
        /// </summary>
        private void SyncDspPowerSwitches()
        {
            if (_dspPowerEntries.Count == 0)
            {
                return;
            }

            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;
            _dspPowerSyncing = true;
            try
            {
                foreach (DspPowerEntry entry in _dspPowerEntries)
                {
                    string? disabledHint = entry.DisabledHint?.Invoke();
                    if (disabledHint != null)
                    {
                        entry.Switch.IsEnabled = false;
                    }
                    else
                    {
                        entry.Switch.IsEnabled = true;
                    }

                    entry.Switch.IsOn = entry.IsOn();

                    if (entry.Hint != null)
                    {
                        if (disabledHint != null)
                        {
                            entry.Hint.Text = disabledHint;
                            entry.Hint.Visibility = Visibility.Visible;
                        }
                        else if (bypass)
                        {
                            entry.Hint.Text = "已旁路";
                            entry.Hint.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            entry.Hint.Visibility = Visibility.Collapsed;
                        }
                    }
                }

                // 顺带维护「断开前的档位」记忆
                if (AudioFxSafetyHeadroomSlider != null && Math.Abs(AudioFxSafetyHeadroomSlider.Value) > 0.01)
                {
                    _dspHeadroomMemory = AudioFxSafetyHeadroomSlider.Value;
                }

                if (SrcRateCombo != null && SrcRateCombo.SelectedIndex > 0)
                {
                    _dspSrcMemoryIndex = SrcRateCombo.SelectedIndex;
                }

                if (AudioFxRgModeCombo != null && AudioFxRgModeCombo.SelectedIndex > 0)
                {
                    _dspRgMemoryIndex = AudioFxRgModeCombo.SelectedIndex;
                }
            }
            finally
            {
                _dspPowerSyncing = false;
            }
        }
    }
}
