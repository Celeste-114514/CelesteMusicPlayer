using System;

namespace CelesteMusicPlayer
{
    public sealed class AbRepeatState
    {
        public TimeSpan? PointA { get; private set; }

        public TimeSpan? PointB { get; private set; }

        public bool IsActive => PointA.HasValue && PointB.HasValue && PointB > PointA;

        public void SetA(TimeSpan position)
        {
            PointA = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            if (PointB.HasValue && PointB <= PointA)
            {
                PointB = null;
            }
        }

        public void SetB(TimeSpan position)
        {
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            if (PointA.HasValue && position <= PointA)
            {
                return;
            }

            PointB = position;
        }

        public void Clear()
        {
            PointA = null;
            PointB = null;
        }

        public TimeSpan ClampPosition(TimeSpan position)
        {
            if (!IsActive)
            {
                return position;
            }

            if (position < PointA)
            {
                return PointA!.Value;
            }

            if (position > PointB)
            {
                return PointB!.Value;
            }

            return position;
        }

        public bool ShouldLoopToA(TimeSpan position)
        {
            return IsActive && position >= PointB;
        }
    }
}
