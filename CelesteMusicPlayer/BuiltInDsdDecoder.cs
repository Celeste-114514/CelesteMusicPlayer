using System;
using System.IO;
using System.Text;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 内建 DSF/DFF 解析器：从 DSD 容器直接提取 1-bit DSD 流（不转 PCM）。
    /// 数据统一规范为「L/R 逐字节交织」字节流，供 <see cref="DoPWaveSource"/> 封装。
    /// 支持普通 DSD（DSF 全；DFF 未 DST 压缩）。DFF 的 DST 压缩解码暂不支持（返回不支持说明）。
    /// </summary>
    internal sealed class BuiltInDsdDecoder : IDsDDecoder
    {
        public bool CanDecode(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext is ".dsf" or ".dff";
        }

        public IDsDStream Open(string path) => BuiltInDsdStream.Open(path);
    }

    /// <summary>内建 DSD 1-bit 流（源码在数组或文件，统一输出 L/R 交织字节）。</summary>
    internal sealed class BuiltInDsdStream : IDsDStream
    {
        private readonly FileStream _fs;
        private readonly long _dataBytes;       // DSD 数据总字节
        private readonly DsdRate _rate;
        private readonly int _channels;
        private readonly int _blockSize;        // DSF 块交错粒度（bytes/声道/块）
        private readonly bool _dff;             // true=DFF(L/R 逐字节交织), false=DSF(块交错)
        private long _samplesRead;

        // 整段 DSD 数据预载入内存：云盘同步盘等慢盘上若靠 FileStream 懒读，预读线程读不过播放速率
        // → DoP ring 持续欠载补静音，听感为"滋滋电流/断音/后半段无声"。改为开播前一次性读入内存，
        // 后续 Read/Seek 全部走内存数组，预读线程极速取数，ring 永不欠载（对齐 PCM 整曲内存策略）。
        private readonly byte[] _data;
        private long _dataPos;                  // 当前 DSD 数据偏移（相对 _data[0]）

        private byte[]? _block;                 // DSF 重交织缓冲
        private int _blockPos;
        private readonly object _ioLock = new(); // 串行化读位置/块/seek，防预读线程与 seek 线程并发

        private BuiltInDsdStream(FileStream fs, long dataStart, long dataBytes,
            DsdRate rate, int channels, int blockSize, bool dff)
        {
            _fs = fs;
            _dataBytes = dataBytes;
            _rate = rate;
            _channels = channels;
            _blockSize = blockSize;
            _dff = dff;
            _data = new byte[dataBytes];
            fs.Position = dataStart;
            int got = 0;
            while (got < _data.Length)
            {
                int n = fs.Read(_data, got, _data.Length - got);
                if (n <= 0)
                {
                    break;
                }

                got += n;
            }

            // 已全部读入内存，可关闭底层文件句柄（样本读取不再触碰磁盘）
            try { fs.Dispose(); } catch { }
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
        private static BuiltInDsdStream OpenDff(Stream fs)
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
                            if (comp.Contains("NDSD", StringComparison.Ordinal)
                                && fs.Position < propEnd)
                            {
                                // 跳过 NDST 等字段
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
                "[DFF解析] 频道={0} freq={1} dataBytes={2}", ch, fsFreq, dataBytes));

            // DST 压缩检测：CMPR 内容在数据里若为 NDST 正常；若为压缩则拒绝
            return new BuiltInDsdStream(
                fs as FileStream ?? throw new InvalidDataException("DFF 需文件流"),
                dataStart, dataBytes, RateFromFreq(fsFreq), (int)ch, 1, dff: true);
        }

        private static DsdRate RateFromFreq(uint freq) => freq switch
        {
            >= 22579200 => DsdRate.Dsd512,
            >= 11289600 => DsdRate.Dsd256,
            >= 5644800 => DsdRate.Dsd128,
            _ => DsdRate.Dsd64,
        };

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

        /// <summary>DFF：数据本身就是 L/R 逐字节交织，直接流水读（全从内存 _data）。</summary>
        private int ReadDffDirect(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, _dataBytes - _dataPos);
            if (n <= 0)
            {
                return 0;
            }

            Buffer.BlockCopy(_data, (int)_dataPos, buffer, offset, n);
            _dataPos += n;
            return n;
        }

        /// <summary>DSF：数据按 blockSize/声道 块交错(L块,R块…)，读时重交织成 L,R 逐字节（全从内存 _data）。</summary>
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
                byte l = _block[bi];
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

            int alloc = _blockSize * 2; // L+R（当前按立体声 pair-block；与原有读法一致）
            var block = new byte[alloc];
            int got = (int)Math.Min(alloc, _dataBytes - _dataPos);
            Buffer.BlockCopy(_data, (int)_dataPos, block, 0, got);
            if (got < alloc)
            {
                Array.Clear(block, got, alloc - got); // 尾部不足补零
            }

            _block = block;
            _blockPos = 0;
            _dataPos += alloc; // 前进到下一个 pair-block 起点（不足时超出 _dataBytes，下次返回 false）
            return got > 0;
        }

        public void SeekSample(long sampleIndex)
        {
            lock (_ioLock)
            {
                // DSF data 布局是「每声道分块交错」：data = [L块(blockSize)][R块(blockSize)][L块][R块]...
                // sampleIndex = 每声道 1-bit 样本数。每声道字节偏移 = sampleIndex/8。
                // 旧实现用线性偏移定位到错误声道/相位 → seek 后声道错位（膜状鼓动噪音）。
                // 改按 DSF pair-block 精确换算，并从内存 _data 载入对应块。
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
                long blockDataPos = pair * (long)_blockSize * _channels; // 该 pair-block 在 _data 中的偏移

                // 载入含 off 的这一个 pair-block（L 在前 R 在后，各 blockSize 字节）
                int alloc = _blockSize * _channels;
                var block = new byte[alloc];
                int got = (int)Math.Min(alloc, _dataBytes - blockDataPos);
                if (got > 0)
                {
                    Buffer.BlockCopy(_data, (int)blockDataPos, block, 0, got);
                }

                if (got < alloc)
                {
                    Array.Clear(block, got, alloc - got); // 不足补零（尾部残缺）
                }

                _block = block;
                _blockPos = Math.Min(off, _blockSize);   // 从块内 off 处续读 L,R 交错
                _dataPos = blockDataPos + alloc;         // 指向下一个 pair-block 起点（_data 内偏移）
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

        public void Dispose() => _fs.Dispose();
    }
}
