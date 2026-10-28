using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;

namespace CelesteMusicPlayer;

/// <summary>Web presentation only; all audio commands use the existing mini bridge.</summary>
internal sealed class WebMiniPlayerWindow : Window
{
    // Keep the browser surface stable: resizing it every frame recreates browser
    // layout/surface work. The native client bounds clip this fixed canvas.
    private readonly WebView2 _web = new() { Height = 620, VerticalAlignment = VerticalAlignment.Top };
    private readonly Grid _root = new();
    private bool _closed, _ready;
    private byte[]? _cover;
    private object? _lines = new object();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _themeTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _resizeTimer;
    private readonly Stopwatch _resizeClock = new();
    private double _height = 188, _fromHeight, _targetHeight;
    private int _dragGeneration, _themeRevision = -1;
    private string _themeMode = "";
    private int _lastPixelHeight = -1, _lastPixelWidth = -1;
    private double _lastResizeFrame = -100;
    internal WebMiniPlayerWindow()
    {
        Title = "Celeste · 迷你播放器";
        WindowIconHelper.Apply(this);
        _root.Children.Add(_web);
        Content = _root;
        _themeTimer = DispatcherQueue.CreateTimer();
        _themeTimer.Interval = TimeSpan.FromMilliseconds(500);
        _themeTimer.Tick += (_, _) => SendTheme();
        _resizeTimer = DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(16);
        _resizeTimer.Tick += AnimateResize;
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false; p.IsMaximizable = false;
            p.IsAlwaysOnTop = AppSettingsStore.Load().MiniPlayerAlwaysOnTop;
        }
        Resize(false);
        _root.Loaded += Initialize;
        MiniPlayerBridge.StateChanged += StateChanged;
        Closed += (_, _) => { _closed = true; Interlocked.Increment(ref _dragGeneration); _themeTimer.Stop(); StopResize(); MiniPlayerBridge.StateChanged -= StateChanged; _web.Close(); };
    }
    private void Resize(bool expanded)
    {
        _height = expanded ? 620 : 188;
        ApplyHeight();
    }
    private void ApplyHeight()
    {
        double scale = _root.XamlRoot?.RasterizationScale ?? 1;
        int width = (int)Math.Round(400 * scale), height = (int)Math.Round(_height * scale);
        if (width == _lastPixelWidth && height == _lastPixelHeight) return;
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, height));
        _lastPixelWidth = width; _lastPixelHeight = height;
    }
    private void Expand(bool expanded, bool animate)
    {
        StopResize();
        _fromHeight = _height; _targetHeight = expanded ? 620 : 188;
        if (!animate) { Resize(expanded); Post(new { kind = "resized", expanded }); return; }
        _resizeClock.Restart();
        _lastResizeFrame = -100;
        CompositionTarget.Rendering += AnimateResize;
        _resizeTimer.Start();
    }
    private void AnimateResize(object? sender, object e)
    {
        double elapsed = _resizeClock.Elapsed.TotalMilliseconds;
        // The fallback must not resize again immediately after a render callback.
        if (ReferenceEquals(sender, _resizeTimer) && elapsed - _lastResizeFrame < 50 && elapsed < 260) return;
        if (elapsed - _lastResizeFrame < 4 && elapsed < 260) return;
        _lastResizeFrame = elapsed;
        double t = Math.Clamp(elapsed / 260, 0, 1);
        _height = _fromHeight + (_targetHeight - _fromHeight) * (1 - Math.Pow(1 - t, 3));
        ApplyHeight();
        if (t >= 1) { StopResize(); Post(new { kind = "resized", expanded = _targetHeight > 188 }); }
    }
    private void StopResize() { CompositionTarget.Rendering -= AnimateResize; _resizeTimer.Stop(); _resizeClock.Stop(); }
    private void SendTheme()
    {
        if (!_ready || _closed) return;
        var state = WebAppearance.HasDraft() ? WebAppearance.Draft() : WebAppearance.Load();
        string mode = WebAppearance.ResolveMode(state.mode);
        if (_themeRevision == WebAppearance.CurrentRevision && _themeMode == mode) return;
        _themeRevision = WebAppearance.CurrentRevision; _themeMode = mode;
        var light = WebAppearance.Resolve(state, WebAppearance.Light);
        var dark = WebAppearance.Resolve(state, WebAppearance.Dark);
        SetNativeBackground(mode == "dark" ? dark : light);
        Post(new { kind = "theme", mode, light, dark });
    }
    private void SetNativeBackground(System.Collections.Generic.Dictionary<string, string> palette)
    {
        try
        {
            string value = palette.TryGetValue("shell", out var shell) ? shell
                : palette.TryGetValue("bg", out var bg) ? bg : "#fcf9f8";
            var color = Windows.UI.Color.FromArgb(255,
                Convert.ToByte(value.Substring(1, 2), 16), Convert.ToByte(value.Substring(3, 2), 16), Convert.ToByte(value.Substring(5, 2), 16));
            _root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            if (_web.CoreWebView2 != null) _web.DefaultBackgroundColor = color;
        }
        catch (Exception ex) { StartupLog.WriteException("WebMiniPlayer.SetNativeBackground", ex); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    private void StartDrag()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!GetCursorPos(out var cursor) || !GetWindowRect(hwnd, out var rect) || (GetAsyncKeyState(1) & 0x8000) == 0) return;
        int dx = cursor.X - rect.Left, dy = cursor.Y - rect.Top, ticket = Interlocked.Increment(ref _dragGeneration);
        new Thread(() =>
        {
            int x = rect.Left, y = rect.Top;
            while (Volatile.Read(ref _dragGeneration) == ticket && (GetAsyncKeyState(1) & 0x8000) != 0)
            {
                if (!GetCursorPos(out var point)) break;
                if (x != point.X - dx || y != point.Y - dy) { x = point.X - dx; y = point.Y - dy; if (!SetWindowPos(hwnd, 0, x, y, 0, 0, 0x0015)) break; }
                Thread.Sleep(4);
            }
        }) { IsBackground = true, Name = "Celeste mini drag" }.Start();
    }
    private async void Initialize(object sender, RoutedEventArgs e)
    {
        _root.Loaded -= Initialize;
        Resize(false);
        try
        {
            await _web.EnsureCoreWebView2Async();
            if (_closed) return;
            var core = _web.CoreWebView2;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.SetVirtualHostNameToFolderMapping("celeste-mini.internal", Path.Combine(AppContext.BaseDirectory, "WebShell"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith("https://celeste-mini.internal/", StringComparison.Ordinal)) a.Cancel = true; };
            core.NewWindowRequested += (_, a) => a.Handled = true;
            core.WebMessageReceived += (_, a) =>
            {
                if (_closed || !a.Source.StartsWith("https://celeste-mini.internal/", StringComparison.Ordinal)) return;
                try
                {
                    using var doc = JsonDocument.Parse(a.WebMessageAsJson);
                    var m = doc.RootElement;
                    switch (m.GetProperty("command").GetString())
                    {
                        case "ready": _ready = true; _themeRevision = -1; SendTheme(); _themeTimer.Start(); MiniPlayerBridge.Push(); StateChanged(MiniPlayerBridge.Current); break;
                        case "drag-start": StartDrag(); break;
                        case "drag-end": Interlocked.Increment(ref _dragGeneration); break;
                        case "settings": MiniPlayerBridge.OpenAppearanceSettings(); break;
                        case "play": MiniPlayerBridge.TogglePlayPause(); break;
                        case "previous": MiniPlayerBridge.Previous(); break;
                        case "next": MiniPlayerBridge.Next(); break;
                        case "seek": if (m.GetProperty("value").TryGetDouble(out var t) && double.IsFinite(t)) MiniPlayerBridge.Seek(Math.Clamp(t, 0, MiniPlayerBridge.Current.DurationSeconds)); break;
                        case "volume": if (m.GetProperty("value").TryGetDouble(out var v) && double.IsFinite(v)) MiniPlayerBridge.SetVolume(Math.Clamp(v, 0, 100)); break;
                        case "queue": MiniPlayerBridge.PlayQueueAt(m.GetProperty("value").GetInt32()); break;
                        case "expand": Expand(m.GetProperty("value").GetBoolean(), !m.TryGetProperty("animate", out var animate) || animate.GetBoolean()); break;
                        case "main": MiniPlayerBridge.BringMainWindowToFront(); break;
                        case "close": Close(); break;
                        case "minimize": (AppWindow.Presenter as OverlappedPresenter)?.Minimize(); break;
                        case "pin":
                            if (AppWindow.Presenter is OverlappedPresenter p) { p.IsAlwaysOnTop = !p.IsAlwaysOnTop; var s = AppSettingsStore.Load(); s.MiniPlayerAlwaysOnTop = p.IsAlwaysOnTop; AppSettingsStore.Save(s); Post(new { kind = "pin", value = p.IsAlwaysOnTop }); }
                            break;
                    }
                }
                catch (Exception ex) { StartupLog.WriteException("WebMiniPlayer.command", ex); }
            };
            core.Navigate("https://celeste-mini.internal/mini-player.html");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("WebMiniPlayer.initialize", ex);
            _root.Children.Clear();
            var retry = new Button { Content = "加载失败，点击重新加载", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            retry.Click += (_, _) => { _root.Children.Clear(); _root.Children.Add(_web); Initialize(_root, new RoutedEventArgs()); };
            _root.Children.Add(retry);
        }
    }
    private void Post(object value) { if (_ready && !_closed) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value)); }
    private void StateChanged(MiniPlayerSnapshot s)
    {
        if (!_ready || _closed) return;
        if (!ReferenceEquals(_cover, s.CoverBytes)) { _cover = s.CoverBytes; Post(new { kind = "cover", value = _cover is { Length: > 0 } ? "data:image/jpeg;base64," + Convert.ToBase64String(_cover) : "" }); }
        if (!ReferenceEquals(_lines, s.LyricLines)) { _lines = s.LyricLines; Post(new { kind = "lyrics", value = s.LyricLines?.Select(l => new { text = l.Text, time = l.Time.TotalSeconds }) }); }
        Post(new { kind = "state", s.Title, s.Artist, position = s.PositionSeconds, duration = s.DurationSeconds, playing = s.IsPlaying, volume = s.Volume, queue = s.QueueTitles, queueIndex = s.QueueIndex, lyricIndex = s.LyricIndex, pinned = (AppWindow.Presenter as OverlappedPresenter)?.IsAlwaysOnTop ?? false });
    }
}
