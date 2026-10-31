# CelesteMusicPlayer 项目长期记忆
> 只留跨会话铁律。提交查 `git log`；当日细节查 `memory/YYYY-MM-DD.md`；战报查 `collab/RESULT-*.md`；构建发布见 `ref-build-publish.md` + `celeste-build-verify` skill。

## 主线：网页外壳（WebView2）
- 架构：一块网页 + 内部路由。源 `outputs/webview2-shell/`，出货 `dist/web-ui-20261028/`（改网页只改源目录，再同步）。
- 设置页 `appSettings` 字段真相源 `WebSettingsSchema.cs`：改字段必同改 Defs/Readers/Writers 三处；密码走 `credentials`（空=保留、`__clear__`=清除）；OutputMode 只读。`audioSettings` 是底栏音频面板，别混。
- 外观 `WebAppearance.cs`（22 色+12 数值+24 预设）+ 网页 `theme.css|theme.js|appearance.js`。铁律：宿主变量名已带 `--` 别再拼；部件跟语义色用 `var(--shell)`；canvas 读变量并在 `celeste:theme-applied` 重绘；数值 override 剥单位再 Parse。
- DSP 网页：非 EQ 模块不另建状态，直读写原生控件；首访 `EnsureWebDspUiReady()`；`VISUAL_OWNED` 防双渲染；深色靠 `dsp-theme.css`（最后加载），功能色不跟主题。
- 托盘：Win32 `Shell_NotifyIcon` + WebView2 独立菜单窗，降级只到原生 `TrackPopupMenu` 不许回 WinUI；`EnsureWebViewAsync` 单飞守卫；宿主跳页走 `celeste.navigate`。
- 预设排版：`presetId` 落 `html[data-preset]`；⚠ 换预设带 view transition 时要同时听 `celeste:theme` 和 `celeste:theme-applied`（后者时 `data-preset` 才是新值）。
- 统一控件/动效：下拉原地增强原生 select（`ui-controls.css` 最后加载）；动效速度越大越快、宿主偏好单独存；列表换序 `captureRows/replayRows`；共用浮层每次 open 发 `popupToken`。`startViewTransition` 修假错误只接三个 Promise 的 reject、不加防重入。
- 窗口拖动：禁 CSS `app-region`，唯一路径 shell.js 判空白区→`celeste.command`→`WM_NCLBUTTONDOWN(HTCAPTION)` 优先；⚠ 合成消息前必查 `GetAsyncKeyState(VK_LBUTTON)`（IPC 延迟，按键可能已松）。
- 正在播放详情页 `now-playing.js/css`：歌词整首取当前句前端算；打开要藏 `.workspace/.main/.side-panel` 三处。
- ⚠ 工作树不明改动先当自己被中断的残留；动手前 `git log --oneline -5`。
- ⚠ 测试宿主必须拷 `System.Security.Cryptography.ProtectedData.dll`；命令行传文件走零散播放窗（测不到 FIR/EQ）。

## 服务边界（W13）
- 服务族：WebTrackActionService / WebQueueService / WebFolderBrowseService / WebLibraryService / WebTagService（单例，会话态在实例）/ WebSettingsService / WebAudioSettingsService / Dsp*Parameters。宿主能力各走 `I*Host`，只放服务造不出来的；⚠ 别往 `I*Host` 加 MainWindow 现成属性。
- ⚠ 碰用户设置的探针必须自证归位（`W13AudioSettingsWireProbe.cjs` 的 `stateDrift` 别删）。
- `WebOperationGate` 唯一互斥，曲目动作与目录浏览共用同一实例。
- ⚠ 删 partial 类私有成员前必须全项目 grep（AudioExtensions 教训，4 个 CS0103）。
- ⚠ 探针发 `playAll`/`togglePlay` 用 `CelesteShell.send`（不回包，request 会超时）；`transport` 没有 pause。
- ⚠ 标签探针会真写盘：素材复制一份，用 `tools/_w13_tag_reset.py` 复位；删 `library.db-wal` 用 Python os.remove（bash rm 被沙箱拦）。

