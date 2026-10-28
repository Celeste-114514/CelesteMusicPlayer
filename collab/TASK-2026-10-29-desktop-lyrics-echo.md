# TASK-2026-10-29-desktop-lyrics-echo

用户：按 ECHO 桌面歌词思路接入；用户制作/导入/选择样式包待 W6 完成再做。

本步：复用现有 LayeredWindow、歌词同步和逐字时间，加入 Theme/Solid/Gradient 三种颜色模式、独立前后歌词字号。Theme 根据网页主程序 WebAppearance 解析颜色并在修订后更新，旧极客主界面分支仍保留。设置落点：歌词与封面 → 桌面歌词。AppSettings 新字段及 Clone 显式拷贝同步。

参考：本地 ECHO-main/src/shared/types/desktopLyrics.ts、src/renderer/styles/desktop-lyrics.css 及 DesktopLyricsApp.tsx 的主题/自定义渐变和主次字号思路；渲染由现有 System.Drawing 透明窗口适配，未整块搬入 Electron/React。尚未接入 ECHO 竖排、自定义字体文件、音乐响应背景等能力，不能称为全套 ECHO 已完成。

此前迷你样式包 WIP 保留工作区，不随本步提交或交付。完整源构建已通过；独立提交后在隔离目录发布，避免混入 WIP。
