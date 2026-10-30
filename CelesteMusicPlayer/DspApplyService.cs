using System;

namespace CelesteMusicPlayer;

internal interface IDspParameterTarget
{
    void SetEqCurve(EqCurveState? curve);
    void SetChannelBalance(ChannelBalanceState? state);
    void SetSafety(DspSafetyState? state);
    void SetRoomCorrection(RoomCorrectionState? state);
}

/// <summary>Persist and deliver explicit DSP parameters. Presentation and initialization guards
/// belong to the caller. A/B previews skip EQ persistence but still reach the output.</summary>
internal sealed class DspApplyService
{
    private readonly Action<EqCurveState> _saveEq;
    private readonly Action<DspExtraState> _saveExtra;
    private readonly Func<RoomCorrectionState> _loadRoom;
    internal DspApplyService() : this(EqCurveStore.Save, DspExtraStore.Save, RoomCorrectionStore.Load) { }
    internal DspApplyService(Action<EqCurveState> saveEq, Action<DspExtraState> saveExtra, Func<RoomCorrectionState> loadRoom)
    {
        _saveEq = saveEq;
        _saveExtra = saveExtra;
        _loadRoom = loadRoom;
    }
    internal void Apply(EqCurveState curve, bool previewing, DspExtraState extra, IDspParameterTarget? output)
    {
        if (!previewing) _saveEq(curve);
        _saveExtra(extra);
        output?.SetEqCurve(curve);
        output?.SetChannelBalance(extra.ChannelBalance);
        output?.SetSafety(extra.Safety);
        if (output != null) output.SetRoomCorrection(_loadRoom());
    }
}