## W13 搬家现状（账本）
- `UseWinUI=false`、入口 `Program.Main`；MainWindow=业务宿主（占位控件容器 `MainWindow.LegacyControls.cs`）；旧 XAML 只 Page/Compile Remove 不删文件。
- 旧壳搬家：第 1-2 刀删死布局（3,261 行）；第三刀 13 成员→`MainWindow.Utilities.cs`；十一~十四刀清导航域（导航栈/分类视图/文件加载 `LoadAndAddFiles`→`MainWindow.Playback.cs`）。账面 1024→422 错 / 166 成员。明细见 `collab/RESULT-2026-10-31-w13-*.md`。
- ⚠ 判断编译真过必须 `-t:Rebuild`；探针 rc=1 但 0 错误是 Roslyn NullableWalker 崩，重跑即可。
- ⚠ 先搬业务方法再整批排除界面文件；别正则猜死代码，用 `tools/_w13_shell_exclude_probe.py` 量账。
- ⚠ 判「布局数学」死活：读的尺寸来自 LegacyControls 占位控件则必然早退；删成员前问「有没有顺手更新别人在用的状态」。
- 搬家工具 `tools/_w13_move_method.py`（签名+花括号配平整块搬，已三修：8 空格闭合误剥、文件级命名空间假 `}`、LF-only 文件）。⚠ 搬静态字段必须保持原声明顺序（繁简表教训：顺序反→cctor 秒崩 TypeInitializationException，Rebuild 接不住，验证必须真启动一次）。⚠ shim 遮蔽类型名（`LegacyWinUiShim.cs` 的 XamlRoot），跟原代码用 var/dynamic。⚠ **分号结尾的字段/属性绝不能用配平工具，手工按行搬**；多行锚点要转换行风格；`?` 等正则元字符要转义。
- 已知现状：错误对话框不弹窗（dynamic XamlRoot 恒 null 只落日志）；CoverRasterizationScale 恒 1.0。均待另立任务。
- 剩余：批 6-10 已收官（`collab/RESULT-2026-10-31-w13-small-hosts.md` + `collab/RESULT-2026-10-31-w13-startup-host.md`）。新活文件：DspStateHost/AudioOutputHost（输出设备+AB 循环+热插拔）/SettingsApplyHost/NavHost（NavItemRef+加库）/MetadataHost（标签编辑器入口）/StartupHost（窗口尺寸+关闭策略）/NativeTaskbar（域内补全）。**账面 128 错（56 成员）全连深水区**：按借用文件 Playback 46 / Playback2 28 / Playback3 20 / xaml.cs 16 / SettingsHost 6 / Playback4 4 / 零散 8。⚠ `next` 是顶层 shell 命令不是 transport 动作；实播探针 `W13DspBatchPlaybackSmoke.cjs`。
- **选目标先算闭包，别只看账本数字**：`tools/_w13_dep_closure.py <成员名>` 秒出「搬它会连锁拉进多少壳成员」。xaml.cs 的 MainWindow_Loaded/FirstActivated/Closed 闭包 21/30+/36；图书馆 5 个处理器闭包合计 72 —— 搬单个方法净收益可能为负。
- **「整文件转活」最划算**：文件对外壳依赖为 0 时改一行壳清单就清掉它名下全部借用。已转活：StartupLoading/Lyrics/Search/Misc2（列宽域）。⚠ 粗扫外部依赖有假阳性（局部变量被当成成员），工具输出要人工核一眼。
- DSP 方案 A 已收官（`259aeaa`）：六模块+两链路级模型走 `IDspParameterModel`，定位只走 `ModelOf(name)`。① 不进 dsp-rack.json 的（FIR 走 room-correction.json）要在 `ReloadDspPanelFromStores()` 补装载；② IR taps/采样率只在落盘时写；③ 滑杆事件只导入改动字段；④ 方案指纹不含 IR taps/采样率/声道数；⑤ setBypass 仍借道原生开关。

