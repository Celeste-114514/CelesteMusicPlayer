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
- **主题判断必须从元素树解析，禁读 App 级 RequestedTheme**（2026-10-12 用户实测踩坑）：外壳实际主题是 `UiTheme.ApplyUiStyleMode` 在 Content 根上显式设置的（经典浅色=ElementTheme.Light），`Application.Current.RequestedTheme` 只是 App 级默认值，两者会不一致（经典浅色下页签字被赋成白色、悬停反而变黑）。正确姿势=`MainWindow.ArtistDetail.cs` 的 `IsLightThemeFor(元素)`：沿视觉树向上找第一个显式 Light/Dark 的祖先，全 Default 才回落 App 级。
- **网易云老 API 已被风控，艺术家概览走酷狗**（2026-10-12 实测定位）：`music.163.com/api/search/get`+`artist/head/info/get` 对程序化访问返回 `{"code":-462}`（滑块验证页，HTTP 200 无 artists=解析出 0 命中）；UA 带「CelesteMusicPlayer/1.0」标识必被拦、标准浏览器 UA 能过但连发后 IP 被标记（curl 也概率性中招）。QQ 搜索可用但简介接口已废弃（no supply）；iTunes/维基本机不可达。**可用源=酷狗 mobilecdn**（`api/v3/search/singer`→singerid→`api/v3/singer/info`→`data.profile`，中文简介无风控，Referer 要发 kugou.com）。HttpClient UA 已换标准浏览器 UA（WAF 甄别 UA 自定义 token）；概览链路=酷狗优先+网易云兜底（0 命中退避 1.2s 重试）。
- **⚠️ obj/bin 备份禁止留在项目目录里**（2026-10-12 新血坑）：csproj 无显式 Compile 配置，SDK 默认 glob `**/*.cs` 会把 `_bak_obj_x64/.../MainWindow.g.i.cs` 一起编译 → 满屏假 CS0102/CS0111，还伴随 XamlCompiler/Roslyn 原生崩（中间态污染）。「移走 obj 重编」的备份一律放仓库外。
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
- WebUI 路线 B：网页只长样子不做事，音频链路零改动。铁律：点行播放=整表替换队列；消息手动 JsonDocument.Parse 提 kind（默认区分大小写会静默全丢）；CSS 写了≠生效（_webcheck 验，几何 --dump-dom 禁读 PNG）；视图切换只切 display 且必须 scrollTop=0；后台回填先占坑；重推防抖三件套（clearInterval(readyTimer)、coverByAlbum 跨推存活、data-src 守卫）。⚠ **winmain.html 有两套图标表**（SVG path 版 + Segoe 字形版如 `TagSort:'\uE8CB'`），main.html 只有 SVG 那套——增删导航图标两文件都要改、winmain 要改两处。
- **标签排序 2026-10-12 整体删除**（d0d7ab6，用户「一直不满意」待重构；25 文件 5788 行，含 10 个专属文件）。侧栏留了墓碑注释，代码全在 git 历史里，**恢复=revert d0d7ab6**。别再把它当现存功能汇报。
- **令牌皮肤会覆写全局文字刷子（2026-10-12 用户实测坑）**：TokenSkin.cs 的 TokenBrushMap 把 `TextFillColorPrimaryBrush`/`TextFillColorSecondaryBrush` 全局覆写成令牌色（为深色皮肤设计）——浅色主题下任何读这两个资源的 UI 文字都会变色/看不见。**新增文字色元素要么按 RequestedTheme 给固定色，要么确认令牌色在两个主题下都可读**；别默认「ThemeResource 会自动适配」。
- **艺术家详情页右栏结构（2026-10-12 第三轮定稿）**：页签只有「歌曲/专辑」两个（方案B 文字页签+下划线，ApplyTabChrome 按 RequestedTheme 给固定黑/白）；**头像不是页签**，是按需打开的面板（`_artistAvatarOpen` + `CloseArtistAvatarPanel()` 幂等，右键进入时页签条整条隐藏、面板「← 返回」收回）。
- **publish 连崩先查嵌套三元（2026-10-12 新血坑）**：二元嵌套三元 `a ? (b ? x : y) : (b ? z : w)` 会触发 Roslyn NullableWalker 深递归，`dotnet publish` 偶发原生崩 0xC0000005 连崩不止（`dotnet build`/_build_check 反而不崩，疑似编译线程池栈更小）。拆成局部变量即过。别再对 publish 连崩一味重试。
- **二级窗口现状（2026-10-13 再盘点）**：仍活跃=迷你播放器/标签编辑/歌词编辑/重复文件 4 个（在线搜索窗已于 27ec416 整体删除）；已不可达（代码在、入口全摘）=当前播放列表/播放队列/均衡器（EQ 已入 DSP 机架）/房间校正/RG 扫描/设置/文件关联/头像两窗/取色器（主 UI 已用 InlineColorPicker）/CustomSortOrder（纯死代码）。下次要清理窗口代码时按这个清单来。

