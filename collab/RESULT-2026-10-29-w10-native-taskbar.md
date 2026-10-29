# W10 原生宿主任务栏交付

四按钮复用现有TaskbarThumbnailButtons和命令，改绑W7原生主窗口HWND，释放旧绑定。主窗口TaskbarButtonCreated通知重建绑定（Explorer重建场景的接线）；原有延迟注册/更新pump仍保留。新增NativeTaskbarPreview：DWM iconic thumbnail与live preview请求，封面/标题/艺术家/播放状态、无封面占位、尺寸约束、异步封面版本保护、切曲/状态变化失效，所有临时HBITMAP及时释放。仍依赖旧业务适配器和旧图标渲染服务，彻底去WinUI属于W13。

验证：w10-final-build.log自包含publish成功。NativeTaskbarPreviewRegression链接生产绘制代码，在隐藏HWND处理100轮DWM请求、封面/占位、1px极小尺寸，GDI15→17（首次字体缓存），未持续增长；outputs/w10-thumbnail.png人工检查通过。W10TaskbarSmoke真实生产进程验证buttons HWND=main HWND=1052196，两轮包含reload均通过；4095首/设置/封面/RPC保持正常，最后关闭进程。仅验证后台消息与绘制，不声称已重启用户Explorer或人工悬停任务栏实测；测试未激活窗口或播放音频。

交付固定dist/web-ui-20261028，保留更新前完整备份。下一项W12图标定稿；W13移除WinUI依赖、W14文档后续。任务栏常驻播放条保持用户已验收形态。
