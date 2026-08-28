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

        // —— 交叉淡化 ——
        // 当前曲进入结尾窗口时，与已预加载的下一首按等功率曲线重叠混合，再提升为当前曲。
        // 0 毫秒 = 关闭，行为与改造前完全一致（无缝硬切）。
        private int _crossfadeMs;
        private bool _xfActive;        // 是否正处于淡化过程中（跨 Read 调用保持状态）
        private long _xfFramesDone;    // 已混合的帧数
        private long _xfTotalFrames;   // 本次淡化的总帧数
        private byte[] _xfBufCur = Array.Empty<byte>();
        private byte[] _xfBufNext = Array.Empty<byte>();
        private float[] _xfMix = Array.Empty<float>();
        private float[] _xfTmp = Array.Empty<float>();

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

            ResetCrossfadeUnsafe();
            DisposeNextUnsafe(); // 释放旧 next（在 _sync 内调用，安全）
        }

        /// <summary>设置交叉淡化时长（毫秒）。0 = 关闭，恢复原来的无缝硬切行为。
        /// 只对「自动连续播放」的自然换曲生效；手动切歌会重建播放会话，不经过淡化。</summary>
        public void SetCrossfade(int milliseconds)
        {
            lock (_sync)
            {
                _crossfadeMs = milliseconds > 0 ? milliseconds : 0;
                ResetCrossfadeUnsafe();
            }
        }

        /// <summary>复位淡化状态（换曲 / seek / 重新预载时；调用方须持 _sync）。</summary>
        private void ResetCrossfadeUnsafe()
        {
            _xfActive = false;
            _xfFramesDone = 0;
            _xfTotalFrames = 0;
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

                ResetCrossfadeUnsafe(); // seek 后位置变了，废弃进行中的淡化
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
                WaveFormat fmt = current.WaveFormat;
                int block = fmt.BlockAlign;

                // 交叉淡化：当前曲进入结尾窗口且下一首已就绪 → 两首重叠混合，淡化完再提升为当前曲
                WaveFileReader? xfNext;
                lock (_snap) { xfNext = _next; }
                bool xfPossible = _crossfadeMs > 0 && xfNext != null && SameFormat(fmt, xfNext.WaveFormat);
                if (xfNext != null && ShouldCrossfade(current, xfNext))
                {
                    int mixed = CrossfadeRead(current, xfNext, buffer, pos, remaining);
                    bool switched;
                    lock (_snap)
                    {
                        switched = !ReferenceEquals(current, _current);
                        current = _current;
                    }

                    if (mixed > 0)
                    {
                        total += mixed;
                        pos += mixed;
                        remaining -= mixed;
                    }

                    if (current == null)
                    {
                        break;
                    }

                    // 保险：既没产出数据也没切换曲目时跳出，防止死循环
                    if (mixed <= 0 && !switched)
                    {
                        break;
                    }

                    continue;
                }

                // 正常读取。若淡化窗口正好落在本轮读取区间内，先把本轮截断到淡化起点——
                // 否则一次读取会跨过起点，淡化起点被输出缓冲大小"量化"，实际淡化时长会短于设定值。
                int maxFrames = block > 0 ? remaining / block : 0;
                if (xfPossible && maxFrames > 0 && block > 0)
                {
                    long want = (long)_crossfadeMs * fmt.SampleRate / 1000;
                    long curRemaining = (current.Length - current.Position) / block;
                    if (curRemaining > want)
                    {
                        long untilFade = curRemaining - want; // 还需正常播放多少帧才到淡化起点
                        if (untilFade > 0 && untilFade < maxFrames) maxFrames = (int)untilFade;
                    }
                }

                int bytesToRead = block > 0 ? maxFrames * block : remaining;
                int n = bytesToRead > 0 ? current.Read(buffer, pos, bytesToRead) : 0;
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
                        // 走的是「读尽后硬切」（未进入淡化，例如淡化关闭或下一首预载太晚）
                        ResetCrossfadeUnsafe();
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

        #region 交叉淡化

        /// <summary>是否应进行（或继续）交叉淡化。已在淡化中则继续；否则当当前曲剩余时长
        /// 进入淡化窗口、且下一首格式与当前一致时才启动。调用方须持 _sync。</summary>
        private bool ShouldCrossfade(WaveFileReader current, WaveFileReader next)
        {
            if (_xfActive) return true;
            if (_crossfadeMs <= 0) return false;

            WaveFormat fmt = current.WaveFormat;
            if (!SameFormat(fmt, next.WaveFormat)) return false; // 格式不同无法逐样本混合
            int block = fmt.BlockAlign;
            if (block <= 0) return false;

            long want = (long)_crossfadeMs * fmt.SampleRate / 1000;
            long curRemaining = (current.Length - current.Position) / block;
            long nextRemaining = (next.Length - next.Position) / block;
            if (curRemaining > want) return false; // 还没到结尾窗口，先正常播放

            // 淡化长度不能超过任一方剩余长度
            long frames = Math.Min(want, curRemaining);
            frames = Math.Min(frames, nextRemaining);
            if (frames <= 0) return false;

            _xfTotalFrames = frames;
            _xfFramesDone = 0;
            _xfActive = true;
            int ch = Math.Max(1, fmt.Channels);
            if (_xfMix.Length < ch) _xfMix = new float[ch];
            if (_xfTmp.Length < ch) _xfTmp = new float[ch];
            return true;
        }

        /// <summary>交叉淡化读取：从当前曲与下一首各取同样帧数，按等功率曲线混合写入输出缓冲。
        /// 返回写入字节数；淡化完成（或任一方读尽）时把下一首提升为当前曲并置 SwitchedToNext。
        /// 调用方须持 _sync（内部按固定锁序 _sync → _snap 取 _snap）。</summary>
        private int CrossfadeRead(WaveFileReader current, WaveFileReader next, byte[] buffer, int pos, int remaining)
        {
            WaveFormat fmt = current.WaveFormat;
            int block = fmt.BlockAlign;
            int channels = fmt.Channels;
            if (block <= 0 || channels <= 0)
            {
                FinishCrossfade();
                return 0;
            }

            int framesAvail = remaining / block;
            long framesLeft = _xfTotalFrames - _xfFramesDone;
            if (framesAvail <= 0 || framesLeft <= 0)
            {
                FinishCrossfade();
                return 0;
            }

            int framesWanted = (int)Math.Min(framesAvail, framesLeft);
            int bytesWanted = framesWanted * block;
            if (_xfBufCur.Length < bytesWanted) _xfBufCur = new byte[bytesWanted];
            if (_xfBufNext.Length < bytesWanted) _xfBufNext = new byte[bytesWanted];

            int nCur = current.Read(_xfBufCur, 0, bytesWanted);
            int nNext = next.Read(_xfBufNext, 0, bytesWanted);
            int frames = Math.Min(nCur / block, nNext / block);
            if (frames <= 0)
            {
                FinishCrossfade(); // 有一方已读尽 → 直接切过去
                return 0;
            }

            for (int f = 0; f < frames; f++)
            {
                PcmSampleCodec.DecodeFrame(_xfBufCur, f * block, fmt, _xfMix, 0);
                PcmSampleCodec.DecodeFrame(_xfBufNext, f * block, fmt, _xfTmp, 0);

                double t = (double)_xfFramesDone / _xfTotalFrames; // 淡化进度 0→1
                // 等功率（constant-power）曲线：cos/sin 使混合区总功率恒定，
                // 避免线性淡化在中点出现可闻的音量下陷。
                double gCur = Math.Cos(t * Math.PI / 2.0);
                double gNext = Math.Sin(t * Math.PI / 2.0);
                for (int c = 0; c < channels; c++)
                {
                    _xfMix[c] = (float)(_xfMix[c] * gCur + _xfTmp[c] * gNext);
                }

                PcmSampleCodec.EncodeFrame(buffer, pos + f * block, fmt, _xfMix, 0);
                _xfFramesDone++;
            }

            int bytesOut = frames * block;
            if (frames < framesWanted || _xfFramesDone >= _xfTotalFrames)
            {
                FinishCrossfade();
            }

            return bytesOut;
        }

        /// <summary>结束淡化：把下一首提升为当前曲。
        /// 旧 current 的释放交给上层（上层按自己持有的 _waveFile 引用处理，与既有无缝逻辑一致）。</summary>
        private void FinishCrossfade()
        {
            lock (_snap)
            {
                WaveFileReader? n = _next;
                if (n != null)
                {
                    _current = n;
                    _next = null;
                    SwitchedToNext = true;
                }
            }

            _xfActive = false;
            _xfFramesDone = 0;
            _xfTotalFrames = 0;
        }

        #endregion

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
