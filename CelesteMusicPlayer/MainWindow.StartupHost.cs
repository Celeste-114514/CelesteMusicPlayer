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
                if (WindowMaxRestoreIcon != null)
                {
                    WindowMaxRestoreIcon.Glyph = _windowMaximized ? "\uE923" : "\uE922";
                }
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

        // W13 第二十一刀：关闭策略（托盘/退出/每次询问）

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            if (_closePromptOpen)
            {
                return;
            }

            _ = HandleCloseRequestAsync();
        }

        private async Task<CloseWindowAction> ShowCloseChoiceDialogAsync()
        {
            if (Content?.XamlRoot == null)
            {
                return CloseWindowAction.Exit;
            }

            var dontAsk = new CheckBox
            {
                Content = "下次不再询问",
                Margin = new Thickness(0, 8, 0, 0)
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "关闭主界面时，要将播放器缩小到系统托盘继续在后台运行，还是退出播放器？",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(dontAsk);

            var dialog = new ContentDialog
            {
                Title = "关闭 CelesteMusicPlayer",
                Content = panel,
                PrimaryButtonText = "缩小到托盘",
                SecondaryButtonText = "退出播放器",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            // 关闭对话框按钮用当前主题色(ContentDialog 局部资源)
            try
            {
                Windows.UI.Color closeAccent = ResolveAccentColor();
                var closeBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(closeAccent);
                dialog.Resources["AccentButtonBackground"] = closeBrush;
                dialog.Resources["AccentButtonBackgroundPointerOver"] = closeBrush;
                dialog.Resources["AccentButtonBackgroundPressed"] = closeBrush;
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("MainWindow.xaml.cs", caught); }

            ColorHelper.ApplyDialogAccent(dialog);
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
            {
                return CloseWindowAction.Ask; // 取消：不做事
            }

            CloseWindowAction chosen = result == ContentDialogResult.Primary
                ? CloseWindowAction.MinimizeToTray
                : CloseWindowAction.Exit;

            if (dontAsk.IsChecked == true)
            {
                AppClosePreferences.Save(new AppClosePreferencesState
                {
                    DontAskAgain = true,
                    PreferredAction = chosen == CloseWindowAction.Exit
                        ? nameof(CloseWindowAction.Exit)
                        : nameof(CloseWindowAction.MinimizeToTray)
                });
            }

            return chosen;
        }

        private void MinimizeToTray()
        {
            try
            {
                _trayIcon ??= new WindowsTrayService(this);
                _trayIcon.Show();
                if (!_trayIcon.IsCreated)
                {
                    // 别再假装成功：图标没建起来就藏窗口，右下角会一个入口都没有。
                    // 补救：再启动一次 CelesteMusicPlayer.exe，单实例会把命令转给本进程把窗口拉回来。
                    StartupLog.Write("托盘: 图标尚未创建成功，已安排后台补建；若最终仍失败，可再次运行程序唤回窗口");
                }
                StartupLog.Write("托盘: MinimizeToTray 完成 (图标已创建=" + _trayIcon.IsCreated + ")");
                AppWindow.Hide();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("MinimizeToTray", ex);
                // 托盘失败则直接退出，避免关不掉
                ExitApplication();
            }
        }

        private async Task HandleCloseRequestAsync()
        {
            if (_closePromptOpen)
            {
                return;
            }

            _closePromptOpen = true;
            try
            {
                AppClosePreferencesState prefs = AppClosePreferences.Load();
                CloseWindowAction action = AppClosePreferences.ResolveAction(prefs);
                if (action == CloseWindowAction.Ask)
                {
                    action = await ShowCloseChoiceDialogAsync();
                }

                switch (action)
                {
                    case CloseWindowAction.MinimizeToTray:
                        MinimizeToTray();
                        break;
                    case CloseWindowAction.Exit:
                        ExitApplication();
                        break;
                }
            }
            finally
            {
                _closePromptOpen = false;
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
