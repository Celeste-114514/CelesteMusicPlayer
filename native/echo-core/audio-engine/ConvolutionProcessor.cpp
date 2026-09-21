#include "ConvolutionProcessor.h"
#include "DspSafetyLimiter.h"

#include <algorithm>
#include <cmath>
#include <memory>
#include <sys/stat.h>
#include <vector>

// MSVC/UCRT 兼容：POSIX 的 stat/S_ISREG 在 MSVC 分别是 _stat/_S_IFREG
#ifdef _WIN32
#ifndef S_ISREG
#define S_ISREG(m) (((m) & _S_IFMT) == _S_IFREG)
#endif
#define celeste_stat _stat
#define celeste_stat_t struct _stat
#else
#define celeste_stat stat
#define celeste_stat_t struct stat
#endif

namespace echo
{
namespace
{

float dbToGain(float db)
{
    return std::pow(10.0f, db / 20.0f);
}

float readLinear(const FloatAudioBuffer& buffer, int channel, double position)
{
    const int sourceSamples = buffer.getNumSamples();
    if (sourceSamples <= 0)
        return 0.0f;

    const double clamped = std::max(0.0, std::min(position, static_cast<double>(sourceSamples - 1)));
    const int index = static_cast<int>(std::floor(clamped));
    const int nextIndex = std::min(index + 1, sourceSamples - 1);
    const float fraction = static_cast<float>(clamped - static_cast<double>(index));
    const float left = buffer.getSample(channel, index);
    const float right = buffer.getSample(channel, nextIndex);
    return left + (right - left) * fraction;
}

bool isFiniteBuffer(const FloatAudioBuffer& buffer)
{
    for (int channel = 0; channel < buffer.getNumChannels(); ++channel)
        for (int sample = 0; sample < buffer.getNumSamples(); ++sample)
            if (! std::isfinite(buffer.getSample(channel, sample)))
                return false;

    return true;
}

/// Decode an audio file into a FloatAudioBuffer.
/// Celeste 内核裁剪版：原实现依赖 FFmpeg 解码 IR 音频文件；本内核只取输出通路，
/// IR 的数据加载后续由 C# 侧解析音频后经内存接口提供，文件解码入口在此构建中禁用。
/// 返回空 buffer 后 loadImpulseResponse 的空值检查会自然短路，无需改动调用方。
FloatAudioBuffer decodeAudioFile(const std::string& path)
{
    (void)path;
    return {};
}

} // namespace

float clampRoomCorrectionTrimDb(float value)
{
    if (! std::isfinite(value))
        return 0.0f;

    return std::max(roomCorrectionMinTrimDb, std::min(roomCorrectionMaxTrimDb, value));
}

ConvolutionProcessor::ConvolutionProcessor() = default;

void ConvolutionProcessor::prepare(double sampleRate, int maximumBlockSize, int channelCount)
{
    currentSampleRate = sampleRate > 0.0 ? sampleRate : 44100.0;
    preparedChannels = std::max(1, channelCount);
    preparedBlockSize = std::max(1, maximumBlockSize);
    history.assign(static_cast<size_t>(preparedChannels), std::vector<float>(static_cast<size_t>(roomCorrectionMaxTaps), 0.0f));
    historyWriteIndex = 0;
    clippingRisk.store(false, std::memory_order_release);
}

void ConvolutionProcessor::reset()
{
    for (auto& channelHistory : history)
        std::fill(channelHistory.begin(), channelHistory.end(), 0.0f);

    historyWriteIndex = 0;
    clippingRisk.store(false, std::memory_order_release);
}

void ConvolutionProcessor::processBlock(echo::FloatAudioBuffer& buffer, int startSample, int numSamples)
{
    if (numSamples <= 0)
        return;

    auto impulse = std::atomic_load_explicit(&activeImpulse, std::memory_order_acquire);
    const bool enabled = targetEnabled.load(std::memory_order_acquire);
    const int channelCount = std::min(buffer.getNumChannels(), preparedChannels);
    if (! enabled || impulse == nullptr || impulse->tapCount <= 0 || channelCount <= 0)
    {
        clippingRisk.store(false, std::memory_order_release);
        return;
    }

    const int tapCount = std::min(impulse->tapCount, roomCorrectionMaxTaps);
    const float trimGain = dbToGain(atomicTrimDb.load(std::memory_order_acquire));
    bool risk = false;

    for (int sample = 0; sample < numSamples; ++sample)
    {
        for (int channel = 0; channel < channelCount; ++channel)
        {
            const float input = sanitize(buffer.getSample(channel, startSample + sample));
            history[static_cast<size_t>(channel)][static_cast<size_t>(historyWriteIndex)] = input;
        }

        for (int channel = 0; channel < channelCount; ++channel)
        {
            const int impulseChannel = impulse->taps.size() <= 1 ? 0 : std::min(channel, static_cast<int>(impulse->taps.size()) - 1);
            const auto& taps = impulse->taps[static_cast<size_t>(impulseChannel)];
            const auto& channelHistory = history[static_cast<size_t>(channel)];
            double output = 0.0;

            for (int tap = 0; tap < tapCount; ++tap)
            {
                int historyIndex = historyWriteIndex - tap;
                if (historyIndex < 0)
                    historyIndex += roomCorrectionMaxTaps;

                output += static_cast<double>(taps[static_cast<size_t>(tap)]) * static_cast<double>(channelHistory[static_cast<size_t>(historyIndex)]);
            }

            buffer.setSample(
                channel,
                startSample + sample,
                protectClippingSample(static_cast<float>(output) * trimGain, isDspSafetyLimiterEnabled(), risk));
        }

        historyWriteIndex = (historyWriteIndex + 1) % roomCorrectionMaxTaps;
    }

    clippingRisk.store(risk, std::memory_order_release);
}

void ConvolutionProcessor::setEnabled(bool shouldBeEnabled)
{
    targetEnabled.store(shouldBeEnabled, std::memory_order_release);
}

void ConvolutionProcessor::setTrimDb(float value)
{
    atomicTrimDb.store(clampRoomCorrectionTrimDb(value), std::memory_order_release);
}

bool ConvolutionProcessor::loadImpulseResponse(const std::string& path, const std::string& id, const std::string& name)
{
    auto source = decodeAudioFile(path);
    if (source.getNumChannels() <= 0 || source.getNumSamples() <= 0)
    {
        hasError.store(true, std::memory_order_release);
        // Differentiate missing file from invalid audio
        celeste_stat_t st {};
        if (celeste_stat(path.c_str(), &st) != 0 || !S_ISREG(st.st_mode))
            errorMessage = "missing_file";
        else
            errorMessage = "invalid_audio";
        return false;
    }

    const int sourceChannels = source.getNumChannels();
    const int sourceSamples = source.getNumSamples();

    // Check impulse length against max taps
    const double estimatedOutputSamples = static_cast<double>(sourceSamples) * currentSampleRate / std::max(1.0, currentSampleRate);
    if (! std::isfinite(estimatedOutputSamples) || estimatedOutputSamples > static_cast<double>(roomCorrectionMaxTaps))
    {
        hasError.store(true, std::memory_order_release);
        errorMessage = "impulse_too_long";
        return false;
    }

    // Truncate if necessary
    const int maxSourceSamples = roomCorrectionMaxTaps * 4;
    if (sourceSamples > maxSourceSamples)
    {
        FloatAudioBuffer trimmed(sourceChannels, maxSourceSamples);
        for (int ch = 0; ch < sourceChannels; ++ch)
        {
            const float* src = source.getReadPointer(ch);
            float* dst = trimmed.getWritePointer(ch);
            std::copy_n(src, static_cast<size_t>(maxSourceSamples), dst);
        }
        source = std::move(trimmed);
    }

    auto prepared = createPreparedImpulse(source, currentSampleRate, currentSampleRate, id, name);
    if (prepared == nullptr)
    {
        hasError.store(true, std::memory_order_release);
        errorMessage = "invalid_impulse";
        return false;
    }

    std::atomic_store_explicit(&activeImpulse, prepared, std::memory_order_release);
    reset();
    hasError.store(false, std::memory_order_release);
    errorMessage.clear();
    return true;
}

void ConvolutionProcessor::clearImpulseResponse()
{
    std::shared_ptr<const PreparedImpulse> empty;
    std::atomic_store_explicit(&activeImpulse, empty, std::memory_order_release);
    reset();
    hasError.store(false, std::memory_order_release);
    errorMessage.clear();
}

RoomCorrectionState ConvolutionProcessor::getState() const
{
    auto impulse = std::atomic_load_explicit(&activeImpulse, std::memory_order_acquire);
    RoomCorrectionState state;
    state.enabled = targetEnabled.load(std::memory_order_acquire);
    state.trimDb = atomicTrimDb.load(std::memory_order_acquire);
    state.clippingRisk = clippingRisk.load(std::memory_order_acquire);
    state.error = hasError.load(std::memory_order_acquire) ? errorMessage : std::string();

    if (impulse != nullptr)
    {
        state.status = state.enabled ? "active" : "loaded";
        state.irId = impulse->id;
        state.irName = impulse->name;
        state.channelMode = impulse->channelMode;
        state.sampleRate = impulse->sampleRate;
        state.tapCount = impulse->tapCount;
    }
    else
    {
        state.status = state.error.empty() ? "empty" : "error";
        state.channelMode = "none";
    }

    return state;
}

bool ConvolutionProcessor::isEnabled() const
{
    auto impulse = std::atomic_load_explicit(&activeImpulse, std::memory_order_acquire);
    return targetEnabled.load(std::memory_order_acquire) && impulse != nullptr && impulse->tapCount > 0;
}

bool ConvolutionProcessor::hasClippingRisk() const
{
    return clippingRisk.load(std::memory_order_acquire);
}

std::shared_ptr<const ConvolutionProcessor::PreparedImpulse> ConvolutionProcessor::createPreparedImpulse(
    const echo::FloatAudioBuffer& source,
    double sourceSampleRate,
    double targetSampleRate,
    const std::string& id,
    const std::string& name)
{
    if (source.getNumChannels() <= 0 || source.getNumSamples() <= 0 || ! isFiniteBuffer(source))
        return nullptr;

    const double safeSourceRate = sourceSampleRate > 0.0 ? sourceSampleRate : targetSampleRate;
    const double safeTargetRate = targetSampleRate > 0.0 ? targetSampleRate : safeSourceRate;
    const int outputSamples = std::max(1, static_cast<int>(std::ceil(static_cast<double>(source.getNumSamples()) * safeTargetRate / safeSourceRate)));
    if (outputSamples > roomCorrectionMaxTaps)
        return nullptr;

    auto impulse = std::make_shared<PreparedImpulse>();
    impulse->id = id;
    impulse->name = name;
    impulse->sampleRate = safeTargetRate;
    impulse->tapCount = outputSamples;
    impulse->channelMode = source.getNumChannels() > 1 ? "stereo" : "mono";
    impulse->taps.assign(static_cast<size_t>(std::min(2, source.getNumChannels())), std::vector<float>(static_cast<size_t>(outputSamples), 0.0f));

    const double ratio = safeSourceRate / safeTargetRate;
    for (int channel = 0; channel < static_cast<int>(impulse->taps.size()); ++channel)
    {
        for (int sample = 0; sample < outputSamples; ++sample)
            impulse->taps[static_cast<size_t>(channel)][static_cast<size_t>(sample)] = sanitize(readLinear(source, channel, static_cast<double>(sample) * ratio));
    }

    return impulse;
}

float ConvolutionProcessor::sanitize(float value)
{
    return std::isfinite(value) ? value : 0.0f;
}

float ConvolutionProcessor::protectClippingSample(float sample, bool shouldProtect, bool& risk)
{
    (void)shouldProtect;

    if (! std::isfinite(sample))
        return 0.0f;

    constexpr float riskThreshold = 0.98f;
    const float magnitude = std::abs(sample);
    risk = risk || magnitude > riskThreshold;
    return sample;
}

#if defined(ECHO_AUDIO_ENGINE_TESTS) && ECHO_AUDIO_ENGINE_TESTS
bool ConvolutionProcessor::loadImpulseResponseForTests(
    const std::vector<std::vector<float>>& taps,
    double sourceSampleRate,
    const std::string& id,
    const std::string& name)
{
    if (taps.empty() || taps[0].empty() || taps[0].size() > static_cast<size_t>(roomCorrectionMaxTaps))
        return false;

    const int channels = std::min<int>(2, static_cast<int>(taps.size()));
    const int samples = static_cast<int>(taps[0].size());
    FloatAudioBuffer source(channels, samples);
    for (int ch = 0; ch < channels; ++ch)
    {
        float* dst = source.getWritePointer(ch);
        for (int s = 0; s < std::min(samples, static_cast<int>(taps[static_cast<size_t>(ch)].size())); ++s)
            dst[s] = taps[static_cast<size_t>(ch)][static_cast<size_t>(s)];
    }

    auto prepared = createPreparedImpulse(source, sourceSampleRate, currentSampleRate, id, name);
    if (prepared == nullptr)
        return false;

    std::atomic_store_explicit(&activeImpulse, prepared, std::memory_order_release);
    reset();
    hasError.store(false, std::memory_order_release);
    errorMessage.clear();
    return true;
}
#endif
} // namespace echo