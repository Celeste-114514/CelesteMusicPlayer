using System;
using System.IO;
using System.Numerics;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 内建 DST（Digital Stream Transfer）解码器：把 DSDIFF 中「DST 压缩」的 DFF 文件逐帧解码回原始 1-bit DSD，
    /// 输出为 L/R 逐字节交织、MSB-first 的原始 DSD 流（与 <see cref="BuiltInDsdStream"/> 未压缩路径输出完全一致），
    /// 直接喂给现有 DoP 原生直出链路 —— bit-perfect（DST 是无损压缩，还原的 1-bit 与原未压缩 DSD 逐位相同）。
    ///
    /// 为什么要在进程内解码、不走 ffmpeg：
    /// 内置 ffmpeg.exe 带有 <c>dst</c> 解码器，但**缺少 dsdiff(DFF) 解封装器**，无法把 .dff 容器喂给 dst 解码器；
    /// 因此 ffmpeg 转码路径对 DST .dff 不可用。进程内移植 ffmpeg dstdec.c 的算法是唯一正确路径。
    ///
    /// 移植自 FFmpeg libavcodec/dstdec.c（LGPL，Peter Ross），仅取「DST → 1-bit DSD」部分，跳过 dsd2pcm（我们不需要 PCM）。
    /// 关键差异：ffmpeg 的解码缓冲与 PCM float 共享（每样本 4 字节、带 ×4 步长），本实现输出**紧凑** 1-bit 流
    /// （每字节 = 8 个 1-bit 样本，声道按字节交织），以对接 DoP 直出。
    ///
    /// 性能：DST 是逐样本算术解码，单帧约数十万次解码；但解码速度（>1 亿样本/秒）远超实时消耗（DSD256 仅 ~1100 万样本/秒），
    /// 故采用「按需逐帧解码 + 当前帧缓存」：DoPWaveSource 后台顺序读取时逐帧触发解码，内存仅常驻一帧（~数十~数百 KB）。
    /// </summary>
    internal sealed class DstDsdStream : IDsDStream
    {
        private const int DstMaxChannels = 6;
        private const int DstMaxElements = 12;

        // DSD_FS44(sampleRate) = sampleRate*8/44100；DST_SAMPLES_PER_FRAME = 588 * DSD_FS44
        private static int DsdFs44(int sampleRate) => (int)((long)sampleRate * 8 / 44100);
        private static int DstSamplesPerFrame(int sampleRate) => 588 * DsdFs44(sampleRate);

        // 预测系数表（ffmpeg fsets_code_pred_coeff / probs_code_pred_coeff）
        private static readonly int[][] FsetsCodePredCoeff =
        {
            new[] { -8 },
            new[] { -16, 8 },
            new[] { -9, -5, 6 },
        };
        private static readonly int[][] ProbsCodePredCoeff =
        {
            new[] { -8 },
            new[] { -16, 8 },
            new[] { -24, 24, -8 },
        };

        private readonly byte[] _compressed;       // DSD 块中的压缩数据（整段在内存）
        private readonly int[] _frameSizes;        // 每帧压缩字节数
        private readonly int _numFrames;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly DsdRate _rate;
        private readonly int _samplesPerFrame;
        private readonly int _bytesPerFrame;       // samplesPerFrame/8 * channels
        private readonly long _totalDecodedBytes;
        private readonly int[] _frameOffsets;      // 前缀和，长度 _numFrames+1

        // 可复用的解码状态（避免每帧分配）
        private readonly int[][][] _filter = new int[DstMaxElements][][]; // [元素][16][256]
        private readonly DstTable _fsets = new();
        private readonly DstTable _probs = new();
        private readonly byte[][] _status = new byte[DstMaxChannels][];
        private readonly int[] _halfProb = new int[DstMaxChannels];
        private readonly uint[] _mapF = new uint[DstMaxChannels];
        private readonly uint[] _mapP = new uint[DstMaxChannels];
        private readonly BitReader _br = new();
        private readonly ArithCoder _ac = new();
        private readonly byte[] _frameBuf;         // 当前帧解码输出（大小 _bytesPerFrame）

        private int _curFrame = -1;
        private long _decodedPos;
        private long _samplesRead;

        private static readonly byte[] FfReverse = BuildFfReverse();

        private DstDsdStream(byte[] compressed, int[] frameSizes, uint fsFreq, int channels)
        {
            _compressed = compressed;
            _frameSizes = frameSizes;
            _numFrames = frameSizes.Length;
            _sampleRate = (int)fsFreq;
            _channels = channels;
            _samplesPerFrame = DstSamplesPerFrame(_sampleRate);
            if ((_samplesPerFrame & 7) != 0)
            {
                throw new InvalidDataException("DST 每帧样本数非 8 的整数倍，采样率不受支持：" + _sampleRate);
            }

            _bytesPerFrame = _samplesPerFrame / 8 * channels;
            _totalDecodedBytes = (long)_numFrames * _bytesPerFrame;
            _rate = RateFromFreq(fsFreq);

            _frameBuf = new byte[_bytesPerFrame];
            _frameOffsets = new int[_numFrames + 1];
            int off = 0;
            for (int i = 0; i < _numFrames; i++)
            {
                _frameOffsets[i] = off;
                off += _frameSizes[i];
            }

            _frameOffsets[_numFrames] = off;
            for (int i = 0; i < DstMaxElements; i++)
            {
                _filter[i] = new int[16][];
                for (int j = 0; j < 16; j++)
                {
                    _filter[i][j] = new int[256];
                }
            }

            for (int c = 0; c < DstMaxChannels; c++)
            {
                _status[c] = new byte[16];
            }
        }

        public static IDsDStream Create(byte[] compressed, int[] frameSizes, uint fsFreq, int channels)
        {
            if (compressed == null || compressed.Length == 0)
            {
                throw new InvalidDataException("DST 压缩数据为空。");
            }

            if (frameSizes == null || frameSizes.Length == 0)
            {
                throw new InvalidDataException("DST 帧表为空。");
            }

            return new DstDsdStream(compressed, frameSizes, fsFreq, channels);
        }

        public DsdRate Rate => _rate;
        public int Channels => _channels;

        public long TotalSamples
        {
            get
            {
                // 每声道 1-bit 样本数
                return _channels > 0 ? (long)_numFrames * _samplesPerFrame : 0;
            }
        }

        public long SamplesRead => _samplesRead;

        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            int total = 0;
            while (total < count && _decodedPos < _totalDecodedBytes)
            {
                int frameIndex = (int)(_decodedPos / _bytesPerFrame);
                int byteInFrame = (int)(_decodedPos % _bytesPerFrame);
                DecodeFrame(frameIndex);

                int avail = _bytesPerFrame - byteInFrame;
                int take = count - total;
                if (take > avail)
                {
                    take = avail;
                }

                if (take <= 0)
                {
                    break;
                }

                Buffer.BlockCopy(_frameBuf, byteInFrame, buffer, offset + total, take);
                _decodedPos += take;
                total += take;
            }

            _samplesRead += (long)total * 8;
            return total;
        }

        public void SeekSample(long sampleIndex)
        {
            long byteOff = (sampleIndex / 8) * _channels;
            if (byteOff < 0)
            {
                byteOff = 0;
            }

            if (byteOff > _totalDecodedBytes)
            {
                byteOff = _totalDecodedBytes;
            }

            _decodedPos = byteOff;
            _curFrame = -1; // 强制下一帧重新解码
            _samplesRead = sampleIndex;
        }

        public void Dispose()
        {
            // 全部数据已在内存，无文件句柄需要释放
        }

        // ---------- DST 解码（移植自 ffmpeg dstdec.c） ----------

        private void DecodeFrame(int frameIndex)
        {
            if (frameIndex == _curFrame)
            {
                return;
            }

            if (frameIndex < 0 || frameIndex >= _numFrames)
            {
                throw new InvalidDataException("DST 帧索引越界：" + frameIndex);
            }

            int srcOff = _frameOffsets[frameIndex];
            int frameSize = _frameSizes[frameIndex];
            if (srcOff + frameSize > _compressed.Length)
            {
                frameSize = _compressed.Length - srcOff;
            }

            _br.Init(_compressed, srcOff, frameSize);
            Array.Clear(_frameBuf, 0, _frameBuf.Length);

            // decode_frame 头部
            if (_br.GetBits(1) == 0)
            {
                // 未压缩（原始 DSD 直通）：跳过 1 位保留位，再读 6 位保留位（须为 0）
                _br.GetBits(1);
                if (_br.GetBits(6) != 0)
                {
                    throw new InvalidDataException("DST 原始帧保留位非零。");
                }

                int copy = Math.Min(frameSize - 1, _bytesPerFrame);
                if (copy > 0)
                {
                    Buffer.BlockCopy(_compressed, srcOff + 1, _frameBuf, 0, copy);
                }

                _curFrame = frameIndex;
                return;
            }

            // 分段标志（10.4/10.5/10.6）：三者都须为 1
            if (_br.GetBits(1) == 0)
            {
                throw new InvalidDataException("DST：不支持的非相同分段。");
            }

            if (_br.GetBits(1) == 0)
            {
                throw new InvalidDataException("DST：不支持的非全声道相同分段。");
            }

            if (_br.GetBits(1) == 0)
            {
                throw new InvalidDataException("DST：声道分段未结束。");
            }

            // 映射（10.7/10.8/10.9）
            int sameMap = _br.GetBits(1);
            ReadMap(_fsets, _mapF);
            if (sameMap != 0)
            {
                _probs.elements = _fsets.elements;
                Array.Copy(_mapF, _mapP, _channels);
            }
            else
            {
                ReadMap(_probs, _mapP);
            }

            // 半概率（10.10）
            for (int ch = 0; ch < _channels; ch++)
            {
                _halfProb[ch] = _br.GetBits(1);
            }

            // 滤波系数集（10.12）
            ReadTable(_fsets, FsetsCodePredCoeff, 7, 9, 1, 0);
            // 概率表（10.13）
            ReadTable(_probs, ProbsCodePredCoeff, 6, 7, 0, 1);

            // 算术编码数据（10.11）
            if (_br.GetBits(1) != 0)
            {
                throw new InvalidDataException("DST：保留位非零。");
            }

            AcInit();
            BuildFilter();

            for (int ch = 0; ch < DstMaxChannels; ch++)
            {
                _status[ch].AsSpan().Fill(0xAA);
            }

            // 首比特（DST 帧头算术位，参考解码器读取后不使用，仅用于维持算术解码器状态）
            AcGet(ProbDstXBit(_fsets.coeff[0][0]));

            // 逐样本解码
            for (int i = 0; i < _samplesPerFrame; i++)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    uint felem = _mapF[ch];
                    int[][] filter = _filter[felem];
                    byte[] status = _status[ch];

                    int predict = filter[0][status[0]] + filter[1][status[1]] + filter[2][status[2]] + filter[3][status[3]]
                                + filter[4][status[4]] + filter[5][status[5]] + filter[6][status[6]] + filter[7][status[7]]
                                + filter[8][status[8]] + filter[9][status[9]] + filter[10][status[10]] + filter[11][status[11]]
                                + filter[12][status[12]] + filter[13][status[13]] + filter[14][status[14]] + filter[15][status[15]];

                    int prob;
                    if (_halfProb[ch] == 0 || i >= _fsets.length[felem])
                    {
                        uint pelem = _mapP[ch];
                        int idx = Math.Abs(predict) >> 3;
                        int len = _probs.length[pelem];
                        if (idx >= len)
                        {
                            idx = len - 1;
                        }

                        prob = _probs.coeff[pelem][idx];
                    }
                    else
                    {
                        prob = 128;
                    }

                    int residual = AcGet(prob);
                    int v = ((predict >> 15) ^ residual) & 1;

                    // 紧凑布局：字节偏移 = (i>>3)*channels + ch；位 = 7-(i&7)
                    int byteOff = (i >> 3) * _channels + ch;
                    _frameBuf[byteOff] |= (byte)(v << (7 - (i & 7)));

                    // status 左移 1 位，新比特 v 进入最低字节最低位（两个 64-bit 小端字）
                    ulong lo = ReadLE64(status, 0);
                    ulong hi = ReadLE64(status, 8);
                    ulong newHi = (hi << 1) | (lo >> 63);
                    ulong newLo = (lo << 1) | (ulong)v;
                    WriteLE64(status, 8, newHi);
                    WriteLE64(status, 0, newLo);
                }
            }

            _curFrame = frameIndex;
        }

        private void ReadMap(DstTable t, uint[] map)
        {
            t.elements = 1;
            map[0] = 0;
            if (_br.GetBits(1) == 0)
            {
                for (int ch = 1; ch < _channels; ch++)
                {
                    int bits = AvLog2((uint)t.elements) + 1;
                    int m = _br.GetBits(bits);
                    map[ch] = (uint)m;
                    if (m == t.elements)
                    {
                        t.elements++;
                        if (t.elements >= DstMaxElements)
                        {
                            throw new InvalidDataException("DST：滤波/概率元素数过多。");
                        }
                    }
                    else if (m > t.elements)
                    {
                        throw new InvalidDataException("DST：映射表越界。");
                    }
                }
            }
            else
            {
                for (int ch = 0; ch < DstMaxChannels; ch++)
                {
                    map[ch] = 0;
                }
            }
        }

        private void ReadTable(DstTable t, int[][] codePred, int lengthBits, int coeffBits, int isSigned, int offset)
        {
            for (int i = 0; i < t.elements; i++)
            {
                t.length[i] = _br.GetBits(lengthBits) + 1;
                if (_br.GetBits(1) == 0)
                {
                    // 未预测：直接读 length[i] 个系数
                    ReadUncodedCoeff(t.coeff[i], t.length[i], coeffBits, isSigned, offset);
                }
                else
                {
                    int method = _br.GetBits(2);
                    if (method == 3)
                    {
                        throw new InvalidDataException("DST：无效的预测方法。");
                    }

                    ReadUncodedCoeff(t.coeff[i], method + 1, coeffBits, isSigned, offset);
                    int lsbSize = _br.GetBits(3);
                    for (int j = method + 1; j < t.length[i]; j++)
                    {
                        long x = 0;
                        for (int k = 0; k <= method; k++)
                        {
                            x += codePred[method][k] * (uint)t.coeff[i][j - k - 1];
                        }

                        int c = GetSrGolombDst(lsbSize);
                        if (x >= 0)
                        {
                            c -= (int)((x + 4) / 8);
                        }
                        else
                        {
                            c += (int)((-x + 3) / 8);
                        }

                        if (isSigned == 0)
                        {
                            if (c < offset || c >= offset + (1 << coeffBits))
                            {
                                throw new InvalidDataException("DST：系数越界。");
                            }
                        }

                        t.coeff[i][j] = c;
                    }
                }
            }
        }

        private void ReadUncodedCoeff(int[] dst, int n, int coeffBits, int isSigned, int offset)
        {
            for (int i = 0; i < n; i++)
            {
                int val = isSigned != 0 ? _br.GetSbits(coeffBits) : _br.GetBits(coeffBits);
                dst[i] = val + offset;
            }
        }

        private void AcInit()
        {
            _ac.a = 4095;
            _ac.c = (uint)_br.GetBits(12);
        }

        private void BuildFilter()
        {
            for (int i = 0; i < _fsets.elements; i++)
            {
                int length = _fsets.length[i];
                for (int j = 0; j < 16; j++)
                {
                    int total = length - j * 8;
                    if (total < 0)
                    {
                        total = 0;
                    }
                    else if (total > 8)
                    {
                        total = 8;
                    }

                    int[] ftab = _filter[i][j];
                    for (int k = 0; k < 256; k++)
                    {
                        long v = 0;
                        for (int l = 0; l < total; l++)
                        {
                            int bit = (k >> l) & 1;
                            v += (bit * 2 - 1) * (long)_fsets.coeff[i][j * 8 + l];
                        }

                        ftab[k] = (int)v; // 实测 |v| <= 2040，必然落在 int16 范围
                    }
                }
            }
        }

        private int AcGet(int p)
        {
            int k = (int)((_ac.a >> 8) | ((_ac.a >> 7) & 1));
            uint q = (uint)k * (uint)p;
            uint aQ = _ac.a - q;
            int e = _ac.c < aQ ? 1 : 0;
            if (e != 0)
            {
                _ac.a = aQ;
            }
            else
            {
                _ac.c -= aQ;
                _ac.a = q;
            }

            if (_ac.a < 2048)
            {
                int n = 11 - AvLog2(_ac.a);
                _ac.a <<= n;
                _ac.c = (_ac.c << n) | (uint)_br.GetBits(n);
            }

            return e;
        }

        private static int ProbDstXBit(int c)
        {
            return (FfReverse[c & 127] >> 1) + 1;
        }

        // ---------- 比特读取 / Golomb ----------

        private int GetSrGolombDst(int k)
        {
            int v = GetUrGolombJpegls(k, _br.GetBitsLeft(), 0);
            if (v != 0 && _br.GetBits(1) != 0)
            {
                v = -v;
            }

            return v;
        }

        private int GetUrGolombJpegls(int k, int limit, int escLen)
        {
            int buf = _br.GetBits(k);
            if (buf >= limit - (limit >> k) + 1)
            {
                int buf2 = _br.GetBits(escLen);
                int range = (limit << 1) - (((limit >> k) + 1) << k);
                if (buf < limit)
                {
                    buf += (buf2 << k) - range + 1;
                }
                else
                {
                    buf = ((buf2 + limit - (limit >> k) + 1) << k) | (buf - limit);
                }
            }

            return buf;
        }

        // ---------- 工具 ----------

        private static int AvLog2(uint x)
        {
            if (x == 0)
            {
                return 0;
            }

            return 31 - BitOperations.LeadingZeroCount(x);
        }

        private static ulong ReadLE64(byte[] b, int off)
        {
            return (ulong)b[off]
                 | ((ulong)b[off + 1] << 8)
                 | ((ulong)b[off + 2] << 16)
                 | ((ulong)b[off + 3] << 24)
                 | ((ulong)b[off + 4] << 32)
                 | ((ulong)b[off + 5] << 40)
                 | ((ulong)b[off + 6] << 48)
                 | ((ulong)b[off + 7] << 56);
        }

        private static void WriteLE64(byte[] b, int off, ulong v)
        {
            b[off] = (byte)v;
            b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16);
            b[off + 3] = (byte)(v >> 24);
            b[off + 4] = (byte)(v >> 32);
            b[off + 5] = (byte)(v >> 40);
            b[off + 6] = (byte)(v >> 48);
            b[off + 7] = (byte)(v >> 56);
        }

        private static byte[] BuildFfReverse()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int b = i, r = 0;
                for (int k = 0; k < 8; k++)
                {
                    r = (r << 1) | (b & 1);
                    b >>= 1;
                }

                t[i] = (byte)r;
            }

            return t;
        }

        private static DsdRate RateFromFreq(uint freq) => freq switch
        {
            >= 22579200 => DsdRate.Dsd512,
            >= 11289600 => DsdRate.Dsd256,
            >= 5644800 => DsdRate.Dsd128,
            _ => DsdRate.Dsd64,
        };

        // ---------- 内部类型 ----------

        private sealed class BitReader
        {
            private byte[] _buf = Array.Empty<byte>();
            private int _bitPos;   // 绝对位位置
            private int _bitEnd;   // 总位数
            private ulong _cache;
            private int _cacheBits;

            public void Init(byte[] buf, int byteOff, int byteLen)
            {
                _buf = buf;
                _bitPos = byteOff * 8;
                _bitEnd = byteOff * 8 + byteLen * 8;
                _cache = 0;
                _cacheBits = 0;
                Refill();
            }

            public int GetBits(int n)
            {
                if (n <= 0)
                {
                    return 0;
                }

                if (_cacheBits < n)
                {
                    Refill();
                }

                int val;
                if (n >= 32)
                {
                    val = (int)(_cache >> (64 - n));
                }
                else
                {
                    uint mask = (1U << n) - 1;
                    val = (int)((_cache >> (64 - n)) & mask);
                }

                _cache <<= n;
                _cacheBits -= n;
                _bitPos += n;
                return val;
            }

            public int GetBitsLeft() => _bitEnd - _bitPos;

            public int GetSbits(int n)
            {
                int v = GetBits(n);
                return (int)((uint)v << (32 - n)) >> (32 - n);
            }

            private void Refill()
            {
                int bytePos = _bitPos >> 3;
                if (bytePos >= _buf.Length)
                {
                    _cache = 0;
                    _cacheBits = 0;
                    return;
                }

                ulong w = 0;
                int maxB = Math.Min(8, _buf.Length - bytePos);
                for (int i = 0; i < maxB; i++)
                {
                    w = (w << 8) | _buf[bytePos + i];
                }

                // 丢弃字节内位于当前位之前的前导无效位，使首个有效位对齐到 _cache 最高位
                w <<= (_bitPos & 7);
                _cache = w;
                _cacheBits = maxB * 8 - (_bitPos & 7);
            }
        }

        private sealed class ArithCoder
        {
            public uint a;
            public uint c;
        }

        private sealed class DstTable
        {
            public int elements;
            public int[] length = new int[DstMaxElements];
            public int[][] coeff = new int[DstMaxElements][];

            public DstTable()
            {
                for (int i = 0; i < DstMaxElements; i++)
                {
                    coeff[i] = new int[128];
                }
            }
        }
    }
}
