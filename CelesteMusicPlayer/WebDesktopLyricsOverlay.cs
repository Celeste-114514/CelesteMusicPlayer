using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
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
    private Windows.System.DispatcherQueueTimer? _timer;
    private IntPtr _hwnd;
    private double _scale = 1;
    private double _widthDip = 900, _heightDip = 230;

    // —— 合成 ——
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.Desktop.DesktopWindowTarget? _target;
    private Windows.UI.Composition.ContainerVisual? _root;
    private Windows.UI.Composition.ContainerVisual? _webVisual;

    // —— WebView2 ——
    private CoreWebView2Environment? _environment;
    private CoreWebView2CompositionController? _controller;
    private CoreWebView2? _core;

    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private bool _ready, _disposed, _closing, _paused, _wanted, _locked, _settingsApplied, _trackingLeave, _initializing;
    private Windows.System.DispatcherQueueTimer? _envRetry;
    private string _direction = "";
    private AppSettingsState _settings = AppSettingsStore.Load();
    private string _lastConfig = "";

    // —— 专用线程 ——
    // 窗口、合成树、WebView2 全部绑在 _queue 所属的那个线程上，本类的状态也只在该线程读写。
    // 对外的公开方法一律编组过去（原因见 EnsureLyricsThread 的注释）。
    private Windows.System.DispatcherQueue? _queue;

    private volatile Func<TimeSpan>? _positionProvider;

    public Func<TimeSpan>? PositionProvider
    {
        get => _positionProvider;
        set => _positionProvider = value;
    }
    public event Action? ClosedByUser;
    public bool IsLocked => _locked;
    public bool IsVisible => !_disposed && _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);
    private (int X, int Y) _lastPosition;
    public (int X, int Y) CurrentPosition => GetWindowRect(_hwnd, out var r) ? (r.Left, r.Top) : _lastPosition;

    public WebDesktopLyricsOverlay(MainWindow owner)
    {
        _owner = owner;

        // 创建必须落在「自带系统级 DispatcherQueue」的专用线程上，见 EnsureLyricsThread 的注释。
        Windows.System.DispatcherQueue queue = EnsureLyricsThread();
        _queue = queue;

        Exception? failure = null;
        using (var ready = new ManualResetEventSlim(false))
        {
            if (!queue.TryEnqueue(() =>
            {
                try { CreateWindowAndComposition(); }
                catch (Exception ex)
                {
                    failure = ex;
                    // 创建失败要把窗口收干净，别把裸窗口留成孤儿
                    try { if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); } catch { /* 已经在失败路径上 */ }
                    lock (WindowGate) { if (_hwnd != IntPtr.Zero) Instances.Remove(_hwnd); }
                    _hwnd = IntPtr.Zero;
                }
                finally { ready.Set(); }
            }))
            {
                throw new InvalidOperationException("桌面歌词线程已停止，无法创建窗口");
            }

            // 构造必须同步完成：调用方紧接着就要 ApplySettings / SetSavedPosition / Show。
            if (!ready.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new InvalidOperationException("桌面歌词窗口创建超时");
            }
        }

        if (failure != null)
        {
            StartupLog.WriteException("DesktopLyrics.Create", failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>窗口 + 合成树 + WebView2 的创建（只在桌面歌词线程上跑）。</summary>
    private void CreateWindowAndComposition()
    {
        // 只应当存在一个歌词窗：多开就是两层字叠在一起（点开关 + 启动项同时命中时撞出来过）。
        // ⚠ 不能在持有 WindowGate 的时候关旧窗：关闭会走 DestroyWindow → WM_DESTROY，
        //    那条路径要拿同一把锁，跨线程等待会直接死锁。所以先取快照，再在锁外收。
        List<WebDesktopLyricsOverlay> stale;
        lock (WindowGate) stale = Instances.Values.ToList();
        if (stale.Count > 0)
        {
            StartupLog.Write("[桌面歌词] 已有 " + stale.Count + " 个实例，先收掉再建");
            foreach (WebDesktopLyricsOverlay old in stale) old.Close();
        }

        EnsureWindowClass();
        _hwnd = CreateWindowExW(WsExTopmost | WsExToolWindow | WsExNoActivate | WsExNoRedirectionBitmap,
            _className!, "Celeste 桌面歌词", WsPopup, 0, 0, 900, 230, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("桌面歌词窗口创建失败，错误码 " + Marshal.GetLastWin32Error());
        lock (WindowGate) Instances[_hwnd] = this;

        _scale = GetDpiForWindow(_hwnd) / 96.0;
        if (_scale <= 0 || double.IsNaN(_scale)) _scale = 1;
        ResizeForDirection(placeDefault: true);

        // Windows.UI.Composition 要求当前线程上有「系统级」DispatcherQueue —— 这正是本类
        // 单独起一个线程的理由（主线程那个 Microsoft.UI.Dispatching 是手写 Win32 泵，不算数）。
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

        _timer = _queue!.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) =>
        {
            // 定时器回调抛异常 = 进程直接死（没有上层能接），所以这里必须兜住。
            try { Tick(); }
            catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.Tick", ex); }
        };
        _envRetry = _queue.CreateTimer();
        _envRetry.Interval = TimeSpan.FromMilliseconds(300);
        _envRetry.Tick += (_, _) =>
        {
            try { Initialize(); }
            catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.Retry", ex); }
        };
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
                _timer?.Stop();
                lock (WindowGate) Instances.Remove(_hwnd);
                _hwnd = IntPtr.Zero;
                _controller?.Close();
                _controller = null;
                // 事件订阅方（MainWindow）的状态都在主 UI 线程上，别在歌词线程里改。
                _owner.RunOnUi(() => ClosedByUser?.Invoke());
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
        _initializing = true;
        _envRetry?.Stop();
        try
        {
            // 必须在**本线程**上建自己的环境：跨线程复用主界面那个 CoreWebView2Environment
            // 会让 CLR 报 "Internal CLR error (0x80131506)" 直接杀进程（它不是敏捷对象）。
            // 参数与主界面完全一致 → WebView2 复用同一个浏览器进程，不会多开一个。
            if (_environment == null)
            {
                var options = new CoreWebView2EnvironmentOptions();
                (string? profile, string? browserArgs) = NativeMainWebWindow.WebViewEnvironmentSpec();
                if (!string.IsNullOrEmpty(browserArgs)) options.AdditionalBrowserArguments = browserArgs;
                _environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
            }
            if (_disposed) return;

            _controller = await _environment.CreateCoreWebView2CompositionControllerAsync(_hwnd);
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
                case "ready": _ready = true; StartupLog.Write("[桌面歌词] 页面就绪"); PublishLines(); ApplySettingsCore(_settings); _timer?.Start(); Tick(); break;
                case "drag": StartDrag(); break;
                case "drag-end": EndDrag(); break;
                case "close": Close(); break;
                case "lock": SetLocked(!_locked); break;
                case "unlock": SetLocked(false); break;
                case "play": _owner.RunOnUi(MiniPlayerBridge.TogglePlayPause); break;
                case "previous": _owner.RunOnUi(MiniPlayerBridge.Previous); break;
                case "next": _owner.RunOnUi(MiniPlayerBridge.Next); break;
                case "stylePatch":
                    DesktopLyricStyles.Apply(doc.RootElement.GetProperty("changes"));
                    ApplySettingsCore(AppSettingsStore.Load());
                    break;
                case "styleList": Post(new { kind = "styles", items = DesktopLyricStyles.List() }); break;
                case "styleSave":
                    DesktopLyricStyles.Save(doc.RootElement.GetProperty("name").GetString() ?? "我的样式");
                    Post(new { kind = "styles", items = DesktopLyricStyles.List() }); break;
                case "styleSelect":
                    DesktopLyricStyles.Select(doc.RootElement.GetProperty("id").GetString() ?? "");
                    ApplySettingsCore(AppSettingsStore.Load()); break;
                case "styleImport":
                    // Picker 必须回主 UI 线程开（歌词窗口这条线程不是 UI 线程），见 PickDesktopLyricStylePathAsync
                    string? importPath = await _owner.PickDesktopLyricStylePathAsync(save: false);
                    if (!string.IsNullOrEmpty(importPath)) { DesktopLyricStyles.Import(importPath!); Post(new { kind = "styles", items = DesktopLyricStyles.List() }); }
                    break;
                case "styleExport":
                    string? exportPath = await _owner.PickDesktopLyricStylePathAsync(save: true);
                    if (!string.IsNullOrEmpty(exportPath)) await File.WriteAllTextAsync(exportPath!, DesktopLyricStyles.Export());
                    break;
                case "resetPosition": ResizeForDirection(placeDefault: true); break;
                case "settings": _owner.RunOnUi(() => _owner.ShowWebPage("desktop-lyrics-settings")); break;
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
    // 全部编组到桌面歌词线程：窗口、合成树、WebView2 都绑在那个线程上，
    // 从主线程直接碰它们会撞 RPC_E_WRONG_THREAD（或者更隐蔽地画不出来）。

    public void Show() => OnLyricsThread(ShowCore);

    private void ShowCore()
    {
        if (_disposed) return;
        _wanted = true;
        ShowWindow(_hwnd, 8 /*SW_SHOWNA*/);
        StartupLog.Write("[桌面歌词] 已显示 pos=" + CurrentPosition + " dip=" + _widthDip + "x" + _heightDip);
        Tick();
    }

    public void Close() => OnLyricsThreadAndWait(CloseCore);

    private void CloseCore()
    {
        if (_disposed || _closing) return;
        _closing = true; _wanted = false;
        try { if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); }
        catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.Close", ex); _disposed = true; }
    }

    public void Dispose()
    {
        ClosedByUser = null;
        OnLyricsThreadAndWait(CloseCore);
    }

    /// <summary>恢复上次位置，并夹进当前屏幕可见范围（换显示器或改分辨率后不至于跑到屏幕外）。</summary>
    public void SetSavedPosition(int x, int y) => OnLyricsThread(() =>
    {
        int width = (int)Math.Round(_widthDip * _scale), height = (int)Math.Round(_heightDip * _scale);
        var area = WorkAreaFor(x, y);
        int maxX = Math.Max(area.Left, area.Right - width), maxY = Math.Max(area.Top, area.Bottom - height);
        SetWindowPos(_hwnd, IntPtr.Zero, Math.Clamp(x, area.Left, maxX), Math.Clamp(y, area.Top, maxY), width, height, SwpNoZOrder | SwpNoActivate);
    });

    public void SetLyrics(IReadOnlyList<LyricLine> lines) => OnLyricsThread(() => { _lines = lines; PublishLines(); });
    private void PublishLines() => Post(new { kind = "lyrics", lines = _lines.Select(l => new { t = l.Time.TotalSeconds, text = l.Text, romanization = l.Romanization, tr = l.IsTranslation, chars = l.CharTimes?.Select(t => t.TotalSeconds) }) });
    public void Sync(TimeSpan time) => OnLyricsThread(() => Post(new { kind = "clock", time = time.TotalSeconds, paused = _paused, rate = _owner.DesktopLyricsPlaybackRate }));
    public void SetPlaybackPaused(bool paused) => OnLyricsThread(() => { _paused = paused; Tick(); });

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

    public void SetLocked(bool locked) => OnLyricsThread(() =>
    {
        EndDrag();
        _locked = locked;
        if (!locked) _hintHot = false;
        Post(new { kind = "lock", locked });
        ApplyInput();
    });

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

    public void ApplySettings(AppSettingsState s) => OnLyricsThread(() => ApplySettingsCore(s));

    private void ApplySettingsCore(AppSettingsState s)
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

    // —————————————————— 专用线程 ——————————————————
    //
    // 为什么不能在主线程：Windows.UI.Composition 的 Compositor 要求**当前线程**上有系统级
    // DispatcherQueue（CoreMessaging）。本程序自 W13 起用 NativeDispatcherQueue.cs 里的手写
    // Win32 消息泵替换了 Windows App SDK 的 DispatcherQueue，主线程上并没有那个系统队列 ——
    // 于是 new Compositor() 直接抛 UnauthorizedAccessException「拒绝访问 / The caller must
    // initialize DispatcherQueue on this thread」。（日志证据：10-29 成功创建 32 次，
    // 10-31 换宿主后 13 次全失败。）
    //
    // 解法：整块搬到系统提供的「专用线程」上 —— CreateOnDedicatedThread 会建一条线程，
    // 线程上带真正的 DispatcherQueue 和它自己的消息循环。窗口、合成树、WebView2 全部归它，
    // 对外接口一律编组过去。
    //
    // ⚠ 走过的弯路（别再踩）：不要试图自己起线程再调 CoreMessaging 的
    // CreateDispatcherQueueForCurrentThread（DllImport 或显式 GetProcAddress 都一样）——
    // 同一份代码 Debug 正常、Release 必 0xC0000005 崩进程，连 Main 第一行调用都会崩。

    private static Windows.System.DispatcherQueue? s_queue;
    private static readonly object ThreadGate = new();
    private static Windows.System.DispatcherQueueController? s_controller;   // 必须留引用：控制器没了队列会被回收

    private static Windows.System.DispatcherQueue EnsureLyricsThread()
    {
        lock (ThreadGate)
        {
            if (s_queue != null) return s_queue;

            // 用系统自带的「专用线程」：这个 API 会建一条新线程，线程上有真正的
            // DispatcherQueue 和它自己的消息循环 —— 正好满足 Windows.UI.Composition 的要求，
            // 而不用去碰 CoreMessaging 的内部导出（那条路在 Release 下会 0xC0000005 崩进程）。
            s_controller = Windows.System.DispatcherQueueController.CreateOnDedicatedThread();
            Windows.System.DispatcherQueue queue = s_controller.DispatcherQueue;
            if (queue == null)
            {
                throw new InvalidOperationException("系统专用线程没有返回 DispatcherQueue");
            }

            s_queue = queue;
            // 在这条线程上装 SynchronizationContext：Initialize() 里的 await 才会回到它，
            // 否则延续扔到线程池，WebView2 就会被跨线程碰。
            queue.TryEnqueue(() => SynchronizationContext.SetSynchronizationContext(new QueueSynchronizationContext(queue)));
            StartupLog.Write("[桌面歌词] 已取得系统专用线程队列");
            return queue;
        }
    }

    /// <summary>Windows.System.DispatcherQueue 版的 SynchronizationContext（项目里的 Microsoft.UI.Dispatching 是垫片，别混用）。</summary>
    private sealed class QueueSynchronizationContext : SynchronizationContext
    {
        private readonly Windows.System.DispatcherQueue _queue;

        public QueueSynchronizationContext(Windows.System.DispatcherQueue queue) => _queue = queue;

        public override void Post(SendOrPostCallback d, object? state) => _queue.TryEnqueue(() =>
        {
            // 这里要是让异常冒到 CoreMessaging，整个进程会被干掉（async void 的未处理异常就走这条路）。
            try { d(state); }
            catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.SyncContext", ex); }
        });

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (_queue.HasThreadAccess) { d(state); return; }
            using var done = new ManualResetEventSlim(false);
            _queue.TryEnqueue(() => { try { d(state); } finally { done.Set(); } });
            done.Wait();
        }

        public override SynchronizationContext CreateCopy() => new QueueSynchronizationContext(_queue);
    }

    /// <summary>把动作投到桌面歌词线程（已经在那个线程上就直接执行）。</summary>
    private void OnLyricsThread(Action action)
    {
        Windows.System.DispatcherQueue? queue = _queue;
        if (queue == null || queue.HasThreadAccess)
        {
            try { action(); }
            catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.OnThread", ex); }
            return;
        }

        queue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.OnThread", ex); }
        });
    }

    /// <summary>同上但等它做完 —— 关闭窗口要用：调用方紧接着就认为窗口已经没了（否则进程会被它吊住）。</summary>
    private void OnLyricsThreadAndWait(Action action)
    {
        Windows.System.DispatcherQueue? queue = _queue;
        if (queue == null || queue.HasThreadAccess) { action(); return; }

        using var done = new ManualResetEventSlim(false);
        if (!queue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { StartupLog.WriteException("DesktopLyrics.OnThread", ex); }
            finally { done.Set(); }
        }))
        {
            return;
        }

        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            StartupLog.Write("[桌面歌词] 跨线程关闭超时（窗口可能还在）");
        }
    }

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
