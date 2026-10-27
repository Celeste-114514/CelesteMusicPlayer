# 启动恢复与拖动调度修复验收（2026-10-28）

## 完成

- 完整自包含 publish：outputs/drag-worker-final，使用项目固定核心脚本，日志 outputs/drag-worker-final.log；发布完成，未使用单 DLL 替换交付。
- 原目录整体保留为 dist/web-ui-20261028-before-startup-repair-20261028-025354。
- 正式目录 dist/web-ui-20261028 已从完整发布目录复制，包含配套 DLL/PRI/XBF/依赖/网页，DLL SHA256 与发布结果一致。
- 从正式路径启动 PID 3292；持续运行超过 12 秒，Responding=True，MainWindowTitle=Celeste。日志 02:54:06 显示启动加载完成，02:54:14 显示正常任务栏初始化收尾。此前独立完整发布版亦实际显示网页主界面。

## 拖动修改

- 不再用 DispatcherQueueTimer 跟踪鼠标；独立后台线程读取系统坐标，用 GetWindowRect 和 GetCursorPos 同一物理坐标系计算位移。
- 仅拖动期间使用 timeBeginPeriod(1)，finally 配对恢复；无位置变化不调用 SetWindowPos，物理松键或代次变化结束线程，关闭窗不等待该线程，避免跨线程移动死锁。
- 去掉原生移动循环/探针/轮询之间的混合接管路径；保留网页按钮/输入的 no-drag。
- AppWindow 纯位置改变不再向 WebView2 连发 visibility/fullscreen 状态，降低每次移动的额外开销。

## 边界

此前零散 DLL/PRI 覆盖导致交付不可依赖；完整发布恢复启动验证通过。没有 dump 证据将先前所有 failfast 精确归因到某一个 DLL 或资源。用户按 Esc 停止了 Computer Use，后续不继续界面输入；新版拖动的主观跟手程度与具体帧率未实测，不宣称固定 FPS。

node --check shell.js 通过；现有 WebShellLoadingSmoke 因找不到 playwright-core 未执行成功。构建/发布、真实启动和响应检查为本次主要验证。

用户配置、数据库与音乐文件未清除。正式目录恢复可运行，运行入口 tools/OpenCeleste.cmd 不变。
