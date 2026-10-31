# W13 战报：启动链 + 关闭策略 + 列宽域（批 10，2026-10-31 晚）

接小件宿主四刀（176/88）后按账本打「xaml.cs 启动链（24）」。实测发现 12 个成员里只有一半能干净搬走，
于是把策略从「搬成员」改成「**能整文件转活的先转活**」，最终账面 **176 → 142 错（-34）**，
全天累计 **1,024 → 142（消化 86%）**。一个提交 `45393b9`，四道验证全绿。

## 做了什么

| # | 内容 | 账面 |
|---|---|---|
| 1 | `MainWindow.StartupLoading.cs` **整文件转活**（壳清单除名，零搬运）。依赖 `GeekPhosphorColor`/`ResolveAccentColorStatic` 都在活文件 Utilities.cs | 176→166 |
| 2 | 新 `MainWindow.StartupHost.cs`（258 行）：`ResizeWindowToDips`（DIP→物理像素）、`SetupMinSizeHooks`+`MainMinMaxWndProc`（WM_GETMINMAXINFO 子类化锁 1400×800）、`OnWindowMaximizeStateChanged`+`UpdateMaxRestoreIcon`+`_windowMaximized`（WM_SIZE 驱动图标） | 166→? |
| 3 | 关闭策略整族并入 StartupHost：`AppWindow_Closing` / `HandleCloseRequestAsync`（原在 EqDsp.cs）/ `ShowCloseChoiceDialogAsync` / `MinimizeToTray` | →150 |
| 4 | `MainWindow.Lyrics.cs`（410 行）、`MainWindow.Search.cs`（45 行）**整文件转活** | →150 |
| 5 | `MainWindow.Misc2.cs`（311 行，列宽域）**整文件转活**：`FitColumnsToAvailableWidth` / `ScheduleFitColumns` / 分隔条拖动四件 / 列宽读写 | →142 |

探针壳清单：**21 → 17 个文件**。

## 关键判断

1. **不要只看账本数字选目标**。xaml.cs 剩下的 `MainWindow_Loaded` / `MainWindow_FirstActivated` /
   `MainWindow_Closed` 闭包分别是 **21 / 30+ / 36** 个壳成员（连着 CelesteTheme + ThemeFiles + Geek +
   Settings 整片），图书馆那 5 个 `ContainerContentChanging` 处理器闭包合计 **72** 个。
   搬单个方法进去，新文件借壳产生的账可能比清掉的还多。
2. **「整文件转活」比「搬成员」便宜一个数量级**：文件对外壳依赖为 0 时，改一行壳清单就清掉它名下
   全部借用（Lyrics.cs 一笔清 14 错）。这次四个文件都是这么拿下的。
3. **新工具 `tools/_w13_dep_closure.py`**：搬之前先算闭包，把「试搬→编译→倒贴→退回」的成本
   从几分钟压到几秒。这次靠它躲掉三次无效搬运（Geek 播放状态三件的闭包有 79 个成员）。
4. ⚠ 粗扫外部依赖会假阳性：Misc2 报的「依赖 Features.total」其实是局部变量名。工具输出要人核一眼。
5. 字段 `_windowMaximized` 按铁律手工搬；方法仍走 `_w13_move_method.py`（5 次搬运零手抄）。

## 四道验证

| 项 | 结果 |
|---|---|
| 全量重建（Debug x64, Rebuild） | 0 错误（530 警告，均为既有） |
| publish | DLL **3,019,264** 字节 / **269** 个文件 |
| 接口回归 `W13InterfaceRegression` | failures 空、jsErrors 空、exitCode 0、关闭后进程退出 |
| 实播 `W13DspBatchPlaybackSmoke` | exit 0，ABYSS→Diana，**pos 8.1 真实推进** |

出货已同步 `dist/web-ui-20261028`（COPIED=2、BAD=0）；回退件
`dist/web-ui-20261028-pre-w13-startuphost-20261031`（旧 DLL 3,018,752 字节）。

## 剩余 142（全连深水区，按声明文件排错数）

