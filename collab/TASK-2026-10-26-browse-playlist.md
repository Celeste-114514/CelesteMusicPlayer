# TASK-2026-10-26 — 曲库分类面板的三个遗留问题（文件夹 / 播放列表）

**给谁的**：Codex
**日期**：2026-10-26
**状态**：WorkBuddy 已完成排查，坐标为自查所得，**请不要重复探索**

---

## 0. 前置：读这三个文件再动手

```
collab/PROJECT-BRIEF.md   ← 项目速览 + 网页壳已知坑
collab/LOCK.md            ← 改源码前确认持有方
outputs/webview2-shell/   ← ★ 网页源码唯一真身，改 dist 会被构建覆盖
```

2026-10-26 当日 WorkBuddy 已修好三项纯前端问题，**不必再动**：轮盘图标居中、
队列弹窗居中、播放顺序图标换新。详见第 5 节，避免和你的改动冲突。

---

## 1. 任务 A：文件夹分类从盘符开始，且歌曲数统计不对

### 现象
用户往曲库里添加的是一个具体目录（例如某个音乐文件夹）。网页 folders 面板
却从 `D:` 这一层开始逐层展开；显示的"N 首歌曲"数目也不对。

### 根因（已定位）

**根因 1 — 后端返回的是磁盘绝对路径**

`CelesteMusicPlayer/MainWindow.WebBrowse.cs` 第 33 行：

```csharp
id = WebTrackId(item), folder = item.IsRemote ? "远程音乐" : Path.GetDirectoryName(item.FilePath) ?? "未知文件夹",
```

`Path.GetDirectoryName` 返回完整路径（如 `D:\Music\Artist\Album`）。前端逐段切分自然落到盘符。

**曲库根目录其实是有存的**，在 `CelesteMusicPlayer/AppSettingsStore.cs` 第 341 行：

```csharp
public List<string> LibraryWatchFolders { get; set; } = new();
```

它的用法范例在 `CelesteMusicPlayer/MainWindow.Features.cs` 第 303–305 行（建议直接照抄那里的取法）：

```csharp
if (settings.LibraryWatchFolders != null)
    folders.AddRange(settings.LibraryWatchFolders.Where(Directory.Exists));
```

### ⚠ 更正（用户 2026-10-26 当面纠正，以此为准，覆盖上面所有旧说法）

**歌曲数量是对的，不要碰它。** 用户实测确认：面板显示的 4031 首，正是他加入曲库的那个文件夹的总数，
数字本身没有问题。之前"数目不对"属于误判。

因此：

- **不要改 `browse.js:40` 的计数逻辑**，也不要去动 `split('/')` 那一句 ——
  动了只会把本来正确的数字改坏。
- **这一个任务要解决的只有一件事：层级显示的起点不对。**
  现在是从 `D:` 盘符开始一层层往下钻，而用户往曲库里添加的是一个**具体的音乐文件夹**，
  面板最顶层应当就是那个文件夹。

### 建议方向（只针对起点）

后端把每首歌的 `folder` 改成**相对于用户根目录**的路径。命中某个根目录就用相对部分，
一个都没命中再给个兜底名称。**前端除了层级起点之外不要改。**

请给出前后端两侧的具体改法 + 落点。

---

## 2. 任务 B：播放列表没有用封面图

### 现状
`browse.js` 第 45 行渲染播放列表卡片时，图标位置写死了字符 `'♫'`，没有任何图片：

```js
...forEach(item=>card(item.name,item.trackIds.length+' 首歌曲'+...,'♫',()=>{playlist=item;limit=200;render();}));
```

### 已查明的事实（省得你翻）

- 播放列表的数据结构里**没有封面字段**：`CelesteMusicPlayer/NamedPlaylistStore.cs` 第 7 行起的
  `NamedPlaylistDto` 只有 `Name`（和歌曲路径），所以要显示封面必须新增字段 + 存储。
- 后端给网页发封面**已经有现成通道**（虚拟主机名，见 `WebShellWindow.cs` 第 72 行），
  曲目封面是这么发出去的 —— `MainWindow.WebShell.cs` 第 104 行：

  ```csharp
  coverUrl = item == null ? "" : "https://celeste-cover.internal/cover/" + WebTrackId(item),
  ```

### 请给出两个方案让用户拍板（用户不懂编程，用大白话写）

- **方案 A（改动小）**：列表封面直接取列表里第一首歌的封面。**纯前端改动**，后端一行不用动。
- **方案 B（完整）**：支持用户给播放列表单独指定封面图 —— 需要新增字段、持久化、
  以及一条新的封面 URL 路由。

