using System;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// ASIO 专用喂源扩容器：24bit 打包 PCM（3 字节/样本）→ 32bit 容器 PCM
    /// （24 位有效、左对齐 v&lt;&lt;8，低字节补零；与 <see cref="NativeCoreOutput"/> 的
    /// Expand24To32、ECHO 内核 WASAPI_FORMAT_PCM24_IN_32 同一容器约定）。
    ///
    /// 背景（2026-09-23 ASIO 双击播放必崩实锤，日志四连 02:56/02:57/17:06:01/17:06:19，
    /// 全部是 24bit 源）：
    /// NAudio 2.2.1 的 AsioOut 只支持 16/32bit（含 float32）源——
    /// AsioSampleConvertor.SelectSampleConvertor 的 switch 对 24bit 源没有任何分支，
    /// convertor 返回 null；而 AsioOut.InitRecordAndPlayback 不检查这个 null 照常"成功"，
    /// driver.Start() 后驱动第一次 BufferSwitchCallBack 调 convertor(...) 即
    /// NullReferenceException。异常抛在 ASIO 驱动的回调线程上 →
    /// AppDomain.UnhandledException → 整个进程崩溃（界面直接消失）。
    /// 本播放器转码 WAV 主体就是 24bit，等于 ASIO 输出对所有 24bit 曲目必崩。
    ///
    /// 修复：喂给 AsioOut 前先把 24bit 打包流扩成 24-in-32 容器（样本值逐位一致）。
    /// FiiO KA13 等驱动报 Int32LSB 时 NAudio 选中 ConvertorIntToInt2Channels——
    /// 原样拷贝、不做任何移位/缩放 → 端到端 bit-perfect，且容器正是独占内核
    /// 在同设备上实测零卡顿的 pcm24in32。
    ///
    /// 只包 24bit 源：16/32bit/float32 源 NAudio 均有对应分支，不会崩，原样通过。
    /// DoP 容器绝不经过本类（DoP 32bit 不是 24bit；24bit DoP 若被扩容器标记会错位，
    /// 详见调用点约束）。
    /// </summary>
    internal sealed class Widen24To32Provider : IWaveProvider, IWaveSourceProvider
    {
        private readonly IWaveSourceProvider _source;
        private readonly WaveFormat _outFormat;
        private readonly int _srcChannels;
        private readonly int _srcBlockAlign;  // 3 * ch
        private readonly int _outBlockAlign;  // 4 * ch
        private byte[] _readBuf = Array.Empty<byte>();

        public Widen24To32Provider(IWaveSourceProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            WaveFormat f = source.WaveFormat;
            if (f.BitsPerSample != 24)
            {
                throw new ArgumentException("Widen24To32Provider 只接受 24bit 源（当前 " + f.BitsPerSample + "bit）。");
            }

            _srcChannels = f.Channels;
            _srcBlockAlign = f.BlockAlign; // 3*ch
            _outFormat = new WaveFormat(f.SampleRate, 32, f.Channels);
            _outBlockAlign = _outFormat.BlockAlign; // 4*ch
        }

        public WaveFormat WaveFormat => _outFormat;

        public TimeSpan TotalTime => _source.TotalTime;

        public (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState => _source.ProbeCurrentState;

        public bool NextMounted => _source.NextMounted;

        public void Seek(TimeSpan position) => _source.Seek(position);

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_srcBlockAlign != 3 * _srcChannels || _outBlockAlign != 4 * _srcChannels)
            {
                return 0; // 非常规布局（理论不可达）：不让下游拿到错位字节
            }

            int frames = count / _outBlockAlign;
            if (frames <= 0)
            {
                return 0;
            }

            int srcBytes = frames * _srcBlockAlign;
            if (_readBuf.Length < srcBytes)
            {
                _readBuf = new byte[srcBytes * 2];
            }

            int read = _source.Read(_readBuf, 0, srcBytes);
            if (read <= 0)
            {
                return 0;
            }

            // 源 guaranteed 整帧（本仓各 provider 均按 BlockAlign 对齐返回）；
            // 防御性向下取整，尾零头不送（NAudio 的 driver_BufferUpdate 会自行清尾）。
            int gotFrames = read / _srcBlockAlign;
            if (gotFrames <= 0)
            {
                return 0;
            }

            // [b0 b1 b2]（24bit 小端，b0=LSB）→ [0 b0 b1 b2]（32bit 小端，v<<8 左对齐）
            int si = 0;
            int di = offset;
            int samples = gotFrames * _srcChannels;
            for (int n = 0; n < samples; n++)
            {
                buffer[di] = 0;
                buffer[di + 1] = _readBuf[si];
                buffer[di + 2] = _readBuf[si + 1];
                buffer[di + 3] = _readBuf[si + 2];
                si += 3;
                di += 4;
            }

            return gotFrames * _outBlockAlign;
        }
    }
}
