# CelesteMusicPlayer 项目长期记忆
> 只留跨会话有效事实。提交查 git log；当日查 memory/YYYY-MM-DD.md；构建发布见 celeste-build-verify skill；细目见 memory/ref-*.md。

## 工作主线
- ⚠ **网页外壳重做（10-20 启动）**：不再用 Apple Music 试点；做"Windows 风脱离 WinUI"新界面。**上次失败根因=平行界面架构**（每原生页在 web 重抄一遍，维护无底洞），这次=一个网页一整块界面+内部路由、先浏览器调再接 C#。原型在 `outputs/webui-proto/`（纯静态，双击即看，host.js 双模式兼容真 WebView2，协议同旧 WebHost.cs）。BiliPai 动效参数已提取进 tokens/motion.css（曲线/时长/spring/液态指示器原理）。旧壳备份 C:\Users\MSI\_celeste_skin_removed_20261018\（WebHost.cs 可整搬）。详见 memory/2026-10-20.md。
- ⚠ **网页皮肤 + 令牌测试皮肤 10-18 已全删（用户拍板，未提交）**：仓库外备份 `C:\Users\MSI\_celeste_skin_removed_20261018`（网页皮肤与 token/ 两套）。**保留**主题文件机制（CelesteTheme.cs + CelesteTokenApplier.cs + MainWindow.ThemeFiles/CelesteTheme.cs，与测试皮肤无关，用户 ClassicLight+主题文件照常生效）。教训：删文件后必须全仓 grep 方法名抓调用点；沙箱拦覆盖写会让 .g.cs 变 0 字节 → XamlCompiler 确定性 exit 1，须 obj/bin 整体搬出树外重建。
- 令牌化遗留：圆角 7 档已落地；主题文件=明/暗颜色组+metrics；**动效层全项目为零**（新皮肤主要工作量）；主题色底字色统一走 Accent.Text 单一来源；两条主题色链已合一（ResolveEffectiveAccentHex 为唯一真相源）。
- ⚠ **UserControl 里选文件不能 `WindowNative.GetWindowHandle(this)`**（InvalidCastException）→ 统一 `MainWindow.PickerHostHandle()`；主窗口自己用 this。
- ⚠ 标签页**保留 DataGrid**（用户 10-17「现在挺好的」）→ 待办里"弃用 DataGrid"**作废**；**零散播放窗口 Q1~Q5 已作废**（改独立窗口迭代，见下）。
- B 组音频链路面板（Echo 风格信号路径）：B1-B4✅（AudioSignalPath.cs + MainWindow.AudioChain.cs 右侧抽屉）。**下一步 B5 主题适配细修 + B6 验收清单**。铁律=拿不到的数据整行不显示。
- ⚠ **零散播放窗口（ScatteredPlayWindow，10-18 定案并交付两批）**：自持引擎独立播放器，纯内存队列不碰曲库；冷启动直达不建 MainWindow。已做：十段 EQ / 设置菜单（文件位置·属性·倍速）/ 常驻音量条；**滑条一律白**（本地 Background=#38FFFFFF+Foreground=#FFFFFF 压全局素条模板，走 TemplateBinding）；**窗口 chrome 自绘**（SetBorderAndTitleBar(false,false)+标题栏折叠 0 高；固定 540×740、无最大化、自绘最小化 E921/关闭 E8BB）；**外圈 1px 白线框必须原生层收尾**（ScatteredPlayWindow.Chrome.cs：剥 WS_THICKFRAME/WS_CAPTION/WS_BORDER/WS_DLGFRAME + 子类化吞 WM_NCCALCSIZE/WM_NCPAINT + DWMWA 圆角 DoNotRound + 边框色推背景色兜底 + SetWindowRgn **20dp 圆角**裁剪区内缩 2px 剪丙烯白边；⚠ SetWindowRgn 在 **user32 不在 gdi32**（CreateRoundRectRgn/DeleteObject 才是 gdi32，挂错被 Safe 吞不崩但裁剪区整个失效）；不加 WS_EX_TOOLWINDOW 保任务栏按钮）；EQ 图标 E9D9 + 队列/EQ 面板**原地视图**（Grid.Row=1 主体区让位，不盖进度/传输行）+ 开启动画（代码 Storyboard SetTarget + 延帧 Begin + 400ms 兜底）；**文件属性走 shell32 SHObjectProperties**（Process.Start Verb=properties 对 .m4a 等抛 SE_ERR_NOASSOC，日志实证）；背景四态 Cover/Dark/Light/**Frosted 毛玻璃**（RootGrid 65% 深色纱透 DesktopAcrylic）。
- ⚠ **零散窗口拖动（10-19 定案，改过三版才对）**：**只能用轮询定时器**——按下那一下进来后
  16ms 一帧自己 `GetCursorPos` + `AppWindow.Move`，用 `GetAsyncKeyState(VK_LBUTTON)` 判松手。
  三条常规路在本窗口全不通：① XAML 手动跟踪（PointerMoved 收不到）② `SendMessage(WM_NCLBUTTONDOWN)`
  （鼠标消息是发给 XAML 子窗口 DesktopChildSiteBridge 的）③ `SetCapture` 到顶层窗口（按下能进但窗口不动）。
  **两个硬坑**：`IsLeftButtonPressed` 在开了 `AllowDrop` 的元素上恒为 false（别拿它当守卫）；
  `IsControlElement` 类判断**只能往上查 3 层**（无限上溯会追到窗口内容根的滚动容器，把顶栏判成控件）。
  验证用 `_drag_probe_scattered.py`（模拟真实拖 + 读坐标差）+ `_drag_probe_control.py` 对照；
  ⚠ 收场要发 WM_CLOSE 正常关窗（kill 会丢日志缓冲，会误判），且先 `WindowFromPoint` 确认没被全屏游戏挡住。
