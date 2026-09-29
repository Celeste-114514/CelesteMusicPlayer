// celeste_dsp_bridge.cpp
// C ABI 桥接层：把 ECHO 新版 DSP 机架（audio-engine/，照搬未改）导出给 C# P/Invoke。
//
// 设计约束：
//  - 块处理走 persistent planar FloatAudioBuffer（零每块分配）；
//  - 交错 float32 <-> planar 转换在桥接层完成（C# 侧只认交错）；
//  - 所有设置走 ECHO 原子接口（UI 线程写、音频线程读，无锁，与 ECHO 原设计一致）；
//  - 不允许 C++ 异常穿过 ABI 边界（统一 try/catch 收口）；
//  - 单实例：播放器同时只有一条 DSP 链。销毁前 C# 必须保证音频线程已停止。
//
// 错误码约定：0=成功；-1=引擎未创建/未准备；-2=参数非法；-3=设置被拒；-4=IR 校验失败；-100=构造异常。

#include "audio-engine/ChannelBalanceProcessor.h"
#include "audio-engine/CompressorProcessor.h"
#include "audio-engine/ConvolutionProcessor.h"
#include "audio-engine/DspChain.h"
#include "audio-engine/DspHeadroomProcessor.h"
#include "audio-engine/DspRackOrder.h"
#include "audio-engine/DspSafetyLimiter.h"
#include "audio-engine/EqPresetStore.h"
#include "audio-engine/EqProcessor.h"
#include "audio-engine/EqTypes.h"
#include "audio-engine/LevelMeterProcessor.h"
#include "audio-engine/PlaybackRateProcessor.h"
#include "audio-engine/ReplayGainProcessor.h"
#include "audio-engine/SpatialDspProcessor.h"
#include "audio-engine/TruePeakLimiterProcessor.h"
#include "audio-engine/buffer.h"

#include <algorithm>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

namespace
{
constexpr int kMaxChannels = 8;
constexpr int kMaxBlockFrames = 16384;

int copyUtf8(const std::string& text, char* out, int cap) noexcept
{
    if (out == nullptr || cap <= 0)
        return -1;
    const int needed = static_cast<int>(text.size()) + 1;
    if (needed > cap)
        return -1;
    std::memcpy(out, text.c_str(), static_cast<std::size_t>(needed));
    return needed - 1;
}

struct DspEngine
{
    // 声明顺序即构造顺序：chain 持有以上所有处理器的引用，必须最后构造。
    echo::EqProcessor eq;
    echo::ConvolutionProcessor convolution;
    echo::ChannelBalanceProcessor channelBalance;
    echo::DspHeadroomProcessor headroom;
    echo::ReplayGainProcessor replayGain;
    echo::CompressorProcessor compressor;
    echo::SpatialDspProcessor spatial;
    echo::PlaybackRateProcessor rate;
    echo::LevelMeterProcessor meter;
    echo::DspRackOrder rack;
    echo::DspChain chain;

    echo::FloatAudioBuffer buffer; // persistent planar（channels x maxBlockFrames）

    double sampleRate = 48000.0;
    int channels = 2;
    int maxBlockFrames = 0;
    bool prepared = false;

    std::vector<echo::EqPreset> builtins;
    bool builtinsLoaded = false;

    DspEngine()
        : chain(eq, convolution, channelBalance, headroom, replayGain,
                compressor, spatial, rate, meter, &rack)
    {
    }
};

std::unique_ptr<DspEngine> g_engine;

DspEngine* requireEngine() noexcept
{
    return g_engine.get();
}

void ensureBuiltins(DspEngine& engine)
{
    if (! engine.builtinsLoaded)
    {
        engine.builtins = echo::EqPresetStore::createBuiltInPresets();
        engine.builtinsLoaded = true;
    }
}

} // namespace

// ─────────────────────────────────────────────────────────────────────────────
// 生命周期
// ─────────────────────────────────────────────────────────────────────────────

