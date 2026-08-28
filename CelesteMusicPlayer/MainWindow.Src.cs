using System;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 采样率升频（SRC）的 UI 部分：音频设置面板里的目标采样率下拉。
    /// 真正的重采样在 <see cref="ResamplingSourceProvider"/>（WDL 高质量重采样），
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

        /// <summary>在构造函数里调用：回填已保存的升频设置并填充下拉选项。</summary>
        private void InitializeSrcUi()
        {
            try
            {
                SrcRateCombo.Items.Clear();
                foreach (var opt in SrcRateOptions)
                {
                    SrcRateCombo.Items.Add(opt.Label);
                }

                int savedHz = AppSettingsStore.Load().SrcTargetHz;
                SrcRateCombo.SelectedIndex = IndexOfHz(savedHz);
                UpdateSrcStateText(SrcRateOptions[SrcRateCombo.SelectedIndex].Hz);
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
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Src.cs", caught);
            }
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
    }
}
