// celeste_bridge.cpp — Celeste 音频内核 C ABI 桥（基于 ECHO audio-host 裁剪）
//
// 目标：把 ECHO 已验证的 WASAPI 独占渲染 + 环形缓冲组装成一个 C# 可 P/Invoke 的 DLL。
//
// 设计要点（对应 Celeste 独占卡顿的三个根因）：
//   1. 渲染线程是 C++ 原生线程（MMCSS Pro Audio），.NET GC 的 Stop-The-World 永远碰不到它；
//   2. 渲染回调每次只做 memcpy：数据由 C# feeder 提前 push 进环形缓冲，设备事件到来时
//      renderInterleaved 从缓冲取走固定一整周期（与 ECHO 完全一致的节奏）；
//   3. 统计口子（水位/欠载/已播）原样透出，诊断面板直接读。
//
// 数据流：C# DSP 链 → float[]（P/Invoke 参数）→ NativeFifo 环形缓冲
//          ↓ 设备事件
//          emit fixed period（memcpy）→ convert_float_to_endpoint → DAC
//
// bit-perfect 说明：整条链路是 float32。16bit/24bit 整数 PCM 与 float32 之间的转换
// 是精确可逆的（float32 有 24 位有效精度，24bit PCM 取值范围 ±2^23），无数值损失；
// 设备格式按 ECHO 的顺序协商（IEEE_FLOAT → PCM24-in-32 → PCM16 → PCM32）。
// 32bit 整数源请先在 C# 侧降级或改走其它输出路径（后续版本加格式偏好参数）。

#include "wasapi_exclusive.h"
#include "PcmRingAudioSource.h"

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>

namespace {

struct celeste_engine {
    // 常驻处理器（引用注入给 ring；默认全旁路：EQ 无频段=恒等，声道平衡单位增益）。
    // 真正的 EQ/限幅/软音量在 C# DSP 链里处理，这里只借 ECHO 的容器。
    echo::EqProcessor eq;
    echo::ChannelBalanceProcessor balance;

    std::unique_ptr<PcmRingAudioSource> ring;
    wasapi_exclusive_runtime* runtime = nullptr;

    uint32_t sampleRate = 0;
    uint32_t channels = 0;
    int bufferFrames = 0;   // 设备实际周期帧数（ready.bufferFrameCount）
    int capacityFrames = 0; // 环形缓冲容量（帧）

    std::atomic<bool> started { false };

    // 起播预缓冲：等 feeder 灌满一个周期量才允许出声，避免开录瞬间从零起跳
    int startupPrebufferFrames = 0;

