# 吸附、还原与标题栏修订（r8）

## 修改与依据

此前修订未通过用户实测：拖到屏幕边缘最大化后网页偏移；后续又出现标题栏灰色区域截断，以及还原后顶部/左侧约 8 像素残边。

实测日志曾捕获网页承载面板位于 (-685,-290)，而主窗口尺寸正常。第一阶段结束校正后，用户确认“松开鼠标后恢复正常”。本轮把对齐提前到位置/尺寸变化时，同时解决边框模型不一致：

- WinForms 的 FormBorderStyle 与 CreateParams 统一为无标准标题栏。此前仅修改 HWND 样式，托管 Form 仍按标准带框窗口布局。
- HWND 保留 WS_THICKFRAME、最小化/最大化能力，移除 WS_CAPTION；禁用原生非客户区绘制，网页独自绘制标题栏。
- 原生鼠标消息携带真实屏幕坐标，不再传入 (0,0)；保留对负坐标显示器的编码。
- 最大化客户区对齐显示器工作区，不包含屏幕外的隐藏尺寸边框。
- 网页面板、控制器与实际浏览器子 HWND 在移动、缩放和还原时对齐；结束时再做一次一致性校正。
- 已恢复前轮为排查而暂停的 Windows/Apple 原生透明材质，未更改用户外观偏好。

## 无干扰验证

新增 tools/NativeChromeRegression：启动测试子进程到单独创建、从不切换到的 Win32 桌面，不注入鼠标/键盘，不激活用户桌面的窗口，不加载音乐库或应用偏好。

13 个阶段全部通过：普通窗口，以及三轮最大化、还原、左上角缩放、注入已观察到的负向子窗口偏移。每阶段检查：托管边框模型、原生标题栏样式、客户区/网页面板/实际 Chrome 子窗口屏幕矩形、最大化工作区尺寸，并捕获测试页面，检查标题栏左/中/右像素完整。

局限：独立桌面测试不能替代用户当前桌面上的 DWM 动画与真实鼠标吸附验收。本轮未再操作用户桌面。

Release 自包含发布、详情页现有自动化检查、JavaScript 语法和 diff 检查通过。

## 入口与回退

桌面“CelesteMusicPlayer 最新测试版.lnk”和仓库“打开当前播放器.lnk”均指向 dist/bugfix-20261102-r8。入口脚本、说明与项目简报同步。
用户可忙完后从托盘退出旧实例，再用相同快捷方式启动。未强制结束正在运行的旧实例。
回退：dist/bugfix-20261102-r5-final 等历史构建保留。

参考：
https://learn.microsoft.com/windows/win32/inputdev/wm-nclbuttondown
https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmncrenderingpolicy
https://learn.microsoft.com/windows/win32/winmsg/wm-nccalcsize
