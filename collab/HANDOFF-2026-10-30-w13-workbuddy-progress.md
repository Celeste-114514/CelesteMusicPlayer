# W13 交接：WorkBuddy 接手一轮的进度（2026-10-30）

接手时状态：编译发布已完成，但 `IWebPlayerServices` 那批接口改造**没有回归证据**，
业务仍住在 `MainWindow` 里，审计判定「W13 整体未完成」。

这一轮做了 24 个提交，全部可单独回退。下面按「搬了什么 / 怎么验的 / 没验到什么」列清楚。

## 一、搬出去的业务（都已提交）

| 提交 | 新类型 | 接管什么 | 宿主口子 | 验证方式 |
|---|---|---|---|---|
| `c05d49a` | `WebRecoveryService` | 崩溃恢复卡、上次未正常退出提示、安全模式、诊断快照与导出 | `IWebRecoveryHost`（4） | 真实进程回归 |
| `09e32f3` | `WebHomeService` | 主页聚合：最近在听、听歌统计、最近专辑、活跃热力图、某日历史 | 3 支委托 | 真实进程回归 |
| `a971b15` | `WebLibraryService` + `WebTrackIdentity` | 曲目 id、曲目快照（本地+网络）、专辑分组、文件夹节点登记簿 | 1 支委托 | 回包逐字段比对 + 逻辑逐行核对 |
| `bd24b68` | `WebNowPlayingService` | 正在播放快照、频谱/波形/立体声、采集闸门 | `INowPlayingHost`（8） | 真实进程回归 |
| `cb2434b` | `PlaybackQueue` | 队列曲子 + 播放下标 + 网页修订号（279 处引用改指向） | — | 真实进程回归 |
| `f58581e` | `WebQueuePreview` | 队列「上一首/下一首」预览 | — | 回包 0 差异 |
| `548aac5` | `WebAudioChainService` | 音频链面板描述 + 极客读数 | 1 支委托 | 回包 0 差异 |
| `22720cb` | `PlaybackJournalService` | 播放统计、播放历史、Last.fm 回传 | `IPlaybackJournalHost`（8） | **实听**：历史 114→121、秒数累积 |
| `1adf7db` | `MediaSessionService` | SMTC 静默宿主、元数据/封面、时间轴、媒体键分发 | `IMediaSessionHost`（8） | **实听**：占位源/元数据/封面 332KB/timeline 有日志 |
| `68033a0` | `OutputModeService` | 输出模式字符串→引擎枚举、链路记账、下发 | `IOutputModeHost`（2） | 回包 0 差异 |
| `f9be68c` | `LibraryAdvanceRule` | 队列为空时库内「下一首」决策（纯函数） | — | **14 条用例穷举** |
| `4dc2861` | （给 `PlaybackOrderResolver` 补覆盖） | 下一首/上一首/随机解析 | — | **24 条检查 + 全量边界扫描** |
| `dacee81` | `WebUpdateService` | 更新检查/下载/取消/安装转发 + 26 字段状态翻译 | `IUpdateHost`（4） | 回包 26 字段齐全 |
| `dae9078` | `WebSettingsMisc`（静态） | 缓存统计/清理、文件关联、清「最近播放」标记、关于信息 | 无（原本就是静态） | 回包 about + 74 defs 齐全 |
| `c00d59d` | `HotkeysService` | 全局热键读写、校验、恢复默认 | 1 个回调（重启注册） | 回包四字段 + 11 条默认绑定 |
| `e399b65` | `WebSettingsService` + `ISettingsHost` | 设置域剩余 12 个操作（`patch`/`credentials`/`webdav`/`folders`/三个选择器/`export`/`import`/`miniStyles`/`reset`） | `ISettingsHost`（4） | 新增设置域探针，12 项检查全过 |

顺带修了两个既有缺陷：

- `8471afd` 主窗口最大化盖住任务栏、聚焦时多出 1px 系统边线（未提交的旧改动，补交）
- `ac11abc` 累计收听秒数恒 0：采样挂在旧界面定时器上（引擎播放不跑）+ 判据问的是 MediaPlayer

## 二、当前口径

`IWebPlayerServices` **47 条成员**（原 48，`WebTrackId` 已移出边界），**15 条**走独立服务，32 条仍直连 MainWindow。
设置域已经是「薄壳 + 服务」的形态：`WebSettingsAsync` 只剩一行转发到 `WebSettingsService`，
17 个命令没有一个还住在 MainWindow 里。

