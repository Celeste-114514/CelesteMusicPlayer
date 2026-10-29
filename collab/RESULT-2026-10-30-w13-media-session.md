# W13 系统媒体控件（SMTC）搬出 MainWindow — 2026-10-30

提交 `1adf7db`。

## 结论

Windows 媒体浮层 / 媒体键 / 锁屏那一套（SMTC）已经从 MainWindow 里搬出来，
成为 `MediaSessionService` + 八成员宿主接口 `IMediaSessionHost`。真实播放验证通过。

## 搬了什么

| 原来散在 | 内容 | 现在 |
|---|---|---|
| `MainWindow.Playback3.cs` | 静默宿主 MediaPlayer 的创建与占位源、1 秒静音 WAV 构造、元数据与封面发布、状态切换 | `MediaSessionService` |
| `MainWindow.Playback4.cs` | 时间轴同步（`UpdateTimelineProperties`） | 同上 |
| `MainWindow.Features.cs` | 设置开关联动、媒体键按钮分发 | 同上 |
| `MainWindow.Playback.cs` | `_engineSmtc` / `_smtcHost` 两个字段 | 服务内部持有 |

MainWindow 只留八个事实：任务栏按钮同步、取封面字节、引擎时长、播放/暂停、下一首、上一首、暂停、回 UI 线程。

## 为什么要单独一个静默宿主（原样保留的坑）

- Unpackaged 应用不能用 `GetForCurrentView()`（打包专用）→ 只能用 MediaPlayer 自带的 SMTC。
- MediaPlayer 没真正播放时 SMTC 不显示数据（系统要求"有播放会话"）→ 挂一个**静音循环占位源**。
- 占位源已存在就**不重建**，否则播放会话重置会让系统浮层闪一下。

这三点在代码注释里都留着，别再"优化"掉。

## 验证（真实播放，非回包）

| 日志 | 说明 |
|---|---|
| `[SMTC] 静音占位源已挂载并 Play()，Source=有` | 占位宿主建起来了 |
| `[SMTC] 静音占位源已存在，跳过重建` | 二次调用没重建会话 |
| `[SMTC] 元数据已写入: Title=Epilogue Artist=Asriel Album=ABYSS Status=Playing` | 元数据发布正常 |
| `[SMTC] 封面已设置: 332038 字节` | 封面异步写入正常 |
| `[SMTC] timeline 已更新: pos=51.49 dur=98.80` | 时间轴在跑 |

无 SMTC 相关异常。全量 Rebuild 0 个 `error CS`，554 警告（与迁移前一致）。

## 没验到的

- **媒体键与系统浮层按钮没按过**（需要在真机上按键盘上的 ⏯/⏭，或点系统浮层）。
  按钮分发路径与旧代码逐行一致，但没有运行时证据。
- 「设置里关掉 SMTC」这个开关没有实测（要改设置并观察浮层消失）。
- 零散播放窗口接管时让出 SMTC（`MediaSession.Disable()`）没有实测。

## 进度

`IWebPlayerServices` 47 条成员：**15 条**已走独立服务。
播放域里已搬出：正在播放投影、队列、播放台账、系统媒体控件。
剩下：引擎生命周期、无缝接续、输出模式切换。
