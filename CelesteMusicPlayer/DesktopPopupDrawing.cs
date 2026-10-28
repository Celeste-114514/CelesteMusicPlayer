using System;
using System.Drawing;
using System.Drawing.Drawing2D;
namespace CelesteMusicPlayer
{
    internal static class DesktopPopupDrawing
    {
        internal static void Text(Graphics g, string text, float size, Color color, RectangleF rect, bool bold = false)
        { using var font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); using var brush = new SolidBrush(color); using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center }; g.DrawString(text, font, brush, rect, format); }
        internal static void Fill(Graphics g, Color color, RectangleF rect) { using var brush = new SolidBrush(color); g.FillRectangle(brush, rect); }
        internal static void Icon(Graphics g, string action, RectangleF rect, Color color, bool playing = false)
        {
            var saved = g.Save(); g.TranslateTransform(rect.X, rect.Y); g.ScaleTransform(rect.Width / 20, rect.Height / 20); using var p = new Pen(color, 1.45f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            switch (action)
            {
                case "previous": g.DrawLine(p, 5, 5, 5, 15); g.DrawPolygon(p, new[] { new Point(15, 5), new Point(7, 10), new Point(15, 15) }); break;
                case "next": g.DrawLine(p, 15, 5, 15, 15); g.DrawPolygon(p, new[] { new Point(5, 5), new Point(13, 10), new Point(5, 15) }); break;
                case "togglePlay": if (playing) { g.DrawLine(p, 7, 5, 7, 15); g.DrawLine(p, 13, 5, 13, 15); } else g.DrawPolygon(p, new[] { new Point(7, 4), new Point(16, 10), new Point(7, 16) }); break;
                case "exit": g.DrawLines(p, new[] { new Point(9, 3), new Point(4, 3), new Point(4, 17), new Point(9, 17) }); g.DrawLine(p, 9, 10, 17, 10); g.DrawLines(p, new[] { new Point(14, 7), new Point(17, 10), new Point(14, 13) }); break;
                case "favorite": using (var path = new GraphicsPath()) { path.AddBezier(10, 16, 0, 11, 2, 1, 10, 6); path.AddBezier(10, 6, 18, 1, 20, 11, 10, 16); g.DrawPath(p, path); } break;
                case "openLogs": g.DrawRectangle(p, 3, 6, 14, 10); g.DrawLines(p, new[] { new Point(3, 6), new Point(3, 4), new Point(8, 4), new Point(10, 6) }); break;
                case "toggleLyrics": g.DrawLine(p, 4, 5, 16, 5); g.DrawLine(p, 4, 10, 12, 10); g.DrawLine(p, 4, 15, 14, 15); break;
                case "openSettings": g.DrawLine(p, 4, 5, 16, 5); g.DrawLine(p, 4, 10, 16, 10); g.DrawLine(p, 4, 15, 16, 15); g.DrawLine(p, 7, 3, 7, 7); g.DrawLine(p, 13, 8, 13, 12); g.DrawLine(p, 8, 13, 8, 17); break;
                default: g.DrawRectangle(p, 3, 4, 14, 12); g.DrawLine(p, 3, 8, 17, 8); break;
            }
            g.Restore(saved);
        }
    }
}
