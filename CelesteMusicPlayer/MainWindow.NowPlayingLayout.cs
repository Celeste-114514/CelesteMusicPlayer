using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Playback;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 播放信息页（沉浸式播放页）的布局样式 —— 每一种都是"看音乐的一种方式"，不只是换皮：
    ///   Classic 经典 —— 封面 + 信息在左，歌词在右（原有版式，默认）；
    ///   Water   水面 —— 封面倒影与波形倒影共用一条水面线；
    ///   Vinyl   黑胶 —— 旋转唱片 + 唱臂落下/抬起；
    ///   Stage   剧场 —— 三栏：左歌词 / 中封面+频谱 / 右信息（封面成为视觉中心）；
    ///   Lyrics  歌词 —— 小封面在左上，歌词占满右侧，歌词是绝对主角；
    ///   Mirror  镜像 —— 封面与信息在右、歌词在左（经典的左右镜像，视觉重心反过来）；
    ///   Center  居中 —— 封面在上，信息、歌词依次居中往下（上下结构，窗口缩放最稳）。
    /// 默认始终是 Classic，新布局只在用户显式选择后生效，保证不会把默认体验弄坏、随时可切回。
    /// </summary>
    public sealed partial class MainWindow
    {
        internal const string LayoutClassic = "Classic";
        internal const string LayoutWater = "Water";
        internal const string LayoutVinyl = "Vinyl";
        internal const string LayoutStage = "Stage";
        internal const string LayoutLyrics = "Lyrics";
        internal const string LayoutMirror = "Mirror";
        internal const string LayoutCenter = "Center";

        private bool _nowPlayingLayoutHooked;

        /// <summary>当前布局名。给"需要按具体布局分支"的代码用（比一堆 bool 好读）。</summary>
        private string _nowPlayingLayoutName = LayoutClassic;

        /// <summary>当前是否为「水面」布局。缓存下来供 UpdateNowPlayingCardLayout 用，
        /// 避免在窗口尺寸变化的路径上反复读设置。</summary>
        private bool _layoutIsWater;

        /// <summary>当前是否为「黑胶」布局。</summary>
        private bool _layoutIsVinyl;

        /// <summary>当前是否为「剧场」（三栏）布局。</summary>
        private bool _layoutIsStage;

        /// <summary>当前是否为「歌词」布局。</summary>
        private bool _layoutIsLyrics;

        /// <summary>当前是否为「镜像」布局。</summary>
        private bool _layoutIsMirror;

        /// <summary>当前是否为「居中」布局。</summary>
        private bool _layoutIsCenter;

        /// <summary>
        /// 读取设置里的播放页布局并套用。触发时机：启动、进入播放页、以及设置变更（AppSettingsStore.Changed）。
        /// 未知取值一律回退「经典」，避免配置异常把界面弄坏。
        /// </summary>
        internal void ApplyNowPlayingLayout()
        {
            try
            {
                string layout = AppSettingsStore.Load().NowPlayingLayout;
                if (!IsKnownLayout(layout))
                {
                    layout = LayoutClassic;
                }

                bool water = layout == LayoutWater;
                bool vinyl = layout == LayoutVinyl;
                bool stage = layout == LayoutStage;
                bool lyrics = layout == LayoutLyrics;
                bool mirror = layout == LayoutMirror;
                bool center = layout == LayoutCenter;
                bool layoutChanged = _layoutIsWater != water || _layoutIsVinyl != vinyl
                                     || _layoutIsStage != stage || _layoutIsLyrics != lyrics
                                     || _layoutIsMirror != mirror || _layoutIsCenter != center
                                     || _nowPlayingLayoutName != layout;
                _layoutIsWater = water;
                _layoutIsVinyl = vinyl;
                _layoutIsStage = stage;
                _layoutIsLyrics = lyrics;
                _layoutIsMirror = mirror;
                _layoutIsCenter = center;
                _nowPlayingLayoutName = layout;

                // 封面倒影：仅「水面」布局显示
                if (NowPlayingCoverReflection != null)
                {
                    NowPlayingCoverReflection.Visibility = water
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                // 黑胶唱机：显示唱盘；此时原来那一列（封面 + 倒影 + 波形）整体让位。
                if (VinylStage != null)
                {
                    VinylStage.Visibility = vinyl ? Visibility.Visible : Visibility.Collapsed;
                }
                if (NowPlayingCoverColumn != null)
                {
                    NowPlayingCoverColumn.Visibility = vinyl ? Visibility.Collapsed : Visibility.Visible;
                }
                if (WaveformCanvas != null)
                {
                    // 波形（频谱）只在「经典 / 水面 / 剧场 / 镜像」里出现：
                    // 黑胶右半区要放信息和歌词；歌词布局要的是大歌词；居中布局是纯上下结构。
                    bool showWave = !vinyl && !lyrics && !center;
                    WaveformCanvas.Visibility = showWave ? Visibility.Visible : Visibility.Collapsed;
                }

                bool classic = layout == LayoutClassic;

                // 非「经典」布局只留 标题 / 艺术家 / 专辑 三行：
                // 文件格式、位深、信号链这些技术信息只在经典布局显示，免得破坏沉浸感。
                if (NowPlayingAudioInfoText != null)
                {
                    NowPlayingAudioInfoText.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
                }

                if (SignalChainInfoText != null)
                {
                    SignalChainInfoText.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
                }

                // 「水面」布局的封面：完整正方形、无圆角。
                // Stretch=Uniform 完整显示（长方形封面不裁切、居中、上下或左右留白透明）——
                // 用户要求长方形封面也要显示完整；倒影生成端用同一规则取"实际显示区域"的底边镜像，
                // 并按留白量把倒影上移贴住封面底边（见 UpdateNowPlayingReflectionGeometry）。
                // 水面布局去掉 1px 边框：避免可见图片被内缩 1px 造成倒影相对封面偏移。
                if (NowPlayingCoverBorder != null)
                {
                    NowPlayingCoverBorder.CornerRadius = water ? new CornerRadius(0) : new CornerRadius(10);
                    NowPlayingCoverBorder.BorderThickness = water ? new Thickness(0) : new Thickness(1);
                }

                if (NowPlayingCoverImage != null)
                {
                    NowPlayingCoverImage.Stretch = Stretch.Uniform;
                }

                // 标题：水面用主题强调色（AccentTextFillColorPrimaryBrush 由 ThemeColorService 注入），
                // 经典恢复为标准主文本色。信息块本身不做倒影。
                if (NowPlayingTitleText != null)
                {
                    Brush titleBrush;
                    if ((water || stage || lyrics || mirror || center) && Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out object? accentObj) && accentObj is Brush ab)
                    {
                        titleBrush = ab;
                    }
                    else if (Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out object? textObj) && textObj is Brush tb)
                    {
                        titleBrush = tb;
                    }
                    else
                    {
                        titleBrush = NowPlayingTitleText.Foreground ?? new SolidColorBrush(Colors.White);
                    }
                    NowPlayingTitleText.Foreground = titleBrush;
                }

                // 切换布局时同步播放条样式（水面内嵌 / 经典悬浮）
                ApplyFloatingBarStyle();

                // 歌词的"水面感"不在这里做：它靠"非当前行按距离衰减透明度"实现，
                // 见 MainWindow.Playback3.cs 的歌词高亮循环（那边本来就按距离分段调透明度）。
                // 这里不额外做遮罩 —— WinUI 3 没有 OpacityMask。

                if (NowPlayingLayoutButtonText != null)
                {
                    NowPlayingLayoutButtonText.Text = "布局：" + LayoutDisplayName(layout);
                }

                if (water)
                {
                    // 播着歌直接切到「水面」时倒影还是空的：用当前封面补生成一张，
                    // 否则要等下一首歌才会出现倒影。
                    SyncNowPlayingReflection(_lastCoverBytes);
                }
                else if (vinyl)
                {
                    // 同理：切到「黑胶」时用当前封面补一次唱片中心标签。
                    _ = UpdateVinylArtFromBytesAsync(_lastCoverBytes);
                    SyncVinylMotion();
                }
                else
                {
                    // 离开黑胶布局：唱片停下、唱臂抬起，别让动画在后台空转。
                    StopVinylMotion();
                }

                if (layoutChanged)
                {
                    // 布局变了 → 封面尺寸预算也变（水面要给倒影留高度），重算一次。
                    // 面板尚未布局时该方法会自行早退，不必额外判断。
                    UpdateNowPlayingCardLayout();
                    UpdateNowPlayingReflectionGeometry();
                }

                if (layoutChanged && _lastCoverBytes is { Length: > 0 })
                {
                    // 切布局要重刷一次背景：除「经典」外的布局都固定带一层轻模糊，
                    // 不重刷的话当前这张背景还是按上一个布局的半径生成的。
                    _ = ApplyAlbumArtBackgroundAsync(_lastCoverBytes, _nowPlayingPath ?? string.Empty);
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.ApplyNowPlayingLayout", caught);
            }
        }

        internal static bool IsKnownLayout(string? layout)
            => layout == LayoutClassic || layout == LayoutWater || layout == LayoutVinyl
               || layout == LayoutStage || layout == LayoutLyrics || layout == LayoutMirror
               || layout == LayoutCenter;

        internal static string LayoutDisplayName(string layout) => layout switch
        {
            LayoutWater => "水面",
            LayoutVinyl => "黑胶",
            LayoutStage => "剧场",
            LayoutLyrics => "歌词",
            LayoutMirror => "镜像",
            LayoutCenter => "居中",
            _ => "经典"
        };


        /// <summary>当前倒影贴图的实际像素尺寸（512 坐标系）。正方形封面 dispH=512；
        /// 长方形封面按 Uniform 完整显示后更小。由 SyncNowPlayingReflectionAsync 生成贴图时更新。</summary>
        private double _reflectionDispW = NowPlayingReflection.CanvasSize;
        private double _reflectionDispH = NowPlayingReflection.CanvasSize;
        private double _reflectionStripH = NowPlayingReflection.CanvasSize * NowPlayingReflection.HeightRatio;

        /// <summary>对齐探针只挂一次（见 WireReflectionAlignProbe）。</summary>
        private bool _reflectionProbeWired;

        /// <summary>
        /// 倒影几何：容器尺寸 = 倒影贴图实际像素 × (封面宽 / 512)，与贴图严格等比，
        /// 保证 Stretch=Uniform 正好铺满（不 letterbox——这是此前左右缺 1px 的根因）。
        /// 长方形封面完整显示时上下（或左右）有留白：容器位置/宽度跟随"实际显示区域"，
        /// 并把倒影上移贴住封面实际显示的底边（否则倒影和封面之间会隔一条留白）。
        /// </summary>
        internal void UpdateNowPlayingReflectionGeometry()
        {
            try
            {
                if (NowPlayingCoverReflection == null || NowPlayingCoverReflectionImage == null)
                {
                    return;
                }

                double cover = NowPlayingCoverBorder?.Width ?? 0;
                if (cover <= 0)
                {
                    return;
                }

                double unit = cover / (double)NowPlayingReflection.CanvasSize;
                // 尺寸取整：容器高是"贴图高 × 比例"，天然带小数（如 110 × 0.82 = 90.23）。
                // 保留小数 + Image.Stretch=Uniform 会让图片按取整后的高度等比缩放，横向填不满容器
                // （左右各露约 0.5 逻辑像素的透明边，高 DPI 下就是 1~2 个物理像素的"缺缝"）。
                // 现在：XAML 侧改用 UniformToFill 保证横向铺满，这里再把尺寸取整让渲染落在整像素上。
                double width = Math.Round(_reflectionDispW * unit);
                double height = Math.Round(_reflectionStripH * unit);

                // 水平定位不再交给"居中"：封面列宽度已固定为 cover、封面左对齐到 x=0，
                // 这里显式左对齐 + 内缩 (cover-width)/2 —— 正方形封面内缩 0（左边缘与封面严格重合），
                // 长方形封面内缩后与"封面实际显示区域"（Uniform 居中）严格重合。
                // 之前的 Center 方案在宽度带小数 + 布局取整时，两边取整方向可能不一致，会左右各差 1px。
                double leftInset = Math.Round((cover - width) / 2.0);
                NowPlayingCoverReflection.Width = width;
                NowPlayingCoverReflection.Height = height;
                NowPlayingCoverReflection.HorizontalAlignment = HorizontalAlignment.Left;

                // 上移量 = 底部留白（(512-dispH)/2 × unit，正方形封面为 0）+ 抵消 StackPanel Spacing 10
                double bottomWhitespace = (NowPlayingReflection.CanvasSize - _reflectionDispH) / 2.0 * unit;
                NowPlayingCoverReflection.Margin = new Thickness(leftInset, -(10 + bottomWhitespace), 0, 0);

                // 不再设 Clip：容器比例与贴图严格一致，Image 的 Uniform 会正好铺满，
                // 额外的裁剪矩形在宽度带小数时反而可能削掉最右/最左一列像素。
                NowPlayingCoverReflection.Clip = null;

                WireReflectionAlignProbe();

                StartupLog.Write(
                    $"[ReflectionGeom] cover={cover:0.##} width={width:0.##} height={height:0.##} " +
                    $"leftInset={leftInset:0.##} dispW={_reflectionDispW} dispH={_reflectionDispH} stripH={_reflectionStripH} " +
                    $"panelW={NowPlayingCoverColumn?.ActualWidth:0.##} coverW={NowPlayingCoverBorder?.ActualWidth:0.##} " +
                    $"coverX={TransformedX(NowPlayingCoverBorder):0.##} reflX={TransformedX(NowPlayingCoverReflection):0.##}");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateNowPlayingReflectionGeometry", caught);
            }
        }

        /// <summary>元素在 NowPlayingBody 坐标系里的左边界 x（用于诊断"倒影与封面到底哪一层错位"）。</summary>
        private double TransformedX(UIElement? element)
        {
            try
            {
                if (element == null || NowPlayingBody == null)
                {
                    return double.NaN;
                }

                return element.TransformToVisual(NowPlayingBody)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            }
            catch
            {
                return double.NaN;
            }
        }

        /// <summary>元素在 NowPlayingBody 坐标系里的上边界 y（用于验证"封面倒影与波形倒影同一条水面线"）。</summary>
        private double TransformedY(UIElement? element)
        {
            try
            {
                if (element == null || NowPlayingBody == null)
                {
                    return double.NaN;
                }

                return element.TransformToVisual(NowPlayingBody)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            }
            catch
            {
                return double.NaN;
            }
        }

        /// <summary>只挂一次：布局完成后把四个元素的真实位置写进日志，用于定位肉眼看到的错位出在哪一层。</summary>
        private void WireReflectionAlignProbe()
        {
            if (_reflectionProbeWired || NowPlayingCoverReflection == null)
            {
                return;
            }

            _reflectionProbeWired = true;
            NowPlayingCoverReflection.SizeChanged += (_, _) => LogReflectionAlign();
        }

        private void LogReflectionAlign()
        {
            try
            {
                string D(string tag, FrameworkElement? e)
                {
                    if (e == null)
                    {
                        return tag + "=null";
                    }

                    return $"{tag}(x={TransformedX(e):0.##},w={e.ActualWidth:0.##},h={e.ActualHeight:0.##})";
                }

                // 贴图实际像素尺寸：与容器宽高比是否一致，决定了 Image 会不会"填不满"而露出透明边。
                string coverPx = NowPlayingCoverImage?.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapSource cb
                    ? $"{cb.PixelWidth}x{cb.PixelHeight}"
                    : "?";
                string reflPx = NowPlayingCoverReflectionImage?.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapSource rb
                    ? $"{rb.PixelWidth}x{rb.PixelHeight}"
                    : "?";

                // 水面线校验：封面底边（= 封面倒影顶边）与波形中线必须重合。
                // 只靠肉眼看容易反复怀疑 1~2px，这里把三个真实坐标打出来，差值一眼可判。
                double coverBottom = TransformedY(NowPlayingCoverBorder) + (NowPlayingCoverBorder?.ActualHeight ?? 0);
                double waveTop = TransformedY(WaveformCanvas);
                double waveMid = waveTop + (WaveformCanvas?.ActualHeight ?? 0) * 0.5;
                string waterLine = _layoutIsWater
                    ? $"waterLine: coverBottom={coverBottom:0.##} waveMid={waveMid:0.##} delta={coverBottom - waveMid:0.##}"
                    : $"classic: coverBottom={coverBottom:0.##} waveTop={waveTop:0.##}";

                StartupLog.Write(
                    "[ReflectAlign] " + string.Join(" | ", new[]
                    {
                        D("coverBox", NowPlayingCoverBorder),
                        D("coverImg", NowPlayingCoverImage),
                        D("reflBox", NowPlayingCoverReflection),
                        D("reflImg", NowPlayingCoverReflectionImage),
                        $"coverY={TransformedY(NowPlayingCoverBorder):0.##} reflY={TransformedY(NowPlayingCoverReflection):0.##} " +
                        $"waveY={TransformedY(WaveformCanvas):0.##} waveH={WaveformCanvas?.ActualHeight:0.##}",
                        $"coverPx={coverPx} reflPx={reflPx}",
                        waterLine
                    }));
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.LogReflectionAlign", caught);
            }
        }

        /// <summary>
        /// 封面变化时重做倒影贴图（镜像 + 渐隐在 CPU 侧烤进 PNG，见 NowPlayingReflection.CreateReflection）。
        /// 传 null 表示清空倒影（停止播放、无封面）。
        /// </summary>
        internal void SyncNowPlayingReflection(byte[]? coverBytes)
        {
            try
            {
                if (NowPlayingCoverReflectionImage == null)
                {
                    return;
                }

                if (coverBytes == null || coverBytes.Length == 0)
                {
                    NowPlayingCoverReflectionImage.Source = null;
                    return;
                }

                _ = SyncNowPlayingReflectionAsync(coverBytes);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.SyncNowPlayingReflection", caught);
            }
        }

        private async System.Threading.Tasks.Task SyncNowPlayingReflectionAsync(byte[] coverBytes)
        {
            try
            {
                // 解码 + 逐像素处理放到后台线程，避免切歌时卡住 UI。
                (byte[]? png, int dispW, int dispH, int stripH) =
                    await System.Threading.Tasks.Task.Run(() => NowPlayingReflection.CreateReflection(coverBytes));
                if (png == null || png.Length == 0)
                {
                    return;
                }

                // 记录贴图实际尺寸（长方形封面完整显示后比 512 小），几何随之重算：
                // 容器尺寸/位置都按这组值换算，否则倒影与封面之间会错位或露缝。
                _reflectionDispW = dispW;
                _reflectionDispH = dispH;
                _reflectionStripH = stripH;

                var bitmap = await CreateBitmapFromBytesAsync(png);
                try
                {
                    if (NowPlayingCoverReflectionImage != null)
                    {
                        NowPlayingCoverReflectionImage.Source = bitmap;
                    }
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // 关窗竞态：后台线程生成完倒影时 XAML 已销毁，set_Source 会抛 0x8000FFFF。
                    // 此时程序正在退出，忽略即可（否则日志里会出现一条看起来像故障的异常）。
                }

                UpdateNowPlayingReflectionGeometry();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.SyncNowPlayingReflectionAsync", caught);
            }
        }

        /// <summary>页内布局菜单：写设置（会触发 Changed → 本页实时套用）。</summary>
        private void NowPlayingLayoutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not MenuFlyoutItem item)
                {
                    return;
                }

                string layout = item.Tag as string ?? LayoutClassic;
                if (!IsKnownLayout(layout))
                {
                    layout = LayoutClassic;
                }

                AppSettingsStore.Update(s => s.NowPlayingLayout = layout);
                ApplyNowPlayingLayout();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.NowPlayingLayoutMenuItem_Click", caught);
            }
        }

        /// <summary>订阅设置变更（全局仅一次），让"在设置里改布局"也能立即生效。</summary>
        private void HookNowPlayingLayoutSettings()
        {
            if (_nowPlayingLayoutHooked)
            {
                return;
            }

            _nowPlayingLayoutHooked = true;
            AppSettingsStore.Changed += () =>
            {
                try
                {
                    DispatcherQueue.TryEnqueue(ApplyNowPlayingLayout);
                }
                catch (Exception caught)
                {
                    StartupLog.WriteException("MainWindow.HookNowPlayingLayoutSettings", caught);
                }
            };
        }

        // =====================================================================
        // 黑胶唱机布局
        // =====================================================================

        private DispatcherQueueTimer? _vinylMotionTimer;

        /// <summary>唱片当前是否在转（null = 还没套用过，首次同步时无条件套用一次）。</summary>
        private bool? _vinylSpinning;

        /// <summary>旋转动画是否已经 Begin 过（没 Begin 就 Pause 会抛异常）。</summary>
        private bool _vinylSpinBegun;

        /// <summary>
        /// 每 500ms 查一次真实播放状态并驱动唱片 / 唱臂。
        /// 不用播放事件的理由：引擎播放（HiFi/独占/ASIO）与普通 MediaPlayer 是两条不同的路，
        /// 事件不一定都触发；轮询最省心，代价只是每半秒一次布尔比较。
        /// </summary>
        private void SyncVinylMotion()
        {
            try
            {
                if (!_layoutIsVinyl || VinylStage == null)
                {
                    return;
                }

                bool playing = IsEnginePlayingNow;
                if (!playing && !_usingEnginePlayback)
                {
                    var session = GetPlayer()?.PlaybackSession;
                    playing = session != null && session.PlaybackState == MediaPlaybackState.Playing;
                }

                if (_vinylSpinning == playing)
                {
                    return;
                }

                _vinylSpinning = playing;

                Storyboard? spin = VinylStage.Resources["VinylSpinStoryboard"] as Storyboard;
                if (playing)
                {
                    if (spin != null)
                    {
                        spin.Begin();
                        _vinylSpinBegun = true;
                    }
                }
                else if (spin != null && _vinylSpinBegun)
                {
                    // 用 Pause 而不是 Stop：唱片停在当前角度，恢复播放时接着转（像真唱机）
                    spin.Pause();
                }

                Storyboard? armDown = VinylStage.Resources["VinylArmDownStoryboard"] as Storyboard;
                Storyboard? armUp = VinylStage.Resources["VinylArmUpStoryboard"] as Storyboard;
                if (playing)
                {
                    armDown?.Begin();
                }
                else
                {
                    armUp?.Begin();
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.SyncVinylMotion", caught);
            }
        }

        /// <summary>离开黑胶布局 / 关闭播放页时调用：停掉旋转并把唱臂抬回原位。</summary>
        private void StopVinylMotion()
        {
            try
            {
                _vinylSpinning = null;
                if (VinylStage == null)
                {
                    return;
                }

                if (VinylStage.Resources["VinylSpinStoryboard"] is Storyboard spin && _vinylSpinBegun)
                {
                    spin.Stop();
                }
                _vinylSpinBegun = false;

                if (VinylStage.Resources["VinylArmUpStoryboard"] is Storyboard armUp)
                {
                    armUp.Begin();
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.StopVinylMotion", caught);
            }
        }

        /// <summary>启动黑胶状态轮询定时器（只启动一次，全程常驻，回调里有布局判断）。</summary>
        internal void EnsureVinylMotionTimer()
        {
            if (_vinylMotionTimer != null)
            {
                return;
            }

            _vinylMotionTimer = DispatcherQueue.CreateTimer();
            _vinylMotionTimer.Interval = TimeSpan.FromMilliseconds(500);
            _vinylMotionTimer.Tick += (_, _) => SyncVinylMotion();
            _vinylMotionTimer.Start();
        }

        /// <summary>按面板大小设置唱盘 / 唱片标签 / 唱臂的尺寸（由 UpdateNowPlayingCardLayout 调用）。</summary>
        internal void UpdateVinylGeometry(double paneWidth, double paneHeight)
        {
            if (VinylStage == null || paneWidth <= 0 || paneHeight <= 0)
            {
                return;
            }

            double size = Math.Clamp(Math.Min(paneHeight * 0.62, paneWidth * 0.42), 200, 380);
            size = Math.Round(size);

            if (VinylDisc != null)
            {
                VinylDisc.Width = size;
                VinylDisc.Height = size;
            }

            if (VinylArtEllipse != null)
            {
                double label = Math.Round(size * 0.39);
                VinylArtEllipse.Width = label;
                VinylArtEllipse.Height = label;
            }

            if (VinylTonearm != null)
            {
                VinylTonearm.Width = Math.Round(size * 0.55);
            }
        }

        /// <summary>用封面位图做唱片中心标签（切歌时调用）。</summary>
        internal void UpdateVinylArt(BitmapImage? coverImage)
        {
            try
            {
                if (VinylArtEllipse == null)
                {
                    return;
                }

                if (coverImage == null)
                {
                    VinylArtEllipse.Fill = null;
                    return;
                }

                VinylArtEllipse.Fill = new ImageBrush
                {
                    ImageSource = coverImage,
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateVinylArt", caught);
            }
        }

        /// <summary>切到黑胶布局时用当前封面字节补一次标签（避免要等下一首才有图）。</summary>
        private async System.Threading.Tasks.Task UpdateVinylArtFromBytesAsync(byte[]? coverBytes)
        {
            try
            {
                if (coverBytes == null || coverBytes.Length == 0)
                {
                    return;
                }

                BitmapImage? image = await CreateBitmapFromBytesAsync(coverBytes);
                UpdateVinylArt(image);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MainWindow.UpdateVinylArtFromBytesAsync", caught);
            }
        }
    }
}
