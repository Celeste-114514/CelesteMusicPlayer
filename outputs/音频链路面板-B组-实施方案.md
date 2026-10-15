# 音频链路面板（B 组）实施方案 · 2026-10-16 修订版

> 状态：**B1 勘察已完成，入口已定（B：播放页右上角按钮→独立大面板），
> 分组可收起（用户 2026-10-16 拍板）**。B2 数据模型待用户批准后动工。
> 本版取代 10-15 旧清单里的 B 组条目——旧条目说"方案在旧清单、六阶段、单独排期"，
> 但没写六阶段具体是什么；本文把六阶段落到当前代码现状上，逐条给证据（file:line）。
>
> ⚠️ 先纠正一个流传了两轮的误会：**09-22 那四个提交（8766b46 / 7eb8492 / f78aca9）
> 做的"音频链路"是格式显示层 + 独占协商 + 限幅默认关，早已推送 origin/main**。
> 它和本文的面板是两回事：那个解决"数字别显示错"，本文要做的是 **ECHO 风格的
> 信号路径可视化面板**（纵向链路图 + 每节点真实状态 + 实时电平 + 事件时间线）。

---

## 0. 一页摘要（大白话）

**要做的东西**：播放页上能打开一个「音频链路」面板，把声音从文件到喇叭的整条路
画成一张**纵向链路图**——每个环节一块卡片（解码 → 缓存 → 无缝 → 降混 → 变速 →
DSP 机架 → 输出），卡片上写着这个环节**此刻的真实状态**：什么格式进出、开没开、
实际延迟多少。底部一条 bit-perfect 结论。外加实时电平条、可折叠的诊断与事件时间线。

**铁律**（B1 第一条结论）：面板只当"读数器"——**拿不到的数据显示"未知"，绝不编数值**；
面板不碰音频线程（电平表已有 30fps 消费路径，面板关掉就停刷新）。

**六阶段**：① 勘察（✅ 本轮已完成）→ ② 数据模型 `AudioSignalPath` → ③ 面板 UI →
④ 接真实状态 → ⑤ 主题适配 → ⑥ 验收。每阶段独立 commit、独立回退、出可运行版本。

---

## 1. B1 架构勘察报告（✅ 2026-10-16 完成）

按面板需要的数据逐项查了当前代码。结论：**七成数据已经存在且可信**，缺的主要是
"统一快照"和"事件历史"，不用新造采集逻辑。

### 1.1 已经拿到、可直接复用的

| 面板要显示的数据 | 现在在哪 | 证据 |
|---|---|---|
| 源文件真实格式（率/位深/声道/是否浮点） | `ChainFormatState.SourceFile`（结构化，不解析字符串） | `AudioChainFormat.cs:57`；产出点 `FfmpegDecoderBackend.cs:62` |
| 实际送链路的转码 WAV 格式 | `ChainFormatState.TranscodeWav` | `AudioChainFormat.cs`；填点 `HiFiOutputBackend.cs`（PlayWavAsync 出口） |
| 设备端协商格式 + 无损/降级判定 | `ChainFormatState.DeviceOutput` / `Device` | `AudioChainFormat.cs`；三个输出后端都实现了 `NegotiatedFormat`/`DevicePathKind`：`NativeWasapiExclusiveOut.cs:75`、`NativeCoreOutput.cs:116`、`EchoCoreOutput.cs:63` |
| 转码结果（同格式/容器扩容/重采样/降级/兜底） | `ChainFormatState.Outcome`（枚举） | `AudioChainFormat.cs` `TranscodeOutcome` |
| 变速开没开 | `ChainFormatState.TempoShifted`；倍率 `AppSettingsStore.PlaybackRate` | `AudioChainFormat.cs:90`；`HiFiOutputBackend.cs:1445` |
| 输出模式（共享/独占/ASIO） | `HiFiOutputBackend.OutputMode` 枚举 + `ChainFormat.SharedMode` | `HiFiOutputBackend.cs:17`、`:1543` |
| 当前设备 ID | `_audioEngine.OutputDeviceId` | `MainWindow.Features.cs:3192` |
| DSP 八模块各自"是否真在处理" | `RackModuleActive(moduleId)`——注意它读的是 **UI 控件状态**（toggle/slider），不是引擎状态 | `MainWindow.DspRack.cs:578` |
| DSP 全局旁路 | `DspRackGlobalBypassed` | `MainWindow.DspRack.cs:504` |
| 模块顺序（机架编排） | `_dspRackOrderDraft`（与盘上值一致） | `MainWindow.DspRack.cs:511` |
| 实时电平（post-DSP 每声道峰值/RMS） | `LevelMeter`（线程安全），UI 侧 30fps 消费 | `LevelMeter.cs:8`；`MainWindow.LevelMeter.cs:14` |
| 输出缓冲设置值 | `OutputBufferMs`（20~500ms） | `HiFiOutputBackend.cs:262` |
| 卷积引入的延迟（帧） | `ConvolutionProcessor` IR 长度；引擎暴露延迟帧 | `ConvolutionProcessor.cs:144/163`；`AudioPlaybackEngine.cs:44` |
| SRC 会话状态（源→目标/未升频原因） | `RefreshSrcSessionState()` 已有整套展示 | `MainWindow.Src.cs:158` |
| bit-perfect 综合判定 | `EvaluateBitPerfectChain`（标志位判定，不反解析字符串） | `MainWindow.Features.cs:3343` |
| DAC 独占支持率列表 | `ProbeExclusivePlan` → `ExclusivePlan.SupportedRates`（按设备缓存，含 300ms 复核并集） | `HiFiOutputBackend.cs:857/889` |
| 设备变更事件 | `AudioDeviceWatcher.DevicesChanged` | `AudioDeviceWatcher.cs:24` |
| 无缝切歌事件 | `SeamlessTrackChanged` | `AudioPlaybackEngine.cs:185` |
| 播放位置/时长 | `EnginePositionValue` / `EngineDurationValue` | `MainWindow.Features.cs:3159` |
| 软件音量是否激活 | `_audioEngine.IsSoftwareVolumeActive` | `MainWindow.Features.cs:3132` |
| 限幅/余量状态 | `DspExtraStore.Safety`（EnableLimiter / AffectsBits） | `MainWindow.Features.cs:3129` |