| 壳文件 | 错数 | 代表成员 |
|---|---|---|
| Features.cs | 26 | InitializeMusicPlayer2Features、三大进度条 Pointer 事件、ImportM3u/OpenCue、ToggleFavoriteForCurrent、OnTagsSaved |
| Library.cs | 22 | RescanLocalLibraryButton_Click、RestoreLastLibraryAsync、文件夹搜索导航四件、RefreshArtistViewAsync |
| Library2.cs | 22 | 三个 ContainerContentChanging、OpenAlbumDetail/OpenArtistDetail、多选退出两件、RefreshFolderBrowserSelectionChrome |
| Misc.cs | 20 | 多选视觉四件（CreateMultiSelectFrostBrush/DisableContainerSelectionCheckMark/SetListSelectionMode/IsMultiSelectSelection）、MoveToRecycleBin、FadeInEngineAsync |
| UiTheme.cs | 16 | MainWindow_Closed/FirstActivated/Loaded、ResolveClassicRowSelectionColor、TryApplyBarStyleRowSelection、EnforceMinimumWindowSize |
| GeekShell.cs | 6 | SetGeekPlayKeyState / SetGeekPlaybackOrderKey / UpdateGeekVolumeText |
| EqDsp.cs / Settings.cs | 4 / 4 | GetAlbumCollectionForGrid、ResolveMultiSelectTargetList、ApplySettingsLive、ConfigureMultiSelectPrimaryAction |

**下一仗建议**：Playback 族要的「多选视觉 / 行选中框」横跨 Misc + UiTheme + Library2 三处，
按域整块搬能一次削三个文件；`ApplySettingsLive` 仍卡在 `ApplyUiStyleMode`（UiTheme 大域）。

---

# 批 11（同会话续）：行选中框样式族整体转活，142 → 128

批 10 提交后继续按闭包挑目标。选中框样式族是 Playback 族里最「横跨三处」的一块：
播放列表 / 专辑墙 / 艺术家专辑 / 文件夹浏览 / 网页桥都用同一套「左侧 3px 主题色竖条 +
半透明灰底」的行选中款，成员却散在 UiTheme / Misc / Library / Library2 四个壳文件里。

## 做了什么（提交 `3a11e1f`）

新 `MainWindow.SelectionChromeHost.cs`（286 行），一次搬入 7 个成员：

| 成员 | 原位置 | 作用 |
|---|---|---|
| `ResolveClassicRowSelectionColor` | UiTheme.cs | 经典选中底色（深色浅灰/浅色灰） |
| `TryApplyBarStyleRowSelection` | UiTheme.cs | 行选中款统一入口（竖条+半透明底） |
| `SoftenItemPresenterCorners` | UiTheme.cs | 关闭系统勾选层、给 Presenter 圆角 |
| `CreateMultiSelectFrostBrush` | Misc.cs | 多选时未选中行的毛玻璃底（带缓存） |
| `DisableContainerSelectionCheckMark` | Misc.cs | 容器加载后关闭系统选中勾 |
| `ApplyAlbumGridItemSelectionChrome` | Library2.cs | 专辑墙卡片行样式入口 |
| `ApplyFolderBrowserItemSelectionChrome` | Library.cs | 文件夹浏览行样式入口 |

**零新借用**：多选状态字段（`_isMultiSelectMode`/`_multiSelectAlbumGrid`/`_multiSelectFolderList`）、
取色入口（`ResolveAccentBrush`/`IsOpaqueUiStyleActive`）、前景色工具
（`ApplyForegroundToDescendants`/`ClearForegroundOnDescendants`）全在活文件里。

⚠ 两个坑：①手写垫底成员后忘删原件 → CS0111 重复定义（工具只搬它负责的那几个）；
②`ListViewItemPresenter`/`GridViewItemPresenter` 在 `Microsoft.UI.Xaml.Controls.Primitives`，
漏 using 会报 CS0246。

## 验证

Rebuild 0 错误；publish DLL 3,019,264 字节/269 文件；接口回归 failures+jsErrors 空、exitCode 0；
实播 `ok=true`、Diana pos 8.1。出货已同步 `dist/web-ui-20261028`，
回退件 `dist/web-ui-20261028-pre-w13-selectionchrome-20261031`。

## 剩余 128（56 个成员）

Playback.cs 46 / Playback2 28 / Playback3 20 / xaml.cs 16 / SettingsHost 6 / Playback4 4 /
AppFlow 2 / ExternalFile 2 / WebBrowseActions 2 / WebTags 2。

