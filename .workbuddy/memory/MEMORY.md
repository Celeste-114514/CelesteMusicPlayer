# CelesteMusicPlayer 项目长期记忆
> 只留跨会话铁律。提交查 git log；当日细节查 memory/YYYY-MM-DD.md；构建/发布坑见 ref-build-publish.md 与 celeste-build-verify skill；DataGrid 见 ref-datagrid-toolkit.md；在线源见 ref-online-sources.md。

## 主线：网页外壳（WebView2）
- **架构**：一个网页一整块界面 + 内部路由，先浏览器调再接 C#。**别走"平行界面架构"老路**（每个原生页在 web 重抄一遍）。
  原型 `outputs/webui-proto/`，正式源 `outputs/webview2-shell/`；交付 `dist/web-shell-dsp-tags-complete/`（改网页只改源目录，再同步才进 dist）。
- **设置页命令 `appSettings`**（`audioSettings` 是底栏音频面板，别混）。字段单一真相源 `WebSettingsSchema.cs`；
  **铁律**：改字段必须同改 Defs/Readers/Writers 三处；patch 按白名单 + revision 冲突检测；密码走 `credentials`（空=保留、`__clear__`=清除）；OutputMode 设置页只读。
- **外观**：`WebAppearance.cs`（22 色+12 数值、24 预设）+ `MainWindow.WebAppearance.cs` + `WebShellWindow.theme` 命令；网页 `theme.css|theme.js|appearance.js`。
  铁律：变量名宿主已带 `--` 别再拼；预览用宿主新算的 bundle 收尾；部件跟语义色用 `var(--shell)`/color-mix **别单独存值**；canvas 画的读变量并在 `celeste:theme-applied` 重绘；深色要 `color-scheme:dark`；数值 override 剥单位再 Parse。
- **DSP 网页**：非 EQ 模块不另建状态，直读写原生 XAML 控件；首次访问 `EnsureWebDspUiReady()`；`VISUAL_OWNED` 表防双渲染。DSP 深色靠 `dsp-theme.css`（最后加载），功能色（EQ 渐变/电平绿/白指针）不跟主题。
- **系统托盘（10-28 起）**：Win32 `Shell_NotifyIcon` + **WebView2 独立菜单窗**（`WindowsTrayIcon`/`TrayMenuWindow`/`WindowsTrayService`/`TrayCommandAdapter`），
  H.NotifyIcon.WinUI 与 AppTrayIcon/TrayMenuBuilder/TrayTheme 已删。降级只到系统原生 `TrackPopupMenu`，**不许回 WinUI 菜单**。
  铁律：① 菜单页只吃 `WebAppearance` 那套变量（`tray-menu.*` + `html[data-kbd]` 才画焦点环）；② `EnsureWebViewAsync` 必须单飞守卫；
  ③ 宿主跳页走 `celeste.navigate`（WebShell 未 ready 先记下）；④ 托盘定位靠 `Shell_NotifyIconGetRect` + 工作区夹取。
- **预设级排版**：`theme.js` 把 `presetId` 落到 `html[data-preset]`；极客独有改动全挂 `html[data-preset^="geek"]`。
  ⚠ 标题栏中间偏右被分类轮盘压着，**别往标题栏中间放控件**（OUT/FMT/FLAG/BUF 也因此落在工具条）。
  ⚠ 换预设带 view transition 时 `celeste:theme` 派发时 `data-preset` 还是旧值 → 同时听 `celeste:theme-applied`。
- **统一控件/动效**：`ui-controls.js/css`、`popup-manager.js`、`motion.js/css`。铁律：
  - 下拉**原地增强原生 select**，绝不藏原生再插兄弟按钮；`ui-controls.css` 必须最后加载（箭头 `background-image!important` 钉住）。
  - 动效速度=越大越快；宿主动效偏好单独存不随预设切；系统"减少动画"优先于用户选择。
  - 列表换序用 `captureRows/replayRows`（按 `data-flipkey` 对齐），网格要算 dx；设置页点分类不能 `renderNav()`。
  - 共用浮层：每次 open 发 `popupToken`；关闭立刻还焦点。右键菜单元素复用同一个。
  - **主题过渡（10-27 教训）**：`startViewTransition` 修假错误**只接三个 Promise 的 reject 就够**；
    **不要加 transitionRunning 防重入**（把 work 改同步会掐断背景视频刚起的 play()）；也不要查 `#bg-video` src 守卫（mount 时 src 还没设，判不出）。
  - 冒烟回退 `playwright-core`；量位置等转场结束（有 10px 位移）。
