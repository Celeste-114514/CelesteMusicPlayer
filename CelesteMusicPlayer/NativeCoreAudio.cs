using System;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// celeste_core.dll（Celeste 自研原生音频内核，MIT）的 P/Invoke 封装。
    /// 与 <see cref="EchoCoreAudio"/> 的关键差异：**本内核走整数字节直喂**——
    /// feeder 从 C# 链读出来的就是最终字节（16/24/32bit 整数或 float32），
    /// 原生渲染线程只 memcpy 进端点容器（协商保证端点容器与源布局一致），
    /// 全程不经 float 中转。DSP 全关时 = 端到端 bit-perfect（字节级）。
    ///
    /// 为什么要有原生线程：.NET GC 的 Stop-The-World 会冻住进程内所有托管线程
    /// （2026-09-23 用户实机 3/3 尖峰伴随 gen2 GC 实锤），C# 渲染线程
    /// MMCSS/优先级再高也躲不掉。C++ 原生渲染线程 GC 永远碰不到；
    /// C# 侧只做 feeder（读链→灌 ring），被冻住时 ring 里 1.5s 存货继续供血。
    ///
    /// 许可证：MIT（与主项目一致）。与 LGPL 的 celeste_audio_core.dll 相互独立、互不派生。
    /// </summary>
    internal static class NativeCoreAudio
    {
        private const string Dll = "celeste_core.dll";

        // ---- 源格式标签（与 celeste_core.h 的 CELESTE_FMT_* 一一对应）----
        public const uint FmtPcm16 = 1;      // 2 字节/样本 LE
        public const uint FmtPcm24 = 2;      // 3 字节/样本 LE 紧密排列
        public const uint FmtPcm24In32 = 3;  // 4 字节/样本 LE，样本占高 24 位
        public const uint FmtPcm32 = 4;      // 4 字节/样本 LE
        public const uint FmtFloat32 = 5;    // 4 字节/样本 LE IEEE float

        /// <summary>
        /// 打开独占输出并起 C++ 渲染线程。端点容器必须与源布局一致才成功
        /// （不支持的设备 Init 失败，由上层切回自研内核；本内核不做格式转换回退）。
        /// </summary>
        /// <param name="sampleRate">源采样率。</param>
        /// <param name="channels">源声道数（1~2）。</param>
        /// <param name="requestedBufferFrames">请求的设备缓冲帧数（DLL 按设备周期/对齐规则调整）。</param>
        /// <param name="deviceId">设备 ID 宽字符串（IMMDevice::GetId），null/空 = 默认渲染设备。</param>
        /// <param name="sourceFormatTag">源字节布局标签（见 Fmt* 常量），协商只接受同布局端点。</param>
        /// <param name="prefill">起播预填的真实字节（≤一个周期）。</param>
        /// <param name="prefillFrames">预填帧数（字节数须为 prefillFrames×帧字节）。</param>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_core_start(uint sampleRate, uint channels, uint requestedBufferFrames,
            [MarshalAs(UnmanagedType.LPWStr)] string? deviceId, uint sourceFormatTag,
            byte[]? prefill, uint prefillFrames, out IntPtr engineHandle);

        /// <summary>feeder 推送整数字节（交错，含格式标签一致性校验）。ring 满时阻塞（背压，stop 时返回 -3 中断）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_core_write(IntPtr engineHandle, byte[] bytes, uint byteCount, uint formatTag);

        /// <summary>换歌/seek：清空缓冲、灌入新数据、重置统计与 declick 淡入（防爆音）。仅 feeder 线程调用。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_core_replace(IntPtr engineHandle, byte[]? bytes, uint byteCount, uint formatTag);

        /// <summary>标记当前输入到头（存货播完即 drained，不再计欠载）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_core_mark_input_ended(IntPtr engineHandle);

        /// <summary>暂停/恢复：设备继续运转，输出静音（不停设备，避免独占重启爆音）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_core_set_paused(IntPtr engineHandle, int paused);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_core_stats(IntPtr engineHandle, ref NativeCoreStats stats);

        /// <summary>停渲染线程、关设备（可重复调用）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void celeste_core_stop(IntPtr engineHandle);

        /// <summary>释放引擎（stop 之后的收尾；对运行中的句柄调用会先 stop）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void celeste_core_destroy(IntPtr engineHandle);

        /// <summary>
        /// 统计快照。字段布局必须与 celeste_core.h 的 celeste_core_stats_t 逐个对齐（x64）。
        /// 末尾三字段是 I 轮设备侧探针（2026-09-23：应用侧全绿仍偶发卡顿，补设备实际消费轴）：
        /// DevicePosition=设备播放游标（IAudioClock::GetPosition，自流启动累计帧）；
        /// PadMaxFrames/lateWakeups=自上次 Stats() 起的窗口值（内核读走即清零）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct NativeCoreStats
        {
            public uint StructSize;
            public uint SampleRate;
            public uint Channels;
            public int BufferFrames;
            public int ReadyFrames;
            public int CapacityFrames;
            public ulong FramesPlayed;
            public ulong UnderrunCallbacks;
            public ulong UnderrunFrames;
            public int Paused;
            public int Drained;
            public int Failed;
            public int MaxGapMs;
            public ulong SpikeCount;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Format;
            public ulong DevicePosition;
            public int PadMaxFrames;
            public uint LateWakeups;
        }

        /// <summary>取统计快照（结构体大小由本方法填好，DLL 侧据此拒绝旧版越界读）。</summary>
        public static NativeCoreStats Stats(IntPtr handle)
        {
            var st = new NativeCoreStats { StructSize = (uint)Marshal.SizeOf<NativeCoreStats>() };
            if (handle == IntPtr.Zero) return st;
            celeste_core_stats(handle, ref st);
            return st;
        }

        /// <summary>格式标签 → DLL 协商名（与 celeste_core.cpp fmt_tag_name 一致）。</summary>
        public static string TagName(uint tag) => tag switch
        {
            FmtPcm16 => "pcm16",
            FmtPcm24 => "pcm24",
            FmtPcm24In32 => "pcm24in32",
            FmtPcm32 => "pcm32",
            FmtFloat32 => "float32",
            _ => "?",
        };

        /// <summary>start 失败码 → 大白话（给用户看的，不甩错误码）。
        /// 与 celeste_core.cpp 对应：-1=一般失败 / -2=AUDCLNT_E_UNSUPPORTED_FORMAT / -3=E_PENDING 驱动挂住。</summary>
        public static string DescribeStartError(int rc) => rc switch
        {
            -1 => "原生内核初始化失败（设备被其它程序独占？）",
            -2 => "设备不支持该独占格式（此格式本内核不做转换回退，将回退自研内核）",
            -3 => "原生内核启动设备超时（驱动未响应，请重试）",
            -100 => "原生内核内部错误（参数）",
            -101 => "采样率超出内核支持范围",
            -102 => "声道数超出内核支持范围（仅 1/2 声道）",
            -103 => "缓冲参数无效",
            -104 => "源格式不被原生内核支持",
            -105 => "原生内核内部错误（预填数据）",
            -106 => "原生内核内存不足",
            _ => "原生内核启动失败（错误码 " + rc + "）",
        };
    }
}
