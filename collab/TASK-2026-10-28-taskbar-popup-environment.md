# 任务栏播放条：换掉它复用的主界面网页环境

用户 23:26 让我接手 codex 的工作。接手前先看了托盘的实测结果：

- 22:59 / 23:00（托盘用**自己建的**环境）：`网页收到 pointerdown` → `收到命令 openSettings /
  openLogs / exit` → 全部执行 —— **点击已经完全恢复**。
- 22:43 / 22:53（托盘复用主界面环境）：一次 `网页收到 pointerdown` 都没有。

而 codex 的任务栏播放条（`TaskbarPlayerWindow.TickAsync`）同样在用主界面的
WebView2 环境（`_owner.TraySharedWebViewEnvironment`），所以**它的按钮也会点不动**。

## 改动

1. `TrayMenuWindow` 暴露 `PopupEnvironment`（自己建的、能正常收点击的那套）。
2. `WindowsTrayService` 转发为 `PopupEnvironment`。
3. `MainWindow.TraySharedWebViewEnvironment` 改名 `PopupWebViewEnvironment`，
   指向**托盘菜单**的环境，不再指向主界面网页控件。
4. `TaskbarPlayerWindow` 改用它。这样两个弹出窗口共用一套浏览器进程，
   既省内存，输入链路也是正常的（都是窗口版 controller）。
5. 顺手收敛托盘首次导航的等待：`NavigationTimeoutMs` 20s → **6s**，预热重试间隔 2s → **0.5s**。
   依据 22:59 那轮：第一次导航撞满 20 秒超时，重试只用了 2.4 秒 —— 早超时早重试，
   整体就绪时间从约 24 秒压到约 9 秒。

## 没动的地方

- `WebShellWindow.WebViewEnvironment` 保留，但没有任何弹出窗口再引用它
  （属性上已写明"不要给弹出窗口用"）。
- 任务栏条的布局、AppBar、对齐、DPI 逻辑一行没动，那是 codex 的实现。

## 待用户实机验收（W11 清单，来自 TASK-2026-10-28-taskbar-player.md）

开启/关闭/重开、两张 DSD 专辑和普通 PCM 切换、上一首/暂停恢复/下一首、双击恢复、
主题变化、Win+D、开始菜单、全屏、Explorer 重启、高 DPI。

重点看：按钮点得动、音频不重建、工作窗口不被抢焦点、系统按钮不被遮挡、关闭后无残留窗口。
