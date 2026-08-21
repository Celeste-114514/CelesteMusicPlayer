using System;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DoP（DSD over PCM）封装源：把 <see cref="IDsDStream"/> 的 1-bit DSD 流
    /// 封装成 176.4k/24bit/2ch（DSD64 基准 ×倍率）PCM 容器帧，供 WASAPI 独占原样直通。
    /// 每容器帧：L/R 各 24bit小端，低 16bit=DSD 数据，高 8bit=DoP 标记(0x05/0xFA 交替)。
    /// </summary>
    internal sealed class DoPWaveSource : IWaveSourceProvider, IDisposable
    {
        private readonly IDsDStream _src;
        private readonly int _frameRate;
        private readonly long _totalFrames;
        private readonly byte[] _srcBuf = new byte[4];   // L,R,L,R（每声道8样本）
        private int _srcCount;
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
        }

        public WaveFormat WaveFormat => new WaveFormat(_frameRate, 24, 2);

        public TimeSpan TotalTime
        {
            get
            {
                // DSD 每帧 1-bit 样本按声道采样率；用容器帧率换算更稳
                return _frameRate > 0 ? TimeSpan.FromSeconds((double)_totalFrames / _frameRate) : TimeSpan.Zero;
            }
        }

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState
            => (_frameIndex * 6, _totalFrames * 6, false);

        public bool NextMounted => false;

        /// <summary>读 DoP 容器帧字节（每 6 字节 = 1 帧 L3+R3）。</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            int remaining = count - (count % 6);
            int pos = offset;
            while (remaining > 0)
            {
                if (!NextFrame(out byte[] frame))
                {
                    break; // 源尽
                }

                Buffer.BlockCopy(frame, 0, buffer, pos, 6);
                pos += 6;
                total += 6;
                remaining -= 6;
            }

            return total;
        }

        public void Seek(TimeSpan position)
        {
            long frame = (long)(position.TotalSeconds * _frameRate);
            frame = Math.Clamp(frame, 0, _totalFrames);
            _frameIndex = frame;
            _src.SeekSample(frame * 16 * _src.Channels);
            _srcCount = 0;
        }

        private bool NextFrame(out byte[] frame)
        {
            frame = Frames[0];
            // 多线程下用本地缓冲避免共享数组竞态：仅 render 线程单线程回调，此处常量即可
            byte marker = (_frameIndex & 1) == 0 ? (byte)0x05 : (byte)0xFA;

            if (_srcCount < 4)
            {
                // 填 4 字节 L,R,L,R：每个声道 16 个 1-bit 样本
                _srcCount = _src.Read(_srcBuf, 0, 4);
                if (_srcCount < 4)
                {
                    // 不够一帧：末尾可能残缺，丢弃（DoP 必须整帧完整）
                    return false;
                }
            }

            byte l0 = _srcBuf[0], l1 = _srcBuf[2];
            byte r0 = _srcBuf[1], r1 = _srcBuf[3];
            _srcCount = 0;

            // LE 24bit：低16bit=DSD数据，byte2=标记
            frame[0] = l0;
            frame[1] = l1;
            frame[2] = marker;
            frame[3] = r0;
            frame[4] = r1;
            frame[5] = marker;

            _frameIndex++;
            return true;
        }

        private static readonly byte[][] Frames = { new byte[6] };

        public void Dispose() => _src.Dispose();
    }
}
