using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第十九刀（2026-10-31）：导航条目宿主。
/// 从壳（UiTheme/Features/Library）转活的「左侧导航条目 + 目录浏览器折叠 + 加库」域：
/// NavItemRef 条目模型与懒构建、条目显隐、文件夹折叠、文件夹加入媒体库。
/// 网页 WebDav/WebFolders/ExternalFile 借用的行为入口住这里。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// 一个左侧导航条目。指示条（Indicator）是按钮外面的独立 Border ——
    /// 画在按钮的左边框上会被 CornerRadius 裁成一段弧，用户反馈过"竖条是弧形"。
    /// </summary>
    private sealed class NavItemRef
    {
        public Button Button = null!;
        public Border? Indicator;
        public FrameworkElement? Glyph;   // 背景图式用的单色图标（FontIcon / Path）
        public FrameworkElement? Emoji;   // 经典模式用的彩色 emoji
        public TextBlock? Label;
        public bool IsTool;               // 音效处理 / 播放列表

        // 极客界面不用 Fluent 字形（它就是"塑料感"的来源）：换成等宽符号 + 磷光单色
        public string GeekSymbol = string.Empty;
        public Panel? GlyphHost;          // 图标挂在哪个容器上（符号文本插同一层）
        public TextBlock? GeekGlyph;      // 懒创建；切出极客只是隐藏，不删元素
    }

    private List<NavItemRef>? _navItems;

        // ---- 导航条目懒构建（经典/背景图式/极客三套外观共用的条目模型）----

        private List<NavItemRef> NavItems => _navItems ??= BuildNavItems();

        private List<NavItemRef> BuildNavItems()
        {
            var items = new List<NavItemRef>();
            AddNavItem(items, NavSongsButton, NavSongsIndicator, false, "♪");
            AddNavItem(items, NavAlbumsButton, NavAlbumsIndicator, false, "■");
            AddNavItem(items, NavArtistsButton, NavArtistsIndicator, false, "◉");
            AddNavItem(items, NavAlbumArtistsButton, NavAlbumArtistsIndicator, false, "◎");
            AddNavItem(items, NavFavoritesButton, NavFavoritesIndicator, false, "♥");
            AddNavItem(items, NavRatingsButton, NavRatingsIndicator, false, "★");
            AddNavItem(items, NavRecentButton, NavRecentIndicator, false, "↺");
            AddNavItem(items, NavGenreButton, NavGenreIndicator, false, "※");
            AddNavItem(items, NavYearButton, NavYearIndicator, false, "▒");
            AddNavItem(items, NavMostPlayedButton, NavMostPlayedIndicator, false, "▲");
            AddNavItem(items, NavFoldersButton, NavFoldersIndicator, false, "▤");
            // 2026-10-12 用户要求：播放列表 / 播放队列 上移到分割线上方（媒体库之下）。
            // IsTool 只作标注，动到哪组就跟着改。
            AddNavItem(items, UserPlaylistNavButton, UserPlaylistNavIndicator, false, "▦");
            AddNavItem(items, NavPlaylistWallButton, NavPlaylistWallIndicator, false, "≡");
            AddNavItem(items, NavWebDavButton, NavWebDavIndicator, false, "☁");
            // 「标签」（2026-10-13 阶段 5a）：工具区最上一格，与音效处理 / 设置同组
            AddNavItem(items, NavTagEditButton, NavTagEditIndicator, true, "✎");
            AddNavItem(items, NavAudioFxButton, NavAudioFxIndicator, true, "♫");
            // 「设置」（2026-10-12 阶段 0）：工具区最下，点进去是主区里的设置面板
            AddNavItem(items, NavSettingsButton, NavSettingsIndicator, true, "⚙");

            // 自检：条目里的「字形 / emoji / 文字」是靠遍历按钮内容抓的，抓漏了会静默退化
            // （经典界面下 emoji 不显示、图标还是老的单色字形）。这里留一行日志便于核对。
            int glyph = items.Count(i => i.Glyph != null);
            int emoji = items.Count(i => i.Emoji != null);
            int label = items.Count(i => i.Label != null);
            int bar = items.Count(i => i.Indicator != null);
            StartupLog.Write($"[导航] 条目={items.Count} 字形={glyph} emoji={emoji} 文字={label} 竖条={bar}");
            return items;
        }

        private static void AddNavItem(List<NavItemRef> items, Button? button, Border? indicator, bool isTool, string geekSymbol)
        {
            if (button == null)
            {
                return;
            }

            var item = new NavItemRef { Button = button, Indicator = indicator, IsTool = isTool, GeekSymbol = geekSymbol };

            // 竖条以自身中心缩放（"长出来"的动画用）。XAML 里不写，省得 15 个条目抄 15 遍。
            if (indicator != null)
            {
                indicator.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                indicator.RenderTransform = new ScaleTransform();
            }

            if (button.Content is DependencyObject content)
            {
                // 带父容器的遍历：极客符号要插在图标那一层，得知道图标挂在哪个 Panel 上
                var queue = new Queue<(DependencyObject Node, Panel? Parent)>();
                queue.Enqueue((content, null));

                while (queue.Count > 0)
                {
                    (DependencyObject node, Panel? parent) = queue.Dequeue();

                    if (node is TextBlock block)
                    {
                        string? marker = block.Tag as string;
                        if (marker == "NavEmoji")
                        {
                            item.Emoji ??= block;
                        }
                        else if (marker == "NavLabel")
                        {
                            item.Label ??= block;
                        }
                    }
                    else if (item.Glyph == null && node is FontIcon or Shapes.Path)
                    {
                        item.Glyph = (FrameworkElement)node;
                        item.GlyphHost = parent;
                    }

                    if (node is Panel panel)
                    {
                        foreach (UIElement child in panel.Children)
                        {
                            queue.Enqueue((child, panel));
                        }
                    }
                    else
                    {
                        int count = VisualTreeHelper.GetChildrenCount(node);
                        for (int i = 0; i < count; i++)
                        {
                            queue.Enqueue((VisualTreeHelper.GetChild(node, i), parent));
                        }
                    }
                }
            }

            items.Add(item);
        }

        /// <summary>造一个插入式的等宽符号文本（放在原图标的位置上，宽度钉死所以不错位）。</summary>
        private static TextBlock? BuildNavGeekGlyph(NavItemRef item)
        {
            Panel? host = item.GlyphHost;
            if (host == null)
            {
                return null;
            }

            var symbol = new TextBlock
            {
                Text = item.GeekSymbol,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 13,
                Width = 20,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Tag = "GeekNavSymbol",
            };

            int index = item.Glyph is UIElement glyphElement ? host.Children.IndexOf(glyphElement) : -1;
            host.Children.Insert(index >= 0 ? index : 0, symbol);
            return symbol;
        }

        // ---- 导航条目显隐（必须连外层 Grid 一起藏，避免 16px 空格）----

        /// <summary>
        /// 显示 / 隐藏一个左侧导航条目。
        /// ⚠️ 必须连外层那个 Grid 一起隐藏，不能只隐藏 Button：
        /// 每个条目是「Grid 包 [Button + 指示条 Border]」，指示条是常驻元素（Height=16、Opacity=0、Visibility=Visible），
        /// 只把 Button 设成 Collapsed 的话 Grid 还留着 16px 高 ——
        /// 用户就看到了「播放队列」和「播放最多」之间凭空多出两格空白（隐藏的流派 + 年份），还把左侧顶出滚动条。
        /// </summary>
        private static void SetNavEntryVisibility(FrameworkElement? button, bool visible)
        {
            if (button == null)
            {
                return;
            }

            Visibility v = visible ? Visibility.Visible : Visibility.Collapsed;
            button.Visibility = v;
            if (button.Parent is FrameworkElement entry)
            {
                entry.Visibility = v;
            }
        }

        // ---- 目录浏览器折叠 / 文件夹加入媒体库 ----

        private void CollapseFolderAt(int index)
        {
            int depth = _folderBrowserItems[index].Depth;
            int removeAt = index + 1;
            while (removeAt < _folderBrowserItems.Count && _folderBrowserItems[removeAt].Depth > depth)
            {
                _folderBrowserItems.RemoveAt(removeAt);
            }
        }

        /// <summary>
        /// 把一个文件夹加入音乐库（幂等）：记进 LibraryWatchFolders（已在列表里不重复存），
        /// 并立刻把该文件夹的音频加进当前列表。音乐库只按文件夹收录（2026-10-14 口径），
        /// 这条路不写 ManualLibraryFiles（那只是历史遗留单曲的保留通道，不再新增）。
        /// </summary>
        internal async System.Threading.Tasks.Task AddFolderToLibraryAsync(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            AppSettingsState settings = AppSettingsStore.Load();
            settings.LibraryWatchFolders ??= new List<string>();

            bool isNew = !settings.LibraryWatchFolders.Contains(folderPath, StringComparer.OrdinalIgnoreCase);
            if (isNew)
            {
                settings.LibraryWatchFolders.Add(folderPath);
                AppSettingsStore.Save(settings);
            }

            // 把该文件夹的音频加入主库（枚举走后台线程，避免大盘文件夹卡 UI）
            string[] paths = await System.Threading.Tasks.Task.Run(() => LibraryFolderScan.AudioFiles(folderPath).ToArray());
            LoadAndAddFiles(paths, persist: false);

            if (isNew)
            {
                StartupLog.Write("加入媒体库文件夹: " + folderPath);
            }
        }
    }