extern "C" {

__declspec(dllexport) int celeste_dsp_create(double sampleRate, int maxBlockFrames, int channels)
{
    if (! (sampleRate >= 8000.0) || ! (sampleRate <= 768000.0))
        return -2;
    if (maxBlockFrames <= 0 || maxBlockFrames > kMaxBlockFrames)
        return -2;
    if (channels <= 0 || channels > kMaxChannels)
        return -2;

    try
    {
        auto engine = std::make_unique<DspEngine>();
        engine->sampleRate = sampleRate;
        engine->channels = channels;
        engine->maxBlockFrames = maxBlockFrames;
        engine->buffer.setSize(channels, maxBlockFrames);
        engine->chain.prepare(sampleRate, maxBlockFrames, channels);
        engine->prepared = true;
        g_engine = std::move(engine);
        return 0;
    }
    catch (...)
    {
        g_engine.reset();
        return -100;
    }
}

__declspec(dllexport) void celeste_dsp_destroy()
{
    g_engine.reset();
}

/// 清空所有处理器状态（滤波器历史/延迟线/卷积历史），设置保留。seek/换曲时调用。
__declspec(dllexport) void celeste_dsp_reset()
{
    if (auto* engine = requireEngine())
        engine->chain.reset();
}

/// 交错 float32 原地处理。frames <= create 时的 maxBlockFrames。
/// 返回处理的帧数；负值=错误（-1 未创建 / -2 参数非法）。
__declspec(dllexport) int celeste_dsp_process(float* interleaved, int frames)
{
    auto* engine = requireEngine();
    if (engine == nullptr || ! engine->prepared)
        return -1;
    if (interleaved == nullptr || frames <= 0 || frames > engine->maxBlockFrames)
        return -2;

    const int channels = engine->channels;
    auto& buffer = engine->buffer;

    for (int ch = 0; ch < channels; ++ch)
    {
        float* dst = buffer.getWritePointer(ch);
        const float* src = interleaved + ch;
        for (int i = 0; i < frames; ++i)
            dst[i] = src[static_cast<std::size_t>(i) * static_cast<std::size_t>(channels)];
    }

    engine->chain.processBlock(buffer, 0, frames, true);

    for (int ch = 0; ch < channels; ++ch)
    {
        const float* src = buffer.getReadPointer(ch);
        float* dst = interleaved + ch;
        for (int i = 0; i < frames; ++i)
            dst[static_cast<std::size_t>(i) * static_cast<std::size_t>(channels)] = src[i];
    }

    return frames;
}

// ─────────────────────────────────────────────────────────────────────────────
// EQ（31 槽参数化；与 ECHO 一致：6 种滤波器、每段启停、preamp、25ms 平滑）
// ─────────────────────────────────────────────────────────────────────────────

__declspec(dllexport) int celeste_dsp_eq_set_enabled(int enabled)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    engine->eq.setEnabled(enabled != 0);
    return 0;
}

__declspec(dllexport) int celeste_dsp_eq_set_preamp(float preampDb)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    engine->eq.setPreampDb(preampDb);
    return 0;
}

__declspec(dllexport) int celeste_dsp_eq_set_band(int index, float gainDb, float freqHz,
                                                  float q, int filterType, int bandEnabled)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (index < 0 || index >= echo::eqBandCount)
        return -2;

    auto& eq = engine->eq;
    if (! eq.setBandGainDb(index, gainDb))
        return -3;
    if (! eq.setBandFrequencyHz(index, freqHz))
        return -3;
    if (! eq.setBandQ(index, q))
        return -3;
    eq.setBandFilterType(index, echo::normalizeEqFilterType(filterType));
    eq.setBandEnabled(index, bandEnabled != 0);
    return 0;
}

