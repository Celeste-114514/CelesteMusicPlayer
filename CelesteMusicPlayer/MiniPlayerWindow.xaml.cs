using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    public sealed partial class MiniPlayerWindow : Window
    {
        private readonly MainWindow _owner;
        private bool _updatingProgress;
        private bool _userSeeking;
        private DispatcherTimer? _marqueeTimer;
        private double _lyricOffset;
        private string _lyricSource = string.Empty;
        private bool _alwaysOnTop = true;
        private int _lastRegionW = -1;
        private int _lastRegionH = -1;

        private bool _isDragging;
        private uint _dragPointerId;
        private int _dragCursorStartX;
        private int _dragCursorStartY;
        private int _dragWindowStartX;
        private int _dragWindowStartY;

        private IntPtr _hwnd;
        private SubclassProc? _subclassProc;
        private bool _subclassInstalled;

        private const double CornerRadiusDip = 16;
        private const int RegionInsetPx = 2;
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

            OverlappedPresenter presenter = OverlappedPresenter.Create();
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = _alwaysOnTop;
            AppWindow.SetPresenter(presenter);

            ResizeToDips(440, 228);
            PositionNearBottomCenter();

            // 不把整窗设成标题栏（双击会最大化）；拖动用手动 Move，避免系统拖动阈值
            ExtendsContentIntoTitleBar = false;
            TryCollapseTitleBar();

            RootGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ChromeBorder.Background = FrostedGlass.CreateMiniPlayerDimOverlay();
            ChromeBorder.CornerRadius = new CornerRadius(CornerRadiusDip);
            EdgeMaskBorder.CornerRadius = new CornerRadius(CornerRadiusDip);

            if (Content is FrameworkElement root)
            {
                root.Loaded += (_, _) =>
                {
                    ApplyWindowChromeNative();
                    EnsureHeightFitsControls();
                    ApplyRoundedWindowRegion();
                    RefreshFromOwner();
                    StartMarquee();
                };

                LyricCanvas.SizeChanged += (_, _) => UpdateLyricClip();
                root.DoubleTapped += RootGrid_DoubleTapped;
            }

            AppWindow.Changed += AppWindow_Changed;

            Closed += (_, _) =>
            {
                // 窗口销毁前取消主题色事件订阅,防止已销毁窗口被回调
                try
                {
                    ThemeColorService.ThemeColorChanged -= OnThemeColorChangedMini;
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }

                try
                {
                    EndDrag();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }

                try
                {
                    RemoveWindowSubclassIfNeeded();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }

                try
                {
                    _marqueeTimer?.Stop();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }

                try
                {
                    ClosedByUser?.Invoke();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }
            };
        }

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
            try
            {
                if (enabled)
                {
                    FrostedGlass.ApplyWindowBackdrop(this);
                }
                else
                {
                    SystemBackdrop = null;
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }
        }

        /// <summary>主题色变化后刷新迷你播放器的强调元素(进度条等)。</summary>
        public void RefreshAccentFromOwner()
        {
            try
            {
                AppSettingsState s = AppSettingsStore.Load();
                Windows.UI.Color accent = s.AccentSource == "Custom"
                    ? (ThemeColorService.ParseHexColor(s.CustomAccentColor) ?? Windows.UI.Color.FromArgb(255, 0, 120, 212))
                    : Windows.UI.Color.FromArgb(255, 0, 120, 212);
                StartupLog.Write("迷你播放器主题色: " + accent.ToString() + " ProgressSlider=" + (ProgressSlider != null));
                ThemeColorService.ApplySliderAccent(ProgressSlider, accent);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }
        }

        public void RefreshFromOwner()
        {
            PlaylistItem? item = _owner.GetCurrentPlayingItem();
            ImageSource? cover = _owner.GetCurrentCoverImage();
            if (item == null)
            {
                TitleText.Text = "未在播放";
                ArtistText.Text = string.Empty;
                CoverImage.Source = null;
                StatusText.Text = "已暂停";
                PlayPauseIcon.Glyph = "\uE768";
                SetLyricText(string.Empty);
            }
            else
            {
                TitleText.Text = item.Title;
                ArtistText.Text = item.Artist;
                CoverImage.Source = cover;
            }

            MediaPlayer? player = _owner.GetMediaPlayerPublic();
            if (player?.Source != null)
            {
                bool playing = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                StatusText.Text = playing ? "正在播放" : "已暂停";
                PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
                UpdateProgressUi(player.PlaybackSession.Position, player.PlaybackSession.NaturalDuration);
                double vol = player.Volume * 100;
                VolumeIcon.Glyph = vol <= 0.5 ? "\uE74F" : vol < 34 ? "\uE992" : vol < 67 ? "\uE993" : "\uE767";
            }
            else if (_owner.IsEngineActiveNow)
            {
                bool playing = _owner.IsEnginePlayingNow;
                StatusText.Text = playing ? "正在播放" : "已暂停";
                PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
                UpdateProgressUi(_owner.EnginePositionValue, _owner.EngineDurationValue);
            }

            PlaybackOrderIcon.Glyph = _owner.GetPlaybackOrderGlyphPublic();
            string lyric = PreferEnglishLyric(_owner.GetCurrentLyricTextPublic());
            SetLyricText(lyric);
        }

        public void SyncPosition(TimeSpan position, TimeSpan duration)
        {
            if (_userSeeking)
            {
                return;
            }

            UpdateProgressUi(position, duration);
            string lyric = PreferEnglishLyric(_owner.GetCurrentLyricTextPublic());
            if (!string.Equals(lyric, _lyricSource, StringComparison.Ordinal))
            {
                SetLyricText(lyric);
            }

            MediaPlayer? player = _owner.GetMediaPlayerPublic();
            bool playing = (player?.Source != null
                    && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                || _owner.IsEnginePlayingNow;
            StatusText.Text = playing ? "正在播放" : "已暂停";
            PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
        }

        internal static string PreferEnglishLyric(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string text = raw.Trim();
            bool hasCjk = Regex.IsMatch(text, @"[\u3040-\u30ff\u3400-\u9fff]");
            bool hasLatin = Regex.IsMatch(text, @"[A-Za-z]");
            if (!(hasCjk && hasLatin))
            {
                return text;
            }

            string[] parts = Regex.Split(text, @"\s*[\/／|｜]\s*|\r\n|\n|\r");
            foreach (string part in parts)
            {
                string p = part.Trim();
                if (p.Length == 0)
                {
                    continue;
                }

                if (Regex.IsMatch(p, @"[A-Za-z]") && !Regex.IsMatch(p, @"[\u3040-\u30ff\u3400-\u9fff]"))
                {
                    return p;
                }
            }

            string stripped = Regex.Replace(text, @"[\u3040-\u30ff\u3400-\u9fff]+", " ").Trim();
            stripped = Regex.Replace(stripped, @"\s{2,}", " ").Trim(" -/|｜".ToCharArray());
            return string.IsNullOrWhiteSpace(stripped) ? text : stripped;
        }

        private void SetLyricText(string text)
        {
            _lyricSource = text ?? string.Empty;
            LyricText.Text = _lyricSource;
            _lyricOffset = 0;
            LyricText.ClearValue(Canvas.LeftProperty);
            Canvas.SetLeft(LyricText, 0);
            UpdateLyricClip();
        }

        private void UpdateLyricClip()
        {
            double w = LyricCanvas.ActualWidth;
            if (w <= 0)
            {
                return;
            }

            LyricCanvas.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, w, LyricCanvas.ActualHeight)
            };
        }

        private void StartMarquee()
        {
            _marqueeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _marqueeTimer.Tick -= MarqueeTimer_Tick;
            _marqueeTimer.Tick += MarqueeTimer_Tick;
            _marqueeTimer.Start();
        }

        private void MarqueeTimer_Tick(object? sender, object e)
        {
            if (string.IsNullOrEmpty(_lyricSource))
            {
                return;
            }

            LyricText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textW = LyricText.DesiredSize.Width;
            double viewW = LyricCanvas.ActualWidth;
            if (viewW <= 0 || textW <= viewW + 4)
            {
                Canvas.SetLeft(LyricText, 0);
                return;
            }

            _lyricOffset -= 0.7;
            if (_lyricOffset < -(textW + 28))
            {
                _lyricOffset = viewW;
            }

            Canvas.SetLeft(LyricText, _lyricOffset);
        }

        private void UpdateProgressUi(TimeSpan position, TimeSpan duration)
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
                }
                else
                {
                    ProgressSlider.Maximum = total;
                    ProgressSlider.Value = Math.Clamp(position.TotalSeconds, 0, total);
                    TotalTimeText.Text = FormatTime(duration);
                }

                CurrentTimeText.Text = FormatTime(position);
            }
            finally
            {
                _updatingProgress = false;
            }
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
            {
                return t.ToString(@"h\:mm\:ss");
            }

            return t.ToString(@"mm\:ss");
        }

        private void ResizeToDips(int widthDip, int heightDip)
        {
            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                double scale = dpi / 96.0;
                AppWindow.Resize(new SizeInt32(
                    (int)Math.Round(widthDip * scale),
                    (int)Math.Round(heightDip * scale)));
            }
            catch
            {
                AppWindow.Resize(new SizeInt32(widthDip, heightDip));
            }
        }

        private void EnsureHeightFitsControls()
        {
            try
            {
                ChromeBorder.UpdateLayout();
                double width = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 440;
                ChromeBorder.Measure(new Size(width, double.PositiveInfinity));
                double need = ChromeBorder.DesiredSize.Height;
                if (ChromeBorder.ActualHeight > need)
                {
                    need = ChromeBorder.ActualHeight;
                }

                int heightDip = (int)Math.Ceiling(need + 2);
                heightDip = Math.Clamp(heightDip, 200, 260);
                ResizeToDips(440, heightDip);
                PositionNearBottomCenter();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }
        }

        private void PositionNearBottomCenter()
        {
            try
            {
                DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                RectInt32 work = display.WorkArea;
                int w = AppWindow.Size.Width;
                int h = AppWindow.Size.Height;
                int x = work.X + (work.Width - w) / 2;
                int y = work.Y + work.Height - h - 72;
                AppWindow.Move(new PointInt32(x, Math.Max(work.Y, y)));
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }
        }

        private void TryCollapseTitleBar()
        {
            try
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
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }
        }

        private void ApplyWindowChromeNative()
        {
            try
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

                // 圆角由 SetWindowRgn 负责；DWM 设 DONOTROUND，避免再画一圈系统描边
                uint corner = DwmWcpDoNotRound;
                DwmSetWindowAttribute(_hwnd, DwmWaWindowCornerPreference, ref corner, sizeof(uint));

                // 边框色贴近面板，即使残留 1px 也不发白
                uint darkBorder = 0x001C151A;
                DwmSetWindowAttribute(_hwnd, DwmWaBorderColor, ref darkBorder, sizeof(uint));
                DwmSetWindowAttribute(_hwnd, DwmWaCaptionColor, ref darkBorder, sizeof(uint));

                SetWindowPos(
                    _hwnd,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);

                ChromeBorder.CornerRadius = new CornerRadius(CornerRadiusDip);
                EdgeMaskBorder.CornerRadius = new CornerRadius(CornerRadiusDip);
                ApplyRoundedWindowRegion();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }
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
            // 去掉非客户区，消除顶部标题条/拖动手柄与外圈系统边
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

        /// <summary>
        /// 圆角裁剪 HWND；内缩 2px 剪掉丙烯酸最外圈细白线。
        /// </summary>
        private void ApplyRoundedWindowRegion()
        {
            try
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
                int radiusPx = Math.Max(6, (int)Math.Round(CornerRadiusDip * dpi / 96.0) - inset);
                IntPtr rgn = CreateRoundRectRgn(
                    inset,
                    inset,
                    w - inset + 1,
                    h - inset + 1,
                    radiusPx * 2,
                    radiusPx * 2);
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
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidSizeChange)
            {
                _lastRegionW = -1;
                _lastRegionH = -1;
                ApplyRoundedWindowRegion();
            }

            if (!args.DidPresenterChange && !args.DidSizeChange)
            {
                return;
            }

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Maximized)
                {
                    presenter.Restore();
                    presenter.IsMaximizable = false;
                    EnsureHeightFitsControls();
                    PositionNearBottomCenter();
                    ApplyWindowChromeNative();
                }
            }
            else
            {
                OverlappedPresenter restored = OverlappedPresenter.Create();
                restored.IsResizable = false;
                restored.IsMinimizable = false;
                restored.IsMaximizable = false;
                restored.SetBorderAndTitleBar(false, false);
                restored.IsAlwaysOnTop = _alwaysOnTop;
                AppWindow.SetPresenter(restored);
                EnsureHeightFitsControls();
                PositionNearBottomCenter();
                ApplyWindowChromeNative();
            }
        }

        private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
            => e.Handled = true;

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
                && !e.GetCurrentPoint(RootGrid).Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (e.OriginalSource is DependencyObject src
                && (IsDescendantOf(src, SettingsButton)
                    || IsDescendantOf(src, ProgressSlider)
                    || IsControlButton(src)))
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

            int x = _dragWindowStartX + (cursor.X - _dragCursorStartX);
            int y = _dragWindowStartY + (cursor.Y - _dragCursorStartY);
            AppWindow.Move(new PointInt32(x, y));
            e.Handled = true;
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerId != _dragPointerId)
            {
                return;
            }

            EndDrag();
            try
            {
                RootGrid.ReleasePointerCapture(e.Pointer);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }

            e.Handled = true;
        }

        private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
            => EndDrag();

        private void EndDrag()
        {
            _isDragging = false;
            _dragPointerId = 0;
        }

        private bool IsControlButton(DependencyObject src)
        {
            DependencyObject? n = src;
            while (n != null)
            {
                if (n is Button)
                {
                    return true;
                }

                n = VisualTreeHelper.GetParent(n);
            }

            return false;
        }

        private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor))
                {
                    return true;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return false;
        }

        private void CloseMiniButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ThemeColorService.ThemeColorChanged -= OnThemeColorChangedMini;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }

            try
            {
                Close();
            }
            catch (Exception closeEx)
            {
                StartupLog.WriteException("迷你播放器关闭异常", closeEx);
            }
        }

        private void OnThemeColorChangedMini(Windows.UI.Color accent)
        {
            try
            {
                RefreshAccentFromOwner();
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MiniPlayerWindow.xaml.cs", caught); }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
            => SettingsWindow.ShowOrActivate();

        private void PlaybackOrderButton_Click(object sender, RoutedEventArgs e)
            => _owner.CyclePlaybackOrderPublic();

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
            => _owner.PreviousPublic();

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
            => _owner.TogglePlayPausePublic();

        private void NextButton_Click(object sender, RoutedEventArgs e)
            => _owner.NextPublic();

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
            => _owner.Activate();

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingProgress)
            {
                return;
            }

            _userSeeking = true;
            try
            {
                _owner.SeekPublic(TimeSpan.FromSeconds(e.NewValue));
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            }
            finally
            {
                _userSeeking = false;
            }
        }

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
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

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
            IntPtr hWnd,
            SubclassProc pfnSubclass,
            IntPtr uIdSubclass,
            IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(
            IntPtr hWnd,
            SubclassProc pfnSubclass,
            IntPtr uIdSubclass);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    }
}
