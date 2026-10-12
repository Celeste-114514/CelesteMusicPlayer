# CelesteMusicPlayer 项目长期记忆

> 只留跨会话有效事实。提交细节查 git log；当日细节查 memory/YYYY-MM-DD.md；构建发布见 celeste-build-verify skill。

## 音频链路与 DSP 内核
- 链路：ffmpeg PCM WAV 缓存（2GB LRU）→OpenWaveSource→SeamlessWaveProvider（gapless）→独占 SRC→降混→ManagedDspSourceProvider→一次 celeste_dsp_process 进 celeste_dsp_core.dll（机架 EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→变速→电平表）。**全关=bit-perfect**；测量只读音频、只写标签。
- ⚠内核进程级单实例（CreateEngine 全程过 EngineSwapGate 全局锁）。内核=ECHO C++ 裁剪改写（许可=如实披露+风险自担）；UI 文案除「播放内核」外禁出现 ECHO；变速 processBlock 是空壳，禁宣传「内核已支持」。可视化先读内核源码再写（压缩=CompressorProcessor.cpp:160-175 逐行复刻）。
- 独占内核 2026-10-11 起默认 native2（self 仅存读取失败兜底）。**用户已实机自测 native2 通过，勿再催测**。

## WinUI3 铁律（血坑汇总）
- 新增界面模式/枚举值落地三连，缺一即静默不生效：① 设置白名单（AppSettingsStore.Normalize 的 `is "Scene" or "Classic*"` 链）② UI 选择项（ComboBoxItem Tag）③ 应用分支（ApplyUiStyleMode）。
- 取色只认 resolver 那套（ResolveAccentBrush/Color）；CurrentAccent 是直通系统的另一套，抄近道会「别处都变就这块不变」。静态上下文用 MainWindow.ResolveAccentColorStatic()。按钮前景恒白（用户拍板）。
- 主题判断必须沿元素树找显式 Light/Dark 祖先，禁读 App 级 RequestedTheme。
- XAML+cs 必须同 commit（拆开 revert 任一半编译不过）。
- XamlCompiler 崩 0xC0000005 别重试直接二分：ET.parse 验良构→查 BOM→_build_check.py（Release/win-x64）。诱因：Border 塞多子元素（叠加一律 Grid）、ItemsWrapGrid 属性名写错。dotnet 长命令走 python subprocess。
- 秒崩 0xC000027B：XAML 带初值+事件绑定→InitializeComponent 就地触发→写后续 x:Name（null）→NRE。新面板 ValueChanged/Toggled 必加「面板就绪」守卫；临时下架带 x:Name 控件用 Collapsed 禁注释（=CS0103）。
- obj/bin 备份禁止留项目目录（SDK glob 会把备份 .g.i.cs 编进去→假 CS0102+原生崩），备份放仓库外。
- publish 连崩先查嵌套三元（Roslyn NullableWalker 深递归），拆局部变量即过。
- 删代码别按行号整段删（误伤夹中间的方法），按方法签名定位逐段删，删完立刻 _build_check 到 BUILD_OK。
- UserControl 拿宿主句柄只能宿主注入（WindowNative.GetWindowHandle(this) 在 UserControl 上抛 CCW cast 错）。
- **git commit -- <paths> 提交的是工作树版本不是 index**；按 index 拆提交=git apply --cached 拆出的补丁→无 paths commit。
- 二级窗口并入主 UI：底座 ShellPageHost+强调色外部注入；三步=先内联→摘入口→删窗；搬运前 grep 列全分部文件（只有一处能写 `: UserControl`）。

## 皮肤 / WebUI
- 皮肤入口只隐藏了主界面快捷 pill；设置→外观→「界面风格→测试皮肤」+「主题文件」是活的（用户实测可选），对外不许说「整体隐藏」。
- 智能播放列表（④）已作废；设备记忆 DSP 档（③）活着（DeviceDspMemoryToggle）别当待办。极客色源唯一 GeekPhosphorColor，严禁改全局资源（必崩）。
- WebUI 路线 B：网页只长样子不做事。点行播放=整表替换队列；消息手动 JsonDocument.Parse 提 kind；CSS 写了≠生效；视图切换只切 display 且 scrollTop=0。⚠ winmain.html 有两套图标表，main.html 只有 SVG 那套。
- 标签排序已整体删除（d0d7ab6；恢复=revert d0d7ab6），别当现存功能汇报。令牌皮肤覆写全局文字刷子，浅色主题下读 TextFillColorPrimary/Secondary 的文字会看不见。

