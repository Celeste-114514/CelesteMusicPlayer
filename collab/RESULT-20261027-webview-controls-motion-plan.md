# WebView2 控件统一与全交互动效：调查和实施方案

日期：2026-10-27。对应本轮要求：**先修复标签表格，再调查控件和动画；本轮不批量实施控件替换或动画。** 歌曲详情页属于下一阶段，本文给它预留转场接口。迷你播放器、零散播放窗口重做另立任务。

## 1. 本轮已经修复的标签表格

原问题是网页每次只向宿主取 200 首，滚动再取下一批。前端的全选、编号等依赖已经拿到的数组，因此未滚动到底时，它们只覆盖部分歌曲。**原后端已经先对完整工作集排序，再分页**，不能将根因误写成“后端只排序了 200 首”。

旧界面的依据是 [TagPanelControl.xaml.cs](C:/Users/MSI/source/repos/CelesteMusicPlayer/CelesteMusicPlayer/TagPanelControl.xaml.cs:336)：读取时先收集完整集合，完成后统一绑定；排序在完整 `_files` 上进行，DataGrid 只虚拟化画面。

本轮采用同样的分工：

- [MainWindow.WebTags.cs](C:/Users/MSI/source/repos/CelesteMusicPlayer/CelesteMusicPlayer/MainWindow.WebTags.cs:53) 增加完整工作集请求，旧分页调用仍兼容。
- [tags.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/tags.js:146) 一次取得全部行模型，校验数量和 ID 唯一性，准备完成才替换旧表格。失败或截断回复保留原表；旧搜索回复不能覆盖新搜索。
- 所有行的数据已经在内存里，滚动只切换屏幕附近的行元素，不再请求下一页。这与“滚动后才加载歌曲”不同；全选、排序、编号始终使用完整集合。按文件读取的扩展标签值仍可按需显示；这些字段参加排序时，宿主会读取整个工作集的排序键。
- 全量编号先计算完整次序，再以每批 500 首传送写入请求，避免超过宿主单次写入上限，也避免每批重新从 1 编号。
- 沿用已有工作集 20,000 文件上限，超过时明确提示，不静默截断。这个上限不是整个曲库的容量限制。

验证脚本：[WebShellTagsWorkspaceSmoke.cjs](C:/Users/MSI/source/repos/CelesteMusicPlayer/tools/WebShellTagsWorkspaceSmoke.cjs)。4031 首模拟数据验证：完整集合、有限可见行、滚动不再取列表、真实行高与虚拟行高一致、全选 4031、全局倒序、跨批编号 1–4031、截断回复保留旧集合、搜索竞态保护。模拟写入不会修改用户音频文件。构建和交付详见本轮验证记录。

## 2. 调查范围与证据的含义

调查唯一网页源码目录：[webview2-shell](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell)。覆盖当前 43 个 HTML/JS/CSS 文件（24 JS、18 CSS、1 HTML），另查宿主对话框、外观状态与旧标签表格；参考本机 BiliPai 的设计令牌、动画状态管理与音乐栏实现。

可复查的逐行候选清单：[AUDIT CSV](C:/Users/MSI/source/repos/CelesteMusicPlayer/collab/AUDIT-20261027-webview-controls-motion.csv)。生成工具：[AuditWebShellControlsMotion.py](C:/Users/MSI/source/repos/CelesteMusicPlayer/tools/AuditWebShellControlsMotion.py)。按当前源码扫描结果：

| 项目 | 静态匹配次数 | 如何理解 |
|---|---:|---|
| 事件绑定或事件属性赋值 | 325 | 包括宿主消息和生命周期监听；不是 325 个独立用户操作。动态工厂一次绑定会创建多个实例 |
| select 创建或 HTML 标记 | 27 | 位置数，不是运行时下拉框总数；设置和 DSP 的数据定义会扩展生成 |
| checkbox/radio、range、color | 8 / 12 / 3 | 包含控件类型和 CSS 选择器候选；需结合实际 DOM 分类 |
| 原生 confirm | 1 | 标签页切换工作目录时的丢弃确认 |
| dialog 创建或标记 | 2 | 不含 C# ContentDialog；不是所有浮层总数 |
| transition / animation / WAAPI 等 | 89 | 包含已有有效动效和被覆盖的规则 |
| 去掉 outline | 15 | 部分已有替代焦点提示，不能一律判作缺陷 |
| 直接颜色值 | 1096 | 包含令牌默认值、图表语义色和 SVG；不是 1096 个风格问题 |

