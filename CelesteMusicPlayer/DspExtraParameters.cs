using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CelesteMusicPlayer;

/// <summary>Authoritative channel/safety parameters and web metadata. No control dependencies.
/// Channel.Enabled tracks the module switch; snapshots enable processing when crossfeed alone is on,
/// matching the existing storage contract.</summary>
internal sealed class DspExtraParameters : IDspParameterModel
{
    internal ChannelBalanceState Channel { get; private set; } = ChannelBalanceState.Default();
    internal DspSafetyState Safety { get; private set; } = DspSafetyState.Default();
    internal double HeadroomMemory { get; set; } = -3;
    internal double CrossfeedMemory { get; set; } = 25;
    private double _crossfeedLevel;
    private static readonly string[] MonoModes = ["off", "left", "right", "sum"];
    private static readonly string[] MonoLabels = ["关闭（立体声）", "只用左声道", "只用右声道", "左右求和"];
    internal void Load(DspExtraState state)
    {
        Channel = state.ChannelBalance.Clone();
        Safety = state.Safety.Clone();
        _crossfeedLevel = Channel.CrossfeedLevel;
    }
    internal DspExtraState Snapshot()
    {
        var channel = Channel.Clone();
        channel.Enabled = Channel.Enabled || Channel.CrossfeedEnabled;
        channel.CrossfeedLevel = (int)Math.Round(_crossfeedLevel);
        return new() { ChannelBalance = channel, Safety = Safety.Clone() };
    }
    private sealed record Numeric(double Min, double Max, double Step);
    private static readonly Dictionary<string, Numeric> Sliders = new()
    {
        ["AudioFxSafetyHeadroomSlider"] = new(-12,0,0.5),
        ["AudioFxChannelBalanceSlider"] = new(-1,1,0.05),
        ["AudioFxChannelLeftGainSlider"] = new(-12,12,1),
        ["AudioFxChannelRightGainSlider"] = new(-12,12,1),
        ["AudioFxChannelLeftDelaySlider"] = new(0,10,0.5),
        ["AudioFxChannelRightDelaySlider"] = new(0,10,0.5),
        ["AudioFxChannelCrossfeedSlider"] = new(0,100,5),
        ["AudioFxChannelCrossfeedCutoffSlider"] = new(100,4000,50)
    };
    public bool Owns(string name) => Sliders.ContainsKey(name) || name is
        "AudioFxChannelMonoCombo" or "AudioFxChannelToggle" or "AudioFxChannelSwapToggle" or
        "AudioFxChannelInvertLToggle" or "AudioFxChannelInvertRToggle" or "AudioFxChannelCrossfeedToggle" or
        "AudioFxSafetyLimiterToggle" or "DspPowerChannel" or "DspPowerHeadroom" or "DspPowerXfeed";
    private object Value(string name) => name switch
    {
        "AudioFxChannelToggle" or "DspPowerChannel" => Channel.Enabled,
        "AudioFxChannelSwapToggle" => Channel.SwapChannels,
        "AudioFxChannelInvertLToggle" => Channel.InvertLeft,
        "AudioFxChannelInvertRToggle" => Channel.InvertRight,
        "AudioFxChannelCrossfeedToggle" => Channel.CrossfeedEnabled,
        "AudioFxSafetyLimiterToggle" => Safety.EnableLimiter,
        "DspPowerHeadroom" => Math.Abs(Safety.HeadroomDb)>0.01,
        "DspPowerXfeed" => Channel.Enabled && Channel.CrossfeedEnabled,
        "AudioFxChannelBalanceSlider" => Channel.Balance,
        "AudioFxChannelLeftGainSlider" => Channel.LeftGainDb,
        "AudioFxChannelRightGainSlider" => Channel.RightGainDb,
        "AudioFxChannelLeftDelaySlider" => Channel.LeftDelayMs,
        "AudioFxChannelRightDelaySlider" => Channel.RightDelayMs,
        "AudioFxChannelCrossfeedSlider" => _crossfeedLevel,
        "AudioFxChannelCrossfeedCutoffSlider" => Channel.CrossfeedCutoffHz,
        "AudioFxSafetyHeadroomSlider" => Safety.HeadroomDb,
        "AudioFxChannelMonoCombo" => Array.IndexOf(MonoModes,Channel.MonoMode),
        _ => throw new ArgumentException(name)
    };
    public object Describe(string name)
    {
        if (Sliders.TryGetValue(name,out var d)) return new {name,kind="slider",value=(double)Value(name),min=d.Min,max=d.Max,step=d.Step,enabled=true};
        if (name=="AudioFxChannelMonoCombo") return new {name,kind="combo",index=(int)Value(name),items=MonoLabels,enabled=true};
        return new {name,kind="switch",value=(bool)Value(name),enabled=name!="DspPowerXfeed"||Channel.Enabled};
    }
    public bool Set(string name, JsonElement message)
    {
        if (!Owns(name)) throw new ArgumentException(name);
        string key=name=="AudioFxChannelMonoCombo"?"index":"value";
        if (!message.TryGetProperty(key,out var element)) return false;
        object next;
        if (Sliders.TryGetValue(name,out var d))
        {
            if (element.ValueKind!=JsonValueKind.Number) return false;
            double n=element.GetDouble();
            if (!double.IsFinite(n)) throw new ArgumentException("数值必须是有限数字。");
            next=Math.Clamp(n,d.Min,d.Max);
        }
        else if (key=="index")
        {
            if (element.ValueKind!=JsonValueKind.Number) return false;
            int i=(int)element.GetDouble();if(i<0||i>=MonoModes.Length)return false;next=i;
        }
        else next=element.ValueKind==JsonValueKind.True;
        if (Equals(Value(name),next)) return false;
        switch(name)
        {
            case "AudioFxChannelToggle":case "DspPowerChannel":Channel.Enabled=(bool)next;break;
            case "AudioFxChannelSwapToggle":Channel.SwapChannels=(bool)next;break;
            case "AudioFxChannelInvertLToggle":Channel.InvertLeft=(bool)next;break;
            case "AudioFxChannelInvertRToggle":Channel.InvertRight=(bool)next;break;
            case "AudioFxChannelCrossfeedToggle":Channel.CrossfeedEnabled=(bool)next;break;
            case "AudioFxSafetyLimiterToggle":Safety.EnableLimiter=(bool)next;break;
            case "AudioFxChannelBalanceSlider":Channel.Balance=(double)next;break;
            case "AudioFxChannelLeftGainSlider":Channel.LeftGainDb=(double)next;break;
            case "AudioFxChannelRightGainSlider":Channel.RightGainDb=(double)next;break;
            case "AudioFxChannelLeftDelaySlider":Channel.LeftDelayMs=(double)next;break;
            case "AudioFxChannelRightDelaySlider":Channel.RightDelayMs=(double)next;break;
            case "AudioFxChannelCrossfeedSlider":_crossfeedLevel=(double)next;break;
            case "AudioFxChannelCrossfeedCutoffSlider":Channel.CrossfeedCutoffHz=(double)next;break;
            case "AudioFxSafetyHeadroomSlider":Safety.HeadroomDb=(double)next;break;
            case "AudioFxChannelMonoCombo":Channel.MonoMode=MonoModes[(int)next];break;
            case "DspPowerHeadroom":
                if((bool)next) Safety.HeadroomDb=Math.Clamp(Math.Abs(HeadroomMemory)>0.01?HeadroomMemory:-3,-12,0);
                else {if(Math.Abs(Safety.HeadroomDb)>0.01)HeadroomMemory=Safety.HeadroomDb;Safety.HeadroomDb=0;}
                break;
            case "DspPowerXfeed":
                if((bool)next){Channel.Enabled=true;if(_crossfeedLevel<0.1)_crossfeedLevel=Math.Clamp(CrossfeedMemory>0.1?CrossfeedMemory:25,0,100);Channel.CrossfeedEnabled=true;}
                else {if(_crossfeedLevel>0.1)CrossfeedMemory=_crossfeedLevel;Channel.CrossfeedEnabled=false;}
                break;
        }
        Channel.CrossfeedLevel = (int)Math.Round(_crossfeedLevel);
        if(Math.Abs(Safety.HeadroomDb)>0.01)HeadroomMemory=Safety.HeadroomDb;
        return true;
    }
}
