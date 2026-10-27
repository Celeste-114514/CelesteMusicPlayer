# CelesteMusicPlayer 项目长期记忆
> 只留跨会话有效事实。提交查 git log；当日细节查 memory/YYYY-MM-DD.md；构建发布见 celeste-build-verify skill；细目见 memory/ref-*.md。

## 当前主线：网页外壳（WebView2）+ 设置页 + 外观
- **架构定案（10-20）**：一个网页一整块界面 + 内部路由，先浏览器调再接 C#。**上次失败根因=平行界面架构**
  （每个原生页在 web 重抄一遍，维护无底洞），别再走那条路。原型 `outputs/webui-proto/`，正式源 `outputs/webview2-shell/`。
- **交付目录** `dist/web-shell-dsp-tags-complete/`（自包含，双击 `打开网页直接进播放器.cmd` 即 `--web-shell`），
  含 `交付哈希清单.txt`；旧目录留 `dist/_prev_*`。网页文件改动只改源目录，再发布/同步才进 dist。
- **设置页命令是 `appSettings`**（`audioSettings` 是底栏那套音频面板，别混）。
  C# 侧 `WebSettingsSchema.cs`（字段单一真相源：10 分类 ~130 项，带 kind·note·scope·min-max-step·advanced）
  + `MainWindow.WebSettings.cs`（get/patch/pickFolder/pickFile/cache/webdav/credentials/fileAssoc/hotkeys/
  folders/clearRecent/export/import/reset/about/update）；网页侧 `settings.css|settings.js` + `#settings-page` + shell.js 路由。
  **铁律**：改字段必须同改 Defs/Readers/Writers 三处，否则网页上根本不出现；`patch` 按字段白名单写 +
  revision 冲突检测（别整对象回写）；密码走 `credentials`（空=保留、`__clear__`=清除，快照只回"有没有"）；
  `OutputMode` 在设置页**只读**（真正切换走底栏音频面板，别两处写同一件事）。
- **外观（10-27 做到「元素可调」）**：`WebAppearance.cs`（语义变量目录 22 色 + 12 数值、24 套预设 6 组、
  明暗成对由 HSL 派生、草稿/覆盖、`web-appearance.json`）+ `MainWindow.WebAppearance.cs`
  （get/select/mode/accent/preview/commit/cancel/reset/savePreset/deletePreset）+ `WebShellWindow` 的 `theme` 命令
  （ready 立即推快照，之后只在 revision 变时重发）；网页 `theme.css|theme.js|appearance.js`。
  **铁律**：
  - 变量名宿主生成时**已带 `--`**，web 侧别再拼一次（拼成 `----bg` → setProperty 抛错 → 全盘不生效，已踩过一次）。
  - 预览必须用**宿主新算的那套** bundle 写根变量，不能写完后用 `state`（旧值）收尾，等于没改。
  - 底栏/某部件要跟随某语义色时**用 `var(--shell)` 或 color-mix 派生，别单独存一份值** ——
    否则用户改了外壳色、底栏留旧值 = "半截没变"（这次用户报的"播放状态栏一直浅色"就是这个 + 背景写死 rgba）。
  - `canvas` 里画的（进度波形）拿不到 CSS 变量：读 `--wave-idle/--wave-played`，并在
    `celeste:theme-applied` 事件里重读色板重绘。
  - 深色还要 `:root[data-theme=dark]{color-scheme:dark}`，否则滚动条/滑轨/下拉仍是浅色（原生绘制，CSS 变量管不到）。
  - 数值 override 存的是 `"70px"`，读回时先剥单位再 `TryParse`，否则滑杆回弹看着像没存上。
  - 逐页去硬编码用 `_theme_var_sweep.py`（按 HLS 亮度+色相分类映射）。**金/绿/蓝装饰色与 DSP 功能性配色一律不动**。
  - 旧 `.celeste-theme` 的 39 个令牌是**原生窗口**的消费者，与网页外观各管各的，别硬绑。
