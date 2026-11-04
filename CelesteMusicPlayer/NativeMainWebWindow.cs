using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
namespace CelesteMusicPlayer;
// Native HWND hosts the production web surface. The legacy service adapter remains until W13.
internal class NativeMainWebWindow : System.Windows.Forms.Form
{
    internal NativeTaskbarPreview? TaskbarPreview;
    protected virtual bool ClientOnlyChrome => false;
    internal bool UsesClientOnlyChrome => ClientOnlyChrome;
    protected override System.Windows.Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            if (ClientOnlyChrome)
                parameters.Style = (parameters.Style & ~0x00C00000) | 0x000F0000;
            return parameters;
        }
    }
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
    internal event Action? TaskbarRecreated;
    [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    protected override void WndProc(ref System.Windows.Forms.Message message)
    {
        if ((uint)message.Msg == TaskbarCreatedMessage) { TaskbarPreview?.Enable(); TaskbarRecreated?.Invoke(); }
        if (TaskbarPreview?.Handle((uint)message.Msg, message.LParam) == true) { message.Result = 0; return; }
        if (ClientOnlyChrome && message.Msg == 0x0085) // WM_NCPAINT
        {
            PaintResizeFrame(message.HWnd);
            message.Result = 0;
            return;
        }
        if (ClientOnlyChrome && message.Msg == 0x0086) // WM_NCACTIVATE
        {
            // Update activation state without asking Windows to repaint the accent frame.
            message.LParam = -1;
            base.WndProc(ref message);
            PaintResizeFrame(message.HWnd);
            return;
        }
        // Fit the maximized client to the current monitor work area.
        if (message.Msg == 0x0024)   // WM_GETMINMAXINFO
        {
            base.WndProc(ref message);
            ClampMaximizeToWorkArea(message.HWnd, message.LParam);
            message.Result = 0;
            return;
        }
        if (!ClientOnlyChrome && message.Msg == 0x84 && Microsoft.UI.Input.InputNonClientPointerSource.IsCaption(Handle, message.LParam)) { message.Result = (nint)2; return; }
        base.WndProc(ref message);
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NPoint { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NMinMaxInfo { public NPoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NMonitorInfo { public int Size; public NRect Monitor, Work; public int Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, int flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref NMonitorInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AdjustWindowRectExForDpi(ref NRect rect, uint style, bool hasMenu, uint exStyle, uint dpi);
    private void ClampMaximizeToWorkArea(nint hwnd, nint lParam)
    {
        nint monitor = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
        if (monitor == 0) return;
        var info = new NMonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NMonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return;
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<NMinMaxInfo>(lParam);
        var outer = info.Work;
        if (ClientOnlyChrome)
        {
            // The native sizing frame sits outside the work-area-sized client.
            uint style = unchecked((uint)NativeMethods.GetWindowLong(hwnd, -16).ToInt64());
            uint exStyle = unchecked((uint)NativeMethods.GetWindowLong(hwnd, -20).ToInt64());
            AdjustWindowRectExForDpi(ref outer, style, false, exStyle, GetDpiForWindow(hwnd));
        }
        mmi.MaxPosition.X = outer.Left - info.Monitor.Left;
        mmi.MaxPosition.Y = outer.Top - info.Monitor.Top;
        mmi.MaxSize.X = outer.Right - outer.Left;
        mmi.MaxSize.Y = outer.Bottom - outer.Top;
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
    }
    internal static bool BackgroundVerification => Array.Exists(Environment.GetCommandLineArgs(), a => a == "--w7-background-test");

    /// <summary>
    /// 主界面 WebView2 用的用户数据目录（null = 默认目录；后台验证模式是临时目录）。
    /// 桌面歌词在**自己的线程**上建独立环境时必须用同一个值，
    /// 否则两个环境参数不一致 → 建环境直接失败（而且绝不能跨线程复用主线程的环境对象：
    /// CoreWebView2Environment 不是敏捷对象，跨套间调用会让 CLR 报 0x80131506 直接杀进程）。
    /// </summary>
    internal static string? WebViewProfilePath { get; set; }

    /// <summary>创建环境时的浏览器参数（后台验证要带调试端口），两个环境必须完全一致才能共用浏览器进程。</summary>
    internal static string? WebViewBrowserArguments { get; set; }

    /// <summary>
    /// 建 WebView2 环境用的参数。主界面还没建环境时也能算出正确值，
    /// 避免桌面歌词抢先建出一个参数不同的环境（那样就不能共用浏览器进程了）。
    /// </summary>
    internal static (string? Profile, string? Arguments) WebViewEnvironmentSpec()
    {
        if (WebViewProfilePath != null || WebViewBrowserArguments != null)
        {
            return (WebViewProfilePath, WebViewBrowserArguments);
        }

        if (BackgroundVerification)
        {
            return (System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Celeste-W7-" + Environment.ProcessId),
                "--remote-debugging-port=9347 --remote-debugging-address=127.0.0.1");
        }

        return (null, null);
    }
    internal DispatcherQueue DispatcherQueue { get; }
    internal AppWindow AppWindow { get; }
    internal static nint HandleOf(object host) => host is NativeMainWebWindow native ? native.Handle : host is Microsoft.UI.Xaml.Window service ? service.NativeHandle : WinRT.Interop.WindowNative.GetWindowHandle(host);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Title { get => Text; set => Text = value; }
    internal event EventHandler? Closed;
    internal NativeMainWebWindow(DispatcherQueue dispatcher)
    {
        DispatcherQueue = dispatcher;
        string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath)) Icon = new System.Drawing.Icon(iconPath);

        // The managed and native border models must agree. Mutating only HWND styles
        // leaves WinForms sizing its children as a standard captioned form on restore.
        if (ClientOnlyChrome) FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
        MinimumSize = new System.Drawing.Size(640,480);
        AppWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.Interop.GetWindowIdFromWindow(Handle));
        FormClosed += (_,_) => { Microsoft.UI.Input.InputNonClientPointerSource.Forget(Handle); Closed?.Invoke(this,EventArgs.Empty); };
    }
    private (int Width, int Height, int Diameter)? _cornerMask;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!ClientOnlyChrome) return;
        int disabled = 1; // DWMWA_NCRENDERING_POLICY / DWMNCRP_DISABLED
        DwmSetWindowAttribute(Handle, 2, ref disabled, sizeof(int));
        _cornerMask = null;
        UpdateCornerMask();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateCornerMask();
    }

    protected override void OnDpiChanged(System.Windows.Forms.DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateCornerMask();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (ClientOnlyChrome && IsHandleCreated) PaintResizeFrame(Handle);
    }

    protected void UpdateCornerMask()
    {
        if (!ClientOnlyChrome || !IsHandleCreated || WindowState == System.Windows.Forms.FormWindowState.Minimized) return;
        if (WindowState == System.Windows.Forms.FormWindowState.Maximized ||
            AppWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            if (_cornerMask != null && SetWindowRgn(Handle, 0, true) != 0) _cornerMask = null;
            return;
        }
        // DWM offers preset sizes only. Clip the opaque top-level window at 14 DIPs.
        int diameter = (int)Math.Round(28 * GetDpiForWindow(Handle) / 96.0);
        var mask = (Width, Height, diameter);
        if (_cornerMask == mask || Width < diameter || Height < diameter) return;
        nint region = CreateRoundRectRgn(0, 0, Width, Height, diameter, diameter);
        if (region == 0) return;
        if (SetWindowRgn(Handle, region, true) != 0) _cornerMask = mask; // HWND now owns the region.
        else DeleteObject(region);
    }

    private void PaintResizeFrame(nint hwnd)
    {
        // Paint only the non-client strip. Keep the native frame geometry and
        // hit testing that Windows uses for resizing and Snap.
        var origin = new NPoint();
        if (!GetWindowRect(hwnd, out var window) ||
            !GetClientRect(hwnd, out var client) ||
            !ClientToScreen(hwnd, ref origin)) return;
        nint dc = GetWindowDC(hwnd);
        if (dc == 0) return;
        nint brush = 0;
        try
        {
            int x = origin.X - window.Left;
            int y = origin.Y - window.Top;
            if (ExcludeClipRect(dc, x, y, x + client.Right, y + client.Bottom) <= 1) return;
            var color = BackColor;
            brush = CreateSolidBrush((uint)(color.R | color.G << 8 | color.B << 16));
            var bounds = new NRect { Right = window.Right - window.Left, Bottom = window.Bottom - window.Top };
            if (brush != 0) FillRect(dc, ref bounds, brush);
        }
        finally
        {
            if (brush != 0) DeleteObject(brush);
            ReleaseDC(hwnd, dc);
        }
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NPoint point);
    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NRect rect, nint brush);
    [DllImport("gdi32.dll")] private static extern int ExcludeClipRect(nint dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int size);
    internal static void SetMaterial(nint hwnd,int backdrop,int dark) { DwmSetWindowAttribute(hwnd,38,ref backdrop,4); DwmSetWindowAttribute(hwnd,20,ref dark,4); }
    // 网页自绘 chrome 的窗口收尾：去掉系统那圈 1px 边线，并保留 Win11 圆角。
    // 边线不关的话，聚焦时窗口四周会浮出一条主题色细线（顶部那条粗带的同源问题）。
    internal static void SetFrameless(nint hwnd)
    {
        int rounded=2;          // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd,33,ref rounded,4);
        int none=unchecked((int)0xFFFFFFFE);   // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(hwnd,34,ref none,4);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    public new void Activate() { if (BackgroundVerification) { AppWindow.Hide(); return; } Show(); base.Activate(); SetForegroundWindow(Handle); }
}

