# W13 移除正式项目WinUI依赖

状态：开始清理。当前阻塞面不是主网页或迷你网页，而是隐藏MainWindow业务适配器与原生服务对其依赖。W7/10已迁窗口与DWM；本次修复迷你迁为原生宿主，为W13移掉一处活动WinUI视图。

必须分步：1）任务栏图标渲染改无XAML，迁出原生HWND辅助视图与调度；2）将WebShellWindow的MainWindow owner替换业务接口/服务实现，逐域保持设置、标签、曲库、播放功能；3）由Core正式PlaybackSession接管队列/DSP/输出/SMTC/历史，不依赖控件；4）生产入口改原生消息循环，删除隐藏MainWindow构造；5）项目去UseWinUI/AppSDK/XAML编译，完整自包含干净机验收。

不得直接删除包引用制造缺功能版本，不把现有受控PlaybackSession当作正式全功能会话。用户要求验证不抢窗口、不播放声音；实际声卡测试需避免干扰其现有工作。

## 第一小步已交付

迷你宿主已迁原生（4200839）；任务栏按钮图标已改NativeTaskbarIconFactory，无XAML Canvas/FontIcon/RenderTargetBitmap活动依赖，600图标创建销毁回归通过。实际生产进程两轮后台回归（含reload）通过，原生主窗与按钮同HWND、曲库4095首、封面与设置正常。

本机一次背景启动停在旧MainWindow首激活阶段，另一次Node出现V8分配失败；未据此当作产品通过。清理本轮测试进程后，按仓库既有单核心进程规避方式启动，最终后台回归实跑通过。没有给日常程序加CPU亲和性限制。

下一步：WebShellWindow对MainWindow的业务接口拆分；正式PlaybackSession及应用生命周期接管后，才能删UseWinUI。整个W13尚未完成。
