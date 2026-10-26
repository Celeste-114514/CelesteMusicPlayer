// Exercise production appearance commands with only Windows shell services stubbed.
// Config goes to an isolated temporary directory. JSON lines also drive browser integration tests.
using System.Text.Json;
using CelesteMusicPlayer;

AppSettingsStore.Root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "celeste-appearance-" + Guid.NewGuid().ToString("N"));
var window = new MainWindow();
while (Console.ReadLine() is { } line)
{
    try
    {
        using var doc = JsonDocument.Parse(line);
        var msg = doc.RootElement;
        if (msg.TryGetProperty("testPick", out var path)) StubPicker.NextPath = path.GetString();
        var data = await window.WebAppearanceAsync(msg);
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, data }));
    }
    catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
}

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private static string ReadString(JsonElement msg, string key) => msg.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        internal static IntPtr PickerHostHandle() => IntPtr.Zero;
    }
    internal static class AppSettingsStore
    {
        internal static string Root = "";
        internal static string GetConfigDirectory() => Root;
    }
    internal static class StartupLog
    {
        internal static void Write(string value) { }
        internal static void WriteException(string label, Exception ex) => Console.Error.WriteLine(label + ": " + ex.Message);
    }
    internal static class FontCatalog { internal static string[] Families() => new[] { "Segoe UI", "Microsoft YaHei UI", "Consolas" }; }
    internal static class StubPicker { internal static string? NextPath; }
}
namespace Windows.UI { public struct Color { public byte R, G, B; } }
namespace Windows.UI.ViewManagement
{
    public enum UIColorType { Background }
    public class UISettings { public Windows.UI.Color GetColorValue(UIColorType value) => new() { R = 255, G = 255, B = 255 }; }
}
namespace Windows.Storage
{
    public class StorageFile { public string Path { get; set; } = ""; }
}
namespace Windows.Storage.Pickers
{
    public class FileOpenPicker
    {
        public List<string> FileTypeFilter { get; } = new();
        public Task<Windows.Storage.StorageFile?> PickSingleFileAsync()
        {
            string? path = CelesteMusicPlayer.StubPicker.NextPath;
            CelesteMusicPlayer.StubPicker.NextPath = null;
            return Task.FromResult(path == null ? null : new Windows.Storage.StorageFile { Path = path });
        }
    }
}
namespace WinRT.Interop { public static class InitializeWithWindow { public static void Initialize(object value, IntPtr handle) { } } }
