# CelesteMusicPlayer 项目长期记忆

> 只留跨会话有效事实。提交细节查 git log，当日细节查 memory/YYYY-MM-DD.md。

## 音频链路（有效）
源文件 → ffmpeg 转码 PCM WAV 缓存（%LocalAppData%\CelesteMusicPlayer\TranscodeCache，2GB LRU）→ OpenWaveSource（≤192MB 整读否则 8MB 流式）→ SeamlessWaveProvider（同格式字节续接=gapless）→（独占）SRC 升频→降混→ ManagedDspSourceProvider（软音量链首 → 一次 celeste_dsp_process 进原生 celeste_dsp_core.dll；机架：EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→变速→电平表）→ 输出分支。**全关=不进 process=bit-perfect。**

## DSP 内核
- DspCoreInterop.cs 45 P/Invoke（fixed 指针 + 16384 帧分块）；dsp-rack.json=机架顺序+压缩/声场/矩阵。⚠ **内核进程级单实例**：CreateEngine 必须整段过 EngineSwapGate 全局锁；xunit 并行测试互毁引擎 → EqRegression 整程序集串行。
- 内核 = ECHO 的 C++ 裁剪改写；native/echo-core/ 只服务独占输出、不在 DSP 链上。**设计照搬 ECHO、代码不用他的**（机架顺序+参数口径照 native/dsp-core/audio-engine/DspRackOrder.cpp:74-83）。**许可：如实披露+风险自担**（根 LICENSE / native/dsp-core/README-LICENSE.md / 设置页三处）。
- 参数范围：余量 −12..0、trim −24..+6、EQ31 ±12dB Q0.1..12、压缩阈值 −60..0/比 1..20/启动 0.1..200/释放 5..2000/补偿 −12..24、crossfeed 默认 25%/700Hz、声场 0..200%、矩阵 ±2。SDM/DSD 不做。
- 变速在链尾但 processBlock 是空壳 → 宣传"内核已支持"是错的；要落地需用户审批动原生内核。
- 输出分派：PCM 独占 HiFiOutputBackend.cs:1355；DSD/DoP requireExact 只有 self 支持；ASIO 只 16/32bit（24bit 已修 Widen24To32Provider）；音量：HiFi+软音量→DSP 采样级增益；DSD/DoP→软音量无效。
- **口径铁律：每个可视化先读内核源码再写。** 压缩=`CompressorProcessor.cpp:160-175` 逐行复刻、干湿混合在线性域；M/S=`sideDb+20log10(width/100)`；Crossfeed=one-pole；SRC 无真系数→标「示意图 · 非实测」。Crossfeed 有父开关依赖。
- 面板：13 模块/6 组导航，耳机校正无独立页（在「参数 EQ」页下半）；页头徽章=TextBlock hint + ToggleSwitch；MainWindow.DspPower.cs 10 条 entry。

## 极客皮肤
- 色源唯一：AppSettingsStore.GeekPhosphorColor → PhosphorColor()。非极客主题色=ThemeColorService 启动前写 Application.Resources ~50 accent 键；**极客严禁改全局资源（必崩 0xc000027b）** → MainWindow.GeekDsp.cs 做成 AudioFxBorder.Resources 元素级副本。已在树上的 Slider/ToggleSwitch 必须重建模板才认新值。
- **图标两套模式（一律白色）**：`GeekIconStyle`=Icon（默认）/ Glyph（字符键，磷光色）。上色判据：只刷 `ReadLocalValue(IconElement.ForegroundProperty)==UnsetValue` 的图标。
  - **⚠ 跨模式共用的定时器是头号陷阱**：`RehideAsciiIcons()`（每秒补藏）曾不分模式藏掉所有 FontIcon，已加 `if (_geekAsciiIconMode != false) return;`。**给极客加任何新模式，先检查所有"每秒自愈/补藏/补色"逻辑有没有对新模式放行。**
- 纯代码绘制走 GeekDspAccentColor() 分支；徽章「生效中」磷光浅底深字、「已旁路」琥珀保留；换色需 force:true。**新 UI 模块第一天就得接上色。**
- **⚠ "文字字符当图标"绝不能用等宽字体**（Consolas 缺字形被压瘦条 10x21）。统一走 `GeekShell.GeekGlyphFont` + `GeekGlyphFontSize=17`。

