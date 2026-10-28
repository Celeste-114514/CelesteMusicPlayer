using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 托盘服务：通知区图标 + 网页菜单的唯一所有者。
    ///
    /// 所有权只能有一个 —— 旧实现（AppTrayIcon / H.NotifyIcon.WinUI）和新实现不能同时注册，
    /// 否则右下角会出现两个图标、两套回调。MainWindow 里按「新的失败才退回旧的」处理。
    ///
    /// 菜单失败不影响图标：WebView2 起不来时自动退到系统原生弹出菜单（TrayMenuWindow 里判），
    /// 保证「显示主界面 / 播放暂停 / 退出」永远点得到。
    /// </summary>
    internal sealed class WindowsTrayService : ITrayIcon
    {
        private readonly MainWindow _owner;
        private readonly WindowsTrayIcon _icon;
        private readonly TrayMenuWindow _menu;
        private readonly TrayCommandAdapter _commands;

        private string? _pendingUpdateVersion;
        private bool _disposed;
        private bool _opening;

        private Task RunWithUiContext(Func<Task> work)
        {
            var previous = System.Threading.SynchronizationContext.Current;
            try
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(
                    new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(_owner.DispatcherQueue));
                return work();
            }
            finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
        }

        internal WindowsTrayService(MainWindow owner)
        {
            _owner = owner;
            _commands = new TrayCommandAdapter(owner);

            _icon = new WindowsTrayIcon();
            _icon.LeftClick += OnLeftClick;
            _icon.RightClick += OnRightClick;
            _icon.BalloonClick += OnLeftClick;

            _menu = new TrayMenuWindow
            {
                IconRectProvider = () => _icon.TryGetIconRect(out WindowsTrayIcon.RECT rect)
                    ? rect
                    : (WindowsTrayIcon.RECT?)null
            };
            _menu.Invoked += actionId =>
            {
                // Never close a WebView or activate another window inside its callback.
                _owner.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    try { StartupLog.Write("[托盘菜单] 执行命令 " + actionId); _commands.Invoke(actionId); }
                    catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.MenuInvoked", ex); }
                });
            };

            StartupLog.Write("托盘: Win32 通知图标已创建 (成功=" + _icon.IsCreated + ")");
            if (!_icon.IsCreated) ScheduleRetry();
            Prewarm();
        }

        /// <summary>
        /// 开机自启时任务栏可能还没准备好，Shell_NotifyIcon 会失败。旧实现也有这套补建逻辑，
        /// 不能省 —— 否则图标一次没注册上，整场运行都点不到托盘。
        /// Explorer 重启那条路在 WindowsTrayIcon 里（TaskbarCreated 广播），这里只管启动期。
        /// </summary>
        private void ScheduleRetry()
        {
            try
            {
                var queue = _owner.DispatcherQueue;
                if (queue == null) return;
                var timer = queue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(3);
                timer.IsRepeating = true;
                int attempts = 0;
                timer.Tick += (_, _) =>
                {
                    attempts++;
                    if (_disposed || _icon.IsCreated || attempts > 10)
                    {
                        timer.Stop();
                        if (_icon.IsCreated) StartupLog.Write("托盘: 补建成功（第 " + attempts + " 次重试）");
                        else StartupLog.Write("托盘: 补建失败，图标本次运行不可用");
                        return;
                    }
                    _icon.TryAdd();
                };
                timer.Start();
            }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.ScheduleRetry", ex); }
        }

        public bool IsCreated => _icon.IsCreated;

        public void Show()
        {
            if (_disposed) return;
            if (!_icon.IsCreated) _icon.TryAdd();
        }

        public void Hide()
        {
            if (_disposed) return;
            _icon.Remove();
        }

        public void ShowUpdateNotification(string versionTag)
        {
            _pendingUpdateVersion = versionTag;
            _icon.ShowBalloon("CelesteMusicPlayer 更新", "发现新版本 " + versionTag + "，右键托盘图标查看。");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _menu.Dispose(); } catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.Dispose.Menu", ex); }
            try { _icon.Dispose(); } catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.Dispose.Icon", ex); }
        }

        // —— 交互 ——

        private void OnLeftClick()
        {
            if (_disposed) return;
            try
            {
                if (!string.IsNullOrEmpty(_pendingUpdateVersion)) _owner.ShowWebPage("settings");
                else _owner.RestoreFromTray();
            }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.OnLeftClick", ex); }
        }

        private void OnRightClick()
        {
            // Shell_NotifyIcon invokes us inside a native window procedure.
            // Return to the message loop before entering WebView2 or activating UI.
            if (_disposed) return;
            _owner.DispatcherQueue.TryEnqueue(OnRightClickCore);
        }

        private void OnRightClickCore()
        {
            if (_disposed || _opening) return;
            try
            {
                if (_menu.IsVisible) { _menu.Hide(); return; }
                List<TrayMenuModel.Item> items = TrayMenuModel.Build(_owner, _pendingUpdateVersion);
                (string mode, Dictionary<string, string> vars) = BuildTheme();
                _ = RunWithUiContext(() => ShowMenuAsync(items, vars, mode));
            }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.OnRightClick", ex); }
        }

        private async Task ShowMenuAsync(List<TrayMenuModel.Item> items, Dictionary<string, string> vars, string mode)
        {
            _opening = true;
            try
            {
            bool shown = false;
            try { shown = await _menu.ShowAsync(items, vars, mode); }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.ShowMenu", ex); }

            if (shown) return;

            // 降级：系统原生弹出菜单（方角、系统色），但功能齐全。绝不退回 WinUI 菜单。
            try
            {
                string? action = _icon.ShowFallbackMenu(items);
                if (!string.IsNullOrEmpty(action)) _commands.Invoke(action);
            }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.FallbackMenu", ex); }
            }
            finally { _opening = false; }
        }

        /// <summary>
        /// 主题变量直接取 WebAppearance 的解析结果 —— 托盘不另建一套配色，
        /// 网页外壳换皮时这里自动跟上。空值要剔掉，否则会把 CSS 里的兜底值清成空。
        /// </summary>
        private static (string mode, Dictionary<string, string> vars) BuildTheme()
        {
            WebAppearance.State state = WebAppearance.HasDraft() ? WebAppearance.Draft() : WebAppearance.Load();
            string mode = WebAppearance.ResolveMode(state.mode);
            Dictionary<string, string> raw = WebAppearance.Resolve(state, mode);
            var values = new Dictionary<string, string>(raw.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in raw)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value)) values[pair.Key] = pair.Value;
            }
            return (mode, values);
        }

        /// <summary>启动后就悄悄把菜单的 WebView 建好，第一次右键才是即开的。</summary>
        private void Prewarm()
        {
            try
            {
                var queue = _owner.DispatcherQueue;
                if (queue == null) return;
                var timer = queue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(3);
                timer.IsRepeating = false;
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    _ = RunWithUiContext(PrewarmAsync);
                };
                timer.Start();
            }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.Prewarm", ex); }
        }

        private async Task PrewarmAsync()
        {
            if (_disposed) return;
            try
            {
                bool ok = await _menu.PrewarmAsync();
                if (!ok) StartupLog.Write("[托盘菜单] 预热失败，右键时退到系统原生菜单");
            }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayService.PrewarmAsync", ex); }
        }
    }
}
