using System;
using System.Collections.ObjectModel;

namespace CelesteMusicPlayer;

internal static class WebQueueRules
{
    internal static int Move<T>(ObservableCollection<T> queue, int from, int to, int playingIndex)
    {
        if (from < 0 || from >= queue.Count || to < 0 || to >= queue.Count)
            throw new ArgumentOutOfRangeException(nameof(from));
        if (from == to) return playingIndex;
        queue.Move(from, to);
        return playingIndex == from ? to
            : from < playingIndex && to >= playingIndex ? playingIndex - 1
            : from > playingIndex && to <= playingIndex ? playingIndex + 1 : playingIndex;
    }
}
