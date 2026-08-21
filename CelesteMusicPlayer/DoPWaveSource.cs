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
        private readonly byte[] _chunk = new byte[64 * 1024]; // 批量 DSD 源缓冲(L,R 交织)；命中 render 批量读，避免每帧小 IO
        private int _chunkCount;
        private int _chunkPos;
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

        /// <summary>读 DoP 容器帧字节（每 6 字节 = 1 帧 L3+R3）。
        /// 分批从 DSD 源批量读大块，缓存后逐帧封装——避免每帧一次小 IO 拖垮 render 线程导致固定频率卡顿。</summary>
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

        /// <summary>从批量缓冲取 L,R,L,R 连产 DoP 帧，直到产出 want 字节或缓存耗尽。</summary>
        private int EmitFrames(byte[] buffer, int offset, int want)
        {
            if (_chunkPos >= _chunkCount)
            {
                _chunkCount = _src.Read(_chunk, 0, _chunk.Length);
                _chunkPos = 0;
                if (_chunkCount < 4)
                {
                    return 0; // 源尽或残余不足一帧
                }
            }

            int frames = 0;
            while (frames * 6 < want && (_chunkPos + 4) <= _chunkCount)
            {
                byte l0 = _chunk[_chunkPos];
                byte r0 = _chunk[_chunkPos + 1];
                byte l1 = _chunk[_chunkPos + 2];
                byte r1 = _chunk[_chunkPos + 3];
                _chunkPos += 4;

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

            return frames * 6;
        }

        public void Seek(TimeSpan position)
        {
            long frame = (long)(position.TotalSeconds * _frameRate);
            frame = Math.Clamp(frame, 0, _totalFrames);
            _frameIndex = frame;
            _src.SeekSample(frame * 16 * _src.Channels);
            _chunkCount = 0;
            _chunkPos = 0;
        }

        public void Dispose() => _src.Dispose();
    }
}
