using CelesteMusicPlayer;
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
DateTime now = new(2026, 1, 1);
var state = new EqEditSession(() => now);
Check(!state.Undo() && !state.Redo(), "empty history");
state.Current.PreampDb = -1;
state.PushUndo();
state.Current.PreampDb = -2;
Check(state.Undo() && state.Current.PreampDb == -1, "deep undo snapshot");
Check(state.Redo() && state.Current.PreampDb == -2, "redo current");
now = now.AddMilliseconds(399);
Check(!state.PushUndo(true), "399ms coalesces");
now = now.AddMilliseconds(399);
Check(!state.PushUndo(true), "sliding window");
now = now.AddMilliseconds(400);
Check(state.PushUndo(true), "400ms new snapshot");
Check(!state.CanRedo, "new edit clears redo");
for (int i=0;i<60;i++) {state.PushUndo();state.Current.PreampDb=i;}
Check(state.UndoCount==40, "40 snapshot limit");
Check(state.ToggleAb(false)==EqAbTransition.Armed && state.AbArmed && !state.Previewing, "arm without preview");
double a=state.Current.PreampDb;
state.Current.PreampDb=-4;
Check(state.ToggleAb(false)==EqAbTransition.ShowingA && state.Previewing && state.Current.PreampDb==a, "A isolates B");
state.Current.PreampDb=-8;
Check(state.ToggleAb(false)==EqAbTransition.ShowingB && !state.Previewing && state.Current.PreampDb==-4, "return B");
Check(state.ToggleAb(false)==EqAbTransition.ShowingA && state.Current.PreampDb==-8, "retain edits in A");
state.PushUndo();state.Current.PreampDb=-9;
Check(state.Undo() && !state.Previewing && state.AbArmed, "undo exits preview keeps reference");
Check(state.ShowingA, "legacy showing-A flag retained across undo");
Check(state.Redo() && !state.Previewing, "redo exits preview");
var before=state.Current;
Check(state.ToggleAb(true)==EqAbTransition.Cancelled && !state.AbArmed && !state.ShowingA && !state.Previewing && ReferenceEquals(before,state.Current), "cancel retains current");
Check(state.ToggleAb(true)==EqAbTransition.Armed, "cancel flag without armed reference arms");
Console.WriteLine($"PASS {checks} EQ editing checks");

var calls = new List<string>();
var curve = EqCurveState.Default();
var extra = new DspExtraState();
var room = new RoomCorrectionState();
var apply = new DspApplyService(c => { Check(ReferenceEquals(c, curve), "persist selected curve"); calls.Add("saveEq"); },
    e => { Check(ReferenceEquals(e,extra), "persist explicit extra"); calls.Add("saveExtra"); }, () => {calls.Add("loadRoom");return room;});
var output = new RecordingOutput(calls,curve,extra,room);
apply.Apply(curve,false,extra,output);
Check(string.Join(",", calls)=="saveEq,saveExtra,eq,channel,safety,loadRoom,room", "persist then output order");
calls.Clear();apply.Apply(curve,true,extra,output);
Check(string.Join(",", calls)=="saveExtra,eq,channel,safety,loadRoom,room", "preview skips EQ save only");
calls.Clear();apply.Apply(curve,false,extra,null);
Check(string.Join(",",calls)=="saveEq,saveExtra", "stopped saves without loading room");
Console.WriteLine($"PASS {checks} total editing and DSP application checks");

