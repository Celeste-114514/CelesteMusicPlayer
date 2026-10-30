using System;
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

    // Presentation adapter only. Guarded writes cannot feed back into the parameter model.
    private void ProjectExtraParameters()
    {
        bool loading = _audioFxLoading, syncing = _dspPowerSyncing;
        _audioFxLoading = true; _dspPowerSyncing = true;
        try
        {
            foreach (string name in WebDspModuleControls["channel"].Concat(WebDspModuleControls["headroom"]))
            {
                var descriptor = JsonSerializer.SerializeToElement(ExtraParameters.Describe(name));
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
    }

    // Legacy editors may still raise input events. Import only the field that changed,
    // rather than reconstructing all business parameters from controls on every EQ edit.
    private void ImportExtraParameterInput(object sender)
    {
        if (_audioFxLoading || !_audioFxPanelReady) return;
        foreach (string name in WebDspModuleControls["channel"].Concat(WebDspModuleControls["headroom"]))
        {
            if (name.StartsWith("DspPower",StringComparison.Ordinal) || !ReferenceEquals(WebDspControl(name), sender)) continue;
            var message = sender switch
            {
                Slider slider => JsonSerializer.SerializeToElement(new { value = slider.Value }),
                ToggleSwitch toggle => JsonSerializer.SerializeToElement(new { value = toggle.IsOn }),
                ComboBox combo => JsonSerializer.SerializeToElement(new { index = combo.SelectedIndex }),
                _ => default
            };
            if (message.ValueKind == JsonValueKind.Object) ExtraParameters.Set(name, message);
            return;
        }
    }
}