- ⚠ **删代码别按行号整段删**（2026-10-13 血坑）：想删的多个方法之间往往夹着别的方法
  （本例删 347–608 误伤了 `ClearDetail()` 与 `WebPlayButton_Click()`）。用**方法签名定位**逐段删，
  删完立刻 `_build_check.py` 验证到 BUILD_OK。XamlCompiler 崩时先 ET.parse 验 XAML 良构，
  再考虑移走 `obj/<平台>`+`bin/<平台>`（备份必须放仓库外）全量重建。

## 合规整改（2026-10-13 进行中）
- 用户拍板：在线功能**只留 MusicBrainz 系**（MusicBrainz + Cover Art Archive + Last.fm）。
  网易云/QQ/酷狗/iTunes（含 `music.apple.com` 网页抓取）全部删除。
- 接入门槛（已核实）：MusicBrainz/CAA/LRClib/Wikidata **零门槛**（MB 限时速 1 req/s + 强制 User-Agent）；
  Discogs/Last.fm/fanart.tv 需**本人注册拿 key**（免费）；AcoustID 商业用途需付费，仓库公开慎用。
- 歌词换 **LRClib**（`lrclib.net/api`，免费无 key），艺术家简介换 **Wikidata**。
- 进度：阶段 0（删音频直链下载）9c40183 → 阶段 1/2（删网易云/QQ/酷狗源）f058aeb 等 → 阶段 3（歌词改 LRClib）1328c77 → 阶段 4（在线搜索窗整体下架 + 设置页改「在线服务」）27ec416。⚠ 27ec416 只 `git rm` 了窗口文件没摘引用，`MainWindow.Features.cs`×2 + `MainWindow.WebMain.cs`×1 三处 `OnlineSearchWindow` 悬挂引用让该提交编译不过（工作树修改即修复，收尾提交补上）。
- ⚠ **`git rm` 删文件与摘引用必须同一个提交**（2026-10-13 27ec416 实证）：只删文件不摘引用=那个提交自身编译不过，revert 它也回不到干净状态。
- **阶段 5a（标签板块骨架）已完成**（2026-10-13，构建 BUILD_OK + UIA 冒烟 10/10）：主界面导航新增「🏷️ 标签」（`NavTagEditButton`，Tag="TagEdit"），`TagEditBorder` 内挂 `TagPanelControl`（新增 `TagPanelControl.xaml`/`.cs`，`ShellPageHost` 四子页：快速编辑/批量工具/排序编号/在线补全，后三个是占位）。右栏文件表格复用 `PlaylistItem` 行模型；添加文件/文件夹走 WinRT 选取器（`WindowNative.GetWindowHandle(this)`+`InitializeWithWindow`），后台每批 50 异步加载带去重；左栏选中预览=封面+路径+9 个只读标签框（`TagEditorService`）。accent 经 `() => DspAccentBrush()` 外部注入，主题切换 `ReapplyTagPanelAccent()`。**编辑/保存/改动前快照回滚是阶段 6**；旧 `TagEditorWindow` 入口重定向+删窗也在阶段 6（入口清单：MainWindow.Misc.cs:1384、Features.cs:1626/3373、Library2.cs:1154、Settings.cs:196、WebMain.cs:586/701、SettingsPanelControl.xaml.cs:3913、SettingsWindow.xaml.cs:3709）。