这是一轮完整源码入口调查和针对标签修复的浏览器验证，**没有把所有服务器账号、所有设置组合、所有控件的真实点击都声称实测通过**。后续实施要按第 8 节逐项验收。

## 3. 风格问题与替换方式

以现有 Celeste 设计令牌为基础，保留 Windows / Apple 两套预设的区别。它们共用控件语义和状态，分别提供圆角、间距、阴影、字体和透明度参数；不要给每一套预设复制一份组件代码。

### 3.1 必须统一的控件

| 类别与当前落点 | 调查结论 | 替换方案及必保行为 |
|---|---|---|
| 全局来源、输出设备、睡眠等静态 select：[index.html](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html:110) | 外框有网页样式，但原生选项弹出层不完全由网页令牌控制；大小、箭头和选中反馈也分散 | 共用主题下拉组件，保留值/change 接口；锚点定位、屏幕边缘翻转、滚动上限、选中项滚入视野；设备不存在时的禁用提示必须保留 |
| 设置工厂：[settings.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/settings.js:272)，字体/背景/导航：[appearance.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/appearance.js:488)；网络：[sources.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/sources.js:44) | 动态创建，现有只增强静态排序 select 的机制覆盖不到；字体列表可能很长 | 工厂完成 options 后主动 mount/update/dispose；动态更新 disabled/options/value，字体支持键入定位或搜索；来源编辑期间不能因重新渲染丢失输入 |
| 标签排序/批量/编号及工具：[tags.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/tags.js:215)、[tags-tools.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/tags-tools.js:8)，评分：[browse.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/browse.js:57) | 同一页存在多种 select 外观；表格紧凑控件和一般表单密度不同 | 同一组件增加 compact 尺寸，不改变排序字段值、规则先后顺序和评分含义；长标签不撑破表格 |
| DSP 模块和 EQ：[dsp.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/dsp.js:247)、[dsp-eq.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/dsp-eq.js:54) | 共用音频业务但控件绘制方式不同，重绘时还会重建选项框 | 同一下拉工厂；参数刷新只更新值，不销毁正在操作的控件；滤波器、声道等技术值保持不变 |
| 复选框、开关：标签表头/行、列选择、标签工具、音频弹窗；设置已有 role=switch 按钮 | 部分只有 accent-color，部分已经自画开关；尺寸与焦点反馈不一致，标签全选还需部分选中状态 | checkbox 保留真实 input，用统一 CSS/SVG 绘制勾号，支持 indeterminate/disabled/focus；switch 保留现有语义并统一轨道/滑块状态。混合字段状态不能显示成普通未选中 |
| 滑杆：进度/音量、倍速、设置、外观、DSP/EQ、DSP 图形控制 | 有多个轨道/滑块实现；全部替换成 div 会丢键盘、触控和读屏 | 保留真实 range，统一 thumb/track/fill/focus；尺寸可按 transport/compact/form 配置。拖动值直接跟手，松手再做很轻的回弹；键盘步进和精确数值输入保留 |
| 数字/搜索/文本/密码/textarea：设置、网络、标签、EQ | 浏览器增减按钮、搜索清除按钮、占位色、错误边框、焦点圈各自设置 | 共用 input/number/search/textarea 样式；数字步进按需给网页按钮；Enter/Esc/失焦提交语义写清；不拦截输入法组合状态；密码显示切换只在页面需要时提供 |
| 颜色选择：settings.js:298、appearance.js:238/676 | type=color 的系统颜色窗口不跟随 Celeste；背景/遮罩还需要保持 alpha | 页面内色板+HEX/RGB/alpha 面板、预览/应用/取消，带最近色；现有原生取色可作为可选入口。透明度单独保存，不能用六位 HEX 覆盖后变成不透明 |
| 主题排序菜单：[sort-menus.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/sort-menus.js:4)，右键菜单：[track-menu.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/track-menu.js:10) | 已有网页风格和部分键盘行为，是可复用起点；当前关闭直接 remove/hidden，彼此的定位与生命周期独立 | 抽出共用 popup 管理，保留菜单与 listbox 的不同角色；补齐禁用项、typeahead、焦点恢复、退出动画和相互排他；不可为所有控件硬套 menu 语义 |
| 确认/选择对话框：[tags.js](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/tags.js:127)，宿主 [MainWindow.WebBrowseActions.cs](C:/Users/MSI/source/repos/CelesteMusicPlayer/CelesteMusicPlayer/MainWindow.WebBrowseActions.cs:155) | 标签丢弃确认用 confirm；“添加到播放列表”仍用 WinUI ContentDialog/ComboBox/TextBox，与网页的主题和节奏不统一 | 页面内共享 modal：标题、说明、主/次/危险按钮、忙碌/失败状态；只让最顶层响应 Esc；焦点圈定并还给触发者。现有文件/文件夹选择器仍走系统，网页只处理等待和取消反馈 |
| disclosure、进度条、滚动条、列宽拖柄、分隔线、tooltip | `<details>` 默认标记、原生 progress、浏览器 title 提示、表格拖柄没有统一完整状态；源码出现不等于全部已显示 | 同一 SVG 展开箭头；progress 统一轨道和颜色并标注确定/不确定；滚动条保留可抓取和系统可访问性；拖柄给 hover/focus/drag 状态。重要提示用可聚焦的自有 tooltip，普通 title 可保留作兜底 |

