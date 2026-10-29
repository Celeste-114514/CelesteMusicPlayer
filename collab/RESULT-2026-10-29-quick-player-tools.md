# 零散播放器：动效、均衡器横排、速度滑条（2026-10-29）

接 `TASK-2026-10-29-quick-player-rebuild.md` 之后的三项用户反馈。改动都落在零散窗，外加两处让"频点数字全站一致"的常量调整。

## 1. 动效和主程序一致

- `quick-player.html` 引入 `motion.css` / `motion.js`——和主界面同一套动效令牌，不在零散窗另写毫秒数。
- 宿主 `theme` 消息附带 `motion`（mode / speed / strength / bounce / off），值来自 `WebAppearance.EffectiveMotionSpeed` 等；
  外观里改速度、幅度、关掉某一类动效，零散窗跟着变（此前完全不跟随，因为它根本没加载 motion.js）。
- 接线：面板进/出 `CelesteMotion.popupEnter / popupExit`（退场动画结束才真正 `hidden`，期间再点同一按钮不会被旧动画关掉）；
  ESC / 点空白统一走 `closePanels()`；按钮按下 `press()`；队列重建 `fadeIn()`；歌词行颜色与字号走 `--dur-list`；
  换主题走 `--dur-theme`；窗口出现时跑一次 `page()`。
- 尊重 `prefers-reduced-motion` 和外观里的"关闭动效"（时长归零，结构、焦点、可读性都保留）。

## 2. 均衡器横过来 + 频点统一成 32/64

- 面板改成**十根竖条横排**，排布对齐主程序 `EqualizerWindow`：数值在上、滑块居中、频率在下；
  量程与步进也改成和主程序同口径 **±15 dB / 步进 1**（此前是 ±12 / 0.5）。
- 频点问题查清了：**31/62 是这套十段滤波器真正的中心频率**（`ManagedDspSourceProvider.UpdateEq` 的映射表），
  主程序里能看到的那页「参数 EQ」写的是 **32/64**，而且 `EqCurveState.Normalize` 还专门把老数据的 31/62 迁到 32/64。
  同一个程序里两套数字，看起来就像打架。
- 处理：**全站统一到 32/64**——`ManagedDspSourceProvider.UpdateEq` 映射表、`Eq10WaveProvider.Centers`（当前已无调用方）、
  `EqualizerWindow` 标签、`MainWindow.EqDsp.AppendSimple`、旧零散窗注释，零散窗面板标签同步。
  ⚠ 这不只是改标签：十段均衡器的两个低频中心真的从 31/62 移到 32/64（约 3%，听感无差），换的是"全站一个数字"。
- 预设补齐「柔和 Soft」，与主程序均衡器窗口的七个预设对齐。

## 3. 播放速度改成滑条

- 下拉框 + 复选框 → **0.5–2.00× 滑条（步进 0.01）+ 大字读数 + 「恢复 1.00×」+ 音调两个单选**，
  形状与主程序底栏「播放速度」一致。
- 只在 `change` 时下发（拖动过程只更新读数，不刷 IPC），写的仍是同一个 `PlaybackRate` / `PlaybackRatePreservePitch`。

## 验证

- `tools/WebShellQuickPlayerSmoke.cjs` 扩了断言并通过：EQ 是十根竖条（高 > 宽 × 4）、标签 `32…16K`、
  拖到 +6 发 `Custom` 且读数 `+6`、速度 `change` 发 `1.5` + `pitch=true`、切「音调随速度变化」发 `false`、
  恢复按钮发 `1`、打开面板有动画在跑、`--motion-scale` 跟随外观速度。
- 截图：`outputs/quick-eq-horizontal-20261029.png`、`quick-speed-slider-20261029.png`、`quick-eq-narrow-20261029.png`（620 宽不裁切）。
- 自包含 publish 退出码 0、0 错误；`dist/web-ui-20261028` 已同步 11 个文件（BAD=0），
  DLL md5 `d43fc79c837ac06a8e6ce821d57d95a7` 与 publish 一致。
- 备份：`dist/_prev_20261029-quick-tools`（`quick-player.*`、`quick-tools.js`、`CelesteMusicPlayer.dll`、`.pri`）。
- 没做：真实窗口里手动拖滑条的手感、DSD 直出下 EQ/速度被禁用的实机确认、多 DPI。

## 回退

- 源码：revert 本提交。
- 运行目录：完全退出播放器后，把 `dist/_prev_20261029-quick-tools` 里的文件按相对路径复制回 `dist/web-ui-20261028`。

---

# 追加（19:25）：三处错位修复

用户看实际窗口后报：均衡器错位、速度条不重合、频谱和分隔线重合。三个都是真 bug，根因各不相同：

## 1. 均衡器竖条整体右偏

`.slider-host` 只有 16px 宽，里面塞 132px 宽的 `input`，用 grid 的 `place-items:center` 居中——
**子项比容器宽时 CSS Grid 按"安全"策略退化成起始对齐**，输入框左端贴着容器左边溢出，旋转 -90° 后
整根竖条往右偏了 60px（实测：值/标签中心 462.4，滑块中心 522.4）。

