# 托盘菜单精简样式

用户已确认 outputs/tray-style-preview/preview.png 的设计，授权实施。

实现：CELESTE 标题、逐项 SVG 细线图标、34px 基础行高、柔和悬停、分组留白，退出单独分组；勾选态移到右侧。颜色与字号继续使用宿主的外观变量，原生 DWM 负责外层圆角，网页不重复描边/裁角。播放图标根据当前播放/暂停文案变化。命令 actionId、pointerdown 防重和宿主消息协议不变。

验证：WebShellTraySmoke.cjs 通过；Edge headless 真实 tray-menu.html 的浅/深两套主题、100%/150% 字号均通过图标数量、标签溢出和宽度检查；actual-light.png / actual-dark.png 已人工查看。此为网页渲染验证，不代替 Windows 多屏/多 DPI 完整验收。

交付只同步 tray-menu.js、tray-menu.css，更新前逐文件备份。当前实例已加载的网页不强制刷新；用户下次重启播放器生效。未调用 computer-use 或操作用户窗口。
