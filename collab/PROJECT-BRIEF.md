# PROJECT-BRIEF — CelesteMusicPlayer 给 Codex 的压缩包

> 维护方 WorkBuddy 与 Codex（本地）。当前架构核对日期为 2026-11-09。
> 有新会话时**只读这一个文件**，不要自己去 `ls -R` / 全仓 grep 探索项目结构 —— 下面都有了。
> 本文件可能滞后于代码。**有矛盾以代码为准，并且请你在结论里指出本文件哪句过时了**，我会更新它。

---

## 1. 这是什么

Windows 桌面 HiFi 音乐播放器。C# / .NET 10（`net10.0-windows10.0.19041.0`），日常验证发布为 `win-x64`。
正式界面由原生 Windows 窗口、WinForms 宿主和 WebView2 组成。窗口、托盘、任务栏和媒体控制由 C# 对接 Windows。网页处理布局和交互，播放、解码、曲库与 DSP 由宿主服务管理。

## 2. 搜索地盘（先读这一节）

**仓库里堆着大量垃圾和拷贝，全仓搜索会让你上下文爆炸并且结果全是噪音。**

必须排除：

| 排除什么 | 为什么 |
|---|---|
| `**/obj/**`、`**/bin/**` | 自动生成，里面有 9000+ 行的 `MainWindow.g.cs` / `TagPanelControl.g.cs` |
| 根目录下 `_` 开头的目录 | 历史备份：`_celeste_objbin_bak_*`、`_iobj`、`_wv2_cachebak`、`_binbak_*`、`_bin_verify` … 里面是整份拷贝的 obj |
| `outputs/*-cache-*` 目录 | 每次构建缓存下来的整份 obj/Release 树 |
| `outputs/*.log` | 构建日志。**单份约 58KB ≈ 1.5 万 token**，不要整份读 |
| `native/build/**` | C++ 内核的构建产物 |
| `dist/**`、`run-latest/**` | 发布产物 |

**推荐搜索范围**为 `CelesteMusicPlayer/` 的正式源码、`outputs/webview2-shell/`、相关的 `tools/` 子目录与任务文档。
拿不准一个路径是不是垃圾，就先问我（WorkBuddy），一次 `ls` 比一次全仓搜索便宜得多。

已知的大坑 heuristics：
- 搜一个符号同时出现在 `_bak/` 和正式源码里 → 只有正式源码那份是真的。
- 看到 `.g.cs` 后缀 → 一律跳过，那是 XAML 编译器生成的。

## 3. 文件地图（改代码前先看这里）

下列 C# 文件位于 `CelesteMusicPlayer/`。网页文件位于 `outputs/webview2-shell/`。

| 文件 | 职责 |
|---|---|
| `Program.cs`、`App.xaml.cs` | 程序启动、文件激活、单实例和窗口生命周期。`App.xaml.cs` 是沿用的 C# 文件名 |
| `NativeMainWebWindow.cs`、`NativeAppWindow.cs`、`WebShellWindow.cs` | 原生窗口与 WebView2 宿主、网页消息、封面资源和窗口控制 |
| `index.html`、`shell.js`、`now-playing.js` | 正式主界面、导航、曲库与播放详情页 |
| `MainWindow.xaml.cs`、`MainWindow.Playback*.cs`、`IWebPlayerServices.cs` | 主播放和曲库服务。网页经服务接口读写状态 |
| `AudioPlaybackEngine.cs`、`HiFiOutputBackend.cs`、`native/dsp-core/` | 播放引擎、输出设备、原生 DSP |
| `WebMiniPlayerWindow.cs`、`MiniPlayerBridge.cs`、`mini-player.html` | 迷你播放器，读取主播放会话 |
| `WebQuickPlayerWindow.cs`、`QuickPlaybackSession.cs`、`quick-player.html` | 零散播放器，拥有独立会话与临时队列 |
| `settings.js`、`WebSettingsService.cs`、`WebSettingsSchema.cs`、`AppSettingsStore.cs` | 网页设置、设置契约和持久化 |
| `tags.js`、`WebTagService.cs`、`TagEditorService.cs` | 网页标签编辑和文件元数据读写 |
| `dsp.js`、`dsp-eq.js`、`MainWindow.WebDsp.cs` | 网页 DSP 和 EQ 面板与原生处理链的接口 |
| `CoverSourceReader.cs`、`CoverCacheService.cs`、`CoverVariants.cs` | 提取封面和生成按物理像素选用的 240、480、1024 缩略档。大背景走专用路径 |

