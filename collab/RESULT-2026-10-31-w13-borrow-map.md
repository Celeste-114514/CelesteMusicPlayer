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

### 第十刀（提交 `4f40a83`，2026-10-31）：C 域（两山之外）开工
- **传输三件套** → Playback.cs：`AdjustVolumeBy`/`SeekBySeconds`（Features）、`HandleMediaEnded`（Misc）
  ——网页命令/托盘/热键/MediaPlayer.Ended 回调全是活路径。
- **封面缓存字段补搬**：`_lastCoverBytes`/`_lastCoverImage` 上一轮因 `&&` 链短路实际没搬
  （脚本先报错，后面的字段搬移根本没执行，但编译 RC=0 掩盖了它）——本轮真正搬进 Playback3。
  **教训：`&&` 链上前一步失败会静默跳过后续步骤，看探针残账才能发现。**
**518 → 492 错 / 186 → 181 成员**。四道验证全过。回退件 `pre-w13-transport-20261031`。

### C 域（两山之外 ~170 处）剩余构成
- 歌词视觉：`ScrollLyricToCenter`(6)、极客行染色 —— 操作旧歌词控件，等视觉域统一裁
- 缩略图/电平：`_thumbPumpTimer`(4)、`IdleLevel`(4)、TaskbarPlayer 相关 —— NativeTaskbar 域小刀
- 极客按钮域：`_geekAsciiButtons`/`GeekButtonBackup`/`ApplyGeekRowDetailChrome` —— 极客域小刀
- 托盘动作尾巴：`ToggleFavoriteForCurrent`/`ToggleMainWindowVisibility`/`RestoreLastLibraryAsync`
- 房间校正面板：`LoadRoomCorrectionIntoPanel`(4)/`_roomIrLoaded`(4) —— 归 DSP 域一起
- 设置实时应用族：`ApplySettingsLive` 大 switch —— 独立刀

### 第十一刀（提交 `30e0d42`，2026-10-31）：小件清完
搬（12 件）：IdleLevel/EnumerateRealizedListViewItems/ResolveRowHoverBrush → Utilities；
SyncHeaderColumnsFromState+SetColumnWidthIfChanged、ScheduleFitColumns+_fitColumnsTimer+FitColumnsTimer_Tick、
HideHoverTip、DisableContainerSelectionCheckMark、CreateMultiSelectFrostBrush → Playback；
SetListSelectionMode+AttachRangeMultiSelect → Playback2（多选域）；
ToggleMainWindowVisibility+_geekAsciiButtons/GeekButtonBackup → AppFlow。
删调用点（纯占位视觉，5 处）：DrawWaveformStyle(1)/ScrollLyricToCenter(3)/RetargetStoryboard(1)。
跳过（连锅）：ApplyGeekRowDetailChrome→GeekBrowse、ToggleFavoriteForCurrent→ApplyCategoryView（导航刀）、
RestoreLastLibraryAsync→Library.cs。
**492 → 456 错 / 181 → 170 成员**。四道验证全过。回退件 `pre-w13-smallitems-20261031`。

### ⚠ 第十一刀过程坑：脚本 bytes 字面量含中文 = SyntaxError 静默丢块
python heredoc 里 `b'...中文...'` 直接 SyntaxError——若删除源已执行而插入目标未执行，**代码块就丢了**。
本轮 GeekShell 丢过一次（git checkout 秒回）。修复：搬家脚本一律全程 str + UTF-8，杜绝 bytes 中文。
另：`&&` 链上脚本失败会静默跳过后续（上一轮教训），本轮已两次靠探针残账兜底。

### 第十二刀（提交 `fef47a1`，2026-10-31）：导航域开局——导航历史族离壳
新建 `MainWindow.NavHistory.cs`：CaptureLibraryNavState（Library.cs）、
LibraryNavStatesEqual + CommitLibraryNavigation（EqDsp.cs）。状态类/栈/快照字段都在 xaml.cs（活），
三方法是纯栈操作，零行为变化。NavigateLibraryHistoryBack/Forward 留壳（调用者全在壳内）。
**456 → 454 错 / 170 → 168 成员**。四道验证全过。回退件 `pre-w13-navhistory-20261031`。

### 导航域剩余（下一大仗）
- **ApplyCategoryView（290 行混合体）**：case "Songs" 强制重置排序+ApplySort（活，影响网页曲库顺序）、
  case "UserPlaylist" RenumberCollection、RefreshAlbumViewAsync（活数据刷新）+ 大量占位控件操作（死）。
  打法：整族（含 ApplySort/SetSongSortUiForCategory/CloseXxxDetailUi 等伴生）评估后决定
  「整族搬 NavFlow 文件」或「逐 case 等价重写」。**需要网页曲库顺序的实测验证配合。**
- LoadAndAddFiles(6)、RefreshAlbumViewAsync/RefreshFolderViewAsync 等数据刷新族。

