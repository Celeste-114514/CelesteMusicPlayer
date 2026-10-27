# Windows 原生拖动最终交付（2026-10-28）

## 修复

网页不再发送 pointerdown/drag-start/drag-end 逐次模拟拖动；新增 window-drag.js 只在布局/主题/尺寸/全屏变化时上报 Caption 矩形，排除交互控件和轮盘。

宿主启用标题栏并将其高度 Collapsed，通过 InputNonClientPointerSource.SetRegionRects(Caption) 让 Windows 接管命中测试和移动循环。旧的后台线程跟踪不会由正式网页触发。保留原 HTML 窗口按钮，没有第二套可见系统标题栏。

## 证据

完整自包含发布 outputs/native-caption-publish 成功；node --check window-drag.js/shell.js 通过。tools/WebShellCaptionRegionsRegression.cjs 检查控件排除、2倍缩放、全屏清空、详情页切换，通过。

实机 Computer Use 测试：
- 主界面拖动前窗口原点 (163,156)，鼠标拖动 (+160,+80)，窗口变为 (323,236)，位移一致。
- 点击底栏封面可进入详情，未被拖动区域误拦截。
- 详情页原点 (440,318)，鼠标拖动 (-120,-20)，窗口变为 (320,298)，位移一致。
- 暂停状态未被测试改变；关闭测试版使用 Alt+F4 正常退出。

正式目录 dist/web-ui-20261028 已整体替换为这套完整发布产物，DLL/PRI/window-drag.js SHA256 与 stage 一致。上一版整体备份 dist/web-ui-20261028-before-native-caption-20261028-031423。正式路径启动 PID 29744，持续响应检查正常。

未测量显示器实际 FPS，不宣称固定帧数；已移除正式网页拖动对定时器采样和跨进程逐次移动的依赖，并验证实际原生位移与交互。

本次保留接手时已有的原生 Caption 改动，完成集成、回归、实机验证与完整发布；不包含 WorkBuddy 的记忆/截图文件。
