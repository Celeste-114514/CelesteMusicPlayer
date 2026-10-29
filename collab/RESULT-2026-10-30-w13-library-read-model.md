# W13 曲库读模型搬出 MainWindow — 2026-10-30

提交 `a971b15`。

## 结论

网页侧依赖的**曲库读模型**已经从 `MainWindow` 搬出来，分成两个类型：

| 类型 | 负责什么 |
|---|---|
| `WebTrackIdentity` | 曲目在网页协议里的 id（本地曲目 = 路径大写 + 起始秒的 SHA-256；来源曲目 = SourceTrackId）。**全进程只许这一个定义** |
| `WebLibraryService` | 曲目快照（本地 + 已索引/已固定的网络曲目、CUE 片段登记）、专辑分组、文件夹浏览的节点登记簿（含 epoch 作废机制） |

`MainWindow` 只剩一个 `Library` 属性，把「本地曲库」这一支数据源传进去。曲库集合本身的所有权还在 MainWindow，等曲库真正搬出来时把这支委托换成直接持有。

## 迁出的成员（3 条，全部不再是转发）

- `WebAllTracks()` → `WebLibraryService.AllTracks()`
- `WebAlbums(source)` → `WebLibraryService.Albums(source)`
- `WebFolderCoverPath(id)` → `WebLibraryService.CoverPath(id)`

顺带**从边界上删掉一个成员**：`WebTrackId`。网页外壳现在直接调 `WebTrackIdentity.Of(...)`（同一程序集内的纯函数），
接口成员从 48 条减到 47 条。

## 框架内部状态也搬了

文件夹浏览原来靠 `MainWindow` 的三个字段（节点表、曲目表、epoch）维持，现在整套进了服务：
`RegisterNode` / `TryGetNode` / `ResetNodes()` / `RememberTracks()` / `Epoch`。
`MainWindow.WebFolders.cs` 只保留命令处理（挑文件夹对话框、播放、插队列）。

## 验证

| 项 | 结果 |
|---|---|
| Release/x64 全量 Rebuild | 0 个 `error CS`（554 警告，与迁移前一致） |
| 自包含 publish | 成功（第 1 次撞上 NuGet 任务崩溃，用 `_publish_retry_clean.py` 第 2 次过） |
| 真实进程回归（冷启动 + 重载） | PASS，13 条命令全部 ok，无 JS 异常，关闭 exit 0 |
| 回归新增探测 | `folders roots → children`（先取 roots 的 id 再下一层） |
| 逻辑等价性 | 搬走的 `Albums` / `AllTracks` 与 git 原文**逐行比对**：除方法名、`SHA256` 限定写法、`WebTrackId→WebTrackIdentity.Of` 外，语句完全一致 |

## 没验到的（要说清）

- **文件夹节点登记簿的 children/songs 分支这轮没跑起来**：本机没有配置「监视文件夹」，`folders roots` 返回 0 个节点，
  所以 `children` 那条探测直接跳过（脚本里是 `if (firstNode && firstNode.folder)` 才调）。
  它靠「逻辑逐行一致 + 编译通过 + 命令入口仍是同一套」兜底，但不是运行时证据。要在有监视文件夹的机器上补一次。
- 未播放音乐、未做声卡实声验收。
- 未覆盖干净机。

## 进度口径

`IWebPlayerServices` 47 条成员：**9 条已走独立服务**（恢复诊断 5、主页 1、曲库 3），**38 条仍直连 MainWindow**。
按原计划下一个大件是播放会话（约 9600 行），其次是 DSP 与设置。
