# W13 借用地图：壳退出编译前剩下的 614 处都是什么（2026-10-31）

口径：`python tools/_w13_shell_exclude_probe.py` 的 614 错 / 214 成员，按功能域归类。
每个数字 = 活代码里「引用了壳成员」的编译错误数；**它不等于工作量** —— 一个方法搬家就能消掉一片，
一个真死代码删调用点也能消一片，但 DSP 那种状态机要按模块逐个搬。

## ① DSP 音效域 —— 156 处（最大块）

网页 DSP 页（`WebDsp.cs` 138 + `WebDspTools.cs` 20 是主力借用方）要读写的状态机还住在壳里：

| 成员 | 借用 | 说明 |
|---|---|---|
| `ApplyFirParameters` | 8 | FIR 落盘+下发主流程（牵 `RefreshRoomCorrectionInfo`/`SyncDspPowerSwitches` 两个面板刷新） |
| `DspModuleActive*` 家族 | ~30 | 十个模块各自的"启用中"判定（Eq/Rg/Src/Comp/Channel/Field/Matrix/Fir/Headroom/Safety/Rack/Profiles） |
| `PushEqUndoSnapshot` / `SetAudioFxEqFromSnapshot` | 12 | EQ 撤销栈 + 快照回放 |
| `SyncDspPowerSwitches` / `RefreshDspProfileUi` / `UpdateDspNavIndicators` 等 | ~15 | DSP 面板刷新（操作旧控件，但被网页命令路径调用） |
| `_audioFxPanelReady` / `_opraApplied` / `_roomIrLoaded` 等 | ~15 | 状态字段 |
| Apply*Parameters 下发族 | ~30 | Comp/Matrix/Field/Src/Rg 参数下发 |

打法：按 `Dsp*Parameters` 模型既定路线（方案 A 已收官），把"启用判定 + 下发 + 面板刷新"逐模块搬进
各自的活文件。**不碰 DSP 引擎本身**（`celeste_dsp_core.dll` 与 `ApplyDspToEngine` 是活的且已在新文件）。

## ② 曲库/导航域 —— 154 处（第二块，含行为风险）

| 成员 | 借用 | 风险 |
|---|---|---|
| `ApplyCategoryView` | 16 | ★ 案例里 case "Songs" 有 `ApplySort`（真排序）。**已证实网页曲库顺序 = `_playlist`**（`LibraryTracks => _playlist`），删错会改变用户看到的歌曲顺序 |
| `CommitLibraryNavigation` | 10 | 导航后退栈族（`_navBackStack`/`CaptureLibraryNavState`），跨 EqDsp/Library/xaml.cs 三个文件 |
| `LoadAndAddFiles` | 6 | 文件加载入口，尾部按分类调 ApplyCategoryView/ApplySort/RenumberCollection，与上两者咬合 |
| `ScheduleFitColumns` / `HideHoverTip` / `SetListSelectionMode` / 各种 `*Chrome` | ~60 | 列表视觉（列宽自适应、悬停提示、选择模式、行外观）——大概率零状态可裁，需逐个过 |
| `RefreshFolderBrowserSelectionChrome` 等 | ~20 | 文件夹浏览的旧树控件刷新 |

打法：先精读 `ApplyCategoryView` 全分支，把"动 `_playlist`/`_queue` 顺序"的活行为与"刷旧控件"的死行为
分离；导航栈族整体搬到活文件；纯视觉的逐个判死。

## ③ 还活着的杂项功能 —— 134 处（第三块，都是真功能）

托盘（`RestoreFromTray`/`ExitFromTray`）、桌面歌词（`DesktopLyricsEnabledPublic`）、
定时停止（`SleepStopMode`/`_sleepMinutes`）、歌词取数（`GetLyricLinesPublic`/`ScrollLyricToCenter`——
**网页歌词在用**）、封面缓存（`_lastCoverImage`/`InvalidateCoverCache`）、收藏状态（`UpdateFavoriteButtonUi`
——含任务栏缩略图同步）、极客风格判断缓存（`GeekUiStyleCached` 10 处）、全局热键、设置应用
（`ApplySettingsLive`/`ApplySrcSettings`/`ChangeOutputDeviceAsync`）。

打法：这些是**真功能住错了地方**，逐个搬家（像第三、四刀那样）。量在 134 但每件都独立、可自动验证。

## ④ 纯样式/颜色 —— 20 处

