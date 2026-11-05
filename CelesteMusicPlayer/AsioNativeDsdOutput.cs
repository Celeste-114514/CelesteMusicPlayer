using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave.Asio;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 原生 DSD 的 ASIO 输出后端（绕开 NAudio 的 AsioOut）。
    ///
    /// 为什么不能走 AsioOut：NAudio 2.2.1 的 AsioOut.Init 只认 Float32/Int32/Int16/Int24 LSB
    /// 四种 PCM 采样类型，遇到 DSDInt8* 直接抛 NotSupportedException。但它底层的
    /// AsioDriver + AsioDriverExt 是 public 的，缓冲区、回调、采样率、ASIOFuture 全都能自己接管，
    /// 所以这里直接对接底层，把 1-bit 字节流原样送进驱动。
    ///
    /// 打开顺序（ASIO 2.3 DSD 扩展要求，顺序错了驱动不会进 DSD 模式）：
    ///   ① GetAsioDriverByName → AsioDriverExt
    ///   ② ASIOFuture(kAsioSetIoFormat, DSD)   —— 必须在 CreateBuffers 之前
    ///   ③ SetSampleRate(DSD 位时钟)
    ///   ④ 复查每通道 GetChannelInfo().type 是否为 32/33/40
    ///   ⑤ FillBufferCallback + CreateBuffers(2, 0, false)
    ///   ⑥ Start
    /// 收尾必须逆序还原：Stop → 切回 PCM → 恢复原采样率 → ReleaseDriver。
    ///
    /// ⚠ readBytes 由调用方提供，必须是「只从已备好货的内存缓冲里取字节」的非阻塞函数：
    /// 它运行在 ASIO 驱动回调线程上，不能碰磁盘、不能等锁。主项目里请接 AsioFeederProvider。
    /// 字节布局：L/R 逐字节交织的 1-bit DSD 流，一个字节 = 某声道连续 8 个 DSD 采样。
    /// </summary>
    internal sealed class AsioNativeDsdOutput : IDisposable
    {
        // ASIO SDK 2.3 的 DSD 扩展（asio.h）
        private const int KAsioSetIoFormat = 0x23111961;
        private const int KAsioGetIoFormat = 0x23111983;
        private const int FmtPcm = 0;
        private const int FmtDsd = 1;

        private const int TypeDsdLsb1 = 32;
        private const int TypeDsdMsb1 = 33;
        private const int TypeDsdNer8 = 40;

        private readonly string _driverName;
        private readonly double _nativeRateHz;
        private readonly int _channels;
        private readonly bool _sourceLsbFirst;
        private readonly Func<byte[], int, int> _readBytes;
        private readonly Action<string>? _log;

        private AsioDriver? _drv;
        private AsioDriverExt? _ext;
        private double _originalRate;
        private bool _driverMsbFirst;
        private bool _needBitReverse;
        private int _bufferFrames;      // 驱动给的 bufferSize，DSD 模式下单位是「1-bit 采样数」，不是字节
        private int _bytesPerChannel;   // 每通道每次回调要写的字节数 = _bufferFrames / 8
        private byte[]? _interleaved;
        private byte[]? _channelScratch;
        private long _bytesConsumed;
        private long _underruns;
        private int _drained;
        private volatile bool _paused;      // 软暂停：驱动保持 Start，只喂合法 DSD 静音码
        private bool _started;
        private bool _disposed;

        private static readonly byte[] Rev8 = BuildRev8();

        /// <summary>驱动回调已取走的 DSD 字节数（用于进度换算）。</summary>
        public long BytesConsumed => Interlocked.Read(ref _bytesConsumed);

        /// <summary>欠载次数（上游没及时供数，已补合法 DSD 静音码）。</summary>
        public long Underruns => Interlocked.Read(ref _underruns);

        /// <summary>上游读尽（播完）。上层轮询此标志决定何时停。</summary>
        public bool Drained => Volatile.Read(ref _drained) != 0;

        /// <summary>换源时显式复位播完标志（不等下一个回调）。</summary>
        public void ResetDrained() => Interlocked.Exchange(ref _drained, 0);

        /// <summary>驱动给的缓冲大小。⚠ DSD 模式下单位是「1-bit 采样数」，不是字节。</summary>
        public int BufferFrames => _bufferFrames;

        /// <summary>每个通道每次回调要写的字节数 = BufferFrames / 8。</summary>
        public int BytesPerChannel => _bytesPerChannel;

        /// <summary>驱动实际要求的 DSD 采样类型（32/33/40）。</summary>
        public int DriverDsdType { get; private set; }

        /// <param name="driverName">ASIO 驱动名，如 "FiiO ASIO Driver"。</param>
        /// <param name="nativeRateHz">DSD 位时钟：2822400 / 5644800 / 11289600。</param>
        /// <param name="channels">声道数，当前只做 1 或 2。</param>
        /// <param name="sourceLsbFirst">源字节内位序：主流 DSF 为 true（bit0 是最早采样点）。</param>
        /// <param name="readBytes">非阻塞取字节函数：(buffer, 需要的字节数) → 实际取到的字节数。</param>
        /// <param name="log">可选日志（主项目传 StartupLog.Write，独立工具传 Console.WriteLine）。</param>
        public AsioNativeDsdOutput(string driverName, double nativeRateHz, int channels,
            bool sourceLsbFirst, Func<byte[], int, int> readBytes, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(driverName)) throw new ArgumentException("驱动名为空", nameof(driverName));
            if (nativeRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(nativeRateHz));
            if (channels < 1 || channels > 2) throw new ArgumentOutOfRangeException(nameof(channels), "原生 DSD 目前只支持 1-2 声道");

            _driverName = driverName;
            _nativeRateHz = nativeRateHz;
            _channels = channels;
            _sourceLsbFirst = sourceLsbFirst;
            _readBytes = readBytes ?? throw new ArgumentNullException(nameof(readBytes));
            _log = log;
        }

        /// <summary>打开驱动并进入原生 DSD 模式（尚未起播，调 Start 才出声）。</summary>
        public void Open()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AsioNativeDsdOutput));
            if (_drv != null) throw new InvalidOperationException("已经 Open 过了，切格式必须 Dispose 重建。");

            _drv = AsioDriver.GetAsioDriverByName(_driverName);
            _ext = new AsioDriverExt(_drv);
            _originalRate = _ext.Capabilities.SampleRate;
            Log($"打开 ASIO 驱动 {_driverName}，原采样率 {_originalRate:0} Hz，输出通道 {_ext.Capabilities.OutputChannelInfos.Length}");

            // ② 先切 IoFormat（ASIO SDK 注释：必须在 prepared 状态之前，即 CreateBuffers 之前）
            SetIoFormat(FmtDsd);

            // ③ 再设 DSD 位时钟。先问驱动支不支持，别硬发：别的设备能到 DSD512/1024，
            //    KA13 只到 DSD256；这里不支持就抛，由会话自动回退 DoP / PCM。
            if (!_ext.IsSampleRateSupported(_nativeRateHz))
            {
                throw new NotSupportedException(
                    $"ASIO 驱动不支持 DSD 位时钟 {_nativeRateHz:0} Hz（本机 DAC 可能只到 DSD256），已自动回退。");
            }

            _ext.SetSampleRate(_nativeRateHz);

            // ④ 复查驱动真的进了 DSD 模式，并记下它要求的位序
            VerifyDsdChannelTypes();

            // ⑤ 回调 + 缓冲
            // 实测：切 DSD + 设位时钟后，驱动会通过 ASIO 消息让 NAudio 刷新 capabilities，
            // CreateBuffers 拿到的已经是 DSD 模式的 preferred size（本机 131072 个 1-bit 采样）。
            // NAudio 2.2.1 的 BuildCapabilities 不是 public，别显式调。
            _ext.FillBufferCallback = OnFillBuffers;
            _bufferFrames = _ext.CreateBuffers(_channels, 0, false);
            if (_bufferFrames < 8) throw new InvalidOperationException("CreateBuffers 返回了非法的缓冲大小：" + _bufferFrames);

            // ⚠ ASIO 2.3 DSD 扩展里 bufferSize 的单位是「1-bit 采样数」：
            //    一个字节打包 8 个 DSD 采样，所以每通道物理缓冲 = bufferSize / 8 字节。
            //    按 bufferSize 字节去写会越界 8 倍、把驱动缓冲写穿（CLR 0x80131506，已实测踩过）。
            //    依据：HiFi Player src/output/asio/mod.rs 的 buffer_switch 里 `let bytes = st.buffer_size / 8;`
            _bytesPerChannel = _bufferFrames / 8;
            _interleaved = new byte[_bytesPerChannel * _channels];
            _channelScratch = new byte[_bytesPerChannel];
            Log($"原生 DSD 就绪：{_nativeRateHz:0} Hz / {_channels}ch / 驱动类型 {DriverDsdType}"
                + $"（{(_driverMsbFirst ? "MSB-first" : "LSB-first")}）/ bufferSize={_bufferFrames} 个 1-bit 采样"
                + $" = {_bytesPerChannel} 字节每通道"
                + (_needBitReverse ? " / 源位序需逐字节反转" : " / 源位序原样"));
        }

        public void Start()
        {
            if (_ext == null) throw new InvalidOperationException("请先 Open()。");
            if (_started) return;
            _ext.Start();
            _started = true;
            Log("原生 DSD 起播");
        }

        public void Stop()
        {
            if (!_started) return;
            _ext?.Stop();
            _started = false;
            Log("原生 DSD 停止");
        }

        private void VerifyDsdChannelTypes()
        {
            for (int i = 0; i < _channels; i++)
            {
                var info = _drv!.GetChannelInfo(i, false);
                int t = (int)info.type;
                if (t != TypeDsdLsb1 && t != TypeDsdMsb1 && t != TypeDsdNer8)
                {
                    throw new InvalidOperationException(
                        $"通道 {i} 的采样类型是 {info.type}({t})，不是 DSD 类型 —— 驱动没有进入原生 DSD 模式，拒绝起播（硬发会变成噪声）。");
                }

                if (t == TypeDsdNer8)
                {
                    throw new NotSupportedException(
                        "驱动要求 DSDInt8NER8(40)：一个采样占一字节，和本实现的「一字节打包 8 采样」布局不同，暂不支持。"
                        + "本机 FiiO KA13 报的是 33，不受影响。");
                }

                bool msb = t == TypeDsdMsb1;
                if (i == 0)
                {
                    DriverDsdType = t;
                    _driverMsbFirst = msb;
                }
                else if (msb != _driverMsbFirst || t != DriverDsdType)
                {
                    throw new InvalidOperationException($"左右通道采样类型不一致（ch0={DriverDsdType}, ch{i}={t}），拒绝起播。");
                }
            }

            // 源是 MSB-first 当且仅当它不是 LSB-first；两者不一致才需要逐字节 bit 反转。
            // 类型 40（NER8，8bit 一采样）没有位序概念，不反转。
            bool sourceMsbFirst = !_sourceLsbFirst;
            _needBitReverse = DriverDsdType != TypeDsdNer8 && sourceMsbFirst != _driverMsbFirst;
        }

        /// <summary>ASIO 驱动回调：只做内存搬运，不碰磁盘、不等锁、不分配。</summary>
        /// <summary>
        /// 软暂停 / 恢复：驱动一直保持 Start 与 DSD 格式，暂停时只喂 0x69/0x96 静音码。
        /// 比 ASIO Stop/Start 稳（不用赌驱动能不能二次 Start），也不打断 DAC 的 DSD 锁。
        /// </summary>
        public void SetPaused(bool value) => _paused = value;

        private void OnFillBuffers(IntPtr[] inputChannels, IntPtr[] outputChannels)
        {
            if (_disposed || _interleaved == null || _channelScratch == null) return;

            if (_paused)
            {
                // 不读源、不累加进度、不判播完；每通道写满合法 DSD 静音码。
                byte[] scratch = _channelScratch;
                for (int f = 0; f < _bytesPerChannel; f++)
                {
                    scratch[f] = ((f & 1) == 0) ? (byte)0x69 : (byte)0x96;
                }
                for (int c = 0; c < _channels && c < outputChannels.Length; c++)
                {
                    if (outputChannels[c] != IntPtr.Zero)
                    {
                        Marshal.Copy(scratch, 0, outputChannels[c], _bytesPerChannel);
                    }
                }
                return;
            }

            int need = _bytesPerChannel * _channels;
            int got;
            try
            {
                got = _readBytes(_interleaved, need);
            }
            catch (Exception ex)
            {
                Log("取字节异常（本次补静音）：" + ex.Message);
                got = 0;
            }

            // 播完标志是「边沿」不是「锁存」：换源后一旦重新供数就必须复位，
            // 否则监控线程会把新曲第一帧误判成又播完了。
            Interlocked.Exchange(ref _drained, got > 0 ? 0 : 1);
            if (got > need)
            {
                got = need;
            }

            // 必须按整帧（声道数）对齐，否则 L/R 错位 = 左右串音
            got -= got % _channels;

            if (got < need)
            {
                // DSD 静音是 0x69 / 0x96 交替的合法码型，填 0 会让 DAC 失锁甚至咔哒。
                for (int i = got; i < need; i++)
                {
                    _interleaved[i] = ((i & 1) == 0) ? (byte)0x69 : (byte)0x96;
                }
                Interlocked.Increment(ref _underruns);
            }

            for (int c = 0; c < _channels; c++)
            {
                byte[] scratch = _channelScratch;
                if (_needBitReverse)
                {
                    for (int f = 0; f < _bytesPerChannel; f++)
                    {
                        scratch[f] = Rev8[_interleaved[f * _channels + c]];
                    }
                }
                else
                {
                    for (int f = 0; f < _bytesPerChannel; f++)
                    {
                        scratch[f] = _interleaved[f * _channels + c];
                    }
                }

                if (c < outputChannels.Length && outputChannels[c] != IntPtr.Zero)
                {
                    Marshal.Copy(scratch, 0, outputChannels[c], _bytesPerChannel);
                }
            }

            Interlocked.Add(ref _bytesConsumed, need);
        }

        private void SetIoFormat(int format)
        {
            IntPtr p = Marshal.AllocHGlobal(512);
            try
            {
                // ASIOIoFormat { long FormatType; char future[512-sizeof(long)]; }
                Marshal.Copy(new byte[512], 0, p, 512);
                Marshal.WriteInt32(p, 0, format);
                _drv!.Future(KAsioSetIoFormat, p);
                int back = Marshal.ReadInt32(p, 0);
                if (back != format)
                {
                    throw new InvalidOperationException(
                        $"驱动拒绝了 IoFormat 切换：请求 {FmtName(format)}，回来 {back}（{FmtName(back)}）。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        private int GetIoFormat()
        {
            IntPtr p = Marshal.AllocHGlobal(512);
            try
            {
                Marshal.Copy(new byte[512], 0, p, 512);
                _drv!.Future(KAsioGetIoFormat, p);
                return Marshal.ReadInt32(p, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        private static string FmtName(int f) => f switch
        {
            FmtPcm => "PCM",
            FmtDsd => "DSD",
            -1 => "无效/驱动拒绝",
            _ => "未知(" + f + ")",
        };

        private void Log(string s) => _log?.Invoke("[DSD原生] " + s);

        private static byte[] BuildRev8()
        {
            var table = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int v = i;
                int r = 0;
                for (int b = 0; b < 8; b++)
                {
                    r = (r << 1) | (v & 1);
                    v >>= 1;
                }
                table[i] = (byte)r;
            }
            return table;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { Stop(); } catch (Exception ex) { Log("Stop 异常：" + ex.Message); }

            // 逆序还原驱动状态，绝不把用户的 DAC 留在 DSD 模式
            if (_drv != null)
            {
                try
                {
                    SetIoFormat(FmtPcm);
                    Log($"已切回 PCM 格式（复查={FmtName(GetIoFormat())}）");
                }
                catch (Exception ex) { Log("恢复 PCM 格式失败：" + ex.Message); }

                try
                {
                    if (_originalRate > 0)
                    {
                        _ext?.SetSampleRate(_originalRate);
                        Log($"已恢复采样率 {_originalRate:0} Hz");
                    }
                }
                catch (Exception ex) { Log("恢复采样率失败：" + ex.Message); }

                try { _ext?.ReleaseDriver(); } catch (Exception ex) { Log("释放驱动失败：" + ex.Message); }
            }

            _ext = null;
            _drv = null;
        }
    }
}
