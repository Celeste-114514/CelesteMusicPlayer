# CelesteMusicPlayer 项目长期记忆
> 只留跨会话有效事实。提交查 git log；当日查 memory/YYYY-MM-DD.md；构建发布见 celeste-build-verify skill；细目见 memory/ref-*.md（datagrid-toolkit / build-publish / online-sources）。

## 工作主线
- 旧 HiFi 路线图（电平表→交叉淡入→APO EQ I/O→SRC→FIR→SQLite曲库）**已全部落地**，勿当待办。当前=UI 令牌化前准备；MainWindow 拆分放最后（最高风险）。main 分支本地领先远端较多，未推送。
- **当前线 = B 组音频链路面板**：B1 勘察✅、B2 模型 `AudioSignalPath.cs`✅（快照 `Capture(engine, globalBypass)` + 静态事件环 `RecordEvent` / `RecentEvents(int)`）。**B3 面板（本轮）** = 新 `AudioChainPanelControl`（右侧滑出抽屉，复用「音频设置」那套 Visibility + IsHitTestVisible + TranslateX 动画）+ 入口 `NowPlayingChainButton`（播放页右上按钮组）+ 宿主 `MainWindow.AudioChain.cs`；铁律=**拿不到的数据整行不显示**（不写"未知/—"）。B4 接真实状态 / B5 主题适配 / B6 验收 未做。
- **左栏现状（10-16 下午三连修后）**：`NavToolPanel`（标签/音效处理/设置）**在** `LeftCategoryNavBorder` 卡片内，卡片通到窗口底。收起态对齐=Center + `LeftNavPaddingCollapsed=(15,2,15,2)`；对齐切换放在文字淡出 Completed 里。外壳（margin+投影）单一真相源=`ApplyLeftNavCardChrome`：收起态右留白 6（卡片 66 宽）+ **无 ThemeShadow**（渐带 25px 在窄条上显脏=用户说的"右边界有宽度"），切窄壳必须与列宽同帧（Completed 里）。emoji 图标墨迹偏右 +0.9 → 收起态 `Margin=(-1.5,0,0,0)` 补偿（只补可见 emoji）；对齐基准=`FindFirstVisibleNavIcon`（emoji 模式下 FontIcon 是 Collapsed，旧实现只找 FontIcon 导致对齐被跳过）。
- ⚠ **同一 UI 属性多处赋值必须抽单一真相源**（左栏已统一到 `NavContentAlignment` / `ApplyLeftNavCardChrome`）。
- ⚠ **XAML 故事板 TargetName 在本仓有解析失败案**（`MainContentDepthBackStoryboard` 打 `LeftCategoryTransform`，Begin 必抛 COMException；同写法的音频设置故事板却是好的，根因未明）。**稳妥做法：Begin 前代码 `Storyboard.SetTarget` 锁死目标**（`Features.RetargetStoryboard` 已封装，FindName + 字段兜底）。动画目标能用代码锁死就别留给名字解析——本族事故已三次（DataGrid 选中画刷 / LeftCategoryTransform 换实例 / 本次名字解析）。
- ⚠ **播放页景深只动右列**（`LibraryPaneRoot/Transform`）；**播放页展开时左栏整块隐藏**（10-16 晚间用户要求，推翻同天早先"仍显示"）：`LeftCategoryNavBorder.Visibility=Collapsed` + `CategoryColumn` 列宽/MinWidth 压 0，还原走 `RestoreLeftNavForNowPlaying`（可见性+列宽按 `_leftNavCollapsed`/`_leftNavExpandedWidth`+卡片外壳），关闭/降级两路径都调；展开期间 `SetLeftNavCollapsed` 直接 return（`_nowPlayingPaneOpen` 守卫）。
- ⚠ **待办清单在 `outputs/待办清单-*.md`**，不在本目录。未做：m3u「按单歌单导出」、Discogs/fanart 署名、标签页弃用 DataGrid + 去格内编辑（排 B 组后）、零散播放窗口 Q1~Q5。
- 零散播放定案：保留文件关联；做独立「零散播放」窗口（纯内存队列、不碰曲库不读 SQLite）；播放器内选文件 = 送该窗口不入库。

