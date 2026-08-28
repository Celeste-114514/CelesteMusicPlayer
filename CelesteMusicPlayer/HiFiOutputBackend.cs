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
        private double[]? _eqGains; // (旧) 10 段 EQ 增益(dB)，独立 EQ 窗口用
        private EqCurveState? _eqCurve; // 动态 EQ 曲线状态（DSP 面板用）
        private ChannelBalanceState? _channelBalance; // 声道平衡状态
        private DspSafetyState? _safety; // 安全限幅/余量状态
        private int _crossfadeMs; // 交叉淡化时长（毫秒），0 = 关闭（保持无缝硬切）
        private ManagedDspSourceProvider? _dspProvider; // 统一 DSP 链（EQ→声道平衡→限幅），NAudio 与独占共用
        // ReplayGain 响度归一化（缓存到新建链，保证换歌/重播也生效）
        private ReplayGainState? _rgState;
        private double _rgTrackDb, _rgAlbumDb, _rgPeak = 1.0;
        private NativeWasapiExclusiveOut? _native; // 原生 WASAPI 独占输出器（WasapiExclusive 模式替代 NAudio WasapiOut）
        private bool _useNative; // 当前播放是否走原生独占输出
        private bool _isDsd;     // 当前是否 DSD/DoP 直出（独占 + 禁降级）
        private DoPWaveSource? _dsdSource; // DSD/DoP 数据源（仅向独占通道喂 DoP 帧）
        private double _lastDsdPrefillLogSec = double.NegativeInfinity; // 限频 DSD ring 欠载诊断日志
        private MMDevice? _device;     // 用于调设备/系统主音量（WASAPI）；ASIO 无统一接口为 null
        private bool _isPlaying;
        private TimeSpan _pausedPosition;
        private TimeSpan? _pendingSeekTarget; // DSD/native 播放中 seek 的待消费目标：防止 updatePosition/Pause 用旧的 FramesWritten 把它覆盖掉（否则选进度后进度条不变/从头重播）
        private float _resumeVolume = 1f;
        private float _pausedDeviceVol = -1f; // 暂停瞬间记录的真实设备主音量（供恢复回到该值，避免误用 0.02 防爆音残留）
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

        /// <summary>设置 10 段 EQ 增益（dB，-12..12）。null / 全 0 表示直通（bit-perfect）。播放中实时生效。</summary>
        public void SetEqualizer(double[]? gainsDb)
        {
            _eqGains = gainsDb == null ? null : (double[])gainsDb.Clone();
            _eqCurve = null; // 旧窗口与 DSP 面板互斥占用 EQ
            _dspProvider?.UpdateEq(_eqGains);
        }

        /// <summary>设置动态 EQ 曲线状态（DSP 面板用，band 列表 + preamp）。null / 无效果表示直通。播放中实时生效（整数组原子替换）。</summary>
        public void SetEqCurve(EqCurveState? curve)
        {
            _eqCurve = curve?.Clone();
            _eqGains = null; // DSP 面板与旧窗口互斥占用 EQ
            _dspProvider?.UpdateEqCurve(_eqCurve);
        }

        /// <summary>设置声道平衡状态。null / 未启用表示直通。播放中实时生效。</summary>
        public void SetChannelBalance(ChannelBalanceState? state)
        {
            _channelBalance = state?.Clone();
            _dspProvider?.UpdateChannel(_channelBalance);
        }

        /// <summary>设置安全限幅/余量状态。播放中实时生效。</summary>
        public void SetSafety(DspSafetyState? state)
        {
            _safety = state?.Clone();
            _dspProvider?.UpdateSafety(_safety);
        }

        /// <summary>设置交叉淡化时长（毫秒）。0 = 关闭（无缝硬切）。
        /// 仅对自动连续播放的自然换曲生效（手动切歌会重建会话，不淡化）。播放中调用立即生效。</summary>
        public void SetCrossfade(int milliseconds)
        {
            _crossfadeMs = milliseconds > 0 ? milliseconds : 0;
            _seamless?.SetCrossfade(_crossfadeMs);
        }

        /// <summary>当前交叉淡化时长（毫秒，0=关闭）。</summary>
        public int CrossfadeMs => _crossfadeMs;

        /// <summary>设置 ReplayGain（响度归一化）。播放中实时生效（10ms 平滑）。</summary>
        public void SetReplayGain(ReplayGainState? state, double trackGainDb, double albumGainDb, double peak)
        {
            _rgState = state?.Clone();
            _rgTrackDb = trackGainDb;
            _rgAlbumDb = albumGainDb;
            _rgPeak = peak;
            _dspProvider?.SetReplayGain(state, trackGainDb, albumGainDb, peak);
        }

        /// <summary>读取实时电平快照（post-DSP 信号）到调用方数组。返回是否取到
        /// （未播放、或 DSD/DoP 直出不挂 DSP 链时为 false）。UI 线程调用。</summary>
        public bool TryGetLevels(float[] peakOut, float[] rmsOut)
        {
            LevelMeter? m = _dspProvider?.LevelMeter;
            if (m == null)
            {
                return false;
            }

            m.CopyTo(peakOut, rmsOut);
            return true;
        }

        /// <summary>电平表声道数（0 = 当前无可测电平，如未播放或 DSD 直出）。</summary>
        public int LevelMeterChannels => _dspProvider?.LevelMeter.Channels ?? 0;

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

                var next = OpenWaveInMemory(nextWavPath);
                _seamless.PrepareNext(next);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把 WAV 文件整体读入内存后返回其 WaveFileReader，使 render 实时线程只从内存读、磁盘 I/O 移到播放前/预载时。
        /// 根治低质量 PCM 卡顿：之前 render 每填满一轮 WASAPI 缓冲都在实时线程内同步读盘（SeamlessWaveProvider.Read→WaveFileReader.Read）。
        /// MemoryStream 不 dispose，随 reader 由 GC 回收；reader 的 Position/CurrentTime/Length 语义与文件版完全一致（不影响进度/无缝/播完判定）。</summary>
        private static WaveFileReader OpenWaveInMemory(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var ms = new MemoryStream(data, writable: false);
            return new WaveFileReader(ms);
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
                    ActualOutputFormat = wf.SampleRate + "hz / " + bits + "bit / " + wf.Channels + " 声道";
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }

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
        public static (int Rate, int Channels, int Bits, bool IsFloat)? GetDeviceMixFormat(string? deviceId)
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
                return (mf.SampleRate, mf.Channels, mf.BitsPerSample, mf.Encoding == WaveFormatEncoding.IeeeFloat);
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

        /// <summary>从 PCM WAV 文件以指定模式播放。<paramref name="requireExact"/> 为 true（DSD/DoP 容器 WAV）时独占只做源格式精确直通，禁止降级（保 bit-perfect）。</summary>
        public bool PlayWavAsync(string wavPath, OutputMode mode, string? deviceIdentifier = null, TimeSpan? seekTo = null, bool requireExact = false)
        {
            try
            {
                StopCore();

                if (!File.Exists(wavPath))
                {
                    LastError = "音频缓存文件不存在：\n" + wavPath;
                    return false;
                }

                _waveFile = OpenWaveInMemory(wavPath);
                _activeWavPath = wavPath;
                _activeMode = mode;
                _activeDeviceId = deviceIdentifier;
                // 无缝源（当前+可预加载下一首）：共享/ASIO 走 NAudio wasapi/asio，独占走原生 WASAPI，
                // 均用同一份 SeamlessWaveProvider 做同格式字节级续接 → 单输出会话 gapless。
                _seamless = new SeamlessWaveProvider(_waveFile);
                // 交叉淡化：0 = 关闭，行为与加此功能前一致（同格式字节级无缝续接）
                _seamless.SetCrossfade(_crossfadeMs);
                // 统一 DSP 链（EQ→声道平衡→限幅）：任一激活则包住无缝源使 DSP 在 NAudio(ASIO/共享) 与
                // 原生 WASAPI 独占下都生效（非 bit-perfect）；全部关闭则 _dspProvider=null → 源 PCM 直通。
                _dspProvider = BuildDspProvider();
                // 开启实时电平测量（测量 post-DSP 信号；无 DSP 时只解码测量不改写输出，仍 bit-perfect）。
                // requireExact（DSD/DoP 直出）时独占通道直接读无缝源、不经 DSP 链，测不到也无需测 → 关闭。
                _dspProvider.SetMetering(!requireExact);
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
                        // DSP 链在独占下同样生效：传 _dspProvider（包住无缝源，内部短路直通）。
                        // requireExact（DSD/DoP 直出）强制用源原样，禁止 DSP 破坏 1-bit 容器。
                        var natProvider = requireExact ? (IWaveSourceProvider)_seamless : (IWaveSourceProvider)_dspProvider;
                        if (!nat.Init(natDev, natProvider, requireExactFormat: requireExact))
                        {
                            LastError = nat.LastError ?? "原生 WASAPI 初始化失败";
                            StartupLog.Write("WasapiExclusive 原生初始化失败: " + (nat.LastError ?? "未知") + " | 源格式=" + (_waveFile?.WaveFormat.SampleRate) + "/" + (_waveFile?.WaveFormat.BitsPerSample) + "bit/" + (_waveFile?.WaveFormat.Channels) + "ch");
                            try { Marshal.ReleaseComObject(natDev); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
                            Cleanup();
                            return false;
                        }
                        try { Marshal.ReleaseComObject(natDev); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }

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
                SourceFormatDescription = srcWf.SampleRate + "hz / " + srcWf.BitsPerSample + "bit / " + srcWf.Channels + "声道";
                Position = TimeSpan.Zero;
                _pausedPosition = seekTo ?? TimeSpan.Zero;
                _pendingSeekTarget = null;

                // 播放起始前定位到 seekTo（暂停恢复续播的关键：在输出缓冲填充前 seek，避免跳变爆音）
                if (seekTo != null && seekTo.Value > TimeSpan.Zero)
                {
                    try
                    {
                        _waveFile.CurrentTime = seekTo.Value;
                        Position = seekTo.Value;
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
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
                    // 共享/ASIO 走 NAudio：DSP 链 provider 恒存在（DSP 全关时其内部 _active 短路直通→bit-perfect），
                    // 因此播放中开启/调节 DSP 能实时生效；同格式下一首经无缝源无缝续接（gapless）。
                    _output.Init(_dspProvider);
                    CaptureActualOutputFormat();
                    StartupLog.Write("[DSP] HiFi输出（NAudio）挂载统一 DSP 链（内部短路直通按需启用）");
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

        /// <summary>构建统一 DSP 链（包住无缝源）。始终构建（而非"任一激活才建"），使播放中开启/调节
        /// EQ / 声道平衡 / 限幅时能实时生效（_dspProvider 恒存在，内部 _active 短路直通零开销）。
        /// requireExact（DSD/DoP 直出）由调用处强制用无缝源，不经此链。</summary>
        private ManagedDspSourceProvider BuildDspProvider()
        {
            var dsp = new ManagedDspSourceProvider(_seamless!);
            if (_eqCurve != null)
            {
                dsp.UpdateEqCurve(_eqCurve);
            }
            else
            {
                dsp.UpdateEq(_eqGains);
            }

            dsp.UpdateChannel(_channelBalance);
            dsp.UpdateSafety(_safety);
            dsp.SetReplayGain(_rgState, _rgTrackDb, _rgAlbumDb, _rgPeak);
            // 共享模式当前音量（播放会话重建后同步，避免音量丢失/跳回 100%）；
            // 独占/ASIO 走设备主音量，DSP 链保持全音量（不双重衰减）。
            if (_activeMode != OutputMode.WasapiExclusive && _activeMode != OutputMode.Asio)
            {
                dsp.SetVolumeGain(_resumeVolume);
            }

            return dsp;
        }

        /// <summary>是否为 DSD 文件（DSF/DFF）。</summary>
        private static bool IsDsdFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext is ".dsf" or ".dff";
        }

        /// <summary>
        /// DSD/DoP 原生直出：解析 DSF/DFF → DoPWaveSource → WASAPI 独占（requireExact，禁降级）。
        /// 数据 1-bit 从容器直接抽出并封装为 DoP 容器帧，不经 PCM 解码，也不挂任何 DSP/音量（bit-perfect）。
        /// </summary>
        public bool PlayDsdAsync(string dsdPath, string? deviceIdentifier, TimeSpan? seekTo = null)
        {
            try
            {
                StopCore();

                if (!File.Exists(dsdPath))
                {
                    LastError = "DSD 文件不存在：\n" + dsdPath;
                    return false;
                }

                // 解析 DSD 容器 → 1-bit 流 → DoP 封装
                IDsDDecoder? decoder = DsdDecoderRegistry.Resolve(dsdPath);
                if (decoder == null)
                {
                    LastError = "没有可用的 DSD 解码器（内建解析器不可用）。";
                    StartupLog.Write("DSD 直出失败：无解码器 path=" + dsdPath);
                    return false;
                }

                IDsDStream dsd = decoder.Open(dsdPath);
                int dopBits = AppSettingsStore.Load().DsDoP32 ? 32 : 24;
                var dop = new DoPWaveSource(dsd, dopBits);
                _dsdSource = dop;
                _isDsd = true;
                _activeWavPath = dsdPath;
                _activeMode = OutputMode.WasapiExclusive;
                _activeDeviceId = deviceIdentifier;

                _device = ResolveDeviceForVolume(deviceIdentifier);
                var natDev = NativeWasapi.GetRenderDeviceById(deviceIdentifier);
                if (natDev == null)
                {
                    LastError = "无法解析输出设备。";
                    Cleanup();
                    return false;
                }

                var nat = new NativeWasapiExclusiveOut();
                if (!nat.Init(natDev, dop, requireExactFormat: true))
                {
                    LastError = nat.LastError ?? "DSD/DoP 独占初始化失败";
                    StartupLog.Write("DSD 独占初始化失败: " + LastError
                        + " | DoP容器=" + dop.WaveFormat.SampleRate + "/" + dop.WaveFormat.BitsPerSample + "bit/" + dop.WaveFormat.Channels + "ch"
                        + " | rate=" + dsd.Rate);
                    try { Marshal.ReleaseComObject(natDev); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
                    Cleanup();
                    return false;
                }

                try { Marshal.ReleaseComObject(natDev); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }

                _native = nat;
                _useNative = true;
                _isDsd = true;
                OutputDeviceName = nat.ActualFormatDescription != null
                    ? "WASAPI 独占（DSD/DoP " + nat.ActualFormatDescription + "）"
                    : "WASAPI 独占（DSD/DoP）";
                Duration = dop.TotalTime;
                _sourceDuration = dop.TotalTime;
                Position = TimeSpan.Zero;
                SourceFormatDescription = dsd.Rate + " / " + dsd.Channels + "声道 1-bit DSD";
                _pausedPosition = seekTo ?? TimeSpan.Zero;
                _pendingSeekTarget = null;
                if (seekTo != null && seekTo.Value > TimeSpan.Zero)
                {
                    try { dop.Seek(seekTo.Value); Position = seekTo.Value; } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
                }

                _native.Ended += Native_Ended;
                // 起播前等后台预读线程把 ring 填够预缓冲（对齐 ECHO 的 startupPrebuffer），
                // 避免"边解边播受磁盘/解码速度影响"导致的起播欠载/卡顿（Ring 空时 render 会补 0x69 静音但那是无声间断）。
                dop.WaitForPrefill(TimeSpan.FromMilliseconds(1200));
                if (!_native.Play(_pausedPosition))
                {
                    LastError = _native.LastError ?? "DSD/DoP 播放启动失败";
                    Cleanup();
                    return false;
                }

                ActualOutputFormat = _native.ActualFormatDescription;
                _isPlaying = true;
                CurrentMode = OutputMode.WasapiExclusive;
                _nativePosBaselineFrames = 0;
                _positionTimer.Start();
                StartupLog.Write(string.Format(
                    "DSD直出启动 源={0} {1}/{2}ch 1-bit → DoP容器={3}Hz/{4}bit/{5}ch → 设备=[{6}] | bit-perfect，CPU DSP/音量已绕过",
                    Path.GetFileName(dsdPath), dsd.Rate, dsd.Channels,
                    dop.WaveFormat.SampleRate, dop.WaveFormat.BitsPerSample, dop.WaveFormat.Channels,
                    ActualOutputFormat ?? OutputDeviceName ?? "?"));
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
            _pausedPosition = _pendingSeekTarget ?? Position;
            _pendingSeekTarget = null;
            // 记录暂停瞬间的真实设备主音量（此时设备音量仍是用户当前设定），供恢复时回到该值
            _pausedDeviceVol = GetDeviceVolume();

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
            // （独占下音量完全由 Windows 托盘/DAC 物理键控制，程序不写设备主音量，避免多次 select/暂停音量跳变到 0/100。）

            // 用缓存的激活参数完全重建输出，并在启动缓冲前定位到暂停点
            TimeSpan resumeAt = _pausedPosition;
            bool ok;
            if (_isDsd || IsDsdFile(_activeWavPath ?? ""))
            {
                ok = PlayDsdAsync(_activeWavPath ?? "", _activeDeviceId, resumeAt);
            }
            else
            {
                ok = PlayWavAsync(_activeWavPath, _activeMode, _activeDeviceId, resumeAt);
            }

            if (!ok)
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
                // 播放中 seek：交由 render 线程安全重定位（避免与正在读源的线程直接竞争）。
                // 记录待消费目标，避免随后 updatePosition/Pause 用尚未更新的 FramesWritten 把它覆盖（选进度后进度条不变/从头重播）。
                _pendingSeekTarget = position;
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
            catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
        }

        /// <summary>暂停前记录的真实设备主音量（供恢复使用）；未暂停或无设备返回 -1。</summary>
        public float GetPausedDeviceVolume() => _pausedDeviceVol;

        /// <summary>当前设备主音量标量 0..1；无设备/ASIO 返回 -1（未知）。</summary>
        public float GetDeviceVolume()
        {
            if (_device?.AudioEndpointVolume != null)
            {
                try
                {
                    return _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
            }

            return -1f;
        }

        public void SetVolume(float volume)
        {
            _resumeVolume = Math.Clamp(volume, 0f, 1f);
            bool hifiDevice = CurrentMode == OutputMode.WasapiExclusive || CurrentMode == OutputMode.Asio;

            // 独占/ASIO（及 DSD 直出）：bit-perfect，控设备主音量——滑块 100%→满、其它非线性压缩（slider²）。
            if (hifiDevice || (_isDsd))
            {
                if (_device?.AudioEndpointVolume != null)
                {
                    try
                    {
                        float dev = _resumeVolume * _resumeVolume;
                        _device.AudioEndpointVolume.MasterVolumeLevelScalar = dev;
                    }
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
                }

                return;
            }

            // 共享模式：NAudio WasapiOut.Volume 抛 NotSupportedException（不支持软件音量），
            // 必须经 DSP 链做采样级增益（只影响本播放器，绝不动系统音/设备主音量，避免"共享音量突然变大"）。
            _dspProvider?.SetVolumeGain(_resumeVolume);
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
                if (_isDsd)
                {
                    // DSD/DoP：无 WaveFileReader，用独占写帧数 / 容器帧率换算绝对进度
                    int rate = _native.SampleRateValue;
                    if (_pendingSeekTarget is TimeSpan t)
                    {
                        // 播放中 seek 目标尚未被 render 消费（FramesWritten 未更新）：显示 seek 目标，
                        // 待 FramesWritten 追上目标后清除，恢复正常进度，避免选进度后进度条不动/回头。
                        Position = t;
                        if (rate > 0 && _native.FramesWritten >= (long)(t.TotalSeconds * rate))
                        {
                            _pendingSeekTarget = null;
                        }
                    }
                    else
                    {
                        Position = rate > 0 ? TimeSpan.FromSeconds((double)_native.FramesWritten / rate) : TimeSpan.Zero;
                    }

                    // 诊断：DSD ring 预读欠载补静音统计（>0 说明预读跟不上/起播冷启动→短暂无声，是潜在卡顿点）。
                    // 限频记录，便于与"雪花/卡顿"音频现象对照定位根因。
                    if (_dsdSource != null && Position.TotalSeconds - _lastDsdPrefillLogSec > 2.0 && _dsdSource.PrefillFrames > 0)
                    {
                        _lastDsdPrefillLogSec = Position.TotalSeconds;
                        StartupLog.Write($"[DSD诊断] ring预读欠载补静音帧累计={_dsdSource.PrefillFrames} (t={Position.TotalSeconds:F1}s)");
                    }
                }
                else
                {
                    var curReader = _seamless?.Current ?? _waveFile;
                    Position = curReader != null ? curReader.CurrentTime : TimeSpan.Zero;
                }
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
            if (_isDsd && _native != null)
            {
                // DSD：无 WaveFileReader，用播放位置≥源总时长判定播完（触发 Stop→下层切下一首）
                sourceExhausted = Duration > TimeSpan.Zero && Position >= Duration;
            }
            else if (_waveFile != null && _waveFile.Length > 16
                && (!_useNative || (_native?.IsStarted == true))) // native 模式下仅在渲染线程真正启动时判定，避免重建窗口期的旧 reader 误判为已读尽
            {
                // 数据源已真实读到末尾（最可靠，避免依赖被源时长改短的 Duration；DSD 转码 WAV 读尽即播完）。
                // 阈值与 SeamlessWaveProvider.HasReadyNext 的 -8 对齐，避免"源已读尽但续接尚未标记"的窗口被误判为需要重建。
                // 要求 Position>0 且 Length>16，避免空/极小缓存文件（Length 异常小）在开播即被误判为已读尽。
                sourceExhausted = _waveFile.Position >= _waveFile.Length - 8 && _waveFile.Position > 0;
            }

            // 修复：外层判定原来要求 _waveFile!=null，导致 DSD(_waveFile 恒 null) 播完(sourceExhausted)永不触发
            // Stop→自动切下一首，进度条延续不切歌。改为仅要求 Duration>0 且 sourceExhausted（DSD 走 _native.Stop，PCM 走 _waveFile/_output）。
            if (Duration > TimeSpan.Zero && sourceExhausted)
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
                    catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
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
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }

                try
                {
                    _output.Dispose();
                }
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
            }

            _output = null;
            if (_native != null)
            {
                try { _native.Ended -= Native_Ended; _native.Stop(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
                try { _native.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
                _native = null;
            }
            _useNative = false;
            _dsdSource?.Dispose();
            _dsdSource = null;
            _isDsd = false;
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
                catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }

                _output = null;
            }

            if (_native != null)
            {
                try { _native.Ended -= Native_Ended; _native.Stop(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
                try { _native.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("HiFiOutputBackend.cs", caught); }
                _native = null;
            }
            _useNative = false;
            _dsdSource?.Dispose();
            _dsdSource = null;
            _isDsd = false;
            _dspProvider = null;
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
