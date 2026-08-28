using System;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 采样率升频（SRC）的 UI 部分：音频设置面板里的目标采样率 / 质量档位 / 量化抖动下拉，
    /// 以及最近一次播放的实际 SRC 状态显示。
    /// 真正的重采样在 <see cref="ResamplingSourceProvider"/>（WDL 高质量重采样 + 可选 Dither），
    /// 由 <see cref="HiFiOutputBackend.BuildSrcChain"/> 在 WASAPI 独占模式下挂入链路。
    /// </summary>
    public sealed partial class MainWindow
    {
        private const int SrcRateOff = 0;

        /// <summary>可选的升频目标采样率（Hz）。首项为「关闭」。</summary>
        private static readonly (string Label, int Hz)[] SrcRateOptions =
        {
            ("关闭（原采样率）", 0),
            ("44.1 kHz", 44100),
            ("48 kHz", 48000),
            ("88.2 kHz", 88200),
            ("96 kHz", 96000),
            ("176.4 kHz", 176400),
            ("192 kHz", 192000),
        };

        private static readonly (string Label, string Key, string Hint)[] SrcQualityOptions =
        {
            ("低延迟（省 CPU）", ResamplingSourceProvider.QualityLowLatency, "低延迟"),
            ("均衡（推荐）", ResamplingSourceProvider.QualityBalanced, "均衡"),
            ("透明（最高质量）", ResamplingSourceProvider.QualityTransparent, "透明"),
        };

        private static readonly (string Label, string Key, string Hint)[] SrcDitherOptions =
        {
            ("关闭", ResamplingSourceProvider.DitherOff, "关闭"),
            ("TPDF 抖动", ResamplingSourceProvider.DitherTpdf, "TPDF"),
            ("高通 TPDF", ResamplingSourceProvider.DitherHighpass, "高通"),
            ("NS-5 噪声整形", ResamplingSourceProvider.DitherNs5, "NS-5"),
        };

        /// <summary>在构造函数里调用：回填已保存的升频设置并填充下拉选项。</summary>
        private void InitializeSrcUi()
        {
            try
            {
                AppSettingsState saved = AppSettingsStore.Load();

                SrcRateCombo.Items.Clear();
                foreach (var opt in SrcRateOptions)
                {
                    SrcRateCombo.Items.Add(opt.Label);
                }

                SrcRateCombo.SelectedIndex = IndexOfHz(saved.SrcTargetHz);
                UpdateSrcStateText(SrcRateOptions[SrcRateCombo.SelectedIndex].Hz);

                SrcQualityCombo.Items.Clear();
                foreach (var opt in SrcQualityOptions)
                {
                    SrcQualityCombo.Items.Add(opt.Label);
                }

                SrcQualityCombo.SelectedIndex = IndexOfKey(SrcQualityOptions, saved.SrcQuality, 1);
                SrcQualityHintText.Text = SrcQualityOptions[SrcQualityCombo.SelectedIndex].Hint;

                SrcDitherCombo.Items.Clear();
                foreach (var opt in SrcDitherOptions)
                {
                    SrcDitherCombo.Items.Add(opt.Label);
                }

                SrcDitherCombo.SelectedIndex = IndexOfKey(SrcDitherOptions, saved.SrcDither, 0);
                SrcDitherHintText.Text = SrcDitherOptions[SrcDitherCombo.SelectedIndex].Hint;

                RefreshSrcSessionState();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Src.cs", caught);
            }
        }

        private void SrcRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                int hz = SelectedHz();
                UpdateSrcStateText(hz);

                AppSettingsState s = AppSettingsStore.Load();
                if (s.SrcTargetHz != hz)
                {
                    s.SrcTargetHz = hz;
                    AppSettingsStore.Save(s);
                }

                // 播放中调用只保存（SRC 改变输出格式，必须下次开播生效）；引擎侧同样只透传保存
                _audioEngine?.SetResampleTargetRate(hz);
                RefreshSrcSessionState();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Src.cs", caught);
            }
        }

        private void SrcQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string key = SelectedKey(SrcQualityCombo, SrcQualityOptions, "balanced");
                SrcQualityHintText.Text = SelectedHint(SrcQualityCombo, SrcQualityOptions, "均衡");

                AppSettingsState s = AppSettingsStore.Load();
                if (s.SrcQuality != key)
                {
                    s.SrcQuality = key;
                    AppSettingsStore.Save(s);
                }

                _audioEngine?.SetSrcQuality(key);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Src.cs", caught);
            }
        }

        private void SrcDitherCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string key = SelectedKey(SrcDitherCombo, SrcDitherOptions, "off");
                SrcDitherHintText.Text = SelectedHint(SrcDitherCombo, SrcDitherOptions, "关闭");

                AppSettingsState s = AppSettingsStore.Load();
                if (s.SrcDither != key)
                {
                    s.SrcDither = key;
                    AppSettingsStore.Save(s);
                }

                _audioEngine?.SetSrcDither(key);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Src.cs", caught);
            }
        }

        /// <summary>刷新「当前会话 SRC 实际状态」：显示最近一次播放的真实升频结果（含自动退回原因）。
        /// 播放会话变化时由播放逻辑调用。</summary>
        internal void RefreshSrcSessionState()
        {
            if (SrcSessionStateText == null) return;
            string desc = _audioEngine?.SrcStateDescription ?? "";
            SrcSessionStateText.Text = string.IsNullOrEmpty(desc)
                ? "尚未播放"
                : "当前会话：" + desc;
        }

        /// <summary>刷新右侧状态文字：关闭 / 已开启。</summary>
        private void UpdateSrcStateText(int hz)
        {
            if (SrcStateText == null) return;
            SrcStateText.Text = hz <= 0 ? "关闭" : (hz / 1000.0).ToString("0.#") + " kHz";
        }

        private int SelectedHz()
        {
            int idx = SrcRateCombo.SelectedIndex;
            if (idx < 0 || idx >= SrcRateOptions.Length)
            {
                return SrcRateOff;
            }

            return SrcRateOptions[idx].Hz;
        }

        private static int IndexOfHz(int hz)
        {
            for (int i = 0; i < SrcRateOptions.Length; i++)
            {
                if (SrcRateOptions[i].Hz == hz)
                {
                    return i;
                }
            }

            return 0; // 未知值 → 关闭
        }

        private static int IndexOfKey((string Label, string Key, string Hint)[] options, string key, int fallback)
        {
            for (int i = 0; i < options.Length; i++)
            {
                if (string.Equals(options[i].Key, key, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return fallback;
        }

        private static string SelectedKey(ComboBox combo, (string Label, string Key, string Hint)[] options, string fallback)
        {
            int idx = combo?.SelectedIndex ?? -1;
            if (idx < 0 || idx >= options.Length)
            {
                return fallback;
            }

            return options[idx].Key;
        }

        private static string SelectedHint(ComboBox combo, (string Label, string Key, string Hint)[] options, string fallback)
        {
            int idx = combo?.SelectedIndex ?? -1;
            if (idx < 0 || idx >= options.Length)
            {
                return fallback;
            }

            return options[idx].Hint;
        }
    }
}