## 音频链路 / DSP
- 链路：ffmpeg PCM 缓存→SeamlessWaveProvider(gapless)→独占SRC→降混→ManagedDspSourceProvider（变速）→celeste_dsp_core.dll 机架(EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→电平表)。**全关=bit-perfect**；测量只读音频只写标签。
- 内核**只剩 native2**（10-16 拍板，ExclusiveEngine 强制归一）；self/echo 选项已删，ECHO 源码/dll 留档。
- 变速在托管侧（SpeedStretch.cs 挂 ManagedDspSourceProvider）：拖动即时变声、音调可选（保调=WSOLA / 变调=Hermite）；倍率回 1 排空 FIFO；seek/换曲清 FIFO；DSD DoP 直出不变速。设置项 `PlaybackRatePreservePitch`。⚠ C# 无 volatile double。
- 音频设置面板四页签（音频输出/播放设置/播放速度/睡眠定时），Flyout 内用 Grid+ShellPageHost；新控件事件带 `_audioSettingsReady` 就绪守卫。

## WinUI3 铁律
- 新界面模式三连：①设置白名单 ②UI ComboBoxItem Tag ③ApplyUiStyleMode 分支，缺一静默不生效。
- 取色只认 resolver 套；主题判断沿元素树找显式 Light/Dark 祖先，禁读 App.RequestedTheme。XAML 与 cs 同 commit。
- 秒崩 0xC000027B = XAML 初值 + 事件绑定 → 写后续 x:Name(null) → NRE；新面板 ValueChanged/Toggled 加就绪守卫。
- ⚠ 第三方控件模板的 VisualState 动画属性链**别在运行时打补丁**（换画刷实例 / ClearValue → 动画 Begin 解析失败 → COMException 0x80040154 崩）。DataGrid 细节见 ref-datagrid-toolkit.md。
- ⚠ **"稳定检测"判据要容得下自激振荡**：进度条启动补校（`ScheduleStartupTransportCentering`，DispatcherQueueTimer 150ms）实测有 ±1px 极限环（CenterOnElement 0.5px 写入阈值与"排一帧补量"错位，永远差 0.5px 永远写）→ changed 判据用 **2px 漂移阈值**（`MarginDrift`），精确比较会永远判"动"、定时器永远不停（195+ tick 实录）。
- ⚠ **兜底 Stop 必须无条件可达**：别把停机检查写在 `if (changed)` 的 else/return 后面——changed 恒真时永远走不到（本轮自学bug，探针两期才抓到）。另："改了没生效但代码明明对"→ 加临时探针日志让运行时说话，别盯代码猜。

## 标签板块（定稿）
- 位深两条路径：表格 TryGetFactsFast(TagLib only，有损容器显"-")；状态栏/歌曲面板 TryGetPartsCached(含 ffmpeg)。表格 15 列；拖列宽帧合并。单元格编辑=mp3tag 语义（唯一选中项才进编辑）。
- 歌词页 6 子页；外挂 .lrc 走 QuickLrcBox。⚠ xaml.cs 禁裸 `Path.` 用 `System.IO.Path.`。
- 在线标签 OnlineTagFill.cs（MB+iTunes 免 key / Discogs+Last.fm+franart 需 Key，Key 只落本机）。「标签编辑」入口统一走 OpenTagEditForFiles。

## 皮肤 / WebUI
- 皮肤入口只隐藏主界面 pill；智能播放列表④作废；设备记忆 DSP 档③活。令牌皮肤覆写全局文字刷子，浅色下读 TextFillColorPrimary 看不见。
- WebUI 路线 B：网页只长样；消息提 kind；winmain.html 两套图标表。

## 工程约定
- 用户不懂编程：大白话结论先行禁营销腔。推送：不 rebase / force-push、不 `git add -A`、commit 独立可回退、构建过才推；日常交付 `dist\run-latest\`。
- ⚠ 禁 UIA 点击冒烟：交付只做单次启动验证（`_smoke_runlatest.py`）。
- 源码偶发被清零（前 4096 字节 NUL>0.5 判坏，从 HEAD 恢复）。工作树大量未跟踪文件（dist/run-latest、native/build、.workbuddy 等）动不得。
- 构建/发布坑见 ref-build-publish.md；在线源商用条款见 ref-online-sources.md。
