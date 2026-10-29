// W13：用 Win32 直接实现 Microsoft.UI.Windowing 的那套窗口 API。
// 原生网页窗口（WinForms Form + WebView2）只用到标题栏/显隐/尺寸/位置/最大化最小化，
// 这里用 HWND + ShowWindow/SetWindowPos/GetWindowPlacement 等价实现，去掉 AppSDK 依赖。
#pragma warning disable CS0436
#pragma warning disable CS0067
#pragma warning disable CS0169
#pragma warning disable CS0414

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Microsoft.UI
{
    /// <summary>WindowId 直接携带 HWND：Win32 世界里的窗口标识就是句柄本身。</summary>
    public struct WindowId
    {
        public ulong Value;
        public WindowId(ulong value) { Value = value; }
    }
}

namespace Microsoft.UI.Win32Interop
{
    public static class Interop
    {
        public static Microsoft.UI.WindowId GetWindowIdFromWindow(nint hwnd) => new((ulong)hwnd);
        public static nint GetWindowFromWindowId(Microsoft.UI.WindowId id) => (nint)id.Value;
    }
}

namespace Microsoft.UI.Windowing
{
    public enum AppWindowPresenterKind
    {
        Overlapped = 0,
        FullScreen = 1,
        CompactOverlay = 2,
        Default = 0,
    }

    public enum OverlappedPresenterState
    {
        Restored = 0,
        Minimized = 1,
        Maximized = 2,
    }

    public enum TitleBarHeightOption
    {
        Collapsed = 0,
        Standard = 1,
        Tall = 2,
    }

    public enum AppWindowTitleBarVisibility
    {
        Always = 0,
        Auto = 1,
    }

    public class AppWindowChangedEventArgs
    {
        public bool DidSizeChange { get; set; }
        public bool DidPositionChange { get; set; }
        public bool DidVisibilityChange { get; set; }
        public bool DidPresenterChange { get; set; }
        public bool DidFrameChange { get; set; }
    }

    public class AppWindowClosingEventArgs
    {
        public bool Cancel { get; set; }
    }

    public class AppWindowTitleBar
    {
        public bool ExtendsContentIntoTitleBar { get; set; }
        public TitleBarHeightOption PreferredHeightOption { get; set; }
        public double Height { get; set; }
        public double LeftInset { get; }
        public double RightInset { get; }
        public AppWindowTitleBarVisibility PreferredVisibility { get; set; }
        public Windows.UI.Color ForegroundColor { get; set; }
        public Windows.UI.Color BackgroundColor { get; set; }
        public Windows.UI.Color ButtonForegroundColor { get; set; }
        public Windows.UI.Color ButtonBackgroundColor { get; set; }
        public Windows.UI.Color InactiveForegroundColor { get; set; }
        public Windows.UI.Color InactiveBackgroundColor { get; set; }
        public Windows.UI.Color ButtonHoverForegroundColor { get; set; }
        public Windows.UI.Color ButtonHoverBackgroundColor { get; set; }
        public Windows.UI.Color ButtonPressedForegroundColor { get; set; }
        public Windows.UI.Color ButtonPressedBackgroundColor { get; set; }
        public Windows.UI.Color ButtonInactiveForegroundColor { get; set; }
        public Windows.UI.Color ButtonInactiveBackgroundColor { get; set; }

        /// <summary>WinUI 里需要系统支持自定义标题栏；原生窗口自绘标题栏，恒为 true。</summary>
        public static bool IsCustomizationSupported() => true;
        public void ResetToDefault() { }
    }

    public class AppWindowPresenter
    {
        public AppWindowPresenterKind Kind { get; protected set; } = AppWindowPresenterKind.Overlapped;
    }

    public sealed class OverlappedPresenter : AppWindowPresenter
    {
        private readonly nint _hwnd;

        internal OverlappedPresenter(nint hwnd) { _hwnd = hwnd; }

        public static OverlappedPresenter Create() => new(0);

        public OverlappedPresenterState State
        {
            get
            {
                if (NativeMethods.IsIconic(_hwnd)) return OverlappedPresenterState.Minimized;
                if (NativeMethods.IsZoomed(_hwnd)) return OverlappedPresenterState.Maximized;
                return OverlappedPresenterState.Restored;
            }
        }

