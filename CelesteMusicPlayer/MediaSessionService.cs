using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 系统媒体控件（SMTC）需要向宿主打听/请求的事。
    /// 服务只管「往系统上贴什么」，出声与切歌仍归宿主。
    /// </summary>
    internal interface IMediaSessionHost
    {
        /// <summary>任务栏缩略图上的播放/暂停按钮跟着一起翻（独立于 SMTC 开关）。</summary>
        void UpdateTaskbarPlayPause(bool playing);

        /// <summary>取曲目封面字节（取不到返回 null）。</summary>
        byte[]? ExtractCover(string audioPath);

        /// <summary>引擎当前时长（就绪态传给系统时间轴用）。</summary>
        TimeSpan EngineDuration { get; }

        void TogglePlayPause();
        void PlayNext();
        void PlayPrevious();
        void PauseCurrent();

        /// <summary>把动作丢回 UI 线程（SMTC 回调不在 UI 线程上）。</summary>
        void RunOnUiThread(Action action);
    }

    /// <summary>
    /// 系统媒体控件（Windows 媒体浮层 / 媒体键 / 锁屏）的完整实现。
    ///
    /// 关键约束（都是踩过的坑）：
    /// ① Unpackaged 应用不能用 `GetForCurrentView()`（打包专用），只能用 MediaPlayer 自带的 SMTC；
    /// ② MediaPlayer 没有真正播放时 SMTC 不显示数据 —— 系统要求"有播放会话"，
    ///    所以这里挂一个**静音循环**的占位源，且**绝不发声**（Volume=0、独立实例）；
    /// ③ 已有占位源时不重建，否则播放会话重置会让系统浮层闪一下。
    ///
    /// W13：从 MainWindow.Playback3 / Playback4 / Features 三处搬出来。
    /// </summary>
    internal sealed class MediaSessionService(IMediaSessionHost host)
    {
        private MediaPlayer? _silentHost;
        private SystemMediaTransportControls? _smtc;
        private long _lastTimelineMs;
        private long _lastTimelineLogMs;

        /// <summary>设置页那个「系统媒体控件」开关：开/关都要落到同一个 SMTC 实例上。</summary>
        internal void ConfigureFromSettings()
        {
            AppSettingsState settings = AppSettingsStore.Load();
            bool enable = settings.EnableSmtc;

            MediaPlayer? player = EnsureSilentHost();
            if (player == null)
            {
                return;
            }

            SystemMediaTransportControls smtc = player.SystemMediaTransportControls;
            smtc.IsEnabled = enable;
            smtc.IsPlayEnabled = enable;
            smtc.IsPauseEnabled = enable;
            smtc.IsNextEnabled = enable;
            smtc.IsPreviousEnabled = enable;
            smtc.IsStopEnabled = enable;
            smtc.ButtonPressed -= OnButtonPressed;
            if (enable)
            {
                smtc.ButtonPressed += OnButtonPressed;
            }
        }

        /// <summary>切歌/起播/就绪时把曲目信息贴到系统浮层。</summary>
        internal void ConfigureTrack(PlaylistItem item, bool playing)
        {
            try
            {
                // 任务栏缩略图按钮的播放/暂停图标同步：独立于 SMTC 开关，
                // 在引擎播放/暂停/就绪时统一刷新，避免启动自动播放时按钮停在「播放」图标。
                host.UpdateTaskbarPlayPause(playing);

                if (!AppSettingsStore.Load().EnableSmtc)
                {
                    return;
                }

                MediaPlayer? player = EnsureSilentHost();
                if (player == null)
                {
                    return;
                }

                _smtc ??= player.SystemMediaTransportControls;
                SystemMediaTransportControls smtc = _smtc;
                if (smtc == null)
                {
                    return;
                }

                smtc.IsEnabled = true;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;
                smtc.IsNextEnabled = true;
                smtc.IsPreviousEnabled = true;
                smtc.IsStopEnabled = true;
                smtc.ButtonPressed -= OnButtonPressed;
                smtc.ButtonPressed += OnButtonPressed;
                smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

                SystemMediaTransportControlsDisplayUpdater updater = smtc.DisplayUpdater;
                // 切歌/重设前清空旧元数据，避免 deskbox 等系统小组件残留上一首状态（标题缺失/两状态叠加）。
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = item.Title;
                updater.MusicProperties.Artist = item.Artist;
                updater.MusicProperties.AlbumTitle = item.Album;
                updater.Thumbnail = null;
                updater.Update();
                StartupLog.Write("[SMTC] 元数据已写入: Title=" + item.Title + " Artist=" + item.Artist + " Album=" + item.Album + " Status=" + smtc.PlaybackStatus);

                // 异步补封面缩略图（deskbox/系统媒体浮层可显示专辑封面）
                _ = LoadAndSetThumbnailAsync(updater, item);
            }
            catch (Exception caught) { StartupLog.WriteException("[SMTC] ConfigureTrack", caught); }
        }

        /// <summary>暂停/恢复/结束。</summary>
        internal void SetStatus(MediaPlaybackStatus status)
        {
            if (_smtc == null)
            {
                return;
            }

            try
            {
                _smtc.PlaybackStatus = status;
                if (status == MediaPlaybackStatus.Stopped)
                {
                    _smtc.IsEnabled = false;
                }
            }
            catch (Exception caught) { StartupLog.WriteException("[SMTC] SetStatus", caught); }
        }

        /// <summary>让出系统媒体控件（零散播放窗口接管时用）。</summary>
        internal void Disable()
        {
            if (_smtc == null) return;
            try { _smtc.IsEnabled = false; }
            catch (Exception caught) { StartupLog.WriteException("[SMTC] Disable", caught); }
        }

        /// <summary>同步系统浮层的进度条。就绪态（引擎还没建）由调用方传时长进来。</summary>
        internal void UpdateTimeline(TimeSpan position, TimeSpan? durationOverride = null)
        {
            if (_smtc == null)
            {
                if (Environment.TickCount64 - _lastTimelineMs > 3000)
                {
                    _lastTimelineMs = Environment.TickCount64;
                    StartupLog.Write("[SMTC] UpdateSmtcTimeline: 媒体控件未创建，跳过");
                }
                return;
            }

            try
            {
                TimeSpan duration = durationOverride ?? host.EngineDuration;
                if (duration <= TimeSpan.Zero)
                {
                    if (Environment.TickCount64 - _lastTimelineMs > 3000)
                    {
                        _lastTimelineMs = Environment.TickCount64;
                        StartupLog.Write("[SMTC] UpdateSmtcTimeline: duration=0，跳过 pos=" + position.TotalSeconds.ToString("F2"));
                    }
                    return;
                }

                var props = new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    EndTime = duration,
                    MinSeekTime = TimeSpan.Zero,
                    MaxSeekTime = duration,
                    Position = position
                };
                _smtc.UpdateTimelineProperties(props);

                // 诊断：首次成功写入 timeline 时打点，确认播放中进度同步链路是否真的在跑
                if (Environment.TickCount64 - _lastTimelineLogMs > 3000)
                {
                    _lastTimelineLogMs = Environment.TickCount64;
                    StartupLog.Write("[SMTC] timeline 已更新: pos=" + position.TotalSeconds.ToString("F2") + " dur=" + duration.TotalSeconds.ToString("F2"));
                }
            }
            catch (Exception caught) { StartupLog.WriteException("[SMTC] UpdateTimeline", caught); }
        }

        // ---------------------------------------------------------------- 内部

        private MediaPlayer? EnsureSilentHost()
        {
            try
            {
                if (_silentHost == null)
                {
                    _silentHost = new MediaPlayer();
                    _silentHost.CommandManager.IsEnabled = false; // 手动控制，禁用内置命令自动关联
                    _silentHost.AudioCategory = MediaPlayerAudioCategory.Media;
                    _silentHost.Volume = 0.0;              // 绝对静音，绝不发声
                    _silentHost.IsLoopingEnabled = true;   // 循环，永不停止
                    StartupLog.Write("[SMTC] 创建独立宿主 MediaPlayer 完成");
                }

                // 已挂过占位源就跳过，避免重复重建播放会话导致 SMTC 闪断
                if (_silentHost.Source == null)
                {
                    byte[] wav = BuildSilentWav();
                    using var ms = new MemoryStream(wav);
                    var stream = ms.AsRandomAccessStream();
                    _silentHost.Source = MediaSource.CreateFromStream(stream, "audio/wav");
                    _silentHost.Play();
                    StartupLog.Write("[SMTC] 静音占位源已挂载并 Play()，Source=" + (_silentHost.Source != null ? "有" : "无"));
                }
                else
                {
                    StartupLog.Write("[SMTC] 静音占位源已存在，跳过重建");
                }

                return _silentHost;
            }
            catch (Exception caught) { StartupLog.WriteException("[SMTC] EnsureSmtcSilentSource", caught); }
            return null;
        }

        private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            host.RunOnUiThread(() =>
            {
                switch (args.Button)
                {
                    case SystemMediaTransportControlsButton.Play:
                    case SystemMediaTransportControlsButton.Pause:
                        host.TogglePlayPause();
                        break;
                    case SystemMediaTransportControlsButton.Next:
                        host.PlayNext();
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        host.PlayPrevious();
                        break;
                    case SystemMediaTransportControlsButton.Stop:
                        host.PauseCurrent();
                        break;
                }
            });
        }

        /// <summary>异步读取曲目封面并设置到 SMTC 缩略图（失败静默）。</summary>
        private async Task LoadAndSetThumbnailAsync(SystemMediaTransportControlsDisplayUpdater updater, PlaylistItem item)
        {
            try
            {
                if (updater == null || item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
                byte[]? bytes = await Task.Run(() => host.ExtractCover(item.FilePath));
                if (bytes is not { Length: > 0 })
                {
                    StartupLog.Write("[SMTC] 封面：未取到封面字节，跳过");
                    return;
                }

                // 关键：DataWriter 在 Dispose 时会 DetachStream 并释放其关联的底层流，
                // 所以必须先用 DetachStream() 把 mem 的所有权拿回来，否则 mem.Seek(0) 抛 ObjectDisposedException。
                // 而 mem 本身不能被 using 释放——RandomAccessStreamReference 只持引用，系统异步读缩略图时才真正读流。
                var mem = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(mem))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                mem.Seek(0);

                var reference = RandomAccessStreamReference.CreateFromStream(mem);
                updater.Thumbnail = reference;
                updater.Update();
                StartupLog.Write("[SMTC] 封面已设置: " + bytes.Length + " 字节");
            }
            catch (Exception caught) { StartupLog.WriteException("[SMTC] 封面设置", caught); }
        }

        /// <summary>在内存中构造 1 秒 44.1kHz / 16bit / 单声道纯静音 PCM WAV（44 字节头 + 全零采样）。</summary>
        private static byte[] BuildSilentWav()
        {
            const int sampleRate = 44100;
            const int channels = 1;
            const int bitsPerSample = 16;
            const int seconds = 1;

            int bytesPerSample = bitsPerSample / 8;
            int dataLength = sampleRate * channels * bytesPerSample * seconds; // 88200
            byte[] wav = new byte[44 + dataLength];

            // RIFF 头
            wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
            int fileLength = 36 + dataLength;
            wav[4] = (byte)(fileLength & 0xFF);
            wav[5] = (byte)((fileLength >> 8) & 0xFF);
            wav[6] = (byte)((fileLength >> 16) & 0xFF);
            wav[7] = (byte)((fileLength >> 24) & 0xFF);
            wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';

            // fmt 子块
            wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
            wav[16] = 16; wav[17] = 0; wav[18] = 0; wav[19] = 0; // fmt 长度 16
            wav[20] = 1; wav[21] = 0; // PCM 编码
            wav[22] = (byte)channels; wav[23] = 0;
            wav[24] = (byte)(sampleRate & 0xFF);
            wav[25] = (byte)((sampleRate >> 8) & 0xFF);
            wav[26] = (byte)((sampleRate >> 16) & 0xFF);
            wav[27] = (byte)((sampleRate >> 24) & 0xFF);
            wav[28] = (byte)((sampleRate * bytesPerSample * channels) & 0xFF);
            wav[29] = (byte)(((sampleRate * bytesPerSample * channels) >> 8) & 0xFF);
            wav[30] = (byte)(((sampleRate * bytesPerSample * channels) >> 16) & 0xFF);
            wav[31] = (byte)(((sampleRate * bytesPerSample * channels) >> 24) & 0xFF);
            wav[32] = (byte)((channels * bytesPerSample) & 0xFF); wav[33] = 0; // 块对齐
            wav[34] = (byte)bitsPerSample; wav[35] = 0;

            // data 子块
            wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
            wav[40] = (byte)(dataLength & 0xFF);
            wav[41] = (byte)((dataLength >> 8) & 0xFF);
            wav[42] = (byte)((dataLength >> 16) & 0xFF);
            wav[43] = (byte)((dataLength >> 24) & 0xFF);

            // 采样数据全零（byte 数组默认就是 0，无需再填）

            return wav;
        }
    }
}
