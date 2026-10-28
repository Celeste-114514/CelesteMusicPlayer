using System;
using Microsoft.Web.WebView2.Core;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private TaskbarPlayerWindow? _taskbarPlayer;
        internal void AttachTaskbarCovers(CoreWebView2 core) => _webShellWindow?.AttachTaskbarCovers(core);
        private void ApplyTaskbarPlayerPreference()
        {
            if (AppSettingsStore.Load().TaskbarPlayerEnabled)
            {
                if (_taskbarPlayer == null)
                {
                    _taskbarPlayer = new TaskbarPlayerWindow(this);
                    Closed -= CloseTaskbarPlayer;
                    Closed += CloseTaskbarPlayer;
                }
            }
            else { _taskbarPlayer?.Dispose(); _taskbarPlayer = null; }
        }
        private void CloseTaskbarPlayer(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
        { _taskbarPlayer?.Dispose(); _taskbarPlayer = null; }
        internal void HideTaskbarPlayer()
        {
            var settings = AppSettingsStore.Load();
            settings.TaskbarPlayerEnabled = false;
            AppSettingsStore.Save(settings);
            ApplyTaskbarPlayerPreference();
        }
    }
}