下拉组件最少具备：关联可读标签、aria-expanded/controls、listbox/option、正确 active/selected 状态；上下键、Home/End、键入定位、Enter、Esc、Tab、点击外部；不可选项跳过；展开与收起焦点恢复。模态中的下拉留在该模态所属层内，避免被 dialog 顶层遮住。不能用全页 MutationObserver 不断替换 DOM，也不能把不可见原生 select 继续留在读屏和 Tab 顺序里造成双控件。

### 3.2 颜色、图标与状态缺口

| 发现 | 证据与处理 |
|---|---|
| 写死粉色焦点/边框，换 Windows/Apple 强调色后仍露出原粉色 | [tags.css:39/90](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/tags.css:39)、[dsp.css:87](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/dsp.css:87)。改用 focus-ring/input-line 等语义令牌；hover、pressed、selected、focus 必须继续分开 |
| DSP 多层参考样式含直接浅色/白色 | [dsp-echo.css:143](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/dsp-echo.css:143) 的导航焦点背景和大量 dsp-reference.css 规则需按实际 computed style 合并。当前颜色命中清单不能直接批量替换；先标出最终生效规则，再清除重复覆盖 |
| 有去除 outline 的规则，但替代提示不一致 | tags、settings 搜索、歌曲来源控件、folders、DSP。已有 focus-within 或 focus-visible 背景可保留，再统一可见边框与对比度。禁用状态不要只靠降低文字到看不清 |
| 字符冒充图形仍有残留 | [albums.js:19](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/albums.js:19) 的音符占位、[shell.js:220](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/shell.js:220)、[transport.css:25](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/transport.css:25) 的无封面占位；评分星形、音频链按钮箭头也应检查。改 SVG symbol，维持线宽/光学居中。排序箭头已有后加载规则重绘、部分返回按钮被隐藏，应清理遗留而非再叠一层。DSP 文本“L → R”是信息文字，可保留 |
| 选中、悬停、失败、忙碌在各模块独立绘制 | 建立 default/hover/pressed/focus/selected/disabled/pending/error/success/mixed 状态表；选择保持到异步操作明确完成，失败保留输入并就地解释；toast 只作补充 |
| 标签列选择列表被左侧面板遮挡 | 现有 WebShellDspTagsSmoke 在点击“显示注释”时被 `.tags-panel` 拦截；在改动前 HEAD 的隔离副本也复现。属于已有浮层层级问题。列选择器要进入共用 popup 层并重新计算锚点，不能只提高子节点 z-index 而忽略祖先 stacking context |
| 科学图形的色彩不能按普通装饰色处理 | EQ 的三段曲线、频率坐标和浅/深色分支以现有 [MainWindow.EqDsp.cs](C:/Users/MSI/source/repos/CelesteMusicPlayer/CelesteMusicPlayer/MainWindow.EqDsp.cs:594) 为基准；电平告警、波形已播放段、声道区分保留语义。仅调整外围控件和缺失的明暗适配 |

