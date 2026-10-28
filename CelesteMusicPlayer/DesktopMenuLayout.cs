using System;
using System.Collections.Generic;
namespace CelesteMusicPlayer
{
    internal sealed class DesktopMenuLayout
    {
        internal readonly List<(float Top, float Height, bool Separator)> Rows = new();
        internal float Height { get; }
        internal DesktopMenuLayout(IEnumerable<bool> separators, float textScale)
        { float y = 42; foreach (bool separator in separators) { float height = separator ? 15 : 34 * Math.Max(1, textScale); Rows.Add((y, height, separator)); y += height; } Height = y + 8; }
        internal int Hit(float x, float y, float width = 282)
        { if (x < 8 || x >= width - 8) return -1; for (int i = 0; i < Rows.Count; i++) { var r = Rows[i]; if (!r.Separator && y >= r.Top && y < r.Top + r.Height) return i; } return -1; }
    }
}
