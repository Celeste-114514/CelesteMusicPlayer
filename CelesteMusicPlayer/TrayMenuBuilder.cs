using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

        /// 「发现新版本 x.y.z（点击查看）」这条单独用的最小宽度：比通用宽度宽，
        /// 免得长版本号把「（点击查看）」挤出菜单（测量只认 MinWidth，理由见 Separator()）。
        private const double UpdateItemMinWidth = 300;

        /// <summary>
        ///  Build 一份新的右键菜单。
        /// </summary>
        /// <param name="owner">主窗口，菜单命令都转发给它的现成 public 方法。</param>
        /// <param name="pendingUpdateVersion">
        /// 有待处理更新时传入版本号，会多出一条「发现新版本」入口；null 就没有。
        /// 每次值变化就整体重建一次菜单，比往旧菜单里插条目干净。
        /// </param>
        public static MenuFlyout Build(MainWindow owner, string? pendingUpdateVersion, TrayTheme? theme = null)
        {
            TrayTheme palette = theme ?? TrayTheme.Current();
            var flyout = new MenuFlyout();
            flyout.MenuFlyoutPresenterStyle = BuildPresenterStyle(palette);

            flyout.Items.Add(Item("显示主界面", () => owner.RestoreFromTray(), palette));
            flyout.Items.Add(Item("零散播放", () => owner.ShowScatteredWindow(), palette));
            flyout.Items.Add(Item("迷你播放器", () => owner.ShowMiniPlayerWindow(), palette));

            if (!string.IsNullOrEmpty(pendingUpdateVersion))
            {
                flyout.Items.Add(Separator(palette));
                // 这条最长（版本号+中文尾巴）。冷测量只认 MinWidth（见 Separator() 注释），
                // 220px 装不下「发现新版本 26.10.3.0（点击查看）」→ 单独放宽，不然尾巴被裁。
                flyout.Items.Add(Item(
                    "发现新版本 " + pendingUpdateVersion + "（点击查看）",
                    SettingsPanelControl.ShowAbout, palette,
                    UpdateItemMinWidth));
            }

            flyout.Items.Add(Separator(palette));
            flyout.Items.Add(Item("播放 / 暂停", () => owner.TogglePlayPausePublic(), palette));
            flyout.Items.Add(Item("上一首", () => owner.PreviousPublic(), palette));
            flyout.Items.Add(Item("下一首", () => owner.NextPublic(), palette));
            flyout.Items.Add(Item("添加到我喜欢", () => owner.FavoriteCurrentPublic(), palette));

            flyout.Items.Add(Separator(palette));
            flyout.Items.Add(Item("打开日志文件夹", OpenLogsFolder, palette));
            flyout.Items.Add(Item("退出播放器", () => owner.ExitFromTray(), palette));

            return flyout;
        }

        /// <summary>
        /// 菜单分隔线。⚠ 必须显式钉 <see cref="FrameworkElement.Height"/> = 3：
        ///
        /// H.NotifyIcon 2.3.0 的 SecondWindow 模式在弹菜单前会调 MeasureFlyout
        /// 「冷测量」每一项 —— 那一刻菜单项还没套上控件模板，MenuFlyoutItem 靠库
        /// 钉死的 Height=32 能测出高度，而 separator 什么都没钉，实测 DesiredSize=(0,0)。
        /// 窗口高度就是按这个 Σ 算的：round(scale × (4 + Σ) + 4)。
        /// 但 separator 真正渲染时占 3px（WinUI 默认模板：1px 线 + 上下 Padding 各 1px）。
        /// 两条分隔线 → 测量少算 6px → 窗口比内容矮 → ScrollViewer 弹出竖向滚动条。
        /// 钉成 3px 后「测量=渲染」，滚动条消失。（上游 master 至今没修这个。）
        /// </summary>
        private static MenuFlyoutSeparator Separator(TrayTheme palette)
        {
            var separator = new MenuFlyoutSeparator { Height = 3 };
            // 分隔线走自己的两支笔：名字按 WinUI 模板里的实际键给，认不出来就退回系统色，
            // 不会像把整行设成实心那样把菜单变难看。
            separator.Resources["MenuFlyoutSeparatorBorderBrush"] = new SolidColorBrush(palette.Border);
            separator.Resources["MenuFlyoutItemSeparatorBrush"] = new SolidColorBrush(palette.Border);
            return separator;
        }

        /// <summary>
        /// 菜单项统一走 Command 而不是 Click —— 托盘菜单的显示模式（PopupMenu /
        /// SecondWindow）由库决定，PopupMenu 模式下 Click 事件根本不触发，
        /// 只有 Command 一定被执行。为了切模式不翻车，这里统一用 Command。
        /// </summary>
        private static Microsoft.UI.Xaml.Style BuildPresenterStyle(TrayTheme palette)
        {
            var style = new Microsoft.UI.Xaml.Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(
                FrameworkElement.MinWidthProperty, MenuMinWidth));

            // SecondWindow 模式的菜单跑在自己的 XAML 树里，拿不到主窗口的主题，
            // 所以深浅、底色、字都必须在这儿显式钉死，各项资源也要铺到 presenter 的作用域上
            // （MenuFlyoutItem 会沿元素树向上查，正好查到 presenter）。
            var transparent = new SolidColorBrush(Colors.Transparent);
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(FrameworkElement.RequestedThemeProperty,
                palette.Dark ? ElementTheme.Dark : ElementTheme.Light));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(Control.BackgroundProperty, new SolidColorBrush(palette.Panel)));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(Control.BorderBrushProperty, new SolidColorBrush(palette.Border)));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(Control.ForegroundProperty, new SolidColorBrush(palette.Foreground)));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(Control.CornerRadiusProperty, palette.Radius));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(Control.FontFamilyProperty, palette.Font));
            style.Setters.Add(new Microsoft.UI.Xaml.Setter(Control.FontSizeProperty, palette.FontSize));

            return style;
        }

        /// <summary>
        /// 菜单项的悬停/按下这些状态色：铺在**菜单项自己的**资源字典里。
        ///
        /// ⚠ 别铺到 presenter 上：WinUI3 的 <see cref="Microsoft.UI.Xaml.Style"/> 压根没有
        /// Resources 属性（那是 WPF 的），编译都过不了。而控件模板里的资源是相对「被模板化的
        /// 那个控件本身」查的，所以直接给每个菜单项一份字典最准，也不会污染全局。
        /// </summary>
        private static void ApplyItemStateColors(FrameworkElement item, TrayTheme palette)
        {
            item.Resources["MenuFlyoutItemBackground"] = new SolidColorBrush(Colors.Transparent);
            item.Resources["MenuFlyoutItemBackgroundPointerOver"] = new SolidColorBrush(palette.Hover);
            item.Resources["MenuFlyoutItemBackgroundPressed"] = new SolidColorBrush(palette.Pressed);
            item.Resources["MenuFlyoutItemForeground"] = new SolidColorBrush(palette.Foreground);
            item.Resources["MenuFlyoutItemForegroundPointerOver"] = new SolidColorBrush(palette.Foreground);
            item.Resources["MenuFlyoutItemForegroundPressed"] = new SolidColorBrush(palette.Accent);
            item.Resources["MenuFlyoutItemForegroundDisabled"] = new SolidColorBrush(palette.Muted);
            item.Resources["MenuFlyoutItemKeyboardAcceleratorTextForeground"] = new SolidColorBrush(palette.Muted);
        }

        private static MenuFlyoutItem Item(string text, Action action, TrayTheme palette, double minWidth = MenuMinWidth)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                MinWidth = minWidth
            };
            ApplyItemStateColors(item, palette);
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
