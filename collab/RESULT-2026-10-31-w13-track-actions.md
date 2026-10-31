# W13：曲目动作搬出 MainWindow（trackAction + browse）

日期：2026-10-31
提交：见本次 commit（父提交 `fcace61`）
回退件：`dist/web-ui-20261028-pre-w13-trackactions-20261031/`（`A1076EDE…`，3,085,312 字节）

## 一、这一刀切的是什么

上一刀（目录浏览）结尾列的下一块就是它：网页右键菜单里能点的所有动作。

`MainWindow.WebBrowseActions.cs` 原来一个文件装两件事：

| 命令 | 干的事 |
|---|---|
| `browse` | 播放列表封面 / 复制 / 导入导出 / 整表播放入队 / 打开 .cue |
| `trackAction` | 播放、下一首播放、加入队列、置顶、移出队列、喜欢、评分、标签、定位、复制、删除 |

两者都要「解析网页给的行 → 找曲目 → 调宿主」。搬出去之后这个文件只剩宿主实现。

## 二、做了什么

### 新增 `WebOperationGate.cs`

原来是 `private bool _webBrowseBusy`，曲目动作和目录浏览各写一遍 `if/true/try/finally`。
现在抽成一个小类型，两个分片**共用同一个实例**：

```csharp
using IDisposable lease = busy.TryEnter() ?? throw new InvalidOperationException("请先完成当前操作。");
```

必须共用。否则「挑文件夹」和「挑播放列表」能同时弹出来，用户看到两个对话框叠在一起——
那是行为变化，不是重构。

### 新增 `WebTrackActionService.cs`（203 行）+ `IWebTrackActionHost`

服务里零界面代码。宿主接口只留服务自己造不出来的东西：

| 成员 | 为什么必须在宿主 |
|---|---|
| `Queue` | `PlaybackQueue` 本来就是独立类型，整个交出去 |
| `AllLibraryTracks` / `LibraryFolderTracks` | 曲库快照（属性取，沿用 `WebLibraryService` 口径） |
| `PlayQueueIndex` / `PlaySong` | 真播放；「曲库曲目 vs 临时曲目」的判定也留在宿主 |
| `RefreshQueueUi` | 重编号 + 队列视图 + 通知网页主页 + 用户播放列表过滤 |
| `RefreshFavoriteChrome` | 喜欢的图标和任务栏缩略图按钮 |
| `ShowTagEditor` / `RevealInExplorer` / `ActivateWindow` | 外部程序与窗口 |
| `DeleteFromDiskAsync` | 删盘要确认对话框，`XamlRoot` 是宿主内部细节 |
| `BrowseSnapshot` | 曲库页回包还没搬（在 `MainWindow.Library.cs`） |
| `LoadNamedPlaylistAndPlayAsync` / `AddNamedPlaylistToQueue` / `PlayCueFileAsync` | 播放列表装载与 .cue |

`PlaylistMedia`（封面/导入导出）本来就是 `WebPlaylistMediaService`，现在由服务自己持有——
无状态，多一个实例没有代价。

### `MainWindow.WebBrowseActions.cs` 144 → 83 行

只剩：`_webBusy` 字段、`TrackActions` 懒加载、三条转发、`RefreshQueueUi`、嵌套的 `WebTrackActionHost`。

`RefreshQueueUi` 里那两句 `PlaylistView.ItemsSource` 重绑照抄了原顺序：
`PlaylistView` 现在是 `LegacyControls` 里的编译占位控件（没有视觉树），
保留它只是不想让行为漂移，等真界面回来时不用重想顺序。

### `MainWindow.WebFolders.cs` 改用同一把锁

`_webBrowseBusy` 的三行 try/finally 换成 `using (var lease = _webBusy.TryEnter() ?? throw …)`，
括号位置和原来一致（只包住挑文件夹那一段，不包 `RootsAsync`）。

## 三、验证

