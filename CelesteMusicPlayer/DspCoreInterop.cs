// DspCoreInterop.cs
// celeste_dsp_core.dll 的 C ABI P/Invoke 封装（Stage B）。
// 桥接层源码：native/dsp-core/celeste_dsp_bridge.cpp（extern "C" 导出，照搬 ECHO 机架）。
//
// 线程约定（与桥接层设计一致）：
//   - 设置 / 查询（*_set / *_get / rack_* / conv_*）：UI 线程调用，下一个音频块生效；
//   - Process：音频渲染线程调用；
//   - Create / Destroy：仅播放会话起止，且音频渲染已停止时调用（桥接层单实例）。
//
// 错误码约定：0=成功；-1=引擎未创建/未准备；-2=参数非法；-3=设置被拒；
//             -4=IR 校验失败；-100=构造异常。
//
// 本类只做声明 + 常量 + DLL 解析；生命周期与调用编排由 ManagedDspSourceProvider 负责。

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    internal static partial class DspCoreInterop
    {
        internal const string Lib = "celeste_dsp_core";

        // ── 内核常量（与 audio-engine 头文件一一对应，改内核须同步）──
        internal const int EqBandCount = 31;          // echo::eqBandCount
        internal const int RackModuleCount = 8;       // echo::DspRackOrder::moduleCount
        internal const int BalanceBandCount = 3;      // echo::channelBalanceBandCount
        internal const int MaxBlockFrames = 16384;    // 桥接层 kMaxBlockFrames
        internal const int MaxChannels = 8;           // 桥接层 kMaxChannels
        internal const int MaxIrTaps = 8192;          // ConvolutionProcessor taps 上限

        // ── 错误码 ──
        internal const int Ok = 0;
        internal const int ErrEngine = -1;
        internal const int ErrParam = -2;
        internal const int ErrRejected = -3;
        internal const int ErrIr = -4;
        internal const int ErrCtor = -100;

        // ── 机架模块 ID（echo::DspRackModuleId）──
        internal const int RackEqualizer = 0;
        internal const int RackConvolution = 1;
        internal const int RackReplayGain = 2;
        internal const int RackCompressor = 3;
        internal const int RackCrossfeed = 4;
        internal const int RackStereoField = 5;
        internal const int RackChannelMatrix = 6;
        internal const int RackChannelBalance = 7;

        // ── EQ 滤波器类型（echo::EqFilterType）──
        internal const int EqPeaking = 0;
        internal const int EqLowShelf = 1;
        internal const int EqHighShelf = 2;
        internal const int EqLowPass = 3;
        internal const int EqHighPass = 4;
        internal const int EqNotch = 5;

        // ── ReplayGain 模式（echo::ReplayGainConfig.mode）──
        internal const int RgOff = 0;
        internal const int RgTrack = 1;
        internal const int RgAlbum = 2;

        // ── 声道平衡 mono 模式（echo::ChannelBalanceMonoMode）──
        internal const int MonoOff = 0;
        internal const int MonoSum = 1;
        internal const int MonoLeft = 2;
        internal const int MonoRight = 3;

        static DspCoreInterop()
        {
            // 与其它原生 DLL（celeste_core / celeste_audio_core）同目录加载；仅解析本库名，
            // 其余名字返回 IntPtr.Zero 交回默认解析器，不影响其它 P/Invoke。
            NativeLibrary.SetDllImportResolver(typeof(DspCoreInterop).Assembly, (name, asm, path) =>
            {
                if (name != Lib)
                    return IntPtr.Zero;
                string local = Path.Combine(AppContext.BaseDirectory, "celeste_dsp_core.dll");
                return File.Exists(local) ? NativeLibrary.Load(local) : IntPtr.Zero;
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // 生命周期
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(Lib)] internal static extern int celeste_dsp_create(double sampleRate, int maxBlockFrames, int channels);
        [DllImport(Lib)] internal static extern void celeste_dsp_destroy();
        [DllImport(Lib)] internal static extern void celeste_dsp_reset();

        /// <summary>交错 float32 原地处理。frames &le; create 时的 maxBlockFrames。</summary>
        [DllImport(Lib)] internal static extern unsafe int celeste_dsp_process(float* interleaved, int frames);

        // ─────────────────────────────────────────────────────────────────────
        // EQ（31 槽参数化）
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(Lib)] internal static extern int celeste_dsp_eq_set_enabled(int enabled);
        [DllImport(Lib)] internal static extern int celeste_dsp_eq_set_preamp(float preampDb);
        [DllImport(Lib)] internal static extern int celeste_dsp_eq_set_band(int index, float gainDb, float freqHz, float q, int filterType, int bandEnabled);

        /// <summary>一次性下发全部 31 槽 + preamp + 总开关。数组长度均须为 31。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_eq_set_all(float[] gains, float[] freqs, float[] qs, int[] types, int[] bandEnabled, float preampDb, int eqEnabled);

        /// <summary>回读 EQ 原子目标值（平滑中的瞬时值不回读）。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_eq_get_all(float[] gains, float[] freqs, float[] qs, int[] types, int[] bandEnabled, out float preampDb, out int eqEnabled);
        [DllImport(Lib)] internal static extern int celeste_dsp_eq_reset_flat();
        [DllImport(Lib)] internal static extern int celeste_dsp_eq_builtin_count();

        /// <summary>取第 index 个内置预设（ECHO 16 个）。bands 恒为 31 项；nameOut 可空。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_eq_builtin_get(int index, byte[]? nameOut, int nameCap, out float preampDbOut, float[] gains, float[] freqs, float[] qs, int[] types, int[] bandEnabled);

        // ─────────────────────────────────────────────────────────────────────
        // 压缩器（前馈检测 + 软拐点 + 干湿混合）
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(Lib)] internal static extern int celeste_dsp_compressor_set(int enabled, float thresholdDb, float ratio, float attackMs, float releaseMs, float kneeDb, float makeupDb, float mix);
        [DllImport(Lib)] internal static extern int celeste_dsp_compressor_get(out int enabled, out float thresholdDb, out float ratio, out float attackMs, out float releaseMs, out float kneeDb, out float makeupDb, out float mix);

        /// <summary>压缩器实时增益衰减（dB，正数表示衰减量）。</summary>
        [DllImport(Lib)] internal static extern float celeste_dsp_compressor_gr_db();

        // ─────────────────────────────────────────────────────────────────────
        // 空间域：Crossfeed / StereoField / ChannelMatrix
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(Lib)] internal static extern int celeste_dsp_crossfeed_set(int enabled, float amount, float cutoffHz);
        [DllImport(Lib)] internal static extern int celeste_dsp_crossfeed_get(out int enabled, out float amount, out float cutoffHz);

        [DllImport(Lib)] internal static extern int celeste_dsp_stereofield_set(int enabled, float width, float centerGainDb, float sideGainDb);
        [DllImport(Lib)] internal static extern int celeste_dsp_stereofield_get(out int enabled, out float width, out float centerGainDb, out float sideGainDb);

        [DllImport(Lib)] internal static extern int celeste_dsp_matrix_set(int enabled, float leftToLeft, float rightToLeft, float leftToRight, float rightToRight);
        [DllImport(Lib)] internal static extern int celeste_dsp_matrix_get(out int enabled, out float leftToLeft, out float rightToLeft, out float leftToRight, out float rightToRight);

        // ─────────────────────────────────────────────────────────────────────
        // 声道平衡（balance / 三段频补 / 延迟 / 交换 / mono / 反相 / 恒功率）
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(Lib)] internal static extern int celeste_dsp_balance_set(int enabled, float balance, float leftGainDb, float rightGainDb, float[] leftBandGains, float[] rightBandGains, float leftDelayMs, float rightDelayMs, int swapLeftRight, int monoMode, int invertLeft, int invertRight, int constantPower);
        [DllImport(Lib)] internal static extern int celeste_dsp_balance_get(out int enabled, out float balance, out float leftGainDb, out float rightGainDb, float[] leftBandGains, float[] rightBandGains, out float leftDelayMs, out float rightDelayMs, out int swapLeftRight, out int monoMode, out int invertLeft, out int invertRight, out int constantPower);

        // ─────────────────────────────────────────────────────────────────────
        // Headroom / ReplayGain / 安全限幅器 / 上游 PCM 标志
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(Lib)] internal static extern int celeste_dsp_headroom_set_db(float headroomDb);
        [DllImport(Lib)] internal static extern float celeste_dsp_headroom_get_db();

        [DllImport(Lib)] internal static extern int celeste_dsp_rg_set(float trackGainDb, float albumGainDb, float peak, int mode, float preampDb, int preventClipping);
        [DllImport(Lib)] internal static extern int celeste_dsp_rg_get(out float trackGainDb, out float albumGainDb, out float peak, out int mode, out float preampDb, out int preventClipping);

        /// <summary>ReplayGain 当前实际施加的增益（dB；含防削波截断后的值）。</summary>
        [DllImport(Lib)] internal static extern float celeste_dsp_rg_applied_db();

        /// <summary>安全限幅器（ECHO 进程级静态开关，默认开）。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_safety_set_enabled(int enabled);
        [DllImport(Lib)] internal static extern int celeste_dsp_safety_get_enabled();

        /// <summary>置 1 时限幅天花板从 0dB 收紧到 -1dB。Celeste：独占输出做了 SRC 升频/降混时置 1。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_set_upstream_active(int active);

        // ─────────────────────────────────────────────────────────────────────
        // 机架顺序（8 模块，seqlock 快照）
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(Lib)] internal static extern int celeste_dsp_rack_set_order(int[] order);
        [DllImport(Lib)] internal static extern int celeste_dsp_rack_get_order(int[] order);
        [DllImport(Lib)] internal static extern int celeste_dsp_rack_reset_default();

        // ─────────────────────────────────────────────────────────────────────
        // 卷积（房间校正 / 耳机 IR；直积，8192 taps 上限）
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>taps 为 planar float32：[ch0 全部 taps][ch1 全部 taps]。
        /// tapsChannels 1=mono / 2=stereo；sourceSampleRate 为 IR 自身采样率（内部重采样）。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_conv_load_ir(float[] taps, int tapsChannels, int tapsLen, double sourceSampleRate);
        [DllImport(Lib)] internal static extern int celeste_dsp_conv_clear();
        [DllImport(Lib)] internal static extern int celeste_dsp_conv_set_enabled(int enabled);
        [DllImport(Lib)] internal static extern int celeste_dsp_conv_set_trim_db(float trimDb);
        [DllImport(Lib)] internal static extern int celeste_dsp_conv_get_state(out int enabled, out int tapCount, out double sampleRate, out float trimDb, out int latencySamples, out int clippingRisk, out int hasError);

        // ─────────────────────────────────────────────────────────────────────
        // 链状态查询（徽标 / 诊断用）
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>机架是否有任一模块在真正处理（bypass 淡出完成后为 0）。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_is_active();

        /// <summary>链上任意模块报告削波风险（卷积顶高 / 立体声场 / 矩阵等）。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_has_clipping_risk();

        /// <summary>安全限幅器正在保护（正在衰减）为 1。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_limiter_protecting();

        /// <summary>安全限幅器实时增益衰减（dB，正数）。</summary>
        [DllImport(Lib)] internal static extern float celeste_dsp_limiter_gr_db();

        /// <summary>安全限幅器当前天花板（dBFS；upstream 不活跃=0、活跃=-1）。</summary>
        [DllImport(Lib)] internal static extern float celeste_dsp_limiter_ceiling_db();

        /// <summary>内核版本（0x00010000 = 1.0）。供启动日志核对桥接与库匹配。</summary>
        [DllImport(Lib)] internal static extern int celeste_dsp_version();
    }
}
