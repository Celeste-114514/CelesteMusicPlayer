# 播放栏、正在播放详情页与链路浮层修复

## 范围与原因

- 底栏歌曲区与新详情页共用 `.now-playing`，详情样式把底栏变成纵向卡片。详情页改为独立 `.now-playing-page`，恢复底栏原有横排。
- 背景层规则 `.shell>*:not(#bg-layer)` 强制所有直接子元素使用 `position:relative;z-index:1`，覆盖链路抽屉的 fixed 定位，导致主区被挤走。规则现只作用于正常布局中的标题栏、工作区和底栏。
- 详情封面原来请求默认 160px 缩略图。现在单独请求 768px，宿主新增对应尺寸，底栏仍用小图。
- 详情页改为覆盖整个网页外壳，左上只有一个向下收起箭头；艺术家/专辑信息从 13px 增至 18px。
- 补齐右侧高度约束与独立滚动、文字选择；第一句 0 秒也允许定位，选择文字时不误跳。修正详情播放按钮的播放/暂停图标同步。
- 选歌入口立即显示曲名与“正在准备播放”，等待宿主确认声音状态；宿主明确忙于切歌时，不让上一首的快照闪回。正常确认、错误或忙碌结束均恢复接受宿主状态。
- 顶部导航轮盘上移 15px，保留顶部间隙。
- 补上详情进入/退出、封面双向过渡、歌词内容入场、回到当前句按钮淡入。沿用现有速度、分类开关、减少动画设置；链路抽屉减少动画模式只淡变。快速重开时旧退场回调不能隐藏新页面。

## 验证

- Release win-x64 build：0 错误，172 条既有警告。
- 原生启动检查：修复版以 `--web-shell` 启动后进程持续运行；未自动触发歌曲播放。
- 独立发布：`dist/web-shell-playback-repair/`；原发布目录保留。入口：`tools/OpenPlaybackRepair.cmd`。
- `WebShellPlaybackRepairSmoke.cjs`：即时反馈、旧忙碌状态隔离、底栏横排、链路打开前后主区几何不变、全壳覆盖、768px 请求、1426/920/720 宽度歌词独立滚动、真实滚轮、文字选择、0 秒/后续句定位、动画重入与关闭模式，通过。
- `WebShellNowPlayingSmoke.cjs`：详情开关、取词、高亮、回当前句、换歌清词、Esc 回焦点，通过。
- `WebShellPlayerBarSmoke.cjs`、`WebShellBridgeSmoke.cjs`、`WebShellNavSmoke.cjs`：通过。
- `_wb_verify_publish.py dist/web-shell-playback-repair`：52/52 网页资源一致，VERIFY_OK。新 DLL 与本轮构建 MD5 均为 `21164b695608528b2d3c0d6df1dbbd72`。
- 自动化使用模拟宿主与测试封面验证网页交互；真实音乐的实际出声延时尚需用户试听确认。封面清晰度仍受歌曲内嵌原图分辨率限制。

## 项目简报滞后

`PROJECT-BRIEF.md` 中 .NET 9 已滞后，当前工程是 .NET 10；其“当前实跑目录为 web-shell-library-preview”也滞后，原最新产物为 web-shell-dsp-tags-complete。本轮另建修复版目录，便于独立回退。