- ⚠ **Cover 态压暗纱 = 迷你那套**：采封面主色 → `CoverTint.BuildDimGradient`（保色相/饱和度、亮度压 28%→13%）→
  做成 alpha 199→219 的半透明渐变盖在封面上（封面透约两成，白字白条可读）；取不到色兜底 82% 纯黑（原来 65% 不够）。
- ⚠ **AppSettingsStore.Clone() 逐字段显式拷贝，加字段必须同步**——漏拷=「存得进文件但 Load() 恒默认」，已五例（+ScatteredPlayBackground 白名单：Dark/Light/Frosted/Cover）。
- ⚠ **CS0176 跨 partial 文件别照抄写法**：xaml.cs 无 `using Microsoft.UI.Dispatching` 时裸写 `DispatcherQueue.GetForCurrentThread()` 只解析到 Window 实例属性 → 报错；Settings.cs 有该 using 同样写法却能编译。本文件一律全限定。
- 左栏单一真相源：`NavContentAlignment` / `ApplyLeftNavCardChrome`；**同一 UI 属性多处赋值必须抽单一真相源**。
- ⚠ 浮层遮罩别用 `Grid.RowSpan` 盖全窗（吃标题栏点击）→ TitleBarFloatingButtons 加 Canvas.ZIndex=100；面板入口按钮不做开关。
- ⚠ **Collapsed→Visible 后不同帧 Begin 动画**（被静默吞）：`DispatcherQueue.TryEnqueue` 延一帧 + 400ms 兜底复查；收起侧先 Stop 动画与兜底定时器。
- ⚠ **XAML 故事板 TargetName 有解析失败案** → 代码 `Storyboard.SetTarget` 锁死目标再 Begin。
- 播放页景深只动右列；播放页展开时左栏整块隐藏（RestoreLeftNavForNowPlaying 还原）。
- 封面分档缓存 `CoverArtService`：Thumb **160**（10-19 由 96 提上来；96 在 200% 缩放下被放大＝用户报的"播放栏糊"，
  而歌曲面板走 Album 320 所以清楚）/ Album 320 / Large 768；JPEG 编码质量设 **0.92**（默认质量在小图上块效应＝"糊"）。
  ⚠ **改档位必须把 `CacheVersion` +1**（v1=96、v2=160），否则旧档一直命中、新档永不生效；Large 档单张约 258KB，缓存会明显变大。
  排查"糊/没走缓存"看日志 `[播放栏封面] 缓存=命中/现解码 像素=WxH`（在 **logs/app** 当日日志）。
