# CelesteMusicPlayer 项目长期记忆
> 只留跨会话铁律。提交查 `git log`；当日细节查 `memory/YYYY-MM-DD.md`；构建发布见 `ref-build-publish.md` + `celeste-build-verify` skill；DataGrid 见 `ref-datagrid-toolkit.md`；在线源见 `ref-online-sources.md`。

## 主线：网页外壳（WebView2）
- **架构**：一块网页 + 内部路由，先浏览器调再接 C#。**别走"平行界面架构"老路**（每个原生页在 web 重抄一遍）。
  源 `outputs/webview2-shell/`，出货 `dist/web-ui-20261028/`（改网页只改源目录，再同步）。
- **设置页 `appSettings`**（`audioSettings` 是底栏音频面板，别混）。字段真相源 `WebSettingsSchema.cs`：
  **改字段必须同改 Defs/Readers/Writers 三处**；patch 白名单 + revision 冲突检测；密码走 `credentials`（空=保留、`__clear__`=清除）；OutputMode 只读。
- **外观**：`WebAppearance.cs`（22 色 + 12 数值 + 24 预设）+ `WebShellWindow.theme` 命令；网页 `theme.css|theme.js|appearance.js`。
  铁律：变量名宿主已带 `--` 别再拼；预览要用宿主新算的 bundle 收尾；部件跟语义色用 `var(--shell)`/color-mix 别单独存值；
  canvas 读变量并在 `celeste:theme-applied` 重绘；深色要 `color-scheme:dark`；数值 override 剥单位再 Parse。
- **DSP 网页**：非 EQ 模块不另建状态，直读写原生控件；首访 `EnsureWebDspUiReady()`；`VISUAL_OWNED` 防双渲染。
  深色靠 `dsp-theme.css`（最后加载），功能色（EQ 渐变/电平绿/白指针）不跟主题。
- **系统托盘**：Win32 `Shell_NotifyIcon` + **WebView2 独立菜单窗**；降级只到原生 `TrackPopupMenu`，**不许回 WinUI 菜单**。
  ① 菜单页只吃 `WebAppearance` 那套变量（`tray-menu.*` + `html[data-kbd]` 才画焦点环）；② `EnsureWebViewAsync` 必须单飞守卫；
  ③ 宿主跳页走 `celeste.navigate`（未 ready 先记下）；④ 定位靠 `Shell_NotifyIconGetRect` + 工作区夹取。
- **预设级排版**：`theme.js` 把 `presetId` 落到 `html[data-preset]`；极客改动全挂 `html[data-preset^="geek"]`。
  ⚠ 标题栏中间偏右被分类轮盘压着，**别往那放控件**。⚠ 换预设带 view transition 时 `celeste:theme` 派发时 `data-preset` 还是旧值 → 同时听 `celeste:theme-applied`。
- **统一控件/动效**（`ui-controls.js/css`、`popup-manager.js`、`motion.js/css`）：
  - 下拉**原地增强原生 select**，绝不藏原生再插兄弟按钮；`ui-controls.css` 必须最后加载（箭头 `background-image!important` 钉住）。
  - 动效速度 = 越大越快；宿主动效偏好单独存不随预设切；系统"减少动画"优先。
  - 列表换序用 `captureRows/replayRows`（按 `data-flipkey`）；设置页点分类不能 `renderNav()`。
  - 共用浮层每次 open 发 `popupToken`；关闭立刻还焦点；右键菜单复用同一元素。
  - **主题过渡**：`startViewTransition` 修假错误**只接三个 Promise 的 reject**；**不要加防重入**；
    也不要查 `#bg-video` src 守卫（mount 时 src 还没设）。冒烟用 `playwright-core`；量位置要等转场结束。
