using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CelesteMusicPlayer
{
    /// <summary>HiFi 独占输出后端（NAudio）：WASAPI Exclusive / ASIO，从 PCM WAV 流式输出。</summary>
    public sealed class HiFiOutputBackend : IDisposable
    {
        /// <summary>输出模式。</summary>
        public enum OutputMode
        {
            /// <summary>WASAPI 共享模式（对照用，走系统混音器）。</summary>
            WasapiShared,

            /// <summary>WASAPI 独占模式（绕开系统混音器，HiFi 首选）。</summary>
            WasapiExclusive,

            /// <summary>ASIO（专有声卡驱动）。</summary>
            Asio,
        }

        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _positionTimer;
        // 用 WaveFileReader（IWaveProvider）直出源 PCM，保证严格样本直通（bit-perfect），
        // 且不依赖 ACM（AudioFileReader 读 24bit 等需 ACM，缺 driver 会抛 "NoDriver calling acmFormatSuggest"）。
        private WaveFileReader? _waveFile;
        private IWavePlayer? _output; // WasapiOut 或 AsioOut
        private SeamlessWaveProvider? _seamless; // NAudio 输出（共享/ASIO）的无缝续接源（当前+下一首）
        private double[]? _eqGains; // HiFi(ASIO/共享 NAudio) 10 段 EQ 增益(dB)，启用则包 EQ provider
        private Eq10WaveProvider? _eqProvider; // 当前输出使用的 EQ provider（NAudio 路径），用于播放中实时更新增益
        private NativeWasapiExclusiveOut? _native; // 原生 WASAPI 独占输出器（WasapiExclusive 模式替代 NAudio WasapiOut）
        private bool _useNative; // 当前播放是否走原生独占输出
        private MMDevice? _device;     // 用于调设备/系统主音量（WASAPI）；ASIO 无统一接口为 null
        private bool _isPlaying;
        private TimeSpan _pausedPosition;
        private float _resumeVolume = 1f;
        private string? _activeWavPath;
        private long _nativePosBaselineFrames; // 原生独占下当前曲目起始帧基准（用于按曲目换算相对进度，避免跨曲累加）
        private OutputMode _activeMode;
        private string? _activeDeviceId;

        /// <summary>播放位置变化（约 200ms 一次）。</summary>
        public event Action<TimeSpan>? PositionChanged;

        /// <summary>文件播放结束。</summary>
        public event Action? PlaybackStopped;

        /// <summary>共享/ASIO 无缝切到下一首（上层需更新标题/时长并继续预加载下下首）。</summary>
        public event Action? SeamlessTrackChanged;

        /// <summary>失败（初始化/打开/输出）。</summary>
        public event Action<Exception>? Failed;

        public HiFiOutputBackend()
        {
            _positionTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            _positionTimer.Interval = TimeSpan.FromMilliseconds(200);
            _positionTimer.Tick += (_, _) => UpdatePosition();
        }

        public bool IsPlaying => _isPlaying;

        public TimeSpan Duration { get; private set; }

        public TimeSpan Position { get; private set; }

        // 源音频实际时长（来自元数据/TagLib）。非 0 时优先用它作为 Duration 上限，
        // 避免 DSD 转 PCM 后 WAV 尾部 padding 让进度条越过源时长。
        private TimeSpan _sourceDuration;

        /// <summary>设置源音频实际时长（可覆盖转码 WAV 计算出的更长时长，如 DSD 转 PCM 带尾部）。</summary>
        public void SetSourceDuration(TimeSpan duration)
        {
            _sourceDuration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            if (_sourceDuration > TimeSpan.Zero && (Duration <= TimeSpan.Zero || _sourceDuration < Duration))
            {
                Duration = _sourceDuration;
            }
        }

        /// <summary>设置 HiFi（ASIO/共享 NAudio 输出）的 10 段 EQ 增益（dB，-12..12）。null / 全 0 表示直通（bit-perfect）。</summary>
        public void SetEqualizer(double[]? gainsDb)
        {
            _eqGains = gainsDb == null ? null : (double[])gainsDb.Clone();
            // 仅在播放中且为 NAudio(ASIO/共享) 输出且已在用 EQ provider 时实时更新增益；
            // 否则仅记录，由下次 PlayWavAsync 按新增益决定是否启用 EQ provider。
            if (_output != null && !_useNative && _isPlaying && _eqProvider != null && _eqGains != null && HasNonZeroGain(_eqGains))
            {
                _eqProvider.UpdateGains(_eqGains);
            }
        }

        private static bool HasNonZeroGain(double[] gains)
        {
            for (int i = 0; i < gains.Length; i++)
            {
                if (Math.Abs(gains[i]) > 0.01)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>预加载下一首 WAV 到无缝源（共享/ASIO）。
        /// 与当前同格式未读及时可无缝续接；格式不同或未就绪则由上层走重建。返回是否采纳为无缝续接。</summary>
        public bool PrepareNextSeamless(string nextWavPath)
        {
            if (_seamless == null)
            {
                return false;
            }

            try
            {
                using var probe = File.Exists(nextWavPath) ? new WaveFileReader(nextWavPath) : null;
                if (probe == null)
                {
                    return false;
                }

                var curWf = _seamless.WaveFormat;
                if (curWf == null)
                {
                    return false; // 当前 reader 无有效格式（如 seek/暂停后状态未就绪），下次重试
                }

                bool same = probe.WaveFormat.SampleRate == curWf.SampleRate
                    && probe.WaveFormat.BitsPerSample == curWf.BitsPerSample
                    && probe.WaveFormat.Channels == curWf.Channels;
                if (!same)
                {
                    StartupLog.Write($"无缝预载 格式不符: next={probe.WaveFormat.SampleRate}/{probe.WaveFormat.BitsPerSample}bit/{probe.WaveFormat.Channels}ch cur={curWf.SampleRate}/{curWf.BitsPerSample}bit/{curWf.Channels}ch → 不采纳");
                    return false;
                }

                var next = new WaveFileReader(nextWavPath);
                _seamless.PrepareNext(next);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>最近一次失败原因。</summary>
        public string? LastError { get; private set; }

        /// <summary>当前输出模式（只读，由 PlayWavAsync 决定并用 OutputDeviceName 记录）。</summary>
        public OutputMode? CurrentMode { get; private set; }

        /// <summary>当前输出设备描述（如声卡名 / ASIO 驱动名）。</summary>
        public string? OutputDeviceName { get; private set; }

        /// <summary>实际协商后的输出格式（WASAPI 设备端采样率/位深/声道），如 "176400 Hz / 24 bit / 2声道"。null=未知。</summary>
        public string? ActualOutputFormat { get; private set; }

        /// <summary>当前播放源的原始格式（WAV 直通源），如 "44100 Hz / 16 bit / 2声道"。</summary>
        public string? SourceFormatDescription { get; private set; }

        /// <summary>播放后从 WasapiOut/AsioOut 读取实际输出格式并更新 <see cref="ActualOutputFormat"/>。</summary>
        private void CaptureActualOutputFormat()
        {
            ActualOutputFormat = null;
            if (_output == null)
            {
                return;
            }

            try
            {
                // WasapiOut 暴露 OutputWaveFormat（实际设备协商格式）；AsioOut 需其它途径，暂取未知
                var prop = _output.GetType().GetProperty("OutputWaveFormat");
                if (prop?.GetValue(_output) is NAudio.Wave.WaveFormat wf && wf != null)
                {
                    int bits = wf.BitsPerSample;
                    // 24bit 可能实际是 IeeeFloat/32；对 DoP/独占显示核心格式
                    ActualOutputFormat = wf.SampleRate + " Hz / " + bits + " bit / " + wf.Channels + " 声道";
                }
            }
            catch
            {
                ActualOutputFormat = null;
            }
        }

        /// <summary>枚举 WASAPI 渲染设备（NAudio MMDevice，返回 Id + 友好名）。</summary>
        public static IReadOnlyList<(string Id, string Name)> EnumerateWasapiDevices()
        {
            var list = new List<(string Id, string Name)>();
            try
            {
                using var mmde = new MMDeviceEnumerator();
                foreach (MMDevice dev in mmde.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    list.Add((dev.ID, dev.DeviceFriendlyName));
                }
            }
            catch
            {
            }

            return list;
        }

        /// <summary>按 MMDevice.ID 解析 WASAPI 渲染设备（找不到返回 null）。</summary>
        public static MMDevice? ResolveWasapiDevice(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            try
            {
                using var mmde = new MMDeviceEnumerator();
                foreach (MMDevice d in mmde.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (string.Equals(d.ID, id, StringComparison.OrdinalIgnoreCase))
                    {
                        return d;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>系统默认渲染设备的 NAudio MMDevice.ID（获取失败返回空）。</summary>
        public static string GetDefaultWasapiDeviceId()
        {
            try
            {
                using var mmde = new MMDeviceEnumerator();
                return mmde.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>读取 WASAPI 设备的 MixFormat（系统默认格式，设备必定支持）。返回 (采样率, 声道数, 位深)；失败返回 null。</summary>
        public static (int Rate, int Channels, int Bits)? GetDeviceMixFormat(string? deviceId)
        {
            try
            {
                MMDevice? dev = string.IsNullOrWhiteSpace(deviceId) ? null : ResolveWasapiDevice(deviceId);
                if (dev == null)
                {
                    using var mmde = new MMDeviceEnumerator();
                    dev = mmde.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }

                using var ac = dev.AudioClient;
                WaveFormat mf = ac.MixFormat;
                return (mf.SampleRate, mf.Channels, mf.BitsPerSample);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>检测 WASAPI 独占模式下设备是否支持给定格式（无法检测时保守放行）。</summary>
        public static bool IsExclusiveFormatSupported(string? deviceId, WaveFormat format)
        {
            try
            {
                MMDevice? dev = string.IsNullOrWhiteSpace(deviceId) ? null : ResolveWasapiDevice(deviceId);
                if (dev == null)
                {
                    using var mmde = new MMDeviceEnumerator();
                    dev = mmde.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }

                using var ac = dev.AudioClient;
                return ac.IsFormatSupported(AudioClientShareMode.Exclusive, format);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>解析用于调设备/系统音量的 MMDevice（失败返回 null → 不调设备音量）。</summary>
        private static MMDevice? ResolveDeviceForVolume(string? deviceId)
        {
            try
            {
                MMDevice? dev = string.IsNullOrWhiteSpace(deviceId) ? null : ResolveWasapiDevice(deviceId);
                if (dev == null)
                {
                    using var mmde = new MMDeviceEnumerator();
                    dev = mmde.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }

                return dev;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>创建 WasapiOut：有 deviceIdentifier 时输出到指定 MMDevice，否则系统默认。</summary>
        private static WasapiOut? CreateWasapiOut(AudioClientShareMode mode, string? deviceIdentifier, int latency)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(deviceIdentifier))
                {
                    MMDevice? dev = ResolveWasapiDevice(deviceIdentifier);
                    if (dev != null)
                    {
                        return new WasapiOut(dev, mode, true, latency);
                    }
                }

                return new WasapiOut(mode, true, latency);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>枚举 ASIO 驱动名。</summary>
        public static IReadOnlyList<string> EnumerateAsioDrivers()
        {
            try
            {
                return AsioOut.GetDriverNames();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>从 PCM WAV 文件以指定模式播放。</summary>
        public bool PlayWavAsync(string wavPath, OutputMode mode, string? deviceIdentifier = null, TimeSpan? seekTo = null)
        {
            try
            {
                StopCore();

                if (!File.Exists(wavPath))
                {
                    LastError = "音频缓存文件不存在：\n" + wavPath;
                    return false;
                }

                _waveFile = new WaveFileReader(wavPath);
                _activeWavPath = wavPath;
                _activeMode = mode;
                _activeDeviceId = deviceIdentifier;
                // 无缝源（当前+可预加载下一首）：共享/ASIO 走 NAudio wasapi/asio，独占走原生 WASAPI，
                // 均用同一份 SeamlessWaveProvider 做同格式字节级续接 → 单输出会话 gapless。
                _seamless = new SeamlessWaveProvider(_waveFile);
                switch (mode)
                {
                    case OutputMode.WasapiShared:
                        _device = ResolveDeviceForVolume(deviceIdentifier);
                        _output = CreateWasapiOut(AudioClientShareMode.Shared, deviceIdentifier, 100);
                        OutputDeviceName = "WASAPI 共享";
                        break;

                    case OutputMode.WasapiExclusive:
                        // 原生 WASAPI 独占（复刻 ECHO）：不经 NAudio sample 转换层，源 PCM 直通，
                        // 避免 "not a supported encoding"(Extensible)；设备不支持源格式时内部降级 FLOAT32。
                        _device = ResolveDeviceForVolume(deviceIdentifier);
                        var natDev = NativeWasapi.GetRenderDeviceById(deviceIdentifier);
                        if (natDev == null)
                        {
                            LastError = "无法解析输出设备。";
                            StartupLog.Write("WasapiExclusive 设备解析失败 id=" + (deviceIdentifier ?? "<默认>") + " diag=" + (NativeWasapi.LastEnumDiag ?? "?"));
                            Cleanup();
                            return false;
                        }

                        var nat = new NativeWasapiExclusiveOut();
                        if (!nat.Init(natDev, _seamless))
                        {
                            LastError = nat.LastError ?? "原生 WASAPI 初始化失败";
                            StartupLog.Write("WasapiExclusive 原生初始化失败: " + (nat.LastError ?? "未知") + " | 源格式=" + (_waveFile?.WaveFormat.SampleRate) + "/" + (_waveFile?.WaveFormat.BitsPerSample) + "bit/" + (_waveFile?.WaveFormat.Channels) + "ch");
                            try { Marshal.ReleaseComObject(natDev); } catch { }
                            Cleanup();
                            return false;
                        }
                        try { Marshal.ReleaseComObject(natDev); } catch { }

                        _native = nat;
                        _useNative = true;
                        OutputDeviceName = nat.ActualFormatDescription != null ? "WASAPI 独占（" + nat.ActualFormatDescription + "）" : "WASAPI 独占(原生)";
                        break;

                    case OutputMode.Asio:
                        string driver = deviceIdentifier ?? (AsioOut.GetDriverNames().Length > 0 ? AsioOut.GetDriverNames()[0] : string.Empty);
                        if (string.IsNullOrEmpty(driver))
                        {
                            LastError = "未检测到 ASIO 驱动。";
                            Cleanup();
                            return false;
                        }

                        _device = null; // ASIO 无统一端点音量，靠声卡硬件旋钮
                        _output = new AsioOut(driver);
                        OutputDeviceName = "ASIO: " + driver;
                        break;

                    default:
                        LastError = "未知输出模式。";
                        Cleanup();
                        return false;
                }

                Duration = _waveFile.TotalTime;
                // 若调用了 SetSourceDuration（源元数据时长），优先用源时长，规避转码 WAV 尾部 padding 越界
                if (_sourceDuration > TimeSpan.Zero && _sourceDuration < Duration)
                {
                    Duration = _sourceDuration;
                }
                var srcWf = _waveFile.WaveFormat;
                SourceFormatDescription = srcWf.SampleRate + " Hz / " + srcWf.BitsPerSample + " bit / " + srcWf.Channels + "声道";
                Position = TimeSpan.Zero;
                _pausedPosition = seekTo ?? TimeSpan.Zero;

                // 播放起始前定位到 seekTo（暂停恢复续播的关键：在输出缓冲填充前 seek，避免跳变爆音）
                if (seekTo != null && seekTo.Value > TimeSpan.Zero)
                {
                    try
                    {
                        _waveFile.CurrentTime = seekTo.Value;
                        Position = seekTo.Value;
                    }
                    catch
                    {
                    }
                }

                if (_useNative)
                {
                    _native!.Ended += Native_Ended;
                    if (!_native.Play(_pausedPosition))
                    {
                        LastError = _native.LastError ?? "原生 WASAPI 播放启动失败";
                        Cleanup();
                        return false;
                    }

                    ActualOutputFormat = _native.ActualFormatDescription;
                }
                else
                {
                    // 共享/ASIO 用无缝源：同格式下一首可无缝续接（gapless）。
                    // 若用户开启 EQ（非全 0 增益），包一层 10 段 PeakingEQ provider（数据经 DSP → 非 bit-perfect）；
                    // 全 0 / 默认时保持源 PCM 直通。
                    if (_eqGains != null && HasNonZeroGain(_eqGains))
                    {
                        _eqProvider = new Eq10WaveProvider(_seamless!, _eqGains);
                        _output.Init(_eqProvider);
                        CaptureActualOutputFormat();
                        StartupLog.Write("[EQ诊断] HiFi输出已启用 EQ（gain 非全0） → 数据经 DSP，非 bit-perfect");
                    }
                    else
                    {
                        _eqProvider = null;
                        _output.Init(_seamless); // IWaveProvider：源 PCM 原样直通（严格 bit-perfect）
                        CaptureActualOutputFormat();
                        StartupLog.Write("[EQ诊断] HiFi输出 EQ=false(gain 全0/未设) → bit-perfect直通");
                    }

                    _output.PlaybackStopped += Output_PlaybackStopped;
                    _output.Play();
                }

                _isPlaying = true;
                CurrentMode = mode;
                // 新播放会话：位置基准归零（_native 已按 seekTo 初始化 _framesWritten，
                // 基准=0 使 Position 显示为 seekTo→绝对进度；无缝续接时才重置基准到新歌起点）。
                _nativePosBaselineFrames = 0;
                _positionTimer.Start();
                // 输出层协商日志（排障"假 bit-perfect"）：源格式 → 模式 → 设备端实际格式 → 对齐dance/降级
                StartupLog.Write(string.Format(
                    "输出启动 mode={0} 源={1}bit/{2}Hz/{3}ch → {4} | 对齐dance={5}",
                    mode,
                    _waveFile?.WaveFormat.BitsPerSample, _waveFile?.WaveFormat.SampleRate, _waveFile?.WaveFormat.Channels,
                    ActualOutputFormat ?? OutputDeviceName ?? "?",
                    (_native?.LastAlignDance == true) ? "是" : "否"));
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Cleanup();
                Failed?.Invoke(ex);
                return false;
            }
        }

        /// <summary>暂停播放：释放输出，记录暂停位置。（Echo 风格：暂停释放独占，避免 Pause/Play 缓冲重建爆音。）</summary>
        public void Pause()
        {
            if ((_output == null && _native == null) || !_isPlaying)
            {
                return;
            }

            // 暂停点用当前显示的播放位置（Position）：seek 后 HiFiOutputBackend.Seek 已把 Position 设为用户 seek 目标，
            // 正常播放时是 UpdatePosition 维护的实时位置。不要依赖渲染线程异步消费后的 reader 游标，
            // 否则 seek→暂停的瞬间可能记录成错误位置（如读到文件尾）导致恢复后误判已播完而切歌。
            _pausedPosition = Position;

            // 完全释放输出（Stop + Dispose），记录激活参数以便恢复时重建
            OutputMode savedMode = _activeMode;
            string? savedDev = _activeDeviceId;
            StopCore();
            _activeMode = savedMode;
            _activeDeviceId = savedDev;
            CurrentMode = savedMode;
            _isPlaying = false;
            _positionTimer.Stop();
        }

        /// <summary>恢复播放：彻底重建输出并从暂停点续播，规避独占 Pause/Play 的不连续爆音。</summary>
        public void Resume()
        {
            if ((_output != null || _native != null) || string.IsNullOrEmpty(_activeWavPath))
            {
                return;
            }

            // 恢复前先把设备主音量压到很低，使重建起播就以低音量输出，避免恢复瞬间的全音量爆音；
            // 之后由上层 FadeInEngineAfterResumeAsync 渐变回用户音量。
            try
            {
                if (_device?.AudioEndpointVolume != null)
                {
                    _device.AudioEndpointVolume.MasterVolumeLevelScalar = 0.02f;
                }
            }
            catch
            {
            }

            // 用缓存的激活参数完全重建输出，并在启动缓冲前定位到暂停点
            TimeSpan resumeAt = _pausedPosition;
            if (!PlayWavAsync(_activeWavPath, _activeMode, _activeDeviceId, resumeAt))
            {
                return;
            }

            // 恢复用户音量的渐变由上层（MainWindow.FadeInEngineAfterResumeAsync）完成；
            // 此处不直接 SetVolume(_resumeVolume)（否则会立刻回到全音量重新造成爆音）。

            _isPlaying = true;
            _positionTimer.Start();
        }

        public void Stop()
        {
            StopCore();
            Position = TimeSpan.Zero;
            OutputDeviceName = null;
            ActualOutputFormat = null;
            SourceFormatDescription = null;
            CurrentMode = null;
        }

        public void Seek(TimeSpan position)
        {
            Position = position;
            _pausedPosition = position;

            if (_useNative && _native != null)
            {
                // 播放中 seek：交由 render 线程安全重定位（避免与正在读源的线程直接竞争）
                _native.SeekTo(position);
                return;
            }

            if (_waveFile == null)
            {
                // 暂停时 wav 已释放；仅记录目标位置，Resume 时会从该处续播。
                return;
            }

            try
            {
                _waveFile.CurrentTime = position;
            }
            catch
            {
            }
        }

        /// <summary>当前设备主音量标量 0..1；无设备/ASIO 返回 -1（未知）。</summary>
        public float GetDeviceVolume()
        {
            if (_device?.AudioEndpointVolume != null)
            {
                try
                {
                    return _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                }
                catch
                {
                }
            }

            return -1f;
        }

        public void SetVolume(float volume)
        {
            _resumeVolume = Math.Clamp(volume, 0f, 1f);
            if (_device?.AudioEndpointVolume != null)
            {
                try
                {
                    // 滑块与设备主音量联动（独占直通时靠设备端控响）。为缓解“整体偏大”，
                    // 对滑块标量做非线性压缩：dev = slider^2 —— 滑块 100%→100%、50%→25%、80%→64%，
                    // 高段更平缓、整体更轻且调节更细。
                    float dev = _resumeVolume * _resumeVolume;
                    _device.AudioEndpointVolume.MasterVolumeLevelScalar = dev;
                }
                catch
                {
                }
            }
        }

        private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            _isPlaying = false;
            _positionTimer.Stop();
            Position = Duration;
            PlaybackStopped?.Invoke();
        }

        private void Native_Ended()
        {
            _isPlaying = false;
            _positionTimer.Stop();
            Position = Duration;
            PlaybackStopped?.Invoke();
        }

        private void UpdatePosition()
        {
            if (!_isPlaying)
            {
                return;
            }

            if (_useNative && _native != null)
            {
                // 用数据源 reader 的实时游标作为位置（渲染线程读取同一 reader，反映真实播放进度）：
                // 无缝续接后 _seamless.Current 变为下一首 reader（位置从 0），seek 后 CurrentTime 即被更新，
                // 避免依赖跨曲累加的 _framesWritten 相对基准（seek/暂停恢复/续接时不一致）。
                var curReader = _seamless?.Current ?? _waveFile;
                Position = curReader != null ? curReader.CurrentTime : TimeSpan.Zero;
            }
            else if (_waveFile != null)
            {
                Position = _waveFile.CurrentTime;
            }
            else
            {
                return;
            }

            // NAudio WasapiOut/AsioOut 在数据源自然播放到末尾时，若不主动 Stop，PlaybackStopped 事件不会触发
            // （实测 HasReachedEnd=true 但 PlaybackState 仍为 Playing），导致“播完不自动下一首”。
            // 若共享/ASIO 已预加载同格式下一首，则由无缝源自动续接（不 Stop → gapless），并通知上层切换。
            if (_seamless != null && _seamless.SwitchedToNext)
            {
                StartupLog.Write($"[无缝诊断] 已无缝续接到下一首 (initiator={(_useNative?"独占native":"naudio")})");
                _seamless.ResetSwitchFlag(); // 允许下一次无缝切换
                // 同步到已无缝切入的下一首：源 reader / 时长 / 位置，保证 Position/Duration 继续正确
                var nextReader = _seamless.Current;
                if (nextReader != null && !ReferenceEquals(nextReader, _waveFile))
                {
                    _waveFile?.Dispose();
                    _waveFile = nextReader;
                    // Duration：源时长优先，否则用新 reader 的 WAV 时长
                    Duration = _sourceDuration > TimeSpan.Zero ? _sourceDuration : _waveFile.TotalTime;
                    if (_sourceDuration > TimeSpan.Zero && _sourceDuration < Duration)
                    {
                        Duration = _sourceDuration;
                    }
                }
                // 无缝续接到下一首：位置基准重置为当前累计帧，使下一首从 0 起算（不跨曲累加）。
                if (_native != null)
                {
                    _nativePosBaselineFrames = _native.FramesWritten;
                }

                Position = TimeSpan.Zero;
                SeamlessTrackChanged?.Invoke();
            }

            bool sourceExhausted = false;
            if (_waveFile != null && _waveFile.Length > 16
                && (!_useNative || (_native?.IsStarted == true))) // native 模式下仅在渲染线程真正启动时判定，避免重建窗口期的旧 reader 误判为已读尽
            {
                // 数据源已真实读到末尾（最可靠，避免依赖被源时长改短的 Duration；DSD 转码 WAV 读尽即播完）。
                // 阈值与 SeamlessWaveProvider.HasReadyNext 的 -8 对齐，避免"源已读尽但续接尚未标记"的窗口被误判为需要重建。
                // 要求 Position>0 且 Length>16，避免空/极小缓存文件（Length 异常小）在开播即被误判为已读尽。
                sourceExhausted = _waveFile.Position >= _waveFile.Length - 8 && _waveFile.Position > 0;
            }

            if (_waveFile != null && Duration > TimeSpan.Zero && sourceExhausted)
            {
                // 有可续接的下一首：交给无缝源，不主动 Stop（播完检测挪到切歌后）
                if (_seamless != null && _seamless.HasReadyNext)
                {
                    // 本轮末尾不 Stop；切换下一首后由上层 PrepareNext 继续预加载
                }
                else
                {
                    bool sameObj = _seamless?.Current != null && _waveFile != null && ReferenceEquals(_seamless.Current, _waveFile);
                    var pc = _seamless?.ProbeCurrentState;
                    StartupLog.Write($"[无缝诊断] 决定 Stop→重建: durSec={Duration.TotalSeconds:F1} posSec={Position.TotalSeconds:F1} srcExh={sourceExhausted} wavefileLen={( _waveFile!=null?_waveFile.Length:"null")} wavefilePos={( _waveFile!=null?_waveFile.Position.ToString():"null")} seamlessCurrent{( _seamless!=null&&_seamless.Current!=null?"=wave:"+sameObj:  _seamless!=null?"=null":"无")} curPos={( pc?.Pos.ToString() ?? "-")}/{( pc?.Len.ToString() ?? "-")} nextMounted={(_seamless?.NextMounted==true)} switched={(_seamless?.SwitchedToNext==true)}");
                    try
                    {
                        if (_useNative && _native != null)
                        {
                            _native.Stop(); // 停止渲染线程
                            if (!_isPlaying) { /* already stopping */ }
                            else { PlaybackStopped?.Invoke(); } // 原生 Stop 不触发 Ended（_requestStop），手动通知上层切下一首
                        }
                        else
                        {
                            _output?.Stop(); // 触发 Output_PlaybackStopped（内含 PlaybackStopped）
                        }
                    }
                    catch
                    {
                    }
                }
            }

            PositionChanged?.Invoke(Position);
        }

        private void StopCore()
        {
            _positionTimer.Stop();
            if (_output != null)
            {
                _output.PlaybackStopped -= Output_PlaybackStopped;
                try
                {
                    _output.Stop();
                }
                catch
                {
                }

                try
                {
                    _output.Dispose();
                }
                catch
                {
                }
            }

            _output = null;
            if (_native != null)
            {
                try { _native.Ended -= Native_Ended; _native.Stop(); } catch { }
                try { _native.Dispose(); } catch { }
                _native = null;
            }
            _useNative = false;
            _waveFile?.Dispose();
            _waveFile = null;
            _seamless?.Dispose(); // 释放已预加载的 next reader，避免切歌后文件句柄延迟释放
            _seamless = null;
            _isPlaying = false;
        }

        private void Cleanup()
        {
            if (_output != null)
            {
                _output.PlaybackStopped -= Output_PlaybackStopped;
                try
                {
                    _output.Dispose();
                }
                catch
                {
                }

                _output = null;
            }

            if (_native != null)
            {
                try { _native.Ended -= Native_Ended; _native.Stop(); } catch { }
                try { _native.Dispose(); } catch { }
                _native = null;
            }
            _useNative = false;
            _seamless?.Dispose();
            _seamless = null;
            _waveFile?.Dispose();
            _waveFile = null;
            _positionTimer.Stop();
            _isPlaying = false;
        }

        public void Dispose() => Cleanup();
    }
}
