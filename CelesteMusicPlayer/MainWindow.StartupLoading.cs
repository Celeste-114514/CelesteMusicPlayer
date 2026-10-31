using System;
using Microsoft.UI.Dispatching;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 启动收尾：兜底定时器 + 「启动完成」这个时间点。
    ///
    /// 以前这里还负责一块盖在主窗口上的加载遮罩（淡出动画、转圈、状态文字）。那块遮罩画在
    /// 垫片控件上，从界面换到网页壳那天起就没再显示过 —— 网页有自己的启动遮罩
    /// （WebShellWindow 里的 _status 层）。这里只保留两件仍然有意义的事：
    ///   ① 兜底：曲库恢复若因故没走完，到点也要把「启动完成」标出来，别让外壳的
    ///      「上次没正常退出」提示永远等下去；
    ///   ② 揭开的分界点：<see cref="HideStartupLoading"/> 里通知外壳可以亮提示了。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>启动收尾的最长等待。曲库再慢也会到点就走。</summary>
        private static readonly TimeSpan LoadingSafetyTimeout = TimeSpan.FromSeconds(25);

        private DispatcherQueueTimer? _loadingSafetyTimer;
        private bool _startupLoadingHidden;

        /// <summary>在构造函数里调一次：起兜底定时器。任何一步失败都不该影响主程序启动。</summary>
        private void SetupStartupLoading()
        {
            try
            {
                _loadingSafetyTimer = DispatcherQueue.CreateTimer();
                _loadingSafetyTimer.Interval = LoadingSafetyTimeout;
                _loadingSafetyTimer.Tick += (_, _) =>
                {
                    AppLog.Warn(LogCategory.App, "启动收尾超时（曲库恢复可能没走完），兜底放行");
                    HideStartupLoading();
                };
                _loadingSafetyTimer.Start();

                AppLog.Info(LogCategory.App, "启动加载遮罩已就绪（超时兜底 "
                    + LoadingSafetyTimeout.TotalSeconds + "s）");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.SetupStartupLoading", caught);
                _startupLoadingHidden = true;
            }
        }

        /// <summary>标记「启动完成」。曲库恢复、超时两条路都会调，幂等，只生效第一次。</summary>
        private void HideStartupLoading()
        {
            if (_startupLoadingHidden) return;
            _startupLoadingHidden = true;

            // 这里就是"进入主界面"的分界点：外壳攒到这一刻才把
            // 「上次没正常退出」的提示亮出来（崩溃当时那条不走这里）。
            try { _webShellWindow?.NotifyStartupSettled(); }
            catch (Exception caught) { StartupLog.WriteException("MainWindow.NotifyStartupSettled", caught); }

            try { _loadingSafetyTimer?.Stop(); } catch { /* 停表失败无所谓，反正已经放行了 */ }

            AppLog.Info(LogCategory.App, "启动加载遮罩已撤下");
        }
    }
}
