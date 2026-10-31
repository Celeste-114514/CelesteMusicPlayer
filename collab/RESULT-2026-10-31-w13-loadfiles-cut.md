# W13 第十四刀：LoadAndAddFiles 整体离壳，导航域收尾（2026-10-31）

提交：代码 `见 git log`。接第十三刀（ApplyCategoryView 调用点改道，`a63a6e4`）。

## 1. 判定与处理

`LoadAndAddFiles`（壳 Misc.cs，原 133 行，文件加载入口）：去重 + CUE 分轨展开 + 进 `_playlist` +
按当前分类收尾（Songs 重排 / UserPlaylist 只重编号 / 其它 ApplySort 保播放下标）。

壳里那条旧注记（「搬走会给工具文件引入壳依赖，已退回」）**已过时**：逐个核对它的全部依赖——

| 依赖 | 现居 |
|---|---|
| `GetPlayer` / `CreatePlaylistItemFromPath` | Playback.cs（活） |
| `ClearNowPlayingPanel` | Playback3.cs（活） |
| `StopEngineIfActive` | Playback4.cs（活） |
| `RenumberCollection` / `ApplySort` | Playback2.cs（活） |
| `_playlist` / `_currentIndex` / `PlaylistView` / `NowPlayingText` | xaml.cs / 控件（活） |
| `CueTrackExpansion` / `LibraryPathGuard` / `LibrarySessionStore` | 顶层类，各自独立文件 |

**全部在活文件 → 整体原样搬 `MainWindow.Playback.cs`（CreatePlaylistItemFromPath 旁边），零新借用。**
唯一口径调整：尾部 4 处 `ApplyCategoryView()` 按第十三刀既定口径改调活版 `ApplyCategoryViewData()`
（Songs 重排/队列重编号保留；Albums/Artists 分支原本只剩死行为）。壳内三个调用点
（打开文件 picker、文件夹导入 ×2）自动跟随，无需改。

## 2. 量账与验证

**428 → 422 错 / 167 → 166 成员，`LoadAndAddFiles` 从借用榜除名（残余 0）。**

| 项 | 结果 |
|---|---|
| 全量重建（Debug x64） | 0 错误 |
| publish DLL | 3,019,264 字节，PDB 775,688 齐全 |
| 接口回归 | cold=4041 reload=4041，0 失败，0 JS 异常，正常退出（首次 CDP 瞬断，重试全绿） |
| 实播两首 | Epilogue → Prologue，exitCode 0（CDP 首连瞬断，重试全绿） |

⚠ 本轮 CDP 首连两次瞬断（回归与实播各一次），重试均过、结果干净；疑似发布后首启扫库超时窗口，
非代码问题。若后续频繁出现，考虑把探针的 CDP 等待窗口从 60s 放宽。

## 3. 交付

- 出货目录已同步：`dist/web-ui-20261028/`（COPIED=3，BAD=0）；重启即生效。
- 回退件：`dist/web-ui-20261028-pre-w13-loadfiles-20261031/`（= 第十三刀出货版）。

## 4. 导航域战报（第二、三、四刀以来）

- 借用榜开账时导航域 ~154 处 → 现存：`ScheduleFitColumns`(6)、`RefreshFolderBrowserSelectionChrome`(6)、
  `SetListSelectionMode`(6) 等列表视觉族（多为列宽/悬停/选择模式，逐个甄别即可）。
- `ApplyCategoryView`、`CommitLibraryNavigation`、`LoadAndAddFiles` 三个大件全部下榜。

## 5. 下一仗

- **DSP 域**（账面最大块，~150 处）：榜首 `ApplyFirParameters`(8)，其后 `PushEqUndoSnapshot`/
  `SetAudioFxEqFromSnapshot`(各 6)、`DspModuleActive*` 家族(~30)、`_opraApplied`(6) 等。
  按 `Dsp*Parameters` 模型既定路线逐模块搬（方案 A 已收官路线）。
- 列表视觉族（ScheduleFitColumns/RefreshFolderBrowserSelectionChrome/SetListSelectionMode，各 6）
  可作 DSP 之间的调剂小刀。
