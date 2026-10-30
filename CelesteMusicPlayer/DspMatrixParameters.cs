using System;
using System.Text.Json;

namespace CelesteMusicPlayer;

/// <summary>Authoritative channel-matrix parameters and web metadata. No control dependencies.
/// The four coefficients are the engine's raw gains (-2..2); presets write through this model.</summary>
internal sealed class DspMatrixParameters : IDspParameterModel
{
    private const double MinGain = -2.0;
    private const double MaxGain = 2.0;
    private const double Step = 0.05;

    private static readonly string[] Names =
    [
        "DspMatrixLlSlider", "DspMatrixRlSlider", "DspMatrixLrSlider", "DspMatrixRrSlider",
    ];

    internal ChannelMatrixSettings Current { get; private set; } = new();

    internal void Load(ChannelMatrixSettings state)
    {
        Current = state.Clone();
        foreach (string name in Names)
        {
            SetValue(name, Math.Clamp(GetValue(name), MinGain, MaxGain));
        }
    }

    internal ChannelMatrixSettings Snapshot() => Current.Clone();

    public bool Owns(string name) => name is "DspPowerMatrix" or "DspMatrixToggle" || Index(name) >= 0;

    private static int Index(string name)
    {
        for (int i = 0; i < Names.Length; i++)
        {
            if (string.Equals(Names[i], name, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    private double GetValue(string name) => name switch
    {
        "DspMatrixLlSlider" => Current.LeftToLeft,
        "DspMatrixRlSlider" => Current.RightToLeft,
        "DspMatrixLrSlider" => Current.LeftToRight,
        "DspMatrixRrSlider" => Current.RightToRight,
        _ => throw new ArgumentException(name)
    };

    private void SetValue(string name, double value)
    {
        switch (name)
        {
            case "DspMatrixLlSlider": Current.LeftToLeft = value; break;
            case "DspMatrixRlSlider": Current.RightToLeft = value; break;
            case "DspMatrixLrSlider": Current.LeftToRight = value; break;
            case "DspMatrixRrSlider": Current.RightToRight = value; break;
            default: throw new ArgumentException(name);
        }
    }

    public object Describe(string name)
    {
        if (name is "DspPowerMatrix" or "DspMatrixToggle")
        {
            return new { name, kind = "switch", value = Current.Enabled, enabled = true };
        }

        if (Index(name) < 0) throw new ArgumentException(name);
        return new { name, kind = "slider", value = GetValue(name), min = MinGain, max = MaxGain, step = Step, enabled = true };
    }

    public bool Set(string name, JsonElement message)
    {
        if (!Owns(name)) throw new ArgumentException(name);

        if (name is "DspPowerMatrix" or "DspMatrixToggle")
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

        double clamped = Math.Clamp(wanted, MinGain, MaxGain);
        if (Math.Abs(GetValue(name) - clamped) < 1e-9) return false;
        SetValue(name, clamped);
        return true;
    }
}
