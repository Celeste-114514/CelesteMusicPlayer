# W13 第四刀（上半）：波形装载与活状态离开旧壳（2026-10-31）

提交：`85ca192`。接 `RESULT-2026-10-31-w13-utils-cut.md`（第三刀）。

## 1. 这半刀做了什么

对借用榜逐个**判死活**（新方法：把每个调用点向上找所在方法名，看它是旧 XAML 事件链还是网页命令/播放主流程），
先搬**确认活的**部分 —— 只搬家，不删任何东西，行为零变化：

| 成员 | 原 → 新 | 判活依据 |
|---|---|---|
| `LoadWaveformForCurrentAsync` | Misc → Playback3 | `StartPlayback` / `OnSeamlessAdvanced`（起播、无缝切歌主流程）在调；波形字段组本来就住 Playback.cs（活文件） |
| `_audioFxLoading` | EqDsp → DspStateHost | `ProjectExtraParameters`（网页 DSP 状态投影）读写 |
| `_dspPowerSyncing` | DspPower → DspStateHost | 同上 |
| `_opra`（OpraService 实例） | Opra → WebOpra | 唯一使用者是网页命令 `WebDspOpraAsync` |
| `_lastCoverThumbImage` | UiTheme → Playback3 | 封面刷新路径读写 |

`ApplyFirParameters`（8 处借用，活）**有意没搬**：它牵着 `RefreshRoomCorrectionInfo` / `SyncDspPowerSwitches`
两个壳面板刷新方法，搬它要连锅端，留给 FIR/DSP 那一刀一起做。

## 2. 死活甄别的中间结论（下一刀的原料）

调用点所在方法名挖出来后的分组：

- **疑似死链**（调用点全是旧界面 Click / 导航刷新，待查状态读写后删调用点）：
  `ExitMultiSelectMode`(18)、`ApplyCategoryView`(16)、`CommitLibraryNavigation`(10)、
  `UpdateSelectAllMultiSelectButtonState`(8)、`ApplyForegroundToDescendants`/`ClearForegroundOnDescendants`(8+8)、
  `ResetRowRunColors`(10，全在 `SyncLyricsToPosition` —— 该方法一半推网页一半刷死 TextBlock，要拆)、
  `SetNowPlayingPaneVisible`(8)、`UpdateLibraryNavHighlight`(8，含 MainWindow 构造函数 2 处)、
  `CreateBitmapFromBytesAsync`(10)
- **判断要领**：多选/分类导航这些状态（`_isMultiSelectMode`、`_currentCategory`）如果只被壳文件读写，
  活方法里的调用就是"纯无操作"，可安全删；`ApplyCategoryView` 里的 `ApplySort()` 是真排序，删调用点前必须确认
  「那条路径本来就不该触发排序」。

## 3. 搬家工具的两个新坑（都修在 `tools/_w13_move_method.py` 里了）

1. **先删源、后写目标 = 内容丢失**：目标文件尾部结构检查失败时，源文件已经被删了方法。
   第一次跑波形搬移就把 `LoadWaveformForCurrentAsync` 弄丢了（靠 `git diff --stat` 确认差异只有这一段后
   `git checkout --` 找回）。已改为**先写目标、成功后才动源**。
2. **file-scoped namespace**：`DspStateHost.cs` / `WebOpra.cs` 是 `namespace X;` 风格，
   class 闭合是 0 缩进的裸 `}`，**没有** 4 空格的 class 花括号（方法闭合才是 4 空格）——
   按"4 空格 }"定位会把字段插进方法体（CS1513）。手工修正过一次；以后对这类文件插入点 = 最后一个裸 `}` 之前。

## 4. 验证

| 项 | 结果 |
|---|---|
| 全量重建 | 0 错误 |
| publish DLL | 3,020,288，`GenericHelpers=1` / `VtableClasses=5` |
| 接口回归（publish） | cold=4041 reload=4041，0 失败，0 JS 异常，正常退出 |
| 实播两首 | Epilogue → Prologue，切歌正常，正常退出 |
| 启动 | 进程存活、CDP 正常 |

## 5. 交付

- 出货目录已同步（DLL 3,020,288）；回退件 `dist/web-ui-20261028-pre-w13-wavefield-20261031/`（第三刀版本）。
  本轮起回退件纪律改为**同步出货前先拷回退件**。
- 量账：**698 错 / 223 成员**（第三刀后 744/228）。DspStateHost 26→12、WebOpra 20→8、Playback 122→106。

## 6. 下一刀

- **删死链调用点**（第 2 节清单），从 `ExitMultiSelectMode` 开始：先追 `_isMultiSelectMode` 的读写分布，
  确认只有壳在用再动手。
- `ApplyFirParameters` 链（含两个面板刷新）随 FIR/DSP 刀处理。
- `WebDsp.cs` 138 处仍是单一最大借用方（DSP 状态机未清）。
