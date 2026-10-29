# W7 正式网页宿主

正式 WebShellWindow 不再派生 WinUI Window，改为 WinForms 提供 HWND、窗口与兜底恢复面板。WebView2 直接用 WinRT 窗口控制器挂到原生子 HWND，保留全部现有数据/命令桥接；旧业务适配层仍是 MainWindow，WinUI依赖彻底删除属于W13。

修复集成测试实际发现的问题：控制器的 CoreWebView2 包装对象必须被字段持有，否则初始ready之后垃圾回收导致消息订阅失效，表现为曲库能收到、主页/RPC无回复。原生窗口和选择器共享真实 HWND；播放列表输入与回收站删除确认改用有主窗口归属的原生对话框。设置移除 scope=native 的旧界面字段；删除三种极客预设及读数区。

验证：outputs/w7-background-build3.log 自包含 publish 成功；tools/W7NativeHostSmoke.cjs 通过真实 app、独立 WebView profile 与 localhost CDP 后台运行，两轮（包含reload）均验证4095首、74项设置且旧界面项为0、封面图片有效、原生窗口状态可读、极客读数区移除，最后网页关闭命令正常退出进程。WebShellNowPlayingSmoke 通过。本轮没有播放实际声音，避免影响用户现有工作；鼠标拖拽/多DPI未做新的人工测试，不冒充已实测。调试端口仅 --w7-background-test 开启，日常启动不开启。

用户已验收结束：零散、歌词、CUE、当前任务栏条、其他现有功能；自动更新待后续真实版本；DSD仅余Native DSD。下一步W9新宿主激活/托盘，随后W10绑定与缩略图、W12图标、W13去WinUI、W14文档。完整网页页面包仍在后排。
