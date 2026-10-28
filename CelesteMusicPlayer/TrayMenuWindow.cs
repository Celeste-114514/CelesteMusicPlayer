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
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const int WA_INACTIVE = 0;
        private const int VK_LBUTTON = 0x01;

        /// <summary>导航最多等多久。冷启动 WebView2（首帧建浏览器进程）在机器忙时 4 秒根本不够。</summary>
        private const int NavigationTimeoutMs = 20000;

        /// <summary>连续失败这么多次才彻底放弃（在此之前每次右键都会重新试）。</summary>
        private const int MaxInitFailures = 3;

        /// <summary>刚弹出后这段时间内不判"点外面"，避开右键余波与首帧布局。</summary>
        private const int OutsideGraceMs = 250;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private readonly string _className;
        private readonly WndProcDelegate _wndProc;
        private IntPtr _hwnd;
        private bool _disposed;
        private bool _visible;
        private bool _initFailed;
        private int _initFailures;
        private bool _ready;
        private bool _showPending;

        /// <summary>点外面收起的观察器。不再依赖 WM_ACTIVATE —— 那条路会在点击瞬间把菜单收掉。</summary>
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _outsideTimer;
        private int _outsideHits;
        private long _shownAt;

        /// <summary>网页是否已经报过"点击进到 DOM"，只记第一次，避免刷日志。</summary>
        private bool _clickProbeLogged;

        /// <summary>收到无法识别的消息只记一次，避免刷屏。</summary>
        private bool _unknownMessageLogged;

        private CoreWebView2Environment? _environment;
        private CoreWebView2Controller? _controller;
        private Task<bool>? _initTask;

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

                string payload = JsonSerializer.Serialize(new
                {
                    kind = "celeste.trayTheme",
                    version = 1,
                    theme = new { mode, vars = themeVars },
                    items
                });
                // Render and measure in one operation; no stale theme-only size
                // or fixed 320px fallback can win the asynchronous message race.
                string measured = await _controller!.CoreWebView2.ExecuteScriptAsync("window.CelesteTray.render(" + payload + ")");
                using (JsonDocument size = JsonDocument.Parse(measured))
                {
                    _contentWidth = size.RootElement.GetProperty("width").GetDouble();
                    _contentHeight = size.RootElement.GetProperty("height").GetDouble();
                }
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
            StopOutsideWatcher();
            if (_hwnd == IntPtr.Zero || !_visible) return;
            _visible = false;
            ShowWindow(_hwnd, SW_HIDE);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopOutsideWatcher();
            _outsideTimer = null;
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

            // Resize the parent before sizing its child WebView. Otherwise the
            // controller can retain the old 260x320 client viewport.
            // 这里**故意**带激活（不再用 SWP_NOACTIVATE）：未激活窗口收到的第一次点击
            // 会被 Windows 拿去"激活窗口"而不是送给网页，表现就是"点了全都没反应"。
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW);
            if (_controller != null)
            {
                _controller.Bounds = new Rect(0, 0, width, height);
                _controller.IsVisible = true;
            }

            _visible = true;
            _shownAt = Environment.TickCount64;
            StartupLog.Write($"[托盘菜单] 布局 content={_contentWidth}x{_contentHeight} scale={scale:F2} window={width}x{height}");
            ForceForeground();
            bool active = SetForegroundWindow(_hwnd);
            SetFocus(_hwnd);
            _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            StartupLog.Write("[托盘菜单] 激活 foreground=" + (GetForegroundWindow() == _hwnd) + " setfg=" + active);

            // 再下一轮消息里补设一次尺寸：窗口刚显示时 WebView 子窗口可能还没跟上父窗口的
            // 物理尺寸，补这一次可以让命中区域和画面严格对齐。
            Microsoft.UI.Dispatching.DispatcherQueue? queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            queue?.TryEnqueue(() =>
            {
                if (_disposed || !_visible || _controller == null) return;
                try { _controller.Bounds = new Rect(0, 0, width, height); }
                catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.PlaceAndShow.Resync", ex); }
            });

            StartOutsideWatcher();
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

        /// <summary>
        /// 点菜单外面就收起。用轮询而不是 WM_ACTIVATE：点击穿过 WebView 子窗口时，
        /// 父窗口可能先收到一条 WA_INACTIVE，照它隐藏就会把"点的这一下"连同菜单一起吞掉。
        /// 这里只认一个事实：光标真的在菜单矩形外面、而且没有按键按着，连续两次就收起。
        /// </summary>
        private void StartOutsideWatcher()
        {
            try
            {
                if (_outsideTimer == null)
                {
                    Microsoft.UI.Dispatching.DispatcherQueue? queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                    if (queue == null) return;
                    _outsideTimer = queue.CreateTimer();
                    _outsideTimer.Interval = TimeSpan.FromMilliseconds(120);
                    _outsideTimer.IsRepeating = true;
                    _outsideTimer.Tick += (_, _) => CheckOutside();
                }
                _outsideHits = 0;
                _outsideTimer.Start();
            }
            catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.StartOutsideWatcher", ex); }
        }

        private void StopOutsideWatcher()
        {
            try { _outsideTimer?.Stop(); } catch { }
            _outsideHits = 0;
        }

        private void CheckOutside()
        {
            if (_disposed || !_visible || _hwnd == IntPtr.Zero) { StopOutsideWatcher(); return; }
            if (Environment.TickCount64 - _shownAt < OutsideGraceMs) return;
            try
            {
                GetCursorPos(out POINT cursor);
                bool inside = GetWindowRect(_hwnd, out RECT bounds)
                    && cursor.X >= bounds.Left && cursor.X < bounds.Right
                    && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
                // 按着键不收起：按下-拖动-松开这一串可能跨出边界，中途收菜单会打断操作。
                bool pressing = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
                             || (GetAsyncKeyState(0x02 /* VK_RBUTTON */) & 0x8000) != 0;
                if (inside || pressing) { _outsideHits = 0; return; }
                if (++_outsideHits >= 2) { Hide(); }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TrayMenuWindow.CheckOutside", ex);
                StopOutsideWatcher();
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    // Activate without discarding the click that opened/focused
                    // the native WebView child (MA_ACTIVATE, not ACTIVATEANDEAT).
                    return new IntPtr(1);
                case WM_ACTIVATE:
                    // 这里**不再**收起菜单。原先"一失活就隐藏"会在点击穿过 WebView 子窗口的
                    // 那一瞬间把菜单收掉，网页里的 pointerdown 还没发出去窗口就没了 ——
                    // 表现就是"点了完全没反应"。收起改由 StartOutsideWatcher 轮询光标位置。
                    if (_visible)
                        StartupLog.Write("[托盘菜单] WM_ACTIVATE " + (((int)(wParam.ToInt64() & 0xFFFF)) == WA_INACTIVE ? "INACTIVE(忽略)" : "ACTIVE"));
                    break;
                case WM_LBUTTONDOWN:
                    // 正常情况下鼠标落在 WebView 子窗口上，父窗口收不到这条。
                    // 收到 = 点在窗口里但没进网页（就是命中区域不对的证据）。
                    if (_visible) StartupLog.Write("[托盘菜单] WM_LBUTTONDOWN 落在父窗口（未进入网页）");
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
            if (_initTask != null) return await _initTask;

            _initTask = EnsureWebViewCoreAsync();
            bool ok;
            try { ok = await _initTask; }
            catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.EnsureWebViewAsync", ex); ok = false; }
            finally { _initTask = null; }   // 失败后允许下次右键再试，不再一次失败就整场报废

            if (!ok)
            {
                _initFailures++;
                // 失败原因多半是上一次运行留下的浏览器进程还占着同一个 user data folder，
                // 或者机器太忙导航超时 —— 都不是永久性的，所以只数次数，不立刻判死。
                if (_initFailures >= MaxInitFailures)
                {
                    _initFailed = true;
                    StartupLog.Write("[托盘菜单] 连续 " + _initFailures + " 次初始化失败，本次运行改用系统原生菜单");
                }
                else
                {
                    StartupLog.Write("[托盘菜单] 初始化失败（第 " + _initFailures + " 次），下次右键会重试");
                }
            }
            return ok;
        }

        private async Task<bool> EnsureWebViewCoreAsync()
        {
            if (_hwnd == IntPtr.Zero) { _initFailures = MaxInitFailures; _initFailed = true; return false; }

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
                if (_environment == null)
                {
                    try
                    {
                        _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userData, null);
                    }
                    catch (Exception ex)
                    {
                        // 同一个 user data folder 被另一个实例（双开/上一次没退干净）占着时会失败。
                        // 换一个备用目录再来一次，菜单本身照常能用。
                        StartupLog.WriteException("TrayMenuWindow.CreateEnvironment(primary)", ex);
                        string alt = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "CelesteMusicPlayer", "TrayWebView2-alt");
                        Directory.CreateDirectory(alt);
                        _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, alt, null);
                        StartupLog.Write("[托盘菜单] 主数据目录被占用，已改用备用目录");
                    }
                }
                var windowRef = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_hwnd.ToInt64());
                _controller = await _environment.CreateCoreWebView2ControllerAsync(windowRef);
                if (_disposed) { _controller.Close(); _controller = null; return false; }
                _controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
                _controller.ZoomFactor = 1;

                CoreWebView2 core = _controller.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsWebMessageEnabled = true;
                core.SetVirtualHostNameToFolderMapping("celeste.tray", folder, CoreWebView2HostResourceAccessKind.DenyCors);
                core.WebMessageReceived += (_, args) => OnWebMessage(args);

                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess)
                        StartupLog.Write("[托盘菜单] 导航失败 WebErrorStatus=" + args.WebErrorStatus);
                    ready.TrySetResult(args.IsSuccess);
                };
                core.Navigate("https://celeste.tray/tray-menu.html");
                Task finished = await Task.WhenAny(ready.Task, Task.Delay(NavigationTimeoutMs));
                if (_disposed) return false;
                if (finished != ready.Task)
                    throw new TimeoutException("托盘菜单页面 " + (NavigationTimeoutMs / 1000) + " 秒内没加载完");
                if (!await ready.Task)
                    throw new InvalidOperationException("托盘菜单页面加载失败（WebErrorStatus 见上一条日志）");

                if (_controller != null)
                {
                    _controller.Bounds = new Rect(0, 0, 260, 320);
                    // 一直保持"可见"：靠父窗口的显示/隐藏来控制看不看得见。
                    // 原先在显示时把 IsVisible 从 false 切到 true，WebView 会重建渲染目标，
                    // 那一瞬间命中区域还停在旧尺寸上 —— 点菜单项落空就是这么来的。
                    _controller.IsVisible = true;
                }
                _ready = true;
                _initFailures = 0;
                StartupLog.Write("[托盘菜单] WebView2 菜单已就绪");
                return true;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("TrayMenuWindow.EnsureWebView", ex);
                try { _controller?.Close(); } catch { }
                _controller = null;
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
                if (!root.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != "celeste.command")
                {
                    if (!_unknownMessageLogged)
                    {
                        _unknownMessageLogged = true;
                        StartupLog.Write("[托盘菜单] 收到无法识别的消息: " + Truncate(args.WebMessageAsJson, 160));
                    }
                    return;
                }
                if (!root.TryGetProperty("action", out JsonElement action)) return;
                string? verb = action.GetString();
                if (verb == "ready") { StartupLog.Write("[托盘菜单] 网页消息通道已连接"); return; }

                if (verb == "probe")
                {
                    // 网页在真正下发 invoke 之前先报一声"这一下点进 DOM 了"。
                    // 有了它就能分清：点根本没进网页（没有这条），还是进了但命令丢了（有 probe 无 收到命令）。
                    if (!_clickProbeLogged)
                    {
                        _clickProbeLogged = true;
                        StartupLog.Write("[托盘菜单] 网页已收到点击(pointerdown)");
                    }
                    return;
                }

                if (verb == "size")
                {
                    // The awaited render result is authoritative. Delayed size
                    // messages from an earlier render must not overwrite it.
                    return;
                }
                if (verb == "close") { Hide(); return; }
                if (verb == "blur") return; // 收起由 StartOutsideWatcher 负责，DOM 失焦不算数。
                if (verb == "invoke")
                {
                    string? actionId = root.TryGetProperty("actionId", out JsonElement id) ? id.GetString() : null;
                    if (string.IsNullOrEmpty(actionId)) return;
                    // 只在白名单里认 —— 网页传来的任何其它字符串一律丢弃
                    if (!TrayMenuModel.IsKnownAction(actionId))
                    {
                        StartupLog.Write("[托盘菜单] 拒绝未知动作 " + actionId);
                        return;
                    }
                    StartupLog.Write("[托盘菜单] 收到命令 " + actionId);
                    Hide();
                    Invoked?.Invoke(actionId!);
                }
            }
            catch (Exception ex) { StartupLog.WriteException("TrayMenuWindow.OnWebMessage", ex); }
        }

        private static string Truncate(string? text, int max)
            => string.IsNullOrEmpty(text) ? "" : (text!.Length <= max ? text : text.Substring(0, max));

        // —— P/Invoke ——

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr parent, IntPtr child);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

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
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

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