### 第十三刀（2026-10-31）：导航域核心战役第一阶段——ApplyCategoryView 借用清零
全分支甄别结论：290 行里九成是给旧占位控件摆面板（死）；活行为只有两处——
case "Songs" 强制重置标题升序+ApplySort（_playlist 顺序=网页曲库顺序）、
case "UserPlaylist" RenumberCollection 重写队列序号。
整体搬家会倒贴账（-16 借用换 ~+19 新借用，19 个壳伴生被拖进活路径），故选**调用点改道**：
新活方法 `ApplyCategoryViewData()`（NavHistory.cs，只含两个活分支），13 个活调用点全部改道
（启动 xaml.cs、网页清空队列、WebDav 未配置回退、托盘收藏刷新等）。
**454 → 428 错 / 168 → 167 成员，ApplyCategoryView 从借用榜除名（残余 0）**。
四道验证全过；出货已同步；回退件 `pre-w13-navdata-20261031`。
⚠ 待用户实测：网页曲库（歌曲页）顺序应与改前一致（标题升序）。
明细见 `RESULT-2026-10-31-w13-navdata-cut.md`。

### 导航域剩余（第二阶段）
- **LoadAndAddFiles（6 处）**：文件加载入口，尾部与 ApplySort/RenumberCollection 咬合；活路径已不调 ApplyCategoryView，搬家阻力减小。
- 列表视觉族逐个甄别：ScheduleFitColumns(6)/RefreshFolderBrowserSelectionChrome(6)/SetListSelectionMode(6)。
- DSP 域（~150 处）成为账面最大块：ApplyFirParameters(8) 榜首。

### 第十四刀（2026-10-31）：导航域收尾——LoadAndAddFiles 整体离壳
壳里旧注记「搬走会引入壳依赖」已过时：逐个核对，依赖（GetPlayer/CreatePlaylistItemFromPath/
ClearNowPlayingPanel/StopEngineIfActive/RenumberCollection/ApplySort 及三个顶层工具类）现全在活文件。
133 行**原样搬 MainWindow.Playback.cs**，尾部 4 处 ApplyCategoryView 按第十三刀口径改调活版
ApplyCategoryViewData，壳内调用点自动跟随。**428 → 422 错 / 167 → 166 成员，残余 0**。
四道验证全过（CDP 首连两次瞬断，重试全绿，疑似发布后首启扫库超时窗口，非代码问题）。
出货已同步；回退件 `pre-w13-loadfiles-20261031`。明细见 `RESULT-2026-10-31-w13-loadfiles-cut.md`。
导航域三大件（ApplyCategoryView/CommitLibraryNavigation/LoadAndAddFiles）全部下榜；
导航域剩余只剩列表视觉族（ScheduleFitColumns/RefreshFolderBrowserSelectionChrome/SetListSelectionMode 各 6）。
下一仗：DSP 域（~150 处，榜首 ApplyFirParameters 8）。

### DSP 域 1/N（2026-10-31 晚，提交 `11750a6`，⚠ 刀号与并行会话的十三/十四刀撞号，以下按时间线称"DSP 批 1"）
> 并行会话（Absolomb）在此期间完成了第十三/十四刀（ApplyCategoryView 改道 ApplyCategoryViewData、
> LoadAndAddFiles 离壳，454→422）。本批在它们之上进行，文件无重叠。

- **状态字段组**：`_audioFxPanelReady`/`_dspRackReady` → DspStateHost；`_roomIrUiReady/_roomIrLoaded/_roomIrLoadedRate` → WebDspTools；`_eqSimpleBass/Vocal/Air/Warm` → WebDsp。
- **模块启用判定族**（从 git HEAD 原文整批）：`DspModuleActive`/`UpdateDspNavIndicators`/`_opraApplied` 属性/12 个 `DspActive*` 下标常量（DspNav）、`DspComp/Field/MatrixActive`（DspRack）→ DspStateHost（模型与状态容器本就在该文件）。
**422 → 378 错 / 166 → 156 成员**。四道验证全过。回退件 `pre-w13-dspbatch1-20261031`。

### ⚠ DSP 批 1 过程坑（两个新形态，均已修复）
1. **分号结尾的字段声明被花括号配平误切**：`private bool _roomIrUiReady;` 后面跟着方法，配平一路吃到
   方法闭合，把 `LoadRoomCorrectionIntoPanel`、`LoadDspRackUi` 等整段误切走（EqDsp -77/DspRack -64）。
   修复：RoomIr 从 git 恢复；误切段从 DspStateHost 按归属搬回。**教训：分号结尾的声明绝不做配平。**
2. **从 HEAD 提取插入时忘了删源文件原文**：DspNav 的判定族在 DspStateHost 与 DspNav 双份 → CS0111。
   修复：从 DspNav 补删。**教训：HEAD 提取式搬移的"删源"步骤必须显式执行。**
另：本机 python/dotnet 各出现一次 Segmentation fault（沙箱偶发），重跑即过。

### DSP 域剩余（~120 处）
EQ 撤销/快照族（PushEqUndoSnapshot/SetAudioFxEqFromSnapshot/Undo/Redo/ToggleAb/AutoGain ~20）、
参数下发族（ApplySrc/Comp/Field/MatrixParameters + ApplyDspToEngine ~10）、
ApplyFirParameters+RefreshFirReadouts（~10）、面板刷新族（SyncDspPowerSwitches/RefreshDspProfileUi/
EnsureAudioFxUiBuilt/LoadAudioFxUiFromStore ~15）、Profile/机架管理（~15）、
ApplySimpleTones+SyncAudioFxEqSimpleFromState（~4）、ApplyOpraCurve/NoteOpraApplied（~4）、
ApplyDeviceDspProfileIfEnabled（2）。
