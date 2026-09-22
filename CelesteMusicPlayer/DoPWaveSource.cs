using System;
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
    /// 整曲封装进内存：render 只从内存顺序 memcpy，绝无欠载/并发填充；源尽补 0x69 静音；Seek 直接改偏移。
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

        private readonly byte[] _all;
        private long _allPos;
        private long _readPos;
        private long _frameIndex;
        private long _framesRead;
        private volatile bool _done;
        private int _diagMilestone;
        private volatile bool _disposed;
        private Thread? _encode;

        /// <summary>诊断：读到整曲末尾补"合法静音"帧累计。</summary>
        public long PrefillFrames { get; private set; }

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
            _all = new byte[checked(_totalFrames * _bpF)];

            _encode = new Thread(EncodeAll)
            {
                IsBackground = true,
                Name = "DoP整曲封装",
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
                    int got = _src.Read(raw, 0, raw.Length);
                    int whole = got - (got % 4);
                    if (whole <= 0)
                    {
                        break;
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
                            _allPos = _all.Length;
                        }

                        Monitor.PulseAll(_lock);
                    }
                }
            }
            catch (Exception ex)
            {
                try { StartupLog.Write("[DoP整曲封装异常] " + ex); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DoPWaveSource.cs", caught); }
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

        /// <summary>封装 whole 个原始 L,R,L,R… 交织字节为 DoP 容器帧；返回产出字节数。
        /// 位序（DoP 1.1 规范图）：24bit 帧 MSB→LSB = [标记][t₀…t₁₅]，t₀=最老比特在 16bit 字段 MSB；
        /// 小端 wire = [次字节原样][老字节原样][标记]，无位反转。</summary>
        private int EncodeBlock(byte[] raw, int whole, byte[] dopp, long startByte)
        {
            int fp = 0;
            long fi = startByte / _bpF; // startByte 是字节偏移 → 帧序号
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
                while (!_disposed && !_done && DateTime.UtcNow < deadline && _allPos < need)
                {
                    Monitor.Wait(_lock, TimeSpan.FromMilliseconds(5));
                }
            }
        }

        /// <summary>读 DoP 容器帧字节（每 _bpF 字节 = 1 帧）。仅从内存 _all 顺序取；到尾补 0x69 静音。</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int want = count - (count % _bpF);
            if (want <= 0)
            {
                return 0;
            }

            int total = 0;
            // 等待封装追进度（若封装线程仍在后台整曲封装且尚未覆盖到当前读位）：
            // 避免 read 读到 `_all` 中尚未被 EncodeAll 写入的未初始化区域（全 0 → 卡顿/静音段）。
            var waitDeadline = Environment.TickCount64 + 1500;
            while (total < want)
            {
                long avail;
                lock (_lock)
                {
                    // 关键：只取"已封装"的字节数（_allPos），而非虚拟总长 _all.Length，
                    // 否则 read 会超前读到 EncodeAll 还没写到的全 0 内存。整曲封装完成（_done）后两值相等。
                    avail = _allPos - _readPos;
                }

                if (avail <= 0)
                {
                    if (_done)
                    {
                        // 整曲已封装且已读完 → 无更多数据，跳出补尾静音。
                        break;
                    }

                    // 封装线程尚在填充：等待其 PulseAll（每次写入后会唤醒）追进度，超时兜底后补静音。
                    if (Environment.TickCount64 > waitDeadline)
                    {
                        break;
                    }

                    lock (_lock)
                    {
                        if (!_disposed && !_done && _allPos - _readPos <= 0)
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
                    Buffer.BlockCopy(_all, (int)_readPos, buffer, offset + total, (int)take);
                    lock (_lock) { _readPos += take; }
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
                _readPos = Math.Max(0, Math.Min(_all.Length, frame * _bpF));
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
            try { _encode?.Join(2000); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DoPWaveSource.cs", caught); }
            try { _src.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("DoPWaveSource.cs", caught); }
        }

        private static readonly double[] MilestoneFracs = { 0.10, 0.40, 0.70, 0.99 };
    }
}