        public bool IsMinimizable { get; set; } = true;
        public bool IsMaximizable { get; set; } = true;
        public bool IsResizable { get; set; } = true;
        public bool IsAlwaysOnTop { get; set; }
        public bool HasBorder { get; set; } = true;
        public bool HasTitleBar { get; set; } = true;

        public void Minimize() => NativeMethods.ShowWindow(_hwnd, NativeMethods.SwMinimize);
        public bool Maximize() => NativeMethods.ShowWindow(_hwnd, NativeMethods.SwMaximize);
        public bool Restore() => NativeMethods.ShowWindow(_hwnd, NativeMethods.SwRestore);
        public void SetBorderAndTitleBar(bool hasBorder, bool hasTitleBar)
        {
            HasBorder = hasBorder;
            HasTitleBar = hasTitleBar;
        }
    }

    public sealed class FullScreenPresenter : AppWindowPresenter
    {
        internal FullScreenPresenter() { Kind = AppWindowPresenterKind.FullScreen; }
        public static FullScreenPresenter Create() => new();
    }

    public sealed class AppWindow
    {
        private static readonly Dictionary<nint, AppWindow> s_known = new();
        private readonly nint _hwnd;
        private bool _visible = true;

        internal AppWindow(nint hwnd)
        {
            _hwnd = hwnd;
            TitleBar = new AppWindowTitleBar();
            Presenter = new OverlappedPresenter(hwnd);
        }

        public static AppWindow GetFromWindowId(Microsoft.UI.WindowId id)
        {
            nint hwnd = (nint)id.Value;
            if (s_known.TryGetValue(hwnd, out AppWindow? existing)) return existing;
            AppWindow created = new(hwnd);
            s_known[hwnd] = created;
            return created;
        }

        public Microsoft.UI.WindowId Id => new((ulong)_hwnd);

        public AppWindowPresenter Presenter { get; private set; }

        public AppWindowTitleBar TitleBar { get; }

        public bool IsVisible
        {
            get
            {
                // 以真实窗口状态为准：其它代码（置顶/最小化）可能直接动过 HWND。
                return NativeMethods.IsWindowVisible(_hwnd) && !NativeMethods.IsIconic(_hwnd);
            }
        }

        public Windows.Graphics.SizeInt32 Size
        {
            get
            {
                NativeMethods.GetWindowRect(_hwnd, out NativeMethods.Rect r);
                return new Windows.Graphics.SizeInt32(r.Right - r.Left, r.Bottom - r.Top);
            }
        }

        public Windows.Graphics.SizeInt32 ClientSize
        {
            get
            {
                NativeMethods.GetClientRect(_hwnd, out NativeMethods.Rect r);
                return new Windows.Graphics.SizeInt32(r.Right - r.Left, r.Bottom - r.Top);
            }
        }

        public Windows.Graphics.PointInt32 Position
        {
            get
            {
                NativeMethods.GetWindowRect(_hwnd, out NativeMethods.Rect r);
                return new Windows.Graphics.PointInt32(r.Left, r.Top);
            }
        }

        public string Title { get; set; } = string.Empty;

        public nint OwnerWindowHandle { get; set; }

        public event global::Microsoft.UI.Xaml.AnyEventHandler? Changed;
        public event global::Microsoft.UI.Xaml.AnyEventHandler? Closing;

        public void Show()
        {
            _visible = true;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShow);
            RaiseChanged(new AppWindowChangedEventArgs { DidVisibilityChange = true });
        }

        public void Hide()
        {
            _visible = false;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
            RaiseChanged(new AppWindowChangedEventArgs { DidVisibilityChange = true });
        }

        public void ShowOnce() => Show();