新增控件令牌应引用现有 `--surface / --ink / --muted / --accent / --hover / --pressed / --input-bg / --input-line / --focus / --menu-bg / --shadow` 等实际字段，缺项再增加，避免重新建立一套平行颜色体系。尺寸分普通、紧凑、底栏三档，焦点样式不随密度被压掉。

## 4. 现有动画为什么不够统一

1. [navigation.css:23](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/navigation.css:23) 有 `.page{animation:none!important}`，而 [shell.js:90](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/shell.js:90) 导航直接切换 hidden。仅给 `.page` 再加 animation 不会解决问题，需要把页面显隐生命周期交给转场控制器。
2. 轮盘固定 420/320ms；音频弹窗 [transport-panels.js:33](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/transport-panels.js:33) 已有 BiliPai Continuity 与抽样弹簧，进场 420ms、退出 140ms、标签切换 180ms。它们没有统一读取外观速度和强度。
3. [theme.css:83](C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/theme.css:83) 定义 motion-speed/strength，实际只有 settings.css 两条过渡使用 speed；strength 没有动画消费方。现有 speed 是时长乘数，大值实际更慢，和“速度”一词相反。不能宣称目前滑杆已经控制全站动画。
4. 页面内容普遍 replaceChildren / hidden，关闭排序菜单直接 remove，右键菜单直接 hidden；缺少退出阶段、列表重排、错误回滚以及连续操作时的接续。
5. CSS 去动画规则与 JS 的 prefers-reduced-motion 检查不统一；CSS 变量被用户覆盖后也未必保持系统减少动画优先。未来统一监听系统变化，并主动结束已经运行的动画。

## 5. 从 BiliPai 参考什么

参考的是本机 Android/Kotlin Compose 源码，不假定它能在 WebView2 直接运行。BiliPai 根 LICENSE 为 GPL-3.0；本轮只研究交互组织和参数，不复制其组件源码或素材。后续网页仍用项目现有原生 JS/CSS/SVG 与 Web Animations，不引入 React/Tailwind 或 Android 依赖。

