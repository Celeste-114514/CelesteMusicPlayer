using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        // Web bridge for the DSP rack.
        //
        // EQ editing state, channel/safety, ReplayGain and SRC parameters are authoritative model
        // objects. Web reads and writes for those do not route through control events. The
        // remaining DSP modules still use the legacy control adapter pending their migrations.
        internal object WebDsp(JsonElement msg)
        {
            string operation = "get";
            if (msg.TryGetProperty("operation", out var opElement) && opElement.ValueKind == JsonValueKind.String)
                operation = opElement.GetString() ?? "get";

            EnsureWebDspUiReady();

            var curve = EqState.Current ?? EqCurveState.Default();

            if (operation == "get")
            {
                return new
                {
                    enabled = curve.Enabled,
                    preampDb = curve.PreampDb,
                    presetId = curve.PresetId ?? "flat",
                    presetName = curve.PresetName ?? "",
                    undo = EqState.CanUndo,
                    redo = EqState.CanRedo,
                    ab = EqState.AbArmed,
                    abShowingA = EqState.ShowingA,
                    simple = SimpleEqStore.Load(),
                    userPresets = EqUserPresetStore.Load().Select(p => new { id = p.PresetId, name = p.PresetName }).ToArray(),
                    bands = curve.Bands.Select(band => new
                    {
                        enabled = band.Enabled,
                        freq = band.FrequencyHz,
                        gain = band.GainDb,
                        q = band.Q,
                        type = band.FilterType.ToString()
                    }).ToArray()
                };
            }

            if (operation == "modules")
            {
                // One call so the nav dots can show what is really processing, instead of a decoration.
                bool[] active = DspModuleActive();
                var map = new Dictionary<string, bool>();
                foreach (var pair in WebDspModuleActiveIndex)
                    map[pair.Key] = pair.Value >= 0 && pair.Value < active.Length && active[pair.Value];
                return new { modules = map };
            }

            if (operation == "module")
            {
                string module = ReadString(msg, "module");
                if (!WebDspModuleControls.ContainsKey(module))
                    throw new ArgumentException("未知的 DSP 模块：" + module);
                return new { module, active = WebDspModuleIsActive(module), controls = WebDspDescribeModule(module), info = WebDspModuleInfo(module) };
            }

            if (operation == "setControl")
            {
                WebDspApplyControl(ReadString(msg, "name"), msg);
                SyncDspPowerSwitches();
                return new { ok = true, controls = WebDspDescribeModule(ReadString(msg, "module")) };
            }

            // ---- 听音方案 ------------------------------------------------------------------
            // Same reason as the widget modules: the native page already owns capture / apply /
            // update / delete, so the web page calls those exact paths instead of re-serialising
            // the whole DSP state in a second place.
            if (operation == "profiles")
                return WebDspProfilesPayload();

            if (operation == "profileSave")
            {
                SaveDspProfileCore(ReadString(msg, "name"));
                return WebDspProfilesPayload();
            }

            if (operation == "profileApply")
            {
                string name = ReadString(msg, "name");
                var book = DspProfileStore.Load();
                var target = book.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    throw new ArgumentException("找不到方案：" + name);
                ApplyDspProfile(target);
                return WebDspProfilesPayload();
            }

            if (operation == "profileUpdate")
            {
                string name = ReadString(msg, "name");
                var book = DspProfileStore.Load();
                var target = book.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    throw new ArgumentException("找不到方案：" + name);

                DspProfile fresh = CaptureCurrentDspProfile();
                fresh.Name = target.Name;
                fresh.CreatedUtc = target.CreatedUtc;
                fresh.ModifiedUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                DspProfileStore.Update(inner =>
                {
                    int at = inner.Profiles.FindIndex(p => string.Equals(p.Name, target.Name, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0) inner.Profiles[at] = fresh; else inner.Profiles.Add(fresh);
                    inner.ActiveProfile = target.Name;
                });
                _dspProfileActiveName = target.Name;
                _dspProfileLeftName = string.Empty;
                RefreshDspProfileUi();
                UpdateDspNavIndicators();
                return WebDspProfilesPayload();
            }

            if (operation == "profileDelete")
            {
                string name = ReadString(msg, "name");
                bool wasActive = string.Equals(_dspProfileActiveName, name, StringComparison.OrdinalIgnoreCase);
                DspProfileStore.Update(inner =>
                {
                    inner.Profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (string.Equals(inner.ActiveProfile, name, StringComparison.OrdinalIgnoreCase))
                        inner.ActiveProfile = string.Empty;
                });
                if (wasActive)
                {
                    _dspProfileActiveName = string.Empty;
                    _dspProfileLeftName = string.Empty;
                    _dspProfileRefKey = string.Empty;
                }
                RefreshDspProfileUi();
                UpdateDspNavIndicators();
                return WebDspProfilesPayload();
            }

            // ---- 机架编排 ------------------------------------------------------------------
            if (operation == "rack")
                return WebDspRackPayload();

            if (operation == "rackMove")
            {
                int index = ReadInt(msg, "index", -1);
                int delta = ReadInt(msg, "delta", 0);
                var rack = DspRackStore.Load();
                int[] order = rack.RackOrder ?? Array.Empty<int>();
                int target = index + delta;
                if (index < 0 || target < 0 || index >= order.Length || target >= order.Length)
                    throw new ArgumentException("这一行不能再移动了。");

                int[] next = (int[])order.Clone();
                (next[index], next[target]) = (next[target], next[index]);
                rack.RackOrder = next;
                DspRackStore.Save(rack);
                _audioEngine?.SetRack(rack);      // persists through DspRackStore inside SetRack
                _dspRackOrderDraft = next;        // keep the native page's draft in step
                RefreshDspRackRows();
                UpdateDspNavIndicators();
                return WebDspRackPayload();
            }

            if (operation == "rackReset")
            {
                ResetDspRackOrderCore();
                return WebDspRackPayload();
            }

            // ---- 输出安全：实时读数 ---------------------------------------------------------
            if (operation == "levels")
            {
                float peak = _audioEngine?.OutputPeakDbfs ?? float.NegativeInfinity;
                int clip = _audioEngine?.OutputClipCount ?? 0;
                bool risk = _audioEngine?.ConvolutionClippingRisk ?? false;
                bool bypass = DspBypassToggle != null && DspBypassToggle.IsOn;

                int channels = _audioEngine?.LevelMeterChannels ?? 0;
                double leftPeak = double.NegativeInfinity, rightPeak = double.NegativeInfinity;
                double leftRms = double.NegativeInfinity, rightRms = double.NegativeInfinity;
                if (channels > 0)
                {
                    // Allocated per poll on purpose: the meter is polled twice a second and the
                    // buffer size follows the device, so a cached array would be the wrong size
                    // after a device change.
                    var peaks = new float[channels];
                    var rms = new float[channels];
                    if (_audioEngine!.TryGetLevels(peaks, rms))
                    {
                        leftPeak = WebDspToDb(peaks[0]);
                        rightPeak = channels > 1 ? WebDspToDb(peaks[1]) : leftPeak;
                        leftRms = WebDspToDb(rms[0]);
                        rightRms = channels > 1 ? WebDspToDb(rms[1]) : leftRms;
                    }
                }

                bool[] active = DspModuleActive();
                var on = new List<string>();
                void AddIf(int slot, string label)
                {
                    if (slot >= 0 && slot < active.Length && active[slot]) on.Add(label);
                }
                AddIf(DspActiveHeadroom, "输入余量");
                AddIf(DspActiveRg, "ReplayGain");
                AddIf(DspActiveSrc, "SRC");
                AddIf(DspActiveEq, "EQ");
                if (_opraApplied) on.Add("耳机校正");
                AddIf(DspActiveComp, "压缩器");
                AddIf(DspActiveChannel, "声道工具");
                AddIf(DspActiveField, "声场");
                AddIf(DspActiveMatrix, "矩阵");
                AddIf(DspActiveFir, "FIR");

                string src = SrcSessionStateText?.Text ?? "";
                return new
                {
                    session = channels > 0,
                    bypass,
                    peakDb = float.IsInfinity(peak) ? -140.0 : Math.Round(peak, 2),
                    clipCount = clip,
                    overload = clip > 0 ? "clip" : peak > -0.5f ? "near" : "ok",
                    convolutionRisk = risk,
                    left = new { peak = WebDspRound(leftPeak), rms = WebDspRound(leftRms) },
                    right = new { peak = WebDspRound(rightPeak), rms = WebDspRound(rightRms) },
                    compressorGr = Math.Round(_audioEngine?.CompressorGainReductionDb ?? 0f, 2),
                    chain = string.IsNullOrWhiteSpace(src) || src == "—" ? "未升频（直出原始采样率）" : src,
                    activeDsp = on.ToArray(),
                    bitPerfect = bypass || on.Count == 0
                };
            }

            if (operation == "levelsReset")
            {
                ResetOutputMonitorCore();
                return new { ok = true };
            }

            // ---- 总旁路（页面标题右侧那颗开关） -------------------------------------------------
            if (operation == "bypass")
                return new { bypass = DspBypassToggle != null && DspBypassToggle.IsOn };

            if (operation == "setBypass")
            {
                bool next = msg.TryGetProperty("value", out var bypassElement) && bypassElement.ValueKind == JsonValueKind.True;
                if (DspBypassToggle != null && DspBypassToggle.IsOn != next)
                    DspBypassToggle.IsOn = next;   // fires the native handler: SetBypassAll + bit-perfect UI
                return new { bypass = DspBypassToggle != null && DspBypassToggle.IsOn };
            }

            if (operation is "set" or "preview")
            {
                var next = curve.Clone();

                if (msg.TryGetProperty("enabled", out var enabledElement))
                    next.Enabled = enabledElement.ValueKind == JsonValueKind.True;

                if (msg.TryGetProperty("preampDb", out var preampElement) && preampElement.ValueKind == JsonValueKind.Number)
                    next.PreampDb = WebDspFinite(preampElement, -24.0, 24.0);

                if (msg.TryGetProperty("bands", out var bandsElement) && bandsElement.ValueKind == JsonValueKind.Array)
                {
                    if (bandsElement.GetArrayLength() > Math.Max(31, curve.Bands.Count)) throw new ArgumentException("EQ 最多支持 31 个新滤波段。");
                    next.Bands.Clear();
                    foreach (var item in bandsElement.EnumerateArray())
                    {
                        var band = new EqBand();
                        if (item.TryGetProperty("enabled", out var bandEnabled))
                            band.Enabled = bandEnabled.ValueKind == JsonValueKind.True;
                        if (item.TryGetProperty("freq", out var bandFreq) && bandFreq.ValueKind == JsonValueKind.Number)
                            band.FrequencyHz = WebDspFinite(bandFreq, 20.0, 20000.0);
                        if (item.TryGetProperty("gain", out var bandGain) && bandGain.ValueKind == JsonValueKind.Number)
                            band.GainDb = WebDspFinite(bandGain, -24.0, 24.0);
                        if (item.TryGetProperty("q", out var bandQ) && bandQ.ValueKind == JsonValueKind.Number)
                            band.Q = WebDspFinite(bandQ, 0.1, 24.0);
                        string type = ReadString(item, "type");
                        if (type.Length > 0 && (!Enum.TryParse(type, out EqFilterType filter) || !Enum.IsDefined(filter)))
                            throw new ArgumentException("未知的 EQ 滤波器类型。");
                        if (type.Length > 0) band.FilterType = Enum.Parse<EqFilterType>(type);
                        next.Bands.Add(band);
                    }
                }

                next.Normalize();
                next.PresetId = "custom";
                next.PresetName = "自定义";
                if (operation == "preview") _audioEngine?.SetEqCurve(next);
                else { PushEqUndoSnapshot(); SetAudioFxEqFromSnapshot(next); }
                return new { ok = true };
            }

            if (operation == "reset")
            {
                var flat = EqCurveState.CreatePreset("flat");
                flat.PresetId = "flat"; flat.PresetName = "平坦";
                flat.Enabled = curve.Enabled;
                PushEqUndoSnapshot(); SetAudioFxEqFromSnapshot(flat);
                return new { ok = true };
            }

            if (operation == "preset")
            {
                string presetId = "flat";
                if (msg.TryGetProperty("presetId", out var presetElement) && presetElement.ValueKind == JsonValueKind.String)
                    presetId = presetElement.GetString() ?? "flat";
                var preset = EqUserPresetStore.FindById(presetId)?.Clone() ?? EqCurveState.CreatePreset(presetId);
                preset.PresetId = presetId;
                preset.PresetName = presetId switch { "flat" => "平坦", "pop" => "流行", "rock" => "摇滚", "jazz" => "爵士", "classical" => "古典", "bass" => "低音增强", "soft" => "柔和", _ => preset.PresetName };
                preset.Enabled = curve.Enabled;
                PushEqUndoSnapshot(); SetAudioFxEqFromSnapshot(preset);
                return new { ok = true };
            }

            if (operation == "undo") return new { message = UndoAudioFxEqCore() };
            if (operation == "simple")
            {
                _eqSimpleBass = WebDspFinite(msg.GetProperty("bass"), 0, 1);
                _eqSimpleVocal = WebDspFinite(msg.GetProperty("vocal"), 0, 1);
                _eqSimpleAir = WebDspFinite(msg.GetProperty("air"), 0, 1);
                _eqSimpleWarm = WebDspFinite(msg.GetProperty("warm"), 0, 1);
                ApplySimpleTones(); SyncAudioFxEqSimpleFromState();
                return new { ok = true };
            }
            if (operation == "redo") return new { message = RedoAudioFxEqCore() };
            if (operation == "ab") return new { message = ToggleAudioFxEqAbCore() };
            if (operation == "autoGain") return new { message = AutoGainAudioFxEqCore() };
            if (operation == "spectrum")
            {
                var values = new float[FormatHelper.WaveBarCount];
                bool available = _audioEngine?.TryGetSpectrum(values) ?? false;
                return new { available, bands = values.Select(v => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f).ToArray() };
            }
            throw new ArgumentException("未知的 DSP 操作。");
        }

        // Page id -> the native controls that page owns, in visual order.
        // The ids match the nav table in MainWindow.DspNav.cs:124-140. Power switches (DspPower*) are
        // included first because every module page starts with one.
        private static readonly Dictionary<string, string[]> WebDspModuleControls = new(StringComparer.Ordinal)
        {
            ["headroom"] = new[] { "DspPowerHeadroom", "AudioFxSafetyHeadroomSlider", "AudioFxSafetyLimiterToggle" },
            ["rg"] = new[] { "DspPowerRg", "AudioFxRgModeCombo", "AudioFxRgPreampSlider", "AudioFxRgPreventClippingToggle" },
            ["src"] = new[] { "DspPowerSrc", "SrcRateCombo", "SrcQualityCombo", "SrcDitherCombo" },
            ["comp"] = new[]
            {
                "DspPowerComp", "DspCompToggle", "DspCompThresholdSlider", "DspCompRatioSlider",
                "DspCompAttackSlider", "DspCompReleaseSlider", "DspCompKneeSlider",
                "DspCompMakeupSlider", "DspCompMixSlider", "DspCompPeakToggle"
            },
            ["channel"] = new[]
            {
                "DspPowerChannel", "AudioFxChannelToggle", "AudioFxChannelBalanceSlider",
                "AudioFxChannelLeftGainSlider", "AudioFxChannelRightGainSlider", "AudioFxChannelMonoCombo",
                "AudioFxChannelSwapToggle", "AudioFxChannelInvertLToggle", "AudioFxChannelInvertRToggle",
                "AudioFxChannelLeftDelaySlider", "AudioFxChannelRightDelaySlider",
                "DspPowerXfeed", "AudioFxChannelCrossfeedToggle", "AudioFxChannelCrossfeedSlider",
                "AudioFxChannelCrossfeedCutoffSlider"
            },
            ["stereo"] = new[] { "DspPowerField", "DspFieldToggle", "DspFieldWidthSlider", "DspFieldCenterSlider", "DspFieldSideSlider" },
            ["matrix"] = new[] { "DspPowerMatrix", "DspMatrixToggle", "DspMatrixLlSlider", "DspMatrixLrSlider", "DspMatrixRlSlider", "DspMatrixRrSlider" },
            ["fir"] = new[] { "DspPowerFir", "RoomIrEnabledToggle", "RoomIrGainSlider", "RoomTrimSlider" }
        };

        // Page id -> index into DspModuleActive(). Mirrors the named constants in MainWindow.DspNav.cs;
        // 'stereo' is the native "立体声场" page (DspActiveField).
        private static readonly Dictionary<string, int> WebDspModuleActiveIndex = new(StringComparer.Ordinal)
        {
            ["profiles"] = DspActiveProfiles,
            ["rack"] = DspActiveRack,
            ["headroom"] = DspActiveHeadroom,
            ["rg"] = DspActiveRg,
            ["src"] = DspActiveSrc,
            ["eq"] = DspActiveEq,
            ["comp"] = DspActiveComp,
            ["channel"] = DspActiveChannel,
            ["stereo"] = DspActiveField,
            ["matrix"] = DspActiveMatrix,
            ["fir"] = DspActiveFir,
            ["safety"] = DspActiveSafety
        };

        private bool WebDspModuleIsActive(string module)
        {
            if (!WebDspModuleActiveIndex.TryGetValue(module, out int index))
                return false;
            bool[] active = DspModuleActive();
            return index >= 0 && index < active.Length && active[index];
        }

        private Dictionary<string, FrameworkElement>? _webDspControls;

        /// <summary>
        /// Resolves a control by its x:Name. The map is built from the generated fields rather than
        /// from RootShell.FindName on purpose: a typo in a name then fails at compile time instead of
        /// turning into a page that silently renders nothing at runtime.
        /// </summary>
        private FrameworkElement? WebDspControl(string name)
        {
            if (_webDspControls == null)
            {
                var map = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
                void Add(string key, FrameworkElement? element)
                {
                    if (element != null)
                        map[key] = element;
                }

                Add("DspPowerHeadroom", DspPowerHeadroom);
                Add("AudioFxSafetyHeadroomSlider", AudioFxSafetyHeadroomSlider);
                Add("AudioFxSafetyLimiterToggle", AudioFxSafetyLimiterToggle);

                Add("DspPowerRg", DspPowerRg);
                Add("AudioFxRgModeCombo", AudioFxRgModeCombo);
                Add("AudioFxRgPreampSlider", AudioFxRgPreampSlider);
                Add("AudioFxRgPreventClippingToggle", AudioFxRgPreventClippingToggle);

                Add("DspPowerSrc", DspPowerSrc);
                Add("SrcRateCombo", SrcRateCombo);
                Add("SrcQualityCombo", SrcQualityCombo);
                Add("SrcDitherCombo", SrcDitherCombo);

                Add("DspPowerComp", DspPowerComp);
                Add("DspCompToggle", DspCompToggle);
                Add("DspCompThresholdSlider", DspCompThresholdSlider);
                Add("DspCompRatioSlider", DspCompRatioSlider);
                Add("DspCompAttackSlider", DspCompAttackSlider);
                Add("DspCompReleaseSlider", DspCompReleaseSlider);
                Add("DspCompKneeSlider", DspCompKneeSlider);
                Add("DspCompMakeupSlider", DspCompMakeupSlider);
                Add("DspCompMixSlider", DspCompMixSlider);
                Add("DspCompPeakToggle", DspCompPeakToggle);

                Add("DspPowerChannel", DspPowerChannel);
                Add("AudioFxChannelToggle", AudioFxChannelToggle);
                Add("AudioFxChannelBalanceSlider", AudioFxChannelBalanceSlider);
                Add("AudioFxChannelLeftGainSlider", AudioFxChannelLeftGainSlider);
                Add("AudioFxChannelRightGainSlider", AudioFxChannelRightGainSlider);
                Add("AudioFxChannelMonoCombo", AudioFxChannelMonoCombo);
                Add("AudioFxChannelSwapToggle", AudioFxChannelSwapToggle);
                Add("AudioFxChannelInvertLToggle", AudioFxChannelInvertLToggle);
                Add("AudioFxChannelInvertRToggle", AudioFxChannelInvertRToggle);
                Add("AudioFxChannelLeftDelaySlider", AudioFxChannelLeftDelaySlider);
                Add("AudioFxChannelRightDelaySlider", AudioFxChannelRightDelaySlider);
                Add("DspPowerXfeed", DspPowerXfeed);
                Add("AudioFxChannelCrossfeedToggle", AudioFxChannelCrossfeedToggle);
                Add("AudioFxChannelCrossfeedSlider", AudioFxChannelCrossfeedSlider);
                Add("AudioFxChannelCrossfeedCutoffSlider", AudioFxChannelCrossfeedCutoffSlider);

                Add("DspPowerField", DspPowerField);
                Add("DspFieldToggle", DspFieldToggle);
                Add("DspFieldWidthSlider", DspFieldWidthSlider);
                Add("DspFieldCenterSlider", DspFieldCenterSlider);
                Add("DspFieldSideSlider", DspFieldSideSlider);

                Add("DspPowerMatrix", DspPowerMatrix);
                Add("DspMatrixToggle", DspMatrixToggle);
                Add("DspMatrixLlSlider", DspMatrixLlSlider);
                Add("DspMatrixLrSlider", DspMatrixLrSlider);
                Add("DspMatrixRlSlider", DspMatrixRlSlider);
                Add("DspMatrixRrSlider", DspMatrixRrSlider);

                Add("DspPowerFir", DspPowerFir);
                Add("RoomIrEnabledToggle", RoomIrEnabledToggle);
                Add("RoomIrGainSlider", RoomIrGainSlider);
                Add("RoomTrimSlider", RoomTrimSlider);

                _webDspControls = map;
            }

            return _webDspControls.TryGetValue(name, out FrameworkElement? control) ? control : null;
        }

        private object[] WebDspDescribeModule(string module)
        {
            if (!WebDspModuleControls.TryGetValue(module, out string[]? names))
                return Array.Empty<object>();

            var result = new List<object>(names.Length);
            foreach (string name in names)
                result.AddRange(WebDspDescribeControl(name));
            return result.ToArray();
        }

        private IEnumerable<object> WebDspDescribeControl(string name)
        {
            if (ModelOwns(name))
            {
                yield return DescribeModelControl(name);
                yield break;
            }

            if (WebDspControl(name) is not FrameworkElement element)
                yield break;

            object? described = element switch
            {
                Slider slider => new
                {
                    name,
                    kind = "slider",
                    value = slider.Value,
                    min = slider.Minimum,
                    max = slider.Maximum,
                    step = slider.StepFrequency,
                    enabled = slider.IsEnabled
                },
                ToggleSwitch toggle => new
                {
                    name,
                    kind = "switch",
                    value = toggle.IsOn,
                    enabled = toggle.IsEnabled
                },
                CheckBox check => new
                {
                    name,
                    kind = "switch",
                    value = check.IsChecked == true,
                    enabled = check.IsEnabled
                },
                ComboBox combo => new
                {
                    name,
                    kind = "combo",
                    index = combo.SelectedIndex,
                    items = combo.Items.OfType<object>().Select(item => WebDspComboLabel(item)).ToArray(),
                    enabled = combo.IsEnabled
                },
                _ => null
            };

            if (described != null)
                yield return described;
        }

        /// <summary>ComboBox items may be plain strings or ComboBoxItem; both are shown by their text.</summary>
        private static string WebDspComboLabel(object item)
        {
            if (item is ComboBoxItem comboItem)
                return comboItem.Content?.ToString() ?? "";
            return item?.ToString() ?? "";
        }

        private void WebDspApplyControl(string name, JsonElement msg)
        {
            if (ModelOwns(name))
            {
                if (SetModelControl(name, msg))
                {
                    ProjectExtraParameters();
                    if (RgParameters.Owns(name)) ApplyReplayGainToEngine();
                    else if (SrcParameters.Owns(name)) ApplySrcSettings();
                    else ApplyDspToEngine();
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("缺少控件名。");
            if (WebDspControl(name) is not FrameworkElement element)
                throw new ArgumentException("控件不存在：" + name);

            switch (element)
            {
                case Slider slider:
                    if (msg.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.Number)
                        slider.Value = WebDspFinite(valueElement, slider.Minimum, slider.Maximum);
                    break;

                case ToggleSwitch toggle:
                    if (msg.TryGetProperty("value", out var toggleElement))
                        toggle.IsOn = toggleElement.ValueKind == JsonValueKind.True;
                    break;

                case CheckBox check:
                    if (msg.TryGetProperty("value", out var checkElement))
                        check.IsChecked = checkElement.ValueKind == JsonValueKind.True;
                    break;

                case ComboBox combo:
                    if (msg.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number)
                    {
                        int index = (int)indexElement.GetDouble();
                        if (index >= 0 && index < combo.Items.Count)
                            combo.SelectedIndex = index;
                    }
                    break;

                default:
                    throw new ArgumentException("这个控件不支持从网页修改：" + name);
            }
            if (name is "RoomIrEnabledToggle" or "RoomIrGainSlider" or "RoomTrimSlider" or "DspPowerFir")
                WebDspSaveRoom();
        }

        private static string ReadString(JsonElement msg, string property)
        {
            if (msg.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
                return element.GetString() ?? "";
            return "";
        }

        // ---- helpers shared by the three non-widget pages ------------------------------------

        /// <summary>
        /// The native DSP page only fills itself in when the user visits 音效处理 once:
        /// LoadAudioFxUiFromStore is what loads the store values into the sliders, populates the
        /// combos, sets EqState.Current, and flips _dspRackReady so the handlers stop ignoring input.
        /// A user who opens the web shell straight after launch never went there, so every slider
        /// the page shows would be a dead XAML default. Forcing the same load on first use keeps
        /// one code path instead of a second set of defaults.
        /// </summary>
        private bool _webDspForcedLoad;

        private void EnsureWebDspUiReady()
        {
            if (_dspRackReady || _webDspForcedLoad)
            {
                if (!_roomIrUiReady) LoadRoomCorrectionIntoPanel();
                return;
            }
            try
            {
                EnsureAudioFxUiBuilt();
                LoadAudioFxUiFromStore();
                LoadRoomCorrectionIntoPanel();
                SyncDspPowerSwitches();
                _webDspForcedLoad = true;
                StartupLog.Write("[WebDsp] 首次访问，已按原生路径加载音效面板");
            }
            catch (Exception error)
            {
                StartupLog.WriteException("MainWindow.WebDsp.EnsureUi", error);
                throw new InvalidOperationException("音效设置加载失败，请重试。", error);
            }
        }

        private object WebDspProfilesPayload()
        {
            var book = DspProfileStore.Load();
            string active = book.ActiveProfile ?? "";
            var rows = book.Profiles.Select(profile => new
            {
                name = profile.Name,
                active = string.Equals(profile.Name, active, StringComparison.OrdinalIgnoreCase),
                stamp = WebDspProfileStamp(profile.ModifiedUtc, profile.CreatedUtc),
                created = profile.CreatedUtc ?? "",
                modified = profile.ModifiedUtc ?? ""
            }).ToArray();

            return new
            {
                active,
                drifted = string.IsNullOrEmpty(active) && rows.Length > 0,
                count = rows.Length,
                profiles = rows
            };
        }

        private static string WebDspProfileStamp(string? modifiedUtc, string? createdUtc)
        {
            foreach (string? raw in new[] { modifiedUtc, createdUtc })
            {
                if (!string.IsNullOrWhiteSpace(raw)
                    && DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime stamp))
                {
                    return stamp.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            return "";
        }

        private object WebDspRackPayload()
        {
            var rack = DspRackStore.Load();
            int[] order = rack.RackOrder ?? Array.Empty<int>();
            int[] defaults = DefaultDspRackOrder();
            var modules = order.Select((id, index) => new
            {
                index,
                id,
                name = id >= 0 && id < RackModuleNames.Length ? RackModuleNames[id] : ("模块 " + id),
                hint = id >= 0 && id < RackModuleHints.Length ? RackModuleHints[id] : ""
            }).ToArray();

            return new
            {
                isDefault = order.SequenceEqual(defaults),
                defaultOrder = defaults,
                modules
            };
        }

        private static double WebDspToDb(float linear) =>
            linear <= 0.00001f ? -140.0 : 20.0 * Math.Log10(linear);

        private static double WebDspRound(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) ? -140.0 : Math.Round(value, 1);
    }
}
