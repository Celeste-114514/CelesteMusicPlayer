# W13 第六刀：导航高亮与播放页显隐调用离开活路径（2026-10-31）

提交：`f8ba8d3`。接 `RESULT-2026-10-31-w13-deadcalls-cut.md`（第五刀）。

## 1. 判定与处理

两个方法的**全部效果面在网页世界里不可观察**，处理方式 = 删除活文件里的调用点（方法本体留在壳里随壳死）：

- **`UpdateLibraryNavHighlight`**（8 处借用）：给旧导航按钮（`NavItems`，占位控件）刷强调色高亮，
  并维护 `_lastAnimatedNavTag` —— 这个标记**只有它自己读**。活文件 4 处调用删除
  （`MainWindow()` 构造 ×2、`InitializePlayerAndTimers`、`RefreshWebDavNavEntry`），另 6 处在壳内部不用管。
  没有连 `NavItemRef`/`BuildNavItems` 那组（约 250 行）搬出来 —— 调用点删除比搬迁更省、更稳。
- **`SetNowPlayingPaneVisible`**（8 处借用）：161 行全是占位面板动画 + 写 `_nowPlayingPaneOpen`
  （该标志**只有壳文件读写**）。活文件 4 处调用删除：`ExternalFile.cs` 两处（⚠ 它们后面的 `return;`
  保留，只删调用行，否则外部文件重复播放检测的流程会改变）、`Playback3.cs` 两处（艺术家/专辑跳转
  的不可达 Click 里）。

## 2. 有意不动的（下一刀主菜）

- **`ApplyCategoryView`（16 处）/ `CommitLibraryNavigation`（10 处）**：`ApplyCategoryView` 的
  `case "Songs"` 分支里有 `ApplySort(playingPath)` 真排序，而**构造函数**（默认分类就是 "Songs"）和
  `FavoriteCurrentPublic` / `ClearUserPlaylistPublicFromQueue` 等网页可达路径都会走到它。
  删调用会改变播放列表顺序，必须先搞清「网页曲库显示顺序是否来自 `_playlist`」再动手。
- `CommitLibraryNavigation` 牵着导航栈族（`_navBackStack`/`_navCurrent`/`CaptureLibraryNavState`…），
  分布在 EqDsp(7) / Library(14) / xaml.cs(5) 三个文件，体量大，也留到导航刀。

## 3. 量账

**636 → 620 错 / 217 → 215 成员**。DLL 3,018,240 字节。

## 4. 验证

| 项 | 结果 |
|---|---|
| 全量重建 | 0 错误 |
| publish DLL | 3,018,240，符号齐 |
| 接口回归（publish） | cold=4041 reload=4041，0 失败，0 JS 异常，正常退出 |
| 实播两首 | Epilogue → Prologue，切歌正常，正常退出 |

## 5. 交付

- 出货目录已同步；回退件 `dist/web-ui-20261028-pre-w13-navclean-20261031/`（第五刀版本）。

## 6. 下一刀

- **导航域专门甄别**：查「网页曲库显示顺序来源」（`WebLibraryService` 读 `_playlist` 还是直读 `library.db`），
  由此决定 `ApplyCategoryView` 里 `ApplySort` 的去留；`CommitLibraryNavigation` 栈族随导航刀一起。
- `LoadAndAddFiles`（8 处，`OpenFileButton_Click`/`ExternalFile`/`Playback` 在调）：它依赖占位控件
  `PlaylistView.ItemsSource` 的解绑/重绑 —— 那两处绑定操作无效，裁掉后方法可搬活文件。
- `WebDsp.cs` 138 处仍是最大单一借用方（DSP 状态机，第③步之前的硬骨头）。
