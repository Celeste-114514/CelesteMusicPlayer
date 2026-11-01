using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
namespace CelesteMusicPlayer;
// Native HWND hosts the production web surface. The legacy service adapter remains until W13.
internal class NativeMainWebWindow : System.Windows.Forms.Form
{
    internal NativeTaskbarPreview? TaskbarPreview;
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
    // 系统接管拖动（HTCAPTION）的时间戳：网页侧那条 drag-start 轮询要避开这窗口，
    // 否则两套机制同时移窗口会互相打架。
    private long _systemDragTicks;
    private bool _systemMoving;
    internal bool SystemDragActive => _systemMoving;
    internal event Action? TaskbarRecreated;
    [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    protected override void WndProc(ref System.Windows.Forms.Message message)
    {
        if (message.Msg == 0x231) _systemMoving = true;
        if (message.Msg == 0x232) _systemMoving = false;
        if ((uint)message.Msg == TaskbarCreatedMessage) { TaskbarPreview?.Enable(); TaskbarRecreated?.Invoke(); }
        if (TaskbarPreview?.Handle((uint)message.Msg, message.LParam) == true) { message.Result = 0; return; }
        // 网页自绘 chrome 的窗口没有非客户区，系统给的最大化矩形就是整块显示器（连任务栏一起盖住），
        // 看起来跟全屏一模一样。这里把它夹回当前显示器的工作区，最大化才是"最大化"。
        if (message.Msg == 0x0024)   // WM_GETMINMAXINFO
        {
            base.WndProc(ref message);
            ClampMaximizeToWorkArea(message.HWnd, message.LParam);
            message.Result = 0;
            return;
        }
        if (this is not WebShellWindow && message.Msg == 0x84 && Microsoft.UI.Input.InputNonClientPointerSource.IsCaption(Handle, message.LParam)) { Interlocked.Exchange(ref _systemDragTicks, Environment.TickCount64); message.Result = (nint)2; return; }
        if (this is WebShellWindow)
        {
            // Do not preserve/copy old client pixels across acrylic resizes: the transparent
            // WebView surface must repaint at its new size, including the former caption area.
            if (message.Msg == 0x83) { message.Result = message.WParam != 0 ? (nint)0x300 : 0; return; } // WVR_REDRAW
            if (message.Msg == 0x85) { message.Result = 0; return; }
            if (message.Msg == 0x86) { message.Result = 1; return; } // Activation must not repaint the removed frame.

        }
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
    private static void ClampMaximizeToWorkArea(nint hwnd, nint lParam)
    {
        nint monitor = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
        if (monitor == 0) return;
        var info = new NMonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NMonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return;
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<NMinMaxInfo>(lParam);
        // 最大化矩形以显示器左上角为原点，位置要换成工作区相对显示器原点的偏移。
        mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        mmi.MaxSize.X = info.Work.Right - info.Work.Left;
        mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
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

        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
        MinimumSize = new System.Drawing.Size(640,480);
        AppWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.Interop.GetWindowIdFromWindow(Handle));
        FormClosed += (_,_) => { Microsoft.UI.Input.InputNonClientPointerSource.Forget(Handle); Closed?.Invoke(this,EventArgs.Empty); };
    }
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
    private void ResizeView()
    {
        if (_controller == null) return;
        var bounds = new System.Drawing.Rectangle(0,0,ClientSize.Width,ClientSize.Height);
        if (_controller.Bounds != bounds) _controller.Bounds = bounds;
    }
    internal void ParentWindowPositionChanged()
    {
        if (_controller == null || IsDisposed) return;
        ResizeView();
        _controller.NotifyParentWindowPositionChanged();
    }
    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); ParentWindowPositionChanged(); }
    protected override void OnLayout(System.Windows.Forms.LayoutEventArgs e) { base.OnLayout(e); ResizeView(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ResizeView(); }
    internal void Reload() => CoreWebView2.Reload();
    protected override void Dispose(bool disposing) { if(disposing) { _controller?.Close(); _controller=null; } base.Dispose(disposing); }
}
