# W13 播放域第一步：正在播放投影 + 队列类型化 — 2026-10-30

提交 `bd24b68`（正在播放）、`cb2434b`（队列）。

播放域一共 10.7k 行（Playback.cs 3181 / Playback2 2474 / Playback3 3482 / Playback4 512 等），
整体搬不现实，这轮按「先外面那层、再里面那层」切了两刀。

## 第一刀：正在播放的投影（`bd24b68`）

新增 `WebNowPlayingService`，拿走原来 `MainWindow.WebNowPlaying.cs` 的全部实现：

| 职责 | 内容 |
|---|---|
| `Snapshot()` | 曲目身份 + 采样率/位深/码率/声道/文件大小 + 整首歌词行 + 当前句 |
| `Visual()` | 频谱 140 段、波形 256 点、DSP 之后的左右声道 512 对样本；含换曲代次（generation）与序号（sequence）守卫 |
| `SetStereoSampling(owner, wanted)` | 引用计数的立体声采集闸门（多个页面同时要看，不能一关就把别人关了） |

MainWindow 只留 `INowPlayingHost` 八个事实：是否在播、当前曲目、频谱/波形/立体声取数、开关采集、歌词行、当前句。
接口里 3 条成员改走服务。

## 第二刀：队列从两个裸字段变成类型（`cb2434b`）

`PlaybackQueue` 接管：
- `Items`（原来 `_userPlaylist`）
- `Index`（原来 `_userPlaylistIndex`）
- 网页修订号：`WebSnapshot()` / `Revision` / `Touch()`（原来 `_webQueueSnapshot` + `_webQueueRevision` 两个字段住在 `MainWindow.WebTransport`）

279 处引用被机械改指向队列；5 处整体替换改成 `Replace(...)`。

**这刀只改所有权、没搬调度逻辑**：选下一首、随机/循环、插入规则仍在 MainWindow。
diff 是 +236 / −239，基本对消，说明是纯替换而不是顺手改行为。

## 验证

| 项 | 结果 |
|---|---|
| 全量 Rebuild | 0 个 `error CS`，554 警告（与改动前一致） |
| 自包含 publish | 两刀各一次，均成功 |
| 真实进程回归（冷启动 + 重载） | 两次都 PASS：13 条命令 ok、无 JS 异常、关闭 exit 0 |
| `nowPlaying` 回包形状 | `audio / currentIndex / lines / lyricState / trackId` —— 与新服务的输出一致 |

### 一处需要说明的实现细节

队列的 `Touch()` 第一版写成「清空快照 + 修订号 +1」，会让拖动后修订号连跳两次（下一次轮询又 +1）。
已改回与旧实现一致：记下当前快照再 +1，所以一次拖动只动一次修订号。

## 没验到的

- `queue play` / `queue move` 没有实际驱动（会让本机开始放音频），只验了 `queue get`；
  修订号语义靠代码对齐保证，不是运行时证据。
- 立体声采集闸门没有实声验证（不播放就没采样）。
- 未做干净机验收。

## 进度

`IWebPlayerServices` 47 条成员：**13 条已走独立服务**（恢复诊断 5、主页 1、曲库 3、正在播放 3、队列修订 1），
**34 条仍直连 MainWindow**。

下一步真正的大件是**播放会话本身**：把「选下一首 / 随机循环 / 插入规则 / 引擎生命周期 / SMTC / 播放历史写入」
从 Playback* 四个文件里抽成 `PlaybackSession`。队列已经是类型，可以承载调度状态了。