    // 设备端点格式名（ready.format 拷贝，统计口子透出给 C# 组链路描述）
    char endpointFormat[32] = { 0 };
};

// WASAPI 渲染回调（设备线程）：只 memcpy，不分配，不读盘，不抛
unsigned int celeste_render_callback(void* userData, float* output,
                                      unsigned int frameCount, unsigned int channels)
{
    auto* engine = static_cast<celeste_engine*>(userData);
    if (engine == nullptr || engine->ring == nullptr)
    {
        if (output != nullptr && frameCount > 0)
            std::memset(output, 0,
                static_cast<size_t>(frameCount) * (channels ? channels : 1) * sizeof(float));
        return 0;
    }
    engine->ring->renderInterleaved(output, frameCount, channels);
    return 0;
}

void celeste_notification_callback(void* userData, const wasapi_host_notification* notification)
{
    (void)userData;
    if (notification == nullptr)
        return;
    if (notification->event != nullptr)
        std::fprintf(stderr, "[celeste-audio-core] host event: %s (code=%u)\n",
                     notification->event, notification->code);
}

int start_engine(celeste_engine* engine, uint32_t sampleRate, uint32_t channels,
                 uint32_t requestedBufferFrames, const wchar_t* deviceId,
                 const float* prefill, uint32_t prefillFrames)
{
    // 环形缓冲容量 = 一个设备周期 + 500ms 存货（吸收 GC 停顿/读源抖动）
    const int reserveFrames = static_cast<int>(
        static_cast<double>(sampleRate) * 0.5 + 0.5);
    const int periodFrames = static_cast<int>(requestedBufferFrames);
    engine->capacityFrames = periodFrames + reserveFrames;
    engine->startupPrebufferFrames = periodFrames; // 灌满一个周期即开播

    engine->ring.reset(new PcmRingAudioSource(
        static_cast<int>(channels),
        engine->capacityFrames,
        engine->startupPrebufferFrames,
        2000, // 等预缓冲最多 2 秒（feeder 被 GC 冻住也不至于永远静音）
        1.0,  // gain=1：音量完全交给 C# DSP 链，这里零干预
        engine->eq,
        engine->balance));

    // 开一个新会话。PlaybackSession 的构造态是 inputEnded=true（=没有会话），
    // 不 begin 的话：underrun 计数条件(!isInputEnded && hasAudio)恒假、isDrained 恒真，
    // 统计口子会失了真。begin 后 push 会自动 markHasAudio。
    engine->ring->beginSession();

    // 先备货、再启动设备：预填数据由 C# feeder 在调用本函数前读好（≤一个周期）。
    // 设备启动时首缓冲直接填真实音频（wasapi_exclusive.cpp 的 priming 改动），
    // 起播即出声，不再先播一整块静音。
    if (prefill != nullptr && prefillFrames > 0)
        engine->ring->push(prefill, static_cast<int>(prefillFrames));

    // priming 回调跑在 wasapi_exclusive_start 内部（设备起来之前），
    // 必须提前把 render scratch 准备好，否则 renderInterleaved 直接返回静音。
    // 按请求周期预备；设备对齐后的真实周期 ≤ 该值（renderInterleaved 内部分块循环自适应）。
    engine->ring->prepareForNativeRender(periodFrames, static_cast<double>(sampleRate));

    char error[512] = { 0 };
    wasapi_exclusive_ready_info ready;
    std::memset(&ready, 0, sizeof(ready));

    const int rc = wasapi_exclusive_start(
        nullptr, // 设备名：不用（C# 传的是设备 ID，见下一个参数）
        -1,
        deviceId, // 空 = 默认渲染设备（用户单 DAC 场景）
        sampleRate,
        channels,
        requestedBufferFrames,
        celeste_render_callback,
        engine,
        celeste_notification_callback,
        engine,
        &engine->runtime,
        &ready,
        error,
        sizeof(error));

    if (rc != 0 || engine->runtime == nullptr)
    {
        std::fprintf(stderr, "[celeste-audio-core] start failed rc=%d err=%s\n",
                     rc, error[0] != '\0' ? error : "(no detail)");
        engine->ring.reset();
        return rc != 0 ? rc : -1;
    }

    // 把设备真实周期告诉 ring（内部缓冲按回调最大帧数预备，避免回调内分配）
    engine->ring->prepareForNativeRender(
        static_cast<int>(ready.bufferFrameCount),
        static_cast<double>(ready.sampleRate > 0 ? ready.sampleRate : sampleRate));
    engine->bufferFrames = static_cast<int>(ready.bufferFrameCount);
    engine->sampleRate = ready.sampleRate > 0 ? ready.sampleRate : sampleRate;
    engine->channels = ready.channels > 0 ? ready.channels : channels;
    engine->started.store(true, std::memory_order_release);

    // 设备端点格式名透出给统计口子（C# 组链路描述 + bit-perfect 徽标用）。
    // 之前只打进 stderr 日志、endpointFormat 永远是空 → stats 返回 "?" →
    // C# 描述显示"设备 ?"且误判"已降级"，徽标还被源的位深蒙混过关照样亮绿
    // （2026-09-22 用户实测 24bit/96kHz）。wasapi_exclusive_start 成功路径
    // 必填 ready.format（wasapi_exclusive.cpp result=0 前），空值只可能是防御。
    std::snprintf(engine->endpointFormat, sizeof(engine->endpointFormat), "%s",
                  ready.format[0] != '\0' ? ready.format : "?");

    std::fprintf(stderr,
                 "[celeste-audio-core] exclusive started: rate=%u ch=%u bufferFrames=%d "
                 "capacityFrames=%d endpointFormat=%s hwRate=%u\n",
                 engine->sampleRate, engine->channels, engine->bufferFrames,
                 engine->capacityFrames, ready.format, ready.hardwareSampleRate);
    return 0;
}

} // namespace

