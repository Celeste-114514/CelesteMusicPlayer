using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WinRT;

namespace CelesteMusicPlayer;

/// <summary>
/// 桌面歌词宿主。刻意**不用 WinUI/XAML**：
/// 歌词要浮在桌面上、背景逐像素全透，XAML 的 Window 走的是另一棵合成树，拿不到 DWM 逐像素混合
/// （实测：给它挂透明 SystemBackdrop 也只会退回 Mica 兜底色 #202020，桌面上是一块不透明的板子）。
/// 所以这里自己建一个 WS_EX_NOREDIRECTIONBITMAP 的裸窗口，用 DirectComposition 做目标，
/// WebView2 走「合成宿主」（CoreWebView2CompositionController）把网页画进合成树。
/// 参考 ECHO / Twilight 的做法：无边框 + transparent 顶层窗 + 网页渲染 + 穿透开关。
/// 只有这一层是 Windows 专有的；页面（WebShell/desktop-lyrics.*）和设置那一套是跨平台可复用的。
/// </summary>
internal sealed class WebDesktopLyricsOverlay : IDisposable
{
    // —— Win32 ——
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const int WmDestroy = 0x0002, WmClose = 0x0010, WmSetCursor = 0x0020, WmMouseMove = 0x0200,
        WmLeftButtonDown = 0x0201, WmLeftButtonUp = 0x0202, WmRightButtonDown = 0x0204, WmRightButtonUp = 0x0205,
        WmMouseWheel = 0x020A, WmMouseLeave = 0x02A3;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoSize = 0x0001;

    private static readonly object WindowGate = new();
    private static readonly Dictionary<IntPtr, WebDesktopLyricsOverlay> Instances = new();
    private static string? _className;
    private static WndProcDelegate? _wndProc;   // 必须留引用，否则委托被回收后窗口过程直接崩

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private readonly MainWindow _owner;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private IntPtr _hwnd;
    private double _scale = 1;
    private double _widthDip = 900, _heightDip = 230;

    // —— 合成 ——
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.Desktop.DesktopWindowTarget? _target;
    private Windows.UI.Composition.ContainerVisual? _root;
    private Windows.UI.Composition.ContainerVisual? _webVisual;

    // —— WebView2 ——
    private CoreWebView2CompositionController? _controller;
    private CoreWebView2? _core;

    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private bool _ready, _disposed, _closing, _paused, _wanted, _locked, _settingsApplied, _trackingLeave, _initializing;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _envRetry;
    private string _direction = "";
    private AppSettingsState _settings = AppSettingsStore.Load();
    private string _lastConfig = "";

