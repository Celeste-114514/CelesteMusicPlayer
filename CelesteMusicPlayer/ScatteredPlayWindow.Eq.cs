using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 零散播放窗口的「十段均衡器」（2026-10-18 用户拍板改版；原「7 预设 + 低/中/高音
    /// 三滑条」版废除）：
    /// 10 段（31/62/125/250/500/1K/2K/4K/8K/16K）各一根滑条，±15dB 独立调节，
    /// 读写与主程序「均衡器」同一条 equalizer.json（<see cref="EqualizerStore"/>）。
    ///
    /// 拖某段：只改这一段，即时下发引擎 + 防抖落盘（主程序重开均衡器看到的就是它）；
    /// 「全部归零」：十段一起回 0dB。手动改过 = 自定义（Preset 标签落 Flat，
    /// 与主程序拖滑条同口径）。
    /// 31 段精调 / 压缩 / 声场等不给编辑入口 —— 那些在主程序「音频设置」里调，
    /// 引擎构造时自己从盘上读，零散窗口自动生效。
    /// </summary>
    public sealed partial class ScatteredPlayWindow
    {
        private EqualizerState _eqState = new();

        /// <summary>XAML 初值/回填滑条期间不响应用户改动（防 LoadEqFromStore 反触发写盘）。</summary>
        private bool _eqUiReady;

        /// <summary>拖动滑条后防抖落盘（600ms）；关窗/关面板时无条件 flush。</summary>
        private DispatcherQueueTimer? _eqSaveTimer;
        private bool _eqSavePending;

        // =====================================================================
        // 按段号取控件（XAML 命名：EqBand0Slider..EqBand9Slider / EqBand0Value..EqBand9Value）
        // =====================================================================

        private Slider EqBandSlider(int band) => band switch
        {
            0 => EqBand0Slider,
            1 => EqBand1Slider,
            2 => EqBand2Slider,
            3 => EqBand3Slider,
            4 => EqBand4Slider,
            5 => EqBand5Slider,
            6 => EqBand6Slider,
            7 => EqBand7Slider,
            8 => EqBand8Slider,
            9 => EqBand9Slider,
            _ => EqBand0Slider
        };

        private TextBlock EqBandValueText(int band) => band switch
        {
            0 => EqBand0Value,
            1 => EqBand1Value,
            2 => EqBand2Value,
            3 => EqBand3Value,
            4 => EqBand4Value,
            5 => EqBand5Value,
            6 => EqBand6Value,
            7 => EqBand7Value,
            8 => EqBand8Value,
            9 => EqBand9Value,
            _ => EqBand0Value
        };

        // =====================================================================
        // 读取 / 回显
        // =====================================================================

        /// <summary>从与主程序同一条 store 读 EQ 初值，回填十根滑条。</summary>
        private void LoadEqFromStore()
        {
            try
            {
                _eqState = EqualizerStore.Load();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.LoadEqFromStore", caught);
                _eqState = new EqualizerState();
            }

            SyncEqSlidersFromState();
        }

        /// <summary>回填十根滑条与数值文本（期间屏蔽 ValueChanged，防止反写）。</summary>
        private void SyncEqSlidersFromState()
        {
            _eqUiReady = false;
            try
            {
                for (int band = 0; band < EqualizerState.BandCount; band++)
                {
                    double value = _eqState.BandGains[band];
                    EqBandSlider(band).Value = value;
                    EqBandValueText(band).Text = FormatEqGain(value);
                }
            }
            finally
            {
                _eqUiReady = true;
            }
        }

        // =====================================================================
        // 事件
        // =====================================================================

        private void EqButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                if (_eqPanelOpen)
                {
                    CloseEqPanel();
                    return;
                }

                // 先落盘待保存改动，再从盘上拉最新（主程序可能刚改过 equalizer.json）
                SaveEqNow();
                LoadEqFromStore();

                // 互斥：队列面板开着先收（原地视图一次只占一个面板）
                if (_queuePanelOpen)
                {
                    CloseQueuePanel();
                }

                MainContentGrid.Visibility = Visibility.Collapsed;
                EqPanel.Visibility = Visibility.Visible;
                _eqPanelOpen = true;
                AnimatePanelIn(EqPanel, EqPanelTransform);
            });
        }

        private void EqCloseButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                SaveEqNow(); // 关面板前把最后一次拖动落盘
                CloseEqPanel();
            });
        }

        /// <summary>十段一起归零（离散操作，立即落盘）。</summary>
        private void EqResetButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                for (int band = 0; band < EqualizerState.BandCount; band++)
                {
                    _eqState.BandGains[band] = 0;
                }

                _eqState.Preset = EqualizerPreset.Flat;
                SyncEqSlidersFromState();
                PushEqToEngine();
                _eqSavePending = false;
                try
                {
                    EqualizerStore.Save(_eqState);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.EqResetButton_Click", caught);
                }
            });
        }

        /// <summary>十段共用：Tag 存段索引，只改对应那一段。</summary>
        private void EqBandSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_eqUiReady)
            {
                return; // 回填滑条期间，不是用户操作
            }

            if (sender is not Slider { Tag: string tag }
                || !int.TryParse(tag, out int band)
                || band < 0 || band >= EqualizerState.BandCount)
            {
                return;
            }

            Safe(() =>
            {
                double value = Math.Clamp(e.NewValue, -15, 15);
                _eqState.BandGains[band] = value;

                // 手动改过 = 自定义（与主程序拖滑条同口径：标签落 Flat）
                _eqState.Preset = EqualizerPreset.Flat;
                EqBandValueText(band).Text = FormatEqGain(value);
                PushEqToEngine();
                ScheduleEqSave();
            });
        }

        // =====================================================================
        // 下发引擎 / 落盘
        // =====================================================================

        /// <summary>把 10 段增益即时推给引擎（引擎没建时无需推 —— 构造时自己会读 store）。</summary>
        private void PushEqToEngine()
        {
            try
            {
                _engine?.SetEqualizer(_eqState.BandGains);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.PushEqToEngine", caught);
            }
        }

        /// <summary>防抖保存：拖动滑条期间每 600ms 最多写一次盘。</summary>
        private void ScheduleEqSave()
        {
            _eqSavePending = true;
            if (_eqSaveTimer == null)
            {
                _eqSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _eqSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
                _eqSaveTimer.Tick += (_, _) =>
                {
                    _eqSaveTimer.Stop();
                    SaveEqNow();
                };
            }

            // 先停再启：重复 Start 的语义不明确，显式重启保证计时器重新计满 600ms
            _eqSaveTimer.Stop();
            _eqSaveTimer.Start();
        }

        /// <summary>无条件 flush 待保存改动（关面板 / 关窗 / 打开面板前调用）。</summary>
        private void SaveEqNow()
        {
            if (!_eqSavePending)
            {
                return;
            }

            _eqSavePending = false;
            try
            {
                EqualizerStore.Save(_eqState);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.SaveEqNow", caught);
            }
        }

        /// <summary>十段数值文本（面板窄，不带单位；说明行注明 ±15dB）。</summary>
        private static string FormatEqGain(double value)
        {
            string sign = value > 0.001 ? "+" : value < -0.001 ? "-" : "";
            return sign + Math.Abs(value).ToString("0.0");
        }
    }
}
