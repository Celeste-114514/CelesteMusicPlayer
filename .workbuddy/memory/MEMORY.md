# CelesteMusicPlayer 项目长期记忆

> 只留跨会话有效事实。提交细节查 git log；当日细节查 memory/YYYY-MM-DD.md；构建发布见 celeste-build-verify skill。

## 工作主线（2026-10-14 用户校正）
- 旧 HiFi 路线图（电平表→交叉淡入淡出→APO EQ I/O→SRC→FIR 卷积→SQLite 曲库）**已全部落地**，勿再当"计划/待办"汇报。
- 当前语境 = **UI 令牌化之前的准备工作**，标签板块（已定稿）是其中一环；MainWindow 拆分是最后一项（用户既定"放最后、风险最高"）。

## 音频链路与 DSP 内核
- 链路：ffmpeg PCM WAV 缓存（2GB LRU）→OpenWaveSource→SeamlessWaveProvider（gapless）→独占 SRC→降混→ManagedDspSourceProvider→一次 celeste_dsp_process 进 celeste_dsp_core.dll（机架 EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→变速→电平表）。**全关=bit-perfect**；测量只读音频、只写标签。
- ⚠内核进程级单实例（CreateEngine 全程过 EngineSwapGate 全局锁）。内核=ECHO C++ 裁剪改写（许可=如实披露+风险自担）；UI 文案除「播放内核」外禁出现 ECHO；变速 processBlock 是空壳，禁宣传「内核已支持」。可视化先读内核源码再写（压缩=CompressorProcessor.cpp:160-175 逐行复刻）。
- 独占内核 2026-10-11 起默认 native2（self 仅存读取失败兜底）。**用户已实机自测 native2 通过，勿再催测**。

## WinUI3 铁律（血坑汇总）
- 新增界面模式/枚举值落地三连，缺一即静默不生效：① 设置白名单（AppSettingsStore.Normalize 的 `is "Scene" or "Classic*"` 链）② UI 选择项（ComboBoxItem Tag）③ 应用分支（ApplyUiStyleMode）。
- 取色只认 resolver 那套（ResolveAccentBrush/Color）；CurrentAccent 是直通系统的另一套，抄近道会「别处都变就这块不变」。静态上下文用 MainWindow.ResolveAccentColorStatic()。按钮前景恒白（用户拍板）。
- 主题判断必须沿元素树找显式 Light/Dark 祖先，禁读 App 级 RequestedTheme。
- XAML+cs 必须同 commit（拆开 revert 任一半编译不过）。
- **XamlCompiler/csc 0xC0000005 崩溃分两种**：① 偶发单次抽风，重试即过；② **确定性连崩先查内容**（ET.parse 验良构→查 BOM→诱因：Border 塞多子元素（叠加一律 Grid）、ItemsWrapGrid 属性名写错、嵌套三元（Roslyn NullableWalker 深递归））。⚠ 2026-10-14 实测新形态：XamlCompiler 是 **32 位进程**，环境性崩溃点会在 Pass1/Pass2 间**漂移**（同一次构建 csc 也可能跟着崩）= 机器不稳定征兆（本机有 CPU 故障史）；且崩溃会留下 **0 字节 .g.cs**，不清则下轮 csc 次生崩（反馈环，CS=0 全是假象）→ 破法=清 obj/x64+bin/x64（移仓库外）再重建，脚本 `_build_retry_clean.py`。
- 秒崩 0xC000027B：XAML 带初值+事件绑定→InitializeComponent 就地触发→写后续 x:Name（null）→NRE。新面板 ValueChanged/Toggled 必加「面板就绪」守卫；临时下架带 x:Name 控件用 Collapsed 禁注释（=CS0103）。
- obj/bin 备份禁止留项目目录（SDK glob 会把备份 .g.i.cs 编进去→假 CS0102+原生崩），备份放仓库外。
- 删代码别按行号整段删（误伤夹中间的方法），按方法签名定位逐段删，删完立刻 _build_check 到 BUILD_OK。
- UserControl 拿宿主句柄只能宿主注入（WindowNative.GetWindowHandle(this) 在 UserControl 上抛 CCW cast 错）。
- **git commit -- <paths> 提交的是工作树版本不是 index**；按 index 拆提交=git apply --cached 拆出的补丁→无 paths commit。⚠ 拆提交两坑：① Python text 模式把 git diff 的 LF 写成 CRLF→apply 上下文必败，补丁/消息文件一律 `open(...,newline="")`；② git diff 是工作区对 index，每 commit 一次剩余 hunk 编号就平移，每批补丁前重新 grep `^@@` 数数+按内容关键字断言防呆。
- 二级窗口并入主 UI（已全部完成）：底座 ShellPageHost+强调色外部注入；三步=先内联→摘入口→删窗；搬运前 grep 列全分部文件（只有一处能写 `: UserControl`）。
- 单元格焦点/选择铁律（3b0dfdf 实测定稿）：`AllowFocusOnInteraction=False` 会拒掉 **FocusState.Pointer** 焦点请求（「选中后点格子没进编辑」真根因）→进编辑必须 `Focus(FocusState.Programmatic)`；选择不依赖 ListViewItem 默认行为=press 侧显式 `SelectedItem=item`+`e.Handled`，release 侧用 `_cellPressHandled` 标志挡默认翻选；Ctrl/Shift 多选手势放行。
- ListViewItem 选中视觉铁律：默认 Selected 态=方角纯色矩形（ItemContainerStyle 里写 CornerRadius 默认模板根本不读），且默认把 ContentPresenter.Foreground 动画成白色（动画优先级高于本地值）。修法=杀 `ListViewItemBackgroundSelected*` 四资源为透明+行模板自绘圆角 chrome（accent 底+`ResolveContrastingForeground` 对比前景）+行内每个 TextBlock/TextBox 显式设前景；chrome 是内容层的**兄弟**节点，前景遍历范围取 `chrome.Parent`。
- ItemContainerStyle 默认 `VerticalContentAlignment=Top`→行内容贴行顶；设 `Stretch` 后各单元格自带 VerticalAlignment=Center 自行居中。

