// celeste_core.cpp — Celeste 自研原生音频内核（PCM WASAPI 独占输出）
//
// MIT。全新编写；架构参照 ECHO audio-host（LGPL，另目录 native/echo-core/）的
// C ABI 外形与自研 NativeWasapiExclusiveOut 的调度修复经验，不派生其代码。
//
// 编译：见同目录 build_args.rsp（cl 响应文件；含空格 LIBPATH 会被 rsp
// 解析器拆开，libs 已拷到无空格目录 build/libs）。
//
// 线程模型：
//   - celeste_core_start 跑在 C# 调用方线程：枚举设备、协商格式、初始化
//     IAudioClient、prime 首缓冲、Start 设备、起渲染线程。
//   - 渲染线程（本文件创建）：MMCSS "Pro Audio" + AvSetMmThreadPriority
//     (CRITICAL)（2026-09-23 用户实机定论：只注册 MMCSS 不设频段内优先级
//     = NORMAL = 频段最底层，补货间隔被系统时钟量成 15.6ms 的整数倍）；
//     timeBeginPeriod(1) 让轮询真正按毫秒跑；轮询补货（缓冲/8，1~15ms），
//     不傻等设备事件。
//   - feeder 是 C# 线程：push 字节进 ring；stop 时 push 返回 false 退出。

#include "celeste_core.h"
#include "byte_ring.h"

#include <windows.h>
#include <audioclient.h>
#include <audiopolicy.h>
#include <avrt.h>
#include <combaseapi.h>
#include <mmdeviceapi.h>
#include <objbase.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <future>
#include <memory>
#include <mutex>
#include <vector>

// ---------------------------------------------------------------------------
// 常量
// ---------------------------------------------------------------------------

static const uint32_t kMinRate = 8000;
static const uint32_t kMaxRate = 768000;
static const uint32_t kMaxChannels = 2;    // 独占自研内核只做 1/2 声道（与自研 C# 版一致）
static const int kRingReserveMs = 1500;     // ring 存货 1.5s（吸收 feeder 被 GC 冻住的 ≤64ms，余量 20×+）
static const int kRingMaxFrames = 4000000;  // ring 容量 sanity cap（帧）
static const int kPollMinMs = 1;
static const int kPollMaxMs = 15;
static const int kStopJoinMs = 5000;        // 等渲染线程退出的上限
static const int kWasapiTimeoutMs = 3000;   // Activate/GetDevicePeriod/Initialize/Start 单步超时（驱动可能挂住）
static const int kFadeMs = 10;              // replace（seek/切歌）后淡入时长

