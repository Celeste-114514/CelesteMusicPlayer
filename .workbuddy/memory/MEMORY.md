# CelesteMusicPlayer 项目长期记忆

> 只留跨会话有效事实。提交细节查 git log，当日细节查 memory/YYYY-MM-DD.md。

## 音频链路
源→ffmpeg PCM WAV 缓存（%LocalAppData%\CelesteMusicPlayer\TranscodeCache，2GB LRU）→OpenWaveSource（≤192MB 整读否则 8MB 流式）→SeamlessWaveProvider（同格式字节续接=gapless）→（独占）SRC→降混→ManagedDspSourceProvider（软音量链首→一次 celeste_dsp_process 进 celeste_dsp_core.dll；机架 EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→变速→电平表）→输出。**全关=不进 process=bit-perfect。**

## DSP 内核
- ⚠**内核进程级单实例**：CreateEngine 必须整段过 EngineSwapGate 全局锁；xunit 并行互毁引擎→EqRegression 整程序集串行。
- 内核=ECHO C++ 裁剪改写；native/echo-core/ 只服务独占输出、不在 DSP 链上。设计照搬 ECHO、代码不用他的（照 DspRackOrder.cpp:74-83）。许可：如实披露+风险自担（三处）。变速 processBlock 是空壳→宣传"内核已支持"是错的。
- **可视化口径铁律：先读内核源码再写。** 压缩=CompressorProcessor.cpp:160-175 逐行复刻、干湿在线性域；M/S=`sideDb+20log10(width/100)`；Crossfeed=one-pole；SRC 标「示意图·非实测」。
- 原生面板 12 模块/6 组导航；耳机校正无独立页（在「参数 EQ」页下半）。

## 极客皮肤
- 色源唯一：GeekPhosphorColor→PhosphorColor()。**极客严禁改全局资源（必崩 0xc000027b）**→元素级副本；树上 Slider/ToggleSwitch 须重建模板才认新值。
- 图标两套模式（一律白色）：Icon/Glyph。⚠**头号陷阱：跨模式共用的每秒定时器**（RehideAsciiIcons 曾藏掉所有 FontIcon）→加新模式先查所有"每秒自愈/补藏"逻辑放行。
- **文字字符当图标绝不能用等宽字体**（Consolas 缺字形）→`GeekShell.GeekGlyphFont`+17。新 UI 模块第一天接上色。

