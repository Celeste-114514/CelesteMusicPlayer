using System;
using System.Collections.Generic;
using System.Linq;

namespace CelesteMusicPlayer
{
    /// <summary>链路节点状态。UI 只读这些值渲染，不自己判。</summary>
    public enum SignalNodeState
    {
        /// <summary>正在参与处理（逐样本 / 正在协商 / 正在送数）。</summary>
        Active,

        /// <summary>开着但被总旁路压住（内容不经过它）。</summary>
        Bypassed,

        /// <summary>待命：机制就绪但此刻不动样本（限幅无其它 DSP、缓存未预热）。</summary>
        Standby,

        /// <summary>未启用 / 不参与（默认值旁路态）。</summary>
        Off,

        /// <summary>拿不到真实数据——按铁律显示"未知"，绝不编数值。</summary>
        Unknown
    }

    /// <summary>链路一个环节的快照。格式取不到就留 null（面板显示"—"）。</summary>
    public sealed class SignalNode
    {
        /// <summary>稳定 Id："decode" / "cache" / "gapless" / "downmix" / "tempo" /
        /// "dsp:&lt;模块Id&gt;" / "safety" / "output"。</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>展示名（"解码" / "DSP · 31 段 EQ"）。</summary>
        public string Title { get; init; } = string.Empty;

        public SignalNodeState State { get; init; }

        /// <summary>进这个节点的格式（推导不出=null）。</summary>
        public AudioFormat? InFormat { get; init; }

        /// <summary>出这个节点的格式（推导不出=null）。</summary>
        public AudioFormat? OutFormat { get; init; }

        /// <summary>一句话注（"已重采样 96k→48k" / "限幅待命"）。没有就 null。</summary>
        public string? Note { get; init; }

        /// <summary>只有有真实测量/设置值的节点填（输出缓冲、卷积延迟），其余 null。</summary>
        public double? LatencyMs { get; init; }
    }

    /// <summary>链路事件（时间线一行）。</summary>
    public sealed class ChainEvent
    {
        public DateTime Time { get; init; }

        /// <summary>分类："track" / "device" / "mode" / "negotiate" / "tempo" / "dsp" / "system"。</summary>
        public string Kind { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;
    }

    /// <summary>
    /// 音频链路统一快照（B 组音频链路面板 B2 数据模型，2026-10-16）。
    ///
    /// 存在意义：链路状态原先散在三处——引擎侧 <see cref="ChainFormatState"/>、
    /// UI 控件侧 <c>MainWindow.RackModuleActive</c>（读 toggle/slider）、
    /// 设置侧各 Store。面板逐个去问就会和三处口径打架（09-22 徽标谎报教训）。
    /// 本模型把三处收敛成一份快照，UI 只读它。
    ///
    /// 铁律：拿不到的数据显示 Unknown / null，绝不编数值；本类不碰音频线程，
    /// 只读已有结构化状态（Store.Load 是内存缓存 + 深拷贝，LevelMeter 有锁）。
    ///
    /// ⚠ 判定口径说明：八模块"是否真在处理"用的是 **Store 口径**（与
    /// MainWindow.IsBitPerfectPure / 音频设置 DSP 摘要同源），不是
    /// RackModuleActive 的 UI 控件口径。两处差异（UI 开着总开关但全默认值时
    /// UI 显示"未启用"）以 Store 口径为准——bit-perfect 判定本来就认总开关。
    /// B4 接线后 MainWindow 侧两处显示也都改读本模型，彻底消除漂移。
    /// </summary>
    public sealed class AudioSignalPath
    {
        // ─────────────────────────────────────────────────────────────
        // 事件环形缓冲（B2 立缓冲，B4 接线；面板关着不记也无妨，便宜）
        // ─────────────────────────────────────────────────────────────

        private const int EventCapacity = 50;
        private static readonly object EventGate = new();
        private static readonly List<ChainEvent> EventRing = new();

        /// <summary>记一条链路事件（切歌/切设备/切模式/协商结果/变速/DSP 开关）。</summary>
        public static void RecordEvent(string kind, string text)
        {
            lock (EventGate)
            {
                EventRing.Add(new ChainEvent { Time = DateTime.Now, Kind = kind, Text = text });
                if (EventRing.Count > EventCapacity)
                {
                    EventRing.RemoveRange(0, EventRing.Count - EventCapacity);
                }
            }
        }

