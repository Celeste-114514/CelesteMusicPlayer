using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using Xunit;

namespace CelesteMusicPlayer.EqRegression
{
    /// <summary>
    /// EQ / DSP 回归测试。
    /// 这些测试直接驱动 ManagedDspSourceProvider（internal，经 InternalsVisibleTo 可见），
    /// 用纯正弦扫频实测频响，断言「声道隔离」「无八度偏移」「多声道均生效」「无 DSP 时数值直通」等不变量。
    /// </summary>
    internal sealed class SineSource : IWaveSourceProvider
    {
        private readonly WaveFormat _fmt;
        private readonly float[] _samples;
        private int _pos;

        public SineSource(WaveFormat fmt, float[] samples)
        {
            _fmt = fmt;
            _samples = samples;
        }

        public WaveFormat WaveFormat => _fmt;
        public TimeSpan TotalTime => TimeSpan.Zero;
        public (long, long, bool)? ProbeCurrentState => null;
        public bool NextMounted => false;
        public void Seek(TimeSpan position) { }

        public int Read(byte[] buffer, int offset, int count)
        {
            int n = count / 4;
            int bi = offset;
            for (int i = 0; i < n; i++)
            {
                float v = _samples[_pos];
                _pos = (_pos + 1) % _samples.Length;
                BitConverter.GetBytes(v).CopyTo(buffer, bi);
                bi += 4;
            }

            return count;
        }
    }

    public class EqRegressionTests
    {
        private const int Fs = 44100;

        // 标准 10 段 EQ 中心频率：[31,62,125,250,500,1000,2000,4000,8000,16000]
        // 索引 5 = 1000Hz
        private static double[] GainAt1000(double db) => new double[10] { 0, 0, 0, 0, 0, db, 0, 0, 0, 0 };

        [Fact]
        public void Stereo_EqPeakAtDesignFrequency_NotOctaveShifted()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            double peakL = ProbePeakHz(GainAt1000(12.0), fmt, 0);
            double peakR = ProbePeakHz(GainAt1000(12.0), fmt, 1);

            // 修复前（左右共用滤波器实例）峰值会落在 ~2000Hz（一个八度偏移），这里必须落在 1000Hz 附近。
            Assert.InRange(peakL, 850, 1150);
            Assert.InRange(peakR, 850, 1150);
            Assert.True(Math.Abs(peakL - peakR) <= 50,
                $"L/R 峰值应一致（声道对称），实际 L={peakL:0}Hz R={peakR:0}Hz");
        }

