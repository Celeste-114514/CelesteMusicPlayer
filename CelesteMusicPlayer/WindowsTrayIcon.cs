using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// Win32 通知区图标（Shell_NotifyIconW 直调），替代 H.NotifyIcon.WinUI。
    ///
    /// 为什么自己写：H.NotifyIcon 的 TaskbarIcon 是 WinUI 控件，菜单只能走 MenuFlyout，
    /// 而 MenuFlyout 跑在独立 XAML 树里、主题继承不到，深色底上文字发闷、底部还透背景。
    /// 托盘要跟网页外壳同一套观感，就必须换承载方式（见
    /// collab/RESULT-20261028-tray-without-winui-small-plan.md）。
    ///
    /// 这里只管「图标 + 消息」这一层：注册/注销、左键右键、Explorer 重启后补建、
    /// 取图标矩形用于菜单定位，以及 WebView2 菜单不可用时退到系统原生弹出菜单。
    /// 菜单界面本身在 TrayMenuWindow。
    /// </summary>
    internal sealed class WindowsTrayIcon : IDisposable
    {
        private const int NIM_ADD = 0x0;
        private const int NIM_MODIFY = 0x1;
        private const int NIM_DELETE = 0x2;

        private const uint NIF_MESSAGE = 0x01;
        private const uint NIF_ICON = 0x02;
        private const uint NIF_TIP = 0x04;
        private const uint NIF_INFO = 0x10;

        private const uint NIIF_INFO = 0x01;
        private const uint NIIF_ERROR = 0x03;

        private const uint WM_TRAY = 0x8000 + 1;          // WM_APP + 1
        private const int NIN_BALLOONUSERCLICK = 0x0405;

        private const uint MF_STRING = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint MF_GRAYED = 0x0001;
        private const uint MF_CHECKED = 0x0008;
        private const uint MF_UNCHECKED = 0x0000;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_NONOTIFY = 0x0080;

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x0010;

        private readonly string _className;
        private IntPtr _hwnd;
        private IntPtr _hicon;
        private bool _added;
        private bool _disposed;
        private uint _taskbarCreated;
        private long _lastRightClickTick;
        private readonly WndProcDelegate _wndProc;   // 必须保活，否则 GC 收回委托后窗口就死了

        internal event Action? LeftClick;
        internal event Action? RightClick;
        internal event Action? BalloonClick;

        internal bool IsCreated => _added;

        internal WindowsTrayIcon()
        {
            _wndProc = WindowProc;
            _className = "CelesteTrayHost_" + Guid.NewGuid().ToString("N");
            CreateHostWindow();
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            _hicon = LoadTrayIcon();
            TryAdd();
        }

        /// <summary>注册图标。失败不抛：调用方据此决定要不要退回旧实现。</summary>
        internal bool TryAdd()
        {
            if (_disposed || _hwnd == IntPtr.Zero) return false;
            try
            {
                NOTIFYICONDATAW data = BaseData();
                data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                data.uCallbackMessage = WM_TRAY;
                data.hIcon = _hicon;
                data.szTip = "CelesteMusicPlayer";
                _added = Shell_NotifyIconW(NIM_ADD, ref data);
                if (!_added)
                {
                    StartupLog.Write("[托盘] Shell_NotifyIcon 注册失败，Win32 托盘不可用（err=" + Marshal.GetLastWin32Error() + "）");
                }
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WindowsTrayIcon.TryAdd", ex);
                _added = false;
            }
            return _added;
        }

        internal void Remove()
        {
            if (!_added) return;
            try
            {
                NOTIFYICONDATAW data = BaseData();
                Shell_NotifyIconW(NIM_DELETE, ref data);
            }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayIcon.Remove", ex); }
            _added = false;
        }

        /// <summary>气泡提示。没有 AUMID 的应用在 Win10/11 上仍会走老式气泡，能用就够了。</summary>
        internal bool ShowBalloon(string title, string text, bool error = false)
        {
            if (!_added) return false;
            try
            {
                NOTIFYICONDATAW data = BaseData();
                data.uFlags = NIF_INFO;
                data.szInfoTitle = Trim(title, 63);
                data.szInfo = Trim(text, 255);
                data.dwInfoFlags = error ? NIIF_ERROR : NIIF_INFO;
                data.uTimeout = 10000;
                return Shell_NotifyIconW(NIM_MODIFY, ref data);
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WindowsTrayIcon.ShowBalloon", ex);
                return false;
            }
        }

        /// <summary>取图标在屏幕上的矩形（物理像素）。用于把菜单贴在图标上方而不是鼠标尖上。</summary>
        internal bool TryGetIconRect(out RECT rect)
        {
            rect = default;
            if (!_added) return false;
            try
            {
                var id = new NOTIFYICONIDENTIFIER
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                    hWnd = _hwnd,
                    uID = 1
                };
                return Shell_NotifyIconGetRect(ref id, out rect) == 0;
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("WindowsTrayIcon.TryGetIconRect", ex);
                return false;
            }
        }

        /// <summary>
        /// 退路菜单已按用户要求移除（2026-10-28：不要任何系统方块）。
        /// 保留这条注释是为了以后别再把它接回来 —— 托盘菜单只有网页这一种形态。
        /// </summary>

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Remove();
            try
            {
                if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayIcon.Dispose.DestroyWindow", ex); }
            _hwnd = IntPtr.Zero;
            if (_hicon != IntPtr.Zero)
            {
                try { DestroyIcon(_hicon); } catch { }
                _hicon = IntPtr.Zero;
            }
        }

        // —— 内部 ——

        private NOTIFYICONDATAW BaseData() => new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };

        private static string Trim(string? text, int max)
            => string.IsNullOrEmpty(text) ? "" : (text!.Length <= max ? text : text.Substring(0, max));

        private void CreateHostWindow()
        {
            IntPtr instance = GetModuleHandle(null);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = _className
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                StartupLog.Write("[托盘] RegisterClassEx 失败（err=" + Marshal.GetLastWin32Error() + "）");
            }

            // 必须是**顶层窗口**（不是 message-only）：TaskbarCreated 是广播消息，
            // 只有顶层窗口收得到；用 message-only 窗口的话 Explorer 重启后图标永远补不回来。
            // 样式里没有 WS_VISIBLE，所以它一直隐藏；TOOLWINDOW 保证不进 Alt+Tab。
            _hwnd = CreateWindowExW(0x00000080 /* WS_EX_TOOLWINDOW */, _className, "CelesteTray",
                0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                StartupLog.Write("[托盘] 创建托盘宿主窗口失败（err=" + Marshal.GetLastWin32Error() + "）");
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_taskbarCreated != 0 && msg == _taskbarCreated)
            {
                // Explorer 重启了，通知区里所有图标都没了 —— 重新注册
                _added = false;
                TryAdd();
                return IntPtr.Zero;
            }

            if (msg == WM_TRAY)
            {
                int code = (int)(lParam.ToInt64() & 0xFFFF);
                switch (code)
                {
                    case 0x0202: LeftClick?.Invoke(); break;        // WM_LBUTTONUP
                    case 0x0203: LeftClick?.Invoke(); break;        // WM_LBUTTONDBLCLK
                    case 0x0205: _lastRightClickTick = Environment.TickCount64; RightClick?.Invoke(); break;   // WM_RBUTTONUP
                    case 0x007B:
                        // 右键既会来 WM_RBUTTONUP 也可能补一条 WM_CONTEXTMENU（键盘菜单键只会来这条）。
                        // 中间没有去重的话菜单会"弹出又被自己收起" —— 400ms 内视作同一次。
                        if (Environment.TickCount64 - _lastRightClickTick > 400) RightClick?.Invoke();
                        break;
                    case NIN_BALLOONUSERCLICK: BalloonClick?.Invoke(); break;
                }
                return IntPtr.Zero;
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private static IntPtr LoadTrayIcon()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(path))
                {
                    int size = GetSystemMetrics(49 /* SM_CXSMICON */);
                    IntPtr handle = LoadImageW(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
                    if (handle != IntPtr.Zero) { StartupLog.Write("[tray.icon] loaded application ICO " + size + "px"); return handle; }
                    StartupLog.Write("[tray.icon] LoadImageW failed err=" + Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex) { StartupLog.WriteException("WindowsTrayIcon.LoadTrayIcon", ex); }

            // 兜底用系统默认图标，至少图标能出来
            return LoadIconW(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */);
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeout;   // 与 uVersion 共用一个联合体
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint RegisterWindowMessage(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? text);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
