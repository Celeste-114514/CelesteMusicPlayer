# W13 旧界面功能壳：收容盘点（2026-10-30 晚）

> 这是**盘点**，不是动手方案。目的是把"旧界面功能壳"从一句口号变成一张有数字的地图：
> 谁是壳、谁是活、活代码从壳里借了什么、收容按什么顺序走、哪些必须等用户自己重构。

## 一、一句话结论

**壳不是 12,600 行，是 33,455 行（46 个文件）；它还不能被排除，因为三层活引用拴着它：
网页桥/播放域（要救）、独立 WinUI 窗口（等用户自己重构）、控件字段容器（最后收）。**

## 二、数字总账（文件级，2026-10-30 实测）

MainWindow partial 总计 **47,366 行 / 79 个文件**，分三类：

### LIVE（保留域）≈ 14,050 行 / 45 文件
| 组 | 文件 | 行数 |
|---|---|---|
| 播放域 | Playback / 2 / 3 / 4 / Host / State / Lyrics / DsdPreload / RgScan | ≈ 10,400 |
| 网页桥 | WebShell / WebBrowse / WebBrowseActions / WebFolders / WebSources / WebTags / WebTagTools / WebDsp / WebDspTools / WebTransport / WebOpra / WebLibraryBrowseHost / WebRecoveryHost / WebSettings / WebAppearance / WebHomeHost | ≈ 2,900 |
| 宿主接线 | SettingsHost / JournalHost / MediaSessionHost / NowPlayingHost / OutputModeHost / HotkeysHost / AudioChainHost / UpdateHost / WebLibraryHost / PlaybackCommands / LegacyQueue / ContextMenu / ExternalFile / MiniPlayer / Scattered / WindowCoordination / MainWindowCoordinator / TaskbarPlayer / NativeTaskbar | ≈ 1,400 |

### SHELL（旧界面页）= 33,455 行 / 46 文件 —— 本次盘点对象
大头：Features 3,864、Misc 3,322、UiTheme 3,148、Library 2,398、EqDsp 2,291、Library2 1,556、
GeekShell 917、DspNav 896、NowPlayingLayout 848、DspRack 843、Terminal 761……
外加 MainWindow.xaml.cs 946（`FolderBrowserItem` 等模型住在里面）和
**LegacyControls 720（463 个控件字段注册表）**。

## 三、成员级账（五大壳文件，逐个声明扫过）

| 文件 | 声明 | 被活代码引用 | 只在壳内互调（陪葬） | 全无人引用 |
|---|---|---|---|---|
| Features | ~179 | 47 | 132 | 0 |
| Misc | ~104 | 50 | 54 | 0 |
| UiTheme | ~115 | 50 | 65 | 0 |
| EqDsp | ~127 | 52 | 75 | 0 |
| LegacyControls | ~706 | 463（全是控件字段） | — | 0 |

**陪葬共 569、死代码 0。** 整个壳是一张互相咬合的网，没有"直接删掉也不影响"的独立块——
这印证了任务书的判断：直接排除界面文件会更糟（1450→1980 错误），必须先拔线。

## 四、关键发现：活引用分三层，处置完全不同

### 第 1 层：网页桥 / 播放域（**要救**）
真业务被壳攥着，典型（按引用数）：
- **播放域找壳借工具**：`FormatTime`(25)、`ShowErrorAsync`(21)、`ApplyCategoryView`(20)、
  `ExitMultiSelectMode`(18)、`IsHiFiModeSelected`(13)、`CreateBitmapFromBytesAsync`(12)、
  `PickerHostHandle`(12)、`IsDsdFile`(11)、`ContainsIgnoreCase`(10)、`LoadAndAddFiles`(10)、
  `LoadWaveformForCurrentAsync`(5)、`PlayCueFileAsync`、`SeekBySeconds`、`FadeInEngineAsync`
- **WebTransport 找 Features 借睡眠定时/AB 循环/输出设备**：`_abLoop*`、`SleepStopMode`、
  `_sleep*`、`ChangeOutputDeviceAsync`、`ChangeOutputModeAsync`、`SetAbLoopStart/End`、`ToggleAbLoop`
