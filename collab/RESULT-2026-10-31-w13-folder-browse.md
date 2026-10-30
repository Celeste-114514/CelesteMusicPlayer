# W13：目录浏览 + 曲目动作（文件夹侧）搬出 MainWindow

日期：2026-10-31
提交：见本次 commit（父提交 `259aeaa`，方案 A 收官）
回退件：`dist/web-ui-20261028-w13-planA-done-20261031/`（`09E74A41…`）

## 一、为什么先做这一块

MainWindow 里剩下的域有四个：目录浏览（含目录监视）、曲目动作、标签的在线搜索与歌词编排、引擎本体。

挑目录浏览，理由是它**能自动验**：网页侧的 `folders` 命令有完整回包（roots / children / songs + 拒绝路径），
可以像前几刀那样「先采基线、再搬、再逐字段比对」，不用人眼点。引擎本体要真出声才能验，风险最高，放最后。

## 二、做了什么

### 新增 `LibraryFolderScan.cs`（磁盘侧，纯逻辑）

从 `MainWindow.Library.cs` 与 `MainWindow.Playback.cs` 搬出的三件事：

| 成员 | 原来在哪 | 说明 |
|---|---|---|
| `AudioExtensions` | `MainWindow.Playback.cs`（数组字面量） | 受支持扩展名，**改成单一真相源** |
| `AudioFiles` | `EnumerateAudioFiles` | 递归枚举，文件系统顺序 |
| `AudioFilesRecursiveOrdered` | 同名 | 深度优先、按名字排序（界面播放整夹用） |
| `Children` | `EnumerateFolderChildren` | 列一层，隐藏/系统项跳过 |
| `OrderAlbumTracks` | 同名 | 碟号 → 轨道号 → 标题 |

搬法与以前一致：**逐行照抄**，只把 `FolderBrowserItem` 换成不依赖界面的 `FolderScanEntry`
（`DisplayName / FullPath / IsFolder / Depth` 四个字段一模一样）。

原生曲库树和网页目录命令**共用这一份**。两边各写一份的话，迟早一边加了新格式另一边看不到。

### 新增 `WebFolderBrowseService.cs`（服务层，零 UI 零播放）

`folders` 命令里的 roots / children / songs 三段逻辑搬到这里，外部依赖只剩两个委托：

- `Func<IReadOnlyList<PlaylistItem>> libraryTracks` —— 曲库快照（沿用 `WebLibraryService` 的做法）
- `Func<string, PlaylistItem> createFromPath` —— 单曲元数据工厂（TagLib + 网络引用解析仍在宿主）

「挑文件夹」的 `FolderPicker`、以及 `play` / `queue` 的播放入队**留在宿主**：那些本来就是宿主的事。

### `MainWindow.WebFolders.cs` 瘦成壳（154 → 55 行）

只剩命令分发、`FolderPicker`、play/queue 三个动作。

### `WebLibraryService.cs` 节点登记簿改类型

`Dictionary<string, FolderBrowserItem>` → `Dictionary<string, FolderScanEntry>`。
网页拿到的回包字段（`id / name / path / folder`）一个没变。

### `MainWindow.Library.cs` / `MainWindow.Playback.cs` 收口

- 删掉 4 个已搬走的枚举/排序方法（约 290 行）
- `EnumerateFolderChildren` 保留成**类型映射适配器**（原生曲库树还在用界面类型）
- `AudioExtensions` 保留成**别名**指向 `LibraryFolderScan.AudioExtensions`
  （`MainWindow.Misc.cs` / `WebDav.cs` / `WebTagTools.cs` 三个 partial 文件都在用这个名字）
- 所有调用点改指 `LibraryFolderScan.*`

## 三、验证

| 项目 | 结果 |
|---|---|
| 编译发布 | 成功，0 错误 |
| 网页目录回包（有媒体库根） | 与改动前**逐字段 0 差异** |
| 网页目录回包（无媒体库根，回退分支） | 与改动前**逐字段 0 差异** |
| 拒绝路径 | 未知操作 / 旧 nodeId / 对文件节点做 children / epoch 作废 —— 报错文字完全一致 |
| 接口回归 | 冷启动、重载各 4041 首，0 失败、0 JS 错误 |
| 真实播放 | Time → Hi! → Vitamin C → 回到 Time 正常 |
| 声场 + 矩阵旁证复跑 | 0 差异 |

### 探针怎么保证不是空跑

临时把设置里的「媒体库文件夹」指向一支自造测试目录（`work/fold-fixture/`）：

```
fold-fixture/
  Alpha/01 First.wav、02 Second.wav、notes.txt
  Beta/03 Third.wav
  top-level.wav
  readme.txt
```

断言到的实际内容：

- `roots` → 1 个根节点、`total` = 曲库总数
- `children` → 3 项（Alpha、Beta、top-level.wav），`.txt` 被正确过滤
- `songs`（Alpha）→ 2 首，顺序 `01 First` → `02 Second`
- `songs`（整根递归）→ 4 首，顺序 `01 First, 02 Second, 03 Third, top-level`
- 对文件节点做 `children` → `该条目不是文件夹。`
- 旧 nodeId → `目录已刷新，请重新选择。`
- 未知操作 → `The given key was not present in the dictionary.`（旧版就是这样，没改）

跑完把设置还原、把测试曲目从 `library.db` 里删掉（`work/_library.db.w13fold-bak` 是当时的备份）。

## 四、过程中踩到的两个坑

1. **第一次探针是空跑**：曲库是「文件模式」恢复出来的，`LibraryWatchFolders` 是空的，
   `roots` 返回 0 个节点，后面的 children / songs 全被跳过 —— 回包照样「0 差异」，但什么都没测。
   所以补了测试目录，并把「无根回退」单独当一个场景跑。
2. **`AudioExtensions` 别名不能删**：我在 `MainWindow.Playback.cs` 里没搜到用处就删了，
   编译立刻报 4 个 `CS0103` —— 它被 `MainWindow.Misc.cs` / `WebDav.cs` / `WebTagTools.cs` 用着
   （partial 类里私有静态字段跨文件可见）。已恢复。

## 五、没做的

- `folders` 的 `add`（文件夹选择对话框）、`play`、`queue` 三条**没有自动验**：都要真人点或真出声。
  代码路径没改（还在宿主），只是把 `songs` 提前了一步算。
- 目录监视（`LibraryWatchService`）本来就不在 MainWindow 里，这刀没碰。
- 曲目动作里剩下的 play / 删除 / 标签，以及标签的在线搜索与歌词编排，仍在 MainWindow。

## 六、下一步

MainWindow 剩下的域按风险排：

1. **曲目动作（play / 删除 / 标签）** —— 能自动验，沿用这套方法。
2. **标签的在线搜索与歌词编排** —— 有网络依赖，回包可比对。
3. **引擎本体（起播 / 停止 / 无缝接续）** —— 必须真出声，一次只动一块。
