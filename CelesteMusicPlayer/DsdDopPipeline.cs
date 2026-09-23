using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DSF 比特流（ECHO 方案：DoP 直出只吃 DSF）：分段池化读 + 块交错重交织，
    /// 统一输出 L,R 逐字节交织的 1-bit DSD 字节流（mono 在读出口复制为双声道，DoP 容器恒 2ch）。
    /// 移植自已实证的 BuiltInDsdStream DSF 通路（分段池化读 / stride 对齐 / seek 公式不变）；
    /// DFF/DST 不走本类（探头阶段即回退 PCM）。
    /// </summary>
    internal sealed class DsdBitstream : IDisposable
    {
        private readonly FileStream _fs;
        private readonly long _dataStart;        // DSD 数据块在文件中的起始偏移
        private readonly long _dataBytes;        // DSD 数据总字节
        private readonly uint _nativeHz;         // DSD 位时钟（2822400/5644800/11289600）
        private readonly int _channels;          // 1 或 2（探头已拦下 3+ 声道）
        private readonly int _blockSize;         // DSF 块交错粒度（bytes/声道/块）
        private long _samplesRead;

        // 分段池化读：FileStream 保持打开 + ArrayPool<byte> 租 1MB 分段（容量/偏移按 stride 对齐
        // → pair-block 永不跨段，无需跨段拼接）；内存定额 ~1MB，与曲长无关。
        private byte[]? _seg;
        private readonly int _segBytes;
        private long _segStart = -1;
        private int _segLen;
        private int _segPos;
        private long _dataPos;
        private readonly int _stride;            // 完整交错块步长（= blockSize×声道数）

        private byte[]? _block;                 // DSF 重交织缓冲（复用）
        private int _blockPos;
        private readonly object _ioLock = new();

        private DsdBitstream(FileStream fs, long dataStart, long dataBytes,
            uint nativeHz, int channels, int blockSize)
        {
            _fs = fs;
            _dataStart = dataStart;
            _dataBytes = dataBytes;
            _nativeHz = nativeHz;
            _channels = channels;
            _blockSize = blockSize;

            _stride = Math.Max(2, blockSize * Math.Max(2, channels));
            int seg = 1 << 20;
            int r = seg % _stride;
            if (r != 0)
            {
                seg += _stride - r;
            }

            _segBytes = (int)Math.Min(seg, Math.Max(_stride, ((dataBytes + _stride - 1) / _stride) * _stride));
            StartupLog.Write(string.Format(
                "[DSD流] 分段池化读 dataBytes={0} 分段={1}B stride={2}（内存定额，不再整读）",
                dataBytes, _segBytes, _stride));
        }

        public static DsdBitstream Open(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (ext != ".dsf")
                {
                    throw new InvalidDataException("DoP 比特流仅支持 DSF（ECHO 方案；DFF 走 PCM 回退）。");
                }

                return OpenDsf(fs);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        // ---------- DSF ----------
        private static DsdBitstream OpenDsf(Stream fs)
        {
            if (ReadTag(fs, 4) != "DSD ")
            {
                throw new InvalidDataException("不是有效的 DSF 文件（缺 DSD 头）。");
            }

            ReadI64(fs);          // header size
            fs.Position += 16;    // fileTotalSize(8) + metadataPtr(8)

            if (ReadTag(fs, 4) != "fmt ")
            {
                throw new InvalidDataException("DSF 缺 fmt 块。");
            }

            ReadI64(fs);          // fmt size
            fs.Position += 4 + 4; // version + format id
            ReadU32(fs);          // channel type(0=stereo)
            uint ch = ReadU32(fs);
            uint freq = ReadU32(fs);   // e.g. 2822400
            ReadU32(fs);          // bits per sample(=1)
            ReadU64(fs);          // sampleCount
            uint blockSize = ReadU32(fs); // e.g. 4096

            while (ReadTag(fs, 4) == "data")
            {
                long size = ReadI64(fs);
                long start = fs.Position;
                long avail = Math.Min(size, fs.Length - start);
                StartupLog.Write(string.Format(
                    "[DSF解析] 频道={0} blockSize={1} freq={2} dataBytes={3}",
                    ch, blockSize, freq, Math.Max(0, avail)));
                return new DsdBitstream(
                    fs as FileStream ?? throw new InvalidDataException("DSF 需文件流"),
                    start, Math.Max(0, avail), freq, (int)ch, (int)blockSize);
            }

            throw new InvalidDataException("DSF 缺 data 块。");
        }

        public int Channels => _channels;
        public uint NativeRateHz => _nativeHz;

        /// <summary>DoP 传输速率（native/16）。</summary>
        public int DopRateHz => DsdProbe.TryDopRate(_nativeHz, out int hz) ? hz : 0;

        /// <summary>每声道 1-bit 样本总数（= dataBytes × 8 / 声道数）。</summary>
        public long TotalSamples => _channels > 0 ? _dataBytes * 8 / _channels : 0;

        public long SamplesRead => _samplesRead;

        // ---------- 读取（统一 L/R 交织字节） ----------
        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_ioLock)
            {
                int total = 0;
                int pos = offset;
                while (total < count)
                {
                    int n = _channels == 1
                        ? ReadMonoDuplicated(buffer, pos, count - total)
                        : ReadDsfInterleaved(buffer, pos, count - total);
                    if (n <= 0)
                    {
                        break;
                    }

                    total += n;
                    pos += n;
                }

                _samplesRead += (long)total * 8 / Math.Max(1, _channels);
                return total;
            }
        }

        /// <summary>确保 [absOffset, ...) 落在当前分段内；不在则换段重读。absOffset 需 stride 对齐。</summary>
        private bool EnsureSegment(long absOffset)
        {
            if (_seg == null)
            {
                _seg = ArrayPool<byte>.Shared.Rent(_segBytes);
            }

            if (absOffset < _segStart || absOffset >= _segStart + _segLen)
            {
                try
                {
                    _fs.Position = _dataStart + absOffset;
                }
                catch (Exception caught)
                {
                    global::CelesteMusicPlayer.StartupLog.WriteException("DsdDopPipeline.cs", caught);
                    return false;
                }

                _segLen = _fs.Read(_seg, 0, _segBytes);
                _segStart = absOffset;
            }

            _segPos = (int)(absOffset - _segStart);
            return _segLen > 0 && _segPos < _segLen;
        }

        /// <summary>mono：块内每字节即一个声道样本，读出口复制为 L,R（DoP 容器恒 2ch）。</summary>
        private int ReadMonoDuplicated(byte[] buffer, int offset, int count)
        {
            if (_block == null && !LoadNextDsfBlock())
            {
                return 0;
            }

            int pairs = Math.Min(count / 2, _blockSize - _blockPos);
            for (int i = 0; i < pairs; i++)
            {
                byte v = _block![_blockPos + i];
                buffer[offset + i * 2] = v;
                buffer[offset + i * 2 + 1] = v;
            }

            _blockPos += pairs;
            if (_blockPos >= _blockSize)
            {
                _block = null;
            }

            return pairs * 2;
        }

        /// <summary>DSF 立体声：数据按 blockSize/声道 块交错（L块,R块…），读时重交织成 L,R 逐字节。</summary>
        private int ReadDsfInterleaved(byte[] buffer, int offset, int count)
        {
            if (_block == null && !LoadNextDsfBlock())
            {
                return 0;
            }

            int produced = 0;
            int pairs = Math.Min(count / 2, _blockSize - _blockPos);
            for (int i = 0; i < pairs; i++)
            {
                int bi = _blockPos + i;
                byte l = _block![bi];
                byte r = _block[bi + _blockSize];
                buffer[offset + produced++] = l;
                buffer[offset + produced++] = r;
            }

            _blockPos += pairs;
            if (_blockPos >= _blockSize)
            {
                _block = null;
            }

            return produced;
        }

        private bool LoadNextDsfBlock()
        {
            if (_dataPos >= _dataBytes)
            {
                return false;
            }

            if (!EnsureSegment(_dataPos))
            {
                return false;
            }

            int alloc = _blockSize * _channels; // 完整交错块（立体声=L+R）
            _block ??= new byte[alloc];
            int got = Math.Min(alloc, _segLen - _segPos);
            if (got > 0)
            {
                Buffer.BlockCopy(_seg!, _segPos, _block, 0, got);
            }

            if (got < alloc)
            {
                Array.Clear(_block, got, alloc - got); // 尾部不足补零
            }

            _blockPos = 0;
            _dataPos += alloc;
            _segPos += got;
            return got > 0;
        }

        /// <summary>按 1-bit 样本数 seek（DSF 块交错公式：每声道字节偏移 → pair-block → 块内偏移）。</summary>
        public void SeekSample(long sampleIndex)
        {
            lock (_ioLock)
            {
                if (_channels <= 0 || _blockSize <= 0)
                {
                    long byteOff = _channels > 0 ? sampleIndex / 8 / _channels : 0;
                    _dataPos = Math.Min(byteOff, _dataBytes);
                    _block = null;
                    _samplesRead = sampleIndex;
                    return;
                }

                long chByte = sampleIndex / 8;                 // 每声道字节偏移
                long pair = chByte / _blockSize;               // 目标所在 L/R pair-block 序号
                int off = (int)(chByte % _blockSize);          // 块内字节位置
                long blockDataPos = pair * (long)_blockSize * _channels;

                int alloc = _blockSize * _channels;
                _block ??= new byte[alloc];
                _blockPos = 0;
                _dataPos = Math.Min(blockDataPos, _dataBytes);
                if (blockDataPos < _dataBytes && EnsureSegment(blockDataPos))
                {
                    int got = Math.Min(alloc, _segLen - _segPos);
                    if (got > 0)
                    {
                        Buffer.BlockCopy(_seg!, _segPos, _block, 0, got);
                    }

                    if (got < alloc)
                    {
                        Array.Clear(_block, got, alloc - got);
                    }

                    _blockPos = Math.Min(off, _blockSize);
                    _dataPos = blockDataPos + alloc;
                    _segPos += got;
                }
                else
                {
                    Array.Clear(_block, 0, alloc); // seek 越界：空块静默
                    _blockPos = Math.Min(off, _blockSize);
                }

                _samplesRead = sampleIndex;
            }
        }

        public void Dispose()
        {
            if (_seg != null)
            {
                ArrayPool<byte>.Shared.Return(_seg);
                _seg = null;
            }

            _fs.Dispose();
        }

        // ---------- 字节工具 ----------
        private static string ReadTag(Stream s, int n)
        {
            byte[] b = ReadN(s, n);
            return System.Text.Encoding.ASCII.GetString(b, 0, n);
        }

        private static byte[] ReadN(Stream s, int n)
        {
            var b = new byte[n];
            int r = 0;
            while (r < n)
            {
                int k = s.Read(b, r, n - r);
                if (k <= 0)
                {
                    break;
                }

                r += k;
            }

            return b;
        }

        private static long ReadI64(Stream s) => BitConverter.ToInt64(ReadN(s, 8), 0);

        private static ulong ReadU64(Stream s) => BitConverter.ToUInt64(ReadN(s, 8), 0);

        private static uint ReadU32(Stream s) => BitConverter.ToUInt32(ReadN(s, 4), 0);
    }

    /// <summary>
    /// DoP24（DSD over PCM）封装源：把 <see cref="DsdBitstream"/> 的 1-bit DSD 流封装为
    /// 176.4k/352.8k/705.6k × 24bit × 2ch 的 PCM 容器帧，供 WASAPI 独占原样直通（或经
    /// <see cref="AsioDoPProvider"/> 重铸为 32bit 喂 ASIO）。
    /// 位序（DoP 1.1 规范图实证，2026-09-22 dop_fix）：小端 wire = [次 8bit 原样][老 8bit 原样][标记]，
    /// 零位反转；标记 0x05/0xFA 按绝对帧序号奇偶交替；尾补 0x69 静音。
    /// 32MB 非托管环形缓冲（SPSC）：后台线程「读源→装箱→入环」（环满则等待，天然限流读盘），
    /// render 只从环内顺序取；内存定额与曲长无关（DSD64≈30s），AllocHGlobal 不在 GC 堆上。
    /// </summary>
    internal sealed class DoP24LeSource : IWaveSourceProvider, IDisposable
    {
        private const int RawChunk = 1 << 17;          // 后台线程每块从源读取的原始交织字节（128KB）
        private const double PrebufferSeconds = 0.10;
        private const int PrebufferTimeoutMs = 3000;
        private const int BpF = 6;                     // 24bit：每立体声帧 6 字节（L3+R3）
        private const int RingBytesWanted = 32 << 20;

        private readonly DsdBitstream _src;
        private readonly int _frameRate;               // DoP 容器帧率（= native/16）
        private readonly long _totalFrames;            // DoP 容器帧总数
        private readonly object _lock = new();

        private readonly IntPtr _ring;                 // 非托管环形缓冲
        private readonly int _ringBytes;
        private long _writePos;                        // 生产者逻辑写位（单调递增）
        private long _readPos;                         // 消费者逻辑读位（单调递增）
        private long _encodedFrames;                   // 已封装的绝对帧序号（marker 奇偶用，不随 Seek 回退）
        private long _pendingSeekFrame = -1;           // >=0 表示生产者需先 seek 源再继续
        private long _ringWrapped;                     // 诊断：环形回绕次数
        private long _producerStalls;                  // 诊断：生产者因环满等待次数
        private long _frameIndex;                      // 续静音帧的 marker 奇偶
        private long _framesRead;
        private volatile bool _done;
        private int _diagMilestone;
        private volatile bool _disposed;
        private Thread? _encode;

        /// <summary>诊断：读到整曲末尾补"合法静音"帧累计。</summary>
        public long PrefillFrames { get; private set; }

        /// <summary>当前已读 DoP 帧序号（进度显示用；UI 线程调用，持锁读）。</summary>
        public long FramesRead
        {
            get { lock (_lock) { return _framesRead; } }
        }

        /// <summary>DoP 容器帧总数。</summary>
        public long TotalFrames => _totalFrames;

        public DoP24LeSource(DsdBitstream src)
        {
            _src = src ?? throw new ArgumentNullException(nameof(src));
            _frameRate = src.DopRateHz;
            _totalFrames = src.Channels > 0 ? src.TotalSamples / 16 : 0; // 每 DoP 帧=16 1-bit/声道

            // 环容量取 BpF 整数倍，保证帧不在环内被截断（跨回绕点的帧由两段拷贝处理）
            long ringWanted = Math.Max(RingBytesWanted, (long)PrebufferSeconds * _frameRate * BpF * 4);
            _ringBytes = (int)((ringWanted + BpF - 1) / BpF * BpF);
            _ring = Marshal.AllocHGlobal(_ringBytes);

            _encode = new Thread(EncodeAll)
            {
                IsBackground = true,
                Name = "DoP24环封装",
                Priority = ThreadPriority.BelowNormal
            };
            _encode.Start();
        }

        public WaveFormat WaveFormat => new WaveFormat(_frameRate, 24, 2);

        public TimeSpan TotalTime
        {
            get { return _frameRate > 0 ? TimeSpan.FromSeconds((double)_totalFrames / _frameRate) : TimeSpan.Zero; }
        }

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState
            => (_framesRead * BpF, _totalFrames * BpF, false);

        public bool NextMounted => false;

        private void EncodeAll()
        {
            var raw = new byte[RawChunk];
            var dopp = new byte[(RawChunk / 4) * BpF]; // 每 4B 原始 → 6B DoP
            try
            {
                while (!_disposed)
                {
                    // 1) Seek 请求 / 环满等待：都在锁内等（读盘与装箱在锁外，不阻塞消费者）
                    lock (_lock)
                    {
                        while (!_disposed)
                        {
                            if (_pendingSeekFrame >= 0)
                            {
                                long target = _pendingSeekFrame;
                                _pendingSeekFrame = -1;
                                try { _src.SeekSample(target * 16); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DsdDopPipeline.cs", caught); }
                                _writePos = 0;
                                _readPos = 0;
                                _encodedFrames = target;
                                _done = false;
                                Monitor.PulseAll(_lock);
                            }

                            long used = _writePos - _readPos;
                            if (used + dopp.Length <= _ringBytes)
                            {
                                break; // 有空间，出锁干活
                            }

                            _producerStalls++;
                            Monitor.Wait(_lock, 5);
                        }

                        if (_disposed)
                        {
                            break;
                        }
                    }

                    // 2) 读源（锁外，FileStream 慢盘不阻塞 render）
                    int got = _src.Read(raw, 0, raw.Length);
                    int whole = got - (got % 4);
                    if (whole <= 0)
                    {
                        break;
                    }

                    // 3) 装箱（锁外纯计算）。帧序号用绝对 _encodedFrames，不随环回绕/Seek 错乱
                    int n = EncodeBlock(raw, whole, dopp, _encodedFrames);

                    // 4) 入环（锁内两段拷贝，跨回绕点自动分段）
                    lock (_lock)
                    {
                        if (_disposed)
                        {
                            break;
                        }

                        if (_pendingSeekFrame >= 0)
                        {
                            // Seek 在读源之后插入：刚读的是旧位置数据，丢弃重读（消 seek 杂音）
                            continue;
                        }

                        RingCopyIn(dopp, 0, _writePos, n);
                        _writePos += n;
                        _encodedFrames += n / BpF;
                        Monitor.PulseAll(_lock);
                    }
                }
            }
            catch (Exception ex)
            {
                try { StartupLog.Write("[DoP24环封装异常] " + ex); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DsdDopPipeline.cs", caught); }
            }
            finally
            {
                lock (_lock)
                {
                    _done = true;
                    Monitor.PulseAll(_lock);
                }
            }
        }

        /// <summary>把 src[0..n) 拷进环内逻辑位置 pos（自动处理回绕两段）。</summary>
        private void RingCopyIn(byte[] src, int srcOff, long pos, int n)
        {
            int off = (int)(pos % _ringBytes);
            int first = Math.Min(n, _ringBytes - off);
            Marshal.Copy(src, srcOff, _ring + off, first);
            if (first < n)
            {
                Marshal.Copy(src, srcOff + first, _ring, n - first);
                _ringWrapped++;
            }
        }

        /// <summary>从环内逻辑位置 pos 拷出 n 字节到 dst（自动处理回绕两段）。</summary>
        private void RingCopyOut(byte[] dst, int dstOff, long pos, int n)
        {
            int off = (int)(pos % _ringBytes);
            int first = Math.Min(n, _ringBytes - off);
            Marshal.Copy(_ring + off, dst, dstOff, first);
            if (first < n)
            {
                Marshal.Copy(_ring, dst, dstOff + first, n - first);
                _ringWrapped++;
            }
        }

        /// <summary>封装 whole 个原始 L,R,L,R… 交织字节为 DoP 容器帧；返回产出字节数。
        /// 位序（DoP 1.1 规范图）：小端 wire = [次字节原样][老字节原样][标记]，无位反转。
        /// <param name="frameIndex">绝对帧序号（marker 奇偶用，不随环回绕/Seek 回退）。</param>
        private static int EncodeBlock(byte[] raw, int whole, byte[] dopp, long frameIndex)
        {
            int fp = 0;
            long fi = frameIndex;
            int frames = whole / 4;
            for (int f = 0; f < frames; f++)
            {
                int i = f * 4;
                byte m = ((fi + f) & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                // L 通道：raw[i]=老 8bit(t0..t7)、raw[i+2]=次 8bit(t8..t15)，均 MSB-first 原样
                dopp[fp++] = raw[i + 2]; // 低字节 = 次 8bit 原样
                dopp[fp++] = raw[i];     // 高字节 = 老 8bit 原样（t0 落在字段 MSB）
                dopp[fp++] = m;          // marker
                // R 通道
                dopp[fp++] = raw[i + 3];
                dopp[fp++] = raw[i + 1];
                dopp[fp++] = m;
            }

            return fp;
        }

        public void WaitForPrefill(TimeSpan timeout)
        {
            long need = (long)(PrebufferSeconds * _frameRate * BpF);
            var deadline = DateTime.UtcNow + timeout;
            lock (_lock)
            {
                while (!_disposed && !_done && DateTime.UtcNow < deadline && _writePos < need)
                {
                    Monitor.Wait(_lock, TimeSpan.FromMilliseconds(5));
                }
            }
        }

        /// <summary>读 DoP 容器帧字节（每 6 字节 = 1 帧）。从非托管环内顺序取；到尾补 0x69 静音。</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int want = count - (count % BpF);
            if (want <= 0)
            {
                return 0;
            }

            int total = 0;
            // 等待封装追进度（环空且后台线程仍未收尾）：等 PulseAll，超时兜底补静音。
            var waitDeadline = Environment.TickCount64 + 1500;
            while (total < want)
            {
                long avail;
                lock (_lock)
                {
                    // 只取"已入环"的字节数（_writePos），而非虚拟总长；生产者环满等待时二者差值被容量封顶
                    avail = _writePos - _readPos;
                }

                if (avail <= 0)
                {
                    if (_done)
                    {
                        // 源已封装完且已读完 → 无更多数据，跳出补尾静音。
                        break;
                    }

                    if (Environment.TickCount64 > waitDeadline)
                    {
                        break;
                    }

                    lock (_lock)
                    {
                        if (!_disposed && !_done && _writePos - _readPos <= 0)
                        {
                            Monitor.Wait(_lock, 10);
                        }
                    }

                    if (_disposed)
                    {
                        break;
                    }

                    continue;
                }

                long take = Math.Min(avail, (long)(want - total));
                take -= take % BpF;
                if (take > 0)
                {
                    lock (_lock)
                    {
                        RingCopyOut(buffer, offset + total, _readPos, (int)take);
                        _readPos += take;
                        Monitor.PulseAll(_lock); // 唤醒可能因环满等待的生产者
                    }

                    _framesRead += take / BpF;
                    total += (int)take;
                }
                else break;
            }

            // 抽样诊断：按【真实进度】打标签 + marker 合法性自检（编码器按构造只产 0x05/0xFA，
            // 抽到其它值说明链路某处坏了 1 字节，大声报）。
            if (total > 0 && _totalFrames > 0 && _diagMilestone < 4)
            {
                int pct = (int)(_framesRead * 100 / _totalFrames);
                if (pct >= (int)(MilestoneFracs[_diagMilestone] * 100))
                {
                    int t = Math.Min(12, total);
                    var sb = new System.Text.StringBuilder(36);
                    int badMarker = 0;
                    byte badValue = 0;
                    for (int i = 0; i < t; i++)
                    {
                        byte bv = buffer[offset + i];
                        sb.Append(bv.ToString("X2"));
                        // 6B 帧(24bit) 的第 3 字节 = marker
                        if (i % BpF == 2 && bv != 0x05 && bv != 0xFA)
                        {
                            badMarker++;
                            badValue = bv;
                        }
                    }

                    StartupLog.Write(string.Format(
                        "[DoP抽样 真实进度{0}%] 位置={1:F1}/{2:F1}s 帧={3} 字节={4}{5}",
                        pct,
                        (double)_framesRead / _frameRate, (double)_totalFrames / _frameRate,
                        _framesRead, sb.ToString(),
                        badMarker > 0
                            ? " ⚠marker非法×" + badMarker + "(如0x" + badValue.ToString("X2") + ")—链路有字节损坏！"
                            : " marker合法"));
                    _diagMilestone++;
                }
            }

            if (total < want)
            {
                lock (_lock)
                {
                    int filled = FillSilenceTo(buffer, offset + total, want - total);
                    PrefillFrames += filled;
                    total = want;
                }
            }

            return total;
        }

        private int FillSilenceTo(byte[] dst, int off, int count)
        {
            int filled = 0;
            for (int i = 0; i + BpF - 1 < count; i += BpF)
            {
                byte m = (_frameIndex & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                dst[off + i] = 0x69;
                dst[off + i + 1] = 0x69;
                dst[off + i + 2] = m;
                _frameIndex++;
                filled++;
            }

            return filled;
        }

        public void Seek(TimeSpan position)
        {
            long frame = (long)Math.Round(position.TotalSeconds * _frameRate);
            frame = Math.Clamp(frame, 0, _totalFrames);
            lock (_lock)
            {
                // 冲刷环 + 通知生产者重定位源（生产者在锁外读盘，不能在锁内直接 seek 源）。
                // 若生产者已 EOF 收线程，没人处理 _pendingSeekFrame → 必须重启生产者。
                bool restart = _done;
                _readPos = 0;
                _writePos = 0;
                _pendingSeekFrame = frame;
                _framesRead = frame;
                _frameIndex = frame;
                if (restart)
                {
                    _done = false;
                    _encode = new Thread(EncodeAll)
                    {
                        IsBackground = true,
                        Name = "DoP24环封装",
                        Priority = ThreadPriority.BelowNormal
                    };
                    _encode.Start();
                }

                Monitor.PulseAll(_lock);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_lock) { Monitor.PulseAll(_lock); }
            try { _encode?.Join(2000); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DsdDopPipeline.cs", caught); }

            // 非托管环必须在锁内释放：所有 Marshal.Copy 都在 _lock 下执行，锁外 free 可能撞 use-after-free。
            lock (_lock)
            {
                if (_ring != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_ring);
                }
            }

            try { _src.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DsdDopPipeline.cs", caught); }
        }

        private static readonly double[] MilestoneFracs = { 0.10, 0.40, 0.70, 0.99 };
    }

    /// <summary>
    /// ASIO DoP 装箱（ECHO write_asio_dop_sample 配方）：NAudio AsioOut 只支持 16/32bit 源，
    /// DoP 24bit 容器必须重铸为 32bit——Int32LSB 右对齐变体：每样本 [lo][hi][marker][0]
    /// （DSD 占低 16bit、标记占 bits 23..16）。绝不经过 Widen24To32Provider（value&lt;&lt;8 会把标记挤到最高字节）。
    /// KA13 无 ASIO 驱动，本路径默认不可达；fail-closed：任何异常即失败，由上层回退 PCM。
    /// ⚠ 若将来接实录 ASIO 设备不认 DSD：换成 payload&lt;&lt;8 左移变体（[0][lo][hi][marker]），以设备日志为准。
    /// </summary>
    internal sealed class AsioDoPProvider : IWaveProvider, IDisposable
    {
        private const int InChunk = 1 << 17;   // 24bit 中间缓冲（128KB，BpF 整数倍）
        private readonly DoP24LeSource _src;
        private readonly byte[] _in;
        private long _silenceFrames;
        private bool _disposed;

        public AsioDoPProvider(DoP24LeSource src)
        {
            _src = src ?? throw new ArgumentNullException(nameof(src));
            WaveFormat = new WaveFormat(src.WaveFormat.SampleRate, 32, 2);
            _in = new byte[InChunk];
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / 8; // 32bit 立体声帧 = 8 字节
            if (frames <= 0)
            {
                return 0;
            }

            int need24 = frames * 6;
            int got24 = _src.Read(_in, 0, need24);
            if (got24 <= 0)
            {
                return 0;
            }

            int availFrames = got24 / 6;
            int fp = 0;
            for (int f = 0; f < frames; f++)
            {
                if (f < availFrames)
                {
                    int i = f * 6;
                    buffer[offset + fp++] = _in[i];
                    buffer[offset + fp++] = _in[i + 1];
                    buffer[offset + fp++] = _in[i + 2];
                    buffer[offset + fp++] = 0;
                    buffer[offset + fp++] = _in[i + 3];
                    buffer[offset + fp++] = _in[i + 4];
                    buffer[offset + fp++] = _in[i + 5];
                    buffer[offset + fp++] = 0;
                }
                else
                {
                    // 源尽（正常不会到这：DoP24LeSource.Read 自带 0x69 补尾）——补合法静音帧防喇叭杂音
                    byte m = ((_silenceFrames++) & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                    buffer[offset + fp++] = 0x69;
                    buffer[offset + fp++] = 0x69;
                    buffer[offset + fp++] = m;
                    buffer[offset + fp++] = 0;
                    buffer[offset + fp++] = 0x69;
                    buffer[offset + fp++] = 0x69;
                    buffer[offset + fp++] = m;
                    buffer[offset + fp++] = 0;
                }
            }

            return frames * 8;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _src.Dispose();
        }
    }
}
