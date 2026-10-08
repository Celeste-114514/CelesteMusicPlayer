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
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var web = new WebView2 { Visibility = Visibility.Collapsed };
                // 挂进视觉树才初始化；宿主容器是零尺寸的，不影响任何布局
                if (CelesteWebHostGrid == null)
                {
                    // 没有宿主容器（XAML 未加 WebHostGrid）→ 静默跳过，不算错误
                    _celesteWebInitializing = false;
                    return;
                }
                CelesteWebHostGrid.Children.Add(web);
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
                // ⚠ 必须用 WebMessageAsString 读。PostWebMessageAsJson 发对象时
                //   TryGetWebMessageAsString() 读不出字符串（探针实测 50/50 失败）。
                //   所以业务消息一律「字符串 JSON」，C# 侧自己反序列化。
                cv.WebMessageReceived += CelesteWebOnMessageReceived;

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
                if (_celesteWeb != null) CleanupCelesteWeb(_celesteWeb);
            }
            finally
            {
                _celesteWebInitializing = false;
            }
        }

        private void CleanupCelesteWeb(WebView2? web)
        {
            if (web?.CoreWebView2 != null) web.CoreWebView2.WebMessageReceived -= CelesteWebOnMessageReceived;
            if (CelesteWebHostGrid != null && web != null) CelesteWebHostGrid.Children.Remove(web);
            web?.Close();
            _celesteWeb = null;
            _celesteWebReady = false;
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
            string raw;
            try { raw = e.TryGetWebMessageAsString() ?? ""; }
            catch (Exception ex)
            {
                StartupLog.WriteException("CelesteWebOnMessageReceived.read", ex);
                return;
            }
            if (string.IsNullOrWhiteSpace(raw)) return;

            WebInboundMessage? msg;
            try { msg = JsonSerializer.Deserialize<WebInboundMessage>(raw); }
            catch { return; }   // 网页发了非法 JSON：静默丢弃，不让一个坏消息影响程序
            if (msg == null || string.IsNullOrWhiteSpace(msg.Kind)) return;

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
