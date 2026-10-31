# W13 第十三刀：切分类视图活调用点改道，ApplyCategoryView 借用清零（2026-10-31）

提交：代码 `见 git log`。接第十二刀（导航栈族，`fef47a1`）。
本轮是**导航域核心战役的第一阶段**：不打 290 行本体，先打它的全部活调用点。

## 1. 全分支甄别结论（ApplyCategoryView，壳 Misc.cs:1271，276 行）

九成篇幅是给旧界面占位控件摆面板（网页看不到，死）；**真正影响网页/播放行为的只有两处**：

| 分支 | 活行为（保留） | 死行为（网页不可观察） |
|---|---|---|
| `Songs` | **强制重置「标题升序」+ `ApplySort(playingPath)`** —— `_playlist` 顺序 = 网页曲库显示顺序（`LibraryTracks => _playlist`，第七刀查实）；经 `SyncIndicesAfterSort` 保住 `_currentIndex` | 摆 SongSortPanel/四个列表边框、关两个详情页 |
| `UserPlaylist` | **`RenumberCollection(_queue.Items)`** 重写队列序号（不排序，保持添加顺序） | ItemsSource 重绑、滚到正在播放、关详情页 |
| `Albums` | 无（`RefreshAlbumViewAsync` 重建 `_albums`，读者只有旧专辑墙与两个旧链接 handler；网页专辑页自己算数据） | 同上 |
| `Artists`/`AlbumArtists` | 无（同上，`_artists` 无网页读者） | 同上 |
| `Folders`/`WebDav` | 无（`_folderBrowserItems` 旧树专用） | 同上 |
| `PlaylistWall` | 无（`_playlistWall` 旧卡片墙） | 同上 |
| `Favorites`/`Recent`/`Ratings`/`MostPlayed` | 无（过滤副本进旧 `PlaylistView`，不动 `_playlist`/`_queue`） | 同上 |
| `Genres`/`Years`/`GenreSongs`/`YearSongs` | 无（GenreSongs 只**读** `_playlist` 建过滤副本，不写） | 同上 |
| `AudioFX`/`Settings`/`TagEdit` | 无（旧面板构建） | 同上 |

伴生方法定性：`ApplySort`/`RenumberCollection`/`SetSongSortUiForCategory` **本来就在活文件**（Playback2.cs），
不欠壳的账；`CloseAlbumDetailUi`/`CloseArtistDetailUi`/`Refresh*ViewAsync` 等 ~19 个是壳成员，
**整体搬 ApplyCategoryView 会倒贴账（-16 借用换 +19 新借用）**，故选「调用点改道」而非「搬家」。

## 2. 处理：13 个活调用点全部改道 `ApplyCategoryViewData()`

新活方法放进 `MainWindow.NavHistory.cs`（导航域），只含上表两个活分支，逐行等价：
`Songs` → playingPath 捕获 + 重置 Title/asc + `ApplySort`；`UserPlaylist` → `RenumberCollection`。

13 处改道（壳内调用不动，随壳退场）：

| 文件 | 处数 | 路径性质 |
|---|---|---|
| MainWindow.xaml.cs | 1 | **启动**（startupCategory 可为 9 个合法值；Songs 时触发真排序） |
| MainWindow.Playback.cs | 5 | 播放队列/列表墙导航、`ShowPlaylistDetail`（仅旧墙卡片调）、`OpenGenreYearSongs`（仅旧网格调） |
| MainWindow.Playback2.cs | 3 | **网页清空队列** `ClearUserPlaylistPublicFromQueue`、加队列跳转、旧按钮 |
| MainWindow.Playback3.cs | 3 | 托盘收藏刷新（Favorites）、正在播放歌手/专辑链接（旧控件，不可达） |
| MainWindow.WebDav.cs | 1 | **WebDav 未配置回退到 Songs**（活路径，触发真排序） |

改道后各路径的活行为与旧版逐行一致（Songs 排序重排、UserPlaylist 重编号都保留）；
唯一差异是不再执行旧控件摆面板——那本来就是网页不可观察的。导航快照
`CaptureLibraryNavState` 只读分类/详情字段，不受影响（已核对）。

## 3. 量账与验证

**454 → 428 错 / 168 → 167 成员。`ApplyCategoryView` 从借用榜除名（残余 0）。
账面新的最大单项回到 DSP 域：`ApplyFirParameters`(8)、`PushEqUndoSnapshot`/`SetAudioFxEqFromSnapshot`(各 6)、
`LoadAndAddFiles`(6)。**

| 项 | 结果 |
|---|---|
| 全量重建（Debug x64） | 0 错误（533 警告，均为既有 CS0169/CS0067） |
| publish DLL | 3,019,264 字节（与上刀同体积，改动仅调用改道+一个新方法），PDB 775,372 齐全，DLL 内含新方法名 |
| 接口回归 | cold=4041 reload=4041，0 失败，0 JS 异常，正常退出 |
| 实播两首 | Epilogue → Prologue，currentIndex 正常，exitCode 0 |
| csproj | 探针跑完字节级还原 ✓ |

## 4. 交付

- 出货目录已同步：`dist/web-ui-20261028/`（COPIED=3，BAD=0）；重启即生效。
- 回退件：`dist/web-ui-20261028-pre-w13-navdata-20261031/`（3,019,264 = 本轮改前出货版）。

## 5. ⚠ 需要用户实测的一项

`case "Songs"` 的强制排序直接决定**网页曲库的显示顺序**。请打开播放器核对：
网页曲库（歌曲页）顺序应与改前一致（标题升序）。启动分类若设为「播放队列」，队列序号应正常。

## 6. 下一刀

- **`LoadAndAddFiles`（6 处）**：文件加载入口，尾部与 ApplySort/RenumberCollection/ApplyCategoryView 咬合，
  现在活路径不再调 ApplyCategoryView，搬离壳的阻力已减小 —— 导航域第二阶段。
- **DSP 域**（最大块 ~150 处）：`ApplyFirParameters`(8) 已是榜上第一，按模块逐个小刀。
- `ScheduleFitColumns`(6)/`RefreshFolderBrowserSelectionChrome`(6)/`SetListSelectionMode`(6) 等列表视觉族逐个甄别。