    public Func<TimeSpan>? PositionProvider { get; set; }
    public event Action? ClosedByUser;
    public bool IsLocked => _locked;
    public bool IsVisible => !_disposed && _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);
    private (int X, int Y) _lastPosition;
    public (int X, int Y) CurrentPosition => GetWindowRect(_hwnd, out var r) ? (r.Left, r.Top) : _lastPosition;

    public WebDesktopLyricsOverlay(MainWindow owner)
    {
        _owner = owner;
        // 只应当存在一个歌词窗：多开就是两层字叠在一起（点开关 + 启动项同时命中时撞出来过）。
        lock (WindowGate)
        {
            if (Instances.Count > 0)
            {
                StartupLog.Write("[桌面歌词] 已有 " + Instances.Count + " 个实例，先收掉再建");
                foreach (var stale in Instances.Values.ToList()) stale.Close();
            }
        }
        EnsureWindowClass();
        _hwnd = CreateWindowExW(WsExTopmost | WsExToolWindow | WsExNoActivate | WsExNoRedirectionBitmap,
            _className!, "Celeste 桌面歌词", WsPopup, 0, 0, 900, 230, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("桌面歌词窗口创建失败，错误码 " + Marshal.GetLastWin32Error());
        lock (WindowGate) Instances[_hwnd] = this;

        _scale = GetDpiForWindow(_hwnd) / 96.0;
        if (_scale <= 0 || double.IsNaN(_scale)) _scale = 1;
        ResizeForDirection(placeDefault: true);

        try
        {
            _compositor = new Windows.UI.Composition.Compositor();
            var interop = _compositor.As<ICompositorDesktopInterop>();
            interop.CreateDesktopWindowTarget(_hwnd, true, out IntPtr targetPtr);
            _target = WinRT.MarshalInterface<Windows.UI.Composition.Desktop.DesktopWindowTarget>.FromAbi(targetPtr);
            // 两层结构不能省：直接挂到 target 上的根视觉，它的 Size 是被窗口决定的、设了也不算数，
            // 于是网页宿主视觉量出来是 0×0 —— 表现就是"什么都不画"，而且 SendMouseInput 会报
            // "值超出范围"（命中区就是 0×0）。所以根容器用相对尺寸铺满，网页宿主单独给绝对尺寸。
            _root = _compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = System.Numerics.Vector2.One;
            _target.Root = _root;
            _webVisual = _compositor.CreateContainerVisual();
            _webVisual.Size = new System.Numerics.Vector2((float)_widthDip, (float)_heightDip);
            _root.Children.InsertAtTop(_webVisual);
            // 排障用：设了 CELESTE_DL_SOLID=1 就在网页底下铺半透明红。
            // 有红块=合成链路通了、问题在网页；没红块=合成链路没通。别删，下次还靠它分辨。
            if (Environment.GetEnvironmentVariable("CELESTE_DL_SOLID") == "1")
            {
                var debug = _compositor.CreateSpriteVisual();
                debug.Size = new System.Numerics.Vector2((float)_widthDip, (float)_heightDip);
                debug.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(120, 255, 0, 0));
                _root.Children.InsertAtTop(debug);
            }
        }
        catch
        {
            // 合成目标建不起来就没有任何意义：别把裸窗口留成孤儿，交给上层去报错。
            lock (WindowGate) Instances.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            throw;
        }

        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Tick();
        _envRetry = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _envRetry.Interval = TimeSpan.FromMilliseconds(300);
        _envRetry.Tick += (_, _) => Initialize();
        _envRetry.Start();
        StartupLog.Write("[桌面歌词] 裸窗口已创建 hwnd=0x" + _hwnd.ToInt64().ToString("X") + " scale=" + _scale);
        Initialize();
    }

    // —————————————————— 窗口 ——————————————————

    private static void EnsureWindowClass()
    {
        if (_className != null) return;
        _wndProc = StaticWndProc;
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "CelesteDesktopLyricsWeb",
            hCursor = LoadCursorW(IntPtr.Zero, 32512), // IDC_ARROW
        };
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) // 1410=类已存在
            throw new InvalidOperationException("桌面歌词窗口类注册失败，错误码 " + Marshal.GetLastWin32Error());
        _className = "CelesteDesktopLyricsWeb";
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        WebDesktopLyricsOverlay? self;
        lock (WindowGate) Instances.TryGetValue(hwnd, out self);
        if (self != null && self.WndProc(message, wParam, lParam, out IntPtr result)) return result;
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private bool WndProc(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (message)
        {
            case WmClose:
                _wanted = false;
                DestroyWindow(_hwnd);
                return true;
            case WmDestroy:
                _lastPosition = CurrentPosition;
                EndDrag();
                _envRetry?.Stop();
                _disposed = true;
                _timer.Stop();
                lock (WindowGate) Instances.Remove(_hwnd);
                _hwnd = IntPtr.Zero;
                _controller?.Close();
                _controller = null;
                ClosedByUser?.Invoke();
                return true;
        }
        if (message == 0x0215 /*WM_CAPTURECHANGED*/) _dragging = false;
        if (_controller == null) return false;
        try
        {
            switch (message)
            {
                case WmMouseMove:
                    if (_dragging)
                    {
                        if (GetCursorPos(out var cursor))
                            SetWindowPos(_hwnd, IntPtr.Zero, _dragOrigin.Left + cursor.X - _dragStart.X,
                                _dragOrigin.Top + cursor.Y - _dragStart.Y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                        return true;
                    }
                    TrackLeave();
                    return SendMouse(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0, lParam);
                case WmLeftButtonDown:
                    return SendMouse(CoreWebView2MouseEventKind.LeftButtonDown, CoreWebView2MouseEventVirtualKeys.LeftButton, 0, lParam);
                case WmLeftButtonUp:
                    EndDrag();
                    return SendMouse(CoreWebView2MouseEventKind.LeftButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, lParam);
                case WmRightButtonDown:
                    return SendMouse(CoreWebView2MouseEventKind.RightButtonDown, CoreWebView2MouseEventVirtualKeys.RightButton, 0, lParam);
                case WmRightButtonUp:
                    return SendMouse(CoreWebView2MouseEventKind.RightButtonUp, CoreWebView2MouseEventVirtualKeys.None, 0, lParam);
                case WmMouseWheel:
                    // WebView2 takes the signed delta itself, not the packed Win32 high word.
                    uint delta = unchecked((uint)(short)(((long)wParam >> 16) & 0xFFFF));
                    int wheelX = (short)((long)lParam & 0xFFFF), wheelY = (short)(((long)lParam >> 16) & 0xFFFF);
                    var client = new PointStruct { X = wheelX, Y = wheelY };
                    ScreenToClient(_hwnd, ref client);
                    var wheelPoint = new IntPtr((client.Y << 16) | (client.X & 0xFFFF));
                    return SendMouse(CoreWebView2MouseEventKind.Wheel, CoreWebView2MouseEventVirtualKeys.None, delta, wheelPoint);
                case WmMouseLeave:
                    _trackingLeave = false;
                    _controller.SendMouseInput(CoreWebView2MouseEventKind.Leave, CoreWebView2MouseEventVirtualKeys.None, 0, new System.Drawing.Point(0, 0));
                    return true;
                case WmSetCursor:
                    // 合成宿主的 CoreCursor 拿不到 Win32 句柄，光标统一用窗口类默认箭头。
                    return false;
            }
        }
        catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.Input", ex); }
        return false;
    }

    /// <summary>鼠标坐标要从物理像素换算成 DIP（合成树的单位），否则高分屏上点哪都偏。</summary>
    private bool SendMouse(CoreWebView2MouseEventKind kind, CoreWebView2MouseEventVirtualKeys keys, uint data, IntPtr lParam)
    {
        if (_controller == null) return false;
        int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
        _controller.SendMouseInput(kind, keys, data, new System.Drawing.Point((int)Math.Round(x / _scale), (int)Math.Round(y / _scale)));
        return true;
    }

    private void TrackLeave()
    {
        if (_trackingLeave) return;
        _trackingLeave = true;
        var tme = new TrackMouseEventStruct { cbSize = (uint)Marshal.SizeOf<TrackMouseEventStruct>(), dwFlags = 0x2 /*TME_LEAVE*/, hwndTrack = _hwnd };
        TrackMouseEvent(ref tme);
    }

    // —————————————————— WebView2 ——————————————————

    private async void Initialize()
    {
        if (_initializing || _disposed) return;
        // 只复用主界面那个 WebView2 环境：同样是合成环境，省一个浏览器进程。
        // 外壳可能比歌词晚就绪，所以拿不到时由重试定时器再来，而不是自己另建一个环境。
        CoreWebView2Environment? env = _owner.WebShellEnvironment;
        if (env == null) return;
        _initializing = true;
        _envRetry?.Stop();
        try
        {
            _controller = await env.CreateCoreWebView2CompositionControllerAsync(_hwnd);
            if (_disposed) { _controller?.Close(); _controller = null; return; }
            if (_webVisual != null) _controller.RootVisualTarget = _webVisual;
            // 合成宿主也要靠 Bounds 定网页尺寸（官方文档要求）。只设这一项，IsVisible 之前一起加会崩。
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)_widthDip, (int)_heightDip);
            _core = _controller.CoreWebView2;
            _core.Settings.AreDefaultContextMenusEnabled = false;
            _core.Settings.AreDevToolsEnabled = false;
            _core.Settings.IsStatusBarEnabled = false;
            _core.SetVirtualHostNameToFolderMapping("celeste-lyrics.internal", Path.Combine(AppContext.BaseDirectory, "WebShell"), CoreWebView2HostResourceAccessKind.DenyCors);
            _core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://celeste-lyrics.internal/", StringComparison.Ordinal)) e.Cancel = true; };
            _core.NewWindowRequested += (_, e) => e.Handled = true;
            _core.AddWebResourceRequestedFilter("https://celeste-lyric-font.internal/*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnFontRequested;
            _core.WebMessageReceived += OnWebMessage;
            _core.Navigate("https://celeste-lyrics.internal/desktop-lyrics.html");
            StartupLog.Write("[桌面歌词] 合成宿主已挂载，开始载入页面");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("DesktopLyrics.Initialize", ex);
            // 合成宿主挂不上就别把窗口留着当白块，重试交给定时器
            _initializing = false;
            _controller?.Close();
            _controller = null;
            _envRetry?.Start();
        }
    }

    /// <summary>自定义字体：只开放当前选中的那一个文件，读不到给 404（不给响应会让 FontFace 一直挂着等）。</summary>
    private void OnFontRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            if (!e.Request.Uri.StartsWith("https://celeste-lyric-font.internal/", StringComparison.Ordinal)) return;
            string? fontFile = _settings.DesktopLyricFontFile;
            string ext = Path.GetExtension(fontFile ?? string.Empty).ToLowerInvariant();
            string cors = "Access-Control-Allow-Origin: https://celeste-lyrics.internal\r\n";
            if (string.IsNullOrWhiteSpace(fontFile) || !new[] { ".ttf", ".otf", ".woff", ".woff2" }.Contains(ext) || !File.Exists(fontFile))
            {
                e.Response = _core!.Environment.CreateWebResourceResponse(null, 404, "Not Found", cors);
                return;
            }
            string mime = ext switch { ".woff" => "font/woff", ".woff2" => "font/woff2", ".otf" => "font/otf", _ => "font/ttf" };
            e.Response = _core!.Environment.CreateWebResourceResponse(File.OpenRead(fontFile), 200, "OK",
                "Content-Type: " + mime + "\r\n" + cors + "Cache-Control: no-store");
        }
        catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.Font", ex); }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith("https://celeste-lyrics.internal/", StringComparison.Ordinal)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            switch (doc.RootElement.GetProperty("command").GetString())
            {
                case "ready": _ready = true; StartupLog.Write("[桌面歌词] 页面就绪"); PublishLines(); ApplySettings(_settings); _timer.Start(); Tick(); break;
                case "drag": StartDrag(); break;
                case "drag-end": EndDrag(); break;
                case "close": Close(); break;
                case "lock": SetLocked(!_locked); break;
                case "unlock": SetLocked(false); break;
                case "play": MiniPlayerBridge.TogglePlayPause(); break;
                case "previous": MiniPlayerBridge.Previous(); break;
                case "next": MiniPlayerBridge.Next(); break;
                case "stylePatch":
                    DesktopLyricStyles.Apply(doc.RootElement.GetProperty("changes"));
                    ApplySettings(AppSettingsStore.Load());
                    break;
                case "styleList": Post(new { kind = "styles", items = DesktopLyricStyles.List() }); break;
                case "styleSave":
                    DesktopLyricStyles.Save(doc.RootElement.GetProperty("name").GetString() ?? "我的样式");
                    Post(new { kind = "styles", items = DesktopLyricStyles.List() }); break;
                case "styleSelect":
                    DesktopLyricStyles.Select(doc.RootElement.GetProperty("id").GetString() ?? "");
                    ApplySettings(AppSettingsStore.Load()); break;
                case "styleImport":
                    var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".json");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
                    var input = await picker.PickSingleFileAsync();
                    if (input != null) { DesktopLyricStyles.Import(input.Path); Post(new { kind = "styles", items = DesktopLyricStyles.List() }); }
                    break;
                case "styleExport":
                    var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                    savePicker.FileTypeChoices.Add("桌面歌词样式", new[] { ".json" }); savePicker.SuggestedFileName = "celeste-lyrics-style";
                    WinRT.Interop.InitializeWithWindow.Initialize(savePicker, _hwnd);
                    var output = await savePicker.PickSaveFileAsync();
                    if (output != null) await File.WriteAllTextAsync(output.Path, DesktopLyricStyles.Export());
                    break;
                case "resetPosition": ResizeForDirection(placeDefault: true); break;
                case "settings": _owner.ShowWebPage("desktop-lyrics-settings"); break;
                // 锁定 + 穿透时页面收不到任何输入，宿主自己盯着光标：指到解锁条上就临时撤掉穿透。
                case "hintRect":
                    var hint = doc.RootElement;
                    _hintRect = (hint.GetProperty("x").GetDouble(), hint.GetProperty("y").GetDouble(),
                        hint.GetProperty("w").GetDouble(), hint.GetProperty("h").GetDouble());
                    UpdateHintHot();
                    break;
            }
        }
        catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.Command", ex); Post(new { kind = "error", message = ex.Message }); }
    }

    // —————————————————— 对外接口（和原来的 XAML 版一致） ——————————————————

    public void Show()
    {
        if (_disposed) return;
        _wanted = true;
        ShowWindow(_hwnd, 8 /*SW_SHOWNA*/);
        StartupLog.Write("[桌面歌词] 已显示 pos=" + CurrentPosition + " dip=" + _widthDip + "x" + _heightDip);
        Tick();
    }

    public void Close()
    {
        if (_disposed || _closing) return;
        _closing = true; _wanted = false;
        try { if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); }
        catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.Close", ex); _disposed = true; }
    }

    public void Dispose()
    {
        if (_disposed) { ClosedByUser = null; return; }
        ClosedByUser = null;
        Close();
    }

    /// <summary>恢复上次位置，并夹进当前屏幕可见范围（换显示器或改分辨率后不至于跑到屏幕外）。</summary>
    public void SetSavedPosition(int x, int y)
    {
        int width = (int)Math.Round(_widthDip * _scale), height = (int)Math.Round(_heightDip * _scale);
        var area = WorkAreaFor(x, y);
        int maxX = Math.Max(area.Left, area.Right - width), maxY = Math.Max(area.Top, area.Bottom - height);
        SetWindowPos(_hwnd, IntPtr.Zero, Math.Clamp(x, area.Left, maxX), Math.Clamp(y, area.Top, maxY), width, height, SwpNoZOrder | SwpNoActivate);
    }

    public void SetLyrics(IReadOnlyList<LyricLine> lines) { _lines = lines; PublishLines(); }
    private void PublishLines() => Post(new { kind = "lyrics", lines = _lines.Select(l => new { t = l.Time.TotalSeconds, text = l.Text, romanization = l.Romanization, tr = l.IsTranslation, chars = l.CharTimes?.Select(t => t.TotalSeconds) }) });
    public void Sync(TimeSpan time) => Post(new { kind = "clock", time = time.TotalSeconds, paused = _paused, rate = _owner.DesktopLyricsPlaybackRate });
    public void SetPlaybackPaused(bool paused) { _paused = paused; Tick(); }

    /// <summary>光标是否停在解锁条上 —— 决定这一刻要不要临时恢复鼠标事件，否则锁定穿透后解不开。</summary>
    private void UpdateHintHot()
    {
        bool want = _locked && _settings.DesktopLyricClickThrough && _settings.DesktopLyricShowUnlockWhenLocked && _hintRect.W > 0;
        bool hot = false;
        if (want && GetCursorPos(out var p))
        {
            double scale = _scale;
            var origin = CurrentPosition;
            double left = origin.X + _hintRect.X * scale, right = left + _hintRect.W * scale;
            double top = origin.Y + _hintRect.Y * scale, bottom = top + _hintRect.H * scale;
            // 四周放宽 4px：提示条很窄，卡太死会"看得见点不着"。
            hot = p.X >= left - 4 && p.X <= right + 4 && p.Y >= top - 4 && p.Y <= bottom + 4;
        }
        if (hot == _hintHot) return;
        _hintHot = hot;
        ApplyInput();
    }

    public void SetLocked(bool locked)
    {
        EndDrag();
        _locked = locked;
        if (!locked) _hintHot = false;
        Post(new { kind = "lock", locked });
        ApplyInput();
    }

    /// <summary>穿透只能靠窗口扩展样式；合成宿主没有子窗口，所以改完必须让系统重算窗口框架。</summary>
    private void ApplyInput()
    {
        if (_hwnd == IntPtr.Zero) return;
        long style = GetWindowLongPtr(_hwnd, -20).ToInt64();
        bool through = _locked && _settings.DesktopLyricClickThrough && !_hintHot;
        style = through ? style | WsExTransparent : style & ~(long)WsExTransparent;
        SetWindowLongPtr(_hwnd, -20, new IntPtr(style));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x37);
    }

    public void ApplySettings(AppSettingsState s)
    {
        // 「启动即锁定」只在第一次应用时取；之后用户手动解锁不该被别的设置改动重新锁上。
        if (!_settingsApplied) { _locked = s.DesktopLyricLockOnStart; _settingsApplied = true; }
        bool directionChanged = _direction != s.DesktopLyricDirection;
        _settings = s;
        if (directionChanged) { _direction = s.DesktopLyricDirection; ResizeForDirection(); }
        ApplyInput();
        _lastConfig = "";
        Tick();
    }

    /// <summary>横排/竖排切换后重设窗口尺寸。窗口尺寸是物理像素，页面/合成树是 DIP。</summary>
    private void ResizeForDirection(bool placeDefault = false)
    {
        _widthDip = _direction == "Vertical" ? 360 : 900;
        _heightDip = _direction == "Vertical" ? 680 : 230;
        int width = (int)Math.Round(_widthDip * _scale), height = (int)Math.Round(_heightDip * _scale);
        if (_webVisual != null) _webVisual.Size = new System.Numerics.Vector2((float)_widthDip, (float)_heightDip);
        if (_hwnd == IntPtr.Zero) return;
        if (placeDefault)
        {
            var area = WorkAreaFor(0, 0);
            SetWindowPos(_hwnd, IntPtr.Zero, area.Left + (area.Right - area.Left - width) / 2, area.Bottom - height - 60, width, height, SwpNoZOrder | SwpNoActivate);
        }
        else
        {
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height, SwpNoZOrder | SwpNoActivate | 0x0002 /*SWP_NOMOVE*/);
        }
        if (_controller != null) _controller.Bounds = new System.Drawing.Rectangle(0, 0, (int)_widthDip, (int)_heightDip);
        // 尺寸变了解锁条位置也变了：清掉旧的命中矩形，让页面重新量一次。
        _hintRect = (0, 0, 0, 0);
        Post(new { kind = "resize" });
    }

    private void Tick()
    {
        if (_disposed || !_ready || _core == null) return;
        _paused = !_owner.DesktopLyricsPlaying;
        bool hidden = (_settings.DesktopLyricHideWithoutLyric && _lines.Count == 0) || (_settings.DesktopLyricHideWhenPaused && _paused);
        if (_wanted)
        {
            if (hidden && IsVisible) ShowWindow(_hwnd, 0 /*SW_HIDE*/);
            else if (!hidden && !IsVisible) ShowWindow(_hwnd, 8);
        }

        UpdateHintHot();
        var s = _settings;
        var appearance = WebAppearance.HasDraft() ? WebAppearance.Draft() : WebAppearance.Load();
        var palette = WebAppearance.Resolve(appearance, WebAppearance.ResolveMode(appearance.mode));
        bool geek = MainWindow.IsGeekUiStyleActive();
        string phosphor = "";
        if (geek) { var c = MainWindow.GeekPhosphorColor(); phosphor = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
        var config = new
        {
            kind = "settings", s.DesktopLyricFontSize, s.DesktopLyricSecondaryFontSize, s.DesktopLyricFontFamily,
            s.DesktopLyricFontFile, s.DesktopLyricDirection, s.DesktopLyricScale, s.DesktopLyricMusicReactive,
            s.DesktopLyricShowUnlockWhenLocked, s.DesktopLyricColorMode, s.DesktopLyricGradientStart, s.DesktopLyricGradientEnd,
            s.DesktopLyricPlayedColor, s.DesktopLyricUnplayedColor, s.DesktopLyricOutlineColor, s.DesktopLyricOutlineWidth,
            s.DesktopLyricShadowStrength, s.DesktopLyricOpacity, s.DesktopLyricVisibleLines, s.DesktopLyricHideTranslation, s.DesktopLyricShowRomanization,
            s.DesktopLyricAlign, s.DesktopLyricLineSpacing, s.DesktopLyricShowBackgroundBar, s.DesktopLyricBackgroundColor,
            s.LyricOffsetMs, s.LyricKaraokeStyle, locked = _locked, geek, phosphor, palette
        };
        string json = JsonSerializer.Serialize(config);
        if (json != _lastConfig) { _lastConfig = json; _core.PostWebMessageAsJson(json); }
        Sync(PositionProvider?.Invoke() ?? TimeSpan.Zero);
        Post(new { kind = "energy", value = s.DesktopLyricMusicReactive && !hidden && !_paused ? _owner.DesktopLyricsEnergy() : 0 });
    }

    private void Post(object data)
    {
        if (!_ready || _disposed || _core == null) return;
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(data)); }
        catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.Post", ex); }
    }

    // —————————————————— 拖动 ——————————————————

    private bool _dragging;
    private bool _hintHot;
    private (double X, double Y, double W, double H) _hintRect;
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointStruct p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);

    private PointStruct _dragStart;
    private RectStruct _dragOrigin;

    // Capture keeps receiving movement and release even outside the transparent window.
    // Move on the UI thread: cross-thread SetWindowPos can block the input pump.
    private void StartDrag()
    {
        if (_locked || _hwnd == IntPtr.Zero || (GetAsyncKeyState(1) & 0x8000) == 0) return;
        if (!GetCursorPos(out _dragStart) || !GetWindowRect(_hwnd, out _dragOrigin)) return;
        _dragging = true;
        SetCapture(_hwnd);
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseCapture();
    }

    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref PointStruct point);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    // —————————————————— 结构体 / 导入 ——————————————————

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventStruct { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, bool isTopmost, out IntPtr target);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectStruct rect);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, int name);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouseEventStruct info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PointStruct point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize; public RectStruct rcMonitor, rcWork; public uint dwFlags;
    }

    private static RectStruct WorkAreaFor(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new PointStruct { X = x, Y = y }, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info)) return info.rcWork;
        return new RectStruct { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }
}

public sealed partial class MainWindow
{
    internal double DesktopLyricsPlaybackRate => TempoScale();
    internal bool DesktopLyricsPlaying => IsPlaybackActuallyPlaying();
    internal double DesktopLyricsEnergy()
    {
        var data = new float[256];
        if (_audioEngine?.TryGetSamples(data) != true) return 0;
        double sum = 0;
        foreach (float f in data) if (float.IsFinite(f)) sum += f * f;
        return Math.Clamp(Math.Sqrt(sum / data.Length) * 4, 0, 1);
    }
}
