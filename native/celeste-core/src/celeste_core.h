// celeste_core.h — Celeste 自研原生音频内核（PCM WASAPI 独占输出）
//
// 许可：MIT（与 Celeste 主项目一致）。本工程全新编写，
// 不派生 ECHO audio-host（LGPL，另目录 native/echo-core/）任何代码；
// 架构参照物：ECHO 的 C ABI 外形 + 自研 NativeWasapiExclusiveOut 的
// 调度修复（MMCSS CRITICAL + 1ms 定时器 + 轮询补货 + C3 护栏）。
//
// 设计目标（为什么要有这个 DLL）：
//   .NET GC 的 Stop-The-World 会冻住进程内所有托管线程——C# 渲染线程
//   优先级再高也躲不掉（用户实机 3/3 尖峰伴随 gen2 GC 实锤）。本内核把
//   渲染线程搬进 C++ 原生线程（MMCSS Pro Audio + CRITICAL 频段内优先级），
//   GC 永远碰不到它；C# 侧只做 feeder（读链→灌 ring），被冻住时 ring 里
//   1.5s 存货继续供血。
//
// 数据链：源 → …（C# DSP 链）→ feeder 4096帧/块 → 非托管 ring（1.5s 容量）
//          → 原生渲染线程（轮询补货，memcpy 直通同布局端点）→ DAC。
//          DSP 全关时整数字节从 feeder 到 DAC 不经任何 float 中转（bit-perfect）。
//
// 协商口径（决策 2：C# 决策、原生执行）：C# 决定采样率/声道/缓冲/源格式标签，
// 原生只按固定策略执行——端点容器必须与源布局一致（16/24/32bit 或 float32），
// 否则 Init 失败（上层切回自研内核），不做跨格式转换（转换是阶段 3 的事）。
#pragma once

#ifdef _WIN32

#include <cstdint>

// 源/链输出格式标签：C# feeder 推送的字节布局（write 带格式标签做一致性校验）。
#define CELESTE_FMT_PCM16     1  // 2 字节/样本，LE
#define CELESTE_FMT_PCM24     2  // 3 字节/样本，LE，紧密排列
#define CELESTE_FMT_PCM24IN32 3  // 4 字节/样本，LE，样本占高 24 位
#define CELESTE_FMT_PCM32     4  // 4 字节/样本，LE
#define CELESTE_FMT_FLOAT32   5  // 4 字节/样本，LE，IEEE float

// 统计快照。字段布局必须与 C# NativeCoreAudio.NativeCoreStats 逐个对齐。
// 口径：
//   framesPlayed/underrunCallbacks/underrunFrames = 累计值（C# 侧取差分）；
//   maxGapMs/spikeCount/padMaxFrames/lateWakeups = 自上次 celeste_core_stats() 起的
//     窗口值（读走即清零）；
//   其余为即时状态。
struct celeste_core_stats_t {
    uint32_t structSize;        // = sizeof(本结构体)，前向兼容防线
    uint32_t sampleRate;        // 设备端点实际采样率
    uint32_t channels;          // 声道数
    int32_t  bufferFrames;      // 设备周期帧数（独占模式下 = 缓冲）
    int32_t  readyFrames;       // ring 当前存货（帧）
    int32_t  capacityFrames;    // ring 容量（帧）
    uint64_t framesPlayed;      // 已播帧（replace 后归零重计）
    uint64_t underrunCallbacks; // 设备要数据而 ring 已空的次数（真欠载硬指标）
    uint64_t underrunFrames;    // 欠载补的静音帧数
    int32_t  paused;            // set_paused 状态（0/1）
    int32_t  drained;           // 输入已结束且 ring 播空（这一曲真放完了）
    int32_t  failed;            // 渲染线程异常退出（设备失效/驱动异常）
    int32_t  maxGapMs;          // 窗口内相邻两次补货最大间隔（毫秒；卡顿硬指标）
    uint64_t spikeCount;        // 窗口内间隔尖峰次数（> 3×轮询间隔）
    char     format[32];        // 端点容器名：pcm16/pcm24in32/pcm24/pcm32/float32
    // ---- I 轮设备侧探针（2026-09-23：应用侧全绿仍偶发卡顿，补设备实际消费轴）----
    // 三者回答同一个问题：设备到底有没有在平稳消费我们写进去的数据。
    uint64_t devicePosition;    // 设备播放游标（IAudioClient::GetPosition，自流启动累计帧，即时值）
    int32_t  padMaxFrames;      // 窗口内事件唤醒时 GetCurrentPadding 最大值（应恒 0；>0 = 事件早到/陈旧信号）
    uint32_t lateWakeups;       // 窗口内唤醒间隔 > 1.5×缓冲周期 的次数（线程醒晚 = 设备已断供）
};

#endif // _WIN32
