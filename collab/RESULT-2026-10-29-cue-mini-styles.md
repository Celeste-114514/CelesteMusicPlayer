# CUE 专辑与迷你样式包交付

CUE：继承专辑/专辑歌手/原轨号；GBK/Big5/UTF-8 按实际引用文件匹配。扫描与直接打开均按独立轨道边界处理，格式栏CUE/PCM。真实《解决》夹具：10轨、单一专辑“解決[FX 500762]”/崔健，所有文件可解析；分轨合计2929.667秒与WAV一致。仅验证元数据及边界，不代表人工试听。

迷你：承接WorkBuddy样式包半成品并完成制作、命名、预览、另存并使用、选择、导入/导出和删除用户样式。支持七项配色、字号、明暗；新增封面左/右/纯文字、封面尺寸/圆角、文字对齐。不执行HTML和脚本，不等于任意网页布局编辑器。旧格式可用默认布局继续导入。

测试：完整publish退出0（outputs/cue-mini-build.log）；tools/CueMiniRegression调用最终DLL验证真实CUE/WAV和样式读写、布局保留、非法颜色/版本/范围拒绝及删除PASS（outputs/cue-mini-regression.log）；tools/WebShellMiniStylesSmoke.cjs验证编辑器及实际渲染PASS；outputs/verify-mini-player.cjs既有交互回归PASS。未代替用户操作文件选择对话框。

已同步dist/web-ui-20261028并启动，DLL SHA256 956d31bd702e40cf314145fdb6bd6f03d2abe9a7fb6fc946a9d08e24f84238f9；备份outputs/cue-mini-before-20261029，退出后按相对路径复制回退。

入口：设置→迷你播放器。CUE沿用扫描时自动应用CUE开关；旧曲库需重新扫描或直接打开CUE。本轮明确取代旧清单迷你样式包暂缓的状态。
