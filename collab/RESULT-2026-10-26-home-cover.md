# RESULT-2026-10-26-home-cover

- 用户目标：修复 WorkBuddy 新主页的封面只显示一部分，然后升级 .NET 10。
- 根因：当前曲目封面嵌套普通 span，音符与 img 参与正常布局；排行榜封面同样分占 grid 行，overflow:hidden 裁掉图片。
- 修复：当前曲目直接放入占位图标与图片；两类封面采用相对容器 + 绝对定位图片，与专辑卡片的既有方案一致。
- 验证：Edge headless 实测修复前当前曲目图片向下偏 42px，排行榜偏 19px；修复后三种视口 1426/900/500px 的图片与容器尺寸完全一致、偏移均为 0。加载失败仍保留音符。
- 构建：.NET 9 Release/x64 自包含 build 成功，91 warnings / 0 errors。
- 证据（不入库）：outputs/codex-home-net10/check-before.cjs、check-after.cjs、after-*.png、build-net9.log。
