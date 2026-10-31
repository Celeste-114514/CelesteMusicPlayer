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
using Microsoft.UI.Xaml.Media.Animation; // DoubleAnimation / CubicEase / Storyboard（左栏展开收起动画）
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

        /// <summary>当前设置是否处于 HiFi 独占输出模式（WASAPI 独占 / ASIO）。</summary>
        /// <summary>是否为 DSD 文件（DSF/DFF）。</summary>
        private static bool IsDsdFile(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext is ".dsf" or ".dff";
        }


        /// <summary>返回 DSD 容器扩展名大写（"DSF"/"DFF"），非 DSD 返回 null。</summary>
        private static string? DsdExtOf(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext switch { ".dsf" => "DSF", ".dff" => "DFF", _ => null };
        }


        // 2026-10-31：IsHiFiModeSelected / IsHiFiSoftVolumeUiUnlocked 已搬到 MainWindow.Utilities.cs
        // （纯状态查询，18 + 10 处调用点全在活代码里，定义留在壳里等于壳被拴住）。


        /// <summary>对主窗口进行窗口过程子类化，拦截 WM_GETMINMAXINFO 设置最小尺寸。</summary>
        private void SetupMinSizeHooks()
        {
            try
            {
                if (_minMaxWndProc != null || _mainWindowHwnd == IntPtr.Zero)
                {
                    return;
                }

                uint dpi = GetDpiForWindow(_mainWindowHwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                _minTrackScale = dpi / 96.0;
                _minMaxWndProc = MainMinMaxWndProc;
                _prevWndProc = SetWindowLongPtr64(_mainWindowHwnd, (int)GWL_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_minMaxWndProc));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private static nint MainMinMaxWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
            {
                try
                {
                    MINMAXINFO mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    mmi.ptMinTrackSize.X = (int)Math.Round(MinWindowWidthDip * _minTrackScale);
                    mmi.ptMinTrackSize.Y = (int)Math.Round(MinWindowHeightDip * _minTrackScale);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
                    return IntPtr.Zero;
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            }

            // 系统 resize 边框（hasBorder）已提供四边/四角调大小；这里只需同步最大化/还原状态
            // （自绘最大化按钮图标，含从最大化拖拽还原场景）。
            if (msg == WM_SIZE && MainWindow.Instance is MainWindow mw)
            {
                int sizeType = (int)(wParam.ToInt64() & 0xFFFF);
                mw.OnWindowMaximizeStateChanged(sizeType == SIZE_MAXIMIZED);
            }

            return CallWindowProcW(_prevWndProc, hWnd, msg, wParam, lParam);
        }


        /// <summary>延后设置 Desktop Acrylic（壁纸色毛玻璃）；失败则回退 Mica / 纯色。</summary>
        private void TryApplySystemBackdrop()
        {
            try
            {
                AppSettingsState settings = AppSettingsStore.Load();
                FrostedGlass.BackdropEnabled = settings.EnableFrostedGlass;
                if (!settings.EnableFrostedGlass)
                {
                    SystemBackdrop = null;
                    StartupLog.Write("SystemBackdrop disabled by settings");
                    return;
                }

                FrostedGlass.ApplyWindowBackdrop(this);
                StartupLog.Write(
                    SystemBackdrop is DesktopAcrylicBackdrop
                        ? "DesktopAcrylicBackdrop applied"
                        : SystemBackdrop is MicaBackdrop
                            ? "MicaBackdrop fallback applied"
                            : "SystemBackdrop cleared");
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ApplyWindowBackdrop", ex);
            }
        }


        // =====================================================================
        // 打开文件 / 选择文件夹
        // =====================================================================


        /// <summary>
        /// 「打开文件」菜单项（Playback.cs:943 动态挂到 fileItem.Click）。
        /// 2026-10-31 曾随纯工具函数一起搬到 MainWindow.Utilities.cs，但它的落点是
        /// LoadAndAddFiles（播放域，仍在本文件），搬走会给工具文件引入壳依赖，已退回。
        /// </summary>
        private async void OpenFileButton_Click(dynamic sender, dynamic e)
        {
            try
            {
                FileOpenPicker picker = new();
                nint hwnd = NativeHandle;
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".m4a");
                picker.FileTypeFilter.Add(".flac");
                picker.FileTypeFilter.Add(".wma");
                picker.FileTypeFilter.Add(".ogg");
                picker.FileTypeFilter.Add(".aac");
                picker.FileTypeFilter.Add(".ape");
                picker.FileTypeFilter.Add(".wv");
                picker.FileTypeFilter.Add(".tta");
                picker.FileTypeFilter.Add(".mpc");
                picker.FileTypeFilter.Add(".tak");
                picker.FileTypeFilter.Add(".opus");
                picker.FileTypeFilter.Add(".dsf");
                picker.FileTypeFilter.Add(".dff");

                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0)
                {
                    return;
                }

                string[] paths = files
                    .Select(f => f.Path)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

                LoadAndAddFiles(paths, persistAsFiles: true, replace: true);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("打开文件失败", ex.Message);
            }
        }


        /// <summary>把本地路径构造成 file URI,转义 # % ? 避免被解析为 fragment/query。</summary>
        private static Uri CreateFileMediaUri(string path)
        {
            string escaped = path
                .Replace("%", "%25")
                .Replace("#", "%23")
                .Replace("?", "%3F");
            return new Uri(escaped, UriKind.Absolute);
        }


        /// <summary>根据路径读元数据并加入列表，然后按当前排序规则重排。</summary>
        /// <param name="persist">为 true 时写入上次会话（默认）。</param>
        /// <param name="persistAsFiles">为 true 时按「文件列表」模式保存整份播放列表。</param>
        private void LoadAndAddFiles(string[] filePaths, bool persist = true, bool persistAsFiles = false, bool replace = false)
        {
            if (filePaths == null || filePaths.Length == 0)
            {
                return;
            }

            string? playingPath = null;
            if (replace)
            {
                // 替换模式：停止当前播放并清空媒体库，再载入新内容
                MediaPlayer? player = GetPlayer();
                if (player != null)
                {
                    player.Pause();
                    player.Source = null;
                }

                StopEngineIfActive();
                _playlist.Clear();
                _currentIndex = -1;
                ClearNowPlayingPanel();
            }
            else
            {
                // 记住当前正在播的文件，排序后要能找回下标
                playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                    ? _playlist[_currentIndex].FilePath
                    : null;
            }

            // HashSet 去重，避免大批量导入时对 _playlist 反复线性扫描
            var knownPaths = new HashSet<string>(
                _playlist.Select(i => i.FilePath),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;

            // 大批量导入时临时解绑列表视图，避免逐条 Add 触发 ListView 反复创建/测量容器造成卡顿
            bool rebounded = ReferenceEquals(PlaylistView.ItemsSource, _playlist);
            if (rebounded)
            {
                PlaylistView.ItemsSource = null;
            }

            foreach (string path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    continue;
                }

                // 内部缓存产物（DSD DoP 缓存 / 转码缓存 / WebDAV 下载缓存）不是本地曲库内容，不进音乐库
                if (LibraryPathGuard.IsLibraryExcludedFile(path))
                {
                    continue;
                }

                if (!knownPaths.Add(path))
                {
                    continue;
                }

                try
                {
                    PlaylistItem item = CreatePlaylistItemFromPath(path);
                    // CUE 分轨展开：有同名 .cue 的文件按轨加入（设置关闭时原样返回单条）
                    foreach (PlaylistItem track in CueTrackExpansion.Expand(new List<PlaylistItem> { item }))
                    {
                        _playlist.Add(track);
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    knownPaths.Remove(path);
                    System.Diagnostics.Debug.WriteLine($"加载失败: {path} → {ex.Message}");
                }
            }

            // 循环结束恢复绑定；即使下方早返回(added==0)也能保持列表可见
            if (rebounded)
            {
                PlaylistView.ItemsSource = _playlist;
            }

            if (added == 0)
            {
                if (replace)
                {
                    ApplyCategoryView();
                }

                return;
            }

            // 当前在「歌曲」分类时，按标题默认排序刷新列表
            if (_currentCategory == "Songs")
            {
                ApplyCategoryView();
            }
            else if (_currentCategory == "Albums")
            {
                ApplyCategoryView();
            }
            else if (_currentCategory == "Artists" || _currentCategory == "AlbumArtists")
            {
                ApplyCategoryView();
            }
            else if (string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
            {
                // 播放列表保持用户自己的顺序（添加顺序 / 手动拖动的结果）——
                // 这里只是往曲库补歌，不该顺手把播放队列重排一遍。
                // 2026-10-08 用户实测：启动恢复出的播放队列被自动按标题打乱，就是这条分支干的。
                // 用户主动点排序按钮走 SortField/SortOrder 那条路径，不受这里影响。
                RenumberCollection(_queue.Items);
            }
            else
            {
                ApplySort(preservePlayingPath: playingPath);
            }

            // 不自动播放：用户双击或点播放后再开始
            if (added > 0)
            {
                NowPlayingText.Text = $"已添加 {added} 首，共 {_playlist.Count} 首";
            }

            if (persist && persistAsFiles)
            {
                LibrarySessionStore.SaveFiles(_playlist.Select(i => i.FilePath));
            }
        }


        // =====================================================================
        // 左侧分类导航
        // =====================================================================

        private void CategoryNavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string tag } || string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            if (string.Equals(_currentCategory, tag, StringComparison.Ordinal)
                && _openedAlbum == null
                && _openedArtist == null)
            {
                return;
            }

            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = tag;
                ApplyCategoryView();
            });
            ApplySwitchPlaylistPausePreference();
        }


        // =====================================================================
        // E3：左侧分类栏收起 / 展开
        // =====================================================================

        private bool _leftNavCollapsed;

        /// <summary>收起后的窄条宽度（只放图标）。展开宽度取收起前的实际列宽（记住，恢复时不跳回 200）。
        /// 2026-10-16：44 → 56。原来 44 减去卡片留白后只剩约 26px，汉堡按钮（28px）都放不下，
        /// 更没法让它和下面的分类图标对齐。56 是按「卡片留白 + 汉堡 28 + 图标列」算出来的最小值。</summary>
        private const double LeftNavCollapsedWidth = 56;
        private double _leftNavExpandedWidth = 200;

        /// <summary>展开态列宽的下限（与 XAML 里 CategoryColumn 的 MinWidth 一致）。</summary>
        private const double LeftNavExpandedMinWidth = 190;

        /// <summary>收起态窄条的内边距。
        ///
        /// 2026-10-16 用户反馈「收起时图标还是没水平居中」+「图标闪一下」——两个都由这里决定：
        ///
        /// ① 为什么必须**左右相等**（15/15，不是 16/4）：
        ///    收起态的对齐是 Center（见 NavContentAlignment，用户要居中）。内容盒 =
        ///    [Padding.Left, 卡片内宽 - Padding.Right]，它被居中 = 内容盒自己被摆到卡片中间，
        ///    所以**只有左右留白相等，盒中心才等于卡片中心、图标才真正居中**。
        ///    旧值 16/4 右窄左宽 → 盒中心整体右移 (16-4)/2 = 6px（实测确实偏右，用户看出来了）。
        ///    取 15 而不是 16：卡片内宽约 56，左右各 15 剩 26，正好等于按钮自身
        ///    （Padding 6 + 图标 14 + 6）→ 不挤不裁；左右相等 → 精确居中，且不依赖内宽具体值。
        ///
        /// ② 为什么左 15 与展开态的左 16 差 1 也不打紧：展开态是 Left 对齐、收起态收尾才切
        ///    Center，切换那一刻两种算法的图标落点都是「左留白 + 13」，15/15 与它只差 1px，
        ///    肉眼不可见（见 SetLeftNavIconMode 的完成回调）。
        ///
        /// ⚠️ 改这里会同时影响"居中"和"两态图标不跳"，改数前先读上面两条。</summary>
        private static Microsoft.UI.Xaml.Thickness LeftNavPaddingCollapsed => new(15, 2, 15, 2);

        /// <summary>展开态卡片内边距。
        /// 左 16：2026-10-16 用户反馈"图标离主程序左边界太近"，但同时要求"分类栏要和左边界相接"
        /// → 卡片继续贴边（Margin 左 -16），靠**内边距**把图标往右推到 16px（再算按钮自身
        ///   Padding 6，图标实际落在离窗口左缘约 22px 处）。
        /// 右 14：按钮离卡片右边界的呼吸（用户嫌 2px 太挤）。</summary>
        private static Microsoft.UI.Xaml.Thickness LeftNavPaddingExpanded => new(16, 2, 14, 2);

        private void LeftNavCollapseButton_Click(object sender, RoutedEventArgs e)
            => SetLeftNavCollapsed(!_leftNavCollapsed);

        /// <summary>
        /// 收起/展开左侧分类栏（2026-10-16 用户要求：收起后显示各分类图标 + 展开收起要有动画）。
        ///
        /// 做法是"抽屉"式：列宽在动画结束后才真正切换，动画期间卡片用 RenderTransform 向左滑出，
        /// 滑到只剩窄条同宽时再把列宽切过去 —— 卡片露出窗口左缘的部分被窗口裁掉，
        /// 所以切换那一帧看不出跳变。展开反过来：先切回宽列，卡片再滑回 0。
        ///
        /// 2026-10-16 用户反馈「展开收起动画会重新加载图标显示，现在会一闪」—— 两个原因：
        ///   ① 文字用 Visibility 瞬间切换，动画一开始就整排文字凭空消失；
        ///      → 改成 Opacity 淡出（与滑动同长），淡完了才真正 Collapsed。
        ///   ② 图标的对齐在两态之间被切来切去，位置一变就看着像"重新出现"；
        ///      → 两态都取 NavContentAlignment（同值），图标从头到尾不动，不闪。
        /// 会话内有效，不持久化（重启回到展开态）。
        /// </summary>
        private void SetLeftNavCollapsed(bool collapsed)
        {
            // 播放页展开期间左栏整块隐藏、左列宽压 0（2026-10-16 晚间用户要求），
            // 这时不允许收起/展开：否则 Completed 里改列宽会在播放页左侧撕出空白条。
            if (_nowPlayingPaneOpen)
            {
                return;
            }

            _leftNavCollapsed = collapsed;
            try
            {
                if (CategoryColumn == null || LeftCategoryNavBorder == null)
                {
                    return;
                }

                // 收起前记住当前列宽（用户可能拖宽过分栏，恢复时按原宽度）
                if (!collapsed && CategoryColumn.ActualWidth > LeftNavCollapsedWidth + 20)
                {
                    _leftNavExpandedWidth = CategoryColumn.ActualWidth;
                }

                double slide = Math.Max(0, _leftNavExpandedWidth - LeftNavCollapsedWidth);

                // 1) 内容模式切换（图标 ↔ 文字）：淡出淡入，不再瞬间消失
                SetLeftNavIconMode(collapsed, animate: true);

                // 1b) 窄条 / 宽栏的留白 + 汉堡按钮归位（收起时挪到左竖线，与分类图标同一条线）
                ApplyLeftNavChrome(collapsed);

                if (LibraryNavTitleText != null)
                {
                    LibraryNavTitleText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                }

                // 极客模式的曲库统计块（TRK/ALB/ART）在窄条里放不下，收起时一起藏
                if (GeekLibStats != null)
                {
                    GeekLibStats.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                }

                if (LeftNavCollapseButton != null)
                {
                    ToolTipService.SetToolTip(LeftNavCollapseButton, collapsed ? "展开分类栏" : "收起分类栏");
                }

                // 2) RenderTransform 就位并播放滑动动画
                if (LeftCategoryNavBorder.RenderTransform is not CompositeTransform ct)
                {
                    ct = new CompositeTransform();
                    LeftCategoryNavBorder.RenderTransform = ct;
                }

                var slideAnim = new DoubleAnimation
                {
                    From = collapsed ? 0 : -slide,
                    To = collapsed ? -slide : 0,
                    Duration = new Duration(TimeSpan.FromSeconds(collapsed ? 0.16 : 0.2)),
                    EasingFunction = new CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slideAnim, LeftCategoryNavBorder);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slideAnim,
                    "(UIElement.RenderTransform).(CompositeTransform.TranslateX)");

                // ⚠️ 关键（2026-10-16 用户反馈"展开收起图标会重新加载然后一闪"的真正原因）：
                // 上面那条动画是把**整块卡片**向左滑出窗口外（收起时滑 -slide）。图标在卡片
                // 左边约 22px 处，跟着卡片一起滑 → 滑出窗口左缘被裁掉 → 动画结束列宽切成窄条、
                // 卡片位移归零，图标又"弹"回原处。视觉上就是图标消失一下又出现 = 一闪。
                // 修法=**底板滑、内容反向补偿**：给内容容器一条等量反向的位移，两者相加为 0，
                // 图标在整个动画期间钉在原地不动，只有卡片底板（背景/圆角/边线）滑出去。
                var contentSlideAnim = new DoubleAnimation
                {
                    From = collapsed ? 0 : slide,
                    To = collapsed ? slide : 0,
                    Duration = slideAnim.Duration,
                    EasingFunction = new CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(contentSlideAnim, LeftCategoryGrid);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(contentSlideAnim,
                    "(UIElement.RenderTransform).(CompositeTransform.TranslateX)");

                var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                sb.Children.Add(slideAnim);
                sb.Children.Add(contentSlideAnim);
                sb.Completed += (_, _) =>
                {
                    try
                    {
                        if (CategoryColumn != null)
                        {
                            // 收起：动画结束才把列宽切成窄条（播放列表这时候让位）；
                            // 展开：上面已提前切好，这里只需把残留位移清掉
                            if (_leftNavCollapsed)
                            {
                                CategoryColumn.Width = new GridLength(LeftNavCollapsedWidth);
                                CategoryColumn.MinWidth = LeftNavCollapsedWidth;
                                // 窄条外壳（右留白 6 + 去投影）与列宽同一帧切过去。
                                // ⚠️ 不能更早：抽屉动画期间卡片得保持展开态外宽（202px），
                                // 从宽位置滑出；提前切窄的话整张卡会滑出窗口左缘、动画中段消失。
                                ApplyLeftNavCardChrome(true);
                            }

                            if (LeftCategoryNavBorder?.RenderTransform is CompositeTransform t)
                            {
                                t.TranslateX = 0;
                            }

                            // 内容容器的补偿位移同步清零（否则图标会被永久推到右边）
                            // ⚠️ 只能改现有 CompositeTransform 的 TranslateX，绝不能 new 一个替换掉：
                            // 这个变换对象在 XAML 里挂了 x:Name="LeftCategoryTransform"，
                            // MainContentGrid 资源里的 MainContentDepthBackStoryboard / ...RestoreStoryboard
                            // 按 TargetName 引用它。运行时换实例 → 名称解析不到 → 播放页展开时
                            // mcBack?.Begin() 抛 COMException「Cannot resolve TargetName LeftCategoryTransform」。
                            if (LeftCategoryGrid?.RenderTransform is CompositeTransform content)
                            {
                                content.TranslateX = 0;
                            }

                            // 列宽真正落定之后再量汉堡与图标的实际位置：只有这时候窄条的最终
                            // 宽度才确定，量出来的偏移才准（动画前量会量到宽栏的值）。
                            // ⚠️ 还要再排一帧：列宽刚改完的这一帧布局还没消化完，直接量会量到
                            // 过渡值（表现为"对齐差一点点"），排两帧更稳。
                            try
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    AlignCollapsedHeaderButton();
                                    DispatcherQueue.TryEnqueue(AlignCollapsedHeaderButton);
                                });
                            }
                            catch (Exception caught)
                            {
                                StartupLog.WriteException("MainWindow.Misc.cs.SetLeftNavCollapsed.Enqueue", caught);
                            }
                        }
                    }
                    catch (Exception caught)
                    {
                        StartupLog.WriteException("MainWindow.Misc.cs.SetLeftNavCollapsed.Completed", caught);
                    }
                };

                if (!collapsed)
                {
                    // 展开：先让播放列表让位（列宽立刻变宽），卡片从窄条位置滑出
                    CategoryColumn.Width = new GridLength(_leftNavExpandedWidth);
                    CategoryColumn.MinWidth = LeftNavExpandedMinWidth;
                    ct.TranslateX = -slide;
                }

                sb.Begin();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.Misc.cs.SetLeftNavCollapsed", caught);
            }
        }

        /// <summary>
        /// 窄条 / 宽栏切换时，把留白和汉堡按钮一起换到位。
        /// 汉堡按钮（左上角那个）在展开态靠右、在收起态必须挪到左边，
        /// 与下面那列分类图标落在同一条竖线上（2026-10-16 用户要求）。
        /// 只在这里改，别在别处再写一份 Margin。
        /// </summary>
        private void ApplyLeftNavChrome(bool collapsed)
        {
            if (LeftCategoryGrid != null)
            {
                LeftCategoryGrid.Padding = collapsed ? LeftNavPaddingCollapsed : LeftNavPaddingExpanded;
            }

            if (LeftNavHeaderRow != null)
            {
                // 收起态先把左偏移清零，真正的对齐量由 AlignCollapsedHeaderButton 在
                // 列宽落定后按实测值补上（这里写死数字会因为图标字体宽度不同而漂）。
                LeftNavHeaderRow.Margin = collapsed ? new Microsoft.UI.Xaml.Thickness(0, 6, 0, 0)
                                                    : new Microsoft.UI.Xaml.Thickness(6, 6, 8, 0);
            }

            if (LeftNavCollapseButton != null)
            {
                // 收起态挪到第 0 列并靠左（第 1 列是 Auto，会把汉堡顶到最右，跟图标对不上）；
                // 展开态还原回第 1 列靠右，跟「音乐库」标题同一行。
                LeftNavCollapseButton.SetValue(Grid.ColumnProperty, collapsed ? 0 : 1);
                LeftNavCollapseButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Left
                                                                      : HorizontalAlignment.Right;
            }

            // 展开态外壳（外边距 + 投影）立刻换到位：抽屉要从宽位置滑入。
            // 收起态外壳不在这里换——动画期间卡片必须保持展开态的外宽从宽位置滑出，
            // 切到窄条外壳的时机在 SetLeftNavCollapsed 的 Completed 里（列宽落定同一帧）。
            if (!collapsed)
            {
                ApplyLeftNavCardChrome(false);
            }
        }

        /// <summary>
        /// 左栏卡片"外壳"（外边距 + 投影）按收起/展开落到单一真相源。
        ///
        /// 2026-10-16 用户反馈收起态「右边界似乎有宽度，遮住了一点图标」——逐像素量过：
        /// 那圈宽度主要是 ThemeShadow 的渐带（卡片右缘往外约 25px 才淡完），在 58px 的
        /// 窄条上占比接近一半；再加 14px 右留白，右缘一整条都发"糊"。
        /// 收起态改成：右留白 14→6（卡片宽 58→66，左缘仍贴窗口，图标靠内边距自动居中）
        /// + 去掉投影，右缘只剩 1px 边线，利落。
        /// 展开态：宽留白 + 投影照旧（播放页展开时的无背景形态由 Features 那边管）。
        ///
        /// ⚠️ 播放页展开态（_nowPlayingPaneOpen）不走本方法的收起分支：那个状态下卡片
        /// 已由 SetNowPlayingPaneVisible 改成无背景/无投影，Margin 用 NowPlaying 那组。</summary>
        private void ApplyLeftNavCardChrome(bool collapsed)
        {
            if (LeftCategoryNavBorder == null)
            {
                return;
            }

            if (collapsed && !_nowPlayingPaneOpen)
            {
                LeftCategoryNavBorder.Margin = LeftNavCardMarginCollapsed;
                LeftCategoryNavBorder.Shadow = null;
                return;
            }

            LeftCategoryNavBorder.Margin = _nowPlayingPaneOpen
                ? LeftNavCardMarginNowPlaying
                : LeftNavCardMarginExpanded;

            if (LeftCategoryNavBorder.Shadow is not Microsoft.UI.Xaml.Media.ThemeShadow)
            {
                LeftCategoryNavBorder.Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
                LeftCategoryNavBorder.Translation = new System.Numerics.Vector3(0, 0, 40f);
            }
        }

        /// <summary>
        /// 收起态：把汉堡按钮的中心挪到与分类图标中心同一条竖线上。
        /// 不写死偏移量 —— 直接量两个元素相对左栏卡片的实际中心，差多少补多少，
        /// 换字体 / 换图标大小都不会漂。量不到（还没布局）就跳过。
        /// </summary>
        private void AlignCollapsedHeaderButton()
        {
            if (!_leftNavCollapsed || LeftCategoryGrid == null || LeftNavHeaderRow == null
                || LeftNavCollapseButton == null)
            {
                return;
            }

            FrameworkElement? icon = FindFirstNavIcon();
            if (icon == null || icon.ActualWidth <= 0 || LeftNavCollapseButton.ActualWidth <= 0)
            {
                return;
            }

            double iconCenter = icon.TransformToVisual(LeftCategoryGrid)
                .TransformPoint(new Windows.Foundation.Point(icon.ActualWidth / 2, 0)).X;
            double buttonCenter = LeftNavCollapseButton.TransformToVisual(LeftCategoryGrid)
                .TransformPoint(new Windows.Foundation.Point(LeftNavCollapseButton.ActualWidth / 2, 0)).X;

            // ⚠️ 算**绝对目标值**，不要写成 "Margin.Left += delta" 那种累加：
            // 实测值里带了一个固定的微调量（见下），累加会每次调用都再推一次，越推越远。
            // 先把当前 Margin 的影响减掉，得到"Margin.Left=0 时汉堡中心在哪"，再一次性算目标。
            double buttonCenterAtZero = buttonCenter - LeftNavHeaderRow.Margin.Left;
            double targetLeft = iconCenter - buttonCenterAtZero + CollapsedHeaderNudgeX;

            if (Math.Abs(targetLeft - LeftNavHeaderRow.Margin.Left) < 0.5)
            {
                return;
            }

            LeftNavHeaderRow.Margin = new Microsoft.UI.Xaml.Thickness(targetLeft, 6, 0, 0);
        }

        /// <summary>
        /// 收起态汉堡按钮的**额外右移量**（px）。2026-10-16 用户："图标偏右了，把汉堡图标往右调一点点"。
        /// 主体对齐是实测出来的（不写死），这个值只做视觉微调：正数=汉堡再往右，负数=往左。
        /// 想改松紧只动这一个数字。
        /// </summary>
        private const double CollapsedHeaderNudgeX = 2;

        /// <summary>
        /// 找左栏第一个**可见**的导航图标元素（收起态汉堡对齐的测量基准）。
        /// 2026-10-16：设置里「启用 emoji 分类图标」开着时，每个按钮里的 FontIcon 是
        /// Collapsed（ActualWidth=0、TransformToVisual 量不到有效位置），旧实现只找
        /// FontIcon → 对齐整体跳过 → 汉堡和下面的图标对不齐。改成优先取可见的 emoji，
        /// 其次 FontIcon / 极客皮肤的 Path。
        /// </summary>
        private FrameworkElement? FindFirstNavIcon()
        {
            foreach (FrameworkElement? root in new FrameworkElement?[] { CategoryNavPanel, NavToolPanel })
            {
                if (root == null)
                {
                    continue;
                }

                FrameworkElement? found = FindFirstVisibleNavIcon(root);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// 在导航面板里找第一个**可见且已布局**的图标元素（emoji TextBlock / FontIcon / 极客 Path）。
        /// 深度优先，顺序与视觉上的排列一致（先出现先返回）。
        /// </summary>
        private static FrameworkElement? FindFirstVisibleNavIcon(DependencyObject? root)
        {
            if (root == null)
            {
                return null;
            }

            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);

                FrameworkElement? candidate = child switch
                {
                    TextBlock tb when tb.Tag is string tag && tag == "NavEmoji" => tb,
                    Microsoft.UI.Xaml.Controls.FontIcon fontIcon => fontIcon,
                    Microsoft.UI.Xaml.Shapes.Path path => path,
                    _ => null
                };

                if (candidate != null && candidate.Visibility == Visibility.Visible && candidate.ActualWidth > 0)
                {
                    return candidate;
                }

                FrameworkElement? nested = FindFirstVisibleNavIcon(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        // ⚠️ FindFirstFontIcon 不在这里再定义一份：MainWindow.UiTheme.cs 里已经有一个
        // （同为 MainWindow 的 partial，可直接调用）。2026-10-16 两个会话各加了一份，
        // 编译直接 CS0111 重复定义。删掉这份，用 UiTheme.cs 那版（它带 null 检查更稳）。

        /// <summary>
        /// 左栏导航按钮内容的对齐方式（收起 / 展开两态统一取值）。
        ///
        /// ⚠️ 2026-10-16 用户反馈两个现象：① 收起和展开图标位置不一样；
        /// ② 收起后点另一个分类，整列图标会往左偏。根因是同一个 ——
        /// 收起时 SetLeftNavIconMode 把对齐设成 Center，而切换分类走到的
        /// UpdateLibraryNavHighlight（MainWindow.Library2.cs）里写死了
        /// HorizontalContentAlignment = Left，点一次就把 Center 覆盖掉了。
        /// 修法=两态都取同一个值（Left：图标左边距与展开态完全一致，收起后
        /// 图标就停在原处不跳），所有会设这个属性的地方统一走这里，别再各写各的。
        /// </summary>
        // 2026-10-16 第三轮：用户明确要「收起时图标在窄条里**水平居中**」（Left 靠左观感差）。
        // 收起=Center、展开=Left。⚠️ 两态取不同值正是当初"点一下分类整列偏移"的起因，
        // 所以必须让**所有**会设这个属性的地方都走本属性（SetLeftNavIconMode 与
        // UpdateLibraryNavHighlight 已统一），任一行写死字面量都会让偏移复发。
        private HorizontalAlignment NavContentAlignment
            => _leftNavCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        /// <summary>
        /// 左栏内容在「文字 ↔ 图标」两种模式间切换：隐藏每个导航按钮里的 NavLabel。
        /// 所有导航按钮结构一致（Button &gt; StackPanel &gt; [FontIcon][NavEmoji][NavLabel]），
        /// 递归找 Tag="NavLabel" 即可。
        /// 对齐不在这里切：收起态保持与展开态同一对齐（见 NavContentAlignment），
        /// 否则「点一下分类就整列偏移」会复发。
        ///
        /// <param name="animate">true = 文字淡出/淡入（收起展开动画期间用，避免整排文字瞬间消失的
        /// "一闪"）；false = 直接切换（初始化时用，此时不该有动画）。</param>
        /// </summary>
        private void SetLeftNavIconMode(bool iconOnly, bool animate = false)
        {
            try
            {
                var labels = new List<TextBlock>();
                var emojis = new List<TextBlock>();
                var buttons = new List<Button>();
                foreach (FrameworkElement? root in new FrameworkElement?[] { CategoryNavPanel, NavToolPanel })
                {
                    if (root == null)
                    {
                        continue;
                    }

                    foreach (Button? button in FindDescendantButtons(root))
                    {
                        buttons.Add(button);

                        foreach (TextBlock? label in FindNavLabels(button))
                        {
                            labels.Add(label);
                        }

                        foreach (TextBlock? emoji in FindNavEmojis(button))
                        {
                            emojis.Add(emoji);
                        }
                    }
                }

                if (!animate)
                {
                    foreach (Button button in buttons)
                    {
                        button.HorizontalContentAlignment = NavContentAlignment;
                    }

                    ApplyCollapsedEmojiNudge(emojis, iconOnly);

                    foreach (TextBlock label in labels)
                    {
                        label.Opacity = 1;
                        label.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
                    }

                    return;
                }

                // ⚠️ 对齐（Left ↔ Center）的**切换时机**是"图标闪一下"的关键（2026-10-16 第二次反馈）：
                //   对齐一改，内容的居中基准立刻变化。若在动画开始就切 Center，而此时文字还没淡完
                //   （Opacity 淡出期间仍占位、内容仍是一个很宽的块）→ 宽块被重新居中 → 图标先被挤到
                //   左边、淡出结束文字收起后又弹回中间 = 一次"闪"。
                //   所以：
                //     · 展开：必须在文字**变可见之前**就把对齐切回 Left（下面立刻切）；
                //     · 收起：对齐留到淡出**结束后**再切（见 Completed），那时文字已收起、
                //       内容只剩图标，切过去落点不变（配合 LeftNavPaddingCollapsed 的左右等宽）。
                if (!iconOnly)
                {
                    foreach (Button button in buttons)
                    {
                        button.HorizontalContentAlignment = NavContentAlignment;
                    }
                }

                // emoji 墨迹补偿与对齐不同，不挑时机：现在就Apply（只挪 0.75px，看不见）。
                // ⚠️ 必须早于 AlignCollapsedHeaderButton 的测量——它在滑动结束后两帧量图标
                // 中心，得让它量到补偿后的位置，否则汉堡会按没补偿的中心去对齐。
                ApplyCollapsedEmojiNudge(emojis, iconOnly);

                // 淡出（收起）/ 淡入（展开）：与卡片滑动同长，动画跑完才真正改 Visibility。
                // ⚠️ 淡的是 Opacity 不是 Visibility —— 文字仍占位，按钮内容宽度不变，
                //    所以图标（Left 对齐）位置一动不动，这才是"图标持久显示不闪"的关键。
                var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                foreach (TextBlock label in labels)
                {
                    if (!iconOnly)
                    {
                        label.Visibility = Visibility.Visible;
                    }

                    label.Opacity = iconOnly ? 1 : 0;

                    var fade = new DoubleAnimation
                    {
                        From = iconOnly ? 1 : 0,
                        To = iconOnly ? 0 : 1,
                        Duration = new Duration(TimeSpan.FromSeconds(iconOnly ? 0.16 : 0.2))
                    };
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, label);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
                    sb.Children.Add(fade);
                }

                sb.Completed += (_, _) =>
                {
                    try
                    {
                        // 收起路径的对齐在这里才切（见上面那段"切换时机"）：
                        // 文字已收起，落点由"左留白 + 13"变成"盒中心"，两者相等/差 1px，不跳。
                        foreach (Button button in buttons)
                        {
                            button.HorizontalContentAlignment = NavContentAlignment;
                        }

                        foreach (TextBlock label in labels)
                        {
                            label.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
                            label.Opacity = iconOnly ? 0 : 1;
                        }
                    }
                    catch (Exception caught)
                    {
                        StartupLog.WriteException("MainWindow.Misc.cs.SetLeftNavIconMode.Completed", caught);
                    }
                };

                sb.Begin();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.Misc.cs.SetLeftNavIconMode", caught);
            }
        }

        private static IEnumerable<Button> FindDescendantButtons(DependencyObject root)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Button b)
                {
                    yield return b;
                }
                foreach (Button inner in FindDescendantButtons(child))
                {
                    yield return inner;
                }
            }
        }

        private static IEnumerable<TextBlock> FindNavLabels(DependencyObject root)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb && tb.Tag is string tag && tag == "NavLabel")
                {
                    yield return tb;
                }
                foreach (TextBlock inner in FindNavLabels(child))
                {
                    yield return inner;
                }
            }
        }

        private static IEnumerable<TextBlock> FindNavEmojis(DependencyObject root)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb && tb.Tag is string tag && tag == "NavEmoji")
                {
                    yield return tb;
                }
                foreach (TextBlock inner in FindNavEmojis(child))
                {
                    yield return inner;
                }
            }
        }

        /// <summary>
        /// 收起态 emoji 图标的**左移补偿**（px，负数=往左）。
        ///
        /// 2026-10-16 实测（ClassicLight +「启用 emoji 分类图标」开，2560x1392 截图逐像素量）：
        /// Segoe UI Emoji 的字形墨迹比元素盒中心整体偏右（15 个图标 +0.5~+1.5，均值 +0.875），
        /// 而元素盒本身是居中的（LeftNavPaddingCollapsed 左右等宽 → 盒中心=卡片中心）。
        /// "盒居中"≠"墨迹居中"——用户看到的正是"图标整体偏右一点点，还是没有水平居中"。
        /// 墨迹中心 ≈ 盒中心 + Margin.Left/2 + 0.9，取 -1.5 让墨迹落在卡片中心。
        ///
        /// 只补 emoji：Fluent 字形（FontIcon）的墨迹基本就在盒心里（汉堡 E700 实测 -0.5），
        /// 动了反而偏；极客 Path 同理不碰。展开态必须清零（展开是左对齐，不该有位移）。</summary>
        private static readonly Microsoft.UI.Xaml.Thickness CollapsedEmojiNudgeMargin = new(-1.5, 0, 0, 0);

        private static void ApplyCollapsedEmojiNudge(List<TextBlock> emojis, bool iconOnly)
        {
            foreach (TextBlock emoji in emojis)
            {
                emoji.Margin = iconOnly ? CollapsedEmojiNudgeMargin : default(Microsoft.UI.Xaml.Thickness);
            }
        }


        /// <summary>
        /// 供选项设置「启动后进入」即时预览：直接切到指定分类（白名单内才生效）。
        /// 与左侧导航点击等价，但不做「同分类提前返回」判断，便于从任意分类立即跳转。
        /// </summary>
        internal void NavigateLibraryCategory(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag) || !AppSettingsStore.ValidStartupCategories.Contains(tag))
            {
                return;
            }

            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = tag;
                ApplyCategoryView();
            });
            ApplySwitchPlaylistPausePreference();
        }


        // 音效处理按钮：占位入口（后续阶段接入 ECHO 音效处理页面）
        private void NavAudioFxButton_Click(object sender, RoutedEventArgs e)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "AudioFX";
                ApplyCategoryView();
            });
            // 音效处理 DSP 工作台（EQ / 声道平衡 / 限幅）
            NowPlayingText.Text = "音效处理（EQ / 声道平衡 / 安全限幅）";
        }


        private void AddMonoComboItem(string mode, string label)
        {
            AudioFxChannelMonoCombo.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        }


        /// <summary>从持久化状态加载音效面板控件。</summary>
        private void LoadAudioFxUiFromStore()
        {
            _audioFxLoading = true;
            try
            {
                EqState.Current = EqCurveStore.Load();
                AudioFxEqEnableToggle.IsOn = EqState.Current.Enabled;
                AudioFxEqPreampText.Text = "预增益 (preamp)：" + FormatHelper.FormatAudioFxDb(EqState.Current.PreampDb) + " dB";
                SelectAudioFxEqPreset(EqState.Current.PresetId);
                AudioFxEqModeRadio.SelectedIndex = 0; // 专业
                SyncAudioFxEqSimpleFromState();
                SelectAudioFxEqBand(_audioFxEqSelected);
                RedrawAudioFxEqCurve();
                RefreshAudioFxEqBandEditor();

                DspExtraState extra = DspExtraStore.Load();
                ExtraParameters.Load(extra);
                var ch = extra.ChannelBalance;
                ReplayGainState rglog = ReplayGainStore.Load();
                StartupLog.Write($"[DSP] 加载读盘 eq.Enabled={EqState.Current.Enabled} limiter={extra.Safety?.EnableLimiter} rg.Mode={rglog.Mode}");
                AudioFxChannelToggle.IsOn = ch.Enabled;
                AudioFxChannelBalanceSlider.Value = ch.Balance;
                AudioFxChannelLeftGainSlider.Value = ch.LeftGainDb;
                AudioFxChannelRightGainSlider.Value = ch.RightGainDb;
                SelectAudioFxMonoMode(ch.MonoMode);
                AudioFxChannelSwapToggle.IsOn = ch.SwapChannels;
                AudioFxChannelInvertLToggle.IsOn = ch.InvertLeft;
                AudioFxChannelInvertRToggle.IsOn = ch.InvertRight;
                AudioFxChannelLeftDelaySlider.Value = ch.LeftDelayMs;
                AudioFxChannelRightDelaySlider.Value = ch.RightDelayMs;
                AudioFxChannelCrossfeedToggle.IsOn = ch.CrossfeedEnabled;
                // Crossfeed 已独立成页：强度和截止的两处读数要在加载时同步一次
                RefreshDspXfeedLevelReadout();
                RefreshCrossfeedCutoffReadout();
                AudioFxChannelCrossfeedSlider.Value = ch.CrossfeedLevel;
                // 老存档没有这个字段 → 反序列化得到默认 700，正好等于旧版写死值，行为不变
                AudioFxChannelCrossfeedCutoffSlider.Value = ch.CrossfeedCutoffHz;

                var safety = extra.Safety;
                AudioFxSafetyHeadroomSlider.Value = safety.HeadroomDb;
                AudioFxSafetyHeadroomLabel.Text = "余量 (dB)：" + FormatHelper.FormatAudioFxDb(safety.HeadroomDb);
                AudioFxSafetyLimiterToggle.IsOn = safety.EnableLimiter;

                // ReplayGain：先装载模型，再让控件照模型投影（模型是唯一参数来源）
                RgParameters.Load(ReplayGainStore.Load());
                SelectAudioFxRgMode(RgParameters.Current.Mode);
                AudioFxRgPreampSlider.Value = RgParameters.Current.PreampDb;
                AudioFxRgPreampLabel.Text = "额外增益 (dB)：" + FormatHelper.FormatAudioFxDb(RgParameters.Current.PreampDb);
                AudioFxRgPreventClippingToggle.IsOn = RgParameters.Current.PreventClipping;
                RefreshAudioFxRgInfo();
            }
            finally
            {
                _audioFxLoading = false;
            }

            // 面板已从盘加载完成，之后才允许 DSP handler 持久化/应用（启动阶段误触发需屏蔽）。
            _audioFxPanelReady = true;

            // 模块导航：默认定位第一个模块，并按当前状态点亮各模块圆点
            InitDspNav();

            // EQ 页快捷状态条（EQ 状态 / bit-perfect / 预增益 / 余量 / 预设）初始同步
            UpdateAudioFxEqQuickStrip();

        }

        /// <summary>
        /// 打开房间校正：2026-10-12 阶段 2 起不再弹独立窗口 —— IR 导入已内嵌到
        /// 音效处理的「FIR / 房间校正」页，这里直接跳过去并选中该页。
        /// （网页「音效处理」页也走这里。）
        /// </summary>
        internal void OpenRoomCorrectionCore()
        {
            GoToDspPage(DspPageFirIndex);
        }

        /// <summary>切到音效处理面板并定位到指定 DSP 子页（阶段 2 起 RG 扫描 / 房间校正都走这条）。</summary>
        private void GoToDspPage(int pageIndex)
        {
            ExitMultiSelectMode();
            CommitLibraryNavigation(() =>
            {
                _currentCategory = "AudioFX";
                ApplyCategoryView();
            });
            if (pageIndex >= 0)
            {
                SelectDspPage(pageIndex);
            }
        }


        private void SelectAudioFxMonoMode(string mode)
        {
            for (int i = 0; i < AudioFxChannelMonoCombo.Items.Count; i++)
            {
                if (AudioFxChannelMonoCombo.Items[i] is ComboBoxItem { Tag: string m } && string.Equals(m, mode, StringComparison.Ordinal))
                {
                    AudioFxChannelMonoCombo.SelectedIndex = i;
                    return;
                }
            }

            AudioFxChannelMonoCombo.SelectedIndex = 0;
        }


        private void ApplySimpleTones()
        {
            PushEqUndoSnapshot(coalesce: true);
            var s = new EqCurveState { Enabled = AudioFxEqEnableToggle.IsOn, PreampDb = EqState.Current.PreampDb, PresetId = "simple", PresetName = "简单模式" };
            SimpleEqStore.Save(new SimpleEqState { Bass = _eqSimpleBass, Vocal = _eqSimpleVocal, Air = _eqSimpleAir, Warm = _eqSimpleWarm });
            if (_eqSimpleBass > 0.01) AppendSimple(s, "bass", _eqSimpleBass);
            if (_eqSimpleVocal > 0.01) AppendSimple(s, "vocal", _eqSimpleVocal);
            if (_eqSimpleAir > 0.01) AppendSimple(s, "air", _eqSimpleAir);
            if (_eqSimpleWarm > 0.01) AppendSimple(s, "warm", _eqSimpleWarm);
            if (s.Bands.Count == 0) s.Bands.Add(new EqBand { Enabled = true, FrequencyHz = 1000, GainDb = 0 });

            EqState.Current = s;
            _audioFxEqSelected = EqState.Current.Bands.Count > 0 ? 0 : -1;
            RedrawAudioFxEqCurve();
            RefreshAudioFxEqBandEditor();
            ApplyDspToEngine();
        }


        /// <summary>重建预设下拉里的"我的预设"段（保存/删除后调用）。</summary>
        private void RefreshAudioFxUserPresetItems()
        {
            if (AudioFxEqPresetCombo == null || !_audioFxEqBuilt) return;
            string? selected = (AudioFxEqPresetCombo.SelectedItem as ComboBoxItem)?.Tag as string;

            AudioFxEqPresetCombo.Items.Clear();
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "平坦", Tag = "flat" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "古典", Tag = "classical" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "流行", Tag = "pop" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "爵士", Tag = "jazz" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "摇滚", Tag = "rock" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "柔和", Tag = "soft" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "低音增强", Tag = "bass" });
            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "自定义…", Tag = "custom" });
            var userPresets = EqUserPresetStore.Load();
            if (userPresets.Count > 0)
            {
                AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "—— 我的预设 ——", IsEnabled = false });
                foreach (var p in userPresets)
                {
                    AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "★ " + (string.IsNullOrWhiteSpace(p.PresetName) ? "未命名" : p.PresetName), Tag = p.PresetId });
                }
            }

            AudioFxEqPresetCombo.Items.Add(new ComboBoxItem { Content = "管理（删除）我的预设…", Tag = "manage" });

            _audioFxLoading = true;
            try { SelectAudioFxEqPreset(selected ?? "flat"); }
            finally { _audioFxLoading = false; }
        }


        private void AudioFxChannelMono_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ImportExtraParameterInput(sender);
            if (!_audioFxLoading) ApplyDspToEngine();
        }


        private void AudioFxChannelSwap_Toggled(object sender, RoutedEventArgs e)
        {
            ImportExtraParameterInput(sender);
            if (!_audioFxLoading) ApplyDspToEngine();
        }


        private void AudioFxSafetyHeadroom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            ImportExtraParameterInput(sender);
            if (_audioFxLoading) return;
            if (AudioFxSafetyHeadroomLabel != null)
            {
                AudioFxSafetyHeadroomLabel.Text = "余量 (dB)：" + FormatHelper.FormatAudioFxDb(AudioFxSafetyHeadroomSlider.Value);
            }

            ApplyDspToEngine();
        }


        private void AudioFxRgMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ImportExtraParameterInput(sender);
            if (_audioFxLoading) return;
            ApplyReplayGainToEngine();
        }


        private string CurrentAudioFxMonoMode()
        {
            return AudioFxChannelMonoCombo.SelectedItem is ComboBoxItem { Tag: string m } ? m : "off";
        }


        /// <summary>从指定列表进入多选。</summary>
        private void EnterMultiSelectModeFrom(ListView list)
        {
            if (list == null) return;
            _multiSelectTargetList = list;
            EnterMultiSelectMode((list.SelectedItems.FirstOrDefault() as PlaylistItem) ?? null);
        }


        /// <summary>预设字符串 → 排序字段链。</summary>
        private static List<(string field, bool asc)> PresetToFields(string preset)
        {
            var asc = new[] {
                ("Album", "专辑"),
                ("AlbumArtist,Album", "专辑艺术家 / 专辑"),
                ("AlbumArtist,Year,Album", "专辑艺术家 / 年份 / 专辑"),
                ("Artist,Album", "艺术家 / 专辑"),
                ("Genre,Album", "流派 / 专辑"),
                ("Year,Album", "年份 / 专辑")
            };
            foreach (var (fields, label) in asc)
            {
                if (preset == label)
                {
                    return fields.Split(',').Select(f => (f, true)).ToList();
                }
            }
            return new List<(string, bool)>();
        }


        /// <summary>根据当前左侧分类刷新中间区域</summary>
        private void ApplyCategoryView()
        {
            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;

            UpdateLibraryNavHighlight();

            // 列表墙/命中单详情默认隐藏：仅其它分类内按需显 Visible，避免切到其它页面残留盖层
            PlaylistWallBorder.Visibility = Visibility.Collapsed;
            PlaylistDetailBorder.Visibility = Visibility.Collapsed;
            AudioFxBorder.Visibility = Visibility.Collapsed;
            SettingsBorder.Visibility = Visibility.Collapsed;
            // ⚠ 标签板块（2026-10-13 并入时漏了这一行）：切到别的分类时没人把它收起来，
            // 于是标签面板会一直叠在其它页面内容上（2026-10-14 用户实测）。
            TagEditBorder.Visibility = Visibility.Collapsed;
            RatingFilterPanel.Visibility = Visibility.Collapsed;

            switch (_currentCategory)
            {
                case "Songs":
                    LibraryPaneTitle.Text = "歌曲";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    PlaylistView.ItemsSource = _playlist;

                    _sortField = SortField.Title;
                    _sortAscending = true;
                    SortFieldText.Text = "排序：标题";
                    SortOrderText.Text = "升序";
                    ApplySort(playingPath);
                    break;

                case "UserPlaylist":
                    LibraryPaneTitle.Text = "播放列表";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: true);
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    PlaylistView.ItemsSource = _queue.Items;
                    // 播放列表默认不排序：保持添加顺序（后添加批次在前，批内相对顺序不变）
                    RenumberCollection(_queue.Items);
                    ScrollUserPlaylistToPlaying();
                    break;

                case "Albums":
                    LibraryPaneTitle.Text = "专辑";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Visible;
                    UpdateAlbumSortButtonsUi();
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Visible;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    _ = RefreshAlbumViewAsync();
                    break;

                case "Artists":
                case "AlbumArtists":
                    LibraryPaneTitle.Text = _currentCategory == "AlbumArtists" ? "专辑艺术家" : "艺术家";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Visible;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    _ = RefreshArtistViewAsync();
                    break;

                case "Folders":
                    LibraryPaneTitle.Text = "文件夹";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Visible;
                    SetFolderBrowserMode(isWebDav: false);
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    RefreshFolderBrowserRoots();
                    break;

                case "WebDav":
                    LibraryPaneTitle.Text = "网络音乐库";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Visible;
                    // 同一个「文件夹」界面复用给网络库：台头换名字、藏掉「添加文件夹」（网络库不是加本地目录）
                    SetFolderBrowserMode(isWebDav: true);
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    RefreshWebDavBrowserRoots();
                    break;

                case "PlaylistWall":
                    LibraryPaneTitle.Text = "播放列表";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    if (string.IsNullOrEmpty(_currentPlaylistDetail))
                    {
                        PlaylistWallBorder.Visibility = Visibility.Visible;
                        PlaylistDetailBorder.Visibility = Visibility.Collapsed;
                        ApplyPlaylistWallCategory();
                    }
                    else
                    {
                        PlaylistWallBorder.Visibility = Visibility.Collapsed;
                        PlaylistDetailBorder.Visibility = Visibility.Visible;
                    }

                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    break;

                case "Favorites":
                case "Recent":
                    ApplyFavoritesOrRecentCategory();
                    break;

                case "Ratings":
                    LibraryPaneTitle.Text = "评分";
                    LibraryPaneTitle.Visibility = Visibility.Visible;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    AlbumSortButton.Visibility = Visibility.Collapsed;
                    RatingFilterPanel.Visibility = Visibility.Visible;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    ApplyRatingCategory();
                    break;

                case "MostPlayed":
                    ApplyMostPlayedCategory();
                    break;

                case "Genres":
                case "Years":
                    LibraryPaneTitle.Text = _currentCategory == "Genres" ? "流派" : "年份";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Visible;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    ArtistGridView.Visibility = Visibility.Visible;
                    ArtistDetailPanel.Visibility = Visibility.Collapsed;
                    _ = RefreshGenreYearViewAsync();
                    break;

                case "GenreSongs":
                case "YearSongs":
                    LibraryPaneTitle.Text = (_currentCategory == "GenreSongs" ? "流派：" : "年份：") + (_genreYearFilter ?? "");
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Visible;
                    SetSongSortUiForCategory(isUserPlaylist: false);
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Visible;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    var groupedSongs = _currentCategory == "GenreSongs"
                        ? _playlist
                            .Where(t => string.Equals(t.Genre, _genreYearFilter, StringComparison.OrdinalIgnoreCase)
                                        || (string.IsNullOrWhiteSpace(t.Genre) && _genreYearFilter == "未知流派"))
                            .ToList()
                        : _playlist
                            .Where(t => (t.Year > 0 ? t.Year.ToString() : "未知年份") == _genreYearFilter)
                            .ToList();
                    var groupedCollection = new System.Collections.ObjectModel.ObservableCollection<PlaylistItem>(groupedSongs);
                    RenumberCollection(groupedCollection);
                    PlaylistView.ItemsSource = groupedCollection;
                    break;

                case "AudioFX":
                    // 音效处理 DSP 工作台（EQ / 声道平衡 / 安全限幅）
                    LibraryPaneTitle.Text = "音效处理";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    AudioFxBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    EnsureAudioFxUiBuilt();
                    LoadAudioFxUiFromStore();
                    UpdateDspBitPerfectUi();
                    break;

                case "Settings":
                    // 设置面板（2026-10-12 阶段 0 骨架）：版式与音效处理一致，分类导航由通用子页框架生成
                    LibraryPaneTitle.Text = "设置";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    SettingsBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    EnsureSettingsUiBuilt();
                    break;

                case "TagEdit":
                    // 标签板块（2026-10-13 阶段 5a）：左标签面板 + 右文件列表，子页导航在面板内部
                    LibraryPaneTitle.Text = "标签";
                    LibraryPaneTitle.Visibility = Visibility.Collapsed;
                    MultiSelectTitlePanel.Visibility = Visibility.Collapsed;
                    SongSortPanel.Visibility = Visibility.Collapsed;
                    AlbumSortPanel.Visibility = Visibility.Collapsed;
                    PlaylistListBorder.Visibility = Visibility.Collapsed;
                    AlbumListBorder.Visibility = Visibility.Collapsed;
                    ArtistListBorder.Visibility = Visibility.Collapsed;
                    FolderListBorder.Visibility = Visibility.Collapsed;
                    TagEditBorder.Visibility = Visibility.Visible;
                    CloseAlbumDetailUi();
                    CloseArtistDetailUi();
                    EnsureTagEditUiBuilt();
                    break;
            }

            // 仅「播放队列」分类支持拖拽重排（其它列表保持排序语义不变）
            PlaylistView.CanReorderItems = ReferenceEquals(PlaylistView.ItemsSource, _queue.Items);
            PlaylistView.CanDragItems = PlaylistView.CanReorderItems;

            // 空状态提示：仅 Favorites / Recent / Ratings 自己管理；其余分类隐藏
            if (_currentCategory is not ("Favorites" or "Recent" or "Ratings"))
            {
                SetPlaylistEmptyHint(false, string.Empty);
            }

            UpdateUserPlaylistActionBarVisibility();
            UpdateLibrarySearchUi();
        }

        /// <summary>
        /// 切到「播放队列」页面时，把列表滚到正在播放的那首歌并高亮它。
        /// ItemsSource 刚赋值时 ListView 还没完成布局，ScrollIntoView 会静默失效，
        /// 所以用 DispatcherQueue 延迟到布局完成后执行；无正在播放的曲目则不滚动。
        /// </summary>
        private void ScrollUserPlaylistToPlaying()
        {
            if (PlaylistView == null || _queue.Items == null || _queue.Items.Count == 0)
            {
                return;
            }

            int index = _queue.Index;
            if (index < 0 || index >= _queue.Items.Count)
            {
                return;
            }

            PlaylistItem playing = _queue.Items[index];
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.Equals(_currentCategory, "UserPlaylist", StringComparison.Ordinal))
                {
                    return;
                }

                PlaylistView.SelectedIndex = index;
                PlaylistView.ScrollIntoView(playing);
            });
        }


        private async Task RefreshGenreYearViewAsync()
        {
            List<ArtistEntry> entries = await Task.Run(() =>
            {
                if (_currentCategory == "Genres")
                {
                    return _playlist
                        .GroupBy(t => string.IsNullOrWhiteSpace(t.Genre) || t.Genre == "未知流派" ? "未知流派" : t.Genre)
                        .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                        .Select(g => new ArtistEntry { Name = g.Key, TrackCount = g.Count() })
                        .ToList();
                }

                return _playlist
                    .GroupBy(t => t.Year > 0 ? t.Year.ToString() : "未知年份")
                    .OrderByDescending(g => g.Key == "未知年份"
                        ? -1
                        : int.TryParse(g.Key, out int year) ? year : -1)
                    .Select(g => new ArtistEntry { Name = g.Key, TrackCount = g.Count() })
                    .ToList();
            });

            if (_currentCategory is "Genres" or "Years")
            {
                ArtistGridView.ItemsSource = entries;
                RefreshPlaylistSelectionChrome();
            }
        }


        private static bool IsSupportedAudioFile(string path)
        {
            string ext = Path.GetExtension(path);
            foreach (string supported in AudioExtensions)
            {
                if (string.Equals(ext, supported, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }


        /// <summary>右键详情区歌曲：非多选态弹歌曲菜单；多选态进入多选。</summary>
        private void MediaDetailsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var fe = e.OriginalSource as FrameworkElement;
            var song = (fe?.DataContext ?? (sender as ListView)?.SelectedItem) as PlaylistItem;
            if (song == null)
            {
                return;
            }

            if (IsMultiSelectSelection(MediaDetailsList))
            {
                // 已进入多选：右键加入选中
                if (!MediaDetailsList.SelectedItems.Contains(song))
                {
                    MediaDetailsList.SelectedItems.Add(song);
                }

                return;
            }

            // 右键：弹出该歌曲的选项菜单（多选作为其中一项，点多选才进入多选态）
            var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
            PlaylistItem songRef = song;

            var play = new MenuFlyoutItem { Text = songRef.IsRemote ? "下载并播放" : "播放" };
            play.Icon = new FontIcon { Glyph = "\uE768" };
            play.Click += (_, _) =>
            {
                if (songRef.IsRemote)
                {
                    // 网络曲目：本地还没有这个文件，先下载再播
                    _ = PlayWebDavItemAsync(songRef);
                    return;
                }

                PlaylistItem? track = EnsureTrackInLibrary(songRef.FilePath);
                if (track != null)
                {
                    PlayPlaylistItem(track);
                }
            };
            flyout.Items.Add(play);

            // 下面几项都要求文件已经在本地磁盘上，网络曲目（未下载）点不动，干脆不显示。
            if (!songRef.IsRemote)
            {
                var add = new MenuFlyoutItem { Text = "加入播放队列" };
                add.Icon = new FontIcon { Glyph = "\uE710" };
                add.Click += (_, _) => AddToUserPlaylistBack(songRef);
                flyout.Items.Add(add);

                var edit = new MenuFlyoutItem { Text = "标签编辑" };
                edit.Icon = new FontIcon { Glyph = "\uE8D2" };
                edit.Click += (_, _) => OpenTagEditForFiles(new[] { songRef.FilePath });
                flyout.Items.Add(edit);

                var del = new MenuFlyoutItem { Text = "从媒体库中删除" };
                del.Icon = new FontIcon { Glyph = "\uE74D" };
                del.Click += (_, _) => _ = DeleteMediaSongWithConfirmAsync(songRef);
                flyout.Items.Add(del);
            }

            var multi = new MenuFlyoutItem { Text = "多选" };
            multi.Icon = new FontIcon { Glyph = "\uE700" };
            multi.Click += (_, _) =>
            {
                if (MediaDetailsList != null)
                {
                    MediaDetailsList.SelectionMode = ListViewSelectionMode.Multiple;
                    AttachRangeMultiSelect(MediaDetailsList);
                    MediaDetailsList.SelectedItems.Clear();
                    MediaDetailsList.SelectedItems.Add(songRef);
                    RefreshMediaSongSelectionChrome();
                }

                if (MediaOptionsButton != null)
                {
                    MediaOptionsButton.Visibility = Visibility.Visible;
                }

                if (MediaExitMultiSelectButton != null)
                {
                    MediaExitMultiSelectButton.Visibility = Visibility.Visible;
                }
            };
            flyout.Items.Add(multi);

            flyout.ShowAt(MediaDetailsList ?? (FrameworkElement)sender, e.GetPosition(MediaDetailsList ?? (FrameworkElement)sender));
            e.Handled = true;
        }


        /// <summary>双击详情区歌曲：直接播放（网络曲目先下载）。</summary>
        private void MediaDetailsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var song = (e.OriginalSource as FrameworkElement)?.DataContext as PlaylistItem;
            if (song == null)
            {
                return;
            }

            if (song.IsRemote)
            {
                _ = PlayWebDavItemAsync(song);
                e.Handled = true;
                return;
            }

            PlaylistItem? track = EnsureTrackInLibrary(song.FilePath);
            if (track != null)
            {
                PlayPlaylistItem(track);
            }

            e.Handled = true;
        }


        /// <summary>详情区歌曲行选中 chrome：主题色圆角背景（去掉多选框）。</summary>
        private void MediaDetailsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is PlaylistItem song && args.ItemContainer is ListViewItem container)
            {
                ApplyMediaSongSelectionChrome(sender as ListView, container, song);
            }
        }


        private void MediaDetailsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshMediaSongSelectionChrome();
        }


        /// <summary>退出媒体库多选：恢复单选并隐藏选项按钮。</summary>
        private void ExitMediaMultiSelect()
        {
            if (MediaDetailsList != null)
            {
                MediaDetailsList.SelectionMode = ListViewSelectionMode.None;
                MediaDetailsList.SelectedItems.Clear();
            }

            if (MediaOptionsButton != null)
            {
                MediaOptionsButton.Visibility = Visibility.Collapsed;
            }

            if (MediaExitMultiSelectButton != null)
            {
                MediaExitMultiSelectButton.Visibility = Visibility.Collapsed;
            }
        }


        /// <summary>显式退出多选按钮。</summary>
        private void MediaExitMultiSelectButton_Click(object sender, RoutedEventArgs e)
            => ExitMediaMultiSelect();

        /// <summary>更新右侧详情列表：中途加载完成/重载时退出多选。</summary>
        private void ResetMediaSelectionUi()
        {
            ExitMediaMultiSelect();
        }


        /// <summary>封面写入磁盘后使缓存失效（下载封面后调用）。</summary>
        internal static void InvalidateCoverCache(string audioPath)
        {
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                CoverBytesCache.TryRemove(audioPath, out _);
            }
        }


        // 2026-10-29（W6 第五小步）：封面源字节提取（TryReadCoverBytes / TryLoadInnerCover /
        // TryLoadOuterCover / TryMeasureImageSize）已整体搬到 CoverSourceReader.cs。
        // 搬出原因：这几段只依赖 TagLib# 与文件 IO，住在主窗口 partial class 里，
        // 任何想复用它们的地方（CoverArtService、跨平台测试宿主）都得拖上整个 WinUI 窗口。
        // 取图规则一字未改：内嵌优先、内嵌多图取像素最大的、外置 folder/cover/同名兜底。


        /// <summary>
        /// 当前播放条目的 CUE 分轨区间（文件时间）。非分轨返回 (0, 0)。
        /// 用于变速重载等"按路径重起"的路径：必须带上区间，否则会把整轨从头播。
        /// </summary>
        private (double Start, double Length) CurrentCueRange()
        {
            PlaylistItem? item = null;
            if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count
                && string.Equals(_queue.Items[_queue.Index].FilePath, _nowPlayingPath, StringComparison.OrdinalIgnoreCase))
            {
                item = _queue.Items[_queue.Index];
            }
            else if (_currentIndex >= 0 && _currentIndex < _playlist.Count
                && string.Equals(_playlist[_currentIndex].FilePath, _nowPlayingPath, StringComparison.OrdinalIgnoreCase))
            {
                item = _playlist[_currentIndex];
            }

            if (item == null || item.StartTimeSeconds <= 0)
            {
                return (0, 0);
            }

            return (item.StartTimeSeconds, Math.Max(0, item.Duration.TotalSeconds));
        }


        private void SortOrderButton_Click(object sender, RoutedEventArgs e)
        {
            _sortAscending = !_sortAscending;
            SortOrderText.Text = _sortAscending ? "升序" : "降序";

            string? playingPath = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? _playlist[_currentIndex].FilePath
                : null;
            ApplySort(playingPath);
        }


        private void UpdateCurrentIndexByPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (int i = 0; i < _playlist.Count; i++)
            {
                if (string.Equals(_playlist[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentIndex = i;
                    return;
                }
            }
        }


        // =====================================================================
        // 播放列表交互 / 右键菜单 / 多选
        // =====================================================================

        /// <summary>主内容区空白点击（非歌曲项）取消当前选中，从主题色背景恢复常态。</summary>
        private void MainContentGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Microsoft.UI.Xaml.DependencyObject? origin = e.OriginalSource as Microsoft.UI.Xaml.DependencyObject;
            while (origin != null)
            {
                if (origin is ListViewItem or GridViewItem)
                {
                    return;
                }

                origin = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(origin);
            }

            // 点空白：取消各歌曲列表的选中
            if (!_isMultiSelectMode)
            {
                PlaylistView.SelectedItem = null;
                if (MediaDetailsList != null && !IsMultiSelectSelection(MediaDetailsList))
                {
                    MediaDetailsList.SelectedItem = null;
                }
            }
        }


        private void SelectAllMultiSelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isMultiSelectMode)
            {
                return;
            }

            if (_multiSelectAlbumGrid != null)
            {
                GridView grid = _multiSelectAlbumGrid;
                ObservableCollection<AlbumEntry> allAlbums = GetAlbumCollectionForGrid(grid);
                bool allSelected = allAlbums.Count > 0
                    && grid.SelectedItems.Count >= allAlbums.Count;
                if (allSelected)
                {
                    ClearListViewBaseSelection(grid);
                }
                else
                {
                    SelectAllInListViewBase(grid, allAlbums.Count);
                }

                RefreshAlbumWallSelectionChrome(grid, allAlbums);
                UpdateSelectAllMultiSelectButtonState();
                return;
            }

            if (_multiSelectFolderList != null)
            {
                bool allSelected = _folderBrowserItems.Count > 0
                    && FolderBrowserView.SelectedItems.Count >= _folderBrowserItems.Count;
                if (allSelected)
                {
                    ClearListViewBaseSelection(FolderBrowserView);
                }
                else
                {
                    SelectAllInListViewBase(FolderBrowserView, _folderBrowserItems.Count);
                }

                RefreshFolderBrowserSelectionChrome();
                UpdateSelectAllMultiSelectButtonState();
                return;
            }

            ListView? target = _multiSelectTargetList;
            if (target == null)
            {
                return;
            }

            IReadOnlyList<PlaylistItem> allSongs = GetMultiSelectSongSource(target);
            bool songsAllSelected = target.SelectedItems.Count >= allSongs.Count && allSongs.Count > 0;
            if (songsAllSelected)
            {
                ClearListViewBaseSelection(target);
            }
            else
            {
                SelectAllInListViewBase(target, allSongs.Count);
            }

            RefreshSongListSelectionChrome(target);
            UpdateSelectAllMultiSelectButtonState();
        }


        /// <summary>用 SelectRange 一次选中全部，避免逐项 Add 触发数千次 SelectionChanged。</summary>
        private void SelectAllInListViewBase(ListViewBase list, int count)
        {
            if (count <= 0)
            {
                return;
            }

            _suppressSelectionUiUpdates = true;
            try
            {
                list.SelectRange(new ItemIndexRange(0, (uint)count));
            }
            catch
            {
                // 少数控件/状态不支持 SelectRange 时回退逐项添加
                for (int i = 0; i < count; i++)
                {
                    object? item = list.Items[i];
                    if (item == null || list.SelectedItems.Contains(item))
                    {
                        continue;
                    }

                    try
                    {
                        list.SelectedItems.Add(item);
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
                }
            }
            finally
            {
                _suppressSelectionUiUpdates = false;
            }
        }


        private void ClearListViewBaseSelection(ListViewBase list)
        {
            _suppressSelectionUiUpdates = true;
            try
            {
                if (list is ListView lv)
                {
                    SetListSelectionMode(lv, ListViewSelectionMode.Multiple);
                }
                else if (list is GridView gv)
                {
                    SetGridSelectionMode(gv, ListViewSelectionMode.Multiple);
                }
                else
                {
                    list.SelectedIndex = -1;
                }
            }
            finally
            {
                _suppressSelectionUiUpdates = false;
            }
        }


        private void ExitMultiSelectButton_Click(object sender, RoutedEventArgs e)
            => ExitMultiSelectMode();

        /// <summary>列表当前是否处于「可多选」的选择模式（Multiple / Extended 均算）。
        /// 旧代码里散落的「== ListViewSelectionMode.Multiple」判断统一走这里，避免漏改。</summary>
        internal static bool IsMultiSelectSelection(ListViewBase? list)
            => RangeMultiSelect.IsMultiSelectMode(list);

        /// <summary>给列表挂上 Shift 连选一段 / Ctrl 跳选（转发到共用实现 RangeMultiSelect）。</summary>
        internal static void AttachRangeMultiSelect(ListViewBase? list)
            => RangeMultiSelect.Attach(list);

        /// <summary>
        /// 通过 None 中转切换选择模式，安全清空选中项，避免 SelectedItems.Clear 崩溃。
        /// </summary>
        private void SetListSelectionMode(ListView list, ListViewSelectionMode mode)
        {
            try
            {
                list.SelectionMode = ListViewSelectionMode.None;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                list.SelectionMode = mode;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 本应用用主题色圆角底表示选中，关闭 Multiple 模式左侧系统复选框（否则会显示小黑块）
            list.IsMultiSelectCheckBoxEnabled = false;

            try
            {
                list.SelectedItem = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            AttachRangeMultiSelect(list);
        }


        private void SetGridSelectionMode(GridView grid, ListViewSelectionMode mode)
        {
            try
            {
                grid.SelectionMode = ListViewSelectionMode.None;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                grid.SelectionMode = mode;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            grid.IsMultiSelectCheckBoxEnabled = false;

            try
            {
                grid.SelectedItem = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            AttachRangeMultiSelect(grid);
        }


        private void MultiSelectFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            foreach (PlaylistItem song in selected)
            {
                TrackStatsStore.SetFavorite(song.FilePath, true);
            }

            NamedPlaylistStore.SyncFavoritesPlaylist();
            UpdateFavoriteButtonUi();
            NowPlayingText.Text = $"已收藏 {selected.Count} 首歌曲";
            if (string.Equals(_currentCategory, "Favorites", StringComparison.Ordinal))
            {
                ApplyCategoryView();
            }

            ExitMultiSelectMode();
        }


        private async void MultiSelectDownloadCoverButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {

            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            int ok = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                PlaylistItem song = selected[i];
                NowPlayingText.Text = $"正在下载封面 ({i + 1}/{selected.Count})…";
                if (await OnlineMusicApi.DownloadAndEmbedCoverAsync(song.Title, song.Artist, song.FilePath))
                {
                    ok++;
                }
            }

            NowPlayingText.Text = $"封面下载完成：{ok}/{selected.Count}";
        
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }}


        private void MultiSelectCopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedMultiSelectSongs();
            if (selected.Count == 0)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            foreach (PlaylistItem song in selected)
            {
                sb.AppendLine(song.FilePath);
            }

            var data = new DataPackage();
            data.SetText(sb.ToString().TrimEnd());
            Clipboard.SetContent(data);
            NowPlayingText.Text = $"已复制 {selected.Count} 个文件位置";
        }


        private static bool MoveToRecycleBin(string path)
        {
            try
            {
                SHFILEOPSTRUCT op = new()
                {
                    wFunc = 3, // FO_DELETE
                    pFrom = path + "\0\0",
                    fFlags = 0x40 | 0x10 | 0x04 // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
                };
                return SHFileOperation(ref op) == 0;
            }
            catch
            {
                return false;
            }
        }


        private Brush ResolveNavCapsuleBorderBrush()
        {
            // 经典界面：Application.Resources 里的这些键取到的是"系统主题"那一份色值，
            // 与经典模式选的浅色/深色不一定一致（跟随系统时更明显），所以这里直接按经典主题给色。
            if (FrostedGlass.ClassicMode != null)
            {
                return new SolidColorBrush(FrostedGlass.ClassicMode == "Light"
                    ? Color.FromArgb(28, 0, 0, 0)
                    : Color.FromArgb(46, 255, 255, 255));
            }

            if (Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? card)
                && card is Brush c)
            {
                return c;
            }

            return new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        }


        private Brush ResolveCapsuleFillBrush()
        {
            // 同上：经典界面下按经典主题给一套明确的中性底色
            if (FrostedGlass.ClassicMode != null)
            {
                return new SolidColorBrush(FrostedGlass.ClassicMode == "Light"
                    ? Color.FromArgb(16, 0, 0, 0)
                    : Color.FromArgb(26, 255, 255, 255));
            }

            if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out object? brushObj)
                && brushObj is Brush brush)
            {
                return brush;
            }

            if (Application.Current.Resources.TryGetValue("ControlFillColorDefaultBrush", out object? controlBrush)
                && controlBrush is Brush c)
            {
                return c;
            }

            return new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        }


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


        private static IEnumerable<ListViewItem> EnumerateRealizedListViewItems(ListView list)
        {
            Panel? panel = VisualTreeWalker.FindItemsPanel(list);
            if (panel == null)
            {
                yield break;
            }

            foreach (UIElement child in panel.Children)
            {
                if (child is ListViewItem item)
                {
                    yield return item;
                }
            }
        }


        private static IEnumerable<GridViewItem> EnumerateRealizedGridViewItems(GridView grid)
        {
            Panel? panel = VisualTreeWalker.FindItemsPanel(grid);
            if (panel == null)
            {
                yield break;
            }

            foreach (UIElement child in panel.Children)
            {
                if (child is GridViewItem item)
                {
                    yield return item;
                }
            }
        }


        /// <summary>在容器已加载后关闭系统选中勾（避免与词条文字重叠）。</summary>
        private void DisableContainerSelectionCheckMark(Control container)
        {
            SoftenItemPresenterCorners(container);
            if (!container.IsLoaded)
            {
                void OnLoaded(object sender, RoutedEventArgs e)
                {
                    container.Loaded -= OnLoaded;
                    SoftenItemPresenterCorners(container);
                }

                container.Loaded += OnLoaded;
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => SoftenItemPresenterCorners(container));
            }
        }


        /// <summary>保存全部状态后重启播放器(用于主题色等需重启生效的更改)。</summary>
        internal void RestartApp()
        {
            try
            {
                TrackStatsStore.Flush();
                // 重启前写盘：同样优先用"用户最后一次主动设定的音量"，避免被未同步的滑条值覆盖
                try
                {
                    _volumeSaveTimer?.Stop();
                    double sliderVolume = VolumeSlider != null ? Math.Clamp(VolumeSlider.Value, 0, 100) : _volumeToSave;
                    double liveVolume = Math.Clamp(LastUserVolume >= 0 ? LastUserVolume : sliderVolume, 0, 100);
                    AppSettingsStore.Update(s => s.Volume = liveVolume);
                    global::CelesteMusicPlayer.StartupLog.Write($"[音量] 重启写盘 = {liveVolume:0.##}（滑条={sliderVolume:0.##}，用户设定={LastUserVolume:0.##}）");
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Misc.cs", caught); }
                PersistPlaybackSession();

                string? exe = Environment.ProcessPath
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true,
                        WorkingDirectory = string.IsNullOrWhiteSpace(System.IO.Path.GetDirectoryName(exe))
                            ? string.Empty
                            : System.IO.Path.GetDirectoryName(exe)!
                    });
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 直接退出当前进程(由新进程接管,绕过托盘/关闭提示逻辑)
            Environment.Exit(0);
        }


        private bool _exitStarted;
        private void ExitApplication()
        {
            if (_exitStarted) return;
            _exitStarted = true;
            // 先标记允许关闭，避免任何 Closing 处理器再弹“最小化到托盘还是退出”的对话框
            _allowClose = true;

            try { _taskbarPlayer?.Dispose(); _taskbarPlayer = null; }
            catch (Exception ex) { StartupLog.WriteException("TaskbarPlayer.Exit", ex); }

            // 立刻销毁托盘/任务栏图标：用户一点“退出”，图标马上消失
            try
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try
            {
                _taskbarButtons?.Dispose();
                _taskbarButtons = null;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            // 持久化关键状态（干净退出标记 / 续播进度），失败也不影响退出
            try { AppSettingsStore.MarkAppCleanExit(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
            try { PersistPlaybackSession(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            try { Close(); } catch (Exception ex) { StartupLog.WriteException("MainWindow.ExitCleanup", ex); }

            // 直接、强制结束进程。不调用 Close()/Application.Current.Exit()，
            // 因为它们在 WinUI3 里可能卡在窗口拆除或资源释放上几秒，导致
            // “托盘图标已没、主程序却还停好几秒”。Environment.Exit(0) 是进程级强杀，
            // 立即终止所有线程与窗口，干净利落。
            Environment.Exit(0);
        }


        /// <summary>读取并显示当前曲目的音频格式信息（采样率/位深/码率/声道）。</summary>
        private async System.Threading.Tasks.Task UpdateAudioInfoTextAsync(string path)
        {
            try
            {
                string? info = await System.Threading.Tasks.Task.Run(() => AudioInfoFormatter.Format(path));
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (NowPlayingAudioInfoText != null && string.Equals(_nowPlayingPath, path, System.StringComparison.OrdinalIgnoreCase))
                    {
                        NowPlayingAudioInfoText.Text = info ?? string.Empty;
                    }
                });
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }



        /// <summary>未播放时的静态频谱形状（始终可见，中间高两边低）。</summary>
        private static double IdleLevel(int index)
        {
            double shape = 0.22 + 0.28 * (0.5 + 0.5 * Math.Sin(index * 1.7 + 1.3));
            return Math.Max(0.18, shape * FormatHelper.SpectrumEnvelope(index));
        }


        /// <summary>波形倒影的画刷缓存（按序号缓存渐变画刷，避免每帧重建 40 个画刷造成 GC 抖动）。
        /// 颜色变化（换主题）时自动重建。</summary>
        private readonly LinearGradientBrush?[] _waveReflBrushes = new LinearGradientBrush?[WaveBarCount];
        private readonly Color[] _waveReflColors = new Color[WaveBarCount];

        /// <summary>取（并缓存）某个柱子的倒影渐变画刷：顶部有颜色 → 底部完全透明，模拟水面渐隐。</summary>
        private Brush ReflectionBrushFor(int index, Color accent)
        {
            if (_waveReflBrushes[index] is LinearGradientBrush cached && _waveReflColors[index] == accent)
            {
                return cached;
            }

            // 水面线处约 40% 不透明度，向下分三段平滑收到全透明 —— 比原来的"整块固定 92 alpha"
            // 自然得多：既不会在倒影底部出现一条生硬的截止边，也不会紧贴水面线时显得"糊成一块"。
            // （用户反馈：波形倒影太突兀 → 降起始不透明度 + 多加一档中间色标，让衰减更渐进。）
            var brush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1)
            };
            brush.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(100, accent.R, accent.G, accent.B),
                Offset = 0.0
            });
            brush.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(52, accent.R, accent.G, accent.B),
                Offset = 0.32
            });
            brush.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(16, accent.R, accent.G, accent.B),
                Offset = 0.68
            });
            brush.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(0, accent.R, accent.G, accent.B),
                Offset = 1.0
            });

            _waveReflBrushes[index] = brush;
            _waveReflColors[index] = accent;
            return brush;
        }



        /// <summary>引擎开播淡入（约 320ms 渐入到当前音量）。</summary>
        private async Task FadeInEngineAsync()
        {
            try
            {
                // 独占/ASIO（bit-perfect）：设备主音量由用户拖动音量条设置，切歌不重置（避免拉回 100%）。
                if (IsHiFiModeSelected())
                {
                    return;
                }

                // 共享用「用户持久化的真实音量」（防切模式后滑块被独占锁成 100% 导致巨声）。
                // 共享引擎音量 = NAudio 软件增益 0..1。
                double target = Math.Clamp(AppSettingsStore.Load().Volume, 0, 100) / 100.0;
                const int steps = 8;
                for (int i = 1; i <= steps; i++)
                {
                    _audioEngine?.SetVolume(target * i / steps);
                    await Task.Delay(40);
                }

                _audioEngine?.SetVolume(target);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }
        }


        private void HandleMediaEnded()
        {
            if (ConsumeSleepStopIfDue())
            {
                return;
            }

            switch (_orderResolver.Order)
            {
                case PlaybackOrder.TrackLoop:
                    // IsLooping 为 true 时通常不会触发；兜底再播当前曲
                    if (_queue.Index >= 0 && _queue.Index < _queue.Items.Count)
                    {
                        PlayUserPlaylistAt(_queue.Index);
                    }
                    break;

                case PlaybackOrder.TrackOnce:
                    break;

                case PlaybackOrder.Sequential:
                    PlayNext(autoAdvance: true);
                    break;

                default:
                    PlayNext(autoAdvance: true);
                    break;
            }
        }
    }
}