- **窗口拖动**：窗口无原生 caption，**禁用 CSS `app-region`**（本机实测不生效且和 JS 拖动打架）。
  唯一路径：`shell.js` 判空白区 → `celeste.command` 发 `window {action:drag-start|drag-end}` → `WebShellWindow` 移动。
  ① 优先 `WM_NCLBUTTONDOWN(HTCAPTION)` 让系统跑模态移动循环；未接管再回落 8ms `SetWindowPos`（**别用 `AppWindow.Move()`**）。
  ② ⚠ **合成非客户区消息前必须先查 `GetAsyncKeyState(VK_LBUTTON)`** —— IPC 有延迟，等宿主收到时按键可能早松了，
  照发会进入没人按住的移动循环（"单击一下就能拖着跑"）。③ 拖动期间收不到 pointerup，`GetAsyncKeyState` 是唯一凭据。
- **正在播放详情页**：`now-playing.js/css`，入口=底栏封面按钮；歌词整首取（`case "nowPlaying"`），当前句前端算；
  打开要藏 `.workspace/.main/.side-panel` 三处（只藏 .main 会被 flex 挤半屏）。
- ⚠ 工作树不明改动先当**自己被中断的残留**；动手前 `git log --oneline -5`。
- ⚠ 测试宿主必须拷 `System.Security.Cryptography.ProtectedData.dll`（缺→DPAPI 失败→误判设置损坏）。
- ⚠ 命令行传文件走「零散播放窗口」（精简引擎），测不到 FIR/EQ。

## 服务边界（W13 主线）
- 曲目动作 `WebTrackActionService`（trackAction + browse）、队列 `WebQueueService`（queue + more）、
  目录 `WebFolderBrowseService`、曲库 `WebLibraryService`、
  标签 `WebTagService`（tags 全部：list/read/write + 在线搜索 + 歌词 + 改名 + 封面，会话态在实例里所以是单例）、
  设置 `WebSettingsService`、DSP 参数 `Dsp*Parameters`。宿主能力各走一个 `I*Host`，
  **只放服务造不出来的**（真播放/删盘/外部程序/页面快照）。
  ⚠ **别往 `I*Host` 加 MainWindow 现成属性** —— 服务能顺手读宿主状态就会长回去。
- `WebOperationGate` 是「网页发起的耗时操作」唯一互斥，**曲目动作与目录浏览共用同一实例**，否则两个选择器会叠在一起。
- ⚠ **删 partial 类的私有字段/方法前必须全项目 grep** —— `AudioExtensions` 就在 `Misc.cs`/`WebDav.cs`/`WebTagTools.cs` 被用，删了 4 个 CS0103。
- ⚠ 探针发 `playAll` / `togglePlay` 必须用 `CelesteShell.send`：不回包，`request` 会等 12 秒超时；
  且播放一直跑会让 `currentIndex` 自己往前走。`transport` 命令没有 `pause`（暂停是顶层 `togglePlay`）。
- ⚠ 探针设计坑：「队列外」曲子被上一步加进队列后，下一步就变"已在队列"，`delta` 恒 0 → 要用两个不同 outsider。
- ⚠ **标签探针会真写盘**（改标签/改名/删封面）：素材必须复制一份，用 `tools/_w13_tag_reset.py` 复位
  （素材 + `library.db` + `LibraryWatchFolders`），跑完还原 `library.db` 与 `app-settings.json`。
  删 `library.db-wal` 别用 bash `rm`（沙箱报 SAFE_DELETE_INVALID_PATH），用 Python `os.remove`。

## W13 现状
- `UseWinUI=false`、AppSDK 包已删、入口 `Program.Main` + `NativeMessagePump`、MainWindow 变纯业务宿主
  （控件→状态容器 `MainWindow.LegacyControls.cs`）。旧 XAML 只做 `Page/Compile Remove`（**不删文件**，迷你/零散留给用户重构）。
- ✅ 编译已收尾（2026-10-30），但**「已去 WinUI」仍不能宣称**：界面残留文件还在，业务还有一部分住在 MainWindow。
- ⚠ **判断编译是否真过必须 `-t:Rebuild`**（增量常报"0 错误"假象，已骗三次）。
- ⚠ 路线：先搬业务方法出来，再整批排除界面文件（直接排除更糟：1450→1980）。工具在 `_w13_tools/`。
- 剩余：标签在线搜索 + 歌词编排（`WebTagTools.cs`）、引擎本体（必须真出声）、
  `Features.cs`(178K)/`UiTheme.cs`(148K) 要先甄别哪些控件操作已死。
