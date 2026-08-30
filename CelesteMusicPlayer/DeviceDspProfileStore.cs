using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace CelesteMusicPlayer
{
    /// <summary>某一输出设备的完整 DSP 配置快照（按设备记忆）。聚合所有 DSP 子状态。</summary>
    public sealed class DeviceDspProfile
    {
        public EqCurveState EqCurve { get; set; } = EqCurveState.Default();
        public ChannelBalanceState ChannelBalance { get; set; } = ChannelBalanceState.Default();
        public DspSafetyState Safety { get; set; } = DspSafetyState.Default();
        public RoomCorrectionState RoomCorrection { get; set; } = new();
        public ReplayGainState ReplayGain { get; set; } = new();
        public EqualizerState Equalizer { get; set; } = new();
        public SimpleEqState SimpleEq { get; set; } = new();

        public DeviceDspProfile Clone() => new()
        {
            EqCurve = EqCurve?.Clone() ?? EqCurveState.Default(),
            ChannelBalance = ChannelBalance?.Clone() ?? ChannelBalanceState.Default(),
            Safety = Safety?.Clone() ?? DspSafetyState.Default(),
            RoomCorrection = RoomCorrection?.Clone() ?? new RoomCorrectionState(),
            ReplayGain = ReplayGain?.Clone() ?? new ReplayGainState(),
            Equalizer = Equalizer != null ? JsonFile.DeepClone(Equalizer) : new EqualizerState(),
            SimpleEq = SimpleEq != null ? JsonFile.DeepClone(SimpleEq) : new SimpleEqState()
        };
    }

    /// <summary>
    /// 按设备记忆 DSP 配置档：开关 + 设备 id → 配置档 字典，持久化到 %LOCALAPPDATA%\CelesteMusicPlayer\device-dsp-profiles.json。
    /// 仅读写配置档 JSON，不触碰音频输出字节流；套用走既有的 写 store → LoadAudioFxUiFromStore → ApplyDspToEngine 链路。
    /// </summary>
    public static class DeviceDspProfileStore
    {
        private const string FileName = "device-dsp-profiles.json";
        private static readonly object Gate = new();

        public sealed class DeviceDspProfileState
        {
            public bool Enabled { get; set; }
            public Dictionary<string, DeviceDspProfile> Profiles { get; set; } = new();
        }

        private static DeviceDspProfileState _cache;

        private static string GetFilePath()
        {
            string root = AppSettingsStore.GetConfigDirectory();
            Directory.CreateDirectory(root);
            return Path.Combine(root, FileName);
        }

        private static DeviceDspProfileState Cache
        {
            get
            {
                lock (Gate)
                {
                    if (_cache == null)
                    {
                        _cache = JsonFile.Read(GetFilePath(), new DeviceDspProfileState());
                        _cache.Profiles ??= new Dictionary<string, DeviceDspProfile>();
                    }

                    return _cache;
                }
            }
        }

        public static bool IsEnabled()
        {
            lock (Gate) return Cache.Enabled;
        }

        public static void SetEnabled(bool enabled)
        {
            lock (Gate)
            {
                Cache.Enabled = enabled;
                JsonFile.Write(GetFilePath(), Cache);
            }
        }

        public static bool HasProfile(string deviceKey)
        {
            lock (Gate)
            {
                return !string.IsNullOrWhiteSpace(deviceKey) && Cache.Profiles.ContainsKey(NormKey(deviceKey));
            }
        }

        public static DeviceDspProfile GetProfile(string deviceKey)
        {
            lock (Gate)
            {
                if (string.IsNullOrWhiteSpace(deviceKey)) return new DeviceDspProfile();
                return Cache.Profiles.TryGetValue(NormKey(deviceKey), out var p) && p != null
                    ? JsonFile.DeepClone(p)
                    : new DeviceDspProfile();
            }
        }

        public static void SaveProfile(string deviceKey, DeviceDspProfile profile)
        {
            if (string.IsNullOrWhiteSpace(deviceKey) || profile == null) return;
            lock (Gate)
            {
                Cache.Profiles[NormKey(deviceKey)] = profile.Clone();
                JsonFile.Write(GetFilePath(), Cache);
            }
        }

        /// <summary>抓取当前所有 DSP 子状态，组装成一份配置档。</summary>
        public static DeviceDspProfile CaptureCurrent()
        {
            return new DeviceDspProfile
            {
                EqCurve = EqCurveStore.Load().Clone(),
                ChannelBalance = DspExtraStore.Load().ChannelBalance?.Clone() ?? ChannelBalanceState.Default(),
                Safety = DspExtraStore.Load().Safety?.Clone() ?? DspSafetyState.Default(),
                RoomCorrection = RoomCorrectionStore.Load().Clone(),
                ReplayGain = ReplayGainStore.Load().Clone(),
                Equalizer = JsonFile.DeepClone(EqualizerStore.Load()),
                SimpleEq = JsonFile.DeepClone(SimpleEqStore.Load())
            };
        }

        /// <summary>把一份配置档写回所有 DSP store（仅改配置档文件，不动音频字节流）。</summary>
        public static void ApplyToStores(DeviceDspProfile profile)
        {
            if (profile == null) return;
            EqCurveStore.Save(profile.EqCurve?.Clone() ?? EqCurveState.Default());
            DspExtraStore.Save(new DspExtraState
            {
                ChannelBalance = profile.ChannelBalance?.Clone() ?? ChannelBalanceState.Default(),
                Safety = profile.Safety?.Clone() ?? DspSafetyState.Default()
            });
            RoomCorrectionStore.Save(profile.RoomCorrection?.Clone() ?? new RoomCorrectionState());
            ReplayGainStore.Save(profile.ReplayGain?.Clone() ?? new ReplayGainState());
            EqualizerStore.Save(profile.Equalizer != null ? JsonFile.DeepClone(profile.Equalizer) : new EqualizerState());
            SimpleEqStore.Save(profile.SimpleEq != null ? JsonFile.DeepClone(profile.SimpleEq) : new SimpleEqState());
        }

        private static string NormKey(string deviceId) => (deviceId ?? string.Empty).Trim();
    }
}