static const GUID kSubtypePcm = { 0x00000001, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
static const GUID kSubtypeIeeeFloat = { 0x00000003, 0x0000, 0x0010, { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };

// ---------------------------------------------------------------------------
// 基础助手
// ---------------------------------------------------------------------------

struct com_scope {
    HRESULT hr;
    int needs_uninit;
};

static com_scope com_enter()
{
    com_scope s;
    s.hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    s.needs_uninit = SUCCEEDED(s.hr) ? 1 : 0;
    if (s.hr == RPC_E_CHANGED_MODE) {
        s.hr = S_OK;
        s.needs_uninit = 0;
    }
    return s;
}

static void com_leave(com_scope* s)
{
    if (s != nullptr && s->needs_uninit) {
        s->needs_uninit = 0;
        CoUninitialize();
    }
}

static void set_error(char* error, size_t len, const char* message, HRESULT hr)
{
    if (error == nullptr || len == 0) return;
    if (message == nullptr) message = "unknown error";
    if (hr != S_OK) {
        snprintf(error, len, "%s (hr=0x%08lx)", message, (unsigned long)hr);
    } else {
        snprintf(error, len, "%s", message);
    }
    error[len - 1] = '\0';
}

static uint32_t fmt_bytes_per_sample(uint32_t tag)
{
    switch (tag) {
        case CELESTE_FMT_PCM16: return 2;
        case CELESTE_FMT_PCM24: return 3;
        case CELESTE_FMT_PCM24IN32: return 4;
        case CELESTE_FMT_PCM32: return 4;
        case CELESTE_FMT_FLOAT32: return 4;
        default: return 0;
    }
}

static const char* fmt_tag_name(uint32_t tag)
{
    switch (tag) {
        case CELESTE_FMT_PCM16: return "pcm16";
        case CELESTE_FMT_PCM24: return "pcm24";
        case CELESTE_FMT_PCM24IN32: return "pcm24in32";
        case CELESTE_FMT_PCM32: return "pcm32";
        case CELESTE_FMT_FLOAT32: return "float32";
        default: return "?";
    }
}

static int fmt_tag_valid(uint32_t tag)
{
    return tag >= CELESTE_FMT_PCM16 && tag <= CELESTE_FMT_FLOAT32;
}

static DWORD channel_mask_for(uint32_t channels)
{
    if (channels == 1) return SPEAKER_FRONT_CENTER;
    if (channels == 2) return SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
    return 0;
}

// 按格式标签造 WAVEFORMATEXTENSIBLE（端点容器 = 源布局）
static void make_waveformat(uint32_t tag, uint32_t rate, uint32_t channels, WAVEFORMATEXTENSIBLE* out)
{
    memset(out, 0, sizeof(*out));
    WAVEFORMATEX* wf = &out->Format;
    wf->wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    wf->nChannels = (WORD)channels;
    wf->nSamplesPerSec = rate;
    wf->cbSize = (WORD)(sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX));
    out->dwChannelMask = channel_mask_for(channels);
    const bool is_float = (tag == CELESTE_FMT_FLOAT32);
    wf->wBitsPerSample = (WORD)(fmt_bytes_per_sample(tag) * 8);
    out->Samples.wValidBitsPerSample = (tag == CELESTE_FMT_PCM24IN32) ? 24 : wf->wBitsPerSample;
    out->SubFormat = is_float ? kSubtypeIeeeFloat : kSubtypePcm;
    wf->nBlockAlign = (WORD)(channels * fmt_bytes_per_sample(tag));
    wf->nAvgBytesPerSec = wf->nSamplesPerSec * wf->nBlockAlign;
}

static REFERENCE_TIME frames_to_hns(uint32_t frames, uint32_t rate)
{
    if (rate == 0) return 0;
    return (REFERENCE_TIME)((10000000.0 * (double)frames / (double)rate) + 0.5);
}

static int64_t now_qpc()
{
    LARGE_INTEGER v;
    QueryPerformanceCounter(&v);
    return (int64_t)v.QuadPart;
}

static int64_t qpc_freq()
{
    static int64_t f = 0;
    if (f == 0) {
        LARGE_INTEGER v;
        QueryPerformanceFrequency(&v);
        f = (int64_t)v.QuadPart;
    }
    return f;
}

// ---------------------------------------------------------------------------
// 超时的 WASAPI 调用（驱动可能挂住 Activate/Initialize/Start，不能无限等）。
// 超时后异步任务仍在跑：future 进坟墓区统一 drain，绝不在超时路径析构
// std::future（async future 的析构会阻塞到任务结束——那正是要避开的事）。
// 共享状态全部经 shared_ptr / AddRef 传递，超时后无悬垂引用。
// ---------------------------------------------------------------------------

namespace {

std::mutex& graveyard_mutex()
{
    static std::mutex* m = new std::mutex();
    return *m;
}

std::vector<std::future<HRESULT>>& graveyard()
{
    static auto* v = new std::vector<std::future<HRESULT>>();
    return *v;
}

struct graveyard_shutdown_guard {
    ~graveyard_shutdown_guard()
    {
        std::vector<std::future<HRESULT>> pending;
        {
            std::lock_guard<std::mutex> lk(graveyard_mutex());
            pending.swap(graveyard());
        }
        for (auto& f : pending) {
            if (!f.valid()) continue;
            try { (void)f.get(); } catch (...) {}
        }
    }
};

void ensure_graveyard_guard()
{
    static graveyard_shutdown_guard guard;
    (void)guard;
}

void abandon(std::future<HRESULT>&& f)
{
    ensure_graveyard_guard();
    std::lock_guard<std::mutex> lk(graveyard_mutex());
    auto& g = graveyard();
    g.erase(std::remove_if(g.begin(), g.end(),
               [](std::future<HRESULT>& x) {
                   return !x.valid() || x.wait_for(std::chrono::seconds(0)) == std::future_status::ready;
               }),
            g.end());
    g.push_back(std::move(f));
}

HRESULT wait_future(std::future<HRESULT>& f, const char* phase)
{
    if (f.wait_for(std::chrono::milliseconds(kWasapiTimeoutMs)) == std::future_status::timeout) {
        std::fprintf(stderr, "[celeste-core] WASAPI %s timed out after %dms\n", phase, kWasapiTimeoutMs);
        abandon(std::move(f));
        return E_PENDING;
    }
    return f.get();
}

} // namespace

static HRESULT activate_with_timeout(IMMDevice* device, IAudioClient** out)
{
    *out = nullptr;
    if (device == nullptr) return E_POINTER;
    struct ctx_t { IAudioClient* client; };
    auto c = std::make_shared<ctx_t>();
    c->client = nullptr;
    device->AddRef();
    auto fut = std::async(std::launch::async, [device, c]() -> HRESULT {
        com_scope com = com_enter();
        if (FAILED(com.hr)) {
            device->Release();
            return com.hr;
        }
        IAudioClient* local = nullptr;
        const HRESULT hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)&local);
        if (SUCCEEDED(hr) && local != nullptr) {
            c->client = local;
        } else if (local != nullptr) {
            local->Release();
        }
        com_leave(&com);
        device->Release();
        return hr;
    });
    const HRESULT hr = wait_future(fut, "Activate");
    if (SUCCEEDED(hr)) *out = c->client;
    return hr;
}

static HRESULT get_device_period_with_timeout(IAudioClient* client, REFERENCE_TIME* def, REFERENCE_TIME* min)
{
    if (client == nullptr || def == nullptr || min == nullptr) return E_POINTER;
    struct ctx_t { REFERENCE_TIME def; REFERENCE_TIME min; };
    auto c = std::make_shared<ctx_t>();
    c->def = 0;
    c->min = 0;
    client->AddRef();
    auto fut = std::async(std::launch::async, [client, c]() -> HRESULT {
        com_scope com = com_enter();
        if (FAILED(com.hr)) {
            client->Release();
            return com.hr;
        }
        const HRESULT hr = client->GetDevicePeriod(&c->def, &c->min);
        com_leave(&com);
        client->Release();
        return hr;
    });
    const HRESULT hr = wait_future(fut, "GetDevicePeriod");
    if (SUCCEEDED(hr)) {
        *def = c->def;
        *min = c->min;
    }
    return hr;
}

static HRESULT initialize_with_timeout(IAudioClient* client, DWORD flags,
                                       REFERENCE_TIME buf_hns, REFERENCE_TIME per_hns,
                                       const WAVEFORMATEX* wave)
{
    if (client == nullptr) return E_POINTER;
    struct ctx_t { std::vector<unsigned char> wave; };
    auto c = std::make_shared<ctx_t>();
    const size_t wave_size = sizeof(WAVEFORMATEX) + (size_t)wave->cbSize;
    c->wave.resize(wave_size);
    memcpy(c->wave.data(), wave, wave_size);
    client->AddRef();
    auto fut = std::async(std::launch::async, [client, c, flags, buf_hns, per_hns]() -> HRESULT {
        com_scope com = com_enter();
        if (FAILED(com.hr)) {
            client->Release();
            return com.hr;
        }
        const HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, flags, buf_hns, per_hns,
                                              reinterpret_cast<const WAVEFORMATEX*>(c->wave.data()), nullptr);
        com_leave(&com);
        client->Release();
        return hr;
    });
    return wait_future(fut, "Initialize");
}