## 前端自由度：三层皮肤架构（路线 B = WebView2 覆盖层，已落地）
- **2026-10-09 用户拍板路线 B**（否决 Electron）。**2026-10-09 深夜拍板**：界面主体回原生 XAML；皮肤系统=令牌接线（710 处微软系统刷子 → Celeste.*）；DSP 留原生并令牌化；WebUI 三页（main/album/dsp.html）**冻结不删、入口可留**。现状：`ApplyCelesteThemeAsync` 零调用、CelesteTokens.xaml 未并入 MainWindow.xaml、710 处 ThemeResource 接微软刷子 / 0 处接令牌（UiTheme.cs 3100 行待退役）。
- ⚠ csproj 铁律：`Themes\CelesteTokens.xaml` 纯 ResourceDictionary 无 x:Class，**必须 `<Page Remove>`** 否则 XamlCompiler 静默崩（只报退出码 1、零错误信息）。
- **分工铁律：网页只长样子不做事，所有点击上报意图、C# 调现有播放方法，音频链路零改动。**
- **play 语义铁律**：网页「点某行播放」= **整表替换播放队列 + 从该行开始**（`PlayAlbum(replacePlaylist:true)` 口径）。
- **消息协议头号陷阱**：`CoreWebView2WebMessageReceivedEventArgs` **没有 `WebMessageAsString`**，只有 `TryGetWebMessageAsString()` + `WebMessageAsJson`；`JsonSerializer.Deserialize` **默认区分大小写**（小写 `"kind"` 映射不到 `Kind` → 全量静默丢弃），必须手动 `JsonDocument.Parse` 提 kind + 整根给 Payload；收消息 handler **无条件记账**（事件到达/长度/src 在任何提前返回之前落日志）。**「消息到了」≠「被处理」，拆信链上每个静默 return 都要点亮。**
- **宿主对象竞态**：`window.chrome.webview` 未注入完时裸访问抛 TypeError → 所有 WebUI 页面照 `whenWv` 队列 + `postQueue` 模式写，ready 幂等补发。
- **内核失效**：浏览器进程死后 CoreWebView2 变 null → EnsureCelesteWebHostAsync 内核失效即 Cleanup+Prewarm 拆旧建新；ProcessFailed 必先于 CoreWebView2 变 null 触发。⚠ 沙箱内无法验证 WebUI 渲染，只能无头 mock 自检 + 交付用户手测。
- **封面按专辑粒度算、同一专辑的歌共用**；albumcovers 每项带 `album` 名，网页建 `songByAlbum` 索引回填歌曲行。⚠ **「CSS 写了 ≠ 生效」——新建样式类必须真的挂到 DOM 上**，用 `_webcheck/` 无头 harness 验证（插 mock 宿主 + 喂数据 + Edge `--headless=new --screenshot`）；注入 JS **别用 Python repr**。
- **视图切换铁律**：独立容器只切 display，**禁改 `#view` innerHTML**（会把 #rows/#albgrid 一起删掉 → 回列表必 TypeError）。
- **⚠ 切视图必须 `#view.scrollTop = 0`**（在 `hideAllViews()` 里做）。漏了就是「切到歌曲页从第 301 首开始显示」——`BATCH=300` 首批只铺 300 行。
- **封面圆角**：`--r-card` 12px（列表行/网格卡/播放条）、`--r-card-lg` 14px（专辑详情大封面，对齐原生 `Radius.Cover` 令牌默认值）；封面 `img` 一律补 `border-radius: inherit`。
- **上行通道**：`songact`(openloc/edittags/delfromlib/queue) / `albumact`(play/queue/addto) / `artistact`(play/queue/addto) / `plsongact`(queue/remove/openloc) / `order` / `rate`(v=倍率) / `favcur` / `showqueue` / `mini` / `lyrics` / `feat`。`now` 带 `order`(枚举名) 与 `rate`(文本) 供回显。
- **⚠ 调原生 Click handler 的两类情况**：`PlaybackOrderButton_Click` / `FavoriteButton_Click` 这类"纯动作"可直接 `XxxButton_Click(this, new RoutedEventArgs())`；**"弹 MenuFlyout"型的（`PlaybackRateButton_Click` / `FeaturesMoreButton_Click`）不能照搬**——flyout 弹在按钮位置=网页底下，看不见。要换成底层 core 方法（`SetPlaybackRateAsync`）或网页自绘菜单再按 act 直调。
- **⚠ 原生 DSP 读数是"按页可见才轮询"**：网页盖着原生面板时都是旧值/空。`PushWebDspStateAsync`/`BuildWebDspMonMessage` 里必须先自己调 `UpdateDspFieldReadouts(...)`（**别调 `RedrawDspFieldRing`，收起时 ActualWidth=0 直接 return**）、`UpdateDspChannelBars()`、`UpdateDspSafetyChain()`、`UpdateDspCompMeters(...)` 再读文本。
- **正在播放页**：`#npPage`（fixed 覆盖层，独立于 `.win`）由点播放条封面 `#pbCov` 进入；封面只在换曲时推，`now.path` 变了先清 `S.nowCover`（否则新曲无封面会留上一首的图）。
- **⚠ `_webcheck` 自检桩必须带 `window.addEventListener('error', ...)` 写 `document.title`**（`make_dspcheck.py` 生成变体）。没这个异常陷阱就只能盲看截图。
- **音效处理已内嵌主界面右栏**（2026-10-10 用户拍板：「不要占左侧分类栏，就在右侧区域显示」）：DSP 面板是 main.html 里 `#dspview` 视图（左 220px 信号链导航 + 右模块内容，外层 `.sb` 分类栏照旧），dsp.html 保留但不再作入口。C# 侧 `_webDspEmbedded` / `OpenEmbeddedWebDspAsync` / `CloseEmbeddedWebDsp`；`HandleWebMainMessage` 开头按 `IsWebDspKind` 转 `HandleWebDspMessage`；切走 AudioFX 或关主界面时收面板。**嵌入模式不推 `now`**（主界面 track={title,artist,album}，DSP 页 track={t,s}，混推播放条必 undefined）。
- **网页侧 DSP 全部 id 加 `d_` 前缀、撞车类名加 d 前缀**（`.dbtn/.dcard/.dpill/.dbtnrow`）；EQ 曲线 `eqApprox/eqDraw` 逐行复刻 `MainWindow.EqDsp.cs`，两端口径必须一起改。
- **右键菜单走「网页自绘 + C# 落地」**（原生侧 `AreDefaultContextMenusEnabled=false`）。⚠ `cmShow` 的点击回调要读 `data-i` 属性索引 items（items 里有 `{sep:1}` 占位，按 DOM 顺序数会整体错位）。
- **⚠ headless Edge 画不出 main.html 里的 `position:fixed` 覆盖层**。验覆盖层样式改用独立小页（`_webcheck/menu_probe.html`），别在 main.html 上追。
- **⚠ async 同步段陷阱**：`_ = PrewarmXxxAsync()` 在 UI 线程调用时，第一个 await 之前的代码（含第一批文件读）**同步跑在 UI 线程** → 批次计算必须 `await Task.Run(...)` 包住。后台/UI 共碰的缓存一律 `ConcurrentDictionary`；**数据代际 bump 只能发生在快照重推时**。
- 专辑分组复用原生 `BuildAlbumEntriesFromTracks` 口径；详情曲目排序同 `OpenAlbumDetailCore`（碟号→音轨号→标题）；封面兜底链（CoverSourcePath 空→专辑内第一首）+无封面显首字符。
- **启动曲库路径**：用户**无会话文件**，每次启动走 `AppendMediaFolderTracksAsync`（曲库 `D:\天翼云盘同步盘\本地音乐`，云盘枚举 ~3s 是 I/O 下限）。该路径走 `BuildPlaylistItemsAsync`（并行指纹 → SQLite 索引命中跳过 TagLib），实测冷索引 5.6s / 热索引 213ms。
- **⚠ 交付排雷**：新旧 run-latest 逐文件比 md5 + 与 NuGet 官方缓存对 md5。"构建通过"≠框架文件原厂。覆盖写会被沙箱拦成全零假文件 → 先 move 整目录再 copytree。
- 设计规格（量自设计稿）：`outputs/Apple风格界面-2026-10-09.html`。窗口 1592×906；顶栏 44 `#F1F1F6`；侧栏 228 `#F5F5F8` 项 h32 圆角 8 选中底 `#FDECF0`；行高 62 分割线 `#EFEFF2`；播放中行序号换红 ♪ 整行 `#FDECF0`；页头 98；播放条 90。强调 `#FA243C`、正文 `#1C1C1F`、次级 `#6E6E73`/`86868B`。

