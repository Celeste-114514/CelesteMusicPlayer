using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 零散播放窗口的播放核心（2026-10-18 自持引擎版）：
    /// 自己的 <see cref="AudioPlaybackEngine"/> 实例 + 自己的纯内存队列。
    ///
    /// 与主程序的关系只有两条：
    /// ① 引擎底层同一套（FFmpeg 解码 + HiFiOutputBackend DSP 链），
    ///    主程序设过的输出模式/设备/EQ/DSP 配置档等由引擎与后端自己从盘上读，自动继承；
    /// ② 各播各的：本窗口的起播/切歌/seek 全部只动自己的引擎和队列，
    ///    主程序的播放队列与「正在播放」状态不受任何影响。
    /// </summary>
    public sealed partial class ScatteredPlayWindow
    {
        private AudioPlaybackEngine? _engine;

        /// <summary>零散队列（纯内存）：不写 SQLite、不入音乐库、不进下次启动的续播。</summary>
        private readonly List<PlaylistItem> _queue = new();

        private int _queueIndex = -1;
        private PlaybackOrder _order = PlaybackOrder.ListLoop;
        private readonly Random _random = new();

        /// <summary>起播令牌：切歌快时旧转码回调不许覆盖新歌状态。</summary>
        private int _playToken;

        /// <summary>转码/起播进行中（引擎门内排队期间也算）。</summary>
        private bool _playInFlight;

        /// <summary>引擎里有没有一个活会话（起播成功 → true；播完/出错/关窗 → false）。</summary>
        private bool _sessionActive;

        private bool _closed;

        private PlaylistItem? CurrentItem =>
            _queueIndex >= 0 && _queueIndex < _queue.Count ? _queue[_queueIndex] : null;

        private TimeSpan CurrentPosition => _engine?.Position ?? TimeSpan.Zero;

        // =====================================================================
        // 引擎生命周期（设置全部从盘上共享 store 读，与主程序口径一致）
        // =====================================================================

        private void EnsureEngine()
        {
            if (_engine != null)
            {
                return;
            }

            try
            {
                _engine = new AudioPlaybackEngine();
                AppSettingsState st = AppSettingsStore.Load();

                HiFiOutputBackend.OutputMode mode =
                    string.Equals(st.OutputMode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase)
                        ? HiFiOutputBackend.OutputMode.WasapiExclusive
                        : string.Equals(st.OutputMode, "Asio", StringComparison.OrdinalIgnoreCase)
                            ? HiFiOutputBackend.OutputMode.Asio
                            : HiFiOutputBackend.OutputMode.WasapiShared;
                _engine.SetOutputMode(mode);
                _engine.SetOutputDevicePreference(
                    string.IsNullOrWhiteSpace(st.OutputDeviceId) ? null : st.OutputDeviceId);
                _engine.SoftwareVolumeEnabled = st.HiFiSoftwareVolume;

                // 音量口径与主程序相同：共享模式恒走数字音量；独占/ASIO 仅「HiFi 软件音量」
                // 开启时才用数字音量，否则设备主音量（DAC 级）接管，保 bit-perfect。
                bool exclusive = mode != HiFiOutputBackend.OutputMode.WasapiShared;
                if (!exclusive || st.HiFiSoftwareVolume)
                {
                    _engine.SetVolume(Math.Clamp(st.Volume, 0, 100) / 100.0);
                }

                // 10 段 EQ：与主程序「均衡器」同一条 equalizer.json
                try
                {
                    _engine.SetEqualizer(EqualizerStore.Load().BandGains);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.EnsureEngine.eq10", caught);
                }

                // 31 段精调 EQ：与主程序「音效处理」同一条 store
                try
                {
                    _engine.SetEqCurve(EqCurveStore.Load());
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.EnsureEngine.eq31", caught);
                }

                // 声道平衡 / 安全限幅 / 房间校正
                try
                {
                    DspExtraState extra = DspExtraStore.Load();
                    _engine.SetChannelBalance(extra.ChannelBalance);
                    _engine.SetSafety(extra.Safety);
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.EnsureEngine.dspExtra", caught);
                }

                try
                {
                    _engine.SetRoomCorrection(RoomCorrectionStore.Load());
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.EnsureEngine.room", caught);
                }

                // 压缩 / crossfeed / 声场 / 矩阵（DSP 配置档）由 HiFiOutputBackend 构造时
                // 自己从 dsp-rack.json 读；交叉淡化 / SRC / DSD 输出模式由引擎起播时自己读设置。

                _engine.PlaybackEnded += OnEnginePlaybackEnded;
                _engine.Failed += OnEngineFailed;

                StartupLog.Write("零散窗口引擎已创建 mode=" + st.OutputMode
                    + " 设备=" + (st.OutputDeviceId ?? "默认"));
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.EnsureEngine", caught);
                _engine = null;
            }
        }

        private void ShutdownEngine()
        {
            _closed = true;
            try
            {
                if (_engine == null)
                {
                    return;
                }

                _engine.PlaybackEnded -= OnEnginePlaybackEnded;
                _engine.Failed -= OnEngineFailed;
                _engine.Stop();
                _engine.Dispose();
                _engine = null;
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.ShutdownEngine", caught);
            }
        }

        // =====================================================================
        // 入队（三个入口共用：双击/转交、窗口内打开文件、拖放）
        // =====================================================================

        /// <summary>
        /// 把一批文件送进零散队列。
        /// playNow = true：整体替换队列并立刻起播第一首（双击文件关联的语义 = 「放这个」）。
        /// playNow = false：追加进队列；当前没在播就起播追加的第一首，在播就只排队不打扰
        /// （窗口内打开文件 / 拖放的语义 = 「加入队列」，与窗口里的提示语一致）。
        /// .cue 按分轨表展开成多首（带区间起止），和其它音频走同一条队列。
        /// </summary>
        internal void EnqueueFiles(IEnumerable<string>? paths, bool playNow = true)
        {
            if (paths == null || _closed)
            {
                return;
            }

            string[] files = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Where(File.Exists)
                .Where(QuickPlayLauncher.IsPlayableFile)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length == 0)
            {
                return;
            }

            try
            {
                List<PlaylistItem> incoming = new();
                foreach (string path in files)
                {
                    if (string.Equals(Path.GetExtension(path), ".cue", StringComparison.OrdinalIgnoreCase))
                    {
                        incoming.AddRange(BuildCueItems(path));
                    }
                    else
                    {
                        incoming.Add(MainWindow.BuildItemFromTag(path));
                    }
                }

                if (incoming.Count == 0)
                {
                    return;
                }

                if (playNow)
                {
                    _queue.Clear();
                    _queue.AddRange(incoming);
                    _queueIndex = 0;
                    PlayQueueAt(0);
                    StartupLog.Write("零散播放入队(替换) " + incoming.Count + " 首，起播: " + incoming[0].FilePath);
                }
                else
                {
                    var existing = new HashSet<string>(
                        _queue.Select(QueueKey), StringComparer.OrdinalIgnoreCase);
                    int firstNew = -1;
                    foreach (PlaylistItem item in incoming)
                    {
                        if (!existing.Add(QueueKey(item)))
                        {
                            continue; // 队列里已有的跳过（CUE 分轨按 路径#起点 区分，不会误伤）
                        }

                        if (firstNew < 0)
                        {
                            firstNew = _queue.Count;
                        }

                        _queue.Add(item);
                    }

                    if (firstNew < 0)
                    {
                        return; // 全是重复
                    }

                    bool idle = _engine == null || (!_sessionActive && !_playInFlight);
                    if (idle)
                    {
                        _queueIndex = firstNew;
                        PlayQueueAt(firstNew);
                    }

                    StartupLog.Write("零散播放追加 " + incoming.Count + " 首（队列共 "
                        + _queue.Count + " 首，idle=" + idle + "）");
                }

                RefreshAll();
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.EnqueueFiles", caught);
            }
        }

        /// <summary>队列去重键：CUE 分轨同一音频文件按「路径#起点」区分。</summary>
        private static string QueueKey(PlaylistItem item)
            => item.FilePath + "#" + item.StartTimeSeconds.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 解析 .cue 分轨表 → 队列条目（带 StartTimeSeconds 与每轨时长）。
        /// 每轨时长 = 同一音频文件内下一轨起点 - 本轨起点；末轨取文件剩余长度。
        /// </summary>
        private static List<PlaylistItem> BuildCueItems(string cuePath)
        {
            List<PlaylistItem> items = new();
            try
            {
                List<CueTrack> tracks = CueSheetParser.LoadCue(cuePath);
                if (tracks.Count == 0)
                {
                    return items;
                }

                var baseItems = new Dictionary<string, PlaylistItem>(StringComparer.OrdinalIgnoreCase);
                foreach (CueTrack track in tracks)
                {
                    if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
                    {
                        continue;
                    }

                    if (!baseItems.TryGetValue(track.FilePath, out PlaylistItem? baseItem))
                    {
                        baseItem = MainWindow.BuildItemFromTag(track.FilePath);
                        baseItems[track.FilePath] = baseItem;
                    }

                    PlaylistItem item = new()
                    {
                        FilePath = baseItem.FilePath,
                        Title = baseItem.Title,
                        Artist = baseItem.Artist,
                        AlbumArtist = baseItem.AlbumArtist,
                        Album = baseItem.Album,
                        Genre = baseItem.Genre,
                        Track = baseItem.Track,
                        Disc = baseItem.Disc,
                        Year = baseItem.Year,
                        Duration = baseItem.Duration,
                        StartTimeSeconds = track.StartTime.TotalSeconds
                    };

                    if (!string.IsNullOrWhiteSpace(track.Title))
                    {
                        item.Title = track.Title;
                    }

                    if (!string.IsNullOrWhiteSpace(track.Artist))
                    {
                        item.Artist = track.Artist;
                        item.AlbumArtist = track.Artist;
                    }

                    items.Add(item);
                }

                // 每轨时长：同文件内按起点排序，末轨吃到文件尾
                for (int i = 0; i < items.Count; i++)
                {
                    PlaylistItem current = items[i];
                    if (current.StartTimeSeconds <= 0)
                    {
                        continue;
                    }

                    PlaylistItem? next = null;
                    for (int j = 0; j < items.Count; j++)
                    {
                        if (j == i)
                        {
                            continue;
                        }

                        PlaylistItem other = items[j];
                        if (!string.Equals(other.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (other.StartTimeSeconds <= current.StartTimeSeconds)
                        {
                            continue;
                        }

                        if (next == null || other.StartTimeSeconds < next.StartTimeSeconds)
                        {
                            next = other;
                        }
                    }

                    double end = next != null ? next.StartTimeSeconds : current.Duration.TotalSeconds;
                    current.Duration = TimeSpan.FromSeconds(Math.Max(1, end - current.StartTimeSeconds));
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.BuildCueItems", caught);
            }

            return items;
        }

        // =====================================================================
        // 起播 / 切歌 / seek / 暂停
        // =====================================================================

        private async void PlayQueueAt(int index)
        {
            if (_closed || index < 0 || index >= _queue.Count)
            {
                return;
            }

            EnsureEngine();
            if (_engine == null)
            {
                StatusText.Text = "引擎初始化失败，无法播放。";
                return;
            }

            int token = ++_playToken;
            _playInFlight = true;
            _sessionActive = false;
            _queueIndex = index;

            PlaylistItem item = _queue[index];

            // ReplayGain：与主程序同口径（读源文件 track/album gain，按持久 mode 由 DSP 处理）
            try
            {
                ReplayGainState rg = ReplayGainStore.Load();
                var rgData = await Task.Run(() => ReplayGainReader.ReadForAudio(item.FilePath));
                if (token != _playToken || _closed)
                {
                    return;
                }

                if (rgData.HasValue)
                {
                    _engine.SetReplayGain(rg, rgData.Value.TrackGainDb, rgData.Value.AlbumGainDb, rgData.Value.Peak);
                }
                else
                {
                    _engine.SetReplayGain(rg, 0, 0, 1.0);
                }
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.PlayQueueAt.rg", caught);
            }

            // CUE 分轨：按 [起点, 起点+时长] 区间转码，进度/播完/seek 全部天然按轨口径
            double cueStart = item.StartTimeSeconds > 0 ? item.StartTimeSeconds : 0;
            double cueLength = cueStart > 0 ? Math.Max(0, item.Duration.TotalSeconds) : 0;

            string label = string.IsNullOrWhiteSpace(item.Title)
                ? Path.GetFileName(item.FilePath)
                : item.Title;
            StatusText.Text = "正在转码：" + label;

            bool ok = await _engine.PlayFileWithFfmpegAsync(
                item.FilePath,
                s => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed && token == _playToken)
                    {
                        StatusText.Text = s;
                    }
                }),
                cueStart,
                cueLength);

            if (token != _playToken || _closed)
            {
                return; // 期间用户点了别的歌 / 关了窗
            }

            _playInFlight = false;

            if (ok)
            {
                _sessionActive = true;
                StatusText.Text = DefaultHintText;
                StartVisualizerTimer();
                RefreshAll();
            }
            else
            {
                _sessionActive = false;
                StatusText.Text = "无法播放：" + (_engine.LastError ?? "未知错误")
                    + "（" + Path.GetFileName(item.FilePath) + "）";
                StartupLog.Write("零散窗口起播失败: " + item.FilePath + " err=" + (_engine.LastError ?? "?"));
            }
        }

        private void PlayNext()
        {
            if (_queue.Count == 0)
            {
                return;
            }

            int next;
            switch (_order)
            {
                case PlaybackOrder.Random:
                    next = _queue.Count > 1 ? _random.Next(_queue.Count) : 0;
                    break;
                case PlaybackOrder.ListLoop:
                    next = (_queueIndex + 1) % _queue.Count;
                    break;
                case PlaybackOrder.TrackLoop:
                case PlaybackOrder.TrackOnce:
                    next = _queueIndex;
                    break;
                default: // Sequential
                    next = Math.Min(_queueIndex + 1, _queue.Count - 1);
                    break;
            }

            PlayQueueAt(next);
        }

        private void PlayPrevious()
        {
            if (_queue.Count == 0)
            {
                return;
            }

            int previous;
            switch (_order)
            {
                case PlaybackOrder.Random:
                    previous = _queue.Count > 1 ? _random.Next(_queue.Count) : 0;
                    break;
                case PlaybackOrder.ListLoop:
                    previous = (_queueIndex - 1 + _queue.Count) % _queue.Count;
                    break;
                default:
                    previous = Math.Max(_queueIndex - 1, 0);
                    break;
            }

            PlayQueueAt(previous);
        }

        private void TogglePlayPause()
        {
            if (_playInFlight)
            {
                return; // 转码/起播中，忽略连点
            }

            if (_engine != null && _sessionActive)
            {
                if (_engine.IsPlaying)
                {
                    _engine.Pause();
                }
                else
                {
                    _engine.Resume();
                }

                return;
            }

            // 没有活会话：队列里有就从当前（或第一首）开始
            if (_queue.Count > 0)
            {
                PlayQueueAt(_queueIndex >= 0 && _queueIndex < _queue.Count ? _queueIndex : 0);
            }
        }

        private void SeekSeconds(double seconds)
        {
            if (_engine == null || !_sessionActive)
            {
                return;
            }

            try
            {
                double duration = _engine.Duration.TotalSeconds;
                if (duration <= 0)
                {
                    return;
                }

                _engine.Seek(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, duration)));
            }
            catch (Exception caught)
            {
                global::CelesteMusicPlayer.StartupLog.WriteException("ScatteredPlayWindow.SeekSeconds", caught);
            }
        }

        // =====================================================================
        // 引擎事件（后端线程触发，必须封送回 UI 线程）
        // =====================================================================

        private void OnEnginePlaybackEnded()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed)
                {
                    return;
                }

                _sessionActive = false;
                Safe(AdvanceAfterEnded);
            });
        }

        private void AdvanceAfterEnded()
        {
            if (_queue.Count == 0)
            {
                return;
            }

            switch (_order)
            {
                case PlaybackOrder.TrackLoop:
                    PlayQueueAt(_queueIndex);
                    break;
                case PlaybackOrder.TrackOnce:
                    break; // 播完即停
                case PlaybackOrder.Random:
                    PlayQueueAt(_random.Next(_queue.Count));
                    break;
                case PlaybackOrder.Sequential:
                    if (_queueIndex + 1 < _queue.Count)
                    {
                        PlayQueueAt(_queueIndex + 1);
                    }
                    break;
                default: // ListLoop
                    PlayQueueAt((_queueIndex + 1) % _queue.Count);
                    break;
            }
        }

        private void OnEngineFailed(Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed)
                {
                    return;
                }

                _sessionActive = false;
                _playInFlight = false;
                StatusText.Text = "播放出错：" + ex.Message;
                StartupLog.WriteException("ScatteredPlayWindow.OnEngineFailed", ex);
            });
        }
    }
}
