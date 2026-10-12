# CelesteMusicPlayer 项目长期记忆

> 只留跨会话有效事实。提交细节查 git log；当日细节查 memory/YYYY-MM-DD.md；构建发布见 celeste-build-verify skill。

## 音频链路与 DSP 内核
- 链路：ffmpeg PCM WAV 缓存（2GB LRU）→OpenWaveSource→SeamlessWaveProvider（gapless）→独占 SRC→降混→ManagedDspSourceProvider→一次 celeste_dsp_process 进 celeste_dsp_core.dll（机架 EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→变速→电平表）。**全关=bit-perfect**；测量只读音频、只写标签。
- ⚠内核进程级单实例：CreateEngine 整段过 EngineSwapGate 全局锁。内核=ECHO C++ 裁剪改写（设计照搬、代码不用他的；许可=如实披露+风险自担）。UI 文案除「播放内核」外禁出现 ECHO；变速 processBlock 是空壳，禁宣传「内核已支持」。
- **独占内核**：2026-10-11 起默认 native2、删自研（self）选项（存量设置 Normalize 归一 native2；echo 保留；self 仅存读取失败兜底）。**用户已实机自测 native2 通过**（2026-10-11 拍板，勿再催测）。
- 可视化铁律：先读内核源码再写（压缩=CompressorProcessor.cpp:160-175 逐行复刻；M/S=sideDb+20log10(width/100)；Crossfeed=one-pole；SRC 标「示意图·非实测」）。

## WinUI3 铁律
- **新增界面模式/枚举值的落地三连（2026-10-12 血坑）**：① 设置白名单（`AppSettingsStore.Normalize` 的 `is "Scene" or "Classic*"` 链）② UI 选择项（`SettingsWindow.xaml` 的 `ComboBoxItem Tag`）③ 应用分支（`ApplyUiStyleMode`）。**缺任何一项都是"静默不生效"**——TokenSkin 写了 241 行却因为白名单没放行，读回来被归一成 ClassicSystem，`IsTokenSkinActive` 恒 false，表面上"代码全对但不工作"。
- **取色只认一个入口（2026-10-12 血坑）**：强调色有两套实现——`ThemeColorService.CurrentAccent`（直通系统强调色）与 `ResolveAccentBrush()`/`ResolveAccentColor()`（带极客/令牌皮肤分支）。**新 UI 元素必须走 resolver 那套**，抄近道用 CurrentAccent 的结果就是「别处都跟主题变了，就这一块不变」（用户实测报的音量条即此）。静态上下文用 `MainWindow.ResolveAccentColorStatic()`（2026-10-12 新增）。
- **按钮/圆底前景色恒白**（2026-10-12 用户拍板）：`ColorHelper.ResolveContrastingForeground` 不再按背景亮度切黑/白，一律白色；未来皮肤设置的开关落在 `ColorHelper.OverrideContrastForeground`（非 null 优先）。
- **二级窗口并入主 UI 的落地模式（2026-10-12 定）**：底座 = `MainWindow.ShellPages.cs` 的 `ShellPageHost`
  （左子导航+右内容+强调条+徽章，视觉对齐 DspNav）。**强调色必须外部注入**（传 `() => DspAccentBrush()`），
  框架自己绝不读资源。并入三步：① 功能先内联到主界面（弹窗→内联字段条）② 确认无问题再摘旧窗口入口
  ③ 最后才删窗口代码。**音效处理的 `DspNav` 是专用实现，暂不迁移**（等新框架站稳）。
- **二级窗口并入主 UI 的标准手法（2026-10-12 阶段 3 定型）**：「机械搬运」不重写业务——脚本读原窗口文件按锚点切块，
  只做 6 项必需替换（Window→UserControl / 删标题栏与 AppWindow 系列 / Activated→Loaded + Closed→Unloaded /
  ApplyBackdropFromSettings 清空 / 「保存并关闭」→「保存」+ 删 Close() / 窗口静态方法留空实现兼容旧调用点）。
  ⚠ 搬运前先 `grep -rln "partial class X"` 列全分部文件（SettingsWindow 有 3 个），**只有一处能写 `: UserControl`**，
  其余必须去掉基类。旧窗口代码保留、只摘入口（可一行接回）。产物：SettingsPanelControl / FileAssociationPanelControl。
- **设置面板加一个分类 = 4 处**：导航项 + 内容 ScrollViewer + `ShowPanel` 一行 + `_searchPanels` 一行。
- **XAML+cs 必须同 commit**：XAML 事件 handler 与 cs 方法强耦合，拆两个 commit 会让 revert 任一半编译不过
  （2026-10-12 实测）。各阶段归属靠 XAML 注释标注。
- **XamlCompiler 原生崩 exit 1/0xC0000005 别重试，直接二分**（2026-10-12 实证）：两类诱因——
  ① `Border` 塞多个子元素（Canvas+Path+Ellipse+Rectangle 叠加层）不报错直接崩，**多元素叠加一律 Grid**
  （原 ArtistAvatarEditorWindow 的 CropViewport 本就是 Grid）；② `ItemsWrapGrid` 属性名是
  `MaximumRowsOrColumns`，写错也崩。固定排查：ET.parse 验良构 → 查 BOM → `_build_check.py`
  （Release/win-x64；Debug 不重编 XAML 会假过）。另：Bash 直调 `dotnet publish` 输出会被吞
  （0 字节、秒退 exit 0、产物不更新），dotnet 长命令走 python subprocess（_build_check.py 同款 env）。
