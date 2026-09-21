using System;
using System.Threading;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// ECHO 核心（celeste_audio_core.dll）独占输出适配器，接口与自研
    /// <see cref="NativeWasapiExclusiveOut"/> 完全一致（<see cref="IExclusiveOutput"/>），
    /// 供设置里 A/B 切换。与自研版的架构差异：
    ///   自研：托管渲染线程直接读源 → 写设备（.NET GC STW 会把它冻住 → 偶发断音）；
    ///   本类：C++ 原生渲染线程只 memcpy 环形缓冲，.NET GC 冻不到它；
    ///         托管 feeder 线程只负责「读源 → 灌缓冲」，被 GC 冻住的间隙由
    ///         ring 里 ~500ms 存货吸收，不直接碰设备。
    ///
    /// 数据精度链：源 PCM（16/24/32bit 整数或 float32）→ float32（2^n 缩放，精确可逆）
    ///   → ring → 设备端点格式（DLL 按 FLOAT32→PCM24-in-32→PCM16→PCM32 协商，
    ///   取整已改为四舍五入，24bit 往返无 ±1 LSB 误差）。数值无损，bit-perfect 成立。
    /// </summary>
    internal sealed class EchoCoreOutput : IExclusiveOutput
    {
        // feeder 每次灌入的帧数。ECHO daemon 用 4096 帧/块；44.1k 时 ≈93ms，
        // 96k 时 ≈43ms。ring 常备 ~500ms 存货，块越小背压越平滑、P/Invoke 次数略多（开销可忽略）。
        private const int ChunkFrames = 4096;

        private readonly object _seekLock = new();
        private Thread? _feeder;
        private volatile bool _stopRequested;
        private volatile bool _disposed;
        private TimeSpan? _pendingSeek; // feeder 消费的 seek 请求（唯一同时碰源与 ring 的线程，天然无竞争）

        private IWaveSourceProvider? _provider;
        private IntPtr _handle;
        private byte[] _readBuf = Array.Empty<byte>();  // feeder 复用（零分配）
        private float[] _chunk = Array.Empty<float>();  // feeder 复用（零分配）
        private int _srcBlockAlign = 4;
        private int _srcBits = 16;
        private bool _srcFloat;
        private int _carryLen; // 上一个读周期剩下的不足一帧字节（一般是 0）
        private readonly byte[] _carry = new byte[7]; // blockAlign 最大 8，余数最多 7 字节

        private long _framesBaseline; // 当前曲目（或 seek 目标）起始帧基准，保持绝对进度
        private int _rate;
        private int _channels = 2;
        private int _bufferFrames;
        private string _endpointFormat = "?";

        public event Action? Ended;
        public event Action<Exception>? Failed;

        public TimeSpan Duration { get; private set; }

        public int BufferMilliseconds { get; set; } = 100;

        public string? LastError { get; private set; }

        public string? ActualFormatDescription { get; private set; }

        public bool LastAlignDance => false; // DLL 内部消化了对齐 dance，不向上暴露

        /// <summary>协商后的采样率（帧/秒）。Init 前为 0。</summary>
        public int SampleRateValue => _rate;

        public bool IsStarted { get; private set; }

        /// <summary>已播帧数（绝对：曲目起点基准 + ring 已播）。</summary>
        public long FramesWritten
        {
            get
            {
                if (_handle == IntPtr.Zero) return 0;
                return _framesBaseline + (long)EchoCoreAudio.Stats(_handle).FramesPlayed;
            }
        }

        /// <summary>ring 欠载回调累计（ feeder 供不上时的硬指标；A/B 对比看这个）。</summary>
        public long UnderrunCount
        {
            get
            {
                if (_handle == IntPtr.Zero) return 0;
                return (long)EchoCoreAudio.Stats(_handle).UnderrunCallbacks;
            }
        }

        /// <summary>源已读尽（feeder 已 mark_input_ended）且 ring 存货也播空 = 这一曲真的放完了。
        /// 注意不能拿「reader 游标到头」当播完：feeder 会超前读源备货（ring 最多囤 ~600ms），
        /// reader 到头时歌曲尾巴还在 ring/设备缓冲里，那时切歌会把尾巴切掉。</summary>
        public bool IsDrained
        {
            get
            {
                if (_handle == IntPtr.Zero) return false;
                return EchoCoreAudio.Stats(_handle).Drained != 0;
            }
        }

        public bool Init(NativeWasapi.IMMDevice device, IWaveSourceProvider provider, bool requireExactFormat = false)
        {
            if (_handle != IntPtr.Zero)
            {
                LastError = "输出已初始化。";
                return false;
            }

            if (requireExactFormat)
            {
                // DSD/DoP 直出走 DoP 容器（DLL 侧需 start_dop 通道，本版本未接）→ 明确拒绝，让上层回退自研。
                LastError = "ECHO 核心暂不支持 DSD/DoP 精确直出（requireExact），请改用自研内核。";
                return false;
            }

            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Duration = provider.TotalTime;
            var src = provider.WaveFormat;
            if (src == null)
            {
                LastError = "输出源无有效格式。";
                return false;
            }

            _srcBlockAlign = src.BlockAlign;
            _srcBits = src.BitsPerSample;
            _srcFloat = src.Encoding == WaveFormatEncoding.IeeeFloat;
            if (src.Channels < 1 || src.Channels > 2)
            {
                LastError = "ECHO 核心仅支持 1/2 声道独占（当前 " + src.Channels + "ch）。";
                return false;
            }
            _channels = src.Channels;
            _carryLen = 0;

            // 请求缓冲帧 = 缓冲毫秒 × 采样率（DLL 按设备对齐规则调整，实际值 stats 读回）。
            int bufferMs = Math.Clamp(BufferMilliseconds, 10, 1000);
            int requestFrames = Math.Max(1, (int)((long)src.SampleRate * bufferMs / 1000));

            // 设备 ID：C# 侧持有 IMMDevice ID；DLL 内按 ID 精确匹配（友好名称有歧义，不用）。
            // 取不到 ID 时传空 = 默认渲染设备。
            string? deviceId = null;
            try
            {
                device.GetId(out deviceId);
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("EchoCoreOutput.Init(GetId)", caught);
                deviceId = null;
            }

            // 起播预填：先读最多一个周期的真实音频。设备启动时首缓冲直接填它（不填静音），
            // 起播即出声——这是照自研 NativeWasapiExclusiveOut.Play 的 pre-roll 修复搬过来的。
            _readBuf = new byte[ChunkFrames * Math.Max(4, _srcBlockAlign)];
            _chunk = new float[ChunkFrames * _channels];
            var prefill = new float[requestFrames * _channels];
            int prefillFrames = ReadAsFloats(prefill, requestFrames);
            if (prefillFrames < requestFrames)
            {
                // 源不足一个周期（极短文件/起播即近尾）：有多少灌多少，剩余 ring 自然补静音。
                StartupLog.Write($"ECHO核心 预填不足：请求{requestFrames}帧 实得{prefillFrames}帧（源短或起播点靠近结尾）");
            }

            int rc;
            IntPtr handle;
            try
            {
                rc = EchoCoreAudio.celeste_audio_start((uint)src.SampleRate, (uint)_channels,
                    (uint)requestFrames, deviceId,
                    prefillFrames > 0 ? prefill : null, (uint)prefillFrames, out handle);
            }
            catch (DllNotFoundException)
            {
                // 文件缺失（非 x64 安装不随包 / 被杀软删了）：给大白话错误，别把 .NET 异常抛给用户。
                LastError = "找不到 celeste_audio_core.dll（ECHO 核心内核文件）。请在「音频设置 → 独占输出内核」切回自研，或重装程序。";
                StartupLog.Write("ECHO核心 celeste_audio_core.dll 缺失（DllNotFoundException），回退自研");
                return false;
            }
            catch (BadImageFormatException)
            {
                // 架构不匹配（如 ARM 机器上放了 x64 DLL）：同上处理。
                LastError = "celeste_audio_core.dll 与当前系统架构不匹配。请切回自研内核，或重装程序。";
                StartupLog.Write("ECHO核心 celeste_audio_core.dll 架构不匹配（BadImageFormatException），回退自研");
                return false;
            }
            if (rc != 0 || handle == IntPtr.Zero)
            {
                LastError = "celeste_audio_start 失败 rc=" + rc
                    + (string.IsNullOrEmpty(deviceId) ? "（默认设备）" : " device=" + deviceId);
                return false;
            }
            _handle = handle;

            EchoCoreAudio.CelesteAudioStats st;
            try { st = EchoCoreAudio.Stats(_handle); }
            catch (Exception caught)
            {
                StartupLog.WriteException("EchoCoreOutput.Init(Stats)", caught);
                LastError = "读取 ECHO 核心状态失败：" + caught.Message;
                return false;
            }
            _rate = (int)st.SampleRate;
            _bufferFrames = st.BufferFrames;
            _endpointFormat = string.IsNullOrEmpty(st.Format) ? "?" : st.Format;
            ActualFormatDescription = DescribeOutput(src);

            StartupLog.Write(string.Format(
                "ECHO核心 协商成功 源={0}bit/{1}Hz/{2}ch → 设备 {3} Hz/{4} 缓冲{5}帧({6}ms) 容量{7}帧 | {8}",
                _srcBits, src.SampleRate, _channels,
                _rate, _endpointFormat, _bufferFrames,
                _rate > 0 ? _bufferFrames * 1000.0 / _rate : 0, st.CapacityFrames,
                ActualFormatDescription));
            return true;
        }

        public bool Play(TimeSpan? initialPosition = null)
        {
            if (_handle == IntPtr.Zero) return false;
            if (_provider == null) return false;

            // 源在开播前已被上层 seek 到 initialPosition（PlayWavAsync 预 seek）；
            // 基准设到该帧，Position = 基准 + 已播，保持绝对进度（与自研版口径一致）。
            _framesBaseline = initialPosition.HasValue && initialPosition.Value > TimeSpan.Zero && _rate > 0
                ? (long)(initialPosition.Value.TotalSeconds * _rate)
                : 0;
            _stopRequested = false;
            IsStarted = true;

            _feeder = new Thread(FeederLoop)
            {
                IsBackground = true,
                Name = "EchoCoreFeeder",
                Priority = ThreadPriority.Normal, // ring 有 ~500ms 存货兜底；normal 即可，不抢渲染线程
            };
            _feeder.Start();
            return true;
        }

        /// <summary>线程安全请求 seek：feeder 在下一轮循环开头消费（重定位源 + replace 缓冲 + declick）。</summary>
        public void SeekTo(TimeSpan pos)
        {
            lock (_seekLock) { _pendingSeek = pos; }
        }

        public void Stop()
        {
            _stopRequested = true;
            try { _feeder?.Join(3000); } catch (Exception caught) { StartupLog.WriteException("EchoCoreOutput.Stop", caught); }
            _feeder = null;
            if (_handle != IntPtr.Zero)
            {
                // stop 会让阻塞中的 push 返回 false → write 得 -3 → feeder 自然退出。
                EchoCoreAudio.celeste_audio_stop(_handle);
            }
            _framesBaseline = 0;
            IsStarted = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Stop(); } catch (Exception caught) { StartupLog.WriteException("EchoCoreOutput.Dispose", caught); }
            if (_handle != IntPtr.Zero)
            {
                try { EchoCoreAudio.celeste_audio_destroy(_handle); } catch (Exception caught) { StartupLog.WriteException("EchoCoreOutput.Dispose(destroy)", caught); }
                _handle = IntPtr.Zero;
            }
            _provider = null;
        }

        // ---------- feeder 线程 ----------

        private void FeederLoop()
        {
            try
            {
                while (!_stopRequested)
                {
                    TimeSpan? seek;
                    lock (_seekLock)
                    {
                        seek = _pendingSeek;
                        _pendingSeek = null;
                    }
                    if (seek.HasValue)
                    {
                        ConsumeSeek(seek.Value);
                        if (_stopRequested) break;
                    }

                    int got = ReadAsFloats(_chunk, ChunkFrames);
                    if (got <= 0)
                    {
                        // 源尽：标记输入结束。ring 播完剩余存货即 drained（不再计欠载），
                        // 之后由上层 UpdatePosition 的「源已读尽」判定 Stop→切下一首。
                        EchoCoreAudio.celeste_audio_mark_input_ended(_handle);
                        break;
                    }

                    int rc = EchoCoreAudio.celeste_audio_write(_handle, _chunk, (uint)got);
                    if (rc != 0) break; // -3=stop 已请求；其余=引擎异常，都退出
                }
            }
            catch (Exception ex)
            {
                IsStarted = false;
                try { EchoCoreAudio.celeste_audio_stop(_handle); } catch { /* 已在异常路径，忽略 */ }
                Failed?.Invoke(ex);
                return;
            }

            // feeder 自然退出（源尽）不算停止：设备会话还活着（ring 播完余货即 drained），
            // IsStarted 保持 true，与自研版「渲染线程只在 stop/故障时退出」口径一致。
            // 播完检测由上层 UpdatePosition 用 IsDrained 统一做。
        }

        /// <summary>feeder 线程内消费 seek：重定位源 → 读一小段 → replace 进 ring（重置统计 + declick）。</summary>
        private void ConsumeSeek(TimeSpan pos)
        {
            try
            {
                _provider!.Seek(pos);
                int got = ReadAsFloats(_chunk, ChunkFrames);
                EchoCoreAudio.celeste_audio_replace(_handle, got > 0 ? _chunk : null, (uint)got);
                // replaceBufferedAudio 内部把 framesPlayed 归零，这里把基准挪到 seek 目标，
                // FramesWritten = 基准 + 已播，进度条连续不回头。
                _framesBaseline = _rate > 0 ? (long)(pos.TotalSeconds * _rate) : 0;
            }
            catch (Exception caught)
            {
                StartupLog.WriteException("EchoCoreOutput.ConsumeSeek", caught);
            }
        }

        // ---------- 源读取：byte → float（精确可逆的 2^n 缩放） ----------

        /// <summary>从源读满 frames 帧（或读到源尽），转成交错 float 写进 dst。返回实得帧数。</summary>
        private int ReadAsFloats(float[] dst, int frames)
        {
            int wantBytes = frames * _srcBlockAlign;
            var buf = _readBuf;
            if (buf.Length < wantBytes)
            {
                buf = _readBuf = new byte[wantBytes];
            }

            int total = 0;
            // 接上上次余下的不足一帧字节（正常路径恒为 0，防御性处理）
            if (_carryLen > 0)
            {
                Buffer.BlockCopy(_carry, 0, buf, 0, _carryLen);
                total = _carryLen;
                _carryLen = 0;
            }

            while (total < wantBytes)
            {
                int n = _provider!.Read(buf, total, wantBytes - total);
                if (n <= 0) break;
                total += n;
            }

            int gotFrames = total / _srcBlockAlign;
            int leftover = total - gotFrames * _srcBlockAlign;
            if (leftover > 0)
            {
                // 防御：源返回了非整帧字节（不规范 provider），余数留到下一轮，避免流错位
                Buffer.BlockCopy(buf, gotFrames * _srcBlockAlign, _carry, 0, leftover);
                _carryLen = leftover;
            }

            BytesToFloats(buf, dst, gotFrames);
            return gotFrames;
        }

        private void BytesToFloats(byte[] src, float[] dst, int frames)
        {
            int ch = _channels;
            int count = frames * ch;
            if (_srcFloat)
            {
                // float32 源：逐样本 BitConverter（LE）。也可 BlockCopy 到 float[]，等价。
                for (int i = 0; i < count; i++)
                    dst[i] = BitConverter.ToSingle(src, i * 4);
                return;
            }

            int si = 0;
            if (_srcBits == 16)
            {
                for (int i = 0; i < count; i++)
                {
                    short s = (short)(src[si] | (src[si + 1] << 8));
                    si += 2;
                    dst[i] = s / 32768f;
                }
            }
            else if (_srcBits == 32)
            {
                for (int i = 0; i < count; i++)
                {
                    int s = src[si] | (src[si + 1] << 8) | (src[si + 2] << 16) | (src[si + 3] << 24);
                    si += 4;
                    dst[i] = s / 2147483648f;
                }
            }
            else
            {
                // 24bit packed（转码 WAV 的 s24le）
                for (int i = 0; i < count; i++)
                {
                    int v = src[si] | (src[si + 1] << 8) | (src[si + 2] << 16);
                    si += 3;
                    int s = (v & 0x800000) != 0 ? v - 0x1000000 : v;
                    dst[i] = s / 8388608f;
                }
            }
        }

        // ---------- 描述 ----------

        private string DescribeOutput(WaveFormat src)
        {
            string endpoint = _endpointFormat switch
            {
                "float32" => "float32",
                "pcm24" => "PCM24-in-32",
                "pcm16" => "PCM16",
                "pcm32" => "PCM32",
                _ => _endpointFormat,
            };
            // 数值无损判定：float32 端点无损承载 ≤24bit 整数与 float32 源；
            // PCM 端点在与源位深一致（或更高）时同样无损（DLL 取整已修成四舍五入）。
            bool lossless =
                (_srcFloat && _endpointFormat == "float32")
                || (!_srcFloat && _srcBits <= 24 && (_endpointFormat == "float32" || _endpointFormat == "pcm24"))
                || (!_srcFloat && _srcBits <= 16 && _endpointFormat == "pcm16")
                || (!_srcFloat && _srcBits <= 32 && _endpointFormat == "pcm32");
            return string.Format("{0} Hz / {1}bit / {2}ch → 设备 {3}{4}",
                src.SampleRate, _srcBits, _channels, endpoint,
                lossless ? "（数值无损）" : "（设备端格式低于源，已降级）");
        }
    }
}
