using System;
namespace CelesteMusicPlayer;
public sealed partial class MainWindow
{
    internal nint NativeTaskbarHandle => _taskbarButtons?.BoundWindow ?? 0;
    internal void BindNativeTaskbar(nint hwnd){_taskbarButtons?.Dispose();_taskbarButtons=new TaskbarThumbnailButtons(this,hwnd);_thumbPumpTimer?.Stop();_thumbPumpTimer=null;_thumbPumpTicks=0;StartThumbnailButtonsPump();}
}
