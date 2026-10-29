using System;
namespace CelesteMusicPlayer;
internal static class QuickPlayerCoordinator
{
    internal static WebQuickPlayerWindow? Window { get; private set; }
    internal static Action? PauseMain;
    internal static WebQuickPlayerWindow Show()
    {
        if (Window == null)
        {
            Window = new WebQuickPlayerWindow(() => PauseMain?.Invoke());
            Window.Closed += (_, _) => Window = null;
        }
        Window.Activate(); return Window;
    }
    internal static void PauseForMain() { if (Window != null && (Window.Session.Engine.IsPlaying || Window.Session.Busy)) Window.PauseForMain(); }
}
