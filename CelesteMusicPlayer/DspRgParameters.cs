using System;
using System.Text.Json;

namespace CelesteMusicPlayer;

/// <summary>Authoritative ReplayGain parameters and web metadata. No control dependencies.
/// Track/album gain and peak are per-file facts read from tags, deliberately not part of this model:
/// only the user's mode, preamp and clipping guard live here.
/// Preamp range matches the panel (-12..12); the store's Normalize() allows -24..24, so a hand-edited
/// file is clamped here exactly like the slider used to clamp it on load.</summary>
internal sealed class DspRgParameters
{
    internal ReplayGainState Current { get; private set; } = new();

    /// <summary>电源开关关掉前记住的模式下标（0 关闭，1 单曲，2 专辑）。</summary>
    internal int MemoryIndex { get; set; } = 1;

    private static readonly string[] ModeLabels = ["关闭", "单曲 (Track)", "专辑 (Album)"];

    internal void Load(ReplayGainState state)
    {
        Current = state.Clone();
        Current.PreampDb = Math.Clamp(Current.PreampDb, -12, 12);
        if (!Enum.IsDefined(Current.Mode)) Current.Mode = ReplayGainMode.Off;
    }

    internal ReplayGainState Snapshot() => Current.Clone();

    internal bool Owns(string name) => name is "DspPowerRg" or "AudioFxRgModeCombo"
        or "AudioFxRgPreampSlider" or "AudioFxRgPreventClippingToggle";

    private object Value(string name) => name switch
    {
        "DspPowerRg" => Current.Mode != ReplayGainMode.Off,
        "AudioFxRgModeCombo" => (int)Current.Mode,
        "AudioFxRgPreampSlider" => Current.PreampDb,
        "AudioFxRgPreventClippingToggle" => Current.PreventClipping,
        _ => throw new ArgumentException(name)
    };

    internal object Describe(string name) => name switch
    {
        "DspPowerRg" => new { name, kind = "switch", value = Current.Mode != ReplayGainMode.Off, enabled = true },
        "AudioFxRgModeCombo" => new { name, kind = "combo", index = (int)Current.Mode, items = ModeLabels, enabled = true },
        "AudioFxRgPreampSlider" => new { name, kind = "slider", value = Current.PreampDb, min = -12d, max = 12d, step = 0.5d, enabled = true },
        "AudioFxRgPreventClippingToggle" => new { name, kind = "switch", value = Current.PreventClipping, enabled = true },
        _ => throw new ArgumentException(name)
    };

    internal bool Set(string name, JsonElement message)
    {
        if (!Owns(name)) throw new ArgumentException(name);
        string key = name == "AudioFxRgModeCombo" ? "index" : "value";
        if (!message.TryGetProperty(key, out var element)) return false;

        object next;
        if (name == "AudioFxRgPreampSlider")
        {
            if (element.ValueKind != JsonValueKind.Number) return false;
            double n = element.GetDouble();
            if (!double.IsFinite(n)) throw new ArgumentException("数值必须是有限数字。");
            next = Math.Clamp(n, -12, 12);
        }
        else if (name == "AudioFxRgModeCombo")
        {
            if (element.ValueKind != JsonValueKind.Number) return false;
            int index = (int)element.GetDouble();
            if (index < 0 || index >= ModeLabels.Length) return false;
            next = index;
        }
        else next = element.ValueKind == JsonValueKind.True;

        if (Equals(Value(name), next)) return false;

        switch (name)
        {
            case "DspPowerRg":
                if ((bool)next)
                {
                    Current.Mode = MemoryIndex is >= 1 and <= 2 ? (ReplayGainMode)MemoryIndex : ReplayGainMode.Track;
                }
                else
                {
                    if (Current.Mode != ReplayGainMode.Off) MemoryIndex = (int)Current.Mode;
                    Current.Mode = ReplayGainMode.Off;
                }
                break;
            case "AudioFxRgModeCombo":
                Current.Mode = (ReplayGainMode)(int)next;
                if (Current.Mode != ReplayGainMode.Off) MemoryIndex = (int)Current.Mode;
                break;
            case "AudioFxRgPreampSlider":
                Current.PreampDb = (double)next;
                break;
            case "AudioFxRgPreventClippingToggle":
                Current.PreventClipping = (bool)next;
                break;
        }
        return true;
    }
}
