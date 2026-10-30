using System;
using System.Text.Json;

namespace CelesteMusicPlayer;

/// <summary>Authoritative stereo-field parameters and web metadata. No control dependencies.
/// Width is kept as the engine's 0..2 ratio and only shown as a percentage; the ring drawing and
/// the preset buttons are presentation, they read this model and write through it.</summary>
internal sealed class DspFieldParameters : IDspParameterModel
{
    private sealed record SliderSpec(string Name, double Min, double Max, double Step);

    private static readonly SliderSpec[] Sliders =
    [
        new("DspFieldWidthSlider", 0.0, 200.0, 1.0),
        new("DspFieldCenterSlider", -18.0, 18.0, 0.5),
        new("DspFieldSideSlider", -18.0, 18.0, 0.5),
    ];

    internal StereoFieldSettings Current { get; private set; } = new();

    internal void Load(StereoFieldSettings state)
    {
        Current = state.Clone();
        foreach (SliderSpec spec in Sliders)
        {
            SetValue(spec.Name, Math.Clamp(GetValue(spec.Name), spec.Min, spec.Max));
        }
    }

    internal StereoFieldSettings Snapshot() => Current.Clone();

    public bool Owns(string name) => name is "DspPowerField" or "DspFieldToggle" || Spec(name) != null;

    private static SliderSpec? Spec(string name)
    {
        foreach (SliderSpec spec in Sliders)
        {
            if (string.Equals(spec.Name, name, StringComparison.Ordinal)) return spec;
        }

        return null;
    }

    private double GetValue(string name) => name switch
    {
        "DspFieldWidthSlider" => Current.Width * 100.0,
        "DspFieldCenterSlider" => Current.CenterGainDb,
        "DspFieldSideSlider" => Current.SideGainDb,
        _ => throw new ArgumentException(name)
    };

    private void SetValue(string name, double value)
    {
        switch (name)
        {
            case "DspFieldWidthSlider": Current.Width = value / 100.0; break;
            case "DspFieldCenterSlider": Current.CenterGainDb = value; break;
            case "DspFieldSideSlider": Current.SideGainDb = value; break;
            default: throw new ArgumentException(name);
        }
    }

    public object Describe(string name)
    {
        if (name is "DspPowerField" or "DspFieldToggle")
        {
            return new { name, kind = "switch", value = Current.Enabled, enabled = true };
        }

        SliderSpec spec = Spec(name) ?? throw new ArgumentException(name);
        return new { name, kind = "slider", value = GetValue(name), min = spec.Min, max = spec.Max, step = spec.Step, enabled = true };
    }

    public bool Set(string name, JsonElement message)
    {
        if (!Owns(name)) throw new ArgumentException(name);

        if (name is "DspPowerField" or "DspFieldToggle")
        {
            if (!message.TryGetProperty("value", out var flag)) return false;
            bool on = flag.ValueKind == JsonValueKind.True;
            if (Current.Enabled == on) return false;
            Current.Enabled = on;
            return true;
        }

        if (!message.TryGetProperty("value", out var element) || element.ValueKind != JsonValueKind.Number) return false;
        double wanted = element.GetDouble();
        if (!double.IsFinite(wanted)) throw new ArgumentException("数值必须是有限数字。");

        SliderSpec target = Spec(name) ?? throw new ArgumentException(name);
        double clamped = Math.Clamp(wanted, target.Min, target.Max);
        if (Math.Abs(GetValue(name) - clamped) < 1e-9) return false;
        SetValue(name, clamped);
        return true;
    }
}
