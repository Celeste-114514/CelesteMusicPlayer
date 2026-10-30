using System;
using System.IO;
using System.Text.Json;

namespace CelesteMusicPlayer;

/// <summary>Authoritative room-correction (convolution FIR) parameters and web metadata.
/// No control dependencies: the IR file itself is chosen by the host (file picker / clear) and handed
/// to this model through <see cref="SetIr"/> or <see cref="ClearIr"/> — the model never opens a dialog.
/// Enablement is guarded here too: convolution without an IR would only burn CPU and look enabled.</summary>
internal sealed class DspFirParameters : IDspParameterModel
{
    private const double GainMin = -12.0;
    private const double GainMax = 12.0;
    private const double TrimMin = -24.0;
    private const double TrimMax = 6.0;
    private const double Step = 0.5;

    internal RoomCorrectionState Current { get; private set; } = new();

    /// <summary>An IR has been chosen. This is what makes the page power switch usable.</summary>
    internal bool HasIr => !string.IsNullOrWhiteSpace(Current.IrPath);

    /// <summary>Enabled with an IR present: the reading the page power switch and the bit-perfect badge use.</summary>
    internal bool Effective => Current.Enabled && HasIr;

    /// <summary>The IR file is on disk right now; convolution can only actually run on this.</summary>
    internal bool IrReady => HasIr && File.Exists((Current.IrPath ?? string.Empty).Trim());

    internal void Load(RoomCorrectionState? state)
    {
        Current = state?.Clone() ?? new RoomCorrectionState();
        Current.GainDb = Math.Clamp(Current.GainDb, GainMin, GainMax);
        Current.TrimDb = Math.Round(Math.Clamp(Current.TrimDb, TrimMin, TrimMax), 1);
    }

    internal RoomCorrectionState Snapshot() => Current.Clone();

    /// <summary>Stores a chosen IR path (and its display name). Metadata comes separately.</summary>
    internal void SetIr(string? path)
    {
        string trimmed = (path ?? string.Empty).Trim();
        Current.IrPath = trimmed;
        Current.IrName = trimmed.Length == 0 ? string.Empty : Path.GetFileName(trimmed);
    }

    /// <summary>Writes what the loader actually read: taps / source rate / channel count.</summary>
    internal void SetIrMetadata(int taps, int sampleRate, int channels)
    {
        Current.IrTapCount = taps;
        Current.IrSampleRate = sampleRate;
        Current.IrChannels = channels;
    }

    /// <summary>Drops the IR; the convolution is switched off with it because an empty IR cannot run.</summary>
    internal void ClearIr()
    {
        SetIr(string.Empty);
        SetIrMetadata(0, 0, 0);
        Current.Enabled = false;
    }

    public bool Owns(string name) =>
        name is "DspPowerFir" or "RoomIrEnabledToggle" or "RoomIrGainSlider" or "RoomTrimSlider";

    public object Describe(string name)
    {
        if (!Owns(name)) throw new ArgumentException(name);

        // 页头电源开关：没有 IR 时原生把它禁用（提示「先导入脉冲响应 (IR)」），enabled 跟着这个口径
        if (name == "DspPowerFir")
        {
            return new { name, kind = "switch", value = Effective, enabled = HasIr };
        }

        if (name == "RoomIrEnabledToggle")
        {
            return new { name, kind = "switch", value = Current.Enabled, enabled = true };
        }

        if (name == "RoomIrGainSlider")
        {
            return new { name, kind = "slider", value = Current.GainDb, min = GainMin, max = GainMax, step = Step, enabled = true };
        }

        return new { name, kind = "slider", value = Current.TrimDb, min = TrimMin, max = TrimMax, step = Step, enabled = true };
    }

    public bool Set(string name, JsonElement message)
    {
        if (!Owns(name)) throw new ArgumentException(name);

        if (name is "DspPowerFir" or "RoomIrEnabledToggle")
        {
            if (!message.TryGetProperty("value", out var flag)) return false;
            bool on = flag.ValueKind == JsonValueKind.True;
            if (Current.Enabled == on) return false;

            if (!on)
            {
                Current.Enabled = false;
                return true;
            }

            if (!IrReady)
            {
                // 模块内开关（网页 / 面板）：没有可用 IR 就明确拒绝。旧版会先把开关画成开、再在保存时报错，
                // 结果界面显示已启用而存档没变、重启又回到关 —— 这个半改状态不再出现。
                if (string.Equals(name, "RoomIrEnabledToggle", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("请先导入有效的脉冲响应文件。");
                }

                // 页头电源开关：没有 IR 时它本来就是禁用的，拨不动就静默忽略（与旧行为一致）
                return false;
            }

            Current.Enabled = true;
            return true;
        }

        if (!message.TryGetProperty("value", out var element) || element.ValueKind != JsonValueKind.Number) return false;
        double wanted = element.GetDouble();
        if (!double.IsFinite(wanted)) throw new ArgumentException("数值必须是有限数字。");

        bool trim = string.Equals(name, "RoomTrimSlider", StringComparison.Ordinal);
        double value = Math.Clamp(wanted, trim ? TrimMin : GainMin, trim ? TrimMax : GainMax);
        // trim 旧滑杆 handler 落盘前取 Math.Round(v,1)；增益不取整（旧「应用」按钮才取整）
        if (trim) value = Math.Round(value, 1);

        double current = trim ? Current.TrimDb : Current.GainDb;
        if (Math.Abs(current - value) < 1e-9) return false;

        if (trim) Current.TrimDb = value;
        else Current.GainDb = value;
        return true;
    }
}
