using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
namespace CelesteMusicPlayer;
// Native HWND hosts the production web surface. The legacy service adapter remains until W13.
internal class NativeMainWebWindow : System.Windows.Forms.Form
{
    internal static bool BackgroundVerification => Array.Exists(Environment.GetCommandLineArgs(), a => a == "--w7-background-test");
    internal DispatcherQueue DispatcherQueue { get; }
    internal AppWindow AppWindow { get; }
    internal static nint HandleOf(object host) => host is NativeMainWebWindow native ? native.Handle : WinRT.Interop.WindowNative.GetWindowHandle(host);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string Title { get => Text; set => Text = value; }
    internal event EventHandler? Closed;
    internal NativeMainWebWindow(DispatcherQueue dispatcher)
    {
        DispatcherQueue = dispatcher;
        AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
        MinimumSize = new System.Drawing.Size(640,480);
        AppWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(Handle));
        FormClosed += (_,_) => Closed?.Invoke(this,EventArgs.Empty);
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int size);
    internal static void SetMaterial(nint hwnd,int backdrop,int dark) { DwmSetWindowAttribute(hwnd,38,ref backdrop,4); DwmSetWindowAttribute(hwnd,20,ref dark,4); }
    public new void Activate() { if (BackgroundVerification) { AppWindow.Hide(); return; } Show(); base.Activate(); }
}

internal sealed class NativeWebViewPanel : System.Windows.Forms.Panel
{
    private Microsoft.Web.WebView2.Core.CoreWebView2Controller? _controller;
    private Microsoft.Web.WebView2.Core.CoreWebView2? _core;
    internal Microsoft.Web.WebView2.Core.CoreWebView2 CoreWebView2 => _core!;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Windows.UI.Color DefaultBackgroundColor { set { if (_controller != null) _controller.DefaultBackgroundColor = value; } }
    internal async System.Threading.Tasks.Task EnsureCoreWebView2Async()
    {
        var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions();
        string? profile = null;
        if (NativeMainWebWindow.BackgroundVerification) { options.AdditionalBrowserArguments = "--remote-debugging-port=9347 --remote-debugging-address=127.0.0.1"; profile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Celeste-W7-" + Environment.ProcessId); }
        var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null,profile,options);
        _controller = await env.CreateCoreWebView2ControllerAsync(Microsoft.Web.WebView2.Core.CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)Handle.ToInt64()));
        _core = _controller.CoreWebView2;
        ResizeView(); _controller.IsVisible = true;
    }
    private void ResizeView() { if (_controller != null) _controller.Bounds = new Windows.Foundation.Rect(0,0,ClientSize.Width,ClientSize.Height); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ResizeView(); }
    internal void Reload() => CoreWebView2.Reload();
    protected override void Dispose(bool disposing) { if(disposing) { _controller?.Close(); _controller=null; } base.Dispose(disposing); }
}
