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
    /// 桌面歌词窗口（**主流风格**：网易云 / QQ 音乐 / Spotify 桌面歌词观感）。
    ///
    /// 设计思路直接照搬 MusicPlayer2（CLyricsWindow + CDesktopLyric）：
    ///   - 双行：当前行（大字号 + 卡拉 OK 渐变）+ 下一行（小字号提示）
    ///   - 卡拉 OK：当前行两个完全相同的 TextBlock 重叠，下层白半透，上层主题色；
    ///             上层用 RectangleGeometry 裁剪矩形，width = splitX = textW × progress。
    ///   - 当前行横向滚动（MusicPlayer2 精髓）：当文本宽度超过可视区宽度时，
    ///             根据卡拉 OK 进度点位置算法：
    ///               A. 未唱宽度 < 容器宽一半 → 右对齐（让文本右侧贴近容器右侧）
    ///               B. 已唱宽度 > 容器宽一半 → 让分割点保持容器中点
    ///               C. 已唱宽度 ≤ 容器宽一半 → 左对齐
    ///             文本不溢出时居中、不滚动。
    ///   - 顶部工具栏横排：双行切换 / 卡拉 OK 切换 / 锁定 / 关闭
    ///   - 双击歌词切换锁定、右键歌词在锁定时解锁
    ///   - 鼠标拖动窗体（系统拖动语义）
    ///
    /// WinUI3 落地简化（避免 GDI+）：
    ///   - WinUI3 Window 默认背景纯黑，所以用 DesktopAcrylic backdrop 铺整窗 = 透明感
    ///   - 卡片用 #B3000000 半透深色 + 圆角 14 + 细白边
    ///   - 横向滚动用 Grid.RenderTransform = TranslateTransform 控制 X
    ///   - 卡拉 OK 裁剪用 CurrentLineSungText.Clip = RectangleGeometry
    ///
    /// 公开契约完全保留（13 个成员签名不变）：
    ///   event ClosedByUser / PositionProvider / IsLocked / IsVisible / CurrentPosition /
    ///   SetSavedPosition / Show / Close / SetLyrics / Sync / SetLocked / ApplySettings /
    ///   SetPlaybackPaused / Dispose —— 主窗口 0 改动。
    /// </summary>
    internal sealed partial class DesktopLyricsOverlay : IDisposable
    {
        // ===== Win32 样式常量 =====
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW  = 0x00000080;

        // ===== 默认尺寸（DIP） =====
        private const int DefaultWidthDip   = 720;
        private const int DefaultHeightDip  = 130;
        private const int ClipWidthDip      = 660;   // 当前行可视区宽，对应 XAML Width=660

        // ===== 状态 =====
        private IntPtr _hwnd;
        private int _savedExStyle;
        private bool _exStyleSaved;
        private bool _disposed;
        private bool _isClosingFromSelf;   // 防 ClosedByUser 订阅方反向触发 Close 递归

        private readonly List<LyricLine> _lines = new();
        private int _currentIndex = -1;
        private TimeSpan _lastSyncPosition;
        private bool _hasSyncedOnce;

        // 卡拉 OK
        private RectangleGeometry? _progressClip;
        private double _lastProgress01;

        // 拖动
        private bool _dragging;
        private Point _dragStartDipPoint;
        private PointInt32 _dragStartWindowPos;

        // 设置缓存
        private double _fontSize = 32;
        private int _opacityPercent = 100;
        private Color _playedColor   = Color.FromArgb(255, 79, 168, 245);   // #4FA8F5 默认主题色蓝
        private Color _unplayedColor = Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF); // #B3FFFFFF 白色 70% 半透
        private bool _doubleLine = true;
        private bool _karaokeEnabled = true;
        private bool _clickThroughSetting;
        private bool _hideWhenPaused;
        private bool _hideWithoutLyric;
        private bool _paused;

        // ============================== 公开契约（13 个） ==============================

        /// <summary>窗被用户主动关闭（按 X / 主窗口开关触发），通知主窗口做清理。</summary>
        public event Action? ClosedByUser;

        /// <summary>主窗口可注入的当前位置 provider（备用，本版本未使用，主窗口直接 Sync）。</summary>
        public Func<TimeSpan>? PositionProvider { get; set; }

        /// <summary>当前是否被锁定。锁定 = 整窗鼠标穿透，露出单"开锁"按钮。</summary>
        public bool IsLocked { get; private set; }

        /// <summary>当前是否已 Show。WinUI3 Window 没显式可见属性，自己 track。</summary>
        public bool IsVisible { get; private set; }

        /// <summary>当前窗位置（物理像素）。主窗口用它做"关闭前保存位置"。</summary>
        public (int X, int Y) CurrentPosition
        {
            get
            {
                try
                {
                    EnsureHwnd();
                    return AppWindow != null ? (AppWindow.Position.X, AppWindow.Position.Y) : (0, 0);
                }
                catch { return (0, 0); }
            }
        }

        public DesktopLyricsOverlay()
        {
            InitializeComponent();

            EnsureHwnd();

            // Window.Closed 事件统一处理"用户按 X / 程序 Close() / 进程结束"
            this.Closed += DesktopLyricsOverlay_Closed;

            // 整窗铺 Desktop Acrylic backdrop（替代纯透明窗 = 黑底的旧 bug），Mica fallback
            TryApplySystemBackdrop();

            // 没任务栏图标 + 不在 Alt+Tab
            int exStyle = GetExStyle(_hwnd, GWL_EXSTYLE);
            _savedExStyle = exStyle;
            exStyle |= WS_EX_TOOLWINDOW;
            SetExStyle(_hwnd, GWL_EXSTYLE, exStyle);
            _exStyleSaved = true;

            // 砍掉系统标题栏（WinUI3 风格），让 Top 48 DIP 处直接画自定义卡
            ExtendsContentIntoTitleBar = true;
            try
            {
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
                AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            }
            catch { }

            // 起始大小
            try { AppWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWidthDip, DefaultHeightDip)); }
            catch { }

            // 默认位置：屏幕顶部居中、48 DIP 上间距
            CenterOnPrimaryDisplay();

            UpdateLockedUi();
            UpdateKaraokeUi();
            UpdateLineUi();
            UpdateDimmedOpacity();

            // Loaded 后再初始化卡拉 OK Clip —— 首次 Layout 时 ActualWidth 才有值
            if (Content is FrameworkElement fe)
            {
                fe.Loaded += DesktopLyricsOverlay_Loaded;
            }
        }

        /// <summary>主窗口在 EnsureDesktopLyricsWindow 后注入上次保存的位置。</summary>
        public void SetSavedPosition(int x, int y)
        {
            Safe(() =>
            {
                EnsureHwnd();
                if (AppWindow != null)
                {
                    AppWindow.Move(new PointInt32(x, y));
                }
            });
        }

        /// <summary>显示窗。仅切换可见性，不重置任何状态。</summary>
        public void Show()
        {
            if (_disposed) return;
            Safe(() =>
            {
                EnsureHwnd();
                AppWindow.Show();
                IsVisible = true;
                ApplyPausedVisibility();
            });
        }

        /// <summary>关闭窗。用 ((Window)this).Close() 跟本类 Close 区分。</summary>
        public void Close()
        {
            if (_disposed) return;
            _isClosingFromSelf = true;
            try { ((Window)this).Close(); }
            catch (Exception caught) { StartupLog.WriteException("DesktopLyricsOverlay.Close", caught); }
            IsVisible = false;
        }

        /// <summary>主窗口每帧/30fps 调：定位当前行并刷卡拉 OK 进度。</summary>
        public void Sync(TimeSpan position)
        {
            if (_disposed) return;
            _lastSyncPosition = position;
            _hasSyncedOnce = true;
            UpdateCurrentLine();
            UpdateKaraokeProgress();
        }

        /// <summary>加载新歌词或切换歌曲时调。空数组也行。</summary>
        public void SetLyrics(IReadOnlyList<LyricLine> lines)
        {
            _lines.Clear();
            if (lines != null) _lines.AddRange(lines);
            _currentIndex = -1;
            UpdateLineUi();
        }

        /// <summary>锁定 / 解锁切换。锁定 = 整窗 WS_EX_TRANSPARENT。</summary>
        public void SetLocked(bool locked)
        {
            if (IsLocked == locked) return;
            IsLocked = locked;
            ApplyClickThrough();
            UpdateLockedUi();
        }

        /// <summary>主窗口切主题 / 改设置时调：刷新字号、不透明度、双行、颜色、穿透、暂停时是否隐藏。</summary>
        public void ApplySettings(AppSettingsState settings)
        {
            _fontSize          = Math.Clamp(settings.DesktopLyricFontSize, 16, 64);
            _opacityPercent    = Math.Clamp(settings.DesktopLyricOpacity, 20, 100);
            _playedColor       = ParseHexColor(settings.DesktopLyricPlayedColor, _playedColor);
            _unplayedColor     = ParseHexColor(settings.DesktopLyricUnplayedColor, _unplayedColor);
            _doubleLine        = settings.DesktopLyricDoubleLine;
            _clickThroughSetting = settings.DesktopLyricClickThrough;
            _hideWhenPaused    = settings.DesktopLyricHideWhenPaused;
            _hideWithoutLyric  = settings.DesktopLyricHideWithoutLyric;

            ApplyClickThrough();
            UpdateLineUi();
            UpdateDimmedOpacity();
        }

        /// <summary>主窗口调：暂停时按 hide-when-paused 设置决定藏起来。</summary>
        public void SetPlaybackPaused(bool paused)
        {
            _paused = paused;
            ApplyPausedVisibility();
        }

        /// <summary>主窗口调：释放窗与所有 hook。位置保存由 Window.Closed 兜底，这里补一刀。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Safe(() =>
            {
                try { PersistPositionToSettings(); } catch { }
                try { _isClosingFromSelf = true; ((Window)this).Close(); } catch { }
                IsVisible = false;
            });
        }

        // ============================== 内部实现 ==============================

        private void DesktopLyricsOverlay_Loaded(object sender, RoutedEventArgs e)
        {
            // 首次 Layout 完成 → 创建 Clip 对象 + 按当前进度算一次
            EnsureProgressClip();
            ApplyProgress(_lastProgress01);
        }

        private void EnsureHwnd()
        {
            if (_hwnd != IntPtr.Zero) return;
            try { _hwnd = WindowNative.GetWindowHandle(this); }
            catch (Exception caught) { StartupLog.WriteException("DesktopLyricsOverlay.EnsureHwnd", caught); }
        }

        private void CenterOnPrimaryDisplay()
        {
            Safe(() =>
            {
                if (AppWindow == null) return;
                var area = DisplayArea.Primary;
                var work = area.WorkArea;
                int x = work.X + Math.Max(0, (work.Width  - AppWindow.Size.Width)  / 2);
                int y = work.Y + 60; // 顶部 60 DIP 处
                AppWindow.Move(new PointInt32(x, y));
            });
        }

        private void ApplyClickThrough()
        {
            // 整窗命中：用户设置勾上 → 整窗 WS_EX_TRANSPARENT；锁定态也穿透，以便工具栏收起后桌面干净
            if (_hwnd == IntPtr.Zero || !_exStyleSaved) return;
            try
            {
                int exStyle = GetExStyle(_hwnd, GWL_EXSTYLE);
                if (_clickThroughSetting || IsLocked)
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
            // 锁定：工具栏收起 + 露单按钮
            Toolbar.Visibility            = IsLocked ? Visibility.Collapsed : Visibility.Visible;
            LockedUnlockButton.Visibility = IsLocked ? Visibility.Visible    : Visibility.Collapsed;
            LockToggleIcon.Glyph          = IsLocked ? "\uE72E"             : "\uE785"; // E72E=解锁 / E785=锁定

            UpdateDimmedOpacity();
        }

        private void UpdateKaraokeUi()
        {
            // 卡拉 OK 关闭时：sungText 的 Clip 强制全宽（让已唱=全宽）；下次恢复卡拉 OK 时切回
            // 同时把按钮图标颜色降一档表示关闭
            if (_karaokeEnabled)
            {
                KaraokeToggleIcon.Foreground = new SolidColorBrush(_playedColor);
                KaraokeToggleIcon.Opacity    = 1.0;
            }
            else
            {
                KaraokeToggleIcon.Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xCC, 0xCC, 0xCC));
                KaraokeToggleIcon.Opacity    = 0.6;
            }
        }

        private void ToggleKaraoke()
        {
            _karaokeEnabled = !_karaokeEnabled;
            UpdateKaraokeUi();
            // 立刻刷一次：把 Clip 推到 0 或推满
            _lastProgress01 = 0.0;
            ApplyProgress(0.0);
        }

        /// <summary>整窗铺 Desktop Acrylic backdrop（替代纯透明窗 = 黑底的旧 bug）。</summary>
        private void TryApplySystemBackdrop()
        {
            Safe(() =>
            {
                try { SystemBackdrop = new DesktopAcrylicBackdrop(); return; }
                catch (Exception caught1) { StartupLog.WriteException("DesktopLyricsOverlay.SystemBackdrop(Acrylic)", caught1); }
                try { SystemBackdrop = new MicaBackdrop(); }
                catch (Exception caught2) { StartupLog.WriteException("DesktopLyricsOverlay.SystemBackdrop(Mica)", caught2); }
            });
        }

        /// <summary>设置切换 / Loaded 时调：刷字号、颜色、双行/单行、当前行内容、初始卡拉 OK 进度。</summary>
        private void UpdateLineUi()
        {
            if (CurrentLineUnsungText == null) return;

            // 字号
            CurrentLineUnsungText.FontSize = _fontSize;
            CurrentLineSungText.FontSize   = _fontSize;
            if (NextLineText != null) NextLineText.FontSize = Math.Max(14, _fontSize * 0.5);

            // 颜色
            CurrentLineSungText.Foreground   = new SolidColorBrush(_playedColor);
            CurrentLineUnsungText.Foreground = new SolidColorBrush(_unplayedColor);
            if (NextLineText != null)
            {
                NextLineText.Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
                NextLineText.Visibility = _doubleLine ? Visibility.Visible : Visibility.Collapsed;
            }

            ApplyLineContent();
        }

        /// <summary>填当前行 + 下一行文本；内容变了重置 Clip + 平移。</summary>
        private void ApplyLineContent()
        {
            if (CurrentLineUnsungText == null) return;

            string curText;
            if (_currentIndex >= 0 && _currentIndex < _lines.Count)
            {
                LyricLine cur = _lines[_currentIndex];
                curText = string.IsNullOrWhiteSpace(cur.Text) ? "·" : cur.Text;
            }
            else
            {
                curText = _lines.Count == 0 ? "♪  暂无歌词" : "";
            }
            CurrentLineUnsungText.Text = curText;
            CurrentLineSungText.Text   = curText;

            if (NextLineText != null)
            {
                int nextIdx = FindNextDisplayLine(_currentIndex);
                string nextText = (nextIdx >= 0 && nextIdx < _lines.Count) ? _lines[nextIdx].Text : "";
                NextLineText.Text = nextText;
            }

            // 切歌/换句：先按 0 进度设 Clip + 平移；下一次 Sync 用真实 progress 修正
            _lastProgress01 = 0.0;
            CurrentLineTranslate.X = 0.0;
            ApplyProgress(0.0);
        }

        private void UpdateCurrentLine()
        {
            if (_lines.Count == 0)
            {
                if (_currentIndex != -1)
                {
                    _currentIndex = -1;
                    ApplyLineContent();
                }
                return;
            }

            // 二分定位最后一行 Time ≤ position
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

            // 翻译行：跳回上一行原文
            if (idx >= 0 && _lines[idx].IsTranslation) idx -= 1;
            if (idx < -1 || idx >= _lines.Count) idx = -1;

            if (idx != _currentIndex)
            {
                _currentIndex = idx;
                ApplyLineContent();
            }
        }

        /// <summary>在 anchor 之后找最近的非翻译行作为下一行提示。</summary>
        private int FindNextDisplayLine(int anchor)
        {
            for (int i = anchor + 1; i < _lines.Count; i++)
            {
                if (!_lines[i].IsTranslation) return i;
            }
            return -1;
        }

        /// <summary>根据当前播放位置计算卡拉 OK 进度 0..1，并设 Clip Rect + TranslateTransform.X。</summary>
        private void UpdateKaraokeProgress()
        {
            double p;
            if (_lines.Count == 0 || _currentIndex < 0 || _currentIndex >= _lines.Count)
            {
                p = 0.0;
            }
            else
            {
                TimeSpan lineStart = _lines[_currentIndex].Time;
                TimeSpan lineEnd;
                if (_currentIndex + 1 < _lines.Count) lineEnd = _lines[_currentIndex + 1].Time;
                else lineEnd = lineStart + TimeSpan.FromSeconds(8);

                double lineDur = (lineEnd - lineStart).TotalSeconds;
                double elapsed = (_lastSyncPosition - lineStart).TotalSeconds;
                p = lineDur > 0.001 ? Math.Clamp(elapsed / lineDur, 0.0, 1.0) : 1.0;
            }

            _lastProgress01 = p;
            ApplyProgress(p);
        }

        /// <summary>
        /// 把 progress 应用到当前行可视区：
        ///   1. 设 CurrentLineSungText.Clip = (0, 0, splitX, h)，splitX = textW × progress
        ///   2. 设 CurrentLineTranslate.X = baseOffset + visibleOffset，让横滚结果等价于 MusicPlayer2 算法：
        ///        baseOffset = (clipW − textW) / 2 —— 短歌词时让 Host 居中，长歌词时为负（把 Host 拉到 cell 左外）
        ///        visibleOffset：仅 textW > clipW 时按 MusicPlayer2 思路滚动 —— 让分割点尽量保持容器中点
        ///          · 未唱宽度 < 容器宽一半 → 右对齐（target = clipW − textW，progressOffset = baseOffset）
        ///          · 已唱宽度 > 容器宽一半 → 分割点保持中点（target = clipW/2 − sungW，progressOffset = target − baseOffset）
        ///          · 否则 → 左对齐（target = 0，progressOffset = −baseOffset）
        ///      卡拉 OK 关闭时直接 sungText Clip 满宽 + translateX 归零
        /// </summary>
        private void ApplyProgress(double progress01)
        {
            if (CurrentLineUnsungText == null) return;

            double textW = CurrentLineUnsungText.ActualWidth;
            double h     = Math.Max(1, CurrentLineUnsungText.ActualHeight);
            double clipContainerW = GetClipContainerWidth();

            EnsureProgressClip();

            // 卡拉 OK 关闭：已唱=全宽 + 不滚动
            if (!_karaokeEnabled)
            {
                if (_progressClip != null)
                {
                    _progressClip.Rect = new Rect(0, 0, Math.Max(0, textW), h);
                }
                CurrentLineTranslate.X = 0.0;
                return;
            }

            // 没排好：等 Layout 完成 CurrentLineClip_SizeChanged / CurrentLineHost_SizeChanged 再调
            if (textW <= 0 || clipContainerW <= 0)
            {
                if (_progressClip != null)
                {
                    _progressClip.Rect = new Rect(0, 0, 0, h);
                }
                CurrentLineTranslate.X = 0.0;
                return;
            }

            double p     = Math.Clamp(progress01, 0.0, 1.0);
            double splitX = textW * p;

            // Clip —— 已唱部分 = 0..splitX
            if (_progressClip != null)
            {
                _progressClip.Rect = new Rect(0, 0, splitX, h);
            }

            // TranslateX = baseOffset + progressOffset（baseOffset 就是 textW≤clipContainerW 时的居中偏移）
            double baseOffset = (clipContainerW - textW) / 2.0;
            double progressOffset = 0.0;

            if (textW > clipContainerW)
            {
                double sungW   = splitX;
                double unsungW = textW - splitX;
                double targetX;
                if (unsungW < clipContainerW / 2.0)
                {
                    // A：右对齐 —— 让 Host 右边缘贴近可视区右侧
                    targetX = clipContainerW - textW;
                }
                else if (sungW > clipContainerW / 2.0)
                {
                    // B：分割点保持容器中点
                    targetX = clipContainerW / 2.0 - sungW;
                }
                else
                {
                    // C：左对齐 —— 让 Host 左边缘贴近可视区左侧
                    targetX = 0.0;
                }
                progressOffset = targetX - baseOffset;
            }

            CurrentLineTranslate.X = baseOffset + progressOffset;
        }

        private double GetClipContainerWidth()
        {
            return CurrentLineClip != null ? CurrentLineClip.ActualWidth : ClipWidthDip;
        }

        /// <summary>当前行文本尺寸变了（切歌 / 字号变了）→ 用同一进度按新尺寸重算 Clip + 平移。</summary>
        private void CurrentLineHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyProgress(_lastProgress01);
        }

        /// <summary>可视区尺寸变了（窗口大小变了 / 字号变了）→ 同样重算。</summary>
        private void CurrentLineClip_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 同步可视区 Clip 矩形（跟实际尺寸保持一致，DPI/缩放变化时跟随）
            if (CurrentLineClipGeometry != null && CurrentLineClip != null)
            {
                CurrentLineClipGeometry.Rect = new Rect(0, 0, CurrentLineClip.ActualWidth, CurrentLineClip.ActualHeight);
            }
            ApplyProgress(_lastProgress01);
        }

        /// <summary>确保 RectangleGeometry 已创建并绑到 CurrentLineSungText.Clip。重复调用是空操作。</summary>
        private void EnsureProgressClip()
        {
            if (_progressClip != null) return;
            if (CurrentLineSungText == null) return;
            _progressClip = new RectangleGeometry { Rect = new Rect(0, 0, 0, 40) };
            CurrentLineSungText.Clip = _progressClip;
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
                RootGrid.Opacity = IsLocked ? 0.5 : Math.Clamp(_opacityPercent / 100.0, 0.2, 1.0);
            }
        }

        private void PersistPositionToSettings()
        {
            if (_hwnd == IntPtr.Zero || AppWindow == null) return;
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
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
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

        private void DesktopLyricsOverlay_Closed(object sender, WindowEventArgs args)
        {
            if (_disposed) return;
            if (_isClosingFromSelf) return;
            try { PersistPositionToSettings(); } catch { }
            try { ClosedByUser?.Invoke(); } catch { }
        }

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (IsLocked) return;
            var p = e.GetCurrentPoint(RootGrid);
            if (!p.Properties.IsLeftButtonPressed) return;
            _dragging = true;
            try { RootGrid.CapturePointer(e.Pointer); } catch { }
            uint dpi = GetDpiForWindow(_hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            _dragStartDipPoint = new Point(p.Position.X * scale, p.Position.Y * scale);
            _dragStartWindowPos = AppWindow.Position;
            e.Handled = true;
        }

        private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
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
            if (!_dragging) return;
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

        private void DoubleLineToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _doubleLine = !_doubleLine;
            UpdateLineUi();
        }

        private void KaraokeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleKaraoke();
        }

        private void LockToggleButton_Click(object sender, RoutedEventArgs e)
        {
            SetLocked(!IsLocked);
        }

        private void LockedUnlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsLocked) SetLocked(false);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Safe(() =>
            {
                try { PersistPositionToSettings(); } catch { }
                ClosedByUser?.Invoke();
                IsVisible = false;
            });
        }

        // ============================== P/Invoke ==============================

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        private static void SetExStyle(IntPtr hWnd, int nIndex, int newExStyle)
        {
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

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception caught) { StartupLog.WriteException("DesktopLyricsOverlay", caught); }
        }
    }
}