var parameters = new DspExtraParameters();
var saved = new DspExtraState { ChannelBalance = new() { Balance=0.2, CrossfeedCutoffHz=900 }, Safety = new() { HeadroomDb=-4, EnableLimiter=false } };
parameters.Load(saved);saved.ChannelBalance.Balance=0.9;
Check(parameters.Channel.Balance==0.2,"load isolates input");
var snapshot=parameters.Snapshot();snapshot.Safety.EnableLimiter=true;
Check(!parameters.Safety.EnableLimiter,"snapshot isolates output");
bool Set(string name, object value) => parameters.Set(name,System.Text.Json.JsonSerializer.SerializeToElement(new {value}));
Check(Set("AudioFxChannelCrossfeedToggle",true) && !parameters.Channel.Enabled && parameters.Snapshot().ChannelBalance.Enabled,"crossfeed effective enabled differs from raw switch");
Check(Set("AudioFxChannelCrossfeedSlider",33.3) && parameters.Snapshot().ChannelBalance.CrossfeedLevel==33,"level rounding only in snapshot");
Check(Set("AudioFxChannelBalanceSlider",100) && parameters.Channel.Balance==1,"upper clamp");
Check(Set("AudioFxChannelBalanceSlider",-100) && parameters.Channel.Balance==-1,"lower clamp");
Check(!Set("AudioFxChannelBalanceSlider",-1),"same value no mutation");
Check(!parameters.Set("AudioFxChannelMonoCombo",System.Text.Json.JsonSerializer.SerializeToElement(new {index=999})),"invalid mono ignored");
Check(parameters.Set("AudioFxChannelMonoCombo",System.Text.Json.JsonSerializer.SerializeToElement(new {index=2})) && parameters.Channel.MonoMode=="right","mono index mapping");
Set("DspPowerHeadroom",false);Check(parameters.Safety.HeadroomDb==0,"headroom off");
Set("DspPowerHeadroom",true);Check(parameters.Safety.HeadroomDb==-4,"headroom restore");
Set("AudioFxChannelCrossfeedToggle",false);Set("AudioFxChannelCrossfeedSlider",0);
Set("DspPowerXfeed",true);Check(parameters.Channel.Enabled && parameters.Channel.CrossfeedEnabled && parameters.Snapshot().ChannelBalance.CrossfeedLevel==25,"power enables parent and restores default level");
Set("AudioFxChannelCrossfeedSlider",45);Set("DspPowerXfeed",false);Set("AudioFxChannelCrossfeedSlider",0);Set("DspPowerXfeed",true);
Check(parameters.Snapshot().ChannelBalance.CrossfeedLevel==45,"crossfeed restore memory");
var description=System.Text.Json.JsonSerializer.SerializeToElement(parameters.Describe("AudioFxChannelBalanceSlider"));
Check(description.GetProperty("min").GetDouble()==-1 && description.GetProperty("step").GetDouble()==0.05,"model owns slider metadata");
parameters.Load(new());Check(parameters.Channel.Balance==0 && !parameters.Safety.EnableLimiter,"profile reload replaces model");
Check(!System.Text.Json.JsonSerializer.SerializeToElement(parameters.Describe("DspPowerXfeed")).GetProperty("enabled").GetBoolean(),"crossfeed power presentation gated by parent");

// ---- ReplayGain model ----
var rgp=new DspRgParameters();
var rgSaved=new ReplayGainState{Mode=ReplayGainMode.Album,PreampDb=3.5,PreventClipping=false};
rgp.Load(rgSaved);rgSaved.PreampDb=99;
Check(rgp.Current.PreampDb==3.5,"rg load isolates input");
var rgSnap=rgp.Snapshot();rgSnap.Mode=ReplayGainMode.Off;
Check(rgp.Current.Mode==ReplayGainMode.Album,"rg snapshot isolates output");
bool RgSet(string name,object value) => rgp.Set(name,System.Text.Json.JsonSerializer.SerializeToElement(new {value}));
bool RgIndex(int index) => rgp.Set("AudioFxRgModeCombo",System.Text.Json.JsonSerializer.SerializeToElement(new {index}));
Check(RgSet("AudioFxRgPreampSlider",100) && rgp.Current.PreampDb==12,"rg preamp upper clamp");
Check(RgSet("AudioFxRgPreampSlider",-100) && rgp.Current.PreampDb==-12,"rg preamp lower clamp");
Check(!RgIndex(999),"rg invalid mode index ignored");
Check(!RgIndex(3),"rg out of range ignored");
RgIndex(1);Check(rgp.Current.Mode==ReplayGainMode.Track && rgp.MemoryIndex==1,"rg mode track");
RgIndex(2);Check(rgp.MemoryIndex==2,"rg mode remembers album");
RgSet("DspPowerRg",false);Check(rgp.Current.Mode==ReplayGainMode.Off && rgp.MemoryIndex==2,"rg power off keeps memory");
RgSet("DspPowerRg",true);Check(rgp.Current.Mode==ReplayGainMode.Album,"rg power restores memory");
Check(RgSet("AudioFxRgPreventClippingToggle",true) && rgp.Current.PreventClipping,"rg clipping on");
Check(!RgSet("AudioFxRgPreventClippingToggle",true),"rg clipping same value no mutation");
RgSet("AudioFxRgPreventClippingToggle",false);Check(!rgp.Current.PreventClipping,"rg clipping off");
var rgDesc=System.Text.Json.JsonSerializer.SerializeToElement(rgp.Describe("AudioFxRgPreampSlider"));
Check(rgDesc.GetProperty("min").GetDouble()==-12 && rgDesc.GetProperty("max").GetDouble()==12 && rgDesc.GetProperty("step").GetDouble()==0.5,"rg model owns slider metadata");
var rgCombo=System.Text.Json.JsonSerializer.SerializeToElement(rgp.Describe("AudioFxRgModeCombo"));
Check(rgCombo.GetProperty("items").GetArrayLength()==3,"rg model owns mode labels");
Check(System.Text.Json.JsonSerializer.SerializeToElement(rgp.Describe("DspPowerRg")).GetProperty("value").GetBoolean(),"rg power mirrors mode");
rgp.Load(new ReplayGainState{Mode=ReplayGainMode.Off,PreampDb=30});
Check(rgp.Current.Mode==ReplayGainMode.Off && rgp.Current.PreampDb==12,"rg reload replaces model and clamps");

