// 皮肤系统批次 1：Windows 风（Fluent）新版主界面。
//
// 平行页方案：winmain.html 是 main.html 的整页拷贝 + Fluent 换肤（字体/圆角/毛玻璃/
// 图标字形），DOM 结构、消息协议、数据字段**零改动**——所以这里不复制 WebMain.cs
// 那 3400 行推送逻辑，直接复用同一套：
//   · _webMainOpen 是共用的主开关（推送/每秒定时器/消息分发全看它）
//   · _webWinOpen 只记一件事：当前加载的是哪张页（winmain.html = true）
// 两个入口互斥：开 Windows 风先关 Apple 风，反之亦然；关闭统一走 CloseWebMain。
//
// 消息协议与 main.html 一一对应（改一边必须改另一边），详见 MainWindow.WebMain.cs。

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        /// <summary>Windows 风（Fluent）新版主界面是否开着。与 _webMainOpen 联动：
        /// 它为 true 时 _webMainOpen 必为 true（共用一套推送/定时器/消息分发）。</summary>
        private bool _webWinOpen;

        /// <summary>打开 Windows 风（Fluent）新版主界面。失败一律静默降级：界面保持原样。</summary>
        public async Task OpenWebWinMainAsync()
        {
            if (_webWinOpen) return;             // 已经是 Windows 风页，别重开
            if (_webMainOpening) return;         // Apple 风正在开，等它（连点防护）
            if (_webMainOpen) CloseWebMain();    // Apple 风开着 → 先关干净（互斥）
            _webMainOpening = true;
            try
            {
                DeployWebAsset("winmain.html");

                if (!await EnsureCelesteWebHostAsync()) return;

                CelesteWebHostGrid.Visibility = Visibility.Visible;
                // 预热时 WebView2 是 Collapsed（免得启动时闪一下白屏），这会儿才让它现形
                if (_celesteWeb != null)
                {
                    _celesteWeb.Visibility = Visibility.Visible;
                    try { _celesteWeb.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
                    catch { /* 聚焦失败不致命，照常打开 */ }
                }
                if (_celesteWebBackButton != null) _celesteWebBackButton.Visibility = Visibility.Collapsed;
                _webWinOpen = true;
                _webMainOpen = true;             // 共用主开关：数据推送/定时器/消息路由全靠它

                var cv = _celesteWeb!.CoreWebView2;
                cv.Navigate("http://celeste.local/winmain.html");

                // 页面自检：与 Apple 风同款——探到 rows/cards 为 0 就主动推一次数据
                _ = ProbeWebMainStateAsync(cv);

                // 深浅色先告知网页（网页不提供切换按钮，跟着程序走）
                _ = PostCelesteWebAsync(new Dictionary<string, object?>
                {
                    ["kind"] = "theme",
                    ["dark"] = false,
                });

                // 每秒把播放状态推给网页（位置、时长、播放中、音量、收藏）
                _webMainTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _webMainTimer.Tick -= WebMainTimer_Tick;
                _webMainTimer.Tick += WebMainTimer_Tick;
                _webMainTimer.Start();

                StartupLog.Write("[新版界面] 已打开 Windows 风（Fluent）主界面");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenWebWinMain", ex);
                CloseWebMain();
            }
            finally
            {
                _webMainOpening = false;
            }
        }

        /// <summary>关掉 Windows 风新版界面，回到原生界面。两种主界面页共用同一个出口。</summary>
        public void CloseWebWinMain() => CloseWebMain();

        /// <summary>歌曲库工具栏里的「新版界面」按钮（Windows 风入口，与「Apple 风格界面」并排）。</summary>
        private void WebWinMainButton_Click(object sender, RoutedEventArgs e)
            => _ = OpenWebWinMainAsync();
    }
}