用户原话是"没有用用户自定义的图片，**改成和原 UI 上一致行为即可**"。
**请先去确认原生界面（旧 UI）里播放列表封面究竟是怎么来的** —— 是自动取第一首歌，
还是能手工设置。以原生行为为准，这点用户明确要求了。

---

## 3. 任务 C：播放列表浏览页 / 详情页缺按钮

### 现状
网页版 `browse-page` 只放了一套通用按钮（播放全部 / 排序 / 升序 / 刷新），
**原 UI 的专有按钮一个都没有**。

### 原生界面的按钮清单（用户给的截图，照这个来）

**播放列表浏览页**（3 个）：
```
新建播放列表（主按钮，粉色实心）  |  导入 M3U...  |  打开 CUE...
```

**播放列表详情页**（5 个）：
```
播放该列表（主按钮）| 添加至播放队列 | 添加到播放列表 | 导出为 m3u8 | 排序
```

### 坐标

- 按钮要加在 `outputs/webview2-shell/index.html` 第 120 行的 `#browse-page` 里，
  现在的结构是 `<div class="albums-heading"><div>…标题…</div><div class="album-sort-actions">…</div></div>`
- 逻辑挂在 `outputs/webview2-shell/browse.js`（全文只有 85 行，很好读）
  - 第 32 行的 `render()` 里用 `$('playlist-create').hidden = ...` 这类方式控制显隐，照这个写法扩展
  - 第 80 行是现有唯一的提交类操作（创建播放列表），可作为后端调用的写法范本

### 后端要新增的操作

现有 `browse` 操作只有 `get` / `createPlaylist`（见 `MainWindow.WebBrowse.cs` 第 11–21 行）。

**好消息**：m3u 读写后端已经有现成实现，可以直接复用 —— `CelesteMusicPlayer/M3uPlaylistIO.cs`。

需要在 `MainWindow.WebBrowse.cs` 里增加操作（例如 `addToQueue` / `addToPlaylist` / `exportPlaylist` /
`importM3u` / `openCue`），并在前端调用。

⚠ 注意 store 层的 API 只有这些（`NamedPlaylistStore.cs`）：
`List()` / `Create()` / `Rename()` / `Delete()` / `LoadSongs()` / `SaveSongs()`。
需要的操作如果超出这个集合，说明要在 store 层先补能力。

---

## 4. 约束（务必遵守）

1. **只改 `outputs/webview2-shell/`**。`dist/*/WebShell/` 是构建产物，改了会被覆盖。
2. **改动必须是懂情况下的**——用户对"能回滚"有安全感，commit 要小而独立。
3. 用户**不懂编程**：给他的方案要用大白话，不要堆术语。
4. 原生界面的行为是**唯一准绳**（用户多次强调"和原 UI 一致"）。
5. `AppSettingsStore.Clone()` 是逐字段显式拷贝 —— 如果你要加设置项，**忘改这里会导致
   "存得进文件但读出来永远是默认值"**，这个坑本项目已经踩过五次。

---

## 5. WorkBuddy 当日已改好的部分（不要重复做）

| 位置 | 改了什么 |
|---|---|
| `shell.js` 环半径 | 改回 **150 / 238** —— 让两圈图标各自居中落在相邻轨道线中间（见下） |
| `index.html` | 新增 5 个播放顺序图标 symbol：`i-order-sequential / listloop / trackloop / once / random` |
| `shell.js` 第 245 行 | 播放顺序按模式映射图标，去掉了原来那个丑陋的 "1" 角标 |
| `transport-panels.css` 第 2 行 | 队列弹窗改为视口居中（`left:50%;top:50%;translate:-50% -50%`） |

**轨道线半径是不可随意改的约束**，写进注释了：
中心所在地 `index.html` 画了三条弧线，半径 **105（core）/ 195（inner）/ 280（edge）**。
图标环必须落在所在区间的中点：`(105+195)/2 = 150`、`(195+280)/2 ≈ 238`。
改半径就必须同步改弧线，否则图标会偏离中线。

---

## 6. 要什么产出

对 **A、B、C 三项分别**给出：

1. 结论 / 方案（B 项要两个方案对比）
2. **前后端两份落点**：`文件:行号` + 可直接替换的片段
3. 影响面：会不会动到公共结构、会不会破坏现有面板
4. 怎么验证（用户能照着点一遍的步骤）

工作量大就**按 A → B → C 的顺序**分次交付，不要一次性塞一大坨。
