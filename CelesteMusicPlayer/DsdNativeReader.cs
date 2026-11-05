using System;
using System.IO;
using System.Text;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 原生 DSD 专用的 1-bit 码流读取器：DSF 与 DSDIFF(DFF) 统一成一个字节源。
    ///
    /// 为什么不复用 DsdBitstream：那份是 DoP 直出链路的已验证实现，只认 DSF / 1-2 声道 /
    /// DSD64-256，且 DFF 在探头阶段就回退。原生路径要支持 DFF、多声道取前两声道、更高倍率、
    /// 以及任意位置 seek，改它会把已验证的 DoP 链路一起拖下水，所以另起一份、职责单一。
    ///
    /// 输出恒为 L/R 逐字节交织的一个字节 = 某声道连续 8 个 1-bit 采样：
    ///   · mono：把唯一的声道复制成 L/R
    ///   · 多声道：只取前两个声道（与 HiFi Player 的做法一致），其余声道字节跳过
    /// 两种容器的字节内位序不同，由 <see cref="LsbFirst"/> 告诉 AsioNativeDsdOutput 要不要反转。
    ///
    /// ── 容器布局（写错就是满耳噪声，依据 DSDIFF 1.5 规范与 DSF 规范）──
    /// DSF : DSD / fmt / data 块；数据按「每声道 blockSize 字节」块交错，
    ///       一个 pair-block = ch0[blockSize] ch1[blockSize] …；常见 bitsPerSample=1 → LSB-first。
    /// DFF : FRM8 / PROP(FS,CHNL,CMPR) / DSD 块，全部大端；数据逐字节按声道交错
    ///       （一帧 N 字节 = 每声道各 1 字节），恒 MSB-first；CMPR='DST ' 为压缩，不支持。
    /// </summary>
    internal sealed class DsdNativeReader : IWaveProvider, IDisposable
    {
        public enum ContainerKind { Dsf, Dff }

        internal struct HeaderInfo
        {
            public ContainerKind Kind;
            public uint RateHz;
            public int Channels;
            public bool LsbFirst;
            public int BlockSize;      // DSF 块大小；DFF 恒 1（逐字节帧）
            public long DataStart;
            public long DataBytes;

            public long TotalFrames => Channels > 0 ? DataBytes / Channels : 0;
            public long TotalSamples => TotalFrames * 8;
            public string ContainerName => Kind == ContainerKind.Dsf ? "DSF" : "DFF";
            public string LevelName => RateHz switch
            {
                2822400 => "DSD64",
                5644800 => "DSD128",
                11289600 => "DSD256",
                22579200 => "DSD512",
                45158400 => "DSD1024",
                _ => RateHz.ToString(),
            };
        }

        private readonly FileStream _fs;
        private readonly HeaderInfo _h;
        private readonly byte[] _raw;
        private int _rawFrames;         // _raw 中有效「源帧」数
        private int _rawPos;            // _raw 内已消费的源帧号
        private long _framePos;         // 全局已输出的源帧号
        private long _pendingFrame = -1;
        private bool _disposed;

        /// <summary>假容器格式：sampleRate = 位时钟/8、8bit、2ch ⇒ 每秒字节数正好等于 1-bit 交织流字节率。</summary>
        public WaveFormat WaveFormat { get; }

        public HeaderInfo Header => _h;
        public uint NativeRateHz => _h.RateHz;
        public int SourceChannels => _h.Channels;
        public int OutputChannels => 2;
        public bool LsbFirst => _h.LsbFirst;
        public long TotalSamples => _h.TotalSamples;
        public long TotalFrames => _h.TotalFrames;
        public string ContainerName => _h.ContainerName;
        public string LevelName => _h.LevelName;

        private DsdNativeReader(FileStream fs, HeaderInfo h)
        {
            _fs = fs;
            _h = h;
            WaveFormat = new WaveFormat(checked((int)(h.RateHz / 8)), 8, 2);
            int rawBytes = h.Kind == ContainerKind.Dsf
                ? Math.Max(1, h.BlockSize * h.Channels)
                : 1 << 16;
            if (h.Kind == ContainerKind.Dff)
            {
                rawBytes = (rawBytes / h.Channels) * h.Channels;
            }
            _raw = new byte[rawBytes];
        }

        /// <summary>只读文件头，用于起播守卫与「同格式无缝」判定；不持有文件句柄。</summary>
        public static HeaderInfo Peek(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ParseHeader(fs);
        }

        public static DsdNativeReader Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                HeaderInfo h = ParseHeader(fs);
                if (h.RateHz == 0 || h.Channels <= 0 || h.DataBytes <= 0)
                {
                    throw new InvalidDataException("DSD 头部不完整（缺采样率 / 声道数 / 数据块）。");
                }

                return new DsdNativeReader(fs, h);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        // ---------- 头部解析 ----------
        private static HeaderInfo ParseHeader(FileStream fs)
        {
            string magic = ReadTag(fs, 4);
            if (magic == "DSD ") return ParseDsf(fs);
            if (magic == "FRM8") return ParseDff(fs);
            throw new InvalidDataException("不是 DSF / DFF 文件（" + magic + "）。");
        }

        private static HeaderInfo ParseDsf(FileStream fs)
        {
            ReadI64LE(fs);                 // DSD chunk size
            ReadI64LE(fs);                 // total file size
            ReadI64LE(fs);                 // metadata pointer
            if (ReadTag(fs, 4) != "fmt ") throw new InvalidDataException("DSF 缺 fmt 块。");
            ReadI64LE(fs);                 // fmt chunk size
            ReadU32LE(fs);                 // version
            ReadU32LE(fs);                 // format id
            ReadU32LE(fs);                 // channel type
            uint ch = ReadU32LE(fs);
            uint freq = ReadU32LE(fs);
            uint bps = ReadU32LE(fs);       // 1 = LSB-first（主流 DSF），8 = MSB-first
            ReadU64LE(fs);                 // sample count
            uint blockSize = ReadU32LE(fs);
            ReadU32LE(fs);                 // reserved（4 字节，漏跳会读错 data 块）

            string tag = ReadTag(fs, 4);
            long dataStart;
            long dataSize;
            if (tag == "data")
            {
                dataSize = ReadI64LE(fs);
                dataStart = fs.Position;
            }
            else
            {
                // 畸形 fmt（保留字段变长等）兜底：在头部小窗内找 data 标签
                (dataStart, dataSize) = ScanDsfData(fs);
            }

            long avail = Math.Min(dataSize, fs.Length - dataStart);
            if (ch < 1 || freq == 0 || blockSize == 0 || avail <= 0)
            {
                throw new InvalidDataException("DSF 头部字段非法（声道/位时钟/块大小/数据块）。");
            }

            StartupLog.Write($"[DSD原生] 解析 DSF: {ch}ch {freq}Hz blockSize={blockSize} bitsPerSample={bps} dataBytes={avail}");
            return new HeaderInfo
            {
                Kind = ContainerKind.Dsf,
                RateHz = freq,
                Channels = (int)ch,
                LsbFirst = bps != 8,
                BlockSize = (int)blockSize,
                DataStart = dataStart,
                DataBytes = avail,
            };
        }

        private static (long Start, long Size) ScanDsfData(FileStream fs)
        {
            long to = Math.Min(fs.Length - 12, 4096);
            for (long p = 44; p <= to; p++)
            {
                fs.Position = p;
                if (ReadTag(fs, 4) != "data") continue;
                long size = ReadI64LE(fs);
                long start = fs.Position;
                if (size > 0 && start + size <= fs.Length) return (start, size);
            }

            throw new InvalidDataException("DSF 缺 data 块。");
        }

        private static HeaderInfo ParseDff(FileStream fs)
        {
            ReadI64BE(fs);                 // FRM8 size
            if (ReadTag(fs, 4) != "DSD ") throw new InvalidDataException("DFF 的 formType 不是 DSD。");

            uint freq = 0;
            int ch = 0;
            bool dst = false;
            long dataStart = 0;
            long dataBytes = 0;

            while (fs.Position + 12 <= fs.Length)
            {
                string id = ReadTag(fs, 4);
                long size = ReadI64BE(fs);
                long start = fs.Position;
                if (size < 0 || size > fs.Length - start) break;

                if (id == "PROP")
                {
                    ReadTag(fs, 4);         // "SND "
                    long propEnd = Math.Min(start + size, fs.Length);
                    while (fs.Position + 12 <= propEnd)
                    {
                        string sid = ReadTag(fs, 4);
                        long ssize = ReadI64BE(fs);
                        long sub = fs.Position;
                        if (ssize < 0 || ssize > propEnd - sub) break;
                        if (sid == "FS  ") freq = ReadU32BE(fs);
                        else if (sid == "CHNL") ch = ReadU16BE(fs);
                        else if (sid == "CMPR")
                        {
                            string comp = ReadTag(fs, 4);
                            dst = comp.StartsWith("DST", StringComparison.Ordinal);
                        }

                        fs.Position = Math.Min(sub + ssize, propEnd);
                    }
                }
                else if (id == "DSD ")
                {
                    dataStart = start;
                    dataBytes = Math.Min(size, fs.Length - start);
                    break;
                }

                fs.Position = Math.Min(start + size + (size & 1), fs.Length); // IFF 偶对齐
            }

            if (dst)
            {
                throw new InvalidDataException("DST 压缩 DFF 需要专用解码器，原生直出不支持（会自动回退 PCM）。");
            }

            if (freq == 0 || ch <= 0 || dataStart == 0 || dataBytes <= 0)
            {
                throw new InvalidDataException("DFF 头部缺 FS / CHNL / DSD 数据块（或不是未压缩 DSD）。");
            }

            StartupLog.Write($"[DSD原生] 解析 DFF: {ch}ch {freq}Hz 逐字节声道交错 MSB-first dataBytes={dataBytes}");
            return new HeaderInfo
            {
                Kind = ContainerKind.Dff,
                RateHz = freq,
                Channels = ch,
                LsbFirst = false,
                BlockSize = 1,
                DataStart = dataStart,
                DataBytes = dataBytes,
            };
        }

        // ---------- 读取 / 随机定位 ----------
        public int Read(byte[] buffer, int offset, int count)
        {
            int framesWanted = count / 2;      // 输出恒 2ch 交织，1 帧 = 2 字节
            int produced = 0;
            while (produced < framesWanted)
            {
                if (!EnsureSourceFrames()) break;
                int take = Math.Min(framesWanted - produced, _rawFrames - _rawPos);
                if (take <= 0) break;
                for (int i = 0; i < take; i++)
                {
                    int f = _rawPos + i;
                    byte l = SourceByte(f, 0);
                    byte r = _h.Channels >= 2 ? SourceByte(f, 1) : l;
                    int p = offset + (produced + i) * 2;
                    buffer[p] = l;
                    buffer[p + 1] = r;
                }

                _rawPos += take;
                produced += take;
            }

            _framePos += produced;
            return produced * 2;
        }

        private byte SourceByte(int localFrame, int channel)
            => _h.Kind == ContainerKind.Dsf
                ? _raw[channel * _h.BlockSize + localFrame]
                : _raw[localFrame * _h.Channels + channel];

        private bool EnsureSourceFrames()
        {
            if (_rawPos < _rawFrames) return true;

            _rawPos = 0;
            _rawFrames = 0;
            if (_pendingFrame >= 0)
            {
                long frame = _pendingFrame;
                _pendingFrame = -1;
                long byteOff = _h.Kind == ContainerKind.Dsf
                    ? (frame / _h.BlockSize) * _h.BlockSize * _h.Channels
                    : frame * _h.Channels;
                _fs.Position = _h.DataStart + Math.Min(byteOff, _h.DataBytes);
                if (_h.Kind == ContainerKind.Dsf)
                {
                    _rawPos = (int)(frame % _h.BlockSize);
                }
            }

            long dataEnd = _h.DataStart + _h.DataBytes;
            if (_fs.Position >= dataEnd) return false;

            int want = _h.Kind == ContainerKind.Dsf
                ? _h.BlockSize * _h.Channels
                : (_raw.Length / _h.Channels) * _h.Channels;
            want = (int)Math.Min(want, dataEnd - _fs.Position);
            if (want <= 0) return false;

            int got = _fs.Read(_raw, 0, want);
            if (got <= 0) return false;
            if (got < want)
            {
                // 尾部不足按整帧截断，不补伪造数据；feeder 会用 0x69/0x96 合法静音码收尾
                for (int i = got; i < want; i++) _raw[i] = 0x69;
            }

            int frames = want / _h.Channels;
            if (_h.Kind == ContainerKind.Dsf)
            {
                // 块交错：want = blockSize*channels，帧数 = blockSize
                frames = _h.BlockSize;
            }

            _rawFrames = frames;
            return _rawPos < _rawFrames;
        }

        /// <summary>按「每声道 1-bit 样本数」定位；1 帧 = 8 个 1-bit 采样。</summary>
        public void SeekSample(long sampleIndex)
        {
            long frame = Math.Clamp(sampleIndex / 8, 0, _h.TotalFrames);
            _pendingFrame = frame;
            _rawPos = 0;
            _rawFrames = 0;
            _framePos = frame;
        }

        public long CurrentFrame => _framePos;

        // ---------- 大小端读取（DSF 小端、DFF 大端，绝不混用）----------
        private static string ReadTag(Stream s, int n)
        {
            var buf = new byte[n];
            if (s.Read(buf, 0, n) != n) throw new EndOfStreamException("DSD 头部提前结束。");
            return Encoding.ASCII.GetString(buf);
        }

        private static uint ReadU32LE(Stream s)
        {
            Span<byte> b = stackalloc byte[4];
            if (s.Read(b) != 4) throw new EndOfStreamException();
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        private static uint ReadU32BE(Stream s)
        {
            Span<byte> b = stackalloc byte[4];
            if (s.Read(b) != 4) throw new EndOfStreamException();
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        private static ushort ReadU16BE(Stream s)
        {
            Span<byte> b = stackalloc byte[2];
            if (s.Read(b) != 2) throw new EndOfStreamException();
            return (ushort)((b[0] << 8) | b[1]);
        }

        private static ulong ReadU64LE(Stream s)
        {
            Span<byte> b = stackalloc byte[8];
            if (s.Read(b) != 8) throw new EndOfStreamException();
            ulong v = 0;
            for (int i = 7; i >= 0; i--) v = (v << 8) | b[i];
            return v;
        }

        private static ulong ReadU64BE(Stream s)
        {
            Span<byte> b = stackalloc byte[8];
            if (s.Read(b) != 8) throw new EndOfStreamException();
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | b[i];
            return v;
        }

        private static long ReadI64LE(Stream s) => unchecked((long)ReadU64LE(s));
        private static long ReadI64BE(Stream s) => unchecked((long)ReadU64BE(s));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _fs.Dispose();
        }
    }
}
