# 启动时旧主窗口黑帧

用户确认：直接打开 EXE，网页前先闪出一个无文字黑窗。

代码证据：App.OnLaunched 对 legacy MainWindow 先 Activate，再 AppWindow.Hide；Loaded 中另开 WebShellWindow。旧窗口在隐藏前存在被合成显示的机会。

实现：在旧窗口首次 Activate 前设置 DWMWA_CLOAK，使其保持不可见，继续触发既有 Loaded、播放及网页初始化。网页窗口独立显示。此路径不再展示旧 UI，因此不解除旧 HWND 的 cloak。

构建：Release build 通过，172 个既有警告，0 错误。直接复制框架依赖构建 DLL 到现有自包含目录时，应用在 Application.Start 前 fail-fast，未进入修改代码；原 DLL 已恢复，改用独立目录自包含 publish 验证。最终验证结果见后续记录。

回退：独立提交可 revert；当前交付目录更新前保留二进制备份。

最终验证：自包含 publish 成功；独立发布版启动存活并正常退出。EnumWindows + DwmGetWindowAttribute 检查：旧 MainWindow Visible=false、Cloaked=1；Celeste 网页窗口 Visible=true、Cloaked=0；托盘菜单与通知窗口隐藏。当前目录按文件 SHA256 比较后同步 133 个不同或新增文件，旧文件与新增标记备份 outputs/startup-cloak-delivery-backup-*/manifest.json。主 DLL SHA256 与独立发布目录一致。此验证确认窗口状态，首次启动瞬间是否完全无闪帧仍可由用户直接观察。