改法：`.slider-host{position:relative}` + `input{position:absolute;left:50%;top:50%;transform:translate(-50%,-50%) rotate(-90deg)}`。
现在十列的数值、竖条、频率中心完全重合，`#eq-bands` 也不再横向溢出（scrollWidth == clientWidth）。

## 2. 速度条短一截、刻度和滑块对不上

两件事叠在一起：
- `<input type=range>` 没写宽度 → 用默认 129px，刻度行却有 346px，视觉上就是"一条短条 + 一排长刻度"。
- 刻度用 `justify-content:space-between` 排 → 1.00× 落在 50%，而 0.5–2.0 的线性轨道上 1.00× 其实在 **1/3** 处。

改法：`#speed-rate{width:100%}`；刻度改成绝对定位 `0 / calc(33.333% + 1.333px) / 右端`——
和主程序 `collections.css` 里的写法一字不差。实测滑块中心 734、1.00× 刻度中心 733.7。

## 3. 频谱压住分隔线、按钮点不到

`.album`（封面 + 信息 + 频谱）是固定尺寸堆叠：封面 344 + 信息 133 + 频谱 74 = 551，
而 740 高的窗口 body 只有 537（减内边距只剩 484），**整列溢出到 footer 上**：
频谱横穿分隔线；更严重的是歌词列也跟着溢出，直接**盖住底栏按钮**（Playwright 点"均衡器"被 `section.lyrics` 拦截）。

改法：
- 封面按窗口高度收：`.album{max-width:min(360px, calc(100vh - 448px))}`；
  频谱关闭时用 `:root[data-spectrum="off"]` 把高度让回来（`100vh - 382px`），默认状态封面仍是 344，和之前一样。
- `#spectrum` 的 `hidden` 用 MutationObserver 同步到 `html[data-spectrum]`（频谱是数据驱动显隐的）。
- 顺手收了一点纵向留白（body padding 28/25→22/18、标题外边距、频谱 62→56）。
- `.body{overflow:hidden}` 兜底：任何情况下都不许再压到底栏。

## 4. 顺带：弹层贴底栏

面板原来写死 `bottom:142px`，而底栏实际 149px → 面板下沿压在分隔线上。
改成 `bottom:var(--panel-offset)`，由 `ResizeObserver` 盯 footer 算出来（状态栏出现时底栏会变高）。

## 复测

- 1000×740 / 1000×620 / 880×560 三档实测：`album` 都没有溢出（over=0），频谱底边 565 < 分隔线 591；
  弹层下沿始终在 footer 上方 10px；十列滑块中心与数值/标签完全一致；滑块中心与 1.00× 刻度差 0.3px。
- `WebShellQuickPlayerSmoke` 全过、无 pageerror。
- 重跑 publish（exit 0、0 错误）后再同步：`quick-player.css/js` 在源目录 / publish / dist 三处 md5 一致。
- 截图：`outputs/quick-eq-fixed-20261029.png`、`quick-speed-fixed-20261029.png`、`quick-eq-fixed-short-20261029.png`（620 高）。

---

# 追加（19:35）：暂停不擦掉频谱 + 文件位置打开错目录

## 1. 频谱暂停时消失

两处一起导致：页面收到 `state.playing=false` 就 `line.hide()`；宿主在暂停时也把 `available` 发成 false，
共享组件 `spectrum-line.js` 一看到不可用就整块隐藏，500ms 没有新样本还会自己隐藏。

改法：组件加 `hold` 选项（只给零散窗开，主程序详情页行为不变）——
- 不可用/超时不再隐藏，而是冻结在最后一帧并**停掉 rAF 循环**（不空转）；
- 页面只在**队列被清空**或用户关掉频谱开关时才隐藏；
- 窗口最小化时保留画面，恢复后继续。

## 2. 「文件位置」打开的是「文档」

`explorer.exe` 的开关必须写成 `/select,"C:\路径\文件.flac"`（只给路径加引号）。
原实现用 `ProcessStartInfo.ArgumentList.Add("/select," + path)`，路径带空格时 .NET 会把整串
再包一层引号变成 `"/select,C:\a b\x.flac"` —— explorer 认不出开关，于是退回打开默认的「文档」。
改成和主程序 `MainWindow.Misc.cs` 完全相同的写法：`Arguments = "/select,\"" + path + "\""`。
已确认新字面量 `/select,"` 编进了发布 DLL。

## 复测

- `WebShellQuickPlayerSmoke` 断言改成：暂停后频谱仍可见、清空队列后隐藏、恢复送样后重新出现——全过、无 pageerror。
- publish 退出 0 / 0 错误；dist 同步 8 个文件 BAD=0，DLL md5 `d8cacf6f…` 与 publish 一致。
- 未做：真实窗口里点「文件位置」的实机确认（需要在他桌面上弹资源管理器，留给用户点一次）。