static HRESULT start_with_timeout(IAudioClient* client)
{
    if (client == nullptr) return E_POINTER;
    client->AddRef();
    auto fut = std::async(std::launch::async, [client]() -> HRESULT {
        com_scope com = com_enter();
        if (FAILED(com.hr)) {
            client->Release();
            return com.hr;
        }
        const HRESULT hr = client->Start();
        com_leave(&com);
        client->Release();
        return hr;
    });
    return wait_future(fut, "Start");
}

// ---------------------------------------------------------------------------
// 设备解析：按 IMMDevice::GetId 精确匹配；空 = 默认渲染设备
// ---------------------------------------------------------------------------

struct endpoint_info {
    wchar_t id[512];
    wchar_t name[128];
};

static int enumerate_render_endpoints(std::vector<endpoint_info>& out, char* error, size_t error_len)
{
    com_scope com = com_enter();
    if (FAILED(com.hr)) {
        set_error(error, error_len, "COM 初始化失败", com.hr);
        return -1;
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  __uuidof(IMMDeviceEnumerator), (void**)&enumerator);
    if (FAILED(hr) || enumerator == nullptr) {
        set_error(error, error_len, "无法创建设备枚举器", hr);
        com_leave(&com);
        return -1;
    }

    IMMDeviceCollection* collection = nullptr;
    hr = enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &collection);
    if (FAILED(hr) || collection == nullptr) {
        set_error(error, error_len, "枚举渲染设备失败", hr);
        if (collection != nullptr) collection->Release();
        enumerator->Release();
        com_leave(&com);
        return -1;
    }

    UINT count = 0;
    hr = collection->GetCount(&count);
    if (FAILED(hr)) {
        set_error(error, error_len, "读取设备数量失败", hr);
        collection->Release();
        enumerator->Release();
        com_leave(&com);
        return -1;
    }

    for (UINT i = 0; i < count; ++i) {
        IMMDevice* device = nullptr;
        if (FAILED(collection->Item(i, &device)) || device == nullptr) continue;

        endpoint_info info;
        memset(&info, 0, sizeof(info));
        LPWSTR id = nullptr;
        if (SUCCEEDED(device->GetId(&id)) && id != nullptr) {
            wcsncpy(info.id, id, sizeof(info.id) / sizeof(info.id[0]) - 1);
            CoTaskMemFree(id);
        }
        IPropertyStore* props = nullptr;
        if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &props)) && props != nullptr) {
            PROPVARIANT v;
            PropVariantInit(&v);
            static const PROPERTYKEY name_key = {
                { 0xa45c254e, 0xdf1c, 0x4efd, { 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0 } }, 14 };
            if (SUCCEEDED(props->GetValue(name_key, &v)) && v.vt == VT_LPWSTR && v.pwszVal != nullptr) {
                wcsncpy(info.name, v.pwszVal, sizeof(info.name) / sizeof(info.name[0]) - 1);
            }
            PropVariantClear(&v);
            props->Release();
        }
        out.push_back(info);
        device->Release();
    }

    collection->Release();
    enumerator->Release();
    com_leave(&com);
    return out.empty() ? -1 : 0;
}

static IMMDevice* resolve_device(const wchar_t* device_id, char* error, size_t error_len)
{
    std::vector<endpoint_info> endpoints;
    if (enumerate_render_endpoints(endpoints, error, error_len) != 0) {
        set_error(error, error_len, "没有可用的渲染设备", S_OK);
        return nullptr;
    }

    com_scope com = com_enter();
    if (FAILED(com.hr)) {
        set_error(error, error_len, "COM 初始化失败", com.hr);
        return nullptr;
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  __uuidof(IMMDeviceEnumerator), (void**)&enumerator);
    if (FAILED(hr)) {
        set_error(error, error_len, "无法创建设备枚举器", hr);
        com_leave(&com);
        return nullptr;
    }

    IMMDevice* device = nullptr;
    if (device_id != nullptr && device_id[0] != L'\0') {
        const wchar_t* selected = nullptr;
        for (const auto& e : endpoints) {
            if (wcscmp(e.id, device_id) == 0) {
                selected = e.id;
                break;
            }
        }
        if (selected == nullptr) {
            set_error(error, error_len, "指定的输出设备不在活动设备列表（已拔出/禁用？）", S_OK);
            enumerator->Release();
            com_leave(&com);
            return nullptr;
        }
        hr = enumerator->GetDevice(selected, &device);
    } else {
        hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
    }
    if (FAILED(hr)) {
        set_error(error, error_len, "解析输出设备失败", hr);
        device = nullptr;
    }

    enumerator->Release();
    com_leave(&com);
    return device;
}

// ---------------------------------------------------------------------------
// 独占初始化：源布局精确直通（bit-perfect）；含 AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED
// 对齐 dance。设备不支持源布局 → 失败（上层切回自研内核，不做转换回退）。
// 周期 = 缓冲（MSDN 铁律：独占 + EVENTCALLBACK 下二者必须相等）。
// ---------------------------------------------------------------------------

