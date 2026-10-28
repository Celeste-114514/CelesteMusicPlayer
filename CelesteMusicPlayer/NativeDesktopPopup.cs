using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    // Native HWND + buffered GDI+. No browser, XAML content or focus-dependent DOM.
    internal sealed class NativeDesktopPopup : IDisposable
    {
        private readonly Proc _proc;
        private readonly string _className = "CelesteNativePopup_" + Guid.NewGuid().ToString("N");
        private readonly bool _activate;
        private bool _disposed, _skipUp;
        internal nint Handle { get; private set; }
        internal Action<Graphics, float, float>? Paint;
        internal Action<PointF>? Click, DoubleClick, Hover, RightClick;
        internal Action<int>? Key;
        internal Action<Exception>? Failed;
        internal bool Visible => Handle != 0 && IsWindowVisible(Handle);
        internal float Scale => Math.Max(96, GetDpiForWindow(Handle)) / 96f;
        internal NativeDesktopPopup(bool activate, bool rounded = true)
        {
            _activate = activate; _proc = WindowProc;
            var wc = new WNDCLASSEX { size = (uint)Marshal.SizeOf<WNDCLASSEX>(), style = 8, proc = Marshal.GetFunctionPointerForDelegate(_proc), instance = GetModuleHandleW(null), name = _className, cursor = LoadCursorW(0, new nint(32512)) };
            if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("注册原生弹出窗口失败");
            Handle = CreateWindowExW(0x80 | 8 | (activate ? 0 : 0x08000000), _className, "CelesteNativePopup", unchecked((int)0x80000000), 0, 0, 320, 400, 0, 0, wc.instance, 0);
            if (Handle == 0)
            {
                UnregisterClassW(_className, wc.instance);
                throw new InvalidOperationException("创建原生弹出窗口失败");
            }
            int corner = rounded ? 2 : 1; DwmSetWindowAttribute(Handle, 33, ref corner, 4);
        }
        internal void Show(Rectangle bounds)
        {
            SetWindowPos(Handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, (uint)(0x40 | (_activate ? 0 : 0x10)));
            if (_activate) { SetForegroundWindow(Handle); SetFocus(Handle); }
            Invalidate();
        }
        internal void Hide() { if (Handle != 0) ShowWindow(Handle, 0); }
        internal void Invalidate() { if (Handle != 0) InvalidateRect(Handle, 0, false); }
        private nint WindowProc(nint h, uint message, nint w, nint l)
        {
            try
            {
                var point = new PointF((short)(l.ToInt64() & 65535) / Scale, (short)((l.ToInt64() >> 16) & 65535) / Scale);
                switch (message)
                {
                    case 0x21: return _activate ? 1 : 3;
                    case 0x14: return 1;
                    case 0xf:
                        var ps = new PAINTSTRUCT(); nint dc = BeginPaint(h, ref ps);
                        try { if (GetClientRect(h, out RECT r) && r.Right > 0 && r.Bottom > 0) { using var bitmap = new Bitmap(r.Right, r.Bottom); using (var g = Graphics.FromImage(bitmap)) { g.SmoothingMode = SmoothingMode.AntiAlias; g.PageUnit = GraphicsUnit.Pixel; g.ScaleTransform(Scale, Scale); Paint?.Invoke(g, r.Right / Scale, r.Bottom / Scale); } using var target = Graphics.FromHdc(dc); target.DrawImageUnscaled(bitmap, 0, 0); } }
                        finally { EndPaint(h, ref ps); }
                        return 0;
                    case 0x200: Hover?.Invoke(point); return 0;
                    case 0x202: if (_skipUp) _skipUp = false; else Click?.Invoke(point); return 0;
                    case 0x203: _skipUp = true; DoubleClick?.Invoke(point); return 0;
                    case 0x205: RightClick?.Invoke(point); return 0;
                    case 0x100: Key?.Invoke((int)w); return 0;
                    case 0x6:
                        if (_activate && (w.ToInt64() & 65535) == 0) Key?.Invoke(27);
                        break;
                    case 0x10: Key?.Invoke(27); Hide(); return 0;
                }
            }
            catch (Exception ex)
            {
                // A logging failure must never escape across the unmanaged callback.
                try { Failed?.Invoke(ex); } catch { }
            }
            return DefWindowProcW(h, message, w, l);
        }
        public void Dispose() { if (_disposed) return; _disposed = true; if (Handle != 0) { DestroyWindow(Handle); Handle = 0; } UnregisterClassW(_className, GetModuleHandleW(null)); }
        internal static Rectangle Bounds(nint h) { GetWindowRect(h, out RECT r); return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }
        internal static Point Cursor() { GetCursorPos(out POINT p); return new(p.X, p.Y); }
        internal static bool MouseDown() => (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0;
        internal static Rectangle WorkArea(Point p) { var i = new MONITORINFO { size = Marshal.SizeOf<MONITORINFO>() }; GetMonitorInfoW(MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, 2), ref i); return Rectangle.FromLTRB(i.work.Left, i.work.Top, i.work.Right, i.work.Bottom); }
        internal static uint DpiAt(Point p) { uint x = 96, y = 96; GetDpiForMonitor(MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, 2), 0, out x, out y); return x; }
        private delegate nint Proc(nint h, uint m, nint w, nint l);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASSEX { public uint size, style; public nint proc; public int clsExtra, wndExtra; public nint instance, icon, cursor, brush; public string? menu; public string name; public nint smallIcon; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int size; public RECT monitor, work; public uint flags; }
        [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { public nint dc; [MarshalAs(UnmanagedType.Bool)] public bool erase; public RECT rect; [MarshalAs(UnmanagedType.Bool)] public bool restore; [MarshalAs(UnmanagedType.Bool)] public bool update; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] reserved; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string c, nint instance);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(int ex, string cls, string title, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadCursorW(nint instance, nint id);
        [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint h);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
        [DllImport("user32.dll")] private static extern nint SetFocus(nint h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint h);
        [DllImport("user32.dll")] private static extern bool GetClientRect(nint h, out RECT rect);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT rect);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT point, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint h, int attr, ref int value, int size);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(nint h, nint rect, bool erase);
        [DllImport("user32.dll")] private static extern nint BeginPaint(nint h, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(nint h, ref PAINTSTRUCT ps);
    }
}