- **网页窗口拖动（10-28）**：窗口无原生 caption，**禁用 CSS `app-region`**（WebView2 那个条件苛刻、本机实测不生效，
  且和 JS 拖动并存会互相打架让窗口抖）。唯一路径：`shell.js` 在 `.titlebar`/`.np-head` 判空白区 →
  `celeste.command` 发 `window {action:drag-start|drag-end}` → `WebShellWindow` 负责移动。
  ① 优先 `WM_NCLBUTTONDOWN(HTCAPTION)` 让系统跑自己的模态移动循环（最跟手、零轮询、UI 线程不参与），
  探测到没接管时回落 8ms `SetWindowPos` 直调（**别用 `AppWindow.Move()`**，每帧开销太大、明显拖后手）。
  ② ⚠ **合成非客户区消息前必须先查 `GetAsyncKeyState(VK_LBUTTON)`**：网页 pointerdown 到宿主跨一次 IPC（几十毫秒），
  等宿主收到时按键可能早松了，照发会让系统进入没人按住的移动循环 → "单击一下就能拖着跑"。详见当日日志。
  ③ 拖动期间宿主收不到 pointerup（WebView 里的 pointerup 被系统循环截走），`GetAsyncKeyState` 是唯一可靠凭据。
- ⚠ 正在播放详情页（10-27）：`now-playing.js/css`，入口=底栏封面按钮；歌词整首取（`case "nowPlaying"`），当前句前端算；
  打开时要藏 `.workspace/.main/.side-panel` 三处（只藏 .main 会被 flex 挤半屏）。
- ⚠ 工作树不明改动先当**自己被中断的残留**（本机网络/CPU 常打断）；动手前 `git log --oneline -5` 查并行改动，别覆盖。
- ⚠ 测试宿主必须拷 `System.Security.Cryptography.ProtectedData.dll`（缺→DPAPI 失败→误判设置损坏）。
- ⚠ 命令行传文件走「零散播放窗口」（精简引擎），测不到 FIR/EQ。
- **服务边界（10-31 起）**：曲目动作 `WebTrackActionService`（trackAction+browse）、目录 `WebFolderBrowseService`、
  曲库 `WebLibraryService`；宿主能力各走一个 `I*Host`（**只放服务造不出来的**：真播放/删盘/外部程序/页面快照）。
  `WebOperationGate` 是「网页发起的耗时操作」唯一互斥，**曲目动作与目录浏览共用同一实例**，否则两个选择器会叠在一起。
- ⚠ 探针发 `playAll` / `togglePlay` 必须用 `CelesteShell.send`：它们不回包，`request` 会等 12 秒超时抛"操作响应超时"，
  且播放一直跑会让 `currentIndex` 自己往前走，插入位置再也对不上。

## Git 元数据损坏修复（本机零填充老毛病会命中 .git）
- 症状：`git status` 报 index corrupt（签名 0x00000000）、`git branch` 报无法解析 HEAD → 通常是 refs/heads/main 与 index 头部被清零。
- 顺序不能错：**先备份元数据到仓库外** → 扫工作区确认源码没坏 → 按 **mtime 找今天新增的 commit 对象**（reflog 可能停在更旧的提交，
  直接照 reflog 恢复会丢当天工作）→ 验证 parent 链后回写 ref → `rm .git/index` + `git read-tree HEAD` 重建（不 reset --hard，不碰工作区）。
- ⚠ 别靠删 index 或覆盖 refs 盲修；每次都先验 `git cat-file -t` 与 parent 再动手。

