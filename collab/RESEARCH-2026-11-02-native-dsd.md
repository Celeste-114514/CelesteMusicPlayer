# Native DSD 调研：参考项目与接入路线（2026-11-02）

> 目标：把「DSD 只能走 DoP」升级成「支持就用原生 DSD，不支持再回退 DoP/PCM」。
> 本机实测：注册表 `HKLM\SOFTWARE\ASIO` 里只有 **FiiO ASIO Driver**
> → `c:\program files\fiio\fiio_driver\x64\fiio_usbaudioasio_x64.dll`。
> FiiO 的 ASIO 驱动是 **Thesycon TUSBAudio** 那一套（飞傲、拓品、XMOS 方案都用它），
> 而 Thesycon 官方文档明确写了：**native DSD 只通过 ASIO 提供，DSD64–1024**。
> ⇒ **结论：这台机器上要做的原生 DSD，路线就是 ASIO，不是 WASAPI。**

---

## 一、可直接参考的项目（按有用程度排）

### 1. HiFi Player —— 最值得抄的一份（**GPL-3.0，开源，Rust**）
- https://github.com/ashenjay/hifiplayer
- 它做的事和我们完全一样：Windows、bit-perfect、ASIO + WASAPI 独占、
  **native DSD over ASIO（DSD64–512）**、DoP 与 DSD→PCM 回退、无缝播放。
- 关键文件（直接看这几处就够）：
  - `src/output/asio/ffi.rs` —— **裸 ASIO 驱动接口**（vtable 手写绑定，没用 Steinberg SDK）。
    这就是我们缺的那一层，可以照着翻成 C#。
  - `src/dsd/` —— DoP 打包 + DSD→PCM 转换（对照我们现有的 `DsdDopPipeline.cs`）。
  - `src/engine.rs` —— 输出路径选择与回退策略（什么时候用 native / DoP / PCM）。
- 它的 README 里有一句很关键：**「There is no volume control, because a bit-perfect stream can't have one」**
  —— 原生 DSD 下软件音量必须禁用，这是硬约束不是设计口味。
- 许可证：GPL-3.0。我们是 AGPL-3.0，**方向兼容**（GPLv3 代码可以并进 AGPLv3 作品），
  照抄代码要在 LICENSE 的第三方清单里注明来源。

### 2. ffaudio —— 同款硬件的先例（闭源，但有价值）
- https://ffaudio.cc/history-zh
- 2026-09-05 的更新日志：**「新增 THESYCON ASIO 驱动的 DSD Native 输出，覆盖飞傲、拓品等采用该驱动的设备」**。
- 用途：证明 **FiiO + Thesycon ASIO 走 native DSD 是通的**（你的 KA13 正是这一类）。
  它闭源，但可以拿它的实现时间点、支持范围当对照；遇到问题也可以对照它的更新日志看有没有已知坑。

### 3. Thesycon 官方文档 —— 事实来源
- http://thesycon.com/eng/usb_audiodriver.shtml
- 明确写的：ASIO 2.3.1；**ASIO DSD mode supported (playback and recording)**；
  DSD64/128/256/512/1024；native DSD 走 ASIO，DoP 走 ASIO 和 WDM。
- 这是判断「驱动到底支不支持、支持到几倍」的权威依据。

### 4. ASIO SDK 的采样类型定义 —— 代码层面的口径
- `ASIOSampleType` 里和 DSD 有关的三项（NAudio 里原样搬了这三项）：
  | 值 | 名称 | 含义 |
  |---|---|---|
  | 32 | `ASIOSTDSDInt8LSB1` | 1 bit，一字节 8 个采样，**低位在前**（DSF 用的就是这个） |
  | 33 | `ASIOSTDSDInt8MSB1` | 同上但**高位在前**（DFF/DSDIFF 常见） |
  | 40 | `ASIOSTDSDInt8NER8` | 8 bit 一采样，无字节序 |
- **选错这两个（32 还是 33）= 满耳噪声**，必须跟文件位序对齐。

### 5. foobar2000 + ASIO Proxy（foo_dsd_asio）—— 已验证的 Windows 配方（闭源）
- 经典做法：`foobar SACD 插件 → ASIO Proxy → Thesycon 驱动`，DSD Playback method 选 **ASIO Native**。
- 有三条经验值钱：
  1. **DSD 音量必须是最大**。Thesycon 面板里把音量调低，PCM 只是变小，DoP 会静音，
     而 **native DSD 会直接变成噪声**（DSD 是 1-bit 码流，线性缩放等于破坏编码）。
  2. 用 DAC 的指示灯判断当前到底是 PCM / DoP / native DSD 三种哪一种。
  3. DAC 如果不支持该 DSD 倍率，要在插件里降级，不能硬发。

### 6. Linux 侧（只取概念，代码不能直接用）
- **MPD** 的 ALSA 输出有 `dsd_native` / `dop` 两种模式；**squeezelite** 有 DSD 原生输出。
- 价值：确认「DSD64 = 2822400、DSD128 = 5644800、DSD256 = 11289600、DSD512 = 22579200」
  这套采样率映射，以及「欠载要补合法静音码型而不是 0」的做法。

---

## 二、我们这边真正的堵点（已核实，不是猜的）

