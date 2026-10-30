# W13 播放会话：搬之前先把话说清楚（2026-10-30）

> 这是**盘点**，不是改造方案。目的是让下一刀（人或 AI）动手前就知道接口该长什么样、
> 哪一段必须出声才能验、哪一段搬错了会静默变差。

## 一、一句话结论

**引擎对象本身是干净的，脏的是"谁在调它"。**

`AudioPlaybackEngine.cs` 888 行、`IDisposable`、属性全是对 `_hifiOut` 的转发，没有一行 UI。
问题在驱动它的人：`MainWindow` 的播放域 9,436 行里，**起播、接续、失败提示、进度条、波形计时器
是混在同一个方法里的**。所以这一刀不是重写引擎，是给引擎加一层会话，把"叙事（UI 文案）"和"物理（出声）"分开。

## 二、现状实测

| 事实 | 数值 | 出处 |
|---|---|---|
| 引擎类 | 888 行，`IDisposable`，零 UI | `AudioPlaybackEngine.cs` |
| **已有**的 `PlaybackSession` | 84 行 | `PlaybackSession.cs` |
| 它的用途 | **精简会话**，给零散/快速窗口用（`SetQueue` / `PlayAtAsync` / `NextAsync` / `ExecuteAsync`） | 同名文件 |
| `_audioEngine` 引用密度 | Playback3 **59**、Features 22、Playback 17、Terminal 13、DspNav 10、WebDsp 9、EqDsp 9、GeekShell 7（≥145 处） | `grep -c` |
| 起播入口 | `StartPlayback` 47 行 | `Playback3.cs:2716` |
| 起播主体 | `PlayExtendedWithEngineAsync` **147 行** | `Playback3.cs:2889–3032` |
| 无缝预载 | `PreloadSeamlessNextAsync` 96 行 | `Playback3.cs:3086` |
| 无缝切换回调 | `OnSeamlessTrackChanged` 37 行 + `OnEnginePlaybackEnded` 16 行 | `Playback3.cs:3223 / 3066` |
| 停止 | `StopEngineIfActive` 12 行 | `Playback4.cs:137` |

⚠ **别把现有 `PlaybackSession` 当正式会话用**：它没有 EQ/DSP/房间校正/ReplayGain/DSD 判断/
无缝预载，只有"放第几首 + 播完推进"。任务是"接管"，不是"改名"。

## 三、状态归属：三类，别混

### A 类：引擎自己的（已经在引擎里，不用搬）
`IsPlaying` / `IsPaused` / `Duration` / `Position` / `CurrentOutputMode` / `IsDsdDirectOut` /
`OutputDeviceName` / `ActualOutputFormat` / `SourceFormatDescription` / 无缝预载队列。
**这些是物理事实，会话只是读它。**

### B 类：会话态（现在是 MainWindow 的散字段，应该进会话）
| 字段 | 现在的作用 | 不进会话会怎样 |
|---|---|---|
| `_queue` / `_currentIndex` | 队列与下标 | 会话不知道"我在队列的第几首" |
| `_nowPlayingPath` | 上一首路径（用于写未播完历史） | 历史写重/写漏 |
| `_startPlaybackInFlight` | 起播在途，期间不写进度 | 进度记到上一首头上 |
| `_usingEnginePlayback` / `_isEnginePaused` | 当前是不是引擎在出声 | 暂停/停止分支判断错 |
| `_pendingStartSeconds/_pendingStartPauseAfter/_pendingStartPath` | 引擎起来后再定位（启动续播/点歌词/拖条） | 每次起播都从头放 |
| `_currentRgData` | 本曲 ReplayGain 数据 | 面板读数与实际不符 |
| `_seamlessPreloaded` / `_lastPreloadTick` / `_lastPreloadPath` | 预载去抖 | 重复预载、切歌卡顿 |

### C 类：界面残留（**留在 MainWindow**，由快照驱动）
`NowPlayingText.Text`（"正在转码/正在播放/DSD DoP 直出/已转码为…"共 6 处）、
`PlayPauseIcon.Glyph`、`ProgressSlider.Maximum/Value`、`TotalTimeText.Text`、
波形计时器 `UpdateWaveformTimerForPlaybackState`、`UpdateSignalChainDisplay`、
`UpdateNowPlayingOutputFormat`、`ShowErrorAsync`。

**判断标准**：这段代码删掉会不会改变出声？不会 → C 类。

## 四、`PlayExtendedWithEngineAsync` 逐段拆解（147 行）

