# W1 第三小步：主窗口显示/恢复协调边界

新增纯 C# MainWindowCoordinator + IMainWindowSurface，统一“没有则创建、最小化先还原、显示并激活、保持旧宿主隐藏、再导航”的规则。处理重入请求，关闭期间不再创建或恢复窗口，创建失败后可重试。

MainWindow.WindowCoordination.cs 是 WinUI 过渡适配器，承接原 MainWindow.WebShell.cs 的建窗、RestoreMainWindow、ShowWebPage 和 owner关闭逻辑。启动、托盘恢复、文件激活及设置跳转原调用点保持，都会进入同一协调器。网页未ready时的 NavigateTo 排队仍归现有 WebShellWindow 管理；未改文件播放、音频引擎、关闭进程策略或 WorkBuddy 独立托盘环境。

验证：WindowCoordinatorRegression 在不加载 WinUI 的 net10.0 工程验证首次创建、存在窗口复用、最小化还原顺序、显示后导航、关闭重入与重复关闭、创建重入的导航请求、创建失败后重试及未创建窗口时关闭。PlaybackStateRegression、PlaybackCommandRegression 同样通过。自包含完整 publish 成功。未操作用户窗口，以上不代替真实Windows焦点/恢复验收。

交付：outputs/window-coordinator-publish 完整产物同步固定目录 dist/web-ui-20261028，更新前逐文件备份。入口仍是根目录“打开当前播放器”。

当前 W1 已形成命令、状态模型、主窗口协调三条最小边界；实际会话/窗口所有权和控件读取仍在 WinUI 适配层，不能称为去 WinUI 完成。下一步可开始 W5 独立薄宿主 PoC，先验证窗口/WebView2/任务栏/桥接，不替换正式入口。W6 服务逐域迁移继续待做。
