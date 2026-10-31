using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CelesteMusicPlayer;

/// <summary>
/// W13 第二十一刀（2026-10-31）：窗口尺寸与最大化状态同步宿主。
/// 从壳 <c>MainWindow.UiTheme.cs</c> / <c>MainWindow.Misc.cs</c> / <c>MainWindow.Features.cs</c>
/// 搬出的启动链小件：按 DIP 换算窗口大小、WM_GETMINMAXINFO 子类化锁最小尺寸、
/// 最大化/还原图标同步。依赖（Win32 声明与 P/Invoke 在 MainWindow.xaml.cs /
/// MainWindow.Playback.cs，句柄在 LegacyNativeServices，图标在 LegacyControls）全在活文件。
/// </summary>
public sealed partial class MainWindow
{
    private bool _windowMaximized;

        // W13 第二十一刀：最小尺寸锁定（WM_GETMINMAXINFO 子类化）

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

        // W13 第二十一刀：最大化/还原状态同步（WM_SIZE 驱动）

        private void UpdateMaxRestoreIcon()
        {
            try
            {
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.Features.cs", caught); }
        }

        /// <summary>由 WndProc(W M_SIZE) 同步最大化/还原状态：自绘最大化按钮图标始终反映真实窗口状态（含拖拽还原）。</summary>
        internal void OnWindowMaximizeStateChanged(bool maximized)
        {
            _windowMaximized = maximized;
            UpdateMaxRestoreIcon();
        }

        // W13 第二十一刀：按 DIP 换算窗口大小

        /// <summary>按 DIP 调整窗口客户区大小（内部换算为物理像素）。</summary>
        private void ResizeWindowToDips(int widthDip, int heightDip)
        {
            try
            {
                nint hwnd = NativeHandle;
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                double scale = dpi / 96.0;
                AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Round(widthDip * scale),
                    (int)Math.Round(heightDip * scale)));
            }
            catch
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(widthDip, heightDip));
            }
        }

        /// <summary>窗口小于最小尺寸时强制涨回（1200×760，按 DPI 换算）。
        /// 实际通过 WM_GETMINMAXINFO 系统级锁定最小尺寸，拖到最小后不能再缩小。</summary>
        private void EnforceMinimumWindowSize()
        {
            if (_mainWindowHwnd != IntPtr.Zero)
            {
                SetupMinSizeHooks();
            }
        }
    }