- 秒崩 0xC000027B：XAML 带初值+事件绑定→InitializeComponent 就地触发→handler 写后续 x:Name（null）→NRE。**新面板 ValueChanged/Toggled 必加「面板就绪」守卫**。临时下架带 x:Name 控件用 Collapsed，禁注释（=CS0103）。
- FontFamily 禁赋 null（COMException）→ClearValue。托盘分隔线钉 `new MenuFlyoutSeparator{Height=3}`（H.NotifyIcon 冷测量 DesiredSize=(0,0) 实占 3px，不钉弹滚动条，上游未修）。启动期判定类 UI 必须曲库定型后重算。

## 皮肤 / WebUI
- **皮肤入口隐藏的范围（2026-10-12 澄清，别再以讹传讹）**：2026-10-11 起隐藏的只是**主界面上的皮肤快捷 pill**（MainWindow.xaml 三处注释，95005a9 同批）；**设置→外观→「界面风格→测试皮肤」+「主题文件」入口自 2026-10-12 阶段 1（086b566/49a2ff6）起是活的**，用户实测可选。早前"皮肤入口整体隐藏"的说法只对主界面快捷入口成立，设置里的路一直通——对外汇报时不许再说"整体隐藏"。
- **智能播放列表（④）= 已作废**：2026-08-30 22:45 提交（8c96f92 Auto-DJ 规则生成），**当天 23:50 按用户确认移除（6250df5）**，代码不存在。云记忆里"代码已完成未验证"是 8-30 旧状态，作废。
- **设备记忆 DSP 配置档（③）= 活着**：2026-08-30 62d948d，插上设备自动套用该设备存过的 DSP 配置；设置开关 `DeviceDspMemoryToggle`，接线在 MainWindow.Features.cs（2915/3192/3214）。八个星期前的事，用户自己不记得，别当成"待办"。
- 极客：色源唯一 GeekPhosphorColor；严禁改全局资源（必崩）→元素级副本；跨模式共用的每秒定时器是头号陷阱；文字当图标禁用等宽字体→GeekGlyphFont+17。播放队列三处图标 RedEye(E7B3)→List(EA37) 已改（b1b6f01，进 26.10.11 包）。
- 皮肤系统的阶段 1「令牌测试皮肤」已合闸（2026-10-12，commit 086b566 + 49a2ff6）：设置→外观→界面风格「测试皮肤」+ 主题文件「演示暖橙」，Classic*/Token 互切热生效不用重启。验证口径、演示主题文件、没跑实机冒烟的原因见 2026-10-12.md。
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
- **日常交付 run-latest 必须自包含发布（2026-10-12 用户实测弹「下载 .NET」）**：csproj 把 `SelfContained` 绑在 `CelesteSelfContainedDistribute` 开关上，**不传 = 框架依赖**（约 86 文件，用户机器没装 .NET 直接弹下载提示；开发机有 SDK 所以冒烟假过）。正确命令：`dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:WindowsPackageType=None -p:CelesteSelfContainedDistribute=true -p:EnableSourceControlManagerQueries=false`（约 625 文件，拷走就能跑）。⚠ 混合目录坑：fd 产物覆盖进旧自包含 run-latest 后，残留的旧 hostfxr.dll/hostpolicy.dll 会把运行时解析带歪——**换发布形态前先把 dist\run-latest 挪走/清空再全量拷**。另：dotnet publish 长命令走后台任务或 python subprocess，回合边界会杀前台长命令（半成品 170 文件就是这么来的）。⚠ **publish 偶发 `Fatal error 0xC0000005` 崩在 `Microsoft.Build.Tasks.Git.GitConfig.Reader`**（SourceLink 读 git 配置，环境偶发非代码问题，_build_check.py 不受影响）：**加 `-p:EnableSourceControlManagerQueries=false` 即过**（2026-10-12 实测，重试原命令也崩，加开关立刻好）。
- **CI 自动发布链（2026-10-11 实证）**：push main/tag `v*` 均触发 ci.yml，tag 运行自动打包并建 release（资产名 CelesteMusicPlayer-Setup-<ver>.exe / -SC-）。**本地构建只作冒烟，发版走 CI**。撤发版=API DELETE /releases/{id} + /git/refs/tags/{tag}（都回 204），再推新 tag 让 CI 重建（v26.10.3 撤了重发 v26.10.11，全流程通）。
- 裸 python 后台跑会段错误→用 binaries/python 全路径。UIA 三坑：ControlTypeName 带后缀；图标按钮有效名=自身+子孙 TextControl 拼接；Collapsed 不在 UIA 树（「找不到」恰是隐藏的通过信号）。换 Segoe MDL2 字形前先验 cmap codepoint（EA37=List、E7B3=RedEye 已验证）。
