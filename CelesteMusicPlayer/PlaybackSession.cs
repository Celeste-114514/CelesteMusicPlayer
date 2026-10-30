using System;
using System.Threading;
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

        // ---- 权威当前曲目 ----
        /// <summary>
        /// 「当前在播哪首」的唯一权威答案：StartPlayback 钉下，无缝换轨时由会话自己推进。
        /// 队列下标和曲库下标是两套独立事实（自动续播走队列、手动点歌可能走曲库），
        /// 谁"看起来有效"谁就赢会让网页整片停在上一首 —— 手动切歌正常、自动切歌卡住就是这个原因。
        /// </summary>
        internal PlaylistItem? CurrentItem { get; private set; }

        /// <summary>StartPlayback 起播前钉下意图；停止/清空传 null。</summary>
        internal void SetCurrentItem(PlaylistItem? item) => CurrentItem = item;

        /// <summary>播完（自然结束或无缝切轨失败等）。宿主自己决定是"下一首"还是"停"。</summary>
        internal event Action? Ended;

        /// <summary>
        /// 无缝接续真的换轨了（引擎已开始放下一首）。载荷是换到的那首；
        /// null = 引擎换了轨但会话没有预载记录可对账（引擎不暴露正在播的文件）。
        /// </summary>
        internal event Action<PlaylistItem?>? SeamlessAdvanced;

        /// <summary>
        /// 宿主注入："current 之后的一首是谁"。队列/曲库/播放顺序（含随机）的规则住在宿主，
        /// 会话只管预载与记账 —— 别把队列对象整个塞进来。
        /// </summary>
        internal Func<PlaylistItem?, PlaylistItem?>? NextResolver { get; set; }

        // ---- 无缝预载状态机（原 MainWindow 散字段，2026-10-30 第三刀归位）----
        private PlaylistItem? _preloadedItem;
        private long _lastPreloadTick;
        private string? _lastPreloadPath;
        private IDisposable? _preloadLease;

        private AudioPlaybackEngine EnsureEngine()
        {
            if (_engine != null) return _engine;
            _engine = new AudioPlaybackEngine();
            // 事件只在创建时挂一次：以前每次起播都 -= 再 +=，同一路事件能挂多次。
            // 这两行带日志是故意的：引擎事件是否真的到达会话，只能靠日志证，猜不出来。
            _engine.PlaybackEnded += () =>
            {
                StartupLog.Write("[会话] 引擎 PlaybackEnded 事件");
                Ended?.Invoke();
            };
            _engine.SeamlessTrackChanged += () =>
            {
                StartupLog.Write("[会话] 引擎 SeamlessTrackChanged 事件");
                // 会话自己记账：预载记录消费掉、权威当前曲目推进到新轨，
                // 再把"换到哪首"交给界面 —— 界面不必回头翻任何内部状态。
                PlaylistItem? next = _preloadedItem;
                _preloadedItem = null;
                if (next != null) CurrentItem = next;
                SeamlessAdvanced?.Invoke(next);
            };
            StartupLog.Write("[会话] 引擎已创建并挂好事件（实例=" + _engine.GetHashCode() + "）");
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

        /// <summary>
        /// 起播：把"能不能放、放的是什么"说清楚，不负责说给用户听。
        /// CUE 分轨在引擎眼里就是一整条流（[起点, 起点+时长] 区间转码），进度/seek/SMTC 天然按轨口径。
        /// </summary>
        /// <param name="progress">转码进度文案。会话只回调字符串，怎么写由界面决定。</param>
        internal async Task<PlaybackStartResult> StartAsync(PlaylistItem item, Action<string>? progress = null)
        {
            AudioPlaybackEngine engine = EnsureEngine();
            CurrentItem = item;   // 起播成功与否先按意图钉住；失败分支由调用方按结果收拾界面
            double cueStart = item.StartTimeSeconds > 0 ? item.StartTimeSeconds : 0;
            double cueLength = cueStart > 0 ? Math.Max(0, item.Duration.TotalSeconds) : 0;
            bool ok = await engine.PlayFileWithFfmpegAsync(item.FilePath, s => progress?.Invoke(s), cueStart, cueLength).ConfigureAwait(true);
            if (!ok)
            {
                string? reason = engine.LastError;
                return new PlaybackStartResult(false, reason, PlaybackKind.Pcm, "", false, TimeSpan.Zero,
                    reason != null && reason.Contains("未找到内置 ffmpeg.exe"));
            }

            bool direct = engine.IsDsdDirectOut;
            return new PlaybackStartResult(
                Ok: true,
                FailureReason: null,
                Kind: PlaybackKindRule.Of(direct, AudioPlaybackEngine.IsDsdFile(item.FilePath)),
                PcmDescription: PlaybackKindRule.PcmDescription(engine.SourceFormatDescription, engine.ActualOutputFormat),
                TempoActive: FfmpegDecoderBackend.IsTempoActive(FfmpegDecoderBackend.GetTempoRate()),
                Duration: engine.Duration,
                NeedFfmpegHint: false);
        }

        /// <summary>销毁引擎。调用方必须先把播放进度落盘 —— 引擎一没，位置就读不出来了。</summary>
        internal void ResetEngine()
        {
            try { _engine?.Dispose(); }
            finally { _engine = null; }
        }

        /// <summary>
        /// 引擎开播后预加载下一首到无缝源（共享/ASIO、顺序播放）。同格式可无缝续接，否则由上层重建。
        /// 从 MainWindow 搬来（2026-10-30 第三刀）：节流、跳过规则、网络解析、租约、落账全在这里，
        /// 宿主只提供"下一首是谁"。**前提：先有起播（引擎非空、CurrentItem 非空），否则直接返回。**
        /// </summary>
        internal async Task PreloadNextAsync()
        {
            AudioPlaybackEngine? engine = _engine;
            PlaylistItem? current = CurrentItem;
            if (engine == null || current == null) return;

            // 节流（2026-09-21）：预加载 = spawn 一次 ffmpeg 整轨转码 + 把 ≤64MB 的 WAV 整读进内存（大对象堆）。
            // 调用点有 4 个（开播、暂停恢复、seek、无缝切歌后），拖一次进度条就触发一轮，
            // 连着拖就是连着转码 + 连着大对象分配 → GC 一暂停，独占渲染线程就断音。
            // 规则：同一首 5 秒内不重复预载；任意两次预载间隔 <800ms 直接跳过（专治拖动风暴）。
            long preloadNow = Environment.TickCount64;
            bool sameTarget = !string.IsNullOrWhiteSpace(_lastPreloadPath)
                && string.Equals(_lastPreloadPath, current.FilePath, StringComparison.OrdinalIgnoreCase);
            if (preloadNow - _lastPreloadTick < 800 || (sameTarget && preloadNow - _lastPreloadTick < 5000))
            {
                StartupLog.Write("预加载: 节流跳过 (<800ms 或同一首<5s) path=" + System.IO.Path.GetFileName(current.FilePath ?? ""));
                return;
            }
            _lastPreloadTick = preloadNow;
            _lastPreloadPath = current.FilePath;

            // DSD 播完自动切歌优先：DSD 源不参与无缝预加载（走原 Stop→PlayNext，避免无缝与 DSD 时长修正冲突）
            string curExt = System.IO.Path.GetExtension(current.FilePath).ToLowerInvariant();
            if (curExt is ".dsf" or ".dff")
            {
                _preloadedItem = null;
                return;
            }

            // 变速激活时不参与无缝预加载（2026-10-15）：SpeedStretch 的 FIFO 里往往还压着
            // 上一曲尾部，无缝续接会把两曲的信号搅在同一段分析窗里（拖腔/回声）。
            // AB 循环同理（2026-10-16）：循环里压根不该切下一首，预载了反而会在 B 点前
            // 被续接逻辑抢走。倍率回 1.0 / 关掉循环后预载自动恢复，gapless 不受影响。
            if (engine.SpeedActive || engine.IsAbLoopActive)
            {
                _preloadedItem = null;
                StartupLog.Write("预加载: 变速/AB循环中，跳过无缝预载（避免跨曲搅混/抢切）");
                return;
            }

            try
            {
                PlaylistItem? next = NextResolver?.Invoke(current);
                _preloadLease?.Dispose(); _preloadLease = null;
                if (next?.SourceId.Length > 0 && NetworkLibraryStore.Track(next.SourceTrackId) is NetworkTrack nextTrack)
                {
                    _preloadLease = NetworkLibraryService.Lease(NetworkLibraryService.CachedPath(nextTrack));
                    next.FilePath = await NetworkLibraryService.ResolveAsync(nextTrack, CancellationToken.None).ConfigureAwait(true);
                    if (!StillOn(current)) return;
                }
                if (next == null || string.IsNullOrWhiteSpace(next.FilePath) || !System.IO.File.Exists(next.FilePath))
                {
                    StartupLog.Write("预加载: 无下一首 or 文件不存在 next=" + (next?.Title ?? "<null>"));
                    _preloadedItem = null;
                    return;
                }

                // CUE 分轨不参与无缝预加载：同文件多轨的"下一首"要靠区间转码重新起播，
                // 无缝续接会从当前位置直接续播整文件（等于跳过轨边界），且无缝换轨事件
                // 不携带分轨信息，UI 会整轨错位。分轨一律走 Stop→PlayNext 常规路径。
                if (current.StartTimeSeconds > 0 || next.StartTimeSeconds > 0)
                {
                    _preloadedItem = null;
                    return;
                }

                string? wav = await engine.EnsureCachedWavAsync(next.FilePath).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(wav))
                {
                    StartupLog.Write("预加载: 转码失败 无WAV next=" + next.Title);
                    _preloadedItem = null;
                    return;
                }

                // ⚠ 预载是异步的：这一路 await（读源/转码）期间用户可能已经切歌。
                // 若此时仍把预载结果记下来，等无缝换轨事件到达时就会拿"上一首的下一首"去更新界面
                // —— 表现就是"实际在放新的那首，界面还停在别的曲目"。所以落记录前必须再确认一次。
                if (!StillOn(current))
                {
                    StartupLog.Write("预加载: 期间已切歌，丢弃这次预载 \"" + next.Title + "\"（当前=" + (CurrentItem?.Title ?? "<无>") + "）");
                    return;
                }

                bool ok = await engine.PrepareNextSeamless(wav).ConfigureAwait(true);
                StartupLog.Write("预加载: \"" + current.Title + "\" → \"" + next.Title + "\" wav=" + System.IO.Path.GetFileName(wav) + " 采纳无缝=" + ok);
                _preloadedItem = ok ? next : null;   // 格式不同等：无缝不启用，后续走重建
            }
            catch (Exception ex)
            {
                StartupLog.Write("预加载: 异常 " + ex.Message);
                _preloadedItem = null;
            }
        }

        /// <summary>预载是异步的：落记录前用它确认"要预载给谁"的那首还在播。按曲目身份比，CUE 分轨也能区分。</summary>
        private bool StillOn(PlaylistItem current)
        {
            PlaylistItem? still = CurrentItem;
            return still != null && WebTrackIdentity.Of(still) == WebTrackIdentity.Of(current);
        }

        /// <summary>退出时放手预载的网络缓存租约。判空调用，不会凭空造会话。</summary>
        internal void ReleasePreloadLease()
        {
            try { _preloadLease?.Dispose(); }
            finally { _preloadLease = null; }
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

    /// <summary>
    /// 起播结果。会话只说事实：成没成、失败原因、是不是 DoP 直出、转了 PCM 没有、时长多少。
    /// **文案怎么写由界面决定** —— 所以这里没有一句中文提示。
    /// </summary>
    internal sealed record PlaybackStartResult(
        bool Ok,
        string? FailureReason,
        PlaybackKind Kind,
        string PcmDescription,
        bool TempoActive,
        TimeSpan Duration,
        bool NeedFfmpegHint);
}