        [Fact]
        public void Stereo_1000HzDominatesOver2000Hz()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            double g1000 = MeasureGainDb(GainAt1000(12.0), fmt, 1000, 0);
            double g2000 = MeasureGainDb(GainAt1000(12.0), fmt, 2000, 0);
            Assert.True(g1000 - g2000 >= 3,
                $"修复后 1000Hz 应明显高于 2000Hz（实际差 {g1000 - g2000:0.0}dB）");
        }

        [Fact]
        public void Multichannel_AllChannelsEqAtDesignFrequency()
        {
            // 5.1 = 6 声道。验证一个非 L/R 的声道（索引 4）也按设计频率作用 EQ，无八度偏移。
            // 修复前该声道完全不过 EQ（峰值=0dB 平线），修复后应与 L/R 一致落在 1000Hz。
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 6);
            double peakCh4 = ProbePeakHz(GainAt1000(12.0), fmt, 4);
            Assert.InRange(peakCh4, 850, 1150);

            double g1000 = MeasureGainDb(GainAt1000(12.0), fmt, 1000, 4);
            double g2000 = MeasureGainDb(GainAt1000(12.0), fmt, 2000, 4);
            Assert.True(g1000 - g2000 >= 3,
                $"多声道 ch4: 1000Hz 应高于 2000Hz（实际差 {g1000 - g2000:0.0}dB）");
        }

        [Fact]
        public void Passthrough_NoDsp_ZeroGainDb()
        {
            // 不启用任何 DSP 时，输出应数值直通（与输入 RMS 一致，增益 0dB）。
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            double g = MeasureGainDb(Array.Empty<double>(), fmt, 1000, 0);
            Assert.True(Math.Abs(g) < 0.01, $"无 DSP 时应 0dB 直通，实际 {g:0.00}dB");
        }

        private static double ProbePeakHz(double[] gains, WaveFormat fmt, int channel)
        {
            double best = double.NegativeInfinity;
            double bestF = 0;
            for (int f = 200; f <= 4000; f += 25)
            {
                double g = MeasureGainDb(gains, fmt, f, channel);
                if (g > best)
                {
                    best = g;
                    bestF = f;
                }
            }

            return bestF;
        }

        private static double MeasureGainDb(double[] gains, WaveFormat fmt, double freq, int channel)
        {
            var src = new SineSource(fmt, BuildSine(fmt, freq));
            var dsp = new ManagedDspSourceProvider(src);
            if (gains != null && gains.Length > 0) dsp.UpdateEq(gains);

            // 预热 0.3s 进入稳态
            byte[] buf = new byte[fmt.BlockAlign * 4410];
            for (int i = 0; i < 3; i++)
            {
                dsp.Read(buf, 0, buf.Length);
            }

            dsp.Read(buf, 0, buf.Length);
            double rmsOut = RmsOf(buf, fmt, channel);
            double rmsIn = 0.05 / Math.Sqrt(2.0); // 输入正弦幅度 0.05 的 RMS
            return 20.0 * Math.Log10(rmsOut / rmsIn);
        }

        private static float[] BuildSine(WaveFormat fmt, double freq)
        {
            int frames = Fs; // 1s
            float[] s = new float[frames * fmt.Channels];
            const double amp = 0.05;
            for (int i = 0; i < frames; i++)
            {
                double v = amp * Math.Sin(2.0 * Math.PI * freq * i / fmt.SampleRate);
                for (int c = 0; c < fmt.Channels; c++)
                {
                    s[i * fmt.Channels + c] = (float)v; // 所有声道同信号
                }
            }

            return s;
        }

        private static double RmsOf(byte[] buf, WaveFormat fmt, int channel)
        {
            double sum = 0;
            int frames = buf.Length / fmt.BlockAlign;
            int bytesPerSample = fmt.BitsPerSample / 8;
            for (int i = 0; i < frames; i++)
            {
                int off = i * fmt.BlockAlign + channel * bytesPerSample;
                float v = BitConverter.ToSingle(buf, off);
                sum += v * v;
            }

            if (frames == 0) return 0;
            return Math.Sqrt(sum / frames);
        }
    }

    /// <summary>实时电平表回归测试：验证测量出的峰值/RMS 与实际信号一致、
    /// 测量的是 DSP 之后的信号，且开启测量不会破坏 bit-perfect 直通。</summary>
    public class LevelMeterTests
    {
        private const int Fs = 44100;
        private const double Amp = 0.5; // 已知幅度：峰值 0.5、RMS = 0.5/√2 ≈ 0.3536

        private static float[] BuildSine(WaveFormat fmt, double freq)
        {
            int frames = Fs;
            float[] s = new float[frames * fmt.Channels];
            for (int i = 0; i < frames; i++)
            {
                double v = Amp * Math.Sin(2.0 * Math.PI * freq * i / Fs);
                for (int c = 0; c < fmt.Channels; c++)
                {
                    s[i * fmt.Channels + c] = (float)v;
                }
            }

            return s;
        }

        private static (float Peak, float Rms) ReadMeter(WaveFormat fmt, bool metering)
        {
            var src = new SineSource(fmt, BuildSine(fmt, 1000));
            var dsp = new ManagedDspSourceProvider(src);
            dsp.SetMetering(metering);
            byte[] buf = new byte[fmt.BlockAlign * 4410];
            for (int i = 0; i < 4; i++) dsp.Read(buf, 0, buf.Length);

            float[] peak = new float[fmt.Channels];
            float[] rms = new float[fmt.Channels];
            dsp.LevelMeter.CopyTo(peak, rms);
            return (peak[0], rms[0]);
        }

        [Fact]
        public void LevelMeter_MeasuresKnownSinePeakAndRms()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            var (peak, rms) = ReadMeter(fmt, true);

            // 幅度 0.5 的正弦 → 峰值 0.5，RMS 0.3536
            Assert.Equal(0.50, peak, 2);
            Assert.Equal(0.3536, rms, 3);
        }

        [Fact]
        public void LevelMeter_HasOneBarPerChannel()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 6); // 5.1
            var src = new SineSource(fmt, BuildSine(fmt, 1000));
            var dsp = new ManagedDspSourceProvider(src);
            dsp.SetMetering(true);
            byte[] buf = new byte[fmt.BlockAlign * 4410];
            dsp.Read(buf, 0, buf.Length);

            // 每个声道都要有独立读数（声道数 = 电平条数量）
            Assert.Equal(6, dsp.LevelMeter.Channels);
            float[] peak = new float[6];
            float[] rms = new float[6];
            dsp.LevelMeter.CopyTo(peak, rms);
            for (int c = 0; c < 6; c++)
            {
                Assert.Equal(0.50, peak[c], 2);
            }
        }

        [Fact]
        public void LevelMeter_WhenDisabled_ReportsNothing()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            var src = new SineSource(fmt, BuildSine(fmt, 1000));
            var dsp = new ManagedDspSourceProvider(src);
            dsp.SetMetering(false);
            byte[] buf = new byte[fmt.BlockAlign * 4410];
            dsp.Read(buf, 0, buf.Length);

            Assert.Equal(0, dsp.LevelMeter.Channels); // 未开启 → 无声道、零开销
        }

        [Fact]
        public void LevelMeter_MeasuresAfterDsp_VolumeGainApplied()
        {
            // 电平表应显示「送去输出」的信号：加 0.5 倍音量后，读数应同步减半。
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            var src = new SineSource(fmt, BuildSine(fmt, 1000));
            var dsp = new ManagedDspSourceProvider(src);
            dsp.SetMetering(true);
            dsp.SetVolumeGain(0.5f);
            byte[] buf = new byte[fmt.BlockAlign * 4410];
            for (int i = 0; i < 4; i++) dsp.Read(buf, 0, buf.Length);

            float[] peak = new float[2];
            float[] rms = new float[2];
            dsp.LevelMeter.CopyTo(peak, rms);

            Assert.Equal(0.25, peak[0], 2);  // 0.5 × 0.5
            Assert.Equal(0.1768, rms[0], 3); // 0.3536 × 0.5
        }

        [Fact]
        public void LevelMeter_DoesNotAlterBitPerfectOutput()
        {
            // 无 DSP（bit-perfect 直通）时开启电平测量，输出字节必须与不开测量时逐字节一致。
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            byte[] withMeter = ReadRaw(fmt, true);
            byte[] withoutMeter = ReadRaw(fmt, false);

            Assert.Equal(withoutMeter, withMeter);
            Assert.True(Array.Exists(withMeter, b => b != 0), "测试数据不应为全静音，否则断言无意义");
        }

        private static byte[] ReadRaw(WaveFormat fmt, bool metering)
        {
            var src = new SineSource(fmt, BuildSine(fmt, 1000));
            var dsp = new ManagedDspSourceProvider(src);
            dsp.SetMetering(metering);
            byte[] buf = new byte[fmt.BlockAlign * 4410];
            dsp.Read(buf, 0, buf.Length);
            return buf;
        }
    }

    /// <summary>
    /// 交叉淡化回归测试。用「常量电平」的假音轨（A=+0.8、B=-0.4）驱动 SeamlessWaveProvider，
    /// 这样淡化曲线上的每个点都有解析解，可以精确断言，不受波形相位影响。
    /// </summary>
    public class CrossfadeTests
    {
        private const int Fs = 44100;
        private const float LevelA = 0.8f;
        private const float LevelB = -0.4f;
        private const int Frames = 88200; // 每首 2 秒

        private static WaveFileReader MakeConstantReader(float level, int frames)
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            var ms = new MemoryStream();
            var writer = new WaveFileWriter(ms, fmt);
            for (int i = 0; i < frames; i++)
            {
                writer.WriteSample(level);
                writer.WriteSample(level);
            }

            writer.Flush();
            writer.Dispose();
            return new WaveFileReader(new MemoryStream(ms.ToArray(), writable: false));
        }

        /// <summary>连续读到底，返回每帧第 0 声道的采样值序列。</summary>
        private static float[] ReadAllValues(SeamlessWaveProvider sp, WaveFormat fmt, int maxFrames)
        {
            int block = fmt.BlockAlign;
            var sink = new MemoryStream();
            byte[] buf = new byte[block * 4096];
            int framesRead = 0;
            while (framesRead < maxFrames)
            {
                int want = Math.Min(buf.Length, (maxFrames - framesRead) * block);
                int n = sp.Read(buf, 0, want);
                if (n <= 0) break;
                sink.Write(buf, 0, n);
                framesRead += n / block;
            }

            byte[] all = sink.ToArray();
            int total = all.Length / block;
            float[] vals = new float[total];
            for (int i = 0; i < total; i++)
            {
                vals[i] = BitConverter.ToSingle(all, i * block);
            }

            return vals;
        }

        [Fact]
        public void Crossfade_Disabled_IsPlainGaplessHardCut()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            var a = MakeConstantReader(LevelA, Frames);
            var b = MakeConstantReader(LevelB, Frames);
            var sp = new SeamlessWaveProvider(a);
            sp.SetCrossfade(0);
            sp.PrepareNext(b);

            float[] v = ReadAllValues(sp, fmt, Frames * 2 + 100);

            // 关闭淡化：长度严格等于 A + B，切换点上没有任何混合
            Assert.Equal(Frames * 2, v.Length);
            Assert.Equal(LevelA, v[Frames - 1], 4);
            Assert.Equal(LevelB, v[Frames], 4);
        }

        [Fact]
        public void Crossfade_Enabled_OverlapsSoTotalIsShorter()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            var a = MakeConstantReader(LevelA, Frames);
            var b = MakeConstantReader(LevelB, Frames);
            var sp = new SeamlessWaveProvider(a);
            sp.SetCrossfade(1000); // 1 秒 = 44100 帧
            sp.PrepareNext(b);

            float[] v = ReadAllValues(sp, fmt, Frames * 2 + 100);

            // 两首重叠了 1 秒 → 总长度 = A + B − 淡化长度
            Assert.Equal(Frames * 2 - Fs, v.Length);
        }

        [Fact]
        public void Crossfade_MixesWithEqualPowerCurve()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            var a = MakeConstantReader(LevelA, Frames);
            var b = MakeConstantReader(LevelB, Frames);
            var sp = new SeamlessWaveProvider(a);
            sp.SetCrossfade(1000);
            sp.PrepareNext(b);

            float[] v = ReadAllValues(sp, fmt, Frames * 2 + 100);

            int fadeStart = Frames - Fs; // 44100：A 播到只剩 1 秒时开始淡化
            int fadeMid = fadeStart + Fs / 2;
            int fadeEnd = fadeStart + Fs;

            // 淡化窗口之前：纯 A，不受影响
            Assert.Equal(LevelA, v[fadeStart - 1], 4);
            // 淡化起点：gCur=cos0=1、gNext=sin0=0 → 纯 A
            Assert.Equal(LevelA, v[fadeStart], 4);
            // 淡化中点：等功率 cos45° / sin45°，两首各占 0.7071
            double expectedMid = LevelA * Math.Cos(Math.PI / 4) + LevelB * Math.Sin(Math.PI / 4);
            Assert.Equal(expectedMid, v[fadeMid], 3);
            // 淡化临终点：几乎全是 B
            Assert.Equal(LevelB, v[fadeEnd - 1], 2);
            // 淡化之后：纯 B
            Assert.Equal(LevelB, v[fadeEnd], 3);
            Assert.Equal(LevelB, v[v.Length - 1], 3);
        }

        [Fact]
        public void Crossfade_WithoutNextTrack_PlaysNormally()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            var a = MakeConstantReader(LevelA, Frames);
            var sp = new SeamlessWaveProvider(a);
            sp.SetCrossfade(1000); // 开了淡化但没有下一首

            float[] v = ReadAllValues(sp, fmt, Frames + 100);

            // 没有下一首可淡化 → 正常播完 A，长度与内容都不受影响
            Assert.Equal(Frames, v.Length);
            Assert.Equal(LevelA, v[0], 4);
            Assert.Equal(LevelA, v[Frames - 1], 4);
        }
    }

    /// <summary>Equalizer APO 配置（config.txt）导入 / 导出的回归测试。</summary>
    public class ApoConverterTests
    {
        [Fact]
        public void Apo_Import_ParsesPreampAndAllFilterTypes()
        {
            string text = string.Join("\n",
                "# 注释行应当被忽略",
                "Preamp: -6.5 dB",
                "Filter: ON PK Fc 1000 Hz Gain 3 dB Q 1",
                "Filter: ON LS Fc 100 Hz Gain -2.5 dB Q 0.7",
                "Filter: OFF HS Fc 8000 Hz Gain 1.5 dB Q 0.707",
                "Filter: ON LP Fc 20000 Hz Q 0.5",
                "Filter: ON NO Fc 60 Hz Q 4");

            Assert.True(EqualizerApoConverter.TryImport(text, out EqCurveState? c, out int imported, out int skipped, out string error), error);
            Assert.NotNull(c);
            Assert.Equal(5, imported);
            Assert.Equal(0, skipped);
            Assert.Equal(-6.5, c!.PreampDb, 3);

            Assert.Equal(EqFilterType.Peaking, c.Bands[0].FilterType);
            Assert.Equal(1000, c.Bands[0].FrequencyHz, 3);
            Assert.Equal(3, c.Bands[0].GainDb, 3);
            Assert.Equal(1, c.Bands[0].Q, 3);
            Assert.True(c.Bands[0].Enabled);

            Assert.Equal(EqFilterType.LowShelf, c.Bands[1].FilterType);
            Assert.Equal(-2.5, c.Bands[1].GainDb, 3);

            Assert.Equal(EqFilterType.HighShelf, c.Bands[2].FilterType);
            Assert.False(c.Bands[2].Enabled); // OFF

            Assert.Equal(EqFilterType.LowPass, c.Bands[3].FilterType);
            Assert.Equal(0, c.Bands[3].GainDb, 3); // APO 的低通不带 Gain

            Assert.Equal(EqFilterType.Notch, c.Bands[4].FilterType);
            Assert.Equal(4, c.Bands[4].Q, 3);
        }

        [Fact]
        public void Apo_RoundTrip_PreservesEveryBand()
        {
            var original = new EqCurveState
            {
                Enabled = true,
                PreampDb = -3.0,
                PresetId = "custom",
                Bands =
                {
                    new EqBand { Enabled = true, FilterType = EqFilterType.Peaking, FrequencyHz = 1000, GainDb = 3, Q = 1 },
                    new EqBand { Enabled = true, FilterType = EqFilterType.LowShelf, FrequencyHz = 100, GainDb = -2.5, Q = 0.7 },
                    new EqBand { Enabled = true, FilterType = EqFilterType.HighShelf, FrequencyHz = 8000, GainDb = 1.5, Q = 0.707 },
                    new EqBand { Enabled = true, FilterType = EqFilterType.LowPass, FrequencyHz = 20000, GainDb = 0, Q = 0.5 },
                    new EqBand { Enabled = true, FilterType = EqFilterType.HighPass, FrequencyHz = 30, GainDb = 0, Q = 0.5 },
                    new EqBand { Enabled = true, FilterType = EqFilterType.Notch, FrequencyHz = 60, GainDb = 0, Q = 4 },
                }
            };

            string text = EqualizerApoConverter.Export(original);
            Assert.True(EqualizerApoConverter.TryImport(text, out EqCurveState? back, out int imported, out int skipped, out string error), error);
            Assert.NotNull(back);
            Assert.Equal(0, skipped);
            Assert.Equal(original.Bands.Count, imported);
            Assert.Equal(original.PreampDb, back!.PreampDb, 3);

            for (int i = 0; i < original.Bands.Count; i++)
            {
                Assert.Equal(original.Bands[i].FilterType, back.Bands[i].FilterType);
                Assert.Equal(original.Bands[i].FrequencyHz, back.Bands[i].FrequencyHz, 1);
                Assert.Equal(original.Bands[i].GainDb, back.Bands[i].GainDb, 2);
                Assert.Equal(original.Bands[i].Q, back.Bands[i].Q, 3);
                Assert.True(back.Bands[i].Enabled);
            }
        }

        [Fact]
        public void Apo_Import_SkipsUnsupportedFilterTypes()
        {
            // AP（全通）本程序没有对应实现，应被跳过而不是崩溃或误当成峰值
            string text = string.Join("\n",
                "Filter: ON PK Fc 500 Hz Gain 2 dB Q 1",
                "Filter: ON AP Fc 1000 Hz Q 1");

            Assert.True(EqualizerApoConverter.TryImport(text, out EqCurveState? c, out int imported, out int skipped, out string error), error);
            Assert.Equal(1, imported);
            Assert.Equal(1, skipped);
            Assert.Single(c!.Bands);
            Assert.Equal(EqFilterType.Peaking, c.Bands[0].FilterType);
        }

        [Fact]
        public void Apo_Import_NoFilterLines_Fails()
        {
            Assert.False(EqualizerApoConverter.TryImport(string.Empty, out _, out _, out _, out string errEmpty));
            Assert.False(string.IsNullOrWhiteSpace(errEmpty));

            // 只有 Preamp、没有任何滤波段的配置也不算有效曲线
            Assert.False(EqualizerApoConverter.TryImport("Preamp: -3 dB", out _, out _, out _, out string errNoFilter));
            Assert.False(string.IsNullOrWhiteSpace(errNoFilter));
        }

        [Fact]
        public void Apo_Export_OmitsGainForLowPassHighPassNotch()
        {
            var curve = new EqCurveState
            {
                Enabled = true,
                PreampDb = -1.5,
                Bands =
                {
                    // APO 的低通 / 高通 / 切除没有 Gain 参数，即使这里设了也不能写出去
                    new EqBand { Enabled = true, FilterType = EqFilterType.LowPass, FrequencyHz = 20000, GainDb = 5, Q = 0.5 },
                    new EqBand { Enabled = true, FilterType = EqFilterType.Peaking, FrequencyHz = 1000, GainDb = 3, Q = 1 },
                }
            };

            string text = EqualizerApoConverter.Export(curve);
            Assert.Contains("Preamp: -1.5 dB", text);

            string lpLine = string.Empty;
            string pkLine = string.Empty;
            foreach (string raw in text.Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("Filter:", StringComparison.Ordinal) && l.Contains(" LP ", StringComparison.Ordinal)) lpLine = l;
                if (l.StartsWith("Filter:", StringComparison.Ordinal) && l.Contains(" PK ", StringComparison.Ordinal)) pkLine = l;
            }

            Assert.False(string.IsNullOrEmpty(lpLine), "应导出低通行");
            Assert.False(string.IsNullOrEmpty(pkLine), "应导出峰值行");
            Assert.DoesNotContain("Gain", lpLine);
            Assert.Contains("Q 0.5", lpLine);
            Assert.Contains("Gain 3 dB", pkLine);
        }
    }

    /// <summary>有限长度正弦源（读尽返回 0），用于 SRC 时长/频率测试。
    /// 同时实现 IWaveProvider（SRC 构造要求）与 IWaveSourceProvider（拖动/时长转发）。</summary>
    internal sealed class FiniteSineSource : IWaveSourceProvider, IWaveProvider
    {
        private readonly WaveFormat _fmt;
        private readonly float[] _samples;
        private int _pos;

        public FiniteSineSource(WaveFormat fmt, float[] samples)
        {
            _fmt = fmt;
            _samples = samples;
        }

        public WaveFormat WaveFormat => _fmt;
        public TimeSpan TotalTime => TimeSpan.FromSeconds((double)_samples.Length / (_fmt.SampleRate * _fmt.Channels));
        public (long, long, bool)? ProbeCurrentState => null;
        public bool NextMounted => false;

        public void Seek(TimeSpan position)
        {
            int frame = Math.Clamp((int)(position.TotalSeconds * _fmt.SampleRate), 0, _samples.Length / _fmt.Channels);
            _pos = frame * _fmt.Channels;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int n = count / 4;
            int i = 0;
            for (; i < n && _pos < _samples.Length; i++, _pos++)
            {
                BitConverter.GetBytes(_samples[_pos]).CopyTo(buffer, offset + i * 4);
            }

            return i * 4;
        }
    }

    public class SrcTests
    {
        private static float[] BuildTone(int sampleRate, double hz, int frames, double amp = 0.5)
        {
            var s = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                s[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / sampleRate));
            }

            return s;
        }

        private static float From24(byte[] b, int offset)
        {
            int v = b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16);
            if ((b[offset + 2] & 0x80) != 0) v |= unchecked((int)0xFF000000);
            return v / 8388608f;
        }

        [Fact]
        public void Src_UpsamplesToTargetRate()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var res = new ResamplingSourceProvider(new FiniteSineSource(fmt, BuildTone(44100, 1000, 44100)), 96000);
            Assert.Equal(96000, res.WaveFormat.SampleRate);
            Assert.Equal(24, res.WaveFormat.BitsPerSample);
            Assert.Equal(1, res.WaveFormat.Channels);
        }

        [Fact]
        public void Src_KeepsDuration()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var src = new FiniteSineSource(fmt, BuildTone(44100, 1000, 44100)); // 1 秒 @44.1k
            var res = new ResamplingSourceProvider(src, 96000);
            // 期望 96000 帧（±500：WDL 滤波器起止 transient/tail）
            long frames = 0;
            var buf = new byte[96000 * 3];
            int read;
            while ((read = res.Read(buf, 0, buf.Length)) > 0)
            {
                frames += read / 3;
            }

            Assert.InRange(frames, 95000, 97000);
        }

        [Fact]
        public void Src_KeepsFrequency()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            var src = new FiniteSineSource(fmt, BuildTone(44100, 1000, 44100 * 2)); // 2 秒 @44.1k
            var res = new ResamplingSourceProvider(src, 96000);
            // 跳过前 0.5 秒（滤波群延迟/瞬态），随后读 1 秒输出：
            // 1kHz 正弦 → 每秒应约 2000 次过零（每周期 2 次）
            int skip = (int)(0.5 * 96000);
            int got = 0;
            var buf = new byte[96000 * 3];
            while (got < skip)
            {
                int read = res.Read(buf, 0, buf.Length);
                if (read <= 0) break;
                got += read / 3;
            }

            int zeros = 0;
            float prev = float.NaN;
            for (int i = 0; i < 96000 * 3; i += 3)
            {
                float v = From24(buf, i);
                if (float.IsNaN(prev))
                {
                    prev = v;
                    continue;
                }

                if (prev != 0 && v != 0 && (prev < 0) != (v < 0)) zeros++;
                prev = v;
            }

            Assert.InRange(zeros, 1900, 2100);
        }

        [Fact]
        public void Src_QualityModes_AllProduceCorrectOutput()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);

            foreach (var quality in new[] { ResamplingSourceProvider.QualityLowLatency, ResamplingSourceProvider.QualityBalanced, ResamplingSourceProvider.QualityTransparent })
            {
                var src = new FiniteSineSource(fmt, BuildTone(44100, 1000, 44100)); // 1 秒（每档独立源）
                var res = new ResamplingSourceProvider(src, 96000, quality, ResamplingSourceProvider.DitherOff);
                Assert.Equal(96000, res.WaveFormat.SampleRate);
                Assert.Equal(24, res.WaveFormat.BitsPerSample);
                long frames = 0;
                var buf = new byte[96000 * 3];
                int read;
                while ((read = res.Read(buf, 0, buf.Length)) > 0)
                {
                    frames += read / 3;
                }

                Assert.InRange(frames, 95000, 97000);
            }
        }

        [Fact]
        public void Src_DitherModes_ProduceDifferentOutput()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
            byte[] ReadAll(string dither)
            {
                var src = new FiniteSineSource(fmt, BuildTone(44100, 1000, 44100));
                var res = new ResamplingSourceProvider(src, 96000, ResamplingSourceProvider.QualityBalanced, dither);
                var buf = new byte[96000 * 3];
                int got = 0;
                while (got < buf.Length)
                {
                    int n = res.Read(buf, got, buf.Length - got);
                    if (n <= 0) break;
                    got += n;
                }

                return buf;
            }

            byte[] off = ReadAll(ResamplingSourceProvider.DitherOff);
            byte[] tpdf = ReadAll(ResamplingSourceProvider.DitherTpdf);
            byte[] ns5 = ReadAll(ResamplingSourceProvider.DitherNs5);
            // 三种模式输出应互不相同（dither 确实注入了噪声/整形）
            Assert.False(SequenceEqual(off, tpdf));
            Assert.False(SequenceEqual(off, ns5));
            Assert.False(SequenceEqual(tpdf, ns5));
        }

        private static bool SequenceEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }
    }

    public class DspBypassAndDelayTests
    {
        private const int Fs = 44100;

        [Fact]
        public void BypassAll_MakesOutputBitPerfect_EvenWithEqActive()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 1);
            float[] tone = BuildTone(Fs, 1000, Fs);

            // 参考：不经任何 DSP 的原始直通字节
            var srcRef = new FiniteSineSource(fmt, tone);
            byte[] passthrough = new byte[Fs * 4];
            srcRef.Read(passthrough, 0, passthrough.Length);

            // EQ 激活：输出应明显不同于原始
            var curve = new EqCurveState { Enabled = true, PresetId = "t", PresetName = "t" };
            curve.Bands.Add(new EqBand { Enabled = true, FrequencyHz = 1000, GainDb = 12.0, Q = 1.0, FilterType = EqFilterType.Peaking });
            var dspEq = new ManagedDspSourceProvider(new FiniteSineSource(fmt, tone));
            dspEq.SetMetering(false);
            dspEq.UpdateEqCurve(curve);
            byte[] eqBuf = new byte[Fs * 4];
            dspEq.Read(eqBuf, 0, eqBuf.Length);
            Assert.False(SequenceEqual(passthrough, eqBuf), "EQ 生效时输出应被改变");

            // 总旁路：输出必须与原始逐字节一致（bit-perfect），且设置保留
            var dspBypass = new ManagedDspSourceProvider(new FiniteSineSource(fmt, tone));
            dspBypass.SetMetering(false);
            dspBypass.UpdateEqCurve(curve);
            dspBypass.SetBypassAll(true);
            byte[] bypassBuf = new byte[Fs * 4];
            dspBypass.Read(bypassBuf, 0, bypassBuf.Length);
            Assert.True(SequenceEqual(passthrough, bypassBuf), "总旁路后输出必须 bit-perfect");
            Assert.True(dspBypass.IsBypassAll);
        }

        [Fact]
        public void ChannelDelay_DefersPulseByDelayMs()
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(Fs, 2);
            // 1 秒立体声：前 0.5 秒全 0（预热延迟渐变），0.5s 处左声道放一个 0.8 脉冲
            float[] s = new float[Fs * 2];
            int pulseFrame = Fs / 2; // 22050
            s[pulseFrame * 2] = 0.8f;
            var src = new FiniteSineSource(fmt, s);

            var dsp = new ManagedDspSourceProvider(src);
            dsp.SetMetering(false);
            dsp.UpdateChannel(new ChannelBalanceState
            {
                Enabled = true,
                LeftDelayMs = 5.0, // 5ms @44.1k = 220.5 样本
                RightDelayMs = 0.0
            });

            byte[] buf = new byte[Fs * fmt.BlockAlign];
            int got = 0;
            while (got < buf.Length)
            {
                int n = dsp.Read(buf, got, buf.Length - got);
                if (n <= 0) break;
                got += n;
            }

            // 扫描左声道最大帧位置与幅度；右声道应全 0
            int maxFrame = -1;
            float maxVal = 0f;
            float maxRight = 0f;
            for (int f = 0; f < Fs; f++)
            {
                float l = BitConverter.ToSingle(buf, f * 8);
                float r = BitConverter.ToSingle(buf, f * 8 + 4);
                if (Math.Abs(l) > maxVal) { maxVal = Math.Abs(l); maxFrame = f; }
                if (Math.Abs(r) > maxRight) maxRight = Math.Abs(r);
            }

            // 脉冲应被延迟约 220.5 帧（线性插值把 0.8 摊到相邻两帧，各约 0.4）
            Assert.InRange(maxFrame, pulseFrame + 218, pulseFrame + 224);
            Assert.InRange(maxVal, 0.30, 0.50);
            Assert.True(maxRight < 1e-6, "右声道不应有信号");
        }

        private static float[] BuildTone(int sampleRate, double hz, int frames, double amp = 0.5)
        {
            var t = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                t[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / sampleRate));
            }

            return t;
        }

        private static bool SequenceEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }
    }

}
