using System;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// 独占输出器统一接口：自研 <see cref="NativeWasapiExclusiveOut"/> 与 ECHO 核心
    /// <see cref="EchoCoreOutput"/> 实现同一外形，HiFiOutputBackend 只依赖本接口，
    /// 即可在设置里一键 A/B 切换（默认自研，可随时回退）。
    /// </summary>
    internal interface IExclusiveOutput : IDisposable
    {
        /// <summary>数据源自然播放到头（自研：渲染线程退出；ECHO：feeder 读尽且设备已停）。</summary>
        event Action? Ended;

        /// <summary>已写入/已播的总帧数（累加，不随曲目切换归零；上层按曲目相对进度换算）。</summary>
        long FramesWritten { get; }

        /// <summary>协商后的采样率（帧/秒）。</summary>
        int SampleRateValue { get; }

        string? LastError { get; }

        /// <summary>设备端实际输出格式描述（链路状态栏显示，口径必须诚实：降级要写明）。</summary>
        string? ActualFormatDescription { get; }

        /// <summary>
        /// 设备端协商结果（结构化）。Init 失败/未初始化时为 null。
        /// 徽标与链路面板据此判定，绝不从 <see cref="ActualFormatDescription"/> 反解析数字
        /// （2026-09-22 用户实测：描述串嵌源位深，正则从源段解析导致徽标谎报绿灯）。
        /// </summary>
        AudioFormat? NegotiatedFormat { get; }

        /// <summary>设备端路径分类：源直通 / 数值无损 / 已降级 / 未知。</summary>
        DevicePath DevicePathKind { get; }

        /// <summary>设备端点容器格式名（如 "PCM24-in-32" / "float32" / "Pcm16"）。仅供链路面板展示。</summary>
        string? DeviceEndpointName { get; }

        bool IsStarted { get; }

        /// <summary>事件驱动缓冲大小（毫秒），须在 <see cref="Init"/> 之前设置。</summary>
        int BufferMilliseconds { get; set; }

        /// <summary>最近一次初始化是否触发 AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED 对齐 dance。</summary>
        bool LastAlignDance { get; }

        /// <summary>累计欠载次数（卡顿硬指标，A/B 对比时两边都要看）。</summary>
        long UnderrunCount { get; }

        bool Init(NativeWasapi.IMMDevice device, IWaveSourceProvider provider, bool requireExactFormat = false);

        bool Play(TimeSpan? initialPosition = null);

        /// <summary>线程安全请求 seek（feeder/渲染线程消费）。</summary>
        void SeekTo(TimeSpan position);

        void Stop();
    }
}
