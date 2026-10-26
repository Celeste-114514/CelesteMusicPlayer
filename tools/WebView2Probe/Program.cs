// WebView2 可行性探针（第 0 步 · 零 UI 改动验证）
// 目的：在不动主工程的前提下，测出四个关键数字
//   1) WebView2 初始化耗时
//   2) 内存增量（宿主进程 + WebView2 子进程，后者才是大头）
//   3) C# <-> JS 通信往返延迟
//   4) CSS 变量注入是否即时生效（主题热切换的前提）
// 附带：首次导航渲染耗时、二次切页耗时（对应前端路由场景）
//
// 宿主用 WinForms 而非 WinUI3：这四个指标与 UI 框架无关，
// 用 WinForms 可以避开 WASDK 大包 restore，几分钟内拿到结果。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2Probe
{
    internal static class Program
    {
        private static readonly List<string> Lines = new List<string>();
        private static readonly string OutPath =
            Path.Combine(@"C:\Users\admin\source\repos\CelesteMusicPlayer", "_wv2_probe_out.txt");

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new Form
            {
                Text = "WebView2 Probe",
                Width = 1100,
                Height = 720,
                StartPosition = FormStartPosition.CenterScreen,
            };
            var wv = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(wv);

            form.Shown += async (_, __) =>
            {
                try
                {
                    await RunAsync(form, wv);
                }
                catch (Exception ex)
                {
                    Say("FATAL: " + ex);
                }
                finally
                {
                    Flush();
                    Application.Exit();
                }
            };

            Application.Run(form);
        }

        private static void Say(string s)
        {
            Lines.Add(s);
            Console.WriteLine(s);
            // 每步立即落盘：外部命令超时被掐断时也能拿到已完成部分的结果
            Flush();
        }

        private static void Flush()
        {
            try
            {
                File.WriteAllText(OutPath, string.Join(Environment.NewLine, Lines), Encoding.UTF8);
            }
            catch
            {
                // 忽略：控制台已经打过了
            }
        }

        private static long SelfMemory()
        {
            Process.GetCurrentProcess().Refresh();
            return Process.GetCurrentProcess().WorkingSet64;
        }

        /// <summary>WebView2 是多进程架构，真正的内存大头在自己的子进程里，必须单独统计。</summary>
        private static (long bytes, int count) WebView2ChildMemory()
        {
            long total = 0;
            int count = 0;
            foreach (var p in Process.GetProcessesByName("msedgewebview2"))
            {
                try
                {
                    total += p.WorkingSet64;
                    count++;
                }
                catch
                {
                    // 进程可能已退出
                }
            }
            return (total, count);
        }

        private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";

        private static string Stats(List<double> xs)
        {
            if (xs.Count == 0) return "n/a";
            var sorted = xs.OrderBy(x => x).ToList();
            return string.Format(
                "avg={0:F2}ms  p50={1:F2}ms  p95={2:F2}ms  min={3:F2}ms  max={4:F2}ms",
                xs.Average(), sorted[xs.Count / 2], sorted[(int)(xs.Count * 0.95)], sorted[0], sorted[^1]);
        }

        private static async Task RunAsync(Form form, WebView2 wv)
        {
            Say("=== WebView2 探针 ===");
            Say("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Say("宿主: " + Environment.OSVersion);
            Say("");

            // ---------- 基线 ----------
            long baseSelf = SelfMemory();
            var (baseChild, baseChildCount) = WebView2ChildMemory();
            Say("[基线] 宿主工作集=" + Mb(baseSelf) + "  WebView2子进程=" + baseChildCount + "个/" + Mb(baseChild));

            // ---------- 1. 初始化耗时 ----------
            Say("");
            Say("--- 1) 初始化 ---");
            var sw = Stopwatch.StartNew();
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Path.GetTempPath(), "CelesteWebviewProbe"));
            await wv.EnsureCoreWebView2Async(env);
            sw.Stop();
            Say("EnsureCoreWebView2Async 耗时: " + sw.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
            if (wv.CoreWebView2 == null)
            {
                Say("!! 初始化失败：CoreWebView2 为 null");
                return;
            }
            var ver = wv.CoreWebView2.Environment.BrowserVersionString;
            Say("Runtime 版本: " + ver);

            // 等子进程起来再测内存
            await Task.Delay(1500);
            long afterSelf = SelfMemory();
            var (afterChild, afterChildCount) = WebView2ChildMemory();
            Say("[初始化后] 宿主=" + Mb(afterSelf) + "  WebView2子进程=" + afterChildCount + "个/" + Mb(afterChild));
            Say("=> 宿主增量=" + Mb(afterSelf - baseSelf) + "  子进程增量=" + Mb(afterChild - baseChild) +
                "  合计增量=" + Mb((afterSelf - baseSelf) + (afterChild - baseChild)));

            // ---------- 2. 首次导航（模拟打开详情页） ----------
            Say("");
            Say("--- 2) 首次导航渲染 ---");
            var html = BuildTestHtml();
            sw.Restart();
            var navDone = new TaskCompletionSource<bool>();
            wv.CoreWebView2.NavigationCompleted += (s, e) => navDone.TrySetResult(e.IsSuccess);
            wv.NavigateToString(html);
            var ok = await navDone.Task;
            sw.Stop();
            Say("NavigateToString(约" + (html.Length / 1024) + "KB HTML) -> NavigationCompleted: " +
                sw.Elapsed.TotalMilliseconds.ToString("F1") + " ms  成功=" + ok);
            await Task.Delay(600);
            var (navChild, navChildCount) = WebView2ChildMemory();
            long navSelf = SelfMemory();
            Say("[渲染后] 宿主=" + Mb(navSelf) + "  WebView2子进程=" + navChildCount + "个/" + Mb(navChild));
            Say("=> 相对基线合计增量=" + Mb((navSelf - baseSelf) + (navChild - baseChild)));

            // ---------- 3. CSS 变量注入（主题热切换的前提） ----------
            Say("");
            Say("--- 3) CSS 变量注入 ---");
            var before = await wv.CoreWebView2.ExecuteScriptAsync(
                "getComputedStyle(document.querySelector('.panel')).backgroundColor");
            Say("注入前 .panel 背景 = " + before);

            var cssTimes = new List<double>();
            string applied = "";
            for (int i = 0; i < 30; i++)
            {
                var cssSw = Stopwatch.StartNew();
                applied = await wv.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement.style.setProperty('--celeste-panel-background'," +
                    (i % 2 == 0 ? "'#ff0000'" : "'#00ff00'") + ");" +
                    "getComputedStyle(document.querySelector('.panel')).backgroundColor");
                cssSw.Stop();
                cssTimes.Add(cssSw.Elapsed.TotalMilliseconds);
                // 每 6 次停 150ms，让人眼能看见红绿在跳（否则几十毫秒就跑完了）
                if (i % 6 == 0) await Task.Delay(150);
            }
            Say("注入+读回 30 次: " + Stats(cssTimes));
            // 收尾还原成正常深色，别让页面停在测试色上
            await wv.CoreWebView2.ExecuteScriptAsync(
                "document.documentElement.style.setProperty('--celeste-panel-background','#181b22');");
            Say("最后一次读回值 = " + applied + "  (应为 rgb(255,0,0) 或 rgb(0,255,0)，证明即时生效)");
            Say("判定: " + (applied.Contains("255") ? "生效 OK" : "!! 未生效"));

            // ---------- 4. 通信往返 ----------
            Say("");
            Say("--- 4) C# <-> JS 通信 ---");

            // 4a. ExecuteScriptAsync 往返（C# 主动调 JS 并取回值）
            var execTimes = new List<double>();
            for (int i = 0; i < 50; i++)
            {
                var esw = Stopwatch.StartNew();
                await wv.CoreWebView2.ExecuteScriptAsync("1+1");
                esw.Stop();
                execTimes.Add(esw.Elapsed.TotalMilliseconds);
            }
            Say("4a) ExecuteScriptAsync 往返 x50: " + Stats(execTimes));

            // 4b. JS -> C# 上行消息（点击播放等业务动作走这条）
            Say("4b) JS->C# 通道诊断");
            var typeOf = await wv.CoreWebView2.ExecuteScriptAsync("typeof window.chrome.webview");
            Say("  typeof window.chrome.webview = " + typeOf);
            var canPost = await wv.CoreWebView2.ExecuteScriptAsync(
                "!!(window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function')");
            Say("  JS 侧可 postMessage = " + canPost);

            await wv.CoreWebView2.ExecuteScriptAsync(
                "window.chrome.webview.addEventListener('message', function(e){" +
                "  try { window.chrome.webview.postMessage(e.data); }" +
                "  catch(err){ window.chrome.webview.postMessage('ERR:' + err); }" +
                "}); true");
            Say("  listener 已注册");

            // 先单次试，看到底通不通
            var probe1 = new TaskCompletionSource<string>();
            void H1(object s, CoreWebView2WebMessageReceivedEventArgs e)
            {
                probe1.TrySetResult(e.TryGetWebMessageAsString() ?? "<null>");
            }
            wv.CoreWebView2.WebMessageReceived += H1;
            var s1 = Stopwatch.StartNew();
            wv.CoreWebView2.PostWebMessageAsString("hello");
            var d1 = await Task.WhenAny(probe1.Task, Task.Delay(2000));
            s1.Stop();
            wv.CoreWebView2.WebMessageReceived -= H1;
            bool upOk = d1 == probe1.Task;
            Say("  PostWebMessageAsString 单次: " +
                (upOk ? "收到='" + probe1.Task.Result + "' 耗时=" + s1.Elapsed.TotalMilliseconds.ToString("F1") + "ms"
                      : "!! 2 秒超时未收到"));

            // 4c. 真实业务路径：用户在网页上点击 -> JS 主动上报 -> C# 接收
            await wv.CoreWebView2.ExecuteScriptAsync(
                "window.__burst=function(n){ for(var i=0;i<n;i++){ window.chrome.webview.postMessage('act:'+i); } return n; }; true");

            int gotCount = 0;
            var burstDone = new TaskCompletionSource<bool>();
            void H2(object s, CoreWebView2WebMessageReceivedEventArgs e)
            {
                gotCount++;
                if (gotCount >= 50) burstDone.TrySetResult(true);
            }
            wv.CoreWebView2.WebMessageReceived += H2;
            var bw = Stopwatch.StartNew();
            await wv.CoreWebView2.ExecuteScriptAsync("window.__burst(50)");
            var bd = await Task.WhenAny(burstDone.Task, Task.Delay(5000));
            bw.Stop();
            wv.CoreWebView2.WebMessageReceived -= H2;
            Say("  JS 主动上报 50 条(真实业务路径): " + (bd == burstDone.Task
                ? "全部到达  总耗时=" + bw.Elapsed.TotalMilliseconds.ToString("F1") +
                  "ms  单条均值≈" + (bw.Elapsed.TotalMilliseconds / 50).ToString("F2") + "ms"
                : "!! 超时，只收到 " + gotCount + " 条"));

            // 4d. 下行：C# 主动推给 JS
            var downTimes = new List<double>();
            for (int i = 0; i < 50; i++)
            {
                var dsw = Stopwatch.StartNew();
                wv.CoreWebView2.PostWebMessageAsString("cmd" + i);
                await wv.CoreWebView2.ExecuteScriptAsync("1");
                dsw.Stop();
                downTimes.Add(dsw.Elapsed.TotalMilliseconds);
            }
            Say("  C# 下行 PostWebMessageAsString x50: " + Stats(downTimes));

            // ---------- 5. 切页（前端路由场景） ----------
            Say("");
            Say("--- 5) 切页（模拟前端路由重绘） ---");
            var switchTimes = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                var ssw = Stopwatch.StartNew();
                await wv.CoreWebView2.ExecuteScriptAsync(
                    "window.renderAlbum(" + (i + 1) + ") && new Promise(r => requestAnimationFrame(() => r(true)))");
                ssw.Stop();
                switchTimes.Add(ssw.Elapsed.TotalMilliseconds);
            }
            Say("替换曲目列表并等到下一帧 x20: " + Stats(switchTimes));

            // ---------- 6. 第二个实例增量（验证"常驻单实例"是否必须） ----------
            Say("");
            Say("--- 6) 第二个 WebView2 实例增量 ---");
            long before2Self = SelfMemory();
            var (before2Child, before2Count) = WebView2ChildMemory();
            var wv2 = new WebView2 { Dock = DockStyle.Fill, Visible = false };
            form.Controls.Add(wv2);
            var sw6 = Stopwatch.StartNew();
            await wv2.EnsureCoreWebView2Async(env);
            sw6.Stop();
            await Task.Delay(1500);
            long after2Self = SelfMemory();
            var (after2Child, after2Count) = WebView2ChildMemory();
            Say("第二个 EnsureCoreWebView2Async 耗时: " + sw6.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
            Say("子进程数 " + before2Count + " -> " + after2Count);
            Say("=> 第二实例宿主增量=" + Mb(after2Self - before2Self) +
                "  子进程增量=" + Mb(after2Child - before2Child) +
                "  合计=" + Mb((after2Self - before2Self) + (after2Child - before2Child)));
            Say("   (对比第一实例合计增量 ≈ 283 MB —— 差额越小，越说明必须走常驻单实例)");

            // ---------- 收尾 ----------
            await Task.Delay(300);
            var (endChild, endChildCount) = WebView2ChildMemory();
            long endSelf = SelfMemory();
            Say("");
            Say("--- 汇总 ---");
            Say("稳态宿主工作集=" + Mb(endSelf) + "  WebView2子进程=" + endChildCount + "个/" + Mb(endChild));
            Say("稳态总内存=" + Mb(endSelf + endChild));
            Say("");
            Say("对照：主工程 CelesteMusicPlayer 实机内存请自行在任务管理器核对（基线参考值）。");
        }

        /// <summary>模拟专辑详情页规模：封面 + 元信息 + 20 轨列表，全部走 CSS 变量。</summary>
        private static string BuildTestHtml()
        {
            var rows = new StringBuilder();
            for (int i = 1; i <= 20; i++)
            {
                int sec = (i * 7) % 60;
                rows.Append("<div class='row'><span class='no'>")
                    .Append(i.ToString("00"))
                    .Append("</span><span class='t'>Track Title ")
                    .Append(i)
                    .Append("</span><span class='d'>")
                    .Append(3 + (i % 3))
                    .Append(":")
                    .Append(sec.ToString("00"))
                    .Append("</span><span class='fmt'>FLAC 24/96</span></div>");
            }

            return @"<!DOCTYPE html><html><head><meta charset='utf-8'>
<style>
:root{
  --celeste-app-background:#101216;
  --celeste-panel-background:#181b22;
  --celeste-card-background:#1e222b;
  --celeste-text-primary:#e7e9ef;
  --celeste-text-secondary:#a8b0c0;
  --celeste-accent-solid:#f0b429;
  --celeste-divider:#2b303b;
  --celeste-radius-medium:8px;
}
*{box-sizing:border-box}
body{margin:0;background:var(--celeste-app-background);color:var(--celeste-text-primary);
     font-family:'Segoe UI','Microsoft YaHei',sans-serif}
.panel{background:var(--celeste-panel-background);padding:18px;margin:0}
.head{display:flex;gap:20px}
.cover{width:200px;height:200px;background:linear-gradient(135deg,#333,#666);
       border-radius:var(--celeste-radius-medium)}
.meta h1{margin:0 0 8px;font-size:24px}
.meta p{margin:2px 0;color:var(--celeste-text-secondary);font-size:13px}
.tracks{margin-top:16px}
.row{display:flex;align-items:center;gap:14px;padding:8px 10px;
     border-bottom:1px solid var(--celeste-divider);font-size:14px}
.row:hover{background:var(--celeste-card-background)}
.no{width:28px;color:var(--celeste-text-secondary)}
.t{flex:1}
.d{width:50px;color:var(--celeste-text-secondary);text-align:right}
.fmt{width:96px;color:var(--celeste-accent-solid);font-size:12px;text-align:right}
</style></head><body>
<div class='panel'>
  <div class='head'>
    <div class='cover'></div>
    <div class='meta'>
      <h1 id='albumName'>Album Title</h1>
      <p id='artist'>Artist Name</p>
      <p id='info'>2026 · 20 tracks · FLAC 24/96</p>
    </div>
  </div>
  <div class='tracks' id='tracks'>" + rows + @"</div>
</div>
<script>
window.renderAlbum = function(n){
  var box = document.getElementById('tracks');
  var html = '';
  for (var i = 1; i <= 20; i++) {
    html += '<div class=""row""><span class=""no"">' + (i<10?'0':'') + i +
            '</span><span class=""t"">Album ' + n + ' - Track ' + i +
            '</span><span class=""d"">3:20</span><span class=""fmt"">FLAC 24/96</span></div>';
  }
  box.innerHTML = html;
  document.getElementById('albumName').textContent = 'Album ' + n;
  return true;
};
</script></body></html>";
        }
    }
}