/// 一次性下发全部 31 槽 + preamp + 总开关。数组长度均须为 31。
__declspec(dllexport) int celeste_dsp_eq_set_all(const float* gains, const float* freqs,
                                                 const float* qs, const int* types,
                                                 const int* bandEnabled, float preampDb,
                                                 int eqEnabled)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (gains == nullptr || freqs == nullptr || qs == nullptr
        || types == nullptr || bandEnabled == nullptr)
        return -2;

    auto& eq = engine->eq;
    for (int i = 0; i < echo::eqBandCount; ++i)
    {
        if (! eq.setBandGainDb(i, gains[i]))
            return -3;
        if (! eq.setBandFrequencyHz(i, freqs[i]))
            return -3;
        if (! eq.setBandQ(i, qs[i]))
            return -3;
        eq.setBandFilterType(i, echo::normalizeEqFilterType(types[i]));
        eq.setBandEnabled(i, bandEnabled[i] != 0);
    }
    eq.setPreampDb(preampDb);
    eq.setEnabled(eqEnabled != 0);
    return 0;
}

/// 回读 EQ 当前状态（原子目标值；平滑中的瞬时值不回读）。用于持久化与冒烟验证。
__declspec(dllexport) int celeste_dsp_eq_get_all(float* gains, float* freqs, float* qs,
                                                 int* types, int* bandEnabled,
                                                 float* preampDb, int* eqEnabled)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (gains == nullptr || freqs == nullptr || qs == nullptr || types == nullptr
        || bandEnabled == nullptr || preampDb == nullptr || eqEnabled == nullptr)
        return -2;

    const echo::EqState state = engine->eq.getState();
    for (int i = 0; i < echo::eqBandCount; ++i)
    {
        gains[i] = state.bandGainsDb[static_cast<std::size_t>(i)];
        freqs[i] = state.bandFrequenciesHz[static_cast<std::size_t>(i)];
        qs[i] = state.bandQ[static_cast<std::size_t>(i)];
        types[i] = static_cast<int>(state.bandFilterTypes[static_cast<std::size_t>(i)]);
        bandEnabled[i] = state.bandEnabled[static_cast<std::size_t>(i)] ? 1 : 0;
    }
    *preampDb = state.preampDb;
    *eqEnabled = state.enabled ? 1 : 0;
    return 0;
}

__declspec(dllexport) int celeste_dsp_eq_reset_flat()
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    engine->eq.resetFlat();
    return 0;
}

/// 内置 EQ 预设（ECHO 16 个）数量。
__declspec(dllexport) int celeste_dsp_eq_builtin_count()
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    ensureBuiltins(*engine);
    return static_cast<int>(engine->builtins.size());
}

/// 取第 index 个内置预设。bands 恒为 31 项（ECHO 构造时补齐）；nameOut 可空。
/// 返回 0；负值=错误。
__declspec(dllexport) int celeste_dsp_eq_builtin_get(int index, char* nameOut, int nameCap,
                                                     float* preampDbOut,
                                                     float* gains, float* freqs, float* qs,
                                                     int* types, int* bandEnabled)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    ensureBuiltins(*engine);
    if (index < 0 || index >= static_cast<int>(engine->builtins.size()))
        return -2;
    if (gains == nullptr || freqs == nullptr || qs == nullptr
        || types == nullptr || bandEnabled == nullptr)
        return -2;

    const auto& preset = engine->builtins[static_cast<std::size_t>(index)];
    const std::size_t bandCount = std::min<std::size_t>(preset.bands.size(), echo::eqBandCount);
    for (std::size_t i = 0; i < static_cast<std::size_t>(echo::eqBandCount); ++i)
    {
        if (i < bandCount)
        {
            const auto& band = preset.bands[i];
            gains[i] = band.gainDb;
            freqs[i] = band.frequencyHz;
            qs[i] = band.q;
            types[i] = static_cast<int>(band.filterType);
            bandEnabled[i] = band.enabled ? 1 : 0;
        }
        else
        {
            gains[i] = 0.0f;
            freqs[i] = echo::eqFrequenciesHz[i];
            qs[i] = 1.0f;
            types[i] = static_cast<int>(echo::EqFilterType::Peaking);
            bandEnabled[i] = 0;
        }
    }
    if (preampDbOut != nullptr)
        *preampDbOut = preset.preampDb;
    if (nameOut != nullptr && nameCap > 0)
        copyUtf8(preset.name, nameOut, nameCap);
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// 压缩器（前馈检测 + 软拐点 + 干湿混合）
// ─────────────────────────────────────────────────────────────────────────────

