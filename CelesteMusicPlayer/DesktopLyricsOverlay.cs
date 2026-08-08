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
        private const uint TimerIntervalMs = 33; // ~30fps，歌词双色更跟手

        private static bool _classRegistered;
        private static readonly object ClassGate = new();

        private readonly List<LyricLine> _lines = new();
        private IntPtr _hwnd;
        private IntPtr _wndProcPtr;
        private WndProcDelegate? _wndProcKeepAlive;
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
        private bool _karaokeStyle = true;

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
            _lines.Clear();
            _lines.AddRange(lines);
            _currentIndex = -1;
            _displayProgress = 0;
            ResizeForContent();
            UpdateVisibilityForState();
            Redraw();
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
                ResizeForContent();
            }

            double target = ComputeLineProgress(position, index);
            // 向目标平滑靠拢，减少跳跃感
            double lerp = indexChanged ? 1.0 : 0.42;
            _displayProgress += (target - _displayProgress) * lerp;
            if (Math.Abs(target - _displayProgress) < 0.002)
            {
                _displayProgress = target;
            }

            if (forceRedraw || indexChanged || Math.Abs(target - _displayProgress) >= 0.0005 || target > 0.995)
            {
                Redraw();
            }
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
            _playedColor = ParseHexColor(settings.DesktopLyricPlayedColor, Color.FromArgb(255, 64, 180, 255));
            _unplayedColor = ParseHexColor(settings.DesktopLyricUnplayedColor, Color.FromArgb(255, 245, 245, 245));
            _opacityPercent = Math.Clamp(settings.DesktopLyricOpacity, 20, 100);
            _hideWithoutLyric = settings.DesktopLyricHideWithoutLyric;
            _hideWhenPaused = settings.DesktopLyricHideWhenPaused;
            _showUnlockWhenLocked = settings.DesktopLyricShowUnlockWhenLocked;
            _doubleLine = settings.DesktopLyricDoubleLine;
            _clickThrough = settings.DesktopLyricClickThrough;
            _karaokeStyle = settings.LyricKaraokeStyle;

            if (settings.DesktopLyricLockOnStart && !_locked)
            {
                SetLocked(true);
            }

            ApplyExStyle();
            UpdateVisibilityForState();
            Redraw();
        }

        private bool _hideWithoutLyric;
        private bool _hideWhenPaused;
        private bool _showUnlockWhenLocked = true;
        private bool _doubleLine = true;
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
            catch
            {
            }

            return fallback;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
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
            return (int)Math.Ceiling(ToolbarHeight + 16 + side * 1.4f + _fontSize * 1.45f + side * 1.4f + 16);
        }

        private void ResizeForContent()
        {
            string cur = GetLineText(_currentIndex >= 0 ? _currentIndex : 0);
            string prev = GetLineText(_currentIndex > 0 ? _currentIndex - 1 : -1);
            string next = GetLineText(_currentIndex >= 0 && _currentIndex + 1 < _lines.Count ? _currentIndex + 1 : -1);

            using FontFamily family = CreateFontFamily();
            using var tmp = new Bitmap(1, 1);
            using Graphics g = Graphics.FromImage(tmp);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            float maxW = Math.Max(
                MeasurePathSize(g, cur, family, _fontSize, FontStyle.Bold).Width,
                Math.Max(
                    MeasurePathSize(g, prev, family, Math.Max(12f, _fontSize * 0.72f), FontStyle.Regular).Width,
                    MeasurePathSize(g, next, family, Math.Max(12f, _fontSize * 0.72f), FontStyle.Regular).Width));

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

            using var bmp = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
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

            UpdateLayeredFromBitmap(bmp);
        }

        private void DrawLyrics(Graphics g)
        {
            string prev = GetLineText(_currentIndex > 0 ? _currentIndex - 1 : -1);
            string cur = GetLineText(_currentIndex >= 0 ? _currentIndex : 0);
            string next = GetLineText(_currentIndex >= 0 && _currentIndex + 1 < _lines.Count ? _currentIndex + 1 : -1);

            float sideSize = Math.Max(12f, _fontSize * 0.72f);
            if (!_doubleLine)
            {
                prev = string.Empty;
                next = string.Empty;
            }
            using FontFamily family = CreateFontFamily();
            using var sideBrush = new SolidBrush(Color.FromArgb(160, _unplayedColor));
            using var shadowBrush = new SolidBrush(Color.FromArgb(140, 0, 0, 0));

            float y = ToolbarHeight + 8;
            float centerX = _width / 2f;

            if (!string.IsNullOrEmpty(prev))
            {
                SizeF sz = MeasurePathSize(prev, family, sideSize, FontStyle.Regular);
                float x = centerX - sz.Width / 2f;
                DrawTextPath(g, prev, family, sideSize, FontStyle.Regular, shadowBrush, x + 1.5f, y + 1.5f);
                DrawTextPath(g, prev, family, sideSize, FontStyle.Regular, sideBrush, x, y);
                y += sz.Height + 6;
            }

            if (!string.IsNullOrEmpty(cur))
            {
                SizeF sz = MeasurePathSize(cur, family, _fontSize, FontStyle.Bold);
                float x = centerX - sz.Width / 2f;
                DrawTextPath(g, cur, family, _fontSize, FontStyle.Bold, shadowBrush, x + 1.5f, y + 1.5f);
                if (_karaokeStyle)
                {
                    DrawKaraokeLine(g, cur, family, x, y, sz);
                }
                else
                {
                    using var curBrush = new SolidBrush(_playedColor);
                    DrawTextPath(g, cur, family, _fontSize, FontStyle.Bold, curBrush, x, y);
                }

                y += sz.Height + 6;
            }

            if (!string.IsNullOrEmpty(next))
            {
                SizeF sz = MeasurePathSize(next, family, sideSize, FontStyle.Regular);
                float x = centerX - sz.Width / 2f;
                DrawTextPath(g, next, family, sideSize, FontStyle.Regular, shadowBrush, x + 1.5f, y + 1.5f);
                DrawTextPath(g, next, family, sideSize, FontStyle.Regular, sideBrush, x, y);
            }
        }

        private void DrawKaraokeLine(Graphics g, string text, FontFamily family, float x, float y, SizeF size)
        {
            double progress = Math.Clamp(_displayProgress, 0, 1);

            using var unplayed = new SolidBrush(_unplayedColor);
            using var played = new SolidBrush(_playedColor);
            DrawTextPath(g, text, family, _fontSize, FontStyle.Bold, unplayed, x, y);

            float highlightW = (float)(size.Width * progress);
            if (highlightW <= 0.5f)
            {
                return;
            }

            float soft = Math.Min(18f, Math.Max(8f, size.Width * 0.035f));
            GraphicsState state = g.Save();
            g.SetClip(new RectangleF(x, y - 2, Math.Max(0, highlightW - soft * 0.15f), size.Height + 4));
            DrawTextPath(g, text, family, _fontSize, FontStyle.Bold, played, x, y);
            g.Restore(state);

            // 切线附近柔和过渡，减轻硬切
            if (soft > 1 && highlightW > soft * 0.5f)
            {
                float edgeX = x + highlightW - soft;
                using var edgeBrush = new LinearGradientBrush(
                    new PointF(edgeX, y),
                    new PointF(edgeX + soft, y),
                    Color.FromArgb(220, _playedColor),
                    Color.FromArgb(0, _playedColor));
                GraphicsState edgeState = g.Save();
                g.SetClip(new RectangleF(edgeX, y - 2, soft, size.Height + 4));
                DrawTextPath(g, text, family, _fontSize, FontStyle.Bold, edgeBrush, x, y);
                g.Restore(edgeState);
            }
        }

        private static FontFamily CreateFontFamily()
        {
            try
            {
                return new FontFamily("Microsoft YaHei UI");
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

        private void UpdateLayeredFromBitmap(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr oldBitmap = SelectObject(memDc, hBitmap);

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
                memDc,
                ref pointSource,
                0,
                ref blend,
                UlwAlpha);

            SelectObject(memDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
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
                    ResizeForContent();
                    Redraw();
                    break;
                case ToolbarBtn.FontPlus:
                    _fontSize = Math.Min(64f, _fontSize + 2f);
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