        /// <summary>最近 N 条事件（新→旧）。</summary>
        public static IReadOnlyList<ChainEvent> RecentEvents(int max = 20)
        {
            lock (EventGate)
            {
                return EventRing.AsEnumerable().Reverse().Take(max).ToList();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 快照
        // ─────────────────────────────────────────────────────────────

        /// <summary>按链序排列的节点（解码 → 缓存 → 无缝 → 降混 → 变速 → DSP×8 → 安全 → 输出）。</summary>
        public IReadOnlyList<SignalNode> Nodes { get; private init; } = Array.Empty<SignalNode>();

        /// <summary>顶部摘要一行（"源 44.1k/16b/2ch → 独占 PCM24"）。</summary>
        public string SummaryLine { get; private init; } = string.Empty;

        /// <summary>bit-perfect 综合判定（与 MainWindow.EvaluateBitPerfectChain 同口径）。</summary>
        public bool BitPerfect { get; private init; }

        /// <summary>判定依据（非 bit-perfect 时的原因串；bit-perfect 时为空）。</summary>
        public string VerdictReason { get; private init; } = string.Empty;

        /// <summary>是否已按设备端协商值确认（false=未播放/无法确认，面板应标"待确认"）。</summary>
        public bool BitPerfectConfirmed { get; private init; }

        /// <summary>实时电平（post-DSP 每声道线性峰值/RMS）。LevelChannels=0 表示无信号可测。</summary>
        public float[] PeakLevels { get; private init; } = Array.Empty<float>();

        public float[] RmsLevels { get; private init; } = Array.Empty<float>();

        public int LevelChannels { get; private init; }

        /// <summary>当前设备 Id（null=系统默认/未初始化）。</summary>
        public string? DeviceId { get; private init; }

        /// <summary>输出缓冲设置值（ms）。0=未初始化。</summary>
        public int OutputBufferMs { get; private init; }

        /// <summary>卷积引入的延迟（帧）。0=未启用卷积。</summary>
        public int ConvolutionLatencyFrames { get; private init; }

        /// <summary>
        /// 抓一份链路快照。UI 线程调用（Store.Load/引擎属性都线程安全，但没必要高频）。
        ///
        /// </summary>
        /// <param name="engine">播放引擎（null=程序刚起未建引擎，全部节点待命）。</param>
        /// <param name="globalBypass">DSP 总旁路开关状态（设置侧没有此开关的持久化，
        /// 由调用方把 UI toggle 现状传进来——这是参数不是"问 UI"）。</param>
        public static AudioSignalPath Capture(AudioPlaybackEngine? engine, bool globalBypass)
        {
            var chain = engine?.ChainFormat;
            bool session = chain != null && chain.HasSession;

            // ── DSP 各模块 Store 口径判定（与 IsBitPerfectPure 同源）────────
            var rack = DspRackStore.Load();
            var extra = DspExtraStore.Load();
            var room = RoomCorrectionStore.Load();
            var eq = EqCurveStore.Load();
            var rg = ReplayGainStore.Load();
            bool eqOn = eq.HasEffect();
            bool firOn = room.Enabled && !string.IsNullOrWhiteSpace(room.IrPath);
            bool rgOn = rg.Mode != ReplayGainMode.Off;
            bool compOn = rack.Compressor.IsActive;
            bool crossfeedOn = extra.ChannelBalance.Enabled
                && extra.ChannelBalance.CrossfeedEnabled
                && extra.ChannelBalance.CrossfeedLevel > 0;
            bool fieldOn = rack.StereoField.IsActive;
            bool matrixOn = rack.Matrix.IsActive;
            bool balanceOn = extra.ChannelBalance.IsActive;
            bool headroomOn = extra.Safety.AffectsBits;
            bool limiterSwitchOn = extra.Safety.EnableLimiter;
            bool volOn = engine?.IsSoftwareVolumeActive ?? false;
            bool anySampleLevelDsp = eqOn || firOn || rgOn || compOn || crossfeedOn
                || fieldOn || matrixOn || balanceOn || headroomOn || volOn;

            // ── 节点装配 ──────────────────────────────────────────────
            var nodes = new List<SignalNode>();

            // 1) 解码：源文件 → 实际送链的转码 WAV
            string? decodeNote = null;
            if (chain != null && chain.IsDsdPath)
            {
                decodeNote = "DSD/DoP 直出（不过处理链）";
            }
            else if (chain != null)
            {
                decodeNote = chain.OutcomeNote();
            }

            nodes.Add(new SignalNode
            {
                Id = "decode",
                Title = "解码（ffmpeg → PCM）",
                State = session ? SignalNodeState.Active : SignalNodeState.Standby,
                InFormat = chain?.SourceFile,
                OutFormat = chain?.TranscodeWav,
                Note = session
                    ? (chain!.SourceFileDescription ?? decodeNote)
                    : "未播放"
            });

            // 2) PCM 缓存：缓冲设置值即此节点的延迟口径
            int bufferMs = engine?.OutputBufferMs ?? AppSettingsStore.Load().OutputBufferMs;
            nodes.Add(new SignalNode
            {
                Id = "cache",
                Title = "PCM 缓存",
                State = session ? SignalNodeState.Active : SignalNodeState.Standby,
                InFormat = chain?.TranscodeWav,
                OutFormat = chain?.TranscodeWav,
                Note = session ? $"缓冲 {bufferMs}ms（设置值）" : "未播放",
                LatencyMs = bufferMs > 0 ? bufferMs : null
            });

            // 3) 无缝接力：没有"下一首预载状态"的结构化值，B4 用事件佐证，此处不编
            nodes.Add(new SignalNode
            {
                Id = "gapless",
                Title = "无缝接力",
                State = session ? SignalNodeState.Active : SignalNodeState.Standby,
                InFormat = chain?.TranscodeWav,
                OutFormat = chain?.TranscodeWav,
                Note = session ? null : "未播放"
            });

            // 4) 降混：推导口径——源与送链 WAV 声道数不一致即发生了降混
            SignalNodeState downmixState = SignalNodeState.Off;
            string? downmixNote = "未启用（源即送链声道数）";
            if (session && chain!.SourceFile is AudioFormat src && chain.TranscodeWav is AudioFormat wav
                && src.Channels != wav.Channels)
            {
                downmixState = SignalNodeState.Active;
                downmixNote = $"{src.Channels}声道 → {wav.Channels}声道";
            }
            else if (session && chain!.SourceFile == null)
            {
                downmixState = SignalNodeState.Unknown;
                downmixNote = null;
            }

            nodes.Add(new SignalNode
            {
                Id = "downmix",
                Title = "降混",
                State = downmixState,
                InFormat = chain?.SourceFile,
                OutFormat = chain?.TranscodeWav,
                Note = downmixNote
            });

            // 5) 变速：TempoShifted 标志（倍率≠1 时机架激活，标志由后端填）
            double rate = AppSettingsStore.Load().PlaybackRate;
            bool tempoOn = session && chain != null && chain.TempoShifted;
            nodes.Add(new SignalNode
            {
                Id = "tempo",
                Title = "变速",
                State = tempoOn ? SignalNodeState.Active : SignalNodeState.Off,
                InFormat = chain?.TranscodeWav,
                OutFormat = chain?.TranscodeWav,
                Note = tempoOn
                    ? $"{rate:0.00}×（已重塑采样，非原始数据）"
                    : $"{rate:0.00}× 直通"
            });

            // 6) DSP 机架 8 模块（按机架顺序；总旁路时一律 Bypassed）
            string[] moduleTitles =
            {
                "DSP · 31 段 EQ",       // RackEqualizer
                "DSP · 房间校正（卷积）",  // RackConvolution
                "DSP · ReplayGain",     // RackReplayGain
                "DSP · 压缩器",          // RackCompressor
                "DSP · Crossfeed",      // RackCrossfeed
                "DSP · 立体声场",        // RackStereoField
                "DSP · 声道矩阵",        // RackChannelMatrix
                "DSP · 声道平衡"         // RackChannelBalance
            };

            bool[] moduleOn =
            {
                eqOn, firOn, rgOn, compOn, crossfeedOn, fieldOn, matrixOn, balanceOn
            };

            for (int i = 0; i < rack.RackOrder.Length && i < moduleTitles.Length; i++)
            {
                int id = rack.RackOrder[i];
                if (id < 0 || id >= moduleOn.Length)
                {
                    continue;
                }

                SignalNodeState state = SignalNodeState.Off;
                if (moduleOn[id])
                {
                    state = globalBypass ? SignalNodeState.Bypassed : SignalNodeState.Active;
                }

                nodes.Add(new SignalNode
                {
                    Id = "dsp:" + id,
                    Title = moduleTitles[id],
                    State = state,
                    Note = state switch
                    {
                        SignalNodeState.Active => "生效中",
                        SignalNodeState.Bypassed => "已旁路（总旁路开）",
                        _ => "未启用"
                    }
                });
            }

            // 7) 安全（余量/限幅）：口径同音频设置摘要——
            //    余量≠0 = 真逐样本；限幅单独开只待命；随链生效要已有其它逐样本模块
            SignalNodeState safetyState;
            string safetyNote;
            if (headroomOn)
            {
                safetyState = globalBypass ? SignalNodeState.Bypassed : SignalNodeState.Active;
                safetyNote = $"余量 {extra.Safety.HeadroomDb:0.#}dB（逐样本预衰减）";
            }
            else if (limiterSwitchOn && anySampleLevelDsp)
            {
                safetyState = globalBypass ? SignalNodeState.Bypassed : SignalNodeState.Active;
                safetyNote = "限幅生效（随 DSP 链防削波）";
            }
            else if (limiterSwitchOn)
            {
                safetyState = SignalNodeState.Standby;
                safetyNote = "限幅待命（无其它 DSP 时不动样本）";
            }
            else
            {
                safetyState = SignalNodeState.Off;
                safetyNote = "未启用";
            }

            nodes.Add(new SignalNode
            {
                Id = "safety",
                Title = "输出安全（余量 / 限幅）",
                State = safetyState,
                Note = safetyNote
            });

            // 8) 输出：设备端协商格式 + 模式
            string modeNote = BuildModeNote(engine);
            nodes.Add(new SignalNode
            {
                Id = "output",
                Title = "输出",
                State = session ? SignalNodeState.Active : SignalNodeState.Standby,
                InFormat = chain?.TranscodeWav,
                OutFormat = chain?.DeviceOutput,
                Note = session
                    ? modeNote + (string.IsNullOrWhiteSpace(chain!.DeviceEndpointName)
                        ? string.Empty
                        : " · " + chain.DeviceEndpointName)
                    : "未播放",
                LatencyMs = null // 设备端真实延迟拿不到（B1 已核），不编
            });

            // ── bit-perfect 综合判定（口径照搬 EvaluateBitPerfectChain）────
            var causes = new List<string>();
            bool confirmed = false;
            bool hifi = IsHiFiModeForChain();
            if (!globalBypass && anySampleLevelDsp)
            {
                causes.Add("DSP：" + string.Join("、", DescribeActiveDsp(
                    eqOn, firOn, rgOn, compOn, crossfeedOn, fieldOn, matrixOn, balanceOn, headroomOn, volOn)));
            }

            if (!hifi)
            {
                causes.Add("系统混音器（共享模式）");
            }

            if (session)
            {
                if (chain!.IsDsdPath)
                {
                    confirmed = true;
                    if (chain.Device == DevicePath.Degraded)
                    {
                        causes.Add("DSD 设备端容器低于源，数值有损");
                    }
                    else if (chain.Device == DevicePath.Resampled)
                    {
                        causes.Add("DSD 设备端对封装做了转换（非直通）");
                    }
                }
                else
                {
                    if (chain.TempoShifted)
                    {
                        causes.Add("变速播放（已重塑采样，非原始数据）");
                    }

                    switch (chain.Outcome)
                    {
                        case TranscodeOutcome.ResampledToDevice:
                            causes.Add("转码已重采样（迁就设备采样率）");
                            break;
                        case TranscodeOutcome.Downsampled:
                            causes.Add("转码降级（规格被做低）");
                            break;
                        case TranscodeOutcome.FailedFallback:
                            causes.Add("源探测失败，按兜底规格转码");
                            break;
                    }

                    var dev = chain.DeviceOutput;
                    var linkWav = chain.TranscodeWav;
                    if (dev != null && linkWav != null)
                    {
                        confirmed = true;
                        if (dev.Value.Rate != linkWav.Value.Rate)
                        {
                            causes.Add($"重采样 {linkWav.Value.Rate}→{dev.Value.Rate}hz");
                        }

                        if (chain.Device == DevicePath.Degraded)
                        {
                            causes.Add("设备端格式低于源，数值有损");
                        }
                        else if (chain.Device == DevicePath.Resampled)
                        {
                            causes.Add("设备端格式转换（非直通）");
                        }
                        else if (chain.Device == DevicePath.Lossless && dev.Value.Bits < linkWav.Value.Bits)
                        {
                            causes.Add($"位深转换 {linkWav.Value.Bits}→{dev.Value.Bits}bit");
                        }
                    }
                    else
                    {
                        causes.Add("设备端格式未知，无法确认直通");
                        confirmed = true;
                    }
                }
            }

            bool bitPerfect = causes.Count == 0;
            string reason = string.Join(" · ", causes);

            // ── 摘要行 ────────────────────────────────────────────────
            string srcText = chain?.SourceFile is AudioFormat sf
                ? sf.Describe()
                : (string.IsNullOrWhiteSpace(chain?.SourceFileDescription) ? "未知" : chain!.SourceFileDescription!);
            string devText = chain?.DeviceOutput is AudioFormat df
                ? df.Describe()
                : "未知";
            string summary = session
                ? $"源 {srcText} → {modeNote} {devText}"
                : "未播放（链路待命）";

            // ── 电平快照 ──────────────────────────────────────────────
            float[] peaks = Array.Empty<float>();
            float[] rms = Array.Empty<float>();
            int channels = 0;
            if (engine != null)
            {
                channels = engine.LevelMeterChannels;
                if (channels > 0)
                {
                    peaks = new float[channels];
                    rms = new float[channels];
                    engine.TryGetLevels(peaks, rms);
                }
            }

            return new AudioSignalPath
            {
                Nodes = nodes,
                SummaryLine = summary,
                BitPerfect = bitPerfect,
                VerdictReason = reason,
                BitPerfectConfirmed = confirmed,
                PeakLevels = peaks,
                RmsLevels = rms,
                LevelChannels = channels,
                DeviceId = engine?.OutputDeviceId,
                OutputBufferMs = bufferMs,
                ConvolutionLatencyFrames = engine?.ConvolutionLatencyFrames ?? 0
            };
        }

        /// <summary>输出模式人话（与音频设置面板同口径：设置层 OutputMode 字符串）。</summary>
        private static string BuildModeNote(AudioPlaybackEngine? engine)
        {
            if (engine?.CurrentOutputMode is HiFiOutputBackend.OutputMode mode)
            {
                return mode switch
                {
                    HiFiOutputBackend.OutputMode.WasapiExclusive => "WASAPI 独占",
                    HiFiOutputBackend.OutputMode.Asio => "ASIO",
                    _ => "共享（系统混音）"
                };
            }

            return IsHiFiModeForChain() ? "独占" : "共享（系统混音）";
        }

        /// <summary>设置层是否选了 HiFi 模式（口径同 MainWindow.IsHiFiModeSelected）。</summary>
        private static bool IsHiFiModeForChain()
        {
            string mode = AppSettingsStore.Load().OutputMode;
            return string.Equals(mode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Asio", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> DescribeActiveDsp(
            bool eq, bool fir, bool rg, bool comp, bool crossfeed, bool field,
            bool matrix, bool balance, bool headroom, bool vol)
        {
            if (eq) yield return "EQ";
            if (fir) yield return "房间校正";
            if (rg) yield return "ReplayGain";
            if (comp) yield return "压缩";
            if (crossfeed) yield return "Crossfeed";
            if (field) yield return "声场";
            if (matrix) yield return "矩阵";
            if (balance) yield return "声道平衡";
            if (headroom) yield return "余量";
            if (vol) yield return "软件音量";
        }
    }
}
