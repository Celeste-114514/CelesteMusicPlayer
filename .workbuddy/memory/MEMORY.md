# CelesteMusicPlayer 项目长期记忆

> 只留跨会话有效事实。提交细节查 git log；当日细节查 memory/YYYY-MM-DD.md；构建发布见 celeste-build-verify skill。

## 音频链路与 DSP 内核
- 链路：ffmpeg PCM WAV 缓存（2GB LRU）→OpenWaveSource→SeamlessWaveProvider（gapless）→独占 SRC→降混→ManagedDspSourceProvider→一次 celeste_dsp_process 进 celeste_dsp_core.dll（机架 EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→变速→电平表）。**全关=bit-perfect**；测量只读音频、只写标签。
- ⚠内核进程级单实例：CreateEngine 整段过 EngineSwapGate 全局锁。内核=ECHO C++ 裁剪改写（设计照搬、代码不用他的；许可=如实披露+风险自担）。UI 文案除「播放内核」外禁出现 ECHO；变速 processBlock 是空壳，禁宣传「内核已支持」。
- **独占内核**：2026-10-11 起默认 native2、删自研（self）选项（存量设置 Normalize 归一 native2；echo 保留；self 仅存读取失败兜底）。**用户已实机自测 native2 通过**（2026-10-11 拍板，勿再催测）。
- 可视化铁律：先读内核源码再写（压缩=CompressorProcessor.cpp:160-175 逐行复刻；M/S=sideDb+20log10(width/100)；Crossfeed=one-pole；SRC 标「示意图·非实测」）。

## WinUI3 铁律
- 秒崩 0xC000027B：XAML 带初值+事件绑定→InitializeComponent 就地触发→handler 写后续 x:Name（null）→NRE。**新面板 ValueChanged/Toggled 必加「面板就绪」守卫**。临时下架带 x:Name 控件用 Collapsed，禁注释（=CS0103）。
- FontFamily 禁赋 null（COMException）→ClearValue。托盘分隔线钉 `new MenuFlyoutSeparator{Height=3}`（H.NotifyIcon 冷测量 DesiredSize=(0,0) 实占 3px，不钉弹滚动条，上游未修）。启动期判定类 UI 必须曲库定型后重算。

## 皮肤 / WebUI
- **皮肤入口 2026-10-11 起整体隐藏**（用户要求新版本不含）：MainWindow.xaml 三个 pill + csproj 4 个 Content 行都注释；SettingsWindow.xaml「主题文件」区两处 Collapsed（95005a9，恢复=删两处 Visibility）。**恢复必须三处一起去**。TokenSkin WIP 未提交（MainWindow.TokenSkin.cs + FrostedGlass/CelesteTheme/Misc/Playback3/UiTheme 5 文件，与 native2 改动文件不重叠；备份 /tmp/CelesteTokSkin_WIP_20261011，TEMP 重启即失）。
- 极客：色源唯一 GeekPhosphorColor；严禁改全局资源（必崩）→元素级副本；跨模式共用的每秒定时器是头号陷阱；文字当图标禁用等宽字体→GeekGlyphFont+17。播放队列三处图标 RedEye(E7B3)→List(EA37) 已改（b1b6f01，进 26.10.11 包）。
- WebUI 路线 B：网页只长样子不做事，音频链路零改动。铁律：点行播放=整表替换队列；消息手动 JsonDocument.Parse 提 kind（默认区分大小写会静默全丢）；CSS 写了≠生效（_webcheck 验，几何 --dump-dom 禁读 PNG）；视图切换只切 display 且必须 scrollTop=0；后台回填先占坑；重推防抖三件套（clearInterval(readyTimer)、coverByAlbum 跨推存活、data-src 守卫）。

## 工程约定
- 用户不懂编程：大白话结论先行，禁营销腔。bit-perfect 铁律。推送：不 rebase/force-push、不 git add -A；commit 独立可回退；构建过才推。安装包只在用户说「发新版」时打；日常交付=dist\run-latest\。
- **版本号=日期命名**：v26.10.3 实际 10-11 发布却编成 26.10.3.0（应 26.10.11），三处皆错。**发版前必须 git log <上tag>..HEAD 逐条过完再写日志**（26.10.3 只写 2 条实际 15 个提交，被用户抓包）。
- 同一文件并行 Edit 会丢：串行，改完 Grep 复核再构建。源码偶发被清零（前 4096 字节 NUL>0.5 判坏，从 git HEAD 恢复）；会话被打断先全仓扫 NUL。

## 构建与发布（详见 celeste-build-verify skill）
- dotnet 必带 env 前缀补 APPDATA/SystemRoot 等。判成功三类全零：error CS/error MSB/error :（Roslyn 崩）+产物时间戳。**Roslyn 崩后的下次 XamlCompiler exit 1 是中间态污染，重试即过**（2026-10-11 三连实证）。本项目无 ImplicitUsings：新 .cs 用 SemaphoreSlim 须显式 using System.Threading;——照抄 MainWindow.Library.cs。
- 冒烟必须跑 exe；用户实例在跑会被单实例挡掉（「已有实例在运行」=没验证到）。活实例判据=tasklist（MSYS_NO_PATHCONV=1）。
- **IL**：本机 UAC 关→explorer High IL，显式 Medium 反丢托盘图标→**「无标签」才是正确状态**；判 IL 以 icacls 实查为准。
- **publish 快照信任铁律**：mtime/md5 证明不了版本，进包前必须冒烟验标题版本号；fd publish 会覆盖 bin 中间目录。GitHub Release（无 gh CLI）：token=git credential fill；直连 api.github.com；tag 已推时不传 target_commitish；POST→上传 upload_url→PATCH make_latest=true。makensis flake 重跑即过。
- **CI 自动发布链（2026-10-11 实证）**：push main/tag `v*` 均触发 ci.yml，tag 运行自动打包并建 release（资产名 CelesteMusicPlayer-Setup-<ver>.exe / -SC-）。**本地构建只作冒烟，发版走 CI**。撤发版=API DELETE /releases/{id} + /git/refs/tags/{tag}（都回 204），再推新 tag 让 CI 重建（v26.10.3 撤了重发 v26.10.11，全流程通）。
- 裸 python 后台跑会段错误→用 binaries/python 全路径。UIA 三坑：ControlTypeName 带后缀；图标按钮有效名=自身+子孙 TextControl 拼接；Collapsed 不在 UIA 树（「找不到」恰是隐藏的通过信号）。换 Segoe MDL2 字形前先验 cmap codepoint（EA37=List、E7B3=RedEye 已验证）。
