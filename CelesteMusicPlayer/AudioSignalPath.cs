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

    /// <summary>技术档案标签的语气（Neutral=灰 / Accent=蓝紫 / Ok=浅绿 / Warn=浅橙）。UI 只按这个上色，不自己判语义。</summary>
    public enum ChainTagTone
    {
        Neutral,
        Accent,
        Ok,
        Warn
    }

    /// <summary>技术档案里的一枚状态胶囊（"均衡器未启用"）。拿不到的项压根不生成，UI 也就没有空胶囊。</summary>
    public sealed class ChainTechTag
    {
        public string Text { get; init; } = string.Empty;

        public ChainTagTone Tone { get; init; } = ChainTagTone.Neutral;
    }

    /// <summary>链路诊断一条：类别标识（如 BIT-PERFECT）+ 标题 + 原因 + 建议。</summary>
    public sealed class ChainDiagnostic
    {
        /// <summary>左侧短类别（"BIT-PERFECT" / "DSP" / "SRC"）。</summary>
        public string Category { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Reason { get; init; } = string.Empty;

        public string Advice { get; init; } = string.Empty;
    }

    /// <summary>底部稳定性反馈网格的一项（标签 + 值 + 可选说明）。</summary>
    public sealed class ChainField
    {
        public string Label { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;

        /// <summary>说明。null/空 = 不显示该行（铁律）。</summary>
        public string? Note { get; init; }
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

        /// <summary>
        /// 记一条链路事件，但**内容跟上次一样就不记**（B4 接线用）。
        ///
        /// 用场：DSP 滑块拖动、音量条移动这类高频回调会连续吐同一个值，
        /// 不去重的话输出记录会被刷成一整屏重复行。按 key 各记各的上一条。
        /// </summary>
        public static void RecordEventDedup(string key, string kind, string text)
        {
            lock (EventGate)
            {
                if (LastEventByKey.TryGetValue(key, out string? last)
                    && string.Equals(last, text, StringComparison.Ordinal))
                {
                    return;
                }

                LastEventByKey[key] = text;
                EventRing.Add(new ChainEvent { Time = DateTime.Now, Kind = kind, Text = text });
                if (EventRing.Count > EventCapacity)
                {
                    EventRing.RemoveRange(0, EventRing.Count - EventCapacity);
                }
            }
        }

        /// <summary>各 key 的上一条事件正文（只服务于去重，不对外）。</summary>
        private static readonly Dictionary<string, string> LastEventByKey = new();

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

        // ── 以下为 Echo 风格「信号路径」面板分区（2026-10-17 扩展，口径仍在模型里） ──

        /// <summary>当前处理阶段大标题（"原生播放" / "DSP 处理中" / "待命"）。</summary>
        public string StageTitle { get; private init; } = string.Empty;

        /// <summary>阶段下一行的状态（"未启用 DSP" / "N 个模块生效"）。</summary>
        public string StageNote { get; private init; } = string.Empty;

        /// <summary>链路环节数（摘要行右侧「N 层链路」）。0=未建立（整枚标签不显示）。</summary>
        public int LayerCount { get; private init; }

        /// <summary>左列「来源」主值（"MP3 48kHz 2ch"）。拿不到=null。</summary>
        public string? SourceValue { get; private init; }

        /// <summary>左列「来源」补充（"320 kbps / Stereo"）。拿不到=null。</summary>
        public string? SourceNote { get; private init; }

        /// <summary>右列「处理」主值（"直接路径" / "DSP 处理"）。</summary>
        public string? ProcessValue { get; private init; }

        /// <summary>右列「处理」补充（"未启用 DSP 模块"）。</summary>
        public string? ProcessNote { get; private init; }

        /// <summary>双列后续字段（输出设备 / 输出模式 / 内部格式 / 实际输出 / 缓冲 / 卷积）。按链序。</summary>
        public IReadOnlyList<ChainField> Fields { get; private init; } = Array.Empty<ChainField>();

        /// <summary>「DSP 实时余量」行的值（"等待信号" / "未在实时播放" / "生效中"）。</summary>
        public string? DspHeadroomValue { get; private init; }

        /// <summary>「DSP 实时余量」行的说明。拿不到=null（不编 CPU/处理速度）。</summary>
        public string? DspHeadroomNote { get; private init; }

        /// <summary>技术档案胶囊组。拿不到的项不生成，一条都没有时 UI 整块收起。</summary>
        public IReadOnlyList<ChainTechTag> TechTags { get; private init; } = Array.Empty<ChainTechTag>();

        /// <summary>链路诊断条目（非 bit-perfect 的每条原因一条）。没有=空（整块收起）。</summary>
        public IReadOnlyList<ChainDiagnostic> Diagnostics { get; private init; } = Array.Empty<ChainDiagnostic>();

        /// <summary>DAC 能力图谱：设备名。null=拿不到（整块收起，不编能力）。</summary>
        public string? DacDeviceName { get; private init; }

        /// <summary>DAC 能力图谱字段（曾经输出 / 已确认能力 / 当前问题）。拿不到=空。</summary>
        public IReadOnlyList<ChainField> DacFields { get; private init; } = Array.Empty<ChainField>();

        /// <summary>稳定性反馈总结标题（"当前输出链路已确认"）。</summary>
        public string? StabilityTitle { get; private init; }

        /// <summary>稳定性反馈说明（"没有观察到错误、回退或 DSP 介入。"）。</summary>
        public string? StabilityNote { get; private init; }

        /// <summary>稳定性反馈双列网格（请求模式 / 实际路径 / 是否回退 / DSP 状态 / 限制说明）。</summary>
        public IReadOnlyList<ChainField> StabilityFields { get; private init; } = Array.Empty<ChainField>();

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

            // ⚠ 2026-10-17 用户要求：链路里**只列已开启的 DSP 模块**；
            //    一个都没开就只摆一个「DSP」节点并写"未启用"，不把八个模块全铺出来（太吵）。
            //    铁律照旧：不存在的环节不虚构——没开的模块不出现，"未启用"那一个节点就是全部事实。
            int shownDsp = 0;
            for (int i = 0; i < rack.RackOrder.Length && i < moduleTitles.Length; i++)
            {
                int id = rack.RackOrder[i];
                if (id < 0 || id >= moduleOn.Length || !moduleOn[id])
                {
                    continue;
                }

                SignalNodeState state = globalBypass ? SignalNodeState.Bypassed : SignalNodeState.Active;
                shownDsp++;
                nodes.Add(new SignalNode
                {
                    Id = "dsp:" + id,
                    Title = moduleTitles[id],
                    State = state,
                    Note = state == SignalNodeState.Bypassed ? "已旁路（总旁路开）" : "生效中"
                });
            }

            if (shownDsp == 0)
            {
                nodes.Add(new SignalNode
                {
                    Id = "dsp:none",
                    Title = "DSP",
                    State = SignalNodeState.Off,
                    Note = "未启用（输出保持原始数据流）"
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

            // ── Echo 风格「信号路径」分区（2026-10-17）────────────────────────
            // 口径一律沿用上面已经算好的判定，不在这里另起一套，避免两处打架。

            // ① 处理阶段：只有"真的在改样本"才算 DSP 处理中（与 bit-perfect 判定同口径）
            bool dspEngaged = !globalBypass && anySampleLevelDsp;
            int activeModules = DescribeActiveDsp(
                eqOn, firOn, rgOn, compOn, crossfeedOn, fieldOn, matrixOn, balanceOn, headroomOn, volOn).Count();
            string stageTitle = !session ? "待命" : (dspEngaged ? "DSP 处理中" : "原生播放");
            string stageNote = !session
                ? "未播放（链路待命）"
                : dspEngaged ? $"{activeModules} 个模块在链上" : "未启用 DSP";

            // ② 双列后续字段：拿不到的一律不生成（铁律）
            var fields = new List<ChainField>();
            if (session && !string.IsNullOrWhiteSpace(chain!.DeviceEndpointName))
            {
                fields.Add(new ChainField { Label = "输出设备", Value = chain.DeviceEndpointName! });
            }

            fields.Add(new ChainField { Label = "输出模式", Value = modeNote });
            if (session && chain!.TranscodeWav is AudioFormat wavFmt)
            {
                fields.Add(new ChainField { Label = "内部处理格式", Value = wavFmt.Describe() });
            }

            if (session && chain!.DeviceOutput is AudioFormat devFmt)
            {
                fields.Add(new ChainField { Label = "实际输出", Value = devFmt.Describe() });
            }

            if (bufferMs > 0)
            {
                fields.Add(new ChainField { Label = "输出缓冲", Value = $"{bufferMs} ms" });
            }

            if ((engine?.ConvolutionLatencyFrames ?? 0) > 0)
            {
                fields.Add(new ChainField
                {
                    Label = "卷积延迟",
                    Value = $"{engine!.ConvolutionLatencyFrames} 帧"
                });
            }

            double rateValue = AppSettingsStore.Load().PlaybackRate;
            if (session && Math.Abs(rateValue - 1.0) > 0.001)
            {
                fields.Add(new ChainField
                {
                    Label = "变速",
                    Value = $"{rateValue:0.00}×",
                    Note = "已重塑采样，非原始数据"
                });
            }

            // ③ DSP 实时余量：不编 CPU / 处理速度；没在播就是等待信号
            bool playing = engine?.IsPlaying ?? false;
            string dspHeadroomValue = playing
                ? (dspEngaged ? "生效中" : "待机")
                : "等待信号";
            string? dspHeadroomNote = playing ? null : "未在实时播放";

            // ④ 技术档案胶囊：拿不到的项不生成（抖动 / 无缝播放没有结构化值）
            var tags = new List<ChainTechTag>();
            int volume = (int)Math.Round(AppSettingsStore.Load().Volume);
            tags.Add(new ChainTechTag { Text = $"音量 {volume}%", Tone = ChainTagTone.Neutral });
            if (volOn)
            {
                tags.Add(new ChainTechTag { Text = "软件音量生效", Tone = ChainTagTone.Warn });
            }

            tags.Add(new ChainTechTag
            {
                Text = rgOn ? "回放增益已启用" : "回放增益未启用",
                Tone = rgOn ? ChainTagTone.Accent : ChainTagTone.Neutral
            });
            tags.Add(new ChainTechTag
            {
                Text = eqOn ? "均衡器已启用" : "均衡器未启用",
                Tone = eqOn ? ChainTagTone.Accent : ChainTagTone.Neutral
            });
            tags.Add(new ChainTechTag
            {
                Text = firOn ? "房间校正已启用" : "房间校正未启用",
                Tone = firOn ? ChainTagTone.Accent : ChainTagTone.Neutral
            });
            tags.Add(new ChainTechTag
            {
                Text = compOn ? "压缩器已启用" : "压缩器未启用",
                Tone = compOn ? ChainTagTone.Accent : ChainTagTone.Neutral
            });

            bool resampling = session && chain!.Outcome == TranscodeOutcome.ResampledToDevice;
            tags.Add(new ChainTechTag
            {
                Text = resampling ? "重采样生效中" : "重采样待机",
                Tone = resampling ? ChainTagTone.Warn : ChainTagTone.Neutral
            });

            if (session && Math.Abs(rateValue - 1.0) > 0.001)
            {
                tags.Add(new ChainTechTag { Text = $"变速 {rateValue:0.00}×", Tone = ChainTagTone.Accent });
            }

            if (globalBypass)
            {
                tags.Add(new ChainTechTag { Text = "DSP 总旁路", Tone = ChainTagTone.Ok });
            }

            // ⑤ 链路诊断：非 bit-perfect 的每条原因一条（原因/建议都是结构化映射，不现场编）
            var diagnostics = new List<ChainDiagnostic>();
            foreach (string cause in causes)
            {
                diagnostics.Add(ToDiagnostic(cause));
            }

            // ⑥ DAC 能力图谱：只有设备名拿得到才整块显示；支持率列表没有公开访问器，不强凑
            string? dacName = session && !string.IsNullOrWhiteSpace(chain!.DeviceEndpointName)
                ? chain.DeviceEndpointName
                : null;
            var dacFields = new List<ChainField>();
            if (dacName != null)
            {
                if (chain!.DeviceOutput is AudioFormat dacFmt)
                {
                    dacFields.Add(new ChainField { Label = "本次协商格式", Value = dacFmt.Describe() });
                }

                dacFields.Add(new ChainField
                {
                    Label = "设备路径判定",
                    Value = DescribeDevicePath(chain!.Device)
                });
            }

            // ⑦ 稳定性反馈：总结 + 双列最终状态
            string stabilityTitle = bitPerfect ? "当前输出链路已确认" : "链路存在介入";
            string stabilityNote = bitPerfect
                ? "没有观察到错误、回退或 DSP 介入。"
                : confirmed
                    ? "已按设备端协商结果确认，但链路中存在介入。"
                    : "尚未与设备端协商确认。";
            var stabilityFields = new List<ChainField>
            {
                new ChainField { Label = "请求模式", Value = modeNote },
                new ChainField
                {
                    Label = "实际确认路径",
                    Value = session ? DescribeDevicePath(chain!.Device) : "未建立",
                    Note = session && chain!.DeviceOutput is AudioFormat cf ? cf.Describe() : null
                },
                new ChainField
                {
                    Label = "是否回退",
                    Value = session && chain!.Outcome == TranscodeOutcome.FailedFallback ? "是（兜底规格）" : "否"
                },
                new ChainField
                {
                    Label = "DSP 状态",
                    Value = globalBypass ? "总旁路" : dspEngaged ? "生效中" : "未启用"
                }
            };
            if (!bitPerfect && !string.IsNullOrWhiteSpace(reason))
            {
                stabilityFields.Add(new ChainField { Label = "限制说明", Value = reason });
            }

            return new AudioSignalPath
            {
                StageTitle = stageTitle,
                StageNote = stageNote,
                LayerCount = nodes.Count,
                SourceValue = session ? srcText : null,
                SourceNote = session && !string.IsNullOrWhiteSpace(chain?.SourceFileDescription)
                    ? chain!.SourceFileDescription
                    : null,
                ProcessValue = session ? (dspEngaged ? "DSP 处理" : "直接路径") : null,
                ProcessNote = session ? stageNote : null,
                Fields = fields,
                DspHeadroomValue = dspHeadroomValue,
                DspHeadroomNote = dspHeadroomNote,
                TechTags = tags,
                Diagnostics = diagnostics,
                DacDeviceName = dacName,
                DacFields = dacFields,
                StabilityTitle = stabilityTitle,
                StabilityNote = stabilityNote,
                StabilityFields = stabilityFields,
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

        /// <summary>
        /// 输出模式人话（B4 接线用：设置层保存的字符串 → 面板显示文案）。
        /// 与 <see cref="BuildModeNote"/>（引擎侧枚举）保持同一套文案，避免两处漂移。
        /// </summary>
        public static string DescribeOutputMode(string? settingsMode)
        {
            return string.Equals(settingsMode, "WasapiExclusive", StringComparison.OrdinalIgnoreCase)
                ? "WASAPI 独占"
                : string.Equals(settingsMode, "Asio", StringComparison.OrdinalIgnoreCase)
                    ? "ASIO"
                    : "共享（系统混音）";
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

        /// <summary>设备端路径判定的人话（与 bit-perfect 判定同口径，UI 不再自己翻译枚举）。</summary>
        private static string DescribeDevicePath(DevicePath path)
        {
            return path switch
            {
                DevicePath.Lossless => "直通（数值无损）",
                DevicePath.Degraded => "容器转换（数值无损，非字节直通）",
                DevicePath.Resampled => "设备端转换（非直通）",
                _ => "未知"
            };
        }

        /// <summary>
        /// 一条非 bit-perfect 原因 → 诊断条目（类别 / 标题 / 原因 / 建议）。
        /// 建议是事先写死的结构化文案，不是现场编的；识别不出类别就归到 BIT-PERFECT。
        /// </summary>
        private static ChainDiagnostic ToDiagnostic(string cause)
        {
            bool Has(string key) => cause.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;

            if (Has("系统混音器"))
            {
                return new ChainDiagnostic
                {
                    Category = "OUTPUT",
                    Title = "不是 bit-perfect",
                    Reason = "共享输出：系统混音器会接管音量与采样率。",
                    Advice = "想要更严格的 bit-perfect 路径，优先使用独占输出或 ASIO 输出，并关闭 DSP/SRC 模块。"
                };
            }

            if (Has("变速播放"))
            {
                return new ChainDiagnostic
                {
                    Category = "TEMPO",
                    Title = "变速播放中",
                    Reason = cause,
                    Advice = "变速必然重塑采样点；回到 1.00× 即恢复原始数据流。"
                };
            }

            if (Has("DSD"))
            {
                return new ChainDiagnostic
                {
                    Category = "DSD",
                    Title = "DSD 直出受限",
                    Reason = cause,
                    Advice = "DSD/DoP 直出依赖设备端支持；必要时改用 PCM 转换输出。"
                };
            }

            if (Has("位深转换") || Has("设备端格式低于源") || Has("源探测失败"))
            {
                return new ChainDiagnostic
                {
                    Category = "FORMAT",
                    Title = "格式被转换",
                    Reason = cause,
                    Advice = "在独占模式下换一个位深/采样率档位，或让设备跟随音源规格。"
                };
            }

            if (Has("重采样") || Has("转码降级") || Has("设备端格式转换"))
            {
                return new ChainDiagnostic
                {
                    Category = "SRC",
                    Title = "采样率被转换",
                    Reason = cause,
                    Advice = "把输出采样率与音源对齐，或使用独占模式让设备跟随音源采样率。"
                };
            }

            if (Has("设备端格式未知"))
            {
                return new ChainDiagnostic
                {
                    Category = "DEVICE",
                    Title = "设备端未上报格式",
                    Reason = cause,
                    Advice = "播放一次后再看；仍然未知说明设备没有回报协商结果。"
                };
            }

            if (cause.StartsWith("DSP：", StringComparison.Ordinal))
            {
                return new ChainDiagnostic
                {
                    Category = "DSP",
                    Title = "DSP 正在改动样本",
                    Reason = cause,
                    Advice = "要听原始数据流就关掉对应模块，或用 DSP 总旁路做 A/B 对比（设置会保留）。"
                };
            }

            return new ChainDiagnostic
            {
                Category = "BIT-PERFECT",
                Title = "不是 bit-perfect",
                Reason = cause,
                Advice = "逐条关掉链路上的介入项（DSP / 共享模式 / 变速 / 重采样）即可恢复直通。"
            };
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
