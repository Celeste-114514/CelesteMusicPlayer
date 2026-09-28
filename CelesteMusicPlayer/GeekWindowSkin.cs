using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 弹窗跟随极客皮肤：所有二级窗口（设置页 / 迷你播放器 / 均衡器 / 标签编辑 /
    /// 播放队列 / 颜色选择器等）跟着主界面的「界面风格 = 极客」一起变成终端风。
    ///
    /// 与主窗口的分工：窗口底色、明暗主题、标题栏配色已由 FrostedGlass 在
    /// ClassicMode="Geek" 时统一处理（近黑根面板 + 深色 RequestedTheme + 深色标题栏按钮），
    /// 这里只补主窗口极客外观的其余三样：
    ///   1. 等宽字体（Consolas；图标字形不换，换会缺字成问号）；
    ///   2. 圆角归零（Border / Button / Control 全部直角，元素级资源覆盖 + 已建树手动刷）；
    ///   3. 1px 暗绿细线 + 输入框近黑填充（与主窗口 GeekBrowse 同一套色）。
    ///
    /// 所有改写都按「元素 + 依赖属性」记了原值（含"原本就没设过"），退出极客逐个还原；
    /// 窗口关闭时整本账一起丢。主窗口不归这里管（它有自己的 ApplyGeekChrome 全套）。
    /// </summary>
    internal static class GeekWindowSkin
    {
        /// <summary>极客底色体系：与主窗口 GeekBrowse 同源。</summary>
        private static readonly Color GeekPanelFill = Color.FromArgb(255, 0x12, 0x17, 0x12);
        private static readonly Color GeekHairline = Color.FromArgb(255, 0x2A, 0x33, 0x2A);

        /// <summary>每个窗口一本还原账：元素 → [(依赖属性, 原局部值)]。UnsetValue 表示原本没设过（还原时 ClearValue）。</summary>
        private static readonly Dictionary<Window, Dictionary<DependencyObject, List<KeyValuePair<DependencyProperty, object?>>>> Backups = new();

        /// <summary>当前处于极客皮肤下的窗口（界面风格切换时逐个刷新）。</summary>
        private static readonly List<Window> SkinnedWindows = new();

        /// <summary>
        /// 给二级窗口套/摘极客皮肤。重复调用是幂等的（已等于目标值的属性直接跳过）。
        /// 通常在窗口构造里随 FrostedGlass.ApplyWindowTheme 一起调用；主窗口请勿调用。
        /// </summary>
        public static void Apply(Window? window, bool geek)
        {
            if (window is MainWindow or null)
            {
                return; // 主窗口走自己的 ApplyGeekChrome，避免两套账本互相污染
            }

            if (window.Content is not FrameworkElement root)
            {
                return;
            }

            try
            {
                if (geek)
                {
                    if (!Backups.ContainsKey(window))
                    {
                        Backups[window] = new Dictionary<DependencyObject, List<KeyValuePair<DependencyProperty, object?>>>();
                        window.Closed += OnWindowClosed;
                    }

                    if (!SkinnedWindows.Contains(window))
                    {
                        SkinnedWindows.Add(window);
                    }

                    // 元素级资源覆盖（不是全局资源）：之后新建的控件/文本走等宽、圆角归零
                    var mono = new FontFamily("Consolas");
                    root.Resources["ContentControlThemeFontFamily"] = mono;
                    root.Resources["ControlCornerRadius"] = new CornerRadius(0);
                    root.Resources["OverlayCornerRadius"] = new CornerRadius(0);

                    // 资源只影响之后新建的元素，已在树上的这一批得手动刷一遍
                    Sweep(root, window);
                }
                else
                {
                    root.Resources.Remove("ContentControlThemeFontFamily");
                    root.Resources.Remove("ControlCornerRadius");
                    root.Resources.Remove("OverlayCornerRadius");

                    if (Backups.TryGetValue(window, out var backup))
                    {
                        Restore(root, backup);
                        Backups.Remove(window);
                        SkinnedWindows.Remove(window);
                        window.Closed -= OnWindowClosed;
                    }
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("GeekWindowSkin.Apply", caught);
            }
        }

        /// <summary>界面风格切换时刷新所有开着的二级窗口（geek=false 逐个还原）。</summary>
        public static void RefreshAllOpen(bool geek)
        {
            Window[] snapshot;
            lock (SkinnedWindows)
            {
                snapshot = SkinnedWindows.ToArray();
            }

            foreach (Window window in snapshot)
            {
                if (window?.Content == null)
                {
                    continue; // 已关但还没收到 Closed 事件的，跳过
                }

                Apply(window, geek);
            }
        }

        private static void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (sender is not Window window)
            {
                return;
            }

            window.Closed -= OnWindowClosed;
            Backups.Remove(window);
            lock (SkinnedWindows)
            {
                SkinnedWindows.Remove(window);
            }
        }

        /// <summary>记一次改写。同一个「元素 + 属性」只记第一次的原值；已等于目标值直接跳过。</summary>
        private static void Store(
            Window window,
            DependencyObject element,
            DependencyProperty property,
            object? value)
        {
            var backup = Backups[window];
            if (!backup.TryGetValue(element, out var list))
            {
                list = new List<KeyValuePair<DependencyProperty, object?>>();
                backup[element] = list;
            }

            foreach (var pair in list)
            {
                if (pair.Key == property)
                {
                    if (Equals(element.GetValue(property), value))
                    {
                        return;
                    }

                    element.SetValue(property, value);
                    return;
                }
            }

            list.Add(new KeyValuePair<DependencyProperty, object?>(property, element.ReadLocalValue(property)));
            element.SetValue(property, value);
        }

        /// <summary>按备份还原一棵子树（只还原被改过的元素）。</summary>
        private static void Restore(DependencyObject node, Dictionary<DependencyObject, List<KeyValuePair<DependencyProperty, object?>>> backup)
        {
            if (backup.TryGetValue(node, out var list))
            {
                foreach (var pair in list)
                {
                    if (pair.Value == DependencyProperty.UnsetValue)
                    {
                        node.ClearValue(pair.Key);
                    }
                    else
                    {
                        node.SetValue(pair.Key, pair.Value);
                    }
                }

                backup.Remove(node);
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                Restore(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), backup);
            }
        }

        /// <summary>递归重塑：等宽字体 + 圆角归零 + 细线 + 输入框近黑填充。</summary>
        private static void Sweep(DependencyObject node, Window window)
        {
            // 图标字形用的是字体里的特殊符号位（PUA 区），Consolas 里没有这些字符 →
            // 渲染成缺字的问号框。图标一律不换字体（与主窗口 ApplyFontToTree 同规则）。
            if (node is FontIcon or SymbolIcon)
            {
                return;
            }

            if (node is FrameworkElement element)
            {
                // 圆形元素（头像 / 圆形播放键底衬一类）方化会很难看，整体跳过。
                bool skip = element is Border round
                    && round.Width > 0
                    && Math.Abs(round.Width - round.Height) < 0.5
                    && round.CornerRadius.TopLeft >= round.Width / 2 - 0.5;
                if (!skip && element is Shape)
                {
                    skip = true;
                }

                if (!skip)
                {
                    SweepElement(window, element);
                }
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                Sweep(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), window);
            }
        }

        private static void SweepElement(Window window, FrameworkElement element)
        {
            var mono = new FontFamily("Consolas");
            var hairline = new SolidColorBrush(GeekHairline);

            switch (element)
            {
                case Border border:
                    // 卡片 / 面板：直角 + 原本就画了边线的话，边线统一成极客细线
                    if (border.CornerRadius != new CornerRadius(0))
                    {
                        Store(window, border, Border.CornerRadiusProperty, new CornerRadius(0));
                    }

                    if (border.BorderThickness != new Thickness(0))
                    {
                        Store(window, border, Border.BorderBrushProperty, hairline);
                    }

                    break;

                case TextBox textBox:
                    // 输入框：命令行方块 —— 直角 + 1px 细线 + 近黑填充
                    Store(window, textBox, Control.CornerRadiusProperty, new CornerRadius(0));
                    Store(window, textBox, Control.BorderThicknessProperty, new Thickness(1));
                    Store(window, textBox, Control.BorderBrushProperty, hairline);
                    Store(window, textBox, Control.BackgroundProperty, new SolidColorBrush(GeekPanelFill));
                    Store(window, textBox, Control.FontFamilyProperty, mono);
                    break;

                case ButtonBase button:
                    // 按钮：直角。描边只在模板本来就画了边线时才有意义（厚度不改）。
                    Store(window, button, Control.CornerRadiusProperty, new CornerRadius(0));
                    if (!SubtreeHasIcon(button))
                    {
                        Store(window, button, Control.FontFamilyProperty, mono);
                    }

                    Store(window, button, Control.BorderBrushProperty, hairline);
                    break;

                case Control control:
                    if (control.CornerRadius != new CornerRadius(0))
                    {
                        Store(window, control, Control.CornerRadiusProperty, new CornerRadius(0));
                    }

                    if (!SubtreeHasIcon(control))
                    {
                        Store(window, control, Control.FontFamilyProperty, mono);
                    }

                    break;

                case TextBlock textBlock:
                    Store(window, textBlock, TextBlock.FontFamilyProperty, mono);
                    break;
            }
        }

        /// <summary>子树里是否有图标控件（有就不换这个 Control 的字体，避免图标缺字）。</summary>
        private static bool SubtreeHasIcon(DependencyObject node)
        {
            if (node is FontIcon or SymbolIcon)
            {
                return true;
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                if (SubtreeHasIcon(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i)))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
