using System.Text.Json;

namespace CelesteMusicPlayer.ThinHost;

internal static class Program
{
    internal static string RunDirectory = "";
    internal static readonly List<string> Evidence = new();
    [STAThread]
    private static int Main(string[] args)
    {
        bool selfTest = args.Contains("--self-test");
        // Private PoC data only; never use the production settings or browser profile.
        RunDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CelesteMusicPlayer-Poc", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + "-" + Environment.ProcessId);
        Directory.CreateDirectory(RunDirectory);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try
        {
            using var form = new ProbeForm(selfTest);
            if (selfTest)
            {
                _ = form.Handle;
                form.BeginInvoke(new Action(async () => await form.SelfTestAsync()));
            }
            Application.Run(form);
            return selfTest && !form.Passed ? 1 : 0;
        }
        catch (Exception ex) { Record("Fatal: " + ex); WriteReport(false, new { error = ex.ToString() }); return 1; }
    }
    internal static void Record(string message)
    {
        Evidence.Add(DateTimeOffset.Now.ToString("o") + " " + message);
        File.WriteAllLines(Path.Combine(RunDirectory,"host.log"),Evidence);
    }
    internal static void WriteReport(bool passed, object detail) => File.WriteAllText(Path.Combine(RunDirectory,"result.json"),
        JsonSerializer.Serialize(new { passed, detail, evidence=Evidence, limitations="Hidden self-test does not verify visible taskbar buttons, DPI monitor transitions or user focus. No real audio is connected." },new JsonSerializerOptions { WriteIndented=true }));
}