项目文件使用 `UseWinUI=false`。正式窗口布局从网页源码找，旧 XAML 布局供迁移参考。

## 4. 已知铁律（踩过的坑，不要再踩）

这些每一条都是真金白银换来的。违反的后果写在括号里。

**原生宿主与网页**

- 文件选择器和对话框以当前原生窗口为父窗口。WebView2 消息在宿主 UI 线程处理。
- 主题走网页语义颜色变量，图标复用 `index.html` 的 SVG 定义。标题栏与拖动区由 `platform-materials.css` 声明。

**P/Invoke**
- `SetWindowRgn` 在 **user32**，`CreateRoundRectRgn`/`DeleteObject` 才在 **gdi32**。挂错 DLL 不崩（被 Safe 吞），但功能整个失效 —— 这类错误在日志里看不到，得肉眼查。

**本项目特有**
- `AppSettingsStore.Clone()` 是**逐字段显式拷贝**，加新字段不同步 = "存得进文件但 Load() 恒为默认"。已中招五次。
- `CoverArtService` 改缓存档位必须 `CacheVersion` +1，否则旧档一直命中、新档永不生效。
- `StartupLog` 按 `CallerFilePath` 的文件名推断日志分类（含 Playback/Audio/HiFi → `logs/audio/`；含 MainWindow/Visualizer/Lyrics → `logs/app/`）。
  **查一个问题常常必须同时看两个日志文件。**
- 自持引擎的新 Window 必须在构造函数赋 `HiFiOutputBackend.UiDispatcherQueue = this.DispatcherQueue`（否则没声音）。
- 冒烟判据不能只看错误行数 — 被 `Safe()` 吞掉的真错抓不到，必须肉眼过一遍新增 ERR 行。
- 验证发布产物新鲜度**别信 exe**：exe 是跨构建字节不变的启动器外壳，真代码在 `CelesteMusicPlayer.dll` —— 对它做 md5 比对 + 搜方法名。
- publish 前若 obj/bin 搬不动（WinError 5 拒绝访问）→ 先 `dotnet build-server shutdown`。

## 5. 交付约定

- 一次任务一个 commit，**不 `git add -A`**，不 rebase / force-push，改动要能独立回退。
- 构建通过之后才提交。
- 用户**不懂编程**。结论要用大白话讲，给可验证的步骤，不要堆术语和营销腔。
- 用户重视"能回滚"：重大改动前先备份 / 重命名，不要直接删。
- 开工前用 `git status --short` 核对当前分支和已有改动，保留其他人的工作。
- 当前任务清单见 `outputs/待办清单-2026-11-02.md`。验收与本地发布以实际提交和运行目录为准。

## 6. 你在被问到时，默认的产出形态

| 被问什么 | 期望产出 |
|---|---|
| 某个 bug 为什么 | 根因假设 + 怎么验证每条假设的具体操作（一条一条能执行） |
| 某个功能怎么做 | 方案 + 影响面（要改哪些文件的哪些位置）+ 要不要回滚备份，必要时给两三个选项让**用户**拍板 |
| 一段代码怎么改 | 可直接替换的片段 + 落点（文件:行号），不要整文件重发 |
| review | 按"会崩 / 会静默失效 / 只是不好看"分级，别一股脑列 30 条风格建议 |

**不确定就说不确定，不要编。** 用户明确要求：判断要带依据。

---

## 7. 网页外壳（WebView2）— 2026-10-26 新增