## 皮肤 / WebUI
- 皮肤入口只隐藏了主界面快捷 pill；设置→外观→「界面风格→测试皮肤」+「主题文件」是活的（用户实测可选），对外不许说「整体隐藏」。
- 智能播放列表（④）已作废；设备记忆 DSP 档（③）活着（DeviceDspMemoryToggle）别当待办。极客色源唯一 GeekPhosphorColor，严禁改全局资源（必崩）。
- WebUI 路线 B：网页只长样子不做事。点行播放=整表替换队列；消息手动 JsonDocument.Parse 提 kind；CSS 写了≠生效；视图切换只切 display 且 scrollTop=0。⚠ winmain.html 有两套图标表，main.html 只有 SVG 那套。
- 标签排序已整体删除（d0d7ab6；恢复=revert d0d7ab6），别当现存功能汇报。令牌皮肤覆写全局文字刷子，浅色主题下读 TextFillColorPrimary/Secondary 的文字会看不见。

## 标签板块（阶段 1-7 已定稿）
- **位深两条路径（别混）**：标签表格走 `TryGetFactsFast`（TagLib only，有损容器刻意不起 ffmpeg，位深显示「-」；mp3tag 同样不显示）；状态栏/歌曲面板走 `TryGetPartsCached`（完整探测含 ffmpeg，MP3 的 16bit=解码输出精度 pcm_s16le，非文件属性）。
- 二级窗口盘点（2026-10-14 更新）：入口只剩**迷你播放器**一个；重复文件窗口本轮已删（曲库健康有重复检测，DuplicateFilesWindow.xaml/.cs 已 git rm）；TagEditorWindow/LyricsEditorWindow 早已删；其余 10 个窗口类（设置/文件关联/均衡器/房间校正/RG扫描/当前播放列表/播放队列/头像两窗/取色器）入口全摘、类文件留兜底仍编译，CustomSortOrderWindow 是真死代码（标签排序已整体删）。
- 文件表格定稿：15 列=14 真实列全定宽+末尾星号填充列（专吸剩余宽度；混在真实列里会吞被拖列变化量）。列：# 标题* 艺术家 专辑 年份 曲目 流派 时长 文件名 格式 位深 采样率 码率 大小。行模型 TagFileItem:PlaylistItem。表头在 ListView 外，ViewChanged 位移+RectangleGeometry 裁剪同步；列边界 6px 把手拖拽=改本列宽右侧跟随。列数/默认宽三处同步（XAML 表头、行模板、cs 数组），运行期以 cs 数组为唯一真相。
- 读标签提速（a85a373）：BuildEntry 单次开 TagLib 一趟拿标签+时长+TryGetFactsFast（吃已开文件 Properties，零额外 I/O 零 ffmpeg）；仅 TagLib 全读不到才回退完整探测（保 DSD 与 1Hz 脏值）。**位深/码率补缺（a6640ca）**：回退条件=码率缺失（任何容器）或位深缺失且无损/PCM 容器（wav/aif/flac/ape/wv/dsf/dff…；m4a 故意不列）→ ffmpeg 完整探测，「有就补」合并回填（rate 过 IsPlausibleSampleRate），不清空已猜。
- 单元格编辑=mp3tag 语义（46bb28a→3b0dfdf）：单击行=选中整行；该行是**唯一选中项**时单击可编辑列=Focus(Programmatic) 进编辑（GotFocus 里 SelectAll）；未选中/多选时首击只收敛选中到该行。提交=读全量→改一字段→写盘，失败这格退回原值；回车提交/Esc 取消。
- 表头/行对齐根因（1eb95c4→00a904d 实测定稿）：表头 Width=NaN 拉伸到宿主全宽、行拉伸到 ListView 内部视口，竖滚动条占位≈16px 让两区域右边缘错开。定稿=表头/行统一读 `GetRowExtentWidth()`（**ItemsPanel.ActualWidth 优先**=行的直接父容器、滚动条占位自动一致，未就绪退回视口宽）+ ApplyColumnWidths 每次现读 ViewportWidth + `TagFileListView.LayoutUpdated` 钩子核对（差 >0.5 才刷）。⚠ Clip 挂带 RenderTransform 的元素上会随平移一起动——必须挂不动的 TagHeaderHost。
- 拖拽/缩放卡顿根因（1eb95c4→d8b0cc1，第四轮实测才清）：① ApplyColumnWidths 原来按 _files 全量 ContainerFromItem，文件上千时 O(n²)→改遍历 ItemsPanel.Children 已实现容器+列宽同值跳过；主窗口 SongListView_SizeChanged 原来全量 chrome 刷新→改只更新已实现行铺满宽，选中态变化才全量刷。② **残余根因=高回报率鼠标一个显示帧灌入多个 PointerMoved，每个都同步改列宽=UI 线程打满**→拖列宽只记 pending 值，DispatcherQueueTimer ~16ms 应用一次（松手立即结算）；主窗口 FitColumnsToAvailableWidth 同样帧合并（MainContentGrid.SizeChanged/AppWindow.Changed/PlaylistListBorder_SizeChanged 三处调用点）。
- ⚠ 用户说「文字贴列边界竖线」≠「末列贴表格边缘」——先对齐语义再动手。正解=全列文字右留 6px。
- # 列全 0 根因：x:Bind 默认 OneTime，行实现时 Renumber 还没跑。修法=Index 改 virtual+TagFileItem override+INPC 通知+Mode=OneWay。⚠ shadow(new) 救不了基类计算属性。保存后刷行=ObservableCollection 索引器换实例+恢复选中+Renumber。
- 阶段六快照回滚：保存前逐文件 ReadTag 存原值→逐个写→任一失败把已成功的写回；SaveTag 返 bool。
- 阶段七批量工具（89e579f）：六工具=文件名→标签（默认只填空）/标签→文件名重命名（先算名不碰文件、冲突跳过、失败整体改回）/文本替换（可正则）/大小写/自动编号（起始+步长写曲目号，格式存不了前导零故无位数）/清空文字标签（留封面）/移除内嵌封面。统一执行器 RunBatchWithRollbackAsync；作用范围=选中项，没选=整个列表。⚠ ReadTag 带文件名兜底，批量工具取真值必须用 ReadTagRaw，否则「只填空」被误判没改动整个跳过。
- **歌词页定稿（5091163→2026-10-14 收敛轮）**：6 子页=快速编辑/批量工具/排序编号/在线标签/在线歌词（原「歌词下载」→2026-10-14 更名；后端 OnlineLyricDownload.cs，QQ+LRClib 双源）/批量下载歌词（独立子页，LyricBatchPageSource 设置项）。后端 OnlineLyricDownload.cs（QQ+LRClib 双源、加权匹配移植 MusicPlayer2、四种写入口径=外挂.lrc/标签纯文本/标签带时间戳/两者）；QQ 请求必须带 Referer: https://y.qq.com/ + SemaphoreSlim 串行闸 1.1s。⚠ TagPanelControl.xaml.cs 里禁裸 `Path.`（CS0104），一律 `System.IO.Path.`。Lyrics 与 UNSYNCEDLYRICS 在 MP3 同帧（USLT），带时间轴的是 SYLT（只读）；ECHO 按 LRC 解析字段内容是兼容潜规则非标准。⚠ 控件 x:Name 不能与新增 record 类型同名（进度条 LyricBatchProgress→LyricBatchProgressBar）。**外挂 .lrc 编辑（本轮新增）**：快速编辑页 QuickLrcBox，路径解析=歌曲同目录已存在→歌词文件夹已存在→都没有按 SaveLyricToSongFolder 默认落点；保存=UTF-8 无 BOM，独立按钮不走标签保存链。同步歌词框（SYLT 只读）按用户要求沉底到快速编辑页最底部。
- **在线标签后端 = OnlineTagFill.cs**：MB release 搜索走 MusicBrainzGetAsync 限速闸 + iTunes song 免 key，双源并行；iTunes 字段最全；MB 选中后拉发行详情补曲目总数/碟片/流派/CAA 封面并按本地标题匹配曲目号；歌词只有 LRClib；封面 CAA/Apple artwork 预览写入同字节；写入=ReadTagRaw 快照+按字段勾选 MarkDirty+SaveTag，封面单独快照，任一失败整体回滚；PickBestMatch 加权（标题0.5/艺术家0.3/专辑0.2，阈值0.45）自动选中。**有 Key 源（用户 2026-10-14 提供 Key 并已导入本机 app-settings.json）**：Discogs（OAuth 消费者密钥对走简化头 `Discogs key=.., secret=..` 免完整 OAuth；搜索给 year/genre/style/缩略封面，选中后 LoadDiscogsDetailAsync 拉发行详情补高清封面+年份+流派）、Last.fm（album.getinfo 给 top tags 流派+mega 封面）、fanart.tv（CAA 没封面时按 MBID 取 HD 专辑封面）。来源 Combo 现含：全部/MB+iTunes/MB/iTunes/Discogs(需Key)/Last.fm(需Key)；`CheckKeyAvailability` 在未配 Key 时给界面提示跳过哪些平台。⚠ 设置访问用 `AppSettingsStore.Load()`（返回 AppSettingsState 克隆、带缓存），**不是** `AppSettingsStore.Instance`（不存在）。Discogs 用户给的是 Consumer Key/Secret，故新增 `DiscogsConsumerKey`/`DiscogsConsumerSecret` 两属性（连同已有 `LastFmApiKey`/`FanartTvApiKey`）。
- **「标签编辑」右键入口**：单曲右键（AppendPlaylistContextFeatureItems）/多选栏（MainWindow.xaml MultiSelectEditTagsButton）/WebUI 歌曲菜单均有，统一走 OpenTagEditForFiles 载入标签表格；曾名「编辑标签」本轮统一改名。

