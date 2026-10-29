# 托盘默认EXE图标根因修复

确切根因：WindowsTrayIcon.LoadImageW缺少CharSet.Unicode，默认ANSI字符串传入W接口，路径被错误解释。之前用另写的正确PInvoke验证ICO本身，没覆盖生产声明，不能证明实际托盘加载正常。本次修正CharSet.Unicode+ExactSpelling；加载成功/失败均有专用日志。

TrayIconRegression直接链接生产WindowsTrayIcon.cs，反射调用其实际LoadTrayIcon，无系统托盘注册或可见窗口；得到16px HICON中心RGB38/59/96定稿深蓝，不是默认图标。生产后台进程日志00:52:25同样确认[tray.icon] loaded application ICO 16px。没有清理图标缓存。

完整自包含publish成功，修复随本轮W13外观服务产物同步固定dist，回退保留完整备份。