__declspec(dllexport) int celeste_dsp_compressor_set(int enabled, float thresholdDb, float ratio,
                                                     float attackMs, float releaseMs, float kneeDb,
                                                     float makeupDb, float mix)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;

    echo::CompressorState state;
    state.enabled = enabled != 0;
    state.thresholdDb = thresholdDb;
    state.ratio = ratio;
    state.attackMs = attackMs;
    state.releaseMs = releaseMs;
    state.kneeDb = kneeDb;
    state.makeupDb = makeupDb;
    state.mix = mix;
    engine->compressor.setState(state);
    return 0;
}

__declspec(dllexport) int celeste_dsp_compressor_get(int* enabled, float* thresholdDb,
                                                     float* ratio, float* attackMs,
                                                     float* releaseMs, float* kneeDb,
                                                     float* makeupDb, float* mix)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (enabled == nullptr || thresholdDb == nullptr || ratio == nullptr || attackMs == nullptr
        || releaseMs == nullptr || kneeDb == nullptr || makeupDb == nullptr || mix == nullptr)
        return -2;

    const echo::CompressorState state = engine->compressor.getState();
    *enabled = state.enabled ? 1 : 0;
    *thresholdDb = state.thresholdDb;
    *ratio = state.ratio;
    *attackMs = state.attackMs;
    *releaseMs = state.releaseMs;
    *kneeDb = state.kneeDb;
    *makeupDb = state.makeupDb;
    *mix = state.mix;
    return 0;
}

__declspec(dllexport) float celeste_dsp_compressor_gr_db()
{
    auto* engine = requireEngine();
    return engine == nullptr ? 0.0f : engine->compressor.gainReductionDb();
}

// ─────────────────────────────────────────────────────────────────────────────
// 空间域：Crossfeed / StereoField / ChannelMatrix
// ─────────────────────────────────────────────────────────────────────────────

__declspec(dllexport) int celeste_dsp_crossfeed_set(int enabled, float amount, float cutoffHz)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;

    echo::CrossfeedState state;
    state.enabled = enabled != 0;
    state.amount = amount;
    state.cutoffHz = cutoffHz;
    engine->spatial.setCrossfeedState(state);
    return 0;
}

__declspec(dllexport) int celeste_dsp_crossfeed_get(int* enabled, float* amount, float* cutoffHz)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (enabled == nullptr || amount == nullptr || cutoffHz == nullptr)
        return -2;

    const echo::CrossfeedState state = engine->spatial.getCrossfeedState();
    *enabled = state.enabled ? 1 : 0;
    *amount = state.amount;
    *cutoffHz = state.cutoffHz;
    return 0;
}

__declspec(dllexport) int celeste_dsp_stereofield_set(int enabled, float width,
                                                      float centerGainDb, float sideGainDb)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;

    echo::StereoFieldState state;
    state.enabled = enabled != 0;
    state.width = width;
    state.centerGainDb = centerGainDb;
    state.sideGainDb = sideGainDb;
    engine->spatial.setStereoFieldState(state);
    return 0;
}

__declspec(dllexport) int celeste_dsp_stereofield_get(int* enabled, float* width,
                                                      float* centerGainDb, float* sideGainDb)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (enabled == nullptr || width == nullptr || centerGainDb == nullptr || sideGainDb == nullptr)
        return -2;

    const echo::StereoFieldState state = engine->spatial.getStereoFieldState();
    *enabled = state.enabled ? 1 : 0;
    *width = state.width;
    *centerGainDb = state.centerGainDb;
    *sideGainDb = state.sideGainDb;
    return 0;
}

__declspec(dllexport) int celeste_dsp_matrix_set(int enabled, float leftToLeft, float rightToLeft,
                                                 float leftToRight, float rightToRight)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;

    echo::ChannelMatrixState state;
    state.enabled = enabled != 0;
    state.leftToLeft = leftToLeft;
    state.rightToLeft = rightToLeft;
    state.leftToRight = leftToRight;
    state.rightToRight = rightToRight;
    engine->spatial.setChannelMatrixState(state);
    return 0;
}

