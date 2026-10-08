using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 托盘右键菜单的构造器。
    ///
    /// 为什么单独拆一个类：以后要做皮肤系统，托盘菜单也得能跟着换皮
    /// （比如极客风的等宽字体、自绘选中条）。菜单"长什么样、有哪些项"集中在这里，
    /// 换皮时只需要换这个 Builder，不动 AppTrayIcon 里那些创建/补建托盘的逻辑。
    ///
    /// 当前模式（Windows 11 风格）：
    /// H.NotifyIcon 的 <see cref="H.NotifyIcon.ContextMenuMode.SecondWindow"/> 用一个
    /// 独立的透明窗口承载 MenuFlyout，走的是 WinUI 原生渲染 —— 圆角、跟随深浅色、
    /// 悬停高亮，和系统右键菜单同一套观感。
    /// （默认的 PopupMenu 是库自己画皮模拟的，看着非常朴素，就是我们之前的樣子。）
    /// </summary>
    internal static class TrayMenuBuilder
    {
        /// 菜单最小宽度（像素）。嫌宽嫌窄改这一个数就行。
        ///
        /// 为什么非要钉宽度：SecondWindow 用一个透明窗口承载菜单，测量时拿到的
        /// 可用宽度偏小，中文长句（比如「发现新版本 26.10.2.0（点击查看）」）尾巴
        /// 会被截掉。给菜单项和容器都设 MinWidth，不管按哪种可用宽度测量都不会再被压窄。
        private const double MenuMinWidth = 220;

        /// <summary>
        ///  Build 一份新的右键菜单。
        /// </summary>
        /// <param name="owner">主窗口，菜单命令都转发给它的现成 public 方法。</param>
        /// <param name="pendingUpdateVersion">
        /// 有待处理更新时传入版本号，会多出一条「发现新版本」入口；null 就没有。
        /// 每次值变化就整体重建一次菜单，比往旧菜单里插条目干净。
        /// </param>
        public static MenuFlyout Build(MainWindow owner, string? pendingUpdateVersion)
        {
            var flyout = new MenuFlyout();
            flyout.MenuFlyoutPresenterStyle = BuildPresenterStyle();

            flyout.Items.Add(Item("显示主界面", () => owner.RestoreFromTray()));

            if (!string.IsNullOrEmpty(pendingUpdateVersion))
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                flyout.Items.Add(Item(
                    "发现新版本 " + pendingUpdateVersion + "（点击查看）",
                    SettingsWindow.ShowAbout));
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(Item("播放 / 暂停", () => owner.TogglePlayPausePublic()));
            flyout.Items.Add(Item("上一首", () => owner.PreviousPublic()));
            flyout.Items.Add(Item("下一首", () => owner.NextPublic()));
            flyout.Items.Add(Item("添加到我喜欢", () => owner.FavoriteCurrentPublic()));

            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(Item("打开日志文件夹", OpenLogsFolder));
            flyout.Items.Add(Item("退出播放器", () => owner.ExitFromTray()));

            return flyout;
        }

        /// <summary>
        /// 菜单项统一走 Command 而不是 Click —— 托盘菜单的显示模式（PopupMenu /
        /// SecondWindow）由库决定，PopupMenu 模式下 Click 事件根本不触发，
        /// 只有 Command 一定被执行。为了切模式不翻车，这里统一用 Command。
        /// </summary>
        private static Microsoft.UI.Xaml.Style BuildPresenterStyle()
        {
            var style = new Microsoft.UI.Xaml.Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(
                FrameworkElement.MinWidthProperty, MenuMinWidth));
            return style;
        }

        private static MenuFlyoutItem Item(string text, Action action)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                MinWidth = MenuMinWidth
            };
            item.Command = new TrayCommand(action);
            return item;
        }

        /// <summary>打开日志目录（现在是分模块多文件，给目录比给单个文件有用）。</summary>
        private static void OpenLogsFolder()
        {
            try
            {
                string dir = AppLog.LogsDirectory;
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                {
                    dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CelesteMusicPlayer");
                }
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("TrayMenuBuilder.OpenLogsFolder", caught);
            }
        }

        private sealed class TrayCommand : System.Windows.Input.ICommand
        {
            private readonly Action _action;

            public TrayCommand(Action action) => _action = action;

            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter) => _action();
        }
    }
}
