using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 卷积 FIR（房间校正 / 脉冲响应）处理：
    ///  - <see cref="ConvolutionIr.Load"/>：从 WAV 读取脉冲响应（16/24/32-bit 或 float PCM），
    ///    必要时线性插值重采样到目标采样率，输出每声道 float[]。
    ///  - <see cref="StreamingPartitionedConvolver"/>：均匀分区 FFT 卷积（UPC），流式实时处理
    ///    interleaved float 块。每声道独立卷积状态；单声道 IR 复制到各声道。
    /// 与参数 EQ 不同：卷积按 IR 做频域相乘，能还原真实房间/耳机脉冲响应。
    /// FFT 使用内置的 <see cref="FftProcessor"/>（radix-2），不依赖 NAudio 已移除的 DSP FFT 类型。
    /// </summary>
    internal static class ConvolutionIr
    {
        /// <summary>从 WAV 读取脉冲响应并（必要时）重采样到 targetSampleRate。</summary>
        /// <returns>每声道 float[]；null 表示读取/解析失败。</returns>
        public static float[][]? Load(string irPath, int targetSampleRate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(irPath) || !File.Exists(irPath))
                {
                    return null;
                }

                using var reader = new WaveFileReader(irPath);
                if (reader.WaveFormat == null || reader.WaveFormat.Channels <= 0)
                {
                    return null;
                }

                int irChannels = reader.WaveFormat.Channels;
                int irRate = reader.WaveFormat.SampleRate;
                var data = new List<float>[irChannels];
                for (int c = 0; c < irChannels; c++)
                {
                    data[c] = new List<float>(4096);
                }

                int bytesPerSample = Math.Max(1, reader.WaveFormat.BitsPerSample / 8);
                if (reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample < 4)
                {
                    bytesPerSample = 4;
                }

                byte[] block = new byte[Math.Max(1, reader.WaveFormat.BlockAlign)];
                float[] sample = new float[irChannels];
                while (reader.Read(block, 0, block.Length) == block.Length)
                {
                    for (int c = 0; c < irChannels; c++)
                    {
                        int idx = c * bytesPerSample;
                        if (idx + bytesPerSample > block.Length)
                        {
                            sample[c] = 0f;
                            continue;
                        }

                        sample[c] = BytesToFloat(block, idx, bytesPerSample, reader.WaveFormat.Encoding);
                    }

                    for (int c = 0; c < irChannels; c++)
                    {
                        data[c].Add(sample[c]);
                    }
                }

                if (data[0].Count == 0)
                {
                    return null;
                }

                var result = new float[irChannels][];
                for (int c = 0; c < irChannels; c++)
                {
                    float[] raw = data[c].ToArray();
                    result[c] = irRate == targetSampleRate || irRate <= 0 || targetSampleRate <= 0
                        ? raw
                        : ResampleLinear(raw, irRate, targetSampleRate);
                }

                return result;
            }
            catch
            {
                return null;
            }
        }

        private static float BytesToFloat(byte[] b, int offset, int bytesPerSample, WaveFormatEncoding encoding)
        {
            if (encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample >= 4)
            {
                return BitConverter.ToSingle(b, offset);
            }

            switch (bytesPerSample)
            {
                case 1:
                    return (b[offset] - 128) / 128f;
                case 2:
                    return BitConverter.ToInt16(b, offset) / 32768f;
                case 3:
                {
                    int v = b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16);
                    if ((v & 0x800000) != 0)
                    {
                        v |= unchecked((int)0xFF000000);
                    }

                    return v / 8388608f;
                }
                case 4:
                    return BitConverter.ToInt32(b, offset) / 2147483648f;
                default:
                    return 0f;
            }
        }

        private static float[] ResampleLinear(float[] src, int srcRate, int dstRate)
        {
            int n = (int)Math.Ceiling(src.Length * (double)dstRate / srcRate);
            var dst = new float[n];
            double ratio = (double)srcRate / dstRate;
            for (int i = 0; i < n; i++)
            {
                double pos = i * ratio;
                int i0 = (int)pos;
                int i1 = Math.Min(i0 + 1, src.Length - 1);
                double frac = pos - i0;
                dst[i] = (float)(src[i0] * (1.0 - frac) + src[i1] * frac);
            }

            return dst;
        }
    }

    /// <summary>
    /// 流式均匀分区卷积（Uniform Partitioned Convolution）。
    /// 处理 interleaved float 块（in-place）。引入延迟 = BlockSize 帧（1024 帧 ≈ 23ms@44.1k / 5.3ms@192k）。
    /// 每声道独立：inTail / outTail / 当前块频谱；IR 取 ir[Math.Min(ch, irChannels-1)]。
    /// </summary>
    internal sealed class StreamingPartitionedConvolver
    {
        private const int BlockSize = 1024;
        private const int FftSize = BlockSize * 2;
        private const int FftLog = 11; // log2(2048)

        private readonly CF[][][] _irSpectra; // [channel][partition][bin]
        private readonly int _irChannels;
        private readonly int _partitionCount;
        private readonly float[][] _inTail;   // [ch][BlockSize]
        private readonly float[][] _outTail;  // [ch][FftSize]（前 BlockSize 为待输出）
        private readonly int[] _inPos;        // [ch] 当前输入尾位置

        public int LatencyFrames => BlockSize;

        public StreamingPartitionedConvolver(float[][] ir, int channels)
        {
            _irChannels = ir?.Length ?? 0;
            if (_irChannels == 0)
            {
                ir = new[] { new float[] { 1f } }; // 单位冲激：直通
                _irChannels = 1;
            }

            _partitionCount = Math.Max(1, (ir[0].Length + BlockSize - 1) / BlockSize);

            // 预计算每声道每分区 FFT 频谱
            _irSpectra = new CF[_irChannels][][];
            for (int c = 0; c < _irChannels; c++)
            {
                float[] taps = ir[Math.Min(c, ir.Length - 1)];
                _irSpectra[c] = new CF[_partitionCount][];
                var block = new CF[FftSize];
                for (int p = 0; p < _partitionCount; p++)
                {
                    Array.Clear(block, 0, FftSize);
                    int start = p * BlockSize;
                    int len = Math.Min(BlockSize, taps.Length - start);
                    for (int i = 0; i < len; i++)
                    {
                        block[i] = new CF(taps[start + i], 0f);
                    }

                    FftProcessor.Transform(block, false);
                    var copy = new CF[FftSize];
                    Array.Copy(block, copy, FftSize);
                    _irSpectra[c][p] = copy;
                }
            }

            _inTail = new float[_irChannels][];
            _outTail = new float[_irChannels][];
            _inPos = new int[_irChannels];
            for (int c = 0; c < _irChannels; c++)
            {
                _inTail[c] = new float[BlockSize];
                _outTail[c] = new float[FftSize];
                _inPos[c] = 0;
            }
        }

        /// <summary>处理 interleaved float 块（in-place）。frames 为帧数（每帧 channels 个采样）。</summary>
        public void Process(float[] buffer, int frames, int channels)
        {
            if (buffer == null || frames <= 0 || channels <= 0)
            {
                return;
            }

            int done = 0;
            while (done < frames)
            {
                int take = frames - done;
                int chunk = Math.Min(BlockSize, take);
                for (int c = 0; c < _irChannels; c++)
                {
                    int pos = _inPos[c];
                    int srcCh = Math.Min(c, channels - 1);
                    for (int i = 0; i < chunk; i++)
                    {
                        _inTail[c][pos + i] = buffer[(done + i) * channels + srcCh];
                    }
                }

                done += chunk;

                if (_inPos[0] + chunk >= BlockSize)
                {
                    for (int c = 0; c < _irChannels; c++)
                    {
                        ConvolveOneBlock(c);
                    }

                    int outStart = done - BlockSize;
                    WriteOutput(buffer, outStart, BlockSize, channels);
                    for (int c = 0; c < _irChannels; c++)
                    {
                        _inPos[c] = 0;
                    }
                }
                else
                {
                    for (int c = 0; c < _irChannels; c++)
                    {
                        _inPos[c] += chunk;
                    }
                }
            }
        }

        /// <summary>源结束时冲刷尾部残响（可选；最多补出 BlockSize 帧）。</summary>
        public void Flush(float[] buffer, int frames, int channels)
        {
            if (_inPos[0] <= 0)
            {
                return;
            }

            for (int c = 0; c < _irChannels; c++)
            {
                ConvolveOneBlock(c);
            }

            int n = Math.Min(frames, BlockSize);
            WriteOutput(buffer, 0, n, channels);
            for (int c = 0; c < _irChannels; c++)
            {
                _inPos[c] = 0;
            }
        }

        private void ConvolveOneBlock(int ch)
        {
            var x = new CF[FftSize];
            for (int i = 0; i < BlockSize; i++)
            {
                x[i] = new CF(_inTail[ch][i], 0f);
            }

            FftProcessor.Transform(x, false);

            var y = new CF[FftSize];
            CF[][] spectra = _irSpectra[ch];
            for (int p = 0; p < _partitionCount; p++)
            {
                CF[] h = spectra[p];
                for (int k = 0; k < FftSize; k++)
                {
                    CF xk = x[k];
                    CF hk = h[k];
                    y[k].Re += xk.Re * hk.Re - xk.Im * hk.Im;
                    y[k].Im += xk.Re * hk.Im + xk.Im * hk.Re;
                }
            }

            FftProcessor.Transform(y, true);

            double scale = 1.0 / FftSize;
            float[] tail = _outTail[ch];
            for (int i = 0; i < FftSize; i++)
            {
                tail[i] += (float)(y[i].Re * scale);
            }

            // 输出前 BlockSize 帧并左移
            for (int i = 0; i < FftSize - BlockSize; i++)
            {
                tail[i] = tail[i + BlockSize];
            }

            for (int i = FftSize - BlockSize; i < FftSize; i++)
            {
                tail[i] = 0f;
            }
        }

        private void WriteOutput(float[] buffer, int baseFrame, int frames, int channels)
        {
            for (int i = 0; i < frames; i++)
            {
                int dst = (baseFrame + i) * channels;
                for (int c = 0; c < _irChannels && c < channels; c++)
                {
                    buffer[dst + c] = _outTail[c][i];
                }
            }
        }
    }

    /// <summary>房间校正 IR 的进程内缓存（按路径 + 采样率），避免每次启用/换歌重复读盘与 FFT 预计算。</summary>
    internal static class RoomCorrectionIrCache
    {
        private sealed class Entry
        {
            public required string Key;
            public required float[][] Ir;
        }

        private const int MaxEntries = 4;
        private static readonly object Gate = new();
        private static readonly List<Entry> Entries = new();

        /// <summary>取 IR；未命中则加载并缓存。加载失败返回 null。</summary>
        public static float[][]? GetOrLoad(string path, int sampleRate)
        {
            string key = path + "|" + sampleRate;
            lock (Gate)
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (string.Equals(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    {
                        return Entries[i].Ir;
                    }
                }
            }

            float[][]? ir = ConvolutionIr.Load(path, sampleRate);
            if (ir == null)
            {
                return null;
            }

            lock (Gate)
            {
                Entries.Add(new Entry { Key = key, Ir = ir });
                while (Entries.Count > MaxEntries)
                {
                    Entries.RemoveAt(0);
                }
            }

            return ir;
        }

        /// <summary>路径失效时清除缓存（如 IR 文件被替换）。</summary>
        public static void Invalidate(string path)
        {
            lock (Gate)
            {
                Entries.RemoveAll(e => string.Equals(e.Key.Split('|')[0], path, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
