using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace CelesteMusicPlayer
{
    // Native themed menu: direct HWND input, no WebView initialization or JS bridge.
    internal sealed class TrayMenuWindow : IDisposable
    {
        private readonly NativeDesktopPopup _window = new(true);
        private readonly DispatcherQueueTimer _outside;
        private IReadOnlyList<TrayMenuModel.Item> _items = Array.Empty<TrayMenuModel.Item>();
        private DesktopMenuLayout _layout = new(Array.Empty<bool>(), 1);
        private int _selected = -1;
        private float _textScale = 1, _fit = 1;
        private Color _background = Color.FromArgb(255, 253, 249), _ink = Color.FromArgb(57, 53, 47), _muted = Color.Gray, _accent = Color.FromArgb(155, 120, 75), _hover = Color.FromArgb(240, 233, 223), _line = Color.FromArgb(235, 229, 219);
        private long _shownAt;
        internal Func<WindowsTrayIcon.RECT?>? IconRectProvider { get; set; }
        internal event Action<string>? Invoked;
        internal event Action? Hidden;
        internal bool IsReady => true;
        internal bool IsVisible => _window.Visible;
        internal TrayMenuWindow()
        {
            _window.Paint = Paint;
            _window.Click = point => Invoke(_layout.Hit(point.X / _fit, point.Y / _fit));
            _window.Hover = point => { int next = _layout.Hit(point.X / _fit, point.Y / _fit); if (next != _selected) { _selected = next; _window.Invalidate(); } };
            _window.Key = HandleKey;
            _window.Failed = ex => StartupLog.WriteException("TrayNative.Window", ex);
            _outside = DispatcherQueue.GetForCurrentThread().CreateTimer(); _outside.Interval = TimeSpan.FromMilliseconds(100);
            _outside.Tick += (_, _) => CloseOnOutsidePress();
        }

        private void HandleKey(int key)
        {
            if (key == 27) { Hide(); return; }
            if (key == 13) { Invoke(_selected); return; }
            if (key != 38 && key != 40) return;
            int step = key == 40 ? 1 : -1;
            if (_selected < 0 && step < 0) _selected = 0;
            for (int n = 0; n < _items.Count; n++)
            {
                _selected = (_selected + step + _items.Count) % _items.Count;
                if (!_items[_selected].separator && _items[_selected].enabled) break;
            }
            _window.Invalidate();
        }

        private void CloseOnOutsidePress()
        {
            if (!IsVisible || Environment.TickCount64 - _shownAt < 250 || !NativeDesktopPopup.MouseDown()) return;
            Point cursor = NativeDesktopPopup.Cursor();
            if (NativeDesktopPopup.Bounds(_window.Handle).Contains(cursor)) return;
            var icon = IconRectProvider?.Invoke();
            if (icon is {} r && Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).Contains(cursor)) return;
            Hide();
        }

        internal void UpdateContent(IReadOnlyList<TrayMenuModel.Item> items, Dictionary<string, string> vars, string mode)
        {
            _items = items; _selected = -1;
            Color Read(string key, Color fallback) { try { return vars.TryGetValue(key, out string? value) && value.StartsWith('#') ? ColorTranslator.FromHtml(value) : fallback; } catch { return fallback; } }
            bool dark = mode == "dark"; _background = Read("menu-bg", dark ? Color.FromArgb(37, 36, 34) : Color.FromArgb(255, 253, 249));
            _ink = Read("ink", dark ? Color.Gainsboro : Color.FromArgb(57, 53, 47)); _muted = Read("muted", Color.Gray); _accent = Read("accent", Color.Tan);
            _hover = Read("hover", dark ? Color.FromArgb(56, 51, 44) : Color.FromArgb(240, 233, 223)); _line = Read("line", dark ? Color.FromArgb(59, 55, 50) : Color.FromArgb(235, 229, 219));
            _textScale = vars.TryGetValue("text-scale", out string? scale) && float.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? Math.Clamp(parsed, .8f, 1.5f) : 1;
            _layout = new(items.Select(i => i.separator), _textScale);
        }
        internal Task<bool> ShowAsync(IReadOnlyList<TrayMenuModel.Item> items, Dictionary<string, string> vars, string mode)
        {
            UpdateContent(items, vars, mode);
            Point cursor = NativeDesktopPopup.Cursor(); var work = NativeDesktopPopup.WorkArea(cursor); float dpi = NativeDesktopPopup.DpiAt(cursor) / 96f;
            int width = (int)Math.Ceiling(282 * dpi), height = (int)Math.Ceiling(_layout.Height * dpi);
            // Fit every row, not a scrolling or clipped viewport, on a short work area.
            if (height > work.Height - 8) { dpi = (work.Height - 8) / _layout.Height; width = (int)Math.Ceiling(282 * dpi); height = work.Height - 8; }
            var anchor = IconRectProvider?.Invoke(); int x = (anchor is { } icon ? icon.Left + icon.Width / 2 : cursor.X) - width / 2;
            int y = (anchor is { } icon2 ? icon2.Top : cursor.Y) - height - 8;
            if (y < work.Top + 4) y = (anchor is { } icon3 ? icon3.Bottom : cursor.Y) + 8;
            x = Math.Clamp(x, work.Left + 4, Math.Max(work.Left + 4, work.Right - width - 4)); y = Math.Clamp(y, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - height - 4));
            _shownAt = Environment.TickCount64; _window.Show(new Rectangle(x, y, width, height)); _fit = height / _window.Scale / _layout.Height; _outside.Start();
            StartupLog.Write("[托盘原生菜单] 显示，立即可点击 items=" + items.Count);
            return Task.FromResult(true);
        }
        private void Invoke(int index)
        {
            if (index < 0 || index >= _items.Count) return; var item = _items[index]; if (item.separator || !item.enabled || !TrayMenuModel.IsKnownAction(item.actionId)) return;
            Hide(); StartupLog.Write("[托盘原生菜单] 收到命令 " + item.actionId); Invoked?.Invoke(item.actionId);
        }
        private void Paint(Graphics g, float width, float height)
        {
            // Rendering and hit testing use exactly the same logical geometry.
            float fit = height / _layout.Height; var saved = g.Save(); g.ScaleTransform(fit, fit); width /= fit;
            DesktopPopupDrawing.Fill(g, _background, new RectangleF(0, 0, width, _layout.Height));
            DesktopPopupDrawing.Text(g, "C E L E S T E", 10, _muted, new RectangleF(21, 10, width - 42, 24)); using (var brush = new SolidBrush(_accent)) g.FillEllipse(brush, width - 25, 20, 5, 5);
            for (int i = 0; i < _items.Count; i++)
            {
                var row = _layout.Rows[i];
                var item = _items[i];
                if (row.Separator)
                {
                    DesktopPopupDrawing.Fill(g, _line, new RectangleF(20, row.Top + 7, width - 40, 1));
                    continue;
                }
                if (i == _selected && item.enabled)
                    DesktopPopupDrawing.Fill(g, _hover, new RectangleF(8, row.Top, width - 16, row.Height));
                Color color = item.enabled ? _ink : _muted;
                DesktopPopupDrawing.Icon(g, item.actionId,
                    new RectangleF(21, row.Top + (row.Height - 15) / 2, 15, 15),
                    i == _selected ? _accent : _muted, item.actionId == "togglePlay" && item.label == "暂停");
                DesktopPopupDrawing.Text(g, item.label, 12 * _textScale, color,
                    new RectangleF(48, row.Top, width - 82, row.Height));
                if (item.@checked == true)
                {
                    using var pen = new Pen(_accent, 1.5f);
                    float y = row.Top + row.Height / 2;
                    g.DrawLines(pen, new[] { new PointF(width - 30, y), new PointF(width - 27, y + 3), new PointF(width - 21, y - 4) });
                }
            }
            g.Restore(saved);
        }
        internal void Hide() { if (!IsVisible) return; _outside.Stop(); _window.Hide(); Hidden?.Invoke(); }
        public void Dispose() { _outside.Stop(); _window.Dispose(); }
    }
}
