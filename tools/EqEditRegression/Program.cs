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
sealed class RecordingOutput(List<string> calls, EqCurveState curve, DspExtraState extra, RoomCorrectionState room) : IDspParameterTarget
{
 public void SetEqCurve(EqCurveState? c){if(!ReferenceEquals(c,curve))throw new Exception("eq");calls.Add("eq");}
 public void SetChannelBalance(ChannelBalanceState? c){if(!ReferenceEquals(c,extra.ChannelBalance))throw new Exception("channel");calls.Add("channel");}
 public void SetSafety(DspSafetyState? c){if(!ReferenceEquals(c,extra.Safety))throw new Exception("safety");calls.Add("safety");}
 public void SetRoomCorrection(RoomCorrectionState? c){if(!ReferenceEquals(c,room))throw new Exception("room");calls.Add("room");}
}
