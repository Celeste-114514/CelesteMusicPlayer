using System;
using System.Runtime.InteropServices;

namespace CelesteMusicPlayer
{
    /// <summary>
    /// celeste_audio_core.dll（基于 ECHO audio-host 裁剪编译）的 P/Invoke 封装。
    /// 这个 DLL 里跑的是 C++ 原生渲染线程（MMCSS Pro Audio）——.NET GC 的 Stop-The-World
    /// 冻不到它，这是「ECHO 核心」相对自研托管渲染线程的核心差异（卡顿根因治理）。
    ///
    /// 数据流：C# DSP 链 → feeder 线程读成 float[] → celeste_audio_write 灌环形缓冲
    ///          → 设备事件到达，C++ 回调只 memcpy 一整周期 → 转端点格式 → DAC。
    ///
    /// 许可证：DLL 内含 ECHO audio-host / audio-engine 代码，LGPL-3.0（见 native/echo-core/）。
    /// 动态链接 + 用户可替换该 DLL，不对 Celeste（MIT）产生传染。
    /// </summary>
    internal static class EchoCoreAudio
    {
        private const string Dll = "celeste_audio_core.dll";

        /// <summary>打开独占输出并起 C++ 渲染线程。实际协商结果用 <see cref="Stats"/> 读回。</summary>
        /// <param name="sampleRate">源采样率。</param>
        /// <param name="channels">源声道数（1~2）。</param>
        /// <param name="requestedBufferFrames">请求的设备缓冲帧数（DLL 按设备对齐规则调整）。</param>
        /// <param name="deviceId">设备 ID 宽字符串（IMMDevice::GetId），null/空 = 默认渲染设备。</param>
        /// <param name="prefillFrames">预填帧数（≤一个周期的真实音频，起播即出声）。</param>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_audio_start(uint sampleRate, uint channels, uint requestedBufferFrames,
            [MarshalAs(UnmanagedType.LPWStr)] string? deviceId,
            float[]? prefill, uint prefillFrames, out IntPtr engineHandle);

        /// <summary>feeder 推送交错 float PCM。环形缓冲满时阻塞（背压，stop 时返回 -3 中断）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_audio_write(IntPtr engineHandle, float[] interleaved, uint frames);

        /// <summary>换歌/seek：清空缓冲、灌入新数据、重置统计与 declick 渐变（防爆音）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_audio_replace(IntPtr engineHandle, float[]? interleaved, uint frames);

        /// <summary>标记当前输入到头（存货播完即 drained，不再计欠载）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_audio_mark_input_ended(IntPtr engineHandle);

        /// <summary>暂停/恢复：设备继续运转，ring 输出静音（不停设备，避免独占重启爆音）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_audio_set_paused(IntPtr engineHandle, int paused);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int celeste_audio_stats(IntPtr engineHandle, ref CelesteAudioStats stats);

        /// <summary>停渲染线程、关设备（可重复调用）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void celeste_audio_stop(IntPtr engineHandle);

        /// <summary>释放引擎（stop 之后的收尾）。</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void celeste_audio_destroy(IntPtr engineHandle);

        /// <summary>统计快照。字段布局必须与 celeste_bridge.cpp 的 celeste_audio_stats_t 逐个对齐。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct CelesteAudioStats
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
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Format;
        }

        /// <summary>取统计快照（结构体大小由本方法填好，DLL 侧据此拒绝旧版越界读）。</summary>
        public static CelesteAudioStats Stats(IntPtr handle)
        {
            var st = new CelesteAudioStats { StructSize = (uint)Marshal.SizeOf<CelesteAudioStats>() };
            if (handle == IntPtr.Zero) return st;
            celeste_audio_stats(handle, ref st);
            return st;
        }
    }
}
