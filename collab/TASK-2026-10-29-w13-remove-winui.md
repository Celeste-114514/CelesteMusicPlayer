# W13 移除正式项目WinUI依赖

状态：开始清理。当前阻塞面不是主网页或迷你网页，而是隐藏MainWindow业务适配器与原生服务对其依赖。W7/10已迁窗口与DWM；本次修复迷你迁为原生宿主，为W13移掉一处活动WinUI视图。

必须分步：1）任务栏图标渲染改无XAML，迁出原生HWND辅助视图与调度；2）将WebShellWindow的MainWindow owner替换业务接口/服务实现，逐域保持设置、标签、曲库、播放功能；3）由Core正式PlaybackSession接管队列/DSP/输出/SMTC/历史，不依赖控件；4）生产入口改原生消息循环，删除隐藏MainWindow构造；5）项目去UseWinUI/AppSDK/XAML编译，完整自包含干净机验收。

不得直接删除包引用制造缺功能版本，不把现有受控PlaybackSession当作正式全功能会话。用户要求验证不抢窗口、不播放声音；实际声卡测试需避免干扰其现有工作。
