# W13 第一块业务真正搬出 MainWindow：恢复 + 诊断 — 2026-10-30

## 结论

「页面崩溃 / 上次没正常退出」恢复卡和「关于 / 诊断」这一域，实现已经从 `MainWindow` 里搬出来，
变成独立类 `WebRecoveryService`。这不是转发壳：代码本体移过去了，`MainWindow` 只剩四个宿主口子。

编译 0 错误、554 警告；自包含发布后真实进程回归 PASS（含恢复 status、诊断导出两条新路径）。

## 改了什么

| 项 | 变化 |
|---|---|
| 新增 `WebRecoveryService.cs` | 恢复卡（crash / unclean）、安全模式、诊断快照、诊断导出、打开诊断目录 —— **全部实现**都在这儿 |
| 新增 `IWebRecoveryHost` | 服务向宿主打听/请求的四件事：`QueueTrackCount`、`IsPlaying`、`TogglePlayPause()`、`ReloadShellForRecovery()` |
| 新增 `MainWindow.WebRecoveryHost.cs` | MainWindow 实现上面那个接口（4 行代码），并提供 `Recovery` 属性（线程安全懒初始化） |
| 删除 `MainWindow.WebRecovery.cs`、`MainWindow.WebDiagnostics.cs` | 实现已搬走，文件不再需要；内容见 git 历史 |
| `LegacyWebPlayerServices` | 这 5 条改成走 `backend.Recovery.*` |
| 外部引用 2 处 | `MainWindow.Playback.cs` 的自动续播判断、`MainWindow.UiTheme.cs` 的设置损坏提示，改读 `Recovery.SafeModeActive` / `Recovery.ShouldShow()` |

## 验证

- 全量 Rebuild：0 错误、554 警告（首次编译撞到已知的 Roslyn 0x80131506 环境崩溃，原样重试即过）
- 自包含 publish：成功
- `tools/W13InterfaceRegression.cjs` 真实进程回归（冷启动 + 重载）：
  - 12 条既有命令全部 ok（4041 首曲库、74 个设置定义、10 段 EQ、7 个网络来源类型…）
  - 新增探测 `recovery {action:status}` → ok，返回 mode/show/safeMode
  - 新增探测 `diagnostics {action:export}` → ok，返回 path/fileName（导出真的写出来了）
  - 无 JavaScript 异常；点关闭后进程 exit 0

## 口径

- 这一块是**真迁移**，可以作为后面几块的样板：接口要窄、实现整个搬走、宿主只留口子。
- 但整体 W13 仍未完成：`LegacyWebPlayerServices` 里还有 43 个成员是转发给 MainWindow 的，
  播放会话、曲库、DSP、设置四大域仍住在 MainWindow 里。
- 恢复卡涉及播放暂停，本轮回归**没有实际播放声音**，`TogglePlayPause` 这条只在代码路径上核对过，
  没有实声卡验证（沿用「不打扰本机」的口径）。
