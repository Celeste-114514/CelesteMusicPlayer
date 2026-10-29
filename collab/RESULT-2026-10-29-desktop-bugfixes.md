# 迷你加载/托盘图标/外观应用修复

迷你窗口日志明确NullReference于EnsureCoreWebView2Async后Core为null。改用W7生产验证的原生HWND+NativeWebViewPanel，保留布局/动画/状态与音频桥接；已经以真实生产进程在隐藏模式创建迷你页面，验证接收曲目标题，不是浏览器mock。

外观经用户澄清继续手动保存，在设置左下加固定“应用”“撤销”，清晰显示待应用状态；应用commit草稿落盘，撤销取消全部未应用外观。两次真实进程启动验证应用后重启保留，撤销不覆盖已应用状态，测试后恢复原外观。普通设置原先即自动保存，不改变其语义。

托盘原LoadImageW读取小尺寸PNG压缩ICO兼容性不足。固定生成器将16—64px改为原生32bit DIB+AND mask，128/256保留PNG；图形不变。用真实user32.LoadImageW读取正式16px得到有效HICON、中心RGB38/59/96，与定稿深蓝一致。

验证desktop-bugs-build.log自包含publish成功；DesktopBugsSmoke.cjs save/restore两阶段通过。无可见窗口、无播放声音、不清图标缓存、不修改默认应用。原始外观在测试后恢复。下一步W13仍须移走隐藏MainWindow业务适配器，不得以迷你宿主迁出声称整个WinUI引用已删除。
