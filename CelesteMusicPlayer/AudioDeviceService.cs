using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace CelesteMusicPlayer
{
    /// <summary>音频渲染设备枚举与名称查询（HiFi 输出设备切换）。</summary>
    public static class AudioDeviceService
    {
        public sealed class RenderDevice
        {
            public string Id { get; init; } = string.Empty;

            public string Name { get; init; } = string.Empty;

            public bool IsDefault { get; init; }
        }

        private static List<RenderDevice>? _cache;
        private static Dictionary<string, string>? _nameCache;

        /// <summary>枚举所有音频渲染设备（带内存缓存）。</summary>
        public static async Task<IReadOnlyList<RenderDevice>> GetRenderDevicesAsync(bool refresh = false)
        {
            if (!refresh && _cache != null)
            {
                return _cache;
            }

            try
            {
                string defaultId = GetDefaultDeviceId();
                IReadOnlyList<DeviceInformation> devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
                StartupLog.Write("设备枚举 default=" + defaultId + " 数量=" + devices.Count);
                foreach (DeviceInformation d in devices)
                {
                    StartupLog.Write("  设备 id=" + d.Id + " name=" + d.Name);
                }

                _cache = devices.Select(d => new RenderDevice
                {
                    Id = d.Id,
                    Name = string.IsNullOrWhiteSpace(d.Name) ? "(未命名设备)" : d.Name,
                    IsDefault = d.Id == defaultId
                }).ToList();
                _nameCache = _cache.ToDictionary(d => d.Id, d => d.Name);
                return _cache;
            }
            catch
            {
                return _cache ?? new List<RenderDevice>();
            }
        }

        /// <summary>按设备 ID 取显示名（未知时返回原始 ID）。</summary>
        public static string? GetDeviceName(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            if (_nameCache != null && _nameCache.TryGetValue(deviceId, out string? name))
            {
                return name;
            }

            return deviceId;
        }

        /// <summary>系统默认渲染设备 ID。</summary>
        public static string GetDefaultDeviceId()
        {
            try
            {
                return MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
