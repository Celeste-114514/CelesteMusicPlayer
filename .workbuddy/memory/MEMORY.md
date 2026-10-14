# CelesteMusicPlayer 项目长期记忆
> 只留跨会话有效事实。提交查 git log；当日查 memory/YYYY-MM-DD.md；构建发布见 celeste-build-verify skill。

## 工作主线
- 旧 HiFi 路线图（电平表→交叉淡入→APO EQ I/O→SRC→FIR→SQLite曲库）**已全部落地**，勿当待办。当前语境=**UI 令牌化之前准备**，标签板块已定稿；MainWindow 拆分放最后（用户定最高风险）。

## 音频链路 / DSP
- 链路：ffmpeg PCM 缓存→SeamlessWaveProvider(gapless)→独占SRC→降混→celeste_dsp_core.dll 机架(EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→变速→电平表)。**全关=bit-perfect**；测量只读音频只写标签。
- 内核=ECHO C++ 裁剪改写（如实披露+风险自担）；UI 除「播放内核」禁出现 ECHO；变速 processBlock 空壳禁宣传「已支持」。默认 native2（用户已实机自测通过）。进程级单实例过 EngineSwapGate 全局锁。

## WinUI3 铁律
- 新界面模式三连：①设置白名单 ②UI ComboBoxItem Tag ③ApplyUiStyleMode 分支，缺一静默不生效。
- 取色只认 resolver 套（ResolveAccentBrush/Color）；静态用 ResolveAccentColorStatic()；按钮前景恒白。主题判断必须沿元素树找显式 Light/Dark 祖先，禁读 App.RequestedTheme。
- XAML+cs 同 commit。XamlCompiler/csc 0xC0000005：①偶发重试即过 ②确定性连崩查内容（Border多子/ItemsWrapGrid错名/嵌套三元）。崩溃留 0 字节 .g.cs→清 obj/bin 重建（_build_retry_clean.py）。XamlCompiler 是 32 位进程，本机有 CPU 故障史，崩溃点会漂移。
- 秒崩 0xC000027B：XAML 初值+事件绑定→写后续 x:Name(null)→NRE；新面板 ValueChanged/Toggled 加「就绪」守卫。
- 单元格：AllowFocusOnInteraction=False 拒 FocusState.Pointer→进编辑必 Focus(Programmatic)；ListViewItem 选中视觉需自绘圆角 chrome+显式前景（默认动画把前景变白）。
- git commit -- <paths> 提工作树版；拆提交=git apply --cached 补丁（Python 写补丁用 newline="" 防 CRLF；补丁前重 grep ^@@）。

## 皮肤 / WebUI
- 皮肤入口只隐藏主界面 pill，设置→外观→界面风格/主题文件仍活。智能播放列表④已作废；设备记忆 DSP 档③活着。令牌皮肤覆写全局文字刷子，浅色下读 TextFillColorPrimary 会看不见。
- WebUI 路线 B：网页只长样不做事；消息 JsonDocument.Parse 提 kind；winmain.html 两套图标表，main.html 仅 SVG。

## 标签板块（定稿）
- 位深两条路径：表格 TryGetFactsFast(TagLib only，有损容器位深显「-」)；状态栏/歌曲面板 TryGetPartsCached(含 ffmpeg)。文件表格 15 列（14 定宽+末尾星号填充）；拖列宽帧合并(DispatcherQueueTimer ~16ms)。
- 单元格编辑=mp3tag 语义（唯一选中项才进编辑，回车提交/Esc 取消）。
- 歌词页 6 子页；后端 OnlineLyricDownload(QQ+LRClib，QQ 带 Referer+1.1s 闸)；外挂 .lrc 走 QuickLrcBox(UTF-8 无 BOM，独立按钮)；SYLT 只读沉底。⚠ xaml.cs 禁裸 `Path.` 用 System.IO.Path.。
- 在线标签 OnlineTagFill.cs：MB+iTunes(免key) + Discogs/Last.fm(需Key，用户 2026-10-14 已提供并导入本机 app-settings.json) + fanart.tv(按MBID取HD封面)。`CheckKeyAvailability` 未配Key提示跳过。设置用 AppSettingsStore.Load() 非 Instance。Key 只落本机不进仓库。
- 「标签编辑」右键/多选栏/WebUI 入口统一走 OpenTagEditForFiles。
- **2026-10-14 回归教训**：标签页整页不显示=上一轮崩溃的 XamlCompiler 弄坏发布 dll 内嵌 TagPanelControl.xaml 资源→运行时 XamlParseException 被 EnsureTagEditUiBuilt 吞掉。修法=清 obj/bin 干净重建重发（非源码错）；验证用临时 `new TagPanelControl()` 自检写日志(禁UIA点击)，验后删。

