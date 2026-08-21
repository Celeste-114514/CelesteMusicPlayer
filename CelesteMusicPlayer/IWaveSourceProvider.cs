using System;
using NAudio.Wave;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// HiFi 输出（WASAPI 独占渲染线程）可消费的音频源抽象。
    /// 既支持 PCM 无缝源（<see cref="SeamlessWaveProvider"/>），也支持 DSD 源
    /// （<see cref="DoPWaveSource"/>，直接在独占通道内输出 DoP 封装的 PCM 容器帧）。
    /// render 线程只依赖这些成员，不关心底层是 PCM 还是 DoP——只要 WaveFormat 与
    /// 独占协商格式一致，样本字节即可原样直通（bit-perfect）。
    /// </summary>
    internal interface IWaveSourceProvider
    {
        /// <summary>输出格式（供独占协商 / marshal 用）。</summary>
        WaveFormat WaveFormat { get; }

        /// <summary>总时长。</summary>
        TimeSpan TotalTime { get; }

        /// <summary>诊断用：当前源的读取进度 / 总长（字节）。可为 null。</summary>
        (long Pos, long Len, bool SameAsOuter)? ProbeCurrentState { get; }

        /// <summary>诊断用：是否已装入可无缝接续的下一首。</summary>
        bool NextMounted { get; }

        /// <summary>读取源数据（阻塞，直到读满或源尽）。与 NAudio IWaveProvider.Read 相同语义。</summary>
        int Read(byte[] buffer, int offset, int count);

        /// <summary>拖动到指定位置（render 线程消费 seek 请求时调用）。</summary>
        void Seek(TimeSpan position);
    }
}
