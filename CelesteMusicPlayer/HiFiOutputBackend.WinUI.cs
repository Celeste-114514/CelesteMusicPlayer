using System;
using Microsoft.UI.Dispatching;
namespace CelesteMusicPlayer;
public sealed partial class HiFiOutputBackend
{
    private static DispatcherQueue? _uiQueue;
    /// <summary>Legacy view adapter only. The core backend can run without this.</summary>
    public static DispatcherQueue? UiDispatcherQueue
    {
        get=>_uiQueue;
        set{_uiQueue=value;PlaybackPulseFactory.Factory=value==null?null:tick=>new WinUiPlaybackPulse(value,tick);}
    }
    private sealed class WinUiPlaybackPulse : IPlaybackPulse
    {
        private readonly DispatcherQueueTimer _timer;
        public WinUiPlaybackPulse(DispatcherQueue queue,Action tick){_timer=queue.CreateTimer();_timer.Interval=TimeSpan.FromMilliseconds(200);_timer.Tick+=(_,_)=>tick();}
        public void Start()=>_timer.Start();public void Stop()=>_timer.Stop();public void Dispose()=>_timer.Stop();
    }
}
