using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 网页「音频设置」命令的接线（W13 第五刀）。
    ///
    /// 业务在 <see cref="WebAudioSettingsService"/>，这里只做转发 + 十样宿主能力。
    /// 这些能力全都要动真家伙：切模式/设备会重开输出链路，倍速要落盘并即时下发，
    /// 睡眠定时器要一个 DispatcherQueue 计时器，AB 循环要写原生面板的读数与按钮态。
    /// </summary>
    public sealed partial class MainWindow
    {
        private WebAudioSettingsService? _webAudioSettings;

        internal Task<object> WebAudioSettingsAsync(JsonElement msg) =>
            LazyInitializer.EnsureInitialized(
                ref _webAudioSettings,
                () => new WebAudioSettingsService(new WebAudioSettingsHost(this))).HandleAsync(msg);

        /// <summary>宿主实现：只读事实 + 转调既有方法，不在这里写任何业务判断。</summary>
        private sealed class WebAudioSettingsHost(MainWindow window) : IAudioSettingsHost
        {
            public bool IsDeviceListRefreshing => window._audioCombosLoading;

            public string EngineDeviceId => window._audioEngine?.OutputDeviceId ?? "";

            public AudioSettingsFacts ReadFacts()
            {
                // 睡眠选择按网页下拉的口径翻译：0=关，1..5=固定分钟，6=本曲后，7=再 N 首，8=自定义。
                // 自定义以外却对不上固定档（比如面板上被改成 45 分钟）时按「关」报，不要在网页上编一个档位。
                int sleepSelection = window._sleepMode switch
                {
                    SleepStopMode.AfterTrack => 6,
                    SleepStopMode.AfterTracks => 7,
                    SleepStopMode.AfterMinutes => window._sleepCustomMinutes > 0 ? 8 : window._sleepMinutes switch
                    {
                        15 => 1,
                        30 => 2,
                        60 => 3,
                        90 => 4,
                        120 => 5,
                        _ => 0
                    },
                    _ => 0
                };

                return new AudioSettingsFacts(
                    window._audioEngine?.CurrentOutputMode?.ToString() ?? "",
                    window._audioEngine?.OutputDeviceName ?? "",
                    window._audioEngine?.IsDsdDirectOut == true,
                    sleepSelection,
                    Math.Max(1, window._sleepTracksRemaining),
                    window._sleepCustomMinutes > 0 ? window._sleepCustomMinutes : window._sleepMinutes,
                    window._abLoopA, window._abLoopB, window._abLoopActive, window._abLoopHint);
            }

            public Task ChangeOutputModeAsync(string mode) => window.ChangeOutputModeAsync(mode);

            public Task ChangeOutputDeviceAsync(string deviceId) => window.ChangeOutputDeviceAsync(deviceId);

            public Task SetPlaybackRateAsync(double rate) => window.SetPlaybackRateAsync(rate);

            public void SetPreservePitch(bool preserve)
            {
                AppSettingsStore.Update(settings => settings.PlaybackRatePreservePitch = preserve);
                window._audioEngine?.SetPlaybackSpeed(AppSettingsStore.Load().PlaybackRate, preserve);
            }

            public void ApplySleep(int selection, double tracks, double customMinutes)
                => window.ApplySleepSelection(selection, tracks, customMinutes);

            public void ApplyAbLoop(string action)
            {
                switch (action)
                {
                    case "a": window.SetAbLoopStart(); break;
                    case "b": window.SetAbLoopEnd(); break;
                    case "toggle": window.ToggleAbLoop(); break;
                    default: window.ClearAbLoop(); break;
                }
            }

            public void RefreshPanel() => window.RefreshAudioSettingsPanel();
        }
    }
}
