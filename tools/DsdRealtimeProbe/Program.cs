using System;
using System.Threading;
using System.Runtime.InteropServices;
using CelesteMusicPlayer;

if (args.Length == 0) throw new ArgumentException("Pass one or more DSF files. Opens the configured DAC for 12 seconds per file.");
NativeWasapi.CoInitializeEx(IntPtr.Zero, 0);
var settingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CelesteMusicPlayer", "app-settings.json");
using var settings = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(settingsPath));
string? deviceId = settings.RootElement.GetProperty("OutputDeviceId").GetString();
foreach (string file in args)
{
    using var bits = DsdBitstream.Open(file);
    using var dop = new DoP24LeSource(bits);
    using var output = new NativeWasapiExclusiveOut();
    Exception? failure = null;
    output.Failed += ex => failure = ex;
    var device = NativeWasapi.GetRenderDeviceById(deviceId) ?? throw new Exception("No device");
    try { if (!output.Init(device, dop, true)) throw new Exception(output.LastError); }
    finally { Marshal.ReleaseComObject(device); }
    dop.WaitForPrefill(TimeSpan.FromSeconds(2));
    if (!output.Play()) throw new Exception(output.LastError);
    Thread.Sleep(12000);
    long frames = output.FramesWritten;
    output.Stop();
    if (failure != null) throw failure;
    double seconds = frames / (double)output.SampleRateValue;
    if (seconds < 10 || seconds > 14 || dop.PrefillFrames != 0 || output.UnderrunCount != 0)
        throw new Exception($"Unexpected progress/padding: {seconds:F2}s, silence={dop.PrefillFrames}, underrun={output.UnderrunCount}");
    Console.WriteLine($"PASS {System.IO.Path.GetFileName(file)} {dop.WaveFormat.SampleRate}Hz progress={seconds:F2}s silence={dop.PrefillFrames}");
}
namespace CelesteMusicPlayer
{
    internal static class StartupLog
    {
        public static void Write(string text) => Console.WriteLine(text);
        public static void WriteException(string where, Exception? ex) => Console.WriteLine(where + ": " + ex);
    }
}