### 1.2 部分拿到、要补一小口的

| 数据 | 现状 | 要补什么 |
|---|---|---|
| DAC 能力图谱 | 支持率列表在 `s_supportedRatesCache`（private static） | 加一个只读访问器给面板用（B4，十行内） |
| 限幅激活口径 | 已有"生效/待命"口径（随链生效 vs 单独待命） | 直接复用 `MainWindow.Features.cs:3139-3146` 的判定，搬进模型 |
| 每节点输入/输出格式 | 只有链首/链尾两个点有结构化值 | 中间节点（降混后声道数、变速后时长基准）按相邻节点推导，推导不了的显示"—" |

### 1.3 拿不到的（按铁律显示"未知"，不编）

| 数据 | 为什么拿不到 |
|---|---|
| 设备端真实缓冲延迟 | WASAPI 共享模式设备延迟 NAudio 不直接暴露；面板显示**设置值 + 理论值**并标注"设置值" |
| ASIO 驱动实际延迟 | 驱动相关；显示驱动缓冲设置值（现有 ASIO 卡顿提示条同源，`MainWindow.Features.cs:3180`） |
| 解码首帧耗时 | 没有埋点；B6 验收时不作要求，将来要加再在解码出口补一行日志 |
| DoP 直出时的链内节点 | DSD DoP 直出不过 DSP 链（既定设计），面板该情形只显示"DSD DoP 直出（不过处理链）" |

### 1.4 B1 结论

- **不用新造采集**：链路图需要的状态 100% 有来源，大部分已结构化。
- **唯一的真缺口是"统一快照 + 历史"**：现在这些值散在 `ChainFormatState`（引擎侧）、
  `RackModuleActive`（UI 控件侧）、`DspExtraStore`（设置侧）三处，面板逐个去问会
  和三处口径打架（09-22 徽标谎报的教训）。所以 B2 的模型层是必须的，不是镀金。
- **`RackModuleActive` 读 UI 控件**是 B2 的最大注意点：模型层不能依赖 MainWindow 的
  toggle/slider，要把同样的判定改成读 `DspState`/各 Store（判定条件一一对照着搬，
  搬完做等价性抽查，和项目里 PlaybackOrderResolver 的做法一样）。

---

## 2. B2 统一数据模型 `AudioSignalPath`（下一阶段，设计如下）

**新文件 `CelesteMusicPlayer/AudioSignalPath.cs`**——UI 只读这一份模型，不跨层问数据。

```csharp
/// <summary>链路一个环节的快照。</summary>
sealed class SignalNode
{
    string Id;              // "decode" / "cache" / "gapless" / "downmix" / "tempo" / "dsp:<模块>" / "output"
    string Title;           // 展示名（"解码" / "无缝接力" / "DSP · 31 段 EQ"）
    NodeState State;        // Active / Bypassed / Standby / Off / Unknown
    AudioFormat? InFormat;  // 进这个节点的格式（推导不出=null → 面板显示"—"）
    AudioFormat? OutFormat; // 出这个节点的格式
    string? Note;           // 一句话注（"已重采样 96k→48k" / "限幅待命"）
    double? LatencyMs;      // 只有有真实值的节点填（卷积/缓冲），其余 null
}

enum NodeState { Active, Bypassed, Standby, Off, Unknown }

sealed class AudioSignalPath
{
    static AudioSignalPath Capture();      // 从引擎/设置/缓存抓一份快照（UI 线程可调）
    IReadOnlyList<SignalNode> Nodes;       // 按链序
    string SummaryLine;                    // 顶部摘要一行
    bool BitPerfect; string VerdictReason; // 与 EvaluateBitPerfectChain 同口径（复用不重写）
    IReadOnlyList<ChainEvent> RecentEvents;// 环形缓冲，最近 N 条（切歌/切设备/模式变化/协商结果）
    double[]? Levels; int LevelChannels;   // 指向 LevelMeter 的引用快照（B4 接）
}
```

