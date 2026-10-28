# W1 第二小步：播放状态读取边界

起点：main 的 2730fe1 为 WorkBuddy 最新托盘修复（独立环境）。本项不改变托盘环境、点击处理或其网页代码，保留已有修复。

实现：新增不依赖 WinUI 的不可变 PlaybackSnapshot 与 IPlaybackStateSource。原 WebPlaybackState 的既有会话/控件读取移到 MainWindow.PlaybackState.cs 过渡适配器，限定在宿主 UI 队列执行。主网页与任务栏播放条通过同一个状态来源接口 Capture，返回强类型快照，替换 anonymous object 边界。无新缓存、无新计时器、无音频算法修改；旧字段、类型、默认文案和取值逻辑保持。

验证：独立 net10.0 PlaybackStateRegression 不加载 WinUI，覆盖原 JSON 字段集合/关键类型、双消费者读取同一来源、切歌后旧快照不变、序列化往返、空状态。PlaybackCommandRegression 同样通过。完整自包含 publish 成功。未操作用户窗口、未修改其设置。

范围：命令入口和状态模型已经形成平台边界；当前数据来源仍是 MainWindow 的旧会话及少量控件回退读取，尚未抽出播放服务所有权。W1 的窗口协调和全量状态解耦仍未完成，W5 新宿主 PoC 未开始。下一步应抽窗口恢复/显示的协调边界，再进入薄宿主验证。

交付：outputs/playback-state-publish 完整产物；更新前备份当前固定目录，再同步 dist/web-ui-20261028。日常仍双击根目录“打开当前播放器”。真实交互保留用户复验，不将模型/构建测试等同于任务栏全部实机验收。
