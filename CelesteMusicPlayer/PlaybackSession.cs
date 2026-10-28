using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace CelesteMusicPlayer;
/// <summary>Host-neutral session state and queue commands for the W7 host. Owns no controls or windows.</summary>
internal sealed class PlaybackSession : IAsyncDisposable
{
    private readonly AudioPlaybackEngine _engine;
    private readonly SemaphoreSlim _commands=new(1,1);
    private readonly object _stateGate=new();
    private List<PlaylistItem> _queue=new();
    private int _index=-1,_request;
    private bool _disposed,_starting;
    private string _error="";
    private double _volume=100;
    internal PlaybackSession(AudioPlaybackEngine engine)
    {
        _engine=engine;_engine.PositionChanged+=OnPosition;_engine.PlaybackEnded+=OnEnded;_engine.Failed+=OnFailed;
    }
    internal event Action<PlaybackSnapshot>? Changed;
    internal void SetQueue(IEnumerable<PlaylistItem> items)
    {
        lock(_stateGate){if(_disposed)throw new ObjectDisposedException(nameof(PlaybackSession));if(_starting||_engine.IsPlaying)throw new InvalidOperationException("请先停止播放再替换队列");_queue=items.Select(x=>x.Clone()).ToList();_index=-1;}
    }
    internal PlaybackSnapshot Capture()
    {
        lock(_stateGate){var item=_index>=0&&_index<_queue.Count?_queue[_index]:null;return new PlaybackSnapshot{
            trackId=item?.PersistenceKey??"",title=item?.DisplayTitle??"未在播放",artist=item?.Artist??"",album=item?.Album??"",
            duration=_engine.Duration.TotalSeconds,position=_engine.Position.TotalSeconds,playing=_engine.IsPlaying,volume=_volume,
            playbackOrder="Sequential",busy=_starting,error=_error,volumeEnabled=true,source=item?.IsRemote==true?"NETWORK":"LOCAL"
        };}
    }
    internal async Task<bool> PlayAtAsync(int index)
    {
        int ticket=Interlocked.Increment(ref _request);await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            PlaylistItem item;
            lock(_stateGate){if(_disposed||ticket!=_request||index<0||index>=_queue.Count)return false;item=_queue[index];_starting=true;_error="";}
            Publish();bool ok=await _engine.PlayFileWithFfmpegAsync(item.FilePath,startSeconds:item.StartTimeSeconds,lengthSeconds:item.IsCueSegment?item.Duration.TotalSeconds:0).ConfigureAwait(false);
            lock(_stateGate){_starting=false;if(_disposed||ticket!=_request){_engine.Stop();return false;}if(ok)_index=index;else _error=_engine.LastError??"无法播放";}
            Publish();return ok;
        }
        finally{_commands.Release();}
    }
    internal Task<bool> NextAsync(){lock(_stateGate)return PlayAtAsync(_index+1);}
    internal Task<bool> PreviousAsync(){lock(_stateGate)return PlayAtAsync(Math.Max(0,_index-1));}
    internal async Task ExecuteAsync(string command,double value=0)
    {
        if(command=="next"){await NextAsync().ConfigureAwait(false);return;}
        if(command=="previous"){await PreviousAsync().ConfigureAwait(false);return;}
        if(command is "play" or "togglePlay" && _index<0){await PlayAtAsync(0).ConfigureAwait(false);return;}
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if(_disposed)return;
            switch(command)
            {
                case "play":if(!_engine.IsPlaying)_engine.Resume();break;
                case "pause":_engine.Pause();break;
                case "togglePlay":if(_engine.IsPlaying)_engine.Pause();else _engine.Resume();break;
                case "stop":Interlocked.Increment(ref _request);_engine.Stop();break;
                case "seek":if(double.IsFinite(value))_engine.Seek(TimeSpan.FromSeconds(Math.Clamp(value,0,_engine.Duration.TotalSeconds)));break;
                case "volume":if(double.IsFinite(value)){_volume=Math.Clamp(value,0,100);_engine.SetVolume(_volume/100);}break;
                default:throw new ArgumentException("未知播放命令",nameof(command));
            }
            Publish();
        }
        finally{_commands.Release();}
    }
    private void OnPosition(TimeSpan position){if(!_starting&&!_disposed)Publish();}
    private void OnFailed(Exception ex){_error=ex.Message;if(!_disposed)Publish();}
    private async void OnEnded(){try{if(!_disposed&&!_starting)await NextAsync().ConfigureAwait(false);}catch(Exception ex){OnFailed(ex);}}
    private void Publish(){if(!_disposed)Changed?.Invoke(Capture());}
    public async ValueTask DisposeAsync()
    {
        lock(_stateGate){if(_disposed)return;_disposed=true;Interlocked.Increment(ref _request);}
        await _commands.WaitAsync().ConfigureAwait(false);
        try{_engine.PositionChanged-=OnPosition;_engine.PlaybackEnded-=OnEnded;_engine.Failed-=OnFailed;_engine.Dispose();}
        finally{_commands.Release();}
    }
}
