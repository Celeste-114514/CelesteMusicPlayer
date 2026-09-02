using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 桌面歌词：仿 MusicPlayer2，用 WS_EX_LAYERED + UpdateLayeredWindow 实现逐像素透明。
    /// </summary>
    internal sealed class DesktopLyricsOverlay : IDisposable
    {
        private const string WindowClassName = "CelesteDesktopLyricsOverlay";
        private const int ToolbarHeight = 56;
        private const int BtnSize = 44;
        private const int BtnGap = 8;
        private const int TimerId = 1;
        // 20ms（约 50fps）。原先 50ms 是为了压住每帧 GetHbitmap 的开销，现在后台位图是持久 DIB，
        // 每帧只重画不重建，可以把刷新率提上去 —— 卡拉 OK 填充的推进才不会一顿一顿。
        private const uint TimerIntervalMs = 20;
        private const long MinRedrawIntervalMs = 16; // 节流下限，略低于定时器间隔，避免定时器抖动导致整帧被丢

        // 进度平滑的时间常数（毫秒）：越大越"黏"、越小越"紧跟"。
        // 用时间常数而不是固定 lerp 系数，帧率变化（20ms/50ms/掉帧）时手感才一致。
        private const double ProgressSmoothTauMs = 45.0;

        // 上次 Redraw 的物理时刻（Environment.TickCount64），用于跨 tick 节流
        private long _lastRedrawMs;

        private static bool _classRegistered;
        private static readonly object ClassGate = new();

        private readonly List<LyricLine> _lines = new();
        private readonly List<LyricLine> _sourceLines = new(); // 原始歌词（含译文），显示时按 _hideTranslation 过滤
        private IntPtr _hwnd;
        private IntPtr _wndProcPtr;
        private WndProcDelegate? _wndProcKeepAlive;

        // 持久后台位图（真正的像素缓冲，非拷贝）：创建一次、尺寸变化时重建，每帧只重画不重建
        private IntPtr _memDc;     // 后台 DC，已选入 _hBitmap
        private IntPtr _hBitmap;   // CreateDIBSection 32 位 ARGB 位图
        private IntPtr _oldBitmap; // _memDc 首次选入时保存的默认位图，Dispose 时还原
        private int _backW;        // 当前后台位图尺寸（用于检测是否需要重建）
        private int _backH;
        private int _width = 800;
        private int _height = 160;
        private int _x;
        private int _y;
        private float _fontSize = 28f;
        private Color _playedColor = Color.FromArgb(255, 64, 180, 255);
        private Color _unplayedColor = Color.FromArgb(255, 245, 245, 245);
        private int _currentIndex = -1;
        private TimeSpan _position;
        private bool _hover;
        private bool _mouseInWindow;
        private bool _locked;
        private bool _unlockHot;
        private bool _dragging;
        private int _dragOffsetX;
        private int _dragOffsetY;
        private bool _disposed;
        private bool _placedOnce;
        private Rectangle[] _btnRects = Array.Empty<Rectangle>();
        private int _pressedBtn = -1;
        private double _displayProgress; // 平滑后的卡拉 OK 进度 0..1
        private long _lastProgressUpdateMs; // 上一次推进平滑的时刻，用于计算真实 dt
        private bool _karaokeStyle = true;

        // 逐字歌词的字宽缓存：避免每帧为每个字重新走一遍 GraphicsPath 测量
        private int _charWidthsIndex = -1;
        private string? _charWidthsText;
        private float _fontSizeAtMeasure;
        private float[]? _charWidths;
        private float _charWidthsTotal;

        // —— 外观设置（来自 AppSettings，ApplySettings 注入）——
        private string _fontFamilyName = "Microsoft YaHei UI";
        private float _outlineWidth;                 // 描边宽度，0 = 不描边
        private Color _outlineColor = Color.FromArgb(255, 0, 0, 0);
        private int _shadowStrength = 2;             // 0=关 1=弱 2=中 3=强
        private string _colorPreset = "Custom";
        private bool _hideTranslation;              // 隐藏译文行
        private string _align = "Center";           // Left / Center / Right
        private int _visibleLines = 3;              // 1 / 2 / 3
        private float _lineSpacing;                 // 额外行距（像素）
        private bool _showBackgroundBar;            // 歌词后加半透明底色条
        private Color _backgroundColor = Color.FromArgb(0x66, 0, 0, 0);

        private enum ToolbarBtn
        {
            FontMinus = 0,
            FontPlus = 1,
            Settings = 2,
            Lock = 3,
            Close = 4,
            Count = 5
        }

        public event Action? ClosedByUser;

        /// <summary>由主窗口注入，用于高频刷新播放进度。</summary>
        public Func<TimeSpan>? PositionProvider { get; set; }

        public bool IsLocked => _locked;

        public bool IsVisible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

        /// <summary>当前窗口位置（物理像素），用于位置记忆。</summary>
        public (int X, int Y) CurrentPosition => (_x, _y);

        /// <summary>恢复记忆的窗口位置（在 EnsureWindow 创建窗口之前调用）。</summary>
        public void SetSavedPosition(int x, int y)
        {
            _x = x;
            _y = y;
            _placedOnce = true;
        }

        public void Show()
        {
            EnsureWindow();
            ShowWindow(_hwnd, SwShowNoActivate);
            SetWindowPos(_hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            Redraw();
        }

        public void Close()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            // 勿先清空 _hwnd，否则 WM_NCDESTROY 里发不出 ClosedByUser（主界面 on/off 不同步）
            DestroyWindow(_hwnd);
        }

        public void SetLyrics(IReadOnlyList<LyricLine> lines)
        {
            _sourceLines.Clear();
            _sourceLines.AddRange(lines);
            _currentIndex = -1;
            _displayProgress = 0;
            RebuildVisibleLines();
            ResizeForContent();
            UpdateVisibilityForState();
            Redraw();
        }

        /// <summary>按 _hideTranslation 过滤 _sourceLines（含译文）得到实际显示的 _lines。</summary>
        private void RebuildVisibleLines()
        {
            _lines.Clear();
            foreach (var l in _sourceLines)
            {
                if (_hideTranslation && l.IsTranslation)
                {
                    continue;
                }

                _lines.Add(l);
            }

            // 过滤后索引可能越界，下一帧 ApplyPosition 会重算；这里只保证不崩
            if (_currentIndex >= _lines.Count)
            {
                _currentIndex = _lines.Count - 1;
            }

            if (_currentIndex < -1)
            {
                _currentIndex = -1;
            }

            // 行集合变了，逐字宽度缓存作废
            _charWidths = null;
        }

        public void Sync(TimeSpan position)
            => ApplyPosition(position, forceRedraw: true);

        private void ApplyPosition(TimeSpan position, bool forceRedraw)
        {
            _position = position;
            if (_lines.Count == 0)
            {
                if (forceRedraw)
                {
                    Redraw();
                }

                return;
            }

            int index = 0;
            for (int i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].Time <= position)
                {
                    index = i;
                }
                else
                {
                    break;
                }
            }

            bool indexChanged = index != _currentIndex;
            if (indexChanged)
            {
                _currentIndex = index;
                _displayProgress = 0;
                _lastProgressUpdateMs = Environment.TickCount64;
                ResizeForContent();
            }

            double target = ComputeLineProgress(position, index);

            // 按真实经过时间做指数平滑（帧率无关）：dt 越大追得越多，掉帧也不会甩尾。
            // 换行时直接就位，避免上一行的残留进度带过来。
            long nowMs = Environment.TickCount64;
            double dtMs = Math.Clamp((double)(nowMs - _lastProgressUpdateMs), 1.0, 200.0);
            _lastProgressUpdateMs = nowMs;
            double alpha = indexChanged ? 1.0 : 1.0 - Math.Exp(-dtMs / ProgressSmoothTauMs);
            _displayProgress += (target - _displayProgress) * alpha;
            if (Math.Abs(target - _displayProgress) < 0.0015)
            {
                _displayProgress = target;
            }

            // 节流：progress 触发的 Redraw 至少 MinRedrawIntervalMs 一次。
            // forceRedraw（鼠标进出窗口、点击按钮）保持立即响应，不受节流限制。
            // 死区放到 0.0015：原来 0.008 会让慢歌的高亮边缘好几十毫秒才动一次，看着就是一顿一顿。
            bool progressChanged = indexChanged
                || Math.Abs(target - _displayProgress) >= 0.0015
                || (target > 0.995 && _displayProgress < 0.995);
            if (forceRedraw)
            {
                Redraw();
            }
            else if (progressChanged && CanRedrawNow())
            {
                Redraw();
            }
        }

        private bool CanRedrawNow()
        {
            long now = Environment.TickCount64;
            if (now - _lastRedrawMs >= MinRedrawIntervalMs)
            {
                return true;
            }
            return false;
        }

        private double ComputeLineProgress(TimeSpan position, int index)
        {
            if (index < 0 || index >= _lines.Count)
            {
                return 0;
            }

            TimeSpan start = _lines[index].Time;
            TimeSpan end = index + 1 < _lines.Count
                ? _lines[index + 1].Time
                : start + TimeSpan.FromSeconds(4);
            double span = Math.Max(0.25, (end - start).TotalSeconds);
            return Math.Clamp((position - start).TotalSeconds / span, 0, 1);
        }

        public void SetLocked(bool locked)
        {
            if (_locked == locked)
            {
                return;
            }

            _locked = locked;
            _unlockHot = false;
            if (_locked)
            {
                _dragging = false;
            }
            else
            {
                _hover = _mouseInWindow;
            }

            ApplyExStyle();
            Redraw();
        }

        /// <summary>应用设置页中的桌面歌词外观与行为选项。</summary>
        public void ApplySettings(AppSettingsState settings)
        {
            if (settings == null)
            {
                return;
            }

            _fontSize = (float)Math.Clamp(settings.DesktopLyricFontSize, 14, 64);
            _fontFamilyName = string.IsNullOrWhiteSpace(settings.DesktopLyricFontFamily)
                ? "Microsoft YaHei UI"
                : settings.DesktopLyricFontFamily;
            _playedColor = ParseHexColor(settings.DesktopLyricPlayedColor, Color.FromArgb(255, 64, 180, 255));
            _unplayedColor = ParseHexColor(settings.DesktopLyricUnplayedColor, Color.FromArgb(255, 245, 245, 245));
            _outlineWidth = (float)Math.Clamp(settings.DesktopLyricOutlineWidth, 0, 4);
            _outlineColor = ParseHexColor(settings.DesktopLyricOutlineColor, Color.FromArgb(255, 0, 0, 0));
            _shadowStrength = Math.Clamp(settings.DesktopLyricShadowStrength, 0, 3);
            _colorPreset = string.IsNullOrWhiteSpace(settings.DesktopLyricColorPreset) ? "Custom" : settings.DesktopLyricColorPreset;
            _opacityPercent = Math.Clamp(settings.DesktopLyricOpacity, 20, 100);
            _hideWithoutLyric = settings.DesktopLyricHideWithoutLyric;
            _hideWhenPaused = settings.DesktopLyricHideWhenPaused;
            _showUnlockWhenLocked = settings.DesktopLyricShowUnlockWhenLocked;
            _visibleLines = Math.Clamp(settings.DesktopLyricVisibleLines, 1, 3);
            _hideTranslation = settings.DesktopLyricHideTranslation;
            _align = settings.DesktopLyricAlign is "Left" or "Right" or "Center" ? settings.DesktopLyricAlign : "Center";
            _lineSpacing = (float)Math.Clamp(settings.DesktopLyricLineSpacing, 0, 24);
            _showBackgroundBar = settings.DesktopLyricShowBackgroundBar;
            _backgroundColor = ParseHexColor(settings.DesktopLyricBackgroundColor, Color.FromArgb(0x66, 0, 0, 0));
            _clickThrough = settings.DesktopLyricClickThrough;
            _karaokeStyle = settings.LyricKaraokeStyle;

            if (settings.DesktopLyricLockOnStart && !_locked)
            {
                SetLocked(true);
            }

            RebuildVisibleLines();
            ApplyExStyle();
            UpdateVisibilityForState();
            Redraw();
        }

        private bool _hideWithoutLyric;
        private bool _hideWhenPaused;
        private bool _showUnlockWhenLocked = true;
        private bool _clickThrough;
        private int _opacityPercent = 100;
        private bool _paused;

        public void SetPlaybackPaused(bool paused)
        {
            _paused = paused;
            UpdateVisibilityForState();
        }

        private void UpdateVisibilityForState()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            bool hide = (_hideWithoutLyric && _lines.Count == 0) || (_hideWhenPaused && _paused);
            if (hide)
            {
                ShowWindow(_hwnd, SwHide);
            }
            else
            {
                ShowWindow(_hwnd, SwShowNoActivate);
            }
        }

        private static Color ParseHexColor(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return fallback;
            }

            hex = hex.Trim();
            if (hex.StartsWith('#'))
            {
                hex = hex[1..];
            }

            try
            {
                if (hex.Length == 6)
                {
                    int r = Convert.ToInt32(hex[..2], 16);
                    int g = Convert.ToInt32(hex[2..4], 16);
                    int b = Convert.ToInt32(hex[4..6], 16);
                    return Color.FromArgb(255, r, g, b);
                }

                if (hex.Length == 8)
                {
                    int a = Convert.ToInt32(hex[..2], 16);
                    int r = Convert.ToInt32(hex[2..4], 16);
                    int g = Convert.ToInt32(hex[4..6], 16);
                    int b = Convert.ToInt32(hex[6..8], 16);
                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DesktopLyricsOverlay.cs", caught); }

            return fallback;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CleanupBackbuffer();
            Close();
            GC.SuppressFinalize(this);
        }

        private void EnsureWindow()
        {
            if (_hwnd != IntPtr.Zero)
            {
                return;
            }

            RegisterClass();
            PlaceInitially();

            _wndProcKeepAlive = WndProc;
            _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive);

            _hwnd = CreateWindowExW(
                WsExToolWindow | WsExTopMost | WsExLayered | WsExNoActivate,
                WindowClassName,
                "CelesteDesktopLyrics",
                WsPopup,
                _x,
                _y,
                _width,
                _height,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandleW(null),
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建桌面歌词窗口。");
            }

            SetWindowProc(_hwnd, _wndProcPtr);
            ApplyExStyle();
            SetTimer(_hwnd, TimerId, TimerIntervalMs, IntPtr.Zero);
            Redraw();
        }

        private void PlaceInitially()
        {
            if (_placedOnce)
            {
                return;
            }

            GetWorkArea(out int left, out int top, out int right, out int bottom);
            _width = Math.Max(420, (right - left) * 2 / 3);
            _height = ComputeHeight();
            _x = left + ((right - left) - _width) / 2;
            _y = bottom - _height - 40;
            _placedOnce = true;
        }

        private int ComputeHeight()
        {
            float side = Math.Max(12f, _fontSize * 0.72f);
            float lineH = _fontSize * 1.45f;
            float gaps = _lineSpacing * (_visibleLines - 1);
            float total;
            if (_visibleLines <= 1)
            {
                total = lineH;
            }
            else if (_visibleLines == 2)
            {
                total = lineH + side * 1.4f + gaps;
            }
            else
            {
                total = side * 1.4f + lineH + side * 1.4f + gaps;
            }

            return (int)Math.Ceiling(ToolbarHeight + 16 + total + 16);
        }

        private void ResizeForContent()
        {
            string cur = GetLineText(_currentIndex >= 0 ? _currentIndex : 0);
            string prev = _visibleLines >= 3 ? GetLineText(_currentIndex > 0 ? _currentIndex - 1 : -1) : string.Empty;
            string next = _visibleLines >= 2 ? GetLineText(_currentIndex >= 0 && _currentIndex + 1 < _lines.Count ? _currentIndex + 1 : -1) : string.Empty;

            using FontFamily family = CreateFontFamily(_fontFamilyName);
            using var tmp = new Bitmap(1, 1);
            using Graphics g = Graphics.FromImage(tmp);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            float maxW = MeasurePathSize(g, cur, family, _fontSize, FontStyle.Bold).Width;
            if (!string.IsNullOrEmpty(prev))
            {
                maxW = Math.Max(maxW, MeasurePathSize(g, prev, family, Math.Max(12f, _fontSize * 0.72f), FontStyle.Regular).Width);
            }

            if (!string.IsNullOrEmpty(next))
            {
                maxW = Math.Max(maxW, MeasurePathSize(g, next, family, Math.Max(12f, _fontSize * 0.72f), FontStyle.Regular).Width);
            }

            GetWorkArea(out int left, out _, out int right, out _);
            int workW = right - left;
            int newW = (int)Math.Clamp(maxW + 48, Math.Min(420, workW), Math.Min(1600, workW));
            int newH = ComputeHeight();

            int cx = _x + _width / 2;
            int cy = _y + _height / 2;
            _width = newW;
            _height = newH;
            _x = cx - _width / 2;
            _y = cy - _height / 2;

            if (_hwnd != IntPtr.Zero)
            {
                SetWindowPos(_hwnd, HwndTopMost, _x, _y, _width, _height, SwpNoActivate);
                // 后台位图尺寸随窗口同步，避免下一帧 Redraw 前出现尺寸错配而闪一下
                EnsureBackbuffer();
            }
        }

        private string GetLineText(int index)
        {
            if (index < 0 || index >= _lines.Count)
            {
                return _lines.Count == 0 ? "暂无歌词" : string.Empty;
            }

            return _lines[index].Text ?? string.Empty;
        }

        private LyricLine? GetLine(int index)
        {
            return index >= 0 && index < _lines.Count ? _lines[index] : null;
        }

        private static Font CreateFont(float size, FontStyle style)
        {
            try
            {
                return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Pixel);
            }
        }

        private void ApplyExStyle()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            int style = GetWindowLong(_hwnd, GwlExStyle);
            style |= WsExToolWindow | WsExTopMost | WsExLayered | WsExNoActivate;
            // 锁定时默认穿透；指针在解锁按钮上时临时取消穿透（与 MusicPlayer2 一致）
            // 设置「歌词背景穿透」时始终穿透（除解锁热区）
            if ((_locked || _clickThrough) && !_unlockHot)
            {
                style |= WsExTransparent;
            }
            else
            {
                style &= ~WsExTransparent;
            }

            SetWindowLong(_hwnd, GwlExStyle, style);
        }

        private void Redraw()
        {
            if (_hwnd == IntPtr.Zero || _disposed)
            {
                return;
            }

            // 实际重绘前打点，让后续 progress 触发的 Redraw 走节流
            _lastRedrawMs = Environment.TickCount64;

            EnsureBackbuffer();

            // 直接画进持久后台位图（真正的 ARGB 像素缓冲），不再每帧 new Bitmap + GetHbitmap
            // —— 那套做法在 Release 下会闪：GetHbitmap 每帧复制+转换像素，20fps 节奏偶尔超帧预算丢帧。
            using (Graphics g = Graphics.FromHdc(_memDc))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                // MusicPlayer2：未锁定时画极淡底，保证透明像素也能接到鼠标
                if (!_locked)
                {
                    byte a = (byte)(_hover ? 70 : 1);
                    using var bg = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
                    g.FillRectangle(bg, 0, 0, _width, _height);
                }

                DrawLyrics(g);

                // 悬停显示完整工具条；锁定后鼠标进入窗口只显示解锁按钮（可按设置关闭）
                bool showToolbar = (!_locked && _hover)
                    || (_locked && _mouseInWindow && _showUnlockWhenLocked);
                if (showToolbar)
                {
                    DrawToolbar(g);
                }
                else if (_locked && _showUnlockWhenLocked)
                {
                    // 仍布局解锁按钮矩形，供定时器命中测试
                    LayoutLockOnlyButton(assignRects: true);
                }
                else
                {
                    _btnRects = Array.Empty<Rectangle>();
                }
            }

            Present();
        }

        private void DrawLyrics(Graphics g)
        {
            string prev = _visibleLines >= 3 ? GetLineText(_currentIndex > 0 ? _currentIndex - 1 : -1) : string.Empty;
            LyricLine? curLine = GetLine(_currentIndex >= 0 ? _currentIndex : 0);
            string cur = curLine?.Text ?? string.Empty;
            string next = _visibleLines >= 2 ? GetLineText(_currentIndex >= 0 && _currentIndex + 1 < _lines.Count ? _currentIndex + 1 : -1) : string.Empty;

            using FontFamily family = CreateFontFamily(_fontFamilyName);
            float sideSize = Math.Max(12f, _fontSize * 0.72f);
            float centerX = _width / 2f;

            // 半透明底色条：提升在复杂/亮色壁纸上的可读性（默认关）
            if (_showBackgroundBar)
            {
                using var bar = new SolidBrush(_backgroundColor);
                g.FillRectangle(bar, 0, ToolbarHeight, _width, _height - ToolbarHeight);
            }

            float y = ToolbarHeight + 8;

            if (!string.IsNullOrEmpty(prev))
            {
                SizeF sz = MeasurePathSize(prev, family, sideSize, FontStyle.Regular);
                float x = XFor(sideSize, sz.Width, centerX);
                DrawTextStyled(g, prev, family, sideSize, FontStyle.Regular, Color.FromArgb(160, _unplayedColor), x, y);
                y += sz.Height + 6 + _lineSpacing;
            }

            if (!string.IsNullOrEmpty(cur))
            {
                SizeF sz = MeasurePathSize(cur, family, _fontSize, FontStyle.Bold);
                float x = XFor(_fontSize, sz.Width, centerX);
                DrawShadow(g, cur, family, _fontSize, x, y);
                if (_karaokeStyle && curLine != null)
                {
                    DrawKaraokeLine(g, curLine, family, x, y, sz);
                }
                else
                {
                    DrawTextStyled(g, cur, family, _fontSize, FontStyle.Bold, _playedColor, x, y, shadow: false);
                }

                y += sz.Height + 6 + _lineSpacing;
            }

            if (!string.IsNullOrEmpty(next))
            {
                SizeF sz = MeasurePathSize(next, family, sideSize, FontStyle.Regular);
                float x = XFor(sideSize, sz.Width, centerX);
                DrawTextStyled(g, next, family, sideSize, FontStyle.Regular, Color.FromArgb(160, _unplayedColor), x, y);
            }
        }

        private float XFor(float size, float textWidth, float centerX)
        {
            return _align switch
            {
                "Left" => 24f,
                "Right" => Math.Max(24f, _width - 24f - textWidth),
                _ => centerX - textWidth / 2f
            };
        }

        /// <summary>阴影：强度 0=关；1/2/3 控制不透明度与偏移。</summary>
        private void DrawShadow(Graphics g, string text, FontFamily family, float size, float x, float y)
        {
            if (_shadowStrength <= 0)
            {
                return;
            }

            int a = _shadowStrength switch
            {
                1 => 90,
                2 => 150,
                3 => 210,
                _ => 0
            };
            if (a <= 0)
            {
                return;
            }

            float off = _shadowStrength >= 3 ? 2.5f : 1.5f;
            using var sb = new SolidBrush(Color.FromArgb((byte)a, 0, 0, 0));
            DrawTextPath(g, text, family, size, FontStyle.Bold, sb, x + off, y + off);
        }

        /// <summary>画一行文字：可选阴影（底层）+ 可选描边（上层）+ 填充。</summary>
        private void DrawTextStyled(Graphics g, string text, FontFamily family, float size, FontStyle style, Color fill, float x, float y, bool shadow = true)
        {
            if (shadow)
            {
                DrawShadow(g, text, family, size, x, y);
            }

            if (_outlineWidth > 0.05f)
            {
                using var pen = new Pen(_outlineColor, _outlineWidth);
                pen.LineJoin = LineJoin.Round;
                using var op = new GraphicsPath();
                op.AddString(text, family, (int)style, size, new PointF(x, y), StringFormat.GenericTypographic);
                g.DrawPath(pen, op);
            }

            using var fb = new SolidBrush(fill);
            DrawTextPath(g, text, family, size, style, fb, x, y);
        }

        private void DrawKaraokeLine(Graphics g, LyricLine line, FontFamily family, float x, float y, SizeF size)
        {
            string text = line.Text;

            using var unplayed = new SolidBrush(_unplayedColor);
            using var played = new SolidBrush(_playedColor);
            // 底色必须先画：这句时间还没唱到时（逐字歌词的第一个字之前）也要有字，
            // 只画阴影会让整行"消失"。
            DrawTextPath(g, text, family, _fontSize, FontStyle.Bold, unplayed, x, y);

            float highlightW = ComputeHighlightWidth(line, text, family, size);
            if (highlightW <= 0.3f)
            {
                return;
            }

            highlightW = Math.Min(highlightW, size.Width);

            // 前沿羽化带：宽度随字号缩放；起步阶段让它退化成"整块都是渐变"，
            // 这样第一个字刚开始时是淡入，而不是突然冒出一小块实心色块。
            float soft = Math.Min(28f, Math.Max(12f, size.Width * 0.05f));
            soft = Math.Min(soft, highlightW);
            // 快唱完时羽化带随剩余距离收窄，整行唱满就是纯色，不会在行尾留一段发灰
            float tail = Math.Max(0f, size.Width - highlightW);
            if (tail < soft)
            {
                soft = tail;
            }

            float solidW = highlightW - soft;

            // 实心段 0..solidW（已唱色，不透明）
            if (solidW > 0.5f)
            {
                GraphicsState state = g.Save();
                g.SetClip(new RectangleF(x, y - 2, solidW, size.Height + 4));
                DrawTextPath(g, text, family, _fontSize, FontStyle.Bold, played, x, y);
                g.Restore(state);
            }

            // 羽化段 solidW..solidW+soft，已唱色 255 → 0。
            // 和实心段首尾相接、不重叠（旧实现两段重叠且起点只有 220 alpha，边界会出现一条色带）。
            if (soft > 0.5f)
            {
                float edgeX = x + solidW;
                using var edgeBrush = new LinearGradientBrush(
                    new PointF(edgeX, y),
                    new PointF(edgeX + soft, y),
                    Color.FromArgb(255, _playedColor),
                    Color.FromArgb(0, _playedColor));
                GraphicsState edgeState = g.Save();
                g.SetClip(new RectangleF(edgeX, y - 2, soft, size.Height + 4));
                DrawTextPath(g, text, family, _fontSize, FontStyle.Bold, edgeBrush, x, y);
                g.Restore(edgeState);
            }

            // 描边放最上层：卡拉 OK 填充（不透明）会盖住下层的描边，
            // 所以必须最后画，只露出字形外缘一圈，提升在亮色壁纸上的可读性。
            if (_outlineWidth > 0.05f)
            {
                using var pen = new Pen(_outlineColor, _outlineWidth);
                pen.LineJoin = LineJoin.Round;
                using var op = new GraphicsPath();
                op.AddString(text, family, (int)FontStyle.Bold, _fontSize, new PointF(x, y), StringFormat.GenericTypographic);
                g.DrawPath(pen, op);
            }
        }

        /// <summary>
        /// 已唱部分应该填充到多少像素宽。
        /// 逐字歌词按"字内比例"连续推进（不再一个字一跳），普通歌词按整行进度。
        /// </summary>
        private float ComputeHighlightWidth(LyricLine line, string text, FontFamily family, SizeF size)
        {
            IReadOnlyList<TimeSpan>? charTimes = line.CharTimes;
            if (charTimes == null || charTimes.Count != text.Length || text.Length == 0)
            {
                return (float)(size.Width * Math.Clamp(_displayProgress, 0, 1));
            }

            int n = 0;
            for (int i = 0; i < charTimes.Count; i++)
            {
                if (charTimes[i] <= _position)
                {
                    n = i + 1;
                }
                else
                {
                    break;
                }
            }

            if (n == 0)
            {
                return 0f;
            }

            float[] widths = GetCharWidths(text, family, out float total);
            int done = n - 1; // 已经完整唱过去的字数
            if (done >= widths.Length)
            {
                return size.Width;
            }

            float wDone = 0f;
            for (int i = 0; i < done; i++)
            {
                wDone += widths[i];
            }

            // 正在唱的这个字：按它在相邻两个时间戳之间的比例连续推进，
            // smoothstep 让每个字的起落更柔和（避免字与字之间出现台阶感）。
            TimeSpan t0 = charTimes[done];
            TimeSpan t1 = done + 1 < charTimes.Count
                ? charTimes[done + 1]
                : t0 + TimeSpan.FromMilliseconds(320);
            double span = Math.Max(0.08, (t1 - t0).TotalSeconds);
            double frac = Math.Clamp((_position - t0).TotalSeconds / span, 0, 1);
            frac = frac * frac * (3 - 2 * frac);

            // 逐字累加宽度和整行测量宽度不完全一致（字距、字形外扩），按比例归一
            float scale = total > 0.5f ? size.Width / total : 1f;
            return (wDone + widths[done] * (float)frac) * scale;
        }

        /// <summary>逐字宽度缓存：同一行同一字号只测量一次，不必每帧重算。</summary>
        private float[] GetCharWidths(string text, FontFamily family, out float total)
        {
            if (_charWidths != null
                && _charWidths.Length == text.Length
                && _charWidthsIndex == _currentIndex
                && string.Equals(_charWidthsText, text, StringComparison.Ordinal)
                && Math.Abs(_fontSizeAtMeasure - _fontSize) < 0.01f)
            {
                total = _charWidthsTotal;
                return _charWidths;
            }

            float[] widths = new float[text.Length];
            float sum = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                float w = MeasurePathSize(text.Substring(i, 1), family, _fontSize, FontStyle.Bold).Width;
                widths[i] = w;
                sum += w;
            }

            _charWidths = widths;
            _charWidthsTotal = sum;
            _charWidthsIndex = _currentIndex;
            _charWidthsText = text;
            _fontSizeAtMeasure = _fontSize;
            total = sum;
            return widths;
        }

        private static FontFamily CreateFontFamily(string name)
        {
            try
            {
                return new FontFamily(name);
            }
            catch
            {
                return new FontFamily(GenericFontFamilies.SansSerif);
            }
        }

        private static SizeF MeasurePathSize(string text, FontFamily family, float emSize, FontStyle style)
        {
            if (string.IsNullOrEmpty(text))
            {
                return SizeF.Empty;
            }

            using var path = new GraphicsPath();
            path.AddString(
                text,
                family,
                (int)style,
                emSize,
                PointF.Empty,
                StringFormat.GenericTypographic);
            RectangleF bounds = path.GetBounds();
            return new SizeF(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        }

        private static SizeF MeasurePathSize(Graphics g, string text, FontFamily family, float emSize, FontStyle style)
            => MeasurePathSize(text, family, emSize, style);

        private static void DrawTextPath(
            Graphics g,
            string text,
            FontFamily family,
            float emSize,
            FontStyle style,
            Brush brush,
            float x,
            float y)
        {
            using var path = new GraphicsPath();
            path.AddString(
                text,
                family,
                (int)style,
                emSize,
                new PointF(x, y),
                StringFormat.GenericTypographic);
            g.FillPath(brush, path);
        }

        private void DrawToolbar(Graphics g)
        {
            if (_locked)
            {
                DrawLockOnlyToolbar(g);
                return;
            }

            int count = (int)ToolbarBtn.Count;
            int pad = 12;
            int totalW = count * BtnSize + (count - 1) * BtnGap + pad * 2;
            int barX = (_width - totalW) / 2;
            int barY = 6;
            int barH = ToolbarHeight - 8;
            var barRect = new Rectangle(barX, barY, totalW, barH);

            using (var path = RoundedRect(barRect, 12))
            using (var brush = new SolidBrush(Color.FromArgb(220, 28, 28, 28)))
            using (var pen = new Pen(Color.FromArgb(100, 255, 255, 255)))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            _btnRects = new Rectangle[count];
            int bx = barX + pad;
            int by = barY + (barH - BtnSize) / 2;
            for (int i = 0; i < count; i++)
            {
                _btnRects[i] = new Rectangle(bx, by, BtnSize, BtnSize);
                bool pressed = _pressedBtn == i;
                if (pressed)
                {
                    using var p = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
                    g.FillRectangle(p, _btnRects[i]);
                }

                DrawBtnLabel(g, (ToolbarBtn)i, _btnRects[i]);
                bx += BtnSize + BtnGap;
            }
        }

        private void DrawLockOnlyToolbar(Graphics g)
        {
            Rectangle lockRect = LayoutLockOnlyButton(assignRects: true);
            int pad = 10;
            var barRect = new Rectangle(lockRect.X - pad, lockRect.Y - 4, lockRect.Width + pad * 2, lockRect.Height + 8);
            using (var path = RoundedRect(barRect, 12))
            using (var brush = new SolidBrush(Color.FromArgb(220, 28, 28, 28)))
            using (var pen = new Pen(Color.FromArgb(100, 255, 255, 255)))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            if (_pressedBtn == 0)
            {
                using var p = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
                g.FillRectangle(p, lockRect);
            }

            DrawBtnLabel(g, ToolbarBtn.Lock, lockRect);
        }

        private Rectangle LayoutLockOnlyButton(bool assignRects)
        {
            int barY = 6;
            int barH = ToolbarHeight - 8;
            int bx = (_width - BtnSize) / 2;
            int by = barY + (barH - BtnSize) / 2;
            var lockRect = new Rectangle(bx, by, BtnSize, BtnSize);
            if (assignRects)
            {
                // 锁定态只有一个按钮，索引 0 表示解锁
                _btnRects = new[] { lockRect };
            }

            return lockRect;
        }

        private void DrawBtnLabel(Graphics g, ToolbarBtn btn, Rectangle r)
        {
            using var brush = new SolidBrush(Color.White);
            if (btn is ToolbarBtn.Settings or ToolbarBtn.Lock or ToolbarBtn.Close)
            {
                using var iconFont = new Font("Segoe MDL2 Assets", 18f, FontStyle.Regular, GraphicsUnit.Pixel);
                string glyph = btn switch
                {
                    ToolbarBtn.Settings => "\uE713",
                    ToolbarBtn.Lock => _locked ? "\uE72E" : "\uE785",
                    ToolbarBtn.Close => "\uE711",
                    _ => ""
                };
                SizeF sz = g.MeasureString(glyph, iconFont);
                g.DrawString(glyph, iconFont, brush, r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f);
                return;
            }

            using var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
            string label = btn == ToolbarBtn.FontMinus ? "A-" : "A+";
            SizeF s = g.MeasureString(label, font);
            g.DrawString(label, font, brush, r.X + (r.Width - s.Width) / 2f, r.Y + (r.Height - s.Height) / 2f);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 确保持久后台位图存在且与当前窗口尺寸一致（尺寸变化或尚未创建时重建）
        private void EnsureBackbuffer()
        {
            if (_memDc == IntPtr.Zero)
            {
                IntPtr screenDc = GetDC(IntPtr.Zero);
                _memDc = CreateCompatibleDC(screenDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }

            if (_memDc == IntPtr.Zero || (_hBitmap != IntPtr.Zero && _backW == _width && _backH == _height))
            {
                return;
            }

            // 旧位图先还原并释放，再按新尺寸建一块 32 位 ARGB DIB（真正的像素缓冲，不是拷贝）
            if (_hBitmap != IntPtr.Zero)
            {
                SelectObject(_memDc, _oldBitmap);
                DeleteObject(_hBitmap);
                _hBitmap = IntPtr.Zero;
                _oldBitmap = IntPtr.Zero;
            }

            IntPtr screenDc2 = GetDC(IntPtr.Zero);
            var info = new BITMAPINFO
            {
                BiSize = Marshal.SizeOf<BITMAPINFO>(),
                BiWidth = _width,
                BiHeight = -_height, // 负值=自上而下，与 GDI+ 坐标一致，避免上下翻转
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = BI_RGB
            };
            _hBitmap = CreateDIBSection(screenDc2, ref info, DIB_RGB_COLORS, out _, IntPtr.Zero, 0);
            ReleaseDC(IntPtr.Zero, screenDc2);

            if (_hBitmap == IntPtr.Zero)
            {
                return;
            }

            _oldBitmap = SelectObject(_memDc, _hBitmap);
            _backW = _width;
            _backH = _height;
        }

        // 把持久后台位图原子地提交到分层窗口（UpdateLayeredWindow 本身不闪）
        private void Present()
        {
            if (_memDc == IntPtr.Zero || _hBitmap == IntPtr.Zero)
            {
                EnsureBackbuffer();
            }

            if (_memDc == IntPtr.Zero || _hBitmap == IntPtr.Zero)
            {
                return;
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            var size = new SIZE { Cx = _width, Cy = _height };
            var pointSource = new POINT { X = 0, Y = 0 };
            var topPos = new POINT { X = _x, Y = _y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Clamp((int)Math.Round(_opacityPercent * 2.55), 51, 255),
                AlphaFormat = AcSrcAlpha
            };

            UpdateLayeredWindow(
                _hwnd,
                screenDc,
                ref topPos,
                ref size,
                _memDc,
                ref pointSource,
                0,
                ref blend,
                UlwAlpha);

            ReleaseDC(IntPtr.Zero, screenDc);
        }

        // 释放持久后台位图与 DC（Dispose 时调用；先还原默认位图再删，避免 DeleteDC 行为未定义）
        private void CleanupBackbuffer()
        {
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            {
                SelectObject(_memDc, _oldBitmap);
                _oldBitmap = IntPtr.Zero;
            }

            if (_hBitmap != IntPtr.Zero)
            {
                DeleteObject(_hBitmap);
                _hBitmap = IntPtr.Zero;
            }

            if (_memDc != IntPtr.Zero)
            {
                DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }

            _backW = _backH = 0;
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WmNcDestroy:
                    KillTimer(hWnd, TimerId);
                    _hwnd = IntPtr.Zero;
                    ClosedByUser?.Invoke();
                    break;

                case WmTimer:
                    if (wParam.ToInt32() == TimerId)
                    {
                        OnTick();
                    }

                    return IntPtr.Zero;

                case WmMouseMove:
                    OnMouseMove(GetX(lParam), GetY(lParam));
                    return IntPtr.Zero;

                case WmLButtonDown:
                    OnLButtonDown(GetX(lParam), GetY(lParam));
                    return IntPtr.Zero;

                case WmLButtonUp:
                    OnLButtonUp(GetX(lParam), GetY(lParam));
                    return IntPtr.Zero;

                case WmMouseLeave:
                    if (!_dragging)
                    {
                        _hover = false;
                        if (!_locked)
                        {
                            Redraw();
                        }
                    }

                    return IntPtr.Zero;

                case WmMouseHover:
                    if (!_locked)
                    {
                        _hover = true;
                        Redraw();
                    }

                    return IntPtr.Zero;
            }

            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        private void OnTick()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            bool needRedraw = false;
            if (GetCursorPos(out POINT pt))
            {
                bool inWindow = pt.X >= _x && pt.X < _x + _width && pt.Y >= _y && pt.Y < _y + _height;
                if (inWindow != _mouseInWindow)
                {
                    _mouseInWindow = inWindow;
                    needRedraw = true;
                }

                if (_locked)
                {
                    if (_btnRects.Length == 0)
                    {
                        LayoutLockOnlyButton(assignRects: true);
                    }

                    bool overLock = false;
                    if (_btnRects.Length > 0)
                    {
                        Rectangle r = _btnRects[0];
                        int lx = _x + r.X;
                        int ly = _y + r.Y;
                        overLock = pt.X >= lx && pt.X < lx + r.Width && pt.Y >= ly && pt.Y < ly + r.Height;
                    }

                    if (overLock != _unlockHot)
                    {
                        _unlockHot = overLock;
                        ApplyExStyle();
                        needRedraw = true;
                    }
                }
            }

            if (PositionProvider != null)
            {
                ApplyPosition(PositionProvider(), forceRedraw: needRedraw);
            }
            else if (needRedraw)
            {
                Redraw();
            }
        }

        private void TrackMouse()
        {
            var tme = new TRACKMOUSEEVENT
            {
                CbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
                DwFlags = TmeHover | TmeLeave,
                HwndTrack = _hwnd,
                DwHoverTime = 1
            };
            TrackMouseEvent(ref tme);
        }

        private void OnMouseMove(int x, int y)
        {
            TrackMouse();
            if (!_locked && !_hover)
            {
                _hover = true;
                Redraw();
                return;
            }

            if (_dragging)
            {
                if (!GetCursorPos(out POINT pt))
                {
                    return;
                }

                _x = pt.X - _dragOffsetX;
                _y = pt.Y - _dragOffsetY;
                SetWindowPos(_hwnd, HwndTopMost, _x, _y, _width, _height, SwpNoActivate);
            }
        }

        private void OnLButtonDown(int x, int y)
        {
            int btn = HitTestButton(x, y);
            if (btn >= 0)
            {
                _pressedBtn = btn;
                Redraw();
                return;
            }

            if (_locked)
            {
                return;
            }

            if (!GetCursorPos(out POINT pt))
            {
                return;
            }

            _dragging = true;
            _dragOffsetX = pt.X - _x;
            _dragOffsetY = pt.Y - _y;
            SetCapture(_hwnd);
        }

        private void OnLButtonUp(int x, int y)
        {
            if (_dragging)
            {
                _dragging = false;
                ReleaseCapture();
            }

            int btn = _pressedBtn;
            _pressedBtn = -1;
            if (btn >= 0 && HitTestButton(x, y) == btn)
            {
                if (_locked)
                {
                    // 锁定态唯一按钮 = 解锁
                    SetLocked(false);
                }
                else
                {
                    HandleButtonClick((ToolbarBtn)btn);
                }
            }

            Redraw();
        }

        private int HitTestButton(int x, int y)
        {
            for (int i = 0; i < _btnRects.Length; i++)
            {
                if (_btnRects[i].Contains(x, y))
                {
                    return i;
                }
            }

            return -1;
        }

        private void HandleButtonClick(ToolbarBtn btn)
        {
            switch (btn)
            {
                case ToolbarBtn.FontMinus:
                    _fontSize = Math.Max(14f, _fontSize - 2f);
                    AppSettingsStore.Update(s => s.DesktopLyricFontSize = _fontSize);
                    ResizeForContent();
                    Redraw();
                    break;
                case ToolbarBtn.FontPlus:
                    _fontSize = Math.Min(64f, _fontSize + 2f);
                    AppSettingsStore.Update(s => s.DesktopLyricFontSize = _fontSize);
                    ResizeForContent();
                    Redraw();
                    break;
                case ToolbarBtn.Settings:
                    SettingsWindow.ShowOrActivate();
                    break;
                case ToolbarBtn.Lock:
                    SetLocked(!_locked);
                    break;
                case ToolbarBtn.Close:
                    Close();
                    break;
            }
        }

        private static readonly WndProcDelegate ClassWndProcKeepAlive = ClassWndProc;

        private static IntPtr ClassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            => DefWindowProcW(hWnd, msg, wParam, lParam);

        private static void RegisterClass()
        {
            lock (ClassGate)
            {
                if (_classRegistered)
                {
                    return;
                }

                var wc = new WNDCLASSEXW
                {
                    CbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                    Style = CsDblClks | CsHRedraw | CsVRedraw,
                    LpfnWndProc = Marshal.GetFunctionPointerForDelegate(ClassWndProcKeepAlive),
                    HInstance = GetModuleHandleW(null),
                    HCursor = LoadCursorW(IntPtr.Zero, IdcArrow),
                    LpszClassName = WindowClassName
                };

                if (RegisterClassExW(ref wc) == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1410) // already exists
                    {
                        throw new InvalidOperationException("RegisterClassEx failed: " + err);
                    }
                }

                _classRegistered = true;
            }
        }

        private static IntPtr SetWindowProc(IntPtr hwnd, IntPtr proc)
        {
            if (IntPtr.Size == 8)
            {
                return SetWindowLongPtr(hwnd, GwlpWndProc, proc);
            }

            return new IntPtr(SetWindowLong(hwnd, GwlpWndProc, proc.ToInt32()));
        }

        private static void GetWorkArea(out int left, out int top, out int right, out int bottom)
        {
            var rc = new RECT();
            SystemParametersInfoW(SpiGetWorkArea, 0, ref rc, 0);
            left = rc.Left;
            top = rc.Top;
            right = rc.Right;
            bottom = rc.Bottom;
        }

        private static int GetX(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);

        private static int GetY(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        #region Win32

        private const int GwlExStyle = -20;
        private const int GwlpWndProc = -4;
        private const int WsPopup = unchecked((int)0x80000000);
        private const int WsExLayered = 0x00080000;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExTopMost = 0x00000008;
        private const int WsExTransparent = 0x00000020;
        private const int WsExNoActivate = 0x08000000;
        private const uint UlwAlpha = 0x00000002;
        private const byte AcSrcOver = 0x00;
        private const byte AcSrcAlpha = 0x01;
        private const int SwHide = 0;
        private const int SwShowNoActivate = 4;
        private const int SwpNoMove = 0x0002;
        private const int SwpNoSize = 0x0001;
        private const int SwpNoActivate = 0x0010;
        private static readonly IntPtr HwndTopMost = new(-1);
        private const uint WmMouseMove = 0x0200;
        private const uint WmLButtonDown = 0x0201;
        private const uint WmLButtonUp = 0x0202;
        private const uint WmMouseLeave = 0x02A3;
        private const uint WmMouseHover = 0x02A1;
        private const uint WmNcDestroy = 0x0082;
        private const uint WmTimer = 0x0113;
        private const uint TmeHover = 0x00000001;
        private const uint TmeLeave = 0x00000002;
        private const uint CsDblClks = 0x0008;
        private const uint CsHRedraw = 0x0002;
        private const uint CsVRedraw = 0x0001;
        private const int SpiGetWorkArea = 0x0030;
        private static readonly IntPtr IdcArrow = new(32512);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int Cx;
            public int Cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        // 持久后台位图用的 DIB 头：32 位 ARGB（BI_RGB），负高度=自上而下，与 GDI+ 坐标一致。
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public int BiSize;
            public int BiWidth;
            public int BiHeight;
            public short BiPlanes;
            public short BiBitCount;
            public int BiCompression;
            public int BiSizeImage;
            public int BiXPelsPerMeter;
            public int BiYPelsPerMeter;
            public int BiClrUsed;
            public int BiClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public int CbSize;
            public uint DwFlags;
            public IntPtr HwndTrack;
            public uint DwHoverTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public int CbSize;
            public uint Style;
            public IntPtr LpfnWndProc;
            public int CbClsExtra;
            public int CbWndExtra;
            public IntPtr HInstance;
            public IntPtr HIcon;
            public IntPtr HCursor;
            public IntPtr HbrBackground;
            public string? LpszMenuName;
            public string LpszClassName;
            public IntPtr HIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            int crKey,
            ref BLENDFUNCTION pblend,
            uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        // 创建一块真正的 32 位 ARGB 像素缓冲（并非拷贝），GDI+ 直接画进它的 DC，
        // UpdateLayeredWindow 直接读它 —— 既不踩 GetHbitmap 的"拷贝不更新"坑，也避免了每帧复制/转换的开销。
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset);

        private const int BI_RGB = 0;
        private const uint DIB_RGB_COLORS = 0;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr SetTimer(IntPtr hWnd, int nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern bool KillTimer(IntPtr hWnd, int uIDEvent);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SystemParametersInfoW(int uiAction, int uiParam, ref RECT pvParam, int fWinIni);

        #endregion
    }
}
