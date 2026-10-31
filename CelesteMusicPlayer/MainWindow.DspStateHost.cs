using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer;

public sealed partial class MainWindow
{
    // One editing session for web DSP, device profiles and the legacy presentation adapter.
    private readonly EqEditSession EqState = new();
    private readonly DspApplyService DspApply = new();
    private readonly DspExtraParameters ExtraParameters = new();
    private readonly DspRgParameters RgParameters = new();
    private readonly DspSrcParameters SrcParameters = new();
    private readonly DspCompParameters CompParameters = new();
    private readonly DspFieldParameters FieldParameters = new();
    private readonly DspMatrixParameters MatrixParameters = new();
    private readonly DspFirParameters FirParameters = new();
    private readonly DspRackParameters RackParameters = new();
    private readonly DspProfileCoordinator ProfileCoordinator = new();

    /// <summary>Modules whose business values already live in a model instead of their controls.</summary>
    private IEnumerable<string> ProjectedControls => WebDspModuleControls["channel"]
        .Concat(WebDspModuleControls["headroom"]).Concat(WebDspModuleControls["rg"])
        .Concat(WebDspModuleControls["src"]).Concat(WebDspModuleControls["comp"])
        .Concat(WebDspModuleControls["stereo"]).Concat(WebDspModuleControls["matrix"])
        .Concat(WebDspModuleControls["fir"]);

    /// <summary>Which model owns a control name (null = still on the legacy control adapter).</summary>
    private IDspParameterModel? ModelOf(string name)
    {
        if (ExtraParameters.Owns(name)) return ExtraParameters;
        if (RgParameters.Owns(name)) return RgParameters;
        if (SrcParameters.Owns(name)) return SrcParameters;
        if (CompParameters.Owns(name)) return CompParameters;
        if (FieldParameters.Owns(name)) return FieldParameters;
        if (MatrixParameters.Owns(name)) return MatrixParameters;
        return FirParameters.Owns(name) ? FirParameters : null;
    }

    private bool ModelOwns(string name) => ModelOf(name) != null;

    private object DescribeModelControl(string name) =>
        (ModelOf(name) ?? throw new ArgumentException(name)).Describe(name);

    private bool SetModelControl(string name, JsonElement message) =>
        (ModelOf(name) ?? throw new ArgumentException(name)).Set(name, message);

    private static JsonElement SwitchMessage(bool value) => JsonSerializer.SerializeToElement(new { value });

    private static JsonElement NumberMessage(double value) => JsonSerializer.SerializeToElement(new { value });

    // Presentation adapter only. Guarded writes cannot feed back into the parameter model.
    private void ProjectExtraParameters()
    {
        bool loading = _audioFxLoading, syncing = _dspPowerSyncing;
        _audioFxLoading = true; _dspPowerSyncing = true;
        try
        {
            foreach (string name in ProjectedControls)
            {
                if (!ModelOwns(name)) continue;
                var descriptor = JsonSerializer.SerializeToElement(DescribeModelControl(name));
                switch (WebDspControl(name))
                {
                    case Slider slider: slider.Value = descriptor.GetProperty("value").GetDouble(); break;
                    case ToggleSwitch toggle: toggle.IsOn = descriptor.GetProperty("value").GetBoolean(); break;
                    case ComboBox combo: combo.SelectedIndex = descriptor.GetProperty("index").GetInt32(); break;
                }
            }
        }
        finally { _audioFxLoading = loading; _dspPowerSyncing = syncing; }
        AudioFxSafetyHeadroomLabel.Text = "余量 (dB)：" + FormatHelper.FormatAudioFxDb(ExtraParameters.Safety.HeadroomDb);
        RefreshCrossfeedCutoffReadout(); RefreshDspXfeedLevelReadout(); RedrawDspXfeedCurve(); UpdateDspChannelBars();
        AudioFxRgPreampLabel.Text = "额外增益 (dB)：" + FormatHelper.FormatAudioFxDb(RgParameters.Current.PreampDb);
        RefreshAudioFxRgInfo();
        RefreshFirReadouts();
    }

    // Legacy editors may still raise input events. Import only the field that changed,
    // rather than reconstructing all business parameters from controls on every EQ edit.
    private void ImportExtraParameterInput(object sender)
    {
        if (_audioFxLoading || !_audioFxPanelReady) return;
        foreach (string name in ProjectedControls)
        {
            if (name.StartsWith("DspPower", StringComparison.Ordinal) || !ModelOwns(name)) continue;
            if (!ReferenceEquals(WebDspControl(name), sender)) continue;
            var message = sender switch
            {
                Slider slider => JsonSerializer.SerializeToElement(new { value = slider.Value }),
                ToggleSwitch toggle => JsonSerializer.SerializeToElement(new { value = toggle.IsOn }),
                ComboBox combo => JsonSerializer.SerializeToElement(new { index = combo.SelectedIndex }),
                _ => default
            };
            if (message.ValueKind == JsonValueKind.Object) SetModelControl(name, message);
            return;
        }


    }

    // DSP 装载中旗标（2026-10-31 从 MainWindow.EqDsp.cs 搬来）：DspStateHost 投影网页状态在读写。
    private bool _audioFxLoading;
    // DSP 电源开关防回环旗标（2026-10-31 从 MainWindow.DspPower.cs 搬来）：同上，DspStateHost 在读写。
    private bool _dspPowerSyncing;

}
