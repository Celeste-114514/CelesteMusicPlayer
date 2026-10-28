# 莱茵三维专辑阵列

用户要求：先把提供的 RhineLabUI 三维效果尝试接进播放器，书架另做。

落点：专辑页新增「莱茵阵列」，独立全屏场景，底部播放条保留。
复用用户本地 RhineLabUI 的 MIT 场景代码与 archive-cassette.glb；用本地专辑替换研究档案数据，接原有播放桥。
Rhine Music Database 是 Qt 参考，本轮未引入其运行时或在线服务。

源码：outputs/webview2-shell/rhine-albums.js、rhine-albums.css；工具与可重建的上游子集 tools/rhine-renderer/。
网页 index.html 仅增加两个加载标签；csproj 增加 GLB 和许可的随包规则。
模型内标注有 Celeste 专辑名；封面与曲目出现在右侧详情，内部仍保留原双环模型。
不是书架，不含上游开场/声音/拆解查看器。

验证：Release x64 build（172 条既有警告，0 错误）；WebShellRhineSmoke.cjs 覆盖实际 WebGL 渲染、专辑身份、抽取、曲目、播放调用、退出释放、重开、导航和窄窗。
运行版本按 PROJECT-BRIEF 指向 dist/web-ui-20261028，网页文件同步前单独备份，可恢复。

回退：独立 commit 可 revert；同时恢复 dist 下备份的 index.html，其余新文件不会被加载。