**下一步可切的簇**（已算过闭包）：
- 多选模式域 8 成员（`IsMultiSelectSelection`/`SetListSelectionMode`/`ResolveMultiSelectTargetList`/
  `Exit*MultiSelectUiOnly`/`ConfigureMultiSelectPrimaryAction`/`UpdateLibrarySearchUi`/`ResolveCellDetailText`）
  闭包 24 个成员、牵连 Geek 搜索占位符 + 曲库筛选 + 选中框刷新——要连搜索域一起打包才划算。
- 极客播放状态三件闭包 79 个成员——必须整片极客域一起做，不适合小刀。

---

# 批 12（用户拍板）：极客皮肤整体删除，128 → 118

用户指示：「极客皮肤是指原 UI 中的极客皮肤吗，新外壳我暂时不想设计一个极客皮肤，能不能直接删掉相关」。

## 先分清两个「极客」

| 名称 | 是什么 | 处置 |
|---|---|---|
| **旧极客皮肤** | 原生界面的 `UiStyleMode="Geek"`：近黑底、等宽字、直角、磷光色、终端表格、顶部链路读数 | **已整体删除** |
| 网页「极客预设」 | 新外壳 24 个外观预设之一（`preset-geek.css` + `geek-readout.js` 顶部 OUT/FMT/FLAG/BUF 读数） | **保留**——这是外观预设，不是旧皮肤；用户未要求删 |

## 删了什么（提交 `ae2c431`，净 -4,290 行）

- 6 个页面文件：GeekShell(881) / GeekNowPlaying(393) / GeekDsp(317) / GeekTables(306) / GeekTransport(289) / GeekBrowse(269)
- 极客终端页 `MainWindow.Terminal.cs`(761)
- `GeekWindowSkin.cs`(309)：二级窗口跟随皮肤
- UiTheme.cs -675 行：`ApplyGeekChrome` 及其 13 个下游（等宽字体树刷、按钮直角、详情页封面直角、
  专辑卡方角、顶部读数定时器、行内细节重塑、导航字形替换…）
- 播放域：`SetGeekPlayKeyState` / `UpdateGeekVolumeText` / `SetGeekPlaybackOrderKey` /
  `ApplyGeekRowDetailChrome` 四处调用点
- FrostedGlass：`"Geek"` 主题分支、近黑面板/根底色、二级窗口皮肤调用
- 设置层：`UiStyleMode` 选项去掉「Geek|极客终端」；校验白名单去掉 `"Geek"`
  （老存档自动归一到 `ClassicSystem`）

## 怎么保证不炸

1. **先预演再动手**：把 8 个文件临时移出项目编译一次，只有 **20 处唯一错误** →
   证明极客是「高内聚」的，可以整块删，不是想象中 500+ 处牵连。
2. `IsGeekUiStyleActive()` 改成恒 `false`，保留函数名 → 全项目约 40 处 `if (geek)` 自动走经典分支，
   不用逐个改调用点；随后再按文件把死分支剥掉。
3. 行为替换：`SetLibrarySearchBasePrompt` 随 GeekBrowse 删除，改成就地写
   `LibrarySearchBox.PlaceholderText`（经典路径本来就是这么干的）。

## 验证

Rebuild 0 错误（警告 530 → 505）；publish DLL **2,978,304** 字节 / 269 文件（比上版少 41KB，
对应删掉的 3,000 行）；接口回归 failures+jsErrors 空、exitCode 0；
实播 `ok=true`、pos 8.11。出货已同步，回退件 `dist/web-ui-20261028-pre-geek-removal-20261031`。

## 账面

**118 错**：Playback.cs 42 / Playback2 28 / Playback3 16 / xaml.cs 16 / SettingsHost 6 /
Playback4 2 / AppFlow 2 / ExternalFile 2 / WebBrowseActions 2 / WebTags 2。
壳清单 17 → **11 个文件**（6 个 Geek 文件彻底消失）。

⚠ 遗留（不影响功能，属死代码）：`SettingsWindow.xaml.cs` / `SettingsPanelControl.xaml.cs` 里
还有极客下拉项与磷光色设置（这两个是已停用的旧原生设置窗口，入口早已改成切面板）；
`AppSettingsStore.GeekPhosphorColor` 字段与 `PhosphorColor()` 仍在，等旧界面整体退休时一并清。

---

# 批 23-26：多选 / 文件夹浏览 / 行样式入口 / 进度条手势（118 → 68）

极客皮肤删掉后，好几处「原本连着 GeekBrowse」的成员变干净了，顺着一路收。

