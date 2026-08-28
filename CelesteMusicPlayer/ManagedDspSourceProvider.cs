using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 统一直管 DSP 源：包装 <see cref="SeamlessWaveProvider"/>，在 Read 时对源 PCM 依次做
    /// 「10段EQ → 声道平衡 → 安全限幅(headroom + soft-clip)」。同时实现 <see cref="IWaveSourceProvider"/>
    /// 与 <see cref="IWaveProvider"/>，因此既可用于 NAudio（ASIO/共享）输出，也可传给原生 WASAPI 独占
    /// 渲染线程 —— 让 DSP 在独占输出下同样生效。
    ///
    /// 任一 DSP 激活即非 bit-perfect（与 ECHO 的界定一致）；全部直通时数值直通。
    /// DSD/DoP 直出（requireExact）路径不使用本 provider，保持 1-bit 直通。
    /// </summary>
    internal sealed class ManagedDspSourceProvider : IWaveProvider, IWaveSourceProvider
    {
        private readonly IWaveSourceProvider _source;
        private readonly WaveFormat _format;
        private readonly int _channels;
        private readonly bool _isFloat;

        // EQ：动态 band 滤波链（按 band 列表/类型重建，render 线程整数组原子读，UI 线程原子替换）
        private volatile BiQuadFilter[][] _eqFilters = Array.Empty<BiQuadFilter[]>();
        private double _preampGain = 1.0; // preamp（double 不能 volatile；仅在 EQ 状态更新时变化，读侧极端误差可忽略）
        private bool _eqEnabled;

        // 声道平衡参数
        private bool _chEnabled;
        private bool _chSwap, _chInvL, _chInvR, _chMono, _chMonoLeft, _chMonoRight;
        private double _gainL, _gainR;

        // 安全限幅
        private double _headroomDb;       // 负值预衰减余量
        private double _headroomGain;     // 当前平滑中的增益
        private double _headroomStep;     // 每样本步进
        private int _headroomSmoothLeft;  // 剩余平滑样本
        private int _headroomSmoothTotal = 22050; // ~0.5s@44.1k，默认
        private const int SmoothingMs = 50; // 快速但无爆音
        private bool _limiterEnabled;
        private volatile bool _active; // 任意 DSP 生效（EQ/声道/headroom/limiter）→ 决定 Read 是否走 ProcessBlock

        // 实时电平表：测量 post-DSP 信号（实际送往输出的信号）的每声道峰值/RMS。
        // 默认关闭（零开销、保持 bit-perfect）；播放开始时被开启。
        private readonly LevelMeter _levelMeter = new();
        private bool _meterEnabled;

        // 软件总音量（共享/ASIO 用，采样级增益；NAudio WasapiOut.Volume 不支持，故由 DSP 链实现）。
        private volatile float _volumeGain = 1f;

        // ReplayGain 响度归一化（对齐 ECHO ReplayGainProcessor：目标增益 + 10ms 平滑渐变）
        private double _rgTargetDb;
        private double _rgCurrentDb;
        private int _rgRampLeft;
        private int _rgRampTotal = 441; // ~10ms @44.1k，构造时按采样率重算
        private bool _rgActive;

        // 房间校正（卷积 FIR）：链首处理（音量/EQ 之前）。_convolver 原子替换（volatile），播放线程读取。
        private volatile StreamingPartitionedConvolver? _convolver;
        private volatile bool _convEnabled;
        private float _convGain = 1f; // 线性增益（由 GainDb 换算；状态更新时变化，读侧极端误差可忽略）

        public ManagedDspSourceProvider(IWaveSourceProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _format = source.WaveFormat;
            _channels = _format.Channels;
            _isFloat = _format.Encoding == WaveFormatEncoding.IeeeFloat;

            if (_format.SampleRate > 0)
            {
                _headroomSmoothTotal = Math.Max(1, (int)(_format.SampleRate * SmoothingMs / 1000.0));
                _rgRampTotal = Math.Max(1, (int)(_format.SampleRate * 0.01)); // ~10ms
            }

            _headroomGain = 1.0;
        }

        #region 状态更新（播放中调用，下一次 Read 生效）

        /// <summary>兼容旧 10 段 EQ（独立 EQ 窗口用）：组装为动态曲线状态再走统一引擎。</summary>
        public void UpdateEq(double[]? gainsDb)
        {
            var curve = new EqCurveState { Enabled = true, PreampDb = 0, PresetId = "custom", PresetName = "自定义" };
            double[] stdFreq = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
            if (gainsDb != null)
            {
                for (int i = 0; i < stdFreq.Length; i++)
                {
                    double g = i < gainsDb.Length ? gainsDb[i] : 0.0;
                    if (Math.Abs(g) <= 0.01) continue;
                    curve.Bands.Add(new EqBand { Enabled = true, FrequencyHz = stdFreq[i], GainDb = g, Q = 1.0, FilterType = EqFilterType.Peaking });
                }
            }

            if (curve.Bands.Count == 0) { _eqEnabled = false; _eqFilters = Array.Empty<BiQuadFilter[]>(); RefreshActive(); return; }
            UpdateEqCurve(curve);
        }

        /// <summary>应用动态 EQ 曲线状态（band 列表 + preamp）。null / 无效果 → 关闭 EQ。
        /// 播放中调用：整数组原子替换滤波链（volatile），render 线程下一次 Read 生效，无锁、可丝滑实时调节。</summary>
        public void UpdateEqCurve(EqCurveState? curve)
        {
            bool on = curve != null && curve.HasEffect();
            if (!on)
            {
                _eqEnabled = false;
                _preampGain = 1.0;
                _eqFilters = Array.Empty<BiQuadFilter[]>();
                RefreshActive();
                return;
            }

            // 自动峰值余量补偿：估算所有启用 band 在频域的最大叠加增益 peakDb，
            // 当用户未手动设 preamp 时，自动施加负余量把输出压回 0dB，避免极端增益（如 +10dB 低频增强）
            // 触发 biQuad 过冲后只能靠削波产生爆音（对齐 ECHO 自动增益/余量思路）。
            double peakDb = EstimateEqPeakDb(curve);
            double autoCompDb = -Math.Max(0, peakDb);
            autoCompDb = Math.Clamp(autoCompDb, -12, 0);
            double userPre = Math.Abs(curve!.PreampDb) > 0.01 ? Math.Pow(10.0, Math.Clamp(curve.PreampDb, -24, 24) / 20.0) : 1.0;
            if (Math.Abs(curve.PreampDb) > 0.01)
            {
                // 用户手动 set 了 preamp：尊重用户设定，但叠加的自动余量也纳入（仍防削波）
                _preampGain = userPre * Math.Pow(10.0, autoCompDb / 20.0);
            }
            else
            {
                _preampGain = Math.Pow(10.0, autoCompDb / 20.0);
            }

            // 按声道数建立独立滤波链：每个声道各持一组全新的 BiQuadFilter 实例。
            // BiQuadFilter.Transform 是有状态的，共用实例会把交错多声道当成更高采样率单声道，
            // 导致频响偏移（立体声下一个八度，多声道更多）。此前只建了 L/R 两条链，
            // 使得 5.1 等多声道的第 3~6 声道完全不过 EQ。这里按 _channels 建链，所有声道都独立滤波。
            int chCount = Math.Max(1, _channels);
            var chains = new BiQuadFilter[chCount][];
            for (int c = 0; c < chCount; c++)
            {
                var list = new List<BiQuadFilter>();
                foreach (var band in curve.Bands)
                {
                    if (band is not { Enabled: true }) continue;
                    var f = BuildBandFilter(band);
                    if (f != null) list.Add(f);
                }

                chains[c] = list.ToArray();
            }

            _eqFilters = chains;
            _eqEnabled = true;
            RefreshActive();
        }

        /// <summary>估算一组 EQ band 在频域的最大叠加增益（dB）。保守起见对 peak 用带宽高斯近似、架/滤子做简化求和。</summary>
        private static double EstimateEqPeakDb(EqCurveState curve)
        {
            double peak = 0;
            if (curve.Bands.Count == 0) return 0;
            double fMin = 20, fMax = 20000;
            double dAdd = Math.Pow(fMax / fMin, 1.0 / 200.0);
            double f = fMin;
            for (int i = 0; i <= 200; i++)
            {
                double g = 0;
                foreach (var b in curve.Bands)
                {
                    if (b is not { Enabled: true }) continue;
                    g += ApproxBandGain(f, b);
                }

                if (g > peak) peak = g;
                f *= dAdd;
            }

            return peak;
        }

        /// <summary>单段在给定频率处的近似幅度增益（dB）——用于峰值估算，与曲线绘制用同一近似。</summary>
        private static double ApproxBandGain(double freq, EqBand b)
        {
            if (Math.Abs(b.GainDb) < 0.01 && b.FilterType is not (EqFilterType.LowPass or EqFilterType.HighPass or EqFilterType.Notch))
            {
                if (b.FilterType is EqFilterType.Peaking or EqFilterType.LowShelf or EqFilterType.HighShelf) return 0;
            }

            switch (b.FilterType)
            {
                case EqFilterType.LowPass:
                {
                    double cutoff = Math.Max(20, b.FrequencyHz);
                    if (freq >= cutoff) { double x = freq / cutoff; return -6.0 * Math.Log10(1 + x * x); }
                    return 0;
                }
                case EqFilterType.HighPass:
                {
                    double hc = Math.Max(20, b.FrequencyHz);
                    if (freq <= hc) { double x = hc / freq; return -6.0 * Math.Log10(1 + x * x); }
                    return 0;
                }
                case EqFilterType.Notch:
                {
                    double d = Math.Abs(System.Math.Log(freq / b.FrequencyHz));
                    double n = b.Q <= 0 ? 1 : b.Q;
                    if (d <= 0.5 / n) return -6 * Math.Min(1, (0.5 / n - d) * n * 2);
                    return 0;
                }
                case EqFilterType.LowShelf:
                case EqFilterType.HighShelf:
                {
                    double gain = Math.Clamp(b.GainDb, -24, 24);
                    double extent = Math.Abs(System.Math.Log(freq / Math.Max(20, b.FrequencyHz)));
                    return gain * Math.Clamp(1.0 / (1.0 + 0.5 * extent), 0.2, 1.0);
                }
                default: // Peaking
                {
                    if (Math.Abs(b.GainDb) < 0.01) return 0;
                    double w = Math.Max(20, b.FrequencyHz);
                    double x = System.Math.Log10(freq / w) * 6; // octave 尺度
                    double q = Math.Max(0.1, b.Q);
                    double sigma = q > 0 ? 0.7 * (1.0 / q) : 0.6; // octaves
                    return b.GainDb * System.Math.Exp(-(x * x) / (2 * sigma * sigma));
                }
            }
        }

        /// <summary>把单条 band 构造成 BiQuad 滤波器（按类型）。</summary>
        private BiQuadFilter? BuildBandFilter(EqBand band)
        {
            float sr = _format.SampleRate;
            float freq = (float)Math.Clamp(band.FrequencyHz, 20, sr * 0.45f);
            float q = (float)Math.Clamp(band.Q, 0.1, 24);
            float gain = (float)Math.Clamp(band.GainDb, -24, 24);
            switch (band.FilterType)
            {
                case EqFilterType.Peaking: return BiQuadFilter.PeakingEQ(sr, freq, q, gain);
                case EqFilterType.LowShelf: return BiQuadFilter.LowShelf(sr, freq, q, gain);
                case EqFilterType.HighShelf: return BiQuadFilter.HighShelf(sr, freq, q, gain);
                case EqFilterType.LowPass: return BiQuadFilter.LowPassFilter(sr, freq, q);
                case EqFilterType.HighPass: return BiQuadFilter.HighPassFilter(sr, freq, q);
                case EqFilterType.Notch: return BiQuadFilter.NotchFilter(sr, freq, q);
                default: return BiQuadFilter.PeakingEQ(sr, freq, q, gain);
            }
        }

        public void UpdateChannel(ChannelBalanceState? state)
        {
            if (state == null || !state.Enabled)
            {
                _chEnabled = false;
                return;
            }

            _chEnabled = true;
            _chSwap = state.SwapChannels;
            _chInvL = state.InvertLeft;
            _chInvR = state.InvertRight;
            _chMono = !string.Equals(state.MonoMode, "off", StringComparison.Ordinal);
            _chMonoLeft = string.Equals(state.MonoMode, "left", StringComparison.Ordinal);
            _chMonoRight = string.Equals(state.MonoMode, "right", StringComparison.Ordinal);

            double balance = Math.Clamp(state.Balance, -1.0, 1.0);
            double panL = balance <= 0 ? 1.0 : Math.Cos(balance * Math.PI / 2.0);
            double panR = balance >= 0 ? 1.0 : Math.Cos(balance * Math.PI / 2.0);
            double lg = Math.Pow(10.0, Math.Clamp(state.LeftGainDb, -12.0, 12.0) / 20.0);
            double rg = Math.Pow(10.0, Math.Clamp(state.RightGainDb, -12.0, 12.0) / 20.0);
            _gainL = lg * panL;
            _gainR = rg * panR;
            RefreshActive();
        }

        public void UpdateSafety(DspSafetyState? state)
        {
            double hd = state?.HeadroomDb ?? 0.0;
            hd = Math.Clamp(hd, -12.0, 0.0);
            double targetGain = Math.Pow(10.0, hd / 20.0);
            _headroomDb = hd;
            _limiterEnabled = state?.EnableLimiter ?? true;
            _headroomSmoothTotal = _format.SampleRate > 0 ? Math.Max(1, (int)(_format.SampleRate * SmoothingMs / 1000.0)) : 1;
            if (Math.Abs(_headroomGain - targetGain) > 1e-5)
            {
                _headroomSmoothLeft = _headroomSmoothTotal;
                _headroomStep = (targetGain - _headroomGain) / _headroomSmoothTotal;
            }

            RefreshActive();
        }

        /// <summary>设置 ReplayGain（对齐 ECHO ReplayGainProcessor.setConfig）。
        /// 传每曲的 track/album 增益（dB）与 peak（线性）。按 state.Mode 选增益、叠加 preamp，
        /// preventClipping 时若 peak×gain&gt;1 则截断到不削波的最大增益。mode=Off 目标 0dB 旁路。
        /// 播放中可实时切换（10ms 平滑渐变，无爆音）。</summary>
        public void SetReplayGain(ReplayGainState? state, double trackGainDb, double albumGainDb, double peak)
        {
            double gainDb = 0;
            bool active = false;
            bool preventClipping = state?.PreventClipping ?? true;
            double preampDb = state?.PreampDb ?? 0.0;

            if (state != null && state.Mode == ReplayGainMode.Track)
            {
                active = true;
                gainDb = trackGainDb;
            }
            else if (state != null && state.Mode == ReplayGainMode.Album)
            {
                active = true;
                gainDb = albumGainDb;
            }

            gainDb += preampDb;

            // 防削波：若 peak × 线性增益 > 1，把增益压到最大不削波值（对齐 ECHO）
            if (active && preventClipping && peak > 0.0)
            {
                double appliedLinear = Math.Pow(10.0, gainDb / 20.0);
                if (peak * appliedLinear > 1.0)
                {
                    double maxGain = 1.0 / peak;
                    double maxGainDb = 20.0 * Math.Log10(maxGain);
                    if (gainDb > maxGainDb) gainDb = maxGainDb;
                }
            }

            _rgActive = active;
            _rgTargetDb = gainDb;
            if (_rgRampLeft <= 0)
            {
                _rgCurrentDb = gainDb; // 从未生效时直接到目标，避免开播瞬间渐变
            }
            else
            {
                _rgRampLeft = _rgRampTotal; // 平滑渐进
            }

            RefreshActive();
        }

        /// <summary>任意 DSP 是否激活（UI 据此提示"非 bit-perfect"）。</summary>
        public bool IsActive => _active;

        /// <summary>重算 DSP 是否生效；当无任一 DSP 生效时后续 Read 直接直通（bit-perfect，零逐样本开销）。</summary>
        private void RefreshActive()
        {
            _active = _eqEnabled || _chEnabled || _limiterEnabled || _rgActive || Math.Abs(_headroomDb) > 0.001
                || Math.Abs(_volumeGain - 1f) > 0.0001f || _convEnabled;
        }

        /// <summary>设置采样级总音量基因（共享/ASIO 软件音量），0..2。音量=1 时不进 Processing。</summary>
        public void SetVolumeGain(float gain)
        {
            _volumeGain = Math.Clamp(gain, 0f, 2f);
            RefreshActive();
        }

        /// <summary>
        /// 设置房间校正（卷积 FIR）。state 为 null / 未启用 / 无 IR 路径 → 关闭卷积。
        /// IR 从 <see cref="RoomCorrectionIrCache"/> 取（没有则加载并缓存），构造分区卷积器后原子替换。
        /// 播放中调用：下一次 Read 生效；换 IR 会重置卷积流式状态（短暂衔接差异可接受）。
        /// </summary>
        public void SetRoomCorrection(RoomCorrectionState? state)
        {
            if (state == null || !state.Enabled || string.IsNullOrWhiteSpace(state.IrPath))
            {
                _convEnabled = false;
                _convolver = null;
                RefreshActive();
                return;
            }

            try
            {
                float[][]? ir = RoomCorrectionIrCache.GetOrLoad(state.IrPath, _format.SampleRate);
                if (ir == null)
                {
                    _convEnabled = false;
                    _convolver = null;
                    RefreshActive();
                    return;
                }

                var conv = new StreamingPartitionedConvolver(ir, _channels);
                _convGain = (float)Math.Pow(10.0, Math.Clamp(state.GainDb, -24.0, 24.0) / 20.0);
                _convolver = conv;
                _convEnabled = true;
                RefreshActive();
            }
            catch
            {
                _convEnabled = false;
                _convolver = null;
                RefreshActive();
            }
        }

        #endregion

        #region IWaveSourceProvider

        public WaveFormat WaveFormat => _format;

        public TimeSpan TotalTime => _source.TotalTime;

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState => _source.ProbeCurrentState;

        public bool NextMounted => _source.NextMounted;

        public void Seek(TimeSpan position) => _source.Seek(position);

        #endregion

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0)
            {
                return read;
            }

            if (_active)
            {
                ProcessBlock(buffer, offset, read);
                return read;
            }

            // 无 DSP 生效 → 全部直通（bit-perfect）；若电平表开启则额外测量，
            // 但只解码到临时 float 缓冲测量，绝不改写输出缓冲 → 仍严格 bit-perfect。
            if (_meterEnabled)
            {
                MeasurePassthrough(buffer, offset, read);
            }

            return read;
        }

        private void ProcessBlock(byte[] b, int offset, int count)
        {
            int block = _format.BlockAlign;
            int bytesPerChannel = _format.BitsPerSample / 8;
            if (block <= 0 || bytesPerChannel <= 0)
            {
                return;
            }

            // 循环前把状态快照到局部（避免每样本访问字段/属性），显著降低托管吞吐开销
            bool doEq = _eqEnabled;
            bool doCh = _chEnabled;
            bool doHeadroom = Math.Abs(_headroomDb) > 0.001;
            bool doLimiter = _limiterEnabled;
            bool doConv = _convEnabled;
            StreamingPartitionedConvolver? convolver = _convolver;
            float convGain = _convGain;
            bool chSwap = _chSwap, chInvL = _chInvL, chInvR = _chInvR, chMono = _chMono, monoL = _chMonoLeft, monoR = _chMonoRight;
            bool isFloat = _isFloat;
            int bits = _format.BitsPerSample;
            double gainL = _gainL, gainR = _gainR;
            bool stereo = _channels >= 2;
            bool wantsClip = doHeadroom || doLimiter || doEq || doCh || _rgActive; // 只要有任何 DSP 即需 Clamp 保护
            float preampGain = (float)_preampGain;
            float volumeGain = _volumeGain;
            bool rgActive = _rgActive;
            double rgTargetDb = _rgTargetDb;
            int rgRampLeft = _rgRampLeft;
            int rgRampTotal = _rgRampTotal;
            double rgCurrentDb = _rgCurrentDb;
            int rgt = rgRampTotal > 0 ? rgRampTotal : 1;

            int frames = count / block;
            int ch = _channels;
            int n = frames * ch;

            // 批量浮点处理显著降低整数字节编解码 + 分支开销（独占 352800Hz 高采样下是关键优化）
            if (n <= 0)
            {
                return;
            }

            float[] buf = GetTempFloatBuffer(n);
            // 解码：byte → float（抽成独立方法，电平表测量与 DSP 共用）
            DecodeToFloat(b, offset, n, buf);

            // 房间校正（卷积 FIR）：链首块级处理（在音量/EQ 之前），流式分区卷积 in-place。
            // 卷积引入 BlockSize(1024) 帧延迟，但输出逐帧连续，对实时播放正确。
            if (doConv && convolver != null)
            {
                convolver.Process(buf, frames, ch);
                if (convGain != 1f)
                {
                    for (int i = 0; i < n; i++)
                    {
                        buf[i] *= convGain;
                    }
                }
            }

            // 逐帧 DSP。
            // 多声道：所有声道都经过「音量 → EQ → ReplayGain → Headroom → 限幅」这套全局 DSP；
            // 仅当立体声（前两个声道）时再做声道处理（交换 / mono / 反相 / 左右增益）。
            // 旧实现只处理 L/R 两声道，5.1 等多声道的第 3~6 声道完全不过任何 DSP。
            BiQuadFilter[][] eq = _eqFilters;
            int eqChains = eq.Length;

            // ReplayGain 与 Headroom 的渐变进度按「每帧」推进一次（不随声道数放大）。
            // 旧实现把这套推进写在声道循环里、随声道数翻倍，多声道下渐变速度会与预期不符。
            if (rgActive)
            {
                if (rgRampLeft > 0)
                {
                    rgRampLeft--;
                    rgCurrentDb += (rgTargetDb - rgCurrentDb) / rgt;
                }
                else
                {
                    rgCurrentDb = rgTargetDb;
                }
            }

            float rgg = rgActive ? (float)Math.Pow(10.0, rgCurrentDb / 20.0) : 1f;

            if (doHeadroom)
            {
                if (_headroomSmoothLeft > 0)
                {
                    _headroomGain += _headroomStep;
                    _headroomSmoothLeft--;
                }
            }

            float hg = doHeadroom ? (float)_headroomGain : 1f;

            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * ch;
                for (int c = 0; c < ch; c++)
                {
                    int idx = baseIdx + c;
                    float s = buf[idx];

                    // 软件总音量（采样级增益，恒在 EQ/声道/RG 之前）
                    if (volumeGain != 1f) s *= volumeGain;

                    if (doEq)
                    {
                        BiQuadFilter[] chain = eq[c < eqChains ? c : 0];
                        for (int k = 0; k < chain.Length; k++) s = chain[k].Transform(s);
                        if (preampGain != 1f) s *= preampGain;
                    }

                    if (rgActive) s *= rgg;
                    if (doHeadroom) s *= hg;
                    if (doLimiter) s = SoftLimit(s);
                    else if (wantsClip) s = SoftLimit(s);

                    buf[idx] = s;
                }

                // 声道处理：仅立体声前两声道有意义
                if (doCh && stereo)
                {
                    int li = baseIdx, ri = baseIdx + 1;
                    float l = buf[li], r = buf[ri];
                    if (chSwap) (l, r) = (r, l);
                    if (chMono)
                    {
                        float m = monoL ? l : monoR ? r : (l + r) * 0.5f;
                        l = m; r = m;
                    }

                    l = (float)(l * gainL);
                    r = (float)(r * gainR);
                    if (chInvL) l = -l;
                    if (chInvR) r = -r;
                    buf[li] = l;
                    buf[ri] = r;
                }
            }

            // 实时电平：测量 post-DSP 信号（即实际送往输出的样本），供 UI 电平条显示
            if (_meterEnabled) _levelMeter.Update(buf, n, ch);

            // 回写 RG 渐变进度（供下一次 block 继续）
            _rgCurrentDb = rgCurrentDb;
            _rgRampLeft = Math.Max(0, rgRampLeft);

            // 编码：float → byte
            if (isFloat)
            {
                int bo = offset;
                for (int i = 0; i < n; i++, bo += 4) BitConverter.GetBytes(buf[i]).CopyTo(b, bo);
            }
            else if (bits == 32)
            {
                int bo = offset;
                for (int i = 0; i < n; i++, bo += 4)
                {
                    int v = (int)(buf[i] * 2147483647f);
                    b[bo] = (byte)(v & 0xFF); b[bo + 1] = (byte)((v >> 8) & 0xFF);
                    b[bo + 2] = (byte)((v >> 16) & 0xFF); b[bo + 3] = (byte)((v >> 24) & 0xFF);
                }
            }
            else if (bits == 24)
            {
                int bo = offset;
                for (int i = 0; i < n; i++, bo += 3)
                {
                    int v = (int)(buf[i] * 8388607f);
                    b[bo] = (byte)(v & 0xFF); b[bo + 1] = (byte)((v >> 8) & 0xFF); b[bo + 2] = (byte)((v >> 16) & 0xFF);
                }
            }
            else
            {
                int bo = offset;
                for (int i = 0; i < n; i++, bo += 2)
                {
                    short s = (short)(buf[i] * 32767f);
                    b[bo] = (byte)(s & 0xFF); b[bo + 1] = (byte)((s >> 8) & 0xFF);
                }
            }
        }

        // 复用临时 float 缓冲，避免独占 render 高频分配 GC
        private float[] _tempFloatBuf = Array.Empty<float>();

        private float[] GetTempFloatBuffer(int n)
        {
            if (_tempFloatBuf.Length < n) _tempFloatBuf = new float[n * 2];
            return _tempFloatBuf;
        }

        #region 实时电平表（测量 post-DSP 信号）

        /// <summary>开启/关闭电平测量。开启时按声道数重置内部缓冲（播放会话开始时调用）。
        /// 关闭时 Read 完全不做解码，恢复零开销 bit-perfect 直通。</summary>
        public void SetMetering(bool enabled)
        {
            _meterEnabled = enabled;
            if (enabled) _levelMeter.Reset(_channels);
        }

        /// <summary>电平表实例（渲染线程写、UI 线程读，内部有锁）。</summary>
        public LevelMeter LevelMeter => _levelMeter;

        /// <summary>byte → float 解码（支持 float / 32bit / 24bit / 16bit），供 DSP 与电平测量共用。</summary>
        private void DecodeToFloat(byte[] b, int offset, int n, float[] buf)
        {
            bool isFloat = _isFloat;
            int bits = _format.BitsPerSample;
            if (isFloat)
            {
                int bi = offset;
                for (int i = 0; i < n; i++, bi += 4) buf[i] = BitConverter.ToSingle(b, bi);
            }
            else if (bits == 32)
            {
                int bi = offset;
                for (int i = 0; i < n; i++, bi += 4) buf[i] = (b[bi] | (b[bi + 1] << 8) | (b[bi + 2] << 16) | (b[bi + 3] << 24)) / 2147483648f;
            }
            else if (bits == 24)
            {
                int bi = offset;
                for (int i = 0; i < n; i++, bi += 3)
                {
                    int v = b[bi] | (b[bi + 1] << 8) | (b[bi + 2] << 16);
                    if ((b[bi + 2] & 0x80) != 0) v |= unchecked((int)0xFF000000);
                    buf[i] = v / 8388608f;
                }
            }
            else // 16
            {
                int bi = offset;
                for (int i = 0; i < n; i++, bi += 2) buf[i] = (short)(b[bi] | (b[bi + 1] << 8)) / 32768f;
            }
        }

        /// <summary>bit-perfect 直通路径下的电平测量：解码到临时 float 缓冲后更新电平表，
        /// 不改写输出缓冲，保证输出严格 bit-perfect。</summary>
        private void MeasurePassthrough(byte[] b, int offset, int count)
        {
            int block = _format.BlockAlign;
            int bpc = _format.BitsPerSample / 8;
            if (block <= 0 || bpc <= 0)
            {
                return;
            }

            int frames = count / block;
            int ch = _channels;
            int n = frames * ch;
            if (n <= 0)
            {
                return;
            }

            float[] buf = GetTempFloatBuffer(n);
            DecodeToFloat(b, offset, n, buf);
            _levelMeter.Update(buf, n, ch);
        }

        #endregion

        /// <summary>软削波（soft-knee limiter）：对接近 ±1 的样本渐近饱和而非瞬时削平，
        /// 避免参数增益过大时硬削产生的谐波爆音/电流杂音（对齐 ECHO 安全限幅思路）。
        /// |x|≤0.9 完全线性（无失真），0.9~∞ 平滑压缩到 ±1。</summary>
        private static float SoftLimit(float s)
        {
            if (!float.IsFinite(s)) s = 0f;
            float m = Math.Abs(s);
            if (m <= 0.9f) return s;
            float k = (m - 0.9f) / 0.1f;
            // k≥0；用 1/(1+k) 而非 tanh，避免 fast-math/性能开销且曲线足够柔
            float compressed = 0.9f + 0.1f * (1f - 1f / (1f + k));
            return s > 0 ? compressed : -compressed;
        }
    }
}