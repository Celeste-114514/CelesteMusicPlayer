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

探针壳清单：**21 → 18 个文件**。

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
