using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace CelesteMusicPlayer;

// All commands and snapshots enter on the window dispatcher. Only decoding awaits off-thread.
internal sealed class QuickPlaybackSession : IDisposable
{
    internal readonly AudioPlaybackEngine Engine;
    internal readonly List<PlaylistItem> Queue = new();
    private readonly Action<Action> _dispatch;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private CancellationTokenSource? _pending;
    private int _generation; private bool _disposed;
    internal int Index { get; private set; } = -1;
    private bool _stopped = true;
    internal double Volume { get; private set; }
    internal void SetVolume(double value) { Volume = Math.Clamp(value, 0, 100); Engine.SetVolume(Volume / 100); Changed?.Invoke(); }
    internal bool Busy { get; private set; }
    internal string Error { get; private set; } = "";
    internal string Order { get; set; } = "list";
    internal PlaylistItem? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;
    internal event Action? TrackChanged;
    internal event Action? Changed;
    internal Action? BeforePlay;
    internal QuickPlaybackSession(Action<Action> dispatch, AudioPlaybackEngine? engine = null)
    {
        _dispatch = dispatch; Engine = engine ?? new AudioPlaybackEngine(new FfmpegDecoderBackend(), () =>
        {
            var b = new HiFiOutputBackend(); var settings = AppSettingsStore.Load();
            b.SoftwareVolumeEnabled = settings.HiFiSoftwareVolume; b.SetVolume((float)(Volume / 100));
            b.SetEqualizer(EqualizerStore.Load().BandGains); b.SetEqCurve(EqCurveStore.Load());
            var extra = DspExtraStore.Load(); b.SetChannelBalance(extra.ChannelBalance); b.SetSafety(extra.Safety);
            b.SetRoomCorrection(RoomCorrectionStore.Load()); return b;
        });
        var s = AppSettingsStore.Load();
        Engine.SetOutputMode(s.OutputMode == "Asio" ? HiFiOutputBackend.OutputMode.Asio : s.OutputMode == "WasapiExclusive" ? HiFiOutputBackend.OutputMode.WasapiExclusive : HiFiOutputBackend.OutputMode.WasapiShared);
        Engine.SetOutputDevicePreference(string.IsNullOrWhiteSpace(s.OutputDeviceId) ? null : s.OutputDeviceId);
        Engine.SoftwareVolumeEnabled = s.HiFiSoftwareVolume;
        Volume = s.Volume; Engine.SetVolume(s.Volume / 100.0);
        Engine.SetEqualizer(EqualizerStore.Load().BandGains); Engine.SetEqCurve(EqCurveStore.Load());
        var extra = DspExtraStore.Load(); Engine.SetChannelBalance(extra.ChannelBalance); Engine.SetSafety(extra.Safety);
        Engine.SetRoomCorrection(RoomCorrectionStore.Load());
        Engine.PlaybackEnded += Ended; Engine.Failed += Failed;
    }
    internal async Task AddAsync(string[] paths, bool replace, bool play)
    {
        await _loadGate.WaitAsync();
        int first;
        try {
        var items = await Task.Run(() => QuickTrackLoader.Load(paths));
        if (_disposed) return;
        if (items.Count == 0) throw new ArgumentException("没有找到可播放的音频或有效 CUE 分轨。");
        if (replace) { Stop(); Queue.Clear(); Index = -1; }
        first = Queue.Count;
        foreach (var item in items) if (!Queue.Any(q => q.PersistenceKey.Equals(item.PersistenceKey, StringComparison.OrdinalIgnoreCase))) Queue.Add(item);
        if (first == Queue.Count) first = Queue.FindIndex(q => q.PersistenceKey == items[0].PersistenceKey);
        Changed?.Invoke();
        } finally { _loadGate.Release(); }
        if (play || Index < 0) await PlayAsync(first);
    }
    internal async Task PlayAsync(int index)
    {
        if (_disposed || index < 0 || index >= Queue.Count) return;
        _pending?.Cancel(); var cancellation = new CancellationTokenSource(); _pending = cancellation;
        int ticket = ++_generation; Busy = true; Error = ""; Changed?.Invoke();
        await _playGate.WaitAsync();
        try
        {
            if (_disposed || ticket != _generation) return;
            BeforePlay?.Invoke(); Engine.Stop();
            var item = Queue[index]; Index = index; TrackChanged?.Invoke();
            bool ok = await Engine.PlayFileWithFfmpegAsync(item.FilePath, startSeconds: item.StartTimeSeconds, lengthSeconds: item.IsCueSegment ? item.Duration.TotalSeconds : 0, cancellationToken: cancellation.Token);
            if (_disposed || ticket != _generation) { Engine.Stop(); return; }
            if (ok) { _stopped = false; Engine.SetSourceDuration(item.Duration); } else Error = Engine.LastError ?? "无法播放，请检查输出设备或文件。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; StartupLog.WriteException("QuickSession.Play", ex); }
        finally { if (ticket == _generation) Busy = false; _playGate.Release(); if (ReferenceEquals(_pending, cancellation)) _pending = null; cancellation.Dispose(); if (!_disposed) Changed?.Invoke(); }
    }
    internal void Stop() { _stopped = true; _pending?.Cancel(); ++_generation; Busy = false; Engine.Stop(); Changed?.Invoke(); }
    internal void Pause() { if (Busy) _stopped = true; _pending?.Cancel(); ++_generation; Busy = false; Engine.Pause(); Changed?.Invoke(); }
    internal async Task ToggleAsync()
    {
        if (Busy) { Stop(); return; }
        if (Engine.IsPlaying) Pause();
        else if (_stopped || Index < 0 || Engine.Duration <= TimeSpan.Zero) await PlayAsync(Math.Max(0, Index));
        else { BeforePlay?.Invoke(); Engine.Resume(); Changed?.Invoke(); }
    }
    internal Task StepAsync(int step)
    {
        if (Queue.Count == 0) return Task.CompletedTask;
        int next = Order == "shuffle" && step > 0 ? Random.Shared.Next(Queue.Count) : (Index + step + Queue.Count) % Queue.Count;
        return PlayAsync(next);
    }
    private void Ended() { int generation = _generation; _dispatch(async () =>
    {
        if (_disposed || Busy || generation != _generation) return;
        if (Order == "single") { await PlayAsync(Index); return; }
        if (Order == "sequential" && Index >= Queue.Count - 1) { Stop(); return; }
        await StepAsync(1);
    }); }
    private void Failed(Exception e) => _dispatch(() => { if (!_disposed) { Error = e.Message; Busy = false; Changed?.Invoke(); } });
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _pending?.Cancel(); ++_generation;
        Engine.PlaybackEnded -= Ended; Engine.Failed -= Failed;
        Engine.Stop(); Engine.Dispose();
    }
}
