using System;
using System.Threading;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DoP（DSD over PCM）封装源：把 <see cref="IDsDStream"/> 的 1-bit DSD 流
    /// 封装成 176.4k/24bit/2ch（DSD64 基准 ×倍率）PCM 容器帧，供 WASAPI 独占原样直通。
    /// 每容器帧：L/R 各 24bit 小端，低 16bit=DSD 数据，高 8bit=DoP 标记(0x05/0xFA 交替)。
    ///
    /// 卡顿根治（对齐 ECHO NEXT 的 DopRingSource）：
    ///   * 数据读取 + 位反转 + DoP 封装全部在【后台预读线程】完成，并写入【内存环形 FIFO】；
    ///   * 独占 render 线程只从环形缓冲做逐块拷贝，绝不触碰 DSD 源、绝不做磁盘/解码 I/O，
    ///     杜绝磁盘读或封装 CPU 阻塞实时输出导致 underrun/卡顿（根治 DSD 与独占卡顿）。
    ///   * 起播预缓冲：开播前先攒够一段数据（或超时/源尽兜底），避免起播即欠载；
    ///   * 源尽后补合规 DoP 静音帧（0x69 + marker 交替），替代二进制 0 填充，避免尾/切换爆音。
    ///   * seek 用 generation 校验丢弃 seek 前预读的旧块，保证 seek 后 ring 里只有目标位置数据。
    /// </summary>
    internal sealed class DoPWaveSource : IWaveSourceProvider, IDisposable
    {
        private const int RawChunk = 1 << 17;          // 预读线程每块从源读取的原始交织字节（128KB，后台线程）
        private const int RingBytes = 32 << 20;        // 环形缓冲容量 32MB（DSD512≈3.7s；DSD64≈30s）
        private const double PrebufferSeconds = 0.30;  // 起播预缓冲时长（秒）
        private const int PrebufferTimeoutMs = 800;    // 预缓冲最久等待，超时兜底开播（避免卡死）
        private static readonly TimeSpan YieldWait = TimeSpan.FromMilliseconds(2);

        private readonly IDsDStream _src;
        private readonly int _srcChannels;
        private readonly int _frameRate;
        private readonly long _totalFrames;           // DoP 容器帧总数
        private readonly object _lock = new();

        // 环形 FIFO（字节级）
        private readonly byte[] _ring = new byte[RingBytes];
        private int _readPos;
        private int _writePos;
        private int _count;                           // 已在 ring 内、尚未被 render 消费的字节
        private readonly int _prebufferBytes;

        private long _frameIndex;                      // 全局 DoP 帧计数（决定 marker 奇偶与进度；仅 lock 内改）
        private long _framesRead;                      // render 已读出的 DoP 帧数（诊断/进度）
        private long _gen;                             // seek generation：预读块读回的归属代次，seek 时递增

        /// <summary>诊断：因 ring 空而补填的"合法静音"帧累计数（>0 说明预读跟不上/起播冷启动，是潜在的无声卡顿点）。</summary>
        public long PrefillFrames { get; private set; }
        private volatile bool _eof;                    // 源已读尽
        private bool _prebuffering;                    // 起播/seek 后仍在攒预缓冲
        private DateTime _prebufferDeadline;
        private bool _headerLogged;                     // 一次性 DoP 头诊断已输出
        private volatile bool _disposed;

        private Thread? _prefetch;

        public DoPWaveSource(IDsDStream src)
        {
            _src = src ?? throw new ArgumentNullException(nameof(src));
            _srcChannels = src.Channels;
            _frameRate = src.Rate switch
            {
                DsdRate.Dsd128 => 352800,
                DsdRate.Dsd256 => 705600,
                DsdRate.Dsd512 => 1411200,
                _ => 176400, // DSD64
            };
            _totalFrames = _srcChannels > 0 ? src.TotalSamples / 16 : 0; // TotalSamples 已是"每声道 1-bit 样本数"；每 DoP 帧=16 1-bit/声道
            _prebufferBytes = (int)(PrebufferSeconds * _frameRate * 6.0);
            _prebuffering = _prebufferBytes > 0;
            _prebufferDeadline = DateTime.UtcNow.AddMilliseconds(PrebufferTimeoutMs);

            _prefetch = new Thread(PrefetchLoop)
            {
                IsBackground = true,
                Name = "DoPRingPrefetch",
                Priority = ThreadPriority.BelowNormal
            };
            _prefetch.Start();
        }

        public WaveFormat WaveFormat => new WaveFormat(_frameRate, 24, 2);

        public TimeSpan TotalTime
        {
            get
            {
                return _frameRate > 0 ? TimeSpan.FromSeconds((double)_totalFrames / _frameRate) : TimeSpan.Zero;
            }
        }

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState
            => (_framesRead * 6, _totalFrames * 6, false);

        public bool NextMounted => false;

        /// <summary>后台预读线程：持续从 DSD 源读原始交织字节 → 位反转 → 封装 DoP 帧 → 写入环形缓冲。
        /// ring 满时阻塞等 render 消费；seek（generation 变更）时丢弃 seek 前预读的旧块，绝不反向阻塞 render 实时输出。</summary>
        private void PrefetchLoop()
        {
            var raw = new byte[RawChunk];
            var dopp = new byte[(RawChunk / 4) * 6]; // 每 4B 原始 → 6B DoP；封装缓冲按原始块上限预分配
            try
            {
                while (!_disposed)
                {
                    // 等 ring 有足够空间再读一块（把磁盘 I/O 放到后台线程）
                    lock (_lock)
                    {
                        while (!_disposed && RingFree() < dopp.Length)
                        {
                            Monitor.Wait(_lock, YieldWait);
                        }
                    }

                    if (_disposed)
                    {
                        return;
                    }

                    long genAtRead;
                    lock (_lock) { genAtRead = _gen; }
                    int got = _src.Read(raw, 0, raw.Length); // 磁盘 I/O + 源读满（后台线程，非 render）

                    lock (_lock)
                    {
                        if (_gen != genAtRead)
                        {
                            continue; // seek 在本块读取期间/之后发生：丢弃旧数据，重新从新位置读
                        }

                        int whole = got - (got % 4); // 丢弃不成双声道的零星尾字节
                        if (whole <= 0)
                        {
                            _eof = true;
                            Monitor.PulseAll(_lock);
                            // 源已尽：进入休眠等待，直到被 Seek 重置（_eof=false）或 Dispose；
                            // seek 后源文件已定位到新位置，本线程须继续循环补读（而不是 return 退出）。
                            while (!_disposed && _eof)
                            {
                                Monitor.Wait(_lock);
                            }

                            continue;
                        }

                        int n = EncodeBlock(raw, whole, dopp); // 封装在 lock 内，与 seek/_frameIndex 无竞态
                        if (n > 0)
                        {
                            WriteRingInLock(dopp, n);
                        }
                    }
                }
            }
            catch
            {
                // 后台线程异常：标记源尽，结束（render 会转到 0x69 静音兜底）
                lock (_lock)
                {
                    _eof = true;
                    Monitor.PulseAll(_lock);
                }
            }
        }

        /// <summary>把 whole 个原始 L,R,L,R… 交织字节封装为 6 字节/帧的 DoP；返回产出字节数。需在 lock(_lock) 内调用。
        /// 位序：DSF/DFF MSB-first → DoP 容器需 LSB-first → 逐字节位反转（据真机"反转可听/不反转雪花"定稿）。</summary>
        private int EncodeBlock(byte[] raw, int whole, byte[] dopp)
        {
            int fp = 0;
            int frames = whole / 4;
            for (int f = 0; f < frames; f++)
            {
                int i = f * 4;
                byte m = (_frameIndex & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                dopp[fp++] = Rev8[raw[i]];      // L 低字节
                dopp[fp++] = Rev8[raw[i + 2]];  // L 高字节
                dopp[fp++] = m;
                dopp[fp++] = Rev8[raw[i + 1]];  // R 低字节
                dopp[fp++] = Rev8[raw[i + 3]];  // R 高字节
                dopp[fp++] = m;
                _frameIndex++;
            }

            return fp;
        }

        /// <summary>把已封装的 DoP 帧写入环形缓冲。需在 lock(_lock) 内调用。</summary>
        private void WriteRingInLock(byte[] data, int len)
        {
            // 一次性诊断：把实际封装的 DoP 前 6 帧(36 字节)hex 打到日志，用于在真机核对 marker 交替/位序/LR
            //（对照"有没有滋滋"：欠载已被内存化排除，滋滋必来自数据字节或 render 时序）。
            if (!_headerLogged)
            {
                _headerLogged = true;
                int n = Math.Min(36, len);
                var sb = new System.Text.StringBuilder(96);
                for (int i = 0; i < n; i++)
                {
                    sb.Append(data[i].ToString("X2"));
                }
                StartupLog.Write("[DoP头6帧] " + sb.ToString());
            }

            int first = Math.Min(len, RingBytes - _writePos);
            Buffer.BlockCopy(data, 0, _ring, _writePos, first);
            _writePos = (_writePos + first) % RingBytes;
            if (len > first)
            {
                Buffer.BlockCopy(data, first, _ring, _writePos, len - first);
                _writePos = (_writePos + (len - first)) % RingBytes;
            }

            _count += len;
            _prebuffering = !_eof && _count < _prebufferBytes;
            Monitor.PulseAll(_lock);
        }

        private int RingFree() => RingBytes - _count;

        /// <summary>读 DoP 容器帧字节（每 6 字节 = 1 帧 L3+R3）。仅从内存环形缓冲取，不做任何 I/O/封装。
        /// 数据不足时**补合法 DoP 静音帧（0x69 + 相位延续的 marker）填满**，绝不返回"部分帧让 render 层补 0"——
        /// 因为全 0 容器帧（marker=0）在真机 DAC 上会被解成连续雪花电流噪音（对齐 ECHO DopRingSource：先 fillDoPSilence 再覆盖真实数据）。</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int want = count - (count % 6);
            if (want <= 0)
            {
                return 0;
            }

            int total = 0;
            DateTime waitEnd = DateTime.UtcNow.AddMilliseconds(6); // 等待后台预读的小窗口（正常播放预读已就绪，几乎不触发）

            while (total < want)
            {
                lock (_lock)
                {
                    int got = PullFromRing(buffer, offset + total, want - total);
                    if (got > 0)
                    {
                        _framesRead += got / 6;
                        total += got;
                        waitEnd = DateTime.UtcNow.AddMilliseconds(6); // 拿到数据，重置等待窗口
                        continue; // 有数据，直接继续
                    }
                }

                if (_disposed)
                {
                    break;
                }

                // 无数据：先小窗口等后台预读填一点（不忙旋、不永久阻塞 render）
                bool data = false;
                if (DateTime.UtcNow < waitEnd)
                {
                    lock (_lock)
                    {
                        Monitor.Wait(_lock, YieldWait);
                    }

                    lock (_lock)
                    {
                        int g = PullFromRing(buffer, offset + total, want - total);
                        if (g > 0)
                        {
                            _framesRead += g / 6;
                            total += g;
                            data = true;
                        }
                    }
                }

                if (data)
                {
                    continue;
                }

                // 仍无数据（源尽量尽 / 起播冷启动 / seek 后 RING 未补满）：补合法 DoP 静音填满，
                // 让 render 拿满一整块，避免它用 0 兜底（0 是雪花噪音之源）。
                lock (_lock)
                {
                    int prefill = FillSilenceTo(buffer, offset + total, want - total);
                    PrefillFrames += prefill;
                    total = want;
                }
            }

            return total;
        }

        /// <summary>从环形缓冲取出至多 want 字节（6 对齐）。需在 lock(_lock) 内调用。</summary>
        private int PullFromRing(byte[] dst, int off, int want)
        {
            if (_count <= 0)
            {
                return 0;
            }

            int avail = _count - (_count % 6);
            int take = Math.Min(avail, want);
            take -= take % 6;
            if (take <= 0)
            {
                return 0;
            }

            int first = Math.Min(take, RingBytes - _readPos);
            Buffer.BlockCopy(_ring, _readPos, dst, off, first);
            _readPos = (_readPos + first) % RingBytes;
            if (take > first)
            {
                Buffer.BlockCopy(_ring, _readPos, dst, off + first, take - first);
                _readPos = (_readPos + (take - first)) % RingBytes;
            }

            _count -= take;
            Monitor.PulseAll(_lock); // 唤醒预读线程继续填
            return take;
        }

        /// <summary>把 count（6 倍数）字节写成合法 DoP 静音帧（0x69 数据 + marker 交替）。返回补写的帧数（诊断用）。</summary>
        private int FillSilenceTo(byte[] dst, int off, int count)
        {
            int filled = 0;
            for (int i = 0; i + 5 < count; i += 6)
            {
                byte m = (_frameIndex & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                dst[off + i] = 0x69;
                dst[off + i + 1] = 0x69;
                dst[off + i + 2] = m;
                dst[off + i + 3] = 0x69;
                dst[off + i + 4] = 0x69;
                dst[off + i + 5] = m;
                _frameIndex++;
                filled++;
            }

            return filled;
        }

        /// <summary>起播阻塞用：等待后台预读线程把 ring 填够预缓冲（或源尽/超时任一即返回）。
        /// 在 WASAPI Start 前调用，可从根上避免"起播即欠载"——这正是"边解边播受磁盘/解码速度影响"的欠载窗口。
        /// 返回后 render 若仍欠载，会走 Read 的 0x69 静音兜底（无声而非雪花），并写 [DSD诊断] 日志量化。</summary>
        public void WaitForPrefill(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            lock (_lock)
            {
                while (!_disposed && !_eof && DateTime.UtcNow < deadline && _count < _prebufferBytes)
                {
                    Monitor.Wait(_lock, TimeSpan.FromMilliseconds(5)); // 预读线程 WriteRingInLock PulseAll 唤醒
                }
            }
        }

        public void Seek(TimeSpan position)
        {
            long frame = (long)Math.Round(position.TotalSeconds * _frameRate); // Round 避免浮点亚帧截断（落到最近帧）
            frame = Math.Clamp(frame, 0, _totalFrames);

            _src.SeekSample(frame * 16 * _srcChannels); // 底层 _ioLock 串行化，防与预读线程 Read 竞争

            lock (_lock)
            {
                _readPos = 0;
                _writePos = 0;
                _count = 0;
                _eof = false;
                _frameIndex = frame;
                _framesRead = frame;
                _gen++;                       // 使预读线程丢弃 seek 前读取的旧块
                _prebuffering = _prebufferBytes > 0;
                _prebufferDeadline = DateTime.UtcNow.AddMilliseconds(PrebufferTimeoutMs);
                Monitor.PulseAll(_lock);      // 唤醒预读线程从新位置重新填
            }
        }

        // 8-bit 位反转查表（DSF MSB-first → DoP LSB-first；据真机"反转可听、不反转雪花"定稿）
        private static readonly byte[] Rev8 = BuildRevTable();
        private static byte[] BuildRevTable()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                byte b = (byte)i, r = 0;
                for (int k = 0; k < 8; k++)
                {
                    r = (byte)((r << 1) | (b & 1));
                    b >>= 1;
                }

                t[i] = r;
            }

            return t;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_lock)
            {
                _disposed = true;
                Monitor.PulseAll(_lock);
            }

            try { _prefetch?.Join(500); } catch { }
            try { _src.Dispose(); } catch { }
        }
    }
}
