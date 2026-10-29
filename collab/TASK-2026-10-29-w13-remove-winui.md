# W13 移除正式项目WinUI依赖

状态：开始清理。当前阻塞面不是主网页或迷你网页，而是隐藏MainWindow业务适配器与原生服务对其依赖。W7/10已迁窗口与DWM；本次修复迷你迁为原生宿主，为W13移掉一处活动WinUI视图。

必须分步：1）任务栏图标渲染改无XAML，迁出原生HWND辅助视图与调度；2）将WebShellWindow的MainWindow owner替换业务接口/服务实现，逐域保持设置、标签、曲库、播放功能；3）由Core正式PlaybackSession接管队列/DSP/输出/SMTC/历史，不依赖控件；4）生产入口改原生消息循环，删除隐藏MainWindow构造；5）项目去UseWinUI/AppSDK/XAML编译，完整自包含干净机验收。

不得直接删除包引用制造缺功能版本，不把现有受控PlaybackSession当作正式全功能会话。用户要求验证不抢窗口、不播放声音；实际声卡测试需避免干扰其现有工作。

## 第一小步已交付

迷你宿主已迁原生（4200839）；任务栏按钮图标已改NativeTaskbarIconFactory，无XAML Canvas/FontIcon/RenderTargetBitmap活动依赖，600图标创建销毁回归通过。实际生产进程两轮后台回归（含reload）通过，原生主窗与按钮同HWND、曲库4095首、封面与设置正常。

本机一次背景启动停在旧MainWindow首激活阶段，另一次Node出现V8分配失败；未据此当作产品通过。清理本轮测试进程后，按仓库既有单核心进程规避方式启动，最终后台回归实跑通过。没有给日常程序加CPU亲和性限制。

下一步：WebShellWindow对MainWindow的业务接口拆分；正式PlaybackSession及应用生命周期接管后，才能删UseWinUI。整个W13尚未完成。

## 2026-10-30 第二步已交付

外观业务已迁为独立WebAppearanceService，正式网页直接调用；MainWindow仅保留兼容代理。完整外观浏览器+C#集成和生产跨进程回归通过。整体W13仍未完成，下一步为其他设置/曲库业务和正式会话迁出。

## 2026-10-30 第三步

零散窗口迁为原生宿主，修复其WinUI WebView初始化Core为空导致黑屏；独立冷启动关闭退出、共存关闭不影响主程序均已后台验证。主/迷你/零散三种活动网页宿主已迁完；隐藏MainWindow业务、AppSDK窗口调度依赖仍待后续迁出。


## 2026-10-30 编译与发布收尾（最新）

296 个剩余错误已清零。Release/x64 全量 Rebuild：0 错误、554 警告；自包含 publish、依赖扫描、原生回归与日常目录后台验证通过。4096 首曲库、设置/DSP/标签/队列、网页重载、迷你展开/收起/关闭、零散独立启动/关闭、正常退出均已验证；DSP 修改落盘后已恢复原值。未播放音乐，未作实声卡或干净机验收。

当前日常目录已同步，回退目录为 dist/web-ui-20261028-pre-w13-20261030。业务状态兼容层仍保留；同名 Window 是本项目管理原生 HWND 的适配类，不是 WinUI 窗口。不能将去运行时依赖表述成全部业务拆分已经完成。详见 RESULT-2026-10-30-w13-build-verified.md。

## 2026-10-30 WorkBuddy 接手（最新）

接手时先按审计口径把「接口改造没回归过」这条补上了：真实进程（自包含 publish）+ CDP，
冷启动与页面重载各一轮，12 条网页命令全部返回真实数据、无 JS 异常、关闭后进程 exit 0。
工具 `tools/W13InterfaceRegression.cjs`，结果 0 失败。

然后把两块业务**真正**搬出 MainWindow（不是加转发层）：

| 提交 | 搬了什么 | 形态 |
|---|---|---|
| `c05d49a` | 恢复卡（页面崩溃 / 上次没正常退出）、安全模式、诊断快照与导出 | `WebRecoveryService` + 四成员宿主接口 `IWebRecoveryHost` |
| `09e32f3` | 网页主页聚合（最近在听、听歌统计、最近专辑、活跃热力图、某日历史） | `WebHomeService`，曲库快照用三支委托注入 |

进度：`LegacyWebPlayerServices` 48 个成员里，6 个已走独立服务，**42 个仍直连 MainWindow**。

编译：Release/x64 全量 Rebuild 0 错误、555 警告；自包含 publish 成功；上面两次改动各跑了一遍回归，均 0 失败。

### 还没做的（按建议顺序）

