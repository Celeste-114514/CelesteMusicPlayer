# 音频链路面板（B 组）实施方案 · 2026-10-16 修订版

> 状态：**B1 勘察✅、B2 数据模型✅（`AudioSignalPath.cs`）、B3 面板 UI✅（2026-10-16）**。
> 入口 = **播放页右上角按钮**（`NowPlayingChainButton`，10-15 预留位）；形态 = **从右缘滑出的抽屉**
> （2026-10-16 用户改版，旧的"独立大面板"作废）；分组可收起（用户 2026-10-16 拍板）。
> 下一步 = B4 接真实状态（切歌/切设备/切模式时主动刷 + 两处旧显示改读本模型）、B5 主题适配、B6 验收。
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

**铁律**（B1 第一条结论；2026-10-16 用户修订第二句）：面板只当"读数器"——**拿不到的数据
整行不显示，绝不编数值、也不留"未知/—"占位**；面板不碰音频线程（电平表已有 30fps
消费路径，面板关掉就停刷新）。

**六阶段**：① 勘察（✅ 10-16 完成）→ ② 数据模型 `AudioSignalPath`（✅ 10-16 完成，
Debug 0 错误，未接 UI）→ ③ 右侧抽屉面板 UI（2026-10-16 改版，见第 3 节）→ ④ 接真实状态
→ ⑤ 主题适配 → ⑥ 验收。
每阶段独立 commit、独立回退、出可运行版本。

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
| 每节点输入/输出格式 | 只有链首/链尾两个点有结构化值 | 中间节点（降混后声道数、变速后时长基准）按相邻节点推导；推导不出的**该行不显示**（2026-10-16 修订） |

### 1.3 拿不到的（2026-10-16 用户修订：**整行不显示**，不留"未知/—"占位）

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

## 2. B2 统一数据模型 `AudioSignalPath`（✅ 2026-10-16 完成，Debug 构建 0 错误）

**新文件 `CelesteMusicPlayer/AudioSignalPath.cs`**（已落地，未接 UI）——UI 只读这一份模型，不跨层问数据。

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

**落地时对设计的修正（都有据）**：
- 八模块判定用 **Store 口径**（`DspRackStore`/`DspExtraStore`/`RoomCorrectionStore`/`EqCurveStore`/
  `ReplayGainStore` 的 IsActive/HasEffect 系列），与 `IsBitPerfectPure`（Features.cs:3297）**同源**，
  不是 `RackModuleActive`（DspRack.cs:578）的 UI 控件口径。两处差异（UI 开着总开关但全默认值）
  以 Store 口径为准——bit-perfect 判定本来就认总开关。B4 接线后 MainWindow 两处显示都改读模型。
- bit-perfect 判定按 `EvaluateBitPerfectChain`（Features.cs:3343）口径在模型内复刻，
  消除"两处各判各的"；MainWindow 后续 B4 切到读模型。
- 事件环形缓冲（50 条，`RecordEvent`/`RecentEvents`）已立，B4 接线（切歌/切设备/切模式/协商/变速/DSP）。
- 电平快照每次 `Capture` 顺带 `TryGetLevels` 拷贝；B4 面板刷新节流（快照 ≥100ms 重抓，电平 33ms）。
- DSD/DoP 直出：decode 节点标注"不过处理链"，中间节点不编造状态。
- 拿不到的设备端真实延迟/ASIO 驱动延迟：LatencyMs=null，**面板该行不显示**（铁律）。

**`Capture(engine, globalBypass)` 的参数说明**：globalBypass 设置侧无持久化，
由调用方传 UI toggle 现状——是参数不是"问 UI"。engine 可空（未建引擎全节点待命）。

---

## 3. B3 面板 UI（设计 · 2026-10-16 用户当场改版）

> ⚠️ 本节已被用户改版：**放弃"播放页右上角按钮 → 独立大面板"的旧方案**，
> 改成**从右侧滑出的抽屉式面板**，视觉风格对齐**左侧分类栏卡片**。
> 用户原话："和左侧分类栏做成类似的风格，效果就是从右侧弹出来的一个面板一样，
> 不要用现在已有的设计"。下面旧的 ASCII 布局图已删除，勿再照它实现。

**形态**：面板从窗口**右缘**滑入 / 滑出（抽屉），宽度约占内容区 1/3，覆盖在内容区之上。
过渡用 `RenderTransform.TranslateX` + 透明度 Storyboard（参考左栏收起/展开那套
"底板滑、内容补偿"写法），**不用** ContentDialog、不用独立 Window。

**风格对齐左栏卡片**（复用同一批令牌，禁新造配色）：
- 底色 `CardBackgroundFillColorDefaultBrush`、边线 `CardStrokeColorDefaultBrush`；
- 圆角贴右缘一侧（左栏贴左缘是 `0,8,8,0`，这里镜像成 `8,0,0,8`）；
- 阴影沿用左栏那套 `ThemeShadow` + `Translation.Z`（见 MainWindow.Features.cs 里的写法）；
- 分组标题与条目的 hover / 选中样式照抄左栏 nav 条目（透明底 Button + CornerRadius 8）。

**内部结构**（自上而下）：
1. 标题行：「音频链路」+ 右上角关闭按钮 + bit-perfect 徽标；
2. 摘要行：源格式 → 输出格式 + 结论一行；
3. 实时电平条（post-DSP 30fps；面板不可见即停，绝不新占音频线程）；
4. **纵向链路图（核心，可滚动）**：解码 → PCM 缓存 → 无缝接力 → 降混 → 变速 →
   DSP 机架（8 模块）→ 输出；每节点一张小卡，卡上写该节点**此刻的真实状态**；
5. 可折叠分组（Expander 风，箭头常驻）：链路诊断 / 输出事件时间线 / DAC 能力图谱；
6. 底部结论行（与音频设置面板同口径）。

**⚠️「显示不出来就不显示」铁律（2026-10-16 用户明确修订）**：
拿不到的数据**整行不出** —— 不写"未知"、不写"—"、不留空占位、不做灰显。
节点若整块数据都拿不到（例如 DSD DoP 直出时的链内节点），该卡只写一句状态说明
（"DSD DoP 直出，不过处理链"），不摆空的格式栏。
本铁律取代 10-15 版方案里"拿不到就显示未知"的写法。

**入口**：播放页右上角（10-15 注释里预留的那块）放入口按钮，点开右侧抽屉。
- A（音频设置第 5 页签）：否（flyout 高度受限，纵向链路图展不开）；
- C（信息区小字扩小面板）：否（用户原话"C 去除就好了"）。

**组内折叠**：每个分组都是可折叠节（Expander 风），折叠箭头常驻；默认收起「链路诊断」
与「事件时间线」，电平条与链路图核心常展。折叠状态**不持久化**（每次打开回默认，
免得用户忘了收起觉得乱）。

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
| 面板"显示不出来"被误当成漏做 | 铁律已改成整行不显示；B6 验收核对的是"该显示的都在、拿不到的确实没占位"，不是"每行都有值" |
| 大 XAML 搬移引发 XamlCompiler 崩 | 面板作为独立 UserControl 新文件（不搬现有 XAML），新文件用简单结构；崩了按既定清 obj/bin 重试 |
| 每阶段回退 | 单 commit revert；B2/B3 不动音频路径，回退零听感风险 |
