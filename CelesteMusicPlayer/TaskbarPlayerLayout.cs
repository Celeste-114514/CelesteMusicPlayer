using System;

namespace CelesteMusicPlayer
{
    internal static class TaskbarPlayerLayout
    {
        // All inputs are physical pixels except scale. No desktop/work-area offset.
        internal static bool TryPlace(int left, int right, double scale, bool leftAligned,
            int notifyLeft, int reservedLeft, out int x, out int width)
        {
            x = 0; width = 0;
            if (!double.IsFinite(scale) || scale <= 0 || right <= left) return false;
            width = (int)Math.Round(360 * scale);
            int gap = Math.Max(1, (int)Math.Round(8 * scale));
            x = leftAligned ? notifyLeft - width - gap : Math.Max(left + gap, reservedLeft + gap);
            int end = leftAligned ? notifyLeft - gap : left + (right - left) / 2 - (int)(100 * scale);
            return x >= left + gap && x + width <= end && end <= right - gap;
        }
    }
}