| 批 | 提交 | 内容 | 账面 |
|---|---|---|---|
| 23 | `5a20ad4` | 新 `MainWindow.MultiSelectHost.cs`（250 行）：选择模式安全切换（None 中转）、多选目标解析、专辑/文件夹多选退出、媒体条主按钮语义、单元格详情文案 | 118→100 |
| 24 | `5a20ad4` | 新 `MainWindow.FolderBrowseHost.cs`（262 行）：文件夹树搜索跳转（上/下一条、跳到第 N 条、按路径展开）、导航条刷新、根节点重建、子项枚举、展开收起、音频扩展名判定 | 100→80 |
| 25 | `a19ca4f` | 三个 `ContainerContentChanging`（专辑墙/艺术家专辑/文件夹浏览）并入 SelectionChromeHost —— 极客方角调用删掉后它们已自足 | 80→74 |
| 26 | `9e33236` | 新 `MainWindow.SeekGestureHost.cs`：进度条按下/松开/丢捕获 + `EndNowPlayingSeekGesture` | 74→68 |

## 两个必须记住的坑

1. **`MainWindow.NowPlayingLayout.cs` 不参与编译**：它在 csproj 的 `_w13_npcluster` 排除块里
   （连同 Terminal/TerminalLyrics/Visualizer）。批 26 一开始把进度条手势搬进它，编译立刻报
   「找不到方法」——搬到那里等于删掉。**判断目标文件是否活，不能只看壳清单，还要看 csproj 的
   Compile Remove 块。**
2. **手写垫底成员后必须删原件**：批 23/24 连续两次踩到，手写 `IsMultiSelectSelection`、
   `IsSupportedAudioFile` 垫在目标文件里，工具又搬来一份 → CS0111。
   正确做法：垫底只用一个**不会被搬**的临时字段，搬完再删。

## 验证

Rebuild 0 错误；publish DLL 2,978,304 字节 / 269 文件；接口回归 failures+jsErrors 空、exitCode 0；
实播 `ok=true`、pos 8.09。出货已同步，回退件 `dist/web-ui-20261028-pre-w13-seekgesture-20261031`。

## 剩余 68 错

Playback 16 / Playback3 16 / Playback2 14 / xaml.cs 8 / SettingsHost 6 / 零散 8。
xaml.cs 只剩 4 个（`MainWindow_Loaded` / `FirstActivated` / `Closed` / `LibraryPaneRoot_PointerPressed`），
全连着主题域或导航历史域——属于下一阶段的大件，不适合小刀。

---

# 批 27-28：播放杂项 + 文件打开入口（68 → 36）

用新写的「自足性扫描」一次性筛出所有**自身零壳依赖**的成员，成批搬走——比逐个试搬快得多。
脚本逻辑：对每个剩余成员跑闭包，把「除了自己以外没有任何壳成员」的直接列出来。

| 批 | 提交 | 内容 | 账面 |
|---|---|---|---|
| 27 | `33dae03` | 新 `MainWindow.PlaybackOpsHost.cs`：按路径回填索引、移入回收站、专辑集合解析、睡眠定时到点判定；`EnforceMinimumWindowSize` 并入 StartupHost | 68→58 |
| 28 | `33dae03` | 同文件续：封面背景铺/撤、波形柱绘制、音频信息文本刷新、引擎淡入、额外资源自动下载；新 `MainWindow.FileOpenHost.cs`：打开文件 / 打开文件夹 / 导入 M3U / 播放 CUE（含 `PlayCueFileAsync`） | 58→36 |

一次筛出的 8 个完全自足成员：`ClearAlbumArtBackground`、`DrawWaveformStyle`、`ImportM3u_Click`、
`MaybeAutoDownloadExtrasAsync`、`OpenFileButton_Click`、`OpenFolderButton_Click`、
`PlayCueFileAsync`、`UpdateAudioInfoTextAsync`；另有 3 个只差「同组内一个成员」
（`ApplyAlbumArtBackgroundAsync→ClearAlbumArtBackground`、`OpenCue_Click→PlayCueFileAsync`，
`FadeInEngineAsync` 的所谓依赖 `steps` 是局部常量、工具误报）。

⚠ 工具假阳性再次出现：`EqDsp.cs -> Features.total` 的 `total` 也是局部变量。
所以「按文件统计外部依赖」只能当线索，`total`/`steps`/`count` 这类名字必须人眼核。

## 验证