## 工程约定
- 用户不懂编程：大白话结论先行，禁营销腔。bit-perfect 铁律。推送：不 rebase/force-push、不 git add -A；commit 独立可回退；构建过才推。安装包只在用户说「发新版」时打；日常交付=dist\run-latest\。
- **版本号=日期命名**：v26.10.3 实际 10-11 发布却编成 26.10.3.0（应 26.10.11），三处皆错。**发版前必须 git log <上tag>..HEAD 逐条过完再写日志**（26.10.3 只写 2 条实际 15 个提交，被用户抓包）。
- 同一文件并行 Edit 会丢：串行，改完 Grep 复核再构建。源码偶发被清零（前 4096 字节 NUL>0.5 判坏，从 git HEAD 恢复）；会话被打断先全仓扫 NUL。
- **工作树未跟踪文件盘点（2026-10-13，588 项约 6.4 G，用户已听过清单、未授权删除）**：`.gitignore` 只盖 bin/obj/publish/*.log，所以各阶段现场全裸奔。**动不得**：`dist/run-latest`（在用的日常交付）、`native/echo-core/build/`（csproj 直接引用其 DLL，删了构建断）、`_backup_tagsort_pre_revert/`（标签排序 revert 备份）、`.workbuddy/`。**可清**：`_wv2_cachebak`/`_binbak_*`/`_objbak_*`/`_th_cachebak`/`_bin_verify`/`_syncheck`/`_iobj`（10 月初构建调试备份约 1.3 G）、`111/`（误存 QQ 截图）、`.reasonix/`（旧 AI 残留）、`@AutomationLog.txt`、`_xc*.txt`、根目录约 200 个 `_*.py/_*.sh` 调试脚本与截图、`commit_msg_*.txt`。**待定**：`tools/`（EqRegression 805 M 音频样本等诊断工具）、`_webcheck/`（WebUI 验证快照 124 html+76 png）、`output/outputs/`。`dist/` 里除 run-latest 外约 2.4 G 过期发布副本也可清。删除前必须列清单获用户确认。

## 构建与发布（详见 celeste-build-verify skill）
- dotnet 必带 env 前缀补 APPDATA/SystemRoot 等。判成功三类全零：error CS/error MSB/error :（Roslyn 崩）+产物时间戳。**Roslyn 崩后的下次 XamlCompiler exit 1 是中间态污染，重试即过**（2026-10-11 三连实证）。本项目无 ImplicitUsings：新 .cs 用 SemaphoreSlim 须显式 using System.Threading;——照抄 MainWindow.Library.cs。
- 冒烟必须跑 exe；用户实例在跑会被单实例挡掉（「已有实例在运行」=没验证到）。活实例判据=tasklist（MSYS_NO_PATHCONV=1）。
- **IL**：本机 UAC 关→explorer High IL，显式 Medium 反丢托盘图标→**「无标签」才是正确状态**；判 IL 以 icacls 实查为准。
- **publish 快照信任铁律**：mtime/md5 证明不了版本，进包前必须冒烟验标题版本号；fd publish 会覆盖 bin 中间目录。GitHub Release（无 gh CLI）：token=git credential fill；直连 api.github.com；tag 已推时不传 target_commitish；POST→上传 upload_url→PATCH make_latest=true。makensis flake 重跑即过。
- **日常交付 run-latest 必须自包含发布（2026-10-12 用户实测弹「下载 .NET」）**：csproj 把 `SelfContained` 绑在 `CelesteSelfContainedDistribute` 开关上，**不传 = 框架依赖**（约 86 文件，用户机器没装 .NET 直接弹下载提示；开发机有 SDK 所以冒烟假过）。正确命令：`dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:WindowsPackageType=None -p:CelesteSelfContainedDistribute=true -p:EnableSourceControlManagerQueries=false`（约 625 文件，拷走就能跑）。⚠ 混合目录坑：fd 产物覆盖进旧自包含 run-latest 后，残留的旧 hostfxr.dll/hostpolicy.dll 会把运行时解析带歪——**换发布形态前先把 dist\run-latest 挪走/清空再全量拷**。另：dotnet publish 长命令走后台任务或 python subprocess，回合边界会杀前台长命令（半成品 170 文件就是这么来的）。⚠ **publish 偶发 `Fatal error 0xC0000005` 崩在 `Microsoft.Build.Tasks.Git.GitConfig.Reader`**（SourceLink 读 git 配置，环境偶发非代码问题，_build_check.py 不受影响）：**加 `-p:EnableSourceControlManagerQueries=false` 即过**（2026-10-12 实测，重试原命令也崩，加开关立刻好）。
- **CI 自动发布链（2026-10-11 实证）**：push main/tag `v*` 均触发 ci.yml，tag 运行自动打包并建 release（资产名 CelesteMusicPlayer-Setup-<ver>.exe / -SC-）。**本地构建只作冒烟，发版走 CI**。撤发版=API DELETE /releases/{id} + /git/refs/tags/{tag}（都回 204），再推新 tag 让 CI 重建（v26.10.3 撤了重发 v26.10.11，全流程通）。
- 裸 python 后台跑会段错误→用 binaries/python 全路径。UIA 三坑：ControlTypeName 带后缀；图标按钮有效名=自身+子孙 TextControl 拼接；Collapsed 不在 UIA 树（「找不到」恰是隐藏的通过信号）。换 Segoe MDL2 字形前先验 cmap codepoint（EA37=List、E7B3=RedEye 已验证）。
