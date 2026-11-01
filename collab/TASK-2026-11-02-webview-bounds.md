# 左侧白块与顶部错位修订

用户截图显示：左侧大片浅色未绘制区域、网页整体偏移、标题栏分段残留。前轮修订未能解决，不能视为已验收。

本轮修改：
- WebView 控制器补充 NotifyParentWindowPositionChanged：主窗移动、尺寸变化、DPI 变化及承载面板移动时同步；布局变化也重新核对控制器 Bounds。
- 主窗口取消 DWM 桌面亚克力穿透，采用与主题匹配的不透明原生和 WebView 底色。Windows/Apple 的配色、几何和网页内部磨砂层仍保留。此项是稳定性取舍，桌面背景不再透入主窗口。
- 外观材质说明同步更新，避免仍声称桌面亚克力生效。

验证：自包含 Release 发布成功；diff 检查通过；现有详情页自动化检查通过。新构建未完成用户原触发场景的原生人工验收，不能宣称完全修复。旧实例仍在运行，未强制退出或覆盖它。

当前入口：桌面 CelesteMusicPlayer 最新测试版.lnk，以及仓库打开当前播放器.lnk，均指向 dist/bugfix-20261102-r5-final。工具脚本、入口说明和项目简报已同步。需要从托盘退出旧实例后再启动。
回退：上一轮运行目录 dist/web-ui-20261028 保留。

位置同步 API 参考：https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2controller.notifyparentwindowpositionchanged
