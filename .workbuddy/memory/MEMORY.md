# CelesteMusicPlayer 项目长期记忆
> 只留跨会话有效事实。提交查 git log；当日查 memory/YYYY-MM-DD.md；构建发布见 celeste-build-verify skill。

## 工作主线
- 旧 HiFi 路线图（电平表→交叉淡入→APO EQ I/O→SRC→FIR→SQLite曲库）**已全部落地**，勿当待办。当前语境=**UI 令牌化之前准备**，标签板块已定稿；MainWindow 拆分放最后（用户定最高风险）。
- **⚠ E 组主 UI 四项（2026-10-15 18:3x 接手态）**：代码完成、审查通过，但**未提交未推送未冒烟**——8 个文件改动全在工作区（+475/-174），`stash@{0}`「E组改动-构建实验」有完整备份；构建被本机 XamlCompiler 100% 抽风挡住（36 连崩，A/B 证 HEAD 同样崩=环境问题）。提交信息在 commit_msg_egroup.txt。机器恢复或用户 VS F5 自验过后再 commit + 交付 run-latest。详细经过见 2026-10-15 日记 18:3x 段。
- **零散播放架构定案（2026-10-15）**：保留文件关联（学 echo 砍关联=否）；做独立「零散播放」窗口（临时队列纯内存、不碰曲库不读 SQLite，冷启动双击只建它秒出声，主窗口按需再开并跳过启动续播）；播放器内「选择文件/文件夹」=送零散窗口播放不入库（现状 replace:true 清主窗口视图是错位）。技术底数：AudioPlaybackEngine 自包含可被零散窗口自持（DSP 读同一份 AppSettingsStore，bit-perfect 不变）；QuickPlayLauncher 已有单实例+relay。实施=待办清单 Q1~Q5。播放设置 10 项音频项**留设置当高级项**（已加「音频输出（高级设置）」分组头，不挪音频设置面板）。

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
- ⚠ **仍有酷狗残留(2026-10-15 实查)**：MainWindow.ArtistDetail.cs / ArtistAvatarSearchWindow / ArtistAvatarEditPanelControl / LyricsLoader.cs / OnlineMusicApi.cs / MainWindow.Lyrics.cs、Library.cs、Library2.cs、WebMain.cs / SettingsPanelControl.xaml.cs / SettingsWindow.xaml(.cs) / AppSettingsStore.cs 仍含酷狗|网易|QQ 字样。艺术家详情页概览仍在走酷狗=违规，待办 A4。

## 在线源商用条款（2026-10-15 查官方 ToS，长期有效）
- **Last.fm**：ToS 3.1/3.2 明确**仅限非商业用途**，商用须书面协议(partners@last.fm)，违约可立即停key+追偿；官方还保留未来分成权。用户自填 key **不改变**"应用是否商用"的判定→"免费版带 Last.fm、付费 DLC 不带"**站不住**。
- **Discogs**：商用一般允许，但① Restricted Data 禁商用 ② **禁止对"集成 Discogs 数据的那部分功能"单独收费**（除非书面许可）③ 必须署名："本应用使用 Discogs API，但未被 Discogs 赞助或认可" + 数据旁 "Data provided by Discogs" 带**可传 SEO 权重**的链接(不能 nofollow)。署名现在就该加。
- **fanart.tv**：API 文档标 CC BY 3.0（署名即可商用）；真正风险在**图片本身版权**（摄影者/厂牌），不在 API。署名待加。
- **GPL-3.0 + Steam 商业化**：GPL 允许卖但须给源码；**Steamworks SDK 不能链接进 GPL 程序**，上 Steam 卖须改许可或做成独立进程。商业化前必须先定。
- 建议：给在线源加"商用开关/编译条件"，商业化时一键下架 Last.fm/Discogs/fanart.tv，只留 MB+CAA。

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
- ⚠ **GitHub Release 上的安装包是 CI 打的，不是本地那份**（ci.yml tag 时调 package-installers.ps1 现场打包后 `gh release upload --clobber`）。排查"用户装不上"先 `curl api.github.com/repos/o/r/releases/latest` 比 size，本地 dist 那份可能压根没传上去。
- ⚠ **publish 三个开关一个都不能少**：`-p:Platform=x64 -p:WindowsPackageType=None [-p:CelesteSelfContainedDistribute=true] -p:EnableSourceControlManagerQueries=false`。漏 `WindowsPackageType=None` → 走 MSIX 路径 → **`.pri` 只有约 253KB（正常约 1.6MB），XAML 资源整批缺失，装完启动即失败**，但 exe 尺寸/哈希/安装过程全都正常，极难发现。2026-10-15 事故即此。
- 打包脚本已内置体检 `Assert-PublishHealthy`：pri<900KB、sc 版 exe<250KB、fd 版 exe>250KB 一律抛错拒打包。
- 解 NSIS 安装包对比内容：完整版 7z 才行（`7zr.exe` 精简版不认 NSIS；先下 `7z2604-x64.exe` 再用 7zr 解出 7z.exe）。静默安装验证用 PowerShell `Start-Process -Wait -PassThru`；bash 下 `/D` 必须用反斜杠。
- 本机**无 Windows 沙盒**（Containers-DisposableClientVM 不存在）、Hyper-V Disabled → 没法模拟全新电脑，改用解包对比 + 静默安装到临时目录。
- ⚠ **XamlCompiler 确定性崩溃（exit 1 / CS=0 / 连崩 6 次）先怀疑 XAML 结构错位，不是机器抽风**：搬移大块 XAML 时**少搬/多搬闭合标签**会造成「标签数量平衡但嵌套错位」，XML 解析仍通过，编译器必崩。判定法=把改动 `git stash` 掉构建上一版（能过就是改动的问题）。搬移必须连闭合标签一起搬，搬完立刻 `ET.parse` 校验 + 看有无孤立标签。
- 搬移后缩进可以不一致（不影响编译），但**剪切区间必须用锚点定位 + 注意插入标记导致的 index 偏移**（曾少切一行 `</Grid>`，留下孤立标签）。
