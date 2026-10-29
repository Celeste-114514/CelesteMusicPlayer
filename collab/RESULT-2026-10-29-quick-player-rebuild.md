# 零散播放器重构交付（2026-10-29）

默认页面替换正式零散窗口入口。冷启动外部文件/--quick 在 LibraryDb.EnsureMigrated、MainWindow 构造前分流。独立临时队列、批量转交去重、CUE 分轨、封面/歌词、输出设备、媒体控制、暂停协调均已接线。普通快捷方式在 quick-only 进程存在时激活已有窗口；完整 HTML/CSS/JS 页面包仍按原方案延后。

播放键通过两侧等宽布局保持窗口中心，包含前后曲、播放暂停、打开替换、追加、顺序、停止、音量、输出设备、临时队列、导出/清空、置顶等。两处频谱均由真实 FFT 数据绘制竖条，失采样500ms、暂停或隐藏后消失，不模拟数据。进度条显式绘制无边框轨道。

验证：Release publish 成功（outputs/quick-player-release-build.log）；QuickPlayerRegression 全通过（outputs/quick-core-regression3.log），覆盖 CUE、真实 PCM 管线、暂停恢复、停止重播、解码期间停止/退出不复活和曲库连接零增长；WebShellQuickPlayerSmoke 覆盖1000/620/460宽度居中、队列/设备命令、竖条及无采样隐藏和详情订阅关闭；已有 WebShellNowPlayingSmoke 通过。早期真实窗口已看到空态，启动审计 libraryConnections=0（本地 app 日志15:24:38）。最终构建后的原生窗口重启遇到启动进程无可交互窗口，未完成最终鼠标拖动/音频设备实测；没有宣称 DSD/多屏/多DPI/媒体键全通过。

同步：dist/web-ui-20261028，DLL SHA256 与 publish 相同：3D26C60AC66DD7917499C199B99D80C69654559B99F859FB999AC6F50AFB6ACA。运行入口仍是根目录“打开当前播放器.lnk”。旧版备份 dist/web-ui-backup-before-quick-20261029-153222。测试启动的无窗口进程已停止，用户重开即可加载新版本。
