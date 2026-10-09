// 常驻 WebView2 宿主（第 1 步 · 基础设施）
//
// 为什么必须"常驻单实例 + 前端路由"（实测依据 outputs/WebView2第0步验证结果-2026-10-02.html）：
//   第一个 WebView2 实例 = 286MB 内存（浏览器主进程 + GPU 进程 + 渲染进程），一次性固定成本
//   第二个实例           =  62MB、初始化仅 26.7ms
//   一页一个 → 286 + 62×N ≈ 1GB+，十几页就是灾难
//   常驻单实例 → 286MB 付一次，页面切换只是网页内部路由
//
// 本文件当前只做「宿主 + 通信 + 主题注入」，不含任何具体页面。
// 极客皮肤注意：绝不在这里改 Application.Resources（会 0xc000027b 秒崩），
// 配色只经由 CSS 变量注入网页，或写进页面自己的字典。

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace CelesteMusicPlayer
{
    /// <summary>网页发过来的消息。kind 决定后续动作。</summary>
    public sealed class WebInboundMessage
    {
        public string Kind { get; set; } = "";
        public JsonElement Payload { get; set; }
    }

    /// <summary>
    /// 常驻 WebView2 宿主。挂在主窗口里，全生命周期只有一个实例。
    /// </summary>
    public sealed partial class MainWindow
    {
        private WebView2? _celesteWeb;
        private bool _celesteWebReady;
        private bool _celesteWebInitializing;
        private string? _celesteWebCurrentRoute;
        private string? _celesteWebPendingRoute;

        /// <summary>网页上行消息计数，只为了日志里能编号（↓#1、↓#2……）。</summary>
        private int _celesteWebMsgSeq;

        /// <summary>试点层右上角的原生返回钮（见 PrewarmCelesteWeb 里创建处）。</summary>
        private Microsoft.UI.Xaml.Controls.Button? _celesteWebBackButton;

        /// <summary>网页请求的路由（消息处理里转发）。</summary>
        private WebView2? CelesteWeb => _celesteWeb;

        /// <summary>宿主是否已就绪，可以往里发消息。</summary>
        public bool IsCelesteWebReady => _celesteWebReady;

        /// <summary>
        /// 启动阶段预热。建一个不可见的 WebView2，把用户数据目录和虚拟主机映射准备好。
        /// 实测 226~414ms，放这里用户感知不到（程序本来就要启动）。
        /// 失败不弹窗、不阻塞启动——纯降级：详情页继续走原生版。
        /// </summary>
        public async Task PrewarmCelesteWeb()
        {
            if (_celesteWeb != null || _celesteWebInitializing) return;
            _celesteWebInitializing = true;
            // 声明在 try 外：catch 里拆半成品实例时要用（声明在 try 内会 CS0103）
            WebView2? web = null;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                web = new WebView2 { Visibility = Visibility.Collapsed };
                // 挂进视觉树才初始化；宿主容器是零尺寸的，不影响任何布局
                if (CelesteWebHostGrid == null)
                {
                    // 没有宿主容器（XAML 未加 WebHostGrid）→ 静默跳过，不算错误
                    _celesteWebInitializing = false;
                    return;
                }
                CelesteWebHostGrid.Children.Add(web);

                // 试点层的原生返回钮，浮在网页右上角（网页自己的返回箭头在左上角，不打架）。
                // WinUI3 岛屿架构下网页的输入链路比原生控件长，万一出问题，这是保证用户
                // 随时能退回原生界面的兜底；代码里后 Add 的子弟在上层，不会网页挡掉。
                var backBtn = new Microsoft.UI.Xaml.Controls.Button
                {
                    Content = "‹ 返回原界面",
                    Visibility = Visibility.Collapsed,
                    Margin = new Thickness(0, 12, 12, 0),
                    Padding = new Thickness(12, 4, 12, 4),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    FontSize = 12,
                    CornerRadius = new CornerRadius(15),
                };
                backBtn.Click += (s, e) => CloseWebAlbumPilot();
                CelesteWebHostGrid.Children.Add(backBtn);
                _celesteWebBackButton = backBtn;

                // ⚠ 别在这里钉 Width/Height=0：第 3 步起这层是要真的显示页面的，
                //   钉死尺寸就永远看不见了。平时靠 CelesteWebHostGrid 的 Collapsed 藏住。

                // ⚠ WinUI3 走的是 WinRT 投影（Microsoft.Web.WebView2.Core.Projection），
                //   API 表面与 net462/WinForms 完全不同：
                //     CoreWebView2Environment.CreateAsync()  【只有无参重载】
                //     WebView2.EnsureCoreWebView2Async()      【只有无参重载】
                //   net462 版的 CreateAsync(browserExecutableFolder, userDataFolder, options)
                //   在这里【不存在】，写了会报 CS1501/CS1739。
                //   投影下用户数据目录走默认位置（应用 LocalAppData 下由 WebView2 自己管），
                //   需要覆盖时得走 CoreWebView2EnvironmentOptions + 页面级 EnsureCoreWebView2Async(env)。
                //   （真实签名由 tools/Wv2ApiDump 反射列出，别再猜。）
                await web.EnsureCoreWebView2Async();
                sw.Stop();

                var cv = web.CoreWebView2;
                if (cv == null)
                {
                    StartupLog.Write("[WebView2] CoreWebView2 为 null，跳过 Web 前端（详情页走原生）");
                    CleanupCelesteWeb(web);
                    _celesteWebInitializing = false;
                    return;
                }

                // ---- 消息通道：网页 → C# ----
                // ⚠ 本投影（WinRT/CsWinRT）里 CoreWebView2WebMessageReceivedEventArgs
                //   只有 TryGetWebMessageAsString() 方法和 WebMessageAsJson 属性，
                //   **没有 WebMessageAsString 属性**（Wv2ApiDump 反射坐实）。
                //   TryGetWebMessageAsString() 读空时用 WebMessageAsJson 兜底
                //   （字符串消息的 JSON 形态，多一层引号转义，解出来就是原文）。
                cv.WebMessageReceived += CelesteWebOnMessageReceived;

                // ---- 故障可见性：浏览器进程死没死、导航成没成，日志里必须留痕 ----
                // "关掉试点后再打不开"的成因推定：浏览器进程崩溃后 CoreWebView2
                // 会变成 null，而 ProcessFailed 一定先于它触发。用户手测后看这两行
                // 就能分清是页面没渲染、宿主坏了、还是消息没到。
                cv.NavigationCompleted += OnCelesteWebNavigationCompleted;
                cv.ProcessFailed += OnCelesteWebProcessFailed;

                // ---- 本地资源映射：封面等文件走虚拟域名，不用 file:// ----
                // file:// 会有跨域和权限一堆坑；映射成 http://celeste.local/ 后同源策略正常。
                var assetRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CelesteMusicPlayer", "WebAssets");
                Directory.CreateDirectory(assetRoot);
                cv.SetVirtualHostNameToFolderMapping(
                    "celeste.local", assetRoot,
                    CoreWebView2HostResourceAccessKind.Allow);

                // ---- 主题变量：即便还没有真实页面，也先注入一份，证明通道通 ----
                await cv.AddScriptToExecuteOnDocumentCreatedAsync(
                    "window.__celesteApplyTheme = function(css){" +
                    "  var s = document.getElementById('__celeste_theme');" +
                    "  if (!s) { s = document.createElement('style');" +
                    "    s.id = '__celeste_theme';" +
                    "    if (document.head) document.head.appendChild(s); }" +
                    "  s.textContent = css; return true; };");

                cv.Settings.AreDefaultContextMenusEnabled = false;   // 右键一律走原生菜单
                cv.Settings.AreDevToolsEnabled = false;              // 正式版不开 DevTools
                cv.Settings.IsStatusBarEnabled = false;
                cv.Settings.IsZoomControlEnabled = true;             // 高刷屏用户可能想放大

                _celesteWeb = web;
                _celesteWebReady = true;

                StartupLog.Write($"[WebView2] 预热完成 {sw.Elapsed.TotalMilliseconds:F0}ms  版本={cv.Environment.BrowserVersionString}");

                // 之前有页面在排队等就绪，现在补上
                if (_celesteWebPendingRoute != null)
                {
                    var r = _celesteWebPendingRoute;
                    _celesteWebPendingRoute = null;
                    await NavigateCelesteWebAsync(r);
                }
            }
            catch (Exception ex)
            {
                // 纯降级：Web 前端不可用时，详情页继续走原生版，程序照常用
                StartupLog.WriteException("PrewarmCelesteWeb", ex);
                // 用局部 web 而不是字段：异常发生在中途时字段还是 null，
                // 但局部这个实例已经挂进视觉树了，必须拆掉，不然每次都漏一个
                CleanupCelesteWeb(web);
            }
            finally
            {
                _celesteWebInitializing = false;
            }
        }

        private void CleanupCelesteWeb(WebView2? web)
        {
            if (web?.CoreWebView2 != null)
            {
                web.CoreWebView2.WebMessageReceived -= CelesteWebOnMessageReceived;
                web.CoreWebView2.NavigationCompleted -= OnCelesteWebNavigationCompleted;
                web.CoreWebView2.ProcessFailed -= OnCelesteWebProcessFailed;
            }
            if (CelesteWebHostGrid != null && web != null) CelesteWebHostGrid.Children.Remove(web);
            if (CelesteWebHostGrid != null && _celesteWebBackButton != null)
            {
                CelesteWebHostGrid.Children.Remove(_celesteWebBackButton);
                _celesteWebBackButton = null;
            }
            web?.Close();
            _celesteWeb = null;
            _celesteWebReady = false;
        }

        /// <summary>每次导航的成败都记一行：http=0 + success=false = 页面根本没加载出来。</summary>
        private void OnCelesteWebNavigationCompleted(
            Microsoft.Web.WebView2.Core.CoreWebView2 sender,
            Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            StartupLog.Write(
                $"[WebView2] 导航完成 success={e.IsSuccess} http={e.HttpStatusCode} err={e.WebErrorStatus}");
        }

        /// <summary>
        /// 浏览器进程崩溃/无响应。ProcessFailedKind=BrowserProcessExited 之后
        /// CoreWebView2 会变 null——"关掉后再打不开"就是这条路径，重建即可恢复。
        /// </summary>
        private void OnCelesteWebProcessFailed(
            Microsoft.Web.WebView2.Core.CoreWebView2 sender,
            Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedEventArgs e)
        {
            StartupLog.Write(
                $"[WebView2] 进程失败 kind={e.ProcessFailedKind} reason={e.Reason} exit={e.ExitCode} desc={e.ProcessDescription}");
        }

        /// <summary>
        /// 切到某个前端路由（如 "album"）。宿主没就绪就先记下，就绪后自动补。
        /// </summary>
        public async Task NavigateCelesteWebAsync(string route)
        {
            if (!_celesteWebReady || _celesteWeb?.CoreWebView2 == null)
            {
                _celesteWebPendingRoute = route;
                return;
            }
            _celesteWebCurrentRoute = route;
            // 前端路由由网页自己处理，C# 这边只发一个消息让它切
            await PostCelesteWebAsync(new { kind = "route", route });
        }

        /// <summary>
        /// C# → 网页。必须传字符串 JSON：PostWebMessageAsJson 发对象时
        /// 网页侧 echo 回来 C# 读不出（见文件头注释与探针实测）。
        /// </summary>
        public Task PostCelesteWebAsync(object payload)
        {
            var cv = _celesteWeb?.CoreWebView2;
            if (cv == null) return Task.CompletedTask;
            try
            {
                var json = JsonSerializer.Serialize(payload);
                cv.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("PostCelesteWebAsync", ex);
            }
            return Task.CompletedTask;
        }

        /// <summary>把当前主题注入网页。实测 0.5ms 内生效，主题热切换靠它。</summary>
        public async Task ApplyCelesteThemeToWebAsync(CelesteThemeFile theme, bool dark)
        {
            var cv = _celesteWeb?.CoreWebView2;
            if (cv == null) return;
            try
            {
                var css = CelesteThemeStore.BuildCssVariables(theme, dark);
                await cv.ExecuteScriptAsync(
                    "window.__celesteApplyTheme && window.__celesteApplyTheme(" +
                    JsonSerializer.Serialize(css) + ");");

                // 深浅色也要告诉网页：CSS 变量只换了颜色值，深色下还有一套独立的
                // 令牌（html.dark）要挂上类名才生效。网页不提供切换按钮，跟着程序走。
                await PostCelesteWebAsync(new { kind = "theme", dark });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ApplyCelesteThemeToWebAsync", ex);
            }
        }

        /// <summary>网页 → C# 消息入口。具体 kind 分发留给后续页面阶段。</summary>
        private void CelesteWebOnMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // 无条件记账：这一行必须在任何读取和提前返回之前。
            // 旧版把记账放在 TryGetWebMessageAsString() 之后，读空就静默 return，
            // 结果"页面显示 0 首"时日志里什么都没有，分不清是
            // 「消息根本没到」还是「到了但读不出来」——两种情况都得猜。
            _celesteWebMsgSeq++;
            string seq = _celesteWebMsgSeq.ToString();

            string raw = "";
            string via = "none";
            try
            {
                raw = e.TryGetWebMessageAsString() ?? "";
                via = "try";
                if (raw.Length == 0)
                {
                    // 兜底：TryGet 读空但 WebMessageAsJson 有内容时，从 JSON 形态恢复原文
                    var json = e.WebMessageAsJson;
                    if (!string.IsNullOrEmpty(json))
                    {
                        try
                        {
                            var un = JsonSerializer.Deserialize<string>(json);
                            if (!string.IsNullOrEmpty(un)) { raw = un; via = "json"; }
                        }
                        catch { /* 解不开就维持 raw 为空，下面照样记账 */ }
                    }
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("CelesteWebOnMessageReceived.read", ex);
                StartupLog.Write($"[Web试点] ↓#{seq} 事件到达但读取失败 hr=0x{ex.HResult:X8}");
                return;
            }

            string src = "";
            try { src = e.Source ?? ""; } catch { /* Source 读不到不致命 */ }
            StartupLog.Write($"[Web试点] ↓#{seq} via={via} len={raw.Length} src={src} | " +
                             (raw.Length > 96 ? raw.Substring(0, 96) + "…" : raw));
            if (string.IsNullOrWhiteSpace(raw)) return;

            WebInboundMessage? msg = null;
            try
            {
                // 网页上行是**扁平结构**：{"kind":"play","index":0}——业务字段和 kind 同级，
                // 没有 "payload" 包装层。以前用 JsonSerializer.Deserialize<WebInboundMessage>：
                //   ① 默认区分大小写，"kind" 映射不到 Kind → 所有消息被当成空 kind
                //      静默丢弃（日志有 ↓ 无 ↑、点击全部没反应，2026-10-09 用户实测坐实）；
                //   ② Payload 属性永远拿不到 index/position/value/on（它们在根上，不在
                //      "payload" 里）——就算 kind 映射上了，"播放第几首"也读不出来。
                // 改成手动解析：kind 提出来，整个根元素克隆给 Payload 当业务字段源。
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("kind", out var kindEl) &&
                    kindEl.ValueKind == JsonValueKind.String)
                {
                    string? kind = kindEl.GetString();
                    if (!string.IsNullOrWhiteSpace(kind))
                        msg = new WebInboundMessage { Kind = kind!, Payload = root.Clone() };
                }
            }
            catch (Exception ex)
            {
                // 解析失败也要记账：以前这里是静默 return，"点击没反应"时日志零线索
                StartupLog.WriteException("CelesteWebOnMessageReceived.parse", ex);
            }
            if (msg == null)
            {
                StartupLog.Write($"[Web试点] ↓#{seq} 解析不出 kind，丢弃");
                return;
            }

            // 每条上行消息都记账：网页"点了没反应"时，先看这里有没有记录——
            // 有记录 = C# 收到了、问题在后续处理；没记录 = 消息根本没发出来。
            StartupLog.Write($"[Web试点] ↑ {msg.Kind}");

            try { CelesteWebHandleMessage(msg); }
            catch (Exception ex) { StartupLog.WriteException("CelesteWebHandleMessage:" + msg.Kind, ex); }
        }

        /// <summary>
        /// 消息分发。网页只管长什么样，不做事：
        ///   点击播放/跳转/排序/多选 → C# 执行，网页只上报意图。
        ///   右键菜单 → C# 弹原生菜单（网页只报"在哪个条目 + 坐标"）。
        /// 阶段 1 只有 ping/ready 两类，其余先记日志不实现。
        /// </summary>
        private void CelesteWebHandleMessage(WebInboundMessage msg)
        {
            switch (msg.Kind)
            {
                case "ping":
                    _ = PostCelesteWebAsync(new { kind = "pong" });
                    break;

                case "ready":
                    // 网页侧初始化完成：把当前主题和路由推过去
                    _ = PushCelesteThemeAndRouteAsync();
                    // 第 3 步起：试点页就绪，顺手把专辑数据也推过去
                    // （ready 被这个 case 截住了，不会落到 default，所以要显式调一次）
                    HandleWebAlbumMessage(msg);
                    break;

                default:
                    // 第 3 步起：专辑详情页试点的消息交给 MainWindow.WebAlbum.cs 处理
                    HandleWebAlbumMessage(msg);
                    break;
            }
        }

        private async Task PushCelesteThemeAndRouteAsync()
        {
            var theme = LoadCelesteActiveTheme();
            if (theme != null)
            {
                bool dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
                await ApplyCelesteThemeToWebAsync(theme, dark);
            }
            if (_celesteWebCurrentRoute != null)
                await PostCelesteWebAsync(new { kind = "route", route = _celesteWebCurrentRoute });
        }
        // ⚠ LoadCelesteActiveTheme() 现在定义在 MainWindow.CelesteTheme.cs（同一 partial 类，直接用）。
        //   这里原本有一份重复实现，装回时已删除，别再拷回来——否则 CS0111 重复定义。
    }
}