__declspec(dllexport) int celeste_dsp_matrix_get(int* enabled, float* leftToLeft,
                                                 float* rightToLeft, float* leftToRight,
                                                 float* rightToRight)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (enabled == nullptr || leftToLeft == nullptr || rightToLeft == nullptr
        || leftToRight == nullptr || rightToRight == nullptr)
        return -2;

    const echo::ChannelMatrixState state = engine->spatial.getChannelMatrixState();
    *enabled = state.enabled ? 1 : 0;
    *leftToLeft = state.leftToLeft;
    *rightToLeft = state.rightToLeft;
    *leftToRight = state.leftToRight;
    *rightToRight = state.rightToRight;
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// 声道平衡（balance / 三段频补 / 延迟 / 交换 / mono / 反相 / 恒功率）
// ─────────────────────────────────────────────────────────────────────────────

__declspec(dllexport) int celeste_dsp_balance_set(int enabled, float balance, float leftGainDb,
                                                  float rightGainDb,
                                                  const float* leftBandGains,
                                                  const float* rightBandGains,
                                                  float leftDelayMs, float rightDelayMs,
                                                  int swapLeftRight, int monoMode,
                                                  int invertLeft, int invertRight,
                                                  int constantPower)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (leftBandGains == nullptr || rightBandGains == nullptr)
        return -2;

    int mono = monoMode;
    if (mono < 0)
        mono = 0;
    if (mono > 3)
        mono = 3;

    echo::ChannelBalanceState state;
    state.enabled = enabled != 0;
    state.balance = balance;
    state.leftGainDb = leftGainDb;
    state.rightGainDb = rightGainDb;
    for (int i = 0; i < echo::channelBalanceBandCount; ++i)
    {
        state.leftBandGainsDb[static_cast<std::size_t>(i)] = leftBandGains[i];
        state.rightBandGainsDb[static_cast<std::size_t>(i)] = rightBandGains[i];
    }
    state.leftDelayMs = leftDelayMs;
    state.rightDelayMs = rightDelayMs;
    state.swapLeftRight = swapLeftRight != 0;
    state.monoMode = static_cast<echo::ChannelBalanceMonoMode>(mono);
    state.invertLeft = invertLeft != 0;
    state.invertRight = invertRight != 0;
    state.constantPower = constantPower != 0;
    engine->channelBalance.setState(state);
    return 0;
}

__declspec(dllexport) int celeste_dsp_balance_get(int* enabled, float* balance, float* leftGainDb,
                                                  float* rightGainDb, float* leftBandGains,
                                                  float* rightBandGains, float* leftDelayMs,
                                                  float* rightDelayMs, int* swapLeftRight,
                                                  int* monoMode, int* invertLeft,
                                                  int* invertRight, int* constantPower)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (enabled == nullptr || balance == nullptr || leftGainDb == nullptr
        || rightGainDb == nullptr || leftBandGains == nullptr || rightBandGains == nullptr
        || leftDelayMs == nullptr || rightDelayMs == nullptr || swapLeftRight == nullptr
        || monoMode == nullptr || invertLeft == nullptr || invertRight == nullptr
        || constantPower == nullptr)
        return -2;

    const echo::ChannelBalanceState state = engine->channelBalance.getState();
    *enabled = state.enabled ? 1 : 0;
    *balance = state.balance;
    *leftGainDb = state.leftGainDb;
    *rightGainDb = state.rightGainDb;
    for (int i = 0; i < echo::channelBalanceBandCount; ++i)
    {
        leftBandGains[i] = state.leftBandGainsDb[static_cast<std::size_t>(i)];
        rightBandGains[i] = state.rightBandGainsDb[static_cast<std::size_t>(i)];
    }
    *leftDelayMs = state.leftDelayMs;
    *rightDelayMs = state.rightDelayMs;
    *swapLeftRight = state.swapLeftRight ? 1 : 0;
    *monoMode = static_cast<int>(state.monoMode);
    *invertLeft = state.invertLeft ? 1 : 0;
    *invertRight = state.invertRight ? 1 : 0;
    *constantPower = state.constantPower ? 1 : 0;
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Headroom / ReplayGain / 安全限幅器（进程级开关）/ 上游 PCM 标志
// ─────────────────────────────────────────────────────────────────────────────