2026-10-27 确认网页壳为正式主界面。正常启动、托盘恢复和文件激活使用原生窗口与网页。C# 中的 `MainWindow` 负责主播放、曲库和设置等后台服务。

当前交付目录为 `dist/bugfix-20261104-borderless/`。用户日常验证统一双击仓库根目录 `打开当前播放器.lnk`，直接启动 EXE，避免批处理黑窗；由 `tools/NewCurrentPlayerShortcut.ps1` 重建。`tools/OpenCeleste.cmd` 保留为工具兼容入口，指向同一目录。旧 dist 目录保留回退，不表示当前运行版本；`dist/web-ui-current/` 当前是普通目录，不是最新版链接。

**交付必须面向不懂代码的用户**：默认同步上述固定运行目录；结论明确“已同步/尚未同步”、是否需重启和回退位置。提交号只是源码回退点，不能当程序入口。若必须更换验证目录，同步更新根目录快捷方式、生成脚本、工具入口和 `先看这里-仓库与验证入口.md`，不要再让用户在多个产物目录找 EXE。

**★ 网页源码唯一真身：`outputs/webview2-shell/`。**
`dist/*/WebShell/` 是**构建产物**（`CelesteMusicPlayer.csproj` 的 Content 条目负责拷贝），
**在 dist 下改会被下次构建覆盖，等于白改。** 改完 src 之后要手动 `cp` 同步到正在实跑的那个 dist 目录
（当前是 `dist/bugfix-20261104-borderless/WebShell/`）才能立刻看到效果。

文件：`index.html`、`shell.js`、`shell.css`（主壳）、`navigation.css`、`library.css`、`collections.css`、
`songs.css`、`corner.css`、`transport*.css|js`、`albums.js`、`artists.js`、`browse.js`、`sort-menus.js`、`waveform.js`。

**已踩过的三个坑（改这块之前务必知道）：**

1. **半圆分类轮盘 `.corner-orbit` 的尺寸锁在三处**，改动必须三处一起等比缩放：
   - `index.html` 里 SVG `viewBox="0 0 600 290"`（弧半径 280）
   - `shell.js` 里**硬编码 600 / 290** 的百分比换算，环半径 150 / 238
   - `navigation.css` 里 `width` + `aspect-ratio:600/290`
   只改 CSS 宽度是不够的 —— 2026-10-26 就因为只改了 CSS、没改 `shell.js`，导致容器和按钮
   缩了而两圈之间的**径向间距没跟着收**，视觉上变成两条散开的环带。当前尺寸 **300×145**
   （`width:min(300px,42vw)`），对应外圈半径 ≈ 119px。`shell.js` 的图标环半径为 **150 / 238**，改尺寸时这三个文件必须一起算。

2. **`.primary` 带着一个全局 `margin-top:21px`**（`shell.css`，本来是给首页 hero 卡片用的）。
   直接放进工具条会把按钮顶出队列。songs 页曾用 `.songs-actions .primary{margin:0;...}` 修过一次，
   browse 页照抄时漏了 → 2026-10-26 又修了一次。新工具条一律给内部控件显式 `height` + 垂直居中。

3. **CSS 是层层覆盖的**，加载顺序：
   `shell → corner → songs → transport → transport-panels → library → collections → navigation`。
   **navigation.css 最后加载，同特异性下永远赢。** `corner.css` 里有套旧的左上角 250px 四分之一圆方案，
   被 navigation.css 的半圆方案盖住了 —— 别被 corner.css 里的旧数值误导。**当前尺寸 300×145。**

