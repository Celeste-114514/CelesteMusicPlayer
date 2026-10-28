using System;

namespace CelesteMusicPlayer
{
    // Host-independent command semantics. The legacy session supplies actions;
    // a future host can supply a different session without changing callers.
    internal sealed class PlaybackCommandRouter
    {
        private readonly Func<bool> _isPlaying;
        private readonly Action _previous, _toggle, _next;
        internal PlaybackCommandRouter(Func<bool> isPlaying, Action previous, Action toggle, Action next)
        { _isPlaying = isPlaying; _previous = previous; _toggle = toggle; _next = next; }

        internal bool TryExecute(string? command)
        {
            switch (command)
            {
                case "previous": _previous(); break;
                case "next": _next(); break;
                case "togglePlay": _toggle(); break;
                case "play": if (!_isPlaying()) _toggle(); break;
                case "pause": if (_isPlaying()) _toggle(); break;
                default: return false;
            }
            return true;
        }
    }
}
