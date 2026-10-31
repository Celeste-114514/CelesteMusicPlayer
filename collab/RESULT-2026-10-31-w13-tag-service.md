# W13：标签页业务搬出 MainWindow（2026-10-31）

## 搬了什么

`tags` 一条命令原来横跨两个 MainWindow 分片：

| 文件 | 改前 | 改后 |
|---|---|---|
| `MainWindow.WebTags.cs` | 215 行（list / read / write + 转发） | 56 行（转发 + 宿主） |
| `MainWindow.WebTagTools.cs` | 381 行（在线搜索 / 歌词 / 改名 / 封面 / 批量工具） | **删除** |
| `WebTagService.cs` | — | 624 行（新建） |

三层分工，各管一段：

- `WebTagService` —— 命令分发 + **会话态**（在线搜索 id、候选缓存、歌词文本缓存、批量下载进度）。
- `WebTagFileService`（2026-10-30 已搬）—— 单文件读写 + 整组写事务（快照 → 全量校验 → 落盘 → 失败整组还原）。
- `TagEditorService` —— 容器格式本身（原样不动）。

会话态必须留在实例里：`searchId` 是「结果已过期」的判据（网页换了候选、旧回包晚到都得拒），
批量歌词下载要跨请求查进度。所以服务是**单例**（`LazyInitializer.EnsureInitialized`），不是每次 new。

## 宿主只给了五样

```csharp
internal interface IWebTagHost
{
    PlaybackQueue Queue { get; }              // 改名后要就地改路径、再落盘
    IReadOnlyList<PlaylistItem> VisibleRows { get; } // 界面绑定集合（_playlist）
    string? NowPlayingPath { get; }           // 播放中禁止改名
    void SavePlayQueue();
    void OnTagsSaved(string path);            // 回 UI 线程刷新元数据
}
```

其余全部走静态入口，**一个都没往宿主加**：
`MainWindow.PickerHostHandle()`（本来就是 static）、`MainWindow.InvalidateCoverCache()`
（本来就是 static）、`WebTrackIdentity`/`TrackStatsStore`/`LibraryDb`/`NamedPlaylistStore` 都是静态存储。
`CreatePlaylistItemFromPath` 只是把 `private static` 改成 `internal static`，没有挪位置。

## 验证

新探针 `tools/W13TagProbe.cjs`，同一份测试现场（`tools/_w13_tag_reset.py` 复位）
分别跑改动前后的可执行文件，**整份回包逐字段比对：0 差异**（8,111 字符）。

覆盖面（全部真实数值，不是「没报错」）：

- `list`：工作集顺序与字段、查询过滤（`Artio` → 1 首）、分页（offset=2 → Aventia/Belenos）、
  未知 id 被丢弃、空工作集、全库口径。
- `read`：两首完整字段（含 `composer`、`trackCount`、`lyrics` 正文、`sampleRate`、`fileSize`），
  封面只比长度桶（726,699 字符 base64）。
- `write`：单组字段写 1 首 → 回读 `PROBE TITLE / PROBE ARTIST / 1999 / 7`；
  逐行字段写 2 首；坏值 `year=abc` 被拒且整组还原；空字段 / 2001 首被拒。
- `batchTransform`：replace / upper / lower / titleCase / fromFilename / clearAll 六种预览逐行比对。
- `rename`：非法名、空名、重名、失效 id、空行五条拒绝路径；真改一次 → 列表里旧名消失 →
  用回包 `remap` 的新 id 改回来，`count` 都是 1。
- `cover`：读（有封面）→ `coverRemove` → 读（空）→ 再删一次（仍然 ok）→ 未知操作被拒。
- 在线 / 歌词只测不联网的拒绝路径（空条件、过期 searchId、未知操作）；
  批量歌词测状态形状、停止、以及没配歌词文件夹时的拒绝。
- `lyricBatchStatus` 形状、`lyricBatchStop` 回包。

旁证：

- 接口回归：冷启动 + 重载各 4041 首，0 失败、0 JS 错误、关窗进程退出 0（bin 与 publish 各跑一遍）。
- 曲线回归：`PASS 164 total EQ, delivery and parameter checks`。
- 全量重建 `-t:Rebuild`：0 个 `error CS`。

## 测试现场怎么隔离

标签这条路会**真写盘**（改标签、改名、删封面），所以必须用复制出来的素材：

1. `tools/_w13_tag_reset.py` 把 6 首 m4a 从 `~\_w13tagfix_pristine` 复制回 `~\_w13tagfix`，
   还原 `library.db`，并把 `LibraryWatchFolders` 指向测试目录。
2. 跑探针（会真实修改这 6 个副本）。
3. 跑完把 `library.db` / `app-settings.json` 从 `_w13bak/tags/` 还原 —— 曲库里 4,106 首、
   `LibraryWatchFolders=[]` 回到原样，测试行 0 条残留。

## 已知未覆盖

- `tags` 的 `addFiles` / `addFolder` / `changeDirectory` 需要弹系统选择框，探针跑不了，
  只能靠代码逐行比对（`AddFilesAsync` 是原方法整体搬运，仅参数名 `host`→`window` 避免与构造函数参数撞名）。
- `coverReplace` / `coverExport` 同样要弹选择框，未覆盖。
- 在线搜索的**成功路径**没测（要走网络，回包不稳定），只测了拒绝路径。

## 出货

- 出货 DLL `CC2DDF3F…` 3,087,360 字节，已同步 `dist/web-ui-20261028`（`BAD=0 STALE=0`）。
- 回退件：`dist/web-ui-20261028-pre-w13-tags-20261031/`（`90A32E34…`，3,086,336 字节）。
