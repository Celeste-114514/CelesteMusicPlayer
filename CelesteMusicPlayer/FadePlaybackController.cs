using System;
using Microsoft.UI.Dispatching;
using Windows.Media.Playback;

namespace CelesteMusicPlayer
{
    /// <summary>在指定毫秒内平滑改变 MediaPlayer.Volume（0–1）。</summary>
    public sealed class FadePlaybackController
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private DispatcherQueueTimer? _timer;
        private MediaPlayer? _fadePlayer;
        private double _startVolume;
        private double _targetVolume;
        private int _durationMs;
        private DateTime _startedUtc;
        private Action? _onComplete;

        public FadePlaybackController(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        }

        public bool IsFading { get; private set; }

        public void Cancel()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= Timer_Tick;
                _timer = null;
            }

            IsFading = false;
            _onComplete = null;
            _fadePlayer = null;
        }

        public void FadeOutThen(MediaPlayer player, int durationMs, Action onComplete)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            Cancel();
            StartFade(player, player.Volume, 0, durationMs, () =>
            {
                onComplete?.Invoke();
            });
        }

        public void FadeIn(MediaPlayer player, double targetVolume, int durationMs)
        {
            if (player == null)
            {
                throw new ArgumentNullException(nameof(player));
            }

            Cancel();
            targetVolume = Math.Clamp(targetVolume, 0, 1);
            StartFade(player, player.Volume, targetVolume, durationMs, null);
        }

        private void StartFade(MediaPlayer player, double fromVolume, double toVolume, int durationMs, Action? onComplete)
        {
            if (durationMs <= 0)
            {
                player.Volume = Math.Clamp(toVolume, 0, 1);
                onComplete?.Invoke();
                return;
            }

            _startVolume = Math.Clamp(fromVolume, 0, 1);
            _targetVolume = Math.Clamp(toVolume, 0, 1);
            _durationMs = durationMs;
            _startedUtc = DateTime.UtcNow;
            _onComplete = onComplete;
            IsFading = true;

            _fadePlayer = player;
            _timer = _dispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(DispatcherQueueTimer sender, object args)
        {
            MediaPlayer? player = _fadePlayer;
            if (player == null)
            {
                Cancel();
                return;
            }

            double elapsedMs = (DateTime.UtcNow - _startedUtc).TotalMilliseconds;
            double t = Math.Clamp(elapsedMs / _durationMs, 0, 1);
            player.Volume = _startVolume + (_targetVolume - _startVolume) * t;

            if (t >= 1)
            {
                player.Volume = _targetVolume;
                Action? complete = _onComplete;
                Cancel();
                complete?.Invoke();
            }
        }
    }
}
