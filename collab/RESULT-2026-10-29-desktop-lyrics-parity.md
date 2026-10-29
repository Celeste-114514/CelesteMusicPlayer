# 桌面歌词：快捷样式、样式包与罗马音

已实现：
- 顶部新增 SVG「快捷样式」按钮，面板内直接调主/辅助字号、缩放、横竖排，独立开关罗马音和翻译，重置窗口位置。
- 主题、纯色、渐变预设及三个自定义取色器，实时写入现有设置白名单并刷新当前歌词窗。
- 保存当前样式（按时间命名）、选择并应用已保存样式、系统文件对话框导入/导出 JSON。用户样式目录为 %LOCALAPPDATA%/CelesteMusicPlayer/desktop-lyric-styles。
- 样式包格式 celeste-desktop-lyrics / version 1。仅含样式白名单；不携带字体文件路径、窗口位置、播放/账号信息。整包验证后才原子更新设置；限制 32KB、100 个用户样式，拒绝未知字段、版本和非法编号。导入后先入列表，点击应用才更换样式。
- LyricLine.Romanization 从歌曲旁同名 .romaji.lrc / .romanized.lrc 读取，按相同时间戳关联；与翻译分开，支持开关和竖排。模糊查找主歌词时排除这两类辅助文件。没有数据不显示，也不声称自动生成日语读音。

验证：
- 隔离自包含 publish 成功，outputs/desktop-lyrics-parity-build.log。
- tools/WebShellDesktopLyricsSmoke.cjs（Playwright + 系统 Edge）PASS：旧有逐字/图标/拖动消息/锁定/竖排回归，加上快捷调节、罗马音、样式选择/保存与错误消息；截图 outputs/desktop-lyrics-controls.png。
- tools/DesktopLyricsRegression 直接加载最终发布 DLL，使用独立临时 app-settings：真实设置重读、样式导出/导入/选择、非法字段整包拒绝不部分写入、版本/尺寸/路径拒绝、真实 LRC + 罗马音加载 PASS。日志 outputs/desktop-lyrics-backend-final.log。
- 已同步 dist/web-ui-20261028；DLL SHA256 b968204b02d36d1c732506d21382f77bf9f93b83f562ce75ac41213d956346ea。已启动正式程序；本轮新增文件选择对话框未用自动化代替用户实际选择文件，后端文件读写已由测试验证。

回退：outputs/desktop-lyrics-before-parity-20261029 保留被覆盖的旧文件及 changed-files.json。退出播放器，按相对路径复制回正式目录；新增 desktop-lyrics-controls.js 可留存，旧 HTML 不引用它。

源码：沿用 outputs/desktop-lyrics-release-source 隔离目录，WebSettingsSchema 只纳入本轮罗马音字段；未混入工作区迷你样式改动。旧任务关于样式包后置已被用户本次明确指令取代。此轮对齐的是上一轮明确列出的三项，不宣称移植 Echo 的全部内部实现。
