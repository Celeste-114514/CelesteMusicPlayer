using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第十六刀（2026-10-31）：音频输出宿主。
/// 从壳 <c>MainWindow.Features.cs</c> 转活的「输出模式 / 输出设备 / AB 循环 / 链路摘要」域：
/// 网页音频设置面板（WebAudioSettings）直接借用的行为入口 + 传递依赖 +
/// 输出设备热插拔监听（USB DAC / 蓝牙耳机插拔自动切换）。
/// 旧原生面板的 poke 目标全是 LegacyControls 占位控件，无害保留。
/// </summary>
public sealed partial class MainWindow
{
    // ---------------- 音频设置右侧面板（输出模式 / 链路状态 / 专业播放状态） ----------------
    private bool _audioCombosLoading;

    // ---------- 页2：AB 循环点（2026-10-16 实现） ----------
    // 实现在数据源（SeamlessWaveProvider）里回绕：读取截断到 B 点、到点直接绕回 A 点，
    // 采样级精确；不走 200ms 轮询 seek（那会在循环点超调打顿）。
    // A/B 记的是"源时间秒数"，换曲/重建会话自动重灌，CUE 分轨同样适用。

    private double _abLoopA = -1;   // 秒；-1 = 未设
    private double _abLoopB = -1;
    private bool _abLoopActive;
    private string _abLoopHint = "先设 A 点，再设 B 点。";

    // ---- 输出设备热插拔（USB DAC / 蓝牙耳机插拔自动切换）字段 ----

    /// <summary>设备被拔掉时暂存的偏好 ID；插回后自动切回。用户手动改选时清空。</summary>
    private string? _preferredOutputDeviceId;

    private bool _deviceHotplugBusy;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hotplugNoticeTimer;

        // ---- AB 循环（网页 audioSettings 与旧面板共用行为，数据源级回绕）----

        /// <summary>当前播放位置（源时间秒）。没在播放返回 -1。</summary>
        private double GetAbLoopReferencePosition()
        {
            try
            {
                if (!IsEngineActiveNow)
                {
                    return -1;
                }

                return _audioEngine?.Position.TotalSeconds ?? -1;
            }
            catch
            {
                return -1;
            }
        }

        private void SetAbLoopStart()
        {
            double pos = GetAbLoopReferencePosition();
            if (pos < 0)
            {
                SetAbLoopHint("先播放一首歌再设循环点。");
                return;
            }

            _abLoopA = pos;
            if (_abLoopB >= 0 && _abLoopB <= _abLoopA)
            {
                // B 不晚于 A：作废 B 让用户重设，别留下空区间/反向区间
                _abLoopB = -1;
                _abLoopActive = false;
            }

            ApplyAbLoop();
            SetAbLoopHint(_abLoopB >= 0 ? "A 点已设，可以开循环了。" : "A 点已设；再拖到晚一点的位置，设 B 点。");
        }

        private void SetAbLoopEnd()
        {
            double pos = GetAbLoopReferencePosition();
            if (pos < 0)
            {
                SetAbLoopHint("先播放一首歌再设循环点。");
                return;
            }

            if (_abLoopA < 0)
            {
                SetAbLoopHint("先设 A 点，再设 B 点。");
                return;
            }

            if (pos <= _abLoopA)
            {
                SetAbLoopHint("B 点要晚于 A 点（现在这个位置比 A 点还靠前）。");
                return;
            }

            _abLoopB = pos;
            ApplyAbLoop();
            SetAbLoopHint("B 点已设。点「开启 AB 循环」就开始循环。");
        }

        private void ToggleAbLoop()
        {
            if (_abLoopA < 0 || _abLoopB < 0)
            {
                SetAbLoopHint("A、B 两点都设好才能开循环。");
                return;
            }

            _abLoopActive = !_abLoopActive;
            ApplyAbLoop();
            SetAbLoopHint(_abLoopActive
                ? "循环中：播到 B 点自动跳回 A 点，进度条跟着跳是正常的。"
                : "循环已关，接着正常播。");
        }

        private void ClearAbLoop()
        {
            _abLoopA = -1;
            _abLoopB = -1;
            _abLoopActive = false;
            ApplyAbLoop();
            SetAbLoopHint("已清除 A / B。");
        }

        private void ApplyAbLoop() => _audioEngine?.SetAbLoop(_abLoopA, _abLoopB, _abLoopActive);

        private void SetAbLoopHint(string text)
        {
            _abLoopHint = text;
        }

        // ---- 组合框填充 + 输出模式切换（网页 ChangeOutputMode 直通路径）----