## 在线源定局（合规整改后，2026-10-14 复核）
- 旧多平台在线源（网易云/QQ/酷狗/iTunes 搜歌）删除于**合规整改**提交（f058aeb 移除网易云/QQ/酷狗、1328c77 歌词改 LRClib、27ec416 在线搜索窗口下架），**不是缺 key**，勿向用户暗示"配上 key 就能恢复"——那批国内平台合规不可恢复。
- **但**用户 2026-10-14 明确要的是 MB 类合规海外元数据平台（Last.fm/Discogs/fanart.tv），并提供了 Key。这些**不是**被删的国内平台，已接入在线标签子页（见上 OnlineTagFill 段）并写入本机 app-settings.json。设置里 Last.fm/Discogs/fanart.tv 三个 key 槽早就是调研落地产物（AppSettingsStore.cs 留有申请地址注释），之前只是空着没接线。
- 现状：在线标签=MusicBrainz+iTunes（免 key）+ Discogs/Last.fm（需 Key，用户 2026-10-14 已提供并导入本机 app-settings.json）+ fanart.tv（按 MBID 取 HD 封面）；歌词=LRClib（唯一免 key 歌词库）+QQ（歌词页）；要 key 的音平台没有值得配的（Musixmatch 免费档仅 30% 片段、Genius 仅 snippets、Spotify 无接口）。
- ⚠ 三个 key 槽（LastFmApiKey / DiscogsConsumerKey+Secret / FanartTvApiKey）已在本机 app-settings.json 配齐；进 git 的是代码槽位与接线，Key 本身只落本机设置不进仓库。勿再以"无 key / 没找到 key"口径答复。

