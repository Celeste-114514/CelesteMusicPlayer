using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
namespace CelesteMusicPlayer
{
    // ECHO-style native renderer. No WebView2 controller, browser or navigation delay.
    internal sealed class TaskbarPlayerWindow : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly DispatcherQueue _queue;
        private readonly DispatcherQueueTimer _timer;
        private readonly NativeDesktopPopup _window = new(false, false);
        private PlaybackSnapshot _state = new();
        private Bitmap? _cover;
        private string _coverTrack = "", _background = "#252525", _accent = "#d9b985", _accentStrong = "#b58c55";
        private long _appearanceAt;
        private bool _disposed, _suspended, _shown, _appBar;
        private int _coverGeneration;
        private nint _taskbarHandle;
        private RECT _lastBounds;
        private string _lastStatus = "";
        internal void Suspend(bool value) { _suspended = value; if (value) { _window.Hide(); _shown = false; } }
        internal TaskbarPlayerWindow(MainWindow owner, bool suspended)
        {
            _owner = owner; _queue = owner.DispatcherQueue; _suspended = suspended;
            _window.Paint = Paint;
            _window.Click = point => { float width = NativeDesktopPopup.Bounds(_window.Handle).Width / _window.Scale; int index = (int)((point.X - (width - 90)) / 27); if (point.X >= width - 90 && index >= 0 && index < 3 && !_state.busy) Queue(new[] { "previous", "togglePlay", "next" }[index]); };
            _window.DoubleClick = point => { float width = NativeDesktopPopup.Bounds(_window.Handle).Width / _window.Scale; if (point.X < width - 90) Queue("showMain"); };
            _window.RightClick = _ => _queue.TryEnqueue(() => { if (!_disposed) ShowMenu(); });
            _window.Failed = ex => StartupLog.WriteException("TaskbarNative.Window", ex);
            _timer = _queue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(250); _timer.Tick += (_, _) => Tick(); _timer.Start(); Tick();
        }
        private void Queue(string action) => _queue.TryEnqueue(() => { if (!_disposed) new TrayCommandAdapter(_owner).Invoke(action); });
        private void Tick()
        {
            if (_disposed || _suspended) return;
            try
            {
                if (!TryPlace(out RECT bounds, out nint taskbar)) { _window.Hide(); _shown = false; return; }
                _state = _owner.PlaybackStateSource.Capture(); _background = SampleBackground(taskbar);
                if (Environment.TickCount64 - _appearanceAt >= 1000) { _appearanceAt = Environment.TickCount64; RefreshAppearanceAccent(); }
                if (_coverTrack != _state.trackId) { _coverTrack = _state.trackId; _cover?.Dispose(); _cover = null; int generation = ++_coverGeneration; string? path = _owner.GetCurrentPlayingItem()?.FilePath; _ = LoadCoverAsync(path, generation); }
                if (!_shown || !_lastBounds.Equals(bounds)) { _window.Show(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)); _shown = true; _lastBounds = bounds; Status($"原生显示 {bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height}"); }
                _window.Invalidate();
            }
            catch (Exception ex) { StartupLog.WriteException("TaskbarNative.Tick", ex); _window.Hide(); _shown = false; }
        }
        private async Task LoadCoverAsync(string? path, int generation)
        {
            Bitmap? image = null;
            try
            {
                image = await Task.Run(async () => { if (string.IsNullOrEmpty(path)) return null; string? file = await CoverArtService.GetDesktopCoverFileAsync(path); if (file == null) return null; using var original = Image.FromFile(file); return new Bitmap(original); }).ConfigureAwait(false);
                Bitmap? loaded = image;
                if (!_queue.TryEnqueue(() => { if (_disposed || generation != _coverGeneration) { loaded?.Dispose(); return; } _cover?.Dispose(); _cover = loaded; _window.Invalidate(); })) loaded?.Dispose();
            }
            catch (Exception ex) { image?.Dispose(); StartupLog.WriteException("TaskbarNative.Cover", ex); }
        }
        private void Paint(Graphics g, float width, float height)
        {
            Color background; try { background = ColorTranslator.FromHtml(_background); } catch { background = Color.FromArgb(37, 37, 37); }
            bool light = background.R * .2126 + background.G * .7152 + background.B * .0722 > 145;
            Color ink = light ? Color.FromArgb(56, 51, 46) : Color.FromArgb(238, 232, 223), muted = light ? Color.FromArgb(116, 108, 96) : Color.FromArgb(177, 169, 157), accent = light ? Color.FromArgb(155, 120, 75) : Color.FromArgb(217, 185, 133);
            try { accent = ColorTranslator.FromHtml(light ? _accentStrong : _accent); } catch {/* Keep the readable default when a CSS color is unsupported. */}
            DesktopPopupDrawing.Fill(g, background, new RectangleF(0, 0, width, height)); float art = Math.Min(34, height - 8), top = (height - art) / 2;
            if (_cover != null) g.DrawImage(_cover, new RectangleF(8, top, art, art)); else DesktopPopupDrawing.Text(g, "C", 15, muted, new RectangleF(8, top, art, art));
            DesktopPopupDrawing.Text(g, _state.title, 11, ink, new RectangleF(52, height / 2 - 16, Math.Max(5, width - 148), 16), true);
            DesktopPopupDrawing.Text(g, _state.artist, 9, muted, new RectangleF(52, height / 2 + 1, Math.Max(5, width - 148), 14));
            var actions = new[] { "previous", "togglePlay", "next" }; for (int i = 0; i < 3; i++) DesktopPopupDrawing.Icon(g, actions[i], new RectangleF(width - 85 + i * 27, (height - 16) / 2, 16, 16), _state.busy ? muted : ink, _state.playing);
            double progress = _state.duration > 0 ? Math.Clamp(_state.position / _state.duration, 0, 1) : 0;
            if (double.IsFinite(progress)) DesktopPopupDrawing.Fill(g, accent, new RectangleF(52, height - 2, (float)((width - 64) * progress), 1));
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true; _timer.Stop(); _coverGeneration++; _cover?.Dispose(); _cover = null;
            if (_appBar) { var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _window.Handle }; SHAppBarMessage(1, ref data); }
            _window.Dispose();
        }
        private bool TryPlace(out RECT bounds, out nint taskbar)
        {
            bounds = default; taskbar = FindWindowW("Shell_TrayWnd", null);
            if (taskbar == 0 || !GetWindowRect(taskbar, out RECT bar)) { Status("等待 Explorer 任务栏"); return false; }
            if (_taskbarHandle != taskbar)
            {
                _taskbarHandle = taskbar;
                var registration = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _window.Handle };
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
            if (foreground != 0 && foreground != taskbar && foreground != _window.Handle && GetWindowRect(foreground, out RECT front)
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

        /// <summary>
        /// 取外观设置里的强调色。任务栏条不另建一套配色：背景仍采样任务栏底色（要贴住任务栏），
        /// 强调色/进度条跟随用户在 设置 → 外观 里的选择，换预设这里自动跟上。
        /// </summary>
        private void RefreshAppearanceAccent()
        {
            try
            {
                (string _, System.Collections.Generic.Dictionary<string, string> vars) = WindowsTrayService.BuildTheme();
                if (vars.TryGetValue("accent", out string? accent) && accent.Length > 0) _accent = accent;
                if (vars.TryGetValue("accent-strong", out string? strong) && strong.Length > 0) _accentStrong = strong;
            }
            catch (Exception ex) { StartupLog.WriteException("TaskbarPlayer.RefreshAppearanceAccent", ex); }
        }

        private string SampleBackground(nint taskbar)
        {
            GetWindowRect(taskbar, out RECT rect);
            nint dc = GetDC(0);
            try { uint c = GetPixel(dc, rect.Right - 3, rect.Top + 3); return c == 0xffffffff ? "#252525" : $"#{c & 255:x2}{(c >> 8) & 255:x2}{(c >> 16) & 255:x2}"; }
            finally { ReleaseDC(0, dc); }
        }
        private void Status(string value) { if (_lastStatus == value) return; _lastStatus = value; StartupLog.Write("[任务栏播放条] " + value); }
        private void ShowMenu()
        {
            nint menu = CreatePopupMenu();
            try
            {
                AppendMenuW(menu, 0, 1, "隐藏播放条"); AppendMenuW(menu, 0, 2, "设置");
                GetCursorPos(out POINT point); SetForegroundWindow(_window.Handle);
                int command = TrackPopupMenuEx(menu, 0x100 | 2, point.X, point.Y, _window.Handle, 0);
                if (command == 1) _owner.HideTaskbarPlayer();
                if (command == 2) _owner.ShowWebPage("settings");
            }
            finally { DestroyMenu(menu); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint flags; }
        [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public uint cbSize; public nint hWnd; public uint callback, edge; public RECT rect; public nint param; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string cls, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string cls, string? title);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT rect);
        [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint h, ref MONITORINFO info);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint h);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint h, System.Text.StringBuilder name, int length);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
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
