// MainWindow.WebDsp.cs
// 「音效处理」网页面板（第 5 步 · WebUI 三层皮肤架构的 DSP 页）。
//
// 分工铁律（与 main.html / album.html 一脉相承）：
//   网页只长样子不做事——所有拨动上报意图，C# 写原生控件值，原生 handler 自然触发、
//   走既有 ApplyDspToEngine / SetXxx 下发引擎。音频链路零改动，bit-perfect 口径不动。
//
// 页面路由：主界面侧栏「音效处理」→ C# 关 main 开 dsp；dsp.html 左上「‹ 返回」→ exit
//           → C# 关 dsp 重开 main。两个网页覆盖层互斥（与 WebMain / WebAlbumPilot 同规矩）。
//
// 状态全量推送（dspstate）：导航 12 项圆点 + 11 个页头电源开关 + 各模块控件值
//   + EQ（模式/预设/频段/选中段/简单模式/快捷条）+ 方案页 + 机架页 + 监控页 + 各页说明文本。
// 每秒推送（WebDspTimer_Tick）：now（播放状态）+ dspmon（输出监控 + bit-perfect）。
//
// ⚠ 读数的前提是原生 DSP 面板已就绪：OpenWebDspAsync 里保证 EnsureAudioFxUiBuilt +
//   LoadAudioFxUiFromStore（后者末尾置 _audioFxPanelReady=true）——dspset 写控件值后，
//   原生 handler 的第一道守卫就是 _audioFxPanelReady，不就绪则写了也不下发引擎。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private bool _webDspOpen;
        private DispatcherTimer? _webDspTimer;

        /// <summary>播放条封面/曲目信息换曲才重算（与 WebMain 的 _webMainLastNowPath 同用途）。</summary>
        private string _webDspLastNowPath = "";

        /// <summary>
        /// 网页页头电源开关键 → <see cref="_dspPowerEntries"/> 下标。
        /// 顺序即 InitDspPowerEntries（MainWindow.DspPower.cs）的添加顺序，也与 dsp.html 的 PWR 表一致。
        /// </summary>
        private static readonly string[] WebDspPowerKeys =
        {
            "headroom", "rg", "src", "eq", "opra", "comp", "xfeed", "channel", "field", "matrix", "fir"
        };

        /// <summary>EQ 频段滤波器类型（网页字符串 ↔ 原生 ComboBox 下标，顺序同 EnsureAudioFxUiBuilt 的填充）。</summary>
        private static readonly string[] WebDspEqBandTypeKeys =
        {
            "Peaking", "LowShelf", "HighShelf", "LowPass", "HighPass", "Notch"
        };

        // ─────────────────────────────────────────────────────────────
        // 开 / 关
        // ─────────────────────────────────────────────────────────────

        /// <summary>打开音效处理网页面板。任何一步失败都静默降级：界面保持原生版，程序照常用。</summary>
        public async Task OpenWebDspAsync()
        {
            if (_webDspOpen) return;
            try
            {
                // 三个网页覆盖层互斥：开 DSP 先关主界面与专辑试点
                if (_webMainOpen) CloseWebMain();
                if (_webPilotOpen) CloseWebAlbumPilot();

                DeployWebAsset("dsp.html");
                if (!await EnsureCelesteWebHostAsync()) return;

                // 原生 DSP 面板必须就绪（网页所有读数来自原生控件；dspset 靠原生 handler 下发引擎）
                EnsureAudioFxUiBuilt();
                if (!_audioFxPanelReady) LoadAudioFxUiFromStore();

                _webDspLastNowPath = "";
                CelesteWebHostGrid.Visibility = Visibility.Visible;
                // 预热时 WebView2 是 Collapsed（免得启动时闪一下白屏），这会儿才让它现形
                if (_celesteWeb != null)
                {
                    _celesteWeb.Visibility = Visibility.Visible;
                    try { _celesteWeb.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
                    catch { /* 聚焦失败不致命，照常打开 */ }
                }
                if (_celesteWebBackButton != null) _celesteWebBackButton.Visibility = Visibility.Collapsed;
                _webDspOpen = true;

                var cv = _celesteWeb!.CoreWebView2;
                cv.Navigate("http://celeste.local/dsp.html");

                // 深浅色先告知网页（2026-10-09 用户拍板：暂不跟随系统，默认浅色）
                _ = PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "theme",
                    ["dark"] = false,
                });

                // 每秒把播放状态与输出监控推给网页
                _webDspTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _webDspTimer.Tick -= WebDspTimer_Tick;
                _webDspTimer.Tick += WebDspTimer_Tick;
                _webDspTimer.Start();

                StartupLog.Write("[Web音效] 已打开音效处理面板");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenWebDsp", ex);
                CloseWebDsp();
            }
        }

        /// <summary>关掉音效网页面板，回到原生界面。</summary>
        public void CloseWebDsp()
        {
            if (!_webDspOpen) return;
            _webDspOpen = false;
            if (_webDspTimer != null) _webDspTimer.Tick -= WebDspTimer_Tick;
            _webDspTimer?.Stop();
            if (CelesteWebHostGrid != null) CelesteWebHostGrid.Visibility = Visibility.Collapsed;
            if (_celesteWebBackButton != null) _celesteWebBackButton.Visibility = Visibility.Collapsed;
            StartupLog.Write("[Web音效] 已关闭，回到原生界面");
        }

        private void WebDspTimer_Tick(object? sender, object e)
        {
            if (!_webDspOpen) return;
            _ = PostCelesteWebAsync(BuildWebDspNowMessage());
            _ = PostCelesteWebAsync(BuildWebDspMonMessage());
        }

        // ─────────────────────────────────────────────────────────────
        // 消息分发（网页 → C#）
        // ─────────────────────────────────────────────────────────────

        /// <summary>网页发过来的消息在这里落地。kind 与 dsp.html 里 post() 的那些一一对应。</summary>
        internal void HandleWebDspMessage(WebInboundMessage msg)
        {
            // 页面没打开就一律不理（网页可能在关掉前又发了一条）
            if (!_webDspOpen && msg.Kind != "ready") return;

            switch (msg.Kind)
            {
                case "ready":
                    // 页面初始化完成：全量状态推过去
                    _ = PushWebDspStateAsync();
                    break;

                case "dspnav":
                    {
                        // 切模块页（下标与原生导航表一致）
                        int i = ReadJsonInt(msg.Payload, "i", -1);
                        if (i >= 0)
                        {
                            SelectDspPage(i);
                            _ = PushWebDspStateAsync();
                        }
                        break;
                    }

                case "dsppower":
                    {
                        // 拨页头电源开关：按键找到 entry，直接 Apply（与用户拨原生开关同路径）
                        string k = ReadJsonStr(msg.Payload, "k", "");
                        bool on = ReadJsonBool(msg.Payload, "on", false);
                        WebDspApplyPower(k, on);
                        break;
                    }

                case "dspset":
                    {
                        // 写控件值：c=控件键，v=数值/布尔/下标/字符串
                        string c = ReadJsonStr(msg.Payload, "c", "");
                        if (c.Length > 0) WebDspSetControl(c, msg.Payload);
                        break;
                    }

                case "dspact":
                    {
                        string a = ReadJsonStr(msg.Payload, "a", "");
                        if (a.Length > 0) WebDspDoAction(a, msg.Payload);
                        break;
                    }

                // ---- 播放控制（与 WebMain 同款语义，复用同一批原生入口）----
                case "pause":
                    TogglePlayPausePublic();
                    break;

                case "resume":
                    TogglePlayPausePublic();
                    break;

                case "next":
                    PlayNext();
                    break;

                case "prev":
                    PlayPrevious();
                    break;

                case "seek":
                    {
                        double sec = ReadJsonDouble(msg.Payload, "position", -1);
                        if (sec >= 0) SeekSourceSeconds(sec);
                        break;
                    }

                case "volume":
                    {
                        double v = ReadJsonDouble(msg.Payload, "value", -1);
                        if (v >= 0) SetVolumePublic(v * 100.0);
                        break;
                    }

                case "exit":
                    // 左上「‹ 返回」：关本页回主界面（主界面重开时自己会重推数据）
                    CloseWebDsp();
                    _ = OpenWebMainAsync();
                    break;

                case "covererr":
                    StartupLog.Write($"[Web音效] 播放条封面加载失败 src={ReadJsonStr(msg.Payload, "src", "")}");
                    break;
            }
        }

        /// <summary>拨电源开关：按键 → entry.Apply → 统一刷指示 → 重推全量。</summary>
        private void WebDspApplyPower(string key, bool on)
        {
            int idx = Array.IndexOf(WebDspPowerKeys, key);
            if (idx < 0 || idx >= _dspPowerEntries.Count)
            {
                StartupLog.Write($"[Web音效] dsppower：未知电源键「{key}」");
                return;
            }

            _dspPowerEntries[idx].Apply(on);
            // 与原生 DspPowerSwitch_Toggled 同路径的收尾：圆点/行状态/链路/电源回显一起刷
            UpdateDspNavIndicators();
            UpdateDspBitPerfectUi();
            _ = PushWebDspStateAsync();
        }

        /// <summary>写原生控件值（dspset）。所有分支只赋控件值，下发全靠原生 handler。</summary>
        private void WebDspSetControl(string key, JsonElement payload)
        {
            double n = ReadJsonDouble(payload, "v", 0);
            bool b = ReadJsonBool(payload, "v", false);
            string s = ReadJsonStr(payload, "v", "");

            // 滑杆统一夹到控件自己的量程再赋值（网页步进与原生一致，夹一下防呆）
            void SetSlider(Slider? slider, double v)
            {
                if (slider == null) return;
                slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum);
            }

            switch (key)
            {
                // ---- 输入余量 ----
                case "headroom": SetSlider(AudioFxSafetyHeadroomSlider, n); break;
                case "limiter":
                    if (AudioFxSafetyLimiterToggle != null) AudioFxSafetyLimiterToggle.IsOn = b;
                    break;

                // ---- 响度 · ReplayGain ----
                case "rgmode":
                    if (AudioFxRgModeCombo != null) AudioFxRgModeCombo.SelectedIndex = (int)n;
                    break;
                case "rgpreamp": SetSlider(AudioFxRgPreampSlider, n); break;
                case "rgclip":
                    if (AudioFxRgPreventClippingToggle != null) AudioFxRgPreventClippingToggle.IsOn = b;
                    break;

                // ---- SRC · 升频 ----
                case "srcrate":
                    if (SrcRateCombo != null) SrcRateCombo.SelectedIndex = (int)n;
                    break;
                case "srcquality":
                    if (SrcQualityCombo != null) SrcQualityCombo.SelectedIndex = (int)n;
                    break;
                case "srcdither":
                    if (SrcDitherCombo != null) SrcDitherCombo.SelectedIndex = (int)n;
                    break;

                // ---- 参数 EQ ----
                case "eqmode":
                    if (AudioFxEqModeRadio != null) AudioFxEqModeRadio.SelectedIndex = (int)n;
                    break;
                case "eqpreset":
                    if (AudioFxEqPresetCombo != null) AudioFxEqPresetCombo.SelectedIndex = (int)n;
                    break;
                case "eqpreamp": SetSlider(AudioFxEqPreampSlider, n); break;
                case "eqbass": SetSlider(AudioFxEqSimpleBassSlider, n); break;
                case "eqvocal": SetSlider(AudioFxEqSimpleVocalSlider, n); break;
                case "eqair": SetSlider(AudioFxEqSimpleAirSlider, n); break;
                case "eqwarm": SetSlider(AudioFxEqSimpleWarmSlider, n); break;
                case "eqbandsel": SelectAudioFxEqBand((int)n); break;
                case "eqbandfreq": SetSlider(AudioFxEqBandFreqSlider, n); break;
                case "eqbandgain": SetSlider(AudioFxEqBandGainSlider, n); break;
                case "eqbandq": SetSlider(AudioFxEqBandQSlider, n); break;
                case "eqbandtype":
                    {
                        // 网页推字符串（"Peaking"…），原生下拉按 Tag=EqFilterType 顺序填充
                        int ti = Array.IndexOf(WebDspEqBandTypeKeys, s);
                        if (AudioFxEqBandTypeCombo != null && ti >= 0)
                            AudioFxEqBandTypeCombo.SelectedIndex = ti;
                        break;
                    }
                case "eqbandon":
                    if (AudioFxEqBandEnableToggle != null) AudioFxEqBandEnableToggle.IsOn = b;
                    break;

                // ---- 动态压缩器 ----
                case "compthreshold": SetSlider(DspCompThresholdSlider, n); break;
                case "compratio": SetSlider(DspCompRatioSlider, n); break;
                case "compattack": SetSlider(DspCompAttackSlider, n); break;
                case "comprelease": SetSlider(DspCompReleaseSlider, n); break;
                case "compknee": SetSlider(DspCompKneeSlider, n); break;
                case "compmakeup": SetSlider(DspCompMakeupSlider, n); break;
                case "compmix": SetSlider(DspCompMixSlider, n); break;
                case "comppeak":
                    if (DspCompPeakToggle != null) DspCompPeakToggle.IsOn = b;
                    break;

                // ---- 声道工具（含 Crossfeed）----
                case "chbalance": SetSlider(AudioFxChannelBalanceSlider, n); break;
                case "chlgain": SetSlider(AudioFxChannelLeftGainSlider, n); break;
                case "chrgain": SetSlider(AudioFxChannelRightGainSlider, n); break;
                case "chldelay": SetSlider(AudioFxChannelLeftDelaySlider, n); break;
                case "chrdelay": SetSlider(AudioFxChannelRightDelaySlider, n); break;
                case "chmono":
                    if (AudioFxChannelMonoCombo != null) AudioFxChannelMonoCombo.SelectedIndex = (int)n;
                    break;
                case "chswap":
                    if (AudioFxChannelSwapToggle != null) AudioFxChannelSwapToggle.IsOn = b;
                    break;
                case "chinvertl":
                    if (AudioFxChannelInvertLToggle != null) AudioFxChannelInvertLToggle.IsOn = b;
                    break;
                case "chinvertr":
                    if (AudioFxChannelInvertRToggle != null) AudioFxChannelInvertRToggle.IsOn = b;
                    break;
                case "xfeedlevel": SetSlider(AudioFxChannelCrossfeedSlider, n); break;
                case "xfeedcutoff": SetSlider(AudioFxChannelCrossfeedCutoffSlider, n); break;

                // ---- 立体声场 ----
                case "fieldwidth": SetSlider(DspFieldWidthSlider, n); break;
                case "fieldcenter": SetSlider(DspFieldCenterSlider, n); break;
                case "fieldside": SetSlider(DspFieldSideSlider, n); break;

                // ---- 声道矩阵 ----
                case "mll": SetSlider(DspMatrixLlSlider, n); break;
                case "mrl": SetSlider(DspMatrixRlSlider, n); break;
                case "mlr": SetSlider(DspMatrixLrSlider, n); break;
                case "mrr": SetSlider(DspMatrixRrSlider, n); break;

                // ---- FIR · 房间校正 ----
                case "roomtrim": SetSlider(RoomTrimSlider, n); break;

                default:
                    StartupLog.Write($"[Web音效] dspset：未知控件键「{key}」");
                    break;
            }

            // 写完统一刷一次原生指示并回推全量（网页立刻看到真值）
            UpdateDspNavIndicators();
            UpdateDspBitPerfectUi();
            _ = PushWebDspStateAsync();
        }

        /// <summary>按钮动作表（dspact）。大部分直接调原生 core 方法，语义与点原生按钮完全一致。</summary>
        private void WebDspDoAction(string a, JsonElement payload)
        {
            switch (a)
            {
                // ---- 总旁路 ----
                case "bypass":
                    if (DspBypassToggle != null) DspBypassToggle.IsOn = ReadJsonBool(payload, "on", false);
                    break;

                // ---- 听音方案 ----
                case "pfsave":
                    SaveDspProfileCore(ReadJsonStr(payload, "name", ""));
                    WebDspToast("已保存方案");
                    break;
                case "pfapply":
                    ApplySelectedDspProfileCore();
                    WebDspToast("已应用选中方案");
                    break;
                case "pfupdate":
                    UpdateSelectedDspProfileCore();
                    WebDspToast("已更新为当前设置");
                    break;
                case "pfdelete":
                    DeleteSelectedDspProfileCore();
                    WebDspToast("已删除方案");
                    break;
                case "pfsel":
                    if (DspProfileList != null)
                        DspProfileList.SelectedIndex = ReadJsonInt(payload, "i", -1);
                    break;

                // ---- 机架编排 ----
                case "rksel":
                    if (DspRackList != null)
                        DspRackList.SelectedIndex = ReadJsonInt(payload, "i", -1);
                    break;
                case "rkup": MoveDspRackEntry(-1); break;
                case "rkdown": MoveDspRackEntry(1); break;
                case "rkreset": ResetDspRackOrderCore(); break;

                // ---- FIR ----
                case "openroom": OpenRoomCorrectionCore(); break;
                case "firsafe": FirSafeEnableCore(); break;

                // ---- 输出监控 ----
                case "monreset": ResetOutputMonitorCore(); break;

                // ---- EQ ----
                case "eqaddband": AddAudioFxEqBandCore(); break;
                case "eqdelband": DeleteAudioFxEqBandCore(); break;
                case "eqflat": FlatAudioFxEqSimpleCore(); break;
                case "equndo": WebDspToast(UndoAudioFxEqCore()); break;
                case "eqredo": WebDspToast(RedoAudioFxEqCore()); break;
                case "eqab": WebDspToast(ToggleAudioFxEqAbCore()); break;
                case "eqautogain": WebDspToast(AutoGainAudioFxEqCore()); break;
                case "eqspectrum":
                    {
                        // 原生 ToggleButton 不会被网页点到，先翻它的选中态，core 再照原语义读
                        if (AudioFxEqSpectrumToggle != null)
                            AudioFxEqSpectrumToggle.IsChecked = AudioFxEqSpectrumToggle.IsChecked != true;
                        WebDspToast(ToggleAudioFxEqSpectrumCore());
                        break;
                    }
                case "eqsavepreset":
                    {
                        string name = ReadJsonStr(payload, "name", "");
                        _ = WebDspSavePresetAsync(name);
                        return; // 异步完成后自己会重推，这里不走统一重推
                    }
                case "eqimportapo":
                    _ = WebDspApoAsync(true);
                    return;
                case "eqexportapo":
                    _ = WebDspApoAsync(false);
                    return;

                default:
                    StartupLog.Write($"[Web音效] dspact：未知动作「{a}」");
                    break;
            }

            // 统一收尾：刷原生指示 + 回推全量（提前 return 的异步分支自己负责）
            UpdateDspNavIndicators();
            UpdateDspBitPerfectUi();
            _ = PushWebDspStateAsync();
        }

        private async Task WebDspSavePresetAsync(string name)
        {
            string r = await SaveAudioFxEqPresetCoreAsync(name);
            WebDspToast(r);
            await PushWebDspStateAsync();
        }

        private async Task WebDspApoAsync(bool import)
        {
            if (import) await ImportAudioFxEqApoCoreAsync();
            else await ExportAudioFxEqApoCoreAsync();
            await PushWebDspStateAsync();
        }

        /// <summary>在网页顶部显示一句操作结果（原生 NowPlayingText 照旧反馈，两边都有）。</summary>
        private void WebDspToast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _ = PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "dspmsg",
                ["text"] = text,
            });
        }

        // ─────────────────────────────────────────────────────────────
        // 状态推送（C# → 网页）
        // ─────────────────────────────────────────────────────────────

        /// <summary>全量状态推送。先把原生各模块 UI 刷到最新，网页读到的全是真值。</summary>
        private async Task PushWebDspStateAsync()
        {
            try
            {
                UpdateAudioFxEqQuickStrip();
                RefreshDspProfileUi();
                RefreshDspRackRows();
                UpdateDspOutputMonitor();
                UpdateDspDeviceStatus();
                RefreshAudioFxRgInfo();
                RefreshRoomCorrectionInfo();

                await PostCelesteWebAsync(BuildWebDspStateMessage());
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PushWebDspState", ex);
            }
        }

        private Dictionary<string, object?> BuildWebDspStateMessage()
        {
            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;

            // 导航 12 项：标题/组/圆点（圆点 = 该模块正在参与处理，口径同原生左侧小圆点）
            bool[] active = DspModuleActive();
            var nav = new List<object>();
            for (int i = 0; i < _dspNavEntries.Count; i++)
            {
                DspNavEntry e = _dspNavEntries[i];
                nav.Add(new Dictionary<string, object?>
                {
                    ["t"] = e.Title,
                    ["g"] = e.Group,
                    ["dot"] = i < active.Length && active[i],
                });
            }

            // 11 个页头电源开关：通电态 / 禁用态 / 提示（禁用原因优先，旁路时显示「已旁路」）
            var power = new Dictionary<string, object?>();
            for (int i = 0; i < WebDspPowerKeys.Length && i < _dspPowerEntries.Count; i++)
            {
                DspPowerEntry entry = _dspPowerEntries[i];
                string? disabledHint = entry.DisabledHint?.Invoke();
                power[WebDspPowerKeys[i]] = new Dictionary<string, object?>
                {
                    ["on"] = entry.IsOn(),
                    ["dis"] = disabledHint != null,
                    ["hint"] = disabledHint ?? (bypass ? "已旁路" : ""),
                };
            }

            // 各模块控件当前值（与原生 XAML 的 Minimum/Maximum/选项顺序一致）
            var ctrl = new Dictionary<string, object?>
            {
                ["headroom"] = AudioFxSafetyHeadroomSlider?.Value ?? 0,
                ["limiter"] = AudioFxSafetyLimiterToggle?.IsOn ?? false,
                ["rgmode"] = AudioFxRgModeCombo?.SelectedIndex ?? 0,
                ["rgpreamp"] = AudioFxRgPreampSlider?.Value ?? 0,
                ["rgclip"] = AudioFxRgPreventClippingToggle?.IsOn ?? false,
                ["srcrate"] = SrcRateCombo?.SelectedIndex ?? 0,
                ["srcquality"] = SrcQualityCombo?.SelectedIndex ?? 0,
                ["srcdither"] = SrcDitherCombo?.SelectedIndex ?? 0,
                ["eqpreamp"] = AudioFxEqPreampSlider?.Value ?? 0,
                ["eqbass"] = _eqSimpleBass,
                ["eqvocal"] = _eqSimpleVocal,
                ["eqair"] = _eqSimpleAir,
                ["eqwarm"] = _eqSimpleWarm,
                ["compthreshold"] = DspCompThresholdSlider?.Value ?? 0,
                ["compratio"] = DspCompRatioSlider?.Value ?? 0,
                ["compattack"] = DspCompAttackSlider?.Value ?? 0,
                ["comprelease"] = DspCompReleaseSlider?.Value ?? 0,
                ["compknee"] = DspCompKneeSlider?.Value ?? 0,
                ["compmakeup"] = DspCompMakeupSlider?.Value ?? 0,
                ["compmix"] = DspCompMixSlider?.Value ?? 0,
                ["comppeak"] = DspCompPeakToggle?.IsOn ?? false,
                ["chbalance"] = AudioFxChannelBalanceSlider?.Value ?? 0,
                ["chlgain"] = AudioFxChannelLeftGainSlider?.Value ?? 0,
                ["chrgain"] = AudioFxChannelRightGainSlider?.Value ?? 0,
                ["chldelay"] = AudioFxChannelLeftDelaySlider?.Value ?? 0,
                ["chrdelay"] = AudioFxChannelRightDelaySlider?.Value ?? 0,
                ["chmono"] = AudioFxChannelMonoCombo?.SelectedIndex ?? 0,
                ["chswap"] = AudioFxChannelSwapToggle?.IsOn ?? false,
                ["chinvertl"] = AudioFxChannelInvertLToggle?.IsOn ?? false,
                ["chinvertr"] = AudioFxChannelInvertRToggle?.IsOn ?? false,
                ["xfeedlevel"] = AudioFxChannelCrossfeedSlider?.Value ?? 0,
                ["xfeedcutoff"] = AudioFxChannelCrossfeedCutoffSlider?.Value ?? 700,
                ["fieldwidth"] = DspFieldWidthSlider?.Value ?? 100,
                ["fieldcenter"] = DspFieldCenterSlider?.Value ?? 0,
                ["fieldside"] = DspFieldSideSlider?.Value ?? 0,
                ["mll"] = DspMatrixLlSlider?.Value ?? 1,
                ["mrl"] = DspMatrixRlSlider?.Value ?? 0,
                ["mlr"] = DspMatrixLrSlider?.Value ?? 0,
                ["mrr"] = DspMatrixRrSlider?.Value ?? 1,
                ["roomtrim"] = RoomTrimSlider?.Value ?? 0,
            };

            // EQ：模式 / 预设表 / 频段 / 选中段 / 简单模式 / 快捷条（pill 全文）
            var presets = new List<object>();
            if (AudioFxEqPresetCombo != null)
            {
                for (int i = 0; i < AudioFxEqPresetCombo.Items.Count; i++)
                {
                    string label = (AudioFxEqPresetCombo.Items[i] as ComboBoxItem)?.Content?.ToString() ?? "";
                    presets.Add(new Dictionary<string, object?> { ["t"] = label });
                }
            }

            var bands = new List<object>();
            if (_audioFxEq != null)
            {
                foreach (EqBand band in _audioFxEq.Bands)
                {
                    bands.Add(new Dictionary<string, object?>
                    {
                        ["f"] = band.FrequencyHz,
                        ["g"] = band.GainDb,
                        ["q"] = band.Q,
                        ["ty"] = band.FilterType.ToString(),
                        ["on"] = band.Enabled,
                    });
                }
            }

            var eq = new Dictionary<string, object?>
            {
                ["mode"] = AudioFxEqModeRadio?.SelectedIndex ?? 0,
                ["presets"] = presets,
                ["preset"] = AudioFxEqPresetCombo?.SelectedIndex ?? 0,
                ["bands"] = bands,
                ["sel"] = _audioFxEqSelected,
                ["simple"] = new Dictionary<string, object?>
                {
                    ["bass"] = _eqSimpleBass,
                    ["vocal"] = _eqSimpleVocal,
                    ["air"] = _eqSimpleAir,
                    ["warm"] = _eqSimpleWarm,
                },
                ["strip"] = new Dictionary<string, object?>
                {
                    ["status"] = AudioFxEqPillStatusText?.Text ?? "",
                    ["perfect"] = AudioFxEqPillPerfectText?.Text ?? "",
                    ["preamp"] = AudioFxEqPillPreampText?.Text ?? "",
                    ["headroom"] = AudioFxEqPillHeadroomText?.Text ?? "",
                    ["preset"] = AudioFxEqPillPresetText?.Text ?? "",
                },
                ["opra"] = _opraApplied,
            };

            // 听音方案：状态行 / 保存提示 / 选中行 / 行表
            var pfRows = new List<object>();
            for (int i = 0; i < _dspProfileRows.Count; i++)
            {
                DspProfile p = _dspProfileRows[i];
                pfRows.Add(new Dictionary<string, object?>
                {
                    ["name"] = p.Name,
                    ["stamp"] = FormatDspProfileStamp(p.ModifiedUtc, p.CreatedUtc),
                    ["on"] = string.Equals(p.Name, _dspProfileActiveName, StringComparison.OrdinalIgnoreCase),
                });
            }

            var profiles = new Dictionary<string, object?>
            {
                ["status"] = DspProfileStatusText?.Text ?? "",
                ["savehint"] = DspProfileSaveHintText?.Text ?? "",
                ["sel"] = DspProfileList?.SelectedIndex ?? -1,
                ["rows"] = pfRows,
            };

            // 机架编排：选中行 / 说明 / 行表（slot=「N.」/ 状态文字 / 是否生效）
            var rkRows = new List<object>();
            foreach (DspRackRow r in _dspRackRows)
            {
                rkRows.Add(new Dictionary<string, object?>
                {
                    ["slot"] = r.SlotLabel,
                    ["n"] = r.Name,
                    ["st"] = r.StateText,
                    ["on"] = string.Equals(r.StateText, "生效中", StringComparison.Ordinal),
                    ["hint"] = r.Hint,
                });
            }

            var rack = new Dictionary<string, object?>
            {
                ["sel"] = DspRackList?.SelectedIndex ?? -1,
                ["hint"] = DspRackHintText?.Text ?? "",
                ["rows"] = rkRows,
            };

            // 输出监控（RefreshDspRackRows/UpdateDspOutputMonitor 刚刷过，控件文本是最新的）
            var mon = new Dictionary<string, object?>
            {
                ["peak"] = OutMonitorPeakText?.Text ?? "",
                ["clip"] = OutMonitorClipText?.Text ?? "",
                ["over"] = OutMonitorOverloadText?.Text ?? "",
                ["active"] = OutMonitorActiveDspText?.Text ?? "",
                ["chain"] = OutMonitorChainText?.Text ?? "",
            };

            // bit-perfect 口径与 UpdateDspBitPerfectUi / IsDspActiveForBadge 完全一致
            string perfect = bypass
                ? "bit-perfect 直通（DSP 已旁路）"
                : (IsDspActiveForBadge() ? "输出非 bit-perfect（DSP 生效）" : "输出 bit-perfect 直出");

            var texts = new Dictionary<string, object?>
            {
                ["srcstate"] = SrcStateText?.Text ?? "",
                ["rg"] = AudioFxRgInfoText?.Text ?? "",
                ["fir"] = RoomIrInfoText?.Text ?? "",
                ["cliprisk"] = RoomClipRiskText?.Text ?? "",
                ["mxphase"] = DspMatrixPhaseText?.Text ?? "",
            };

            return new Dictionary<string, object?>
            {
                ["kind"] = "dspstate",
                ["page"] = _dspPageIndex < 0 ? 0 : _dspPageIndex,
                ["nav"] = nav,
                ["power"] = power,
                ["ctrl"] = ctrl,
                ["eq"] = eq,
                ["profiles"] = profiles,
                ["rack"] = rack,
                ["mon"] = mon,
                ["bypass"] = bypass,
                ["perfect"] = perfect,
                ["texts"] = texts,
                ["device"] = new Dictionary<string, object?>
                {
                    ["fmt"] = DspSessionFormatText?.Text ?? "",
                },
            };
        }

        /// <summary>每秒推送的播放状态。字段名与 dsp.html 的 'now' 分支对应（曲目用 t/s）。</summary>
        private Dictionary<string, object?> BuildWebDspNowMessage()
        {
            bool playing = _audioEngine?.IsPlaying == true;

            var msg = new Dictionary<string, object?>
            {
                ["kind"] = "now",
                ["playing"] = playing,
                ["position"] = EnginePositionValue.TotalSeconds,
                ["duration"] = EngineDurationValue.TotalSeconds,
                ["volume"] = (VolumeSlider?.Value ?? 0) / 100.0,
            };

            if (!string.IsNullOrEmpty(_nowPlayingPath))
            {
                // 曲目信息跟着正在播的那首走（标题 + 「艺术家 · 专辑」）
                var t = _playlist.FirstOrDefault(x =>
                    string.Equals(x.FilePath, _nowPlayingPath, StringComparison.OrdinalIgnoreCase));
                if (t != null)
                {
                    msg["track"] = new Dictionary<string, object?>
                    {
                        ["t"] = string.IsNullOrEmpty(t.Title)
                            ? System.IO.Path.GetFileNameWithoutExtension(t.FilePath)
                            : t.Title,
                        ["s"] = (t.Artist ?? "") + " · " + (t.Album ?? ""),
                    };
                }

                // 换曲时才带封面：WriteCoverFile 要解音频文件，不能每秒调（复用 WebMain 的缓存）
                if (!string.Equals(_nowPlayingPath, _webDspLastNowPath, StringComparison.OrdinalIgnoreCase))
                {
                    _webDspLastNowPath = _nowPlayingPath ?? "";
                    if (!_webMainCoverCache.TryGetValue(_nowPlayingPath, out var cover))
                    {
                        cover = WriteCoverFile(_nowPlayingPath);
                        _webMainCoverCache[_nowPlayingPath] = cover;
                    }
                    if (!string.IsNullOrEmpty(cover)) msg["cover"] = cover;
                }
            }

            return msg;
        }

        /// <summary>每秒推送的输出监控 + bit-perfect 状态（数据来自引擎侧真实统计）。</summary>
        private Dictionary<string, object?> BuildWebDspMonMessage()
        {
            UpdateDspOutputMonitor();

            bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;
            string perfect = bypass
                ? "bit-perfect 直通（DSP 已旁路）"
                : (IsDspActiveForBadge() ? "输出非 bit-perfect（DSP 生效）" : "输出 bit-perfect 直出");

            return new Dictionary<string, object?>
            {
                ["kind"] = "dspmon",
                ["mon"] = new Dictionary<string, object?>
                {
                    ["peak"] = OutMonitorPeakText?.Text ?? "",
                    ["clip"] = OutMonitorClipText?.Text ?? "",
                    ["over"] = OutMonitorOverloadText?.Text ?? "",
                    ["active"] = OutMonitorActiveDspText?.Text ?? "",
                    ["chain"] = OutMonitorChainText?.Text ?? "",
                },
                ["perfect"] = perfect,
            };
        }

        // ─────────────────────────────────────────────────────────────
        // 小工具（WebAlbum.cs 已有 ReadInt/ReadDouble，这里补布尔/字符串/数值读取，避免重名）
        // ─────────────────────────────────────────────────────────────

        private static int ReadJsonInt(JsonElement payload, string name, int fallback)
        {
            if (payload.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
            {
                try { return el.GetInt32(); } catch { /* 越界/小数退回 fallback */ }
            }
            return fallback;
        }

        private static double ReadJsonDouble(JsonElement payload, string name, double fallback)
        {
            if (payload.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
            {
                try { return el.GetDouble(); } catch { /* NaN/Inf 退回 fallback */ }
            }
            return fallback;
        }

        private static bool ReadJsonBool(JsonElement payload, string name, bool fallback)
        {
            if (payload.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
            }
            return fallback;
        }

        private static string ReadJsonStr(JsonElement payload, string name, string fallback)
        {
            if (payload.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString() ?? fallback;
            }
            return fallback;
        }
    }
}
