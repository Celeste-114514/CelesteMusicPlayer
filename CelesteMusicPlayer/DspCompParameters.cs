using System;
using System.Text.Json;

namespace CelesteMusicPlayer;

/// <summary>Authoritative compressor parameters and web metadata. No control dependencies.
/// Only the user's choices live here; the live gain reduction, input level and GR history are
/// per-playback meter facts owned by the engine and drawn by the compressor page.</summary>
internal sealed class DspCompParameters
{
    private sealed record SliderSpec(string Name, double Min, double Max, double Step);

    // 与旧滑杆一一对应（范围/步长取自 LegacyBusinessState 里给滑杆设的值），顺序即 Describe 取用顺序。
    private static readonly SliderSpec[] Sliders =
    [
        new("DspCompThresholdSlider", -60.0, 0.0, 0.5),
        new("DspCompRatioSlider", 1.0, 20.0, 0.1),
        new("DspCompAttackSlider", 0.1, 200.0, 0.1),
        new("DspCompReleaseSlider", 5.0, 2000.0, 5.0),
        new("DspCompKneeSlider", 0.0, 24.0, 0.5),
        new("DspCompMakeupSlider", -12.0, 24.0, 0.5),
        new("DspCompMixSlider", 0.0, 100.0, 1.0),
    ];

    internal CompressorSettings Current { get; private set; } = new();

    /// <summary>从存档装载：超出滑杆范围的旧值按旧滑杆的口径钳回来。</summary>
    internal void Load(CompressorSettings state)
    {
        Current = state.Clone();
        foreach (SliderSpec spec in Sliders)
        {
            SetValue(spec.Name, Math.Clamp(GetValue(spec.Name), spec.Min, spec.Max));
        }
    }

    internal CompressorSettings Snapshot() => Current.Clone();

    /// <summary>压缩器页里归模型管的控件。表头「峰值/GR」显示模式不属于 DSP 参数，仍走旧适配。</summary>
    internal bool Owns(string name) => name is "DspPowerComp" or "DspCompToggle" || Spec(name) != null;

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
        "DspCompThresholdSlider" => Current.ThresholdDb,
        "DspCompRatioSlider" => Current.Ratio,
        "DspCompAttackSlider" => Current.AttackMs,
        "DspCompReleaseSlider" => Current.ReleaseMs,
        "DspCompKneeSlider" => Current.KneeDb,
        "DspCompMakeupSlider" => Current.MakeupDb,
        "DspCompMixSlider" => Current.Mix * 100.0,
        _ => throw new ArgumentException(name)
    };

    private void SetValue(string name, double value)
    {
        switch (name)
        {
            case "DspCompThresholdSlider": Current.ThresholdDb = value; break;
            case "DspCompRatioSlider": Current.Ratio = value; break;
            case "DspCompAttackSlider": Current.AttackMs = value; break;
            case "DspCompReleaseSlider": Current.ReleaseMs = value; break;
            case "DspCompKneeSlider": Current.KneeDb = value; break;
            case "DspCompMakeupSlider": Current.MakeupDb = value; break;
            case "DspCompMixSlider": Current.Mix = value / 100.0; break;
            default: throw new ArgumentException(name);
        }
    }

    internal object Describe(string name)
    {
        if (name is "DspPowerComp" or "DspCompToggle")
        {
            return new { name, kind = "switch", value = Current.Enabled, enabled = true };
        }

        SliderSpec spec = Spec(name) ?? throw new ArgumentException(name);
        return new { name, kind = "slider", value = GetValue(name), min = spec.Min, max = spec.Max, step = spec.Step, enabled = true };
    }

    internal bool Set(string name, JsonElement message)
    {
        if (!Owns(name)) throw new ArgumentException(name);

        if (name is "DspPowerComp" or "DspCompToggle")
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
