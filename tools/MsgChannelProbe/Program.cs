// 消息通道探针：复刻主工程 WebView2 试点的全部条件，定位"页面渲染正常但
// 上行消息到不了 C#"的断点。
//
// 与主工程一致的条件：
//   1) EnsureCoreWebView2Async() 无参（默认用户数据目录，不用自定义 env）
//   2) SetVirtualHostNameToFolderMapping("celeste.local", WebAssets, Allow)
//   3) AddScriptToExecuteOnDocumentCreatedAsync 注入 __celesteApplyTheme
//   4) 同一组 Settings（右键菜单/DevTools/状态栏关、缩放开）
//   5) Navigate("http://celeste.local/album.html") —— 真实的部署网页
//
// 诊断输出：
//   A) 每条 WebMessageReceived 带时间戳落盘（验证页面 post 的 ready 到不到）
//   B) ExecuteScriptAsync 体检 window.chrome / chrome.webview 的真身
//      （typeof postMessage / addEventListener —— 排除"truthy 但不是宿主对象"）
//   C) 手动 postMessage 往返（页面逻辑 vs 通道本身，二分定位）
//   D) 竞态实测：独立小页面记录"脚本执行那一刻 chrome.webview 在不在"，
//      之后每 50ms 轮询记录它何时出现（验证 whenWv 竞态假设是否成立）
//
// 结果写到 _msg_probe_out.txt，每步立即落盘。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MsgChannelProbe
{
    internal static class Program
    {
        private static readonly List<string> Lines = new List<string>();
        private static readonly string OutPath =
            Path.Combine(@"C:\Users\MSI\source\repos\CelesteMusicPlayer", "_msg_probe_out.txt");
        private static readonly string WebAssets =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "CelesteMusicPlayer", "WebAssets");

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new Form
            {
                Text = "MsgChannelProbe",
                Width = 1100,
                Height = 720,
                StartPosition = FormStartPosition.CenterScreen,
            };
            var wv = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(wv);
            form.Shown += async (_, __) =>
            {
                try { await RunAsync(wv); }
                catch (Exception ex) { Say("FATAL: " + ex); }
                finally { Flush(); Application.Exit(); }
            };
            Application.Run(form);
        }

        private static void Say(string s)
        {
            Lines.Add(s);
            Console.WriteLine(s);
            Flush();
        }

        private static void Flush()
        {
            try { File.WriteAllText(OutPath, string.Join(Environment.NewLine, Lines), Encoding.UTF8); }
            catch { /* 控制台已打 */ }
        }

        private static string Ts() => DateTime.Now.ToString("HH:mm:ss.fff");

        private static async Task RunAsync(WebView2 wv)
        {
            Say("=== 消息通道探针 === " + Ts());
            Say("WebAssets = " + WebAssets);
            Say("album.html 存在 = " + File.Exists(Path.Combine(WebAssets, "album.html")) +
                "  大小 = " + (File.Exists(Path.Combine(WebAssets, "album.html"))
                    ? new FileInfo(Path.Combine(WebAssets, "album.html")).Length : 0));
            Say("");

            // ---- 与主工程一致：无参 EnsureCoreWebView2Async（默认 UDF）----
            var sw = Stopwatch.StartNew();
            await wv.EnsureCoreWebView2Async();
            sw.Stop();
            var cv = wv.CoreWebView2;
            Say("[1] EnsureCoreWebView2Async " + sw.Elapsed.TotalMilliseconds.ToString("F0") + "ms  " +
                "cv=" + (cv == null ? "null" : "ok") + "  版本=" + cv?.Environment.BrowserVersionString);

            // ---- 虚拟主机映射（同主工程）----
            cv.SetVirtualHostNameToFolderMapping("celeste.local", WebAssets,
                CoreWebView2HostResourceAccessKind.Allow);
            Say("[2] 虚拟主机映射 celeste.local -> WebAssets 已设");

            // ---- 主题注入脚本（同主工程）----
            await cv.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__celesteApplyTheme = function(css){" +
                "  var s = document.getElementById('__celeste_theme');" +
                "  if (!s) { s = document.createElement('style');" +
                "    s.id = '__celeste_theme';" +
                "    if (document.head) document.head.appendChild(s); }" +
                "  s.textContent = css; return true; };");
            Say("[3] AddScriptToExecuteOnDocumentCreatedAsync 已挂");

            // ---- Settings（同主工程）----
            cv.Settings.AreDefaultContextMenusEnabled = false;
            cv.Settings.AreDevToolsEnabled = false;
            cv.Settings.IsStatusBarEnabled = false;
            cv.Settings.IsZoomControlEnabled = true;
            Say("[4] Settings 已应用");

            // ---- 上行消息全记账 ----
            int upCount = 0;
            cv.WebMessageReceived += (s, e) =>
            {
                upCount++;
                string raw = "<read-failed>";
                try { raw = e.TryGetWebMessageAsString() ?? "<null>"; }
                catch (Exception ex) { raw = "<throw:" + ex.GetType().Name + ">"; }
                Say($"[UP #{upCount}] {Ts()} raw={raw}");
            };
            Say("[5] WebMessageReceived 已挂（每条上行都记账）");

            // ---- 导航真实网页 ----
            var navDone = new TaskCompletionSource<bool>();
            cv.NavigationCompleted += (s, e) =>
                navDone.TrySetResult(e.IsSuccess);
            Say("[6] Navigate http://celeste.local/album.html  " + Ts());
            cv.Navigate("http://celeste.local/album.html");
            var navOk = await Task.WhenAny(navDone.Task, Task.Delay(8000)) == navDone.Task;
            Say("    NavigationCompleted success=" + navOk + "  " + Ts());
            await Task.Delay(2500);
            Say($"[7] 导航后 2.5s：上行消息共 {upCount} 条" +
                (upCount == 0 ? "  <<< 页面一条都没发上来！" : ""));

            // ---- B) window.chrome 真身体检 ----
            Say("");
            Say("--- B) window.chrome 体检 ---");
            string[][] checks =
            {
                new[] { "Object.keys(window.chrome||{}).join(',')", "chrome 的键" },
                new[] { "typeof window.chrome", "typeof chrome" },
                new[] { "typeof (window.chrome&&window.chrome.webview)", "typeof chrome.webview" },
                new[] { "typeof (window.chrome&&window.chrome.webview&&window.chrome.webview.postMessage)", "typeof webview.postMessage" },
                new[] { "typeof (window.chrome&&window.chrome.webview&&window.chrome.webview.addEventListener)", "typeof webview.addEventListener" },
                new[] { "typeof window.__celesteApplyTheme", "页面脚本跑的标记 __celesteApplyTheme" },
                new[] { "!!document.querySelector('.empty')", ".empty 空态出现了吗（paint 跑过）" },
                new[] { "document.querySelectorAll('.row').length", "行数" },
                new[] { "(document.getElementById('hdrTitle')||{}).textContent", "页头标题" },
            };
            foreach (var c in checks)
            {
                try
                {
                    var r = await cv.ExecuteScriptAsync(c[0]);
                    Say("  " + c[1] + " = " + Decode(r));
                }
                catch (Exception ex) { Say("  " + c[1] + " <<抛异常>> " + ex.GetType().Name + " " + ex.Message); }
            }

            // ---- C) 手动 postMessage 往返（二分：页面逻辑 vs 通道）----
            Say("");
            Say("--- C) 手动 postMessage 往返 ---");
            var ping = new TaskCompletionSource<string>();
            void H(object s, CoreWebView2WebMessageReceivedEventArgs e)
            {
                try { ping.TrySetResult(e.TryGetWebMessageAsString() ?? "<null>"); }
                catch (Exception ex) { ping.TrySetResult("<throw:" + ex.GetType().Name + ">"); }
            }
            cv.WebMessageReceived += H;
            try
            {
                await cv.ExecuteScriptAsync(
                    "window.chrome.webview.postMessage('{\"kind\":\"ping\"}'); true");
            }
            catch (Exception ex) { Say("  手动 postMessage 调用本身抛异常: " + ex.Message); }
            var got = await Task.WhenAny(ping.Task, Task.Delay(3000));
            cv.WebMessageReceived -= H;
            Say("  手动 ping -> C#: " + (got == ping.Task
                ? "收到 raw=" + ping.Task.Result + "  （通道本身是通的！断点在页面逻辑）"
                : "3 秒没收到  （通道本身就不通，与页面无关）"));

            // ---- D) 竞态实测：脚本执行那刻 chrome.webview 在不在 ----
            Say("");
            Say("--- D) 竞态实测（独立小页面） ---");
            string probeDir = Path.Combine(Path.GetTempPath(), "CelesteMsgProbe");
            Directory.CreateDirectory(probeDir);
            File.WriteAllText(Path.Combine(probeDir, "race.html"),
                "<!DOCTYPE html><html><head><meta charset='utf-8'></head><body><script>" +
                "window.__atScript = !!(window.chrome && window.chrome.webview);" +
                "window.__appearedAt = -1; var t0=Date.now();" +
                "var iv=setInterval(function(){" +
                "  if (window.chrome && window.chrome.webview){" +
                "    window.__appearedAt = Date.now()-t0; clearInterval(iv);" +
                "  }" +
                "  if (Date.now()-t0>5000) clearInterval(iv);" +
                "},10);" +
                "</script></body></html>", Encoding.UTF8);
            cv.SetVirtualHostNameToFolderMapping("probe.local", probeDir,
                CoreWebView2HostResourceAccessKind.Allow);
            var nav2 = new TaskCompletionSource<bool>();
            void Nav2(object s, CoreWebView2NavigationCompletedEventArgs e) { nav2.TrySetResult(e.IsSuccess); }
            cv.NavigationCompleted += Nav2;
            cv.Navigate("http://probe.local/race.html");
            await Task.WhenAny(nav2.Task, Task.Delay(8000));
            cv.NavigationCompleted -= Nav2;
            await Task.Delay(1200);
            foreach (var expr in new[]
            {
                "window.__atScript",
                "window.__appearedAt",
            })
            {
                try { Say("  " + expr + " = " + Decode(await cv.ExecuteScriptAsync(expr))); }
                catch (Exception ex) { Say("  " + expr + " <<抛异常>> " + ex.Message); }
            }
            Say("  解读：__atScript=false 且 __appearedAt>=0 → 竞态真实存在（whenWv 有必要）；");
            Say("        __atScript=true → 竞态不存在，问题另有所属；");
            Say("        __appearedAt=-1（5 秒都没出现）→ 宿主对象压根没注入（另一类问题）。");

            Say("");
            Say("=== 探针结束 " + Ts() + " ===");
        }

        private static string Decode(string raw)
        {
            try
            {
                var s = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                return s ?? raw;
            }
            catch { return raw; }
        }
    }
}
