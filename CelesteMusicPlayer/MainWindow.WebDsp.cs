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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private bool _webDspOpen;
        /// <summary>
        /// DSP 面板嵌在主界面网页里（左栏分类还在，面板只占右侧内容区）。
        /// 2026-10-10 用户要求：音效处理不要把左侧分类栏占掉，只在右侧区域显示对应面板。
        /// 与 _webDspOpen（整页 dsp.html 覆盖层）互斥：嵌入模式下不再导航到 dsp.html。
        /// </summary>
        private bool _webDspEmbedded;
        private DispatcherTimer? _webDspTimer;

        /// <summary>
        /// 网页 DSP 导航当前停在哪一页（网页自己的下标，含多出来的「耳机校正」页）。
        /// 2026-10-10 用户要求把耳机校正整合进网页 DSP 页：网页导航比原生多一项（下标 6），
        /// 原生仍把它并「参数 EQ」页里不单列。两边下标不能直接互相当同一个数用，
        /// 这个字段记住「网页上该显示的那一页」，状态回推时以它为准。
        /// </summary>
        private int _webDspPage = -1;

        /// <summary>DSP 面板是否嵌在主界面网页里开着。</summary>
        internal bool WebDspEmbedded => _webDspEmbedded;

        /// <summary>网页上行走 DSP 通道的 kind 集合（嵌入模式下主界面要把这些转交给本文件处理）。</summary>
        internal static bool IsWebDspKind(string kind)
            => kind is "dspnav" or "dsppower" or "dspset" or "dspact";

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

        /// <summary>
        /// 把音效处理面板嵌进主界面网页的右侧内容区（左侧分类栏保留）。
        /// 与 OpenWebDspAsync 的区别：不导航到 dsp.html、不隐藏主界面层，
        /// 只是把原生 DSP 面板的读数按秒推给主界面网页，由它渲染在右栏。
        /// </summary>
        public async Task OpenEmbeddedWebDspAsync()
        {
            try
            {
                // 原生 DSP 面板必须就绪（网页所有读数来自原生控件；dspset 靠原生 handler 下发引擎）
                EnsureAudioFxUiBuilt();
                if (!_audioFxPanelReady) LoadAudioFxUiFromStore();

                _webDspEmbedded = true;
                _webDspLastNowPath = "";

                // 每秒把播放状态与输出监控推给网页（与主界面自己的推流并存，互不干扰）
                _webDspTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _webDspTimer.Tick -= WebDspTimer_Tick;
                _webDspTimer.Tick += WebDspTimer_Tick;
                _webDspTimer.Start();

                await PushWebDspStateAsync();
                StartupLog.Write("[Web音效] 已在主界面右栏打开音效处理面板");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenEmbeddedWebDsp", ex);
            }
        }

        /// <summary>离开音效处理页（切到别的分类 / 关掉主界面）时收起嵌入面板。</summary>
        public void CloseEmbeddedWebDsp()
        {
            if (!_webDspEmbedded) return;
            _webDspEmbedded = false;
            if (_webDspTimer != null) _webDspTimer.Tick -= WebDspTimer_Tick;
            _webDspTimer?.Stop();
            StartupLog.Write("[Web音效] 已收起主界面右栏的音效处理面板");
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
            // 整页覆盖（dsp.html）与嵌在主界面右栏两种形态共用这一个定时器
            if (!_webDspOpen && !_webDspEmbedded) return;
            // 嵌入主界面时 now 由主界面自己每秒推：两边的 track 字段口径不同
            // （主界面是 {title,artist,album}，DSP 页是 {t,s}），混着推会把播放条打成 undefined
            if (_webDspOpen) _ = PostCelesteWebAsync(BuildWebDspNowMessage());
            _ = PostCelesteWebAsync(BuildWebDspMonMessage());
        }

        // ─────────────────────────────────────────────────────────────
        // 消息分发（网页 → C#）
        // ─────────────────────────────────────────────────────────────

        /// <summary>网页发过来的消息在这里落地。kind 与 dsp.html 里 post() 的那些一一对应。</summary>
        internal void HandleWebDspMessage(WebInboundMessage msg)
        {
            // 页面没打开就一律不理（网页可能在关掉前又发了一条）；
            // 嵌入主界面右栏的形态也走这条通道（_webDspEmbedded）
            if (!_webDspOpen && !_webDspEmbedded && msg.Kind != "ready") return;

            switch (msg.Kind)
            {
                case "ready":
                    // 页面初始化完成：全量状态推过去
                    _ = PushWebDspStateAsync();
                    break;

                case "dspnav":
                    {
                        // 切模块页。网页导航比原生多一项「耳机校正」（下标 6），先映射回原生下标；
                        // 同时记住网页下标——不回记的话状态回推会把网页弹回「参数 EQ」页。
                        int i = ReadJsonInt(msg.Payload, "i", -1);
                        if (i >= 0)
                        {
                            _webDspPage = i;
                            SelectDspPage(WebDspToNativePage(i));
                            if (i == WebDspOpraPageIndex) WebOpraPushDb();
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

                // ---- 耳机校正（OPRA，2026-10-10 整合进网页 DSP 页）----
                // 以前这里是 opranative：点一下关掉网页、切到原生「音效处理」面板。
                // 用户嫌跳来跳去突兀，现在搜索/选型号/选曲线/应用/收藏/筛选全在网页里做，
                // 只有「应用」这一下仍走原生 ApplyOpraCurve（音频链路零改动）。
                case "opraenter":
                    // 网页进了 OPRA 页：确保数据库在加载 + 推库状态/热门品牌/最近用过
                    WebOpraPushDb();
                    return;
                case "oprasearch":
                    WebOpraSearchAsync(ReadJsonStr(payload, "q", ""));
                    return;
                case "opraproduct":
                    WebOpraSelectProductAsync(ReadJsonStr(payload, "id", ""), ReadJsonStr(payload, "name", ""));
                    return;
                case "opraseleq":
                    WebOpraSelectEq(ReadJsonStr(payload, "id", ""));
                    return;
                case "opraapply":
                    WebOpraApply();
                    return;
                case "oprafav":
                    WebOpraFavorite();
                    return;
                case "oprafilter":
                    WebOpraFilter(ReadJsonStr(payload, "v", "all"));
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
        // 网页页码 ↔ 原生页码
        // 网页导航 13 项、原生 12 项：网页在「参数 EQ」后多一个「耳机校正」页
        // （2026-10-10 用户要求整合进网页 DSP 页；原生仍把它并 EQ 页里，不单列）。
        //   网页 0..5  → 原生 0..5（原样）
        //   网页 6     → 原生 EQ 页（DspPageEqIndex）
        //   网页 7..12 → 原生 6..11（各减 1）
        // ─────────────────────────────────────────────────────────────
        private const int WebDspOpraPageIndex = 6;

        private int WebDspToNativePage(int web)
            => web < WebDspOpraPageIndex ? web
            : (web == WebDspOpraPageIndex ? DspPageEqIndex : web - 1);

        private static int WebDspFromNativePage(int native)
            => native < WebDspOpraPageIndex ? native : native + 1;

        // ─────────────────────────────────────────────────────────────
        // 耳机校正（OPRA）→ 网页
        // 2026-10-10 用户要求：耳机校正整合进网页 DSP 页，别再跳原生界面。
        // 数据全部由这里推：opradb（库状态/品牌/最近）/ opraresults（搜索命中）/
        // opraeqs（某型号的曲线列表）/ opracurve（预览频段）/ oprastatus（操作回执）。
        // 「应用」仍走原生 ApplyOpraCurve 一条路，音频链路零改动、bit-perfect 口径不动。
        // ─────────────────────────────────────────────────────────────

        /// <summary>进 OPRA 页：确保数据库在加载，然后把库状态/热门品牌/最近用过推给网页。</summary>
        private void WebOpraPushDb()
        {
            try
            {
                EnsureOpraLoaded();
                _ = PostCelesteWebAsync(BuildWebOpraDbMessage());
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebOpraPushDb", ex);
            }
        }

        /// <summary>网页正停在 OPRA 页时推最新库状态（OPRA 数据库下载完成时调用）。</summary>
        private void WebOpraPushDbIfOnPage()
        {
            if (!_webDspOpen && !_webDspEmbedded) return;
            if (_webDspPage != WebDspOpraPageIndex) return;
            try
            {
                _ = PostCelesteWebAsync(BuildWebOpraDbMessage());
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebOpraPushDbIfOnPage", ex);
            }
        }

        private Dictionary<string, object?> BuildWebOpraDbMessage()
        {
            // 原生那个状态文本是最全的口径（下载中/已就绪/失败原因都在里面）；
            // 它还没刷字（从没进过原生 EQ 页）时给网页一个进行中的说法，别显示空白。
            string status = OpraStatusText?.Text ?? "";
            if (string.IsNullOrEmpty(status))
            {
                status = _opraDbLoadStarted ? "正在下载 OPRA 数据库…" : "尚未加载 OPRA 数据库";
            }

            var vendors = new List<object>();
            try
            {
                foreach (OpraVendor v in _opra.GetTopVendors(14))
                {
                    vendors.Add(new Dictionary<string, object?>
                    {
                        ["name"] = v.Name,
                        ["count"] = _opra.GetProductCountByVendor(v.Id),
                    });
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("BuildWebOpraDbMessage.Vendors", ex);
            }

            var recent = new List<object>();
            try
            {
                OpraHistoryState st = OpraHistoryStore.Load();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (OpraHistoryItem f in st.Favorites)
                {
                    seen.Add(f.EqId);
                    recent.Add(WebOpraHistoryRow(f, "★"));
                }
                foreach (OpraHistoryItem r in st.Recent)
                {
                    if (seen.Contains(r.EqId)) continue;   // 收藏里出现过的不重复列
                    recent.Add(WebOpraHistoryRow(r, "🕘"));
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("BuildWebOpraDbMessage.Recent", ex);
            }

            return new Dictionary<string, object?>
            {
                ["kind"] = "opradb",
                ["status"] = status,
                ["vendors"] = vendors,
                ["recent"] = recent,
                ["applied"] = _opraApplied,
            };
        }

        private static Dictionary<string, object?> WebOpraHistoryRow(OpraHistoryItem item, string mark)
            => new()
            {
                ["eqId"] = item.EqId,
                ["productId"] = item.ProductId,
                ["name"] = item.ProductName,
                ["vendor"] = item.VendorName,
                ["author"] = item.Author,
                ["bandCount"] = item.BandCount,
                ["preampDb"] = item.PreampDb,
                ["mark"] = mark,
            };

        private async void WebOpraSearchAsync(string q)
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                WebOpraPushResults(q, "先输入耳机型号或品牌关键词。", new List<OpraSearchResult>());
                return;
            }

            _opraCts?.Cancel();
            _opraCts?.Dispose();
            _opraCts = new CancellationTokenSource();
            CancellationToken ct = _opraCts.Token;
            try
            {
                OpraStatus status = await _opra.EnsureLoadedAsync(false, ct);
                if (status.EqCount == 0)
                {
                    WebOpraPushResults(q, "OPRA 数据库为空，请检查网络后重试。", new List<OpraSearchResult>());
                    return;
                }

                List<OpraSearchResult> results = await _opra.SearchAsync(q, 24, ct: ct);
                _opraResults = results;
                WebOpraPushResults(q, results.Count == 0
                    ? "未找到匹配的耳机（换个品牌/型号试试）"
                    : $"找到 {results.Count} 款（点一行选曲线）", results);
            }
            catch (OperationCanceledException)
            {
                // 用户又发起了一次新搜索：旧搜索静默退出
            }
            catch (Exception ex)
            {
                WebOpraPushResults(q, "搜索失败：" + ex.Message, new List<OpraSearchResult>());
            }
        }

        private void WebOpraPushResults(string q, string status, List<OpraSearchResult> results)
        {
            var rows = new List<object>();
            foreach (OpraSearchResult r in results)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = r.ProductId,
                    ["name"] = r.Name,
                    ["subtype"] = r.Subtype ?? "",
                    ["vendor"] = r.VendorName,
                    ["eqCount"] = r.EqCount,
                });
            }

            _ = PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "opraresults",
                ["q"] = q,
                ["status"] = status,
                ["products"] = rows,
            });
        }

        /// <summary>选型号。id 命中当前搜索结果就用它；命中不了（从「最近用过」点进来的）按名字再搜一次。</summary>
        private async void WebOpraSelectProductAsync(string productId, string fallbackName)
        {
            if (string.IsNullOrEmpty(productId)) return;

            OpraSearchResult? product = _opraResults.FirstOrDefault(
                r => string.Equals(r.ProductId, productId, StringComparison.OrdinalIgnoreCase));

            if (product == null && !string.IsNullOrWhiteSpace(fallbackName))
            {
                try
                {
                    List<OpraSearchResult> hits = await _opra.SearchAsync(fallbackName, 24);
                    product = hits.FirstOrDefault(
                        r => string.Equals(r.ProductId, productId, StringComparison.OrdinalIgnoreCase));
                    if (product != null) _opraResults = hits;
                }
                catch (Exception ex)
                {
                    StartupLog.WriteException("WebOpraSelectProduct", ex);
                }
            }

            if (product == null)
            {
                WebOpraToast("没找到这个型号：本地缓存可能被清过，或这条曲线已被上游下架。");
                return;
            }

            _opraSelectedProduct = product;
            _opraEqs = _opra.GetEqsForProduct(product.ProductId);
            _opraSelectedEq = null;
            WebOpraPushEqs($"已选「{product.Name}」，在下面选一条曲线。");
        }

        /// <summary>选中一条曲线：刷新列表选中态 + 推预览频段（网页自己画曲线）。</summary>
        private void WebOpraSelectEq(string eqId)
        {
            OpraProductEqSummary? eq = _opraEqs.FirstOrDefault(
                x => string.Equals(x.EqId, eqId, StringComparison.OrdinalIgnoreCase));
            if (eq == null) return;

            _opraSelectedEq = eq;
            WebOpraPushEqs("已选中：下面看曲线形状，确认后点「应用到 EQ」。");
            WebOpraPushCurve();
        }

        /// <summary>应用：与原生 OpraApplyButton_Click 同一条路（BuildCorrection → ApplyOpraCurve）。</summary>
        private void WebOpraApply()
        {
            if (_opraSelectedProduct == null || _opraSelectedEq == null)
            {
                WebOpraToast("先选一个型号、再选一条曲线。");
                return;
            }

            try
            {
                OpraCorrection? corr = _opra.BuildCorrection(_opraSelectedEq.EqId);
                if (corr == null)
                {
                    WebOpraToast("该曲线无法解析（参数格式不支持）。");
                    return;
                }

                ApplyOpraCurve(corr.Curve);
                NoteOpraApplied(corr);
                WebOpraToast($"已应用：{corr.ProductVendorAndName()}"
                    + $"（{corr.ImportedBandCount} 段 + {corr.Curve.PreampDb:0.##} dB）"
                    + "　开启 EQ 后输出非 bit-perfect，可在「参数 EQ」页继续微调。");

                // 曲线进了 EQ：全量状态里的 eq.opra / 导航圆点 / 电源开关都要跟着变
                UpdateDspNavIndicators();
                UpdateDspBitPerfectUi();
                _ = PushWebDspStateAsync();
                WebOpraPushDb();   // 「最近用过」变了
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebOpraApply", ex);
                WebOpraToast("应用失败：" + ex.Message);
            }
        }

        private void WebOpraFavorite()
        {
            if (_opraSelectedEq == null || _opraSelectedProduct == null) return;
            try
            {
                OpraHistoryItem item = new(
                    _opraSelectedEq.EqId,
                    _opraSelectedProduct.ProductId,
                    _opraSelectedProduct.Name,
                    _opraSelectedProduct.VendorName,
                    _opraSelectedEq.Author,
                    _opraSelectedEq.BandCount,
                    _opraSelectedEq.PreampDb);

                bool nowFavorite = OpraHistoryStore.ToggleFavorite(item);
                WebOpraPushEqs(nowFavorite ? "已加入收藏。" : "已取消收藏。");
                WebOpraPushDb();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebOpraFavorite", ex);
            }
        }

        /// <summary>来源筛选：all / community（AutoEq 社区测量）/ opra（OPRA 库自带）。</summary>
        private void WebOpraFilter(string v)
        {
            _opraSourceFilter = v switch
            {
                "community" => "community",
                "opra" => "opra",
                _ => null,
            };

            WebOpraPushEqs(_opraSourceFilter == null
                ? "显示这个型号的全部曲线。"
                : (_opraSourceFilter == "community"
                    ? "只看社区测量（AutoEq）的曲线。"
                    : "只看 OPRA 库自带的曲线。"));
        }

        /// <summary>推当前型号的曲线列表（含来源筛选结果、选中项、收藏/社区徽章）。</summary>
        private void WebOpraPushEqs(string status)
        {
            List<OpraProductEqSummary> shown = _opraEqs;
            if (_opraSourceFilter != null)
            {
                bool wantCommunity = _opraSourceFilter == "community";
                shown = _opraEqs.Where(x => IsCommunityAuthor(x.Author) == wantCommunity).ToList();
            }

            var rows = new List<object>();
            foreach (OpraProductEqSummary e in shown)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["eqId"] = e.EqId,
                    ["author"] = e.Author,
                    ["preampDb"] = e.PreampDb,
                    ["bandCount"] = e.BandCount,
                    ["community"] = IsCommunityAuthor(e.Author),
                    ["fav"] = OpraHistoryStore.IsFavorite(e.EqId),
                });
            }

            OpraSearchResult? p = _opraSelectedProduct;
            _ = PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "opraeqs",
                ["product"] = new Dictionary<string, object?>
                {
                    ["id"] = p?.ProductId ?? "",
                    ["name"] = p?.Name ?? "",
                    ["subtype"] = p?.Subtype ?? "",
                    ["vendor"] = p?.VendorName ?? "",
                    ["eqCount"] = _opraEqs.Count,
                },
                ["filter"] = _opraSourceFilter ?? "all",
                ["status"] = status,
                ["sel"] = _opraSelectedEq?.EqId ?? "",
                ["eqs"] = rows,
            });
        }

        /// <summary>推选中曲线的预览频段。口径与原生 UpdateOpraPreview 同一份 BuildCorrection 结果。</summary>
        private void WebOpraPushCurve()
        {
            if (_opraSelectedEq == null) return;
            try
            {
                OpraCorrection? corr = _opra.BuildCorrection(_opraSelectedEq.EqId);
                if (corr == null)
                {
                    WebOpraToast("这条曲线无法解析（参数格式不支持）。");
                    return;
                }

                var bands = new List<object>();
                foreach (EqBand b in corr.Curve.Bands)
                {
                    bands.Add(new Dictionary<string, object?>
                    {
                        ["f"] = b.FrequencyHz,
                        ["g"] = b.GainDb,
                        ["q"] = b.Q,
                        ["ty"] = b.FilterType.ToString(),
                        ["on"] = b.Enabled,
                    });
                }

                _ = PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "opracurve",
                    ["eqId"] = corr.EqId,
                    ["author"] = corr.Author,
                    ["preampDb"] = corr.Curve.PreampDb,
                    ["bandCount"] = corr.ImportedBandCount,
                    ["community"] = IsCommunityAuthor(corr.Author),
                    ["fav"] = OpraHistoryStore.IsFavorite(corr.EqId),
                    ["bands"] = bands,
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WebOpraPushCurve", ex);
                WebOpraToast("曲线解析失败：" + ex.Message);
            }
        }

        /// <summary>OPRA 操作回执（网页顶部那一行状态文字）。</summary>
        private void WebOpraToast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _ = PostCelesteWebAsync(new Dictionary<string, object?>
            {
                ["kind"] = "oprastatus",
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
                // 网页那几张可视化（声场环 / 声道电平条 / 链路条）读的是原生这些文本，
                // 原生的这几个刷新只在对应页面可见时才跑 —— 网页嵌在主界面上时得自己补一次，
                // 否则推过去的是上次切页时的旧值（2026-10-10）。
                // 只写原生控件、不碰音频，链路零改动。
                // （声场那几个读数直接按滑杆值算，不调 RedrawDspFieldRing —— 那个要先有画布尺寸，
                //   原生面板被网页盖着时尺寸是 0，会直接 return，读到的是旧值。）
                UpdateDspFieldReadouts(
                    DspFieldToggle?.IsOn == true,
                    DspFieldWidthSlider?.Value ?? 100,
                    DspFieldCenterSlider?.Value ?? 0,
                    DspFieldSideSlider?.Value ?? 0);
                UpdateDspChannelBars();
                UpdateDspSafetyChain();
                UpdateDspCompMeters(_audioEngine?.CompressorGainReductionDb ?? 0f);

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

            // 导航：标题/组/圆点（圆点 = 该模块正在参与处理，口径同原生左侧小圆点）。
            // ⚠ 网页导航比原生多一项「耳机校正」（下标 6）：原生把它并「参数 EQ」页里，
            //   网页给它独占一页（2026-10-10 用户要求整合进网页）。所以推导航时要在
            //   这个位置插一条合成项，否则网页第 6 页之后全部错位一格。
            bool[] active = DspModuleActive();
            var nav = new List<object>();
            for (int i = 0; i < _dspNavEntries.Count; i++)
            {
                if (i == WebDspOpraPageIndex)
                {
                    nav.Add(new Dictionary<string, object?>
                    {
                        ["t"] = "耳机校正",
                        ["g"] = "塑形",
                        ["dot"] = _opraApplied,
                    });
                }
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

            // 各页的说明/读数文本。2026-10-10 补齐：网页要把原生的可视化元素
            // （压缩器曲线 / GR 表 / 输入电平 / 声场环 / 声道电平条）按同一口径画出来，
            // 这些读数是它们的数值来源。
            var texts = new Dictionary<string, object?>
            {
                ["srcstate"] = SrcStateText?.Text ?? "",
                ["rg"] = AudioFxRgInfoText?.Text ?? "",
                ["fir"] = RoomIrInfoText?.Text ?? "",
                ["cliprisk"] = RoomClipRiskText?.Text ?? "",
                ["mxphase"] = DspMatrixPhaseText?.Text ?? "",
                // 压缩器：实时增益衰减（GR）与输入电平（画 GR 表 / 输入表用）
                ["gr"] = DspCompGrText?.Text ?? "",
                ["compin"] = DspCompInText?.Text ?? "",
                // 声道工具：左右声道时间差提示（画 L/R 电平条用）
                ["skew"] = DspChannelSkewText?.Text ?? "",
                // 立体声场：中置 / 侧向读数与相关性提示（画声场环用）
                ["fmid"] = DspFieldMidReadout?.Text ?? "",
                ["fside"] = DspFieldSideReadout?.Text ?? "",
                ["fcorr"] = DspFieldCorrText?.Text ?? "",
                // 参数 EQ：提示行与光标读数
                ["eqhint"] = AudioFxEqHintText?.Text ?? "",
                ["eqread"] = AudioFxEqReadoutText?.Text ?? "",
            };

            // 顶部设备状态条 + 链路条（原生 DspDeviceStatusBar 口径）：
            // 设备名 / 输出模式 / 会话格式 / 输入→余量→处理→输出 四段链路。
            var device = new Dictionary<string, object?>
            {
                ["name"] = DspDeviceNameText?.Text ?? "",
                ["mode"] = DspOutputModeText?.Text ?? "",
                ["fmt"] = DspSessionFormatText?.Text ?? "",
                ["perfect"] = AudioFxBitPerfectStatusText?.Text ?? "",
                ["chain"] = new Dictionary<string, object?>
                {
                    ["input"] = DspChainInputText?.Text ?? "",
                    ["headroom"] = DspChainHeadroomText?.Text ?? "",
                    ["process"] = DspChainProcessText?.Text ?? "",
                    ["output"] = DspChainOutputText?.Text ?? "",
                },
            };

            // 链路条（hero）徽章：原生 UpdateDspHeroBadges 刷的那排模块状态
            var hero = new Dictionary<string, object?>
            {
                ["headroom"] = DspHeroHeadroomText?.Text ?? "",
                ["rg"] = DspHeroRgText?.Text ?? "",
                ["src"] = DspHeroSrcText?.Text ?? "",
                ["eq"] = DspHeroEqText?.Text ?? "",
                ["opra"] = DspHeroOpraText?.Text ?? "",
                ["channel"] = DspHeroChannelText?.Text ?? "",
                ["fir"] = DspHeroFirText?.Text ?? "",
                ["safety"] = DspHeroSafetyText?.Text ?? "",
            };

            return new Dictionary<string, object?>
            {
                ["kind"] = "dspstate",
                // 网页页码（含多出来的「耳机校正」页）。用户点过就以网页下标为准，
                // 没点过（首次打开）才按原生下标换算——原生 EQ 页在网页有两个落点。
                ["page"] = _webDspPage >= 0 ? _webDspPage : WebDspFromNativePage(_dspPageIndex < 0 ? 0 : _dspPageIndex),
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
                ["device"] = device,
                ["hero"] = hero,
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
            // 压缩器 GR / 入口电平的实时读数（原生那边只在压缩器页可见时才轮询，这里补上）
            UpdateDspCompMeters(_audioEngine?.CompressorGainReductionDb ?? 0f);

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
                // 每秒跟着刷新的实时读数：压缩器 GR / 输入电平、声道时间差、声场中侧与相关性
                // （网页那几张可视化的数值来源，2026-10-10）
                ["texts"] = new Dictionary<string, object?>
                {
                    ["gr"] = DspCompGrText?.Text ?? "",
                    ["compin"] = DspCompInText?.Text ?? "",
                    ["skew"] = DspChannelSkewText?.Text ?? "",
                    ["fmid"] = DspFieldMidReadout?.Text ?? "",
                    ["fside"] = DspFieldSideReadout?.Text ?? "",
                    ["fcorr"] = DspFieldCorrText?.Text ?? "",
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
