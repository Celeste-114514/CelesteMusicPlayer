using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Windows.Foundation;

namespace CelesteMusicPlayer
{
    // An independent non-activating host positioned inside the taskbar rectangle.
    // Owns no audio engine and never reparents windows into Explorer.
    internal sealed class TaskbarPlayerWindow : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly DispatcherQueue _queue;
        private readonly DispatcherQueueTimer _timer;
        private readonly WindowProc _proc;
        private readonly string _className = "CelesteTaskbarPlayer_" + Guid.NewGuid().ToString("N");
        private nint _hwnd;
        private CoreWebView2Controller? _controller;
        private bool _busy, _disposed, _appBar;
        private int _failures, _ticks;
        private string _lastStatus = "";
        private RECT _lastBounds;
        private nint _taskbarHandle;
        private bool _suspended, _shown;
        internal void Suspend(bool value)
        {
            _suspended = value;
            if (value && _hwnd != 0) { ShowWindow(_hwnd, 0); _shown = false; }
        }

        internal TaskbarPlayerWindow(MainWindow owner)
        {
            _owner = owner; _queue = owner.DispatcherQueue; _proc = WndProc;
            _timer = _queue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += (_, _) =>
            {
                var previous = SynchronizationContext.Current;
                try { SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(_queue)); _ = TickAsync(); }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            };
            _timer.Start();
        }

        private async Task TickAsync()
        {
            if (_busy || _disposed) return;
            _busy = true;
            try
            {
                if (_suspended) return;
                if (_controller == null)
                {
                    if (_failures >= 3 || (_failures > 0 && ++_ticks % 10 != 0)) return;
                    var environment = _owner.TraySharedWebViewEnvironment;
                    if (environment == null) return;
                    CreateHost();
                    var controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_hwnd));
                    if (_disposed) { controller.Close(); return; }
                    _controller = controller;
                    controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
                    controller.Bounds = new Rect(0, 0, 360, 48);
                    controller.ZoomFactor = 1;
                    var core = controller.CoreWebView2;
                    core.Settings.IsWebMessageEnabled = true;
                    core.Settings.AreDefaultContextMenusEnabled = false;
                    core.Settings.AreDevToolsEnabled = false;
                    core.Settings.IsStatusBarEnabled = false;
                    core.Settings.IsZoomControlEnabled = false;
                    core.SetVirtualHostNameToFolderMapping("celeste.taskbar", Path.Combine(AppContext.BaseDirectory, "WebShell"), CoreWebView2HostResourceAccessKind.DenyCors);
                    _owner.AttachTaskbarCovers(core);
                    core.WebMessageReceived += (_, args) =>
                    {
                        try
                        {
                            if (args.Source != "https://celeste.taskbar/taskbar-player.html") return;
                            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                            string? action = doc.RootElement.GetProperty("action").GetString();
                            if (action is not ("previous" or "togglePlay" or "next" or "showMain" or "menu")) return;
                            _queue.TryEnqueue(() =>
                            {
                                if (_disposed) return;
                                try
                                {
                                    if (action == "menu") ShowMenu();
                                    else new TrayCommandAdapter(_owner).Invoke(action);
                                }
                                catch (Exception ex) { StartupLog.WriteException("TaskbarPlayer.Command", ex); }
                            });
                        }
                        catch (Exception ex) { StartupLog.WriteException("TaskbarPlayer.Message", ex); }
                    };
                    var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    core.NavigationCompleted += (_, args) => ready.TrySetResult(args.IsSuccess);
                    core.Navigate("https://celeste.taskbar/taskbar-player.html");
                    if (await Task.WhenAny(ready.Task, Task.Delay(20000)) != ready.Task || !await ready.Task)
                        throw new InvalidOperationException("任务栏播放条页面加载失败");
                    if (_disposed) return;
                    controller.IsVisible = true;
                }
                if (_suspended) return;
                if (!TryPlace(out RECT bounds, out nint taskbar)) { ShowWindow(_hwnd, 0); _shown = false; return; }
                string color = SampleBackground(taskbar);
                _controller.DefaultBackgroundColor = ColorHelper.ParseHexColor(color)!.Value;
                string payload = JsonSerializer.Serialize(new { state = _owner.WebPlaybackState(), background = color });
                _controller.CoreWebView2.PostWebMessageAsJson(payload);
                if (!_shown || !_lastBounds.Equals(bounds))
                {
                    SetWindowPos(_hwnd, new nint(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x10 | 0x40);
                    _controller.Bounds = new Rect(0, 0, bounds.Width, bounds.Height);
                    _shown = true;
                }
                if (!_lastBounds.Equals(bounds)) { _lastBounds = bounds; Status($"显示 {bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height}"); }
            }
            catch (Exception ex)
            {
                _failures++; Status("初始化失败 " + _failures + "/3");
                StartupLog.WriteException("TaskbarPlayer.Tick", ex);
                try { _controller?.Close(); } catch { }
                _controller = null;
                if (_hwnd != 0) ShowWindow(_hwnd, 0);
            }
            finally { _busy = false; }
        }

        private bool TryPlace(out RECT bounds, out nint taskbar)
        {
            bounds = default; taskbar = FindWindowW("Shell_TrayWnd", null);
            if (taskbar == 0 || !GetWindowRect(taskbar, out RECT bar)) { Status("等待 Explorer 任务栏"); return false; }
            if (_taskbarHandle != taskbar)
            {
                _taskbarHandle = taskbar;
                var registration = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
                _appBar = SHAppBarMessage(0, ref registration) != 0 || _appBar;
            }
            var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = taskbar };
            if ((SHAppBarMessage(4, ref data).ToInt64() & 1) != 0) { Status("自动隐藏任务栏，暂时隐藏"); return false; }
            nint monitor = MonitorFromWindow(taskbar, 2);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(monitor, ref info)) return false;
            if (bar.Width <= bar.Height || bar.Top < info.rcMonitor.Top + info.rcMonitor.Height / 2)
            { Status("当前仅支持主屏底部任务栏"); return false; }
            nint foreground = GetForegroundWindow();
            var className = new System.Text.StringBuilder(128);
            GetClassNameW(foreground, className, className.Capacity);
            if (foreground != 0 && foreground != taskbar && foreground != _hwnd && GetWindowRect(foreground, out RECT front)
                && className.ToString() is not ("Progman" or "WorkerW")
                && front.Left <= info.rcMonitor.Left && front.Top <= info.rcMonitor.Top
                && front.Right >= info.rcMonitor.Right && front.Bottom >= info.rcMonitor.Bottom)
            { Status("全屏应用期间隐藏"); return false; }
            double scale = Math.Max(1, GetDpiForWindow(taskbar)) / 96.0;
            bool leftAligned = Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", 0)) == 0;
            int notifyLeft = bar.Right, reservedLeft = bar.Left;
            if (leftAligned)
            {
                nint notify = FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
                if (notify == 0 || !GetWindowRect(notify, out RECT tray)) { Status("找不到通知区，暂时隐藏"); return false; }
                notifyLeft = tray.Left;
            }
            else
            {
                // Preserve the Windows Widgets surface when it is exposed as a child HWND.
                nint widgets = FindWindowExW(taskbar, 0, "DynamicContent1", null);
                if (widgets != 0 && GetWindowRect(widgets, out RECT widget)) reservedLeft = widget.Right;
            }
            if (!TaskbarPlayerLayout.TryPlace(bar.Left, bar.Right, scale, leftAligned, notifyLeft, reservedLeft, out int x, out int width))
            { Status("任务栏空间不足"); return false; }
            bounds = new RECT { Left = x, Top = bar.Top, Right = x + width, Bottom = bar.Bottom };
            nint echo = FindWindowW("EchoTaskbarHost", null);
            if (echo != 0 && IsWindowVisible(echo) && GetWindowRect(echo, out RECT other)
                && other.Left < bounds.Right && other.Right > bounds.Left && other.Top < bounds.Bottom && other.Bottom > bounds.Top)
            { Status("ECHO 播放条占用相同位置，请先关闭另一播放条"); return false; }
            return true;
        }

        private string SampleBackground(nint taskbar)
        {
            GetWindowRect(taskbar, out RECT rect);
            nint dc = GetDC(0);
            try { uint c = GetPixel(dc, rect.Right - 3, rect.Top + 3); return c == 0xffffffff ? "#252525" : $"#{c & 255:x2}{(c >> 8) & 255:x2}{(c >> 16) & 255:x2}"; }
            finally { ReleaseDC(0, dc); }
        }
        private void Status(string value) { if (_lastStatus == value) return; _lastStatus = value; StartupLog.Write("[任务栏播放条] " + value); }
        private void CreateHost()
        {
            if (_hwnd != 0) return;
            var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandleW(null), lpszClassName = _className };
            if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("注册任务栏窗口失败");
            _hwnd = CreateWindowExW(0x80 | 0x08000000 | 8, _className, "CelesteTaskbarPlayer", unchecked((int)0x80000000), 0, 0, 360, 48, 0, 0, wc.hInstance, 0);
            if (_hwnd == 0) throw new InvalidOperationException("创建任务栏窗口失败");
            var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
            _appBar = SHAppBarMessage(0, ref data) != 0;
        }
        private nint WndProc(nint hwnd, uint msg, nint w, nint l)
        {
            if (msg == 0x21) return 3; // MA_NOACTIVATE, do not eat mouse input.
            if (msg == 0x10) { _queue.TryEnqueue(_owner.HideTaskbarPlayer); return 0; }
            return DefWindowProcW(hwnd, msg, w, l);
        }
        private void ShowMenu()
        {
            nint menu = CreatePopupMenu();
            try
            {
                AppendMenuW(menu, 0, 1, "隐藏播放条"); AppendMenuW(menu, 0, 2, "设置");
                GetCursorPos(out POINT point); SetForegroundWindow(_hwnd);
                int command = TrackPopupMenuEx(menu, 0x100 | 2, point.X, point.Y, _hwnd, 0);
                if (command == 1) _owner.HideTaskbarPlayer();
                if (command == 2) _owner.ShowWebPage("settings");
            }
            finally { DestroyMenu(menu); }
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _timer.Stop();
            try { _controller?.Close(); } catch (Exception ex) { StartupLog.WriteException("TaskbarPlayer.Dispose", ex); }
            _controller = null;
            if (_appBar) { var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd }; SHAppBarMessage(1, ref data); }
            if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
            UnregisterClassW(_className, GetModuleHandleW(null));
        }
        private delegate nint WindowProc(nint h, uint m, nint w, nint l);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASSEX { public uint cbSize, style; public nint lpfnWndProc; public int clsExtra, wndExtra; public nint hInstance, icon, cursor, background; public string? menuName; public string lpszClassName; public nint iconSmall; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint flags; }
        [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public uint cbSize; public nint hWnd; public uint callback, edge; public RECT rect; public nint param; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(int ex, string cls, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string cls, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string cls, string? title);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT rect);
        [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint h, ref MONITORINFO info);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint h);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint h, System.Text.StringBuilder name, int length);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int command);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
        [DllImport("shell32.dll")] private static extern nint SHAppBarMessage(uint message, ref APPBARDATA data);
        [DllImport("user32.dll")] private static extern nint GetDC(nint h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint h, nint dc);
        [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
        [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string text);
        [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint h, nint param);
        [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    }
}
