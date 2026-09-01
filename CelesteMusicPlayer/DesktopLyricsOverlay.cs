using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;
using Color = Windows.UI.Color;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 桌面歌词窗口（**已重写**）。
    ///
    /// 旧版（删掉前 1324 行）是用裸 Win32 自定义窗口类 + `SetTimer` + GDI `DrawText` 自绘歌词 + 卡拉 OK 双色工具栏，
    /// coriander 的代码路数：类型字号、不透明度、双色进度全部要 P/Invoke 拦下、再手动贴窗口。多个 P/Invoke 结构体散落在文件里，
    /// DPI 适配不到位、字号/颜色改一次要改动七八处。播放器主题变更时还要重画 Surface，闪烁 + 卡顿。
    /// 不重做没法解决"粗一点、好看点"以外的体验问题。
    ///
    /// 新版直接走 WinUI3 `Window`：
    /// - 三行（上一行/当前行/下一行），关闭"双行"即单行；当前行高亮 + 字号放大，未播放行低对比
    /// - 拖动 = 整窗 Pointer → `AppWindow.Move()`，DPI 由 `GetDpiForWindow` 自适应
    /// - 锁定 = `WS_EX_TRANSPARENT` 整窗鼠标穿透，右上角小"🔓"按钮可戳解锁
    /// - 双击 = 切换锁定 / 解锁；右键 = 强制解锁（保留旧 API 兼容）
    /// - 字号、不透明度、已播放/未播放色 → 直接 `TextBlock.FontSize` / `RootGrid.Opacity` / `SolidColorBrush`
    /// - 进度同步：主窗 `Sync(pos)` → 二分定位当前行，重设三行；本窗不主动轮询
    /// - 位置记忆：拖动结束 + Dispose 时把 `(AppWindow.X, AppWindow.Y)` 落回 `AppSettingsStore.DesktopLyricPosX/Y`
    ///
    /// **与主窗口的公开契约保持完全兼容**：事件 `ClosedByUser`、属性 `PositionProvider` / `IsLocked` / `IsVisible` / `CurrentPosition`、
    /// 方法 `SetSavedPosition` / `Show` / `Close` / `SetLyrics` / `Sync` / `SetLocked` / `ApplySettings` / `SetPlaybackPaused` / `Dispose` 全部保留。
    /// </summary>
    internal sealed partial class DesktopLyricsOverlay : IDisposable
    {
        // ===== Win32 样式常量 =====
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW  = 0x00000080;

        // ===== 状态 =====
        private IntPtr _hwnd;
        private int _savedExStyle;
        private bool _exStyleSaved;
        private bool _disposed;
        private bool _isClosingFromSelf;   // 防 ClosedByUser 订阅方反向触发 Close 造成递归

        private readonly List<LyricLine> _lines = new();
        private int _currentIndex = -1;
        private TimeSpan _lastSyncPosition;
        private bool _hasSyncedOnce;

        // 拖动
        private bool _dragging;
        private Point _dragStartDipPoint;       // 屏幕坐标（DIP）
        private PointInt32 _dragStartWindowPos; // 物理像素位置

        // 设置缓存
        private double _fontSize = 28;
        private int _opacityPercent = 100;
        private Color _playedColor = Color.FromArgb(255, 64, 180, 255);
        private Color _unplayedColor = Color.FromArgb(255, 245, 245, 245);
        private bool _doubleLine = true;
        private bool _clickThroughSetting;
        private bool _hideWhenPaused;
        private bool _hideWithoutLyric;
        private bool _paused;

        // ============================== 公开契约 ==============================

        /// <summary>窗被用户主动关闭（点了 X / 切主窗口开关触发 Dispose），通知主窗口做清理。</summary>
        public event Action? ClosedByUser;

        /// <summary>可选：主窗口可提供一个当前位置 provider。备用，本版本未使用（主窗口直接 Sync）。</summary>
        public Func<TimeSpan>? PositionProvider { get; set; }

        /// <summary>当前是否被锁定。锁定时窗透明穿透，露出小"🔓"按钮。</summary>
        public bool IsLocked { get; private set; }

        /// <summary>当前是否已 Show。WinUI3 Window 没显式可见属性，自己 track。</summary>
        public bool IsVisible { get; private set; }

        /// <summary>当前窗位置（物理像素）。主窗口调用以做"关闭前保存位置"。</summary>
        public (int X, int Y) CurrentPosition
        {
            get
            {
                try
                {
                    EnsureHwnd();
                    return AppWindow != null ? (AppWindow.Position.X, AppWindow.Position.Y) : (0, 0);
                }
                catch
                {
                    return (0, 0);
                }
            }
        }

        public DesktopLyricsOverlay()
        {
            InitializeComponent();

            EnsureHwnd();

            // Window.Closed 事件是"用户按 X / 程序 Close() / 进程结束"统一触发
            this.Closed += DesktopLyricsOverlay_Closed;

            // 让窗没有任务栏图标，不在 Alt+Tab 出现
            int exStyle = GetExStyle(_hwnd, GWL_EXSTYLE);
            _savedExStyle = exStyle;
            exStyle |= WS_EX_TOOLWINDOW;
            SetExStyle(_hwnd, GWL_EXSTYLE, exStyle);
            _exStyleSaved = true;

            // 标题栏砍掉：WinUI3 风格
            ExtendsContentIntoTitleBar = true;
            try
            {
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
                AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                // 不显示系统按钮：自己画
                AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            }
            catch { }

            // 起始大小留充足（双行 + 工具栏），主窗口可调
            try
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 200));
            }
            catch { }

            UpdateLockedUi();
            UpdateLineUi();
            UpdateDimmedOpacity();
        }

        /// <summary>由主窗口调入：在 EnsureDesktopLyricsWindow 后注入上次保存的位置。</summary>
        public void SetSavedPosition(int x, int y)
        {
            Safe(() =>
            {
                EnsureHwnd();
                AppWindow.MoveAndResize(new RectInt32(x, y, AppWindow.Size.Width, AppWindow.Size.Height));
            }, "DesktopLyricsOverlay.SetSavedPosition");
        }

        /// <summary>显示窗。仅切换可见性，不重置任何状态。</summary>
        public void Show()
        {
            if (_disposed)
            {
                return;
            }
            Safe(() =>
            {
                EnsureHwnd();
                AppWindow.Show();
                IsVisible = true;
                ApplyPausedVisibility();
            }, "DesktopLyricsOverlay.Show");
        }

        /// <summary>关闭窗。与 Window.Close() 区分（同名时不能 this.Close()），用显式类型调用基类。</summary>
        public void Close()
        {
            if (_disposed)
            {
                return;
            }
            // 标志位：本路径不期望触发 ClosedByUser（主窗口主动关闭）
            _isClosingFromSelf = true;
            try
            {
                ((Window)this).Close();
            }
            catch (Exception caught) { StartupLog.WriteException("DesktopLyricsOverlay.Close", caught); }
            IsVisible = false;
        }

        /// <summary>主窗口每帧/30fps 调：传入当前播放位置，本窗根据时间找到当前行并刷新。</summary>
        public void Sync(TimeSpan position)
        {
            if (_disposed)
            {
                return;
            }
            _lastSyncPosition = position;
            _hasSyncedOnce = true;
            UpdateCurrentLine();
        }

        /// <summary>加载新歌词或切换歌曲时调：传入新行（空数组也行）。</summary>
        public void SetLyrics(IReadOnlyList<LyricLine> lines)
        {
            _lines.Clear();
            if (lines != null)
            {
                _lines.AddRange(lines);
            }
            _currentIndex = -1;
            UpdateLineUi();
        }

        /// <summary>锁定 / 解锁切换。锁定 = 整窗 WS_EX_TRANSPARENT。</summary>
        public void SetLocked(bool locked)
        {
            if (IsLocked == locked)
            {
                return;
            }
            IsLocked = locked;
            ApplyClickThrough();
            UpdateLockedUi();
        }

        /// <summary>主窗口切主题 / 改设置时调：刷新字号、不透明度、双行、颜色、穿透、暂停时是否隐藏。</summary>
        public void ApplySettings(AppSettingsState settings)
        {
            _fontSize = Math.Clamp(settings.DesktopLyricFontSize, 14, 64);
            _opacityPercent = Math.Clamp(settings.DesktopLyricOpacity, 20, 100);
            _playedColor = ParseHexColor(settings.DesktopLyricPlayedColor, _playedColor);
            _unplayedColor = ParseHexColor(settings.DesktopLyricUnplayedColor, _unplayedColor);
            _doubleLine = settings.DesktopLyricDoubleLine;
            _clickThroughSetting = settings.DesktopLyricClickThrough;
            _hideWhenPaused = settings.DesktopLyricHideWhenPaused;
            _hideWithoutLyric = settings.DesktopLyricHideWithoutLyric;

            ApplyClickThrough();
            UpdateLineUi();
            UpdateDimmedOpacity();
        }

        /// <summary>主窗口调：暂停时使用 hide-when-paused 设置决定藏起来。</summary>
        public void SetPlaybackPaused(bool paused)
        {
            _paused = paused;
            ApplyPausedVisibility();
        }

        /// <summary>主窗口调：释放窗与所有 hook。位置保存由 Window.Closed 兜底，这里只补一刀。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Safe(() =>
            {
                try { PersistPositionToSettings(); } catch { }
                try { _isClosingFromSelf = true; ((Window)this).Close(); } catch { }
                IsVisible = false;
            }, "DesktopLyricsOverlay.Dispose");
        }

        // ============================== 内部 ==============================

        private void EnsureHwnd()
        {
            if (_hwnd != IntPtr.Zero)
            {
                return;
            }
            try
            {
                _hwnd = WindowNative.GetWindowHandle(this);
            }
            catch (Exception caught) { StartupLog.WriteException("DesktopLyricsOverlay.EnsureHwnd", caught); }
        }

        private void ApplyClickThrough()
        {
            // 设计决定：WS_EX_TRANSPARENT 作用于整窗命中区，无法局部保留按钮。
            // 锁定态为了能"点右上角 🔓 解锁"必须**不**穿透；锁定态靠"半透 + 字号缩小 + 隐藏工具栏"做视觉低调。
            // 用户在设置里勾的"穿透"是另一个维度——全程生效（解锁后也不抢桌面焦点）。
            if (_hwnd == IntPtr.Zero || !_exStyleSaved)
            {
                return;
            }
            try
            {
                int exStyle = GetExStyle(_hwnd, GWL_EXSTYLE);
                if (_clickThroughSetting)
                {
                    exStyle |= WS_EX_TRANSPARENT;
                }
                else
                {
                    exStyle &= ~WS_EX_TRANSPARENT;
                }
                SetExStyle(_hwnd, GWL_EXSTYLE, exStyle);
            }
            catch (Exception caught) { StartupLog.WriteException("DesktopLyricsOverlay.ApplyClickThrough", caught); }
        }

        private void UpdateLockedUi()
        {
            // 锁定：主工具栏收起，只显一个"开锁"小按钮；歌词走"低调"模式（缩字号 + 半透）便于能透过歌词点空白，但点击歌词仍然能拖动整窗
            TopBar.Visibility = IsLocked ? Visibility.Collapsed : Visibility.Visible;
            LockedUnlockButton.Visibility = IsLocked ? Visibility.Visible : Visibility.Collapsed;
            LockToggleIcon.Glyph = IsLocked ? "\uE72E" : "\uE785"; // E72E=解锁 / E785=锁定

            // 锁定视觉低调：仅作用于锁定态透出（不影响设置里的字号）
            if (CurrentLineText != null)
            {
                CurrentLineText.Opacity = IsLocked ? 0.55 : 1.0;
            }
            if (PrevLineText != null) PrevLineText.Opacity = IsLocked ? 0.30 : 0.55;
            if (NextLineText != null) NextLineText.Opacity = IsLocked ? 0.30 : 0.55;
        }

        private void UpdateLineUi()
        {
            if (CurrentLineText == null)
            {
                return;
            }
            // 双行 vs 单行
            PrevLineText.Visibility = _doubleLine ? Visibility.Visible : Visibility.Collapsed;
            NextLineText.Visibility = _doubleLine ? Visibility.Visible : Visibility.Collapsed;

            // 字号 + 颜色
            CurrentLineText.FontSize = _fontSize;
            PrevLineText.FontSize = _fontSize * 0.65;
            NextLineText.FontSize = _fontSize * 0.65;

            var playedBrush = new SolidColorBrush(_playedColor);
            var unplayedBrush = new SolidColorBrush(_unplayedColor);
            ApplyLinesToTextBlocks(playedBrush, unplayedBrush);
        }

        private void UpdateCurrentLine()
        {
            if (_lines.Count == 0)
            {
                if (_currentIndex != -1)
                {
                    _currentIndex = -1;
                    ApplyLinesToTextBlocks(new SolidColorBrush(_playedColor), new SolidColorBrush(_unplayedColor));
                }
                return;
            }

            // 二分定位最后一行 Time <= position
            int idx = -1;
            int lo = 0, hi = _lines.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_lines[mid].Time <= _lastSyncPosition)
                {
                    idx = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            // 翻译行：跳回上一行原文（避免高亮停翻译行上）
            if (idx >= 0 && _lines[idx].IsTranslation)
            {
                idx -= 1;
            }
            if (idx < -1 || idx >= _lines.Count)
            {
                idx = -1;
            }
            if (idx == _currentIndex)
            {
                return;
            }
            _currentIndex = idx;
            ApplyLinesToTextBlocks(new SolidColorBrush(_playedColor), new SolidColorBrush(_unplayedColor));
        }

        private void ApplyLinesToTextBlocks(Brush playedBrush, Brush unplayedBrush)
        {
            if (CurrentLineText == null)
            {
                return;
            }

            // 当前行文本/颜色（Opacity 由锁定状态统一管，不在本方法中设置）
            if (_currentIndex >= 0 && _currentIndex < _lines.Count)
            {
                LyricLine cur = _lines[_currentIndex];
                CurrentLineText.Text = string.IsNullOrWhiteSpace(cur.Text) ? "·" : cur.Text;
                CurrentLineText.Foreground = playedBrush;
            }
            else
            {
                // 歌曲开头/未到第一行/无歌词
                if (_lines.Count == 0)
                {
                    CurrentLineText.Text = "♪";
                    CurrentLineText.Foreground = unplayedBrush;
                }
                else
                {
                    CurrentLineText.Text = "";
                }
            }

            // 上一行 / 下一行
            if (_doubleLine)
            {
                int prevIdx = FindDisplayLineBefore(_currentIndex);
                int nextIdx = FindDisplayLineAfter(_currentIndex);
                PrevLineText.Text = (prevIdx >= 0) ? _lines[prevIdx].Text : "";
                NextLineText.Text = (nextIdx >= 0 && nextIdx < _lines.Count) ? _lines[nextIdx].Text : "";
                PrevLineText.Foreground = unplayedBrush;
                NextLineText.Foreground = unplayedBrush;
            }
        }

        /// <summary>在 anchor 之前找最近的"非翻译"行作为展示的上一行；找不到返回 -1。</summary>
        private int FindDisplayLineBefore(int anchor)
        {
            for (int i = anchor - 1; i >= 0; i--)
            {
                if (!_lines[i].IsTranslation)
                {
                    return i;
                }
            }
            return -1;
        }

        private int FindDisplayLineAfter(int anchor)
        {
            for (int i = anchor + 1; i < _lines.Count; i++)
            {
                if (!_lines[i].IsTranslation)
                {
                    return i;
                }
            }
            return -1;
        }

        private void ApplyPausedVisibility()
        {
            bool shouldHide =
                (_hideWhenPaused && _paused && _hasSyncedOnce)
                || (_hideWithoutLyric && _lines.Count == 0);
            if (shouldHide && IsVisible)
            {
                try { AppWindow.Hide(); IsVisible = false; } catch { }
            }
        }

        private void UpdateDimmedOpacity()
        {
            if (RootGrid != null)
            {
                RootGrid.Opacity = Math.Clamp(_opacityPercent / 100.0, 0.2, 1.0);
            }
        }

        private void PersistPositionToSettings()
        {
            if (_hwnd == IntPtr.Zero || AppWindow == null)
            {
                return;
            }
            try
            {
                int x = AppWindow.Position.X;
                int y = AppWindow.Position.Y;
                AppSettingsStore.Update(s =>
                {
                    s.DesktopLyricPosX = x;
                    s.DesktopLyricPosY = y;
                });
            }
            catch { }
        }

        private static Color ParseHexColor(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return fallback;
            }
            try
            {
                string s = hex.TrimStart('#');
                if (s.Length == 6)
                {
                    byte r = Convert.ToByte(s.Substring(0, 2), 16);
                    byte g = Convert.ToByte(s.Substring(2, 2), 16);
                    byte b = Convert.ToByte(s.Substring(4, 2), 16);
                    return Color.FromArgb(255, r, g, b);
                }
                if (s.Length == 8)
                {
                    byte a = Convert.ToByte(s.Substring(0, 2), 16);
                    byte r = Convert.ToByte(s.Substring(2, 2), 16);
                    byte g = Convert.ToByte(s.Substring(4, 2), 16);
                    byte b = Convert.ToByte(s.Substring(6, 2), 16);
                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch { }
            return fallback;
        }

        // ============================== 事件 ==============================

        /// <summary>Window 真的关闭后：位置保存 + 通知主窗口走 OnDesktopLyricsClosedByUser 路径。</summary>
        private void DesktopLyricsOverlay_Closed(object sender, WindowEventArgs args)
        {
            if (_disposed)
            {
                return;
            }
            // 程序主动 Close 的路径已经在主窗口那清理过了，这里只对"用户按 X"做通知
            if (_isClosingFromSelf)
            {
                return;
            }
            try { PersistPositionToSettings(); } catch { }
            try { ClosedByUser?.Invoke(); } catch { }
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (IsLocked)
            {
                return;
            }
            var p = e.GetCurrentPoint(RootGrid);
            if (!p.Properties.IsLeftButtonPressed)
            {
                return;
            }
            _dragging = true;
            try { RootGrid.CapturePointer(e.Pointer); } catch { }
            // 起点用 DIP 计算窗口内偏移；窗口位置用物理像素
            uint dpi = GetDpiForWindow(_hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            _dragStartDipPoint = new Point(p.Position.X * scale, p.Position.Y * scale);
            _dragStartWindowPos = AppWindow.Position;
            e.Handled = true;
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }
            try
            {
                var p = e.GetCurrentPoint(RootGrid);
                uint dpi = GetDpiForWindow(_hwnd);
                double scale = dpi > 0 ? dpi / 96.0 : 1.0;
                double dxPx = p.Position.X * scale - _dragStartDipPoint.X;
                double dyPx = p.Position.Y * scale - _dragStartDipPoint.Y;
                int newX = _dragStartWindowPos.X + (int)Math.Round(dxPx);
                int newY = _dragStartWindowPos.Y + (int)Math.Round(dyPx);
                AppWindow.Move(new PointInt32(newX, newY));
            }
            catch (Exception caught) { StartupLog.WriteException("DesktopLyricsOverlay.RootGrid_PointerMoved", caught); }
        }

        private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }
            _dragging = false;
            try { RootGrid.ReleasePointerCapture(e.Pointer); } catch { }
            PersistPositionToSettings();
        }

        private void RootGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                PersistPositionToSettings();
            }
        }

        private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            SetLocked(!IsLocked);
            e.Handled = true;
        }

        private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (IsLocked)
            {
                SetLocked(false);
                e.Handled = true;
            }
        }

        private void LockToggleButton_Click(object sender, RoutedEventArgs e)
        {
            SetLocked(!IsLocked);
        }

        private void LockedUnlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsLocked)
            {
                SetLocked(false);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                try { PersistPositionToSettings(); } catch { }
                // 不走 Window.Closed → _isClosingFromSelf 不置位，让通知路径走主窗口预期
                ClosedByUser?.Invoke();
                IsVisible = false;
            }, "DesktopLyricsOverlay.CloseButton_Click");
        }

        // ============================== P/Invoke ==============================

        /// <summary>取窗口样式。GWL_EXSTYLE=int 值，64 位进程用 SetWindowLongPtr 才稳（SetWindowLong 在 32 位索引内能用，但 SetWindowLongPtr 是显式 64 位 API）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private static void SetExStyle(IntPtr hWnd, int nIndex, int newExStyle)
        {
            // 64 位优先，32 位 fallback
            IntPtr result = SetWindowLongPtr64(hWnd, nIndex, (IntPtr)newExStyle);
            if (result == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
            {
                SetWindowLong32(hWnd, nIndex, newExStyle);
            }
        }

        private static int GetExStyle(IntPtr hWnd, int nIndex)
        {
            return GetWindowLong(hWnd, nIndex);
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // ============================== 错误兜底 ==============================

        private static void Safe(Action a, string where)
        {
            try { a(); }
            catch (Exception caught) { StartupLog.WriteException(where, caught); }
        }
    }
}