| BiliPai 的实际依据 | 本项目采用的设计思路 |
|---|---|
| [AppMotionEasing.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/design-tokens/src/main/java/com/android/purebilibili/core/ui/motion/AppMotionEasing.kt)：Continuity (.20,.90,.22,1)、进入 (.22,1,.36,1)、退出 (.32,0,.67,0)、可逆 morph (.42,0,.58,1) | 集中命名曲线；进入减速、退出提速；可逆几何变化用连续对称进度 |
| [AppMotionTokens.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/design-system/src/main/java/com/android/purebilibili/core/ui/motion/AppMotionTokens.kt)：标准 180/200ms、强调 240/300ms；interactiveSnap 阻尼 .78/刚度 420、selection .82/500、press 1/1000 | 按操作语义挑节奏，不给所有控件套同一个弹簧；参数是参考起点，需要在桌面上调试，不能把刚度直接当毫秒 |
| [DampedDragAnimation.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/design-system/src/main/java/com/android/purebilibili/core/ui/animation/DampedDragAnimation.kt)、[LiquidIndicatorMotion.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/design-system/src/main/java/com/android/purebilibili/core/ui/animation/LiquidIndicatorMotion.kt)：共享拖动状态、互斥任务、速度跟踪、有限形变 | 标签指示条和按压反馈连续跟手；快速换目标时保留当前位置/速度；桌面鼠标、触控和键盘分别处理，不照搬手机侧滑导航 |
| [AppNavigationMotionSpec.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/app/src/main/java/com/android/purebilibili/navigation/AppNavigationMotionSpec.kt)、[CommonListMotionSpec.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/app/src/main/java/com/android/purebilibili/feature/list/CommonListMotionSpec.kt) | 导航、快速返回、共享封面、列表采用不同规格；PC 使用更短位移，避免整页长距离飞入 |
| [AudioNowPlayingBarPresenceMotionPolicy.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/app/src/main/java/com/android/purebilibili/feature/audio/screen/AudioNowPlayingBarPresenceMotionPolicy.kt)：进入 300、退出 220、减少动画 160ms；几何先展开，再跟上透明度 | 底栏与歌曲详情页共享封面/标题/背景的连续形变；收起反向衔接，内容在壳体稳定后显现。具体桌面位移不照搬其 28dp |
| [ThemeColorTransition.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/app/src/main/java/com/android/purebilibili/core/theme/ThemeColorTransition.kt)：240ms 同一时钟，连续换色从当前值重定向 | 主题切换一次计算起止语义色，全页面共用时钟，禁止每个节点启动独立循环；取消预览和撤销也走一致过渡 |
| [ReduceMotion.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/brand-motion/src/main/java/com/android/purebilibili/core/ui/motion/ReduceMotion.kt)、[MotionTierPolicy.kt](C:/Users/MSI/Downloads/ABDM/BiliPai-main/design-system/src/main/java/com/android/purebilibili/core/ui/adaptive/MotionTierPolicy.kt) | 系统偏好实时生效，离开页面释放监听；减少动画保持清楚的状态反馈，停用位移/弹跳/景深 |

## 6. 全交互覆盖表

“所有交互都有流畅动画”按**每种状态变化都有合适反馈**执行。文字输入、滑杆采样、音频命令本身立即响应；动画不能让输入、播放或保存额外等待。

下表时间是 Celeste 的建议默认值，不是 BiliPai 原值承诺。所有入口共用速度/幅度/减少动画开关。无限循环只允许当前可见、确有需要的指示器。

