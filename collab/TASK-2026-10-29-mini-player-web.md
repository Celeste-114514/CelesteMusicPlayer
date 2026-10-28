# TASK-2026-10-29-mini-player-web

目标：按用户确认的概念图重构迷你播放器，在主程序底部播放栏最左侧增加入口。

实现：WebMiniPlayerWindow 复用 MiniPlayerBridge，不拥有音频引擎。400×188 紧凑视图，400×620 歌词/队列展开视图，按显示缩放换算客户区尺寸。Celeste 暖白默认样式、封面显著色缓慢漂移光晕、Apple 深色预设、音量、进度、队列切歌、同步歌词点击跳转、置顶、恢复主程序。预设和动效选择存网页本地设置，置顶复用原设置。旧 WinUI 迷你源码保留，入口切换到网页窗口。用户自定义样式导入尚未实现；本任务只交付两个内置样式。

验证：完整自包含 publish；outputs/verify-mini-player.cjs 使用 Edge/Playwright 模拟桥接验证紧凑/展开、队列命令、播放命令、预设切换及无 JS 异常。截图 outputs/mini-player-compact.png、mini-player-lyrics.png。自动化不代替真实音频播放和窗口拖动体验验收。

交付：固定目录 dist/web-ui-20261028，更新前备份 outputs/mini-player-backup-20261029。用户关闭旧进程后通过根目录“打开当前播放器”重开。