- 网页 DSP：非 EQ 模块**不另建状态**，直接读写原生 XAML 控件；首次访问要 `EnsureWebDspUiReady()`；
  `dsp.js` 的 `VISUAL_OWNED` 表登记接管控件，通用控件表跳过防双渲染；EQ 曲线 1 单位 = 1 CSS px（H 恒 300）。
- ⚠ 测试宿主必须拷 `System.Security.Cryptography.ProtectedData.dll`：缺它 → DPAPI 解密失败 →
  把用户真实 `app-settings.json` 判成「损坏」并备份（曾积 13 份 `.corrupt-*`）。
- ⚠ **命令行传文件走「零散播放窗口」**（精简引擎，无完整 DSP 机架），那条路永远测不到 FIR/EQ。
- OPRA 源 `opra.roonlabs.net`，缓存 `%LOCALAPPDATA%\CelesteMusicPlayer\opra\database_v1.jsonl`（12MB）；
  `tools/OpraOnlineProbe` 可无界面验证。⚠ 沙箱曾把 `WebShellDspTagsSmoke.cjs` 整个清成纯 NUL，改完大文件要查 NUL。
- **统一控件层与动效控制器（10-27 落地，方案 `collab/RESULT-20261027-webview-controls-motion-plan.md`）**：
  新增 `ui-controls.js/css`（统一下拉 + 共享模态 + 控件令牌）、`popup-manager.js`（共用浮层）、
  `motion.js/css`（动效控制器）。计划见 `collab/RESULT-20261027-webview-controls-motion-batch1.md`。
  - **铁律：下拉一律"原地增强原生 select"** —— 保留原生控件的值/尺寸/位置/读屏/Tab，
    只接管外框、箭头、焦点圈，展开层用自绘 listbox。
    **绝不藏起原生控件再插一个兄弟按钮**：grid/flex 会多占一格（标签排序 4 列、DSP `grid-column:2/-1`）。
  - ⚠ **`ui-controls.css` 必须加载在所有页面样式之后**（index.html 最后一行），否则页面 `background:` 简写清掉箭头图；
    箭头用 `background-image:...!important` 单独钉住，底色/边框仍归页面。
  - 同一 select 可能被扫两次（自动挂载 + 模块带参数调用）→ `enhanceSelect` 要**并参数**而不是返回旧的。
  - **动效速度语义 = 越大越快**；预设里存的是旧「时长倍率」（大=慢），宿主派生时取倒数换算。
  - 宿主动效偏好 `motionMode/motionSpeed(0.6-1.6)/motionStrength(0-1.25)/motionBounce` **单独存、不随外观预设切换**；
    外观页「动效」卡改它们走 `theme` 通道的 `motion` 操作（显式传 null = 回到预设）。
  - `--motion-continuity`/`--motion-fast` 曾经从未定义 → 30 处 transition 整条作废（悬停一直是瞬变），现已补别名。
  - `file://` 下页面里读 `document.styleSheets[].cssRules` 会抛安全错误 → 扫 CSS 硬编码颜色要在 Node 里读文件。
  - **共用浮层的两条铁律**（10-27 补）：
    ① 每次 open 发一个 `popupToken`，旧浮层的退场动画只许拆自己那一份（同 id 反开不会被拆）；
    ② 关闭时**立刻**把焦点还给触发控件，不等退场动画（等 140ms 的话这段时间键盘操作全丢）。
  - **右键菜单（track-menu.js）已改走共用浮层**；菜单元素**复用同一个**（每次新建会出现两个
    `#track-context-menu`，点按钮"命中两个"）。多选栏拆成"外壳居中 + 内层动画"两层：
    外壳要 translateX(-50%)、动画要 translateY，写一起会互相覆盖。
  - **系统「减少动画」优先于用户在外观页选的模式**：选了"完整"也不该盖过系统的无障碍设置。
    有效档位：off / reduce（系统，只留很短淡变）/ lite（去位移）/ full。
  - 动效分类开关（nav/popup/list/control/theme/cover）与时长令牌由并行 agent 实现（`f1a3940`），
    令牌名很细：`--dur-nav-slow`/`--dur-nav-collapse`/`--dur-panel-in`/`--dur-cover-zoom`/`--dur-tab` 等。
  - ⚠ **本仓库有并行 agent 同时在改**：动手前先 `git log --oneline -5` 看有没有别人的新提交，
    重叠的改动要合并，别覆盖（我曾差点用自己那套分类开关覆盖掉他们的）。
  - 网页冒烟脚本要能回退 `playwright-core`（本机只装了它，只 require 'playwright' 的脚本一直跑不起来）。
    `tools/WebShell*Smoke.cjs` 共 18 个（含新增 `WebShellMotionStress.cjs`），现在全过。
    量位置前要**等页面转场结束**（转场有 10px 位移，量早了会差 10px）。

