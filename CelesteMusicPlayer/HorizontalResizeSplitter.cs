using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 水平调整分割条：固定水平调整光标；悬停静止 0.3s 后在指针旁显示「水平调整大小」提示。
    /// </summary>
    public sealed class HorizontalResizeSplitter : Grid
    {
        public const double HitWidth = 10;
        public const double VisualWidth = 2;

        private static readonly InputSystemCursor ResizeCursor =
            InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        public static readonly DependencyProperty ShowVisualLineProperty =
            DependencyProperty.Register(
                nameof(ShowVisualLine),
                typeof(bool),
                typeof(HorizontalResizeSplitter),
                new PropertyMetadata(true, OnShowVisualLineChanged));

        private readonly Border _visualLine;
        private DispatcherQueueTimer? _tipTimer;
        private Popup? _tipPopup;
        private Point _lastRootPoint;
        private bool _pointerInside;

        public bool ShowVisualLine
        {
            get => (bool)GetValue(ShowVisualLineProperty);
            set => SetValue(ShowVisualLineProperty, value);
        }

        public HorizontalResizeSplitter()
        {
            // 使用完全 Transparent，保证命中稳定（极低 alpha 在合成更新时会命中闪烁）
            Background = new SolidColorBrush(Colors.Transparent);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            // 轻微外扩即可，过大负边距会与邻接控件抢命中导致光标闪烁
            Margin = new Thickness(-2, 0, -2, 0);
            Canvas.SetZIndex(this, 100);
            IsTabStop = false;
            ProtectedCursor = ResizeCursor;

            _visualLine = new Border
            {
                Width = VisualWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 4),
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(Color.FromArgb(255, 200, 200, 200)),
                IsHitTestVisible = false
            };
            Children.Add(_visualLine);

            PointerEntered += OnPointerEntered;
            PointerMoved += OnPointerMoved;
            PointerExited += OnPointerExited;
            PointerPressed += OnPointerPressed;
            PointerCaptureLost += (_, _) => HideTip();
            Loaded += (_, _) => EnsureTipTimer();
            Unloaded += (_, _) =>
            {
                _tipTimer?.Stop();
                HideTip();
            };
        }

        private DispatcherQueueTimer EnsureTipTimer()
        {
            if (_tipTimer != null)
            {
                return _tipTimer;
            }

            DispatcherQueue dq = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("DispatcherQueue unavailable.");

            _tipTimer = dq.CreateTimer();
            _tipTimer.IsRepeating = false;
            _tipTimer.Interval = TimeSpan.FromMilliseconds(300);
            _tipTimer.Tick += TipTimer_Tick;
            return _tipTimer;
        }

        private static void OnShowVisualLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HorizontalResizeSplitter splitter)
            {
                splitter._visualLine.Visibility =
                    (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AssertResizeCursor()
        {
            if (!ReferenceEquals(ProtectedCursor, ResizeCursor))
            {
                ProtectedCursor = ResizeCursor;
            }
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _pointerInside = true;
            AssertResizeCursor();
            UpdateRootPoint(e);
            RestartTipTimer();
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerInside)
            {
                return;
            }

            AssertResizeCursor();

            Point now = GetRootPoint(e);
            double dx = now.X - _lastRootPoint.X;
            double dy = now.Y - _lastRootPoint.Y;
            // 微小抖动忽略；明显移动则重新计时并隐藏提示
            if ((dx * dx) + (dy * dy) > 9)
            {
                _lastRootPoint = now;
                HideTip();
                RestartTipTimer();
            }
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pointerInside = false;
            _tipTimer?.Stop();
            HideTip();
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            AssertResizeCursor();
            _tipTimer?.Stop();
            HideTip();
        }

        private void RestartTipTimer()
        {
            DispatcherQueueTimer timer = EnsureTipTimer();
            timer.Stop();
            if (_pointerInside)
            {
                timer.Start();
            }
        }

        private void TipTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (!_pointerInside || XamlRoot == null)
            {
                return;
            }

            ShowTipNearPointer();
        }

        private void ShowTipNearPointer()
        {
            HideTip();

            var bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 45, 45, 45)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 90, 90, 90)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "水平调整大小",
                    FontSize = 12,
                    IsHitTestVisible = false,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 240, 240))
                }
            };

            _tipPopup = new Popup
            {
                XamlRoot = XamlRoot,
                IsHitTestVisible = false,
                Child = bubble,
                HorizontalOffset = _lastRootPoint.X + 14,
                VerticalOffset = _lastRootPoint.Y + 16,
                IsOpen = true
            };
        }

        private void HideTip()
        {
            if (_tipPopup != null)
            {
                _tipPopup.IsOpen = false;
                _tipPopup.Child = null;
                _tipPopup = null;
            }
        }

        private void UpdateRootPoint(PointerRoutedEventArgs e)
            => _lastRootPoint = GetRootPoint(e);

        private Point GetRootPoint(PointerRoutedEventArgs e)
        {
            if (XamlRoot?.Content is UIElement root)
            {
                return e.GetCurrentPoint(root).Position;
            }

            return e.GetCurrentPoint(this).Position;
        }
    }
}
