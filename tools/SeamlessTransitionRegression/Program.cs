using System.Reflection;
using CelesteMusicPlayer;
using NAudio.Wave;

static WaveFileReader Reader(int frames) {
    var stream = new MemoryStream();
    using (var writer = new WaveFileWriter(new KeepOpen(stream), new WaveFormat(44100, 16, 2))) writer.Write(new byte[frames * 4]);
    stream.Position = 0;
    return new WaveFileReader(stream);
}
static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
var assembly = typeof(HiFiOutputBackend).Assembly;
var providerType = assembly.GetType("CelesteMusicPlayer.SeamlessWaveProvider")!;
using var backend = new HiFiOutputBackend();
var first = Reader(16);
var second = Reader(44100);
var provider = Activator.CreateInstance(providerType, first)!;
providerType.GetMethod("PrepareNext")!.Invoke(provider, [second]);
Set(backend, "_waveFile", first);
Set(backend, "_seamless", provider);
Set(backend, "_isPlaying", true);
var buffer = new byte[128];
providerType.GetMethod("Read")!.Invoke(provider, [buffer, 0, buffer.Length]);
int notifications = 0;
backend.SeamlessTrackChanged += () => notifications++;
var tick = typeof(HiFiOutputBackend).GetMethod("UpdatePosition", BindingFlags.Instance | BindingFlags.NonPublic)!;
try { tick.Invoke(backend, null); tick.Invoke(backend, null); }
catch (TargetInvocationException e) { Console.Error.WriteLine("FAIL: tick before notification: " + e.InnerException); Environment.Exit(1); }
if (notifications != 1 || backend.Duration != second.TotalTime) throw new Exception($"FAIL: notifications={notifications}, duration={backend.Duration}");
Console.WriteLine("PASS: released old reader -> next reader adopted, duration updated, exactly one notification over two ticks");
sealed class KeepOpen(Stream inner) : Stream {
 public override bool CanRead=>inner.CanRead; public override bool CanSeek=>inner.CanSeek; public override bool CanWrite=>inner.CanWrite;
 public override long Length=>inner.Length; public override long Position {get=>inner.Position;set=>inner.Position=value;}
 public override void Flush()=>inner.Flush(); public override int Read(byte[] b,int o,int c)=>inner.Read(b,o,c); public override long Seek(long o,SeekOrigin s)=>inner.Seek(o,s); public override void SetLength(long l)=>inner.SetLength(l); public override void Write(byte[] b,int o,int c)=>inner.Write(b,o,c);
}
