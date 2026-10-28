# 半圆导航上方空白拖窗修复

用户反馈：顶部半圆展开和收起时均挡住拖窗。

根因：window-drag.js 把整个 .corner-orbit 容器从原生标题栏矩形中扣除；隐藏子按钮也参与扣除，导航展开/收起未触发重新计算。

修改：只保留交互控件排除，跳过 visibility:hidden/display:none 元素；监听导航 class/style 和 transitionend，更新顶部标题栏命中区域。不扩大到标题栏外的正文区域。

验证：WebShellCaptionSmoke.cjs 使用 Edge headless 检查展开空白、展开按钮、收起空白、收起按钮及重新展开；通过。JS 语法检查通过。Release 构建首次遭遇既有 XamlCompiler 访问冲突，重试通过（172 警告，0 错误）。

交付：网页源码已同步 dist/web-ui-20261028/WebShell/window-drag.js，SHA256 一致；旧文件保存 outputs/caption-drag-backup-*/window-drag.js，重启播放器生效。实际 Windows 拖窗手感仍待用户确认。

