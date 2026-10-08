using System;
using System.Drawing;
using System.Windows.Input;
using System.IO;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>系统托盘图标：显示主界面 / 播放控制 / 退出 / 打开日志目录。</summary>
    internal sealed class AppTrayIcon : IDisposable
    {
        private readonly MainWindow _owner;
        private TaskbarIcon? _icon;
        private Icon? _drawingIcon;
        private bool _disposed;
        private MenuFlyout? _flyout;

        /// <summary>托盘图标是否已真正注册到系统（ForceCreate 成功）。False = 右下角一定没有图标。</summary>
        public bool IsCreated => _icon != null;

        // 2026-10-08：窗口构造函数里建托盘图标会失败（句柄还没好），失败后必须能补建。
        private int _createAttempts;
        private int _slowAttempts;
        private bool _retryScheduled;
        private System.Threading.Timer? _retryTimer;
        private const int MaxCreateAttempts = 8;
        // 快速补建全失败后转慢速兜底：资源管理器重启/通知区恢复后能自己冒出来。
        private const int MaxSlowCreateAttempts = 12;

        /// <summary>检测到的新版本号（非空表示有待处理更新）。点击托盘图标 / 菜单项时据此跳到关于面板。</summary>
        private string? _pendingUpdateVersion;

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
                _retryTimer?.Dispose();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppTrayIcon.cs", caught); }

            _retryTimer = null;
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
                NoLeftClickDelay = true,
                // Windows 11 风格的右键菜单：库会另开一个透明窗口，用 WinUI 原生渲染
                // MenuFlyout —— 圆角、跟深浅色、悬停高亮，和系统右键菜单同一套观感。
                // ⚠ 默认的 PopupMenu 是库自己画皮模拟的（方角、灰底、没有悬停态），
                //   看着非常朴素 —— 之前就是那个。具体菜单项见 TrayMenuBuilder。
                ContextMenuMode = H.NotifyIcon.ContextMenuMode.SecondWindow
            };

            // ⚠ 2026-10-08：H.NotifyIcon 注册托盘图标时带 NIF_GUID，GUID 由 exe 路径算出来
            //   （库里 CreateUniqueGuidForProcessPath）。系统会把这个 GUID 记进 explorer 的
            //   托盘图标缓存（TrayNotify\IconStreams / PastIconsStream），程序异常退出会留下残记录，
            //   之后再注册就一直失败（"TryCreate failed."）。
            //   实测旁证：不带 GUID 的裸 Shell_NotifyIconW 调用在同一时刻能成功（见 _tray_probe.py）。
            //   所以第一次用库默认的 GUID，失败后每次重试都换一个全新 GUID 绕开那条坏记录。
            if (_createAttempts > 0)
            {
                _icon.Id = Guid.NewGuid();
                StartupLog.Write("[托盘] 第 " + (_createAttempts + 1) + " 次尝试，改用新 GUID " + _icon.Id);
            }

            _flyout = TrayMenuBuilder.Build(_owner, _pendingUpdateVersion);
            _icon.ContextFlyout = _flyout;

            // 左键单击：有待处理更新时打开「关于」面板，否则恢复主界面
            _icon.LeftClickCommand = new TrayRelayCommand(OnLeftClick);

            // 将 TaskbarIcon 挂到主窗口可视树,H.NotifyIcon 的 ContextFlyout/PopupMenu
            // 依赖 FrameworkElement 的 Loaded 状态;纯代码创建不挂载会导致右键菜单不弹出
            if (_owner.Content is Microsoft.UI.Xaml.Controls.Panel rootPanel)
            {
                rootPanel.Children.Add(_icon);
            }

            // ⚠ 2026-10-08 实测：这一段在 MainWindow 构造函数里跑，窗口句柄往往还没建好，
            //   ForceCreate() 会抛 "TryCreate failed."（底层 Shell_NotifyIcon 注册失败）。
            //   以前这个异常是直接往外抛的，但 _icon 已经非空了 —— 于是后面每次 Show()
            //   只是改一下 Visibility 就"成功"了，日志看着正常，右下角其实一个图标都没有。
            //   现在：失败就把 _icon 清掉（让后续 Show 能真的重试）+ 延迟自动补建。
            try
            {
                _icon.ForceCreate();
                _createAttempts = 0;
                _slowAttempts = 0;
                StartupLog.Write("托盘图标已创建 (Icon=" + (_drawingIcon != null) + ")");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("AppTrayIcon.ForceCreate", caught);
                DiscardIcon();
                ScheduleCreateRetry();
            }
        }

        /// <summary>创建失败后的清理：从可视树摘掉并销毁，把 _icon 置空以便下次真重试。</summary>
        private void DiscardIcon()
        {
            try
            {
                if (_icon != null && _owner.Content is Microsoft.UI.Xaml.Controls.Panel panel)
                {
                    panel.Children.Remove(_icon);
                }
            }
            catch (Exception caught) { StartupLog.WriteException("AppTrayIcon.DiscardIcon.Remove", caught); }

            try
            {
                _icon?.Dispose();
            }
            catch (Exception caught) { StartupLog.WriteException("AppTrayIcon.DiscardIcon.Dispose", caught); }

            _icon = null;
            _flyout = null;
        }

        /// <summary>
        /// 托盘图标创建失败后延迟补建。原因多半是「建得太早」：窗口句柄还没就绪，
        /// 或者资源管理器(explorer)刚重启、任务栏还没准备好接收新图标 —— 等一会儿再试就行。
        /// </summary>
        private void ScheduleCreateRetry()
        {
            if (_disposed || _retryScheduled)
            {
                return;
            }

            int delayMs;
            string stage;
            if (_createAttempts < MaxCreateAttempts)
            {
                _createAttempts++;
                delayMs = Math.Min(500 * _createAttempts, 4000);
                stage = "第 " + _createAttempts + " 次补建";
            }
            else
            {
                // 头 8 次都没成，就不再是"建太早"了，而是资源管理器的通知区这会儿不收图标
                // （explorer 通知区卡住 / 被之前反复异常退出搞坏了）。转慢速兜底，等它恢复。
                if (_slowAttempts >= MaxSlowCreateAttempts)
                {
                    StartupLog.Write("[托盘] 兜底重试也用尽，本次运行不再尝试（右下角不会显示托盘图标）。"
                        + "多半是 explorer 的托盘图标缓存坏了：重启 Windows 资源管理器，"
                        + "或清掉 HKCU\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\TrayNotify"
                        + " 下的 IconStreams / PastIconsStream 再重启资源管理器。");
                    return;
                }

                _slowAttempts++;
                delayMs = 30000;
                stage = "慢速兜底第 " + _slowAttempts + " 次（等资源管理器恢复）";
            }

            var dq = _owner.DispatcherQueue;
            if (dq == null)
            {
                StartupLog.Write("[托盘] DispatcherQueue 不可用，无法安排补建");
                return;
            }

            _retryScheduled = true;
            StartupLog.Write("[托盘] 创建失败，安排" + stage + "（" + delayMs + "ms 后）");

            try
            {
                _retryTimer?.Dispose();
                _retryTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        dq.TryEnqueue(() =>
                        {
                            _retryScheduled = false;
                            try
                            {
                                if (!_disposed && _icon == null)
                                {
                                    EnsureCreated();
                                }
                            }
                            catch (Exception caught) { StartupLog.WriteException("AppTrayIcon.Retry.EnsureCreated", caught); }
                        });
                    }
                    catch (Exception caught) { StartupLog.WriteException("AppTrayIcon.Retry.Enqueue", caught); }
                    finally
                    {
                        _retryTimer?.Dispose();
                        _retryTimer = null;
                    }
                }, null, delayMs, System.Threading.Timeout.Infinite);
            }
            catch (Exception caught)
            {
                _retryScheduled = false;
                StartupLog.WriteException("AppTrayIcon.ScheduleCreateRetry", caught);
            }
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

        /// <summary>
        /// 有待处理更新时整体重建菜单（ Builder 会自动带上「发现新版本」那条）。
        /// 重建而不是往旧菜单里插条目 —— 插条目要自己数下标，以后菜单顺序一改就乱。
        /// </summary>
        private void AddOrUpdateMenuItems()
        {
            if (_icon == null || _pendingUpdateVersion == null)
            {
                return;
            }

            try
            {
                _flyout = TrayMenuBuilder.Build(_owner, _pendingUpdateVersion);
                _icon.ContextFlyout = _flyout;
            }
            catch (Exception caught)
            {
                // 重建失败就还用旧菜单，别让一个提示项把整个托盘菜单搞没
                StartupLog.WriteException("AppTrayIcon.AddOrUpdateMenuItems", caught);
            }
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