## 构建 / 发布
- **publish 目录是 `CelesteMusicPlayer\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`**
  （pubxml 的 PublishDir 相对项目目录且**不带 x64 平台段**；构建输出才在 `bin\x64\Release\...`）。
  `_publish_selfcontained.py` / `_sync_webshell_dist.py` 里的 TFM 已同步为 net10；同步 dist **必须用 publish 目录**，
  用错=框架依赖 exe(177KB)+旧自包含 DLL 混在一起 → 启动弹「必须安装 .NET」（本机装了也弹）。
- ⚠ **Roslyn 偶发内部崩溃**（`Internal CLR error 0x80131506` 在 `NullableWalker`；`AccessViolationException`
  在 `MetadataWriter`；`Microsoft.CSharp.CurrentVersion.targets(239,5)`）：第一次崩、**原样重试第二次就成**，
  与 XamlCompiler 连崩同类属环境噪音，不要改自己代码找原因。
- ⚠ publish 前 obj/x64·bin/x64 搬不动（WinError 5）→ 先 `dotnet build-server shutdown`。
- ⚠ **XamlCompiler.exe 会 0xC0000005 崩**（`Microsoft.UI.Xaml.Markup.Compiler.interop.targets` MSB3073），
  与 Roslyn 崩溃同类属环境噪音；挪开 obj/x64、bin/x64 全量重建也压不住。
  **只改网页资源时不必等 publish 成功**：二进制沿用上一次成功发布，
  按 csproj 的 Content 规则把 `outputs/webview2-shell/*.{html,css,js}` 拷进 publish 的 `WebShell\`，
  再跑 `_sync_webshell_dist.py`（BAD=0 即可）。⚠ 改了 C# 就必须等 publish 成功，否则宿主改动不生效。
- ⚠ 仓库根 `global.json` **未跟踪**，把本机 SDK 锁在 9.0.317（想用 net10 必须先动它，否则 NETSDK1045 看着像不支持）。
  net9→net10 已实测可行：现有 WinAppSDK 1.8.2511xxxxx + CommunityToolkit DataGrid 7.1.2 **无需换包**。
- ⚠ 验证产物新鲜度**别信 exe**（跨构建字节不变的启动器外壳），对 `CelesteMusicPlayer.dll` 做 md5 / 搜字符串。
  **搜法要按类型分**：字段/方法名在元数据 `#Strings` 堆里是 **UTF-8**，字符串字面量在 `#US` 堆里才是 UTF-16LE。
  2026-10-27 就因为只搜 UTF-16LE，把一份**确实已编进去**的 DLL 误判成"字段没进去"。
