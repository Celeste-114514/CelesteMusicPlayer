using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer
{
    /// <summary>统一为独立窗口应用应用图标（Assets\AppIcon.ico）。</summary>
    public static class WindowIconHelper
    {
        public static void Apply(Window window)
        {
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(iconPath))
                {
                    window.AppWindow.SetIcon(iconPath);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("WindowIconHelper.cs", caught); }
        }
    }
}