## WebUI（main/album/dsp.html，路线 B）
- 主体原生 XAML + WebView2 覆盖层；**网页只长样子不做事**，点击上报意图、C# 调现有播放方法，**音频链路零改动**。⚠`Themes\CelesteTokens.xaml` 必须 `<Page Remove>`（否则 XamlCompiler 静默崩只报退出码 1）。
- **play 语义铁律**：网页点行播放=整表替换队列+从该行起（`PlayAlbum(replacePlaylist:true)`）。
- **消息协议陷阱**：`WebMessageReceivedEventArgs` 无 `WebMessageAsString`，只有 `TryGetWebMessageAsString()`+`WebMessageAsJson`；`JsonSerializer.Deserialize` 默认区分大小写（小写 "kind" 映射不到 Kind→全量静默丢弃）→手动 `JsonDocument.Parse` 提 kind。收消息 handler 无条件记账。**「消息到了」≠「被处理」，每个静默 return 都要点亮。**
- `window.chrome.webview` 未注入完裸访问抛 TypeError→照 `whenWv`+`postQueue` 写。浏览器进程死后内核失效→Cleanup+Prewarm 拆旧建新。**沙箱内验不了渲染，只能无头 mock 自检+用户手测。**
- **⚠「CSS 写了≠生效」**——新样式类必须真挂 DOM，用 `_webcheck/` harness 验；注入 JS 别用 Python repr。**本模型读不了 PNG**→几何验收一律 `--dump-dom` 写文本再 grep。**headless Edge 画不出 fixed 覆盖层**→用独立小页验。
- **视图切换铁律**：独立容器只切 display，禁改 `#view` innerHTML；**必须 `#view.scrollTop=0`**（漏了=切歌页从第 301 行开始，BATCH=300）。
- **⚠ 后台分批回填必须预留占位高度**：chips 每波回填把行文字顶高 11px→用户看"一跳一跳"。修法：第三行 div 永远渲染+`.chips{height:19px}` 写死。**通用规则：事后插入 DOM 撑高的回填，首渲染占好坑。**
- **⚠ 全量重推防抖三件套（封面闪烁根因）**：ready 补发每次触发 C# `PushWebMainDataAsync`（`_webMainPushing` 只挡并发挡不住串行）→ `S.songs` 整对象替换→行重建丢封面→albumcovers 回填=封面字符之间跳。修法：① `case 'data'` 一到就 `clearInterval(readyTimer)` 停补发（首条丢了有 C# 页面自检兜底）；② `S.coverByAlbum`（专辑名小写→URL）跨重推存活，songRow/albumCard 回退查它；③ 回填加 `data-src` 守卫（同 URL 不重建 img，照 npCov 范本）。**探针量几何前确认元素非 display:none（矩形全 0）**。
- **OPRA 网页通道照搬原生铁律（用户第四次强调「试点页只换外观功能不变」）**：点曲线行=仅选中+预览（`opraseleq`），应用必须点「应用该曲线到均衡器」按钮；来源筛选=徽章点击 toggle（AutoEQ 徽章+来源徽章「点此只看这类/已筛选」）；C# `WebOpraPushEqs("")` 空 status=不动状态文字（原生选中曲线不写 OpraEqStatusText）；筛空才写提示（原生 ApplyOpraEqFilter 口径）。
- DSP 顶部空隙治法：量每行实际高度、砍空载行（#dspbar 开关并入 .dtop，栈高 119→84px），**光调 padding 治标**；副标题必须恒非空（「读取中…」兜底）。
- **原生 DSP 读数按页可见才轮询**→PushWebDspStateAsync 先自调 UpdateDspFieldReadouts/ChannelBars/SafetyChain/CompMeters 再读文本（**别调 RedrawDspFieldRing，收起时 ActualWidth=0 直接 return**）。
- **调原生 Click handler 两类**：纯动作可直接 `XxxButton_Click(this, new RoutedEventArgs())`；**弹 MenuFlyout 型不能照搬**（flyout 弹在网页底下）→换底层 core 方法或网页自绘菜单。
- **⚠ async 同步段陷阱**：`_ = PrewarmXxxAsync()` 第一个 await 前的代码同步跑 UI 线程→批次计算包 `await Task.Run(...)`。后台/UI 共碰缓存一律 ConcurrentDictionary；**数据代际 bump 只能发生在快照重推时**。
- 启动曲库：用户无会话文件→每次启动 AppendMediaFolderTracksAsync（曲库 `D:\天翼云盘同步盘\本地音乐`）；冷索引 5.6s/热 213ms。
- 上行通道：songact/albumact/artistact/plsongact/order/rate/favcur/showqueue/mini/lyrics/feat。正在播放页 `#npPage` 点播放条封面进；`now.path` 变先清 S.nowCover。布局：左封面+右列（信息→歌词 flex:1→npBottom 进度条+控制条）。
- 设计规格 `outputs/Apple风格界面-2026-10-09.html`：窗口 1592×906；顶栏 44；侧栏 228；行高 62；页头 98；播放条 90；强调 `#FA243C`。

## 皮肤系统·平行换肤页（批次 1 = winmain.html，2026-10-11 提交 c411a34）
- **平行页方案**：换肤=整页拷贝改 CSS（winmain：Segoe UI Variable Text/Fluent Icons/五处毛玻璃+Mica/4px 圆角/10px 开关/4px 轨），**DOM 与消息协议零改动**→C# 不复制推送逻辑，与 Apple 页共用 `_webMainOpen` 主开关；`_webWinOpen` 只记当前页；两入口互斥；`CloseWebMain()` 统一出口必须一并复位 `_webWinOpen`（漏了=再点入口被守卫挡掉，按钮失灵）。
- **⚠ 2026-10-11 起皮肤入口整体隐藏**（提交 781ace8，用户要求新版本不含皮肤系统）：MainWindow.xaml 三个 pill 入口整段注释（代码一行没删，注释内写明恢复法）；csproj 4 个 WebUI Content 行整块注释。**恢复皮肤系统要同时动两处**（XAML 三段 + csproj Content），去掉注释标记即可。
- `_webcheck` harness 页面维度：`WB_PAGE=<页>` 环境变量；产物后缀规则 **main/dsp 不加、平行页加 `_<页>`**（两套产物共存互不覆盖，老断言零改动）；dsp 场景只在 main 套跑且单独强制无后缀。字体可用性探针用 `document.fonts.check`（`window.fonts` 是 undefined）。np 歌词高亮：取最后一条 t<=pos 的原文行（场景 t=i*12、pos=65 → index 10=第 6 句）。

