# W1 第一小步：共用播放命令入口

用户要求修复设置入口/托盘问题后继续原计划。前一项修复独立提交 07fcf2b；本项单独可回退。

新增纯 C# PlaybackCommandRouter，只依赖播放状态查询和上一首/切换播放/下一首三个委托，不引用 WinUI、窗口或控件。定义 previous/next/togglePlay/play/pause 语义，play/pause 为幂等命令，未知命令不执行。MainWindow.PlaybackCommands 是过渡适配器，在 UI 队列中连接现有播放会话。

主网页 WebShellWindow 的四个播放动作和 TrayCommandAdapter 的三个播放动作统一走适配器；任务栏条复用 TrayCommandAdapter，因此同样进入统一入口。其它托盘动作、窗口恢复、DSP/DSD算法和辅助播放器内部逻辑不变。

验证：PlaybackCommandRegression 不加载 WinUI，验证重复 play/pause 不反转状态、每次 next/previous 只执行一次、未知命令不执行。原有托盘/任务栏网页回归通过；完整 publish 后同步固定入口。未操作用户窗口。

范围：这是 W1 命令抽取的开始，不代表 W1 状态/窗口协调已全部抽出。实际播放动作仍由现有 MainWindow 会话执行；下一阶段应抽出状态快照和窗口协调，再验证非 WinUI 薄宿主。未更换产品界面。
