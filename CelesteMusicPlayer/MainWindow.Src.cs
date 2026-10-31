using System;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 采样率升频（SRC）的 UI 部分：音频设置面板里的目标采样率 / 质量档位 / 量化抖动下拉，
    /// 以及最近一次播放的实际 SRC 状态显示。
    /// 业务参数真值在 <see cref="DspSrcParameters"/>，本文件只做「装载 → 投影到控件 → 改动后应用」。
    /// 真正的重采样在 <see cref="ResamplingSourceProvider"/>（WDL 高质量重采样 + 可选 Dither），
    /// 由 <see cref="HiFiOutputBackend.BuildSrcChain"/> 在 WASAPI 独占模式下挂入链路。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>在构造函数里调用：回填已保存的升频设置并填充下拉选项。</summary>
        private void InitializeSrcUi()
        {
            try
            {
                AppSettingsState saved = AppSettingsStore.Load();
                SrcParameters.Load(saved.SrcTargetHz, saved.SrcQuality, saved.SrcDither);

                FillLabelCombo(SrcRateCombo, DspSrcParameters.RateLabels);
                UpdateSrcStateText(SrcParameters.TargetHz);

                FillLabelCombo(SrcQualityCombo, DspSrcParameters.QualityLabels);

                FillLabelCombo(SrcDitherCombo, DspSrcParameters.DitherLabels);

                RefreshSrcSessionState();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Src.cs", caught);
            }
        }

        /// <summary>下拉框选项只是模型的投影：清空重填文案，值仍由 SelectedIndex 指向模型下标。</summary>
        private static void FillLabelCombo(ComboBox? combo, string[] labels)
        {
            if (combo == null) return;
            combo.Items.Clear();
            foreach (string label in labels)
            {
                combo.Items.Add(label);
            }
        }

        /// <summary>把模型里的升频参数落盘并下发引擎。网页命令与页头电源开关走这一条（含全部三项）。</summary>
        private void ApplySrcSettings()
        {
            DspSrcParameters.State src = SrcParameters.Snapshot();

            AppSettingsState s = AppSettingsStore.Load();
            if (s.SrcTargetHz != src.TargetHz
                || !string.Equals(s.SrcQuality, src.Quality, StringComparison.Ordinal)
                || !string.Equals(s.SrcDither, src.Dither, StringComparison.Ordinal))
            {
                s.SrcTargetHz = src.TargetHz;
                s.SrcQuality = src.Quality;
                s.SrcDither = src.Dither;
                AppSettingsStore.Save(s);
            }

            _audioEngine?.SetResampleTargetRate(src.TargetHz);
            _audioEngine?.SetSrcQuality(src.Quality);
            _audioEngine?.SetSrcDither(src.Dither);
            UpdateSrcStateText(src.TargetHz);
            RefreshSrcSessionState();
            RedrawSrcCanvas();
        }

        /// <summary>刷新「当前会话 SRC 实际状态」：显示最近一次播放的真实升频结果（含自动退回原因）。
        /// 播放会话变化时由播放逻辑调用。</summary>
        internal void RefreshSrcSessionState()
        {
            // 链路图上的源/目标采样率就来自这段会话描述，一起刷
            RedrawSrcChain();
        }

        /// <summary>刷新右侧状态文字：关闭 / 已开启。</summary>
        private void UpdateSrcStateText(int hz)
        {
        }

        #region 输出缓冲区（抗卡顿 / 低延迟）

        private static readonly (string Label, int Ms, string Hint)[] OutputBufferOptions =
        {
            ("20 ms（极低延迟）", 20, "极低"),
            ("50 ms（低延迟）", 50, "低"),
            ("100 ms（推荐）", 100, "推荐"),
            ("200 ms（抗卡顿）", 200, "抗卡顿"),
            ("300 ms（强抗卡顿）", 300, "强抗"),
            ("500 ms（最稳）", 500, "最稳"),
        };

        /// <summary>在构造函数里调用：回填已保存的输出缓冲设置并填充下拉选项。</summary>
        private void InitializeOutputBufferUi()
        {
            try
            {

                int saved = AppSettingsStore.Load().OutputBufferMs;
                foreach (var opt in OutputBufferOptions)
                {
                }

                int idx = 2; // 默认 100ms（推荐）
                for (int i = 0; i < OutputBufferOptions.Length; i++)
                {
                    if (OutputBufferOptions[i].Ms == saved)
                    {
                        idx = i;
                        break;
                    }
                }

                _audioEngine?.SetOutputBufferMs(OutputBufferOptions[idx].Ms);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Src.cs", caught);
            }
        }

        #endregion
    }
}