| 段 | 行 | 内容 | 归属 |
|---|---|---|---|
| 1 | 2891–2897 | 让快速播放让位、写上一首历史 | B（会话） |
| 2 | 2899 | `NowPlayingText = "正在转码："` | **C** |
| 3 | 2900–2908 | 建引擎、应用输出模式、设备偏好、挂事件 | A/B |
| 4 | 2911–2948 | EQ 曲线、声道平衡/限幅、房间校正 FIR、ReplayGain | A（配置下发） |
| 5 | 2950–2955 | CUE 区间换算 + `PlayFileWithFfmpegAsync` | **核心：物理起播** |
| 6 | 2956–2985 | 成功分支：DSD 判定文案、面板刷新 | **C**（除 `_isEnginePaused/_usingEnginePlayback`） |
| 7 | 2986–3011 | 时长/进度条/位置事件/待定位/淡入/SMTC/预载 | 混合（3 行是 A，6 行是 C） |
| 8 | 3013–3031 | 失败分支：文案 + 找 ffmpeg 的专用弹窗 | C |

**结论**：真正"必须出声"的只有第 3、4、5 段和第 7 段的时长/事件接线。
其余一半是文案。这就是为什么它长得像怪物 —— 它同时是播放器和播报员。

## 五、接口草案（下一刀照这个开）

```csharp
// 正式会话：一个窗口一份，持有引擎，不持有任何控件。
internal sealed class PlaybackSession : IAsyncDisposable
{
    // 起播：把"能不能放、放的是什么"说清楚，不负责说给用户听
    internal Task<PlaybackStartResult> StartAsync(PlaylistItem item, StartOptions? options = null);
    internal Task StopAsync();
    internal Task PauseAsync();
    internal Task ResumeAsync();
    internal bool TrySeek(double seconds);
    internal Task PreloadNextAsync(PlaylistItem? next);
    internal PlaybackSnapshot Capture();          // 复用现有 PlaybackSnapshot
    internal event Action<PlaybackSnapshot>? Changed;
    internal event Action<PlaybackEndedInfo>? Ended;   // 谁播完了、是不是无缝
}

internal sealed record PlaybackStartResult(
    bool Ok, string? FailureReason, bool DsdDirectOut, string OutputDescription,
    TimeSpan Duration, double TempoScale, bool NeedFfmpegHint);

internal sealed record StartOptions(double StartSeconds = 0, bool PauseAfter = false);
```

**宿主口子只要三条**（其余全是现成的静态服务）：
1. `Action<PlaybackSnapshot>` 通知界面刷新（或直接让 MainWindow 订阅 `Changed`）；
2. `Func<PlaylistItem?>` 取"下一首是谁"（队列/曲库续播规则已经在纯函数里，注入即可）；
3. `void OnSeamlessAdvanced(PlaylistItem from, PlaylistItem to)` —— 无缝切轨时让界面换标题。

**不要**把 `NowPlayingText` 这类控件传进会话。会话返回结果，界面自己决定怎么说。

## 六、三刀计划

| 刀 | 内容 | 为什么这么切 | 验证 |
|---|---|---|---|
| **1** | `PrepareAsync`：把段 3、4（引擎创建、输出模式、EQ/DSP/房间/RG）搬进会话 | 纯配置下发，不动换曲调度，最安全 | 出声 + 日志里 EQ/RG/FIR 应用记录 + `dsp get` 回包 |
| **2** | `StartAsync`：段 5 起播 + 段 7 的时长/事件，返回 `PlaybackStartResult`；MainWindow 只渲染文案 | 把"物理"和"叙事"分开 | 起播探针 + 故意给坏文件看失败文案 |
| **3** | 无缝接续：`PreloadSeamlessNextAsync` / `OnSeamlessTrackChanged` / `OnEnginePlaybackEnded` | 最危险：接续失败是"播完就停"，只有实听能发现 | **必须实听跨曲**，且看日志里 seamless 记录 |

## 七、每一刀都要过的四关

1. 编译：数 `error CS` 行数，**0 才算过**（别信末尾"N 个错误"）。
2. 接口回归：`tools/W13InterfaceRegression.cjs`，0 失败 / 0 JS 异常 / exit 0。
3. 起播证据：`logs/audio/<date>.log` 三行 —— `HiFi播放 … ok=True`、`[journal] 起播记录`、`波形解码完成`。
4. 失败路径：给不存在的文件 / 坏文件，**要弹提示且不崩**，不能静默停止。

## 八、本轮明确不做

- 不改 `AudioPlaybackEngine` 内部（888 行，动了就得重新验 DSD/DoP/SRC）。
- 不动 EQ/DSP 面板读写（`WebDsp` 699 行，直读写原生控件树 —— 见交接文档的 DSP 判定）。
- 不碰迷你播放器 / 零散播放窗口（用户 2026-10-28 拍板，他自己重构）。
- 不在这一刀里删旧界面控件容器（`MainWindow.LegacyControls.cs` 706 处 WinUI 引用）——
  那要等 C 类代码全部改由快照驱动之后。

## 九、回退点

- 每刀一个 commit，改前记下 `git rev-parse HEAD`。
- 起播探针与接口回归的"改动前后"结果都留在 `_w13_*.json`，可回放比对。
- 声卡出问题时先看日志 `HiFi播放 … ok=` 与 `设备=`，再怀疑代码。
