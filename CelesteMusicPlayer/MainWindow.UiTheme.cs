using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Shapes = Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
// TagLibSharp：包名 TagLibSharp，命名空间 TagLib
using TagLib;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Color = Windows.UI.Color;


namespace CelesteMusicPlayer
{
    public sealed partial class MainWindow
    {
        // （2026-10-31 第十八刀：缩略图按钮泵三件已随 NativeTaskbar.cs 域整体转活）


        // 极客界面已移除：原 UiStyleGeek 常量与 IsGeekUiStyleActive 保留在 Utilities.cs，恒为 false。
       
        /// <summary>
        /// 最近一次 ApplyUiStyleMode 应用过的模式。设置页保存时对照它：
        /// 经典家族内互切（Classic*/Token）走热切换不弹重启窗；涉极客/图景才提示重启。
        /// </summary>

        // =====================================================================
        // 极客界面模式已于 2026-10-31 整体移除（用户拍板：新外壳不做极客皮肤）。
        // 原实现（等宽字体 / 直角 / 顶栏读数 / 磷光色 / 终端表格）整块删除，
        // 老存档的 "Geek" 由 AppSettingsStore 归一到 ClassicSystem。
        // =====================================================================

        /// <summary>行选中款与容器圆角软化已整体搬到 MainWindow.SelectionChromeHost.cs（W13 第二十二刀）。</summary>

        /// <summary>经典界面的"选中"底色：半透明灰（深色主题用浅灰、浅色主题用灰），随明暗自动切。</summary>
        internal static Brush ResolveClassicRowSelectionBrush(FrameworkElement? anchor)
            => new SolidColorBrush(ResolveClassicRowSelectionColor(anchor));

        /// <summary>行选中款与容器圆角软化已整体搬到 MainWindow.SelectionChromeHost.cs（W13 第二十二刀）。</summary>


        // =====================================================================
        // 左侧导航条目：图标（经典 = 彩色 emoji / 背景图式 = 原单色字形）、
        // 选中高亮（圆角灰底 + 左侧直竖条，切换带淡入动画）、收起时只留图标
        // =====================================================================

        // （2026-10-31 第十九刀：NavItemRef 条目模型 + NavItems 懒构建已转活 MainWindow.NavHost.cs）

        // 注：原 SetLibraryNavHiddenForNowPlaying（播放页展开时隐藏左侧音乐库面板）已于
        // 2026-10-16 按用户要求删除——播放页覆盖层收窄为只盖右列（Grid.Column="1"），
        // 左栏常显，展开/收起不再需要切换它的 Visibility。


        // 左侧音乐库面板的收起/展开功能已按用户要求移除（面板固定展开）。

        /// <summary>波形未就绪时的渐变兜底(仅波形模式内部使用)。</summary>
        private void DrawGradientFallback(Canvas canvas, double w, double h, double ratio, Color accent)
        {
            var track = new Shapes.Rectangle
            {
                Width = w,
                Height = 4,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromArgb(48, accent.R, accent.G, accent.B))
            };
            Canvas.SetTop(track, (h - 4) / 2);
            canvas.Children.Add(track);

            if (ratio > 0.01)
            {
                var fill = new Shapes.Rectangle { Width = Math.Max(2, w * ratio), Height = 4, RadiusX = 2, RadiusY = 2 };
                var grad = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0.5),
                    EndPoint = new Windows.Foundation.Point(1, 0.5)
                };
                grad.GradientStops.Add(new GradientStop { Color = accent, Offset = 0 });
                grad.GradientStops.Add(new GradientStop { Color = Lighten(accent, 0.55), Offset = 1 });
                fill.Fill = grad;
                Canvas.SetTop(fill, (h - 4) / 2);
                canvas.Children.Add(fill);
            }
        }




        /// <summary>自定义背景图的请求序号：连续换图时丢弃过期结果，避免旧图盖掉新图。</summary>

        /// <summary>应用自定义背景图片(设置里选择)；与封面背景互斥——自定义优先。
        /// 无路径时恢复封面背景（用最近缓存的当前曲目封面）。
        /// 设置里开了「背景高斯模糊」时，自定义图片同样先做模糊再铺满窗口。</summary>
        /// <summary>当前视频背景的文件路径。用于「同一路径不重复换源」的判重（2026-10-07 内存修复）。</summary>


        // =====================================================================
        // 预设背景的「缓慢移动」：用极轻微的缩放 + 平移让静态图活起来，
        // 用来替代体积大、还可能踩版权的视频背景。由合成线程驱动，几乎不吃 CPU。
        // =====================================================================



        private static Color WaveColorFor(int index) => _waveAccentColor;

    }
}
