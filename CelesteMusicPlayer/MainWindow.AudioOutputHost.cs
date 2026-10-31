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
            RefreshAbLoopPage();
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
            RefreshAbLoopPage();
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
            RefreshAbLoopPage();
        }

        private void ClearAbLoop()
        {
            _abLoopA = -1;
            _abLoopB = -1;
            _abLoopActive = false;
            ApplyAbLoop();
            SetAbLoopHint("已清除 A / B。");
            RefreshAbLoopPage();
        }

        private void ApplyAbLoop() => _audioEngine?.SetAbLoop(_abLoopA, _abLoopB, _abLoopActive);

        private void SetAbLoopHint(string text)
        {
            _abLoopHint = text;
            if (AbLoopHintText != null)
            {
            }
        }

        /// <summary>弹出面板时刷新 AB 页的读数与按钮态。</summary>
        private void RefreshAbLoopPage()
        {
            if (AbLoopAText == null || AbLoopBText == null || AbLoopToggleButton == null)
            {
                return;
            }

        }

        // ---- 组合框填充 + 输出模式切换（网页 ChangeOutputMode 直通路径）----

        private async System.Threading.Tasks.Task FillAudioSettingsCombosAsync()
        {
            // 全量填充（模式 + 设备），期间置位防止 SelectionChanged 回写中间态
            _audioCombosLoading = true;
            try
            {
                var s = AppSettingsStore.Load();
                string mode = string.IsNullOrWhiteSpace(s.OutputMode) ? "Shared" : s.OutputMode;

                UpdateModeCardHighlight(mode);

                await FillAudioSettingsDevicesAsync();
            }
            finally
            {
                _audioCombosLoading = false;
            }
        }

        private async System.Threading.Tasks.Task FillAudioSettingsDevicesAsync()
        {
            try
            {
                if (AudioOutputDeviceCombo == null)
                {
                    return;
                }

                var s = AppSettingsStore.Load();
                string mode = string.IsNullOrWhiteSpace(s.OutputMode) ? "Shared" : s.OutputMode;
                string selectedId = s.OutputDeviceId ?? string.Empty;
                AudioOutputDeviceCombo.Items.Clear();

                if (string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase))
                {
                    var drivers = HiFiOutputBackend.EnumerateAsioDrivers();
                    if (drivers.Count == 0)
                    {
                        AudioOutputDeviceCombo.Items.Add(new ComboBoxItem { Content = "（未检测到 ASIO 驱动）", Tag = "" });
                    }
                    else
                    {
                        foreach (string d in drivers)
                        {
                            AudioOutputDeviceCombo.Items.Add(new ComboBoxItem { Content = d, Tag = d });
                        }
                    }
                }
                else
                {
                    var devices = HiFiOutputBackend.EnumerateWasapiDevices();
                    string defaultId = HiFiOutputBackend.GetDefaultWasapiDeviceId();
                    AudioOutputDeviceCombo.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "" });
                    foreach ((string id, string name) in devices)
                    {
                        string label = string.Equals(id, defaultId, System.StringComparison.OrdinalIgnoreCase) ? name + " (默认)" : name;
                        AudioOutputDeviceCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
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
            UpdateModeCardHighlight(mode);
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

            RefreshAudioSettingsPanel();
        }

        /// <summary>输出模式三张可视化卡片：当前选中的卡片用主题色高亮，其余还原为常态。</summary>
        private void UpdateModeCardHighlight(string mode)
        {
            Windows.UI.Color accent = ResolveAccentColor();
            var activeBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent);
            var activeBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(46, accent.R, accent.G, accent.B));
            var normalBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(60, 120, 120, 120));

            SetModeCard(ModeSharedCard, string.Equals(mode, "Shared", StringComparison.OrdinalIgnoreCase), activeBorder, activeBg, normalBorder);
            SetModeCard(ModeExclusiveCard, string.Equals(mode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase), activeBorder, activeBg, normalBorder);
            SetModeCard(ModeAsioCard, string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase), activeBorder, activeBg, normalBorder);
        }

        private static void SetModeCard(Button? card, bool active, Microsoft.UI.Xaml.Media.Brush activeBorder, Microsoft.UI.Xaml.Media.Brush activeBg, Microsoft.UI.Xaml.Media.Brush normalBorder)
        {
            if (card == null)
            {
                return;
            }

            card.BorderBrush = active ? activeBorder : normalBorder;
            card.BorderThickness = new Microsoft.UI.Xaml.Thickness(active ? 2 : 1);
            card.Background = active ? activeBg : null;
        }

        /// <summary>内核标识 → 面板显示名。
        /// 2026-10-16：ECHO 内核选项已删（程序只用原生内核），echo 值只会出现在归一化前的存量设置里。</summary>
        private static string EngineDisplayName(string engine) => engine switch
        {
            "echo" => "原生内核", // 存量 echo 设置已被 Load 归一化成 native2，这里只是防御性兜底
            _ => "原生内核",
        };

        /// <summary>内核卡片高亮：只剩原生内核一项（2026-10-16 ECHO 选项已删），恒高亮表示当前生效。</summary>
        private void UpdateEngineCardHighlight(string engine)
        {
            Windows.UI.Color accent = ResolveAccentColor();
            var activeBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent);
            var activeBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(46, accent.R, accent.G, accent.B));
            var normalBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(60, 120, 120, 120));

            SetModeCard(EngineNative2Card, true, activeBorder, activeBg, normalBorder);
        }

        // ---- 音频面板刷新 + 链路摘要行（网页 RefreshPanel 直通路径）----

        private void RefreshAudioSettingsPanel()
        {
            try
            {
                if (AudioLinkSourceFmt == null)
                {
                    return;
                }

                bool hifi = IsHiFiModeSelected();
                var chain = _audioEngine?.ChainFormat;

                // 源胶囊两行：源文件真实值 / 实际送链路的转码 WAV（各说各话，绝不混一行看串）
                // 输出胶囊两行：设备端实际格式 / 结论

                // DSP 摘要（口径与引擎 ManagedDspSourceProvider.RefreshActive 对齐：
                // 只有真正逐样本处理的模块才计入"（开）"；限幅单独开着只待命、不计入）
                bool eqOn = EqCurveStore.Load().HasEffect();
                var extra = DspExtraStore.Load();
                bool chOn = extra.ChannelBalance?.IsActive == true;
                bool limiterOn = extra.Safety?.EnableLimiter != false;    // 限幅开关状态（≠激活：单独开只待命）
                bool hasHeadroom = extra.Safety?.AffectsBits == true;    // 余量≠0：安全模块唯一真正逐样本处理的情形
                bool rgOn = ReplayGainStore.Load().Mode != ReplayGainMode.Off;
                bool volOn = _audioEngine?.IsSoftwareVolumeActive ?? false; // HiFi 软件音量同样是采样级增益
                var active = new System.Collections.Generic.List<string>();
                if (eqOn) active.Add("EQ");
                if (chOn) active.Add("声道");
                if (hasHeadroom) active.Add("余量");
                if (rgOn) active.Add("ReplayGain");
                if (volOn) active.Add("音量");
                // 限幅：开着且已有其它逐样本模块 → 随链生效（防削波）；开着但全链直通 → 待命（不动样本）；
                // 关着 → 完全不介入，摘要里不出现。2026-09-22 用户实测"限幅没开却显示开了"根因：
                // 旧 AffectsBits 口径把"显式关限幅"算成激活（DspState.cs 已修），此处同步改口径。
                string limiterNote = !limiterOn
                    ? string.Empty
                    : (active.Count > 0
                        ? " · 限幅生效（随 DSP 链防削波）"
                        : " · 限幅待命（无其它 DSP 时不做逐样本处理，不影响 bit-perfect）");
                string dsp = active.Count == 0 ? "全部旁路" : string.Join(" / ", active) + "（开）";
                // bit-perfect：综合判定（DSP + 输出格式 + 是否共享模式），不再只看 DSP 开关
                bool chainPure = EvaluateBitPerfectChain(out string chainReason, out bool chainConfirmed);
                AudioLinkBitPerfect.Text = chainPure
                    ? (chainConfirmed
                        ? "bit-perfect 直通：输出格式与源一致，无重采样、无 DSP 干预"
                        : "bit-perfect 直通（DSP 已旁路；开始播放后按输出格式复核）")
                    : "非 bit-perfect：" + chainReason
                      + (chainConfirmed ? string.Empty : "（待播放确认）");

                // 专业播放状态
                // 与上面 DSP 摘要同口径：余量✓=真在处理；限幅✓=随链生效、待=开着但全链直通、—=已关闭
                bool limiterEngaged = limiterOn && active.Count > 0;
                // 链路可视化着色 + bit-perfect 徽章（用整链判定，而非仅 DSP 开关）
                ApplyLinkVisual(pure: chainPure, activeText: chainReason);
                // 同步主界面常驻 bit-perfect 徽章
                // 按设备记忆 DSP 配置档：开关与提示同步
                if (DeviceDspMemoryToggle != null)
                {
                }

                // ASIO 卡顿排查提醒（2026-09-24 KA13 实测定案：驱动缓冲 5.8ms + USB 电源管理=几秒一卡；
                // 喂料器内置 4s ring 已排除备货问题，故选 ASIO 时常显此条，切换模式即时可查）
                if (AsioBufferTipText != null)
                {
                    bool asioMode = string.Equals(AppSettingsStore.Load().OutputMode, "Asio", StringComparison.OrdinalIgnoreCase);
                }

                // 独占输出内核 A/B 卡片高亮同步
                UpdateEngineCardHighlight(AppSettingsStore.Load().ExclusiveEngine);

                if (DeviceDspHint != null)
                {
                    string cur = _audioEngine?.OutputDeviceId;
                    if (string.IsNullOrWhiteSpace(cur))
                    {
                    }
                    else
                    {
                        bool has = DeviceDspProfileStore.HasProfile(cur);
                    }
                }

                // SRC 会话实际状态（源→目标 / 未升频原因）
                RefreshSrcSessionState();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>链路「源」胶囊第一行：源文件真实格式（探测结构化值；DSD/探测失败如实说明，不编数）。
        /// 绝不让显示说谎——2026-09-21 用户实测 24bit 显示成 16bit 后，本行只认 <see cref="ChainFormatState.SourceFile"/>。</summary>
        private static string BuildSourceFileLine(ChainFormatState? chain, bool hifi)
        {
            if (chain?.SourceFile is AudioFormat sf)
            {
                return "源文件：" + sf.Describe();
            }

            if (!string.IsNullOrWhiteSpace(chain?.SourceFileDescription))
            {
                return "源文件：" + chain!.SourceFileDescription;
            }

            if (chain?.Outcome == TranscodeOutcome.FailedFallback)
            {
                return "源文件：探测失败（已按兜底规格转码）";
            }

            return hifi ? "源文件：（解析中…）" : "源文件：MediaPlayer（系统解码）";
        }

        /// <summary>链路「源」胶囊第二行：实际送链路的转码 WAV（WAV 头真实值 + 转码结果注：
        /// 同格式 / 容器扩容数值无损 / 已重采样 / 已降级 / 探测失败兜底）。</summary>
        private static string BuildTranscodeWavLine(ChainFormatState? chain, bool hifi)
        {
            if (chain?.TranscodeWav is AudioFormat wav)
            {
                return "转码 WAV：" + wav.Describe() + chain.OutcomeNote();
            }

            return hifi ? "转码 WAV：（解析中…）" : "转码 WAV：不经转码（系统解码）";
        }

        /// <summary>链路「输出」胶囊第一行：设备端实际输出格式（结构化协商值；取不到时如实说未知，不假设直通）。</summary>
        private static string BuildDeviceLine(ChainFormatState? chain, bool hifi)
        {
            if (chain?.DeviceOutput is AudioFormat dev)
            {
                return "设备端：" + dev.Describe()
                    + (string.IsNullOrWhiteSpace(chain.DeviceEndpointName) ? string.Empty : "（" + chain.DeviceEndpointName + "）");
            }

            if (!hifi)
            {
                return "设备端：系统混音器（Shared）";
            }

            // 播放过但协商值缺失（如 ASIO 无 OutputWaveFormat）：不能假设直通
            return chain?.HasSession == true ? "设备端：未知（协商值缺失，待确认）" : "设备端：（解析中…）";
        }

        /// <summary>链路「输出」胶囊第二行：结论。先讲转码层事实（重采样/降级），再讲设备端路径，
        /// 两者独立——"设备端源直通"与"转码已重采样"可以同时成立且都必须说。</summary>
        private static string BuildVerdictLine(ChainFormatState? chain, bool hifi)
        {
            if (!hifi)
            {
                return "结论：共享模式——系统混音器必然重采样，非 bit-perfect";
            }

            if (chain == null || !chain.HasSession)
            {
                return "结论：待播放确认";
            }

            var parts = new System.Collections.Generic.List<string>();
            string note = chain.OutcomeNote(); // （已重采样，迁就设备）/（已降级：规格被做低）/（源探测失败…）
            if (note.Length > 0)
            {
                // 阶段二：重采样时把缘由一起说清（"设备不支持 96k，已重采样到 48k"），不只写"迁就设备"
                string detail = chain.Outcome == TranscodeOutcome.ResampledToDevice
                    && !string.IsNullOrWhiteSpace(chain.TranscodeReason)
                    ? "转码" + note + "（" + chain.TranscodeReason + "）"
                    : "转码" + note;
                parts.Add(detail);
            }

            parts.Add(chain.VerdictNote());
            return "结论：" + string.Join(" · ", parts);
        }

        private void ApplyLinkVisual(bool pure, string activeText)
        {
            try
            {
                if (LinkDspCapsule == null)
                {
                    return;
                }

                var green = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 160, 67));
                var amber = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 214, 148, 45));
                var greenBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 46, 160, 67));
                var amberBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 214, 148, 45));


            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
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

                RefreshAudioSettingsPanel();
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
                if (AudioDeviceHotplugText == null)
                {
                    return;
                }


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
                if (AudioDeviceHotplugText != null)
                {
                }
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