Rebuild 0 错误；publish 2,978,816 字节 / 269 文件；接口回归 failures+jsErrors 空、exitCode 0；
实播 `ok=true`、pos 8.1。出货已同步，回退件 `dist/web-ui-20261028-pre-w13-playbackops-20261031`。

## 剩余 36：卡在两个枢纽函数

剩余 17 个成员全部有壳依赖（4–88 个），已无「零依赖可搬」的。
按壳文件统计对外依赖，最小的几个卡在这些点：

| 壳文件 | 外依赖数 | 卡在谁身上 |
|---|---|---|
| ShellPages.cs | 1 | `ApplyCategoryView`（Misc，分类视图大 switch，闭包 39） |
| CelesteTheme.cs | 2 | ShellPages 的 `ReapplySettingsAccent` / `ReapplyTagPanelAccent` |
| ArtistDetail.cs | 3 | `ArtistAvatarStoreKey`(Library)、`ApplyArtistAvatarToDetailIfOpen`(Library2)、`ApplyUiStyleMode`(UiTheme) |
| Settings.cs | 3 | `ApplyExtendedSettingsLive`(Features)、`ExitMediaMultiSelect`(Misc)、`ApplyUiStyleMode` |
| ThemeFiles.cs | 3 | `ApplyCelesteThemeAsync` / `LoadCelesteActiveTheme`(CelesteTheme)、`MainWindow_Loaded`(UiTheme) |

**结论：下一步必须拔「枢纽」而不是搬成员。**
两个枢纽：`ApplyCategoryView`（闭包 39）和 `ApplyUiStyleMode`（牵动 ArtistDetail / Settings / ThemeFiles）。
拔掉一个能连带解锁 2-4 个壳文件，但要整块连闭包一起搬，属于一次独立的大活。

---

# 批 29-30：曲库视图 + 主题链（36 → 26，壳清单 11 → 9）

## 批 29（`b783ff7`）：曲库视图刷新域，36 → 28

新 `MainWindow.LibraryViewHost.cs`，一次搬 18 个成员（4 个文件里的同一功能域）：

| 来源 | 成员 |
|---|---|
| Library2.cs | `RefreshAlbumViewAsync`、`ApplyAlbumSortToList`、`OpenAlbumDetail`+`OpenAlbumDetailCore`、`RefreshAlbumWallSelectionChrome` |
| Library.cs | `UpdateLibrarySearchUi`、`ApplyLibrarySearchNow`、`ClearFolderSearchHighlightOnly`、`RefreshArtistViewAsync`、`ArtistAvatarStoreKey`、`ResolveArtistAvatarAsync`、`ResolveArtistDefaultAvatarAsync`、`TryLoadWebArtistAvatarAsync` |
| EqDsp.cs | `ApplyAlbumsSearchFilter`、`ApplyArtistsSearchFilter` |
| Misc.cs | `EnumerateRealizedGridViewItems` |

另手工搬两个静态字段（`WebArtistAvatarCache` / `WebArtistAvatarGate`）——
按铁律「分号结尾字段绝不用配平工具」，手工按行搬。

## 批 30（同提交）：主题链 + 两个文件转活，28 → 26

新 `MainWindow.ThemeHost.cs`（16 个成员）：`MainWindow_Loaded`、`ApplyCelesteThemeAsync`、
`LoadCelesteActiveTheme`、`ResolveIsDark`、`ApplyCelesteTokensToTree`、`CountTokenKeys`、
`RefreshDetailPagesForTheme`、`ReloadThemeFromSettings`、两个空实现的强调条刷新，
以及 4 个字段/事件/属性（`_celesteTokenDictionary`、`_celesteAppliedThemeName`、
`CelesteThemeApplied`、`AppliedCelesteThemeName`、`_themeAppliedOnce`、`_lastAppliedThemeName`）。

**连带效果**：`MainWindow.CelesteTheme.cs`（剩 3 个强调色解析）与 `MainWindow.ThemeFiles.cs`
（剩 7 个主题文件读写）的壳依赖全部消失 → 两个文件**整文件转活**，壳清单 11 → 9。

⚠ 但这俩文件目前**没有活调用方**（探针里从没出现过它们的成员）。它们是被「重新编进来」的死代码：
要么以后接回网页设置页的主题导入/导出，要么确认不需要后删掉。**这件事需要用户拍板，我没擅自删。**

## 验证

