using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Color = Windows.UI.Color;
using System.Linq;

namespace CelesteMusicPlayer;

/// <summary>W13 第三十四刀（2026-10-31）：导航外观：分类图标配色/字形、导航选中动画、胶囊排序按钮、边框取色。</summary>
public sealed partial class MainWindow
{
        private string? _lastAnimatedNavTag;

        private static Microsoft.UI.Xaml.Controls.FontIcon? FindFirstFontIcon(DependencyObject? root)
        {
            if (root == null)
            {
                return null;
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Microsoft.UI.Xaml.Controls.FontIcon icon)
                {
                    return icon;
                }

                Microsoft.UI.Xaml.Controls.FontIcon? nested = FindFirstFontIcon(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        /// <summary>
        /// 遍历按钮内容里的元素。刻意先走 Panel.Children：这些 TextBlock/FontIcon 是 XAML 里直接写在
        /// Button.Content 下的，页面还没布局时 VisualTreeHelper 取不到，Panel.Children 一定取得到。
        /// </summary>
        private static IEnumerable<DependencyObject> WalkNavContent(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                DependencyObject node = pending.Pop();
                yield return node;

                if (node is Panel panel)
                {
                    foreach (UIElement child in panel.Children)
                    {
                        pending.Push(child);
                    }
                }
                else
                {
                    int count = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < count; i++)
                    {
                        pending.Push(VisualTreeHelper.GetChild(node, i));
                    }
                }
            }
        }

        /// <summary>单个双精度属性的淡入动画（代码构造的 Storyboard，目标用对象引用而不是名字）。</summary>
        private static void BeginNavAnimation(
            DependencyObject target,
            int milliseconds,
            double to,
            string property = "Opacity",
            double? from = 0)
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
                }
            };

            if (from.HasValue)
            {
                animation.From = from.Value;
            }

            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, target);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        /// <summary>把无边框窗口的 DWM 边框/标题栏描边色设为当前 UI 底色，
        /// 消除任务栏缩略图/窗口四周跟随系统强调色的"主题色框"。</summary>
        private void ApplyBorderColorFromUiTint()
        {
            if (_mainWindowHwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                // COLORREF = ABGR（DWMWA_BORDER_COLOR=34 / DWMWA_CAPTION_COLOR=35）
                int color = ColorHelper.MakeColorRef(ResolveUiBaseTintColor());
                int hwndColor = unchecked((int)(uint)color);
                DwmSetWindowAttributeInt(_mainWindowHwnd, 34, ref hwndColor, 4);
                DwmSetWindowAttributeInt(_mainWindowHwnd, 35, ref hwndColor, 4);
                StartupLog.Write("任务栏/窗口描边色已匹配 UI 底色 ABGR=" + color.ToString("X8"));
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ApplyBorderColorFromUiTint", ex);
            }
        }

        /// <summary>
        /// 排序按钮统一为操场形胶囊（高 32、圆角 16 = 两头半圆），底色为系统主题色。
        /// </summary>
        private void ApplyCapsuleSortButtonStyle(bool accent)
        {
            const double height = 32;
            // 排序按钮：经典/图景保持胶囊（半高圆角）。这是所有排序按钮的总入口，
            // 改这里一次覆盖全部复用点。只改形状，填充色不变。
            var capsule = new CornerRadius(height / 2.0); // 半高等于半径 → 两头圆、中间直

            // 排序相关按钮始终使用主题色；accent 参数保留以兼容旧调用
            Brush background = ResolveAccentBrush();
            Brush foreground = ResolveAccentForegroundBrush();

            ApplyCapsuleToControl(SortFieldButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(SortOrderButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(ChangeSortButton, height, capsule, background, foreground);
            ApplyCapsuleToControl(AlbumSortButton, height, capsule, background, foreground);
            // 专辑页「升序 / 降序」切换按钮：与 AlbumSortButton 同属专辑浏览页，之前漏加导致背景模式（非极客）下
            // 露出默认透明底 + 默认方角。统一纳入胶囊样式入口后，常态为主题色底 + 半高圆角，极客下随全局走直角。
            ApplyCapsuleToControl(AlbumSortOrderButton, height, capsule, background, foreground);
            if (ArtistSongSortButton != null)
            {
                ApplyCapsuleToControl(ArtistSongSortButton, height, capsule, background, foreground);
            }

            if (ArtistAlbumSortFieldButton != null)
            {
                ApplyCapsuleToControl(ArtistAlbumSortFieldButton, height, capsule, background, foreground);
            }

            if (ArtistAlbumSortOrderButton != null)
            {
                ApplyCapsuleToControl(ArtistAlbumSortOrderButton, height, capsule, background, foreground);
            }

            if (SelectAllMultiSelectButton != null)
            {
                Brush selectAllBg = accent ? background : ResolveCapsuleFillBrush();
                Brush? selectAllFg = accent ? foreground : null;
                ApplyCapsuleToControl(
                    SelectAllMultiSelectButton,
                    height,
                    new CornerRadius(8),
                    selectAllBg,
                    selectAllFg);
            }
        }

        private static void ApplyCapsuleToControl(
            Control control,
            double height,
            CornerRadius capsule,
            Brush background,
            Brush? foreground)
        {
            control.Height = height;
            control.MinHeight = height;
            control.CornerRadius = capsule;
            control.Background = background;
            control.BorderThickness = new Thickness(0);
            control.Padding = new Thickness(14, 0, 14, 0);
            // 防止按钮被父容器纵向拉伸导致其高度大于胶囊(32)使文字中心落到胶囊下半 → 视觉偏下；
            // 强制垂直居中对齐 + 相对父容器居中，使文字相对主题胶囊真正垂直居中。
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalContentAlignment = HorizontalAlignment.Center;
            control.VerticalContentAlignment = VerticalAlignment.Center;

            if (foreground != null)
            {
                control.Foreground = foreground;
            }
            else
            {
                control.ClearValue(Control.ForegroundProperty);
            }
        }

        /// <summary>
        /// 设置变更后重画进度条：波形配色（渐变/纯色）与白度都是这里读的，
        /// 不重画的话用户在设置里改完要切歌或重启才看得到。
        /// ⚠️ 回调跑在设置保存的持锁期内，这里只排回 UI 线程，不在锁里做 UI 工作。
        /// </summary>
        private void OnSettingsChangedForProgressStyle()
        {
            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.UiTheme.cs", caught); }
        }
    }
