using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十二刀（2026-10-31）：列表行选中框样式宿主。
/// 从壳 <c>MainWindow.UiTheme.cs</c> / <c>MainWindow.Misc.cs</c> /
/// <c>MainWindow.Library.cs</c> / <c>MainWindow.Library2.cs</c> 搬出的「行选中款」一族：
/// 经典半透明灰底色、左侧 3px 主题色竖条、多选未选中行的毛玻璃底、系统选中勾关闭、
/// 以及专辑墙 / 文件夹浏览两条列表的行样式入口。
/// 这套东西被播放列表、专辑墙、艺术家专辑、文件夹浏览、网页桥共用，
/// 全部依赖（多选状态字段、取色入口、前景色工具）都在活文件里。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>经典界面的"选中"底色：半透明灰（深色主题用浅灰、浅色主题用灰），随明暗自动切。</summary>
    internal static Color ResolveClassicRowSelectionColor(FrameworkElement? anchor)
    {
        bool dark = anchor?.ActualTheme == ElementTheme.Dark;
        return dark
            ? Color.FromArgb(58, 255, 255, 255)
            : Color.FromArgb(40, 128, 128, 128);
    }

        // W13 第二十二刀：行选中款 + 容器圆角软化

        /// <summary>
        /// 经典 / 极客界面统一的列表行选中款：左侧 3px 主题色竖条 + 半透明灰底，文字保持原色。
        /// 极客近黑底上「整行填强调色」过于突兀（用户实测反馈），所有列表统一走这一款。
        /// 返回 true 表示已按该款处理，调用方不再走「整行强调色」分支。
        /// </summary>
        internal static bool TryApplyBarStyleRowSelection(
            Border? chrome,
            bool selected,
            Brush accent,
            Brush unselectedBg)
        {
            if (chrome == null || !IsOpaqueUiStyleActive())
            {
                return false;
            }

            bool dark = chrome.ActualTheme == ElementTheme.Dark;
            chrome.BorderThickness = new Thickness(3, 0, 0, 0);

            // 条样式底色是「半透明灰」而不是强调色，文字必须显式回落到主题前景色。
            // 注意：ListViewItem/GridViewItem 默认模板的 Selected 视觉状态是用 ObjectAnimation
            // 把 ContentPresenter.Foreground 动画成白色（动画优先级高于任何本地值，压不住），
            // 所以选中时必须给行内每个 TextBlock 显式设值（ApplyForegroundToDescendants）；
            // 未选中时 Clear 让行内文字恢复 DataTemplate 各自的主题色层次。
            var rowForeground = new SolidColorBrush(dark
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(255, 0, 0, 0));

            if (selected)
            {
                // 选中底色走统一入口（令牌皮肤=Selection.Background，经典/极客=原半透明灰）
                chrome.Background = new SolidColorBrush(ResolveClassicRowSelectionColor(chrome));
                chrome.BorderBrush = accent;
                ApplyForegroundToDescendants(chrome, rowForeground);
            }
            else
            {
                chrome.Background = unselectedBg;
                chrome.BorderBrush = new SolidColorBrush(Colors.Transparent);
                ClearForegroundOnDescendants(chrome);
            }

            return true;
        }

        /// <summary>关闭勾选标记，并尽量给 Presenter 圆角，避免系统方角选中层。</summary>
        private static void SoftenItemPresenterCorners(DependencyObject root)
        {
            // 容器自带选中层(ListView/GridViewItemPresenter)的圆角：极客界面一律直角，
            // 否则默认模板的选中高亮始终是圆角 8 —— 之前这里写死 8，导致极客下行选中框仍圆角
            //（用户实测反馈：歌曲面板及复用该面板的多个页面都有此问题）。经典/图景保持 8。
            var presenterRadius = new CornerRadius(IsGeekUiStyleActive() ? 0 : 8);

            if (root is ListViewItemPresenter listPresenter)
            {
                listPresenter.CornerRadius = presenterRadius;
                listPresenter.SelectionCheckMarkVisualEnabled = false;
                try
                {
                    listPresenter.CheckBrush = new SolidColorBrush(Colors.Transparent);
                    listPresenter.CheckHintBrush = new SolidColorBrush(Colors.Transparent);
                    listPresenter.CheckSelectingBrush = new SolidColorBrush(Colors.Transparent);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                return;
            }

            if (root is GridViewItemPresenter gridPresenter)
            {
                gridPresenter.CornerRadius = presenterRadius;
                gridPresenter.SelectionCheckMarkVisualEnabled = false;
                try
                {
                    gridPresenter.CheckBrush = new SolidColorBrush(Colors.Transparent);
                    gridPresenter.CheckHintBrush = new SolidColorBrush(Colors.Transparent);
                    gridPresenter.CheckSelectingBrush = new SolidColorBrush(Colors.Transparent);
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                SoftenItemPresenterCorners(VisualTreeHelper.GetChild(root, i));
            }
        }

        // W13 第二十二刀：多选毛玻璃底 + 关闭系统选中勾

        private Brush CreateMultiSelectFrostBrush()
        {
            // 经典界面：多选态不给未选中行铺底色。
            // 原因：原实现用 ResolveUiBaseTintColor() 取基色，浅色主题下几个候选色都"接近白"被跳过，
            // 最后落到深灰回退色(42,42,42)，于是浅色界面里所有歌曲行都变成一片深色底
            //（鼠标划过时又被悬停底色顶掉 → 就是你看到的"滑过就消失"）。
            if (FrostedGlass.ClassicMode != null)
            {
                return new SolidColorBrush(Colors.Transparent);
            }

            if (_cachedMultiSelectFrostBrush != null)
            {
                return _cachedMultiSelectFrostBrush;
            }

            // 使用半透明纯色，避免 Acrylic 采样悬停高亮后越来越白、移开也不恢复
            Color baseTint = ResolveUiBaseTintColor();
            _cachedMultiSelectFrostBrush = new SolidColorBrush(
                Color.FromArgb(70, baseTint.R, baseTint.G, baseTint.B));
            return _cachedMultiSelectFrostBrush;
        }


        // W13 第二十二刀：专辑墙卡片行样式


        // W13 第二十二刀：文件夹浏览行样式


        // W13 第二十五刀：专辑墙/艺术家专辑 行样式事件入口

        // W13 第二十五刀：文件夹浏览 行样式事件入口

    }
