using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 「并入主 UI 的二级窗口」用的一个子页：左侧一条导航项 + 右侧一块内容。
    /// 视觉与交互完全对齐音效处理那套（MainWindow.DspNav.cs）：圆点 + 标题 + 左侧强调条 + 可选徽章。
    /// 组名为空 = 置顶项，不渲染组标题（与音效处理的「听音方案 / 机架编排」同规则）。
    /// </summary>
    internal sealed class ShellSubPage
    {
        public required string Title { get; init; }
        public string Group { get; init; } = "";
        public UIElement? Page { get; init; }
        public Border? Badge { get; init; }
        public TextBlock? BadgeText { get; init; }

        // 运行时回填（Build 时创建，ReapplyAccent / Select 时使用）
        internal Button? Button;
        internal Border? Bar;
        internal TextBlock? Label;
        internal Border? Dot;
    }

    /// <summary>
    /// 通用子页宿主：把一个二级窗口拆成「左子导航 + 右内容」的板块。
    /// 2026-10-12 阶段 0 建立，供后续并入的页面（设置 / 标签编辑 / 音效处理新页）复用，
    /// 避免每个板块各造一套导航（音效处理那套是专用实现，先不动它，等新框架站稳再谈迁移）。
    ///
    /// 与 DspNav 的两点刻意差异：
    ///   ① 强调色由外部注入（accentProvider），不自己读资源 —— 见「取色只认一个入口」铁律；
    ///   ② 页面切换只做显隐，不内嵌任何业务逻辑，页面自己的定时器/重绘走 PageEnter 回调。
    /// </summary>
    internal sealed class ShellPageHost
    {
        private readonly StackPanel _navPanel;
        private readonly Func<Brush>? _accentProvider;
        private readonly List<ShellSubPage> _entries = new();
        private bool _built;
        private int _index = -1;

        /// <summary>切进某个子页时触发（参数＝被切进的页）。页面自己决定要不要起定时器、要不要补画。</summary>
        public Action<ShellSubPage>? PageEnter;

        public ShellPageHost(StackPanel navPanel, Func<Brush>? accentProvider = null)
        {
            _navPanel = navPanel;
            _accentProvider = accentProvider;
        }

        public int Count => _entries.Count;

        public ShellSubPage? Current => _index >= 0 && _index < _entries.Count ? _entries[_index] : null;

        /// <summary>登记一个子页。页面控件本身要事先放在宿主的父容器里（XAML 中），这里只接管它的显隐。</summary>
        public ShellSubPage Register(string title, string group, UIElement? page,
            Border? badge = null, TextBlock? badgeText = null)
        {
            ShellSubPage entry = new()
            {
                Title = title,
                Group = group ?? string.Empty,
                Page = page,
                Badge = badge,
                BadgeText = badgeText
            };

            // 先折叠：由 Select 决定谁可见，避免首帧所有页一起闪出来
            if (page != null)
            {
                page.Visibility = Visibility.Collapsed;
            }

            _entries.Add(entry);
            return entry;
        }

        /// <summary>按登记顺序生成导航（组标题 + 项），并默认选中第一项。</summary>
        public void Build()
        {
            if (_built)
            {
                return;
            }

            _built = true;
            _navPanel.Children.Clear();

            string? currentGroup = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                ShellSubPage entry = _entries[i];
                if (!string.IsNullOrEmpty(entry.Group) && entry.Group != currentGroup)
                {
                    currentGroup = entry.Group;
                    _navPanel.Children.Add(new TextBlock
                    {
                        Text = currentGroup,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Opacity = 0.55,
                        Margin = new Thickness(8, 10, 0, 2)
                    });
                }

                _navPanel.Children.Add(BuildNavItemGrid(entry, i));
            }

            if (_entries.Count > 0)
            {
                Select(0);
            }
        }

        /// <summary>单条导航项：38px 高的 Grid，Button 撑满，左侧叠 3px 强调条（选中时显示）。</summary>
        private Grid BuildNavItemGrid(ShellSubPage entry, int index)
        {
            Border dot = new()
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(255, 0xB4, 0xB2, 0xA9))
            };

            TextBlock label = new()
            {
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Text = entry.Title
            };

            Grid inner = new() { ColumnSpacing = 8 };
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dot.SetValue(Grid.ColumnProperty, 0);
            label.SetValue(Grid.ColumnProperty, 1);
            inner.Children.Add(dot);
            inner.Children.Add(label);

            // 徽章在最右侧（列 2）：只有传了徽章的页才有，没有就不加这一列
            if (entry.Badge != null)
            {
                inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                entry.Badge.SetValue(Grid.ColumnProperty, 2);
                inner.Children.Add(entry.Badge);
            }

            Button button = new()
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(10, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Tag = index,
                Content = inner
            };
            button.Click += (_, _) => Select(index);

            Border bar = new()
            {
                Width = 3,
                Height = 16,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(1.5),
                Visibility = Visibility.Collapsed,
                Background = AccentBrush()
            };

            Grid grid = new() { Height = 38 };
            grid.Children.Add(button);
            grid.Children.Add(bar);

            entry.Button = button;
            entry.Bar = bar;
            entry.Label = label;
            entry.Dot = dot;
            return grid;
        }

        public void Select(int index)
        {
            if (index < 0 || index >= _entries.Count)
            {
                return;
            }

            _index = index;
            for (int i = 0; i < _entries.Count; i++)
            {
                ShellSubPage entry = _entries[i];
                bool on = i == index;

                if (entry.Page != null)
                {
                    entry.Page.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                }

                if (entry.Bar != null)
                {
                    entry.Bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                }

                if (entry.Label != null)
                {
                    entry.Label.FontWeight = on
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal;
                    entry.Label.Opacity = on ? 1.0 : 0.72;
                }
            }

            PageEnter?.Invoke(_entries[index]);
        }

        public void Select(ShellSubPage? page)
        {
            if (page == null)
            {
                return;
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                if (ReferenceEquals(_entries[i], page))
                {
                    Select(i);
                    return;
                }
            }
        }

        /// <summary>主题 / 皮肤切换后重刷强调条。不刷的话会停在旧色上（极客磷光 ↔ 系统色互切尤其明显）。</summary>
        public void ReapplyAccent()
        {
            Brush brush = AccentBrush();
            foreach (ShellSubPage entry in _entries)
            {
                if (entry.Bar != null)
                {
                    entry.Bar.Background = brush;
                }
            }
        }

        /// <summary>设置某个子页的徽章文字；传 null / 空串即隐藏徽章。</summary>
        public void SetBadge(ShellSubPage page, string? text)
        {
            if (page.BadgeText != null)
            {
                page.BadgeText.Text = text ?? string.Empty;
            }

            if (page.Badge != null)
            {
                page.Badge.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private Brush AccentBrush()
        {
            if (_accentProvider != null)
            {
                try
                {
                    Brush? brush = _accentProvider();
                    if (brush != null)
                    {
                        return brush;
                    }
                }
                catch
                {
                    // 取色失败不致命：回落系统强调色，宁可颜色不对也不能让面板起不来
                }
            }

            return new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        }
    }

    /// <summary>
    /// 设置面板（阶段 0 骨架）：二级窗口「选项设置」并入主 UI 的落地容器。
    /// 现在是空壳 + 占位分类，阶段 3 才会把真实设置项逐个搬进来 —— 刻意留成能看见的过渡态，
    /// 避免"点进去一片空白"让人以为坏了。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>并入主 UI 的「选项设置」面板（阶段 3）。延迟创建：第一次点「设置」才造，不拖慢启动。</summary>
        private SettingsPanelControl? _settingsPanel;
        private bool _settingsUiBuilt;

        private void EnsureSettingsUiBuilt()
        {
            if (_settingsUiBuilt)
            {
                return;
            }

            _settingsUiBuilt = true;

            try
            {
                _settingsPanel = new SettingsPanelControl();
                SettingsPanelHost.Children.Add(_settingsPanel);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("EnsureSettingsUiBuilt", ex);
            }
        }

        /// <summary>
        /// 主题 / 皮肤切换后让设置面板跟上。面板内部是 NavigationView（走系统刷子），
        /// 不需要像音效处理那样手动重刷强调条，这里保留方法是为了不改旧调用点。
        /// 等将来把面板换成 ShellPageHost 那套时，这里再改成真的重刷。
        /// </summary>
        internal void ReapplySettingsAccent()
        {
        }

        /// <summary>并入主 UI 的「标签」板块（2026-10-13 阶段 5a）。延迟创建：第一次点「标签」才造。</summary>
        private TagPanelControl? _tagPanel;
        private bool _tagUiBuilt;

        private void EnsureTagEditUiBuilt()
        {
            if (_tagUiBuilt)
            {
                return;
            }

            _tagUiBuilt = true;

            try
            {
                // 强调色外部注入（取色只认一个入口铁律）：与音效处理共用 DspAccentBrush。
                // 宿主窗口句柄必须从 MainWindow 传（UserControl 自己取会抛 CCW cast 异常，
                // 2026-10-13 用户实测）；_mainWindowHwnd 在 ctor 里已缓存，Zero 时面板内会提示。
                _tagPanel = new TagPanelControl(() => DspAccentBrush(), _mainWindowHwnd);
                // 标签写盘后刷新曲库元数据（取代旧 TagEditorWindow.TagsSaved）
                _tagPanel.TagsSaved += OnTagsSaved;
                TagPanelHost.Children.Add(_tagPanel);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("EnsureTagEditUiBuilt", ex);
            }
        }

        /// <summary>主题 / 皮肤切换后重刷标签板块子导航的强调条。</summary>
        internal void ReapplyTagPanelAccent()
        {
            try
            {
                _tagPanel?.ReapplyAccent();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ReapplyTagPanelAccent", ex);
            }
        }

        private void NavTagEditButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "TagEdit";
                ApplyCategoryView();
            });
            NowPlayingText.Text = "标签";
        }

        /// <summary>
        /// 从任何地方跳到主界面的「标签」板块并打开指定文件：右键菜单、播放页、设置健康检查、
        /// WebUI 的 edittags 命令原来都是弹独立 TagEditorWindow（阶段 6 收尾后已删），现在统一走这里。
        /// 文件不在标签列表里的会先加进来（后台读标签），再全部选中并停在「快速编辑」子页。
        /// </summary>
        internal void OpenTagEditForFiles(IEnumerable<string>? paths)
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ExitMultiSelectMode();
                    CommitLibraryNavigation(() =>
                    {
                        _currentCategory = "TagEdit";
                        ApplyCategoryView();
                    });
                    NowPlayingText.Text = "标签";
                    EnsureTagEditUiBuilt();
                    _tagPanel?.RevealFiles(paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList());
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("OpenTagEditForFiles", ex);
            }
        }

        /// <summary>
        /// 从任何地方跳到主界面的「设置」面板：托盘菜单、桌面歌词工具栏、播放页右键菜单都走这里。
        /// 原来这些入口是弹独立设置窗口（SettingsWindow.ShowOrActivate），阶段 3 起改成切面板。
        /// </summary>
        internal void GoToSettingsPanel()
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ExitMultiSelectMode();
                    CommitLibraryNavigation(() =>
                    {
                        _currentCategory = "Settings";
                        ApplyCategoryView();
                    });
                    NowPlayingText.Text = "设置";
                });
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("GoToSettingsPanel", ex);
            }
        }

        private void NavSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "Settings";
                ApplyCategoryView();
            });
            NowPlayingText.Text = "设置";
        }
    }
}