- **DSP 方案 A 已收官**（`259aeaa`）：六模块 + 两个链路级模型全离开控件；契约 `IDspParameterModel`（`Owns/Describe/Set`），
  定位只走 `ModelOf(name)`，**加新模块只加一行**。链路级两个（`DspRackParameters` 顺序+旁路 / `DspProfileCoordinator` 归属判定）
  **故意不走接口** —— 没有"按控件名读写"语义。
  ① 新模块先问「进不进 `dsp-rack.json`」—— 不进的（FIR 走 `room-correction.json`）必须在 `ReloadDspPanelFromStores()` 补装载；
  ② 「加载后回写」的元数据（IR taps/采样率）**照旧只在落盘时写**，提前写探针会多出差异；
  ③ 滑杆事件只导入被改动的字段，保存/下发交给 `ApplyXxxParameters()`；
  ④ 方案指纹**不含** IR taps/采样率/声道数（播放会回写，放进来刚应用完就误判"已改动"）；
  ⑤ 遗留：`setBypass` 仍借道原生开关触发下发。
- DSD：DoP 已完成，只剩 Native DSD 接入。

## 构建 / 发布
- ⚠ **回退目录纪律**：每次提交后立刻把当前出货 DLL+PDB 存进 `dist/web-ui-20261028-pre-w13-<下一段>-<日期>/`。
- publish 目录 `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`；同步 dist 用 `_sync_webshell_dist.py`，
  ⚠ **默认目标是老目录 `dist/web-shell-dsp-tags-complete`**，务必显式传参 `dist/web-ui-20261028`。
- ✅ publish 崩溃解法：`python _publish_retry_clean.py 8`（每轮关构建服务 + 清 obj/bin + 重试）。
- ⚠⚠ **publish 成功 ≠ 产物正常**：exit=0、`error CS`=0，DLL 却少约 106 KB（缺 CsWinRT `WinRT.*GenericHelpers/*VtableClasses`）。
  **同步 dist 前必须先看体积**（正常 3.0–3.1 MB），突变就重编，别信 exit=0。坏产物"能启动也能跑"，功能测试发现不了。
  重跑 `_publish_retry_clean.py` **救不回来**（它只清 `obj/x64`+`bin/x64`，不清 `bin/Release`）——
  要把 `obj`、`bin/Release`、`bin/x64` **整个**移走再 publish。
- ⚠ 干净 publish 只有 **269 个文件**（UseWinUI=false + AppSDK 已删）：不带 `DirectML.dll`/`CoreMessagingXP.dll`/
  `dwmcorei.dll`/`.mui`/`.winmd`。同步 dist 会报 `STALE=293`，那是 W13 之前的残留，**不是坏产物**。
- ⚠ **Edit 工具会整文件改换行**：CRLF 被写成全 LF，diff 从 1 行炸成上千行。改完必看 `git diff --stat`；
  要恢复用 `git show HEAD:<path>` 取原字节、只换目标片段、写临时文件再 `os.replace`。
- ⚠ 只改网页不必等 publish；**改了 C# 必须等 publish 成功**。
- ⚠ 验产物新鲜度：字段/方法名是 UTF-8（#Strings 堆），字面量才是 UTF-16LE（#US 堆），搜错堆会误判"没编进去"。
- ⚠ 冒烟 `SUSPECT_LINES=0` 抓不到被 `Safe()` 吞的错，必须肉眼过新增 ERR 行。
- ⚠ stash 二分别用 `git checkout --`（会丢工作树改动）；用 `cp` 备份，丢的从 `git fsck --dangling commit` 找。
- 细节全在 `ref-build-publish.md`。