## W13（去 WinUI）现状（2026-10-30）
- **架构已落地**：`UseWinUI=false`、7 个 AppSDK 包已删、入口改原生消息循环（`Program.Main` + `NativeMessagePump`）、
  旧 XAML 全部 `Page/Compile Remove`（**只排除不删文件**，迷你/零散源码留着给用户重构）、
  MainWindow 从 WinUI Window 变纯业务宿主（控件改为状态容器 `MainWindow.LegacyControls.cs`）。
  调度器/窗口 API 用**同名命名空间**顶替（`NativeDispatcherQueue.cs`、`NativeAppWindow.cs`）。
- ✅ **编译已收尾**（2026-10-30 复验）：`_publish_retry_clean.py` 先移走 obj/x64 再跑，
  等于全量重建，0 条 `error CS`、产物可启动。**但「已去 WinUI」仍不能宣称**：
  旧界面残留文件只做了 `Page/Compile Remove`（没删），业务还有一部分住在 MainWindow。
  进度与提交清单看 `collab/HANDOFF-2026-10-30-w13-workbuddy-progress.md`。
- ⚠️ **判断编译是否真过必须用 `-t:Rebuild`**（增量编常报「0 错误」假象，已骗三次）；编译器分批暴露错误。
- ⚠️ 路线：先把业务方法从界面文件搬出、再整批排除界面文件（直接排除会更糟：1450→1980）。工具在 `_w13_tools/`。
- **设置域已收口**（`e399b65`）：17 个 `appSettings` 命令全在 `WebSettingsService`，
  `MainWindow.WebSettings.cs` 只剩一行转发，宿主能力抽成 `ISettingsHost`（**只四条**：
  曲库快照 / 回 UI 线程 / 落地改动 / 重启目录监视）。
  铁律：**别往 `ISettingsHost` 加 MainWindow 现成属性** —— 服务能顺手读宿主状态就会长回去。
  句柄类操作（三个选择器、导入导出）不需要宿主传句柄：`NativeMainWebWindow.HandleOf(window)` 是静态的。
- DSD：用户确认 **DoP 已完成**，只剩 Native DSD 接入。
- **参数模型化（方案 A，2026-10-31 进度）**：RG → SRC → 压缩器 → 声场+矩阵 → **FIR/房间校正** 已全部搬进模型
  （`DspRgParameters`/`DspSrcParameters`/`DspCompParameters`/`DspFieldParameters`/`DspMatrixParameters`/`DspFirParameters`），
  契约是 `IDspParameterModel`（`Owns/Describe/Set`）；宿主侧定位只走 `ModelOf(name)`，**加新模块只加一行**。
  铁律：① 新模块先问「进不进 `dsp-rack.json`」—— 不进的（FIR 走 `room-correction.json`）必须在
  `ReloadDspPanelFromStores()` 里补一次装载，否则切听音方案后模型停在旧值；
  ② 「加载后回写」的元数据（IR taps/采样率）要**照旧在落盘时才写**，提前写会让探针多出字段差异；
  ③ 滑杆事件只导入被改动的那个字段，保存/下发交给模块自己的 `ApplyXxxParameters()`。
  ④ 回退目录纪律：**提交后立刻把当前出货 DLL 存进 `dist/web-ui-20261028-pre-w13-<下一段>-<日期>/`**（已在执行）。
- ✅ **方案 A 已于 2026-10-31 收官**（`259aeaa`）：六个模块 + 两个链路级模型全部离开控件。
  链路级两个模型（`DspRackParameters` 顺序+旁路 / `DspProfileCoordinator` 方案归属判定）
  **故意不走 `IDspParameterModel`** —— 它们没有"按控件名读写"的语义。
  遗留：`setBypass` 仍借道原生开关触发下发（模型已是唯一真值，写入路径没完全脱开控件）。
  方案指纹**不含** IR taps/采样率/声道数（播放会回写，放进来刚应用完就被误判"已改动"）。