static HRESULT init_exclusive_once(IMMDevice* device, uint32_t tag, uint32_t rate, uint32_t channels,
                                   uint32_t requested_buffer_frames,
                                   IAudioClient** out_client, uint32_t* out_buffer_frames,
                                   char* error, size_t error_len)
{
    *out_client = nullptr;
    *out_buffer_frames = 0;

    WAVEFORMATEXTENSIBLE wave;
    make_waveformat(tag, rate, channels, &wave);

    IAudioClient* client = nullptr;
    HRESULT hr = activate_with_timeout(device, &client);
    if (FAILED(hr)) {
        set_error(error, error_len, "激活音频设备失败（设备被占用？）", hr);
        return hr;
    }

    REFERENCE_TIME default_period = 0, min_period = 0;
    hr = get_device_period_with_timeout(client, &default_period, &min_period);
    if (hr == E_PENDING) return hr;  // 驱动挂住：client 已激活但放弃（未来完成时会自行释放）
    if (FAILED(hr)) {
        set_error(error, error_len, "查询设备周期失败", hr);
        client->Release();
        return hr;
    }

    REFERENCE_TIME buffer_hns = frames_to_hns(requested_buffer_frames, rate);
    if (buffer_hns < 100000) buffer_hns = 100000;   // 下限 10ms（C# 侧已 clamp 10..1000，这里双保险）
    if (min_period > buffer_hns) buffer_hns = min_period;  // 设备最小周期更大时服从设备

    const DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST;
    hr = initialize_with_timeout(client, flags, buffer_hns, buffer_hns, (const WAVEFORMATEX*)&wave);
    if (hr == E_PENDING) return hr;

    if (hr == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED) {
        // 对齐 dance：问出对齐帧数，重开 client 再 Initialize（微软示例做法）
        UINT32 aligned = 0;
        if (FAILED(client->GetBufferSize(&aligned)) || aligned == 0) {
            set_error(error, error_len, "对齐后仍无法读取设备缓冲", S_OK);
            client->Release();
            return E_FAIL;
        }
        client->Release();
        client = nullptr;
        hr = activate_with_timeout(device, &client);
        if (FAILED(hr)) {
            set_error(error, error_len, "对齐后重新激活设备失败", hr);
            return hr;
        }
        buffer_hns = frames_to_hns(aligned, rate);
        hr = initialize_with_timeout(client, flags, buffer_hns, buffer_hns, (const WAVEFORMATEX*)&wave);
        if (hr == E_PENDING) return hr;
    }

    if (FAILED(hr)) {
        set_error(error, error_len,
                  hr == AUDCLNT_E_UNSUPPORTED_FORMAT ? "设备不支持该独占格式" : "WASAPI 独占初始化失败", hr);
        client->Release();
        return hr;
    }

    UINT32 buffer_frames = 0;
    hr = client->GetBufferSize(&buffer_frames);
    if (FAILED(hr) || buffer_frames == 0) {
        set_error(error, error_len, "读取设备缓冲大小失败", hr);
        client->Release();
        return E_FAIL;
    }

    *out_client = client;
    *out_buffer_frames = buffer_frames;
    return S_OK;
}

// ---------------------------------------------------------------------------
// 引擎
// ---------------------------------------------------------------------------

struct celeste_engine {
    byte_ring ring;
    IAudioClient* client = nullptr;
    IAudioRenderClient* render = nullptr;
    IAudioClock* clock = nullptr;       // I 轮探针用：设备实际播放游标（GetPosition）
    HANDLE render_event = nullptr;   // auto-reset：设备要数据
    HANDLE stop_event = nullptr;     // manual-reset：停止
    HANDLE thread = nullptr;

    uint32_t rate = 0;
    uint32_t channels = 0;
    uint32_t buffer_frames = 0;      // 设备周期帧数
    uint32_t source_tag = 0;         // feeder 字节布局（协商后 = 端点容器）
    int frame_bytes = 0;             // 每帧字节
    int capacity_frames = 0;         // ring 容量（帧）
    char endpoint_name[32] = { 0 };

    std::atomic<bool> started{ false };
    std::atomic<bool> stop_requested{ false };
    std::atomic<int> paused{ 0 };
    std::atomic<int> failed{ 0 };
    std::atomic<int> drained{ 0 };

    // 窗口统计（stats() 读走即清零）
    std::atomic<int32_t> max_gap_ms{ 0 };
    std::atomic<uint64_t> spike_count{ 0 };

    // I 轮设备侧探针（应用侧全绿仍偶发卡顿：唯一没测的轴=设备实际消费）
    std::atomic<uint64_t> device_pos{ 0 };  // GetPosition 累计帧（即时值，不清零）
    std::atomic<int32_t> pad_max{ 0 };      // 窗口内事件唤醒时 pad 最大值（应恒 0）
    std::atomic<uint32_t> late_wakes{ 0 };  // 窗口内唤醒间隔 >1.5×缓冲周期 次数

    // 累计统计
    std::atomic<uint64_t> frames_played{ 0 };
    std::atomic<uint64_t> underrun_callbacks{ 0 };
    std::atomic<uint64_t> underrun_frames{ 0 };

    // 仅渲染线程访问（stop 会 join；泄漏路径见下）
    int fade_frames = 0;
    bool client_leaked_on_timeout = false;  // Start 挂住：COM 对象 abandoned future 持有，不再碰
    bool engine_leaked = false;             // 渲染线程没按时退出：整体泄漏，进程退出收尸
};

static void note_gap(celeste_engine* e, double gap_ms, int poll_ms)
{
    // 窗口统计：最大间隔 + 尖峰次数（>3×轮询 = 渲染线程没按时醒）
    int32_t cur = e->max_gap_ms.load(std::memory_order_relaxed);
    while (gap_ms > (double)cur &&
           !e->max_gap_ms.compare_exchange_weak(cur, (int32_t)(gap_ms + 0.5), std::memory_order_relaxed)) {
        // compare_exchange 失败时 cur 已刷新为最新值，重试
    }
    if (gap_ms > (double)poll_ms * 3.0) {
        e->spike_count.fetch_add(1, std::memory_order_relaxed);
    }
}

// replace 后线性淡入（削掉 seek/切歌的波形断崖爆音）；仅 2/4 字节布局
// （pcm24 packed 3 字节布局跳过：帧跨步非样本宽，爆音风险可接受且极少见）。
static void apply_fade(celeste_engine* e, uint8_t* scratch, int frames)
{
    const int fb = e->frame_bytes;
    if (fb != 2 && fb != 4) return;
    const int total = e->fade_frames;
    if (total <= 0) return;
    const int n = frames < total ? frames : total;
    for (int i = 0; i < n; ++i) {
        const double g = (double)(i + 1) / (double)(total + 1);
        uint8_t* f = scratch + (size_t)i * (size_t)fb;
        if (e->source_tag == CELESTE_FMT_FLOAT32) {
            float* s = reinterpret_cast<float*>(f);
            for (uint32_t c = 0; c < e->channels; ++c) s[c] = (float)(s[c] * g);
        } else if (e->source_tag == CELESTE_FMT_PCM16) {
            int16_t* s = reinterpret_cast<int16_t*>(f);
            for (uint32_t c = 0; c < e->channels; ++c) s[c] = (int16_t)(s[c] * g);
        } else {
            int32_t* s = reinterpret_cast<int32_t*>(f);
            for (uint32_t c = 0; c < e->channels; ++c) s[c] = (int32_t)(s[c] * g);
        }
    }
    e->fade_frames -= n;
}

