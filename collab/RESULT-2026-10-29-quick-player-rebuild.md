# 零散播放器重构交付（2026-10-29）

默认页面替换正式零散窗口入口。冷启动外部文件/--quick 在 LibraryDb.EnsureMigrated、MainWindow 构造前分流。独立临时队列、批量转交去重、CUE 分轨、封面/歌词、输出设备、媒体控制、暂停协调均已接线。普通快捷方式在 quick-only 进程存在时激活已有窗口；完整 HTML/CSS/JS 页面包仍按原方案延后。

播放键通过两侧等宽布局保持窗口中心，包含前后曲、播放暂停、打开替换、追加、顺序、停止、音量、输出设备、临时队列、导出/清空、置顶等。两处频谱均由真实 FFT 数据绘制竖条，失采样500ms、暂停或隐藏后消失，不模拟数据。进度条显式绘制无边框轨道。

验证：Release publish 成功（outputs/quick-player-release-build.log）；QuickPlayerRegression 全通过（outputs/quick-core-regression3.log），覆盖 CUE、真实 PCM 管线、暂停恢复、停止重播、解码期间停止/退出不复活和曲库连接零增长；WebShellQuickPlayerSmoke 覆盖1000/620/460宽度居中、队列/设备命令、竖条及无采样隐藏和详情订阅关闭；已有 WebShellNowPlayingSmoke 通过。早期真实窗口已看到空态，启动审计 libraryConnections=0（本地 app 日志15:24:38）。最终构建后的原生窗口重启遇到启动进程无可交互窗口，未完成最终鼠标拖动/音频设备实测；没有宣称 DSD/多屏/多DPI/媒体键全通过。

同步：dist/web-ui-20261028，DLL SHA256 与 publish 相同：3D26C60AC66DD7917499C199B99D80C69654559B99F859FB999AC6F50AFB6ACA。运行入口仍是根目录“打开当前播放器.lnk”。旧版备份 dist/web-ui-backup-before-quick-20261029-153222。测试启动的无窗口进程已停止，用户重开即可加载新版本。

## 后续修复与工具按钮

拖动：网页上报排除按钮的物理标题栏矩形，宿主通过 InputNonClientPointerSource 注册 Caption，随 ResizeObserver/窗口缩放更新。频谱：改用 TryGetDetailedSpectrum 填满96频段，旧 TryGetSpectrum 只填固定段数导致尾部人为补零；12kHz 合成采样验证右侧四分之一区域非零。进度：通过 --fill 动态绘制已播放和未播放颜色，拖动时立即更新。

新增主界面按钮：属性、十段均衡器（平直/流行/摇滚/爵士/古典/低音增强、自定义）、速度（0.5—2倍、保持音高）、在 Explorer 选择当前源文件。未增加编辑、投放和视频功能。EQ/速度复用应用设置与实时 DSP，DSD 直出禁用；不是另行实现音频算法。

构建 outputs/quick-tools-build.log 成功；核心高频回归 outputs/quick-spectrum-regression.log 通过；WebShellQuickPlayerSmoke 扩展进度填色、10段控件、预设/速度/定位消息断言通过。已同步固定 dist 运行目录，保留 .NET 自包含文件。备份 dist/web-ui-backup-before-tools-20261029-160252。原生窗口已创建，桌面被正在运行的游戏遮挡，未打断用户游戏做拖动测试；不宣称实际拖拽实测通过。
