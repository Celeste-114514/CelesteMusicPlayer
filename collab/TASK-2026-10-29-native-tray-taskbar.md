# 托盘菜单、任务栏播放条原生重构

## 目标与授权

用户反馈任务栏播放条已开但主界面仍 OFF、托盘菜单不能点击、两个功能启动很慢；指定参考 Twilight_Echo/ECHO，必要时完全重构。暂停 W6，优先完成本任务。没有调用 computer-use，没有启动正式播放器或操作用户窗口。

## 证据与参考

- 主界面 `shell.js` 渲染读取 `state.taskbarPlayer`，但 incoming-state 白名单只有桌面歌词的两个布尔字段，没有复制 taskbarPlayer，导致后台发送 ON 而按钮仍 OFF。新回归直接加载正式 index.html、注入真实消息形状，覆盖 ON/OFF/ON 和部分状态更新。
- 既往日志：菜单 HWND 能收到鼠标，网页没有 pointerdown；另有多次导航等待 20 秒后超时。共享/独立 WebView2 环境的多次修补未解决用户复现。没有把某一种环境冲突假设写成已确定的底层根因。
- Twilight_Echo-main/src/main/integrations/shortcutsTray.ts：原生菜单项直接路由动作；ECHO-main/native/taskbar-host/src/main.cpp：独立无激活 Win32 窗口、原生绘制、任务栏几何与输入分离。参考结构，不复制参考项目实现或资产，不引入 Electron。

## 最终实现

- `NativeDesktopPopup`：Win32 HWND、双缓冲 GDI+，直接接收鼠标/双击/键盘。绘制异常不跨越 unmanaged 回调；释放窗口、窗口类和绘图对象。
- `TrayMenuWindow`：保留 CELESTE 页眉、浅深配色、细线图标、分组及整张无滚动菜单；原生处理点击，保留 actionId 白名单。绘制与点击共用 `DesktopMenuLayout`；短工作区整体缩放并同步命中坐标。鼠标外点、失活、Esc 收起；上下键/Enter 可用。服务将命令延后到 UI 队列执行，避免回调内销毁窗口。
- `TaskbarPlayerWindow`：原生封面/标题/艺术家/三按钮/进度；共享既有播放状态与命令，不创建音频引擎。封面后台使用既有 Thumb 缓存，版本号防止换歌后旧图覆盖；窗口初始遵从暂停状态。保留 ECHO 对齐、任务栏背景采样、外观强调色、DPI、全屏/自动隐藏/空间不足让位、Explorer HWND 重建与 AppBar 释放。
- 两者均不再创建 WebView2 environment/controller，不再预热、导航、等待 JS 握手；移除原 popup environment 转发和任务栏网页封面桥。主界面继续使用现有 WebView2。
- 持久化开关作为主界面开关状态；切换后立即发送状态；shell.js 接收 taskbarPlayer。自动隐藏/全屏暂时让位不把用户开关改成 OFF。
- 旧 tray-menu/taskbar-player HTML 仍保留供历史回退，正式两个窗口不再加载它们。旧 WebShellTraySmoke/WebShellTaskbarPlayerSmoke 不再能证明现行原生窗口可点击。

## 验证

- `NativeDesktopPopupRegression` 链接正式 HWND、菜单、任务栏、布局、命令适配器源文件；只替换播放后端/队列计时器/日志。全部 HWND 隐藏，从不 Show/激活用户窗口。SendMessage 验证菜单每一行 -> 命令回调（含 Exit）、禁用/未知命令/分隔线拦截、文本缩放 .8/1/1.5 与短屏 .65 缩放命中、键盘 Exit、任务栏三按钮/忙碌拦截/双击恢复；40 次创建销毁 GDI 4 -> 4。隐藏 HWND 首次创建约 10–21ms，仅组件时间，不是整个应用冷启动时间。
- 生成并检查 `outputs/native-tray-light.png`、`native-tray-dark.png`、`native-taskbar.png`，直接调用正式绘制回调，不是屏幕截图。
- `WebShellTaskbarBadgeSmoke.cjs`：正式主页 ON/OFF 状态、部分消息保持、开关命令通过（headless Edge）。
- `PlaybackStateRegression`：补上此前新增但旧测试漏收录的 taskbarPlayer 协议字段，序列化与共享快照通过。
- `TaskbarPlayerRegression`：任务栏两种对齐、负坐标、DPI/Widgets/空间不足通过。
- Release 自包含 build 已成功（0 错误）。最初构建遇机器既有 CLR/XamlCompiler 崩溃，使用仓库 Invoke-PinnedDotnet.ps1 后通过；发布也使用此脚本。
- 隐藏窗口测试不能替代用户鼠标与 Explorer 的完整验收，尚需重启后确认系统托盘实际点击、ON 状态和首次出现速度；沿用主屏底部任务栏限制。

## 交付与回退

- 源码重构前备份：outputs/desktop-native-source-backup-20261029-004611/。
- 完整发布：outputs/native-desktop-publish/；交付仍为 dist/web-ui-20261028/，用户根目录“打开当前播放器.lnk”不更换。
- 同步前逐文件备份、记录 manifest；同步后校验主 DLL 与网页脚本哈希。交付记录补在下方。

### 已完成同步

Release/x64 自包含 publish exit 0，703 个发布文件完整覆盖固定运行目录；7 个变化文件已备份到 `outputs/native-desktop-runtime-backup-20261029-010242/`。同步时未运行播放器，未强制关闭用户进程。主 DLL 与正式网页 shell.js 均逐文件 SHA-256 校验通过。

DLL SHA-256：`00ce53459d5ccacc515e5ed7fac2f64b214676b79197d253890f4083d2365b22`。

网页脚本 SHA-256：`af87da4f32819f33432ad55736140693316bb8d1cbc4d08b0b5182a8f85ac6ba`。