1. **播放会话**（Playback.cs/Playback2/3/4 约 9600 行）—— 最重的一块，正式 `PlaybackSession` 接管队列/DSP/输出/SMTC/历史。
2. **曲库数据域**（Library/Library2/WebBrowse/WebCollections/WebFolders 等）—— 建议先做，因为播放会话要读它。
3. **DSP 域** —— 现在仍靠 Slider/ToggleSwitch 替身存状态，要换成真正的参数模型。
4. **设置域**（WebSettings 792 行）—— 已经有 `WebSettingsSchema` 单一真相源，可独立搬。
5. `App.xaml.cs` 仍 `new MainWindow()`；`MainWindow.WebDav.cs` 仍继承本地 Window 替身（创建隐藏 HWND）。
6. 干净机验收未做。

### 本轮没动的

- 日常目录 `dist/web-ui-20261028` 仍是 `95ae9e5` 那版，**没有覆盖**（本轮改动是重构，不改用户可见行为；保留已知良好版本做回退）。
- 迷你把 / 零散播放窗口源码未改动。
- 未播放音乐、未做声卡实声验收。

## 2026-10-30 第四块：曲库读模型（`a971b15`）

`WebTrackIdentity`（曲目 id）+ `WebLibraryService`（曲目快照、专辑分组、文件夹节点登记簿）已搬出 MainWindow。
接口成员从 48 减到 47（`WebTrackId` 从边界删除，外壳直接调纯函数）。
进度：47 条里 9 条走独立服务，38 条仍直连。详见 `RESULT-2026-10-30-w13-library-read-model.md`。

⚠ 遗留验证缺口：本机没配监视文件夹，`folders roots` 返回空，节点登记簿的 `children/songs` 分支
这轮只有「逻辑逐行一致 + 编译通过」兜底，没有运行时证据。

下一步：播放会话（Playback.cs/2/3/4，约 9600 行）是最重的一块；它依赖曲库，而曲库读模型已经就绪。

## 2026-10-30 第五块：播放域前两刀（`bd24b68`、`cb2434b`）

- `WebNowPlayingService`：正在播放的两个投影（快照 / 频谱与立体声）+ 立体声采集闸门。宿主留 `INowPlayingHost` 八个事实。
- `PlaybackQueue`：队列从两个裸字段提升成类型（曲子 / 索引 / 网页修订号），279 处引用改指向它。**只改所有权，没搬调度**。

进度：接口 47 条里 13 条走独立服务，34 条仍直连。详见 `RESULT-2026-10-30-w13-playback-first-cut.md`。

下一步：播放会话本体（选曲 / 随机循环 / 插入规则 / 引擎生命周期 / SMTC / 播放历史）抽成 `PlaybackSession`。
⚠ 本轮没驱动过 `queue play`/`queue move`（会出声），修订号只做了代码对齐，没有运行时证据。

## 2026-10-30 第六块：队列预览 + 音频链投影（`f58581e`、`548aac5`）

两块纯函数搬移：`WebQueuePreview`（上一首/下一首预览）、`WebAudioChainService`（音频链面板 + 极客读数）。
验证方式升级为**改动前后真实回包逐字段比对**，七组命令两次均 0 差异。详见
`RESULT-2026-10-30-w13-queue-preview-and-audio-chain.md`。

进度：接口 47 条里 15 条走独立服务，32 条仍直连。

⚠ 播放域剩下的引擎/会话部分（起播、无缝接续、SMTC、播放历史、输出切换）**必须出声才能验**，
不能再用回包比对兜底，需要用户配合实听。

## 2026-10-30 第七块：系统媒体控件 + 输出模式（`1adf7db`、`68033a0`）

- `MediaSessionService`：SMTC 静默宿主、元数据/封面、时间轴、媒体键分发。实放验证：占位源/元数据/封面 332KB/timeline 全部有日志。
- `OutputModeService`：设置字符串 → 引擎枚举、链路记账、下发；未知值一律回落共享（手改坏设置也不会哑）。
- 另修既有缺陷 `ListenSeconds` 恒 0（`ac11abc`）：采样挂错地方 + 判据问错对象。

进度：47 条接口成员里 **15 条** 走独立服务。播放域剩余：引擎生命周期、无缝接续、输出设备切换（非模式）。

⚠ 未实测：媒体键/系统浮层按钮、设置里关 SMTC、零散窗接管让出、真正的模式切换（OutputMode 设置页只读，无法通过回包驱动）。

## 2026-10-30 第八块：库内续播规则（`f9be68c`）

`AdvanceInLibraryPlaylist` 里那个 switch（顺序/循环/单曲/随机各自的走向）抽成纯函数
`LibraryAdvanceRule.NextIndex`。**这是播放域第一块有正经用例覆盖的逻辑**：
`tools/PlaybackAdvanceRegression` 14 条用例全过（含随机撞当前要顺延、末尾随机回卷、空列表、无在播下标）。

价值不在行数，在于：以前验证「随机播放会不会连播同一首」只能靠真放歌，现在编译期就能穷举。
后续同类规则（队列内跳转、上一首回卷）照这个模式抽。

⚠ 仍未实测：真实点「下一首」在队列为空（媒体库直放）时是否按新路径走 —— 网页协议的 next 动作在我探针里
没让曲目变化（改动前后都一样，属探针问题，不是本次改动引入）。