1. **`AsioOut` 这条路走不通**：
   `AsioOut.Init()` 只认 `Float32LSB / Int32LSB / Int16LSB / Int24LSB`，
   其他类型走 `default: throw new NotSupportedException`；而且缓冲区大小是按
   `BitsPerSample / 8` 算的，DSD 是 1-bit → 算出来 0 字节。
   （这也解释了为什么当初只能做 DoP：把 DSD 塞进 24/32bit 容器骗过 AsioOut。）

2. **但 NAudio 的底层是够用的**（关键发现）：
   - `NAudio.Wave.Asio.AsioSampleType` 里 **已经有** `DSDInt8LSB1=32 / DSDInt8MSB1=33 / DSDInt8NER8=40`；
   - `AsioDriverExt` 是 **public**，`CreateBuffers(outCh, inCh, useMaxBufferSize)`、
     `FillBufferCallback`（可写）、`Start/Stop`、`SetSampleRate`、`IsSampleRateSupported`、
     `ShowControlPanel`、`Capabilities`（`OutputChannelInfos[i].type` 是可变字段）全都在。
   ⇒ **不用自己写 vtable 绑定**，也不用换掉 NAudio：绕开 `AsioOut`，直接用
   `AsioDriver` + `AsioDriverExt` 自己接管回调即可。改动量比照 `ffi.rs` 重写小一个数量级。

---

## 三、要动的地方（按现有代码结构）

| 位置 | 现在 | 要变成 |
|---|---|---|
| `HiFiOutputBackend.cs`（ASIO 分支 ~1257/1290） | `new AsioOut(...)` + `Init(喂料器)` | DSD 直出时改走新的 `AsioNativeDsdOutput`（`AsioDriverExt` 手接管） |
| `AsioFeederProvider.cs` | 面向 `IWaveProvider`（PCM 字节） | 新增 DSD 变体：喂 1-bit 字节流，环形缓冲逻辑可复用 |
| `DsdDopPipeline.cs` | 把 DSD 装箱成 DoP（24/32bit 容器） | 保留（DAC 不支持 native 时回退用）；原生路径不再装箱 |
| `AudioChainFormat.cs` / 音频链路面板 | 显示 DoP 容器格式 | 增加「Native DSD 2822400Hz 1bit」这种展示 |
| `AppSettingsStore` / 设置页 | `DsdOutputMode = Pcm \| Dop` | 加一档 `Native`，并按 DAC 能力自动回退 |

---

## 四、必须踩对的坑（按血亏程度排）

1. **软件音量必须强制 100%（或直接旁路）**。DSD 不能线性缩放，非满音量 = 噪声。
   我们现有的 `HiFiSoftwareVolume`、DSP 音量、ReplayGain 全部要在这条路上禁用。
2. **DSF / DFF 位序反了就是噪声**：DSF 是 LSB-first（用 32），DFF 多数 MSB-first（用 33）。
   读文件时就要把位序标出来，别到 ASIO 层再猜。
3. **采样率映射**：DSD64=2822400、128=5644800、256=11289600、512=22579200。
   驱动 `IsSampleRateSupported()` 逐个问；不支持就按用户设置回退 DoP → PCM。
4. **欠载/暂停要补「合法 DSD 静音码型」**（0x69 / 0x96 交替），填 0 会咔哒甚至让 DAC 失锁。
   我们 DoP 那边已经踩过（`AsioFeederProvider` 的 dopSilence），原生这条路照同样思路。
5. **一次 Init 不能改格式**：切 DoP ↔ native、切倍率都要 `Dispose` 重建。
6. **DSP / EQ / 重采样在原生 DSD 下无意义**：直接禁止或强制先转 PCM，并在界面上说清楚。
7. **驱动独占**：ASIO 下 WASAPI 那条路要彻底释放，否则设备被占用会失败。

---

## 五、建议的执行顺序

| 阶段 | 做什么 | 验收 |
|---|---|---|
| **P0 探测（半天）** | 写一个只读小工具：打开 FiiO ASIO → 打印驱动名/通道数/每通道采样类型/`IsSampleRateSupported(2822400…)`；再把采样率切到 2822400 重读通道类型 | 看到 `DSDInt8LSB1(32)` 这类值 = 驱动支持原生 DSD；同时确认支持到哪一档 |
| **P1 最小可播（1–2 天）** | 用 `AsioDriverExt` 手接管，喂纯 DSD 字节（不接 DSP、不做音量），放一首 DSF，DAC 指示灯变 DSD | 出声、无噪声、DAC 显示 DSD、日志有实际倍率 |
| **P2 接进现有管线（2–3 天）** | 接环形缓冲/无缝/进度/暂停；DSF 与 DFF 位序分别处理；欠载补合法静音 | 连播不乱、暂停恢复不炸、seek 正常 |
| **P3 设置与回退（1 天）** | 设置页加 `Native`；不支持自动回退 DoP→PCM 并写日志；链路面板显示真实格式 | 换个不支持 DSD 的 DAC 也不会哑 |

> P0 的结论决定后面怎么做：如果驱动直接报 DSD 类型，一切按上面走；
> 如果驱动不接受切换（只认 PCM），那这台机器上原生 DSD 就只能靠 ASIO Proxy 那套外部拦截，
> 我们不做，直接告诉用户「你的驱动不支持」比硬做更诚实。
