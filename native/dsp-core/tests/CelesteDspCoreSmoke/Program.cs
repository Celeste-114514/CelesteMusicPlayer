// celeste_dsp_core.dll 冒烟测试：验证 C ABI 桥接层 + ECHO DSP 机架数值行为。
// 用法：dotnet run -c Release。任何 FAIL 都会让进程退出码非 0。
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace CelesteDspCoreSmoke;

internal static partial class Native
{
    private const string Lib = "celeste_dsp_core";

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
        {
            if (name != Lib)
                return IntPtr.Zero;
            var local = Path.Combine(AppContext.BaseDirectory, "celeste_dsp_core.dll");
            return File.Exists(local) ? NativeLibrary.Load(local) : IntPtr.Zero;
        });
    }

    [DllImport(Lib)] public static extern int celeste_dsp_create(double sampleRate, int maxBlockFrames, int channels);
    [DllImport(Lib)] public static extern void celeste_dsp_destroy();
    [DllImport(Lib)] public static extern void celeste_dsp_reset();
    [DllImport(Lib)] public static extern int celeste_dsp_process(float[] interleaved, int frames);
    [DllImport(Lib)] public static extern int celeste_dsp_version();

    [DllImport(Lib)] public static extern int celeste_dsp_eq_set_enabled(int enabled);
    [DllImport(Lib)] public static extern int celeste_dsp_eq_set_preamp(float preampDb);
    [DllImport(Lib)] public static extern int celeste_dsp_eq_set_band(int index, float gainDb, float freqHz, float q, int filterType, int bandEnabled);
    [DllImport(Lib)] public static extern int celeste_dsp_eq_set_all(float[] gains, float[] freqs, float[] qs, int[] types, int[] bandEnabled, float preampDb, int eqEnabled);
    [DllImport(Lib)] public static extern int celeste_dsp_eq_get_all(float[] gains, float[] freqs, float[] qs, int[] types, int[] bandEnabled, out float preampDb, out int eqEnabled);
    [DllImport(Lib)] public static extern int celeste_dsp_eq_reset_flat();
    [DllImport(Lib)] public static extern int celeste_dsp_eq_builtin_count();
    [DllImport(Lib)] public static extern int celeste_dsp_eq_builtin_get(int index, StringBuilder nameOut, int nameCap, out float preampDb, float[] gains, float[] freqs, float[] qs, int[] types, int[] bandEnabled);

    [DllImport(Lib)] public static extern int celeste_dsp_compressor_set(int enabled, float thresholdDb, float ratio, float attackMs, float releaseMs, float kneeDb, float makeupDb, float mix);
    [DllImport(Lib)] public static extern int celeste_dsp_compressor_get(out int enabled, out float thresholdDb, out float ratio, out float attackMs, out float releaseMs, out float kneeDb, out float makeupDb, out float mix);
    [DllImport(Lib)] public static extern float celeste_dsp_compressor_gr_db();

    [DllImport(Lib)] public static extern int celeste_dsp_crossfeed_set(int enabled, float amount, float cutoffHz);
    [DllImport(Lib)] public static extern int celeste_dsp_crossfeed_get(out int enabled, out float amount, out float cutoffHz);
    [DllImport(Lib)] public static extern int celeste_dsp_stereofield_set(int enabled, float width, float centerGainDb, float sideGainDb);
    [DllImport(Lib)] public static extern int celeste_dsp_stereofield_get(out int enabled, out float width, out float centerGainDb, out float sideGainDb);
    [DllImport(Lib)] public static extern int celeste_dsp_matrix_set(int enabled, float leftToLeft, float rightToLeft, float leftToRight, float rightToRight);
    [DllImport(Lib)] public static extern int celeste_dsp_matrix_get(out int enabled, out float leftToLeft, out float rightToLeft, out float leftToRight, out float rightToRight);

    [DllImport(Lib)] public static extern int celeste_dsp_balance_set(int enabled, float balance, float leftGainDb, float rightGainDb, float[] leftBandGains, float[] rightBandGains, float leftDelayMs, float rightDelayMs, int swapLeftRight, int monoMode, int invertLeft, int invertRight, int constantPower);
    [DllImport(Lib)] public static extern int celeste_dsp_balance_get(out int enabled, out float balance, out float leftGainDb, out float rightGainDb, float[] leftBandGains, float[] rightBandGains, out float leftDelayMs, out float rightDelayMs, out int swapLeftRight, out int monoMode, out int invertLeft, out int invertRight, out int constantPower);

    [DllImport(Lib)] public static extern int celeste_dsp_headroom_set_db(float headroomDb);
    [DllImport(Lib)] public static extern float celeste_dsp_headroom_get_db();

    [DllImport(Lib)] public static extern int celeste_dsp_rg_set(float trackGainDb, float albumGainDb, float peak, int mode, float preampDb, int preventClipping);
    [DllImport(Lib)] public static extern int celeste_dsp_rg_get(out float trackGainDb, out float albumGainDb, out float peak, out int mode, out float preampDb, out int preventClipping);
    [DllImport(Lib)] public static extern float celeste_dsp_rg_applied_db();

    [DllImport(Lib)] public static extern int celeste_dsp_safety_set_enabled(int enabled);
    [DllImport(Lib)] public static extern int celeste_dsp_safety_get_enabled();
    [DllImport(Lib)] public static extern int celeste_dsp_set_upstream_active(int active);

    [DllImport(Lib)] public static extern int celeste_dsp_rack_set_order(int[] order);
    [DllImport(Lib)] public static extern int celeste_dsp_rack_get_order(int[] order);
    [DllImport(Lib)] public static extern int celeste_dsp_rack_reset_default();

    [DllImport(Lib)] public static extern int celeste_dsp_conv_load_ir(float[] taps, int tapsChannels, int tapsLen, double sourceSampleRate);
    [DllImport(Lib)] public static extern int celeste_dsp_conv_clear();
    [DllImport(Lib)] public static extern int celeste_dsp_conv_set_enabled(int enabled);
    [DllImport(Lib)] public static extern int celeste_dsp_conv_set_trim_db(float trimDb);
    [DllImport(Lib)] public static extern int celeste_dsp_conv_get_state(out int enabled, out int tapCount, out double sampleRate, out float trimDb, out int latencySamples, out int clippingRisk, out int hasError);

    [DllImport(Lib)] public static extern int celeste_dsp_is_active();
    [DllImport(Lib)] public static extern int celeste_dsp_has_clipping_risk();
    [DllImport(Lib)] public static extern int celeste_dsp_limiter_protecting();
    [DllImport(Lib)] public static extern float celeste_dsp_limiter_gr_db();
    [DllImport(Lib)] public static extern float celeste_dsp_limiter_ceiling_db();
}

