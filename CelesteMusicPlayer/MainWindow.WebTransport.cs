using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        internal async Task<float[]> WebWaveformAsync(string id, System.Threading.CancellationToken cancellation)
        {
            // Leave the initial playback/transcode burst alone. The existing provider
            // uses the same 120 buckets and cache as the native transport.
            await Task.Delay(1200, cancellation);
            var item = GetCurrentPlayingItem();
            if (item == null || WebTrackIdentity.Of(item) != id) return Array.Empty<float>();
            double start = item.StartTimeSeconds;
            bool cue = start > 0 || _queue.Items.Any(other => other.StartTimeSeconds > 0 && string.Equals(other.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
            return await WaveformDataProvider.GetWaveformAsync(item.FilePath, 120, start, cue ? item.Duration.TotalSeconds : 0, cancellation);
        }

        internal void WebSetVolume(double volume)
        {
            if (IsHiFiModeSelected() && !IsHiFiSoftVolumeUiUnlocked())
                throw new InvalidOperationException("当前使用硬件音量，请调节设备音量旋钮。");
            SetVolumePublic(volume);
        }

        // 音频设置已搬到 WebAudioSettingsService，转发在 MainWindow.WebAudioSettings.cs。
    }
}