- **WebDsp 找 EqDsp 借 DSP 状态机**：`_audioFxEq`(18)、`_audioFxPanelReady`(10)、
  `ApplyDspToEngine`(11)、`PushEqUndoSnapshot`/`SetAudioFxEqFromSnapshot`/`Undo/Redo/AB`、
  `ApproxBandGain`(5，连 `ManagedDspSourceProvider` 都在用)、`EqAxisTickFreqs`
- **独立域找 Misc/Features 借杂项**：`OnTagsSaved`(4)、`UpdateFavoriteButtonUi`(4)、
  `RestartGlobalHotkeysFromSettings`、`RestartLibraryWatchFromSettings`、`EngineDisplayName`、
  `EvaluateBitPerfectChain`、`IsBitPerfectPure`、`MinimizeToTray`(11)、`RestoreFromTray`、
  `RefreshWebDavNavEntry` 邻接的 `SetNavEntryVisibility`、`MaybeAutoDownloadExtrasAsync`

### 第 2 层：独立 WinUI 窗口（**等用户重构，不动**）
设置窗口（SettingsPanelControl/SettingsWindow）、迷你播放器、零散播放窗、
CurrentPlaylistWindow 还在引用壳方法（`Instance`、`ApplyCustomBackground`、`RestartApp`、
`RefreshBackgroundMotion`、`PickerHostHandle`、多选工具、`PlaybackRateSlider_ValueChanged`…）。
**用户 2026-10-28 拍板这些窗口他自己重构** —— 壳文件被这一层拴住，不能整批排除。

### 第 3 层：控件字段容器（**最后收**）
`LegacyControls` 的 463 个控件字段被播放域直接写（`NowPlayingText.Text = …`、
`ProgressSlider.Value = …`，共数百处）。这是任务书里的 C 类"界面残留"：
等网页成为唯一界面后，这些写点改成快照驱动，容器才能死。

## 五、收容策略（四步，每步独立可退）

| 步 | 内容 | 验证 |
|---|---|---|
| **① 冻结** | 壳文件宣布封存：不再新增任何业务逻辑；新功能一律进服务。零代码改动，一条注释 + 文档 | 无 |
| **② 救真活** | 把第 1 层清单里的业务成员搬出壳：睡眠定时/AB 循环 → `PlaybackSession` 或独立小服务；DSP 状态机（EQ 曲线/undo/AB/预放）→ `DspStateService`；杂项工具（FormatTime/IsDsdFile/ContainsIgnoreCase）→ 已有纯函数类。**按引用数从高到低切，每刀一个 commit** | 编译 0 错 + dsp get 回包比对 + 接口回归 + 起播 |
| **③ 快照化** | 播放域里数百处 `控件.属性 = …` 改为写 `PlaybackSnapshot`（容器字段暂时还在，只是不再被业务直接写——由快照泵统一刷）。这是最大的一块，分文件推进 | 快照回包比对 + 界面肉眼 |
| **④ 整批排除** | 等第 2 层（独立窗口）被用户重构完，`LegacyControls`/`LegacyBusinessState` 收口，46 个壳文件整批 `Compile Remove`，模型（FolderBrowserItem 等）先迁出 | 干净机验收 |

## 六、本轮明确不做

- 不动独立 WinUI 窗口（用户拍板自己重构）——所以**第④步的时间表由用户决定**，代码侧只做①②③。
- 不删任何壳文件（0 死代码，全是网状互调；删=编译爆炸）。
- 不在①②里重写逻辑——**原样搬家**，行为逐行一致（沿用今天的验证套路）。

## 七、下一步（如果要动手）

第②步的第一刀建议切 **EqDsp 的 DSP 状态机**：`_audioFxEq` / `_audioFxPanelReady` / `_audioFxLoading` /
undo/redo/AB 快照 + `ApplyDspToEngine` —— 引用方是 `WebDsp`(9)/`DspProfiles`/`DeviceDspProfileStore`，
全是真活，且 `dsp get` 回包厚、能比得很硬。预计搬出约 300-400 行，壳瘦身的同时 WebDsp 的依赖变干净。