- ✅ **publish 崩溃有解了（10-27 实测）**：`python _publish_retry_clean.py 8` —— 每轮先
  `dotnet build-server shutdown` + 把 obj/x64、bin/x64 挪走再 publish，**第 1 轮就过（75s）**。
  这类崩溃就是随机的（skill 里写死 ~50% 失败率），**没有神奇开关**：
  `-m:1`/`-nodeReuse:false` 做过受控 A/B 属巧合；`UseWinUIOutOfProcessXamlCompiler=false` 不是真属性；
  `DOTNET_EnableHWIntrinsic=0` 2/4 照崩。**唯一有效的是"关服务 + 清中间产物 + 多轮重试"**。
  判据：`error CS` 行数跨轮稳定 = 真代码错，立即停；崩溃时 stdout/stderr 全空、obj 下无 .g.cs。
- ⚠ 冒烟 SUSPECT_LINES=0 抓不到被 `Safe()` 吞掉的真错 —— **必须肉眼过一遍新增 ERR 行**。
  已知噪音：`App.PrimaryLanguageOverride`、`[thumb] 渲染全透明`。脚本 `_smoke_settings.py`。
- ⚠ Python 改 csproj：`utf-8-sig` 读会吃 BOM、`utf-8` 写回没 BOM → git 显示 ` M`，事后 `git checkout --` 还原。
- 源码偶发被清零（前 4096 字节 NUL>0.5 判坏，从 HEAD 恢复）。工作树大量未跟踪文件（dist、native/build）动不得。
- 构建/发布坑见 ref-build-publish.md；DataGrid 见 ref-datagrid-toolkit.md；在线源条款见 ref-online-sources.md。

## 音频链路 / DSP
- 链路：ffmpeg PCM 缓存→SeamlessWaveProvider(gapless)→独占 SRC→降混→ManagedDspSourceProvider（变速）→
  celeste_dsp_core.dll 机架(EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→电平表)。
  **全关 = bit-perfect**。内核只剩 native2；变速在托管侧（保调 WSOLA / 变调 Hermite，DSD DoP 不变速）。
- 音频设置面板四页签带 `_audioSettingsReady` 守卫。

## WinUI3 铁律
- 新界面模式三连：①设置白名单 ②UI ComboBoxItem Tag ③ApplyUiStyleMode 分支，缺一静默不生效。
- 取色只认 resolver 套；主题判断沿元素树找显式 Light/Dark 祖先，**禁读 App.RequestedTheme**。
- 秒崩 0xC000027B = XAML 初值+事件绑定写后续 x:Name(null) → NRE；新面板 ValueChanged/Toggled 加就绪守卫。
- ⚠ `AppSettingsStore.Clone()` 逐字段显式拷贝，加字段必须同步（漏拷=「存得进文件但 Load() 恒默认」，已五例）。
- ⚠ CS0176：xaml.cs 无 `using Microsoft.UI.Dispatching` 时裸写 `DispatcherQueue.GetForCurrentThread()` 只解析到
  Window 实例属性；跨 partial 文件一律全限定。
- ⚠ UserControl 里选文件不能 `WindowNative.GetWindowHandle(this)` → 统一 `MainWindow.PickerHostHandle()`。
- ⚠ 浮层遮罩别用 `Grid.RowSpan` 盖全窗（吃标题栏点击）；**Collapsed→Visible 后不同帧 Begin 动画会被静默吞**
  （延一帧 + 400ms 兜底）；**XAML 故事板 TargetName 有解析失败案** → 代码 `Storyboard.SetTarget` 锁死再 Begin。
- "稳定检测"判据用 2px 漂移阈值；**兜底 Stop 必须无条件可达**（别写在 if(changed) 后面）。
- 第三方控件模板 VisualState 动画别运行时打补丁（COMException 0x80040154）。
- ⚠ StartupLog 按 CallerFilePath 文件名推断分类：含 Playback/Audio/HiFi → logs/audio/；含 MainWindow/Visualizer/Lyrics → logs/app/。
  **查独立窗口必须同时看两个文件**（`_smoke_scattered2.py` 是范本）。