| 区域 / 实施文件 | 必须覆盖的交互与状态 | 动画建议 |
|---|---|---|
| 主壳、半圆导航、面包屑、来源筛选：shell.js、nav-layout.js、navigation.css | 打开/收起轮盘、导航/返回、选中项、搜索清除、来源切换、空页/错误/重试 | 页面淡入并位移 6–12px，180–240ms；返回反向；轮盘轻弹性 280–360ms；新导航打断旧导航，最终只留目标页。每页保留自己的滚动位置 |
| 首页：home.js、home.css | 当前播放卡、最近专辑、热力图、统计范围、榜单切换、加载/空状态/更新 | 区块淡入、滑动指示条、数值短过渡；只给用户切换或实际变化做动画，周期刷新不反复入场 |
| 歌曲：shell.js、songs.css | 排序/方向、过滤、分页、播放行、收藏/评分、批量选择、上下文菜单 | 可见行轻量 FLIP 或淡变，120–200ms；勾选/喜欢微反馈；列表更新不重置焦点；不对数千行逐个启动动画 |
| 专辑/艺术家/专辑艺人：albums.js、artists.js、library.css、collections.css | 卡片悬停/按下、详情进入/返回、页签、快速播放、排序/显示更多 | 封面共享边界或局部缩放+淡变，220–300ms；卡片悬停 1–3px；已加载图片不重复闪现 |
| 文件夹与七类浏览页：folders.js、browse.js、folders.css、navigation.css | 树展开/收起、键盘选择、上下级、播放列表新建/导入/导出/封面、喜欢/评分/最近/最多/队列操作 | 树子区域短展开 160–220ms；详情与列表变化不改变根目录语义；增删有位置反馈；失败恢复输入。复用 browse 的七类页面都要验 |
| 排序/右键/评分/多选栏：sort-menus.js、track-menu.js | 鼠标和键盘打开、移动选项、选中、子组、关闭、点击外部、Esc/Tab、菜单异步能力加载 | 菜单 140–200ms 进入、100–140ms 退出；选中底色与指示点 100–160ms；先固定锚点再动画，避免横向漂移；异步返回不得复活已关菜单 |
| 底栏：shell.js、player-layout.js、transport.css | 播放/暂停、前后曲、封面/文案、喜欢、顺序、音量、进度、等待/失败 | SVG 图标或图层切换 100–160ms；封面与文案同批交叉淡变 180–240ms；拖进度直接跟手，悬浮读数轻入场，不追赶弹簧 |
| 波形：waveform.js | 加载、缩放/窗口变化、拖动、播放推进、换曲 | 缓存后平稳替换；播放光标按时间连续推进；重采样不重置播放状态；页面隐藏停止无用绘制 |
| 音频设置/队列/更多/播放列表选择：transport-panels.js | 开关、切页、设备刷新/失效、模式、倍速、A-B、睡眠、保存配置、队列操作 | 复用当前已有弹簧但纳入全局；面板进入 220–300ms、退出 120–180ms；页签共享指示条；忙碌反馈和操作结果区平滑展开，失败不关闭面板 |
| 音频链路抽屉：audio-chain.js/css | 开关、节点展开、状态/事件、复制、跳转音频设置 | 抽屉 220–280ms；节点折叠 160ms；电平按音频采样平滑显示，不能周期性让整条链入场 |
| DSP 导航/听音方案/机架：dsp.js、dsp.css、dsp-echo.css、dsp-reference.css | 12 项/6 组导航、启停、参数、方案保存/套用/删除、节点排序、旁路、失败回滚 | 共享导航指示条；模块内容 160–220ms；排序可见节点 FLIP；状态回滚 100–160ms；实际音频参数即时发送 |
| EQ/耳机/其它图形：dsp-eq.js、dsp-headphones.js、dsp-visuals.js | 点/频段拖动、选中、精确数值、简洁/专业、A-B/撤销、导入/导出、自动增益、图表悬停 | 节点拖动直接跟手、指示环 100ms；预设/撤销曲线短插值 120–180ms；数值与曲线来自同一状态；实时频谱保持已有数据节奏；不改变频率对数轴或音质 |
| 标签与全部工具：tags.js、tags-tools.js、tags.css | 完整集合加载、行选择/Shift/Ctrl、编辑/混合值、列显隐/宽度、排序规则、工作目录、编号/批量/重命名预览、歌词/封面/在线匹配、保存/取消/逐文件失败 | 就绪替换 120–180ms；选中/脏标记 80–120ms；面板切页 160ms；进度与结果展开；编辑单元格、IME、光标不做位移动画；虚拟行滚动不逐行入场 |
| 设置：settings.js/css | 类目/搜索、开关、数值/选择、只读/不可用、路径/账号、系统/存储/关于、重置/保存/错误 | 导航和选中指示条 160–220ms；依赖项显隐 160ms；pending 保留值；失败就地回滚。系统设置、文件夹、外部链接只给网页边界反馈 |
| 外观：appearance.js、theme.js、background.js | 两套系统预设/明暗/颜色、字体、导航和底栏构成、背景四类含视频、图片选择取消、保存/取消、撤销/重做、拖动合并历史 | 语义色同一时钟 200–240ms；背景交叉淡变 200–300ms，解码成功后切换；几何调整手指直跟；一次拖动仍只算一步。历史移动由宿主成功结果决定，动画不得增造历史记录 |
| 网络来源：sources.js | 添加/编辑/授权/测试、浏览上下级、播放/加入曲库、启停、同步/取消、进度/失败/完成、缓存清理 | 表单依赖区展开、目录替换、每个任务状态独立反馈；取消迅速显示，旧索引保留；轮询只更新变化部分，不整页销毁正在编辑的表单 |
| 跨页公共状态 | toast、loading、empty/error、tooltip、modal、焦点、禁用、窗口缩放/隐藏、系统减少动画 | toast 140–180ms，错误说明稳定可读；loading 有延迟显示防短请求闪烁；隐藏页面结束离场与定时器，恢复时只刷新一次状态 |
| 下一轮歌曲详情页 | 底栏封面进入、展开/收起、歌词滚动与当前行、简介/技术参数/队列、点击外部和返回 | 共用封面/边界/圆角/背景进度 260–360ms，反向可中断；滚动歌词与用户手动浏览分开，用户浏览期间暂停自动追随；页面数据结构明确后再实现 |