播放域能安全拆的部分**基本拆完**：投影、队列、台账、媒体控件、输出模式、续播规则。
剩下的是引擎本体（起播/停止/无缝接续）——那部分必须出声才能验，风险陡增。

## 三、验证工具（都在 `tools/`）

| 工具 | 用途 |
|---|---|
| `W13InterfaceRegression.cjs` | 真实进程 + CDP 跑 13 条命令，冷启动与重载各一轮，查失败数与 JS 异常 |
| `W13WireProbe.cjs` | 采七组命令的**原始回包**到 JSON，用于改动前后逐字段比对 |
| `W13PlaybackLedgerProbe.cjs` | 起播两首并退出，用于台账/媒体控件类改动（会出声） |
| `PlaybackAdvanceRegression/` | 库内续播规则用例（14 条） |
| `PlaybackOrderRegression/` | 顺序解析器用例（24 条 + 边界扫描） |
| `W13SettingsOpsProbe.cjs` | 设置域逐操作探针：`get`/`patch`（含冲突分支）/`reset`（改写→重置→还原）/`folders`/`webdav`/`credentials`/`hotkeys`/`miniStyles` + 5 条拒绝路径 |

跑法：`NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13InterfaceRegression.cjs [exe]`
设置域：`NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13SettingsOpsProbe.cjs [exe]`
（不传 exe 默认跑 publish；验收时显式传 `dist\web-ui-20261028\CelesteMusicPlayer.exe`）

## 四、没验到的（必须写清）

1. **文件夹节点登记簿的 children/songs 分支**：本机没配监视文件夹，`folders roots` 返回 0 节点，探测直接跳过。
   只有「逻辑逐行一致 + 编译通过」兜底。
2. **SMTC 的媒体键与系统浮层按钮**、设置里关 SMTC、零散窗接管让出：都没实际按过。
3. **输出模式真实切换**：`OutputMode` 在设置页只读，无法用命令驱动，只证明默认路径不变。
4. **队列为空时的「下一首」**：规则有用例，但真实播放路径没驱动到（探针的 next 命令不生效）。
5. 未播放音乐验证 Last.fm 回传；未做干净机验收。
6. **设置域的 5 个对话框操作没跑过**：`pickFolder` / `pickFile` / `pickLyricFont` / `export` / `import`
   都要弹原生选择器，脚本没有入口（项目禁 UIA 点击冒烟），只证明代码原样搬动 + 编译通过。
   其中 `export`/`import` 还会覆盖整份设置，属于必须人手点一次的项。

## 五、踩过的坑（已写进项目记忆）

- 判断编译是否真过：数 `error CS`，别信末尾「N 个错误」——Roslyn 进程崩时也会结算出几十个「错误」。
- 读 `library.db` 要注意 **WAL**：新行先写 `-wal`，只读主库文件会看到旧条数（我因此误判过「台账一条没写」）。
- `StartupLog` 按调用方文件名分流：`PlaybackJournalService` 的日志进 `logs/audio/`，不在 `logs/app/`。
- `track-stats.json` 的 `LastPlayedUtc` 是 **UTC**，按本地日期过滤会得到 0 条。
- `PlaybackOrderResolver` 里「n=1 且下标 -1 时一个返 0、一个返 -1」是**刻意**的，别统一。

## 六、下一步建议

1. 引擎本体（`PlayExtendedWithEngineAsync` 起播链、`StopEngineIfActive`、无缝接续）：建议一次只动一块，改完实听。
2. ~~设置域：需要先抽 `ISettingsHost`~~ **已做完**（`e399b65`）。做法与结论：
   - 那 12 个操作「卡在窗口句柄 / UI 线程」这个判断只对了一半 —— 句柄由静态的
     `NativeMainWebWindow.HandleOf(window)` 从调用方窗口取，服务自己就能弹对话框，
     不需要宿主转发；真正非宿主不可的只有四条：曲库快照、回 UI 线程、落地改动、重启目录监视。
   - `ISettingsHost` 就这四条，**别往里面加 MainWindow 的属性**。服务一旦能顺手读宿主状态，
     就会重新长回宿主身上，等于白搬。
   - 设置修订号与写入闸门跟着搬成服务里的进程级静态，语义不变（网页靠它判断表单过期）。
   - 设置域现在的形态：`MainWindow.WebSettings.cs` 一行转发 → `WebSettingsService`（17 命令）
     + `MainWindow.SettingsHost.cs`（4）→ 副作用仍走原生那套 `ApplySettingsLive` 等入口。
3. 干净机验收：本机之外跑一次，确认没有隐式依赖用户环境。
