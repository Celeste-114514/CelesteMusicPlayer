using System;
using System.Collections.Generic;

namespace CelesteMusicPlayer;

/// <summary>EQ editing state, independent of windows, controls, storage and audio output.
/// Callers serialize edits on the playback host thread; UI selection/drag state stays in the host.</summary>
internal sealed class EqEditSession
{
    private readonly Func<DateTime> _utcNow;
    private readonly List<EqCurveState> _undo = new();
    private readonly List<EqCurveState> _redo = new();
    private DateTime _undoStamp = DateTime.MinValue;
    private EqCurveState? _reference, _live;
    internal EqEditSession(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);
    internal EqCurveState Current { get; set; } = EqCurveState.Default();
    internal int UndoCount => _undo.Count;
    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal bool AbArmed => _reference != null;
    internal bool ShowingA { get; private set; }
    internal bool Previewing { get; private set; }

    // Preserve the original sliding 400ms coalescing window, including timestamp updates.
    internal bool PushUndo(bool coalesce = false)
    {
        DateTime now = _utcNow();
        if (coalesce && (now - _undoStamp).TotalMilliseconds < 400)
        {
            _undoStamp = now;
            return false;
        }
        _undoStamp = now;
        _undo.Add(Current.Clone());
        if (_undo.Count > 40) _undo.RemoveAt(0);
        _redo.Clear();
        return true;
    }

    internal bool Undo()
    {
        if (!CanUndo) return false;
        var target = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(Current.Clone());
        Previewing = false;
        _live = null;
        Current = target;
        return true;
    }

    internal bool Redo()
    {
        if (!CanRedo) return false;
        var target = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(Current.Clone());
        Previewing = false;
        _live = null;
        Current = target;
        return true;
    }

    internal EqAbTransition ToggleAb(bool cancel)
    {
        if (AbArmed && cancel)
        {
            _reference = null;
            _live = null;
            ShowingA = false;
            Previewing = false;
            return EqAbTransition.Cancelled;
        }
        if (!AbArmed)
        {
            _reference = Current.Clone();
            return EqAbTransition.Armed;
        }
        ShowingA = !ShowingA;
        if (ShowingA)
        {
            Previewing = true;
            _live = Current.Clone();
            Current = _reference!.Clone();
            return EqAbTransition.ShowingA;
        }
        Previewing = false;
        _reference = Current.Clone();
        Current = _live ?? Current.Clone();
        _live = null;
        return EqAbTransition.ShowingB;
    }
}
internal enum EqAbTransition { Armed, Cancelled, ShowingA, ShowingB }
