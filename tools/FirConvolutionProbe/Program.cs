using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FirConvolutionProbe
{
    /// <summary>
    /// 无界面验证房间校正（卷积 FIR）真的在改音频。走的全是产品自己的代码路径：
    ///   IR WAV → RoomCorrectionIrCache.GetOrLoad（反射调用，含重采样/planar 化）
    ///         → celeste_dsp_conv_load_ir / set_trim_db / set_enabled（同一份内核）
    ///         → celeste_dsp_process 喂信号 → 比对输入输出。
    /// 判定：开卷积时输出必须与输入不同、且冲激能量被拖到 IR 长度之后；关卷积时必须逐位相同。
    /// </summary>
    internal static class Program
    {
        private const string Lib = "celeste_dsp_core.dll";
        private const int Rate = 48000;
        private const int Ch = 2;
        private const int Block = 512;

        [DllImport(Lib)] private static extern int celeste_dsp_create(double sampleRate, int maxBlockFrames, int channels);
        [DllImport(Lib)] private static extern void celeste_dsp_destroy();
        [DllImport(Lib)] private static extern void celeste_dsp_reset();
        [DllImport(Lib)] private static extern unsafe int celeste_dsp_process(float* interleaved, int frames);
        [DllImport(Lib)] private static extern int celeste_dsp_conv_load_ir(float[] taps, int tapsChannels, int tapsLen, double sourceSampleRate);
        [DllImport(Lib)] private static extern int celeste_dsp_conv_clear();
        [DllImport(Lib)] private static extern int celeste_dsp_conv_set_enabled(int enabled);
        [DllImport(Lib)] private static extern int celeste_dsp_conv_set_trim_db(float trimDb);
        [DllImport(Lib)] private static extern int celeste_dsp_conv_get_state(
            out int enabled, out int tapCount, out double sampleRate, out float trimDb,
            out int latencySamples, out int clippingRisk, out int hasError);

        private static int _fails;

        private static void Check(bool ok, string what, string detail = "")
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what + (detail.Length > 0 ? "  [" + detail + "]" : ""));
            if (!ok) _fails++;
        }

        private static unsafe float[] Process(float[] interleaved)
        {
            float[] buffer = (float[])interleaved.Clone();
            fixed (float* p = buffer)
            {
                celeste_dsp_process(p, buffer.Length / Ch);
            }
            return buffer;
        }

        private static unsafe double Peak(float[] buf)
        {
            double peak = 0;
            for (int i = 0; i < buf.Length; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
            return peak;
        }

        private static unsafe double Rms(float[] buf, int from, int to)
        {
            double sum = 0;
            int n = 0;
            for (int i = from; i < to && i < buf.Length; i++) { sum += buf[i] * buf[i]; n++; }
            return n == 0 ? 0 : Math.Sqrt(sum / n);
        }

        private static bool Identical(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
            return true;
        }

        /// <summary>用产品代码加载 IR（含重采样）。走反射是因为 RoomCorrectionIrCache 是 internal。</summary>
        private static float[][]? LoadIr(string path)
        {
            var asm = typeof(CelesteMusicPlayer.OpraService).Assembly;
            var type = asm.GetType("CelesteMusicPlayer.RoomCorrectionIrCache", throwOnError: false);
            if (type == null) { Console.WriteLine("    !! 找不到 RoomCorrectionIrCache"); return null; }
            var method = type.GetMethod("GetOrLoad", BindingFlags.Public | BindingFlags.Static);
            if (method == null) { Console.WriteLine("    !! 找不到 GetOrLoad"); return null; }
            return (float[][]?)method.Invoke(null, new object[] { path, Rate });
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string ir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "celeste-fieldtest", "test-ir-2048.wav");
            Console.WriteLine("IR: " + ir);
            if (!File.Exists(ir)) { Console.WriteLine("找不到 IR 文件"); return 2; }

            // ---- 1. 产品代码能否把 IR 解析成 taps -----------------------------------------
            Console.WriteLine("[1] 用产品代码加载 IR");
            var taps = LoadIr(ir);
            if (taps == null) { Check(false, "IR 能被 RoomCorrectionIrCache 读出来"); return 1; }
            int tapsLen = taps[0].Length;
            Console.WriteLine("    taps=" + tapsLen + " 声道=" + taps.Length + " 首样本=" + taps[0][0].ToString("0.000"));
            Check(tapsLen > 16, "IR 解析出有效 taps", tapsLen + " taps");
            Check(tapsLen <= 8192, "taps 没超过内核上限 8192", tapsLen.ToString());
            Check(taps.Length is 1 or 2, "声道数合法", taps.Length + "ch");

            // ---- 2. 建内核 + 上传 IR -----------------------------------------------------
            Console.WriteLine("[2] 建内核并上传 IR");
            int rc = celeste_dsp_create(Rate, 8192, Ch);
            Check(rc == 0, "celeste_dsp_create", "rc=" + rc);
            if (rc != 0) return 1;

            float[] planar = new float[tapsLen * taps.Length];
            Buffer.BlockCopy(taps[0], 0, planar, 0, tapsLen * 4);
            if (taps.Length == 2) Buffer.BlockCopy(taps[1], 0, planar, tapsLen * 4, tapsLen * 4);

            rc = celeste_dsp_conv_load_ir(planar, taps.Length, tapsLen, Rate);
            Check(rc == 0, "内核接受 IR", "rc=" + rc);
            if (rc != 0) { celeste_dsp_destroy(); return 1; }
            Check(celeste_dsp_conv_set_trim_db(0f) == 0, "设置 trim=0dB");
            Check(celeste_dsp_conv_set_enabled(1) == 0, "启用卷积");

            int enabled, tapCount, latency, clipRisk, hasError;
            double srcRate;
            float trim;
            celeste_dsp_conv_get_state(out enabled, out tapCount, out srcRate, out trim, out latency, out clipRisk, out hasError);
            Console.WriteLine("    内核回报: enabled=" + enabled + " taps=" + tapCount + " sr=" + srcRate.ToString("0") +
                              " trim=" + trim.ToString("0.#") + " latency=" + latency + " clippingRisk=" + clipRisk +
                              " hasError=" + hasError);
            Check(enabled == 1, "内核里卷积是开着的");
            Check(tapCount == tapsLen, "内核 taps 与上传一致", tapCount + " vs " + tapsLen);
            Check(hasError == 0, "内核没有报错");

            // ---- 3. 真的在改音频吗 -------------------------------------------------------
            Console.WriteLine("[3] 喂信号看输出");
            // 单点冲激 + 静音：卷积开启时能量会被摊到 IR 长度上，长度后的尾部应该有声音。
            float[] impulse = new float[Block * 2];
            impulse[0] = 0.5f;   // L
            impulse[1] = 0.5f;   // R
            celeste_dsp_reset();
            float[] onOut = Process(impulse);
            float[] onTail = new float[Block * 2];
            Array.Copy(onOut, onTail, onTail.Length);

            celeste_dsp_conv_set_enabled(0);
            celeste_dsp_reset();
            float[] offOut = Process(impulse);

            double tailRms = Rms(onTail, 64 * 2, 400 * 2);
            double offTailRms = Rms(offOut, 64 * 2, 400 * 2);
            Console.WriteLine("    开卷积: 冲激峰值=" + Peak(onOut).ToString("0.0000") +
                              " 64-400 样本 RMS=" + tailRms.ToString("0.000000"));
            Console.WriteLine("    关卷积: 冲激峰值=" + Peak(offOut).ToString("0.0000") +
                              " 64-400 样本 RMS=" + offTailRms.ToString("0.000000"));

            Check(!Identical(onOut, offOut), "开卷积的输出与关卷积不同");
            Check(tailRms > 1e-4, "卷积把能量摊到了冲激之后（说明滤波真的在跑）", tailRms.ToString("0.000000"));
            Check(offTailRms < 1e-5, "关卷积时冲激之后是静音（直通对照）", offTailRms.ToString("0.000000"));

            // ---- 4. 关掉卷积要回到逐位直通 -------------------------------------------------
            Console.WriteLine("[4] 关卷积 = 直通");
            celeste_dsp_conv_set_enabled(0);
            celeste_dsp_reset();
            float[] sine = new float[Block * 2];
            for (int i = 0; i < Block; i++)
            {
                float v = (float)(0.25 * Math.Sin(2 * Math.PI * 1000 * i / Rate));
                sine[i * 2] = v;
                sine[i * 2 + 1] = v;
            }
            float[] passOut = Process(sine);
            Check(Identical(sine, passOut), "关掉卷积后数据逐位不变");

            celeste_dsp_conv_clear();
            celeste_dsp_destroy();

            Console.WriteLine();
            if (_fails == 0) { Console.WriteLine("FIR_CONVOLUTION_OK"); return 0; }
            Console.WriteLine("FIR_CONVOLUTION_FAILS=" + _fails);
            return 1;
        }
    }
}