## Git 元数据损坏（本机零填充老毛病会命中 .git）
- 症状：index corrupt（签名 0x00000000）、`git branch` 无法解析 HEAD → refs/heads/main 与 index 头部被清零。
- 顺序：**先备份元数据到仓库外** → 扫工作区确认源码没坏 → 按 **mtime 找今天新增的 commit 对象**
  （reflog 可能停在更旧的提交，照它恢复会丢当天工作）→ 验 parent 链后回写 ref → `rm .git/index` + `git read-tree HEAD`
  （不 reset --hard，不碰工作区）。⚠ 别靠删 index 或覆盖 refs 盲修。

## 音频 / DSP
- 链路：ffmpeg PCM 缓存→SeamlessWaveProvider(gapless)→独占 SRC→降混→变速（保调 WSOLA/变调 Hermite）→`celeste_dsp_core.dll` 机架。
  **全关 = bit-perfect**，内核只剩 native2，DSD DoP 不变速。音频设置面板四页签带 `_audioSettingsReady` 守卫。

## WinUI3 铁律（旧界面仍在用）
- 新界面模式三连：设置白名单 + ComboBoxItem Tag + ApplyUiStyleMode 分支，缺一静默不生效。
- 主题判断沿元素树找显式 Light/Dark 祖先，**禁读 App.RequestedTheme**。
- 秒崩 0xC000027B = XAML 初值绑定写后续 x:Name(null) → 新面板事件加就绪守卫。
- ⚠ `AppSettingsStore.Clone()` 逐字段拷贝，加字段必须同步（漏=存进文件但 Load 恒默认，已五例）。
- ⚠ 跨 partial 文件 `DispatcherQueue.GetForCurrentThread()` 全限定。
- ⚠ 选文件统一 `MainWindow.PickerHostHandle()`。
- ⚠ StartupLog 按文件名分类（Playback/Audio→logs/audio，MainWindow/Lyrics→logs/app）。
- 自持引擎新 Window 构造函数赋 `HiFiOutputBackend.UiDispatcherQueue`（否则不出声）。
- 图标只用已在用的 Segoe 字形；左栏单一真相源 `NavContentAlignment`。

## 独立窗口
- ⛔ **用户 2026-10-28 拍板：迷你播放器、零散播放窗口暂不改** —— 他要自己重构。除明确要求外不碰
  `MiniPlayerWindow*`、`ScatteredPlayWindow*`，也别拿它们当参考"顺手统一"；需要配合先在接口留口子。
- ✅ 桌面歌词例外（`WebDesktopLyricsOverlay.cs`）：必须 WebView2 + **不要 WinUI**。XAML `Window` 拿不到桌面逐像素透明
  （透明背板退回 `#202020`），只能裸 Win32 + `Windows.UI.Composition` `DesktopWindowTarget` + WebView2 合成宿主。
  铁律：合成树两层；合成宿主仍用 `Bounds` 定尺寸；网页透明底走进程环境变量 `WEBVIEW2_DEFAULT_BACKGROUND_COLOR`（环境创建前设）；
  `controller.IsVisible=true` 会硬崩。构建加 `-p:CsWinRTAotOptimizerEnabled=false`。
- 零散播放窗：自持引擎、chrome 全自绘、拖动只能轮询定时器、收场发 WM_CLOSE。
- 迷你播放器：单向数据流（主窗 200ms 推），零播放逻辑，480×118 + 扩展区。

## 封面 / 标签
- CoverArtService 三档缓存 160/320/768，改档位必须 `CacheVersion`+1。
- 标签页保留 DataGrid（用户拍板）；网页皮肤已删，保留主题文件机制。

## 工程约定
- 用户不懂编程：大白话结论先行、诚实交代未做项。不 rebase/force-push、不 `git add -A`、commit 独立可回退、构建过才推。
- 日常交付 `dist\run-latest\`；禁 UIA 点击冒烟。待办唯一真相源 `outputs/待办清单-<日期>.md`（当前 2026-10-29）。
- **完整网页样式包**（`.celeste-player` ZIP，detail/mini/quick 三入口）方案见 `collab/PLAN-2026-10-29-player-page-pack.md`，
  排零散播放器之后：P0 契约→P1 运行时→P2 本地库→P3 小窗→P4 可视化。
  **P1–P3 未完成前不许把 JSON 改色包说成完整页面包**。