__declspec(dllexport) int celeste_dsp_headroom_set_db(float headroomDb)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    engine->headroom.setHeadroomDb(headroomDb);
    return 0;
}

__declspec(dllexport) float celeste_dsp_headroom_get_db()
{
    auto* engine = requireEngine();
    return engine == nullptr ? 0.0f : engine->headroom.getHeadroomDb();
}

__declspec(dllexport) int celeste_dsp_rg_set(float trackGainDb, float albumGainDb, float peak,
                                             int mode, float preampDb, int preventClipping)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;

    echo::ReplayGainConfig config;
    config.trackGainDb = trackGainDb;
    config.albumGainDb = albumGainDb;
    config.peak = peak;
    config.mode = mode;
    config.preampDb = preampDb;
    config.preventClipping = preventClipping != 0;
    engine->replayGain.setConfig(config);
    return 0;
}

__declspec(dllexport) int celeste_dsp_rg_get(float* trackGainDb, float* albumGainDb, float* peak,
                                             int* mode, float* preampDb, int* preventClipping)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (trackGainDb == nullptr || albumGainDb == nullptr || peak == nullptr || mode == nullptr
        || preampDb == nullptr || preventClipping == nullptr)
        return -2;

    const echo::ReplayGainConfig config = engine->replayGain.getConfig();
    *trackGainDb = config.trackGainDb;
    *albumGainDb = config.albumGainDb;
    *peak = config.peak;
    *mode = config.mode;
    *preampDb = config.preampDb;
    *preventClipping = config.preventClipping ? 1 : 0;
    return 0;
}

__declspec(dllexport) float celeste_dsp_rg_applied_db()
{
    auto* engine = requireEngine();
    return engine == nullptr ? 0.0f : engine->replayGain.getAppliedGainDb();
}

/// 安全限幅器是 ECHO 的进程级静态开关，默认开。
__declspec(dllexport) int celeste_dsp_safety_set_enabled(int enabled)
{
    echo::setDspSafetyLimiterEnabled(enabled != 0);
    return 0;
}

__declspec(dllexport) int celeste_dsp_safety_get_enabled()
{
    return echo::isDspSafetyLimiterEnabled() ? 1 : 0;
}

/// ECHO 的原生 PCM 处理标志：置 1 时真峰值限幅天花板从 0dB 收紧到 -1dB。
/// Celeste 侧：独占输出做了 SRC 升频/降混时置 1，否则 0。
__declspec(dllexport) int celeste_dsp_set_upstream_active(int active)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    engine->chain.setUpstreamPcmProcessingActive(active != 0);
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// 机架顺序（8 模块，seqlock 快照）
// ─────────────────────────────────────────────────────────────────────────────

__declspec(dllexport) int celeste_dsp_rack_set_order(const int* order)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (order == nullptr)
        return -2;

    std::vector<echo::DspRackModuleId> next;
    next.reserve(echo::DspRackOrder::moduleCount);
    for (std::size_t i = 0; i < echo::DspRackOrder::moduleCount; ++i)
        next.push_back(static_cast<echo::DspRackModuleId>(order[i]));

    std::string error;
    return engine->rack.setOrder(next, error) ? 0 : -3;
}

__declspec(dllexport) int celeste_dsp_rack_get_order(int* order)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (order == nullptr)
        return -2;

    const auto snapshot = engine->rack.snapshot();
    for (std::size_t i = 0; i < echo::DspRackOrder::moduleCount; ++i)
        order[i] = static_cast<int>(snapshot[i]);
    return 0;
}

__declspec(dllexport) int celeste_dsp_rack_reset_default()
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    engine->rack.resetToDefault();
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// 卷积（房间校正 / 耳机 IR；直积，8192 taps 上限）
// ─────────────────────────────────────────────────────────────────────────────