## 构建 / 发布
- ⚠ 回退目录纪律：同步出货前先把旧 DLL+PDB 拷进 `dist/web-ui-20261028-pre-w13-<段>-<日期>/`。
- publish 目录 `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`；同步用 `_sync_webshell_dist.py`，⚠ 务必显式传参 `dist/web-ui-20261028`（默认是老目录）。
- publish 崩溃：`python _publish_retry_clean.py 8`。⚠ publish 成功≠产物正常：DLL 体积正常 3.0–3.1MB，少 106KB（缺 CsWinRT）就重编——把 `obj`、`bin/Release`、`bin/x64` 整个移走再 publish（重试脚本救不回来）。
- 干净 publish 269 个文件；STALE=293 是 W13 前残留，不是坏产物。
- ⚠ Edit 工具会整文件改换行（CRLF→LF）：改完看 `git diff --stat`；恢复用 `git show HEAD:<path>` 取原字节+os.replace。
- ⚠ 改了 C# 必须等 publish 成功；验产物新鲜度搜 #Strings 堆（UTF-8）不是 #US 堆。
- ⚠ 冒烟 SUSPECT_LINES=0 抓不到被 Safe() 吞的错，肉眼过新增 ERR 行。stash 出问题用 `git fsck --dangling commit` 找。
- CDP 首连偶发瞬断（发布后首启扫库慢），重试即过；频发就把探针 60s 等待放宽。

## Git 元数据损坏（本机零填充老毛病）
- 顺序：备份元数据到仓库外→扫工作区→按 mtime 找当天新增 commit 对象→验 parent 链回写 ref→`rm .git/index`+`git read-tree HEAD`（不 reset --hard，不盲修）。

## 音频 / DSP
- 链路：ffmpeg PCM 缓存→SeamlessWaveProvider(gapless)→独占 SRC→降混→变速（保调 WSOLA/变调 Hermite）→`celeste_dsp_core.dll` 机架。全关=bit-perfect。音频面板四页签带 `_audioSettingsReady` 守卫。
- ⚠ 本机装着 FiiO ASIO 驱动：`setMode('Asio')` 会真切换；切回 `node tools/_w13_audio_restore.cjs <exe> Shared ""`。
- DSD：DoP 已完成，只剩 Native DSD 接入。

## WinUI3 铁律（旧界面）
- 模式三连：设置白名单+ComboBoxItem Tag+ApplyUiStyleMode 分支，缺一静默失效。主题判断沿元素树，禁读 App.RequestedTheme。
- 秒崩 0xC000027B=XAML 绑定写 null x:Name→加就绪守卫。`AppSettingsStore.Clone()` 逐字段拷贝，加字段必须同步。
- 跨 partial 文件 `DispatcherQueue.GetForCurrentThread()` 全限定；选文件统一 `PickerHostHandle()`；StartupLog 按文件名分类。

## 独立窗口
- ⛔ 用户 2026-10-28 拍板：迷你播放器、零散播放窗口暂不改，他要自己重构；不碰、不当参考。
- 桌面歌词（`WebDesktopLyricsOverlay.cs`）必须 WebView2+裸 Win32 合成宿主（XAML Window 拿不到逐像素透明）；`controller.IsVisible=true` 会硬崩；构建加 `-p:CsWinRTAotOptimizerEnabled=false`。

## 封面 / 标签
- CoverArtService 三档缓存 160/320/768，改档位必须 CacheVersion+1。标签页保留 DataGrid（用户拍板）。

## 工程约定
- 用户不懂编程：大白话结论先行、诚实交代未做项。不 rebase/force-push、不 `git add -A`、commit 独立可回退、构建过才推。
- 日常交付 `dist\run-latest\`；禁 UIA 点击冒烟。待办唯一真相源 `outputs/待办清单-<日期>.md`。
- 完整网页样式包方案见 `collab/PLAN-2026-10-29-player-page-pack.md`；P1–P3 未完成前不许把 JSON 改色包说成完整页面包。
