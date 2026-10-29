using CelesteMusicPlayer;
using NAudio.Wave;
var root=Path.Combine(Path.GetTempPath(),"CelesteQuick-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);AppSettingsStore.SettingsPathOverride=Path.Combine(root,"settings.json");
void Check(bool b,string s){if(!b)throw new Exception(s);Console.WriteLine("PASS "+s);}
try{
 AppSettingsStore.Update(s=>{s.OutputMode="Shared";s.DsdOutputMode="Pcm";s.Volume=60;});
 string wav=Path.Combine(root,"tone.wav");using(var writer=new WaveFileWriter(wav,new WaveFormat(44100,16,2))){for(int i=0;i<88200;i++){float v=(float)(Math.Sin(i*2*Math.PI*440/44100)*.1);writer.WriteSample(v);writer.WriteSample(v);}}
 Check(QuickStartupIntent.Files(new[]{"--quick",wav,wav}).SequenceEqual(new[]{wav}),"startup-batch-and-dedup");
 var cue=Path.Combine(root,"tone.cue");File.WriteAllText(cue,"PERFORMER \"Artist\"\nTITLE \"Album\"\nFILE \"tone.wav\" WAVE\n TRACK 01 AUDIO\n TITLE \"First\"\n INDEX 01 00:00:00\n TRACK 02 AUDIO\n TITLE \"Second\"\n INDEX 01 00:01:00");var tracks=QuickTrackLoader.Load(new[]{cue});Check(tracks.Count==2&&tracks[1].StartTimeSeconds==1&&tracks.All(t=>t.Album=="Album"),"real-cue-loader-and-boundaries");
 var db=LibraryDb.OpenConnectionCount;var decoder=new ControlledDecoder(wav);ControlledOutput? output=null;var engine=new AudioPlaybackEngine(decoder,()=>new HiFiOutputBackend(()=>output=new ControlledOutput()));
 using(var session=new QuickPlaybackSession(a=>a(),engine)){
  await session.AddAsync(new[]{wav},true,true);Check(session.Queue.Count==2,"automatic-sidecar-cue-expansion");Check(engine.IsPlaying,"quick-real-pcm-play");output!.Advance(2205);Check(output.NonZero>0,"quick-read-real-audio");
  session.Pause();Check(!engine.IsPlaying,"pause");await session.ToggleAsync();Check(engine.IsPlaying,"resume");session.Stop();await session.ToggleAsync();Check(engine.IsPlaying,"stop-then-restart");await session.StepAsync(1);Check(session.Index==1,"next-track");
  session.SetVolume(33);Check(session.Volume==33,"session-volume");session.Stop();
  decoder.Delay=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);var pending=session.PlayAsync(0);await decoder.Started.Task;session.Stop();decoder.Delay.SetResult(wav);await pending;Check(!engine.IsPlaying&&!session.Busy,"stop-during-decode-cannot-autoplay");
  decoder.Delay=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);decoder.Started=new(TaskCreationOptions.RunContinuationsAsynchronously);var closing=session.PlayAsync(0);await decoder.Started.Task;session.Dispose();decoder.Delay.SetResult(wav);await closing;Check(!engine.IsPlaying,"close-during-decode-cannot-revive-output");
 }
 Check(LibraryDb.OpenConnectionCount==db,"quick-session-never-opens-library");
 Console.WriteLine("All quick-player core regressions passed without an audio device.");
}finally{AppSettingsStore.SettingsPathOverride=null;Directory.Delete(root,true);}
sealed class ControlledDecoder(string wav):IDecoderBackend{
 public TaskCompletionSource<string?>? Delay;public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);public Action<Exception>? FailureHandler{get;set;}public bool CanDecode(string p)=>true;
 public Task<string?> TranscodeToWavAsync(string p,HiFiOutputBackend.OutputMode m,string? d,Action<string>? status,double startSeconds=0,double lengthSeconds=0){Started.TrySetResult();return Delay?.Task??Task.FromResult<string?>(wav);}public Task<string?> EnsureCachedWavAsync(string p,HiFiOutputBackend.OutputMode m,string? d,Action<string>? status,double startSeconds=0,double lengthSeconds=0)=>Task.FromResult<string?>(wav);public Task<TimeSpan> ProbeSourceDurationAsync(string p)=>Task.FromResult(TimeSpan.FromSeconds(2));public Task<bool> RunFfmpegAsync(string a,Action<string>? s=null)=>Task.FromResult(false);
}
sealed class ControlledOutput:IWavePlayer{
 private IWaveProvider? _source;public int NonZero;public PlaybackState PlaybackState{get;private set;}public float Volume{get;set;}=1;public WaveFormat OutputWaveFormat=>_source!.WaveFormat;public event EventHandler<StoppedEventArgs>? PlaybackStopped;public void Init(IWaveProvider p)=>_source=p;public void Play()=>PlaybackState=PlaybackState.Playing;public void Pause()=>PlaybackState=PlaybackState.Paused;public void Stop()=>PlaybackState=PlaybackState.Stopped;public void Dispose()=>PlaybackState=PlaybackState.Stopped;public void Advance(int frames){var b=new byte[frames*_source!.WaveFormat.BlockAlign];int n=_source.Read(b,0,b.Length);NonZero+=b.Take(n).Count(v=>v!=0);}}