- ✅ **目录浏览已搬出**（`aae15cc`）：`LibraryFolderScan`（扩展名表 + 两个递归走法 + 列一层 + 碟号/轨道排序）
  与 `WebFolderBrowseService`（roots/children/songs）是**唯一真相源**，原生曲库树和网页命令共用。
  `MainWindow.WebFolders.cs` 只剩分发 + `FolderPicker` + play/queue。
  铁律：① 加新音频格式只改 `LibraryFolderScan.AudioExtensions`；
  ② **删 partial 类的私有静态字段/方法前必须全项目 grep** —— 只搜本文件会漏（`AudioExtensions` 就在
  `Misc.cs`/`WebDav.cs`/`WebTagTools.cs` 被用，删了直接 4 个 CS0103）；
  ③ 目录相关探针要先给 `app-settings.json` 配测试目录（files 模式下 `LibraryWatchFolders` 是空的，
  不配就是空跑，照样"0 差异"），**跑完必须清 `library.db` tracks 表里的测试行**。

## 构建 / 发布
- publish 目录 `CelesteMusicPlayer\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`；同步 dist 必须用 publish 目录（`_sync_webshell_dist.py`，禁从 bin\x64 拷）。
- ⚠ **`_sync_webshell_dist.py` 默认目标是 `dist/web-shell-dsp-tags-complete`**（早期交付目录）。
  同步当前运行目录**必须显式传参**：`python _sync_webshell_dist.py dist/web-ui-20261028`（2026-10-31 误跑过一次默认目标）。
- ⚠ Roslyn/XamlCompiler 偶发崩溃=环境噪音，原样重试即成，别改代码找原因。
- ✅ publish 崩溃解法：`python _publish_retry_clean.py 8`（每轮关构建服务+清 obj/bin+重试）。
- ⚠⚠ **publish 成功 ≠ 产物正常**（2026-10-30 实测、2026-10-31 又中一次）：exit=0、`error CS`=0，
  DLL 却少了约 106 KB（3,076,096 → 2,970,112），少的是 CsWinRT 生成的 `WinRT.*GenericHelpers/*VtableClasses`。
  同一份源码重编一次即恢复。**同步 dist 之前必须先看体积**（正常 3.0–3.1 MB，突变就重编，别信 exit=0），
  必要时用 `System.Reflection.Metadata` 比两版类型表（异步状态机 `d__NNNN` 编号位移属正常）。
  坏产物通常"能启动也能跑"，靠跑功能测试发现不了，只有体积能一刀见血。
- ⚠ **Edit 工具会整文件改换行**：CRLF 文件被写成全 LF 时 diff 会从 1 行炸成上千行。
  改这类文件后必看 `git diff --stat`（不加 `--ignore-cr-at-eol`）；要恢复就用 `git show HEAD:<path>` 取原字节、
  只替换目标片段、写临时文件再 `os.replace`。
- ⚠ 只改网页不必等 publish；**改了 C# 必须等 publish 成功**。
- ⚠ 仓库根 `global.json` 未跟踪锁 SDK 9.0.317；net10 实测可行无需换包。
- ⚠ 验产物新鲜度对 `CelesteMusicPlayer.dll` 做 md5/搜字符串：字段/方法名是 **UTF-8**（#Strings 堆），字面量才是 UTF-16LE（#US 堆）——搜错堆会误判"没编进去"。
- ⚠ Python 改 csproj 注意 BOM（utf-8-sig 读会吃、utf-8 写没）。源码偶发被清零（前 4096 字节 NUL>0.5 判坏）。
- ⚠ 冒烟 SUSPECT_LINES=0 抓不到被 `Safe()` 吞的错，必须肉眼过新增 ERR 行（脚本 `_smoke_settings.py`）。
- ⚠ stash 二分时别用 `git checkout --`（会丢工作树改动）；用 `cp` 备份，丢了的从 `git fsck --dangling commit` 找。

## 音频 / DSP
- 链路：ffmpeg PCM 缓存→SeamlessWaveProvider(gapless)→独占 SRC→降混→变速（托管，保调 WSOLA/变调 Hermite）→celeste_dsp_core.dll 机架。**全关=bit-perfect**，内核只剩 native2，DSD DoP 不变速。
- 音频设置面板四页签带 `_audioSettingsReady` 守卫。