        public void Resize(Windows.Graphics.SizeInt32 size)
        {
            NativeMethods.SetWindowPos(_hwnd, 0, 0, 0, size.Width, size.Height,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            RaiseChanged(new AppWindowChangedEventArgs { DidSizeChange = true });
        }

        public void ResizeClient(Windows.Graphics.SizeInt32 size)
        {
            // 客户区尺寸 → 含边框的窗口尺寸（窗口样式决定边框厚度，用 AdjustWindowRect 换算）
            NativeMethods.Rect rect = new() { Left = 0, Top = 0, Right = size.Width, Bottom = size.Height };
            NativeMethods.AdjustWindowRect(ref rect, (uint)NativeMethods.GetWindowLong(_hwnd, NativeMethods.GwlStyle), false);
            Resize(new Windows.Graphics.SizeInt32(rect.Right - rect.Left, rect.Bottom - rect.Top));
        }

        public void Move(Windows.Graphics.PointInt32 position)
        {
            NativeMethods.SetWindowPos(_hwnd, 0, position.X, position.Y, 0, 0,
                NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            RaiseChanged(new AppWindowChangedEventArgs { DidPositionChange = true });
        }

        public void MoveAndResize(Windows.Graphics.RectInt32 rect)
        {
            NativeMethods.SetWindowPos(_hwnd, 0, rect.X, rect.Y, rect.Width, rect.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            RaiseChanged(new AppWindowChangedEventArgs { DidSizeChange = true, DidPositionChange = true });
        }

        public void SetPresenter(AppWindowPresenterKind kind)
        {
            Presenter = kind switch
            {
                AppWindowPresenterKind.FullScreen => new FullScreenPresenter(),
                _ => new OverlappedPresenter(_hwnd),
            };
            RaiseChanged(new AppWindowChangedEventArgs { DidPresenterChange = true });
        }

        public void SetPresenter(AppWindowPresenter presenter)
        {
            Presenter = presenter;
            RaiseChanged(new AppWindowChangedEventArgs { DidPresenterChange = true });
        }

        public void SetIcon(string iconPath)
        {
            if (string.IsNullOrEmpty(iconPath) || !System.IO.File.Exists(iconPath)) return;
            try
            {
                nint large = NativeMethods.LoadImage(0, iconPath, NativeMethods.ImageIcon, 32, 32, NativeMethods.LrLoadFromFile);
                nint small = NativeMethods.LoadImage(0, iconPath, NativeMethods.ImageIcon, 16, 16, NativeMethods.LrLoadFromFile);
                if (large != 0) NativeMethods.SendMessage(_hwnd, NativeMethods.WmSetIcon, (nint)1, large);
                if (small != 0) NativeMethods.SendMessage(_hwnd, NativeMethods.WmSetIcon, (nint)0, small);
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("AppWindow.SetIcon", caught); }
        }

        public void SetIcon(nint iconHandle)
        {
            NativeMethods.SendMessage(_hwnd, NativeMethods.WmSetIcon, (nint)1, iconHandle);
            NativeMethods.SendMessage(_hwnd, NativeMethods.WmSetIcon, (nint)0, iconHandle);
        }

        public void SetTaskbarIcon(nint iconHandle) => SetIcon(iconHandle);

        public void Destroy() => NativeMethods.DestroyWindow(_hwnd);

        public void AssociateWithOwnerWindow() { }

        internal void RaiseChanged(AppWindowChangedEventArgs args) => Changed?.Invoke(this, args);
        internal void RaiseClosing(AppWindowClosingEventArgs args) => Closing?.Invoke(this, args);
    }

    public sealed class DisplayArea
    {
        public static DisplayArea Primary => new();
        public Windows.Graphics.RectInt32 WorkArea
        {
            get
            {
                NativeMethods.SystemParametersInfo(NativeMethods.SpiGetWorkArea, 0, out NativeMethods.Rect r, 0);
                return new Windows.Graphics.RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
        }
        public Windows.Graphics.RectInt32 OuterBounds => WorkArea;
        public static DisplayArea GetFromWindowId(Microsoft.UI.WindowId id, DisplayAreaFallback fallback) => new();
    }

    public enum DisplayAreaFallback
    {
        None = 0,
        Nearest = 1,
        Primary = 2,
    }

    internal static class NativeMethods
    {
        internal const int SwShow = 5;
        internal const int SwHide = 0;
        internal const int SwMinimize = 6;
        internal const int SwMaximize = 3;
        internal const int SwRestore = 9;
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoZOrder = 0x0004;
        internal const uint SwpNoActivate = 0x0010;
        internal const int GwlStyle = -16;
        internal const uint ImageIcon = 1;
        internal const uint LrLoadFromFile = 0x00000010;
        internal const uint WmSetIcon = 0x0080;
        internal const uint SpiGetWorkArea = 0x0030;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint hwnd, int cmd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(nint hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint hwnd, out Rect rect);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint hwnd, out Rect rect);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustWindowRect(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint GetWindowLong(nint hwnd, int index);

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint flags);

        internal static nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint flags)
            => LoadImageW(hInst, name, type, cx, cy, flags);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint hwnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SystemParametersInfo(uint action, uint param, out Rect rect, uint update);
    }
}