Rebuild 0 错误；publish DLL 2,978,816 字节 / 269 文件；接口回归 failures+jsErrors 空、exitCode 0；
实播 `ok=true`、pos 8.09。出货已同步，回退件 `dist/web-ui-20261028-pre-w13-themechain-20261031`。

## 剩余 26：12 个成员，每个都是一整块功能

| 成员 | 闭包 | 是什么 |
|---|---|---|
| `ApplySettingsLive` | 74 | 设置实时应用（最大的一块） |
| `RescanLocalLibraryButton_Click` | 51 | 重扫曲库 |
| `LibraryPaneRoot_PointerPressed` | 40 | 鼠标侧键前进/后退 |
| `MainWindow_FirstActivated` | 40 | 首窗口激活（标题栏扩展） |
| `RestoreLastLibraryAsync` | 34 | 启动恢复上次曲库 |
| `MainWindow_Closed` | 32 | 关闭序列 |
| `RestartLibraryWatchFromSettings` | 32 | 曲库监听重启 |
| `AppendPlaylistContextFeatureItems` | 27 | 播放列表右键菜单 |
| `OnTagsSaved` / `OpenArtistDetail` | 25 / 25 | 标签保存后刷新 / 艺术家详情 |
| `InitializeMusicPlayer2Features` / `ToggleFavoriteForCurrent` | 23 / 23 | 播放器初始化 / 收藏切换 |

每个都要连 20-70 个闭包成员一起搬，属于「一块功能一刀」。下一步建议先打 `MainWindow_Closed`
（关闭序列，行为最独立、验证最容易：启停一次就知道有没有问题）。

---

# 批 31-33：鼠标侧键接上 + 三处死接线清理（26 → 18）

## 批 31（`1c91545`）：鼠标侧键前进/后退 —— 用户点名要的功能

新外壳原来没有侧键导航，本次在**网页层**补上（不是原生层，原生窗口是隐藏的，收不到事件）：

- `shell.js` 新增 `backStack` / `fwdStack` 双栈：任何一次新导航清空前进栈。
- `XButton1`（mouseup button 3）= 后退，`XButton2`（button 4）= 前进；
  `mousedown` 里 `preventDefault`，防止 Chromium 把它当自己的历史导航。
- **后退的优先级很重要**：只有「详情页」（面包屑 ≥3 级：主页/分类/条目）才走面包屑父级，
  普通分类页走页面栈。第一版没加这个判断，结果在「专辑」页按后退永远跳回主页 ——
  因为导航后面包屑已经是两级（主页/专辑），`at(-2)` 拿到的是「主页」。
- `CelesteShell` 暴露 `goBack` / `goForward` / `getNavStacks`（后者给探针看栈）。

**探针**：新建 `tools/W13SideButtonProbe.cjs`，合成 XButton 事件跑完整序列：
专辑 → 后退→歌曲 → 后退→总览 → 前进→歌曲 → 前进→专辑，且新导航后前进栈清空。结果 `ok: true`。

**原生侧清理**：`LibraryPaneRoot_PointerPressed` + `NavigateLibraryHistoryBack/Forward` 删除（-48 行）。

## 批 32（`b164e94`）：从未接线的艺术家链接处理器

`NowPlayingArtistLinkButton_Click` 从没被任何 `Click +=` 订阅过（LegacyControls 造的那个按钮
只被设过 `IsEnabled`），纯死代码。删掉后 `OpenArtistDetail` 的最后一个借用消失。

## 批 33（`1106830`）：两处原生菜单死接线

`PlaylistView_RightTapped` 的右键菜单挂在 `CurrentPlaylistWindow.xaml` 上，而
`CurrentPlaylistWindow.xaml.cs` 在 csproj 的 Compile Remove 块里 —— 这个窗口运行时根本不存在，
所以菜单永远弹不出来。据此移除两处接线（**功能本身保留**，只是不再从死入口进）。

## 验证

Rebuild 0 错误；publish 正常 / 269 文件；接口回归 failures+jsErrors 空、exitCode 0；
侧键探针 `ok:true`；实播 `ok=true`、pos 8.1。出货已同步。

## 剩余 18 错 / 8 个成员（逐个定性）

