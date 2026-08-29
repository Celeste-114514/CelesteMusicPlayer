using System;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 交叉淡化的 UI 部分：音频设置面板里的时长滑块。
    /// 真正的混合逻辑在 <see cref="SeamlessWaveProvider"/>（换曲时两首重叠、等功率淡变）。
    /// </summary>
    public sealed partial class MainWindow
    {
        private const int CrossfadeSliderMaxSeconds = 12;

        /// <summary>在构造函数里调用：把已保存的交叉淡化时长回填到滑块。</summary>
        private void InitializeCrossfadeUi()
        {
            try
            {
                int ms = AppSettingsStore.Load().CrossfadeMs;
                if (ms < 0) ms = 0;
                CrossfadeSlider.Value = Math.Clamp(ms / 1000.0, 0, CrossfadeSliderMaxSeconds);
                CrossfadeValueText.Text = FormatHelper.FormatCrossfade(ms);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Crossfade.cs", caught);
            }
        }

        private void CrossfadeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            try
            {
                double seconds = e.NewValue;
                int ms = (int)Math.Round(seconds * 1000);
                if (ms < 0) ms = 0;
                // 滑块刚离开 0 时给一个最小可用值，避免 0.5 秒以下的无意义淡化
                if (ms > 0 && ms < 100) ms = 100;

                CrossfadeValueText.Text = FormatHelper.FormatCrossfade(ms);

                AppSettingsState s = AppSettingsStore.Load();
                if (s.CrossfadeMs != ms)
                {
                    s.CrossfadeMs = ms;
                    AppSettingsStore.Save(s);
                }

                // 正在播放时立即下发；未播放时会在下次开播时从设置读取
                _audioEngine?.SetCrossfade(ms);
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Crossfade.cs", caught);
            }
        }

    }
}
