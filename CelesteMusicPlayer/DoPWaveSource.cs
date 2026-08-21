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
    /// 卡顿根治逻辑：
    ///   * 后台线程把【整首 DSD】一次性读尽并位反转+封装成完整 DoP 字节数组 _all（内存）；
    ///     独占 render 线程只从 _all 顺序 memcpy，绝不做任何磁盘/解码/并发填充。
    ///   * 因整曲已全量在内存，render 永远取得到数据 —— 不存在 ring 欠载/预读跟不上/后半段无声。
    ///   * 起播 WaitForPrefill 等封装进度足够（或源尽/超时兜底）再 Start，避免起播即空。
    ///   * 读到 _all 末尾后补合规 DoP 静音帧（0x69 + marker 交替）收尾，避免用 0 填充爆音。
    /// </summary>
    internal sealed class DoPWaveSource : IWaveSourceProvider, IDisposable
    {
        private const int RawChunk = 1 << 17;          // 后台线程每块从源读取的原始交织字节（128KB）
        private const double PrebufferSeconds = 0.10;  // 起播至少先封装够这么多秒(< 一般起播即整曲完成)
        private const int PrebufferTimeoutMs = 3000;   // 预填充最久等待，超时兜底开播（避免卡死）

        private readonly IDsDStream _src;
        private readonly int _srcChannels;
        private readonly int _frameRate;
        private readonly long _totalFrames;           // DoP 容器帧总数
        private readonly object _lock = new();

        // 完整 DoP 字节（后台线程顺序封装写入，render 顺序读取）
        private readonly byte[] _all;
        private long _allPos;                         // 后台线程已写入的字节数（= 封装进度）
        private long _readPos;                        // render 已读出的偏移

        private long _frameIndex;                      // 全局 DoP 帧计数（决定 marker 奇偶）
        private long _framesRead;                      // render 已读出的 DoP 帧数（诊断/进度）
        private volatile bool _done;                   // 整曲封装完成
        private volatile bool _disposed;
        private Thread? _encode;

        /// <summary>诊断：因读到整曲末尾而补填的"合法静音"帧累计。</summary>
        public long PrefillFrames { get; private set; }

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
            _all = new byte[checked(_totalFrames * 6)];

            _encode = new Thread(EncodeAll)
            {
                IsBackground = true,
                Name = "DoP整曲封装",
                Priority = ThreadPriority.BelowNormal
            };
            _encode.Start();
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

        /// <summary>后台线程：把整首 DSD 顺序读出并位反转+封装为 DoP，写入 _all。</summary>
        private void EncodeAll()
        {
            var raw = new byte[RawChunk];
            var dopp = new byte[(RawChunk / 4) * 6]; // 每 4B 原始 → 6B DoP
            try
            {
                while (!_disposed)
                {
                    int got = _src.Read(raw, 0, raw.Length); // 内存读（整曲已在内存），返回实际读到的原始交织字节
                    int whole = got - (got % 4);             // 丢弃不成双声道的零星尾
                    if (whole <= 0)
                    {
                        break; // 源尽
                    }

                    int n = EncodeBlock(raw, whole, dopp, _allPos);
                    lock (_lock)
                    {
                        if (_allPos + n <= _all.Length)
                        {
                            Buffer.BlockCopy(dopp, 0, _all, (int)_allPos, n);
                            _allPos += n;
                        }
                        else
                        {
                            _allPos = _all.Length; // 数据超出估算：截断
                        }

                        Monitor.PulseAll(_lock);
                    }
                }
            }
            catch (Exception ex)
            {
                try { StartupLog.Write("[DoP整曲封装异常] " + ex); } catch { }
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

        /// <summary>封装 whole 个原始 L,R,L,R… 交织字节为 6 字节/帧 DoP；返回产出字节数。
        /// 位序：DSF MSB-first → DoP 容器 LSB-first → 逐字节位反转（据真机定稿）。</summary>
        private int EncodeBlock(byte[] raw, int whole, byte[] dopp, long startFrame)
        {
            int fp = 0;
            long fi = startFrame / 6; // startFrame 是字节偏移 → 帧序号
            int frames = whole / 4;
            for (int f = 0; f < frames; f++)
            {
                int i = f * 4;
                byte m = ((fi + f) & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                dopp[fp++] = Rev8[raw[i]];      // L 低字节
                dopp[fp++] = Rev8[raw[i + 2]];  // L 高字节
                dopp[fp++] = m;
                dopp[fp++] = Rev8[raw[i + 1]];  // R 低字节
                dopp[fp++] = Rev8[raw[i + 3]];  // R 高字节
                dopp[fp++] = m;
            }

            return fp;
        }

        /// <summary>起播阻塞用：等待后台整曲封装达到至少 prebuffer 字节（或源尽/超时）。</summary>
        public void WaitForPrefill(TimeSpan timeout)
        {
            long need = (long)(PrebufferSeconds * _frameRate * 6.0);
            var deadline = DateTime.UtcNow + timeout;
            lock (_lock)
            {
                while (!_disposed && !_done && DateTime.UtcNow < deadline && _allPos < need)
                {
                    Monitor.Wait(_lock, TimeSpan.FromMilliseconds(5));
                }
            }
        }

        /// <summary>读 DoP 容器帧字节（每 6 字节 = 1 帧）。仅从内存 _all 顺序取；到尾补 0x69 静音。</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int want = count - (count % 6);
            if (want <= 0)
            {
                return 0;
            }

            int total = 0;
            // 从 _all 顺序取（内存，毫秒级）
            while (total < want)
            {
                long avail;
                lock (_lock)
                {
                    avail = _all.Length - _readPos;
                }

                if (avail <= 0) break;
                long take = Math.Min(avail, (long)(want - total));
                take -= take % 6;
                if (take > 0)
                {
                    Buffer.BlockCopy(_all, (int)_readPos, buffer, offset + total, (int)take);
                    lock (_lock) { _readPos += take; }
                    _framesRead += take / 6;
                    total += (int)take;
                }
                else break;
            }

            // 整曲已到尾：补合法 DoP 静音帧填满，避免 render 用 0 兜底（0 是雪花）
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

        public void Seek(TimeSpan position)
        {
            long frame = (long)Math.Round(position.TotalSeconds * _frameRate); // Round 落最近帧
            frame = Math.Clamp(frame, 0, _totalFrames);
            lock (_lock)
            {
                _readPos = Math.Max(0, Math.Min(_all.Length, frame * 6));
                _frameIndex = frame;
                _framesRead = frame;
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
            try { _encode?.Join(2000); } catch { }
            try { _src.Dispose(); } catch { }
        }

        // 8-bit 位反转查表（DSF/DFF MSB-first → DoP LSB-first；据真机"反转可听、不反转雪花"定稿）
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
    }
}