## WinUI3 铁律
- **秒崩 0xC000027B**：XAML 带初值+事件绑定→事件在 InitializeComponent 就地触发，handler 写后续控件 x:Name（未解析=null）→NRE。**新面板 ValueChanged/Toggled 必须加「面板就绪」守卫**（照 _audioFxPanelReady/_dspRackReady）。
- **托盘菜单滚动条（2026-10-11 实证，提交 341c46f）**：H.NotifyIcon 2.3.0 SecondWindow 弹菜单前 `MeasureFlyout` **冷测量**（项未套模板），`MenuFlyoutSeparator` 不钉高度实测 DesiredSize=(0,0)，真正渲染占 3px（1px 线+上下 Padding 各 1px）；窗口高 `round(scale×(4+Σ)+4)`，Σ 少算 6px → 窗口矮于内容 → ScrollViewer 弹滚动条。**修法：`new MenuFlyoutSeparator { Height = 3 }`**（上游 master/2.4.1 未修）。探针手法：`TaskbarIcon.ShowContextMenu(Point)` 公开可绕过鼠标注入直接弹；第二窗口透明 UIA 采不到→EnumWindows 转储本进程窗口矩形兜底。
- **FontFamily 绝不能赋 null**（WinRT 转 "Unknown"→COMException，赋值点当场中断、UI 空白日志干净）→回落 `ClearValue(...)`。Brush 可以 null。
- 启动期「判定类 UI」必须曲库定型后重算。DispatcherTimer 在 Microsoft.UI.Xaml；ObservableCollection 无 FindIndex。

## 工程约定
- 规格显示必须真实（TagLib 偶返 AudioSampleRate=1，判据 IsPlausibleSampleRate>=8000）。bit-perfect：测量只读音频、只写标签。
- 推送：不 rebase/force-push/不 `git add -A`；commit 独立可回退；构建过才推。**用户不懂编程：大白话结论先行。安装包只在用户明确说"发新版"时才打；日常交付=dist\run-latest\。**
- **UI 文案口径**：除"播放内核"范畴外不得出现 ECHO 字样。**新 UI 说明禁"一眼 AI"**——禁营销腔/朗诵腔/说明书式铺陈；像人随手写的便签：短、直、口语。
- 同一文件并行 Edit 会丢：串行，改完 Grep 复核再构建。源码偶发被清零（前 4096 字节 NUL>0.5 判坏，从 git HEAD 恢复）；会话被打断先全仓扫 NUL。