强调色解析的尾巴（`ResolveAccentColor(Static)`/`ResolveAccentForegroundBrush`/`ResolveCapsuleFillBrush`/
`ResolveUiBaseTintColor`）+ `IsOpaqueUiStyleActive`。全是纯函数，逐个搬 Utilities 即可。

## ⑤ 其它 —— 150 处（需要逐个过一遍才能定性）

`IsDsdFile`（纯工具，2 处）、`ExitApplication`、`Instance`（自身引用）、`_thumbPumpTimer`（缩略图泵）、
`IdleLevel`（电平表）、`SyncHeaderColumnsFromState`、`RetargetStoryboard`、各种 `Apply*FromSettings`。
其中可确认的纯工具（如 `IsDsdFile`）直接搬；其余按①③的方法归队。

## 建议的刀序

1. **④ 纯样式尾巴 + ⑤ 里确认的纯工具**（半天量级，~30 处，零风险）
2. **③ 杂项真功能逐件搬**（134 处，每件独立可验证；托盘/桌面歌词/定时优先——用户天天用）
3. **② 导航域专案**（154 处，`ApplyCategoryView` 精细拆是核心战役）
4. **① DSP 域**（156 处，按模块分五六个小刀；做完它，`WebDsp.cs` 就能退出借用名单）
5. 全清后：SHELL 清单 35 个文件整批 `<Compile Remove>`，旧壳正式退出编译

每刀照旧四道验证：Rebuild 0 错误 → publish 验体积 → 接口回归 → 实播。

---
## 执行进度

### 第八刀（提交 `c3d09e0`，2026-10-31）：第 1 步纯函数清完
搬 Utilities（逐行原样）：`IsDsdFile`/`DsdExtOf`/`ResolveCapsuleFillBrush`（Misc）；
`ResolveAccentColor(Static)`/`ResolveAccentForegroundBrush`/`ResolveUiBaseTintColor`/
`IsClassicUiStyleActive`/`IsOpaqueUiStyleActive`/`Instance` 属性（UiTheme）。
`Instance` 是全项目共用的核心静态属性，声明搬家零行为变化。
**614 → 588 错 / 214 → 205 成员**。DLL 3,018,240。四道验证全过。
回退件 `dist/web-ui-20261028-pre-w13-accent-20261031/`。
跳过未动：`RetargetStoryboard`（操作旧界面元素树的名字解析，属视觉域）、`DrawWaveformStyle`（旧 Canvas 绘制）。

### 第九刀（提交 `e2c238d`，2026-10-31）：第 2 步杂项真功能整批搬完
新建两个 partial 文件：
- `MainWindow.LyricsBridge.cs`：歌词数据桥三件套（网页歌词/迷你播放器/托盘在用）+ 桌面歌词开关族
  （Set/Toggle/Badge/Ensure/ClosedByUser —— WebView2 独立窗口是活功能）
- `MainWindow.AppFlow.cs`：定时停止族（枚举+字段+ApplySleepSelection+StopForSleep）、
  托盘显隐（RestoreFromTray/ExitFromTray）、退出流程（ExitApplication+_exitStarted）、
  收藏状态（UpdateFavoriteButtonUi+SetGeekFavoriteState）、全局热键重启（+_hotkeys 字段）
Utilities：GeekUiStyleCached 缓存、GeekLyricAccentColor/PhosphorShade、极客播放页状态字段。
Playback3：封面缓存字段 + InvalidateCoverCache。
**588 → 518 错 / 205 → 186 成员**。DLL 3,018,752。四道验证全过。
回退件 `dist/web-ui-20261028-pre-w13-miscfeatures-20261031/`。
未做（连锅太深）：设置实时应用族（ApplySettingsLive 是大 switch 分发）—— 留独立刀。

### ⚠ 第九刀过程坑：配平切块把 UiTheme 切爆过一次
cut_block 用 `^\s*\}` 当结束标志会把**嵌套块的闭合**当方法结束 —— RefreshGeekUiStyleCache 是
expression-bodied（无花括号），脚本一路匹配到后面几百行的某个 `}`，把 `_lastAppliedUiStyleMode` +
`ApplyUiStyleMode` 前半误切进 Utilities，UiTheme 剩下残缺方法体 → 226 个编译错误。
修复：从 git HEAD 提取 389-431 行原文插回 UiTheme（ApplyUiStyleMode 与现存后半段自然拼合），
Utilities 只保留真正该搬的 GeekUiStyleCached 字段+属性。**教训：expression-bodied 成员绝不能用
花括号配平的通用切块；end_pat 必须锚定具体内容。**
