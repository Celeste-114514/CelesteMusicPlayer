using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 音频输出设备热插拔监听（USB DAC 拔插、蓝牙耳机断开、默认设备切换等）。
    /// <para>
    /// 用 WinRT 的 DeviceWatcher 监听渲染端点变化，不做任何 COM 互操作声明，
    /// 避免手工实现 IMMNotificationClient 带来的线程/释放风险。
    /// </para>
    /// <para>
    /// DeviceWatcher 在插拔瞬间会连续触发大量事件（Added/Removed/Updated 混着来），
    /// 所以这里做 400ms 去抖 + 设备 ID 集合比对：只有集合真的变了才对外抛一次事件。
    /// </para>
    /// </summary>
    public static class AudioDeviceWatcher
    {
        /// <summary>设备集合发生变化（回调在非 UI 线程，订阅方需自行切回 UI 线程）。</summary>
        public static event EventHandler<AudioDeviceChangeEventArgs>? DevicesChanged;

        private static DeviceWatcher? _watcher;
        private static Timer? _debounce;
        private static HashSet<string>? _knownIds;
        private static readonly object Gate = new();
        private static bool _started;
        private static bool _firstEnumerationDone;

        /// <summary>变化类型：Added=新增设备 / Removed=设备消失 / Replaced=同时有增有减。</summary>
        public enum ChangeKind
        {
            Added,
            Removed,
            Replaced,
        }

        /// <summary>开始监听。重复调用安全（只会启动一次）。</summary>
        public static void Start()
        {
            lock (Gate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
                _firstEnumerationDone = false;
                _knownIds = SnapshotIds();
            }

            try
            {
                _watcher = DeviceInformation.CreateWatcher(MediaDevice.GetAudioRenderSelector());
                _watcher.Added += (_, __) => Schedule();
                _watcher.Removed += (_, __) => Schedule();
                _watcher.Updated += (_, __) => Schedule();
                _watcher.EnumerationCompleted += (_, __) =>
                {
                    // 首轮枚举结束只记录基线，不对外抛事件（避免启动时误报一次"设备变化"）
                    lock (Gate)
                    {
                        _firstEnumerationDone = true;
                        _knownIds = SnapshotIds();
                        StartupLog.Write("[设备监听] 首轮枚举完成，当前在线设备 " + _knownIds.Count + " 个");
                    }
                };

                _debounce = new Timer(_ => Evaluate(), null, Timeout.Infinite, Timeout.Infinite);
                _watcher.Start();
                StartupLog.Write("[设备监听] 已启动，基线设备数=" + (_knownIds?.Count ?? 0));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AudioDeviceWatcher.cs", caught);
                lock (Gate)
                {
                    _started = false;
                }
            }
        }

        /// <summary>停止监听并释放资源。窗口关闭时调用，避免退出阶段再收到 COM 回调。</summary>
        public static void Stop()
        {
            Timer? timer;
            DeviceWatcher? watcher;

            lock (Gate)
            {
                if (!_started)
                {
                    return;
                }

                _started = false;
                timer = _debounce;
                watcher = _watcher;
                _debounce = null;
                _watcher = null;
            }

            try
            {
                timer?.Dispose();
            }
            catch (Exception caught) { StartupLog.WriteException("AudioDeviceWatcher.cs", caught); }

            try
            {
                // DeviceWatcher 必须先 Stop 再置空；Stopped 状态下调用 Stop 是安全的
                watcher?.Stop();
            }
            catch (Exception caught) { StartupLog.WriteException("AudioDeviceWatcher.cs", caught); }
        }

        /// <summary>去抖：把连续事件合并成一次延迟执行。</summary>
        private static void Schedule()
        {
            Timer? timer;
            lock (Gate)
            {
                timer = _debounce;
            }

            // 400ms 静默窗口：插拔事件风暴结束后再统一比对一次
            timer?.Change(400, Timeout.Infinite);
        }

        private static void Evaluate()
        {
            HashSet<string> current;
            HashSet<string> previous;

            lock (Gate)
            {
                if (!_started || !_firstEnumerationDone)
                {
                    return;
                }

                previous = _knownIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            current = SnapshotIds();

            var added = current.Where(id => !previous.Contains(id)).ToList();
            var removed = previous.Where(id => !current.Contains(id)).ToList();

            if (added.Count == 0 && removed.Count == 0)
            {
                return;
            }

            lock (Gate)
            {
                _knownIds = current;
            }

            ChangeKind kind = added.Count > 0 && removed.Count > 0
                ? ChangeKind.Replaced
                : added.Count > 0 ? ChangeKind.Added : ChangeKind.Removed;

            StartupLog.Write($"[设备监听] 设备变化：{kind}，新增 {added.Count} 个，移除 {removed.Count} 个");

            try
            {
                DevicesChanged?.Invoke(null, new AudioDeviceChangeEventArgs(kind, added, removed, current));
            }
            catch (Exception caught) { StartupLog.WriteException("AudioDeviceWatcher.cs", caught); }
        }

        /// <summary>抓一份当前活跃渲染设备 ID 的快照（只读一次枚举，失败返回空集合）。</summary>
        private static HashSet<string> SnapshotIds()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach ((string id, string _) in HiFiOutputBackend.EnumerateWasapiDevices())
                {
                    if (!string.IsNullOrEmpty(id))
                    {
                        set.Add(id);
                    }
                }
            }
            catch (Exception caught) { StartupLog.WriteException("AudioDeviceWatcher.cs", caught); }

            return set;
        }
    }

    /// <summary>设备变化详情。</summary>
    public sealed class AudioDeviceChangeEventArgs : EventArgs
    {
        public AudioDeviceChangeEventArgs(
            AudioDeviceWatcher.ChangeKind kind,
            IReadOnlyList<string> added,
            IReadOnlyList<string> removed,
            IReadOnlySet<string> currentIds)
        {
            Kind = kind;
            Added = added;
            Removed = removed;
            CurrentIds = currentIds;
        }

        public AudioDeviceWatcher.ChangeKind Kind { get; }

        public IReadOnlyList<string> Added { get; }

        public IReadOnlyList<string> Removed { get; }

        /// <summary>变化后仍在线的设备 ID 全集（判断"当前设备是否还在"用这个）。</summary>
        public IReadOnlySet<string> CurrentIds { get; }
    }
}
