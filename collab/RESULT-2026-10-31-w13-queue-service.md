# W13 队列页 / 「更多」菜单搬出 MainWindow

日期：2026-10-31　提交：`6f0df69`

## 一、这次搬了什么

`MainWindow.WebTransport.cs` 里剩下的最后两条「曲目动作」命令：

| 命令 | 原位置 | 现位置 |
|---|---|---|
| `queue`（队列页：列表 / 分页 / 点播 / 拖动换序） | `MainWindow.WebTransport.cs` | `WebQueueService.QueueSnapshot` |
| `more`（更多菜单：复制信息 / 文件夹中显示 / 加入播放列表） | 同上 | `WebQueueService.MoreMenu` |

`MainWindow.WebTransport.cs` 从 258 行降到 163 行，只剩音频设置那一段（`audioSettings` 跟引擎贴得紧，不在这一刀里）。
新增 `MainWindow.WebQueue.cs`（38 行，一行转发 + 宿主实现）。

### 宿主接口 `IWebQueueHost` 只给了六样

1. `Queue` —— 播放队列模型（本来就是独立类型 `PlaybackQueue`，不是窗口的东西）
2. `OrderResolver` —— 播放顺序解析器（队列页要照它算「上一首 / 下一首」预览）
3. `CurrentTrack` —— 正在播的那首（「更多」菜单只对它生效）
4. `PlayQueueIndex` —— 真起播，只有引擎能做
5. `RefreshQueueUi` —— 重编号 / 通知副窗 / 用户播放列表重新过滤
6. `RevealInExplorer` —— 外部程序

**没给**：`_playlist`、`_currentCategory`、`_nowPlayingPath` 这类窗口状态。服务要的快照、摘要、格式列全自己算。

## 二、一处刻意的行为差异（已评估）

原来拖动换序之后是手写三句：

```csharp
RenumberCollection(_queue.Items);
NotifyCurrentPlaylistWindow();
if (_currentCategory == "UserPlaylist") ApplyUserPlaylistSearchFilter();
```

现在改走宿主已有的 `RefreshQueueUi()`，也就是 `WebBrowseActions` 里插入/删除路径一直在用的那个。
它比原来多两句 `PlaylistView.ItemsSource = null; = _queue.Items;`。

`PlaylistView` 是 `MainWindow.LegacyControls.cs` 里的编译占位控件（`new ListView()`，**从未加进视觉树**），
两句重绑是空操作。语义上也更对：重排后本来就该整表重绑。改动前/后回包逐字段一致，就是证据。

## 三、验证：`tools/W13QueueWireProbe.cjs`

做法沿用「先采基线 → 再搬 → 逐字段比对」。覆盖：

- 整份队列快照（`keys` / `revision` / `total` / `offset` / `currentIndex` / `order` / `summary` / `previous` / `next` / 每条 9 个字段）
- 分页：`offset=1` 切片正确、`revision` 不因分页变化、`offset=-5` 与 `offset=999` 都回落成 0
- 拒绝路径 5 条：修订号过期（play / move 各一条）、下标越界、`trackId` 对不上、未知操作
- **真拖动往返**：0→1 再 1→0，顺序复原，`currentIndex` 跟着 0→1→0，`revision` +2
- 「更多」菜单：播放列表清单（2 个）+ 两条不产生副作用的拒绝路径

结果：

| 对比 | 字段差异 |
|---|---|
| 改动后 exe（publish） vs 改动前基线 | **0** |
| 出货目录 exe（dist） vs 改动前基线 | **0** |

队列落盘文件 `play-queue.json` 与测试前**逐字节相同**（拖动只在内存里，复原后无害）。

## 四、⚠ 发布时踩到的坑（比代码本身更值得记）

第一次 publish：`exit=0`、`error CS=0`、DLL **2,981,888 字节**，比上一版少 **106,496 字节**，且
`GenericHelpers` / `VtableClasses` 两个 CsWinRT 符号**都不在**。这是记过的坏产物形态
（`能启动也能跑`，功能测试发现不了）。

重跑 `_publish_retry_clean.py` 两次**仍然是坏的**——因为这个脚本只清 `obj/x64` 与 `bin/x64`，
**不清 `bin/Release`**（真正的 publish 输出在 `bin/Release/<tfm>/win-x64/publish`）。
增量 publish 复用了坏状态。

解法：把 `CelesteMusicPlayer/obj`、`bin/Release`、`bin/x64` **整个**移走再 publish →
3,090,432 字节，两个符号都在。

### 顺带纠正一条旧认知

`UseWinUI=false` + `WinUISDKReferences=false` + Windows App SDK 包已删之后，
**一次干净 publish 只有 269 个文件**，不含 `DirectML.dll` / `CoreMessagingXP.dll` / `dwmcorei.dll` /
172 个 `.mui` / 43 个 `.winmd`。那些是 W13 之前的残留，一直躺在 `bin/Release/.../publish` 和 `dist` 里没被清掉。

所以现在 `_sync_webshell_dist.py` 会报 `STALE=293`。**不是这次改动造成的**，
而是 publish 变干净了、dist 还是老样子。dist 是超集，多出来的文件不参与加载（已用 dist 里的 exe 实跑验证过）。
要不要清 dist 是另一件事，动之前应该先备份整目录。

## 五、交付

- 出货 DLL `ABFEB582…` 3,090,432 字节，已同步 `dist/web-ui-20261028`（`BAD=0`）
- 回退件 `dist/web-ui-20261028-pre-w13-queue-20261031/`（`3FDDC3E1`，3,088,384）
- 用户数据：`play-queue.json` 未变；测试前另存 `_w13bak/queue-20261031/`（queue/stats/library/settings）

## 六、没做的

- 没跑全量接口回归（4041×2）。这次只动了两条命令，且这两条命令的全部回包已逐字段比对过。
- `more` 的三条真实分支里，`copy` / `reveal` / `addToPlaylist` 只验证了「代码原样搬过去」，
  没实跑——它们会改剪贴板 / 弹资源管理器 / 写 library.db，属于会碰用户现场的操作。
- `audioSettings` 仍在 `MainWindow.WebTransport.cs`：它和音频引擎、设备枚举、睡眠定时器贴在一起，
  不是「搬个转发」能解决的，得等引擎本体那一刀。
