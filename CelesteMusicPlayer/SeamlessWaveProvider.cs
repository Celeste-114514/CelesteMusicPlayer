using System;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 无缝续接数据源：内部维护"当前"与"下一首"两份 WaveFileReader。
    /// 当当前读尽且下一首已就绪且格式一致时，Read 自动续接下一首数据（输出会话不重建 → gapless）。
    /// 仅当 next 未就绪或格式不同时返回 0（上层回退到重建）。
    /// </summary>
    internal sealed class SeamlessWaveProvider : IWaveProvider
    {
        private WaveFileReader? _current;
        private WaveFileReader? _next;
        private bool _consumed;

        public WaveFormat WaveFormat { get; private set; }

        public SeamlessWaveProvider(WaveFileReader current)
        {
            SetCurrent(current);
        }

        public void SetCurrent(WaveFileReader current)
        {
            _current = current;
            _next = null;
            _consumed = false;
            if (current != null)
            {
                WaveFormat = current.WaveFormat;
            }
        }

        /// <summary>预加载下一首。格式与当前一致才算数（同格式才可真无缝）。</summary>
        public void PrepareNext(WaveFileReader next)
        {
            if (_current == null || next == null)
            {
                return;
            }

            if (!SameFormat(_current.WaveFormat, next.WaveFormat))
            {
                DisposeNext();
                return; // 格式不同：不预接，交给上层重建
            }

            DisposeNext();
            _next = next;
        }

        /// <summary>下一次读取是否会接续到已预加载的下一首。</summary>
        public bool HasReadyNext
        {
            get
            {
                if (_consumed || _next == null || _current == null)
                {
                    return false;
                }

                return SameFormat(_current.WaveFormat, _next.WaveFormat)
                    && _current.Position >= _current.Length - 8;
            }
        }

        /// <summary>下一首是否已无缝接上（用于上层切换播放标题/时长）。读到时置 true，上层处理后应 ResetSwitchFlag。</summary>
        public bool SwitchedToNext { get; private set; }

        /// <summary>重置无缝切换标志（上层在切换到下一首并同步完成后调用，以接受下一次切换）。</summary>
        public void ResetSwitchFlag()
        {
            SwitchedToNext = false;
        }

        /// <summary>当前正在读取的 reader（可能已切到预加载的下一首）。</summary>
        public WaveFileReader? Current => _current;

        /// <summary>释放未消费的下一首 reader（当前 reader 由外部持有、不在此释放）。</summary>
        public void Dispose()
        {
            DisposeNext();
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_current == null)
            {
                return 0;
            }

            int total = 0;
            int remaining = count;
            int pos = offset;

            while (remaining > 0)
            {
                int n = _current.Read(buffer, pos, remaining);
                if (n > 0)
                {
                    total += n;
                    pos += n;
                    remaining -= n;
                }

                if (n <= 0)
                {
                    // 当前读尽：尝试无缝切入已预加载的下一首
                    if (_next != null && SameFormat(_current.WaveFormat, _next.WaveFormat))
                    {
                        _current = _next;
                        _next = null;
                        SwitchedToNext = true;
                        continue; // 继续读下一首
                    }

                    break; // 无续接 → 结束
                }
            }

            return total;
        }

        private static bool SameFormat(WaveFormat a, WaveFormat b)
        {
            return a != null && b != null
                && a.SampleRate == b.SampleRate
                && a.BitsPerSample == b.BitsPerSample
                && a.Channels == b.Channels;
        }

        private void DisposeNext()
        {
            if (_next != null)
            {
                try { _next.Dispose(); } catch { }
                _next = null;
            }
        }
    }
}
