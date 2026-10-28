# W6 收尾报告 · 2026-10-29

接 `HANDOFF-2026-10-29-W6-and-backlog.md`。本轮由 WorkBuddy 接手，把 W6 剩下的部分做完并提交。

## 提交线（main，HEAD = bed920f）

| 提交 | 内容 |
|---|---|
| `37b23bc` | `w6(cover)` 封面统一走 SkiaSharp 缩略图缓存（含网页壳接入） |
| `83ddaa5` | `w6(model)` PlaylistItem 从 MainWindow.xaml.cs 拆出（数据/界面两半） |
| `929370b` | `w6(playback)` 播放脉冲与受控会话可从无 WinUI 的核心调度 |
| `e50b054` | `w6(core)` 新增 CelesteMusicPlayer.Core（源码链接、不引用 WinUI/MainWindow） |
| `bed920f` | `w6(test)` 修两个真失败 + 自检不再吞掉带备注的失败项 |

提交顺序按依赖排：封面 → 模型 → 播放 → 核心 → 测试，每个提交都能单独回退。
**干净 worktree 验证过：`git worktree add HEAD` 后整包 Release publish 通过（RC=0，0 错误）**，
说明这五个提交自洽，别人克隆能构建。

## 交接里两个阻塞项：都不是断言写错

### 1. `cue.parse = FAIL`

`CueSourceParser` 只认 `TITLE "x"`，而真实 CUE 里有相当一批整轨抓轨工具不写引号
（`TITLE x`）。自检样本正是这种写法，于是 title/artist 全空。
改为两种写法都认（带引号取引号内，不带取到行尾）。生产调用方
（`NetworkLibraryService.BuildTracksAsync` 解析网络中继的 .cue）因此也更宽容。

### 2. `purge` 清掉 0 条

不是路径没写进库。真因是**比对口径不一致**：

- `LibraryPathGuard` 比较的是 `Path.GetFullPath` 规范化后的反斜杠路径；
- `PurgeInternalCacheTracks` 直接对原始 `file_path` 做 SQL `LIKE`；
- 缓存根目录来自 `Environment.GetFolderPath`，某些环境返回 `C:/Users/...`，
  库里就存成正斜杠 → 这条永远删不掉，**缓存文件会永久留在用户曲库里**。

修法：`DELETE ... WHERE REPLACE(file_path,'/','\') LIKE $p ESCAPE '\'`，与 guard 同口径，
兼容历史数据（不需要迁移库）。自检加了"正斜杠路径也要被清掉"的用例。

## 顺手挖出来的一个更严重问题：自检会吞掉失败

四个 boundary 的判定原本是 `details.All(d => !d.EndsWith("= FAIL"))`，
而 `Check()` 的备注格式是 `name = FAIL（备注）` —— **带备注的失败行 EndsWith 为 false，
被当成通过**。交接那次"看起来只挂了 cue.parse"就是这么来的，实际同时挂着 `purge`。
已全部改成 `Contains("= FAIL")`。

## 验证矩阵（全部本轮实跑）

| 项目 | 结果 |
|---|---|
| 薄宿主 `--self-test` | 退出码 0；settings 64 / library 30 / parsing 10 / services 13 / cover 59，**FAIL 行 0** |
| `tools/TagEditTests` | 44 项全过（原来编译失败：Core 缺 `TagColumnLayout.cs`，已补） |
| `tools/CoreBoundaryRegression` | 全过，无声卡接收端跑完位置时钟/暂停/恢复/跳转/停止/重开/两首队列切换 |
| `tools/NetworkRegression` | 7 个 provider 全过（webdav/jellyfin/emby/subsonic/baidu/smb/sshfs 本地夹具） |
| `tools/CoverCacheRegression` | 10 项全过（JPEG/WebP 非正方形、20 并发、命中不重写、小图不放大、坏图回退、无临时残留） |
| 主工程 Release publish | RC=0，pri 1.74MB、exe 309KB（自包含启动器）、SkiaSharp + libSkiaSharp 就位 |
| 实机启动 | 曲库 4031 首 / 578 专辑 / 344 艺术家，封面全部正常，无崩溃 |
| 实机播放 | WASAPI 共享 · 16bit/44.1kHz，进度 2:37→2:46 推进，界面转"正在播放" |

日志里本轮启动只有两条既存噪音（不是本次引入）：`App.PrimaryLanguageOverride`
在非打包运行下抛 InvalidOperationException（有 try/catch，不影响启动）、
`[thumb] 渲染结果为全透明`（任务栏缩略图渲染兜底）。

## 封面这件事的现场结论

- 网页壳原来**不走** `CoverArtService`，而用 GDI+ 现做方图：`new Bitmap(image, size, size)`
  会把非正方形封面拉变形，GDI+ 也不认 WebP（这类封面直接 404 显示占位）。
- 现在网页壳的 `/cover/{id}?size=` 走 `CoverCacheService`：160 → Thumb 160，480/768 → Large 768，
  不新增档位、缓存版本仍为 v2（用户已有 938 张缓存继续命中），等比缩放不再变形，WebP 可显示。
- 图像引擎选型踩坑记录：ImageSharp 3.x/4.x 构建期强制校验 Six Labors 商业许可（缺 license
  编不过），2.1.13 虽是 Apache-2.0 但带已知高危漏洞（NU1903）。最终用 SkiaSharp 3.119.4（MIT）。
- 真实封面（3456×3456）新旧引擎对比：尺寸完全一致（320×320），新引擎边缘更干净，无变形无偏色。

## 运行版状态

`dist/web-ui-20261028` 已替换为**HEAD 构建产物**（dll / pri 哈希与干净 worktree 构建一致），
备份在：

- `dist/_prev_20261029-w6/`：本轮开始前的版本（02:29，含封面第 2 步、不含 codex 的 W6 改动）
- `dist/_prev_20261029-w6wip/`：含迷你播放器 WIP 的中间版本（03:07）

回滚方法：把对应目录里的 dll/deps.json/pri（必要时加 exe/runtimeconfig）复制回
`dist/web-ui-20261028` 即可，不需要重新构建。程序需要**完全退出再启动**才会载入新 DLL。

## 故意没提交的东西

迷你播放器样式包 WIP（交接第 3 节列的那批）：

- `CelesteMusicPlayer/MainWindow.MiniStyles.cs`（未跟踪）
- `CelesteMusicPlayer/MiniPlayerStyles.cs`（未跟踪）
- `CelesteMusicPlayer/MainWindow.WebSettings.cs`、`MiniPlayerBridge.cs`、
  `WebMiniPlayerWindow.cs`、`WebSettingsSchema.cs`（已跟踪，改动未提交）

已核对：**HEAD 里没有任何文件引用这些类型**，所以干净构建不受影响。
按交接要求，W6 不带这批半成品发布。

## W6 的边界（别当成 W7）

- 核心是**源码链接**编出来的程序集，不是独立项目引用；正式应用仍是原宿主。
- `PlaybackSession` 是受控测试可用的过渡实现，**尚未接管正式 MainWindow 播放**。
- 因此本轮只到"播放/曲库/设置核心不读 UI 控件、无 MainWindow 可运行受控播放检查"，
  W7（正式网页切到新宿主）未开始。

## 下一步（按用户已确认的顺序）

1. 桌面歌词：旧窗口 → WebView2 透明窗口，竖排、字体文件、跟音乐变化，清旧的界面专用设置。
2. 迷你播放器完整样式包（用户制作/导入/选择），把那批 WIP 收口。
3. W7 起：正式宿主迁移；W9 文件关联、W10 四按钮按既有进度继续。