internal sealed class NativeWebViewPanel : System.Windows.Forms.Panel
{
    private Microsoft.Web.WebView2.Core.CoreWebView2Controller? _controller;
    private Microsoft.Web.WebView2.Core.CoreWebView2? _core;
    internal Microsoft.Web.WebView2.Core.CoreWebView2 CoreWebView2 => _core!;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Windows.UI.Color DefaultBackgroundColor { set { if (_controller != null) _controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(value.A,value.R,value.G,value.B); } }
    internal async System.Threading.Tasks.Task EnsureCoreWebView2Async()
    {
        var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions();
        string? profile = null;
        if (NativeMainWebWindow.BackgroundVerification) { options.AdditionalBrowserArguments = "--remote-debugging-port=9347 --remote-debugging-address=127.0.0.1"; profile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Celeste-W7-" + Environment.ProcessId); }
        // 记下参数：桌面歌词线程要照这套建自己的环境（同一份用户数据目录 → 共用浏览器进程）
        NativeMainWebWindow.WebViewProfilePath = profile;
        NativeMainWebWindow.WebViewBrowserArguments = options.AdditionalBrowserArguments;
        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null,profile,options);
        _controller = await env.CreateCoreWebView2ControllerAsync(Handle);
        _core = _controller.CoreWebView2;
        ResizeView(); _controller.IsVisible = true;
    }
    private bool _aligningParent;
    private void AlignToParentClient()
    {
        // Windows can translate child HWNDs while snapping/restoring custom chrome.
        // Dock layout alone does not run for that native translation; keep the web
        // host anchored immediately, not only after the mouse button is released.
        if (_aligningParent || Dock != System.Windows.Forms.DockStyle.Fill || Parent is not NativeMainWebWindow { UsesClientOnlyChrome: true } parent) return;
        var bounds = parent.ClientRectangle;
        if (Bounds == bounds) return;
        _aligningParent = true;
        try { Bounds = bounds; }
        finally { _aligningParent = false; }
    }
    private void ResizeView()
    {
        if (_controller == null) return;
        var bounds = new System.Drawing.Rectangle(0,0,ClientSize.Width,ClientSize.Height);
        if (_controller.Bounds != bounds) _controller.Bounds = bounds;
    }
    internal string Geometry => $"panel={Bounds},screen={PointToScreen(System.Drawing.Point.Empty)},controller={_controller?.Bounds},parent={_controller?.ParentWindow}";
    internal void ParentWindowPositionChanged()
    {
        if (_controller == null || IsDisposed) return;
        AlignToParentClient();
        ResizeView();
        _controller.NotifyParentWindowPositionChanged();
    }
    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); ParentWindowPositionChanged(); }
    protected override void OnLayout(System.Windows.Forms.LayoutEventArgs e) { base.OnLayout(e); AlignToParentClient(); ResizeView(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); AlignToParentClient(); ResizeView(); }
    internal void Reload() => CoreWebView2.Reload();
    protected override void Dispose(bool disposing) { if(disposing) { _controller?.Close(); _controller=null; } base.Dispose(disposing); }
}
