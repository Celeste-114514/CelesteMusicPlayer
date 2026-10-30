using System;
using System.Text.Json;

namespace CelesteMusicPlayer;

/// <summary>Authoritative sample-rate-conversion parameters and web metadata. No control dependencies.
/// Only the three user choices live here (target rate / quality / dither) plus the "last powered rate"
/// memory; the actual source rate, the output device capabilities and the automatic fallback are
/// per-playback facts owned by the engine, never by this model.</summary>
internal sealed class DspSrcParameters
{
    internal readonly record struct State(int TargetHz, string Quality, string Dither);

    internal sealed record RateOption(string Label, int Hz);
    internal sealed record KeyedOption(string Label, string Key, string Hint);

    /// <summary>可选升频目标采样率（Hz）。首项为「关闭」——0 表示原采样率直出。</summary>
    internal static readonly RateOption[] Rates =
    [
        new("关闭（原采样率）", 0),
        new("44.1 kHz", 44100),
        new("48 kHz", 48000),
        new("88.2 kHz", 88200),
        new("96 kHz", 96000),
        new("176.4 kHz", 176400),
        new("192 kHz", 192000),
    ];

    internal static readonly KeyedOption[] Qualities =
    [
        new("低延迟（省 CPU）", SrcOptions.QualityLowLatency, "低延迟"),
        new("均衡（推荐）", SrcOptions.QualityBalanced, "均衡"),
        new("透明（最高质量）", SrcOptions.QualityTransparent, "透明"),
    ];

    internal static readonly KeyedOption[] Dithers =
    [
        new("关闭", SrcOptions.DitherOff, "关闭"),
        new("TPDF 抖动", SrcOptions.DitherTpdf, "TPDF"),
        new("高通 TPDF", SrcOptions.DitherHighpass, "高通"),
        new("NS-5 噪声整形", SrcOptions.DitherNs5, "NS-5"),
    ];

    internal static readonly string[] RateLabels = Array.ConvertAll(Rates, r => r.Label);
    internal static readonly string[] QualityLabels = Array.ConvertAll(Qualities, r => r.Label);
    internal static readonly string[] DitherLabels = Array.ConvertAll(Dithers, r => r.Label);

    private const int DefaultMemoryIndex = 4;   // 96 kHz

    internal int TargetHz { get; private set; }

    internal string Quality { get; private set; } = SrcOptions.QualityBalanced;

    internal string Dither { get; private set; } = SrcOptions.DitherOff;

    /// <summary>电源开关关掉前记住的档位下标（0 = 关闭，不是可恢复的记忆）。</summary>
    internal int MemoryIndex { get; set; } = DefaultMemoryIndex;

    internal bool Enabled => TargetHz > 0;

    internal int RateIndex => IndexOfHz(TargetHz);

    internal int QualityIndex => IndexOfKey(Qualities, Quality, 1);

    internal int DitherIndex => IndexOfKey(Dithers, Dither, 0);

    internal KeyedOption QualityChoice => Qualities[QualityIndex];

    internal KeyedOption DitherChoice => Dithers[DitherIndex];

    /// <summary>未知采样率一律落到「关闭」，与旧下拉框的 IndexOfHz 口径一致。</summary>
    internal static int IndexOfHz(int hz)
    {
        for (int i = 0; i < Rates.Length; i++)
        {
            if (Rates[i].Hz == hz) return i;
        }

        return 0;
    }

    internal static int IndexOfKey(KeyedOption[] options, string? key, int fallback)
    {
        for (int i = 0; i < options.Length; i++)
        {
            if (string.Equals(options[i].Key, key, StringComparison.Ordinal)) return i;
        }

        return fallback;
    }

    /// <summary>从存档装载：未知采样率落回「关闭」，未知质量/抖动落回默认档（同旧 IndexOfKey 的 fallback）。</summary>
    internal void Load(int targetHz, string? quality, string? dither)
    {
        TargetHz = Rates[IndexOfHz(targetHz)].Hz;
        Quality = Qualities[IndexOfKey(Qualities, quality, 1)].Key;
        Dither = Dithers[IndexOfKey(Dithers, dither, 0)].Key;
    }

    internal State Snapshot()
    {
        return new State(TargetHz, Quality, Dither);
    }

    internal bool Owns(string name) => name is "DspPowerSrc" or "SrcRateCombo"
        or "SrcQualityCombo" or "SrcDitherCombo";

    internal object Describe(string name) => name switch
    {
        "DspPowerSrc" => new { name, kind = "switch", value = Enabled, enabled = true },
        "SrcRateCombo" => new { name, kind = "combo", index = RateIndex, items = RateLabels, enabled = true },
        "SrcQualityCombo" => new { name, kind = "combo", index = QualityIndex, items = QualityLabels, enabled = true },
        "SrcDitherCombo" => new { name, kind = "combo", index = DitherIndex, items = DitherLabels, enabled = true },
        _ => throw new ArgumentException(name)
    };

    internal bool Set(string name, JsonElement message)
    {
        if (!Owns(name)) throw new ArgumentException(name);

        if (name == "DspPowerSrc")
        {
            if (!message.TryGetProperty("value", out var flag)) return false;
            bool on = flag.ValueKind == JsonValueKind.True;
            if (Enabled == on) return false;
            if (on)
            {
                int index = MemoryIndex >= 1 && MemoryIndex < Rates.Length ? MemoryIndex : DefaultMemoryIndex;
                TargetHz = Rates[index].Hz;
            }
            else
            {
                if (Enabled) MemoryIndex = IndexOfHz(TargetHz);
                TargetHz = 0;
            }

            return true;
        }

        if (!message.TryGetProperty("index", out var element) || element.ValueKind != JsonValueKind.Number) return false;
        int wanted = (int)element.GetDouble();

        switch (name)
        {
            case "SrcRateCombo":
                if (wanted < 0 || wanted >= Rates.Length || Rates[wanted].Hz == TargetHz) return false;
                TargetHz = Rates[wanted].Hz;
                if (wanted > 0) MemoryIndex = wanted;
                return true;
            case "SrcQualityCombo":
                if (wanted < 0 || wanted >= Qualities.Length || Qualities[wanted].Key == Quality) return false;
                Quality = Qualities[wanted].Key;
                return true;
            case "SrcDitherCombo":
                if (wanted < 0 || wanted >= Dithers.Length || Dithers[wanted].Key == Dither) return false;
                Dither = Dithers[wanted].Key;
                return true;
        }

        return false;
    }
}
