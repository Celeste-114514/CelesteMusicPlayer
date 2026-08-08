using System;
using System.Drawing;
using System.IO;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>系统托盘图标：显示主界面 / 退出。</summary>
    internal sealed class AppTrayIcon : IDisposable
    {
        private readonly MainWindow _owner;
        private TaskbarIcon? _icon;
        private Icon? _drawingIcon;
        private bool _disposed;

        public AppTrayIcon(MainWindow owner)
        {
            _owner = owner;
        }

        public void Show()
        {
            EnsureCreated();
            if (_icon != null)
            {
                _icon.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            }
        }

        public void Hide()
        {
            if (_icon != null)
            {
                _icon.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _icon?.Dispose();
            }
            catch
            {
            }

            _icon = null;
            try
            {
                _drawingIcon?.Dispose();
            }
            catch
            {
            }

            _drawingIcon = null;
            GC.SuppressFinalize(this);
        }

        private void EnsureCreated()
        {
            if (_icon != null)
            {
                return;
            }

            string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icoPath))
            {
                _drawingIcon = new Icon(icoPath);
            }

            _icon = new TaskbarIcon
            {
                ToolTipText = "CelesteMusicPlayer",
                Icon = _drawingIcon,
                NoLeftClickDelay = true
            };

            var flyout = new MenuFlyout();
            var showItem = new MenuFlyoutItem { Text = "显示主界面" };
            showItem.Click += (_, _) => _owner.RestoreFromTray();
            var exitItem = new MenuFlyoutItem { Text = "退出播放器" };
            exitItem.Click += (_, _) => _owner.ExitFromTray();
            flyout.Items.Add(showItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(exitItem);
            _icon.ContextFlyout = flyout;

            _icon.LeftClickCommand = new TrayRelayCommand(() => _owner.RestoreFromTray());
            _icon.ForceCreate();
        }

        private sealed class TrayRelayCommand : ICommand
        {
            private readonly Action _action;

            public TrayRelayCommand(Action action) => _action = action;

            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter) => _action();
        }
    }
}
