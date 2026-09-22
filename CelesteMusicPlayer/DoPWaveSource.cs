using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DoP（DSD over PCM）封装源：把 <see cref="IDsDStream"/> 的 1-bit DSD 流
    /// 封装成 176.4k 基准 ×倍率的 PCM 容器帧，供 WASAPI 独占原样直通。
    /// 每容器帧：L/R 各 (bits/8) 字节小端；低 16bit=DSD 数据，第 3 字节=DoP 标记(0x05/0xFA 交替)，
    /// 24bit 时帧=6B；32bit 时帧=8B（第 4 字节=0 高 8 位填充，部分 DAC/KA13 认同 32bit DoP 容器）。
    ///
    /// 位序（DoP 1.1 规范图 USB_DSDviaPCM_1v0.jpg 实证）：24bit 帧 MSB→LSB = [标记 8bit][t₀][t₁]…[t₁₅]，
    /// 最老比特 t₀ 在 16bit 字段的 MSB（bit15）。小端 wire 布局 = [次 8bit 原样][老 8bit 原样][标记]，
    /// **零位反转**——DSF 本就是 MSB-first，规范只要求两字节交换位置，不做任何逐位反转。
    /// （2026-09-22 修：旧代码 Rev8+交换使 DAC 解出的流 = 每 16bit 组时间反转，实测音频误差 +3.1dB
    /// ＞信号本身 = DSD 滋滋根因；规范图+音频域判决双证据见 .workbuddy/tmp/dop_verdict3.py）
    ///
    /// 32MB 非托管环形缓冲（SPSC）：后台线程"读源→装箱→入环"（环满则等待，天然限流读盘），
    /// render 只从环内顺序取、绝不欠载（环 ≈ DSD64 30s 定额）；源尽补 0x69 静音；Seek 冲刷环并重定位。
    /// </summary>
    internal sealed class DoPWaveSource : IWaveSourceProvider, IDisposable
    {
        private const int RawChunk = 1 << 17;          // 后台线程每块从源读取的原始交织字节（128KB）
        private const double PrebufferSeconds = 0.10;
        private const int PrebufferTimeoutMs = 3000;

        private readonly IDsDStream _src;
        private readonly int _srcChannels;
        private readonly int _frameRate;
        private readonly long _totalFrames;           // DoP 容器帧总数
        private readonly int _bits;                    // 24 或 32（DoP 容器位深）
        private readonly int _bpF;                     // 每帧字节 = (bits/8)*2
        private readonly object _lock = new();

        // 非托管环形缓冲（SPSC：EncodeAll=唯一生产者，Read=唯一消费者）。
        // 2026-09-22 改：旧实现把整曲封装进managed byte[] _all（DSD64 70s 曲=74MB、169s=179MB，全在 LOH），
        // 叠加 BuiltInDsdStream 整文件读（49-119MB）= 单曲 DSD heap ~156MB → gen2/LOH 回收 STW 冻渲染线程
        // = 卡顿根因。改环形后内存与曲长无关：DSD64 ~30s / DSD512 ~2.8s 的定额缓冲，
        // AllocHGlobal 不在 GC 堆上，memcpy 由 Marshaling 完成，零 LOH 压力。
        private readonly IntPtr _ring;                 // 非托管环形缓冲
        private readonly int _ringBytes;               // 容量（_bpF 的整数倍）
        private long _writePos;                        // 生产者逻辑写位（单调递增，允许超过容量）
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

        /// <summary>环形容量（字节）。32MB：DSD64≈30s / DSD512≈2.8s 定额，与曲长无关。</summary>
        private const int RingBytesWanted = 32 << 20;

        public DoPWaveSource(IDsDStream src, int bits = 24)
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
            _bits = bits == 32 ? 32 : 24;
            _bpF = (_bits / 8) * 2; // 6 (24bit) 或 8 (32bit)
            _totalFrames = _srcChannels > 0 ? src.TotalSamples / 16 : 0; // 每 DoP 帧=16 1-bit/声道

            // 环容量取 _bpF 整数倍，保证帧不在环内被截断（跨回绕点的帧由两段拷贝处理）
            long ringWanted = Math.Max(RingBytesWanted, (long)PrebufferSeconds * _frameRate * _bpF * 4);
            _ringBytes = (int)((ringWanted + _bpF - 1) / _bpF * _bpF);
            _ring = Marshal.AllocHGlobal(_ringBytes);

            _encode = new Thread(EncodeAll)
            {
                IsBackground = true,
                Name = "DoP环封装",
                Priority = ThreadPriority.BelowNormal
            };
            _encode.Start();
        }

        public WaveFormat WaveFormat => new WaveFormat(_frameRate, _bits, 2);

        public TimeSpan TotalTime
        {
            get { return _frameRate > 0 ? TimeSpan.FromSeconds((double)_totalFrames / _frameRate) : TimeSpan.Zero; }
        }

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState
            => (_framesRead * _bpF, _totalFrames * _bpF, false);

        public bool NextMounted => false;

        private void EncodeAll()
        {
            var raw = new byte[RawChunk];
            var dopp = new byte[(RawChunk / 4) * _bpF]; // 每 4B 原始 → _bpF 字节 DoP
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
                                try { _src.SeekSample(target * 16); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DoPWaveSource.cs", caught); }
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
                        _encodedFrames += n / _bpF;
                        Monitor.PulseAll(_lock);
                    }
                }
            }
            catch (Exception ex)
            {
                try { StartupLog.Write("[DoP环封装异常] " + ex); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DoPWaveSource.cs", caught); }
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
        /// 位序（DoP 1.1 规范图）：24bit 帧 MSB→LSB = [标记][t₀…t₁₅]，t₀=最老比特在 16bit 字段 MSB；
        /// 小端 wire = [次字节原样][老字节原样][标记]，无位反转。
        /// <param name="frameIndex">绝对帧序号（marker 奇偶用，不随环回绕/Seek 回退）。</param>
        private int EncodeBlock(byte[] raw, int whole, byte[] dopp, long frameIndex)
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
                if (_bpF == 8) dopp[fp++] = 0;  // 32bit 高 8 位
                // R 通道
                dopp[fp++] = raw[i + 3];
                dopp[fp++] = raw[i + 1];
                dopp[fp++] = m;
                if (_bpF == 8) dopp[fp++] = 0;
            }

            return fp;
        }

        public void WaitForPrefill(TimeSpan timeout)
        {
            long need = (long)(PrebufferSeconds * _frameRate * _bpF);
            var deadline = DateTime.UtcNow + timeout;
            lock (_lock)
            {
                while (!_disposed && !_done && DateTime.UtcNow < deadline && _writePos < need)
                {
                    Monitor.Wait(_lock, TimeSpan.FromMilliseconds(5));
                }
            }
        }

        /// <summary>读 DoP 容器帧字节（每 _bpF 字节 = 1 帧）。从非托管环内顺序取；到尾补 0x69 静音。</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int want = count - (count % _bpF);
            if (want <= 0)
            {
                return 0;
            }

            int total = 0;
            // 等待封装追进度（环空且后台线程仍未收尾）：等 PulseAll（每次入环后会唤醒），超时兜底补静音。
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

                    // 封装线程尚在填充：等待其 PulseAll 追进度，超时兜底后补静音。
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
                take -= take % _bpF;
                if (take > 0)
                {
                    lock (_lock)
                    {
                        RingCopyOut(buffer, offset + total, _readPos, (int)take);
                        _readPos += take;
                        Monitor.PulseAll(_lock); // 唤醒可能因环满等待的生产者
                    }

                    _framesRead += take / _bpF;
                    total += (int)take;
                }
                else break;
            }

            // 抽样诊断：按【真实进度】打标签（旧代码用名义 milestone 当标签，遇读游标突发领先时
            // 会把 47% 标成 10%，2026-09-22 修）；同时做 marker 合法性自检——编码器按构造只产
            // 0x05/0xFA，抽到其它值（如 21:40 会话出现的 0xAA）说明链路某处坏了 1 字节，大声报。
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
                        // 6B 帧(24bit)/8B 帧(32bit) 的第 3 字节 = marker
                        if (i % _bpF == 2 && bv != 0x05 && bv != 0xFA)
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
            for (int i = 0; i + _bpF - 1 < count; i += _bpF)
            {
                byte m = (_frameIndex & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                dst[off + i] = 0x69;
                dst[off + i + 1] = 0x69;
                dst[off + i + 2] = m;
                if (_bpF == 8) dst[off + i + 3] = 0;
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
                // 注意：若生产者已EOF收线程，没人处理 _pendingSeekFrame → 必须重启生产者。
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
                        Name = "DoP环封装",
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
            try { _encode?.Join(2000); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DoPWaveSource.cs", caught); }

            // 非托管环必须在锁内释放：所有 Marshal.Copy（RingCopyIn/Out）都在 _lock 下执行，
            // 锁外 free 可能与消费端在途拷贝撞成 use-after-free。
            lock (_lock)
            {
                if (_ring != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_ring);
                }
            }

            try { _src.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DoPWaveSource.cs", caught); }
        }

        private static readonly double[] MilestoneFracs = { 0.10, 0.40, 0.70, 0.99 };
    }
}