## 构建与发布（详见 celeste-build-verify skill）
- **git 走代理 127.0.0.1:12001**；代理没开时 push 全失败，绕过=`git -c http.proxy= -c https.proxy= -c http.sslBackend=openssl push origin main`；代理开着直连反而 reset，两种都试。
- publish 真实路径=`CelesteMusicPlayer/bin/Release/net9.0-windows10.0.19041.0/win-x64/publish/`。
- **判编译成功查三类**：`error CS`/`error MSB`/**`error : `（Roslyn 崩无编号）**+核对产物时间戳。XamlCompiler 退出码 1=确定性失败；-1073741819/139=环境 flake 可重试。MSB3021=文件锁（有实例在跑）先杀进程。build 卡 7-8 分钟停在 restore=build-server 卡死→`dotnet build-server shutdown`+`-nodeReuse:false`。
- **本项目无 ImplicitUsings**：新 .cs 用 SemaphoreSlim/CancellationTokenSource 须显式 `using System.Threading;`，Encoding 须 `using System.Text;`——照抄 MainWindow.Library.cs 的 using 块。
- **⚠ 冒烟必须跑 exe 不能跑 dll**；用户实例在跑时冒烟被单实例挡掉（日志「已有实例在运行」=没验证到任何东西）。UIA 验证：进 DSP 面板必须先点「音效处理」；判失败逻辑不许写恒真式。
- **沙箱进程自身 Low IL**：新文件带 Low Mandatory Label，apphost dlopen hostfxr 必挂（0x80070005/秒退 0x80008082 日志零行）。正解：①`dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:CelesteSelfContainedDistribute=true`；②`python _fix_runlatest_il.py <目录>`。漏 -p:CelesteSelfContainedDistribute=true → exe 报 You must install .NET。
- **IL 标签认知反转（2026-10-11）**：本机 **EnableLUA=0（UAC 关）→ explorer 跑 High IL**，给文件打显式 Medium 反而被 UIPI 挡掉 Shell_NotifyIcon（托盘图标必丢）→**「无标签」才是正确状态**；`_fix_runlatest_il.py` 已改逻辑只在确实是 Low 时才补 Medium。且本次沙箱写出的 791 个产物文件全部无 Low 标签（与 2026-09-30「沙箱产物必中招」旧结论不同）→ **判 IL 以 icacls 实查为准，别信旧结论**。
- **裸 `python` 在后台 shell 跑脚本会段错误**（805ms 即崩，无任何输出）→ 用 `/c/Users/MSI/.workbuddy/binaries/python/envs/default/Scripts/python.exe`（带 uiautomation）或 `versions/3.13.12/python.exe`。
- **鼠标注入被挡**：UIA 能读控件树/Invoke，Click 无效。窗口类名 `WinUIDesktopWin32WindowClass`。单实例 taskkill 后立刻 Popen 会秒退，需 poll() 后二次拉起。
- **UIA 走树三坑（2026-10-10 实测定论）**：① uiautomation 库 `ControlTypeName` 返回值**带后缀**——判等写 `"Button"` 永远不中，要 `in ("Button","ButtonControl")`；② WinUI 图标按钮自身 AutomationName 常为空、可访问名在**子级 TextControl** 里（「Apple 风格界面」pill：Button name='' + 子 Text 带文案）→ 按钮「有效名」=自身 Name+全部子孙 TextControl 拼接后再匹配；③ 从桌面根 searchDepth=20 搜不到深树里的按钮，必须从 `WindowControl(ClassName="WinUIDesktopWin32WindowClass", searchDepth=3)` 起栈式 `GetChildren()` 逐层下探。
- **「有没有活实例」权威判据 = tasklist 进程在不在**：`.running` 被 taskkill 杀过的实例会留下陈旧文件（文件在≠活着）；日志 mtime 不能当判据（空闲实例不写日志）；app 日志「启动」行时间会把上次测试残骸当活实例（误报过两轮）。Git Bash 里 `tasklist /FI` 的 /FI 被 MSYS 吞成路径 → 必须 `MSYS_NO_PATHCONV=1` 前缀。
- **混合改动拆两个 commit 的安全手法**（改名+守卫混在同文件时）：临时 revert B 类改动 → add+commit A → 原样重新 apply B → **sha256 比对恢复后与 revert 前逐一一致** → commit B。字节一致 = 之前构建交付的产物依然对应当前 HEAD，不必重新构建。
- **入口命名拍板（2026-10-10，提交 4bbaf2e）**：Apple 试点入口用户可见字样=「Apple 风格界面」（两个 pill 按钮 Text/ToolTip、album.html title、日志前缀 `[Apple试点]`）；「新版界面」一名预留给将要**平行新建**的 Windows 风试点页（照现有消息协议新建 HTML+入口，Apple 三页一行不动）。⚠ XAML 字面量不进 dll、进 `CelesteMusicPlayer.pri`——二进制验证扫 .pri。
- **⚠ publish 快照信任铁律（2026-10-11 事故）**：① fd publish（`-o dist\publish-fd`）会顺手覆盖 `bin\...\win-x64\publish` 里的 exe/dll/pri——**fd/sc 两种 publish 不能假定共用中间目录后输出还干净**；② 快照复制保留文件 mtime，`dist/publish-sc` 曾是 26.10.2.0 旧货却顶着 16:07 的当天时间戳。**任何 publish 快照进打包/交付前必须冒烟验标题版本号，mtime/md5 都证明不了「是不是这一版」**。重出 sc：`_build_release_sc.py`（Roslyn 0xC0000005 flake 重试即过）。
- **GitHub Release 发布链（本机无 gh CLI）**：token=`printf "protocol=https\nhost=github.com\n\n" | git credential fill` 取 password；curl/urllib 直连 api.github.com（不用代理）。坑：`target_commitish` 传短 SHA 报 422 invalid，tag 已推远程时**不传该字段**。两步法：POST releases（make_latest=false）→ 上传资产到返回的 upload_url（uploads.github.com，直连）→ PATCH make_latest=true。资产=fd+sc 两个 Setup exe + SHA256SUMS.txt。makensis `compress() failed [-4]`/exit=-1073741819 是环境 flake，重跑即过（installer 脚本零变更时先怀疑环境）。
- **发版 commit 模式**（照 v26.10.2/26.10.3）：README 更新日志条目单独 commit（版本号文件若已提交则只提 README）→ 打**附注 tag** → push main+tag → 建 release。release notes 落 `dist/release-notes-v<版本>.md`（dist/ 不进 git）。
- Edge 无头：`"/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --headless=new --disable-gpu --hide-scrollbars --window-size=1660,980 --virtual-time-budget=3000 --screenshot=<abs.png> "file:///..."`，中文路径 URL 编码。沙箱 Python 隔离环境 `C:\Users\MSI\.workbuddy\binaries\python\envs\default`。
