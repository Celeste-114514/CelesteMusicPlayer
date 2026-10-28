# W6 第二小步：曲库/媒体库服务迁出（无 WinUI 宿主可用）

接续 TASK-2026-10-28-settings-boundary.md（第一小步，提交 c52b8a5 / d87e272）。
托盘/任务栏条由 codex 在 378b146 收尾后按用户指令未再触碰。

## 结论

曲库/媒体库这条链**已整条迁出 WinUI/WinRT 依赖**，链接进 `tools/ThinHostPoC` 编译通过，
隐藏自测新增「曲库边界」20 项全 OK（`Library boundary: checks=20 passed=True`），
result.json `passed=true`（20261029-011919-842b20-1912）。设置边界自测保持全绿
（probed=61, cloneMismatch=0, schemaMismatch=0）。

## 链接进 PoC 的曲库文件

LibraryDb（+ Microsoft.Data.Sqlite 10.0.11，与主工程同版本）、LibrarySessionStore、
LibraryWatchService、LibraryPathGuard、PlaylistLibraryService、TrackStatsStore、
PlayQueueStore、NamedPlaylistStore、UserPlaylistFileStore、JsonFile、
RgScanInput、AppCachePaths、NetworkLibraryStore、NetworkLibraryModels、WebDavLocation、WebDavClient。

## 为切断依赖做的等价改动（行为不变）

| 文件 | 改动 |
|---|---|
| `LibraryDb.cs`（3 处）/ `LibrarySessionStore.cs` / `PlaylistLibraryService.cs` / `TrackStatsStore.cs` | 删 WinRT `ApplicationData.Current.LocalFolder` try/catch，改用 `AppSettingsStore.GetConfigDirectory()`。非打包运行那条分支本就抛异常走 catch 落同一个 `%LOCALAPPDATA%\CelesteMusicPlayer`，路径等价（同第一小步 AppClosePreferences 的处理） |
| `TrackStatsStore.cs` / `PlaybackSessionStore.cs` | 删残留 `using Windows.Storage;`（后者实际早已走 GetConfigDirectory） |
| `RgScanInput.cs`（新） | 3 字段 POCO 从 ReplayGainScanner.cs 迁出：LibraryDb.GetAllTracksForScan 返回它，不该被扫描器的 TagLib# 依赖拖住。命名空间不变，调用点零改动 |
| `AppCachePaths.cs`（新） | 缓存路径真值（DSD 默认根 / 转码缓存 / WebDAV 缓存默认根）从 DsdPreloadService / FfmpegDecoderBackend / WebDavCache 抽出，三个重类反向引用，值逐字一致。照 SrcOptions 先例 |
| `LibraryPathGuard.cs` | 不再引用 DsdPreloadService/FfmpegDecoderBackend/WebDavCache：DSD 根目录逻辑等价内联（读设置 DsdCachePath，空则默认），转码/WebDAV 根走 AppCachePaths。否则曲库层被拖进整个音频引擎 |

**没迁的**：PlaylistCardViewModel、PlaylistColumnWidths（UI 视图模型，BitmapImage/XAML，属界面侧）；
FfmpegDecoderBackend/DsdPreloadService/WebDavCache 本体（音频引擎侧，只反向依赖 AppCachePaths）。

## 新增机器检查（tools/ThinHostPoC/LibraryBoundary.cs，20 项）

红线：只用 `LibraryDb.TestDbOverride` 临时库 + 自检专属临时目录，**不碰用户真实曲库/设置**（结束归零并清理）。
覆盖：临时库迁移自举、命名单增删改查、曲库会话往返、播放历史往返+清空、标签索引指纹往返、
路径守卫（*.dop24.wav 名字兜底 / 转码缓存目录 / 正常曲不拦）、PurgeInternalCacheTracks
（清缓存产物且保留正常曲，顺带走通 LibraryPathGuard→AppCachePaths→AppSettingsStore 只读链）、
JsonFile 读写往返+DeepClone、AppCachePaths 三路径确定性。

## 没动的地方

托盘、任务栏条、音频引擎实现、网页外壳一行未动；用户真实 library.db / app-settings.json 全程未写。