4. **★ 图形一律用 SVG，禁止用 Unicode 字符冒充图标。**
   `index.html` 的 `<svg class="icon-defs">` 里有 **35 个现成 `<symbol>`**
   （`i-home i-folder i-note i-star i-heart i-chevron i-close i-play …`），
   写法是 `document.createElementNS('http://www.w3.org/2000/svg','use')` + `href="#i-xxx"`。
   绝不要在 JS 里写 `node('button','♫')` 这类字符图标 —— 字符是文本不是图形：
   居中居的是文本框不是字形（所以会"偏上/偏下"）、渲染依赖系统字体（换机器就变样）、
   线宽不可控。2026-10-26 就因为 `folders.js` 用 `⌄`/`›` 当展开箭头，出现"箭头偏下"。
   详见 `outputs/避坑清单-网页图标必须用SVG-2026-10-26.md`（内含待清理清单）。
   要新图标就在 `icon-defs` 加一个 `<symbol>`，成本一行，别偷懒。

5. **轮盘图标必须落在两条轨道线的中点上。** `index.html` 画了三条背景弧线，半径
   **105（core）/ 195（inner）/ 280（edge）**；两圈图标的位置在 `shell.js` 的 `rings` 里，取值为
   **150** 和 **238** —— 正是各自区间的中点：`(105+195)/2`、`(195+280)/2`。
   改任何一个都必须同步改另一个，否则图标会偏离轨道中线，看起来就是"没居中"。

6. **`browse.js` 是全部七个分类面板的唯一实现**（文件夹/喜欢/评分/最近播放/播放最多/播放列表/播放队列），
   它们在 HTML 里共用同一个 `#browse-page`（`index.html:120`）。改一处七个全生效 —— 反过来说，
   只想改一个面板时要靠分页变量区分，不能直接改 DOM 结构。

7. **弹窗定位是 JS 说了算，CSS 里别插手。**
   `transport-panels.js` 的 `placePanel()` 负责把队列/加入播放列表面板放到触发按钮上方，
   且**以按钮的水平中心为轴左右对称**（用户明确要的行为；音频设置按钮太靠右做不到对称，
   被 clamp 到屏幕边缘是预期行为）。
   **两个**入口各自定位，改一个是不够的 —— 2026-10-26 就漏改了第二个：
   - `placePanel()`：队列 / 加入播放列表 / 音频设置 这几个 `<dialog>` 面板
   - `open()` 里 `next==='more'` 那段：**更多菜单** `#transport-more-menu`（独立的 `<div>`，不走 placePanel）

   两者现在是同一条规则：**以触发按钮的水平中心为轴对称**。
   更多菜单原本把宽度写死成 232px，已改成运行时测量，别再写回硬编码数字。
   但**盖不掉 CSS 的 `translate`** —— 所以在 CSS 里加 `translate` 只会让面板在被算好的位置上
   再偏移半个身位，直接错位。CSS 里只留一个 `right/bottom` 的无锚点兜底即可。
   （2026-10-26 在这个地方栽过一次，别重蹈。）

8. **已知的待办（2026-10-26 已交 Codex，见 `collab/TASK-2026-10-26-browse-playlist.md`）**：
   文件夹面板从盘符而非用户根目录起算；播放列表缺封面；播放列表浏览页/详情页缺原生按钮。

9. **参考的两个外部项目**（2026-10-26 调研结论，详见
   `outputs/WebView2迁移方案-标签页与DSP-2026-10-26.md`）：

   - **ECHO**：`C:\Users\MSI\Downloads\ABDM\ECHO-main`（Electron + TS，1800 文件）。
     有价值的是：`components/audio/EqCurveView.tsx`（667 行，**EQ 频响曲线用 SVG 画，不是 canvas**，
     这套思路可直接搬）、`pages/HomePage.tsx`（区块是 hero → pulse 听歌热力 → content-grid 货架）、
     以及 `styles/tokens.css`（**原生 CSS + 设计变量，没用任何 UI 框架**，和本项目做法一致，
     所以不要引入 React/Tailwind/组件库）。
     ⚠ **反面教材**：`pages/DspPage.tsx` 有 **6339 行** —— 参考它时照"长相"不照"体格"，
     照它的写法抄，等于把一只六千行的巨兽换个语言重抄一遍。
   - **kid3**：标签编辑页的参考来源。本项目现成的实现在
     `CelesteMusicPlayer/TagPanelControl.xaml`(1662 行) + `.xaml.cs`(6196 行)。
     **注意后端服务层已经有了**：`TagEditorService.cs`，迁网页不等于重写逻辑。
   - EQ 页面用户是满意的，**真正的基准是 `MainWindow.EqDsp.cs`（2291 行）那套**：
     对数频率轴 20–20000Hz、自适应 dB 档位（12/18/24/36，配 3/6/6/12 步长）、三段渐变曲线、
     可拖节点、悬停读数、实时频谱、撤销/A-B/自动增益/预设/APO 导入导出。
     ⚠ 早前说"基准是 `EqualizerWindow.xaml`（71 行）"是错的 —— 那个只是独立小窗口，别照它做。

