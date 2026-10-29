# 桌面歌词收尾（2026-10-29）

范围：修复逐字叠字、窗口拖动、横竖排宿主尺寸、关闭位置记忆；工具栏改为 SVG 图标并随播放状态切换。前后行补描边以适应浅色桌面。保留真实透明合成宿主及锁定穿透/解锁热区。

根因与处理：原 current span::after 复制字形叠加在 inline 元素上，基线/换行存在错位风险；改为单字形渐变停止点显示进度。原拖动在后台线程调用同步 SetWindowPos，改为 UI 线程 SetCapture + WM_MOUSEMOVE，释放或 capture 丢失时结束。切换方向原只修改合成视觉，未修改 WebView2 Bounds；已同步。WM_DESTROY 缓存位置再通知主窗，避免已销毁窗口读回 (0,0)。

设置接线：纳入 WorkBuddy 已写的桌面歌词方向、字体文件、音乐响应、缩放字段、字体选择器、独立分类与导航。对并行修改文件仅提交/发布桌面歌词部分，迷你样式等 WIP 继续留在工作区。隔离发布源码位于 outputs/desktop-lyrics-release-source。

Echo 对照：已有主题/纯色/渐变、主次字号、字体文件、横竖排、音乐响应、透明与锁定。尚未照搬 Echo 的罗马音数据链、完整快捷样式编辑工具栏、样式包导入导出（原任务明确后置）。不应描述为所有 Echo 能力逐项齐平。

验证：tools/WebShellDesktopLyricsSmoke.cjs 覆盖单字形、SVG、播放图标、拖动消息、翻译、锁定解锁、竖排；使用 NODE_PATH 指向可用 playwright，系统 Edge 无头测试。

资料过时：TASK-2026-10-29-desktop-lyrics-echo.md 中仍说 LayeredWindow 和竖排/字体/响应未接入；当前已是 Win32 + WebView2 合成宿主。PROJECT-BRIEF 的 .NET 10 与项目一致，入口 AGENTS 提到 .NET 9 已过时。

实机输入追加根因：用户反馈按钮和拖动均无响应，日志明确在 LeftButtonDown 抛 ArgumentException。SDK 本地 XML（SendMouseInput）规定普通按钮 mouseData 必须为 0，旧代码误传 1/2。已修正普通按键参数；滚轮同时改传实际有符号增量并转客户区坐标。网页回归不覆盖这条原生链路，不能据此宣称实机输入已验证。

最终验证：隔离 publish 成功（outputs/desktop-lyrics-input-final.log，退出码 0），DLL SHA256 547f57e8fc919e8bc88e3346e8a6a3a90493640401dbf39ae0a3b9c1b05d535e。已同步 dist/web-ui-20261028，并重启正式入口。用户明确反馈「经过验证已可行」，确认点击与拖动修复有效。自动 UI 工具未能可靠命中透明工具窗，实机结论以用户反馈为依据。

回退文件：outputs/desktop-lyrics-before-finish-20261029，覆盖前的旧文件均保留；退出播放器后将备份文件按相对路径复制回 dist/web-ui-20261028 即可。
