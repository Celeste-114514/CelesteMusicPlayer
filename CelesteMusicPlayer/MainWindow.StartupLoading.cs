using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 启动加载动画（盖在主窗口自己身上的遮罩）。
    ///
    /// 为什么不另开一个窗口：另开的窗口看着就像另一个程序，而且在主窗口那 8000 多行
    /// XAML 解析期间它一帧都画不出来，反而更尴尬。放在主窗口内部，窗口一出现动画就在转。
    ///
    /// 实测启动耗时（3609 首曲库）：
    ///   0.0 ~ 1.9s  主窗口还没画出来（XAML 解析 + 首次布局，这段遮不住，见下）
    ///   1.9s ~ 16s  窗口出来了，但曲库还在恢复 —— **这 14 秒以前是一片空列表**，
    ///              用户完全不知道程序在干嘛。遮罩就是为这段准备的。
    ///
    /// 撤掉的三种方式，任何一个先到都算：
    ///   ① 曲库恢复流程跑完（RestoreLastLibraryThenPendingFileAsync 收尾，见 MainWindow.ExternalFile.cs）
    ///   ② 用户点一下遮罩 —— 14 秒不短，不想等的人不该被摁在动画前
    ///   ③ 兜底超时（LoadingSafetyTimeoutMs）—— 万一恢复流程哪天不走了，别把人永久困住
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>遮罩最长停留时间。曲库再慢也会到点就走，不许把用户关在动画里。</summary>
        private static readonly TimeSpan LoadingSafetyTimeout = TimeSpan.FromSeconds(25);

        // 注意：不是 readonly —— 它在 SetupStartupLoading() 里赋值，
        // 而 readonly 字段只允许在构造函数/字段初始化器里写，方法内赋值编译不过。
        private DispatcherQueueTimer? _loadingSafetyTimer;
        private bool _startupLoadingHidden;

        /// <summary>
        /// 在构造函数里 InitializeComponent 之后调一次：上色、挂点击、起兜底定时器。
        /// 任何一步失败都不该影响主程序启动 —— 遮罩只是体验，不是功能。
        /// </summary>
        private void SetupStartupLoading()
        {
            try
            {
                // 转圈的进度环跟着主题色走；极客皮肤用磷光色（和其他界面一个口径）
                if (StartupLoadingRing != null)
                {
                    StartupLoadingRing.Foreground = new SolidColorBrush(StartupLoadingAccentColor());
                }

                if (StartupLoadingOverlay != null)
                {
                    // 点一下就放人：不等曲库恢复完也能先进去操作
                    StartupLoadingOverlay.Tapped += (_, _) => HideStartupLoading();
                    StartupLoadingOverlay.IsDoubleTapEnabled = false;
                }

                _loadingSafetyTimer = DispatcherQueue.CreateTimer();
                _loadingSafetyTimer.Interval = LoadingSafetyTimeout;
                _loadingSafetyTimer.Tick += (_, _) =>
                {
                    AppLog.Warn(LogCategory.App, "启动遮罩超时未撤（曲库恢复可能没走完），兜底揭开");
                    HideStartupLoading();
                };
                _loadingSafetyTimer.Start();

                AppLog.Info(LogCategory.App, "启动加载遮罩已就绪（超时兜底 "
                    + LoadingSafetyTimeout.TotalSeconds + "s）");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.SetupStartupLoading", caught);
                // 遮罩搭不起来就直接放行，绝不能让它把主窗口挡住
                _startupLoadingHidden = true;
                try { if (StartupLoadingOverlay != null) StartupLoadingOverlay.Visibility = Visibility.Collapsed; }
                catch { /* 真就连Visibility都设不上也没辙 */ }
            }
        }

        /// <summary>更新遮罩上的状态文字（曲库恢复到不同阶段时由各处调用）。幂等、可频繁调。</summary>
        private void UpdateStartupLoadingStatus(string text)
        {
            if (_startupLoadingHidden) return;
            if (StartupLoadingStatus == null) return;
            try
            {
                StartupLoadingStatus.Text = text;
            }
            catch
            {
                // 控件已经被回收/换掉了，忽略
            }
        }

        /// <summary>
        /// 撤掉遮罩：淡出 140ms 再收起。幂等 —— 曲库恢复、点击、超时三条路都会调，
        /// 谁来都行，只生效第一次。
        /// </summary>
        private void HideStartupLoading()
        {
            if (_startupLoadingHidden) return;
            _startupLoadingHidden = true;

            try { _loadingSafetyTimer?.Stop(); } catch { /* 停表失败无所谓，反正已经放行了 */ }

            try
            {
                if (StartupLoadingOverlay == null
                    || StartupLoadingOverlay.Visibility != Visibility.Visible)
                {
                    return;
                }

                var fade = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                    // Opacity 是独立动画，跑在合成器上，不占 UI 线程
                    EnableDependentAnimation = false
                };
                var storyboard = new Storyboard();
                storyboard.Children.Add(fade);
                Storyboard.SetTarget(fade, StartupLoadingOverlay);
                Storyboard.SetTargetProperty(fade, "Opacity");
                storyboard.Completed += (_, _) =>
                {
                    try
                    {
                        StartupLoadingOverlay.Opacity = 1;
                        StartupLoadingOverlay.Visibility = Visibility.Collapsed;
                        if (StartupLoadingRing != null) StartupLoadingRing.IsActive = false;
                    }
                    catch { /* 窗口正在关，无所谓 */ }
                };
                storyboard.Begin();

                AppLog.Info(LogCategory.App, "启动加载遮罩已撤下");
            }
            catch (Exception caught)
            {
                // 淡出失败就直接收起来，至少不能留在屏幕上挡人
                try
                {
                    if (StartupLoadingOverlay != null) StartupLoadingOverlay.Visibility = Visibility.Collapsed;
                }
                catch { /* 忽略 */ }
                StartupLog.WriteException("MainWindow.HideStartupLoading", caught);
            }
        }

        /// <summary>遮罩配色：极客皮肤用磷光色，其余用当前主题强调色（和三套皮肤口径一致）。</summary>
        private static Windows.UI.Color StartupLoadingAccentColor()
        {
            try
            {
                if (string.Equals(AppSettingsStore.Load().UiStyleMode?.Trim(), "Geek",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return MainWindow.GeekPhosphorColor();
                }
            }
            catch { /* 读设置失败就用默认色，不影响动画 */ }

            try
            {
                return ThemeColorService.CurrentAccent;
            }
            catch
            {
                return Windows.UI.Color.FromArgb(255, 0, 120, 212);
            }
        }
    }
}