## 在线源定局
- 国内平台（网易云/QQ/酷狗/iTunes 搜歌）因**合规整改**删除(f058aeb/1328c77/27ec416)，不可恢复，勿暗示"配key就能恢复"。海外 MB/Discogs/Last.fm/fanart.tv 已接入(非被删平台)。

## toolkit DataGrid 列头
- 默认模板不绑 ContentTemplate→只换整个 ControlTemplate，里面 ContentPresenter 显式 ContentTemplate。保留 x:Name=VerticalSeparator/SortIcon 部件。点击排序 PointerPressed 存 KeyModifiers+PointerReleased 判位移>4px不排序+ Tapped 兜底汇到 SortFromHeader(500ms去重)。

## 工程约定
- 用户不懂编程：大白话结论先行禁营销腔。bit-perfect 铁律。推送：不 rebase/force-push、不 git add -A、commit 独立可回退、构建过才推；日常交付 dist\run-latest\。
- ⚠ 禁 UIA 点击类冒烟：交付只做单次启动验证(启动→存活12s→杀→清.running)，用 _smoke_runlatest.py。
- 源码偶发被清零(前4096字节NUL>0.5判坏，从 HEAD 恢复)。工作树未跟踪 588 项约6.4G 动不得=dist/run-latest、native/echo-core/build/、_backup_tagsort_pre_revert/、.workbuddy/。

## 构建与发布
- dotnet 必带 env 前缀补 APPDATA/SystemRoot 等；无 ImplicitUsings(SemaphoreSlim 须 using System.Threading)。
- run-latest 自包含发布：`dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:WindowsPackageType=None -p:CelesteSelfContainedDistribute=true -p:EnableSourceControlManagerQueries=false`。
- 判成功=rc==0 且产物 mtime 是本次(只时间戳会把旧exe当成功)；XamlCompiler 连崩两次才过要重试。IL 以 icacls 实查，「无标签」才是 UAC 关机的正确态。publish 偶发挂10+分钟→communicate(timeout~540) 杀重试。
- ⚠ **0 字节 .g.cs 反馈环：清 obj/x64 必须同时清 bin/x64**，只清 obj 会连续崩（本机 CPU 故障时 XamlCompiler/csc 必崩，CS=0 只是 XAML 那步崩）。
- ⚠ **本机抽风时全量自包含发布的原生 DLL 可能被拷坏**：症状=启动前无任何日志的 0xC0000005 静默退，或 CLR `0x80131506`（Internal CLR error）。好备份能启动、新发布产物不能即中招。**恢复法**：好备份整体回填 run-latest（原生 DLL+自包含 exe 都完好）→ 只把**带 `-p:CelesteSelfContainedDistribute=true`** 发布出的 CelesteMusicPlayer.dll 覆盖进去（dll 约 2.73MB；漏该开关会出 177KB 依赖运行时版 exe，混搭会在 Main begin 后以 3221227010 退）。判自包含 exe 看尺寸≈303KB。
- ⚠ **Canvas 不认 Horizontal/VerticalAlignment**：里面放 TextBlock 想居中必须改用 Grid（或手动 SetLeft/Top 估高）。MainWindow.DspMatrix.cs 节点圆点 L/R 曾因此偏左上，已改 Grid。