## WinUI3 铁律
- 新界面模式三连：设置白名单 + ComboBoxItem Tag + ApplyUiStyleMode 分支，缺一静默不生效。
- 主题判断沿元素树找显式 Light/Dark 祖先，**禁读 App.RequestedTheme**。
- 秒崩 0xC000027B = XAML 初值绑定写后续 x:Name(null) → 新面板事件加就绪守卫。
- ⚠ `AppSettingsStore.Clone()` 逐字段拷贝，加字段必须同步（漏=存进文件但 Load 恒默认，已五例）。
- ⚠ 跨 partial 文件 `DispatcherQueue.GetForCurrentThread()` 全限定（裸写会解析到 Window 属性）。
- ⚠ 选文件统一 `MainWindow.PickerHostHandle()`。
- ⚠ StartupLog 按文件名分类（Playback/Audio→logs/audio，MainWindow/Lyrics→logs/app）；查独立窗口两个文件都看。
- 自持引擎新 Window 构造函数赋 `HiFiOutputBackend.UiDispatcherQueue`（否则不出声）。
- 图标只用已在用的 Segoe 字形；左栏单一真相源 `NavContentAlignment`。

## 独立窗口
- ⛔ **用户 2026-10-28 拍板：迷你播放器、零散播放窗口暂不改** —— 他要自己重构。
  除他明确要求外，不碰 `MiniPlayerWindow*`、`ScatteredPlayWindow*`，
  也不拿它们当参考去"顺手统一"。需要这些窗口配合的能力先在接口处留口子，别直接改实现。
- ✅ **桌面歌词例外，2026-10-29 已按用户要求重做**（`WebDesktopLyricsOverlay.cs`）：必须 WebView2 + **不要 WinUI**。
  定论：XAML `Window` 拿不到桌面逐像素透明（透明背板会退回 `#202020` 兜底色），
  只能裸 Win32 窗口 + `Windows.UI.Composition` 的 `DesktopWindowTarget` + WebView2 合成宿主。
  铁律：合成树两层（根容器相对尺寸 + 网页宿主绝对尺寸）、合成宿主仍要用 `Bounds` 定网页尺寸、
  网页透明底走进程环境变量 `WEBVIEW2_DEFAULT_BACKGROUND_COLOR`（要在 WebView2 环境创建前设）、
  `controller.IsVisible=true` 会硬崩别碰。构建必须加 `-p:CsWinRTAotOptimizerEnabled=false`。
  细节见 `collab/HANDOFF-2026-10-29-desktop-lyrics-win32-host.md` 与 memory/2026-10-29。
- 零散播放窗：自持引擎、chrome 全自绘、外圈白线原生层收尾、拖动只能轮询定时器、收场发 WM_CLOSE。
- 迷你播放器：单向数据流（主窗 200ms 推），迷你窗零播放逻辑，480×118+扩展区。

## 封面 / 标签
- CoverArtService 三档缓存 160/320/768，改档位必须 `CacheVersion`+1。
- 标签页保留 DataGrid（用户拍板）；网页皮肤/测试皮肤已删，保留主题文件机制。

## 工程约定
- 用户不懂编程：大白话结论先行、诚实交代未做项。不 rebase/force-push、不 `git add -A`、commit 独立可回退、构建过才推。
- 日常交付 `dist\run-latest\`；禁 UIA 点击冒烟。待办唯一真相源 `outputs/待办清单-<日期>.md`（当前 2026-10-29）。
- **完整网页样式包**（`.celeste-player` ZIP，detail/mini/quick 三入口 + manifest + 受限上下文）方案原文
  `collab/PLAN-2026-10-29-player-page-pack.md`，排零散播放器之后，P0 契约→P1 运行时→P2 本地库→P3 小窗→P4 可视化。
  **P1–P3 未完成前不许把 JSON 改色包说成完整页面包**。