操作的展开、成功、失败、取消、重复触发都属于覆盖范围；只写“打开时 animate”不能算完成。窗口标题栏拖动、系统文件选择器、系统授权浏览器和 OS 窗口动画由宿主/系统负责，不给它们叠网页假窗口动画。

## 7. 共用实现与可自定义设置

### 7.1 小模块实现

建议新增 `motion.js / motion.css`、`ui-controls.js / ui-controls.css`、`popup-manager.js`，均放唯一网页源码目录；由 index.html 按依赖顺序加载。迁移后收拢重复选择器，确认最终 CSS 顺序，尤其当前 settings.css、dsp-reference.css 也在后面加载。

- **MotionConfig**：统一计算有效模式、时间倍率、位移倍率、曲线和弹簧。CSS 使用语义令牌，WAAPI 读取同一份有效配置；禁止各模块再写散落的固定时长。
- **MotionController**：每个目标元素持有任务与 generation；新操作接续当前画面，取消/reverse/retarget 旧任务。关闭完成才 hidden/remove；取消和异常也必须 cleanup。只允许当前 generation 更新 hidden/inert/焦点，防止旧动画结束把刚打开的层关掉。
- **PopupManager**：层级、锚点、边缘约束、外部点击、Esc、焦点恢复共享。位置外壳和动画内层分开，防止 transform 相互覆盖。底栏面板仍由 placePanel() 以按钮中心定位，不加导致二次偏移的 CSS translate。
- **ControlFactory**：原有值、输入事件、宿主 RPC 不改语义；提供 mount/update/dispose 与 compact 选项。组件有稳定身份，刷新数据优先 patch，编辑期间不 replaceChildren 整个表单。
- **布局变化**：小规模可见项使用测量前后位置的 FLIP；大集合保持虚拟化，不建立上千个 Animation 对象。共享封面可采用单个覆盖图层，真实内容可访问性仍在页面中；View Transitions 只在检测可用后作为增强，必须有普通 WAAPI 兜底。
- **帧循环**：普通动画交给合成器；需要物理跟随时才 rAF，达到阈值或超时停止。drag 直接跟随输入，释放时的弹簧用位移和速度初始条件计算。限制 blur 大面积重绘，will-change 只在动画期间使用。

### 7.2 建议给用户的选项

| 设置 | 默认与范围建议 | 说明 |
|---|---|---|
| 动画模式 | 跟随系统 / 完整 / 轻量 / 关闭 | 默认跟随系统；系统减少动画优先关闭位移、弹跳、景深，保留短淡变或即时状态切换 |
| 动画速度 | 1.0×，0.6–1.6× | 用 `时长 = 基准 / 速度`，越大越快；不允许 0，关闭另有开关。迁移旧 motion-speed 时按旧“时长倍率”语义转换，避免静默颠倒偏好 |
| 位移/回弹幅度 | 1.0×，0–1.25× | 0 只去几何运动，不取消选中颜色/焦点；不影响弹窗实际锚点和点击范围 |
| 回弹风格 | 克制 / 柔和 / 灵动 | 普通用户先用三档；高级项可开放阻尼约 .7–1.0、刚度约 260–1000 的受限范围，禁止不稳定无限振荡 |
| 分类开关 | 导航、弹窗、列表、控件、主题/背景、封面转场 | 可单独减少某一类，不拆成几十个难理解的参数 |
| 预览与恢复 | 演示按钮、恢复默认、保存/取消、撤销/重做 | 演示覆盖按钮、下拉、列表和页面；改变设置立即预览，仍遵守现有真实覆盖撤销和拖动合并规则 |

