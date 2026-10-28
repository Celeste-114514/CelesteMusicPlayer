using System;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        private TaskbarPlayerWindow? _taskbarPlayer;
        private bool _taskbarPlayerSuspended;
        internal void SuspendTaskbarPlayer(bool suspended)
        {
            _taskbarPlayerSuspended = suspended;
            _taskbarPlayer?.Suspend(suspended);
        }
        private void ApplyTaskbarPlayerPreference()
        {
            if (AppSettingsStore.Load().TaskbarPlayerEnabled)
            {
                if (_taskbarPlayer == null)
                {
                    _taskbarPlayer = new TaskbarPlayerWindow(this, _taskbarPlayerSuspended);
                    Closed -= CloseTaskbarPlayer;
                    Closed += CloseTaskbarPlayer;
                }
            }
            else { _taskbarPlayer?.Dispose(); _taskbarPlayer = null; }
            _webShellWindow?.PublishPlaybackState();
        }
        private void CloseTaskbarPlayer(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
        { _taskbarPlayer?.Dispose(); _taskbarPlayer = null; }
        /// <summary>
        /// 播放状态栏那个开关按钮走这里（设置页的入口已经按用户要求删掉了）。
        /// 改完立刻落盘并应用，同时把新状态推给网页，按钮上的 on/off 才不会骗人。
        /// </summary>
        internal void ToggleTaskbarPlayer()
        {
            try
            {
                var settings = AppSettingsStore.Load();
                settings.TaskbarPlayerEnabled = !settings.TaskbarPlayerEnabled;
                AppSettingsStore.Save(settings);
                ApplyTaskbarPlayerPreference();
            }
            catch (Exception ex) { StartupLog.Write("[任务栏播放条] 切换失败：" + ex.Message); }
        }

        internal void HideTaskbarPlayer()
        {
            var settings = AppSettingsStore.Load();
            settings.TaskbarPlayerEnabled = false;
            AppSettingsStore.Save(settings);
            ApplyTaskbarPlayerPreference();
        }
    }
}