**`Capture()` 的数据来源**（全部来自 B1 表）：`_audioEngine.ChainFormat` + `DspState`
+ `DspExtraStore`/`EqCurveStore`/`ReplayGainStore`/`RoomCorrectionStore` + 机架顺序
+ `ProbeExclusivePlan` 缓存 + `OutputBufferMs`。**判定逻辑从 UI 控件侧搬到设置侧时，
逐条对照 `RackModuleActive`（`MainWindow.DspRack.cs:578`）搬，搬完写个小测试比对两边一致。**

**事件时间线**（B2 顺带立环形缓冲，B4 接）：切歌 / 切设备 / 切输出模式 / 独占协商结果 /
变速开关，各记一行（时间戳 + 一句话）。缓冲上限 50 条，面板关着不记也无妨（便宜）。

---

## 3. B3 面板 UI（设计）

**布局**（ECHO 风格纵向链路图为核心）：

```
┌─────────────────────────────────────┐
│ 音频链路            [bit-perfect 徽标] │  顶部摘要：源格式 → 输出格式 + 结论一行
├─────────────────────────────────────┤
│ ▮ 电平条（实时，post-DSP）            │  当前阶段条：跟随播放位置高亮所在节点
├─────────────────────────────────────┤
│ ▼ 纵向链路图（核心，可滚动）          │
│   ┌ 解码 ─────────── 44.1k/16b/2ch ┐ │
│   ┌ PCM 缓存 ──────── 同格式直通 ─  │ │
│   ┌ 无缝接力 ──────── 下一首已预载 ─│ │
│   ┌ 降混 ─────────── 未启用（源即2ch）│
│   ┌ 变速 ─────────── 1.00× 直通 ─   │ │
│   ┌ DSP 机架（8 模块横排小卡+顺序）   │ │
│   ┌ 输出 ─────────── 独占 · PCM24 ─  │ │
├─────────────────────────────────────┤
│ ▸ 链路诊断（可折叠）：每节点 Note 汇总 │
│ ▸ 输出事件时间线（可折叠）            │
│ ▸ DAC 能力图谱入口（支持率列表）      │
├─────────────────────────────────────┤
│ 底部确认行：结论 + "与音频设置面板同口径"│
└─────────────────────────────────────┘
```

**入口（2026-10-16 用户已拍板）**：
- ✅ **B. 播放页右上角（10-15 注释里预留的那块）放个按钮，点开独立大面板**——已定案。
- A（音频设置第 5 页签）：否（flyout 高度受限，纵向链路图展不开）；
- C（信息区小字扩小面板）：否（用户原话"C 去除就好了"）。

**收起/展开（2026-10-16 用户补充）**：用户看过的 ECHO 面板「很多选项可以收起，
给我看的是**全部展开**的信息」。所以本面板每个分组（电平条 / 链路诊断 / 事件时间线 /
DAC 图谱）都是**可折叠节**（Expander 风），折叠箭头常驻；默认收起的是「链路诊断」和
「事件时间线」，电平条和链路图核心常展。折叠状态**不持久化**（每次打开回到默认，
避免用户忘了收起觉得乱）。

---

## 4~6. B4/B5/B6 概要

- **B4 接真实状态**：曲目切换/设备切换/模式变化/变速开关 → 重抓快照；电平条 30fps
  （照搬 `MainWindow.LevelMeter.cs` 的定时器范式）；**面板不可见就停所有定时器**。
- **B5 主题适配**：链路图卡片用主题资源（CardBackground/CardStroke/SubtleFill），
  状态色复用 `DspRackStateBrush` 的三套皮肤逻辑（经典/极客/令牌），不新造配色。
- **B6 验收清单**：播放/暂停/切歌（含跨格式）/切设备/切输出模式（共享↔独占↔ASIO）/
  开关变速/开关各 DSP 模块/总旁路/开关面板——每个场景核对链路图状态与音频设置面板
  **同一口径**（两处都读同一个模型，不可能不一致，这正是 B2 的意义）。

---

## 7. 风险与回退

| 风险 | 缓解 |
|---|---|
| 模型层判定与 UI 控件侧漂移 | 搬判定时写等价性抽查（同 PlaybackOrderResolver 做法）；两处最终只留模型一处 |
| 电平刷新拖累音频线程 | 只读 LevelMeter 已有快照，不新增音频线程工作；不可见即停 |
| 面板显示"未知"太多 | B1 已核过：需要显示的都有来源；未知只出现在设备延迟等物理拿不到项，且明确标注 |
| 大 XAML 搬移引发 XamlCompiler 崩 | 面板作为独立 UserControl 新文件（不搬现有 XAML），新文件用简单结构；崩了按既定清 obj/bin 重试 |
| 每阶段回退 | 单 commit revert；B2/B3 不动音频路径，回退零听感风险 |