// ---------------------------------------------------------------------------
// C ABI（C# P/Invoke 用）。约定：返回 0=成功，负数=错误码；帧单位一律为"每声道采样点数"。
// ---------------------------------------------------------------------------
extern "C" {

// 打开独占输出并起渲染线程。requestedBufferFrames 会按设备对齐规则调整，
// 实际值通过 celeste_audio_stats 读回。engineHandle 由内部创建。
// deviceId：设备 ID 宽字符串（IMMDevice::GetId），空 = 默认渲染设备。
// prefill/prefillFrames：起播预填的真实音频（≤一个周期，C# feeder 先读好），
//   设备启动时首缓冲直接用它，起播即出声（不填静音）。
__declspec(dllexport) int celeste_audio_start(uint32_t sampleRate, uint32_t channels,
                                              uint32_t requestedBufferFrames,
                                              const wchar_t* deviceId,
                                              const float* prefill, uint32_t prefillFrames,
                                              void** outEngineHandle)
{
    if (outEngineHandle == nullptr)
        return -100;
    *outEngineHandle = nullptr;
    if (sampleRate < 8000 || sampleRate > 768000 || channels < 1 || channels > 8)
        return -101;
    if (requestedBufferFrames == 0)
        return -102;

    auto* engine = new (std::nothrow) celeste_engine();
    if (engine == nullptr)
        return -103;

    const int rc = start_engine(engine, sampleRate, channels, requestedBufferFrames,
                                deviceId, prefill, prefillFrames);
    if (rc != 0)
    {
        delete engine;
        return rc;
    }
    *outEngineHandle = engine;
    return 0;
}

// feeder 推送交错的 float PCM。环形缓冲满时阻塞（背压，最多等到 stop）。
// 返回实际吃进的帧数；0=正常（全部吃进）；负数=错误。
__declspec(dllexport) int celeste_audio_write(void* engineHandle, const float* interleaved,
                                              uint32_t frames)
{
    auto* engine = static_cast<celeste_engine*>(engineHandle);
    if (engine == nullptr || interleaved == nullptr)
        return -1;
    if (engine->ring == nullptr || !engine->started.load(std::memory_order_acquire))
        return -2;
    if (frames == 0)
        return 0;

    const bool complete = engine->ring->push(interleaved, static_cast<int>(frames));
    return complete ? 0 : -3; // -3：stop 已请求，写入被中断
}

// 换歌/ Seek：清空缓冲、灌入新数据、重置统计与 declick 渐变（防爆音）。
// 返回写入的帧数。
__declspec(dllexport) int celeste_audio_replace(void* engineHandle, const float* interleaved,
                                                uint32_t frames)
{
    auto* engine = static_cast<celeste_engine*>(engineHandle);
    if (engine == nullptr)
        return -1;
    if (engine->ring == nullptr || !engine->started.load(std::memory_order_acquire))
        return -2;
    return engine->ring->replaceBufferedAudio(interleaved, static_cast<int>(frames), false);
}

// 标记当前输入到头（剩余存货播完即 drained，不再计欠载）
__declspec(dllexport) int celeste_audio_mark_input_ended(void* engineHandle)
{
    auto* engine = static_cast<celeste_engine*>(engineHandle);
    if (engine == nullptr || engine->ring == nullptr)
        return -1;
    engine->ring->markInputEnded();
    return 0;
}

// 暂停/恢复：设备继续运转，ring 输出静音（不停设备，避免独占重启爆音）
__declspec(dllexport) int celeste_audio_set_paused(void* engineHandle, int paused)
{
    auto* engine = static_cast<celeste_engine*>(engineHandle);
    if (engine == nullptr || engine->ring == nullptr)
        return -1;
    engine->ring->setPaused(paused != 0);
    return 0;
}

// 统计快照。structSize 用于前向兼容（C# 侧传 sizeof 后的值）。
// 注意：结构体名不能与下面的导出函数同名（C++ 里函数会遮蔽类型名，sizeof 会解析失败）。
struct celeste_audio_stats_t
{
    uint32_t structSize;
    uint32_t sampleRate;
    uint32_t channels;
    int32_t bufferFrames;
    int32_t readyFrames;      // 当前存货（帧）
    int32_t capacityFrames;   // 缓冲容量（帧）
    uint64_t framesPlayed;    // 已播帧数
    uint64_t underrunCallbacks;
    uint64_t underrunFrames;
    int32_t paused;
    int32_t drained;
    char format[32];          // 设备端点格式名（float32/pcm24/pcm16/pcm32），C# 组描述用
};

__declspec(dllexport) int celeste_audio_stats(void* engineHandle, celeste_audio_stats_t* out)
{
    auto* engine = static_cast<celeste_engine*>(engineHandle);
    if (engine == nullptr || out == nullptr)
        return -1;
    if (out->structSize < static_cast<uint32_t>(sizeof(celeste_audio_stats_t)))
        return -2; // C# 侧结构体比本 DLL 旧，拒绝读取（防越界)
    if (engine->ring == nullptr)
        return -3;

    out->sampleRate = engine->sampleRate;
    out->channels = engine->channels;
    out->bufferFrames = static_cast<int32_t>(engine->bufferFrames);
    out->readyFrames = static_cast<int32_t>(engine->ring->getReadyFrames());
    out->capacityFrames = static_cast<int32_t>(engine->capacityFrames);
    out->framesPlayed = engine->ring->getFramesPlayed();
    out->underrunCallbacks = engine->ring->getUnderrunCallbacks();
    out->underrunFrames = engine->ring->getUnderrunFrames();
    out->paused = 0; // paused 状态直接读 PcmRingAudioSource.isDrained 之外的内部量，暂不导出
    out->drained = engine->ring->isDrained() ? 1 : 0;
    std::snprintf(out->format, sizeof(out->format), "%s",
                  engine->endpointFormat[0] != '\0' ? engine->endpointFormat : "?");
    return 0;
}

// 停渲染线程、关设备（可重复调用）
__declspec(dllexport) void celeste_audio_stop(void* engineHandle)
{
    auto* engine = static_cast<celeste_engine*>(engineHandle);
    if (engine == nullptr)
        return;
    engine->started.store(false, std::memory_order_release);
    if (engine->ring != nullptr)
        engine->ring->requestStop();
    if (engine->runtime != nullptr)
    {
        wasapi_exclusive_stop(engine->runtime);
        engine->runtime = nullptr;
    }
    engine->ring.reset();
}

// 释放引擎（stop 之后的收尾；直接对运行中的句柄调用会先 stop）
__declspec(dllexport) void celeste_audio_destroy(void* engineHandle)
{
    auto* engine = static_cast<celeste_engine*>(engineHandle);
    if (engine == nullptr)
        return;
    if (engine->runtime != nullptr)
        celeste_audio_stop(engineHandle);
    delete engine;
}

} // extern "C"
