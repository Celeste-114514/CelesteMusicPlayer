using System;
using System.Buffers;
using System.Diagnostics;
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
        private readonly bool _lsbFirst;         // 字节内位序：true=bit0 为最早采样点（DSF bitsPerSample=1，主流）
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
            uint nativeHz, int channels, int blockSize, bool lsbFirst)
        {
            _fs = fs;
            _dataStart = dataStart;
            _dataBytes = dataBytes;
            _nativeHz = nativeHz;
            _channels = channels;
            _blockSize = blockSize;
            _lsbFirst = lsbFirst;

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
            uint bps = ReadU32(fs);    // bits per sample：1=LSB-first(bit0 最早采样点，主流 DSF)，8=MSB-first
            ReadU64(fs);          // sampleCount
            uint blockSize = ReadU32(fs); // e.g. 4096
            fs.Position += 4;          // reserved：fmt 块体 = 36 字节语义 + 4 字节保留 = 40。
            // 2026-09-23 实机教训：漏跳这 4 字节 → data 标签读成 0x00000000 → 整张 DSD 专辑
            // （DSD REMASTERED 版本）集体误判"缺 data 块"回退 PCM。此布局来自旧代码实证（bd89457 前）。

            // data 块
            (long Start, long Avail)? dataChunk = null;
            if (ReadTag(fs, 4) == "data")
            {
                long size = ReadI64(fs);
                long start = fs.Position;
                dataChunk = (start, Math.Min(size, fs.Length - start));
            }
            else
            {
                // 兜底：畸形 fmt（reserved 变长等未知布局）时，在头部小窗内扫描 data 标签，
                // 防未知块布局再次"整张专辑集体失明"。
                dataChunk = ScanForDataChunk(fs);
            }

            if (dataChunk == null)
            {
                throw new InvalidDataException("DSF 缺 data 块。");
            }

            StartupLog.Write(string.Format(
                "[DSF解析] 频道={0} blockSize={1} freq={2} dataBytes={3} bitsPerSample={4}({5})",
                ch, blockSize, freq, Math.Max(0, dataChunk.Value.Avail),
                bps, bps == 1 ? "LSB-first→装箱前做字节内bit反转" : (bps == 8 ? "MSB-first→原样装箱" : "未知值→按LSB-first处理")));
            return new DsdBitstream(
                fs as FileStream ?? throw new InvalidDataException("DSF 需文件流"),
                dataChunk.Value.Start, Math.Max(0, dataChunk.Value.Avail), freq, (int)ch, (int)blockSize,
                bps != 8); // 仅 bitsPerSample=8 视为 MSB-first；1 及其它值按 LSB-first（主流 DSF）
        }

        /// <summary>fmt 块布局畸形（reserved 变长等）时兜底：在文件头小窗内找 data 块。
        /// 判据：4 字节 "data" 标签 + 8 字节 size&gt;0 且数据不越过文件尾（防巧合命中）。</summary>
        private static (long Start, long Avail)? ScanForDataChunk(Stream fs)
        {
            long scanFrom = 44; // 覆盖"无 reserved"极端变体；巧合命中由 size 合理性校验拦下
            long scanTo = Math.Min(fs.Length - 12, scanFrom + 4096);
            for (long p = scanFrom; p <= scanTo; p++)
            {
                fs.Position = p;
                if (ReadTag(fs, 4) != "data")
                {
                    continue;
                }

                long size = ReadI64(fs);
                long start = fs.Position;
                if (size > 0 && start + size <= fs.Length)
                {
                    StartupLog.Write($"[DSF解析] 畸形 fmt 布局，扫描兜底命中 data 块 @{p} size={size}");
                    return (start, Math.Min(size, fs.Length - start));
                }
            }

            return null;
        }

        public int Channels => _channels;
        public uint NativeRateHz => _nativeHz;

        /// <summary>DSF 字节内位序：true=LSB-first（bitsPerSample=1，bit0 为最早采样点）——DoP 装箱前须做字节内 bit 反转。</summary>
        public bool LsbFirst => _lsbFirst;

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
    /// 位序（DoP 1.1 规范 + DSF 规范联合实证，2026-09-23 bitrev_fix）：
    /// ① DSF 文件字节内 LSB-first（bitsPerSample=1：bit0 为最早采样点）→ 先对每个原始字节做
    ///    字节内 bit 反转，得到 MSB-first 语义（t0 落 bit7）；
    /// ② DoP 规范图：最老 bit 进 16bit 字段 MSB → 小端 wire = [次 8bit（t8..t15）][老 8bit（t0..t7）][标记]；
    /// ③ 标记 0x05/0xFA 按绝对帧序号奇偶交替；尾补 0x69 静音（DoP 约定值，与位序无关）。
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

        private readonly DsdBitstream? _src;           // 预生成旁路模式下为 null（数据直接来自 _prebuilt）
        private FileStream? _prebuilt;                 // 非 null = 预生成缓存旁路：直接从文件顺序读，不经环/装箱线程
        private readonly int _frameRate;               // DoP 容器帧率（= native/16）
        private readonly bool _lsbFirst;               // 跟随源文件位序：true=装箱前做字节内 bit 反转
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

        // 卡顿打点（2026-09-23 夜）：用户报"固定位置卡顿"且第二遍一模一样（排除读盘/缓存干扰），
        // 必须在音频线程之外取证。打点只写定长环形数组（无锁、无 I/O、无日志），
        // 由装箱线程（非实时线程）定期或播放结束时统一落盘——绝不在 render/ASIO 回调线程做磁盘写。
        private const int StallCap = 64;
        private const long StallThresholdUs = 3000; // 3ms：正常 Read 是几十微秒，超阈值即异常
        private readonly long[] _stallUs = new long[StallCap];
        private readonly long[] _stallFrame = new long[StallCap];
        private readonly long[] _stallAvail = new long[StallCap];
        private readonly long[] _stallWaitUs = new long[StallCap];
        private int _stallIdx;
        private int _stallTotal;
        private long _lastStallFlushTicks;

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
            _lsbFirst = src.LsbFirst;
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

        /// <summary>
        /// 预生成缓存旁路：从 DsdPreloadService 生成的 DoP(24bit 紧凑) WAV 直接顺序读，
        /// 完全绕开 DsdBitstream 读源 → 装箱线程 → 32MB 环这条实时流水线。
        /// 下游（render 线程、进度、Seek、WaveFormat、徽标）全部复用，行为与实时装箱一致。
        /// 头损坏/格式不符一律抛异常 → 由上层 fail-closed 回退到实时装箱，绝不静默出坏声。
        /// </summary>
        public static DoP24LeSource FromPrebuilt(string wavPath)
        {
            byte[] h = new byte[44];
            using (var probe = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                int n = probe.Read(h, 0, 44);
                if (n < 44
                    || global::System.Text.Encoding.ASCII.GetString(h, 0, 4) != "RIFF"
                    || global::System.Text.Encoding.ASCII.GetString(h, 8, 4) != "WAVE")
                {
                    throw new InvalidDataException("DSD 预载缓存文件头损坏：" + wavPath);
                }
            }

            int channels = BitConverter.ToUInt16(h, 22);
            int rate = (int)BitConverter.ToUInt32(h, 24);
            int bits = BitConverter.ToUInt16(h, 34);
            uint dataBytes = BitConverter.ToUInt32(h, 40);
            if (bits != 24 || channels != 2 || rate <= 0 || dataBytes < BpF)
            {
                throw new InvalidDataException(
                    $"DSD 预载缓存格式不符（期望 24bit/2ch DoP，实际 {bits}bit/{channels}ch/{rate}Hz）");
            }

            return new DoP24LeSource(wavPath, rate, dataBytes / BpF);
        }

        private DoP24LeSource(string wavPath, int frameRate, long totalFrames)
        {
            _src = null;
            _frameRate = frameRate;
            _lsbFirst = true;               // 旁路不再装箱，位序只影响编码阶段
            _totalFrames = totalFrames;
            _ring = IntPtr.Zero;
            _ringBytes = 0;
            _prebuilt = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.SequentialScan);
            _prebuilt.Position = 44;        // 跳过 44B 标准 PCM 头
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
                    int n = EncodeBlock(raw, whole, dopp, _encodedFrames, _lsbFirst);

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
        /// 两步位序（缺一即滋滋声）：
        /// ① DSF 字节内 LSB-first（bit0=最早采样点）→ <see cref="Rev8Table"/> 做字节内 bit 反转，得到 MSB-first 语义；
        /// ② DoP 规范图：最老 bit 进 16bit 字段 MSB → 小端 wire = [次 8bit（t8..t15）][老 8bit（t0..t7）][标记]。
        /// <param name="frameIndex">绝对帧序号（marker 奇偶用，不随环回绕/Seek 回退）。</param>
        /// <param name="lsbFirst">源位序：true=反转（DSF bitsPerSample=1，主流）；false=MSB-first 原样（bitsPerSample=8）。</param>
        private static int EncodeBlock(byte[] raw, int whole, byte[] dopp, long frameIndex, bool lsbFirst)
        {
            int fp = 0;
            long fi = frameIndex;
            int frames = whole / 4;
            for (int f = 0; f < frames; f++)
            {
                int i = f * 4;
                byte m = ((fi + f) & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                // L 通道：raw[i]=老 8bit(t0..t7)、raw[i+2]=次 8bit(t8..t15)
                byte lOld = lsbFirst ? Rev8Table[raw[i]] : raw[i];
                byte lMid = lsbFirst ? Rev8Table[raw[i + 2]] : raw[i + 2];
                byte rOld = lsbFirst ? Rev8Table[raw[i + 1]] : raw[i + 1];
                byte rMid = lsbFirst ? Rev8Table[raw[i + 3]] : raw[i + 3];
                dopp[fp++] = lMid;    // 低字节 = 次 8bit（t8..t15，t15 落 bit0）
                dopp[fp++] = lOld;    // 高字节 = 老 8bit（t0..t7，t0 落 bit7=16bit 字段 MSB，DoP 规范位）
                dopp[fp++] = m;       // marker
                dopp[fp++] = rMid;
                dopp[fp++] = rOld;
                dopp[fp++] = m;
            }

            return fp;
        }

        /// <summary>字节内 bit 反转查表（LSB-first→MSB-first）。</summary>
        private static readonly byte[] Rev8Table = BuildRev8Table();

        private static byte[] BuildRev8Table()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++)
                {
                    if ((i & (1 << b)) != 0)
                    {
                        v |= 1 << (7 - b);
                    }
                }

                t[i] = (byte)v;
            }

            return t;
        }

        public void WaitForPrefill(TimeSpan timeout)
        {
            if (_prebuilt != null)
            {
                return; // 预生成缓存已在盘上，无需等后台装箱
            }

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

            // 预生成缓存旁路：一次 FileStream 顺序读即可，无环、无装箱线程、无等待
            if (_prebuilt != null)
            {
                int got = _prebuilt.Read(buffer, offset, want);
                got -= got % BpF;
                if (got > 0)
                {
                    _framesRead += got / BpF;
                }

                if (got < want)
                {
                    lock (_lock)
                    {
                        _frameIndex = _framesRead;
                        PrefillFrames += FillSilenceTo(buffer, offset + got, want - got);
                    }

                    return want;
                }

                return got;
            }

            long t0 = Stopwatch.GetTimestamp();
            long waitUs = 0;
            long availAtStart = -1;
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

                if (availAtStart < 0)
                {
                    availAtStart = avail;
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
                            long w0 = Stopwatch.GetTimestamp();
                            Monitor.Wait(_lock, 10);
                            waitUs += (Stopwatch.GetTimestamp() - w0) * 1_000_000 / Stopwatch.Frequency;
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

            if (_prebuilt != null)
            {
                long off = 44 + frame * BpF;
                if (off <= _prebuilt.Length)
                {
                    _prebuilt.Position = off;
                }

                _framesRead = frame;
                _frameIndex = frame;
                return;
            }

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

            try { _prebuilt?.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DsdDopPipeline.cs", caught); }
            try { _src?.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DsdDopPipeline.cs", caught); }
        }

        private static readonly double[] MilestoneFracs = { 0.10, 0.40, 0.70, 0.99 };
    }

    /// <summary>
    /// DoP 32bit 标准容器转运层：把已验证 byte-perfect 的 24bit 紧凑 DoP 帧
    /// [lo][hi][marker] 重铸为 32bit 标准 DoP [0x00][lo][hi][marker]
    /// （即 payload&lt;&lt;8：标记移到 32 位字最高字节）。
    /// 背景（2026-09-23 KA13 实测）：FiiO 自家驱动 fiio_usbaudio.sys 原生认 DSD
    /// （内部 DSD_32b/DSD_64b 标识），标准 DoP 正装=32bit 集装箱+标记最高字节；
    /// 24bit 紧凑能被设备识别（绿灯）但非驱动原生路径=锁不稳→周期性掉锁=可闻"一卡一卡"。
    /// 环/装箱/bit 反转链路一行不动（全流校验已证正确），只在外层重铸，
    /// 设置可随时切回 24bit 紧凑（旧行为零变化）。
    /// </summary>
    internal sealed class DoP32PackedSource : IWaveSourceProvider, IDisposable
    {
        private const int ChunkFrames = 1 << 14; // 每次向内层索取的帧数（24bit 临时缓冲=98304B，有界）
        private readonly DoP24LeSource _src;
        private readonly byte[] _in;

        public DoP32PackedSource(DoP24LeSource src)
        {
            _src = src ?? throw new ArgumentNullException(nameof(src));
            WaveFormat = new WaveFormat(src.WaveFormat.SampleRate, 32, src.WaveFormat.Channels);
            _in = new byte[ChunkFrames * 6];
        }

        public WaveFormat WaveFormat { get; }

        public TimeSpan TotalTime => _src.TotalTime;

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState => _src.ProbeCurrentState;

        public bool NextMounted => _src.NextMounted;

        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / 8; // 32bit 立体声帧 = 8 字节
            if (frames <= 0)
            {
                return 0;
            }

            // 内层 Read 自带"读满或补 0x69 静音"语义；按 ChunkFrames 分块搬运，临时缓冲有界。
            int produced = 0;
            while (produced < frames)
            {
                int take = Math.Min(ChunkFrames, frames - produced);
                int got24 = _src.Read(_in, 0, take * 6);
                if (got24 <= 0)
                {
                    break; // 源尽异常（正常不会：内层补尾已填满）
                }

                int avail = got24 / 6;
                int fp = offset + produced * 8;
                for (int f = 0; f < avail; f++)
                {
                    int i = f * 6;
                    buffer[fp++] = 0x00;
                    buffer[fp++] = _in[i];
                    buffer[fp++] = _in[i + 1];
                    buffer[fp++] = _in[i + 2];
                    buffer[fp++] = 0x00;
                    buffer[fp++] = _in[i + 3];
                    buffer[fp++] = _in[i + 4];
                    buffer[fp++] = _in[i + 5];
                }

                produced += avail;
                if (avail < take)
                {
                    break; // 内层短读（理论不出现）→ 剩余零填充
                }
            }

            // 兜底：未产满的帧零填充保帧对齐（正常不会到这）
            for (int f = produced; f < frames; f++)
            {
                int fp = offset + f * 8;
                buffer[fp] = 0x00;
                buffer[fp + 1] = 0x00;
                buffer[fp + 2] = 0x00;
                buffer[fp + 3] = 0x00;
                buffer[fp + 4] = 0x00;
                buffer[fp + 5] = 0x00;
                buffer[fp + 6] = 0x00;
                buffer[fp + 7] = 0x00;
            }

            return frames * 8;
        }

        public void Seek(TimeSpan position) => _src.Seek(position);

        public void Dispose()
        {
            try { _src.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DsdDopPipeline.cs", caught); }
        }
    }

    /// <summary>
    /// ASIO DoP 装箱（ECHO write_asio_dop_sample 配方）：NAudio AsioOut 只支持 16/32bit 源，
    /// DoP 24bit 容器必须重铸为 32bit——标准 DoP 32 变体：每样本 [0][lo][hi][marker]
    /// （payload&lt;&lt;8，DSD 占低 16bit、标记占最高字节 bits 31..24）。
    /// 2026-09-23 KA13 实测修正：旧摆位 [lo][hi][marker][0]（标记 bits 23..16）FiiO 驱动不认
    /// （黄灯=当 352.8kHz PCM32 直放=静音）；fiio_usbaudio.sys 的 DSD_32b 正装=标记最高字节。
    /// fail-closed：任何异常即失败，由上层回退 PCM。
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
                    buffer[offset + fp++] = 0x00;
                    buffer[offset + fp++] = _in[i];
                    buffer[offset + fp++] = _in[i + 1];
                    buffer[offset + fp++] = _in[i + 2];
                    buffer[offset + fp++] = 0x00;
                    buffer[offset + fp++] = _in[i + 3];
                    buffer[offset + fp++] = _in[i + 4];
                    buffer[offset + fp++] = _in[i + 5];
                }
                else
                {
                    // 源尽（正常不会到这：DoP24LeSource.Read 自带 0x69 补尾）——补合法静音帧防喇叭杂音
                    byte m = ((_silenceFrames++) & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                    buffer[offset + fp++] = 0x69;
                    buffer[offset + fp++] = 0x69;
                    buffer[offset + fp++] = 0x00;
                    buffer[offset + fp++] = m;
                    buffer[offset + fp++] = 0x69;
                    buffer[offset + fp++] = 0x69;
                    buffer[offset + fp++] = 0x00;
                    buffer[offset + fp++] = m;
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
