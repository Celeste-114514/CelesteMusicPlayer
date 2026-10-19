// 迷你播放器 —— 原生 chrome 收尾（2026-10-19 锯齿修复：圆角改由 DWM 负责）。
//
// 根因：构造函数里只做了 WinUI 层的 OverlappedPresenter.SetBorderAndTitleBar(false,false)
// + 标题栏折叠成 0 高，系统那一圈 1px 窗口描边（Win11 深色底下呈白色）仍然在。
//
// 分工：
//   - SetWindowLong 剥 WS_THICKFRAME / WS_CAPTION / WS_BORDER / WS_DLGFRAME 与 ex 边样式；
//   - 子类化吞掉 WM_NCCALCSIZE / WM_NCPAINT：客户区直接吃满整窗，非客户区根本不画；
//   - 圆角交给 DWM（DWMWCP_ROUND）：Win11 8px@96dpi，**DWM 自己做抗锯齿**；
//   - 兜底把 DWM 的 BORDER / CAPTION 颜色推成当前背景色：万一还有残留 1px 也融进背景。
//
// ⚠ 为什么不再用 SetWindowRgn 打圆角（2026-10-19 用户报「四个角有像素点的锯齿」）：
//   GDI 裁剪区是 **1 位掩码**，圆弧必然锯齿，抗锯齿无解。全窗口唯一带 AA 的圆角来源
//   就是 DWM（DWMWA_WINDOW_CORNER_PREFERENCE = ROUND）。
//   代价：圆角半径锁死系统值（8px@96dpi，DPI 缩放时按 DIP 恒定）；想更"肥"的圆角
//   只能回到 1 位掩码 —— 圆胖和平滑二选一，用户拍板要平滑。
//   XAML 侧最外圈 rim 同步改为「内缩 2px + 圆角 6」（= 8px 窗角 - 2px 内缩，与窗角同心），
//   四角不会被窗形切掉一截。
//
// ⚠ 坑（都别再踩）：
//   ① 不加 WS_EX_TOOLWINDOW：任务栏按钮必须留着。
//   ② 扩展面板动画现在是**逐帧 AppWindow.Resize**（见 MiniPlayerWindow.xaml.cs），
//      Changed 每帧都会触发；动画期间不要在这里推 DWM 属性（白给），见下面的事件处理器。

using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace CelesteMusicPlayer;

partial class MiniPlayerWindow
{
    private nint _hwnd;
    private bool _subclassInstalled;
    private SubclassProc? _subclassProc;

    private const int SubclassId = 8; // 与零散播放窗口（7）错开，同线程两窗口并存时互不干扰

    /// <summary>
    /// 原生层去边框（root.Loaded 里调一次；句柄这时才拿得到）。
    /// 顺序有讲究：先剥样式再 SetWindowPos(FRAMECHANGED) 让系统按新样式重算，
    /// 最后把 DWM 圆角偏好设成 ROUND —— 和零散播放窗口同一条流水线，本机实测无白框。
    /// </summary>
    private void ApplyWindowChromeNative()
    {
        Safe(() =>
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            InstallWindowSubclassIfNeeded();

            // ① 剥 ex 边样式（模态框边 / 客户区边 / 静态边 / 窗口边）
            int ex = GetWindowLong(_hwnd, GwlExStyle);
            ex &= ~(WsExDlgModalFrame | WsExClientEdge | WsExStaticEdge | WsExWindowEdge);
            SetWindowLong(_hwnd, GwlExStyle, ex);

            // ② 剥 WS_THICKFRAME / WS_CAPTION / WS_BORDER / WS_DLGFRAME 等
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

            // ③ 圆角归 DWM：系统 8px@96dpi，带抗锯齿（替代原来锯齿的 SetWindowRgn）
            uint corner = DwmWcpRound;
            DwmSetWindowAttribute(_hwnd, DwmWaWindowCornerPreference, ref corner, sizeof(uint));

            // ④ 兜底色：真有残留 1px 边框也融进当前背景
            PushDwmFrameColor(_bgMode);

            // ⑤ 让系统按新样式重算非客户区
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        });
    }

    /// <summary>把 DWM 边框/标题栏颜色推成当前背景色（COLORREF = 0x00BBGGRR）。</summary>
    private void PushDwmFrameColor(string mode)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        uint color = mode switch
        {
            "Cover" => 0x00000000,   // 封面态最外圈是压暗纱，取黑
            _ => 0x00161010          // 深色 / 毛玻璃 #101016
        };
        DwmSetWindowAttribute(_hwnd, DwmWaBorderColor, ref color, sizeof(uint));
        DwmSetWindowAttribute(_hwnd, DwmWaCaptionColor, ref color, sizeof(uint));
    }

    private void MiniAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        // 扩展面板动画期间窗口逐帧 resize，Changed 每帧都触发 —— 这时不碰 DWM 属性
        // （每帧两次 DwmSetWindowAttribute 是白给）。属性本身跨 resize 保持，
        // 动画结束尺寸稳定后这里再推一次只是双保险。
        if (_panelAnimating)
        {
            return;
        }

        PushDwmFrameColor(_bgMode);
    }

    private void InstallWindowSubclassIfNeeded()
    {
        if (_subclassInstalled || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _subclassProc = MiniSubclassProc;
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

    private IntPtr MiniSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        // 去掉非客户区：顶部标题条与外圈系统边都不再画
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

    // ---------------------------------------------------------------- 常量

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsThickFrame = 0x00040000;
    private const int WsCaption = 0x00C00000;
    private const int WsSysMenu = 0x00080000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int WsBorder = 0x00800000;
    private const int WsDlgFrame = 0x00400000;
    private const int WsExDlgModalFrame = 0x00000001;
    private const int WsExClientEdge = 0x00000200;
    private const int WsExStaticEdge = 0x00200000;
    private const int WsExWindowEdge = 0x00000100;
    private const int WmNcCalcSize = 0x0083;
    private const int WmNcPaint = 0x0085;
    private const int DwmWaWindowCornerPreference = 33;
    private const int DwmWaBorderColor = 34;
    private const int DwmWaCaptionColor = 35;
    // DWMWCP_DONOTROUND=1 / DWMWCP_ROUND=2（ROUND = Win11 8px@96dpi，DWM 自带 AA）
    private const uint DwmWcpRound = 2;
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

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
}