/// taps 为 planar float32：[ch0 全部 taps][ch1 全部 taps]，每声道 tapsLen 个。
/// tapsChannels 1=mono IR、2=stereo IR。sourceSampleRate 为 IR 自身采样率（内部重采样）。
/// 返回 0；-4=校验失败（空 / 超 8192 taps / 重采样失败）。
__declspec(dllexport) int celeste_dsp_conv_load_ir(const float* taps, int tapsChannels,
                                                   int tapsLen, double sourceSampleRate)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    if (taps == nullptr || tapsChannels <= 0 || tapsLen <= 0)
        return -2;
    if (tapsChannels > 2)
        return -2;
    if (! (sourceSampleRate > 0.0))
        return -2;

    std::vector<std::vector<float>> planar;
    planar.resize(static_cast<std::size_t>(tapsChannels));
    for (int ch = 0; ch < tapsChannels; ++ch)
    {
        planar[static_cast<std::size_t>(ch)].assign(
            taps + static_cast<std::size_t>(ch) * static_cast<std::size_t>(tapsLen),
            taps + static_cast<std::size_t>(ch + 1) * static_cast<std::size_t>(tapsLen));
    }

    const bool ok = engine->convolution.loadImpulseResponseFromMemory(
        planar, sourceSampleRate, "celeste-ir", "celeste-ir");
    return ok ? 0 : -4;
}

__declspec(dllexport) int celeste_dsp_conv_clear()
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    engine->convolution.clearImpulseResponse();
    return 0;
}

__declspec(dllexport) int celeste_dsp_conv_set_enabled(int enabled)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    engine->convolution.setEnabled(enabled != 0);
    return 0;
}

__declspec(dllexport) int celeste_dsp_conv_set_trim_db(float trimDb)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;
    engine->convolution.setTrimDb(trimDb);
    return 0;
}

__declspec(dllexport) int celeste_dsp_conv_get_state(int* enabled, int* tapCount,
                                                     double* sampleRate, float* trimDb,
                                                     int* latencySamples, int* clippingRisk,
                                                     int* hasError)
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return -1;

    const echo::RoomCorrectionState state = engine->convolution.getState();
    if (enabled != nullptr)
        *enabled = state.enabled ? 1 : 0;
    if (tapCount != nullptr)
        *tapCount = state.tapCount;
    if (sampleRate != nullptr)
        *sampleRate = state.sampleRate;
    if (trimDb != nullptr)
        *trimDb = state.trimDb;
    if (latencySamples != nullptr)
        *latencySamples = state.latencySamples;
    if (clippingRisk != nullptr)
        *clippingRisk = state.clippingRisk ? 1 : 0;
    if (hasError != nullptr)
        *hasError = state.error.empty() ? 0 : 1;
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// 链状态查询
// ─────────────────────────────────────────────────────────────────────────────

__declspec(dllexport) int celeste_dsp_is_active()
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return 0;
    return engine->chain.isActive() ? 1 : 0;
}

__declspec(dllexport) int celeste_dsp_has_clipping_risk()
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return 0;
    return engine->chain.hasClippingRisk() ? 1 : 0;
}

__declspec(dllexport) int celeste_dsp_limiter_protecting()
{
    auto* engine = requireEngine();
    if (engine == nullptr)
        return 0;
    return engine->chain.isSafetyLimiterProtecting() ? 1 : 0;
}

__declspec(dllexport) float celeste_dsp_limiter_gr_db()
{
    auto* engine = requireEngine();
    return engine == nullptr ? 0.0f : engine->chain.safetyLimiterGainReductionDb();
}

__declspec(dllexport) float celeste_dsp_limiter_ceiling_db()
{
    auto* engine = requireEngine();
    return engine == nullptr ? 0.0f : engine->chain.safetyLimiterCeilingDb();
}

__declspec(dllexport) int celeste_dsp_version()
{
    return 0x00010000; // 1.0
}

} // extern "C"
