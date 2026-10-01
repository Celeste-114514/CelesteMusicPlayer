using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Media.Playback;
using WinRT.Interop;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 迷你播放器（重做版，布局参照 ECHO 的 mini-player）。
    ///
    /// 旧版的几个实际问题，这次一并解决：
    /// 1. **音量按钮根本不调音量**：旧版 VolumeButton_Click 里只有 _owner.Activate()，
    ///    点了是切回主窗口。现在改成 Flyout 弹音量条，走 MainWindow.SetVolumePublic()
    ///    （内部还是改主窗口那个 VolumeSlider，所以音量图标、写盘、引擎同步全部复用主界面逻辑）。
    /// 2. **高度会自我打架**：旧版 EnsureHeightFitsControls() 里 Measure → Resize →
    ///    触发 AppWindow.Changed → 又调 EnsureHeightFitsControls()，反复递归。
    ///    现在只有"收起 / 展开队列"两种固定高度，且只在目标高度与当前不同时才 Resize，必然收敛。
    /// 3. **440×228 的大方块**：改成 420×74 的紧凑横条，进度行塞进封面右侧那 56px 里，不额外占高。
    /// 4. **歌词跑马灯 33ms 常驻定时器**：常年在跑（费电、还容易把整条挤变形）。
    ///    歌词显示交给独立的桌面歌词窗口，迷你条不再显示歌词。
    /// 5. 文字全部改用主题画刷（旧版歌名写死白色，浅色主题/浅色壁纸上会看不清）。
    /// </summary>
    public sealed partial class MiniPlayerWindow : Window
    {
        private readonly MainWindow _owner;

        private bool _updatingProgress;
        private bool _userSeeking;
        private uint _progressPointerId;
        private bool _progressOnThumb;
        private bool _updatingVolume;
        private bool _alwaysOnTop = true;
        private bool _queueOpen;

        private bool _isDragging;
        private uint _dragPointerId;
        private int _dragCursorStartX;
        private int _dragCursorStartY;
        private int _dragWindowStartX;
        private int _dragWindowStartY;

        private IntPtr _hwnd;
        private SubclassProc? _subclassProc;
        private bool _subclassInstalled;
        private int _lastRegionW = -1;
        private int _lastRegionH = -1;

        // 尺寸（DIP）：560×120 收起、380 展开队列。比例更 deskbox：圆角 20、阴影 8+64。
        private const int WidthDip = 560;
        private const int HeightCollapsedDip = 120;
        private const int HeightQueueDip = 380;

        private const double CornerRadiusDip = 20;
        private const int RegionInsetPx = 3;
        private const int SubclassId = 1;

        public event Action? ClosedByUser;

        public MiniPlayerWindow(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
            WindowIconHelper.Apply(this);
            Title = "迷你播放器";

            AppSettingsState settings = AppSettingsStore.Load();
            _alwaysOnTop = settings.MiniPlayerAlwaysOnTop;
            ApplyBackdropPreference(settings.EnableFrostedGlass);

            ThemeColorService.ThemeColorChanged -= OnThemeColorChangedMini;
            ThemeColorService.ThemeColorChanged += OnThemeColorChangedMini;
            RefreshAccentFromOwner();

            // 极客皮肤：迷你播放器是独立顶级窗口，主窗口的 ApplyGeekChrome 根本走不到这里，
            // 得自带一份上色 + 圆角归零（详见 MiniPlayerWindow.Geek.cs）。
            // 同时挂上设置变更订阅，用户在设置里切风格/换磷光色时开着的迷你条要实时跟。
            HookGeekMiniSettings();
            ApplyGeekMini(MainWindow.IsGeekUiStyleActive());

            // 进度条的指针事件必须代码挂 + handledEventsToo：Slider 内部会把
            // PointerPressed/Released 标成 Handled，XAML 挂的处理器收不到，
            // _userSeeking 永远 false —— 表现为"进度条拖不动、松手也没反应"。
            HookProgressSliderPointer();

            OverlappedPresenter presenter = OverlappedPresenter.Create();
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = _alwaysOnTop;
            AppWindow.SetPresenter(presenter);

            ExtendsContentIntoTitleBar = false;
            TryCollapseTitleBar();

            RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ApplyChromeBackground(settings.EnableFrostedGlass);
            AttachCardShadow();

            // 固定高度：不再像旧版那样 Measure 出来再回写（那套会自我递归）
            ResizeToDips(WidthDip, HeightCollapsedDip);
            PositionNearBottomCenter();

            if (Content is FrameworkElement root)
            {
                root.Loaded += (_, _) =>
                {
                    ApplyWindowChromeNative();
                    ApplyWindowHeight();
                    RefreshFromOwner();
                    SyncVolumeFromOwner();
                };

                // 双击封面/空白 = 回到主窗口（ECHO 没有，但比旧版的"设置按钮"有用）
                root.DoubleTapped += RootGrid_DoubleTapped;
            }

            AppWindow.Changed += AppWindow_Changed;

            Closed += (_, _) =>
            {
                // AppSettingsStore.Changed 是静态事件，不解绑会把本窗口实例一直攥在内存里
                Safe(UnhookGeekMiniSettings);
                Safe(() => ThemeColorService.ThemeColorChanged -= OnThemeColorChangedMini);
                Safe(EndDrag);
                Safe(RemoveWindowSubclassIfNeeded);
                Safe(() => ClosedByUser?.Invoke());
            };
        }

        // =====================================================================
        // 对外接口（主窗口依赖，签名保持不变）
        // =====================================================================

        public void SetAlwaysOnTop(bool onTop)
        {
            _alwaysOnTop = onTop;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = onTop;
            }
        }

        public void ApplyBackdropPreference(bool enabled)
        {
            Safe(() =>
            {
                if (enabled)
                {
                    FrostedGlass.ApplyWindowBackdrop(this);
                }
                else
                {
                    SystemBackdrop = null;
                }

                // 经典界面：迷你条也要跟着主程序的明暗主题走（背景图式下本调用不介入）
                FrostedGlass.ApplyWindowTheme(this);
                ApplyChromeBackground(enabled);
            });
        }

        /// <summary>
        /// 横条自身底色。开毛玻璃时只做一层很淡的压暗，让 Desktop Acrylic 透出来；
        /// 关掉毛玻璃时必须换成不透明底 —— 否则透明内容 + 无 SystemBackdrop 会直接露出白底。
        /// </summary>
        private void ApplyChromeBackground(bool frosted)
        {
            // 经典界面下没有 Desktop Acrylic 可透（背板被关掉了），必须走不透明底，
            // 否则半透明压暗层盖在空背景上＝一条发灰的透明条。
            ChromeBorder.Background = frosted && FrostedGlass.ClassicMode == null
                ? FrostedGlass.CreateMiniPlayerDimOverlay()
                : FrostedGlass.CreateMiniPlayerBrush();
        }

        /// <summary>
        /// 给 ChromeBorder 加 ThemeShadow，让迷你播放器看着像浮在桌面上。
        /// WinUI3 ThemeShadow 对顶级 Window 内容能渲染出柔和阴影；构造时 Window 的合成器
        /// 已就绪，可直接挂。Shadow 自身不会清掉背景，所以调一次就够。
        /// </summary>
        private void AttachCardShadow()
        {
            Safe(() =>
            {
                try
                {
                    ChromeBorder.Shadow ??= new ThemeShadow();
                    // Y 向偏移让光从上方来，Z 提升阴影扩散半径
                    ChromeBorder.Translation = new System.Numerics.Vector3(0, 8f, 32f);
                }
                catch (Exception caught) { StartupLog.WriteException("MiniPlayerWindow.AttachCardShadow", caught); }
            });
        }

        /// <summary>主题色变化后刷新强调元素（进度条 + 播放按钮实心圆）。</summary>
        public void RefreshAccentFromOwner()
        {
            Safe(() =>
            {
                // 极客模式下换成磷光色，其余情况保持原来的主题色逻辑（详见 MiniPlayerWindow.Geek.cs）
                Windows.UI.Color accent = CurrentAccentForMini();

                ThemeColorService.ApplySliderAccent(ProgressSlider, accent);
                ThemeColorService.ApplySliderAccent(VolumeSlider, accent);

                // 播放按钮：强调色实心圆 + 自动选黑/白前景保证可读
                PlayPauseButton.Background = new SolidColorBrush(accent);
                PlayPauseButton.Foreground = new SolidColorBrush(ReadableForeground(accent));
                if (PlayPauseIcon != null)
                {
                    PlayPauseIcon.Foreground = PlayPauseButton.Foreground;
                }
            });
        }

        /// <summary>整条刷新（切歌、开迷你播放器时调用）。</summary>
        public void RefreshFromOwner()
        {
            Safe(() =>
            {
                PlaylistItem? item = _owner.GetCurrentPlayingItem();
                if (item == null)
                {
                    TitleText.Text = "未在播放";
                    ArtistText.Text = string.Empty;
                    CoverImage.Source = null;
                }
                else
                {
                    TitleText.Text = item.Title;
                    ArtistText.Text = item.Artist;
                    CoverImage.Source = _owner.GetCurrentCoverImage();
                }

                CoverPlaceholder.Opacity = CoverImage.Source == null ? 0.45 : 0;

                RefreshTransportState();

                // 引擎在放（HiFi/转码）时优先用引擎位置：MediaPlayer 可能还挂着上一首的 Source，
                // 读它会拿到一个早已结束的会话（Position == NaturalDuration），
                // 表现同样是"暂停后进度变成 3:19/3:19"。
                if (_owner.IsEngineActiveNow)
                {
                    UpdateProgressUi(_owner.EnginePositionValue, _owner.EngineDurationValue);
                }
                else
                {
                    MediaPlayer? player = _owner.GetMediaPlayerPublic();
                    if (player?.Source != null)
                    {
                        UpdateProgressUi(player.PlaybackSession.Position, player.PlaybackSession.NaturalDuration);
                    }
                    else
                    {
                        UpdateProgressUi(TimeSpan.Zero, TimeSpan.Zero);
                    }
                }

                SyncVolumeFromOwner();
                RefreshQueueSelection();
            });
        }

        /// <summary>播放位置推进（主窗口每 tick 调用）。</summary>
        public void SyncPosition(TimeSpan position, TimeSpan duration)
        {
            if (_userSeeking)
            {
                return;
            }

            UpdateProgressUi(position, duration);

            // 播放/暂停图标跟着实际状态走（旧版在这里只改 StatusText，图标可能不同步）
            RefreshTransportState();
        }

        // =====================================================================
        // 内部实现
        // =====================================================================

        /// <summary>同步播放/暂停图标与音量图标。</summary>
        private void RefreshTransportState()
        {
            Safe(() =>
            {
                MediaPlayer? player = _owner.GetMediaPlayerPublic();
                bool playing = (player?.Source != null
                        && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                    || _owner.IsEnginePlayingNow;

                PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
            });
        }

        private void UpdateProgressUi(TimeSpan position, TimeSpan duration)
        {
            Safe(() =>
            {
                _updatingProgress = true;
                try
                {
                    double total = duration.TotalSeconds;
                    if (total <= 0 || double.IsNaN(total))
                    {
                        ProgressSlider.Maximum = 100;
                        ProgressSlider.Value = 0;
                        TotalTimeText.Text = "--:--";
                        CurrentTimeText.Text = "--:--";
                    }
                    else
                    {
                        // 当前时间按总时长夹取：源偶尔会报出比时长略大的位置，
                        // 不夹就会显示成"3:19/3:19"这种一眼假的进度。
                        double pos = Math.Clamp(position.TotalSeconds, 0, total);
                        ProgressSlider.Maximum = total;
                        ProgressSlider.Value = pos;
                        TotalTimeText.Text = FormatTime(duration);
                        CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(pos));
                    }
                }
                finally
                {
                    _updatingProgress = false;
                }
            });
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
            {
                return t.ToString(@"h\:mm\:ss");
            }

            return t.ToString(@"mm\:ss");
        }

        /// <summary>按强调色亮度选黑/白前景，保证圆底上的图标始终看得清。</summary>
        private static Windows.UI.Color ReadableForeground(Windows.UI.Color background)
        {
            // 感知亮度（ITU-R BT.601 加权）
            double luma = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            return luma > 0.6
                ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
                : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }

        // ---------------------------------------------------------------- 音量

        /// <summary>从主窗口读回当前音量，同步到 Flyout 滑条与音量图标。</summary>
        private void SyncVolumeFromOwner()
        {
            Safe(() =>
            {
                _updatingVolume = true;
                try
                {
                    double vol = Math.Clamp(_owner.GetVolumePublic(), 0, 100);
                    VolumeSlider.Value = vol;
                    ApplyVolumeIcon(vol);
                }
                finally
                {
                    _updatingVolume = false;
                }
            });
        }

        private void ApplyVolumeIcon(double vol)
        {
            string glyph = vol <= 0.5 ? "\uE74F" : vol < 34 ? "\uE992" : vol < 67 ? "\uE993" : "\uE767";
            VolumeIcon.Glyph = glyph;
            VolumeFlyoutIcon.Glyph = glyph;
            VolumeValueText.Text = (int)Math.Round(vol) + "%";
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingVolume)
            {
                return;
            }

            Safe(() =>
            {
                double vol = Math.Clamp(e.NewValue, 0, 100);
                // 走主窗口的 VolumeSlider，音量图标/持久化/引擎同步全部复用主界面那套
                _owner.SetVolumePublic(vol);
                ApplyVolumeIcon(vol);
            });
        }

        // ---------------------------------------------------------------- 队列

        private void QueueButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                _queueOpen = !_queueOpen;
                QueuePanel.Visibility = _queueOpen ? Visibility.Visible : Visibility.Collapsed;

                ApplyWindowHeight();

                if (_queueOpen)
                {
                    LoadQueue();
                }
            });
        }

        private void LoadQueue()
        {
            EnsureQueueBound();
            RefreshQueueSelection();
        }

        /// <summary>
        /// 队列数据源绑定。主窗口会把 _userPlaylist 整个 new 掉（排序、重建队列时），
        /// 所以不能只在点按钮时绑一次 —— 每次刷新都比对引用，换了就重绑。
        /// </summary>
        private void EnsureQueueBound()
        {
            Safe(() =>
            {
                IReadOnlyList<PlaylistItem> queue = _owner.GetUserPlaylistPublic();
                if (!ReferenceEquals(QueueList.ItemsSource, queue))
                {
                    QueueList.ItemsSource = queue;
                }
            });
        }

        /// <summary>把"正在播那首"标成选中项（收起队列时跳过，省开销）。</summary>
        private void RefreshQueueSelection()
        {
            if (!_queueOpen || QueueList == null)
            {
                return;
            }

            EnsureQueueBound();

            Safe(() =>
            {
                int index = _owner.GetUserPlaylistIndexPublic();
                if (index >= 0 && QueueList.SelectedIndex != index)
                {
                    QueueList.SelectedIndex = index;
                }
            });
        }

        private void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 只有用户点选才跳歌；代码里设 SelectedIndex 也会触发这里，
            // 但那种情况 index 本来就等于当前曲目，再播一次等于重启当前曲，所以先用索引挡掉。
            Safe(() =>
            {
                int index = QueueList.SelectedIndex;
                if (index < 0 || index == _owner.GetUserPlaylistIndexPublic())
                {
                    return;
                }

                _owner.PlayUserPlaylistAtPublic(index);
            });
        }

        // ---------------------------------------------------------------- 拖动

        private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            Safe(() => _owner.Activate());
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
                && !e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // 命中任何按钮（含音量 Flyout 的按钮）都不起拖，否则点按钮会变成拖窗
            if (e.OriginalSource is DependencyObject src && IsControlButton(src))
            {
                return;
            }

            if (!GetCursorPos(out POINT cursor))
            {
                return;
            }

            PointInt32 pos = AppWindow.Position;
            _dragCursorStartX = cursor.X;
            _dragCursorStartY = cursor.Y;
            _dragWindowStartX = pos.X;
            _dragWindowStartY = pos.Y;
            _dragPointerId = e.Pointer.PointerId;
            _isDragging = true;
            RootGrid.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging || e.Pointer.PointerId != _dragPointerId)
            {
                return;
            }

            if (!GetCursorPos(out POINT cursor))
            {
                return;
            }

            AppWindow.Move(new PointInt32(
                _dragWindowStartX + (cursor.X - _dragCursorStartX),
                _dragWindowStartY + (cursor.Y - _dragCursorStartY)));
            e.Handled = true;
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerId != _dragPointerId)
            {
                return;
            }

            EndDrag();
            Safe(() => RootGrid.ReleasePointerCapture(e.Pointer));
            e.Handled = true;
        }

        private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
            => EndDrag();

        private void EndDrag()
        {
            _isDragging = false;
            _dragPointerId = 0;
        }

        /// <summary>命中测试：来源是按钮（或按钮内部元素）就不拖动。</summary>
        private static bool IsControlButton(DependencyObject src)
        {
            DependencyObject? node = src;
            while (node != null)
            {
                // 队列列表 / 滚动区 / 输入框也要豁免：在列表上按住想滚一下，
                // 结果整条迷你播放器被拖着跑（实测反馈）。ListView 模板里自带
                // ScrollViewer，顺着父链都能被这一网打尽。
                if (node is Button
                    || node is Slider
                    || node is ListViewBase
                    || node is Microsoft.UI.Xaml.Controls.Primitives.SelectorItem
                    || node is ScrollViewer
                    || node is TextBox)
                {
                    return true;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return false;
        }

        // ---------------------------------------------------------------- 按钮

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
            => Safe(_owner.PreviousPublic);

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
            => Safe(() =>
            {
                _owner.TogglePlayPausePublic();
                RefreshTransportState();
            });

        private void NextButton_Click(object sender, RoutedEventArgs e)
            => Safe(_owner.NextPublic);

        private void CloseMiniButton_Click(object sender, RoutedEventArgs e)
            => Safe(Close);

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // 只有用户真的在拖（指针按下过）才认；程序性赋值一律忽略。
            // 旧写法是在 ValueChanged 里直接 SeekPublic，一旦有程序性变更漏过
            // _updatingProgress 标志（例如 Maximum 变化时值被夹取），就会把播放位置顶到
            // 末尾 —— 表现就是"暂停后进度条变成 3:19/3:19"。这里改成和主窗口同一套把关方式。
            if (_updatingProgress)
            {
                return;
            }

            if (_userSeeking)
            {
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            }
        }

        /// <summary>
        /// 指针事件挂接（构造器里调一次）。⚠ 不能用 XAML 属性挂、也不能用 += 裸挂：
        /// Slider 的类处理函数会把 PointerPressed/Released 标记为 Handled，
        /// 这两种挂法都收不到 → _userSeeking 永远 false（主窗口踩过并注释在案）。
        /// handledEventsToo:true 才能无论事件有没有被吃掉都收到。
        /// </summary>
        private void HookProgressSliderPointer()
        {
            Safe(() =>
            {
                ProgressSlider.AddHandler(
                    UIElement.PointerPressedEvent,
                    new PointerEventHandler(ProgressSlider_PointerPressed),
                    handledEventsToo: true);
                ProgressSlider.AddHandler(
                    UIElement.PointerMovedEvent,
                    new PointerEventHandler(ProgressSlider_PointerMoved),
                    handledEventsToo: true);
                ProgressSlider.AddHandler(
                    UIElement.PointerExitedEvent,
                    new PointerEventHandler(ProgressSlider_PointerExited),
                    handledEventsToo: true);
                ProgressSlider.AddHandler(
                    UIElement.PointerReleasedEvent,
                    new PointerEventHandler(ProgressSlider_PointerReleased),
                    handledEventsToo: true);
                ProgressSlider.AddHandler(
                    UIElement.PointerCaptureLostEvent,
                    new PointerEventHandler(ProgressSlider_PointerCaptureLost),
                    handledEventsToo: true);
            });
        }

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 起拖标志先立：下面的落点赋值会触发 ValueChanged，赶在标志前面的话
            // 时间文本不会跟着跳，看起来就是"点了没反应"。
            _userSeeking = true;
            _progressPointerId = e.Pointer.PointerId;
            _progressOnThumb = IsInsideThumb(e.OriginalSource);

            // 点在滑块圆头上 → 原生拖动照常工作（Thumb 自己跟手），我们不插手，
            // 只等松开时提交。点在轨道上 → 原生 Slider 只负责把圆头瞬移过去，
            // 按住继续拖它不管，这里按指针横坐标自己算落点，并把指针捕获过来
            // 让拖动全程都有事件（不然拖出滑块边界就断）。
            if (!_progressOnThumb)
            {
                SeekMiniFromPointer(e);
                try
                {
                    ProgressSlider.CapturePointer(e.Pointer);
                }
                catch (Exception caught)
                {
                    StartupLog.WriteException("MiniPlayerWindow.ProgressSlider_PointerPressed", caught);
                }
            }
        }

        private void ProgressSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // 按住拖动跟随指针。圆头起手的那次不跟（原生 Thumb 自己会走，
            // 我们插手等于两套定位互相打脸）。指针 id 卡着：不是起手那根
            // 手指/鼠标的事件一律不理。
            if (_userSeeking && !_progressOnThumb && e.Pointer.PointerId == _progressPointerId)
            {
                SeekMiniFromPointer(e);
            }
        }

        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // 撒手撒在窗口外、且没人捕获指针时，Released 永远不会到这个滑块上，
            // _userSeeking 会卡在 true —— 之后播放推进全被拦住，进度条像死了。
            // 这里补一次提交（有捕获时不归我们管，等 CaptureLost）。
            if (_userSeeking
                && (ProgressSlider.PointerCaptures == null || ProgressSlider.PointerCaptures.Count == 0))
            {
                CommitMiniSeek();
            }
        }

        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                ProgressSlider.ReleasePointerCapture(e.Pointer);
            }
            catch (Exception caught)
            {
                // 没捕获过（原生滑块自己在管）就会走到这里，属正常路径
                StartupLog.WriteException("MiniPlayerWindow.ProgressSlider_PointerReleased", caught);
            }

            CommitMiniSeek();
        }

        private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            CommitMiniSeek();
        }

        /// <summary>
        /// 按指针在滑轨上的横坐标落点。WinUI 的 Slider 只认"攥住滑块圆头拖"，
        /// 点轨道 / 在轨道上按住拖都没有原生响应 —— 用户实测反馈的"进度条拖不动"。
        /// 宽高还没定时 ratio 会落在 0 或 1，无伤大雅（布局一定下来下次移动就准了）。
        /// </summary>
        private void SeekMiniFromPointer(PointerRoutedEventArgs e)
        {
            Safe(() =>
            {
                if (ProgressSlider.Maximum <= 0)
                {
                    return;
                }

                double px = e.GetCurrentPoint(ProgressSlider).Position.X;
                double ratio = Math.Clamp(px / Math.Max(1, ProgressSlider.ActualWidth), 0, 1);
                ProgressSlider.Value = ratio * ProgressSlider.Maximum;
            });
        }

        /// <summary>按下的位置是不是在滑块圆头里（圆头起手 = 原生拖动，不接管）。</summary>
        private static bool IsInsideThumb(object? originalSource)
        {
            DependencyObject? node = originalSource as DependencyObject;
            while (node != null)
            {
                if (node is Microsoft.UI.Xaml.Controls.Primitives.Thumb)
                {
                    return true;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return false;
        }

        /// <summary>用户拖完进度条后才真正跳转（指针抬起/捕获丢失都算拖完）。</summary>
        private void CommitMiniSeek()
        {
            if (!_userSeeking)
            {
                return;
            }

            _userSeeking = false;
            Safe(() =>
            {
                double seconds = ProgressSlider.Value;
                _owner.SeekSourceSeconds(seconds);
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(seconds));
            });
        }

        private void OnThemeColorChangedMini(Windows.UI.Color accent)
            => RefreshAccentFromOwner();

        // ---------------------------------------------------------------- 窗口外观

        private void ApplyWindowHeight()
        {
            Safe(() =>
            {
                int target = _queueOpen ? HeightQueueDip : HeightCollapsedDip;
                // 只在与当前高度不同时才 Resize —— 避免 Resize → AppWindow.Changed → 再 Resize 的递归
                if (AppWindow.Size.Height == target)
                {
                    return;
                }

                ResizeToDips(WidthDip, target);
            });
        }

        private void ResizeToDips(int widthDip, int heightDip)
        {
            Safe(() =>
            {
                IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : WindowNative.GetWindowHandle(this);
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                double scale = dpi / 96.0;
                AppWindow.Resize(new SizeInt32(
                    (int)Math.Round(widthDip * scale),
                    (int)Math.Round(heightDip * scale)));
            });
        }

        private void PositionNearBottomCenter()
        {
            Safe(() =>
            {
                DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                RectInt32 work = display.WorkArea;
                int w = AppWindow.Size.Width;
                int h = AppWindow.Size.Height;
                int x = work.X + (work.Width - w) / 2;
                int y = work.Y + work.Height - h - 72;
                AppWindow.Move(new PointInt32(x, Math.Max(work.Y, y)));
            });
        }

        private void TryCollapseTitleBar()
        {
            Safe(() =>
            {
                AppWindowTitleBar bar = AppWindow.TitleBar;
                bar.ExtendsContentIntoTitleBar = true;
                bar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
                bar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

                Windows.UI.Color transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                bar.BackgroundColor = transparent;
                bar.InactiveBackgroundColor = transparent;
                bar.ButtonBackgroundColor = transparent;
                bar.ButtonInactiveBackgroundColor = transparent;
                bar.ButtonHoverBackgroundColor = transparent;
                bar.ButtonPressedBackgroundColor = transparent;
                bar.ForegroundColor = transparent;
                bar.InactiveForegroundColor = transparent;
                bar.ButtonForegroundColor = transparent;
                bar.ButtonInactiveForegroundColor = transparent;
            });
        }

        private void ApplyWindowChromeNative()
        {
            Safe(() =>
            {
                _hwnd = WindowNative.GetWindowHandle(this);
                InstallWindowSubclassIfNeeded();

                int ex = GetWindowLong(_hwnd, GwlExStyle);
                ex |= WsExToolWindow;
                ex &= ~(WsExDlgModalFrame | WsExClientEdge | WsExStaticEdge | WsExWindowEdge | WsExLayered);
                SetWindowLong(_hwnd, GwlExStyle, ex);

                int style = GetWindowLong(_hwnd, GwlStyle);
                style &= ~(WsThickFrame | WsCaption | WsMinimizeBox | WsMaximizeBox | WsSysMenu | WsBorder | WsDlgFrame);
                SetWindowLong(_hwnd, GwlStyle, style);

                if (AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.SetBorderAndTitleBar(false, false);
                }

                TryCollapseTitleBar();

                // 圆角交给 SetWindowRgn；同时让 DWM 不要再画一圈系统描边
                uint corner = DwmWcpDoNotRound;
                DwmSetWindowAttribute(_hwnd, DwmWaWindowCornerPreference, ref corner, sizeof(uint));

                uint darkBorder = 0x001C151A;
                DwmSetWindowAttribute(_hwnd, DwmWaBorderColor, ref darkBorder, sizeof(uint));
                DwmSetWindowAttribute(_hwnd, DwmWaCaptionColor, ref darkBorder, sizeof(uint));

                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);

                ApplyRoundedWindowRegion();
            });
        }

        private void InstallWindowSubclassIfNeeded()
        {
            if (_subclassInstalled || _hwnd == IntPtr.Zero)
            {
                return;
            }

            _subclassProc = SubclassWndProc;
            if (SetWindowSubclass(_hwnd, _subclassProc, (IntPtr)SubclassId, IntPtr.Zero))
            {
                _subclassInstalled = true;
            }
        }

        private void RemoveWindowSubclassIfNeeded()
        {
            if (!_subclassInstalled || _hwnd == IntPtr.Zero || _subclassProc == null)
            {
                return;
            }

            RemoveWindowSubclass(_hwnd, _subclassProc, (IntPtr)SubclassId);
            _subclassInstalled = false;
        }

        private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            // 去掉非客户区，消除顶部标题条与外圈系统边
            if (msg == WmNcCalcSize && wParam != IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            if (msg == WmNcPaint)
            {
                return IntPtr.Zero;
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        /// <summary>圆角裁剪 HWND；内缩 2px 剪掉丙烯酸最外圈细白线。</summary>
        private void ApplyRoundedWindowRegion()
        {
            Safe(() =>
            {
                IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : WindowNative.GetWindowHandle(this);
                int w = AppWindow.Size.Width;
                int h = AppWindow.Size.Height;
                if (w <= 0 || h <= 0)
                {
                    return;
                }

                if (w == _lastRegionW && h == _lastRegionH)
                {
                    return;
                }

                uint dpi = GetDpiForWindow(hwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                int inset = RegionInsetPx;
                // _windowCornerDip：极客皮肤下为 0（圆角一律归零），否则是标准的 20
                int radiusPx = Math.Max(6, (int)Math.Round(_windowCornerDip * dpi / 96.0) - inset);
                if (_windowCornerDip <= 0)
                {
                    radiusPx = 0;
                }
                IntPtr rgn = CreateRoundRectRgn(inset, inset, w - inset + 1, h - inset + 1, radiusPx * 2, radiusPx * 2);
                if (rgn == IntPtr.Zero)
                {
                    return;
                }

                if (SetWindowRgn(hwnd, rgn, true) == 0)
                {
                    DeleteObject(rgn);
                    return;
                }

                _lastRegionW = w;
                _lastRegionH = h;
            });
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidSizeChange)
            {
                _lastRegionW = -1;
                _lastRegionH = -1;
                ApplyRoundedWindowRegion();
            }

            // 被意外最大化（如 Win+Up）时恢复成迷你条尺寸
            Safe(() =>
            {
                if (AppWindow.Presenter is OverlappedPresenter presenter
                    && presenter.State == OverlappedPresenterState.Maximized)
                {
                    presenter.Restore();
                    presenter.IsMaximizable = false;
                    presenter.IsResizable = false;
                    _lastRegionW = -1;
                    _lastRegionH = -1;
                    ApplyWindowHeight();
                    ApplyWindowChromeNative();
                }
            });
        }

        // ---------------------------------------------------------------- 工具

        /// <summary>统一兜底：迷你播放器里任何一处抛异常都不该把整个应用带崩。</summary>
        private static void Safe(Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("MiniPlayerWindow", caught);
            }
        }

        // ---------------------------------------------------------------- P/Invoke

        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const int WsThickFrame = 0x00040000;
        private const int WsCaption = 0x00C00000;
        private const int WsSysMenu = 0x00080000;
        private const int WsMinimizeBox = 0x00020000;
        private const int WsMaximizeBox = 0x00010000;
        private const int WsBorder = 0x00800000;
        private const int WsDlgFrame = 0x00400000;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExDlgModalFrame = 0x00000001;
        private const int WsExClientEdge = 0x00000200;
        private const int WsExStaticEdge = 0x00020000;
        private const int WsExWindowEdge = 0x00000100;
        private const int WsExLayered = 0x00080000;
        private const int WmNcCalcSize = 0x0083;
        private const int WmNcPaint = 0x0085;
        private const int DwmWaWindowCornerPreference = 33;
        private const int DwmWaBorderColor = 34;
        private const int DwmWaCaptionColor = 35;
        private const uint DwmWcpDoNotRound = 1;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        private delegate IntPtr SubclassProc(
            IntPtr hWnd,
            uint uMsg,
            IntPtr wParam,
            IntPtr lParam,
            IntPtr uIdSubclass,
            IntPtr dwRefData);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(
            IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(
            IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    }
}
