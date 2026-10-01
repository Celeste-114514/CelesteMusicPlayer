using System;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 统一直管 DSP 源：包装 <see cref="SeamlessWaveProvider"/>，在 Read 时对源 PCM 依次做
    /// 「软件音量 → celeste_dsp_core 原生机架（EQ→卷积→ReplayGain→压缩→Crossfeed→立体声场→
    /// 声道矩阵→声道平衡→Headroom→安全限幅）→ 电平表/频谱测量 → TPDF dither 回写」。
    /// 机架运算全部在原生内核（C ABI，见 <see cref="DspCoreInterop"/>），本类只负责
    /// 交错 PCM ↔ float 编解码、软件音量、测量与生命周期管理。
    ///
    /// 任一 DSP 激活即非 bit-perfect（与 ECHO 的界定一致）；全部直通时数值直通
    /// （本类不进 ProcessBlock，内核同样不参与）。DSD/DoP 直出（requireExact）路径
    /// 不使用本 provider，保持 1-bit 直通。
    ///
    /// 内核创建失败（DLL 缺失等）时本类安全降级：所有 Update 变 no-op、_active 恒 false，
    /// 输出退回 bit-perfect 直通并写日志 —— 绝不抛异常穿到音频渲染线程。
    /// </summary>
    internal sealed class ManagedDspSourceProvider : IWaveProvider, IWaveSourceProvider
    {
        private readonly IWaveSourceProvider _source;
        private readonly WaveFormat _format;
        private readonly int _channels;
        private readonly bool _isFloat;

        /// <summary>原生内核是否就绪（create 成功）。false = DSP 全关直通。</summary>
        private bool _engineReady;

        /// <summary>内核 process 出错后只记一次日志，避免渲染线程每块刷屏。</summary>
        private bool _processErrLogged;

        // 各模块「UI 意图」开关（内核状态以此为准下发；_rackActive 的合成源）
        private bool _eqEnabled;
        private bool _chEnabled;
        private bool _rgActive;
        private bool _convEnabled;
        private bool _compEnabled;
        private bool _stereoEnabled;
        private bool _matrixEnabled;
        private double _headroomDb;

        /// <summary>机架上是否有任一模块被 UI 启用（不含软音量/旁路）。</summary>
        private bool _rackActive;

        // DSP 总旁路（A/B 对比用）：开 = 全部 DSP 跳过、输出 bit-perfect，但设置保留（关掉即恢复）。
        // 旁路时 Read 走直通路径；电平表仍走 MeasurePassthrough 测量（不改写输出）。
        private volatile bool _bypassAll;

        private volatile bool _active; // 旁路关 且（机架激活 或 音量≠1）→ 走 ProcessBlock

        // 实时电平表：测量 post-DSP 信号（实际送往输出的信号）的每声道峰值/RMS。
        // 默认关闭（零开销、保持 bit-perfect）；播放开始时被开启。
        private readonly LevelMeter _levelMeter = new();
        private bool _meterEnabled;

        // 实时频谱分析：与电平表共用同一批 post-DSP 样本。
        // 渲染线程只做单声道降混 + 环形缓冲写入（不分配、不做 FFT），FFT 由 UI 线程执行，不影响音频实时性。
        private const int SpectrumBandCount = FormatHelper.WaveBarCount; // 单一来源，不再手工同步
        private readonly SpectrumAnalyzer _spectrum = new(SpectrumBandCount);
        private bool _spectrumEnabled;

        // 输出安全监控统计：测量 post-DSP、编码回写前的实际输出样本（峰值 / 削波计数）。
        // 只在 ProcessBlock 内更新 —— 直通路径不经过此处，因此不影响 bit-perfect。
        private volatile float _outPeak;     // 会话内最大 |样本|（线性）
        private volatile int _outClipCount;  // 会话内达到满刻度的样本数
        private volatile bool _outStatEnabled;

        /// <summary>会话内输出峰值（dBFS；无数据为负无穷）。</summary>
        public float OutputPeakDbfs
        {
            get
            {
                float p = _outPeak;
                if (p <= 0f) return float.NegativeInfinity;
                return (float)(20.0 * Math.Log10(p));
            }
        }

        /// <summary>会话内削波样本计数（|样本| 达到满刻度）。</summary>
        public int OutputClipCount => _outClipCount;

        /// <summary>输出统计开关。随播放会话开启；关闭后清零并零开销。</summary>
        public void SetOutputStats(bool enabled)
        {
            _outStatEnabled = enabled;
            if (!enabled)
            {
                _outPeak = 0f;
                _outClipCount = 0;
            }
        }

        /// <summary>清零峰值与削波计数（换曲 / 手动重置）。</summary>
        public void ResetOutputStats()
        {
            _outPeak = 0f;
            _outClipCount = 0;
        }

        // 软件总音量（共享/ASIO 用，采样级增益；NAudio WasapiOut.Volume 不支持，故由本类实现）。
        // 恒在链首：内核机架（ECHO 设计）不含音量模块，音量增益之后的峰值由内核尾段
        // Headroom/安全限幅统一保护，与 ECHO 语义一致。
        private volatile float _volumeGain = 1f;

        // ECHO 31 槽 EQ 默认频率（echo::eqFrequenciesHz；未占用槽回填用，保证内核拿到完整 31 槽）
        private static readonly float[] EqDefaultFrequencies =
        {
            20f, 25f, 31.5f, 40f, 50f, 63f, 80f, 100f, 125f, 160f,
            200f, 250f, 315f, 400f, 500f, 630f, 800f, 1000f, 1250f, 1600f,
            2000f, 2500f, 3150f, 4000f, 5000f, 6300f, 8000f, 10000f, 12500f, 16000f, 20000f
        };

        public ManagedDspSourceProvider(IWaveSourceProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _format = source.WaveFormat;
            _channels = _format.Channels;
            _isFloat = _format.Encoding == WaveFormatEncoding.IeeeFloat;

            CreateEngine();
        }

        // 「换引擎」的进程级互斥锁：原生 bridge 的 g_engine 是进程级单实例（与 ECHO 架构一致），
        // destroy+create 必须整段原子完成。两个 provider 同时构造时（并行测试类 / UI 连点），
        // 若锁外交叉执行，另一边在用的引擎会被中途释放 → use-after-free（实测 0xC0000005）。
        // 只在构造时持有，音频线程的 Read 热路径不经过它。
        private static readonly object EngineSwapGate = new();

        /// <summary>创建原生 DSP 内核（单实例：先销毁旧引擎再建；播放会话重建时走到这里）。
        /// 失败仅记录并保持 _engineReady=false —— 上层所有 Update 变 no-op，输出直通。</summary>
        private void CreateEngine()
        {
            try
            {
                int sr = _format.SampleRate;
                int ch = Math.Max(1, _channels);
                if (sr <= 0 || ch > DspCoreInterop.MaxChannels)
                {
                    StartupLog.Write($"[DSP] 内核创建跳过：格式异常 sr={sr} ch={ch}，DSP 全关直通");
                    _engineReady = false;
                    return;
                }

                // 单实例：销毁上一会话可能残留的引擎（含其设置/IR），全新构建。
                // 整段加锁，见 EngineSwapGate 注释：换引擎动作不许被并发构造/读取交叉。
                int rc;
                lock (EngineSwapGate)
                {
                    DspCoreInterop.celeste_dsp_destroy();
                    rc = DspCoreInterop.celeste_dsp_create(sr, DspCoreInterop.MaxBlockFrames, ch);
                }
                _engineReady = rc == DspCoreInterop.Ok;
                if (_engineReady)
                {
                    int ver = DspCoreInterop.celeste_dsp_version();
                    StartupLog.Write($"[DSP] celeste_dsp_core 就绪 ver=0x{ver:X8} sr={sr} maxBlock={DspCoreInterop.MaxBlockFrames} ch={ch}");
                }
                else
                {
                    StartupLog.Write($"[DSP] celeste_dsp_core 创建失败 rc={rc}（DLL 缺失或不兼容），DSP 全部回退直通");
                }
            }
            catch (Exception caught)
            {
                _engineReady = false;
                StartupLog.WriteException("ManagedDspSourceProvider.CreateEngine", caught);
            }
        }

        #region 状态更新（播放中调用，下一次 Read 生效）

        /// <summary>兼容旧 10 段 EQ（独立 EQ 窗口用）：组装为动态曲线状态再走统一内核映射。</summary>
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

            if (curve.Bands.Count == 0)
            {
                UpdateEqCurve(null);
                return;
            }
            UpdateEqCurve(curve);
        }

        /// <summary>应用动态 EQ 曲线状态（band 列表 + preamp）。null / 无效果 → 关闭 EQ。
        /// 映射到内核 31 槽参数化 EQ（前 N 槽按顺序填，其余 0/关）。预增益口径：
        /// 自带预增益的曲线（OPRA/AutoEq/APO 导入）直接采信；无预增益的用户自建曲线按
        /// 峰值估算自动折算负余量（Celeste 特有件，防过冲爆音）。播放中实时生效（内核原子接口）。</summary>
        public void UpdateEqCurve(EqCurveState? curve)
        {
            bool on = _engineReady && curve != null && curve.HasEffect();
            _eqEnabled = on;
            if (!on)
            {
                if (_engineReady)
                {
                    // 内核 EQ 旁路（6ms 淡出由内核处理，爆音安全）
                    DspCoreInterop.celeste_dsp_eq_set_enabled(0);
                }
                RefreshRackActive();
                return;
            }

            // Celeste 特有件：峰值余量处理。估算所有启用 band 在频域的最大叠加增益 peakDb。
            // ① 用户自建曲线（无预增益）：自动施加负余量把输出压回 0dB，避免极端增益
            //    （如 +10dB 低频增强）过冲后只能靠限幅/削波产生爆音。
            // ② 自带预增益的曲线（OPRA/AutoEq/APO 导入）：预增益本身已按这条曲线的峰值
            //    算过削波余量，直接采信、不再叠加。旧逻辑对两类曲线都叠 autoComp，导入曲线
            //    被双重衰减且多数钳到 -12dB 上限——实测连切 5 条 OPRA 曲线总增益
            //    -7.8/-8.4/-10.4/-10.7/-10.7dB 逐条递减，用户听感「用一个再用一个越来越低」。
            double peakDb = EstimateEqPeakDb(curve!);
            double userPreDb = Math.Abs(curve!.PreampDb) > 0.01 ? curve.PreampDb : 0.0;
            double preampDb;
            if (Math.Abs(userPreDb) > 0.01)
            {
                preampDb = Math.Clamp(userPreDb, DspCoreInteropMinPreamp, DspCoreInteropMaxPreamp);
            }
            else
            {
                double autoCompDb = Math.Clamp(-Math.Max(0, peakDb), -12, 0);
                preampDb = Math.Clamp(autoCompDb, DspCoreInteropMinPreamp, DspCoreInteropMaxPreamp);
            }

            if (Math.Abs(preampDb - curve!.PreampDb) > 0.05)
            {
                StartupLog.Write($"[DSP] EQ 预增益: 曲线={curve!.PreampDb:0.##}dB → 生效={preampDb:0.##}dB（峰值估算 {peakDb:0.##}dB）");
            }

            // 31 槽映射：curve.Bands 按顺序填前 N 槽（N ≤ 31），其余增益 0 / 关闭。
            // 频率/Q/类型用 band 自带值；未占用槽用 ECHO 默认频率回填（get_all 回读 round-trip 稳定）。
            float[] gains = new float[DspCoreInterop.EqBandCount];
            float[] freqs = new float[DspCoreInterop.EqBandCount];
            float[] qs = new float[DspCoreInterop.EqBandCount];
            int[] types = new int[DspCoreInterop.EqBandCount];
            int[] bandEnabled = new int[DspCoreInterop.EqBandCount];
            for (int i = 0; i < DspCoreInterop.EqBandCount; i++)
            {
                gains[i] = 0f;
                freqs[i] = EqDefaultFrequencies[i];
                qs[i] = 1f;
                types[i] = DspCoreInterop.EqPeaking;
                bandEnabled[i] = 0;
            }

            int count = Math.Min(curve.Bands.Count, DspCoreInterop.EqBandCount);
            for (int i = 0; i < count; i++)
            {
                var b = curve.Bands[i];
                if (b == null) continue;
                gains[i] = (float)Math.Clamp(b.GainDb, -12, 12);
                freqs[i] = (float)Math.Clamp(b.FrequencyHz, 20, 20000);
                qs[i] = (float)Math.Clamp(b.Q, 0.1, 12);
                types[i] = (int)b.FilterType;
                bandEnabled[i] = b.Enabled ? 1 : 0;
            }

            DspCoreInterop.celeste_dsp_eq_set_all(gains, freqs, qs, types, bandEnabled, (float)preampDb, 1);
            RefreshRackActive();
        }

        // 内核 EQ preamp 合法范围（echo::eqMinPreampDb / eqMaxPreampDb）
        private const double DspCoreInteropMinPreamp = -12.0;
        private const double DspCoreInteropMaxPreamp = 6.0;

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
                if (b.FilterType is (EqFilterType.Peaking or EqFilterType.LowShelf or EqFilterType.HighShelf)) return 0;
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

        /// <summary>设置声道平衡状态（balance / 增益 / 交换 / mono / 反相 / 延迟 + Crossfeed 子能力）。
        /// null / 未启用 → 内核 balance 模块旁路。映射到内核 <c>celeste_dsp_balance_set</c>。
        /// Crossfeed：强度 0-100% 直接映射 ECHO amount 0..1；截止频率 100..4000Hz 由 UI 提供，
        /// 未设置（老存档）时取 ECHO 默认 700Hz。</summary>
        public void UpdateChannel(ChannelBalanceState? state)
        {
            bool on = _engineReady && state != null && state.Enabled;
            _chEnabled = on;

            if (_engineReady)
            {
                if (on)
                {
                    int mono = state!.MonoMode switch
                    {
                        "sum" => DspCoreInterop.MonoSum,
                        "left" => DspCoreInterop.MonoLeft,
                        "right" => DspCoreInterop.MonoRight,
                        _ => DspCoreInterop.MonoOff
                    };

                    bool xfOn = state.CrossfeedEnabled && state.CrossfeedLevel > 0;
                    float xfAmount = xfOn ? (float)Math.Clamp(state.CrossfeedLevel / 100.0, 0.0, 1.0) : 0f;

                    // 截止频率：老版本写死 ECHO 的 700Hz，现由 UI 可调（存量缺省仍是 700，行为不变）。
                    // 这里 clamp 到内核允许区间再下发，内核侧还会再做一次 sanitize。
                    float xfCutoff = (float)Math.Clamp(state.CrossfeedCutoffHz, 100.0, 4000.0);

                    // 三段频补 Celeste UI 暂无 → 全 0；band gains 数组长度须为 3
                    float[] zeroBands = new float[DspCoreInterop.BalanceBandCount];

                    // balance/gains/delay 交内核 clamp（C++ sanitizeState 兜底）
                    DspCoreInterop.celeste_dsp_balance_set(
                        1,
                        (float)state.Balance,
                        (float)state.LeftGainDb,
                        (float)state.RightGainDb,
                        zeroBands, zeroBands,
                        (float)state.LeftDelayMs, (float)state.RightDelayMs,
                        state.SwapChannels ? 1 : 0,
                        mono,
                        state.InvertLeft ? 1 : 0,
                        state.InvertRight ? 1 : 0,
                        1); // constantPower：ECHO 默认
                    DspCoreInterop.celeste_dsp_crossfeed_set(xfOn ? 1 : 0, xfAmount, xfCutoff);
                }
                else
                {
                    float[] zeroBands = new float[DspCoreInterop.BalanceBandCount];
                    DspCoreInterop.celeste_dsp_crossfeed_set(0, 0f, 700f);
                    DspCoreInterop.celeste_dsp_balance_set(
                        0, 0f, 0f, 0f, zeroBands, zeroBands, 0f, 0f, 0,
                        DspCoreInterop.MonoOff, 0, 0, 1);
                }
            }

            RefreshRackActive();
        }

        /// <summary>设置安全限幅/余量。HeadroomDb → 内核 headroom 模块（链尾预衰减）；
        /// EnableLimiter → 内核进程级安全限幅开关（默认开，仅链活跃时介入）。</summary>
        public void UpdateSafety(DspSafetyState? state)
        {
            double hd = Math.Clamp(state?.HeadroomDb ?? 0.0, -12.0, 0.0);
            _headroomDb = hd;
            if (_engineReady)
            {
                DspCoreInterop.celeste_dsp_headroom_set_db((float)hd);
                // 与旧托管链口径一致：限幅开关单独开/关都不改样本，仅余量真正改变信号；
                // 内核侧限幅本就只在链活跃时运行（全关早退路径跳过）。
                DspCoreInterop.celeste_dsp_safety_set_enabled(state?.EnableLimiter ?? false ? 1 : 0);
            }

            RefreshRackActive();
        }

        /// <summary>设置 ReplayGain（对齐 ECHO ReplayGainProcessor）。mode=Off 旁路；
        /// 防削波截断由内核内部完成（ECHO 原语义）。播放中实时切换（内核 10ms 平滑）。</summary>
        public void SetReplayGain(ReplayGainState? state, double trackGainDb, double albumGainDb, double peak)
        {
            bool on = _engineReady && state != null && state.Mode != ReplayGainMode.Off;
            _rgActive = on;
            if (_engineReady)
            {
                int mode = state?.Mode switch
                {
                    ReplayGainMode.Track => DspCoreInterop.RgTrack,
                    ReplayGainMode.Album => DspCoreInterop.RgAlbum,
                    _ => DspCoreInterop.RgOff
                };
                DspCoreInterop.celeste_dsp_rg_set(
                    (float)trackGainDb,
                    (float)albumGainDb,
                    peak > 0 ? (float)peak : 1f,
                    mode,
                    (float)(state?.PreampDb ?? 0.0),
                    state?.PreventClipping ?? true ? 1 : 0);
            }

            RefreshRackActive();
        }

        /// <summary>
        /// 设置房间校正（卷积 FIR）。IR 从 <see cref="RoomCorrectionIrCache"/> 取（已按播放
        /// 采样率重采样）， planar 拼接后交内核 <c>celeste_dsp_conv_load_ir</c>（8192 taps 上限，
        /// 超限/校验失败 → 关闭卷积并记日志）。Celeste 的 Gain+Trim 两个参数合并为内核单一
        /// trim（ECHO ConvolutionProcessor 只有 trimDb，范围 -24..+6）。
        /// 播放中调用：下一次 Read 生效；换 IR 重置卷积流式状态（衔接差异可接受）。
        /// </summary>
        public void SetRoomCorrection(RoomCorrectionState? state)
        {
            if (!_engineReady || state == null || !state.Enabled || string.IsNullOrWhiteSpace(state.IrPath))
            {
                DisableConvolution();
                return;
            }

            try
            {
                float[][]? ir = RoomCorrectionIrCache.GetOrLoad(state.IrPath, _format.SampleRate);
                if (ir == null || ir.Length == 0 || ir.Length > 2 || ir[0] == null)
                {
                    StartupLog.Write("[DSP] 卷积 IR 加载失败（文件不可读/声道数超限），卷积关闭");
                    DisableConvolution();
                    return;
                }

                int tapsLen = ir[0].Length;
                if (ir.Length == 2 && (ir[1] == null || ir[1].Length != tapsLen))
                {
                    StartupLog.Write("[DSP] 卷积 IR 左右声道 taps 数不一致，卷积关闭");
                    DisableConvolution();
                    return;
                }

                if (tapsLen <= 0 || tapsLen > DspCoreInterop.MaxIrTaps)
                {
                    StartupLog.Write($"[DSP] 卷积 IR taps={tapsLen} 超内核上限 {DspCoreInterop.MaxIrTaps}，卷积关闭");
                    DisableConvolution();
                    return;
                }

                // planar 拼接：[ch0 全部 taps][ch1 全部 taps]
                float[] planar = new float[tapsLen * ir.Length];
                Buffer.BlockCopy(ir[0], 0, planar, 0, tapsLen * 4);
                if (ir.Length == 2)
                {
                    Buffer.BlockCopy(ir[1], 0, planar, tapsLen * 4, tapsLen * 4);
                }

                // GetOrLoad 已按播放采样率重采样 → 源采样率=播放采样率（内核不再重采样）
                int rc = DspCoreInterop.celeste_dsp_conv_load_ir(planar, ir.Length, tapsLen, _format.SampleRate);
                if (rc != DspCoreInterop.Ok)
                {
                    StartupLog.Write($"[DSP] 内核拒绝 IR rc={rc}（taps={tapsLen} ch={ir.Length}），卷积关闭");
                    DisableConvolution();
                    return;
                }

                // Celeste Gain+Trim 合并为内核 trim（内核仅 trimDb，-24..+6）
                double trimDb = Math.Clamp(state.GainDb + state.TrimDb, -24.0, 6.0);
                DspCoreInterop.celeste_dsp_conv_set_trim_db((float)trimDb);
                DspCoreInterop.celeste_dsp_conv_set_enabled(1);
                _convEnabled = true;
                StartupLog.Write($"[DSP] 卷积 IR 已加载 taps={tapsLen} ch={ir.Length} trim={trimDb:F1}dB");
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("ManagedDspSourceProvider.SetRoomCorrection", caught);
                DisableConvolution();
                return;
            }

            RefreshRackActive();
        }

        private void DisableConvolution()
        {
            _convEnabled = false;
            if (_engineReady)
            {
                DspCoreInterop.celeste_dsp_conv_set_enabled(0);
                DspCoreInterop.celeste_dsp_conv_clear();
            }

            RefreshRackActive();
        }

        /// <summary>任意 DSP 是否激活（UI 据此提示"非 bit-perfect"）。</summary>
        public bool IsActive => _active;

        /// <summary>DSP 总旁路（A/B 对比用）：开 = 跳过全部 DSP 使输出 bit-perfect，设置全部保留。
        /// 播放中可随时切换，下一次 Read 生效（旁路时走直通路径，电平表仍测量但不改写输出）。</summary>
        public void SetBypassAll(bool bypass)
        {
            _bypassAll = bypass;
            RefreshActive();
        }

        /// <summary>当前是否处于 DSP 总旁路。</summary>
        public bool IsBypassAll => _bypassAll;

        /// <summary>重算机架激活标志（各 Update 里调用）。软音量与旁路不在此列
        /// （它们在 RefreshActive 里单独参与）。</summary>
        private void RefreshRackActive()
        {
            _rackActive = _eqEnabled || _chEnabled || _rgActive || _convEnabled
                || _compEnabled || _stereoEnabled || _matrixEnabled
                || Math.Abs(_headroomDb) > 0.001;
            RefreshActive();
        }

        /// <summary>重算 DSP 是否生效；当无任一 DSP 生效时后续 Read 直接直通（bit-perfect，零逐样本开销）。
        /// 注意：单独的「安全限幅开关」不激活处理链 —— 源 PCM 不会超 ±1，限幅本就不改变样本
        /// （内核侧同样只在链活跃时运行），故保持 bit-perfect 直通。</summary>
        private void RefreshActive()
        {
            _active = !_bypassAll && (_rackActive || Math.Abs(_volumeGain - 1f) > 0.0001f);
        }

        /// <summary>设置采样级总音量基因（共享/ASIO 软件音量），0..2。音量=1 时不进 Processing。
        /// 位于链首（内核机架外），其抬升的峰值由内核尾段 Headroom/限幅保护。</summary>
        public void SetVolumeGain(float gain)
        {
            _volumeGain = Math.Clamp(gain, 0f, 2f);
            RefreshActive();
        }

        /// <summary>上游 PCM 处理标志：SRC 升频 / 多声道降混发生在 DSP 链上游时置 true，
        /// 内核据此把安全限幅天花板从 0dB 收紧到 -1dB（给上游处理留余量，对齐 ECHO 语义）。</summary>
        public void SetUpstreamPcmActive(bool active)
        {
            if (_engineReady)
            {
                DspCoreInterop.celeste_dsp_set_upstream_active(active ? 1 : 0);
            }
        }

        /// <summary>应用机架状态：模块处理顺序 + 压缩器 + 立体声场 + 声道矩阵（Stage B）。
        /// 顺序非法时内核会拒绝（-3），此时回退内核默认顺序并记日志。</summary>
        public void ApplyRack(RackState rack)
        {
            if (!_engineReady || rack == null)
            {
                return;
            }

            rack = rack.Clone();
            rack.Normalize();

            int rc = DspCoreInterop.celeste_dsp_rack_set_order(rack.RackOrder);
            if (rc != DspCoreInterop.Ok)
            {
                StartupLog.Write($"[DSP] 机架顺序被内核拒绝 rc={rc}，回退默认顺序");
                DspCoreInterop.celeste_dsp_rack_reset_default();
            }

            var comp = rack.Compressor;
            DspCoreInterop.celeste_dsp_compressor_set(
                comp.Enabled ? 1 : 0,
                (float)comp.ThresholdDb,
                (float)comp.Ratio,
                (float)comp.AttackMs,
                (float)comp.ReleaseMs,
                (float)comp.KneeDb,
                (float)comp.MakeupDb,
                (float)comp.Mix);

            var field = rack.StereoField;
            DspCoreInterop.celeste_dsp_stereofield_set(
                field.Enabled ? 1 : 0,
                (float)field.Width,
                (float)field.CenterGainDb,
                (float)field.SideGainDb);

            var matrix = rack.Matrix;
            DspCoreInterop.celeste_dsp_matrix_set(
                matrix.Enabled ? 1 : 0,
                (float)matrix.LeftToLeft,
                (float)matrix.RightToLeft,
                (float)matrix.LeftToRight,
                (float)matrix.RightToRight);

            // 新三模块的启用意图纳入 _rackActive 门控（否则 _active=false 时内核根本不被调用）
            _compEnabled = comp.IsActive;
            _stereoEnabled = field.IsActive;
            _matrixEnabled = matrix.IsActive;
            RefreshRackActive();
        }

        #endregion

        #region IWaveSourceProvider

        public WaveFormat WaveFormat => _format;

        public TimeSpan TotalTime => _source.TotalTime;

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState => _source.ProbeCurrentState;

        public bool NextMounted => _source.NextMounted;

        public void Seek(TimeSpan position)
        {
            _source.Seek(position);
            // 清空机架滤波器历史/延迟线/卷积流式状态：否则 seek 前后不连续的信号
            // 会被旧历史污染（旧托管链 EQ/卷积同理，行为对齐）。
            if (_engineReady)
            {
                DspCoreInterop.celeste_dsp_reset();
            }
        }

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

            // 无 DSP 生效 → 全部直通（bit-perfect）；若电平表/频谱开启则额外测量，
            // 但只解码到临时 float 缓冲测量，绝不改写输出缓冲 → 仍严格 bit-perfect。
            if (_meterEnabled || _spectrumEnabled)
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

            // 阶段 1：软件总音量（采样级增益，恒在最前；Celeste 特有件，内核机架无音量模块）
            float volumeGain = _volumeGain;
            if (volumeGain != 1f)
            {
                for (int i = 0; i < n; i++)
                {
                    buf[i] *= volumeGain;
                }
            }

            // 阶段 2：原生 DSP 机架（交错 float32 原地处理）。
            // 分块兜底：单块帧数超过内核上限 16384 时循环调用
            // （如 352.8kHz × 100ms 输出缓冲 = 35280 帧）。内核返回负值 = 出错：
            // 保留已处理部分、剩余原样送出、只记一次日志 —— 绝不静默改写、绝不抛到渲染线程。
            if (_engineReady)
            {
                int done = 0;
                unsafe
                {
                    fixed (float* p = buf)
                    {
                        while (done < frames)
                        {
                            int chunk = Math.Min(frames - done, DspCoreInterop.MaxBlockFrames);
                            int rc = DspCoreInterop.celeste_dsp_process(p + (long)done * ch, chunk);
                            if (rc <= 0)
                            {
                                if (!_processErrLogged)
                                {
                                    _processErrLogged = true;
                                    StartupLog.Write($"[DSP] 内核 process 返回 {rc}（帧 {done}/{frames} sr={_format.SampleRate} ch={ch}），本块剩余原样送出");
                                }
                                break;
                            }
                            done += chunk;
                        }
                    }
                }
            }

            // 实时电平/频谱：测量 post-DSP 信号（即实际送往输出的样本）
            if (_meterEnabled)
            {
                _levelMeter.Update(buf, n, ch);
            }

            // 输出安全监控：同一批 post-DSP 样本统计峰值与削波计数（编码回写前，反映真实送出电平）
            if (_outStatEnabled)
            {
                float pk = 0f;
                int cc = 0;
                for (int i = 0; i < n; i++)
                {
                    float a = buf[i] < 0 ? -buf[i] : buf[i];
                    if (a > pk) pk = a;
                    if (a >= 0.99999f) cc++;
                }
                if (pk > _outPeak) _outPeak = pk;
                if (cc > 0) _outClipCount += cc;
            }

            if (_spectrumEnabled)
            {
                _spectrum.Push(buf, n, ch);
            }

            // 编码：float → byte。整数回写时叠加 TPDF dither（幅度 = 目标位深 1 LSB）后舍入并钳制，
            // 消除截断量化失真（与 ResamplingSourceProvider 的 24bit dither 一致）；
            // 直通路径不经过此处，故不影响 bit-perfect。
            float lsb = _isFloat ? 0f : _format.BitsPerSample switch
            {
                32 => 1f / 2147483647f,
                24 => 1f / 8388607f,
                _ => 1f / 32767f
            };
            if (_isFloat)
            {
                int bo = offset;
                for (int i = 0; i < n; i++, bo += 4) BitConverter.GetBytes(buf[i]).CopyTo(b, bo);
            }
            else if (_format.BitsPerSample == 32)
            {
                int bo = offset;
                for (int i = 0; i < n; i++, bo += 4)
                {
                    float s = buf[i] + TpdfDither(_ditherRng, lsb);
                    long v = (long)Math.Round(s * 2147483647.0);
                    if (v > 2147483647L) v = 2147483647L; else if (v < -2147483648L) v = -2147483648L;
                    int iv = (int)v;
                    b[bo] = (byte)(iv & 0xFF); b[bo + 1] = (byte)((iv >> 8) & 0xFF);
                    b[bo + 2] = (byte)((iv >> 16) & 0xFF); b[bo + 3] = (byte)((iv >> 24) & 0xFF);
                }
            }
            else if (_format.BitsPerSample == 24)
            {
                int bo = offset;
                for (int i = 0; i < n; i++, bo += 3)
                {
                    float s = buf[i] + TpdfDither(_ditherRng, lsb);
                    int v = (int)Math.Round(s * 8388607.0);
                    if (v > 8388607) v = 8388607; else if (v < -8388608) v = -8388608;
                    b[bo] = (byte)(v & 0xFF); b[bo + 1] = (byte)((v >> 8) & 0xFF); b[bo + 2] = (byte)((v >> 16) & 0xFF);
                }
            }
            else
            {
                int bo = offset;
                for (int i = 0; i < n; i++, bo += 2)
                {
                    float s = buf[i] + TpdfDither(_ditherRng, lsb);
                    int v = (int)Math.Round(s * 32767.0);
                    if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                    short sv = (short)v;
                    b[bo] = (byte)(sv & 0xFF); b[bo + 1] = (byte)((sv >> 8) & 0xFF);
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

        // 整数回写 dither：TPDF（三角分布）抖动，幅度按目标位深 1 LSB，消除截断量化失真。
        // 仅在 ProcessBlock（DSP 已修改信号）内使用；直通路径不经过，故不影响 bit-perfect。
        private readonly Random _ditherRng = new();

        /// <summary>TPDF 抖动：两个均匀随机数相减，幅度 ±amp×LSB。用于整数回写量化前的抖动。</summary>
        private static float TpdfDither(Random rng, float lsb)
        {
            float r = (float)(rng.NextDouble() - rng.NextDouble()); // [-1,1)
            return r * lsb;
        }

        #region 实时电平表 / 频谱（测量 post-DSP 信号）

        /// <summary>开启/关闭电平测量。开启时按声道数重置内部缓冲（播放会话开始时调用）。
        /// 关闭时 Read 完全不做解码，恢复零开销 bit-perfect 直通。</summary>
        public void SetMetering(bool enabled)
        {
            _meterEnabled = enabled;
            if (enabled) _levelMeter.Reset(_channels);
        }

        /// <summary>电平表实例（渲染线程写、UI 线程读，内部有锁）。</summary>
        public LevelMeter LevelMeter => _levelMeter;

        /// <summary>开启/关闭频谱采样。开启时按声道数与源采样率重建分频表。
        /// 关闭后渲染线程不再降混写入（零开销）。</summary>
        public void SetSpectrum(bool enabled)
        {
            _spectrumEnabled = enabled;
            if (enabled)
            {
                _spectrum.Reset(_channels, _format.SampleRate, SpectrumBandCount);
            }
            else
            {
                _spectrum.SetEnabled(false);
            }
        }

        /// <summary>频谱分析器实例（渲染线程写样本、UI 线程读算 FFT，内部有锁）。</summary>
        public SpectrumAnalyzer Spectrum => _spectrum;

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

        /// <summary>bit-perfect 直通路径下的测量：解码到临时 float 缓冲后更新电平表并喂频谱，
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
            if (_meterEnabled)
            {
                _levelMeter.Update(buf, n, ch);
            }

            if (_spectrumEnabled)
            {
                _spectrum.Push(buf, n, ch);
            }
        }

        #endregion

        #region 内核状态查询（UI 线程调用；供徽标 / Stage C 新模块面板）

        /// <summary>机架是否有任一模块在真正处理（bypass 淡出完成后为 false）。</summary>
        public bool ChainActive => _engineReady && DspCoreInterop.celeste_dsp_is_active() != 0;

        /// <summary>链上任意模块报告削波风险（卷积顶高 / 立体声场 / 矩阵等）。</summary>
        public bool HasClippingRisk => _engineReady && DspCoreInterop.celeste_dsp_has_clipping_risk() != 0;

        /// <summary>安全限幅器正在保护（正在衰减）。</summary>
        public bool LimiterProtecting => _engineReady && DspCoreInterop.celeste_dsp_limiter_protecting() != 0;

        /// <summary>安全限幅器实时增益衰减（dB，正数）。</summary>
        public float LimiterGainReductionDb => _engineReady ? DspCoreInterop.celeste_dsp_limiter_gr_db() : 0f;

        /// <summary>安全限幅器当前天花板（dBFS；upstream 不活跃=0、活跃=-1）。</summary>
        public float LimiterCeilingDb => _engineReady ? DspCoreInterop.celeste_dsp_limiter_ceiling_db() : 0f;

        /// <summary>压缩器实时增益衰减（dB，正数）。</summary>
        public float CompressorGainReductionDb => _engineReady ? DspCoreInterop.celeste_dsp_compressor_gr_db() : 0f;

        /// <summary>卷积输出是否出现过削波（已被钳到 ±1；界面据此提示「削波风险」）。</summary>
        public bool ConvolutionClippingRisk
        {
            get
            {
                if (!_engineReady) return false;
                DspCoreInterop.celeste_dsp_conv_get_state(out _, out _, out _, out _, out _, out int risk, out _);
                return risk != 0;
            }
        }

        /// <summary>已加载 IR 的 taps 数（未加载为 0）。</summary>
        public int ConvolutionIrTaps
        {
            get
            {
                if (!_engineReady) return 0;
                DspCoreInterop.celeste_dsp_conv_get_state(out _, out int taps, out _, out _, out _, out _, out _);
                return taps;
            }
        }

        /// <summary>卷积引入的延迟帧数（内核分区卷积延迟）。</summary>
        public int ConvolutionLatencyFrames
        {
            get
            {
                if (!_engineReady) return 0;
                DspCoreInterop.celeste_dsp_conv_get_state(out _, out _, out _, out _, out int latency, out _, out _);
                return latency;
            }
        }

        #endregion
    }
}
