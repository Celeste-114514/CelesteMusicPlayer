using System;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 正式播放会话（W13 第一刀）。持有引擎，**不持有任何控件**。
    ///
    /// 这一刀只接管「起播前必须下发到引擎的配置」：建引擎、输出模式、设备偏好，
    /// 以及 EQ 曲线 / 声道平衡与限幅 / 房间校正 FIR / ReplayGain。
    /// 起播本身（`PlayFileWithFfmpegAsync`）、无缝接续、失败文案仍留在调用方，后续两刀再搬。
    ///
    /// ⚠ 三条约束：
    /// 1. 别把 `TextBlock` / `Slider` 这类东西传进来 —— 会话返回事实，界面自己决定怎么说。
    /// 2. 每段配置下发都单独 try/catch：一段坏（比如 FIR 文件被删）不能拖垮整首起播。
    /// 3. 引擎是会话的私有财产，外部只读不算删；销毁走 <see cref="ResetEngine"/>。
    /// </summary>
    internal sealed class PlaybackSession(OutputModeService outputMode)
    {
        private AudioPlaybackEngine? _engine;

        /// <summary>引擎实例；首次 <see cref="PrepareAsync"/> 之前是 null（外部只读）。</summary>
        internal AudioPlaybackEngine? Engine => _engine;

        /// <summary>播完（自然结束或无缝切轨失败等）。宿主自己决定是"下一首"还是"停"。</summary>
        internal event Action? Ended;

        /// <summary>无缝接续真的换轨了（此时引擎已经开始放下一首）。</summary>
        internal event Action? SeamlessChanged;

        private AudioPlaybackEngine EnsureEngine()
        {
            if (_engine != null) return _engine;
            _engine = new AudioPlaybackEngine();
            // 事件只在创建时挂一次：以前每次起播都 -= 再 +=，同一路事件能挂多次。
            _engine.PlaybackEnded += () => Ended?.Invoke();
            _engine.SeamlessTrackChanged += () => SeamlessChanged?.Invoke();
            return _engine;
        }

        /// <summary>
        /// 起播前把该下发的配置全部下发。返回给界面渲染用的快照。
        /// 顺序与原来一致：输出模式 → 设备 → EQ → DSP → FIR → ReplayGain。
        /// </summary>
        internal async Task<PlaybackPreparation> PrepareAsync(PlaylistItem item)
        {
            AppSettingsState settings = AppSettingsStore.Load();
            AudioPlaybackEngine engine = EnsureEngine();

            outputMode.Apply(settings);
            engine.SetOutputDevicePreference(string.IsNullOrWhiteSpace(settings.OutputDeviceId) ? null : settings.OutputDeviceId);

            ApplySafely("EQ", () => engine.SetEqCurve(EqCurveStore.Load()));
            ApplySafely("DSP", () =>
            {
                DspExtraState extra = DspExtraStore.Load();
                engine.SetChannelBalance(extra.ChannelBalance);
                engine.SetSafety(extra.Safety);
            });
            ApplySafely("FIR", () => engine.SetRoomCorrection(RoomCorrectionStore.Load()));

            (double TrackGainDb, double AlbumGainDb, double Peak)? replayGain = null;
            bool replayGainApplied = false;
            try
            {
                ReplayGainState state = ReplayGainStore.Load();
                replayGain = await Task.Run(() => ReplayGainReader.ReadForAudio(item.FilePath)).ConfigureAwait(true);
                if (replayGain.HasValue)
                    engine.SetReplayGain(state, replayGain.Value.TrackGainDb, replayGain.Value.AlbumGainDb, replayGain.Value.Peak);
                else
                    engine.SetReplayGain(state, 0, 0, 1.0);   // 没有标签就是 0dB，别沿用上一首的增益
                replayGainApplied = true;
            }
            catch (Exception caught) { StartupLog.WriteException("PlaybackSession.cs", caught); }

            return new PlaybackPreparation(settings, replayGain, replayGainApplied);
        }

        /// <summary>销毁引擎。调用方必须先把播放进度落盘 —— 引擎一没，位置就读不出来了。</summary>
        internal void ResetEngine()
        {
            try { _engine?.Dispose(); }
            finally { _engine = null; }
        }

        private static void ApplySafely(string what, Action action)
        {
            try { action(); }
            catch (Exception caught) { StartupLog.WriteException("PlaybackSession." + what, caught); }
        }
    }

    /// <summary>起播前下发的配置结果。界面拿它渲染读数，不再自己回头读设置。</summary>
    internal sealed record PlaybackPreparation(
        AppSettingsState Settings,
        (double TrackGainDb, double AlbumGainDb, double Peak)? ReplayGain,
        bool ReplayGainApplied);
}
