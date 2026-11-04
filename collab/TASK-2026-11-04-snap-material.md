# 贴边拖回色带：透明合成路径隔离

用户确认实际故障为贴边拖回后不规则明暗色带。运行进程确认使用 native-snap 最新版本，并非旧实例导致。

发现：WebShellWindow.ApplyMaterial 仍在启用 DWM acrylic (3)、透明 WebView 底色以及 materialActive=true。r8 任务单确认曾恢复该路径。旧版隔离测试验证的是几何与标题栏，不包含用户桌面 DWM 吸附动画，不能证明无残影。

本次替代方案：主窗 DWM backdrop=1（禁用系统背板）；Form、Panel、WebView 底层均不透明；materialActive=false 让网页退出透明根背景。内部磨砂保留，桌面透光取消。根因仍是待实机验证的透明合成假设，不宣称已完全修复。

Release 自包含发布成功；既有 NativeChromeRegression 调整到不透明配置后所有阶段通过，包括原生标题栏命中、最大化、还原、缩放、子窗口偏移恢复与标题栏绘制。JS 语法及 diff 检查通过。

桌面最新测试版与仓库入口同步到 dist/bugfix-20261104-opaque-snap。未终止用户正在运行的实例；需托盘退出后重启。回退目录 dist/bugfix-20261104-native-snap 保留。下一步验收：左右边缘贴边拖回、上边缘最大化后拖回，重复数次观察色带。