| 项目 | 结果 |
|---|---|
| 编译 | `-t:Rebuild` 全量重建，0 错误 |
| 回包逐字段比对 | **0 差异**（含 `queue` 修订号序列，两边都走到 revision 8） |
| 拒绝路径 | 5 条（非队列 pin / 评分越界 / 未知曲目操作 / 空选择 / 旧修订号）文字完全一致 |
| `browse` 转发 | 键集 `[playlists, tracks]` 一致；未知操作、播放列表不存在两条报错一致 |
| 接口回归 | 冷启动、重载各 4041 首，0 失败、0 JS 错误 |
| 曲线回归 | 164 项全过 |
| 发布产物 | 3,086,336 字节（正常区间），md5 与 publish 一致 |
| 出货运维 | 已同步 `dist/web-ui-20261028`，`BAD=0 STALE=0` |

### 探针 `tools/W13TrackActionsProbe.cjs`

改前 / 改后各跑一次，`_w13_trackactions_before.json` 与 `_after.json` 递归比对。

断言到的实际语义（不是「没报错」，是数值对得上）：

| 动作 | 期望 | 实测（前后一致） |
|---|---|---|
| `get`（曲库） | 3 首、`favorite=false`、`canDelete=false` | 一致 |
| `favorite` 翻转 | 翻完 true、还原后 false | 一致 |
| `rating` 3 → 0 | 落盘 `Rating=0`、`IsFavorite=false`、`PlayCount=1` | 一致（外部核对 `track-stats.json`） |
| `next`（已在队列） | 长度不变、落在 `currentIndex+1` | delta 0 / landedAt 1 |
| `next`（队列外） | 长度 +1、落在 `currentIndex+1` | delta 1 / landedAt 1 |
| `addQueue`（已在队列） | 长度不变、因「插入到开头」设置落在 0 | delta 0 / landedAt 0 |
| `addQueue`（队列外） | 长度 +1、落在 0 | delta 1 / landedAt 0 |
| `pin` | 从第 3 行挪到 0、总数不变、只出现 1 次 | 一致 |
| `removeQueue` | 总数 -1、队首换人 | 一致 |
| 队列漂移 | +2（两次插入） | 一致 |

探针全程不播放（播放会让 `currentIndex` 自己往前走，插入位置就不可复现）；
`play` 分支单独测，播 1 秒即 `togglePlay` 停掉。

## 四、踩到的坑

1. **`playAll` / `togglePlay` 不能走 `CelesteTransport.request`**
   它们不回包，`request` 会等到 12 秒超时，然后抛「操作响应超时」。
   第一版探针因此让播放一直跑，`currentIndex` 从 0 自己走到 2，
   `next` 的 `expectedAt` 全错。改用 `CelesteShell.send`（发完不等）。
2. **`transport` 命令没 `pause`**
   `transport` 只认 `action`（`seekBack` / `cycleOrder` / `desktopLyrics` …），
   播放/暂停是顶层命令 `togglePlay`。第一版传 `{operation:'pause'}` 报
   `The given key was not present in the dictionary.`
3. **`getLibrary()` 里没有 `rating`**
   想从网页快照读回评分是读不到的。改成外部读 `%LOCALAPPDATA%\CelesteMusicPlayer\track-stats.json` 核对。
4. **「队列外第 10 首」被自己的前置步骤污染**
   第一版 `next` 和 `addQueue` 用了同一个 outsider，`next` 把它加进队列后，
   `addQueue` 就变成「已在队列」，`delta` 恒 0。改成两个不同的 outsider。

## 五、没做的

- `delete`（真删盘）与 `tags`（开放标签编辑器）**没有自动验**，两条都在宿主里原生实现，
  这刀只改了调用方式。
- `addPlaylist`（挑选目标播放列表）要弹对话框，没自动验。
- `setBypass` 仍借道原生开关触发的老问题没动（在 DSP 那边）。

## 六、下一步

MainWindow 剩下的域：

1. **标签的在线搜索与歌词编排**（`WebTagTools.cs` 26 KB）—— 有网络依赖，回包可比对。
2. **引擎本体（起播 / 停止 / 无缝接续）** —— 必须真出声，一次只动一块。
3. **`MainWindow.Features.cs`（178 KB）与 `UiTheme.cs`（148 KB）** —— 最大的两块，
   里面混着大量 W13 之后已经死掉的控件操作，需要先甄别再搬。
