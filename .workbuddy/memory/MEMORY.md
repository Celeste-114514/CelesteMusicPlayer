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
- DSP 顶部空隙治法：量每行实际高度、砍空载行（#dspbar 开关并入 .dtop，栈高 119→84px），**光调 padding 治标**；副标题必须恒非空（「读取中…」兜底）。
- **原生 DSP 读数按页可见才轮询**→PushWebDspStateAsync 先自调 UpdateDspFieldReadouts/ChannelBars/SafetyChain/CompMeters 再读文本（**别调 RedrawDspFieldRing，收起时 ActualWidth=0 直接 return**）。
- **调原生 Click handler 两类**：纯动作可直接 `XxxButton_Click(this, new RoutedEventArgs())`；**弹 MenuFlyout 型不能照搬**（flyout 弹在网页底下）→换底层 core 方法或网页自绘菜单。
- **⚠ async 同步段陷阱**：`_ = PrewarmXxxAsync()` 第一个 await 前的代码同步跑 UI 线程→批次计算包 `await Task.Run(...)`。后台/UI 共碰缓存一律 ConcurrentDictionary；**数据代际 bump 只能发生在快照重推时**。
- 启动曲库：用户无会话文件→每次启动 AppendMediaFolderTracksAsync（曲库 `D:\天翼云盘同步盘\本地音乐`）；冷索引 5.6s/热 213ms。
- 上行通道：songact/albumact/artistact/plsongact/order/rate/favcur/showqueue/mini/lyrics/feat。正在播放页 `#npPage` 点播放条封面进；`now.path` 变先清 S.nowCover。布局：左封面+右列（信息→歌词 flex:1→npBottom 进度条+控制条）。
- 设计规格 `outputs/Apple风格界面-2026-10-09.html`：窗口 1592×906；顶栏 44；侧栏 228；行高 62；页头 98；播放条 90；强调 `#FA243C`。

## WinUI3 铁律
- **秒崩 0xC000027B**：XAML 带初值+事件绑定→事件在 InitializeComponent 就地触发，handler 写后续控件 x:Name（未解析=null）→NRE。**新面板 ValueChanged/Toggled 必须加「面板就绪」守卫**（照 _audioFxPanelReady/_dspRackReady）。
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
- **鼠标注入被挡**：UIA 能读控件树/Invoke，Click 无效。窗口类名 `WinUIDesktopWin32WindowClass`。单实例 taskkill 后立刻 Popen 会秒退，需 poll() 后二次拉起。
- Edge 无头：`"/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --headless=new --disable-gpu --hide-scrollbars --window-size=1660,980 --virtual-time-budget=3000 --screenshot=<abs.png> "file:///..."`，中文路径 URL 编码。沙箱 Python 隔离环境 `C:\Users\MSI\.workbuddy\binaries\python\envs\default`。