- 自持引擎的新 Window 必须在构造函数赋 `HiFiOutputBackend.UiDispatcherQueue = this.DispatcherQueue`（否则不出声）。
- 自定义 Slider 模板必须横竖两向都做部件（HorizontalTemplate 恰 3 列 / VerticalTemplate 恰 3 行）。
- 图标只用程序里已在用的 Segoe 字形（E8BB 关/E921 最小化/E9D9 均衡器/E713 处理/E767 音量/E768 播放/E893 变速/
  E710 打开文件/EA37 队列/E958 封面占位/E8EE 列表循环/E8B1 随机/E8FD 顺序/E8ED 单曲循环/E72A 单曲播放/E892 上一首/E8D2 属性）。
- 左栏单一真相源：`NavContentAlignment` / `ApplyLeftNavCardChrome`；**同一 UI 属性多处赋值必须抽单一真相源**。

## 独立窗口（零散播放窗 / 迷你播放器）
- **零散播放窗**：自持引擎、纯内存队列不碰曲库；窗口 chrome 全自绘 + **外圈 1px 白线必须原生层收尾**
  （剥 WS_THICKFRAME/WS_CAPTION 等 + 子类化吞 WM_NCCALCSIZE/NCPAINT + SetWindowRgn 20dp 圆角；
  ⚠ SetWindowRgn 在 **user32**，CreateRoundRectRgn 在 gdi32）。拖动**只能用轮询定时器**
  （XAML 跟踪 / WM_NCLBUTTONDOWN / SetCapture 三条路全不通）；`IsLeftButtonPressed` 在开了 AllowDrop 的元素上恒 false；
  `IsControlElement` 只能上查 3 层。文件属性走 shell32 `SHObjectProperties`（Process.Start Verb 会 SE_ERR_NOASSOC）。
  收场发 WM_CLOSE（kill 丢日志缓冲会误判）。
- **迷你播放器**：单向数据流重建（MiniPlayerWindow.* + MiniPlayerBridge + `MainWindow.MiniPlayer.cs` 200ms 推），**迷你窗零播放逻辑**；
  480×118 + 扩展区（轮询定时器逐帧 Resize）；方案见 `outputs/迷你播放器重建方案-2026-10-19.md`。
  备份 `C:\Users\MSI\_celeste_mini_removed_20261019\`。

## 封面 / 视觉
- `CoverArtService` 分档缓存：Thumb **160** / Album 320 / Large 768，JPEG 质量 **0.92**。
  ⚠ **改档位必须 `CacheVersion` +1**（否则旧档一直命中）。排查看日志 `[播放栏封面] 缓存=…像素=WxH`（logs/app）。
- Cover 态压暗纱 = `CoverTint.BuildDimGradient`（保色相、亮度压 28%→13%，alpha 199→219），取不到色兜底 82% 纯黑。
- 播放页景深只动右列；播放页展开时左栏整块隐藏。

## 标签 / 皮肤
- 位深两条路径：表格 `TryGetFactsFast`；状态栏 `TryGetPartsCached`。在线标签 OnlineTagFill（MB+iTunes 免 key）。
- 网页皮肤与令牌测试皮肤 10-18 已全删（备份 `C:\Users\MSI\_celeste_skin_removed_20261018`），
  **保留**主题文件机制（CelesteTheme.cs / CelesteTokenApplier.cs / MainWindow.CelesteTheme.cs）。换前端=另起炉灶。
- 标签页**保留 DataGrid**（用户 10-17「现在挺好的」）；"弃用 DataGrid"作废。智能播放列表④作废；设备记忆 DSP 档③活。

## 工程约定
- 用户不懂编程：大白话结论先行、禁营销腔、诚实交代未做项。推送：不 rebase/force-push、不 `git add -A`、
  commit 独立可回退、构建过才推；日常交付 `dist\run-latest\`；禁 UIA 点击冒烟（只做单次启动验证）。
- 待办在 `outputs/待办清单-*.md`。未做：m3u 按单歌单导出、Discogs/fanart 署名。main 本地领先远端较多，未推送。
