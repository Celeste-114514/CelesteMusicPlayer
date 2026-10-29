# W13 第二步：外观业务迁出窗口

将约540行外观命令/验证/草稿/预设/资源导入逻辑迁为独立WebAppearanceService，服务不引用MainWindow或WinUI。生产WebShellWindow直接调用该服务并传入原生picker HWND；背景资源域名/目录也从服务读取。MainWindow仅保留兼容转发器，供旧调用方过渡，不伪装为整个项目已移除WinUI。

验证：完整自包含publish退出0（outputs/w13-appearance-build.log）。AppearanceRegression同时链接独立服务与兼容转发器；WebShellAppearanceIntegration通过原始值撤销、快速连续编辑、独立手势、双明暗预设、文件选择取消、资源不可变、真实WebM播放、隐藏恢复/释放和持久化。初轮媒体ready超时，额外构建HEAD原实现做基线对照通过，新实现重跑也通过；未放宽断言。测试定位history-undo，避免与左下新增撤销混淆。

生产DesktopBugsSmoke真实后台进程两阶段验证：迷你原生页面加载并收到状态；左下应用提交；真实进程退出并重启后外观保留；撤销取消未应用预览；最终恢复用户原外观。所有测试不抢焦点/不播放音乐，不改默认关联。

W13尚未完成：隐藏MainWindow仍持有曲库/播放会话等业务。下一步迁出其他设置/曲库业务及正式播放会话，之后才能切原生应用生命周期并去UseWinUI。
