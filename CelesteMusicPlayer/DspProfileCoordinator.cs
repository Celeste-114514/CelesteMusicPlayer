using System;
using System.Globalization;
using System.Text;

namespace CelesteMusicPlayer;

/// <summary>"当前这一套设置属于哪份听音方案"的判定（2026-10-31 从 MainWindow.DspProfiles.cs 搬出）。
///
/// 判定不靠各模块 handler 上报，而是拿方案内容指纹和当前设置比对：任何入口改了 DSP 设置都能被发现。
/// 发现偏离就地降级为「手动设置」，并记住是从哪份方案改出去的（只用于页面提示，不落盘）。
/// 这里只管状态与比对；清盘上的 ActiveProfile、刷新界面文本仍由宿主做。</summary>
internal sealed class DspProfileCoordinator
{
    /// <summary>当前生效方案名（空 = 手动设置）。</summary>
    internal string ActiveName { get; private set; } = string.Empty;

    /// <summary>刚脱离的那份方案名：只用于页面提示「已从方案 X 改出去」。</summary>
    internal string LeftName { get; private set; } = string.Empty;

    /// <summary>生效方案的内容指纹（自盘上快照算出）。空 = 当前无生效方案。</summary>
    internal string RefKey { get; private set; } = string.Empty;

    /// <summary>用盘上的 ActiveProfile 与该方案的内容重建判定状态。</summary>
    internal void Load(string? activeName, string? refKey)
    {
        ActiveName = activeName ?? string.Empty;
        RefKey = refKey ?? string.Empty;
    }

    /// <summary>应用了一份方案：它是当前设置的来源，指纹换成这份方案自己的。</summary>
    internal void Apply(string name, string refKey)
    {
        ActiveName = name ?? string.Empty;
        LeftName = string.Empty;
        RefKey = refKey ?? string.Empty;
    }

    /// <summary>没有生效方案了（删除 / 清空）：连同"从哪改出去"一起忘掉。</summary>
    internal void Clear()
    {
        ActiveName = string.Empty;
        LeftName = string.Empty;
        RefKey = string.Empty;
    }

    /// <summary>
    /// 拿当前设置重新比对：偏离了就把状态降级为「手动设置」并返回 true（宿主据此清盘上标记 + 刷界面）。
    /// 没有生效方案、或指纹还没算出来时返回 false；比对本身出错也按"没偏离"处理（不让一次异常把方案踢掉）。
    /// </summary>
    internal bool Refresh(Func<DspProfile> capture)
    {
        if (ActiveName.Length == 0 || RefKey.Length == 0)
        {
            return false;
        }

        try
        {
            if (string.Equals(RefKey, Fingerprint(capture()), StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        LeftName = ActiveName;
        ActiveName = string.Empty;
        RefKey = string.Empty;
        return true;
    }

    /// <summary>
    /// 方案内容指纹：只取"用户可调的设置值"，用于判断当前设置是否还等于某份方案。
    /// 刻意不含 IR 的 taps / 采样率 / 声道数这类"加载后回写"字段 —— 它们会被播放过程改写，
    /// 放进来会让刚应用完的方案立刻被误判成"已改动"。
    /// </summary>
    internal static string Fingerprint(DspProfile p)
    {
        var b = new StringBuilder(1024);
        void Num(double v) => b.Append(v.ToString("0.####", CultureInfo.InvariantCulture)).Append('|');
        void Flag(bool v) => b.Append(v ? '1' : '0').Append('|');

        // 机架：顺序 + 压缩器 + 立体声场 + 声道矩阵
        RackState? rack = p.Rack;
        if (rack?.RackOrder != null)
        {
            foreach (int id in rack.RackOrder)
            {
                b.Append(id).Append(',');
            }
        }

        b.Append('|');
        if (rack?.Compressor is { } c)
        {
            Flag(c.Enabled); Num(c.ThresholdDb); Num(c.Ratio); Num(c.AttackMs);
            Num(c.ReleaseMs); Num(c.KneeDb); Num(c.MakeupDb); Num(c.Mix);
        }

        if (rack?.StereoField is { } f)
        {
            Flag(f.Enabled); Num(f.Width); Num(f.CenterGainDb); Num(f.SideGainDb);
        }

        if (rack?.Matrix is { } m)
        {
            Flag(m.Enabled); Num(m.LeftToLeft); Num(m.RightToLeft); Num(m.LeftToRight); Num(m.RightToRight);
        }

        // EQ 曲线（含耳机校正写进来的那条）
        if (p.Eq is { } eq)
        {
            Flag(eq.Enabled); Num(eq.PreampDb); b.Append(eq.PresetId).Append('|');
            foreach (EqBand band in eq.Bands)
            {
                Flag(band.Enabled); Num(band.FrequencyHz); Num(band.GainDb); Num(band.Q);
                b.Append((int)band.FilterType).Append('|');
            }
        }

        // 声道工具 + 输出安全
        if (p.Extra?.ChannelBalance is { } ch)
        {
            Flag(ch.Enabled); Num(ch.Balance); Num(ch.LeftGainDb); Num(ch.RightGainDb);
            Flag(ch.InvertLeft); Flag(ch.InvertRight); Flag(ch.SwapChannels);
            b.Append(ch.MonoMode).Append('|');
            Num(ch.LeftDelayMs); Num(ch.RightDelayMs);
            Flag(ch.CrossfeedEnabled); b.Append(ch.CrossfeedLevel).Append('|'); Num(ch.CrossfeedCutoffHz);
        }

        if (p.Extra?.Safety is { } safety)
        {
            Num(safety.HeadroomDb); Flag(safety.EnableLimiter);
        }

        // ReplayGain
        if (p.ReplayGain is { } rg)
        {
            b.Append((int)rg.Mode).Append('|'); Num(rg.PreampDb); Flag(rg.PreventClipping);
        }

        // 房间校正：只比用户设定 + IR 路径（长度/采样率是加载后回写的，见方法注释）
        if (p.Room is { } room)
        {
            Flag(room.Enabled); b.Append(room.IrPath).Append('|'); Num(room.GainDb); Num(room.TrimDb);
        }

        // 简单模式四滑杆
        if (p.SimpleEq is { } se)
        {
            Num(se.Bass); Num(se.Vocal); Num(se.Air); Num(se.Warm);
        }

        // 升频三件套
        b.Append(p.SrcTargetHz).Append('|').Append(p.SrcQuality).Append('|').Append(p.SrcDither);
        return b.ToString();
    }
}
