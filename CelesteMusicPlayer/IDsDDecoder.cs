using System;
using System.IO;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// DSD 采样倍率（决定 DoP 容器采样率）。
    ///   DSD64  = 2.8224MHz 1bit → DoP 176.4k/24bit × 2ch
    ///   DSD128 = 5.6448MHz 1bit → DoP 352.8k/24bit × 2ch
    ///   DSD256 = 11.2896MHz     → DoP 705.6k/24bit × 2ch
    ///   DSD512 = 22.5792MHz     → DoP 1411.2k/24bit × 2ch
    /// </summary>
    public enum DsdRate
    {
        Dsd64 = 64,
        Dsd128 = 128,
        Dsd256 = 256,
        Dsd512 = 512,
    }

    /// <summary>解析一个 DSD 容器后得到的 DSD 1-bit 音频流（不转 PCM）。</summary>
    public interface IDsDStream : IDisposable
    {
        /// <summary>采样倍率。</summary>
        DsdRate Rate { get; }

        /// <summary>声道数（2 立体声 / 多声道）。</summary>
        int Channels { get; }

        /// <summary>总 DSD 帧数（每个声道一个 1-bit 样本为 1 帧）。</summary>
        long TotalSamples { get; }

        /// <summary>1-bit 样本按「字节交错」排列；每个字节内含 8 个 1-bit 样本。
        /// 立体声按 L,R,L,R… 逐字节交织（与 DSF DSD 数据块一致）。</summary>
        int Read(byte[] buffer, int offset, int count);

        /// <summary>按 1-bit 样本数 seek。</summary>
        void SeekSample(long sampleIndex);

        /// <summary>当前已读 1-bit 样本总数（跨声道计数）。</summary>
        long SamplesRead { get; }
    }

    /// <summary>
    /// DSD 解码器插件接口：把 DSF/DFF 解成 1-bit DSD 流（绝不转 PCM）。
    /// 内建实现 <see cref="BuiltInDsdDecoder"/>；外部解码器（安装时可选插件）实现同一接口即可接入。
    /// </summary>
    public interface IDsDDecoder
    {
        /// <summary>是否支持该扩展名（.dsf/.dff）。</summary>
        bool CanDecode(string path);

        /// <summary>解码并返回 1-bit 流。失败抛异常或返回 null。</summary>
        IDsDStream Open(string path);
    }
}
