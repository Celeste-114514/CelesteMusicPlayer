using System;
using System.Threading;
namespace CelesteMusicPlayer;
internal interface IPlaybackPulse : IDisposable { void Start(); void Stop(); }
internal static class PlaybackPulseFactory
{
    internal static Func<Action,IPlaybackPulse>? Factory;
    internal static IPlaybackPulse Create(Action tick) => Factory?.Invoke(tick) ?? new ContextPlaybackPulse(tick);
}
/// <summary>A headless or host-context clock. No window/XAML dispatch dependency.</summary>
internal sealed class ContextPlaybackPulse(Action tick) : IPlaybackPulse
{
    private readonly SynchronizationContext? _context=SynchronizationContext.Current;
    private Timer? _timer;
    private int _generation, _pending;
    public void Start(){Stop();int version=Volatile.Read(ref _generation);_timer=new Timer(_=>{
        if(Interlocked.Exchange(ref _pending,1)!=0)return;
        void Deliver(){try{if(Volatile.Read(ref _generation)==version)tick();}finally{Volatile.Write(ref _pending,0);}}
        if(_context==null)Deliver();else _context.Post(_=>Deliver(),null);
    },null,200,200);}
    public void Stop(){Interlocked.Increment(ref _generation);Interlocked.Exchange(ref _timer,null)?.Dispose();}
    public void Dispose()=>Stop();
}