// 渲染线程：轮询补货 + memcpy 直通（端点布局 == 源布局，协商保证）。
// 本线程不分配（除退出前的一次性 Stop/Reset）、不抛、不碰 C#：只动 ring / WASAPI。
static DWORD WINAPI render_proc(void* param)
{
    auto* e = static_cast<celeste_engine*>(param);
    com_scope com = com_enter();
    if (FAILED(com.hr)) {
        e->failed.store(1, std::memory_order_release);
        return 1;
    }

    DWORD task_index = 0;
    HANDLE avrt = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task_index);
    if (avrt != nullptr) {
        // 2026-09-23 实机定论：只注册 MMCSS 不设频段内优先级 = NORMAL = 最底层，
        // 补货间隔被系统时钟量成 15.6ms 的整数倍（45~123.7ms 尖峰）。必须 CRITICAL。
        if (!AvSetMmThreadPriority(avrt, AVRT_PRIORITY_CRITICAL)) {
            std::fprintf(stderr, "[celeste-core] AvSetMmThreadPriority(CRITICAL) 失败\n");
        }
    } else {
        std::fprintf(stderr, "[celeste-core] AvSetMmThreadCharacteristics(Pro Audio) 失败\n");
    }

    // 等待是 INFINITE（不依赖超时），1ms 时钟粒度仍有助于事件响应的及时性；成对归还。
    const bool timer_raised = (timeBeginPeriod(1) == TIMERR_NOERROR);
    if (!timer_raised) {
        std::fprintf(stderr, "[celeste-core] timeBeginPeriod(1) 失败，事件响应可能被时钟量化\n");
    }

    const double buffer_ms = e->rate > 0 ? (double)e->buffer_frames * 1000.0 / (double)e->rate : 0.0;
    // 2026-09-23 实机定论（补货尖峰 0 / 真欠载 0 / ring 水位 93%+ 却仍卡顿 2~3 次）：
    //   根因不是"数据不够"，而是补货"写法"不对——12ms 轮询 + 只填空闲空间，
    //   使唤醒相位与设备周期无关、每 12ms 都往缓冲中段塞一小块，USB 驱动
    //   （FiiO KA13）对这种不规则相位小块写入会抖动。
    //   修复：与 ECHO 内核（实机零卡顿）完全同策略——INFINITE 死等 render_event，
    //   每次填满**整个**设备缓冲。
    // 尖峰判据基准 = 一个缓冲周期（事件驱动下的正常唤醒间隔）。

    std::vector<uint8_t> scratch((size_t)e->buffer_frames * (size_t)e->frame_bytes);
    HANDLE waits[2] = { e->stop_event, e->render_event };
    int64_t ts_last = now_qpc();

    while (!e->stop_requested.load(std::memory_order_acquire)) {
        // 事件驱动优先：正常每 ~100ms（一个设备周期）被 render_event 唤醒一次。
        // 2s 兜底超时只防"驱动完全不发事件"时彻底无声；正常路径永远走不到这里，
        // 所以不会重新引入 12ms 轮询那种与设备周期无关的相位漂移（那正是卡顿根因）。
        const DWORD wait = WaitForMultipleObjects(2, waits, FALSE, 2000);
        if (e->stop_requested.load(std::memory_order_acquire)) break;
        (void)wait; // stop=0 / render=1 / 超时：都走同一套补货检查

        const int64_t ts_now = now_qpc();
        const double gap_ms = (double)(ts_now - ts_last) * 1000.0 / (double)qpc_freq();
        ts_last = ts_now;
        note_gap(e, gap_ms, (int)(buffer_ms + 0.5)); // 判据基准 = 一个缓冲周期（事件驱动下的正常间隔）
        // I 轮探针：唤醒晚于 1.5 个周期 = 设备这一周期已经断供（静音/抖动已发生，
        // 只是 ring 深、pop 不缺数据，所以欠载计数器看不见）。
        if (gap_ms > buffer_ms * 1.5) {
            e->late_wakes.fetch_add(1, std::memory_order_relaxed);
        }

        // 只补空闲空间改为"每次填满整个设备缓冲"（与 ECHO 内核同策略）。
        // GetCurrentPadding 现在只作两用：设备失效探测 + 异常保护。
        // 独占 + 事件驱动下 render_event 到达时本周期已播完，pad 应为 0。
        UINT32 pad = 0;
        if (e->client->GetCurrentPadding(&pad) != S_OK) {
            std::fprintf(stderr, "[celeste-core] GetCurrentPadding 失败（设备失效/拔除？），渲染线程退出\n");
            e->failed.store(1, std::memory_order_release);
            break;
        }
        // I 轮探针：事件驱动下 render_event 到达时本周期应已播完，pad 必须为 0。
        // >0 = 事件早到（相位漂移）或上一次的信号没被消费（合并周期）——两者都让
        // 本次写入踏进设备还没播完的缓冲，是"统计全绿却卡"的候选根因。
        int32_t pm = e->pad_max.load(std::memory_order_relaxed);
        while ((int32_t)pad > pm &&
               !e->pad_max.compare_exchange_weak(pm, (int32_t)pad, std::memory_order_relaxed)) {
            // 失败时 pm 已刷新为最新值，重试
        }
        if (pad >= e->buffer_frames) continue;  // 异常：缓冲竟还满着，等下一事件再写

        const int to_fill = (int)e->buffer_frames;

        BYTE* dst = nullptr;
        if (e->render->GetBuffer((UINT32)to_fill, &dst) != S_OK) {
            std::fprintf(stderr, "[celeste-core] GetBuffer 失败（设备失效/拔除？），渲染线程退出\n");
            e->failed.store(1, std::memory_order_release);
            break;
        }

        const int need = to_fill * e->frame_bytes;
        if (e->paused.load(std::memory_order_acquire) != 0) {
            // 暂停 = 设备照转、输出静音（不停设备，避免独占重启爆音）
            memset(scratch.data(), 0, (size_t)need);
        } else {
            const int got = (int)e->ring.pop(scratch.data(), (size_t)need);
            if (got < need) {
                memset(scratch.data() + got, 0, (size_t)(need - got));
                if (!e->ring.input_ended()) {
                    // 真欠载：源没喂满（feeder 被冻/磁盘慢）——不是曲末的自然播空
                    e->underrun_callbacks.fetch_add(1, std::memory_order_relaxed);
                    e->underrun_frames.fetch_add((uint64_t)((need - got) / e->frame_bytes),
                                                 std::memory_order_relaxed);
                } else if (got == 0) {
                    e->drained.store(1, std::memory_order_release);  // 输入结束且存货播空
                }
            }
            if (e->fade_frames > 0) {
                apply_fade(e, scratch.data(), to_fill);
            }
        }

        memcpy(dst, scratch.data(), (size_t)need);  // 端点容器 == 源布局：整块直通
        if (e->render->ReleaseBuffer((UINT32)to_fill, 0) != S_OK) {
            std::fprintf(stderr, "[celeste-core] ReleaseBuffer 失败，渲染线程退出\n");
            e->failed.store(1, std::memory_order_release);
            break;
        }
        // I 轮探针：设备实际播放游标。与 framesPlayed 的差（滞后）稳定 = 设备在平稳
        // 消费；滞后持续增长 = 设备内部跟不上（USB 驱动交不够货），应用侧无感。
        // 注意：位置在 IAudioClock 上（IAudioClient 没有 GetPosition 成员）。
        if (e->clock != nullptr) {
            UINT64 dev_pos = 0, qpc_pos = 0;
            if (e->clock->GetPosition(&dev_pos, &qpc_pos) == S_OK) {
                e->device_pos.store((uint64_t)dev_pos, std::memory_order_relaxed);
            }
        }
        e->frames_played.fetch_add((uint64_t)to_fill, std::memory_order_relaxed);

        if (e->ring.input_ended() && e->ring.ready() == 0) {
            e->drained.store(1, std::memory_order_release);
        }
    }

    // 渲染线程 owning 设备：自己 Stop/Reset（与主线程竞争同一 COM 对象会 AccessViolation）
    if (!e->client_leaked_on_timeout && e->client != nullptr) {
        e->client->Stop();
        e->client->Reset();
    }

    if (timer_raised) timeEndPeriod(1);  // 必须成对归还，否则系统时钟被钉在 1ms
    if (avrt != nullptr) AvRevertMmThreadCharacteristics(avrt);
    com_leave(&com);
    return e->failed.load(std::memory_order_acquire) ? 1 : 0;
}