        private async System.Threading.Tasks.Task FillAudioSettingsDevicesAsync()
        {
            try
            {

                var s = AppSettingsStore.Load();
                string mode = string.IsNullOrWhiteSpace(s.OutputMode) ? "Shared" : s.OutputMode;
                string selectedId = s.OutputDeviceId ?? string.Empty;

                if (string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase))
                {
                    var drivers = HiFiOutputBackend.EnumerateAsioDrivers();
                    if (drivers.Count == 0)
                    {
                    }
                    else
                    {
                        foreach (string d in drivers)
                        {
                        }
                    }
                }
                else
                {
                    var devices = HiFiOutputBackend.EnumerateWasapiDevices();
                    string defaultId = HiFiOutputBackend.GetDefaultWasapiDeviceId();
                    foreach ((string id, string name) in devices)
                    {
                        string label = string.Equals(id, defaultId, System.StringComparison.OrdinalIgnoreCase) ? name + " (默认)" : name;
                    }
                }

                // 选中已保存设备
                foreach (var o in AudioOutputDeviceCombo.Items)
                {
                    if (o is ComboBoxItem it && it.Tag is string t && string.Equals(t, selectedId ?? "", System.StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        // Both shells use this path; changing a preference does not restart audio.
        private async Task ChangeOutputModeAsync(string mode)
        {
            AppSettingsStore.Update(s => s.OutputMode = mode);
            OutputMode.Apply(AppSettingsStore.Load());
            if (string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase))
            {
                // 2026-09-24 KA13 定案：ASIO 几秒一卡=驱动缓冲过小 + USB 电源管理，喂料器已内置 4s ring 仍可能中招。
                // 切模式时留痕，用户回日志也能看到排查入口（面板内亦有常显提示）。
                StartupLog.Write("[设置] 已切换 ASIO 输出；若播放几秒一次不规则卡顿，请开音频设置面板看「ASIO 卡顿排查」提示（调大控制面板 ASIO 缓冲 + 关闭 USB 选择性暂停）");
            }
            _audioCombosLoading = true;
            try
            {
                await FillAudioSettingsDevicesAsync();
            }
            finally
            {
                _audioCombosLoading = false;
            }

        }


        // ---- 输出设备热插拔（USB DAC / 蓝牙耳机插拔自动切换）----

        /// <summary>启动设备监听（窗口初始化时调用一次）。</summary>
        private void StartAudioDeviceWatcher()
        {
            try
            {
                AudioDeviceWatcher.DevicesChanged += OnAudioDevicesChanged;
                AudioDeviceWatcher.Start();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>设备变化回调。注意：这里跑在非 UI 线程，任何 XAML 访问都必须切回 UI 线程，
        /// 否则会触发 RPC_E_WRONG_THREAD。</summary>
        private void OnAudioDevicesChanged(object? sender, AudioDeviceChangeEventArgs e)
        {
            try
            {
                DispatcherQueue.TryEnqueue(() => _ = HandleAudioDevicesChangedAsync(e));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private async System.Threading.Tasks.Task HandleAudioDevicesChangedAsync(AudioDeviceChangeEventArgs e)
        {
            if (_deviceHotplugBusy)
            {
                return;
            }

            _deviceHotplugBusy = true;
            try
            {
                string savedId = AppSettingsStore.Load().OutputDeviceId ?? string.Empty;

                // 当前选中的设备掉了 → 回退系统默认，并记住它，插回来时自动切回
                bool vanished = savedId.Length > 0 && !e.CurrentIds.Contains(savedId);

                // 之前拔掉的设备又回来了 → 自动切回
                bool returned = !string.IsNullOrEmpty(_preferredOutputDeviceId)
                    && e.Added.Any(id => string.Equals(id, _preferredOutputDeviceId, StringComparison.OrdinalIgnoreCase));

                // 刷新下拉列表（无论哪种变化都要刷新，保证面板里看到的都是当前实际在线的设备）
                _audioCombosLoading = true;
                try
                {
                    await FillAudioSettingsDevicesAsync();
                }
                finally
                {
                    _audioCombosLoading = false;
                }

                if (vanished)
                {
                    _preferredOutputDeviceId = savedId;
                    AppSettingsStore.Update(s => s.OutputDeviceId = string.Empty);
                    await ApplyOutputDeviceAsync(string.Empty);
                    string name = AudioDeviceService.GetDeviceName(savedId) ?? savedId;
                    global::CelesteMusicPlayer.StartupLog.Write("[设备监听] 当前输出设备已移除，回退默认：" + name);
                    ShowHotplugNotice("输出设备「" + name + "」已断开，已切回系统默认。重新插上后会自动切回。");
                }
                else if (returned)
                {
                    string back = _preferredOutputDeviceId!;
                    _preferredOutputDeviceId = null;
                    AppSettingsStore.Update(s => s.OutputDeviceId = back);
                    await ApplyOutputDeviceAsync(back);
                    string name = AudioDeviceService.GetDeviceName(back) ?? back;
                    global::CelesteMusicPlayer.StartupLog.Write("[设备监听] 偏好设备已重新上线，自动切回：" + name);
                    ShowHotplugNotice("检测到「" + name + "」，已自动切回该设备。");
                }

            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
            finally
            {
                _deviceHotplugBusy = false;
            }
        }

        /// <summary>显示一条热插拔提示，10 秒后自动隐藏。</summary>
        private void ShowHotplugNotice(string message)
        {
            try
            {

                _hotplugNoticeTimer ??= DispatcherQueue.CreateTimer();
                _hotplugNoticeTimer.Interval = TimeSpan.FromSeconds(10);
                _hotplugNoticeTimer.Tick -= HotplugNoticeTimer_Tick;
                _hotplugNoticeTimer.Tick += HotplugNoticeTimer_Tick;
                _hotplugNoticeTimer.Stop();
                _hotplugNoticeTimer.Start();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        private void HotplugNoticeTimer_Tick(object? sender, object e)
        {
            try
            {
                _hotplugNoticeTimer?.Stop();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>用户手动改选设备时，放弃「等设备插回自动切回」的暂存偏好。</summary>
        private void ClearPreferredOutputDevice()
        {
            _preferredOutputDeviceId = null;
        }

        // ---- 输出设备切换（网页 ChangeOutputDevice 直通路径）----

        private async Task ChangeOutputDeviceAsync(string deviceId)
        {
            ClearPreferredOutputDevice();
            AppSettingsStore.Update(s => s.OutputDeviceId = deviceId);
            HiFiOutputBackend.ClearExclusivePlanCache();
            await ApplyOutputDeviceAsync(deviceId);
        }
    }
