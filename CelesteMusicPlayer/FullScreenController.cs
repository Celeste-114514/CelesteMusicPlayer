// ============================================================================
//  全屏控制器 —— 自包含、可整体复制到别的 WinUI 3 项目里用
// ----------------------------------------------------------------------------
//  用法（三步）：
//
//  1) XAML 里放一个按钮（播放条上、标题栏上都行）：
//     <Button x:Name="FullScreenButton" Click="OnFullScreenClick"
//             ToolTipService.ToolTip="全屏">
//         <FontIcon x:Name="FullScreenIcon" Glyph="&#xE740;"/>
//     </Button>
//
//  2) 窗口后台代码里声明一个字段，在构造函数里 new 出来：
//     private readonly FullScreenController _fullScreen;
//     public MainWindow()
//     {
//         InitializeComponent();
//         _fullScreen = new FullScreenController(this);
//         _fullScreen.StateChanged += onFullScreenChanged;  // 用来换图标
//     }
//
//  3) 按钮点击处理：
//     private void OnFullScreenClick(object sender, RoutedEventArgs e)
//         => _fullScreen.Toggle();
//
//  想让 Esc 退出全屏：构造时传 enableEscapeExit: true（默认就是 true）。
//  想手动控制：调 Enter() / Exit() / Toggle()，读 IsFullScreen 判断当前状态。
//
// ----------------------------------------------------------------------------
//  之前几次全屏做失败，通常是踩了下面这几个坑，这里都规避了：
//
//  坑 1：用 OverlappedPresenter.Maximize() 当全屏。
//        那是"最大化"，任务栏还在，不是真全屏。真全屏必须走
//        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)。
//
//  坑 2：反复 SetPresenter 不判断状态。
//        已经是全屏还再设一次 FullScreen，行为不可预期。这里用 _isFullScreen
//        挡住重复调用。
//
//  坑 3：退出全屏时顺序搞反 —— 先 Resize 再 SetPresenter(Default)。
//        只要还是 FullScreen presenter，任何 Resize 都会被 presenter 立刻覆盖回
//        屏幕尺寸，于是窗口"退不出去"。必须【先还原 presenter，再 Resize】。
//
//  坑 4：在后台线程上操作窗口。
//        AppWindow 相关调用必须在 UI 线程，否则抛 0x8001010E。这里用构造时缓存的
//        DispatcherQueue 保证投递回 UI 线程。
//
//  坑 5：窗口还没 Activate 就调 AppWindow API。
//        结果拿不到 presenter。这里全部 try 住，失败就静默跳过。
//
//  坑 6：Esc 收不到。
//        全屏后焦点常常落在播放器控件上，普通 KeyDown 订阅收不到按键。这里用
//        AddHandler(handledEventsToo: true) 挂到内容根，并把焦点抢回来。
// ============================================================================

using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 管理一个 WinUI 3 窗口的全屏状态。
    /// 音频播放器这类场景就是"主窗口占满屏幕，界面布局不变"。
    /// </summary>
    public sealed class FullScreenController
    {
        private readonly Window _window;
        private readonly bool _escapeToExit;
        private readonly DispatcherQueue? _uiQueue;

        private bool _isFullScreen;
        private SizeInt32 _sizeBeforeFullScreen;
        private FrameworkElement? _keyTarget;
        private KeyEventHandler? _keyHandler;

        /// <summary>当前是否处于全屏。</summary>
        public bool IsFullScreen => _isFullScreen;

        /// <summary>全屏状态变化时触发，参数=true 表示进入全屏。</summary>
        public event Action<bool>? StateChanged;

        /// <param name="window">要控制的窗口（通常是 this）。</param>
        /// <param name="enableEscapeExit">是否允许按 Esc 退出全屏。</param>
        public FullScreenController(Window window, bool enableEscapeExit = true)
        {
            _window = window;
            _escapeToExit = enableEscapeExit;

            // 在 UI 线程上把队列抓下来存好。
            // 注意：别在后台线程调 DispatcherQueue.GetForCurrentThread()，那边返回 null。
            try { _uiQueue = DispatcherQueue.GetForCurrentThread(); }
            catch { _uiQueue = null; }
        }

        /// <summary>进入/退出全屏。</summary>
        public void Toggle()
        {
            if (_isFullScreen) Exit();
            else Enter();
        }

        /// <summary>进入全屏：整个窗口铺满屏幕，界面布局保持不变。</summary>
        public void Enter()
        {
            if (_isFullScreen) return;
            RunOnUi(EnterCore);
        }

        /// <summary>退出全屏，还原成进入前的尺寸。</summary>
        public void Exit()
        {
            if (!_isFullScreen) return;
            RunOnUi(ExitCore);
        }

        // ---------- 内部实现 ----------

        private void EnterCore()
        {
            try
            {
                var appWindow = _window.AppWindow;
                if (appWindow == null) return;

                // 记住进入前的尺寸，退出时还原
                _sizeBeforeFullScreen = appWindow.Size;

                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _isFullScreen = true;

                AttachEscape();
                StateChanged?.Invoke(true);
            }
            catch
            {
                // 拿不到 AppWindow（窗口还没激活之类）就静默跳过，不要崩
            }
        }

        private void ExitCore()
        {
            try
            {
                var appWindow = _window.AppWindow;
                if (appWindow == null) return;

                DetachEscape();

                // 顺序不能反！先还原 presenter，再改尺寸。
                // 还在 FullScreen 状态下 Resize 会被 presenter 覆盖掉，退不出去。
                appWindow.SetPresenter(AppWindowPresenterKind.Default);

                if (_sizeBeforeFullScreen.Width > 0 && _sizeBeforeFullScreen.Height > 0)
                    appWindow.Resize(_sizeBeforeFullScreen);

                _isFullScreen = false;
                StateChanged?.Invoke(false);
            }
            catch
            {
                _isFullScreen = false;
            }
        }

        // ---------- Esc 退出 ----------

        private void AttachEscape()
        {
            if (!_escapeToExit) return;

            try
            {
                // Content 要到窗口建好之后才有，所以在这里取，不能在构造函数里取
                _keyTarget = _window.Content as FrameworkElement;
                if (_keyTarget == null) return;

                // handledEventsToo: true —— 即使播放器内部已经把按键"吃掉"了，这里也能收到
                _keyHandler = new KeyEventHandler(OnKeyDown);
                _keyTarget.AddHandler(UIElement.KeyDownEvent, _keyHandler, true);

                // 全屏后焦点常常落在播放控件上，抢回内容根，保证按键能路由过来
                _keyTarget.IsTabStop = true;
                _keyTarget.Focus(FocusState.Programmatic);
            }
            catch { /* Esc 收不到也不影响别的功能 */ }
        }

        private void DetachEscape()
        {
            try
            {
                if (_keyTarget == null || _keyHandler == null) return;
                _keyTarget.RemoveHandler(UIElement.KeyDownEvent, _keyHandler);
                _keyTarget = null;
                _keyHandler = null;
            }
            catch { _keyTarget = null; _keyHandler = null; }
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                ExitCore();
                e.Handled = true;
            }
        }

        // ---------- 线程切换 ----------

        private void RunOnUi(Action action)
        {
            var queue = _uiQueue;
            if (queue == null || queue.HasThreadAccess)
            {
                action();
                return;
            }

            try
            {
                if (!queue.TryEnqueue(() => action()))
                    action();
            }
            catch
            {
                try { action(); } catch { /* 尽力而为 */ }
            }
        }
    }
}