// ---------------------------------------------------------------------------
// start：解析设备 → 协商 → ring（按设备真实周期定容量）→ 预填 → prime → Start → 起线程
// ---------------------------------------------------------------------------

static int start_engine_impl(celeste_engine* e, uint32_t rate, uint32_t channels,
                             uint32_t requested_buffer_frames, const wchar_t* device_id,
                             uint32_t source_tag, const void* prefill, uint32_t prefill_frames,
                             char* error, size_t error_len)
{
    com_scope com = com_enter();
    if (FAILED(com.hr)) {
        set_error(error, error_len, "COM 初始化失败", com.hr);
        return -1;
    }

    const int frame_bytes = (int)(fmt_bytes_per_sample(source_tag) * channels);

    IMMDevice* device = resolve_device(device_id, error, error_len);
    if (device == nullptr) {
        com_leave(&com);
        return -1;
    }

    IAudioClient* client = nullptr;
    uint32_t buffer_frames = 0;
    HRESULT hr = init_exclusive_once(device, source_tag, rate, channels, requested_buffer_frames,
                                     &client, &buffer_frames, error, error_len);
    if (FAILED(hr)) {
        device->Release();
        com_leave(&com);
        return (hr == E_PENDING) ? -3 : (hr == AUDCLNT_E_UNSUPPORTED_FORMAT ? -2 : -1);
    }

    hr = client->GetService(__uuidof(IAudioRenderClient), (void**)&e->render);
    if (FAILED(hr) || e->render == nullptr) {
        set_error(error, error_len, "获取 IAudioRenderClient 失败", hr);
        client->Release();
        device->Release();
        com_leave(&com);
        return -1;
    }
    // I 轮探针：设备播放游标走 IAudioClock 服务（失败不致命，只少一条诊断轴）
    client->GetService(__uuidof(IAudioClock), (void**)&e->clock);
    e->client = client;
    e->buffer_frames = buffer_frames;
    e->rate = rate;
    e->channels = channels;
    e->source_tag = source_tag;
    e->frame_bytes = frame_bytes;
    snprintf(e->endpoint_name, sizeof(e->endpoint_name), "%s", fmt_tag_name(source_tag));

    // ring 容量用设备真实周期重算（对齐 dance 后周期可能变大）
    const int reserve_frames = (int)((double)rate * kRingReserveMs / 1000.0 + 0.5);
    int capacity_frames = (int)buffer_frames + reserve_frames;
    if (capacity_frames > kRingMaxFrames) capacity_frames = kRingMaxFrames;
    e->capacity_frames = capacity_frames;
    e->ring.init((size_t)capacity_frames * (size_t)frame_bytes);

    e->render_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    e->stop_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (e->render_event == nullptr || e->stop_event == nullptr) {
        set_error(error, error_len, "创建事件失败", HRESULT_FROM_WIN32(GetLastError()));
        e->render->Release();
        e->render = nullptr;
        client->Release();
        device->Release();
        com_leave(&com);
        return -1;
    }
    hr = client->SetEventHandle(e->render_event);
    if (FAILED(hr)) {
        set_error(error, error_len, "SetEventHandle 失败", hr);
        CloseHandle(e->render_event);
        e->render_event = nullptr;
        CloseHandle(e->stop_event);
        e->stop_event = nullptr;
        e->render->Release();
        e->render = nullptr;
        client->Release();
        device->Release();
        com_leave(&com);
        return -1;
    }

    // 预填（feeder 已读好的 ≤一个周期真实音频，起播即出声）
    if (prefill != nullptr && prefill_frames > 0) {
        e->ring.push(prefill, (size_t)prefill_frames * (size_t)frame_bytes);
    }

    // prime 首个设备缓冲：直接灌真实音频（不填静音，否则每首歌开头白聋一整段）
    {
        BYTE* prime = nullptr;
        hr = e->render->GetBuffer(buffer_frames, &prime);
        if (SUCCEEDED(hr)) {
            const int need = (int)buffer_frames * frame_bytes;
            std::vector<uint8_t> tmp((size_t)need, 0);
            const int got = (int)e->ring.pop(tmp.data(), (size_t)need);
            if (got > 0) memcpy(prime, tmp.data(), (size_t)got);
            hr = e->render->ReleaseBuffer(buffer_frames, 0);
        }
        if (FAILED(hr)) {
            set_error(error, error_len, "预填设备缓冲失败", hr);
            goto fail_after_events;
        }
    }

    device->Release();
    device = nullptr;

    hr = start_with_timeout(client);
    if (hr == E_PENDING) {
        // 驱动 Start 挂住：线程未起、设备状态未知——COM 对象交给 abandoned future，
        // 本侧不再碰任何 COM（防在挂起的驱动里二次调用死锁）。
        e->client_leaked_on_timeout = true;
        e->failed.store(1, std::memory_order_release);
        com_leave(&com);
        return -3;
    }
    if (FAILED(hr)) {
        set_error(error, error_len, "WASAPI Start 失败（设备被占用？）", hr);
        goto fail_after_events;
    }

    e->thread = CreateThread(nullptr, 0, render_proc, e, 0, nullptr);
    if (e->thread == nullptr) {
        set_error(error, error_len, "创建渲染线程失败", HRESULT_FROM_WIN32(GetLastError()));
        client->Stop();  // 设备已 Start：先停掉（此刻只有主线程碰 COM，无竞争）
        client->Reset();
        goto fail_after_events;
    }

    e->started.store(true, std::memory_order_release);
    com_leave(&com);
    std::fprintf(stderr,
                 "[celeste-core] started: rate=%u ch=%u bufferFrames=%u ringFrames=%d format=%s poll~%dms\n",
                 rate, channels, buffer_frames, capacity_frames, fmt_tag_name(source_tag),
                 (int)((double)buffer_frames * 1000.0 / (double)rate / 8.0 + 0.5));
    return 0;

fail_after_events:
    CloseHandle(e->render_event);
    e->render_event = nullptr;
    CloseHandle(e->stop_event);
    e->stop_event = nullptr;
    if (e->clock != nullptr) { e->clock->Release(); e->clock = nullptr; }
    e->render->Release();
    e->render = nullptr;
    client->Release();
    if (device != nullptr) device->Release();
    com_leave(&com);
    return -1;
}