Windows 预设可选更克制的 180–240ms 与较高阻尼；Apple 预设可选柔和的 220–300ms 和连续共享边界。外观预设保存风格参数，**系统/个人减少动画偏好单独保存且优先**，加载预设不能替用户重新开启已禁用的动画。

宿主落点为 [WebAppearance.cs](C:/Users/MSI/source/repos/CelesteMusicPlayer/CelesteMusicPlayer/WebAppearance.cs:564) 的 State/Normalize/Clone、预设与持久化，以及 [MainWindow.WebAppearance.cs](C:/Users/MSI/source/repos/CelesteMusicPlayer/CelesteMusicPlayer/MainWindow.WebAppearance.cs:108) 的 schema/快照/patch 白名单。新增字段必须同步显式 Clone，并接入现有预览事务；已有只调整 CSS token 的滑杆不能继续假装全站生效。

减少动画或窗口隐藏时，控制器取消进行中的装饰动画并立即落在正确目标状态；不暂停音频，也不留下透明但挡点击的层。不要用“时长设成 0 再除以它”的方案。

## 8. 建议实施顺序与验收

| 阶段 | 修改范围 | 可交付验收 |
|---|---|---|
| 本轮完成 | 完整标签工作集、虚拟显示、跨批编号；调查文档和 CSV | 4031 首覆盖测试、Release 构建、自包含交付及哈希核验；无真实音频文件被冒烟脚本修改 |
| 第一批 | 控件令牌、通用下拉/checkbox/range/表单、统一 modal/popup；清理真实生效的颜色与图标问题 | 当前默认、Windows、Apple，各浅/深色截图；所有动态选项更新正常；纯键盘可完成操作；禁用/混合/错误有明确状态 |
| 第二批 | 动画控制器、配置和演示；导航/轮盘/底栏弹窗/菜单/设置切页 | 速度和强度同时影响 CSS/JS；开关 20 次、打开未完成就关闭/反开、快速跨页不闪白、不串页、不遗留遮罩；焦点可恢复 |
| 第三批 | 覆盖第 6 节其余列表、DSP、标签工具、网络与异步状态 | 一项项登记入口、正常/错误/取消/重复触发；大列表 DOM 有界；参数拖动不被动画拖慢；加载失败仍能重试 |
| 第四批 | 歌曲详情页及底栏共享封面、歌词联动 | 页面信息和交互确定后实施；展开/收起可接续；换曲过程中仍可返回；手动滚动歌词不会被强行拉走 |

性能验收在本机实际 WebView2 上记录：1440 和窄窗、100%/150%/200% DPI，4031 首标签和大列表、视频背景与 DSP 同时运行、窗口最小化/恢复。目标 60Hz 下大部分动画帧不超过约 16.7ms，若 GPU/机器负荷达不到，按测量降低模糊/阴影和运动量；这里是验收目标，不是本轮已测得的性能结论。

自动化重点只覆盖会造成实质回归的行为：控件键盘/焦点、值提交、动画打断和最终状态、异步旧回复、减少动画实时切换、监听释放、滚动/编辑状态、完整集合编号。视觉对版用固定截图和短录屏，不写仅检查 CSS 字符串的伪验证。

每批独立提交，发布前备份当前交付；新旧控件迁移保留可回退入口。不能靠一个全站 transition:all 或长时间 stagger 宣称“全交互动画完成”。

## 9. 当前项目简报需要更正的事实

[PROJECT-BRIEF.md](C:/Users/MSI/source/repos/CelesteMusicPlayer/collab/PROJECT-BRIEF.md) 的 .NET 9 已过时，当前项目实际为 net10.0；当前交付目录是 dist/web-shell-dsp-tags-complete；CSS 并非最终停在 navigation.css，index.html 后面还有 DSP/标签/设置样式。轮盘几何目前也有 nav-layout.js 统一重画。调查和后续实现以当前代码为准，不把旧说明当待恢复目标。
