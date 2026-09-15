using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Input;
using System.IO;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>系统托盘图标：显示主界面 / 退出 / 查看日志。</summary>
    internal sealed class AppTrayIcon : IDisposable
    {
        private static string LogPathHint => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CelesteMusicPlayer",
            "CelesteMusicPlayer.log");

        private static void OpenLogFile()
        {
            try
            {
                string path = LogPathHint;
                if (!File.Exists(path))
                {
                    string dir = Path.GetDirectoryName(path) ?? ".";
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                    return;
                }
                // 直接用 notepad 打开（避免关联编辑器卡住/路径含空格）
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = false
                });
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AppTrayIcon.OpenLogFile", caught);
            }
        }
        private readonly MainWindow _owner;
        private TaskbarIcon? _icon;
        private Icon? _drawingIcon;
        private bool _disposed;

        /// <summary>检测到的新版本号（非空表示有待处理更新）。点击托盘图标 / 菜单项时据此跳到关于面板。</summary>
        private string? _pendingUpdateVersion;

        private MenuFlyout? _flyout;

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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppTrayIcon.cs", caught); }

            _icon = null;
            try
            {
                _drawingIcon?.Dispose();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppTrayIcon.cs", caught); }

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

            _flyout = new MenuFlyout();
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
            var openLogItem = new MenuFlyoutItem { Text = "打开日志文件" };
            openLogItem.Command = new TrayRelayCommand(OpenLogFile);
            var exitItem = new MenuFlyoutItem { Text = "退出播放器" };
            exitItem.Command = new TrayRelayCommand(() => { StartupLog.Write("托盘命令: 退出播放器"); _owner.ExitFromTray(); });
            _flyout.Items.Add(showItem);
            _flyout.Items.Add(new MenuFlyoutSeparator());
            _flyout.Items.Add(playPauseItem);
            _flyout.Items.Add(prevItem);
            _flyout.Items.Add(nextItem);
            _flyout.Items.Add(favoriteItem);
            _flyout.Items.Add(new MenuFlyoutSeparator());
            _flyout.Items.Add(openLogItem);
            _flyout.Items.Add(exitItem);
            _icon.ContextFlyout = _flyout;

            // 左键单击：有待处理更新时打开「关于」面板，否则恢复主界面
            _icon.LeftClickCommand = new TrayRelayCommand(OnLeftClick);

            // 将 TaskbarIcon 挂到主窗口可视树,H.NotifyIcon 的 ContextFlyout/PopupMenu
            // 依赖 FrameworkElement 的 Loaded 状态;纯代码创建不挂载会导致右键菜单不弹出
            if (_owner.Content is Microsoft.UI.Xaml.Controls.Panel rootPanel)
            {
                rootPanel.Children.Add(_icon);
            }

            _icon.ForceCreate();

            StartupLog.Write("托盘图标已创建 (Icon=" + (_drawingIcon != null) + ")");
        }

        /// <summary>
        /// 在系统托盘弹出「发现新版本」气泡通知（纯视觉提醒）。同时记录待处理版本号，
        /// 并把右键菜单加上「发现新版本」入口；点击托盘图标也会据此跳到关于面板。
        /// 由启动自动检查更新（MainWindow 后台任务）在发现新版本时调用。
        /// 说明：H.NotifyIcon 的 WinUI 版默认不暴露气泡点击事件（已知 Win10 bug，
        /// 见库文档 OnTrayBalloonTipClicked），故用托盘左键单击 / 右键菜单项来承接
        /// 「点击进入关于面板」这一意图，二者走的是已稳定使用的 LeftClickCommand 机制。
        /// </summary>
        public void ShowUpdateNotification(string versionTag)
        {
            EnsureCreated();
            if (_icon == null)
            {
                return;
            }

            _pendingUpdateVersion = versionTag;
            try
            {
                _icon.ShowNotification(
                    "CelesteMusicPlayer 更新",
                    $"发现新版本 {versionTag}，点击系统托盘图标查看详情。",
                    NotificationIcon.Info);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AppTrayIcon.ShowUpdateNotification", caught);
            }

            AddOrUpdateMenuItems();
        }

        /// <summary>左键单击托盘：有待处理更新时打开关于面板，否则恢复主界面。</summary>
        private void OnLeftClick()
        {
            if (_pendingUpdateVersion != null)
            {
                SettingsWindow.ShowAbout();
            }
            else
            {
                _owner.RestoreFromTray();
            }
        }

        /// <summary>在右键菜单中插入/更新「发现新版本」入口（仅当有待处理更新时，且只加一次）。</summary>
        private void AddOrUpdateMenuItems()
        {
            if (_flyout == null || _pendingUpdateVersion == null)
            {
                return;
            }

            foreach (var item in _flyout.Items)
            {
                if (item is MenuFlyoutItem existing && existing.Name == "UpdateMenuItem")
                {
                    return; // 已添加，避免重复
                }
            }

            var updateItem = new MenuFlyoutItem
            {
                Name = "UpdateMenuItem",
                Text = $"发现新版本 {_pendingUpdateVersion}（点击查看）"
            };
            updateItem.Command = new TrayRelayCommand(SettingsWindow.ShowAbout);

            // 插在「显示主界面」之后：先加一条分隔线，再放更新入口
            _flyout.Items.Insert(1, new MenuFlyoutSeparator());
            _flyout.Items.Insert(2, updateItem);
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