// ---- SRC model ----
var srcp=new DspSrcParameters();
srcp.Load(96000,"transparent","ns5");
Check(srcp.TargetHz==96000 && srcp.Quality=="transparent" && srcp.Dither=="ns5","src load keeps valid values");
var srcSnap=srcp.Snapshot();
Check(srcSnap.TargetHz==96000 && srcp.Enabled,"src snapshot mirrors state");
srcp.Load(12345,"bogus","bogus");
Check(srcp.TargetHz==0 && srcp.Quality=="balanced" && srcp.Dither=="off","src load falls back on unknown values");
bool SrcIndex(string name,int index) => srcp.Set(name,System.Text.Json.JsonSerializer.SerializeToElement(new {index}));
bool SrcPower(bool on) => srcp.Set("DspPowerSrc",System.Text.Json.JsonSerializer.SerializeToElement(new {value=on}));
Check(SrcPower(true) && srcp.TargetHz==96000,"src power default restores 96 kHz");
Check(!SrcPower(true),"src power same value no mutation");
SrcIndex("SrcRateCombo",6);Check(srcp.TargetHz==192000 && srcp.MemoryIndex==6,"src rate change remembers index");
SrcPower(false);Check(srcp.TargetHz==0 && srcp.MemoryIndex==6,"src power off keeps memory");
SrcPower(true);Check(srcp.TargetHz==192000,"src power restores memory");
SrcIndex("SrcRateCombo",0);Check(srcp.TargetHz==0 && srcp.MemoryIndex==6,"src rate off keeps memory");
SrcPower(true);Check(srcp.TargetHz==192000,"src power after rate-off restores memory");
Check(!SrcIndex("SrcRateCombo",999) && !SrcIndex("SrcRateCombo",-1),"src invalid rate indices ignored");
Check(!SrcIndex("SrcQualityCombo",9) && !SrcIndex("SrcDitherCombo",-3),"src invalid option indices ignored");
SrcIndex("SrcQualityCombo",0);Check(srcp.Quality=="lowlatency","src quality key follows index");
Check(!SrcIndex("SrcQualityCombo",0),"src quality same index no mutation");
SrcIndex("SrcDitherCombo",2);Check(srcp.Dither=="highpass" && !SrcIndex("SrcDitherCombo",2),"src dither key follows index once");
var srcRateDesc=System.Text.Json.JsonSerializer.SerializeToElement(srcp.Describe("SrcRateCombo"));
Check(srcRateDesc.GetProperty("items").GetArrayLength()==7 && srcRateDesc.GetProperty("index").GetInt32()==6,"src model owns rate labels");
var srcQualDesc=System.Text.Json.JsonSerializer.SerializeToElement(srcp.Describe("SrcQualityCombo"));
Check(srcQualDesc.GetProperty("items").GetArrayLength()==3,"src model owns quality labels");
var srcDithDesc=System.Text.Json.JsonSerializer.SerializeToElement(srcp.Describe("SrcDitherCombo"));
Check(srcDithDesc.GetProperty("items").GetArrayLength()==4,"src model owns dither labels");
Check(System.Text.Json.JsonSerializer.SerializeToElement(srcp.Describe("DspPowerSrc")).GetProperty("value").GetBoolean(),"src power mirrors rate");
Check(DspSrcParameters.IndexOfHz(96000)==4 && DspSrcParameters.IndexOfHz(44100)==1,"src rate index lookup");
Console.WriteLine($"PASS {checks} total EQ, delivery and parameter checks");
sealed class RecordingOutput(List<string> calls, EqCurveState curve, DspExtraState extra, RoomCorrectionState room) : IDspParameterTarget
{
 public void SetEqCurve(EqCurveState? c){if(!ReferenceEquals(c,curve))throw new Exception("eq");calls.Add("eq");}
 public void SetChannelBalance(ChannelBalanceState? c){if(!ReferenceEquals(c,extra.ChannelBalance))throw new Exception("channel");calls.Add("channel");}
 public void SetSafety(DspSafetyState? c){if(!ReferenceEquals(c,extra.Safety))throw new Exception("safety");calls.Add("safety");}
 public void SetRoomCorrection(RoomCorrectionState? c){if(!ReferenceEquals(c,room))throw new Exception("room");calls.Add("room");}
}