- 迷你播放器底色 = `CoverTint` 取色压暗渐变（`UpdateMiniCoverTintAsync`，换歌才重算）；极客皮肤激活时用 `_geekMiniActive` 守卫跳过。
- ⚠ **迷你播放器 10-19 已按单向数据流重建并交付**（旧版曾整包删除，备份 `C:\Users\MSI\_celeste_mini_removed_20261019\`）：
  新三件套 `MiniPlayerWindow.xaml/.xaml.cs/.Chrome.cs` + `MiniPlayerBridge`（快照+命令）+
  `MainWindow.MiniPlayer.cs`（PositionTimer 200ms 推、早退也推）。**迷你窗零播放逻辑**。
  设置项（MiniPlayerAlwaysOnTop / OpenMiniPlayerOnStartup）保留复用；设置面板那节先 Collapsed。
  480×118 横向小条 + **扩展区**（词/队按钮→轮询定时器逐帧 Resize 长高，展开 220ms/收起 180ms，
  歌词页切歌换引用才重建、队列页开着才刷）+ 立体感（封面洋葱投影 3 层 + 播放键凸起 + 受光 rim）。
  封面 72px 走 Thumb 160 档（不再从 Large 768 硬缩）。方案/拍板见 `outputs/迷你播放器重建方案-2026-10-19.md`。
- 待办清单在 `outputs/待办清单-*.md`。未做：m3u 按单歌单导出、Discogs/fanart 署名。main 本地领先远端较多，未推送。

## 音频链路 / DSP
- 链路：ffmpeg PCM 缓存→SeamlessWaveProvider(gapless)→独占SRC→降混→ManagedDspSourceProvider（变速）→celeste_dsp_core.dll 机架(EQ31→卷积→RG→压缩→crossfeed→声场→矩阵→balance→Headroom→限幅→电平表)。**全关=bit-perfect**。
- 内核只剩 native2；变速在托管侧（保调=WSOLA/变调=Hermite，DSD DoP 不变速）；音频设置面板四页签带 `_audioSettingsReady` 守卫。

## WinUI3 铁律
- 新界面模式三连：①设置白名单 ②UI ComboBoxItem Tag ③ApplyUiStyleMode 分支，缺一静默不生效。
- 取色只认 resolver 套；主题判断沿元素树找显式 Light/Dark 祖先，禁读 App.RequestedTheme。
- 秒崩 0xC000027B = XAML 初值+事件绑定写后续 x:Name(null) → NRE；新面板 ValueChanged/Toggled 加就绪守卫。
- 第三方控件模板 VisualState 动画属性链别运行时打补丁（COMException 0x80040154 崩）。
- "稳定检测"判据用 2px 漂移阈值（精确比较会永远判"动"）；**兜底 Stop 必须无条件可达**（别写在 if(changed) 后面）。
- ⚠ StartupLog 按 CallerFilePath 文件名推断日志分类：含 Playback/Audio/HiFi → logs/audio/；含 MainWindow/Visualizer/Lyrics → logs/app/。**查零散窗口必须同时看两个文件**（_smoke_scattered2.py 是范本）。
- ⚠ 自持引擎的新 Window 必须在构造函数赋 `HiFiOutputBackend.UiDispatcherQueue = this.DispatcherQueue`（否则不出声）。
- 自定义 Slider 模板必须横竖两向都做部件（官方 Slider_Partial.cpp：HorizontalTemplate 恰 3 列 / VerticalTemplate 恰 3 行，圆头靠 Grid push 不设 Margin）；App.xaml 全局素条模板两向已齐，照抄结构。
- 面板内图标用 Segoe 字形 FontIcon，**只取程序里已在用的字形**（E8BB 关/E921 最小化/E9D9 均衡器·波形/E713 处理/E767 音量/E768 播放/E893 变速/E710 打开文件/EA37 队列/E958 封面占位/E8EE 列表循环/E8B1 随机/E8FD 顺序/E8ED 单曲循环/E72A 单曲播放/E892 上一首/E893 下一首/E8D2 属性）。

## 标签板块 / 皮肤
- 位深两条路径：表格 TryGetFactsFast；状态栏 TryGetPartsCached。歌词页 6 子页。在线标签 OnlineTagFill（MB+iTunes 免 key）。xaml.cs 禁裸 `Path.`。
- 智能播放列表④作废；设备记忆 DSP 档③活。换前端=另起炉灶，别翻已删的网页皮肤代码。

## 工程约定
- 用户不懂编程：大白话结论先行禁营销腔。推送：不 rebase/force-push、不 `git add -A`、commit 独立可回退、构建过才推；日常交付 `dist\run-latest\`；禁 UIA 点击冒烟（只做单次启动验证）。
- ⚠ publish 前 obj/x64·bin/x64 搬不动（WinError 5 拒绝访问）→ 先 `dotnet build-server shutdown` 再重试；XamlCompiler 连崩 5+ 次属环境噪音（CS_err=0），重试脚本会自动重跑。
- ⚠ 冒烟判据 SUSPECT_LINES=0 抓不到「被 Safe() 吞掉的真错」（如 P/Invoke 挂错 DLL 的 EntryPointNotFoundException）——**必须肉眼过一遍新增 ERR 行**再下 PASS 结论。
- ⚠ 验证 run-latest 新鲜度**别信 exe**：exe（303KB）是跨构建字节不变的启动器外壳，真代码在 `CelesteMusicPlayer.dll`——对 DLL 做 md5 比对 + 搜方法名，别 ls exe 时间戳。
- 源码偶发被清零（前 4096 字节 NUL>0.5 判坏，从 HEAD 恢复）。工作树大量未跟踪文件（dist/run-latest、native/build 等）动不得。
- 构建/发布坑见 ref-build-publish.md；DataGrid 见 ref-datagrid-toolkit.md；在线源条款见 ref-online-sources.md。