10. **DSP 页的两个唯一真相源**（2026-10-26 定，别再靠截图猜）：
   - **导航分组**：`CelesteMusicPlayer/MainWindow.DspNav.cs:124-140`，**12 项 / 6 组**，顺序即代码顺序：
     `(无组)听音方案 → (无组)DSP 机架编排 → 输入[输入余量, 响度·ReplayGain] → 采样率[SRC/升频] →
     塑形[参数 EQ, 动态压缩器] → 声道[声道工具, 立体声场, 声道矩阵] → 空间[FIR/房间校正] →
     输出安全[输出安全]`。
     前两项 group 为空所以**不渲染组标题**；最后一组的组名与项名**同为"输出安全"**，原生就这样，不是 bug。
   - **配色**：`MainWindow.EqDsp.cs:594-601`，**浅色/深色两套分支都有**。
     `webview2-shell` 宿主当前是浅色（`:root{color-scheme:light}`、`--surface:#fff`、页面底 `#eae8e6`），
     所以网页走浅色分支。两套值都写在 `dsp.css` 的 `--eq-*` 变量里，
     以后要深色只需在 `.eq-curve-wrap` 上加 `data-dark="true"`，**JS 不用改**。
     浅色：曲线 c1 `#1d4ed8` / c2 `#0f766e` / c3 `#b45309`；网格黑 alpha .118（0dB 线 .29）；
     刻度 `rgba(30,30,30,.588)`；填充 `rgba(15,118,110,.157→.02)`；节点 `#2f6fa8`。
   - ⚠ **`_webcheck/dsp.png` 那批截图是 10-09/10-10 的，过期**（`EqDsp.cs` 10-17 才改过），
     拿它对版式会把已经改掉的东西又"修"回去。要外观一律读代码。

2026-11-04：主窗口改用 WebView2 原生 CSS 拖动区域和 Windows 尺寸边框。桌面“CelesteMusicPlayer 最新测试版”和根目录快捷方式指向 dist/bugfix-20261104-borderless。隔离桌面已验证原生标题栏双击、控件命中、四边缩放命中、最大化/还原的客户区对齐和标题栏绘制。系统实际拖到屏幕边缘的吸附提示需要在交互桌面确认。旧构建保留在各自的 dist 目录。

2026-11-04 后续：用户明确故障为贴边拖回后的不规则色带，并非吸附不触发。opaque-snap 关闭主窗 DWM 亚克力与 WebView 透明底色，materialActive=false；保留网页内部材质。前述原生透明材质恢复不再适用。真实桌面贴边回拖仍待用户验收。

2026-11-04 外观跟进：用户已确认不透明构建修好贴边拖回色带。新构建给普通主窗口加 14 DIP 圆角，以主题底色绘制缩放边框，保留不透明合成。主窗口亚克力与 Windows 毛玻璃列入 11-02 待办 B12。前版 dist/bugfix-20261104-opaque-snap 保留。
2026-11-04 无边框跟进：用户指出深灰色缩放框仍可见。主窗口现在让网页填满窗口并保留 14 DIP 圆角，边缘透明热区将缩放交给 Windows。前版 dist/bugfix-20261104-rounded-frame 保留。
