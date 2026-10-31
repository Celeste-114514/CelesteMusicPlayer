# W13 第五刀：多选族缩到"实际会跑的部分"（2026-10-31）

提交：`c3d7b1d`。接 `RESULT-2026-10-31-w13-wavefield-cut.md`（第四刀）。

## 1. 核心证据：多选模式在主窗口从未开启过

`_isMultiSelectMode` 写 true 的入口**全部不可达**：

| 入口 | 位置 | 为什么不可达 |
|---|---|---|
| `EnterFolderMultiSelectMode` | Library.cs:1833 | 只被壳内 `multiItem.Click +=` 挂接（文件夹右键菜单，菜单本身挂在占位控件上，从不显示） |
| `EnterAlbumWallMultiSelectMode` | Library2.cs:1011 | 同上（专辑墙右键菜单） |
| `EnterMultiSelectMode` | Playback2.cs:1434 | 只被"多选"`MenuFlyoutItem` 挂接（`BuildPlaylistItemContextMenu` 构建的 ContextMenu，同上） |

所以 `_isMultiSelectMode` 恒 false，由此：

- **`ExitMultiSelectMode`（18 处调用）每次执行都走"未开启"早退分支**——隐藏多选工具条 + 标题后返回。
  原方法后半段（`ExitSong/Album/FolderMultiSelectUiOnly` → `RefreshAllSongListSelectionChrome` → … 一整串
  刷新链）**从未运行过**。
- **`UpdateSelectAllMultiSelectButtonState`（8 处调用）第一行守卫就 return**，后面 30 行全选图标刷新从未跑过。

## 2. 处理方式："不可达即不搬"

活文件里保留**等价行为**，不可达部分留在壳文件里随壳一起死：

- `ExitMultiSelectMode` → Playback2.cs，只含早退分支原样（3 个占位控件 Visible 操作），附完整证据注释
- `UpdateSelectAllMultiSelectButtonState` → Playback2.cs，空方法体（守卫恒真返回）
- 纯静态函数原样搬：`ApplyForegroundToDescendants` / `ClearForegroundOnDescendants`（UiTheme → Playback3）、
  `ResetRowRunColors` 两个重载（UiTheme → Playback3，唯一用户是 `SyncLyricsToPosition`）、
  `CreateBitmapFromBytesAsync`（Misc → Utilities，与 `ExtractCoverBytes` 同属封面域）

18 处 `ExitMultiSelectMode()` 调用点**一行没动**——它们的行为和之前逐字节等价。

## 3. 量账

**698 → 636 错 / 223 → 217 成员**。多选族、前景色族、歌词行颜色、封面解码全部下榜。
榜首回归导航域：`ApplyCategoryView`(16→8 去重后)、`CommitLibraryNavigation`(10)、`SetNowPlayingPaneVisible`(8)、
`UpdateLibraryNavHighlight`(8)、`ApplyFirParameters`(8)。DLL 3,019,264 字节。

## 4. 验证

| 项 | 结果 |
|---|---|
| 全量重建 | 0 错误 |
| publish DLL | 3,019,264，符号齐 |
| 接口回归（publish） | cold=4041 reload=4041，0 失败，0 JS 异常，正常退出 |
| 实播两首 | Epilogue → Prologue，切歌正常，正常退出 |

## 5. 交付

- 出货目录已同步（DLL 3,019,264）；回退件 `dist/web-ui-20261028-pre-w13-deadcalls-20261031/`（第四刀版本）。
- 本轮差点踩一个新坑：`Playback3.cs` / `Playback2.cs` 一个 LF 一个 CRLF，搬家脚本已按目标文件风格适配；
  file-scoped namespace 的插入点问题已在第四刀文档记录。

## 6. 下一刀

- **`ApplyCategoryView` / `CommitLibraryNavigation`（导航域）**：同样手法——先追 `_currentCategory` 的读写分布
  （它在 xaml.cs:487，活文件）。注意 `ApplyCategoryView` 的 `case "Songs"` 分支里有 `ApplySort(playingPath)`（真排序），
  必须先证明那些调用路径在网页世界里是否该触发排序，不能一刀切。
- `UpdateLibraryNavHighlight`（8 处，含 MainWindow 构造函数 2 处）+ `SetNowPlayingPaneVisible`（8 处）：同法甄别。
- `WebDsp.cs` 138 处仍是最大单一借用方。
