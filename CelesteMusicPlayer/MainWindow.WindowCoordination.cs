using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        public new void Activate() => RestoreMainWindow();

        private MainWindowCoordinator? _windowCoordinator;
        private MainWindowCoordinator WindowCoordinator => _windowCoordinator ??= new(new LegacyMainWindowSurface(this));

        internal void OpenWebShell() => WindowCoordinator.Show();

        /// <summary>W13：启动路径专用。MainWindow 不再是窗口，显式开一次网页会话即可。</summary>
        internal void StartWebShellSession() { OpenWebShell(); base.Activate(); RunPostLoadStartup(); }

        internal void RestoreMainWindow()
        {
            if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(RestoreMainWindow); return; }
            try { WindowCoordinator.Show(); }
            catch (Exception ex) { StartupLog.WriteException("MainWindow.RestoreMainWindow", ex); }
        }

        internal void ShowWebPage(string page)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try { WindowCoordinator.Show(page); }
                catch (Exception ex) { StartupLog.WriteException("MainWindow.ShowWebPage", ex); }
            });
        }

        private void CloseWebShellWithOwner(dynamic sender, dynamic e)
        {
            Closed -= CloseWebShellWithOwner;
            WindowCoordinator.Close();
        }

        private sealed class LegacyMainWindowSurface(MainWindow owner) : IMainWindowSurface
        {
            public bool Exists => owner._webShellWindow != null;
            public bool IsMinimized => owner._webShellWindow?.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            public void Create()
            {
                if (!owner.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("窗口创建必须经 UI 队列执行。");
                var shell = new WebShellWindow(new LegacyWebPlayerServices(owner));
                owner._webShellWindow = shell;
                shell.Closed += (_, _) =>
                {
                    if (ReferenceEquals(owner._webShellWindow, shell)) owner._webShellWindow = null;
                    if (!owner.WindowCoordinator.IsClosing) owner.ExitApplication();
                };
                owner.Closed -= owner.CloseWebShellWithOwner;
                owner.Closed += owner.CloseWebShellWithOwner;
            }
            public void Restore() { if (!NativeMainWebWindow.BackgroundVerification) (owner._webShellWindow?.AppWindow.Presenter as OverlappedPresenter)?.Restore(); }
            public void Show() { if (!NativeMainWebWindow.BackgroundVerification) owner._webShellWindow?.AppWindow.Show(); }
            public void Activate() => owner._webShellWindow?.Activate();
            public void HideLegacyOwner() => owner.AppWindow.Hide();
            public void Navigate(string page) => owner._webShellWindow?.NavigateTo(page);
            public void Close() => owner._webShellWindow?.CloseForExit();
        }
    }
}
