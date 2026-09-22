using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 内建 DSF/DFF 解析器：从 DSD 容器直接提取 1-bit DSD 流（不转 PCM）。
    /// 数据统一规范为「L/R 逐字节交织」字节流，供 <see cref="DoPWaveSource"/> 封装。
    /// 支持普通 DSD（DSF 全；DFF 未 DST 压缩）以及 DST 压缩的 DFF（进程内移植 ffmpeg dstdec.c 逐帧解回 1-bit DSD，bit-perfect 喂 DoP 直出）。
    /// </summary>
    internal sealed class BuiltInDsdDecoder : IDsDDecoder
    {
        public bool CanDecode(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext is ".dsf" or ".dff";
        }

        public IDsDStream Open(string path) => BuiltInDsdStream.Open(path);

        /// <summary>只读头部取 DSD 位时钟频率（Hz），失败返回 0。见 <see cref="BuiltInDsdStream.ProbeFreqHz"/>。</summary>
        public static uint ProbeFreqHz(string path) => BuiltInDsdStream.ProbeFreqHz(path);
    }

    /// <summary>内建 DSD 1-bit 流（文件分段池化读，统一输出 L/R 交织字节）。</summary>
    internal sealed class BuiltInDsdStream : IDsDStream
    {
        private readonly FileStream _fs;
        private readonly long _dataStart;        // DSD 数据块在文件中的起始偏移
        private readonly long _dataBytes;       // DSD 数据总字节
        private readonly DsdRate _rate;
        private readonly int _channels;
        private readonly int _blockSize;        // DSF 块交错粒度（bytes/声道/块）
        private readonly bool _dff;             // true=DFF(L/R 逐字节交织), false=DSF(块交错)
        private long _samplesRead;

        // 分段池化读（2026-09-22 改）：旧实现开播前把整文件读进 managed byte[] _data
        //（DSD64 曲 49-119MB，全在 LOH）——与 DoP 旧整曲数组一起构成单曲 DSD heap ~156MB，
        // = gen2/LOH 回收 STW 冻渲染线程 = 卡顿根因。
        // 改为：FileStream 保持打开 + ArrayPool<byte> 租 1MB 分段（容量/偏移按 pair-block
        // stride 对齐 → pair-block 永不跨段，无需跨段拼接），Seek 换段重定位；
        // 内存定额 ~1MB 与曲长无关，pool 缓冲不碰 GC 堆。
        private byte[]? _seg;                    // ArrayPool 租借的分段缓冲（可能略大于 _segBytes）
        private readonly int _segBytes;          // 分段容量（_stride 整数倍）
        private long _segStart = -1;             // _seg[0] 对应的数据偏移（相对数据起点）
        private int _segLen;                     // 分段内有效字节
        private int _segPos;                     // 分段内读位置
        private long _dataPos;                  // 当前 DSD 数据偏移（相对数据起点）
        private readonly int _stride;            // pair-block 步长（=blockSize*2，立体声对；与既有读法一致）

        private byte[]? _block;                 // DSF 重交织缓冲（复用，不再每块 new）
        private int _blockPos;
        private readonly object _ioLock = new(); // 串行化读位置/块/seek，防预读线程与 seek 线程并发

        private BuiltInDsdStream(FileStream fs, long dataStart, long dataBytes,
            DsdRate rate, int channels, int blockSize, bool dff)
        {
            _fs = fs;
            _dataStart = dataStart;
            _dataBytes = dataBytes;
            _rate = rate;
            _channels = channels;
            _blockSize = blockSize;
            _dff = dff;

            // 分段容量：1MB 向上取齐到 stride 倍数（pair-block 不跨段的前提）。
            // stride = 一个完整交错块的字节数（blockSize × 声道数，立体声=2×blockSize）；
            // 多声道时按 2×blockSize 取齐会算错，块会跨段，必须按真实声道数。
            _stride = Math.Max(2, blockSize * Math.Max(2, _channels));
            int seg = 1 << 20;
            int r = seg % _stride;
            if (r != 0)
            {
                seg += _stride - r;
            }

            _segBytes = (int)Math.Min(seg, Math.Max(_stride, ((dataBytes + _stride - 1) / _stride) * _stride));
            // 不再整文件预读：文件句柄保持打开，Read/Seek 按需分段读（见 _seg 注释）
            StartupLog.Write(string.Format(
                "[DSD流] 分段池化读 dataBytes={0} 分段={1}B stride={2}（内存定额，不再整读）",
                dataBytes, _segBytes, _stride));
        }

        public DsdRate Rate => _rate;
        public int Channels => _channels;

        public long TotalSamples
        {
            get
            {
                // 每字节 = 8 个 1-bit 样本（跨声道）。按单声道 1-bit 样本数计：
                // DSD 总样本 = dataBytes * 8 / channels
                return _channels > 0 ? _dataBytes * 8 / _channels : 0;
            }
        }

        public long SamplesRead => _samplesRead;

        public static IDsDStream Open(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                return ext == ".dsf" ? OpenDsf(fs) : OpenDff(fs);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        // ---------- DSF ----------
        private static BuiltInDsdStream OpenDsf(Stream fs)
        {
            if (ReadTag(fs, 4) != "DSD ")
            {
                throw new InvalidDataException("不是有效的 DSF 文件（缺 DSD 头）。");
            }

            long hdrSize = ReadI64(fs);
            fs.Position += 16; // 跳过 fileTotalSize(8) + metadataPtr(8)

            // fmt 块
            if (ReadTag(fs, 4) != "fmt ")
            {
                throw new InvalidDataException("DSF 缺 fmt 块。");
            }

            ReadI64(fs); // fmt size
            fs.Position += 4 + 4; // version + format id
            uint chType = ReadU32(fs); // channel type(0=stereo)
            uint ch = ReadU32(fs);
            uint freq = ReadU32(fs);   // e.g. 2822400
            ReadU32(fs);               // bits per sample(=1)
            ulong sampleCount = ReadU64(fs);
            uint blockSize = ReadU32(fs); // e.g. 4096
            fs.Position += 4;          // reserved

            // data 块
            while (ReadTag(fs, 4) == "data")
            {
                long size = ReadI64(fs);
                long start = fs.Position;
                long avail = Math.Min(size, fs.Length - start);
                DsdRate rate = RateFromFreq(freq);
                // 诊断：打印每个 DSF 的结构参数，便于对比"正常专辑 vs 有滋滋噪音专辑"的差异（blockSize/声道数/采样率/数据字节）
                StartupLog.Write(string.Format(
                    "[DSF解析] 频道={0} blockSize={1} freq={2} sampleCount={3} dataBytes={4} size={5}",
                    ch, blockSize, freq, sampleCount, avail, size));
                return new BuiltInDsdStream(
                    fs as FileStream ?? throw new InvalidDataException("DSF 需文件流"),
                    start, Math.Max(0, avail), rate, (int)ch, (int)blockSize, dff: false);
            }

            throw new InvalidDataException("DSF 缺 data 块。");
        }

        // ---------- DFF ----------
        private static IDsDStream OpenDff(Stream fs)
        {
            if (ReadTag(fs, 4) != "FRM8")
            {
                throw new InvalidDataException("不是有效的 DFF 文件（缺 FRM8）。");
            }

            ReadI64(fs); // FRM8 size
            string type = ReadTag(fs, 4);
            if (type != "DSD ")
            {
                throw new InvalidDataException("DFF 类型非 DSD。");
            }

            uint fsFreq = 2822400;
            uint ch = 2;
            long dataStart = 0;
            long dataBytes = 0;
            bool sawData = false;
            bool isDst = false;
            long dstTableStart = 0, dstTableBytes = 0;

            while (fs.Position + 12 <= fs.Length)
            {
                string id = ReadTag(fs, 4);
                long size = ReadI64(fs);

                if (id == "PROP")
                {
                    long propEnd = fs.Position + size;
                    ReadTag(fs, 4); // "SND "
                    while (fs.Position + 8 <= propEnd && fs.Position < fs.Length)
                    {
                        string sid = ReadTag(fs, 4);
                        long ssize = ReadI64(fs);
                        if (sid == "FS  ")
                        {
                            fsFreq = ReadU32(fs);
                        }
                        else if (sid == "CHNL")
                        {
                            ch = ReadU32(fs);
                        }
                        else if (sid == "CMPR")
                        {
                            string comp = Encoding.ASCII.GetString(ReadN(fs, 4));
                            if (comp.Contains("NDST", StringComparison.Ordinal))
                            {
                                // DST（Digital Stream Transfer）压缩：走进程内解码
                                isDst = true;
                            }

                            if (comp.Contains("NDSD", StringComparison.Ordinal)
                                && fs.Position < propEnd)
                            {
                                // 跳过 NDSD 等字段
                                fs.Position = propEnd;
                            }
                            else
                            {
                                fs.Position = Math.Min(fs.Position + ssize - 4, fs.Length);
                            }
                        }
                        else
                        {
                            fs.Position = Math.Min(fs.Position + ssize, fs.Length);
                        }
                    }
                }
                else if (id == "DSD ")
                {
                    dataStart = fs.Position;
                    dataBytes = Math.Max(0, Math.Min(size, fs.Length - fs.Position));
                    sawData = true;
                    fs.Position = Math.Min(fs.Position + size, fs.Length);
                }
                else if (id == "DST ")
                {
                    // DST 帧表块：NumFrames(4B) + FrameSize[NumFrames](4B 各)
                    dstTableStart = fs.Position;
                    dstTableBytes = size;
                    fs.Position = Math.Min(fs.Position + size, fs.Length);
                }
                else
                {
                    fs.Position = Math.Min(fs.Position + size, fs.Length);
                }
            }

            if (!sawData)
            {
                throw new InvalidDataException("DFF 缺 DSD 数据块。");
            }

            // 诊断：打印每个 DFF 的结构参数
            StartupLog.Write(string.Format(
                "[DFF解析] 频道={0} freq={1} dataBytes={2} isDst={3}", ch, fsFreq, dataBytes, isDst));

            // DST 压缩 DFF：进程内逐帧解码为原始 1-bit DSD（bit-perfect），直接喂 DoP 原生直出。
            // 内置 ffmpeg.exe 缺 dsdiff 解封装器，无法走转码路径，故在进程内移植 ffmpeg dstdec.c 解码。
            if (isDst)
            {
                if (dstTableBytes <= 0 || dataBytes <= 0)
                {
                    throw new InvalidDataException("DST 压缩 DFF 结构不完整（缺少帧表或数据块）。");
                }

                ReadDstFrameTable(fs, dstTableStart, dstTableBytes, out int numFrames, out int[] frameSizes);
                var compressed = new byte[dataBytes];
                fs.Position = dataStart;
                int got = 0;
                while (got < compressed.Length)
                {
                    int n = fs.Read(compressed, got, compressed.Length - got);
                    if (n <= 0)
                    {
                        break;
                    }

                    got += n;
                }

                long spp = 588L * (fsFreq * 8L / 44100L);
                long bytesPerFrame = spp / 8 * ch;

                try { fs.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("BuiltInDsdDecoder.cs", caught); }
                StartupLog.Write(string.Format(
                    "[DFF-DST] 频道={0} freq={1} 帧数={2} 压缩字节={3} 解码字节={4}",
                    ch, fsFreq, numFrames, compressed.Length, (long)numFrames * bytesPerFrame));
                return DstDsdStream.Create(compressed, frameSizes, fsFreq, (int)ch);
            }

            // DST 压缩检测：CMPR 内容在数据里若为 NDST 正常；若为压缩则拒绝
            return new BuiltInDsdStream(
                fs as FileStream ?? throw new InvalidDataException("DFF 需文件流"),
                dataStart, dataBytes, RateFromFreq(fsFreq), (int)ch, 1, dff: true);
        }

        /// <summary>读取 DST 帧表块（NumFrames + 每帧压缩字节数），与现有 ReadU32 保持同字节序（小端，与文件其余字段一致）。</summary>
        private static void ReadDstFrameTable(Stream fs, long tableStart, long tableBytes, out int numFrames, out int[] frameSizes)
        {
            fs.Position = tableStart;
            numFrames = (int)ReadU32(fs);
            if (numFrames <= 0 || numFrames > 1000000)
            {
                throw new InvalidDataException("DST 帧数异常：" + numFrames);
            }

            frameSizes = new int[numFrames];
            for (int i = 0; i < numFrames; i++)
            {
                frameSizes[i] = (int)ReadU32(fs);
            }
        }

        private static DsdRate RateFromFreq(uint freq) => freq switch
        {
            >= 22579200 => DsdRate.Dsd512,
            >= 11289600 => DsdRate.Dsd256,
            >= 5644800 => DsdRate.Dsd128,
            _ => DsdRate.Dsd64,
        };

        /// <summary>只读文件头部取 DSD 位时钟频率（Hz，如 2822400 / 5644800），失败返回 0。
        /// 用于「按 DSD 级别选 PCM 回退采样率」：不解码、不载入数据，开销可忽略。</summary>
        public static uint ProbeFreqHz(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return 0;
                }

                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                FileStream fs = File.OpenRead(path);
                try
                {
                    if (ext == ".dsf")
                    {
                        if (ReadTag(fs, 4) != "DSD ")
                        {
                            return 0;
                        }

                        ReadI64(fs);          // header size
                        fs.Position += 16;    // fileTotalSize(8) + metadataPtr(8)
                        if (ReadTag(fs, 4) != "fmt ")
                        {
                            return 0;
                        }

                        ReadI64(fs);          // fmt chunk size
                        fs.Position += 8;     // version(4) + format id(4)
                        ReadU32(fs);          // channel type
                        ReadU32(fs);          // channel count
                        return ReadU32(fs);   // sampling frequency，如 2822400
                    }

                    if (ext == ".dff")
                    {
                        if (ReadTag(fs, 4) != "FRM8")
                        {
                            return 0;
                        }

                        ReadI64(fs);          // FRM8 size
                        if (ReadTag(fs, 4) != "DSD ")
                        {
                            return 0;
                        }

                        while (fs.Position + 12 <= fs.Length)
                        {
                            string id = ReadTag(fs, 4);
                            long size = ReadI64(fs);
                            if (id == "PROP")
                            {
                                long propEnd = fs.Position + size;
                                ReadTag(fs, 4); // "SND "
                                while (fs.Position + 8 <= propEnd && fs.Position < fs.Length)
                                {
                                    string sid = ReadTag(fs, 4);
                                    long ssize = ReadI64(fs);
                                    if (sid == "FS  ")
                                    {
                                        return ReadU32(fs);
                                    }

                                    fs.Position = Math.Min(fs.Position + ssize, fs.Length);
                                }

                                return 0;
                            }

                            if (id == "DSD ")
                            {
                                // 数据块到了还没找到 FS，说明没有采样率信息
                                return 0;
                            }

                            fs.Position = Math.Min(fs.Position + size, fs.Length);
                        }
                    }
                }
                finally
                {
                    fs.Dispose();
                }
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("BuiltInDsdDecoder.cs", caught);
            }

            return 0;
        }

        // ---------- 读取（统一 L/R 交织字节） ----------
        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_ioLock)
            {
                int total = 0;
                int pos = offset;
                while (total < count)
                {
                    int n;
                    if (_dff)
                    {
                        n = ReadDffDirect(buffer, pos, count - total);
                    }
                    else
                    {
                        n = ReadDsfInterleaved(buffer, pos, count - total);
                    }

                    if (n <= 0)
                    {
                        break;
                    }

                    total += n;
                    pos += n;
                }

                _samplesRead += (long)total * 8;
                return total;
            }
        }

        /// <summary>确保 [absOffset, ...) 落在当前分段内；不在则换段重读。absOffset 需 stride 对齐（pair-block 不跨段的前提）。</summary>
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
                    global::CelesteMusicPlayer.StartupLog.WriteException("BuiltInDsdDecoder.cs", caught);
                    return false;
                }

                _segLen = _fs.Read(_seg, 0, _segBytes);
                _segStart = absOffset;
            }

            _segPos = (int)(absOffset - _segStart);
            return _segLen > 0 && _segPos < _segLen;
        }

        /// <summary>DFF：数据本身就是 L/R 逐字节交织，直接流水读（从分段缓冲）。</summary>
        private int ReadDffDirect(byte[] buffer, int offset, int count)
        {
            long remain = _dataBytes - _dataPos;
            if (remain <= 0)
            {
                return 0;
            }

            if (!EnsureSegment(_dataPos))
            {
                return 0;
            }

            int avail = _segLen - _segPos;
            if (avail <= 0)
            {
                return 0;
            }

            int take = (int)Math.Min(count, Math.Min(remain, avail));
            if (take <= 0)
            {
                return 0;
            }

            Buffer.BlockCopy(_seg!, _segPos, buffer, offset, take);
            _segPos += take;
            _dataPos += take;
            return take;
        }

        /// <summary>DSF：数据按 blockSize/声道 块交错(L块,R块…)，读时重交织成 L,R 逐字节（从分段缓冲）。</summary>
        private int ReadDsfInterleaved(byte[] buffer, int offset, int count)
        {
            if (_block == null)
            {
                if (!LoadNextDsfBlock())
                {
                    return 0;
                }
            }

            int produced = 0;
            // buffer 需装「交错对」：每 (L,R) 占 2 字节
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

            // pair-block 不跨段（段容量与 _dataPos 均为 stride 倍数），直接从段内拷
            int alloc = _blockSize * _channels; // 完整交错块（立体声=L+R；与 stride 定义一致）
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
            _dataPos += alloc; // 前进到下一个 pair-block 起点（不足时超出 _dataBytes，下次返回 false）
            _segPos += got;
            return got > 0;
        }

        public void SeekSample(long sampleIndex)
        {
            lock (_ioLock)
            {
                // DSF data 布局是「每声道分块交错」：data = [L块(blockSize)][R块(blockSize)][L块][R块]...
                // sampleIndex = 每声道 1-bit 样本数。每声道字节偏移 = sampleIndex/8。
                // 旧实现用线性偏移定位到错误声道/相位 → seek 后声道错位（膜状鼓动噪音）。
                // 改按 DSF pair-block 精确换算，并从分段缓冲载入对应块（见 EnsureSegment）。
                if (_channels <= 0 || _blockSize <= 0 || _dff)
                {
                    // DFF 是线性 L,R 逐字节交织，旧线性偏移正确；DSF 用块交错公式
                    long byteOff = _channels > 0 ? sampleIndex / 8 / _channels : 0;
                    _dataPos = Math.Min(byteOff, _dataBytes);
                    _block = null;
                    _samplesRead = sampleIndex;
                    return;
                }

                long chByte = sampleIndex / 8;                 // 每声道字节偏移（含全部 pair-block 累计）
                long pair = chByte / _blockSize;               // 目标所在 L/R pair-block 序号
                int off = (int)(chByte % _blockSize);          // 块内字节位置（0..blockSize-1）
                long blockDataPos = pair * (long)_blockSize * _channels; // 该 pair-block 在数据中的偏移

                // 载入含 off 的这一个 pair-block（L 在前 R 在后，各 blockSize 字节）
                // 从分段缓冲取（不再引用已删除的整读 _data）：blockDataPos 为 stride 倍数，
                // pair-block 必在单段内，EnsureSegment 换段后直接从 _seg[_segPos] 拷。
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
                        Array.Clear(_block, got, alloc - got); // 尾部不足补零
                    }

                    _blockPos = Math.Min(off, _blockSize);   // 从块内 off 处续读 L,R 交错
                    _dataPos = blockDataPos + alloc;         // 指向下一个 pair-block 起点
                    _segPos += got;
                }
                else
                {
                    Array.Clear(_block, 0, alloc);           // seek 越界：空块静默
                    _blockPos = Math.Min(off, _blockSize);
                }

                _samplesRead = sampleIndex;
            }
        }

        // ---------- 字节工具 ----------
        private static string ReadTag(Stream s, int n)
        {
            byte[] b = ReadN(s, n);
            return Encoding.ASCII.GetString(b, 0, n);
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

        private static long ReadI64(Stream s)
        {
            byte[] b = ReadN(s, 8);
            return BitConverter.ToInt64(b, 0);
        }

        private static ulong ReadU64(Stream s)
        {
            byte[] b = ReadN(s, 8);
            return BitConverter.ToUInt64(b, 0);
        }

        private static uint ReadU32(Stream s)
        {
            byte[] b = ReadN(s, 4);
            return BitConverter.ToUInt32(b, 0);
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
    }
}