| 成员 | 性质 | 新外壳有替代吗 |
|---|---|---|
| `ApplySettingsLive`（闭包 74） | 网页设置改动后回写宿主 | **有**（网页设置自己应用外观），但宿主侧「音量/输出/歌词」等仍需应用 |
| `RestartLibraryWatchFromSettings`（32） | 改曲库文件夹后重启监听 | 无，**必须保留** |
| `RestoreLastLibraryAsync`（34） | 启动恢复上次曲库 | 无，**必须保留** |
| `MainWindow_FirstActivated`（40） | 首窗口激活（标题栏扩展/DPI） | 无，**必须保留** |
| `MainWindow_Closed`（32） | 关闭序列（存进度/销毁引擎/退托盘） | 无，**必须保留** |
| `ToggleFavoriteForCurrent`（23） | 全局热键「收藏当前曲」 | 无（网页没有全局热键），**必须保留** |
| `InitializeMusicPlayer2Features`（23） | 播放器初始化后的功能挂接 | 部分（网页接管 UI），**需逐个看** |
| `OnTagsSaved`（25） | 网页改完标签回宿主刷内存 | **有**（网页自己刷），宿主只需通知，可瘦身 |

---

# 批 34-35：迁移收官（18 → 0），旧界面壳文件全部删除

用户要求「做快一点，这一轮多做一些」，于是不再逐个试搬，改成**一次性算完整闭包 + 批量搬**。

## 批 34（提交 `e2b6e0b`）：最后 107 个成员，18 → 0

先用闭包工具算出 8 个残留成员的完整闭包（107 个成员、跨 8 个壳文件），
再按域一次性建 6 个宿主文件批量搬运：

| 宿主 | 内容 |
|---|---|
| `WindowLifecycleHost` | 首激活 / 关闭序列 / 关闭子窗口 / 窗口框架 / 背景视频的窗口状态 / `IsIconic` |
| `BackgroundMediaHost` | 自定义背景图与视频、背景动效、主题色联动、最小化暂停 |
| `NavigationStyleHost` | 分类图标配色与字形、导航选中动画、胶囊排序按钮、边框取色 |
| `CategoryViewHost` | `ApplyCategoryView` + 收藏/最近/最常听/评分视图、空列表提示、导航胶囊取色 |
| `LibraryLoadHost` | 恢复/重扫曲库、批量建项、文件监听、标签保存后刷新、功能挂接与热键、`PlaylistBuildResult` |
| `LibraryChromeHost` | 详情页关闭、专辑排序按钮、左侧导航高亮 |
| `SettingsApplyHost`（已有） | `ApplySettingsLive` + 扩展应用 + 界面风格应用 |

**86 个方法 + 17 个字段/嵌套类型**。踩到三个坑：
1. **闭包工具漏字段**：它只认「带修饰符的方法」，字段没算进去 → 搬完报 132 个错，
   一看只有 17 个字段，手工补完就归零。**方法闭包 ≠ 完整依赖，字段要单独扫。**
2. **using 被插进方法体**：我用「插到最后一条 using 之后」的脚本补命名空间，结果文件里有
   `using TagLib.File tagFile = ...` 这种语句，被当成文件头 using，插到了方法中间。
   改成「只认 `^using X;$`」并把误插的行搬回文件头才修好。
3. **`[DllImport]` 特性没跟着搬**：`IsIconic` 搬走了、特性行留在原地，编译器报 CS0601。

## 批 35（提交 `1a0d9d5`）：删掉 9 个壳文件，-7,260 行

探针归零意味着**没有任何活代码再引用旧界面**，于是整批删除：

`Features` / `Misc` / `UiTheme` / `Library` / `Library2` / `EqDsp` / `ShellPages` /
`ArtistDetail` / `Settings` —— 合计 7,251 行。

产物 DLL 从 2,978,816 → **2,844,160** 字节（-134KB），与删除行数吻合。

## 验证（删除前后各跑一遍，全绿）

- Rebuild 0 错误
- publish 正常 / 269 文件
- 接口回归：failures+jsErrors 空、exitCode 0、**关闭后进程正常退出**（证明搬走的关闭序列没问题）
- 侧键探针 `ok:true`
- 实播 `ok=true`、pos 8.09

## W13 迁移阶段收官

账面 **1,024 → 0**。三层结构现在是：
① 网页外壳（`outputs/webview2-shell`）+ WebView2 宿主；
② 业务服务与宿主分片（`*Host.cs` 二十余个，按域划分）；
③ 已删除的旧原生界面（只在 git 历史里）。

剩下的旧界面周边（NowPlayingLayout / TerminalLyrics / Visualizer 等 9 个
`Compile Remove` 文件）仍在仓库里但**从未参与编译**，属于下一步清理项。
