using System;

namespace CelesteMusicPlayer
{
    /// <summary>音频格式（结构化）。率=Hz、位深=bit、声道数、是否浮点存储。
    /// 链路面板与 bit-perfect 徽标以此比对，不再从人话描述串反解析（951796f 教训的根治）。</summary>
    public readonly record struct AudioFormat(int Rate, int Bits, int Channels, bool IsFloat)
    {
        /// <summary>人话描述（与旧 SourceFormatDescription 口径一致）。</summary>
        public string Describe() => Rate + "hz / " + Bits + "bit / " + Channels + "声道";

        /// <summary>紧凑描述（日志用）。</summary>
        public string DescribeShort() => Rate + "/" + Bits + "bit/" + Channels + "ch";
    }

    /// <summary>转码结果：实际送链路的 WAV 相对源文件发生了什么（阶段二起由转码计划填充，阶段一按 WAV 头与源探测值推算）。</summary>
    public enum TranscodeOutcome
    {
        /// <summary>同格式转出（率/位深与源一致）。</summary>
        SameFormat,

        /// <summary>容器/表示扩容但数值无损（如 24bit 装进 32bit 容器、float32→int32 等比缩放）。</summary>
        ContainerWidened,

        /// <summary>为迁就设备采样率而重采样（非 bit-perfect）。</summary>
        ResampledToDevice,

        /// <summary>位深/采样率被做低（探测失败兜底、设备回退；非 bit-perfect）。</summary>
        Downsampled,

        /// <summary>源探测失败，按兜底规格（16bit/44.1k/立体声）转码，高于此规格的源被静默降级。</summary>
        FailedFallback
    }

    /// <summary>设备端路径分类（由独占内核的协商结果填，结构化，不再解析描述串）。</summary>
    public enum DevicePath
    {
        /// <summary>未知（未播放 / 未捕获 / 共享-ASIO 路径）。</summary>
        Unknown,

        /// <summary>源格式（或同布局容器）直通，数值无损。</summary>
        Lossless,

        /// <summary>端点格式数值上仍无损（如 24bit 源 → float32 端点），不等于直通但数值无损。</summary>
        Degraded,

        /// <summary>设备端发生重采样/有损转换（非 bit-perfect）。</summary>
        Resampled
    }

    /// <summary>
    /// 链路结构化格式状态（2026-09-22 音频链路重写阶段一地基）。
    /// 一条状态从解码器 → 输出层 → UI：源文件真实值、实际送链的转码 WAV、设备端协商结果，
    /// 外加判定标志位。bit-perfect 徽标只判这些标志，绝不从给人看的描述串反解析。
    /// 未播放时各段为 null、标志为默认值。
    /// </summary>
    public sealed class ChainFormatState
    {
        /// <summary>源文件真实格式（ProbeSourceFormatAsync 探测值；DSD/探测失败为 null）。</summary>
        public AudioFormat? SourceFile { get; set; }

        /// <summary>实际送链路的转码 WAV 格式（WAV 头）。DSD/DoP 直出时为 DoP 容器格式。</summary>
        public AudioFormat? TranscodeWav { get; set; }

        /// <summary>设备端实际输出格式（独占内核协商结果 / 共享模式 MixFormat）。null=未捕获。</summary>
        public AudioFormat? DeviceOutput { get; set; }

        /// <summary>设备端点格式名（PCM24-in-32 / float32 / …；自研内核为容器类型名）。仅展示用。</summary>
        public string? DeviceEndpointName { get; set; }

        /// <summary>设备端路径分类。</summary>
        public DevicePath Device { get; set; } = DevicePath.Unknown;

        /// <summary>转码结果分类。</summary>
        public TranscodeOutcome Outcome { get; set; } = TranscodeOutcome.SameFormat;

        /// <summary>DSP 链是否实际激活（EQ/声道/余量/ReplayGain/卷积；限幅单独开启不算）。</summary>
        public bool DspChainActive { get; set; }

        /// <summary>是否共享模式（系统混音器必然介入，与格式无关地非 bit-perfect）。</summary>
        public bool SharedMode { get; set; }

        /// <summary>是否 DSD/DoP 直出路径（跳过 PCM 重采样比对：输出 PCM 只是 1-bit 的封装载体）。</summary>
        public bool IsDsdPath { get; set; }

        /// <summary>源文件的人话格式（DSD 等无 PCM 探测值时展示，如 "2822.4kHz / 2声道 1-bit DSD"）。</summary>
        public string? SourceFileDescription { get; set; }

        /// <summary>链路是否曾完成过一次协商（区分"未播放"与"播放过又停止"）。</summary>
        public bool HasSession { get; set; }

        /// <summary>复位为"未播放"状态（Stop/Cleanup 时调用）。</summary>
        public void Reset()
        {
            SourceFile = null;
            TranscodeWav = null;
            DeviceOutput = null;
            DeviceEndpointName = null;
            Device = DevicePath.Unknown;
            Outcome = TranscodeOutcome.SameFormat;
            DspChainActive = false;
            SharedMode = false;
            IsDsdPath = false;
            SourceFileDescription = null;
            HasSession = false;
        }

        /// <summary>转码结果的人话短注（源胶囊"转码 WAV"行尾注）。</summary>
        public string OutcomeNote() => Outcome switch
        {
            TranscodeOutcome.ContainerWidened => "（容器扩容，数值无损）",
            TranscodeOutcome.ResampledToDevice => "（已重采样，迁就设备）",
            TranscodeOutcome.Downsampled => "（已降级：规格被做低）",
            TranscodeOutcome.FailedFallback => "（源探测失败，按兜底规格转码）",
            _ => string.Empty
        };

        /// <summary>设备端结论行的人话（输出胶囊第二行）。</summary>
        public string VerdictNote()
        {
            if (IsDsdPath)
            {
                return Device switch
                {
                    DevicePath.Lossless => "1-bit 原生封装直通（bit-perfect）",
                    DevicePath.Degraded => "设备端容器低于源，数值有损",
                    DevicePath.Resampled => "设备端对封装做了转换（非直通）",
                    _ => "待播放确认"
                };
            }

            return Device switch
            {
                DevicePath.Lossless => "源直通（数值无损）",
                DevicePath.Degraded => "设备端格式低于源，数值有损",
                DevicePath.Resampled => "设备端重采样/格式转换（非直通）",
                _ => "待播放确认"
            };
        }
    }
}
