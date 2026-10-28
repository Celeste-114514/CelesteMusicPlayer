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

---

# 追加：W6 第三小步 —— 歌词/CUE/M3U 解析链（01:26-01:40）

## 结论

LyricsLoader / CueSheetParser / M3uPlaylistIO 链接进 PoC（+TagLibSharp 2.3.0），
干净 HEAD 独立验证：设置 61 项 + 曲库 20 项 + 解析 10 项全绿，result.json passed=true
（20261029-013651-e39c0d，worktree 隔离验证）。提交 a153844 后续 3 个：
adf9653（补漏 FfmpegDecoderBackend）、e682982（解析链）、f8e1e3a（EXTINF 逗号修复）。

## 顺手修的潜在 bug

M3uPlaylistIO.TryParseExtInf 的正则没消费 EXTINF 里的逗号，displayTitle 会带前导逗号。
该函数当前无生产调用方（休眠），修正后由解析边界自测钉住行为。

## CueTrackExpansion 暂缓（记为待办）

入参类型 PlaylistItem 定义在 MainWindow.xaml.cs（还读 MainWindow.GeekUiStyleCached 静态缓存）。
要把「播放列表行模型」从界面文件搬出去才能迁——这是独立的一小步，风险高于本次范围。

## ⚠ 并行协作实录（重要教训）

1. **我漏 git add**：a153844 的 add 列表漏了 FfmpegDecoderBackend.cs，提交信息与实际内容不一致
   （工作区和验证环境是对的，仓库里缺），adf9653 补上。教训：提交前 `git status` 全量过一遍，
   别只看 staged。
2. **codex 在我提交前后往同一批 PoC 文件里写他的 WIP**：LibraryBoundary.cs 被他扩到 30 项
   （网络源/cue/identity 检查，引用了他未提交的 NetworkLibraryStore.DbPathOverride），
   csproj 被他加了 4 个 provider 链接和重复的 TagLibSharp 行。**我的 a153844 没带上他的改动
   （HEAD 干净自洽），但工作区混跑了**：_publish_poc 编译的是含他半成品的工作区，
   他的 cue.parse 检查失败导致整包中断，一度让我误判成自己的问题。
3. **处置**：他的 WIP 一行没动、没提交；我的文件单独提交；验证改用 `git worktree` 在干净
   HEAD 上独立构建运行（发布目录、结果目录都隔离）。共享 _run_poc_test.py 取"最新结果目录"
   的逻辑在并行跑时会抓错别人的结果——并行期间要用隔离发布目录自己挑自己的 RUN_DIR。
4. csproj 里 codex 的 provider 链接随我的提交进了仓库（可独立编译，无破坏）；重复的
   TagLibSharp 行已去重。

## W6 剩余候选

播放列表行模型迁出界面文件（解锁 CueTrackExpansion）→ 网络源服务（codex 正在做，别撞）
→ 封面 CoverArtService（注意 CacheVersion）→ TagLib 标签读写层。
