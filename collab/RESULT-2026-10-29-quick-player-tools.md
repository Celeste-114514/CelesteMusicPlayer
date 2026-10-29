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
