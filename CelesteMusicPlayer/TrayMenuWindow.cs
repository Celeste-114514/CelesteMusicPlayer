using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 托盘菜单的承载窗口：一个 Win32 弹出窗 + WebView2 渲染网页菜单。
    ///
    /// 为什么不用 WinUI 窗口/MenuFlyout：菜单必须跟网页外壳同一套主题变量与排版，
    /// 只有让它自己就是一个网页才做得到（MenuFlyout 跑在独立 XAML 树里，主题继承不过去）。
    ///
    /// 生命周期：窗口和 WebView 只建一次，之后反复 Show/Hide；关闭菜单不销毁 WebView，
    /// 这样第二次右键是即开的，不会有"先白一下再出现"的过程。
    /// </summary>
    internal sealed class TrayMenuWindow : IDisposable
    {
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;

        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SW_HIDE = 0;
        private const int GWL_EXSTYLE = -20;

        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_KEYDOWN = 0x0100;
        private const int WA_INACTIVE = 0;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private readonly string _className;
        private readonly WndProcDelegate _wndProc;
        private IntPtr _hwnd;
        private bool _disposed;
        private bool _visible;
        private bool _initFailed;
        private bool _ready;
        private bool _showPending;

        private CoreWebView2Environment? _environment;
        private CoreWebView2Controller? _controller;
        private Task<bool>? _initTask;
        private TaskCompletionSource<bool>? _sizeWaiter;

        /// <summary>网页回报的菜单内容尺寸（DIP）。</summary>
        private double _contentWidth = 260, _contentHeight = 320;

        /// <summary>点了某一项。actionId 已由宿主侧校验过白名单。</summary>
        internal event Action<string>? Invoked;

        internal bool IsReady => _ready && _controller != null;

        /// <summary>菜单此刻是否显示着（右键再点一次要能收起）。</summary>
        internal bool IsVisible => _visible;

        /// <summary>预热：只把 WebView 建好并加载页面，不显示。让第一次右键不再等初始化。</summary>
        internal Task<bool> PrewarmAsync() => EnsureWebViewAsync();

        internal TrayMenuWindow()
        {
            _wndProc = WindowProc;
            _className = "CelesteTrayMenu_" + Guid.NewGuid().ToString("N");
            CreateWindow();
        }

        /// <summary>把菜单弹到光标/托盘图标附近。失败返回 false，由调用方退到系统原生菜单。</summary>
        internal async Task<bool> ShowAsync(IReadOnlyList<TrayMenuModel.Item> items, Dictionary<string, string> themeVars, string mode)
        {
            if (_disposed || _showPending) return false;
            _showPending = true;
            try
            {
            if (!await EnsureWebViewAsync()) return false;

            try
            {
                // WebView2 的窗口版不支持透明背景，所以先把底刷成菜单底色：
                // 这样页面首帧前不会闪一下白，圆角外面也不会露出白色。
                if (_controller != null && themeVars.TryGetValue("menu-bg", out string? menuBg)
                    && ColorHelper.ParseHexColor(menuBg) is { } color)
                {
                    _controller.DefaultBackgroundColor = color;
                }

                _sizeWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                PostToWeb(new
                {
                    kind = "celeste.trayTheme",
                    version = 1,
                    theme = new { mode, vars = themeVars },
                    items
                });

                // 等页面量完高度再定位。等不到就用上一次的尺寸，别让菜单迟迟不出现。
                Task completed = await Task.WhenAny(_sizeWaiter.Task, Task.Delay(500));
                _sizeWaiter = null;
                if (_disposed) return false;
                PlaceAndShow();
                return true;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TrayMenuWindow.ShowAsync", ex);
                return false;
            }
            }
            finally { _showPending = false; }
        }

        internal void Hide()
        {
            if (_hwnd == IntPtr.Zero || !_visible) return;
            _visible = false;
            ShowWindow(_hwnd, SW_HIDE);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_controller != null)
                {
                    _controller.Close();
                }
            }
            catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.Dispose.WebView", ex); }
            _controller = null;
            _environment = null;
            try
            {
                if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            }
            catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.Dispose.Window", ex); }
            _hwnd = IntPtr.Zero;
        }

        // —— 窗口 ——

        private void CreateWindow()
        {
            IntPtr instance = GetModuleHandle(null);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = _className
            };
            RegisterClassExW(ref wc);

            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, _className, "CelesteTrayMenu",
                WS_POPUP, 0, 0, 320, 400, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                StartupLog.Write("[托盘菜单] 创建窗口失败（err=" + Marshal.GetLastWin32Error() + "）");
                return;
            }

            // Win11：让 DWM 把弹出窗的四个角裁圆。Win10 上这条属性会被忽略（方角），不影响功能。
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
            }
            catch { }
        }

        private void PlaceAndShow()
        {
            if (_hwnd == IntPtr.Zero) return;
            double scale = GetDpiForWindow(_hwnd) / 96.0;
            if (scale <= 0) scale = 1;

            int width = (int)Math.Round(Math.Clamp(_contentWidth, 200, 360) * scale);
            int height = (int)Math.Round(Math.Clamp(_contentHeight, 60, 1200) * scale);

            RECT work = WorkArea();
            int anchorX, anchorTop, anchorBottom;
            if (TrayIconRect(out WindowsTrayIcon.RECT icon))
            {
                anchorX = icon.Left + icon.Width / 2;
                anchorTop = icon.Top;
                anchorBottom = icon.Bottom;
            }
            else
            {
                GetCursorPos(out POINT cursor);
                anchorX = cursor.X;
                anchorTop = cursor.Y;
                anchorBottom = cursor.Y;
            }

            int x = anchorX - width / 2;
            int y = anchorTop - height - 8;                       // 优先贴图标上方（任务栏在底部时）
            if (y < work.Top + 4) y = anchorBottom + 8;           // 上方放不下就翻到下面（任务栏在顶部时）
            x = Math.Clamp(x, work.Left + 4, Math.Max(work.Left + 4, work.Right - width - 4));
            y = Math.Clamp(y, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - height - 4));

            if (_controller != null)
            {
                _controller.Bounds = new Rect(0, 0, width, height);
                _controller.IsVisible = true;
            }

            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
            _visible = true;
            ForceForeground();
        }

        /// <summary>给定位用的托盘图标矩形（由 WindowsTrayService 注入，它持有通知图标）。</summary>
        internal Func<WindowsTrayIcon.RECT?>? IconRectProvider { get; set; }

        private bool TrayIconRect(out WindowsTrayIcon.RECT rect)
        {
            rect = default;
            try
            {
                if (IconRectProvider?.Invoke() is { } value) { rect = value; return true; }
            }
            catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.TrayIconRect", ex); }
            return false;
        }

        /// <summary>没抢到前台时菜单会收不到 Esc / 外部点击，用附加线程输入硬抢一次。</summary>
        private void ForceForeground()
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == _hwnd) return;
                uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
                uint current = GetCurrentThreadId();
                bool attached = foregroundThread != 0 && foregroundThread != current
                    && AttachThreadInput(foregroundThread, current, true);
                BringWindowToTop(_hwnd);
                SetForegroundWindow(_hwnd);
                if (attached) AttachThreadInput(foregroundThread, current, false);
            }
            catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.ForceForeground", ex); }
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_ACTIVATE:
                    // 点到菜单外面就收起。子窗口（WebView2）在菜单内部，不会触发失活。
                    if (((int)(wParam.ToInt64() & 0xFFFF)) == WA_INACTIVE)
                    {
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
                        {
                            if (_disposed) return;
                            IntPtr foreground = GetForegroundWindow();
                            if (foreground != _hwnd && !IsChild(_hwnd, foreground)) Hide();
                        });
                    }
                    break;
                case WM_KEYDOWN:
                    if ((int)wParam == 0x1B /* VK_ESCAPE */) { Hide(); return IntPtr.Zero; }
                    break;
                case WM_CLOSE:
                    Hide();
                    return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // —— WebView2 ——

        private async Task<bool> EnsureWebViewAsync()
        {
            if (_ready && _controller != null) return true;
            if (_initFailed) return false;
            // 预热还在跑的时候右键（启动后头几秒）会进到同一个方法里，
            // 两次并发建控制器就会在同一个 HWND 上打起来 —— 只允许有一次初始化，后来者等它。
            _initTask ??= EnsureWebViewCoreAsync();
            return await _initTask;
        }

        private async Task<bool> EnsureWebViewCoreAsync()
        {
            if (_hwnd == IntPtr.Zero) { _initFailed = true; return false; }

            try
            {
                string folder = Path.Combine(AppContext.BaseDirectory, "WebShell");
                if (!File.Exists(Path.Combine(folder, "tray-menu.html")))
                {
                    StartupLog.Write("[托盘菜单] 找不到 WebShell/tray-menu.html，退到系统原生菜单");
                    _initFailed = true;
                    return false;
                }

                // 独立 user data folder：不跟主界面抢同一个浏览器进程，
                // 菜单预热/销毁都动不到曲库那边（代价是多一个进程，几百 MB 的页面不存在，很轻）。
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer", "TrayWebView2");
                Directory.CreateDirectory(userData);
                _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, null);
                var windowRef = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_hwnd.ToInt64());
                _controller = await _environment.CreateCoreWebView2ControllerAsync(windowRef);
                if (_disposed) { _controller.Close(); _controller = null; return false; }

                CoreWebView2 core = _controller.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.SetVirtualHostNameToFolderMapping("celeste.tray", folder, CoreWebView2HostResourceAccessKind.DenyCors);
                core.WebMessageReceived += (_, args) => OnWebMessage(args);

                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, args) => ready.TrySetResult(args.IsSuccess);
                core.Navigate("https://celeste.tray/tray-menu.html");
                Task navigation = await Task.WhenAny(ready.Task, Task.Delay(4000));
                if (_disposed || navigation != ready.Task || !await ready.Task)
                    throw new InvalidOperationException("托盘菜单页面未成功加载");

                _controller.Bounds = new Rect(0, 0, 260, 320);
                _controller.IsVisible = false;
                _ready = true;
                StartupLog.Write("[托盘菜单] WebView2 菜单已就绪");
                return true;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TrayMenuWindow.EnsureWebView", ex);
                _initFailed = true;
                return false;
            }
        }

        private void PostToWeb(object payload)
        {
            try
            {
                _controller?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            }
            catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.PostToWeb", ex); }
        }

        private void OnWebMessage(CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(args.WebMessageAsJson);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != "celeste.command") return;
                if (!root.TryGetProperty("action", out JsonElement action)) return;
                string? verb = action.GetString();

                if (verb == "size")
                {
                    bool changed = false;
                    if (root.TryGetProperty("width", out JsonElement w) && w.TryGetDouble(out double width)
                        && Math.Abs(width - _contentWidth) > 0.5) { _contentWidth = width; changed = true; }
                    if (root.TryGetProperty("height", out JsonElement h) && h.TryGetDouble(out double height)
                        && Math.Abs(height - _contentHeight) > 0.5) { _contentHeight = height; changed = true; }
                    _sizeWaiter?.TrySetResult(true);
                    // 显示期间内容变了（勾选态/字号缩放）→ 跟着改成新尺寸，别让菜单截断
                    if (changed && _visible) PlaceAndShow();
                    return;
                }
                if (verb == "close") { Hide(); return; }
                if (verb == "blur") return; // WM_ACTIVATE owns native focus dismissal.
                if (verb == "invoke")
                {
                    string? actionId = root.TryGetProperty("actionId", out JsonElement id) ? id.GetString() : null;
                    if (string.IsNullOrEmpty(actionId)) return;
                    // 只在白名单里认 —— 网页传来的任何其它字符串一律丢弃
                    if (!TrayMenuModel.IsKnownAction(actionId)) return;
                    StartupLog.Write("[托盘菜单] 收到命令 " + actionId);
                    Hide();
                    Invoked?.Invoke(actionId!);
                }
            }
            catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.OnWebMessage", ex); }
        }

        // —— P/Invoke ——

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr parent, IntPtr child);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private static RECT WorkArea()
        {
            try
            {
                GetCursorPos(out POINT pt);
                IntPtr monitor = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(monitor, ref info)) return info.rcWork;
                return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            }
            catch
            {
                return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
