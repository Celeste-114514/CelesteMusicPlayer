using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private sealed record WebOutputDevice(string id, string name);
        private WebOutputDevice[] _webOutputDevices = Array.Empty<WebOutputDevice>();
        private string _webDevicesMode = "";
        private bool _webSettingsBusy;

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

        private static double WebNumber(JsonElement msg, string name, double min, double max)
        {
            double value = msg.GetProperty(name).GetDouble();
            if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentException("参数超出允许范围。");
            return value;
        }

        private static WebOutputDevice[] WebEnumerateDevices(string mode)
        {
            if (mode == "Asio") return HiFiOutputBackend.EnumerateAsioDrivers().Select(name => new WebOutputDevice(name, name)).ToArray();
            string defaultId = HiFiOutputBackend.GetDefaultWasapiDeviceId();
            return new[] { new WebOutputDevice("", "系统默认设备") }.Concat(HiFiOutputBackend.EnumerateWasapiDevices()
                .Select(device => new WebOutputDevice(device.Id, device.Name + (device.Id == defaultId ? "（默认）" : "")))).ToArray();
        }

        private async Task WebRefreshDevicesAsync(bool force = false)
        {
            string mode = AppSettingsStore.Load().OutputMode;
            if (!force && _webDevicesMode == mode) return;
            var devices = await Task.Run(() => WebEnumerateDevices(mode));
            if (AppSettingsStore.Load().OutputMode != mode) throw new InvalidOperationException("输出模式已变化，请刷新设备。");
            _webOutputDevices = devices;
            _webDevicesMode = mode;
        }

        internal async Task<object> WebAudioSettingsAsync(JsonElement msg)
        {
            if (_webSettingsBusy) throw new InvalidOperationException("正在应用音频设置，请稍后重试。");
            _webSettingsBusy = true;
            try
            {
                string operation = msg.GetProperty("operation").GetString() ?? "";
                switch (operation)
                {
                    case "get": await WebRefreshDevicesAsync(); break;
                    case "refresh": await WebRefreshDevicesAsync(true); break;
                    case "setMode":
                        string mode = msg.GetProperty("mode").GetString() ?? "";
                        if (mode != "Shared" && mode != "WasapiExclusive" && mode != "Asio") throw new ArgumentException("未知的输出模式。");
                        if (_audioCombosLoading) throw new InvalidOperationException("设备列表正在刷新，请稍后重试。");
                        var available = await Task.Run(() => WebEnumerateDevices(mode));
                        if (available.Length == 0) throw new InvalidOperationException("未检测到 ASIO 驱动，当前输出设置已保留。");
                        // WASAPI endpoint IDs and ASIO driver names occupy different namespaces.
                        string previousMode = AppSettingsStore.Load().OutputMode;
                        if (previousMode != mode)
                        {
                            await ChangeOutputModeAsync(mode);
                            if ((previousMode == "Asio") != (mode == "Asio"))
                                await ChangeOutputDeviceAsync(mode == "Asio" ? available[0].id : "");
                        }
                        _webDevicesMode = mode; _webOutputDevices = available;
                        break;
                    case "setDevice":
                        if (msg.GetProperty("mode").GetString() != AppSettingsStore.Load().OutputMode)
                            throw new InvalidOperationException("输出模式已变化，请重新选择设备。");
                        await WebRefreshDevicesAsync(true);
                        string device = msg.GetProperty("deviceId").GetString() ?? "";
                        if (!_webOutputDevices.Any(value => string.Equals(value.id, device, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("设备已断开或不属于当前输出模式，请刷新列表。");
                        await ChangeOutputDeviceAsync(device);
                        break;
                    case "setRate": await SetPlaybackRateAsync(WebNumber(msg, "rate", 0.5, 2)); break;
                    case "setPitch":
                        bool preserve = msg.GetProperty("preservePitch").GetBoolean();
                        AppSettingsStore.Update(settings => settings.PlaybackRatePreservePitch = preserve);
                        _audioEngine?.SetPlaybackSpeed(AppSettingsStore.Load().PlaybackRate, preserve);
                        break;
                    case "setSleep":
                        int selection = msg.GetProperty("selection").GetInt32();
                        if (selection < 0 || selection > 8) throw new ArgumentException("未知的定时选项。");
                        double customMinutes = selection == 8 ? WebNumber(msg, "minutes", 1, 1440) : 0;
                        ApplySleepSelection(selection, WebNumber(msg, "tracks", 1, 99), customMinutes);
                        break;
                    case "ab":
                        switch (msg.GetProperty("action").GetString())
                        {
                            case "a": SetAbLoopStart(); break;
                            case "b": SetAbLoopEnd(); break;
                            case "toggle": ToggleAbLoop(); break;
                            case "clear": ClearAbLoop(); break;
                            default: throw new ArgumentException("未知的循环点操作。");
                        }
                        break;
                    case "deviceMemory":
                        bool enabled = msg.GetProperty("enabled").GetBoolean();
                        DeviceDspProfileStore.SetEnabled(enabled);
                        string currentDevice = _audioEngine?.OutputDeviceId ?? "";
                        if (enabled && currentDevice.Length > 0 && !DeviceDspProfileStore.HasProfile(currentDevice))
                            DeviceDspProfileStore.SaveProfile(currentDevice, DeviceDspProfileStore.CaptureCurrent());
                        break;
                    case "saveDeviceProfile":
                        string actualDevice = _audioEngine?.OutputDeviceId ?? "";
                        if (actualDevice.Length == 0) throw new InvalidOperationException("请先使用具体设备起播，再保存该设备的 DSP 配置。");
                        DeviceDspProfileStore.SaveProfile(actualDevice, DeviceDspProfileStore.CaptureCurrent());
                        break;
                    default: throw new ArgumentException("未知的音频设置操作。");
                }
                RefreshAudioSettingsPanel();
                return WebAudioSnapshot();
            }
            finally { _webSettingsBusy = false; }
        }

        private object WebAudioSnapshot()
        {
            var settings = AppSettingsStore.Load();
            string device = _audioEngine?.OutputDeviceId ?? "";
            int sleepSelection = _sleepMode switch
            {
                SleepStopMode.AfterTrack => 6, SleepStopMode.AfterTracks => 7,
                SleepStopMode.AfterMinutes => _sleepCustomMinutes > 0 ? 8 : _sleepMinutes switch { 15 => 1, 30 => 2, 60 => 3, 90 => 4, 120 => 5, _ => 0 },
                _ => 0
            };
            return new
            {
                selectedMode = settings.OutputMode, selectedDeviceId = settings.OutputDeviceId ?? "",
                actualMode = _audioEngine?.CurrentOutputMode?.ToString() ?? "", actualDevice = _audioEngine?.OutputDeviceName ?? "",
                devices = _webDevicesMode == settings.OutputMode ? _webOutputDevices : Array.Empty<WebOutputDevice>(),
                rate = settings.PlaybackRate, preservePitch = settings.PlaybackRatePreservePitch,
                dsdDirect = _audioEngine?.IsDsdDirectOut == true,
                a = _abLoopA, b = _abLoopB, abActive = _abLoopActive, abHint = _abLoopHint,
                sleepSelection, sleepTracks = Math.Max(1, _sleepTracksRemaining), sleepMinutes = _sleepCustomMinutes > 0 ? _sleepCustomMinutes : _sleepMinutes,
                deviceMemory = DeviceDspProfileStore.IsEnabled(), canSaveProfile = device.Length > 0,
                hasDeviceProfile = device.Length > 0 && DeviceDspProfileStore.HasProfile(device)
            };
        }

        // 队列页（queue）与「更多」菜单（more）已搬到 WebQueueService，转发在 MainWindow.WebQueue.cs。
    }
}