## WinUI3 铁律
- **秒崩（0xC000027B）**：Slider/ToggleSwitch 在 XAML 带初值+事件绑定 → 事件在 InitializeComponent 就地触发；handler 写同页后续控件 x:Name 字段（尚未解析=null）→ NRE。**新面板 ValueChanged/Toggled handler 必须加「面板就绪」守卫**（照 _audioFxPanelReady / _dspRackReady / _dspPowerReady）。
- **FontFamily 绝不能赋 null**：WinRT 转成字符串 `"Unknown"` → XAML 解析抛 COMException(0x800F1001)，**赋值点当场中断**（后面整段循环不执行，UI 空白、日志干净、极难查）。回落一律 `ClearValue(...)`。Brush 可以 null。
- **启动期「判定类 UI」必须在曲库定型后重算**（RestoreLastLibraryAsync 末尾 + 重新扫描末尾各重算一次）。
- DispatcherTimer 在 `Microsoft.UI.Xaml` 命名空间；`ObservableCollection<T>` 没有 `FindIndex`。

## 工程约定
- 规格显示必须真实（TagLib 偶返 AudioSampleRate=1，判据 IsPlausibleSampleRate>=8000）。bit-perfect：测量只读音频、只写标签，不动播放输出字节流。
- 推送：不 rebase / force-push / 不 `git add -A`；commit 独立可回退；构建过才推。用户不懂编程：**大白话结论先行**。
- **发版节奏**：**安装包只在用户明确说"发新版/发布"时才打。** 日常交付物 = `dist\run-latest\`（publish 全量拷 + _fix_runlatest_il.py + exe 冒烟）。
- **UI 文案口径**：除"播放内核"范畴外程序内不得出现 ECHO 字样（豁免：独占内核三选一卡片、关于页署名、渲染线程说明）。**新 UI 说明禁"一眼 AI"**——禁营销腔/朗诵腔/说明书式铺陈/每页开头导语；像人随手写的便签：短、直、口语。
- 同一文件并行 Edit 会丢：串行，改完 Grep 复核再构建。源码偶发被清零（前 4096 字节 NUL>0.5 判坏，从 git HEAD 恢复）；会话被打断先全仓扫 NUL。

## 构建与发布（详见 celeste-build-verify skill）
- **git 走代理 127.0.0.1:12001**，代理没开时 push/fetch 全失败。绕过：`git -c http.proxy= -c https.proxy= -c http.sslBackend=openssl push origin main`。反向实证：代理开着时直连反而 reset，两种都试。
- **publish 真实路径 = `CelesteMusicPlayer/bin/Release/net9.0-windows10.0.19041.0/win-x64/publish/`**。
- **判编译成功必须查三类**：`error CS` / `error MSB` / **`error : `（Roslyn 自身崩，无编号，只查 CS 会漏）**，并核对产物时间戳。XamlCompiler 退出码 1 是确定性失败，`-1073741819`/`139` 才是环境 flake 可重试。
- **MSB3021 = 文件锁（有实例在跑），不是代码问题** → 先杀进程。build 卡 7-8 分钟日志停在 restore = build-server 卡死 → `dotnet build-server shutdown` + `-nodeReuse:false`。
- **本项目无 ImplicitUsings/GlobalUsings**：新 .cs 用 SemaphoreSlim/CancellationTokenSource 须显式 `using System.Threading;`，用 Encoding 须 `using System.Text;`——照抄 MainWindow.Library.cs 的 using 块最稳。
- **⚠ 冒烟必须跑 exe 不能跑 dll**（`dotnet.exe xxx.dll` 静默死在 OnLaunched）。用户实例在跑时冒烟会被单实例挡掉（日志出现「已有实例在运行」= 这次冒烟没验证到任何东西）。
- UIA 验证脚本铁律：进 DSP 面板必须先点「音效处理」；`print(..., flush=True)` 别写进 `%` 元组括号内；判失败逻辑不许写恒真式。

## 沙箱环境限制
- **⚠ 本沙箱进程自身就是 Low IL**：新文件带 Low Mandatory Label，apphost dlopen hostfxr 必挂（`0x80070005`/秒退 `0x80008082`，日志零行）。正解：① `dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:CelesteSelfContainedDistribute=true`；② `python _fix_runlatest_il.py <目录>`（判据 ok=x/y fail=0 + VERIFY=OK）。⚠ 漏 `-p:CelesteSelfContainedDistribute=true` → exe 报 `You must install or update .NET`。
- **⚠ 鼠标注入被挡**：UIA 能读控件树、能 Invoke，Click/SetCursorPos 无效。**唯一交互路径 = UIA Invoke**。窗口类名 `WinUIDesktopWin32WindowClass`。单实例 taskkill 后立刻 Popen 会秒退，需 poll() 后二次拉起。
- **Edge 无头截图**（HTML 交付先自检）：`"/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --headless=new --disable-gpu --hide-scrollbars --window-size=1660,980 --virtual-time-budget=3000 --screenshot=<abs.png> "file:///C:/path/x.html"`，中文路径 URL 编码。
- 沙箱 Python 隔离环境 `C:\Users\MSI\.workbuddy\binaries\python\envs\default`（已装 pymupdf / pillow / numpy / mutagen）。
