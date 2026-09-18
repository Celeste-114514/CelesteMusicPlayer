using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    /// 极客界面（设置里的「界面风格 = 极客」）对**右侧浏览区**的重塑。
    ///
    /// 之前极客化只做到"换底色 + 换等宽字"，浏览页看起来仍是经典页面的变形
    /// （用户实测反馈：胶囊按钮、圆角卡片、圆角封面、药丸格式标签一个没少）。
    /// 这里做的是真正的重新设计，规则取自终端 / 监控屏的视觉语言：
    ///   1. 一律直角 —— 按钮、输入框、卡片、封面、选中块全部圆角归零；
    ///   2. 一律细线 —— 1px 暗绿灰描边替代原来的填充色块 / 无边框；
    ///   3. 一律等宽 —— 字体已由 ApplyGeekChrome 统一切成 Consolas；
    ///   4. 数据表化 —— 行与行之间加 1px 表格线，行内元数据变成暗灰纯文本（见 ApplyGeekRowDetailChrome）；
    ///   5. 命令行化 —— 搜索框占位文字加 "> " 提示符前缀。
    ///
    /// 关键点：**所有被改过的属性都按「元素 + 依赖属性」记了原值**（含"原本就没设过"这一状态），
    /// 关掉极客时逐个还原，元素级资源覆盖那一套（切换时容易漏）在这里不参与。
    /// 不碰左侧分类栏、不碰顶栏、不碰 Application.Resources（运行时改全局系统键会崩 0xc000027b）。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>极客底色体系：与 FrostedGlass 的 "Geek" 分支同源（近黑微绿）。</summary>
        private static readonly Color GeekPanelFill = Color.FromArgb(255, 0x12, 0x17, 0x12);
        private static readonly Color GeekHairline = Color.FromArgb(255, 0x2A, 0x33, 0x2A);

        /// <summary>极客期间被改写过的元素样式：元素 → [(依赖属性, 原局部值)]。UnsetValue 表示原本没设过（还原时 ClearValue）。</summary>
        private readonly Dictionary<FrameworkElement, List<KeyValuePair<DependencyProperty, object?>>> _geekBrowseBackup = new();

        /// <summary>
        /// 记一次改写。同一个「元素 + 属性」只记第一次的原值（后续重复调用不会把极客值当成原值存进去）。
        /// 已经等于目标值的调用直接跳过，避免每次刷新都写一遍依赖属性。
        /// </summary>
        private void GeekStore(FrameworkElement element, DependencyProperty property, object? value)
        {
            if (!_geekBrowseBackup.TryGetValue(element, out var list))
            {
                list = new List<KeyValuePair<DependencyProperty, object?>>();
                _geekBrowseBackup[element] = list;
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

        /// <summary>按备份还原一棵子树（只还原被改过的元素，没动过的一律不碰）。
        /// 顺带摘掉行表格线（GeekRule 是极客期间插进行模板里的额外元素，还原必须删干净）。</summary>
        private void GeekRestoreSubtree(DependencyObject node)
        {
            if (node is Panel host)
            {
                for (int i = host.Children.Count - 1; i >= 0; i--)
                {
                    if (host.Children[i] is FrameworkElement childElement && childElement.Tag as string == "GeekRule")
                    {
                        host.Children.RemoveAt(i);
                    }
                }
            }

            if (node is FrameworkElement element && _geekBrowseBackup.TryGetValue(element, out var list))
            {
                foreach (var pair in list)
                {
                    if (pair.Value == DependencyProperty.UnsetValue)
                    {
                        element.ClearValue(pair.Key);
                    }
                    else
                    {
                        element.SetValue(pair.Key, pair.Value);
                    }
                }

                _geekBrowseBackup.Remove(element);
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                GeekRestoreSubtree(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
            }
        }

        /// <summary>
        /// 极客浏览区重塑总入口：geek=true 走重塑，false 走还原。
        /// 由 ApplyGeekChrome 在界面风格切换时调用一次；列表行内的细节（封面 / 格式标签 / 表格线）
        /// 由行样式入口 ApplyGeekRowDetailChrome 逐行补，虚拟化晚实现的行也不会漏。
        /// </summary>
        internal void ApplyGeekBrowseSkin(bool geek)
        {
            try
            {
                if (MainContentGrid == null)
                {
                    return;
                }

                if (geek)
                {
                    GeekSweepBrowse(MainContentGrid);
                }
                else
                {
                    GeekRestoreSubtree(MainContentGrid);
                }

                ApplyLibrarySearchPrompt();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.ApplyGeekBrowseSkin", caught);
            }
        }

        /// <summary>命令行化的搜索框占位文字：极客下加 "&gt; " 提示符，非极客显示基准文案。
        /// 基准（经典）文案由分类切换时经 SetLibrarySearchBasePrompt 更新，避免切换分类把前缀冲掉。</summary>
        internal void ApplyLibrarySearchPrompt()
        {
            if (LibrarySearchBox == null)
            {
                return;
            }

            bool geek = IsGeekUiStyleActive();
            // 首次进入且还没记录基准文案时，以当前占位文字作为经典基准。
            _geekSearchPlaceholderClassic ??= LibrarySearchBox.PlaceholderText;
            string baseText = string.IsNullOrEmpty(_geekSearchPlaceholderClassic) ? "搜索" : _geekSearchPlaceholderClassic!;
            GeekStore(LibrarySearchBox, TextBox.PlaceholderTextProperty, geek ? "> " + baseText : baseText);
        }

        /// <summary>分类切换时设置搜索框占位文字的“经典基准文案”，并即时反映极客前缀。</summary>
        internal void SetLibrarySearchBasePrompt(string plainText)
        {
            _geekSearchPlaceholderClassic = plainText;
            ApplyLibrarySearchPrompt();
        }

        private string? _geekSearchPlaceholderClassic;

        /// <summary>
        /// 递归重塑：只改「圆角 / 描边 / 填充」三类属性，不做布局改动，所以不会影响任何点击与滚动逻辑。
        /// </summary>
        private void GeekSweepBrowse(DependencyObject node)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);

            if (node is FrameworkElement element && !IsGeekSweepSkipped(element))
            {
                GeekSweepElement(element);
            }

            for (int i = 0; i < count; i++)
            {
                GeekSweepBrowse(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
            }
        }

        /// <summary>圆形元素（头像 / 播放键底衬一类）方化会很难看，整体跳过。</summary>
        private static bool IsGeekSweepSkipped(FrameworkElement element)
        {
            if (element is Border round
                && round.Width > 0
                && Math.Abs(round.Width - round.Height) < 0.5
                && round.CornerRadius.TopLeft >= round.Width / 2 - 0.5)
            {
                return true;
            }

            return element is Shape;
        }

        private void GeekSweepElement(FrameworkElement element)
        {
            var hairline = new SolidColorBrush(GeekHairline);

            switch (element)
            {
                case Border border:
                    // 卡片 / 面板：直角 + 原本就画了边线的话，边线统一成极客细线
                    if (border.CornerRadius != new CornerRadius(0))
                    {
                        GeekStore(border, Border.CornerRadiusProperty, new CornerRadius(0));
                    }

                    if (border.BorderThickness != new Thickness(0))
                    {
                        GeekStore(border, Border.BorderBrushProperty, hairline);
                    }

                    break;

                case TextBox textBox:
                    // 输入框：命令行方块 —— 直角 + 1px 细线 + 近黑填充
                    GeekStore(textBox, Control.CornerRadiusProperty, new CornerRadius(0));
                    GeekStore(textBox, Control.BorderThicknessProperty, new Thickness(1));
                    GeekStore(textBox, Control.BorderBrushProperty, hairline);
                    GeekStore(textBox, Control.BackgroundProperty, new SolidColorBrush(GeekPanelFill));
                    break;

                case ButtonBase button:
                    // 按钮：直角。描边只在模板本来就画了边线时才有意义（厚度不改），
                    // 播放控制那排纯图标键不该被框起来。
                    GeekStore(button, Control.CornerRadiusProperty, new CornerRadius(0));
                    GeekStore(button, Control.BorderBrushProperty, hairline);
                    break;

                case Control control:
                    if (control.CornerRadius != new CornerRadius(0))
                    {
                        GeekStore(control, Control.CornerRadiusProperty, new CornerRadius(0));
                    }

                    break;
            }
        }
    }
}
