using System;
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
}
