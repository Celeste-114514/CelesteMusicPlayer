using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 音频设置服务需要宿主配合的十件事。都做不出替代品：
    /// 切模式/切设备要动引擎，倍速要落盘并下发，睡眠定时器和 AB 循环要写原生面板，刷新面板是界面活。
    /// </summary>
    internal interface IAudioSettingsHost
    {
        /// <summary>原生音频面板正在重建设备列表（此时拒绝切模式，别让两处并发枚举设备）。</summary>
        bool IsDeviceListRefreshing { get; }

        /// <summary>引擎真在用的输出设备 id；从没起播过就是空串。</summary>
        string EngineDeviceId { get; }

        /// <summary>引擎、睡眠定时器、AB 循环的当前事实。服务只读这份，不碰控件。</summary>
        AudioSettingsFacts ReadFacts();

        /// <summary>切输出模式（会重开输出链路）。</summary>
        Task ChangeOutputModeAsync(string mode);

        /// <summary>切输出设备。</summary>
        Task ChangeOutputDeviceAsync(string deviceId);

        /// <summary>设播放倍速：落盘 + 实时下发。</summary>
        Task SetPlaybackRateAsync(double rate);

        /// <summary>只改"音调不变"开关，并用当前倍率重新下发一次。</summary>
        void SetPreservePitch(bool preserve);

        /// <summary>应用睡眠定时选择（含原生面板回显）。</summary>
        void ApplySleep(int selection, double tracks, double customMinutes);

        /// <summary>AB 循环：a / b / toggle / clear。</summary>
        void ApplyAbLoop(string action);

        /// <summary>把最新状态刷回原生音频设置面板（网页改的也要同步过去）。</summary>
        void RefreshPanel();
    }

    /// <summary>
    /// 引擎 + 睡眠定时器 + AB 循环的当前事实快照。
    /// <paramref name="SleepSelection"/> 是面板口径的下拉序号（0=关，1..5=固定分钟，6/7=按曲，8=自定义）。
    /// </summary>
    internal sealed record AudioSettingsFacts(
        string ActualMode, string ActualDevice, bool DsdDirect,
        int SleepSelection, int SleepTracks, double SleepMinutes,
        double AbA, double AbB, bool AbActive, string AbHint);

    /// <summary>
    /// 网页「音频设置」页的桥接层（W13：从 MainWindow.WebTransport.cs 搬出来）。
    ///
    /// 这里只做三件事：校验参数、调用宿主、把状态翻译成网页认识的字段。
    /// 设备枚举本身是静态的（<see cref="HiFiOutputBackend"/>），不占宿主口子；
    /// 枚举结果按「输出模式」缓存，模式没变就复用（`get` 会被网页每次进页面调用）。
    ///
    /// ⚠ 两个坑别踩回去：
    /// 1. `setMode` 只有在跨 WASAPI/ASIO 命名空间时才能自动挑设备 —— 两种 id 不通用，
    ///    在同一个命名空间内贸然换设备会把用户选好的设备冲掉。
    /// 2. 缓存模式与当前设置不一致时，`devices` 必须返回空数组而不是旧列表：
    ///    网页拿旧模式的设备去设，会被 `setDevice` 拒掉，用户看到的是「点了没反应」。
    /// </summary>
    internal sealed class WebAudioSettingsService(IAudioSettingsHost host)
    {
        private sealed record OutputDevice(string id, string name);

        private OutputDevice[] _devices = Array.Empty<OutputDevice>();
        private string _devicesMode = "";
        private bool _busy;

        internal async Task<object> HandleAsync(JsonElement msg)
        {
            if (_busy) throw new InvalidOperationException("正在应用音频设置，请稍后重试。");
            _busy = true;
            try
            {
                string operation = msg.GetProperty("operation").GetString() ?? "";
                switch (operation)
                {
                    case "get": await RefreshDevicesAsync(); break;
                    case "refresh": await RefreshDevicesAsync(true); break;
                    case "setMode":
                        string mode = msg.GetProperty("mode").GetString() ?? "";
                        if (mode != "Shared" && mode != "WasapiExclusive" && mode != "Asio") throw new ArgumentException("未知的输出模式。");
                        if (host.IsDeviceListRefreshing) throw new InvalidOperationException("设备列表正在刷新，请稍后重试。");
                        var available = await Task.Run(() => EnumerateDevices(mode));
                        if (available.Length == 0) throw new InvalidOperationException("未检测到 ASIO 驱动，当前输出设置已保留。");
                        // WASAPI 的 endpoint id 和 ASIO 驱动名不在同一个命名空间里。
                        string previousMode = AppSettingsStore.Load().OutputMode;
                        if (previousMode != mode)
                        {
                            await host.ChangeOutputModeAsync(mode);
                            if ((previousMode == "Asio") != (mode == "Asio"))
                                await host.ChangeOutputDeviceAsync(mode == "Asio" ? available[0].id : "");
                        }
                        _devicesMode = mode;
                        _devices = available;
                        break;
                    case "setDevice":
                        if (msg.GetProperty("mode").GetString() != AppSettingsStore.Load().OutputMode)
                            throw new InvalidOperationException("输出模式已变化，请重新选择设备。");
                        await RefreshDevicesAsync(true);
                        string device = msg.GetProperty("deviceId").GetString() ?? "";
                        if (!_devices.Any(value => string.Equals(value.id, device, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("设备已断开或不属于当前输出模式，请刷新列表。");
                        await host.ChangeOutputDeviceAsync(device);
                        break;
                    case "setRate": await host.SetPlaybackRateAsync(Number(msg, "rate", 0.5, 2)); break;
                    case "setPitch": host.SetPreservePitch(msg.GetProperty("preservePitch").GetBoolean()); break;
                    case "setSleep":
                        int selection = msg.GetProperty("selection").GetInt32();
                        if (selection < 0 || selection > 8) throw new ArgumentException("未知的定时选项。");
                        double customMinutes = selection == 8 ? Number(msg, "minutes", 1, 1440) : 0;
                        host.ApplySleep(selection, Number(msg, "tracks", 1, 99), customMinutes);
                        break;
                    case "ab":
                        // 先认字再动手：未知动作不能落到宿主的 if 链里被当成 clear。
                        string action = msg.GetProperty("action").GetString() ?? "";
                        if (action != "a" && action != "b" && action != "toggle" && action != "clear")
                            throw new ArgumentException("未知的循环点操作。");
                        host.ApplyAbLoop(action);
                        break;
                    case "deviceMemory":
                        bool enabled = msg.GetProperty("enabled").GetBoolean();
                        DeviceDspProfileStore.SetEnabled(enabled);
                        string currentDevice = host.EngineDeviceId;
                        if (enabled && currentDevice.Length > 0 && !DeviceDspProfileStore.HasProfile(currentDevice))
                            DeviceDspProfileStore.SaveProfile(currentDevice, DeviceDspProfileStore.CaptureCurrent());
                        break;
                    case "saveDeviceProfile":
                        string actualDevice = host.EngineDeviceId;
                        if (actualDevice.Length == 0) throw new InvalidOperationException("请先使用具体设备起播，再保存该设备的 DSP 配置。");
                        DeviceDspProfileStore.SaveProfile(actualDevice, DeviceDspProfileStore.CaptureCurrent());
                        break;
                    default: throw new ArgumentException("未知的音频设置操作。");
                }
                host.RefreshPanel();
                return Snapshot();
            }
            finally { _busy = false; }
        }

        /// <summary>网页每次进音频设置页都会调 `get`：模式没变就复用上次枚举结果，不去重复枚举设备。</summary>
        private async Task RefreshDevicesAsync(bool force = false)
        {
            string mode = AppSettingsStore.Load().OutputMode;
            if (!force && _devicesMode == mode) return;
            var devices = await Task.Run(() => EnumerateDevices(mode));
            if (AppSettingsStore.Load().OutputMode != mode) throw new InvalidOperationException("输出模式已变化，请刷新设备。");
            _devices = devices;
            _devicesMode = mode;
        }

        private static OutputDevice[] EnumerateDevices(string mode)
        {
            if (mode == "Asio") return HiFiOutputBackend.EnumerateAsioDrivers().Select(name => new OutputDevice(name, name)).ToArray();
            string defaultId = HiFiOutputBackend.GetDefaultWasapiDeviceId();
            return new[] { new OutputDevice("", "系统默认设备") }.Concat(HiFiOutputBackend.EnumerateWasapiDevices()
                .Select(device => new OutputDevice(device.Id, device.Name + (device.Id == defaultId ? "（默认）" : "")))).ToArray();
        }

        private object Snapshot()
        {
            var settings = AppSettingsStore.Load();
            var facts = host.ReadFacts();
            string device = host.EngineDeviceId;
            return new
            {
                selectedMode = settings.OutputMode,
                selectedDeviceId = settings.OutputDeviceId ?? "",
                actualMode = facts.ActualMode,
                actualDevice = facts.ActualDevice,
                // 缓存模式与当前模式不一致 → 空列表，别把上个模式的设备递给网页。
                devices = _devicesMode == settings.OutputMode ? _devices : Array.Empty<OutputDevice>(),
                rate = settings.PlaybackRate,
                preservePitch = settings.PlaybackRatePreservePitch,
                dsdDirect = facts.DsdDirect,
                a = facts.AbA,
                b = facts.AbB,
                abActive = facts.AbActive,
                abHint = facts.AbHint,
                sleepSelection = facts.SleepSelection,
                sleepTracks = facts.SleepTracks,
                sleepMinutes = facts.SleepMinutes,
                deviceMemory = DeviceDspProfileStore.IsEnabled(),
                canSaveProfile = device.Length > 0,
                hasDeviceProfile = device.Length > 0 && DeviceDspProfileStore.HasProfile(device)
            };
        }

        private static double Number(JsonElement msg, string name, double min, double max)
        {
            double value = msg.GetProperty(name).GetDouble();
            if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentException("参数超出允许范围。");
            return value;
        }
    }
}
