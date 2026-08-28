using System;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 无缝续接数据源：内部维护"当前"与"下一首"两份 WaveFileReader。
    /// 当当前读尽且下一首已就绪且格式一致时，Read 自动续接下一首数据（输出会话不重建 → gapless）。
    /// 仅当 next 未就绪或格式不同时返回 0（上层回退到重建）。
    ///
    /// 并发模型（两把锁，锁序固定 _sync → _snap，单向提升、无环 → 不死锁）：
    ///  - _sync：守卫所有写操作（SetCurrent/PrepareNext/Seek/Dispose）与 render 热路径 Read。
    ///    Read 在 _sync 内做阻塞磁盘读，防止 PrepareNext/Seek/Dispose 释放正在读取的 reader。
    ///  - _snap：只守卫"发布/读取状态快照"（_current/_next/_consumed/SwitchedToNext/WaveFormat 的引用/布尔赋值），
    ///    写入方在 _sync 内顺带取 _snap，UI 线程的位置/时长/切换查询只取 _snap（不含磁盘 IO）
    ///    → 进度条等 UI 读取不会因 render 持锁读盘而被冻结。
    /// </summary>
    internal sealed class SeamlessWaveProvider : IWaveProvider, IWaveSourceProvider
    {
        private readonly object _sync = new();
        private readonly object _snap = new();
        private WaveFileReader? _current;
        private WaveFileReader? _next;
        private bool _consumed;
        private WaveFormat? _waveFormat;

        public WaveFormat WaveFormat
        {
            get
            {
                lock (_snap)
                {
                    // 始终反映当前 reader 的真实格式（seek/暂停/续接后 _current 会变，_waveFormat 可能过期）
                    return _current?.WaveFormat ?? _waveFormat;
                }
            }
        }

        public SeamlessWaveProvider(WaveFileReader current)
        {
            lock (_sync)
            {
                SetCurrentUnsafe(current);
            }
        }

        public void SetCurrent(WaveFileReader current)
        {
            lock (_sync)
            {
                SetCurrentUnsafe(current);
            }
        }

        private void SetCurrentUnsafe(WaveFileReader current)
        {
            lock (_snap)
            {
                _current = current;
                _consumed = false;
                SwitchedToNext = false;
                _waveFormat = current?.WaveFormat;
            }

            DisposeNextUnsafe(); // 释放旧 next（在 _sync 内调用，安全）
        }

        /// <summary>预加载下一首。格式与当前一致才算数（同格式才可真无缝）。</summary>
        public void PrepareNext(WaveFileReader next)
        {
            lock (_sync)
            {
                if (_current == null || next == null)
                {
                    return;
                }

                if (!SameFormat(_current.WaveFormat, next.WaveFormat))
                {
                    DisposeNextUnsafe();
                    return; // 格式不同：不预接，交给上层重建
                }

                DisposeNextUnsafe();
                lock (_snap)
                {
                    _next = next;
                    _consumed = false; // seek 后重新预加载：复位，允许后续无缝续接（且 HasReadyNext 不再因旧 _consumed 恒 false）
                }
            }
        }

        /// <summary>下一次读取是否会接续到已预加载的下一首。</summary>
        public bool HasReadyNext
        {
            get
            {
                lock (_snap)
                {
                    if (_consumed || _next == null || _current == null)
                    {
                        return false;
                    }

                    return SameFormat(_current.WaveFormat, _next.WaveFormat)
                        && _current.Position >= _current.Length - 8;
                }
            }
        }

        /// <summary>诊断用：下一首是否已挂载（未消费）。</summary>
        public bool NextMounted
        {
            get { lock (_snap) { return _next != null; } }
        }

        /// <summary>诊断用：当前 reader 的读取进度 / 总长（字节）。</summary>
        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState
        {
            get
            {
                lock (_snap)
                {
                    if (_current == null) return null;
                    return (_current.Position, _current.Length, false);
                }
            }
        }

        /// <summary>下一首是否已无缝接上（用于上层切换播放标题/时长）。读到时置 true，上层处理后应 ResetSwitchFlag。</summary>
        public bool SwitchedToNext { get; private set; }

        /// <summary>重置无缝切换标志（上层在切换到下一首并同步完成后调用，以接受下一次切换）。</summary>
        public void ResetSwitchFlag()
        {
            lock (_snap)
            {
                SwitchedToNext = false;
            }
        }

        /// <summary>当前正在读取的 reader（可能已切到预加载的下一首）。</summary>
        public WaveFileReader? Current
        {
            get
            {
                lock (_snap)
                {
                    return _current;
                }
            }
        }

        /// <summary>当前 reader 的总时长（供占位/显示；切换后跟随新 reader）。</summary>
        public TimeSpan TotalTime
        {
            get
            {
                lock (_snap)
                {
                    return _current?.TotalTime ?? TimeSpan.Zero;
                }
            }
        }

        /// <summary>释放未消费的下一首 reader（当前 reader 由外部持有、不在此释放）。</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                DisposeNextUnsafe();
            }
        }

        /// <summary>把当前拖动到指定位置（转发到当前 reader）。seek 后丢弃已预加载的下一首，
        /// 因为位置已变，后续应重新预加载，避免接续错位。</summary>
        public void Seek(TimeSpan position)
        {
            lock (_sync)
            {
                if (_current != null)
                {
                    try { _current.CurrentTime = position; } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SeamlessWaveProvider.cs", caught); }
                }

                DisposeNextUnsafe();
                lock (_snap) { _consumed = true; } // seek 后由上层重新 PrepareNext
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                return ReadUnsafe(buffer, offset, count);
            }
        }

        private int ReadUnsafe(byte[] buffer, int offset, int count)
        {
            WaveFileReader? current;
            lock (_snap) { current = _current; }
            if (current == null)
            {
                return 0;
            }

            int total = 0;
            int remaining = count;
            int pos = offset;

            while (remaining > 0)
            {
                int n = current.Read(buffer, pos, remaining);
                if (n > 0)
                {
                    total += n;
                    pos += n;
                    remaining -= n;
                }

                if (n <= 0)
                {
                    // 当前读尽：尝试无缝切入已预加载的下一首
                    WaveFileReader? next;
                    lock (_snap)
                    {
                        next = _next;
                        if (next != null && !SameFormat(current.WaveFormat, next.WaveFormat))
                        {
                            next = null;
                        }
                    }

                    if (next != null)
                    {
                        lock (_snap)
                        {
                            _current = next;
                            _next = null;
                            SwitchedToNext = true;
                            current = next;
                        }
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

        private void DisposeNextUnsafe()
        {
            WaveFileReader? n;
            lock (_snap) { n = _next; _next = null; }
            if (n != null)
            {
                try { n.Dispose(); } catch (Exception caught) { global::CelesteMusicPlayer.StartupLog.WriteException("SeamlessWaveProvider.cs", caught); }
            }
        }
    }
}
