using System;
using System.Threading;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DoP（DSD over PCM）封装源：把 <see cref="IDsDStream"/> 的 1-bit DSD 流
    /// 封装成 176.4k/24bit/2ch（DSD64 基准 ×倍率）PCM 容器帧，供 WASAPI 独占原样直通。
    /// 每容器帧：L/R 各 24bit小端，低 16bit=DSD 数据，高 8bit=DoP 标记(0x05/0xFA 交替)。
    /// 数据读取放在**后台预读线程**，render 线程只在内存缓冲取帧，杜绝磁盘/文件 IO 阻塞实时输出造成卡顿。
    /// </summary>
    internal sealed class DoPWaveSource : IWaveSourceProvider, IDisposable
    {
        private const int ReadChunk = 512 * 1024; // 后台预读块（约 256ms DSD128）

        private readonly IDsDStream _src;
        private readonly int _frameRate;
        private readonly long _totalFrames;
        private readonly object _qLock = new();
        private byte[]? _ready;              // 后台线程已填好的下一块（L,R 交织）
        private bool _eof;                   // 源已读尽
        private byte[] _cur;
        private int _curPos;
        private int _curCount;
        private volatile bool _disposed;
        private Thread? _prefetch;
        private long _frameIndex;

        public DoPWaveSource(IDsDStream src)
        {
            _src = src ?? throw new ArgumentNullException(nameof(src));
            _frameRate = src.Rate switch
            {
                DsdRate.Dsd128 => 352800,
                DsdRate.Dsd256 => 705600,
                DsdRate.Dsd512 => 1411200,
                _ => 176400, // DSD64
            };
            _totalFrames = src.Channels > 0 ? src.TotalSamples / (long)src.Channels / 16 : 0;
            _cur = new byte[ReadChunk];
            _prefetch = new Thread(PrefetchLoop)
            {
                IsBackground = true,
                Name = "DoPPrefetch",
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
            => (_frameIndex * 6, _totalFrames * 6, false);

        public bool NextMounted => false;

        /// <summary>后台预读线程：持续读大块 DSD 源到备用缓冲，render 可取。源尽则置 _eof。</summary>
        private void PrefetchLoop()
        {
            try
            {
                while (!_disposed)
                {
                    byte[] buf = new byte[ReadChunk];
                    int n = _src.Read(buf, 0, buf.Length);
                    if (n <= 0)
                    {
                        lock (_qLock)
                        {
                            _eof = true;
                            Monitor.PulseAll(_qLock);
                        }
                        return;
                    }

                    byte[] chunk = n == buf.Length ? buf : Shrink(buf, n);
                    lock (_qLock)
                    {
                        // 已有待消费的 ready：等消费后继续
                        while (_ready != null && !_disposed)
                        {
                            Monitor.Wait(_qLock);
                        }

                        if (_disposed)
                        {
                            return;
                        }

                        _ready = chunk;
                        Monitor.PulseAll(_qLock);
                    }
                }
            }
            catch
            {
            }
        }

        private static byte[] Shrink(byte[] src, int n)
        {
            var r = new byte[n];
            Buffer.BlockCopy(src, 0, r, 0, n);
            return r;
        }

        /// <summary>读 DoP 容器帧字节（每 6 字节 = 1 帧 L3+R3）。仅从内存缓冲取，不做 IO。</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            int remaining = count - (count % 6);
            int pos = offset;
            while (remaining > 0)
            {
                int produced = EmitFrames(buffer, pos, remaining);
                if (produced <= 0)
                {
                    break; // 源尽
                }

                pos += produced;
                total += produced;
                remaining -= produced;
            }

            return total;
        }

        /// <summary>从当前缓冲取帧；当前块耗尽时换到后台已填好的下一块（无则同步兜底读），并唤醒预读线程。</summary>
        private int EmitFrames(byte[] buffer, int offset, int want)
        {
            if (_curPos >= _curCount)
            {
                lock (_qLock)
                {
                    byte[]? r = _ready;
                    if (r != null)
                    {
                        _ready = null;
                        Monitor.PulseAll(_qLock); // 唤醒预读继续填
                        _cur = r;
                        _curPos = 0;
                        _curCount = r.Length;
                    }
                    else
                    {
                        // 预读尚未跟上：同步读（很少发生，预读通常 512KB 远超前）
                        _curCount = _src.Read(_cur, 0, _cur.Length);
                        _curPos = 0;
                    }
                }

                if (_curCount < 4)
                {
                    _eof = true; // 源尽：转补合规 DoP 静音帧（0x69），避免 render 用 0 填充造成爆音
                }
            }

            int frames = 0;
            while (frames * 6 < want && (_curPos + 4) <= _curCount)
            {
                // DSF/DFF 的 DSD 字节内 bit7 为最早样本(MSB-first)，DoP 规范要求最早样本在容器 bit0(LSB-first) → 逐字节位反转。
                byte l0 = Rev8[_cur[_curPos]];
                byte r0 = Rev8[_cur[_curPos + 1]];
                byte l1 = Rev8[_cur[_curPos + 2]];
                byte r1 = Rev8[_cur[_curPos + 3]];
                _curPos += 4;

                byte marker = (_frameIndex & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                int o = offset + frames * 6;
                buffer[o] = l0;
                buffer[o + 1] = l1;
                buffer[o + 2] = marker;
                buffer[o + 3] = r0;
                buffer[o + 4] = r1;
                buffer[o + 5] = marker;
                _frameIndex++;
                frames++;
            }

            // 源尽后补合规 DoP 静音帧（0x69 数据 + 0x05/0xFA 交替），避免尾/切换时用 0 填充造成爆音/杂音
            if (_eof)
            {
                while (frames * 6 < want)
                {
                    byte marker = (_frameIndex & 1) == 0 ? (byte)0x05 : (byte)0xFA;
                    int o = offset + frames * 6;
                    buffer[o] = 0x69;
                    buffer[o + 1] = 0x69;
                    buffer[o + 2] = marker;
                    buffer[o + 3] = 0x69;
                    buffer[o + 4] = 0x69;
                    buffer[o + 5] = marker;
                    _frameIndex++;
                    frames++;
                }
            }

            return frames * 6;
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

        public void Seek(TimeSpan position)
        {
            long frame = (long)(position.TotalSeconds * _frameRate);
            frame = Math.Clamp(frame, 0, _totalFrames);
            _frameIndex = frame;

            lock (_qLock)
            {
                _src.SeekSample(frame * 16 * _src.Channels);
                _ready = null;
                _curCount = 0;
                _curPos = 0;
                _eof = false;
                Monitor.PulseAll(_qLock);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_qLock)
            {
                Monitor.PulseAll(_qLock);
            }

            try { _prefetch?.Join(500); } catch { }
            try { _src.Dispose(); } catch { }
        }
    }
}