## 标签板块（进行中，阶段 1-7）
- 在线功能只留 MusicBrainz 系（MB+CAA+LRClib+Wikidata 零门槛）；网易云/QQ/酷狗/iTunes 源已全删。歌词=LRClib，简介=Wikidata。
- 二级窗口：仍活跃=迷你播放器/歌词编辑/重复文件；TagEditorWindow 已删（七处入口全切主界面）；已不可达（代码在入口全摘）=当前播放列表/播放队列/均衡器/房间校正/RG 扫描/设置/文件关联/头像两窗/取色器/CustomSortOrder。
- 文件表格定稿：15 列=14 真实列全定宽+末尾星号填充列（专吸剩余宽度；混在真实列里会吞被拖列变化量）。列：# 标题* 艺术家 专辑 年份 曲目 流派 时长 文件名 格式 位深 采样率 码率 大小。行模型 TagFileItem:PlaylistItem。表头在 ListView 外，ViewChanged 位移+RectangleGeometry 裁剪同步；列边界 6px 把手拖拽=改本列宽右侧跟随。列数/默认宽三处同步（XAML 表头、行模板、cs 数组），运行期以 cs 数组为唯一真相。
- 读标签提速（a85a373）：BuildEntry 单次开 TagLib 一趟拿标签+时长+TryGetFactsFast（吃已开文件 Properties，零额外 I/O 零 ffmpeg）；仅 TagLib 全读不到才回退完整探测（保 DSD 与 1Hz 脏值）。副作用=mp3/aac 位深列显示「-」（已披露）。
- 单元格编辑=mp3tag 式两击（51790bc）：AllowFocusOnInteraction=False + PointerPressed 管「武装」——首击只选中（e.Handled+手动 SelectedItem），再击同一格 Focus 进编辑；GotFocus 里 SelectAll。提交=读全量→改一字段→写盘，失败这格退回原值；回车提交/Esc 取消。
- ⚠ 用户说「文字贴列边界竖线」≠「末列贴表格边缘」——先对齐语义再动手。误加 10px 末列 margin 被回退，正解=全列文字右留 6px。
- # 列全 0 根因：x:Bind 默认 OneTime，行实现时 Renumber 还没跑。修法=Index 改 virtual+TagFileItem override+INPC 通知+Mode=OneWay。⚠ shadow(new) 救不了基类计算属性。保存后刷行=ObservableCollection 索引器换实例+恢复选中+Renumber。
- 阶段六快照回滚：保存前逐文件 ReadTag 存原值→逐个写→任一失败把已成功的写回；SaveTag 返 bool。
- 阶段七批量工具（89e579f）：六工具=文件名→标签（默认只填空）/标签→文件名重命名（先算名不碰文件、冲突跳过、失败整体改回）/文本替换（可正则）/大小写/自动编号（起始+步长写曲目号，格式存不了前导零故无位数）/清空文字标签（留封面）/移除内嵌封面。统一执行器 RunBatchWithRollbackAsync；作用范围=选中项，没选=整个列表。⚠ ReadTag 带文件名兜底，批量工具取真值必须用 ReadTagRaw（本次新增），否则「只填空」被误判没改动整个跳过。

## 工程约定
- 用户不懂编程：大白话结论先行，禁营销腔。bit-perfect 铁律。推送：不 rebase/force-push、不 git add -A；commit 独立可回退；构建过才推。安装包只在用户说「发新版」时打；日常交付=dist\run-latest\。
- 版本号=日期命名；发版前 git log <上tag>..HEAD 逐条过完再写日志。
- 同一文件并行 Edit 会丢：串行，改完 Grep 复核再构建。源码偶发被清零（前 4096 字节 NUL>0.5 判坏，从 git HEAD 恢复）。
- ⚠ 禁止 UIA 点击类冒烟（用户：「不要再打开我窗口用鼠标点了」）：最终交付只做单次启动验证（启动→存活 12s→杀→清 .running）。
- 日志无异常 ≠ 功能正常：每条静默失败路径都要在 UI 写明原因 + 写 StartupLog。
- 工作树未跟踪文件 588 项约 6.4G（未授权删除）：动不得=dist/run-latest、native/echo-core/build/（csproj 直接引用其 DLL）、_backup_tagsort_pre_revert/、.workbuddy/；可清=_wv2_cachebak/_binbak_*/_objbak_*/_th_cachebak/_bin_verify/_syncheck/_iobj、111/、.reasonix/、@AutomationLog.txt、_xc*.txt、根目录约 200 个 _*.py/_*.sh 与截图、commit_msg_*.txt；待定=tools/、_webcheck/、output/outputs/。

## 构建与发布（详见 celeste-build-verify skill）
- dotnet 必带 env 前缀补 APPDATA/SystemRoot 等。判成功三类全零：error CS/MSB/:（Roslyn 崩）+产物时间戳。本项目无 ImplicitUsings：SemaphoreSlim 须显式 using System.Threading。
- run-latest 必须自包含发布：`dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:WindowsPackageType=None -p:CelesteSelfContainedDistribute=true -p:EnableSourceControlManagerQueries=false`（约 625 文件）。⚠ 换形态前先清空 dist\run-latest 再全量拷（残留旧 hostfxr/hostpolicy 会带歪运行时）。
- CI：push main/tag v* 均触发 ci.yml 自动打包建 release；本地构建只作冒烟，发版走 CI。IL 判据以 icacls 实查为准（「无标签」才是正确状态）。
