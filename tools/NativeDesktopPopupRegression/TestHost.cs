// Only the backend, timer and log dependencies are replaced. The HWND, menu,
// layout, whitelist and command adapter are the actual production source files.
using System;
namespace Microsoft.UI.Dispatching
{
    internal sealed class DispatcherQueue { public static DispatcherQueue GetForCurrentThread()=>new(); public DispatcherQueueTimer CreateTimer()=>new(); public bool TryEnqueue(Action action){action();return true;} }
    internal sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval{get;set;}
        public event Action<object,object>? Tick;
        public void Start(){} public void Stop(){}
    }
}
namespace CelesteMusicPlayer
{
    internal sealed class WindowsTrayIcon { internal struct RECT{public int Left,Top,Right,Bottom; public int Width=>Right-Left; public int Height=>Bottom-Top;} }
    internal static class StartupLog { public static void Write(string value){} public static void WriteException(string context,Exception ex)=>throw new Exception(context,ex); }
    // No Explorer windows are opened during the test.
    internal static class AppLog { public static string LogsDirectory=>throw new OperationCanceledException("Test must not open Explorer"); }
    internal sealed class TestTrack { public string FilePath=""; }
    internal static class CoverArtService { internal static System.Threading.Tasks.Task<string?> GetDesktopCoverFileAsync(string path)=>System.Threading.Tasks.Task.FromResult<string?>(null); }
    internal static class WindowsTrayService { internal static (string,System.Collections.Generic.Dictionary<string,string>) BuildTheme()=>("light",new()); }
    internal sealed class MainWindow
    {
        public string LastAction=""; public int Exits;
        public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue=>new();
        public IPlaybackStateSource PlaybackStateSource=>throw new Exception("Suspended taskbar should not poll playback");
        public TestTrack? GetCurrentPlayingItem()=>null;
        public void HideTaskbarPlayer()=>LastAction="hideTaskbarPlayer";
        public bool IsEnginePlayingNow=>false; public bool DesktopLyricsEnabledPublic=>false;
        public bool IsCurrentFavoritePublic()=>false;
        public void RestoreFromTray()=>LastAction="showMain";
        public void ShowMiniPlayerWindow()=>LastAction="showMini";
        public void ShowScatteredWindow()=>LastAction="showScattered";
        public void ToggleDesktopLyricsPublic()=>LastAction="toggleLyrics";
        public void ExecutePlaybackCommand(string id)=>LastAction=id;
        public void FavoriteCurrentPublic()=>LastAction="favorite";
        public void ShowWebPage(string id)=>LastAction="openSettings";
        public void ExitFromTray(){LastAction="exit";Exits++;}
    }
}
