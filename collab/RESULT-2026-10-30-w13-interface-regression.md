# W13 接口边界回归 — 2026-10-30

上一轮审计交接留了一项没确认：`IWebPlayerServices` / `LegacyWebPlayerServices` 那批改动虽然编译 0 错误，
但完整后台回归没跑过。本轮补上了。

## 结论

PASS。真实进程（自包含 publish 产物）+ CDP 遥控网页，冷启动与页面重载各跑一轮，
12 条网页命令全部返回真实数据，无 JavaScript 异常，点关闭后进程正常退出（exit 0，业务清理走完）。

## 依据

- 提交 `f81ffd0 w13: route web shell host through IWebPlayerServices`（被测代码）
- 全量 Rebuild：0 错误、554 警告
- 自包含 publish：`bin/Release/net10.0-windows10.0.19041.0/win-x64/publish`
- 回归脚本：`tools/W13InterfaceRegression.cjs`
  （`NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13InterfaceRegression.cjs`）
- 机器可读结果：`_w13_interface_regression.json`

| 命令（接口成员） | 冷启动 | 重载 |
|---|---|---|
| library（LibraryTracks） | 4041 首 | 4041 首 |
| home | ok，topAlbums 4 / topArtists 5 / recentAlbumIds 12 | ok |
| folders（roots） | ok，rootCount/total/nodes | ok |
| sources（list） | ok，providers 7 | ok |
| appSettings（get） | ok，defs 74 / groups 12 | ok |
| dsp（get） | ok，bands 10 | ok |
| audioChain | ok，nodes/levels/fields/bitPerfect | ok |
| audioReadout | ok，mode/deviceFormat/bitPerfect | ok |
| tags（list） | ok，rows/total/offset | ok |
| queue（get） | ok，tracks 1 / summary / order | ok |
| nowPlaying | ok，audio/currentIndex/lyricState | ok |
| diagnostics（status） | ok，appVersion/crashes/unclean/marker | ok |
| more（get） | ok，playlists 2 | ok |

## 没验证的

- 未播放音乐，未做声卡实声验收（沿用之前口径，避免打扰本机）。
- 未验标签写入、队列拖动、DSP 落盘（这些有既有独立回归脚本，本轮只确认接口通路未断）。
- 未做干净机验收。

## 口径

本轮只是「接口改造没有破坏运行时行为」，不代表业务已经脱离 MainWindow。
`LegacyWebPlayerServices` 仍然逐条转发给 `MainWindow`，这一点在下一轮继续拆。