// ---------------------------------------------------------------------------
// C ABI（C# P/Invoke 用）。约定：返回 0=成功，负数=错误码；帧 = 每声道采样点数。
// ---------------------------------------------------------------------------

extern "C" {

// 打开独占输出并起渲染线程。源格式标签决定端点容器协商（只接受同布局=bit-perfect）。
// prefill/prefillFrames：起播预填的真实音频（≤一个周期，C# feeder 先读好）。
__declspec(dllexport) int celeste_core_start(uint32_t sampleRate, uint32_t channels,
                                             uint32_t requestedBufferFrames,
                                             const wchar_t* deviceId,
                                             uint32_t sourceFormatTag,
                                             const void* prefill, uint32_t prefillFrames,
                                             void** outEngineHandle)
{
    if (outEngineHandle == nullptr) return -100;
    *outEngineHandle = nullptr;
    if (sampleRate < kMinRate || sampleRate > kMaxRate) return -101;
    if (channels < 1 || channels > kMaxChannels) return -102;
    if (requestedBufferFrames == 0) return -103;
    if (!fmt_tag_valid(sourceFormatTag)) return -104;
    if (prefillFrames > 0 && prefill == nullptr) return -105;

    auto* engine = new (std::nothrow) celeste_engine();
    if (engine == nullptr) return -106;

    char error[512] = { 0 };
    const int rc = start_engine_impl(engine, sampleRate, channels, requestedBufferFrames, deviceId,
                                     sourceFormatTag, prefill, prefillFrames, error, sizeof(error));
    if (rc != 0) {
        std::fprintf(stderr, "[celeste-core] start failed rc=%d err=%s\n", rc,
                     error[0] != '\0' ? error : "(no detail)");
        delete engine;
        return rc;
    }
    *outEngineHandle = engine;
    return 0;
}

// feeder 推送整数字节（交错）。ring 满时阻塞（背压，stop 时返回 -3 中断）。
// 返回 0=成功；负数见头注释。
__declspec(dllexport) int celeste_core_write(void* engineHandle, const void* bytes,
                                             uint32_t byteCount, uint32_t formatTag)
{
    auto* e = static_cast<celeste_engine*>(engineHandle);
    if (e == nullptr || bytes == nullptr) return -1;
    if (!e->started.load(std::memory_order_acquire)) return -2;
    if (formatTag != e->source_tag) return -4;  // 格式标签与会话不符：拒绝，防流错位
    if (byteCount == 0) return 0;
    if (e->frame_bytes > 0 && byteCount % (uint32_t)e->frame_bytes != 0) return -5;  // 非整帧
    return e->ring.push(bytes, byteCount) ? 0 : -3;
}

// 换歌/seek：清空缓冲、灌入新数据、重置统计与 declick 淡入（防爆音）。
// 仅允许 feeder 线程调用（单一 push 方）。返回灌入的帧数。
__declspec(dllexport) int celeste_core_replace(void* engineHandle, const void* bytes,
                                               uint32_t byteCount, uint32_t formatTag)
{
    auto* e = static_cast<celeste_engine*>(engineHandle);
    if (e == nullptr) return -1;
    if (!e->started.load(std::memory_order_acquire)) return -2;
    if (formatTag != e->source_tag) return -4;
    if (byteCount > 0 && (e->frame_bytes <= 0 || byteCount % (uint32_t)e->frame_bytes != 0)) return -5;

    e->ring.clear();
    e->frames_played.store(0, std::memory_order_release);
    e->drained.store(0, std::memory_order_release);
    if (bytes != nullptr && byteCount > 0) {
        if (!e->ring.push(bytes, byteCount)) return -3;
    }
    // 淡入 10ms（pcm24 packed 跳过，见 apply_fade）
    e->fade_frames = (e->source_tag == CELESTE_FMT_PCM24)
        ? 0
        : (int)((double)e->rate * kFadeMs / 1000.0 + 0.5);
    return (int)(e->frame_bytes > 0 ? byteCount / (uint32_t)e->frame_bytes : 0);
}

// 标记当前输入到头（剩余存货播完即 drained，不再计欠载）
__declspec(dllexport) int celeste_core_mark_input_ended(void* engineHandle)
{
    auto* e = static_cast<celeste_engine*>(engineHandle);
    if (e == nullptr) return -1;
    e->ring.mark_input_ended();
    return 0;
}

// 暂停/恢复：设备继续运转，输出静音（不停设备，避免独占重启爆音）
__declspec(dllexport) int celeste_core_set_paused(void* engineHandle, int paused)
{
    auto* e = static_cast<celeste_engine*>(engineHandle);
    if (e == nullptr) return -1;
    e->paused.store(paused != 0 ? 1 : 0, std::memory_order_release);
    return 0;
}

// 统计快照。maxGapMs/spikeCount 是「自上次调用起」的窗口值（读走即清零），
// 其余字段口径见 celeste_core.h。
__declspec(dllexport) int celeste_core_stats(void* engineHandle, celeste_core_stats_t* out)
{
    auto* e = static_cast<celeste_engine*>(engineHandle);
    if (e == nullptr || out == nullptr) return -1;
    if (out->structSize < static_cast<uint32_t>(sizeof(celeste_core_stats_t))) return -2;  // C# 结构体更旧：拒绝越界读

    out->sampleRate = e->rate;
    out->channels = e->channels;
    out->bufferFrames = static_cast<int32_t>(e->buffer_frames);
    out->readyFrames = static_cast<int32_t>(e->ring.ready() / (size_t)(e->frame_bytes > 0 ? e->frame_bytes : 1));
    out->capacityFrames = static_cast<int32_t>(e->capacity_frames);
    out->framesPlayed = e->frames_played.load(std::memory_order_relaxed);
    out->underrunCallbacks = e->underrun_callbacks.load(std::memory_order_relaxed);
    out->underrunFrames = e->underrun_frames.load(std::memory_order_relaxed);
    out->paused = e->paused.load(std::memory_order_relaxed);
    out->drained = e->drained.load(std::memory_order_relaxed);
    out->failed = e->failed.load(std::memory_order_relaxed);
    out->maxGapMs = e->max_gap_ms.exchange(0, std::memory_order_relaxed);
    out->spikeCount = e->spike_count.exchange(0, std::memory_order_relaxed);
    out->devicePosition = e->device_pos.load(std::memory_order_relaxed);
    out->padMaxFrames = e->pad_max.exchange(0, std::memory_order_relaxed);
    out->lateWakeups = e->late_wakes.exchange(0, std::memory_order_relaxed);
    snprintf(out->format, sizeof(out->format), "%s",
             e->endpoint_name[0] != '\0' ? e->endpoint_name : "?");
    return 0;
}

// 停渲染线程、关设备（可重复调用）
__declspec(dllexport) void celeste_core_stop(void* engineHandle)
{
    auto* e = static_cast<celeste_engine*>(engineHandle);
    if (e == nullptr) return;
    e->stop_requested.store(true, std::memory_order_release);
    e->started.store(false, std::memory_order_release);
    e->ring.request_stop();  // 放行阻塞中的 feeder push
    if (e->stop_event != nullptr) SetEvent(e->stop_event);

    if (e->thread != nullptr) {
        if (WaitForSingleObject(e->thread, kStopJoinMs) == WAIT_OBJECT_0) {
            CloseHandle(e->thread);
            e->thread = nullptr;
        } else {
            // 渲染线程没按时退出：标记失败并整体泄漏（它还引用引擎内的 COM/事件），
            // 进程退出时收尸。绝不 close 它还在 wait 的事件句柄。
            e->failed.store(1, std::memory_order_release);
            e->engine_leaked = true;
            CloseHandle(e->thread);  // 句柄可关（线程对象本身由泄漏持有）
            e->thread = nullptr;
            return;
        }
    }

    // 渲染线程退出时已 Stop/Reset 过设备；这里只释放 COM 与事件
    if (!e->client_leaked_on_timeout) {
        if (e->render != nullptr) {
            e->render->Release();
            e->render = nullptr;
        }
        if (e->clock != nullptr) {
            e->clock->Release();
            e->clock = nullptr;
        }
        if (e->client != nullptr) {
            e->client->Release();
            e->client = nullptr;
        }
    }
    if (e->render_event != nullptr) {
        CloseHandle(e->render_event);
        e->render_event = nullptr;
    }
    if (e->stop_event != nullptr) {
        CloseHandle(e->stop_event);
        e->stop_event = nullptr;
    }
}

// 释放引擎（stop 之后的收尾；直接对运行中的句柄调用会先 stop）
__declspec(dllexport) void celeste_core_destroy(void* engineHandle)
{
    auto* e = static_cast<celeste_engine*>(engineHandle);
    if (e == nullptr) return;
    celeste_core_stop(e);
    if (e->engine_leaked) return;  // 渲染线程可能还在跑：不释放内存
    delete e;
}

} // extern "C"