internal static class Program
{
    private const int Sr = 48000;
    private const int Block = 1024;
    private const int Frames = Sr; // 1 秒

    private static int _pass;
    private static int _fail;

    private static string FirstDiff(float[] a, float[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                return $"首个差异 idx={i} (帧 {i / 2} 声道 {i % 2}): {a[i]:R} vs {b[i]:R}";
        }
        return "无差异";
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"PASS  {name}  {detail}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"FAIL  {name}  {detail}");
        }
    }

    // ── 工具 ──────────────────────────────────────────────────────────────

    private static float[] Interleave(float l, float r, int frames) // 直流双声道
    {
        var buf = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            buf[i * 2] = l;
            buf[i * 2 + 1] = r;
        }
        return buf;
    }

    private static float[] SineStereo(float ampL, double freqL, float ampR, double freqR, int frames, double phaseR = 0)
    {
        var buf = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            buf[i * 2] = (float)(ampL * Math.Sin(2 * Math.PI * freqL * i / Sr));
            buf[i * 2 + 1] = (float)(ampR * Math.Sin(2 * Math.PI * freqR * i / Sr + phaseR));
        }
        return buf;
    }

    private static float[] RandomBlock(int seed, int frames)
    {
        var rng = new Random(seed);
        var buf = new float[frames * 2];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = (float)(rng.NextDouble() * 1.8 - 0.9);
        return buf;
    }

    private static double Rms(float[] buf, int startFrame, int frames, int channel)
    {
        double sum = 0;
        for (int i = startFrame; i < startFrame + frames; i++)
            sum += (double)buf[i * 2 + channel] * buf[i * 2 + channel];
        return Math.Sqrt(sum / frames);
    }

    private static float Peak(float[] buf, int startFrame, int frames)
    {
        float peak = 0;
        for (int i = startFrame; i < startFrame + frames; i++)
        {
            peak = Math.Max(peak, Math.Abs(buf[i * 2]));
            peak = Math.Max(peak, Math.Abs(buf[i * 2 + 1]));
        }
        return peak;
    }

    private static void Feed(float[] signal)
    {
        for (int off = 0; off < signal.Length; off += Block * 2)
        {
            int n = Math.Min(Block * 2, signal.Length - off);
            var slice = new float[n];
            Array.Copy(signal, off, slice, 0, n);
            int rc = Native.celeste_dsp_process(slice, n / 2);
            if (rc != n / 2)
                throw new InvalidOperationException($"process rc={rc}");
            Array.Copy(slice, 0, signal, off, n);
        }
    }

    private static float[] NewEqArrays(out float[] gains, out float[] freqs, out float[] qs, out int[] types, out int[] en)
    {
        gains = new float[31];
        freqs = new float[31];
        qs = new float[31];
        types = new int[31];
        en = new int[31];
        for (int i = 0; i < 31; i++)
        {
            gains[i] = 0f;
            freqs[i] = 1000f;
            qs[i] = 1f;
            types[i] = 0;
            en[i] = 1;
        }
        return gains;
    }

    // ─────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────
    // 实证剖分：新引擎 + 单模块隔离，DC 直通，定位神秘增益来源
    // ─────────────────────────────────────────────────────────────────────

    private static void ProbeFresh(string name, Action setup, float l, float r)
    {
        Native.celeste_dsp_destroy();
        if (Native.celeste_dsp_create(Sr, Block, 2) != 0)
        {
            Check(name, false, "create 失败");
            return;
        }
        Native.celeste_dsp_safety_set_enabled(1);
        setup();
        var buf = Interleave(l, r, Block);
        Feed(buf);
        double ratioL = buf[0] / l;
        double ratioR = buf[1] / r;
        Check(name, Math.Abs(ratioL - 1) < 1e-6 && Math.Abs(ratioR - 1) < 1e-6,
            $"ratioL={ratioL:R} ratioR={ratioR:R}");
    }

    private static void RunProbes()
    {
        ProbeFresh("probe_all_off", () => { }, 0.5f, -0.2f); // 全关：链不活跃，应精确

        ProbeFresh("probe_matrix_identity", () =>
            Native.celeste_dsp_matrix_set(1, 1, 0, 0, 1), 0.5f, -0.2f);

        ProbeFresh("probe_matrix_identity_safety_off", () =>
        {
            Native.celeste_dsp_matrix_set(1, 1, 0, 0, 1);
            Native.celeste_dsp_safety_set_enabled(0);
        }, 0.5f, -0.2f);

        ProbeFresh("probe_eq_flat_only", () =>
        {
            NewEqArrays(out var g, out var f, out var q, out var t, out var en);
            Native.celeste_dsp_eq_set_all(g, f, q, t, en, 0f, 1);
        }, 0.5f, -0.2f);

        ProbeFresh("probe_comp_noop_only", () =>
            Native.celeste_dsp_compressor_set(1, 0, 1, 10, 120, 0, 0, 1), 0.5f, -0.2f);

        ProbeFresh("probe_crossfeed_zero_amount", () =>
            Native.celeste_dsp_crossfeed_set(1, 0f, 700), 0.5f, -0.2f);

        ProbeFresh("probe_stereofield_identity", () =>
            Native.celeste_dsp_stereofield_set(1, 1, 0, 0), 0.5f, -0.2f);

        ProbeFresh("probe_balance_identity", () =>
            Native.celeste_dsp_balance_set(1, 0, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 0),
            0.5f, -0.2f);

        ProbeFresh("probe_conv_identity_ir", () =>
        {
            var taps = new float[] { 1f };
            Native.celeste_dsp_conv_load_ir(taps, 1, 1, Sr);
            Native.celeste_dsp_conv_set_enabled(1);
        }, 0.5f, -0.2f);

        // 序列复现：重放主测试到 rnd3 的关键步骤，定位状态残留
        ReproSequence();
    }

    // ─────────────────────────────────────────────────────────────────────
    // 二分诊断：每个变体全新引擎 + 单一变量，最后检查同一个 rnd3
    // ─────────────────────────────────────────────────────────────────────

    private static void FreshEngine()
    {
        Native.celeste_dsp_destroy();
        if (Native.celeste_dsp_create(Sr, Block, 2) != 0) throw new InvalidOperationException("create 失败");
        Native.celeste_dsp_safety_set_enabled(1);
    }

    private static void DisableAll()
    {
        Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
        Native.celeste_dsp_crossfeed_set(0, 0.25f, 700);
        Native.celeste_dsp_stereofield_set(0, 1, 0, 0);
        Native.celeste_dsp_matrix_set(0, 1, 0, 0, 1);
        Native.celeste_dsp_balance_set(0, 0, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 1);
        Native.celeste_dsp_conv_clear();
        Native.celeste_dsp_headroom_set_db(0);
        Native.celeste_dsp_rg_set(0, 0, 1, 0, 0, 1);
        Native.celeste_dsp_eq_set_enabled(0);
        Native.celeste_dsp_set_upstream_active(0);
    }

    private static void BisectCase(string name, Action body, bool resetBeforeCheck = false)
    {
        FreshEngine();
        DisableAll();
        body();
        if (resetBeforeCheck)
            Native.celeste_dsp_reset();
        var c = RandomBlock(9, 2048);
        var cc = (float[])c.Clone();
        Feed(c);
        Check(name, c.AsSpan().SequenceEqual(cc), FirstDiff(c, cc));
    }

    private static void RunBisect()
    {
        // 0) 对照：什么都不做
        BisectCase("bisect_control", () => { });

        // 1) 完整压缩器循环（复现主测试 compressor_off_bitperfect / repro_3 失败）
        BisectCase("bisect_comp_full", () =>
        {
            Native.celeste_dsp_compressor_set(1, -20, 4, 1, 10, 6, 0, 1);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
            Console.WriteLine($"    [状态] active={Native.celeste_dsp_is_active()} compGR={Native.celeste_dsp_compressor_gr_db():F3} limGR={Native.celeste_dsp_limiter_gr_db():F4} protecting={Native.celeste_dsp_limiter_protecting()} ceiling={Native.celeste_dsp_limiter_ceiling_db():F2} rgApplied={Native.celeste_dsp_rg_applied_db():F4}");
            Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
        });

        // 2) 同上但禁用后 reset 再检查（若通过 → 状态可被 reset 清零）
        BisectCase("bisect_comp_full_reset", () =>
        {
            Native.celeste_dsp_compressor_set(1, -20, 4, 1, 10, 6, 0, 1);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
            Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
        }, resetBeforeCheck: true);

        // 3) 只喂信号不开压缩器
        BisectCase("bisect_feedonly", () =>
        {
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
        });

        // 4) 压缩器循环 + 安全限幅关
        BisectCase("bisect_comp_safetyoff", () =>
        {
            Native.celeste_dsp_safety_set_enabled(0);
            Native.celeste_dsp_compressor_set(1, -20, 4, 1, 10, 6, 0, 1);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
            Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
            Native.celeste_dsp_safety_set_enabled(1);
        });

        // 5) 压缩器 loud 换 1.2 幅度（限幅器必然介入，验证其残留可见）
        BisectCase("bisect_comp_loud12", () =>
        {
            Native.celeste_dsp_compressor_set(1, -20, 4, 1, 10, 6, 0, 1);
            Feed(SineStereo(1.2f, 1000, 1.2f, 1000, Frames));
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
            Console.WriteLine($"    [状态] limGR={Native.celeste_dsp_limiter_gr_db():F4} protecting={Native.celeste_dsp_limiter_protecting()}");
            Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
        });

        // 6) EQ 预设加载后即禁用（复现 repro_3 的 preset 步骤，无压缩器）
        BisectCase("bisect_eq_preset_only", () =>
        {
            var pg = new float[31]; var pf = new float[31]; var pq = new float[31];
            var pt = new int[31]; var pe = new int[31];
            Native.celeste_dsp_eq_builtin_get(15, new StringBuilder(128), 128, out var p15pre, pg, pf, pq, pt, pe);
            Native.celeste_dsp_eq_set_all(pg, pf, pq, pt, pe, p15pre, 1);
            Native.celeste_dsp_eq_set_enabled(0);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
        });

        // 7) EQ 启用喂 1s 再禁用，淡出在 quiet 中完成
        BisectCase("bisect_eq_enabled_settled", () =>
        {
            NewEqArrays(out var g, out var f, out var q, out var t, out var en);
            Native.celeste_dsp_eq_set_all(g, f, q, t, en, 0f, 1);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Native.celeste_dsp_eq_set_enabled(0);
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
        });

        // 8) balance 启用→禁用：前 576 样本（12ms 开关淡出）预期不精确，
        //    淡出沉淀后必须回到 bit-exact（ECHO 设计行为，与 EQ 6ms 淡出同性质）
        FreshEngine();
        DisableAll();
        Native.celeste_dsp_balance_set(1, 0, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 1);
        Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
        Native.celeste_dsp_balance_set(0, 0, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 1);
        var bal = RandomBlock(9, 2048);
        var balc = (float[])bal.Clone();
        Feed(bal);
        bool balHeadDiff = BitConverter.SingleToInt32Bits(bal[0]) != BitConverter.SingleToInt32Bits(balc[0]);
        bool balTailExact = true;
        for (int i = 600 * 2; i < bal.Length; i++)
            if (BitConverter.SingleToInt32Bits(bal[i]) != BitConverter.SingleToInt32Bits(balc[i])) { balTailExact = false; break; }
        Check("bisect_balance_cycle", balHeadDiff && balTailExact,
            $"头部淡出中差异={balHeadDiff}(预期True) 600帧后精确={balTailExact}(预期True)");

        // 9) balance 循环 + 1s quiet 沉淀
        BisectCase("bisect_balance_settled", () =>
        {
            Native.celeste_dsp_balance_set(1, 0, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 1);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Native.celeste_dsp_balance_set(0, 0, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 1);
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
        });

        // 10) upstream active（ceiling -1dB）下的压缩器循环
        BisectCase("bisect_upstream_active", () =>
        {
            Native.celeste_dsp_set_upstream_active(1);
            Native.celeste_dsp_compressor_set(1, -20, 4, 1, 10, 6, 0, 1);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
            Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
            Native.celeste_dsp_set_upstream_active(0);
        });

        // 11) 仅 rg_set
        BisectCase("bisect_rg_only", () => { });

        // 12) stereofield 启用→禁用
        BisectCase("bisect_stereofield_cycle", () =>
        {
            Native.celeste_dsp_stereofield_set(1, 1, 0, 0);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Native.celeste_dsp_stereofield_set(0, 1, 0, 0);
        });

        // 13) crossfeed 启用→禁用
        BisectCase("bisect_crossfeed_cycle", () =>
        {
            Native.celeste_dsp_crossfeed_set(1, 0.25f, 700);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Native.celeste_dsp_crossfeed_set(0, 0.25f, 700);
        });

        // 14) matrix 启用→禁用
        BisectCase("bisect_matrix_cycle", () =>
        {
            Native.celeste_dsp_matrix_set(1, 1, 0, 0, 1);
            Feed(SineStereo(0.5f, 1000, 0.5f, 1000, Frames));
            Native.celeste_dsp_matrix_set(0, 1, 0, 0, 1);
        });

        // 15) 限幅器释放曲线实测：loud12 后每 100ms 打点
        BisectCase("bisect_limiter_release", () =>
        {
            var hot = SineStereo(1.2f, 1000, 1.2f, 1000, Frames);
            Feed(hot);
            Console.WriteLine($"    [release t=0ms]   limGR={Native.celeste_dsp_limiter_gr_db():F8} peak={Peak(hot, Sr / 2, Sr / 4):F4}");
            for (int i = 1; i <= 10; i++)
            {
                Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Sr / 10));
                Console.WriteLine($"    [release t={i * 100}ms] limGR={Native.celeste_dsp_limiter_gr_db():F8}");
            }
        });

        // 16) repro_3 全程追踪：每步打印限幅器状态，定位首次介入点
        BisectCase("bisect_repro3_trace", () =>
        {
            void Dump(string tag)
            {
                Console.WriteLine($"    [{tag}] active={Native.celeste_dsp_is_active()} limProt={Native.celeste_dsp_limiter_protecting()} " +
                    $"limGR={Native.celeste_dsp_limiter_gr_db():F8} compGR={Native.celeste_dsp_compressor_gr_db():F4}");
            }

            var a = RandomBlock(42, 2048);
            Feed(a);
            Dump($"1.bypass rnd peak={Peak(a, 0, 2048):F4}");

            NewEqArrays(out var g, out var f, out var q, out var t, out var en);
            g[17] = 6f;
            Native.celeste_dsp_eq_set_all(g, f, q, t, en, 0f, 1);
            var sine = SineStereo(0.25f, 1000, 0.25f, 1000, Frames);
            Feed(sine);
            Dump($"2.eq+6dB sine peak={Peak(sine, Sr / 2, Sr / 4):F4}");

            Native.celeste_dsp_eq_set_enabled(0);
            var b = RandomBlock(7, 2048);
            Feed(b);
            Dump($"3.rnd2(淡出中) peak={Peak(b, 0, 2048):F4}");

            Feed(new float[Frames * 2]);
            Dump("4.settle zeros 1s");

            var pg = new float[31]; var pf = new float[31]; var pq = new float[31];
            var pt = new int[31]; var pe = new int[31];
            Native.celeste_dsp_eq_builtin_get(15, new StringBuilder(128), 128, out var p15pre, pg, pf, pq, pt, pe);
            Console.WriteLine($"    [preset15] preamp={p15pre} g1k={pg[17]}");
            Native.celeste_dsp_eq_set_all(pg, pf, pq, pt, pe, p15pre, 1);
            Native.celeste_dsp_eq_set_enabled(0);
            Dump("5.preset加载即禁用(无喂音)");

            Native.celeste_dsp_compressor_set(1, -20, 4, 1, 10, 6, 0, 1);
            var loud = SineStereo(0.5f, 1000, 0.5f, 1000, Frames);
            Feed(loud);
            Dump($"6.comp loud peak={Peak(loud, Sr / 2, Sr / 4):F4}");

            var quiet = SineStereo(0.01f, 1000, 0.01f, 1000, Frames);
            Feed(quiet);
            Dump("7.comp quiet 1s");

            Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
            Dump("8.comp禁用");
        });

        // 17) 逐块追踪：loud 每块的峰值 + 限幅器 GR，定位 1.000114 从哪来
        BisectCase("bisect_blockwise", () =>
        {
            Native.celeste_dsp_compressor_set(1, -20, 4, 1, 10, 6, 0, 1);
            var loud = SineStereo(0.5f, 1000, 0.5f, 1000, Frames);
            int block = 0;
            for (int off = 0; off < loud.Length; off += Block * 2)
            {
                int n = Math.Min(Block * 2, loud.Length - off);
                var slice = new float[n];
                Array.Copy(loud, off, slice, 0, n);
                Native.celeste_dsp_process(slice, n / 2);
                Array.Copy(slice, 0, loud, off, n);
                float pk = 0f;
                foreach (var v in slice) pk = Math.Max(pk, Math.Abs(v));
                float gr = Native.celeste_dsp_limiter_gr_db();
                int prot = Native.celeste_dsp_limiter_protecting();
                if (prot == 1 || pk > 0.99f || block < 3 || block >= 44)
                    Console.WriteLine($"    [block {block,2} frames={n / 2,4}] outPeak={pk:F6} limGR={gr:F8} prot={prot}");
                block++;
            }
            Console.WriteLine($"    [loud 结束] outPeak={Peak(loud, Sr / 2, Sr / 4):F6}");
            Feed(SineStereo(0.01f, 1000, 0.01f, 1000, Frames));
            Console.WriteLine($"    [quiet 1s 后] limGR={Native.celeste_dsp_limiter_gr_db():F8}");
            Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
        });
    }

    private static void ReproSequence()
    {
        Native.celeste_dsp_destroy();
        if (Native.celeste_dsp_create(Sr, Block, 2) != 0) { Check("repro_create", false); return; }
        Native.celeste_dsp_safety_set_enabled(1);

        // 全关
        Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
        Native.celeste_dsp_crossfeed_set(0, 0.25f, 700);
        Native.celeste_dsp_stereofield_set(0, 1, 0, 0);
        Native.celeste_dsp_matrix_set(0, 1, 0, 0, 1);
        Native.celeste_dsp_balance_set(0, 0, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 1);
        Native.celeste_dsp_conv_clear();
        Native.celeste_dsp_headroom_set_db(0);
        Native.celeste_dsp_rg_set(0, 0, 1, 0, 0, 1);

        // 1) bypass（全关）bit-perfect
        var a = RandomBlock(42, 2048); var ac = (float[])a.Clone();
        Feed(a);
        Check("repro_1_bypass", a.AsSpan().SequenceEqual(ac), FirstDiff(a, ac));

        // 2) EQ 启用（+6dB@1k）1s → 禁用 → 2 块随机（淡出中，预期不精确）
        NewEqArrays(out var g, out var f, out var q, out var t, out var en);
        g[17] = 6f;
        Native.celeste_dsp_eq_set_all(g, f, q, t, en, 0f, 1);
        var sine = SineStereo(0.25f, 1000, 0.25f, 1000, Frames);
        Feed(sine);
        Native.celeste_dsp_eq_set_enabled(0);
        var b = RandomBlock(7, 2048); var bc = (float[])b.Clone();
        Feed(b);
        bool bFade = b.AsSpan().SequenceEqual(bc);
        Console.WriteLine($"  [repro] rnd2(2块,淡出中) bit-exact={bFade}");

        // 2b) 再喂 1s 让淡出彻底完成，然后随机块应精确
        var settle = new float[Frames * 2];
        Feed(settle);
        var b2 = RandomBlock(8, 2048); var b2c = (float[])b2.Clone();
        Feed(b2);
        Check("repro_2b_after_fade_settle", b2.AsSpan().SequenceEqual(b2c), FirstDiff(b2, b2c));

        // 3) 预设循环：EQ 启用 preset 后禁用，然后立刻喂 1s loud（淡出随播放完成）
        var pg = new float[31]; var pf = new float[31]; var pq = new float[31];
        var pt = new int[31]; var pe = new int[31];
        Native.celeste_dsp_eq_builtin_get(15, new StringBuilder(128), 128, out var p15pre, pg, pf, pq, pt, pe);
        Native.celeste_dsp_eq_set_all(pg, pf, pq, pt, pe, p15pre, 1);
        Native.celeste_dsp_eq_set_enabled(0);
        Native.celeste_dsp_compressor_set(1, -20, 4, 1, 10, 6, 0, 1);
        var loud = SineStereo(0.5f, 1000, 0.5f, 1000, Frames);
        Feed(loud);
        var quiet = SineStereo(0.01f, 1000, 0.01f, 1000, Frames);
        Feed(quiet);
        Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
        var c = RandomBlock(9, 2048); var cc = (float[])c.Clone();
        Feed(c);
        Check("repro_3_rnd3_after_preset_cycle", c.AsSpan().SequenceEqual(cc), FirstDiff(c, cc));
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "probe")
        {
            RunProbes();
            Console.WriteLine($"\n==== probe: {_pass} passed, {_fail} failed ====");
            return _fail == 0 ? 0 : 1;
        }

        if (args.Length > 0 && args[0] == "bisect")
        {
            RunBisect();
            Console.WriteLine($"\n==== bisect: {_pass} passed, {_fail} failed ====");
            return _fail == 0 ? 0 : 1;
        }

        Console.WriteLine($"DLL path: {Path.Combine(AppContext.BaseDirectory, "celeste_dsp_core.dll")}");
        Check("version", Native.celeste_dsp_version() == 0x00010000, $"v={Native.celeste_dsp_version():X8}");

        // ── 生命周期与参数校验 ──────────────────────────────────────────
        Check("create", Native.celeste_dsp_create(Sr, Block, 2) == 0);
        Check("create_bad_rate", Native.celeste_dsp_create(100, Block, 2) == -2);
        Check("create_bad_block", Native.celeste_dsp_create(Sr, 0, 2) == -2);
        Check("create_bad_ch", Native.celeste_dsp_create(Sr, Block, 0) == -2);
        Check("create_ok_again", Native.celeste_dsp_create(Sr, Block, 2) == 0);

        // ── 全旁路 bit-perfect ─────────────────────────────────────────
        Native.celeste_dsp_eq_set_enabled(0);
        Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
        Native.celeste_dsp_crossfeed_set(0, 0.25f, 700);
        Native.celeste_dsp_stereofield_set(0, 1, 0, 0);
        Native.celeste_dsp_matrix_set(0, 1, 0, 0, 1);
        Native.celeste_dsp_balance_set(0, 0, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 1);
        Native.celeste_dsp_conv_clear();
        Native.celeste_dsp_headroom_set_db(0);
        Native.celeste_dsp_rg_set(0, 0, 1, 0, 0, 1);
        Native.celeste_dsp_safety_set_enabled(1);
        Native.celeste_dsp_set_upstream_active(0);

        var rnd = RandomBlock(42, 2048);
        var rndCopy = (float[])rnd.Clone();
        Feed(rnd);
        Check("bypass_bitperfect", rnd.AsSpan().SequenceEqual(rndCopy), $"is_active={Native.celeste_dsp_is_active()}");

        // ── EQ：1kHz +6dB → RMS ×2 ─────────────────────────────────────
        NewEqArrays(out var g, out var f, out var q, out var t, out var en);
        g[17] = 6f; // 1000Hz 槽
        f[17] = 1000f;
        Check("eq_set_all", Native.celeste_dsp_eq_set_all(g, f, q, t, en, 0f, 1) == 0);
        var sine = SineStereo(0.25f, 1000, 0.25f, 1000, Frames);
        var sineCopy = (float[])sine.Clone();
        Feed(sine);
        double rmsIn = Rms(sineCopy, Sr / 2, Sr / 4, 0);
        double rmsOut = Rms(sine, Sr / 2, Sr / 4, 0);
        double ratio = rmsOut / rmsIn;
        Check("eq_gain_6db", Math.Abs(ratio - 2.0) < 0.06, $"ratio={ratio:F3} (期望≈2.0)");
        Check("eq_active", Native.celeste_dsp_is_active() == 1);

        // EQ 关 → 烧掉 6ms 旁路淡出（288 样本@48k）后再比 bit-perfect
        Native.celeste_dsp_eq_set_enabled(0);
        Feed(new float[Sr / 4 * 2]); // 0.25s 淡出燃烧
        var rnd2 = RandomBlock(7, 2048);
        var rnd2Copy = (float[])rnd2.Clone();
        Feed(rnd2);
        Check("eq_off_bitperfect", rnd2.AsSpan().SequenceEqual(rnd2Copy), FirstDiff(rnd2, rnd2Copy));

        // EQ 回读一致
        NewEqArrays(out var g2, out var f2, out var q2, out var t2, out var en2);
        g2[17] = 6f; f2[17] = 1000f; t2[17] = 1; // LowShelf
        Native.celeste_dsp_eq_set_all(g2, f2, q2, t2, en2, -3f, 1);
        var g3 = new float[31]; var f3 = new float[31]; var q3 = new float[31];
        var t3 = new int[31]; var en3 = new int[31];
        Native.celeste_dsp_eq_get_all(g3, f3, q3, t3, en3, out var preamp, out var eqEn);
        Check("eq_roundtrip",
            g3.AsSpan().SequenceEqual(g2) && f3.AsSpan().SequenceEqual(f2)
            && q3.AsSpan().SequenceEqual(q2) && t3.AsSpan().SequenceEqual(t2)
            && en3.AsSpan().SequenceEqual(en2) && preamp == -3f && eqEn == 1,
            $"preamp={preamp} type={t3[17]}");

        // ── 内置预设 ───────────────────────────────────────────────────
        int presetCount = Native.celeste_dsp_eq_builtin_count();
        Check("eq_builtin_count", presetCount >= 8, $"count={presetCount}");
        var firstName = new StringBuilder(128);
        var pg = new float[31]; var pf = new float[31]; var pq = new float[31];
        var pt = new int[31]; var pe = new int[31];
        Native.celeste_dsp_eq_builtin_get(0, firstName, firstName.Capacity, out var pp, pg, pf, pq, pt, pe);
        bool flatAllZero = true;
        foreach (var v in pg)
            if (v != 0f) { flatAllZero = false; break; }
        Check("eq_builtin_get", firstName.Length > 0 && flatAllZero && pp == 0f,
            $"preset0=\"{firstName}\" preamp={pp} 全平={flatAllZero}");
        bool presetRoundtrip = true;
        for (int i = 0; i < presetCount; i++)
        {
            var name = new StringBuilder(128);
            Native.celeste_dsp_eq_builtin_get(i, name, name.Capacity, out var pa,
                pg, pf, pq, pt, pe);
            if (Native.celeste_dsp_eq_set_all(pg, pf, pq, pt, pe, pa, 1) != 0) { presetRoundtrip = false; break; }
        }
        Check("eq_builtin_set_all", presetRoundtrip, $"{presetCount} 个预设逐个下发");
        Native.celeste_dsp_eq_set_enabled(0);

        // ── 压缩器：响信号被压、轻信号不压 ─────────────────────────────
        Native.celeste_dsp_compressor_set(1, -20, 4, 1, 10, 6, 0, 1);
        var loud = SineStereo(0.5f, 1000, 0.5f, 1000, Frames);
        var loudCopy = (float[])loud.Clone();
        Feed(loud);
        double loudIn = Rms(loudCopy, Sr / 2, Sr / 8, 0);
        double loudOut = Rms(loud, Sr / 2, Sr / 8, 0);
        float gr = Native.celeste_dsp_compressor_gr_db();
        // ECHO 约定：GR 以正数 dB 存储（gainReductionDb = -20·log10(gain)）
        Check("compressor_gr", loudOut < loudIn * 0.75 && gr > 1f,
            $"in={loudIn:F4} out={loudOut:F4} gr={gr:F2}dB");

        var quiet = SineStereo(0.01f, 1000, 0.01f, 1000, Frames);
        var quietCopy = (float[])quiet.Clone();
        Feed(quiet);
        double quietIn = Rms(quietCopy, Sr / 2, Sr / 8, 0);
        double quietOut = Rms(quiet, Sr / 2, Sr / 8, 0);
        Check("compressor_quiet_passthrough", Math.Abs(quietOut / quietIn - 1.0) < 0.02,
            $"ratio={quietOut / quietIn:F4}");

        Native.celeste_dsp_compressor_set(0, -18, 4, 10, 120, 6, 0, 1);
        var rnd3 = RandomBlock(9, 2048);
        var rnd3Copy = (float[])rnd3.Clone();
        Feed(rnd3);
        Check("compressor_off_bitperfect", rnd3.AsSpan().SequenceEqual(rnd3Copy), FirstDiff(rnd3, rnd3Copy));

        // ── Crossfeed：L 独有信号 → R 获得能量 ─────────────────────────
        Native.celeste_dsp_crossfeed_set(1, 0.5f, 700);
        var lOnly = SineStereo(0.3f, 1000, 0f, 1000, Frames);
        Feed(lOnly);
        double lRms = Rms(lOnly, Sr / 2, Sr / 8, 0);
        double rRms = Rms(lOnly, Sr / 2, Sr / 8, 1);
        Check("crossfeed_side_energy", rRms > 0.02 * lRms && lRms > 0.15,
            $"L={lRms:F4} R={rRms:F4}");
        Native.celeste_dsp_crossfeed_set(0, 0.25f, 700);

        // ── StereoField width=0 → 单声道 ───────────────────────────────
        Native.celeste_dsp_stereofield_set(1, 0f, 0, 0);
        var wide = SineStereo(0.3f, 440, 0.3f, 660, Frames, phaseR: Math.PI / 2);
        Feed(wide);
        double lOut = Rms(wide, Sr / 2, Sr / 8, 0);
        double rOut = Rms(wide, Sr / 2, Sr / 8, 1);
        Check("stereofield_width0_mono", Math.Abs(lOut - rOut) < 1e-4 && lOut > 0.05,
            $"L={lOut:F4} R={rOut:F4}");
        Native.celeste_dsp_stereofield_set(0, 1, 0, 0);

        // ── ChannelMatrix：直流交换验证 ────────────────────────────────
        Native.celeste_dsp_matrix_set(1, 0, 1, 1, 0);
        var dc = Interleave(0.5f, -0.2f, Block);
        Feed(dc);
        Check("matrix_swap_dc",
            Math.Abs(dc[0] - (-0.2f)) < 1e-5 && Math.Abs(dc[1] - 0.5f) < 1e-5
            && Math.Abs(dc[^2] - (-0.2f)) < 1e-5 && Math.Abs(dc[^1] - 0.5f) < 1e-5,
            $"L={dc[0]:R} R={dc[1]:R} 尾帧 L={dc[^2]:R} R={dc[^1]:R}");
        Native.celeste_dsp_matrix_set(0, 1, 0, 0, 1);

        // ── ChannelBalance：balance=-1 → R 静音 ────────────────────────
        Native.celeste_dsp_balance_set(1, -1, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 1);
        var bal = SineStereo(0.4f, 300, 0.4f, 300, Frames);
        Feed(bal);
        double balL = Rms(bal, Sr / 2, Sr / 8, 0);
        double balR = Rms(bal, Sr / 2, Sr / 8, 1);
        Check("balance_full_left", balR < 1e-3 && Math.Abs(balL - 0.4 / Math.Sqrt(2)) < 0.02,
            $"L={balL:F4} R={balR:F5}");
        Native.celeste_dsp_balance_set(0, 0, 0, 0, new float[3], new float[3], 0, 0, 0, 0, 0, 0, 1);

        // ── Headroom：-6dB → 幅度减半 ──────────────────────────────────
        Native.celeste_dsp_headroom_set_db(-6);
        Check("headroom_get", Math.Abs(Native.celeste_dsp_headroom_get_db() - (-6)) < 1e-4);
        var hr = SineStereo(0.25f, 1000, 0.25f, 1000, Frames);
        var hrCopy = (float[])hr.Clone();
        Feed(hr);
        double hrIn = Rms(hrCopy, Sr / 2, Sr / 8, 0);
        double hrOut = Rms(hr, Sr / 2, Sr / 8, 0);
        Check("headroom_6db", Math.Abs(hrOut / hrIn - 0.5) < 0.03, $"ratio={hrOut / hrIn:F4}");
        Native.celeste_dsp_headroom_set_db(0);

        // ── ReplayGain round-trip ──────────────────────────────────────
        Native.celeste_dsp_rg_set(3.5f, -1.25f, 0.9f, 1, -2f, 1);
        Native.celeste_dsp_rg_get(out var rgT, out var rgA, out var rgP, out var rgM, out var rgPre, out var rgPC);
        Check("rg_roundtrip", rgT == 3.5f && rgA == -1.25f && rgP == 0.9f && rgM == 1 && rgPre == -2f && rgPC == 1,
            $"t={rgT} a={rgA} m={rgM}");
        Native.celeste_dsp_rg_set(0, 0, 1, 0, 0, 1);

        // ── 限幅器：1.2 幅度热信号 → 被压到 ceiling 以下 ────────────────
        // 需要链处于 active：开一个全平 EQ（增益全 0）
        g[17] = 0f; // 前面 eq_gain 测试把它改成了 6dB，这里恢复全平
        Native.celeste_dsp_eq_set_all(g, f, q, t, en, 0f, 1);
        Native.celeste_dsp_safety_set_enabled(1);
        var hot = SineStereo(1.2f, 1000, 1.2f, 1000, Frames);
        Feed(hot);
        float hotPeak = Peak(hot, Sr / 2, Sr / 4);
        int protecting = Native.celeste_dsp_limiter_protecting();
        float limiterGr = Native.celeste_dsp_limiter_gr_db();
        Check("limiter_bounds", hotPeak <= 1.0002f && protecting == 1 && limiterGr > 0.5f,
            $"peak={hotPeak:F4} protecting={protecting} gr={limiterGr:F2}dB");
        Native.celeste_dsp_eq_set_enabled(0);
        // 限幅器释放已修复 float32 精度地板（Celeste 补丁：停滞即归位），
        // 这里仍保留 reset 作为测试卫生，双重保险。
        Native.celeste_dsp_reset();

        // upstream 标志 → ceiling -1dB
        Native.celeste_dsp_set_upstream_active(1);
        Check("upstream_ceiling", Math.Abs(Native.celeste_dsp_limiter_ceiling_db() - (-1)) < 1e-3,
            $"ceiling={Native.celeste_dsp_limiter_ceiling_db()}");
        Native.celeste_dsp_set_upstream_active(0);
        Check("upstream_ceiling0", Math.Abs(Native.celeste_dsp_limiter_ceiling_db()) < 1e-3);

        // ── 机架顺序 ───────────────────────────────────────────────────
        var order = new int[8];
        Native.celeste_dsp_rack_reset_default();
        Native.celeste_dsp_rack_get_order(order);
        Check("rack_default", order.AsSpan().SequenceEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }),
            $"[{string.Join(",", order)}]");
        var reversed = new[] { 7, 6, 5, 4, 3, 2, 1, 0 };
        Check("rack_set_reversed", Native.celeste_dsp_rack_set_order(reversed) == 0);
        Native.celeste_dsp_rack_get_order(order);
        Check("rack_get_reversed", order.AsSpan().SequenceEqual(reversed));
        Check("rack_dup_rejected", Native.celeste_dsp_rack_set_order(new[] { 0, 0, 2, 3, 4, 5, 6, 7 }) == -3);
        Check("rack_unknown_rejected", Native.celeste_dsp_rack_set_order(new[] { 0, 1, 2, 3, 4, 5, 6, 8 }) == -3);
        Native.celeste_dsp_rack_reset_default();

        // ── 卷积：IR 灌入 + delta 响应 + 8192 拒绝 ─────────────────────
        var taps = new float[] { 1f, 0.5f, 0.25f, 0.125f };
        Check("conv_load", Native.celeste_dsp_conv_load_ir(taps, 1, 4, Sr) == 0);
        Native.celeste_dsp_conv_get_state(out _, out var tapCount, out var irSr, out _, out _, out _, out _);
        Check("conv_taps", tapCount == 4 && Math.Abs(irSr - Sr) < 1e-6, $"taps={tapCount} sr={irSr}");
        Native.celeste_dsp_conv_set_trim_db(0);
        Native.celeste_dsp_conv_set_enabled(1);
        var delta = new float[Block * 2];
        delta[30 * 2] = 1f;
        delta[30 * 2 + 1] = 1f;
        var deltaCopy = (float[])delta.Clone();
        Feed(delta);
        bool deltaOk = Math.Abs(delta[30 * 2] - 1f) < 1e-5
            && Math.Abs(delta[31 * 2] - 0.5f) < 1e-5
            && Math.Abs(delta[32 * 2] - 0.25f) < 1e-5
            && Math.Abs(delta[33 * 2] - 0.125f) < 1e-5
            && Math.Abs(delta[34 * 2]) < 1e-6
            && Math.Abs(delta[35 * 2]) < 1e-6;
        Check("conv_delta_response", deltaOk,
            $"y=[{delta[30 * 2]:F4},{delta[31 * 2]:F4},{delta[32 * 2]:F4},{delta[33 * 2]:F4},{delta[34 * 2]:F4}]");

        // 跨块历史延续：delta 放在第一块末尾，尾部应落在第二块
        var delta2 = new float[Block * 4];
        delta2[(Block - 2) * 2] = 1f;
        delta2[(Block - 2) * 2 + 1] = 1f;
        Feed(delta2);
        bool crossOk = Math.Abs(delta2[(Block - 2) * 2] - 1f) < 1e-5
            && Math.Abs(delta2[(Block - 1) * 2] - 0.5f) < 1e-5
            && Math.Abs(delta2[Block * 2] - 0.25f) < 1e-5
            && Math.Abs(delta2[(Block + 1) * 2] - 0.125f) < 1e-5;
        Check("conv_cross_block", crossOk,
            $"y=[{delta2[(Block - 2) * 2]:F4},{delta2[(Block - 1) * 2]:F4},{delta2[Block * 2]:F4},{delta2[(Block + 1) * 2]:F4}]");

        // 8192 上限拒绝
        var tooLong = new float[8193];
        Check("conv_reject_8193", Native.celeste_dsp_conv_load_ir(tooLong, 1, 8193, Sr) == -4);
        var maxOk = new float[8192];
        maxOk[0] = 1f;
        Check("conv_accept_8192", Native.celeste_dsp_conv_load_ir(maxOk, 1, 8192, Sr) == 0);

        // 重采样：96k IR 4 taps @48k → 2 taps
        Check("conv_resample", Native.celeste_dsp_conv_load_ir(taps, 1, 4, 96000) == 0);
        Native.celeste_dsp_conv_get_state(out _, out var tapCount96, out _, out _, out _, out _, out _);
        Check("conv_resample_taps", tapCount96 == 2, $"taps={tapCount96}");

        // 关卷积 → bit-perfect
        Native.celeste_dsp_conv_set_enabled(0);
        Native.celeste_dsp_conv_clear();
        var rnd4 = RandomBlock(11, 2048);
        var rnd4Copy = (float[])rnd4.Clone();
        Feed(rnd4);
        Check("conv_off_bitperfect", rnd4.AsSpan().SequenceEqual(rnd4Copy));

        // ── reset 清零状态 ─────────────────────────────────────────────
        Native.celeste_dsp_conv_load_ir(taps, 1, 4, Sr);
        Native.celeste_dsp_conv_set_enabled(1);
        var pre = new float[Block * 2];
        pre[10 * 2] = 1f; pre[10 * 2 + 1] = 1f;
        Feed(pre);
        Native.celeste_dsp_reset();
        var zeros = new float[Block * 2];
        Feed(zeros);
        bool allZero = true;
        foreach (var v in zeros)
            if (v != 0f) { allZero = false; break; }
        Check("reset_clears_history", allZero);

        // ── destroy 后调用 → -1；重建恢复 ──────────────────────────────
        Native.celeste_dsp_destroy();
        Check("process_after_destroy", Native.celeste_dsp_process(new float[16], 8) == -1);
        Check("setter_after_destroy", Native.celeste_dsp_eq_set_enabled(1) == -1);
        Check("create_after_destroy", Native.celeste_dsp_create(Sr, Block, 2) == 0);
        var rnd5 = RandomBlock(13, 2048);
        var rnd5Copy = (float[])rnd5.Clone();
        Feed(rnd5);
        Check("fresh_engine_bitperfect", rnd5.AsSpan().SequenceEqual(rnd5Copy));

        Native.celeste_dsp_destroy();

        Console.WriteLine($"\n==== {_pass} passed, {_fail} failed ====");
        return _fail == 0 ? 0 : 1;
    }
}
