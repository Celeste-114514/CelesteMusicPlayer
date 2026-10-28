# 托盘菜单完整显示，无滚动

用户截图：菜单只显示到收藏附近，设置、日志与退出被裁切，要求全部显示且没有滚动样式。

修正尺寸交接：不再等待异步 size 消息并在超时后沿用初始320px高度；宿主调用 CelesteTray.render，一次完成主题、条目渲染和尺寸返回，等待最终尺寸后再显示。迟到的 size 消息不再覆盖最终值。网页测量包含 scrollWidth/scrollHeight，主题应用本身不发送旧内容尺寸。父窗口先调整，再设置子 WebView Bounds；明确 UseRawPixels 与 ZoomFactor=1，维持 CSS 尺寸乘 DPI 后对应物理窗口。外观与命令逻辑不变。

依据：WebView2 BoundsMode 文档区分物理与逻辑像素：https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2boundsmode 。这里只应用一次 DPI 换算。

验证：WebShellTraySmoke.cjs 包含原有命令回归，以及从260x320初始视口渲染全菜单、返回高度大于320、按返回尺寸调整后最末退出项完整显示且页面无纵向溢出；100%与150%字号通过。没有调用 computer-use 或操作用户窗口。Windows 实际显示仍待用户复验。

交付：自包含发布到独立目录 outputs/tray-full-height-publish；验证后更新固定目录 dist/web-ui-20261028，并逐文件备份。用户仍用根目录“打开当前播放器”入口。