## toolkit DataGrid 列头（2026-10-14 实测，别再踩）
- ⚠ **DataGridColumnHeader 默认模板不绑定 ContentTemplate**：只在 Style 里设 `ContentTemplate` 完全无效，Header 对象被当文本渲染（只显示 ToString()）。后果=挂在自定义模板上的点击手势**一次都不触发**。想改列头长相必须**整个换 ControlTemplate**，在模板里的 ContentPresenter 上显式写 `ContentTemplate="{StaticResource ...}"`。
- 默认模板右侧常驻一块排序图标位：深色主题下就是竖线左边那块**白斑**，既挡列名又吃列宽（"列头文字显示不全"的真凶）。自写模板只留「列名+排序标记」+ 右侧 1px 分隔线。
- 自写 ControlTemplate 必须保留 `x:Name="VerticalSeparator"` 与 `x:Name="SortIcon"` 两个部件（toolkit 内部按名字找）；排序图标自己 Collapsed，排序标记自己画。
- 列头点击排序：PointerPressed 存 `e.KeyModifiers` + PointerReleased 判位移（>4px = 拖列宽不排序），**再挂 Tapped 兜底**，两条路汇到 SortFromHeader 并用 500ms 去重。

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
- ⚠ publish 偶发挂住 10+ 分钟 CPU 几乎不动（环境性非代码）：流式读 `p.stdout` 在子进程不输出时永久阻塞、timeout 形同虚设→必须 `communicate(timeout=~540)` 超时杀进程重试。dotnet 管道输出是块缓冲，别指望流式等。
- ⚠ **发布脚本判成功必须 `rc==0` 且产物 mtime 是本次的**：只看时间戳会把上一次旧产物当成功（曾让用户拿到旧 exe 白等一轮）。XamlCompiler MSB3073 会连崩两次才过，脚本要自动重试。
- 发布脚本 `_pub_runlatest.py`：publish → 备份 run-latest 到仓库外 `C:\Users\MSI\_runlatest_bak_<时间戳>`（**别用 rmtree 删旧备份**，626 文件会触发安全删除批量确认把脚本打断）→ 按 mtime 增量覆盖同步（不清空：CelesteUpdater.exe / RestartAgent.exe / createdump.exe 是 publish 产物里没有的，清空会丢）。
- publish 产物路径会漂（`bin/Release/...` 与 `bin/x64/Release/...` 都出现过），交付前先 find 定位再拷。kill 杀掉的冒烟启动不写主日志（退出才 flush），佐证进程活跃看 `app-settings.json.pathlog` mtime 或 .running 生命周期。
