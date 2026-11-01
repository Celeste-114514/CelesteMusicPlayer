# Windows 风格缩放黑条修订

触发条件：Windows Fluent 外观，拖动窗口边缘调整大小。

修改：主窗口边缘缩放从后台线程 4 ms SetWindowPos 轮询改为 Windows SC_SIZE 原生缩放循环。WM_NCCALCSIZE 在尺寸变化时返回 WVR_REDRAW，避免透明亚克力/WebView 复用旧客户区像素。保留 Windows 透明材质与用户外观偏好。

验证：自包含 Release 发布成功，diff 检查通过；运行 Windows 主题原生窗口无初始黑条。自动拖拽工具未能可靠改变窗口尺寸，因此不能据此确认原触发场景已完全解决，需用户按原操作复测。

已同步固定 dist/web-ui-20261028，桌面 CelesteMusicPlayer 最新测试版.lnk 继续指向该目录。主程序集 SHA256 与 r4 一致。
回退目录：dist/web-ui-20261028-before-r4-060258。交付中不包含本次测试生成的 WebView2 用户配置目录。

参考：https://learn.microsoft.com/windows/win32/winmsg/wm-nccalcsize
参考：https://learn.microsoft.com/windows/win32/menurc/wm-syscommand
