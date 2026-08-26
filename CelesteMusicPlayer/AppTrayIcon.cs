using System;
using System.Drawing;
using System.Windows.Input;
using System.IO;
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

            // 托盘图标必须用 .ico：System.Drawing.Icon 只支持 ICO 格式。
            // 注意：不能设置 IconSource 为 PNG，H.NotifyIcon 会把 ImageSource 异步转成 Icon
            // （TaskbarIcon.IconSource.cs -> ToIconAsync -> ToSmallIcon），而 Icon 无法从 PNG 构造，
            // 会在 DispatcherQueue 回调中抛 "Argument 'picture' must be a picture that can be used as a Icon"，
            // 导致托盘图标后续点击消息处理异常（点击无反应）。
            string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icoPath))
            {
                try
                {
                    _drawingIcon = new Icon(icoPath);
                }
                catch
                {
                    _drawingIcon = null;
                }
            }

            _icon = new TaskbarIcon
            {
                ToolTipText = "CelesteMusicPlayer",
                Icon = _drawingIcon,
                NoLeftClickDelay = true
            };

            var flyout = new MenuFlyout();
            // 注意：H.NotifyIcon 默认 ContextMenuMode=PopupMenu，会把 MenuFlyout 转成
            // Win32 菜单（TrackPopupMenuEx），点击菜单项时只执行 MenuFlyoutItem.Command，
            // 不会触发 Click 事件（见库源码 TaskbarIcon.ContextMenu.WinRT.PopupMenu.cs）。
            // 因此这里必须给菜单项设置 Command，Click 订阅在 PopupMenu 模式下无效。
            var showItem = new MenuFlyoutItem { Text = "显示主界面" };
            showItem.Command = new TrayRelayCommand(() => { StartupLog.Write("托盘命令: 显示主界面"); _owner.RestoreFromTray(); });
            // 播放控制 + 收藏（转发 MainWindow 现成 public 方法）
            var playPauseItem = new MenuFlyoutItem { Text = "播放 / 暂停" };
            playPauseItem.Command = new TrayRelayCommand(() => { _owner.TogglePlayPausePublic(); });
            var prevItem = new MenuFlyoutItem { Text = "上一首" };
            prevItem.Command = new TrayRelayCommand(() => { _owner.PreviousPublic(); });
            var nextItem = new MenuFlyoutItem { Text = "下一首" };
            nextItem.Command = new TrayRelayCommand(() => { _owner.NextPublic(); });
            var favoriteItem = new MenuFlyoutItem { Text = "添加到我喜欢" };
            favoriteItem.Command = new TrayRelayCommand(() => { _owner.FavoriteCurrentPublic(); });
            var exitItem = new MenuFlyoutItem { Text = "退出播放器" };
            exitItem.Command = new TrayRelayCommand(() => { StartupLog.Write("托盘命令: 退出播放器"); _owner.ExitFromTray(); });
            flyout.Items.Add(showItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(playPauseItem);
            flyout.Items.Add(prevItem);
            flyout.Items.Add(nextItem);
            flyout.Items.Add(favoriteItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(exitItem);
            _icon.ContextFlyout = flyout;

            // 左键单击恢复主界面
            _icon.LeftClickCommand = new TrayRelayCommand(() => _owner.RestoreFromTray());

            // 将 TaskbarIcon 挂到主窗口可视树,H.NotifyIcon 的 ContextFlyout/PopupMenu
            // 依赖 FrameworkElement 的 Loaded 状态;纯代码创建不挂载会导致右键菜单不弹出
            if (_owner.Content is Microsoft.UI.Xaml.Controls.Panel rootPanel)
            {
                rootPanel.Children.Add(_icon);
            }

            _icon.ForceCreate();
            StartupLog.Write("托盘图标已创建 (Icon=" + (_drawingIcon != null) + ")");
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
